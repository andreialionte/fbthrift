// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Rocket;

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;

/// <summary>
/// Shared buffer pool for frame encoding/decoding.
/// </summary>
public static class RocketFramePool
{
    public static readonly ArrayPool<byte> Shared = ArrayPool<byte>.Shared;
}

/// <summary>
/// SETUP frame (first frame on connection).
/// </summary>
public sealed class SetupFrame
{
    public uint Version { get; init; } = 1;
    public uint KeepAliveTimeoutMs { get; init; } = 0;
    public uint MaxLifetimeMs { get; init; } = 0;
    public Dictionary<string, string> Parameters { get; init; } = new();
    public string Token { get; init; } = string.Empty;
    
    public static RocketFrame Decode(RocketFrameHeader header, ReadOnlyMemory<byte> payload)
    {
        var reader = new SetupFrameReader(payload);
        return reader.Read();
    }
    
    public RocketFrame ToRocketFrame(int streamId)
    {
        var buffer = RocketFramePool.Shared.Rent(512);
        try
        {
            var writer = new ArrayBufferWriter<byte>(buffer);
            var header = new RocketFrameHeader(
                0, // Will be updated after writing metadata
                RocketFrameType.Setup,
                RocketFrameFlags.Metadata,
                streamId);
            
            var headerSpan = writer.GetSpan(RocketFrameHeader.HeaderSize);
            writer.Advance(RocketFrameHeader.HeaderSize);
            
            var metadataWriter = new SetupFrameWriter(writer);
            metadataWriter.WriteVersion(Version);
            metadataWriter.WriteKeepAlive(KeepAliveTimeoutMs);
            metadataWriter.WriteMaxLifetime(MaxLifetimeMs);
            metadataWriter.WriteParameters(Parameters);
            metadataWriter.WriteToken(Token);
            
            int metadataLength = metadataWriter.Written;
            
            // Calculate total length and write actual header
            var totalLength = metadataLength;
            var frameHeader = new RocketFrameHeader(
                totalLength,
                RocketFrameType.Setup,
                RocketFrameFlags.Metadata,
                streamId);
            
            var finalSpan = writer.GetSpan(RocketFrameHeader.HeaderSize);
            frameHeader.Write(finalSpan);
            writer.Advance(RocketFrameHeader.HeaderSize);
            
            return new RocketFrame(frameHeader, writer.WrittenMemory.Slice(RocketFrameHeader.HeaderSize), ReadOnlyMemory<byte>.Empty);
        }
        finally
        {
            RocketFramePool.Shared.Return(buffer);
        }
    }
}

/// <summary>
/// SETUP frame reader.
/// </summary>
public sealed class SetupFrameReader
{
    private readonly ReadOnlySequence<byte> _input;
    private SequencePosition _position;
    
    public SetupFrameReader(ReadOnlyMemory<byte> input)
    {
        _input = new ReadOnlySequence<byte>(input);
        _position = _input.Start;
    }
    
    public SetupFrame Read()
    {
        var frame = new SetupFrame();
        
        if (!TryReadVarint(out uint version))
            throw new InvalidDataException("Failed to read version");
        frame.Version = version;
        
        if (!TryReadVarint(out uint keepAlive))
            throw new InvalidDataException("Failed to read keepalive");
        frame.KeepAliveTimeoutMs = keepAlive;
        
        if (!TryReadVarint(out uint maxLifetime))
            throw new InvalidDataException("Failed to read max lifetime");
        frame.MaxLifetimeMs = maxLifetime;
        
        if (!TryReadVarint(out uint paramCount))
            throw new InvalidDataException("Failed to read param count");
        
        var parameters = new Dictionary<string, string>();
        for (uint i = 0; i < paramCount; i++)
        {
            string key = ReadString();
            string value = ReadString();
            parameters[key] = value;
        }
        frame.Parameters = parameters;
        
        frame.Token = ReadString();
        
        return frame;
    }
    
    private bool TryReadVarint(out uint value)
    {
        value = 0;
        int shift = 0;
        
        while (true)
        {
            if (_position == _input.End)
                return false;
            
            byte b = ReadByte();
            value |= (ulong)(b & 0x7F) << shift;
            
            if ((b & 0x80) == 0)
                return true;
            
            shift += 7;
            if (shift >= 64)
                return false;
        }
    }
    
    private byte ReadByte()
    {
        var segment = _input.GetSegment(_position);
        byte b = segment.Span[0];
        _position = _input.GetPosition(1, _position);
        return b;
    }
    
