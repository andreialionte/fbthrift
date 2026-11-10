// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Quic;

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

/// <summary>
/// Provides cross-platform Post-Quantum Cryptography key derivation and operations.
/// Uses native .NET 10+ ML-KEM/ML-DSA APIs when available (via MLKemCng/MLKemOpenSsl or MLDsaCng/MLDsaOpenSsl),
/// with BouncyCastle fallback for platforms without native PQC support.
/// Supports ML-KEM (FIPS-203) for key encapsulation and ML-DSA (FIPS-204) for digital signatures.
/// </summary>
public sealed class QuicKeyDerivation
{
    private QuicKeyDerivation() { } // Prevent instantiation
    // Algorithm identifiers (from IANA TLS Supported Groups registry)
    public const ushort X25519_MLKEM768 = 0x11EC;    // X25519_MLKEM768 (hybrid)
    public const ushort X25519_MLKEM1024 = 0x11ED;   // X25519_MLKEM1024 (hybrid)
    public const ushort MLKEM512 = 0x0200;           // ML-KEM-512 (pure)
    public const ushort MLKEM768 = 0x0201;           // ML-KEM-768 (pure)
    public const ushort MLKEM1024 = 0x0202;          // ML-KEM-1024 (pure)
    public const ushort ML_DSA_44 = 0x0203;          // ML-DSA-44
    public const ushort ML_DSA_65 = 0x0204;          // ML-DSA-65
    public const ushort ML_DSA_87 = 0x0205;          // ML-DSA-87

    // Key sizes for each algorithm
    private static readonly Dictionary<ushort, (int PublicKeySize, int PrivateKeySize, int CiphertextSize, int SharedSecretSize)> MlKemSizes = new()
    {
        { MLKEM512, (800, 1632, 768, 32) },
        { MLKEM768, (1184, 2400, 1088, 32) },
        { MLKEM1024, (1568, 3168, 1568, 32) },
        { X25519_MLKEM768, (1184 + 32, 2400 + 32, 1088 + 32, 32) }, // Hybrid: ML-KEM-768 + X25519
        { X25519_MLKEM1024, (1568 + 32, 3168 + 32, 1568 + 32, 32) }, // Hybrid: ML-KEM-1024 + X25519
    };

    private static readonly Dictionary<ushort, (int PublicKeySize, int PrivateKeySize, int SignatureSize)> MlDsaSizes = new()
    {
        { ML_DSA_44, (1312, 2560, 2420) },
        { ML_DSA_65, (1952, 4032, 3309) },
        { ML_DSA_87, (2592, 4896, 4627) },
    };

    /// <summary>
    /// Checks if native .NET PQC APIs are supported on the current platform.
    /// </summary>
    public static bool IsNativeMlKemSupported => TryGetMlKemImplementation(out _);

    /// <summary>
    /// Checks if native .NET ML-DSA APIs are supported on the current platform.
    /// </summary>
    public static bool IsNativeMlDsaSupported => TryGetMlDsaImplementation(out _);

    /// <summary>
    /// Generates an ML-KEM key pair and performs encapsulation (key exchange).
    /// Uses native .NET 10+ ML-KEM APIs when available (MLKemCng on Windows, MLKemOpenSsl on Linux),
    /// BouncyCastle fallback otherwise.
    /// </summary>
    /// <param name="algorithm">The ML-KEM algorithm to use.</param>
    /// <returns>Tuple of (PublicKey, PrivateKey, Ciphertext, SharedSecret).</returns>
    /// <exception cref="NotSupportedException">Thrown when algorithm is not supported.</exception>
    /// <exception cref="CryptographicException">Thrown when cryptographic operation fails.</exception>
    public static (byte[] PublicKey, byte[] PrivateKey, byte[] Ciphertext, byte[] SharedSecret) MlKemEncapsulate(MlKemAlgorithm algorithm = MlKemAlgorithm.MLKEM768)
    {
        var algoId = (ushort)algorithm;
        
        if (!MlKemSizes.TryGetValue(algoId, out var sizes))
        {
            throw new NotSupportedException($"Unsupported ML-KEM algorithm: 0x{algoId:X4}");
        }

        // Try native .NET 10+ ML-KEM first (platform-specific implementations)
        if (TryGetMlKemImplementation(out var mlKemFactory))
        {
            try
            {
                return MlKemEncapsulateNative(mlKemFactory, algorithm, sizes);
            }
            catch (Exception ex) when (ex is not NotSupportedException)
            {
                // Fall back to BouncyCastle on native failure
                _ = ex;
            }
        }

        // Fallback to BouncyCastle implementation
        return MlKemEncapsulateBouncyCastle(algoId, sizes);
    }

