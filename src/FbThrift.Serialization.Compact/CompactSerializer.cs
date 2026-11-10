// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Serialization.Compact;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using FbThrift.Core.Abstractions;
using FbThrift.Protocols.Compact;
using FbThrift.Core.Memory;

/// <summary>
/// Compact serializer using CompactProtocol.
/// </summary>
public sealed class CompactSerializer : ISerializer
{
    private readonly CompactProtocolWriter _writer;

    public CompactSerializer()
    {
        _writer = new CompactProtocolWriter();
    }

    public string Name => "compact";

    public byte[] Serialize<T>(T value) where T : class, IThriftSerializable
    {
        _writer.Reset();
        value.Write(_writer);
        return _writer.ToArray();
    }

    public async ValueTask SerializeAsync<T>(Stream stream, T value, CancellationToken cancellationToken = default) where T : class, IThriftSerializable
    {
        var bytes = Serialize(value);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    public T Deserialize<T>(byte[] data) where T : class, IThriftSerializable, new()
    {
        var reader = new CompactProtocolReader(data);
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