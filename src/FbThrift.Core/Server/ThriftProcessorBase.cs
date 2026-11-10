namespace FbThrift.Core.Server;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FbThrift.Core.Protocol;

/// <summary>
/// Delegate that reads a Thrift args struct from an input protocol.
/// </summary>
public delegate object? ArgsReader(ITProtocolReader input);

/// <summary>
/// Delegate that invokes a Thrift method handler (unary).
/// </summary>
public delegate ValueTask<object?> MethodInvoker(object? args, CancellationToken cancellationToken);

/// <summary>
/// Delegate that writes a Thrift result struct to an output protocol.
/// </summary>
public delegate void ResultWriter(ITProtocolWriter output, object? result);

/// <summary>
/// Delegate that invokes a streaming method handler (server-side streaming).
/// </summary>
public delegate IAsyncEnumerable<object?> StreamingInvoker(object? args, CancellationToken cancellationToken);

/// <summary>
/// Delegate that invokes a client-streaming method handler.
/// </summary>
public delegate ValueTask<object?> ClientStreamingInvoker(IAsyncEnumerable<object?> requestStream, CancellationToken cancellationToken);

/// <summary>
/// Delegate that invokes a bidirectional streaming method handler.
/// </summary>
public delegate IAsyncEnumerable<object?> BidiStreamingInvoker(IAsyncEnumerable<object?> requestStream, CancellationToken cancellationToken);

/// <summary>
/// Delegate that writes a streaming response element to an output protocol.
/// </summary>
public delegate void StreamElementWriter(ITProtocolWriter output, object? element);

/// <summary>
/// Context for a single process invocation, includes connection context and metadata.
/// </summary>
public sealed class ProcessContext
{
    public TConnectionContext ConnectionContext { get; init; } = new();
    public Dictionary<string, object> Metadata { get; init; } = new();
}

/// <summary>
/// Streaming configuration for a method.
/// </summary>
public sealed class StreamingConfig
{
    public StreamingType Type { get; init; }
    public StreamElementWriter? ElementWriter { get; init; }
    public Func<ITProtocolReader, object?>? ElementReader { get; init; }
}

/// <summary>
/// Type of streaming RPC.
/// </summary>
public enum StreamingType
{
    None,
    ServerStreaming,
    ClientStreaming,
    BidiStreaming
}

/// <summary>
/// Base implementation of <see cref="IThriftProcessor"/> that dispatches incoming Thrift
/// calls to registered method handlers and writes replies or application exceptions.
/// Supports event handlers (TProcessorEventHandler), connection context (TConnectionContext),
/// and streaming (IAsyncEnumerable) / interactions.
/// </summary>
public class ThriftProcessorBase : IThriftProcessor
{
    private readonly Dictionary<string, ProcessorEntry> _handlers = new(StringComparer.Ordinal);
    private readonly List<TProcessorEventHandler> _eventHandlers = new();

    /// <summary>
    /// Adds an event handler for processor lifecycle events.
    /// </summary>
    public void AddEventHandler(TProcessorEventHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _eventHandlers.Add(handler);
    }

    /// <summary>
    /// Registers a unary method handler (request-response).
    /// </summary>
    public void RegisterHandler(
        string methodName,
        ArgsReader readArgs,
        MethodInvoker invoke,
        ResultWriter writeResult)
    {
        RegisterHandlerCore(methodName, new ProcessorEntry
        {
            ReadArgs = readArgs,
            Invoke = invoke,
            WriteResult = writeResult,
            StreamingConfig = null
        });
    }

    /// <summary>
    /// Registers a server-streaming method handler.
    /// </summary>
    public void RegisterServerStreamingHandler(
        string methodName,
        ArgsReader readArgs,
        StreamingInvoker invoke,
        StreamElementWriter writeElement)
    {
        RegisterHandlerCore(methodName, new ProcessorEntry
        {
            ReadArgs = readArgs,
            StreamingInvoke = invoke,
            StreamElementWriter = writeElement,
            StreamingConfig = new StreamingConfig { Type = StreamingType.ServerStreaming, ElementWriter = writeElement }
        });
    }

    /// <summary>
    /// Registers a client-streaming method handler.
    /// </summary>
    public void RegisterClientStreamingHandler(
        string methodName,
        Func<ITProtocolReader, object?> readElement,
        ClientStreamingInvoker invoke,
        ResultWriter writeResult)
    {
        RegisterHandlerCore(methodName, new ProcessorEntry
        {
            StreamingElementReader = readElement,
            ClientStreamingInvoke = invoke,
            WriteResult = writeResult,
            StreamingConfig = new StreamingConfig { Type = StreamingType.ClientStreaming, ElementReader = readElement }
        });
    }

    /// <summary>
    /// Registers a bidirectional streaming method handler.
    /// </summary>
    public void RegisterBidiStreamingHandler(
        string methodName,
        Func<ITProtocolReader, object?> readElement,
        BidiStreamingInvoker invoke,
        StreamElementWriter writeElement)
    {
        RegisterHandlerCore(methodName, new ProcessorEntry
        {
            StreamingElementReader = readElement,
            BidiStreamingInvoke = invoke,
            StreamElementWriter = writeElement,
            StreamingConfig = new StreamingConfig { Type = StreamingType.BidiStreaming, ElementWriter = writeElement, ElementReader = readElement }
        });
    }

