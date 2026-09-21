using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace CanKit.Pro.CANopen.Pdo;

/// <summary>
/// A single entry in a PDO mapping: reference to an OD <c>(index, subindex)</c> plus the number
/// of bits contributed to the assembled PDO payload. Bit lengths must be multiples of eight
/// (byte-aligned mapping), which matches every profile the tests exercise and keeps the
/// packing/unpacking loop trivially correct; bit-granular mapping is a documented omission.
/// </summary>
/// <remarks>
/// Bit lengths are the same units the CiA 301 §7.5.2.38 mapping record uses (a 32-bit mapping
/// entry is <c>index &lt;&lt; 16 | subindex &lt;&lt; 8 | bit-length</c>). The node stores every
/// mapping in its <c>1600h</c>–<c>1603h</c> / <c>1A00h</c>–<c>1A03h</c> records, whether it was
/// configured through <c>ConfigureTpdo</c>/<c>ConfigureRpdo</c> or written over SDO.
/// An entry whose index is one of the static data types <c>0002h</c>–<c>0007h</c> (with
/// sub-index 0) is a <em>dummy mapping</em> (CiA 301 §7.5.2.36): it occupies its bytes in the
/// PDO without touching the object dictionary, which pads an RPDO to a peer's layout.
/// </remarks>
public readonly struct PdoMappingEntry : IEquatable<PdoMappingEntry>
{
    /// <summary>Constructs a new mapping entry.</summary>
    /// <param name="index">Target OD index, or a dummy data-type index <c>0002h</c>–<c>0007h</c>.</param>
    /// <param name="subindex">Target OD subindex (0 for a dummy entry).</param>
    /// <param name="bitLength">Field size in bits; must be a positive multiple of eight and no
    /// larger than 64.</param>
    public PdoMappingEntry(ushort index, byte subindex, byte bitLength)
    {
        if (bitLength == 0 || bitLength > 64 || bitLength % 8 != 0)
            throw new ArgumentOutOfRangeException(nameof(bitLength), bitLength,
                "Mapping requires positive byte-aligned bit lengths (8/16/24/32/40/48/56/64).");
        Index = index;
        Subindex = subindex;
        BitLength = bitLength;
    }

    /// <summary>OD index this entry references.</summary>
    public ushort Index { get; }

    /// <summary>OD subindex this entry references.</summary>
    public byte Subindex { get; }

    /// <summary>Contribution size in bits (always a multiple of 8).</summary>
    public byte BitLength { get; }

    /// <summary>Contribution size in bytes.</summary>
    public int ByteLength => BitLength / 8;

    /// <summary>Whether this is a dummy mapping (CiA 301 §7.5.2.36): index <c>0002h</c>–<c>0007h</c>,
    /// sub-index 0. The bytes are padding — zeros in a TPDO, ignored in an RPDO.</summary>
    public bool IsDummy => Subindex == 0 && Index is >= 0x0002 and <= 0x0007;

    /// <summary>The bit length CiA 301 assigns to the dummy data type <paramref name="index"/>
    /// (<c>0002h</c>/<c>0005h</c> = 8, <c>0003h</c>/<c>0006h</c> = 16, <c>0004h</c>/<c>0007h</c> = 32),
    /// or 0 for any other index.</summary>
    public static byte DummyBitLength(ushort index) => index switch
    {
        0x0002 or 0x0005 => 8,
        0x0003 or 0x0006 => 16,
        0x0004 or 0x0007 => 32,
        _ => 0,
    };

    /// <inheritdoc />
    public bool Equals(PdoMappingEntry other)
        => Index == other.Index && Subindex == other.Subindex && BitLength == other.BitLength;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PdoMappingEntry other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => unchecked((Index * 397) ^ (Subindex * 31) ^ BitLength);
}

/// <summary>
/// TPDO transmission trigger for <c>ICanOpenNode.ConfigureTpdo</c>. Each member maps to a
/// CiA 301 §7.5.2.37 Table 72 transmission type byte in the TPDO's <c>1800h:02</c> record (see
/// <see cref="CanOpenTransmissionType"/>); the numeric values of this enum are not the wire
/// encoding. "Every n-th SYNC" (<c>02h</c>–<c>F0h</c>) has no member because it carries a
/// number: write the raw byte to <c>1800h:02</c> through the object dictionary instead.
/// </summary>
public enum TpdoTransmission : byte
{
    /// <summary>Event-driven (<c>FEh</c>): emitted on change of state of a mapped object and by
    /// <c>ICanOpenNode.TriggerTpdoAsync</c>; rate-limited by the inhibit time.</summary>
    EventDriven = 0,

