using System;
using System.Collections.Generic;
using System.Linq;
using CanKit.Pro.CANopen.Pdo;
using CanKit.Pro.CANopen.Sdo;

namespace CanKit.Pro.CANopen.Safety;

/// <summary>Where a captured value came from; file values never stand in for live evidence.</summary>
public enum SafetyValueOrigin
{
    /// <summary>No usable value was obtained.</summary>
    Unavailable,
    /// <summary>Bytes returned by this acquisition's SDO upload.</summary>
    Live,
    /// <summary>Bound file's ParameterValue, or its DefaultValue where absent.</summary>
    DeviceDescription,
    /// <summary>Explicit CiA 319 SRVT rule for an unimplemented producer parameter.</summary>
    CiA319Default,
}

/// <summary>A comparison, separate from the device's validity flag and from safety approval.</summary>
public enum SafetyComparison
{
    /// <summary>No expectation was supplied.</summary>
    NotRequested,
    /// <summary>Required evidence was unavailable.</summary>
    Unavailable,
    /// <summary>The available values match under the documented implementation rules.</summary>
    Match,
    /// <summary>At least one available value differs.</summary>
    Mismatch,
}

/// <summary>Sequential uploads cannot establish an atomic device image.</summary>
public enum PeerSafetySnapshotConsistency
{
    /// <summary>No change was demonstrated; atomic consistency remains unconfirmed.</summary>
    Unconfirmed,
    /// <summary>Live or explicit SRVT-rule evidence differed when read again.</summary>
    ChangeObserved,
}

/// <summary>Machine-readable diagnostic category (#403).</summary>
public enum PeerSafetyFindingKind
{
    /// <summary>An SDO upload ended in an abort; the entry retains code and origin.</summary>
    SdoAbort,
    /// <summary>The local peer-description gate refused an entry.</summary>
    AccessRefused,
    /// <summary>A transport/state refusal made a value unavailable.</summary>
    Unavailable,
    /// <summary>Wrong width, count, direction, timing or communication value.</summary>
    InvalidValue,
    /// <summary>The mapping cannot be interpreted by this implementation.</summary>
    InvalidMapping,
    /// <summary>The recomputed signature differs from the live stored signature.</summary>
    SignatureMismatch,
    /// <summary>A live value differs from an optional expectation.</summary>
    ExpectedMismatch,
    /// <summary>An optional expectation could not be compared.</summary>
    ExpectedUnavailable,
    /// <summary>Repeated live uploads differ.</summary>
    ConfigurationChanged,
    /// <summary>An interoperability/normative choice remains open in #289.</summary>
    NormativeUncertainty,
}

/// <summary>One immutable diagnostic, located at an object entry.</summary>
public sealed class PeerSafetyFinding
{
    internal PeerSafetyFinding(ushort index, byte subindex, PeerSafetyFindingKind kind, string message)
    { Index = index; Subindex = subindex; Kind = kind; Message = message; }
    /// <summary>Object index; zero for an acquisition-wide finding.</summary>
    public ushort Index { get; }
    /// <summary>Object sub-index.</summary>
    public byte Subindex { get; }
    /// <summary>Diagnostic category.</summary>
    public PeerSafetyFindingKind Kind { get; }
    /// <summary>Diagnostic explanation; not a safety approval.</summary>
    public string Message { get; }
}

/// <summary>Immutable raw bytes and provenance of one attempted upload.</summary>
public sealed class PeerSafetyEntry
{
    internal PeerSafetyEntry(ushort index, byte subindex, byte[] value, SafetyValueOrigin origin,
        uint? abortCode = null, SdoAbortOrigin? abortOrigin = null)
    {
        Index = index; Subindex = subindex; Origin = origin;
        RawValue = Array.AsReadOnly((byte[])value.Clone());
        AbortCode = abortCode; AbortOrigin = abortOrigin;
    }
    /// <summary>Object index.</summary>
    public ushort Index { get; }
    /// <summary>Object sub-index.</summary>
    public byte Subindex { get; }
    /// <summary>Owned, read-only bytes; empty when unavailable.</summary>
    public IReadOnlyList<byte> RawValue { get; }
    /// <summary>Live, file/default, explicitly derived, or unavailable.</summary>
    public SafetyValueOrigin Origin { get; }
    /// <summary>SDO abort code, retained even when a file/default value is available.</summary>
    public uint? AbortCode { get; }
    /// <summary>Side that originated the abort.</summary>
    public SdoAbortOrigin? AbortOrigin { get; }
    /// <summary>Little-endian unsigned value for a 1-, 2- or 4-byte entry; otherwise null.</summary>
    public uint? UnsignedValue => RawValue.Count switch
    {
        1 => RawValue[0],
        2 => (uint)(RawValue[0] | RawValue[1] << 8),
        4 => (uint)(RawValue[0] | RawValue[1] << 8 | RawValue[2] << 16 | RawValue[3] << 24),
        _ => null,
    };
}

