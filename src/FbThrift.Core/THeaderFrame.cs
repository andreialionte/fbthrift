namespace FbThrift.Core;

/// <summary>
/// Provides encoding and decoding of THeader frames.
/// </summary>
public static class THeaderFrame
{
    /// <summary>The fixed size of a THeader frame header in bytes.</summary>
    public const int FixedHeaderSize = 8;

    /// <summary>
    /// Writes a THeader frame header followed by the payload into the destination span.
    /// </summary>
    /// <param name="destination">The span to write the frame into.</param>
    /// <param name="payload">The payload bytes to write after the header.</param>
    /// <returns>The total number of bytes written (header + payload).</returns>
    /// <exception cref="InvalidOperationException">Thrown when the payload exceeds ushort.MaxValue.</exception>
    public static int WriteFrame(Span<byte> destination, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > ushort.MaxValue)
            throw new InvalidOperationException("Payload too large for THeader frame.");

        destination[0] = (byte)(THeaderFrameConstants.Magic >> 8);
        destination[1] = (byte)(THeaderFrameConstants.Magic & 0xFF);
        destination[2] = THeaderFrameConstants.Version;
        destination[3] = 0;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(4, 2), (ushort)payload.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(6, 2), 0);
        payload.CopyTo(destination.Slice(FixedHeaderSize));
        return FixedHeaderSize + payload.Length;
    }

    /// <summary>
    /// Reads a THeader frame header and reports the total frame length.
    /// </summary>
    /// <param name="header">The header bytes to read.</param>
    /// <param name="frameLength">When the method returns, the total frame length in bytes, or 0 on failure.</param>
    /// <returns>-1 if the header is too short, -2 if the magic number is invalid, otherwise the total frame length.</returns>
    public static int TryReadFrameLength(ReadOnlySpan<byte> header, out int frameLength)
    {
        if (header.Length < FixedHeaderSize)
        {
            frameLength = 0;
            return -1;
        }

        ushort magic = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(header);
        if (magic != THeaderFrameConstants.Magic)
        {
            frameLength = 0;
            return -2;
        }

        frameLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(header.Slice(4));
        return FixedHeaderSize + frameLength;
    }
}
