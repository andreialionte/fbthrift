using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using FBThrift;

namespace FBThrift;

public class ThriftCompactWriter : IThriftProtocolWriter
{
    private readonly Stream _stream;
    private short _lastFieldId = 0;

    public ThriftCompactWriter(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    public Stream BaseStream => _stream;

    private void WriteVarint32(int value)
    {
        uint v = (uint)value;
        while ((v & 0xFFFFFF80) != 0)
        {
            _stream.WriteByte((byte)((v & 0x7F) | 0x80));
            v >>= 7;
        }
        _stream.WriteByte((byte)(v & 0x7F));
    }

    private void WriteVarint64(long value)
    {
        ulong v = (ulong)value;
        while ((v & 0xFFFFFFFFFFFFFF80UL) != 0)
        {
            _stream.WriteByte((byte)((v & 0x7F) | 0x80));
            v >>= 7;
        }
        _stream.WriteByte((byte)(v & 0x7F));
    }

    private ulong ZigzagEncode(long value)
    {
        return (ulong)((value << 1) ^ (value >> 63));
    }

    private CompactType GetCompactType(ThriftWireType type)
    {
        return type switch
        {
            ThriftWireType.Bool => CompactType.Bool,
            ThriftWireType.Byte => CompactType.Byte,
            ThriftWireType.I16 => CompactType.I16,
            ThriftWireType.I32 => CompactType.I32,
            ThriftWireType.I64 => CompactType.I64,
            ThriftWireType.Double => CompactType.Double,
            ThriftWireType.String => CompactType.String,
            ThriftWireType.List => CompactType.List,
            ThriftWireType.Set => CompactType.Set,
            ThriftWireType.Map => CompactType.Map,
            ThriftWireType.Struct => CompactType.Struct,
            _ => CompactType.Byte
        };
    }

    private enum CompactType : byte
    {
        Bool = 1,
        Byte = 2,
        I16 = 3,
        I32 = 4,
        I64 = 5,
        Double = 6,
        String = 7,
        List = 8,
        Set = 9,
        Map = 10,
        Struct = 11
    }

    public void WriteFieldBegin(ThriftWireType fieldType, short fieldId)
    {
        if (fieldId == 0)
        {
            throw new ArgumentException("Field ID cannot be 0 for field begin");
        }
        short delta = (short)(fieldId - _lastFieldId);
        if (delta > 0 && delta <= 15)
        {
            if (fieldType == ThriftWireType.Bool)
            {
                _stream.WriteByte((byte)((delta << 4) | 0x01)); // inline bool type marker handled separately
            }
            else
            {
                _stream.WriteByte((byte)((delta << 4) | (byte)GetCompactType(fieldType)));
            }
            _lastFieldId = fieldId;
        }
        else
        {
            if (fieldType == ThriftWireType.Bool)
            {
                _stream.WriteByte((byte)(0x0 | 0x01));
            }
            else
            {
                _stream.WriteByte((byte)(0x00 | (byte)GetCompactType(fieldType)));
            }
            WriteVarint32(fieldId);
            _lastFieldId = fieldId;
        }
    }

    public void WriteFieldStop()
    {
        _stream.WriteByte(0x00);
        _lastFieldId = 0;
    }

    public void WriteBool(bool value)
    {
        // For inline bool in field header, handled differently; write as byte
        _stream.WriteByte(value ? (byte)1 : (byte)0);
    }

    public void WriteByte(sbyte value)
    {
        WriteVarint32(value);
    }

    public void WriteI16(short value)
    {
        WriteVarint64((long)(int)ZigzagEncode(value));
    }

    public void WriteI32(int value)
    {
        WriteVarint64((long)ZigzagEncode(value));
    }

    public void WriteI64(long value)
    {
        WriteVarint64((long)ZigzagEncode(value));
    }

    public void WriteFloat(float value)
    {
        var intBits = BitConverter.SingleToInt32Bits(value);
        WriteI32(intBits);
    }

    public void WriteDouble(double value)
    {
        Span<byte> buf = stackalloc byte[8];
        BitConverter.GetBytes(value).CopyTo(buf);
        Array.Reverse(buf.ToArray());
        // Use big endian for double
        ulong bits = (ulong)BitConverter.DoubleToInt64Bits(value);
        Span<byte> b = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(b, bits);
        _stream.Write(b);
    }

    public void WriteString(string value)
    {
        if (value == null) throw new ArgumentNullException(nameof(value));
        var maxByteCount = Encoding.UTF8.GetMaxByteCount(value.Length);
        var rented = ArrayPool<byte>.Shared.Rent(maxByteCount);
        try
        {
            var byteCount = Encoding.UTF8.GetBytes(value, 0, value.Length, rented, 0);
            WriteVarint32(byteCount);
            _stream.Write(rented, 0, byteCount);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public void WriteBinary(byte[] value)
    {
        if (value == null) throw new ArgumentNullException(nameof(value));
        WriteVarint32(value.Length);
        _stream.Write(value, 0, value.Length);
    }

    public void WriteListBegin(ThriftWireType elemType, int size)
    {
        CompactType ctype = GetCompactType(elemType);
        if (size <= 14)
        {
            _stream.WriteByte((byte)((size << 4) | (byte)ctype));
        }
        else
        {
            _stream.WriteByte((byte)(0xF0 | (byte)ctype));
            WriteVarint32(size);
        }
    }

    public void WriteSetBegin(ThriftWireType elemType, int size)
    {
        CompactType ctype = GetCompactType(elemType);
        if (size <= 14)
        {
            _stream.WriteByte((byte)((size << 4) | (byte)ctype));
        }
        else
        {
            _stream.WriteByte((byte)(0xF0 | (byte)ctype));
            WriteVarint32(size);
        }
    }

    public void WriteMapBegin(ThriftWireType keyType, ThriftWireType valType, int size)
    {
        if (size == 0)
        {
            WriteVarint32(0);
            return;
        }
        WriteVarint32(size);
        _stream.WriteByte((byte)(((byte)GetCompactType(keyType)) << 4 | (byte)GetCompactType(valType)));
    }

    public void WriteStruct<T>(T value) where T : IThriftSerializable
    {
        if (value == null) throw new ArgumentNullException(nameof(value));
        value.__fbthrift_write(this);
    }

    public void WriteListEnd()
    {
        // Compact protocol doesn't need explicit list end marker
    }

    public void WriteSetEnd()
    {
        // Compact protocol doesn't need explicit set end marker
    }

    public void WriteMapEnd()
    {
        // Compact protocol doesn't need explicit map end marker
    }

    public void WriteStructBegin(string? name)
    {
        // Compact protocol doesn't need explicit struct begin marker
    }

    public void WriteFieldEnd()
    {
        // Compact protocol doesn't need explicit field end marker
    }

    public void WriteStructEnd()
    {
        // Compact protocol doesn't need explicit struct end marker
    }
}
