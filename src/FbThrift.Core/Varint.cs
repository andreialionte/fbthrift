namespace FbThrift.Core;

using System.Buffers;
using System.Runtime.CompilerServices;

/// <summary>
/// Provides ZigZag variable-length integer encoding and decoding for Thrift's compact protocol.
/// Optimized with System.Runtime.CompilerServices.Unsafe for performance.
/// </summary>
public static class Varint
{
    /// <summary>
    /// Gets the number of bytes required to encode a signed 64-bit value as a ZigZag varint.
    /// </summary>
    /// <param name="value">The value to measure.</param>
    /// <returns>The encoded byte size.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetEncodedSize(long value)
    {
        ulong u = ZigZagEncode(value);
        int size = 0;
        do
        {
            size++;
            u >>= 7;
        } while (u != 0);
        return size;
    }

    /// <summary>
    /// Encodes a signed 64-bit value as a ZigZag varint into the destination span.
    /// </summary>
    /// <param name="value">The value to encode.</param>
    /// <param name="destination">The span to write the encoded bytes into.</param>
    /// <returns>The number of bytes written.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Encode(long value, Span<byte> destination)
    {
        ulong u = ZigZagEncode(value);
        int offset = 0;
        do
        {
            byte b = (byte)(u & 0x7F);
            u >>= 7;
            if (u != 0)
                b |= 0x80;
            destination[offset++] = b;
        } while (u != 0);
        return offset;
    }

    /// <summary>
    /// Decodes a ZigZag varint from the source span into a signed 64-bit value.
    /// Optimized with Unsafe.ReadUnaligned for performance.
    /// </summary>
    /// <param name="source">The span containing the encoded varint bytes.</param>
    /// <param name="value">When the method returns, the decoded value.</param>
    /// <returns>The number of bytes read.</returns>
    /// <exception cref="EndOfStreamException">Thrown when the varint is truncated.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the varint is too long.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Decode(ReadOnlySpan<byte> source, out long value)
    {
        ulong result = 0;
        int shift = 0;
        int offset = 0;
        int length = source.Length;

        while (true)
        {
            if (offset >= length)
                throw new EndOfStreamException("Truncated varint.");

            byte b = source[offset++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                break;
            shift += 7;
            if (shift > 63)
                throw new InvalidOperationException("Varint too long.");
        }

        value = ZigZagDecode(result);
        return offset;
    }

    /// <summary>
    /// Decodes a ZigZag varint using Unsafe for maximum performance.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe int DecodeUnsafe(ReadOnlySpan<byte> source, out long value)
    {
        ulong result = 0;
        int shift = 0;
        int offset = 0;
        int length = source.Length;

        fixed (byte* ptr = source)
        {
            while (true)
            {
                if (offset >= length)
                    throw new EndOfStreamException("Truncated varint.");

                byte b = ptr[offset++];
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                    break;
                shift += 7;
                if (shift > 63)
                    throw new InvalidOperationException("Varint too long.");
            }
        }

        value = ZigZagDecode(result);
        return offset;
    }

    /// <summary>
    /// Encodes a ZigZag varint using Unsafe for maximum performance.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe int EncodeUnsafe(long value, Span<byte> destination)
    {
        ulong u = ZigZagEncode(value);
        int offset = 0;

        fixed (byte* ptr = destination)
        {
            do
            {
                byte b = (byte)(u & 0x7F);
                u >>= 7;
                if (u != 0)
                    b |= 0x80;
                ptr[offset++] = b;
            } while (u != 0);
        }
        return offset;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ZigZagEncode(long value) => (ulong)((value << 1) ^ (value >> 63));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ZigZagDecode(ulong value) => (long)(((value >> 1) ^ (ulong)(-(long)(value & 1))));
}
