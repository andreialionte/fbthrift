// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Rocket;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

/// <summary>
/// Thread-local timer wheel for QUIC packet loss detection.
/// Provides O(1) timer scheduling with zero heap allocations.
/// Based on Hashed Wheel Timer algorithm optimized for QUIC loss detection.
/// </summary>
public sealed class TimerWheel : IDisposable
{
    // Wheel configuration - 512 slots at 1ms resolution = 512ms wheel
    private const int WheelSize = 512;
    private const int BucketCapacity = 16;
    private const int TickIntervalMs = 1;
    
    private readonly TimerWheelBucket[] _wheel;
    private int _currentTick = 0;
    private readonly Thread _wheelThread;
    private readonly CancellationTokenSource _cts = new();
    private long _tickCount = 0;
    
    // Callback for timeout events
    private readonly Action<PacketMetadata> _onTimeout;
    
    /// <summary>
    /// Initializes a new timer wheel for QUIC packet loss detection.
    /// </summary>
    /// <param name="onTimeout">Callback invoked when a packet timeout expires</param>
    public TimerWheel(Action<PacketMetadata> onTimeout)
    {
        _onTimeout = onTimeout ?? throw new ArgumentNullException(nameof(onTimeout));
        
        // Initialize wheel buckets
        for (int i = 0; i < WheelSize; i++)
        {
            _wheel[i] = new TimerWheelBucket(BucketCapacity);
        }
        
        // Start background ticker thread
        _wheelThread = new Thread(RunWheel) 
        { 
            IsBackground = true, 
            Name = "QUIC-TimerWheel" 
        };
        _wheelThread.Start();
    }
    
    /// <summary>
    /// Schedules a packet timeout in the timer wheel. O(1) operation.
    /// </summary>
    /// <param name="packet">Packet metadata to track</param>
    /// <param name="timeoutMs">Timeout in milliseconds</param>
    public void SchedulePacketTimeout(PacketMetadata packet, int timeoutMs)
    {
        if (timeoutMs <= 0) timeoutMs = 1;
        
        int bucketIndex = (_currentTick + timeoutMs) % WheelSize;
        _wheel[bucketIndex].Add(packet);
    }
    
    /// <summary>
    /// Cancels a previously scheduled packet timeout.
    /// </summary>
    public bool CancelPacketTimeout(PacketMetadata packet)
    {
        // Note: This is O(n) where n is bucket capacity (small constant)
        // For true O(1) cancellation, packet would need a reference to its bucket slot
        foreach (var bucket in _wheel)
        {
            if (bucket.TryRemove(packet))
            {
                return true;
            }
        }
        return false;
    }
    
    private void RunWheel()
    {
        var nextTick = DateTime.UtcNow + TimeSpan.FromMilliseconds(TickIntervalMs);
        
        while (!_cts.Token.IsCancellationRequested)
        {
            // Sleep until next tick
            var delay = nextTick - DateTime.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                Thread.Sleep(delay);
            }
            
            if (_cts.Token.IsCancellationRequested) break;
            
            Tick();
            nextTick += TimeSpan.FromMilliseconds(TickIntervalMs);
        }
    }
    
    private void Tick()
    {
        var bucket = _wheel[_currentTick];
        _currentTick = (_currentTick + 1) % WheelSize;
        _tickCount++;
        
        // Process all expired packets in this bucket
        while (bucket.TryDequeue(out var packet))
        {
            try
            {
                _onTimeout(packet);
            }
            catch
            {
                // Ignore callback exceptions to keep wheel running
            }
        }
    }
    
    /// <summary>
    /// Gets the current tick count (for monitoring/debugging).
    /// </summary>
    public long TickCount => _tickCount;
    
    /// <summary>
    /// Gets the current wheel position (0 to WheelSize-1).
    /// </summary>
    public int CurrentTick => _currentTick;
    
    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _wheelThread.Join(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Ignore join exceptions
        }
        _cts.Dispose();
    }
}

/// <summary>
/// Fixed-capacity bucket for timer wheel using circular array.
/// O(1) enqueue/dequeue with zero allocations after initialization.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public sealed class TimerWheelBucket
{
    private readonly PacketMetadata[] _items;
    private int _head;
    private int _tail;
    private int _count;
    
    public TimerWheelBucket(int capacity)
    {
        _items = new PacketMetadata[capacity];
        _head = 0;
        _tail = 0;
        _count = 0;
    }
    
    public int Count => _count;
    
    public bool TryEnqueue(PacketMetadata item)
    {
        if (_count >= _items.Length) return false;
        
        _items[_tail] = item;
        _tail = (_tail + 1) % _items.Length;
        _count++;
        return true;
    }
    
    public bool TryDequeue(out PacketMetadata item)
    {
        if (_count == 0)
        {
            item = default;
            return false;
        }
        
        item = _items[_head];
        _head = (_head + 1) % _items.Length;
        _count--;
        return true;
    }
    
    public bool TryRemove(PacketMetadata item)
    {
        // O(n) where n = capacity (small constant)
        for (int i = 0; i < _count; i++)
        {
            int index = (_head + i) % _items.Length;
            if (_items[index].Equals(item))
            {
                // Shift remaining items
                for (int j = i; j < _count - 1; j++)
                {
                    int src = (_head + j + 1) % _items.Length;
                    int dst = (_head + j) % _items.Length;
                    _items[dst] = _items[src];
                }
                _count--;
                _tail = (_tail - 1 + _items.Length) % _items.Length;
                return true;
            }
        }
        return false;
    }
    
    public void Add(PacketMetadata item)
    {
        if (!TryEnqueue(item))
        {
            // Bucket full - this shouldn't happen with proper sizing
            // In production, you'd want to handle this more gracefully
            throw new InvalidOperationException("Timer wheel bucket overflow");
        }
    }
}

/// <summary>
/// Packet metadata for timer wheel tracking.
/// </summary>
public sealed class PacketMetadata
{
    public long PacketNumber { get; set; }
    public long SendTime { get; set; }
    public long Bytes { get; set; }
    public bool IsRetransmission { get; set; }
    public bool InFlight { get; set; } = true;
    
    // For timeout callback
    public Action<PacketMetadata>? OnTimeout { get; set; }
}
