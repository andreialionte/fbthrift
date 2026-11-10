// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Quic;

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

/// <summary>
/// QUIC short header with explicit struct layout for zero-copy header manipulation.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 16)]
public struct QuicShortHeader
{
    [FieldOffset(0)] public byte FormByte;           // 0x80 | CID_LEN
    [FieldOffset(1)] public byte ConnectionIdLength; // 0-20
    [FieldOffset(2)] public ulong PacketNumber;      // 64-bit packet number (atomic)
    [FieldOffset(10)] public ushort Length;          // Payload length
    // Remaining: payload + tag
}

/// <summary>
/// QUIC packet keys for encryption/decryption.
/// </summary>
public readonly struct QuicPacketKeys
{
    public ReadOnlyMemory<byte> Key { get; init; }
    public ReadOnlyMemory<byte> Iv { get; init; }
    public ReadOnlyMemory<byte> HeaderProtectionKey { get; init; }
}

/// <summary>
/// QUIC packet types following tquic's packet.rs and codec.rs
/// See: https://github.com/Tencent/tquic/blob/develop/src/packet.rs
/// See: https://github.com/Tencent/tquic/blob/develop/src/codec.rs
/// </summary>
public enum QuicPacketType : byte
{
    Initial = 0xC0,
    ZeroRTT = 0xD0,
    Handshake = 0xE0,
    Retry = 0xF0,
    OneRTT = 0x40,
    VersionNegotiation = 0x80
}

/// <summary>
/// QUIC packet header following tquic's packet.rs
/// </summary>
public readonly struct QuicPacketHeader
{
    public QuicPacketType PacketType { get; }
    public uint Version { get; }
    public ReadOnlyMemory<byte> DestinationConnectionId { get; }
    public ReadOnlyMemory<byte> SourceConnectionId { get; }
    public ReadOnlyMemory<byte> Token { get; }
    public ulong PacketNumber { get; }
    public int PacketNumberLength { get; }
    public int HeaderLength { get; }

    public QuicPacketHeader(
        QuicPacketType packetType,
        uint version,
        ReadOnlyMemory<byte> destCid,
        ReadOnlyMemory<byte> srcCid,
        ReadOnlyMemory<byte> token,
        ulong packetNumber,
        int packetNumberLength,
        int headerLength)
    {
        PacketType = packetType;
        Version = version;
        DestinationConnectionId = destCid;
        SourceConnectionId = srcCid;
        Token = token;
        PacketNumber = packetNumber;
        PacketNumberLength = packetNumberLength;
        HeaderLength = headerLength;
    }

    public static QuicPacketHeader Parse(ReadOnlySpan<byte> buffer, out int headerLength)
    {
        if (buffer.Length < 1)
            throw new ArgumentException("Buffer too short for packet header");

        var firstByte = buffer[0];
        var packetType = (QuicPacketType)(firstByte & 0xF0);

        if (packetType == QuicPacketType.VersionNegotiation)
        {
            return ParseVersionNegotiation(buffer, out headerLength);
        }

        if (packetType != QuicPacketType.OneRTT)
        {
            return ParseLongHeader(buffer, packetType, out headerLength);
        }
        else
        {
            return ParseShortHeader(buffer, out headerLength);
        }
    }

    private static QuicPacketHeader ParseVersionNegotiation(ReadOnlySpan<byte> buffer, out int headerLength)
    {
        if (buffer.Length < 9)
            throw new InvalidDataException("Version negotiation packet too short");

        var version = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(1, 4));
        var destCidLen = buffer[5];
        var destCid = buffer.Slice(6, destCidLen).ToArray();
        var srcCidLen = buffer[6 + destCidLen];
        var srcCidStart = 6 + destCidLen + 1;
        var srcCid = buffer.Slice(srcCidStart, srcCidLen).ToArray();
        
        headerLength = 1 + 4 + 1 + destCidLen + 1 + srcCidLen;
        return new QuicPacketHeader(
            QuicPacketType.VersionNegotiation, version,
            new ReadOnlyMemory<byte>(destCid), new ReadOnlyMemory<byte>(srcCid), ReadOnlyMemory<byte>.Empty,
            0, 0, headerLength);
    }

    private static QuicPacketHeader ParseLongHeader(ReadOnlySpan<byte> buffer, QuicPacketType packetType, out int headerLength)
    {
        if (buffer.Length < 9)
            throw new InvalidDataException("Long header packet too short");

        var version = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(1, 4));
        var destCidLen = buffer[5];
        var destCidStart = 6;
        var destCid = buffer.Slice(destCidStart, destCidLen).ToArray();
        var srcCidLen = buffer[6 + destCidLen];
        var srcCidStart = 6 + destCidLen + 1;
        var srcCid = buffer.Slice(srcCidStart, srcCidLen).ToArray();
        int offset = 6 + destCidLen + 1 + srcCidLen;

        if (packetType == QuicPacketType.Retry)
        {
            var tokenLength = buffer.Length - offset;
            var token = buffer.Slice(offset).ToArray();
            headerLength = offset + tokenLength;
            return new QuicPacketHeader(
                QuicPacketType.Retry, 0, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty,
                buffer.Slice(offset).ToArray(), 0, 0, buffer.Length);
        }

        var tokenLen = (int)Varint.Decode(buffer.Slice(offset), out var consumed);
        offset += consumed;

        var tokenData = buffer.Slice(offset, tokenLen).ToArray();
        offset += tokenLen;

        var packetNumberLength = 4;
        var packetNumber = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(offset, packetNumberLength));
        offset += packetNumberLength;

        headerLength = offset;
        return new QuicPacketHeader(
            packetType, 0,
            new ReadOnlyMemory<byte>(destCid), new ReadOnlyMemory<byte>(srcCid),
            new ReadOnlyMemory<byte>(tokenData), packetNumber, packetNumberLength, headerLength);
    }

    private static QuicPacketHeader ParseShortHeader(ReadOnlySpan<byte> buffer, out int headerLength)
    {
        var firstByte = buffer[0];
        var keyPhase = (firstByte & 0x04) != 0;
        var packetNumberLength = (firstByte & 0x03) + 1;
        
        ulong packetNumber = 0;
        for (int i = 0; i < (firstByte & 0x03) + 1; i++)
        {
            packetNumber = (packetNumber << 8) | buffer[1 + i];
        }
        
        headerLength = 1 + (firstByte & 0x03) + 1;
        
        return new QuicPacketHeader(
            QuicPacketType.OneRTT, 0,
            ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty,
            ReadOnlyMemory<byte>.Empty, packetNumber, (firstByte & 0x03) + 1, 1 + (firstByte & 0x03) + 1);
    }
}

