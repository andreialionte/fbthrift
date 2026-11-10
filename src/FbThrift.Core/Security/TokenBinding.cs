// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Core.Security;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

/// <summary>
/// TLS Token Binding implementation for QUIC/Thrift.
/// Based on RFC 8472 (Token Binding over HTTP) and fbthrift's token binding support.
/// Provides cryptographic binding between TLS connections and application tokens.
/// </summary>
public static class TokenBinding
{
    // Token Binding types (RFC 8472)
    public enum TokenBindingType : byte
    {
        Provided = 0x01,      // Token provided by client
        Referred = 0x02,      // Token referred by server
    }

    // Token Binding key parameters (RFC 8472 Section 3)
    public enum TokenBindingKeyParameters : byte
    {
        ECDSAP256 = 0x01,     // ECDSA P-256
        ECDSAP384 = 0x02,     // ECDSA P-384
        RSA2048PKCS15 = 0x03, // RSA 2048 PKCS#1 v1.5
        RSA2048PSS = 0x04,    // RSA 2048 PSS
    }

    /// <summary>
    /// Generates a Token Binding ID from a TLS connection.
    /// </summary>
    /// <param name="tlsExporter">TLS keying material exporter.</param>
    /// <param name="keyParameters">Key parameters to use.</param>
    /// <returns>Token Binding ID (EKM - Exported Keying Material).</returns>
    public static byte[] GenerateTokenBindingId(
        Func<string, byte[], int, byte[]> tlsExporter,
        TokenBindingKeyParameters keyParameters = TokenBindingKeyParameters.ECDSAP256)
    {
        // EKM = TLS-Exporter("EXPORTER-Token-Binding", "", 32)
        // Per RFC 8472 Section 4.1
        var ekmLabel = "EXPORTER-Token-Binding";
        var context = Array.Empty<byte>();
        var length = 32; // 256 bits for P-256

        return tlsExporter(ekmLabel, context, length);
    }

    /// <summary>
    /// Creates a Token Binding message for the client to send.
    /// </summary>
    public static byte[] CreateTokenBindingMessage(
        TokenBindingType type,
        byte[] tokenBindingId,
        byte[] signature,
        TokenBindingKeyParameters keyParameters = TokenBindingKeyParameters.ECDSAP256)
    {
        var writer = new ArrayBufferWriter<byte>();

        // Token Binding message structure (RFC 8472 Section 3.1)
        // struct {
        //   TokenBindingType type;
        //   uint16 length;
        //   opaque token_binding_id<0..2^16-1>;
        //   opaque signature<0..2^16-1>;
        // } TokenBindingMessage;

writer.Write(new byte[] { (byte)type });
        
        // Length = token_binding_id.length + signature.length + 2 (for key_parameters)
        var totalLength = (ushort)(tokenBindingId.Length + signature.Length + 1);
        var totalLengthBytes = BitConverter.GetBytes(totalLength);
        Array.Reverse(totalLengthBytes);
        writer.Write(totalLengthBytes);
        
        // Key parameters
        writer.Write(new byte[] { (byte)keyParameters });
        
        // Token Binding ID
        var tbidLengthBytes = BitConverter.GetBytes((ushort)tokenBindingId.Length);
        Array.Reverse(tbidLengthBytes);
        writer.Write(tbidLengthBytes);
        writer.Write(tokenBindingId);
        
        // Signature
        var sigLengthBytes = BitConverter.GetBytes((ushort)signature.Length);
        Array.Reverse(sigLengthBytes);
        writer.Write(sigLengthBytes);
        writer.Write(signature);
        
        return writer.WrittenMemory.ToArray();
    }

