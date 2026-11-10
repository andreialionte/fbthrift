using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FbThrift.Core.Memory;

namespace FbThrift.Protocols.Compact;

/// <summary>
/// Reads Thrift values using the Compact protocol wire encoding.
/// Optimized with System.Runtime.CompilerServices.Unsafe for performance.
/// </summary>
public sealed class CompactProtocolReader : ITProtocolReader
{
    private readonly byte[] _buffer;
    private int _offset;

    /// <summary>
    /// Initializes a new instance of the <see cref="CompactProtocolReader"/> class.
    /// </summary>
    /// <param name="buffer">The buffer to read from.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="buffer"/> is null.</exception>
    public CompactProtocolReader(byte[] buffer)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        _offset = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Require(int count)
    {
        if (_offset + count > _buffer.Length)
            throw new EndOfStreamException("Truncated compact protocol data.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadByteUnchecked()
    {
        return _buffer[_offset++];
    }

    /// <inheritdoc/>
    public (string Name, TMessageType MessageType, int SeqId) ReadMessageBegin()
    {
        byte version = ReadByteUnchecked();
        if (version != 0)
            throw new FbThrift.Core.FbThriftException($"Invalid compact protocol version: {version}.");

        TMessageType messageType = (TMessageType)ReadByteUnchecked();
        int seqId = ReadVarintInt32();
        string name = ReadString();
        return (name, messageType, seqId);
    }

    /// <inheritdoc/>
    public void ReadMessageEnd() { }

    /// <inheritdoc/>
    public string? ReadStructBegin() => null;

    /// <inheritdoc/>
    public void ReadStructEnd() { }

    /// <inheritdoc/>
    public (TType Type, short FieldId) ReadFieldBegin()
    {
        byte b = ReadByteUnchecked();
        TType type = (TType)(b >> 4);
        short fieldId = (short)(b & 0x0F);
        if (fieldId == 15)
        {
            fieldId = (short)ReadVarintInt32();
        }
        return (type, fieldId);
    }

    /// <inheritdoc/>
    public void ReadFieldEnd() { }

    /// <inheritdoc/>
    public (TType KeyType, TType ValueType, int Size) ReadMapBegin()
    {
        byte b = ReadByteUnchecked();
        TType keyType = (TType)(b >> 4);
        TType valueType = (TType)(b & 0x0F);
        int size = ReadVarintInt32();
        return (keyType, valueType, size);
    }

    /// <inheritdoc/>
    public void ReadMapEnd() { }

    /// <inheritdoc/>
    public (TType ElementType, int Size) ReadListBegin()
    {
        TType elemType = (TType)ReadByteUnchecked();
        int size = ReadVarintInt32();
        return (elemType, size);
    }

    /// <inheritdoc/>
    public void ReadListEnd() { }

    /// <inheritdoc/>
    public (TType ElementType, int Size) ReadSetBegin()
    {
        TType elemType = (TType)ReadByteUnchecked();
        int size = ReadVarintInt32();
        return (elemType, size);
    }

    /// <inheritdoc/>
    public void ReadSetEnd() { }

    /// <inheritdoc/>
    public bool ReadBool()
    {
        return ReadByteUnchecked() != 0;
    }

    /// <inheritdoc/>
    public byte ReadByte()
    {
        return ReadByteUnchecked();
    }

    /// <inheritdoc/>
    public short ReadI16()
    {
        Require(2);
        short v = UnsafeMemory.ReadInt16BE(_buffer.AsSpan(_offset, 2));
        _offset += 2;
        return v;
    }

    /// <inheritdoc/>
    public int ReadI32()
    {
        Require(4);
        int v = UnsafeMemory.ReadInt32BE(_buffer.AsSpan(_offset, 4));
        _offset += 4;
        return v;
    }

    /// <inheritdoc/>
    public long ReadI64()
    {
        Require(8);
        long v = UnsafeMemory.ReadInt64BE(_buffer.AsSpan(_offset, 8));
        _offset += 8;
        return v;
    }

    /// <inheritdoc/>
    public double ReadDouble()
    {
        Require(8);
        double v = UnsafeMemory.ReadDoubleBE(_buffer.AsSpan(_offset, 8));
        _offset += 8;
        return v;
    }

    /// <inheritdoc/>
    public string ReadString()
    {
        int len = ReadVarintInt32();
        if (len < 0)
            return string.Empty;
        Require(len);
        string s = System.Text.Encoding.UTF8.GetString(_buffer, _offset, len);
        _offset += len;
        return s;
    }

    /// <inheritdoc/>
    public ReadOnlyMemory<byte> ReadBinary()
    {
        int len = ReadVarintInt32();
        if (len < 0)
            return ReadOnlyMemory<byte>.Empty;
        Require(len);
        ReadOnlyMemory<byte> mem = _buffer.AsMemory(_offset, len);
        _offset += len;
        return mem;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ReadVarintInt32()
    {
        int consumed = Varint.Decode(_buffer.AsSpan(_offset), out long value);
        _offset += consumed;
        return checked((int)value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private short ReadInt16BigEndian()
    {
        short v = UnsafeMemory.ReadInt16BE(_buffer.AsSpan(_offset, 2));
        _offset += 2;
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ReadInt32BigEndian()
    {
        int v = UnsafeMemory.ReadInt32BE(_buffer.AsSpan(_offset, 4));
        _offset += 4;
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long ReadInt64BigEndian()
    {
        long v = UnsafeMemory.ReadInt64BE(_buffer.AsSpan(_offset, 8));
        _offset += 8;
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private double ReadDoubleBigEndian()
    {
        double v = UnsafeMemory.ReadDoubleBE(_buffer.AsSpan(_offset, 8));
        _offset += 8;
        return v;
    }

    /// <summary>
    /// Reads a 16-bit integer using Unsafe for maximum performance.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe short ReadInt16BigEndianUnsafe()
    {
        Require(2);
        fixed (byte* ptr = _buffer)
        {
            short v = Unsafe.ReadUnaligned<short>(ptr + _offset);
            _offset += 2;
            // Swap bytes for big-endian
            return (short)(((v & 0xFF) << 8) | ((v >> 8) & 0xFF));
        }
    }

    /// <summary>
    /// Reads a 32-bit integer using Unsafe for maximum performance.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe int ReadInt32BigEndianUnsafe()
    {
        Require(4);
        fixed (byte* ptr = _buffer)
        {
            int v = Unsafe.ReadUnaligned<int>(ptr + _offset);
            _offset += 4;
            // Swap bytes for big-endian
            return BinaryPrimitives.ReverseEndianness(v);
        }
    }

    /// <summary>
    /// Reads a 64-bit integer using Unsafe for maximum performance.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe long ReadInt64BigEndianUnsafe()
    {
        Require(8);
        fixed (byte* ptr = _buffer)
        {
            long v = Unsafe.ReadUnaligned<long>(ptr + _offset);
            _offset += 8;
            // Swap bytes for big-endian
            return BinaryPrimitives.ReverseEndianness(v);
        }
    }

    /// <summary>
    /// Reads a double using Unsafe for maximum performance.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe double ReadDoubleBigEndianUnsafe()
    {
        Require(8);
        fixed (byte* ptr = _buffer)
        {
            ulong v = Unsafe.ReadUnaligned<ulong>(ptr + _offset);
            _offset += 8;
            // Swap bytes for big-endian
            v = BinaryPrimitives.ReverseEndianness(v);
            return BitConverter.Int64BitsToDouble((long)v);
        }
    }
}
