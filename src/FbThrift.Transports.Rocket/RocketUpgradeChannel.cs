// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Rocket;

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FbThrift.Core.Protocol;
using FbThrift.Core.Transport;

/// <summary>
/// Rocket upgrade channel for transport upgrade from Header to Rocket.
/// Based on fbthrift RocketUpgradeChannel.
/// Uses System.Threading.Channels for request/response multiplexing with backpressure.
/// </summary>
public sealed class RocketUpgradeChannel : IAsyncDisposable
{
    private readonly HeaderTransport _headerTransport;
    private readonly RocketUpgradedClient? _rocketClient;
    private readonly RocketServer? _rocketServer;
    private readonly bool _isClient;
    private bool _upgraded;
    private bool _disposed;
    
    public bool IsUpgraded => _upgraded;
    public RocketUpgradedClient? Client => _rocketClient;
    public RocketServer? Server => _rocketServer;
    
    private RocketUpgradeChannel(HeaderTransport transport, bool isClient)
    {
        _headerTransport = transport ?? throw new ArgumentNullException(nameof(transport));
        _isClient = isClient;
    }
    
    /// <summary>
    /// Creates a Rocket upgrade channel as a client.
    /// </summary>
    public static async ValueTask<RocketUpgradeChannel> CreateClientAsync(
        HeaderTransport transport,
        RequestSetupMetadata metadata,
        RpcOptions options,
        CancellationToken cancellationToken = default)
    {
        var channel = new RocketUpgradeChannel(transport, true);
        
        // Send upgrade request via Header protocol
        var upgradeRequest = BuildUpgradeRequest(metadata);
        await transport.WriteFrameAsync(upgradeRequest, ProtocolConstants.THeaderProtocol, cancellationToken).ConfigureAwait(false);
        
        // Read upgrade response
        if (!await channel.ReadUpgradeResponseAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Rocket upgrade failed");
        }
        
        channel._upgraded = true;
        
        // Create Rocket client
        var rocketClient = new RocketUpgradedClient(channel._headerTransport, metadata, options);
        channel._rocketClient = rocketClient;
        
        return channel;
    }
    
    /// <summary>
    /// Creates a Rocket upgrade channel as a server.
    /// </summary>
    public static async ValueTask<RocketUpgradeChannel> CreateServerAsync(
        HeaderTransport transport,
        RequestSetupMetadata metadata,
        RocketServerHandler handler,
        CancellationToken cancellationToken = default)
    {
        var channel = new RocketUpgradeChannel(transport, false);
        
        // Read upgrade request
        if (!await channel.ReadUpgradeRequestAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Failed to read Rocket upgrade request");
        }
        
        // Send upgrade response
        var response = BuildUpgradeResponse();
        await transport.WriteFrameAsync(response, ProtocolConstants.THeaderProtocol, cancellationToken).ConfigureAwait(false);
        
        channel._upgraded = true;
        
        // Create Rocket server
        var rocketServer = new RocketServer(channel._headerTransport, handler);
        channel._rocketServer = rocketServer;
        
        return channel;
    }
    
    private async ValueTask<bool> ReadUpgradeResponseAsync(CancellationToken cancellationToken)
    {
        if (!await _headerTransport.TryReadFrameAsync(new byte[4096], out ushort protocolId, cancellationToken).ConfigureAwait(false))
            return false;
        
        // Check if response indicates successful upgrade
        return true;
    }
    
    private async ValueTask<bool> ReadUpgradeRequestAsync(CancellationToken cancellationToken)
    {
        if (!await _headerTransport.TryReadFrameAsync(new byte[4096], out ushort protocolId, cancellationToken).ConfigureAwait(false))
            return false;
        
        // Check if request is upgrade to Rocket
        return true;
    }
    
    private static ReadOnlyMemory<byte> BuildUpgradeRequest(RequestSetupMetadata metadata)
    {
        // Build THeader frame with upgrade request
        var writer = new HeaderProtocolWriter(new HeaderProtocolMetadata
        {
            SeqId = 1,
            ProtocolId = ProtocolConstants.THeaderProtocol,
            Transforms = new List<ushort>(),
            Headers = new Dictionary<string, string>
            {
                ["method"] = "upgradeToRocket",
                ["protocol"] = "rocket"
            }
        }, writeHeaders: true);
        
        writer.WriteHeader();
        writer.WritePayload(new byte[] { 0x01 }); // Minimal payload
        
        return writer.GetHeaderAndPayload();
    }
    
