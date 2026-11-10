// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Core.Protocol;

using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using FbThrift.Core.Memory;
using ZstdSharp;

/// <summary>
/// Zstandard compression/decompression for THeader transforms.
/// Uses ZstdSharp for native Zstandard compression.
/// </summary>
public static class ZstdCompression
{
    /// <summary>
    /// Compresses data using Zstandard compression.
    /// </summary>
    public static byte[] Compress(ReadOnlySpan<byte> data, int compressionLevel = 3)
    {
        using var output = RecyclableStreamManager.GetStream();
        using var compressor = new Compressor(compressionLevel);
        {
            var compressed = compressor.Wrap(data.ToArray());
            output.Write(compressed);
        }
        return output.ToArray();
    }
    
    /// <summary>
    /// Decompresses Zstandard compressed data.
    /// </summary>
    public static byte[] Decompress(ReadOnlySpan<byte> data)
    {
        using var output = RecyclableStreamManager.GetStream();
        using (var decompressor = new Decompressor())
        {
            var decompressed = decompressor.Unwrap(data.ToArray());
            output.Write(decompressed);
        }
        return output.ToArray();
    }
    
    /// <summary>
    /// Compresses data to a buffer using Zstd.
    /// </summary>
    public static int CompressToBuffer(ReadOnlySpan<byte> source, Span<byte> destination, int compressionLevel = 3)
    {
        var compressed = Compress(source, compressionLevel);
        if (compressed.Length > destination.Length)
        {
            throw new ArgumentException("Destination buffer too small");
        }
        compressed.CopyTo(destination);
        return compressed.Length;
    }
    
    /// <summary>
    /// Decompresses Zstd data to a buffer.
    /// </summary>
    public static int DecompressToBuffer(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        var decompressed = Decompress(source);
        if (decompressed.Length > destination.Length)
        {
            throw new ArgumentException("Destination buffer too small");
        }
        decompressed.CopyTo(destination);
        return decompressed.Length;
    }
}