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
using FbThrift.Core.Protocol;
using FBThrift;
using FbThrift.Core.Server;

/// <summary>
/// Options for configuring a Thrift server.
/// </summary>
public sealed class ServerThriftServerOptions : ServerOptions
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
public sealed class ServerServiceRegistry : IAsyncDisposable
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
public sealed class ServerThriftServer : IThriftServer
{
    private readonly IServerTransport _transport;
    private readonly IProtocolFactory _protocolFactory;
    private readonly ServerServiceRegistry _serviceRegistry;
    private readonly ServerThriftServerOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly Channel<ConnectionContext> _connectionChannel;
    private readonly ConcurrentDictionary<long, ConnectionContext> _connections = new();
    private long _connectionCounter;
    private bool _disposed;

    public ServerThriftServer(
        IServerTransport transport,
        IProtocolFactory protocolFactory,
        ServerServiceRegistry serviceRegistry,
        ServerThriftServerOptions? options = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _protocolFactory = protocolFactory ?? throw new ArgumentNullException(nameof(protocolFactory));
        _serviceRegistry = serviceRegistry ?? throw new ArgumentNullException(nameof(serviceRegistry));
        _options = options ?? new ServerThriftServerOptions();

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
                var reader = _protocolFactory.Create(ProtocolId.Compact);
                var writer = _protocolFactory.Create(ProtocolId.Compact);
                var connection = new ConnectionContext(
                    connectionId,
                    clientTransport,
                    reader,
                    writer,
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
                var reader = _protocolFactory.Create(ProtocolId.Compact);
                var writer = _protocolFactory.Create(ProtocolId.Compact);

                try
                {
                    var processor = _serviceRegistry.GetProcessor("default");
                    if (processor == null)
                    {
                        break;
                    }

                    var context = new ProcessContext
                    {
                        ConnectionContext = new ServerTConnectionContext { ConnectionId = connection.Id },
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
                        reader,
                        writer,
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
                    reader.Dispose();
                    writer.Dispose();
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
            ITProtocolReader reader,
            ITProtocolWriter writer,
            CancellationToken cancellationToken)
        {
            Id = id;
            Transport = transport;
            Reader = reader;
            Writer = writer;
            CancellationToken = cancellationToken;
            Tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public long Id { get; }
        public IClientTransport Transport { get; }
        public ITProtocolReader Reader { get; }
        public ITProtocolWriter Writer { get; }
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
    
    private sealed class ProcessContext
    {
        public ServerTConnectionContext ConnectionContext { get; set; } = new();
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    private sealed class ServerTConnectionContext
    {
        public long ConnectionId { get; init; }
        public Dictionary<string, object> Properties { get; init; } = new();
    }

    private sealed record ServerTraceContext(string TraceId, string SpanId, string? ParentSpanId);

    private ServerTraceContext? ExtractTraceContext(ProcessContext context)
    {
        if (context.Metadata.TryGetValue("traceparent", out var traceparentObj) &&
            traceparentObj is string traceparent &&
            TryParseTraceparent(traceparent, out var traceId, out var spanId, out var parentSpanId))
        {
            return new ServerTraceContext(traceId, spanId, parentSpanId);
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
}

/// <summary>
/// Fluent builder for creating <see cref="ServerThriftServer"/> instances.
/// </summary>
public sealed class ServerThriftServerBuilder : IThriftServerBuilder
{
    private IServerTransport? _transport;
    private IProtocolFactory? _protocolFactory;
    private readonly ServerServiceRegistry _serviceRegistry = new();
    private readonly ServerThriftServerOptions _options = new();
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

        var serviceRegistry = new ServerServiceRegistry();
        return new ServerThriftServer(_transport, _protocolFactory, serviceRegistry, _options);
    }

    public ServerServiceRegistry ServiceRegistry => _serviceRegistry;
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
/// Factory for creating <see cref="ServerThriftServer"/> instances with dependency injection support.
/// </summary>
public sealed class ServerThriftServerFactory : IAsyncDisposable
{
    private readonly List<ServerThriftServer> _servers = new();
    private readonly object _lock = new();

    public ServerThriftServer CreateServer(
        IServerTransport transport,
        IProtocolFactory protocolFactory,
        ServerServiceRegistry serviceRegistry,
        ServerThriftServerOptions? options = null)
    {
        var server = new ServerThriftServer(transport, protocolFactory, serviceRegistry, options);

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
}
