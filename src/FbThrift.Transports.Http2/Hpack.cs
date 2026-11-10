// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Http2;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

/// <summary>
/// HPACK Header Compression for HTTP/2 (RFC 7541).
/// Used by fbthrift's HTTP/2 transport (THeader over h2).
/// Based on tquic's hpack implementation and quiche's hpack.
/// </summary>
public static class Hpack
{
    // Static header table (RFC 7541 Appendix A)
    private static readonly (string Name, string Value)[] StaticTable = new[]
    {
        (":authority", ""),
        (":method", "GET"),
        (":method", "POST"),
        (":path", "/"),
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
        ("accept-charset", ""),
        ("accept-encoding", "gzip, deflate"),
        ("accept-language", ""),
        ("accept-ranges", ""),
        ("accept", ""),
        ("access-control-allow-origin", ""),
        ("age", ""),
        ("allow", ""),
        ("authorization", ""),
        ("cache-control", ""),
        ("content-disposition", ""),
        ("content-encoding", ""),
        ("content-language", ""),
        ("content-length", ""),
        ("content-location", ""),
        ("content-range", ""),
        ("content-type", ""),
        ("cookie", ""),
        ("date", ""),
        ("etag", ""),
        ("expect", ""),
        ("expires", ""),
        ("from", ""),
        ("host", ""),
        ("if-match", ""),
        ("if-modified-since", ""),
        ("if-none-match", ""),
        ("if-range", ""),
        ("if-unmodified-since", ""),
        ("last-modified", ""),
        ("link", ""),
        ("location", ""),
        ("max-forwards", ""),
        ("proxy-authenticate", ""),
        ("proxy-authorization", ""),
        ("range", ""),
        ("referer", ""),
        ("refresh", ""),
        ("retry-after", ""),
        ("server", ""),
        ("set-cookie", ""),
        ("strict-transport-security", ""),
        ("transfer-encoding", ""),
        ("user-agent", ""),
        ("vary", ""),
        ("via", ""),
        ("www-authenticate", ""),
    };

    // Encoder
    public sealed class Encoder
    {
        private readonly List<HeaderEntry> _dynamicTable = new();
        private int _maxTableSize = 4096;
        private int _currentTableSize = 0;
        private readonly ArrayBufferWriter<byte> _output;

        public Encoder(ArrayBufferWriter<byte> output)
        {
            _output = output ?? throw new ArgumentNullException(nameof(output));
        }

        public int MaxTableSize 
        { 
            get => _maxTableSize;
            set => SetMaxTableSize(value);
        }

        public void SetMaxTableSize(int size)
        {
            _maxTableSize = Math.Max(0, Math.Min(size, 65535));
            EvictOldEntries();
        }

        public void EncodeHeaders(IEnumerable<KeyValuePair<string, string>> headers)
        {
            foreach (var (name, value) in headers)
            {
                EncodeHeader(name, value);
            }
        }

        public void EncodeHeader(string name, string value)
        {
            // Check static table
            var staticIndex = FindInStaticTable(name, value);
            if (staticIndex >= 0)
            {
                // Indexed header field (static table)
                WriteIndexed(staticIndex + 1); // 1-based
                return;
            }

            // Check dynamic table
            var dynamicIndex = FindInDynamicTable(name, value);
            if (dynamicIndex >= 0)
            {
                // Indexed header field (dynamic table)
                WriteIndexed(StaticTable.Length + dynamicIndex + 1);
                return;
            }

            // Check for name match in static table
            var nameStaticIndex = FindNameInStaticTable(name);
            if (nameStaticIndex >= 0)
            {
                // Literal header field with name reference (static)
                WriteLiteralWithNameRef(nameStaticIndex + 1, value);
                AddToDynamicTable(name, value);
                return;
            }

            // Check for name match in dynamic table
            var nameDynamicIndex = FindNameInDynamicTable(name);
            if (nameDynamicIndex >= 0)
            {
                // Literal header field with name reference (dynamic)
                WriteLiteralWithNameRef(StaticTable.Length + nameDynamicIndex + 1, value);
                AddToDynamicTable(name, value);
                return;
            }

            // Literal header field with literal name
            WriteLiteral(name, value);
            AddToDynamicTable(name, value);
        }

        private void WriteIndexed(int index)
        {
            // 0x80 | index (7-bit prefix)
            WriteVarint(index, 0x80);
        }

        private void WriteLiteralWithNameRef(int nameIndex, string value)
        {
            // 0x40 | nameIndex (6-bit prefix)
            WriteVarint(nameIndex, 0x40);
            WriteString(value);
        }

