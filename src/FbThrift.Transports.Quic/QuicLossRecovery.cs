// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Quic;

using System;
using System.Collections.Generic;
using System.Diagnostics;

/// <summary>
/// QUIC Loss Recovery implementation.
/// Based on RFC 9002 and tquic/quiche implementations.
/// </summary>
public sealed class QuicLossRecovery
{
    // Constants from RFC 9002
    private const double PacketThreshold = 3.0;        // kPacketThreshold
    private const double TimeThreshold = 9.0 / 8.0;    // kTimeThreshold (1.125)
    private const double Granularity = 1.0 / 1000.0;   // Timer granularity (1ms)
    private const double InitialRttMs = 100.0;         // Initial RTT estimate
    private const int MaxAckDelayMs = 25;              // Max ACK delay
    private const int MinPacketsForRtt = 2;            // Min packets before RTT update
private const double PacingRateHeadroom = 1.25;    // Pacing rate multiplier

    // TimerWheel for O(1) loss detection timer management
    private readonly TimerWheel _timerWheel;

    // Packet tracking
    private readonly SortedDictionary<long, SentPacket> _sentPackets = new();
    private readonly HashSet<long> _lostPackets = new();
    private long _largestAckedPacket;                  // Largest packet number acknowledged
    private long _largestSentPacket;                   // Largest packet number sent
    private long _largestAckedAtLastLoss;              // Largest acked at last loss event

    // RTT estimation
    private long _minRtt;                              // Minimum RTT (microseconds)
    private long _srtt;                                // Smoothed RTT (microseconds)
    private long _rttvar;                              // RTT variation (microseconds)
    private long _latestRtt;                           // Latest RTT sample
    private int _rttSampleCount;                       // Number of RTT samples

    // Loss recovery
    private long _bytesInFlight;                       // Bytes currently in flight
    private long _lossRecoveryStart;                   // Time when recovery started
    private bool _inRecovery;                          // Whether in recovery
    private long _recoveryWindow;                      // Recovery window

    // ECN
    private long _ecnCeCount;                          // ECN-CE count
    private long _ecnCeCountAtLastLoss;                // ECN-CE at last loss

    // PMTU
    private int _maxDatagramSize = 1200;

    // Statistics
    private long _totalPacketsSent;
    private long _totalPacketsLost;
    private long _totalPacketsRetransmitted;
    private long _totalBytesRetransmitted;

    /// <summary>
    /// Initializes a new instance of the <see cref="QuicLossRecovery"/> class.
    /// </summary>
    /// <param name="onPacketTimeout">Callback invoked when a packet timeout expires (for retransmission)</param>
    public QuicLossRecovery(Action<PacketMetadata> onPacketTimeout = null)
    {
        _timerWheel = new TimerWheel(onPacketTimeout ?? (_ => { }));
        Initialize();
    }

    /// <summary>
    /// Gets the minimum RTT in microseconds.
    /// </summary>
    public long MinRtt => _minRtt;

    /// <summary>
    /// Gets the smoothed RTT in microseconds.
    /// </summary>
    public long SmoothedRtt => _srtt;

    /// <summary>
    /// Gets the RTT variation in microseconds.
    /// </summary>
    public long Rttvar => _rttvar;

    /// <summary>
    /// Gets the latest RTT sample in microseconds.
    /// </summary>
    public long LatestRtt => _latestRtt;

    /// <summary>
    /// Gets the loss detection timer expiration time in microseconds.
    /// </summary>
    public long LossDetectionTimer => _timerWheel.CurrentTick * 1000; // Convert ticks to microseconds

    /// <summary>
    /// Gets whether the loss detection timer is set.
    /// </summary>
    public bool IsLossDetectionTimerSet => true; // TimerWheel always running

    /// <summary>
    /// Gets the bytes currently in flight.
    /// </summary>
    public long BytesInFlight => _bytesInFlight;

    /// <summary>
    /// Gets whether we're in recovery.
    /// </summary>
    public bool InRecovery => _inRecovery;

    /// <summary>
    /// Gets the largest acknowledged packet number.
    /// </summary>
    public long LargestAckedPacket => _largestAckedPacket;

    /// <summary>
    /// Gets the largest sent packet number.
    /// </summary>
    public long LargestSentPacket => _largestSentPacket;

    /// <summary>
    /// Gets the number of packets in flight.
    /// </summary>
    public int PacketsInFlight => _sentPackets.Count;

    /// <summary>
    /// Gets the total packets sent.
    /// </summary>
    public long TotalPacketsSent => _totalPacketsSent;

    /// <summary>
    /// Gets the total packets lost.
    /// </summary>
    public long TotalPacketsLost => _totalPacketsLost;

