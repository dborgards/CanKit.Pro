using CanKit.Abstractions.API.Common.Definitions;

namespace CanKit.Pro.Reliability
{
    /// <summary>
    /// Small, pure classification helpers over <see cref="BusState"/> (SRS FR-RAW-051), so protocol
    /// instances can express "may I still transmit?" / "is the bus degraded?" without repeating the
    /// same enum comparisons at every call site.
    /// </summary>
    public static class BusStateExtensions
    {
        /// <summary>
        /// True only for <see cref="BusState.BusOff"/>: the controller has removed itself from the
        /// bus and cannot transmit at all until it recovers, so a controlled TX must be aborted
        /// (FR-RAW-051). Error-warning/passive states still allow transmission (the controller is
        /// merely degraded), so they are deliberately <i>not</i> treated as transmit-blocking here.
        /// </summary>
        /// <remarks>
        /// <see cref="BusState.Unknown"/> is deliberately <i>not</i> transmit-blocking, unlike in
        /// <see cref="IsDegraded"/>: plenty of adapters never report a controller state at all, and
        /// refusing to transmit on every one of them would break working setups to guard against a
        /// bus-off we have no evidence for. Blocking TX needs proof; flagging degradation does not.
        /// </remarks>
        public static bool IsTransmitBlocked(this BusState state) => state == BusState.BusOff;

        /// <summary>
        /// True for <see cref="BusState.ErrWarning"/>, <see cref="BusState.ErrPassive"/>,
        /// <see cref="BusState.BusOff"/> and <see cref="BusState.Unknown"/>: the bus is not known
        /// to be in the healthy <see cref="BusState.ErrActive"/> range, and a protocol may want to
        /// pause/slow down or surface a warning (FR-RAW-051), even where transmission is still
        /// technically possible.
        /// </summary>
        /// <remarks>
        /// <see cref="BusState.Unknown"/> counts as degraded on purpose. It means "we could not
        /// determine the controller state", and answering a health question with "healthy" on the
        /// strength of no information is the one answer that can never be justified: a caller that
        /// treats degraded as "warn / slow down / do not start a long block transfer yet" is
        /// merely cautious when the state is unknown, whereas a caller told "healthy" proceeds as
        /// if a bus that may already be off were fine. <see cref="BusState.None"/> stays healthy —
        /// it is the "no error condition" reading, not an absence of information.
        /// </remarks>
        public static bool IsDegraded(this BusState state) =>
            state == BusState.ErrWarning || state == BusState.ErrPassive
            || state == BusState.BusOff || state == BusState.Unknown;
    }
}
