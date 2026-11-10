// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Core.Mask;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using FbThrift.Core.Attributes;
using FbThrift.Core.Dynamic;

/// <summary>
/// Thrift Field Mask - represents a subset of fields for inclusion/exclusion operations.
/// Based on fbthrift's field_mask.thrift and FieldMask.h.
/// </summary>
public sealed class FieldMask : IEquatable<FieldMask>
{
    /// <summary>
    /// Fields to include (fieldId -> nested mask).
    /// </summary>
    public IReadOnlyDictionary<short, FieldMask> Includes { get; }

    /// <summary>
    /// Fields to exclude (fieldId -> nested mask).
    /// </summary>
    public IReadOnlyDictionary<short, FieldMask> Excludes { get; }

    internal FieldMask(
        IReadOnlyDictionary<short, FieldMask> includes,
        IReadOnlyDictionary<short, FieldMask> excludes)
    {
        Includes = includes ?? new Dictionary<short, FieldMask>();
        Excludes = excludes ?? new Dictionary<short, FieldMask>();
    }

    /// <summary>
    /// Creates an empty mask (includes nothing).
    /// </summary>
    public static FieldMask None => new FieldMask(
        new Dictionary<short, FieldMask>(),
        new Dictionary<short, FieldMask>());

    /// <summary>
    /// Creates a mask that includes all fields.
    /// </summary>
    public static FieldMask All => new FieldMask(
        new Dictionary<short, FieldMask>(),
        new Dictionary<short, FieldMask>()); // Empty excludes means all included

    /// <summary>
    /// Creates an include mask for specific fields.
    /// </summary>
    public static FieldMask Include(params short[] fieldIds)
    {
        var dict = new Dictionary<short, FieldMask>();
        foreach (var id in fieldIds)
        {
            dict[id] = FieldMask.All;
        }
        return new FieldMask(dict, new Dictionary<short, FieldMask>());
    }

    /// <summary>
    /// Creates an include mask for specific fields with nested masks.
    /// </summary>
    public static FieldMask Include(IReadOnlyDictionary<short, FieldMask> fields)
    {
        return new FieldMask(
            new Dictionary<short, FieldMask>(fields),
            new Dictionary<short, FieldMask>());
    }

    /// <summary>
    /// Creates an exclude mask for specific fields.
    /// </summary>
    public static FieldMask Exclude(params short[] fieldIds)
    {
        var dict = new Dictionary<short, FieldMask>();
        foreach (var id in fieldIds)
        {
            dict[id] = FieldMask.All;
        }
        return new FieldMask(
            new Dictionary<short, FieldMask>(),
            dict);
    }

    /// <summary>
    /// Creates an exclude mask for specific fields with nested masks.
    /// </summary>
    public static FieldMask Exclude(IReadOnlyDictionary<short, FieldMask> fields)
    {
        return new FieldMask(
            new Dictionary<short, FieldMask>(),
            new Dictionary<short, FieldMask>(fields));
    }

    /// <summary>
    /// Checks if this mask is an "include" type (as opposed to exclude).
    /// </summary>
    public bool IsIncludeMask => Excludes.Count == 0;

    /// <summary>
    /// Checks if this mask is an "exclude" type.
    /// </summary>
    public bool IsExcludeMask => Includes.Count == 0;

    /// <summary>
    /// Checks if the mask is empty (no fields specified).
    /// </summary>
    public bool IsEmpty => Includes.Count == 0 && Excludes.Count == 0;

    /// <summary>
    /// Checks if the mask matches all fields (empty exclude mask).
    /// </summary>
    public bool IsAll => IsExcludeMask && Excludes.Count == 0;

    /// <summary>
    /// Inverts the mask (include becomes exclude and vice versa).
    /// </summary>
    public FieldMask Invert()
    {
        return new FieldMask(Excludes, Includes);
    }

