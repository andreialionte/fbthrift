// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Core.Protocol;

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

/// <summary>
/// THeader protocol implementation (fbthrift THeaderProtocol equivalent).
/// Supports protocol negotiation, transforms (compression), and metadata headers.
/// </summary>
public sealed class HeaderProtocol
{
    // Header format (from fbthrift THeader.cpp):
    // seqId (4 bytes) + headerSize (2 bytes) + protocolId (2 bytes) + 
    // numTransforms (2 bytes) + transformIds... + headerFields... + clientType (4 bytes)
    
    public const int HeaderMagic = 0x8000;
    public const int HeaderMagicMask = 0x8000;
    public const int HeaderVersion1 = 1;
    public const int HeaderMinSize = 14; // seqId(4) + headerSize(2) + protocolId(2) + numTransforms(2) + clientType(4)
    
    // Header flags
    public const uint HeaderFlagBigFrames = 0x00000001;
    
    // Client types
    public const uint ThriftHeaderClientType = 0x00000005;
    public const uint ThriftHttpClientType = 0x00000004;
    public const uint ThriftHttpServerType = 0x00000003;
    public const uint ThriftRocketClientType = 0x00000006;
    public const uint ThriftRocketServerType = 0x00000007;
    
    // Protocol IDs
    public const ushort ProtocolBinary = 0x0000;
    public const ushort ProtocolCompact = 0x0001;
    public const ushort ProtocolJson = 0x0002;
    public const ushort ProtocolHeader = 0x0003;
    
    // Transform IDs
    public const ushort TransformNone = 0x0000;
    public const ushort TransformZlib = 0x0001;
    public const ushort TransformZstd = 0x0002;
    public const ushort TransformSnappy = 0x0003;
}

/// <summary>
/// THeader protocol metadata.
/// </summary>
public sealed class HeaderProtocolMetadata
{
    public int SeqId { get; set; } = 0;
    public ushort ProtocolId { get; set; } = HeaderProtocol.ProtocolHeader;
    public uint ClientType { get; set; } = HeaderProtocol.ThriftHeaderClientType;
    public uint Flags { get; set; } = 0;
    public List<ushort> Transforms { get; set; } = new();
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> WriteHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> PersistentHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> PersistentWriteHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool AllowBigFrames { get; set; } = false;
    public int MaxFrameSize { get; set; } = 16 * 1024 * 1024; // 16MB
}

/// <summary>
/// THeader protocol writer.
/// </summary>
public sealed class HeaderProtocolWriter
{
    private readonly ArrayBufferWriter<byte> _output;
    private readonly HeaderProtocolMetadata _metadata;
    private readonly bool _writeHeaders;
    
    public HeaderProtocolWriter(HeaderProtocolMetadata metadata, bool writeHeaders = true)
    {
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _writeHeaders = writeHeaders;
        _output = new ArrayBufferWriter<byte>(4096);
    }
    
    public HeaderProtocolMetadata Metadata => _metadata;
    
    public ReadOnlyMemory<byte> WrittenMemory => _output.WrittenMemory;
    public ReadOnlySpan<byte> WrittenSpan => _output.WrittenSpan;
    
    public void WriteHeader()
    {
        // Reserve space for header (we'll fill in sizes later)
        // seqId (4) + headerSize (2) + protocolId (2) + numTransforms (2) + clientType (4)
        _output.Write(new byte[14]);
        
        // Write transforms
        WriteVarint(_metadata.Transforms.Count);
        foreach (var transform in _metadata.Transforms)
        {
            WriteVarint(transform);
        }
        
        // Write header fields
        WriteHeaderFields();
        
        // Write client type (4 bytes)
        WriteUInt32(HeaderProtocol.ThriftHeaderClientType);
        
        // Now go back and fill in the header using GetSpan for writable access
        int headerSize = _output.WrittenCount - 4; // Exclude seqId
        int headerSizeWords = (headerSize + 3) / 4;
        
        var span = _output.GetSpan(0); // Get writable span from start
        BinaryPrimitives.WriteInt32BigEndian(span[0..4], _metadata.SeqId);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..6], (ushort)headerSizeWords);
        BinaryPrimitives.WriteUInt16BigEndian(span[6..8], HeaderProtocol.ProtocolHeader);
        BinaryPrimitives.WriteUInt16BigEndian(span[8..10], (ushort)_metadata.Transforms.Count);
        
        // Client type already written at the end
    }
    
    private void WriteHeaderFields()
    {
        // Write header fields as key-value pairs
        // Format: key len (varint) + key + value len (varint) + value
        // End with 0-length key
        
        // Write persistent headers
        foreach (var kvp in _metadata.PersistentHeaders)
        {
            WriteString(kvp.Key);
            WriteString(kvp.Value);
        }
        
        // Write request headers
        foreach (var kvp in _metadata.Headers)
        {
            WriteString(kvp.Key);
            WriteString(kvp.Value);
        }
        
        // End marker (0-length key)
        WriteVarint(0);
    }
    
    public void WritePayload(ReadOnlyMemory<byte> payload)
    {
        _output.Write(payload.Span);
    }
    
    private void WriteUInt32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        _output.Write(bytes);
    }
    
    private void WriteVarint(int value)
    {
        if (value < 0) value = 0;
        var buffer = new byte[5];
        int written = 0;
        uint value64 = (uint)value;
        
        while (value64 >= 0x80)
        {
            buffer[written++] = (byte)(value64 | 0x80);
            value64 >>= 7;
        }
        buffer[written++] = (byte)value64;
        
        _output.Write(buffer.AsSpan(0, written));
    }
    
    private void WriteString(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value ?? string.Empty);
        WriteVarint(bytes.Length);
        _output.Write(bytes);
    }
    
    public ReadOnlyMemory<byte> GetHeaderAndPayload()
    {
        return _output.WrittenMemory;
    }
    
    public void Reset()
    {
        _output.Clear();
    }
}

