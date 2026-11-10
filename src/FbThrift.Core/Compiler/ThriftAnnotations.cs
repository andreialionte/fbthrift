// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Core.Compiler;

using System;
using System.Collections.Generic;

/// <summary>
/// Thrift annotation scope - where an annotation can be applied.
/// </summary>
[Flags]
public enum AnnotationScope
{
    None = 0,
    Program = 1 << 0,           // File-level
    Struct = 1 << 1,            // Struct definition
    Exception = 1 << 2,         // Exception definition
    Union = 1 << 3,             // Union definition
    Enum = 1 << 4,              // Enum definition
    Typedef = 1 << 5,           // Typedef
    Service = 1 << 6,           // Service definition
    Field = 1 << 7,             // Field definition
    Function = 1 << 8,          // Function definition
    FunctionParameter = 1 << 9, // Function parameter
    Const = 1 << 10,            // Constant
    EnumValue = 1 << 11,        // Enum value
    Interaction = 1 << 15,      // Interaction definition
    Definition = 1 << 16,       // Any definition
    Structured = Struct | Exception | Union, // Structured types (struct, exception, union)
}

/// <summary>
/// Thrift annotation definition.
/// </summary>
public sealed class ThriftAnnotationDef
{
    public string Name { get; set; }
    public string ThriftName { get; set; } // e.g., "thrift.Box"
    public AnnotationScope Scope { get; set; }
    public List<ThriftField> Fields { get; set; } = new();
    public string Description { get; set; }
    public bool IsExperimental { get; set; }
}

/// <summary>
/// Registry of built-in fbthrift annotations.
/// </summary>
public static class ThriftAnnotations
{
    private static readonly Dictionary<string, ThriftAnnotationDef> _annotations = new();
    private static readonly Dictionary<AnnotationScope, List<ThriftAnnotationDef>> _annotationsByScope = new();
    
    static ThriftAnnotations()
    {
        InitializeAnnotations();
    }
    
