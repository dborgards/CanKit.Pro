using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CanKit.Pro.CANopen.Pdo;

namespace CanKit.Pro.CANopen.Safety;

/// <summary>
/// The application objects an SRDO carries (CiA DSP 304 V1.0 §8.4.2.2, object 1381h–13C0h;
/// EN 50325-5:2010 §6.4.1.4, Table 8): up to <see cref="MaxEntries"/> byte-aligned entries whose
/// widths sum to at most 8 bytes — the plain-data frame. The record holds a pair per entry: the
/// plain object at the odd sub-index, the object of its bitwise inverted data at the even one.
/// <see cref="Add(PdoMappingEntry)"/> names one object for both (the even slot repeats the odd
/// one, the only form a CanKit device holds); <see cref="Add(PdoMappingEntry, PdoMappingEntry)"/>
/// names a separate inverted object, which a peer may keep (tool and monitor roles, #506). Each entry must name an
/// object of fixed width equal to the entry's: the dictionary, the device-description loader and
/// the peer configuration refuse a string or a domain. That is this implementation's rule, not a
/// clause of DSP 304 — safety process data has a fixed width, and the width is what the mapping
/// entry and the checksum pin.
/// </summary>
public sealed class SrdoMapping
{
    /// <summary>8 one-byte objects fill the frame; the record therefore has 16 sub-indices.</summary>
    public const int MaxEntries = 8;

    private readonly List<PdoMappingEntry> _entries = new();
    private readonly List<PdoMappingEntry> _inverted = new();
    private ReadOnlyCollection<PdoMappingEntry>? _view;
    private ReadOnlyCollection<PdoMappingEntry>? _invertedView;

    /// <summary>An empty mapping.</summary>
    public SrdoMapping() { }

    /// <summary>The entries, in frame order.</summary>
    public IReadOnlyList<PdoMappingEntry> Entries => _view ??= _entries.AsReadOnly();

    /// <summary>The object at the even sub-index of each entry, in frame order: the entry itself
    /// unless <see cref="Add(PdoMappingEntry, PdoMappingEntry)"/> named another one.</summary>
    public IReadOnlyList<PdoMappingEntry> InvertedEntries => _invertedView ??= _inverted.AsReadOnly();

    /// <summary>True when at least one entry's inverted data live in an object of their own.</summary>
    public bool HasSeparateInvertedObjects
    {
        get
        {
            for (int i = 0; i < _entries.Count; i++)
                if (!_inverted[i].Equals(_entries[i])) return true;
            return false;
        }
    }

    /// <summary>Length of the plain-data frame in bytes.</summary>
    public int TotalBytes
    {
        get
        {
            int total = 0;
            foreach (var entry in _entries) total += entry.ByteLength;
            return total;
        }
    }

    /// <summary>Appends an object. Dummy entries (0002h–0007h) are not accepted: safety data is
    /// never padding.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The width is not a byte multiple of 8..64 bits.</exception>
    /// <exception cref="InvalidOperationException">A ninth entry, a dummy, or more than 8 bytes.</exception>
    public SrdoMapping Add(ushort index, byte subindex, byte bitLength) => Add(new PdoMappingEntry(index, subindex, bitLength));

    /// <inheritdoc cref="Add(ushort, byte, byte)"/>
    public SrdoMapping Add(PdoMappingEntry entry) => Add(entry, entry);

    /// <summary>Appends an object whose bitwise inverted data live in <paramref name="inverted"/>,
    /// the object the even sub-index names (EN 50325-5:2010 §6.4.1.4, Table 8). The second frame
    /// is still the bitwise inverse of the first (§7.1.1), so the two objects must have the same
    /// width; the inverted one is not a dummy either. For a peer: a CanKit device holds only
    /// mappings whose inverted object is the plain one, and its configuration refuses others.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A width is not a byte multiple of 8..64 bits.</exception>
    /// <exception cref="ArgumentException"><paramref name="inverted"/> has another width than <paramref name="plain"/>.</exception>
    /// <exception cref="InvalidOperationException">A ninth entry, a dummy, or more than 8 bytes.</exception>
    public SrdoMapping Add(PdoMappingEntry plain, PdoMappingEntry inverted)
    {
        var entry = plain;
        // The entry is a struct: default(PdoMappingEntry) has never passed its constructor, so the
        // width rule is applied here again — a 0-bit entry would add no bytes and pass the rest.
        if (entry.BitLength == 0 || entry.BitLength > 64 || entry.BitLength % 8 != 0)
            throw new ArgumentOutOfRangeException(nameof(entry), entry.BitLength,
                "An SRDO mapping entry is a byte multiple of 8..64 bits: this implementation maps byte-aligned objects (CiA DSP 304 §8.4.2.2 allows 2, 4–128 entries).");
        if (entry.IsDummy || inverted.IsDummy) throw new InvalidOperationException("An SRDO mapping carries no dummy entries.");
        if (inverted.BitLength != entry.BitLength)
            throw new ArgumentException(
                $"The inverted object {inverted.Index:X4}h:{inverted.Subindex:X2} has {inverted.BitLength} bits, its plain object {entry.BitLength}: the second frame is the bitwise inverse of the first (EN 50325-5:2010 §7.1.1), so each pair has one width.",
                nameof(inverted));
        if (_entries.Count >= MaxEntries) throw new InvalidOperationException($"An SRDO mapping holds at most {MaxEntries} entries.");
        if (TotalBytes + entry.ByteLength > 8) throw new InvalidOperationException("An SRDO carries at most 8 bytes of plain data (§8.1.3.1).");
        _entries.Add(entry);
        _inverted.Add(inverted);
        return this;
    }

    /// <summary>The entries as an array (a copy).</summary>
    public PdoMappingEntry[] ToArray() => _entries.ToArray();

    internal static SrdoMapping FromEntries(PdoMappingEntry[] entries)
    {
        var mapping = new SrdoMapping();
        foreach (var entry in entries) mapping.Add(entry);
        return mapping;
    }

    internal static SrdoMapping FromPairs((PdoMappingEntry Plain, PdoMappingEntry Inverted)[] pairs)
    {
        var mapping = new SrdoMapping();
        foreach (var (plain, inverted) in pairs) mapping.Add(plain, inverted);
        return mapping;
    }

    /// <summary>A copy with the same pairs.</summary>
    internal SrdoMapping Clone()
    {
        var copy = new SrdoMapping();
        for (int i = 0; i < _entries.Count; i++) copy.Add(_entries[i], _inverted[i]);
        return copy;
    }
}
