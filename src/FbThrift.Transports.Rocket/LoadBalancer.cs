// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Rocket;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Load balancer for distributing requests across multiple endpoints.
/// Supports multiple algorithms: RoundRobin, LeastConnections, LeastLatency, WeightedRoundRobin.
/// </summary>
public sealed class LoadBalancer : IDisposable
{
    private readonly ConcurrentDictionary<string, EndPointInfo> _endpoints = new();
    private readonly LoadBalancerOptions _options;
    private readonly ConcurrentDictionary<string, EndpointMetrics> _metrics = new();
    private readonly object _lock = new();
    private volatile int _roundRobinIndex = 0;
    private bool _disposed;
    
    public LoadBalancer(LoadBalancerOptions? options = null)
    {
        _options = options ?? new LoadBalancerOptions();
    }
    
    /// <summary>
    /// Adds an endpoint to the load balancer.
    /// </summary>
    public void AddEndpoint(EndPoint endpoint, double weight = 1.0, Dictionary<string, string>? metadata = null)
    {
        if (endpoint == null)
            throw new ArgumentNullException(nameof(endpoint));
        
        var key = endpoint.ToString()!;
        var info = new EndPointInfo
        {
            EndPoint = endpoint,
            Weight = weight,
            Metadata = metadata ?? new Dictionary<string, string>(),
            IsHealthy = true,
            AddedAt = DateTime.UtcNow
        };
        
        _endpoints.TryAdd(key, info);
        _metrics.TryAdd(key, new EndpointMetrics());
    }
    
    /// <summary>
    /// Removes an endpoint from the load balancer.
    /// </summary>
    public bool RemoveEndpoint(EndPoint endpoint)
    {
        if (endpoint == null) return false;
        
        var key = endpoint.ToString()!;
        return _endpoints.TryRemove(key, out _) && _metrics.TryRemove(key, out _);
    }
    
    /// <summary>
    /// Selects an endpoint using the configured load balancing algorithm.
    /// </summary>
    public EndPoint? SelectEndpoint(string? serviceName = null)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LoadBalancer));
        
        var healthyEndpoints = GetHealthyEndpoints();
        if (!healthyEndpoints.Any()) return null;
        
        return _options.Algorithm switch
        {
            LoadBalancingAlgorithm.RoundRobin => SelectRoundRobin(healthyEndpoints),
            LoadBalancingAlgorithm.LeastConnections => SelectLeastConnections(healthyEndpoints),
            LoadBalancingAlgorithm.LeastLatency => SelectLeastLatency(healthyEndpoints),
            LoadBalancingAlgorithm.WeightedRoundRobin => SelectWeightedRoundRobin(healthyEndpoints),
            LoadBalancingAlgorithm.Random => SelectRandom(healthyEndpoints),
            LoadBalancingAlgorithm.LeastRequests => SelectLeastRequests(healthyEndpoints),
            _ => SelectRoundRobin(healthyEndpoints)
        };
    }
    
    /// <summary>
    /// Marks an endpoint as healthy/unhealthy.
    /// </summary>
    public void SetEndpointHealth(EndPoint endpoint, bool healthy)
    {
        var key = endpoint.ToString()!;
        if (_endpoints.TryGetValue(key, out var info))
        {
            info.IsHealthy = healthy;
        }
    }
    
    /// <summary>
    /// Records a successful request for an endpoint.
    /// </summary>
    public void RecordSuccess(EndPoint endpoint, TimeSpan latency)
    {
        var key = endpoint.ToString()!;
        if (_metrics.TryGetValue(key, out var metrics))
        {
            metrics.RecordSuccess(latency);
        }
    }
    
    /// <summary>
    /// Records a failed request for an endpoint.
    /// </summary>
    public void RecordError(EndPoint endpoint, Exception exception)
    {
        var key = endpoint.ToString()!;
        if (_metrics.TryGetValue(key, out var metrics))
        {
            metrics.RecordError();
        }
    }
    
    /// <summary>
    /// Gets all registered endpoints.
    /// </summary>
    public IReadOnlyList<EndPointInfo> GetEndpoints()
    {
        return _endpoints.Values.ToList();
    }
    
    /// <summary>
    /// Gets healthy endpoints.
    /// </summary>
    public IReadOnlyList<EndPointInfo> GetHealthyEndpoints()
    {
        return _endpoints.Values.Where(e => e.IsHealthy).ToList();
    }
    
    /// <summary>
    /// Gets metrics for an endpoint.
    /// </summary>
    public EndpointMetrics? GetMetrics(EndPoint endpoint)
    {
        var key = endpoint.ToString()!;
        _metrics.TryGetValue(key, out var metrics);
        return metrics;
    }
    
    /// <summary>
    /// Gets all endpoint metrics.
    /// </summary>
    public IReadOnlyDictionary<string, EndpointMetrics> GetAllMetrics()
    {
        return _metrics;
    }
    
    private EndPoint? SelectRoundRobin(IEnumerable<EndPointInfo> endpoints)
    {
        var list = endpoints.ToList();
        if (!list.Any()) return null;
        
        var index = Interlocked.Increment(ref _roundRobinIndex) % list.Count;
        return list[index].EndPoint;
    }
    
    private EndPoint? SelectWeightedRoundRobin(IEnumerable<EndPointInfo> endpoints)
    {
        var list = endpoints.Where(e => e.Weight > 0).ToList();
        if (!list.Any()) return null;
        
        var totalWeight = list.Sum(e => e.Weight);
        var random = new Random().NextDouble() * totalWeight;
        
        double cumulative = 0;
        foreach (var endpoint in list)
        {
            cumulative += endpoint.Weight;
            if (random <= cumulative)
                return endpoint.EndPoint;
        }
        
        return list.Last().EndPoint;
    }
    
    private EndPoint? SelectLeastConnections(IEnumerable<EndPointInfo> endpoints)
    {
        return endpoints
            .OrderBy(e => _metrics.TryGetValue(e.EndPoint.ToString()!, out var m) ? m.ActiveConnections : 0)
            .FirstOrDefault()?.EndPoint;
    }
    
    private EndPoint? SelectLeastLatency(IEnumerable<EndPointInfo> endpoints)
    {
        return endpoints
            .Where(e => _metrics.TryGetValue(e.EndPoint.ToString()!, out var m) && m.AverageLatencyMs > 0)
            .OrderBy(e => _metrics[e.EndPoint.ToString()!].AverageLatencyMs)
            .FirstOrDefault()?.EndPoint;
    }
    
    private EndPoint? SelectRandom(IEnumerable<EndPointInfo> endpoints)
    {
        var list = endpoints.ToList();
        if (!list.Any()) return null;
        return list[new Random().Next(list.Count)].EndPoint;
    }
    
    private EndPoint? SelectLeastRequests(IEnumerable<EndPointInfo> endpoints)
    {
        return endpoints
            .OrderBy(e => _metrics.TryGetValue(e.EndPoint.ToString()!, out var m) ? m.RequestCount : 0)
            .FirstOrDefault()?.EndPoint;
    }
    
    private List<EndPointInfo> GetHealthyEndpoints()
    {
        return _endpoints.Values.Where(e => e.IsHealthy).ToList();
    }
    
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
        }
    }
}

