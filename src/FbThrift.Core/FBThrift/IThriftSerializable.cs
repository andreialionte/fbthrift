namespace FBThrift;

public interface IThriftSerializable
{
    void __fbthrift_write(IThriftProtocolWriter writer);
    void __fbthrift_read(IThriftProtocolReader reader);
}
