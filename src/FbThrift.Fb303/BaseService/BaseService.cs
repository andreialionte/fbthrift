// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Fb303.BaseService;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FbThrift.Core.Abstractions;
using FbThrift.Fb303.Metrics;
using FbThriftOptions = FbThrift.Fb303.Options.BaseServiceOptions;
using Microsoft.Extensions.Logger;
using Microsoft.Extensions.Options;

/// <summary>
 * Service status enum matching fb303_status from fb303_core.thrift
 * </summary>
public enum Fb303Status
{
    DEAD = 0,
    STARTING = 1,
    ALIVE = 2,
    STOPPING = 3,
    STOPPED = 4,
    WARNING = 5,
}

/// <summary>
 * Base service implementation matching fb303_core.thrift BaseService interface.
 * Provides standard service introspection and monitoring capabilities.
 * </summary>
public sealed class BaseService : IFb303BaseService
{
    private readonly ILogger<BaseService> _logger;
    private readonly BaseServiceOptions _options;
    private readonly ConcurrentDictionary<string, long> _counters = new();
    private readonly ConcurrentDictionary<string, string> _exportedValues = new();
    private readonly ConcurrentDictionary<string, string> _options = new();
    private readonly Fb303Metrics _metrics;
    private readonly DateTimeOffset _startTime = DateTimeOffset.UtcNow;
    private volatile Fb303Status _status = Fb303Status.STARTING;
    private string? _statusDetails;
    private readonly RegexOptions _regexOptions = RegexOptions.Compiled | RegexOptions.IgnoreCase;

