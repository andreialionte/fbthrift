namespace FBThrift;

/// <summary>
/// Represents a streaming response with an initial response followed by a stream of elements.
/// </summary>
/// <typeparam name="TResponse">The type of the initial response.</typeparam>
/// <typeparam name="TElement">The type of stream elements.</typeparam>
public sealed class ResponseAndStream<TResponse, TElement>
{
    /// <summary>
    /// Gets the initial response.
    /// </summary>
    public TResponse Response { get; init; } = default!;

    /// <summary>
    /// Gets the async stream of elements.
    /// </summary>
    public IAsyncEnumerable<TElement> Stream { get; init; } = AsyncEnumerable.Empty<TElement>();
}

/// <summary>
/// Represents a sink (client-streaming) response pattern.
/// </summary>
/// <typeparam name="TRequest">The type of request elements from client.</typeparam>
/// <typeparam name="TResponse">The type of the final response.</typeparam>
public sealed class SinkClient<TRequest, TResponse>
{
    /// <summary>
    /// Gets the channel to send requests to.
    /// </summary>
    public IAsyncEnumerable<TRequest> Requests { get; init; } = AsyncEnumerable.Empty<TRequest>();

    /// <summary>
    /// Gets the task that completes when the call finishes with a response.
    /// </summary>
    public Task<TResponse> ResponseTask { get; init; } = Task.FromResult<TResponse>(default!)!;
}

/// <summary>
/// Represents a bidirectional streaming pattern.
/// </summary>
/// <typeparam name="TRequest">The type of request elements.</typeparam>
/// <typeparam name="TResponse">The type of response elements.</typeparam>
public sealed class BiDiStream<TRequest, TResponse>
{
    /// <summary>
    /// Gets the request stream from client.
    /// </summary>
    public IAsyncEnumerable<TRequest> Requests { get; init; } = AsyncEnumerable.Empty<TRequest>();

    /// <summary>
    /// Gets the response stream to client.
    /// </summary>
    public IAsyncEnumerable<TResponse> Responses { get; init; } = AsyncEnumerable.Empty<TResponse>();
}

