namespace FbThrift.Core.Protocol;

using FBThrift;

/// <summary>
/// Adapter to wrap FBThrift.IThriftProtocolWriter as FbThrift.Core.Protocol.ITProtocolWriter.
/// </summary>
public sealed class FBThriftProtocolWriterAdapter : ITProtocolWriter
{
    private readonly FBThrift.IThriftProtocolWriter _inner;

    public FBThriftProtocolWriterAdapter(FBThrift.IThriftProtocolWriter inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public void WriteMessageBegin(string name, TMessageType messageType, int seqId)
    {
        _inner.WriteStructBegin(name);
    }

    public void WriteMessageEnd()
    {
        _inner.WriteStructEnd();
    }

    public void WriteStructBegin(string? name)
    {
        _inner.WriteStructBegin(name);
    }

    public void WriteStructEnd()
    {
        _inner.WriteStructEnd();
    }

    public void WriteFieldBegin(TType fieldType, short fieldId)
    {
        _inner.WriteFieldBegin((ThriftWireType)fieldType, fieldId);
    }

    public void WriteFieldEnd()
    {
        _inner.WriteFieldEnd();
    }

    public void WriteStop()
    {
        _inner.WriteFieldStop();
    }

    public void WriteBool(bool value) => _inner.WriteBool(value);
    public void WriteByte(byte value) => _inner.WriteByte((sbyte)value);
    public void WriteI16(short value) => _inner.WriteI16(value);
    public void WriteI32(int value) => _inner.WriteI32(value);
    public void WriteI64(long value) => _inner.WriteI64(value);
    public void WriteFloat(float value) => _inner.WriteFloat(value);
    public void WriteDouble(double value) => _inner.WriteDouble(value);
    public void WriteString(string? value) => _inner.WriteString(value ?? "");
    public void WriteBinary(byte[] value) => _inner.WriteBinary(value);
    public void WriteBinary(ReadOnlySpan<byte> value) => _inner.WriteBinary(value.ToArray());
    public void WriteBinary(ReadOnlyMemory<byte> value) => _inner.WriteBinary(value.ToArray());

    public void WriteListBegin(TType elemType, int size)
    {
        _inner.WriteListBegin((ThriftWireType)elemType, size);
    }

    public void WriteListEnd()
    {
        _inner.WriteListEnd();
    }

    public void WriteSetBegin(TType elemType, int size)
    {
        _inner.WriteSetBegin((ThriftWireType)elemType, size);
    }

    public void WriteSetEnd()
    {
        _inner.WriteSetEnd();
    }

    public void WriteMapBegin(TType keyType, TType valType, int size)
    {
        _inner.WriteMapBegin((ThriftWireType)keyType, (ThriftWireType)valType, size);
    }

    public void WriteMapEnd()
    {
        _inner.WriteMapEnd();
    }

    public void WriteFieldStop()
    {
        _inner.WriteFieldStop();
    }

    public void Dispose() => _inner.Dispose();
}