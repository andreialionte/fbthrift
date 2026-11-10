// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Quic;

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using FBThrift;
using FbThrift.Core.Transport;

/// <summary>
/// QUIC frame types following tquic frame definitions.
/// See: https://github.com/Tencent/tquic/blob/develop/src/frame.rs
/// </summary>
public enum QuicFrameType : byte
{
    Padding = 0x00,
    Ping = 0x01,
    Ack = 0x02,
    AckFrequency = 0x03,
    ResetStream = 0x04,
    StopSending = 0x05,
    Crypto = 0x06,
    NewToken = 0x07,
    Stream = 0x08, // 0x08-0x0F (variable length)
    MaxData = 0x10,
    MaxStreamData = 0x11,
    MaxStreamsBidi = 0x12,
    MaxStreamsUni = 0x13,
    DataBlocked = 0x14,
    StreamDataBlocked = 0x15,
    StreamsBlockedBidi = 0x16,
    StreamsBlockedUni = 0x17,
    NewConnectionId = 0x18,
    RetireConnectionId = 0x19,
    PathChallenge = 0x1A,
    PathResponse = 0x1B,
    ConnectionClose = 0x1C,
    HandshakeDone = 0x1E,
}

/// <summary>
/// QUIC frame base class following tquic frame structure.
/// </summary>
public abstract class QuicFrame
{
    public QuicFrameType Type { get; }
    
    protected QuicFrame(QuicFrameType type)
    {
        Type = type;
    }
    
    public abstract int Encode(Span<byte> destination);
    public abstract int GetEncodedSize();
    
    public static QuicFrame Decode(ReadOnlySpan<byte> buffer, out int bytesConsumed)
    {
        if (buffer.Length == 0)
            throw new InvalidDataException("Empty frame buffer");
            
        var firstByte = buffer[0];
        
        // Check for Stream frame first (0x08-0x0F)
        if ((firstByte & 0xF8) == 0x08)
        {
            return DecodeStream(buffer, out bytesConsumed);
        }
        
        var frameType = (QuicFrameType)firstByte;
        return frameType switch
        {
            QuicFrameType.Padding => DecodePadding(buffer, out bytesConsumed),
            QuicFrameType.Ping => DecodePing(buffer, out bytesConsumed),
            QuicFrameType.Ack => DecodeAck(buffer, out bytesConsumed),
            QuicFrameType.ResetStream => DecodeResetStream(buffer, out bytesConsumed),
            QuicFrameType.StopSending => DecodeStopSending(buffer, out bytesConsumed),
            QuicFrameType.Crypto => DecodeCrypto(buffer, out bytesConsumed),
            QuicFrameType.NewToken => DecodeNewToken(buffer, out bytesConsumed),
            QuicFrameType.MaxData => DecodeMaxData(buffer, out bytesConsumed),
            QuicFrameType.MaxStreamData => DecodeMaxStreamData(buffer, out bytesConsumed),
            QuicFrameType.MaxStreamsBidi => DecodeMaxStreams(buffer, out bytesConsumed, true),
            QuicFrameType.MaxStreamsUni => DecodeMaxStreams(buffer, out bytesConsumed, false),
            QuicFrameType.DataBlocked => DecodeDataBlocked(buffer, out bytesConsumed),
            QuicFrameType.StreamDataBlocked => DecodeStreamDataBlocked(buffer, out bytesConsumed),
            QuicFrameType.StreamsBlockedBidi => DecodeStreamsBlocked(buffer, out bytesConsumed, true),
            QuicFrameType.StreamsBlockedUni => DecodeStreamsBlocked(buffer, out bytesConsumed, false),
            QuicFrameType.NewConnectionId => DecodeNewConnectionId(buffer, out bytesConsumed),
            QuicFrameType.RetireConnectionId => DecodeRetireConnectionId(buffer, out bytesConsumed),
            QuicFrameType.PathChallenge => DecodePathChallenge(buffer, out bytesConsumed),
            QuicFrameType.PathResponse => DecodePathResponse(buffer, out bytesConsumed),
            QuicFrameType.ConnectionClose => DecodeConnectionClose(buffer, out bytesConsumed),
            QuicFrameType.HandshakeDone => DecodeHandshakeDone(buffer, out bytesConsumed),
            _ => throw new InvalidDataException($"Unknown frame type: {frameType}")
        };
    }
    
