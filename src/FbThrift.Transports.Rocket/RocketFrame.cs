// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Rocket;

using System.Buffers;
using System.Buffers.Binary;
using System.IO;

/// <summary>
/// Rocket frame header (6 bytes: 3 bytes length + 1 byte type + 2 bytes flags + 4 bytes stream ID = 10 bytes total).
/// </summary>
public readonly struct RocketFrameHeader
{
    public const int HeaderSize = 10;

    public readonly int Length;      // 24-bit length (payload + metadata)
    public readonly RocketFrameType Type;
    public readonly RocketFrameFlags Flags;
    public readonly int StreamId;    // 31-bit stream ID (0 for connection-level frames)

    public RocketFrameHeader(int length, RocketFrameType type, RocketFrameFlags flags, int streamId)
    {
        Length = length;
        Type = type;
        Flags = flags;
        StreamId = streamId;
    }

    /// <summary>
    /// Writes the frame header to a buffer writer.
    /// </summary>
    public void Write(IBufferWriter<byte> writer)
    {
        var span = writer.GetSpan(HeaderSize);
        
        // 24-bit length (big-endian)
        span[0] = (byte)(Length >> 16);
        span[1] = (byte)(Length >> 8);
        span[2] = (byte)Length;
        
        // 1 byte frame type
        span[3] = (byte)Type;
        
        // 2 bytes flags (big-endian)
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(4, 2), (ushort)Flags);
        
        // 4 bytes stream ID (big-endian, 31-bit)
        BinaryPrimitives.WriteInt32BigEndian(span.Slice(6, 4), StreamId);
        
        writer.Advance(HeaderSize);
    }

    /// <summary>
    /// Reads a frame header from a span.
    /// </summary>
    public static RocketFrameHeader Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
            throw new ArgumentException("Insufficient data for frame header");

        int length = (data[0] << 16) | (data[1] << 8) | data[2];
        var type = (RocketFrameType)data[3];
        var flags = (RocketFrameFlags)BinaryPrimitives.ReadUInt16BigEndian(data.Slice(4, 2));
        int streamId = BinaryPrimitives.ReadInt32BigEndian(data.Slice(6, 4)) & 0x7FFFFFFF;

        return new RocketFrameHeader(length, type, flags, streamId);
    }
}

/// <summary>
/// Rocket frame with header, metadata, and data.
/// </summary>
public sealed class RocketFrame
{
    public RocketFrameHeader Header { get; }
    public ReadOnlyMemory<byte> Metadata { get; }
    public ReadOnlyMemory<byte> Data { get; }

    public RocketFrame(RocketFrameHeader header, ReadOnlyMemory<byte> metadata, ReadOnlyMemory<byte> data)
    {
        Header = header;
        Metadata = metadata;
        Data = data;
    }

    public bool HasMetadata => (Header.Flags & RocketFrameFlags.Metadata) != 0;
    public bool IsComplete => (Header.Flags & RocketFrameFlags.Complete) != 0;
    public bool HasFollows => (Header.Flags & RocketFrameFlags.Follows) != 0;
    public bool IsNext => (Header.Flags & RocketFrameFlags.Next) != 0;
    public bool IsIgnore => (Header.Flags & RocketFrameFlags.Ignore) != 0;

    /// <summary>
    /// Writes the complete frame to a buffer writer.
    /// </summary>
    public void Write(IBufferWriter<byte> writer)
    {
        Header.Write(writer);
        if (Metadata.Length > 0)
            writer.Write(Metadata.Span);
        if (Data.Length > 0)
            writer.Write(Data.Span);
    }

    /// <summary>
    /// Creates a REQUEST_RESPONSE frame.
    /// </summary>
    public static RocketFrame CreateRequestResponse(int streamId, ReadOnlyMemory<byte> metadata, ReadOnlyMemory<byte> data, bool complete = true)
    {
        var flags = RocketFrameFlags.Metadata;
        if (complete) flags |= RocketFrameFlags.Complete;
        
        var header = new RocketFrameHeader(
            metadata.Length + data.Length,
            RocketFrameType.RequestResponse,
            flags,
            streamId);
        
        return new RocketFrame(header, metadata, data);
    }

    /// <summary>
    /// Creates a STREAM frame.
    /// </summary>
    public static RocketFrame CreateStream(int streamId, ReadOnlyMemory<byte> metadata, ReadOnlyMemory<byte> data, bool complete = false)
    {
        var flags = RocketFrameFlags.Metadata;
        if (complete) flags |= RocketFrameFlags.Complete;
        
        var header = new RocketFrameHeader(
            metadata.Length + data.Length,
            RocketFrameType.RequestStream,
            flags,
            streamId);
        
        return new RocketFrame(header, metadata, data);
    }

    /// <summary>
    /// Creates a PAYLOAD frame (stream chunk).
    /// </summary>
    public static RocketFrame CreatePayload(int streamId, ReadOnlyMemory<byte> metadata, ReadOnlyMemory<byte> data, bool complete = false, bool follows = false)
    {
        var flags = RocketFrameFlags.Metadata;
        if (complete) flags |= RocketFrameFlags.Complete;
        if (follows) flags |= RocketFrameFlags.Follows;
        
        var header = new RocketFrameHeader(
            metadata.Length + data.Length,
            RocketFrameType.Payload,
            flags,
            streamId);
        
        return new RocketFrame(header, metadata, data);
    }

    /// <summary>
    /// Creates a CANCEL frame.
    /// </summary>
    public static RocketFrame CreateCancel(int streamId)
    {
        var header = new RocketFrameHeader(
            0,
            RocketFrameType.Cancel,
            RocketFrameFlags.None,
            streamId);
        
        return new RocketFrame(header, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty);
    }

    /// <summary>
    /// Creates an ERROR frame.
    /// </summary>
    public static RocketFrame CreateError(int streamId, RocketErrorCode errorCode, string message = "")
    {
        var data = System.Text.Encoding.UTF8.GetBytes(message);
        
        var header = new RocketFrameHeader(
            data.Length,
            RocketFrameType.Error,
            RocketFrameFlags.None,
            streamId);
        
        return new RocketFrame(header, ReadOnlyMemory<byte>.Empty, data);
    }
}