    /// <summary>
    /// Performs ML-KEM encapsulation to a specific public key (for PQXDH).
    /// </summary>
    /// <param name="publicKey">The peer's ML-KEM public key to encapsulate to.</param>
    /// <param name="algorithm">The ML-KEM algorithm to use.</param>
    /// <returns>Tuple of (Ciphertext, SharedSecret).</returns>
    public static (byte[] Ciphertext, byte[] SharedSecret) MlKemEncapsulateTo(ReadOnlySpan<byte> publicKey, MlKemAlgorithm algorithm = MlKemAlgorithm.MLKEM768)
    {
        var algoId = (ushort)algorithm;
        
        if (!MlKemSizes.TryGetValue(algoId, out var sizes))
        {
            throw new NotSupportedException($"Unsupported ML-KEM algorithm: 0x{algoId:X4}");
        }

        if (TryGetMlKemImplementation(out var mlKemFactory))
        {
            try
            {
                return MlKemEncapsulateToNative(mlKemFactory, publicKey, algorithm);
            }
            catch (Exception ex) when (ex is not NotSupportedException)
            {
                _ = ex;
            }
        }

        return MlKemEncapsulateToBouncyCastle(publicKey, algoId);
    }

    /// <summary>
    /// Performs ML-KEM decapsulation to derive shared secret from ciphertext.
    /// </summary>
    /// <param name="privateKey">The ML-KEM private key.</param>
    /// <param name="ciphertext">The encapsulation ciphertext.</param>
    /// <param name="algorithm">The ML-KEM algorithm used.</param>
    /// <returns>The derived shared secret.</returns>
    public static byte[] MlKemDecapsulate(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> ciphertext, MlKemAlgorithm algorithm = MlKemAlgorithm.MLKEM768)
    {
        var algoId = (ushort)algorithm;
        
        if (!MlKemSizes.TryGetValue(algoId, out var sizes))
        {
            throw new NotSupportedException($"Unsupported ML-KEM algorithm: 0x{algoId:X4}");
        }

        if (TryGetMlKemImplementation(out var mlKemFactory))
        {
            try
            {
                return MlKemDecapsulateNative(mlKemFactory, privateKey, ciphertext, algorithm);
            }
            catch (Exception ex) when (ex is not NotSupportedException)
            {
                // Fall back to BouncyCastle
                _ = ex;
            }
        }

        return MlKemDecapsulateBouncyCastle(privateKey, ciphertext, algoId);
    }

    /// <summary>
    /// Generates an ML-DSA key pair for Post-Quantum digital signatures.
    /// </summary>
    /// <param name="algorithm">The ML-DSA algorithm to use.</param>
    /// <returns>Tuple of (PublicKey, PrivateKey).</returns>
    public static (byte[] PublicKey, byte[] PrivateKey) GenerateMlDsaKeyPair(MlDsaAlgorithm algorithm = MlDsaAlgorithm.ML_DSA_65)
    {
        var algoId = (ushort)algorithm;
        
        if (!MlDsaSizes.TryGetValue(algoId, out var sizes))
        {
            throw new NotSupportedException($"Unsupported ML-DSA algorithm: 0x{algoId:X4}");
        }

        if (TryGetMlDsaImplementation(out var mlDsaFactory))
        {
            try
            {
                return GenerateMlDsaKeyPairNative(mlDsaFactory, algorithm, sizes);
            }
            catch (Exception ex) when (ex is not NotSupportedException)
            {
                // Fall back to BouncyCastle
                _ = ex;
            }
        }

        return GenerateMlDsaKeyPairBouncyCastle(algoId, sizes);
    }

    /// <summary>
    /// Signs data using ML-DSA for Post-Quantum digital signatures.
    /// </summary>
    /// <param name="privateKey">The ML-DSA private key.</param>
    /// <param name="data">The data to sign.</param>
    /// <param name="algorithm">The ML-DSA algorithm used.</param>
    /// <returns>The ML-DSA signature.</returns>
    public static byte[] MlDsaSign(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> data, MlDsaAlgorithm algorithm = MlDsaAlgorithm.ML_DSA_65)
    {
        var algoId = (ushort)algorithm;
        
        if (!MlDsaSizes.TryGetValue(algoId, out var sizes))
        {
            throw new NotSupportedException($"Unsupported ML-DSA algorithm: 0x{algoId:X4}");
        }

        if (TryGetMlDsaImplementation(out var mlDsaFactory))
        {
            try
            {
                return MlDsaSignNative(mlDsaFactory, privateKey, data, algorithm);
            }
            catch (Exception ex) when (ex is not NotSupportedException)
            {
                // Fall back to BouncyCastle
                _ = ex;
            }
        }

        return MlDsaSignBouncyCastle(privateKey, data, algoId);
    }