    // Frame decoders (simplified - full implementation in tquic/src/frame.rs)
    private static QuicFrame DecodePadding(ReadOnlySpan<byte> buffer, out int bytesConsumed)
    {
        bytesConsumed = 1;
        return new PaddingFrame();
    }
    
    private static QuicFrame DecodePing(ReadOnlySpan<byte> buffer, out int bytesConsumed)
    {
        bytesConsumed = 1;
        return new PingFrame();
    }
    
private static QuicFrame DecodeAck(ReadOnlySpan<byte> buffer, out int bytesConsumed)
    {
        int offset = 1;
        var largestAcked = Varint.Decode(buffer[1..], out var consumed);
        offset += consumed;
        var ackDelay = Varint.Decode(buffer[offset..], out consumed);
        offset += consumed;
        var ackRangeCount = Varint.Decode(buffer[offset..], out consumed);
        offset += consumed;
        
        var ranges = new List<(ulong Start, ulong End)>();
        
        // Total ranges = ackRangeCount + 1 (first range + additional ranges)
        if (ackRangeCount + 1 > 0)
        {
            var firstAckRange = Varint.Decode(buffer[offset..], out var consumed1);
            offset += consumed1;
            var largest = largestAcked;
            var smallest = largest - firstAckRange;
            ranges.Add((Start: smallest, End: largest));
            
            var prevSmallest = smallest;
            var prevLargest = largest;
            
            // Additional ranges: gap and range (ackRangeCount additional ranges)
            for (ulong i = 0; i < ackRangeCount; i++)
            {
                var gap = Varint.Decode(buffer[offset..], out var consumedGap);
                offset += consumedGap;
                var range = Varint.Decode(buffer[offset..], out var consumedRange);
                offset += consumedRange;
                largest = prevSmallest - gap - 2;
                smallest = largest - range;
                ranges.Add((Start: smallest, End: largest));
                prevSmallest = smallest;
                prevLargest = largest;
            }
        }
        
        bytesConsumed = offset;
        return new AckFrame(largestAcked, ackDelay, ranges);
    }
    
    private static QuicFrame DecodeResetStream(ReadOnlySpan<byte> buffer, out int bytesConsumed)
    {
        int offset = 1;
        var streamId = Varint.Decode(buffer[1..], out var consumed1);
        offset += consumed1;
        var errorCode = Varint.Decode(buffer[offset..], out var consumed2);
        offset += consumed2;
        var finalSize = Varint.Decode(buffer[offset..], out var consumed3);
        offset += consumed3;
        bytesConsumed = offset;
        return new ResetStreamFrame(streamId, errorCode, finalSize);
    }
    
    private static QuicFrame DecodeStopSending(ReadOnlySpan<byte> buffer, out int bytesConsumed)
    {
        int offset = 1;
        var streamId = Varint.Decode(buffer[1..], out var consumed1);
        offset += consumed1;
        var errorCode = Varint.Decode(buffer[offset..], out var consumed2);
        offset += consumed2;
        bytesConsumed = offset;
        return new StopSendingFrame(streamId, errorCode);
    }
    
    private static QuicFrame DecodeCrypto(ReadOnlySpan<byte> buffer, out int bytesConsumed)
    {
        int offset = 1;
        var offset_ = Varint.Decode(buffer[1..], out var consumed1);
        offset += consumed1;
        var length = Varint.Decode(buffer[offset..], out var consumed2);
        offset += consumed2;
        var data = buffer.Slice(offset, (int)length).ToArray();
        offset += (int)length;
        bytesConsumed = offset;
        return new CryptoFrame(offset_, data);
    }
    
    private static QuicFrame DecodeNewToken(ReadOnlySpan<byte> buffer, out int bytesConsumed)
    {
        int offset = 1;
        var length = Varint.Decode(buffer[1..], out var consumed1);
        offset += consumed1;
        var token = buffer.Slice(offset, (int)length).ToArray();
        offset += (int)length;
        bytesConsumed = offset;
        return new NewTokenFrame(token);
    }
    
