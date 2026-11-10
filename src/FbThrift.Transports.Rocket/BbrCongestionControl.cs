// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Rocket;

using System;
using System.Collections.Generic;

/// <summary>
/// BBR (Bottleneck Bandwidth and Round-trip propagation time) congestion control.
/// Based on RFC 9002 Appendix B and quiche implementation.
/// </summary>
public sealed class BbrCongestionControl : ICongestionControl
{
    // BBR constants
    private const double Ln2 = 0.6931471805599453; // Math.Log(2)
    private const double BbrStartupGrowthFactor = 2.0 / Ln2;  // ~2.885
    private const double BbrDrainGrowthFactor = 1.0 / BbrStartupGrowthFactor;
    private const double BbrProbeBwGainCycle = 1.25;  // Cycle: 1.25, 0.75, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0
    private const double BbrProbeRttGain = 0.5;
    private const double BbrCwndGain = 2.0;
    private const int MinCwndPackets = 4;
    private const int InitialCwndPackets = 10;
    private const int MaxDatagramSize = 1200;
    private const long ProbeRttDurationMs = 200;
    private const long StartupMinRttSamples = 4;

    private static readonly double[] ProbeBwGains = 
    {
        1.25, 0.75, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0
    };

    // BBR states
    private enum BbrState
    {
        Startup,
        Drain,
        ProbeBw,
        ProbeRtt
    }

    // State variables
    private long _cwnd;
    private long _bytesInFlight;
    private long _slowStartThreshold;
    private BbrState _state;
    private int _probeBwPhase;
    private long _probeRttStartTime;
    private long _minRtt;
    private long _srtt;
    private long _rttvar;
    private long _maxBandwidth;           // bytes/second
    private long _maxBandwidthTimestamp;  // When max bandwidth was measured
    private long _lastAckedTime;
    private long _packetCount;
    private long _maxDatagramSize;
    private double _pacingRate;
    private long _nextPacketSendTime;
    private long _epochStartTime;
    private long _recoveryStartTime;
    private bool _inRecovery;
    private long _lastMaxCwnd;

    // Bandwidth filter (max filter over window)
    private readonly MaxFilter _bandwidthFilter = new(10); // 10 RTT window

    public BbrCongestionControl()
    {
        _maxDatagramSize = MaxDatagramSize;
        Initialize();
    }

    public long CongestionWindow => _cwnd;
    public long BytesInFlight => _bytesInFlight;
    public long SlowStartThreshold => _slowStartThreshold;
    public bool InSlowStart => _state == BbrState.Startup || _state == BbrState.Drain;
    public bool InRecovery => _inRecovery;
    public double PacingRate => _pacingRate;
    public long MinRtt => _minRtt;
    public long SmoothedRtt => _srtt;

    public void Initialize()
    {
        _cwnd = InitialCwndPackets * _maxDatagramSize;
        _bytesInFlight = 0;
        _slowStartThreshold = long.MaxValue;
        _state = BbrState.Startup;
        _probeBwPhase = 0;
        _probeRttStartTime = 0;
        _minRtt = long.MaxValue;
        _srtt = 0;
        _rttvar = 0;
        _maxBandwidth = 0;
        _maxBandwidthTimestamp = 0;
        _lastAckedTime = 0;
        _packetCount = 0;
        _pacingRate = InitialCwndPackets * _maxDatagramSize * 1_000_000.0 / 100_000;
        _nextPacketSendTime = 0;
        _epochStartTime = 0;
        _recoveryStartTime = 0;
        _inRecovery = false;
        _lastMaxCwnd = 0;
        _bandwidthFilter.Reset();
    }

    public void OnPacketSent(long bytesSent, long currentTime)
    {
        _bytesInFlight += bytesSent;
        _packetCount++;
        UpdatePacingRate(currentTime);
    }

    public void OnPacketAcknowledged(long bytesAcked, long ackDelay, long currentTime, long rtt)
    {
        _bytesInFlight = Math.Max(0, _bytesInFlight - bytesAcked);
        _lastAckedTime = currentTime;

        UpdateRtt(rtt, ackDelay);
        UpdateBandwidth(bytesAcked, rtt, currentTime);
        UpdateState(currentTime);
        UpdatePacingRate(currentTime);
    }

