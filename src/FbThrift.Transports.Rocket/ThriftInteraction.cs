// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Rocket;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FBThrift;
using FbThrift.Transports.Rocket;

/// <summary>
/// Represents an interaction - a stateful, long-lived RPC connection.
/// </summary>
public interface IThriftInteraction : IAsyncDisposable
{
    /// <summary>
    /// Gets the unique interaction ID.
    /// </summary>
    long InteractionId { get; }

    /// <summary>
    /// Gets the interaction name/type.
    /// </summary>
    string InteractionName { get; }

    /// <summary>
    /// Sends a request to the interaction and awaits a response.
    /// </summary>
    ValueTask<TResponse> RequestResponseAsync<TRequest, TResponse>(string methodName, TRequest request, CancellationToken cancellationToken = default)
        where TRequest : class, IThriftSerializable
        where TResponse : class, IThriftSerializable, new();

    /// <summary>
    /// Sends a oneway request (no response).
    /// </summary>
    ValueTask RequestNoResponseAsync<TRequest>(string methodName, TRequest request, CancellationToken cancellationToken = default)
        where TRequest : class, IThriftSerializable;

    /// <summary>
    /// Initiates a server-streaming request.
    /// </summary>
    IAsyncEnumerable<TResponse> RequestStreamAsync<TRequest, TResponse>(string methodName, TRequest request, CancellationToken cancellationToken = default)
        where TRequest : class, IThriftSerializable
        where TResponse : class, IThriftSerializable, new();

    /// <summary>
    /// Initiates a client-streaming request (sink).
    /// </summary>
    ValueTask<TResponse> RequestSinkAsync<TRequest, TResponse>(IAsyncEnumerable<TRequest> requests, CancellationToken cancellationToken = default)
        where TRequest : class, IThriftSerializable
        where TResponse : class, IThriftSerializable, new();

    /// <summary>
    /// Initiates a bidirectional streaming request.
    /// </summary>
    IAsyncEnumerable<TResponse> RequestBidiAsync<TRequest, TResponse>(IAsyncEnumerable<TRequest> requests, CancellationToken cancellationToken = default)
        where TRequest : class, IThriftSerializable
        where TResponse : class, IThriftSerializable, new();
}

/// <summary>
/// Client-side interaction handle.
/// </summary>
public sealed class ThriftInteractionClient : IThriftInteraction
{
    private readonly RocketClient _client;
    private readonly long _interactionId;
    private readonly string _interactionName;
    private readonly Dictionary<string, Delegate> _methodCache = new();