    private void RegisterHandlerCore(string methodName, ProcessorEntry entry)
    {
        ArgumentNullException.ThrowIfNull(methodName);
        if (!_handlers.TryAdd(methodName, entry))
            throw new ArgumentException($"Method '{methodName}' is already registered.", nameof(methodName));
    }

    /// <inheritdoc/>
    public async ValueTask<bool> ProcessAsync(
        ITProtocolReader input,
        ITProtocolWriter output,
        CancellationToken cancellationToken = default)
    {
        return await ProcessAsync(input, output, new ProcessContext(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Processes a single request/response exchange with connection context and metadata.
    /// </summary>
    public async ValueTask<bool> ProcessAsync(
        ITProtocolReader input,
        ITProtocolWriter output,
        ProcessContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(context);

        foreach (var handler in _eventHandlers)
        {
            try { handler.GetContext(context.ConnectionContext, context.Metadata); } catch { }
        }

        (string name, TMessageType messageType, int seqId) = input.ReadMessageBegin();

        bool oneway = messageType == TMessageType.Oneway;

        try
        {
            foreach (var handler in _eventHandlers)
            {
                try { handler.RequestRead(context.ConnectionContext, context.Metadata); } catch { }
            }

            if (messageType == TMessageType.Call || oneway)
            {
                if (!_handlers.TryGetValue(name, out ProcessorEntry? entry))
                {
                    throw new TApplicationException(TApplicationExceptionType.UnknownMethod, $"Unknown method '{name}'.");
                }

                // Fire ProcessContext event
                foreach (var handler in _eventHandlers)
                {
                    try { handler.ProcessContext(context.ConnectionContext, context.Metadata); } catch { }
                }

                // Fire RequestContext event
                foreach (var handler in _eventHandlers)
                {
                    try { handler.RequestContext(context.ConnectionContext, context.Metadata); } catch { }
                }

                if (entry.StreamingConfig?.Type == StreamingType.ServerStreaming)
                {
                    await ProcessServerStreamingAsync(input, output, context, name, seqId, entry, cancellationToken).ConfigureAwait(false);
                }
                else if (entry.StreamingConfig?.Type == StreamingType.ClientStreaming)
                {
                    await ProcessClientStreamingAsync(input, output, context, name, seqId, entry, cancellationToken).ConfigureAwait(false);
                }
                else if (entry.StreamingConfig?.Type == StreamingType.BidiStreaming)
                {
                    await ProcessBidiStreamingAsync(input, output, context, name, seqId, entry, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    // Unary call
                    object? args = entry.ReadArgs(input);
                    object? result = await entry.Invoke(args, cancellationToken).ConfigureAwait(false);

                    if (!oneway)
                    {
                        foreach (var handler in _eventHandlers)
                        {
                            try { handler.RequestWrite(context.ConnectionContext, context.Metadata); } catch { }
                        }

                        await WriteReplyAsync(output, name, seqId, entry.WriteResult, result, cancellationToken).ConfigureAwait(false);

                        foreach (var handler in _eventHandlers)
                        {
                            try { handler.PostWrite(context.ConnectionContext, context.Metadata); } catch { }
                        }
                    }
                }

                return true;
            }

            if (messageType == TMessageType.Reply || messageType == TMessageType.Exception)
            {
                return false;
            }

            throw new TApplicationException(TApplicationExceptionType.InvalidMessageType, $"Unexpected message type '{messageType}'.");
        }
        catch (TApplicationException ex)
        {
            foreach (var handler in _eventHandlers)
            {
                try { handler.Error(context.ConnectionContext, ex); } catch { }
            }

            if (!oneway)
            {
                await WriteExceptionAsync(output, name, seqId, ex, cancellationToken).ConfigureAwait(false);
            }
            throw;
        }
        catch (Exception ex)
        {
            TApplicationException appEx = ex is TApplicationException ta ? ta : new TApplicationException(TApplicationExceptionType.InternalError, ex.Message, ex);

            foreach (var handler in _eventHandlers)
            {
                try { handler.Error(context.ConnectionContext, ex); } catch { }
            }

            if (!oneway)
            {
                await WriteExceptionAsync(output, name, seqId, appEx, cancellationToken).ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            foreach (var handler in _eventHandlers)
            {
                try { handler.Close(context.ConnectionContext); } catch { }
            }
        }
    }

    private async ValueTask ProcessServerStreamingAsync(
        ITProtocolReader input,
        ITProtocolWriter output,
        ProcessContext context,
        string name,
        int seqId,
        ProcessorEntry entry,
        CancellationToken cancellationToken)
    {
        object? args = entry.ReadArgs(input);

        // Write initial reply header
        output.WriteMessageBegin(name, TMessageType.Reply, (short)seqId);
        output.WriteStructBegin(null);

        // Get the streaming response
        var stream = entry.StreamingInvoke(args, cancellationToken);

        foreach (var handler in _eventHandlers)
        {
            try { handler.RequestWrite(context.ConnectionContext, context.Metadata); } catch { }
        }

        await foreach (var element in stream.WithCancellation(cancellationToken))
        {
            if (element != null)
            {
                entry.StreamElementWriter(output, element);
            }
        }

        output.WriteStop();
        output.WriteStructEnd();
        output.WriteMessageEnd();

        foreach (var handler in _eventHandlers)
        {
            try { handler.PostWrite(context.ConnectionContext, context.Metadata); } catch { }
        }
    }

    private async ValueTask ProcessClientStreamingAsync(
        ITProtocolReader input,
        ITProtocolWriter output,
        ProcessContext context,
        string name,
        int seqId,
        ProcessorEntry entry,
        CancellationToken cancellationToken)
    {
        // Read all request elements from the input stream
        var requestStream = ReadClientStream(input, entry.StreamingElementReader!, cancellationToken);

        foreach (var handler in _eventHandlers)
        {
            try { handler.RequestWrite(context.ConnectionContext, context.Metadata); } catch { }
        }

        object? result = await entry.ClientStreamingInvoke(requestStream, cancellationToken).ConfigureAwait(false);

        await WriteReplyAsync(output, name, seqId, entry.WriteResult, result, cancellationToken).ConfigureAwait(false);

        foreach (var handler in _eventHandlers)
        {
            try { handler.PostWrite(context.ConnectionContext, context.Metadata); } catch { }
        }
    }

    private async ValueTask ProcessBidiStreamingAsync(
        ITProtocolReader input,
        ITProtocolWriter output,
        ProcessContext context,
        string name,
        int seqId,
        ProcessorEntry entry,
        CancellationToken cancellationToken)
    {
        // Read all request elements from the input stream
        var requestStream = ReadClientStream(input, entry.StreamingElementReader!, cancellationToken);

        // Write initial reply header
        output.WriteMessageBegin(name, TMessageType.Reply, (short)seqId);
        output.WriteStructBegin(null);

        foreach (var handler in _eventHandlers)
        {
            try { handler.RequestWrite(context.ConnectionContext, context.Metadata); } catch { }
        }

        var responseStream = entry.BidiStreamingInvoke(requestStream, cancellationToken);

        await foreach (var element in responseStream.WithCancellation(cancellationToken))
        {
            if (element != null)
            {
                entry.StreamElementWriter(output, element);
            }
        }

        output.WriteStop();
        output.WriteStructEnd();
        output.WriteMessageEnd();

        foreach (var handler in _eventHandlers)
        {
            try { handler.PostWrite(context.ConnectionContext, context.Metadata); } catch { }
        }
    }

    private static async IAsyncEnumerable<object?> ReadClientStream(
        ITProtocolReader input,
        Func<ITProtocolReader, object?> readElement,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Read struct begin
        input.ReadStructBegin();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (fieldType, fieldId) = input.ReadFieldBegin();
            if (fieldType == TType.Stop)
            {
                break;
            }

            var element = readElement(input);
            if (element != null)
            {
                yield return element;
            }

            input.ReadFieldEnd();
        }

        input.ReadStructEnd();
    }

    private static Task WriteReplyAsync(
        ITProtocolWriter output,
        string name,
        int seqId,
        ResultWriter writeResult,
        object? result,
        CancellationToken cancellationToken)
    {
        output.WriteMessageBegin(name, TMessageType.Reply, (short)seqId);
        output.WriteStructBegin(null);
        writeResult(output, result);
        output.WriteStop();
        output.WriteStructEnd();
        output.WriteMessageEnd();
        return Task.CompletedTask;
    }

    private static Task WriteExceptionAsync(
        ITProtocolWriter output,
        string name,
        int seqId,
        TApplicationException ex,
        CancellationToken cancellationToken)
    {
        output.WriteMessageBegin(name, TMessageType.Exception, (short)seqId);
        output.WriteStructBegin(null);
        output.WriteFieldBegin(TType.String, 1);
        output.WriteString(ex.Message);
        output.WriteFieldEnd();
        output.WriteFieldBegin(TType.I32, 2);
        output.WriteI32((int)ex.Type);
        output.WriteFieldEnd();
        output.WriteStop();
        output.WriteStructEnd();
        output.WriteMessageEnd();
        return Task.CompletedTask;
    }

    private sealed class ProcessorEntry
    {
        // Unary
        public ArgsReader? ReadArgs { get; init; }
        public MethodInvoker? Invoke { get; init; }
        public ResultWriter? WriteResult { get; init; }

        // Server streaming
        public StreamingInvoker? StreamingInvoke { get; init; }
        public StreamElementWriter? StreamElementWriter { get; init; }

        // Client streaming
        public Func<ITProtocolReader, object?>? StreamingElementReader { get; init; }
        public ClientStreamingInvoker? ClientStreamingInvoke { get; init; }

        // Bidi streaming
        public BidiStreamingInvoker? BidiStreamingInvoke { get; init; }

        // Config
        public StreamingConfig? StreamingConfig { get; init; }
    }
}