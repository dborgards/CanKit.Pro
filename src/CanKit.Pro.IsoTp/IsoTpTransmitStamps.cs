using System;

namespace CanKit.Pro.IsoTp;

/// <summary>
/// The two host-monotonic instants (<see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>)
/// that bracket the driver call for a PDU's last frame, for a caller whose response deadline is
/// measured against the bus rather than against its own scheduling. Zero means not reported.
/// </summary>
/// <remarks>
/// <see cref="LastFrameTransmitTimestamp"/> is where a response deadline starts: "no later than
/// the driver accepted the last frame" (#112). <see cref="LastFrameHandoffTimestamp"/> is taken
/// just before that call and bounds what can be a response at all: a peer answers only a
/// complete request, so nothing that reached the wire before its last frame was handed over can
/// answer this PDU. The two are not interchangeable -- the acceptance instant is taken after a
/// synchronous delivery or a completion callback, and a fast peer's answer can be stamped by the
/// demux before it (#146), so a stale-response cutoff keyed on it rejects genuine responses; a
/// cutoff taken by the caller before it enters the channel, or at the first frame's handoff,
/// leaves the channel's own transmission as a window in which an earlier request's late response
/// passes (Codex on #147).
/// </remarks>
public readonly struct IsoTpTransmitStamps : IEquatable<IsoTpTransmitStamps>
{
    /// <summary>Creates the pair; the channel is the intended caller.</summary>
    public IsoTpTransmitStamps(long lastFrameHandoffTimestamp, long lastFrameTransmitTimestamp)
    {
        LastFrameHandoffTimestamp = lastFrameHandoffTimestamp;
        LastFrameTransmitTimestamp = lastFrameTransmitTimestamp;
    }

    /// <summary>Just before the PDU's last frame was handed to the driver.</summary>
    public long LastFrameHandoffTimestamp { get; }

    /// <summary>No later than the driver accepted the PDU's last frame.</summary>
    public long LastFrameTransmitTimestamp { get; }

    /// <inheritdoc />
    public bool Equals(IsoTpTransmitStamps other)
        => LastFrameHandoffTimestamp == other.LastFrameHandoffTimestamp
           && LastFrameTransmitTimestamp == other.LastFrameTransmitTimestamp;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is IsoTpTransmitStamps other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
        => unchecked((LastFrameHandoffTimestamp.GetHashCode() * 397) ^ LastFrameTransmitTimestamp.GetHashCode());

    /// <summary>Value equality.</summary>
    public static bool operator ==(IsoTpTransmitStamps left, IsoTpTransmitStamps right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(IsoTpTransmitStamps left, IsoTpTransmitStamps right) => !left.Equals(right);
}