    private static QuicFrame DecodeStream(ReadOnlySpan<byte> buffer, out int bytesConsumed)
    {
        var typeByte = buffer[0];
        bool fin = (typeByte & 0x01) != 0;
        bool lenPresent = (typeByte & 0x02) != 0;
        bool offPresent = (typeByte & 0x04) != 0;
        
        int offset = 1;
        var streamId = Varint.Decode(buffer[1..], out var consumed1);
        offset += consumed1;
        
        ulong offset_ = 0;
        if (offPresent)
        {
            offset_ = Varint.Decode(buffer[offset..], out var consumed2);
            offset += consumed2;
        }
        
        int length = 0;
        if (lenPresent)
        {
            length = (int)Varint.Decode(buffer[offset..], out var consumed3);
            offset += consumed3;
        }
        else
        {
            length = buffer.Length - offset;
        }
        
        var data = buffer.Slice(offset, length).ToArray();
        offset += length;
        
        bytesConsumed = offset;
        return new StreamFrame(streamId, offset_, data, fin);
    }
    
    private static QuicFrame DecodeMaxData(ReadOnlySpan<byte> buffer, out int bytesConsumed)
    {
        var maxData = Varint.Decode(buffer[1..], out var consumed);
        bytesConsumed = 1 + consumed;
        return new MaxDataFrame(maxData);
    }
    
private static QuicFrame DecodeMaxStreamData(ReadOnlySpan<byte> buffer, out int bytesConsumed)
    {
        int offset = 1;
        var streamId = Varint.Decode(buffer[1..], out var consumed1);
        offset += consumed1;
        var maxData = Varint.Decode(buffer[offset..], out var consumed2);
        offset += consumed2;
        bytesConsumed = offset;
        return new MaxStreamDataFrame(streamId, maxData);
    }
    
    private static QuicFrame DecodeMaxStreams(ReadOnlySpan<byte> buffer, out int bytesConsumed, bool bidi)
    {
        var maxStreams = Varint.Decode(buffer[1..], out var consumed);
        bytesConsumed = 1 + consumed;
        return new MaxStreamsFrame(maxStreams, bidi);
    }
    
    private static QuicFrame DecodeDataBlocked(ReadOnlySpan<byte> buffer, out int bytesConsumed)
    {
        var limit = Varint.Decode(buffer[1..], out var consumed);
        bytesConsumed = 1 + consumed;
        return new DataBlockedFrame(limit);
    }
    
private static QuicFrame DecodeStreamDataBlocked(ReadOnlySpan<byte> buffer, out int bytesConsumed)
    {
        int offset = 1;
        var streamId = Varint.Decode(buffer[1..], out var consumed1);
        offset += consumed1;
        var limit = Varint.Decode(buffer[offset..], out var consumed2);
        offset += consumed2;
        bytesConsumed = offset;
        return new StreamDataBlockedFrame(streamId, limit);
    }
    
    private static QuicFrame DecodeStreamsBlocked(ReadOnlySpan<byte> buffer, out int bytesConsumed, bool bidi)
    {
        var limit = Varint.Decode(buffer[1..], out var consumed);
        bytesConsumed = 1 + consumed;
        return new StreamsBlockedFrame(limit, bidi);
    }
    
    private static QuicFrame DecodeNewConnectionId(ReadOnlySpan<byte> buffer, out int bytesConsumed)
    {
        int offset = 1;
        var seq = Varint.Decode(buffer[1..], out var consumed1);
        offset += consumed1;
        var retirePriorTo = Varint.Decode(buffer[offset..], out var consumed2);
        offset += consumed2;
        var length = buffer[offset++];
        var connectionId = buffer.Slice(offset, length).ToArray();
        offset += length;
        var resetToken = buffer.Slice(offset, 16).ToArray();
        offset += 16;
        bytesConsumed = offset;
        return new NewConnectionIdFrame(seq, retirePriorTo, connectionId, resetToken);
    }
    
