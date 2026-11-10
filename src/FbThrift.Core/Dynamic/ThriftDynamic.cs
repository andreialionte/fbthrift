// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Core.Dynamic;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using FbThrift.Core.Compiler;
using FbThrift.Core.Memory;
using FbThrift.Core.Protocol;
using FBThrift;

/// <summary>
    /// Represents a dynamic Thrift value that can hold any Thrift type.
    /// Similar to protobuf's Any or C# dynamic, but with Thrift-specific type information.
    /// </summary>
    [JsonConverter(typeof(ThriftDynamicJsonConverter))]
    public readonly partial struct ThriftDynamic : IEquatable<ThriftDynamic>
    {
    private readonly object? _value;
    private readonly FbThrift.Core.Compiler.ThriftType _type;

    /// <summary>
    /// Creates an empty/null ThriftDynamic.
    /// </summary>
    public ThriftDynamic()
    {
        _value = null;
        _type = FbThrift.Core.Compiler.ThriftType.Void;
    }

    /// <summary>
    /// Creates a ThriftDynamic from a bool.
    /// </summary>
    public ThriftDynamic(bool value)
    {
        _value = value;
        _type = FbThrift.Core.Compiler.ThriftType.Bool;
    }

    /// <summary>
    /// Creates a ThriftDynamic from a byte.
    /// </summary>
    public ThriftDynamic(byte value)
    {
        _value = value;
        _type = FbThrift.Core.Compiler.ThriftType.Byte;
    }

    /// <summary>
    /// Creates a ThriftDynamic from a short.
    /// </summary>
    public ThriftDynamic(short value)
    {
        _value = value;
        _type = FbThrift.Core.Compiler.ThriftType.I16;
    }

    /// <summary>
    /// Creates a ThriftDynamic from an int.
    /// </summary>
    public ThriftDynamic(int value)
    {
        _value = value;
        _type = FbThrift.Core.Compiler.ThriftType.I32;
    }

    /// <summary>
    /// Creates a ThriftDynamic from a long.
    /// </summary>
    public ThriftDynamic(long value)
    {
        _value = value;
        _type = FbThrift.Core.Compiler.ThriftType.I64;
    }

    /// <summary>
    /// Creates a ThriftDynamic from a float.
    /// </summary>
    public ThriftDynamic(float value)
    {
        _value = value;
        _type = FbThrift.Core.Compiler.ThriftType.Float;
    }

    /// <summary>
    /// Creates a ThriftDynamic from a double.
    /// </summary>
    public ThriftDynamic(double value)
    {
        _value = value;
        _type = FbThrift.Core.Compiler.ThriftType.Double;
    }

    /// <summary>
    /// Creates a ThriftDynamic from a string.
    /// </summary>
    public ThriftDynamic(string? value)
    {
        _value = value ?? string.Empty;
        _type = FbThrift.Core.Compiler.ThriftType.String;
    }

    /// <summary>
    /// Creates a ThriftDynamic from a byte array (binary).
    /// </summary>
    public ThriftDynamic(byte[] value)
    {
        _value = value ?? Array.Empty<byte>();
        _type = FbThrift.Core.Compiler.ThriftType.Binary;
    }

    /// <summary>
    /// Creates a ThriftDynamic from a list.
    /// </summary>
    public ThriftDynamic(IReadOnlyList<ThriftDynamic> value)
    {
        _value = value ?? Array.Empty<ThriftDynamic>();
        _type = FbThrift.Core.Compiler.ThriftType.List;
    }

    /// <summary>
    /// Creates a ThriftDynamic from a set.
    /// </summary>
    public ThriftDynamic(IReadOnlySet<ThriftDynamic> value)
    {
        _value = value ?? new HashSet<ThriftDynamic>();
        _type = FbThrift.Core.Compiler.ThriftType.Set;
    }

    /// <summary>
    /// Creates a ThriftDynamic from a map.
    /// </summary>
    public ThriftDynamic(IReadOnlyDictionary<ThriftDynamic, ThriftDynamic> value)
    {
        _value = value ?? new Dictionary<ThriftDynamic, ThriftDynamic>();
        _type = FbThrift.Core.Compiler.ThriftType.Map;
    }

    /// <summary>
    /// Creates a ThriftDynamic from a struct (dictionary of field name -> value).
    /// </summary>
    public ThriftDynamic(IReadOnlyDictionary<string, ThriftDynamic> value)
    {
        _value = value ?? new Dictionary<string, ThriftDynamic>();
        _type = FbThrift.Core.Compiler.ThriftType.Struct;
    }

    /// <summary>
    /// Creates a ThriftDynamic from an enum value.
    /// </summary>
    public ThriftDynamic(int value, string enumName)
    {
        _value = (value, enumName);
        _type = FbThrift.Core.Compiler.ThriftType.Enum;
    }

    /// <summary>
    /// Gets the Thrift type of this dynamic value.
    /// </summary>
    public FbThrift.Core.Compiler.ThriftType Type => _type;

    /// <summary>
    /// Gets whether this value is null/empty.
    /// </summary>
    public bool IsNull => _value == null || _type == FbThrift.Core.Compiler.ThriftType.Void;

    /// <summary>
    /// Gets the underlying value as an object.
    /// </summary>
    public object? Value => _value;

    /// <summary>
    /// Attempts to get the value as a specific type.
    /// </summary>
    public bool TryGet<T>(out T result)
    {
        if (_value is T typed)
        {
            result = typed;
            return true;
        }

        if (_value == null)
        {
            result = default!;
            return false;
        }

        // Try conversion for numeric types
        if (typeof(T) == typeof(int) && _value is long l)
        {
            result = (T)(object)(int)l;
            return true;
        }
        if (typeof(T) == typeof(long) && _value is int i)
        {
            result = (T)(object)(long)i;
            return true;
        }
        if (typeof(T) == typeof(float) && _value is double d)
        {
            result = (T)(object)(float)d;
            return true;
        }
        if (typeof(T) == typeof(double) && _value is float f)
        {
            result = (T)(object)(double)f;
            return true;
        }

        result = default!;
        return false;
    }

    /// <summary>
    /// Gets the value as a specific type, throwing if not compatible.
    /// </summary>
    public T Get<T>()
    {
        if (TryGet<T>(out var result))
        {
            return result;
        }
        throw new InvalidCastException($"Cannot convert ThriftDynamic of type {_type} to {typeof(T).Name}");
    }

    /// <summary>
    /// Implicit conversion from bool.
    /// </summary>
    public static implicit operator ThriftDynamic(bool value) => new(value);

    /// <summary>
    /// Implicit conversion from byte.
    /// </summary>
    public static implicit operator ThriftDynamic(byte value) => new(value);

    /// <summary>
    /// Implicit conversion from short.
    /// </summary>
    public static implicit operator ThriftDynamic(short value) => new(value);

    /// <summary>
    /// Implicit conversion from int.
    /// </summary>
    public static implicit operator ThriftDynamic(int value) => new(value);

    /// <summary>
    /// Implicit conversion from long.
    /// </summary>
    public static implicit operator ThriftDynamic(long value) => new(value);

    /// <summary>
    /// Implicit conversion from float.
    /// </summary>
    public static implicit operator ThriftDynamic(float value) => new(value);

    /// <summary>
    /// Implicit conversion from double.
    /// </summary>
    public static implicit operator ThriftDynamic(double value) => new(value);

    /// <summary>
    /// Implicit conversion from string.
    /// </summary>
    public static implicit operator ThriftDynamic(string? value) => new(value);

    /// <summary>
    /// Implicit conversion from byte array.
    /// </summary>
    public static implicit operator ThriftDynamic(byte[] value) => new(value);

    /// <summary>
    /// Implicit conversion from list.
    /// </summary>
    public static implicit operator ThriftDynamic(List<ThriftDynamic> value) => new(value);

    /// <summary>
    /// Implicit conversion from dictionary (struct/map).
    /// </summary>
    public static implicit operator ThriftDynamic(Dictionary<string, ThriftDynamic> value) => new(value);

    /// <summary>
    /// Implicit conversion from dictionary (map with dynamic keys).
    /// </summary>
    public static implicit operator ThriftDynamic(Dictionary<ThriftDynamic, ThriftDynamic> value) => new(value);

    /// <summary>
    /// Equality comparison.
    /// </summary>
    public bool Equals(ThriftDynamic other)
    {
        if (_type != other._type) return false;
        if (_value == null && other._value == null) return true;
        if (_value == null || other._value == null) return false;

        var kind = _type.Kind;
        return kind switch
        {
            ThriftTypeKind.Bool => (bool)_value == (bool)other._value,
            ThriftTypeKind.Byte => (byte)_value == (byte)other._value,
            ThriftTypeKind.I16 => (short)_value == (short)other._value,
            ThriftTypeKind.I32 => (int)_value == (int)other._value,
            ThriftTypeKind.I64 => (long)_value == (long)other._value,
            ThriftTypeKind.Float => (float)_value == (float)other._value,
            ThriftTypeKind.Double => (double)_value == (double)other._value,
            ThriftTypeKind.String => (string)_value == (string)other._value,
            ThriftTypeKind.Binary => ((byte[])_value).SequenceEqual((byte[])other._value),
            ThriftTypeKind.List => ListEquals((IReadOnlyList<ThriftDynamic>)_value, (IReadOnlyList<ThriftDynamic>)other._value),
            ThriftTypeKind.Set => SetEquals((IReadOnlySet<ThriftDynamic>)_value, (IReadOnlySet<ThriftDynamic>)other._value),
            ThriftTypeKind.Map => MapEquals((IReadOnlyDictionary<ThriftDynamic, ThriftDynamic>)_value, (IReadOnlyDictionary<ThriftDynamic, ThriftDynamic>)other._value),
            ThriftTypeKind.Struct => StructEquals((IReadOnlyDictionary<string, ThriftDynamic>)_value, (IReadOnlyDictionary<string, ThriftDynamic>)other._value),
            ThriftTypeKind.Enum => ((int)_value, (string)_value.GetType().GetProperty("Item2")!.GetValue(_value)!) == ((int)other._value, (string)other._value.GetType().GetProperty("Item2")!.GetValue(other._value)!),
            _ => false
        };
    }

    /// <summary>
    /// Equality comparison with object.
    /// </summary>
    public override bool Equals(object? obj)
    {
        return obj is ThriftDynamic other && Equals(other);
    }

    /// <summary>
    /// Gets hash code.
    /// </summary>
    public override int GetHashCode()
    {
        return HashCode.Combine(_type, _value);
    }

    /// <summary>
    /// Equality operator.
    /// </summary>
    public static bool operator ==(ThriftDynamic left, ThriftDynamic right) => left.Equals(right);

    /// <summary>
    /// Inequality operator.
    /// </summary>
    public static bool operator !=(ThriftDynamic left, ThriftDynamic right) => !left.Equals(right);

    /// <summary>
    /// Converts to string representation.
    /// </summary>
    public override string ToString()
    {
        if (IsNull) return "null";

        var kind = _type.Kind;
        return kind switch
        {
            ThriftTypeKind.Bool => _value?.ToString()?.ToLower() ?? "false",
            ThriftTypeKind.Byte => _value?.ToString() ?? "0",
            ThriftTypeKind.I16 => _value?.ToString() ?? "0",
            ThriftTypeKind.I32 => _value?.ToString() ?? "0",
            ThriftTypeKind.I64 => _value?.ToString() ?? "0",
            ThriftTypeKind.Float => _value?.ToString() ?? "0",
            ThriftTypeKind.Double => _value?.ToString() ?? "0",
            ThriftTypeKind.String => $"\"{_value}\"",
            ThriftTypeKind.Binary => $"0x{Convert.ToHexString((byte[])_value)}",
            ThriftTypeKind.List => $"[{string.Join(", ", ((IReadOnlyList<ThriftDynamic>)_value).Select(v => v.ToString()))}]",
            ThriftTypeKind.Set => $"{{{string.Join(", ", ((IReadOnlySet<ThriftDynamic>)_value).Select(v => v.ToString()))}}}",
            ThriftTypeKind.Map => $"{{{string.Join(", ", ((IReadOnlyDictionary<ThriftDynamic, ThriftDynamic>)_value).Select(kvp => $"{kvp.Key}: {kvp.Value}"))}}}",
            ThriftTypeKind.Struct => $"{{{string.Join(", ", ((IReadOnlyDictionary<string, ThriftDynamic>)_value).Select(kvp => $"{kvp.Key}: {kvp.Value}"))}}}",
            ThriftTypeKind.Enum => $"{_value}",
            _ => "null"
        };
    }

    private static bool ListEquals(IReadOnlyList<ThriftDynamic> left, IReadOnlyList<ThriftDynamic> right)
    {
        if (left.Count != right.Count) return false;
        for (int i = 0; i < left.Count; i++)
        {
            if (!left[i].Equals(right[i])) return false;
        }
        return true;
    }

    private static bool SetEquals(IReadOnlySet<ThriftDynamic> left, IReadOnlySet<ThriftDynamic> right)
    {
        if (left.Count != right.Count) return false;
        foreach (var item in left)
        {
            if (!right.Contains(item)) return false;
        }
        return true;
    }

    private static bool MapEquals(IReadOnlyDictionary<ThriftDynamic, ThriftDynamic> left, IReadOnlyDictionary<ThriftDynamic, ThriftDynamic> right)
    {
        if (left.Count != right.Count) return false;
        foreach (var kvp in left)
        {
            if (!right.TryGetValue(kvp.Key, out var value) || !kvp.Value.Equals(value)) return false;
        }
        return true;
    }

    private static bool StructEquals(IReadOnlyDictionary<string, ThriftDynamic> left, IReadOnlyDictionary<string, ThriftDynamic> right)
    {
        if (left.Count != right.Count) return false;
        foreach (var kvp in left)
        {
            if (!right.TryGetValue(kvp.Key, out var value) || !kvp.Value.Equals(value)) return false;
        }
        return true;
    }
}

