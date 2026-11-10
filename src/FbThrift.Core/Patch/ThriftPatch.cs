// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Core.Patch;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using FbThrift.Core.Compiler;
using FbThrift.Core.Dynamic;
using FBThrift;

/// <summary>
/// Thrift Patch - experimental feature for partial updates to Thrift structs.
/// Based on fbthrift's Patch feature.
/// Allows applying JSON Patch (RFC 6902) operations to Thrift structs.
/// </summary>
public static class ThriftPatch
{
    /// <summary>
    /// Applies a JSON Patch to a Thrift struct.
    /// </summary>
    /// <typeparam name="T">The Thrift struct type.</typeparam>
    /// <param name="target">The target struct to patch.</param>
    /// <param name="patch">The JSON Patch operations.</param>
    /// <returns>The patched struct (new instance if struct, modified if class).</returns>
    public static T Apply<T>(T target, JsonPatchDocument patch) where T : class, IThriftSerializable, new()
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(patch);

        // Convert target to dynamic for manipulation
        var dynamic = ToDynamic(target);
        var dict = dynamic.Get<IReadOnlyDictionary<string, ThriftDynamic>>().ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        
        // Apply each operation
        foreach (var operation in patch.Operations)
        {
            ApplyOperation(dict, operation);
        }

        // Convert back to struct
        return FromDynamic<T>(new ThriftDynamic(dict));
    }

    /// <summary>
    /// Applies a JSON Patch to a ThriftDynamic value.
    /// </summary>
    public static ThriftDynamic Apply(ThriftDynamic target, JsonPatchDocument patch)
    {
        ArgumentNullException.ThrowIfNull(patch);

        var dynamic = target.Type.Kind == ThriftTypeKind.Struct 
            ? target.Get<IReadOnlyDictionary<string, ThriftDynamic>>().ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
            : new Dictionary<string, ThriftDynamic>();

        foreach (var operation in patch.Operations)
        {
            ApplyOperation(dynamic, operation);
        }

        return new ThriftDynamic(dynamic);
    }

    private static void ApplyOperation(Dictionary<string, ThriftDynamic> target, JsonPatchOperation operation)
    {
        switch (operation.Op)
        {
            case "add":
                ApplyAdd(target, operation);
                break;
            case "remove":
                ApplyRemove(target, operation);
                break;
            case "replace":
                ApplyReplace(target, operation);
                break;
            case "move":
                ApplyMove(target, operation);
                break;
            case "copy":
                ApplyCopy(target, operation);
                break;
            case "test":
                ApplyTest(target, operation);
                break;
            default:
                throw new NotSupportedException($"Patch operation '{operation.Op}' is not supported");
        }
    }

    private static void ApplyAdd(Dictionary<string, ThriftDynamic> target, JsonPatchOperation operation)
    {
        var path = ParsePath(operation.Path);
        
        if (path.Length == 1)
        {
            // Adding to root object
            target[path[0]] = operation.Value ?? new ThriftDynamic();
        }
        else
        {
            // Navigate to parent
            var parent = NavigateToParent(target, path);
            if (parent != null)
            {
                parent[path[^1]] = operation.Value ?? new ThriftDynamic();
            }
        }
    }

    private static void ApplyRemove(Dictionary<string, ThriftDynamic> target, JsonPatchOperation operation)
    {
        var path = ParsePath(operation.Path);
        
        if (path.Length == 1)
        {
            target.Remove(path[0]);
        }
        else
        {
            var parent = NavigateToParent(target, path);
            parent?.Remove(path[^1]);
        }
    }

    private static void ApplyReplace(Dictionary<string, ThriftDynamic> target, JsonPatchOperation operation)
    {
        var path = ParsePath(operation.Path);
        
        if (path.Length == 1)
        {
            if (target.ContainsKey(path[0]))
            {
                target[path[0]] = operation.Value ?? new ThriftDynamic();
            }
            else
            {
                throw new InvalidOperationException($"Path '{operation.Path}' does not exist for replace");
            }
        }
        else
        {
            var parent = NavigateToParent(target, path);
            if (parent != null && parent.ContainsKey(path[^1]))
            {
                parent[path[^1]] = operation.Value ?? new ThriftDynamic();
            }
            else
            {
                throw new InvalidOperationException($"Path '{operation.Path}' does not exist for replace");
            }
        }
    }

    private static void ApplyMove(Dictionary<string, ThriftDynamic> target, JsonPatchOperation operation)
    {
        if (string.IsNullOrEmpty(operation.From))
            throw new ArgumentException("Move operation requires 'from' field");

var fromPath = ParsePath(operation.From);
        var toPath = ParsePath(operation.Path);

        // Get value from source
        ThriftDynamic value;
        try
        {
            value = NavigateToValue(target, fromPath);
        }
        catch (InvalidOperationException)
        {
            throw new InvalidOperationException($"Source path '{operation.From}' does not exist");
        }
        
        // Remove from source
        if (fromPath.Length == 1)
        {
            target.Remove(fromPath[0]);
        }
        else
        {
            var fromParent = NavigateToParent(target, fromPath);
            fromParent?.Remove(fromPath[^1]);
        }

        // Add to destination
        if (toPath.Length == 1)
        {
            target[toPath[0]] = value;
        }
        else
        {
            var toParent = NavigateToParent(target, toPath);
            toParent?[toPath[^1]] = value;
        }
    }

    private static void ApplyCopy(Dictionary<string, ThriftDynamic> target, JsonPatchOperation operation)
    {
        if (string.IsNullOrEmpty(operation.From))
            throw new ArgumentException("Copy operation requires 'from' field");

        var fromPath = ParsePath(operation.From);
        var toPath = ParsePath(operation.Path);

        ThriftDynamic value;
        try
        {
            value = NavigateToValue(target, fromPath);
        }
        catch (InvalidOperationException)
        {
            throw new InvalidOperationException($"Source path '{operation.From}' does not exist");
        }

        // Deep clone for safety
        var clonedValue = CloneThriftDynamic(value);

        if (toPath.Length == 1)
        {
            target[toPath[0]] = clonedValue;
        }
        else
        {
            var toParent = NavigateToParent(target, toPath);
            toParent?[toPath[^1]] = clonedValue;
        }
    }

    private static void ApplyTest(Dictionary<string, ThriftDynamic> target, JsonPatchOperation operation)
    {
        var path = ParsePath(operation.Path);
        var value = NavigateToValue(target, path);
        
        var expected = operation.Value;
        if (expected == null)
        {
            if (value.IsNull) return;
            throw new InvalidOperationException($"Test failed: path '{operation.Path}' has value '{value}' but expected null");
        }
        
        if (!value.Equals(expected))
        {
            throw new InvalidOperationException($"Test failed: path '{operation.Path}' has value '{value}' but expected '{expected}'");
        }
    }

    private static string[] ParsePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/")
            return Array.Empty<string>();

        // Remove leading slash and split
        return path.TrimStart('/').Split('/');
    }

    private static Dictionary<string, ThriftDynamic>? NavigateToParent(Dictionary<string, ThriftDynamic> root, string[] path)
    {
        if (path.Length <= 1) return root;

        var current = root;
        for (int i = 0; i < path.Length - 1; i++)
        {
            if (!current.TryGetValue(path[i], out var next) || next.Type != ThriftType.Struct)
                return null;

            current = next.Get<IReadOnlyDictionary<string, ThriftDynamic>>().ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        return current;
    }

private static ThriftDynamic NavigateToValue(Dictionary<string, ThriftDynamic> root, string[] path)
    {
        var current = root;
        
        for (int i = 0; i < path.Length; i++)
        {
            if (!current.TryGetValue(path[i], out var value))
                throw new InvalidOperationException($"Path '{string.Join(".", path)}' not found at segment '{path[i]}'");
            
            if (i == path.Length - 1)
                return value;
            
            if (value.Type != ThriftType.Struct)
                throw new InvalidOperationException($"Path segment '{path[i]}' is not a struct");
            
            current = value.Get<IReadOnlyDictionary<string, ThriftDynamic>>().ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }
        
        throw new InvalidOperationException("Empty path");
    }

    private static ThriftDynamic CloneThriftDynamic(ThriftDynamic value)
    {
        return value.Type.Kind switch
        {
            ThriftTypeKind.Struct => new ThriftDynamic(
                value.Get<IReadOnlyDictionary<string, ThriftDynamic>>()
                    .ToDictionary(kvp => kvp.Key, kvp => CloneThriftDynamic(kvp.Value))),
            ThriftTypeKind.List => new ThriftDynamic(
                value.Get<IReadOnlyList<ThriftDynamic>>()
                    .Select(CloneThriftDynamic).ToList()),
            ThriftTypeKind.Set => new ThriftDynamic(
                value.Get<IReadOnlySet<ThriftDynamic>>()
                    .Select(CloneThriftDynamic).ToHashSet()),
            ThriftTypeKind.Map => new ThriftDynamic(
                value.Get<IReadOnlyDictionary<ThriftDynamic, ThriftDynamic>>()
                    .ToDictionary(kvp => CloneThriftDynamic(kvp.Key), kvp => CloneThriftDynamic(kvp.Value))),
            _ => value // Primitives are immutable
        };
    }

    private static ThriftDynamic CloneThriftDynamicNullable(ThriftDynamic? value)
    {
        if (value == null) return new ThriftDynamic();
        return CloneThriftDynamic((ThriftDynamic)value);
    }

    private static ThriftDynamic ToDynamic(IThriftSerializable value)
    {
        // Use reflection to convert to dynamic
        var dict = new Dictionary<string, ThriftDynamic>();
        var props = value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
        
        foreach (var prop in props)
        {
            if (prop.CanRead)
            {
                var propValue = prop.GetValue(value);
                dict[prop.Name] = ConvertToDynamic(propValue);
            }
        }

        return new ThriftDynamic(dict);
    }

    private static T FromDynamic<T>(ThriftDynamic dynamic) where T : class, IThriftSerializable, new()
    {
        var result = new T();
        var dict = dynamic.Get<IReadOnlyDictionary<string, ThriftDynamic>>();
        var props = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        
        foreach (var prop in props)
        {
            if (prop.CanWrite && dict.TryGetValue(prop.Name, out var value))
            {
                prop.SetValue(result, ConvertFromDynamic(value, prop.PropertyType));
            }
        }

        return result;
    }

    private static ThriftDynamic ConvertToDynamic(object? value)
    {
        return value switch
        {
            null => new ThriftDynamic(),
            bool b => new ThriftDynamic(b),
            byte b => new ThriftDynamic(b),
            short s => new ThriftDynamic(s),
            int i => new ThriftDynamic(i),
            long l => new ThriftDynamic(l),
            float f => new ThriftDynamic(f),
            double d => new ThriftDynamic(d),
            string s => new ThriftDynamic(s),
            byte[] b => new ThriftDynamic(b),
            IEnumerable<object> e => new ThriftDynamic(e.Cast<object>().Select(ConvertToDynamic).ToList()),
            _ => new ThriftDynamic() // Fallback
        };
    }

    private static object? ConvertFromDynamic(ThriftDynamic value, Type targetType)
    {
        if (value.IsNull) return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;

        return value.Type.Kind switch
        {
            ThriftTypeKind.Bool => value.Get<bool>(),
            ThriftTypeKind.Byte => value.Get<byte>(),
            ThriftTypeKind.I16 => value.Get<short>(),
            ThriftTypeKind.I32 => value.Get<int>(),
            ThriftTypeKind.I64 => value.Get<long>(),
            ThriftTypeKind.Float => value.Get<float>(),
            ThriftTypeKind.Double => value.Get<double>(),
            ThriftTypeKind.String => value.Get<string>(),
            ThriftTypeKind.Binary => value.Get<byte[]>(),
            ThriftTypeKind.List => ConvertList(value.Get<IReadOnlyList<ThriftDynamic>>(), targetType),
            ThriftTypeKind.Set => ConvertSet(value.Get<IReadOnlySet<ThriftDynamic>>(), targetType),
            ThriftTypeKind.Map => ConvertMap(value.Get<IReadOnlyDictionary<ThriftDynamic, ThriftDynamic>>(), targetType),
            ThriftTypeKind.Struct => ConvertStruct(value.Get<IReadOnlyDictionary<string, ThriftDynamic>>(), targetType),
            _ => null
        };
    }

    private static object ConvertList(IReadOnlyList<ThriftDynamic> list, Type targetType)
    {
        var elementType = targetType.GetGenericArguments()[0];
        var resultType = typeof(List<>).MakeGenericType(elementType);
        var result = Activator.CreateInstance(resultType);
        var addMethod = resultType.GetMethod("Add");

        foreach (var item in list)
        {
            addMethod!.Invoke(result, new[] { ConvertFromDynamic(item, elementType) });
        }

        return result!;
    }

    private static object ConvertSet(IReadOnlySet<ThriftDynamic> set, Type targetType)
    {
        var elementType = targetType.GetGenericArguments()[0];
        var resultType = typeof(HashSet<>).MakeGenericType(elementType);
        var result = Activator.CreateInstance(resultType);
        var addMethod = resultType.GetMethod("Add");

        foreach (var item in set)
        {
            addMethod!.Invoke(result, new[] { ConvertFromDynamic(item, elementType) });
        }

        return result!;
    }

    private static object ConvertMap(IReadOnlyDictionary<ThriftDynamic, ThriftDynamic> map, Type targetType)
    {
        var keyType = targetType.GetGenericArguments()[0];
        var valueType = targetType.GetGenericArguments()[1];
        var resultType = typeof(Dictionary<,>).MakeGenericType(keyType, valueType);
        var result = Activator.CreateInstance(resultType);
        var addMethod = resultType.GetMethod("Add");

        foreach (var kvp in map)
        {
            addMethod!.Invoke(result, new[] { 
                ConvertFromDynamic(kvp.Key, keyType), 
                ConvertFromDynamic(kvp.Value, valueType) 
            });
        }

        return result!;
    }

    private static object ConvertStruct(IReadOnlyDictionary<string, ThriftDynamic> dict, Type targetType)
    {
        var result = Activator.CreateInstance(targetType);
        var props = targetType.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var prop in props)
        {
            if (prop.CanWrite && dict.TryGetValue(prop.Name, out var value))
            {
                prop.SetValue(result, ConvertFromDynamic(value, prop.PropertyType));
            }
        }

        return result!;
    }
}

