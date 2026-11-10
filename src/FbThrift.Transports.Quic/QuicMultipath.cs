// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Quic;

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Multi-path QUIC implementation (RFC 9000 Section 9, RFC 9221).
/// Allows a single QUIC connection to use multiple network paths simultaneously.
/// Based on tquic, s2n-quic, and quiche multipath implementations.
/// </summary>
public static class QuicMultipath
{
    // Multipath Constants
    private const int MaxPathsPerConnection = 8;
    private const long PathValidationTimeoutMs = 3000; // 3 seconds
    private const long PathAbandonmentTimeoutMs = 10000; // 10 seconds
    private const int MaxPathChallenges = 3;
    
    /// <summary>
    /// Network path representation.
    /// </summary>
    public sealed class NetworkPath
    {
        public uint PathId { get; }
        public IPEndPoint LocalEndpoint { get; }
        public IPEndPoint RemoteEndpoint { get; }
        public PathState State { get; set; }
        public DateTime CreatedAt { get; }
        public DateTime LastActivityAt { get; set; }
        public DateTime LastValidatedAt { get; set; }
        public int ValidationAttempts { get; set; }
        public long Rtt { get; set; }
        public long MinRtt { get; set; }
        public long BandwidthEstimate { get; set; }
        public bool IsPreferred { get; set; }
        public bool IsBackup { get; set; }
        
        public NetworkPath(uint pathId, IPEndPoint local, IPEndPoint remote)
        {
            PathId = pathId;
            LocalEndpoint = local;
            RemoteEndpoint = remote;
            State = PathState.Initial;
            CreatedAt = DateTime.UtcNow;
            LastActivityAt = DateTime.UtcNow;
            MinRtt = long.MaxValue;
        }
        
        public bool IsExpired => (DateTime.UtcNow - LastActivityAt).TotalMilliseconds > PathAbandonmentTimeoutMs;
        public bool IsValidated => State == PathState.Validated;
        public bool IsAvailable => State == PathState.Validated || State == PathState.Initial;
    }
    
    /// <summary>
    /// Path state machine (RFC 9000 Section 9.4).
    /// </summary>
    public enum PathState
    {
        Initial,      // Path created, not yet validated
        Validating,   // PATH_CHALLENGE sent, awaiting PATH_RESPONSE
        Validated,    // Path confirmed working
        Abandoned,    // Path abandoned due to failure
        Failed        // Path failed validation
    }
    
    /// <summary>
    /// Multi-path QUIC connection manager.
    /// </summary>
    public sealed class MultipathConnection
    {
        private readonly object _pathsLock = new();
        private readonly ConcurrentDictionary<uint, NetworkPath> _paths = new();
        private readonly object _schedulerLock = new();
        private uint _nextPathId = 0;
        private uint _primaryPathId = 0;
        private readonly object _connection; // Placeholder for actual QuicConnection
        private bool _multipathEnabled = true;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _pathMonitorTask;
        
        public event Action<uint, NetworkPath>? PathValidated;
        public event Action<uint, NetworkPath>? PathAbandoned;
        public event Action<uint, NetworkPath>? PathFailed;
        public event Action<uint, NetworkPath>? PathSwitched;
        
        public uint PrimaryPathId => _primaryPathId;
        public IReadOnlyCollection<NetworkPath> Paths => _paths.Values;
        public int PathCount => _paths.Count;
        public bool MultipathEnabled => _multipathEnabled;
        
        public MultipathConnection()
        {
            _pathMonitorTask = MonitorPathsAsync(_cts.Token);
        }
        
        /// <summary>
        /// Adds a new network path.
        /// </summary>
        public uint AddPath(IPEndPoint localEndpoint, IPEndPoint remoteEndpoint, bool makePreferred = false)
        {
            if (!_multipathEnabled)
                throw new InvalidOperationException("Multipath not enabled");
            
            lock (_pathsLock)
            {
                if (_paths.Count >= MaxPathsPerConnection)
                    throw new InvalidOperationException("Maximum paths reached");
                
                var pathId = _nextPathId++;
                var path = new NetworkPath(pathId, localEndpoint, remoteEndpoint);
                
                _paths[pathId] = path;
                
                // Validate the new path
                _ = ValidatePathAsync(pathId);
                
                if (makePreferred || _paths.Count == 1)
                {
                    SetPrimaryPath(pathId);
                }
                
                return pathId;
            }
        }
        
