// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Core;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

/// <summary>
/// Lock-free concurrent queue optimized for QUIC packet processing.
/// Based on tquic's lock-free queue design.
/// </summary>
public sealed class LockFreeQueue<T> where T : class
{
    private Node _head;
    private Node _tail;
    private int _count;

    public LockFreeQueue()
    {
        var dummy = new Node(null);
        _head = dummy;
        _tail = dummy;
    }

    /// <summary>
    /// Enqueues an item (lock-free).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryEnqueue(T item)
    {
        if (item == null) return false;

        var newNode = new Node(item);
        while (true)
        {
            var tail = _tail;
            var next = tail.Next;
            
            if (tail == _tail)
            {
                if (next == null)
                {
                    if (Interlocked.CompareExchange(ref tail.Next, newNode, null) == null)
                    {
                        Interlocked.CompareExchange(ref _tail, newNode, tail);
                        Interlocked.Increment(ref _count);
                        return true;
                    }
                }
                else
                {
                    Interlocked.CompareExchange(ref _tail, next, tail);
                }
            }
        }
    }

    /// <summary>
    /// Dequeues an item (lock-free).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeue(out T item)
    {
        while (true)
        {
            var head = _head;
            var tail = _tail;
            var next = head.Next;

            if (head == _head)
            {
                if (head == tail)
                {
                    if (next == null)
                    {
                        item = null!;
                        return false;
                    }
                    Interlocked.CompareExchange(ref _tail, next, tail);
                }
                else
                {
                    if (next != null)
                    {
                        item = next.Value;
                        if (Interlocked.CompareExchange(ref _head, next, head) == head)
                        {
                            Interlocked.Decrement(ref _count);
                            return true;
                        }
                    }
                }
            }
        }
    }

    public int Count => _count;

    private sealed class Node
    {
        public T Value { get; }
        public Node Next;

        public Node(T value)
        {
            Value = value;
        }
    }
}

/// <summary>
/// Lock-free ring buffer for high-throughput packet batching.
/// Based on tquic's ring buffer design.
/// </summary>
public sealed class LockFreeRingBuffer<T> where T : class
{
    private readonly T[] _buffer;
    private readonly int _mask;
    private int _head;
    private int _tail;

    public LockFreeRingBuffer(int capacity)
    {
        if ((capacity & (capacity - 1)) != 0)
            throw new ArgumentException("Capacity must be a power of 2", nameof(capacity));

        _buffer = new T[capacity];
        _mask = capacity - 1;
    }

    /// <summary>
    /// Tries to enqueue an item.
    /// </summary>
    public bool TryEnqueue(T item)
    {
        var tail = _tail;
        var nextTail = (tail + 1) & _mask;

        if (nextTail == _head)
        {
            return false; // Full
        }

        _buffer[tail] = item;
        Volatile.Write(ref _tail, nextTail);
        return true;
    }

    /// <summary>
    /// Tries to dequeue an item.
    /// </summary>
    public bool TryDequeue(out T item)
    {
        var head = _head;
        if (head == Volatile.Read(ref _tail))
        {
            item = null!;
            return false; // Empty
        }

        item = _buffer[head];
        _buffer[head] = null!;
        Volatile.Write(ref _head, (head + 1) & _mask);
        return true;
    }

    /// <summary>
    /// Tries to dequeue multiple items efficiently.
    /// </summary>
    public int TryDequeueMultiple(Span<T> items)
    {
        var count = 0;
        var head = _head;
        var tail = Volatile.Read(ref _tail);

        while (head != tail && count < items.Length)
        {
            items[count] = _buffer[head];
            _buffer[head] = null!;
            head = (head + 1) & _mask;
            count++;
        }

        if (count > 0)
        {
            Volatile.Write(ref _head, head);
        }

        return count;
    }

    public int Count => (Volatile.Read(ref _tail) - Volatile.Read(ref _head)) & (_buffer.Length - 1);
    public int Capacity => _buffer.Length;
    public bool IsEmpty => _head == Volatile.Read(ref _tail);
    public bool IsFull => ((Volatile.Read(ref _tail) + 1) & (_buffer.Length - 1)) == _head;
}

/// <summary>
/// High-performance channel for QUIC packet processing pipeline.
/// Uses System.Threading.Channels for lock-free async operations.
/// </summary>
public sealed class QuicChannel<TPacket>
{
    private readonly Channel<TPacket> _channel;
    private readonly ChannelWriter<TPacket> _writer;
    private readonly ChannelReader<TPacket> _reader;