/// <summary>Immutable interpreted SRDO records and independent comparison outcomes.</summary>
public sealed class PeerSafetySrdoSnapshot
{
    internal PeerSafetySrdoSnapshot(int number, SrdoCommunicationParameter? parameter, PdoMappingEntry[] mapping,
        ushort? stored, ushort? computed, SafetyComparison signature, bool eligible)
    {
        Number = number; Parameter = parameter; Mapping = Array.AsReadOnly((PdoMappingEntry[])mapping.Clone());
        StoredSignature = stored; ComputedSignature = computed; SignatureComparison = signature; EligibleForMonitoring = eligible;
    }
    /// <summary>SRDO number, 1..64.</summary>
    public int Number { get; }
    /// <summary>Structurally interpreted communication values from live/explicit SRVT-rule evidence; findings determine usability.</summary>
    public SrdoCommunicationParameter? Parameter { get; }
    /// <summary>Validated plain mapping entries; empty for an invalid or deleted record.</summary>
    public IReadOnlyList<PdoMappingEntry> Mapping { get; }
    /// <summary>Live 13FFh:n, when available with its required width.</summary>
    public ushort? StoredSignature { get; }
    /// <summary>Independently computed signature over live fields (and explicit CiA 319 SRVT rule only).</summary>
    public ushort? ComputedSignature { get; }
    /// <summary>Stored versus recomputed signature; separate from 13FEh and expected configuration.</summary>
    public SafetyComparison SignatureComparison { get; }
    /// <summary>Usable mapping/timing, matching signature, live valid flag and successful unchanged rechecks.
    /// Consistency is still unconfirmed; this is diagnostic input eligibility, never safety approval.</summary>
    public bool EligibleForMonitoring { get; }
}

/// <summary>One immutable, read-only acquisition of a peer's safety communication configuration (#403).</summary>
/// <remarks>Each acquisition has a new identity. Replacing a running monitor's snapshot must start
/// a new evaluation period. No snapshot is an atomic device image or a safety approval. #289's
/// unresolved byte-order/version questions also apply to the signature comparisons here.</remarks>
public sealed class PeerSafetySnapshot
{
    internal PeerSafetySnapshot(byte peer, DateTimeOffset started, TimeSpan duration,
        IEnumerable<PeerSafetyEntry> entries, IEnumerable<PeerSafetyEntry> rechecks,
        IEnumerable<PeerSafetySrdoSnapshot> srdos, IEnumerable<PeerSafetyFinding> findings,
        bool? valid, bool complete, SafetyComparison expected, PeerSafetySnapshotConsistency consistency)
    {
        ConfigurationId = Guid.NewGuid(); PeerNodeId = peer; StartedAtUtc = started; Duration = duration;
        Entries = Array.AsReadOnly(entries.ToArray()); RecheckEntries = Array.AsReadOnly(rechecks.ToArray());
        Srdos = Array.AsReadOnly(srdos.ToArray()); Findings = Array.AsReadOnly(findings.ToArray());
        DeviceConfigurationValid = valid; IsComplete = complete; ExpectedComparison = expected; Consistency = consistency;
    }
    /// <summary>This acquisition's identity; not a device-provided version.</summary>
    public Guid ConfigurationId { get; }
    /// <summary>Peer node-id.</summary>
    public byte PeerNodeId { get; }
    /// <summary>UTC wall time at acquisition start, after obtaining the client's peer-SDO channel.</summary>
    public DateTimeOffset StartedAtUtc { get; }
    /// <summary>Monotonic elapsed acquisition duration.</summary>
    public TimeSpan Duration { get; }
    /// <summary>First uploads, including 1018h identity entries where readable; every raw value has provenance.</summary>
    public IReadOnlyList<PeerSafetyEntry> Entries { get; }
    /// <summary>Second uploads of the same bounded entry set, retained for observed-change diagnostics.</summary>
    public IReadOnlyList<PeerSafetyEntry> RecheckEntries { get; }
    /// <summary>Interpreted records, including deleted/unusable ones.</summary>
    public IReadOnlyList<PeerSafetySrdoSnapshot> Srdos { get; }
    /// <summary>Per-entry findings, including unavailable values and interoperability limitations.</summary>
    public IReadOnlyList<PeerSafetyFinding> Findings { get; }
    /// <summary>Live 13FEh: A5h is true, 00h false, unavailable or another value null.</summary>
    public bool? DeviceConfigurationValid { get; }
    /// <summary>The bounded safety image and rechecks were read from live/explicit SRVT-rule evidence.
    /// Does not imply valid parameters, atomic consistency, or safety approval.</summary>
    public bool IsComplete { get; }
    /// <summary>Optional expected configuration comparison, independent of signature and device flag. Match requires
    /// a complete acquisition without observed changes; a known difference still reports Mismatch in a partial image.</summary>
    public SafetyComparison ExpectedComparison { get; }
    /// <summary>Observed change or unconfirmed consistency; never atomic consistency.</summary>
    public PeerSafetySnapshotConsistency Consistency { get; }
}
