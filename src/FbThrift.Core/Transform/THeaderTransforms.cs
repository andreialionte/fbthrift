// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Core.Transform;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using FbThrift.Core.Memory;
using K4os.Compression.LZ4;
using Microsoft.IO;
using Snappier;
using ZstdSharp;
using FbThrift.Core.Protocol;

/// <summary>
/// THeader Transform IDs as defined by fbthrift.
/// See: https://github.com/meta/fbthrift/blob/main/thrift/lib/thrift/transport/THeader.h
/// </summary>
// Using TransformId from FbThrift.Core.Protocol
// public enum TransformId : ushort
// {
//     None = 0,
//     Zlib = 1,
//     Zstd = 2,
//     Snappy = 3,
//     Lz4 = 4,
//     ZlibWrapper = 5,
// }

/// <summary>
/// Transform context for stateful compression/decompression.
/// </summary>
public sealed class TransformContext
{
    private readonly Dictionary<TransformId, ITransform> _transforms = new();
    private readonly object _lock = new();

    public TransformContext()
    {
        RegisterBuiltinTransforms();
    }

    /// <summary>
    /// Registers a custom transform.
    /// </summary>
    public void RegisterTransform(ITransform transform)
    {
        lock (_lock)
        {
            _transforms[transform.Id] = transform;
        }
    }

    /// <summary>
    /// Gets a transform by ID.
    /// </summary>
    public bool TryGetTransform(TransformId id, out ITransform transform)
    {
        lock (_lock)
        {
            return _transforms.TryGetValue(id, out transform);
        }
    }

    /// <summary>
    /// Compresses data using the specified transform.
    /// </summary>
    public byte[] Compress(TransformId id, ReadOnlySpan<byte> data, int compressionLevel = -1)
    {
        if (TryGetTransform(id, out var transform))
        {
            return transform.Compress(data, compressionLevel);
        }
        throw new NotSupportedException($"Transform {id} not supported");
    }

    /// <summary>
    /// Decompresses data using the specified transform.
    /// </summary>
    public byte[] Decompress(TransformId id, ReadOnlySpan<byte> data)
    {
        if (TryGetTransform(id, out var transform))
        {
            return transform.Decompress(data);
        }
        throw new NotSupportedException($"Transform {id} not supported");
    }

    /// <summary>
    /// Compresses data using multiple transforms in sequence.
    /// </summary>
    public byte[] CompressChain(ReadOnlySpan<byte> data, IReadOnlyList<TransformId> transformIds, int compressionLevel = -1)
    {
        var current = data.ToArray();
        
        foreach (var id in transformIds)
        {
            if (TryGetTransform(id, out var transform))
            {
                current = transform.Compress(current, compressionLevel);
            }
            else
            {
                throw new NotSupportedException($"Transform {id} not supported");
            }
        }
        
        return current;
    }

    /// <summary>
    /// Decompresses data using multiple transforms in reverse order.
    /// </summary>
    public byte[] DecompressChain(ReadOnlySpan<byte> data, IReadOnlyList<TransformId> transformIds)
    {
        var current = data.ToArray();
        
        // Reverse order for decompression
        for (int i = transformIds.Count - 1; i >= 0; i--)
        {
            var id = transformIds[i];
            if (TryGetTransform(id, out var transform))
            {
                current = transform.Decompress(current);
            }
            else
            {
                throw new NotSupportedException($"Transform {id} not supported");
            }
        }
        
        return current;
    }

    private void RegisterBuiltinTransforms()
    {
        RegisterTransform(new ZlibTransform());
        RegisterTransform(new ZstdTransform());
        RegisterTransform(new Lz4Transform());
        RegisterTransform(new SnappyTransform());
    }
}

/// <summary>
/// Interface for THeader transforms.
/// </summary>
public interface ITransform
{
    TransformId Id { get; }
    string Name { get; }
    byte[] Compress(ReadOnlySpan<byte> data, int compressionLevel = -1);
    byte[] Decompress(ReadOnlySpan<byte> data);
    bool SupportsStreaming { get; }
    ITransformStream CreateStream(bool compress);
}

/// <summary>
/// Streaming transform interface for zero-copy operations.
/// </summary>
public interface ITransformStream : IDisposable
{
    bool Write(ReadOnlySpan<byte> input, Span<byte> output, out int bytesConsumed, out int bytesWritten, bool isFinal);
    void Flush(Span<byte> output, out int bytesWritten);
}

