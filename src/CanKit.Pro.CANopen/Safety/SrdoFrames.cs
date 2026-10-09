namespace CanKit.Pro.CANopen.Safety;

/// <summary>The two CAN frames of an SRDO (CiA DSP 304 V1.0 §8.1, §8.1.3.1) and the COB-ID
/// rules of Figure 7 / Table 4.</summary>
internal static class SrdoFrames
{
    /// <summary>"the data on the 2nd transmission is inverted bitwise" (§8.1).</summary>
    public static byte[] Invert(byte[] data)
    {
        var inverted = new byte[data.Length];
        for (int i = 0; i < data.Length; i++) inverted[i] = (byte)~data[i];
        return inverted;
    }

    /// <summary>§9.5: "compared bit by bit (modulo 2)". Equal length and every byte inverted.</summary>
    public static bool IsInversePair(byte[] first, byte[] second)
    {
        if (first.Length != second.Length) return false;
        for (int i = 0; i < first.Length; i++)
        {
            if ((byte)~first[i] != second[i]) return false;
        }
        return true;
    }

    /// <summary>Sub-index 5 value range: 257, 259..383 — the odd ids of 101h..17Fh.</summary>
    public static bool IsCobId1(uint canId) => canId is >= 0x101 and <= 0x17F && (canId & 1) == 1;

    /// <summary>Sub-index 6 value range: 258, 260..384 — the even ids of 102h..180h.</summary>
    public static bool IsCobId2(uint canId) => canId is >= 0x102 and <= 0x180 && (canId & 1) == 0;

    /// <summary>The pair an SRDO is created with (§8.4.2.2): COB-ID 1 in its range, COB-ID 2 in
    /// its range and "two following COB-IDs". A bit above bit 10 puts an id out of range.</summary>
    public static bool IsCobIdPair(uint cobId1, uint cobId2) => IsCobId1(cobId1) && IsCobId2(cobId2) && cobId2 == cobId1 + 1;
}
