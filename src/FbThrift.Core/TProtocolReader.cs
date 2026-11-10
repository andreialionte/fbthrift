namespace FbThrift.Core;

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FbThrift.Core.Memory;

/// <summary>
/// Reads Thrift protocol values from a byte buffer using big-endian encoding.
/// Optimized with System.Runtime.CompilerServices.Unsafe for performance.
/// </summary>
public sealed class TProtocolReader : IDisposable
{
    private readonly byte[] _buffer;
    private int _offset;
    private readonly bool _leaveOpen;

    /// <summary>
    /// Initializes a new instance of the <see cref="TProtocolReader"/> class.
    /// </summary>
    /// <param name="buffer">The buffer to read from.</param>
    /// <param name="leaveOpen">True to leave the buffer open after disposal; otherwise, false.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="buffer"/> is null.</exception>
    public TProtocolReader(byte[] buffer, bool leaveOpen = false)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        _offset = 0;
        _leaveOpen = leaveOpen;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TProtocolReader"/> class from a span.
    /// </summary>
    /// <param name="buffer">The span to read from.</param>
    /// <param name="leaveOpen">True to leave the source open after disposal; otherwise, false.</param>
    public TProtocolReader(ReadOnlySpan<byte> buffer, bool leaveOpen = false)
    {
        _buffer = buffer.ToArray();
        _offset = 0;
        _leaveOpen = leaveOpen;
    }

    /// <summary>
    /// Reads a single byte.
    /// </summary>
    /// <returns>The byte read.</returns>
    /// <exception cref="EndOfStreamException">Thrown when the buffer is exhausted.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ReadByte()
    {
        if (_offset >= _buffer.Length)
            throw new EndOfStreamException();
        return _buffer[_offset++];
    }

    /// <summary>
    /// Reads a boolean value (non-zero byte is true).
    /// </summary>
    /// <returns>The boolean value read.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ReadBool() => ReadByte() != 0;

    /// <summary>
    /// Reads a 16-bit signed integer in big-endian order.
    /// </summary>
    /// <returns>The value read.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public short ReadI16()
    {
        short v = UnsafeMemory.ReadInt16BE(_buffer.AsSpan(_offset, 2));
        _offset += 2;
        return v;
    }

    /// <summary>
    /// Reads a 32-bit signed integer in big-endian order.
    /// </summary>
    /// <returns>The value read.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadI32()
    {
        int v = UnsafeMemory.ReadInt32BE(_buffer.AsSpan(_offset, 4));
        _offset += 4;
        return v;
    }

    /// <summary>
    /// Reads a 64-bit signed integer in big-endian order.
    /// </summary>
    /// <returns>The value read.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long ReadI64()
    {
        long v = UnsafeMemory.ReadInt64BE(_buffer.AsSpan(_offset, 8));
        _offset += 8;
        return v;
    }

    /// <summary>
    /// Reads a 64-bit IEEE 754 floating-point value in big-endian order.
    /// </summary>
    /// <returns>The value read.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double ReadDouble()
    {
        double v = UnsafeMemory.ReadDoubleBE(_buffer.AsSpan(_offset, 8));
        _offset += 8;
        return v;
    }

    /// <summary>
    /// Reads a length-prefixed byte string.
    /// </summary>
    /// <returns>The bytes read.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> ReadStringBytes()
    {
        int len = ReadI32();
        if (len < 0)
            throw new InvalidOperationException("Negative string length.");
        if (len > _buffer.Length - _offset)
            throw new EndOfStreamException("String longer than buffer.");
        ReadOnlySpan<byte> bytes = _buffer.AsSpan(_offset, len);
        _offset += len;
        return bytes;
    }

    /// <summary>
    /// Reads a length-prefixed UTF-8 encoded string.
    /// </summary>
    /// <returns>The string read.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string ReadString() => System.Text.Encoding.UTF8.GetString(ReadStringBytes());

    /// <summary>
    /// Reads a field type byte.
    /// </summary>
    /// <returns>The field type.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TType ReadFieldType() => (TType)ReadByte();

    /// <summary>
    /// Reads a field id as a 16-bit integer.
    /// </summary>
    /// <returns>The field id.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public short ReadFieldId() => ReadI16();

    /// <summary>
    /// Reads a map header.
    /// </summary>
    /// <returns>A tuple containing the key type, value type, and size.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (TType KeyType, TType ValueType, int Size) ReadMapBegin()
    {
        TType keyType = ReadFieldType();
        TType valueType = ReadFieldType();
        int size = ReadI32();
        return (keyType, valueType, size);
    }

    /// <summary>
    /// Reads a list header.
    /// </summary>
    /// <returns>A tuple containing the element type and size.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (TType ElementType, int Size) ReadListBegin()
    {
        TType elemType = ReadFieldType();
        int size = ReadI32();
        return (elemType, size);
    }

    /// <summary>
    /// Disposes the reader.
    /// </summary>
    public void Dispose()
    {
    }

    /// <summary>
    /// Reads a 16-bit integer using Unsafe for maximum performance.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe short ReadI16Unsafe()
    {
        fixed (byte* ptr = _buffer)
        {
            short v = Unsafe.ReadUnaligned<short>(ptr + _offset);
            _offset += 2;
            return BinaryPrimitives.ReverseEndianness(v);
        }
    }

    /// <summary>
    /// Reads a 32-bit integer using Unsafe for maximum performance.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe int ReadI32Unsafe()
    {
        fixed (byte* ptr = _buffer)
        {
            int v = Unsafe.ReadUnaligned<int>(ptr + _offset);
            _offset += 4;
            return BinaryPrimitives.ReverseEndianness(v);
        }
    }

    /// <summary>
    /// Reads a 64-bit integer using Unsafe for maximum performance.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe long ReadI64Unsafe()
    {
        fixed (byte* ptr = _buffer)
        {
            long v = Unsafe.ReadUnaligned<long>(ptr + _offset);
            _offset += 8;
            return BinaryPrimitives.ReverseEndianness(v);
        }
    }

    /// <summary>
    /// Reads a double using Unsafe for maximum performance.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe double ReadDoubleUnsafe()
    {
        fixed (byte* ptr = _buffer)
        {
            ulong v = Unsafe.ReadUnaligned<ulong>(ptr + _offset);
            _offset += 8;
            ulong swapped = BinaryPrimitives.ReverseEndianness(v);
            return BitConverter.Int64BitsToDouble((long)swapped);
        }
    }
}