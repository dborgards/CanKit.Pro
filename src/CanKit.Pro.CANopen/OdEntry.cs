using System;

namespace CanKit.Pro.CANopen;

/// <summary>
/// Data types supported by the Object Dictionary (CiA 301 §7.4.7 Table 44, the data-type
/// indices <c>0001h</c>–<c>001Bh</c> a device description names). The fixed-width types imply
/// their on-the-wire size (little-endian) for SDO expedited encoding and PDO mapping
/// (FR-CO-001 / FR-CO-005); the strings and <see cref="Domain"/> are variable-length.
/// </summary>
public enum OdDataType : byte
{
    /// <summary>1-byte boolean (CiA 301 <c>BOOLEAN</c>, 0 = false).</summary>
    Boolean = 0x01,

    /// <summary>1-byte unsigned integer (CiA 301 <c>UNSIGNED8</c>).</summary>
    Unsigned8 = 0x05,

    /// <summary>2-byte unsigned integer, little-endian (CiA 301 <c>UNSIGNED16</c>).</summary>
    Unsigned16 = 0x06,

    /// <summary>4-byte unsigned integer, little-endian (CiA 301 <c>UNSIGNED32</c>).</summary>
    Unsigned32 = 0x07,

    /// <summary>1-byte signed integer (CiA 301 <c>INTEGER8</c>).</summary>
    Integer8 = 0x02,

    /// <summary>2-byte signed integer, little-endian (CiA 301 <c>INTEGER16</c>).</summary>
    Integer16 = 0x03,

    /// <summary>4-byte signed integer, little-endian (CiA 301 <c>INTEGER32</c>).</summary>
    Integer32 = 0x04,

    /// <summary>4-byte IEEE 754 single (CiA 301 <c>REAL32</c>), little-endian.</summary>
    Real32 = 0x08,

    /// <summary>Variable-length ISO 646 string (CiA 301 <c>VISIBLE_STRING</c>), one byte per
    /// character, no terminator.</summary>
    VisibleString = 0x09,

    /// <summary>Variable-length byte string (CiA 301 <c>OCTET_STRING</c>).</summary>
    OctetString = 0x0A,

    /// <summary>Variable-length UTF-16LE string (CiA 301 <c>UNICODE_STRING</c>).</summary>
    UnicodeString = 0x0B,

    /// <summary>Variable-length byte string (CiA 301 <c>DOMAIN</c>). Always exchanged via SDO
    /// segmented transfer regardless of length.</summary>
    Domain = 0x0F,

    /// <summary>8-byte IEEE 754 double (CiA 301 <c>REAL64</c>), little-endian.</summary>
    Real64 = 0x11,

    /// <summary>8-byte signed integer, little-endian (CiA 301 <c>INTEGER64</c>).</summary>
    Integer64 = 0x15,

    /// <summary>8-byte unsigned integer, little-endian (CiA 301 <c>UNSIGNED64</c>).</summary>
    Unsigned64 = 0x1B,
}

/// <summary>
/// Read/write access flag for an <see cref="OdEntry"/>. SDO write attempts against a read-only
/// entry produce SDO abort <c>0x06010002</c> ("attempt to write a read-only object"). CiA 301's
/// <c>const</c> access is represented as <see cref="ReadOnly"/>: on the bus both behave the same.
/// </summary>
[Flags]
public enum OdAccess : byte
{
    /// <summary>Value is only readable.</summary>
    ReadOnly = 1,

    /// <summary>Value is only writable.</summary>
    WriteOnly = 2,

    /// <summary>Value is both readable and writable (default).</summary>
    ReadWrite = ReadOnly | WriteOnly,
}

/// <summary>
/// A single subindex entry in the local Object Dictionary (FR-CO-001): value + declared data
/// type + access flags + PDO mappability. Stored inside <see cref="ObjectDictionary"/>; not
/// intended to be constructed directly by callers — use the fluent <c>Add*</c> methods on the
/// dictionary.
/// </summary>
/// <remarks>
/// The stored raw value is a little-endian byte array in the same layout SDO expedited encoding
/// uses on the wire. This keeps the SDO server code path allocation-cheap (no round-trip through
/// typed converters on every request) while typed accessors on <see cref="ObjectDictionary"/>
/// preserve type-safety for local reads/writes. The value is only ever read or replaced under
/// the dictionary's lock; the non-copying <see cref="RawSpan"/> view is therefore internal and
/// used solely from code paths that already hold that lock.
/// </remarks>
public sealed class OdEntry
{
    private byte[] _value;

    internal OdEntry(OdDataType type, OdAccess access, byte[] value, bool pdoMappable)
    {
        DataType = type;
        Access = access;
        PdoMappable = pdoMappable;
        _value = value;
    }

    /// <summary>CiA 301 data type of the entry.</summary>
    public OdDataType DataType { get; }

    /// <summary>Access permissions (read-only / write-only / read-write).</summary>
    public OdAccess Access { get; }

    /// <summary>
    /// Whether the entry may be mapped into a PDO — the <c>PDOMapping</c> attribute of a
    /// CiA 306 device description. Communication-profile objects (<c>1000h</c>–<c>1FFFh</c>)
    /// are not mappable per CiA 301; application objects default to mappable. A mapping
    /// entry that references a non-mappable object is rejected with SDO abort
    /// <c>0604 0041h</c> (CiA 301 §7.5.2.36 / §7.5.2.38).
    /// </summary>
    public bool PdoMappable { get; }

    /// <summary>Fixed on-the-wire size in bytes for the entry's <see cref="DataType"/>. For
    /// <see cref="OdDataType.Domain"/> the value is variable and this property returns the
    /// current stored length.</summary>
    public int Size => _value.Length;

    /// <summary>Snapshot of the raw little-endian bytes. Safe to hand out — callers get a
    /// fresh copy and cannot mutate the dictionary's private buffer.</summary>
    public byte[] GetRawValue()
    {
        var current = _value;
        var copy = new byte[current.Length];
        Buffer.BlockCopy(current, 0, copy, 0, current.Length);
        return copy;
    }

    /// <summary>
    /// Replaces the stored raw bytes. Called by the SDO server on writes as well as by the
    /// dictionary's typed setters after they have serialized the caller's value. Enforces that
    /// fixed-width types keep their declared size; DOMAIN may grow/shrink.
    /// </summary>
    internal void SetRawValue(byte[] value)
    {
        int fixedSize = OdEntryLayout.FixedSize(DataType);
        if (fixedSize > 0 && value.Length != fixedSize)
            throw new InvalidOperationException(
                $"OD entry expects {fixedSize} bytes for {DataType} but got {value.Length}.");
        _value = value;
    }

    /// <summary>Non-copying view of the current raw bytes, for callers holding the OD lock.</summary>
    internal ReadOnlySpan<byte> RawSpan => _value;
}

internal static class OdEntryLayout
{
    internal static int FixedSize(OdDataType type) => type switch
    {
        OdDataType.Boolean => 1,
        OdDataType.Unsigned8 => 1,
        OdDataType.Integer8 => 1,
        OdDataType.Unsigned16 => 2,
        OdDataType.Integer16 => 2,
        OdDataType.Unsigned32 => 4,
        OdDataType.Integer32 => 4,
        OdDataType.Real32 => 4,
        OdDataType.Real64 => 8,
        OdDataType.Integer64 => 8,
        OdDataType.Unsigned64 => 8,
        OdDataType.VisibleString => -1,
        OdDataType.OctetString => -1,
        OdDataType.UnicodeString => -1,
        OdDataType.Domain => -1,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown OD data type."),
    };
}
