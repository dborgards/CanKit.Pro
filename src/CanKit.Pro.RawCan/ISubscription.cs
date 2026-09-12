using System;
using System.Collections.Generic;

namespace CanKit.Pro.RawCan
{
    /// <summary>
    /// A single, independent, filtered view onto the frames of one <see cref="ICanBusService"/>
    /// (arc42 §5.3 "Multi-Protokoll-Demux"; FR-RAW-010..012, FR-RAW-015).
    /// </summary>
    /// <remarks>
    /// Each subscription owns its own bounded buffer, so a slow (or never-drained) consumer only
    /// drops its own oldest frames and can never delay delivery to other subscriptions or to the
    /// underlying bus's own <c>FrameObserved</c> event (FR-RAW-011).
    /// <para>
    /// Disposing the subscription deterministically deregisters it from the service (it stops
    /// receiving frames) and completes <see cref="Frames"/>, so any in-flight
    /// <c>await foreach</c> terminates gracefully. Dispose is idempotent (FR-RAW-012).
    /// </para>
    /// <para>
    /// <see cref="Frames"/> is exposed without a cancellation-token parameter to match the arc42
    /// interface shape; pass a token via <c>subscription.Frames.WithCancellation(token)</c> to
    /// stop enumerating early.
    /// </para>
    /// <para>
    /// The stream carries <see cref="CanFrameEvent"/>, not a bare frame: the bus reports an echo
    /// flag and a receive timestamp alongside every frame, and a demux that dropped them forced
    /// every protocol layer above to guess at the first and do without the second. A subscription
    /// that did not ask for echoes never sees one at all, so the flag matters only to a caller
    /// that opted in.
    /// </para>
    /// </remarks>
    public interface ISubscription : IDisposable
    {
        /// <summary>
        /// Asynchronously yields the frames accepted by this subscription's filter, in arrival
        /// order, from its own buffer. Completes when the subscription (or its owning service) is
        /// disposed.
        /// </summary>
        IAsyncEnumerable<CanFrameEvent> Frames { get; }

        /// <summary>
        /// Non-blocking: try to remove one already-buffered frame from this subscription. Returns
        /// <c>false</c> if the buffer is currently empty.
        /// </summary>
        /// <remarks>
        /// This is the synchronous counterpart to <see cref="Frames"/>: it never awaits and never
        /// waits for a frame to arrive, it only removes what has already been queued at the moment
        /// of the call. It is intended for consumers that need to snapshot-then-drop stale traffic
        /// (for example, a request/reply client draining background chatter before issuing its
        /// request) without racing an async enumerator against a wall-clock deadline.
        /// </remarks>
        /// <param name="frameEvent">The buffered frame, if any; otherwise the default value.</param>
        /// <returns><c>true</c> if a frame was removed; <c>false</c> if the buffer was empty.</returns>
        bool TryRead(out CanFrameEvent frameEvent);

        /// <summary>
        /// Replaces this subscription's filter with an ID-range/mask fast-path filter (FR-RAW-014).
        /// Any previously configured predicate is cleared. Frames already buffered are not
        /// retroactively filtered; only frames observed after this call follow the new criterion.
        /// </summary>
        /// <remarks>
        /// The echo choice made at <c>Subscribe</c> time is not part of the filter and is not
        /// affected by reconfiguring one.
        /// </remarks>
        /// <param name="filter">The new filter. Must not be a default-initialized struct when a
        /// meaningful filter is required — use <see cref="Reconfigure(Func{CanFrameEvent, bool})"/>
        /// with <c>null</c> to accept all frames.</param>
        void Reconfigure(CanIdFilter filter);

        /// <summary>
        /// Replaces this subscription's filter with a generic predicate, or accepts all frames when
        /// <paramref name="predicate"/> is <c>null</c> (FR-RAW-014). Any previously configured
        /// <see cref="CanIdFilter"/> is cleared. Frames already buffered are not retroactively
        /// filtered; only frames observed after this call follow the new criterion.
        /// </summary>
        /// <remarks>
        /// The predicate runs on the bus's dispatch thread, before the payload is copied into this
        /// subscription's buffer, so the <see cref="CanFrameEvent.Frame"/> it inspects aliases the
        /// adapter's RX lease and must not be retained beyond the call. The echo choice made at
        /// <c>Subscribe</c> time is not part of the filter and is not affected by reconfiguring
        /// one: a predicate on a subscription that did not opt in is never even offered an echo.
        /// </remarks>
        void Reconfigure(Func<CanFrameEvent, bool>? predicate);
    }
}