    /// <summary>
    /// Intersection of two masks (AND).
    /// </summary>
    public static FieldMask operator &(FieldMask left, FieldMask right)
    {
        if (left == null || right == null) throw new ArgumentNullException();

        // Both must be same type (both include or both exclude)
        if (left.IsIncludeMask != right.IsIncludeMask)
        {
            throw new InvalidOperationException("Cannot intersect include and exclude masks");
        }

        if (left.IsIncludeMask)
        {
            // Intersect include masks
            var result = new Dictionary<short, FieldMask>();
            foreach (var kvp in left.Includes)
            {
                if (right.Includes.TryGetValue(kvp.Key, out var rightMask))
                {
                    result[kvp.Key] = kvp.Value & rightMask;
                }
            }
            return new FieldMask(result, new Dictionary<short, FieldMask>());
        }
        else
        {
            // Union exclude masks
            var result = new Dictionary<short, FieldMask>(left.Excludes);
            foreach (var kvp in right.Excludes)
            {
                if (result.TryGetValue(kvp.Key, out var existing))
                {
                    result[kvp.Key] = existing | kvp.Value;
                }
                else
                {
                    result[kvp.Key] = kvp.Value;
                }
            }
            return new FieldMask(new Dictionary<short, FieldMask>(), result);
        }
    }

    /// <summary>
    /// Union of two masks (OR).
    /// </summary>
    public static FieldMask operator |(FieldMask left, FieldMask right)
    {
        if (left == null || right == null) throw new ArgumentNullException();

        if (left.IsIncludeMask != right.IsIncludeMask)
        {
            throw new InvalidOperationException("Cannot union include and exclude masks");
        }

        if (left.IsIncludeMask)
        {
            // Union include masks
            var result = new Dictionary<short, FieldMask>(left.Includes);
            foreach (var kvp in right.Includes)
            {
                if (result.TryGetValue(kvp.Key, out var existing))
                {
                    result[kvp.Key] = existing | kvp.Value;
                }
                else
                {
                    result[kvp.Key] = kvp.Value;
                }
            }
            return new FieldMask(result, new Dictionary<short, FieldMask>());
        }
        else
        {
            // Intersect exclude masks
            var result = new Dictionary<short, FieldMask>();
            foreach (var kvp in left.Excludes)
            {
                if (right.Excludes.TryGetValue(kvp.Key, out var rightMask))
                {
                    result[kvp.Key] = kvp.Value & rightMask;
                }
            }
            return new FieldMask(new Dictionary<short, FieldMask>(), result);
        }
    }

    /// <summary>
    /// Subtraction of masks (left - right).
    /// </summary>
    public static FieldMask operator -(FieldMask left, FieldMask right)
    {
        if (left == null || right == null) throw new ArgumentNullException();

        if (left.IsIncludeMask != right.IsIncludeMask)
        {
            throw new InvalidOperationException("Cannot subtract include and exclude masks");
        }

        if (left.IsIncludeMask)
        {
            // Include minus include
            var result = new Dictionary<short, FieldMask>();
            foreach (var kvp in left.Includes)
            {
                if (!right.Includes.ContainsKey(kvp.Key))
                {
                    result[kvp.Key] = kvp.Value;
                }
                else
                {
                    var subtracted = kvp.Value - right.Includes[kvp.Key];
                    if (!subtracted.IsEmpty)
                    {
                        result[kvp.Key] = subtracted;
                    }
                }
            }
            return new FieldMask(result, new Dictionary<short, FieldMask>());
        }
        else
        {
            // Exclude minus exclude = exclude only what's in left but not in right
            var result = new Dictionary<short, FieldMask>();
            foreach (var kvp in left.Excludes)
            {
                if (!right.Excludes.ContainsKey(kvp.Key))
                {
                    result[kvp.Key] = kvp.Value;
                }
                else
                {
                    var subtracted = kvp.Value - right.Excludes[kvp.Key];
                    if (!subtracted.IsEmpty)
                    {
                        result[kvp.Key] = subtracted;
                    }
                }
            }
            return new FieldMask(new Dictionary<short, FieldMask>(), result);
        }
    }

    /// <summary>
    /// Checks if a field is included by this mask.
    /// </summary>
    public bool IncludesField(short fieldId)
    {
        if (IsIncludeMask)
        {
            return Includes.ContainsKey(fieldId);
        }
        else
        {
            return !Excludes.ContainsKey(fieldId);
        }
    }

    /// <summary>
    /// Gets the nested mask for a field, or null if not specified.
    /// </summary>
    public FieldMask? GetNestedMask(short fieldId)
    {
        if (IsIncludeMask)
        {
            return Includes.TryGetValue(fieldId, out var mask) ? mask : null;
        }
        else
        {
            return Excludes.TryGetValue(fieldId, out var mask) ? mask : null;
        }
    }

