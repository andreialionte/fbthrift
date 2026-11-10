// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Core.Protocol;

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

/// <summary>
/// Thrift message types.
/// </summary>
public enum TMessageType : byte
{
    Call = 1,
    Reply = 2,
    Exception = 3,
    Oneway = 4,
}

/// <summary>
/// Protocol identifiers (from fbthrift THeader).
/// </summary>
public enum ProtocolId : ushort
{
    TBinaryProtocol = 0x0000,
    TCompactProtocol = 0x0001,
    TSimpleJsonProtocol = 0x0002,
    THeaderProtocol = 0x0003,
}

/// <summary>
/// Transform identifiers for THeader.
/// </summary>
public enum TransformId : ushort
{
    None = 0x0000,
    Zlib = 0x0001,
    Zstd = 0x0002,
    Snappy = 0x0003,
    Lz4 = 0x0004,
}

/// <summary>
/// Client types for THeader protocol detection.
/// </summary>
public enum ClientType : uint
{
    ThriftFramedDeprecated = 0x00000001,
    ThriftUnframedDeprecated = 0x00000002,
    ThriftHttpServer = 0x00000003,
    ThriftHttpClient = 0x00000004,
    ThriftHeader = 0x00000005,
    ThriftFramedCompact = 0x00000006,
    ThriftUnframedCompact = 0x00000007,
    ThriftRocketClient = 0x00000006, // Same as ThriftFramedCompact for detection
    ThriftRocketServer = 0x00000007,
    ThriftUnknown = 0xFFFFFFFF,
}

/// <summary>
/// THeader flags.
/// </summary>
[Flags]
public enum THeaderFlags : uint
{
    None = 0x00000000,
    BigFrames = 0x00000001,
}

/// <summary>
/// Transport protocol types (matches fbthrift PROTOCOL_TYPES).
/// </summary>
public enum TransportProtocolType : byte
{
    TBinaryProtocol = 0,
    TCompactProtocol = 1,
    TJsonProtocol = 2,
    THeaderProtocol = 3,
    TSimpleJsonProtocol = 3,
}

/// <summary>
/// Client types for Thrift.
/// </summary>
public enum ThriftClientType : uint
{
    ThriftHeaderClientType = 0,
    ThriftHttpClientType = 1,
    ThriftHttpServerType = 2,
    ThriftUnframedDeprecated = 3,
    ThriftFramedDeprecated = 4,
    ThriftUnframedCompactDeprecated = 5,
    ThriftFramedCompact = 6,
    ThriftHttpServerType2 = 7,
    ThriftRocketClientType = 8,
    ThriftRocketServerType = 9,
    ThriftUnknownClientType = 255,
}

/// <summary>
/// Constants for protocol detection (from fbthrift THeader.cpp).
/// </summary>
public static class ProtocolConstants
{
    // Magic numbers for protocol detection
    public const uint BinaryProtocolVersion1 = 0x80010000;
    public const uint BinaryProtocolVersionMask = 0xFFFF0000;
    public const uint TBinaryProtocol = 0x80010000;
    public const ushort BinaryProtocolId = 0x0001;
    
    // Compact protocol
    public const byte CompactProtocolId = 0x82;
    public const byte CompactProtocolVersion = 0x02;
    public const byte CompactVersionMask = 0x1F;
    public const byte CompactVersionLow = 1;
    public const byte CompactVersionHigh = 2;
    
    // HTTP magic numbers
    public const uint HttpServerMagic = 0x48545450; // "HTTP"
    public const uint HttpGetClientMagic = 0x47455420; // "GET "
    public const uint HttpHeadClientMagic = 0x48454144; // "HEAD"
    public const uint HttpClientMagic = 0x504F5354; // "POST"
    
    // Header protocol
    public const uint HeaderMagic = 0x00008000;
    public const uint HeaderMask = 0xFFFF0000;
    public const uint HeaderCommonSize = 4 + 4 + 2; // seqId(4) + flags(4) + headerSize(2)
    
    // THeader format: seqId(4) + headerSize(2) + protocolId(2) + numTransforms(2) + transforms... + headerFields...
    public const int HeaderMinSize = 14; // seqId(4) + headerSize(2) + protocolId(2) + numTransforms(2) + 2 bytes min
    
    // Compact protocol field types
    public const byte CompactStop = 0x00;
    public const byte CompactBooleanTrue = 0x01;
    public const byte CompactBooleanFalse = 0x02;
    public const byte CompactByte = 0x03;
    public const byte CompactI16 = 0x04;
    public const byte CompactI32 = 0x05;
    public const byte CompactI64 = 0x06;
    public const byte CompactDouble = 0x07;
    public const byte CompactBinary = 0x08;
    public const byte CompactList = 0x09;
    public const byte CompactSet = 0x0A;
    public const byte CompactMap = 0x0B;
    public const byte CompactStruct = 0x0C;
    public const byte CompactUuid = 0x0D;
    
    // Compact protocol type mask/shift
    public const byte CompactTypeMask = 0xE0;
    public const byte CompactTypeShift = 5;
    public const byte CompactVersion = 0x02;
    
    // Varint encoding
    public const int MaxVarintBytes16 = 2;
    public const int MaxVarintBytes32 = 5;
    public const int MaxVarintBytes64 = 10;
    
    // ZigZag encoding
    public const long ZigZagMax = long.MaxValue;
    
    // Container limits
    public const int DefaultStringLimit = 1024 * 1024; // 1MB
    public const int DefaultContainerLimit = 1024 * 1024; // 1M elements
    
    // Header protocol
    public const ushort HeaderVersion1 = 1;
    public const ushort HeaderMagicValue = 0x8000;
    public const ushort HeaderMagicMask = 0x8000;
    
    // Transform types
    public const ushort TransformZlib = 1;
    public const ushort TransformZstd = 2;
    public const ushort TransformSnappy = 3;
    
    // Max frame sizes
    public const int DefaultMaxFrameSize = 16 * 1024 * 1024; // 16MB
    public const int DefaultHeaderSize = 4096;
}