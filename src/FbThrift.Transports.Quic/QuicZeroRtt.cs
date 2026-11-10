// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Quic;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// QUIC 0-RTT (Zero Round Trip Time) support.
/// Implements early data, session tickets, and anti-replay protection.
/// Based on RFC 9001 Section 8 and RFC 9000 Section 17.2.2.
/// </summary>
public sealed class QuicZeroRtt
{
    // 0-RTT constants
    private const int DefaultMaxEarlyDataSize = 16384;        // Max early data per RFC 9000
    private const int TicketAgeAdd = 1000;              // Ticket age add (ms)
    private const long TicketLifetimeMs = 3600_000;     // 1 hour default ticket lifetime
    private const int MaxTicketSize = 4096;             // Max ticket size
    private const int AntiReplayWindowSize = 10000;     // Anti-replay window size

    // Ticket encryption keys (rotated periodically)
    private readonly byte[] _ticketKeyCurrent;
    private readonly byte[] _ticketKeyPrevious;
    private readonly byte[] _ticketKeyNext;
    private long _keyRotationTime;

    // Anti-replay protection
    private readonly HashSet<string> _usedTickets = new();
    private readonly Queue<string> _ticketQueue = new();

    // Early data tracking
    private long _earlyDataAccepted;
    private long _earlyDataRejected;
    private long _replayAttempts;

    // State
    private bool _zeroRttEnabled = true;
    private long _maxEarlyDataSize = DefaultMaxEarlyDataSize;

    /// <summary>
    /// Initializes a new instance of the <see cref="QuicZeroRtt"/> class.
    /// </summary>
    public QuicZeroRtt()
    {
        // Generate ticket encryption keys
        using var rng = RandomNumberGenerator.Create();
        _ticketKeyCurrent = new byte[32];
        _ticketKeyPrevious = new byte[32];
        _ticketKeyNext = new byte[32];
        rng.GetBytes(_ticketKeyCurrent);
        rng.GetBytes(_ticketKeyPrevious);
        rng.GetBytes(_ticketKeyNext);
        _keyRotationTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 12 * 60 * 60 * 1000; // 12 hours
    }

    /// <summary>
    /// Gets whether 0-RTT is enabled.
    /// </summary>
    public bool ZeroRttEnabled => _zeroRttEnabled;

    /// <summary>
    /// Gets the maximum early data size in bytes.
    /// </summary>
    public long MaxEarlyDataSize => _maxEarlyDataSize;

    /// <summary>
    /// Gets the number of early data bytes accepted.
    /// </summary>
    public long EarlyDataAccepted => _earlyDataAccepted;

    /// <summary>
    /// Gets the number of early data bytes rejected.
    /// </summary>
    public long EarlyDataRejected => _earlyDataRejected;

    /// <summary>
    /// Gets the number of replay attempts detected.
    /// </summary>
    public long ReplayAttempts => _replayAttempts;

    /// <summary>
    /// Enables or disables 0-RTT.
    /// </summary>
    public void SetZeroRttEnabled(bool enabled)
    {
        _zeroRttEnabled = enabled;
    }

    /// <summary>
    /// Sets the maximum early data size.
    /// </summary>
    public void SetMaxEarlyDataSize(long size)
    {
        _maxEarlyDataSize = Math.Max(0, Math.Min(size, DefaultMaxEarlyDataSize));
    }

    /// <summary>
    /// Creates a new session ticket for 0-RTT resumption.
    /// </summary>
    /// <param name="connectionId">The connection ID.</param>
    /// <param name="clientAddress">The client's IP address.</param>
    /// <param name="earlyDataLimit">Maximum early data allowed for this ticket.</param>
    /// <param name="alpn">The ALPN protocol negotiated.</param>
    /// <param name="cipherSuite">The TLS cipher suite used.</param>
    /// <param name="currentTime">Current time in milliseconds.</param>
    /// <returns>The encoded session ticket.</returns>
    public SessionTicket CreateTicket(
        ReadOnlyMemory<byte> connectionId,
        string clientAddress,
        long earlyDataLimit,
        string alpn,
        ushort cipherSuite,
        long currentTime)
    {
        var ticket = new SessionTicket
        {
            ConnectionId = connectionId.ToArray(),
            ClientAddress = clientAddress,
            EarlyDataLimit = Math.Min(earlyDataLimit, _maxEarlyDataSize),
            Alpn = alpn,
            CipherSuite = cipherSuite,
            CreationTime = currentTime,
            Lifetime = TicketLifetimeMs,
            TicketAgeAdd = TicketAgeAdd,
            KeyPhase = 0 // Current key
        };

        return ticket;
    }

