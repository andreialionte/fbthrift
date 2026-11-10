// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FBThrift.Core.Interactions;

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FBThrift;
using FbThrift.Core.Rocket;

/// <summary>
/// Interaction ID for Thrift interactions (Rocket streaming).
/// </summary>
public readonly struct InteractionId : IEquatable<InteractionId>
{
    private readonly long _id;

    public InteractionId(long id)
    {
        _id = id;
    }

    public long Value => _id;

    public bool Equals(InteractionId other) => _id == other._id;
    public override bool Equals(object obj) => obj is InteractionId other && Equals(other);
    public override int GetHashCode() => _id.GetHashCode();
    public static bool operator ==(InteractionId left, InteractionId right) => left.Equals(right);
    public static bool operator !=(InteractionId left, InteractionId right) => !left.Equals(right);
    public override string ToString() => _id.ToString();
}

/// <summary>
/// Interaction kinds as defined by fbthrift Rocket protocol.
/// </summary>
public enum InteractionKind : byte
{
    // Client -> Server
    RequestResponse = 0x01,      // Single request, single response
    RequestStream = 0x02,        // Single request, streaming response
    RequestChannel = 0x03,       // Streaming request, streaming response (bidirectional)
    RequestFnf = 0x04,           // Fire-and-forget (no response)
    
    // Server -> Client (for responses)
    Response = 0x10,
    StreamChunk = 0x11,
    ChannelChunk = 0x12,
    
    // Control
    Close = 0x20,
    Error = 0x21,
}

/// <summary>
/// Interaction frame for streaming/chunked interactions.
/// </summary>
public readonly struct InteractionFrame
{
    public InteractionId InteractionId { get; init; }
    public InteractionKind Kind { get; init; }
    public long SequenceNumber { get; init; }
    public bool IsComplete { get; init; }
    public bool HasError { get; init; }
    public ReadOnlyMemory<byte> Payload { get; init; }
    public Dictionary<string, string> Metadata { get; init; }

    public static InteractionFrame CreateRequest(InteractionId id, InteractionKind kind, ReadOnlyMemory<byte> payload, Dictionary<string, string> metadata = null)
    {
        return new InteractionFrame
        {
            InteractionId = id,
            Kind = kind,
            SequenceNumber = 0,
            IsComplete = kind == InteractionKind.RequestResponse || kind == InteractionKind.RequestFnf,
            Payload = payload,
            Metadata = metadata ?? new Dictionary<string, string>()
        };
    }

    public static InteractionFrame CreateChunk(InteractionId id, long sequenceNumber, ReadOnlyMemory<byte> payload, bool isComplete = false)
    {
        return new InteractionFrame
        {
            InteractionId = id,
            Kind = InteractionKind.StreamChunk,
            SequenceNumber = sequenceNumber,
            IsComplete = isComplete,
            Payload = payload
        };
    }

    public static InteractionFrame CreateError(InteractionId id, string errorMessage)
    {
        return new InteractionFrame
        {
            InteractionId = id,
            Kind = InteractionKind.Error,
            IsComplete = true,
            Payload = System.Text.Encoding.UTF8.GetBytes(errorMessage)
        };
    }

    public static InteractionFrame CreateClose(InteractionId id)
    {
        return new InteractionFrame
        {
            InteractionId = id,
            Kind = InteractionKind.Close,
            IsComplete = true
        };
    }
}

/// <summary>
/// Inbound interaction handler for server-side streaming.
/// </summary>
public interface IInboundInteractionHandler
{
    ValueTask<InteractionFrame> HandleRequestAsync(InteractionFrame request, CancellationToken cancellationToken = default);
    IAsyncEnumerable<InteractionFrame> HandleStreamAsync(InteractionFrame request, CancellationToken cancellationToken = default);
    IAsyncEnumerable<InteractionFrame> HandleChannelAsync(IAsyncEnumerable<InteractionFrame> requestStream, CancellationToken cancellationToken = default);
}

