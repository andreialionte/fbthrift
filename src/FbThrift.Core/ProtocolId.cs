namespace FbThrift.Core;

/// <summary>
/// Protocol identifiers for THeader framing.
/// </summary>
public enum ProtocolId : uint
{
    Binary = 0,
    Compact = 2,
    SimpleJson = 3
}

/// <summary>
/// Transform identifiers for THeader framing.
/// </summary>
public enum TransformId : uint
{
    None = 0,
    Zlib = 1,
    Hmac = 2,
    Snappy = 3,
    Qlz = 4,
    Zstd = 5,
    Lz4 = 6
}