// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Core.Fb303;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// fb303-compatible service metadata and health checks.
/// Based on fbthrift's fb303 specification.
/// </summary>
public static class Fb303Metrics
{
    private static readonly Meter _meter = new Meter("fb303");
    private static readonly Counter<long> _requestsTotal = _meter.CreateCounter<long>("fb303_requests_total", "requests", "Total number of requests");
    private static readonly Counter<long> _requestsErrors = _meter.CreateCounter<long>("fb303_requests_errors_total", "requests", "Total number of request errors");
    private static readonly Histogram<double> _requestDuration = _meter.CreateHistogram<double>("fb303_request_duration_seconds", "seconds", "Request duration");
    private static readonly Counter<long> _connectionsTotal = _meter.CreateCounter<long>("fb303_connections_total", "connections", "Total connections");
    private static readonly UpDownCounter<long> _activeConnections = _meter.CreateUpDownCounter<long>("fb303_active_connections", "connections", "Active connections");
    private static readonly Counter<long> _bytesReceived = _meter.CreateCounter<long>("fb303_bytes_received_total", "bytes", "Total bytes received");
    private static readonly Counter<long> _bytesSent = _meter.CreateCounter<long>("fb303_bytes_sent_total", "bytes", "Total bytes sent");
    
    /// <summary>
    /// Increments the total request counter.
    /// </summary>
    public static void IncrementRequestCount(string service, string method, long value = 1)
    {
        _requestsTotal.Add(value, new KeyValuePair<string, object>("service", "fbthrift"), new KeyValuePair<string, object>("method", method));
    }
    
    /// <summary>
    /// Increments the error counter.
    /// </summary>
    public static void IncrementErrorCount(string service, string method, string errorType, long value = 1)
    {
        _requestsErrors.Add(value, 
            new KeyValuePair<string, object>("service", "fbthrift"),
            new KeyValuePair<string, object>("method", method),
            new KeyValuePair<string, object>("error_type", errorType));
    }
    
    /// <summary>
    /// Records request duration.
    /// </summary>
    public static void RecordRequestDuration(string service, string method, double durationSeconds)
    {
        _requestDuration.Record(durationSeconds, 
            new KeyValuePair<string, object>("service", "fbthrift"),
            new KeyValuePair<string, object>("method", method));
    }
    
    /// <summary>
    /// Increments total connections.
    /// </summary>
    public static void IncrementConnections(long value = 1)
    {
        _connectionsTotal.Add(value);
        _activeConnections.Add(value);
    }
    
    /// <summary>
    /// Decrements active connections.
    /// </summary>
    public static void DecrementActiveConnections(long value = 1)
    {
        _activeConnections.Add(-value);
    }
    
    /// <summary>
    /// Records bytes received.
    /// </summary>
    public static void RecordBytesReceived(long bytes)
    {
        _bytesReceived.Add(bytes);
    }
    
    /// <summary>
    /// Records bytes sent.
    /// </summary>
    public static void RecordBytesSent(long bytes)
    {
        _bytesSent.Add(bytes);
    }
}

/// <summary>
/// fb303 service handler interface.
/// </summary>
public interface IFb303Service
{
    string GetName();
    string GetVersion();
    ServiceStatus GetStatus();
    string GetStatusDetails();
    Dictionary<string, string> GetCounters();
    Dictionary<string, string> GetDynamicCounters();
    Task<Dictionary<string, string>> GetCountersAsync();
    Task<Dictionary<string, string>> GetDynamicCountersAsync();
}

/// <summary>
/// Base implementation of fb303 service.
/// </summary>
public abstract class Fb303ServiceBase : IFb303Service
{
    private readonly string _name;
    private readonly string _version;
    private readonly ConcurrentDictionary<string, string> _counters = new();
    private readonly ConcurrentDictionary<string, string> _dynamicCounters = new();
    private ServiceStatus _status = ServiceStatus.Starting;
    private string _statusDetails = string.Empty;
    
    protected Fb303ServiceBase(string name, string version = "1.0.0")
    {
        _name = name;
        _version = version;
    }
    
    public string GetName() => _name;
    public string GetVersion() => _version;
    public ServiceStatus GetStatus() => _status;
    public string GetStatusDetails() => _statusDetails;
    public Dictionary<string, string> GetCounters() => new(_counters);
    public Dictionary<string, string> GetDynamicCounters() => new(_dynamicCounters);
    
    public Task<Dictionary<string, string>> GetCountersAsync()
    {
        return Task.FromResult(new Dictionary<string, string>(_counters));
    }
    
    public Task<Dictionary<string, string>> GetDynamicCountersAsync()
    {
        return Task.FromResult(new Dictionary<string, string>(_dynamicCounters));
    }
    
    protected void SetStatus(ServiceStatus status, string details = "")
    {
        _status = status;
        _statusDetails = details;
    }
    
    protected void IncrementCounter(string name, long value = 1)
    {
        _counters.AddOrUpdate(name, value.ToString(), (_, old) => (long.Parse(old) + 1).ToString());
    }
    
    protected void SetCounter(string name, string value)
    {
        _counters[name] = value;
    }
    
    protected void IncrementDynamicCounter(string name, long value = 1)
    {
        _dynamicCounters.AddOrUpdate(name, value.ToString(), (_, old) => (long.Parse(old) + 1).ToString());
    }
    
    protected void SetDynamicCounter(string name, string value)
    {
        _dynamicCounters[name] = value;
    }
}

/// <summary>
/// fb303 service metadata response.
/// </summary>
public sealed class ServiceMetadataResponse
{
    public string Name { get; set; }
    public string Version { get; set; }
    public ServiceStatus Status { get; set; }
    public string StatusDetails { get; set; }
    public Dictionary<string, string> Counters { get; set; }
    public Dictionary<string, string> DynamicCounters { get; set; }
}

/// <summary>
/// fb303 service metadata request.
/// </summary>
public sealed class ServiceMetadataRequest
{
    public string ServiceName { get; set; }
}

/// <summary>
/// fb303 get counters request.
/// </summary>
public sealed class GetCountersRequest
{
    public List<string> CounterNames { get; set; }
}

/// <summary>
/// fb303 get dynamic counters request.
/// </summary>
public sealed class GetDynamicCountersRequest
{
    public List<string> CounterNames { get; set; }
}