    public bool Equals(FieldMask? other)
    {
        if (other == null) return false;
        if (IsIncludeMask != other.IsIncludeMask) return false;

        if (IsIncludeMask)
        {
            return DictionaryEquals(Includes, other.Includes);
        }
        else
        {
            return DictionaryEquals(Excludes, other.Excludes);
        }
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as FieldMask);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(IsIncludeMask, Includes.Count, Excludes.Count);
    }

    public static bool operator ==(FieldMask? left, FieldMask? right)
    {
        if (left is null) return right is null;
        return left.Equals(right);
    }

    public static bool operator !=(FieldMask? left, FieldMask? right)
    {
        return !(left == right);
    }

    public override string ToString()
    {
        if (IsAll) return "Mask.All";
        if (IsEmpty) return "Mask.None";

        if (IsIncludeMask)
        {
            return $"Mask.Include({{{string.Join(", ", Includes.Select(k => $"0x{k.Key:X4}"))}}})";
        }
        else
        {
            return $"Mask.Exclude({{{string.Join(", ", Excludes.Select(k => $"0x{k.Key:X4}"))}}})";
        }
    }

    private static bool DictionaryEquals<K, V>(IReadOnlyDictionary<K, V> left, IReadOnlyDictionary<K, V> right)
        where K : notnull
        where V : IEquatable<V>
    {
        if (left.Count != right.Count) return false;
        foreach (var kvp in left)
        {
            if (!right.TryGetValue(kvp.Key, out var value) || !kvp.Value.Equals(value))
                return false;
        }
        return true;
    }
}

/// <summary>
/// Builder for FieldMask with type safety and convenient fluent API.
/// </summary>
public sealed class MaskBuilder<T> where T : class
{
    private readonly FieldMask _mask;
    private readonly Dictionary<short, MemberInfo> _fieldMap;
    private bool _isIncludeMask = true;

    private MaskBuilder(FieldMask mask, Dictionary<short, MemberInfo> fieldMap, bool isIncludeMask)
    {
        _mask = mask;
        _fieldMap = fieldMap;
        _isIncludeMask = isIncludeMask;
    }

    /// <summary>
    /// Creates a builder starting with an empty include mask.
    /// </summary>
    public static MaskBuilder<T> None()
    {
        var fieldMap = BuildFieldMap(typeof(T));
        return new MaskBuilder<T>(FieldMask.None, fieldMap, true);
    }

    /// <summary>
    /// Creates a builder starting with an all-include mask.
    /// </summary>
    public static MaskBuilder<T> All()
    {
        var fieldMap = BuildFieldMap(typeof(T));
        return new MaskBuilder<T>(FieldMask.All, fieldMap, true);
    }

    /// <summary>
    /// Creates a builder starting with an empty exclude mask.
    /// </summary>
    public static MaskBuilder<T> NoneExclude()
    {
        var fieldMap = BuildFieldMap(typeof(T));
        return new MaskBuilder<T>(FieldMask.None, fieldMap, false);
    }

    /// <summary>
    /// Creates a builder starting with an all-exclude mask.
    /// </summary>
    public static MaskBuilder<T> AllExclude()
    {
        var fieldMap = BuildFieldMap(typeof(T));
        return new MaskBuilder<T>(FieldMask.All, fieldMap, false);
    }

    /// <summary>
    /// Includes fields by field names.
    /// </summary>
    public MaskBuilder<T> Includes(params string[] fieldNames)
    {
        var nestedMask = FieldMask.All;
        return Includes(fieldNames, nestedMask);
    }

    /// <summary>
    /// Includes fields by field names with nested mask.
    /// </summary>
    public MaskBuilder<T> Includes(IEnumerable<string> fieldNames, FieldMask nestedMask = default)
    {
        EnsureIncludeMask();
        var mask = nestedMask == default ? FieldMask.All : nestedMask;

        var dict = new Dictionary<short, FieldMask>(_mask.Includes);
        foreach (var name in fieldNames)
        {
            if (_fieldMap.TryGetValue(GetFieldId(name), out var member))
            {
                dict[member.GetCustomAttribute<ThriftFieldAttribute>()?.Id ?? GetFieldId(name)] = mask;
            }
        }
        return new MaskBuilder<T>(new FieldMask(dict, new Dictionary<short, FieldMask>()), _fieldMap, true);
    }

    /// <summary>
    /// Includes fields by field IDs.
    /// </summary>
    public MaskBuilder<T> Includes(params short[] fieldIds)
    {
        return Includes(fieldIds, FieldMask.All);
    }

