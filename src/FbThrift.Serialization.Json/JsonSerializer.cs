// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Serialization.Json;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FbThrift.Core.Abstractions;
using FbThrift.Protocols.Json;
using FbThrift.Core.Memory;

/// <summary>
/// JSON serializer using JsonProtocol.
/// </summary>
public sealed class JsonSerializer : ISerializer
{
    private readonly JsonProtocolWriter _writer;

    public JsonSerializer()
    {
        _writer = new JsonProtocolWriter();
    }

    public string Name => "json";

    public byte[] Serialize<T>(T value) where T : class, IThriftSerializable
    {
        _writer.Reset();
        value.Write(_writer);
        _writer.Flush();
        return _writer.ToArray();
    }

    public async ValueTask SerializeAsync<T>(Stream stream, T value, CancellationToken cancellationToken = default) where T : class, IThriftSerializable
    {
        var bytes = Serialize(value);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    public T Deserialize<T>(byte[] data) where T : class, IThriftSerializable, new()
    {
        var reader = new JsonProtocolReader(data);
        var result = new T();
        result.Read(reader);
        return result;
    }

    public async ValueTask<T> DeserializeAsync<T>(Stream stream, CancellationToken cancellationToken = default) where T : class, IThriftSerializable, new()
    {
        var data = new byte[stream.Length];
        await stream.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
        return Deserialize<T>(data);
    }

    public async ValueTask DisposeAsync()
    {
        await Task.CompletedTask.ConfigureAwait(false);
    }
}