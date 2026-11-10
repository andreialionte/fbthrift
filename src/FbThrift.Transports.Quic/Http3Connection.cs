// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Quic;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

/// <summary>
/// HTTP/3 implementation for QUIC.
/// Based on RFC 9114 and QPACK (RFC 9204).
/// </summary>
public sealed class Http3Connection : IDisposable
{
    // HTTP/3 frame types (RFC 9114)
    public const byte FrameData = 0x00;
    public const byte FrameHeaders = 0x01;
    public const byte FrameCancelPush = 0x03;
    public const byte FrameSettings = 0x04;
    public const byte FramePushPromise = 0x05;
    public const byte FrameGoAway = 0x07;
    public const byte FrameMaxPushId = 0x0D;
    public const byte FrameDatagram = 0x30; // Draft

    // HTTP/3 settings
    private const int MaxFieldSectionSize = 16384;
    private const int MaxHeaderListSize = 65536;
    private const int InitialMaxFieldSectionSize = 16384;
    private const int InitialMaxHeaderListSize = 65536;

    // QPACK constants (RFC 9204)
    private const int MaxDynamicTableSize = 4096;
    private const int MaxDynamicTableEntries = 100;
    private const int DefaultMaxTableSize = 4096;

    // Shared buffer pool for frame encoding
    private static readonly ArrayPool<byte> _framePool = ArrayPool<byte>.Shared;

    // Static header table (RFC 9204 Appendix A)
    private static readonly (string Name, string Value)[] StaticTable = new[]
    {
        (":authority", ""),
        (":path", "/"),
        ("age", "0"),
        ("content-disposition", ""),
        ("content-length", "0"),
        ("cookie", ""),
        ("date", ""),
        ("etag", ""),
        ("if-modified-since", ""),
        ("if-none-match", ""),
        ("last-modified", ""),
        ("link", ""),
        ("location", ""),
        ("referer", ""),
        ("set-cookie", ""),
        (":method", "CONNECT"),
        (":method", "DELETE"),
        (":method", "GET"),
        (":method", "HEAD"),
        (":method", "OPTIONS"),
        (":method", "POST"),
        (":method", "PUT"),
        (":path", "/index.html"),
        (":scheme", "http"),
        (":scheme", "https"),
        (":status", "200"),
        (":status", "204"),
        (":status", "206"),
        (":status", "304"),
        (":status", "400"),
        (":status", "404"),
        (":status", "500"),
        ("accept", "*/*"),
        ("accept", "application/dns-message"),
        ("accept-encoding", "gzip, deflate, br"),
        ("accept-ranges", "bytes"),
        ("access-control-allow-headers", "cache-control"),
        ("access-control-allow-methods", "GET"),
        ("access-control-allow-origin", "*"),
        ("cache-control", "max-age=0"),
        ("cache-control", "max-age=2592000"),
        ("cache-control", "max-age=604800"),
        ("cache-control", "no-cache"),
        ("cache-control", "no-store"),
        ("cache-control", "public, max-age=31536000"),
        ("content-encoding", "br"),
        ("content-encoding", "gzip"),
        ("content-type", "application/dns-message"),
        ("content-type", "application/json"),
        ("content-type", "application/x-www-form-urlencoded"),
        ("content-type", "image/gif"),
        ("content-type", "image/jpeg"),
        ("content-type", "image/png"),
        ("content-type", "text/css"),
        ("content-type", "text/html; charset=utf-8"),
        ("content-type", "text/plain; charset=utf-8"),
        ("range", "bytes=0-"),
        ("strict-transport-security", "max-age=31536000"),
        ("strict-transport-security", "max-age=31536000; includesubdomains"),
        ("vary", "accept-encoding"),
        ("vary", "origin"),
        ("x-content-type-options", "nosniff"),
        ("x-xss-protection", "1; mode=block"),
        (":status", "100"),
        (":status", "201"),
        (":status", "301"),
        (":status", "302"),
        (":status", "403"),
        (":status", "421"),
        (":status", "425"),
        (":status", "502"),
        (":status", "503"),
        (":status", "504"),
    };