    private string ReadString()
    {
        if (!TryReadVarint(out uint length))
            throw new InvalidDataException("Failed to read string length");
        
        if (length == 0)
            return string.Empty;
        
        var bytes = new byte[length];
        var span = ReadBytes(length);
        span.CopyTo(bytes);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }
    
    private ReadOnlySpan<byte> ReadBytes(int count)
    {
        var result = new byte[count];
        int copied = 0;
        
        while (copied < count)
        {
            var segment = _input.GetSegment(_position);
            int toCopy = Math.Min(count - copied, segment.Length);
            segment.Span.Slice(0, toCopy).CopyTo(new Memory<byte>(new byte[count]).Span.Slice(copied));
            copied += toCopy;
            _position = _input.GetPosition(toCopy, _position);
        }
        
        return new ReadOnlySpan<byte>(new byte[count]); // Simplified
    }
}

/// <summary>
/// SETUP frame writer.
/// </summary>
public sealed class SetupFrameWriter
{
    private readonly ArrayBufferWriter<byte> _output;
    
    public SetupFrameWriter(int initialCapacity = 256)
    {
        _output = new ArrayBufferWriter<byte>(initialCapacity);
    }
    
    public int Written => _output.WrittenCount;
    
    public void WriteVersion(uint version)
    {
        WriteVarint(version);
    }
    
    public void WriteKeepAlive(uint ms)
    {
        WriteVarint(ms);
    }
    
    public void WriteMaxLifetime(uint ms)
    {
        WriteVarint(ms);
    }
    
    public void WriteParameters(Dictionary<string, string> parameters)
    {
        WriteVarint((uint)parameters.Count);
        foreach (var kvp in parameters)
        {
            WriteString(kvp.Key);
            WriteString(kvp.Value);
        }
    }
    
    public void WriteToken(string token)
    {
        WriteString(token);
    }
    
    private void WriteVarint(uint value)
    {
        while (value >= 0x80)
        {
            _output.Write(new byte[] { (byte)(value | 0x80) });
            value >>= 7;
        }
        _output.Write(new byte[] { (byte)value });
    }
    
    private void WriteString(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            _output.Write(new byte[] { 0 });
            return;
        }
        
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        WriteVarint((uint)bytes.Length);
        _output.Write(bytes);
    }
}

/// <summary>
/// LEASE frame.
/// </summary>
public sealed class LeaseFrame
{
    public uint TtlMs { get; set; }
    public uint NumberOfRequests { get; set; }
    
    public static RocketFrame Decode(RocketFrameHeader header, ReadOnlyMemory<byte> payload)
    {
        return new RocketFrame(header, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty);
    }
    
    public RocketFrame ToRocketFrame(int streamId)
    {
        var header = new RocketFrameHeader(0, RocketFrameType.Lease, RocketFrameFlags.None, streamId);
        return new RocketFrame(header, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty);
    }
}

/// <summary>
/// KEEPALIVE frame.
/// </summary>
public sealed class KeepAliveFrame
{
    public uint LastReceivedPosition { get; set; }
    
    public static RocketFrame Decode(RocketFrameHeader header, ReadOnlyMemory<byte> payload)
    {
        return new RocketFrame(header, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty);
    }
    
    public RocketFrame ToRocketFrame(int streamId)
    {
        var header = new RocketFrameHeader(0, RocketFrameType.KeepAlive, RocketFrameFlags.None, streamId);
        return new RocketFrame(header, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty);
    }
}

/// <summary>
/// REQUEST_RESPONSE frame.
/// </summary>
public sealed class RequestResponseFrame
{
    public static RocketFrame Decode(RocketFrameHeader header, ReadOnlyMemory<byte> payload)
    {
        return new RocketFrame(header, ReadOnlyMemory<byte>.Empty, payload);
    }
    
    public static RocketFrame Create(int streamId, ReadOnlyMemory<byte> metadata, ReadOnlyMemory<byte> data, bool complete = true)
    {
        var flags = RocketFrameFlags.Metadata;
        if (complete) flags |= RocketFrameFlags.Complete;
        
        var totalLength = metadata.Length + data.Length;
        var header = new RocketFrameHeader(totalLength, RocketFrameType.RequestResponse, flags, streamId);
        return new RocketFrame(header, metadata, data);
    }
}

/// <summary>
/// REQUEST_FNF frame.
/// </summary>
public sealed class RequestFnfFrame
{
    public static RocketFrame Decode(RocketFrameHeader header, ReadOnlyMemory<byte> payload)
    {
        return new RocketFrame(header, ReadOnlyMemory<byte>.Empty, payload);
    }
    
