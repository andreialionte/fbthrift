// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Core.TypeRegistry;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using FbThrift.Core.Dynamic;
using FbThrift.Core.Memory;
using FbThrift.Core.Protocol;
using FbThrift.Core.UniversalNames;
using FBThrift;

/// <summary>
/// Type Registry for Thrift Any deserialization.
/// Maps Universal Names to generated Thrift types and custom serializers.
/// Based on fbthrift's TypeRegistry (C++, Java, Python).
/// </summary>
public sealed class TypeRegistry
{
    private readonly ConcurrentDictionary<string, TypeInfo> _types = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SerializerInfo> _serializers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DeserializerInfo> _deserializers = new(StringComparer.Ordinal);
    private readonly ReaderWriterLockSlim _lock = new();

    /// <summary>
    /// Global generated type registry (auto-populated from loaded assemblies).
    /// </summary>
    public static readonly TypeRegistry Generated = CreateGeneratedRegistry();

    private TypeRegistry() { }

    /// <summary>
    /// Registers a Thrift type with its universal name.
    /// </summary>
    public void RegisterType(string universalName, Type type, SerializationProtocol defaultProtocol = SerializationProtocol.Compact)
    {
        ArgumentNullException.ThrowIfNull(universalName);
        ArgumentNullException.ThrowIfNull(type);

        var info = new TypeInfo
        {
            UniversalName = universalName,
            Type = type,
            DefaultProtocol = defaultProtocol,
            TypeStruct = BuildTypeStruct(type)
        };

        _types[universalName] = info;
    }

    /// <summary>
    /// Registers a custom serializer for a protocol.
    /// </summary>
    public void RegisterSerializer(string protocolName, Func<object, byte[]> serializer)
    {
        _serializers[protocolName] = new SerializerInfo
        {
            ProtocolName = protocolName,
            Serializer = serializer
        };
    }

    /// <summary>
    /// Registers a custom deserializer for a protocol.
    /// </summary>
    public void RegisterDeserializer(string protocolName, Func<Type, byte[], object> deserializer)
    {
        _deserializers[protocolName] = new DeserializerInfo
        {
            ProtocolName = protocolName,
            Deserializer = deserializer
        };
    }

    /// <summary>
    /// Registers all Thrift types from an assembly.
    /// </summary>
    public void RegisterAssembly(Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
        {
            var attr = type.GetCustomAttribute<UniversalNameAttribute>();
            if (attr != null)
            {
                RegisterType(attr.UniversalName, type);
            }
        }
    }

    /// <summary>
    /// Stores a value into an AnyStruct using the type registry.
    /// </summary>
    public AnyStruct Store<T>(T value, SerializationProtocol protocol = SerializationProtocol.Compact)
        where T : class, IThriftSerializable
    {
        var type = typeof(T);
        var universalName = GetUniversalName(type);

        var typeInfo = _types.TryGetValue(universalName, out var info) ? info :
            new TypeInfo
            {
                UniversalName = universalName,
                Type = type,
                DefaultProtocol = protocol,
                TypeStruct = BuildTypeStruct(type)
            };

        // Serialize using specified protocol
        byte[] data;
        using (var stream = RecyclableStreamManager.GetStream())
        {
            var writer = CreateWriter(protocol, stream);
            value.__fbthrift_write(writer);
            data = stream.ToArray();
        }

        return new AnyStruct
        {
            Type = typeInfo.TypeStruct,
            Protocol = ProtocolUnion.FromStandard(protocol),
            Data = data
        };
    }

    /// <summary>
    /// Loads a value from an AnyStruct using the type registry.
    /// </summary>
    public T Load<T>(AnyStruct anyStruct) where T : class, IThriftSerializable, new()
    {
        var expectedType = typeof(T);
        var expectedName = GetUniversalName(expectedType);

        // Verify type matches
        if (anyStruct.Type?.Name?.Value != expectedName)
        {
            throw new InvalidOperationException(
                $"Type mismatch: expected {expectedName}, got {anyStruct.Type?.Name?.Value ?? "null"}");
        }

        // Check protocol
        var protocol = anyStruct.Protocol?.Standard ?? SerializationProtocol.Compact;

        // Deserialize
        using var stream = new MemoryStream(anyStruct.Data ?? Array.Empty<byte>());
        var reader = CreateReader(protocol, stream);
        var result = new T();
        result.__fbthrift_read(reader);
        return result;
    }

