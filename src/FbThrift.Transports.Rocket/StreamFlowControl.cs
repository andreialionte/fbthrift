// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Rocket;

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;

/// <summary>
/// Write completion tracker for QUIC stream frames.
/// Based on fbthrift commit cfb76a1 - integrates event-based write completion
/// notification into the rocket client and server pipelines.
/// </summary>
public sealed class WriteCompletionTracker : IDisposable
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource<bool>> _pendingWrites = new();
    private long _nextWriteId = 0;

    /// <summary>
    /// Registers a write operation and returns a task that completes when the write is acknowledged.
    /// </summary>
    /// <returns>A task that completes when the write is acknowledged by the peer.</returns>
    public Task RegisterWrite(long streamId, long bytes)
    {
        var writeId = Interlocked.Increment(ref _nextWriteId);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        
        var key = (streamId << 32) | (ulong)writeId;
        if (!_pendingWrites.TryAdd(key, tcs))
        {
            throw new InvalidOperationException($"Write ID {key} already exists");
        }
        
        return tcs.Task;
    }

    /// <summary>
    /// Completes a write operation with success.
    /// </summary>
    public void CompleteWrite(long streamId, long writeId)
    {
        var key = (streamId << 32) | (ulong)writeId;
        if (_pendingWrites.TryRemove(key, out var tcs))
        {
            tcs.TrySetResult(true);
        }
    }

    /// <summary>
    /// Completes a write operation with failure.
    /// </summary>
    public void FailWrite(long streamId, long writeId, Exception exception)
    {
        var key = (streamId << 32) | (ulong)writeId;
        if (_pendingWrites.TryRemove(key, out var tcs))
        {
            tcs.TrySetException(exception);
        }
    }

    /// <summary>
    /// Completes all pending writes with the given exception.
    /// </summary>
    public void FailAll(Exception exception)
    {
        foreach (var kvp in _pendingWrites)
        {
            kvp.Value.TrySetException(exception);
        }
        _pendingWrites.Clear();
    }

    public void Dispose()
    {
        FailAll(new ObjectDisposedException(nameof(WriteCompletionTracker)));
    }
}

/// <summary>
/// Stream flow control context for managing per-stream flow control state.
/// </summary>
public sealed class StreamFlowContext
{
    public long StreamId { get; }
    public long BytesSent { get; private set; }
    public long BytesReceived { get; private set; }
    public long Window { get; private set; }
    public long RemoteWindow { get; private set; }
    public bool FinSent { get; private set; }
    public bool FinReceived { get; private set; }
    public bool Closed { get; private set; }
    private readonly object _lock = new();

    public StreamFlowContext(long streamId, long initialWindow, long remoteWindow)
    {
        StreamId = streamId;
        Window = initialWindow;
        RemoteWindow = remoteWindow;
    }

    public bool CanSend(long bytes)
    {
        lock (_lock)
        {
            if (Closed || FinSent) return false;
            return BytesSent + bytes <= RemoteWindow;
        }
    }

    public bool TryConsumeWindow(long bytes)
    {
        lock (_lock)
        {
            if (BytesSent + bytes > RemoteWindow)
            {
                return false;
            }
            BytesSent += bytes;
            return true;
        }
    }

    public void AddBytesSent(long bytes)
    {
        lock (_lock)
        {
            BytesSent += bytes;
        }
    }

    public void AddBytesReceived(long bytes)
    {
        lock (_lock)
        {
            BytesReceived += bytes;
        }
    }

    public void UpdateRemoteWindow(long newWindow)
    {
        lock (_lock)
        {
            RemoteWindow = Math.Max(RemoteWindow, newWindow);
        }
    }

    public void UpdateLocalWindow(long newWindow)
    {
        lock (_lock)
        {
            Window = Math.Max(Window, newWindow);
        }
    }

    public void MarkFinSent()
    {
        lock (_lock)
        {
            FinSent = true;
        }
    }

    public void MarkFinReceived()
    {
        lock (_lock)
        {
            FinReceived = true;
        }
    }

    public void Close()
    {
        lock (_lock)
        {
            Closed = true;
        }
    }

    public bool IsClosed => Closed;
    public bool IsComplete => FinSent && FinReceived;
}
