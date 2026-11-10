// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Core.Mixins;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

/// <summary>
/// Attribute to mark a field as a Mixin.
/// When applied, the fields of the mixin type are exposed directly on the parent type.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class ThriftMixinAttribute : Attribute
{
}

/// <summary>
/// Mixin metadata for code generation and runtime reflection.
/// </summary>
public sealed class MixinInfo
{
    public MemberInfo Member { get; }
    public Type MixinType { get; }
    public IReadOnlyList<MemberInfo> ExposedMembers { get; }

    public MixinInfo(MemberInfo member, Type mixinType, IReadOnlyList<MemberInfo> exposedMembers)
    {
        Member = member ?? throw new ArgumentNullException(nameof(member));
        MixinType = mixinType ?? throw new ArgumentNullException(nameof(mixinType));
        ExposedMembers = exposedMembers ?? throw new ArgumentNullException(nameof(exposedMembers));
    }
}

/// <summary>
/// Registry for mixin information on types.
/// </summary>
public static class MixinRegistry
{
    private static readonly Dictionary<Type, IReadOnlyList<MixinInfo>> _typeMixins = new();
    private static readonly object _lock = new();

    /// <summary>
    /// Gets all mixins for a type (including inherited).
    /// </summary>
    public static IReadOnlyList<MixinInfo> GetMixins(Type type)
    {
        lock (_lock)
        {
            if (_typeMixins.TryGetValue(type, out var mixins))
            {
                return mixins;
            }

            var result = DiscoverMixins(type);
            _typeMixins[type] = result;
            return result;
        }
    }

    /// <summary>
    /// Checks if a type has any mixins.
    /// </summary>
    public static bool HasMixins(Type type)
    {
        return GetMixins(type).Count > 0;
    }

    /// <summary>
    /// Gets all exposed member names from mixins (for duplicate detection).
    /// </summary>
    public static IReadOnlySet<string> GetExposedMemberNames(Type type)
    {
        var names = new HashSet<string>();
        foreach (var mixin in GetMixins(type))
        {
            foreach (var member in mixin.ExposedMembers)
            {
                names.Add(member.Name);
            }
        }
        return names;
    }

    private static List<MixinInfo> DiscoverMixins(Type type)
    {
        var mixins = new List<MixinInfo>();

        // Check fields
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (field.GetCustomAttribute<ThriftMixinAttribute>() != null)
            {
                var mixinType = field.FieldType;
                var exposed = GetExposedMembers(mixinType);
                mixins.Add(new MixinInfo(field, mixinType, exposed));
            }
        }

        // Check properties
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (prop.GetCustomAttribute<ThriftMixinAttribute>() != null)
            {
                var mixinType = prop.PropertyType;
                var exposed = GetExposedMembers(mixinType);
                mixins.Add(new MixinInfo(prop, mixinType, exposed));
            }
        }

        return mixins;
    }

    private static IReadOnlyList<MemberInfo> GetExposedMembers(Type mixinType)
    {
        var members = new List<MemberInfo>();

        // Expose public fields
        foreach (var field in mixinType.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            members.Add(field);
        }

        // Expose public properties with getters
        foreach (var prop in mixinType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.CanRead)
            {
                members.Add(prop);
            }
        }

        return members;
    }
}

/// <summary>
/// Extension methods for working with mixins.
/// </summary>
public static class MixinExtensions
{
    /// <summary>
    /// Gets a mixin member value by name (searches mixins).
    /// </summary>
    public static object? GetMixinMember(this object obj, string memberName)
    {
        if (obj == null) return null;

        var type = obj.GetType();
        foreach (var mixin in MixinRegistry.GetMixins(type))
        {
            var member = mixin.ExposedMembers.FirstOrDefault(m => m.Name == memberName);
            if (member != null)
            {
                var mixinInstance = mixin.Member is FieldInfo field
                    ? field.GetValue(obj)
                    : ((PropertyInfo)mixin.Member).GetValue(obj);

                if (mixinInstance != null)
                {
                    return member is FieldInfo f ? f.GetValue(mixinInstance) : ((PropertyInfo)member).GetValue(mixinInstance);
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Sets a mixin member value by name (searches mixins).
    /// </summary>
    public static bool SetMixinMember(this object obj, string memberName, object? value)
    {
        if (obj == null) return false;

        var type = obj.GetType();
        foreach (var mixin in MixinRegistry.GetMixins(type))
        {
            var member = mixin.ExposedMembers.FirstOrDefault(m => m.Name == memberName);
            if (member != null)
            {
                var mixinInstance = mixin.Member is FieldInfo field
                    ? field.GetValue(obj)
                    : ((PropertyInfo)mixin.Member).GetValue(obj);

                if (mixinInstance != null)
                {
                    if (member is FieldInfo f)
                    {
                        f.SetValue(mixinInstance, value);
                    }
                    else
                    {
                        ((PropertyInfo)member).SetValue(mixinInstance, value);
                    }
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Tries to get a mixin member by name.
    /// </summary>
    public static bool TryGetMixinMember(this object obj, string memberName, out object? value)
    {
        value = GetMixinMember(obj, memberName);
        return value != null;
    }
}

/// <summary>
/// Mixin validation for code generation.
/// </summary>
public static class MixinValidator
{
    /// <summary>
    /// Validates that a type's mixins don't have duplicate member names.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when duplicate member names are found.</exception>
    public static void ValidateNoDuplicates(Type type)
    {
        var seen = new Dictionary<string, MemberInfo>();
        foreach (var mixin in MixinRegistry.GetMixins(type))
        {
            foreach (var member in mixin.ExposedMembers)
            {
                if (seen.TryGetValue(member.Name, out var existing))
                {
                    throw new InvalidOperationException(
                        $"Duplicate mixin member name '{member.Name}' found in type {type.Name}. " +
                        $"Conflicts: {existing.DeclaringType?.Name}.{existing.Name} vs {member.DeclaringType?.Name}.{member.Name}");
                }
                seen[member.Name] = member;
            }
        }
    }
}