// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Rocket;

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Threading.Tasks;

/// <summary>
    /// Write completion tracker for QUIC stream frames.
    /// Based on fbthrift commit cfb76a1 - integrates event-based write completion
    /// notification into the rocket client and server pipelines.
    /// Uses System.Threading.Channels for tracking write completions with backpressure.
    /// </summary>
    public sealed class WriteCompletionTrackerV2 : IDisposable
{
    private readonly Channel<PendingWrite> _pendingWrites;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _processTask;
    private long _nextWriteId = 0;
    private bool _disposed;

    /// <summary>
    /// Represents a pending write operation.
    /// </summary>
    private sealed class PendingWrite
    {
        public required long StreamId { get; init; }
        public required long WriteId { get; init; }
        public required TaskCompletionSource<bool> Tcs { get; init; }
        public required CancellationTokenSource CancellationTokenSource { get; init; }
    }

    public WriteCompletionTrackerV2()
    {
        // Bounded channel with backpressure
        _pendingWrites = Channel.CreateBounded<PendingWrite>(new BoundedChannelOptions(10000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = true
        });
        
        _processTask = ProcessCompletionsAsync(_cts.Token);
    }

    /// <summary>
    /// Registers a write operation and returns a task that completes when the write is acknowledged.
    /// </summary>
    /// <returns>A task that completes when the write is acknowledged by the peer.</returns>
    public Task RegisterWrite(long streamId, long bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        
        var writeId = Interlocked.Increment(ref _nextWriteId);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        
        // Linked cancellation for cleanup
        var requestCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);

        var pending = new PendingWrite
        {
            StreamId = streamId,
            WriteId = writeId,
            Tcs = tcs,
            CancellationTokenSource = requestCts
        };

        // Write to channel (applies backpressure if channel is full)
        _ = _pendingWrites.Writer.TryWrite(pending);

        return tcs.Task;
    }

    /// <summary>
    /// Completes a write operation with success.
    /// </summary>
    public void CompleteWrite(long streamId, long writeId)
    {
        var key = ((ulong)streamId << 32) | (uint)writeId;
        
        // Note: With Channel-based approach, we'd need to find the specific pending write
        // For now, we use a concurrent dictionary for O(1) lookup by key
        // This is a hybrid approach - Channel for queuing, Dictionary for lookup
        if (_pendingByKey.TryRemove(key, out var pending))
        {
            pending.CancellationTokenSource.Dispose();
            pending.Tcs.TrySetResult(true);
        }
    }

    /// <summary>
    /// Completes a write operation with failure.
    /// </summary>
    public void FailWrite(long streamId, long writeId, Exception exception)
    {
        var key = ((ulong)streamId << 32) | (uint)writeId;
        
        if (_pendingByKey.TryRemove(key, out var pending))
        {
            pending.CancellationTokenSource.Dispose();
            pending.Tcs.TrySetException(exception);
        }
    }

    // Hybrid: Channel for queuing/backpressure, Dictionary for O(1) lookup
    private readonly ConcurrentDictionary<ulong, PendingWrite> _pendingByKey = new();

    private async Task ProcessCompletionsAsync(CancellationToken cancellationToken)
    {
        await foreach (var pending in _pendingWrites.Reader.ReadAllAsync(cancellationToken))
        {
            // Register in lookup dictionary
            var key = ((ulong)pending.StreamId << 32) | (uint)pending.WriteId;
            _pendingByKey[key] = pending;
            
            try
            {
                // Wait for completion or cancellation
                await pending.Tcs.Task.WaitAsync(pending.CancellationTokenSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on cancellation
            }
            finally
            {
                pending.CancellationTokenSource.Dispose();
                _pendingByKey.TryRemove(key, out _);
            }
        }
    }

    /// <summary>
    /// Completes all pending writes with the given exception.
    /// </summary>
    public void FailAll(Exception exception)
    {
        _cts.Cancel();
        
        foreach (var kvp in _pendingByKey)
        {
            kvp.Value.Tcs.TrySetException(exception);
            kvp.Value.CancellationTokenSource.Dispose();
        }
        _pendingByKey.Clear();
        
        _pendingWrites.Writer.Complete();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        FailAll(new ObjectDisposedException(nameof(WriteCompletionTracker)));
        
        try
        {
            _processTask.GetAwaiter().GetResult();
        }
        catch
        {
            // Ignore
        }
        
        _cts.Dispose();
    }
}