/// <summary>
/// QUIC packet encoder/decoder following tquic's codec.rs
/// </summary>
public static class QuicPacketCodec
{
    public static int EncryptPacket(
        ReadOnlySpan<byte> header,
        ReadOnlySpan<byte> payload,
        Span<byte> destination,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> iv,
        ulong packetNumber)
    {
        throw new NotImplementedException("Encryption requires Aead implementation");
    }

    public static int DecryptPacket(
        ReadOnlySpan<byte> header,
        ReadOnlySpan<byte> encryptedPayload,
        Span<byte> destination,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> iv,
        ulong packetNumber,
        out int payloadLength)
    {
        throw new NotImplementedException();
    }

    private static readonly ArrayPool<byte> _cidPool = ArrayPool<byte>.Shared;

    public static ReadOnlyMemory<byte> GenerateConnectionId(int length = 8)
    {
        var buffer = _cidPool.Rent(length);
        try
        {
            var cid = new byte[length];
            RandomNumberGenerator.Fill(cid);
            if (cid[0] == 0) cid[0] = 1;
            cid.CopyTo(buffer);
            return buffer.AsMemory(0, length);
        }
        finally
        {
            _cidPool.Return(buffer);
        }
    }

    public static int GetPacketNumberLength(ulong packetNumber, ulong largestAcked)
    {
        var delta = packetNumber > largestAcked ? packetNumber - largestAcked : largestAcked - packetNumber;
        
        if (delta < (1 << 8)) return 1;
        if (delta < (1 << 16)) return 2;
        if (delta < (1 << 24)) return 3;
        return 4;
    }

    public static ulong DecodePacketNumber(ulong truncated, int length, ulong expected)
    {
        ulong best = 0;
        ulong bestDiff = ulong.MaxValue;
        
        for (int i = 0; i < 3; i++)
        {
            var candidate = (truncated | ((ulong)i << (length * 8)));
            var diff = candidate > expected ? candidate - expected : expected - candidate;
            if (diff < bestDiff)
            {
                best = candidate;
            }
        }
        
        return best;
    }

    /// <summary>
    /// Atomically stamps a packet number into a QUIC short header buffer.
    /// Uses Interlocked for lock-free atomic increment and Unsafe for zero-copy write.
    /// </summary>
    /// <param name="headerSpan">Span covering the QUIC short header (must include packet number field)</param>
    /// <param name="packetNumberOffset">Offset of packet number field within the header span</param>
    /// <param name="packetNumberLength">Length of packet number field (1-4 bytes)</param>
    /// <returns>The packet number that was stamped</returns>
    public static long StampPacketNumberAtomic(Span<byte> headerSpan, int packetNumberOffset, int packetNumberLength)
    {
        // Atomically reserve a sequence number
        long uniquePacketNumber = Interlocked.Increment(ref _globalPacketSequence);

        // Directly write into the raw memory span at the exact offset using zero-overhead primitives
        ref byte packetNumLocation = ref MemoryMarshal.GetReference(headerSpan.Slice(packetNumberOffset, packetNumberLength));
        
        // Write as big-endian (network byte order)
        ulong pn = (ulong)uniquePacketNumber;
        for (int i = packetNumberLength - 1; i >= 0; i--)
        {
            Unsafe.Add(ref packetNumLocation, i) = (byte)(pn & 0xFF);
            pn >>= 8;
        }

        return uniquePacketNumber;
    }

    /// <summary>
    /// Stamps a packet number into a QUIC short header using explicit struct layout.
    /// </summary>
    public static void StampPacketNumberStruct(ref QuicShortHeader header, long packetNumber)
    {
        var pn = BinaryPrimitives.ReverseEndianness((long)packetNumber);
        ref byte dest = ref Unsafe.AsRef<byte>(Unsafe.AsPointer(ref header) + 2);
        Unsafe.WriteUnaligned(ref dest, pn);
    }

    private static long _globalPacketSequence = 0;

    /// <summary>
    /// Gets the next global packet sequence number atomically.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long NextPacketNumber() => Interlocked.Increment(ref _globalPacketSequence);
}
