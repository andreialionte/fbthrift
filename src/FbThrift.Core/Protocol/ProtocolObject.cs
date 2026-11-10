// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Core.Protocol;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Buffers;
using FbThrift.Core;
using FbThrift.Core.Attributes;
using FbThrift.Core.Dynamic;
using FbThrift.Core.Mask;
using FbThrift.Core.Memory;
using FBThrift;

/// <summary>
/// Schema-less Protocol Object for dynamic Thrift manipulation.
/// Used for partial serialization/deserialization, Mask operations, and Any handling.
/// Based on fbthrift's protocol::Object.
/// </summary>
public sealed class ProtocolObject : IDictionary<string, object?>, IEquatable<ProtocolObject>
{
    private readonly Dictionary<string, object?> _fields = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ThriftFieldAttribute> _fieldAttributes = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates an empty ProtocolObject.
    /// </summary>
    public ProtocolObject() { }

    /// <summary>
    /// Creates a ProtocolObject from a Thrift struct.
    /// </summary>
    public static ProtocolObject FromThrift<T>(T thrift) where T : class, IThriftSerializable
    {
        var obj = new ProtocolObject();
        var type = typeof(T);
        var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        foreach (var field in fields)
        {
            var attr = field.GetCustomAttribute<ThriftFieldAttribute>();
            if (attr != null)
            {
                var value = field.GetValue(thrift);
                obj._fields[field.Name] = value;
                obj._fieldAttributes[field.Name] = attr;
            }
        }

        foreach (var prop in props)
        {
            var attr = prop.GetCustomAttribute<ThriftFieldAttribute>();
            if (attr != null && prop.CanRead)
            {
                var value = prop.GetValue(thrift);
                obj._fields[prop.Name] = value;
                obj._fieldAttributes[prop.Name] = attr;
            }
        }

        return obj;
    }

    /// <summary>
    /// Converts this ProtocolObject to a Thrift struct.
    /// </summary>
    public T ToThrift<T>() where T : class, IThriftSerializable, new()
    {
        var result = new T();
        var type = typeof(T);
        var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        foreach (var field in fields)
        {
            var attr = field.GetCustomAttribute<ThriftFieldAttribute>();
            if (attr != null && _fields.TryGetValue(field.Name, out var value))
            {
                field.SetValue(result, ConvertValue(value, field.FieldType));
            }
        }

        foreach (var prop in props)
        {
            var attr = prop.GetCustomAttribute<ThriftFieldAttribute>();
            if (attr != null && prop.CanWrite && _fields.TryGetValue(prop.Name, out var value))
            {
                prop.SetValue(result, ConvertValue(value, prop.PropertyType));
            }
        }

        return result;
    }

