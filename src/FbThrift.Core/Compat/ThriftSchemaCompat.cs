// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Core.Compat;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using FbThrift.Core.Attributes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

/// <summary>
/// Schema Compatibility Linter (FBTHRIFTCOMPAT) - Roslyn analyzer for Thrift schema changes.
/// Checks for wire and code compatibility issues between Thrift schema versions.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class ThriftSchemaCompatibilityAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "FBTHRIFTCOMPAT";

    private static readonly LocalizableString Title = "Thrift Schema Compatibility";
    private static readonly LocalizableString MessageFormat = "Thrift schema compatibility issue: {0}";
    private static readonly LocalizableString Description = "Detects breaking changes in Thrift schemas that affect wire or code compatibility.";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        Title,
        MessageFormat,
        "Compatibility",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: Description);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(AnalyzeNamedType, SymbolKind.NamedType);
    }

    private void AnalyzeNamedType(SymbolAnalysisContext context)
    {
        var namedType = (INamedTypeSymbol)context.Symbol;

        // Check for Thrift struct/class attributes
        var thriftStructAttr = namedType.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.Name == "ThriftStructAttribute" ||
                                 a.AttributeClass?.Name == "ThriftExceptionAttribute" ||
                                 a.AttributeClass?.Name == "ThriftUnionAttribute");

        if (thriftStructAttr == null) return;

        // Analyze fields for compatibility issues
        var fields = namedType.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(f => f.GetAttributes().Any(a => a.AttributeClass?.Name == "ThriftFieldAttribute"))
            .ToList();

        var fieldIds = new HashSet<int>();
        var fieldNames = new HashSet<string>();

        foreach (var field in fields)
        {
            var attr = field.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.Name == "ThriftFieldAttribute");
            if (attr == null) continue;

            var fieldId = attr.ConstructorArguments.FirstOrDefault().Value as int? ?? 0;
            var fieldName = field.Name;

            // Check duplicate field IDs
            if (!fieldIds.Add(fieldId))
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, field.Locations[0],
                    $"Duplicate field ID {fieldId}"));
            }

            // Check duplicate field names (for serialization by name)
            if (!fieldNames.Add(fieldName))
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, field.Locations[0],
                    $"Duplicate field name '{fieldName}' - affects JSON/SimpleJSON serialization"));
            }

            // Check required/optional/terse transitions
            CheckFieldQualifierTransitions(context, field, attr);

            // Check field type changes
            CheckFieldTypeCompatibility(context, field, attr);
        }

        // Check ReserveIds attribute
        CheckReserveIds(context, namedType);
    }

    private void CheckFieldQualifierTransitions(SymbolAnalysisContext context, IFieldSymbol field, AttributeData attr)
    {
        var qualifier = GetFieldQualifier(field);
        var isOptional = qualifier == FieldQualifier.Optional;
        var isRequired = qualifier == FieldQualifier.Required;
        var isTerse = qualifier == FieldQualifier.Terse;
        var isUnqualified = qualifier == FieldQualifier.Unqualified;

        // These transitions are NOT wire compatible:
        // Required -> Optional/Unqualified/Terse (if reader enforces required)
        // Optional -> Required (new reader sees field as set when old writer never set it)
        // Unqualified -> Optional (old writer always writes, new reader sees as set)
        // Unqualified -> Terse (custom default vs intrinsic default)
        // Optional -> Terse (unset vs intrinsic default)

        // We can only warn about current state; need baseline comparison for transitions
        // For now, flag potentially problematic qualifiers
        if (isRequired)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, field.Locations[0],
                $"Field '{field.Name}' is 'required' - not recommended. Most languages ignore 'required'. " +
                $"Consider 'unqualified' or 'optional'. If changing from required, ensure no reader enforces required."));
        }

        if (isOptional && HasCustomDefault(field))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, field.Locations[0],
                $"Optional field '{field.Name}' has custom default - behavior varies across languages. " +
                $"C++/Java/Rust/Python leave unset; Go/Hack/Thrift-Py-Deprecated put default in field."));
        }
    }

    private void CheckFieldTypeCompatibility(SymbolAnalysisContext context, IFieldSymbol field, AttributeData attr)
    {
        var fieldType = field.Type;

        // Check for incompatible type changes:
        // - i32 <-> enum: COMPATIBLE
        // - string <-> binary: COMPATIBLE (Binary/Compact), NOT COMPATIBLE (JSON/SimpleJSON)
        // - Other primitive changes: NOT COMPATIBLE

        if (fieldType.TypeKind == TypeKind.Enum)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, field.Locations[0],
                $"Enum field '{field.Name}' - adding/removing enum values is wire compatible. " +
                $"Changing enum to i32 or vice versa is wire compatible. " +
                $"Renaming enum values is compatible only with serialization by ID."));
        }

        if (fieldType.SpecialType == SpecialType.System_String ||
            fieldType.Name == "byte[]")
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, field.Locations[0],
                $"String/Binary field '{field.Name}' - changing between string and binary is " +
                $"wire compatible for Binary/Compact protocols, NOT compatible for JSON/SimpleJSON. " +
                $"After binary->string change, non-UTF8 values may break some readers."));
        }
    }

    private void CheckReserveIds(SymbolAnalysisContext context, INamedTypeSymbol type)
    {
        var reserveAttr = type.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.Name == "ThriftReserveIdsAttribute");

        if (reserveAttr != null)
        {
            var ids = reserveAttr.ConstructorArguments
                .SelectMany(a => a.Values)
                .Select(v => v.Value as int?)
                .Where(v => v.HasValue)
                .Select(v => v.Value)
                .ToHashSet();

            // Verify reserved IDs are not used by current fields
            var fields = type.GetMembers()
                .OfType<IFieldSymbol>()
                .Where(f => f.GetAttributes().Any(a => a.AttributeClass?.Name == "ThriftFieldAttribute"));

            foreach (var field in fields)
            {
                var fieldAttr = field.GetAttributes()
                    .FirstOrDefault(a => a.AttributeClass?.Name == "ThriftFieldAttribute");
                var fieldId = fieldAttr?.ConstructorArguments.FirstOrDefault().Value as int? ?? 0;

                if (ids.Contains(fieldId))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Rule, field.Locations[0],
                        $"Field '{field.Name}' uses reserved ID {fieldId}. " +
                        $"Reserved IDs should not be reused."));
                }
            }
        }
    }

    private FieldQualifier GetFieldQualifier(IFieldSymbol field)
    {
        var attrs = field.GetAttributes();
        if (attrs.Any(a => a.AttributeClass?.Name == "ThriftOptionalAttribute")) return FieldQualifier.Optional;
        if (attrs.Any(a => a.AttributeClass?.Name == "ThriftRequiredAttribute")) return FieldQualifier.Required;
        if (attrs.Any(a => a.AttributeClass?.Name == "ThriftTerseWriteAttribute")) return FieldQualifier.Terse;
        if (attrs.Any(a => a.AttributeClass?.Name == "ThriftFillAttribute")) return FieldQualifier.Fill;
        return FieldQualifier.Unqualified;
    }

    private bool HasCustomDefault(IFieldSymbol field)
    {
        return field.HasConstantValue && field.ConstantValue != null;
    }
}

