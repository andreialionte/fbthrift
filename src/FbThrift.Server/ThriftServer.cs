// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Server;

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FbThrift.Core.Abstractions;
using FBThrift;
using FbThrift.Core.Server;

/// <summary>
/// Options for configuring a Thrift server.
/// </summary>
public sealed class ThriftServerOptions
{
    /// <summary>
    /// Gets or sets the endpoint to listen on.
    /// </summary>
    public Uri Endpoint { get; set; } = new Uri("tcp://localhost:9090");

    /// <summary>
    /// Gets or sets the maximum number of concurrent connections.
    /// </summary>
    public int MaxConnections { get; set; } = 10000;

    /// <summary>
    /// Gets or sets the maximum number of concurrent streams per connection.
    /// </summary>
    public int MaxConcurrentStreams { get; set; } = 100;

    /// <summary>
    /// Gets or sets the request timeout.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets whether to use TLS/SSL.
    /// </summary>
    public bool UseTls { get; set; } = false;

    /// <summary>
    /// Gets or sets the server certificate for TLS.
    /// </summary>
    public System.Security.Cryptography.X509Certificates.X509Certificate2? ServerCertificate { get; set; }

    /// <summary>
    /// Gets or sets whether to require client certificates.
    /// </summary>
    public bool RequireClientCertificate { get; set; } = false;

    /// <summary>
    /// Gets or sets custom server properties.
    /// </summary>
    public Dictionary<string, object> Properties { get; set; } = new();
}

/// <summary>
/// Registry for service handlers. Maps service names to their Thrift processors.
/// </summary>
public sealed class ServiceRegistry : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ThriftProcessorBase> _services = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>
    /// Registers a service handler with its processor.
    /// </summary>
    public void RegisterService(string serviceName, ThriftProcessorBase processor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _services[serviceName] = processor ?? throw new ArgumentNullException(nameof(processor));
    }

    /// <summary>
    /// Unregisters a service handler.
    /// </summary>
    public bool UnregisterService(string serviceName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _services.TryRemove(serviceName, out _);
    }

    /// <summary>
    /// Gets the processor for a service, or null if not found.
    /// </summary>
    public ThriftProcessorBase? GetProcessor(string serviceName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _services.TryGetValue(serviceName, out var processor) ? processor : null;
    }

    /// <summary>
    /// Gets all registered service names.
    /// </summary>
    public IReadOnlyList<string> GetServiceNames()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _services.Keys.ToList().AsReadOnly();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var kvp in _services)
        {
            if (kvp.Value is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
        }
        _services.Clear();
    }
}

/// <summary>
/// A high-performance Thrift server that implements <see cref="IThriftServer"/>.
/// Uses System.Threading.Channels for connection management, System.IO.Pipelines
/// for efficient I/O, and System.Runtime.CompilerServices.Unsafe for high-performance memory operations.
/// </summary>
public sealed class ThriftServer : IThriftServer
{
    private readonly IServerTransport _transport;
    private readonly IProtocolFactory _protocolFactory;
    private readonly ServiceRegistry _serviceRegistry;
    private readonly ThriftServerOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly Channel<ConnectionContext> _connectionChannel;
    private readonly ConcurrentDictionary<long, ConnectionContext> _connections = new();
    private long _connectionCounter;
    private bool _disposed;

    public ThriftServer(
        IServerTransport transport,
        IProtocolFactory protocolFactory,
        ServiceRegistry serviceRegistry,
        ThriftServerOptions? options = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _protocolFactory = protocolFactory ?? throw new ArgumentNullException(nameof(protocolFactory));
        _serviceRegistry = serviceRegistry ?? throw new ArgumentNullException(nameof(serviceRegistry));
        _options = options ?? new ThriftServerOptions();

        _connectionChannel = Channel.CreateBounded<ConnectionContext>(
            new BoundedChannelOptions(_options.MaxConnections)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = true
            });

        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        await _transport.ListenAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();

        foreach (var kvp in _connections)
        {
            try
            {
                await kvp.Value.Transport.CloseAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Ignore close errors during shutdown
            }
        }

        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch
        {
            // Ignore
        }