    // State
    private readonly bool _isServer;
    private readonly QpackEncoder _qpackEncoder;
    private readonly QpackDecoder _qpackDecoder;
    private readonly Dictionary<long, Http3Stream> _streams = new();
    private readonly Dictionary<long, Http3Stream> _remoteStreams = new();
    private long _nextLocalStreamId;
    private long _nextRemoteStreamId;
    private long _maxRemoteStreams;
    private long _maxFieldSectionSize;
    private long _maxHeaderListSize;
    private bool _settingsReceived;
    private bool _closed;
    private long _bytesSent;
    private long _bytesReceived;

    /// <summary>
    /// Initializes a new instance of the <see cref="Http3Connection"/> class.
    /// </summary>
    public Http3Connection(bool isServer)
    {
        _isServer = isServer;
        _qpackEncoder = new QpackEncoder(MaxDynamicTableSize);
        _qpackDecoder = new QpackDecoder(MaxDynamicTableSize);
        _nextLocalStreamId = isServer ? 0 : 0; // Client: 0, 4, 8... Server: 1, 5, 9...
        _nextRemoteStreamId = isServer ? 1 : 0; // Server-initiated: 1, 5, 9... Client-initiated: 0, 4, 8...
        _maxRemoteStreams = 100;
        _maxFieldSectionSize = InitialMaxFieldSectionSize;
        _maxHeaderListSize = InitialMaxHeaderListSize;
    }

    /// <summary>
    /// Gets the next available local stream ID.
    /// </summary>
    public long GetNextLocalStreamId()
    {
        var id = _nextLocalStreamId;
        _nextLocalStreamId += 4;
        return id;
    }

    /// <summary>
    /// Creates a new outgoing HTTP/3 stream.
    /// </summary>
    public Http3Stream CreateStream()
    {
        var streamId = GetNextLocalStreamId();
        var stream = new Http3Stream
        {
            StreamId = streamId,
            State = Http3StreamState.Idle,
            SendState = Http3StreamSendState.Ready,
            RecvState = Http3StreamRecvState.Ready
        };
        _streams[streamId] = stream;
        return stream;
    }

    /// <summary>
    /// Handles an incoming stream from the remote peer.
    /// </summary>
    public Http3Stream HandleRemoteStream(long streamId)
    {
        if (_remoteStreams.ContainsKey(streamId))
        {
            return _remoteStreams[streamId];
        }

        var stream = new Http3Stream
        {
            StreamId = streamId,
            State = Http3StreamState.Idle,
            SendState = Http3StreamSendState.Ready,
            RecvState = Http3StreamRecvState.Ready
        };
        _remoteStreams[streamId] = stream;
        return stream;
    }

    /// <summary>
    /// Encodes and sends an HTTP/3 HEADERS frame.
    /// </summary>
    public ReadOnlyMemory<byte> EncodeHeadersFrame(long streamId, Http3Headers headers, bool endStream)
    {
        var buffer = _framePool.Rent(4096);
        try
        {
            var writer = new ArrayBufferWriter<byte>(buffer);
            
            // Encode headers using QPACK
            var encodedHeaders = _qpackEncoder.Encode(headers);
            
            // Frame header
            writer.Write(new byte[] { FrameHeaders });
            
            // Length (varint)
            WriteVarint(writer, (ulong)encodedHeaders.Length);
            
            // Stream ID (varint)
            WriteVarint(writer, (ulong)streamId);
            
            // Flags
            var flags = (byte)0;
            if (endStream) flags |= 0x01; // END_STREAM
            writer.Write(new byte[] { flags });
            
            // Header block
            writer.Write(encodedHeaders);
            
            return writer.WrittenMemory;
        }
        finally
        {
            _framePool.Return(buffer);
        }
    }

    /// <summary>
    /// Encodes and sends an HTTP/3 DATA frame.
    /// </summary>
    public ReadOnlyMemory<byte> EncodeDataFrame(long streamId, ReadOnlyMemory<byte> data, bool endStream)
    {
        var buffer = _framePool.Rent(data.Length + 64); // Extra space for frame header
        try
        {
            var writer = new ArrayBufferWriter<byte>(buffer);
            
            writer.Write(new byte[] { FrameData });
            WriteVarint(writer, (ulong)data.Length);
            WriteVarint(writer, (ulong)streamId);
            
            var flags = (byte)0;
            if (endStream) flags |= 0x01; // END_STREAM
            writer.Write(new byte[] { flags });
            
            writer.Write(data.Span);
            
            return writer.WrittenMemory;
        }
        finally
        {
            _framePool.Return(buffer);
        }
    }

