// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Core.Memory;

using System;
using System.Buffers;
using System.Diagnostics;
using Microsoft.IO;

/// <summary>
/// Provides access to a shared <see cref="RecyclableMemoryStreamManager"/> instance for the application.
/// Using RecyclableMemoryStream reduces LOH allocations, memory fragmentation, and GC pressure
/// by pooling uniformly-sized buffers (128KB small blocks, 1MB+ large buffers).
/// </summary>
public static class RecyclableStreamManager
{
    private static readonly RecyclableMemoryStreamManager _manager = CreateManager();
    
    /// <summary>
    /// Gets the shared manager instance.
    /// </summary>
    public static RecyclableMemoryStreamManager Manager => _manager;
    
    /// <summary>
    /// Creates a new recyclable memory stream with default settings.
    /// </summary>
    public static RecyclableMemoryStream GetStream()
    {
        return _manager.GetStream();
    }
    
    /// <summary>
    /// Creates a new recyclable memory stream with a tag for debugging/monitoring.
    /// </summary>
    public static RecyclableMemoryStream GetStream(string tag)
    {
        return _manager.GetStream(Guid.NewGuid(), tag);
    }
    
    /// <summary>
    /// Creates a new recyclable memory stream with initial capacity.
    /// </summary>
    public static RecyclableMemoryStream GetStream(int initialCapacity)
    {
        return _manager.GetStream(Guid.NewGuid(), "", initialCapacity);
    }
    
    /// <summary>
    /// Creates a new recyclable memory stream with initial capacity and tag.
    /// </summary>
    public static RecyclableMemoryStream GetStream(string tag, int initialCapacity)
    {
        return _manager.GetStream(Guid.NewGuid(), tag, initialCapacity);
    }
    
    /// <summary>
    /// Creates a new recyclable memory stream from an existing buffer.
    /// </summary>
    public static RecyclableMemoryStream GetStream(byte[] buffer, int index, int count)
    {
        return _manager.GetStream(Guid.NewGuid(), "", buffer, index, count);
    }
    
    /// <summary>
    /// Creates a new recyclable memory stream from an existing buffer with tag.
    /// </summary>
    public static RecyclableMemoryStream GetStream(string tag, byte[] buffer, int index, int count)
    {
        return _manager.GetStream(Guid.NewGuid(), tag, buffer, index, count);
    }
    
    /// <summary>
    /// Creates a new recyclable memory stream from an existing buffer (zero-copy when possible).
    /// </summary>
    public static RecyclableMemoryStream GetStream(ReadOnlyMemory<byte> buffer)
    {
        return _manager.GetStream(Guid.NewGuid(), "", buffer.ToArray());
    }
    
    /// <summary>
    /// Creates a new recyclable memory stream from an existing buffer with tag.
    /// </summary>
    public static RecyclableMemoryStream GetStream(string tag, ReadOnlyMemory<byte> buffer)
    {
        return _manager.GetStream(Guid.NewGuid(), tag, buffer.ToArray());
    }
    
    /// <summary>
    /// Gets current pool statistics for monitoring.
    /// </summary>
    public static PoolStatistics GetStatistics()
    {
        return new PoolStatistics
        {
            SmallBlocksFree = _manager.SmallBlocksFree,
            SmallPoolFreeSize = _manager.SmallPoolFreeSize,
            SmallPoolInUseSize = _manager.SmallPoolInUseSize,
            LargeBuffersFree = _manager.LargeBuffersFree,
            LargePoolFreeSize = _manager.LargePoolFreeSize,
            LargePoolInUseSize = _manager.LargePoolInUseSize
        };
    }
    
    private static RecyclableMemoryStreamManager CreateManager()
    {
        var manager = new RecyclableMemoryStreamManager();
        
        return manager;
    }
}

/// <summary>
/// Statistics for the recyclable memory stream pools.
/// </summary>
public readonly struct PoolStatistics
{
    public long SmallBlocksFree { get; init; }
    public long SmallPoolFreeSize { get; init; }
    public long SmallPoolInUseSize { get; init; }
    public long LargeBuffersFree { get; init; }
    public long LargePoolFreeSize { get; init; }
    public long LargePoolInUseSize { get; init; }
    
    public override string ToString()
    {
        return $"Small: {SmallBlocksFree} blocks ({SmallPoolFreeSize:N0} free / {SmallPoolInUseSize:N0} in use), " +
               $"Large: {LargeBuffersFree} buffers ({LargePoolFreeSize:N0} free / {LargePoolInUseSize:N0} in use)";
    }
}