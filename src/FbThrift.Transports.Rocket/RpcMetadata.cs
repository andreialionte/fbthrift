// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using FBThrift;
using FbThrift.Core;

namespace FbThrift.Transports.Rocket;

/// <summary>
/// Request RPC metadata as defined in fbthrift RpcMetadata.thrift.
/// </summary>
public sealed class RequestRpcMetadata : IThriftSerializable
{
    public string? Name { get; set; }
    public ProtocolId? Protocol { get; set; }
    public RpcKind? Kind { get; set; }
    public CompressionAlgorithm? Compression { get; set; }
    public Dictionary<string, string>? OtherMetadata { get; set; }
    public Dictionary<string, string>? FrameworkMetadata { get; set; }
    public int? QueueTimeoutMs { get; set; }
    public int? ClientTimeoutMs { get; set; }
    public long? InteractionId { get; set; }
    public InteractionCreate? InteractionCreate { get; set; }
    public ChecksumAlgorithm? ChecksumAlgorithm { get; set; }
    public byte[]? Checksum { get; set; }

    public void __fbthrift_write(IThriftProtocolWriter writer)
    {
        if (Name != null)
        {
            writer.WriteFieldBegin(ThriftWireType.String, 1);
            writer.WriteString(Name);
        }
        if (Protocol.HasValue)
        {
            writer.WriteFieldBegin(ThriftWireType.I16, 2);
            writer.WriteI16((short)Protocol.Value);
        }
        if (Kind.HasValue)
        {
            writer.WriteFieldBegin(ThriftWireType.Byte, 3);
            writer.WriteByte((sbyte)Kind.Value);
        }
        if (Compression.HasValue)
        {
            writer.WriteFieldBegin(ThriftWireType.Byte, 4);
            writer.WriteByte((sbyte)Compression.Value);
        }
        if (OtherMetadata != null && OtherMetadata.Count > 0)
        {
            writer.WriteFieldBegin(ThriftWireType.Map, 5);
            writer.WriteMapBegin(ThriftWireType.String, ThriftWireType.String, OtherMetadata.Count);
            foreach (var kv in OtherMetadata)
            {
                writer.WriteString(kv.Key);
                writer.WriteString(kv.Value);
            }
        }
        if (FrameworkMetadata != null && FrameworkMetadata.Count > 0)
        {
            writer.WriteFieldBegin(ThriftWireType.Map, 6);
            writer.WriteMapBegin(ThriftWireType.String, ThriftWireType.String, FrameworkMetadata.Count);
            foreach (var kv in FrameworkMetadata)
            {
                writer.WriteString(kv.Key);
                writer.WriteString(kv.Value);
            }
        }
        if (QueueTimeoutMs.HasValue)
        {
            writer.WriteFieldBegin(ThriftWireType.I32, 7);
            writer.WriteI32(QueueTimeoutMs.Value);
        }
        if (ClientTimeoutMs.HasValue)
        {
            writer.WriteFieldBegin(ThriftWireType.I32, 8);
            writer.WriteI32(ClientTimeoutMs.Value);
        }
        if (InteractionId.HasValue)
        {
            writer.WriteFieldBegin(ThriftWireType.I64, 9);
            writer.WriteI64(InteractionId.Value);
        }
        if (InteractionCreate != null)
        {
            writer.WriteFieldBegin(ThriftWireType.Struct, 10);
            writer.WriteStruct(InteractionCreate);
        }
        if (ChecksumAlgorithm.HasValue)
        {
            writer.WriteFieldBegin(ThriftWireType.Byte, 11);
            writer.WriteByte((sbyte)ChecksumAlgorithm.Value);
        }
        if (Checksum != null && Checksum.Length > 0)
        {
            writer.WriteFieldBegin(ThriftWireType.String, 12);
            writer.WriteBinary(Checksum);
        }
        writer.WriteFieldStop();
    }

