namespace FBThrift.Core.Interceptors;

/// <summary>
/// Context for RPC calls passed to interceptors.
/// </summary>
public class RpcContext
{
    /// <summary>
    /// Gets or sets the method name.
    /// </summary>
    public string MethodName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the service name.
    /// </summary>
    public string? ServiceName { get; set; }

    /// <summary>
    /// Gets or sets the headers/metadata.
    /// </summary>
    public IDictionary<string, string> Headers { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets additional state.
    /// </summary>
    public IDictionary<string, object> State { get; set; } = new Dictionary<string, object>();
}

/// <summary>
/// Interceptor for Thrift RPC calls.
/// </summary>
public interface IServiceInterceptor
{
    /// <summary>
    /// Called before processing a request.
    /// </summary>
    ValueTask OnRequestAsync(RpcContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Called after processing a request successfully.
    /// </summary>
    ValueTask OnResponseAsync(RpcContext context, object? response, CancellationToken cancellationToken = default);

    /// <summary>
    /// Called when processing a request fails.
    /// </summary>
    ValueTask OnErrorAsync(RpcContext context, Exception exception, CancellationToken cancellationToken = default);

    /// <summary>
    /// Called when the server starts serving.
    /// </summary>
    ValueTask OnStartServingAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Base implementation of service interceptor.
/// </summary>
public abstract class ServiceInterceptorBase : IServiceInterceptor
{
    /// <inheritdoc/>
    public virtual ValueTask OnRequestAsync(RpcContext context, CancellationToken cancellationToken = default)
        => default;

    /// <inheritdoc/>
    public virtual ValueTask OnResponseAsync(RpcContext context, object? response, CancellationToken cancellationToken = default)
        => default;

    /// <inheritdoc/>
    public virtual ValueTask OnErrorAsync(RpcContext context, Exception exception, CancellationToken cancellationToken = default)
        => default;

    /// <inheritdoc/>
    public virtual ValueTask OnStartServingAsync(CancellationToken cancellationToken = default)
        => default;
}
