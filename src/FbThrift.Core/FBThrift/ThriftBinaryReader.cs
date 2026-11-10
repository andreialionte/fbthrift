// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FBThrift;

using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using FbThrift.Core.Memory;

public class ThriftBinaryReader : IThriftProtocolReader
{
    private readonly Stream _stream;
    private readonly bool _strictUtf8 = true;

    public ThriftBinaryReader(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    public Stream BaseStream => _stream;

    public (ThriftWireType fieldType, short fieldId) ReadFieldBegin()
    {
        byte typeByte = ReadByteRaw();
        var fieldType = (ThriftWireType)typeByte;
        if (fieldType == ThriftWireType.Stop)
        {
            return (ThriftWireType.Stop, 0);
        }
        short fieldId = ReadI16();
        return (fieldType, fieldId);
    }

    public void ReadFieldEnd() { }
    public void ReadStructBegin() { }
    public void ReadStructEnd() { }

    public bool ReadBool()
    {
        byte b = ReadByteRaw();
        return b != 0;
    }

    public sbyte ReadByte()
    {
        return (sbyte)ReadByteRaw();
    }

    private byte ReadByteRaw()
    {
        int b = _stream.ReadByte();
        if (b < 0) throw new EndOfStreamException();
        return (byte)b;
    }

    public short ReadI16()
    {
        Span<byte> buf = stackalloc byte[2];
        ReadExactly(buf);
        return UnsafeMemory.ReadInt16BE(buf);
    }

    public int ReadI32()
    {
        Span<byte> buf = stackalloc byte[4];
        ReadExactly(buf);
        return UnsafeMemory.ReadInt32BE(buf);
    }

    public long ReadI64()
    {
        Span<byte> buf = stackalloc byte[8];
        ReadExactly(buf);
        return UnsafeMemory.ReadInt64BE(buf);
    }

    public float ReadFloat()
    {
        int bits = ReadI32();
        return UnsafeMemory.BitCast<int, float>(bits);
    }

    public double ReadDouble()
    {
        long bits = ReadI64();
        return UnsafeMemory.BitCast<long, double>(bits);
    }

    public string ReadString()
    {
        int length = ReadI32();
        if (length == 0) return string.Empty;
        byte[] buf = new byte[length];
        ReadExactly(buf);
        if (_strictUtf8)
        {
            return Encoding.UTF8.GetString(buf);
        }
        return Encoding.UTF8.GetString(buf);
    }

    public byte[] ReadBinary()
    {
        int length = ReadI32();
        if (length == 0) return Array.Empty<byte>();
        byte[] buf = new byte[length];
        ReadExactly(buf);
        return buf;
    }

    public (ThriftWireType elemType, int size) ReadListBegin()
    {
        byte typeByte = ReadByteRaw();
        int size = ReadI32();
        return ((ThriftWireType)typeByte, size);
    }

    public void ReadListEnd() { }

    public (ThriftWireType elemType, int size) ReadSetBegin()
    {
        return ReadListBegin();
    }

    public void ReadSetEnd() { }

    public (ThriftWireType keyType, ThriftWireType valType, int size) ReadMapBegin()
    {
        byte keyType = ReadByteRaw();
        byte valType = ReadByteRaw();
        int size = ReadI32();
        return ((ThriftWireType)keyType, (ThriftWireType)valType, size);
    }

    public void ReadMapEnd() { }

    public void Skip(ThriftWireType type)
    {
        switch (type)
        {
            case ThriftWireType.Bool:
            case ThriftWireType.Byte:
                ReadByteRaw();
                break;
            case ThriftWireType.I16:
                _stream.Position += 2;
                break;
            case ThriftWireType.I32:
                _stream.Position += 4;
                break;
            case ThriftWireType.I64:
            case ThriftWireType.Double:
                _stream.Position += 8;
                break;
            case ThriftWireType.String:
                int len = ReadI32();
                if (len > 0) _stream.Position += len;
                break;
            case ThriftWireType.List:
                var list = ReadListBegin();
                for (int i = 0; i < list.size; i++) Skip(list.elemType);
                break;
            case ThriftWireType.Set:
                var set = ReadSetBegin();
                for (int i = 0; i < set.size; i++) Skip(set.elemType);
                break;
            case ThriftWireType.Map:
                var map = ReadMapBegin();
                for (int i = 0; i < map.size; i++) { Skip(map.keyType); Skip(map.valType); }
                break;
            case ThriftWireType.Struct:
                while (true)
                {
                    var field = ReadFieldBegin();
                    if (field.fieldType == ThriftWireType.Stop) break;
                    Skip(field.fieldType);
                    ReadFieldEnd();
                }
                break;
        }
    }

    private void ReadExactly(Span<byte> buf)
    {
        int total = 0;
        while (total < buf.Length)
        {
            int read = _stream.Read(buf.Slice(total));
            if (read == 0) throw new EndOfStreamException();
            total += read;
        }
    }

    private void ReadExactly(byte[] buf)
    {
        int total = 0;
        while (total < buf.Length)
        {
            int read = _stream.Read(buf, total, buf.Length - total);
            if (read == 0) throw new EndOfStreamException();
            total += read;
        }
    }
}
