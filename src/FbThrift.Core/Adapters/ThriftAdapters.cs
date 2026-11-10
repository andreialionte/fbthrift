// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Core.Adapters;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FbThrift.Core.Server;
using System.Reflection; // For MethodInvoker disambiguation

// Alias to avoid ambiguity with System.Reflection.MethodInvoker
using ThriftMethodInvoker = FbThrift.Core.Server.MethodInvoker;

/// <summary>
/// Base interface for all Thrift adapters.
/// Adapters provide cross-cutting functionality that can be applied to Thrift service methods.
/// </summary>
public interface IThriftAdapter
{
    /// <summary>
    /// The name of this adapter (for debugging/logging).
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Priority for adapter execution order (lower = earlier).
    /// </summary>
    int Priority { get; }
}

/// <summary>
/// Adapter that wraps a unary method call.
/// </summary>
public interface IUnaryAdapter : IThriftAdapter
{
    /// <summary>
    /// Invokes the adapter around a unary call.
    /// </summary>
    ValueTask<object?> InvokeAsync(
        string methodName,
        object? args,
        Func<object?, CancellationToken, ValueTask<object?>> next,
        CancellationToken cancellationToken);
}

/// <summary>
/// Adapter that wraps a server-streaming call.
/// </summary>
public interface IServerStreamingAdapter : IThriftAdapter
{
    /// <summary>
    /// Invokes the adapter around a server-streaming call.
    /// </summary>
    IAsyncEnumerable<object?> InvokeAsync(
        string methodName,
        object? args,
        Func<object?, CancellationToken, IAsyncEnumerable<object?>> next,
        CancellationToken cancellationToken);
}

/// <summary>
/// Adapter that wraps a client-streaming call.
/// </summary>
public interface IClientStreamingAdapter : IThriftAdapter
{
    /// <summary>
    /// Invokes the adapter around a client-streaming call.
    /// </summary>
    ValueTask<object?> InvokeAsync(
        string methodName,
        IAsyncEnumerable<object?> requestStream,
        Func<IAsyncEnumerable<object?>, CancellationToken, ValueTask<object?>> next,
        CancellationToken cancellationToken);
}

/// <summary>
/// Adapter that wraps a bidirectional streaming call.
/// </summary>
public interface IBidiStreamingAdapter : IThriftAdapter
{
    /// <summary>
    /// Invokes the adapter around a bidi streaming call.
    /// </summary>
    IAsyncEnumerable<object?> InvokeAsync(
        string methodName,
        IAsyncEnumerable<object?> requestStream,
        Func<IAsyncEnumerable<object?>, CancellationToken, IAsyncEnumerable<object?>> next,
        CancellationToken cancellationToken);
}

/// <summary>
/// Adapter registry for managing and applying adapters to service methods.
/// </summary>
public sealed class AdapterRegistry
{
    private readonly List<IUnaryAdapter> _unaryAdapters = new();
    private readonly List<IServerStreamingAdapter> _serverStreamingAdapters = new();
    private readonly List<IClientStreamingAdapter> _clientStreamingAdapters = new();
    private readonly List<IBidiStreamingAdapter> _bidiStreamingAdapters = new();

    /// <summary>
    /// Adds a unary adapter.
    /// </summary>
    public void AddUnaryAdapter(IUnaryAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        _unaryAdapters.Add(adapter);
        _unaryAdapters.Sort((a, b) => a.Priority.CompareTo(b.Priority));
    }

    /// <summary>
    /// Adds a server-streaming adapter.
    /// </summary>
    public void AddServerStreamingAdapter(IServerStreamingAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        _serverStreamingAdapters.Add(adapter);
        _serverStreamingAdapters.Sort((a, b) => a.Priority.CompareTo(b.Priority));
    }

    /// <summary>
    /// Adds a client-streaming adapter.
    /// </summary>
    public void AddClientStreamingAdapter(IClientStreamingAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        _clientStreamingAdapters.Add(adapter);
        _clientStreamingAdapters.Sort((a, b) => a.Priority.CompareTo(b.Priority));
    }

    /// <summary>
    /// Adds a bidi streaming adapter.
    /// </summary>
    public void AddBidiStreamingAdapter(IBidiStreamingAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        _bidiStreamingAdapters.Add(adapter);
        _bidiStreamingAdapters.Sort((a, b) => a.Priority.CompareTo(b.Priority));
    }