    private static QuicFrame DecodeRetireConnectionId(ReadOnlySpan<byte> buffer, out int bytesConsumed)
    {
        var seq = Varint.Decode(buffer[1..], out var consumed);
        bytesConsumed = 1 + consumed;
        return new RetireConnectionIdFrame(seq);
    }
    
    private static QuicFrame DecodePathChallenge(ReadOnlySpan<byte> buffer, out int bytesConsumed)
    {
        var data = buffer.Slice(1, 8).ToArray();
        bytesConsumed = 9;
        return new PathChallengeFrame(data);
    }
    
    private static QuicFrame DecodePathResponse(ReadOnlySpan<byte> buffer, out int bytesConsumed)
    {
        var data = buffer.Slice(1, 8).ToArray();
        bytesConsumed = 9;
        return new PathResponseFrame(data);
    }
    
    private static QuicFrame DecodeConnectionClose(ReadOnlySpan<byte> buffer, out int bytesConsumed)
    {
        int offset = 1;
        var errorCode = Varint.Decode(buffer[1..], out var consumed1);
        offset += consumed1;
        var frameType = Varint.Decode(buffer[offset..], out var consumed2);
        offset += consumed2;
        var reasonLength = Varint.Decode(buffer[offset..], out var consumed3);
        offset += consumed3;
        var reason = System.Text.Encoding.UTF8.GetString(buffer.Slice(offset, (int)reasonLength).ToArray());
        offset += (int)reasonLength;
        bytesConsumed = offset;
        return new ConnectionCloseFrame(errorCode, frameType, reason);
    }
    
    private static QuicFrame DecodeHandshakeDone(ReadOnlySpan<byte> buffer, out int bytesConsumed)
    {
        bytesConsumed = 1;
        return new HandshakeDoneFrame();
    }
}

// Frame implementations (following tquic frame.rs structure)
public sealed class PaddingFrame : QuicFrame
{
    public PaddingFrame() : base(QuicFrameType.Padding) { }
    public override int Encode(Span<byte> destination) { destination[0] = 0x00; return 1; }
    public override int GetEncodedSize() => 1;
}

public sealed class PingFrame : QuicFrame
{
    public PingFrame() : base(QuicFrameType.Ping) { }
    public override int Encode(Span<byte> destination) { destination[0] = 0x01; return 1; }
    public override int GetEncodedSize() => 1;
}

public sealed class AckFrame : QuicFrame
{
    public ulong LargestAcknowledged { get; }
    public ulong AckDelay { get; }
    public IReadOnlyList<(ulong Start, ulong End)> AckRanges { get; }
    
    public AckFrame(ulong largestAcknowledged, ulong ackDelay, IReadOnlyList<(ulong Start, ulong End)> ackRanges) 
        : base(QuicFrameType.Ack)
    {
        LargestAcknowledged = largestAcknowledged;
        AckDelay = ackDelay;
        AckRanges = ackRanges;
    }
    
    public override int Encode(Span<byte> destination)
    {
        int offset = 0;
        destination[offset++] = (byte)QuicFrameType.Ack;
        offset += Varint.Encode((ulong)LargestAcknowledged, destination[offset..]);
        offset += Varint.Encode((ulong)AckDelay, destination[offset..]);
        
        // Encode ack range count (number of ADDITIONAL ranges beyond the first)
        var rangeCount = (ulong)(AckRanges.Count > 0 ? AckRanges.Count - 1 : 0);
        offset += Varint.Encode(rangeCount, destination[offset..]);
        
        if (AckRanges.Count > 0)
        {
            // First ack range: largest - smallest
            var firstRange = AckRanges[0];
            var firstAckRange = firstRange.End - firstRange.Start;
            offset += Varint.Encode(firstAckRange, destination[offset..]);
            
            var prevSmallest = firstRange.Start;
            var prevLargest = firstRange.End;
            
            // Additional ranges: gap and range
            for (ulong i = 1; i < (ulong)AckRanges.Count; i++)
            {
                var range = AckRanges[(int)i];
                var gap = prevSmallest - range.End - 2;
                var rangeLen = range.End - range.Start;
                
                offset += Varint.Encode(gap, destination[offset..]);
                offset += Varint.Encode(rangeLen, destination[offset..]);
                
                prevSmallest = range.Start;
                prevLargest = range.End;
            }
        }
        
        return offset;
    }
    
