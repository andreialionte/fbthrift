namespace FbThrift.Core.Streaming;

/// <summary>
/// Represents a streaming response with an initial response followed by a stream of elements.
/// Matches fbthrift's ResponseAndStream pattern.
/// </summary>
/// <typeparam name="TResponse">The type of the initial response.</typeparam>
/// <typeparam name="TElement">The type of stream elements.</typeparam>
public sealed class ResponseAndStream<TResponse, TElement>
{
    /// <summary>
    /// Gets the initial response.
    /// </summary>
    public TResponse Response { get; init; } = default!;

    /// <summary>
    /// Gets the async stream of elements.
    /// </summary>
    public IAsyncEnumerable<TElement> Stream { get; init; } = AsyncEnumerable.Empty<TElement>();
}

/// <summary>
/// Represents a sink (client-streaming) response pattern.
/// </summary>
/// <typeparam name="TRequest">The type of request elements from client.</typeparam>
/// <typeparam name="TResponse">The type of the final response.</typeparam>
public sealed class SinkClient<TRequest, TResponse>
{
    /// <summary>
    /// Gets the channel to send requests to.
    /// </summary>
    public IAsyncEnumerable<TRequest> Requests { get; init; } = AsyncEnumerable.Empty<TRequest>();

    /// <summary>
    /// Gets the task that completes when the call finishes with a response.
    /// </summary>
    public Task<TResponse> ResponseTask { get; init; } = Task.FromResult<TResponse>(default!)!;
}

/// <summary>
/// Represents a bidirectional streaming pattern.
/// </summary>
/// <typeparam name="TRequest">The type of request elements.</typeparam>
/// <typeparam name="TResponse">The type of response elements.</typeparam>
public sealed class BiDiStream<TRequest, TResponse>
{
    /// <summary>
    /// Gets the request stream from client.
    /// </summary>
    public IAsyncEnumerable<TRequest> Requests { get; init; } = AsyncEnumerable.Empty<TRequest>();

    /// <summary>
    /// Gets the response stream to client.
    /// </summary>
    public IAsyncEnumerable<TResponse> Responses { get; init; } = AsyncEnumerable.Empty<TResponse>();
}
