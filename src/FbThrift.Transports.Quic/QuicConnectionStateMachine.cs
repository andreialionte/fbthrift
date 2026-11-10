// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Quic;

using System;
using System.Collections.Generic;
using System.Net.Quic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

/// <summary>
/// QUIC connection state machine following tquic's connection.rs
/// See: https://github.com/Tencent/tquic/blob/develop/src/connection.rs
/// </summary>
public enum QuicConnectionState
{
    Initial,
    Handshaking,
    Connected,
    Closing,
    Closed,
    Draining
}

/// <summary>
/// QUIC connection state machine following tquic's Connection state machine.
/// See: https://github.com/Tencent/tquic/blob/develop/src/connection.rs
/// </summary>
public sealed class QuicConnectionStateMachine
{
    private QuicConnectionState _state = QuicConnectionState.Initial;
    private readonly object _lock = new();
    private long _localConnectionId;
    private long _remoteConnectionId;
    private long _nextStreamId = 0;
    private long _maxInboundBidirectionalStreams = 100;
    private long _maxInboundUnidirectionalStreams = 0;
    private long _maxOutboundBidirectionalStreams = 100;
    private long _maxOutboundUnidirectionalStreams = 100;
    private long _localMaxData = 1024 * 1024;
    private long _remoteMaxData = 1024 * 1024;
    private readonly Dictionary<long, StreamState> _streams = new();
    private long _bytesSent = 0;
    private long _bytesReceived = 0;
    private DateTime _lastActivity = DateTime.UtcNow;
    private readonly Timer _idleTimer;
    private readonly TimeSpan _idleTimeout = TimeSpan.FromSeconds(30);
    private long _ptoCount = 0;
    private DateTime? _lossTime;

    public QuicConnectionStateMachine()
    {
        _idleTimer = new Timer(OnIdleTimeout, null, _idleTimeout, _idleTimeout);
    }

    public QuicConnectionState State
    {
        get
        {
            lock (_lock) return _state;
        }
    }

    public long LocalConnectionId
    {
        get { lock (_lock) return _localConnectionId; }
        set { lock (_lock) _localConnectionId = value; }
    }

    public long RemoteConnectionId
    {
        get { lock (_lock) return _remoteConnectionId; }
        set { lock (_lock) _remoteConnectionId = value; }
    }

    public long MaxInboundBidirectionalStreams
    {
        get { lock (_lock) return _maxInboundBidirectionalStreams; }
        set { lock (_lock) _maxInboundBidirectionalStreams = value; }
    }

    public long MaxInboundUnidirectionalStreams
    {
        get { lock (_lock) return _maxInboundUnidirectionalStreams; }
        set { lock (_lock) _maxInboundUnidirectionalStreams = value; }
    }

    public long MaxOutboundBidirectionalStreams
    {
        get { lock (_lock) return _maxOutboundBidirectionalStreams; }
        set { lock (_lock) _maxOutboundBidirectionalStreams = value; }
    }

    public long MaxOutboundUnidirectionalStreams
    {
        get { lock (_lock) return _maxOutboundUnidirectionalStreams; }
        set { lock (_lock) _maxOutboundUnidirectionalStreams = value; }
    }

    public long LocalMaxData
    {
        get { lock (_lock) return _localMaxData; }
        set { lock (_lock) _localMaxData = value; }
    }

    public long RemoteMaxData
    {
        get { lock (_lock) return _remoteMaxData; }
        set { lock (_lock) _remoteMaxData = value; }
    }

    public long NextStreamId
    {
        get
        {
            lock (_lock)
            {
                var id = _nextStreamId;
                _nextStreamId += 4; // Client uses even IDs, server uses odd
                return id;
            }
        }
    }

    public long BytesSent
    {
        get { lock (_lock) return _bytesSent; }
        set { lock (_lock) _bytesSent = value; }
    }

    public long BytesReceived
    {
        get { lock (_lock) return _bytesReceived; }
        set { lock (_lock) _bytesReceived = value; }
    }

    public DateTime LastActivity
    {
        get { lock (_lock) return _lastActivity; }
        set { lock (_lock) _lastActivity = value; }
    }

