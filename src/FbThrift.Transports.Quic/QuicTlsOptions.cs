// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Quic;

using System;
using System.Collections.Generic;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// TLS configuration for QUIC connections.
/// Supports both native .NET TLS and platform-specific implementations.
/// </summary>
public sealed class QuicTlsOptions
{
    /// <summary>
    /// Server certificate for TLS.
    /// </summary>
    public X509Certificate2? ServerCertificate { get; set; }
    
    /// <summary>
    /// Client certificate for mutual TLS.
    /// </summary>
    public X509Certificate2? ClientCertificate { get; set; }
    
    /// <summary>
    /// Client certificates collection for mTLS.
    /// </summary>
    public X509CertificateCollection? ClientCertificates { get; set; }
    
    /// <summary>
    /// Whether to validate the server certificate.
    /// </summary>
    public bool ValidateServerCertificate { get; set; } = true;
    
    /// <summary>
    /// Custom server certificate validation callback.
    /// </summary>
    public RemoteCertificateValidationCallback? ServerCertificateValidationCallback { get; set; }
    
    /// <summary>
    /// Client certificate selection callback.
    /// </summary>
    public LocalCertificateSelectionCallback? ClientCertificateSelectionCallback { get; set; }
    
    /// <summary>
    /// Minimum TLS version.
    /// </summary>
    public SslProtocols MinimumTlsVersion { get; set; } = SslProtocols.Tls13;
    
    /// <summary>
    /// Maximum TLS version.
    /// </summary>
    public SslProtocols MaximumTlsVersion { get; set; } = SslProtocols.Tls13;
    
    /// <summary>
    /// ALPN protocols to negotiate.
    /// </summary>
    public List<string> ApplicationProtocols { get; set; } = new() { "h3", "h3-29", "h3-27" };
    
    /// <summary>
    /// Enable 0-RTT.
    /// </summary>
    public bool EnableZeroRtt { get; set; } = true;
    
    /// <summary>
    /// Maximum early data size.
    /// </summary>
    public long MaxEarlyDataSize { get; set; } = 16384;
    
    /// <summary>
    /// Require client certificate for mTLS.
    /// </summary>
    public bool RequireClientCertificate { get; set; } = false;
    
    /// <summary>
    /// Enable post-quantum TLS (hybrid KEM).
    /// </summary>
    public bool EnablePostQuantumTls { get; set; } = false;
    
    /// <summary>
    /// Post-quantum KEM algorithm.
    /// </summary>
    public ushort PostQuantumKemAlgorithm { get; set; } = 0x11EC; // X25519_MLKEM768
    
    /// <summary>
    /// Require post-quantum key exchange.
    /// </summary>
    public bool RequirePostQuantum { get; set; } = false;
    
    /// <summary>
    /// Enable hybrid key exchange.
    /// </summary>
    public bool EnableHybridKeyExchange { get; set; } = true;
    
    /// <summary>
    /// Enable PQ-mTLS (Post-Quantum mutual TLS).
    /// </summary>
    public bool EnablePostQuantumMtls { get; set; } = false;
    
    /// <summary>
    /// ML-DSA algorithm for PQ-mTLS.
    /// </summary>
    public ushort MlDsaAlgorithm { get; set; } = 0x0204; // ML-DSA-65
    
    /// <summary>
    /// Maximum CRYPTO frame buffer size for handshake messages.
    /// </summary>
    public int MaxCryptoFrameBufferSize { get; set; } = 16384;
    
    /// <summary>
    /// Creates default options for client.
    /// </summary>
    public static QuicTlsOptions CreateClientDefaults()
    {
        return new QuicTlsOptions
        {
            ValidateServerCertificate = true,
            EnableZeroRtt = true,
            ApplicationProtocols = new List<string> { "h3", "h3-29", "h3-27" }
        };
    }
    
    /// <summary>
    /// Creates default options for server.
    /// </summary>
    public static QuicTlsOptions CreateServerDefaults(X509Certificate2 serverCertificate)
    {
        return new QuicTlsOptions
        {
            ServerCertificate = serverCertificate ?? throw new ArgumentNullException(nameof(serverCertificate)),
            ValidateServerCertificate = true,
            EnableZeroRtt = true,
            ApplicationProtocols = new List<string> { "h3", "h3-29", "h3-27" }
        };
    }
    
