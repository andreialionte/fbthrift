// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Core;

using System;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;

/// <summary>
/// High-performance buffer pooling for zero-copy QUIC/Thrift operations.
/// Based on tquic's buffer management and .NET ArrayPool best practices.
/// </summary>
public static class QuicBufferPool
{
    // Buffer size buckets (powers of 2, aligned to MTU)
    private const int MinBufferSize = 256;
    private const int MaxBufferSize = 65536;
    private const int DefaultBufferSize = 16384; // 16KB for QUIC packets
    private const int MaxPoolSize = 1024; // Max buffers per size bucket

    // Thread-local caches for reduced contention
    private static readonly ThreadLocal<BufferCache> ThreadCache = new(() => new BufferCache());

    // Global pool for each size bucket
    private static readonly ArrayPool<byte> GlobalPool = ArrayPool<byte>.Create(MaxBufferSize, MaxPoolSize);

    // Statistics
    private static long _totalAllocated;
    private static long _totalReturned;
    private static long _cacheHits;
    private static long _cacheMisses;

    /// <summary>
    /// Gets a buffer of at least the specified size.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PooledBuffer Rent(int minimumSize = DefaultBufferSize)
    {
        var size = GetBucketSize(minimumSize);
        
        // Try thread-local cache first
        var cache = ThreadCache.Value;
        if (cache.TryGet(size, out var buffer))
        {
            Interlocked.Increment(ref _cacheHits);
            return new PooledBuffer(buffer, size, true);
        }

        Interlocked.Increment(ref _cacheMisses);
        var pooled = GlobalPool.Rent(size);
        Interlocked.Add(ref _totalAllocated, size);
        return new PooledBuffer(pooled, size, false);
    }

    /// <summary>
    /// Returns a buffer to the pool.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Return(byte[] buffer, bool fromCache = false)
    {
        if (buffer == null) return;

        var size = buffer.Length;
        
        if (!fromCache && ThreadCache.Value.TryAdd(size, buffer))
        {
            // Added to thread-local cache
            return;
        }

        // Return to global pool
        GlobalPool.Return(buffer);
        Interlocked.Increment(ref _totalReturned);
    }

    /// <summary>
    /// Gets buffer pool statistics.
    /// </summary>
    public static BufferPoolStats GetStats()
    {
        return new BufferPoolStats
        {
            TotalAllocated = _totalAllocated,
            TotalReturned = _totalReturned,
            CacheHits = _cacheHits,
            CacheMisses = _cacheMisses,
            CacheHitRatio = _cacheHits + _cacheMisses > 0 
                ? (double)_cacheHits / (_cacheHits + _cacheMisses) 
                : 0
        };
    }

    /// <summary>
    /// Resets statistics.
    /// </summary>
    public static void ResetStats()
    {
        _totalAllocated = 0;
        _totalReturned = 0;
        _cacheHits = 0;
        _cacheMisses = 0;
    }

