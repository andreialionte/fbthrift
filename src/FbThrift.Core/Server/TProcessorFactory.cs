namespace FbThrift.Core.Server;

/// <summary>
/// Context information for a Thrift connection.
/// </summary>
public sealed class TConnectionContext
{
    private readonly Dictionary<string, object> _properties = new();

    public string? PeerAddress { get; init; }
    public string? LocalAddress { get; init; }
    public int PeerPort { get; init; }
    public int LocalPort { get; init; }
    public DateTime ConnectedAt { get; init; } = DateTime.UtcNow;
    public Dictionary<string, object> Properties => _properties;

    public T GetProperty<T>(string key)
    {
        return _properties.TryGetValue(key, out var value) && value is T typed ? typed : default!;
    }

    public void SetProperty(string key, object value)
    {
        _properties[key] = value;
    }
}

/// <summary>
/// Base event handler for processor events.
/// Matches fbthrift's TProcessorEventHandler.
/// </summary>
public abstract class TProcessorEventHandler
{
    public virtual void GetContext(TConnectionContext context, IDictionary<string, object>? metadata)
    {
    }

    public virtual void ProcessContext(TConnectionContext context, IDictionary<string, object>? metadata)
    {
    }

    public virtual void RequestContext(TConnectionContext context, IDictionary<string, object>? metadata)
    {
    }

    public virtual void PostWrite(TConnectionContext context, IDictionary<string, object>? metadata)
    {
    }

    public virtual void RequestRead(TConnectionContext context, IDictionary<string, object>? metadata)
    {
    }

    public virtual void RequestWrite(TConnectionContext context, IDictionary<string, object>? metadata)
    {
    }

    public virtual void ResponseRead(TConnectionContext context, IDictionary<string, object>? metadata)
    {
    }

    public virtual void ResponseWrite(TConnectionContext context, IDictionary<string, object>? metadata)
    {
    }

    public virtual void Error(TConnectionContext context, Exception ex)
    {
    }

    public virtual void Close(TConnectionContext context)
    {
    }
}

/// <summary>
/// Factory for creating TProcessor instances per connection.
/// Matches fbthrift's TProcessorFactory.
/// </summary>
public interface IThriftProcessorFactory
{
    /// <summary>
    /// Gets the processor for a specific connection context.
    /// </summary>
    IThriftProcessor GetProcessor(TConnectionContext context);
}

/// <summary>
/// Singleton processor factory that returns the same processor for all connections.
/// Matches fbthrift's TSingletonProcessorFactory.
/// </summary>
public sealed class TSingletonProcessorFactory : IThriftProcessorFactory
{
    private readonly IThriftProcessor _processor;

    public TSingletonProcessorFactory(IThriftProcessor processor)
    {
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
    }

    public IThriftProcessor GetProcessor(TConnectionContext context) => _processor;

    public IThriftProcessor Processor => _processor;
}

/// <summary>
/// Helper for managing handler lifetime with factory pattern.
/// Matches fbthrift's ReleaseHandler.
/// </summary>
public sealed class ReleaseHandler<THandlerFactory> where THandlerFactory : IHandlerFactory
{
    private readonly THandlerFactory _factory;

    public ReleaseHandler(THandlerFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public void Release(object handler)
    {
        if (handler != null)
        {
            _factory.ReleaseHandler(handler);
        }
    }
}

/// <summary>
/// Interface for handler factories that create service handlers.
/// </summary>
public interface IHandlerFactory
{
    object CreateHandler();
    void ReleaseHandler(object handler);
}