// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Quic;

using System;
using System.Collections.Generic;
using System.Diagnostics;

/// <summary>
/// QUIC Stream Prioritization and Scheduling.
/// Based on RFC 9218 (Extensible Prioritization Scheme for HTTP) and QUIC stream scheduling.
/// </summary>
public sealed class QuicStreamScheduler
{
    // Priority constants
    private const int DefaultPriority = 128;     // Medium priority (0-255)
    private const int MaxPriority = 255;         // Highest priority
    private const int MinPriority = 0;           // Lowest priority
    private const int DefaultUrgency = 3;        // Default urgency (0-7)
    private const bool DefaultIncremental = false;

    // Scheduling constants
    private const int MaxStreamsPerRound = 16;   // Max streams to service per round
    private const long DefaultQuantum = 16384;   // Default scheduling quantum (bytes)

    // Stream priority tree (using urgency levels 0-7)
    private readonly List<PriorityBucket> _priorityBuckets = new(8);
    private readonly Dictionary<long, StreamPriority> _streamPriorities = new();
    private readonly object _lock = new();
    private long _totalQueuedBytes;
    private int _currentUrgency = 7; // Start with highest urgency

    /// <summary>
    /// Initializes a new instance of the <see cref="QuicStreamScheduler"/> class.
    /// </summary>
    public QuicStreamScheduler()
    {
        // Initialize priority buckets for each urgency level (0-7)
        for (int i = 0; i < 8; i++)
        {
            _priorityBuckets.Add(new PriorityBucket(i));
        }
    }

    /// <summary>
    /// Registers a stream with the scheduler.
    /// </summary>
    public void RegisterStream(long streamId, StreamPriority priority = null)
    {
        lock (_lock)
        {
            priority ??= new StreamPriority
            {
                StreamId = streamId,
                Priority = DefaultPriority,
                Urgency = DefaultUrgency,
                Incremental = DefaultIncremental
            };

            priority.StreamId = streamId;
            _streamPriorities[streamId] = priority;
            
            // Add to appropriate urgency bucket
            var bucket = _priorityBuckets[priority.Urgency];
            bucket.AddStream(streamId, priority);
        }
    }

    /// <summary>
    /// Updates the priority of a stream.
    /// </summary>
    public void UpdatePriority(long streamId, StreamPriority priority)
    {
        lock (_lock)
        {
            if (!_streamPriorities.TryGetValue(streamId, out var oldPriority))
            {
                return;
            }

            // Remove from old bucket
            var oldBucket = _priorityBuckets[oldPriority.Urgency];
            oldBucket.RemoveStream(streamId);

            // Update priority
            priority.StreamId = streamId;
            _streamPriorities[streamId] = priority;

            // Add to new bucket
            var newBucket = _priorityBuckets[priority.Urgency];
            newBucket.AddStream(streamId, priority);
        }
    }

    /// <summary>
    /// Unregisters a stream from the scheduler.
    /// </summary>
    public void UnregisterStream(long streamId)
    {
        lock (_lock)
        {
            if (_streamPriorities.TryGetValue(streamId, out var priority))
            {
                var bucket = _priorityBuckets[priority.Urgency];
                bucket.RemoveStream(streamId);
                _streamPriorities.Remove(streamId);
            }
        }
    }

    /// <summary>
    /// Gets the next stream to service based on priority and fairness.
    /// Returns stream IDs in priority order.
    /// </summary>
    public List<long> GetNextStreams(int maxStreams = MaxStreamsPerRound)
    {
        lock (_lock)
        {
            var result = new List<long>();
            var streamsServiced = 0;

            // Service streams from highest urgency to lowest
            for (int urgency = 7; urgency >= 0 && streamsServiced < maxStreams; urgency--)
            {
                var bucket = _priorityBuckets[urgency];
                var streams = bucket.GetNextStreams(maxStreams - streamsServiced);
                
                foreach (var streamId in streams)
                {
                    if (_streamPriorities.TryGetValue(streamId, out var priority))
                    {
                        // Check if stream has data to send
                        if (priority.HasDataToSend)
                        {
                            result.Add(streamId);
                            streamsServiced++;
                            
                            // Update stream's service state
                            priority.LastServiced = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        }
                    }
                }
            }

            return result;
        }
    }