    public void OnPacketLost(long bytesLost, long currentTime)
    {
        _bytesInFlight = Math.Max(0, _bytesInFlight - bytesLost);
        
        if (!_inRecovery)
        {
            EnterRecovery(currentTime);
        }
        
        // BBR doesn't reduce cwnd on loss immediately
        // It relies on bandwidth model to adjust
    }

    public void OnEcnCongestionExperienced(long currentTime)
    {
        if (!_inRecovery)
        {
            EnterRecovery(currentTime);
        }
        // BBR doesn't reduce cwnd for ECN either
    }

    public void OnPersistentCongestion(long currentTime)
    {
        _cwnd = MinCwndPackets * _maxDatagramSize;
        _slowStartThreshold = _cwnd;
        _state = BbrState.Startup;
        _inRecovery = false;
        _epochStartTime = 0;
        _bandwidthFilter.Reset();
    }

    public bool CanSend(long currentTime)
    {
        if (_bytesInFlight >= _cwnd)
        {
            return false;
        }
        
        if (_nextPacketSendTime > currentTime)
        {
            return false;
        }
        
        return true;
    }

    public long GetNextSendTime(long currentTime)
    {
        return _nextPacketSendTime;
    }

    public void SetMaxDatagramSize(long size)
    {
        _maxDatagramSize = Math.Max(size, 1200);
        
        if (_cwnd < InitialCwndPackets * _maxDatagramSize)
        {
            _cwnd = InitialCwndPackets * _maxDatagramSize;
        }
    }

    public void Reset()
    {
        Initialize();
    }

    private void UpdateRtt(long rtt, long ackDelay)
    {
        if (rtt <= 0) return;
        
        var adjustedRtt = rtt - ackDelay;
        if (adjustedRtt < 1) adjustedRtt = 1;
        
        _minRtt = Math.Min(_minRtt, adjustedRtt);
        
        if (_srtt == 0)
        {
            _srtt = adjustedRtt;
            _rttvar = adjustedRtt / 2;
        }
        else
        {
            _rttvar = (3 * _rttvar + Math.Abs(_srtt - adjustedRtt)) / 4;
            _srtt = (7 * _srtt + adjustedRtt) / 8;
        }
    }

    private void UpdateBandwidth(long bytesAcked, long rtt, long currentTime)
    {
        if (rtt <= 0) return;
        
        // Calculate delivery rate
        var rate = (bytesAcked * 1_000_000L) / rtt; // bytes/second
        _bandwidthFilter.Update(rate);
        
        var newMaxBandwidth = _bandwidthFilter.GetMax();
        if (newMaxBandwidth > _maxBandwidth)
        {
            _maxBandwidth = newMaxBandwidth;
            _maxBandwidthTimestamp = currentTime;
        }
    }

    private void UpdateState(long currentTime)
    {
        switch (_state)
        {
            case BbrState.Startup:
                UpdateStartup(currentTime);
                break;
            case BbrState.Drain:
                UpdateDrain(currentTime);
                break;
            case BbrState.ProbeBw:
                UpdateProbeBw(currentTime);
                break;
            case BbrState.ProbeRtt:
                UpdateProbeRtt(currentTime);
                break;
        }
    }

    private void UpdateStartup(long currentTime)
    {
        // Check if we've filled the pipe (bandwidth growth stalled)
        if (IsBandwidthStalled())
        {
            _state = BbrState.Drain;
            _epochStartTime = currentTime;
        }
        
        // Aggressive growth in startup
        _cwnd = (long)(_cwnd * BbrStartupGrowthFactor);
    }

    private void UpdateDrain(long currentTime)
    {
        // Drain queue by reducing rate
        _cwnd = (long)(_cwnd * BbrDrainGrowthFactor);
        
        // Exit drain when queue is drained (inflight <= BDP)
        var bdp = (_maxBandwidth * _minRtt) / 1_000_000L;
        if (_bytesInFlight <= bdp)
        {
            _state = BbrState.ProbeBw;
            _probeBwPhase = 0;
            _epochStartTime = currentTime;
        }
    }

