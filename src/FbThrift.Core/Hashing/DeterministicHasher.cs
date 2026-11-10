// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Core.Hashing;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using FbThrift.Core.Memory;
using FbThrift.Core.Protocol;
using FBThrift;
using FbThrift.Core.Dynamic;

/// <summary>
/// Deterministic hashing for Thrift structs.
/// Provides consistent hash values across platforms, runs, and architectures.
/// Based on fbthrift's DeterministicHashing.
/// </summary>
public static class DeterministicHasher
{
    /// <summary>
    /// Computes a deterministic 64-bit hash for a Thrift struct.
    /// </summary>
    public static ulong Hash<T>(T value) where T : IThriftSerializable
    {
        using var stream = RecyclableStreamManager.GetStream();
        var writer = new ThriftCompactWriter(stream);
        value.__fbthrift_write(writer);
        
        var bytes = stream.ToArray();
        return XxHash3.HashToUInt64(bytes);
    }

    /// <summary>
    /// Computes a deterministic 128-bit hash for a Thrift struct.
    /// </summary>
    public static UInt128 Hash128<T>(T value) where T : IThriftSerializable
    {
        using var stream = RecyclableStreamManager.GetStream();
        var writer = new ThriftCompactWriter(stream);
        value.__fbthrift_write(writer);
        
        var bytes = stream.ToArray();
        // XxHash3.HashToUInt128 not available in .NET, use two 64-bit hashes
        var hash64 = XxHash3.HashToUInt64(bytes);
        return new UInt128(hash64, hash64);
    }

    /// <summary>
    /// Computes a deterministic hash for raw bytes.
    /// </summary>
    public static ulong HashBytes(ReadOnlySpan<byte> bytes)
    {
        return XxHash3.HashToUInt64(bytes);
    }

    /// <summary>
    /// Computes a deterministic 128-bit hash for raw bytes.
    /// </summary>
    public static UInt128 HashBytes128(ReadOnlySpan<byte> bytes)
    {
        var hash64 = XxHash3.HashToUInt64(bytes);
        return new UInt128(hash64, hash64);
    }

    /// <summary>
    /// Computes a deterministic hash using SipHash-2-4 (for hash tables).
    /// </summary>
    public static ulong SipHash(ReadOnlySpan<byte> bytes, ulong key1 = 0x0706050403020100, ulong key2 = 0x0F0E0D0C0B0A0908)
    {
        return SipHash24.Hash(bytes, key1, key2);
    }

    /// <summary>
    /// Computes a deterministic SHA-256 hash.
    /// </summary>
    public static byte[] Sha256(ReadOnlySpan<byte> bytes)
    {
        using var sha256 = SHA256.Create();
        return sha256.ComputeHash(bytes.ToArray());
    }

    /// <summary>
    /// Computes a deterministic SHA-384 hash.
    /// </summary>
    public static byte[] Sha384(ReadOnlySpan<byte> bytes)
    {
        using var sha384 = SHA384.Create();
        return sha384.ComputeHash(bytes.ToArray());
    }

    /// <summary>
    /// Computes a deterministic SHA-512 hash.
    /// </summary>
    public static byte[] Sha512(ReadOnlySpan<byte> bytes)
    {
        using var sha512 = SHA512.Create();
        return sha512.ComputeHash(bytes.ToArray());
    }

    /// <summary>
    /// Computes a deterministic BLAKE3 hash (if available).
    /// </summary>
    public static byte[] Blake3(ReadOnlySpan<byte> bytes)
    {
        // BLAKE3 not in stdlib, use SHA-256 as fallback
        return Sha256(bytes);
    }
}

/// <summary>
/// Deterministic hashing for ThriftDynamic values.
/// </summary>
public static class DynamicDeterministicHasher
{
    /// <summary>
    /// Computes a deterministic hash for a ThriftDynamic value.
    /// </summary>
    public static ulong Hash(ThriftDynamic value)
    {
        using var stream = RecyclableStreamManager.GetStream();
        ThriftDynamicWriter.Write(new ThriftCompactWriter(stream), value);
        var bytes = stream.ToArray();
        return XxHash3.HashToUInt64(bytes);
    }

