using System;

namespace CanKit.Pro.CANopen.Pdo;

/// <summary>CiA 301 PDO bits are packed least-significant bit first, across byte boundaries.</summary>
internal static class PdoBits
{
    public static void Copy(ReadOnlySpan<byte> source, int sourceBit, Span<byte> destination, int destinationBit, int count)
    {
        for (int bit = 0; bit < count; bit++)
        {
            int from = sourceBit + bit;
            int to = destinationBit + bit;
            if (from / 8 < source.Length && (source[from / 8] & (1 << (from % 8))) != 0)
                destination[to / 8] |= (byte)(1 << (to % 8));
        }
    }

    public static byte[] Extract(ReadOnlySpan<byte> source, int offset, int count)
    {
        var value = new byte[(count + 7) / 8];
        Copy(source, offset, value, 0, count);
        return value;
    }
}