        await _transport.CloseAsync(cancellationToken).ConfigureAwait(false);
    }

    public void RegisterService<TService>(TService handler) where TService : class
    {
        throw new NotSupportedException("Use ServiceRegistry.RegisterService() instead.");
    }

    public bool UnregisterService<TService>() where TService : class
    {
        throw new NotSupportedException("Use ServiceRegistry.UnregisterService() instead.");
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var clientTransport = await _transport.AcceptAsync(_cts.Token).ConfigureAwait(false);

                if (clientTransport == null)
                {
                    continue;
                }

                if (_connections.Count >= _options.MaxConnections)
                {
                    try
                    {
                        await clientTransport.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Ignore
                    }
                    continue;
                }

                var connectionId = Interlocked.Increment(ref _connectionCounter);
                var connection = new ConnectionContext(
                    connectionId,
                    clientTransport,
                    _protocolFactory.Create(ProtocolId.Binary),
                    _cts.Token);

                _connections[connectionId] = connection;

                _ = Task.Run(() => ProcessConnectionAsync(connection), _cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
        catch (Exception ex)
        {
            foreach (var kvp in _connections)
            {
                kvp.Value.Tcs.TrySetException(ex);
            }
        }
    }

    private async Task ProcessConnectionAsync(ConnectionContext connection)
    {
        try
        {
            await _connectionChannel.Writer.WriteAsync(connection, _cts.Token).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            // Server is shutting down
        }

        try
        {
            while (!_cts.Token.IsCancellationRequested && connection.Transport.IsOpen)
            {
                var protocol = _protocolFactory.Create(ProtocolId.Binary);

                try
                {
                    var processor = _serviceRegistry.GetProcessor("default");
                    if (processor == null)
                    {
                        break;
                    }

                    var context = new ProcessContext
                    {
                        ConnectionContext = new TConnectionContext { ConnectionId = connection.Id },
                        Metadata = new Dictionary<string, object>()
                    };

                    var traceContext = ExtractTraceContext(context);
                    using var activity = traceContext != null
                        ? new Activity("thrift.request")
                            .SetTraceId(ActivityTraceId.CreateFromString(traceContext.TraceId))
                            .SetSpanId(ActivitySpanId.CreateFromString(traceContext.SpanId))
                            .SetParentId(traceContext.ParentSpanId)
                            .Start()
                        : null;

                    bool continueProcessing = await processor.ProcessAsync(
                        protocol,
                        protocol,
                        context,
                        _cts.Token).ConfigureAwait(false);

                    if (!continueProcessing)
                    {
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    break;
                }
                finally
                {
                    protocol.Dispose();
                }
            }
        }
        finally
        {
            _connections.TryRemove(connection.Id, out _);
            await connection.Transport.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private sealed class ConnectionContext
    {
        public ConnectionContext(
            long id,
            IClientTransport transport,
            IProtocol protocol,
            CancellationToken cancellationToken)
        {
            Id = id;
            Transport = transport;
            Protocol = protocol;
            CancellationToken = cancellationToken;
            Tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public long Id { get; }
        public IClientTransport Transport { get; }
        public IProtocol Protocol { get; }
        public CancellationToken CancellationToken { get; }
        public TaskCompletionSource<object?> Tcs { get; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();

        foreach (var kvp in _connections)
        {
            try
            {
                await kvp.Value.Transport.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Ignore
            }
        }

        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch
        {
            // Ignore
        }

        await _transport.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        _cts.Dispose();
    }
}

/// <summary>
/// Fluent builder for creating <see cref="ThriftServer"/> instances.
/// </summary>
public sealed class ThriftServerBuilder : IThriftServerBuilder
{
    private IServerTransport? _transport;
    private IProtocolFactory? _protocolFactory;
    private readonly ServiceRegistry _serviceRegistry = new(null!);
    private readonly ThriftServerOptions _options = new();
    private Uri? _endpoint;

    public IThriftServerBuilder WithTransport(IServerTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        return this;
    }

    public IThriftServerBuilder WithTransportFactory(ITransportFactory factory)
    {
        throw new NotSupportedException("Transport factory not yet supported in ThriftServerBuilder.");
    }

    public IThriftServerBuilder WithProtocolFactory(IProtocolFactory factory)
    {
        _protocolFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    public IThriftServerBuilder WithOptions(Action<ServerOptions> configure)
    {
        configure?.Invoke(_options);
        return this;
    }

    public IThriftServerBuilder WithEndpoint(Uri endpoint)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        return this;
    }

    public IThriftServerBuilder WithService<TService>(TService handler) where TService : class
    {
        throw new NotSupportedException("Use ServiceRegistry.RegisterService() instead.");
    }

    public IThriftServer Build()
    {
        if (_transport == null)
            throw new InvalidOperationException("Transport must be configured.");
        if (_protocolFactory == null)
            throw new InvalidOperationException("Protocol factory must be configured.");

        var serviceRegistry = new ServiceRegistry(_protocolFactory);
        return new ThriftServer(_transport, _protocolFactory, serviceRegistry, _options);
    }

    public ServiceRegistry ServiceRegistry => _serviceRegistry;
}

/// <summary>
/// Connection context for a single Thrift connection.
/// </summary>
public sealed class TConnectionContext
{
    public long ConnectionId { get; init; }
    public Dictionary<string, object> Properties { get; init; } = new();
}

/// <summary>
/// Factory for creating <see cref="ThriftServer"/> instances with dependency injection support.
/// </summary>
public sealed class ThriftServerFactory : IAsyncDisposable
{
    private readonly List<ThriftServer> _servers = new();
    private readonly object _lock = new();

    public ThriftServer CreateServer(
        IServerTransport transport,
        IProtocolFactory protocolFactory,
        ServiceRegistry serviceRegistry,
        ThriftServerOptions? options = null)
    {
        var server = new ThriftServer(transport, protocolFactory, serviceRegistry, options);

        lock (_lock)
        {
            _servers.Add(server);
        }

        return server;
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            foreach (var server in _servers)
            {
                _ = server.DisposeAsync();
            }
            _servers.Clear();
        }
    }

    private TraceContext? ExtractTraceContext(ProcessContext context)
    {
        if (context.Metadata.TryGetValue("traceparent", out var traceparentObj) &&
            traceparentObj is string traceparent &&
            TryParseTraceparent(traceparent, out var traceId, out var spanId, out var parentSpanId))
        {
            return new TraceContext(traceId, spanId, parentSpanId);
        }
        return null;
    }

    private static bool TryParseTraceparent(string traceparent, out string traceId, out string spanId, out string? parentSpanId)
    {
        traceId = string.Empty;
        spanId = string.Empty;
        parentSpanId = null;

        var parts = traceparent.Split('-');
        if (parts.Length >= 3 && parts[0] == "00")
        {
            traceId = parts[1];
            spanId = parts[2];
            parentSpanId = parts[2];
            return true;
        }
        return false;
    }

    private sealed record TraceContext(string TraceId, string SpanId, string? ParentSpanId);
}