    /// <summary>
    /// Encodes and sends an HTTP/3 SETTINGS frame.
    /// </summary>
    public ReadOnlyMemory<byte> EncodeSettingsFrame()
    {
        var buffer = _framePool.Rent(256);
        try
        {
            var writer = new ArrayBufferWriter<byte>(buffer);
            
            writer.Write(new byte[] { FrameSettings });
            
            // Settings payload
            var settingsWriter = new ArrayBufferWriter<byte>(buffer); // Reuse buffer space
            WriteSetting(settingsWriter, 0x01, (ulong)_maxFieldSectionSize); // MAX_FIELD_SECTION_SIZE
            WriteSetting(settingsWriter, 0x06, (ulong)_maxHeaderListSize);   // MAX_HEADER_LIST_SIZE
            
            var settingsData = settingsWriter.WrittenMemory;
            WriteVarint(writer, (ulong)settingsData.Length);
            WriteVarint(writer, 0); // Stream ID (0 for connection-level)
            writer.Write(new byte[] { 0 }); // No flags
            writer.Write(settingsData.Span);
            
            return writer.WrittenMemory;
        }
        finally
        {
            _framePool.Return(buffer);
        }
    }

    /// <summary>
    /// Decodes an HTTP/3 frame from the given data.
    /// </summary>
    public (Http3Frame Frame, int BytesConsumed) DecodeFrame(ReadOnlyMemory<byte> data)
    {
        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(data));
        
        if (!reader.TryRead(out var frameType))
        {
            return (null, 0);
        }
        
        if (!ReadVarint(ref reader, out var length))
        {
            return (null, 0);
        }
        
        if (!ReadVarint(ref reader, out var streamId))
        {
            return (null, 0);
        }
        
        if (!reader.TryRead(out var flags))
        {
            return (null, 0);
        }
        
        var payloadLength = (int)length;
        if (reader.Remaining < payloadLength)
        {
            return (null, 0);
        }
        
        var payload = reader.Sequence.Slice(reader.Position, payloadLength);
        reader.Advance(payloadLength);
        
        var frame = new Http3Frame
        {
            Type = frameType,
            StreamId = (long)streamId,
            Flags = flags,
            Payload = payload // Use ReadOnlySequence directly, avoid ToArray()
        };
        
        // Parse frame-specific data
        switch (frameType)
        {
            case FrameHeaders:
                frame.Headers = _qpackDecoder.Decode(payload);
                break;
            case FrameData:
                frame.Data = payload;
                frame.EndStream = (flags & 0x01) != 0;
                break;
            case FrameSettings:
                frame.Settings = ParseSettings(payload);
                break;
        }
        
