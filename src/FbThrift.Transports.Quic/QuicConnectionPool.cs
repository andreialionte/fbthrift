// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Quic;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Connection pool for QUIC/Rocket transports.
/// Manages a pool of reusable connections with health checking and lifecycle management.
/// </summary>
public sealed class QuicConnectionPool : IDisposable
{
    private readonly QuicConnectionPoolOptions _options;
    private readonly ConcurrentBag<PooledConnection> _availableConnections = new();
    private readonly ConcurrentDictionary<string, PooledConnection> _inUseConnections = new();
    private readonly SemaphoreSlim _semaphore;
    private readonly Timer _cleanupTimer;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;
    
    public QuicConnectionPool(QuicConnectionPoolOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _semaphore = new SemaphoreSlim(options.MaxPoolSize, options.MaxPoolSize);
        
        // Start cleanup timer
        _cleanupTimer = new Timer(CleanupExpiredConnections, null, 
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }
    
    /// <summary>
    /// Acquires a connection from the pool.
    /// </summary>
    public async ValueTask<PooledConnection> AcquireAsync(
        EndPoint endpoint, 
        QuicTlsOptions tlsOptions,
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(QuicConnectionPool));
        
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        
        try
        {
            // Try to get an existing connection
            if (_availableConnections.TryTake(out var pooledConnection))
            {
                if (pooledConnection.IsHealthy && pooledConnection.MatchesEndpoint(endpoint))
                {
                    _inUseConnections.TryAdd(pooledConnection.ConnectionId, pooledConnection);
                    pooledConnection.MarkInUse();
                    return pooledConnection;
                }
                else
                {
                    // Connection unhealthy or wrong endpoint, dispose and create new
                    await pooledConnection.DisposeAsync().ConfigureAwait(false);
                }
            }
            
            // Create new connection
            var connection = await CreateConnectionAsync(endpoint, cancellationToken).ConfigureAwait(false);
            var pooled = new PooledConnection(connection, Guid.NewGuid().ToString());
            _inUseConnections.TryAdd(pooled.ConnectionId, pooled);
            pooled.MarkInUse();
            return pooled;
        }
        catch
        {
            _semaphore.Release();
            throw;
        }
    }
    
    /// <summary>
    /// Returns a connection to the pool.
    /// </summary>
    public void Release(PooledConnection connection)
    {
        if (_disposed || connection == null) return;
        
        if (_inUseConnections.TryRemove(connection.ConnectionId, out var pooled))
        {
            if (pooled.IsHealthy && !pooled.IsExpired)
            {
                pooled.MarkAvailable();
                _availableConnections.Add(pooled);
            }
            else
            {
                // Dispose unhealthy/expired connection
                _ = pooled.DisposeAsync();
            }
            
            _semaphore.Release();
        }
    }
    
    /// <summary>
    /// Gets pool statistics.
    /// </summary>
    public PoolStatistics GetStatistics()
    {
        return new PoolStatistics
        {
            AvailableConnections = _availableConnections.Count,
            InUseConnections = _inUseConnections.Count,
            MaxPoolSize = _options.MaxPoolSize,
            TotalCreated = _totalCreated,
            TotalDisposed = _totalDisposed
        };
    }
    
    private async ValueTask<QuicConnection> CreateConnectionAsync(EndPoint endpoint, CancellationToken cancellationToken)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        
        try
        {
            await socket.ConnectAsync(endpoint, CancellationToken.None).ConfigureAwait(false);
            
            var connection = new QuicConnection(socket, _options.TlsOptions);
            await connection.ConnectAsync(cancellationToken).ConfigureAwait(false);
            
            Interlocked.Increment(ref _totalCreated);
            return connection;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
    
    private void CleanupExpiredConnections(object? state)
    {
        if (_disposed) return;
        
        var expired = new List<PooledConnection>();
        
        foreach (var conn in _availableConnections)
        {
            if (conn.IsExpired)
            {
                expired.Add(conn);
            }
        }
        
        foreach (var conn in expired)
        {
            if (_availableConnections.TryTake(out var removed) && removed == conn)
            {
                _ = conn.DisposeAsync();
                Interlocked.Increment(ref _totalDisposed);
            }
        }
    }
    
    private long _totalCreated = 0;
    private long _totalDisposed = 0;
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        _cts.Cancel();
        _cleanupTimer.Dispose();
        _flushLock.Dispose();
        
        // Dispose all connections
        while (_availableConnections.TryTake(out var conn))
        {
            _ = conn.DisposeAsync();
        }
        
        foreach (var conn in _inUseConnections.Values)
        {
            _ = conn.DisposeAsync();
        }
        
        _semaphore.Dispose();
        _cts.Dispose();
    }
}

/// <summary>
/// Configuration options for connection pool.
/// </summary>
public sealed class QuicConnectionPoolOptions
{
    /// <summary>
    /// Maximum number of connections in the pool.
    /// </summary>
    public int MaxPoolSize { get; set; } = 100;
    
    /// <summary>
    /// Minimum number of connections to keep in pool.
    /// </summary>
    public int MinPoolSize { get; set; } = 0;
    
    /// <summary>
    /// Maximum time a connection can be idle before being evicted.
    /// </summary>
    public TimeSpan MaxIdleTime { get; set; } = TimeSpan.FromMinutes(5);
    
    /// <summary>
    /// Maximum lifetime of a connection.
    /// </summary>
    public TimeSpan MaxLifetime { get; set; } = TimeSpan.FromMinutes(30);
    
    /// <summary>
    /// Health check interval.
    /// </summary>
    public TimeSpan HealthCheckInterval { get; set; } = TimeSpan.FromSeconds(30);
    
    /// <summary>
    /// TLS options for connections.
    /// </summary>
    public QuicTlsOptions TlsOptions { get; set; } = new();
    
    /// <summary>
    /// Maximum time to wait for a connection from the pool.
    /// </summary>
    public TimeSpan AcquisitionTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Statistics for the connection pool.
/// </summary>
public readonly struct PoolStatistics
{
    public int AvailableConnections { get; init; }
    public int InUseConnections { get; init; }
    public int MaxPoolSize { get; init; }
    public long TotalCreated { get; init; }
    public long TotalDisposed { get; init; }
}
