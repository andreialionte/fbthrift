namespace FbThrift.Core.Protocol;

/// <summary>
/// Reads Thrift protocol values using a specific wire encoding (Binary, Compact, etc.).
/// </summary>
public interface ITProtocolReader : IDisposable
{
    /// <summary>
    /// Reads the beginning of a message header.
    /// </summary>
    /// <returns>The message name, type, and sequence identifier.</returns>
    (string Name, TMessageType MessageType, int SeqId) ReadMessageBegin();

    /// <summary>
    /// Reads the end of a message header.
    /// </summary>
    void ReadMessageEnd();

    /// <summary>
    /// Reads the beginning of a struct.
    /// </summary>
    /// <returns>The name of the struct.</returns>
    string? ReadStructBegin();

    /// <summary>
    /// Reads the end of a struct.
    /// </summary>
    void ReadStructEnd();

    /// <summary>
    /// Reads the beginning of a field.
    /// </summary>
    /// <returns>The field type and field identifier.</returns>
    (TType Type, short FieldId) ReadFieldBegin();

    /// <summary>
    /// Reads the end of a field.
    /// </summary>
    void ReadFieldEnd();

    /// <summary>
    /// Reads the beginning of a map.
    /// </summary>
    /// <returns>The key type, value type, and size.</returns>
    (TType KeyType, TType ValueType, int Size) ReadMapBegin();

    /// <summary>
    /// Reads the end of a map.
    /// </summary>
    void ReadMapEnd();

    /// <summary>
    /// Reads the beginning of a list.
    /// </summary>
    /// <returns>The element type and size.</returns>
    (TType ElementType, int Size) ReadListBegin();

    /// <summary>
    /// Reads the end of a list.
    /// </summary>
    void ReadListEnd();

    /// <summary>
    /// Reads the beginning of a set.
    /// </summary>
    /// <returns>The element type and size.</returns>
    (TType ElementType, int Size) ReadSetBegin();

    /// <summary>
    /// Reads the end of a set.
    /// </summary>
    void ReadSetEnd();

    /// <summary>
    /// Reads a boolean value.
    /// </summary>
    /// <returns>The value read.</returns>
    bool ReadBool();

    /// <summary>
    /// Reads a single byte.
    /// </summary>
    /// <returns>The byte read.</returns>
    byte ReadByte();

    /// <summary>
    /// Reads a 16-bit signed integer.
    /// </summary>
    /// <returns>The value read.</returns>
    short ReadI16();

    /// <summary>
    /// Reads a 32-bit signed integer.
    /// </summary>
    /// <returns>The value read.</returns>
    int ReadI32();

    /// <summary>
    /// Reads a 64-bit signed integer.
    /// </summary>
    /// <returns>The value read.</returns>
    long ReadI64();

    /// <summary>
    /// Reads a 64-bit IEEE 754 floating-point value.
    /// </summary>
    /// <returns>The value read.</returns>
    double ReadDouble();

    /// <summary>
    /// Reads a 32-bit IEEE 754 floating-point value.
    /// </summary>
    /// <returns>The value read.</returns>
    float ReadFloat();

    /// <summary>
    /// Reads a UTF-8 encoded string.
    /// </summary>
    /// <returns>The string read.</returns>
    string ReadString();

    /// <summary>
    /// Reads a raw byte sequence (binary).
    /// </summary>
    /// <returns>The bytes read.</returns>
    ReadOnlyMemory<byte> ReadBinary();
}
