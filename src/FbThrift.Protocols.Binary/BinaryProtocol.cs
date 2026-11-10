// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Protocols.Binary;

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

/// <summary>
/// Binary protocol writer (fbthrift BinaryProtocolWriter equivalent).
/// Supports framed (with 4-byte length prefix) and unframed modes.
/// </summary>
public sealed class BinaryProtocolWriter
{
    private readonly ArrayBufferWriter<byte> _output;
    private readonly bool _strictRead;
    private readonly bool _strictWrite;
    
    public const int Version1 = 0x80010000;
    public const int VersionMask = 0xFFFF0000;
    public const int TypeMask = 0x000000FF;
    public const int TypeShift = 0;
    
    public BinaryProtocolWriter(bool strictRead = true, bool strictWrite = true)
    {
        _output = new ArrayBufferWriter<byte>(4096);
        _strictRead = strictRead;
        _strictWrite = strictWrite;
    }
    
    public BinaryProtocolWriter(int initialCapacity, bool strictRead = true, bool strictWrite = true)
    {
        _output = new ArrayBufferWriter<byte>(initialCapacity);
        _strictRead = strictRead;
        _strictWrite = strictWrite;
    }
    
    public ReadOnlyMemory<byte> WrittenMemory => _output.WrittenMemory;
    public ReadOnlySpan<byte> WrittenSpan => _output.WrittenSpan;
    
    public void Reset()
    {
        _output.Clear();
    }
    
    // ===== Message Writing =====
    
    public void WriteMessageBegin(string name, TMessageType messageType, int seqId)
    {
        if (_strictWrite)
        {
            // Write version + message type
            _output.Write(BinaryPrimitives.WriteInt32BigEndian(new byte[4], Version1 | (int)messageType));
        }
        else
        {
            _output.Write(BinaryPrimitives.WriteInt32BigEndian(new byte[4], (int)messageType));
        }
        
        // Write seqId
        var seqIdBytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(seqIdBytes, seqId);
        _output.Write(seqIdBytes);
        
        // Write method name
        WriteString(name);
    }
    
    public void WriteMessageEnd()
    {
        // Nothing to do
    }
    
    // ===== Struct Writing =====
    
    public void WriteStructBegin(string name)
    {
        // Nothing to do for binary protocol
    }
    
    public void WriteStructEnd()
    {
        // Nothing to do
    }
    
    // ===== Field Writing =====
    
    public void WriteFieldBegin(string name, TType fieldType, int fieldId)
    {
        _output.Write(new byte[] { (byte)fieldType });
        
        var fieldIdBytes = new byte[2];
        BinaryPrimitives.WriteInt16BigEndian(fieldIdBytes, (short)fieldId);
        _output.Write(fieldIdBytes);
    }
    
    public void WriteFieldEnd()
    {
        // Nothing to do
    }
    
    public void WriteFieldStop()
    {
        _output.Write(new byte[] { (byte)TType.Stop });
    }
    
    // ===== Primitive Writing =====
    
    public void WriteBool(bool value)
    {
        _output.Write(new byte[] { value ? (byte)1 : (byte)0 });
    }
    
    public void WriteByte(sbyte value)
    {
        _output.Write(new byte[] { (byte)value });
    }
    
    public void WriteI16(short value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteInt16BigEndian(bytes, value);
        _output.Write(bytes);
    }
    
    public void WriteI32(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        _output.Write(bytes);
    }
    
    public void WriteI64(long value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        _output.Write(bytes);
    }
    
    public void WriteDouble(double value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(bytes, value);
        _output.Write(bytes);
    }
    
    public void WriteFloat(float value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteSingleBigEndian(bytes, value);
        _output.Write(bytes);
    }
    
