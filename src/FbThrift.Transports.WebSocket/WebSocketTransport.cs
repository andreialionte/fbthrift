// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.WebSocket;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FbThrift.Core.Header;
using FbThrift.Core.Transport;

/// <summary>
/// WebSocket transport for THeader frames.
/// Implements THeader over WebSocket as used by fbthrift for web clients.
/// Based on RFC 6455 and fbthrift's WebSocket transport.
/// Uses System.Threading.Channels for request/response multiplexing with backpressure.
/// </summary>
public sealed class WebSocketTransport : IFbThriftTransport, IAsyncDisposable
{
    private readonly ClientWebSocket _webSocket;
    private readonly Uri _serverUri;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<PendingRequest> _pendingChannel;
    private readonly Task _receiveTask;
    private int _nextRequestId = 0;
    private bool _disposed;

    /// <summary>
    /// Represents a pending request with its response channel.
    /// </summary>
    private sealed class PendingRequest
    {
        public required int RequestId { get; init; }
        public required TaskCompletionSource<ReadOnlyMemory<byte>> Tcs { get; init; }
        public required CancellationTokenSource CancellationTokenSource { get; init; }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="WebSocketTransport"/> class.
    /// </summary>
    public WebSocketTransport(
        Uri serverUri,
        ClientWebSocket? webSocket = null,
        X509Certificate2? clientCertificate = null,
        CancellationToken cancellationToken = default)
    {
        _serverUri = serverUri ?? throw new ArgumentNullException(nameof(serverUri));
        _webSocket = webSocket ?? new ClientWebSocket();
        
        // Configure for THeader
        _webSocket.Options.SetRequestHeader("Sec-WebSocket-Protocol", "thrift");
        
        if (clientCertificate != null)
        {
            _webSocket.Options.ClientCertificates.Add(clientCertificate);
        }

        // Bounded channel with backpressure
        _pendingChannel = Channel.CreateBounded<PendingRequest>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = true
        });
        
        _receiveTask = ReceiveLoopAsync(_cts.Token);
    }

    /// <summary>
    /// Connects to the WebSocket server.
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _webSocket.ConnectAsync(_serverUri, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<ReadOnlyMemory<byte>> SendRequestAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (body.IsEmpty)
        {
            throw new ArgumentException("Request body must not be empty.", nameof(body));
        }

        if (_webSocket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("WebSocket is not connected.");
        }

        var requestId = Interlocked.Increment(ref _nextRequestId);
        var tcs = new TaskCompletionSource<ReadOnlyMemory<byte>>(TaskCreationOptions.RunContinuationsAsynchronously);
        
        // Linked cancellation with timeout
        var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        requestCts.CancelAfter(TimeSpan.FromSeconds(30)); // Default timeout

        var pending = new PendingRequest
        {
            RequestId = requestId,
            Tcs = tcs,
            CancellationTokenSource = requestCts
        };

        // Write to channel (applies backpressure if channel is full)
        await _pendingChannel.Writer.WriteAsync(pending, cancellationToken).ConfigureAwait(false);

        try
        {
            // Send THeader frame with request ID
            var frame = new THeaderFrame
            {
                FrameSize = (uint)body.Length,
                Flags = 0,
                SeqId = (uint)requestId,
                ProtoId = ProtocolId.Compact,
                Payload = body
            };

            var frameData = THeaderFrame.Encode(frame);
            
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _webSocket.SendAsync(frameData, WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }

            return await tcs.Task.WaitAsync(requestCts.Token).ConfigureAwait(false);
        }
        finally
        {
            requestCts.Dispose();
            // Note: We don't remove from channel here - the read loop will consume it
            // If we need to cancel, the cancellation token on the TCS will handle it
        }
    }

    /// <summary>
    /// Main receive loop for WebSocket messages.
    /// </summary>
    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[65536];
        
        try
        {
            // Process pending requests from channel
            await foreach (var pending in _pendingChannel.Reader.ReadAllAsync(cancellationToken))
            {
                // Wait for frame with matching requestId
                var payload = await ReadFrameWithRequestIdAsync(pending.RequestId, pending.CancellationTokenSource.Token).ConfigureAwait(false);
                
                pending.CancellationTokenSource.Dispose();
                pending.Tcs.TrySetResult(payload);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected
        }
        catch (WebSocketException)
        {
            // Connection closed
        }
        finally
        {
            // Complete any remaining pending requests
            while (_pendingChannel.Reader.TryRead(out var pending))
            {
                pending.CancellationTokenSource.Dispose();
                pending.Tcs.TrySetCanceled(cancellationToken);
            }
            
            _pendingChannel.Writer.Complete();
        }
    }

    /// <summary>
    /// Reads frames until one with the matching requestId is found.
    /// </summary>
    private async ValueTask<ReadOnlyMemory<byte>> ReadFrameWithRequestIdAsync(int targetRequestId, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            var result = await _webSocket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by client", CancellationToken.None).ConfigureAwait(false);
                throw new FbThriftException("WebSocket connection closed by server.");
            }

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                var data = buffer.AsMemory(0, result.Count);
                
                // Decode THeader frame
                var frame = THeaderFrame.Decode(data);
                
                if (frame.SeqId == targetRequestId)
                {
                    return frame.Payload;
                }
                
                // Frame doesn't match - this shouldn't happen in well-behaved server
                // but we continue waiting for the correct frame
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        
        try
        {
            if (_webSocket.State == WebSocketState.Open)
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disposing", CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch
        {
            // Ignore
        }

        try
        {
            await _receiveTask.ConfigureAwait(false);
        }
        catch
        {
            // Ignore
        }

        _sendLock.Dispose();
        _cts.Dispose();
        _webSocket.Dispose();
    }
}