/// <summary>
/// Field qualifier enumeration.
/// </summary>
public enum FieldQualifier
{
    Required,
    Optional,
    Unqualified,
    Terse,
    Fill
}

/// <summary>
/// Static analysis helper for Thrift schema compatibility.
/// Can be used without Roslyn for build-time or runtime checks.
/// </summary>
public static class ThriftSchemaCompat
{
    /// <summary>
    /// Compatibility check result.
    /// </summary>
    public readonly struct CompatResult
    {
        public bool WireCompatible { get; init; }
        public bool CodeCompatible { get; init; }
        public IReadOnlyList<string> Warnings { get; init; }
        public IReadOnlyList<string> Errors { get; init; }

        public static CompatResult Compatible() => new() { WireCompatible = true, CodeCompatible = true };
        public static CompatResult Incompatible(string error) => new() { WireCompatible = false, CodeCompatible = false, Errors = new[] { error } };
        public static CompatResult Warning(string warning) => new() { WireCompatible = true, CodeCompatible = true, Warnings = new[] { warning } };
    }

    /// <summary>
    /// Checks compatibility between two Thrift struct types.
    /// </summary>
    public static CompatResult CheckCompatibility(Type oldType, Type newType)
    {
        var warnings = new List<string>();
        var errors = new List<string>();

        var oldFields = GetThriftFields(oldType);
        var newFields = GetThriftFields(newType);

        var oldFieldIds = oldFields.ToDictionary(f => f.Id, f => f);
        var newFieldIds = newFields.ToDictionary(f => f.Id, f => f);
        var oldFieldNames = oldFields.ToDictionary(f => f.Name, f => f);
        var newFieldNames = newFields.ToDictionary(f => f.Name, f => f);

        // Check removed fields
        foreach (var oldField in oldFields)
        {
            if (!newFieldIds.ContainsKey(oldField.Id))
            {
                // Removed field - wire compatible, not code compatible
                warnings.Add($"Field '{oldField.Name}' (id={oldField.Id}) removed. Wire compatible, not code compatible.");
            }
        }

        // Check added fields
        foreach (var newField in newFields)
        {
            if (!oldFieldIds.ContainsKey(newField.Id))
            {
                // Added field - wire compatible, code compatible
                warnings.Add($"Field '{newField.Name}' (id={newField.Id}) added. Wire and code compatible.");
            }
        }

        // Check modified fields
        foreach (var newField in newFields)
        {
            if (oldFieldIds.TryGetValue(newField.Id, out var oldField))
            {
                // Field exists in both - check for incompatible changes
                CheckFieldModification(oldField, newField, warnings, errors);
            }
        }

        // Check renamed fields (by name, keeping ID)
        foreach (var newField in newFields)
        {
            if (oldFieldNames.TryGetValue(newField.Name, out var oldField))
            {
                if (oldField.Id != newField.Id)
                {
                    warnings.Add($"Field '{oldField.Name}' renamed to '{newField.Name}' but ID changed from {oldField.Id} to {newField.Id}. " +
                        $"Compatible only with serialization by name (JSON/SimpleJSON).");
                }
            }
        }

        // Check ReserveIds
        CheckReserveIds(oldType, newType, errors);

        var wireCompatible = errors.Count == 0;
        var codeCompatible = errors.Count == 0 && warnings.Count(w => w.Contains("not code compatible")) == 0;

        return new CompatResult
        {
            WireCompatible = wireCompatible,
            CodeCompatible = codeCompatible,
            Warnings = warnings,
            Errors = errors
        };
    }

