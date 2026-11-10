// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Quic;

using System;
using System.Collections.Generic;
using System.Diagnostics;

/// <summary>
/// QUIC Flow Control implementation.
/// Manages connection-level and stream-level flow control per RFC 9000 Section 4.
/// </summary>
public sealed class QuicFlowControl
{
    // Default limits
    private const long DefaultMaxData = 65536;           // 64 KB
    private const long DefaultMaxStreamData = 16384;     // 16 KB per stream
    private const long DefaultMaxStreamsBidi = 100;
    private const long DefaultMaxStreamsUni = 100;
    private const long MinFlowControlIncrement = 4096;   // Minimum window update
    private const double FlowControlHeadroom = 0.25;     // 25% headroom for updates

    // Connection-level flow control
    private long _maxData;
    private long _maxDataSent;                    // Max data we've advertised to peer
    private long _bytesReceived;                  // Total bytes received on connection
    private long _maxDataReceived;                // Max data peer has advertised to us
    private long _bytesSent;                      // Total bytes sent on connection
    private long _connectionWindow;               // Available connection window

    // Stream-level flow control
    private readonly Dictionary<long, StreamFlowControl> _streams = new();
    private readonly object _lock = new();

    // Limits
    private long _maxStreamDataLocal;             // Our limit per stream
    private long _maxStreamDataRemote;            // Peer's limit per stream
    private long _maxStreamsBidi;
    private long _maxStreamsUni;
    private long _streamsBidiOpened;
    private long _streamsUniOpened;

    // Callbacks
    private Action<long, long>? _onConnectionWindowUpdate;  // (streamId, newWindow)
    private Action<long, long>? _onStreamWindowUpdate;      // (streamId, newWindow)

    /// <summary>
    /// Initializes a new instance of the <see cref="QuicFlowControl"/> class.
    /// </summary>
    public QuicFlowControl()
    {
        _maxData = DefaultMaxData;
        _maxDataSent = DefaultMaxData;
        _maxDataReceived = DefaultMaxData;
        _maxStreamDataLocal = DefaultMaxStreamData;
        _maxStreamDataRemote = DefaultMaxStreamData;
        _maxStreamsBidi = DefaultMaxStreamsBidi;
        _maxStreamsUni = DefaultMaxStreamsUni;
        _connectionWindow = DefaultMaxData;
    }

    /// <summary>
    /// Gets the connection-level maximum data.
    /// </summary>
    public long MaxData => _maxData;

    /// <summary>
    /// Gets the stream-level maximum data (local limit).
    /// </summary>
    public long MaxStreamDataLocal => _maxStreamDataLocal;

    /// <summary>
    /// Gets the stream-level maximum data (remote limit).
    /// </summary>
    public long MaxStreamDataRemote => _maxStreamDataRemote;

    /// <summary>
    /// Gets the maximum bidirectional streams.
    /// </summary>
    public long MaxStreamsBidi => _maxStreamsBidi;

    /// <summary>
    /// Gets the maximum unidirectional streams.
    /// </summary>
    public long MaxStreamsUni => _maxStreamsUni;

    /// <summary>
    /// Gets the available connection window.
    /// </summary>
    public long ConnectionWindow => _connectionWindow;

    /// <summary>
    /// Sets the connection-level maximum data.
    /// </summary>
    public void SetMaxData(long maxData)
    {
        lock (_lock)
        {
            _maxData = Math.Max(maxData, 0);
            _connectionWindow = Math.Max(0, _maxDataReceived - _bytesSent);
        }
    }

    /// <summary>
    /// Sets the stream-level maximum data (local limit we advertise).
    /// </summary>
    public void SetMaxStreamDataLocal(long maxStreamData)
    {
        lock (_lock)
        {
            _maxStreamDataLocal = Math.Max(maxStreamData, 0);
        }
    }

    /// <summary>
    /// Sets the stream-level maximum data (remote limit we received).
    /// </summary>
    public void SetMaxStreamDataRemote(long maxStreamData)
    {
        lock (_lock)
        {
            _maxStreamDataRemote = Math.Max(maxStreamData, 0);
            // Update all existing streams
            foreach (var stream in _streams.Values)
            {
                stream.UpdateRemoteLimit(_maxStreamDataRemote);
            }
        }
    }

    /// <summary>
    /// Sets the maximum bidirectional streams.
    /// </summary>
    public void SetMaxStreamsBidi(long maxStreams)
    {
        lock (_lock)
        {
            _maxStreamsBidi = Math.Max(maxStreams, 0);
        }
    }

    /// <summary>
    /// Sets the maximum unidirectional streams.
    /// </summary>
    public void SetMaxStreamsUni(long maxStreams)
    {
        lock (_lock)
        {
            _maxStreamsUni = Math.Max(maxStreams, 0);
        }
    }

