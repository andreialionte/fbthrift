// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Core.UniversalNames;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Universal Names for Thrift - a URI-based naming scheme for Thrift entities.
/// Based on fbthrift's Universal Names specification.
/// Provides globally unique, resolvable names for Thrift services, structs, fields, etc.
/// </summary>
public static class UniversalNames
{
    /// <summary>
    /// The URI scheme for Universal Names.
    /// </summary>
    public const string Scheme = "thrift";

    /// <summary>
    /// Regular expression for validating Universal Names.
    /// </summary>
    private static readonly Regex UniversalNameRegex = new(
        @"^thrift://([^/]+)/([^/]+)(?:/([^/]+))?(?:/([^/]+))?$",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture);

    /// <summary>
    /// Parses a Universal Name string into its components.
    /// </summary>
    /// <param name="universalName">The Universal Name string (e.g., "thrift://namespace/ServiceName/methodName").</param>
    /// <returns>A tuple of (namespace, service/struct name, member name).</returns>
    /// <exception cref="ArgumentException">Thrown if the Universal Name is invalid.</exception>
    public static (string Namespace, string TypeName, string? MemberName) Parse(string universalName)
    {
        ArgumentNullException.ThrowIfNull(universalName);

        var match = UniversalNameRegex.Match(universalName);
        if (!match.Success)
        {
            throw new ArgumentException($"Invalid Universal Name format: {universalName}", nameof(universalName));
        }

        string ns = match.Groups[1].Value;
        string typeName = match.Groups[2].Value;
        string? memberName = match.Groups[3].Success ? match.Groups[3].Value : null;

        return (ns, typeName, memberName);
    }

    /// <summary>
    /// Creates a Universal Name for a service.
    /// </summary>
    public static string ForService(string @namespace, string serviceName)
    {
        ValidateNamespace(@namespace);
        ValidateTypeName(serviceName);
        return $"{Scheme}://{@namespace}/{serviceName}";
    }

    /// <summary>
    /// Creates a Universal Name for a service method.
    /// </summary>
    public static string ForMethod(string @namespace, string serviceName, string methodName)
    {
        ValidateNamespace(@namespace);
        ValidateTypeName(serviceName);
        ValidateMemberName(methodName);
        return $"{Scheme}://{@namespace}/{serviceName}/{methodName}";
    }

    /// <summary>
    /// Creates a Universal Name for a struct.
    /// </summary>
    public static string ForStruct(string @namespace, string structName)
    {
        ValidateNamespace(@namespace);
        ValidateTypeName(structName);
        return $"{Scheme}://{@namespace}/{structName}";
    }

    /// <summary>
    /// Creates a Universal Name for a struct field.
    /// </summary>
    public static string ForField(string @namespace, string structName, string fieldName)
    {
        ValidateNamespace(@namespace);
        ValidateTypeName(structName);
        ValidateMemberName(fieldName);
        return $"{Scheme}://{@namespace}/{structName}/{fieldName}";
    }

    /// <summary>
    /// Creates a Universal Name for an enum.
    /// </summary>
    public static string ForEnum(string @namespace, string enumName)
    {
        ValidateNamespace(@namespace);
        ValidateTypeName(enumName);
        return $"{Scheme}://{@namespace}/{enumName}";
    }

    /// <summary>
    /// Creates a Universal Name for an enum value.
    /// </summary>
    public static string ForEnumValue(string @namespace, string enumName, string valueName)
    {
        ValidateNamespace(@namespace);
        ValidateTypeName(enumName);
        ValidateMemberName(valueName);
        return $"{Scheme}://{@namespace}/{enumName}/{valueName}";
    }

    /// <summary>
    /// Creates a Universal Name for a typedef.
    /// </summary>
    public static string ForTypedef(string @namespace, string typedefName)
    {
        ValidateNamespace(@namespace);
        ValidateTypeName(typedefName);
        return $"{Scheme}://{@namespace}/{typedefName}";
    }

    /// <summary>
    /// Creates a Universal Name for an exception.
    /// </summary>
    public static string ForException(string @namespace, string exceptionName)
    {
        ValidateNamespace(@namespace);
        ValidateTypeName(exceptionName);
        return $"{Scheme}://{@namespace}/{exceptionName}";
    }

    /// <summary>
    /// Creates a Universal Name for a union.
    /// </summary>
    public static string ForUnion(string @namespace, string unionName)
    {
        ValidateNamespace(@namespace);
        ValidateTypeName(unionName);
        return $"{Scheme}://{@namespace}/{unionName}";
    }