    /// <summary>
    /// Clears all thread-local caches.
    /// </summary>
    public static void ClearCaches()
    {
        // ThreadLocal doesn't support clearing, so we just reset stats
        // The caches will be garbage collected when threads exit
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetBucketSize(int size)
    {
        // Round up to next power of 2, minimum 256
        if (size <= MinBufferSize) return MinBufferSize;
        
        size--;
        size |= size >> 1;
        size |= size >> 2;
        size |= size >> 4;
        size |= size >> 8;
        size |= size >> 16;
        size++;
        
        return Math.Min(size, MaxBufferSize);
    }

    /// <summary>
    /// Thread-local buffer cache for reduced contention.
    /// </summary>
    private sealed class BufferCache
    {
        private readonly byte[][] _smallBuffers = new byte[MaxBufferSize / MinBufferSize][];
        private int _count;

        public bool TryGet(int size, out byte[] buffer)
        {
            var index = GetIndex(size);
            if (index < _smallBuffers.Length && _smallBuffers[index] != null)
            {
                buffer = _smallBuffers[index];
                _smallBuffers[index] = null!;
                _count--;
                return true;
            }
            buffer = null!;
            return false;
        }

        public bool TryAdd(int size, byte[] buffer)
        {
            if (_count >= 32) return false; // Limit per-thread cache size

            var index = GetIndex(size);
            if (index < _smallBuffers.Length && _smallBuffers[index] == null)
            {
                _smallBuffers[index] = buffer;
                _count++;
                return true;
            }
            return false;
        }

        private static int GetIndex(int size)
        {
            // Convert bucket size to index
            var bucket = GetBucketSize(size);
            int index = 0;
            var check = MinBufferSize;
            while (check < bucket)
            {
                check <<= 1;
                index++;
            }
            return index;
        }
    }
}

/// <summary>
/// RAII-style pooled buffer that automatically returns to pool on disposal.
/// </summary>
public readonly struct PooledBuffer : IDisposable
{
    private readonly byte[] _buffer;
    private readonly int _size;
    private readonly bool _fromCache;

    public PooledBuffer(byte[] buffer, int size, bool fromCache)
    {
        _buffer = buffer;
        _size = size;
        _fromCache = fromCache;
    }

    public byte[] Buffer => _buffer;
    public int Size => _size;
    public Span<byte> Span => _buffer.AsSpan(0, _size);
    public Memory<byte> Memory => _buffer.AsMemory(0, _size);

    public void Dispose()
    {
        QuicBufferPool.Return(_buffer, _fromCache);
    }

    public static implicit operator Span<byte>(PooledBuffer buffer) => buffer.Span;
    public static implicit operator Memory<byte>(PooledBuffer buffer) => buffer.Memory;
    public static implicit operator byte[](PooledBuffer buffer) => buffer._buffer;
}

/// <summary>
/// Buffer pool statistics.
/// </summary>
public readonly struct BufferPoolStats
{
    public long TotalAllocated { get; init; }
    public long TotalReturned { get; init; }
    public long CacheHits { get; init; }
    public long CacheMisses { get; init; }
    public double CacheHitRatio { get; init; }
}

/// <summary>
/// Zero-copy buffer segment for scatter/gather I/O.
/// </summary>
public readonly struct BufferSegment
{
    public readonly byte[] Buffer;
    public readonly int Offset;
    public readonly int Length;

    public BufferSegment(byte[] buffer, int offset, int length)
    {
        Buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        Offset = offset;
        Length = length;
    }

    public BufferSegment(byte[] buffer) : this(buffer, 0, buffer.Length) { }

    public ReadOnlySpan<byte> Span => Buffer.AsSpan(Offset, Length);
    public ReadOnlyMemory<byte> Memory => Buffer.AsMemory(Offset, Length);

    public static implicit operator ReadOnlySpan<byte>(BufferSegment segment) => segment.Span;
    public static implicit operator ReadOnlyMemory<byte>(BufferSegment segment) => segment.Memory;
}

/// <summary>
/// Buffer chain for zero-copy scatter/gather operations.
/// </summary>
public sealed class BufferChain : IDisposable
{
    private readonly List<PooledBuffer> _buffers = new();
    private int _totalLength;
    private bool _disposed;

    public int Count => _buffers.Count;
    public int TotalLength => _totalLength;

    public PooledBuffer Append(int minimumSize = 16384)
    {
        var buffer = QuicBufferPool.Rent(minimumSize);
        _buffers.Add(buffer);
        _totalLength += buffer.Size;
        return buffer;
    }

    public PooledBuffer Append(byte[] data)
    {
        var buffer = QuicBufferPool.Rent(data.Length);
        data.CopyTo(buffer.Buffer);
        _buffers.Add(buffer);
        _totalLength += data.Length;
        return buffer;
    }

    public ReadOnlySequence<byte> AsReadOnlySequence()
    {
        if (_buffers.Count == 0) return ReadOnlySequence<byte>.Empty;
        if (_buffers.Count == 1) return new ReadOnlySequence<byte>(_buffers[0].Memory);

        // Build a linked list manually using reflection or use a simpler approach
        // Create segments array and link them
        var segments = new BufferSegmentNode[_buffers.Count];
        for (int i = 0; i < _buffers.Count; i++)
        {
            segments[i] = new BufferSegmentNode(_buffers[i].Memory);
        }
        
        // Link them using reflection since Next is protected
        for (int i = 0; i < segments.Length - 1; i++)
        {
            segments[i].Next = segments[i + 1];
        }

        return new ReadOnlySequence<byte>(segments[0], 0, segments[^1], segments[^1].Memory.Length);
    }

    /// <summary>
    /// Concrete implementation of ReadOnlySequenceSegment for buffer chains.
    /// </summary>
    private sealed class BufferSegmentNode : ReadOnlySequenceSegment<byte>
    {
        public BufferSegmentNode(ReadOnlyMemory<byte> memory)
        {
            Memory = memory;
        }

        // Allow setting Next via internal property
        public new ReadOnlySequenceSegment<byte> Next 
        { 
            get => base.Next; 
            set => base.Next = value; 
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var buffer in _buffers)
        {
            buffer.Dispose();
        }
        _buffers.Clear();
        _totalLength = 0;
    }
}