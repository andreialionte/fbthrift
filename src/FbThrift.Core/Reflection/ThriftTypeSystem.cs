// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Core.Reflection;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FbThrift.Core.Attributes;
using FbThrift.Core.Compiler;

/// <summary>
/// Thrift Type System - provides runtime type reflection for Thrift types.
/// Based on fbthrift's type reflection system (thrift/lib/cpp2/reflection).
/// </summary>
public static class ThriftTypeSystem
{
    private static readonly Dictionary<Type, ThriftTypeInfo> _typeCache = new();
    private static readonly object _cacheLock = new();
    
    /// <summary>
    /// Gets type information for a Thrift-serializable type.
    /// </summary>
    public static ThriftTypeInfo GetTypeInfo(Type type)
    {
        lock (_cacheLock)
        {
            if (_typeCache.TryGetValue(type, out var cachedInfo))
                return cachedInfo;
            
            var newInfo = CreateTypeInfo(type);
            _typeCache[type] = newInfo;
            return newInfo;
        }
    }
    
    private static ThriftTypeInfo CreateTypeInfo(Type type)
    {
        if (type.IsPrimitive || type == typeof(string) || type == typeof(byte[]))
        {
            return new ThriftTypeInfo
            {
                Type = type,
                Kind = GetThriftTypeKind(type),
                IsPrimitive = true
            };
        }
        
        if (type.IsArray)
        {
            var elementType = type.GetElementType()!;
            return new ThriftTypeInfo
            {
                Type = type,
                Kind = ThriftTypeKind.List,
                IsPrimitive = false,
                ElementType = GetTypeInfo(type.GetElementType()!)
            };
        }
        
        if (type.IsGenericType)
        {
            var genericDef = type.GetGenericTypeDefinition();
            
            if (genericDef == typeof(List<>) || genericDef == typeof(IList<>) || genericDef == typeof(IEnumerable<>))
            {
                var elementType = type.GetGenericArguments()[0];
                return new ThriftTypeInfo
                {
                    Type = type,
                    Kind = ThriftTypeKind.List,
                    IsPrimitive = false,
                    ElementType = GetTypeInfo(type.GetGenericArguments()[0])
                };
            }
            
            if (genericDef == typeof(HashSet<>) || genericDef == typeof(ISet<>) || genericDef == typeof(ICollection<>))
            {
                var elementType = type.GetGenericArguments()[0];
                return new ThriftTypeInfo
                {
                    Type = type,
                    Kind = ThriftTypeKind.Set,
                    IsPrimitive = false,
                    ElementType = GetTypeInfo(type.GetGenericArguments()[0])
                };
            }
            
            if (genericDef == typeof(Dictionary<,>) || genericDef == typeof(IDictionary<,>) || genericDef == typeof(IReadOnlyDictionary<,>))
            {
                var args = type.GetGenericArguments();
                return new ThriftTypeInfo
                {
                    Type = type,
                    Kind = ThriftTypeKind.Map,
                    IsPrimitive = false,
                    KeyType = GetTypeInfo(args[0]),
                    ValueType = GetTypeInfo(args[1])
                };
            }
        }
        
        if (type.IsEnum)
        {
            return new ThriftTypeInfo
            {
                Type = type,
                Kind = ThriftTypeKind.Enum,
                IsPrimitive = false,
                EnumValues = Enum.GetValues(type)
                    .Cast<Enum>()
                    .ToDictionary(e => e.ToString(), e => Convert.ToInt64(e))
            };
        }
        
        // Struct/class - check for ThriftStruct attribute
        var structAttr = type.GetCustomAttribute<ThriftStructAttribute>();
        if (structAttr != null || type.IsClass || type.IsValueType)
        {
            var fields = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite)
                .Select(p => new ThriftFieldInfo
                {
                    Name = p.Name,
                    Id = p.GetCustomAttribute<ThriftFieldAttribute>()?.Id ?? 0,
                    Type = GetTypeInfo(p.PropertyType),
                    Required = p.GetCustomAttribute<ThriftFieldAttribute>()?.Required ?? false,
                    Optional = p.GetCustomAttribute<ThriftFieldAttribute>()?.Optional ?? false,
                    DefaultValue = null // DefaultValue not in ThriftFieldAttribute
                }).ToList();
            
            return new ThriftTypeInfo
            {
                Type = type,
                Kind = ThriftTypeKind.Struct,
                IsPrimitive = false,
                Fields = new List<ThriftFieldInfo>(),
                Properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.CanRead && p.CanWrite)
                    .Select(p => new ThriftFieldInfo
                    {
                        Name = p.Name,
                        Id = p.GetCustomAttribute<ThriftFieldAttribute>()?.Id ?? 0,
                        Type = GetTypeInfo(p.PropertyType),
                        Required = p.GetCustomAttribute<ThriftFieldAttribute>()?.Required ?? false,
                        Optional = p.GetCustomAttribute<ThriftFieldAttribute>()?.Optional ?? false,
                        DefaultValue = null // DefaultValue not in ThriftFieldAttribute
                    }).ToList()
            };
        }
        
        throw new NotSupportedException($"Type {type.FullName} is not supported by Thrift");
    }
    
    private static ThriftTypeKind GetThriftTypeKind(Type type)
    {
        if (type == typeof(bool)) return ThriftTypeKind.Bool;
        if (type == typeof(sbyte) || type == typeof(byte)) return ThriftTypeKind.Byte;
        if (type == typeof(short)) return ThriftTypeKind.I16;
        if (type == typeof(int)) return ThriftTypeKind.I32;
        if (type == typeof(long)) return ThriftTypeKind.I64;
        if (type == typeof(double)) return ThriftTypeKind.Double;
        if (type == typeof(float)) return ThriftTypeKind.Double; // Thrift only has double
        if (type == typeof(string)) return ThriftTypeKind.String;
        if (type == typeof(byte[])) return ThriftTypeKind.Binary;
        if (type == typeof(void)) return ThriftTypeKind.Void;
        
        if (type.IsEnum) return ThriftTypeKind.Enum;
        if (type.IsArray || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))) return ThriftTypeKind.List;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(HashSet<>)) return ThriftTypeKind.Set;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>)) return ThriftTypeKind.Map;
        
        return ThriftTypeKind.Struct;
    }
}

