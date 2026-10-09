using System;
using System.Collections.Generic;

namespace CanKit.Pro.CANopen.Safety;

/// <summary>
/// The safety configuration checksum of object 13FFh (CiA DSP 304 V1.0 §8.4.2.2). The norm
/// gives the generator polynomial x^16 + x^12 + x^5 + 1 and the order of the fields; it names
/// no initial value and no byte order. This implementation uses CRC-16/XMODEM (initial value
/// 0000h, no reflection, no final XOR — the algorithm CiA 301 §7.2.4.3.16 prescribes for the
/// SDO block transfer) and feeds multi-byte fields MSB-first, the order in which D(x) lists
/// their bits (b15 … b0). Both choices are recorded in
/// docs/reviews/2026-10-09-canopen-safety-scope.md and are reconciled against EN 50325-5 in #289.
/// </summary>
public static class SrdoCrc
{
    /// <summary>The checksum the device compares at the transition to Operational and the tool
    /// writes to 13FFh:n (§8.3.1 step D, §9.2).</summary>
    public static ushort Compute(in SrdoCommunicationParameter parameter, SrdoMapping mapping)
    {
        if (mapping is null) throw new ArgumentNullException(nameof(mapping));
        return Crc16Xmodem(CanonicalBytes(parameter, mapping));
    }

    /// <summary>CRC-16/XMODEM: poly 1021h, init 0000h, no reflection, no xor-out. Check value of
    /// "123456789" is 31C3h.</summary>
    public static ushort Crc16Xmodem(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        for (int i = 0; i < data.Length; i++)
        {
            crc ^= (ushort)(data[i] << 8);
            for (int j = 0; j < 8; j++)
            {
                if ((crc & 0x8000) != 0) crc = (ushort)((crc << 1) ^ 0x1021);
                else crc <<= 1;
            }
        }
        return crc;
    }

    /// <summary>§8.4.2.2 "The order for data which are checked by the CRC": a) direction (1),
    /// b) refresh time / SCT (2), c) SRVT (1), d) COB-ID 1 (4), e) COB-ID 2 (4), f) mapping
    /// sub-index 0 (1), then per mapping entry g) its sub-index (1) and h) its value (4). The
    /// record form holds each object twice, plain at the odd and inverted at the even sub-index,
    /// with the same mapping value.</summary>
    internal static byte[] CanonicalBytes(in SrdoCommunicationParameter parameter, SrdoMapping mapping)
    {
        var bytes = new List<byte>(13 + 10 * mapping.Entries.Count);
        bytes.Add((byte)parameter.Direction);
        ushort cycle = (ushort)Math.Round(parameter.RefreshOrSafeguardCycleTime.TotalMilliseconds);
        bytes.Add((byte)(cycle >> 8));
        bytes.Add((byte)cycle);
        bytes.Add((byte)Math.Round(parameter.ValidationTime.TotalMilliseconds));
        AddU32(bytes, parameter.CobId1);
        AddU32(bytes, parameter.CobId2);
        bytes.Add((byte)(2 * mapping.Entries.Count));
        byte sub = 1;
        foreach (var entry in mapping.Entries)
        {
            uint value = ((uint)entry.Index << 16) | ((uint)entry.Subindex << 8) | entry.BitLength;
            bytes.Add(sub++);
            AddU32(bytes, value);
            bytes.Add(sub++);
            AddU32(bytes, value);
        }
        return bytes.ToArray();
    }

    private static void AddU32(List<byte> bytes, uint value)
    {
        bytes.Add((byte)(value >> 24));
        bytes.Add((byte)(value >> 16));
        bytes.Add((byte)(value >> 8));
        bytes.Add((byte)value);
    }
}
