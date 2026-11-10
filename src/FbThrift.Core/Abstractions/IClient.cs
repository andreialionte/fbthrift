// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Core.Abstractions;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Interface for serializing/deserializing Thrift objects.
/// </summary>
public interface ISerializer : IAsyncDisposable
{
    /// <summary>
    /// Serializes an object to a byte array.
    /// </summary>
    byte[] Serialize<T>(T value) where T : class, IThriftSerializable;

    /// <summary>
    /// Serializes an object to a stream.
    /// </summary>
    ValueTask SerializeAsync<T>(Stream stream, T value, CancellationToken cancellationToken = default) where T : class, IThriftSerializable;

    /// <summary>
    /// Deserializes an object from a byte array.
    /// </summary>
    T Deserialize<T>(byte[] data) where T : class, IThriftSerializable, new();

    /// <summary>
    /// Deserializes an object from a stream.
    /// </summary>
    ValueTask<T> DeserializeAsync<T>(Stream stream, CancellationToken cancellationToken = default) where T : class, IThriftSerializable, new();

    /// <summary>
    /// Gets the serializer name/identifier.
    /// </summary>
    string Name { get; }
}

/// <summary>
/// Factory for creating serializer instances.
/// </summary>
public interface ISerializerFactory
{
    /// <summary>
    /// Creates a serializer for the specified format.
    /// </summary>
    ISerializer Create(SerializationFormat format);

    /// <summary>
    /// Creates a serializer by name.
    /// </summary>
    ISerializer Create(string name);

    /// <summary>
    /// Gets all supported serialization formats.
    /// </summary>
    IReadOnlyList<SerializationFormat> SupportedFormats { get; }
}

/// <summary>
/// Supported serialization formats.
/// </summary>
public enum SerializationFormat
{
    Binary,
    Compact,
    Json,
    SimpleJson,
    Terse,
    Custom
}

/// <summary>
/// Interface for a Thrift client that can make RPC calls.
/// </summary>
public interface IThriftClient : IAsyncDisposable
{
    /// <summary>
    /// Makes a request-response call.
    /// </summary>
    ValueTask<TResponse> CallAsync<TRequest, TResponse>(
        string methodName,
        TRequest request,
        RpcOptions? options = null,
        CancellationToken cancellationToken = default)
        where TRequest : class, IThriftSerializable
        where TResponse : class, IThriftSerializable, new();

    /// <summary>
    /// Makes a one-way (fire-and-forget) call.
    /// </summary>
    ValueTask CallOneWayAsync<TRequest>(
        string methodName,
        TRequest request,
        RpcOptions? options = null,
        CancellationToken cancellationToken = default)
        where TRequest : class, IThriftSerializable;

    /// <summary>
    /// Makes a streaming request (client sends stream, gets single response).
    /// </summary>
    ValueTask<TResponse> CallStreamAsync<TRequest, TResponse>(
        string methodName,
        IAsyncEnumerable<TRequest> requests,
        RpcOptions? options = null,
        CancellationToken cancellationToken = default)
        where TRequest : class, IThriftSerializable
        where TResponse : class, IThriftSerializable, new();

    /// <summary>
    /// Makes a server-streaming call (client sends request, gets stream of responses).
    /// </summary>
    IAsyncEnumerable<TResponse> CallServerStreamAsync<TRequest, TResponse>(
        string methodName,
        TRequest request,
        RpcOptions? options = null,
        CancellationToken cancellationToken = default)
        where TRequest : class, IThriftSerializable
        where TResponse : class, IThriftSerializable, new();

    /// <summary>
    /// Makes a bidirectional streaming call.
    /// </summary>
    IAsyncEnumerable<TResponse> CallBidiStreamAsync<TRequest, TResponse>(
        string methodName,
        IAsyncEnumerable<TRequest> requests,
        RpcOptions? options = null,
        CancellationToken cancellationToken = default)
        where TRequest : class, IThriftSerializable
        where TResponse : class, IThriftSerializable, new();
}

/// <summary>
/// Options for RPC calls.
/// </summary>
public sealed class RpcOptions
{
    /// <summary>
    /// Request timeout.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Custom headers to send with the request.
    /// </summary>
    public Dictionary<string, string> Headers { get; set; } = new();

    /// <summary>
    /// Custom write headers (sent only on request).
    /// </summary>
    public Dictionary<string, string> WriteHeaders { get; set; } = new();

    /// <summary>
    /// Compression algorithm for this call.
    /// </summary>
    public CompressionAlgorithm Compression { get; set; } = CompressionAlgorithm.None;

    /// <summary>
    /// Checksum algorithm for this call.
    /// </summary>
    public ChecksumAlgorithm Checksum { get; set; } = ChecksumAlgorithm.None;

    /// <summary>
    /// Priority of this call (higher = more important).
    /// </summary>
    public int Priority { get; set; } = 0;

    /// <summary>
    /// Whether this call is idempotent (safe to retry).
    /// </summary>
    public bool IsIdempotent { get; set; } = false;