    public long PtoCount
    {
        get { lock (_lock) return _ptoCount; }
        set { lock (_lock) _ptoCount = value; }
    }

    public DateTime? LossTime
    {
        get { lock (_lock) return _lossTime; }
        set { lock (_lock) _lossTime = value; }
    }

    public IReadOnlyDictionary<long, StreamState> Streams
    {
        get
        {
            lock (_lock) return new Dictionary<long, StreamState>(_streams);
        }
    }

    public bool CanOpenStream(bool bidi)
    {
        lock (_lock)
        {
            if (bidi)
            {
                return _streams.Count(s => s.Value.Bidi) < MaxOutboundBidirectionalStreams;
            }
            else
            {
                return _streams.Count(s => !s.Value.Bidi) < MaxOutboundUnidirectionalStreams;
            }
        }
    }

    public bool TryCreateStream(out long streamId, bool bidi = true)
    {
        lock (_lock)
        {
            if (bidi)
            {
                if (_streams.Count(s => s.Value.Bidi) >= MaxOutboundBidirectionalStreams)
                {
                    streamId = 0;
                    return false;
                }
            }
            else
            {
                if (_streams.Count(s => !s.Value.Bidi) >= MaxOutboundUnidirectionalStreams)
                {
                    streamId = 0;
                    return false;
                }
            }

            var id = _nextStreamId;
            _nextStreamId += 4;
            streamId = id;
            _streams[id] = new StreamState { Id = id, Bidi = bidi, State = StreamStateEnum.Ready };
            return true;
        }
    }

    public bool TryGetStream(long streamId, out StreamState streamState)
    {
        lock (_lock) return _streams.TryGetValue(streamId, out streamState);
    }

    public void UpdateStreamState(long streamId, StreamStateEnum newState)
    {
        lock (_lock)
        {
            if (_streams.TryGetValue(streamId, out var stream))
            {
                stream.State = newState;
            }
        }
    }

    public void RecordActivity()
    {
        lock (_lock) _lastActivity = DateTime.UtcNow;
    }

    public void RecordBytesSent(long bytes)
    {
        lock (_lock) _bytesSent += bytes;
    }

    public void RecordBytesReceived(long bytes)
    {
        lock (_lock) _bytesReceived += bytes;
    }

    public void IncrementPtoCount()
    {
        lock (_lock) _ptoCount++;
    }

    public void SetLossTime(DateTime? lossTime)
    {
        lock (_lock) _lossTime = lossTime;
    }

    public bool IsIdle()
    {
        lock (_lock) return (DateTime.UtcNow - _lastActivity) > _idleTimeout;
    }

    public void Close()
    {
        lock (_lock)
        {
            _state = QuicConnectionState.Closing;
            _idleTimer?.Dispose();
        }
    }

    public void Drain()
    {
        lock (_lock)
        {
            if (_state == QuicConnectionState.Closing)
            {
                _state = QuicConnectionState.Draining;
            }
        }
    }

    public void CloseComplete()
    {
        lock (_lock)
        {
            _state = QuicConnectionState.Closed;
            _idleTimer?.Dispose();
        }
    }

    private void OnIdleTimeout(object? state)
    {
        lock (_lock)
        {
            if (_state == QuicConnectionState.Connected && IsIdle())
            {
                _state = QuicConnectionState.Closing;
                // Would trigger connection close with idle timeout error
            }
        }
    }

    public void Dispose()
    {
        _idleTimer?.Dispose();
    }
}

public enum StreamStateEnum
{
    Ready,
    Sending,
    Receiving,
    SendComplete,
    ReceiveComplete,
    ResetSent,
    ResetReceived,
    Closed
}

public sealed class StreamState
{
    public long Id { get; set; }
    public bool Bidi { get; set; }
    public StreamStateEnum State { get; set; }
    public long BytesSent { get; set; }
    public long BytesReceived { get; set; }
    public long SendOffset { get; set; }
    public long ReceiveOffset { get; set; }
    public long MaxSendOffset { get; set; }
    public long MaxReceiveOffset { get; set; }
    public bool FinSent { get; set; }
    public bool FinReceived { get; set; }
    public long SendWindow { get; set; } = 16384;
    public long ReceiveWindow { get; set; } = 16384;
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
}
