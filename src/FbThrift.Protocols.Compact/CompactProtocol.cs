// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Protocols.Compact;

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

/// <summary>
/// Compact protocol writer (fbthrift CompactProtocolWriter equivalent).
/// </summary>
public sealed class CompactProtocolWriter
{
    private readonly ArrayBufferWriter<byte> _output;
    private int _lastFieldId = 0;
    private readonly StringBuilder _stringBuilder = new();
    private static readonly byte[] _protocolHeader = new byte[] { 0x82, 0x01, 0x00, 0x00, 0x00 }; // Protocol ID + version + seqId placeholder
    
    public CompactProtocolWriter()
    {
        _output = new ArrayBufferWriter<byte>(4096);
    }
    
    public CompactProtocolWriter(int initialCapacity)
    {
        _output = new ArrayBufferWriter<byte>(initialCapacity);
    }
    
    public ReadOnlyMemory<byte> WrittenMemory => _output.WrittenMemory;
    public ReadOnlySpan<byte> WrittenSpan => _output.WrittenSpan;
    
    public void Reset()
    {
        _output.Clear();
        _lastFieldId = 0;
    }
    
    // ===== Message Writing =====
    
    public void WriteMessageBegin(string name, TMessageType messageType, int seqId)
    {
        // Compact protocol message header:
        // [protocol id: 0x82] [version + type: 0x01 | (messageType << 5)] [seqId varint] [name string]
        
        // Protocol ID and version (0x82 = 10000010, version 1)
        _output.Write(new byte[] { 0x82, 0x01 });
        
        // Message type in upper 3 bits of next byte
        byte typeByte = (byte)(((byte)messageType << 5) & 0xE0);
        _output.Write(new byte[] { typeByte });
        
        // SeqId as varint (not ZigZag)
        var seqIdBytes = new byte[5];
        int seqIdBytesWritten = CompactEncoding.WriteVarint(seqIdBytes, (ulong)seqId);
        _output.Write(seqIdBytes[..seqIdBytesWritten]);
        
        // Method name as string
        WriteString(name);
    }
    
    public void WriteMessageEnd()
    {
        // Nothing to do for compact protocol
    }
    
    // ===== Struct Writing =====
    
    public void WriteStructBegin(string name)
    {
        // Struct begin is implicit in compact protocol
        _lastFieldId = 0;
    }
    
    public void WriteStructEnd()
    {
        // Nothing to do
    }
    
    // ===== Field Writing =====
    
    public void WriteFieldBegin(string name, TType fieldType, int fieldId)
    {
        byte compactType = CompactTypeMapping.GetCompactType(fieldType);
        if (compactType == 0 && fieldType != TType.Stop)
            throw new ArgumentException($"Unknown field type: {fieldType}");
        
        if (CompactFieldHeader.TryEncodeShortForm(fieldId, _lastFieldId, compactType, out byte header))
        {
            _output.Write(new byte[] { header });
        }
        else
        {
            // Long form
            var buffer = new byte[8];
            buffer[0] = (byte)(compactType & 0x0F);
            int varintBytes = CompactEncoding.WriteI16(buffer.AsSpan(1), (short)fieldId);
            _output.Write(buffer.AsSpan(0, 1 + varintBytes));
        }
        
        _lastFieldId = fieldId;
    }
    
    public void WriteFieldEnd()
    {
        // Nothing to do
    }
    
    public void WriteFieldStop()
    {
        _output.Write(new byte[] { CompactFieldHeader.StopByte });
    }
    
    // ===== Primitive Writing =====
    
    public void WriteBool(bool value)
    {
        _output.Write(new byte[] { value ? (byte)0x01 : (byte)0x02 });
    }
    
    public void WriteByte(sbyte value)
    {
        _output.Write(new byte[] { (byte)value });
    }
    