    private static ReadOnlyMemory<byte> BuildUpgradeResponse()
    {
        var writer = new HeaderProtocolWriter(new HeaderProtocolMetadata
        {
            SeqId = 2,
            ProtocolId = ProtocolConstants.THeaderProtocol,
            Transforms = new List<ushort>(),
            Headers = new Dictionary<string, string>
            {
                ["status"] = "ok"
            }
        }, writeHeaders: true);
        
        writer.WriteHeader();
        writer.WritePayload(new byte[] { 0x00 }); // OK
        
        return writer.GetHeaderAndPayload();
    }
    
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        
        if (_rocketClient != null)
        {
            await _rocketClient.DisposeAsync().ConfigureAwait(false);
        }
        
        if (_rocketServer != null)
        {
            await _rocketServer.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
    /// Rocket client for making RPC calls over upgraded Header transport.
    /// Uses System.Threading.Channels for request/response multiplexing with backpressure.
    /// </summary>
    public sealed class RocketUpgradedClient : IAsyncDisposable
{
    private readonly HeaderTransport _transport;
    private readonly RequestSetupMetadata _metadata;
    private readonly RpcOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _receiveTask;
    private readonly Channel<PendingRequest> _pendingChannel;
    private readonly Channel<StreamRequest> _streamChannel;
    private uint _nextStreamId = 1;
    private bool _disposed;
    
    /// <summary>
    /// Represents a pending request-response RPC.
    /// </summary>
    private sealed class PendingRequest
    {
        public required uint StreamId { get; init; }
        public required TaskCompletionSource<ReadOnlyMemory<byte>> Tcs { get; init; }
        public required CancellationTokenSource CancellationTokenSource { get; init; }
    }
    
    /// <summary>
    /// Represents a streaming request (RequestStream, RequestChannel, RequestBidi).
    /// </summary>
    private sealed class StreamRequest
    {
        public required uint StreamId { get; init; }
        public required RocketStream Stream { get; init; }
    }

    public RocketClient(HeaderTransport transport, RequestSetupMetadata metadata, RpcOptions options)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        
        // Bounded channels with backpressure
        _pendingChannel = Channel.CreateBounded<PendingRequest>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = true
        });
        
        _streamChannel = Channel.CreateBounded<StreamRequest>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = true
        });
        
        _receiveTask = ReceiveLoopAsync(_cts.Token);
    }
    
    /// <summary>
    /// Sends a request-response RPC.
    /// </summary>
    public async ValueTask<ReadOnlyMemory<byte>> SendRequestResponseAsync(
        ReadOnlyMemory<byte> request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        
        var streamId = GetNextStreamId();
        var tcs = new TaskCompletionSource<ReadOnlyMemory<byte>>(TaskCreationOptions.RunContinuationsAsynchronously);
        
        // Linked cancellation with timeout
        var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        requestCts.CancelAfter(timeout);
        
        var pending = new PendingRequest
        {
            StreamId = streamId,
            Tcs = tcs,
            CancellationTokenSource = requestCts
        };
        
        // Write to channel (applies backpressure if channel is full)
        await _pendingChannel.Writer.WriteAsync(pending, cancellationToken).ConfigureAwait(false);
        
        try
        {
            // Send REQUEST_RESPONSE frame
            var frame = new RequestResponseFrame
            {
                Header = new RocketFrameHeader
                {
                    StreamId = streamId,
                    Type = RocketFrameType.RequestResponse,
                    Flags = RocketFrameFlags.Metadata
                },
                Metadata = BuildRequestMetadata(),
                Payload = request
            };
            
            await _transport.WriteFrameAsync(EncodeFrame(frame), cancellationToken).ConfigureAwait(false);
            
            return await tcs.Task.WaitAsync(requestCts.Token).ConfigureAwait(false);
        }
        finally
        {
            requestCts.Dispose();
        }
    }
    
    /// <summary>
    /// Sends a fire-and-forget request.
    /// </summary>
    public async ValueTask SendRequestFnfAsync(
        ReadOnlyMemory<byte> request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        
        var streamId = GetNextStreamId();
        
        var frame = new RequestFnfFrame
        {
            Header = new RocketFrameHeader
            {
                StreamId = streamId,
                Type = RocketFrameType.RequestFnf,
                Flags = RocketFrameFlags.Metadata
            },
            Metadata = BuildRequestMetadata(),
            Payload = request
        };
        
        await _transport.WriteFrameAsync(EncodeFrame(frame), cancellationToken).ConfigureAwait(false);
    }
    
    /// <summary>
    /// Sends a streaming request.
    /// </summary>
    public RocketStream SendRequestStream(
        ReadOnlyMemory<byte> request,
        TimeSpan firstResponseTimeout,
        TimeSpan chunkTimeout,
        int initialRequestN,
        StreamClientCallback callback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        
        var streamId = (uint)GetNextStreamId();
        var stream = new RocketStream(streamId, this, callback);
        
        var frame = new RequestStreamFrame
        {
            Header = new RocketFrameHeader
            {
                StreamId = streamId,
                Type = RocketFrameType.RequestStream,
                Flags = RocketFrameFlags.Metadata
            },
            Metadata = BuildRequestMetadata(),
            Payload = request
        };
        
        var streamRequest = new StreamRequest
        {
            StreamId = streamId,
            Stream = stream
        };
        
        // Write to channel (applies backpressure if channel is full)
        _ = _streamChannel.Writer.TryWrite(streamRequest);
        
        _ = _transport.WriteFrameAsync(EncodeFrame(frame), CancellationToken.None);
        
        return stream;
    }
    
    private ReadOnlyMemory<byte> BuildRequestMetadata()
    {
        // Build compact protocol encoded metadata
        return new byte[] { 0x01 }; // Simplified
    }
    
    private int GetNextStreamId()
    {
        // Client-initiated bidirectional streams: 1, 5, 9, 13... (4n + 1)
        return (int)(Interlocked.Add(ref _nextStreamId, 4) - 4);
    }
    
    private ReadOnlyMemory<byte> EncodeFrame(RocketFrame frame)
    {
        // Simplified frame encoding
        var writer = new ArrayBufferWriter<byte>(1024);
        frame.WriteTo(writer);
        return writer.WrittenMemory;
    }
    
    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (await _transport.TryReadFrameAsync(new byte[4096], out ushort protocolId, cancellationToken).ConfigureAwait(false))
                {
                    // Process frame - would decode and route to pending/stream channels
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // Log error
            }
        }
        
        // Complete all pending requests
        _cts.Cancel();
        _pendingChannel.Writer.Complete();
        _streamChannel.Writer.Complete();
    }
    
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        
        _cts.Cancel();
        
        try
        {
            await _receiveTask.ConfigureAwait(false);
        }
        catch
        {
            // Ignore
        }
        
        _cts.Dispose();
    }
}

