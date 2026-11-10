namespace FbThrift.Tests;

using System.Buffers.Binary;
using FbThrift.Core;
using FbThrift.Core.Protocol;

public class ProtocolTests
{
    private static byte[] RoundTripCompact(Action<ITProtocolWriter> write, Action<ITProtocolReader> verify)
    {
        var buffer = new GrowableBufferWriter();
        var writer = new CompactProtocolWriter(buffer);
        write(writer);
        byte[] bytes = buffer.ToArray();

        var reader = new CompactProtocolReader(bytes);
        verify(reader);
        reader.Dispose();
        return bytes;
    }

    [Fact]
    public void Compact_WriteAndReadScalarFields()
    {
        RoundTripCompact(
            w =>
            {
                w.WriteMessageBegin("add", TMessageType.Call, 1);
                w.WriteStructBegin("Args");
                w.WriteFieldBegin(TType.I32, 1);
                w.WriteI32(42);
                w.WriteFieldEnd();
                w.WriteFieldBegin(TType.String, 2);
                w.WriteString("hello");
                w.WriteFieldEnd();
                w.WriteFieldBegin(TType.Bool, 3);
                w.WriteBool(true);
                w.WriteFieldEnd();
                w.WriteStop();
                w.WriteStructEnd();
                w.WriteMessageEnd();
            },
            r =>
            {
                var (name, msgType, seqId) = r.ReadMessageBegin();
                Assert.Equal("add", name);
                Assert.Equal(TMessageType.Call, msgType);
                Assert.Equal(1, seqId);

                Assert.Null(r.ReadStructBegin());

                var (t1, id1) = r.ReadFieldBegin();
                Assert.Equal(TType.I32, t1);
                Assert.Equal((short)1, id1);
                Assert.Equal(42, r.ReadI32());
                r.ReadFieldEnd();

                var (t2, id2) = r.ReadFieldBegin();
                Assert.Equal(TType.String, t2);
                Assert.Equal((short)2, id2);
                Assert.Equal("hello", r.ReadString());
                r.ReadFieldEnd();

                var (t3, id3) = r.ReadFieldBegin();
                Assert.Equal(TType.Bool, t3);
                Assert.Equal((short)3, id3);
                Assert.True(r.ReadBool());
                r.ReadFieldEnd();

                var (tStop, idStop) = r.ReadFieldBegin();
                Assert.Equal(TType.Stop, tStop);
                Assert.Equal((short)0, idStop);

                r.ReadStructEnd();
                r.ReadMessageEnd();
            });
    }

    [Fact]
    public void Compact_WriteAndReadListMapSet()
    {
        RoundTripCompact(
            w =>
            {
                w.WriteMessageBegin("batch", TMessageType.Call, 7);
                w.WriteStructBegin("Batch");
                w.WriteFieldBegin(TType.List, 1);
                w.WriteListBegin(TType.I32, 3);
                w.WriteI32(1);
                w.WriteI32(2);
                w.WriteI32(3);
                w.WriteListEnd();
                w.WriteFieldEnd();
                w.WriteFieldBegin(TType.Map, 2);
                w.WriteMapBegin(TType.String, TType.I32, 2);
                w.WriteString("a");
                w.WriteI32(10);
                w.WriteString("b");
                w.WriteI32(20);
                w.WriteMapEnd();
                w.WriteFieldEnd();
                w.WriteStop();
                w.WriteStructEnd();
                w.WriteMessageEnd();
            },
            r =>
            {
                r.ReadMessageBegin();
                r.ReadStructBegin();

                var (t1, id1) = r.ReadFieldBegin();
                Assert.Equal(TType.List, t1);
                Assert.Equal((short)1, id1);
                var (elem1, size1) = r.ReadListBegin();
                Assert.Equal(TType.I32, elem1);
                Assert.Equal(3, size1);
                Assert.Equal(1, r.ReadI32());
                Assert.Equal(2, r.ReadI32());
                Assert.Equal(3, r.ReadI32());
                r.ReadListEnd();
                r.ReadFieldEnd();

                var (t2, id2) = r.ReadFieldBegin();
                Assert.Equal(TType.Map, t2);
                Assert.Equal((short)2, id2);
                var (kt, vt, msize) = r.ReadMapBegin();
                Assert.Equal(TType.String, kt);
                Assert.Equal(TType.I32, vt);
                Assert.Equal(2, msize);
                Assert.Equal("a", r.ReadString());
                Assert.Equal(10, r.ReadI32());
                Assert.Equal("b", r.ReadString());
                Assert.Equal(20, r.ReadI32());
                r.ReadMapEnd();
                r.ReadFieldEnd();

                var (tStop, idStop) = r.ReadFieldBegin();
                Assert.Equal(TType.Stop, tStop);

                r.ReadStructEnd();
                r.ReadMessageEnd();
            });
    }

    [Fact]
    public void Compact_FieldIdEscape_UsesVarintForLargeFieldId()
    {
        var buffer = new GrowableBufferWriter();
        var writer = new CompactProtocolWriter(buffer);
        writer.WriteMessageBegin("big", TMessageType.Call, 0);
        writer.WriteStructBegin("S");
        // field id 100 escapes the 4-bit field-id nibble
        writer.WriteFieldBegin(TType.I32, 100);
        writer.WriteI32(999);
        writer.WriteFieldEnd();
        writer.WriteStop();
        writer.WriteStructEnd();
        writer.WriteMessageEnd();
        writer.Flush();

        byte[] bytes = buffer.ToArray();
        var reader = new CompactProtocolReader(bytes);
        reader.ReadMessageBegin();
        reader.ReadStructBegin();
        var (t, id) = reader.ReadFieldBegin();
        Assert.Equal(TType.I32, t);
        Assert.Equal((short)100, id);
        Assert.Equal(999, reader.ReadI32());
        reader.ReadFieldEnd();
        var (tStop, idStop) = reader.ReadFieldBegin();
        Assert.Equal(TType.Stop, tStop);
        reader.ReadStructEnd();
        reader.ReadMessageEnd();
        reader.Dispose();
    }
}
