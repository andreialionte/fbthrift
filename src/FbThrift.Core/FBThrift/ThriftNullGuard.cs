#nullable enable
using System;

namespace FBThrift;

public static class ThriftNullGuard
{
    public static void ThrowIfNull(object? value, string fieldName)
    {
        if (value == null)
            throw new ArgumentNullException(nameof(value), $"Struct field '{fieldName}' is non-nullable and cannot be set to null");
    }

    public static void ThrowIfNullUnion<T>(T? value, string fieldName) where T : class
    {
        if (value == null)
            throw new ArgumentNullException(nameof(value), $"Union field '{fieldName}' cannot be set to null");
    }

    public static void ThrowIfNullUnion<T>(T? value, string fieldName) where T : struct
    {
        if (!value.HasValue)
            throw new ArgumentNullException(nameof(value), $"Union field '{fieldName}' cannot be set to null");
    }
}
