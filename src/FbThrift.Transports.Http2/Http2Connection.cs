// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Http2;

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FbThrift.Core.Header;
using FbThrift.Core.Protocol;
using FbThrift.Core.Transport;

/// <summary>
/// HTTP/2 Stream State (RFC 7540 Section 5.1).
/// Based on mvfst QuicStreamState pattern.
/// </summary>
public enum Http2StreamState
{
    Idle,
    ReservedLocal,
    ReservedRemote,
    Open,
    HalfClosedLocal,
    HalfClosedRemote,
    Closed,
}

/// <summary>
    /// HTTP/2 Stream with flow control and priority.
    /// Based on mvfst QuicStreamLike pattern.
    /// Uses System.Threading.Channels for frame queuing with backpressure.
    /// </summary>
    public sealed class Http2Stream
    {
        private readonly object _lock = new();
        private readonly Channel<Http2FrameHeader> _pendingFrames;
        private long _sendWindow;
        private long _receiveWindow;
        private Http2StreamState _state;
        private uint _priorityWeight = 16;
        private uint _streamDependency = 0;
        private bool _exclusive = false;
        
        public uint StreamId { get; }
        public Http2StreamState State
        {
            get => _state;
            set
            {
                lock (_lock)
                {
                    _state = value;
                }
            }
        }
        
        public long SendWindow
        {
            get => _sendWindow;
            set
            {
                lock (_lock)
                {
                    _sendWindow = value;
                }
            }
        }
        
        public long ReceiveWindow
        {
            get => _receiveWindow;
            set
            {
                lock (_lock)
                {
                    _receiveWindow = value;
                }
            }
        }
        
        public uint PriorityWeight => _priorityWeight;
        public uint StreamDependency => _streamDependency;
        public bool Exclusive => _exclusive;
        
        public event Action<Http2FrameHeader>? FrameReady;
        public event Action<uint, Http2ErrorCode>? StreamReset;
        public event Action? StreamClosed;
        
        public Http2Stream(uint streamId, long initialWindowSize = 65535)
        {
            StreamId = streamId;
            _sendWindow = initialWindowSize;
            _receiveWindow = initialWindowSize;
            _state = Http2StreamState.Idle;
            
            // Bounded channel with backpressure
            _pendingFrames = Channel.CreateBounded<Http2FrameHeader>(new BoundedChannelOptions(1000)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = true
            });
        }
        
        public void SetPriority(uint streamDependency, byte weight, bool exclusive)
        {
            lock (_lock)
            {
                _streamDependency = streamDependency;
                _priorityWeight = weight == 0 ? (byte)1 : weight;
                _exclusive = exclusive;
            }
        }
        
        public void UpdateSendWindow(long increment)
        {
            lock (_lock)
            {
                _sendWindow += increment;
                if (_sendWindow > int.MaxValue)
                    _sendWindow = int.MaxValue;
            }
        }
        
        public bool TryConsumeSendWindow(int length)
        {
            lock (_lock)
            {
                if (_sendWindow >= length)
                {
                    _sendWindow -= length;
                    return true;
                }
                return false;
            }
        }
        
        public void UpdateReceiveWindow(long increment)
        {
            lock (_lock)
            {
                _receiveWindow += increment;
                if (_receiveWindow > int.MaxValue)
                    _receiveWindow = int.MaxValue;
            }
        }
        
        public void EnqueueFrame(Http2FrameHeader frame)
        {
            // Non-blocking try write - if full, frame is dropped (backpressure)
            if (_pendingFrames.Writer.TryWrite(frame))
            {
                FrameReady?.Invoke(frame);
            }
            else
            {
                // Channel full - could implement DropOldest or wait
                FrameReady?.Invoke(frame); // Still notify for monitoring
            }
        }
        
        public async ValueTask<bool> WaitForFrameAsync(Http2FrameHeader frame, CancellationToken cancellationToken = default)
        {
            // Wait for space in channel if needed, then write
            await _pendingFrames.Writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            FrameReady?.Invoke(frame);
            return true;
        }
        
        public bool TryDequeueFrame(out Http2FrameHeader frame)
        {
            return _pendingFrames.Reader.TryRead(out frame!);
        }
        
        public async ValueTask<Http2FrameHeader> DequeueFrameAsync(CancellationToken cancellationToken = default)
        {
            return await _pendingFrames.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        
        public void Reset(Http2ErrorCode errorCode)
        {
            lock (_lock)
            {
                if (_state == Http2StreamState.Closed)
                    return;
                
                _state = Http2StreamState.Closed;
            }
            
            StreamReset?.Invoke(StreamId, errorCode);
            StreamClosed?.Invoke();
        }
        
        public void Close()
        {
            lock (_lock)
            {
                if (_state == Http2StreamState.Closed)
                    return;
                
                _state = Http2StreamState.Closed;
            }
            
            StreamClosed?.Invoke();
        }
        
        public bool TryTransitionState(Http2StreamState newState)
        {
            lock (_lock)
            {
                if (!IsValidTransition(_state, newState))
                    return false;
                
                _state = newState;
                return true;
            }
        }
        
        private static bool IsValidTransition(Http2StreamState from, Http2StreamState to)
        {
            return (from, to) switch
            {
                (Http2StreamState.Idle, Http2StreamState.Open) => true,
                (Http2StreamState.Idle, Http2StreamState.ReservedLocal) => true,
                (Http2StreamState.Idle, Http2StreamState.ReservedRemote) => true,
                (Http2StreamState.ReservedLocal, Http2StreamState.HalfClosedRemote) => true,
                (Http2StreamState.ReservedRemote, Http2StreamState.HalfClosedLocal) => true,
                (Http2StreamState.Open, Http2StreamState.HalfClosedLocal) => true,
                (Http2StreamState.Open, Http2StreamState.HalfClosedRemote) => true,
                (Http2StreamState.HalfClosedLocal, Http2StreamState.Closed) => true,
                (Http2StreamState.HalfClosedRemote, Http2StreamState.Closed) => true,
                (_, Http2StreamState.Closed) => true,
                _ => false
            };
        }
    }