    /// <summary>
    /// Encodes a session ticket for transmission.
    /// </summary>
    public byte[] EncodeTicket(SessionTicket ticket)
    {
        var writer = new ArrayBufferWriter<byte>();
        
        // Ticket version (1 byte)
        writer.Write(new byte[] { 1 });
        
        // Key phase (1 byte)
        writer.Write(new byte[] { (byte)ticket.KeyPhase });
        
        // Creation time (8 bytes, milliseconds since epoch)
        WriteVarint(writer, (ulong)ticket.CreationTime);
        
        // Lifetime (4 bytes, milliseconds)
        WriteVarint(writer, (ulong)ticket.Lifetime);
        
        // Ticket age add (4 bytes, milliseconds)
        WriteVarint(writer, (ulong)ticket.TicketAgeAdd);
        
        // Early data limit (4 bytes)
        WriteVarint(writer, (ulong)ticket.EarlyDataLimit);
        
        // Connection ID
        WriteVarint(writer, (ulong)ticket.ConnectionId.Length);
        writer.Write(ticket.ConnectionId);
        
        // Client address
        var addrBytes = Encoding.UTF8.GetBytes(ticket.ClientAddress);
        WriteVarint(writer, (ulong)addrBytes.Length);
        writer.Write(addrBytes);
        
        // ALPN
        var alpnBytes = Encoding.UTF8.GetBytes(ticket.Alpn);
        WriteVarint(writer, (ulong)alpnBytes.Length);
        writer.Write(alpnBytes);
        
        // Cipher suite (2 bytes)
        writer.Write(new byte[] { (byte)(ticket.CipherSuite >> 8), (byte)(ticket.CipherSuite & 0xFF) });
        
        // Reserved for future extensions
        WriteVarint(writer, 0);
        
        // Encrypt the ticket
        var plaintext = writer.WrittenMemory;
        var ciphertext = EncryptTicket(plaintext, ticket.KeyPhase);
        
        // Write encrypted ticket length and data
        var output = new ArrayBufferWriter<byte>();
        WriteVarint(output, (ulong)ciphertext.Length);
        output.Write(ciphertext);
        
        return output.WrittenMemory.ToArray();
    }