    /// <summary>
    /// Custom properties for this call.
    /// </summary>
    public Dictionary<string, object> Properties { get; set; } = new();
}

/// <summary>
/// Compression algorithms supported by Thrift.
/// </summary>
public enum CompressionAlgorithm : byte
{
    None = 0,
    Zlib = 1,
    Zstd = 2,
    Lz4 = 3,
    Snappy = 4
}

/// <summary>
/// Checksum algorithms supported by Thrift.
/// </summary>
public enum ChecksumAlgorithm : byte
{
    None = 0,
    Crc32 = 1,
    Xxh3_64 = 2,
    Xxh3_128 = 3
}

/// <summary>
/// Interface for a Thrift server that handles incoming requests.
/// </summary>
public interface IThriftServer : IAsyncDisposable
{
    /// <summary>
    /// Starts the server.
    /// </summary>
    ValueTask StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the server.
    /// </summary>
    ValueTask StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a service handler.
    /// </summary>
    void RegisterService<TService>(TService handler) where TService : class;

    /// <summary>
    /// Unregisters a service handler.
    /// </summary>
    bool UnregisterService<TService>() where TService : class;
}

/// <summary>
/// Options for configuring a Thrift server.
/// </summary>
public sealed class ServerOptions
{
    /// <summary>
    /// The endpoint to listen on.
    /// </summary>
    public Uri Endpoint { get; set; } = new Uri("tcp://localhost:9090");

    /// <summary>
    /// Maximum number of concurrent connections.
    /// </summary>
    public int MaxConnections { get; set; } = 10000;

    /// <summary>
    /// Maximum number of concurrent streams per connection.
    /// </summary>
    public int MaxConcurrentStreams { get; set; } = 100;

    /// <summary>
    /// Request timeout.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Enable TLS/SSL.
    /// </summary>
    public bool UseTls { get; set; } = false;

    /// <summary>
    /// Server certificate for TLS.
    /// </summary>
    public System.Security.Cryptography.X509Certificates.X509Certificate2? ServerCertificate { get; set; }

    /// <summary>
    /// Whether to require client certificates.
    /// </summary>
    public bool RequireClientCertificate { get; set; } = false;

    /// <summary>
    /// Custom server properties.
    /// </summary>
    public Dictionary<string, object> Properties { get; set; } = new();
}

/// <summary>
/// Builder for creating Thrift clients with fluent configuration.
/// </summary>
public interface IThriftClientBuilder
{
    /// <summary>
    /// Sets the transport to use.
    /// </summary>
    IThriftClientBuilder WithTransport(ITransport transport);

    /// <summary>
    /// Sets the transport factory to use.
    /// </summary>
    IThriftClientBuilder WithTransportFactory(ITransportFactory factory);

    /// <summary>
    /// Sets the protocol to use.
    /// </summary>
    IThriftClientBuilder WithProtocol(IProtocol protocol);

    /// <summary>
    /// Sets the protocol factory to use.
    /// </summary>
    IThriftClientBuilder WithProtocolFactory(IProtocolFactory factory);

    /// <summary>
    /// Sets the serializer to use.
    /// </summary>
    IThriftClientBuilder WithSerializer(ISerializer serializer);

    /// <summary>
    /// Sets the serializer factory to use.
    /// </summary>
    IThriftClientBuilder WithSerializerFactory(ISerializerFactory factory);

    /// <summary>
    /// Configures default RPC options.
    /// </summary>
    IThriftClientBuilder WithOptions(Action<RpcOptions> configure);

    /// <summary>
    /// Sets the endpoint to connect to.
    /// </summary>
    IThriftClientBuilder WithEndpoint(Uri endpoint);

    /// <summary>
    /// Builds the client.
    /// </summary>
    IThriftClient Build();
}

/// <summary>
/// Builder for creating Thrift servers with fluent configuration.
/// </summary>
public interface IThriftServerBuilder
{
    /// <summary>
    /// Sets the transport to use.
    /// </summary>
    IThriftServerBuilder WithTransport(IServerTransport transport);

    /// <summary>
    /// Sets the transport factory to use.
    /// </summary>
    IThriftServerBuilder WithTransportFactory(ITransportFactory factory);

    /// <summary>
    /// Sets the protocol factory to use.
    /// </summary>
    IThriftServerBuilder WithProtocolFactory(IProtocolFactory factory);

    /// <summary>
    /// Configures server options.
    /// </summary>
    IThriftServerBuilder WithOptions(Action<ServerOptions> configure);

    /// <summary>
    /// Sets the endpoint to listen on.
    /// </summary>
    IThriftServerBuilder WithEndpoint(Uri endpoint);

    /// <summary>
    /// Registers a service handler.
    /// </summary>
    IThriftServerBuilder WithService<TService>(TService handler) where TService : class;

    /// <summary>
    /// Builds the server.
    /// </summary>
    IThriftServer Build();
}