// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Core.Compiler;

using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Base class for all Thrift AST nodes.
/// </summary>
public abstract class ThriftNode
{
    public int Line { get; set; }
    public int Column { get; set; }
    public string SourceFile { get; set; }
}

/// <summary>
/// Thrift module (top-level container).
/// </summary>
public sealed class ThriftModule : ThriftNode
{
    public string Name { get; set; }
    public string Namespace { get; set; }
    public List<string> Includes { get; } = new();
    public Dictionary<string, string> Namespaces { get; } = new();
    public List<ThriftConst> Constants { get; } = new();
    public List<ThriftTypedef> Typedefs { get; } = new();
    public List<ThriftEnum> Enums { get; } = new();
    public List<ThriftStruct> Structs { get; } = new();
    public List<ThriftException> Exceptions { get; } = new();
    public List<ThriftUnion> Unions { get; } = new();
    public List<ThriftService> Services { get; } = new();
    public List<ThriftAnnotation> Annotations { get; } = new();
}

/// <summary>
/// Base class for type definitions.
/// </summary>
public abstract class ThriftTypeDefinition : ThriftNode
{
    public string Name { get; set; }
    public Dictionary<string, string> Annotations { get; } = new();
}

/// <summary>
/// Thrift constant definition.
/// </summary>
public sealed class ThriftConst : ThriftNode
{
    public string Name { get; set; }
    public ThriftType Type { get; set; }
    public object Value { get; set; }
    public string CSharpName { get; set; }
}

/// <summary>
/// Thrift typedef definition.
/// </summary>
public class ThriftTypedef : ThriftTypeDefinition
{
    public ThriftType Type { get; set; }
    public string CSharpName { get; set; }
}

/// <summary>
/// Thrift enum definition.
/// </summary>
public class ThriftEnum : ThriftTypeDefinition
{
    public List<ThriftEnumValue> Values { get; } = new();
    public string CSharpName { get; set; }
}

/// <summary>
/// Thrift enum value.
/// </summary>
public class ThriftEnumValue : ThriftNode
{
    public string Name { get; set; }
    public long Value { get; set; }
    public string CSharpName { get; set; }
}

/// <summary>
/// Thrift field definition.
/// </summary>
public sealed class ThriftField : ThriftNode
{
    public int Id { get; set; }
    public string Name { get; set; }
    public ThriftType Type { get; set; }
    public bool Required { get; set; }
    public bool Optional { get; set; }
    public object DefaultValue { get; set; }
    public string Description { get; set; }
    public Dictionary<string, string> Annotations { get; } = new();
    public string CSharpName { get; set; }
    public string ThriftName => Name;
}

/// <summary>
/// Thrift struct definition.
/// </summary>
public class ThriftStruct : ThriftTypeDefinition
{
    public List<ThriftField> Fields { get; } = new();
    public string BaseType { get; set; }
    public bool IsException { get; set; }
    public string CSharpName { get; set; }
}

/// <summary>
/// Thrift exception definition.
/// </summary>
public class ThriftException : ThriftStruct
{
    public ThriftException()
    {
        IsException = true;
    }
    public string CSharpName { get; set; }
}

/// <summary>
/// Thrift union definition.
/// </summary>
public class ThriftUnion : ThriftTypeDefinition
{
    public List<ThriftField> Fields { get; } = new();
    public string CSharpName { get; set; }
}

/// <summary>
/// Thrift service definition.
/// </summary>
public class ThriftService : ThriftTypeDefinition
{
    public ThriftService? Extends { get; set; }
    public List<ThriftFunction> Functions { get; } = new();
    public string CSharpName { get; set; }
    public string Namespace { get; set; }
}

/// <summary>
/// Thrift function definition.
/// </summary>
public class ThriftFunction : ThriftNode
{
    public string Name { get; set; }
    public ThriftType ReturnType { get; set; }
    public List<ThriftField> Parameters { get; } = new();
    public List<ThriftType> Throws { get; } = new();
    public bool IsOneWay { get; set; }
    public string CSharpName { get; set; }
    public bool IsAsync { get; set; }
}

/// <summary>
/// Thrift annotation.
/// </summary>
public class ThriftAnnotation : ThriftTypeDefinition
{
    public List<ThriftField> Fields { get; } = new();
    public string CSharpName { get; set; }
}

