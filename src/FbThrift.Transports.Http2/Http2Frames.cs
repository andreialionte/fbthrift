// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Http2;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using FbThrift.Core.Header;
using FbThrift.Core.Protocol;
using FbThrift.Core.Transport;

/// <summary>
/// HTTP/2 Frame Types (RFC 7540).
/// </summary>
public enum Http2FrameType : byte
{
    Data = 0x0,
    Headers = 0x1,
    Priority = 0x2,
    RstStream = 0x3,
    Settings = 0x4,
    PushPromise = 0x5,
    Ping = 0x6,
    GoAway = 0x7,
    WindowUpdate = 0x8,
    Continuation = 0x9,
}

/// <summary>
/// HTTP/2 Frame Flags.
/// </summary>
[Flags]
public enum Http2FrameFlags : byte
{
    None = 0x0,
    
    // DATA flags
    DataEndStream = 0x1,
    DataPadded = 0x8,
    
    // HEADERS flags
    HeadersEndStream = 0x1,
    HeadersEndHeaders = 0x4,
    HeadersPadded = 0x8,
    HeadersPriority = 0x20,
    
    // SETTINGS flags
    SettingsAck = 0x1,
    
    // PING flags
    PingAck = 0x1,
}

/// <summary>
/// HTTP/2 Settings Identifiers (RFC 7540 Section 6.5.2).
/// </summary>
public enum Http2SettingsId : ushort
{
    HeaderTableSize = 0x1,
    EnablePush = 0x2,
    MaxConcurrentStreams = 0x3,
    InitialWindowSize = 0x4,
    MaxFrameSize = 0x5,
    MaxHeaderListSize = 0x6,
    EnableConnectProtocol = 0x8, // RFC 8441
}

/// <summary>
/// HTTP/2 Error Codes (RFC 7540 Section 7).
/// </summary>
public enum Http2ErrorCode : uint
{
    NoError = 0x0,
    ProtocolError = 0x1,
    InternalError = 0x2,
    FlowControlError = 0x3,
    SettingsTimeout = 0x4,
    StreamClosed = 0x5,
    FrameSizeError = 0x6,
    RefusedStream = 0x7,
    Cancel = 0x8,
    CompressionError = 0x9,
    ConnectError = 0xA,
    EnhanceYourCalm = 0xB,
    InadequateSecurity = 0xC,
    Http11Required = 0xD,
}

/// <summary>
/// HTTP/2 Frame Header (9 bytes).
/// </summary>
public readonly struct Http2FrameHeader
{
    public const int Size = 9;
    
    public uint Length { get; init; } // 24-bit
    public Http2FrameType Type { get; init; }
    public Http2FrameFlags Flags { get; init; }
    public uint StreamId { get; init; } // 31-bit (bit 31 reserved)
    
    public static Http2FrameHeader Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < Size)
            throw new ArgumentException("Frame header too small");
        
        uint length = (uint)(data[0] << 16 | data[1] << 8 | data[2]);
        var type = (Http2FrameType)data[3];
        var flags = (Http2FrameFlags)data[4];
        uint streamId = (uint)(data[5] << 24 | data[6] << 16 | data[7] << 8 | data[8]);
        streamId &= 0x7FFFFFFF; // Mask reserved bit
        
        return new Http2FrameHeader
        {
            Length = length,
            Type = type,
            Flags = flags,
            StreamId = streamId
        };
    }
    
    public void Encode(Span<byte> output)
    {
        if (output.Length < Size)
            throw new ArgumentException("Output buffer too small");
        
        // Length (24-bit)
        output[0] = (byte)(Length >> 16);
        output[1] = (byte)(Length >> 8);
        output[2] = (byte)Length;
        
        // Type
        output[3] = (byte)Type;
        
        // Flags
        output[4] = (byte)Flags;
        
        // Stream ID (31-bit)
        output[5] = (byte)(StreamId >> 24);
        output[6] = (byte)(StreamId >> 16);
        output[7] = (byte)(StreamId >> 8);
        output[8] = (byte)StreamId;
    }
}

