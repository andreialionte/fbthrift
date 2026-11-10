// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Serialization.Dynamic;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FbThrift.Core.Abstractions;
using FbThrift.Core.Dynamic;
using FbThrift.Core.Memory;

/// <summary>
/// Dynamic serializer for ThriftDynamic values.
/// </summary>
public sealed class DynamicSerializer : ISerializer
{
    public DynamicSerializer()
    {
    }

    public string Name => "dynamic";

    public byte[] Serialize<T>(T value) where T : class, IThriftSerializable
    {
        // For dynamic serialization, we use the Compact protocol
        var writer = new FbThrift.Protocols.Compact.CompactProtocolWriter();
        value.Write(writer);
        return writer.ToArray();
    }

    public async ValueTask SerializeAsync<T>(Stream stream, T value, CancellationToken cancellationToken = default) where T : class, IThriftSerializable
    {
        var bytes = Serialize(value);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    public T Deserialize<T>(byte[] data) where T : class, IThriftSerializable, new()
    {
        var reader = new FbThrift.Protocols.Compact.CompactProtocolReader(data);
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

    public byte[] SerializeDynamic(ThriftDynamic value)
    {
        var writer = new FbThrift.Protocols.Compact.CompactProtocolWriter();
        FbThrift.Core.Dynamic.ThriftDynamicWriter.Write(writer, value);
        return writer.ToArray();
    }

    public ThriftDynamic DeserializeDynamic(byte[] data)
    {
        var reader = new FbThrift.Protocols.Compact.CompactProtocolReader(data);
        return FbThrift.Core.Dynamic.ThriftDynamicReader.Read(reader, ThriftType.Struct);
    }

    public async ValueTask DisposeAsync()
    {
        await Task.CompletedTask.ConfigureAwait(false);
    }
}