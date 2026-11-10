namespace FbThrift.Tests.Serialization;

using System;
using System.Buffers;
using FbThrift.Core;
using FbThrift.Core.Protocol;
using Xunit;

public class StructSerializationTests
{
    [Fact]
    public void CompactProtocol_RoundTrip_SimpleStruct()
    {
        // Test basic compact protocol serialization
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new CompactProtocolWriter(buffer);
        
        // Write a simple struct
        writer.WriteMessageBegin("test", TMessageType.Call, 1);
        writer.WriteStructBegin("TestStruct");
        writer.WriteFieldBegin(TType.I32, 1);
        writer.WriteI32(42);
        writer.WriteFieldEnd();
        writer.WriteFieldBegin(TType.String, 2);
        writer.WriteString("hello");
        writer.WriteFieldEnd();
        writer.WriteStop();
        writer.WriteStructEnd();
        writer.WriteMessageEnd();
        
        var bytes = buffer.WrittenSpan.ToArray();
        
        // Read back
        var reader = new CompactProtocolReader(bytes);
        (string name, TMessageType msgType, int seqId) = reader.ReadMessageBegin();
        
        Assert.Equal("test", name);
        Assert.Equal(TMessageType.Call, msgType);
        Assert.Equal(1, seqId);
        
        Assert.Null(reader.ReadStructBegin());
        
        (TType t1, short id1) = reader.ReadFieldBegin();
        Assert.Equal(TType.I32, t1);
        Assert.Equal((short)1, id1);
        Assert.Equal(42, reader.ReadI32());
        reader.ReadFieldEnd();
        
        (TType t2, short id2) = reader.ReadFieldBegin();
        Assert.Equal(TType.String, t2);
        Assert.Equal((short)2, id2);
        Assert.Equal("hello", reader.ReadString());
        reader.ReadFieldEnd();
        
        (TType tStop, short idStop) = reader.ReadFieldBegin();
        Assert.Equal(TType.Stop, tStop);
        Assert.Equal((short)0, idStop);
        
        reader.ReadStructEnd();
        reader.ReadMessageEnd();
    }

    [Fact]
    public void CompactProtocol_RoundTrip_Containers()
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new CompactProtocolWriter(buffer);
        
        // Write list
        writer.WriteMessageBegin("test", TMessageType.Call, 1);
        writer.WriteStructBegin("Container");
        writer.WriteFieldBegin(TType.List, 1);
        writer.WriteListBegin(TType.I32, 3);
        writer.WriteI32(1);
        writer.WriteI32(2);
        writer.WriteI32(3);
        writer.WriteListEnd();
        writer.WriteFieldEnd();
        
        // Write map
        writer.WriteFieldBegin(TType.Map, 2);
        writer.WriteMapBegin(TType.String, TType.I32, 2);
        writer.WriteString("a");
        writer.WriteI32(10);
        writer.WriteString("b");
        writer.WriteI32(20);
        writer.WriteMapEnd();
        writer.WriteFieldEnd();
        
        writer.WriteStop();
        writer.WriteStructEnd();
        writer.WriteMessageEnd();
        
        var bytes = buffer.WrittenSpan.ToArray();
        
        // Read back
        var reader = new CompactProtocolReader(bytes);
        reader.ReadMessageBegin();
        reader.ReadStructBegin();
        
        (TType t1, short id1) = reader.ReadFieldBegin();
        Assert.Equal(TType.List, t1);
        Assert.Equal((short)1, id1);
        
        (TType elem1, int size1) = reader.ReadListBegin();
        Assert.Equal(TType.I32, elem1);
        Assert.Equal(3, size1);
        Assert.Equal(1, reader.ReadI32());
        Assert.Equal(2, reader.ReadI32());
        Assert.Equal(3, reader.ReadI32());
        reader.ReadListEnd();
        reader.ReadFieldEnd();
        
        (TType t2, short id2) = reader.ReadFieldBegin();
        Assert.Equal(TType.Map, t2);
        Assert.Equal((short)2, id2);
        
        (TType kt, TType vt, int msize) = reader.ReadMapBegin();
        Assert.Equal(TType.String, kt);
        Assert.Equal(TType.I32, vt);
        Assert.Equal(2, msize);
        Assert.Equal("a", reader.ReadString());
        Assert.Equal(10, reader.ReadI32());
        Assert.Equal("b", reader.ReadString());
        Assert.Equal(20, reader.ReadI32());
        reader.ReadMapEnd();
        reader.ReadFieldEnd();
        
        (TType tStop, short idStop) = reader.ReadFieldBegin();
        Assert.Equal(TType.Stop, tStop);
        
        reader.ReadStructEnd();
        reader.ReadMessageEnd();
    }
}

// Placeholder for generated struct tests
// These will work once the source generator produces code from .thrift files
public class GeneratedStructSerializationTests
{
    [Fact]
    public void Placeholder_GeneratedStructTests_Ready()
    {
        // This test is a placeholder - generated struct tests will be added
        // once the source generator is working correctly
        Assert.True(true);
    }
}