/// <summary>
/// JSON Patch document (RFC 6902).
/// </summary>
public sealed class JsonPatchDocument
{
    public List<JsonPatchOperation> Operations { get; } = new();

    public JsonPatchDocument Add(string path, ThriftDynamic? value = null)
    {
        Operations.Add(new JsonPatchOperation { Op = "add", Path = path, Value = value });
        return this;
    }

    public JsonPatchDocument Remove(string path)
    {
        Operations.Add(new JsonPatchOperation { Op = "remove", Path = path });
        return this;
    }

    public JsonPatchDocument Replace(string path, ThriftDynamic? value = null)
    {
        Operations.Add(new JsonPatchOperation { Op = "replace", Path = path, Value = value });
        return this;
    }

    public JsonPatchDocument Move(string fromPath, string toPath)
    {
        Operations.Add(new JsonPatchOperation { Op = "move", Path = toPath, From = fromPath });
        return this;
    }

    public JsonPatchDocument Copy(string fromPath, string toPath)
    {
        Operations.Add(new JsonPatchOperation { Op = "copy", Path = toPath, From = fromPath });
        return this;
    }

    public JsonPatchDocument Test(string path, ThriftDynamic value)
    {
        Operations.Add(new JsonPatchOperation { Op = "test", Path = path, Value = value });
        return this;
    }

    public static JsonPatchDocument FromJson(string json)
    {
        return JsonSerializer.Deserialize<JsonPatchDocument>(json) ?? new JsonPatchDocument();
    }

    public string ToJson()
    {
        return JsonSerializer.Serialize(this);
    }
}

/// <summary>
/// A single JSON Patch operation.
/// </summary>
public sealed class JsonPatchOperation
{
    [JsonPropertyName("op")]
    public string Op { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("from")]
    public string? From { get; set; }

    [JsonPropertyName("value")]
    public ThriftDynamic? Value { get; set; }
}