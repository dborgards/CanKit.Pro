using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CanKit.Pro.CANopen.Pdo;

namespace CanKit.Pro.CANopen.Safety;

/// <summary>
/// The application objects an SRDO carries (CiA DSP 304 V1.0 §8.4.2.2, object 1381h–13C0h):
/// up to <see cref="MaxEntries"/> byte-aligned entries whose widths sum to at most 8 bytes —
/// the plain-data frame. The record in the dictionary holds each entry twice (odd sub-index
/// plain, even sub-index inverted); this type names the objects once.
/// </summary>
public sealed class SrdoMapping
{
    /// <summary>8 one-byte objects fill the frame; the record therefore has 16 sub-indices.</summary>
    public const int MaxEntries = 8;

    private readonly List<PdoMappingEntry> _entries = new();
    private ReadOnlyCollection<PdoMappingEntry>? _view;

    /// <summary>An empty mapping.</summary>
    public SrdoMapping() { }

    /// <summary>The entries, in frame order.</summary>
    public IReadOnlyList<PdoMappingEntry> Entries => _view ??= _entries.AsReadOnly();

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
    public SrdoMapping Add(PdoMappingEntry entry)
    {
        if (entry.IsDummy) throw new InvalidOperationException("An SRDO mapping carries no dummy entries.");
        if (_entries.Count >= MaxEntries) throw new InvalidOperationException($"An SRDO mapping holds at most {MaxEntries} entries.");
        if (TotalBytes + entry.ByteLength > 8) throw new InvalidOperationException("An SRDO carries at most 8 bytes of plain data (§8.1.3.1).");
        _entries.Add(entry);
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
}