    /// <summary>
    /// Decodes and decrypts a session ticket.
    /// </summary>
    public (SessionTicket Ticket, bool Success) DecodeTicket(ReadOnlyMemory<byte> data)
    {
        try
        {
            var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(data));
            
            // Read encrypted ticket length
            if (!ReadVarint(ref reader, out var ciphertextLen))
            {
                return (null, false);
            }
            
            if (ciphertextLen > MaxTicketSize)
            {
                return (null, false);
            }
            
            // Read ciphertext
            var ciphertext = reader.Sequence.Slice(reader.Position, (int)ciphertextLen);
            reader.Advance((long)ciphertextLen);
            
            // Try decrypting with current key
            byte[] plaintext = null;
            int keyPhase = 0;
            
            if (TryDecryptTicket(ciphertext, _ticketKeyCurrent, out plaintext))
            {
                keyPhase = 0;
            }
            else if (TryDecryptTicket(ciphertext, _ticketKeyPrevious, out plaintext))
            {
                keyPhase = 1; // Previous key
            }
            else if (TryDecryptTicket(ciphertext, _ticketKeyNext, out plaintext))
            {
                keyPhase = 2; // Next key (for key rotation)
            }
            else
            {
                return (null, false); // Decryption failed
            }
            
            // Parse plaintext
            var ticketReader = new SequenceReader<byte>(new ReadOnlySequence<byte>(plaintext));
            
            if (!ticketReader.TryRead(out var version) || version != 1)
            {
                return (null, false);
            }
            
            if (!ticketReader.TryRead(out var readKeyPhase) || readKeyPhase != keyPhase)
            {
                return (null, false);
            }
            
            if (!ReadVarint(ref ticketReader, out var creationTime) ||
                !ReadVarint(ref ticketReader, out var lifetime) ||
                !ReadVarint(ref ticketReader, out var ticketAgeAdd) ||
                !ReadVarint(ref ticketReader, out var earlyDataLimit))
            {
                return (null, false);
            }
            
            if (!ReadBytes(ref ticketReader, out var connectionId) ||
                !ReadBytes(ref ticketReader, out var clientAddressBytes) ||
                !ReadBytes(ref ticketReader, out var alpnBytes))
            {
                return (null, false);
            }
            
            if (ticketReader.Remaining < 2)
            {
                return (null, false);
            }
            
            var cipherSuiteBytes = ticketReader.Sequence.Slice(ticketReader.Position, 2).ToArray();
            var cipherSuite = (ushort)((cipherSuiteBytes[0] << 8) | cipherSuiteBytes[1]);
            
            var ticket = new SessionTicket
            {
                ConnectionId = connectionId,
                ClientAddress = Encoding.UTF8.GetString(clientAddressBytes),
                EarlyDataLimit = (long)earlyDataLimit,
                Alpn = Encoding.UTF8.GetString(alpnBytes),
                CipherSuite = cipherSuite,
                CreationTime = (long)creationTime,
                Lifetime = (long)lifetime,
                TicketAgeAdd = (long)ticketAgeAdd,
                KeyPhase = keyPhase
            };
            
            return (ticket, true);
        }
        catch
        {
            return (null, false);
        }
    }

    /// <summary>
    /// Validates a session ticket for 0-RTT.
    /// </summary>
    public (bool Valid, string Error) ValidateTicket(
        SessionTicket ticket,
        string clientAddress,
        string alpn,
        long currentTime,
        out long earlyDataLimit)
    {
        earlyDataLimit = 0;
        
        if (!_zeroRttEnabled)
        {
            return (false, "0-RTT disabled");
        }
        
        // Check ticket expiration
        var ticketAge = currentTime - ticket.CreationTime;
        if (ticketAge > ticket.Lifetime)
        {
            return (false, "Ticket expired");
        }
        
        // Check client address match (optional but recommended)
        if (!string.Equals(ticket.ClientAddress, clientAddress, StringComparison.OrdinalIgnoreCase))
        {
            return (false, "Client address mismatch");
        }
        
        // Check ALPN match
        if (!string.Equals(ticket.Alpn, alpn, StringComparison.OrdinalIgnoreCase))
        {
            return (false, "ALPN mismatch");
        }
        
        // Check anti-replay
        var ticketId = GetTicketId(ticket);
        if (_usedTickets.Contains(ticketId))
        {
            _replayAttempts++;
            return (false, "Replay detected");
        }
        
        // Check early data limit
        if (ticket.EarlyDataLimit > 0)
        {
            earlyDataLimit = ticket.EarlyDataLimit;
        }
        
        return (true, "");
    }

    /// <summary>
    /// Marks a ticket as used (for anti-replay).
    /// </summary>
    public void MarkTicketUsed(SessionTicket ticket)
    {
        var ticketId = GetTicketId(ticket);
        _usedTickets.Add(ticketId);
        _ticketQueue.Enqueue(ticketId);
        
        // Clean old entries
        while (_ticketQueue.Count > AntiReplayWindowSize)
        {
            var oldId = _ticketQueue.Dequeue();
            _usedTickets.Remove(oldId);
        }
    }

    /// <summary>
    /// Accepts early data.
    /// </summary>
    public void AcceptEarlyData(long bytes)
    {
        _earlyDataAccepted += bytes;
    }

    /// <summary>
    /// Rejects early data.
    /// </summary>
    public void RejectEarlyData(long bytes)
    {
        _earlyDataRejected += bytes;
    }

    /// <summary>
    /// Rotates ticket encryption keys.
    /// </summary>
    public void RotateKeys()
    {
        // Shift keys: next -> current, current -> previous, generate new next
        Array.Copy(_ticketKeyNext, _ticketKeyCurrent, 32);
        Array.Copy(_ticketKeyCurrent, _ticketKeyPrevious, 32);
        
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(_ticketKeyNext);
        
        _keyRotationTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 12 * 60 * 60 * 1000;
    }

    /// <summary>
    /// Checks if key rotation is needed.
    /// </summary>
    public bool ShouldRotateKeys(long currentTime)
    {
        return currentTime >= _keyRotationTime;
    }

    /// <summary>
    /// Encrypts a ticket using AES-256-GCM.
    /// </summary>
    private byte[] EncryptTicket(ReadOnlyMemory<byte> plaintext, int keyPhase)
    {
        var key = keyPhase switch
        {
            0 => _ticketKeyCurrent,
            1 => _ticketKeyPrevious,
            2 => _ticketKeyNext,
            _ => _ticketKeyCurrent
        };
        
        using var aes = new AesGcm(key);
        var nonce = new byte[12];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(nonce);
        
        var ciphertext = new byte[plaintext.Length + 16]; // +16 for tag
        var tag = new byte[16];
        
        aes.Encrypt(nonce, plaintext.Span, ciphertext, tag);
        
        // Prepend nonce to ciphertext
        var result = new byte[12 + ciphertext.Length];
        nonce.CopyTo(result, 0);
        ciphertext.CopyTo(result, 12);
        
        return result;
    }

    /// <summary>
    /// Decrypts a ticket using AES-256-GCM.
    /// </summary>
    private bool TryDecryptTicket(ReadOnlySequence<byte> ciphertext, byte[] key, out byte[] plaintext)
    {
        plaintext = null;
        
        try
        {
            if (ciphertext.Length < 12 + 16)
            {
                return false;
            }
            
            var nonce = ciphertext.Slice(0, 12).ToArray();
            var ct = ciphertext.Slice(12, ciphertext.Length - 12 - 16).ToArray();
            var tag = ciphertext.Slice(ciphertext.Length - 16, 16).ToArray();
            
            using var aes = new AesGcm(key);
            plaintext = new byte[ct.Length];
            aes.Decrypt(nonce, ct, plaintext, tag);
            
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets a unique identifier for a ticket (for anti-replay).
    /// </summary>
    private string GetTicketId(SessionTicket ticket)
    {
        // Use connection ID + creation time as unique identifier
        return Convert.ToBase64String(ticket.ConnectionId) + "|" + ticket.CreationTime;
    }

    /// <summary>
    /// Writes a varint to the buffer.
    /// </summary>
    private static void WriteVarint(ArrayBufferWriter<byte> writer, ulong value)
    {
        if (value < 0x40)
        {
            writer.Write(new byte[] { (byte)value });
        }
        else if (value < 0x4000)
        {
            writer.Write(new byte[] { (byte)(0x40 | (value >> 8)), (byte)value });
        }
        else if (value < 0x40000000)
        {
            writer.Write(new byte[] { (byte)(0x80 | (value >> 24)), (byte)(value >> 16), (byte)(value >> 8), (byte)value });
        }
        else
        {
            writer.Write(new byte[] { 
                (byte)(0xC0 | (value >> 56)), 
                (byte)(value >> 48), 
                (byte)(value >> 40), 
                (byte)(value >> 32), 
                (byte)(value >> 24), 
                (byte)(value >> 16), 
                (byte)(value >> 8), 
                (byte)value 
            });
        }
    }

    /// <summary>
    /// Reads a varint from the reader.
    /// </summary>
    private static bool ReadVarint(ref SequenceReader<byte> reader, out ulong value)
    {
        value = 0;
        
        if (!reader.TryRead(out var firstByte))
        {
            return false;
        }
        
        var prefix = firstByte >> 6;
        var length = 1 << (int)prefix;
        
        value = (ulong)(firstByte & 0x3F);
        
        for (int i = 1; i < length; i++)
        {
            if (!reader.TryRead(out var b))
            {
                return false;
            }
            value = (value << 8) | b;
        }
        
        return true;
    }

    /// <summary>
    /// Reads a length-prefixed byte array.
    /// </summary>
    private static bool ReadBytes(ref SequenceReader<byte> reader, out byte[] bytes)
    {
        bytes = null;
        
        if (!ReadVarint(ref reader, out var len))
        {
            return false;
        }
        
        if ((ulong)reader.Remaining < len)
        {
            return false;
        }
        
        bytes = reader.Sequence.Slice(reader.Position, (int)len).ToArray();
        reader.Advance((long)len);
        return true;
    }

    /// <summary>
    /// Represents a QUIC session ticket for 0-RTT resumption.
    /// </summary>
    public sealed class SessionTicket
    {
        public byte[] ConnectionId { get; set; }
        public string ClientAddress { get; set; }
        public long EarlyDataLimit { get; set; }
        public string Alpn { get; set; }
        public ushort CipherSuite { get; set; }
        public long CreationTime { get; set; }
        public long Lifetime { get; set; }
        public long TicketAgeAdd { get; set; }
        public int KeyPhase { get; set; }
    }
}
