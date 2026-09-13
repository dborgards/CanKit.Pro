namespace CanKit.Pro.RawCan
{
    /// <summary>
    /// Two registered <see cref="CanIdFilter"/>-based subscriptions whose ID spaces intersect, and
    /// the range of CAN IDs on which they do (FR-RAW-041) — the result of
    /// <see cref="ICanBusService.FindOverlappingFilterSubscriptions"/>.
    /// </summary>
    /// <remarks>
    /// The relation is symmetric: <see cref="A"/> and <see cref="B"/> are the two subscriptions
    /// that share ID space, in registration order, and swapping them describes the same overlap.
    /// The names deliberately say nothing more than that — an earlier version of this API returned
    /// a <c>(First, Second)</c> tuple, whose element names read as if the order carried meaning.
    /// </remarks>
    /// <param name="A">One of the two overlapping subscriptions.</param>
    /// <param name="B">The other one.</param>
    /// <param name="LowestSharedId">
    /// The smallest CAN ID both filters accept.
    /// </param>
    /// <param name="HighestSharedId">
    /// The largest CAN ID both filters accept. Together with <see cref="LowestSharedId"/> this is
    /// the answer to "where do they collide?", which is what a caller diagnosing a misconfigured
    /// set of protocol instances is actually after.
    /// <para>
    /// For two range filters every ID in between is shared as well. For acceptance-code/mask
    /// filters it is an inclusive hull: every shared ID lies within these bounds, but the IDs in
    /// between need not all be shared, because a mask filter accepts a scattered set rather than a
    /// contiguous run.
    /// </para>
    /// </param>
    public readonly record struct FilterOverlap(
        ISubscription A,
        ISubscription B,
        uint LowestSharedId,
        uint HighestSharedId)
    {
        /// <summary>
        /// Destructures just the two subscriptions, for callers that only want to name the pair:
        /// <c>foreach (var (a, b) in service.FindOverlappingFilterSubscriptions())</c>.
        /// </summary>
        /// <param name="a">Receives <see cref="A"/>.</param>
        /// <param name="b">Receives <see cref="B"/>.</param>
        public void Deconstruct(out ISubscription a, out ISubscription b)
        {
            a = A;
            b = B;
        }
    }
}
