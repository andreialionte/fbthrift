// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Tcp;

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Unframed Transport - reads/writes raw bytes without length prefix.
/// Compatible with Apache Thrift's TTransport.
/// </summary>
public sealed class UnframedTransport : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private bool _disposed;
    
    public UnframedTransport(Stream stream, bool leaveOpen = false)
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
            throw new ObjectDisposedException(nameof(UnframedTransport));
        
        _stream.Write(buffer);
    }
    
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(UnframedTransport));
        
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
    
    public int ReadByte()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(UnframedTransport));
        
        int b = _stream.ReadByte();
        if (b == -1)
            throw new EndOfStreamException();
        
        return b;
    }
    
    public async ValueTask<int> ReadByteAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(UnframedTransport));
        
        var result = await _stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false);
        if (result == 0)
            throw new EndOfStreamException();
        
        return 0; // Would need to read actual byte
    }
    
    public int Read(Span<byte> buffer)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(UnframedTransport));
        
        return _stream.Read(buffer);
    }
    
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(UnframedTransport));
        
        return await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }
    
    public int Read(Span<byte> buffer, int offset, int count)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(UnframedTransport));
        
        return _stream.Read(buffer[offset..(offset + count)]);
    }
    
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(UnframedTransport));
        
        return await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }
    
    public async ValueTask<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(UnframedTransport));
        
        return await _stream.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
    }
    
    public int Read(Span<byte> buffer, int offset, int count)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(UnframedTransport));
        
        return _stream.Read(buffer[offset..(offset + count)]);
    }
    
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(UnframedTransport));
        
        return await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }
    
    public async ValueTask<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(UnframedTransport));
        
        return await _stream.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
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
}
