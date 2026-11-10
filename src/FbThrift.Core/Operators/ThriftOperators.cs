// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Core.Operators;

using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using FbThrift.Core.Attributes;
using FbThrift.Core.Dynamic;
using FbThrift.Core.Hashing;
using FbThrift.Core.Mask;
using FbThrift.Core.Memory;
using FbThrift.Core.Protocol;
using FBThrift;

/// <summary>
/// Deterministic Thrift operators for value comparison, hashing, and manipulation.
/// Based on fbthrift's operators.md specification.
/// </summary>
public static class ThriftOperators
{
    #region Primitive Operators

    /// <summary>
    /// Checks if two primitive values are identical.
    /// NaN == NaN, -0.0 != 0.0
    /// </summary>
    public static bool Identical<T>(T left, T right)
    {
        if (typeof(T) == typeof(float))
        {
            float l = (float)(object)left;
            float r = (float)(object)right;
            return float.IsNaN(l) && float.IsNaN(r) || l == r && (l != 0f || (l == 0f && 1f / l > 0) == (r == 0f && 1f / r > 0));
        }
        if (typeof(T) == typeof(double))
        {
            double l = (double)(object)left;
            double r = (double)(object)right;
            return double.IsNaN(l) && double.IsNaN(r) || l == r && (l != 0d || (l == 0d && 1d / l > 0) == (r == 0d && 1d / r > 0));
        }
        return EqualityComparer<T>.Default.Equals(left, right);
    }

    /// <summary>
    /// Checks if two primitive values are equal.
    /// NaN != NaN, -0.0 == 0.0, cross-type numeric equality
    /// </summary>
    public static bool Equal<TLeft, TRight>(TLeft left, TRight right)
    {
        // Cross-type numeric equality
        if (IsNumeric(typeof(TLeft)) && IsNumeric(typeof(TRight)))
        {
            return Convert.ToDecimal(left) == Convert.ToDecimal(right);
        }

        if (typeof(TLeft) == typeof(float) && typeof(TRight) == typeof(float))
        {
            return (float)(object)left == (float)(object)right;
        }
        if (typeof(TLeft) == typeof(double) && typeof(TRight) == typeof(double))
        {
            return (double)(object)left == (double)(object)right;
        }

        return EqualityComparer<TLeft>.Default.Equals(left, (TLeft)(object)right);
    }

    /// <summary>
    /// Checks if a primitive value is empty (intrinsic default).
    /// </summary>
    public static bool Empty<T>(T value)
    {
        return EqualityComparer<T>.Default.Equals(value, default);
    }

    /// <summary>
    /// Clears a primitive value to its intrinsic default.
    /// </summary>
    public static void Clear<T>(ref T value)
    {
        value = default;
    }

    /// <summary>
    /// Computes a deterministic hash for a primitive value.
    /// </summary>
    public static ulong Hash<T>(T value)
    {
        if (typeof(T) == typeof(float))
        {
            return Hash(BitConverter.GetBytes((float)(object)value));
        }
        if (typeof(T) == typeof(double))
        {
            return Hash(BitConverter.GetBytes((double)(object)value));
        }
        if (typeof(T) == typeof(string))
        {
            return Hash(System.Text.Encoding.UTF8.GetBytes((string)(object)value));
        }
        // For other value types, use reflection to get size
        var size = System.Runtime.InteropServices.Marshal.SizeOf(value);
        var bytes = new byte[size];
        System.Buffer.BlockCopy(new[] { value }, 0, bytes, 0, size);
        return Hash(bytes);
    }

    private static ulong Hash(ReadOnlySpan<byte> bytes) => XxHash3.HashToUInt64(bytes);

    private static bool IsNumeric(Type type)
    {
        return type.IsPrimitive && type != typeof(bool) && type != typeof(char);
    }

    #endregion

    #region Container Operators

    /// <summary>
    /// Checks if two lists are identical (same order, pairwise identical).
    /// </summary>
    public static bool Identical<T>(IReadOnlyList<T> left, IReadOnlyList<T> right)
    {
        if (left.Count != right.Count) return false;
        for (int i = 0; i < left.Count; i++)
        {
            if (!Identical(left[i], right[i])) return false;
        }
        return true;
    }