/// <summary>
/// HTTP/2 DATA Frame.
/// </summary>
public readonly struct Http2DataFrame
{
    public Http2FrameHeader Header { get; init; }
    public ReadOnlyMemory<byte> Data { get; init; }
    public int PadLength { get; init; }
    
    public bool EndStream => (Header.Flags & Http2FrameFlags.DataEndStream) != 0;
    public bool Padded => (Header.Flags & Http2FrameFlags.DataPadded) != 0;
}

/// <summary>
/// HTTP/2 HEADERS Frame.
/// </summary>
public readonly struct Http2HeadersFrame
{
    public Http2FrameHeader Header { get; init; }
    public ReadOnlyMemory<byte> HeaderBlockFragment { get; init; }
    public int PadLength { get; init; }
    public Http2Priority? Priority { get; init; }
    
    public bool EndStream => (Header.Flags & Http2FrameFlags.HeadersEndStream) != 0;
    public bool EndHeaders => (Header.Flags & Http2FrameFlags.HeadersEndHeaders) != 0;
    public bool Padded => (Header.Flags & Http2FrameFlags.HeadersPadded) != 0;
    public bool HasPriority => (Header.Flags & Http2FrameFlags.HeadersPriority) != 0;
}

/// <summary>
/// HTTP/2 PRIORITY Frame.
/// </summary>
public readonly struct Http2PriorityFrame
{
    public Http2FrameHeader Header { get; init; }
    public Http2Priority Priority { get; init; }
}

/// <summary>
/// HTTP/2 Priority Information (RFC 7540 Section 5.3).
/// </summary>
public readonly struct Http2Priority
{
    public uint StreamDependency { get; init; } // 31-bit
    public byte Weight { get; init; } // 0-255
    public bool Exclusive { get; init; }
}

/// <summary>
/// HTTP/2 RST_STREAM Frame.
/// </summary>
public readonly struct Http2RstStreamFrame
{
    public Http2FrameHeader Header { get; init; }
    public Http2ErrorCode ErrorCode { get; init; }
}

/// <summary>
/// HTTP/2 SETTINGS Frame.
/// </summary>
public readonly struct Http2SettingsFrame
{
    public Http2FrameHeader Header { get; init; }
    public IReadOnlyList<Http2Setting> Settings { get; init; }
    
    public bool IsAck => (Header.Flags & Http2FrameFlags.SettingsAck) != 0;
}

/// <summary>
/// HTTP/2 Setting Entry.
/// </summary>
public readonly struct Http2Setting
{
    public Http2SettingsId Id { get; init; }
    public uint Value { get; init; }
}

/// <summary>
/// HTTP/2 PUSH_PROMISE Frame.
/// </summary>
public readonly struct Http2PushPromiseFrame
{
    public Http2FrameHeader Header { get; init; }
    public uint PromisedStreamId { get; init; } // 31-bit
    public ReadOnlyMemory<byte> HeaderBlockFragment { get; init; }
    public int PadLength { get; init; }
    
    public bool EndHeaders => (Header.Flags & Http2FrameFlags.HeadersEndHeaders) != 0;
    public bool Padded => (Header.Flags & Http2FrameFlags.HeadersPadded) != 0;
}

/// <summary>
/// HTTP/2 PING Frame.
/// </summary>
public readonly struct Http2PingFrame
{
    public Http2FrameHeader Header { get; init; }
    public ulong OpaqueData { get; init; }
    
    public bool IsAck => (Header.Flags & Http2FrameFlags.PingAck) != 0;
}

/// <summary>
/// HTTP/2 GOAWAY Frame.
/// </summary>
public readonly struct Http2GoAwayFrame
{
    public Http2FrameHeader Header { get; init; }
    public uint LastStreamId { get; init; }
    public Http2ErrorCode ErrorCode { get; init; }
    public ReadOnlyMemory<byte> AdditionalDebugData { get; init; }
}

/// <summary>
/// HTTP/2 WINDOW_UPDATE Frame.
/// </summary>
public readonly struct Http2WindowUpdateFrame
{
    public Http2FrameHeader Header { get; init; }
    public uint WindowSizeIncrement { get; init; } // 31-bit
}

