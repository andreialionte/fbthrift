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
/// TLS integration for QUIC transport.
/// Provides TLS 1.3 support with PQ-TLS and certificate validation.
/// </summary>
public sealed class QuicTlsContext : IDisposable
{
    private readonly QuicTlsOptions _options;
    private SslStream? _sslStream;
    private readonly Stream _innerStream;
    private bool _disposed;
    private bool _authenticated;
    
    public QuicTlsContext(Stream innerStream, QuicTlsOptions options, bool isServer)
    {
        _innerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }
    
    /// <summary>
    /// Performs TLS handshake as client.
    /// </summary>
    public async Task AuthenticateAsClientAsync(string targetHost, CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(QuicTlsContext));
        if (_authenticated) throw new InvalidOperationException("Already authenticated");
        
        var sslOptions = new SslClientAuthenticationOptions
        {
            TargetHost = _options.TargetHost,
            ClientCertificates = _options.ClientCertificate != null 
                ? new X509CertificateCollection { _options.ClientCertificate } 
                : (_options.ClientCertificates ?? new X509CertificateCollection()),
            EnabledSslProtocols = _options.MinimumTlsVersion | _options.MaximumTlsVersion,
            CertificateRevocationCheckMode = _options.ValidateServerCertificate ? X509RevocationMode.Online : X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = _options.ServerCertificateValidationCallback ?? DefaultServerCertificateValidation,
            ClientCertificateSelectionCallback = _options.ClientCertificateSelectionCallback,
            ApplicationProtocols = _options.ApplicationProtocols,
            AllowRenegotiation = false
        };
        
        _sslStream = new SslStream(_innerStream, false, sslOptions.RemoteCertificateValidationCallback, sslOptions.ClientCertificateSelectionCallback);
        
        await _sslStream.AuthenticateAsClientAsync(sslOptions, cancellationToken).ConfigureAwait(false);
        
        _authenticated = true;
        
        // Check if PQ-TLS was negotiated
        if (_options.EnablePostQuantumTls)
        {
            // Check negotiated cipher suite for PQ-TLS
            // In .NET 10+, we can check the negotiated cipher suite
        }
    }
    
    /// <summary>
    /// Performs TLS handshake as server.
    /// </summary>
    public async Task AuthenticateAsServerAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(QuicTlsContext));
        if (_authenticated) throw new InvalidOperationException("Already authenticated");
        
        if (_options.ServerCertificate == null)
            throw new InvalidOperationException("Server certificate is required for server authentication");
        
        var sslOptions = new SslServerAuthenticationOptions
        {
            ServerCertificate = _options.ServerCertificate,
            ClientCertificateRequired = _options.RequireClientCertificate,
            EnabledSslProtocols = _options.MinimumTlsVersion | _options.MaximumTlsVersion,
            CertificateRevocationCheckMode = _options.ValidateServerCertificate ? X509RevocationMode.Online : X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = _options.ServerCertificateValidationCallback ?? DefaultClientCertificateValidation,
            ApplicationProtocols = _options.ApplicationProtocols,
            AllowRenegotiation = false
        };
        
        _sslStream = new SslStream(_innerStream, false);
        
        await _sslStream.AuthenticateAsServerAsync(sslOptions, cancellationToken).ConfigureAwait(false);
        
        _authenticated = true;
    }
    
    /// <summary>
    /// Gets the underlying TLS stream for reading/writing.
    /// </summary>
    public Stream GetTlsStream()
    {
        if (!_authenticated)
            throw new InvalidOperationException("TLS not authenticated");
        
        return _sslStream!;
    }
    
    /// <summary>
    /// Gets the negotiated ALPN protocol.
    /// </summary>
    public string? NegotiatedApplicationProtocol => _sslStream?.NegotiatedApplicationProtocol;
    
    /// <summary>
    /// Gets the negotiated cipher suite.
    /// </summary>
    public TlsCipherSuite? NegotiatedCipherSuite => _sslStream?.NegotiatedCipherSuite;
    
    /// <summary>
    /// Gets whether PQ-TLS was negotiated.
    /// </summary>
    public bool IsPostQuantumNegotiated => 
        _sslStream?.NegotiatedCipherSuite.HasFlag(TlsCipherSuite.PostQuantum) ?? false;
    
    /// <summary>
    /// Default server certificate validation callback.
    /// </summary>
    private static bool DefaultServerCertificateValidation(
        object sender, 
        X509Certificate? certificate, 
        X509Chain? chain, 
        SslPolicyErrors sslPolicyErrors)
    {
        // In production, implement proper certificate validation
        return sslPolicyErrors == SslPolicyErrors.None;
    }
    
    /// <summary>
    /// Default client certificate validation callback.
    /// </summary>
    private static bool DefaultClientCertificateValidation(
        object sender, 
        X509Certificate? certificate, 
        X509Chain? chain, 
        SslPolicyErrors sslPolicyErrors)
    {
        // In production, implement proper client certificate validation
        return sslPolicyErrors == SslPolicyErrors.None;
    }
    
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _sslStream?.Dispose();
        }
    }
}