/// <summary>
    /// HTTP/2 Connection with stream management, flow control, and settings.
    /// Based on mvfst QuicConnectionStateBase pattern with HTTP/2 specifics.
    /// Uses System.Threading.Channels for frame queuing with backpressure.
    /// </summary>
    public sealed class Http2Connection : IAsyncDisposable
    {
        private readonly object _streamsLock = new();
        private readonly Dictionary<uint, Http2Stream> _streams = new();
        private readonly Channel<Http2FrameHeader> _outboundFrames;
        private uint _nextStreamId = 1; // Client-initiated streams are odd
        private long _connectionSendWindow = 65535;
        private long _connectionReceiveWindow = 65535;
        private int _maxFrameSize = 16384;
        private int _maxConcurrentStreams = 100;
        private int _maxHeaderListSize = 16384;
        private bool _enablePush = true;
        private int _headerTableSize = 4096;
        private bool _disposed;
        private readonly Hpack.Encoder _hpackEncoder;
        private readonly Hpack.Decoder _hpackDecoder;
        private readonly ArrayBufferWriter<byte> _encodeBuffer = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _processingTask;
        
        // Settings
        public int MaxFrameSize => _maxFrameSize;
        public int MaxConcurrentStreams => _maxConcurrentStreams;
        public int MaxHeaderListSize => _maxHeaderListSize;
        public bool EnablePush => _enablePush;
        public int HeaderTableSize => _headerTableSize;
        public long ConnectionSendWindow => _connectionSendWindow;
        public long ConnectionReceiveWindow => _connectionReceiveWindow;
        
        // Events
        public event Action<Http2FrameHeader>? FrameReady;
        public event Action<uint, IReadOnlyList<KeyValuePair<string, string>>>? HeadersReceived;
        public event Action<uint, ReadOnlyMemory<byte>, bool>? DataReceived;
        public event Action<uint, Http2ErrorCode>? StreamReset;
        public event Action<uint, uint, IReadOnlyList<KeyValuePair<string, string>>>? PushPromiseReceived;
        public event Action<Http2ErrorCode>? GoAwayReceived;
        public event Action? ConnectionClosed;
        
        public Http2Connection(bool isClient = true)
        {
            _nextStreamId = isClient ? 1u : 2u;
            
            _sendPipe = new Pipe();
            _receivePipe = new Pipe();
            
            _hpackEncoder = new Hpack.Encoder(_encodeBuffer);
            _hpackDecoder = new Hpack.Decoder();
            _hpackDecoder.SetMaxTableSize(_headerTableSize);
            
            // Bounded channel with backpressure for outbound frames
            _outboundFrames = Channel.CreateBounded<Http2FrameHeader>(new BoundedChannelOptions(10000)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = true
            });
            
            _processingTask = ProcessFramesAsync(_cts.Token);
        }
        
        private readonly Pipe _sendPipe;
        private readonly Pipe _receivePipe;
    
    /// <summary>
    /// Creates a new stream for outgoing request.
    /// </summary>
    public Http2Stream CreateStream()
    {
        lock (_streamsLock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(Http2Connection));
            
            if (_streams.Count >= _maxConcurrentStreams)
                throw new InvalidOperationException("Maximum concurrent streams reached");
            
            var streamId = _nextStreamId;
            _nextStreamId += 2;
            
            var stream = new Http2Stream(streamId, _connectionSendWindow);
            stream.FrameReady += OnStreamFrameReady;
            stream.StreamReset += (id, code) => StreamReset?.Invoke(id, code);
            stream.StreamClosed += OnStreamClosed;
            
            _streams[streamId] = stream;
            
            return stream;
        }
    }
    
    /// <summary>
    /// Gets or creates a stream by ID (for server/push).
    /// </summary>
    public Http2Stream GetOrCreateStream(uint streamId)
    {
        lock (_streamsLock)
        {
            if (_streams.TryGetValue(streamId, out var existing))
                return existing;
            
            var stream = new Http2Stream(streamId, _connectionSendWindow);
            stream.FrameReady += OnStreamFrameReady;
            stream.StreamReset += (id, code) => StreamReset?.Invoke(id, code);
            stream.StreamClosed += OnStreamClosed;
            
            _streams[streamId] = stream;
            return stream;
        }
    }
    
    /// <summary>
    /// Removes a closed stream.
    /// </summary>
    public void RemoveStream(uint streamId)
    {
        lock (_streamsLock)
        {
            if (_streams.Remove(streamId, out var stream))
            {
                stream.FrameReady -= OnStreamFrameReady;
                // Note: lambda handlers can't be easily removed, but stream is being removed anyway
                stream.StreamClosed -= OnStreamClosed;
            }
        }
    }
    
    /// <summary>
    /// Sends a HEADERS frame.
    /// </summary>
    public void SendHeaders(uint streamId, IReadOnlyList<KeyValuePair<string, string>> headers, bool endStream = false)
    {
        var stream = GetOrCreateStream(streamId);
        
        // Encode headers using HPACK
        _encodeBuffer.Clear();
        _hpackEncoder.EncodeHeaders(headers);
        var headerBlock = _encodeBuffer.WrittenMemory;
        
        var flags = Http2FrameFlags.HeadersEndHeaders;
        if (endStream)
            flags |= Http2FrameFlags.HeadersEndStream;
        
        var header = new Http2FrameHeader
        {
            Length = (uint)headerBlock.Length,
            Type = Http2FrameType.Headers,
            Flags = flags,
            StreamId = streamId
        };
        
        _outboundFrames.Writer.TryWrite(header);
        FrameReady?.Invoke(header);
    }
    
    /// <summary>
    /// Sends a DATA frame.
    /// </summary>
    public void SendData(uint streamId, ReadOnlyMemory<byte> data, bool endStream = false)
    {
        var stream = GetOrCreateStream(streamId);
        
        // Check flow control
        if (!stream.TryConsumeSendWindow(data.Length))
        {
            // Buffer for later when window opens
            throw new InvalidOperationException("Insufficient send window");
        }
        
        // Check connection-level flow control
        if (_connectionSendWindow < data.Length)
        {
            throw new InvalidOperationException("Insufficient connection send window");
        }
        
        _connectionSendWindow -= data.Length;
        
        var flags = Http2FrameFlags.None;
        if (endStream)
            flags |= Http2FrameFlags.DataEndStream;
        
        var header = new Http2FrameHeader
        {
            Length = (uint)data.Length,
            Type = Http2FrameType.Data,
            Flags = flags,
            StreamId = streamId
        };
        
        _outboundFrames.Writer.TryWrite(header);
        FrameReady?.Invoke(header);
    }
    
    /// <summary>
    /// Sends a WINDOW_UPDATE frame.
    /// </summary>
    public void SendWindowUpdate(uint streamId, uint increment)
    {
        var header = new Http2FrameHeader
        {
            Length = 4,
            Type = Http2FrameType.WindowUpdate,
            Flags = Http2FrameFlags.None,
            StreamId = streamId
        };
        
        _outboundFrames.Writer.TryWrite(header);
        FrameReady?.Invoke(header);
    }
    
    /// <summary>
    /// Sends a SETTINGS frame.
    /// </summary>
    public void SendSettings(IReadOnlyList<Http2Setting> settings, bool ack = false)
    {
        var flags = ack ? Http2FrameFlags.SettingsAck : Http2FrameFlags.None;
        var header = new Http2FrameHeader
        {
            Length = (uint)(settings.Count * 6),
            Type = Http2FrameType.Settings,
            Flags = flags,
            StreamId = 0
        };
        
        _outboundFrames.Writer.TryWrite(header);
        FrameReady?.Invoke(header);
    }
    
    /// <summary>
    /// Sends a PING frame.
    /// </summary>
    public void SendPing(ulong opaqueData, bool ack = false)
    {
        var flags = ack ? Http2FrameFlags.PingAck : Http2FrameFlags.None;
        var header = new Http2FrameHeader
        {
            Length = 8,
            Type = Http2FrameType.Ping,
            Flags = flags,
            StreamId = 0
        };
        
        _outboundFrames.Writer.TryWrite(header);
        FrameReady?.Invoke(header);
    }
    
    /// <summary>
    /// Sends a GOAWAY frame.
    /// </summary>
    public void SendGoAway(uint lastStreamId, Http2ErrorCode errorCode, ReadOnlyMemory<byte> debugData = default)
    {
        uint debugLength = (uint)debugData.Length;
        var header = new Http2FrameHeader
        {
            Length = 8 + debugLength,
            Type = Http2FrameType.GoAway,
            Flags = Http2FrameFlags.None,
            StreamId = 0
        };
        
        _outboundFrames.Writer.TryWrite(header);
        FrameReady?.Invoke(header);
    }
    
    /// <summary>
    /// Sends a PUSH_PROMISE frame (server only).
    /// </summary>
    public void SendPushPromise(uint streamId, uint promisedStreamId, IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        if (!_enablePush)
            throw new InvalidOperationException("Push not enabled");
        
        // Encode headers using HPACK
        _encodeBuffer.Clear();
        _hpackEncoder.EncodeHeaders(headers);
        var headerBlock = _encodeBuffer.WrittenMemory;
        
        var flags = Http2FrameFlags.HeadersEndHeaders;
        var header = new Http2FrameHeader
        {
            Length = (uint)(4 + headerBlock.Length), // 4 bytes for promised stream ID
            Type = Http2FrameType.PushPromise,
            Flags = flags,
            StreamId = streamId
        };
        
        _outboundFrames.Writer.TryWrite(header);
        FrameReady?.Invoke(header);
    }
    
    /// <summary>
    /// Applies received settings.
    /// </summary>
    public void ApplySettings(IReadOnlyList<Http2Setting> settings)
    {
        foreach (var setting in settings)
        {
            switch (setting.Id)
            {
                case Http2SettingsId.HeaderTableSize:
                    _headerTableSize = (int)Math.Min(setting.Value, 16384);
                    _hpackDecoder.SetMaxTableSize(_headerTableSize);
                    break;
                case Http2SettingsId.EnablePush:
                    _enablePush = setting.Value != 0;
                    break;
                case Http2SettingsId.MaxConcurrentStreams:
                    _maxConcurrentStreams = (int)setting.Value;
                    break;
                case Http2SettingsId.InitialWindowSize:
                    if (setting.Value > 0x7FFFFFFF)
                        throw new InvalidOperationException("Initial window size too large");
                    _connectionSendWindow = setting.Value;
                    break;
                case Http2SettingsId.MaxFrameSize:
                    if (setting.Value < 16384 || setting.Value > 16777215)
                        throw new InvalidOperationException("Invalid max frame size");
                    _maxFrameSize = (int)setting.Value;
                    break;
                case Http2SettingsId.MaxHeaderListSize:
                    _maxHeaderListSize = (int)setting.Value;
                    break;
            }
        }
    }
    
    /// <summary>
    /// Processes incoming frames.
    /// </summary>
    public void ProcessInboundFrame(Http2FrameHeader header, ReadOnlyMemory<byte> payload)
    {
        switch (header.Type)
        {
            case Http2FrameType.Headers:
                ProcessHeaders(header, payload);
                break;
            case Http2FrameType.Data:
                ProcessData(header, payload);
                break;
            case Http2FrameType.Settings:
                ProcessSettings(header, payload);
                break;
            case Http2FrameType.WindowUpdate:
                ProcessWindowUpdate(header, payload);
                break;
            case Http2FrameType.Ping:
                ProcessPing(header, payload);
                break;
            case Http2FrameType.GoAway:
                ProcessGoAway(header, payload);
                break;
            case Http2FrameType.RstStream:
                ProcessRstStream(header, payload);
                break;
            case Http2FrameType.PushPromise:
                ProcessPushPromise(header, payload);
                break;
            case Http2FrameType.Priority:
                ProcessPriority(header, payload);
                break;
            case Http2FrameType.Continuation:
                // Handle continuation frames (for large header blocks)
                break;
        }
    }
    
    private void ProcessHeaders(Http2FrameHeader header, ReadOnlyMemory<byte> payload)
    {
        var stream = GetOrCreateStream(header.StreamId);
        
        // Decode HPACK headers
        var headers = _hpackDecoder.DecodeHeaders(payload.Span);
        
        stream.TryTransitionState(header.Flags.HasFlag(Http2FrameFlags.HeadersEndStream) 
            ? Http2StreamState.HalfClosedRemote 
            : Http2StreamState.Open);
        
        HeadersReceived?.Invoke(header.StreamId, headers);
    }
    
    private void ProcessData(Http2FrameHeader header, ReadOnlyMemory<byte> payload)
    {
        var stream = GetOrCreateStream(header.StreamId);
        
        // Update connection receive window
        _connectionReceiveWindow -= payload.Length;
        
        // Send WINDOW_UPDATE if window is getting low
        if (_connectionReceiveWindow < 16384)
        {
            SendWindowUpdate(0, 65535);
            _connectionReceiveWindow += 65535;
        }
        
        bool endStream = header.Flags.HasFlag(Http2FrameFlags.DataEndStream);
        
        if (endStream)
            stream.TryTransitionState(Http2StreamState.HalfClosedLocal);
        
        DataReceived?.Invoke(header.StreamId, payload, endStream);
    }
    
    private void ProcessSettings(Http2FrameHeader header, ReadOnlyMemory<byte> payload)
    {
        if (header.StreamId != 0)
        {
            // Stream-specific settings not allowed
            SendGoAway(_nextStreamId - 2, Http2ErrorCode.ProtocolError);
            return;
        }
        
        if (header.Flags.HasFlag(Http2FrameFlags.SettingsAck))
        {
            // Settings acknowledged
            return;
        }
        
        var settings = Http2FrameParser.ParseSettings(payload.Span);
        ApplySettings(settings);
        
        // Send ACK
        SendSettings(Array.Empty<Http2Setting>(), ack: true);
    }
    
    private void ProcessWindowUpdate(Http2FrameHeader header, ReadOnlyMemory<byte> payload)
    {
        var increment = Http2FrameParser.ParseWindowUpdate(payload.Span);
        
        if (header.StreamId == 0)
        {
            // Connection-level window update
            _connectionSendWindow += increment;
        }
        else
        {
            // Stream-level window update
            var stream = GetOrCreateStream(header.StreamId);
            stream.UpdateSendWindow(increment);
        }
    }
    
    private void ProcessPing(Http2FrameHeader header, ReadOnlyMemory<byte> payload)
    {
        if (header.StreamId != 0)
        {
            SendGoAway(_nextStreamId - 2, Http2ErrorCode.ProtocolError);
            return;
        }
        
        var opaqueData = Http2FrameParser.ParsePing(payload.Span);
        
        if (!header.Flags.HasFlag(Http2FrameFlags.PingAck))
        {
            // Respond with PING ACK
            SendPing(opaqueData, ack: true);
        }
    }
    
    private void ProcessGoAway(Http2FrameHeader header, ReadOnlyMemory<byte> payload)
    {
        if (header.StreamId != 0)
        {
            return;
        }
        
        var (lastStreamId, errorCode, debugData) = Http2FrameParser.ParseGoAway(payload.Span);
        GoAwayReceived?.Invoke(errorCode);
        
        // Close all streams with ID > lastStreamId
        lock (_streamsLock)
        {
            foreach (var stream in _streams.Values)
            {
                if (stream.StreamId > lastStreamId)
                {
                    stream.Reset(Http2ErrorCode.NoError);
                }
            }
        }
    }
    
    private void ProcessRstStream(Http2FrameHeader header, ReadOnlyMemory<byte> payload)
    {
        var errorCode = Http2FrameParser.ParseRstStream(payload.Span);
        var stream = GetOrCreateStream(header.StreamId);
        stream.Reset(errorCode);
        StreamReset?.Invoke(header.StreamId, errorCode);
        RemoveStream(header.StreamId);
    }
    
    private void ProcessPushPromise(Http2FrameHeader header, ReadOnlyMemory<byte> payload)
    {
        if (!_enablePush)
        {
            SendGoAway(_nextStreamId - 2, Http2ErrorCode.ProtocolError);
            return;
        }
        
        var (promisedStreamId, headerBlock, _) = Http2FrameParser.ParsePushPromise(payload.Span, 
            header.Flags.HasFlag(Http2FrameFlags.HeadersPadded));
        
        var headers = _hpackDecoder.DecodeHeaders(headerBlock.Span);
        
        // Create promised stream in reserved state
        var promisedStream = GetOrCreateStream(promisedStreamId);
        promisedStream.TryTransitionState(Http2StreamState.ReservedRemote);
        
        PushPromiseReceived?.Invoke(header.StreamId, promisedStreamId, headers);
    }
    
    private void ProcessPriority(Http2FrameHeader header, ReadOnlyMemory<byte> payload)
    {
        var priority = Http2FrameParser.ParsePriority(payload.Span);
        var stream = GetOrCreateStream(header.StreamId);
        stream.SetPriority(priority.StreamDependency, priority.Weight, priority.Exclusive);
    }
    
    private void OnStreamFrameReady(Http2FrameHeader frame)
    {
        _outboundFrames.Writer.TryWrite(frame);
        FrameReady?.Invoke(frame);
    }
    
    private void OnStreamReset(uint streamId, Http2ErrorCode errorCode)
    {
        StreamReset?.Invoke(streamId, errorCode);
        RemoveStream(streamId);
    }
    
    private void OnStreamClosed()
    {
        // Stream closed, could do cleanup
    }
    
    /// <summary>
    /// Processes outbound frames and writes to send pipe.
    /// </summary>
    private async Task ProcessFramesAsync(CancellationToken cancellationToken)
    {
        await foreach (var frame in _outboundFrames.Reader.ReadAllAsync(cancellationToken))
        {
            // In a real implementation, you'd write the frame to the transport
            // For now, just notify
            FrameReady?.Invoke(frame);
        }
    }
    
    /// <summary>
    /// Gets the send pipe writer for writing frames to the wire.
    /// </summary>
    public PipeWriter GetSendWriter() => _sendPipe.Writer;
    
    /// <summary>
    /// Gets the receive pipe reader for reading frames from the wire.
    /// </summary>
    public PipeReader GetReceiveReader() => _receivePipe.Reader;
    
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        
        _cts.Cancel();
        
        try
        {
            await _processingTask.ConfigureAwait(false);
        }
        catch
        {
            // Ignore
        }
        
        _sendPipe.Writer.Complete();
        _receivePipe.Reader.Complete();
        _cts.Dispose();
        
        ConnectionClosed?.Invoke();
    }
}

