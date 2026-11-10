namespace FBThrift;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

/// <summary>
/// Type registry for Thrift Any deserialization.
/// Maps universal names to concrete IThriftSerializable types.
/// </summary>
public sealed class TypeRegistry
{
    private readonly ConcurrentDictionary<UniversalName, Type> _typeMap = new();
    private readonly ConcurrentDictionary<Type, UniversalName> _reverseMap = new();
    private readonly ConcurrentDictionary<string, Func<IThriftProtocolReader, object>> _deserializerCache = new();

    /// <summary>
    /// Registers a Thrift serializable type with its universal name.
    /// </summary>
    /// <typeparam name="T">The Thrift serializable type.</typeparam>
    /// <param name="universalName">The universal name of the type.</param>
    public void Register<T>(UniversalName universalName) where T : IThriftSerializable, new()
    {
        _typeMap[universalName] = typeof(T);
        _reverseMap[typeof(T)] = universalName;
    }

    /// <summary>
    /// Registers a Thrift serializable type with a string universal name.
    /// </summary>
    /// <typeparam name="T">The Thrift serializable type.</typeparam>
    /// <param name="universalName">The universal name string.</param>
    public void Register<T>(string universalName) where T : IThriftSerializable, new()
    {
        Register<T>(new UniversalName(universalName));
    }

    /// <summary>
    /// Tries to get the type for a universal name.
    /// </summary>
    public bool TryGetType(UniversalName universalName, out Type? type)
    {
        return _typeMap.TryGetValue(universalName, out type);
    }

    /// <summary>
    /// Tries to get the universal name for a type.
    /// </summary>
    public bool TryGetUniversalName(Type type, out UniversalName universalName)
    {
        return _reverseMap.TryGetValue(type, out universalName);
    }

    /// <summary>
    /// Creates an instance of the type for a universal name and deserializes it.
    /// </summary>
    public IThriftSerializable Deserialize(UniversalName universalName, IThriftProtocolReader reader)
    {
        if (!_typeMap.TryGetValue(universalName, out var type))
        {
            throw new KeyNotFoundException($"Type not registered for universal name: {universalName}");
        }

        var instance = Activator.CreateInstance(type) as IThriftSerializable
            ?? throw new InvalidOperationException($"Failed to create instance of {type}");

        instance.__fbthrift_read(reader);
        return instance;
    }

    /// <summary>
    /// Gets the global default type registry (singleton).
    /// </summary>
    public static TypeRegistry Global { get; } = new TypeRegistry();

    private TypeRegistry() { }
}