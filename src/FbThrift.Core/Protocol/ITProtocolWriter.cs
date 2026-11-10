namespace FbThrift.Core.Protocol;

/// <summary>
/// Writes Thrift protocol values using a specific wire encoding (Binary, Compact, etc.).
/// </summary>
public interface ITProtocolWriter : IDisposable
{
    /// <summary>
    /// Writes the beginning of a message header.
    /// </summary>
    /// <param name="name">The name of the remote procedure.</param>
    /// <param name="messageType">The type of the message.</param>
    /// <param name="seqId">The sequence identifier.</param>
    void WriteMessageBegin(string name, TMessageType messageType, int seqId);

    /// <summary>
    /// Writes the end of a message header.
    /// </summary>
    void WriteMessageEnd();

    /// <summary>
    /// Writes the beginning of a struct.
    /// </summary>
    /// <param name="name">The name of the struct.</param>
    void WriteStructBegin(string? name);

    /// <summary>
    /// Writes the end of a struct.
    /// </summary>
    void WriteStructEnd();

    /// <summary>
    /// Writes the beginning of a field.
    /// </summary>
    /// <param name="type">The field type.</param>
    /// <param name="fieldId">The field identifier.</param>
    void WriteFieldBegin(TType type, short fieldId);

    /// <summary>
    /// Writes the end of a field.
    /// </summary>
    void WriteFieldEnd();

    /// <summary>
    /// Writes a stop field, marking the end of a struct's fields.
    /// </summary>
    void WriteStop();

    /// <summary>
    /// Writes the beginning of a map.
    /// </summary>
    /// <param name="keyType">The type of map keys.</param>
    /// <param name="valueType">The type of map values.</param>
    /// <param name="size">The number of entries.</param>
    void WriteMapBegin(TType keyType, TType valueType, int size);

    /// <summary>
    /// Writes the end of a map.
    /// </summary>
    void WriteMapEnd();

    /// <summary>
    /// Writes the beginning of a list.
    /// </summary>
    /// <param name="elemType">The type of list elements.</param>
    /// <param name="size">The number of elements.</param>
    void WriteListBegin(TType elemType, int size);

    /// <summary>
    /// Writes the end of a list.
    /// </summary>
    void WriteListEnd();

    /// <summary>
    /// Writes the beginning of a set.
    /// </summary>
    /// <param name="elemType">The type of set elements.</param>
    /// <param name="size">The number of elements.</param>
    void WriteSetBegin(TType elemType, int size);

    /// <summary>
    /// Writes the end of a set.
    /// </summary>
    void WriteSetEnd();

    /// <summary>
    /// Writes a boolean value.
    /// </summary>
    /// <param name="value">The value to write.</param>
    void WriteBool(bool value);

    /// <summary>
    /// Writes a single byte.
    /// </summary>
    /// <param name="value">The byte value to write.</param>
    void WriteByte(byte value);

    /// <summary>
    /// Writes a 16-bit signed integer.
    /// </summary>
    /// <param name="value">The value to write.</param>
    void WriteI16(short value);

    /// <summary>
    /// Writes a 32-bit signed integer.
    /// </summary>
    /// <param name="value">The value to write.</param>
    void WriteI32(int value);

    /// <summary>
    /// Writes a 64-bit signed integer.
    /// </summary>
    /// <param name="value">The value to write.</param>
    void WriteI64(long value);

    /// <summary>
    /// Writes a 64-bit IEEE 754 floating-point value.
    /// </summary>
    /// <param name="value">The value to write.</param>
    void WriteDouble(double value);

    /// <summary>
    /// Writes a 32-bit IEEE 754 floating-point value.
    /// </summary>
    /// <param name="value">The value to write.</param>
    void WriteFloat(float value);

    /// <summary>
    /// Writes a UTF-8 encoded string.
    /// </summary>
    /// <param name="value">The string to write.</param>
    void WriteString(string? value);

    /// <summary>
    /// Writes a stop field, marking the end of a struct's fields.
    /// </summary>
    void WriteFieldStop();

    /// <summary>
    /// Writes a raw byte sequence (binary).
    /// </summary>
    /// <param name="value">The bytes to write.</param>
    void WriteBinary(ReadOnlySpan<byte> value);
}
