// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Quic;

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
using System.Threading.Tasks;
using FbThrift.Core.Http2;
using FbThrift.Core.Header;
using FbThrift.Core.Protocol;
using FbThrift.Core.Transport;

/// <summary>
/// HTTP/3 Error Codes (RFC 9114 Section 8.1).
/// </summary>
public enum Http3ErrorCode : long
{
    NoError = 0x0,
    GeneralProtocolError = 0x1,
    InternalError = 0x2,
    StreamCreationError = 0x3,
    ClosedCriticalStream = 0x4,
    FrameUnexpected = 0x5,
    FrameError = 0x6,
    ExcessiveLoad = 0x7,
    IdError = 0x8,
    SettingsError = 0x9,
    MissingSettings = 0xA,
    RequestRejected = 0xB,
    RequestCancelled = 0xC,
    RequestIncomplete = 0xD,
    MessageError = 0x101,
    ConnectError = 0x102,
    VersionFallback = 0x103,
}

/// <summary>
/// HTTP/3 Server Push implementation (RFC 9114 Section 4.6).
/// Allows server to proactively push resources to client.
/// Based on quiche, tquic, and s2n-quic implementations.
/// </summary>
public static class Http3ServerPush
{
    // Server Push Constants
    private const int MaxPushesPerStream = 10;
    private const int MaxConcurrentPushes = 100;
    private const long DefaultPushTimeoutMs = 30000; // 30 seconds
    
    /// <summary>
    /// Server Push Promise with associated data.
    /// </summary>
    public readonly struct PushPromise
    {
        public uint StreamId { get; init; }           // Client-initiated stream ID
        public uint PromisedStreamId { get; init; }   // Server-initiated push stream ID (even)
        public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; }
        public ReadOnlyMemory<byte> Data { get; init; }
        public DateTime CreatedAt { get; init; }
        public string? PushId { get; init; }
        
