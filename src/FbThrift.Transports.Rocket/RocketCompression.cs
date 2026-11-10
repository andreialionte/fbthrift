// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Rocket;

using System;
using System.Buffers;
using NativeCompressions;

/// <summary>
/// Compression utilities using high-performance native libraries.
/// Uses LZ4 and ZSTD via NativeCompressions libraries.
/// </summary>
public static class RocketCompression
{
    /// <summary>
    /// Compresses data using the specified algorithm.
    /// </summary>
    public static byte[] Compress(ReadOnlySpan<byte> data, CompressionAlgorithm algorithm, int level = 3)
    {
        return algorithm switch
        {
            CompressionAlgorithm.None => data.ToArray(),
            CompressionAlgorithm.Zlib => throw new NotSupportedException("Use Zstd or LZ4 instead"),
            CompressionAlgorithm.Zstd => NativeCompressions.Zstandard.Compress(data.ToArray(), level),
            CompressionAlgorithm.ZstdLess => NativeCompressions.Zstandard.Compress(data.ToArray(), 1),
            CompressionAlgorithm.ZstdMore => NativeCompressions.Zstandard.Compress(data.ToArray(), 9),
            CompressionAlgorithm.Lz4 => NativeCompressions.LZ4.Compress(data),
            CompressionAlgorithm.Lz4Less => NativeCompressions.LZ4.Compress(data),
            CompressionAlgorithm.Lz4More => NativeCompressions.LZ4.Compress(data),
            CompressionAlgorithm.Custom => throw new NotSupportedException("Custom compression not implemented"),
            _ => data.ToArray()
        };
    }

    /// <summary>
    /// Decompresses data using the specified algorithm.
    /// </summary>
    public static byte[] Decompress(ReadOnlySpan<byte> data, CompressionAlgorithm algorithm)
    {
        return algorithm switch
        {
            CompressionAlgorithm.None => data.ToArray(),
            CompressionAlgorithm.Zlib => throw new NotSupportedException("Use Zstd or LZ4 instead"),
            CompressionAlgorithm.Zstd => NativeCompressions.Zstandard.Decompress(data.ToArray()),
            CompressionAlgorithm.ZstdLess => NativeCompressions.Zstandard.Decompress(data.ToArray()),
            CompressionAlgorithm.ZstdMore => NativeCompressions.Zstandard.Decompress(data.ToArray()),
            CompressionAlgorithm.Lz4 => NativeCompressions.LZ4.Decompress(data.ToArray()),
            CompressionAlgorithm.Lz4Less => NativeCompressions.LZ4.Decompress(data.ToArray()),
            CompressionAlgorithm.Lz4More => NativeCompressions.LZ4.Decompress(data.ToArray()),
            CompressionAlgorithm.Custom => throw new NotSupportedException("Custom compression not implemented"),
            _ => data.ToArray()
        };
    }
}