    public void WriteString(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            WriteI32(0);
            return;
        }
        
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteI32(bytes.Length);
        _output.Write(bytes);
    }
    
    public void WriteBinary(ReadOnlySpan<byte> value)
    {
        WriteI32(value.Length);
        _output.Write(value);
    }
    
    public void WriteBinary(byte[] value)
    {
        WriteBinary(value.AsSpan());
    }
    
    // ===== Container Writing =====
    
    public void WriteListBegin(TType elemType, int size)
    {
        _output.Write(new byte[] { (byte)elemType });
        WriteI32(size);
    }
    
    public void WriteListEnd()
    {
        // Nothing to do
    }
    
    public void WriteSetBegin(TType elemType, int size)
    {
        WriteListBegin(elemType, size); // Sets encoded same as lists
    }
    
    public void WriteSetEnd()
    {
        // Nothing to do
    }
    
    public void WriteMapBegin(TType keyType, TType valType, int size)
    {
        _output.Write(new byte[] { (byte)keyType, (byte)valType });
        WriteI32(size);
    }
    
    public void WriteMapEnd()
    {
        // Nothing to do
    }
    
    public void WriteStructBegin(string name)
    {
        // Nothing to do
    }
    
    public void WriteStructEnd()
    {
        // Nothing to do
    }
    
    public void WriteFieldBegin(string name, TType fieldType, int fieldId)
    {
        _output.Write(new byte[] { (byte)fieldType });
        
        var fieldIdBytes = new byte[2];
        BinaryPrimitives.WriteInt16BigEndian(fieldIdBytes, (short)fieldId);
        _output.Write(fieldIdBytes);
    }
    
    public void WriteFieldEnd()
    {
        // Nothing to do
    }
    
    public void WriteFieldStop()
    {
        _output.Write(new byte[] { (byte)TType.Stop });
    }
    
    // ===== Helpers =====
    
    public ReadOnlyMemory<byte> WrittenMemory => throw new NotImplementedException("Use BinaryProtocolReader with ArrayBufferWriter");
    public int Length => 0;
    
    public byte[] ToArray()
    {
        return _output.WrittenSpan.ToArray();
    }
    
    public void WriteToStream(Stream stream)
    {
        stream.Write(_output.WrittenSpan);
    }
}

/// <summary>
/// Binary protocol reader (fbthrift BinaryProtocolReader equivalent).
/// Supports framed (with 4-byte length prefix) and unframed modes.
/// </summary>
public sealed class BinaryProtocolReader
{
    private readonly ReadOnlySequence<byte> _input;
    private SequencePosition _position;
    private readonly bool _strictRead;
    
    public BinaryProtocolReader(ReadOnlyMemory<byte> input, bool strictRead = true)
    {
        _input = new ReadOnlySequence<byte>(input);
        _position = _input.Start;
        _strictRead = strictRead;
    }
    
    public BinaryProtocolReader(ReadOnlySequence<byte> input, bool strictRead = true)
    {
        _input = input;
        _position = _input.Start;
        _strictRead = strictRead;
    }
    
    public void ReadMessageBegin(out string name, out TMessageType messageType, out int seqId)
    {
        int versionAndType = ReadI32();
        
        if (_strictRead)
        {
            int version = versionAndType & 0xFFFF0000;
            if (version != BinaryProtocolWriter.Version1)
                throw new InvalidDataException($"Invalid binary protocol version: {version:X8}");
            
            messageType = (TMessageType)(versionAndType & 0x000000FF);
        }
        else
        {
            messageType = (TMessageType)(versionAndType & 0x000000FF);
        }
        
        seqId = ReadI32();
        name = ReadString();
    }
    
    public void ReadMessageEnd()
    {
        // Nothing to do
    }
    
    public void ReadStructBegin(out string name)
    {
        name = string.Empty;
    }
    
    public void ReadStructEnd()
    {
        // Nothing to do
    }
    
    public void ReadFieldBegin(out string name, out TType fieldType, out int fieldId)
    {
        name = string.Empty;
        fieldType = (TType)ReadByte();
        
        if (fieldType == TType.Stop)
        {
            fieldId = 0;
            return;
        }
        
        fieldId = ReadI16();
    }
    
    public void ReadFieldEnd()
    {
        // Nothing to do
    }
    
    public bool ReadBool()
    {
        return ReadByte() != 0;
    }
    
    public sbyte ReadByte()
    {
        return (sbyte)ReadByte();
    }
    
    public short ReadI16()
    {
        var bytes = ReadBytes(2);
        return BinaryPrimitives.ReadInt16BigEndian(bytes);
    }
    
    public int ReadI32()
    {
        var bytes = ReadBytes(4);
        return BinaryPrimitives.ReadInt32BigEndian(bytes);
    }
    
    public long ReadI64()
    {
        var bytes = ReadBytes(8);
        return BinaryPrimitives.ReadInt64BigEndian(bytes);
    }
    
    public double ReadDouble()
    {
        var bytes = ReadBytes(8);
        return BinaryPrimitives.ReadDoubleBigEndian(bytes);
    }
    
    public float ReadFloat()
    {
        var bytes = ReadBytes(4);
        return BinaryPrimitives.ReadSingleBigEndian(bytes);
    }
    