    /// <summary>
    /// Checks if two lists are equal (same order, pairwise equal).
    /// </summary>
    public static bool Equal<T>(IReadOnlyList<T> left, IReadOnlyList<T> right)
    {
        if (left.Count != right.Count) return false;
        for (int i = 0; i < left.Count; i++)
        {
            if (!Equal(left[i], right[i])) return false;
        }
        return true;
    }

    /// <summary>
    /// Checks if two sets are identical (pairwise identical, order independent).
    /// </summary>
    public static bool Identical<T>(IReadOnlySet<T> left, IReadOnlySet<T> right)
    {
        if (left.Count != right.Count) return false;
        foreach (var item in left)
        {
            if (!right.Contains(item)) return false;
        }
        return true;
    }

    /// <summary>
    /// Checks if two sets are equal (pairwise equal, order independent).
    /// </summary>
    public static bool Equal<T>(IReadOnlySet<T> left, IReadOnlySet<T> right)
    {
        if (left.Count != right.Count) return false;
        foreach (var item in left)
        {
            bool found = false;
            foreach (var other in right)
            {
                if (Equal(item, other))
                {
                    found = true;
                    break;
                }
            }
            if (!found) return false;
        }
        return true;
    }

    /// <summary>
    /// Checks if two maps are identical (pairwise identical keys and values).
    /// </summary>
    public static bool Identical<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> left, IReadOnlyDictionary<TKey, TValue> right)
    {
        if (left.Count != right.Count) return false;
        foreach (var kvp in left)
        {
            if (!right.TryGetValue(kvp.Key, out var value) || !Identical(kvp.Value, value)) return false;
        }
        return true;
    }

    /// <summary>
    /// Checks if two maps are equal (pairwise equal keys and values).
    /// </summary>
    public static bool Equal<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> left, IReadOnlyDictionary<TKey, TValue> right)
    {
        if (left.Count != right.Count) return false;
        foreach (var kvp in left)
        {
            if (!right.TryGetValue(kvp.Key, out var value) || !Equal(kvp.Value, value)) return false;
        }
        return true;
    }

    /// <summary>
    /// Checks if a container is empty.
    /// </summary>
    public static bool Empty<T>(IReadOnlyCollection<T> container) => container.Count == 0;

    /// <summary>
    /// Clears a container.
    /// </summary>
    public static void Clear<T>(ICollection<T> container) => container.Clear();

    /// <summary>
    /// Computes deterministic hash for a list (ordered accumulation).
    /// </summary>
    public static ulong Hash<T>(IReadOnlyList<T> list)
    {
        using var stream = RecyclableStreamManager.GetStream();
        foreach (var item in list)
        {
            WriteHashable(stream, item);
        }
        return Hash(stream.ToArray());
    }

    /// <summary>
    /// Computes deterministic hash for a set (unordered accumulation).
    /// </summary>
    public static ulong Hash<T>(IReadOnlySet<T> set)
    {
        var hashes = new ulong[set.Count];
        int i = 0;
        foreach (var item in set)
        {
            hashes[i++] = Hash(item);
        }
        Array.Sort(hashes);
        return HashCombined(hashes);
    }

    /// <summary>
    /// Computes deterministic hash for a map (unordered accumulation of key-value pairs).
    /// </summary>
    public static ulong Hash<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> map)
    {
        var hashes = new ulong[map.Count];
        int i = 0;
        foreach (var kvp in map)
        {
            ulong keyHash = Hash(kvp.Key);
            ulong valueHash = Hash(kvp.Value);
            hashes[i++] = keyHash ^ (valueHash << 1);
        }
        Array.Sort(hashes);
        return HashCombined(hashes);
    }

    private static ulong HashCombined(ulong[] hashes)
    {
        ulong result = 0x9e3779b97f4a7c15; // Golden ratio
        foreach (var h in hashes)
        {
            result ^= h + 0x9e3779b97f4a7c15 + (result << 6) + (result >> 2);
        }
        return result;
    }

    #endregion

    #region Structured Operators (Struct/Union/Exception)

    /// <summary>
    /// Checks if two Thrift structs are identical (pairwise identical field id/value pairs).
    /// </summary>
    public static bool IdenticalStruct<T>(T left, T right) where T : class, IThriftSerializable
    {
        if (ReferenceEquals(left, right)) return true;
        if (left == null || right == null) return false;

        var type = typeof(T);
        var fields = GetThriftFields(type);

        foreach (var field in fields)
        {
            var leftValue = GetFieldValue(field, left);
            var rightValue = GetFieldValue(field, right);

            if (!ValuesIdentical(leftValue, rightValue, GetFieldType(field)))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Checks if two Thrift structs are equal (pairwise equal field id/value pairs).
    /// </summary>
    public static bool EqualStruct<T>(T left, T right) where T : class, IThriftSerializable
    {
        if (ReferenceEquals(left, right)) return true;
        if (left == null || right == null) return false;

        var type = typeof(T);
        var fields = GetThriftFields(type);

        foreach (var field in fields)
        {
            var leftValue = GetFieldValue(field, left);
            var rightValue = GetFieldValue(field, right);

            if (!ValuesEqual(leftValue, rightValue, GetFieldType(field)))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Checks if a Thrift struct is empty (all fields empty).
    /// A field is empty if:
    /// - Optional/Union field and value is unset (null)
    /// - Terse field and value is intrinsic default
    /// </summary>
    public static bool EmptyStruct<T>(T value) where T : class, IThriftSerializable
    {
        if (value == null) return true;

        var type = typeof(T);
        var fields = GetThriftFields(type);

        foreach (var field in fields)
        {
            var attr = field.GetCustomAttribute<ThriftFieldAttribute>();
            var fieldValue = GetFieldValue(field, value);

            if (attr != null)
            {
                if (attr.Optional && fieldValue == null) continue;
                if (attr.Terse && IsIntrinsicDefault(fieldValue, GetFieldType(field))) continue;
            }

            if (!IsIntrinsicDefault(fieldValue, GetFieldType(field)))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Clears a Thrift struct (sets all fields to intrinsic default).
    /// </summary>
    public static void ClearStruct<T>(T value) where T : class, IThriftSerializable
    {
        if (value == null) return;

        var type = typeof(T);
        var fields = GetThriftFields(type);

        foreach (var field in fields)
        {
            var defaultValue = GetFieldType(field).IsValueType ? Activator.CreateInstance(GetFieldType(field)) : null;
            SetFieldValue(field, value, defaultValue);
        }
    }

    /// <summary>
    /// Computes deterministic hash for a Thrift struct (unordered accumulation of non-empty field hashes).
    /// </summary>
    public static ulong HashStruct<T>(T value) where T : class, IThriftSerializable
    {
        if (value == null) return 0;

        var type = typeof(T);
        var fields = GetThriftFields(type);
        var hashes = new List<ulong>();

        foreach (var field in fields)
        {
            var fieldValue = GetFieldValue(field, value);
            var attr = field.GetCustomAttribute<ThriftFieldAttribute>();

            // Skip empty fields
            if (attr != null)
            {
                if (attr.Optional && fieldValue == null) continue;
                if (attr.Terse && IsIntrinsicDefault(fieldValue, GetFieldType(field))) continue;
            }

            if (!IsIntrinsicDefault(fieldValue, GetFieldType(field)))
            {
                var fieldHash = Hash<object>(fieldValue);
                // Combine field ID with value hash
                var idHash = Hash(attr?.Id ?? (short)0);
                hashes.Add(idHash ^ (fieldHash << 1));
            }
        }

        hashes.Sort();
        return HashCombined(hashes.ToArray());
    }

    #endregion

    #region Helper Methods

    private static IReadOnlyList<MemberInfo> GetThriftFields(Type type)
    {
        var fields = new List<MemberInfo>();
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (field.GetCustomAttribute<ThriftFieldAttribute>() != null)
                fields.Add(field);
        }
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (prop.GetCustomAttribute<ThriftFieldAttribute>() != null && prop.CanRead && prop.CanWrite)
                fields.Add(prop);
        }
        return fields;
    }

    private static object? GetFieldValue(MemberInfo member, object obj)
    {
        return member switch
        {
            FieldInfo f => f.GetValue(obj),
            PropertyInfo p => p.GetValue(obj),
            _ => null
        };
    }

    private static void SetFieldValue(MemberInfo member, object obj, object? value)
    {
        switch (member)
        {
            case FieldInfo f: f.SetValue(obj, value); break;
            case PropertyInfo p: p.SetValue(obj, value); break;
        }
    }

    private static Type GetFieldType(MemberInfo member)
    {
        return member switch
        {
            FieldInfo f => f.FieldType,
            PropertyInfo p => p.PropertyType,
            _ => throw new InvalidOperationException($"Unexpected member type: {member.GetType()}")
        };
    }

    private static bool ValuesIdentical(object? left, object? right, Type type)
    {
        if (left == null && right == null) return true;
        if (left == null || right == null) return false;

        if (type.IsPrimitive)
        {
            return IdenticalPrimitive(left, right, type);
        }
        if (type == typeof(string))
        {
            return (string)left == (string)right;
        }
if (typeof(IThriftSerializable).IsAssignableFrom(type))
            {
                return IdenticalStruct((IThriftSerializable)left, (IThriftSerializable)right);
            }
        if (typeof(IEnumerable).IsAssignableFrom(type))
        {
            return IdenticalEnumerable((IEnumerable)left, (IEnumerable)right);
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            return IdenticalDictionary((IDictionary)left, (IDictionary)right);
        }

        return left.Equals(right);
    }

    private static bool ValuesEqual(object? left, object? right, Type type)
    {
        if (left == null && right == null) return true;
        if (left == null || right == null) return false;

        if (type.IsPrimitive)
        {
            return EqualPrimitive(left, right, type);
        }
        if (type == typeof(string))
        {
            return (string)left == (string)right;
        }
if (typeof(IThriftSerializable).IsAssignableFrom(type))
            {
                return EqualStruct((IThriftSerializable)left, (IThriftSerializable)right);
            }
        if (typeof(IEnumerable).IsAssignableFrom(type))
        {
            return EqualEnumerable((IEnumerable)left, (IEnumerable)right);
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            return EqualDictionary((IDictionary)left, (IDictionary)right);
        }

        return left.Equals(right);
    }

    private static bool IdenticalPrimitive(object left, object right, Type type)
    {
        return type switch
        {
            Type t when t == typeof(float) => Identical((float)left, (float)right),
            Type t when t == typeof(double) => Identical((double)left, (double)right),
            _ => left.Equals(right)
        };
    }

    private static bool EqualPrimitive(object left, object right, Type type)
    {
        return type switch
        {
            Type t when t == typeof(float) => Equal((float)left, (float)right),
            Type t when t == typeof(double) => Equal((double)left, (double)right),
            _ => left.Equals(right)
        };
    }

    private static bool IdenticalEnumerable(IEnumerable left, IEnumerable right)
    {
        var leftList = left.Cast<object>().ToList();
        var rightList = right.Cast<object>().ToList();
        if (leftList.Count != rightList.Count) return false;
        for (int i = 0; i < leftList.Count; i++)
        {
            if (!ValuesIdentical(leftList[i], rightList[i], leftList[i]?.GetType() ?? typeof(object))) return false;
        }
        return true;
    }

    private static bool EqualEnumerable(IEnumerable left, IEnumerable right)
    {
        var leftList = left.Cast<object>().ToList();
        var rightList = right.Cast<object>().ToList();
        if (leftList.Count != rightList.Count) return false;
        for (int i = 0; i < leftList.Count; i++)
        {
            if (!ValuesEqual(leftList[i], rightList[i], leftList[i]?.GetType() ?? typeof(object))) return false;
        }
        return true;
    }

    private static bool IdenticalDictionary(IDictionary left, IDictionary right)
    {
        if (left.Count != right.Count) return false;
        foreach (var key in left.Keys)
        {
            if (!right.Contains(key)) return false;
            if (!ValuesIdentical(left[key], right[key], left[key]?.GetType() ?? typeof(object))) return false;
        }
        return true;
    }

    private static bool EqualDictionary(IDictionary left, IDictionary right)
    {
        if (left.Count != right.Count) return false;
        foreach (var key in left.Keys)
        {
            if (!right.Contains(key)) return false;
            if (!ValuesEqual(left[key], right[key], left[key]?.GetType() ?? typeof(object))) return false;
        }
        return true;
    }

    private static bool IsIntrinsicDefault(object? value, Type type)
    {
        if (value == null) return !type.IsValueType;
        if (type.IsPrimitive) return value.Equals(Activator.CreateInstance(type));
        if (type == typeof(string)) return value.Equals(string.Empty);
        return value.Equals(Activator.CreateInstance(type));
    }

    private static void WriteHashable(MemoryStream stream, object? value)
    {
        if (value == null) return;

        var type = value.GetType();
        if (type.IsPrimitive)
        {
            var bytes = GetBytes(value);
            stream.Write(bytes);
        }
        else if (type == typeof(string))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes((string)value);
            stream.Write(BitConverter.GetBytes(bytes.Length));
            stream.Write(bytes);
        }
        else if (typeof(IThriftSerializable).IsAssignableFrom(type))
        {
            using var thriftStream = RecyclableStreamManager.GetStream();
            var writer = new ThriftCompactWriter(thriftStream);
            ((IThriftSerializable)value).__fbthrift_write(writer);
            var bytes = thriftStream.ToArray();
            stream.Write(BitConverter.GetBytes(bytes.Length));
            stream.Write(bytes);
        }
    }

    private static byte[] GetBytes(object value)
    {
        return value switch
        {
            bool b => BitConverter.GetBytes(b),
            byte b => new[] { b },
            short s => BitConverter.GetBytes(s),
            int i => BitConverter.GetBytes(i),
            long l => BitConverter.GetBytes(l),
            float f => BitConverter.GetBytes(f),
            double d => BitConverter.GetBytes(d),
            _ => Array.Empty<byte>()
        };
    }

    #endregion
}

/// <summary>
/// Extension methods for Thrift operators.
/// </summary>
public static class ThriftOperatorExtensions
{
    /// <summary>
    /// Checks if this value is identical to another.
    /// </summary>
    public static bool IdenticalTo<T>(this T left, T right) where T : unmanaged
        => ThriftOperators.Identical(left, right);

    /// <summary>
    /// Checks if this value is equal to another.
    /// </summary>
    public static bool EqualTo<TLeft, TRight>(this TLeft left, TRight right)
        where TLeft : unmanaged
        where TRight : unmanaged
        => ThriftOperators.Equal(left, right);

    /// <summary>
    /// Checks if this value is empty (intrinsic default).
    /// </summary>
    public static bool IsEmpty<T>(this T value) where T : unmanaged
        => ThriftOperators.Empty(value);

    /// <summary>
    /// Clears this value to its intrinsic default.
    /// </summary>
    public static void ClearPrimitive<T>(ref this T value) where T : unmanaged
        => ThriftOperators.Clear(ref value);

    /// <summary>
    /// Computes a deterministic hash for this value.
    /// </summary>
    public static ulong DeterministicHashPrimitive<T>(this T value) where T : unmanaged
        => ThriftOperators.Hash(value);

    /// <summary>
    /// Checks if two structs are identical.
    /// </summary>
    public static bool IdenticalToStruct<T>(this T left, T right) where T : class, IThriftSerializable
        => ThriftOperators.IdenticalStruct(left, right);

    /// <summary>
    /// Checks if two structs are equal.
    /// </summary>
    public static bool EqualToStruct<T>(this T left, T right) where T : class, IThriftSerializable
        => ThriftOperators.EqualStruct(left, right);

    /// <summary>
    /// Checks if a struct is empty.
    /// </summary>
    public static bool IsEmptyStruct<T>(this T value) where T : class, IThriftSerializable
        => ThriftOperators.EmptyStruct(value);

    /// <summary>
    /// Clears a struct (sets all fields to intrinsic default).
    /// </summary>
    public static void ClearStruct<T>(this T value) where T : class, IThriftSerializable
        => ThriftOperators.ClearStruct(value);

    /// <summary>
    /// Computes a deterministic hash for a struct.
    /// </summary>
    public static ulong DeterministicHashStruct<T>(this T value) where T : class, IThriftSerializable
        => ThriftOperators.HashStruct(value);
}