// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Http2;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using FbThrift.Core.Header;
using FbThrift.Core.Protocol;
using FbThrift.Core.Transport;

/// <summary>
/// HTTP/2 transport for THeader frames.
/// Implements THeader over HTTP/2 (h2) as used by fbthrift.
/// Based on RFC 7540 and fbthrift's HTTP/2 transport.
/// </summary>
public sealed class Http2Transport : IFbThriftTransport, IAsyncDisposable
{
    private readonly HttpClient _httpClient;
    private readonly Uri _serverUri;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="Http2Transport"/> class.
    /// </summary>
    public Http2Transport(
        Uri serverUri,
        X509Certificate2? clientCertificate = null,
        HttpClientHandler? customHandler = null)
    {
        _serverUri = serverUri ?? throw new ArgumentNullException(nameof(serverUri));
        
        var handler = customHandler ?? new HttpClientHandler
        {
            ClientCertificateOptions = ClientCertificateOption.Manual,
            SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
            ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true // For testing
        };

        if (clientCertificate != null)
        {
            handler.ClientCertificates.Add(clientCertificate);
        }

        _httpClient = new HttpClient(handler)
        {
            DefaultRequestVersion = new Version(2, 0),
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
    }

    /// <inheritdoc/>
    public async ValueTask<ReadOnlyMemory<byte>> SendRequestAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (body.IsEmpty)
        {
            throw new ArgumentException("Request body must not be empty.", nameof(body));
        }

        await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        
        try
        {
            // THeader frames are sent as HTTP/2 DATA frames
            using var content = new ReadOnlyMemoryContent(body);
            content.Headers.Add("Content-Type", "application/x-thrift");
            
            var request = new HttpRequestMessage(HttpMethod.Post, _serverUri)
            {
                Content = content,
                Version = new Version(2, 0),
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };

            // Add THeader-specific headers
            request.Headers.Add("x-thrift-protocol", "compact");
            
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            
            response.EnsureSuccessStatusCode();
            
            var responseBody = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            
            return responseBody;
        }
        finally
        {
            _requestLock.Release();
        }
    }

    /// <summary>
    /// Sends a THeader frame with proper framing.
    /// </summary>
    public async ValueTask<THeaderFrame> SendRequestAsync(THeaderFrame frame, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Encode THeader frame
        var frameData = THeaderFrame.Encode(frame);
        
        var responseData = await SendRequestAsync(frameData, cancellationToken).ConfigureAwait(false);
        
        // Decode response
        return THeaderFrame.Decode(responseData);
    }

    /// <summary>
    /// Starts the transport (no-op for HTTP/2 client).
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        
        _requestLock.Dispose();
        _httpClient.Dispose();
        
        await Task.CompletedTask;
    }

    /// <summary>
    /// HTTP content wrapper for ReadOnlyMemory.
    /// </summary>
    private sealed class ReadOnlyMemoryContent : HttpContent
    {
        private readonly ReadOnlyMemory<byte> _content;

        public ReadOnlyMemoryContent(ReadOnlyMemory<byte> content)
        {
            _content = content;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(_content).ConfigureAwait(false);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _content.Length;
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }
    }
}
