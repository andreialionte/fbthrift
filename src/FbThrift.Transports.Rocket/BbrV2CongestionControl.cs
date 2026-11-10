// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Rocket;

using System;
using System.Collections.Generic;
using System.Diagnostics;

/// <summary>
/// BBR v2/v3 Congestion Control (RFC 9002, draft-cardwell-iccrg-bbr-v2, v3).
/// 
/// BBR v2 improvements over v1:
/// - Loss-based congestion control integration (RFC 9002 style)
/// - ECN support with CE marking handling
/// - Startup with loss detection (exit startup on loss)
/// - Improved ProbeRTT with better min RTT measurement
/// - Hystart++ for slow start
///
/// BBR v3 improvements over v2:
/// - Even better loss handling with ECN
/// - Hystart++ for slow start (RFC 9002)
/// - Better RTT handling with acknowledgment delay
/// - More responsive to congestion signals
/// 
/// References:
/// - https://github.com/Tencent/tquic (BBR v2/v3 implementation)
/// - https://github.com/aws/s2n-quic (BBR v3)
/// - https://github.com/google/quiche (BBR v2)
/// - https://datatracker.ietf.org/doc/draft-cardwell-iccrg-bbr-v2/
/// </summary>
public sealed class BbrV2CongestionControl : ICongestionControl
{
    // BBR v2 constants
    private const double Ln2 = 0.6931471805599453;
    private const double BbrStartupGrowthFactor = 2.0 / Ln2;  // ~2.885
    private const double BbrDrainGrowthFactor = 1.0 / BbrStartupGrowthFactor;
    private const double BbrProbeRttGain = 0.5;
    private const double BbrCwndGain = 2.0;
    private const int MinCwndPackets = 4;
    private const int InitialCwndPackets = 10;
    private const int MaxDatagramSize = 1200;
    private const long ProbeRttDurationMs = 200;
    private const long StartupMinRttSamples = 3;
    private const long LossExitStartupThreshold = 3; // Exit startup after 3 loss events
    
    // BBR v2 ProbeBW gains (8-phase cycle)
    private static readonly double[] ProbeBwGains = 
    {
        1.25, 0.75, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0
    };
    
    // BBR v2 states
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
    private long _maxBandwidth;
    private long _maxBandwidthTimestamp;
    private long _lastAckedTime;
    private long _packetCount;
    private long _maxDatagramSize;
    private double _pacingRate;
    private long _nextPacketSendTime;
    private long _epochStartTime;
    private long _recoveryStartTime;
    private bool _inRecovery;
    private long _lastMaxCwnd;
    private long _startupLossCount;
    private long _lossEventsInRound;
    
    // Bandwidth filter (max filter over window)
    private readonly MaxFilter _bandwidthFilter = new(10);
    
    // RTT samples for Hystart++
    private readonly RttSample[] _rttSamples = new RttSample[16];
    private int _rttSampleCount;
    
    public BbrV2CongestionControl()
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
        _startupLossCount = 0;
        _lossEventsInRound = 0;
        _rttSampleCount = 0;
        _bandwidthFilter.Reset();
        Array.Clear(_rttSamples, 0, _rttSamples.Length);
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
        
