// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.LoadBalancing;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Load balancing algorithms supported by <see cref="ILoadBalancer"/>.
/// </summary>
public enum LoadBalancingAlgorithm
{
    /// <summary>
    /// Round-robin selection across all healthy endpoints.
    /// </summary>
    RoundRobin,

    /// <summary>
    /// Weighted round-robin where endpoints with higher weights are selected more frequently.
    /// </summary>
    WeightedRoundRobin,

    /// <summary>
    /// Selects the endpoint with the fewest active connections.
    /// </summary>
    LeastConnections,

    /// <summary>
    /// Selects the endpoint with the lowest average latency.
    /// </summary>
    LeastLatency,

    /// <summary>
    /// Selects a random healthy endpoint.
    /// </summary>
    Random,

    /// <summary>
    /// Selects the endpoint with the fewest total requests served.
    /// </summary>
    LeastRequests,
}

/// <summary>
/// Configuration options for load balancing behavior.
/// </summary>
public sealed class LoadBalancerOptions
{
    /// <summary>
    /// Gets or sets the load balancing algorithm (default: <see cref="LoadBalancingAlgorithm.RoundRobin"/>).
    /// </summary>
    public LoadBalancingAlgorithm Algorithm { get; set; } = LoadBalancingAlgorithm.RoundRobin;

