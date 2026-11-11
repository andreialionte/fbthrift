namespace FbThrift.Core.Protocol;

using FBThrift;

/// <summary>
/// Adapter to wrap FBThrift.IThriftProtocolReader as FbThrift.Core.Protocol.ITProtocolReader.
/// </summary>
public sealed class FBThriftProtocolReaderAdapter : ITProtocolReader
{
    private readonly FBThrift.IThriftProtocolReader _inner;

    public FBThriftProtocolReaderAdapter(FBThrift.IThriftProtocolReader inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public (string Name, TMessageType MessageType, int SeqId) ReadMessageBegin()
    {
        // FBThrift reader doesn't have ReadMessageBegin, we need to simulate
        throw new NotSupportedException("FBThrift reader doesn't support ReadMessageBegin");
    }

    public void ReadMessageEnd()
    {
        // No-op for FBThrift
    }

    public string? ReadStructBegin()
    {
        _inner.ReadStructBegin();
        return null;
    }

    public void ReadStructEnd()
    {
        _inner.ReadStructEnd();
    }

    public (TType Type, short FieldId) ReadFieldBegin()
    {
        var (wireType, fieldId) = _inner.ReadFieldBegin();
        return ((TType)wireType, fieldId);
    }

    public void ReadFieldEnd()
    {
        _inner.ReadFieldEnd();
    }

    public (TType KeyType, TType ValueType, int Size) ReadMapBegin()
    {
        var (keyType, valType, size) = _inner.ReadMapBegin();
        return ((TType)keyType, (TType)valType, size);
    }

    public void ReadMapEnd()
    {
        _inner.ReadMapEnd();
    }

    public (TType ElementType, int Size) ReadListBegin()
    {
        var (elemType, size) = _inner.ReadListBegin();
        return ((TType)elemType, size);
    }

    public void ReadListEnd()
    {
        _inner.ReadListEnd();
    }

    public (TType ElementType, int Size) ReadSetBegin()
    {
        var (elemType, size) = _inner.ReadSetBegin();
        return ((TType)elemType, size);
    }

    public void ReadSetEnd()
    {
        _inner.ReadSetEnd();
    }

    public bool ReadBool() => _inner.ReadBool();
    public byte ReadByte() => unchecked((byte)_inner.ReadByte());
    public short ReadI16() => _inner.ReadI16();
    public int ReadI32() => _inner.ReadI32();
    public long ReadI64() => _inner.ReadI64();
    public double ReadDouble() => _inner.ReadDouble();
    public float ReadFloat() => _inner.ReadFloat();
    public string ReadString() => _inner.ReadString();
    public ReadOnlyMemory<byte> ReadBinary() => _inner.ReadBinary();

    public void Dispose() => _inner.Dispose();
}