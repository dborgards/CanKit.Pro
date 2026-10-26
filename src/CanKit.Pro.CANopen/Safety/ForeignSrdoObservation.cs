using System;
using System.Collections.Generic;

namespace CanKit.Pro.CANopen.Safety;

/// <summary>What the monitor observed, rather than a remote device's safety state.</summary>
public enum ForeignSrdoStatus
{
    /// <summary>No complete pair in this evaluation period.</summary>
    NotReceived,
    /// <summary>A complete, usable inverse pair within the observed timing limits.</summary>
    Valid,
    /// <summary>A normal frame is pending; also reports replacement by another normal frame.</summary>
    WaitingForInverted,
    /// <summary>A lone inverted frame at startup was ignored, matching local consumer semantics.</summary>
    StartupInverted,
    /// <summary>A lone inverted frame after startup or a fault.</summary>
    OutOfOrder,
    /// <summary>Payload lengths or bitwise inversion differ.</summary>
    Mismatch,
    /// <summary>A pair is shorter than the mapping.</summary>
    ShortPayload,
    /// <summary>Extended, RTR, FD or oversized SRDO candidate.</summary>
    InvalidFormat,
    /// <summary>No inverted frame was observed before SRVT elapsed.</summary>
    ValidationTimeExpired,
    /// <summary>No usable pair was observed before SCT elapsed.</summary>
    SafeguardCycleExpired,
    /// <summary>Known input discontinuity; pending and current data were discarded.</summary>
    CaptureGap,
    /// <summary>Host capture timing is absent, late, future or non-monotonic.</summary>
    TimingUnavailable,
    /// <summary>A buffered SRDO predates the current configuration/evaluation period.</summary>
    BeforeEvaluationPeriod,
    /// <summary>One captured valid GFC, without node attribution.</summary>
    GfcObserved,
    /// <summary>One malformed candidate on identifier 001h.</summary>
    MalformedGfc,
}

/// <summary>Capture evidence, independent of pair status. No value proves upstream completeness.</summary>
public enum ForeignSrdoCaptureQuality
{
    /// <summary>The input has no ordered loss metadata.</summary>
    Unknown,
    /// <summary>No subscription-local discontinuity has been observed in this window.</summary>
    Contiguous,
    /// <summary>A sequence gap or reported drop has occurred.</summary>
    Incomplete,
}

/// <summary>Owned immutable bytes of one decoded object, in CANopen little-endian order.</summary>
public sealed class ForeignSrdoSignal
{
    internal ForeignSrdoSignal(ushort index, byte subindex, byte[] value)
    { Index = index; Subindex = subindex; Value = Array.AsReadOnly(value); }
    /// <summary>Mapped object index.</summary>
    public ushort Index { get; }
    /// <summary>Mapped object sub-index.</summary>
    public byte Subindex { get; }
    /// <summary>Owned read-only bytes.</summary>
    public IReadOnlyList<byte> Value { get; }
}

/// <summary>Immutable trace item. Every captured GFC occurrence gets its own item.</summary>
public sealed class ForeignSrdoObservation
{
    internal ForeignSrdoObservation(string bus, ForeignSrdoStatus status, long timestamp,
        TimeSpan? receiveTimestamp, ulong? sequence, ForeignSrdoConfiguration? configuration,
        Guid period, ForeignSrdoCaptureQuality quality, bool timingAvailable,
        IReadOnlyList<ForeignSrdoSignal> signals)
    {
        BusIdentity = bus; Status = status; HostTimestamp = timestamp; ReceiveTimestamp = receiveTimestamp;
        SequenceNumber = sequence; Configuration = configuration; EvaluationPeriodId = period;
        CaptureQuality = quality; TimingAvailable = timingAvailable; Signals = signals;
    }
    /// <summary>Caller-supplied identity of this physical capture bus.</summary>
    public string BusIdentity { get; }
    /// <summary>Observation category.</summary>
    public ForeignSrdoStatus Status { get; }
    /// <summary>Capture time, or logical expiry time, in Stopwatch ticks. Zero denotes missing time.</summary>
    public long HostTimestamp { get; }
    /// <summary>Original adapter timestamp for frame events, without cross-bus arithmetic.</summary>
    public TimeSpan? ReceiveTimestamp { get; }
    /// <summary>Subscription-local sequence, when available and frame-associated.</summary>
    public ulong? SequenceNumber { get; }
    /// <summary>Configuration association; null for GFCs and capture-wide findings. Never wire sender authentication.</summary>
    public ForeignSrdoConfiguration? Configuration { get; }
    /// <summary>Evaluation window; replacement or discontinuity starts a new window.</summary>
    public Guid EvaluationPeriodId { get; }
    /// <summary>Input quality for this observation's window.</summary>
    public ForeignSrdoCaptureQuality CaptureQuality { get; }
    /// <summary>Whether the host timestamp can be used for this assessment.</summary>
    public bool TimingAvailable { get; }
    /// <summary>Decoded values only on a valid-pair item; faults never repeat old values.</summary>
    public IReadOnlyList<ForeignSrdoSignal> Signals { get; }
}