    private void UpdateProbeBw(long currentTime)
    {
        // Cycle through gains
        var gain = ProbeBwGains[_probeBwPhase];
        var targetCwnd = (long)(_maxBandwidth * _minRtt * gain / 1_000_000L * BbrCwndGain);
        _cwnd = Math.Max(targetCwnd, MinCwndPackets * _maxDatagramSize);
        
        // Check if it's time to probe RTT
        if (currentTime - _epochStartTime > 10_000_000 && // 10 seconds
            _probeBwPhase == 0) // Only at start of cycle
        {
            _state = BbrState.ProbeRtt;
            _probeRttStartTime = currentTime;
        }
        
        // Advance phase
        _probeBwPhase = (_probeBwPhase + 1) % ProbeBwGains.Length;
        if (_probeBwPhase == 0)
        {
            _epochStartTime = currentTime;
        }
    }

    private void UpdateProbeRtt(long currentTime)
    {
        // Reduce inflight to 4 packets to measure min RTT
        _cwnd = MinCwndPackets * _maxDatagramSize;
        
        if (currentTime - _probeRttStartTime > ProbeRttDurationMs * 1000)
        {
            // Probe RTT complete, return to ProbeBW
            _state = BbrState.ProbeBw;
            _probeBwPhase = 0;
            _epochStartTime = currentTime;
        }
    }

    private bool IsBandwidthStalled()
    {
        // Check if bandwidth hasn't increased in last 3 rounds
        if (_bandwidthFilter.Count < 3) return false;
        
        var max1 = _bandwidthFilter.GetMaxAt(_bandwidthFilter.Count - 1);
        var max2 = _bandwidthFilter.GetMaxAt(_bandwidthFilter.Count - 2);
        var max3 = _bandwidthFilter.GetMaxAt(_bandwidthFilter.Count - 3);
        
        return max1 <= max2 * 1.25 && max2 <= max3 * 1.25;
    }

    private void EnterRecovery(long currentTime)
    {
        _inRecovery = true;
        _recoveryStartTime = currentTime;
        _lastMaxCwnd = _cwnd;
        
        // BBR doesn't cut cwnd in half, but records for potential reduction
        _slowStartThreshold = _cwnd;
    }

    private void UpdatePacingRate(long currentTime)
    {
        if (_maxBandwidth > 0)
        {
            // Pacing rate = max_bandwidth * gain
            var gain = _state switch
            {
                BbrState.Startup => BbrStartupGrowthFactor,
                BbrState.Drain => BbrDrainGrowthFactor,
                BbrState.ProbeRtt => BbrProbeRttGain,
                _ => ProbeBwGains[_probeBwPhase]
            };
            
            _pacingRate = _maxBandwidth * gain;
        }
        else
        {
            _pacingRate = InitialCwndPackets * _maxDatagramSize * 1_000_000.0 / 100_000;
        }
        
        if (_pacingRate > 0)
        {
            var packetTime = (long)(_maxDatagramSize * 1_000_000.0 / _pacingRate);
            _nextPacketSendTime = currentTime + packetTime;
        }
    }

    /// <summary>
    /// Max filter for bandwidth estimation.
    /// </summary>
    private sealed class MaxFilter
    {
        private readonly long[] _values;
        private readonly int _windowSize;
        private int _count;
        private int _head;

        public MaxFilter(int windowSize)
        {
            _windowSize = windowSize;
            _values = new long[windowSize];
            _count = 0;
            _head = 0;
        }

        public int Count => _count;

        public void Update(long value)
        {
            _values[_head] = value;
            _head = (_head + 1) % _windowSize;
            if (_count < _windowSize)
            {
                _count++;
            }
        }

        public long GetMax()
        {
            if (_count == 0) return 0;
            
            long max = _values[0];
            for (int i = 1; i < _count; i++)
            {
                if (_values[i] > max) max = _values[i];
            }
            return max;
        }

        public long GetMaxAt(int index)
        {
            if (index < 0 || index >= _count) return 0;
            return _values[index];
        }

        public void Reset()
        {
            _count = 0;
            _head = 0;
            Array.Clear(_values, 0, _values.Length);
        }
    }
}
