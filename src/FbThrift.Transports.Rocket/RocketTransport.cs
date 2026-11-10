// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Rocket;

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Threading.Channels;
using System.Threading.Tasks;
using FBThrift;
using FbThrift.Core;

/// <summary>
/// Rocket transport using RSocket framing over TCP.
/// Uses System.Threading.Channels for request/response multiplexing with backpressure.
/// </summary>
public sealed class RocketTransport : IAsyncDisposable
{
    private readonly Socket _socket;
    private readonly Pipe _sendPipe;
    private readonly Task _receiveTask;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<PendingRequest> _pendingChannel;
    private int _nextStreamId = 1;

    // For stream/sink/bidi streaming, track stream state
    private readonly Dictionary<int, StreamState> _streamStates = new();

    /// <summary>
    /// Represents a pending request with its response channel.
    /// </summary>
    private sealed class PendingRequest
    {
        public required int StreamId { get; init; }
        public required TaskCompletionSource<RocketFrame> Tcs { get; init; }
        public required CancellationTokenSource CancellationTokenSource { get; init; }
    }

    public RocketTransport(Socket socket)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _sendPipe = new Pipe();
        
        // Bounded channel with backpressure
        _pendingChannel = Channel.CreateBounded<PendingRequest>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = true
        });
        
        _receiveTask = ReceiveLoopAsync(_cts.Token);
    }

    public async ValueTask<RocketFrame> SendRequestAsync(RocketFrame request, CancellationToken cancellationToken = default)
    {
        int streamId;
        
        if (request.Header.StreamId == 0)
        {
            streamId = GetNextStreamId();
            request = new RocketFrame(
                new RocketFrameHeader(request.Header.Length, request.Header.Type, request.Header.Flags, streamId),
                request.Metadata,
                request.Data);
        }
        else
        {
            streamId = request.Header.StreamId;
        }

        var tcs = new TaskCompletionSource<RocketFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        
        // Linked cancellation
        var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);

        var pending = new PendingRequest
        {
            StreamId = streamId,
            Tcs = tcs,
            CancellationTokenSource = requestCts
        };

        // Write to channel (applies backpressure if channel is full)
        await _pendingChannel.Writer.WriteAsync(pending, cancellationToken).ConfigureAwait(false);

        try
        {
            await WriteFrameAsync(request, cancellationToken).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(requestCts.Token).ConfigureAwait(false);
        }
        finally
        {
            requestCts.Dispose();
        }
    }

    private int GetNextStreamId()
    {
        int id;
        do
        {
            id = Interlocked.Increment(ref _nextStreamId) * 2; // Client uses odd stream IDs
        } while (id == 0);
        return id;
    }

    private async ValueTask WriteFrameAsync(RocketFrame frame, CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>();
        frame.Write(buffer);
        var data = buffer.WrittenMemory;

        await _socket.SendAsync(data, SocketFlags.None, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[65536];
        var bufferOffset = 0;

        try
        {
            // Process pending requests from channel
            await foreach (var pending in _pendingChannel.Reader.ReadAllAsync(cancellationToken))
            {
                var frame = await ReadFrameWithStreamIdAsync(pending.StreamId, pending.CancellationTokenSource.Token).ConfigureAwait(false);
                
                pending.CancellationTokenSource.Dispose();
                pending.Tcs.TrySetResult(frame);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation
        }
        catch (Exception ex)
        {
            // Complete all pending requests with error
            while (_pendingChannel.Reader.TryRead(out var pending))
            {
                pending.CancellationTokenSource.Dispose();
                pending.Tcs.TrySetException(new IOException("Connection lost", ex));
            }
        }
        finally
        {
            _pendingChannel.Writer.Complete();
        }
    }

    private async ValueTask<RocketFrame> ReadFrameWithStreamIdAsync(int targetStreamId, CancellationToken cancellationToken)
    {
        var buffer = new byte[65536];
        var bufferOffset = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            int bytesRead = await _socket.ReceiveAsync(
                new ArraySegment<byte>(buffer, bufferOffset, buffer.Length - bufferOffset),
                SocketFlags.None,
                cancellationToken).ConfigureAwait(false);

            if (bytesRead == 0)
                throw new FbThriftException("Connection closed before response received.");

            bufferOffset += bytesRead;
            
            while (bufferOffset >= RocketFrameHeader.HeaderSize)
            {
                var header = RocketFrameHeader.Read(new ReadOnlySpan<byte>(buffer, 0, RocketFrameHeader.HeaderSize));
                int totalFrameSize = RocketFrameHeader.HeaderSize + header.Length;

                if (bufferOffset < totalFrameSize)
                    break; // Need more data

                // Extract metadata and data
                var metadataLength = (header.Flags & RocketFrameFlags.Metadata) != 0 ? 
                    BinaryPrimitives.ReadInt32BigEndian(new ReadOnlySpan<byte>(buffer, RocketFrameHeader.HeaderSize, 4)) : 0;
                
                var metadataStart = RocketFrameHeader.HeaderSize;
                var dataStart = metadataStart + metadataLength;
                var dataLength = header.Length - metadataLength;

                var metadata = metadataLength > 0 ? 
                    new ReadOnlyMemory<byte>(buffer, metadataStart, metadataLength) : 
                    ReadOnlyMemory<byte>.Empty;
                
                var data = dataLength > 0 ? 
                    new ReadOnlyMemory<byte>(buffer, dataStart, dataLength) : 
                    ReadOnlyMemory<byte>.Empty;

                var frame = new RocketFrame(
                    new RocketFrameHeader(header.Length, header.Type, header.Flags, header.StreamId),
                    metadata,
                    data);

                // Shift remaining data
                int remaining = bufferOffset - totalFrameSize;
                if (remaining > 0)
                {
                    Array.Copy(buffer, totalFrameSize, buffer, 0, remaining);
                }
                bufferOffset = remaining;

                if (frame.Header.StreamId == targetStreamId)
                {
                    return frame;
                }
                
                // Handle other frames (stream responses, etc.)
                HandleOtherFrame(frame);
            }
        }
    }

    private void HandleOtherFrame(RocketFrame frame)
    {
        switch (frame.Header.Type)
        {
            case RocketFrameType.Payload:
            case RocketFrameType.RequestStream:
            case RocketFrameType.RequestChannel:
            case RocketFrameType.RequestBidi:
                if (_streamStates.TryGetValue(frame.Header.StreamId, out var streamState))
                {
                    streamState.HandleResponse(frame);
                }
                break;

            case RocketFrameType.Cancel:
                HandleCancel(frame);
                break;

            case RocketFrameType.Error:
                HandleError(frame);
                break;

            case RocketFrameType.Setup:
            case RocketFrameType.KeepAlive:
            case RocketFrameType.Lease:
            case RocketFrameType.MetadataPush:
            case RocketFrameType.Resume:
            case RocketFrameType.ResumeOk:
                // Connection-level frames - handle as needed
                break;
        }
    }

    private void HandleError(RocketFrame frame)
    {
        if (_streamStates.TryGetValue(frame.Header.StreamId, out var streamState))
        {
            streamState.HandleError(frame);
        }
        // For request-response, the pending request will be handled by ReadFrameWithStreamIdAsync
        // since it matches the streamId
    }

    private void HandleCancel(RocketFrame frame)
    {
        if (_streamStates.TryGetValue(frame.Header.StreamId, out var streamState))
        {
            streamState.HandleCancel();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            await _receiveTask.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Ignore disposal exceptions
        }

        _socket?.Dispose();
        _sendPipe.Reader.Complete();
        _sendPipe.Writer.Complete();
    }

    private static RocketErrorCode ParseErrorCode(ReadOnlySpan<byte> data)
    {
        // ERROR frame data format: error code (4 bytes) + message
        if (data.Length >= 4)
        {
            return (RocketErrorCode)BinaryPrimitives.ReadUInt32BigEndian(data);
        }
        return RocketErrorCode.Reserved;
    }

    private sealed class StreamState
    {
        public int StreamId { get; }
        public RpcKind Kind { get; }
        public bool FirstResponseReceived { get; set; }
        public Action<RocketFrame>? OnResponse { get; set; }
        public Action<Exception>? OnError { get; set; }
        public Action? OnComplete { get; set; }

        public StreamState(int streamId, RpcKind kind)
        {
            StreamId = streamId;
            Kind = kind;
        }

        public void HandleResponse(RocketFrame frame)
        {
            if (!FirstResponseReceived)
            {
                FirstResponseReceived = true;
                
                // Verify checksum on first response if present
                if (frame.Metadata.Length > 0)
                {
                    var responseMeta = ThriftSerializer.DecodeCompact<ResponseRpcMetadata>(frame.Metadata.ToArray());
                    if (responseMeta.ChecksumAlgorithm.HasValue && responseMeta.Checksum != null)
                    {
                        if (!ChecksumUtil.VerifyChecksum(responseMeta.ChecksumAlgorithm.Value, frame.Data.Span, responseMeta.Checksum))
                        {
                            // Checksum mismatch on first response - cancel stream
                            OnError?.Invoke(new TApplicationException(TApplicationExceptionType.ChecksumMismatch, "Checksum mismatch"));
                            return;
                        }
                    }

                    // Check for application exception in first response
                    if (responseMeta.PayloadMetadata?.ExceptionMetadata != null)
                    {
                        var exc = responseMeta.PayloadMetadata.ExceptionMetadata;
                        OnError?.Invoke(new ThriftApplicationException((int)exc.ExceptionType, exc.What ?? "Unknown error"));
                        return;
                    }
                }
            }
            else
            {
                // Subsequent stream chunks - verify checksum if present
                if (frame.Metadata.Length > 0)
                {
                    var chunkMeta = ThriftSerializer.DecodeCompact<StreamPayloadMetadata>(frame.Metadata.ToArray());
                    if (chunkMeta.ChecksumAlgorithm.HasValue && chunkMeta.Checksum != null)
                    {
                        if (!ChecksumUtil.VerifyChecksum(chunkMeta.ChecksumAlgorithm.Value, frame.Data.Span, chunkMeta.Checksum))
                        {
                            OnError?.Invoke(new TApplicationException(TApplicationExceptionType.ChecksumMismatch, "Checksum mismatch"));
                            return;
                        }
                    }
                }
            }

            OnResponse?.Invoke(frame);
        }

        public void HandleError(RocketFrame frame)
        {
            var errorCode = ParseErrorCode(frame.Data.Span);
            
            // Per fbthrift compatibility spec:
            // For stream/sink/bidi, ERROR with CANCELED carries no data
            // For sink, client sends CANCEL then ERROR with CANCELED
            // For bidi, client sends CANCEL then sink's ERROR
            
            var errorData = System.Text.Encoding.UTF8.GetString(frame.Data.Span);
            
            if (errorCode == RocketErrorCode.Canceled)
            {
                OnError?.Invoke(new TApplicationException(TApplicationExceptionType.Canceled, errorData));
            }
            else if (errorCode == RocketErrorCode.ApplicationError)
            {
                // Application error with data
                OnError?.Invoke(new ThriftApplicationException(0, errorData));
            }
            else
            {
                OnError?.Invoke(new TApplicationException(TApplicationExceptionType.Unknown, $"Unexpected error frame type: {errorCode}"));
            }
            
            OnComplete?.Invoke();
        }

        public void HandleCancel()
        {
            OnError?.Invoke(new TApplicationException(TApplicationExceptionType.Canceled, "Stream canceled by peer"));
            OnComplete?.Invoke();
        }

        private static RocketErrorCode ParseErrorCode(ReadOnlySpan<byte> data)
        {
            if (data.Length >= 4)
            {
                return (RocketErrorCode)BinaryPrimitives.ReadUInt32BigEndian(data);
            }
            return RocketErrorCode.Reserved;
        }
    }
}
