// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Core.Resilience;

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FBThrift;

/// <summary>
/// Circuit Breaker States (following fbthrift/meta patterns).
/// </summary>
public enum CircuitBreakerState
{
    Closed,     // Normal operation, requests pass through
    Open,       // Failing, requests blocked
    HalfOpen    // Testing if service recovered
}

/// <summary>
/// Circuit Breaker configuration options.
/// </summary>
public sealed class CircuitBreakerOptions
{
    /// <summary>
    /// Number of failures before opening the circuit (default: 5).
    /// </summary>
    public int FailureThreshold { get; set; } = 5;
    
    /// <summary>
    /// Time in milliseconds before transitioning from Open to HalfOpen (default: 30000ms = 30s).
    /// </summary>
    public int OpenTimeoutMs { get; set; } = 30000;
    
    /// <summary>
    /// Number of successful requests in HalfOpen before closing (default: 3).
    /// </summary>
    public int HalfOpenSuccessThreshold { get; set; } = 3;
    
    /// <summary>
    /// Minimum throughput (requests per second) before evaluating failures (default: 10).
    /// </summary>
    public int MinimumThroughput { get; set; } = 10;
    
    /// <summary>
    /// Sampling period in milliseconds for failure rate calculation (default: 10000ms = 10s).
    /// </summary>
    public int SamplingPeriodMs { get; set; } = 10000;
    
    /// <summary>
    /// Failure rate threshold (0.0-1.0) to open circuit (default: 0.5 = 50%).
    /// </summary>
    public double FailureRateThreshold { get; set; } = 0.5;
    
    /// <summary>
    /// Exceptions that should trigger the circuit breaker.
    /// </summary>
    public Type[] TrackedExceptions { get; set; } = 
    [
        typeof(TimeoutException),
        typeof(TaskCanceledException),
        typeof(OperationCanceledException),
        typeof(HttpRequestException),
        typeof(IOException)
    ];
}

/// <summary>
/// Circuit Breaker implementation following fbthrift/meta patterns.
/// </summary>
public sealed class CircuitBreaker : IDisposable
{
    private readonly CircuitBreakerOptions _options;
    private readonly object _lock = new();
    private readonly ConcurrentQueue<(long Timestamp, bool Success)> _requestHistory = new();
    private CircuitBreakerState _state = CircuitBreakerState.Closed;
    private long _failureCount;
    private long _successCount;
    private DateTime _stateChangedAt = DateTime.UtcNow;
    private long _halfOpenSuccesses;
    private readonly Timer? _stateTransitionTimer;
    private bool _disposed;
    
    public CircuitBreakerState State => _state;
    public long FailureCount => _failureCount;
    public long SuccessCount => _successCount;
    public DateTime StateChangedAt => _stateChangedAt;
    public double CurrentFailureRate => GetCurrentFailureRate();
    
    public event Action<CircuitBreakerState, CircuitBreakerState>? StateChanged;
    
    public CircuitBreaker(CircuitBreakerOptions? options = null)
    {
        _options = options ?? new CircuitBreakerOptions();
        
        // Timer for automatic state transitions
        _stateTransitionTimer = new Timer(CheckStateTransition, null, 
            TimeSpan.FromMilliseconds(_options.OpenTimeoutMs), 
            TimeSpan.FromMilliseconds(_options.OpenTimeoutMs));
    }
    
