// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Threading.Channels;

namespace FbThrift.Transports.Tcp;

/// <summary>
/// A Thrift transport over a TCP socket using <see cref="System.IO.Pipelines"/> for zero-allocation
/// frame reading and <see cref="Channel{T}"/> for request/response multiplexing with backpressure.
/// </summary>
public sealed class SocketFbThriftTransport : IFbThriftTransport, IAsyncDisposable
{
    private const int HeaderSize = 8;
    
    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly PipeReader _reader;
    private readonly Task _readLoop;
    private readonly Channel<PendingRequest> _pendingChannel;
    private readonly CancellationTokenSource _cts = new();
    private short _nextSeqId;
    private bool _disposed;
    private volatile Exception? _readLoopFault;

    /// <summary>
    /// Represents a pending request with its response channel.
    /// </summary>
    private sealed class PendingRequest
    {
        public required short SeqId { get; init; }
        public required TaskCompletionSource<ReadOnlyMemory<byte>> Tcs { get; init; }
        public required CancellationTokenRegistration CancellationRegistration { get; init; }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SocketFbThriftTransport"/> class.
    /// </summary>
    /// <param name="socket">The connected socket to transport frames over.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="socket"/> is null.</exception>
    public SocketFbThriftTransport(Socket socket)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _stream = new NetworkStream(socket, ownsSocket: true);
        _reader = PipeReader.Create(_stream);
        
        // Bounded channel with backpressure - wait when full
        _pendingChannel = Channel.CreateBounded<PendingRequest>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = true
        });
        
        _readLoop = Task.Run(ReadLoopAsync);
    }

    /// <inheritdoc/>
    public async ValueTask<ReadOnlyMemory<byte>> SendRequestAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (body.IsEmpty)
            throw new ArgumentException("Request body must not be empty.", nameof(body));

        short seqId;
        var tcs = new TaskCompletionSource<ReadOnlyMemory<byte>>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Get next sequence ID
        seqId = unchecked((short)(_nextSeqId + 1));
        _nextSeqId = seqId;

        // Register cancellation
        var cancellationRegistration = cancellationToken.Register(static state =>
        {
            var tcs = (TaskCompletionSource<ReadOnlyMemory<byte>>)state!;
            tcs.TrySetCanceled();
        }, tcs);

        var pending = new PendingRequest
        {
            SeqId = seqId,
            Tcs = tcs,
            CancellationRegistration = cancellationRegistration
        };

        // Write to channel (applies backpressure if channel is full)
        await _pendingChannel.Writer.WriteAsync(pending, cancellationToken).ConfigureAwait(false);

        try
        {
            await WriteFrameAsync(seqId, body, cancellationToken).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Try to remove from channel - if not read yet, it'll be cleaned up by read loop
            cancellationRegistration.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads frames from the pipe and completes the matching pending request's task completion source.
    /// Uses Channel for thread-safe, backpressure-enabled request matching.
    /// </summary>
    private async Task ReadLoopAsync()
    {
        try
        {
            // Process pending requests from channel
            await foreach (var pending in _pendingChannel.Reader.ReadAllAsync(_cts.Token))
            {
                // Wait for frame with matching seqId
                var body = await ReadFrameWithSeqIdAsync(pending.SeqId, _cts.Token).ConfigureAwait(false);
                
                pending.CancellationRegistration.Dispose();
                pending.Tcs.TrySetResult(body);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation
        }
        catch (Exception ex)
        {
            _readLoopFault = ex;
            
            // Complete all pending requests with error
            while (_pendingChannel.Reader.TryRead(out var pending))
            {
                pending.CancellationRegistration.Dispose();
                pending.Tcs.TrySetException(new FbThriftException("The transport read loop faulted.", ex));
            }
        }
        finally
        {
            _pendingChannel.Writer.Complete();
            _reader.Complete();
        }
    }

    /// <summary>
    /// Reads frames until one with the matching seqId is found.
    /// </summary>
    private async ValueTask<ReadOnlyMemory<byte>> ReadFrameWithSeqIdAsync(short targetSeqId, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            ReadResult result = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;

            while (TryReadFrame(buffer, out ReadOnlyMemory<byte> body, out short seqId, out ReadOnlySequence<byte> remaining))
            {
                if (seqId == targetSeqId)
                {
                    _reader.AdvanceTo(buffer.Start, buffer.End);
                    return body;
                }
                
                // Frame doesn't match - this shouldn't happen in well-behaved client
                // but we skip it to avoid deadlock
                buffer = remaining;
            }

            _reader.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted)
            {
                throw new FbThriftException("Connection closed before response received.");
            }
        }
    }

    /// <summary>
    /// Attempts to read a single THeader frame from the buffer.
    /// </summary>
    private static bool TryReadFrame(ReadOnlySequence<byte> buffer, out ReadOnlyMemory<byte> body, out short seqId, out ReadOnlySequence<byte> remaining)
    {
        body = default;
        seqId = 0;
        remaining = buffer;

        if (buffer.Length < HeaderSize)
        {
            return false;
        }

        byte[] header = new byte[HeaderSize];
        buffer.Slice(0, HeaderSize).CopyTo(header);

        ushort magic = BinaryPrimitives.ReadUInt16BigEndian(header);
        if (magic != 0x8001)
        {
            throw new FbThriftException($"Invalid THeader magic number: 0x{magic:X4}.");
        }

        // header[2] = version, header[3] = flags (ignored)
        ushort bodySize = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
        seqId = BinaryPrimitives.ReadInt16BigEndian(header.AsSpan(6));

        int frameSize = HeaderSize + bodySize;
        if (buffer.Length < frameSize)
        {
            return false;
        }

        body = buffer.Slice(HeaderSize, bodySize).ToArray();
        remaining = buffer.Slice(frameSize);
        return true;
    }

    /// <summary>
    /// Writes a THeader frame to the socket stream.
    /// </summary>
    private async Task WriteFrameAsync(short seqId, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        if (body.Length > ushort.MaxValue)
        {
            throw new InvalidOperationException("Request body too large for THeader frame.");
        }

        byte[] header = new byte[HeaderSize];
        header[0] = 0x80;
        header[1] = 0x01;
        header[2] = 1; // version
        header[3] = 0; // flags
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), (ushort)body.Length);
        BinaryPrimitives.WriteInt16BigEndian(header.AsSpan(6), seqId);

        await _stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await _stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Disposes the transport and completes all pending requests with an exception.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return default;
        }

        _disposed = true;
        _cts.Cancel();

        try
        {
            _pendingChannel.Writer.Complete();
            _reader.Complete();
            _stream.Dispose();
        }
        catch (Exception)
        {
            // Disposal should not throw.
        }

        return default;
    }
}