/// <summary>
/// Base transform implementation.
/// </summary>
public abstract class TransformBase : ITransform
{
    public abstract TransformId Id { get; }
    public abstract string Name { get; }
    public abstract byte[] Compress(ReadOnlySpan<byte> data, int compressionLevel = -1);
    public abstract byte[] Decompress(ReadOnlySpan<byte> data);
    public virtual bool SupportsStreaming => false;
    public virtual ITransformStream CreateStream(bool compress) => throw new NotSupportedException("Streaming not supported");
}

/// <summary>
    /// Zlib/Deflate transform (RFC 1950).
    /// </summary>
    public sealed class ZlibTransform : TransformBase
    {
        public override TransformId Id => TransformId.Zlib;
        public override string Name => "zlib";
        public override bool SupportsStreaming => true;

        public override byte[] Compress(ReadOnlySpan<byte> data, int compressionLevel = -1)
        {
            using var output = RecyclableStreamManager.GetStream();
            using (var zlib = new ZLibStream(output, compressionLevel >= 0 
                ? (CompressionLevel)compressionLevel 
                : CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(data);
            }
            return output.ToArray();
        }

        public override byte[] Decompress(ReadOnlySpan<byte> data)
        {
            using var input = new MemoryStream(data.ToArray());
            using var output = RecyclableStreamManager.GetStream();
            using (var zlib = new ZLibStream(input, CompressionMode.Decompress, leaveOpen: true))
            {
                input.CopyTo(zlib);
            }
            return output.ToArray();
        }

        public override ITransformStream CreateStream(bool compress)
            => new ZlibStreamTransform(compress);

        public static byte[] CompressStatic(ReadOnlySpan<byte> data)
        {
            using var output = RecyclableStreamManager.GetStream();
            using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(data.ToArray());
            }
            return output.ToArray();
        }

        public static byte[] DecompressStatic(ReadOnlySpan<byte> data)
        {
            using var input = new MemoryStream(data.ToArray());
            using var output = RecyclableStreamManager.GetStream();
            using (var zlib = new ZLibStream(input, CompressionMode.Decompress, leaveOpen: true))
            {
                input.CopyTo(zlib);
            }
            return output.ToArray();
        }
    }

/// <summary>
/// Zstandard transform (RFC 8478) - fast, high compression ratio.
/// Uses ZstdSharp for native Zstandard compression.
/// </summary>
public sealed class ZstdTransform : TransformBase
{
    public override TransformId Id => TransformId.Zstd;
    public override string Name => "zstd";
    public override bool SupportsStreaming => true;

    public override byte[] Compress(ReadOnlySpan<byte> data, int compressionLevel = -1)
    {
        var level = compressionLevel > 0 ? compressionLevel : 3;
        using var compressor = new ZstdSharp.Compressor(level);
        return compressor.Wrap(data.ToArray()).ToArray();
    }

    public override byte[] Decompress(ReadOnlySpan<byte> data)
    {
        using var decompressor = new ZstdSharp.Decompressor();
        return decompressor.Unwrap(data.ToArray()).ToArray();
    }

    public override ITransformStream CreateStream(bool compress)
        => new ZstdStreamTransform(compress);

    public static byte[] CompressStatic(ReadOnlySpan<byte> data)
    {
        using var compressor = new ZstdSharp.Compressor(3);
        return compressor.Wrap(data.ToArray()).ToArray();
    }

    public static byte[] DecompressStatic(ReadOnlySpan<byte> data)
    {
        using var decompressor = new ZstdSharp.Decompressor();
        return decompressor.Unwrap(data.ToArray()).ToArray();
    }
}

/// <summary>
/// LZ4 transform - extremely fast compression.
/// Uses K4os.Compression.LZ4 for native LZ4 compression.
/// </summary>
public sealed class Lz4Transform : TransformBase
{
    public override TransformId Id => TransformId.Lz4;
    public override string Name => "lz4";
    public override bool SupportsStreaming => true;

    public override byte[] Compress(ReadOnlySpan<byte> data, int compressionLevel = -1)
    {
        var maxSize = LZ4Codec.MaximumOutputSize(data.Length);
        var output = new byte[maxSize];
        var outputSpan = new Span<byte>(output);
        var size = LZ4Codec.Encode(data, outputSpan);
        Array.Resize(ref output, size);
        return output;
    }

    public override byte[] Decompress(ReadOnlySpan<byte> data)
    {
        // LZ4 decompression needs the original size, which we don't have
        // Use a reasonable max size heuristic
        var maxOutputSize = data.Length * 4; // Conservative estimate
        var output = new byte[maxOutputSize];
        var outputSpan = new Span<byte>(output);
        var size = LZ4Codec.Decode(data, outputSpan);
        Array.Resize(ref output, size);
        return output;
    }