/// <summary>
/// Information about a Thrift type.
/// </summary>
public sealed class ThriftTypeInfo
{
    public Type Type { get; set; } = null!;
    public ThriftTypeKind Kind { get; set; }
    public bool IsPrimitive { get; set; }
    public ThriftTypeInfo? ElementType { get; set; }
    public ThriftTypeInfo? KeyType { get; set; }
    public ThriftTypeInfo? ValueType { get; set; }
    public Dictionary<string, long> EnumValues { get; set; } = new();
    public List<ThriftFieldInfo> Fields { get; set; } = new();
    public List<ThriftFieldInfo> Properties { get; set; } = new();
}

/// <summary>
/// Information about a Thrift field.
/// </summary>
public sealed class ThriftFieldInfo
{
    public string Name { get; set; } = string.Empty;
    public int Id { get; set; }
    public ThriftTypeInfo Type { get; set; } = null!;
    public bool Required { get; set; }
    public bool Optional { get; set; }
    public object? DefaultValue { get; set; }
}

/// <summary>
/// Attribute to mark a class/struct as a Thrift struct.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class ThriftStructAttribute : Attribute
{
    public string? Name { get; set; }
}

/// <summary>
/// Helper class for Thrift type operations.
/// </summary>
public static class ThriftTypeHelper
{
    /// <summary>
    /// Gets the Thrift type name for a .NET type.
    /// </summary>
    public static string GetThriftTypeName(Type type)
    {
        var info = ThriftTypeSystem.GetTypeInfo(type);
        return info.Kind switch
        {
            ThriftTypeKind.Void => "void",
            ThriftTypeKind.Bool => "bool",
            ThriftTypeKind.Byte => "byte",
            ThriftTypeKind.I16 => "i16",
            ThriftTypeKind.I32 => "i32",
            ThriftTypeKind.I64 => "i64",
            ThriftTypeKind.Float => "float",
            ThriftTypeKind.Double => "double",
            ThriftTypeKind.String => "string",
            ThriftTypeKind.Binary => "binary",
            ThriftTypeKind.List => $"list<{GetTypeName(info.ElementType)}>",
            ThriftTypeKind.Set => $"set<{GetTypeName(info.ElementType)}>",
            ThriftTypeKind.Map => $"map<{GetTypeName(info.KeyType)},{GetTypeName(info.ValueType)}>",
            ThriftTypeKind.Struct => info.Type.Name,
            ThriftTypeKind.Exception => info.Type.Name,
            ThriftTypeKind.Union => info.Type.Name,
            ThriftTypeKind.Typedef => info.Type.Name,
            ThriftTypeKind.Enum => info.Type.Name,
            ThriftTypeKind.Base => info.Type.Name,
            _ => type.Name
        };
        
        string GetTypeName(ThriftTypeInfo? info) => info?.Type.Name ?? "void";
    }
    
    /// <summary>
    /// Gets the TType enum value for a .NET type.
    /// </summary>
    public static TType GetTType(Type type)
    {
        var info = ThriftTypeSystem.GetTypeInfo(type);
        return info.Kind switch
        {
            ThriftTypeKind.Void => TType.Void,
            ThriftTypeKind.Bool => TType.Bool,
            ThriftTypeKind.Byte => TType.Byte,
            ThriftTypeKind.I16 => TType.I16,
            ThriftTypeKind.I32 => TType.I32,
            ThriftTypeKind.I64 => TType.I64,
            ThriftTypeKind.Float => TType.Float,
            ThriftTypeKind.Double => TType.Double,
            ThriftTypeKind.String => TType.String,
            ThriftTypeKind.Binary => TType.Binary,
            ThriftTypeKind.List => TType.List,
            ThriftTypeKind.Set => TType.Set,
            ThriftTypeKind.Map => TType.Map,
            ThriftTypeKind.Struct => TType.Struct,
            ThriftTypeKind.Exception => TType.Struct,
            ThriftTypeKind.Union => TType.Struct,
            ThriftTypeKind.Typedef => TType.Stop,
            ThriftTypeKind.Enum => TType.Enum,
            ThriftTypeKind.Base => TType.Stop,
            _ => TType.Stop
        };
    }
}