    private static void CheckFieldModification(ThriftFieldInfo oldField, ThriftFieldInfo newField, List<string> warnings, List<string> errors)
    {
        // Qualifier changes
        var oldQual = oldField.Qualifier;
        var newQual = newField.Qualifier;

        if (oldQual != newQual)
        {
            var compat = GetQualifierCompatibility(oldQual, newQual);
            if (!compat.WireCompatible)
            {
                errors.Add($"Field '{oldField.Name}' qualifier changed from {oldQual} to {newQual}: NOT wire compatible. {compat.Message}");
            }
            else if (!compat.CodeCompatible)
            {
                warnings.Add($"Field '{oldField.Name}' qualifier changed from {oldQual} to {newQual}: Wire compatible, not code compatible. {compat.Message}");
            }
        }

        // Type changes
        if (oldField.Type != newField.Type)
        {
            var compat = GetTypeCompatibility(oldField.Type, newField.Type);
            if (!compat.WireCompatible)
            {
                errors.Add($"Field '{oldField.Name}' type changed from {oldField.Type.Name} to {newField.Type.Name}: NOT wire compatible. {compat.Message}");
            }
            else if (!compat.CodeCompatible)
            {
                warnings.Add($"Field '{oldField.Name}' type changed from {oldField.Type.Name} to {newField.Type.Name}: Wire compatible, not code compatible. {compat.Message}");
            }
        }

        // Enum value changes
        if (oldField.Type.IsEnum && newField.Type.IsEnum)
        {
            var oldValues = Enum.GetNames(oldField.Type).ToHashSet();
            var newValues = Enum.GetNames(newField.Type).ToHashSet();

            var removed = oldValues.Except(newValues).ToList();
            var added = newValues.Except(oldValues).ToList();

            if (removed.Any())
            {
                warnings.Add($"Enum '{oldField.Type.Name}' removed values: {string.Join(", ", removed)}. Wire compatible, not code compatible.");
            }
            if (added.Any())
            {
                warnings.Add($"Enum '{newField.Type.Name}' added values: {string.Join(", ", added)}. Wire and code compatible.");
            }
        }

        // Default value changes
        if (oldField.HasDefault && newField.HasDefault)
        {
            if (!Equals(oldField.DefaultValue, newField.DefaultValue))
            {
                if (oldQual == FieldQualifier.Required || oldQual == FieldQualifier.Unqualified)
                {
                    errors.Add($"Non-optional field '{oldField.Name}' default changed: NOT wire compatible. Old writer sends old default.");
                }
                else
                {
                    warnings.Add($"Optional field '{oldField.Name}' default changed: Wire compatible, not code compatible.");
                }
            }
        }
        else if (oldField.HasDefault != newField.HasDefault)
        {
            warnings.Add($"Field '{oldField.Name}' default added/removed. Wire compatible, behavior varies by language.");
        }
    }

