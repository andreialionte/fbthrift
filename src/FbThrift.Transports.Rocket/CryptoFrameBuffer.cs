// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Rocket;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;

/// <summary>
/// Buffer for reassembling CRYPTO frames across multiple QUIC packets.
/// Required for Post-Quantum TLS where ClientHello can exceed single packet size (~1.5KB+).
/// Based on tquic's crypto buffer implementation.
/// See: https://github.com/Tencent/tquic/blob/develop/src/crypto.rs
/// </summary>
public sealed class CryptoFrameBuffer
{
    private readonly Dictionary<ulong, EncryptionLevelBuffer> _buffers = new();
    private readonly int _maxBufferSize;
    private readonly object _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="CryptoFrameBuffer"/> class.
    /// </summary>
    /// <param name="maxBufferSize">Maximum buffer size per encryption level.</param>
    public CryptoFrameBuffer(int maxBufferSize = 16384)
    {
        _maxBufferSize = maxBufferSize;
        
        // Initialize buffers for each encryption level
        foreach (EncryptionLevel level in Enum.GetValues(typeof(EncryptionLevel)))
        {
            _buffers[(ulong)level] = new EncryptionLevelBuffer(maxBufferSize);
        }
    }

    /// <summary>
    /// Adds CRYPTO frame data to the buffer for the given encryption level.
    /// </summary>
    /// <param name="level">The encryption level.</param>
    /// <param name="offset">The offset of the data in the handshake stream.</param>
    /// <param name="data">The crypto frame data.</param>
    /// <returns>True if data was added successfully, false if buffer would overflow.</returns>
    public bool AddData(EncryptionLevel level, ulong offset, ReadOnlyMemory<byte> data)
    {
        lock (_lock)
        {
            var buffer = _buffers[(ulong)level];
            return buffer.AddData(offset, data);
        }
    }

    /// <summary>
    /// Gets the complete handshake data for the given encryption level if fully received.
    /// </summary>
    /// <param name="level">The encryption level.</param>
    /// <returns>The complete handshake data, or null if not fully received.</returns>
    public byte[] GetCompleteData(EncryptionLevel level)
    {
        lock (_lock)
        {
            var buffer = _buffers[(ulong)level];
            return buffer.IsComplete ? buffer.Data.ToArray() : null;
        }
    }

    /// <summary>
    /// Gets the current buffered data for the given encryption level.
    /// </summary>
    /// <param name="level">The encryption level.</param>
    /// <returns>The currently buffered data (may be incomplete).</returns>
    public byte[] GetBufferedData(EncryptionLevel level)
    {
        lock (_lock)
        {
            var buffer = _buffers[(ulong)level];
            return buffer.Data.ToArray();
        }
    }

    /// <summary>
    /// Gets the number of bytes buffered for the given encryption level.
    /// </summary>
    public int GetBufferedLength(EncryptionLevel level)
    {
        lock (_lock)
        {
            var buffer = _buffers[(ulong)level];
            return (int)buffer.Data.Length;
        }
    }

    /// <summary>
    /// Clears the buffer for the given encryption level.
    /// </summary>
    public void Clear(EncryptionLevel level)
    {
        lock (_lock)
        {
            var buffer = _buffers[(ulong)level];
            buffer.Clear();
        }
    }

    /// <summary>
    /// Clears all buffers.
    /// </summary>
    public void ClearAll()
    {
        lock (_lock)
        {
            foreach (var buffer in _buffers.Values)
            {
                buffer.Clear();
            }
        }
    }

    /// <summary>
    /// Buffer for a single encryption level's CRYPTO frames.
    /// </summary>
    private sealed class EncryptionLevelBuffer
    {
        private readonly int _maxSize;
        private readonly MemoryStream _buffer = new();
        private ulong _expectedOffset = 0;
        private bool _isComplete = false;

        public EncryptionLevelBuffer(int maxSize)
        {
            _maxSize = maxSize;
        }

        public bool IsComplete => _isComplete;
        public MemoryStream Data => _buffer;

        public bool AddData(ulong offset, ReadOnlyMemory<byte> data)
        {
            // Check for buffer overflow
            if (_buffer.Length + data.Length > _maxSize)
            {
                return false; // Buffer would overflow
            }

            // Handle out-of-order data by writing at the correct offset
            if (offset == _expectedOffset)
            {
                // In-order data
                _buffer.Write(data.Span);
                _expectedOffset += (ulong)data.Length;
            }
            else if (offset < _expectedOffset)
            {
                // Retransmitted data we already have - ignore
                return true;
            }
            else
            {
                // Out-of-order data - for simplicity, we only handle in-order
                // A full implementation would use a gap buffer
                return false;
            }

            return true;
        }

        public void Clear()
        {
            _buffer.SetLength(0);
            _expectedOffset = 0;
            _isComplete = false;
        }

        public void MarkComplete()
        {
            _isComplete = true;
        }
    }
}

/// <summary>
/// QUIC encryption levels for CRYPTO frames.
/// </summary>
public enum EncryptionLevel : ulong
{
    Initial = 0,
    EarlyData = 1,
    Handshake = 2,
    Application = 3
}
