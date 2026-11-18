using System;
using System.IO;

namespace CanKit.Pro.CANopen.Cia401;

/// <summary>
/// The one place a value of the catalogue becomes the bytes the dictionary, an SDO transfer or a PDO
/// holds, and back, for the device and the peer alike: little-endian, in the width of the data type,
/// two's complement for the signed types (CiA 301 v4.2.0 §7.1.4.4, §7.1.4.5, and §7.1.2: the least
/// significant byte first).
/// </summary>
internal static class Cia401Codec
{
    private const int BitsPerByte = 8;
    private const int MaxBits = 64;

    /// <summary>Whether <paramref name="type"/> is one of the signed integer types, whose value is two's
    /// complement (CiA 301 v4.2.0 §7.1.4.5).</summary>
    internal static bool IsSigned(OdDataType type) => type switch
    {
        OdDataType.Integer8 or OdDataType.Integer16 or OdDataType.Integer24 or OdDataType.Integer32
            or OdDataType.Integer40 or OdDataType.Integer48 or OdDataType.Integer56 or OdDataType.Integer64 => true,
        _ => false,
    };

    /// <summary>The little-endian bytes of <paramref name="value"/> in the width of
    /// <paramref name="type"/>; the bits above the width are dropped, so a signed value is its two's
    /// complement.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="type"/> has no fixed width
    /// (a string or <see cref="OdDataType.Domain"/>).</exception>
    /// <exception cref="NotSupportedException"><paramref name="type"/> is REAL32 or REAL64: a real is
    /// not an integer, and its bit pattern is not what a caller of a typed value means.</exception>
    internal static byte[] Encode(OdDataType type, long value)
    {
        var bytes = new byte[IntegerWidth(type)];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = unchecked((byte)(value >> (BitsPerByte * i)));
        return bytes;
    }

    /// <summary>The value of the little-endian <paramref name="raw"/> as <paramref name="type"/>:
    /// sign-extended for a signed type, zero-extended otherwise.</summary>
    /// <exception cref="InvalidDataException"><paramref name="raw"/> is not as long as the type is
    /// wide. The value is refused, not cut to its low bytes or padded.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="type"/> has no fixed width.</exception>
    /// <exception cref="NotSupportedException"><paramref name="type"/> is REAL32 or REAL64, as for
    /// <see cref="Encode"/>.</exception>
    internal static long Decode(OdDataType type, byte[] raw)
    {
        int width = IntegerWidth(type);
        if (raw.Length != width)
        {
            throw new InvalidDataException(
                $"A {type} value has {width} byte(s); the peer returned {raw.Length}.");
        }

        return DecodeBits(raw, width * BitsPerByte, IsSigned(type));
    }

    /// <summary>The value of the low <paramref name="bits"/> of the little-endian
    /// <paramref name="value"/>, as a PDO carries a mapped entry: sign-extended from bit
    /// <paramref name="bits"/> − 1 when <paramref name="signed"/>, zero-extended otherwise. Bits of
    /// <paramref name="value"/> beyond <paramref name="bits"/> are ignored. It knows no data type, so a
    /// mapped REAL32 comes out as its bit pattern.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bits"/> is outside 1 to 64.</exception>
    /// <exception cref="InvalidDataException"><paramref name="value"/> has fewer bytes than
    /// <paramref name="bits"/> need.</exception>
    internal static long DecodeBits(byte[] value, int bits, bool signed)
    {
        if (bits is < 1 or > MaxBits)
            throw new ArgumentOutOfRangeException(nameof(bits), bits, "A mapped entry has 1 to 64 bits.");
        int bytes = (bits + BitsPerByte - 1) / BitsPerByte;
        if (value.Length < bytes)
            throw new InvalidDataException($"{bits} bit(s) need {bytes} byte(s); the value has {value.Length}.");

        ulong raw = 0;
        for (int i = 0; i < bytes; i++) raw |= (ulong)value[i] << (BitsPerByte * i);
        if (bits == MaxBits) return unchecked((long)raw);

        raw &= (1UL << bits) - 1;
        if (signed && (raw & (1UL << (bits - 1))) != 0) raw |= ~0UL << bits;
        return unchecked((long)raw);
    }

    private static int IntegerWidth(OdDataType type)
    {
        if (type is OdDataType.Real32 or OdDataType.Real64)
            throw new NotSupportedException($"A {type} value is a real, not an integer: it is not encoded or decoded as one.");
        int width = OdEntryLayout.FixedSize(type);
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(type), type, "The data type has no fixed width.");
        return width;
    }
}