        private void WriteLiteral(string name, string value)
        {
            // 0x00 (literal name)
            _output.Write(new byte[] { 0x00 });
            WriteString(name);
            WriteString(value);
            AddToDynamicTable(name, value);
        }

        private void WriteString(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            // String length with 7-bit prefix (0x00 = no huffman)
            WriteVarint(bytes.Length, 0x00);
            _output.Write(bytes);
        }

        private void WriteVarint(int value, byte prefix)
        {
            if (value < (1 << (8 - GetPrefixBits(prefix))) - 1)
            {
                _output.Write(new byte[] { (byte)(prefix | value) });
            }
            else
            {
                _output.Write(new byte[] { (byte)(prefix | ((1 << (8 - GetPrefixBits(prefix))) - 1)) });
                value -= (1 << (8 - GetPrefixBits(prefix))) - 1;
                while (value >= 128)
                {
                    _output.Write(new byte[] { (byte)(value | 0x80) });
                    value >>= 7;
                }
                _output.Write(new byte[] { (byte)value });
            }
        }

        private int GetPrefixBits(byte prefix)
        {
            if ((prefix & 0x80) != 0) return 1; // 7-bit
            if ((prefix & 0x40) != 0) return 2; // 6-bit
            if ((prefix & 0x20) != 0) return 3; // 5-bit
            if ((prefix & 0x10) != 0) return 4; // 4-bit
            return 8; // 8-bit (no prefix)
        }

        private int FindInStaticTable(string name, string value)
        {
            for (int i = 0; i < StaticTable.Length; i++)
            {
                if (StaticTable[i].Name == name && StaticTable[i].Value == value)
                    return i;
            }
            return -1;
        }

        private int FindNameInStaticTable(string name)
        {
            for (int i = 0; i < StaticTable.Length; i++)
            {
                if (StaticTable[i].Name == name)
                    return i;
            }
            return -1;
        }

        private int FindInDynamicTable(string name, string value)
        {
            for (int i = 0; i < _dynamicTable.Count; i++)
            {
                if (_dynamicTable[i].Name == name && _dynamicTable[i].Value == value)
                    return i;
            }
            return -1;
        }

        private int FindNameInDynamicTable(string name)
        {
            for (int i = 0; i < _dynamicTable.Count; i++)
            {
                if (_dynamicTable[i].Name == name)
                    return i;
            }
            return -1;
        }

        private void AddToDynamicTable(string name, string value)
        {
            var entrySize = 32 + name.Length + value.Length; // RFC 7541: 32 bytes overhead
            
            // Evict old entries if needed
            while (_currentTableSize + entrySize > _maxTableSize && _dynamicTable.Count > 0)
            {
                var removed = _dynamicTable[0];
                _currentTableSize -= 32 + removed.Name.Length + removed.Value.Length;
                _dynamicTable.RemoveAt(0);
            }

            if (entrySize <= _maxTableSize)
            {
                _dynamicTable.Add(new HeaderEntry { Name = name, Value = value });
                _currentTableSize += entrySize;
            }
        }

        private void EvictOldEntries()
        {
            while (_currentTableSize > _maxTableSize && _dynamicTable.Count > 0)
            {
                var removed = _dynamicTable[0];
                _currentTableSize -= 32 + removed.Name.Length + removed.Value.Length;
                _dynamicTable.RemoveAt(0);
            }
        }

        private readonly struct HeaderEntry
        {
            public string Name { get; init; }
            public string Value { get; init; }
        }
    }

    // Decoder
    public sealed class Decoder
    {
        private readonly List<HeaderEntry> _dynamicTable = new();
        private int _maxTableSize = 4096;
        private int _currentTableSize = 0;

        public void SetMaxTableSize(int size)
        {
            _maxTableSize = Math.Max(0, Math.Min(size, 65535));
            EvictOldEntries();
        }

public IReadOnlyList<KeyValuePair<string, string>> DecodeHeaders(ReadOnlySpan<byte> data)
    {
        var result = new List<KeyValuePair<string, string>>();
        var reader = new SpanReader(data);
        
        while (reader.Remaining > 0)
        {
            result.Add(DecodeHeader(ref reader));
        }
        
        return result;
    }

