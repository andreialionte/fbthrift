// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Client;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FbThrift.Core.Abstractions;
using FbThrift.Core.FBThrift;

/// <summary>
/// Options for configuring a Thrift client.
/// </summary>
public sealed class ThriftClientOptions
{
    /// <summary>
    /// Gets or sets the default RPC options for all calls.
    /// </summary>
    public RpcOptions DefaultRpcOptions { get; set; } = new();

    /// <summary>
    /// Gets or sets whether to enable connection pooling.
    /// </summary>
    public bool EnableConnectionPooling { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum number of pooled connections per endpoint.
    /// </summary>
    public int MaxPooledConnections { get; set; } = 10;

    /// <summary>
    /// Gets or sets the channel capacity for pending requests.
    /// </summary>
    public int PendingRequestChannelCapacity { get; set; } = 1000;
}

/// <summary>
/// A high-performance Thrift client that implements <see cref="IThriftClient"/>.
/// Uses System.Threading.Channels for async request queuing, System.IO.Pipelines
/// for efficient I/O, and System.Runtime.CompilerServices.Unsafe for high-performance memory operations.
/// </summary>
public sealed class ThriftClient : IThriftClient
{
    private readonly IClientTransport _transport;
    private readonly IProtocol _protocol;
    private readonly ISerializer? _serializer;
    private readonly ThriftClientOptions _options;
    private readonly Channel<(string Method, object Request, TaskCompletionSource<object?> Tcs, RpcOptions Options, CancellationToken Cts)> _pendingChannel;
    private readonly Task _dispatchLoop;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;
    private int _nextSeqId;

    public ThriftClient(
        IClientTransport transport,
        IProtocol protocol,
        ThriftClientOptions? options = null,
        ISerializer? serializer = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _protocol = protocol ?? throw new ArgumentNullException(nameof(protocol));
        _serializer = serializer;
        _options = options ?? new ThriftClientOptions();

        _pendingChannel = Channel.CreateBounded<(string, object, TaskCompletionSource<object?>, RpcOptions, CancellationToken)>(
            new BoundedChannelOptions(_options.PendingRequestChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = true
            });

        _dispatchLoop = Task.Run(DispatchLoopAsync);
    }

    public async ValueTask<TResponse> CallAsync<TRequest, TResponse>(
        string methodName,
        TRequest request,
        RpcOptions? options = null,
        CancellationToken cancellationToken = default)
        where TRequest : class, IThriftSerializable
        where TResponse : class, IThriftSerializable, new()
    {
        if (string.IsNullOrWhiteSpace(methodName))
            throw new ArgumentException("Method name must not be empty or whitespace.", nameof(methodName));

        var rpcOptions = options ?? _options.DefaultRpcOptions;

        // Inject W3C TraceContext for distributed tracing
        InjectTraceContext(rpcOptions);

        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);

        if (rpcOptions.Timeout > TimeSpan.Zero)
        {
            linkedCts.CancelAfter(rpcOptions.Timeout);
        }

        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

        await _pendingChannel.Writer.WriteAsync((methodName, request, tcs, rpcOptions, linkedCts.Token), linkedCts.Token).ConfigureAwait(false);

        try
        {
            var result = await tcs.Task.WaitAsync(linkedCts.Token).ConfigureAwait(false);

            return Unsafe.As<TResponse>(result!);
        }
        finally
        {
            linkedCts.Dispose();
        }
    }

    public async ValueTask CallOneWayAsync<TRequest>(
        string methodName,
        TRequest request,
        RpcOptions? options = null,
        CancellationToken cancellationToken = default)
        where TRequest : class, IThriftSerializable
    {
        if (string.IsNullOrWhiteSpace(methodName))
            throw new ArgumentException("Method name must not be empty or whitespace.", nameof(methodName));

        var rpcOptions = options ?? _options.DefaultRpcOptions;

        // Inject W3C TraceContext for distributed tracing
        InjectTraceContext(rpcOptions);

        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);

