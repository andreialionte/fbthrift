// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Client;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FbThrift.Core.Abstractions;
using FbThrift.Core.FBThrift;
using FbThrift.LoadBalancing;

/// <summary>
/// Manages a pool of <see cref="ThriftClient"/> instances across multiple endpoints,
/// integrating with <see cref="ILoadBalancer"/> for endpoint selection and failover.
/// Provides lifecycle management (start/stop/dispose) for all managed clients.
/// </summary>
public sealed class ThriftClientManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ManagedClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILoadBalancer _loadBalancer;
    private readonly Func<string, ThriftClientOptions, ThriftClient> _clientFactory;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of <see cref="ThriftClientManager"/>.
    /// </summary>
    /// <param name="loadBalancer">The load balancer used for endpoint selection.</param>
    /// <param name="clientFactory">Factory to create ThriftClient instances for endpoints.</param>
    public ThriftClientManager(ILoadBalancer loadBalancer, Func<string, ThriftClientOptions, ThriftClient> clientFactory)
    {
        _loadBalancer = loadBalancer ?? throw new ArgumentNullException(nameof(loadBalancer));
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    }

    /// <summary>
    /// Registers an endpoint and creates a ThriftClient for it.
    /// </summary>
    /// <param name="address">The endpoint address (host:port).</param>
    /// <param name="options">Thrift client options for this endpoint.</param>
    /// <param name="weight">Load balancing weight for this endpoint.</param>
    public void AddEndpoint(string address, ThriftClientOptions? options = null, double weight = 1.0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(address))
            throw new ArgumentException("Endpoint address must not be empty.", nameof(address));

        var clientOptions = options ?? new ThriftClientOptions();
        var client = _clientFactory(address, clientOptions);

        var managed = new ManagedClient
        {
            Address = address,
            Client = client,
            CreatedAt = DateTimeOffset.UtcNow,
            IsHealthy = true
        };

        _clients[address] = managed;
        _loadBalancer.AddEndpoint(address, weight);
    }

    /// <summary>
    /// Removes an endpoint and disposes its ThriftClient.
    /// </summary>
    /// <param name="address">The endpoint address to remove.</param>
    /// <returns>true if the endpoint was removed.</returns>
    public bool RemoveEndpoint(string address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_clients.TryRemove(address, out var managed))
        {
            _ = managed.Client.DisposeAsync();
            _loadBalancer.RemoveEndpoint(address);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Selects a healthy endpoint and returns its ThriftClient.
    /// </summary>
    /// <param name="serviceName">Optional service name for service-aware load balancing.</param>
    /// <returns>The ThriftClient for the selected endpoint, or null if no healthy endpoints.</returns>
    public ThriftClient? SelectClient(string? serviceName = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var endpoint = _loadBalancer.SelectEndpoint(serviceName);
        if (endpoint == null || !_clients.TryGetValue(endpoint.Address, out var managed))
            return null;

        return managed.Client;
    }

    /// <summary>
    /// Marks an endpoint as healthy or unhealthy.
    /// </summary>
    /// <param name="address">The endpoint address.</param>
    /// <param name="healthy">Whether the endpoint is healthy.</param>
    public void SetEndpointHealth(string address, bool healthy)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _loadBalancer.SetEndpointHealth(address, healthy);

        if (_clients.TryGetValue(address, out var managed))
            managed.IsHealthy = healthy;
    }

    /// <summary>
    /// Records a successful request for metrics tracking.
    /// </summary>
    /// <param name="address">The endpoint address.</param>
    /// <param name="latency">The request latency.</param>
    public void RecordSuccess(string address, TimeSpan latency)
    {
        _loadBalancer.RecordSuccess(address, latency);
    }

    /// <summary>
    /// Records a failed request for metrics tracking.
    /// </summary>
    /// <param name="address">The endpoint address.</param>
    /// <param name="exception">The exception that occurred.</param>
    public void RecordError(string address, Exception exception)
    {
        _loadBalancer.RecordError(address, exception);
    }

    /// <summary>
    /// Gets all registered endpoints.
    /// </summary>
    public IReadOnlyList<string> GetEndpoints()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _clients.Keys.ToList().AsReadOnly();
    }

    /// <summary>
    /// Gets only healthy endpoints.
    /// </summary>
    public IReadOnlyList<string> GetHealthyEndpoints()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _clients.Values.Where(c => c.IsHealthy).Select(c => c.Address).ToList().AsReadOnly();
    }

    /// <summary>
    /// Disposes all managed clients and cleans up.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();

        foreach (var kvp in _clients)
        {
            try
            {
                await kvp.Value.Client.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Ignore disposal errors
            }
        }

        _clients.Clear();
        _cts.Dispose();
    }

    private sealed class ManagedClient
    {
        public string Address { get; set; } = string.Empty;
        public ThriftClient Client { get; set; } = null!;
        public DateTimeOffset CreatedAt { get; set; }
        public bool IsHealthy { get; set; } = true;
    }
}