    private static (bool WireCompatible, bool CodeCompatible, string Message) GetQualifierCompatibility(FieldQualifier from, FieldQualifier to)
    {
        return (from, to) switch
        {
            // Required -> anything (if reader enforces required)
            (FieldQualifier.Required, FieldQualifier.Optional) => (false, false, "Reader enforcing required will throw when field missing"),
            (FieldQualifier.Required, FieldQualifier.Unqualified) => (false, false, "Reader enforcing required will throw when field missing"),
            (FieldQualifier.Required, FieldQualifier.Terse) => (false, false, "Reader enforcing required will throw when field missing (terse skips intrinsic default)"),
            (FieldQualifier.Required, FieldQualifier.Fill) => (false, false, "Reader enforcing required will throw when field missing"),

            // Optional -> Required
            (FieldQualifier.Optional, FieldQualifier.Required) => (true, false, "Old writer leaves field unset, new reader sees as set"),

            // Optional -> Terse
            (FieldQualifier.Optional, FieldQualifier.Terse) => (true, false, "Old writer: unset = missing. New writer: intrinsic default = missing. Unset vs 0/false distinction lost"),

            // Unqualified -> Optional
            (FieldQualifier.Unqualified, FieldQualifier.Optional) => (true, false, "Old writer always writes field, new reader sees as set even when unset"),

            // Unqualified -> Terse
            (FieldQualifier.Unqualified, FieldQualifier.Terse) => (true, false, "Custom default vs intrinsic default mismatch. Old reader gets custom default where new writer sent intrinsic default"),

            // Terse -> Optional
            (FieldQualifier.Terse, FieldQualifier.Optional) => (true, false, "New reader sees intrinsic default as unset"),

            // Terse -> Unqualified
            (FieldQualifier.Terse, FieldQualifier.Unqualified) => (true, false, "Custom default vs intrinsic default mismatch"),

            // Compatible transitions
            (FieldQualifier.Optional, FieldQualifier.Unqualified) => (true, true, "Compatible"),
            (FieldQualifier.Unqualified, FieldQualifier.Required) => (true, true, "Compatible if no reader enforces required"),
            
            // Fill fields are always serialized (but don't match Fill,Fill case which is handled below)
            (FieldQualifier.Fill, FieldQualifier.Optional) => (true, true, "Fill fields always serialized"),
            (FieldQualifier.Fill, FieldQualifier.Unqualified) => (true, true, "Fill fields always serialized"),
            (FieldQualifier.Fill, FieldQualifier.Terse) => (true, true, "Fill fields always serialized"),
            (FieldQualifier.Fill, FieldQualifier.Required) => (true, true, "Fill fields always serialized"),

            _ when from == to => (true, true, "No change"),
            _ => (true, true, "Compatible")
        };
    }

    private static (bool WireCompatible, bool CodeCompatible, string Message) GetTypeCompatibility(Type from, Type to)
    {
        // i32 <-> enum
        if ((from == typeof(int) && to.IsEnum) || (from.IsEnum && to == typeof(int)))
        {
            return (true, true, "i32 <-> enum compatible. Old reader may get unnamed enum value.");
        }

        // string <-> binary
        if ((from == typeof(string) && to == typeof(byte[])) || (from == typeof(byte[]) && to == typeof(string)))
        {
            return (true, false, "string <-> binary compatible for Binary/Compact, NOT for JSON/SimpleJSON");
        }

        // Other primitive changes
        if (from.IsPrimitive && to.IsPrimitive)
        {
            return (false, false, $"Primitive type change {from.Name} -> {to.Name} not wire compatible");
        }

        return (false, false, $"Type change {from.Name} -> {to.Name} not wire compatible");
    }