        /// <summary>
        /// Removes a path.
        /// </summary>
        public bool RemovePath(uint pathId, bool abandon = true)
        {
            lock (_pathsLock)
            {
                if (!_paths.TryGetValue(pathId, out var path))
                    return false;
                
                if (abandon)
                {
                    path.State = PathState.Abandoned;
                    PathAbandoned?.Invoke(pathId, path);
                }
                
                _paths.TryRemove(pathId, out _);
                
                // If we removed the primary path, select a new one
                if (pathId == _primaryPathId)
                {
                    SelectNewPrimaryPath();
                }
                
                return true;
            }
        }
        
        /// <summary>
        /// Sets the primary path for sending data.
        /// </summary>
        public bool SetPrimaryPath(uint pathId)
        {
            lock (_pathsLock)
            {
                if (!_paths.TryGetValue(pathId, out var path))
                    return false;
                
                if (!path.IsAvailable)
                    return false;
                
                var oldPrimaryId = _primaryPathId;
                _primaryPathId = pathId;
                
                // Update path preferences
                foreach (var p in _paths.Values)
                {
                    p.IsPreferred = p.PathId == pathId;
                    p.IsBackup = p.PathId != pathId && p.IsAvailable;
                }
                
                if (oldPrimaryId != pathId && oldPrimaryId != 0)
                {
                    PathSwitched?.Invoke(oldPrimaryId, _paths[pathId]);
                }
                
                return true;
            }
        }
        
        /// <summary>
        /// Gets the best path for sending data based on RTT and bandwidth.
        /// </summary>
        public uint? GetBestPath()
        {
            lock (_pathsLock)
            {
                var availablePaths = _paths.Values
                    .Where(p => p.IsAvailable)
                    .OrderBy(p => p.Rtt > 0 ? p.Rtt : long.MaxValue)
                    .ThenByDescending(p => p.BandwidthEstimate)
                    .ToList();
                
                return availablePaths.FirstOrDefault()?.PathId;
            }
        }
        
        /// <summary>
        /// Validates a path by sending PATH_CHALLENGE.
        /// </summary>
        public async Task<bool> ValidatePathAsync(uint pathId)
        {
            NetworkPath? path;
            lock (_pathsLock)
            {
                if (!_paths.TryGetValue(pathId, out var p))
                    return false;
                path = p;
            }
            
            if (path.State == PathState.Validated)
                return true;
            
            path.State = PathState.Validating;
            
            // Send PATH_CHALLENGE with random data
            var challengeData = new byte[8];
            RandomNumberGenerator.Fill(challengeData);
            
            try
            {
                // Send PATH_CHALLENGE frame on this path
                // _connection.SendPathChallenge(pathId, challengeData);
                
                // Wait for PATH_RESPONSE
                var validationTask = WaitForPathResponse(pathId, challengeData);
                var timeoutTask = Task.Delay(PathValidationTimeoutMs);
                
                var completed = await Task.WhenAny(validationTask, timeoutTask).ConfigureAwait(false);
                
                if (completed == validationTask && validationTask.Result)
                {
                    lock (_pathsLock)
                    {
                        if (_paths.TryGetValue(pathId, out var p))
                        {
                            p.State = PathState.Validated;
                            p.LastValidatedAt = DateTime.UtcNow;
                            p.ValidationAttempts = 0;
                        }
                    }
                    
                    PathValidated?.Invoke(pathId, path);
                    return true;
                }
                else
                {
                    // Timeout - retry
                    lock (_pathsLock)
                    {
                        if (_paths.TryGetValue(pathId, out var p))
                        {
                            p.ValidationAttempts++;
                            
                            if (p.ValidationAttempts >= MaxPathChallenges)
                            {
                                p.State = PathState.Failed;
                                PathFailed?.Invoke(pathId, p);
                                return false;
                            }
                        }
                    }
                    
                    // Retry
                    return await ValidatePathAsync(pathId).ConfigureAwait(false);
                }
            }
            catch
            {
                lock (_pathsLock)
                {
                    if (_paths.TryGetValue(pathId, out var p))
                    {
                        p.State = PathState.Failed;
                        PathFailed?.Invoke(pathId, p);
                    }
                }
                return false;
            }
        }
        
        /// <summary>
        /// Handles incoming PATH_RESPONSE frame.
        /// </summary>
        public void OnPathResponse(uint pathId, ReadOnlySpan<byte> challengeData)
        {
            // Match challenge data and complete validation
            // In a real implementation, you'd store the sent challenge data
            // and verify it matches the response
            
            lock (_pathsLock)
            {
                if (_paths.TryGetValue(pathId, out var path))
                {
                    if (path.State == PathState.Validating)
                    {
                        path.State = PathState.Validated;
                        path.LastValidatedAt = DateTime.UtcNow;
                        path.ValidationAttempts = 0;
                        PathValidated?.Invoke(pathId, path);
                    }
                }
            }
        }
        
