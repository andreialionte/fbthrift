// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Quic;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;

/// <summary>
/// QUIC Connection Migration support.
/// Allows connections to survive network changes (IP/port).
/// Based on RFC 9000 Section 9 and RFC 9001 Section 18.
/// </summary>
public sealed class QuicConnectionMigration
{
    // Migration constants
    private const int MaxPaths = 8;                    // Maximum paths to track
    private const long PathValidationTimeoutMs = 3000; // Path validation timeout
    private const int MinPathValidationProbes = 2;     // Minimum probes for validation
    private const long IdleTimeoutMs = 30000;          // Idle timeout

    // Path state
    private readonly Dictionary<string, PathState> _paths = new();
    private string _currentPathId;
    private readonly object _lock = new();

    // Migration state
    private bool _migrationEnabled = true;
    private bool _preferNewPath = true;
    private long _lastMigrationTime;
    private int _migrationCount;

    // Statistics
    private long _pathValidationsStarted;
    private long _pathValidationsSucceeded;
    private long _pathValidationsFailed;
    private long _migrationsAttempted;
    private long _migrationsSucceeded;
    private long _migrationsFailed;

    /// <summary>
    /// Initializes a new instance of the <see cref="QuicConnectionMigration"/> class.
    /// </summary>
    public QuicConnectionMigration()
    {
        _currentPathId = "";
    }

    /// <summary>
    /// Gets whether connection migration is enabled.
    /// </summary>
    public bool MigrationEnabled => _migrationEnabled;

    /// <summary>
    /// Gets the current path ID.
    /// </summary>
    public string CurrentPathId => _currentPathId;

    /// <summary>
    /// Gets the number of active paths.
    /// </summary>
    public int ActivePathCount => _paths.Count;

    /// <summary>
    /// Gets migration statistics.
    /// </summary>
    public MigrationStats GetStats()
    {
        lock (_lock)
        {
            return new MigrationStats
            {
                PathValidationsStarted = _pathValidationsStarted,
                PathValidationsSucceeded = _pathValidationsSucceeded,
                PathValidationsFailed = _pathValidationsFailed,
                MigrationsAttempted = _migrationsAttempted,
                MigrationsSucceeded = _migrationsSucceeded,
                MigrationsFailed = _migrationsFailed,
                CurrentPathId = _currentPathId,
                ActivePaths = _paths.Count
            };
        }
    }

    /// <summary>
    /// Enables or disables connection migration.
    /// </summary>
    public void SetMigrationEnabled(bool enabled)
    {
        lock (_lock)
        {
            _migrationEnabled = enabled;
        }
    }

    /// <summary>
    /// Sets the initial path for a connection.
    /// </summary>
    public void SetInitialPath(IPEndPoint localEndPoint, IPEndPoint remoteEndPoint, long currentTime)
    {
        lock (_lock)
        {
            var pathId = CreatePathId(localEndPoint, remoteEndPoint);
            var path = new PathState
            {
                PathId = pathId,
                LocalEndPoint = localEndPoint,
                RemoteEndPoint = remoteEndPoint,
                State = PathState.PathStateEnum.Active,
                CreatedAt = currentTime,
                LastActivity = currentTime,
                Validated = true,
                Preferred = true
            };
            
            _paths[pathId] = path;
            _currentPathId = pathId;
        }
    }

    /// <summary>
    /// Initiates migration to a new path.
    /// </summary>
    public (bool Success, string PathId) InitiateMigration(
        IPEndPoint newLocalEndPoint,
        IPEndPoint newRemoteEndPoint,
        long currentTime)
    {
        lock (_lock)
        {
            if (!_migrationEnabled)
            {
                return (false, "Migration disabled");
            }
            
            _migrationsAttempted++;
            
            var pathId = CreatePathId(newLocalEndPoint, newRemoteEndPoint);
            
            // Check if path already exists
            if (_paths.ContainsKey(pathId))
            {
                var existingPath = _paths[pathId];
                if (existingPath.State == PathState.PathStateEnum.Validating)
                {
                    return (false, "Path already being validated");
                }
                if (existingPath.State == PathState.PathStateEnum.Active)
                {
                    return (true, pathId); // Already active
                }
            }
            
            // Create new path in validating state
            var newPath = new PathState
            {
                PathId = pathId,
                LocalEndPoint = newLocalEndPoint,
                RemoteEndPoint = newRemoteEndPoint,
                State = PathState.PathStateEnum.Validating,
                CreatedAt = currentTime,
                LastActivity = currentTime,
                Validated = false,
                Preferred = false,
                ValidationProbesSent = 0,
                ValidationProbesAcked = 0
            };
            
            _paths[pathId] = newPath;
            _pathValidationsStarted++;
            
            // Send PATH_CHALLENGE on new path
            // This would be handled by the caller
            
            return (true, pathId);
        }
    }