    /// <summary>
    /// Gets or sets the interval between health checks (default: 30 seconds).
    /// </summary>
    public TimeSpan HealthCheckInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the threshold for considering an endpoint unhealthy (default: 10 seconds).
    /// </summary>
    public TimeSpan UnhealthyThreshold { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or sets the maximum consecutive failures before marking an endpoint unhealthy (default: 3).
    /// </summary>
    public int MaxConsecutiveFailures { get; set; } = 3;

    /// <summary>
    /// Gets or sets whether to enable automatic failover on errors (default: true).
    /// </summary>
    public bool EnableFailover { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum number of failover attempts per request (default: 3).
    /// </summary>
    public int MaxFailoverAttempts { get; set; } = 3;
}

/// <summary>
/// Information about a registered load balancing endpoint.
/// </summary>
public sealed class EndPointInfo
{
    /// <summary>
    /// Gets the endpoint address (host:port).
    /// </summary>
    public string Address { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the weight for weighted round-robin (default: 1.0).
    /// </summary>
    public double Weight { get; set; } = 1.0;

    /// <summary>
    /// Gets or sets whether the endpoint is currently healthy (default: true).
    /// </summary>
    public bool IsHealthy { get; set; } = true;

    /// <summary>
    /// Gets or sets arbitrary metadata associated with the endpoint.
    /// </summary>
    public Dictionary<string, string> Metadata { get; } = new();

    /// <summary>
    /// Gets the timestamp when the endpoint was added.
    /// </summary>
    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets or sets the timestamp of the last health check.
    /// </summary>
    public DateTimeOffset? LastHealthCheck { get; set; }

    /// <summary>
    /// Gets or sets the number of consecutive failures.
    /// </summary>
    public int ConsecutiveFailures { get; set; }
}

/// <summary>
/// Metrics collected for a load balancing endpoint.
/// </summary>
public sealed class EndpointMetrics
{
    private long _requestCount;
    private long _errorCount;
    private long _totalLatencyMs;
    private long _activeConnections;

    /// <summary>
    /// Gets the total number of requests served by this endpoint.
    /// </summary>
    public long RequestCount => Interlocked.Read(ref _requestCount);

    /// <summary>
    /// Gets the total number of errors encountered by this endpoint.
    /// </summary>
    public long ErrorCount => Interlocked.Read(ref _errorCount);

    /// <summary>
    /// Gets the number of currently active connections on this endpoint.
    /// </summary>
    public long ActiveConnections => Interlocked.Read(ref _activeConnections);

    /// <summary>
    /// Gets the average latency in milliseconds (0 if no requests).
    /// </summary>
    public double AverageLatencyMs => _requestCount > 0 ? (double)_totalLatencyMs / _requestCount : 0;

    /// <summary>
    /// Gets the error rate (0-1, default 0).
    /// </summary>
    public double ErrorRate => _requestCount > 0 ? (double)_errorCount / _requestCount : 0;

    internal void RecordSuccess(TimeSpan latency)
    {
        Interlocked.Increment(ref _requestCount);
        Interlocked.Add(ref _totalLatencyMs, (long)latency.TotalMilliseconds);
    }

    internal void RecordError()
    {
        Interlocked.Increment(ref _errorCount);
    }

    internal void IncrementConnections()
    {
        Interlocked.Increment(ref _activeConnections);
    }

    internal void DecrementConnections()
    {
        Interlocked.Decrement(ref _activeConnections);
    }
}

/// <summary>
/// Abstraction for selecting endpoints across multiple service instances.
/// Follows the Steeltoe ILoadBalancer pattern with async resolution and statistics tracking.
/// Implementations should be thread-safe for concurrent use.
/// </summary>
public interface ILoadBalancer
{
    /// <summary>
    /// Selects a healthy endpoint for a request.
    /// </summary>
    /// <param name="serviceName">Optional service name for service-aware load balancing.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>The selected endpoint, or null if no healthy endpoints are available.</returns>
    ValueTask<EndPointInfo?> SelectEndpointAsync(string? serviceName = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records statistics for a completed request.
    /// </summary>
    /// <param name="address">The endpoint address that handled the request.</param>
    /// <param name="responseTime">The time taken for the request to complete.</param>
    /// <param name="exception">Any exception thrown during the request, or null if successful.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    ValueTask UpdateStatisticsAsync(string address, TimeSpan responseTime, Exception? exception, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a new endpoint for load balancing.
    /// </summary>
    /// <param name="address">The endpoint address (host:port).</param>
    /// <param name="weight">The weight for weighted algorithms (default: 1.0).</param>
    /// <param name="metadata">Optional metadata associated with the endpoint.</param>
    ValueTask AddEndpointAsync(string address, double weight = 1.0, Dictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes an endpoint from the load balancer.
    /// </summary>
    /// <param name="address">The endpoint address to remove.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>true if the endpoint was removed; false if not found.</returns>
    ValueTask<bool> RemoveEndpointAsync(string address, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks an endpoint as healthy or unhealthy.
    /// </summary>
    /// <param name="address">The endpoint address.</param>
    /// <param name="healthy">Whether the endpoint is healthy.</param>
    ValueTask SetEndpointHealthAsync(string address, bool healthy, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all registered endpoints (healthy and unhealthy).
    /// </summary>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    ValueTask<IReadOnlyList<EndPointInfo>> GetEndpointsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets only healthy endpoints.
    /// </summary>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    ValueTask<IReadOnlyList<EndPointInfo>> GetHealthyEndpointsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets metrics for a specific endpoint.
    /// </summary>
    /// <param name="address">The endpoint address.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    ValueTask<EndpointMetrics?> GetMetricsAsync(string address, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default implementation of <see cref="ILoadBalancer"/> supporting multiple algorithms.
/// Thread-safe for concurrent use. Follows Steeltoe patterns with async methods,
/// ArgumentNullException.ThrowIfNull validation, and ConcurrentDictionary for state.
/// </summary>
public sealed class LoadBalancer : ILoadBalancer, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, EndPointInfo> _endpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, EndpointMetrics> _metrics = new(StringComparer.OrdinalIgnoreCase);
    private readonly LoadBalancerOptions _options;
    private readonly object _lock = new();
    private volatile int _roundRobinIndex;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of <see cref="LoadBalancer"/> with default options.
    /// </summary>
    public LoadBalancer() : this(new LoadBalancerOptions()) { }

    /// <summary>
    /// Initializes a new instance of <see cref="LoadBalancer"/> with custom options.
    /// </summary>
    public LoadBalancer(LoadBalancerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public ValueTask<EndPointInfo?> SelectEndpointAsync(string? serviceName = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var healthyEndpoints = GetHealthyEndpointsInternal();
        if (!healthyEndpoints.Any())
            return ValueTask.FromResult<EndPointInfo?>(null);

        var endpoint = _options.Algorithm switch
        {
            LoadBalancingAlgorithm.RoundRobin => SelectRoundRobin(healthyEndpoints),
            LoadBalancingAlgorithm.WeightedRoundRobin => SelectWeightedRoundRobin(healthyEndpoints),
            LoadBalancingAlgorithm.LeastConnections => SelectLeastConnections(healthyEndpoints),
            LoadBalancingAlgorithm.LeastLatency => SelectLeastLatency(healthyEndpoints),
            LoadBalancingAlgorithm.Random => SelectRandom(healthyEndpoints),
            LoadBalancingAlgorithm.LeastRequests => SelectLeastRequests(healthyEndpoints),
            _ => SelectRoundRobin(healthyEndpoints)
        };

        return ValueTask.FromResult<EndPointInfo?>(endpoint);
    }

    /// <inheritdoc />
    public async ValueTask UpdateStatisticsAsync(string address, TimeSpan responseTime, Exception? exception, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(address))
            throw new ArgumentException("Endpoint address must not be empty.", nameof(address));

        if (_metrics.TryGetValue(address, out var metrics))
        {
            if (exception != null)
            {
                metrics.RecordError();

                // Track consecutive failures for disaster recovery
                if (_endpoints.TryGetValue(address, out var endpoint))
                {
                    endpoint.ConsecutiveFailures++;
                    endpoint.LastHealthCheck = DateTimeOffset.UtcNow;

                    if (endpoint.ConsecutiveFailures >= _options.MaxConsecutiveFailures)
                    {
                        endpoint.IsHealthy = false;
                    }
                }
            }
            else
            {
                metrics.RecordSuccess(responseTime);

                // Reset consecutive failures on success
                if (_endpoints.TryGetValue(address, out var endpoint))
                {
                    endpoint.ConsecutiveFailures = 0;
                    endpoint.IsHealthy = true;
                    endpoint.LastHealthCheck = DateTimeOffset.UtcNow;
                }
            }
        }
    }

    /// <inheritdoc />
    public ValueTask AddEndpointAsync(string address, double weight = 1.0, Dictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(address))
            throw new ArgumentException("Endpoint address must not be empty.", nameof(address));

        var info = new EndPointInfo
        {
            Address = address,
            Weight = weight,
            IsHealthy = true,
            AddedAt = DateTimeOffset.UtcNow
        };
        if (metadata != null)
        {
            foreach (var kvp in metadata)
                info.Metadata[kvp.Key] = kvp.Value;
        }

        _endpoints[address] = info;
        _metrics[address] = new EndpointMetrics();

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<bool> RemoveEndpointAsync(string address, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ValueTask.FromResult(_endpoints.TryRemove(address, out _) && _metrics.TryRemove(address, out _));
    }

    /// <inheritdoc />
    public ValueTask SetEndpointHealthAsync(string address, bool healthy, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_endpoints.TryGetValue(address, out var info))
        {
            info.IsHealthy = healthy;
            if (healthy)
                info.ConsecutiveFailures = 0;
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<EndPointInfo>> GetEndpointsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ValueTask.FromResult<IReadOnlyList<EndPointInfo>>(_endpoints.Values.ToList());
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<EndPointInfo>> GetHealthyEndpointsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ValueTask.FromResult<IReadOnlyList<EndPointInfo>>(GetHealthyEndpointsInternal());
    }

    /// <inheritdoc />
    public ValueTask<EndpointMetrics?> GetMetricsAsync(string address, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ValueTask.FromResult(_metrics.TryGetValue(address, out var metrics) ? metrics : null);
    }

    /// <summary>
    /// Disposes the load balancer and clears all endpoints.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _endpoints.Clear();
            _metrics.Clear();
        }
        return ValueTask.CompletedTask;
    }

    private List<EndPointInfo> GetHealthyEndpointsInternal()
    {
        lock (_lock)
        {
            return _endpoints.Values.Where(e => e.IsHealthy).ToList();
        }
    }

    private EndPointInfo SelectRoundRobin(List<EndPointInfo> endpoints)
    {
        var index = Interlocked.Increment(ref _roundRobinIndex) % endpoints.Count;
        return endpoints[index];
    }

    private EndPointInfo SelectWeightedRoundRobin(List<EndPointInfo> endpoints)
    {
        var list = endpoints.Where(e => e.Weight > 0).ToList();
        if (list.Count == 0)
            return endpoints[0];

        var totalWeight = list.Sum(e => e.Weight);
        var random = new Random().NextDouble() * totalWeight;

        double cumulative = 0;
        foreach (var endpoint in list)
        {
            cumulative += endpoint.Weight;
            if (random <= cumulative)
                return endpoint;
        }

        return list[^1];
    }

    private EndPointInfo SelectLeastConnections(List<EndPointInfo> endpoints)
    {
        return endpoints
            .OrderBy(e => GetMetricsInternal(e.Address)?.ActiveConnections ?? 0)
            .First();
    }

    private EndPointInfo SelectLeastLatency(List<EndPointInfo> endpoints)
    {
        return endpoints
            .Where(e => GetMetricsInternal(e.Address)?.AverageLatencyMs > 0)
            .OrderBy(e => GetMetricsInternal(e.Address)!.AverageLatencyMs)
            .FirstOrDefault() ?? endpoints[0];
    }

    private EndPointInfo SelectRandom(List<EndPointInfo> endpoints)
    {
        return endpoints[new Random().Next(endpoints.Count)];
    }

    private EndPointInfo SelectLeastRequests(List<EndPointInfo> endpoints)
    {
        return endpoints
            .OrderBy(e => GetMetricsInternal(e.Address)?.RequestCount ?? 0)
            .First();
    }

    private EndpointMetrics? GetMetricsInternal(string address)
    {
        return _metrics.TryGetValue(address, out var metrics) ? metrics : null;
    }
}