    public void WriteI16(short value)
    {
        var buffer = new byte[5];
        int bytesWritten = CompactEncoding.WriteI16(buffer, value);
        _output.Write(buffer.AsSpan(0, bytesWritten));
    }
    
    public void WriteI32(int value)
    {
        var buffer = new byte[5];
        int bytesWritten = CompactEncoding.WriteI32(buffer, value);
        _output.Write(buffer.AsSpan(0, bytesWritten));
    }
    
    public void WriteI64(long value)
    {
        var buffer = new byte[10];
        int bytesWritten = CompactEncoding.WriteI64(buffer, value);
        _output.Write(buffer.AsSpan(0, bytesWritten));
    }
    
    public void WriteDouble(double value)
    {
        var buffer = new byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(buffer, value);
        _output.Write(buffer);
    }
    
    public void WriteFloat(float value)
    {
        var buffer = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(buffer, value);
        _output.Write(buffer);
    }
    
    public void WriteString(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            // Empty string: size 0
            _output.Write(new byte[] { 0x00 });
            return;
        }
        
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteBinary(bytes);
    }
    
    public void WriteBinary(ReadOnlySpan<byte> value)
    {
        // Write size as varint
        var sizeBuffer = new byte[5];
        int sizeBytes = CompactEncoding.WriteSize(sizeBuffer, value.Length);
        _output.Write(sizeBuffer.AsSpan(0, sizeBytes));
        
        // Write bytes
        _output.Write(value);
    }
    
    public void WriteBinary(byte[] value)
    {
        WriteBinary(value.AsSpan());
    }
    
    // ===== Container Writing =====
    
    public void WriteListBegin(TType elemType, int size)
    {
        byte compactElemType = CompactTypeMapping.GetCompactType(elemType);
        var buffer = new byte[6];
        int bytesWritten = CompactContainerHeader.Encode(buffer, size, compactElemType);
        _output.Write(buffer.AsSpan(0, bytesWritten));
    }
    
    public void WriteListEnd()
    {
        // Nothing to do
    }
    
    public void WriteSetBegin(TType elemType, int size)
    {
        // Sets encoded same as lists
        WriteListBegin(elemType, size);
    }
    
    public void WriteSetEnd()
    {
        // Nothing to do
    }
    
    public void WriteMapBegin(TType keyType, TType valType, int size)
    {
        byte compactKeyType = CompactTypeMapping.GetCompactType(keyType);
        byte compactValType = CompactTypeMapping.GetCompactType(valType);
        
        // Map header: size + key type + val type
        var buffer = new byte[7];
        int sizeBytes = CompactEncoding.WriteSize(buffer, size);
        buffer[sizeBytes] = (byte)((compactKeyType << 4) | (compactValType & 0x0F));
        _output.Write(buffer.AsSpan(0, sizeBytes + 1));
    }
    
    public void WriteMapEnd()
    {
        // Nothing to do
    }
    
    public void WriteSetEnd()
    {
        // Nothing to do
    }
    
    // ===== Helpers =====
    
    public ReadOnlyMemory<byte> GetBuffer() => _output.WrittenMemory;
    public int Length => _output.WrittenCount;
    
    public void WriteToStream(Stream stream)
    {
        stream.Write(_output.WrittenSpan);
    }
    
    public byte[] ToArray() => _output.WrittenSpan.ToArray();
}

/// <summary>
/// Compact protocol reader (fbthrift CompactProtocolReader equivalent).
/// </summary>
public sealed class CompactProtocolReader
{
    private readonly ReadOnlySequence<byte> _input;
    private SequencePosition _position;
    private int _lastFieldId = 0;
    private readonly StringBuilder _stringBuilder = new();
    private int _stringLimit = ProtocolConstants.DefaultStringLimit;
    private int _containerLimit = ProtocolConstants.DefaultContainerLimit;
    
    public CompactProtocolReader(ReadOnlyMemory<byte> input)
    {
        _input = new ReadOnlySequence<byte>(input);
        _position = _input.Start;
    }
    