/// <summary>
/// Reader for ThriftDynamic values from a protocol.
/// </summary>
public static class ThriftDynamicReader
{
    /// <summary>
    /// Reads a ThriftDynamic value from the protocol.
    /// </summary>
    public static ThriftDynamic Read(ITProtocolReader input, TType type)
    {
        return Read(input, GetThriftType(type));
    }

/// <summary>
    /// Reads a ThriftDynamic value from the protocol.
    /// </summary>
    public static ThriftDynamic Read(ITProtocolReader input, FbThrift.Core.Compiler.ThriftType type)
    {
        var kind = type.Kind;
        return kind switch
        {
            ThriftTypeKind.Bool => new ThriftDynamic(input.ReadBool()),
            ThriftTypeKind.Byte => new ThriftDynamic(input.ReadByte()),
            ThriftTypeKind.I16 => new ThriftDynamic(input.ReadI16()),
            ThriftTypeKind.I32 => new ThriftDynamic(input.ReadI32()),
            ThriftTypeKind.I64 => new ThriftDynamic(input.ReadI64()),
            ThriftTypeKind.Float => new ThriftDynamic(input.ReadFloat()),
            ThriftTypeKind.Double => new ThriftDynamic(input.ReadDouble()),
            ThriftTypeKind.String => new ThriftDynamic(input.ReadString()),
            ThriftTypeKind.Binary => new ThriftDynamic(input.ReadBinary().ToArray()),
            ThriftTypeKind.List => ReadList(input),
            ThriftTypeKind.Set => ReadSet(input),
            ThriftTypeKind.Map => ReadMap(input),
            ThriftTypeKind.Struct => ReadStruct(input),
            _ => default
        };
    }

