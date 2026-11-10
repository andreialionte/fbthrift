// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.LoadBalancing;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Configuration for retry behavior, following OCI SDK's RetryConfiguration pattern.
/// </summary>
public sealed class RetryConfiguration
{
    /// <summary>
    /// Gets or sets the maximum number of retry attempts (default: 3).
    /// </summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>
    /// Gets or sets the base delay between retries (default: 100ms).
    /// </summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Gets or sets the maximum delay between retries (default: 30s).
    /// </summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets whether to use jitter in retry delays (default: true).
    /// </summary>
    public bool UseJitter { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to retry on transient errors (default: true).
    /// </summary>
    public bool RetryOnTransientErrors { get; set; } = true;

    /// <summary>
    /// Gets the default retry configuration.
    /// </summary>
    public static RetryConfiguration Default => new();

    /// <summary>
    /// Calculates the delay for a given retry attempt using exponential backoff with optional jitter.
    /// </summary>
    /// <param name="attempt">The current retry attempt (0-indexed).</param>
    /// <returns>The delay to wait before the next retry.</returns>
    public TimeSpan CalculateDelay(int attempt)
    {
        var delay = TimeSpan.FromMilliseconds(
            Math.Min(
                BaseDelay.TotalMilliseconds * Math.Pow(2, attempt),
                MaxDelay.TotalMilliseconds));

        if (UseJitter)
        {
            var jitter = Random.Shared.NextDouble() * delay.TotalMilliseconds * 0.2;
            delay = TimeSpan.FromMilliseconds(delay.TotalMilliseconds + jitter);
        }

        return delay;
    }
}

/// <summary>
/// Configuration for waiter behavior, following OCI SDK's WaiterConfiguration pattern.
/// </summary>
public sealed class WaiterConfiguration
{
    /// <summary>
    /// Gets or sets the maximum time to wait (default: 30s).
    /// </summary>
    public TimeSpan MaxWaitTime { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the interval between polling attempts (default: 1s).
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets the default waiter configuration.
    /// </summary>
    public static WaiterConfiguration DefaultWaiterConfiguration => new();

    /// <summary>
    /// Calculates the remaining time for a waiter.
    /// </summary>
    /// <param name="startTime">The start time of the wait operation.</param>
    /// <returns>true if the maximum wait time has not been exceeded.</returns>
    public bool HasTimeRemaining(DateTimeOffset startTime)
    {
        return DateTimeOffset.UtcNow - startTime < MaxWaitTime;
    }
}

/// <summary>
/// Details about an API call for tracing and logging, following OCI SDK's ApiDetails pattern.
/// </summary>
public sealed class ApiDetails
{
    /// <summary>
    /// Gets or sets the service name.
    /// </summary>
    public string ServiceName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the operation name.
    /// </summary>
    public string OperationName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the request endpoint (HTTP method + URL).
    /// </summary>
    public string RequestEndpoint { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the API reference link.
    /// </summary>
    public string ApiReferenceLink { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the user agent string.
    /// </summary>
    public string UserAgent { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the latency in milliseconds.
    /// </summary>
    public long LatencyMs { get; set; }
}

/// <summary>
/// A waiter that polls for a resource to reach a target state, following OCI SDK's Waiter pattern.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
public sealed class Waiter<TRequest, TResponse> where TResponse : class
{
    private readonly WaiterConfiguration _config;
    private readonly WaiterAgent<TRequest, TResponse> _agent;

    /// <summary>
    /// Initializes a new instance of <see cref="Waiter{TRequest, TResponse}"/>.
    /// </summary>
    /// <param name="config">The waiter configuration.</param>
    /// <param name="agent">The waiter agent that performs the polling.</param>
    public Waiter(WaiterConfiguration config, WaiterAgent<TRequest, TResponse> agent)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
    }

    /// <summary>
    /// Waits for the resource to reach a target state.
    /// </summary>
    /// <param name="request">The request to poll.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation.</param>
    /// <returns>The final response when the target state is reached.</returns>
    public async Task<TResponse> WaitAsync(TRequest request, CancellationToken cancellationToken = default)
    {
        var startTime = DateTimeOffset.UtcNow;

        while (_config.HasTimeRemaining(startTime))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var response = await _agent.FetchAsync(request, cancellationToken).ConfigureAwait(false);

            if (_agent.IsTargetState(response))
            {
                return response;
            }

            if (_agent.IsTerminalState(response))
            {
                throw new InvalidOperationException(
                    $"Resource reached terminal state before target state. Last response: {response}");
            }

            await Task.Delay(_config.PollInterval, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Waiter timed out after {_config.MaxWaitTime} waiting for target state.");
    }
}

/// <summary>
/// Agent that performs the actual polling for a waiter.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
public sealed class WaiterAgent<TRequest, TResponse> where TResponse : class
{
    private readonly TRequest _request;
    private readonly Func<TRequest, CancellationToken, Task<TResponse>> _fetchFunc;
    private readonly Func<TResponse, bool> _isTargetState;
    private readonly Func<TResponse, bool>? _isTerminalState;

    /// <summary>
    /// Initializes a new instance of <see cref="WaiterAgent{TRequest, TResponse}"/>.
    /// </summary>
    /// <param name="request">The request to poll.</param>
    /// <param name="fetchFunc">The function to fetch the resource state.</param>
    /// <param name="isTargetState">Predicate to check if the target state is reached.</param>
    /// <param name="isTerminalState">Optional predicate to check if a terminal state is reached.</param>
    public WaiterAgent(
        TRequest request,
        Func<TRequest, CancellationToken, Task<TResponse>> fetchFunc,
        Func<TResponse, bool> isTargetState,
        Func<TResponse, bool>? isTerminalState = null)
    {
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _fetchFunc = fetchFunc ?? throw new ArgumentNullException(nameof(fetchFunc));
        _isTargetState = isTargetState ?? throw new ArgumentNullException(nameof(isTargetState));
        _isTerminalState = isTerminalState;
    }

    /// <summary>
    /// Fetches the current resource state.
    /// </summary>
    public Task<TResponse> FetchAsync(TRequest request, CancellationToken cancellationToken)
    {
        return _fetchFunc(request, cancellationToken);
    }

    /// <summary>
    /// Checks if the response is in the target state.
    /// </summary>
    public bool IsTargetState(TResponse response)
    {
        return _isTargetState(response);
    }

    /// <summary>
    /// Checks if the response is in a terminal state.
    /// </summary>
    public bool IsTerminalState(TResponse response)
    {
        return _isTerminalState?.Invoke(response) ?? false;
    }
}

/// <summary>
/// A paginator for paginated endpoint listing, following OCI SDK's Paginator pattern.
/// </summary>
/// <typeparam name="TItem">The item type.</typeparam>
public sealed class Paginator<TItem> : IAsyncEnumerable<TItem>
{
    private readonly Func<PaginationToken, CancellationToken, Task<PaginationResult<TItem>>> _fetchFunc;
    private readonly PaginationToken _initialToken;

    /// <summary>
    /// Initializes a new instance of <see cref="Paginator{TItem}"/>.
    /// </summary>
    /// <param name="fetchFunc">The function to fetch a page of items.</param>
    /// <param name="initialToken">The initial pagination token.</param>
    public Paginator(
        Func<PaginationToken, CancellationToken, Task<PaginationResult<TItem>>> fetchFunc,
        PaginationToken? initialToken = null)
    {
        _fetchFunc = fetchFunc ?? throw new ArgumentNullException(nameof(fetchFunc));
        _initialToken = initialToken ?? PaginationToken.First;
    }

    /// <summary>
    /// Gets the async enumerator for paginated items.
    /// </summary>
    public IAsyncEnumerator<TItem> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        return new PaginatorEnumerator<TItem>(_fetchFunc, _initialToken, cancellationToken);
    }

    /// <summary>
    /// Gets the async enumerator for paginated items.
    /// </summary>
    IAsyncEnumerator<TItem> IAsyncEnumerable<TItem>.GetAsyncEnumerator(CancellationToken cancellationToken)
    {
        return GetAsyncEnumerator(cancellationToken);
    }
}

/// <summary>
/// A token for pagination, representing the current page position.
/// </summary>
public sealed class PaginationToken
{
    /// <summary>
    /// Gets the first page token.
    /// </summary>
    public static PaginationToken First => new(null);

    /// <summary>
    /// Gets the next page token based on the current token and next page marker.
    /// </summary>
    public static PaginationToken Next(string? nextPageToken) => new(nextPageToken);

    /// <summary>
    /// Gets the next page marker, or null if this is the first page.
    /// </summary>
    public string? NextPageMarker { get; }

    private PaginationToken(string? nextPageMarker)
    {
        NextPageMarker = nextPageMarker;
    }
}

/// <summary>
/// The result of a paginated fetch operation.
/// </summary>
/// <typeparam name="TItem">The item type.</typeparam>
public sealed class PaginationResult<TItem>
{
    /// <summary>
    /// Gets the items in this page.
    /// </summary>
    public IReadOnlyList<TItem> Items { get; init; } = new List<TItem>();

    /// <summary>
    /// Gets the token for the next page, or null if there are no more pages.
    /// </summary>
    public PaginationToken? NextPageToken { get; init; }

    /// <summary>
    /// Gets whether there are more pages to fetch.
    /// </summary>
    public bool HasMorePages => NextPageToken != null;
}

/// <summary>
/// Enumerator for paginated items.
/// </summary>
/// <typeparam name="TItem">The item type.</typeparam>
internal sealed class PaginatorEnumerator<TItem> : IAsyncEnumerator<TItem>
{
    private readonly Func<PaginationToken, CancellationToken, Task<PaginationResult<TItem>>> _fetchFunc;
    private readonly CancellationToken _cancellationToken;
    private PaginationToken _currentToken;
    private PaginationResult<TItem> _currentPage;
    private int _currentIndex;
    private TItem? _current;
    private bool _hasMorePages;
    private bool _initialized;

    public PaginatorEnumerator(
        Func<PaginationToken, CancellationToken, Task<PaginationResult<TItem>>> fetchFunc,
        PaginationToken initialToken,
        CancellationToken cancellationToken)
    {
        _fetchFunc = fetchFunc;
        _currentToken = initialToken;
        _cancellationToken = cancellationToken;
        _hasMorePages = true;
    }

    public TItem Current => _current!;

    public ValueTask<bool> MoveNextAsync()
    {
        return MoveNextAsyncCore();
    }

    private async ValueTask<bool> MoveNextAsyncCore()
    {
        if (!_initialized)
        {
            _initialized = true;
        }
        else
        {
            _currentIndex++;
        }

        if (_currentIndex < _currentPage.Items.Count)
        {
            _current = _currentPage.Items[_currentIndex];
            return true;
        }

        if (!_hasMorePages)
        {
            _current = default!;
            return false;
        }

        _currentPage = await _fetchFunc(_currentToken, _cancellationToken).ConfigureAwait(false);
        _currentToken = _currentPage.NextPageToken ?? PaginationToken.First;
        _hasMorePages = _currentPage.HasMorePages;
        _currentIndex = 0;

        if (_currentPage.Items.Count == 0)
        {
            _current = default!;
            return false;
        }

        _current = _currentPage.Items[0];
        return true;
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
    }
}