    /// <summary>
    /// Executes an action with circuit breaker protection.
    /// </summary>
    public async Task<T> ExecuteAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken = default)
    {
        if (!TryEnter())
        {
            throw new CircuitBreakerOpenException("Circuit breaker is open");
        }
        
        try
        {
            var result = await action().ConfigureAwait(false);
            RecordSuccess();
            return result;
        }
        catch (Exception ex) when (ShouldTrackException(ex))
        {
            RecordFailure();
            throw;
        }
    }
    
    /// <summary>
    /// Executes an action with circuit breaker protection (void).
    /// </summary>
    public async Task ExecuteAsync(Func<Task> action, CancellationToken cancellationToken = default)
    {
        if (!TryEnter())
        {
            throw new CircuitBreakerOpenException("Circuit breaker is open");
        }
        
        try
        {
            await action().ConfigureAwait(false);
            RecordSuccess();
        }
        catch (Exception ex) when (ShouldTrackException(ex))
        {
            RecordFailure();
            throw;
        }
    }
    
    /// <summary>
    /// Attempts to enter the circuit breaker.
    /// </summary>
    private bool TryEnter()
    {
        lock (_lock)
        {
            switch (_state)
            {
                case CircuitBreakerState.Closed:
                    return true;
                    
                case CircuitBreakerState.Open:
                    // Check if timeout has elapsed to transition to HalfOpen
                    if (DateTime.UtcNow - _stateChangedAt >= TimeSpan.FromMilliseconds(_options.OpenTimeoutMs))
                    {
                        TransitionToHalfOpen();
                        return true;
                    }
                    return false;
                    
                case CircuitBreakerState.HalfOpen:
                    return true;
                    
                default:
                    return false;
            }
        }
    }
    
    /// <summary>
    /// Records a successful request.
    /// </summary>
    public void RecordSuccess()
    {
        lock (_lock)
        {
            _successCount++;
            _requestHistory.Enqueue((Stopwatch.GetTimestamp(), true));
            CleanupHistory();
            
            if (_state == CircuitBreakerState.HalfOpen)
            {
                _halfOpenSuccesses++;
                if (_halfOpenSuccesses >= _options.HalfOpenSuccessThreshold)
                {
                    TransitionToClosed();
                }
            }
        }
    }
    
    /// <summary>
    /// Records a failed request.
    /// </summary>
    public void RecordFailure()
    {
        lock (_lock)
        {
            _failureCount++;
            _requestHistory.Enqueue((Stopwatch.GetTimestamp(), false));
            CleanupHistory();
            
            if (_state == CircuitBreakerState.HalfOpen)
            {
                // Any failure in HalfOpen reopens the circuit
                TransitionToOpen();
            }
            else if (_state == CircuitBreakerState.Closed)
            {
                CheckFailureThreshold();
            }
        }
    }
    
    private void CheckFailureThreshold()
    {
        if (_requestHistory.Count < _options.MinimumThroughput)
            return;
        
        var failureRate = GetCurrentFailureRateLocked();
        if (failureRate >= _options.FailureRateThreshold)
        {
            TransitionToOpen();
        }
    }
    
    private double GetCurrentFailureRate()
    {
        lock (_lock)
        {
            return GetCurrentFailureRateLocked();
        }
    }
    
    private double GetCurrentFailureRateLocked()
    {
        if (_requestHistory.IsEmpty) return 0.0;
        
        var total = _requestHistory.Count;
        var failures = 0;
        
        foreach (var (_, success) in _requestHistory)
        {
            if (!success) failures++;
        }
        
        return (double)failures / total;
    }
    
    private void CleanupHistory()
    {
        var cutoff = Stopwatch.GetTimestamp() - TimeSpan.FromMilliseconds(_options.SamplingPeriodMs).Ticks;
        
        while (_requestHistory.TryPeek(out var entry))
        {
            if (entry.Timestamp < cutoff)
            {
                _requestHistory.TryDequeue(out _);
            }
            else
            {
                break;
            }
        }
    }
    
    private void TransitionToOpen()
    {
        if (_state == CircuitBreakerState.Open) return;
        
        var oldState = _state;
        _state = CircuitBreakerState.Open;
        _stateChangedAt = DateTime.UtcNow;
        _halfOpenSuccesses = 0;
        
        StateChanged?.Invoke(oldState, _state);
    }
    
    private void TransitionToHalfOpen()
    {
        if (_state == CircuitBreakerState.HalfOpen) return;
        
        var oldState = _state;
        _state = CircuitBreakerState.HalfOpen;
        _stateChangedAt = DateTime.UtcNow;
        _halfOpenSuccesses = 0;
        
        StateChanged?.Invoke(oldState, _state);
    }
    
    private void TransitionToClosed()
    {
        if (_state == CircuitBreakerState.Closed) return;
        
        var oldState = _state;
        _state = CircuitBreakerState.Closed;
        _stateChangedAt = DateTime.UtcNow;
        _failureCount = 0;
        _halfOpenSuccesses = 0;
        _requestHistory.Clear();
        
        StateChanged?.Invoke(oldState, _state);
    }
    
    private void CheckStateTransition(object? state)
    {
        lock (_lock)
        {
            if (_state == CircuitBreakerState.Open &&
                DateTime.UtcNow - _stateChangedAt >= TimeSpan.FromMilliseconds(_options.OpenTimeoutMs))
            {
                TransitionToHalfOpen();
            }
        }
    }
    
    private bool ShouldTrackException(Exception ex)
    {
        foreach (var trackedType in _options.TrackedExceptions)
        {
            if (trackedType.IsAssignableFrom(ex.GetType()))
                return true;
        }
        return false;
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stateTransitionTimer?.Dispose();
    }
}