    public ThriftInteractionClient(RocketClient client, long interactionId, string interactionName)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _interactionId = interactionId;
        _interactionName = interactionName ?? throw new ArgumentNullException(nameof(interactionName));
    }

    public long InteractionId => _interactionId;
    public string InteractionName => _interactionName;

    public async ValueTask<TResponse> RequestResponseAsync<TRequest, TResponse>(string methodName, TRequest request, CancellationToken cancellationToken = default)
        where TRequest : class, IThriftSerializable
        where TResponse : class, IThriftSerializable, new()
    {
        // For interactions, we need to include the interaction ID in metadata
        var options = new RpcOptions
        {
            WriteHeaders = new Dictionary<string, string>
            {
                ["interaction-id"] = _interactionId.ToString(),
                ["interaction-name"] = _interactionName
            }
        };

        return await _client.CallAsync<TRequest, TResponse>($"{_interactionName}.{methodName}", request, options, cancellationToken);
    }

    public async ValueTask RequestNoResponseAsync<TRequest>(string methodName, TRequest request, CancellationToken cancellationToken = default)
        where TRequest : class, IThriftSerializable
    {
        // Fire and forget - use a void response type
        var options = new RpcOptions
        {
            WriteHeaders = new Dictionary<string, string>
            {
                ["interaction-id"] = _interactionId.ToString(),
                ["interaction-name"] = _interactionName
            }
        };

        await _client.CallAsync<TRequest, VoidResponse>($"{_interactionName}.{methodName}", request, options, cancellationToken);
    }

    public IAsyncEnumerable<TResponse> RequestStreamAsync<TRequest, TResponse>(string methodName, TRequest request, CancellationToken cancellationToken = default)
        where TRequest : class, IThriftSerializable
        where TResponse : class, IThriftSerializable, new()
    {
        return StreamAsyncCore<TRequest, TResponse>(methodName, request, cancellationToken);
    }

    private async IAsyncEnumerable<TResponse> StreamAsyncCore<TRequest, TResponse>(string methodName, TRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        where TRequest : class, IThriftSerializable
        where TResponse : class, IThriftSerializable, new()
    {
        var options = new RpcOptions
        {
            WriteHeaders = new Dictionary<string, string>
            {
                ["interaction-id"] = _interactionId.ToString(),
                ["interaction-name"] = _interactionName
            }
        };

        // For streaming, we use a stream response wrapper
        var response = await _client.CallAsync<TRequest, StreamResponse>(
            $"{_interactionName}.{methodName}", request, options, cancellationToken);

        // Deserialize the stream response data
        if (response.Data.Length > 0)
        {
            var streamResponse = ThriftSerializer.DecodeBinary<StreamResponse>(response.Data);
            var responseObj = ThriftSerializer.DecodeBinary<TResponse>(streamResponse.Data);
            yield return responseObj;
        }
    }

    public async ValueTask<TResponse> RequestSinkAsync<TRequest, TResponse>(IAsyncEnumerable<TRequest> requests, CancellationToken cancellationToken = default)
        where TRequest : class, IThriftSerializable
        where TResponse : class, IThriftSerializable, new()
    {
        // Serialize all requests first
        var requestsList = new List<byte[]>();
        await foreach (var req in requests.WithCancellation(cancellationToken))
        {
            requestsList.Add(ThriftSerializer.EncodeBinary(req));
        }

        var sinkRequest = new SinkRequest { Requests = requestsList };

        var options = new RpcOptions
        {
            WriteHeaders = new Dictionary<string, string>
            {
                ["interaction-id"] = _interactionId.ToString(),
                ["interaction-name"] = _interactionName
            }
        };

        // For sink, we send a SinkRequest and get a SinkResponse
        var sinkResponse = await _client.CallAsync<SinkRequest, SinkResponse>(
            $"{_interactionName}.Sink", sinkRequest, options, cancellationToken);

        // Deserialize the actual response from the sink response data
        return ThriftSerializer.DecodeBinary<TResponse>(sinkResponse.Data);
    }

    public IAsyncEnumerable<TResponse> RequestBidiAsync<TRequest, TResponse>(IAsyncEnumerable<TRequest> requests, CancellationToken cancellationToken = default)
        where TRequest : class, IThriftSerializable
        where TResponse : class, IThriftSerializable, new()
    {
        return BidiAsyncCore<TRequest, TResponse>(requests, cancellationToken);
    }

    private async IAsyncEnumerable<TResponse> BidiAsyncCore<TRequest, TResponse>(IAsyncEnumerable<TRequest> requests, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        where TRequest : class, IThriftSerializable
        where TResponse : class, IThriftSerializable, new()
    {
        // Simplified bidirectional streaming
        await foreach (var request in requests.WithCancellation(cancellationToken))
        {
            var options = new RpcOptions
            {
                WriteHeaders = new Dictionary<string, string>
                {
                    ["interaction-id"] = _interactionId.ToString(),
                    ["interaction-name"] = _interactionName
                }
            };

            var response = await _client.CallAsync<TRequest, TResponse>($"bidi", request, options, cancellationToken);
            yield return response;
        }
    }

    public ValueTask DisposeAsync()
    {
        // Cleanup interaction resources
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Void response for oneway methods.
/// </summary>
public sealed class VoidResponse : IThriftSerializable
{
    public void __fbthrift_write(IThriftProtocolWriter writer)
    {
        writer.WriteFieldStop();
    }

    public void __fbthrift_read(IThriftProtocolReader reader)
    {
        while (true)
        {
            var (fieldType, fieldId) = reader.ReadFieldBegin();
            if (fieldType == ThriftWireType.Stop) break;
            reader.Skip(fieldType);
            reader.ReadFieldEnd();
        }
    }
}

/// <summary>
/// Streaming response for interaction streaming calls.
/// </summary>
public sealed class StreamResponse : IThriftSerializable
{
    public byte[] Data { get; set; } = Array.Empty<byte>();

    public void __fbthrift_write(IThriftProtocolWriter writer)
    {
        writer.WriteFieldBegin(ThriftWireType.String, 1);
        writer.WriteBinary(Data);
        writer.WriteFieldStop();
    }

    public void __fbthrift_read(IThriftProtocolReader reader)
    {
        while (true)
        {
            var (fieldType, fieldId) = reader.ReadFieldBegin();
            if (fieldType == ThriftWireType.Stop) break;
            if (fieldId == 1 && fieldType == ThriftWireType.String)
                Data = reader.ReadBinary();
            else
                reader.Skip(fieldType);
            reader.ReadFieldEnd();
        }
    }
}

/// <summary>
/// Sink request wrapper for interaction sink calls.
/// </summary>
public sealed class SinkRequest : IThriftSerializable
{
    public List<byte[]> Requests { get; set; } = new();

    public void __fbthrift_write(IThriftProtocolWriter writer)
    {
        writer.WriteFieldBegin(ThriftWireType.List, 1);
        writer.WriteListBegin(ThriftWireType.String, Requests.Count);
        foreach (var req in Requests)
        {
            writer.WriteBinary(req);
        }
        writer.WriteFieldStop();
    }

    public void __fbthrift_read(IThriftProtocolReader reader)
    {
        while (true)
        {
            var (fieldType, fieldId) = reader.ReadFieldBegin();
            if (fieldType == ThriftWireType.Stop) break;
            if (fieldId == 1 && fieldType == ThriftWireType.List)
            {
                var (elemType, size) = reader.ReadListBegin();
                Requests = new List<byte[]>(size);
                for (int i = 0; i < size; i++)
                    Requests.Add(reader.ReadBinary());
                reader.ReadListEnd();
            }
            else
            {
                reader.Skip(fieldType);
            }
            reader.ReadFieldEnd();
        }
    }
}

/// <summary>
/// Sink response for interaction sink calls.
/// </summary>
public sealed class SinkResponse : IThriftSerializable
{
    public byte[] Data { get; set; } = Array.Empty<byte>();

    public void __fbthrift_write(IThriftProtocolWriter writer)
    {
        writer.WriteFieldBegin(ThriftWireType.String, 1);
        writer.WriteBinary(Data);
        writer.WriteFieldStop();
    }

    public void __fbthrift_read(IThriftProtocolReader reader)
    {
        while (true)
        {
            var (fieldType, fieldId) = reader.ReadFieldBegin();
            if (fieldType == ThriftWireType.Stop) break;
            if (fieldId == 1 && fieldType == ThriftWireType.String)
                Data = reader.ReadBinary();
            else
                reader.Skip(fieldType);
            reader.ReadFieldEnd();
        }
    }
}