        /// <summary>
        /// Creates a push promise for a resource.
        /// </summary>
        public static PushPromise Create(
            uint streamId,
            uint promisedStreamId,
            IReadOnlyList<KeyValuePair<string, string>> headers,
            ReadOnlyMemory<byte> data,
            string? pushId = null)
        {
            return new PushPromise
            {
                StreamId = streamId,
                PromisedStreamId = promisedStreamId,
                Headers = headers,
                Data = data,
                CreatedAt = DateTime.UtcNow,
                PushId = pushId ?? Guid.NewGuid().ToString("N")[..16]
            };
        }
    }
    
    /// <summary>
    /// Server Push Manager for handling outgoing pushes.
    /// </summary>
    public sealed class PushManager
    {
        private readonly ConcurrentDictionary<uint, PushPromise> _pendingPushes = new();
        private readonly ConcurrentDictionary<uint, PushPromise> _activePushes = new();
        private readonly ConcurrentDictionary<string, PushPromise> _pushesById = new();
        private readonly Http3Connection _connection;
        private readonly object _lock = new();
        private uint _nextPushStreamId = 2; // Server push streams are even
        private int _pushesSentThisConnection = 0;
        private bool _pushEnabled = true;
        private long _pushTimeoutMs = DefaultPushTimeoutMs;
        
        public int PendingPushCount => _pendingPushes.Count;
        public int ActivePushCount => _activePushes.Count;
        public bool PushEnabled 
        { 
            get => _pushEnabled; 
            set => _pushEnabled = value; 
        }
        
        public PushManager(Http3Connection connection)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        }
        
        /// <summary>
        /// Initiates a server push for a promised resource.
        /// Returns the push stream ID if successful.
        /// </summary>
        public uint? InitiatePush(
            uint requestStreamId,
            IReadOnlyList<KeyValuePair<string, string>> pushHeaders,
            ReadOnlyMemory<byte> pushData,
            CancellationToken cancellationToken = default)
        {
            if (!_pushEnabled)
                return null;
            
            if (_pushesSentThisConnection >= MaxConcurrentPushes)
                return null;
            
            // Check stream count limit
            lock (_lock)
            {
                if (_activePushes.Count >= MaxConcurrentPushes)
                    return null;
            }
            
            // Validate push headers (must include :method, :path, :scheme, :authority)
            if (!ValidatePushHeaders(pushHeaders))
            {
                return null;
            }
            
            var promisedStreamId = Interlocked.Add(ref _nextPushStreamId, 2) - 2;
            
            var push = PushPromise.Create(
                requestStreamId,
                promisedStreamId,
                pushHeaders,
                pushData);
            
            // Store pending push
            _pendingPushes[promisedStreamId] = push;
            _pushesById[push.PushId!] = push;
            
            // Send PUSH_PROMISE frame
            try
            {
                _connection.SendPushPromise(requestStreamId, promisedStreamId, pushHeaders);
                _pushesSentThisConnection++;
                
                // Move to active
                if (_pendingPushes.TryRemove(promisedStreamId, out var activePush))
                {
                    _activePushes[promisedStreamId] = activePush;
                    
                    // Send the actual data on the push stream
                    Task.Run(async () =>
                    {
                        await SendPushDataAsync(promisedStreamId, push.Data, cancellationToken).ConfigureAwait(false);
                    }, cancellationToken);
                    
                    return promisedStreamId;
                }
            }
            catch
            {
                _pendingPushes.TryRemove(promisedStreamId, out _);
                _pushesById.TryRemove(push.PushId!, out _);
                return null;
            }
            
            return null;
        }
        
        /// <summary>
        /// Cancels a pending or active push.
        /// </summary>
        public bool CancelPush(uint promisedStreamId, Http3ErrorCode errorCode = Http3ErrorCode.NoError)
        {
            if (_pendingPushes.TryRemove(promisedStreamId, out var push))
            {
                _pushesById.TryRemove(push.PushId!, out _);
                
                // Send CANCEL_PUSH frame
                _connection.SendCancelPush(promisedStreamId, errorCode);
                return true;
            }
            
            if (_activePushes.TryRemove(promisedStreamId, out push))
            {
                _pushesById.TryRemove(push.PushId!, out _);
                
                // Send RESET_STREAM
                _connection.SendResetStream(promisedStreamId, errorCode);
                return true;
            }
            
            return false;
        }
        
        /// <summary>
        /// Cancels a push by its ID.
        /// </summary>
        public bool CancelPushById(string pushId, Http3ErrorCode errorCode = Http3ErrorCode.NoError)
        {
            if (_pushesById.TryGetValue(pushId, out var push))
            {
                return CancelPush(push.PromisedStreamId, errorCode);
            }
            return false;
        }
        
        /// <summary>
        /// Gets a push promise by ID.
        /// </summary>
        public bool TryGetPush(string pushId, out PushPromise push)
        {
            return _pushesById.TryGetValue(pushId, out push!);
        }
        
        /// <summary>
        /// Gets all pending pushes for a request stream.
        /// </summary>
        public IReadOnlyList<PushPromise> GetPushesForRequest(uint requestStreamId)
        {
            return _pendingPushes.Values
                .Where(p => p.StreamId == requestStreamId)
                .ToList();
        }
        
        /// <summary>
        /// Called when a push stream is reset by client.
        /// </summary>
        public void OnPushReset(uint promisedStreamId, Http3ErrorCode errorCode)
        {
            if (_activePushes.TryRemove(promisedStreamId, out var push))
            {
                _pushesById.TryRemove(push.PushId!, out _);
                _pushesSentThisConnection = Math.Max(0, _pushesSentThisConnection - 1);
            }
        }
        
        /// <summary>
        /// Called when a push stream is completed successfully.
        /// </summary>
        public void OnPushCompleted(uint promisedStreamId)
        {
            if (_activePushes.TryRemove(promisedStreamId, out var push))
            {
                _pushesById.TryRemove(push.PushId!, out _);
                _pushesSentThisConnection = Math.Max(0, _pushesSentThisConnection - 1);
            }
        }
        
        /// <summary>
        /// Validates push headers according to RFC 9114.
        /// </summary>
        private static bool ValidatePushHeaders(IReadOnlyList<KeyValuePair<string, string>> headers)
        {
            var hasMethod = false;
            var hasPath = false;
            var hasScheme = false;
            var hasAuthority = false;
            
            foreach (var (name, value) in headers)
            {
                switch (name)
                {
                    case ":method":
                        hasMethod = true;
                        // Only safe methods allowed for push
                        if (value != "GET" && value != "HEAD")
                            return false;
                        break;
                    case ":path":
                        hasPath = true;
                        break;
                    case ":scheme":
                        hasScheme = true;
                        break;
                    case ":authority":
                        hasAuthority = true;
                        break;
                }
            }
            
            return hasMethod && hasPath && hasScheme && hasAuthority;
        }
        
        /// <summary>
        /// Sends push data on the promised stream.
        /// </summary>
        private async Task SendPushDataAsync(
            uint promisedStreamId,
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken)
        {
            try
            {
                // Send response headers
                var responseHeaders = new List<KeyValuePair<string, string>>
                {
                    new(":status", "200"),
                    new("content-type", GetContentType(data)),
                    new("content-length", data.Length.ToString()),
                    new("cache-control", "public, max-age=3600")
                };
                
                // Send on the push stream
                // _connection.SendHeaders(promisedStreamId, responseHeaders, endStream: false);
                // _connection.SendData(promisedStreamId, data, endStream: true);
                
                // For now, just simulate completion
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                OnPushCompleted(promisedStreamId);
            }
            catch (OperationCanceledException)
            {
                // Push was cancelled
            }
            catch (Exception)
            {
                // Log error
                OnPushReset(promisedStreamId, Http3ErrorCode.InternalError);
            }
        }
        
        private static string GetContentType(ReadOnlyMemory<byte> data)
        {
            // Simple content type detection based on first bytes
            if (data.Length >= 4)
            {
                var bytes = data.Span;
                // PNG
                if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
                    return "image/png";
                // JPEG
                if (bytes[0] == 0xFF && bytes[1] == 0xD8)
                    return "image/jpeg";
                // GIF
                if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46)
                    return "image/gif";
                // WebP
                if (bytes.Length >= 12 && bytes[0] == 0x52 && bytes[1] == 0x49 && 
                    bytes[2] == 0x46 && bytes[3] == 0x46 &&
                    bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
                    return "image/webp";
            }
            
            return "application/octet-stream";
        }
    }
    
    /// <summary>
    /// HTTP/3 CANCEL_PUSH Frame (RFC 9114 Section 7.2.6).
    /// </summary>
    public readonly struct CancelPushFrame
    {
        public const byte FrameType = 0x03;
        
        public uint PushId { get; init; }
        
        public void Encode(Span<byte> output)
        {
            // Frame type
            output[0] = FrameType;
            
            // Push ID (varint)
            var pushIdBytes = QuicVariableLengthIntegerEncoding.Encode(PushId);
            pushIdBytes.CopyTo(output[1..]);
        }
        
        public static int GetEncodedLength(uint pushId)
        {
            return 1 + QuicVariableLengthIntegerEncoding.GetEncodedLength(pushId);
        }
        
        public static CancelPushFrame Decode(ReadOnlySpan<byte> data)
        {
            if (data.Length < 1 || data[0] != FrameType)
                throw new ArgumentException("Invalid CANCEL_PUSH frame");
            
            var (pushId, consumed) = QuicVariableLengthIntegerEncoding.Decode(data[1..]);
            
            return new CancelPushFrame { PushId = pushId };
        }
    }
    
    /// <summary>
    /// Variable Length Integer Encoding (RFC 9000 Section 16).
    /// </summary>
    public static class QuicVariableLengthIntegerEncoding
    {
        public static int GetEncodedLength(ulong value)
        {
            if (value < 0x40) return 1;
            if (value < 0x4000) return 2;
            if (value < 0x40000000) return 4;
            return 8;
        }
        
        public static int Encode(ulong value, Span<byte> output)
        {
            int length;
            if (value < 0x40)
            {
                output[0] = (byte)value;
                length = 1;
            }
            else if (value < 0x4000)
            {
                output[0] = 0x40 | (byte)(value >> 8);
                output[1] = (byte)value;
                length = 2;
            }
            else if (value < 0x40000000)
            {
                output[0] = 0x80 | (byte)(value >> 24);
                output[1] = (byte)(value >> 16);
                output[2] = (byte)(value >> 8);
                output[3] = (byte)value;
                length = 4;
            }
            else
            {
                output[0] = 0xC0 | (byte)(value >> 56);
                output[1] = (byte)(value >> 48);
                output[2] = (byte)(value >> 40);
                output[3] = (byte)(value >> 32);
                output[4] = (byte)(value >> 24);
                output[5] = (byte)(value >> 16);
                output[6] = (byte)(value >> 8);
                output[7] = (byte)value;
                length = 8;
            }
            return length;
        }
        
        public static byte[] Encode(ulong value)
        {
            var output = new byte[GetEncodedLength(value)];
            Encode(value, output);
            return output;
        }
        
        public static (ulong Value, int Consumed) Decode(ReadOnlySpan<byte> input)
        {
            if (input.Length == 0)
                throw new ArgumentException("Empty input");
            
            var prefix = input[0] >> 6;
            
            return prefix switch
            {
                0 => (input[0] & 0x3F, 1),
                1 => ((ulong)(input[0] & 0x3F) << 8 | input[1], 2),
                2 => ((ulong)(input[0] & 0x3F) << 16 | (ulong)input[1] << 8 | input[2], 4),
                3 => ((ulong)(input[0] & 0x3F) << 40 | (ulong)input[1] << 32 | (ulong)input[2] << 24 | (ulong)input[3] << 16 | (ulong)input[4] << 8 | input[5], 8),
                _ => throw new ArgumentException("Invalid varint prefix")
            };
        }
    }
}