    public string ReadString()
    {
        int size = ReadI32();
        if (size < 0)
            throw new InvalidDataException("Negative string length");
        
        if (size == 0)
            return string.Empty;
        
        var bytes = ReadBytes(size);
        return System.Text.Encoding.UTF8.GetString(ReadBytes(size));
    }
    
    public ReadOnlyMemory<byte> ReadBinary()
    {
        int size = ReadI32();
        if (size < 0)
            throw new InvalidDataException("Negative binary length");
        
        if (size == 0)
            return ReadOnlyMemory<byte>.Empty;
        
        var bytes = ReadBytes(size);
        return new ReadOnlyMemory<byte>(bytes.ToArray());
    }
    
    public void ReadListBegin(out TType elemType, out int size)
    {
        elemType = (TType)ReadByte();
        size = ReadI32();
    }
    
    public void ReadListEnd()
    {
        // Nothing to do
    }
    
    public void ReadSetBegin(out TType elemType, out int size)
    {
        elemType = ReadByte();
        size = ReadI32();
    }
    
    public void ReadSetEnd()
    {
        // Nothing to do
    }
    
    public void ReadMapBegin(out TType keyType, out TType valType, out int size)
    {
        keyType = (TType)ReadByte();
        valType = (TType)ReadByte();
        size = ReadI32();
    }
    
    public void ReadMapEnd()
    {
        // Nothing to do
    }
    
    public void ReadStructBegin(out string name)
    {
        // Nothing to do
    }
    
    public void ReadStructEnd()
    {
        // Nothing to do
    }
    
    public void ReadFieldBegin(out string name, out TType fieldType, out int fieldId)
    {
        name = string.Empty;
        fieldType = (TType)ReadByte();
        
        if (fieldType == TType.Stop)
        {
            fieldId = 0;
            return;
        }
        
        fieldId = ReadI16();
    }
    
    public void ReadFieldEnd()
    {
        // Nothing to do
    }
    
    public bool ReadBool()
    {
        return ReadByte() != 0;
    }
    
    public sbyte ReadByte()
    {
        return (sbyte)ReadByte();
    }
    
    public short ReadI16()
    {
        var bytes = ReadBytes(2);
        return BinaryPrimitives.ReadInt16BigEndian(bytes);
    }
    
    public int ReadI32()
    {
        var bytes = ReadBytes(4);
        return BinaryPrimitives.ReadInt32BigEndian(bytes);
    }
    
    public long ReadI64()
    {
        var bytes = ReadBytes(8);
        return BinaryPrimitives.ReadInt64BigEndian(bytes);
    }
    
    public double ReadDouble()
    {
        var bytes = ReadBytes(8);
        return BinaryPrimitives.ReadDoubleBigEndian(bytes);
    }
    
    public float ReadFloat()
    {
        var bytes = ReadBytes(4);
        return BinaryPrimitives.ReadSingleBigEndian(bytes);
    }
    
    public string ReadString()
    {
        int size = ReadI32();
        if (size < 0)
            throw new InvalidDataException("Negative string length");
        
        if (size == 0)
            return string.Empty;
        
        var bytes = ReadBytes(size);
        return System.Text.Encoding.UTF8.GetString(ReadBytes(size));
    }
    
    public ReadOnlyMemory<byte> ReadBinary()
    {
        int size = ReadI32();
        if (size < 0)
            throw new InvalidDataException("Negative binary length");
        
        if (size == 0)
            return ReadOnlyMemory<byte>.Empty;
        
        var bytes = ReadBytes(size);
        return new ReadOnlyMemory<byte>(bytes.ToArray());
    }
    
    public void ReadListBegin(out TType elemType, out int size)
    {
        elemType = (TType)ReadByte();
        size = ReadI32();
    }
    
    public void ReadListEnd()
    {
        // Nothing to do
    }
    
    public void ReadSetBegin(out TType elemType, out int size)
    {
        elemType = (TType)ReadByte();
        size = ReadI32();
    }
    
    public void ReadSetEnd()
    {
        // Nothing to do
    }
    
    public void ReadMapBegin(out TType keyType, out TType valType, out int size)
    {
        keyType = (TType)ReadByte();
        valType = (TType)ReadByte();
        size = ReadI32();
    }
    
    public void ReadMapEnd()
    {
        // Nothing to do
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
        var span = GetRemainingSpan();
        if (span.Length < count)
            throw new InvalidDataException($"Expected {count} bytes, got {span.Length}");
        
        var result = span[..count];
        Advance(count);
        return result;
    }
    
    private ReadOnlySpan<byte> GetRemainingSpan()
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
