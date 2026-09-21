using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CanKit.Pro.IsoTp;

/// <summary>
/// A single ISO 15765-2 (ISO-TP) channel bound to one <see cref="IsoTpEndpoint"/>
/// (TX/RX CAN-ID pair, plus addressing mode). Threading model per SRS FR-TP-016 / FR-RAW-020..023:
/// all protocol state is owned by a single <see cref="Actor.IProtocolActor"/> mailbox, so callers
/// may invoke <see cref="SendAsync"/> concurrently from arbitrary threads and receive frames from
/// arbitrary threads; internal state is never mutated from more than one place at a time.
/// </summary>
/// <remarks>
/// <para>
/// One channel = one PDU at a time on the wire: an outgoing multi-frame PDU
/// (First-Frame + Consecutive-Frames) is completed (or aborted with an
/// <see cref="IsoTpException"/>) before the next <see cref="SendAsync"/> call is transmitted.
/// This mirrors ISO 15765-2 §6.4's "one N-USData at a time" model; overlapping sends from
/// different callers are serialized by the channel.
/// </para>
/// <para>
/// Received PDUs are delivered both as an event (<see cref="DatagramReceived"/>) and as an
/// <see cref="IAsyncEnumerable{T}"/> from <see cref="ReceiveAllAsync"/>; a single
/// <see cref="ReceiveAsync"/> call awaits the next one. Both surfaces share the same bounded
/// buffer.
/// </para>
/// <para>
/// <see cref="IDisposable.Dispose"/> is thread-safe and idempotent (FR-RAW-021).
/// </para>
/// </remarks>
public interface IIsoTpChannel : IDisposable
{
    /// <summary>The endpoint this channel is bound to (TX/RX CAN-ID pair + addressing mode).</summary>
    IsoTpEndpoint Endpoint { get; }

    /// <summary>The channel options used at construction (immutable).</summary>
    IsoTpChannelOptions Options { get; }

    /// <summary>
    /// Sends <paramref name="pdu"/> as one ISO-TP N-USData PDU. The returned task completes when
    /// the last frame of the PDU is TX-confirmed by the driver (SF) or when the peer has FC-cleared
    /// the entire multi-frame PDU and the last CF is confirmed (FF+CFs). It faults with an
    /// <see cref="IsoTpException"/> on protocol timeouts (FR-TP-010), an Overflow FC (FR-TP-012),
    /// exceeding WFTmax (FR-TP-011), or a driver rejection.
    /// </summary>
    /// <param name="pdu">User data, 1..4095 bytes for classic CAN, or up to
    /// <see cref="IsoTpFrameCodec.MaxFdFirstFrameLength"/> for CAN-FD. Must not be empty.</param>
    /// <param name="cancellationToken">Cancels the returned task (standard .NET convention); a
    /// canceled send aborts the in-flight PDU. The channel keeps the one-PDU-at-a-time send gate
    /// until any already-submitted bus TX from that PDU completes, so the next call cannot
    /// interleave frames on the wire with the aborted transfer.</param>
    Task SendAsync(ReadOnlyMemory<byte> pdu, CancellationToken cancellationToken = default);

