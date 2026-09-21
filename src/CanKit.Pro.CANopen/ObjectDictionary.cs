using System;
using System.Collections.Generic;
using CanKit.Pro.CANopen.Sdo;

namespace CanKit.Pro.CANopen;

/// <summary>
/// Thread-safe in-memory Object Dictionary keyed by <c>(index, subindex)</c> per CiA 301 §7.4.6
/// (FR-CO-001). Provides typed read/write accessors that enforce the declared
/// <see cref="OdDataType"/>; typed reads against an entry of the wrong data type throw
/// <see cref="InvalidOperationException"/>.
/// </summary>
/// <remarks>
/// <para>
/// The dictionary is the single source of truth shared by the local application, the SDO server,
/// the PDO engine and — for the communication-profile objects the node manages (<c>1005h</c>,
/// <c>1006h</c>, <c>1014h</c>, <c>1016h</c>, <c>1017h</c>, <c>100Ch</c>, <c>100Dh</c>, the SDO
/// and PDO parameter records) — the node's own runtime configuration. Every value the dictionary
/// accepts for one of those objects reaches the service it describes, whether it arrived over
/// SDO or through a local write here; the node's public methods (<c>ConfigureTpdo</c>,
/// <c>StartHeartbeatProducer</c>, …) are themselves writes to these objects.
/// </para>
/// <para>
/// All lookups take the internal lock briefly to avoid tearing when the SDO server writes
/// concurrently with an application read; individual entry values are copied out under the
/// lock so no reference leaks back to caller state. Writes to managed objects are validated
/// before they are stored; a rejected local write throws <see cref="ArgumentException"/> whose
/// message names the CiA 301 abort code the same write would have produced over SDO.
/// </para>
/// <para>
/// Storage is a <see cref="Dictionary{TKey,TValue}"/> plus a single mutex, sized for the tens to
/// hundreds of entries a device or tool node carries. If profiles grow into the thousands the
/// storage can be swapped without changing the public surface.
/// </para>
/// </remarks>
public sealed class ObjectDictionary
{
    private readonly Dictionary<uint, OdEntry> _entries = new();
    private readonly object _sync = new();
    // Writers queue here so that a write's validation and its store are one transaction: the
    // validators read the dictionary (a 1016h duplicate check, "not while valid" rules), and two
    // writes validating against the same state could both pass a rule that only one of them
    // may. Readers never take this lock, so a validator reading under _sync cannot deadlock.
    private readonly object _writeGate = new();

    /// <summary>
    /// Internal hook for <see cref="CanOpenNode"/>: raised after a value-mutating write
    /// (<see cref="WriteRaw"/> / <see cref="WriteUnsigned"/>, or an <c>Add*</c> call that
    /// replaced an existing entry) completes successfully. The invocation happens synchronously
    /// on the writing thread, outside the internal lock, so subscribers must marshal to their
    /// own context instead of doing expensive work inline.
    /// </summary>
    internal event Action<ushort, byte>? EntryWritten;

    /// <summary>
    /// Internal hook for <see cref="CanOpenNode"/>: consulted before a value is stored. Returns
    /// the decision for the write — store, reject with an SDO abort code, or "handled, do not
    /// store" (used by the <c>1010h</c>/<c>1011h</c> signature objects, whose write is a command
    /// rather than a value). Runs on the writing thread, outside the lock.
    /// </summary>
    internal Func<ushort, byte, byte[], OdWriteDecision>? WriteValidator { get; set; }

    /// <summary>
    /// Internal hook for <see cref="CanOpenNode"/>: consulted for every <c>Add*</c> call, whether
    /// it would replace an existing entry or add a new sub-index. Returning
    /// <see langword="false"/> makes the call throw, which is how the node keeps its managed
    /// communication objects from being re-declared with a different type or access, or extended
    /// by a sub-index it does not implement (a reserved <c>1800h:04</c> the generic SDO server
    /// would otherwise serve), behind its back. The node declares its own through
    /// <see cref="Declare"/>.
    /// </summary>
    internal Func<ushort, byte, bool>? DeclareGuard { get; set; }

    /// <summary>Total number of registered <c>(index, subindex)</c> entries.</summary>
    public int Count
    {
        get { lock (_sync) return _entries.Count; }
    }

    /// <summary>Adds or replaces an <see cref="OdDataType.Unsigned8"/> entry.</summary>
    public OdEntry AddU8(ushort index, byte subindex, byte value, OdAccess access = OdAccess.ReadWrite,
        bool pdoMappable = true)
        => Add(index, subindex, OdDataType.Unsigned8, access, new[] { value }, pdoMappable);

