using FbThrift.Core.Abstractions;

namespace FbThrift.Core;

/// <summary>
/// Marker interface for a Thrift client that can invoke remote procedures.
/// </summary>
public interface IFbThriftClient
{
}

/// <summary>
/// A Thrift client that sends requests over an <see cref="IClientTransport"/> and deserializes replies.
/// </summary>
public sealed class FbThriftClient : IFbThriftClient, IAsyncDisposable
{
    private readonly IClientTransport _transport;

    /// <summary>
    /// Initializes a new instance of the <see cref="FbThriftClient"/> class.
    /// </summary>
    /// <param name="transport">The transport to send requests over.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="transport"/> is null.</exception>
    public FbThriftClient(IClientTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    /// <summary>
    /// Invokes a remote procedure and deserializes the reply.
    /// </summary>
    /// <typeparam name="TRequest">The type of the request message.</typeparam>
    /// <typeparam name="TResponse">The type of the response message.</typeparam>
    /// <param name="method">The name of the remote procedure to invoke.</param>
    /// <param name="request">The request message to serialize and send.</param>
    /// <param name="serialize">A function that serializes <paramref name="request"/> into Thrift body bytes.</param>
    /// <param name="deserialize">A function that deserializes Thrift reply bytes into a <typeparamref name="TResponse"/>.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation.</param>
    /// <returns>A task whose result is the deserialized response.</returns>
    /// <exception cref="FbThriftException">Thrown when the transport faults or the call fails.</exception>
    public async Task<TResponse> CallAsync<TRequest, TResponse>(
        string method,
        TRequest request,
        Func<TRequest, byte[]> serialize,
        Func<ReadOnlyMemory<byte>, TResponse> deserialize,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(method))
            throw new ArgumentException("Method name must not be empty or whitespace.", nameof(method));
        if (serialize is null)
            throw new ArgumentNullException(nameof(serialize));
        if (deserialize is null)
            throw new ArgumentNullException(nameof(deserialize));

        byte[] body = serialize(request);
        ReadOnlyMemory<byte> reply = await _transport.SendRequestAsync(body, cancellationToken).ConfigureAwait(false);
        return deserialize(reply);
    }

    /// <summary>
    /// Disposes the underlying transport.
    /// </summary>
    /// <returns>A value task representing the asynchronous dispose operation.</returns>
    public ValueTask DisposeAsync() => _transport.DisposeAsync();
}
