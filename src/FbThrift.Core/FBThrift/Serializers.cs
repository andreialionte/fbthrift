using System;
using System.IO;

namespace FBThrift;

public static class BinarySerializer
{
    public static byte[] Serialize<T>(T obj) where T : IThriftSerializable
    {
        return ThriftSerializer.EncodeBinary(obj);
    }

    public static T Deserialize<T>(byte[] data) where T : IThriftSerializable, new()
    {
        return ThriftSerializer.DecodeBinary<T>(data);
    }
}

public static class CompactSerializer
{
    public static byte[] Serialize<T>(T obj) where T : IThriftSerializable
    {
        return ThriftSerializer.EncodeCompact(obj);
    }

    public static T Deserialize<T>(byte[] data) where T : IThriftSerializable, new()
    {
        return ThriftSerializer.DecodeCompact<T>(data);
    }
}