    private static void InitializeAnnotations()
    {
        // Thrift feature annotations
        Register(new ThriftAnnotationDef
        {
            Name = "Box",
            ThriftName = "thrift.Box",
            Scope = AnnotationScope.Field,
            Description = "Indicates whether the nested fields are accessible directly. Affects C++ and Rust.",
            IsExperimental = true
        });
        
        Register(new ThriftAnnotationDef
        {
            Name = "Mixin",
            ThriftName = "thrift.Mixin",
            Scope = AnnotationScope.Field,
            Description = "Indicates whether the nested fields are accessible directly. See mixins.md"
        });
        
        Register(new ThriftAnnotationDef
        {
            Name = "SerializeInFieldIdOrder",
            ThriftName = "thrift.SerializeInFieldIdOrder",
            Scope = AnnotationScope.Struct | AnnotationScope.Exception,
            Description = "Option to serialize thrift struct in ascending field id order instead of field declaration order. Can make serialized data smaller in compact protocol.",
            IsExperimental = true
        });
        
        Register(new ThriftAnnotationDef
        {
            Name = "BitmaskEnum",
            ThriftName = "thrift.BitmaskEnum",
            Scope = AnnotationScope.Enum,
            Description = "Indicates an enum is a bitmask and should support bit-wise operators. Currently generates additional code in C++ and Hack."
        });
        
        Register(new ThriftAnnotationDef
        {
            Name = "ExceptionMessage",
            ThriftName = "thrift.ExceptionMessage",
            Scope = AnnotationScope.Field,
            Description = "Specifies the field where the exception message is stored. Can be specified on at most one field of an exception definition, whose type must be string."
        });
        
        Register(new ThriftAnnotationDef
        {
            Name = "InternBox",
            ThriftName = "thrift.InternBox",
            Scope = AnnotationScope.Field,
            Description = "Indicates whether a field's value should never be stored on the stack, and that identical values can be shared in immutable contexts.",
            IsExperimental = true
        });
        
        Register(new ThriftAnnotationDef
        {
            Name = "Serial",
            ThriftName = "thrift.Serial",
            Scope = AnnotationScope.Interaction,
            Description = "Indicates that an interaction's methods should be processed sequentially."
        });
        
        Register(new ThriftAnnotationDef
        {
            Name = "Uri",
            ThriftName = "thrift.Uri",
            Scope = AnnotationScope.Enum | AnnotationScope.Service | AnnotationScope.Struct,
            Description = "Changes the URI of this definition away from the default-generated one."
        });
        
        Register(new ThriftAnnotationDef
        {
            Name = "Priority",
            ThriftName = "thrift.Priority",
            Scope = AnnotationScope.Function,
            Description = "Changes the priority of this function (default NORMAL).",
            Fields = new List<ThriftField>
            {
                new ThriftField { Name = "level", Type = ThriftType.Void /* RpcPriority enum */ }
            }
        });
        
        Register(new ThriftAnnotationDef
        {
            Name = "Sealed",
            ThriftName = "thrift.Sealed",
            Scope = AnnotationScope.Struct | AnnotationScope.Exception | AnnotationScope.Union,
            Description = "States that the target structured type is sealed. A sealed structured type can only have fields whose types are also sealed."
        });
        
        Register(new ThriftAnnotationDef
        {
            Name = "DeprecatedUnvalidatedAnnotations",
            ThriftName = "thrift.DeprecatedUnvalidatedAnnotations",
            Scope = AnnotationScope.Definition,
            Description = "Applies unstructured annotations to a definition."
        });
        
        Register(new ThriftAnnotationDef
        {
            Name = "AllowReservedIdentifier",
            ThriftName = "thrift.AllowReservedIdentifier",
            Scope = AnnotationScope.Definition,
            Description = "Allows the use of reserved identifiers containing 'fbthrift' substring."
        });
        
        Register(new ThriftAnnotationDef
        {
            Name = "AllowReservedFilename",
            ThriftName = "thrift.AllowReservedFilename",
            Scope = AnnotationScope.Program,
            Description = "Allows the use of reserved filenames containing 'fbthrift' substring."
        });
        
        Register(new ThriftAnnotationDef
        {
            Name = "RuntimeAnnotation",
            ThriftName = "thrift.RuntimeAnnotation",
            Scope = AnnotationScope.Struct,
            Description = "Applies to structured annotations that need to be accessed runtime in TypeSystem and C++ always-on reflection."
        });
        
        Register(new ThriftAnnotationDef
        {
            Name = "Deprecated",
            ThriftName = "thrift.Deprecated",
            Scope = AnnotationScope.Field,
            Description = "Marks a definition as deprecated. When applied, generated code will include language-specific deprecation markers.",
            Fields = new List<ThriftField>
            {
                new ThriftField { Name = "message", Type = ThriftType.String, Description = "Explanation of why this is deprecated and what to use instead." }
            }
        });
        
        Register(new ThriftAnnotationDef
        {
            Name = "TerseWrite",
            ThriftName = "thrift.TerseWrite",
            Scope = AnnotationScope.Program | AnnotationScope.Struct | AnnotationScope.Exception | AnnotationScope.Field,
            Description = "Changes the field qualifier from 'none' to 'terse'. A terse field is eligible to skip serialization when it equals the intrinsic default value."
        });
        
        Register(new ThriftAnnotationDef
        {
            Name = "AllowUnsafeOptionalCustomDefaultValue",
            ThriftName = "thrift.AllowUnsafeOptionalCustomDefaultValue",
            Scope = AnnotationScope.Field,
            Description = "Allows an optional field to have a custom default value (discouraged)."
        });
        
        Register(new ThriftAnnotationDef
        {
            Name = "AllowUnsafeUnionFieldCustomDefaultValue",
            ThriftName = "thrift.AllowUnsafeUnionFieldCustomDefaultValue",
            Scope = AnnotationScope.Field,
            Description = "Allows a union field to have a custom default value (discouraged)."
        });
        
        Register(new ThriftAnnotationDef
        {
            Name = "AllowUnsafeRequiredFieldQualifier",
            ThriftName = "thrift.AllowUnsafeRequiredFieldQualifier",
            Scope = AnnotationScope.Field,
            Description = "Allows a field to be marked 'required' (deprecated since 2018, behavior inconsistent)."
        });
        
        Register(new ThriftAnnotationDef
        {
            Name = "AllowLegacyMissingUris",
            ThriftName = "thrift.AllowLegacyMissingUris",
            Scope = AnnotationScope.Program | AnnotationScope.Structured | AnnotationScope.Enum,
            Description = "Allows Thrift IDL to build despite missing otherwise required Thrift URIs."
        });
        
        Register(new ThriftAnnotationDef
        {
            Name = "AllowUnsafeNonSealedKeyType",
            ThriftName = "thrift.AllowUnsafeNonSealedKeyType",
            Scope = AnnotationScope.Field | AnnotationScope.Typedef | AnnotationScope.FunctionParameter,
            Description = "Allows map/set field with non-sealed key type (discouraged)."
        });
        
        Register(new ThriftAnnotationDef
        {
            Name = "AllowLegacyTypedefUri",
            ThriftName = "thrift.AllowLegacyTypedefUri",
            Scope = AnnotationScope.Typedef,
            Description = "Allows typedef to have a legacy URI (for backwards compatibility)."
        });
        
        Register(new ThriftAnnotationDef
        {
            Name = "CppType",
            ThriftName = "cpp.Type",
            Scope = AnnotationScope.Struct | AnnotationScope.Enum | AnnotationScope.Union | AnnotationScope.Exception | AnnotationScope.Typedef | AnnotationScope.Field | AnnotationScope.Function | AnnotationScope.Service,
            Description = "C++ type customization annotation.",
            Fields = new List<ThriftField>
            {
                new ThriftField { Name = "template", Type = ThriftType.String },
                new ThriftField { Name = "type", Type = ThriftType.String },
                new ThriftField { Name = "ref_type", Type = ThriftType.String }
            }
        });
        
        Register(new ThriftAnnotationDef
        {
            Name = "CppRef",
            ThriftName = "cpp.Ref",
            Scope = AnnotationScope.Field,
            Description = "C++ reference type customization.",
            Fields = new List<ThriftField>
            {
                new ThriftField { Name = "type", Type = ThriftType.String }
            }
        });
        
        Register(new ThriftAnnotationDef
        {
            Name = "CppProcessInEbThreadUnsafe",
            ThriftName = "cpp.ProcessInEbThreadUnsafe",
            Scope = AnnotationScope.Function,
            Description = "Indicates the function should be processed in the event base thread (unsafe)."
        });
        
        Register(new ThriftAnnotationDef
        {
            Name = "CppStackArguments",
            ThriftName = "cpp.stack_arguments",
            Scope = AnnotationScope.Function,
            Description = "Specifies stack arguments for C++."
        });
        
        // Hack annotations
        Register(new ThriftAnnotationDef
        {
            Name = "HackMigrationBlockingLegacyJSONSerialization",
            ThriftName = "hack.MigrationBlockingLegacyJSONSerialization",
            Scope = AnnotationScope.Union,
            Description = "Blocks legacy JSON serialization for Hack."
        });
        
        // Java annotations
        Register(new ThriftAnnotationDef
        {
            Name = "JavaType",
            ThriftName = "java.Type",
            Scope = AnnotationScope.Struct | AnnotationScope.Field | AnnotationScope.Typedef,
            Description = "Java type customization."
        });
        
        Register(new ThriftAnnotationDef
        {
            Name = "JavaFinal",
            ThriftName = "java.final",
            Scope = AnnotationScope.Struct | AnnotationScope.Field,
            Description = "Makes generated Java class/field final."
        });
        
        // Python annotations
        Register(new ThriftAnnotationDef
        {
            Name = "Py3Hidden",
            ThriftName = "python.Py3Hidden",
            Scope = AnnotationScope.Struct | AnnotationScope.Enum | AnnotationScope.Union | AnnotationScope.Exception | AnnotationScope.Typedef,
            Description = "Hides the type from Python 3 code generation."
        });
        
        // Rust annotations
        Register(new ThriftAnnotationDef
        {
            Name = "RustAsync",
            ThriftName = "rust.Async",
            Scope = AnnotationScope.Function,
            Description = "Generates async Rust code for the function."
        });
        
        Register(new ThriftAnnotationDef
        {
            Name = "RustBox",
            ThriftName = "rust.Box",
            Scope = AnnotationScope.Field,
            Description = "Wraps field in Box for Rust."
        });
    }
    
