// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Rocket;

using System;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Represents a pooled QUIC connection with health tracking.
/// </summary>
public sealed class PooledConnection : IDisposable, IAsyncDisposable
{
    private readonly QuicConnection _connection;
    private readonly string _connectionId;
    private readonly DateTime _createdAt;
    private DateTime _lastUsed;
    private volatile bool _inUse;
    private volatile bool _healthy = true;
    private volatile bool _disposed;
    private readonly object _lock = new();
    
    public string ConnectionId => _connectionId;
    public DateTime CreatedAt => _createdAt;
    public DateTime LastUsed => _lastUsed;
    public bool InUse => _inUse;
    public bool IsHealthy => _healthy && !_disposed;
    public bool IsExpired => DateTime.UtcNow - _createdAt > TimeSpan.FromMinutes(30);
    
    public QuicConnection Connection => _connection;
    
    public PooledConnection(QuicConnection connection, string connectionId)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _connectionId = connectionId ?? Guid.NewGuid().ToString();
        _createdAt = DateTime.UtcNow;
        _lastUsed = DateTime.UtcNow;
    }
    
    public bool IsHealthy => _healthy && !_disposed;
    
    public bool MatchesEndpoint(EndPoint endpoint)
    {
        // In a full implementation, check if the connection's remote endpoint matches
        return true; // Simplified for now
    }
    
    public void MarkInUse()
    {
        lock (_lock)
        {
            _inUse = true;
            _lastUsed = DateTime.UtcNow;
        }
    }
    
    public void MarkAvailable()
    {
        lock (_lock)
        {
            _inUse = false;
            _lastUsed = DateTime.UtcNow;
        }
    }
    
    public void MarkUnhealthy()
    {
        _healthy = false;
    }
    
    public bool IsHealthy => _healthy && !_disposed;
    
    public bool IsExpired => DateTime.UtcNow - _createdAt > TimeSpan.FromMinutes(30);
    
    public bool IsInUse => _inUse;
    
    public EndPoint RemoteEndPoint => _connection.RemoteEndPoint;
    
    public EndPoint LocalEndPoint => _connection.LocalEndPoint;
    
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _connection?.Dispose();
        }
    }
    
    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_connection != null)
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
