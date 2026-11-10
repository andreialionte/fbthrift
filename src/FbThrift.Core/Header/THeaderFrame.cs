using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace FbThrift.Core.Header;

/// <summary>
/// Represents a THeader frame as defined by fbthrift.
/// THeader magic is 0x0FFF0000 in the high bits.
/// </summary>
public sealed class THeaderFrame
{
    /// <summary>
    /// THeader magic mask.
    /// </summary>
    public const uint HeaderMagic = 0x0FFF0000;

    /// <summary>
    /// THeader magic mask for verification.
    /// </summary>
    public const uint HeaderMask = 0xFFFF0000;

    /// <summary>
    /// Gets or sets the frame size.
    /// </summary>
    public uint FrameSize { get; set; }

    /// <summary>
    /// Gets or sets the flags.
    /// </summary>
    public ushort Flags { get; set; }

    /// <summary>
    /// Gets or sets the sequence id.
    /// </summary>
    public uint SeqId { get; set; }

    /// <summary>
    /// Gets or sets the protocol id.
    /// </summary>
    public ProtocolId ProtoId { get; set; }

    /// <summary>
    /// Gets or sets the transforms.
    /// </summary>
    public List<TransformId> Transforms { get; set; } = new();

    /// <summary>
    /// Gets or sets the headers.
    /// </summary>
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets the persistent headers.
    /// </summary>
    public Dictionary<string, string> PersistentHeaders { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets the payload.
    /// </summary>
    public ReadOnlyMemory<byte> Payload { get; set; }

    /// <summary>
    /// Encodes a THeader frame to a byte array.
    /// </summary>
    public static byte[] Encode(THeaderFrame frame)
    {
        var buffer = new byte[CalculateFrameSize(frame)];
        var writer = new SpanWriter(buffer);
        
        // Frame size (4 bytes) - will be filled in later
        writer.WriteUInt32BigEndian(frame.FrameSize);
        
        // Flags (2 bytes)
        writer.WriteUInt16BigEndian(frame.Flags);
        
        // Sequence ID (4 bytes)
        writer.WriteUInt32BigEndian(frame.SeqId);
        
        // Protocol ID (1 byte)
        writer.WriteByte((byte)frame.ProtoId);
        
        // Transforms count (1 byte)
        writer.WriteByte((byte)frame.Transforms.Count);
        foreach (var transform in frame.Transforms)
        {
            writer.WriteByte((byte)transform);
        }
        
        // Headers count (2 bytes)
        writer.WriteUInt16BigEndian((ushort)frame.Headers.Count);
        foreach (var (key, value) in frame.Headers)
        {
            var keyBytes = Encoding.UTF8.GetBytes(key);
            var valueBytes = Encoding.UTF8.GetBytes(value);
            
            writer.WriteUInt16BigEndian((ushort)keyBytes.Length);
            writer.Write(keyBytes);
            
            writer.WriteUInt16BigEndian((ushort)valueBytes.Length);
            writer.Write(valueBytes);
        }
        
        // Persistent headers count (2 bytes)
        writer.WriteUInt16BigEndian((ushort)frame.PersistentHeaders.Count);
        foreach (var (key, value) in frame.PersistentHeaders)
        {
            var keyBytes = Encoding.UTF8.GetBytes(key);
            var valueBytes = Encoding.UTF8.GetBytes(value);
            
            writer.WriteUInt16BigEndian((ushort)keyBytes.Length);
            writer.Write(keyBytes);
            
            writer.WriteUInt16BigEndian((ushort)valueBytes.Length);
            writer.Write(valueBytes);
        }
        
        // Payload
        writer.Write(frame.Payload.Span);
        
        // Fill in frame size (first 4 bytes)
        var totalSize = writer.Position;
        BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)totalSize);
        
