// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Rocket;

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FbThrift.Core.Abstractions;
using FbThrift.Core.FBThrift;

/// <summary>
/// Server-side RSocket transport that accepts incoming RSocket connections.
/// Listens on a TCP port, handles SETUP frames, and routes RequestStream/RequestChannel/RequestBidi
/// frames to registered Thrift processors.
/// Matches Meta fbthrift's <c>com.facebook.thrift.rsocket.server</c> package.
/// </summary>
public sealed class RocketServerTransport : IAsyncDisposable
{
    private readonly Socket _listenSocket;
    private readonly IProtocolFactory _protocolFactory;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> _requestHandler;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptTask;
    private readonly ConcurrentDictionary<int, RocketConnectionContext> _connections = new();
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of <see cref="RocketServerTransport"/>.
    /// </summary>
    /// <param name="endPoint">The local endpoint to listen on.</param>
    /// <param name="protocolFactory">The protocol factory for creating protocol instances.</param>
    /// <param name="requestHandler">Handler for processing incoming Thrift requests.</param>
    public RocketServerTransport(
        IPEndPoint endPoint,
        IProtocolFactory protocolFactory,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> requestHandler)
    {
        _listenSocket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        _protocolFactory = protocolFactory ?? throw new ArgumentNullException(nameof(protocolFactory));
        _requestHandler = requestHandler ?? throw new ArgumentNullException(nameof(requestHandler));

        _listenSocket.Bind(endPoint);
        _listenSocket.Listen(128);

        _acceptTask = Task.Run(AcceptLoopAsync, _cts.Token);
    }

    /// <summary>
    /// The local endpoint the server is listening on.
    /// </summary>
    public IPEndPoint LocalEndPoint => (IPEndPoint)_listenSocket.LocalEndPoint!;

    /// <summary>
    /// Gets the number of active connections.
    /// </summary>
    public int ActiveConnectionCount => _connections.Count;

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var clientSocket = await _listenSocket.AcceptAsync(_cts.Token).ConfigureAwait(false);

                if (clientSocket == null)
                    continue;

                var connectionId = Interlocked.Increment(ref _connectionCounter);
                var context = new RocketConnectionContext(
                    connectionId,
                    clientSocket,
                    _protocolFactory,
                    _requestHandler,
                    _cts.Token);

                _connections[connectionId] = context;

                _ = Task.Run(() => ProcessConnectionAsync(context), _cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
        catch (SocketException)
        {
            // Socket closed during shutdown
        }
        catch (Exception ex)
        {
            // Fault all connections
            foreach (var kvp in _connections)
            {
                kvp.Value.Tcs.TrySetException(ex);
            }
        }
    }