    public CompactProtocolReader(ReadOnlySequence<byte> input)
    {
        _input = input;
        _position = _input.Start;
    }
    
    public void SetStringLimit(int limit) => _stringLimit = limit;
    public void SetContainerLimit(int limit) => _containerLimit = limit;
    
    // ===== Message Reading =====
    
    public void ReadMessageBegin(out string name, out TMessageType messageType, out int seqId)
    {
        // Read protocol header: 0x82 0x01
        byte b1 = ReadByte();
        byte b2 = ReadByte();
        
        if (b1 != 0x82 || b2 != 0x01)
            throw new InvalidDataException($"Invalid compact protocol header: {b1:X2} {b2:X2}");
        
        // Read message type
        byte typeByte = ReadByte();
        messageType = (TMessageType)((typeByte >> 5) & 0x07);
        
        // Read seqId (varint, not ZigZag)
        seqId = (int)ReadVarint();
        
        // Read method name
        name = ReadString();
    }
    
    public void ReadMessageEnd()
    {
        // Nothing to do
    }
    
    // ===== Struct Reading =====
    
    public void ReadStructBegin(out string name)
    {
        name = string.Empty; // Compact doesn't encode struct name
        _lastFieldId = 0;
    }
    
    public void ReadStructEnd()
    {
        // Nothing to do
    }
    
    // ===== Field Reading =====
    
    public void ReadFieldBegin(out string name, out TType fieldType, out int fieldId)
    {
        name = string.Empty; // Compact doesn't encode field names
        
        if (TryPeekStop())
        {
            fieldType = TType.Stop;
            fieldId = 0;
            ReadByte(); // Consume stop byte
            return;
        }
        
        byte firstByte = ReadByte();
        byte type = (byte)(firstByte >> 4);
        byte delta = (byte)(firstByte & 0x0F);
        
        fieldType = CompactTypeMapping.GetTType(type);
        
        if (delta != 0)
        {
            // Short form
            fieldId = _lastFieldId + delta;
        }
        else
        {
            // Long form
            fieldId = ReadI16();
        }
        
        _lastFieldId = fieldId;
    }
    
    public void ReadFieldEnd()
    {
        // Nothing to do
    }
    
    // ===== Primitive Reading =====
    
    public bool ReadBool()
    {
        byte b = ReadByte();
        return b == 0x01;
    }
    
    public sbyte ReadByte()
    {
        return (sbyte)ReadByte();
    }
    
    public short ReadI16()
    {
        if (!CompactEncoding.TryReadI16(ReadRemainingSpan(), out short value, out int bytesRead))
            throw new InvalidDataException("Failed to read I16");
        
        Advance(bytesRead);
        return value;
    }
    
    public int ReadI32()
    {
        if (!CompactEncoding.TryReadI32(ReadRemainingSpan(), out int value, out int bytesRead))
            throw new InvalidDataException("Failed to read I32");
        
        Advance(bytesRead);
        return value;
    }
    
    public long ReadI64()
    {
        if (!CompactEncoding.TryReadI64(ReadRemainingSpan(), out long value, out int bytesRead))
            throw new InvalidDataException("Failed to read I64");
        
        Advance(bytesRead);
        return value;
    }
    
    public double ReadDouble()
    {
        var bytes = ReadBytes(8);
        return BinaryPrimitives.ReadDoubleLittleEndian(bytes);
    }
    
    public float ReadFloat()
    {
        var bytes = ReadBytes(4);
        return BinaryPrimitives.ReadSingleLittleEndian(bytes);
    }
    
    public string ReadString()
    {
        int size = ReadSize();
        if (size == 0)
            return string.Empty;
        
        if (size > _stringLimit)
            throw new InvalidDataException($"String size {size} exceeds limit {_stringLimit}");
        
        var bytes = ReadBytes(size);
        return Encoding.UTF8.GetString(bytes);
    }
    