/// <summary>
/// HTTP/2 CONTINUATION Frame.
/// </summary>
public readonly struct Http2ContinuationFrame
{
    public Http2FrameHeader Header { get; init; }
    public ReadOnlyMemory<byte> HeaderBlockFragment { get; init; }
    
    public bool EndHeaders => (Header.Flags & Http2FrameFlags.HeadersEndHeaders) != 0;
}

/// <summary>
/// HTTP/2 Frame Parser.
/// </summary>
public static class Http2FrameParser
{
    public const int MaxFrameSize = 16384; // 16KB default
    
    /// <summary>
    /// Parses an HTTP/2 frame from the buffer.
    /// </summary>
    public static (Http2FrameHeader Header, ReadOnlyMemory<byte> Payload, int Consumed) ParseFrame(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < Http2FrameHeader.Size)
            return (default, default, 0);
        
        var header = Http2FrameHeader.Decode(buffer);
        
        if (header.Length > MaxFrameSize)
            throw new InvalidDataException($"Frame size {header.Length} exceeds maximum {MaxFrameSize}");
        
        int totalSize = Http2FrameHeader.Size + (int)header.Length;
        if (buffer.Length < totalSize)
            return (default, default, 0);
        
        var payload = buffer.Slice(Http2FrameHeader.Size, (int)header.Length).ToArray();
        
