// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Serialization.Binary;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using FbThrift.Core.Abstractions;
using FbThrift.Protocols.Binary;
using FbThrift.Core.Memory;

/// <summary>
/// Binary serializer using BinaryProtocol.
/// </summary>
public sealed class BinarySerializer : ISerializer
{
    private readonly BinaryProtocolWriter _writer;
    private readonly BinaryProtocolReader _reader;

    public BinarySerializer()
    {
        _writer = new BinaryProtocolWriter();
    }

    public string Name => "binary";

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
        var reader = new BinaryProtocolReader(data);
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