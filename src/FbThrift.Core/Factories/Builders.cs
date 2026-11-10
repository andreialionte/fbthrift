// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Core.Factories;

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FbThrift.Core.Abstractions;
using FbThrift.Core.Exceptions;
using FbThrift.Core.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Builder for creating Thrift clients with fluent configuration.
/// </summary>
public sealed class ThriftClientBuilder : IThriftClientBuilder
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ThriftClientBuilder> _logger;
    private ITransport? _transport;
    private ITransportFactory? _transportFactory;
    private IProtocol? _protocol;
    private IProtocolFactory? _protocolFactory;
    private ISerializer? _serializer;
    private ISerializerFactory? _serializerFactory;
    private Action<RpcOptions>? _optionsConfigure;
    private Uri? _endpoint;

    public ThriftClientBuilder(
        IServiceProvider serviceProvider,
        ILogger<ThriftClientBuilder> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IThriftClientBuilder WithTransport(ITransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        return this;
    }

    public IThriftClientBuilder WithTransportFactory(ITransportFactory factory)
    {
        _transportFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    public IThriftClientBuilder WithProtocol(IProtocol protocol)
    {
        _protocol = protocol ?? throw new ArgumentNullException(nameof(protocol));
        return this;
    }

    public IThriftClientBuilder WithProtocolFactory(IProtocolFactory factory)
    {
        _protocolFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    public IThriftClientBuilder WithSerializer(ISerializer serializer)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        return this;
    }

    public IThriftClientBuilder WithSerializerFactory(ISerializerFactory factory)
    {
        _serializerFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    public IThriftClientBuilder WithOptions(Action<RpcOptions> configure)
    {
        _optionsConfigure = configure ?? throw new ArgumentNullException(nameof(configure));
        return this;
    }

    public IThriftClientBuilder WithEndpoint(Uri endpoint)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        return this;
    }

    public IThriftClient Build()
    {
        // Resolve transport
        IClientTransport transport;
        if (_transport != null)
        {
            transport = _transport as IClientTransport ?? throw new InvalidOperationException("Provided transport is not a client transport");
        }
        else if (_transportFactory != null)
        {
            if (_endpoint == null) throw new InvalidOperationException("Endpoint must be set when using transport factory");
            transport = _transportFactory.CreateClientTransport(_endpoint, new TransportOptions());
        }
        else
        {
            var factory = _serviceProvider.GetRequiredService<ITransportFactory>();
            if (_endpoint == null) throw new InvalidOperationException("Endpoint must be set");
            transport = factory.CreateClientTransport(_endpoint, new TransportOptions());
        }

        // Resolve protocol
        IProtocol protocol;
        if (_protocol != null)
        {
            protocol = _protocol;
        }
        else if (_protocolFactory != null)
        {
            protocol = _protocolFactory.Create(ProtocolId.Compact);
        }
        else
        {
            var factory = _serviceProvider.GetRequiredService<IProtocolFactory>();
            protocol = factory.Create(ProtocolId.Compact);
        }

        // Resolve serializer
        ISerializer serializer;
        if (_serializer != null)
        {
            serializer = _serializer;
        }
        else if (_serializerFactory != null)
        {
            serializer = _serializerFactory.Create(SerializationFormat.Compact);
        }
        else
        {
            var factory = _serviceProvider.GetRequiredService<ISerializerFactory>();
            serializer = factory.Create(SerializationFormat.Compact);
        }

        var client = new ThriftClient(transport, protocol, serializer, _serviceProvider, _logger);
        
        if (_endpoint != null)
        {
            _ = transport.ConnectAsync(CancellationToken.None);
        }

        return client;
    }
}

/// <summary>
/// Builder for creating Thrift servers with fluent configuration.
/// </summary>
public sealed class ThriftServerBuilder : IThriftServerBuilder
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ThriftServerBuilder> _logger;
    private IServerTransport? _transport;
    private ITransportFactory? _transportFactory;
    private IProtocolFactory? _protocolFactory;
    private Action<ServerOptions>? _optionsConfigure;
    private Uri? _endpoint;
    private readonly Dictionary<Type, object> _services = new();

    public ThriftServerBuilder(
        IServiceProvider serviceProvider,
        ILogger<ThriftServerBuilder> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IThriftServerBuilder WithTransport(IServerTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        return this;
    }

    public IThriftServerBuilder WithTransportFactory(ITransportFactory factory)
    {
        _transportFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    public IThriftServerBuilder WithProtocolFactory(IProtocolFactory factory)
    {
        _protocolFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    public IThriftServerBuilder WithOptions(Action<ServerOptions> configure)
    {
        _optionsConfigure = configure ?? throw new ArgumentNullException(nameof(configure));
        return this;
    }

    public IThriftServerBuilder WithEndpoint(Uri endpoint)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        return this;
    }

    public IThriftServerBuilder WithService<TService>(TService handler) where TService : class
    {
        _services[typeof(TService)] = handler ?? throw new ArgumentNullException(nameof(handler));
        return this;
    }

public IThriftServer Build()
        {
            // Resolve transport
            IServerTransport transport;
            if (_transport != null)
            {
                transport = _transport;
            }
            else if (_transportFactory != null)
            {
                if (_endpoint == null) throw new InvalidOperationException("Endpoint must be set when using transport factory");
                transport = _transportFactory.CreateServerTransport(_endpoint, new TransportOptions());
            }
            else
            {
                var factory = _serviceProvider.GetRequiredService<ITransportFactory>();
                if (_endpoint == null) throw new InvalidOperationException("Endpoint must be set");
                transport = factory.CreateServerTransport(_endpoint, new TransportOptions());
            }

            // Resolve protocol factory
            IProtocolFactory protocolFactory;
            if (_protocolFactory != null)
            {
                protocolFactory = _protocolFactory;
            }
            else
            {
                protocolFactory = _serviceProvider.GetRequiredService<IProtocolFactory>();
            }

            // Apply options
            var options = new ServerOptions();
            _optionsConfigure?.Invoke(options);

            if (_endpoint != null)
            {
                options.Endpoint = _endpoint;
            }

            // Create a simple composite service
            var serverOptions = new ThriftServerOptions
            {
                Port = options.Endpoint?.Port ?? 9090,
                Host = options.Endpoint?.Host ?? "0.0.0.0"
            };
            
            // Get first registered service
            IThriftService service = null;
            if (_services.Count > 0)
            {
                var firstService = _services.Values.FirstOrDefault();
                if (firstService is IThriftService thriftService)
                {
                    service = thriftService;
                }
            }
            
            if (service == null)
            {
                // Create a dummy service if none registered
                service = new DummyThriftService();
            }
            
            var server = new ThriftServer(serverOptions, service);
            return new ThriftServerAdapter(server);
        }
        
private sealed class DummyThriftService : IThriftService
        {
            public string ServiceName => "Dummy";
            public ValueTask<ReadOnlyMemory<byte>> ProcessAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken = default)
                => new ValueTask<ReadOnlyMemory<byte>>(ReadOnlyMemory<byte>.Empty);
        }

        private sealed class ThriftServerAdapter : IThriftServer
        {
            private readonly ThriftServer _server;
            private readonly Dictionary<Type, object> _services = new();

            public ThriftServerAdapter(ThriftServer server)
            {
                _server = server;
            }

            public ValueTask StartAsync(CancellationToken cancellationToken = default)
            {
                return ValueTask.CompletedTask;
            }

            public ValueTask StopAsync(CancellationToken cancellationToken = default)
            {
                return _server.DisposeAsync();
            }

            public void RegisterService<TService>(TService handler) where TService : class
            {
                _services[typeof(TService)] = handler;
            }

            public bool UnregisterService<TService>() where TService : class
            {
                return _services.Remove(typeof(TService));
            }

            public ValueTask DisposeAsync() => _server.DisposeAsync();
        }
    }

/// <summary>
/// Default implementation of IThriftClient.
/// </summary>
public sealed class ThriftClient : IThriftClient
{
    private readonly IClientTransport _transport;
    private readonly IProtocol _protocol;
    private readonly ISerializer _serializer;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger _logger;
    private bool _disposed;

    public ThriftClient(
        IClientTransport transport,
        IProtocol protocol,
        ISerializer serializer,
        IServiceProvider serviceProvider,
        ILogger logger)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _protocol = protocol ?? throw new ArgumentNullException(nameof(protocol));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask<TResponse> CallAsync<TRequest, TResponse>(
        string methodName,
        TRequest request,
        RpcOptions? options = null,
        CancellationToken cancellationToken = default)
        where TRequest : class, IThriftSerializable
        where TResponse : class, IThriftSerializable, new()
    {
        using var requestStream = new System.IO.MemoryStream();
        await _protocol.WriteAsync(requestStream, request, cancellationToken);
        
        var requestData = requestStream.ToArray();
        
        // Write message header
        await _protocol.WriteMessageHeaderAsync(requestStream, methodName, MessageType.Call, 0, cancellationToken);
        await _transport.WriteFrameAsync(requestData, cancellationToken);
        
        // Read response
        var buffer = new byte[8192];
        var bytesRead = await _transport.ReadFrameAsync(buffer, cancellationToken);
        if (bytesRead == 0)
        {
            throw new RpcException("Connection closed by server");
        }

        using var responseStream = new System.IO.MemoryStream(buffer, 0, bytesRead);
        var (responseMethod, messageType, seqId) = await _protocol.ReadMessageHeaderAsync(responseStream, cancellationToken);
        
        if (messageType == MessageType.Exception)
        {
            // Read exception
            throw new RpcException("Server returned exception");
        }

        var response = await _protocol.ReadAsync<TResponse>(responseStream, cancellationToken);
        return response;
    }

    public async ValueTask CallOneWayAsync<TRequest>(
        string methodName,
        TRequest request,
        RpcOptions? options = null,
        CancellationToken cancellationToken = default)
        where TRequest : class, IThriftSerializable
    {
        using var stream = new System.IO.MemoryStream();
        await _protocol.WriteAsync(stream, request, cancellationToken);
        await _protocol.WriteMessageHeaderAsync(stream, methodName, MessageType.Oneway, 0, cancellationToken);
        var requestData = stream.ToArray();
        await _transport.WriteFrameAsync(requestData, cancellationToken);
    }

    public async ValueTask<TResponse> CallStreamAsync<TRequest, TResponse>(
        string methodName,
        IAsyncEnumerable<TRequest> requests,
        RpcOptions? options = null,
        CancellationToken cancellationToken = default)
        where TRequest : class, IThriftSerializable
        where TResponse : class, IThriftSerializable, new()
    {
        // Simplified implementation - would need proper streaming protocol
        throw new NotImplementedException("Streaming not yet implemented");
    }

    public IAsyncEnumerable<TResponse> CallServerStreamAsync<TRequest, TResponse>(
        string methodName,
        TRequest request,
        RpcOptions? options = null,
        CancellationToken cancellationToken = default)
        where TRequest : class, IThriftSerializable
        where TResponse : class, IThriftSerializable, new()
    {
        throw new NotImplementedException("Server streaming not yet implemented");
    }

    public IAsyncEnumerable<TResponse> CallBidiStreamAsync<TRequest, TResponse>(
        string methodName,
        IAsyncEnumerable<TRequest> requests,
        RpcOptions? options = null,
        CancellationToken cancellationToken = default)
        where TRequest : class, IThriftSerializable
        where TResponse : class, IThriftSerializable, new()
    {
        throw new NotImplementedException("Bidirectional streaming not yet implemented");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        
        try
        {
            await _transport.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disposing transport");
        }
        
        try
        {
            await _protocol.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disposing protocol");
        }
        
        try
        {
            await _serializer.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disposing serializer");
        }
    }
}