/// <summary>
    /// Post-Quantum TLS configuration options.
    /// </summary>
    public sealed class PostQuantumTlsOptions
    {
        /// <summary>
        /// Gets or sets whether to enable Post-Quantum TLS (PQ-TLS) with X25519_MLKEM768 (0x11EC).
        /// When enabled, the X25519_MLKEM768 key exchange is added to the top of the supported_groups list.
        /// </summary>
        public bool EnablePostQuantumTls { get; set; } = false;

        /// <summary>
        /// Gets or sets the Post-Quantum KEM algorithm to use.
        /// Default is X25519_MLKEM768 (0x11EC).
        /// OpenSSL 3.5+ native hybrid KEMs:
        /// - X25519MLKEM768 (0x11EC) - default
        /// - SecP256r1MLKEM768 (0x11EE) - ECDSA P-256 + ML-KEM-768
        /// - SecP384r1MLKEM1024 (0x11EF) - ECDSA P-384 + ML-KEM-1024
        /// Pure ML-KEM:
        /// - MLKEM512 (0x0200), MLKEM768 (0x0201), MLKEM1024 (0x0202)
        /// </summary>
        public ushort PostQuantumKemAlgorithm { get; set; } = 0x11EC; // X25519_MLKEM768

        /// <summary>
        /// Gets or sets whether to require Post-Quantum key exchange (fail if not negotiated).
        /// When false (default), falls back to classical curves if peer doesn't support PQ.
        /// </summary>
        public bool RequirePostQuantum { get; set; } = false;

        /// <summary>
        /// Gets or sets whether to require client certificate for mTLS.
        /// When true, the server will require a client certificate during handshake.
        /// </summary>
        public bool RequireClientCertificate { get; set; } = false;

        /// <summary>
        /// Gets or sets the maximum CRYPTO frame buffer size for handshake messages.
        /// Increase this for Post-Quantum TLS which has larger ClientHello messages (~1.5KB+).
        /// Default is 16KB.
        /// </summary>
        public int MaxCryptoFrameBufferSize { get; set; } = 16384;

        /// <summary>
        /// Gets or sets whether to enable hybrid key exchange (classical + PQ).
        /// When true, both classical and PQ key exchanges are negotiated.
        /// </summary>
        public bool EnableHybridKeyExchange { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to enable PQ-mTLS (Post-Quantum mutual TLS) with ML-DSA certificate signatures.
        /// When true, requires certificates signed with ML-DSA algorithms.
        /// </summary>
        public bool EnablePostQuantumMtls { get; set; } = false;

        /// <summary>
        /// Gets or sets the ML-DSA algorithm to use for certificate signatures in PQ-mTLS.
        /// Default is ML-DSA-65 (0x0204).
        /// </summary>
        public ushort MlDsaAlgorithm { get; set; } = 0x0204; // ML-DSA-65
    }

    /// <summary>
    /// Predefined Post-Quantum KEM algorithm identifiers matching OpenSSL 3.5+ / oqs-provider / s2n-tls / AWS-LC.
    /// </summary>
    public static class PostQuantumKemAlgorithms
    {
        // IANA TLS Supported Groups (standardized hybrid KEMs)
        // Reference: https://github.com/aws/s2n-tls (s2n-tls KEM preferences)
        // Reference: https://github.com/open-quantum-safe/oqs-provider (OpenSSL provider)
        // Reference: https://github.com/aws/aws-lc (AWS libcrypto)
        // Reference: AWS KMS production deployment
        // 
        // IANA-Standardized Hybrid Groups for TLS 1.3:
        // | KEM Group              | IANA ID | ECC Curve   | PQ KEM      | Status      |
        // |------------------------|---------|-------------|-------------|-------------|
        // | secp256r1_mlkem768     | 0x11EB  | secp256r1   | ML-KEM-768  | IETF standard |
        // | x25519_mlkem768        | 0x11EC  | X25519      | ML-KEM-768  | IETF standard |
        // | secp384r1_mlkem1024    | 0x11ED  | secp384r1   | ML-KEM-1024 | IETF standard |
        // | mlkem1024              | 0x0202  | None        | ML-KEM-1024 | IETF standard (standalone) |
        // | mlkem768               | 0x0201  | None        | ML-KEM-768  | IETF standard (standalone) |
        // | mlkem512               | 0x0200  | None        | ML-KEM-512  | IETF standard (standalone) |
        
        // IANA-Standardized Hybrid Groups (TLS 1.3)
        public const ushort SecP256r1MLKEM768 = 0x11EB;   // secp256r1_mlkem768 (ECDSA P-256 + ML-KEM-768)
        public const ushort X25519MLKEM768 = 0x11EC;      // x25519_mlkem768 (X25519 + ML-KEM-768) - DEFAULT
        public const ushort SecP384r1MLKEM1024 = 0x11ED;  // secp384r1_mlkem1024 (ECDSA P-384 + ML-KEM-1024)
        
        // Pure ML-KEM (standalone)
        public const ushort MLKEM1024 = 0x0202;           // mlkem1024
        public const ushort MLKEM768 = 0x0201;            // mlkem768
        public const ushort MLKEM512 = 0x0200;            // mlkem512
        
        // OpenSSL 3.5+ naming (matches IANA but with different naming convention)
        public const ushort OpenSslX25519MLKEM768 = 0x11EC;      // X25519MLKEM768
        public const ushort OpenSslSecP256r1MLKEM768 = 0x11EE;   // SecP256r1MLKEM768
        public const ushort OpenSslSecP384r1MLKEM1024 = 0x11EF;  // SecP384r1MLKEM1024
        
        // oqs-provider additional hybrids (OpenSSL < 3.5)
        public const ushort X25519MLKEM512 = 0x0200; // x25519_mlkem512
        public const ushort P256MLKEM512 = 0x0200;   // p256_mlkem512
        public const ushort P384MLKEM768 = 0x0201;   // p384_mlkem768
        public const ushort P521MLKEM1024 = 0x0202;  // p521_mlkem1024
    }

    /// <summary>
    /// s2n-tls Security Policies for Post-Quantum TLS.
    /// Reference: https://github.com/aws/s2n-tls
    /// Reference: https://aws.github.io/s2n-tls/usage-guide/ch16-post-quantum.html
    /// Reference: AWS KMS production deployment
    /// </summary>
    public enum S2nTlsSecurityPolicy
    {
        /// <summary>
        /// default_pq / 20250721 - Latest IETF standardized hybrid KEMs
        /// KEMs: secp256r1_mlkem768 (0x11EB) → x25519_mlkem768 (0x11EC) → secp384r1_mlkem1024 (0x11ED) → mlkem1024 (0x0202)
        /// Classical fallback: x25519, secp256r1, secp384r1
        /// </summary>
        DefaultPq = 0,
        
        /// <summary>
        /// CNSA Suite 2.0 - Pure PQ only, no classical fallback
        /// KEMs: mlkem1024 (0x0202) only
        /// For classified/TOP SECRET environments
        /// </summary>
        Cnsa2 = 1,
        
        /// <summary>
        /// CNSA 1.2 Interop - Transitional policy (cnsa_2 + rfc9151)
        /// KEMs: mlkem1024 + classical groups from rfc9151
        /// For transitioning from CNSA 1.0 to CNSA 2.0
        /// </summary>
        Cnsa12Interop = 2,
        
        /// <summary>
        /// 20250512 - Earlier PQ policy
        /// KEMs: x25519_mlkem768, secp256r1_mlkem768 (no secp384r1_mlkem1024, no mlkem1024)
        /// </summary>
        Policy20250512 = 3,
    }
    
    /// <summary>
    /// Extension methods for s2n-tls security policies.
    /// </summary>
    public static class S2nTlsSecurityPolicyExtensions
    {
        /// <summary>
        /// Gets the KEM preference list for the given security policy.
        /// Matches s2n-tls KEM preference lists.
        /// </summary>
        public static ushort[] GetKemPreferences(this S2nTlsSecurityPolicy policy)
        {
            return policy switch
            {
                S2nTlsSecurityPolicy.DefaultPq => new ushort[]
                {
                    0x11EB, // secp256r1_mlkem768 (highest priority)
                    0x11EC, // x25519_mlkem768
                    0x11ED, // secp384r1_mlkem1024
                    0x0202, // mlkem1024 (pure)
                },
                S2nTlsSecurityPolicy.Cnsa2 => new ushort[]
                {
                    0x0202, // mlkem1024 only (pure PQ)
                },
                S2nTlsSecurityPolicy.Cnsa12Interop => new ushort[]
                {
                    0x0202, // mlkem1024 (from cnsa_2)
                    // Classical groups from rfc9151 would be added by TLS implementation
                },
                S2nTlsSecurityPolicy.Policy20250512 => new ushort[]
                {
                    0x11EC, // x25519_mlkem768
                    0x11EB, // secp256r1_mlkem768
                    // Note: no secp384r1_mlkem1024, no mlkem1024
                },
                _ => throw new ArgumentOutOfRangeException(nameof(policy))
            };
        }
        
        /// <summary>
        /// Gets the classical fallback groups for the given security policy.
        /// </summary>
        public static ushort[] GetClassicalFallback(this S2nTlsSecurityPolicy policy)
        {
            return policy switch
            {
                S2nTlsSecurityPolicy.DefaultPq => new ushort[] { 0x001D, 0x0017, 0x0018 }, // x25519, secp256r1, secp384r1
                S2nTlsSecurityPolicy.Cnsa2 => Array.Empty<ushort>(), // No classical fallback for CNSA 2.0
                S2nTlsSecurityPolicy.Cnsa12Interop => new ushort[] { 0x0018, 0x0017, 0x0019 }, // secp384r1, secp256r1, secp521r1 (from rfc9151)
                S2nTlsSecurityPolicy.Policy20250512 => new ushort[] { 0x001D, 0x0017, 0x0018 }, // x25519, secp256r1, secp384r1
                _ => throw new ArgumentOutOfRangeException(nameof(policy))
            };
        }
    }

/// <summary>
/// Options for configuring Thrift client calls.
/// </summary>
public class RpcOptions
{
    /// <summary>
    /// Gets or sets additional headers to send with requests.
    /// </summary>
    public IDictionary<string, string> WriteHeaders { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets the I/O timeout in milliseconds (null = no timeout).
    /// </summary>
    public int? TimeoutMs { get; set; }

    /// <summary>
    /// Gets or sets additional state data.
    /// </summary>
    public IDictionary<string, object> State { get; set; } = new Dictionary<string, object>();

    /// <summary>
    /// Gets or sets Post-Quantum TLS configuration options.
    /// </summary>
    public PostQuantumTlsOptions PostQuantumTls { get; set; } = new();
}

/// <summary>
/// Builder for configuring Thrift client connections.
/// </summary>
public static class ThriftClientBuilder
{
    /// <summary>
    /// Sets a persistent header that will be sent with every request.
    /// </summary>
    public static Action<RpcOptions> WithPersistentHeader(string name, string value)
    {
        return opts => opts.WriteHeaders[name] = value;
    }

    /// <summary>
    /// Sets multiple persistent headers.
    /// </summary>
    public static Action<RpcOptions> WithPersistentHeaders(IDictionary<string, string> headers)
    {
        return opts => { foreach (var kv in headers) { opts.WriteHeaders[kv.Key] = kv.Value; } };
    }

    /// <summary>
    /// Sets an I/O timeout in milliseconds.
    /// </summary>
    public static Action<RpcOptions> WithIoTimeout(int timeoutMs)
    {
        return opts => opts.TimeoutMs = timeoutMs;
    }

    /// <summary>
    /// Enables Post-Quantum TLS with X25519_MLKEM768 (0x11EC).
    /// </summary>
    public static Action<RpcOptions> UsePostQuantumTls(
        bool enable = true,
        ushort kemAlgorithm = 0x11EC, // X25519_MLKEM768
        bool requirePostQuantum = false,
        bool enableHybrid = true,
        bool requireClientCertificate = false,
        bool enablePostQuantumMtls = false,
        ushort mlDsaAlgorithm = 0x0204) // ML-DSA-65
    {
        return opts =>
        {
            opts.PostQuantumTls.EnablePostQuantumTls = enable;
            opts.PostQuantumTls.PostQuantumKemAlgorithm = kemAlgorithm;
            opts.PostQuantumTls.RequirePostQuantum = requirePostQuantum;
            opts.PostQuantumTls.EnableHybridKeyExchange = enableHybrid;
            opts.PostQuantumTls.RequireClientCertificate = requireClientCertificate;
            opts.PostQuantumTls.EnablePostQuantumMtls = enablePostQuantumMtls;
            opts.PostQuantumTls.MlDsaAlgorithm = mlDsaAlgorithm;
        };
    }

    /// <summary>
    /// Enables Post-Quantum TLS with OpenSSL 3.5+ native X25519MLKEM768 (0x11EC).
    /// This is the default hybrid KEM in OpenSSL 3.5+ default provider.
    /// </summary>
    public static Action<RpcOptions> UseOpenSslNativePqTls(
        bool enable = true,
        OpenSslNativeKemAlgorithm kemAlgorithm = OpenSslNativeKemAlgorithm.X25519MLKEM768,
        bool requirePostQuantum = false,
        bool enableHybrid = true,
        bool requireClientCertificate = false,
        bool enablePostQuantumMtls = false,
        ushort mlDsaAlgorithm = 0x0204) // ML-DSA-65
    {
        return opts =>
        {
            opts.PostQuantumTls.EnablePostQuantumTls = enable;
            opts.PostQuantumTls.PostQuantumKemAlgorithm = (ushort)kemAlgorithm;
            opts.PostQuantumTls.RequirePostQuantum = requirePostQuantum;
            opts.PostQuantumTls.EnableHybridKeyExchange = enableHybrid;
            opts.PostQuantumTls.RequireClientCertificate = requireClientCertificate;
            opts.PostQuantumTls.EnablePostQuantumMtls = enablePostQuantumMtls;
            opts.PostQuantumTls.MlDsaAlgorithm = mlDsaAlgorithm;
        };
    }

    /// <summary>
    /// OpenSSL 3.5+ native hybrid KEM algorithms (enabled by default in default provider).
    /// Reference: https://github.com/open-quantum-safe/oqs-provider
    /// </summary>
    public enum OpenSslNativeKemAlgorithm : ushort
    {
        /// <summary>X25519 + ML-KEM-768 (default, most compatible)</summary>
        X25519MLKEM768 = 0x11EC,
        
        /// <summary>ECDSA P-256 + ML-KEM-768</summary>
        SecP256r1MLKEM768 = 0x11EE,
        
        /// <summary>ECDSA P-384 + ML-KEM-1024</summary>
        SecP384r1MLKEM1024 = 0x11EF,
    }

    /// <summary>
    /// Configures Post-Quantum TLS with custom options.
    /// </summary>
    public static Action<RpcOptions> ConfigurePostQuantumTls(Action<PostQuantumTlsOptions> configure)
    {
        return opts => configure(opts.PostQuantumTls);
    }
}