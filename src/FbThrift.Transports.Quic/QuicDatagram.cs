// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Quic;

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;

/// <summary>
/// QUIC Datagram support for unreliable data transfer.
/// Based on RFC 9221 (Unreliable Datagram Extension for QUIC).
/// </summary>
public sealed class QuicDatagram
{
    // Datagram frame type
    private const byte DatagramFrameType = 0x30; // 0x30-0x31 (with length bit)

    // Default limits
    private const int DefaultMaxDatagramSize = 1200;
    private const int DefaultMaxDatagramQueueSize = 1024;
    private const int DefaultReceiveBufferSize = 65536;

    // State
    private readonly ConcurrentQueue<DatagramFrame> _sendQueue = new();
    private readonly ConcurrentQueue<DatagramFrame> _receiveQueue = new();
    private readonly SemaphoreSlim _sendSemaphore;
    private readonly SemaphoreSlim _receiveSemaphore;
    private int _maxDatagramSize;
    private int _maxSendQueueSize;
    private int _maxReceiveQueueSize;
    private long _datagramsSent;
    private long _datagramsReceived;
    private long _datagramsDroppedSend;
    private long _datagramsDroppedReceive;
    private long _bytesSent;
    private long _bytesReceived;
    private bool _closed;

    /// <summary>
    /// Initializes a new instance of the <see cref="QuicDatagram"/> class.
    /// </summary>
    public QuicDatagram(int maxDatagramSize = DefaultMaxDatagramSize)
    {
        _maxDatagramSize = Math.Max(1, Math.Min(maxDatagramSize, DefaultMaxDatagramSize));
        _maxSendQueueSize = DefaultMaxDatagramQueueSize;
        _maxReceiveQueueSize = DefaultMaxDatagramQueueSize;
        _sendSemaphore = new SemaphoreSlim(_maxSendQueueSize);
        _receiveSemaphore = new SemaphoreSlim(_maxReceiveQueueSize);
    }

    /// <summary>
    /// Gets the maximum datagram size.
    /// </summary>
    public int MaxDatagramSize => _maxDatagramSize;

    /// <summary>
    /// Gets the number of datagrams sent.
    /// </summary>
    public long DatagramsSent => _datagramsSent;

    /// <summary>
    /// Gets the number of datagrams received.
    /// </summary>
    public long DatagramsReceived => _datagramsReceived;

    /// <summary>
    /// Gets the number of datagrams dropped due to send queue full.
    /// </summary>
    public long DatagramsDroppedSend => _datagramsDroppedSend;

    /// <summary>
    /// Gets the number of datagrams dropped due to receive queue full.
    /// </summary>
    public long DatagramsDroppedReceive => _datagramsDroppedReceive;

    /// <summary>
    /// Gets the total bytes sent.
    /// </summary>
    public long BytesSent => _bytesSent;

    /// <summary>
    /// Gets the total bytes received.
    /// </summary>
    public long BytesReceived => _bytesReceived;

    /// <summary>
    /// Sets the maximum datagram size.
    /// </summary>
    public void SetMaxDatagramSize(int size)
    {
        _maxDatagramSize = Math.Max(1, Math.Min(size, DefaultMaxDatagramSize));
    }

    /// <summary>
    /// Sets the maximum send queue size.
    /// </summary>
    public void SetMaxSendQueueSize(int size)
    {
        _maxSendQueueSize = Math.Max(1, size);
        // Note: SemaphoreSlim doesn't support dynamic resizing easily
    }

    /// <summary>
    /// Sets the maximum receive queue size.
    /// </summary>
    public void SetMaxReceiveQueueSize(int size)
    {
        _maxReceiveQueueSize = Math.Max(1, size);
    }

    /// <summary>
    /// Sends a datagram (fire-and-forget).
    /// </summary>
    /// <returns>True if queued, false if queue is full or datagram too large.</returns>
    public bool TrySend(ReadOnlyMemory<byte> data)
    {
        if (_closed)
        {
            return false;
        }

        if (data.Length > _maxDatagramSize)
        {
            return false; // Datagram too large
        }

        if (!_sendSemaphore.Wait(0))
        {
            Interlocked.Increment(ref _datagramsDroppedSend);
            return false; // Queue full
        }

        try
        {
            var frame = new DatagramFrame
            {
                Data = data.ToArray(),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            _sendQueue.Enqueue(frame);
            Interlocked.Increment(ref _datagramsSent);
            Interlocked.Add(ref _bytesSent, data.Length);
            return true;
        }
        catch
        {
            _sendSemaphore.Release();
            return false;
        }
    }

    /// <summary>
    /// Sends a datagram asynchronously.
    /// </summary>
    public async Task<bool> SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (_closed)
        {
            return false;
        }

        if (data.Length > _maxDatagramSize)
        {
            return false;
        }

        try
        {
            await _sendSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _datagramsDroppedSend);
            return false;
        }

        try
        {
            var frame = new DatagramFrame
            {
                Data = data.ToArray(),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            _sendQueue.Enqueue(frame);
            Interlocked.Increment(ref _datagramsSent);
            Interlocked.Add(ref _bytesSent, data.Length);
            return true;
        }
        catch
        {
            _sendSemaphore.Release();
            return false;
        }
    }