    /// <summary>
    /// Removes an adapter by name.
    /// </summary>
    public bool RemoveAdapter(string name)
    {
        bool removed = _unaryAdapters.RemoveAll(a => a.Name == name) > 0;
        removed |= _serverStreamingAdapters.RemoveAll(a => a.Name == name) > 0;
        removed |= _clientStreamingAdapters.RemoveAll(a => a.Name == name) > 0;
        removed |= _bidiStreamingAdapters.RemoveAll(a => a.Name == name) > 0;
        return removed;
    }

    /// <summary>
    /// Gets all registered adapters.
    /// </summary>
    public IReadOnlyList<IThriftAdapter> GetAllAdapters()
    {
        var all = new List<IThriftAdapter>();
        all.AddRange(_unaryAdapters);
        all.AddRange(_serverStreamingAdapters);
        all.AddRange(_clientStreamingAdapters);
        all.AddRange(_bidiStreamingAdapters);
        return all;
    }

/// <summary>
    /// Creates a method invoker that applies all registered unary adapters.
    /// </summary>
    public ThriftMethodInvoker CreateUnaryInvoker(string methodName, ThriftMethodInvoker innerInvoker)
    {
        ThriftMethodInvoker current = innerInvoker;

        // Apply adapters in reverse order (last added = outermost)
        for (int i = _unaryAdapters.Count - 1; i >= 0; i--)
        {
            var adapter = _unaryAdapters[i];
            var next = current;
            current = async (args, ct) => await adapter.InvokeAsync(methodName, args, 
                (object? args, CancellationToken ct) => next(args, ct), ct);
        }

        return current;
    }

    /// <summary>
    /// Creates a server streaming invoker that applies all registered server streaming adapters.
    /// </summary>
    public StreamingInvoker CreateServerStreamingInvoker(string methodName, StreamingInvoker innerInvoker)
    {
        StreamingInvoker current = innerInvoker;

        for (int i = _serverStreamingAdapters.Count - 1; i >= 0; i--)
        {
            var adapter = _serverStreamingAdapters[i];
            var next = current;
            current = (args, ct) => adapter.InvokeAsync(methodName, args, 
                (object? args, CancellationToken ct) => next(args, ct), ct);
        }

        return current;
    }

    /// <summary>
    /// Creates a client streaming invoker that applies all registered client streaming adapters.
    /// </summary>
    public ClientStreamingInvoker CreateClientStreamingInvoker(string methodName, ClientStreamingInvoker innerInvoker)
    {
        ClientStreamingInvoker current = innerInvoker;

        for (int i = _clientStreamingAdapters.Count - 1; i >= 0; i--)
        {
            var adapter = _clientStreamingAdapters[i];
            var next = current;
            current = (stream, ct) => adapter.InvokeAsync(methodName, stream, 
                (IAsyncEnumerable<object?> stream, CancellationToken ct) => next(stream, ct), ct);
        }

        return current;
    }

    /// <summary>
    /// Creates a bidi streaming invoker that applies all registered bidi streaming adapters.
    /// </summary>
    public BidiStreamingInvoker CreateBidiStreamingInvoker(string methodName, BidiStreamingInvoker innerInvoker)
    {
        BidiStreamingInvoker current = innerInvoker;

        for (int i = _bidiStreamingAdapters.Count - 1; i >= 0; i--)
        {
            var adapter = _bidiStreamingAdapters[i];
            var next = current;
            current = (stream, ct) => adapter.InvokeAsync(methodName, stream, 
                (IAsyncEnumerable<object?> stream, CancellationToken ct) => next(stream, ct), ct);
        }

        return current;
    }
}

/// <summary>
/// Built-in logging adapter.
/// </summary>
public sealed class LoggingAdapter : IUnaryAdapter, IServerStreamingAdapter, IClientStreamingAdapter, IBidiStreamingAdapter
{
    private readonly ILogger _logger;
    private readonly bool _logArguments;
    private readonly bool _logResults;

    public string Name => "Logging";
    public int Priority => 100;

