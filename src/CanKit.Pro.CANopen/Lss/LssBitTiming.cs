using System;
using System.Collections.Generic;

namespace CanKit.Pro.CANopen.Lss;

/// <summary>
/// Table selector 0, the CiA 301 bit timing table as CiA 305 Table 4 indexes it.
/// Index 5 is reserved. Index 9 selects automatic bit rate detection: a configure-bit-timing
/// command cannot make a slave perform it (the master only names the index), but the procedure
/// itself is CiA 801 and is available as <see cref="LssBitRateDetection"/>.
/// </summary>
public static class LssBitTiming
{
    /// <summary>Table selector for the CiA 301 bit timing table (§6.4.3).</summary>
    public const byte Cia301Table = 0x00;

    /// <summary>First table selector a manufacturer may use for its own table (§7.5.2).</summary>
    public const byte ManufacturerTableFirst = 0x80;

    /// <summary>Last table selector a manufacturer may use for its own table.</summary>
    public const byte ManufacturerTableLast = 0xFE;

    /// <summary>Table index of automatic bit rate detection. Not implemented here.</summary>
    public const byte AutomaticIndex = 9;

    private static readonly int?[] Rates =
    {
        1_000_000,
        800_000,
        500_000,
        250_000,
        125_000,
        null,
        50_000,
        20_000,
        10_000,
        null,
    };

    /// <summary>Entries of table 0 that name a bit rate, in index order. Index 5 and 9 are absent.</summary>
    public static IReadOnlyList<(byte Index, int BitsPerSecond)> Cia301Rates { get; } = Build();

    /// <summary>Whether <paramref name="tableSelector"/> addresses a manufacturer-specific table.</summary>
    public static bool IsManufacturerTable(byte tableSelector)
        => tableSelector >= ManufacturerTableFirst && tableSelector <= ManufacturerTableLast;

    /// <summary>
    /// Bit rate of CiA 301 table index <paramref name="tableIndex"/>, when the index names one.
    /// </summary>
    public static bool TryGetCia301Rate(byte tableIndex, out int bitsPerSecond)
    {
        if (tableIndex >= Rates.Length || Rates[tableIndex] is not int rate)
        {
            bitsPerSecond = 0;
            return false;
        }
        bitsPerSecond = rate;
        return true;
    }

    /// <summary>CiA 301 table index of <paramref name="bitsPerSecond"/>, when the table lists it.</summary>
    public static bool TryGetCia301Index(int bitsPerSecond, out byte tableIndex)
    {
        for (byte i = 0; i < Rates.Length; i++)
        {
            if (Rates[i] == bitsPerSecond)
            {
                tableIndex = i;
                return true;
            }
        }
        tableIndex = 0;
        return false;
    }

    private static IReadOnlyList<(byte Index, int BitsPerSecond)> Build()
    {
        var list = new List<(byte, int)>();
        for (byte i = 0; i < Rates.Length; i++)
        {
            if (Rates[i] is int rate) list.Add((i, rate));
        }
        return list;
    }
}
