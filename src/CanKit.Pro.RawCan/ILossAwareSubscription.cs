using System.Collections.Generic;

namespace CanKit.Pro.RawCan
{
    /// <summary>
    /// Optional subscription-local capture continuity information (FR-RAW-016).
    /// Subscriptions created by <see cref="CanBusService"/> implement this interface;
    /// existing implementations of <see cref="ISubscription"/> need not do so.
    /// </summary>
    /// <remarks>
    /// These diagnostics describe this subscription's bounded buffer only. Zero drops do not
    /// prove that the adapter, driver or upstream path lost nothing.
    /// <para>
    /// Use one reader and <see cref="FramesWithSequence"/> or <see cref="TryReadWithSequence"/>
    /// to order a discontinuity relative to delivered frames. The first accepted frame has
    /// sequence number 1; each accepted enqueue advances it, modulo 2^64. A number other than
    /// <c>unchecked(previous + 1)</c> identifies missing input before that frame, including
    /// overflow before the first read. Discard pending pairing/timing evidence across such a
    /// gap and qualify the capture separately from protocol findings.
    /// </para>
    /// <para>
    /// Both projections and both read methods consume the same buffer. Competing readers can
    /// also create gaps in a reader's sequence; their reads do not increase the drop counter.
    /// Sequence numbers belong to one subscription's enqueue order, not hardware order, and
    /// cannot be compared across subscriptions. Filtering and echo exclusion allocate no number.
    /// </para>
    /// </remarks>
    public interface ILossAwareSubscription : ISubscription
    {
        /// <summary>
        /// Number of frames actually evicted by this subscription's bounded buffer. Monotonic
        /// for its lifetime, readable after disposal, and independent of other subscriptions.
        /// </summary>
        /// <remarks>
        /// Filter/echo exclusions, reads, cancellation and disposal do not increment it.
        /// This is a separate instantaneous sample, not a timestamped boundary: a reader may
        /// obtain a retained frame before a concurrent enqueue finishes updating the counter.
        /// Use sequence numbers for ordering, rather than assuming a counter sample describes
        /// a particular frame. The counter saturates at <see cref="long.MaxValue"/>; then its
        /// value is a lower bound and a fresh subscription is needed for exact counting.
        /// </remarks>
        long DroppedFrameCount { get; }

        /// <summary>
        /// The same buffered frames as <see cref="ISubscription.Frames"/>, with their accepted
        /// enqueue numbers. Completes after disposal and draining; cancellation ends the reader.
        /// </summary>
        IAsyncEnumerable<SubscriptionFrame> FramesWithSequence { get; }

        /// <summary>
        /// Removes one buffered frame with its enqueue number without waiting. Returns false
        /// and the default value when the buffer is empty. <see cref="ISubscription.WaitToReadAsync"/>
        /// can be used before this method as with <see cref="ISubscription.TryRead"/>.
        /// </summary>
        bool TryReadWithSequence(out SubscriptionFrame frame);
    }
}