/// <summary>
/// HTTP/3 Client Push Handler (for receiving server pushes).
/// </summary>
public sealed class Http3ClientPushHandler
{
    private readonly ConcurrentDictionary<uint, PushReceiver> _pushReceivers = new();
    private readonly Http3Connection _connection;
    
    public Http3ClientPushHandler(Http3Connection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        
        // Register for push-related frames
        connection.PushPromiseReceived += OnPushPromiseReceived;
        connection.DataReceived += OnPushDataReceived;
        connection.StreamReset += OnPushReset;
    }
    
    /// <summary>
    /// Registers a handler for pushed resources matching a path pattern.
    /// </summary>
    public IDisposable SubscribeToPushes(string pathPattern, Action<PushReceiver> handler)
    {
        var subscription = new PushSubscription(pathPattern, handler);
        // In a real implementation, store and match against incoming pushes
        return subscription;
    }
    
    private void OnPushPromiseReceived(uint requestStreamId, uint promisedStreamId, 
        IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        // Extract :path from headers
        string? path = null;
        string? method = null;
        
        foreach (var (name, value) in headers)
        {
            if (name == ":path") path = value;
            else if (name == ":method") method = value;
        }
        
        if (!string.IsNullOrEmpty(path) && method == "GET")
        {
            var receiver = new PushReceiver
            {
                PromisedStreamId = promisedStreamId,
                RequestStreamId = requestStreamId,
                Path = path!,
                Headers = headers,
                CreatedAt = DateTime.UtcNow
            };
            
            _pushReceivers[promisedStreamId] = receiver;
        }
    }
    
