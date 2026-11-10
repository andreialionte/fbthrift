namespace FBThrift;

using FbThrift.Core.Memory;

public static class ThriftSerializer
{
    public static byte[] EncodeBinary<T>(T obj) where T : IThriftSerializable
    {
        if (obj == null) throw new ArgumentNullException(nameof(obj));
        using var ms = RecyclableStreamManager.GetStream();
        var writer = new ThriftBinaryWriter(ms);
        obj.__fbthrift_write(writer);
        return ms.ToArray();
    }

    public static T DecodeBinary<T>(byte[] data) where T : IThriftSerializable, new()
    {
        if (data == null || data.Length == 0) throw new ArgumentException("Cannot deserialize empty data", nameof(data));
        using var ms = new MemoryStream(data);
        var reader = new ThriftBinaryReader(ms);
        var result = new T();
        result.__fbthrift_read(reader);
        return result;
    }

    public static byte[] EncodeCompact<T>(T obj) where T : IThriftSerializable
    {
        if (obj == null) throw new ArgumentNullException(nameof(obj));
        using var ms = RecyclableStreamManager.GetStream();
        var writer = new ThriftCompactWriter(ms);
        obj.__fbthrift_write(writer);
        return ms.ToArray();
    }

    public static T DecodeCompact<T>(byte[] data) where T : IThriftSerializable, new()
    {
        if (data == null || data.Length == 0) throw new ArgumentException("Cannot deserialize empty data", nameof(data));
        using var ms = new MemoryStream(data);
        var reader = new ThriftCompactReader(ms);
        var result = new T();
        result.__fbthrift_read(reader);
        return result;
    }
}

public static class ThriftSerializerExtensions
{
    public static byte[] ToBinary(this IThriftSerializable obj)
        => ThriftSerializer.EncodeBinary(obj);

    public static byte[] ToCompact(this IThriftSerializable obj)
        => ThriftSerializer.EncodeCompact(obj);

    public static T FromBinary<T>(byte[] data) where T : IThriftSerializable, new()
        => ThriftSerializer.DecodeBinary<T>(data);

    public static T FromCompact<T>(byte[] data) where T : IThriftSerializable, new()
        => ThriftSerializer.DecodeCompact<T>(data);
}