        if (rpcOptions.Timeout > TimeSpan.Zero)
        {
            linkedCts.CancelAfter(rpcOptions.Timeout);
        }

        await _pendingChannel.Writer.WriteAsync((methodName, request, null!, rpcOptions, linkedCts.Token), linkedCts.Token).ConfigureAwait(false);

        linkedCts.Dispose();
    }

    public async ValueTask<TResponse> CallStreamAsync<TRequest, TResponse>(
        string methodName,
        IAsyncEnumerable<TRequest> requests,
        RpcOptions? options = null,
        CancellationToken cancellationToken = default)
        where TRequest : class, IThriftSerializable
        where TResponse : class, IThriftSerializable, new()
    {
        throw new NotImplementedException("Client streaming is not yet implemented.");
    }

    public IAsyncEnumerable<TResponse> CallServerStreamAsync<TRequest, TResponse>(
        string methodName,
        TRequest request,
        RpcOptions? options = null,
        CancellationToken cancellationToken = default)
        where TRequest : class, IThriftSerializable
        where TResponse : class, IThriftSerializable, new()
    {
        throw new NotImplementedException("Server streaming is not yet implemented.");
    }

    public IAsyncEnumerable<TResponse> CallBidiStreamAsync<TRequest, TResponse>(
        string methodName,
        IAsyncEnumerable<TRequest> requests,
        RpcOptions? options = null,
        CancellationToken cancellationToken = default)
        where TRequest : class, IThriftSerializable
        where TResponse : class, IThriftSerializable, new()
    {
        throw new NotImplementedException("Bidi streaming is not yet implemented.");
    }

    private async Task DispatchLoopAsync()
    {
        try
        {
            await foreach (var item in _pendingChannel.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                var (methodName, request, tcs, rpcOptions, ct) = item;

                try
                {
                    if (ct.IsCancellationRequested)
                    {
                        tcs?.TrySetCanceled();
                        continue;
                    }

                    await ProcessRequestAsync(methodName, request, tcs, rpcOptions, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    tcs?.TrySetException(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on disposal
        }
        catch (Exception ex)
        {
            while (_pendingChannel.Reader.TryRead(out var pending))
            {
                pending.Tcs.TrySetException(ex);
            }
        }
    }

    private async Task ProcessRequestAsync<TRequest>(
        string methodName,
        TRequest request,
        TaskCompletionSource<object?> tcs,
        RpcOptions rpcOptions,
        CancellationToken ct)
        where TRequest : class, IThriftSerializable
    {
        var seqId = Interlocked.Increment(ref _nextSeqId);

        byte[] body;
        if (_serializer != null)
        {
            body = _serializer.Serialize(request);
        }
        else
        {
            using var ms = new MemoryStream();
            await _protocol.WriteAsync(ms, request, ct).ConfigureAwait(false);
            body = ms.ToArray();
        }

        await _transport.WriteFrameAsync(body, ct).ConfigureAwait(false);

        var responseBuffer = ArrayPool<byte>.Shared.Rent(rpcOptions.MaxFrameSize > 0 ? rpcOptions.MaxFrameSize : 65536);
        try
        {
            var bytesRead = await _transport.ReadFrameAsync(responseBuffer, ct).ConfigureAwait(false);

            if (bytesRead == 0)
            {
                throw new FbThriftException("Connection closed by server.");
            }

            var responseData = responseBuffer.AsSpan(0, bytesRead);

            TResponse response;
            if (_serializer != null)
            {
                using var ms = new MemoryStream(responseData.ToArray());
                response = _serializer.Deserialize<TResponse>(responseData.ToArray());
            }
            else
            {
                using var ms = new MemoryStream(responseData.ToArray());
                response = await _protocol.ReadAsync<TResponse>(ms, ct).ConfigureAwait(false);
            }

            tcs.TrySetResult(response);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(responseBuffer);
        }
    }

    private void InjectTraceContext(RpcOptions rpcOptions)
    {
        var activity = Activity.Current;
        if (activity == null || !activity.TraceId.IsValid()) return;

        var traceId = activity.TraceId.ToHexString();
        var spanId = activity.SpanId.ToHexString();
        var traceFlags = activity.TraceFlags.ToString("x2");

        var traceParent = $"00-{traceId}-{spanId}-{traceFlags}";
        rpcOptions.Headers["traceparent"] = traceParent;

        if (activity.TraceStateString != null && activity.TraceStateString.Length > 0)
        {
            rpcOptions.Headers["tracestate"] = activity.TraceStateString;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();

        try
        {
            _pendingChannel.Writer.Complete();
            await _dispatchLoop.ConfigureAwait(false);
        }
        catch
        {
            // Ignore disposal errors
        }

        await _transport.DisposeAsync().ConfigureAwait(false);
        _protocol.Dispose();
        _cts.Dispose();
    }
}

/// <summary>
/// Fluent builder for creating <see cref="ThriftClient"/> instances.
/// </summary>
public sealed class ThriftClientBuilder : IThriftClientBuilder
{
    private IClientTransport? _transport;
    private IProtocol? _protocol;
    private ISerializer? _serializer;
    private readonly ThriftClientOptions _options = new();
    private Uri? _endpoint;

    public IThriftClientBuilder WithTransport(ITransport transport)
    {
        _transport = transport as IClientTransport
            ?? throw new ArgumentException("Transport must be a client transport.", nameof(transport));
        return this;
    }

    public IThriftClientBuilder WithTransportFactory(ITransportFactory factory)
    {
        throw new NotSupportedException("Transport factory not yet supported in ThriftClientBuilder.");
    }

    public IThriftClientBuilder WithProtocol(IProtocol protocol)
    {
        _protocol = protocol ?? throw new ArgumentNullException(nameof(protocol));
        return this;
    }

    public IThriftClientBuilder WithProtocolFactory(IProtocolFactory factory)
    {
        throw new NotSupportedException("Protocol factory not yet supported in ThriftClientBuilder.");
    }

    public IThriftClientBuilder WithSerializer(ISerializer serializer)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        return this;
    }

    public IThriftClientBuilder WithSerializerFactory(ISerializerFactory factory)
    {
        throw new NotSupportedException("Serializer factory not yet supported in ThriftClientBuilder.");
    }

    public IThriftClientBuilder WithOptions(Action<RpcOptions> configure)
    {
        configure?.Invoke(_options.DefaultRpcOptions);
        return this;
    }

    public IThriftClientBuilder WithEndpoint(Uri endpoint)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        return this;
    }

    public IThriftClient Build()
    {
        if (_transport == null)
            throw new InvalidOperationException("Transport must be configured.");
        if (_protocol == null)
            throw new InvalidOperationException("Protocol must be configured.");

        return new ThriftClient(_transport, _protocol, _options, _serializer);
    }
}

/// <summary>
/// Factory for creating <see cref="ThriftClient"/> instances with dependency injection support.
/// </summary>
public sealed class ThriftClientFactory : IAsyncDisposable
{
    private readonly Dictionary<string, ThriftClient> _clients = new();
    private readonly object _lock = new();

    public ThriftClient CreateClient(
        IClientTransport transport,
        IProtocol protocol,
        ThriftClientOptions? options = null,
        ISerializer? serializer = null,
        string? name = null)
    {
        var client = new ThriftClient(transport, protocol, options, serializer);

        if (name != null)
        {
            lock (_lock)
            {
                _clients[name] = client;
            }
        }

        return client;
    }

    public ThriftClient? GetClient(string name)
    {
        lock (_lock)
        {
            return _clients.TryGetValue(name, out var client) ? client : null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            foreach (var client in _clients.Values)
            {
                _ = client.DisposeAsync();
            }
            _clients.Clear();
        }
    }
}
