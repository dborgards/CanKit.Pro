using System;
using System.Collections.Generic;
using System.Linq;
using CanKit.Pro.CANopen.Pdo;

namespace CanKit.Pro.CANopen.Safety;

/// <summary>Immutable, explicitly prepared input for a passive SRDO monitor. Construction sends nothing.</summary>
public sealed class ForeignSrdoConfiguration
{
    /// <summary>Copies and validates a mapping and explicit observer SRVT/SCT. Peer identity is a
    /// configuration association, never sender identification from the wire. Provenance names the
    /// application's source (for example an approved DCF revision).</summary>
    public ForeignSrdoConfiguration(byte peerNodeId, int number, uint cobId1, uint cobId2,
        IEnumerable<PdoMappingEntry> mapping, TimeSpan validationTime, TimeSpan safeguardCycleTime,
        string provenance)
        : this(peerNodeId, number, cobId1, cobId2, CopyPlain(mapping), validationTime, safeguardCycleTime, provenance)
    {
    }

    /// <summary>As the constructor, for a peer whose mapping may name separate inverted objects
    /// (EN 50325-5:2010 §6.4.1.4, Table 8; #506): <paramref name="mapping"/> carries the pairs. The
    /// monitor checks the second frame against the inverse of the first, as for any SRDO
    /// (§7.1.1), and decodes the plain objects; <see cref="InvertedMapping"/> records what the
    /// even slots name.</summary>
    public static ForeignSrdoConfiguration Create(byte peerNodeId, int number, uint cobId1, uint cobId2,
        SrdoMapping mapping, TimeSpan validationTime, TimeSpan safeguardCycleTime, string provenance)
    {
        if (mapping is null) throw new ArgumentNullException(nameof(mapping));
        return new ForeignSrdoConfiguration(peerNodeId, number, cobId1, cobId2, mapping.Clone(),
            validationTime, safeguardCycleTime, provenance);
    }

    private ForeignSrdoConfiguration(byte peerNodeId, int number, uint cobId1, uint cobId2,
        SrdoMapping? mapping, TimeSpan validationTime, TimeSpan safeguardCycleTime,
        string provenance)
    {
        if (peerNodeId is < 1 or > 127) throw new ArgumentOutOfRangeException(nameof(peerNodeId));
        if (number is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(number));
        if (!SrdoFrames.IsCobIdPair(cobId1, cobId2)) throw new ArgumentException("Expected a consecutive DS304 odd/even COB-ID pair.", nameof(cobId1));
        if (validationTime <= TimeSpan.Zero || validationTime > TimeSpan.FromMilliseconds(255))
            throw new ArgumentOutOfRangeException(nameof(validationTime));
        if (safeguardCycleTime <= TimeSpan.Zero || safeguardCycleTime > TimeSpan.FromMilliseconds(65535))
            throw new ArgumentOutOfRangeException(nameof(safeguardCycleTime));
        if (mapping is null) throw new ArgumentNullException(nameof(mapping));
        if (string.IsNullOrWhiteSpace(provenance)) throw new ArgumentException("Configuration provenance is required.", nameof(provenance));
        var validated = mapping;
        Mapping = Array.AsReadOnly(validated.ToArray());
        InvertedMapping = Array.AsReadOnly(validated.InvertedEntries.ToArray());
        TotalBytes = validated.TotalBytes;
        PeerNodeId = peerNodeId; Number = number; CobId1 = cobId1; CobId2 = cobId2;
        ValidationTime = validationTime; SafeguardCycleTime = safeguardCycleTime;
        Provenance = provenance; ConfigurationId = Guid.NewGuid();
    }

    /// <summary>Creates observer input from an eligible snapshot. Producer refresh time and
    /// checksum SRVT defaults are not observer deadlines: both overrides are required for a
    /// producer. Receiver records can supply their explicit consumer timings.</summary>
    public static ForeignSrdoConfiguration FromSnapshot(PeerSafetySnapshot snapshot, int number,
        TimeSpan? validationTime = null, TimeSpan? safeguardCycleTime = null)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        var record = snapshot.Srdos.SingleOrDefault(s => s.Number == number);
        if (record is not { EligibleForMonitoring: true, Parameter: { } parameter })
            throw new ArgumentException("The snapshot has no eligible SRDO with this number.", nameof(number));
        if (parameter.Direction == SrdoDirection.Transmit && (!validationTime.HasValue || !safeguardCycleTime.HasValue))
            throw new ArgumentException("Producer snapshots require explicit observer SRVT and SCT.", nameof(snapshot));
        var mapping = new SrdoMapping();
        for (int i = 0; i < record.Mapping.Count; i++)
            mapping.Add(record.Mapping[i], record.InvertedMapping[i]); // a snapshot keeps one inverted entry per plain one
        return new ForeignSrdoConfiguration(snapshot.PeerNodeId, number, parameter.CobId1, parameter.CobId2,
            mapping, validationTime ?? parameter.ValidationTime,
            safeguardCycleTime ?? parameter.RefreshOrSafeguardCycleTime, "PeerSafetySnapshot")
        { SourceSnapshot = snapshot, ConfigurationId = snapshot.ConfigurationId };
    }

    /// <summary>Prepared configuration identity, or the source acquisition's identity.</summary>
    public Guid ConfigurationId { get; private set; }
    /// <summary>Configuration owner, not authenticated wire sender.</summary>
    public byte PeerNodeId { get; }
    /// <summary>SRDO number in that configuration.</summary>
    public int Number { get; }
    /// <summary>Normal frame identifier.</summary>
    public uint CobId1 { get; }
    /// <summary>Inverted frame identifier.</summary>
    public uint CobId2 { get; }
    /// <summary>Owned, validated mapping in payload order.</summary>
    public IReadOnlyList<PdoMappingEntry> Mapping { get; }
    /// <summary>The object of each entry's inverted data, in the same order: the entry itself
    /// unless the configuration names a separate one.</summary>
    public IReadOnlyList<PdoMappingEntry> InvertedMapping { get; }
    /// <summary>Observer SRVT.</summary>
    public TimeSpan ValidationTime { get; }
    /// <summary>Observer SCT.</summary>
    public TimeSpan SafeguardCycleTime { get; }
    /// <summary>Explicit application source description.</summary>
    public string Provenance { get; }
    /// <summary>Full acquisition and per-entry provenance when created from a snapshot.</summary>
    public PeerSafetySnapshot? SourceSnapshot { get; private set; }
    internal int TotalBytes { get; }

    private static SrdoMapping CopyPlain(IEnumerable<PdoMappingEntry> mapping)
    {
        if (mapping is null) return null!; // the constructor reports it, after the other arguments
        var validated = new SrdoMapping();
        foreach (var entry in mapping) validated.Add(entry);
        return validated;
    }
}
