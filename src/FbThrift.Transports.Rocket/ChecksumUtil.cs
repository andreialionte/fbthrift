// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Rocket;

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using System.Security.Cryptography;

/// <summary>
/// Checksum utilities for Rocket protocol.
/// Supports CRC32 and XXH3_64 as per fbthrift specification.
/// Uses System.IO.Hashing (built into .NET 8+) for XXH3.
/// </summary>
public static class ChecksumUtil
{
    /// <summary>
    /// Computes CRC32 checksum.
    /// </summary>
    public static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        // CRC32-IEEE 802.3 polynomial
        const uint polynomial = 0xEDB88320;
        uint crc = 0xFFFFFFFF;

        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320 : 0);
            }
        }

        return ~crc;
    }

    /// <summary>
    /// Computes XXH3_64 checksum using System.IO.Hashing (.NET 8+ built-in).
    /// </summary>
    public static ulong ComputeXxh3_64(ReadOnlySpan<byte> data)
    {
        return XxHash3.HashToUInt64(data);
    }

    /// <summary>
    /// Computes XXH3_128 checksum using System.IO.Hashing (.NET 8+ built-in).
    /// </summary>
    public static UInt128 ComputeXxh3_128(ReadOnlySpan<byte> data)
    {
        return XxHash3.HashToUInt128(data);
    }

    /// <summary>
    /// Computes checksum based on algorithm.
    /// </summary>
    public static byte[] ComputeChecksum(ChecksumAlgorithm algorithm, ReadOnlySpan<byte> data)
    {
        return algorithm switch
        {
            ChecksumAlgorithm.Crc32 => BitConverter.GetBytes(ComputeCrc32(data)),
            ChecksumAlgorithm.Xxh3_64 => BitConverter.GetBytes(ComputeXxh3_64(data)),
            _ => Array.Empty<byte>()
        };
    }

    /// <summary>
    /// Verifies checksum.
    /// </summary>
    public static bool VerifyChecksum(ChecksumAlgorithm algorithm, ReadOnlySpan<byte> data, ReadOnlySpan<byte> expectedChecksum)
    {
        var computed = ComputeChecksum(algorithm, data);
        if (computed.Length != expectedChecksum.Length)
            return false;

        for (int i = 0; i < computed.Length; i++)
        {
            if (computed[i] != expectedChecksum[i])
                return false;
        }
        return true;
    }
}
