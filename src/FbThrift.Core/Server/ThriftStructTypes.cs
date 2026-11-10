namespace FbThrift.Core.Server;

/// <summary>
/// Server-side Thrift processor: reads a request from an input protocol, dispatches it to a
/// registered handler, and writes the response (or an exception) to an output protocol.
/// </summary>
public interface IThriftProcessor : FbThrift.Core.ITService
{
    /// <summary>
    /// Processes a single request/response exchange.
    /// </summary>
    /// <param name="input">The input protocol to read the request from.</param>
    /// <param name="output">The output protocol to write the response to.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation.</param>
    /// <returns>True if a request was processed; false if no more requests are available.</returns>
    /// <exception cref="FbThrift.Core.FbThriftException">Thrown when the exchange fails.</exception>
    ValueTask<bool> ProcessAsync(
        FbThrift.Core.Protocol.ITProtocolReader input,
        FbThrift.Core.Protocol.ITProtocolWriter output,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Processes a single request/response exchange with connection context and metadata.
    /// </summary>
    ValueTask<bool> ProcessAsync(
        FbThrift.Core.Protocol.ITProtocolReader input,
        FbThrift.Core.Protocol.ITProtocolWriter output,
        ProcessContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Registry of Thrift struct types by their Thrift URI (universal name), used by generated code
/// to look up the runtime type for deserialization and <see cref="FbThrift.Core.TProtocolReader"/>
/// <c>any</c> values.
/// </summary>
public static class ThriftStructTypes
{
    private static readonly Dictionary<string, Type> _types = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers a Thrift struct type under its Thrift URI.
    /// </summary>
    /// <param name="thriftUri">The Thrift URI (universal name) of the struct.</param>
    /// <param name="type">The CLR type implementing the struct.</param>
    /// <exception cref="ArgumentNullException">Thrown when either argument is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="thriftUri"/> is already registered.</exception>
    public static void Register(string thriftUri, Type type)
    {
        ArgumentNullException.ThrowIfNull(thriftUri);
        ArgumentNullException.ThrowIfNull(type);
        if (!_types.TryAdd(thriftUri, type))
            throw new ArgumentException($"Type '{thriftUri}' is already registered.", nameof(thriftUri));
    }

    /// <summary>
    /// Looks up a registered Thrift struct type by its Thrift URI.
    /// </summary>
    /// <param name="thriftUri">The Thrift URI (universal name) of the struct.</param>
    /// <returns>The registered CLR type, or null if not found.</returns>
    public static Type? Lookup(string thriftUri)
    {
        ArgumentNullException.ThrowIfNull(thriftUri);
        _types.TryGetValue(thriftUri, out Type? type);
        return type;
    }
}
