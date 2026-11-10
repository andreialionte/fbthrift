using System.Buffers;
using System.Buffers.Binary;

namespace FbThrift.Core.Framing;

/// <summary>
/// Base framing utilities.
/// </summary>
public static class FramingUtilities
{
    /// <summary>
    /// THeader magic number.
    /// </summary>
    public const ushort THeaderMagic = 0x8001;

    /// <summary>
    /// Header transport magic.
    /// </summary>
    public const uint HeaderTransportMagic = 0x0FFF0000;
}