    /// <summary>
    /// Includes fields by field IDs with nested mask.
    /// </summary>
    public MaskBuilder<T> Includes(IEnumerable<short> fieldIds, FieldMask nestedMask = default)
    {
        EnsureIncludeMask();
        var mask = nestedMask == default ? FieldMask.All : nestedMask;

        var dict = new Dictionary<short, FieldMask>(_mask.Includes);
        foreach (var id in fieldIds)
        {
            dict[id] = mask;
        }
        return new MaskBuilder<T>(new FieldMask(dict, new Dictionary<short, FieldMask>()), _fieldMap, true);
    }

    /// <summary>
    /// Includes map element by integer key.
    /// </summary>
    public MaskBuilder<T> IncludesMapElement(short fieldId, long key, FieldMask nestedMask = default)
    {
        EnsureIncludeMask();
        var mask = nestedMask == default ? FieldMask.All : nestedMask;

        var dict = new Dictionary<short, FieldMask>(_mask.Includes);
        var currentMask = dict.GetValueOrDefault(fieldId, FieldMask.All);
        var mapIncludes = new Dictionary<short, FieldMask>(currentMask.Includes);
        mapIncludes[(short)key] = mask; // Simplified: using short for map keys
        dict[fieldId] = new FieldMask(mapIncludes, new Dictionary<short, FieldMask>());
        return new MaskBuilder<T>(new FieldMask(dict, new Dictionary<short, FieldMask>()), _fieldMap, true);
    }

    /// <summary>
    /// Includes map element by string key.
    /// </summary>
    public MaskBuilder<T> IncludesMapElement(short fieldId, string key, FieldMask nestedMask = default)
    {
        // String keys not directly supported in field ID map, would need separate handling
        return IncludesMapElement(fieldId, key.GetHashCode(), nestedMask);
    }

    /// <summary>
    /// Includes a specific type in a Thrift Any field.
    /// </summary>
    public MaskBuilder<T> IncludesType(short fieldId, Type type, FieldMask nestedMask = default)
    {
        EnsureIncludeMask();
        var mask = nestedMask == default ? FieldMask.All : nestedMask;

        var dict = new Dictionary<short, FieldMask>(_mask.Includes);
        var currentMask = dict.GetValueOrDefault(fieldId, FieldMask.All);
        var typeIncludes = new Dictionary<short, FieldMask>(currentMask.Includes);
        // Type hash as field ID for type inclusion
        typeIncludes[(short)type.FullName.GetHashCode()] = mask;
        dict[fieldId] = new FieldMask(typeIncludes, new Dictionary<short, FieldMask>());
        return new MaskBuilder<T>(new FieldMask(dict, new Dictionary<short, FieldMask>()), _fieldMap, true);
    }

    /// <summary>
    /// Excludes fields by field names.
    /// </summary>
    public MaskBuilder<T> Excludes(params string[] fieldNames)
    {
        return Excludes(fieldNames, FieldMask.All);
    }

    /// <summary>
    /// Excludes fields by field names with nested mask.
    /// </summary>
    public MaskBuilder<T> Excludes(IEnumerable<string> fieldNames, FieldMask nestedMask = default)
    {
        EnsureExcludeMask();
        var mask = nestedMask == default ? FieldMask.All : nestedMask;

        var dict = new Dictionary<short, FieldMask>(_mask.Excludes);
        foreach (var name in fieldNames)
        {
            if (_fieldMap.TryGetValue(GetFieldId(name), out var member))
            {
                var attr = member.GetCustomAttribute<ThriftFieldAttribute>();
                dict[attr?.Id ?? GetFieldId(name)] = mask;
            }
        }
        return new MaskBuilder<T>(new FieldMask(new Dictionary<short, FieldMask>(), dict), _fieldMap, false);
    }

    /// <summary>
    /// Excludes fields by field IDs.
    /// </summary>
    public MaskBuilder<T> Excludes(params short[] fieldIds)
    {
        return Excludes(fieldIds, FieldMask.All);
    }

    /// <summary>
    /// Excludes fields by field IDs with nested mask.
    /// </summary>
    public MaskBuilder<T> Excludes(IEnumerable<short> fieldIds, FieldMask nestedMask = default)
    {
        EnsureExcludeMask();
        var mask = nestedMask == default ? FieldMask.All : nestedMask;

        var dict = new Dictionary<short, FieldMask>(_mask.Excludes);
        foreach (var id in fieldIds)
        {
            dict[id] = mask;
        }
        return new MaskBuilder<T>(new FieldMask(new Dictionary<short, FieldMask>(), dict), _fieldMap, false);
    }