        return (frame, (int)reader.Consumed);
    }

    /// <summary>
    /// Updates settings based on received SETTINGS frame.
    /// </summary>
    public void ApplySettings(Dictionary<ulong, ulong> settings)
    {
        _settingsReceived = true;
        
        if (settings.TryGetValue(0x01, out var maxFieldSectionSize))
        {
            _maxFieldSectionSize = (long)Math.Min(maxFieldSectionSize, (ulong)MaxFieldSectionSize);
        }
        
        if (settings.TryGetValue(0x06, out var maxHeaderListSize))
        {
            _maxHeaderListSize = (long)Math.Min(maxHeaderListSize, (ulong)MaxHeaderListSize);
        }
        
        // Update QPACK decoder max table size
        if (settings.TryGetValue(0x01, out var tableSize))
        {
            _qpackDecoder.SetMaxTableSize((long)Math.Min(tableSize, (ulong)MaxDynamicTableSize));
        }
    }

    /// <summary>
    /// Sends a GOAWAY frame.
    /// </summary>
    public byte[] EncodeGoAwayFrame(long streamId, long errorCode)
    {
        var writer = new ArrayBufferWriter<byte>();
        
        writer.Write(new byte[] { FrameGoAway });
        
        var payloadWriter = new ArrayBufferWriter<byte>();
        WriteVarint(payloadWriter, (ulong)streamId);
        
        var payload = payloadWriter.WrittenMemory.ToArray();
        WriteVarint(writer, (ulong)payload.Length);
        WriteVarint(writer, 0); // Stream ID 0 for connection-level
        writer.Write(new byte[] { 0 });
        writer.Write(payload);
        
        return writer.WrittenMemory.ToArray();
    }

    /// <summary>
    /// Gets a stream by ID.
    /// </summary>
    public bool TryGetStream(long streamId, out Http3Stream stream)
    {
        if (_streams.TryGetValue(streamId, out stream))
        {
            return true;
        }
        return _remoteStreams.TryGetValue(streamId, out stream);
    }

    /// <summary>
    /// Closes the connection.
    /// </summary>
    public void Close()
    {
        _closed = true;
    }

    public void Dispose()
    {
        Close();
    }

    private static void WriteVarint(ArrayBufferWriter<byte> writer, ulong value)
    {
        if (value < 0x40)
        {
            writer.Write(new byte[] { (byte)value });
        }
        else if (value < 0x4000)
        {
            writer.Write(new byte[] { (byte)(0x40 | (value >> 8)), (byte)value });
        }
        else if (value < 0x40000000)
        {
            writer.Write(new byte[] { (byte)(0x80 | (value >> 24)), (byte)(value >> 16), (byte)(value >> 8), (byte)value });
        }
        else
        {
            writer.Write(new byte[] { 
                (byte)(0xC0 | (value >> 56)), 
                (byte)(value >> 48), 
                (byte)(value >> 40), 
                (byte)(value >> 32), 
                (byte)(value >> 24), 
                (byte)(value >> 16), 
                (byte)(value >> 8), 
                (byte)value 
            });
        }
    }

    private static bool ReadVarint(ref SequenceReader<byte> reader, out ulong value)
    {
        value = 0;
        
        if (!reader.TryRead(out var firstByte))
        {
            return false;
        }
        
        var prefix = firstByte >> 6;
        var length = 1 << (int)prefix;
        
        value = (ulong)(firstByte & 0x3F);
        
        for (int i = 1; i < length; i++)
        {
            if (!reader.TryRead(out var b))
            {
                return false;
            }
            value = (value << 8) | b;
        }
        
        return true;
    }

    private static bool ReadString(ref SequenceReader<byte> reader, out string value)
    {
        value = null;
        
        if (!ReadVarint(ref reader, out var len))
        {
            return false;
        }
        
        if ((ulong)reader.Remaining < len)
        {
            return false;
        }
        
        var bytes = reader.Sequence.Slice(reader.Position, (int)len).ToArray();
        reader.Advance((long)len);
        value = Encoding.UTF8.GetString(bytes);
        return true;
    }

    private static void WriteSetting(ArrayBufferWriter<byte> writer, ulong id, ulong value)
    {
        WriteVarint(writer, id);
        WriteVarint(writer, value);
    }

    private static Dictionary<ulong, ulong> ParseSettings(ReadOnlySequence<byte> data)
    {
        var settings = new Dictionary<ulong, ulong>();
        var reader = new SequenceReader<byte>(data);
        
        while (reader.Remaining > 0)
        {
            if (ReadVarint(ref reader, out var id) && ReadVarint(ref reader, out var value))
            {
                settings[id] = value;
            }
            else
            {
                break;
            }
        }
        
        return settings;
    }

    /// <summary>
    /// HTTP/3 frame representation.
    /// </summary>
    public sealed class Http3Frame
    {
        public byte Type { get; set; }
        public long StreamId { get; set; }
        public byte Flags { get; set; }
        public byte[] Payload { get; set; }
        public Http3Headers Headers { get; set; }
        public byte[] Data { get; set; }
        public bool EndStream { get; set; }
        public Dictionary<ulong, ulong> Settings { get; set; }
    }

    /// <summary>
    /// HTTP/3 headers collection.
    /// </summary>
    public sealed class Http3Headers
    {
        private readonly Dictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase);

        public string this[string name]
        {
            get => _headers.TryGetValue(name, out var value) ? value : null;
            set => _headers[name] = value;
        }

        public void Add(string name, string value)
        {
            _headers[name] = value;
        }

        public bool TryGetValue(string name, out string value)
        {
            return _headers.TryGetValue(name, out value);
        }

        public IEnumerable<KeyValuePair<string, string>> GetHeaders()
        {
            return _headers;
        }

        public int Count => _headers.Count;
    }

    /// <summary>
    /// HTTP/3 stream state.
    /// </summary>
    public sealed class Http3Stream
    {
        public long StreamId { get; set; }
        public Http3StreamState State { get; set; }
        public Http3StreamSendState SendState { get; set; }
        public Http3StreamRecvState RecvState { get; set; }
        public Http3Headers RequestHeaders { get; set; }
        public Http3Headers ResponseHeaders { get; set; }
        public long BytesSent { get; set; }
        public long BytesReceived { get; set; }
    }

    public enum Http3StreamState
    {
        Idle,
        Open,
        HalfClosedLocal,
        HalfClosedRemote,
        Closed
    }

    public enum Http3StreamSendState
    {
        Ready,
        SendHeaders,
        SendBody,
        DataSent,
        ResetSent
    }

    public enum Http3StreamRecvState
    {
        Ready,
        RecvHeaders,
        RecvBody,
        DataRecvd,
        ResetRecvd
    }

    /// <summary>
    /// QPACK Header Encoder (RFC 9204).
    /// </summary>
    public sealed class QpackEncoder
    {
        private readonly List<(string Name, string Value)> _dynamicTable = new();
        private long _maxTableSize;
        private long _currentTableSize;

        public QpackEncoder(long maxTableSize = MaxDynamicTableSize)
        {
            _maxTableSize = maxTableSize;
        }

        public byte[] Encode(Http3Headers headers)
        {
            var writer = new ArrayBufferWriter<byte>();
            
            // Required Insert Count (varint) - always 0 for simplicity
            WriteVarint(writer, 0);
            
            // Base (varint) - 0 for static table
            WriteVarint(writer, 0);
            
            // Encode each header
            foreach (var kvp in headers.GetHeaders())
            {
                EncodeHeader(writer, kvp.Key, kvp.Value);
            }
            
            return writer.WrittenMemory.ToArray();
        }

        private void EncodeHeader(ArrayBufferWriter<byte> writer, string name, string value)
        {
            // Check static table
            var staticIndex = FindInStaticTable(name, value);
            if (staticIndex >= 0)
            {
                // Indexed header field (static)
                writer.Write(new byte[] { (byte)(0x80 | (byte)staticIndex) });
                return;
            }
            
            // Check dynamic table
            var dynamicIndex = FindInDynamicTable(name, value);
            if (dynamicIndex >= 0)
            {
                // Indexed header field (dynamic)
                var index = StaticTable.Length + dynamicIndex;
                writer.Write(new byte[] { (byte)(0x80 | (byte)index) });
                return;
            }
            
            // Literal header field with name reference
            var nameIndex = FindNameInStaticTable(name);
            if (nameIndex >= 0)
            {
                // Name from static table
                writer.Write(new byte[] { (byte)(0x40 | (byte)nameIndex) });
                WriteString(writer, value);
                return;
            }
            
            nameIndex = FindNameInDynamicTable(name);
            if (nameIndex >= 0)
            {
                // Name from dynamic table
                var index = StaticTable.Length + nameIndex;
                writer.Write(new byte[] { (byte)(0x40 | (byte)index) });
                WriteString(writer, value);
                return;
            }
            
            // Literal header field with literal name
            writer.Write(new byte[] { 0x00 }); // Literal name
            WriteString(writer, name);
            WriteString(writer, value);
            
            // Add to dynamic table
            AddToDynamicTable(name, value);
        }

        private int FindInStaticTable(string name, string value)
        {
            for (int i = 0; i < StaticTable.Length; i++)
            {
                if (StaticTable[i].Name == name && StaticTable[i].Value == value)
                {
                    return i;
                }
            }
            return -1;
        }

        private int FindInDynamicTable(string name, string value)
        {
            for (int i = 0; i < _dynamicTable.Count; i++)
            {
                if (_dynamicTable[i].Name == name && _dynamicTable[i].Value == value)
                {
                    return i;
                }
            }
            return -1;
        }

        private int FindNameInStaticTable(string name)
        {
            for (int i = 0; i < StaticTable.Length; i++)
            {
                if (StaticTable[i].Name == name)
                {
                    return i;
                }
            }
            return -1;
        }

        private int FindNameInDynamicTable(string name)
        {
            for (int i = 0; i < _dynamicTable.Count; i++)
            {
                if (_dynamicTable[i].Name == name)
                {
                    return i;
                }
            }
            return -1;
        }

        private void AddToDynamicTable(string name, string value)
        {
            var entrySize = 32 + name.Length + value.Length; // RFC 9204: 32 bytes overhead
            
            // Evict old entries if needed
            while (_currentTableSize + entrySize > _maxTableSize && _dynamicTable.Count > 0)
            {
                var removed = _dynamicTable[0];
                _currentTableSize -= 32 + removed.Name.Length + removed.Value.Length;
                _dynamicTable.RemoveAt(0);
            }
            
            if (entrySize <= _maxTableSize)
            {
                _dynamicTable.Add((name, value));
                _currentTableSize += entrySize;
            }
        }

        private void WriteString(ArrayBufferWriter<byte> writer, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            WriteVarint(writer, (ulong)bytes.Length);
            writer.Write(bytes);
        }

        public void SetMaxTableSize(long size)
        {
            _maxTableSize = Math.Min(size, MaxDynamicTableSize);
        }
    }

    /// <summary>
    /// QPACK Header Decoder (RFC 9204).
    /// </summary>
    public sealed class QpackDecoder
    {
        private readonly List<(string Name, string Value)> _dynamicTable = new();
        private long _maxTableSize;
        private long _currentTableSize;

        public QpackDecoder(long maxTableSize = MaxDynamicTableSize)
        {
            _maxTableSize = maxTableSize;
        }

        public Http3Headers Decode(ReadOnlyMemory<byte> data)
        {
            var headers = new Http3Headers();
            var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(data));
            
            // Read Required Insert Count
            if (!ReadVarint(ref reader, out var requiredInsertCount))
            {
                return headers;
            }
            
            // Read Base
            if (!ReadVarint(ref reader, out var baseIndex))
            {
                return headers;
            }
            
            // Decode each header
            while (reader.Remaining > 0)
            {
                if (!reader.TryRead(out var firstByte))
                {
                    break;
                }
                
                if ((firstByte & 0x80) != 0)
                {
                    // Indexed header field
                    var index = firstByte & 0x7F;
                    if (index < StaticTable.Length)
                    {
                        var entry = StaticTable[index];
                        headers.Add(entry.Name, entry.Value);
                    }
                    else
                    {
                        var dynamicIndex = index - StaticTable.Length;
                        if (dynamicIndex < _dynamicTable.Count)
                        {
                            var entry = _dynamicTable[(int)dynamicIndex];
                            headers.Add(entry.Name, entry.Value);
                        }
                    }
                }
                else if ((firstByte & 0x40) != 0)
                {
                    // Literal header field with name reference
                    var nameIndex = firstByte & 0x3F;
                    string name;
                    
                    if (nameIndex < StaticTable.Length)
                    {
                        name = StaticTable[nameIndex].Name;
                    }
                    else
                    {
                        var dynamicIndex = nameIndex - StaticTable.Length;
                        if (dynamicIndex < _dynamicTable.Count)
                        {
                            name = _dynamicTable[(int)dynamicIndex].Name;
                        }
                        else
                        {
                            break;
                        }
                    }
                    
                    if (ReadString(ref reader, out var value))
                    {
                        headers.Add(name, value);
                        AddToDynamicTable(name, value);
                    }
                }
                else
                {
                    // Literal header field with literal name
                    if (ReadString(ref reader, out var name) && ReadString(ref reader, out var value))
                    {
                        headers.Add(name, value);
                        AddToDynamicTable(name, value);
                    }
                }
            }
            
            return headers;
        }

        private void AddToDynamicTable(string name, string value)
        {
            var entrySize = 32 + name.Length + value.Length;
            
            while (_currentTableSize + entrySize > _maxTableSize && _dynamicTable.Count > 0)
            {
                var removed = _dynamicTable[0];
                _currentTableSize -= 32 + removed.Name.Length + removed.Value.Length;
                _dynamicTable.RemoveAt(0);
            }
            
            if (entrySize <= _maxTableSize)
            {
                _dynamicTable.Add((name, value));
                _currentTableSize += entrySize;
            }
        }

        public void SetMaxTableSize(long size)
        {
            _maxTableSize = Math.Min(size, MaxDynamicTableSize);
        }
    }
}
