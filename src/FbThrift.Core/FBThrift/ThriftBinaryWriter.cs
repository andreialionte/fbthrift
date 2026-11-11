// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FBThrift;

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using FbThrift.Core.Memory;

public class ThriftBinaryWriter : IThriftProtocolWriter
{
    private readonly Stream _stream;

    public ThriftBinaryWriter(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    public Stream BaseStream => _stream;

    public void WriteFieldBegin(ThriftWireType fieldType, short fieldId)
    {
        WriteByte((sbyte)fieldType);
        WriteI16(fieldId);
    }

    public void WriteFieldStop()
    {
        _stream.WriteByte((byte)ThriftWireType.Stop);
    }

    public void WriteBool(bool value)
    {
        _stream.WriteByte(value ? (byte)1 : (byte)0);
    }

    public void WriteByte(sbyte value)
    {
        _stream.WriteByte((byte)value);
    }

    public void WriteI16(short value)
    {
        Span<byte> buf = stackalloc byte[2];
        UnsafeMemory.WriteInt16BE(buf, value);
        _stream.Write(buf);
    }

    public void WriteI32(int value)
    {
        Span<byte> buf = stackalloc byte[4];
        UnsafeMemory.WriteInt32BE(buf, value);
        _stream.Write(buf);
    }

    public void WriteI64(long value)
    {
        Span<byte> buf = stackalloc byte[8];
        UnsafeMemory.WriteInt64BE(buf, value);
        _stream.Write(buf);
    }

    public void WriteFloat(float value)
    {
        int intBits = UnsafeMemory.BitCast<float, int>(value);
        WriteI32(intBits);
    }

    public void WriteDouble(double value)
    {
        long longBits = UnsafeMemory.BitCast<double, long>(value);
        WriteI64(longBits);
    }

    public void WriteString(string value)
    {
        if (value == null)
            throw new ArgumentNullException(nameof(value));
        var maxByteCount = Encoding.UTF8.GetMaxByteCount(value.Length);
        var rented = ArrayPool<byte>.Shared.Rent(maxByteCount);
        try
        {
            var byteCount = Encoding.UTF8.GetBytes(value, 0, value.Length, rented, 0);
            WriteI32(byteCount);
            _stream.Write(rented, 0, byteCount);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public void WriteBinary(byte[] value)
    {
        if (value == null)
            throw new ArgumentNullException(nameof(value));
        WriteI32(value.Length);
        _stream.Write(value, 0, value.Length);
    }

    public void WriteListBegin(ThriftWireType elemType, int size)
    {
        _stream.WriteByte((byte)elemType);
        WriteI32(size);
    }

    public void WriteSetBegin(ThriftWireType elemType, int size)
    {
        WriteListBegin(elemType, size);
    }

    public void WriteMapBegin(ThriftWireType keyType, ThriftWireType valType, int size)
    {
        _stream.WriteByte((byte)keyType);
        _stream.WriteByte((byte)valType);
        WriteI32(size);
    }

    public void WriteStruct<T>(T value) where T : IThriftSerializable
    {
        if (value == null) throw new ArgumentNullException(nameof(value));
        value.__fbthrift_write(this);
    }

    public void WriteFieldEnd()
    {
        // Binary protocol doesn't need explicit field end marker
    }

    public void WriteListEnd()
    {
        // Binary protocol doesn't need explicit list end marker
    }

    public void WriteSetEnd()
    {
        // Binary protocol doesn't need explicit set end marker
    }

    public void WriteMapEnd()
    {
        // Binary protocol doesn't need explicit map end marker
    }

    public void WriteStructBegin(string? name)
    {
        // Binary protocol doesn't need explicit struct begin marker
    }

    public void WriteStructEnd()
    {
        // Binary protocol doesn't need explicit struct end marker
    }

    public void Dispose()
    {
        _stream?.Dispose();
    }
}