// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Tcp;

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Framed Transport - adds length prefix to each message.
/// Compatible with Apache Thrift's TFramedTransport.
/// </summary>
public sealed class FramedTransport : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly byte[] _readBuffer = new byte[4096];
    private readonly byte[] _writeBuffer = new byte[4096];
    private int _readBufferOffset = 0;
    private int _readBufferCount = 0;
    private bool _disposed;
    
    public FramedTransport(Stream stream, bool leaveOpen = false)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _leaveOpen = leaveOpen;
    }
    
    public bool IsOpen => _stream.CanRead && !_disposed;
    
    public void Open() { }
    
    public void Close()
    {
        Dispose();
    }
    
    public void Dispose()
    {
        if (!_disposed)
        {
            if (!_leaveOpen)
            {
                _stream.Dispose();
            }
            _disposed = true;
        }
    }
    
    public void Write(ReadOnlySpan<byte> buffer)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(FramedTransport));
        
        // Write frame length (4 bytes big-endian) + data
        var lengthBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lengthBytes, (uint)buffer.Length);
        
        _stream.Write(lengthBytes);
        _stream.Write(buffer);
    }
    
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(FramedTransport));
        
        var lengthBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lengthBytes, (uint)buffer.Length);
        
        await _stream.WriteAsync(lengthBytes, cancellationToken).ConfigureAwait(false);
        await _stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }
    
    public void WriteByte(byte value)
    {
        var buffer = new byte[] { value };
        Write(buffer);
    }
    
    public async ValueTask WriteByteAsync(byte value, CancellationToken cancellationToken = default)
    {
        await WriteAsync(new byte[] { value }, cancellationToken).ConfigureAwait(false);
    }
    
    public int Read(Span<byte> buffer)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(FramedTransport));
        
        // Read frame length
        if (!TryReadExact(4, out var lengthBytes))
            throw new EndOfStreamException();
        
        uint frameLength = BinaryPrimitives.ReadUInt32BigEndian(lengthBytes);
        
        if (buffer.Length < frameLength)
            throw new ArgumentException("Buffer too small for frame");
        
        int totalRead = 0;
        while (totalRead < frameLength)
        {
            int read = _stream.Read(buffer.Slice(totalRead, (int)frameLength - totalRead));
            if (read == 0)
                throw new EndOfStreamException();
            totalRead += read;
        }
        
        return (int)frameLength;
    }
    
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(FramedTransport));
        
        // Read frame length
        var lengthBytes = new byte[4];
        if (!await TryReadExactAsync(new Memory<byte>(lengthBytes), cancellationToken).ConfigureAwait(false))
            throw new EndOfStreamException();
        
        uint frameLength = BinaryPrimitives.ReadUInt32BigEndian(lengthBytes);
        
        if (buffer.Length < frameLength)
            throw new ArgumentException("Buffer too small for frame");
        
        int totalRead = 0;
        while (totalRead < frameLength)
        {
            int read = await _stream.ReadAsync(buffer.Slice(totalRead, (int)frameLength - totalRead), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException();
            totalRead += read;
        }
        
        return (int)frameLength;
    }
    
    public async ValueTask<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(FramedTransport));
        
        var bufferMemory = new Memory<byte>(buffer, offset, count);
        return await ReadAsync(bufferMemory, cancellationToken).ConfigureAwait(false);
    }
    
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
    
    private async ValueTask<bool> TryReadExactAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int totalRead = 0;
        
        while (totalRead < buffer.Length)
        {
            int read = await _stream.ReadAsync(buffer.Slice(totalRead, buffer.Length - totalRead), cancellationToken).ConfigureAwait(false);
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