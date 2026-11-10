// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Rocket;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Service Router - routes requests to appropriate service handlers.
/// Based on fbthrift's service routing mechanism.
/// </summary>
public sealed class ServiceRouter : IDisposable
{
    private readonly ConcurrentDictionary<string, IServiceHandler> _handlers = new();
    private readonly ConcurrentDictionary<string, ServiceMetadata> _services = new();
    private readonly ConcurrentDictionary<string, ServiceMetrics> _metrics = new();
    private readonly ReaderWriterLockSlim _lock = new();
    private bool _disposed;
    
    /// <summary>
    /// Registers a service handler.
    /// </summary>
    public void RegisterService(string serviceName, IServiceHandler handler, ServiceMetadata? metadata = null)
    {
        if (string.IsNullOrEmpty(serviceName))
            throw new ArgumentException("Service name cannot be null or empty", nameof(serviceName));
        
        if (handler == null)
            throw new ArgumentNullException(nameof(handler));
        
        _handlers.TryAdd(serviceName, handler);
        
        if (metadata != null)
        {
            _services.TryAdd(serviceName, metadata);
        }
        
        _metrics.TryAdd(serviceName, new ServiceMetrics());
    }
    
    /// <summary>
    /// Unregisters a service handler.
    /// </summary>
    public bool UnregisterService(string serviceName)
    {
        if (string.IsNullOrEmpty(serviceName))
            return false;
        
        _handlers.TryRemove(serviceName, out _);
        _services.TryRemove(serviceName, out _);
        _metrics.TryRemove(serviceName, out _);
        return true;
    }
    
    /// <summary>
    /// Routes a request to the appropriate service handler.
    /// </summary>
    public async ValueTask<byte[]> RouteRequestAsync(
        string serviceName, 
        string methodName, 
        ReadOnlyMemory<byte> requestData, 
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ServiceRouter));
        
        if (!_handlers.TryGetValue(serviceName, out var handler))
        {
            throw new KeyNotFoundException($"Service '{serviceName}' not found");
        }
        
        var metrics = _metrics.GetOrAdd(serviceName, _ => new ServiceMetrics());
        var stopwatch = Stopwatch.StartNew();
        
        try
        {
            var result = await handler.HandleRequestAsync(methodName, requestData, cancellationToken).ConfigureAwait(false);
            
            metrics.RecordSuccess(stopwatch.Elapsed);
            return result;
        }
        catch (Exception ex)
        {
            metrics.RecordError(stopwatch.Elapsed);
            throw;
        }
    }
    
    /// <summary>
    /// Gets service metadata.
    /// </summary>
    public ServiceMetadata? GetServiceMetadata(string serviceName)
    {
        _services.TryGetValue(serviceName, out var metadata);
        return metadata;
    }
    
    /// <summary>
    /// Gets service metrics.
    /// </summary>
    public ServiceMetrics? GetServiceMetrics(string serviceName)
    {
        _metrics.TryGetValue(serviceName, out var metrics);
        return metrics;
    }
    
    /// <summary>
    /// Gets all registered service names.
    /// </summary>
    public IReadOnlyList<string> GetServiceNames()
    {
        return _handlers.Keys.ToList();
    }
    
    /// <summary>
    /// Checks if a service is registered.
    /// </summary>
    public bool HasService(string serviceName)
    {
        return _handlers.ContainsKey(serviceName);
    }
    
    /// <summary>
    /// Gets metrics for all services.
    /// </summary>
    public IReadOnlyDictionary<string, ServiceMetrics> GetAllMetrics()
    {
        return _metrics;
    }
    
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            // Dispose handlers if they implement IDisposable
            foreach (var handler in _handlers.Values)
            {
                if (handler is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            _handlers.Clear();
            _services.Clear();
            _metrics.Clear();
        }
    }
}

/// <summary>
/// Service handler interface.
/// </summary>
public interface IServiceHandler
{
    /// <summary>
    /// Handles a request and returns the response.
    /// </summary>
    ValueTask<byte[]> HandleRequestAsync(
        string methodName, 
        ReadOnlyMemory<byte> requestData, 
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Service metadata.
/// </summary>
public sealed class ServiceMetadata
{
    public string ServiceName { get; init; } = string.Empty;
    public string Version { get; init; } = "1.0.0";
    public string Description { get; init; } = string.Empty;
    public Dictionary<string, string> Metadata { get; } = new();
    public List<string> SupportedMethods { get; } = new();
    public bool RequiresAuthentication { get; init; } = false;
    public Dictionary<string, string> Annotations { get; } = new();
}

/// <summary>
/// Service metrics for monitoring.
/// </summary>
public sealed class ServiceMetrics
{
    private long _requestCount;
    private long _errorCount;
    private long _totalLatencyMs;
    private long _lastErrorTime;
    
    public long RequestCount => Interlocked.Read(ref _requestCount);
    public long ErrorCount => Interlocked.Read(ref _errorCount);
    public double AverageLatencyMs => _requestCount > 0 ? (double)_totalLatencyMs / _requestCount : 0;
    public double ErrorRate => _requestCount > 0 ? (double)_errorCount / _requestCount : 0;
    public DateTime? LastErrorTime => _lastErrorTime > 0 ? DateTime.FromBinary(_lastErrorTime) : null;
    
    public void RecordSuccess(TimeSpan latency)
    {
        Interlocked.Increment(ref _requestCount);
        Interlocked.Add(ref _totalLatencyMs, (long)latency.TotalMilliseconds);
    }
    
    public void RecordError(TimeSpan latency)
    {
        Interlocked.Increment(ref _errorCount);
        Interlocked.Add(ref _totalLatencyMs, (long)latency.TotalMilliseconds);
        Interlocked.Exchange(ref _lastErrorTime, DateTime.UtcNow.Ticks);
    }
    
    public void Reset()
    {
        Interlocked.Exchange(ref _requestCount, 0);
        Interlocked.Exchange(ref _errorCount, 0);
        Interlocked.Exchange(ref _totalLatencyMs, 0);
        Interlocked.Exchange(ref _lastErrorTime, 0);
    }
}