    private static FbThrift.Core.Compiler.ThriftType GetThriftType(TType type)
    {
        return type switch
        {
            TType.Bool => FbThrift.Core.Compiler.ThriftType.Bool,
            TType.Byte => FbThrift.Core.Compiler.ThriftType.Byte,
            TType.I16 => FbThrift.Core.Compiler.ThriftType.I16,
            TType.I32 => FbThrift.Core.Compiler.ThriftType.I32,
            TType.I64 => FbThrift.Core.Compiler.ThriftType.I64,
            TType.Float => FbThrift.Core.Compiler.ThriftType.Float,
            TType.Double => FbThrift.Core.Compiler.ThriftType.Double,
            TType.String => FbThrift.Core.Compiler.ThriftType.String,
            TType.Binary => FbThrift.Core.Compiler.ThriftType.Binary,
            TType.List => FbThrift.Core.Compiler.ThriftType.List,
            TType.Set => FbThrift.Core.Compiler.ThriftType.Set,
            TType.Map => FbThrift.Core.Compiler.ThriftType.Map,
            TType.Struct => FbThrift.Core.Compiler.ThriftType.Struct,
            _ => FbThrift.Core.Compiler.ThriftType.Void
        };
    }

    private static ThriftDynamic ReadList(ITProtocolReader input)
    {
        var (elementType, count) = input.ReadListBegin();
        var list = new List<ThriftDynamic>(count);
        var elementThriftType = GetThriftType(elementType);
        for (int i = 0; i < count; i++)
        {
            list.Add(Read(input, elementThriftType));
        }
        input.ReadListEnd();
        return new ThriftDynamic(list);
    }

