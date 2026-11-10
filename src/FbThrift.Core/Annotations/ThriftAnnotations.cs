// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Annotations;

using System;
using FbThrift.Core;

/// <summary>
/// Base attribute for all Thrift annotations.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum | 
                AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Interface | 
                AttributeTargets.Delegate, AllowMultiple = true, Inherited = false)]
public abstract class ThriftAnnotationAttribute : Attribute
{
    public string? Name { get; init; }
}

/// <summary>
/// Specifies the C# namespace for generated code.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Module, AllowMultiple = false)]
public sealed class ThriftNamespaceAttribute : ThriftAnnotationAttribute
{
    public string Namespace { get; }

    public ThriftNamespaceAttribute(string @namespace)
    {
        Namespace = @namespace ?? throw new ArgumentNullException(nameof(@namespace));
    }
}

/// <summary>
/// Universal Resource Identifier for a Thrift definition.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum | 
                AttributeTargets.Interface, AllowMultiple = false)]
public sealed class ThriftUriAttribute : ThriftAnnotationAttribute
{
    public string Uri { get; }

    public ThriftUriAttribute(string uri)
    {
        Uri = uri ?? throw new ArgumentNullException(nameof(uri));
    }
}

/// <summary>
/// Marks a type as deprecated.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum | 
                AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Method, 
                AllowMultiple = false)]
public sealed class ThriftDeprecatedAttribute : ThriftAnnotationAttribute
{
    public string? Message { get; init; }
}

/// <summary>
/// Allows use of reserved identifiers.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum | 
                AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false)]
public sealed class ThriftAllowReservedIdentifierAttribute : ThriftAnnotationAttribute { }

/// <summary>
/// Allows use of reserved filenames.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Module, AllowMultiple = false)]
public sealed class ThriftAllowReservedFilenameAttribute : ThriftAnnotationAttribute { }

/// <summary>
/// Specifies the Thrift protocol to use.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface, 
                AllowMultiple = false)]
public sealed class ThriftProtocolAttribute : ThriftAnnotationAttribute
{
    public string Protocol { get; }

    public ThriftProtocolAttribute(string protocol)
    {
        Protocol = protocol ?? throw new ArgumentNullException(nameof(protocol));
    }
}

/// <summary>
/// Specifies the compression algorithm for a field or struct.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Class | 
                AttributeTargets.Struct, AllowMultiple = false)]
public sealed class ThriftCompressionAttribute : ThriftAnnotationAttribute
{
    public CompressionAlgorithm Algorithm { get; }

    public ThriftCompressionAttribute(CompressionAlgorithm algorithm)
    {
        Algorithm = algorithm;
    }
}

/// <summary>
/// Specifies a custom type adapter for a field or typedef.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Class | 
                AttributeTargets.Struct | AttributeTargets.Delegate, AllowMultiple = false)]
public sealed class ThriftAdapterAttribute : ThriftAnnotationAttribute
{
    public string AdapterTypeName { get; }

    public ThriftAdapterAttribute(string adapterTypeName)
    {
        AdapterTypeName = adapterTypeName ?? throw new ArgumentNullException(nameof(adapterTypeName));
    }

    /// <summary>
    /// The adapted type (for cases where the compiler can't infer it).
    /// </summary>
    public string? AdaptedType { get; init; }

    /// <summary>
    /// The underlying name for the adapted type (to avoid collisions).
    /// </summary>
    public string? UnderlyingName { get; init; }

    /// <summary>
    /// Extra namespace for the adapted type.
    /// </summary>
    public string? ExtraNamespace { get; init; }
}

/// <summary>
/// Specifies a custom type for a typedef or field.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Class | 
                AttributeTargets.Struct | AttributeTargets.Delegate, AllowMultiple = false)]
public sealed class ThriftTypeAttribute : ThriftAnnotationAttribute
{
    public string TypeName { get; }

    public ThriftTypeAttribute(string typeName)
    {
        TypeName = typeName ?? throw new ArgumentNullException(nameof(typeName));
    }

    public string? Template { get; init; }
}

/// <summary>
/// Specifies that a field is readonly.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false)]
public sealed class ThriftReadonlyAttribute : ThriftAnnotationAttribute { }

/// <summary>
/// Specifies that a field is idempotent.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ThriftIdempotentAttribute : ThriftAnnotationAttribute { }

/// <summary>
/// Specifies that a service method is oneway (no response).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ThriftOnewayAttribute : ThriftAnnotationAttribute { }

/// <summary>
/// Specifies that a field is a sink (client streaming).
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Parameter, 
                AllowMultiple = false)]
public sealed class ThriftSinkAttribute : ThriftAnnotationAttribute { }

/// <summary>
/// Specifies that a field is a stream (server streaming).
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Parameter | 
                AttributeTargets.ReturnValue, AllowMultiple = false)]
public sealed class ThriftStreamAttribute : ThriftAnnotationAttribute { }

/// <summary>
/// Specifies that a service supports interactions.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, AllowMultiple = false)]
public sealed class ThriftInteractionAttribute : ThriftAnnotationAttribute { }

/// <summary>
/// Specifies the interaction ID for a field or method.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Method | 
                AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class ThriftInteractionIdAttribute : ThriftAnnotationAttribute
{
    public long InteractionId { get; }

    public ThriftInteractionIdAttribute(long interactionId)
    {
        InteractionId = interactionId;
    }
}

/// <summary>
/// Marks a type as a boxed type (for Protocol Objects).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false)]
public sealed class ThriftBoxAttribute : ThriftAnnotationAttribute
{
    public string? TypeName { get; init; }
}

/// <summary>
/// Specifies a custom C++ type name for a typedef or field.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum | 
                AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Delegate, 
                AllowMultiple = false)]
public sealed class CppTypeNameAttribute : ThriftAnnotationAttribute
{
    public string TypeName { get; }

    public CppTypeNameAttribute(string typeName)
    {
        TypeName = typeName ?? throw new ArgumentNullException(nameof(typeName));
    }

    public string? UnderlyingName { get; init; }
    public string? ExtraNamespace { get; init; }
}

/// <summary>
/// Specifies a custom C++ template for a typedef.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Delegate, 
                AllowMultiple = false)]
public sealed class CppTemplateAttribute : ThriftAnnotationAttribute
{
    public string Template { get; }

    public CppTemplateAttribute(string template)
    {
        Template = template ?? throw new ArgumentNullException(nameof(template));
    }
}

/// <summary>
/// Marks a definition as allowing legacy missing URIs.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum | 
                AttributeTargets.Interface, AllowMultiple = false)]
public sealed class ThriftAllowLegacyMissingUrisAttribute : ThriftAnnotationAttribute { }