    /// <summary>Adds or replaces an <see cref="OdDataType.Unsigned16"/> entry.</summary>
    public OdEntry AddU16(ushort index, byte subindex, ushort value, OdAccess access = OdAccess.ReadWrite,
        bool pdoMappable = true)
        => Add(index, subindex, OdDataType.Unsigned16, access,
            new[] { (byte)(value & 0xFF), (byte)((value >> 8) & 0xFF) }, pdoMappable);

    /// <summary>Adds or replaces an <see cref="OdDataType.Unsigned32"/> entry.</summary>
    public OdEntry AddU32(ushort index, byte subindex, uint value, OdAccess access = OdAccess.ReadWrite,
        bool pdoMappable = true)
        => Add(index, subindex, OdDataType.Unsigned32, access, EncodeU32(value), pdoMappable);

    /// <summary>Adds or replaces an <see cref="OdDataType.Integer8"/> entry.</summary>
    public OdEntry AddI8(ushort index, byte subindex, sbyte value, OdAccess access = OdAccess.ReadWrite,
        bool pdoMappable = true)
        => Add(index, subindex, OdDataType.Integer8, access, new[] { unchecked((byte)value) }, pdoMappable);

    /// <summary>Adds or replaces an <see cref="OdDataType.Integer16"/> entry.</summary>
    public OdEntry AddI16(ushort index, byte subindex, short value, OdAccess access = OdAccess.ReadWrite,
        bool pdoMappable = true)
    {
        var u = unchecked((ushort)value);
        return Add(index, subindex, OdDataType.Integer16, access,
            new[] { (byte)(u & 0xFF), (byte)((u >> 8) & 0xFF) }, pdoMappable);
    }

    /// <summary>Adds or replaces an <see cref="OdDataType.Integer32"/> entry.</summary>
    public OdEntry AddI32(ushort index, byte subindex, int value, OdAccess access = OdAccess.ReadWrite,
        bool pdoMappable = true)
        => Add(index, subindex, OdDataType.Integer32, access, EncodeU32(unchecked((uint)value)), pdoMappable);

