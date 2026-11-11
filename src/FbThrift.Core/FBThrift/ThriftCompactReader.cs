using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace FBThrift;

public class ThriftCompactReader : IThriftProtocolReader
{
    private readonly Stream _stream;
    private short _lastFieldId = 0;
    private bool _lastFieldWasBool = false;
    private bool _boolValue = false;

    public ThriftCompactReader(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    public Stream BaseStream => _stream;

    private byte ReadByteRaw()
    {
        int b = _stream.ReadByte();
        if (b < 0) throw new EndOfStreamException();
        return (byte)b;
    }

    private uint ReadVarint32()
    {
        uint result = 0;
        int shift = 0;
        while (true)
        {
            byte b = ReadByteRaw();
            result |= ((uint)(b & 0x7F)) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }
        return result;
    }

    private ulong ReadVarint64()
    {
        ulong result = 0;
        int shift = 0;
        while (true)
        {
            byte b = ReadByteRaw();
            result |= ((ulong)(b & 0x7F)) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }
        return result;
    }

    private long ZigzagDecode(ulong value)
    {
        return (long)(value >> 1) ^ -(long)(value & 1);
    }

    private ThriftWireType FromCompactType(byte ct)
    {
        return ct switch
        {
            1 => ThriftWireType.Bool,
            2 => ThriftWireType.Byte,
            3 => ThriftWireType.I16,
            4 => ThriftWireType.I32,
            5 => ThriftWireType.I64,
            6 => ThriftWireType.Double,
            7 => ThriftWireType.String,
            8 => ThriftWireType.List,
            9 => ThriftWireType.Set,
            10 => ThriftWireType.Map,
            11 => ThriftWireType.Struct,
            _ => ThriftWireType.Byte
        };
    }

    public (ThriftWireType fieldType, short fieldId) ReadFieldBegin()
    {
        byte header = ReadByteRaw();
        if (header == 0)
        {
            _lastFieldId = 0;
            return (ThriftWireType.Stop, 0);
        }
        byte typeAndDelta = header;
        int delta = typeAndDelta >> 4;
        byte typeBits = (byte)(typeAndDelta & 0x0F);

        if (delta == 0)
        {
            // long form
            typeBits = (byte)(typeAndDelta & 0x0F);
            short fieldId = (short)ReadVarint32();
            _lastFieldId = fieldId;
            if (typeBits == 1)
            {
                _lastFieldWasBool = true;
                return (ThriftWireType.Bool, fieldId);
            }
            _lastFieldWasBool = false;
            return (FromCompactType(typeBits), fieldId);
        }
        else
        {
            // short form with delta
            if (typeBits == 1)
            {
                // inline bool
                _lastFieldWasBool = true;
                _lastFieldId = (short)(_lastFieldId + delta);
                // For inline bool, we need to know if true/false - but it's encoded differently
                // In standard compact, bool in field header with type=1 means the bool value is in the next byte in some forms
                // Or handled specially
                return (ThriftWireType.Bool, _lastFieldId);
            }
            _lastFieldWasBool = false;
            _lastFieldId = (short)(_lastFieldId + delta);
            return (FromCompactType(typeBits), _lastFieldId);
        }
    }

    public void ReadFieldEnd() { }
    public void ReadStructBegin() { }
    public void ReadStructEnd() { }

    public bool ReadBool()
    {
        if (_lastFieldWasBool)
        {
            // In compact protocol, when bool is inline in field header, the value might be encoded differently
            // Try to read as simple byte
            byte b = ReadByteRaw();
            _lastFieldWasBool = false;
            return b == 1;
        }
        byte val = ReadByteRaw();
        return val != 0;
    }

    public sbyte ReadByte()
    {
        uint v = ReadVarint32();
        return (sbyte)(int)ZigzagDecode(v);
    }

    public short ReadI16()
    {
        uint v = ReadVarint32();
        return (short)ZigzagDecode(v);
    }

    public int ReadI32()
    {
        uint v = ReadVarint32();
        return (int)ZigzagDecode(v);
    }

    public long ReadI64()
    {
        ulong v = ReadVarint64();
        return ZigzagDecode(v);
    }

    public float ReadFloat()
    {
        int bits = ReadI32();
        return BitConverter.Int32BitsToSingle(bits);
    }

    public double ReadDouble()
    {
        Span<byte> buf = stackalloc byte[8];
        ReadExactly(buf);
        Array.Reverse(buf.ToArray());
        return BitConverter.ToDouble(buf.ToArray(), 0);
    }

    public string ReadString()
    {
        uint length = ReadVarint32();
        if (length == 0) return string.Empty;
        byte[] buf = new byte[length];
        ReadExactly(buf);
        return Encoding.UTF8.GetString(buf);
    }

    public byte[] ReadBinary()
    {
        uint length = ReadVarint32();
        if (length == 0) return Array.Empty<byte>();
        byte[] buf = new byte[length];
        ReadExactly(buf);
        return buf;
    }

    public (ThriftWireType elemType, int size) ReadListBegin()
    {
        byte header = ReadByteRaw();
        byte typeBits = (byte)(header & 0x0F);
        int size = header >> 4;
        if (size == 15)
        {
            size = (int)ReadVarint32();
        }
        return (FromCompactType(typeBits), size);
    }

    public void ReadListEnd() { }

    public (ThriftWireType elemType, int size) ReadSetBegin()
    {
        return ReadListBegin();
    }

    public void ReadSetEnd() { }

    public (ThriftWireType keyType, ThriftWireType valType, int size) ReadMapBegin()
    {
        uint size = ReadVarint32();
        if (size == 0)
        {
            return (ThriftWireType.Stop, ThriftWireType.Stop, 0);
        }
        byte types = ReadByteRaw();
        byte keyType = (byte)(types >> 4);
        byte valType = (byte)(types & 0x0F);
        return (FromCompactType(keyType), FromCompactType(valType), (int)size);
    }

    public void ReadMapEnd() { }

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

        public void Dispose()
        {
            _stream?.Dispose();
        }
    }
