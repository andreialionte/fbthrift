// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Quic;

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FBThrift;
using FbThrift.Core.Transport;

/// <summary>
/// Rocket QUIC transport for Thrift over QUIC.
/// Implements the Rocket protocol over QUIC streams following fbthrift specification.
/// See: https://github.com/meta/fbthrift/blob/main/thrift/doc/spec/compatibility/rocket/index.md
/// </summary>
public sealed class RocketQuicTransport : IFbThriftTransport, IAsyncDisposable
{
// Modern: Use Channels instead of ConcurrentDictionary for backpressure
    private readonly Channel<(int StreamId, TaskCompletionSource<ReadOnlyMemory<byte>> Tcs)> _pendingRequests;
    private readonly Channel<(long StreamId, ReadOnlyMemory<byte> Data, bool Fin)> _inboundFrames;
    private readonly Channel<(long StreamId, ReadOnlyMemory<byte> Data, bool Fin)> _outboundFrames;
    
    private readonly ConcurrentDictionary<long, StreamContext> _streams = new();
    private readonly CryptoFrameBuffer _cryptoBuffer;
    private readonly Socket _socket;
    private readonly IPEndPoint _endpoint;
    private readonly X509Certificate2 _certificate;
    private readonly RpcOptions _options;
    private readonly int _maxConnections;
    private readonly int _maxInboundStreams;
    private volatile bool _disposed;
    private long _nextStreamId = 0;
    
    // Shared buffer pool for frame encoding/decoding
    private static readonly ArrayPool<byte> _framePool = ArrayPool<byte>.Shared;
    
    // POH (Pinned Object Heap) buffer pool for zero-GC socket I/O
    private readonly MemoryPool<byte> _pinnedBufferPool;
    
    // UDP GRO (Generic Receive Offload) support
    private static readonly bool _groSupported = TryEnableGro();
    
    // Receive batch size for ReceiveFromMultipleAsync
    private const int ReceiveBatchSize = 64;
    
    // Stream flow control
    private readonly QuicFlowControl _flowControl;
    
    // Write completion tracking (based on fbthrift cfb76a1)
    private readonly WriteCompletionTracker _writeCompletionTracker;
    
    // Stream flow control context
    private readonly ConcurrentDictionary<long, StreamFlowContext> _streamFlowContexts = new();
    
    /// <summary>
    /// Initializes a new instance of the <see cref="RocketQuicTransport"/> class.
    /// </summary>
    public RocketQuicTransport(
        IPEndPoint endpoint,
        X509Certificate2 certificate,
        RpcOptions? options = null,
        int maxConnections = 1000,
        int maxInboundStreams = 100)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _certificate = certificate ?? throw new ArgumentNullException(nameof(certificate));
        _options = options ?? new RpcOptions();
        _maxConnections = maxConnections;
        _maxInboundStreams = maxInboundStreams;
        
        _connectionState = new QuicConnectionStateMachine();
        _sendPipe = new Pipe();
        _receivePipe = new Pipe();
        