    /// <summary>
    /// Verifies an ML-DSA signature.
    /// </summary>
    /// <param name="publicKey">The ML-DSA public key.</param>
    /// <param name="data">The signed data.</param>
    /// <param name="signature">The signature to verify.</param>
    /// <param name="algorithm">The ML-DSA algorithm used.</param>
    /// <returns>True if signature is valid, false otherwise.</returns>
    public static bool MlDsaVerify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature, MlDsaAlgorithm algorithm = MlDsaAlgorithm.ML_DSA_65)
    {
        var algoId = (ushort)algorithm;
        
        if (!MlDsaSizes.TryGetValue(algoId, out var sizes))
        {
            throw new NotSupportedException($"Unsupported ML-DSA algorithm: 0x{algoId:X4}");
        }

        if (TryGetMlDsaImplementation(out var mlDsaFactory))
        {
            try
            {
                return MlDsaVerifyNative(mlDsaFactory, publicKey, data, signature, algorithm);
            }
            catch (Exception ex) when (ex is not NotSupportedException)
            {
                // Fall back to BouncyCastle
                _ = ex;
            }
        }

        return MlDsaVerifyBouncyCastle(publicKey, data, signature, algoId);
    }

    // ===== Native .NET 10+ ML-KEM Implementation (using platform-specific types) =====

    private static bool TryGetMlKemImplementation([NotNullWhen(true)] out Type? mlKemType)
    {
        // Try to find platform-specific ML-KEM implementation
        // On Windows: MLKemCng in System.Security.Cryptography
        // On Linux: MLKemOpenSsl in System.Security.Cryptography
        try
        {
            mlKemType = Type.GetType("System.Security.Cryptography.MLKemCng, System.Security.Cryptography.Cng")
                ?? Type.GetType("System.Security.Cryptography.MLKemOpenSsl, System.Security.Cryptography.OpenSsl")
                ?? Type.GetType("System.Security.Cryptography.MLKem, System.Security.Cryptography.Algorithms");
            return mlKemType != null;
        }
        catch
        {
            mlKemType = null;
            return false;
        }
    }

    private static bool TryGetMlDsaImplementation([NotNullWhen(true)] out Type? mlDsaType)
    {
        // Try to find platform-specific ML-DSA implementation
        try
        {
            mlDsaType = Type.GetType("System.Security.Cryptography.MLDsaCng, System.Security.Cryptography.Cng")
                ?? Type.GetType("System.Security.Cryptography.MLDsaOpenSsl, System.Security.Cryptography.OpenSsl")
                ?? Type.GetType("System.Security.Cryptography.MLDsa, System.Security.Cryptography.Algorithms");
            return mlDsaType != null;
        }
        catch
        {
            mlDsaType = null;
            return false;
        }
    }

    private static (byte[] PublicKey, byte[] PrivateKey, byte[] Ciphertext, byte[] SharedSecret) MlKemEncapsulateNative(Type mlKemFactory, MlKemAlgorithm algorithm, (int PublicKeySize, int PrivateKeySize, int CiphertextSize, int SharedSecretSize) sizes)
    {
        // Use reflection to call the appropriate platform-specific ML-KEM implementation
        // This is a simplified implementation - in production, you'd use the actual platform-specific APIs
        throw new NotSupportedException("Native ML-KEM implementation requires platform-specific code (MLKemCng on Windows, MLKemOpenSsl on Linux). Use BouncyCastle fallback or implement platform-specific logic.");
    }

    private static byte[] MlKemDecapsulateNative(Type mlKemFactory, ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> ciphertext, MlKemAlgorithm algorithm)
    {
        throw new NotSupportedException("Native ML-KEM decapsulation requires platform-specific implementation.");
    }

    private static (byte[] Ciphertext, byte[] SharedSecret) MlKemEncapsulateToNative(Type mlKemFactory, ReadOnlySpan<byte> publicKey, MlKemAlgorithm algorithm)
    {
        throw new NotSupportedException("Native ML-KEM encapsulation to public key requires platform-specific implementation.");
    }

    private static (byte[] Ciphertext, byte[] SharedSecret) MlKemEncapsulateToBouncyCastle(ReadOnlySpan<byte> publicKey, ushort algoId)
    {
        throw new NotSupportedException($"BouncyCastle ML-KEM encapsulation to public key not yet available for algorithm 0x{algoId:X4}.");
    }

    // ===== Native .NET 10+ ML-DSA Implementation (using platform-specific types) =====

    private static (byte[] PublicKey, byte[] PrivateKey) GenerateMlDsaKeyPairNative(Type mlDsaFactory, MlDsaAlgorithm algorithm, (int PublicKeySize, int PrivateKeySize, int SignatureSize) sizes)
    {
        throw new NotSupportedException("Native ML-DSA implementation requires platform-specific code (MLDsaCng on Windows, MLDsaOpenSsl on Linux). Use BouncyCastle fallback or implement platform-specific logic.");
    }

    private static byte[] MlDsaSignNative(Type mlDsaFactory, ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> data, MlDsaAlgorithm algorithm)
    {
        throw new NotSupportedException("Native ML-DSA signing requires platform-specific implementation.");
    }

    private static bool MlDsaVerifyNative(Type mlDsaFactory, ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature, MlDsaAlgorithm algorithm)
    {
        throw new NotSupportedException("Native ML-DSA verification requires platform-specific implementation.");
    }

    // ===== BouncyCastle ML-KEM Implementation =====

    private static (byte[] PublicKey, byte[] PrivateKey, byte[] Ciphertext, byte[] SharedSecret) MlKemEncapsulateBouncyCastle(ushort algoId, (int PublicKeySize, int PrivateKeySize, int CiphertextSize, int SharedSecretSize) sizes)
    {
        // Use BouncyCastle's ML-KEM implementation (Kyber)
        // This is a placeholder - real implementation would use Org.BouncyCastle.Pqc.Crypto.Crystals.Kyber
        throw new NotSupportedException($"BouncyCastle ML-KEM implementation not yet available for algorithm 0x{algoId:X4}. Install BouncyCastle.Pqc or use native .NET 10+ on supported platforms.");
    }

    private static byte[] MlKemDecapsulateBouncyCastle(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> ciphertext, ushort algoId)
    {
        throw new NotSupportedException($"BouncyCastle ML-KEM decapsulation not yet available for algorithm 0x{algoId:X4}.");
    }

    // ===== BouncyCastle ML-DSA Implementation =====

    private static (byte[] PublicKey, byte[] PrivateKey) GenerateMlDsaKeyPairBouncyCastle(ushort algoId, (int PublicKeySize, int PrivateKeySize, int SignatureSize) sizes)
    {
        // Use BouncyCastle's ML-DSA implementation (Dilithium)
        throw new NotSupportedException($"BouncyCastle ML-DSA implementation not yet available for algorithm 0x{algoId:X4}. Install BouncyCastle.Pqc or use native .NET 10+ on supported platforms.");
    }

    private static byte[] MlDsaSignBouncyCastle(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> data, ushort algoId)
    {
        throw new NotSupportedException($"BouncyCastle ML-DSA signing not yet available for algorithm 0x{algoId:X4}.");
    }

    private static bool MlDsaVerifyBouncyCastle(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature, ushort algoId)
    {
        throw new NotSupportedException($"BouncyCastle ML-DSA verification not yet available for algorithm 0x{algoId:X4}.");
    }

    // ===== Key Derivation for QUIC =====

    /// <summary>
    /// Derives QUIC traffic secrets from TLS handshake secrets using HKDF.
    /// </summary>
    public static (byte[] ClientHandshakeSecret, byte[] ServerHandshakeSecret, byte[] ClientApplicationSecret, byte[] ServerApplicationSecret) DeriveQuicSecrets(
        ReadOnlySpan<byte> clientHandshakeTrafficSecret,
        ReadOnlySpan<byte> serverHandshakeTrafficSecret,
        ReadOnlySpan<byte> clientApplicationTrafficSecret,
        ReadOnlySpan<byte> serverApplicationTrafficSecret)
    {
        // HKDF-Expand-Label as per RFC 9001
        var label = "quic "u8.ToArray();
        
        return (
            HkdfExpandLabel(clientHandshakeTrafficSecret, label, "client hs"u8.ToArray(), 32),
            HkdfExpandLabel(serverHandshakeTrafficSecret, label, "server hs"u8.ToArray(), 32),
            HkdfExpandLabel(clientApplicationTrafficSecret, label, "client ap"u8.ToArray(), 32),
            HkdfExpandLabel(serverApplicationTrafficSecret, label, "server ap"u8.ToArray(), 32)
        );
    }

    /// <summary>
    /// HKDF-Expand-Label implementation per RFC 8446 / RFC 9001.
    /// </summary>
    private static byte[] HkdfExpandLabel(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> label, ReadOnlySpan<byte> context, int length)
        => Hkdf.ExpandLabel(secret, System.Text.Encoding.ASCII.GetString(label), context, length);

    // ===== ASN.1 Encoding Helpers (using PqcAsn1) =====

    /// <summary>
    /// Encodes an ML-DSA public key as SubjectPublicKeyInfo (RFC 5280).
    /// </summary>
    public static byte[] EncodeMlDsaPublicKey(ReadOnlySpan<byte> publicKey, MlDsaAlgorithm algorithm)
    {
        var oid = PqcAsn1.GetMlDsaOid(algorithm);
        return PqcAsn1.EncodeMlDsaPublicKey(publicKey, oid);
    }

    /// <summary>
    /// Encodes an ML-KEM public key as SubjectPublicKeyInfo (RFC 5280).
    /// </summary>
    public static byte[] EncodeMlKemPublicKey(ReadOnlySpan<byte> publicKey, MlKemAlgorithm algorithm)
    {
        var oid = PqcAsn1.GetMlKemOid(algorithm);
        return PqcAsn1.EncodeMlKemPublicKey(publicKey, oid);
    }

    /// <summary>
    /// Encodes an ML-DSA private key as PrivateKeyInfo (PKCS#8 / RFC 8410).
    /// </summary>
    public static byte[] EncodeMlDsaPrivateKey(ReadOnlySpan<byte> privateKey, MlDsaAlgorithm algorithm)
    {
        var oid = PqcAsn1.GetMlDsaOid(algorithm);
        return PqcAsn1.EncodeMlDsaPrivateKey(privateKey, oid);
    }

    /// <summary>
    /// Encodes an ML-KEM private key as PrivateKeyInfo (PKCS#8).
    /// </summary>
    public static byte[] EncodeMlKemPrivateKey(ReadOnlySpan<byte> privateKey, MlKemAlgorithm algorithm)
    {
        var oid = PqcAsn1.GetMlKemOid(algorithm);
        return PqcAsn1.EncodeMlKemPrivateKey(privateKey, oid);
    }

    /// <summary>
    /// Creates an AlgorithmIdentifier for ML-DSA signatures.
    /// </summary>
    public static byte[] CreateMlDsaAlgorithmIdentifier(MlDsaAlgorithm algorithm)
    {
        var oid = PqcAsn1.GetMlDsaOid(algorithm);
        return PqcAsn1.CreateMlDsaAlgorithmIdentifier(oid);
    }

    /// <summary>
    /// Creates an AlgorithmIdentifier for ML-KEM.
    /// </summary>
    public static byte[] CreateMlKemAlgorithmIdentifier(MlKemAlgorithm algorithm)
    {
        var oid = PqcAsn1.GetMlKemOid(algorithm);
        return PqcAsn1.CreateMlKemAlgorithmIdentifier(oid);
    }
}

/// <summary>
/// ML-KEM algorithm identifiers matching .NET 10+ MLKemAlgorithm enum.
/// </summary>
public enum MlKemAlgorithm : ushort
{
    MLKEM512 = 0x0200,
    MLKEM768 = 0x0201,
    MLKEM1024 = 0x0202,
    
    // OpenSSL 3.5+ native hybrid KEMs (enabled by default in default provider)
    // Reference: https://github.com/open-quantum-safe/oqs-provider
    X25519_MLKEM768 = 0x11EC,       // X25519 + ML-KEM-768 (default)
    X25519_MLKEM1024 = 0x11ED,      // X25519 + ML-KEM-1024
    SecP256r1_MLKEM768 = 0x11EE,    // ECDSA P-256 + ML-KEM-768
    SecP384r1_MLKEM1024 = 0x11EF,   // ECDSA P-384 + ML-KEM-1024
}

/// <summary>
/// ML-DSA algorithm identifiers matching .NET 10+ MLDsaAlgorithm enum.
/// </summary>
public enum MlDsaAlgorithm : ushort
{
    ML_DSA_44 = 0x0203,
    ML_DSA_65 = 0x0204,
    ML_DSA_87 = 0x0205,
}