    private static void CheckReserveIds(Type oldType, Type newType, List<string> errors)
    {
        var oldReserve = oldType.GetCustomAttribute<ThriftReserveIdsAttribute>();
        var newReserve = newType.GetCustomAttribute<ThriftReserveIdsAttribute>();

        var oldIds = oldReserve?.Ids ?? Array.Empty<int>();
        var newIds = newReserve?.Ids ?? Array.Empty<int>();

        var oldFields = GetThriftFields(oldType);
        var newFields = GetThriftFields(newType);

        foreach (var id in oldIds)
        {
            if (oldFields.Any(f => f.Id == id) || newFields.Any(f => f.Id == id))
            {
                errors.Add($"Reserved ID {id} is used by a field. Reserved IDs must not be reused.");
            }
        }

        // New reserved IDs should not conflict with existing fields
        foreach (var id in newIds.Except(oldIds))
        {
            if (newFields.Any(f => f.Id == id))
            {
                errors.Add($"New reserved ID {id} conflicts with existing field.");
            }
        }
    }

    private static IReadOnlyList<ThriftFieldInfo> GetThriftFields(Type type)
    {
        var fields = new List<ThriftFieldInfo>();

        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            var attr = field.GetCustomAttribute<ThriftFieldAttribute>();
            if (attr != null)
            {
                fields.Add(new ThriftFieldInfo
                {
                    Id = attr.Id,
                    Name = field.Name,
                    Type = field.FieldType,
                    Qualifier = GetQualifier(field),
                    HasDefault = field.IsLiteral && !field.IsInitOnly,
                    DefaultValue = field.GetRawConstantValue()
                });
            }
        }

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            var attr = prop.GetCustomAttribute<ThriftFieldAttribute>();
            if (attr != null && prop.CanRead && prop.CanWrite)
            {
                fields.Add(new ThriftFieldInfo
                {
                    Id = attr.Id,
                    Name = prop.Name,
                    Type = prop.PropertyType,
                    Qualifier = GetQualifier(prop),
                    HasDefault = false // Properties don't have constant values in same way
                });
            }
        }

        return fields;
    }

    private static FieldQualifier GetQualifier(MemberInfo member)
    {
        if (member.GetCustomAttribute<ThriftOptionalAttribute>() != null) return FieldQualifier.Optional;
        if (member.GetCustomAttribute<ThriftRequiredAttribute>() != null) return FieldQualifier.Required;
        if (member.GetCustomAttribute<ThriftTerseWriteAttribute>() != null) return FieldQualifier.Terse;
        if (member.GetCustomAttribute<ThriftFillAttribute>() != null) return FieldQualifier.Fill;
        return FieldQualifier.Unqualified;
    }
}

/// <summary>
/// Thrift field information for compatibility checking.
/// </summary>
public readonly struct ThriftFieldInfo
{
    public ThriftFieldInfo(short id, string name, Type type, FieldQualifier qualifier, bool hasDefault, object? defaultValue)
    {
        Id = id;
        Name = name;
        Type = type;
        Qualifier = qualifier;
        HasDefault = hasDefault;
        DefaultValue = defaultValue;
    }

    public short Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public Type Type { get; init; } = typeof(void);
    public FieldQualifier Qualifier { get; init; }
    public bool HasDefault { get; init; }
    public object? DefaultValue { get; init; }
}

/// <summary>
/// Attributes for Thrift field qualifiers.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class ThriftOptionalAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class ThriftRequiredAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class ThriftTerseWriteAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class ThriftFillAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true, Inherited = false)]
public sealed class ThriftReserveIdsAttribute : Attribute
{
    public IReadOnlyList<int> Ids { get; }

    public ThriftReserveIdsAttribute(params int[] ids)
    {
        Ids = ids ?? Array.Empty<int>();
    }
}