    private static void Register(ThriftAnnotationDef annotation)
    {
        _annotations[annotation.ThriftName] = annotation;
        
        foreach (var scope in Enum.GetValues(typeof(AnnotationScope)))
        {
            if (annotation.Scope.HasFlag((AnnotationScope)scope))
            {
                if (!_annotationsByScope.TryGetValue((AnnotationScope)scope, out var list))
                {
                    list = new List<ThriftAnnotationDef>();
                    _annotationsByScope[(AnnotationScope)scope] = list;
                }
                list.Add(annotation);
            }
        }
    }
    
    /// <summary>
    /// Gets an annotation by its Thrift name (e.g., "thrift.Box").
    /// </summary>
    public static ThriftAnnotationDef Get(string thriftName)
    {
        return _annotations.TryGetValue(thriftName, out var annotation) ? annotation : null;
    }
    
    /// <summary>
    /// Gets all annotations applicable to a given scope.
    /// </summary>
    public static IReadOnlyList<ThriftAnnotationDef> GetByScope(AnnotationScope scope)
    {
        return _annotationsByScope.TryGetValue(scope, out var list) ? list : new List<ThriftAnnotationDef>();
    }
    
    /// <summary>
    /// Gets all registered annotations.
    /// </summary>
    public static IReadOnlyDictionary<string, ThriftAnnotationDef> All => _annotations;
    
    /// <summary>
    /// Checks if an annotation is valid for a given scope.
    /// </summary>
    public static bool IsValidForScope(string thriftName, AnnotationScope scope)
    {
        if (_annotations.TryGetValue(thriftName, out var annotation))
        {
            return annotation.Scope.HasFlag(scope);
        }
        return false;
    }
}