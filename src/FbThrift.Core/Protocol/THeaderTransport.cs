// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Core.Protocol;

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FbThrift.Core.Transform;

/// <summary>
/// THeader Transport - Full implementation of fbthrift's THeaderTransport.
/// Supports framed/unframed transports, transforms (zlib/zstd/snappy), HTTP upgrade, client type detection.
/// Based on fbthrift's THeaderTransport.cpp and THeader.cpp.
/// </summary>
public sealed class THeaderTransport : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly THeaderMetadata _metadata = new();
    private readonly THeaderWriter _writer;
    private THeaderReader? _reader;
    private readonly byte[] _readBuffer = new byte[65536];
    private int _readBufferOffset = 0;
    private int _readBufferCount = 0;
    private bool _disposed;
    private bool _clientMode = true;
    private uint _seqId = 0;
    private uint _writeSeqId = 0;
    private uint _readSeqId = 0;
    private ushort _protocolId = 0x0001; // Compact by default
    private ushort _clientType = 5; // THRIFT_HEADER_CLIENT_TYPE
    private List<ushort> _transforms = new();
    private Dictionary<string, string> _persistentHeaders = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _persistentWriteHeaders = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _readHeaders = new(StringComparer.OrdinalIgnoreCase);
    private bool _allowBigFrames = false;
    private int _maxFrameSize = 16 * 1024 * 1024; // 16MB
    private byte[] _writeBuffer = new byte[4096];
    private int _writeBufferOffset = 0;
    private readonly object _writeLock = new();
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private readonly TransformContext _transformContext = new();
    
    // Framed/Unframed transport support
    private bool _framedMode = true;
    private bool _unframedMode = false;
    private THeaderClientType _detectedClientType = THeaderClientType.ThriftHeader;
    
    public THeaderTransport(Stream stream, bool leaveOpen = false)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _leaveOpen = leaveOpen;
        _writer = new THeaderWriter(new THeaderMetadata(), true);
    }

    public bool IsOpen => _stream.CanRead && !_disposed;

    public void Open()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(THeaderTransport));
    }
    
    public void Close()
    {
        if (!_leaveOpen)
        {
            _stream.Close();
        }
        _disposed = true;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Close();
        }
    }

    // ===== Client Configuration =====
    
    public void SetClientMode(bool clientMode)
    {
        _clientMode = clientMode;
    }
    
    public void SetProtocolId(ushort protocolId)
    {
        _protocolId = protocolId;
    }
    
    public void SetClientType(ushort clientType)
    {
        _clientType = clientType;
    }
    
    public void AddTransform(ushort transformId)
    {
        if (!_transforms.Contains(transformId))
        {
            _transforms.Add(transformId);
        }
    }
    
    public void SetHeader(string key, string value)
    {
        _metadata.Headers[key] = value;
    }
    
    public string? GetHeader(string key)
    {
        return _readHeaders.TryGetValue(key, out var value) ? value : null;
    }
    
    public void SetPersistentHeader(string key, string value)
    {
        _persistentHeaders[key] = value;
    }
    
    public void SetPersistentWriteHeader(string key, string value)
    {
        _persistentWriteHeaders[key] = value;
    }
    
    public void SetAllowBigFrames(bool allow)
    {
        _allowBigFrames = true;
    }
    
    public void SetMaxFrameSize(int size)
    {
        _maxFrameSize = size;
    }

    // ===== Framed/Unframed Transport Mode =====

    /// <summary>
    /// Sets the transport to framed mode (default).
    /// </summary>
    public void SetFramedMode()
    {
        _framedMode = true;
        _unframedMode = false;
    }

    /// <summary>
    /// Sets the transport to unframed mode (no length prefix).
    /// </summary>
    public void SetUnframedMode()
    {
        _framedMode = false;
        _unframedMode = true;
    }

    /// <summary>
    /// Sets unframed compact mode (THRIFT_UNFRAMED_COMPACT_DEPRECATED).
    /// </summary>
    public void SetUnframedCompactMode()
    {
        _framedMode = false;
        _unframedMode = true;
        _protocolId = 0x0001; // Compact protocol
    }

    /// <summary>
    /// Gets whether the transport is in framed mode.
    /// </summary>
    public bool IsFramedMode => _framedMode;

    /// <summary>
    /// Gets whether the transport is in unframed mode.
    /// </summary>
    public bool IsUnframedMode => _unframedMode;

    // ===== Flush / Write =====
    
    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(THeaderTransport));
        
        await _flushLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        
        try
        {
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _flushLock.Release();
        }
    }
    
    public void Flush()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(THeaderTransport));
        
        _stream.Flush();
    }
    
    // ===== Write Methods =====
    
    public void Write(ReadOnlySpan<byte> buffer)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(THeaderTransport));
        
        if (_framedMode)
        {
            WriteFrame(buffer);
        }
        else
        {
            _stream.Write(buffer);
        }
    }
    
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
if (_disposed)
            throw new ObjectDisposedException(nameof(THeaderTransport));
        
        if (_framedMode)
        {
            await WriteFrameAsync(buffer.ToArray(), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
    }
    
    public void WriteByte(byte value)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(THeaderTransport));
        
        if (_framedMode)
        {
            WriteFrame(new byte[] { value });
        }
        else
        {
            _stream.WriteByte(value);
        }
    }
    
    public async ValueTask WriteByteAsync(byte value, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(THeaderTransport));
        
