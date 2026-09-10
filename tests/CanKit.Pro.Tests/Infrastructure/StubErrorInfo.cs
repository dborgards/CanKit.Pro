using System;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;

namespace CanKit.Pro.Tests.Infrastructure;

/// <summary>
/// The payload <see cref="ControllableBus.RaiseErrorFrame"/> hands to
/// <see cref="ICanBus.ErrorFrameReceived"/> when a test does not care what the error frame said.
///
/// <para>
/// It exists because CanKit's own <c>ICanErrorInfo</c> implementation is internal to
/// <c>CanKit.Core</c> and there is nothing public to construct, and because passing <c>null</c>
/// into an event whose contract says it carries error info would quietly excuse a subscriber that
/// dereferences it. Every member reports its default: the only CanKit.Pro consumer of this event,
/// <c>BusStateMonitor</c>, treats an error frame purely as a "the controller may have moved" hint
/// and reads <c>ICanBus.BusState</c> for the answer instead of trusting the payload. A test that
/// does care what the frame said should construct its own with the properties it cares about set,
/// rather than extend this default.
/// </para>
/// </summary>
public sealed class StubErrorInfo : ICanErrorInfo
{
    /// <summary>A shared all-defaults instance; every member is init-only, so it cannot be mutated.</summary>
    public static readonly StubErrorInfo Instance = new();

    public FrameErrorType Type { get; init; }

    public CanControllerStatus ControllerStatus { get; init; }

    public CanProtocolViolationType ProtocolViolation { get; init; }

    public FrameErrorLocation ProtocolViolationLocation { get; init; }

    public CanTransceiverStatus TransceiverStatus { get; init; }

    public DateTime SystemTimestamp { get; init; }

    public uint RawErrorCode { get; init; }

    public TimeSpan? DeviceTimeSpan { get; init; }

    public FrameDirection Direction { get; init; }

    public byte? ArbitrationLostBit { get; init; }

    public CanErrorCounters? ErrorCounters { get; init; }

    public CanFrame? Frame { get; init; }
}