    private static ThriftDynamic ReadSet(ITProtocolReader input)
    {
        var (elementType, count) = input.ReadSetBegin();
        var set = new HashSet<ThriftDynamic>();
        var elementThriftType = GetThriftType(elementType);
        for (int i = 0; i < count; i++)
        {
            set.Add(Read(input, elementThriftType));
        }
        input.ReadSetEnd();
        return new ThriftDynamic(set);
    }

    private static ThriftDynamic ReadMap(ITProtocolReader input)
    {
        var (keyType, valueType, count) = input.ReadMapBegin();
        var map = new Dictionary<ThriftDynamic, ThriftDynamic>();
        var keyThriftType = GetThriftType(keyType);
        var valueThriftType = GetThriftType(valueType);
        for (int i = 0; i < count; i++)
        {
            var key = Read(input, keyThriftType);
            var value = Read(input, valueThriftType);
            map[key] = value;
        }
        input.ReadMapEnd();
        return new ThriftDynamic(map);
    }

    private static ThriftDynamic ReadStruct(ITProtocolReader input)
    {
        input.ReadStructBegin();
        var dict = new Dictionary<string, ThriftDynamic>();

        while (true)
        {
            var (type, id) = input.ReadFieldBegin();
            if (type == TType.Stop)
            {
                break;
            }

            var value = Read(input, GetThriftType(type));
            dict[id.ToString()] = value;
            input.ReadFieldEnd();
        }

        input.ReadStructEnd();
        return new ThriftDynamic(dict);
    }
}