    /// <summary>Adds or replaces an <see cref="OdDataType.Domain"/> entry with the given initial
    /// bytes (may be empty; will be resized on later writes).</summary>
    public OdEntry AddDomain(ushort index, byte subindex, byte[] value, OdAccess access = OdAccess.ReadWrite,
        bool pdoMappable = true)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        var copy = new byte[value.Length];
        Buffer.BlockCopy(value, 0, copy, 0, value.Length);
        return Add(index, subindex, OdDataType.Domain, access, copy, pdoMappable);
    }

    /// <summary>Attempts to look up the entry for <paramref name="index"/>/<paramref name="subindex"/>.</summary>
    public bool TryGet(ushort index, byte subindex, out OdEntry entry)
    {
        lock (_sync)
        {
            return _entries.TryGetValue(Key(index, subindex), out entry!);
        }
    }

    /// <summary>
    /// Whether any sub-index of <paramref name="index"/> exists. Distinguishes "object does not
    /// exist" (SDO abort <c>0602 0000h</c>) from "sub-index does not exist" (<c>0609 0011h</c>).
    /// </summary>
    public bool ContainsIndex(ushort index)
    {
        lock (_sync)
        {
            foreach (var key in _entries.Keys)
            {
                if ((key >> 8) == index) return true;
            }
            return false;
        }
    }

    /// <summary>Reads an entry's raw little-endian byte value.</summary>
    /// <exception cref="KeyNotFoundException">Thrown when the entry does not exist.</exception>
    public byte[] ReadRaw(ushort index, byte subindex)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(Key(index, subindex), out var entry))
                throw new KeyNotFoundException($"OD entry 0x{index:X4}:{subindex:X2} not found.");
            return entry.GetRawValue();
        }
    }

    /// <summary>
    /// Attempts to atomically snapshot the raw little-endian bytes of an entry under the OD's
    /// internal lock. Preferred over <see cref="TryGet"/> + <see cref="OdEntry.GetRawValue"/>
    /// for the SDO server and TPDO hot paths: a concurrent <see cref="WriteRaw"/> from another
    /// thread cannot tear a snapshot taken under the lock, so readers see either the entire
    /// pre-write value or the entire post-write value — never a mix. Returns
    /// <see langword="false"/> when the entry does not exist (mirrors <see cref="TryGet"/>).
    /// </summary>
    public bool TryReadRaw(ushort index, byte subindex, out byte[] value)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(Key(index, subindex), out var entry))
            {
                value = Array.Empty<byte>();
                return false;
            }
            value = entry.GetRawValue();
            return true;
        }
    }

    /// <summary>Writes raw bytes to an entry; used both by application code and by the SDO
    /// server. Enforces size-consistency for fixed-width data types and — for the
    /// communication-profile objects the node manages — the CiA 301 value rules for that
    /// object.</summary>
    /// <exception cref="KeyNotFoundException">Thrown when the entry does not exist.</exception>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="value"/>'s length
    /// does not match the entry's declared size.</exception>
    /// <exception cref="ArgumentException">Thrown when the value is rejected for a managed
    /// communication object; the message carries the SDO abort code the same write would
    /// have produced on the bus.</exception>
    public void WriteRaw(ushort index, byte subindex, byte[] value)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        if (!TryWriteRaw(index, subindex, value, out var abort, throwOnMissingOrSize: true))
        {
            throw new ArgumentException(
                $"Write to OD entry 0x{index:X4}:{subindex:X2} rejected: SDO abort 0x{(uint)abort!.Value:X8} ({abort.Value}).",
                nameof(value));
        }
    }

    /// <summary>
    /// Server-side write: validates and stores <paramref name="value"/>, reporting a rejection as
    /// the abort code to put on the wire instead of throwing. Returns <see langword="true"/> when
    /// the value was accepted (stored, or consumed as a command by the validator).
    /// </summary>
    internal bool TryWriteRaw(ushort index, byte subindex, byte[] value, out SdoAbortCode? abort)
        => TryWriteRaw(index, subindex, value, out abort, throwOnMissingOrSize: false);

    /// <summary>
    /// Runs <paramref name="writes"/> as one transaction: the write gate is held for the whole of
    /// it, so no other writer — an SDO download, a configuration method, a direct write on
    /// another thread — lands between two of its writes. The writes it makes re-enter the gate,
    /// and the validation and the apply each of them raises run inside it as always.
    /// </summary>
    internal void Transaction(Action writes)
    {
        lock (_writeGate)
        {
            writes();
        }
    }

    private bool TryWriteRaw(ushort index, byte subindex, byte[] value, out SdoAbortCode? abort,
        bool throwOnMissingOrSize)
    {
        lock (_writeGate)
        {
            return TryWriteRawLocked(index, subindex, value, out abort, throwOnMissingOrSize);
        }
    }

    private bool TryWriteRawLocked(ushort index, byte subindex, byte[] value, out SdoAbortCode? abort,
        bool throwOnMissingOrSize)
    {
        abort = null;
        OdEntry? entry;
        lock (_sync)
        {
            _entries.TryGetValue(Key(index, subindex), out entry);
        }
        if (entry is null)
        {
            if (throwOnMissingOrSize)
                throw new KeyNotFoundException($"OD entry 0x{index:X4}:{subindex:X2} not found.");
            abort = ContainsIndex(index) ? SdoAbortCode.SubIndexDoesNotExist : SdoAbortCode.ObjectDoesNotExist;
            return false;
        }
        int fixedSize = OdEntryLayout.FixedSize(entry.DataType);
        if (fixedSize > 0 && value.Length != fixedSize)
        {
            if (throwOnMissingOrSize)
                throw new InvalidOperationException(
                    $"OD entry 0x{index:X4}:{subindex:X2} expects {fixedSize} bytes for {entry.DataType} but got {value.Length}.");
            abort = value.Length > fixedSize ? SdoAbortCode.LengthTooHigh : SdoAbortCode.LengthTooLow;
            return false;
        }

        var validator = WriteValidator;
        if (validator is not null)
        {
            var decision = validator(index, subindex, value);
            if (decision.Abort is { } code)
            {
                abort = code;
                return false;
            }
            if (!decision.Store) return true;
        }

        var copy = new byte[value.Length];
        Buffer.BlockCopy(value, 0, copy, 0, value.Length);
        lock (_sync)
        {
            // Re-resolve: the entry may have been replaced between the validation and the store.
            if (!_entries.TryGetValue(Key(index, subindex), out entry))
            {
                if (throwOnMissingOrSize)
                    throw new KeyNotFoundException($"OD entry 0x{index:X4}:{subindex:X2} not found.");
                abort = SdoAbortCode.ObjectDoesNotExist;
                return false;
            }
            entry.SetRawValue(copy);
        }
        EntryWritten?.Invoke(index, subindex);
        return true;
    }

    /// <summary>
    /// Stores <paramref name="value"/> without consulting <see cref="WriteValidator"/>. Used by
    /// the node when it restores power-on values on an NMT reset: those values were validated
    /// when they were first accepted, and the "not while the object is valid" rules would
    /// otherwise refuse to put a record back to what it was.
    /// </summary>
    internal void WriteRawUnchecked(ushort index, byte subindex, byte[] value)
    {
        var copy = new byte[value.Length];
        Buffer.BlockCopy(value, 0, copy, 0, value.Length);
        lock (_sync)
        {
            if (!_entries.TryGetValue(Key(index, subindex), out var entry))
                throw new KeyNotFoundException($"OD entry 0x{index:X4}:{subindex:X2} not found.");
            entry.SetRawValue(copy);
        }
        EntryWritten?.Invoke(index, subindex);
    }

    /// <summary>Reads a fixed-width unsigned entry as <see cref="uint"/> (upcasts U8/U16/U32).
    /// Throws when the declared type is not one of those three.</summary>
    public uint ReadUnsigned(ushort index, byte subindex)
    {
        lock (_sync)
        {
            var entry = Require(index, subindex);
            return ReadUnsignedCore(entry, index, subindex);
        }
    }

    /// <summary>Attempts to read a fixed-width unsigned entry (U8/U16/U32) as <see cref="uint"/>.
    /// Returns <see langword="false"/> when the entry does not exist or has another data type.</summary>
    public bool TryReadUnsigned(ushort index, byte subindex, out uint value)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(Key(index, subindex), out var entry)
                || entry.DataType is not (OdDataType.Unsigned8 or OdDataType.Unsigned16 or OdDataType.Unsigned32))
            {
                value = 0;
                return false;
            }
            value = ReadUnsignedCore(entry, index, subindex);
            return true;
        }
    }

    private static uint ReadUnsignedCore(OdEntry entry, ushort index, byte subindex) => entry.DataType switch
    {
        OdDataType.Unsigned8 => entry.RawSpan[0],
        OdDataType.Unsigned16 => (uint)(entry.RawSpan[0] | (entry.RawSpan[1] << 8)),
        OdDataType.Unsigned32 => DecodeU32(entry.RawSpan),
        _ => throw new InvalidOperationException(
            $"OD entry 0x{index:X4}:{subindex:X2} is {entry.DataType}, not an unsigned type."),
    };

    /// <summary>Reads a fixed-width signed entry as <see cref="int"/> (upcasts I8/I16/I32).
    /// Throws when the declared type is not one of those three.</summary>
    public int ReadSigned(ushort index, byte subindex)
    {
        lock (_sync)
        {
            var entry = Require(index, subindex);
            return entry.DataType switch
            {
                OdDataType.Integer8 => unchecked((sbyte)entry.RawSpan[0]),
                OdDataType.Integer16 => unchecked((short)(entry.RawSpan[0] | (entry.RawSpan[1] << 8))),
                OdDataType.Integer32 => unchecked((int)DecodeU32(entry.RawSpan)),
                _ => throw new InvalidOperationException(
                    $"OD entry 0x{index:X4}:{subindex:X2} is {entry.DataType}, not a signed type."),
            };
        }
    }

    /// <summary>
    /// Writes a fixed-width unsigned value into an entry, enforcing the declared type width.
    /// Throws when the target entry is not U8/U16/U32, and <see cref="ArgumentException"/> when a
    /// managed communication object rejects the value (see <see cref="WriteRaw"/>).
    /// </summary>
    public void WriteUnsigned(ushort index, byte subindex, uint value)
    {
        OdDataType type;
        lock (_sync)
        {
            type = Require(index, subindex).DataType;
        }
        byte[] raw = type switch
        {
            OdDataType.Unsigned8 => value > byte.MaxValue
                ? throw ValueOutOfRange(index, subindex, type)
                : new[] { (byte)value },
            OdDataType.Unsigned16 => value > ushort.MaxValue
                ? throw ValueOutOfRange(index, subindex, type)
                : new[] { (byte)(value & 0xFF), (byte)((value >> 8) & 0xFF) },
            OdDataType.Unsigned32 => EncodeU32(value),
            _ => throw new InvalidOperationException(
                $"OD entry 0x{index:X4}:{subindex:X2} is {type}, not an unsigned type."),
        };
        WriteRaw(index, subindex, raw);
    }

    private OdEntry Add(ushort index, byte subindex, OdDataType type, OdAccess access, byte[] value,
        bool pdoMappable)
    {
        var entry = new OdEntry(type, access, value, pdoMappable);
        bool replaced;
        lock (_sync)
        {
            var key = Key(index, subindex);
            replaced = _entries.ContainsKey(key);
            if (DeclareGuard is { } guard && !guard(index, subindex))
            {
                throw new InvalidOperationException(replaced
                    ? $"OD entry 0x{index:X4}:{subindex:X2} is a communication object managed by the node and "
                      + "cannot be re-declared; write its value instead (WriteUnsigned / WriteRaw)."
                    : $"OD object 0x{index:X4} is a communication object managed by the node: its sub-indices are "
                      + "declared by the node, not by the application (1016h grows through AddHeartbeatConsumer).");
            }
            _entries[key] = entry;
        }
        if (replaced) EntryWritten?.Invoke(index, subindex);
        return entry;
    }

    /// <summary>
    /// Declares an entry on behalf of the node itself — a <c>1016h</c> slot it grows by, a device
    /// description being loaded — bypassing <see cref="DeclareGuard"/>: a managed communication
    /// object keeps its identity but takes the access rights and the value the node gives it.
    /// Raises <see cref="EntryWritten"/> when it replaces an entry, like <c>Add*</c>.
    /// </summary>
    internal OdEntry Declare(ushort index, byte subindex, OdDataType type, OdAccess access, byte[] value,
        bool pdoMappable)
    {
        var entry = new OdEntry(type, access, value, pdoMappable);
        bool replaced;
        lock (_sync)
        {
            var key = Key(index, subindex);
            replaced = _entries.ContainsKey(key);
            _entries[key] = entry;
        }
        if (replaced) EntryWritten?.Invoke(index, subindex);
        return entry;
    }

    /// <summary>The <c>(index &lt;&lt; 8 | subindex)</c> keys present right now, for callers that
    /// walk the dictionary (power-on snapshots and restores).</summary>
    internal uint[] SnapshotKeys()
    {
        lock (_sync)
        {
            var keys = new uint[_entries.Count];
            _entries.Keys.CopyTo(keys, 0);
            Array.Sort(keys);
            return keys;
        }
    }

    private OdEntry Require(ushort index, byte subindex)
    {
        if (!_entries.TryGetValue(Key(index, subindex), out var entry))
            throw new KeyNotFoundException($"OD entry 0x{index:X4}:{subindex:X2} not found.");
        return entry;
    }

    private static InvalidOperationException ValueOutOfRange(ushort index, byte subindex, OdDataType type)
        => new($"Value out of range for OD entry 0x{index:X4}:{subindex:X2} ({type}).");

    internal static uint Key(ushort index, byte subindex) => ((uint)index << 8) | subindex;

    internal static byte[] EncodeU32(uint value) => new[]
    {
        (byte)(value & 0xFF),
        (byte)((value >> 8) & 0xFF),
        (byte)((value >> 16) & 0xFF),
        (byte)((value >> 24) & 0xFF),
    };

    internal static uint DecodeU32(ReadOnlySpan<byte> data)
        => (uint)(data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24));
}

/// <summary>
/// Outcome of <see cref="ObjectDictionary.WriteValidator"/>: store the value, reject it with an
/// SDO abort code, or treat the write as a command that was carried out and leaves the stored
/// value unchanged.
/// </summary>
internal readonly struct OdWriteDecision
{
    private OdWriteDecision(bool store, SdoAbortCode? abort)
    {
        Store = store;
        Abort = abort;
    }

    /// <summary>Whether the value is to be stored.</summary>
    public bool Store { get; }

    /// <summary>The abort code when the write is rejected; <see langword="null"/> otherwise.</summary>
    public SdoAbortCode? Abort { get; }

    /// <summary>Accept and store the value.</summary>
    public static OdWriteDecision Accept => new(store: true, abort: null);

    /// <summary>Accept the write as a command; do not change the stored value.</summary>
    public static OdWriteDecision Handled => new(store: false, abort: null);

    /// <summary>Reject the write with <paramref name="code"/>.</summary>
    public static OdWriteDecision Reject(SdoAbortCode code) => new(store: false, abort: code);
}