    /// <summary>
    /// Loads a value as ThriftDynamic (for unknown types).
    /// </summary>
    public ThriftDynamic LoadDynamic(AnyStruct anyStruct)
    {
        var protocol = anyStruct.Protocol?.Standard ?? SerializationProtocol.Compact;

        using var stream = new MemoryStream(anyStruct.Data ?? Array.Empty<byte>());
        var reader = CreateReader(protocol, stream);

        // Read as dynamic based on type struct
        if (anyStruct.Type != null)
        {
            return ReadDynamic(reader, anyStruct.Type);
        }

        return default;
    }

    /// <summary>
    /// Tries to find a type by universal name.
    /// </summary>
    public bool TryFindType(string universalName, out Type? type)
    {
        if (_types.TryGetValue(universalName, out var info))
        {
            type = info.Type;
            return true;
        }
        type = null;
        return false;
    }

    /// <summary>
    /// Tries to find a type by .NET Type.
    /// </summary>
    public bool TryFindByType(Type type, out string? universalName)
    {
        var name = GetUniversalName(type);
        universalName = _types.ContainsKey(name) ? name : null;
        return universalName != null;
    }

    /// <summary>
    /// Gets all registered universal names.
    /// </summary>
    public IReadOnlyCollection<string> GetRegisteredNames() => _types.Keys.ToList();

    /// <summary>
    /// Creates a generated registry from all loaded assemblies.
    /// </summary>
    private static TypeRegistry CreateGeneratedRegistry()
    {
        var registry = new TypeRegistry();

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                registry.RegisterAssembly(assembly);
            }
            catch
            {
                // Ignore assemblies that can't be scanned
            }
        }

        return registry;
    }

    private static string GetUniversalName(Type type)
    {
        var attr = type.GetCustomAttribute<UniversalNameAttribute>();
        if (attr != null) return attr.UniversalName;

        // Infer from namespace and type name
        var ns = type.Namespace?.Replace('.', '/') ?? "unknown";
        return UniversalNames.ForStruct(ns, type.Name);
    }

    private static TypeStruct BuildTypeStruct(Type type)
    {
        // Build TypeStruct from type metadata
        // This is a simplified version - real implementation would use thrift/lib/thrift/type_rep.thrift types
        return new TypeStruct
        {
            Name = new TypeName { Value = GetUniversalName(type) },
            // Params would be filled for container types
        };
    }

    private static FBThrift.IThriftProtocolWriter CreateWriter(SerializationProtocol protocol, Stream stream)
    {
        return protocol switch
        {
            SerializationProtocol.Binary => new ThriftBinaryWriter(stream),
            SerializationProtocol.Compact => new ThriftCompactWriter(stream),
            _ => new ThriftCompactWriter(stream)
        };
    }

    private static FBThrift.IThriftProtocolReader CreateReader(SerializationProtocol protocol, Stream stream)
    {
        return protocol switch
        {
            SerializationProtocol.Binary => new ThriftBinaryReader(stream),
            SerializationProtocol.Compact => new ThriftCompactReader(stream),
            _ => new ThriftCompactReader(stream)
        };
    }

    private static ThriftDynamic ReadDynamic(FBThrift.IThriftProtocolReader reader, TypeStruct typeStruct)
    {
        // Simplified - would need full type-aware reading
        return default;
    }
}

/// <summary>
/// Type information stored in the registry.
/// </summary>
internal sealed class TypeInfo
{
    public string UniversalName { get; init; } = string.Empty;
    public Type Type { get; init; } = typeof(void);
    public SerializationProtocol DefaultProtocol { get; init; }
    public TypeStruct TypeStruct { get; init; } = new();
}

/// <summary>
/// Custom serializer info.
/// </summary>
internal sealed class SerializerInfo
{
    public string ProtocolName { get; init; } = string.Empty;
    public Func<object, byte[]> Serializer { get; init; } = _ => Array.Empty<byte>();
}

/// <summary>
/// Custom deserializer info.
/// </summary>
internal sealed class DeserializerInfo
{
    public string ProtocolName { get; init; } = string.Empty;
    public Func<Type, byte[], object> Deserializer { get; init; } = (_, _) => null!;
}

/// <summary>
/// Serialization protocol enumeration.
/// </summary>
public enum SerializationProtocol
{
    Binary,
    Compact,
    Json,
    SimpleJson,
    Custom
}

