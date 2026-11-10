// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Core.Attributes;

using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

/// <summary>
/// Attribute to reserve field IDs so they cannot be reused.
/// Applied to Thrift structs/exceptions/unions.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface, AllowMultiple = true, Inherited = false)]
public sealed class ThriftReserveIdsAttribute : Attribute
{
    /// <summary>
    /// The reserved field IDs.
    /// </summary>
    public IReadOnlyList<int> Ids { get; }

    /// <summary>
    /// Creates a new ReserveIds attribute.
    /// </summary>
    public ThriftReserveIdsAttribute(params int[] ids)
    {
        Ids = ids ?? Array.Empty<int>();
    }

    /// <summary>
    /// Creates a new ReserveIds attribute with a range.
    /// </summary>
    public ThriftReserveIdsAttribute(int start, int end)
    {
        Ids = Enumerable.Range(start, end - start + 1).ToArray();
    }
}

/// <summary>
/// Attribute to mark a field as @thrift.TerseWrite.
/// If a terse field equals its intrinsic default value, it will be skipped during serialization.
/// This differs from optional (which serializes if explicitly set) and unqualified (always serializes).
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class ThriftTerseWriteAttribute : Attribute
{
    /// <summary>
    /// Whether this terse field should be treated as required for validation purposes.
    /// </summary>
    public bool ValidateRequired { get; set; } = false;
}

/// <summary>
/// Attribute to mark a field as @thrift.Fill.
/// A fill field is always serialized, even if it has the intrinsic default value.
/// This prevents the field from being omitted and ensures the struct is never "empty".
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class ThriftFillAttribute : Attribute
{
}

/// <summary>
/// Attribute to mark a field as @thrift.Optional.
/// Optional fields are only serialized if explicitly set.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class ThriftOptionalAttribute : Attribute
{
}

/// <summary>
/// Attribute to mark a field as @thrift.Required.
/// Required fields must be set; behavior varies by language.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class ThriftRequiredAttribute : Attribute
{
}

/// <summary>
/// Attribute to mark a field as @thrift.Mixin.
/// The fields of the mixin type are exposed directly on the parent type.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class ThriftMixinAttribute : Attribute
{
}

/// <summary>
/// Attribute to specify the universal name (URI) for a Thrift definition.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface | AttributeTargets.Enum, AllowMultiple = false, Inherited = false)]
public sealed class UniversalNameAttribute : Attribute
{
    public string UniversalName { get; }

    public UniversalNameAttribute(string universalName)
    {
        UniversalName = universalName ?? throw new ArgumentNullException(nameof(universalName));
    }
}

/// <summary>
/// Attribute to specify the universal name for a Thrift service method.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class UniversalMethodNameAttribute : Attribute
{
    public string UniversalName { get; }

    public UniversalMethodNameAttribute(string universalName)
    {
        UniversalName = universalName ?? throw new ArgumentNullException(nameof(universalName));
    }
}

/// <summary>
/// Attribute to specify the universal name for a Thrift struct field.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class UniversalFieldNameAttribute : Attribute
{
    public string UniversalName { get; }

    public UniversalFieldNameAttribute(string universalName)
    {
        UniversalName = universalName ?? throw new ArgumentNullException(nameof(universalName));
    }
}

/// <summary>
/// Attribute to mark an exception field as the exception message.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class ThriftExceptionMessageAttribute : Attribute
{
}

/// <summary>
/// Attribute to mark a field with a custom default value that is allowed on optional fields.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class ThriftAllowUnsafeOptionalCustomDefaultValueAttribute : Attribute
{
}

/// <summary>
/// Thrift field attribute with full metadata.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class ThriftFieldAttribute : Attribute
{
    public short Id { get; }
    public string? Name { get; set; }
    public TType Type { get; set; } = TType.Stop;
    public bool Required { get; set; }
    public bool Optional { get; set; }
    public bool Terse { get; set; }
    public bool Fill { get; set; }

    public ThriftFieldAttribute(short id)
    {
        Id = id;
    }
}