    public override int GetEncodedSize()
    {
        int size = 1 + Varint.GetEncodedSize((ulong)LargestAcknowledged) + Varint.GetEncodedSize((ulong)AckDelay) + Varint.GetEncodedSize((ulong)AckRanges.Count);
        
        if (AckRanges.Count > 0)
        {
            var firstRange = AckRanges[0];
            size += Varint.GetEncodedSize(firstRange.End - firstRange.Start);
            
            var prevSmallest = firstRange.Start;
            for (int i = 1; i < AckRanges.Count; i++)
            {
                var range = AckRanges[i];
                var gap = prevSmallest - range.End - 2;
                var rangeLen = range.End - range.Start;
                size += Varint.GetEncodedSize(gap) + Varint.GetEncodedSize(rangeLen);
                prevSmallest = range.Start;
            }
        }
        
        return size;
    }
}

public sealed class ResetStreamFrame : QuicFrame
{
    public ulong StreamId { get; }
    public ulong ErrorCode { get; }
    public ulong FinalSize { get; }
    
    public ResetStreamFrame(ulong streamId, ulong errorCode, ulong finalSize) : base(QuicFrameType.ResetStream)
    {
        StreamId = streamId;
        ErrorCode = errorCode;
        FinalSize = finalSize;
    }
    
    public override int Encode(Span<byte> destination)
    {
        int offset = 0;
        destination[offset++] = (byte)QuicFrameType.ResetStream;
        offset += Varint.Encode((ulong)StreamId, destination[offset..]);
        offset += Varint.Encode((ulong)ErrorCode, destination[offset..]);
        offset += Varint.Encode((ulong)FinalSize, destination[offset..]);
        return offset;
    }
    
    public override int GetEncodedSize() => 1 + Varint.GetEncodedSize((ulong)StreamId) + Varint.GetEncodedSize((ulong)ErrorCode) + Varint.GetEncodedSize((ulong)FinalSize);
}

public sealed class StopSendingFrame : QuicFrame
{
    public ulong StreamId { get; }
    public ulong ErrorCode { get; }
    
    public StopSendingFrame(ulong streamId, ulong errorCode) : base(QuicFrameType.StopSending)
    {
        StreamId = streamId;
        ErrorCode = errorCode;
    }
    
    public override int Encode(Span<byte> destination)
    {
        int offset = 0;
        destination[offset++] = (byte)QuicFrameType.StopSending;
        offset += Varint.Encode((ulong)StreamId, destination[offset..]);
        offset += Varint.Encode((ulong)ErrorCode, destination[offset..]);
        return offset;
    }
    
    public override int GetEncodedSize() => 1 + Varint.GetEncodedSize((ulong)StreamId) + Varint.GetEncodedSize((ulong)ErrorCode);
}

public sealed class CryptoFrame : QuicFrame
{
    public ulong Offset { get; }
    public ReadOnlyMemory<byte> Data { get; }
    
    public CryptoFrame(ulong offset, ReadOnlyMemory<byte> data) : base(QuicFrameType.Crypto)
    {
        Offset = offset;
        Data = data;
    }
    
    public override int Encode(Span<byte> destination)
    {
        int offset = 0;
        destination[offset++] = (byte)QuicFrameType.Crypto;
        offset += Varint.Encode((ulong)Offset, destination[offset..]);
        offset += Varint.Encode((ulong)Data.Length, destination[offset..]);
        Data.Span.CopyTo(destination[offset..]);
        return 1 + Varint.GetEncodedSize(Offset) + Varint.GetEncodedSize((ulong)Data.Length) + Data.Length;
    }
    
    public override int GetEncodedSize() => 1 + Varint.GetEncodedSize(Offset) + Varint.GetEncodedSize((ulong)Data.Length) + Data.Length;
}

