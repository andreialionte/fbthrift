#nullable enable
using System;

namespace FBThrift;

public abstract class ThriftStructBase : IThriftSerializable
{
    public abstract void __fbthrift_write(IThriftProtocolWriter writer);
    public abstract void __fbthrift_read(IThriftProtocolReader reader);
    public abstract void __fbthrift_clear();
    public abstract bool __fbthrift_is_empty();
}
