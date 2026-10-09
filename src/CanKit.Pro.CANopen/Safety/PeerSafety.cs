using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using CanKit.Pro.CANopen.Pdo;
using EdsDcfNet;
using EdsDcfNet.Models;
using EdsDcfNet.Utilities;

namespace CanKit.Pro.CANopen.Safety;

/// <summary>What a safety node's configuration should be: 1300h and, per SRDO number, the
/// communication parameter and the mapping. Built by hand or from a DCF's parameter values
/// (CiA 306). A record whose direction is 0 is not part of it — it is deleted on the peer.</summary>
public sealed class PeerSafetyConfiguration
{
    private readonly Dictionary<int, (SrdoCommunicationParameter Parameter, SrdoMapping Mapping)> _srdos = new();
    private ReadOnlyDictionary<int, (SrdoCommunicationParameter Parameter, SrdoMapping Mapping)>? _view;

    /// <summary>1300h: 1 when true.</summary>
    public bool GlobalFailsafeCommandEnabled { get; init; }

    /// <summary>The SRDOs to exist on the peer, by number.</summary>
    public IReadOnlyDictionary<int, (SrdoCommunicationParameter Parameter, SrdoMapping Mapping)> Srdos => _view ??= new(_srdos);

    /// <summary>True when at least one SRDO is configured — what makes a peer a safety slave for the boot-up.</summary>
    public bool DeclaresAnySrdo => _srdos.Count > 0;

    /// <summary>Adds or replaces SRDO <paramref name="srdoNumber"/> (1..64). The direction must not
    /// be None. The configuration keeps a copy of <paramref name="mapping"/>: a later change to
    /// the caller's instance does not change it.</summary>
    public PeerSafetyConfiguration Add(int srdoNumber, SrdoCommunicationParameter parameter, SrdoMapping mapping)
    {
        if (srdoNumber is < 1 or > SrdoRecords.MaxSrdoCount) throw new ArgumentOutOfRangeException(nameof(srdoNumber));
        if (parameter.Direction == SrdoDirection.None) throw new ArgumentException("An SRDO in a configuration has a direction; leave it out to delete it.", nameof(parameter));
        if (mapping is null) throw new ArgumentNullException(nameof(mapping));
        _srdos[srdoNumber] = (parameter, SrdoMapping.FromEntries(mapping.ToArray()));
        return this;
    }

    /// <summary>Reads 1300h, 1301h–1340h and 1381h–13C0h from the file's ParameterValue (DefaultValue
    /// when absent, $NODEID resolved with <paramref name="nodeId"/>), by the rules a device loading
    /// the same file follows, so that step D against a slave's own DCF expects what the slave holds.
    /// Records the file does not declare, or declares with direction 0, are absent. A sub-index the
    /// file leaves out keeps the device's default (§8.4.2.2): 25 ms for sub-index 2; for sub-indices
    /// 5 and 6 the pre-defined pair of SRDO 1 of a node-id ≤ 64 (§8.3.3), and any other SRDO is
    /// absent, because it has no COB-ID to be created with. A sub-index 3 left out, or 0, gets the
    /// default of 20 ms: the device stores that value and recomputes its checksum from it. The SRDO
    /// is absent without a mapping record, when a plain mapping slot up to the count is missing, 0
    /// or not byte-aligned, or when an inverted slot is missing or differs from its plain slot
    /// (§8.4.2.3): the device leaves it deleted then. Other malformed values make the record absent too.</summary>
    public static PeerSafetyConfiguration FromDeviceDescription(CanOpenDeviceDescription description, byte nodeId)
    {
        if (description is null) throw new ArgumentNullException(nameof(description));
        var objects = description.Objects.Objects;
        var configuration = new PeerSafetyConfiguration
        {
            GlobalFailsafeCommandEnabled = objects.TryGetValue(SrdoRecords.GfcParameter, out var gfc) && Value(gfc, 0, nodeId) == 1,
        };
        bool predefined = nodeId is >= 1 and <= 64;
        for (int n = 1; n <= SrdoRecords.MaxSrdoCount; n++)
        {
            if (!objects.TryGetValue(SrdoRecords.CommIndex(n), out var comm)) continue;
            uint direction = Value(comm, 1, nodeId) ?? 0;
            if (direction is 0 or > 2) continue;
            uint? srvt = Value(comm, 3, nodeId);
            uint? cycle = Declares(comm, 2) ? Value(comm, 2, nodeId) : DefaultCycleTimeMilliseconds;
            uint? cob1 = Declares(comm, 5) ? Value(comm, 5, nodeId) : n == 1 && predefined ? CanOpenCobId.SrdoDefaultCobId1(nodeId) : null;
            uint? cob2 = Declares(comm, 6) ? Value(comm, 6, nodeId) : n == 1 && predefined ? CanOpenCobId.SrdoDefaultCobId2(nodeId) : null;
            if (cycle is null || cob1 is null || cob2 is null) continue;
            if (!objects.TryGetValue(SrdoRecords.MapIndex(n), out var map)) continue;
            var mapping = new SrdoMapping();
            uint? count = Declares(map, 0) ? Value(map, 0, nodeId) : 0;
            bool ok = count is { } c && (c & 1) == 0 && c <= SrdoRecords.MappingSubindices;
            for (byte s = 1; ok && s <= count; s += 2)
            {
                uint? raw = Value(map, s, nodeId);
                if (raw is null or 0 || Value(map, (byte)(s + 1), nodeId) != raw) { ok = false; break; }
                try { mapping.Add(new PdoMappingEntry((ushort)(raw.Value >> 16), (byte)(raw.Value >> 8), (byte)raw.Value)); }
                catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidOperationException) { ok = false; }
            }
            if (!ok) continue;
            configuration.Add(n, new SrdoCommunicationParameter((SrdoDirection)direction,
                TimeSpan.FromMilliseconds(cycle.Value), TimeSpan.FromMilliseconds(srvt is null or 0 ? DefaultValidationTimeMilliseconds : srvt.Value),
                cob1.Value & CanOpenCobId.CanIdMask, cob2.Value & CanOpenCobId.CanIdMask), mapping);
        }
        return configuration;
    }

    /// <summary>The default of sub-index 2 (§8.4.2.2), in milliseconds.</summary>
    private const uint DefaultCycleTimeMilliseconds = 25;

    /// <summary>The default of sub-index 3 (§8.4.2.2), in milliseconds.</summary>
    private const uint DefaultValidationTimeMilliseconds = 20;

    /// <summary>Whether the file gives the sub-index a value at all: <see cref="Value"/> is null
    /// both for one it leaves out and for one that does not parse.</summary>
    private static bool Declares(CanOpenObject obj, byte subindex) => !string.IsNullOrEmpty(Text(obj, subindex));

    private static string? Text(CanOpenObject obj, byte subindex)
    {
        if (obj.SubObjects.Count == 0)
            return subindex != 0 ? null : string.IsNullOrEmpty(obj.ParameterValue) ? obj.DefaultValue : obj.ParameterValue;
        return obj.SubObjects.TryGetValue(subindex, out var sub)
            ? string.IsNullOrEmpty(sub.ParameterValue) ? sub.DefaultValue : sub.ParameterValue
            : null;
    }

    private static uint? Value(CanOpenObject obj, byte subindex, byte nodeId)
    {
        var text = Text(obj, subindex);
        if (string.IsNullOrEmpty(text)) return null;
        try
        {
            return Convert.ToUInt32(CanOpenValueConverter.Parse(text!, CanOpenDataType.Unsigned32, nodeId), CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or NotSupportedException or ArgumentException or InvalidCastException)
        {
            return null;
        }
    }
}