/// <summary>
/// Load balancing algorithms.
/// </summary>
public enum LoadBalancingAlgorithm
{
    RoundRobin,
    WeightedRoundRobin,
    LeastConnections,
    LeastLatency,
    Random,
    LeastRequests
}

/// <summary>
/// Load balancer configuration options.
/// </summary>
public sealed class LoadBalancerOptions
{
    public LoadBalancingAlgorithm Algorithm { get; set; } = LoadBalancingAlgorithm.RoundRobin;
    public TimeSpan HealthCheckInterval { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan UnhealthyThreshold { get; set; } = TimeSpan.FromSeconds(10);
    public int MaxConsecutiveFailures { get; set; } = 3;
}

/// <summary>
/// Endpoint information.
/// </summary>
public sealed class EndPointInfo
{
    public EndPoint EndPoint { get; init; } = null!;
    public double Weight { get; set; } = 1.0;
    public bool IsHealthy { get; set; } = true;
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public Dictionary<string, string> Metadata { get; } = new();
}

/// <summary>
/// Endpoint metrics for load balancing decisions.
/// </summary>
public sealed class EndpointMetrics
{
    private long _requestCount;
    private long _errorCount;
    private long _totalLatencyMs;
    private long _activeConnections;
    private long _lastErrorTime;
    
    public long RequestCount => Interlocked.Read(ref _requestCount);
    public long ErrorCount => Interlocked.Read(ref _errorCount);
    public long ActiveConnections => Interlocked.Read(ref _activeConnections);
    public double AverageLatencyMs => _requestCount > 0 ? (double)_totalLatencyMs / _requestCount : 0;
    public double ErrorRate => _requestCount > 0 ? (double)_errorCount / _requestCount : 0;
    public DateTime? LastErrorTime => _lastErrorTime > 0 ? DateTime.FromBinary(_lastErrorTime) : null;
    
    public void RecordSuccess(TimeSpan latency)
    {
        Interlocked.Increment(ref _requestCount);
        Interlocked.Add(ref _totalLatencyMs, (long)latency.TotalMilliseconds);
    }
    
    public void RecordError()
    {
        Interlocked.Increment(ref _errorCount);
        Interlocked.Exchange(ref _lastErrorTime, DateTime.UtcNow.Ticks);
    }
    
    public void IncrementConnections()
    {
        Interlocked.Increment(ref _activeConnections);
    }
    
    public void DecrementConnections()
    {
        Interlocked.Decrement(ref _activeConnections);
    }
}
