using System;
using System.Collections.Generic;
using CanKit.Pro.CANopen.Pdo;

namespace CanKit.Pro.CANopen.Safety;

/// <summary>Where the safety objects live (CiA DSP 304 V1.0 §8.4.2.1 Table 6) and how to read
/// an SRDO's records out of the dictionary into typed values. Pure functions over an
/// <see cref="ObjectDictionary"/>, shared by the validator (writing thread) and the engine (actor).</summary>
internal static class SrdoRecords
{
    public const ushort GfcParameter = 0x1300;
    public const ushort CommunicationBase = 0x1300;   // record n at 1300h + n
    public const ushort MappingBase = 0x1380;         // record n at 1380h + n
    public const ushort ConfigurationValid = 0x13FE;
    public const ushort Checksum = 0x13FF;
    public const byte ConfigurationValidValue = 0xA5;
    public const int MaxSrdoCount = 64;
    /// <summary>8 objects, each plain (odd sub-index) and inverted (even sub-index).</summary>
    public const int MappingSubindices = 2 * SrdoMapping.MaxEntries;
    /// <summary>Sub-index 4 of every record: "defined as type 254" (§8.4.2.2).</summary>
    public const byte TransmissionType = 254;

    public static ushort CommIndex(int n) => (ushort)(CommunicationBase + n);
    public static ushort MapIndex(int n) => (ushort)(MappingBase + n);

    public static bool IsCommunicationRecord(ushort index) => index is > CommunicationBase and <= CommunicationBase + MaxSrdoCount;
    public static bool IsMappingRecord(ushort index) => index is > MappingBase and <= MappingBase + MaxSrdoCount;

    /// <summary>Table 6: 1300h, the records, 13FEh and 13FFh.</summary>
    public static bool IsSafetyObject(ushort index)
        => index == GfcParameter || index == ConfigurationValid || index == Checksum
           || IsCommunicationRecord(index) || IsMappingRecord(index);

    /// <summary>§8.3.2.4 note 1: "Writing to a safety entry in the OPERATIONAL state leads to an
    /// abort message" — the records, 13FEh and 13FFh. 1300h is not CRC-covered and not gated.</summary>
    public static bool IsStateGated(ushort index)
        => index == ConfigurationValid || index == Checksum || IsCommunicationRecord(index) || IsMappingRecord(index);

    /// <summary>§8.4.2.2 object 13FEh: "After a write access to the safety-relevant parameter
    /// the entry of object 13FEh is automatically 0" — the records and the checksums.</summary>
    public static bool IsChecksummed(ushort index)
        => index == Checksum || IsCommunicationRecord(index) || IsMappingRecord(index);

    public static int? SrdoNumberOf(ushort index)
    {
        if (IsCommunicationRecord(index)) return index - CommunicationBase;
        if (IsMappingRecord(index)) return index - MappingBase;
        return null;
    }

    public static bool TryReadCommunication(ObjectDictionary od, int n, out SrdoCommunicationParameter parameter)
    {
        var comm = CommIndex(n);
        parameter = default;
        if (!od.TryReadUnsigned(comm, 0x01, out var direction)) return false;
        od.TryReadUnsigned(comm, 0x02, out var cycle);
        od.TryReadUnsigned(comm, 0x03, out var srvt);
        od.TryReadUnsigned(comm, 0x05, out var cob1);
        od.TryReadUnsigned(comm, 0x06, out var cob2);
        parameter = new SrdoCommunicationParameter(
            direction <= 2 ? (SrdoDirection)direction : SrdoDirection.None,
            TimeSpan.FromMilliseconds(cycle), TimeSpan.FromMilliseconds(srvt),
            cob1 & CanOpenCobId.CanIdMask, cob2 & CanOpenCobId.CanIdMask);
        return true;
    }

    /// <summary>The objects of the mapping, read from the odd sub-indices. Empty when sub0 is 0
    /// or when any slot up to sub0 is empty or malformed (the validator refuses such a record;
    /// one can only get here through a re-declaration).</summary>
    public static PdoMappingEntry[] ReadMapping(ObjectDictionary od, int n)
    {
        var map = MapIndex(n);
        if (!od.TryReadUnsigned(map, 0x00, out var count) || count == 0 || count > MappingSubindices || (count & 1) != 0)
            return Array.Empty<PdoMappingEntry>();
        var entries = new List<PdoMappingEntry>((int)count / 2);
        for (byte s = 1; s <= count; s += 2)
        {
            if (!od.TryReadUnsigned(map, s, out var raw) || raw == 0) return Array.Empty<PdoMappingEntry>();
            try
            {
                entries.Add(new PdoMappingEntry((ushort)(raw >> 16), (byte)(raw >> 8), (byte)raw));
            }
            catch (ArgumentOutOfRangeException)
            {
                return Array.Empty<PdoMappingEntry>();
            }
        }
        return entries.ToArray();
    }

    /// <summary>§9.5 last rule: "The CRC-Entry in the object dictionary shall be equal to the CRC
    /// calculation of the safety device and the configuration-valid flag shall be valid."</summary>
    public static bool IsConfigurationValid(ObjectDictionary od, int n)
    {
        if (!od.TryReadUnsigned(ConfigurationValid, 0x00, out var valid) || valid != ConfigurationValidValue) return false;
        if (!od.TryReadUnsigned(Checksum, (byte)n, out var stored)) return false;
        if (!TryReadCommunication(od, n, out var parameter)) return false;
        return stored == SrdoCrc.Compute(parameter, SrdoMapping.FromEntries(ReadMapping(od, n)));
    }
}