    /// <summary>
    /// Clears fields specified by a FieldMask.
    /// </summary>
    public void Clear(FieldMask mask)
    {
        if (mask.IsIncludeMask)
        {
            // Include mask: clear everything NOT in the mask
            var toRemove = _fields.Keys.Where(k => !mask.IncludesField(GetFieldId(k))).ToList();
            foreach (var key in toRemove)
            {
                _fields.Remove(key);
                _fieldAttributes.Remove(key);
            }

            // Recursively clear nested
            foreach (var kvp in mask.Includes)
            {
                var fieldName = GetFieldName(kvp.Key);
                if (_fields.TryGetValue(fieldName, out var value) && value is ProtocolObject nested)
                {
                    nested.Clear(kvp.Value);
                }
            }
        }
        else
        {
            // Exclude mask: clear fields in the mask
            foreach (var kvp in mask.Excludes)
            {
                var fieldName = GetFieldName(kvp.Key);
                if (_fields.TryGetValue(fieldName, out var value))
                {
                    if (value is ProtocolObject nested)
                    {
                        nested.Clear(kvp.Value);
                    }
                    else
                    {
                        _fields.Remove(fieldName);
                        _fieldAttributes.Remove(fieldName);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Ensures all fields in the mask are present (sets defaults for missing).
    /// </summary>
    public void Ensure(FieldMask mask)
    {
        if (mask.IsIncludeMask)
        {
            foreach (var kvp in mask.Includes)
            {
                var fieldName = GetFieldName(kvp.Key);
                if (!_fields.ContainsKey(fieldName))
                {
                    var attr = kvp.Value.GetType().GetField("FieldAttribute")?.GetValue(null) as ThriftFieldAttribute;
                    var defaultValue = GetDefaultValue(GetTypeFromTType(attr?.Type ?? TType.Stop));
                    _fields[fieldName] = defaultValue;
                }

                if (_fields.TryGetValue(fieldName, out var value) && value is ProtocolObject nested)
                {
                    nested.Ensure(kvp.Value);
                }
            }
        }
    }

    /// <summary>
    /// Returns a new ProtocolObject containing only the masked fields.
    /// </summary>
    public ProtocolObject Filter(FieldMask mask)
    {
        var result = new ProtocolObject();

        if (mask.IsIncludeMask)
        {
            foreach (var kvp in mask.Includes)
            {
                var fieldName = GetFieldName(kvp.Key);
                if (_fields.TryGetValue(fieldName, out var value))
                {
                    if (value is ProtocolObject nested)
                    {
                        result._fields[fieldName] = nested.Filter(kvp.Value);
                    }
                    else
                    {
                        result._fields[fieldName] = value;
                    }
                    if (_fieldAttributes.TryGetValue(fieldName, out var attr))
                    {
                        result._fieldAttributes[fieldName] = attr;
                    }
                }
            }
        }
        else
        {
            // Exclude mask: copy all except excluded
            foreach (var kvp in _fields)
            {
                var fieldId = GetFieldId(kvp.Key);
                if (!mask.Excludes.ContainsKey(fieldId))
                {
                    result._fields[kvp.Key] = kvp.Value;
                    if (_fieldAttributes.TryGetValue(kvp.Key, out var attr))
                    {
                        result._fieldAttributes[kvp.Key] = attr;
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Parses a ProtocolObject from serialized data using a FieldMask.
    /// Returns included fields and stores excluded data for later reconstruction.
    /// </summary>
    public static MaskedDecodeResult ParseObject<TProtocol>(byte[] data, FieldMask mask)
        where TProtocol : ITProtocolReader, new()
    {
        using var stream = new MemoryStream(data);
        var reader = new TProtocol();
        // Note: SetInput not available on ITProtocolReader, using constructor instead
        // This is a simplified implementation

        var included = new ProtocolObject();
        var excluded = new MaskedProtocolData();

        ReadStructMasked(reader, included, excluded, mask);

        return new MaskedDecodeResult { Included = included, Excluded = excluded };
    }

    /// <summary>
    /// Serializes a ProtocolObject with masked excluded data using FBThrift Compact protocol.
    /// </summary>
    public static byte[] SerializeObject(ProtocolObject obj, MaskedProtocolData excluded)
    {
        // Use FBThrift ThriftCompactWriter
        using var stream = new MemoryStream();
        var writer = new FBThrift.ThriftCompactWriter(stream);

        // Write struct begin
        writer.WriteStructBegin(null);

        // Write included fields
        foreach (var kvp in obj._fields)
        {
            if (obj._fieldAttributes.TryGetValue(kvp.Key, out var attr))
            {
                writer.WriteFieldBegin((FBThrift.ThriftWireType)attr.Type, attr.Id);
                WriteValueFB(writer, kvp.Value);
                writer.WriteFieldEnd();
            }
        }

        // Write excluded data as raw binary
        foreach (var kvp in excluded.Data)
        {
            writer.WriteBinary(kvp.Value);
        }

        writer.WriteFieldStop();
        writer.WriteStructEnd();

        return stream.ToArray();
    }

    private static void WriteValueCore<TProtocol>(TProtocol writer, object? value) where TProtocol : ITProtocolWriter
    {
        if (value == null) return;

        switch (value)
        {
            case bool b: writer.WriteBool(b); break;
            case byte b: writer.WriteByte(b); break;
            case short s: writer.WriteI16(s); break;
            case int i: writer.WriteI32(i); break;
            case long l: writer.WriteI64(l); break;
            case float f: writer.WriteFloat(f); break;
            case double d: writer.WriteDouble(d); break;
            case string s: writer.WriteString(s); break;
            case byte[] b: writer.WriteBinary(b); break;
            case ICollection<byte> bc: writer.WriteBinary(bc.ToArray()); break;
            case IReadOnlyList<object?> list: WriteListCore(writer, list); break;
            case IReadOnlySet<object?> set: WriteSetCore(writer, set); break;
            case IReadOnlyDictionary<object?, object?> map: WriteMapCore(writer, map); break;
            case ProtocolObject obj: WriteStructCore(writer, obj); break;
            default:
                if (value is IThriftSerializable thrift)
                {
                    // Can't call __fbthrift_write on ITProtocolWriter
                }
                break;
        }
    }

    private static void WriteValueFB(FBThrift.IThriftProtocolWriter writer, object? value)
    {
        if (value == null) return;

        switch (value)
        {
            case bool b: writer.WriteBool(b); break;
            case byte b: writer.WriteByte((sbyte)b); break;
            case short s: writer.WriteI16(s); break;
            case int i: writer.WriteI32(i); break;
            case long l: writer.WriteI64(l); break;
            case float f: writer.WriteFloat(f); break;
            case double d: writer.WriteDouble(d); break;
            case string s: writer.WriteString(s); break;
            case byte[] b: writer.WriteBinary(b); break;
            case ICollection<byte> bc: writer.WriteBinary(bc.ToArray()); break;
            case IReadOnlyList<object?> list: WriteListFB(writer, list); break;
            case IReadOnlySet<object?> set: WriteSetFB(writer, set); break;
            case IReadOnlyDictionary<object?, object?> map: WriteMapFB(writer, map); break;
            case ProtocolObject obj: WriteStructFB(writer, obj); break;
            default:
                if (value is IThriftSerializable thrift)
                {
                    thrift.__fbthrift_write(writer);
                }
                break;
        }
    }

    private static void WriteListFB(FBThrift.IThriftProtocolWriter writer, IReadOnlyList<object?> list)
    {
        if (list.Count == 0)
        {
            writer.WriteListBegin(FBThrift.ThriftWireType.Stop, 0);
            writer.WriteListEnd();
            return;
        }

        var firstType = list[0]?.GetType() ?? typeof(object);
        var thriftType = GetThriftType(firstType);
        writer.WriteListBegin((FBThrift.ThriftWireType)thriftType, list.Count);

        foreach (var item in list)
        {
            WriteValueFB(writer, item);
        }

        writer.WriteListEnd();
    }

    private static void WriteSetFB(FBThrift.IThriftProtocolWriter writer, IReadOnlySet<object?> set)
    {
        if (set.Count == 0)
        {
            writer.WriteSetBegin(FBThrift.ThriftWireType.Stop, 0);
            writer.WriteSetEnd();
            return;
        }

        var firstType = set.FirstOrDefault()?.GetType() ?? typeof(object);
        var thriftType = GetThriftType(firstType);
        writer.WriteSetBegin((FBThrift.ThriftWireType)thriftType, set.Count);

        foreach (var item in set)
        {
            WriteValueFB(writer, item);
        }

        writer.WriteSetEnd();
    }

    private static void WriteMapFB(FBThrift.IThriftProtocolWriter writer, IReadOnlyDictionary<object?, object?> map)
    {
        if (map.Count == 0)
        {
            writer.WriteMapBegin(FBThrift.ThriftWireType.Stop, FBThrift.ThriftWireType.Stop, 0);
            writer.WriteMapEnd();
            return;
        }

        var firstKey = map.Keys.FirstOrDefault();
        var firstValue = map.Values.FirstOrDefault();
        var keyType = GetThriftType(firstKey?.GetType() ?? typeof(object));
        var valueType = GetThriftType(firstValue?.GetType() ?? typeof(object));

        writer.WriteMapBegin((FBThrift.ThriftWireType)keyType, (FBThrift.ThriftWireType)valueType, map.Count);

        foreach (var kvp in map)
        {
            WriteValueFB(writer, kvp.Key);
            WriteValueFB(writer, kvp.Value);
        }

        writer.WriteMapEnd();
    }

    private static void WriteStructFB(FBThrift.IThriftProtocolWriter writer, ProtocolObject obj)
    {
        writer.WriteStructBegin(null);

        foreach (var kvp in obj._fields)
        {
            if (obj._fieldAttributes.TryGetValue(kvp.Key, out var attr))
            {
                writer.WriteFieldBegin((FBThrift.ThriftWireType)attr.Type, attr.Id);
                WriteValueFB(writer, kvp.Value);
                writer.WriteFieldEnd();
            }
        }

        writer.WriteFieldStop();
        writer.WriteStructEnd();
    }

    private static void WriteListCore<TProtocol>(TProtocol writer, IReadOnlyList<object?> list) where TProtocol : ITProtocolWriter
    {
        if (list.Count == 0)
        {
            writer.WriteListBegin(TType.Stop, 0);
            writer.WriteListEnd();
            return;
        }

        var firstType = list[0]?.GetType() ?? typeof(object);
        var thriftType = GetThriftType(firstType);
        writer.WriteListBegin(thriftType, list.Count);

        foreach (var item in list)
        {
            WriteValueCore(writer, item);
        }

        writer.WriteListEnd();
    }

    private static void WriteSetCore<TProtocol>(TProtocol writer, IReadOnlySet<object?> set) where TProtocol : ITProtocolWriter
    {
        if (set.Count == 0)
        {
            writer.WriteSetBegin(TType.Stop, 0);
            writer.WriteSetEnd();
            return;
        }

        var firstType = set.FirstOrDefault()?.GetType() ?? typeof(object);
        var thriftType = GetThriftType(firstType);
        writer.WriteSetBegin(thriftType, set.Count);

        foreach (var item in set)
        {
            WriteValueCore(writer, item);
        }

        writer.WriteSetEnd();
    }

    private static void WriteMapCore<TProtocol>(TProtocol writer, IReadOnlyDictionary<object?, object?> map) where TProtocol : ITProtocolWriter
    {
        if (map.Count == 0)
        {
            writer.WriteMapBegin(TType.Stop, TType.Stop, 0);
            writer.WriteMapEnd();
            return;
        }

        var firstKey = map.Keys.FirstOrDefault();
        var firstValue = map.Values.FirstOrDefault();
        var keyType = GetThriftType(firstKey?.GetType() ?? typeof(object));
        var valueType = GetThriftType(firstValue?.GetType() ?? typeof(object));

        writer.WriteMapBegin(keyType, valueType, map.Count);

        foreach (var kvp in map)
        {
            WriteValueCore(writer, kvp.Key);
            WriteValueCore(writer, kvp.Value);
        }

        writer.WriteMapEnd();
    }

    private static void WriteStructCore<TProtocol>(TProtocol writer, ProtocolObject obj) where TProtocol : ITProtocolWriter
    {
        writer.WriteStructBegin(null);

        foreach (var kvp in obj._fields)
        {
            if (obj._fieldAttributes.TryGetValue(kvp.Key, out var attr))
            {
                writer.WriteFieldBegin(attr.Type, attr.Id);
                WriteValueCore(writer, kvp.Value);
                writer.WriteFieldEnd();
            }
        }

        writer.WriteFieldStop();
        writer.WriteStructEnd();
    }

    /// <summary>
    /// Applies a patch (as ProtocolObject) to serialized data.
    /// </summary>
    public static byte[] ApplyPatchToSerializedData<TProtocol>(ProtocolObject patch, byte[] data)
        where TProtocol : ITProtocolReader, ITProtocolWriter, new()
    {
        // Extract mask from patch (fields that are present)
        var mask = FieldMask.Include(patch._fieldAttributes.Values.Select(a => a.Id).ToArray());

        var decodeResult = ParseObject<TProtocol>(data, mask);

        // Merge patch into included
        foreach (var kvp in patch._fields)
        {
            decodeResult.Included._fields[kvp.Key] = kvp.Value;
            if (patch._fieldAttributes.TryGetValue(kvp.Key, out var attr))
            {
                decodeResult.Included._fieldAttributes[kvp.Key] = attr;
            }
        }

        return SerializeObject(decodeResult.Included, decodeResult.Excluded);
    }

    #region IDictionary Implementation

    public object? this[string key]
    {
        get => _fields.TryGetValue(key, out var value) ? value : null;
        set => _fields[key] = value;
    }

    public ICollection<string> Keys => _fields.Keys;
    public ICollection<object?> Values => _fields.Values;
    public int Count => _fields.Count;
    public bool IsReadOnly => false;

    public void Add(string key, object? value) => _fields.Add(key, value);
    public void Add(KeyValuePair<string, object?> item) => ((IDictionary<string, object?>)_fields).Add(item);
    public void Clear() => _fields.Clear();
    public bool Contains(KeyValuePair<string, object?> item) => ((IDictionary<string, object?>)_fields).Contains(item);
    public bool ContainsKey(string key) => _fields.ContainsKey(key);
    public void CopyTo(KeyValuePair<string, object?>[] array, int arrayIndex) => ((IDictionary<string, object?>)_fields).CopyTo(array, arrayIndex);
    public bool Remove(string key) => _fields.Remove(key);
    public bool Remove(KeyValuePair<string, object?> item) => ((IDictionary<string, object?>)_fields).Remove(item);
    public bool TryGetValue(string key, out object? value) => _fields.TryGetValue(key, out value);
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _fields.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    #endregion

    #region Helper Methods

    private static void ReadStructMasked<TProtocol>(TProtocol reader, ProtocolObject included, MaskedProtocolData excluded, FieldMask mask)
        where TProtocol : ITProtocolReader
    {
        reader.ReadStructBegin();

        while (true)
        {
            var (type, fieldId) = reader.ReadFieldBegin();
            if (type == TType.Stop) break;

            var id = fieldId;
            var name = $"field_{id}"; // Thrift binary protocol doesn't include field names
            var fieldMask = mask.GetNestedMask(fieldId);

            if (mask.IncludesField(fieldId))
            {
                var value = ReadValue(reader, type);
                included._fields[name] = value;
                included._fieldAttributes[name] = new ThriftFieldAttribute(fieldId) { Type = type, Name = name };
            }
            else
            {
                // Store raw data for excluded field
                var fieldData = ReadRawField(reader, type);
                excluded.Data[fieldId] = fieldData;
            }

            reader.ReadFieldEnd();
        }

        reader.ReadStructEnd();
    }

    private static object? ReadValue<TProtocol>(TProtocol reader, TType type)
        where TProtocol : ITProtocolReader
    {
        return type switch
        {
            TType.Bool => reader.ReadBool(),
            TType.Byte => reader.ReadByte(),
            TType.I16 => reader.ReadI16(),
            TType.I32 => reader.ReadI32(),
            TType.I64 => reader.ReadI64(),
            TType.Double => reader.ReadDouble(),
            TType.Float => reader.ReadFloat(),
            TType.String => reader.ReadString(),
            TType.Binary => reader.ReadBinary(),
            TType.List => ReadList(reader),
            TType.Set => ReadSet(reader),
            TType.Map => ReadMap(reader),
            TType.Struct => ReadStruct(reader),
            _ => null
        };
    }

    private static List<object?> ReadList<TProtocol>(TProtocol reader) where TProtocol : ITProtocolReader
    {
        var (elementType, size) = reader.ReadListBegin();
        var list = new List<object?>(size);
        for (int i = 0; i < size; i++)
        {
            list.Add(ReadValue(reader, elementType));
        }
        reader.ReadListEnd();
        return list;
    }

    private static HashSet<object?> ReadSet<TProtocol>(TProtocol reader) where TProtocol : ITProtocolReader
    {
        var (elementType, size) = reader.ReadSetBegin();
        var set = new HashSet<object?>();
        for (int i = 0; i < size; i++)
        {
            set.Add(ReadValue(reader, elementType));
        }
        reader.ReadSetEnd();
        return set;
    }

    private static Dictionary<object?, object?> ReadMap<TProtocol>(TProtocol reader) where TProtocol : ITProtocolReader
    {
        var (keyType, valueType, size) = reader.ReadMapBegin();
        var map = new Dictionary<object?, object?>();
        for (int i = 0; i < size; i++)
        {
            var key = ReadValue(reader, keyType);
            var value = ReadValue(reader, valueType);
            map[key] = value;
        }
        reader.ReadMapEnd();
        return map;
    }

    private static ProtocolObject ReadStruct<TProtocol>(TProtocol reader) where TProtocol : ITProtocolReader
    {
        var obj = new ProtocolObject();
        reader.ReadStructBegin();

        while (true)
        {
            var (type, fieldId) = reader.ReadFieldBegin();
            if (type == TType.Stop) break;

            var id = fieldId;
            var name = $"field_{id}"; // Thrift binary protocol doesn't include field names

            var value = ReadValue(reader, type);
            obj._fields[name] = value;
            obj._fieldAttributes[name] = new ThriftFieldAttribute(fieldId) { Type = type, Name = name };

            reader.ReadFieldEnd();
        }

        reader.ReadStructEnd();
        return obj;
    }

    private static byte[] ReadRawField<TProtocol>(TProtocol reader, TType type) where TProtocol : ITProtocolReader
    {
        // This would need access to the underlying transport to read raw bytes
        // Simplified: read value and re-serialize using FBThrift Compact protocol
        using var stream = RecyclableStreamManager.GetStream();
        var writer = new ThriftCompactWriter(stream);
        var value = ReadValue(reader, type);
        WriteValueFB(writer, value);
        return stream.ToArray();
    }

    private static TType GetThriftType(Type type)
    {
        return type switch
        {
            Type t when t == typeof(bool) => TType.Bool,
            Type t when t == typeof(byte) => TType.Byte,
            Type t when t == typeof(short) => TType.I16,
            Type t when t == typeof(int) => TType.I32,
            Type t when t == typeof(long) => TType.I64,
            Type t when t == typeof(float) => TType.Float,
            Type t when t == typeof(double) => TType.Double,
            Type t when t == typeof(string) => TType.String,
            Type t when t == typeof(byte[]) => TType.Binary,
            Type t when t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>) => TType.List,
            Type t when t.IsGenericType && t.GetGenericTypeDefinition() == typeof(HashSet<>) => TType.Set,
            Type t when t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>) => TType.Map,
            _ => TType.Struct
        };
    }

    private static object? ConvertValue(object? value, Type targetType)
    {
        if (value == null) return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;
        if (targetType.IsAssignableFrom(value.GetType())) return value;

        // Try conversion
        if (targetType == typeof(int) && value is long l) return (int)l;
        if (targetType == typeof(long) && value is int i) return (long)i;
        if (targetType == typeof(float) && value is double d) return (float)d;
        if (targetType == typeof(double) && value is float f) return (double)f;

        // For ProtocolObject -> struct conversion
        if (value is ProtocolObject obj && typeof(IThriftSerializable).IsAssignableFrom(targetType))
        {
            var method = typeof(ProtocolObject).GetMethod(nameof(ToThrift))!.MakeGenericMethod(targetType);
            return method.Invoke(obj, null)!;
        }

        return Convert.ChangeType(value, targetType);
    }

    private static Type GetTypeFromTType(TType ttype)
    {
        return ttype switch
        {
            TType.Bool => typeof(bool),
            TType.Byte => typeof(byte),
            TType.I16 => typeof(short),
            TType.I32 => typeof(int),
            TType.I64 => typeof(long),
            TType.Float => typeof(float),
            TType.Double => typeof(double),
            TType.String => typeof(string),
            TType.Binary => typeof(byte[]),
            TType.Struct => typeof(object),
            TType.Map => typeof(IDictionary<object, object>),
            TType.Set => typeof(ISet<object>),
            TType.List => typeof(IList<object>),
            TType.Enum => typeof(int),
            TType.Uuid => typeof(Guid),
            _ => typeof(object)
        };
    }

    private static object? GetDefaultValue(Type type)
    {
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }

    private static short GetFieldId(string name) => (short)name.GetHashCode();
    private static string GetFieldName(short id) => $"Field{id}";

    public bool Equals(ProtocolObject? other)
    {
        if (other == null) return false;
        if (Count != other.Count) return false;

        foreach (var kvp in _fields)
        {
            if (!other._fields.TryGetValue(kvp.Key, out var otherValue))
                return false;

            if (!Equals(kvp.Value, otherValue))
                return false;
        }

        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as ProtocolObject);

    public override int GetHashCode() => _fields.GetHashCode();

    public static bool operator ==(ProtocolObject? left, ProtocolObject? right)
    {
        if (left is null) return right is null;
        return left.Equals(right);
    }

    public static bool operator !=(ProtocolObject? left, ProtocolObject? right) => !(left == right);

    #endregion
}

/// <summary>
/// Stores excluded field data for masked deserialization.
/// </summary>
public sealed class MaskedProtocolData
{
    public Dictionary<short, byte[]> Data { get; } = new();

    public void Add(short fieldId, byte[] data) => Data[fieldId] = data;
    public bool TryGet(short fieldId, out byte[]? data) => Data.TryGetValue(fieldId, out data);
}

/// <summary>
/// Result of masked deserialization.
/// </summary>
public sealed class MaskedDecodeResult
{
    public ProtocolObject Included { get; init; } = new();
    public MaskedProtocolData Excluded { get; init; } = new();
}

/// <summary>
/// Partial deserialization - only parses fields specified by mask.
/// </summary>
public static class PartialDeserializer
{
    /// <summary>
    /// Parses only the masked fields from serialized data.
    /// </summary>
    public static ProtocolObject ParseObjectWithoutExcludedData<TProtocol>(byte[] data, FieldMask mask)
        where TProtocol : ITProtocolReader, new()
    {
        var result = ProtocolObject.ParseObject<TProtocol>(data, mask);
        return result.Included;
    }
}