    /// <summary>Event-driven with an event timer (<c>FEh</c> plus <c>1800h:05</c>): as
    /// <see cref="EventDriven"/>, and additionally re-transmitted whenever the event timer elapses
    /// without a transmission — the maximum interval between two transmissions.</summary>
    EventTimer = 1,

    /// <summary>Synchronous, cyclic every SYNC (<c>01h</c>).</summary>
    Synchronous = 2,

    /// <summary>Synchronous, acyclic (<c>00h</c>): transmitted after the next SYNC only if a
    /// change of state or a <c>TriggerTpdoAsync</c> call occurred since the previous SYNC.</summary>
    SynchronousAcyclic = 3,

    /// <summary>RTR-only, synchronous (<c>FCh</c>): sampled at every SYNC into a buffer, and the
    /// buffered sample is transmitted when a consumer requests the PDO with an RTR.</summary>
    RtrOnlySynchronous = 4,

    /// <summary>RTR-only, event-driven (<c>FDh</c>): sampled and transmitted when a consumer
    /// requests the PDO with an RTR.</summary>
    RtrOnlyEventDriven = 5,
}

/// <summary>
/// RPDO reception character for <c>ICanOpenNode.ConfigureRpdo</c> (CiA 301 §7.5.2.35 Table 68,
/// stored in <c>1400h:02</c>).
/// </summary>
public enum RpdoTransmission : byte
{
    /// <summary>Event-driven (<c>FEh</c>): received data is written to the object dictionary
    /// immediately.</summary>
    EventDriven = 0,

    /// <summary>Synchronous (<c>00h</c>): received data is held and actuated — written to the
    /// object dictionary and reported through <c>RpdoReceived</c> — with the next SYNC.</summary>
    Synchronous = 1,
}

/// <summary>
/// The CiA 301 transmission type byte carried in <c>1400h:02</c> / <c>1800h:02</c> (Tables 68 and
/// 72). Constants and classification helpers for callers that configure a PDO through the object
/// dictionary rather than through <see cref="TpdoTransmission"/>.
/// </summary>
public static class CanOpenTransmissionType
{
    /// <summary>Synchronous, acyclic (TPDO) / synchronous (RPDO).</summary>
    public const byte SynchronousAcyclic = 0x00;

    /// <summary>Synchronous, cyclic every SYNC.</summary>
    public const byte SynchronousEverySync = 0x01;

    /// <summary>Largest cyclic-synchronous value: every 240th SYNC.</summary>
    public const byte SynchronousCyclicMax = 0xF0;

    /// <summary>RTR-only, synchronous (TPDO only).</summary>
    public const byte RtrOnlySynchronous = 0xFC;

    /// <summary>RTR-only, event-driven (TPDO only).</summary>
    public const byte RtrOnlyEventDriven = 0xFD;

    /// <summary>Event-driven, manufacturer-specific.</summary>
    public const byte EventDrivenManufacturer = 0xFE;

    /// <summary>Event-driven, device-profile / application-profile specific.</summary>
    public const byte EventDrivenProfile = 0xFF;

    /// <summary>The value for "cyclic every <paramref name="n"/>-th SYNC" (1..240).</summary>
    public static byte SynchronousEveryNthSync(int n)
    {
        if (n is < 1 or > SynchronousCyclicMax)
            throw new ArgumentOutOfRangeException(nameof(n), n, "A cyclic SYNC factor is 1..240 (CiA 301 Table 72).");
        return (byte)n;
    }

    /// <summary>Whether <paramref name="type"/> is one of the synchronous values <c>00h</c>–<c>F0h</c>.</summary>
    public static bool IsSynchronous(byte type) => type <= SynchronousCyclicMax;

    /// <summary>Whether <paramref name="type"/> is <c>FEh</c> or <c>FFh</c>.</summary>
    public static bool IsEventDriven(byte type) => type >= EventDrivenManufacturer;

    /// <summary>Whether <paramref name="type"/> is <c>FCh</c> or <c>FDh</c>.</summary>
    public static bool IsRtrOnly(byte type) => type is RtrOnlySynchronous or RtrOnlyEventDriven;

    /// <summary>Whether <paramref name="type"/> is reserved for a TPDO (<c>F1h</c>–<c>FBh</c>).</summary>
    public static bool IsReservedForTpdo(byte type) => type is > SynchronousCyclicMax and < RtrOnlySynchronous;