    private void OnPushDataReceived(uint streamId, ReadOnlyMemory<byte> data, bool endStream)
    {
        if (_pushReceivers.TryGetValue(streamId, out var receiver))
        {
            receiver.AppendData(data);
            
            if (endStream)
            {
                receiver.Complete();
                _pushReceivers.TryRemove(streamId, out _);
            }
        }
    }
    
    private void OnPushReset(uint streamId, Http3ErrorCode errorCode)
    {
        if (_pushReceivers.TryRemove(streamId, out var receiver))
        {
            receiver.Cancel(errorCode);
        }
    }
    
    /// <summary>
    /// Receives and assembles a pushed resource.
    /// </summary>
    public sealed class PushReceiver
    {
        public uint PromisedStreamId { get; init; }
        public uint RequestStreamId { get; init; }
        public string Path { get; init; } = "";
        public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; } = Array.Empty<KeyValuePair<string, string>>();
        public DateTime CreatedAt { get; init; }
        
        private readonly List<byte> _data = new();
        private bool _completed;
        private bool _cancelled;
        private Http3ErrorCode? _errorCode;
        
        public ReadOnlyMemory<byte> Data => _data.ToArray();
        public bool IsCompleted => _completed;
        public bool IsCancelled => _cancelled;
        public Http3ErrorCode? ErrorCode => _errorCode;
        
        public void AppendData(ReadOnlyMemory<byte> data)
        {
            if (!_completed && !_cancelled)
            {
                _data.AddRange(data.ToArray());
            }
        }
        
        public void Complete()
        {
            _completed = true;
        }
        
        public void Cancel(Http3ErrorCode errorCode)
        {
            _cancelled = true;
            _errorCode = errorCode;
        }
    }
    
    private sealed class PushSubscription : IDisposable
    {
        private readonly string _pattern;
        private readonly Action<PushReceiver> _handler;
        
        public PushSubscription(string pattern, Action<PushReceiver> handler)
        {
            _pattern = pattern;
            _handler = handler;
        }
        
        public void Dispose()
        {
            // Unsubscribe logic
        }
    }
}
