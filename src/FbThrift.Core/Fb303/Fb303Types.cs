// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Core.Fb303;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// fb303 Service Status enum.
/// </summary>
public enum ServiceStatus
{
    Alive = 0,
    Dead = 1,
    Starting = 2,
    Stopping = 3,
    Warning = 4
}

/// <summary>
/// fb303 request/response types.
/// </summary>
public sealed class GetNameRequest { }
public sealed class GetNameResponse { public string Name { get; set; } = string.Empty; }

public sealed class GetVersionRequest { }
public sealed class GetVersionResponse { public string Version { get; set; } = string.Empty; }

public sealed class GetStatusRequest { }
public sealed class GetStatusResponse { public ServiceStatus Status { get; set; } }

public sealed class GetStatusDetailsRequest { }
public sealed class GetStatusDetailsResponse { public string Details { get; set; } = string.Empty; }

public sealed class ReinitializeRequest { }
public sealed class ReinitializeResponse { }