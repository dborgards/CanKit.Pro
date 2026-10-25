namespace CanKit.Pro.RawCan
{
    /// <summary>
    /// A buffered frame and its subscription-local enqueue number (FR-RAW-016).
    /// See <see cref="ILossAwareSubscription"/> for continuity and competing-reader semantics.
    /// </summary>
    public readonly struct SubscriptionFrame
    {
        /// <summary>Creates a frame envelope. The supplied event retains its payload lifetime.</summary>
        /// <param name="frameEvent">The frame, echo flag and original arrival timestamps.</param>
        /// <param name="sequenceNumber">Accepted enqueue number, modulo 2^64.</param>
        public SubscriptionFrame(CanFrameEvent frameEvent, ulong sequenceNumber)
        {
            FrameEvent = frameEvent;
            SequenceNumber = sequenceNumber;
        }

        /// <summary>The original event, with independently owned payload when read from the demux.</summary>
        public CanFrameEvent FrameEvent { get; }

        /// <summary>
        /// This subscription's accepted enqueue number. Starts at 1 and wraps from
        /// <see cref="ulong.MaxValue"/> to 0; excluded or refused frames consume no number.
        /// </summary>
        public ulong SequenceNumber { get; }
    }
}