    /// <summary>
    /// Initializes the loss recovery state.
    /// </summary>
    public void Initialize()
    {
        _sentPackets.Clear();
        _lostPackets.Clear();
        _largestAckedPacket = 0;
        _largestSentPacket = 0;
        _largestAckedAtLastLoss = 0;
        _minRtt = long.MaxValue;
        _srtt = 0;
        _rttvar = 0;
        _latestRtt = 0;
        _rttSampleCount = 0;
        _bytesInFlight = 0;
        _lossRecoveryStart = 0;
        _inRecovery = false;
        _recoveryWindow = 0;
        _ecnCeCount = 0;
        _ecnCeCountAtLastLoss = 0;
        _totalPacketsSent = 0;
        _totalPacketsLost = 0;
        _totalPacketsRetransmitted = 0;
        _totalBytesRetransmitted = 0;
    }

    /// <summary>
    /// Called when a packet is sent.
    /// </summary>
    public void OnPacketSent(long packetNumber, long bytes, long currentTime, bool isRetransmission = false)
    {
        if (packetNumber <= _largestSentPacket)
        {
            throw new ArgumentException($"Packet number {packetNumber} <= largest sent {_largestSentPacket}");
        }

        var packet = new SentPacket
        {
            PacketNumber = packetNumber,
            Bytes = bytes,
            SendTime = currentTime,
            IsRetransmission = isRetransmission,
            InFlight = true
        };

        _sentPackets[packetNumber] = packet;
        _largestSentPacket = packetNumber;
        _bytesInFlight += bytes;
        _totalPacketsSent++;

        if (isRetransmission)
        {
            _totalPacketsRetransmitted++;
            _totalBytesRetransmitted += bytes;
        }

        // Schedule timeout in TimerWheel (O(1) scheduling)
        var packetMeta = new PacketMetadata
        {
            PacketNumber = packetNumber,
            Bytes = bytes,
            SendTime = currentTime,
            IsRetransmission = isRetransmission,
            InFlight = true
        };
        
        // PTO in milliseconds
        int timeoutMs = (int)(GetPto() / 1000);
        _timerWheel.SchedulePacketTimeout(packetMeta, timeoutMs);
    }

    /// <summary>
    /// Called when an ACK frame is received.
    /// </summary>
    public void OnAckReceived(
        long largestAcked,
        long ackDelay,
        IReadOnlyList<(ulong Start, ulong End)> ackRanges,
        long currentTime,
        out List<long> newlyAckedPackets,
        out List<long> newlyLostPackets)
    {
        newlyAckedPackets = new List<long>();
        newlyLostPackets = new List<long>();

        if (largestAcked > _largestAckedPacket)
        {
            _largestAckedPacket = largestAcked;
        }

        // Process each acknowledged range
        foreach (var range in ackRanges)
        {
            for (ulong pn = range.Start; pn <= range.End; pn++)
            {
                var packetNum = (long)pn;
                if (_sentPackets.TryGetValue(packetNum, out var packet) && packet.InFlight)
                {
                    packet.InFlight = false;
                    _bytesInFlight = Math.Max(0, _bytesInFlight - packet.Bytes);
                    newlyAckedPackets.Add(packetNum);

                    // Cancel timer for this packet (O(1) in TimerWheel)
                    var packetMeta = new PacketMetadata { PacketNumber = packetNum };
                    _timerWheel.CancelPacketTimeout(packetMeta);

                    // RTT sample for non-retransmitted packets
                    if (!packet.IsRetransmission)
                    {
                        OnRttSample(currentTime - packet.SendTime, ackDelay, currentTime);
                    }
                }
            }
        }

        // Detect lost packets
        DetectLostPackets(currentTime, newlyLostPackets);

        // Exit recovery if all packets before recovery start are acknowledged
        if (_inRecovery && _largestAckedPacket >= _largestAckedAtLastLoss)
        {
            ExitRecovery(currentTime);
        }
    }

    /// <summary>
    /// Called when an RTT sample is available.
    /// </summary>
    private void OnRttSample(long rtt, long ackDelay, long currentTime)
    {
        _latestRtt = rtt;
        _rttSampleCount++;

        // Adjust for ACK delay
        var adjustedRtt = rtt - ackDelay;
        if (adjustedRtt < 1) adjustedRtt = 1;

        _minRtt = Math.Min(_minRtt, adjustedRtt);

        if (_srtt == 0)
        {
            // First RTT sample
            _srtt = adjustedRtt;
            _rttvar = adjustedRtt / 2;
        }
        else
        {
            // RFC 6298 / Jacobson/Karels
            _rttvar = (long)((1 - 1.0/4) * _rttvar + (1.0/4) * Math.Abs(_srtt - adjustedRtt));
            _srtt = (long)((1 - 1.0/8) * _srtt + (1.0/8) * adjustedRtt);
        }
    }