/// <summary>State as of one poll. A retained instance is historical, not a live clock.</summary>
public sealed class ForeignSrdoState
{
    internal ForeignSrdoState(ForeignSrdoConfiguration configuration, Guid period, ForeignSrdoStatus status,
        ForeignSrdoCaptureQuality quality, long asOf, long? lastValid, TimeSpan? age,
        IReadOnlyList<ForeignSrdoSignal> signals, bool caughtUp)
    {
        Configuration = configuration; EvaluationPeriodId = period; Status = status; CaptureQuality = quality;
        AsOfTimestamp = asOf; LastValidTimestamp = lastValid; Age = age; Signals = signals;
        IsCurrent = caughtUp && status == ForeignSrdoStatus.Valid;
    }
    /// <summary>Immutable configuration and provenance.</summary>
    public ForeignSrdoConfiguration Configuration { get; }
    /// <summary>Current evaluation window.</summary>
    public Guid EvaluationPeriodId { get; }
    /// <summary>Latest pair/timing state.</summary>
    public ForeignSrdoStatus Status { get; }
    /// <summary>Quality since this evaluation window began; recovery requires a fresh complete pair.</summary>
    public ForeignSrdoCaptureQuality CaptureQuality { get; }
    /// <summary>Monotonic timestamp through which this state has been evaluated.</summary>
    public long AsOfTimestamp { get; }
    /// <summary>Last usable pair, retained as historical metadata across faults, but cleared on replacement.</summary>
    public long? LastValidTimestamp { get; }
    /// <summary>Age of that last pair at AsOfTimestamp, even when no longer current.</summary>
    public TimeSpan? Age { get; }
    /// <summary>True only for a valid state after the input has been drained.</summary>
    public bool IsCurrent { get; }
    /// <summary>Current values only; empty after a fault, while waiting, or while catching up.</summary>
    public IReadOnlyList<ForeignSrdoSignal> Signals { get; }
}

/// <summary>Bounded poll result. Items are returned directly without a lossy callback/event buffer.</summary>
public sealed class ForeignSrdoPollResult
{
    internal ForeignSrdoPollResult(ForeignSrdoObservation[] observations, ForeignSrdoState[] states,
        bool backlog, ForeignSrdoCaptureQuality quality, long? drops)
    { Observations = Array.AsReadOnly(observations); States = Array.AsReadOnly(states); HasBacklog = backlog; CaptureQuality = quality; DroppedFrameCount = drops; }
    /// <summary>Trace entries in input/deadline order; GFCs are never coalesced.</summary>
    public IReadOnlyList<ForeignSrdoObservation> Observations { get; }
    /// <summary>One state per prepared SRDO.</summary>
    public IReadOnlyList<ForeignSrdoState> States { get; }
    /// <summary>The batch limit was reached. Poll again before claiming current values or wall-clock expiry.</summary>
    public bool HasBacklog { get; }
    /// <summary>Sticky session trace quality; a known gap cannot be erased by later traffic.</summary>
    public ForeignSrdoCaptureQuality CaptureQuality { get; }
    /// <summary>Durable subscription drop count, or null for a legacy input.</summary>
    public long? DroppedFrameCount { get; }
}