        return buffer;
    }

    /// <summary>
    /// Decodes a THeader frame from a byte array.
    /// </summary>
    public static THeaderFrame Decode(ReadOnlyMemory<byte> data)
    {
        var reader = new SpanReader(data.Span);
        
        // Frame size (4 bytes)
        var frameSize = reader.ReadUInt32BigEndian();
        
        // Flags (2 bytes)
        var flags = reader.ReadUInt16BigEndian();
        
        // Sequence ID (4 bytes)
        var seqId = reader.ReadUInt32BigEndian();
        
        // Protocol ID (1 byte)
        var protoId = reader.ReadByte();
        
        // Transforms count (1 byte)
        var transformsCount = reader.ReadByte();
        
        var transforms = new List<TransformId>();
        for (int i = 0; i < transformsCount; i++)
        {
            var transform = reader.ReadByte();
            transforms.Add((TransformId)transform);
        }
        
        // Headers count (2 bytes)
        var headersCount = reader.ReadUInt16BigEndian();
        
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < headersCount; i++)
        {
            var keyLen = reader.ReadUInt16BigEndian();
            var keyBytes = reader.ReadBytes(keyLen);
            var key = Encoding.UTF8.GetString(keyBytes);
            
            var valueLen = reader.ReadUInt16BigEndian();
            var valueBytes = reader.ReadBytes(valueLen);
            var value = Encoding.UTF8.GetString(valueBytes);
            
            headers[key] = value;
        }
        
        // Persistent headers count (2 bytes)
        var persistentCount = reader.ReadUInt16BigEndian();
        
        var persistentHeaders = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < persistentCount; i++)
        {
            var keyLen = reader.ReadUInt16BigEndian();
            var keyBytes = reader.ReadBytes(keyLen);
            var key = Encoding.UTF8.GetString(keyBytes);
            
            var valueLen = reader.ReadUInt16BigEndian();
            var valueBytes = reader.ReadBytes(valueLen);
            var value = Encoding.UTF8.GetString(valueBytes);
            
            persistentHeaders[key] = value;
        }
        
        // Payload (remaining bytes)
        var payload = reader.ReadRemaining();
        
        return new THeaderFrame
        {
            FrameSize = 0, // Will be set by Encode
            Flags = 0,
            SeqId = 0,
            ProtoId = (ProtocolId)0,
            Transforms = new List<TransformId>(),
            Headers = headers,
            PersistentHeaders = persistentHeaders,
            Payload = payload
        };
    }

    private static int CalculateFrameSize(THeaderFrame frame)
    {
        int size = 4 + 2 + 4 + 1; // frameSize + flags + seqId + protoId
        size += 1; // transforms count
        size += frame.Transforms.Count; // transforms
        
        size += 2; // headers count
        foreach (var (key, value) in frame.Headers)
        {
            var keyBytes = Encoding.UTF8.GetBytes(key);
            var valueBytes = Encoding.UTF8.GetBytes(value);
            size += 2 + keyBytes.Length + 2 + valueBytes.Length;
        }
        
        size += 2; // persistent headers count
        foreach (var (key, value) in frame.PersistentHeaders)
        {
            var keyBytes = Encoding.UTF8.GetBytes(key);
            var valueBytes = Encoding.UTF8.GetBytes(value);
            size += 2 + keyBytes.Length + 2 + valueBytes.Length;
        }
        
        size += frame.Payload.Length;
        
        return size;
    }
}

/// <summary>
/// Helper for writing to a span.
/// </summary>
internal sealed class SpanWriter
{
    private readonly byte[] _buffer;
    public int Position { get; private set; }

    public SpanWriter(byte[] buffer)
    {
        _buffer = buffer;
    }

    public Span<byte> GetSpan(int length) => _buffer.AsSpan(Position, length);

    public void WriteUInt32BigEndian(uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(Position, 4), value);
        Position += 4;
    }

    public void WriteUInt16BigEndian(ushort value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(_buffer.AsSpan(Position, 2), value);
        Position += 2;
    }

    public void WriteByte(byte value)
    {
        _buffer[Position++] = value;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        data.CopyTo(_buffer.AsSpan(Position));
        Position += data.Length;
    }

    public void Write(byte[] data)
    {
        data.CopyTo(_buffer.AsSpan(Position));
        Position += data.Length;
    }
}

/// <summary>
/// Helper for reading from a span.
/// </summary>
internal ref struct SpanReader
{
    private readonly ReadOnlySpan<byte> _data;
    public int Position { get; private set; }

    public SpanReader(ReadOnlySpan<byte> data)
    {
        _data = data;
    }

    public uint ReadUInt32BigEndian()
    {
        var value = BinaryPrimitives.ReadUInt32BigEndian(_data.Slice(Position, 4));
        Position += 4;
        return value;
    }

    public ushort ReadUInt16BigEndian()
    {
        var value = BinaryPrimitives.ReadUInt16BigEndian(_data.Slice(Position, 2));
        Position += 2;
        return value;
    }

    public byte ReadByte()
    {
        return _data[Position++];
    }

    public byte[] ReadBytes(int length)
    {
        var result = _data.Slice(Position, length).ToArray();
        Position += length;
        return result;
    }

    public byte[] ReadRemaining()
    {
        var result = _data.Slice(Position).ToArray();
        Position = _data.Length;
        return result;
    }
}