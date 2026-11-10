namespace FbThrift.Core;

/// <summary>
/// Specifies the Thrift type of a field value as defined by the Apache Thrift type system.
/// Values match fbthrift type definitions.
/// </summary>
public enum TType : byte
{
    /// <summary>Marks the end of a struct's fields.</summary>
    Stop = 0,
    /// <summary>An untyped void value.</summary>
    Void = 1,
    /// <summary>A boolean value.</summary>
    Bool = 2,
    /// <summary>A single byte (8-bit signed integer).</summary>
    Byte = 3,
    /// <summary>A 64-bit IEEE 754 floating-point value.</summary>
    Double = 4,
    /// <summary>A 16-bit signed integer.</summary>
    I16 = 6,
    /// <summary>A 32-bit signed integer.</summary>
    I32 = 8,
    /// <summary>A 64-bit signed integer.</summary>
    I64 = 10,
    /// <summary>A UTF-8 encoded string.</summary>
    String = 11,
    /// <summary>A struct value.</summary>
    Struct = 12,
    /// <summary>A map value.</summary>
    Map = 13,
    /// <summary>A set value.</summary>
    Set = 14,
    /// <summary>A list value.</summary>
    List = 15,
    /// <summary>An enumeration value.</summary>
    Enum = 16,
    /// <summary>A 32-bit IEEE 754 floating-point value.</summary>
    Float = 17,
    /// <summary>A binary blob (length-prefixed bytes).</summary>
    Binary = 18,
    /// <summary>A UUID value (fbthrift extension).</summary>
    Uuid = 19
}

/// <summary>
/// Constants for the THeader framing format used by Apache fbthrift.
/// </summary>
public static class THeaderFrameConstants
{
    /// <summary>The THeader magic number (0x8001).</summary>
    public const ushort Magic = 0x8001;
    /// <summary>The THeader frame version.</summary>
    public const byte Version = 1;
}