/// <summary>One (index, sub-index) whose value on the peer is not what was expected.</summary>
public sealed class PeerSafetyMismatch
{
    internal PeerSafetyMismatch(ushort index, byte subindex, byte[] expected, byte[] actual)
    {
        Index = index; Subindex = subindex; Expected = expected; Actual = actual;
    }
    /// <summary>Object index.</summary>
    public ushort Index { get; }
    /// <summary>Sub-index.</summary>
    public byte Subindex { get; }
    /// <summary>The bytes written (configuration) or computed (verification).</summary>
    public byte[] Expected { get; }
    /// <summary>The bytes read back.</summary>
    public byte[] Actual { get; }
    /// <inheritdoc />
    public override string ToString() => $"0x{Index:X4}:{Subindex:X2} expected {BitConverter.ToString(Expected)} read {BitConverter.ToString(Actual)}";
}

/// <summary>Outcome of <c>ICanOpenSafety.ConfigurePeerSafetyAsync</c> (succeeded =
/// acknowledged with 13FEh = A5h) and <c>ICanOpenSafety.VerifyPeerSafetyConfigurationAsync</c>
/// (succeeded = verified).</summary>
public sealed class PeerSafetyResult
{
    internal PeerSafetyResult(bool succeeded, IReadOnlyList<PeerSafetyMismatch> mismatches)
    {
        Succeeded = succeeded; Mismatches = mismatches;
    }
    /// <summary>No mismatch, and for a configuration the acknowledgement was written and read back.</summary>
    public bool Succeeded { get; }
    /// <summary>Every pair that differed; empty when <see cref="Succeeded"/>.</summary>
    public IReadOnlyList<PeerSafetyMismatch> Mismatches { get; }
}

/// <summary>Outcome of <c>ICanOpenSafety.ObserveForeignSrdoAsync</c>.</summary>
public sealed class ForeignSrdoObserveResult
{
    internal ForeignSrdoObserveResult(uint cobId1, ForeignPdoObservation? observation, string? reason)
    {
        CobId1 = cobId1; Observation = observation; Reason = reason;
    }
    /// <summary>The id of the plain-data frame the caller passed.</summary>
    public uint CobId1 { get; }
    /// <summary>The decoding, with <see cref="ForeignPdoObservation.Kind"/> = <see cref="ForeignPdoKind.Srdo"/>; null when no record matched.</summary>
    public ForeignPdoObservation? Observation { get; }
    /// <summary>Why nothing was decoded, when <see cref="Observation"/> is null or not decoded.</summary>
    public string? Reason { get; }
}