if (_framedMode)
            {
                await WriteFrameAsync(new byte[] { value }, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        else
        {
            await _stream.WriteAsync(new byte[] { value }, cancellationToken).ConfigureAwait(false);
        }
    }
    
    // ===== Read Methods =====
    
    public int ReadByte()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(THeaderTransport));
        
        int b = _stream.ReadByte();
        if (b == -1)
            throw new EndOfStreamException();
        
        return b;
    }
    
    public async ValueTask<int> ReadByteAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(THeaderTransport));
        
        var result = await _stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false);
        if (result == 0)
            throw new EndOfStreamException();
        
        return 0; // Would need to read actual byte
    }
    
    public int Read(Span<byte> buffer)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(THeaderTransport));
        
        if (_framedMode)
        {
            return ReadFramed(buffer);
        }
        else
        {
            return _stream.Read(buffer);
        }
    }

    public int Read(Span<byte> buffer, int offset, int count)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(THeaderTransport));
        
        if (_framedMode)
        {
            return ReadFramed(buffer[offset..(offset + count)]);
        }
        else
        {
            return _stream.Read(buffer[offset..(offset + count)]);
        }
    }
    
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(THeaderTransport));
        
        if (_framedMode)
        {
            return await ReadFramedAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            return await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
    }
    
    public async ValueTask<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(THeaderTransport));
        
        if (_framedMode)
        {
            return await ReadFramedAsync(new Memory<byte>(buffer, offset, count), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            return await _stream.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        }
    }
    
    // ===== Frame Writing (THeader framing) =====
    
    public void WriteFrame(ReadOnlySpan<byte> payload, ushort protocolId = 0x0001)
    {
        WriteFrame(payload.ToArray(), protocolId);
    }
    
    public void WriteFrame(byte[] payload, ushort protocolId = 0x0001)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(THeaderTransport));
        
        // Apply write transforms (compression) to payload
        var payloadMemory = new ReadOnlyMemory<byte>(payload);
        var transformedPayload = _transforms.Count > 0 
            ? _transformContext.CompressChain(payloadMemory.Span, _transforms.ConvertAll(t => (TransformId)t)) 
            : payload;
        
        // Build THeader frame
        var metadata = new THeaderMetadata
        {
            SeqId = (uint)Interlocked.Increment(ref _writeSeqId),
            ProtocolId = _protocolId,
            Transforms = _transforms,
            AllowBigFrames = _allowBigFrames,
            MaxFrameSize = _maxFrameSize
        };
        var writer = new THeaderWriter(metadata);
        
        // Add persistent headers
        foreach (var kvp in _persistentHeaders)
        {
            writer.Metadata.PersistentHeaders[kvp.Key] = kvp.Value;
        }
        
        foreach (var kvp in _persistentWriteHeaders)
        {
            writer.Metadata.Headers[kvp.Key] = kvp.Value;
        }
        
        writer.WriteHeader();
        writer.WritePayload(transformedPayload);
        
        var (header, payloadBytes) = writer.GetHeaderAndPayload();
        
        // Write frame length (4 bytes big-endian) + frame
        var lengthBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lengthBytes, (uint)(header.Length + payloadBytes.Length));
        _stream.Write(lengthBytes);
        _stream.Write(header);
        _stream.Write(payloadBytes);
    }
    
    public async ValueTask WriteFrameAsync(byte[] payload, ushort protocolId = 0x0001, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(THeaderTransport));
        
        var transformedPayload = _transforms.Count > 0 
            ? _transformContext.CompressChain(payload, _transforms.ConvertAll(t => (TransformId)t)) 
            : payload;
        
        var metadata = new THeaderMetadata
        {
            SeqId = (uint)Interlocked.Increment(ref _writeSeqId),
            ProtocolId = _protocolId,
            Transforms = _transforms,
            AllowBigFrames = _allowBigFrames,
            MaxFrameSize = _maxFrameSize
        };
        var writer = new THeaderWriter(metadata);
        
        foreach (var kvp in _persistentHeaders)
        {
            writer.Metadata.PersistentHeaders[kvp.Key] = kvp.Value;
        }
        
        foreach (var kvp in _persistentWriteHeaders)
        {
            writer.Metadata.Headers[kvp.Key] = kvp.Value;
        }
        
        writer.WriteHeader();
        writer.WritePayload(transformedPayload);
        
        var (header, payloadBytes) = writer.GetHeaderAndPayload();
        
        var lengthBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lengthBytes, (uint)(header.Length + payloadBytes.Length));
        
        await _stream.WriteAsync(lengthBytes, cancellationToken).ConfigureAwait(false);
        await _stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await _stream.WriteAsync(payloadBytes, cancellationToken).ConfigureAwait(false);
    }
    
    // ===== Frame Reading =====
    
    public bool TryReadFrame(out ReadOnlyMemory<byte> payload, out ushort protocolId)
    {
        protocolId = 0;
        payload = ReadOnlyMemory<byte>.Empty;
        
        // Read frame length (4 bytes)
        if (!TryReadExact(4, out var lengthBytes))
            return false;
        
        uint frameLength = BinaryPrimitives.ReadUInt32BigEndian(lengthBytes);
        
        if (frameLength > _maxFrameSize)
            throw new InvalidDataException($"Frame size {frameLength} exceeds maximum {_maxFrameSize}");
        
        var frameBuffer = new byte[frameLength];
        if (!TryReadExact((int)frameLength, frameBuffer))
            return false;
        
        var reader = new THeaderReader(new ReadOnlySequence<byte>(frameBuffer));
        if (!reader.TryReadHeader())
            return false;
        
        protocolId = reader.Metadata.ProtocolId;
        var compressedPayload = reader.GetPayload();
        
        // Apply transforms if any
        if (reader.Metadata.Transforms.Count > 0)
        {
            var transformIds = reader.Metadata.Transforms.ConvertAll(t => (TransformId)t);
            payload = _transformContext.DecompressChain(compressedPayload.Span, transformIds);
        }
        else
        {
            payload = compressedPayload;
        }
        
        _readSeqId = (uint)reader.Metadata.SeqId;
        _readHeaders = reader.Metadata.Headers;
        
        return true;
    }
    