    /// <summary>
    /// Handles a PATH_CHALLENGE frame received on a path.
    /// </summary>
    public byte[] HandlePathChallenge(string pathId, ReadOnlyMemory<byte> challengeData)
    {
        lock (_lock)
        {
            if (!_paths.TryGetValue(pathId, out var path))
            {
                return null;
            }
            
            // Respond with PATH_RESPONSE containing the same data
            return challengeData.ToArray();
        }
    }

    /// <summary>
    /// Handles a PATH_RESPONSE frame received on a path.
    /// </summary>
    public (bool Success, bool PathValidated) HandlePathResponse(string pathId, ReadOnlyMemory<byte> responseData)
    {
        lock (_lock)
        {
            if (!_paths.TryGetValue(pathId, out var path))
            {
                return (false, false);
            }
            
            if (path.State != PathState.PathStateEnum.Validating)
            {
                return (false, false);
            }
            
            // Verify response matches challenge
            if (path.PendingChallenge != null && 
                responseData.Span.SequenceEqual(path.PendingChallenge.AsSpan()))
            {
                path.ValidationProbesAcked++;
                
                // Check if we have enough successful probes
                if (path.ValidationProbesAcked >= MinPathValidationProbes)
                {
                    path.State = PathState.PathStateEnum.Active;
                    path.Validated = true;
                    path.LastActivity = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    _pathValidationsSucceeded++;
                    
                    // Optionally switch to new path
                    if (_preferNewPath && pathId != _currentPathId)
                    {
                        SwitchToPath(pathId);
                        _migrationsSucceeded++;
                    }
                    
                    return (true, true);
                }
                
                return (true, false); // Probe succeeded but need more
            }
            
            return (false, false);
        }
    }