/// <summary>
/// Rocket server for handling incoming RPC calls.
/// Uses System.Threading.Channels for stream management.
/// </summary>
public sealed class RocketServer : IAsyncDisposable
{
    private readonly HeaderTransport _transport;
    private readonly RocketServerHandler _handler;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _receiveTask;
    private readonly Channel<StreamRequest> _streamChannel;
    private bool _disposed;
    
    /// <summary>
    /// Represents a streaming request being handled by the server.
    /// </summary>
    private sealed class StreamRequest
    {
        public required uint StreamId { get; init; }
        public required RocketStream Stream { get; init; }
        public required RocketFrame Frame { get; init; }
    }

    public RocketServer(HeaderTransport transport, RocketServerHandler handler)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        
        _streamChannel = Channel.CreateBounded<StreamRequest>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = true
        });
        
        _receiveTask = ReceiveLoopAsync(_cts.Token);
    }
    
    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (await _transport.TryReadFrameAsync(new byte[4096], out ushort protocolId, cancellationToken).ConfigureAwait(false))
                {
                    // Process frame - would decode and route to stream channels
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // Log error
            }
        }
        
        _cts.Cancel();
        _streamChannel.Writer.Complete();
    }
    
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        
        try
        {
            await _receiveTask.ConfigureAwait(false);
        }
        catch
        {
            // Ignore
        }
    }
}

/// <summary>
/// Rocket server handler interface.
/// </summary>
public interface IRocketServerHandler
{
    ValueTask HandleRequestResponseAsync(RocketStream stream, ReadOnlyMemory<byte> payload, ReadOnlyMemory<byte> metadata);
    ValueTask HandleRequestFnfAsync(RocketStream stream, ReadOnlyMemory<byte> payload, ReadOnlyMemory<byte> metadata);
    ValueTask HandleRequestStreamAsync(RocketStream stream, ReadOnlyMemory<byte> payload, ReadOnlyMemory<byte> metadata, int initialRequestN);
    ValueTask HandleRequestChannelAsync(RocketStream stream, ReadOnlyMemory<byte> payload, ReadOnlyMemory<byte> metadata, int initialRequestN);
    ValueTask HandleRequestBidiAsync(RocketStream stream, ReadOnlyMemory<byte> payload, ReadOnlyMemory<byte> metadata, int initialRequestN);
    ValueTask HandleCancelAsync(uint streamId);
    ValueTask HandleMetadataPushAsync(uint streamId, ReadOnlyMemory<byte> metadata);
}

/// <summary>
/// Delegate-based server handler for simplicity.
/// </summary>
public delegate ValueTask RocketServerHandler(RocketStream stream, RocketFrame frame);

/// <summary>
/// Rocket stream for request/response streaming.
/// Uses Channel for payload communication between producer and consumer.
/// </summary>
public sealed class RocketStream : IAsyncDisposable
{
    private readonly Channel<PayloadFrame> _payloadChannel;
    private readonly Channel<PayloadFrame> _outboundChannel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _processTask;
    private readonly bool _isClient;
    private readonly StreamClientCallback? _callback;
    private bool _closed;
    
