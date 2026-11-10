// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Quic;

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using FBThrift;
using FbThrift.Core.Transport;

/// <summary>
/// Factory for creating Rocket QUIC client transports.
/// </summary>
public static class RocketQuicClientFactory
{
    /// <summary>
    /// Creates a Rocket QUIC client transport.
    /// </summary>
    /// <param name="endpoint">The server endpoint to connect to.</param>
    /// <param name="clientCertificate">The client certificate for mutual TLS.</param>
    /// <param name="options">RPC options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A QUIC client transport.</returns>
    /// <exception cref="ArgumentNullException">Thrown when required parameters are null.</exception>
    public static async Task<IFbThriftTransport> CreateAsync(
        IPEndPoint endpoint,
        X509Certificate2 clientCertificate,
        RpcOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(clientCertificate);

        var transport = new RocketQuicClientTransport(endpoint, clientCertificate, options);
        await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
        
        return transport;
    }
}

/// <summary>
    /// Rocket QUIC client transport for Thrift over QUIC.
    /// </summary>
    internal sealed class RocketQuicClientTransport : IFbThriftTransport, IAsyncDisposable
    {
        private readonly QuicConnectionStateMachine _connectionState;
        private readonly Pipe _sendPipe;
        private readonly Pipe _receivePipe;
        private readonly Task _receiveTask;
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentDictionary<int, TaskCompletionSource<ReadOnlyMemory<byte>>> _pendingRequests = new();
        private readonly ConcurrentDictionary<long, StreamContext> _streams = new();
        private readonly CryptoFrameBuffer _cryptoBuffer;
        private readonly Socket _socket;
        private readonly IPEndPoint _endpoint;
        private readonly X509Certificate2 _clientCertificate;
        private readonly RpcOptions _options;
        private volatile bool _disposed;
        private volatile bool _connected;
        private long _nextStreamId = 0;
        private readonly EndPoint _remoteEndPoint;

        /// <summary>
        /// Initializes a new instance of the <see cref="RocketQuicClientTransport"/> class.
        /// </summary>
        internal RocketQuicClientTransport(
            IPEndPoint endpoint,
            X509Certificate2 clientCertificate,
            RpcOptions? options = null)
        {
            _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
            _clientCertificate = clientCertificate ?? throw new ArgumentNullException(nameof(clientCertificate));
            _options = options ?? new RpcOptions();

            _connectionState = new QuicConnectionStateMachine();
            _sendPipe = new Pipe();
            _receivePipe = new Pipe();
            
            // Initialize CRYPTO frame buffer with PQ-TLS support
            var maxCryptoBufferSize = _options.PostQuantumTls?.MaxCryptoFrameBufferSize ?? 16384;
            _cryptoBuffer = new CryptoFrameBuffer(maxCryptoBufferSize);

            // Create UDP socket
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _remoteEndPoint = _endpoint;

            // Start receive loop
            _receiveTask = ReceiveLoopAsync(_cts.Token);
        }

    /// <summary>
    /// Connects to the QUIC server and performs handshake.
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_connected)
            return;

        // Send Initial packet to start QUIC handshake
        await SendInitialPacketAsync(cancellationToken).ConfigureAwait(false);

        // Wait for handshake completion (simplified)
        // In a full implementation, this would wait for HandshakeDone frame
        await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        
        _connected = true;
        _connectionState.Close(); // Transition to Connected state
    }

    /// <summary>
    /// Sends a QUIC Initial packet to start the handshake.
    /// </summary>
    private async ValueTask SendInitialPacketAsync(CancellationToken cancellationToken)
    {
        // Build Initial packet with CRYPTO frame containing ClientHello
        var packetBuilder = new QuicPacketBuilder();
        
        var clientHello = BuildClientHello();
        var cryptoFrame = new CryptoFrame(0, clientHello);
        
        var packet = packetBuilder.BuildInitial(
            QuicPacketCodec.GenerateConnectionId(8), // Destination CID
            QuicPacketCodec.GenerateConnectionId(8), // Source CID
            new[] { cryptoFrame });

        await _socket.SendToAsync(packet, SocketFlags.None, _remoteEndPoint, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds a TLS ClientHello message with optional Post-Quantum KEM support.
    /// Uses native .NET 10 ML-KEM for key share generation when PQC is enabled.
    /// </summary>
    private ReadOnlyMemory<byte> BuildClientHello()
    {
        var pqOptions = _options.PostQuantumTls;
        
        if (pqOptions?.EnablePostQuantumTls == true && QuicKeyDerivation.IsNativeMlKemSupported)
        {
            // Build ClientHello with X25519_MLKEM768 (0x11EC) in supported_groups
            // Uses native ML-KEM for key share generation
            
            // Generate ML-KEM key pair for key_share extension
            var (publicKey, privateKey, ciphertext, sharedSecret) = QuicKeyDerivation.MlKemEncapsulate(
                (MlKemAlgorithm)pqOptions.PostQuantumKemAlgorithm);
            
            // Store private key for decapsulation later
            // In a real implementation, this would be stored securely
            
            // Build ClientHello with supported_groups and key_share extensions
            var clientHello = BuildClientHelloWithPqKem(
                pqOptions.PostQuantumKemAlgorithm, 
                pqOptions.EnableHybridKeyExchange,
                publicKey);
            
            return clientHello;
        }
        
        // Standard ClientHello without PQ
        return new byte[] { 0x16, 0x03, 0x01, 0x00, 0x01, 0x01 }; // TLS 1.2 ClientHello placeholder
    }

    /// <summary>
    /// Builds a ClientHello with Post-Quantum KEM in supported_groups and key_share extensions.
    /// Uses native ML-KEM for key share generation.
    /// </summary>
    private static byte[] BuildClientHelloWithPqKem(ushort pqKemAlgorithm, bool hybrid, byte[] pqPublicKey)
    {
        // This is a simplified implementation
        // A real implementation would construct a proper TLS 1.3 ClientHello with:
        // - supported_groups extension containing pqKemAlgorithm (0x11EC) first
        // - followed by classical groups (0x001D for X25519, 0x0017 for secp256r1, etc.) if hybrid
        // - key_share extension with PQ key share (public key from ML-KEM)
        
        // For now, return a larger placeholder to simulate PQ ClientHello (~1.5KB)
        // with the PQ public key embedded
        var size = 1500 + pqPublicKey.Length; // Simulate ~1.5KB PQ ClientHello + key share
        var data = new byte[size];
        data[0] = 0x16; // Handshake
        data[1] = 0x03; // TLS 1.2
        data[2] = 0x03; // TLS 1.3
        
        // Embed the PQ public key in the key_share extension area
        pqPublicKey.CopyTo(data.AsSpan(100)); // Simplified placement
        
        // ... rest would be proper TLS 1.3 ClientHello with supported_groups and key_share
        return data;
    }

    /// <inheritdoc/>
    public async ValueTask<ReadOnlyMemory<byte>> SendRequestAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_connected)
            throw new InvalidOperationException("Transport not connected. Call ConnectAsync first.");

        if (body.IsEmpty)
            throw new ArgumentException("Request body must not be empty.", nameof(body));

        // Create a new QUIC stream for this request
        var streamId = GetNextStreamId();
        var tcs = new TaskCompletionSource<ReadOnlyMemory<byte>>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_pendingRequests.TryAdd(streamId, tcs))
        {
            throw new InvalidOperationException($"Stream ID {streamId} already in use");
        }

        try
        {
            // Write request to stream
            await WriteStreamAsync(streamId, body, true, cancellationToken).ConfigureAwait(false);

            using var registration = cancellationToken.Register(static state =>
            {
                var tcs = (TaskCompletionSource<ReadOnlyMemory<byte>>)state!;
                tcs.TrySetCanceled();
            }, tcs);

            return await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _pendingRequests.TryRemove(streamId, out _);
            throw;
        }
    }

    /// <summary>
    /// Gets the next available stream ID (client-initiated, bidirectional = even).
    /// </summary>
    private int GetNextStreamId()
    {
        // Client-initiated bidirectional streams: 0, 4, 8, 12, ...
        return (int)(Interlocked.Add(ref _nextStreamId, 4) - 4);
    }

    /// <summary>
    /// Writes a Rocket frame to a QUIC stream.
    /// </summary>
    private async ValueTask WriteStreamAsync(long streamId, ReadOnlyMemory<byte> data, bool fin, CancellationToken cancellationToken)
    {
        // Build request metadata
        var metadata = BuildRequestMetadata(streamId, data.Length);
        var frame = RocketFrame.CreateRequestResponse((int)streamId, metadata, data, fin);

        // Encode frame
        var buffer = new ArrayBufferWriter<byte>();
        frame.Write(buffer);
        var frameData = buffer.WrittenMemory;

        // Write to QUIC stream
        var streamContext = _streams.GetOrAdd(streamId, _ => new StreamContext(streamId));
        await streamContext.WriteAsync(frameData, cancellationToken).ConfigureAwait(false);

        // Send as STREAM frame in QUIC packet
        var streamFrame = new StreamFrame(
            (ulong)streamId,
            0,
            frameData,
            fin);

        await SendQuicFrameAsync(streamFrame, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds request metadata for Rocket frame.
    /// </summary>
    private ReadOnlyMemory<byte> BuildRequestMetadata(long streamId, int dataLength)
    {
        var metadata = new RequestRpcMetadata
        {
            Name = "Unknown",
            Protocol = ProtocolId.Compact,
            Kind = RpcKind.SingleRequestSingleResponse,
            Compression = CompressionAlgorithm.None,
        };

        return ThriftSerializer.EncodeCompact(metadata);
    }

    /// <summary>
    /// Sends a QUIC frame in a packet.
    /// </summary>
    private async ValueTask SendQuicFrameAsync(QuicFrame frame, CancellationToken cancellationToken)
    {
        var packetBuilder = new QuicPacketBuilder();
        var destCid = QuicPacketCodec.GenerateConnectionId(8);
        var srcCid = QuicPacketCodec.GenerateConnectionId(8);
        var packet = packetBuilder.BuildShortHeader(
            destCid,
            srcCid,
            new[] { frame });

        await _socket.SendToAsync(packet, SocketFlags.None, _remoteEndPoint, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Main receive loop for QUIC packets.
    /// </summary>
    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[65536];
        var endPoint = new IPEndPoint(IPAddress.Any, 0);

        try
        {
            while (!cancellationToken.IsCancellationRequested && !_disposed)
            {
                SocketReceiveFromResult result;
                try
                {
                    result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, endPoint, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (result.ReceivedBytes == 0)
                    continue;

                var packetData = new ReadOnlyMemory<byte>(buffer, 0, result.ReceivedBytes);
                await ProcessPacketAsync(packetData, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            foreach (var (_, tcs) in _pendingRequests)
            {
                tcs.TrySetException(new IOException("Connection lost", ex));
            }
            _pendingRequests.Clear();
        }
    }

    /// <summary>
    /// Processes a received QUIC packet.
    /// </summary>
    private async ValueTask ProcessPacketAsync(ReadOnlyMemory<byte> packetData, CancellationToken cancellationToken)
    {
        var header = QuicPacketHeader.Parse(packetData.Span, out int headerLength);
        var payload = packetData.Slice(headerLength);

        var offset = 0;
        while (offset < payload.Length)
        {
            var frame = QuicFrame.Decode(payload.Span[offset..], out int bytesConsumed);
            offset += bytesConsumed;

            await HandleQuicFrameAsync(frame, cancellationToken).ConfigureAwait(false);
        }

        _connectionState.RecordActivity();
    }

    /// <summary>
    /// Handles a QUIC frame.
    /// </summary>
    private async ValueTask HandleQuicFrameAsync(QuicFrame frame, CancellationToken cancellationToken)
    {
        switch (frame.Type)
        {
            case QuicFrameType.Stream:
                if (frame is StreamFrame streamFrame)
                {
                    await HandleStreamFrameAsync(streamFrame, cancellationToken).ConfigureAwait(false);
                }
                break;

            case QuicFrameType.Ack:
                break;

            case QuicFrameType.Crypto:
                if (frame is CryptoFrame cryptoFrame)
                {
                    await HandleCryptoFrameAsync(cryptoFrame, cancellationToken).ConfigureAwait(false);
                }
                break;

            case QuicFrameType.HandshakeDone:
                _connectionState.Close(); // Connected
                break;

            case QuicFrameType.ConnectionClose:
                _connectionState.Close();
                break;
        }
    }

    /// <summary>
    /// Handles a CRYPTO frame (TLS handshake data).
    /// Buffers and reassembles CRYPTO frames across multiple packets for PQ-TLS support.
    /// </summary>
    private async ValueTask HandleCryptoFrameAsync(CryptoFrame frame, CancellationToken cancellationToken)
    {
        var level = (EncryptionLevel)frame.Offset; // Simplified: using offset as level indicator
        
        // Add crypto frame data to buffer
        var added = _cryptoBuffer.AddData(level, frame.Offset, frame.Data);
        if (!added)
        {
            // Buffer overflow - connection should be closed
            _connectionState.Close();
            return;
        }

        // Check if we have complete handshake data for this encryption level
        var completeData = _cryptoBuffer.GetCompleteData(level);
        if (completeData != null)
        {
            // Process complete handshake data (would integrate with TLS stack)
            _cryptoBuffer.Clear(level);
        }
    }

    /// <summary>
    /// Handles a STREAM frame containing Rocket data.
    /// </summary>
    private async ValueTask HandleStreamFrameAsync(StreamFrame frame, CancellationToken cancellationToken)
    {
        var streamId = (long)frame.StreamId;
        var streamContext = _streams.GetOrAdd(streamId, _ => new StreamContext(streamId));
        
        streamContext.AppendData(frame.Data, frame.Fin);

        if (frame.Fin)
        {
            var completeData = streamContext.GetCompleteData();
            if (completeData.Length > 0)
            {
                await ProcessRocketFrameAsync(completeData, streamId, cancellationToken).ConfigureAwait(false);
            }
            
            _streams.TryRemove(streamId, out _);
        }
    }

    /// <summary>
    /// Processes a complete Rocket frame from QUIC stream data.
    /// </summary>
    private async ValueTask ProcessRocketFrameAsync(ReadOnlyMemory<byte> data, long streamId, CancellationToken cancellationToken)
    {
        if (data.Length < RocketFrameHeader.HeaderSize)
            return;

        var header = RocketFrameHeader.Read(data.Span);
        
        var metadataLength = (header.Flags & RocketFrameFlags.Metadata) != 0
            ? BinaryPrimitives.ReadInt32BigEndian(data.Span.Slice(RocketFrameHeader.HeaderSize, 4))
            : 0;

        var metadataStart = RocketFrameHeader.HeaderSize;
        var dataStart = metadataStart + metadataLength;
        var dataLength = header.Length - metadataLength;

        var metadata = metadataLength > 0
            ? data.Slice(metadataStart, metadataLength)
            : ReadOnlyMemory<byte>.Empty;

        var frameData = dataLength > 0
            ? data.Slice(dataStart, dataLength)
            : ReadOnlyMemory<byte>.Empty;

        var rocketFrame = new RocketFrame(
            new RocketFrameHeader(header.Length, header.Type, header.Flags, header.StreamId),
            metadata,
            frameData);

        switch (rocketFrame.Header.Type)
        {
            case RocketFrameType.RequestResponse:
            case RocketFrameType.Payload:
                if (_pendingRequests.TryRemove(rocketFrame.Header.StreamId, out var tcs))
                {
                    tcs.TrySetResult(frameData);
                }
                break;

            case RocketFrameType.Error:
                if (_pendingRequests.TryRemove(rocketFrame.Header.StreamId, out var errorTcs))
                {
                    var errorMsg = System.Text.Encoding.UTF8.GetString(frameData.Span);
                    errorTcs.TrySetException(new IOException($"Rocket error: {errorMsg}"));
                }
                break;
        }
    }

    /// <summary>
    /// Disposes the transport and completes all pending requests.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cts.Cancel();

        try
        {
            await _receiveTask.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Ignore
        }

        foreach (var (_, tcs) in _pendingRequests)
        {
            tcs.TrySetCanceled();
        }
        _pendingRequests.Clear();

        foreach (var (_, stream) in _streams)
        {
            stream.Dispose();
        }
        _streams.Clear();

        _sendPipe.Reader.Complete();
        _sendPipe.Writer.Complete();
        _receivePipe.Reader.Complete();
        _receivePipe.Writer.Complete();

        _socket?.Dispose();
        _connectionState.Dispose();
    }

    /// <summary>
    /// Stream context for managing QUIC stream state.
    /// </summary>
    private sealed class StreamContext : IDisposable
    {
        private readonly long _streamId;
        private readonly List<ReadOnlyMemory<byte>> _buffers = new();
        private long _totalLength = 0;
        private bool _finReceived = false;
        private readonly Pipe _pipe = new();

        public StreamContext(long streamId)
        {
            _streamId = streamId;
        }

        public void AppendData(ReadOnlyMemory<byte> data, bool fin)
        {
            if (data.Length > 0)
            {
                _buffers.Add(data);
                _totalLength += data.Length;
            }
            _finReceived = fin;
        }

        public ReadOnlyMemory<byte> GetCompleteData()
        {
            if (_totalLength == 0)
                return ReadOnlyMemory<byte>.Empty;

            var result = new byte[_totalLength];
            int offset = 0;
            foreach (var buffer in _buffers)
            {
                buffer.Span.CopyTo(result.AsSpan(offset));
                offset += buffer.Length;
            }
            return result;
        }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            await _pipe.Writer.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        }

        public void Dispose()
        {
            _pipe.Reader.Complete();
            _pipe.Writer.Complete();
        }
    }
}

/// <summary>
/// QUIC packet builder for constructing packets (following tquic packet.rs).
/// </summary>
internal sealed class QuicPacketBuilder
{
    private const int MaxPacketSize = 1200;

    /// <summary>
    /// Builds an Initial packet.
    /// </summary>
    public ReadOnlyMemory<byte> BuildInitial(
        ReadOnlyMemory<byte> destCid,
        ReadOnlyMemory<byte> srcCid,
        QuicFrame[] frames)
    {
        var buffer = new byte[MaxPacketSize];
        int offset = 0;

        // Initial packet header: 0xC0 | version | DCIL | DCID | SCIL | SCID | token | length | packet number | frames
        buffer[offset++] = (byte)QuicPacketType.Initial;
        
        // Version (4 bytes)
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset, 4), 0x00000001); // QUIC v1
        offset += 4;

        // Destination Connection ID
        buffer[offset++] = (byte)destCid.Length;
        destCid.Span.CopyTo(buffer.AsSpan(offset));
        offset += destCid.Length;

        // Source Connection ID
        buffer[offset++] = (byte)srcCid.Length;
        srcCid.Span.CopyTo(buffer.AsSpan(offset));
        offset += srcCid.Length;

        // Token (empty for Initial)
        buffer[offset++] = 0;

        // Packet number (simplified - 4 bytes)
        var packetNumberOffset = offset;
        buffer[offset++] = 0;
        buffer[offset++] = 0;
        buffer[offset++] = 0;
        buffer[offset++] = 1;

        // Encode frames
        foreach (var frame in frames)
        {
            offset += frame.Encode(buffer.AsSpan(offset));
        }

        // Update packet length (from packet number to end)
        var packetLength = offset - packetNumberOffset - 4;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(packetNumberOffset - 2, 2), (ushort)packetLength);

        return new ReadOnlyMemory<byte>(buffer, 0, offset);
    }

    /// <summary>
    /// Builds a 1-RTT (Short Header) packet.
    /// </summary>
    public ReadOnlyMemory<byte> BuildShortHeader(
        ReadOnlyMemory<byte> destCid,
        ReadOnlyMemory<byte> srcCid,
        QuicFrame[] frames)
    {
        var buffer = new byte[MaxPacketSize];
        int offset = 0;

        // Short header: 0x40 | key phase | packet number length | DCID | packet number | frames
        buffer[offset++] = 0x40; // Short header, key phase 0, packet number len = 4 bytes (0x03 + 1 = 4)

        // Destination Connection ID (simplified - using connection ID from connection state)
        destCid.Span.CopyTo(buffer.AsSpan(offset));
        offset += destCid.Length;

        // Packet number (4 bytes)
        var packetNumberOffset = offset;
        buffer[offset++] = 0;
        buffer[offset++] = 0;
        buffer[offset++] = 0;
        buffer[offset++] = 1;

        // Encode frames
        foreach (var frame in frames)
        {
            offset += frame.Encode(buffer.AsSpan(offset));
        }

        return new ReadOnlyMemory<byte>(buffer, 0, offset);
    }
}
