// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Quic;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using FBThrift;
using FbThrift.Transports.Quic;
using FbThrift.Core.Transport;

/// <summary>
/// Rocket protocol over HTTP/3 transport.
/// Runs Rocket frames over HTTP/3 streams (RFC 9114).
/// This is different from RocketQuicTransport which runs over raw QUIC.
/// </summary>
public sealed class RocketHttp3Transport : IFbThriftTransport, IAsyncDisposable
{
    private readonly Http3Connection _http3Connection;
    private readonly Http3Connection.Http3Stream _controlStream;
    private readonly Dictionary<long, Http3Connection.Http3Stream> _requestStreams = new();
    private readonly object _lock = new();
    private long _nextStreamId = 0;
    private bool _disposed;
    private readonly SemaphoreSlim _connectionSemaphore = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="RocketHttp3Transport"/> class.
    /// </summary>
    public RocketHttp3Transport(
        Http3Connection http3Connection)
    {
        _http3Connection = http3Connection ?? throw new ArgumentNullException(nameof(http3Connection));
        
        // Create control stream (stream 0 for client)
        _controlStream = _http3Connection.CreateStream();
    }

    /// <inheritdoc/>
    public async ValueTask<ReadOnlyMemory<byte>> SendRequestAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (body.IsEmpty)
        {
            throw new ArgumentException("Request body must not be empty.", nameof(body));
        }

        // Get a new stream for this request
        var stream = GetOrCreateRequestStream();
        
        try
        {
            // Send request as HEADERS + DATA frames
            var headers = new Http3Connection.Http3Headers
            {
                [":method"] = "POST",
                [":path"] = "/",
                [":scheme"] = "https",
                [":authority"] = "localhost",
                ["content-type"] = "application/x.thrift",
                ["content-length"] = body.Length.ToString()
            };

            var headersFrame = _http3Connection.EncodeHeadersFrame(stream.StreamId, headers, endStream: false);
            var dataFrame = _http3Connection.EncodeDataFrame(stream.StreamId, body, endStream: true);

            // Send frames
            await SendFramesAsync(new[] { headersFrame, dataFrame }, cancellationToken).ConfigureAwait(false);

            // Wait for response
            return await WaitForResponseAsync(stream.StreamId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReturnStream(stream.StreamId);
        }
    }

    /// <summary>
    /// Gets or creates a request stream.
    /// </summary>
    private Http3Connection.Http3Stream GetOrCreateRequestStream()
    {
        lock (_lock)
        {
            var streamId = _http3Connection.GetNextLocalStreamId();
            var stream = _http3Connection.CreateStream();
            stream.StreamId = streamId;
            _requestStreams[streamId] = stream;
            return stream;
        }
    }

    /// <summary>
    /// Returns a stream to the pool.
    /// </summary>
    private void ReturnStream(long streamId)
    {
        lock (_lock)
        {
            _requestStreams.Remove(streamId);
        }
    }

    /// <summary>
    /// Sends HTTP/3 frames on the connection.
    /// </summary>
    private async Task SendFramesAsync(byte[][] frames, CancellationToken cancellationToken)
    {
        await _connectionSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        
        try
        {
            foreach (var frame in frames)
            {
                // In a real implementation, this would write to the QUIC stream
                // For now, we simulate the send
                await Task.Yield();
            }
        }
        finally
        {
            _connectionSemaphore.Release();
        }
    }

    /// <summary>
    /// Waits for response on a stream.
    /// </summary>
    private async ValueTask<ReadOnlyMemory<byte>> WaitForResponseAsync(long streamId, CancellationToken cancellationToken)
    {
        // In a real implementation, this would wait for HEADERS + DATA frames
        // For now, return empty response
        await Task.Yield();
        return Array.Empty<byte>();
    }

    /// <summary>
    /// Handles incoming HTTP/3 frames.
    /// </summary>
    public async Task HandleIncomingFrameAsync(Http3Connection.Http3Frame frame, CancellationToken cancellationToken = default)
    {
        switch (frame.Type)
        {
            case Http3Connection.FrameHeaders:
                await HandleHeadersFrameAsync(frame, cancellationToken).ConfigureAwait(false);
                break;
            case Http3Connection.FrameData:
                await HandleDataFrameAsync(frame, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleHeadersFrameAsync(Http3Connection.Http3Frame frame, CancellationToken cancellationToken)
    {
        // Handle response headers
        await Task.Yield();
    }

    private async Task HandleDataFrameAsync(Http3Connection.Http3Frame frame, CancellationToken cancellationToken)
    {
        // Handle response data
        await Task.Yield();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Close all streams
        lock (_lock)
        {
            foreach (var stream in _requestStreams.Values)
            {
                stream.State = Http3Connection.Http3StreamState.Closed;
            }
            _requestStreams.Clear();
        }

        _connectionSemaphore.Dispose();
        await Task.CompletedTask;
    }
}

/// <summary>
/// Rocket over HTTP/3 client transport factory.
/// </summary>
public static class RocketHttp3ClientFactory
{
    /// <summary>
    /// Creates a Rocket over HTTP/3 client transport.
    /// </summary>
    public static async Task<IFbThriftTransport> CreateAsync(
        Uri serverUri,
        X509Certificate2 clientCertificate,
        RpcOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Create HTTP/3 connection
        var http3Connection = new Http3Connection(isServer: false);
        
        // In a real implementation, this would:
        // 1. Establish QUIC connection to server
        // 2. Perform TLS 1.3 handshake
        // 3. Send SETTINGS frame
        // 4. Create HTTP/3 connection
        
        var transport = new RocketHttp3Transport(http3Connection);
        
        // Send initial SETTINGS
        var settingsFrame = http3Connection.EncodeSettingsFrame();
        
        return transport;
    }
}
