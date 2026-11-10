// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Rocket;

using System;
using System.Diagnostics;

/// <summary>
/// CUBIC congestion control algorithm implementation for QUIC.
/// Based on RFC 8312 and tquic implementation (https://github.com/Tencent/tquic).
/// </summary>
public sealed class CubicCongestionControl : ICongestionControl
{
    // CUBIC constants from RFC 8312
    private const double CubicBeta = 0.7;           // Multiplicative decrease factor
    private const double CubicC = 0.4;              // Scaling constant
    private const double BetaLastMax = 0.85;        // Fast convergence parameter
    private const int MinCwndPackets = 2;           // Minimum congestion window in packets
    private const int InitialCwndPackets = 10;      // Initial congestion window (RFC 9002)
    private const int MaxDatagramSize = 1200;       // Assumed max datagram size

    // State variables
    private long _cwnd;                    // Congestion window in bytes
    private long _bytesInFlight;           // Bytes currently in flight
    private long _slowStartThreshold;      // Slow start threshold (ssthresh)
    private long _lastMaxCwnd;             // Last maximum cwnd before reduction
    private long _epochStartTime;          // Time when current epoch started (microseconds)
    private long _k;                       // Time to reach lastMaxCwnd from current cwnd
    private bool _inSlowStart;             // Whether in slow start phase
    private bool _inRecovery;              // Whether in recovery phase
    private long _recoveryStartTime;       // Time when recovery started
    private long _lastAckedTime;           // Time of last ACK received
    private long _minRtt;                  // Minimum RTT observed (microseconds)
    private long _srtt;                    // Smoothed RTT (microseconds)
    private long _rttvar;                  // RTT variation (microseconds)
    private long _packetCount;             // Packet counter for pacing
    private long _maxDatagramSize;         // Maximum datagram size

    // Pacing
    private double _pacingRate;            // Current pacing rate (bytes/second)
    private long _nextPacketSendTime;      // Next scheduled packet send time

    /// <summary>
    /// Initializes a new instance of the <see cref="CubicCongestionControl"/> class.
    /// </summary>
    public CubicCongestionControl()
    {
        _maxDatagramSize = MaxDatagramSize;
        Initialize();
    }

    /// <inheritdoc/>
    public long CongestionWindow => _cwnd;

    /// <inheritdoc/>
    public long BytesInFlight => _bytesInFlight;

    /// <inheritdoc/>
    public long SlowStartThreshold => _slowStartThreshold;

    /// <inheritdoc/>
    public bool InSlowStart => _inSlowStart;

    /// <inheritdoc/>
    public bool InRecovery => _inRecovery;

    /// <inheritdoc/>
    public double PacingRate => _pacingRate;

    /// <inheritdoc/>
    public long MinRtt => _minRtt;

    /// <inheritdoc/>
    public long SmoothedRtt => _srtt;

    /// <inheritdoc/>
    public void Initialize()
    {
        _cwnd = InitialCwndPackets * _maxDatagramSize;
        _bytesInFlight = 0;
        _slowStartThreshold = long.MaxValue;
        _lastMaxCwnd = 0;
        _epochStartTime = 0;
        _k = 0;
        _inSlowStart = true;
        _inRecovery = false;
        _recoveryStartTime = 0;
        _lastAckedTime = 0;
        _minRtt = long.MaxValue;
        _srtt = 0;
        _rttvar = 0;
        _packetCount = 0;
        _pacingRate = InitialCwndPackets * _maxDatagramSize * 1_000_000.0 / 100_000; // Initial rate estimate
        _nextPacketSendTime = 0;
    }

    /// <inheritdoc/>
    public void OnPacketSent(long bytesSent, long currentTime)
    {
        _bytesInFlight += bytesSent;
        _packetCount++;
        
        // Update pacing
        UpdatePacingRate(currentTime);
    }

    /// <inheritdoc/>
    public void OnPacketAcknowledged(long bytesAcked, long ackDelay, long currentTime, long rtt)
    {
        _bytesInFlight = Math.Max(0, _bytesInFlight - bytesAcked);
        _lastAckedTime = currentTime;
        
        // Update RTT estimates
        UpdateRtt(rtt, ackDelay);
        
        // Update congestion window
        if (_inSlowStart)
        {
            SlowStart(bytesAcked);
        }
        else
        {
            CongestionAvoidance(bytesAcked, currentTime);
        }
        
        // Update pacing
        UpdatePacingRate(currentTime);
    }

