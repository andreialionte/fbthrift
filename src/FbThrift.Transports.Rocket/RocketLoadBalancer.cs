// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Rocket;

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using FbThrift.LoadBalancing;

/// <summary>
/// Rocket-specific load balancer that wraps the shared <see cref="LoadBalancing.LoadBalancer"/>
/// with Rocket transport-specific endpoint addressing (IPEndPoint).
/// </summary>
public sealed class RocketLoadBalancer : ILoadBalancingAdapter
{
    private readonly LoadBalancing.LoadBalancer _loadBalancer;

    /// <summary>
    /// Initializes a new instance of <see cref="RocketLoadBalancer"/> with default options.
    /// </summary>
    public RocketLoadBalancer() : this(new LoadBalancing.LoadBalancerOptions()) { }

    /// <summary>
    /// Initializes a new instance of <see cref="RocketLoadBalancer"/> with custom options.
    /// </summary>
    public RocketLoadBalancer(LoadBalancing.LoadBalancerOptions options)
    {
        _loadBalancer = new LoadBalancing.LoadBalancer(options);
    }

    /// <summary>
    /// Selects a healthy endpoint using the configured algorithm.
    /// </summary>
    public EndPointInfo? SelectEndpoint(string? serviceName = null)
    {
        return _loadBalancer.SelectEndpoint(serviceName);
    }

    /// <summary>
    /// Registers a new endpoint for load balancing.
    /// </summary>
    public void AddEndpoint(string address, double weight = 1.0, Dictionary<string, string>? metadata = null)
    {
        _loadBalancer.AddEndpoint(address, weight, metadata);
    }

    /// <summary>
    /// Removes an endpoint from the load balancer.
    /// </summary>
    public bool RemoveEndpoint(string address)
    {
        return _loadBalancer.RemoveEndpoint(address);
    }

    /// <summary>
    /// Marks an endpoint as healthy or unhealthy.
    /// </summary>
    public void SetEndpointHealth(string address, bool healthy)
    {
        _loadBalancer.SetEndpointHealth(address, healthy);
    }

    /// <summary>
    /// Records a successful request for metrics tracking.
    /// </summary>
    public void RecordSuccess(string address, TimeSpan latency)
    {
        _loadBalancer.RecordSuccess(address, latency);
    }

    /// <summary>
    /// Records a failed request for metrics tracking.
    /// </summary>
    public void RecordError(string address, Exception exception)
    {
        _loadBalancer.RecordError(address, exception);
    }

    /// <summary>
    /// Gets all registered endpoints (healthy and unhealthy).
    /// </summary>
    public IReadOnlyList<LoadBalancing.EndPointInfo> GetEndpoints()
    {
        return _loadBalancer.GetEndpoints();
    }

    /// <summary>
    /// Gets only healthy endpoints.
    /// </summary>
    public IReadOnlyList<LoadBalancing.EndPointInfo> GetHealthyEndpoints()
    {
        return _loadBalancer.GetHealthyEndpoints();
    }

    /// <summary>
    /// Gets metrics for a specific endpoint.
    /// </summary>
    public LoadBalancing.EndpointMetrics? GetMetrics(string address)
    {
        return _loadBalancer.GetMetrics(address);
    }

    /// <summary>
    /// Gets metrics for all endpoints.
    /// </summary>
    public IReadOnlyDictionary<string, LoadBalancing.EndpointMetrics> GetAllMetrics()
    {
        return _loadBalancer.GetAllMetrics();
    }

    /// <summary>
    /// Disposes the load balancer.
    /// </summary>
    public void Dispose()
    {
        _loadBalancer.Dispose();
    }
}

/// <summary>
/// Adapter interface for Rocket-specific load balancing operations.
/// </summary>
public interface ILoadBalancingAdapter : IDisposable
{
    EndPointInfo? SelectEndpoint(string? serviceName = null);
    void AddEndpoint(string address, double weight = 1.0, Dictionary<string, string>? metadata = null);
    bool RemoveEndpoint(string address);
    void SetEndpointHealth(string address, bool healthy);
    void RecordSuccess(string address, TimeSpan latency);
    void RecordError(string address, Exception exception);
    IReadOnlyList<LoadBalancing.EndPointInfo> GetEndpoints();
    IReadOnlyList<LoadBalancing.EndPointInfo> GetHealthyEndpoints();
    LoadBalancing.EndpointMetrics? GetMetrics(string address);
    IReadOnlyDictionary<string, LoadBalancing.EndpointMetrics> GetAllMetrics();
}
