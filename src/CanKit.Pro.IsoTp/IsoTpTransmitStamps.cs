using System;

namespace CanKit.Pro.IsoTp;

/// <summary>
/// The two host-monotonic instants (<see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>) a
/// completed send reports, for a caller whose response deadline is measured against the bus
/// rather than against its own scheduling. Zero means not reported.
/// </summary>
/// <remarks>
/// <see cref="LastFrameTransmitTimestamp"/> is where a response deadline starts: "no later than
/// the driver accepted the last frame" (#112). <see cref="FirstFrameHandoffTimestamp"/> is taken
/// just before the first frame is handed to the driver, and bounds what can be a response at
/// all: nothing that reached the wire before it can answer this PDU. The two are not
/// interchangeable -- the acceptance instant is taken after a synchronous delivery or a
/// completion callback, and a fast peer's answer can be stamped by the demux before it
/// (#146), so a stale-response cutoff keyed on it rejects genuine responses; a cutoff taken by
/// the caller before it enters the channel leaves the channel's own gate and scheduling as a
/// window in which an earlier request's late response passes (Codex on #147).
/// </remarks>
public readonly struct IsoTpTransmitStamps : IEquatable<IsoTpTransmitStamps>
{
    /// <summary>Creates the pair; the channel is the intended caller.</summary>
    public IsoTpTransmitStamps(long firstFrameHandoffTimestamp, long lastFrameTransmitTimestamp)
    {
        FirstFrameHandoffTimestamp = firstFrameHandoffTimestamp;
        LastFrameTransmitTimestamp = lastFrameTransmitTimestamp;
    }

    /// <summary>Just before the PDU's first frame was handed to the driver.</summary>
    public long FirstFrameHandoffTimestamp { get; }

    /// <summary>No later than the driver accepted the PDU's last frame.</summary>
    public long LastFrameTransmitTimestamp { get; }

    /// <inheritdoc />
    public bool Equals(IsoTpTransmitStamps other)
        => FirstFrameHandoffTimestamp == other.FirstFrameHandoffTimestamp
           && LastFrameTransmitTimestamp == other.LastFrameTransmitTimestamp;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is IsoTpTransmitStamps other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
        => unchecked((FirstFrameHandoffTimestamp.GetHashCode() * 397) ^ LastFrameTransmitTimestamp.GetHashCode());

    /// <summary>Value equality.</summary>
    public static bool operator ==(IsoTpTransmitStamps left, IsoTpTransmitStamps right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(IsoTpTransmitStamps left, IsoTpTransmitStamps right) => !left.Equals(right);
}