    /// <inheritdoc/>
    public void OnPacketLost(long bytesLost, long currentTime)
    {
        _bytesInFlight = Math.Max(0, _bytesInFlight - bytesLost);
        
        // Enter recovery if not already in recovery
        if (!_inRecovery)
        {
            EnterRecovery(currentTime);
        }
    }

    /// <inheritdoc/>
    public void OnEcnCongestionExperienced(long currentTime)
    {
        // ECN-CE marked packet - treat similar to loss but less severe
        if (!_inRecovery)
        {
            EnterRecovery(currentTime);
        }
        
        // Reduce congestion window (less aggressive than loss)
        _cwnd = (long)(_cwnd * 0.85);
        _slowStartThreshold = _cwnd;
    }

    /// <inheritdoc/>
    public void OnPersistentCongestion(long currentTime)
    {
        // Persistent congestion - collapse congestion window
        _cwnd = MinCwndPackets * _maxDatagramSize;
        _slowStartThreshold = _cwnd;
        _inSlowStart = true;
        _inRecovery = false;
        _epochStartTime = 0;
        _lastMaxCwnd = 0;
    }

    /// <inheritdoc/>
    public bool CanSend(long currentTime)
    {
        if (_bytesInFlight >= _cwnd)
        {
            return false;
        }
        
        // Check pacing
        if (_nextPacketSendTime > currentTime)
        {
            return false;
        }
        
        return true;
    }

    /// <inheritdoc/>
    public long GetNextSendTime(long currentTime)
    {
        return _nextPacketSendTime;
    }

    /// <inheritdoc/>
    public void SetMaxDatagramSize(long size)
    {
        _maxDatagramSize = Math.Max(size, 1200);
        
        // Adjust cwnd if it was based on packet count
        if (_cwnd < InitialCwndPackets * _maxDatagramSize)
        {
            _cwnd = InitialCwndPackets * _maxDatagramSize;
        }
    }

    /// <inheritdoc/>
    public void Reset()
    {
        Initialize();
    }

    /// <summary>
    /// Updates RTT estimates using standard Jacobson/Karels algorithm.
    /// </summary>
    private void UpdateRtt(long rtt, long ackDelay)
    {
        if (rtt <= 0)
        {
            return;
        }
        
        // Adjust RTT for ACK delay
        var adjustedRtt = rtt - ackDelay;
        if (adjustedRtt < 1)
        {
            adjustedRtt = 1;
        }
        
        _minRtt = Math.Min(_minRtt, adjustedRtt);
        
        if (_srtt == 0)
        {
            // First RTT sample
            _srtt = adjustedRtt;
            _rttvar = adjustedRtt / 2;
        }
        else
        {
            // Update smoothed RTT and variation
            _rttvar = (3 * _rttvar + Math.Abs(_srtt - adjustedRtt)) / 4;
            _srtt = (7 * _srtt + adjustedRtt) / 8;
        }
    }

    /// <summary>
    /// Slow start phase - exponential growth.
    /// </summary>
    private void SlowStart(long bytesAcked)
    {
        _cwnd += bytesAcked;
        
        // Exit slow start if cwnd exceeds ssthresh
        if (_cwnd >= _slowStartThreshold)
        {
            _inSlowStart = false;
            _epochStartTime = 0; // Will be set on next ACK
        }
    }

    /// <summary>
    /// Congestion avoidance phase - CUBIC algorithm.
    /// </summary>
    private void CongestionAvoidance(long bytesAcked, long currentTime)
    {
        // Calculate target cwnd using CUBIC function
        var targetCwnd = CalculateCubicCwnd(currentTime);
        
        if (targetCwnd > _cwnd)
        {
            // Increase cwnd towards target
            var increment = (long)((double)bytesAcked * _maxDatagramSize / _cwnd);
            _cwnd = Math.Min(_cwnd + increment, targetCwnd);
        }
        else if (targetCwnd < _cwnd)
        {
            // CUBIC says we should decrease (unusual, but handle it)
            _cwnd = targetCwnd;
        }
    }