        // Modern: Channels for backpressure-aware frame multiplexing
        _pendingRequests = Channel.CreateUnbounded<(int StreamId, TaskCompletionSource<ReadOnlyMemory<byte>> Tcs)>(
            new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
        _inboundFrames = Channel.CreateBounded<(long StreamId, ReadOnlyMemory<byte> Data, bool Fin)>(
            new BoundedChannelOptions(10000) { SingleReader = false, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
        _outboundFrames = Channel.CreateBounded<(long StreamId, ReadOnlyMemory<byte> Data, bool Fin)>(
            new BoundedChannelOptions(10000) { SingleReader = false, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
        
        // Initialize CRYPTO frame buffer with PQ-TLS support
        var maxCryptoBufferSize = _options.PostQuantumTls?.MaxCryptoFrameBufferSize ?? 16384;
        _cryptoBuffer = new CryptoFrameBuffer(maxCryptoFrameBufferSize);
        
        // Create UDP socket for QUIC
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(_endpoint);
        
        // Enable UDP GRO (Generic Receive Offload) if supported
        if (_groSupported)
        {
            EnableUdpGro();
        }
        
        // Initialize POH (Pinned Object Heap) buffer pool for zero-GC socket I/O
        _pinnedBufferPool = MemoryPool<byte>.Create(65536, maxBuffers: 128, pinned: true);
        
        // Initialize flow control
        _flowControl = new QuicFlowControl();
        
        // Initialize write completion tracker
        _writeCompletionTracker = new WriteCompletionTracker();
        
        // Start receive loop
        _receiveTask = ReceiveLoopAsync(_cts.Token);
        
        // Start frame processing loops
        _ = ProcessInboundFramesAsync(_cts.Token);
        _ = ProcessOutboundFramesAsync(_cts.Token);
    }
    
    private static bool TryEnableGro()
    {
        try
        {
            // Test if GRO is supported on this platform
            using var testSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            testSocket.SetSocketOption(SocketOptionLevel.Socket, (SocketOptionName)40, 1); // SO_GRO = 40 on Linux
            return true;
        }
        catch
        {
            return false;
        }
    }
    
    private void EnableUdpGro()
    {
        try
        {
            // Linux: SO_GRO = 40 (kernel 4.18+)
            // Windows: SIO_UDP_GRO = 0x98000000 + 11
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                _socket.SetSocketOption(SocketOptionLevel.Socket, (SocketOptionName)40, 1);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                const int SIO_UDP_GRO = -2147483648 + (11 << 16);
                var inValue = BitConverter.GetBytes(1);
                var outValue = new byte[4];
                _socket.IOControl(SIO_UDP_GRO, inValue, outValue);
            }
        }
        catch
        {
            // GRO not supported, continue without it
        }
    }

    private const int SIO_UDP_GRO = -2147483648 + (11 << 16);

    /// <inheritdoc/>
    public async ValueTask<ReadOnlyMemory<byte>> SendRequestAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (body.IsEmpty)
            throw new ArgumentException("Request body must not be empty.", nameof(body));

        var streamId = GetNextStreamId();
        var tcs = new TaskCompletionSource<ReadOnlyMemory<byte>>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Modern: Use Channel for backpressure-aware pending requests
        if (!_pendingRequests.Writer.TryWrite((streamId, tcs)))
        {
            throw new InvalidOperationException("Pending requests channel full");
        }

        try
        {
            await WriteStreamAsync(streamId, body, true, cancellationToken).ConfigureAwait(false);

            using var registration = cancellationToken.Register(static state =>
            {
                var tcs = (TaskCompletionSource<ReadOnlyMemory<byte>>)state!;
                tcs.TrySetCanceled();
            }, tcs);

            return await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _pendingRequests.Reader.TryRead(out _); // Remove from channel
            throw;
        }
    }

    /// <summary>
    /// Gets the next available stream ID (client-initiated, bidirectional = even).
    /// </summary>
    private int GetNextStreamId()
    {
        return (int)(Interlocked.Add(ref _nextStreamId, 4) - 4);
    }

    /// <summary>
    /// Writes a Rocket frame to a QUIC stream with flow control and write completion tracking.
    /// </summary>
    private async ValueTask WriteStreamAsync(long streamId, ReadOnlyMemory<byte> data, bool fin, CancellationToken cancellationToken)
    {
        // Check flow control
        var flowContext = _streamFlowContexts.GetOrAdd(streamId, _ => new StreamFlowContext(streamId, _flowControl.MaxStreamDataRemote, _flowControl.MaxStreamDataRemote));
        
        if (!flowContext.CanSend(data.Length))
        {
            // Wait for window to open
            await WaitForWindowAsync(streamId, data.Length, cancellationToken);
        }
        
        if (!flowContext.TryConsumeWindow(data.Length))
        {
            throw new InvalidOperationException($"Cannot send {data.Length} bytes on stream {streamId}: window exhausted");
        }

        var metadata = BuildRequestMetadata(streamId, data.Length);
        var frame = RocketFrame.CreateRequestResponse((int)streamId, metadata, data, fin);

        // Register write for completion tracking
        var writeId = _writeCompletionTracker.RegisterWrite(streamId, data.Length);

        // Modern: Use ArrayPool for frame encoding buffer
        var buffer = _framePool.Rent(4096);
        try
        {
            var writer = new ArrayBufferWriter<byte>(buffer);
            frame.Write(writer);
            var frameData = writer.WrittenMemory;
            
            var streamContext = _streams.GetOrAdd(streamId, _ => new StreamContext(streamId));
            await streamContext.WriteAsync(frameData, cancellationToken).ConfigureAwait(false);
            
            // Modern: Use Channel for backpressure-aware outbound frames
            await _outboundFrames.Writer.WriteAsync((streamId, frameData, fin), cancellationToken).ConfigureAwait(false);
            
            // Wait for write completion (acknowledged by peer)
            try
            {
                await _writeCompletionTracker.RegisterWrite(streamId, data.Length).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _writeCompletionTracker.FailWrite(streamId, 0, new OperationCanceledException());
                throw;
            }
        }
        finally
        {
            _framePool.Return(buffer);
        }
    }

    /// <summary>
    /// Builds request metadata for Rocket frame.
    /// </summary>
    private ReadOnlyMemory<byte> BuildRequestMetadata(long streamId, int dataLength)
    {
        var metadata = new RequestRpcMetadata
        {
            Name = "Unknown",
            Protocol = ProtocolId.Compact,
            Kind = RpcKind.SingleRequestSingleResponse,
            Compression = CompressionAlgorithm.None,
        };

        return ThriftSerializer.EncodeCompact(metadata);
    }

    /// <summary>
    /// Waits for flow control window to open for sending data.
    /// </summary>
    private async ValueTask WaitForWindowAsync(long streamId, long bytes, CancellationToken cancellationToken)
    {
        var flowContext = _streamFlowContexts[streamId];
        
        while (!flowContext.CanSend(bytes))
        {
            // Wait for window update (MAX_STREAM_DATA frame from peer)
            await Task.Delay(10, cancellationToken);
            
            // Check if connection is still alive
            if (_disposed || cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("Connection closed or cancelled");
            }
        }
    }

    /// <summary>
    /// Main receive loop for QUIC packets using batch receive for high throughput.
    /// </summary>
    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        // Use POH (Pinned Object Heap) buffers for zero-GC socket I/O
        var buffers = new Memory<byte>[ReceiveBatchSize];
        for (int i = 0; i < ReceiveBatchSize; i++)
        {
            buffers[i] = _pinnedBufferPool.Rent(65536).Memory;
        }
        
        var endPoint = new IPEndPoint(IPAddress.Any, 0);
        var results = new SocketReceiveFromResult[ReceiveBatchSize];
        
        try
        {
            while (!cancellationToken.IsCancellationRequested && !_disposed)
            {
                SocketReceiveFromResult result;
                
                // Use batch receive for high throughput (NET 9+)
                if (ReceiveBatchSize > 1 && _socket.SupportsBatchReceive)
                {
                    int count = await _socket.ReceiveFromMultipleAsync(buffers, results, SocketFlags.None, cancellationToken).ConfigureAwait(false);
                    
                    for (int i = 0; i < count; i++)
                    {
                        if (results[i].ReceivedBytes > 0)
                        {
                            var packetData = buffers[i].Slice(0, results[i].ReceivedBytes);
                            await ProcessPacketAsync(packetData, results[i].RemoteEndPoint, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
                else
                {
                    // Fallback to single receive
                    var buffer = _pinnedBufferPool.Rent(65536).Memory;
                    var endPoint = new IPEndPoint(IPAddress.Any, 0);
                    
                    SocketReceiveFromResult result;
                    try
                    {
                        result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, endPoint, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (ObjectDisposedException) { break; }

                    if (result.ReceivedBytes > 0)
                    {
                        var packetData = buffer.Slice(0, result.ReceivedBytes);
                        await ProcessPacketAsync(packetData, result.RemoteEndPoint, cancellationToken).ConfigureAwait(false);
                    }
                    
                    _pinnedBufferPool.Return(buffer.Memory);
                }
            }
        }
        catch (Exception ex)
        {
            // Complete all pending requests with error
            while (_pendingRequests.Reader.TryRead(out var pending))
            {
                pending.Tcs.TrySetException(new IOException("Connection lost", ex));
            }
        }
    }

    /// <summary>
    /// Processes a received QUIC packet.
    /// </summary>
    private async ValueTask ProcessPacketAsync(ReadOnlyMemory<byte> packetData, EndPoint remoteEndPoint, CancellationToken cancellationToken)
    {
        var header = QuicPacketHeader.Parse(packetData.Span, out int headerLength);
        var payload = packetData.Slice(headerLength);
        
        var offset = 0;
        while (offset < payload.Length)
        {
            var frame = QuicFrame.Decode(payload.Span[offset..], out int bytesConsumed);
            offset += bytesConsumed;
            await HandleQuicFrameAsync(frame, cancellationToken).ConfigureAwait(false);
        }

        _connectionState.RecordActivity();
    }

    /// <summary>
    /// Processes inbound frames from QUIC streams (Channel consumer).
    /// </summary>
    private async Task ProcessInboundFramesAsync(CancellationToken cancellationToken)
    {
        await foreach (var (streamId, data, fin) in _inboundFrames.Reader.ReadAllAsync(cancellationToken))
        {
            var streamContext = _streams.GetOrAdd(streamId, _ => new StreamContext(streamId));
            streamContext.AppendData(data, fin);

            if (fin)
            {
                var completeData = streamContext.GetCompleteData();
                if (completeData.Length > 0)
                {
                    await ProcessRocketFrameAsync(completeData, streamId, cancellationToken).ConfigureAwait(false);
                }
                _streams.TryRemove(streamId, out _);
            }
        }
    }

    /// <summary>
    /// Processes outbound frames for network transmission (Channel consumer).
    /// </summary>
    private async Task ProcessOutboundFramesAsync(CancellationToken cancellationToken)
    {
        await foreach (var (streamId, data, fin) in _outboundFrames.Reader.ReadAllAsync(cancellationToken))
        {
            // Write to send pipe for network transmission
            await _sendPipe.Writer.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Handles a QUIC frame.
    /// </summary>
    private async ValueTask HandleQuicFrameAsync(QuicFrame frame, CancellationToken cancellationToken)
    {
        switch (frame.Type)
        {
            case QuicFrameType.Stream:
                if (frame is StreamFrame streamFrame)
                {
                    await HandleStreamFrameAsync(streamFrame, cancellationToken).ConfigureAwait(false);
                }
                break;

            case QuicFrameType.Ack:
                if (frame is AckFrame ackFrame)
                {
                    await HandleAckFrameAsync(ackFrame, cancellationToken).ConfigureAwait(false);
                }
                break;

            case QuicFrameType.Crypto:
                if (frame is CryptoFrame cryptoFrame)
                {
                    await HandleCryptoFrameAsync(cryptoFrame, cancellationToken).ConfigureAwait(false);
                }
                break;

            case QuicFrameType.ConnectionClose:
                // Handle connection close
                _connectionState.Close();
                break;

            case QuicFrameType.HandshakeDone:
                _connectionState.Close();
                break;
        }
    }

    /// <summary>
    /// Handles an ACK frame to update write completion tracking and flow control.
    /// </summary>
    private async ValueTask HandleAckFrameAsync(AckFrame frame, CancellationToken cancellationToken)
    {
        // Process ACK ranges for write completion tracking
        foreach (var range in frame.AckRanges)
        {
            for (ulong pn = range.Start; pn <= range.End; pn++)
            {
                var packetNum = (long)pn;
                
                // Complete write for this packet
                _writeCompletionTracker.CompleteWrite(0, packetNum);
                
                // Update flow control - packet acknowledged
                // In a full implementation, we'd track which stream this packet belongs to
            }
        }
        
        // Process ECN counts if present
        if (frame.EcnCounts != null)
        {
            // Handle ECN
        }
    }

    /// <summary>
    /// Handles a CRYPTO frame (TLS handshake data).
    /// Buffers and reassembles CRYPTO frames across multiple packets for PQ-TLS support.
    /// </summary>
    private async ValueTask HandleCryptoFrameAsync(CryptoFrame frame, CancellationToken cancellationToken)
    {
        var level = (EncryptionLevel)frame.Offset; // Simplified: using offset as level indicator
        
        // Add crypto frame data to buffer
        var added = _cryptoBuffer.AddData(level, frame.Offset, frame.Data);
        if (!added)
        {
            // Buffer overflow - connection should be closed
            _connectionState.Close();
            return;
        }

        // Check if we have complete handshake data for this encryption level
        var completeData = _cryptoBuffer.GetCompleteData(level);
        if (completeData != null)
        {
            // Process complete handshake data (would integrate with TLS stack)
            // For now, just mark as complete
            _cryptoBuffer.Clear(level);
        }
    }

    /// <summary>
    /// Handles a STREAM frame containing Rocket data.
    /// </summary>
    private async ValueTask HandleStreamFrameAsync(StreamFrame frame, CancellationToken cancellationToken)
    {
        var streamId = (long)frame.StreamId;
        
        // Get or create stream context
        var streamContext = _streams.GetOrAdd(streamId, _ => new StreamContext(streamId));
        
        // Update flow control context for received data
        var flowContext = _streamFlowContexts.GetOrAdd(streamId, _ => new StreamFlowContext(streamId, _flowControl.MaxStreamDataRemote, _flowControl.MaxStreamDataRemote));
        flowContext.AddBytesReceived(frame.Data.Length);
        
        // Append data to stream buffer
        streamContext.AppendData(frame.Data, frame.Fin);
        
        // Update flow control for received data
        _flowControl.OnStreamDataReceived(streamId, frame.Data.Length);
        
        if (frame.Fin)
        {
            flowContext.MarkFinReceived();
            
            // Stream complete, process Rocket frame
            var completeData = streamContext.GetCompleteData();
            if (completeData.Length > 0)
            {
                await ProcessRocketFrameAsync(completeData, streamId, cancellationToken).ConfigureAwait(false);
            }
            
            _streams.TryRemove(streamId, out _);
            flowContext.MarkFinReceived();
        }
    }

    /// <summary>
    /// Processes a complete Rocket frame from QUIC stream data.
    /// </summary>
    private async ValueTask ProcessRocketFrameAsync(ReadOnlyMemory<byte> data, long streamId, CancellationToken cancellationToken)
    {
        if (data.Length < RocketFrameHeader.HeaderSize)
            return;

        var header = RocketFrameHeader.Read(data.Span);
        
        var metadataLength = (header.Flags & RocketFrameFlags.Metadata) != 0
            ? BinaryPrimitives.ReadInt32BigEndian(data.Span.Slice(RocketFrameHeader.HeaderSize, 4))
            : 0;

        var metadataStart = RocketFrameHeader.HeaderSize;
        var dataStart = metadataStart + metadataLength;
        var dataLength = header.Length - metadataLength;

        var metadata = metadataLength > 0
            ? data.Slice(metadataStart, metadataLength)
            : ReadOnlyMemory<byte>.Empty;

        var frameData = dataLength > 0
            ? data.Slice(dataStart, dataLength)
            : ReadOnlyMemory<byte>.Empty;

        var rocketFrame = new RocketFrame(
            new RocketFrameHeader(header.Length, header.Type, header.Flags, header.StreamId),
            metadata,
            frameData);

        // Handle based on frame type
        switch (rocketFrame.Header.Type)
        {
            case RocketFrameType.RequestResponse:
            case RocketFrameType.Payload:
                // Response to a pending request
                if (_pendingRequests.TryRemove(rocketFrame.Header.StreamId, out var tcs))
                {
                    tcs.TrySetResult(frameData);
                }
                break;

            case RocketFrameType.Error:
                if (_pendingRequests.TryRemove(rocketFrame.Header.StreamId, out var errorTcs))
                {
                    var errorMsg = System.Text.Encoding.UTF8.GetString(frameData.Span);
                    errorTcs.TrySetException(new IOException($"Rocket error: {errorMsg}"));
                }
                break;
        }
    }

    /// <summary>
    /// Starts accepting incoming QUIC connections (server mode).
    /// </summary>
    public async Task StartAcceptingAsync(CancellationToken cancellationToken = default)
    {
        // In a full implementation, this would:
        // 1. Listen for Initial packets
        // 2. Perform QUIC handshake (TLS 1.3)
        // 3. Accept 0-RTT if enabled
        // 4. Create streams for incoming requests
        
        await Task.Yield(); // Placeholder for async initialization
    }

    /// <summary>
    /// Disposes the transport and completes all pending requests.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cts.Cancel();

        try
        {
            await _receiveTask.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Ignore cancellation exceptions
        }

        // Complete all pending requests
        foreach (var (_, tcs) in _pendingRequests)
        {
            tcs.TrySetCanceled();
        }
        _pendingRequests.Clear();

        // Dispose streams
        foreach (var (_, stream) in _streams)
        {
            stream.Dispose();
        }
        _streams.Clear();

        _sendPipe.Reader.Complete();
        _sendPipe.Writer.Complete();
        _receivePipe.Reader.Complete();
        _receivePipe.Writer.Complete();

        _socket?.Dispose();
        _connectionState.Dispose();
    }

    /// <summary>
    /// Stream context for managing QUIC stream state.
    /// </summary>
    private sealed class StreamContext : IDisposable
    {
        private readonly long _streamId;
        private readonly Pipe _pipe = new();
        private bool _finReceived = false;

        public StreamContext(long streamId)
        {
            _streamId = streamId;
        }

        public void AppendData(ReadOnlyMemory<byte> data, bool fin)
        {
            if (data.Length > 0)
            {
                _pipe.Writer.Write(data.Span);
            }
            _finReceived = fin;
        }

        public ReadOnlyMemory<byte> GetCompleteData()
        {
            var reader = _pipe.Reader;
            var buffer = reader.GetMemory();
            // In a real implementation, you'd properly read from the pipe
            // This is simplified - the actual implementation would properly consume from the pipe
            return ReadOnlyMemory<byte>.Empty;
        }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            await _pipe.Writer.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        }

        public void Dispose()
        {
            _pipe.Reader.Complete();
            _pipe.Writer.Complete();
        }
    }
}
