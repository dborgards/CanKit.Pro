using System;

namespace CanKit.Pro.J1939Tp;

/// <summary>
/// Configuration for a <see cref="IJ1939TpChannel"/>: J1939-21 §5.10 timing parameters, transmit
/// priority, and per-channel bounds. All values are captured at channel construction time and
/// treated as immutable for its lifetime.
/// </summary>
/// <remarks>
/// The J1939-21 §5.10.2.4 recommended values are hard-coded defaults; production callers can
/// override any subset via <see cref="With"/>. Every timer defaults to its standard value:
/// <list type="bullet">
///   <item><description><see cref="T1"/> = 750 ms — TP.DT gap timeout at the receiver.</description></item>
///   <item><description><see cref="T2"/> = 1250 ms — CTS→first TP.DT timeout at the receiver.</description></item>
///   <item><description><see cref="T3"/> = 1250 ms — RTS→CTS, block→next CTS and last DT→EndOfMsgAck timeout at the originator.</description></item>
///   <item><description><see cref="T4"/> = 1050 ms — TP.CM hold timeout at the originator after CTS(0) (§5.10.2.4).</description></item>
/// </list>
/// The standard's Tr (200 ms) is the time a node has to <em>send</em> a response it owes, not
/// a timer a peer is held to; this stack answers at once and has no option for it (#31). Its
/// Th (500 ms) is the holding time between two CTS(0) messages a responder sends; this stack
/// sends none and has no option for it either (#144). <see cref="BamPacketSpacing"/> (50 ms)
/// is the spacing between two BAM packets, §5.10.3's 50..200 ms -- not a timer of §5.10.2.4.
/// </remarks>
public sealed class J1939TpOptions
{
    /// <summary>T1 — TP.DT gap timeout at the receiver (J1939-21 §5.10.2.4). Default 750 ms.</summary>
    public TimeSpan T1 { get; init; } = TimeSpan.FromMilliseconds(750);

    /// <summary>
    /// T2 — the receiver's timeout from its CTS to the first TP.DT of the granted block
    /// (§5.10.2.4). Default 1250 ms. Before #31 this window was held to Tr (200 ms), which
    /// rejected a conforming but slow originator.
    /// </summary>
    public TimeSpan T2 { get; init; } = TimeSpan.FromMilliseconds(1250);

    /// <summary>
    /// T3 — the originator's timeout for the response it is owed: CTS after RTS, the next CTS
    /// after the last TP.DT of a block, EndOfMsgAck after the last TP.DT (§5.10.2.4). Default
    /// 1250 ms.
    /// </summary>
    public TimeSpan T3 { get; init; } = TimeSpan.FromMilliseconds(1250);

    /// <summary>
    /// T4 — originator-side hold timeout after a CTS with numPackets=0 ("hold connection open").
    /// Per J1939-21 §5.10.2.4, lack of a follow-up CTS within T4 closes the connection. Default 1050 ms.
    /// </summary>
    public TimeSpan T4 { get; init; } = TimeSpan.FromMilliseconds(1050);