/// <summary>
/// Thrift type system.
/// </summary>
public abstract class ThriftType : ThriftNode
{
    public static readonly ThriftType Bool = new ThriftBaseType(ThriftTypeKind.Bool);
    public static readonly ThriftType Byte = new ThriftBaseType(ThriftTypeKind.Byte);
    public static readonly ThriftType I16 = new ThriftBaseType(ThriftTypeKind.I16);
    public static readonly ThriftType I32 = new ThriftBaseType(ThriftTypeKind.I32);
    public static readonly ThriftType I64 = new ThriftBaseType(ThriftTypeKind.I64);
    public static readonly ThriftType Float = new ThriftBaseType(ThriftTypeKind.Float);
    public static readonly ThriftType Double = new ThriftBaseType(ThriftTypeKind.Double);
    public static readonly ThriftType String = new ThriftBaseType(ThriftTypeKind.String);
    public static readonly ThriftType Binary = new ThriftBaseType(ThriftTypeKind.Binary);
    public static readonly ThriftType List = new ThriftContainerType(ThriftTypeKind.List, "list", null);
    public static readonly ThriftType Set = new ThriftContainerType(ThriftTypeKind.Set, "set", null);
    public static readonly ThriftType Map = new ThriftContainerType(ThriftTypeKind.Map, "map", null);
    public static readonly ThriftType Struct = new ThriftBaseType(ThriftTypeKind.Struct);
    public static readonly ThriftType Enum = new ThriftBaseType(ThriftTypeKind.Enum);
    public static readonly ThriftType Void = new ThriftBaseType(ThriftTypeKind.Void);
    
    public ThriftTypeKind Kind { get; }
    public string Name { get; }
    public bool IsNullable { get; set; } = true;
    public bool IsPrimitive => Kind == ThriftTypeKind.Base;
    public bool IsContainer => Kind is ThriftTypeKind.List or ThriftTypeKind.Set or ThriftTypeKind.Map;
    public ThriftType ElementType { get; set; }
    public ThriftType KeyType { get; set; }
    public ThriftType ValueType { get; set; }
    public string CustomName { get; set; }
    public string CSharpName => CustomName ?? Name;
    public string ThriftName => Name;
    
    protected ThriftType(ThriftTypeKind kind, string name)
    {
        Kind = kind;
        Name = name;
    }
    
    public static ThriftType CreateList(ThriftType elementType)
    {
        return new ThriftContainerType(ThriftTypeKind.List, $"list<{elementType.Name}>", elementType);
    }
    
    public static ThriftType CreateSet(ThriftType elementType)
    {
        return new ThriftContainerType(ThriftTypeKind.Set, $"set<{elementType.Name}>", elementType);
    }
    
    public static ThriftType CreateMap(ThriftType keyType, ThriftType valueType)
    {
        return new ThriftContainerType(ThriftTypeKind.Map, $"map<{keyType.Name},{valueType.Name}>", keyType, valueType);
    }
    
    public static ThriftType CreateTypedef(string name, ThriftType baseType)
    {
        return new ThriftTypedefType(name, baseType);
    }
    
    public override string ToString() => Name;
}

/// <summary>
/// Thrift type kind.
/// </summary>
public enum ThriftTypeKind
{
    Base,
    Bool,
    Byte,
    I16,
    I32,
    I64,
    Float,
    Double,
    String,
    Binary,
    Struct,
    Exception,
    Union,
    Typedef,
    List,
    Set,
    Map,
    Enum,
    Void
}

/// <summary>
/// Base Thrift type (primitives).
/// </summary>
public sealed class ThriftBaseType : ThriftType
{
    public ThriftBaseType(ThriftTypeKind kind) : base(ThriftTypeKind.Base, kind.ToString().ToLowerInvariant())
    {
    }
}

/// <summary>
/// Container Thrift type (list, set, map).
/// </summary>
public sealed class ThriftContainerType : ThriftType
{
    public ThriftContainerType(ThriftTypeKind kind, string name, ThriftType elementType, ThriftType keyType = null, ThriftType valueType = null)
        : base(kind, name)
    {
        ElementType = elementType;
        KeyType = keyType;
        ValueType = valueType;
    }
}

/// <summary>
/// Typedef type.
/// </summary>
public class ThriftTypedefType : ThriftType
{
    public ThriftTypedefType(string name, ThriftType baseType)
        : base(ThriftTypeKind.Typedef, name)
    {
        CustomName = name;
        // The base type is stored in ElementType for typedefs
        ElementType = baseType;
    }
}