    /// <summary>
    /// Creates a Universal Name for a union field.
    /// </summary>
    public static string ForUnionField(string @namespace, string unionName, string fieldName)
    {
        ValidateNamespace(@namespace);
        ValidateTypeName(unionName);
        ValidateMemberName(fieldName);
        return $"{Scheme}://{@namespace}/{unionName}/{fieldName}";
    }

    /// <summary>
    /// Creates a Universal Name for a constant.
    /// </summary>
    public static string ForConstant(string @namespace, string constantName)
    {
        ValidateNamespace(@namespace);
        ValidateMemberName(constantName);
        return $"{Scheme}://{@namespace}/{constantName}";
    }

    /// <summary>
    /// Creates a Universal Name for a service with version.
    /// </summary>
    public static string ForServiceVersion(string @namespace, string serviceName, string version)
    {
        ValidateNamespace(@namespace);
        ValidateTypeName(serviceName);
        ValidateVersion(version);
        return $"{Scheme}://{@namespace}/{serviceName}@{version}";
    }

    /// <summary>
    /// Resolves a Universal Name to a .NET Type.
    /// </summary>
    public static Type? ResolveType(string universalName, Assembly? assembly = null)
    {
        var (ns, typeName, memberName) = Parse(universalName);
        
        // Try to find the type in the current or specified assembly
        var assemblies = assembly != null ? new[] { assembly } : AppDomain.CurrentDomain.GetAssemblies();
        
        foreach (var asm in assemblies)
        {
            // Try exact match first
            var type = asm.GetType($"{ns}.{typeName}");
            if (type != null) return type;

            // Try with common namespace patterns
            type = asm.GetType($"{ns}.{typeName}, {asm.GetName().Name}");
            if (type != null) return type;
        }

        return null;
    }

    /// <summary>
    /// Gets all Universal Names for types in an assembly.
    /// </summary>
    public static IEnumerable<string> GetUniversalNames(Assembly assembly, string defaultNamespace = "")
    {
        foreach (var type in assembly.GetTypes())
        {
            if (type.IsDefined(typeof(UniversalNameAttribute), false))
            {
                var attr = type.GetCustomAttribute<UniversalNameAttribute>()!;
                yield return attr.UniversalName;
            }
            else if (!string.IsNullOrEmpty(defaultNamespace))
            {
                yield return ForStruct(defaultNamespace, type.Name);
            }
        }
    }

    private static void ValidateNamespace(string @namespace)
    {
        if (string.IsNullOrWhiteSpace(@namespace))
            throw new ArgumentException("Namespace cannot be empty", nameof(@namespace));

        if (!Regex.IsMatch(@namespace, @"^[a-zA-Z][a-zA-Z0-9_.-]*$"))
            throw new ArgumentException($"Invalid namespace format: {@namespace}", nameof(@namespace));
    }

    private static void ValidateTypeName(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            throw new ArgumentException("Type name cannot be empty", nameof(typeName));

        if (!Regex.IsMatch(typeName, @"^[A-Za-z_][A-Za-z0-9_]*$"))
            throw new ArgumentException($"Invalid type name format: {typeName}", nameof(typeName));
    }

    private static void ValidateMemberName(string memberName)
    {
        if (string.IsNullOrWhiteSpace(memberName))
            throw new ArgumentException("Member name cannot be empty", nameof(memberName));

        if (!Regex.IsMatch(memberName, @"^[A-Za-z_][A-Za-z0-9_]*$"))
            throw new ArgumentException($"Invalid member name format: {memberName}", nameof(memberName));
    }

    private static void ValidateVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("Version cannot be empty", nameof(version));

        if (!Regex.IsMatch(version, @"^v?\d+(\.\d+)*(-[a-zA-Z0-9]+)?$"))
            throw new ArgumentException($"Invalid version format: {version}", nameof(version));
    }
}

/// <summary>
/// Attribute for marking a type with its Universal Name.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface | AttributeTargets.Enum, AllowMultiple = false, Inherited = false)]
public sealed class UniversalNameAttribute : Attribute
{
    public string UniversalName { get; }

    public UniversalNameAttribute(string universalName)
    {
        UniversalName = universalName ?? throw new ArgumentNullException(nameof(universalName));
        
        // Validate format
        var _ = UniversalNames.Parse(universalName);
    }
}