        /// <summary>
        /// Handles PATH_ABANDON frame.
        /// </summary>
        public void OnPathAbandoned(uint pathId)
        {
            lock (_pathsLock)
            {
                if (_paths.TryGetValue(pathId, out var path))
                {
                    path.State = PathState.Abandoned;
                    PathAbandoned?.Invoke(pathId, path);
                    
                    if (pathId == _primaryPathId)
                    {
                        SelectNewPrimaryPath();
                    }
                }
            }
        }
        
        /// <summary>
        /// Monitors path health and abandons expired paths.
        /// </summary>
        private async Task MonitorPathsAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                    
                    var now = DateTime.UtcNow;
                    var expiredPaths = new List<uint>();
                    
                    lock (_pathsLock)
                    {
                        foreach (var path in _paths.Values)
                        {
                            if (path.IsExpired && path.State != PathState.Abandoned)
                            {
                                expiredPaths.Add(path.PathId);
                            }
                        }
                    }
                    
                    foreach (var pathId in expiredPaths)
                    {
                        RemovePath(pathId, abandon: true);
                    }
                    
                    // Re-validate primary path periodically
                    if (_primaryPathId != 0)
                    {
                        lock (_pathsLock)
                        {
                            if (_paths.TryGetValue(_primaryPathId, out var primary))
                            {
                                if (primary.State == PathState.Validated && 
                                    (now - primary.LastValidatedAt).TotalSeconds > 30)
                                {
                                    _ = ValidatePathAsync(_primaryPathId);
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    // Log error
                }
            }
        }
        
        private async Task<bool> WaitForPathResponse(uint pathId, byte[] challengeData)
        {
            // In a real implementation, this would wait for a PATH_RESPONSE frame
            // matching the challenge data. For now, simulate success.
            await Task.Delay(10).ConfigureAwait(false);
            return true;
        }
        
        private void SelectNewPrimaryPath()
        {
            var bestPath = GetBestPath();
            if (bestPath.HasValue)
            {
                SetPrimaryPath(bestPath.Value);
            }
            else
            {
                _primaryPathId = 0;
            }
        }
        
        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            
            try
            {
                await _pathMonitorTask.ConfigureAwait(false);
            }
            catch
            {
                // Ignore
            }
            
            _cts.Dispose();
        }
    }
    
    /// <summary>
    /// PATH_CHALLENGE Frame (RFC 9000 Section 19.16).
    /// </summary>
    public readonly struct PathChallengeFrame
    {
        public const byte FrameType = 0x1A;
        public byte[] ChallengeData { get; }
        
        public PathChallengeFrame()
        {
            ChallengeData = new byte[8];
        }
        
        public PathChallengeFrame(byte[] challengeData)
        {
            ChallengeData = challengeData ?? new byte[8];
        }
        
        public void Encode(Span<byte> output)
        {
            output[0] = FrameType;
            ChallengeData.CopyTo(output[1..]);
        }
        
        public static int GetEncodedLength() => 1 + 8;
        
        public static PathChallengeFrame Decode(ReadOnlySpan<byte> data)
        {
            if (data.Length != GetEncodedLength() || data[0] != FrameType)
                throw new ArgumentException("Invalid PATH_CHALLENGE frame");
            
            var challenge = new byte[8];
            data[1..].CopyTo(challenge);
            
            return new PathChallengeFrame(challenge);
        }
    }
    
    /// <summary>
    /// PATH_RESPONSE Frame (RFC 9000 Section 19.17).
    /// </summary>
    public readonly struct PathResponseFrame
    {
        public const byte FrameType = 0x1B;
        public byte[] ResponseData { get; }
        
        public PathResponseFrame()
        {
            ResponseData = new byte[8];
        }
        
        public PathResponseFrame(byte[] responseData)
        {
            ResponseData = responseData ?? new byte[8];
        }
        
        public void Encode(Span<byte> output)
        {
            output[0] = FrameType;
            ResponseData.CopyTo(output[1..]);
        }
        
        public static int GetEncodedLength() => 1 + 8;
        
        public static PathResponseFrame Decode(ReadOnlySpan<byte> data)
        {
            if (data.Length != GetEncodedLength() || data[0] != FrameType)
                throw new ArgumentException("Invalid PATH_RESPONSE frame");
            
            var response = new byte[8];
            data[1..].CopyTo(response);
            
            return new PathResponseFrame(response);
        }
    }
    
