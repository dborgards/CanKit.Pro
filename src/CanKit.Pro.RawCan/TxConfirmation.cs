using System;

namespace CanKit.Pro.RawCan
{
    /// <summary>
    /// Why a <see cref="TxConfirmation"/> did not confirm the send (only meaningful when
    /// <see cref="TxConfirmation.Confirmed"/> is false).
    /// </summary>
    public enum TxConfirmFailureReason
    {
        /// <summary>Not a failure: <see cref="TxConfirmation.Confirmed"/> is true.</summary>
        None = 0,

        /// <summary>No echo arrived within the configured timeout (FR-RAW-033).</summary>
        Timeout,

        /// <summary>
        /// The bus transitioned to <see cref="CanKit.Abstractions.API.Common.Definitions.BusState.BusOff"/>
        /// while the confirmation was outstanding (FR-RAW-033).
        /// </summary>
        BusOff,

        /// <summary>
        /// The driver did not accept the frame at all (<c>ICanBus.Transmit</c>/<c>TransmitAsync</c>
        /// returned 0) (FR-RAW-033).
        /// </summary>
        Rejected,
    }

    /// <summary>
    /// Result of <see cref="ICanBusService.SendConfirmed"/>: a uniform "was this frame actually
    /// sent" answer regardless of whether the underlying adapter supports hardware TX echo
    /// (arc42 §5.3/§6.3, ADR-7; FR-RAW-030..034).
    /// </summary>
    /// <remarks>
    /// A <see cref="TxConfirmation"/> value is only ever produced for a *resolved* outcome — the
    /// returned <c>Task&lt;TxConfirmation&gt;</c> never completes successfully while the send is
    /// still pending. <see cref="Confirmed"/> is true only for an actual driver-accepted send
    /// (approximated) or an actually-matched echo frame (real); for every other outcome
    /// (timeout, bus-off, outright rejection) <see cref="Confirmed"/> is false and
    /// <see cref="FailureReason"/> explains why (FR-RAW-033). Explicit caller-supplied
    /// <see cref="System.Threading.CancellationToken"/> cancellation is reported as task
    /// cancellation (standard .NET convention), not as a <see cref="TxConfirmation"/> value.
    /// </remarks>
    public readonly record struct TxConfirmation
    {
        /// <summary>
        /// True if the send was confirmed — either by a matched hardware echo, or (when the
        /// adapter has no echo capability enabled) by the driver accepting the frame. See
        /// <see cref="IsApproximated"/> to tell the two apart.
        /// </summary>
        public bool Confirmed { get; init; }

        /// <summary>
        /// True when <see cref="Confirmed"/> reflects driver acceptance rather than an actual
        /// hardware echo (FR-RAW-032) — i.e. "best-effort acknowledgment", not a guarantee the
        /// frame reached the wire. Always false when <see cref="Confirmed"/> is false.
        /// </summary>
        public bool IsApproximated { get; init; }

        /// <summary>
        /// UTC timestamp of when this result was produced (confirmation or failure).
        /// </summary>
        public DateTime Timestamp { get; init; }

        /// <summary>
        /// Why the send was not confirmed; <see cref="TxConfirmFailureReason.None"/> when
        /// <see cref="Confirmed"/> is true.
        /// </summary>
        public TxConfirmFailureReason FailureReason { get; init; }

        /// <summary>
        /// <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> reading taken immediately
        /// after the driver accepted the frame, or zero when nothing was handed to the driver
        /// (an outright rejection). Monotonic, and comparable with the
        /// <c>HostArrivalTimestamp</c> the demux puts on received frames.
        /// </summary>
        /// <remarks>
        /// <see cref="Timestamp"/> answers "when was this result produced" and is a wall-clock
        /// reading; this answers "when did the frame go out", which is a different question and
        /// needs a different clock. A caller whose response deadline starts at transmission has
        /// no other way to learn that instant: by the time the returned task completes it is
        /// behind the echo wait, and reading the clock then starts the deadline late enough to
        /// let a late response pass for a punctual one (#92).
        /// </remarks>
        public long HostTransmitTimestamp { get; init; }

        /// <summary>
        /// The host-monotonic <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> reading
        /// taken immediately <em>before</em> the driver call, inside the service's send lock, or
        /// zero when nothing was handed to the driver. Comparable with
        /// <see cref="HostTransmitTimestamp"/> and with the demux's <c>HostArrivalTimestamp</c>.
        /// </summary>
        /// <remarks>
        /// The other end of the driver call. <see cref="HostTransmitTimestamp"/> is where a
        /// response deadline starts, and is taken after the call -- which on an in-process bus
        /// includes the peer's answer, and on an asynchronous adapter a completion callback -- so
        /// a fast peer's response can be stamped by the demux before it. A caller deciding
        /// whether a received frame can be a response to this transmission at all needs an
        /// instant no later than the frame's wire instant: this one. Taken inside the lock, so
        /// no wait for another sender's driver call sits between it and the handoff (Codex on
        /// #147).
        /// </remarks>
        public long HostHandoffTimestamp { get; init; }
    }
}
