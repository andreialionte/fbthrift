using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FbThrift.Core.Memory;

namespace FbThrift.Protocols.Compact;

/// <summary>
/// Writes Thrift values using the Compact protocol wire encoding.
/// Optimized with System.Runtime.CompilerServices.Unsafe for performance.
/// </summary>
public sealed class CompactProtocolWriter : ITProtocolWriter
{
    private readonly System.Buffers.IBufferWriter<byte> _writer;

    /// <summary>
    /// Initializes a new instance of the <see cref="CompactProtocolWriter"/> class.
    /// </summary>
    /// <param name="writer">The underlying buffer writer.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="writer"/> is null.</exception>
    public CompactProtocolWriter(System.Buffers.IBufferWriter<byte> writer)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Span<byte> GetSpan(int size) => _writer.GetSpan(size);

    /// <inheritdoc/>
    public void WriteMessageBegin(string name, TMessageType messageType, int seqId)
    {
        GetSpan(1)[0] = 0; // protocol version
        _writer.Advance(1);
        GetSpan(1)[0] = (byte)messageType;
        _writer.Advance(1);
        int written = Varint.Encode(seqId, GetSpan(Varint.GetEncodedSize(seqId)));
        _writer.Advance(written);
        WriteString(name);
    }

    /// <inheritdoc/>
    public void WriteMessageEnd() { }

    /// <inheritdoc/>
    public void WriteStructBegin(string? name) { }

    /// <inheritdoc/>
    public void WriteStructEnd() { }

    /// <inheritdoc/>
    public void WriteFieldBegin(TType type, short fieldId)
    {
        if (fieldId > 0 && fieldId < 15)
        {
            GetSpan(1)[0] = (byte)((byte)type << 4 | fieldId);
            _writer.Advance(1);
        }
        else
        {
            GetSpan(1)[0] = (byte)((byte)type << 4 | 15);
            _writer.Advance(1);
            int written = Varint.Encode(fieldId, GetSpan(Varint.GetEncodedSize(fieldId)));
            _writer.Advance(written);
        }
    }

    /// <inheritdoc/>
    public void WriteFieldEnd() { }

    /// <inheritdoc/>
    public void WriteStop()
    {
        GetSpan(1)[0] = 0;
        _writer.Advance(1);
    }

    /// <inheritdoc/>
    public void WriteMapBegin(TType keyType, TType valueType, int size)
    {
        GetSpan(1)[0] = (byte)((byte)keyType << 4 | (byte)valueType);
        _writer.Advance(1);
        int written = Varint.Encode(size, GetSpan(Varint.GetEncodedSize(size)));
        _writer.Advance(written);
    }

    /// <inheritdoc/>
    public void WriteMapEnd() { }

    /// <inheritdoc/>
    public void WriteListBegin(TType elemType, int size)
    {
        GetSpan(1)[0] = (byte)elemType;
        _writer.Advance(1);
        int written = Varint.Encode(size, GetSpan(Varint.GetEncodedSize(size)));
        _writer.Advance(written);
    }

    /// <inheritdoc/>
    public void WriteListEnd() { }

    /// <inheritdoc/>
    public void WriteSetBegin(TType elemType, int size)
    {
        GetSpan(1)[0] = (byte)elemType;
        _writer.Advance(1);
        int written = Varint.Encode(size, GetSpan(Varint.GetEncodedSize(size)));
        _writer.Advance(written);
    }

    /// <inheritdoc/>
    public void WriteSetEnd() { }

    /// <inheritdoc/>
    public void WriteBool(bool value)
    {
        GetSpan(1)[0] = value ? (byte)1 : (byte)0;
        _writer.Advance(1);
    }

    /// <inheritdoc/>
    public void WriteByte(byte value)
    {
        GetSpan(1)[0] = value;
        _writer.Advance(1);
    }

    /// <inheritdoc/>
    public void WriteI16(short value)
    {
        Span<byte> span = GetSpan(2);
        UnsafeMemory.WriteInt16BE(span, value);
        _writer.Advance(2);
    }

    /// <inheritdoc/>
    public void WriteI32(int value)
    {
        Span<byte> span = GetSpan(4);
        UnsafeMemory.WriteInt32BE(span, value);
        _writer.Advance(4);
    }

    /// <inheritdoc/>
    public void WriteI64(long value)
    {
        Span<byte> span = GetSpan(8);
        UnsafeMemory.WriteInt64BE(span, value);
        _writer.Advance(8);
    }

    /// <inheritdoc/>
    public void WriteDouble(double value)
    {
        Span<byte> span = GetSpan(8);
        UnsafeMemory.WriteDoubleBE(span, value);
        _writer.Advance(8);
    }

    /// <inheritdoc/>
    public void WriteString(string? value)
    {
        if (value is null)
        {
            int written = Varint.Encode(-1, GetSpan(Varint.GetEncodedSize(-1)));
            _writer.Advance(written);
            return;
        }

        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value);
        int sizeWritten = Varint.Encode(bytes.Length, GetSpan(Varint.GetEncodedSize(bytes.Length)));
        _writer.Advance(sizeWritten);
        bytes.AsSpan().CopyTo(GetSpan(bytes.Length));
        _writer.Advance(bytes.Length);
    }

    /// <inheritdoc/>
    public void WriteBinary(ReadOnlySpan<byte> value)
    {
        int sizeWritten = Varint.Encode(value.Length, GetSpan(Varint.GetEncodedSize(value.Length)));
        _writer.Advance(sizeWritten);
        value.CopyTo(GetSpan(value.Length));
        _writer.Advance(value.Length);
    }

    /// <inheritdoc/>
    public void Flush()
    {
        if (_writer is IDisposable disp)
            disp.Dispose();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_writer is IDisposable disp)
            disp.Dispose();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteInt16BigEndian(Span<byte> span, short value)
    {
        UnsafeMemory.WriteInt16BE(span, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteInt32BigEndian(Span<byte> span, int value)
    {
        UnsafeMemory.WriteInt32BE(span, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteInt64BigEndian(Span<byte> span, long value)
    {
        UnsafeMemory.WriteInt64BE(span, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteDoubleBigEndian(Span<byte> span, double value)
    {
        UnsafeMemory.WriteDoubleBE(span, value);
    }

    /// <summary>
    /// Writes a 16-bit integer using Unsafe for maximum performance.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe void WriteInt16BigEndianUnsafe(Span<byte> span, short value)
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
    private unsafe void WriteInt32BigEndianUnsafe(Span<byte> span, int value)
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
    private unsafe void WriteInt64BigEndianUnsafe(Span<byte> span, long value)
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
    private unsafe void WriteDoubleBigEndianUnsafe(Span<byte> span, double value)
    {
        fixed (byte* ptr = &MemoryMarshal.GetReference(span))
        {
            ulong bits = (ulong)BitConverter.DoubleToInt64Bits(value);
            ulong swapped = BinaryPrimitives.ReverseEndianness(bits);
            Unsafe.WriteUnaligned(ptr, swapped);
        }
    }
}