    public ReadOnlyMemory<byte> ReadBinary()
    {
        int size = ReadSize();
        if (size == 0)
            return ReadOnlyMemory<byte>.Empty;
        
        var bytes = ReadBytes(size);
        return new ReadOnlyMemory<byte>(bytes.ToArray());
    }
    
    // ===== Container Reading =====
    
    public void ReadListBegin(out TType elemType, out int size)
    {
        ReadContainerHeader(out size, out byte elemType);
        elemType = CompactTypeMapping.GetTType(elemType);
    }
    
    public void ReadListEnd()
    {
        // Nothing to do
    }
    
    public void ReadSetBegin(out TType elemType, out int size)
    {
        ReadListBegin(out elemType, out size); // Sets encoded same as lists
    }
    
    public void ReadSetEnd()
    {
        // Nothing to do
    }
    
    public void ReadMapBegin(out TType keyType, out TType valType, out int size)
    {
        int size = ReadSize();
        
        if (_input.Length == 0)
            throw new InvalidDataException("Unexpected end of data reading map header");
        
        byte typeByte = ReadByte();
        byte keyType = (byte)(typeByte >> 4);
        byte valType = (byte)(typeByte & 0x0F);
        
        keyType = CompactTypeMapping.GetTType(keyType);
        valType = CompactTypeMapping.GetTType(valType);
    }
    
    public void ReadMapEnd()
    {
        // Nothing to do
    }
    
    public void ReadSetEnd()
    {
        // Nothing to do
    }
    
    // ===== Helpers =====
    
    private bool TryPeekStop()
    {
        var span = ReadRemainingSpan();
        return span.Length > 0 && span[0] == CompactFieldHeader.StopByte;
    }
    
    private void ReadContainerHeader(out int size, out byte elementType)
    {
        if (_input.Length == 0)
            throw new InvalidDataException("Unexpected end of data reading container header");
        
        byte firstByte = ReadByte();
        
        if ((firstByte & 0xF0) == 0xF0)
        {
            // Long form
            elementType = (byte)(firstByte & 0x0F);
            size = (int)ReadVarint();
        }
        else
        {
            // Short form
            elementType = (byte)(firstByte & 0x0F);
            size = (firstByte >> 4) & 0x0F;
        }
        
        if (size > _containerLimit)
            throw new InvalidDataException($"Container size {size} exceeds limit {_containerLimit}");
    }
    
    private int ReadSize()
    {
        return (int)ReadVarint();
    }
    
    private ulong ReadVarint()
    {
        ulong value = 0;
        int shift = 0;
        
        while (true)
        {
            byte b = ReadByte();
            value |= (ulong)(b & 0x7F) << shift;
            
            if ((b & 0x80) == 0)
                break;
            
            shift += 7;
            if (shift >= 64)
                throw new InvalidDataException("Varint too long");
        }
        
        return value;
    }
    
    private byte ReadByte()
    {
        if (_input.IsEmpty || _position == _input.End)
            throw new InvalidDataException("Unexpected end of data");
        
        byte b = _input.First.Span[_position.GetOffset(_input.First)];
        _position = _input.GetPosition(1, _position);
        return b;
    }
    
    private ReadOnlySpan<byte> ReadBytes(int count)
    {
        var span = ReadRemainingSpan();
        if (span.Length < count)
            throw new InvalidDataException($"Expected {count} bytes, got {span.Length}");
        
        var result = span[..count];
        Advance(count);
        return result;
    }
    
    private ReadOnlySpan<byte> ReadRemainingSpan()
    {
        if (_position == _input.End)
            return ReadOnlySpan<byte>.Empty;
        
        var segment = _input.GetSegment(_position);
        return segment.Span;
    }
    
    private void Advance(int count)
    {
        _position = _input.GetPosition(count, _position);
    }
}