/// <summary>
/// Attribute for marking a service method with its Universal Name.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class UniversalMethodNameAttribute : Attribute
{
    public string UniversalName { get; }

    public UniversalMethodNameAttribute(string universalName)
    {
        UniversalName = universalName ?? throw new ArgumentNullException(nameof(universalName));
        var _ = UniversalNames.Parse(universalName);
    }
}

/// <summary>
/// Attribute for marking a struct field with its Universal Name.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class UniversalFieldNameAttribute : Attribute
{
    public string UniversalName { get; }

    public UniversalFieldNameAttribute(string universalName)
    {
        UniversalName = universalName ?? throw new ArgumentNullException(nameof(universalName));
        var _ = UniversalNames.Parse(universalName);
    }
}

/// <summary>
/// Extension methods for working with Universal Names on types.
/// </summary>
public static class UniversalNameExtensions
{
    /// <summary>
    /// Gets the Universal Name for a service type.
    /// </summary>
    public static string GetUniversalName(this Type serviceType)
    {
        var attr = serviceType.GetCustomAttribute<UniversalNameAttribute>();
        if (attr != null) return attr.UniversalName;

        // Infer from type name
        var ns = serviceType.Namespace?.Replace('.', '/') ?? "unknown";
        return UniversalNames.ForService(ns, serviceType.Name);
    }

    /// <summary>
    /// Gets the Universal Name for a method.
    /// </summary>
    public static string GetUniversalName(this MethodInfo method)
    {
        var attr = method.GetCustomAttribute<UniversalMethodNameAttribute>();
        if (attr != null) return attr.UniversalName;

        var serviceAttr = method.DeclaringType?.GetCustomAttribute<UniversalNameAttribute>();
        if (serviceAttr != null)
        {
            var (ns, typeName, _) = UniversalNames.Parse(serviceAttr.UniversalName);
            return UniversalNames.ForMethod(ns, typeName, method.Name);
        }

        return UniversalNames.ForMethod("unknown", method.DeclaringType?.Name ?? "Unknown", method.Name);
    }

    /// <summary>
    /// Gets the Universal Name for a field/property.
    /// </summary>
    public static string GetUniversalName(this MemberInfo member)
    {
        var attr = member.GetCustomAttribute<UniversalFieldNameAttribute>();
        if (attr != null) return attr.UniversalName;

        var typeAttr = member.DeclaringType?.GetCustomAttribute<UniversalNameAttribute>();
        if (typeAttr != null)
        {
            var (ns, typeName, _) = UniversalNames.Parse(typeAttr.UniversalName);
            return UniversalNames.ForField(ns, typeName, member.Name);
        }

        return UniversalNames.ForField("unknown", member.DeclaringType?.Name ?? "Unknown", member.Name);
    }
}

/// <summary>
/// Universal Name resolver for dynamic dispatch.
/// </summary>
public sealed class UniversalNameResolver
{
    private readonly Dictionary<string, Type> _typeRegistry = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MethodInfo> _methodRegistry = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a type by its Universal Name.
    /// </summary>
    public void RegisterType(string universalName, Type type)
    {
        ArgumentNullException.ThrowIfNull(universalName);
        ArgumentNullException.ThrowIfNull(type);
        _typeRegistry[universalName] = type;
    }

    /// <summary>
    /// Registers all types from an assembly.
    /// </summary>
    public void RegisterAssembly(Assembly assembly, string defaultNamespace = "")
    {
        foreach (var type in assembly.GetTypes())
        {
            var attr = type.GetCustomAttribute<UniversalNameAttribute>();
            if (attr != null)
            {
                RegisterType(attr.UniversalName, type);
            }
            else if (!string.IsNullOrEmpty(defaultNamespace))
            {
                var un = UniversalNames.ForStruct(defaultNamespace, type.Name);
                RegisterType(un, type);
            }
        }
    }

    /// <summary>
    /// Resolves a type by Universal Name.
    /// </summary>
    public Type? ResolveType(string universalName)
    {
        _typeRegistry.TryGetValue(universalName, out var type);
        return type;
    }

    /// <summary>
    /// Registers a method by its Universal Name.
    /// </summary>
    public void RegisterMethod(string universalName, MethodInfo method)
    {
        ArgumentNullException.ThrowIfNull(universalName);
        ArgumentNullException.ThrowIfNull(method);
        _methodRegistry[universalName] = method;
    }

    /// <summary>
    /// Resolves a method by Universal Name.
    /// </summary>
    public MethodInfo? ResolveMethod(string universalName)
    {
        _methodRegistry.TryGetValue(universalName, out var method);
        return method;
    }
}