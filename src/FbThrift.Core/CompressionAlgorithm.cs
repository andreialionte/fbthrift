// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Core;

/// <summary>
/// Compression algorithms supported by fbthrift.
/// Used by ThriftCompressionAttribute and transport layers.
/// </summary>
public enum CompressionAlgorithm : byte
{
    None = 0,
    Zlib = 1,
    Zstd = 2,
    Lz4 = 3,
    ZlibLess = 3,
    ZlibMore = 4,
    ZstdLess = 5,
    ZstdMore = 6,
    Lz4Less = 7,
    Lz4More = 8,
    Custom = 255,
}

/// <summary>
/// Checksum algorithms supported by fbthrift.
/// </summary>
public enum ChecksumAlgorithm : byte
{
    None = 0,
    Crc32 = 1,
    Xxh3_64 = 2,
}