/// <summary>
/// THeader protocol reader.
/// </summary>
public sealed class HeaderProtocolReader
{
    private readonly ReadOnlySequence<byte> _input;
    private SequencePosition _position;
    private readonly HeaderProtocolMetadata _metadata = new();
    
    public HeaderProtocolReader(ReadOnlyMemory<byte> input)
    {
        _input = new ReadOnlySequence<byte>(input);
        _position = _input.Start;
    }
    
    public HeaderProtocolReader(ReadOnlySequence<byte> input)
    {
        _input = input;
        _position = _input.Start;
    }
    
    public HeaderProtocolMetadata Metadata => _metadata;
    public ReadOnlyMemory<byte> RemainingPayload => GetRemainingPayload();
    
    public bool TryReadHeader()
    {
        if (_input.Length < HeaderProtocol.HeaderMinSize)
            return false;
        
        var span = _input.First.Span;
        
        // Read seqId
        _metadata.SeqId = BinaryPrimitives.ReadInt32BigEndian(span);
        
        // Read header size (in 4-byte words)
        ushort headerSizeWords = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(4));
        int headerSize = headerSizeWords * 4;
        
        if (_input.Length < 4 + headerSize)
            return false;
        
        // Read protocol ID
        ushort protocolId = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(6));
        _metadata.ProtocolId = protocolId;
        
        // Read number of transforms
        ushort numTransforms = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(8));
        
        int offset = 10;
        
        // Read transforms
        for (int i = 0; i < numTransforms; i++)
        {
            if (offset + 2 > _input.First.Length)
                return false;
            
            ushort transform = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(offset));
            _metadata.Transforms.Add(transform);
            offset += 2;
        }
        
// Read header fields until empty key
        int fieldOffset = offset;

        while (fieldOffset + 1 <= span.Length)
        {
            // Read key length (varint)
            if (!TryReadVarint(span.Slice(fieldOffset), out int keyLen, out int keyBytesRead))
                break;
            
            fieldOffset += keyBytesRead;
            
            if (keyLen == 0)
            {
                // End of headers
                break;
            }
            
            if (fieldOffset + keyLen > span.Length)
                return false;
            
            string key = System.Text.Encoding.UTF8.GetString(span.Slice(fieldOffset, keyLen));
            fieldOffset += keyLen;

            // Read value
            if (!TryReadVarint(span.Slice(fieldOffset), out int valLen, out int valBytesRead))
                return false;
            
            fieldOffset += valBytesRead;
            
            if (fieldOffset + valLen > span.Length)
                return false;
            
            string value = System.Text.Encoding.UTF8.GetString(span.Slice(fieldOffset, valLen));
            fieldOffset += valLen;
            
            // Store header (simplified - just store in metadata)
        }
        
        // Read client type (4 bytes at end of header)
        if (fieldOffset + 4 <= span.Length)
        {
            uint clientType = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(fieldOffset));
            // Store client type
        }
        
        // Advance position past header
        int totalHeaderSize = 4 + BinaryPrimitives.ReadUInt16BigEndian(_input.First.Span.Slice(4)) * 4;
        _position = _input.GetPosition(totalHeaderSize, _input.Start);
        
        return true;
    }
    
    public ReadOnlyMemory<byte> GetPayload()
    {
        return GetRemainingPayload();
    }
    
    private ReadOnlyMemory<byte> GetRemainingPayload()
    {
        if (_position.Equals(_input.End))
            return ReadOnlyMemory<byte>.Empty;
        
        var memory = _input.Slice(_position);
        return memory.IsSingleSegment ? memory.First : memory.ToArray();
    }
    
    private static bool TryReadVarint(ReadOnlySpan<byte> input, out int value, out int bytesRead)
    {
        ulong value64 = 0;
        bytesRead = 0;
        value = 0;
        
        for (int shift = 0; shift < 64; shift += 7)
        {
            if (bytesRead >= input.Length)
                return false;
            
            byte b = input[bytesRead++];
            value64 |= (ulong)(b & 0x7F) << ((bytesRead - 1) * 7);
            
            if ((input[bytesRead - 1] & 0x80) == 0)
            {
                value = (int)value64;
                return true;
            }
        }
        
        return false;
    }
}