    public QuicChannel(int capacity = 1024)
    {
        var options = new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = true
        };
        _channel = Channel.CreateBounded<TPacket>(options);
        _writer = _channel.Writer;
        _reader = _channel.Reader;
    }

    public QuicChannel(UnboundedChannelOptions options)
    {
        _channel = Channel.CreateUnbounded<TPacket>(options);
        _writer = _channel.Writer;
        _reader = _channel.Reader;
    }

    public QuicChannel() : this(new UnboundedChannelOptions 
    { 
        SingleReader = false, 
        SingleWriter = false,
        AllowSynchronousContinuations = true 
    }) { }

    public ChannelReader<TPacket> Reader => _reader;
    public ChannelWriter<TPacket> Writer => _writer;

    /// <summary>
    /// Tries to write without blocking.
    /// </summary>
    public bool TryWrite(TPacket item) => _writer.TryWrite(item);

    /// <summary>
    /// Writes asynchronously.
    /// </summary>
    public ValueTask WriteAsync(TPacket item, CancellationToken cancellationToken = default)
        => _writer.WriteAsync(item, cancellationToken);

    /// <summary>
    /// Tries to read without blocking.
    /// </summary>
    public bool TryRead(out TPacket item) => _reader.TryRead(out item);

    /// <summary>
    /// Reads asynchronously.
    /// </summary>
    public ValueTask<TPacket> ReadAsync(CancellationToken cancellationToken = default)
        => _reader.ReadAsync(cancellationToken);

    /// <summary>
    /// Completes the channel.
    /// </summary>
    public void Complete(Exception error = null!)
    {
        _writer.Complete(error);
    }

    /// <summary>
    /// Waits for all items to be processed.
    /// </summary>
    public async Task WaitToReadAsync(CancellationToken cancellationToken = default)
    {
        await _reader.WaitToReadAsync(cancellationToken);
    }

    public bool IsCompleted => _reader.Completion.IsCompleted;
    public Task Completion => _reader.Completion;
}

/// <summary>
/// Lock-free packet batch processor for QUIC.
/// Batches packets for efficient processing.
/// </summary>
public sealed class PacketBatchProcessor<TPacket>
{
    private readonly QuicChannel<TPacket> _inputChannel;
    private readonly Action<TPacket[]> _processBatch;
    private readonly int _batchSize;
    private readonly CancellationTokenSource _cts = new();
    private Task _processingTask;
    private bool _started;

    public PacketBatchProcessor(
        Action<TPacket[]> processBatch,
        int batchSize = 64,
        int channelCapacity = 1024)
    {
        _processBatch = processBatch ?? throw new ArgumentNullException(nameof(processBatch));
        _batchSize = Math.Max(1, batchSize);
        _inputChannel = new QuicChannel<TPacket>(channelCapacity);
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _processingTask = ProcessLoopAsync(_cts.Token);
    }

    public bool TryEnqueue(TPacket packet) => _inputChannel.Writer.TryWrite(packet);

    public ValueTask EnqueueAsync(TPacket packet, CancellationToken cancellationToken = default)
        => _inputChannel.Writer.WriteAsync(packet, cancellationToken);

    public async Task StopAsync()
    {
        _cts.Cancel();
        _inputChannel.Writer.Complete();
        if (_processingTask != null)
        {
            await _processingTask.ConfigureAwait(false);
        }
    }

    private async Task ProcessLoopAsync(CancellationToken cancellationToken)
    {
        var batch = new TPacket[_batchSize];
        int batchCount = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Try to fill batch
                batchCount = 0;
                
                // Get first item (blocking)
                if (!await _inputChannel.Reader.WaitToReadAsync(cancellationToken))
                    break;

                while (batchCount < _batchSize && _inputChannel.Reader.TryRead(out var packet))
                {
                    batch[batchCount++] = packet;
                }

                if (batchCount > 0)
                {
                    // Process batch
                    var batchSlice = batch.AsSpan(0, batchCount).ToArray();
                    _processBatch(batchSlice);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation
        }
        catch (Exception ex)
        {
            // Log error but continue
            Debug.WriteLine($"Packet batch processor error: {ex}");
        }
    }
}

/// <summary>
/// Lock-free atomic counter with efficient snapshots.
/// </summary>
public sealed class AtomicCounter
{
    private long _value;

    public AtomicCounter(long initialValue = 0)
    {
        _value = initialValue;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long Increment() => Interlocked.Increment(ref _value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long Decrement() => Interlocked.Decrement(ref _value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long Add(long value) => Interlocked.Add(ref _value, value);

    public long Value => Volatile.Read(ref _value);

    public long Exchange(long value) => Interlocked.Exchange(ref _value, value);

    public long CompareExchange(long value, long comparand) 
        => Interlocked.CompareExchange(ref _value, value, comparand);

    public long Reset() => Interlocked.Exchange(ref _value, 0);
}

/// <summary>
/// Lock-free atomic reference with snapshot capability.
/// </summary>
public sealed class AtomicReference<T> where T : class
{
    private T _value;

    public AtomicReference(T initialValue = null!)
    {
        _value = initialValue;
    }

    public T Value => Volatile.Read(ref _value);

    public T Exchange(T value) => Interlocked.Exchange(ref _value, value);

    public T CompareExchange(T value, T comparand) 
        => Interlocked.CompareExchange(ref _value, value, comparand);

    public T GetAndSet(T value) => Interlocked.Exchange(ref _value, value);
}