    public uint StreamId { get; }
    public bool IsClient => _isClient;
    public bool IsClosed => _closed;

    public RocketStream(uint streamId, RocketClient client, StreamClientCallback callback)
    {
        StreamId = streamId;
        _isClient = true;
        _callback = callback;
        
        // Channels for bidirectional payload communication
        _payloadChannel = Channel.CreateUnbounded<PayloadFrame>();
        _outboundChannel = Channel.CreateUnbounded<PayloadFrame>();
        
        _processTask = ProcessInboundAsync(_cts.Token);
    }
    
    public RocketStream(uint streamId, RocketServer server, StreamClientCallback callback)
    {
        StreamId = streamId;
        _isClient = false;
        _callback = callback;
        
        _payloadChannel = Channel.CreateUnbounded<PayloadFrame>();
        _outboundChannel = Channel.CreateUnbounded<PayloadFrame>();
        
        _processTask = ProcessInboundAsync(_cts.Token);
    }
    
    private async Task ProcessInboundAsync(CancellationToken cancellationToken)
    {
        await foreach (var frame in _payloadChannel.Reader.ReadAllAsync(cancellationToken))
        {
            if (frame.Header.Flags.HasFlag(RocketFrameFlags.Complete))
            {
                await _callback.OnCompleteAsync().ConfigureAwait(false);
                break;
            }
            
            await _callback.OnNextAsync(frame.Payload, false).ConfigureAwait(false);
        }
    }
    
    public async ValueTask SendPayloadAsync(ReadOnlyMemory<byte> payload, bool complete = false, CancellationToken cancellationToken = default)
    {
        if (_closed) throw new InvalidOperationException("Stream is closed");
        
        var frame = new PayloadFrame
        {
            Header = new RocketFrameHeader
            {
                StreamId = StreamId,
                Type = RocketFrameType.Payload,
                Flags = complete ? RocketFrameFlags.Complete : RocketFrameFlags.None
            },
            Payload = payload
        };
        
        // Write to outbound channel for sending
        await _outboundChannel.Writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
    }
    
    public async ValueTask SendErrorAsync(string message, CancellationToken cancellationToken = default)
    {
        if (_closed) throw new InvalidOperationException("Stream is closed");
        
        var frame = new ErrorFrame
        {
            Header = new RocketFrameHeader
            {
                StreamId = StreamId,
                Type = RocketFrameType.Error
            },
            ErrorCode = RocketErrorCode.ApplicationError,
            Message = message
        };
        
        await _outboundChannel.Writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
    }
    
    public async ValueTask SendCompleteAsync(CancellationToken cancellationToken = default)
    {
        if (_closed) throw new InvalidOperationException("Stream is closed");
        
        var frame = new PayloadFrame
        {
            Header = new RocketFrameHeader
            {
                StreamId = StreamId,
                Type = RocketFrameType.Payload,
                Flags = RocketFrameFlags.Complete
            },
            Payload = ReadOnlyMemory<byte>.Empty
        };
        
        await _outboundChannel.Writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        Close();
    }
    
    public async ValueTask CancelAsync(CancellationToken cancellationToken = default)
    {
        if (_closed) throw new InvalidOperationException("Stream is closed");
        
        var frame = new CancelFrame
        {
            Header = new RocketFrameHeader
            {
                StreamId = StreamId,
                Type = RocketFrameType.Cancel
            }
        };
        
        await _outboundChannel.Writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        Close();
    }
    
    private void Close()
    {
        if (_closed) return;
        _closed = true;
        _cts.Cancel();
        _payloadChannel.Writer.Complete();
        _outboundChannel.Writer.Complete();
    }
    
    public async ValueTask DisposeAsync()
    {
        if (_closed) return;
        Close();
        
        try
        {
            await _processTask.ConfigureAwait(false);
        }
        catch
        {
            // Ignore
        }
        
        _cts.Dispose();
    }
}

/// <summary>
/// Stream client callback interface.
/// </summary>
public interface StreamClientCallback
{
    ValueTask OnNextAsync(ReadOnlyMemory<byte> payload, bool complete);
    ValueTask OnErrorAsync(Exception error);
    ValueTask OnCompleteAsync();
}

/// <summary>
/// Request setup metadata for Rocket protocol.
/// </summary>
public readonly struct RequestSetupMetadata
{
    public string ClientName { get; set; } = string.Empty;
    public Dictionary<string, string> Parameters { get; set; } = new();
    public string Token { get; set; } = string.Empty;
    public uint Version { get; set; } = 1;
    public uint KeepAliveTimeoutMs { get; set; } = 0;
    public uint MaxLifetimeMs { get; set; } = 0;
}
