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
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using FbThrift.Core.Transform;

/// <summary>
/// THeader transport for automatic protocol detection and negotiation.
/// Based on fbthrift THeaderTransport.
/// </summary>
public sealed class HeaderTransport : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly HeaderProtocolMetadata _metadata = new();
    private readonly HeaderProtocolWriter _writer;
    private HeaderProtocolReader? _reader;
    private readonly byte[] _readBuffer = new byte[4096];
    private int _readBufferOffset = 0;
    private int _readBufferCount = 0;
    private bool _disposed;
    private bool _clientMode = true;
    private uint _seqId = 0;
    private uint _writeSeqId = 0;
    private uint _readSeqId = 0;
    private ProtocolId _protocolId = ProtocolId.TBinaryProtocol;
    private ClientType _clientType = ClientType.ThriftHeader;
    private List<ushort> _transforms = new();
    private Dictionary<string, string> _persistentHeaders = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _persistentWriteHeaders = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _readHeaders = new(StringComparer.OrdinalIgnoreCase);
    private bool _allowBigFrames = false;
    private int _maxFrameSize = 16 * 1024 * 1024; // 16MB
    private readonly TransformContext _transformContext = new();
    
    public HeaderTransport(Stream stream, bool leaveOpen = false)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _leaveOpen = leaveOpen;
    }
    
    public bool IsOpen => _stream.CanRead && !_disposed;
    
    public void Open()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(HeaderTransport));
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
        _protocolId = (ProtocolId)protocolId;
    }
    
    public void SetClientType(ushort clientType)
    {
        _clientType = (ClientType)clientType;
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
        _readHeaders[key] = value;
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
    
    // ===== Flush / Write =====
    
    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(HeaderTransport));
        
        // Write any buffered data
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
    
    public void Flush()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(HeaderTransport));
        
        _stream.Flush();
    }
    
    // ===== Write Methods =====
    
    public void Write(ReadOnlySpan<byte> buffer)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(HeaderTransport));
        
        // In a real implementation, we'd buffer and write frames with headers
        // For now, just write directly
        _stream.Write(buffer);
    }
    
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(HeaderTransport));
        
        await _stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }
    
    public void WriteByte(byte value)
    {
        _stream.WriteByte(value);
    }
    
    public async ValueTask WriteByteAsync(byte value, CancellationToken cancellationToken = default)
    {
        await _stream.WriteAsync(new byte[] { value }, cancellationToken).ConfigureAwait(false);
    }
    
    // ===== Read Methods =====
    
    public int ReadByte()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(HeaderTransport));
        
        int b = _stream.ReadByte();
        if (b == -1)
            throw new EndOfStreamException();
        
        return b;
    }
    
    public async ValueTask<int> ReadByteAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(HeaderTransport));
        
        var result = await _stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false);
        if (result == 0)
            throw new EndOfStreamException();
        
        return 0; // Would need to read actual byte
    }
    
    public int Read(Span<byte> buffer)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(HeaderTransport));
        
        return _stream.Read(buffer);
    }
    
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(HeaderTransport));
        
        return await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }
    
    public async ValueTask<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(HeaderTransport));
        
        return await _stream.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
    }
    
    public int Read(Span<byte> buffer, int offset, int count)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(HeaderTransport));
        
        return _stream.Read(buffer[offset..(offset + count)]);
    }
    
    // ===== Frame Writing (THeader framing) =====
    
    public void WriteFrame(ReadOnlyMemory<byte> payload, ushort protocolId = (ushort)ProtocolConstants.BinaryProtocolId)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(HeaderTransport));
        
        // Apply write transforms (compression) to payload
        var transformedPayload = _transforms.Count > 0 
            ? _transformContext.CompressChain(payload.Span, _transforms.ConvertAll(t => (TransformId)t)) 
            : payload.ToArray();
        
        // Build THeader frame
        var writer = new HeaderProtocolWriter(new HeaderProtocolMetadata
        {
            SeqId = (int)Interlocked.Increment(ref _writeSeqId),
            ProtocolId = protocolId,
            Transforms = new List<ushort>(_transforms),
            AllowBigFrames = _allowBigFrames,
            MaxFrameSize = _maxFrameSize
        }, writeHeaders: true);
        
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
        
        var frame = writer.GetHeaderAndPayload();
        
        // Write frame length (4 bytes big-endian) + frame
        var lengthBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lengthBytes, (uint)frame.Length);
        _stream.Write(lengthBytes);
        _stream.Write(frame.Span);
    }
    
    public async ValueTask WriteFrameAsync(ReadOnlyMemory<byte> payload, ushort protocolId = (ushort)ProtocolConstants.BinaryProtocolId, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(HeaderTransport));
        
        // Apply write transforms (compression) to payload
        var transformedPayload = _transforms.Count > 0 
            ? _transformContext.CompressChain(payload.Span, _transforms.ConvertAll(t => (TransformId)t)) 
            : payload.ToArray();
        
        var writer = new HeaderProtocolWriter(new HeaderProtocolMetadata
        {
            SeqId = (int)Interlocked.Increment(ref _writeSeqId),
            ProtocolId = protocolId,
            Transforms = _transforms,
            AllowBigFrames = _allowBigFrames,
            MaxFrameSize = _maxFrameSize
        });
        
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
        
        var frame = writer.GetHeaderAndPayload();
        
        var lengthBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lengthBytes, (uint)frame.Length);
        
        await _stream.WriteAsync(lengthBytes, cancellationToken).ConfigureAwait(false);
        await _stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
    }
    
    // ===== Frame Reading =====
    
    public bool TryReadFrame(out ReadOnlyMemory<byte> payload, out ushort protocolId)
    {
        protocolId = 0;
        payload = ReadOnlyMemory<byte>.Empty;
        
        // Read frame length (4 bytes)
        if (!TryReadExact(4, out var lengthBytes))
            return false;
        
        uint frameLength = BinaryPrimitives.ReadUInt32BigEndian(lengthBytes.AsSpan());
        
        if (frameLength > _maxFrameSize)
            throw new InvalidDataException($"Frame size {frameLength} exceeds maximum {_maxFrameSize}");
        
        var frameBuffer = new byte[frameLength];
        if (!TryReadExact((int)frameLength, frameBuffer))
            return false;
        
        var reader = new HeaderProtocolReader(new ReadOnlySequence<byte>(frameBuffer));
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
        
        // Read frame length
        var lengthBuffer = new byte[4];
        if (!await TryReadExactAsync(lengthBuffer, cancellationToken).ConfigureAwait(false))
            return (false, (ushort)0);
        
        uint frameLength = BinaryPrimitives.ReadUInt32BigEndian(lengthBuffer);
        
        if (frameLength > _maxFrameSize)
            throw new InvalidDataException($"Frame size {frameLength} exceeds maximum {_maxFrameSize}");
        
        var frameBuffer = new byte[frameLength];
        if (!await TryReadExactAsync(frameBuffer, cancellationToken).ConfigureAwait(false))
            return (false, (ushort)0);
        
        var reader = new HeaderProtocolReader(new ReadOnlySequence<byte>(frameBuffer));
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
}