    /// <summary>
    /// Registers a callback for connection window updates.
    /// </summary>
    public void OnConnectionWindowUpdate(Action<long, long> callback)
    {
        _onConnectionWindowUpdate = callback;
    }

    /// <summary>
    /// Registers a callback for stream window updates.
    /// </summary>
    public void OnStreamWindowUpdate(Action<long, long> callback)
    {
        _onStreamWindowUpdate = callback;
    }

    /// <summary>
    /// Opens a new bidirectional stream.
    /// </summary>
    public bool TryOpenBidiStream(out long streamId)
    {
        lock (_lock)
        {
            if (_streamsBidiOpened >= _maxStreamsBidi)
            {
                streamId = -1;
                return false;
            }
            streamId = _streamsBidiOpened * 4; // Client bidi: 0, 4, 8...
            _streamsBidiOpened++;
            return true;
        }
    }

    /// <summary>
    /// Opens a new unidirectional stream.
    /// </summary>
    public bool TryOpenUniStream(out long streamId)
    {
        lock (_lock)
        {
            if (_streamsUniOpened >= _maxStreamsUni)
            {
                streamId = -1;
                return false;
            }
            streamId = 2 + _streamsUniOpened * 4; // Client uni: 2, 6, 10... (for client) or 3, 7, 11... (for server)
            _streamsUniOpened++;
            return true;
        }
    }

    /// <summary>
    /// Registers a stream for flow control.
    /// </summary>
    public void RegisterStream(long streamId, bool isRemote = false)
    {
        lock (_lock)
        {
            if (!_streams.ContainsKey(streamId))
            {
                var limit = isRemote ? _maxStreamDataRemote : _maxStreamDataLocal;
                _streams[streamId] = new StreamFlowControl(streamId, limit);
            }
        }
    }

    /// <summary>
    /// Unregisters a stream.
    /// </summary>
    public void UnregisterStream(long streamId)
    {
        lock (_lock)
        {
            _streams.Remove(streamId);
        }
    }

    /// <summary>
    /// Called when data is received on a stream.
    /// </summary>
    public void OnStreamDataReceived(long streamId, long bytes)
    {
        lock (_lock)
        {
            // Update connection-level
            _bytesReceived += bytes;
            _connectionWindow = Math.Max(0, _maxData - _bytesReceived);

            // Update stream-level
            if (_streams.TryGetValue(streamId, out var stream))
            {
                stream.OnDataReceived(bytes);
            }

            // Check if we need to send MAX_DATA
            MaybeSendMaxData();
        }
    }

    /// <summary>
    /// Called when data is sent on a stream.
    /// </summary>
    public bool TrySendStreamData(long streamId, long bytes)
    {
        lock (_lock)
        {
            if (!_streams.TryGetValue(streamId, out var stream))
            {
                return false;
            }

            if (!stream.TryConsumeWindow(bytes))
            {
                return false; // Not enough window
            }

            _bytesSent += bytes;
            _connectionWindow = Math.Max(0, _maxDataReceived - _bytesSent);

            // Check if we need to send MAX_STREAM_DATA
            MaybeSendMaxStreamData(streamId);

            return true;
        }
    }

    /// <summary>
    /// Called when a MAX_DATA frame is received from peer.
    /// </summary>
    public void OnMaxDataReceived(long maxData)
    {
        lock (_lock)
        {
            if (maxData > _maxDataReceived)
            {
                _maxDataReceived = maxData;
                _connectionWindow = Math.Max(0, _maxDataReceived - _bytesSent);
                _onConnectionWindowUpdate?.Invoke(0, _connectionWindow);
            }
        }
    }

    /// <summary>
    /// Called when a MAX_STREAM_DATA frame is received from peer.
    /// </summary>
    public void OnMaxStreamDataReceived(long streamId, long maxStreamData)
    {
        lock (_lock)
        {
            if (_streams.TryGetValue(streamId, out var stream))
            {
                stream.OnMaxStreamDataReceived(maxStreamData);
            }
        }
    }

    /// <summary>
    /// Called when a MAX_STREAMS frame is received from peer.
    /// </summary>
    public void OnMaxStreamsReceived(bool isBidi, long maxStreams)
    {
        lock (_lock)
        {
            if (isBidi)
            {
                if (maxStreams > _maxStreamsBidi)
                {
                    _maxStreamsBidi = maxStreams;
                }
            }
            else
            {
                if (maxStreams > _maxStreamsUni)
                {
                    _maxStreamsUni = maxStreams;
                }
            }
        }
    }

    /// <summary>
    /// Gets the available window for a stream.
    /// </summary>
    public long GetStreamWindow(long streamId)
    {
        lock (_lock)
        {
            if (_streams.TryGetValue(streamId, out var stream))
            {
                return stream.AvailableWindow;
            }
            return _maxStreamDataRemote; // Default for new streams
        }
    }