    public LoggingAdapter(ILogger logger, bool logArguments = true, bool logResults = false)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _logArguments = logArguments;
        _logResults = logResults;
    }

    public async ValueTask<object?> InvokeAsync(
        string methodName,
        object? args,
        Func<object?, CancellationToken, ValueTask<object?>> next,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        if (_logArguments)
        {
            _logger.LogDebug("Calling {Method} with args: {Args}", methodName, args);
        }
        else
        {
            _logger.LogDebug("Calling {Method}", methodName);
        }

        try
        {
            var result = await next(args, cancellationToken).ConfigureAwait(false);

            stopwatch.Stop();

            if (_logResults)
            {
                _logger.LogDebug("Completed {Method} in {Elapsed}ms with result: {Result}", methodName, stopwatch.ElapsedMilliseconds, result);
            }
            else
            {
                _logger.LogDebug("Completed {Method} in {Elapsed}ms", methodName, stopwatch.ElapsedMilliseconds);
            }

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Failed {Method} after {Elapsed}ms", methodName, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    public IAsyncEnumerable<object?> InvokeAsync(
        string methodName,
        object? args,
        Func<object?, CancellationToken, IAsyncEnumerable<object?>> next,
        CancellationToken cancellationToken)
    {
        return InvokeServerStreamingCore(methodName, args, next, cancellationToken);
    }

    private async IAsyncEnumerable<object?> InvokeServerStreamingCore(
        string methodName,
        object? args,
        Func<object?, CancellationToken, IAsyncEnumerable<object?>> next,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var elementCount = 0;

        _logger.LogDebug("Starting server stream {Method}", methodName);

        var configuredEnumerable = next(args, cancellationToken).WithCancellation(cancellationToken);
        await foreach (var item in configuredEnumerable)
        {
            elementCount++;
            yield return item;
        }

        stopwatch.Stop();
        _logger.LogDebug("Completed server stream {Method} in {Elapsed}ms with {Count} elements", methodName, stopwatch.ElapsedMilliseconds, elementCount);
    }

    public async ValueTask<object?> InvokeAsync(
        string methodName,
        IAsyncEnumerable<object?> requestStream,
        Func<IAsyncEnumerable<object?>, CancellationToken, ValueTask<object?>> next,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        _logger.LogDebug("Starting client stream {Method}", methodName);

        try
        {
            var result = await next(requestStream, cancellationToken).ConfigureAwait(false);

            stopwatch.Stop();
            _logger.LogDebug("Completed client stream {Method} in {Elapsed}ms", methodName, stopwatch.ElapsedMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Failed client stream {Method} after {Elapsed}ms", methodName, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    public IAsyncEnumerable<object?> InvokeAsync(
        string methodName,
        IAsyncEnumerable<object?> requestStream,
        Func<IAsyncEnumerable<object?>, CancellationToken, IAsyncEnumerable<object?>> next,
        CancellationToken cancellationToken)
    {
        return InvokeBidiStreamingCore(methodName, requestStream, next, cancellationToken);
    }

private async IAsyncEnumerable<object?> InvokeBidiStreamingCore(
        string methodName,
        IAsyncEnumerable<object?> requestStream,
        Func<IAsyncEnumerable<object?>, CancellationToken, IAsyncEnumerable<object?>> next,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        int requestCount = 0;
        int responseCount = 0;

        _logger.LogDebug("Starting bidi stream {Method}", methodName);

        var configuredEnumerable = next(TrackRequests(requestStream, () => requestCount++), cancellationToken).WithCancellation(cancellationToken);
        await foreach (var item in configuredEnumerable)
        {
            responseCount++;
            yield return item;
        }

        stopwatch.Stop();
        _logger.LogDebug("Completed bidi stream {Method} in {Elapsed}ms ({RequestCount} req, {ResponseCount} resp)",
            methodName, stopwatch.ElapsedMilliseconds, requestCount, responseCount);
    }

    private static async IAsyncEnumerable<T> TrackRequests<T>(
        IAsyncEnumerable<T> source,
        Action onNext,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var configuredEnumerable = source.WithCancellation(cancellationToken);
        await foreach (var item in configuredEnumerable)
        {
            onNext();
            yield return item;
        }
    }
}

/// <summary>
/// Built-in metrics adapter.
/// </summary>
public sealed class MetricsAdapter : IUnaryAdapter, IServerStreamingAdapter, IClientStreamingAdapter, IBidiStreamingAdapter
{
    private readonly IMetricsCollector _metrics;

    public string Name => "Metrics";
    public int Priority => 200;

    public MetricsAdapter(IMetricsCollector metrics)
    {
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
    }

    public async ValueTask<object?> InvokeAsync(
        string methodName,
        object? args,
        Func<object?, CancellationToken, ValueTask<object?>> next,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        bool success = false;

        try
        {
            var result = await next(args, cancellationToken).ConfigureAwait(false);
            success = true;
            return result;
        }
        finally
        {
            stopwatch.Stop();
            _metrics.RecordUnaryCall(methodName, stopwatch.Elapsed, success);
        }
    }

    public IAsyncEnumerable<object?> InvokeAsync(
        string methodName,
        object? args,
        Func<object?, CancellationToken, IAsyncEnumerable<object?>> next,
        CancellationToken cancellationToken)
    {
        return InvokeServerStreamingCore(methodName, args, next, cancellationToken);
    }

    private async IAsyncEnumerable<object?> InvokeServerStreamingCore(
        string methodName,
        object? args,
        Func<object?, CancellationToken, IAsyncEnumerable<object?>> next,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        int elementCount = 0;
        bool success = false;

        try
        {
            await foreach (var element in next(args, cancellationToken).WithCancellation(cancellationToken))
            {
                elementCount++;
                yield return element;
            }
            success = true;
        }
        finally
        {
            stopwatch.Stop();
            _metrics.RecordServerStream(methodName, stopwatch.Elapsed, elementCount, success);
        }
    }

    public async ValueTask<object?> InvokeAsync(
        string methodName,
        IAsyncEnumerable<object?> requestStream,
        Func<IAsyncEnumerable<object?>, CancellationToken, ValueTask<object?>> next,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        bool success = false;

        try
        {
            var result = await next(requestStream, cancellationToken).ConfigureAwait(false);
            success = true;
            return result;
        }
        finally
        {
            stopwatch.Stop();
            _metrics.RecordClientStream(methodName, stopwatch.Elapsed, success);
        }
    }

    public IAsyncEnumerable<object?> InvokeAsync(
        string methodName,
        IAsyncEnumerable<object?> requestStream,
        Func<IAsyncEnumerable<object?>, CancellationToken, IAsyncEnumerable<object?>> next,
        CancellationToken cancellationToken)
    {
        return InvokeBidiStreamingCore(methodName, requestStream, next, cancellationToken);
    }

    private async IAsyncEnumerable<object?> InvokeBidiStreamingCore(
        string methodName,
        IAsyncEnumerable<object?> requestStream,
        Func<IAsyncEnumerable<object?>, CancellationToken, IAsyncEnumerable<object?>> next,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        int requestCount = 0;
        int responseCount = 0;
        bool success = false;

        try
        {
            var trackedStream = TrackRequests(requestStream, () => requestCount++);

            await foreach (var element in next(trackedStream, cancellationToken).WithCancellation(cancellationToken))
            {
                responseCount++;
                yield return element;
            }
            success = true;
        }
        finally
        {
            stopwatch.Stop();
            _metrics.RecordBidiStream(methodName, stopwatch.Elapsed, requestCount, responseCount, success);
        }
    }

    private static async IAsyncEnumerable<T> TrackRequests<T>(
        IAsyncEnumerable<T> source,
        Action onNext,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in source.WithCancellation(cancellationToken))
        {
            onNext();
            yield return item;
        }
    }
}

/// <summary>
/// Interface for logging (to avoid dependency on specific logging framework).
/// </summary>
public interface ILogger
{
    void LogDebug(string message, params object?[] args);
    void LogError(Exception ex, string message, params object?[] args);
}

/// <summary>
/// Interface for metrics collection.
/// </summary>
public interface IMetricsCollector
{
    void RecordUnaryCall(string methodName, TimeSpan duration, bool success);
    void RecordServerStream(string methodName, TimeSpan duration, int elementCount, bool success);
    void RecordClientStream(string methodName, TimeSpan duration, bool success);
    void RecordBidiStream(string methodName, TimeSpan duration, int requestCount, int responseCount, bool success);
}

/// <summary>
/// Adapter attribute for declarative adapter application.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class | AttributeTargets.Interface, AllowMultiple = true)]
public sealed class ThriftAdapterAttribute : Attribute
{
    public Type AdapterType { get; }
    public int Priority { get; set; }

    public ThriftAdapterAttribute(Type adapterType)
    {
        AdapterType = adapterType ?? throw new ArgumentNullException(nameof(adapterType));
        if (!typeof(IThriftAdapter).IsAssignableFrom(adapterType))
        {
            throw new ArgumentException($"Type {adapterType.Name} must implement IThriftAdapter", nameof(adapterType));
        }
    }
}