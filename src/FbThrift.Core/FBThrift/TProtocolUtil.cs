using System;
using System.Buffers.Binary;
using System.IO;

namespace FBThrift;

public static class TProtocolUtil
{
    /// <summary>
    /// Caps the initial capacity that generated decoders reserve from a wire-supplied container element count.
    /// </summary>
    public static int PreallocSize(int count)
    {
        // Default max prealloc size (matches fbthrift default)
        const int MaxPreallocSize = 64;
        if (count <= 0) return 0;
        if (count >= MaxPreallocSize) return MaxPreallocSize;
        return count;
    }

    /// <summary>
    /// Skips a field of the given type, enforcing a depth limit.
    /// </summary>
    public static void Skip(this IThriftProtocolReader reader, ThriftWireType fieldType, int remainingDepth = 64)
    {
        if (remainingDepth <= 0)
            throw new ThriftProtocolException("Maximum nesting depth exceeded while skipping");

        switch (fieldType)
        {
            case ThriftWireType.Bool:
                reader.ReadBool();
                break;
            case ThriftWireType.Byte:
                reader.ReadByte();
                break;
            case ThriftWireType.I16:
                reader.ReadI16();
                break;
            case ThriftWireType.I32:
                reader.ReadI32();
                break;
            case ThriftWireType.I64:
                reader.ReadI64();
                break;
            case ThriftWireType.Float:
                reader.ReadFloat();
                break;
            case ThriftWireType.Double:
                reader.ReadDouble();
                break;
            case ThriftWireType.String:
                reader.ReadBinary(); // Avoid UTF-8 decoding issues with binary data
                break;
            case ThriftWireType.Struct:
                SkipStruct(reader, remainingDepth - 1);
                break;
            case ThriftWireType.Map:
                SkipMap(reader, remainingDepth - 1);
                break;
            case ThriftWireType.Set:
            case ThriftWireType.List:
                SkipListOrSet(reader, remainingDepth - 1);
                break;
            default:
                throw new ThriftProtocolException($"Unknown field type: {fieldType}");
        }
    }

    private static void SkipStruct(IThriftProtocolReader reader, int remainingDepth)
    {
        while (true)
        {
            var (fieldType, fieldId) = reader.ReadFieldBegin();
            if (fieldType == ThriftWireType.Stop) break;
            reader.Skip(fieldType, remainingDepth);
        }
        reader.ReadFieldEnd();
    }

    private static void SkipMap(IThriftProtocolReader reader, int remainingDepth)
    {
        var (keyType, valType, size) = reader.ReadMapBegin();
        for (int i = 0; i < size; i++)
        {
            reader.Skip(keyType, remainingDepth);
            reader.Skip(valType, remainingDepth);
        }
        reader.ReadMapEnd();
    }

    private static void SkipListOrSet(IThriftProtocolReader reader, int remainingDepth)
    {
        var (elemType, size) = reader.ReadListBegin();
        for (int i = 0; i < size; i++)
        {
            reader.Skip(elemType, remainingDepth);
        }
        reader.ReadListEnd();
    }
}