        private KeyValuePair<string, string> DecodeHeader(ref SpanReader reader)
        {
            if (reader.Remaining == 0)
                throw new InvalidDataException("Unexpected end of header block");

            var firstByte = reader.ReadByte();

            if ((firstByte & 0x80) != 0)
            {
                // Indexed header field
                var index = ReadVarint(firstByte, 0x80, ref reader);
                return GetHeaderByIndex(index);
            }
            else if ((firstByte & 0x40) != 0)
            {
                // Literal header field with name reference
                var nameIndex = ReadVarint(firstByte, 0x40, ref reader);
                var value = ReadString(ref reader);
                var name = GetNameByIndex(nameIndex);
                
                if (nameIndex <= StaticTable.Length)
                {
                    AddToDynamicTable(name, value);
                }
                
                return new KeyValuePair<string, string>(name, value);
            }
            else if ((firstByte & 0x20) != 0)
            {
                // Dynamic table size update
                var newSize = ReadVarint(firstByte, 0x20, ref reader);
                SetMaxTableSize(newSize);
                return DecodeHeader(ref reader); // Recurse to get actual header
            }
            else
            {
                // Literal header field with literal name (0x00)
                var name = ReadString(ref reader);
                var value = ReadString(ref reader);
                AddToDynamicTable(name, value);
                return new KeyValuePair<string, string>(name, value);
            }
        }

        private KeyValuePair<string, string> GetHeaderByIndex(int index)
        {
            if (index <= 0 || index > StaticTable.Length + _dynamicTable.Count)
                throw new InvalidDataException($"Invalid header index: {index}");

            if (index <= StaticTable.Length)
            {
                var entry = StaticTable[index - 1];
                return new KeyValuePair<string, string>(entry.Name, entry.Value);
            }
            else
            {
                var entry = _dynamicTable[index - StaticTable.Length - 1];
                return new KeyValuePair<string, string>(entry.Name, entry.Value);
            }
        }

        private string GetNameByIndex(int index)
        {
            if (index <= 0 || index > StaticTable.Length + _dynamicTable.Count)
                throw new InvalidDataException($"Invalid header name index: {index}");

            if (index <= StaticTable.Length)
            {
                return StaticTable[index - 1].Name;
            }
            else
            {
                return _dynamicTable[index - StaticTable.Length - 1].Name;
            }
        }

        private string ReadString(ref SpanReader reader)
        {
            var firstByte = reader.ReadByte();
            
            // Check for Huffman encoding (MSB)
            var huffman = (firstByte & 0x80) != 0;
            var length = ReadVarint(firstByte, 0x80, ref reader);
            
            var bytes = reader.ReadBytes(length);
            
            if (huffman)
            {
                // Huffman decoding not implemented, fallback
                return Encoding.UTF8.GetString(bytes);
            }
            
            return Encoding.UTF8.GetString(bytes);
        }

        private int ReadVarint(byte firstByte, byte prefix, ref SpanReader reader)
        {
            var maxValue = (1 << (8 - GetPrefixBits(prefix))) - 1;
            var value = firstByte & maxValue;
            
            if (value < maxValue)
                return value;

            var m = 0;
            while (true)
            {
                var b = reader.ReadByte();
                value += (b & 0x7F) << (7 * m);
                if ((b & 0x80) == 0)
                    break;
                m++;
            }
            return value;
        }

        private int GetPrefixBits(byte prefix)
        {
            if ((prefix & 0x80) != 0) return 1;
            if ((prefix & 0x40) != 0) return 2;
            if ((prefix & 0x20) != 0) return 3;
            if ((prefix & 0x10) != 0) return 4;
            return 8;
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
                _dynamicTable.Add(new HeaderEntry { Name = name, Value = value });
                _currentTableSize += entrySize;
            }
        }

        private void EvictOldEntries()
        {
            while (_currentTableSize > _maxTableSize && _dynamicTable.Count > 0)
            {
                var removed = _dynamicTable[0];
                _currentTableSize -= 32 + removed.Name.Length + removed.Value.Length;
                _dynamicTable.RemoveAt(0);
            }
        }

        private readonly struct HeaderEntry
        {
            public string Name { get; init; }
            public string Value { get; init; }
        }
    }

    // Span reader helper
    internal ref struct SpanReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _position;

        public SpanReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _position = 0;
        }

        public int Remaining => _data.Length - _position;

        public byte ReadByte()
        {
            if (_position >= _data.Length)
                throw new InvalidDataException("Unexpected end of data");
            return _data[_position++];
        }

        public byte[] ReadBytes(int length)
        {
            if (_position + length > _data.Length)
                throw new InvalidDataException("Not enough data");
            var result = _data.Slice(_position, length).ToArray();
            _position += length;
            return result;
        }
    }
}
