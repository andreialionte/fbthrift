// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Fb303.Metrics;

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;

/// <summary>
/// Provides fb303 metrics collection integrated with OpenTelemetry Meter.
/// This class bridges fb303-style service metrics with .NET's System.Diagnostics.Metrics.
/// </summary>
public sealed class Fb303MetricsProvider : IDisposable
{
    private readonly Meter _meter;
    private readonly Fb303MetricsOptions _options;
    private readonly Counter<long>? _requestsTotal;
    private readonly Counter<long>? _requestsErrors;
    private readonly Histogram<double>? _requestDuration;
    private readonly Counter<long>? _connectionsTotal;
    private readonly UpDownCounter<long>? _activeConnections;
    private readonly Counter<long>? _bytesReceived;
    private readonly Counter<long>? _bytesSent;

    public Fb303MetricsProvider(IOptions<Fb303MetricsOptions> options)
    {
        _options = options.Value;
        _meter = new Meter(_options.MeterName);

        if (_options.EnableRequestCounters)
        {
            _requestsTotal = _meter.CreateCounter<long>(
                "fb303_requests_total",
                "requests",
                "Total number of requests processed");

            _requestsErrors = _meter.CreateCounter<long>(
                "fb303_requests_errors_total",
                "requests",
                "Total number of request errors");
        }

        if (_options.EnableDurationHistograms)
        {
            _requestDuration = _meter.CreateHistogram<double>(
                "fb303_request_duration_seconds",
                "seconds",
                "Request processing duration in seconds");
        }

        if (_options.EnableConnectionCounters)
        {
            _connectionsTotal = _meter.CreateCounter<long>(
                "fb303_connections_total",
                "connections",
                "Total number of connections");

            _activeConnections = _meter.CreateUpDownCounter<long>(
                "fb303_active_connections",
                "connections",
                "Current number of active connections");
        }

        if (_options.EnableByteCounters)
        {
            _bytesReceived = _meter.CreateCounter<long>(
                "fb303_bytes_received_total",
                "bytes",
                "Total bytes received");

            _bytesSent = _meter.CreateCounter<long>(
                "fb303_bytes_sent_total",
                "bytes",
                "Total bytes sent");
        }
    }

    public void RecordRequest(string service, string method, long value = 1)
    {
        if (_requestsTotal == null) return;

        var tags = new List<KeyValuePair<string, object>>
        {
            new("service", service),
            new("method", method)
        };
        tags.AddRange(_options.DefaultTags);

        _requestsTotal.Add(value, tags);
    }

    public void RecordError(string service, string method, string errorType, long value = 1)
    {
        if (_requestsErrors == null) return;

        var tags = new List<KeyValuePair<string, object>>
        {
            new("service", service),
            new("method", method),
            new("error_type", errorType)
        };
        tags.AddRange(_options.DefaultTags);

        _requestsErrors.Add(value, tags);
    }

    public void RecordRequestDuration(string service, string method, double durationSeconds)
    {
        if (_requestDuration == null) return;

        var tags = new List<KeyValuePair<string, object>>
        {
            new("service", service),
            new("method", method)
        };
        tags.AddRange(_options.DefaultTags);

        _requestDuration.Record(durationSeconds, tags);
    }

    public void RecordConnection(long value = 1)
    {
        if (_connectionsTotal == null || _activeConnections == null) return;

        _connectionsTotal.Add(value);
        _activeConnections.Add(value);
    }

    public void DecrementActiveConnections(long value = 1)
    {
        if (_activeConnections == null) return;

        _activeConnections.Add(-value);
    }

    public void RecordBytesReceived(long bytes)
    {
        if (_bytesReceived == null) return;

        _bytesReceived.Add(bytes, _options.DefaultTags);
    }

    public void RecordBytesSent(long bytes)
    {
        if (_bytesSent == null) return;

        _bytesSent.Add(bytes, _options.DefaultTags);
    }

    public void Dispose()
    {
        _meter.Dispose();
    }
}