    /// <summary>
    /// Creates mTLS options for client.
    /// </summary>
    public static QuicTlsOptions CreateMtlsClientDefaults(X509Certificate2 clientCertificate)
    {
        return new QuicTlsOptions
        {
            ClientCertificate = clientCertificate ?? throw new ArgumentNullException(nameof(clientCertificate)),
            ValidateServerCertificate = true,
            RequireClientCertificate = true,
            EnableZeroRtt = true,
            ApplicationProtocols = new List<string> { "h3", "h3-29", "h3-27" }
        };
    }
    
    /// <summary>
    /// Creates mTLS options for server.
    /// </summary>
    public static QuicTlsOptions CreateMtlsServerDefaults(X509Certificate2 serverCertificate, X509CertificateCollection? clientCAs = null)
    {
        return new QuicTlsOptions
        {
            ServerCertificate = serverCertificate ?? throw new ArgumentNullException(nameof(serverCertificate)),
            ClientCertificates = clientCAs,
            ValidateServerCertificate = true,
            RequireClientCertificate = true,
            EnableZeroRtt = true,
            ApplicationProtocols = new List<string> { "h3", "h3-29", "h3-27" }
        };
    }
    
    /// <summary>
    /// Creates PQ-TLS options for client.
    /// </summary>
    public static QuicTlsOptions CreatePqTlsClientDefaults(X509Certificate2 clientCertificate, ushort kemAlgorithm = 0x11EC)
    {
        return new QuicTlsOptions
        {
            ClientCertificate = clientCertificate ?? throw new ArgumentNullException(nameof(clientCertificate)),
            ValidateServerCertificate = true,
            EnablePostQuantumTls = true,
            PostQuantumKemAlgorithm = kemAlgorithm,
            EnableHybridKeyExchange = true,
            RequirePostQuantum = false,
            EnableZeroRtt = true,
            ApplicationProtocols = new List<string> { "h3", "h3-29", "h3-27" }
        };
    }
    
    /// <summary>
    /// Creates PQ-TLS options for server.
    /// </summary>
    public static QuicTlsOptions CreatePqTlsServerDefaults(X509Certificate2 serverCertificate, ushort kemAlgorithm = 0x11EC)
    {
        return new QuicTlsOptions
        {
            ServerCertificate = serverCertificate ?? throw new ArgumentNullException(nameof(serverCertificate)),
            ValidateServerCertificate = true,
            EnablePostQuantumTls = true,
            PostQuantumKemAlgorithm = kemAlgorithm,
            EnableHybridKeyExchange = true,
            RequirePostQuantum = false,
            EnableZeroRtt = true,
            ApplicationProtocols = new List<string> { "h3", "h3-29", "h3-27" }
        };
    }
    
    /// <summary>
    /// Creates PQ-mTLS options for client.
    /// </summary>
    public static QuicTlsOptions CreatePqMtlsClientDefaults(X509Certificate2 clientCertificate, ushort mlDsaAlgorithm = 0x0204)
    {
        return new QuicTlsOptions
        {
            ClientCertificate = clientCertificate ?? throw new ArgumentNullException(nameof(clientCertificate)),
            ValidateServerCertificate = true,
            EnablePostQuantumTls = true,
            EnablePostQuantumMtls = true,
            MlDsaAlgorithm = mlDsaAlgorithm,
            RequirePostQuantum = false,
            EnableZeroRtt = true,
            ApplicationProtocols = new List<string> { "h3", "h3-29", "h3-27" }
        };
    }
    
    /// <summary>
    /// Creates PQ-mTLS options for server.
    /// </summary>
    public static QuicTlsOptions CreatePqMtlsServerDefaults(X509Certificate2 serverCertificate, X509CertificateCollection? clientCAs = null, ushort mlDsaAlgorithm = 0x0204)
    {
        return new QuicTlsOptions
        {
            ServerCertificate = serverCertificate ?? throw new ArgumentNullException(nameof(serverCertificate)),
            ClientCertificates = clientCAs,
            ValidateServerCertificate = true,
            EnablePostQuantumTls = true,
            EnablePostQuantumMtls = true,
            MlDsaAlgorithm = mlDsaAlgorithm,
            RequireClientCertificate = true,
            EnableZeroRtt = true,
            ApplicationProtocols = new List<string> { "h3", "h3-29", "h3-27" }
        };
    }
}