/// <summary>
/// Exception thrown when circuit breaker is open.
/// </summary>
public sealed class CircuitBreakerOpenException : Exception
{
    public CircuitBreakerOpenException(string message) : base(message) { }
    public CircuitBreakerOpenException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Retry Policy configuration.
/// </summary>
public sealed class RetryPolicyOptions
{
    /// <summary>
    /// Maximum number of retry attempts (default: 3).
    /// </summary>
    public int MaxAttempts { get; set; } = 3;
    
    /// <summary>
    /// Base delay between retries in milliseconds (default: 100ms).
    /// </summary>
    public int BaseDelayMs { get; set; } = 100;
    
    /// <summary>
    /// Maximum delay between retries in milliseconds (default: 10000ms = 10s).
    /// </summary>
    public int MaxDelayMs { get; set; } = 10000;
    
    /// <summary>
    /// Backoff multiplier for exponential backoff (default: 2.0).
    /// </summary>
    public double BackoffMultiplier { get; set; } = 2.0;
    
    /// <summary>
    /// Jitter factor to prevent thundering herd (default: 0.1 = 10%).
    /// </summary>
    public double JitterFactor { get; set; } = 0.1;
    
    /// <summary>
    /// Exceptions that should trigger a retry.
    /// </summary>
    public Type[] RetryableExceptions { get; set; } = 
    [
        typeof(TimeoutException),
        typeof(TaskCanceledException),
        typeof(OperationCanceledException),
        typeof(HttpRequestException),
        typeof(IOException)
    ];
    
    /// <summary>
    /// HTTP status codes that should trigger a retry.
    /// </summary>
    public int[] RetryableStatusCodes { get; set; } = 
    [
        408,  // Request Timeout
        429,  // Too Many Requests
        500,  // Internal Server Error
        502,  // Bad Gateway
        503,  // Service Unavailable
        504   // Gateway Timeout
    ];
}

/// <summary>
/// Retry Policy with exponential backoff and jitter.
/// </summary>
public sealed class RetryPolicy
{
    private readonly RetryPolicyOptions _options;
    private readonly Random _random = new();
    
    public RetryPolicy(RetryPolicyOptions? options = null)
    {
        _options = options ?? new RetryPolicyOptions();
    }
    
    /// <summary>
    /// Executes an action with retry logic.
    /// </summary>
    public async Task<T> ExecuteAsync<T>(
        Func<Task<T>> action, 
        CancellationToken cancellationToken = default,
        IProgress<RetryAttempt>? progress = null)
    {
        Exception? lastException = null;
        
        for (int attempt = 0; attempt <= _options.MaxAttempts; attempt++)
        {
            try
            {
                if (attempt > 0)
                {
                    // Calculate delay with exponential backoff and jitter
                    var delay = CalculateDelay(attempt);
                    
                    progress?.Report(new RetryAttempt(attempt, delay, lastException));
                    
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                
                return await action().ConfigureAwait(false);
            }
            catch (Exception ex) when (ShouldRetry(ex, attempt))
            {
                lastException = ex;
                
                if (attempt == _options.MaxAttempts)
                {
                    throw new RetryExhaustedException($"Retry policy exhausted after {_options.MaxAttempts} attempts", ex);
                }
            }
        }
        
        throw new RetryExhaustedException($"Retry policy exhausted after {_options.MaxAttempts} attempts", lastException!);
    }
    
    /// <summary>
    /// Executes an action with retry logic (void).
    /// </summary>
    public async Task ExecuteAsync(
        Func<Task> action, 
        CancellationToken cancellationToken = default,
        IProgress<RetryAttempt>? progress = null)
    {
        await ExecuteAsync(async () => 
        {
            await action().ConfigureAwait(false);
            return true;
        }, cancellationToken, progress).ConfigureAwait(false);
    }
    
    private TimeSpan CalculateDelay(int attempt)
    {
        var delay = _options.BaseDelayMs * Math.Pow(_options.BackoffMultiplier, attempt);
        delay = Math.Min(delay, _options.MaxDelayMs);
        
        // Add jitter
        var jitter = delay * _options.JitterFactor * (_random.NextDouble() * 2 - 1);
        delay += jitter;
        
        return TimeSpan.FromMilliseconds(Math.Max(0, delay));
    }
    
    private bool ShouldRetry(Exception ex, int attempt)
    {
        if (attempt >= _options.MaxAttempts)
            return false;
        
        // Check if exception type is retryable
        foreach (var retryableType in _options.RetryableExceptions)
        {
            if (retryableType.IsAssignableFrom(ex.GetType()))
                return true;
        }
        
        // Check for HTTP status codes
        if (ex is HttpRequestException httpEx && httpEx.StatusCode.HasValue)
        {
            foreach (var code in _options.RetryableStatusCodes)
            {
                if ((int)httpEx.StatusCode.Value == code)
                    return true;
            }
        }
        
        return false;
    }
}

/// <summary>
/// Information about a retry attempt.
/// </summary>
public readonly struct RetryAttempt
{
    public int AttemptNumber { get; }
    public TimeSpan Delay { get; }
    public Exception? LastException { get; }
    
    public RetryAttempt(int attemptNumber, TimeSpan delay, Exception? lastException)
    {
        AttemptNumber = attemptNumber;
        Delay = delay;
        LastException = lastException;
    }
}

/// <summary>
/// Exception thrown when retry policy is exhausted.
/// </summary>
public sealed class RetryExhaustedException : Exception
{
    public int Attempts { get; }
    public Exception? LastException { get; }
    
