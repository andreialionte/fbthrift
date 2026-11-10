// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Rocket;

using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

/// <summary>
/// HKDF (RFC 5869) utility functions shared across TLS/QUIC implementations.
/// Used by QuicTls and QuicKeyDerivation for TLS 1.3 / QUIC key schedule.
/// Reference: RFC 5869 (HKDF), RFC 8446 (TLS 1.3 Key Schedule), RFC 9001 (QUIC Key Schedule)
/// </summary>
public static class Hkdf
{
    /// <summary>
    /// HKDF-Extract per RFC 5869.
    /// PRK = HMAC-Hash(salt, IKM)
    /// </summary>
    public static byte[] Extract(ReadOnlySpan<byte> salt, ReadOnlySpan<byte> ikm)
    {
        var saltBytes = salt.ToArray();
        using var hmac = new HMACSHA256(saltBytes);
        return hmac.ComputeHash(ikm.ToArray());
    }

    /// <summary>
    /// HKDF-Expand per RFC 5869.
    /// OKM = HMAC-Hash(PRK, info || counter)
    /// </summary>
    public static byte[] Expand(ReadOnlySpan<byte> prk, ReadOnlySpan<byte> info, int length)
    {
        var prkBytes = prk.ToArray();
        using var hmac = new HMACSHA256(prkBytes);
        
        var okm = new byte[length];
        var t = Array.Empty<byte>();
        var counter = 1;
        var offset = 0;
        
        while (offset < length)
        {
            var data = new byte[t.Length + info.Length + 1];
            if (t.Length > 0) t.AsSpan().CopyTo(data);
            info.CopyTo(data.AsSpan(t.Length));
            data[data.Length - 1] = (byte)counter;
            
            t = hmac.ComputeHash(data);
            var copyLen = Math.Min(t.Length, length - offset);
            t.AsSpan(0, copyLen).CopyTo(okm.AsSpan(offset));
            offset += copyLen;
            counter++;
        }
        
        return okm;
    }

    /// <summary>
    /// HKDF-Expand-Label per RFC 8446 / RFC 9001.
    /// HkdfLabel = length (2 bytes) + label length (1 byte) + label + context length (1 byte) + context
    /// </summary>
    public static byte[] ExpandLabel(ReadOnlySpan<byte> secret, string label, ReadOnlySpan<byte> context, int length)
    {
        var secretBytes = secret.ToArray();
        using var hmac = new HMACSHA256(secretBytes);
        
        // Encode label as "tls13 " + label
        var fullLabel = "tls13 " + label;
        var labelBytes = System.Text.Encoding.ASCII.GetBytes(fullLabel);
        
        // HkdfLabel = length (2 bytes) + label length (1 byte) + label + context length (1 byte) + context
        var hkdfLabel = new byte[2 + 1 + labelBytes.Length + 1 + context.Length];
        BinaryPrimitives.WriteUInt16BigEndian(hkdfLabel.AsSpan(0, 2), (ushort)length);
        hkdfLabel[2] = (byte)labelBytes.Length;
        labelBytes.CopyTo(hkdfLabel.AsSpan(3, labelBytes.Length));
        hkdfLabel[3 + labelBytes.Length] = (byte)context.Length;
        context.CopyTo(hkdfLabel.AsSpan(4 + labelBytes.Length, context.Length));
        
        return hmac.ComputeHash(hkdfLabel);
    }
}