    /// <summary>
    /// Records a PATH_CHALLENGE sent on a path.
    /// </summary>
    public void RecordPathChallengeSent(string pathId, ReadOnlyMemory<byte> challengeData)
    {
        lock (_lock)
        {
            if (_paths.TryGetValue(pathId, out var path))
            {
                path.PendingChallenge = challengeData.ToArray();
                path.ValidationProbesSent++;
                path.LastActivity = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
        }
    }

    /// <summary>
    /// Records activity on a path.
    /// </summary>
    public void RecordPathActivity(string pathId, long currentTime)
    {
        lock (_lock)
        {
            if (_paths.TryGetValue(pathId, out var path))
            {
                path.LastActivity = currentTime;
            }
        }
    }

    /// <summary>
    /// Switches to a different path.
    /// </summary>
    public bool SwitchToPath(string pathId)
    {
        lock (_lock)
        {
            if (!_paths.TryGetValue(pathId, out var path))
            {
                return false;
            }
            
            if (!path.Validated)
            {
                return false;
            }
            
            // Mark old path as backup
            if (!string.IsNullOrEmpty(_currentPathId) && 
                _paths.TryGetValue(_currentPathId, out var oldPath))
            {
                oldPath.Preferred = false;
                oldPath.State = PathState.PathStateEnum.Backup;
            }
            
            // Switch to new path
            path.Preferred = true;
            path.State = PathState.PathStateEnum.Active;
            _currentPathId = pathId;
            
            return true;
        }
    }

    /// <summary>
    /// Gets the current path's endpoints.
    /// </summary>
    public (IPEndPoint Local, IPEndPoint Remote) GetCurrentEndPoints()
    {
        lock (_lock)
        {
            if (_paths.TryGetValue(_currentPathId, out var path))
            {
                return (path.LocalEndPoint, path.RemoteEndPoint);
            }
            return (null, null);
        }
    }

    /// <summary>
    /// Gets all active paths.
    /// </summary>
    public List<PathInfo> GetActivePaths()
    {
        lock (_lock)
        {
            var result = new List<PathInfo>();
            foreach (var kvp in _paths)
            {
                var path = kvp.Value;
                if (path.State != PathState.PathStateEnum.Failed)
                {
                    result.Add(new PathInfo
                    {
                        PathId = path.PathId,
                        LocalEndPoint = path.LocalEndPoint,
                        RemoteEndPoint = path.RemoteEndPoint,
                        State = path.State,
                        Validated = path.Validated,
                        Preferred = path.Preferred,
                        CreatedAt = path.CreatedAt,
                        LastActivity = path.LastActivity,
                        Rtt = path.SmoothedRtt
                    });
                }
            }
            return result;
        }
    }

    /// <summary>
    /// Validates all paths and removes expired ones.
    /// </summary>
    public void ValidatePaths(long currentTime)
    {
        lock (_lock)
        {
            var toRemove = new List<string>();
            
            foreach (var kvp in _paths)
            {
                var path = kvp.Value;
                
                // Check for validation timeout
                if (path.State == PathState.PathStateEnum.Validating)
                {
                    if (currentTime - path.CreatedAt > PathValidationTimeoutMs)
                    {
                        path.State = PathState.PathStateEnum.Failed;
                        _pathValidationsFailed++;
                        
                        if (path.PathId != _currentPathId)
                        {
                            toRemove.Add(path.PathId);
                        }
                    }
                }
                
                // Check for idle timeout on non-current paths
                if (path.PathId != _currentPathId && 
                    path.State != PathState.PathStateEnum.Failed &&
                    currentTime - path.LastActivity > IdleTimeoutMs)
                {
                    toRemove.Add(path.PathId);
                }
            }
            
            foreach (var pathId in toRemove)
            {
                _paths.Remove(pathId);
            }
        }
    }

    /// <summary>
    /// Handles a new local address (e.g., network change detected).
    /// </summary>
    public void OnLocalAddressChanged(IPEndPoint newLocalEndPoint, long currentTime)
    {
        lock (_lock)
        {
            var currentRemote = _paths.TryGetValue(_currentPathId, out var currentPath) 
                ? currentPath.RemoteEndPoint 
                : null;
            
            if (currentRemote != null)
            {
                // Initiate migration to new local address with same remote
                InitiateMigration(newLocalEndPoint, currentRemote, currentTime);
            }
        }
    }

    /// <summary>
    /// Updates RTT for a path.
    /// </summary>
    public void UpdatePathRtt(string pathId, long rtt)
    {
        lock (_lock)
        {
            if (_paths.TryGetValue(pathId, out var path))
            {
                if (path.SmoothedRtt == 0)
                {
                    path.SmoothedRtt = rtt;
                    path.Rttvar = rtt / 2;
                }
                else
                {
                    path.Rttvar = (long)((1 - 1.0/4) * path.Rttvar + (1.0/4) * Math.Abs(path.SmoothedRtt - rtt));
                    path.SmoothedRtt = (long)((1 - 1.0/8) * path.SmoothedRtt + (1.0/8) * rtt);
                }
            }
        }
    }

    /// <summary>
    /// Creates a unique path ID from endpoints.
    /// </summary>
    private static string CreatePathId(IPEndPoint local, IPEndPoint remote)
    {
        return $"{local.Address}:{local.Port}->{remote.Address}:{remote.Port}";
    }

    /// <summary>
    /// Migration statistics.
    /// </summary>
    public sealed class MigrationStats
    {
        public long PathValidationsStarted { get; set; }
        public long PathValidationsSucceeded { get; set; }
        public long PathValidationsFailed { get; set; }
        public long MigrationsAttempted { get; set; }
        public long MigrationsSucceeded { get; set; }
        public long MigrationsFailed { get; set; }
        public string CurrentPathId { get; set; }
        public int ActivePaths { get; set; }
    }

    /// <summary>
    /// Information about a path.
    /// </summary>
    public sealed class PathInfo
    {
        public string PathId { get; set; }
        public IPEndPoint LocalEndPoint { get; set; }
        public IPEndPoint RemoteEndPoint { get; set; }
        public PathState.PathStateEnum State { get; set; }
        public bool Validated { get; set; }
        public bool Preferred { get; set; }
        public long CreatedAt { get; set; }
        public long LastActivity { get; set; }
        public long Rtt { get; set; }
    }

    /// <summary>
    /// State of a network path.
    /// </summary>
    public sealed class PathState
    {
        public enum PathStateEnum
        {
            Validating,
            Active,
            Backup,
            Failed
        }

        public string PathId { get; set; }
        public IPEndPoint LocalEndPoint { get; set; }
        public IPEndPoint RemoteEndPoint { get; set; }
        public PathStateEnum State { get; set; }
        public bool Validated { get; set; }
        public bool Preferred { get; set; }
        public long CreatedAt { get; set; }
        public long LastActivity { get; set; }
        public long SmoothedRtt { get; set; }
        public long Rttvar { get; set; }
        public int ValidationProbesSent { get; set; }
        public int ValidationProbesAcked { get; set; }
        public byte[] PendingChallenge { get; set; }
    }
}
