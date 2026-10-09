using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

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

    /// <summary>Adds or replaces SRDO <paramref name="srdoNumber"/> (1..64). What the peer's
    /// validator would refuse whatever its state is refused here, before a configuration can delete
    /// the peer's SRDOs (CiA DSP 304 §8.4.2.2): the direction is tx or rx (None: leave the SRDO out
    /// to delete it); COB-ID 1 is an odd id of 101h..17Fh and COB-ID 2 the next one; the refresh
    /// time or SCT is 1..65535 ms and the SRVT 1..255 ms after rounding to whole milliseconds; and
    /// no other SRDO of this configuration uses either COB-ID. The configuration keeps a copy of
    /// <paramref name="mapping"/>: a later change to the caller's instance does not change it.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The number is not 1..64, or a time is out of range.</exception>
    /// <exception cref="ArgumentException">The direction, the COB-IDs, or a COB-ID another SRDO of the configuration uses.</exception>
    public PeerSafetyConfiguration Add(int srdoNumber, SrdoCommunicationParameter parameter, SrdoMapping mapping)
    {
        if (srdoNumber is < 1 or > SrdoRecords.MaxSrdoCount) throw new ArgumentOutOfRangeException(nameof(srdoNumber));
        if (parameter.Direction == SrdoDirection.None) throw new ArgumentException("An SRDO in a configuration has a direction; leave it out to delete it.", nameof(parameter));
        if (parameter.Direction is not (SrdoDirection.Transmit or SrdoDirection.Receive))
            throw new ArgumentException($"SRDO {srdoNumber}: the direction is 1 (tx) or 2 (rx), not {(byte)parameter.Direction} (CiA DSP 304 §8.4.2.2, sub-index 1).", nameof(parameter));
        if (!SrdoFrames.IsCobIdPair(parameter.CobId1, parameter.CobId2))
            throw new ArgumentException(
                $"SRDO {srdoNumber}: COB-ID 1 is an odd id of 101h..17Fh and COB-ID 2 the next one, not {parameter.CobId1:X}h/{parameter.CobId2:X}h (CiA DSP 304 §8.4.2.2, Figure 7).", nameof(parameter));
        ValidateTimes(srdoNumber, parameter, nameof(parameter));
        if (mapping is null) throw new ArgumentNullException(nameof(mapping));
        foreach (var other in _srdos)
        {
            if (other.Key == srdoNumber) continue;
            var ids = other.Value.Parameter;
            if (ids.CobId1 == parameter.CobId1 || ids.CobId1 == parameter.CobId2 || ids.CobId2 == parameter.CobId1 || ids.CobId2 == parameter.CobId2)
                throw new ArgumentException(
                    $"SRDO {srdoNumber}: COB-IDs {parameter.CobId1:X}h/{parameter.CobId2:X}h are used by SRDO {other.Key} of this configuration; one CAN-ID carries one SRDO (CiA DSP 304 §8.4.2.2).", nameof(parameter));
        }
        _srdos[srdoNumber] = (parameter, SrdoMapping.FromEntries(mapping.ToArray()));
        return this;
    }

    /// <summary>Sub-index 2 is 1..65535 ms and sub-index 3 1..255 ms (§8.4.2.2), checked after
    /// rounding to whole milliseconds. Sub-index 3 is bounded for a producer too: the checksum
    /// covers it whatever the direction (§8.4.2.2 field c).</summary>
    internal static void ValidateTimes(int srdoNumber, in SrdoCommunicationParameter parameter, string paramName)
    {
        CheckMilliseconds(srdoNumber, "cycle time", parameter.RefreshOrSafeguardCycleTime, 1, ushort.MaxValue, paramName);
        CheckMilliseconds(srdoNumber, "validation time", parameter.ValidationTime, 1, byte.MaxValue, paramName);
    }

    internal static ushort CheckMilliseconds(int srdoNumber, string what, TimeSpan time, int min, int max, string paramName)
    {
        var ms = Math.Round(time.TotalMilliseconds);
        if (!(ms >= min && ms <= max))
            throw new ArgumentOutOfRangeException(paramName, time,
                $"SRDO {srdoNumber}: the {what} must be {min}..{max} ms, not {time.TotalMilliseconds} ms (CiA DSP 304 §8.4.2.2).");
        return (ushort)ms;
    }

    /// <summary>What a device with node-id <paramref name="nodeId"/> holds after loading
    /// <paramref name="description"/>: 1300h and every SRDO the device creates from the file,
    /// with the records it then holds. It is built by the node's own loader — the same code, over
    /// a dictionary of its own — not by a second reading of the file: the §8.4.2.2 defaults for
    /// what the file leaves out or the device refuses (25 ms, 20 ms, the pre-defined COB-IDs of
    /// SRDO 1 for a node-id 1..64, §8.3.3), the device's write rules (§8.4.2.2, §8.4.2.3:
    /// consecutive COB-IDs, an inverted slot equal to its plain one, no COB-ID of another
    /// existing SRDO, mapped objects the file declares), and the SRDO left deleted where the
    /// loader leaves it deleted. So step D against a slave's own DCF expects what the slave
    /// holds. Parameter values are read as ParameterValue, DefaultValue where none, $NODEID
    /// resolved with <paramref name="nodeId"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="description"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="nodeId"/> is not 1..127.</exception>
    public static PeerSafetyConfiguration FromDeviceDescription(CanOpenDeviceDescription description, byte nodeId)
    {
        if (description is null) throw new ArgumentNullException(nameof(description));
        var od = CanOpenNode.LoadDescribedSafetyObjects(description, nodeId);
        var configuration = new PeerSafetyConfiguration
        {
            GlobalFailsafeCommandEnabled = od.TryReadUnsigned(SrdoRecords.GfcParameter, 0x00, out var gfc) && gfc == 1,
        };
        for (int n = 1; n <= SrdoRecords.MaxSrdoCount; n++)
        {
            if (!SrdoRecords.TryReadCommunication(od, n, out var parameter) || parameter.Direction == SrdoDirection.None) continue;
            configuration.Add(n, parameter, SrdoMapping.FromEntries(SrdoRecords.ReadMapping(od, n)));
        }
        return configuration;
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