    public RetryExhaustedException(string message, Exception? inner) : base(message, inner)
    {
        Attempts = 0;
        LastException = inner;
    }
}

/// <summary>
/// Combined Resilience Policy (Circuit Breaker + Retry).
/// </summary>
public sealed class ResiliencePolicy
{
    private readonly CircuitBreaker _circuitBreaker;
    private readonly RetryPolicy _retryPolicy;
    
    public CircuitBreaker CircuitBreaker => _circuitBreaker;
    public RetryPolicy RetryPolicy => _retryPolicy;
    
    public ResiliencePolicy(CircuitBreakerOptions? circuitBreakerOptions = null, RetryPolicyOptions? retryPolicyOptions = null)
    {
        _circuitBreaker = new CircuitBreaker(circuitBreakerOptions);
        _retryPolicy = new RetryPolicy(retryPolicyOptions);
    }
    
    /// <summary>
    /// Executes an action with both circuit breaker and retry protection.
    /// </summary>
    public async Task<T> ExecuteAsync<T>(
        Func<Task<T>> action, 
        CancellationToken cancellationToken = default,
        IProgress<RetryAttempt>? retryProgress = null)
    {
        return await _circuitBreaker.ExecuteAsync(async () => 
        {
            return await _retryPolicy.ExecuteAsync(action, cancellationToken, retryProgress).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }
    
    /// <summary>
    /// Executes an action with both circuit breaker and retry protection (void).
    /// </summary>
    public async Task ExecuteAsync(
        Func<Task> action, 
        CancellationToken cancellationToken = default,
        IProgress<RetryAttempt>? retryProgress = null)
    {
        await _circuitBreaker.ExecuteAsync(async () => 
        {
            await _retryPolicy.ExecuteAsync(action, cancellationToken, retryProgress).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Fluent builders for ResiliencePolicy following fbthrift patterns.
/// </summary>
public static class ResiliencePolicyBuilder
{
    /// <summary>
    /// Adds a circuit breaker to the RPC options.
    /// </summary>
    public static Action<RpcOptions> WithCircuitBreaker(
        int failureThreshold = 5,
        int openTimeoutMs = 30000,
        int halfOpenSuccessThreshold = 3,
        double failureRateThreshold = 0.5)
    {
        return opts =>
        {
            opts.State["CircuitBreaker"] = new CircuitBreakerOptions
            {
                FailureThreshold = failureThreshold,
                OpenTimeoutMs = openTimeoutMs,
                HalfOpenSuccessThreshold = halfOpenSuccessThreshold,
                FailureRateThreshold = failureRateThreshold
            };
        };
    }
    
    /// <summary>
    /// Adds a retry policy to the RPC options.
    /// </summary>
    public static Action<RpcOptions> WithRetryPolicy(
        int maxAttempts = 3,
        int baseDelayMs = 100,
        int maxDelayMs = 10000,
        double backoffMultiplier = 2.0,
        double jitterFactor = 0.1)
    {
        return opts =>
        {
            opts.State["RetryPolicy"] = new RetryPolicyOptions
            {
                MaxAttempts = maxAttempts,
                BaseDelayMs = baseDelayMs,
                MaxDelayMs = maxDelayMs,
                BackoffMultiplier = backoffMultiplier,
                JitterFactor = jitterFactor
            };
        };
    }
    
    /// <summary>
    /// Adds a combined resilience policy to the RPC options.
    /// </summary>
    public static Action<RpcOptions> WithResiliencePolicy(
        CircuitBreakerOptions? circuitBreakerOptions = null,
        RetryPolicyOptions? retryPolicyOptions = null)
    {
        return opts =>
        {
            opts.State["ResiliencePolicy"] = new ResiliencePolicy(circuitBreakerOptions, retryPolicyOptions);
        };
    }
}