/// <summary>
    /// Writer for ThriftDynamic values to a protocol.
    /// </summary>
    public static class ThriftDynamicWriter
    {
        /// <summary>
        /// Writes a ThriftDynamic value to the protocol.
        /// </summary>
        public static void Write(FBThrift.IThriftProtocolWriter output, ThriftDynamic value)
        {
            var kind = value.Type.Kind;
            switch (kind)
            {
                case ThriftTypeKind.Bool:
                    output.WriteBool(value.Get<bool>());
                    break;
                case ThriftTypeKind.Byte:
                    output.WriteByte((sbyte)value.Get<byte>());
                    break;
                case ThriftTypeKind.I16:
                    output.WriteI16(value.Get<short>());
                    break;
                case ThriftTypeKind.I32:
                    output.WriteI32(value.Get<int>());
                    break;
                case ThriftTypeKind.I64:
                    output.WriteI64(value.Get<long>());
                    break;
                case ThriftTypeKind.Float:
                case ThriftTypeKind.Double:
                    output.WriteDouble(value.Get<double>());
                    break;
                case ThriftTypeKind.String:
                    output.WriteString(value.Get<string>());
                    break;
                case ThriftTypeKind.List:
                    WriteList(output, value.Get<IReadOnlyList<ThriftDynamic>>());
                    break;
                case ThriftTypeKind.Set:
                    WriteSet(output, value.Get<IReadOnlySet<ThriftDynamic>>());
                    break;
                case ThriftTypeKind.Map:
                    WriteMap(output, value.Get<IReadOnlyDictionary<ThriftDynamic, ThriftDynamic>>());
                    break;
                case ThriftTypeKind.Struct:
                    WriteStruct(output, value.Get<IReadOnlyDictionary<string, ThriftDynamic>>());
                    break;
                default:
                    // Void/Null - write nothing or throw
                    break;
            }
        }

    private static void WriteList(FBThrift.IThriftProtocolWriter output, IReadOnlyList<ThriftDynamic> list)
    {
        if (list.Count == 0)
        {
            output.WriteListBegin(FBThrift.ThriftWireType.Stop, 0);
            output.WriteListEnd();
            return;
        }

        // Determine element type from first element
        var elementType = list[0].Type;
        output.WriteListBegin((FBThrift.ThriftWireType)elementType.Kind, list.Count);

        foreach (var item in list)
        {
            Write(output, item);
        }

        output.WriteListEnd();
    }

    private static void WriteSet(FBThrift.IThriftProtocolWriter output, IReadOnlySet<ThriftDynamic> set)
    {
        if (set.Count == 0)
        {
            output.WriteSetBegin(FBThrift.ThriftWireType.Stop, 0);
            output.WriteSetEnd();
            return;
        }

        var elementType = set.First().Type;
        output.WriteSetBegin((FBThrift.ThriftWireType)elementType.Kind, set.Count);

        foreach (var item in set)
        {
            Write(output, item);
        }

        output.WriteSetEnd();
    }

    private static void WriteMap(FBThrift.IThriftProtocolWriter output, IReadOnlyDictionary<ThriftDynamic, ThriftDynamic> map)
    {
        if (map.Count == 0)
        {
            output.WriteMapBegin(FBThrift.ThriftWireType.Stop, FBThrift.ThriftWireType.Stop, 0);
            output.WriteMapEnd();
            return;
        }

        var first = map.First();
        var keyType = first.Key.Type;
        var valueType = first.Value.Type;

        output.WriteMapBegin((FBThrift.ThriftWireType)keyType.Kind, (FBThrift.ThriftWireType)valueType.Kind, map.Count);

        foreach (var kvp in map)
        {
            Write(output, kvp.Key);
            Write(output, kvp.Value);
        }

        output.WriteMapEnd();
    }

    private static void WriteStruct(FBThrift.IThriftProtocolWriter output, IReadOnlyDictionary<string, ThriftDynamic> dict)
    {
        output.WriteStructBegin(null);

        foreach (var kvp in dict)
        {
            output.WriteFieldBegin((FBThrift.ThriftWireType)kvp.Value.Type.Kind, 0);
            Write(output, kvp.Value);
            output.WriteFieldEnd();
        }

        output.WriteFieldStop();
        output.WriteStructEnd();
    }
}

