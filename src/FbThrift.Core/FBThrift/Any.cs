namespace FBThrift;

using System;
using System.Collections.Generic;

/// <summary>
/// Represents a Thrift type for Any/SemiAny serialization.
/// </summary>
public sealed class TypeStruct : IThriftSerializable, IEquatable<TypeStruct>
{
    public string? Name { get; set; }
    public List<TypeStruct>? Params { get; set; }

    public bool Equals(TypeStruct? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Name == other.Name && 
               (Params == other.Params || 
                (Params != null && other.Params != null && Params.Count == other.Params.Count && 
                 Params.TrueForAll(p => other.Params!.Exists(o => p.Equals(o)))));
    }

    public override bool Equals(object? obj) => obj is TypeStruct other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Name, Params);

    public void __fbthrift_write(IThriftProtocolWriter writer)
    {
        writer.WriteFieldBegin(ThriftWireType.String, 1);
        writer.WriteString(Name ?? "");
        writer.WriteFieldBegin(ThriftWireType.List, 2);
        writer.WriteListBegin(ThriftWireType.Struct, Params?.Count ?? 0);
        if (Params != null)
        {
            foreach (var param in Params)
            {
                writer.WriteStruct(param);
            }
        }
        writer.WriteFieldStop();
    }

    public void __fbthrift_read(IThriftProtocolReader reader)
    {
        while (true)
        {
            var (fieldType, fieldId) = reader.ReadFieldBegin();
            if (fieldType == ThriftWireType.Stop) break;
            if (fieldId == 1 && fieldType == ThriftWireType.String)
            {
                Name = reader.ReadString();
            }
            else if (fieldId == 2 && fieldType == ThriftWireType.List)
            {
                var (elemType, size) = reader.ReadListBegin();
                Params = new List<TypeStruct>(size);
                for (int i = 0; i < size; i++)
                {
                    Params.Add(reader.ReadStruct<TypeStruct>());
                }
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
/// Protocol union for Any serialization.
/// </summary>
public abstract class ProtocolUnion
{
    public static ProtocolUnion Binary { get; } = new BinaryProtocol();
    public static ProtocolUnion Compact { get; } = new CompactProtocol();
    public static ProtocolUnion Json { get; } = new JsonProtocol();

    public abstract string Name { get; }

    private sealed class BinaryProtocol : ProtocolUnion
    {
        public override string Name => "binary";
    }

    private sealed class CompactProtocol : ProtocolUnion
    {
        public override string Name => "compact";
    }

    private sealed class JsonProtocol : ProtocolUnion
    {
        public override string Name => "json";
    }
}

/// <summary>
/// Thrift Any - stores any Thrift value with type information.
/// </summary>
public sealed class AnyStruct : IThriftSerializable
{
    public TypeStruct? Type { get; set; }
    public ProtocolUnion? Protocol { get; set; }
    public byte[]? Data { get; set; }

    public void __fbthrift_write(IThriftProtocolWriter writer)
    {
        writer.WriteFieldBegin(ThriftWireType.Struct, 1);
        if (Type != null)
        {
            writer.WriteStruct(Type);
        }
        writer.WriteFieldBegin(ThriftWireType.Struct, 2);
        if (Protocol != null)
        {
            // Write protocol as string
            writer.WriteFieldBegin(ThriftWireType.String, 1);
            writer.WriteString(Protocol!.Name);
            writer.WriteFieldStop();
        }
        writer.WriteFieldBegin(ThriftWireType.String, 3);
        writer.WriteBinary(Data ?? Array.Empty<byte>());
        writer.WriteFieldStop();
    }

    public void __fbthrift_read(IThriftProtocolReader reader)
    {
        while (true)
        {
            var (fieldType, fieldId) = reader.ReadFieldBegin();
            if (fieldType == ThriftWireType.Stop) break;

            if (fieldId == 1 && fieldType == ThriftWireType.Struct)
            {
                Type = reader.ReadStruct<TypeStruct>();
            }
            else if (fieldId == 2 && fieldType == ThriftWireType.Struct)
            {
                // Read protocol as string
                var (subType, subId) = reader.ReadFieldBegin();
                if (subType == ThriftWireType.String && subId == 1)
                {
                    var protocolName = reader.ReadString();
                    Protocol = protocolName switch
                    {
                        "binary" => ProtocolUnion.Binary,
                        "compact" => ProtocolUnion.Compact,
                        "json" => ProtocolUnion.Json,
                        _ => ProtocolUnion.Binary
                    };
                }
                reader.ReadFieldEnd();
            }
            else if (fieldId == 3 && fieldType == ThriftWireType.String)
            {
                Data = reader.ReadBinary();
            }
            else
            {
                reader.Skip(fieldType);
            }
            reader.ReadFieldEnd();
        }
    }

    public bool IsEmpty => Data == null || Data.Length == 0;
}

/// <summary>
/// Thrift SemiAny - flexible container with optional type/protocol info.
/// </summary>
public sealed class SemiAnyStruct : IThriftSerializable
{
    public TypeStruct? Type { get; set; }
    public ProtocolUnion? Protocol { get; set; }
    public byte[]? Data { get; set; }

    public void __fbthrift_write(IThriftProtocolWriter writer)
    {
        if (Type != null)
        {
            writer.WriteFieldBegin(ThriftWireType.Struct, 1);
            writer.WriteStruct(Type);
        }
        if (Protocol != null)
        {
            writer.WriteFieldBegin(ThriftWireType.Struct, 2);
            writer.WriteFieldBegin(ThriftWireType.String, 1);
            writer.WriteString(Protocol.Name);
            writer.WriteFieldStop();
        }
        writer.WriteFieldBegin(ThriftWireType.String, 3);
        writer.WriteBinary(Data ?? Array.Empty<byte>());
        writer.WriteFieldStop();
    }

    public void __fbthrift_read(IThriftProtocolReader reader)
    {
        while (true)
        {
            var (fieldType, fieldId) = reader.ReadFieldBegin();
            if (fieldType == ThriftWireType.Stop) break;

            if (fieldId == 1 && fieldType == ThriftWireType.Struct)
            {
                Type = reader.ReadStruct<TypeStruct>();
            }
            else if (fieldId == 2 && fieldType == ThriftWireType.Struct)
            {
                var (subType, subId) = reader.ReadFieldBegin();
                if (subType == ThriftWireType.String && subId == 1)
                {
                    var protocolName = reader.ReadString();
                    Protocol = protocolName switch
                    {
                        "binary" => ProtocolUnion.Binary,
                        "compact" => ProtocolUnion.Compact,
                        "json" => ProtocolUnion.Json,
                        _ => ProtocolUnion.Binary
                    };
                }
                reader.ReadFieldEnd();
            }
            else if (fieldId == 3 && fieldType == ThriftWireType.String)
            {
                Data = reader.ReadBinary();
            }
            else
            {
                reader.Skip(fieldType);
            }
            reader.ReadFieldEnd();
        }
    }

    public bool IsEmpty => Data == null || Data.Length == 0;

    /// <summary>
    /// Promotes this SemiAny to an AnyStruct if type and protocol are present.
    /// </summary>
    public AnyStruct ToAny()
    {
        if (Type == null || Protocol == null)
            throw new InvalidOperationException("Cannot promote SemiAny to Any: missing type or protocol");

        return new AnyStruct { Type = Type, Protocol = Protocol, Data = Data };
    }
}