    /// <summary>
    /// Detects lost packets based on packet and time thresholds.
    /// </summary>
    private void DetectLostPackets(long currentTime, List<long> newlyLostPackets)
    {
        if (_largestAckedPacket == 0) return;

        // Packet threshold loss detection
        var lossThreshold = _largestAckedPacket - (long)PacketThreshold;

        // Time threshold loss detection
        var rto = _srtt + Math.Max(4 * _rttvar, (long)(Granularity * 1_000_000));
        var timeThreshold = currentTime - (long)(rto * TimeThreshold);

        foreach (var kvp in _sentPackets)
        {
            var packet = kvp.Value;
            if (!packet.InFlight || _lostPackets.Contains(packet.PacketNumber))
            {
                continue;
            }

            bool isLost = false;

            // Packet threshold
            if (packet.PacketNumber <= lossThreshold)
            {
                isLost = true;
            }
            // Time threshold
            else if (packet.SendTime <= timeThreshold)
            {
                isLost = true;
            }

            if (isLost)
            {
                _lostPackets.Add(packet.PacketNumber);
                packet.InFlight = false;
                _bytesInFlight = Math.Max(0, _bytesInFlight - packet.Bytes);
                newlyLostPackets.Add(packet.PacketNumber);
                _totalPacketsLost++;
                
                // Cancel timer for lost packet (O(1) in TimerWheel)
                var packetMeta = new PacketMetadata { PacketNumber = packet.PacketNumber };
                _timerWheel.CancelPacketTimeout(packetMeta);
            }
        }

        // Enter recovery if new losses detected
        if (newlyLostPackets.Count > 0 && !_inRecovery)
        {
            EnterRecovery(currentTime);
        }
    }

    /// <summary>
    /// Enters recovery phase.
    /// </summary>
    private void EnterRecovery(long currentTime)
    {
        _inRecovery = true;
        _lossRecoveryStart = currentTime;
        _largestAckedAtLastLoss = _largestAckedPacket;
        _ecnCeCountAtLastLoss = _ecnCeCount;
        
        // Reduce congestion window (will be done by congestion controller)
        _recoveryWindow = _bytesInFlight;
    }

    /// <summary>
    /// Exits recovery phase.
    /// </summary>
    private void ExitRecovery(long currentTime)
    {
        _inRecovery = false;
        _lossRecoveryStart = 0;
        _recoveryWindow = 0;
    }

    /// <summary>
    /// Called when an ECN-CE marked packet is acknowledged.
    /// </summary>
    public void OnEcnCeReceived(long currentTime)
    {
        _ecnCeCount++;
    }

    /// <summary>
    /// Gets packets that need to be retransmitted.
    /// </summary>
    public List<long> GetRetransmittablePackets()
    {
        var result = new List<long>();
        foreach (var kvp in _sentPackets)
        {
            var packet = kvp.Value;
            if (!packet.InFlight && !packet.IsRetransmission && _lostPackets.Contains(packet.PacketNumber))
            {
                result.Add(packet.PacketNumber);
            }
        }
        return result;
    }

    /// <summary>
    /// Marks a packet as retransmitted.
    /// </summary>
    public void MarkRetransmitted(long packetNumber, long newPacketNumber, long bytes, long currentTime)
    {
        if (_sentPackets.TryGetValue(packetNumber, out var oldPacket))
        {
            oldPacket.IsRetransmission = true;
        }

        OnPacketSent(newPacketNumber, bytes, currentTime, true);
    }

    /// <summary>
    /// Gets the PTO (Probe Timeout) value in microseconds.
    /// </summary>
    public long GetPto()
    {
        var rto = _srtt + Math.Max(4 * _rttvar, (long)(Granularity * 1_000_000));
        return rto + MaxAckDelayMs * 1000;
    }

    /// <summary>
    /// Gets the time until the next timeout in microseconds.
    /// </summary>
    public long GetTimeUntilTimeout(long currentTime)
    {
        if (!_lossDetectionTimerSet)
        {
            return long.MaxValue;
        }
        return Math.Max(0, _lossDetectionTimer - currentTime);
    }

    /// <summary>
    /// Sets the maximum datagram size.
    /// </summary>
    public void SetMaxDatagramSize(int size)
    {
        _maxDatagramSize = Math.Max(size, 1200);
    }

    /// <summary>
    /// Resets the loss recovery state.
    /// </summary>
    public void Reset()
    {
        Initialize();
    }

    /// <summary>
    /// Represents a sent packet tracked for loss detection.
    /// </summary>
    private sealed class SentPacket
    {
        public long PacketNumber { get; set; }
        public long Bytes { get; set; }
        public long SendTime { get; set; }
        public bool IsRetransmission { get; set; }
        public bool InFlight { get; set; }
    }
}