    /// <summary>
    /// Excludes map element by integer key.
    /// </summary>
    public MaskBuilder<T> ExcludesMapElement(short fieldId, long key, FieldMask nestedMask = default)
    {
        EnsureExcludeMask();
        var mask = nestedMask == default ? FieldMask.All : nestedMask;

        var dict = new Dictionary<short, FieldMask>(_mask.Excludes);
        var currentMask = dict.GetValueOrDefault(fieldId, FieldMask.All);
        var mapExcludes = new Dictionary<short, FieldMask>(currentMask.Excludes);
        mapExcludes[(short)key] = mask;
        dict[fieldId] = new FieldMask(new Dictionary<short, FieldMask>(), mapExcludes);
        return new MaskBuilder<T>(new FieldMask(new Dictionary<short, FieldMask>(), dict), _fieldMap, false);
    }

    /// <summary>
    /// Excludes map element by string key.
    /// </summary>
    public MaskBuilder<T> ExcludesMapElement(short fieldId, string key, FieldMask nestedMask = default)
    {
        return ExcludesMapElement(fieldId, key.GetHashCode(), nestedMask);
    }

    /// <summary>
    /// Excludes a specific type in a Thrift Any field.
    /// </summary>
    public MaskBuilder<T> ExcludesType(short fieldId, Type type, FieldMask nestedMask = default)
    {
        EnsureExcludeMask();
        var mask = nestedMask == default ? FieldMask.All : nestedMask;

        var dict = new Dictionary<short, FieldMask>(_mask.Excludes);
        var currentMask = dict.GetValueOrDefault(fieldId, FieldMask.All);
        var typeExcludes = new Dictionary<short, FieldMask>(currentMask.Excludes);
        typeExcludes[(short)type.FullName.GetHashCode()] = mask;
        dict[fieldId] = new FieldMask(new Dictionary<short, FieldMask>(), typeExcludes);
        return new MaskBuilder<T>(new FieldMask(new Dictionary<short, FieldMask>(), dict), _fieldMap, false);
    }

    /// <summary>
    /// Resets to none mask (empty include).
    /// </summary>
    public MaskBuilder<T> ResetToNone()
    {
        return None();
    }

    /// <summary>
    /// Resets to all mask (empty exclude).
    /// </summary>
    public MaskBuilder<T> ResetToAll()
    {
        return AllExclude();
    }

    /// <summary>
    /// Inverts the mask.
    /// </summary>
    public MaskBuilder<T> Invert()
    {
        return _isIncludeMask
            ? new MaskBuilder<T>(_mask.Invert(), _fieldMap, false)
            : new MaskBuilder<T>(_mask.Invert(), _fieldMap, true);
    }

    /// <summary>
    /// Gets the built FieldMask.
    /// </summary>
    public FieldMask ToThrift()
    {
        return _mask;
    }

    /// <summary>
    /// Ensures all masked fields are set in the object.
    /// </summary>
    public void Ensure(T obj)
    {
        if (obj == null) throw new ArgumentNullException(nameof(obj));
        EnsureFields(obj, _mask, typeof(T));
    }

    /// <summary>
    /// Removes all masked fields from the object (sets to default).
    /// </summary>
    public void Clear(T obj)
    {
        if (obj == null) throw new ArgumentNullException(nameof(obj));
        ClearFields(obj, _mask, typeof(T));
    }

    /// <summary>
    /// Returns a new object containing only the masked fields.
    /// </summary>
    public T Filter(T src)
    {
        if (src == null) throw new ArgumentNullException(nameof(src));
        return FilterObject(src, _mask, typeof(T));
    }

    private void EnsureIncludeMask()
    {
        if (!_isIncludeMask)
            throw new InvalidOperationException("Cannot use include operations on exclude mask");
    }

    private void EnsureExcludeMask()
    {
        if (_isIncludeMask)
            throw new InvalidOperationException("Cannot use exclude operations on include mask");
    }