/// <summary>
/// A Thrift client wrapper that integrates with <see cref="ILoadBalancer"/> for
/// automatic endpoint selection and failover. Selects an endpoint per request
/// using the load balancer, and records success/error for metrics tracking.
/// </summary>
public sealed class LoadBalancedThriftClient : IAsyncDisposable
{
    private readonly ThriftClientManager _manager;
    private readonly ThriftClientOptions _defaultOptions;
    private readonly bool _enableFailover;
    private readonly int _maxFailoverAttempts;
    private readonly Random _random = new();
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of <see cref="LoadBalancedThriftClient"/>.
    /// </summary>
    /// <param name="manager">The ThriftClientManager managing the endpoint pool.</param>
    /// <param name="defaultOptions">Default Thrift client options.</param>
    /// <param name="enableFailover">Whether to automatically failover to another endpoint on error.</param>
    /// <param name="maxFailoverAttempts">Maximum number of failover attempts (default: 3).</param>
    public LoadBalancedThriftClient(
        ThriftClientManager manager,
        ThriftClientOptions? defaultOptions = null,
        bool enableFailover = true,
        int maxFailoverAttempts = 3)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _defaultOptions = defaultOptions ?? new ThriftClientOptions();
        _enableFailover = enableFailover;
        _maxFailoverAttempts = maxFailoverAttempts;
    }

    /// <summary>
    /// Calls a Thrift method with automatic load balancing and optional failover.
    /// </summary>
    public async ValueTask<TResponse> CallAsync<TRequest, TResponse>(
        string methodName,
        TRequest request,
        RpcOptions? options = null,
        CancellationToken cancellationToken = default)
        where TRequest : class, IThriftSerializable
        where TResponse : class, IThriftSerializable, new()
    {
        if (string.IsNullOrWhiteSpace(methodName))
            throw new ArgumentException("Method name must not be empty or whitespace.", nameof(methodName));

        var endpoints = GetAvailableEndpoints();
        if (endpoints.Count == 0)
            throw new InvalidOperationException("No healthy endpoints available.");

        var attempt = 0;
        var lastException = default(Exception?);

        while (attempt < _maxFailoverAttempts)
        {
            attempt++;

            // Select an endpoint (randomize among healthy to spread load across retries)
            var endpoint = SelectEndpoint(endpoints);
            if (endpoint == null)
                break;

            var client = _manager.SelectClient();
            if (client == null)
                break;

            var stopwatch = Stopwatch.StartNew();
            try
            {
                var response = await client.CallAsync<TRequest, TResponse>(
                    methodName,
                    request,
                    options,
                    cancellationToken).ConfigureAwait(false);

                stopwatch.Stop();
                _manager.RecordSuccess(endpoint.Address, stopwatch.Elapsed);

                return Unsafe.As<TResponse>(response);
            }
            catch (Exception ex) when (_enableFailover && attempt < _maxFailoverAttempts)
            {
                stopwatch.Stop();
                lastException = ex;
                _manager.RecordError(endpoint.Address, ex);

                // Mark endpoint as unhealthy and try another
                _manager.SetEndpointHealth(endpoint.Address, false);

                // Remove from available list for next attempt
                endpoints.Remove(endpoint);
                if (endpoints.Count == 0)
                    break;
            }
        }

        throw lastException ?? new InvalidOperationException("All endpoints failed.");
    }

    /// <summary>
    /// Calls a one-way Thrift method with load balancing.
    /// </summary>
    public async ValueTask CallOneWayAsync<TRequest>(
        string methodName,
        TRequest request,
        RpcOptions? options = null,
        CancellationToken cancellationToken = default)
        where TRequest : class, IThriftSerializable
    {
        if (string.IsNullOrWhiteSpace(methodName))
            throw new ArgumentException("Method name must not be empty or whitespace.", nameof(methodName));

        var endpoints = GetAvailableEndpoints();
        if (endpoints.Count == 0)
            throw new InvalidOperationException("No healthy endpoints available.");

        var endpoint = SelectEndpoint(endpoints);
        if (endpoint == null)
            throw new InvalidOperationException("No healthy endpoints available.");

        var client = _manager.SelectClient();
        if (client == null)
            throw new InvalidOperationException("No client available for selected endpoint.");

        try
        {
            await client.CallOneWayAsync<TRequest>(
                methodName,
                request,
                options,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _manager.RecordError(endpoint.Address, ex);
            throw;
        }
    }

    /// <summary>
    /// Gets all registered endpoint addresses.
    /// </summary>
    public IReadOnlyList<string> GetEndpoints()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _manager.GetEndpoints();
    }

    /// <summary>
    /// Gets only healthy endpoint addresses.
    /// </summary>
    public IReadOnlyList<string> GetHealthyEndpoints()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _manager.GetHealthyEndpoints();
    }

    private List<string> GetAvailableEndpoints()
    {
        return _manager.GetHealthyEndpoints();
    }

    private string? SelectEndpoint(List<string> endpoints)
    {
        if (endpoints.Count == 0)
            return null;

        return endpoints[_random.Next(endpoints.Count)];
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _manager.DisposeAsync().ConfigureAwait(false);
    }
}