/// <summary>
/// HTTP/2 Client Transport using the new connection implementation.
/// </summary>
public sealed class Http2ClientTransport : IFbThriftTransport, IAsyncDisposable
{
    private readonly Http2Connection _connection;
    private readonly Uri _serverUri;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private bool _disposed;
    
    public Http2ClientTransport(Uri serverUri, X509Certificate2? clientCertificate = null)
    {
        _serverUri = serverUri ?? throw new ArgumentNullException(nameof(serverUri));
        _connection = new Http2Connection(isClient: true);
        
        // Set up frame handling
        _connection.FrameReady += OnFrameReady;
        _connection.HeadersReceived += OnHeadersReceived;
        _connection.DataReceived += OnDataReceived;
    }
    
    private void OnFrameReady(Http2FrameHeader frame)
    {
        // Write frame to network
    }
    
    private void OnHeadersReceived(uint streamId, IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        // Handle response headers
    }
    
    private void OnDataReceived(uint streamId, ReadOnlyMemory<byte> data, bool endStream)
    {
        // Handle response data
    }
    
    public async ValueTask<ReadOnlyMemory<byte>> SendRequestAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        
        await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        
        try
        {
            var stream = _connection.CreateStream();
            
            // Send headers
            var headers = new List<KeyValuePair<string, string>>
            {
                new(":method", "POST"),
                new(":path", _serverUri.AbsolutePath),
                new(":scheme", _serverUri.Scheme),
                new(":authority", _serverUri.Authority),
                new("content-type", "application/x-thrift"),
                new("content-length", body.Length.ToString()),
                new("x-thrift-protocol", "compact")
            };
            
            _connection.SendHeaders(stream.StreamId, headers, endStream: false);
            _connection.SendData(stream.StreamId, body, endStream: true);
            
            // Wait for response (simplified - real implementation would use completion sources)
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            
            return Array.Empty<byte>(); // Placeholder
        }
        finally
        {
            _requestLock.Release();
        }
    }
    
    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        
        _requestLock.Dispose();
        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// HTTP/2 Server Transport.
