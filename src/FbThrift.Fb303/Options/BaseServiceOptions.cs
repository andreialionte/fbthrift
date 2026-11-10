// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Fb303.Options;

using System.Collections.Generic;

/// <summary>
/// Options for configuring the fb303 BaseService.
/// </summary>
public sealed class BaseServiceOptions
{
    /// <summary>
    /// Gets or sets the service name reported by fb303 introspection.
    /// </summary>
    public string ServiceName { get; set; } = "FbThriftService";

    /// <summary>
    /// Gets or sets the service version reported by fb303 introspection.
    /// </summary>
    public string Version { get; set; } = "1.0.0";

    /// <summary>
    /// Gets or sets default options key-value pairs for the service.
    /// </summary>
    public Dictionary<string, string> DefaultOptions { get; set; } = new();

    /// <summary>
    /// Gets or sets default counter key-value pairs for the service.
    /// </summary>
    public Dictionary<string, long> DefaultCounters { get; set; } = new();

    /// <summary>
    /// Gets or sets default exported values key-value pairs for the service.
    /// </summary>
    public Dictionary<string, string> DefaultExportedValues { get; set; } = new();
}