public sealed class NewTokenFrame : QuicFrame
{
    public ReadOnlyMemory<byte> Token { get; }
    
    public NewTokenFrame(ReadOnlyMemory<byte> token) : base(QuicFrameType.NewToken)
    {
        Token = token;
    }
    
    public override int Encode(Span<byte> destination)
    {
        int offset = 0;
        destination[offset++] = (byte)QuicFrameType.NewToken;
        offset += Varint.Encode((ulong)Token.Length, destination[offset..]);
        Token.Span.CopyTo(destination[offset..]);
        return 1 + Varint.GetEncodedSize((ulong)Token.Length) + Token.Length;
    }
    
    public override int GetEncodedSize() => 1 + Varint.GetEncodedSize((ulong)Token.Length) + Token.Length;
}

public sealed class StreamFrame : QuicFrame
{
    public ulong StreamId { get; }
    public ulong Offset { get; }
    public ReadOnlyMemory<byte> Data { get; }
    public bool Fin { get; }
    
    public StreamFrame(ulong streamId, ulong offset, ReadOnlyMemory<byte> data, bool fin) : base(QuicFrameType.Stream)
    {
        StreamId = streamId;
        Offset = offset;
        Data = data;
        Fin = fin;
    }
    
    public override int Encode(Span<byte> destination)
    {
        int offset = 0;
        
        // Type byte with flags
        var typeByte = (byte)QuicFrameType.Stream;
        if (Fin) typeByte |= 0x01;
        if (Offset != 0) typeByte |= 0x04;
        if (Data.Length > 0) typeByte |= 0x02;
        destination[offset++] = typeByte;
        
        // Stream ID (varint)
        offset += Varint.Encode(StreamId, destination[offset..]);
        
        // Offset (varint) if present
        if (Offset != 0)
        {
            offset += Varint.Encode(Offset, destination[offset..]);
        }
        
        // Length (varint) if present
        if (Data.Length > 0)
        {
            offset += Varint.Encode((ulong)Data.Length, destination[offset..]);
        }
        
        // Data
        Data.Span.CopyTo(destination[offset..]);
        offset += Data.Length;
        
        return offset;
    }
    
    public override int GetEncodedSize()
    {
        int size = 1; // type byte
        size += Varint.GetEncodedSize(StreamId);
        if (Offset != 0) size += Varint.GetEncodedSize(Offset);
        if (Data.Length > 0) size += Varint.GetEncodedSize((ulong)Data.Length);
        size += Data.Length;
        return size;
    }
}

public sealed class MaxDataFrame : QuicFrame
{
    public ulong MaxData { get; }
    public MaxDataFrame(ulong maxData) : base(QuicFrameType.MaxData) { MaxData = maxData; }
    public override int Encode(Span<byte> destination)
    {
        destination[0] = (byte)QuicFrameType.MaxData;
        return 1 + Varint.Encode(MaxData, destination[1..]);
    }
    public override int GetEncodedSize() => 1 + Varint.GetEncodedSize(MaxData);
}

public sealed class MaxStreamDataFrame : QuicFrame
{
    public ulong StreamId { get; }
    public ulong MaxStreamData { get; }
    
    public MaxStreamDataFrame(ulong streamId, ulong maxStreamData) : base(QuicFrameType.MaxStreamData) 
    { StreamId = streamId; MaxStreamData = maxStreamData; }
    
    public override int Encode(Span<byte> destination)
    {
        int offset = 0;
        destination[offset++] = (byte)QuicFrameType.MaxStreamData;
        offset += Varint.Encode(StreamId, destination[offset..]);
        offset += Varint.Encode(MaxStreamData, destination[offset..]);
        return offset;
    }
    
    public override int GetEncodedSize() => 1 + Varint.GetEncodedSize(StreamId) + Varint.GetEncodedSize(MaxStreamData);
}

public sealed class MaxStreamsFrame : QuicFrame
{
    public ulong MaxStreams { get; }
    public bool Bidi { get; }
    public MaxStreamsFrame(ulong maxStreams, bool bidi) : base(bidi ? QuicFrameType.MaxStreamsBidi : QuicFrameType.MaxStreamsUni)
    { MaxStreams = maxStreams; Bidi = bidi; }
    