/// <summary>
/// Outbound interaction client for client-side streaming.
/// </summary>
public interface IOutboundInteractionClient
{
    ValueTask<InteractionFrame> RequestResponseAsync(InteractionFrame request, CancellationToken cancellationToken = default);
    IAsyncEnumerable<InteractionFrame> RequestStreamAsync(InteractionFrame request, CancellationToken cancellationToken = default);
    IAsyncEnumerable<InteractionFrame> RequestChannelAsync(IAsyncEnumerable<InteractionFrame> requestStream, CancellationToken cancellationToken = default);
    ValueTask RequestFnfAsync(InteractionFrame request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Interaction manager for managing active interactions.
/// </summary>
public sealed class InteractionManager
{
    private readonly ConcurrentDictionary<InteractionId, InteractionState> _interactions = new();
    private long _nextInteractionId = 1;
    private readonly InteractionOptions _options;

    public InteractionManager(InteractionOptions options = null)
    {
        _options = options ?? new InteractionOptions();
    }

    /// <summary>
    /// Creates a new interaction ID.
    /// </summary>
    public InteractionId CreateInteractionId()
    {
        return new InteractionId(Interlocked.Increment(ref _nextInteractionId));
    }

    /// <summary>
    /// Registers an outbound interaction (client-side).
    /// </summary>
    public InteractionState RegisterOutbound(InteractionId id, InteractionKind kind, TaskCompletionSource<InteractionFrame> responseTcs = null)
    {
        var state = new InteractionState
        {
            Id = id,
            Kind = kind,
            Direction = InteractionDirection.Outbound,
            ResponseTcs = responseTcs,
            CreatedAt = DateTime.UtcNow
        };
        
        _interactions[id] = state;
        return state;
    }

    /// <summary>
    /// Registers an inbound interaction (server-side).
    /// </summary>
    public InteractionState RegisterInbound(InteractionId id, InteractionKind kind)
    {
        var state = new InteractionState
        {
            Id = id,
            Kind = kind,
            Direction = InteractionDirection.Inbound,
            CreatedAt = DateTime.UtcNow
        };
        
        _interactions[id] = state;
        return state;
    }

    /// <summary>
    /// Gets an interaction state.
    /// </summary>
    public bool TryGetInteraction(InteractionId id, out InteractionState state)
    {
        return _interactions.TryGetValue(id, out state);
    }

    /// <summary>
    /// Removes an interaction.
    /// </summary>
    public bool RemoveInteraction(InteractionId id)
    {
        return _interactions.TryRemove(id, out _);
    }

    /// <summary>
    /// Sends a response for an inbound interaction.
    /// </summary>
    public bool TryCompleteInbound(InteractionId id, InteractionFrame response)
    {
        if (_interactions.TryGetValue(id, out var state) && state.Direction == InteractionDirection.Inbound)
        {
            state.ResponseTcs?.TrySetResult(response);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Sends a chunk for a streaming interaction.
    /// </summary>
    public bool TrySendChunk(InteractionId id, InteractionFrame chunk)
    {
        if (_interactions.TryGetValue(id, out var state))
        {
            state.ChunkChannel?.Writer.TryWrite(chunk);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Completes an interaction with an error.
    /// </summary>
    public bool TryCompleteWithError(InteractionId id, Exception error)
    {
        if (_interactions.TryGetValue(id, out var state))
        {
            state.ResponseTcs?.TrySetException(error);
            state.ChunkChannel?.Writer.Complete(error);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Cleans up expired interactions.
    /// </summary>
    public int CleanupExpired(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        var removed = 0;

        foreach (var kvp in _interactions)
        {
            if (kvp.Value.CreatedAt < cutoff && kvp.Value.IsComplete)
            {
                if (_interactions.TryRemove(kvp.Key, out _))
                {
                    removed++;
                }
            }
        }

        return removed;
    }

    /// <summary>
    /// Gets all active interaction IDs.
    /// </summary>
    public IReadOnlyList<InteractionId> GetActiveInteractions()
    {
        var list = new List<InteractionId>();
        foreach (var kvp in _interactions)
        {
            if (!kvp.Value.IsComplete)
            {
                list.Add(kvp.Key);
            }
        }
        return list;
    }
}

/// <summary>
/// State of an interaction.
/// </summary>
public sealed class InteractionState
{
    public InteractionId Id { get; init; }
    public InteractionKind Kind { get; init; }
    public InteractionDirection Direction { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? CompletedAt { get; private set; }
    public bool IsComplete => CompletedAt.HasValue;
    public Exception Error { get; private set; }

    // For responses
    public TaskCompletionSource<InteractionFrame> ResponseTcs { get; init; }

    // For streaming chunks
    public Channel<InteractionFrame> ChunkChannel { get; } = Channel.CreateUnbounded<InteractionFrame>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = true
    });

    public IAsyncEnumerable<InteractionFrame> InboundChunks => ChunkChannel.Reader.ReadAllAsync();

    public void Complete(InteractionFrame response)
    {
        CompletedAt = DateTime.UtcNow;
        ResponseTcs?.TrySetResult(response);
        ChunkChannel.Writer.Complete();
    }

    public void CompleteWithError(Exception error)
    {
        Error = error;
        CompletedAt = DateTime.UtcNow;
        ResponseTcs?.TrySetException(error);
        ChunkChannel.Writer.Complete(error);
    }
}

/// <summary>
/// Direction of interaction.
/// </summary>
public enum InteractionDirection
{
    Inbound,   // Server receiving
    Outbound   // Client sending
}

/// <summary>
/// Options for interaction management.
/// </summary>
public sealed class InteractionOptions
{
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public int MaxConcurrentInteractions { get; set; } = 10000;
    public int MaxChunkBufferSize { get; set; } = 1024 * 1024; // 1MB
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// High-level streaming client for Thrift interactions.
/// </summary>
public sealed class StreamingClient : IOutboundInteractionClient, IAsyncDisposable
{
    private readonly InteractionManager _manager;
    private readonly Func<InteractionFrame, ValueTask> _sendFrame;
    private readonly InteractionOptions _options;

    public StreamingClient(
        InteractionManager manager,
        Func<InteractionFrame, ValueTask> sendFrame,
        InteractionOptions options = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _sendFrame = sendFrame ?? throw new ArgumentNullException(nameof(sendFrame));
        _options = options ?? new InteractionOptions();
    }

    public async ValueTask<InteractionFrame> RequestResponseAsync(InteractionFrame request, CancellationToken cancellationToken = default)
    {
        var interactionId = _manager.CreateInteractionId();
        var state = _manager.RegisterOutbound(interactionId, InteractionKind.RequestResponse);
        
        var requestFrame = request with { InteractionId = interactionId };
        await _sendFrame(requestFrame).ConfigureAwait(false);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_options.DefaultTimeout);

        try
        {
            var response = await state.ResponseTcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
            return response;
        }
        finally
        {
            _manager.RemoveInteraction(interactionId);
        }
    }

    public IAsyncEnumerable<InteractionFrame> RequestStreamAsync(InteractionFrame request, CancellationToken cancellationToken = default)
    {
        var interactionId = _manager.CreateInteractionId();
        var state = _manager.RegisterOutbound(interactionId, InteractionKind.RequestStream);
        
        var requestFrame = request with { InteractionId = interactionId };
        _ = _sendFrame(requestFrame).ConfigureAwait(false);

        return ReadStreamAsync(state, cancellationToken);
    }

    public async IAsyncEnumerable<InteractionFrame> RequestChannelAsync(
        IAsyncEnumerable<InteractionFrame> requestStream, 
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var interactionId = _manager.CreateInteractionId();
        var state = _manager.RegisterOutbound(interactionId, InteractionKind.RequestChannel);
        
        // Start reading response stream
        var responseTask = ReadStreamAsync(state, cancellationToken);
        
        // Send request stream
        await foreach (var frame in requestStream.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            await _sendFrame(frame with { InteractionId = interactionId }).ConfigureAwait(false);
        }

        // Send close frame
        await _sendFrame(InteractionFrame.CreateClose(interactionId)).ConfigureAwait(false);

        await foreach (var response in responseTask.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return response;
        }
    }

    public async ValueTask RequestFnfAsync(InteractionFrame request, CancellationToken cancellationToken = default)
    {
        var interactionId = _manager.CreateInteractionId();
        var state = _manager.RegisterOutbound(interactionId, InteractionKind.RequestFnf);
        
        var requestFrame = request with { InteractionId = interactionId };
        await _sendFrame(requestFrame).ConfigureAwait(false);
        
        // Fire-and-forget: no response expected
        _manager.RemoveInteraction(interactionId);
    }

    private async IAsyncEnumerable<InteractionFrame> ReadStreamAsync(
        InteractionState state, 
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var chunk in state.InboundChunks.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return chunk;
            if (chunk.IsComplete) break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Clean up any pending interactions
        var active = _manager.GetActiveInteractions();
        foreach (var id in active)
        {
            _manager.TryCompleteWithError(id, new ObjectDisposedException("StreamingClient disposed"));
        }
    }
}

/// <summary>
/// Server-side streaming handler.
/// </summary>
public sealed class StreamingServer : IInboundInteractionHandler
{
    private readonly InteractionManager _manager;
    private readonly Func<InteractionFrame, ValueTask> _sendFrame;
    private readonly InteractionOptions _options;

    public StreamingServer(
        InteractionManager manager,
        Func<InteractionFrame, ValueTask> sendFrame,
        InteractionOptions options = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _sendFrame = sendFrame ?? throw new ArgumentNullException(nameof(sendFrame));
        _options = options ?? new InteractionOptions();
    }

    public async ValueTask<InteractionFrame> HandleRequestAsync(InteractionFrame request, CancellationToken cancellationToken = default)
    {
        // This would be implemented by the user's service handler
        // For now, return a default response
        return InteractionFrame.CreateError(request.InteractionId, "Not implemented");
    }

    public async IAsyncEnumerable<InteractionFrame> HandleStreamAsync(
        InteractionFrame request, 
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var state = _manager.RegisterInbound(request.InteractionId, request.Kind);
        
        // This would be implemented by the user's service handler
        // For now, yield a default chunk
        yield return InteractionFrame.CreateChunk(request.InteractionId, 1, 
            System.Text.Encoding.UTF8.GetBytes("Stream not implemented"), true);
        
        state.Complete(InteractionFrame.CreateClose(request.InteractionId));
    }

    public async IAsyncEnumerable<InteractionFrame> HandleChannelAsync(
        IAsyncEnumerable<InteractionFrame> requestStream, 
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var firstFrame = default(InteractionFrame);
        var first = true;
        
        await foreach (var frame in requestStream.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (first)
            {
                firstFrame = frame;
                var state = _manager.RegisterInbound(frame.InteractionId, frame.Kind);
                first = false;
            }
            
            // Process incoming chunk
            // This would be implemented by the user's service handler
        }
        
        if (firstFrame.InteractionId.Value != 0)
        {
            yield return InteractionFrame.CreateClose(firstFrame.InteractionId);
        }
    }
}

/// <summary>
/// Extension for async enumerable with cancellation.
/// </summary>
internal static class AsyncEnumerableExtensions
{
    public static async IAsyncEnumerable<T> WithCancellation<T>(
        this IAsyncEnumerable<T> source,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }
}