    /// <summary>
    /// Marks a stream as having data ready to send.
    /// </summary>
    public void MarkDataReady(long streamId, long bytes)
    {
        lock (_lock)
        {
            if (_streamPriorities.TryGetValue(streamId, out var priority))
            {
                priority.HasDataToSend = true;
                priority.QueuedBytes = bytes;
                _totalQueuedBytes += bytes;
            }
        }
    }

    /// <summary>
    /// Marks a stream as having sent data.
    /// </summary>
    public void MarkDataSent(long streamId, long bytes)
    {
        lock (_lock)
        {
            if (_streamPriorities.TryGetValue(streamId, out var priority))
            {
                priority.QueuedBytes = Math.Max(0, priority.QueuedBytes - bytes);
                _totalQueuedBytes = Math.Max(0, _totalQueuedBytes - bytes);
                
                if (priority.QueuedBytes == 0)
                {
                    priority.HasDataToSend = false;
                }
            }
        }
    }

    /// <summary>
    /// Gets the priority of a stream.
    /// </summary>
    public bool TryGetPriority(long streamId, out StreamPriority priority)
    {
        lock (_lock)
        {
            return _streamPriorities.TryGetValue(streamId, out priority);
        }
    }

    /// <summary>
    /// Gets all streams sorted by priority.
    /// </summary>
    public List<StreamPriority> GetAllStreamsByPriority()
    {
        lock (_lock)
        {
            var result = new List<StreamPriority>(_streamPriorities.Values);
            result.Sort((a, b) => b.Urgency.CompareTo(a.Urgency)); // Highest urgency first
            return result;
        }
    }

    /// <summary>
    /// Gets scheduling statistics.
    /// </summary>
    public SchedulerStats GetStats()
    {
        lock (_lock)
        {
            return new SchedulerStats
            {
                TotalStreams = _streamPriorities.Count,
                TotalQueuedBytes = _totalQueuedBytes,
                StreamsPerUrgency = _priorityBuckets.ConvertAll(b => b.StreamCount)
            };
        }
    }

    /// <summary>
    /// Resets the scheduler.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            foreach (var bucket in _priorityBuckets)
            {
                bucket.Clear();
            }
            _streamPriorities.Clear();
            _totalQueuedBytes = 0;
        }
    }

    /// <summary>
    /// Priority bucket for a specific urgency level.
    /// </summary>
    private sealed class PriorityBucket
    {
        private readonly int _urgency;
        private readonly List<long> _streamIds = new();
        private int _roundRobinIndex = 0;

        public PriorityBucket(int urgency)
        {
            _urgency = urgency;
        }

        public int Urgency => _urgency;
        public int StreamCount => _streamIds.Count;

        public void AddStream(long streamId, StreamPriority priority)
        {
            if (!_streamIds.Contains(streamId))
            {
                _streamIds.Add(streamId);
            }
        }

        public void RemoveStream(long streamId)
        {
            _streamIds.Remove(streamId);
            if (_roundRobinIndex >= _streamIds.Count)
            {
                _roundRobinIndex = 0;
            }
        }

        public List<long> GetNextStreams(int maxCount)
        {
            var result = new List<long>();
            var count = Math.Min(maxCount, _streamIds.Count);
            
            for (int i = 0; i < count; i++)
            {
                var index = (_roundRobinIndex + i) % _streamIds.Count;
                result.Add(_streamIds[index]);
            }
            
            _roundRobinIndex = (_roundRobinIndex + count) % Math.Max(1, _streamIds.Count);
            return result;
        }

        public void Clear()
        {
            _streamIds.Clear();
            _roundRobinIndex = 0;
        }
    }

    /// <summary>
    /// Stream priority information.
    /// </summary>
    public sealed class StreamPriority
    {
        public long StreamId { get; set; }
        public int Priority { get; set; } = DefaultPriority;      // 0-255
        public int Urgency { get; set; } = DefaultUrgency;        // 0-7
        public bool Incremental { get; set; } = DefaultIncremental;
        public long QueuedBytes { get; set; }
        public bool HasDataToSend { get; set; }
        public long LastServiced { get; set; }
        public long TotalBytesSent { get; set; }
        public long TotalBytesReceived { get; set; }
    }

    /// <summary>
    /// Scheduler statistics.
    /// </summary>
    public sealed class SchedulerStats
    {
        public int TotalStreams { get; set; }
        public long TotalQueuedBytes { get; set; }
        public List<int> StreamsPerUrgency { get; set; }
    }
}