    public override int Encode(Span<byte> destination)
    {
        int offset = 0;
        destination[offset++] = (byte)(Bidi ? QuicFrameType.MaxStreamsBidi : QuicFrameType.MaxStreamsUni);
        offset += Varint.Encode(MaxStreams, destination[offset..]);
        return offset;
    }
    
    public override int GetEncodedSize() => 1 + Varint.GetEncodedSize(MaxStreams);
}

public sealed class DataBlockedFrame : QuicFrame
{
    public ulong Limit { get; }
    public DataBlockedFrame(ulong limit) : base(QuicFrameType.DataBlocked) { Limit = limit; }
    public override int Encode(Span<byte> destination) { return 0; }
    public override int GetEncodedSize() => 0;
}

public sealed class StreamDataBlockedFrame : QuicFrame
{
    public ulong StreamId { get; }
    public ulong Limit { get; }
    public StreamDataBlockedFrame(ulong streamId, ulong limit) : base(QuicFrameType.StreamDataBlocked) { StreamId = streamId; Limit = limit; }
    public override int Encode(Span<byte> destination) { return 0; }
    public override int GetEncodedSize() => 0;
}

public sealed class StreamsBlockedFrame : QuicFrame
{
    public ulong Limit { get; }
    public bool Bidi { get; }
    public StreamsBlockedFrame(ulong limit, bool bidi) : base(QuicFrameType.StreamsBlockedBidi) { Limit = limit; Bidi = bidi; }
    public override int Encode(Span<byte> destination) { return 0; }
    public override int GetEncodedSize() => 0;
}

public sealed class NewConnectionIdFrame : QuicFrame
{
    public ulong Sequence { get; }
    public ulong RetirePriorTo { get; }
    public ReadOnlyMemory<byte> ConnectionId { get; }
    public ReadOnlyMemory<byte> ResetToken { get; }
    
    public NewConnectionIdFrame(ulong seq, ulong retirePriorTo, ReadOnlyMemory<byte> connectionId, ReadOnlyMemory<byte> resetToken) 
        : base(QuicFrameType.NewConnectionId)
    {
        Sequence = seq;
        RetirePriorTo = retirePriorTo;
        ConnectionId = connectionId;
        ResetToken = resetToken;
    }
    public override int Encode(Span<byte> destination) { return 0; }
    public override int GetEncodedSize() => 0;
}

public sealed class RetireConnectionIdFrame : QuicFrame
{
    public ulong Sequence { get; }
    public RetireConnectionIdFrame(ulong seq) : base(QuicFrameType.RetireConnectionId) { Sequence = seq; }
    public override int Encode(Span<byte> destination) { return 0; }
    public override int GetEncodedSize() => 0;
}

public sealed class PathChallengeFrame : QuicFrame
{
    public ReadOnlyMemory<byte> Data { get; }
    public PathChallengeFrame(ReadOnlyMemory<byte> data) : base(QuicFrameType.PathChallenge) { Data = data; }
    public override int Encode(Span<byte> destination) { return 0; }
    public override int GetEncodedSize() => 0;
}

public sealed class PathResponseFrame : QuicFrame
{
    public ReadOnlyMemory<byte> Data { get; }
    public PathResponseFrame(ReadOnlyMemory<byte> data) : base(QuicFrameType.PathResponse) { Data = data; }
    public override int Encode(Span<byte> destination) { return 0; }
    public override int GetEncodedSize() => 0;
}

public sealed class ConnectionCloseFrame : QuicFrame
{
    public ulong ErrorCode { get; }
    public ulong FrameType { get; }
    public string Reason { get; }
    
    public ConnectionCloseFrame(ulong errorCode, ulong frameType, string reason) : base(QuicFrameType.ConnectionClose)
    { ErrorCode = errorCode; FrameType = frameType; Reason = reason; }
    