    /// <summary>Whether <paramref name="type"/> is reserved for an RPDO (<c>F1h</c>–<c>FDh</c>).</summary>
    public static bool IsReservedForRpdo(byte type) => type is > SynchronousCyclicMax and < EventDrivenManufacturer;

    /// <summary>The transmission type byte <see cref="TpdoTransmission"/> stands for.</summary>
    public static byte FromTpdoTransmission(TpdoTransmission transmission) => transmission switch
    {
        TpdoTransmission.EventDriven => EventDrivenManufacturer,
        TpdoTransmission.EventTimer => EventDrivenManufacturer,
        TpdoTransmission.Synchronous => SynchronousEverySync,
        TpdoTransmission.SynchronousAcyclic => SynchronousAcyclic,
        TpdoTransmission.RtrOnlySynchronous => RtrOnlySynchronous,
        TpdoTransmission.RtrOnlyEventDriven => RtrOnlyEventDriven,
        _ => throw new ArgumentOutOfRangeException(nameof(transmission), transmission, "Unknown TPDO transmission mode."),
    };

    /// <summary>The transmission type byte <see cref="RpdoTransmission"/> stands for.</summary>
    public static byte FromRpdoTransmission(RpdoTransmission transmission) => transmission switch
    {
        RpdoTransmission.EventDriven => EventDrivenManufacturer,
        RpdoTransmission.Synchronous => SynchronousAcyclic,
        _ => throw new ArgumentOutOfRangeException(nameof(transmission), transmission, "Unknown RPDO transmission mode."),
    };
}

/// <summary>
/// A PDO mapping under construction: the ordered list of <see cref="PdoMappingEntry"/> values a
/// PDO carries. Handed to <c>ConfigureTpdo</c> / <c>ConfigureRpdo</c>, which copy the entries
/// into the node's mapping record; the node keeps no reference to this object, so mutating it
/// afterwards has no effect on the configured PDO.
/// </summary>
public sealed class PdoMapping
{
    /// <summary>Largest number of entries a byte-aligned mapping can hold within 8 bytes.</summary>
    public const int MaxEntries = 8;

    private readonly List<PdoMappingEntry> _entries = new();
    private ReadOnlyCollection<PdoMappingEntry>? _view;

    /// <summary>Constructs an empty mapping.</summary>
    public PdoMapping() { }

    /// <summary>Constructs a mapping from a copy of <paramref name="entries"/>.</summary>
    public PdoMapping(IEnumerable<PdoMappingEntry> entries)
    {
        if (entries is null) throw new ArgumentNullException(nameof(entries));
        _entries.AddRange(entries);
        Validate();
    }

    /// <summary>Total assembled payload size in bytes across every mapping entry.</summary>
    public int TotalBytes
    {
        get
        {
            int total = 0;
            foreach (var entry in _entries) total += entry.ByteLength;
            return total;
        }
    }

    /// <summary>Read-only view of the mapping entries. The view tracks later
    /// <see cref="Add(PdoMappingEntry)"/> / <see cref="Clear"/> calls and allocates nothing per
    /// access.</summary>
    public IReadOnlyList<PdoMappingEntry> Entries => _view ??= _entries.AsReadOnly();

    /// <summary>Appends a new entry to the mapping.</summary>
    public PdoMapping Add(PdoMappingEntry entry)
    {
        _entries.Add(entry);
        Validate();
        return this;
    }

    /// <summary>Convenience overload of <see cref="Add(PdoMappingEntry)"/>.</summary>
    public PdoMapping Add(ushort index, byte subindex, byte bitLength)
        => Add(new PdoMappingEntry(index, subindex, bitLength));

    /// <summary>Removes every mapped entry.</summary>
    public void Clear() => _entries.Clear();

    /// <summary>Snapshot of the entries as an array (for the node's runtime tables).</summary>
    internal PdoMappingEntry[] ToArray() => _entries.ToArray();

    private void Validate()
    {
        if (TotalBytes > 8)
        {
            _entries.RemoveAt(_entries.Count - 1);
            throw new InvalidOperationException(
                "PDO mapping total exceeds the 8-byte classic CAN limit.");
        }
        if (_entries.Count > MaxEntries)
        {
            _entries.RemoveAt(_entries.Count - 1);
            throw new InvalidOperationException(
                $"PDO mapping holds at most {MaxEntries} byte-aligned entries.");
        }
    }
}