    public static RocketFrame Create(int streamId, ReadOnlyMemory<byte> metadata, ReadOnlyMemory<byte> data)
    {
        var flags = RocketFrameFlags.Metadata;
        
        var totalLength = metadata.Length + data.Length;
        var header = new RocketFrameHeader(totalLength, RocketFrameType.RequestFnf, flags, streamId);
        return new RocketFrame(header, metadata, data);
    }
}

/// <summary>
/// REQUEST_STREAM frame.
/// </summary>
public sealed class RequestStreamFrame
{
    public int InitialRequestN { get; set; } = 0x7FFFFFFF;
    
    public static RocketFrame Decode(RocketFrameHeader header, ReadOnlyMemory<byte> payload)
    {
        return new RocketFrame(header, ReadOnlyMemory<byte>.Empty, payload);
    }
    
    public static RocketFrame Create(int streamId, ReadOnlyMemory<byte> metadata, ReadOnlyMemory<byte> data, bool complete = false)
    {
        var flags = RocketFrameFlags.Metadata;
        if (complete) flags |= RocketFrameFlags.Complete;
        
        var totalLength = metadata.Length + data.Length;
        var header = new RocketFrameHeader(totalLength, RocketFrameType.RequestStream, flags, streamId);
        return new RocketFrame(header, metadata, data);
    }
}

/// <summary>
/// REQUEST_CHANNEL frame.
/// </summary>
public sealed class RequestChannelFrame
{
    public int InitialRequestN { get; set; } = 0x7FFFFFFF;
    
    public static RocketFrame Decode(RocketFrameHeader header, ReadOnlyMemory<byte> payload)
    {
        return new RocketFrame(header, ReadOnlyMemory<byte>.Empty, payload);
    }
    
    public static RocketFrame Create(int streamId, ReadOnlyMemory<byte> metadata, ReadOnlyMemory<byte> data, bool complete = false)
    {
        var flags = RocketFrameFlags.Metadata;
        if (complete) flags |= RocketFrameFlags.Complete;
        
        var totalLength = metadata.Length + data.Length;
        var header = new RocketFrameHeader(totalLength, RocketFrameType.RequestChannel, flags, streamId);
        return new RocketFrame(header, metadata, data);
    }
}

/// <summary>
/// REQUEST_BIDI frame.
/// </summary>
public sealed class RequestBidiFrame
{
    public int InitialRequestN { get; set; } = 0x7FFFFFFF;
    
    public static RocketFrame Decode(RocketFrameHeader header, ReadOnlyMemory<byte> payload)
    {
        return new RocketFrame(header, ReadOnlyMemory<byte>.Empty, payload);
    }
    
    public static RocketFrame Create(int streamId, ReadOnlyMemory<byte> metadata, ReadOnlyMemory<byte> data, bool complete = false)
    {
        var flags = RocketFrameFlags.Metadata;
        if (complete) flags |= RocketFrameFlags.Complete;
        
        var totalLength = metadata.Length + data.Length;
        var header = new RocketFrameHeader(totalLength, RocketFrameType.RequestBidi, flags, streamId);
        return new RocketFrame(header, metadata, data);
    }
}

/// <summary>
/// CANCEL frame.
/// </summary>
public sealed class CancelFrame
{
    public static RocketFrame Decode(RocketFrameHeader header, ReadOnlyMemory<byte> payload)
    {
        return new RocketFrame(header, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty);
    }
    
    public static RocketFrame Create(int streamId)
    {
        var header = new RocketFrameHeader(0, RocketFrameType.Cancel, RocketFrameFlags.None, streamId);
        return new RocketFrame(header, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty);
    }
}

/// <summary>
/// PAYLOAD frame.
/// </summary>
public sealed class PayloadFrame
{
    public static RocketFrame Decode(RocketFrameHeader header, ReadOnlyMemory<byte> payload)
    {
        return new RocketFrame(header, ReadOnlyMemory<byte>.Empty, payload);
    }
    
    public static RocketFrame Create(int streamId, ReadOnlyMemory<byte> metadata, ReadOnlyMemory<byte> data, bool complete = false, bool follows = false)
    {
        var flags = RocketFrameFlags.Metadata;
        if (complete) flags |= RocketFrameFlags.Complete;
        if (follows) flags |= RocketFrameFlags.Follows;
        
        var totalLength = metadata.Length + data.Length;
        var header = new RocketFrameHeader(totalLength, RocketFrameType.Payload, flags, streamId);
        return new RocketFrame(header, metadata, data);
    }
    
