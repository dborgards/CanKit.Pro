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
        var validated = new SrdoMapping();
        foreach (var entry in mapping) validated.Add(entry);
        Mapping = Array.AsReadOnly(validated.ToArray());
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
        return new ForeignSrdoConfiguration(snapshot.PeerNodeId, number, parameter.CobId1, parameter.CobId2,
            record.Mapping, validationTime ?? parameter.ValidationTime,
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
    /// <summary>Observer SRVT.</summary>
    public TimeSpan ValidationTime { get; }
    /// <summary>Observer SCT.</summary>
    public TimeSpan SafeguardCycleTime { get; }
    /// <summary>Explicit application source description.</summary>
    public string Provenance { get; }
    /// <summary>Full acquisition and per-entry provenance when created from a snapshot.</summary>
    public PeerSafetySnapshot? SourceSnapshot { get; private set; }
    internal int TotalBytes { get; }
}