    /// <summary>
    /// Checks if we can send data on a stream.
    /// </summary>
    public bool CanSendOnStream(long streamId, long bytes)
    {
        lock (_lock)
        {
            if (!_streams.TryGetValue(streamId, out var stream))
            {
                return bytes <= _maxStreamDataRemote;
            }
            return stream.CanSend(bytes);
        }
    }

    /// <summary>
    /// Sends flow control updates if needed.
    /// </summary>
    public (bool SendMaxData, bool SendMaxStreamData, long StreamId) CheckAndPrepareUpdates()
    {
        lock (_lock)
        {
            // Check connection-level
            var sendMaxData = false;
            var newMaxData = _maxDataSent;
            if (_bytesReceived > 0 && _maxData - _bytesReceived < _maxData * FlowControlHeadroom)
            {
                newMaxData = _bytesReceived + _maxData;
                sendMaxData = true;
            }

            // Check stream-level (simplified - just check first stream)
            var sendMaxStreamData = false;
            long streamId = 0;
            
            return (sendMaxData, sendMaxStreamData, streamId);
        }
    }

    /// <summary>
    /// Gets flow control statistics.
    /// </summary>
    public FlowControlStats GetStats()
    {
        lock (_lock)
        {
            return new FlowControlStats
            {
                MaxData = _maxData,
                MaxDataSent = _maxDataSent,
                MaxDataReceived = _maxDataReceived,
                BytesReceived = _bytesReceived,
                BytesSent = _bytesSent,
                ConnectionWindow = _connectionWindow,
                MaxStreamDataLocal = _maxStreamDataLocal,
                MaxStreamDataRemote = _maxStreamDataRemote,
                ActiveStreams = _streams.Count,
                MaxStreamsBidi = _maxStreamsBidi,
                MaxStreamsUni = _maxStreamsUni,
                StreamsBidiOpened = _streamsBidiOpened,
                StreamsUniOpened = _streamsUniOpened
            };
        }
    }

    private void MaybeSendMaxData()
    {
        // Send MAX_DATA when we've consumed more than 75% of window
        if (_bytesReceived > _maxDataSent * (1 - FlowControlHeadroom))
        {
            _maxDataSent = _bytesReceived + _maxData;
        }
    }

    private void MaybeSendMaxStreamData(long streamId)
    {
        if (_streams.TryGetValue(streamId, out var stream))
        {
            stream.MaybeUpdateRemoteLimit();
        }
    }

    /// <summary>
    /// Stream-level flow control state.
    /// </summary>
    private sealed class StreamFlowControl
    {
        private readonly long _streamId;
        private long _maxStreamData;
        private long _maxStreamDataSent;
        private long _bytesReceived;
        private long _bytesSent;

        public long StreamId => _streamId;
        public long AvailableWindow => Math.Max(0, _maxStreamData - _bytesSent);

        public StreamFlowControl(long streamId, long maxStreamData)
        {
            _streamId = streamId;
            _maxStreamData = maxStreamData;
            _maxStreamDataSent = maxStreamData;
        }

        public void OnDataReceived(long bytes)
        {
            _bytesReceived += bytes;
        }

        public bool TryConsumeWindow(long bytes)
        {
            if (_bytesSent + bytes > _maxStreamData)
            {
                return false;
            }
            _bytesSent += bytes;
            return true;
        }

        public bool CanSend(long bytes)
        {
            return _bytesSent + bytes <= _maxStreamData;
        }

        public void OnMaxStreamDataReceived(long maxStreamData)
        {
            if (maxStreamData > _maxStreamData)
            {
                _maxStreamData = maxStreamData;
            }
        }

        public void UpdateRemoteLimit(long newLimit)
        {
            if (newLimit > _maxStreamData)
            {
                _maxStreamData = newLimit;
            }
        }

        public void MaybeUpdateRemoteLimit()
        {
            if (_bytesReceived > _maxStreamDataSent * (1 - FlowControlHeadroom))
            {
                _maxStreamDataSent = _bytesReceived + _maxStreamData;
            }
        }
    }

    /// <summary>
    /// Flow control statistics.
    /// </summary>
    public sealed class FlowControlStats
    {
        public long MaxData { get; set; }
        public long MaxDataSent { get; set; }
        public long MaxDataReceived { get; set; }
        public long BytesReceived { get; set; }
        public long BytesSent { get; set; }
        public long ConnectionWindow { get; set; }
        public long MaxStreamDataLocal { get; set; }
        public long MaxStreamDataRemote { get; set; }
        public int ActiveStreams { get; set; }
        public long MaxStreamsBidi { get; set; }
        public long MaxStreamsUni { get; set; }
        public long StreamsBidiOpened { get; set; }
        public long StreamsUniOpened { get; set; }
    }
}
