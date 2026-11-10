// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Core.Protocol;

/// <summary>
/// THeader client type enumeration.
/// </summary>
public enum THeaderClientType : uint
{
    ThriftFramedDeprecated = 0x00000001,
    ThriftUnframedDeprecated = 0x00000002,
    ThriftHttpServer = 0x00000003,
    ThriftHttpClient = 0x00000004,
    ThriftHeader = 0x00000005,
    ThriftFramedCompact = 0x00000006,
    ThriftUnframedCompact = 0x00000007,
    ThriftRocketClient = 0x00000006,
    ThriftRocketServer = 0x00000007,
    ThriftUnknown = 0xFFFFFFFF,
}