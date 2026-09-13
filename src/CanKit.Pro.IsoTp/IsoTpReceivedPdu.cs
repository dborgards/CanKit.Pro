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
    {
        Pdu = pdu;
        ArrivalTimestamp = arrivalTimestamp;
    }

    /// <summary>The reassembled PDU.</summary>
    public byte[] Pdu { get; }

    /// <summary>
    /// Monotonic <see cref="Stopwatch.GetTimestamp"/> reading taken when the PDU was queued for
    /// delivery — that is, when it arrived, not when it was observed.
    /// </summary>
    public long ArrivalTimestamp { get; }
}