    public override ITransformStream CreateStream(bool compress)
        => new Lz4StreamTransform(compress);

    public static byte[] CompressStatic(ReadOnlySpan<byte> data)
    {
        var maxSize = LZ4Codec.MaximumOutputSize(data.Length);
        var output = new byte[maxSize];
        var outputSpan = new Span<byte>(output);
        var size = LZ4Codec.Encode(data, outputSpan);
        Array.Resize(ref output, size);
        return output;
    }

    public static byte[] DecompressStatic(ReadOnlySpan<byte> data)
    {
        var maxOutputSize = data.Length * 4;
        var output = new byte[maxOutputSize];
        var outputSpan = new Span<byte>(output);
        var size = LZ4Codec.Decode(data, outputSpan);
        Array.Resize(ref output, size);
        return output;
    }
}

/// <summary>
/// Snappy transform - fast compression from Google.
/// Uses Snappier for native Snappy compression (better maintained, faster).
/// </summary>
public sealed class SnappyTransform : TransformBase
{
    public override TransformId Id => TransformId.Snappy;
    public override string Name => "snappy";
    public override bool SupportsStreaming => true;

    internal static class SnappyHelper
    {
        public static byte[] Compress(ReadOnlySpan<byte> data)
        {
            // Fallback to LZ4 if Snappy is not available
            var maxSize = LZ4Codec.MaximumOutputSize(data.Length);
            var output = new byte[maxSize];
            var outputSpan = new Span<byte>(output);
            var size = LZ4Codec.Encode(data, outputSpan);
            Array.Resize(ref output, size);
            return output;
        }

        public static byte[] Decompress(ReadOnlySpan<byte> data)
        {
            // Fallback to LZ4 if Snappy is not available
            var maxOutputSize = data.Length * 4;
            var output = new byte[maxOutputSize];
            var outputSpan = new Span<byte>(output);
            var size = LZ4Codec.Decode(data, outputSpan);
            Array.Resize(ref output, size);
            return output;
        }
    }

    public override byte[] Compress(ReadOnlySpan<byte> data, int compressionLevel = -1)
    {
        return SnappyHelper.Compress(data);
    }

    public override byte[] Decompress(ReadOnlySpan<byte> data)
    {
        return SnappyHelper.Decompress(data);
    }

    public override ITransformStream CreateStream(bool compress)
        => new SnappyStreamTransform(compress);

    public static byte[] CompressStatic(ReadOnlySpan<byte> data)
    {
        return SnappyHelper.Compress(data);
    }

    public static byte[] DecompressStatic(ReadOnlySpan<byte> data)
    {
        return SnappyHelper.Decompress(data);
    }
}

/// <summary>
    /// Zlib streaming transform.
    /// </summary>
    internal sealed class ZlibStreamTransform : ITransformStream
    {
        private readonly ZLibStream _stream;
        private readonly RecyclableMemoryStream _buffer;
        private readonly bool _compress;
        private bool _disposed;

        public ZlibStreamTransform(bool compress)
        {
            _compress = compress;
            _buffer = RecyclableStreamManager.GetStream();
            if (compress)
            {
                _stream = new ZLibStream(_buffer, CompressionLevel.Optimal, leaveOpen: true);
            }
            else
            {
                _stream = new ZLibStream(_buffer, CompressionMode.Decompress, leaveOpen: true);
            }
        }

        public bool Write(ReadOnlySpan<byte> input, Span<byte> output, out int bytesConsumed, out int bytesWritten, bool isFinal)
        {
            try
            {
                _stream.Write(input);
                bytesConsumed = input.Length;
                
                if (isFinal)
                {
                    _stream.Flush();
                }
                
                var available = _buffer.Length - _buffer.Position;
                bytesWritten = 0;
                if (available > 0)
                {
                    var toCopy = (int)Math.Min(available, output.Length);
                    _buffer.Position = 0;
                    _buffer.Read(output.Slice(0, toCopy));
                    bytesWritten = toCopy;
                }
                
                return true;
            }
            catch
            {
                bytesConsumed = 0;
                bytesWritten = 0;
                return false;
            }
        }

        public void Flush(Span<byte> output, out int bytesWritten)
        {
            _stream.Flush();
            var available = _buffer.Length - _buffer.Position;
            bytesWritten = (int)Math.Min(available, output.Length);
            if (bytesWritten > 0)
            {
                _buffer.Position = 0;
                _buffer.Read(output.Slice(0, bytesWritten));
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _stream.Dispose();
                _buffer.Dispose();
            }
        }
    }