/// <summary>
/// Runtime support for Thrift field qualifiers (TerseWrite, Fill, etc.).
/// </summary>
public static class ThriftFieldQualifierSupport
{
    /// <summary>
    /// Checks if a field should be serialized based on its qualifier and value.
    /// </summary>
    public static bool ShouldSerialize(object? value, MemberInfo member)
    {
        var terseAttr = member.GetCustomAttribute<ThriftTerseWriteAttribute>();
        var fillAttr = member.GetCustomAttribute<ThriftFillAttribute>();
        var optionalAttr = member.GetCustomAttribute<ThriftOptionalAttribute>();

        // Fill fields are always serialized
        if (fillAttr != null) return true;

        // Optional fields: serialize only if set (non-null for reference types)
        if (optionalAttr != null)
        {
            return value != null;
        }

        // Terse fields: skip if intrinsic default
        if (terseAttr != null)
        {
            return !IsIntrinsicDefault(value, GetFieldType(member));
        }

        // Unqualified fields: always serialize
        return true;
    }

    /// <summary>
    /// Checks if a value is the intrinsic default for its type.
    /// </summary>
    public static bool IsIntrinsicDefault(object? value, Type type)
    {
        if (value == null) return !type.IsValueType;

        return type switch
        {
            Type t when t == typeof(bool) => value.Equals(false),
            Type t when t == typeof(byte) => value.Equals((byte)0),
            Type t when t == typeof(short) => value.Equals((short)0),
            Type t when t == typeof(int) => value.Equals(0),
            Type t when t == typeof(long) => value.Equals(0L),
            Type t when t == typeof(float) => value.Equals(0f),
            Type t when t == typeof(double) => value.Equals(0.0),
            Type t when t == typeof(string) => value.Equals(string.Empty),
            Type t when t == typeof(byte[]) => value is byte[] arr && arr.Length == 0,
            Type t when t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>) =>
                value is ICollection<object> col && col.Count == 0,
            Type t when t.IsGenericType && t.GetGenericTypeDefinition() == typeof(HashSet<>) =>
                value is ICollection<object> col2 && col2.Count == 0,
            Type t when t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>) =>
                value is ICollection<object> col3 && col3.Count == 0,
            _ => value.Equals(Activator.CreateInstance(type))
        };
    }

    /// <summary>
    /// Gets the intrinsic default value for a type.
    /// </summary>
    public static object? GetIntrinsicDefault(Type type)
    {
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }

    private static Type GetFieldType(MemberInfo member)
    {
        return member switch
        {
            FieldInfo field => field.FieldType,
            PropertyInfo prop => prop.PropertyType,
            _ => typeof(object)
        };
    }

    /// <summary>
    /// Validates that required fields are set.
    /// </summary>
    public static void ValidateRequiredFields(object obj)
    {
        if (obj == null) throw new ArgumentNullException(nameof(obj));

        var type = obj.GetType();
        var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        foreach (var field in fields)
        {
            var requiredAttr = field.GetCustomAttribute<ThriftRequiredAttribute>();
            if (requiredAttr != null)
            {
                var value = field.GetValue(obj);
                if (value == null || (field.FieldType.IsValueType && value.Equals(Activator.CreateInstance(field.FieldType))))
                {
                    throw new InvalidOperationException($"Required field '{field.Name}' is not set");
                }
            }
        }

        foreach (var prop in props)
        {
            var requiredAttr = prop.GetCustomAttribute<ThriftRequiredAttribute>();
            if (requiredAttr != null && prop.CanRead)
            {
                var value = prop.GetValue(obj);
                if (value == null || (prop.PropertyType.IsValueType && value.Equals(Activator.CreateInstance(prop.PropertyType))))
                {
                    throw new InvalidOperationException($"Required property '{prop.Name}' is not set");
                }
            }
        }
    }

    /// <summary>
    /// Ensures terse fields with intrinsic defaults are cleared (set to default).
    /// </summary>
    public static void ClearTerseDefaults(object obj)
    {
        if (obj == null) return;

        var type = obj.GetType();
        var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        foreach (var field in fields)
        {
            var terseAttr = field.GetCustomAttribute<ThriftTerseWriteAttribute>();
            if (terseAttr != null)
            {
                var value = field.GetValue(obj);
                if (IsIntrinsicDefault(value, field.FieldType))
                {
                    field.SetValue(obj, GetIntrinsicDefault(field.FieldType));
                }
            }
        }

        foreach (var prop in props)
        {
            var terseAttr = prop.GetCustomAttribute<ThriftTerseWriteAttribute>();
            if (terseAttr != null && prop.CanWrite)
            {
                var value = prop.GetValue(obj);
                if (IsIntrinsicDefault(value, prop.PropertyType))
                {
                    prop.SetValue(obj, GetIntrinsicDefault(prop.PropertyType));
                }
            }
        }
    }
}