        return (header, payload, totalSize);
    }
    
    /// <summary>
    /// Parses a SETTINGS payload.
    /// </summary>
    public static IReadOnlyList<Http2Setting> ParseSettings(ReadOnlySpan<byte> payload)
    {
        if (payload.Length % 6 != 0)
            throw new InvalidDataException("Invalid SETTINGS payload length");
        
        var settings = new List<Http2Setting>(payload.Length / 6);
        
        for (int i = 0; i < payload.Length; i += 6)
        {
            var id = (Http2SettingsId)(payload[i] << 8 | payload[i + 1]);
            uint value = (uint)(payload[i + 2] << 24 | payload[i + 3] << 16 | payload[i + 4] << 8 | payload[i + 5]);
            settings.Add(new Http2Setting { Id = id, Value = value });
        }
        
        return settings;
    }
    
    /// <summary>
    /// Encodes a SETTINGS payload.
    /// </summary>
    public static void EncodeSettings(Span<byte> output, IReadOnlyList<Http2Setting> settings)
    {
        for (int i = 0; i < settings.Count; i++)
        {
            var setting = settings[i];
            int offset = i * 6;
            output[offset] = (byte)(setting.Id >> 8);
            output[offset + 1] = (byte)setting.Id;
            output[offset + 2] = (byte)(setting.Value >> 24);
            output[offset + 3] = (byte)(setting.Value >> 16);
            output[offset + 4] = (byte)(setting.Value >> 8);
            output[offset + 5] = (byte)setting.Value;
        }
    }
    
    /// <summary>
    /// Parses a PRIORITY payload.
    /// </summary>
    public static Http2Priority ParsePriority(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 5)
            throw new ArgumentException("PRIORITY payload must be 5 bytes");
        
        uint streamDependency = (uint)(payload[0] << 24 | payload[1] << 16 | payload[2] << 8 | payload[3]);
        bool exclusive = (streamDependency & 0x80000000) != 0;
        streamDependency &= 0x7FFFFFFF;
        byte weight = payload[4];
        
        return new Http2Priority
        {
            StreamDependency = streamDependency,
            Weight = weight,
            Exclusive = exclusive
        };
    }
    
    /// <summary>
    /// Encodes a PRIORITY payload.
    /// </summary>
    public static void EncodePriority(Span<byte> output, Http2Priority priority)
    {
        uint streamDep = priority.StreamDependency;
        if (priority.Exclusive)
            streamDep |= 0x80000000;
        
        output[0] = (byte)(streamDep >> 24);
        output[1] = (byte)(streamDep >> 16);
        output[2] = (byte)(streamDep >> 8);
        output[3] = (byte)streamDep;
        output[4] = priority.Weight;
    }
    
    /// <summary>
    /// Parses a WINDOW_UPDATE payload.
    /// </summary>
    public static uint ParseWindowUpdate(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 4)
            throw new ArgumentException("WINDOW_UPDATE payload must be 4 bytes");
        
        uint increment = (uint)(payload[0] << 24 | payload[1] << 16 | payload[2] << 8 | payload[3]);
        increment &= 0x7FFFFFFF;
        
        if (increment == 0)
            throw new InvalidDataException("WINDOW_UPDATE increment must not be zero");
        
        return increment;
    }
    
    /// <summary>
    /// Encodes a WINDOW_UPDATE payload.
    /// </summary>
    public static void EncodeWindowUpdate(Span<byte> output, uint increment)
    {
        if (increment == 0 || increment > 0x7FFFFFFF)
            throw new ArgumentException("Window increment must be 1-2^31-1");
        
        output[0] = (byte)(increment >> 24);
        output[1] = (byte)(increment >> 16);
        output[2] = (byte)(increment >> 8);
        output[3] = (byte)increment;
    }
    
    /// <summary>
    /// Parses a GOAWAY payload.
    /// </summary>
    public static (uint LastStreamId, Http2ErrorCode ErrorCode, ReadOnlyMemory<byte> DebugData) ParseGoAway(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8)
            throw new ArgumentException("GOAWAY payload too small");
        
        uint lastStreamId = (uint)(payload[0] << 24 | payload[1] << 16 | payload[2] << 8 | payload[2]);
        lastStreamId &= 0x7FFFFFFF;
        var errorCode = (Http2ErrorCode)(payload[4] << 24 | payload[5] << 16 | payload[6] << 8 | payload[7]);
        var debugData = payload.Length > 8 ? payload[8..].ToArray() : ReadOnlyMemory<byte>.Empty;
        
        return (lastStreamId, errorCode, debugData);
    }
    
    /// <summary>
    /// Parses a PING payload.
    /// </summary>
    public static ulong ParsePing(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 8)
            throw new ArgumentException("PING payload must be 8 bytes");
        
        return (ulong)(payload[0] << 56 | payload[1] << 48 | payload[2] << 40 | payload[3] << 32 |
                       payload[4] << 24 | payload[5] << 16 | payload[6] << 8 | payload[7]);
    }
    
    /// <summary>
    /// Parses a PUSH_PROMISE payload.
    /// </summary>
    public static (uint PromisedStreamId, ReadOnlyMemory<byte> HeaderBlock, int PadLength) ParsePushPromise(ReadOnlySpan<byte> payload, bool padded)
    {
        if (payload.Length < 4)
            throw new ArgumentException("PUSH_PROMISE payload too small");
        
        uint promisedStreamId = (uint)(payload[0] << 24 | payload[1] << 16 | payload[2] << 8 | payload[3]);
        promisedStreamId &= 0x7FFFFFFF;
        
        int offset = 4;
        int padLength = 0;
        
        if (padded)
        {
            if (payload.Length < 5)
                throw new ArgumentException("Padded PUSH_PROMISE too small");
            padLength = payload[4];
            offset = 5;
        }
        
        if (offset >= payload.Length)
            throw new ArgumentException("No header block in PUSH_PROMISE");
        
        var headerBlock = payload[offset..].ToArray();
        
        if (padded && padLength > 0)
        {
            if (headerBlock.Length < padLength)
                throw new ArgumentException("Invalid pad length");
            headerBlock = headerBlock[..^padLength];
        }
        
        return (promisedStreamId, headerBlock, padLength);
    }
    
    /// <summary>
    /// Parses an RST_STREAM payload.
    /// </summary>
    public static Http2ErrorCode ParseRstStream(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 4)
            throw new ArgumentException("RST_STREAM payload must be 4 bytes");
        
        return (Http2ErrorCode)(payload[0] << 24 | payload[1] << 16 | payload[2] << 8 | payload[3]);
    }
}