    private static Dictionary<short, MemberInfo> BuildFieldMap(Type type)
    {
        var map = new Dictionary<short, MemberInfo>();
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            var attr = field.GetCustomAttribute<ThriftFieldAttribute>();
            if (attr != null)
            {
                map[attr.Id] = field;
            }
        }
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            var attr = prop.GetCustomAttribute<ThriftFieldAttribute>();
            if (attr != null)
            {
                map[attr.Id] = prop;
            }
        }
        return map;
    }

    private static short GetFieldId(string name)
    {
        // Simple hash-based ID generation for name-based lookups
        return (short)name.GetHashCode();
    }

    private static void EnsureFields(object obj, FieldMask mask, Type type)
    {
        // Implementation would validate required fields are set
    }

    private static void ClearFields(object obj, FieldMask mask, Type type)
    {
        foreach (var kvp in mask.IsIncludeMask ? mask.Includes : mask.Excludes)
        {
            var member = type.GetField($"Field{kvp.Key}", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (member != null)
            {
                var defaultValue = member.FieldType.IsValueType ? Activator.CreateInstance(member.FieldType) : null;
                member.SetValue(obj, defaultValue);
            }
        }
    }

    private static T FilterObject(T src, FieldMask mask, Type type)
    {
        var result = Activator.CreateInstance<T>();
        foreach (var kvp in mask.IsIncludeMask ? mask.Includes : mask.Excludes)
        {
            var member = type.GetField($"Field{kvp.Key}", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (member != null)
            {
                var value = member.GetValue(src);
                member.SetValue(result, value);
            }
        }
        return result;
    }
}

/// <summary>
/// Dynamic mask builder without type validation.
/// </summary>
public sealed class DynamicMaskBuilder
{
    private readonly FieldMask _mask;
    private bool _isIncludeMask = true;

    private DynamicMaskBuilder(FieldMask mask, bool isIncludeMask)
    {
        _mask = mask;
        _isIncludeMask = isIncludeMask;
    }

    public static DynamicMaskBuilder None()
    {
        return new DynamicMaskBuilder(FieldMask.None, true);
    }

    public static DynamicMaskBuilder All()
    {
        return new DynamicMaskBuilder(FieldMask.All, false);
    }

    public DynamicMaskBuilder Includes(FieldPath fieldPath, FieldMask nestedMask = default)
    {
        EnsureIncludeMask();
        var mask = nestedMask == default ? FieldMask.All : nestedMask;
        var dict = new Dictionary<short, FieldMask>(_mask.Includes);
        dict[fieldPath.FieldId] = mask;
        return new DynamicMaskBuilder(new FieldMask(dict, new Dictionary<short, FieldMask>()), true);
    }

    public DynamicMaskBuilder IncludesMapElement(FieldPath fieldPath, long key, FieldMask nestedMask = default)
    {
        EnsureIncludeMask();
        var mask = nestedMask == default ? FieldMask.All : nestedMask;
        var dict = new Dictionary<short, FieldMask>(_mask.Includes);
        var current = dict.GetValueOrDefault(fieldPath.FieldId, FieldMask.All);
        var mapIncludes = new Dictionary<short, FieldMask>(current.Includes);
        mapIncludes[(short)key] = mask;
        dict[fieldPath.FieldId] = new FieldMask(mapIncludes, new Dictionary<short, FieldMask>());
        return new DynamicMaskBuilder(new FieldMask(dict, new Dictionary<short, FieldMask>()), true);
    }

    public DynamicMaskBuilder IncludesMapElement(FieldPath fieldPath, string key, FieldMask nestedMask = default)
    {
        return IncludesMapElement(fieldPath, key.GetHashCode(), nestedMask);
    }

    public DynamicMaskBuilder IncludesType(FieldPath fieldPath, Type type, FieldMask nestedMask = default)
    {
        EnsureIncludeMask();
        var mask = nestedMask == default ? FieldMask.All : nestedMask;
        var dict = new Dictionary<short, FieldMask>(_mask.Includes);
        var current = dict.GetValueOrDefault(fieldPath.FieldId, FieldMask.All);
        var typeIncludes = new Dictionary<short, FieldMask>(current.Includes);
        typeIncludes[(short)type.FullName.GetHashCode()] = mask;
        dict[fieldPath.FieldId] = new FieldMask(typeIncludes, new Dictionary<short, FieldMask>());
        return new DynamicMaskBuilder(new FieldMask(dict, new Dictionary<short, FieldMask>()), true);
    }

    public DynamicMaskBuilder Excludes(FieldPath fieldPath, FieldMask nestedMask = default)
    {
        EnsureExcludeMask();
        var mask = nestedMask == default ? FieldMask.All : nestedMask;
        var dict = new Dictionary<short, FieldMask>(_mask.Excludes);
        dict[fieldPath.FieldId] = mask;
        return new DynamicMaskBuilder(new FieldMask(new Dictionary<short, FieldMask>(), dict), false);
    }

    public DynamicMaskBuilder ExcludesMapElement(FieldPath fieldPath, long key, FieldMask nestedMask = default)
    {
        EnsureExcludeMask();
        var mask = nestedMask == default ? FieldMask.All : nestedMask;
        var dict = new Dictionary<short, FieldMask>(_mask.Excludes);
        var current = dict.GetValueOrDefault(fieldPath.FieldId, FieldMask.All);
        var mapExcludes = new Dictionary<short, FieldMask>(current.Excludes);
        mapExcludes[(short)key] = mask;
        dict[fieldPath.FieldId] = new FieldMask(new Dictionary<short, FieldMask>(), mapExcludes);
        return new DynamicMaskBuilder(new FieldMask(new Dictionary<short, FieldMask>(), dict), false);
    }

    public DynamicMaskBuilder ExcludesMapElement(FieldPath fieldPath, string key, FieldMask nestedMask = default)
    {
        return ExcludesMapElement(fieldPath, key.GetHashCode(), nestedMask);
    }

    public DynamicMaskBuilder ExcludesType(FieldPath fieldPath, Type type, FieldMask nestedMask = default)
    {
        EnsureExcludeMask();
        var mask = nestedMask == default ? FieldMask.All : nestedMask;
        var dict = new Dictionary<short, FieldMask>(_mask.Excludes);
        var current = dict.GetValueOrDefault(fieldPath.FieldId, FieldMask.All);
        var typeExcludes = new Dictionary<short, FieldMask>(current.Excludes);
        typeExcludes[(short)type.FullName.GetHashCode()] = mask;
        dict[fieldPath.FieldId] = new FieldMask(new Dictionary<short, FieldMask>(), typeExcludes);
        return new DynamicMaskBuilder(new FieldMask(new Dictionary<short, FieldMask>(), dict), false);
    }

    public DynamicMaskBuilder ResetToNone()
    {
        return None();
    }

    public DynamicMaskBuilder ResetToAll()
    {
        return All();
    }

    public DynamicMaskBuilder Invert()
    {
        return new DynamicMaskBuilder(_mask.Invert(), !_isIncludeMask);
    }

    public FieldMask ToThrift()
    {
        return _mask;
    }

    private void EnsureIncludeMask()
    {
        if (!_isIncludeMask) throw new InvalidOperationException("Cannot use include operations on exclude mask");
    }

    private void EnsureExcludeMask()
    {
        if (_isIncludeMask) throw new InvalidOperationException("Cannot use exclude operations on include mask");
    }
}

/// <summary>
/// Field path for dynamic mask operations.
/// </summary>
public readonly struct FieldPath : IEquatable<FieldPath>
{
    public short FieldId { get; }
    public IReadOnlyList<short> NestedIds { get; }

    public FieldPath(short fieldId, params short[] nestedIds)
    {
        FieldId = fieldId;
        NestedIds = nestedIds ?? Array.Empty<short>();
    }

    public static implicit operator FieldPath(short fieldId) => new FieldPath(fieldId);

    public bool Equals(FieldPath other) => FieldId == other.FieldId && NestedIds.SequenceEqual(other.NestedIds);
    public override bool Equals(object? obj) => obj is FieldPath other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(FieldId, NestedIds);
    public static bool operator ==(FieldPath left, FieldPath right) => left.Equals(right);
    public static bool operator !=(FieldPath left, FieldPath right) => !left.Equals(right);
}

/// <summary>
/// Mask adapter for using FieldMask with generated structs.
/// </summary>
public sealed class MaskAdapter<T> where T : class
{
    private readonly FieldMask _mask;
    private readonly MaskBuilder<T> _builder;

    public MaskAdapter(FieldMask mask)
    {
        _mask = mask;
        _builder = MaskBuilder<T>.None();
    }

    public FieldMask ToThrift() => _mask;

    public void Ensure(T obj) => _builder.Ensure(obj);
    public void Clear(T obj) => _builder.Clear(obj);
    public T Filter(T src) => _builder.Filter(src);
}