/// <summary>
/// JSON converter for ThriftDynamic.
/// </summary>
internal sealed class ThriftDynamicJsonConverter : JsonConverter<ThriftDynamic>
{
    public override ThriftDynamic Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Null => default,
            JsonTokenType.True => new ThriftDynamic(true),
            JsonTokenType.False => new ThriftDynamic(false),
            JsonTokenType.Number when reader.TryGetInt32(out int i) => new ThriftDynamic(i),
            JsonTokenType.Number when reader.TryGetInt64(out long l) => new ThriftDynamic(l),
            JsonTokenType.Number when reader.TryGetDouble(out double d) => new ThriftDynamic(d),
            JsonTokenType.String => new ThriftDynamic(reader.GetString()),
            JsonTokenType.StartArray => ReadArray(ref reader, options),
            JsonTokenType.StartObject => ReadObject(ref reader, options),
            _ => default
        };
    }

    private static ThriftDynamic ReadArray(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        var list = new List<ThriftDynamic>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            list.Add(ReadArray(ref reader, options));
        }
        return new ThriftDynamic(list);
    }

    private static ThriftDynamic ReadObject(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        var dict = new Dictionary<string, ThriftDynamic>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString()!;
                reader.Read();
                dict[name] = ReadObject(ref reader, options);
            }
        }
        return new ThriftDynamic(dict);
    }

    public override void Write(Utf8JsonWriter writer, ThriftDynamic value, JsonSerializerOptions options)
    {
        if (value.IsNull)
        {
            writer.WriteNullValue();
            return;
        }

        var kind = value.Type.Kind;
        switch (kind)
        {
            case ThriftTypeKind.Bool:
                writer.WriteBooleanValue(value.Get<bool>());
                break;
            case ThriftTypeKind.Byte:
            case ThriftTypeKind.I16:
            case ThriftTypeKind.I32:
                writer.WriteNumberValue(value.Get<int>());
                break;
            case ThriftTypeKind.I64:
                writer.WriteNumberValue(value.Get<long>());
                break;
            case ThriftTypeKind.Float:
            case ThriftTypeKind.Double:
                writer.WriteNumberValue(value.Get<double>());
                break;
            case ThriftTypeKind.String:
                writer.WriteStringValue(value.Get<string>());
                break;
            case ThriftTypeKind.Binary:
                writer.WriteBase64StringValue(value.Get<byte[]>());
                break;
            case ThriftTypeKind.List:
                writer.WriteStartArray();
                foreach (var item in value.Get<IReadOnlyList<ThriftDynamic>>())
                {
                    Write(writer, item, options);
                }
                writer.WriteEndArray();
                break;
            case ThriftTypeKind.Set:
                writer.WriteStartArray();
                foreach (var item in value.Get<IReadOnlySet<ThriftDynamic>>())
                {
                    Write(writer, item, options);
                }
                writer.WriteEndArray();
                break;
            case ThriftTypeKind.Map:
                writer.WriteStartObject();
                foreach (var kvp in value.Get<IReadOnlyDictionary<ThriftDynamic, ThriftDynamic>>())
                {
                    writer.WritePropertyName(kvp.Key.ToString());
                    Write(writer, kvp.Value, options);
                }
                writer.WriteEndObject();
                break;
            case ThriftTypeKind.Struct:
                writer.WriteStartObject();
                foreach (var kvp in value.Get<IReadOnlyDictionary<string, ThriftDynamic>>())
                {
                    writer.WritePropertyName(kvp.Key);
                    Write(writer, kvp.Value, options);
                }
                writer.WriteEndObject();
                break;
            case ThriftTypeKind.Enum:
                writer.WriteNumberValue(value.Get<int>());
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }
}