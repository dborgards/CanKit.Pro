using System;
using System.Collections.Generic;

namespace CanKit.Pro.CANopen.Safety;

/// <summary>
/// The safety configuration checksum of object 13FFh (CiA DSP 304 V1.0 §8.4.2.2; EN 50325-5:2010
/// §6.4.1.5, Table 9). Both texts give the generator polynomial x^16 + x^12 + x^5 + 1, the order
/// of the fields and the seed 0000h (EN 50325-5); neither names the byte order of a multi-byte
/// field — Table 9 only lists each field's bits from the most significant down (b15 … b0). This
/// implementation uses CRC-16/XMODEM (initial value 0000h, no reflection, no final XOR — the
/// algorithm CiA 301 §7.2.4.3.16 prescribes for the SDO block transfer) and feeds multi-byte
/// fields little-endian, the CANopen byte image, as CANopenNode's CO_SRDOcalcCrc does (#289).
/// </summary>
public static class SrdoCrc
{
    /// <summary>The checksum the device compares at the transition to Operational and the tool
    /// writes to 13FFh:n (§8.3.1 step D, §9.2). The times go in as whole milliseconds, rounded
    /// half to even (<see cref="Math.Round(double)"/>), and must fit their fields after rounding:
    /// the refresh time or SCT 0..65535 ms (UNSIGNED16), the SRVT 0..255 ms (UNSIGNED8). 0 is
    /// accepted — the device path checksums what its dictionary holds, and the dictionary refuses
    /// 0 itself.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A time is negative or does not fit its field
    /// after rounding, or the direction is above 2 (§8.4.2.2).</exception>
    /// <exception cref="ArgumentNullException"><paramref name="mapping"/> is null.</exception>
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

    /// <summary>The 13FFh byte sequence of a configuration being written — the record form of
    /// <paramref name="parameter"/> and <paramref name="mapping"/>: each mapped object twice,
    /// plain at the odd and inverted at the even sub-index, with the same mapping value — fed to
    /// the serializer below.</summary>
    internal static byte[] CanonicalBytes(in SrdoCommunicationParameter parameter, SrdoMapping mapping)
    {
        if ((byte)parameter.Direction > (byte)SrdoDirection.Receive)
            throw new ArgumentOutOfRangeException(nameof(parameter), parameter.Direction, "The direction is 0..2 (CiA DSP 304 §8.4.2.2, sub-index 1).");
        ushort cycle = (ushort)Milliseconds(parameter.RefreshOrSafeguardCycleTime, ushort.MaxValue, "refresh time or SCT (sub-index 2, UNSIGNED16)", nameof(parameter));
        byte srvt = (byte)Milliseconds(parameter.ValidationTime, byte.MaxValue, "SRVT (sub-index 3, UNSIGNED8)", nameof(parameter));
        var slots = new List<(byte Subindex, uint Value)>(2 * mapping.Entries.Count);
        byte sub = 1;
        foreach (var entry in mapping.Entries)
        {
            uint value = ((uint)entry.Index << 16) | ((uint)entry.Subindex << 8) | entry.BitLength;
            slots.Add((sub++, value));
            slots.Add((sub++, value));
        }
        return CanonicalBytes((byte)parameter.Direction, cycle, srvt, parameter.CobId1, parameter.CobId2, (byte)slots.Count, slots);
    }

    /// <summary>§8.4.2.2 "The order for data which are checked by the CRC": a) direction (1),
    /// b) refresh time / SCT (2), c) SRVT (1), d) COB-ID 1 (4), e) COB-ID 2 (4), f) mapping
    /// sub-index 0 (1), then per mapping slot g) its sub-index (1) and h) its value (4) — every
    /// field as given, multi-byte fields little-endian (#289). The one serializer of the 13FFh
    /// layout: the tool side (<see cref="Compute"/>, over a configuration being written) and the
    /// device side (<see cref="SrdoRecords.RecordChecksum"/>, over a record as the dictionary
    /// holds it) both feed it, so the byte order is decided here once and cannot drift between
    /// the two.</summary>
    internal static byte[] CanonicalBytes(byte direction, ushort cycleMs, byte validationMs, uint cobId1, uint cobId2,
        byte mappingCount, IReadOnlyList<(byte Subindex, uint Value)> slots)
    {
        var bytes = new List<byte>(13 + 5 * slots.Count);
        bytes.Add(direction);
        bytes.Add((byte)cycleMs);
        bytes.Add((byte)(cycleMs >> 8));
        bytes.Add(validationMs);
        AddU32(bytes, cobId1);
        AddU32(bytes, cobId2);
        bytes.Add(mappingCount);
        foreach (var (subindex, value) in slots)
        {
            bytes.Add(subindex);
            AddU32(bytes, value);
        }
        return bytes.ToArray();
    }

    /// <summary>Whole milliseconds, rounded half to even, within 0..<paramref name="max"/>.</summary>
    private static int Milliseconds(TimeSpan time, int max, string field, string paramName)
    {
        var ms = Math.Round(time.TotalMilliseconds);
        if (!(ms >= 0 && ms <= max))
            throw new ArgumentOutOfRangeException(paramName, time,
                $"The {field} must be 0..{max} ms after rounding to whole milliseconds, not {time.TotalMilliseconds} ms (CiA DSP 304 §8.4.2.2).");
        return (int)ms;
    }

    private static void AddU32(List<byte> bytes, uint value)
    {
        bytes.Add((byte)value);
        bytes.Add((byte)(value >> 8));
        bytes.Add((byte)(value >> 16));
        bytes.Add((byte)(value >> 24));
    }
}