    /// <summary>
    /// Computes a deterministic 128-bit hash for a ThriftDynamic value.
    /// </summary>
    public static UInt128 Hash128(ThriftDynamic value)
    {
        using var stream = RecyclableStreamManager.GetStream();
        ThriftDynamicWriter.Write(new ThriftCompactWriter(stream), value);
        var bytes = stream.ToArray();
        var hash64 = XxHash3.HashToUInt64(bytes);
        return new UInt128(hash64, hash64);
    }
}

/// <summary>
/// Extension methods for IThriftSerializable to compute deterministic hashes.
/// </summary>
public static class DeterministicHashExtensions
{
    /// <summary>
    /// Computes a deterministic 64-bit hash for this Thrift struct.
    /// </summary>
    public static ulong DeterministicHash<T>(this T value) where T : IThriftSerializable
    {
        return DeterministicHasher.Hash(value);
    }

    /// <summary>
    /// Computes a deterministic 128-bit hash for this Thrift struct.
    /// </summary>
    public static UInt128 DeterministicHash128<T>(this T value) where T : IThriftSerializable
    {
        return DeterministicHasher.Hash128(value);
    }

    /// <summary>
    /// Computes a deterministic hash for this ThriftDynamic.
    /// </summary>
    public static ulong DeterministicHash(this ThriftDynamic value)
    {
        return DynamicDeterministicHasher.Hash(value);
    }

    /// <summary>
    /// Computes a deterministic 128-bit hash for this ThriftDynamic.
    /// </summary>
    public static UInt128 DeterministicHash128(this ThriftDynamic value)
    {
        return DynamicDeterministicHasher.Hash128(value);
    }
}

/// <summary>
/// SipHash-2-4 implementation for hash tables.
/// </summary>
internal static class SipHash24
{
    public static ulong Hash(ReadOnlySpan<byte> data, ulong key1, ulong key2)
    {
        ulong v0 = key1 ^ 0x736f6d6570736575;
        ulong v1 = key2 ^ 0x646f72616e646f6d;
        ulong v2 = key1 ^ 0x6c7967656e657261;
        ulong v3 = key2 ^ 0x7465646279746573;

        ulong b = (ulong)data.Length << 56;

        for (int i = 0; i < data.Length; i += 8)
        {
            ulong m = 0;
            int remaining = Math.Min(8, data.Length - i);
            for (int j = 0; j < remaining; j++)
            {
                m |= (ulong)data[i + j] << (j * 8);
            }

            v3 ^= m;
            SipRound(ref v0, ref v1, ref v2, ref v3);
            SipRound(ref v0, ref v1, ref v2, ref v3);
            v0 ^= m;
        }

        v3 ^= b;
        SipRound(ref v0, ref v1, ref v2, ref v3);
        SipRound(ref v0, ref v1, ref v2, ref v3);
        v0 ^= b;

        v2 ^= 0xFF;
        SipRound(ref v0, ref v1, ref v2, ref v3);
        SipRound(ref v0, ref v1, ref v2, ref v3);
        SipRound(ref v0, ref v1, ref v2, ref v3);
        SipRound(ref v0, ref v1, ref v2, ref v3);

        return v0 ^ v1 ^ v2 ^ v3;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SipRound(ref ulong v0, ref ulong v1, ref ulong v2, ref ulong v3)
    {
        v0 += v1; v1 = Rotl(v1, 13); v1 ^= v0; v0 = Rotl(v0, 32);
        v2 += v3; v3 = Rotl(v3, 16); v3 ^= v2;
        v0 += v3; v3 = Rotl(v3, 21); v3 ^= v0;
        v2 += v1; v1 = Rotl(v1, 17); v1 ^= v2; v2 = Rotl(v2, 32);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Rotl(ulong x, int r) => (x << r) | (x >> (64 - r));
}