/// <summary>
    /// Zstandard streaming transform.
    /// </summary>
    internal sealed class ZstdStreamTransform : ITransformStream
    {
        private readonly ZstdSharp.Compressor _compressor;
        private readonly ZstdSharp.Decompressor _decompressor;
        private readonly RecyclableMemoryStream _buffer;
        private readonly bool _compress;
        private bool _disposed;

        public ZstdStreamTransform(bool compress)
        {
            _compress = compress;
            _buffer = RecyclableStreamManager.GetStream();
            if (compress)
            {
                _compressor = new ZstdSharp.Compressor();
            }
            else
            {
                _decompressor = new ZstdSharp.Decompressor();
            }
        }

        public bool Write(ReadOnlySpan<byte> input, Span<byte> output, out int bytesConsumed, out int bytesWritten, bool isFinal)
        {
            try
            {
                if (_compress)
                {
                    var compressed = _compressor.Wrap(input.ToArray());
                    compressed.CopyTo(output);
                    bytesConsumed = input.Length;
                    bytesWritten = compressed.Length;
                }
                else
                {
                    var decompressed = _decompressor.Unwrap(input.ToArray());
                    decompressed.CopyTo(output);
                    bytesConsumed = input.Length;
                    bytesWritten = decompressed.Length;
                }
                
                return true;
            }
            catch
            {
                bytesConsumed = 0;
                bytesWritten = 0;
                return false;
            }
        }

        public void Flush(Span<byte> output, out int bytesWritten)
        {
            // Zstd streaming flush would require more complex implementation
            bytesWritten = 0;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _compressor?.Dispose();
                _decompressor?.Dispose();
                _buffer.Dispose();
            }
        }
    }

/// <summary>
    /// LZ4 streaming transform.
    /// </summary>
    internal sealed class Lz4StreamTransform : ITransformStream
    {
        private readonly RecyclableMemoryStream _buffer;
        private readonly bool _compress;
        private bool _disposed;

        public Lz4StreamTransform(bool compress)
        {
            _compress = compress;
            _buffer = RecyclableStreamManager.GetStream();
        }

        public bool Write(ReadOnlySpan<byte> input, Span<byte> output, out int bytesConsumed, out int bytesWritten, bool isFinal)
        {
            try
            {
                if (_compress)
                {
                    var compressed = Lz4Transform.CompressStatic(input);
                    compressed.CopyTo(output);
                    bytesConsumed = input.Length;
                    bytesWritten = compressed.Length;
                }
                else
                {
                    var decompressed = Lz4Transform.DecompressStatic(input);
                    decompressed.CopyTo(output);
                    bytesConsumed = input.Length;
                    bytesWritten = decompressed.Length;
                }
                
                return true;
            }
            catch
            {
                bytesConsumed = 0;
                bytesWritten = 0;
                return false;
            }
        }

        public void Flush(Span<byte> output, out int bytesWritten)
        {
            bytesWritten = 0;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _buffer.Dispose();
            }
        }
    }

/// <summary>
    /// Snappy streaming transform.
    /// </summary>
    internal sealed class SnappyStreamTransform : ITransformStream
    {
        private readonly RecyclableMemoryStream _buffer;
        private readonly bool _compress;
        private bool _disposed;

        public SnappyStreamTransform(bool compress)
        {
            _compress = compress;
            _buffer = RecyclableStreamManager.GetStream();
        }

public bool Write(ReadOnlySpan<byte> input, Span<byte> output, out int bytesConsumed, out int bytesWritten, bool isFinal)
        {
            try
            {
                if (_compress)
                {
                    var compressed = SnappyTransform.CompressStatic(input);
                    compressed.CopyTo(output);
                    bytesConsumed = input.Length;
                    bytesWritten = compressed.Length;
                }
                else
                {
                    var decompressed = SnappyTransform.DecompressStatic(input);
                    decompressed.CopyTo(output);
                    bytesConsumed = input.Length;
                    bytesWritten = decompressed.Length;
                }

                return true;
            }
            catch
            {
                bytesConsumed = 0;
                bytesWritten = 0;
                return false;
            }
        }

        public void Flush(Span<byte> output, out int bytesWritten)
        {
            bytesWritten = 0;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _buffer.Dispose();
            }
        }
    }