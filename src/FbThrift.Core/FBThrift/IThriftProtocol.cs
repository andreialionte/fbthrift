using System;
using System.IO;

namespace FBThrift;

public interface IThriftProtocolWriter
{
    void WriteFieldBegin(ThriftWireType fieldType, short fieldId);
    void WriteFieldStop();
    void WriteFieldEnd();
    void WriteBool(bool value);
    void WriteByte(sbyte value);
    void WriteI16(short value);
    void WriteI32(int value);
    void WriteI64(long value);
    void WriteFloat(float value);
    void WriteDouble(double value);
    void WriteString(string value);
    void WriteBinary(byte[] value);
    void WriteListBegin(ThriftWireType elemType, int size);
    void WriteListEnd();
    void WriteSetBegin(ThriftWireType elemType, int size);
    void WriteSetEnd();
    void WriteMapBegin(ThriftWireType keyType, ThriftWireType valType, int size);
    void WriteMapEnd();
    void WriteStructBegin(string? name);
    void WriteStructEnd();
    void WriteStruct<T>(T value) where T : IThriftSerializable => value.__fbthrift_write(this);
}

public interface IThriftProtocolReader
{
    (ThriftWireType fieldType, short fieldId) ReadFieldBegin();
    void ReadFieldEnd();
    void ReadStructBegin();
    void ReadStructEnd();
    bool ReadBool();
    sbyte ReadByte();
    short ReadI16();
    int ReadI32();
    long ReadI64();
    float ReadFloat();
    double ReadDouble();
    string ReadString();
    byte[] ReadBinary();
    (ThriftWireType elemType, int size) ReadListBegin();
    void ReadListEnd();
    (ThriftWireType elemType, int size) ReadSetBegin();
    void ReadSetEnd();
    (ThriftWireType keyType, ThriftWireType valType, int size) ReadMapBegin();
    void ReadMapEnd();
    T ReadStruct<T>() where T : IThriftSerializable, new()
    {
        var result = new T();
        result.__fbthrift_read(this);
        return result;
    }
}