    public void __fbthrift_read(IThriftProtocolReader reader)
    {
        while (true)
        {
            var (fieldType, fieldId) = reader.ReadFieldBegin();
            if (fieldType == ThriftWireType.Stop) break;

            switch (fieldId)
            {
                case 1 when fieldType == ThriftWireType.String:
                    Name = reader.ReadString();
                    break;
                case 2 when fieldType == ThriftWireType.I16:
                    Protocol = (ProtocolId)reader.ReadI16();
                    break;
                case 3 when fieldType == ThriftWireType.Byte:
                    Kind = (RpcKind)reader.ReadByte();
                    break;
                case 4 when fieldType == ThriftWireType.Byte:
                    Compression = (CompressionAlgorithm)reader.ReadByte();
                    break;
                case 5 when fieldType == ThriftWireType.Map:
                    var (kt, vt, size) = reader.ReadMapBegin();
                    OtherMetadata = new Dictionary<string, string>(size);
                    for (int i = 0; i < size; i++)
                    {
                        var k = reader.ReadString();
                        var v = reader.ReadString();
                        OtherMetadata[k] = v;
                    }
                    break;
                case 6 when fieldType == ThriftWireType.Map:
                    var (kt2, vt2, size2) = reader.ReadMapBegin();
                    FrameworkMetadata = new Dictionary<string, string>(size2);
                    for (int i = 0; i < size2; i++)
                    {
                        var k = reader.ReadString();
                        var v = reader.ReadString();
                        FrameworkMetadata[k] = v;
                    }
                    break;
                case 7 when fieldType == ThriftWireType.I32:
                    QueueTimeoutMs = reader.ReadI32();
                    break;
                case 8 when fieldType == ThriftWireType.I32:
                    ClientTimeoutMs = reader.ReadI32();
                    break;
                case 9 when fieldType == ThriftWireType.I64:
                    InteractionId = reader.ReadI64();
                    break;
                case 10 when fieldType == ThriftWireType.Struct:
                    InteractionCreate = reader.ReadStruct<InteractionCreate>();
                    break;
                case 11 when fieldType == ThriftWireType.Byte:
                    ChecksumAlgorithm = (ChecksumAlgorithm)reader.ReadByte();
                    break;
                case 12 when fieldType == ThriftWireType.String:
                    Checksum = reader.ReadBinary();
                    break;
                default:
                    reader.Skip(fieldType);
                    break;
            }
            reader.ReadFieldEnd();
        }
    }
}

/// <summary>
/// Interaction create metadata.
/// </summary>
public sealed class InteractionCreate : IThriftSerializable
{
    public long InteractionId { get; set; }
    public string? InteractionName { get; set; }

    public void __fbthrift_write(IThriftProtocolWriter writer)
    {
        writer.WriteFieldBegin(ThriftWireType.I64, 1);
        writer.WriteI64(InteractionId);
        writer.WriteFieldBegin(ThriftWireType.String, 2);
        writer.WriteString(InteractionName ?? "");
        writer.WriteFieldStop();
    }

    public void __fbthrift_read(IThriftProtocolReader reader)
    {
        while (true)
        {
            var (fieldType, fieldId) = reader.ReadFieldBegin();
            if (fieldType == ThriftWireType.Stop) break;

            if (fieldId == 1 && fieldType == ThriftWireType.I64)
                InteractionId = reader.ReadI64();
            else if (fieldId == 2 && fieldType == ThriftWireType.String)
                InteractionName = reader.ReadString();
            else
                reader.Skip(fieldType);

            reader.ReadFieldEnd();
        }
    }
}

/// <summary>
/// Response RPC metadata.
/// </summary>
public sealed class ResponseRpcMetadata : IThriftSerializable
{
    public ChecksumAlgorithm? ChecksumAlgorithm { get; set; }
    public byte[]? Checksum { get; set; }
    public PayloadMetadata? PayloadMetadata { get; set; }

    public void __fbthrift_write(IThriftProtocolWriter writer)
    {
        if (ChecksumAlgorithm.HasValue)
        {
            writer.WriteFieldBegin(ThriftWireType.Byte, 1);
            writer.WriteByte((sbyte)ChecksumAlgorithm.Value);
        }
        if (Checksum != null && Checksum.Length > 0)
        {
            writer.WriteFieldBegin(ThriftWireType.String, 2);
            writer.WriteBinary(Checksum);
        }
        if (PayloadMetadata != null)
        {
            writer.WriteFieldBegin(ThriftWireType.Struct, 3);
            writer.WriteStruct(PayloadMetadata);
        }
        writer.WriteFieldStop();
    }

    public void __fbthrift_read(IThriftProtocolReader reader)
    {
        while (true)
        {
            var (fieldType, fieldId) = reader.ReadFieldBegin();
            if (fieldType == ThriftWireType.Stop) break;

            switch (fieldId)
            {
                case 1 when fieldType == ThriftWireType.Byte:
                    ChecksumAlgorithm = (ChecksumAlgorithm)reader.ReadByte();
                    break;
                case 2 when fieldType == ThriftWireType.String:
                    Checksum = reader.ReadBinary();
                    break;
                case 3 when fieldType == ThriftWireType.Struct:
                    PayloadMetadata = reader.ReadStruct<PayloadMetadata>();
                    break;
                default:
                    reader.Skip(fieldType);
                    break;
            }
            reader.ReadFieldEnd();
        }
    }
}

