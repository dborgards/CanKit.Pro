using System.Diagnostics;

namespace CanKit.Pro.IsoTp;

/// <summary>
/// A reassembled inbound PDU together with the monotonic instant it was queued for delivery.
/// </summary>
/// <remarks>
/// <para>
/// The timestamp exists so a caller enforcing a response deadline can ask <i>when the PDU
/// arrived</i> rather than <i>when it got round to looking</i>. Those differ by however long the
/// caller was descheduled, and on a loaded host that is not a small quantity — which is exactly
/// the case a deadline has to survive.
/// </para>
/// <para>
/// <see cref="ArrivalTimestamp"/> is a raw <see cref="Stopwatch.GetTimestamp"/> reading in units
/// of <see cref="Stopwatch.Frequency"/> per second, not a wall-clock time: an elapsed quantity
/// stays an elapsed quantity, and no NTP step or DST change can move it. Compare it against
/// another reading from the same source and divide by <see cref="Stopwatch.Frequency"/>; the
/// absolute value is meaningless on its own.
/// </para>
/// </remarks>
public readonly struct IsoTpReceivedPdu
{
    /// <summary>Creates a delivery record.</summary>
    /// <param name="pdu">The reassembled PDU.</param>
    /// <param name="arrivalTimestamp">A <see cref="Stopwatch.GetTimestamp"/> reading taken when
    /// the PDU was queued.</param>
    public IsoTpReceivedPdu(byte[] pdu, long arrivalTimestamp)
        : this(pdu, arrivalTimestamp, arrivalTimestamp)
    {
    }

    /// <summary>Creates a delivery record of a multi-frame PDU.</summary>
    /// <param name="pdu">The reassembled PDU.</param>
    /// <param name="arrivalTimestamp">A <see cref="Stopwatch.GetTimestamp"/> reading taken when
    /// the PDU's final frame arrived.</param>
    /// <param name="firstFrameArrivalTimestamp">The same reading for its first frame.</param>
    public IsoTpReceivedPdu(byte[] pdu, long arrivalTimestamp, long firstFrameArrivalTimestamp)
    {
        Pdu = pdu;
        ArrivalTimestamp = arrivalTimestamp;
        FirstFrameArrivalTimestamp = firstFrameArrivalTimestamp;
    }

    /// <summary>The reassembled PDU.</summary>
    public byte[] Pdu { get; }

    /// <summary>
    /// Monotonic <see cref="Stopwatch.GetTimestamp"/> reading taken when the PDU's <b>final
    /// frame</b> was dequeued from the bus subscription — ahead of the protocol actor and of
    /// reassembly, so neither contributes to it. It is therefore an arrival time rather than an
    /// observation time, up to one channel hop between the demux and the channel's reader.
    /// </summary>
    public long ArrivalTimestamp { get; }

    /// <summary>
    /// The same reading for the PDU's <b>first</b> frame — the Single Frame itself, or the First
    /// Frame of a multi-frame PDU. This is the instant a response <em>began</em>, which is what
    /// ISO 14229-2 measures P2 against: P2 ends with the first frame of the response, the rest
    /// of the transfer is the transport's (N_Cr) business (#28). Equal to
    /// <see cref="ArrivalTimestamp"/> for a Single Frame.
    /// </summary>
    public long FirstFrameArrivalTimestamp { get; }
}