/// </summary>
public sealed class Http2ServerTransport : IFbThriftTransport, IAsyncDisposable
{
    private readonly Http2Connection _connection;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> _requestHandler;
    private bool _disposed;
    
    public Http2ServerTransport(Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> requestHandler)
    {
        _requestHandler = requestHandler ?? throw new ArgumentNullException(nameof(requestHandler));
        _connection = new Http2Connection(isClient: false);
        
        _connection.HeadersReceived += OnHeadersReceived;
        _connection.DataReceived += OnDataReceived;
    }
    
    private void OnHeadersReceived(uint streamId, IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        // Handle incoming request headers
    }
    
    private async void OnDataReceived(uint streamId, ReadOnlyMemory<byte> data, bool endStream)
    {
        if (endStream)
        {
            try
            {
                var response = await _requestHandler(data, CancellationToken.None).ConfigureAwait(false);
                
                // Send response headers
                var respHeaders = new List<KeyValuePair<string, string>>
                {
                    new(":status", "200"),
                    new("content-type", "application/x-thrift"),
                    new("content-length", response.Length.ToString())
                };
                
                _connection.SendHeaders(streamId, respHeaders, endStream: false);
                _connection.SendData(streamId, response, endStream: true);
            }
            catch (Exception)
            {
                _connection.SendHeaders(streamId, 
                    new[] { new KeyValuePair<string, string>(":status", "500") }, 
                    endStream: true);
            }
        }
    }
    
    public ValueTask<ReadOnlyMemory<byte>> SendRequestAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Server transport cannot initiate requests.");
    
    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        
        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}
