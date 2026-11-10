// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using FBThrift;
using FbThrift.Core.Http2;
using FbThrift.Transports.Rocket;
using FbThrift.Core.WebSocket;

namespace FbThrift.Transports.Rocket;

/// <summary>
/// Factory for creating Thrift transports (TCP and QUIC).
/// </summary>
public sealed class FbThriftTransportFactory
{
    /// <summary>
    /// Creates a TCP socket transport.
    /// </summary>
    public static IFbThriftTransport CreateSocketTransport(Socket socket)
    {
        return new SocketFbThriftTransport(socket);
    }

    /// <summary>
    /// Creates a QUIC transport server.
    /// </summary>
    public static async Task<IFbThriftTransport> CreateQuicTransportAsync(
        IPEndPoint endpoint,
        X509Certificate2 certificate,
        RpcOptions? options = null,
        int maxConnections = 1000,
        int maxInboundStreams = 100,
        CancellationToken cancellationToken = default)
    {
        var transport = new RocketQuicTransport(endpoint, certificate, options, maxConnections, maxInboundStreams);
        await Task.Yield(); // Ensure async context
        return transport;
    }

    /// <summary>
    /// Creates a QUIC client transport.
    /// </summary>
    public static async Task<IFbThriftTransport> CreateQuicClientAsync(
        IPEndPoint endpoint,
        X509Certificate2 clientCertificate,
        RpcOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var client = await RocketQuicClientFactory.CreateAsync(endpoint, clientCertificate, options, cancellationToken);
        return client;
    }

    /// <summary>
    /// Creates an HTTP/2 transport for THeader frames.
    /// </summary>
    public static IFbThriftTransport CreateHttp2Transport(
        Uri serverUri,
        X509Certificate2? clientCertificate = null)
    {
        return new Http2Transport(serverUri, clientCertificate);
    }

    /// <summary>
    /// Creates an HTTP/2 server transport for THeader frames.
    /// </summary>
    public static IFbThriftTransport CreateHttp2ServerTransport(
        string prefix,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> requestHandler,
        X509Certificate2? serverCertificate = null)
    {
        return new Http2ServerTransport(prefix, requestHandler, serverCertificate);
    }

    /// <summary>
    /// Creates a Rocket over HTTP/3 client transport.
    /// </summary>
    public static async Task<IFbThriftTransport> CreateRocketHttp3ClientAsync(
        Uri serverUri,
        X509Certificate2 clientCertificate,
        RpcOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return await RocketHttp3ClientFactory.CreateAsync(serverUri, clientCertificate, options, cancellationToken);
    }

    /// <summary>
    /// Creates a WebSocket transport for THeader frames.
    /// </summary>
    public static async Task<IFbThriftTransport> CreateWebSocketTransportAsync(
        Uri serverUri,
        X509Certificate2? clientCertificate = null,
        CancellationToken cancellationToken = default)
    {
        var transport = new WebSocketTransport(serverUri, clientCertificate: clientCertificate);
        await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
        return transport;
    }

    /// <summary>
    /// Creates a WebSocket server transport for THeader frames.
    /// </summary>
    public static IFbThriftTransport CreateWebSocketServerTransport(
        string prefix,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> requestHandler)
    {
        return new WebSocketServerTransport(prefix, requestHandler);
    }
}