/// <summary>
/// Payload metadata for streaming.
/// </summary>
public sealed class PayloadMetadata : IThriftSerializable
{
    public ExceptionMetadata? ExceptionMetadata { get; set; }
    public StreamPayloadMetadata? StreamPayloadMetadata { get; set; }

    public void __fbthrift_write(IThriftProtocolWriter writer)
    {
        if (ExceptionMetadata != null)
        {
            writer.WriteFieldBegin(ThriftWireType.Struct, 1);
            writer.WriteStruct(ExceptionMetadata);
        }
        if (StreamPayloadMetadata != null)
        {
            writer.WriteFieldBegin(ThriftWireType.Struct, 2);
            writer.WriteStruct(StreamPayloadMetadata);
        }
        writer.WriteFieldStop();
    }

    public void __fbthrift_read(IThriftProtocolReader reader)
    {
        while (true)
        {
            var (fieldType, fieldId) = reader.ReadFieldBegin();
            if (fieldType == ThriftWireType.Stop) break;

            if (fieldId == 1 && fieldType == ThriftWireType.Struct)
                ExceptionMetadata = reader.ReadStruct<ExceptionMetadata>();
            else if (fieldId == 2 && fieldType == ThriftWireType.Struct)
                StreamPayloadMetadata = reader.ReadStruct<StreamPayloadMetadata>();
            else
                reader.Skip(fieldType);

            reader.ReadFieldEnd();
        }
    }
}

/// <summary>
/// Exception metadata for Rocket.
/// </summary>
public sealed class ExceptionMetadata : IThriftSerializable
{
    public string? What { get; set; }
    public RocketErrorCode ExceptionType { get; set; }
    public bool IsDeclared { get; set; }

    public void __fbthrift_write(IThriftProtocolWriter writer)
    {
        writer.WriteFieldBegin(ThriftWireType.String, 1);
        writer.WriteString(What ?? "");
        writer.WriteFieldBegin(ThriftWireType.I32, 2);
        writer.WriteI32((int)ExceptionType);
        writer.WriteFieldBegin(ThriftWireType.Bool, 3);
        writer.WriteBool(IsDeclared);
        writer.WriteFieldStop();
    }

    public void __fbthrift_read(IThriftProtocolReader reader)
    {
        while (true)
        {
            var (fieldType, fieldId) = reader.ReadFieldBegin();
            if (fieldType == ThriftWireType.Stop) break;

            if (fieldId == 1 && fieldType == ThriftWireType.String)
                What = reader.ReadString();
            else if (fieldId == 2 && fieldType == ThriftWireType.I32)
                ExceptionType = (RocketErrorCode)reader.ReadI32();
            else if (fieldId == 3 && fieldType == ThriftWireType.Bool)
                IsDeclared = reader.ReadBool();
            else
                reader.Skip(fieldType);

            reader.ReadFieldEnd();
        }
    }
}

/// <summary>
/// Stream payload metadata for checksums.
/// </summary>
public sealed class StreamPayloadMetadata : IThriftSerializable
{
    public ChecksumAlgorithm? ChecksumAlgorithm { get; set; }
    public byte[]? Checksum { get; set; }

    public void __fbthrift_write(IThriftProtocolWriter writer)
    {
        if (ChecksumAlgorithm.HasValue)
        {
            writer.WriteFieldBegin(ThriftWireType.Byte, 1);
            writer.WriteByte((sbyte)ChecksumAlgorithm.Value);
        }
        if (Checksum != null && Checksum.Length > 0)
        {
            writer.WriteFieldBegin(ThriftWireType.String, 2);
            writer.WriteBinary(Checksum);
        }
        writer.WriteFieldStop();
    }

    public void __fbthrift_read(IThriftProtocolReader reader)
    {
        while (true)
        {
            var (fieldType, fieldId) = reader.ReadFieldBegin();
            if (fieldType == ThriftWireType.Stop) break;

            if (fieldId == 1 && fieldType == ThriftWireType.Byte)
                ChecksumAlgorithm = (ChecksumAlgorithm)reader.ReadByte();
            else if (fieldId == 2 && fieldType == ThriftWireType.String)
                Checksum = reader.ReadBinary();
            else
                reader.Skip(fieldType);

            reader.ReadFieldEnd();
        }
    }
}