/// <summary>
/// Thrift Any struct (matches thrift/lib/thrift/any.thrift).
/// </summary>
public sealed class AnyStruct
{
    public TypeStruct Type { get; set; } = new();
    public ProtocolUnion Protocol { get; set; } = new();
    public byte[] Data { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// Thrift SemiAny struct (matches thrift/lib/thrift/any.thrift).
/// </summary>
public sealed class SemiAnyStruct
{
    public TypeStruct? Type { get; set; }
    public ProtocolUnion? Protocol { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Promotes SemiAny to Any by providing missing type/protocol.
    /// </summary>
    public AnyStruct Promote(TypeStruct? type = null, ProtocolUnion? protocol = null)
    {
        if (Type == null && type == null)
            throw new InvalidOperationException("Type is required for promotion");
        if (Protocol == null && protocol == null)
            throw new InvalidOperationException("Protocol is required for promotion");

        return new AnyStruct
        {
            Type = Type ?? type!,
            Protocol = Protocol ?? protocol!,
            Data = Data
        };
    }
}

/// <summary>
/// Protocol union (standard or custom).
/// </summary>
public sealed class ProtocolUnion
{
    public SerializationProtocol Standard { get; private set; }
    public string? Custom { get; private set; }

    public static ProtocolUnion FromStandard(SerializationProtocol protocol)
    {
        return new ProtocolUnion { Standard = protocol };
    }

    public static ProtocolUnion FromCustom(string customProtocol)
    {
        return new ProtocolUnion { Custom = customProtocol };
    }
}

/// <summary>
/// Type struct (simplified from thrift/lib/thrift/type_rep.thrift).
/// </summary>
public sealed class TypeStruct
{
    public TypeName Name { get; set; } = new();
    public IReadOnlyList<TypeStruct> Params { get; set; } = Array.Empty<TypeStruct>();
}

/// <summary>
/// Type name (simplified from thrift/lib/thrift/type_rep.thrift).
/// </summary>
public sealed class TypeName
{
    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// Lazy Any implementation (similar to Java's LazyAny).
/// Defers deserialization until value is accessed.
/// </summary>
public abstract class LazyAny<T> where T : class, IThriftSerializable, new()
{
    private static readonly ConcurrentDictionary<string, Func<object, byte[]>> _serializers = new();
    private static readonly ConcurrentDictionary<string, Func<Type, byte[], object>> _deserializers = new();

    public static void RegisterSerializer(string name, Func<object, byte[]> serializer)
    {
        _serializers[name] = serializer;
    }

    public static void RegisterDeserializer(string name, Func<Type, byte[], object> deserializer)
    {
        _deserializers[name] = deserializer;
    }

    public static LazyAny<T> Wrap(AnyStruct any)
    {
        return new SerializedLazyAny<T>(any);
    }

    public abstract T Get();
    public abstract AnyStruct GetAny();

    public sealed class Builder
    {
        private T _value;
        private string? _customProtocol;
        private SerializationProtocol _protocol = SerializationProtocol.Compact;
        private Func<object, byte[]>? _serializer;
        private bool _useHashPrefix = true;
        private bool _useUri;
        private int _hashBytes = 8;

        public Builder() { }
        public Builder(T value) { _value = value; }

        public Builder SetValue(T value) { _value = value; return this; }
        public Builder SetProtocol(SerializationProtocol protocol) { _protocol = protocol; return this; }
        public Builder SetCustomProtocol(string customProtocol) { _customProtocol = customProtocol; return this; }
        public Builder UseHashPrefix() { _useHashPrefix = true; return this; }
        public Builder UseHashPrefix(int bytes) { _useHashPrefix = true; _hashBytes = bytes; return this; }
        public Builder UseUri() { _useUri = true; return this; }

        public LazyAny<T> Build()
        {
            ArgumentNullException.ThrowIfNull(_value);

            if (_useHashPrefix && _useUri)
                throw new InvalidOperationException("Must set either useHashPrefix or useUri, not both");

            if (!_useHashPrefix && !_useUri)
            {
                // Auto-detect based on type preference
                var universalName = TypeRegistry.Generated.TryFindByType(typeof(T), out var name) ? name : null;
                // Simplified - would check TypeRegistry for preference
                _useHashPrefix = true;
            }

            if (!string.IsNullOrEmpty(_customProtocol) && !_serializers.ContainsKey(_customProtocol))
                throw new ArgumentException($"Custom protocol '{_customProtocol}' not registered");

            if (string.IsNullOrEmpty(_customProtocol))
            {
                if (_serializer == null)
                {
                    // Generate serializer for ThriftSerializable
                    if (_value is IThriftSerializable thrift)
                    {
                        _serializer = obj =>
                        {
                            using var stream = RecyclableStreamManager.GetStream();
                            var writer = CreateWriter(_protocol, stream);
                            ((IThriftSerializable)obj).__fbthrift_write(writer);
                            return stream.ToArray();
                        };
                    }
                    else
                    {
                        throw new InvalidOperationException("Serializer required for non-ThriftSerializable types");
                    }
                }
            }

            var typeInfo = TypeRegistry.Generated.TryFindByType(typeof(T), out var un) ?
                new TypeInfo { UniversalName = un, Type = typeof(T), DefaultProtocol = _protocol } :
                new TypeInfo { UniversalName = un ?? "unknown", Type = typeof(T), DefaultProtocol = _protocol };

            var standardProtocol = string.IsNullOrEmpty(_customProtocol) ? _protocol : SerializationProtocol.Custom;

            return new UnserializedLazyAny<T>(
                _value, _customProtocol, standardProtocol, typeInfo, _serializer!,
                _useHashPrefix, _useUri, _hashBytes);
        }

        private static FBThrift.IThriftProtocolWriter CreateWriter(SerializationProtocol protocol, Stream stream)
        {
            return protocol switch
            {
                SerializationProtocol.Binary => new ThriftBinaryWriter(stream),
                SerializationProtocol.Compact => new ThriftCompactWriter(stream),
                _ => new ThriftCompactWriter(stream)
            };
        }
    }
}

/// <summary>
/// Unserialized LazyAny - holds value and serializes on demand.
/// </summary>
internal sealed class UnserializedLazyAny<T> : LazyAny<T> where T : class, IThriftSerializable, new()
{
    private readonly T _value;
    private readonly string? _customProtocol;
    private readonly SerializationProtocol _standardProtocol;
    private readonly TypeInfo _typeInfo;
    private readonly Func<object, byte[]> _serializer;
    private readonly bool _useHashPrefix;
    private readonly bool _useUri;
    private readonly int _hashBytes;
    private AnyStruct? _cachedAny;

    public UnserializedLazyAny(
        T value, string? customProtocol, SerializationProtocol standardProtocol,
        TypeInfo typeInfo, Func<object, byte[]> serializer,
        bool useHashPrefix, bool useUri, int hashBytes)
    {
        _value = value;
        _customProtocol = customProtocol;
        _standardProtocol = standardProtocol;
        _typeInfo = typeInfo;
        _serializer = serializer;
        _useHashPrefix = useHashPrefix;
        _useUri = useUri;
        _hashBytes = hashBytes;
    }

    public override T Get() => _value;

    public override AnyStruct GetAny()
    {
        if (_cachedAny != null) return _cachedAny;

        var data = _serializer(_value);
        var protocol = string.IsNullOrEmpty(_customProtocol)
            ? ProtocolUnion.FromStandard(_standardProtocol)
            : ProtocolUnion.FromCustom(_customProtocol);

        _cachedAny = new AnyStruct
        {
            Type = _typeInfo.TypeStruct,
            Protocol = protocol,
            Data = data
        };

        return _cachedAny;
    }
}

/// <summary>
/// Serialized LazyAny - wraps an existing AnyStruct.
/// </summary>
internal sealed class SerializedLazyAny<T> : LazyAny<T> where T : class, IThriftSerializable, new()
{
    private readonly AnyStruct _any;
    private T? _cachedValue;

    public SerializedLazyAny(AnyStruct any)
    {
        _any = any ?? throw new ArgumentNullException(nameof(any));
    }

    public override T Get()
    {
        if (_cachedValue != null) return _cachedValue;

        var registry = TypeRegistry.Generated;
        _cachedValue = registry.Load<T>(_any);
        return _cachedValue;
    }

    public override AnyStruct GetAny() => _any;
}

/// <summary>
/// Lazy deserialization flags (similar to C++ LazyDeserializationFlags).
/// </summary>
public static class LazyDeserializationFlags
{
    private static bool _enabled = true;
    private static bool _disabledDueToChecksumMismatch = false;

    /// <summary>
    /// Whether lazy deserialization is enabled globally.
    /// </summary>
    public static bool Enabled => _enabled && !_disabledDueToChecksumMismatch;

    /// <summary>
    /// Enables or disables lazy deserialization globally.
    /// </summary>
    public static void SetEnabled(bool enabled)
    {
        _enabled = enabled;
    }

    /// <summary>
    /// Disables lazy deserialization due to checksum mismatch (security).
    /// </summary>
    public static void DisableDueToChecksumMismatch()
    {
        _disabledDueToChecksumMismatch = true;
    }

    /// <summary>
    /// Resets the checksum mismatch flag (e.g., on new connection).
    /// </summary>
    public static void ResetChecksumMismatch()
    {
        _disabledDueToChecksumMismatch = false;
    }
}