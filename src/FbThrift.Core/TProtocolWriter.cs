using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FbThrift.Core.Memory;

namespace FbThrift.Core;

/// <summary>
/// Writes Thrift protocol values to an <see cref="System.Buffers.IBufferWriter{Byte}"/> using big-endian encoding.
/// Optimized with System.Runtime.CompilerServices.Unsafe.
/// </summary>
public sealed class TProtocolWriter : IDisposable
{
    private readonly IBufferWriter<byte> _writer;
    private readonly bool _leaveOpen;

    /// <summary>
    /// Initializes a new instance of the <see cref="TProtocolWriter"/> class.
    /// </summary>
    /// <param name="writer">The buffer writer to write into.</param>
    /// <param name="leaveOpen">True to leave the writer open after disposal; otherwise, false.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="writer"/> is null.</exception>
    public TProtocolWriter(IBufferWriter<byte> writer, bool leaveOpen = false)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _leaveOpen = leaveOpen;
    }

    /// <summary>
    /// Writes a single byte.
    /// </summary>
    /// <param name="value">The byte value to write.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteByte(byte value)
    {
        _writer.GetSpan(1)[0] = value;
        _writer.Advance(1);
    }

    /// <summary>
    /// Writes a boolean value as a single byte (1 for true, 0 for false).
    /// </summary>
    /// <param name="value">The boolean value to write.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteBool(bool value)
    {
        _writer.GetSpan(1)[0] = value ? (byte)1 : (byte)0;
        _writer.Advance(1);
    }

    /// <summary>
    /// Writes a 16-bit signed integer in big-endian order.
    /// </summary>
    /// <param name="value">The value to write.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteI16(short value)
    {
        Span<byte> span = _writer.GetSpan(2);
        UnsafeMemory.WriteInt16BE(span, value);
        _writer.Advance(2);
    }

    /// <summary>
    /// Writes a 32-bit signed integer in big-endian order.
    /// </summary>
    /// <param name="value">The value to write.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteI32(int value)
    {
        Span<byte> span = _writer.GetSpan(4);
        UnsafeMemory.WriteInt32BE(span, value);
        _writer.Advance(4);
    }

    /// <summary>
    /// Writes a 64-bit signed integer in big-endian order.
    /// </summary>
    /// <param name="value">The value to write.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteI64(long value)
    {
        Span<byte> span = _writer.GetSpan(8);
        UnsafeMemory.WriteInt64BE(span, value);
        _writer.Advance(8);
    }

    /// <summary>
    /// Writes a 64-bit IEEE 754 floating-point value in big-endian order.
    /// </summary>
    /// <param name="value">The value to write.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteDouble(double value)
    {
        Span<byte> span = _writer.GetSpan(8);
        UnsafeMemory.WriteDoubleBE(span, value);
        _writer.Advance(8);
    }

    /// <summary>
    /// Writes a length-prefixed byte string.
    /// </summary>
    /// <param name="value">The bytes to write.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteString(ReadOnlySpan<byte> value)
    {
        WriteI32(value.Length);
        Span<byte> span = _writer.GetSpan(value.Length);
        value.CopyTo(span);
        _writer.Advance(value.Length);
    }

    /// <summary>
    /// Writes a length-prefixed UTF-8 encoded string.
    /// </summary>
    /// <param name="value">The string to write.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteString(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        WriteString(bytes);
    }

    /// <summary>
    /// Writes a field header consisting of a type and a field id.
    /// </summary>
    /// <param name="type">The field type.</param>
    /// <param name="fieldId">The field identifier.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteFieldHeader(TType type, short fieldId)
    {
        WriteByte((byte)type);
        WriteI16(fieldId);
    }

    /// <summary>
    /// Marks the beginning of a struct. No-op in this implementation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteStructBegin() { }

    /// <summary>
    /// Marks the end of a struct. No-op in this implementation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteStructEnd() { }

    /// <summary>
    /// Writes a map header consisting of key type, value type, and size.
    /// </summary>
    /// <param name="keyType">The type of map keys.</param>
    /// <param name="valueType">The type of map values.</param>
    /// <param name="size">The number of entries.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteMapBegin(TType keyType, TType valueType, int size)
    {
        WriteByte((byte)keyType);
        WriteByte((byte)valueType);
        WriteI32(size);
    }

    /// <summary>
    /// Marks the end of a map. No-op in this implementation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteMapEnd() { }

    /// <summary>
    /// Writes a list header consisting of element type and size.
    /// </summary>
    /// <param name="elemType">The type of list elements.</param>
    /// <param name="size">The number of elements.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteListBegin(TType elemType, int size)
    {
        WriteByte((byte)elemType);
        WriteI32(size);
    }

    /// <summary>
    /// Marks the end of a list. No-op in this implementation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteListEnd() { }

    /// <summary>
    /// Flushes the underlying writer if it is disposable.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Flush()
    {
        if (_writer is IDisposable disp)
            disp.Dispose();
    }

    /// <summary>
    /// Disposes the writer unless it was created with leaveOpen set to true.
    /// </summary>
    public void Dispose()
    {
        if (!_leaveOpen && _writer is IDisposable disp)
            disp.Dispose();
    }

    /// <summary>
    /// Writes a 16-bit integer using Unsafe for maximum performance.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe void WriteI16Unsafe(Span<byte> span, short value)
    {
        fixed (byte* ptr = &MemoryMarshal.GetReference(span))
        {
            short swapped = BinaryPrimitives.ReverseEndianness(value);
            Unsafe.WriteUnaligned(ptr, swapped);
        }
    }

    /// <summary>
    /// Writes a 32-bit integer using Unsafe for maximum performance.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe void WriteI32Unsafe(Span<byte> span, int value)
    {
        fixed (byte* ptr = &MemoryMarshal.GetReference(span))
        {
            int swapped = BinaryPrimitives.ReverseEndianness(value);
            Unsafe.WriteUnaligned(ptr, swapped);
        }
    }

    /// <summary>
    /// Writes a 64-bit integer using Unsafe for maximum performance.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe void WriteI64Unsafe(Span<byte> span, long value)
    {
        fixed (byte* ptr = &MemoryMarshal.GetReference(span))
        {
            long swapped = BinaryPrimitives.ReverseEndianness(value);
            Unsafe.WriteUnaligned(ptr, swapped);
        }
    }

    /// <summary>
    /// Writes a double using Unsafe for maximum performance.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe void WriteDoubleUnsafe(Span<byte> span, double value)
    {
        fixed (byte* ptr = &MemoryMarshal.GetReference(span))
        {
            ulong bits = (ulong)BitConverter.DoubleToInt64Bits(value);
            ulong swapped = BinaryPrimitives.ReverseEndianness(bits);
            Unsafe.WriteUnaligned(ptr, swapped);
        }
    }
}