    public override int Encode(Span<byte> destination)
    {
        int offset = 0;
        destination[offset++] = (byte)QuicFrameType.ConnectionClose;
        offset += Varint.Encode(ErrorCode, destination[offset..]);
        offset += Varint.Encode(FrameType, destination[offset..]);
        
        var reasonBytes = System.Text.Encoding.UTF8.GetBytes(Reason);
        offset += Varint.Encode((ulong)reasonBytes.Length, destination[offset..]);
        reasonBytes.CopyTo(destination[offset..]);
        offset += reasonBytes.Length;
        
        return offset;
    }
    
    public override int GetEncodedSize()
    {
        var reasonBytes = System.Text.Encoding.UTF8.GetBytes(Reason);
        return 1 + Varint.GetEncodedSize(ErrorCode) + Varint.GetEncodedSize(FrameType) + Varint.GetEncodedSize((ulong)reasonBytes.Length) + reasonBytes.Length;
    }
}

public sealed class HandshakeDoneFrame : QuicFrame
{
    public HandshakeDoneFrame() : base(QuicFrameType.HandshakeDone) { }
    public override int Encode(Span<byte> destination) { destination[0] = (byte)QuicFrameType.HandshakeDone; return 1; }
    public override int GetEncodedSize() => 1;
}

// Varint encoding/decoding (matching tquic's varint implementation)
public static class Varint
{
    public static ulong Decode(ReadOnlySpan<byte> buffer, out int bytesConsumed)
    {
        if (buffer.Length == 0) throw new ArgumentException("Empty buffer");
        
        var first = buffer[0];
        var prefix = first >> 6;
        
        switch (prefix)
        {
            case 0: // 1 byte
                bytesConsumed = 1;
                return (ulong)(first & 0x3F);
            case 1: // 2 bytes
                if (buffer.Length < 2) throw new ArgumentException("Buffer too short for 2-byte varint");
                bytesConsumed = 2;
                return ((ulong)(buffer[0] & 0x3F) << 8) | buffer[1];
            case 2: // 4 bytes
                if (buffer.Length < 4) throw new ArgumentException("Buffer too short for 4-byte varint");
                bytesConsumed = 4;
                return ((ulong)(buffer[0] & 0x3F) << 24) | ((ulong)buffer[1] << 16) | ((ulong)buffer[2] << 8) | buffer[3];
            case 3: // 8 bytes
                if (buffer.Length < 8) throw new ArgumentException("Buffer too short for 8-byte varint");
                bytesConsumed = 8;
                return ((ulong)(buffer[0] & 0x3F) << 56) | ((ulong)buffer[1] << 48) | ((ulong)buffer[2] << 40) | ((ulong)buffer[3] << 32) | ((ulong)buffer[4] << 24) | ((ulong)buffer[5] << 16) | ((ulong)buffer[6] << 8) | buffer[7];
            default:
                throw new InvalidOperationException("Invalid varint prefix");
        }
    }
    
    public static int Encode(ulong value, Span<byte> destination)
    {
        if (value <= 0x3F)
        {
            destination[0] = (byte)value;
            return 1;
        }
        else if (value <= 0x3FFF)
        {
            destination[0] = (byte)(0x40 | (value >> 8));
            destination[1] = (byte)value;
            return 2;
        }
        else if (value <= 0x3FFFFFFF)
        {
            destination[0] = (byte)(0x80 | (value >> 24));
            destination[1] = (byte)(value >> 16);
            destination[2] = (byte)(value >> 8);
            destination[3] = (byte)value;
            return 4;
        }
        else
        {
            destination[0] = (byte)(0xC0 | (value >> 56));
            destination[1] = (byte)(value >> 48);
            destination[2] = (byte)(value >> 40);
            destination[3] = (byte)(value >> 32);
            destination[4] = (byte)(value >> 24);
            destination[5] = (byte)(value >> 16);
            destination[6] = (byte)(value >> 8);
            destination[7] = (byte)value;
            return 8;
        }
    }
    
    public static int GetEncodedSize(ulong value)
    {
        if (value <= 0x3F) return 1;
        if (value <= 0x3FFF) return 2;
        if (value <= 0x3FFFFFFF) return 4;
        return 8;
    }
}
