namespace FbThrift.Core.Server;

using FBThrift.Core.Interceptors;

/// <summary>
/// Enhanced processor base with interceptor support and better dispatch semantics.
/// </summary>
public class ThriftProcessor : ThriftProcessorBase
{
    private readonly List<IServiceInterceptor> _interceptors = new();

    /// <summary>
    /// Adds an interceptor.
    /// </summary>
    public void AddInterceptor(IServiceInterceptor interceptor)
    {
        ArgumentNullException.ThrowIfNull(interceptor);
        _interceptors.Add(interceptor);
    }
}
