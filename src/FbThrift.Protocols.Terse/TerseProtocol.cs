// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Protocols.Terse;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using FbThrift.Core.Memory;
using FbThrift.Core.Protocol;

/// <summary>
/// Terse Write Protocol - a compact binary protocol optimized for size.
/// Based on fbthrift's TerseWriteProtocol.
/// Uses shorter field headers, variable-length integers, and omits type information when inferable.
/// </summary>
public sealed class TerseProtocolWriter : ITProtocolWriter
{
    private readonly IBufferWriter<byte> _writer;
    private readonly MemoryStream _stream;

    public TerseProtocolWriter(IBufferWriter<byte> writer)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _stream = new MemoryStream();
    }

    public TerseProtocolWriter(Stream stream)
    {
        _stream = stream as MemoryStream ?? new MemoryStream();
        _writer = new StreamBufferWriter(_stream);
    }

    /// <summary>
    /// Gets the underlying buffer.
    /// </summary>
    public ReadOnlyMemory<byte> GetBuffer()
    {
        if (_stream is MemoryStream ms)
        {
            return ms.GetBuffer().AsMemory(0, (int)ms.Length);
        }
        return _writer.GetMemory();
    }

    /// <inheritdoc/>
    public void WriteMessageBegin(string name, TMessageType messageType, short seqId)
    {
        WriteString(name);
        WriteByte((byte)messageType);
        WriteI16(seqId);
    }

    /// <inheritdoc/>
    public void WriteMessageEnd() { }

    /// <inheritdoc/>
    public void WriteStructBegin(string? name) { }

    /// <inheritdoc/>
    public void WriteStructEnd() { }

    /// <inheritdoc/>
    public void WriteFieldBegin(string name, TType fieldType, short fieldId)
    {
        // Terse: combine field ID and type into single varint
        // Field ID uses lower bits, type uses upper bits
        uint combined = ((uint)fieldId << 4) | (uint)fieldType;
        WriteVarint(combined);
        
        // For field names, write as string if present (for structs with named fields)
        if (!string.IsNullOrEmpty(name))
        {
            WriteString(name);
        }
    }

    /// <inheritdoc/>
    public void WriteFieldEnd() { }

    /// <inheritdoc/>
    public void WriteFieldStop()
    {
        WriteByte(0); // Stop marker
    }

    /// <inheritdoc/>
    public void WriteMapBegin(TType keyType, TType valueType, int size)
    {
        WriteVarint((uint)size);
        WriteByte((byte)keyType);
        WriteByte((byte)valueType);
    }

    /// <inheritdoc/>
    public void WriteMapEnd() { }

    /// <inheritdoc/>
    public void WriteListBegin(TType elementType, int size)
    {
        WriteVarint((uint)size);
        WriteByte((byte)elementType);
    }

    /// <inheritdoc/>
    public void WriteListEnd() { }

    /// <inheritdoc/>
    public void WriteSetBegin(TType elementType, int size)
    {
        WriteVarint((uint)size);
        WriteByte((byte)elementType);
    }

    /// <inheritdoc/>
    public void WriteSetEnd() { }

    /// <inheritdoc/>
    public void WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);

    /// <inheritdoc/>
    public void WriteByte(byte value) => WriteByte(value);

    /// <inheritdoc/>
    public void WriteI16(short value) => WriteZigZagVarint(value);

    /// <inheritdoc/>
    public void WriteI32(int value) => WriteZigZagVarint(value);

    /// <inheritdoc/>
    public void WriteI64(long value) => WriteZigZagVarint(value);

    /// <inheritdoc/>
    public void WriteFloat(float value)
    {
        Span<byte> bytes = stackalloc byte[4];
        UnsafeMemory.WriteSingleBE(bytes, value);
        WriteBytes(bytes);
    }

    /// <inheritdoc/>
    public void WriteDouble(double value)
    {
        Span<byte> bytes = stackalloc byte[8];
        UnsafeMemory.WriteDoubleBE(bytes, value);
        WriteBytes(bytes);
    }

    /// <inheritdoc/>
    public void WriteString(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value ?? string.Empty);
        WriteVarint((uint)bytes.Length);
        WriteBytes(bytes);
    }

    /// <inheritdoc/>
    public void WriteBinary(byte[] value)
    {
        WriteVarint((uint)value.Length);
        WriteBytes(value);
    }

    // Terse-specific optimized methods

    /// <summary>
    /// Writes a variable-length integer (LEB128).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteVarint(uint value)
    {
        Span<byte> buffer = stackalloc byte[5];
        int length = 0;
        while (value >= 0x80)
        {
            buffer[length++] = (byte)(value | 0x80);
            value >>= 7;
        }
        buffer[length++] = (byte)value;
        WriteBytes(buffer[..length]);
    }

    /// <summary>
    /// Writes a signed integer using zigzag encoding + varint.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteZigZagVarint(int value)
    {
        uint encoded = (uint)((value << 1) ^ (value >> 31));
        WriteVarint(encoded);
    }

    /// <summary>
    /// Writes a signed long using zigzag encoding + varint.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteZigZagVarint(long value)
    {
        ulong encoded = (ulong)((value << 1) ^ (value >> 63));
        WriteVarint(encoded);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteByte(byte value)
    {
        _writer.GetSpan(1)[0] = value;
        _writer.Advance(1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        var span = _writer.GetSpan(bytes.Length);
        bytes.CopyTo(span);
        _writer.Advance(bytes.Length);
    }
}

/// <summary>
/// Terse Protocol Reader.
/// </summary>
public sealed class TerseProtocolReader : ITProtocolReader
{
    private readonly ReadOnlySequence<byte> _buffer;
    private SequenceReader<byte> _reader;

    public TerseProtocolReader(ReadOnlySequence<byte> buffer)
    {
        _buffer = buffer;
        _reader = new SequenceReader<byte>(buffer);
    }

    /// <inheritdoc/>
    public (string name, TMessageType messageType, int seqId) ReadMessageBegin()
    {
        string name = ReadString();
        TMessageType type = (TMessageType)ReadByte();
        short seqId = ReadI16();
        return (name, type, seqId);
    }

    /// <inheritdoc/>
    public void ReadMessageEnd() { }

    /// <inheritdoc/>
    public void ReadStructBegin() { }

    /// <inheritdoc/>
    public void ReadStructEnd() { }

    /// <inheritdoc/>
    public (string name, TType fieldType, short fieldId) ReadFieldBegin()
    {
        if (!_reader.TryRead(out byte firstByte))
        {
            return (string.Empty, TType.Stop, 0);
        }

        if (firstByte == 0)
        {
            return (string.Empty, TType.Stop, 0);
        }

        // Read rest of varint
        uint combined = firstByte & 0x7F;
        int shift = 7;
        while ((firstByte & 0x80) != 0 && _reader.TryRead(out firstByte))
        {
            combined |= (uint)(firstByte & 0x7F) << shift;
            shift += 7;
        }

        short fieldId = (short)(combined >> 4);
        TType fieldType = (TType)(combined & 0xF);

        // Check if there's a field name (for named fields)
        string name = string.Empty;
        if (_reader.Remaining > 0)
        {
            // Peek next byte - if it looks like a string length, read it
            // This is a heuristic; in practice, field names are often omitted in terse
        }

        return (name, fieldType, fieldId);
    }

    /// <inheritdoc/>
    public void ReadFieldEnd() { }

    /// <inheritdoc/>
    public (TType keyType, TType valueType, int size) ReadMapBegin()
    {
        int size = (int)ReadVarint();
        TType keyType = (TType)ReadByte();
        TType valueType = (TType)ReadByte();
        return (keyType, valueType, size);
    }

    /// <inheritdoc/>
    public void ReadMapEnd() { }

    /// <inheritdoc/>
    public (TType elementType, int size) ReadListBegin()
    {
        int size = (int)ReadVarint();
        TType elementType = (TType)ReadByte();
        return (elementType, size);
    }

    /// <inheritdoc/>
    public void ReadListEnd() { }

    /// <inheritdoc/>
    public (TType elementType, int size) ReadSetBegin()
    {
        int size = (int)ReadVarint();
        TType elementType = (TType)ReadByte();
        return (elementType, size);
    }

    /// <inheritdoc/>
    public void ReadSetEnd() { }

    /// <inheritdoc/>
    public bool ReadBool() => ReadByte() != 0;

    /// <inheritdoc/>
    public byte ReadByte()
    {
        _reader.TryRead(out byte b);
        return b;
    }

    /// <inheritdoc/>
    public short ReadI16() => (short)ReadZigZagVarint();

    /// <inheritdoc/>
    public int ReadI32() => (int)ReadZigZagVarint();

    /// <inheritdoc/>
    public long ReadI64() => (long)ReadZigZagVarint();

    /// <inheritdoc/>
    public float ReadFloat()
    {
        Span<byte> bytes = stackalloc byte[4];
        ReadBytes(bytes);
        return UnsafeMemory.ReadSingleBE(bytes);
    }

    /// <inheritdoc/>
    public double ReadDouble()
    {
        Span<byte> bytes = stackalloc byte[8];
        ReadBytes(bytes);
        return UnsafeMemory.ReadDoubleBE(bytes);
    }

    /// <inheritdoc/>
    public string ReadString()
    {
        int length = (int)ReadVarint();
        var bytes = new byte[length];
        ReadBytes(bytes);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    /// <inheritdoc/>
    public byte[] ReadBinary()
    {
        int length = (int)ReadVarint();
        var bytes = new byte[length];
        ReadBytes(bytes);
        return bytes;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint ReadVarint()
    {
        uint result = 0;
        int shift = 0;
        while (_reader.TryRead(out byte b))
        {
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long ReadZigZagVarint()
    {
        ulong encoded = ReadVarint();
        return (long)(encoded >> 1) ^ -(long)(encoded & 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ReadBytes(Span<byte> buffer)
    {
        for (int i = 0; i < buffer.Length; i++)
        {
            _reader.TryRead(out buffer[i]);
        }
    }
}

/// <summary>
/// Buffer writer for Stream.
/// </summary>
internal sealed class StreamBufferWriter : IBufferWriter<byte>
{
    private readonly MemoryStream _stream;

    public StreamBufferWriter(MemoryStream stream)
    {
        _stream = stream;
    }

    public void Advance(int count) { }

    public Memory<byte> GetMemory(int sizeHint = 0) => _stream.GetMemory(sizeHint);

    public Span<byte> GetSpan(int sizeHint = 0) => _stream.GetSpan(sizeHint);
}
