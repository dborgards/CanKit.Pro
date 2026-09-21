using System;

namespace CanKit.Pro.IsoTp;

/// <summary>
/// A multi-frame reception that has begun and not completed: the First Frame's arrival, the
/// length it announced, and the data bytes it carried (see
/// <see cref="IIsoTpChannel.TryGetReceptionInProgress"/>).
/// </summary>
/// <remarks>
/// Published when the First Frame is read off the bus, before the channel's protocol actor
/// has accepted it — so a caller whose deadline fires while the actor is still behind sees the
/// frame at its arrival, not at its processing (Codex on #143). The actor withdraws the record
/// if it refuses the frame (a short First Frame, or one announcing more than the channel
/// accepts), so the record is not a promise that the PDU will be delivered; a caller waiting
/// on it re-checks rather than waiting unboundedly.
/// </remarks>
public sealed class IsoTpReceptionInProgress
{
    /// <summary>Creates a record; the channel is the only intended caller.</summary>
    public IsoTpReceptionInProgress(long firstFrameArrivalTimestamp, int announcedLength,
        ReadOnlyMemory<byte> firstFrameData)
    {
        FirstFrameArrivalTimestamp = firstFrameArrivalTimestamp;
        AnnouncedLength = announcedLength;
        FirstFrameData = firstFrameData;
    }

    /// <summary>
    /// When the First Frame arrived, as a <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>
    /// reading on the same clock as <see cref="IsoTpReceivedPdu.ArrivalTimestamp"/>.
    /// </summary>
    public long FirstFrameArrivalTimestamp { get; }

    /// <summary>The PDU length the First Frame announced.</summary>
    public int AnnouncedLength { get; }

    /// <summary>
    /// The PDU's leading bytes as the First Frame carried them — enough for an application
    /// layer to tell whose response this is (a UDS response SID is the first byte).
    /// </summary>
    public ReadOnlyMemory<byte> FirstFrameData { get; }
}