    /// <summary>
    /// Minimum spacing between two consecutive BAM TP.DT frames on the wire (§5.10.3
    /// "50..200 ms"). Default 50 ms to stay at the lower recommended bound while still gating
    /// against a receiver that cannot keep up. Not the standard's Th, which is the holding time
    /// between CTS(0) messages and which this stack does not use; this option was named Th
    /// before #144.
    /// </summary>
    public TimeSpan BamPacketSpacing { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// How many times per TP.CM session this originator serves a CTS that asks for a packet it
    /// has already sent -- a retransmit request (§5.10.2.4); the next one is answered with
    /// Connection Abort reason 5, "maximum retransmit request limit reached" (table 7). The
    /// standard names the limit and leaves its value to the implementation. Default 2; 0 serves
    /// none (#58).
    /// </summary>
    public int MaxRetransmitRequests { get; init; } = 2;

    /// <summary>
    /// TX priority for TP.CM / TP.DT frames sent by this channel (0..7, 0 = highest). J1939-21
    /// uses 7 by default for its transport traffic.
    /// </summary>
    public byte Priority { get; init; } = 7;

    /// <summary>
    /// Maximum number of TP.DT packets per CTS this channel advertises when it is the receiver
    /// of a TP.CM session. Must be in [1, 255] (0 is illegal: a CTS with numPackets=0 is the
    /// "hold connection open" form, not a real grant). Defaults to 16, well below the 255 hard
    /// cap so a slow consumer can keep the block short.
    /// </summary>
    public byte MaxPacketsPerCts { get; init; } = 16;

    /// <summary>
    /// Bounded capacity of the internal receive buffer that holds fully reassembled PDUs waiting
    /// for the consumer. Drops the oldest PDU when full so the RX pipeline never stalls the actor.
    /// Defaults to 32.
    /// </summary>
    public int ReceiveBufferCapacity { get; init; } = 32;

    /// <summary>
    /// How many sends may wait for their destination's session slot, per destination address,
    /// behind the one on the wire; a send the channel has accepted but not yet started counts as
    /// waiting, so the bound holds against a producer that outruns the channel. A send that would
    /// exceed it faults at the call, before its PDU is queued anywhere, with
    /// <see cref="J1939TpSendRejectedException"/>; every BAM goes to the global address, so this
    /// also bounds the BAMs waiting on the channel. Each waiting send holds its whole PDU (up to
    /// 1785 bytes) and, against a peer that never answers, drains at T3 pace, so an unbounded
    /// queue grows in memory and latency with the producer (#204). Default 8; 0 admits no
    /// waiting send at all. There is no overall queue deadline: the caller's
    /// <see cref="System.Threading.CancellationToken"/> bounds the wait, and a cancelled send
    /// leaves the queue at once.
    /// </summary>
    public int MaxQueuedSendsPerDestination { get; init; } = 8;

    /// <summary>
    /// Convenience clone that returns a new instance with the provided overrides. Useful for
    /// tests that want to tweak one field of a shared default template.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="maxPacketsPerCts"/> is 0.
    /// </exception>
    public J1939TpOptions With(
        TimeSpan? t1 = null,
        TimeSpan? t2 = null,
        TimeSpan? t3 = null,
        TimeSpan? t4 = null,
        TimeSpan? bamPacketSpacing = null,
        byte? priority = null,
        byte? maxPacketsPerCts = null,
        int? receiveBufferCapacity = null,
        int? maxRetransmitRequests = null,
        int? maxQueuedSendsPerDestination = null)
    {
        if (maxPacketsPerCts is 0)
            throw new ArgumentOutOfRangeException(nameof(maxPacketsPerCts), maxPacketsPerCts,
                "MaxPacketsPerCts must be in [1, 255]; 0 is not a valid CTS grant size.");
        if (maxRetransmitRequests < 0)
            throw new ArgumentOutOfRangeException(nameof(maxRetransmitRequests), maxRetransmitRequests,
                "MaxRetransmitRequests must be >= 0 (0 serves none).");

        if (maxQueuedSendsPerDestination < 0)
            throw new ArgumentOutOfRangeException(nameof(maxQueuedSendsPerDestination), maxQueuedSendsPerDestination,
                "MaxQueuedSendsPerDestination must be >= 0 (0 admits no waiting send).");

        return new()
        {
            T1 = t1 ?? T1,
            T2 = t2 ?? T2,
            T3 = t3 ?? T3,
            T4 = t4 ?? T4,
            BamPacketSpacing = bamPacketSpacing ?? BamPacketSpacing,
            Priority = priority ?? Priority,
            MaxPacketsPerCts = maxPacketsPerCts ?? MaxPacketsPerCts,
            ReceiveBufferCapacity = receiveBufferCapacity ?? ReceiveBufferCapacity,
            MaxRetransmitRequests = maxRetransmitRequests ?? MaxRetransmitRequests,
            MaxQueuedSendsPerDestination = maxQueuedSendsPerDestination ?? MaxQueuedSendsPerDestination,
        };
    }

    /// <summary>
    /// Validates invariants that must hold for a channel to open safely (called from
    /// <see cref="J1939TpChannel"/> construction).
    /// </summary>
    internal void Validate()
    {
        // A negative timer makes Arm throw after the session is registered and the RTS is on the
        // wire, which leaves a send with no deadline and its destination blocked; reject it here.
        RequirePositive(nameof(T1), T1);
        RequirePositive(nameof(T2), T2);
        RequirePositive(nameof(T3), T3);
        RequirePositive(nameof(T4), T4);
        if (BamPacketSpacing < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(BamPacketSpacing), BamPacketSpacing,
                "BamPacketSpacing must not be negative.");
        if (MaxPacketsPerCts == 0)
            throw new ArgumentOutOfRangeException(nameof(MaxPacketsPerCts), MaxPacketsPerCts,
                "MaxPacketsPerCts must be in [1, 255]; 0 is not a valid CTS grant size.");
        if (Priority > 7)
            throw new ArgumentOutOfRangeException(nameof(Priority), Priority,
                "J1939 priority must be in [0, 7].");
        if (ReceiveBufferCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(ReceiveBufferCapacity), ReceiveBufferCapacity,
                "ReceiveBufferCapacity must be >= 1.");
        if (MaxRetransmitRequests < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxRetransmitRequests), MaxRetransmitRequests,
                "MaxRetransmitRequests must be >= 0 (0 serves none).");
        if (MaxQueuedSendsPerDestination < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxQueuedSendsPerDestination), MaxQueuedSendsPerDestination,
                "MaxQueuedSendsPerDestination must be >= 0 (0 admits no waiting send).");
    }

    private static void RequirePositive(string name, TimeSpan value)
    {
        if (value <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(name, value, name + " must be greater than zero.");
    }
}