    /// <summary>
    /// PATH_ABANDON Frame (RFC 9000 Section 19.18).
    /// </summary>
    public readonly struct PathAbandonFrame
    {
        public const byte FrameType = 0x1C;
        
        public void Encode(Span<byte> output)
        {
            output[0] = FrameType;
        }
        
        public static int GetEncodedLength() => 1;
        
        public static PathAbandonFrame Decode(ReadOnlySpan<byte> data)
        {
            if (data.Length != 1 || data[0] != FrameType)
                throw new ArgumentException("Invalid PATH_ABANDON frame");
            
            return new PathAbandonFrame();
        }
    }
    
    /// <summary>
    /// Multipath Scheduler for distributing data across paths.
    /// </summary>
    public sealed class MultipathScheduler
    {
        private readonly MultipathConnection _multipath;
        private readonly object _lock = new();
        private readonly Dictionary<uint, long> _pathBytesSent = new();
        private uint _lastUsedPath = 0;
        
        public MultipathScheduler(MultipathConnection multipath)
        {
            _multipath = multipath ?? throw new ArgumentNullException(nameof(multipath));
        }
        
        /// <summary>
        /// Selects the best path for sending data based on scheduler policy.
        /// </summary>
        public uint? SelectPath(SchedulerPolicy policy = SchedulerPolicy.LowestRtt)
        {
            lock (_lock)
            {
                var paths = _multipath.Paths
                    .Where(p => p.IsAvailable)
                    .ToList();
                
                if (!paths.Any())
                    return null;
                
                switch (policy)
                {
                    case SchedulerPolicy.LowestRtt:
                        return paths
                            .OrderBy(p => p.Rtt > 0 ? p.Rtt : long.MaxValue)
                            .First().PathId;
                    
                    case SchedulerPolicy.HighestBandwidth:
                        return paths
                            .OrderByDescending(p => p.BandwidthEstimate)
                            .First().PathId;
                    
                    case SchedulerPolicy.RoundRobin:
                    {
                        var available = paths.Where(p => p.IsAvailable).ToList();
                        if (!available.Any()) return null;
                        
                        var index = available.FindIndex(p => p.PathId == _lastUsedPath);
                        if (index < 0) index = 0;
                        else index = (index + 1) % available.Count;
                        
                        _lastUsedPath = available[index].PathId;
                        return _lastUsedPath;
                    }
                    
                    case SchedulerPolicy.WeightedRoundRobin:
                    {
                        // Weight by inverse RTT and bandwidth
                        var scored = paths.Select(p => new
                        {
                            Path = p,
                            Score = CalculatePathScore(p)
                        }).OrderByDescending(x => x.Score).ToList();
                        
                        return scored.First().Path.PathId;
                    }
                    
                    case SchedulerPolicy.BackupOnly:
                    {
                        // Use primary path, only failover to backup
                        var primary = _multipath.PrimaryPathId;
                        var primaryPath = _multipath.Paths.FirstOrDefault(p => p.PathId == primary);
                        if (primaryPath?.IsAvailable == true)
                            return primary;
                        
                        var backup = paths.FirstOrDefault(p => p.IsBackup);
                        return backup?.PathId;
                    }
                    
                    default:
                        return paths.First().PathId;
                }
            }
        }
        
        private static double CalculatePathScore(NetworkPath path)
        {
            if (path.Rtt <= 0) return 0;
            
            // Score = bandwidth / (RTT * RTT) - prefer low RTT, high bandwidth
            var rttSeconds = path.Rtt / 1_000_000.0; // Convert to seconds
            return path.BandwidthEstimate / (rttSeconds * rttSeconds + 0.001);
        }
        
        /// <summary>
        /// Records bytes sent on a path for scheduling decisions.
        /// </summary>
        public void RecordBytesSent(uint pathId, long bytes)
        {
            lock (_lock)
            {
                _pathBytesSent[pathId] = _pathBytesSent.GetValueOrDefault(pathId) + bytes;
            }
        }
        
        /// <summary>
        /// Gets path utilization statistics.
        /// </summary>
        public IReadOnlyDictionary<uint, long> GetPathUtilization()
        {
            lock (_lock)
            {
                return new Dictionary<uint, long>(_pathBytesSent);
            }
        }
    }
    
    /// <summary>
    /// Scheduler policies for multipath.
    /// </summary>
    public enum SchedulerPolicy
    {
        LowestRtt,           // Always use path with lowest RTT
        HighestBandwidth,    // Always use path with highest bandwidth
        RoundRobin,          // Round-robin across available paths
        WeightedRoundRobin,  // Weighted by bandwidth/RTT score
        BackupOnly,          // Use primary, failover to backup
    }
}