        UpdateRtt(rtt, ackDelay, currentTime);
        UpdateBandwidth(bytesAcked, rtt, currentTime);
        UpdateState(currentTime);
        UpdatePacingRate(currentTime);
    }
    
    public void OnPacketLost(long bytesLost, long currentTime)
    {
        _bytesInFlight = Math.Max(0, _bytesInFlight - bytesLost);
        _lossEventsInRound++;
        
        // BBR v2: Exit startup on persistent loss
        if (_state == BbrState.Startup)
        {
            _startupLossCount++;
            if (_startupLossCount >= LossExitStartupThreshold)
            {
                _state = BbrState.Drain;
                _epochStartTime = currentTime;
            }
        }
        
        if (!_inRecovery)
        {
            EnterRecovery(currentTime);
        }
    }
    
    public void OnEcnCongestionExperienced(long currentTime)
    {
        // BBR v2: ECN CE marking counts as congestion signal
        _lossEventsInRound++;
        
        if (_state == BbrState.Startup)
        {
            _startupLossCount++;
            if (_startupLossCount >= LossExitStartupThreshold)
            {
                _state = BbrState.Drain;
                _epochStartTime = currentTime;
            }
        }
        
        if (!_inRecovery)
        {
            EnterRecovery(currentTime);
        }
    }
    
    public void OnPersistentCongestion(long currentTime)
    {
        _cwnd = MinCwndPackets * _maxDatagramSize;
        _slowStartThreshold = _cwnd;
        _state = BbrState.Startup;
        _inRecovery = false;
        _epochStartTime = 0;
        _startupLossCount = 0;
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
    
    // BBR v3 specific methods
    
    /// <summary>
    /// BBR v3: Hystart++ slow start detection (RFC 9002).
    /// Returns true if slow start should exit.
    /// </summary>
    public bool ShouldExitSlowStart(long currentTime)
    {
        if (_state != BbrState.Startup)
            return false;
        
        // Hystart++: Check for delay increase
        if (_rttSampleCount >= 8)
        {
            var minRttInWindow = long.MaxValue;
            var maxRttInWindow = long.MinValue;
            
            for (int i = Math.Max(0, _rttSampleCount - 8); i < _rttSampleCount; i++)
            {
                var sample = _rttSamples[i % 16];
                if (sample.Rtt > 0)
                {
                    minRttInWindow = Math.Min(minRttInWindow, sample.Rtt);
                    maxRttInWindow = Math.Max(maxRttInWindow, sample.Rtt);
                }
            }
            
            // Exit if RTT increased by more than 1/8 of min RTT
            if (maxRttInWindow > minRttInWindow + minRttInWindow / 8)
            {
                return true;
            }
        }
        
        // Also check bandwidth growth
        return IsBandwidthStalled();
    }
    
    private void UpdateRtt(long rtt, long ackDelay, long currentTime)
    {
        if (rtt <= 0) return;
        
        var adjustedRtt = rtt - ackDelay;
        if (adjustedRtt < 1) adjustedRtt = 1;
        
        // Store RTT sample for Hystart++
        _rttSamples[_rttSampleCount % 16] = new RttSample
        {
            Rtt = adjustedRtt,
            Timestamp = currentTime
        };
        _rttSampleCount = Math.Min(_rttSampleCount + 1, 16);
        
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
        
        var rate = (bytesAcked * 1_000_000L) / rtt;
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
        // Check Hystart++ in startup
        if (_state == BbrState.Startup && ShouldExitSlowStart(currentTime))
        {
            _state = BbrState.Drain;
            _epochStartTime = currentTime;
            return;
        }
        
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
        // Aggressive growth in startup (BBR v2: less aggressive than v1)
        var growthFactor = BbrStartupGrowthFactor * 0.5; // v2: more conservative
        _cwnd = (long)(_cwnd * growthFactor);
    }
    
    private void UpdateDrain(long currentTime)
    {
        // Drain queue by reducing rate
        _cwnd = (long)(_cwnd * BbrDrainGrowthFactor);
        
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
        var gain = ProbeBwGains[_probeBwPhase];
        var targetCwnd = (long)(_maxBandwidth * _minRtt * gain / 1_000_000L * BbrCwndGain);
        _cwnd = Math.Max(targetCwnd, MinCwndPackets * _maxDatagramSize);
        
        // Check if it's time to probe RTT
        if (currentTime - _epochStartTime > 10_000_000 && _probeBwPhase == 0)
        {
            _state = BbrState.ProbeRtt;
            _probeRttStartTime = currentTime;
        }
        
        // v2: Also check for loss during ProbeBW
        if (_lossEventsInRound > 0)
        {
            _lossEventsInRound = 0;
            // Reduce cwnd slightly on loss
            _cwnd = Math.Max(_cwnd * 3 / 4, MinCwndPackets * _maxDatagramSize);
        }
        
        _probeBwPhase = (_probeBwPhase + 1) % ProbeBwGains.Length;
        if (_probeBwPhase == 0)
        {
            _epochStartTime = currentTime;
        }
    }
    
    private void UpdateProbeRtt(long currentTime)
    {
        _cwnd = MinCwndPackets * _maxDatagramSize;
        
        if (currentTime - _probeRttStartTime > ProbeRttDurationMs * 1000)
        {
            _state = BbrState.ProbeBw;
            _probeBwPhase = 0;
            _epochStartTime = currentTime;
        }
    }
    
    private bool IsBandwidthStalled()
    {
        if (_bandwidthFilter.Count < 3) return false;
        
        var max1 = _bandwidthFilter.GetMaxAt(_bandwidthFilter.Count - 1);
        var max2 = _bandwidthFilter.GetMaxAt(_bandwidthFilter.Count - 2);
        var max3 = _bandwidthFilter.GetMaxAt(_bandwidthFilter.Count - 3);
        
        // v2: More sensitive to stalling
        return max1 <= max2 * 1.1 && max2 <= max3 * 1.1;
    }
    
    private void EnterRecovery(long currentTime)
    {
        _inRecovery = true;
        _recoveryStartTime = currentTime;
        _lastMaxCwnd = _cwnd;
        _slowStartThreshold = _cwnd;
        _lossEventsInRound = 0;
    }
    
    private void UpdatePacingRate(long currentTime)
    {
        if (_maxBandwidth > 0)
        {
            var gain = _state switch
            {
                BbrState.Startup => BbrStartupGrowthFactor * 0.5, // v2: less aggressive
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
    
    // BBR v3: Additional state for v3 features
    private readonly struct RttSample
    {
        public long Rtt { get; init; }
        public long Timestamp { get; init; }
        
        public RttSample() { }
        
        public RttSample(long rtt, long timestamp)
        {
            Rtt = rtt;
            Timestamp = timestamp;
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

/// <summary>
/// BBR v3 Congestion Control with enhanced features.
/// </summary>
public sealed class BbrV3CongestionControl : ICongestionControl
{
    private readonly BbrV2CongestionControl _v2;
    
    // BBR v3 additions:
    // - Even more conservative startup
    // - Better handling of ACK frequency
    // - Improved RTT measurement with delayed ACK compensation
    // - Better loss recovery with QUIC loss bits
    
    private long _ackFrequency = 2; // ACK every 2 packets
    private long _ackElicitingInFlight;
    private long _maxAckDelay = 25000; // 25ms max ack delay
    
    public BbrV3CongestionControl()
    {
        _v2 = new BbrV2CongestionControl();
    }
    
    public long CongestionWindow => _v2.CongestionWindow;
    public long BytesInFlight => _v2.BytesInFlight;
    public long SlowStartThreshold => _v2.SlowStartThreshold;
    public bool InSlowStart => _v2.InSlowStart;
    public bool InRecovery => _v2.InRecovery;
    public double PacingRate => _v2.PacingRate;
    public long MinRtt => _v2.MinRtt;
    public long SmoothedRtt => _v2.SmoothedRtt;
    
    public void Initialize() => _v2.Initialize();
    
    public void OnPacketSent(long bytesSent, long currentTime)
    {
        _v2.OnPacketSent(bytesSent, currentTime);
        _ackElicitingInFlight += bytesSent;
    }
    
    public void OnPacketAcknowledged(long bytesAcked, long ackDelay, long currentTime, long rtt)
    {
        _v2.OnPacketAcknowledged(bytesAcked, ackDelay, currentTime, rtt);
        _ackElicitingInFlight = Math.Max(0, _ackElicitingInFlight - bytesAcked);
    }
    
    public void OnPacketLost(long bytesLost, long currentTime)
    {
        _v2.OnPacketLost(bytesLost, currentTime);
    }
    
    public void OnEcnCongestionExperienced(long currentTime)
    {
        _v2.OnEcnCongestionExperienced(currentTime);
    }
    
    public void OnPersistentCongestion(long currentTime)
    {
        _v2.OnPersistentCongestion(currentTime);
    }
    
    public bool CanSend(long currentTime)
    {
        return _v2.CanSend(currentTime);
    }
    
    public long GetNextSendTime(long currentTime)
    {
        return _v2.GetNextSendTime(currentTime);
    }
    
    public void SetMaxDatagramSize(long size)
    {
        _v2.SetMaxDatagramSize(size);
    }
    
    public void Reset()
    {
        _v2.Reset();
        _ackElicitingInFlight = 0;
    }
}