    public static RocketFrame CreateChunk(int streamId, ReadOnlyMemory<byte> data, bool complete = false, bool follows = false)
    {
        var totalLength = data.Length;
        var flags = RocketFrameFlags.Metadata;
        if (complete) flags |= RocketFrameFlags.Complete;
        if (follows) flags |= RocketFrameFlags.Follows;
        
        var header = new RocketFrameHeader(totalLength, RocketFrameType.Payload, flags, streamId);
        return new RocketFrame(header, ReadOnlyMemory<byte>.Empty, data);
    }
}

/// <summary>
/// ERROR frame.
/// </summary>
public sealed class ErrorFrame
{
    public RocketErrorCode ErrorCode { get; set; }
    public string Message { get; set; } = string.Empty;
    
    public static RocketFrame Decode(RocketFrameHeader header, ReadOnlyMemory<byte> payload)
    {
        return new RocketFrame(header, ReadOnlyMemory<byte>.Empty, payload);
    }
    
    public static RocketFrame Create(int streamId, RocketErrorCode errorCode, string message = "")
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

/// <summary>
/// METADATA_PUSH frame.
/// </summary>
public sealed class MetadataPushFrame
{
    public static RocketFrame Decode(RocketFrameHeader header, ReadOnlyMemory<byte> payload)
    {
        return new RocketFrame(header, ReadOnlyMemory<byte>.Empty, payload);
    }
    
    public static RocketFrame Create(int streamId, ReadOnlyMemory<byte> metadata)
    {
        var header = new RocketFrameHeader(metadata.Length, RocketFrameType.MetadataPush, RocketFrameFlags.Metadata, 0);
        return new RocketFrame(header, metadata, ReadOnlyMemory<byte>.Empty);
    }
}

/// <summary>
/// RESUME frame.
/// </summary>
public sealed class ResumeFrame
{
    public string Token { get; set; } = string.Empty;
    public Dictionary<string, string> Parameters { get; set; } = new();
    
    public static RocketFrame Decode(RocketFrameHeader header, ReadOnlyMemory<byte> payload)
    {
        return new RocketFrame(header, ReadOnlyMemory<byte>.Empty, payload);
    }
    
    public RocketFrame ToRocketFrame(int streamId)
    {
        var header = new RocketFrameHeader(0, RocketFrameType.Resume, RocketFrameFlags.Metadata, streamId);
        // Would need to serialize token and parameters
        return new RocketFrame(header, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty);
    }
}

/// <summary>
/// RESUME_OK frame.
/// </summary>
public sealed class ResumeOkFrame
{
    public static RocketFrame Decode(RocketFrameHeader header, ReadOnlyMemory<byte> payload)
    {
        return new RocketFrame(header, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty);
    }
    
    public static RocketFrame Create(int streamId)
    {
        var header = new RocketFrameHeader(0, RocketFrameType.ResumeOk, RocketFrameFlags.None, streamId);
        return new RocketFrame(header, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty);
    }
}

/// <summary>
/// EXT frame for extensions.
/// </summary>
public sealed class ExtFrame
{
    public uint ExtensionType { get; set; }
    
    public static RocketFrame Decode(RocketFrameHeader header, ReadOnlyMemory<byte> payload)
    {
        return new RocketFrame(header, ReadOnlyMemory<byte>.Empty, payload);
    }
    
    public RocketFrame ToRocketFrame(int streamId)
    {
        var header = new RocketFrameHeader(0, RocketFrameType.Ext, RocketFrameFlags.None, streamId);
        return new RocketFrame(header, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty);
    }
}

/// <summary>
/// Rocket frame codec for encoding/decoding frames.
/// </summary>
public static class RocketFrameCodec
{
    public static RocketFrame Decode(ReadOnlySpan<byte> data)
    {
        return RocketFrame.Decode(data);
    }
    
    public static int Encode(RocketFrame frame, IBufferWriter<byte> writer)
    {
        frame.WriteTo(writer);
        return writer.WrittenCount;
    }
    
    public static bool TryDecodeFrame(ReadOnlySequence<byte> buffer, out RocketFrame frame, out SequencePosition nextPosition)
    {
        if (buffer.Length < RocketFrameHeader.Size)
        {
            frame = null!;
            nextPosition = default;
            return false;
        }
        
        var header = RocketFrameHeader.Decode(buffer.FirstSpan);
        int totalSize = RocketFrameHeader.Size;
        
        if (buffer.Length < totalSize)
        {
            return false;
        }
        
        var frame = RocketFrame.Decode(buffer.FirstSpan);
        nextPosition = buffer.GetPosition(RocketFrameHeader.Size, buffer.Start);
        return true;
    }
    
    /// <summary>
    /// Generates a connection ID.
    /// </summary>
    public static byte[] GenerateConnectionId(int length = 8)
    {
        var id = new byte[length];
        using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
        rng.GetBytes(id);
        return id;
    }
}