    /// <summary>
    /// Calculates the CUBIC congestion window target.
    /// CUBIC function: W(t) = C * (t - K)^3 + W_max
    /// Where K = cubic_root(W_max * (1 - beta) / C)
    /// </summary>
    private long CalculateCubicCwnd(long currentTime)
    {
        if (_epochStartTime == 0)
        {
            // First ACK after entering congestion avoidance
            _epochStartTime = currentTime;
            
            if (_lastMaxCwnd <= _cwnd)
            {
                _lastMaxCwnd = _cwnd;
                _k = 0;
            }
            else
            {
                // Fast convergence
                _lastMaxCwnd = (long)(_lastMaxCwnd * BetaLastMax);
                _k = CalculateK();
            }
            
            return _cwnd;
        }
        
        var t = (currentTime - _epochStartTime) / 1_000_000.0; // Convert to seconds
        var cubicValue = CubicC * Math.Pow(t - _k, 3) + _lastMaxCwnd;
        
        return (long)Math.Max(cubicValue, MinCwndPackets * _maxDatagramSize);
    }

    /// <summary>
    /// Calculates K = cubic_root(W_max * (1 - beta) / C)
    /// </summary>
    private long CalculateK()
    {
        var value = _lastMaxCwnd * (1.0 - CubicBeta) / CubicC;
        return (long)Math.Round(Math.Pow(value, 1.0 / 3.0) * 1_000_000); // Microseconds
    }

    /// <summary>
    /// Enters recovery phase after packet loss.
    /// </summary>
    private void EnterRecovery(long currentTime)
    {
        _inRecovery = true;
        _inSlowStart = false;
        _recoveryStartTime = currentTime;
        
        // Save current cwnd as last max
        _lastMaxCwnd = _cwnd;
        
        // Reduce cwnd and ssthresh
        _cwnd = (long)(_cwnd * CubicBeta);
        _slowStartThreshold = _cwnd;
        
        // Ensure minimum
        _cwnd = Math.Max(_cwnd, MinCwndPackets * _maxDatagramSize);
        _slowStartThreshold = Math.Max(_slowStartThreshold, MinCwndPackets * _maxDatagramSize);
        
        // Reset epoch
        _epochStartTime = 0;
        _k = CalculateK();
    }

    /// <summary>
    /// Updates the pacing rate based on current cwnd and RTT.
    /// </summary>
    private void UpdatePacingRate(long currentTime)
    {
        if (_srtt > 0)
        {
            // Pacing rate = cwnd / srtt (with 1.25x headroom)
            _pacingRate = (_cwnd * 1.25 * 1_000_000.0) / _srtt;
        }
        else
        {
            // No RTT estimate yet, use initial rate
            _pacingRate = InitialCwndPackets * _maxDatagramSize * 1_000_000.0 / 100_000;
        }
        
        // Calculate next packet send time
        if (_pacingRate > 0)
        {
            var packetTime = (long)(_maxDatagramSize * 1_000_000.0 / _pacingRate);
            _nextPacketSendTime = currentTime + packetTime;
        }
        else
        {
            _nextPacketSendTime = currentTime;
        }
    }
}

/// <summary>
/// Interface for congestion control algorithms.
/// </summary>
public interface ICongestionControl
{
    long CongestionWindow { get; }
    long BytesInFlight { get; }
    long SlowStartThreshold { get; }
    bool InSlowStart { get; }
    bool InRecovery { get; }
    double PacingRate { get; }
    long MinRtt { get; }
    long SmoothedRtt { get; }

    void Initialize();
    void OnPacketSent(long bytesSent, long currentTime);
    void OnPacketAcknowledged(long bytesAcked, long ackDelay, long currentTime, long rtt);
    void OnPacketLost(long bytesLost, long currentTime);
    void OnEcnCongestionExperienced(long currentTime);
    void OnPersistentCongestion(long currentTime);
    bool CanSend(long currentTime);
    long GetNextSendTime(long currentTime);
    void SetMaxDatagramSize(long size);
    void Reset();
}
