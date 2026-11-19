using System;
using System.IO;
using CanKit.Pro.CANopen.Application;

namespace CanKit.Pro.CANopen.Cia401;

/// <summary>
/// The value codec of the CiA 401 device and peer: <see cref="CanOpenValueCodec"/>, which every
/// profile shares, under the name the CiA 401 code and its tests use. It forwards and holds no rule
/// of its own; little-endian, the width of the data type and two's complement are stated there.
/// </summary>
internal static class Cia401Codec
{
    /// <inheritdoc cref="CanOpenValueCodec.IsSigned"/>
    internal static bool IsSigned(OdDataType type) => CanOpenValueCodec.IsSigned(type);

    /// <inheritdoc cref="CanOpenValueCodec.Encode"/>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="type"/> has no fixed width.</exception>
    /// <exception cref="NotSupportedException"><paramref name="type"/> is REAL32 or REAL64.</exception>
    internal static byte[] Encode(OdDataType type, long value) => CanOpenValueCodec.Encode(type, value);

    /// <inheritdoc cref="CanOpenValueCodec.Decode"/>
    /// <exception cref="InvalidDataException"><paramref name="raw"/> is not as long as the type is
    /// wide.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="type"/> has no fixed width.</exception>
    /// <exception cref="NotSupportedException"><paramref name="type"/> is REAL32 or REAL64.</exception>
    internal static long Decode(OdDataType type, byte[] raw) => CanOpenValueCodec.Decode(type, raw);

    /// <inheritdoc cref="CanOpenValueCodec.DecodeBits"/>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bits"/> is outside 1 to 64.</exception>
    /// <exception cref="InvalidDataException"><paramref name="value"/> has fewer bytes than
    /// <paramref name="bits"/> need.</exception>
    internal static long DecodeBits(byte[] value, int bits, bool signed) => CanOpenValueCodec.DecodeBits(value, bits, signed);
}
