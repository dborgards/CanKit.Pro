using System;

namespace CanKit.Pro.Reliability
{
    /// <summary>
    /// A single armed deadline (SRS FR-RAW-050). A deadline starts <c>Pending</c> and resolves
    /// exactly once into one of three terminal outcomes: it <b>expires</b> (its <c>onExpired</c>
    /// callback fired), it is <b>completed</b> (the awaited transition finished in time), or it is
    /// <b>cancelled</b> (disposed).
    /// </summary>
    /// <remarks>
    /// The three terminal outcomes are mutually exclusive under normal operation: whichever of the
    /// expiry callback, <see cref="Complete"/>, or <see cref="IDisposable.Dispose"/> reaches the internal state
    /// field first "wins" the transition out of <c>Pending</c>, and the others become no-ops. This
    /// lets a caller (e.g. a UDS client tracking a P2 window) ask "did I complete before the
    /// deadline fired?" via <see cref="Complete"/>'s return value.
    /// <para>
    /// <b>One case resolves into none of the three</b>: if the owning actor is disposed while the
    /// deadline is still <c>Pending</c>, the actor discards its not-yet-due callbacks, so the
    /// expiry can never fire and all three flags stay false forever — indistinguishable from a
    /// healthy pending deadline. Reporting that state would take a fourth flag (or an event) on
    /// this interface, which existing implementers could not absorb without a break, so it is
    /// documented rather than signalled: tie deadline lifetime to actor lifetime, i.e. resolve
    /// outstanding deadlines with <see cref="Complete"/>/<see cref="IDisposable.Dispose"/> before
    /// disposing the actor they were armed on. <see cref="Rearm"/> is the one operation that does
    /// detect it, because it has to talk to the actor: it throws
    /// <see cref="ObjectDisposedException"/> and forces the deadline to <c>Cancelled</c>.
    /// </para>
    /// </remarks>
    public interface IDeadline : IDisposable
    {
        /// <summary>
        /// True once the deadline's timeout elapsed and its <c>onExpired</c> callback won the race
        /// to fire.
        /// </summary>
        bool IsExpired { get; }

        /// <summary>
        /// True once <see cref="Complete"/> won the race, i.e. the awaited transition finished
        /// before the timeout.
        /// </summary>
        bool IsCompleted { get; }

        /// <summary>
        /// True once the deadline was cancelled via <see cref="IDisposable.Dispose"/> before it
        /// expired or completed.
        /// </summary>
        bool IsCancelled { get; }

        /// <summary>
        /// Extends (or shortens) a still-<c>Pending</c> deadline to a new timeout measured from now,
        /// e.g. an ISO-TP receiver refreshing N_Cr on each consecutive frame.
        /// </summary>
        /// <param name="timeout">New time until expiry, measured from now. Must be &gt;= <see cref="TimeSpan.Zero"/>.</param>
        /// <returns>
        /// True if the deadline was still <c>Pending</c> and has been re-armed; false if it had
        /// already expired, completed, or been cancelled (in which case nothing changes).
        /// </returns>
        bool Rearm(TimeSpan timeout);

        /// <summary>
        /// Marks a still-<c>Pending</c> deadline as completed, cancelling its pending expiry.
        /// </summary>
        /// <returns>
        /// True if this call won the race and moved the deadline from <c>Pending</c> to
        /// <c>Completed</c>; false if the deadline had already expired, completed, or been cancelled
        /// (idempotent no-op). The return value is the caller's answer to "did I finish before the
        /// deadline fired?".
        /// </returns>
        bool Complete();
    }
}