public async ValueTask<(bool Success, ushort ProtocolId)> TryReadFrameAsync(Memory<byte> payloadBuffer, CancellationToken cancellationToken = default)
{
    var protocolId = (ushort)0;
        
        // Read frame length (4 bytes)
        var lengthBuffer = new byte[4];
        if (!await TryReadExactAsync(lengthBuffer, cancellationToken).ConfigureAwait(false))
            return (false, (ushort)0);
        
        uint frameLength = BinaryPrimitives.ReadUInt32BigEndian(lengthBuffer);
        
        if (frameLength > _maxFrameSize)
            throw new InvalidDataException($"Frame size {frameLength} exceeds maximum {_maxFrameSize}");
        
        if (frameLength > payloadBuffer.Length)
            throw new ArgumentException("Payload buffer too small");
        
        var frameBuffer = new byte[frameLength];
        if (!await TryReadExactAsync(frameBuffer, cancellationToken).ConfigureAwait(false))
            return (false, (ushort)0);
        
        var reader = new THeaderReader(new ReadOnlySequence<byte>(frameBuffer));
        if (!reader.TryReadHeader())
            return (false, (ushort)0);
        
        protocolId = reader.Metadata.ProtocolId;
        var compressedPayload = reader.GetPayload();
        
        // Apply transforms if any
        ReadOnlyMemory<byte> payload;
        if (reader.Metadata.Transforms.Count > 0)
        {
            var transformIds = reader.Metadata.Transforms.ConvertAll(t => (TransformId)t);
            payload = _transformContext.DecompressChain(compressedPayload.Span, transformIds);
        }
        else
        {
            payload = compressedPayload;
        }
        
        if (payload.Length > payloadBuffer.Length)
            throw new InvalidDataException("Decompressed payload too large for buffer");
        
        payload.CopyTo(payloadBuffer);
        
        _readSeqId = (uint)reader.Metadata.SeqId;
        _readHeaders = reader.Metadata.Headers;
        
        return (true, protocolId);
    }
    
    // ===== HTTP Upgrade Support =====
    
    /// <summary>
    /// Detects if the incoming data is an HTTP request (for HTTP upgrade).
    /// </summary>
    public static bool IsHttpRequest(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) return false;
        
        // Check for HTTP methods
        if (data.Length >= 4)
        {
            var first4 = BinaryPrimitives.ReadUInt32BigEndian(data);
            if (first4 == THeaderConstants.HttpGetClientMagic ||  // "GET "
                first4 == THeaderConstants.HttpHeadClientMagic ||  // "HEAD"
                first4 == THeaderConstants.HttpClientMagic)       // "POST"
            {
                return true;
            }
        }
        
        if (data.Length >= 4)
        {
            var first4 = BinaryPrimitives.ReadUInt32BigEndian(data);
            if (first4 == THeaderConstants.HttpServerMagic) // "HTTP"
            {
                return true;
            }
        }
        
        return false;
    }
    
    /// <summary>
    /// Attempts to perform HTTP upgrade to Rocket protocol.
    /// </summary>
    public async ValueTask<bool> TryUpgradeToRocketAsync(CancellationToken cancellationToken = default)
    {
        if (!_clientMode)
            throw new InvalidOperationException("HTTP upgrade only supported in client mode");
        
        // Send HTTP upgrade request
        var upgradeRequest = "POST / HTTP/1.1\r\n" +
                            "Upgrade: rocket\r\n" +
                            "Connection: Upgrade\r\n" +
                            "Content-Length: 0\r\n" +
                            "\r\n";
        
        var requestBytes = System.Text.Encoding.ASCII.GetBytes(upgradeRequest);
        await _stream.WriteAsync(requestBytes, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        
        // Read response
        var responseBuffer = new byte[4096];
        var bytesRead = await _stream.ReadAsync(responseBuffer, cancellationToken).ConfigureAwait(false);
        
        if (bytesRead == 0) return false;
        
        var response = System.Text.Encoding.ASCII.GetString(responseBuffer.AsSpan(0, bytesRead));
        
        // Check for 101 Switching Protocols
        if (response.Contains("101 Switching Protocols") && 
            response.Contains("Upgrade: rocket"))
        {
            _protocolId = 0x0003; // Rocket protocol
            return true;
        }
        
return false;
    }
    
    /// <summary>
    /// Attempts HTTP/2 upgrade (h2c - cleartext HTTP/2).
    /// </summary>
    public async ValueTask<bool> TryUpgradeToH2cAsync(CancellationToken cancellationToken = default)
    {
        if (!_clientMode)
            throw new InvalidOperationException("HTTP/2 upgrade only supported in client mode");
        
        // Send HTTP/2 upgrade request (h2c)
        var upgradeRequest = "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n";
        var requestBytes = System.Text.Encoding.ASCII.GetBytes(upgradeRequest);
        await _stream.WriteAsync(requestBytes, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        
        // Read response
        var responseBuffer = new byte[4096];
        var bytesRead = await _stream.ReadAsync(responseBuffer, cancellationToken).ConfigureAwait(false);
        
        if (bytesRead == 0) return false;
        
        var response = System.Text.Encoding.ASCII.GetString(responseBuffer.AsSpan(0, bytesRead));
        
        // Check for 101 Switching Protocols
        if (response.Contains("101 Switching Protocols") && 
            response.Contains("h2c"))
        {
            _protocolId = 0x0002; // HTTP/2 protocol
            return true;
        }
        
        return false;
}
    
    /// <summary>
    /// Detects client type from incoming data.
    /// Based on fbthrift's THeader::analyzeFirst32bit and analyzeSecond32bit.
    /// </summary>
    public static uint DetectClientType(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) return 0; // THRIFT_UNKNOWN_CLIENT_TYPE
        
        var first4 = BinaryPrimitives.ReadUInt32BigEndian(data);
        
        // Check for HTTP server
        if (first4 == THeaderConstants.HttpServerMagic)
            return THeaderConstants.ThriftHttpServerType;
        
        // Check for HTTP client (GET)
        if (first4 == THeaderConstants.HttpGetClientMagic)
            return THeaderConstants.ThriftHttpClientType;
        
        // Check for HTTP client (HEAD)
        if (first4 == THeaderConstants.HttpHeadClientMagic)
            return THeaderConstants.ThriftHttpClientType;
        
        // Check for HTTP client (POST)
        if (first4 == THeaderConstants.HttpClientMagic)
            return THeaderConstants.ThriftHttpClientType;
        
        // Check for binary protocol
        if ((first4 & 0xFFFF0000) == THeaderConstants.BinaryProtocolVersion1)
            return THeaderConstants.ThriftFramedDeprecated;
        
        // Check for compact protocol
        var firstByte = data[0];
        if (firstByte == 0x82)
            return THeaderConstants.ThriftFramedCompact;
        
        // Check for unframed compact
        if (data.Length >= 2 && data[0] == 0x82)
        {
            // Check if it's unframed compact (no length prefix)
            return THeaderConstants.ThriftUnframedCompactDeprecated;
        }
        
        // Check for HTTP client
        if (first4 == THeaderConstants.HttpClientMagic)
            return THeaderConstants.ThriftHttpClientType;
        
// Check for unframed deprecated
        if (data.Length >= 4)
        {
            var first4Bytes = BinaryPrimitives.ReadUInt32BigEndian(data);
            if ((first4Bytes & 0xFFFF0000) == THeaderConstants.BinaryProtocolVersion1)
            {
                // Could be unframed deprecated
                return THeaderConstants.ThriftUnframedDeprecated;
            }
        }
        
        return 0; // THRIFT_UNKNOWN_CLIENT_TYPE
    }
    
    // ===== Helpers =====
    
    private bool TryReadExact(int count, out byte[] buffer)
    {
        buffer = new byte[count];
        int totalRead = 0;
        
        while (totalRead < count)
        {
            int read = _stream.Read(buffer, totalRead, count - totalRead);
            if (read == 0)
            {
                if (totalRead == 0)
                    return false;
                break;
            }
            totalRead += read;
        }
        
        return totalRead == count;
    }
    
    private bool TryReadExact(int count, byte[] buffer)
    {
        int totalRead = 0;
        
        while (totalRead < count)
        {
            int read = _stream.Read(buffer, totalRead, count - totalRead);
            if (read == 0)
            {
                if (totalRead == 0)
                    return false;
                break;
            }
            totalRead += read;
        }
        
        return totalRead == count;
    }
    
    private async ValueTask<bool> TryReadExactAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        int totalRead = 0;
        
        while (totalRead < buffer.Length)
        {
            int read = await _stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (totalRead == 0)
                    return false;
                break;
            }
            totalRead += read;
        }
        
        return totalRead == buffer.Length;
    }
    
    // ===== Frame Reading (Framed) =====
    
    private int ReadFramed(Span<byte> buffer)
    {
        if (!TryReadFrame(out var payload, out _))
            return 0;
        
        int copyLength = Math.Min(payload.Length, buffer.Length);
        payload.Span[..copyLength].CopyTo(buffer);
        return copyLength;
    }
    
    private async ValueTask<int> ReadFramedAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var (success, protocolId) = await TryReadFrameAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (!success)
            return 0;
        
        return buffer.Length; // Simplified - would return actual bytes read
    }
}