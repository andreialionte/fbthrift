namespace FbThrift.Tests;

public class VarintTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(100)]
    [InlineData(-100)]
    [InlineData(16383)]
    [InlineData(-16383)]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    public void RoundTrip(long value)
    {
        Span<byte> buf = stackalloc byte[16];
        int written = FbThrift.Core.Varint.Encode(value, buf);
        int read = FbThrift.Core.Varint.Decode(buf.Slice(0, written), out long decoded);
        Assert.Equal(written, read);
        Assert.Equal(value, decoded);
    }
}

public class ProtocolRoundTripTests
{
    [Fact]
    public void WriteAndReadFields()
    {
        var bufWriter = new GrowableBufferWriter();
        var writer = new FbThrift.Core.TProtocolWriter(bufWriter);

        writer.WriteFieldHeader(FbThrift.Core.TType.I32, 1);
        writer.WriteI32(42);
        writer.WriteFieldHeader(FbThrift.Core.TType.String, 2);
        writer.WriteString("hello");
        writer.WriteFieldHeader(FbThrift.Core.TType.Stop, 0);

        var bytes = bufWriter.ToArray();
        var reader = new FbThrift.Core.TProtocolReader(bytes);

        Assert.Equal(FbThrift.Core.TType.I32, reader.ReadFieldType());
        Assert.Equal((short)1, reader.ReadFieldId());
        Assert.Equal(42, reader.ReadI32());
        Assert.Equal(FbThrift.Core.TType.String, reader.ReadFieldType());
        Assert.Equal((short)2, reader.ReadFieldId());
        Assert.Equal("hello", reader.ReadString());
        Assert.Equal(FbThrift.Core.TType.Stop, reader.ReadFieldType());
        Assert.Equal((short)0, reader.ReadFieldId());
    }
}

public sealed class GrowableBufferWriter : System.Buffers.IBufferWriter<byte>
{
    private byte[] _buffer = Array.Empty<byte>();
    private int _position;

    public void Advance(int count) => _position += count;

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer.AsMemory(_position, sizeHint > 0 ? sizeHint : _buffer.Length - _position);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer.AsSpan(_position, sizeHint > 0 ? sizeHint : _buffer.Length - _position);
    }

    public byte[] ToArray()
    {
        var arr = new byte[_position];
        Buffer.BlockCopy(_buffer, 0, arr, 0, _position);
        return arr;
    }

    private void Ensure(int sizeHint)
    {
        int needed = _position + Math.Max(sizeHint, 1);
        if (_buffer.Length < needed)
        {
            int newSize = _buffer.Length == 0 ? 16 : _buffer.Length;
            while (newSize < needed) newSize *= 2;
            Array.Resize(ref _buffer, newSize);
        }
    }
}