    /// <summary>
    /// As <see cref="SendAsync"/>, but returns the monotonic
    /// (<see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>) instant at which the PDU's last
    /// frame was handed to the bus. Zero when nothing was transmitted.
    /// </summary>
    /// <remarks>
    /// The send-side counterpart of <see cref="ReceiveWithArrivalAsync"/>, and needed for the
    /// same reason. A caller whose response deadline starts when its request went out cannot read
    /// that instant off its own clock: awaiting this method returns behind the bus TX
    /// confirmation, an actor hop and the caller's own scheduling, all of which happen after the
    /// peer already has the request. Timing the deadline from the returned stamp makes its start
    /// as independent of scheduling as the arrival stamp makes its end -- pinning only one of the
    /// two leaves the deadline movable from the other side.
    /// </remarks>
    Task<long> SendWithTransmitStampAsync(ReadOnlyMemory<byte> pdu,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Awaits the next fully reassembled inbound PDU. Cancels via <paramref name="cancellationToken"/>.
    /// Faults with <see cref="IsoTpTimeoutException"/> (<see cref="IsoTpTimer.NCr"/>) or
    /// <see cref="IsoTpException"/> when an in-progress multi-frame reassembly is aborted
    /// (N_Cr, CF sequence mismatch, or a superseding SF/FF — including when the new FF is
    /// refused with FC(OVFLW); FR-TP-010), so a caller waiting for that PDU does not hang
    /// indefinitely.
    /// </summary>
    Task<byte[]> ReceiveAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// As <see cref="ReceiveAsync"/>, but also reports the monotonic instant the PDU was queued.
    /// </summary>
    /// <remarks>
    /// For callers that enforce a response deadline. <see cref="ReceiveAsync"/> can only tell
    /// them when they observed the PDU, and a caller descheduled past its own deadline cannot
    /// tell a punctual response from a late one on that basis — it would either accept a
    /// response it had already given up on, or reject a timely one for arriving while it was not
    /// looking. The arrival timestamp removes the ambiguity, so the deadline holds regardless of
    /// scheduling.
    /// </remarks>
    Task<IsoTpReceivedPdu> ReceiveWithArrivalAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes the next inbound PDU if one is already queued, without waiting for one to arrive.
    /// Returns <see langword="false"/> — leaving <paramref name="pdu"/> at its default — when the
    /// inbox is empty or the channel is disposed. Throws the recorded reassembly-abort exception
    /// when the queued item is a fault, exactly as
    /// <see cref="ReceiveWithArrivalAsync"/> would.
    /// </summary>
    /// <remarks>
    /// The companion to <see cref="ReceiveWithArrivalAsync"/> for a caller whose deadline has
    /// already passed. Awaiting with an expired token is not the same thing: an already-cancelled
    /// token wins against a queued item, so the wait would report a timeout while the answer sat
    /// unread in the inbox. Separating <em>how long to wait</em> from <em>was it in time</em>
    /// leaves the second question to the arrival stamp, which is the only reading of it that does
    /// not depend on when the caller was scheduled.
    /// </remarks>
    bool TryReceiveWithArrival(out IsoTpReceivedPdu pdu);

    /// <summary>
    /// Whether a multi-frame reception is in progress — a First Frame was accepted and its last
    /// Consecutive Frame has not arrived — and if so, when that First Frame arrived, as a
    /// <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> reading. For a caller whose own
    /// deadline ends with the first frame of a response and not with its last (ISO 14229-2 P2,
    /// #28): a response that began in time is then waited for without that deadline, bounded by
    /// the transport's N_Cr instead. Answered from the channel's current state; a reception can
    /// begin or complete the moment after.
    /// </summary>
    bool TryGetReceptionInProgress(out long firstFrameArrivalTimestamp);

    /// <summary>
    /// Drains every buffered inbox item — both completed PDUs and pending reassembly-abort
    /// faults enqueued by <c>AbortRx</c> — and returns how many were dropped. Also silently
    /// aborts any in-flight multi-frame reassembly on the actor so leftover consecutive frames
    /// cannot finish and enqueue a stale PDU after a higher-layer timeout/cancel
    /// (Bugbot 3596444314). Higher layers (e.g. UDS) call this after a cancelled/timed-out wait
    /// so a late peer PDU or a leftover abort fault cannot be consumed as the answer to a later
    /// request.
    /// </summary>
    int DiscardPendingPdus();

    /// <summary>
    /// Enumerates every fully reassembled inbound PDU as it becomes available. The enumeration
    /// ends when the channel is disposed. A reassembly abort (N_Cr / CF sequence mismatch /
    /// SF·FF supersede) faults the enumerator with the same exception
    /// <see cref="ReceiveAsync"/> would throw.
    /// Cancel by passing a token via <c>WithCancellation</c> or by disposing the channel.
    /// </summary>
    IAsyncEnumerable<byte[]> ReceiveAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Raised (on a thread-pool thread) every time a full PDU is reassembled. The same PDU is
    /// enqueued for <see cref="ReceiveAsync"/>/<see cref="ReceiveAllAsync"/> before the event
    /// fires, so a handler that synchronously waits on those APIs — or on
    /// <see cref="DiscardPendingPdus"/> — cannot deadlock the protocol actor. Handlers must be
    /// non-throwing; a throwing handler is caught and surfaced via
    /// <see cref="BackgroundExceptionOccurred"/>.
    /// </summary>
    event EventHandler<IsoTpDatagramReceivedEventArgs>? DatagramReceived;

    /// <summary>
    /// Raised when a background failure (reassembly abort, event-handler exception, subscription
    /// failure, actor loop exception) needs to be surfaced to the application. This is the
    /// documented channel for out-of-band errors (FR-RAW-023). Failures tied to a specific
    /// <see cref="SendAsync"/> call are reported via that call's returned task; reassembly
    /// aborts are reported both here and by faulting a pending <see cref="ReceiveAsync"/>
    /// (FailTx analogue on the receive side).
    /// </summary>
    event EventHandler<Exception>? BackgroundExceptionOccurred;
}
