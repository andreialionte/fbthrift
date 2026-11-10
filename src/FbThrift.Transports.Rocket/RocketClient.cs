// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Rocket;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FBThrift;
using FbThrift.Core;

/// <summary>
/// Rocket client for making Thrift calls over Rocket transport.
/// </summary>
public sealed class RocketClient : IAsyncDisposable
{
    private readonly RocketTransport _transport;
    private readonly RpcOptions _options;

    public RocketClient(RocketTransport transport, RpcOptions? options = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options ?? new RpcOptions();
    }

    /// <summary>
    /// Makes a request-response call.
    /// </summary>
    public async Task<TResponse> CallAsync<TRequest, TResponse>(
        string methodName,
        TRequest request,
        CancellationToken cancellationToken = default)
        where TRequest : class, IThriftSerializable
        where TResponse : class, IThriftSerializable, new()
    {
        return await CallAsyncCore<TRequest, TResponse>(methodName, request, _options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Makes a request-response call with custom options.
    /// </summary>
    public async Task<TResponse> CallAsync<TRequest, TResponse>(
        string methodName,
        TRequest request,
        RpcOptions options,
        CancellationToken cancellationToken = default)
        where TRequest : class, IThriftSerializable
        where TResponse : class, IThriftSerializable, new()
    {
        ArgumentNullException.ThrowIfNull(options);
        return await CallAsyncCore<TRequest, TResponse>(methodName, request, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Core implementation for request-response calls.
    /// </summary>
    private async Task<TResponse> CallAsyncCore<TRequest, TResponse>(
        string methodName,
        TRequest request,
        RpcOptions options,
        CancellationToken cancellationToken)
        where TRequest : class, IThriftSerializable
        where TResponse : class, IThriftSerializable, new()
    {
        // Serialize request
        var requestData = ThriftSerializer.EncodeBinary(request);

        // Build request metadata
        var metadata = BuildRequestMetadata(methodName, requestData, options);

        // Encode metadata
        var metadataBytes = ThriftSerializer.EncodeCompact(metadata);

        // Create request frame
        var requestFrame = RocketFrame.CreateRequestResponse(
            0, // Will be set by transport
            metadataBytes,
            requestData,
            complete: true);

        // Send request and get response
        var responseFrame = await _transport.SendRequestAsync(requestFrame, cancellationToken).ConfigureAwait(false);

        // Process response
        return ProcessResponse<TResponse>(responseFrame);
    }

    /// <summary>
    /// Builds request metadata for Rocket frame.
    /// </summary>
    private static RequestRpcMetadata BuildRequestMetadata(string methodName, ReadOnlyMemory<byte> requestData, RpcOptions options)
    {
        var metadata = new RequestRpcMetadata
        {
            Name = methodName,
            Protocol = ProtocolId.Compact,
            Kind = RpcKind.SingleRequestSingleResponse,
            Compression = CompressionAlgorithm.None,
            OtherMetadata = options.WriteHeaders?.Count > 0 ? new Dictionary<string, string>(options.WriteHeaders) : null,
        };

        // Add checksum if requested
        if (options.State?.TryGetValue("checksum_algorithm", out var checksumAlgo) == true)
        {
            if (Enum.TryParse<ChecksumAlgorithm>(checksumAlgo.ToString(), out var algo))
            {
                metadata.ChecksumAlgorithm = algo;
                metadata.Checksum = ChecksumUtil.ComputeChecksum(algo, requestData.Span);
            }
        }

        return metadata;
    }

    /// <summary>
    /// Processes response frame and returns deserialized response.
    /// </summary>
    private static TResponse ProcessResponse<TResponse>(RocketFrame responseFrame)
        where TResponse : class, IThriftSerializable, new()
    {
        if (responseFrame.Header.Type == RocketFrameType.Error)
        {
            var errorMsg = System.Text.Encoding.UTF8.GetString(responseFrame.Data.Span);
            throw new IOException($"Rocket error: {errorMsg}");
        }

        // Parse response metadata
        if (responseFrame.Metadata.Length > 0)
        {
            var responseMeta = ThriftSerializer.DecodeCompact<ResponseRpcMetadata>(responseFrame.Metadata.ToArray());

            // Verify checksum if present
            if (responseMeta.ChecksumAlgorithm.HasValue && responseMeta.Checksum != null)
            {
                if (!ChecksumUtil.VerifyChecksum(responseMeta.ChecksumAlgorithm.Value, responseFrame.Data.Span, responseMeta.Checksum))
                {
                    throw new IOException("Checksum mismatch in response");
                }
            }

            // Check for application exception
            if (responseMeta.PayloadMetadata?.ExceptionMetadata != null)
            {
                var exc = responseMeta.PayloadMetadata.ExceptionMetadata;
                throw new ThriftApplicationException((int)exc.ExceptionType, exc.What ?? "Unknown error");
            }
        }

        // Deserialize response
        return ThriftSerializer.DecodeBinary<TResponse>(responseFrame.Data.ToArray());
    }

    /// <summary>
    /// Makes a streaming call (single request, streaming response).
    /// </summary>
    public async IAsyncEnumerable<TResponse> StreamAsync<TRequest, TResponse>(
        string methodName,
        TRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        where TRequest : class, IThriftSerializable
        where TResponse : class, IThriftSerializable, new()
    {
        // Serialize request
        var requestData = ThriftSerializer.EncodeBinary(request);

        // Build request metadata for streaming
        var metadata = new RequestRpcMetadata
        {
            Name = methodName,
            Protocol = ProtocolId.Compact,
            Kind = RpcKind.SingleRequestStreamingResponse,
        };

        var metadataBytes = ThriftSerializer.EncodeCompact(metadata);

        var requestFrame = RocketFrame.CreateStream(
            0,
            metadataBytes,
            requestData,
            complete: false);

        var responseFrame = await _transport.SendRequestAsync(requestFrame, cancellationToken).ConfigureAwait(false);

        // For streaming, we'd need to handle multiple PAYLOAD frames
        // This is a simplified implementation
        if (responseFrame.Header.Type == RocketFrameType.Error)
        {
            var errorMsg = System.Text.Encoding.UTF8.GetString(responseFrame.Data.Span);
            throw new IOException($"Rocket error: {errorMsg}");
        }

        // Parse initial response
        if (responseFrame.Metadata.Length > 0)
        {
            var responseMeta = ThriftSerializer.DecodeCompact<ResponseRpcMetadata>(responseFrame.Metadata.ToArray());

            if (responseMeta.PayloadMetadata?.ExceptionMetadata != null)
            {
                var exc = responseMeta.PayloadMetadata.ExceptionMetadata;
                throw new ThriftApplicationException((int)exc.ExceptionType, exc.What ?? "Unknown error");
            }
        }

        // Yield initial response if any
        if (responseFrame.Data.Length > 0)
        {
            var response = ThriftSerializer.DecodeBinary<TResponse>(responseFrame.Data.ToArray());
            yield return response;
        }

        // For full streaming support, we'd need to handle subsequent PAYLOAD frames
        // This would require a more complex implementation with a persistent connection
        // and handling of PAYLOAD frames with Follows flag
    }

    public async ValueTask DisposeAsync()
    {
        await _transport.DisposeAsync().ConfigureAwait(false);
    }
}