    public BaseService(
        ILogger<BaseService> logger,
        IOptions<FbThriftOptions> options,
        Fb303Metrics metrics)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));

        // Set default exported values
        SetExportedValue("service_name", _options.ServiceName);
        SetExportedValue("version", _options.Version);
        SetExportedValue("build_timestamp", DateTimeOffset.UtcNow.ToString("O"));
        
        // Set default options
        foreach (var kvp in _options.DefaultOptions)
        {
            _options.TryAdd(kvp.Key, kvp.Value);
        }

        _logger.LogInformation("BaseService initialized for {ServiceName} v{Version}", _options.ServiceName, _options.Version);
    }

    /// <inheritdoc/>
    public Fb303Status getStatus()
    {
        return _status;
    }

    /// <inheritdoc/>
    public string getName()
    {
        return _options.ServiceName;
    }

    /// <inheritdoc/>
    public string getVersion()
    {
        return _options.Version;
    }

    /// <inheritdoc/>
    public string getStatusDetails()
    {
        return _statusDetails ?? string.Empty;
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, long> getCounters()
    {
        return new Dictionary<string, long>(_counters);
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, long> getRegexCounters(string regex)
    {
        try
        {
            var regexObj = new Regex(regex, _regexOptions);
            return _counters
                .Where(kvp => regexObj.IsMatch(kvp.Key))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }
        catch (RegexParseException ex)
        {
            throw new ArgumentException($"Invalid regex pattern: {regex}", ex);
        }
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, long> getSelectedCounters(IReadOnlyList<string> keys)
    {
        var result = new Dictionary<string, long>();
        foreach (var key in keys)
        {
            if (_counters.TryGetValue(key, out var value))
            {
                result[key] = value;
            }
        }
        return result;
    }

    /// <inheritdoc/>
    public long getCounter(string key)
    {
        return _counters.TryGetValue(key, out var value) ? value : 0;
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, string> getExportedValues()
    {
        return new Dictionary<string, string>(_exportedValues);
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, string> getSelectedExportedValues(IReadOnlyList<string> keys)
    {
        var result = new Dictionary<string, string>();
        foreach (var key in keys)
        {
            if (_exportedValues.TryGetValue(key, out var value))
            {
                result[key] = value;
            }
        }
        return result;
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, string> getRegexExportedValues(string regex)
    {
        try
        {
            var regexObj = new Regex(regex, _regexOptions);
            return _exportedValues
                .Where(kvp => regexObj.IsMatch(kvp.Key))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }
        catch (RegexParseException ex)
        {
            throw new ArgumentException($"Invalid regex pattern: {regex}", ex);
        }
    }

    /// <inheritdoc/>
    public string getExportedValue(string key)
    {
        return _exportedValues.TryGetValue(key, out var value) ? value : string.Empty;
    }

    /// <inheritdoc/>
    public void setOption(string key, string value)
    {
        _options.AddOrUpdate(key, value, (k, v) => value);
        _logger.LogDebug("Option set: {Key} = {Value}", key, value);
    }

    /// <inheritdoc/>
    public string getOption(string key)
    {
        return _options.TryGetValue(key, out var value) ? value : string.Empty;
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, string> getOptions()
    {
        return new Dictionary<string, string>(_options);
    }

    /// <inheritdoc/>
    public long aliveSince()
    {
        return _startTime.ToUnixTimeSeconds();
    }

    // Additional public methods for managing service state

    /// <summary>
     * Sets the service status
     * </summary>
    public void SetStatus(Fb303Status status, string? details = null)
    {
        _status = status;
        _statusDetails = details;
        _logger.LogInformation("Service status changed to {Status}: {Details}", status, details ?? "N/A");
        
        _metrics.StatusChanges.Add(1, new KeyValuePair<string, object?>("status", status.ToString()));
    }

    /// <summary>
     * Increments a counter by the specified value
     * </summary>
    public long IncrementCounter(string key, long increment = 1)
    {
        var newValue = _counters.AddOrUpdate(key, increment, (k, v) => v + increment);
        _metrics.CounterIncrement.Add(increment, new KeyValuePair<string, object?>("counter", key));
        return newValue;
    }

    /// <summary>
     * Sets a counter to a specific value
     * </summary>
    public void SetCounter(string key, long value)
    {
        _counters[key] = value;
        _metrics.CounterValue.Record(value, new KeyValuePair<string, object?>("counter", key));
    }

    /// <summary>
     * Sets an exported string value
     * </summary>
    public void SetExportedValue(string key, string value)
    {
        _exportedValues[key] = value;
        _logger.LogDebug("Exported value set: {Key} = {Value}", key, value);
    }

    /// <summary>
     * Removes an exported value
     * </summary>
    public bool RemoveExportedValue(string key)
    {
        return _exportedValues.TryRemove(key, out _);
    }

    /// <summary>
     * Gets the health check result for this service
     * </summary>
    public HealthCheckResult GetHealthCheck()
    {
        var status = _status switch
        {
            Fb303Status.ALIVE => HealthStatus.Healthy,
            Fb303Status.STARTING => HealthStatus.Healthy,
            Fb303Status.WARNING => HealthStatus.Degraded,
            _ => HealthStatus.Unhealthy
        };

        return new HealthCheckResult
        {
            Status = status,
            Description = _statusDetails ?? _status.ToString(),
            Data = new Dictionary<string, object>
            {
                ["status"] = _status.ToString(),
                ["alive_since"] = aliveSince(),
                ["uptime_seconds"] = (DateTimeOffset.UtcNow - _startTime).TotalSeconds,
                ["counters_count"] = _counters.Count,
                ["exported_values_count"] = _exportedValues.Count,
                ["options_count"] = _options.Count
            }
        };
    }
}

/// <summary>
 * Interface for the fb303 BaseService
 * </summary>
public interface IFb303BaseService
{
    Fb303Status getStatus();
    string getName();
    string getVersion();
    string getStatusDetails();
    IReadOnlyDictionary<string, long> getCounters();
    IReadOnlyDictionary<string, long> getRegexCounters(string regex);
    IReadOnlyDictionary<string, long> getSelectedCounters(IReadOnlyList<string> keys);
    long getCounter(string key);
    IReadOnlyDictionary<string, string> getExportedValues();
    IReadOnlyDictionary<string, string> getSelectedExportedValues(IReadOnlyList<string> keys);
    IReadOnlyDictionary<string, string> getRegexExportedValues(string regex);
    string getExportedValue(string key);
    void setOption(string key, string value);
    string getOption(string key);
    IReadOnlyDictionary<string, string> getOptions();
    long aliveSince();
}