    /// <summary>
    /// Verifies a Token Binding message from the client.
    /// </summary>
    public static bool VerifyTokenBindingMessage(
        byte[] message,
        byte[] tokenBindingId,
        Func<byte[], byte[], bool> verifySignature,
        out TokenBindingType type,
        out TokenBindingKeyParameters keyParameters)
    {
        type = TokenBindingType.Provided;
        keyParameters = TokenBindingKeyParameters.ECDSAP256;

        try
        {
            var reader = new SpanReader(message);

            // Read type
            type = (TokenBindingType)reader.ReadByte();

            // Read length
            var length = reader.ReadUInt16BigEndian();

            // Read key parameters
            keyParameters = (TokenBindingKeyParameters)reader.ReadByte();

            // Read token binding ID
            var tbidLength = reader.ReadUInt16BigEndian();
            var readTokenBindingId = reader.ReadBytes(tbidLength);

            // Verify token binding ID matches
            if (!readTokenBindingId.AsSpan().SequenceEqual(tokenBindingId))
            {
                return false;
            }

            // Read signature
            var sigLength = reader.ReadUInt16BigEndian();
            var signature = reader.ReadBytes(sigLength);

            // Verify signature
            return verifySignature(tokenBindingId, signature);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Signs a token binding using the client's private key.
    /// </summary>
    public static byte[] SignTokenBinding(
        byte[] tokenBindingId,
        ECDsa privateKey,
        HashAlgorithmName hashAlgorithm = default)
    {
        if (hashAlgorithm == default)
            hashAlgorithm = HashAlgorithmName.SHA256;

        // Data to sign = token_binding_id
        return privateKey.SignData(tokenBindingId, hashAlgorithm);
    }

    /// <summary>
    /// Generates an ECDSA P-256 key pair for Token Binding.
    /// </summary>
    public static ECDsa GenerateTokenBindingKey()
    {
        return ECDsa.Create(ECCurve.NamedCurves.nistP256);
    }

    /// <summary>
    /// Exports the public key in uncompressed format for Token Binding.
    /// </summary>
    public static byte[] ExportPublicKey(ECDsa key)
    {
        var parameters = key.ExportParameters(false);
        var publicKey = new byte[1 + parameters.Q.X!.Length + parameters.Q.Y!.Length];
        publicKey[0] = 0x04; // Uncompressed point format
        parameters.Q.X.CopyTo(publicKey, 1);
        parameters.Q.Y.CopyTo(publicKey, 1 + parameters.Q.X.Length);
        return publicKey;
    }

    /// <summary>
    /// Imports a public key from uncompressed format.
    /// </summary>
    public static ECDsa ImportPublicKey(byte[] publicKey)
    {
        if (publicKey.Length < 1 || publicKey[0] != 0x04)
            throw new ArgumentException("Invalid public key format");

        var coordLength = (publicKey.Length - 1) / 2;
        var x = new byte[coordLength];
        var y = new byte[coordLength];
        
        Array.Copy(publicKey, 1, x, 0, coordLength);
        Array.Copy(publicKey, 1 + coordLength, y, 0, coordLength);

        var parameters = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = x, Y = y }
        };

        var key = ECDsa.Create();
        key.ImportParameters(parameters);
        return key;
    }

    // Span reader helper
    internal ref struct SpanReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _position;

        public SpanReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _position = 0;
        }

        public byte ReadByte()
        {
            if (_position >= _data.Length)
                throw new InvalidDataException("Unexpected end of data");
            return _data[_position++];
        }

        public ushort ReadUInt16BigEndian()
        {
            if (_position + 2 > _data.Length)
                throw new InvalidDataException("Not enough data for uint16");
            var value = (ushort)((_data[_position] << 8) | _data[_position + 1]);
            _position += 2;
            return value;
        }

        public byte[] ReadBytes(int length)
        {
            if (_position + length > _data.Length)
                throw new InvalidDataException("Not enough data");
            var result = _data.Slice(_position, length).ToArray();
            _position += length;
            return result;
        }
    }
}

/// <summary>
/// Token Binding context for managing token bindings per connection.
/// </summary>
public sealed class TokenBindingContext
{
    private readonly Dictionary<string, TokenBindingInfo> _bindings = new();
    private readonly object _lock = new();

    /// <summary>
    /// Stores a token binding for a connection.
    /// </summary>
    public void StoreBinding(string connectionId, TokenBindingInfo info)
    {
        lock (_lock)
        {
            _bindings[connectionId] = info;
        }
    }

    /// <summary>
    /// Retrieves a token binding for a connection.
    /// </summary>
    public bool TryGetBinding(string connectionId, out TokenBindingInfo info)
    {
        lock (_lock)
        {
            return _bindings.TryGetValue(connectionId, out info);
        }
    }

    /// <summary>
    /// Removes a token binding for a connection.
    /// </summary>
    public bool RemoveBinding(string connectionId)
    {
        lock (_lock)
        {
            return _bindings.Remove(connectionId);
        }
    }

    /// <summary>
    /// Clears all bindings.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _bindings.Clear();
        }
    }
}

/// <summary>
/// Information about a token binding.
/// </summary>
public sealed class TokenBindingInfo
{
    public byte[] TokenBindingId { get; init; }
    public TokenBinding.TokenBindingType Type { get; init; }
    public TokenBinding.TokenBindingKeyParameters KeyParameters { get; init; }
    public byte[] PublicKey { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; init; }
    public bool IsValid => DateTime.UtcNow < ExpiresAt;
}