// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Fb303.Metrics;

/// <summary>
/// Options for configuring fb303 metrics collection and export.
/// </summary>
public sealed class Fb303MetricsOptions
{
    /// <summary>
    /// Gets or sets the meter name used for fb303 metrics.
    /// </summary>
    public string MeterName { get; set; } = "fb303";

    /// <summary>
    /// Gets or sets whether request counters are enabled.
    /// </summary>
    public bool EnableRequestCounters { get; set; } = true;

    /// <summary>
    /// Gets or sets whether error counters are enabled.
    /// </summary>
    public bool EnableErrorCounters { get; set; } = true;

    /// <summary>
    /// Gets or sets whether request duration histograms are enabled.
    /// </summary>
    public bool EnableDurationHistograms { get; set; } = true;

    /// <summary>
    /// Gets or sets whether connection counters are enabled.
    /// </summary>
    public bool EnableConnectionCounters { get; set; } = true;

    /// <summary>
    /// Gets or sets whether byte counters are enabled.
    /// </summary>
    public bool EnableByteCounters { get; set; } = true;

    /// <summary>
    /// Gets or sets the default tags applied to all metrics.
    /// </summary>
    public Dictionary<string, string> DefaultTags { get; set; } = new()
    {
        ["service"] = "fbthrift"
    };

    /// <summary>
    /// Gets or sets whether to export metrics via OpenTelemetry.
    /// </summary>
    public bool EnableOpenTelemetryExport { get; set; } = false;
}