    /// <summary>
    /// Tries to receive a datagram.
    /// </summary>
    public bool TryReceive(out byte[] data)
    {
        data = null;

        if (_receiveQueue.TryDequeue(out var frame))
        {
            _receiveSemaphore.Release();
            data = frame.Data;
            Interlocked.Increment(ref _datagramsReceived);
            Interlocked.Add(ref _bytesReceived, data.Length);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Receives a datagram asynchronously.
    /// </summary>
    public async Task<byte[]> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        await _receiveSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

        if (_receiveQueue.TryDequeue(out var frame))
        {
            var data = frame.Data;
            Interlocked.Increment(ref _datagramsReceived);
            Interlocked.Add(ref _bytesReceived, data.Length);
            return data;
        }

        return null;
    }

    /// <summary>
    /// Gets the next datagram to send (for the transport layer).
    /// </summary>
    public bool TryDequeueSend(out DatagramFrame frame)
    {
        return _sendQueue.TryDequeue(out frame);
    }

    /// <summary>
    /// Enqueues a received datagram (called by transport layer).
    /// </summary>
    public bool TryEnqueueReceive(ReadOnlyMemory<byte> data)
    {
        if (_closed)
        {
            return false;
        }

        if (data.Length > _maxDatagramSize)
        {
            Interlocked.Increment(ref _datagramsDroppedReceive);
            return false;
        }

        if (!_receiveSemaphore.Wait(0))
        {
            Interlocked.Increment(ref _datagramsDroppedReceive);
            return false;
        }

        try
        {
            var frame = new DatagramFrame
            {
                Data = data.ToArray(),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            _receiveQueue.Enqueue(frame);
            return true;
        }
        catch
        {
            _receiveSemaphore.Release();
            return false;
        }
    }

    /// <summary>
    /// Encodes a DATAGRAM frame for transmission.
    /// </summary>
    public static byte[] EncodeDatagramFrame(ReadOnlyMemory<byte> data)
    {
        var writer = new ArrayBufferWriter<byte>();
        
        // Frame type (0x30 for datagram without length, 0x31 with length)
        // We use 0x31 (with length) for simplicity
        writer.Write(new byte[] { 0x31 });
        
        // Length (varint)
        WriteVarint(writer, (ulong)data.Length);
        
        // Data
        writer.Write(data.Span);
        
        return writer.WrittenMemory.ToArray();
    }

    /// <summary>
    /// Decodes a DATAGRAM frame from the given data.
    /// </summary>
    public static (byte[] Data, int BytesConsumed) DecodeDatagramFrame(ReadOnlyMemory<byte> data)
    {
        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(data));
        
        if (!reader.TryRead(out var frameType))
        {
            return (null, 0);
        }
        
        // Check frame type (0x30 or 0x31)
        if (frameType != 0x30 && frameType != 0x31)
        {
            return (null, 0);
        }
        
        var hasLength = frameType == 0x31;
        
        ulong length = 0;
        if (hasLength)
        {
            if (!ReadVarint(ref reader, out length))
            {
                return (null, 0);
            }
        }
        else
        {
            // Length is rest of frame
            length = (ulong)reader.Remaining;
        }
        
        if ((ulong)reader.Remaining < length)
        {
            return (null, 0);
        }
        
        var payload = reader.Sequence.Slice(reader.Position, (int)length).ToArray();
        reader.Advance((long)length);
        
        return (payload, (int)reader.Consumed);
    }

    /// <summary>
    /// Closes the datagram queues.
    /// </summary>
    public void Close()
    {
        _closed = true;
        _sendSemaphore.Release(_maxSendQueueSize);
        _receiveSemaphore.Release(_maxReceiveQueueSize);
    }

    /// <summary>
    /// Resets statistics.
    /// </summary>
    public void ResetStats()
    {
        _datagramsSent = 0;
        _datagramsReceived = 0;
        _datagramsDroppedSend = 0;
        _datagramsDroppedReceive = 0;
        _bytesSent = 0;
        _bytesReceived = 0;
    }

    private static void WriteVarint(ArrayBufferWriter<byte> writer, ulong value)
    {
        if (value < 0x40)
        {
            writer.Write(new byte[] { (byte)value });
        }
        else if (value < 0x4000)
        {
            writer.Write(new byte[] { (byte)(0x40 | (value >> 8)), (byte)value });
        }
        else if (value < 0x40000000)
        {
            writer.Write(new byte[] { (byte)(0x80 | (value >> 24)), (byte)(value >> 16), (byte)(value >> 8), (byte)value });
        }
        else
        {
            writer.Write(new byte[] { 
                (byte)(0xC0 | (value >> 56)), 
                (byte)(value >> 48), 
                (byte)(value >> 40), 
                (byte)(value >> 32), 
                (byte)(value >> 24), 
                (byte)(value >> 16), 
                (byte)(value >> 8), 
                (byte)value 
            });
        }
    }

    private static bool ReadVarint(ref SequenceReader<byte> reader, out ulong value)
    {
        value = 0;
        
        if (!reader.TryRead(out var firstByte))
        {
            return false;
        }
        
        var prefix = firstByte >> 6;
        var length = 1 << (int)prefix;
        
        value = (ulong)(firstByte & 0x3F);
        
        for (int i = 1; i < length; i++)
        {
            if (!reader.TryRead(out var b))
            {
                return false;
            }
            value = (value << 8) | b;
        }
        
        return true;
    }

    /// <summary>
    /// Represents a QUIC datagram frame.
    /// </summary>
    public sealed class DatagramFrame
    {
        public byte[] Data { get; set; }
        public long Timestamp { get; set; }
    }
}