    private async Task ProcessConnectionAsync(RocketConnectionContext context)
    {
        try
        {
            // Read SETUP frame from client
            var setupFrame = await ReadFrameAsync(context, context.CancellationToken).ConfigureAwait(false);
            if (setupFrame == null || setupFrame.Header.Type != RocketFrameType.Setup)
            {
                // Invalid connection - close
                await context.Transport.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                return;
            }

            // Process frames until connection closes
            while (!_cts.Token.IsCancellationRequested && context.Transport.IsOpen)
            {
                var frame = await ReadFrameAsync(context, context.CancellationToken).ConfigureAwait(false);
                if (frame == null)
                    break;

                try
                {
                    await HandleFrameAsync(context, frame, context.CancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Send error frame back
                    await SendErrorFrameAsync(context, frame.Header.StreamId, RocketErrorCode.ApplicationError, ex.Message).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
        catch (IOException)
        {
            // Connection closed by client
        }
        finally
        {
            _connections.TryRemove(context.ConnectionId, out _);
            await context.Transport.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task HandleFrameAsync(RocketConnectionContext context, RocketFrame frame, CancellationToken ct)
    {
        switch (frame.Header.Type)
        {
            case RocketFrameType.RequestResponse:
                await HandleRequestResponseAsync(context, frame, ct).ConfigureAwait(false);
                break;

            case RocketFrameType.RequestStream:
                await HandleRequestStreamAsync(context, frame, ct).ConfigureAwait(false);
                break;

            case RocketFrameType.RequestChannel:
                await HandleRequestChannelAsync(context, frame, ct).ConfigureAwait(false);
                break;

            case RocketFrameType.RequestBidi:
                await HandleRequestBidiAsync(context, frame, ct).ConfigureAwait(false);
                break;

            case RocketFrameType.Cancel:
                // Cancel stream - no response needed
                break;

            case RocketFrameType.KeepAlive:
                await SendFrameAsync(context, new RocketFrame(
                    new RocketFrameHeader(0, RocketFrameType.KeepAlive, RocketFrameFlags.None, 0),
                    ReadOnlyMemory<byte>.Empty,
                    ReadOnlyMemory<byte>.Empty), ct).ConfigureAwait(false);
                break;

            case RocketFrameType.MetadataPush:
                // Metadata push - no response needed
                break;
        }
    }

    private async Task HandleRequestResponseAsync(RocketConnectionContext context, RocketFrame frame, CancellationToken ct)
    {
        var streamId = frame.Header.StreamId;
        var data = frame.Data;

        try
        {
            var response = await _requestHandler(data, ct).ConfigureAwait(false);

            await SendFrameAsync(context, new RocketFrame(
                new RocketFrameHeader((uint)response.Length, RocketFrameType.Payload, RocketFrameFlags.Complete, streamId),
                ReadOnlyMemory<byte>.Empty,
                response), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await SendErrorFrameAsync(context, streamId, RocketErrorCode.ApplicationError, ex.Message).ConfigureAwait(false);
        }
    }

    private async Task HandleRequestStreamAsync(RocketConnectionContext context, RocketFrame frame, CancellationToken ct)
    {
        // RequestStream: client requests a stream of responses
        // For now, handle as single response (can be extended for true streaming)
        await HandleRequestResponseAsync(context, frame, ct).ConfigureAwait(false);
    }

    private async Task HandleRequestChannelAsync(RocketConnectionContext context, RocketFrame frame, CancellationToken ct)
    {
        // RequestChannel: bidirectional streaming
        // For now, handle as single request-response
        await HandleRequestResponseAsync(context, frame, ct).ConfigureAwait(false);
    }

    private async Task HandleRequestBidiAsync(RocketConnectionContext context, RocketFrame frame, CancellationToken ct)
    {
        // RequestBidi: bidirectional streaming
        // For now, handle as single request-response
        await HandleRequestResponseAsync(context, frame, ct).ConfigureAwait(false);
    }

    private async ValueTask<RocketFrame?> ReadFrameAsync(RocketConnectionContext context, CancellationToken ct)
    {
        try
        {
            var headerBuffer = new byte[RocketFrameHeader.HeaderSize];
            var bytesRead = await context.Transport.ReceiveAsync(headerBuffer, ct).ConfigureAwait(false);
            if (bytesRead == 0)
                return null;

            var header = RocketFrameHeader.Read(new ReadOnlySpan<byte>(headerBuffer));

            var frameData = new byte[header.Length];
            if (header.Length > 0)
            {
                var dataRead = await context.Transport.ReceiveAsync(frameData, ct).ConfigureAwait(false);
                if (dataRead < header.Length)
                    return null; // Incomplete frame
            }

            var metadataLength = (header.Flags & RocketFrameFlags.Metadata) != 0 ? 4 : 0;
            var metadata = metadataLength > 0 ? frameData.AsMemory(0, metadataLength) : ReadOnlyMemory<byte>.Empty;
            var payload = metadataLength > 0 ? frameData.AsMemory(metadataLength) : frameData;

            return new RocketFrame(header, metadata, payload);
        }
        catch
        {
            return null;
        }
    }

    private async Task SendFrameAsync(RocketConnectionContext context, RocketFrame frame, CancellationToken ct)
    {
        var buffer = new ArrayBufferWriter<byte>();
        frame.Write(buffer);
        await context.Transport.SendAsync(buffer.WrittenMemory, ct).ConfigureAwait(false);
    }

    private async Task SendErrorFrameAsync(RocketConnectionContext context, int streamId, RocketErrorCode errorCode, string message)
    {
        var errorData = System.Text.Encoding.UTF8.GetBytes(message);
        var frame = new RocketFrame(
            new RocketFrameHeader((uint)errorData.Length, RocketFrameType.Error, RocketFrameFlags.None, streamId),
            ReadOnlyMemory<byte>.Empty,
            errorData);

        await SendFrameAsync(context, frame, ct: default).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops the server and closes all connections.
    /// </summary>
    public async ValueTask StopAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();

        foreach (var kvp in _connections)
        {
            try
            {
                await kvp.Value.Transport.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Ignore close errors during shutdown
            }
        }

        try
        {
            await _acceptTask.ConfigureAwait(false);
        }
        catch
        {
            // Ignore
        }

        _listenSocket.Close();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _listenSocket.Dispose();
        _cts.Dispose();
    }

    private sealed class RocketConnectionContext
    {
        public RocketConnectionContext(
            int connectionId,
            Socket socket,
            IProtocolFactory protocolFactory,
            Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> requestHandler,
            CancellationToken cancellationToken)
        {
            ConnectionId = connectionId;
            Transport = new RocketSocketTransport(socket);
            ProtocolFactory = protocolFactory;
            RequestHandler = requestHandler;
            CancellationToken = cancellationToken;
            Tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public int ConnectionId { get; }
        public IRocketTransport Transport { get; }
        public IProtocolFactory ProtocolFactory { get; }
        public Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> RequestHandler { get; }
        public CancellationToken CancellationToken { get; }
        public TaskCompletionSource<object?> Tcs { get; }
    }

    private static int _connectionCounter;
}

/// <summary>
/// Socket-based RSocket transport for server connections.
/// </summary>
internal sealed class RocketSocketTransport : IRocketTransport
{
    private readonly Socket _socket;
    private readonly Pipe _sendPipe;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public RocketSocketTransport(Socket socket)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _sendPipe = new Pipe();
    }

    public bool IsOpen => _socket.Connected && !_disposed;

    public async ValueTask<ReadOnlyMemory<byte>> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var bytesRead = await _socket.ReceiveAsync(buffer, SocketFlags.None, ct).ConfigureAwait(false);
        return buffer[..bytesRead];
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _socket.SendAsync(data, SocketFlags.None, ct).ConfigureAwait(false);
    }

    public async ValueTask CloseAsync(CancellationToken ct)
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();

        try
        {
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch
        {
            // Ignore
        }

        _socket.Close();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _cts.Dispose();
            _socket.Dispose();
        }
    }
}

/// <summary>
/// Interface for Rocket transport operations.
/// </summary>
internal interface IRocketTransport
{
    bool IsOpen { get; }
    ValueTask<ReadOnlyMemory<byte>> ReceiveAsync(Memory<byte> buffer, CancellationToken ct);
    ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct);
    ValueTask CloseAsync(CancellationToken ct);
}