/// <summary>
/// WebSocket server transport for THeader frames.
/// </summary>
public sealed class WebSocketServerTransport : IFbThriftTransport, IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> _requestHandler;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _serverTask;
    private bool _disposed;

    public WebSocketServerTransport(
        string prefix,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> requestHandler)
    {
        _requestHandler = requestHandler ?? throw new ArgumentNullException(nameof(requestHandler));
        _listener = new HttpListener();
        _listener.Prefixes.Add(prefix);

        _serverTask = RunServerAsync(_cts.Token);
    }

    private async Task RunServerAsync(CancellationToken cancellationToken)
    {
        _listener.Start();
        
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync().ConfigureAwait(false);
                
                if (context.Request.IsWebSocketRequest)
                {
                    _ = Task.Run(() => HandleWebSocketAsync(context, cancellationToken), cancellationToken);
                }
                else
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                }
            }
            catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task HandleWebSocketAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        WebSocketContext wsContext = null;
        
        try
        {
            wsContext = await context.AcceptWebSocketAsync(subProtocol: "thrift").ConfigureAwait(false);
            var webSocket = wsContext.WebSocket;
            var buffer = new byte[65536];

            while (!cancellationToken.IsCancellationRequested && webSocket.State == WebSocketState.Open)
            {
                var result = await webSocket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by server", CancellationToken.None).ConfigureAwait(false);
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    var data = buffer.AsMemory(0, result.Count);
                    
                    // Decode THeader
                    var frame = THeaderFrame.Decode(data);
                    
                    // Process request
                    var responseData = await _requestHandler(frame.Payload, cancellationToken).ConfigureAwait(false);
                    
                    // Encode response
                    var responseFrame = new THeaderFrame
                    {
                        FrameSize = (uint)responseData.Length,
                        Flags = 0,
                        SeqId = frame.SeqId,
                        ProtoId = frame.ProtoId,
                        Payload = responseData
                    };
                    
                    var responseBytes = THeaderFrame.Encode(responseFrame);
                    await webSocket.SendAsync(responseBytes, WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception)
        {
            // Log error
        }
        finally
        {
            wsContext?.WebSocket.Dispose();
        }
    }

    /// <inheritdoc/>
    public async ValueTask<ReadOnlyMemory<byte>> SendRequestAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Server transport cannot initiate requests.");
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        
        _cts.Cancel();
        _listener.Stop();
        _listener.Close();
        
        try
        {
            await _serverTask.ConfigureAwait(false);
        }
        catch
        {
            // Ignore
        }
        
        _cts.Dispose();
    }
}
