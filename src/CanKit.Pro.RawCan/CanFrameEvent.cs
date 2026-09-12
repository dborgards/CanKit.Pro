using System;
using CanKit.Abstractions.API.Can.Definitions;

namespace CanKit.Pro.RawCan
{
    /// <summary>
    /// One frame as a subscription saw it: the frame itself, whether it is this host's own
    /// transmit echo, and the timestamp the adapter recorded for it (FR-RAW-015).
    /// </summary>
    /// <remarks>
    /// This is what <see cref="ISubscription.Frames"/> yields. It exists because the bus already
    /// knows all three facts — <c>ICanBus.FrameObserved</c> carries them on
    /// <c>CanReceiveDataView</c> — and the demux used to forward only the frame, discarding the
    /// other two before any subscriber could see them.
    /// <para>
    /// That loss was not cosmetic. Three protocol layers had each rebuilt "is this my own
    /// transmission?" from application data: J1939 compared the NAME in an Address Claim, CANopen
    /// used an actor-affinity guard, and the J1939 transport compared source addresses. Each of
    /// those is a heuristic over payload bytes standing in for a bit the driver had already told
    /// us. <see cref="IsEcho"/> is that bit.
    /// </para>
    /// <para>
    /// A CanKit.Pro type rather than the upstream <c>CanReceiveDataView</c>: the payload of an
    /// observed frame has to be copied before it is buffered (the adapter may return the RX lease
    /// to its pool while a subscriber has not read it yet), and rebuilding an upstream event
    /// payload around a copied frame would tie this package's public surface to a type it does
    /// not own. <see cref="Frame"/> is that owned copy.
    /// </para>
    /// </remarks>
    public readonly struct CanFrameEvent : IEquatable<CanFrameEvent>
    {
        /// <summary>
        /// Creates an event. The caller is responsible for <paramref name="frame"/> owning its
        /// payload; see the remarks on <see cref="CanFrameEvent"/>.
        /// </summary>
        public CanFrameEvent(CanFrameView frame, bool isEcho, TimeSpan receiveTimestamp)
        {
            Frame = frame;
            IsEcho = isEcho;
            ReceiveTimestamp = receiveTimestamp;
        }

        /// <summary>The frame. Owns its payload buffer, so it stays valid after the adapter has
        /// released the RX lease it was observed from.</summary>
        public CanFrameView Frame { get; }

        /// <summary>
        /// True when the bus reported this frame as the local host's own transmit echo.
        /// </summary>
        /// <remarks>
        /// <b>Host, not instance.</b> The bit means "something on this host transmitted this",
        /// and nothing finer. Where several protocol instances share one
        /// <see cref="ICanBusService"/> — which every protocol factory in this repository
        /// documents as supported — a sibling instance's transmission is flagged exactly like
        /// this one's own. Deciding "did *I* send this?" needs an instance-level identity the
        /// demux does not have: a source address, a NAME, a node-id.
        /// </remarks>
        /// <remarks>
        /// Only ever true for a subscription that asked for echoes — see the <c>includeEcho</c>
        /// parameter on <see cref="ICanBusService.Subscribe(Func{CanFrameEvent, bool}, int?, bool)"/>.
        /// A subscription that did not ask never sees an echo at all, so a consumer that does not
        /// care about the distinction does not have to check this.
        /// <para>
        /// <b>The flag is the adapter's, and not every adapter sets it.</b> An adapter that
        /// cannot distinguish an echo — or simply does not bother — reports every frame as
        /// non-echo, and then <c>includeEcho: false</c> has nothing to filter on and the echo is
        /// delivered like any other frame. <c>CanKit.Adapter.Virtual</c> in
        /// <c>ChannelWorkMode.Echo</c> is exactly that case today. So a protocol layer for which
        /// acting on its own transmission would be harmful must still carry its own check; the
        /// echo gate is a first line of defence, not a guarantee.
        /// </para>
        /// </remarks>
        public bool IsEcho { get; }

        /// <summary>
        /// The receive timestamp the adapter recorded, as reported by the bus. Zero on adapters
        /// that do not timestamp; not comparable across buses.
        /// </summary>
        public TimeSpan ReceiveTimestamp { get; }

        /// <inheritdoc />
        public bool Equals(CanFrameEvent other)
            => IsEcho == other.IsEcho
               && ReceiveTimestamp == other.ReceiveTimestamp
               && Frame.Equals(other.Frame);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is CanFrameEvent other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            var hash = Frame.GetHashCode();
            hash = (hash * 397) ^ IsEcho.GetHashCode();
            return (hash * 397) ^ ReceiveTimestamp.GetHashCode();
        }

        /// <summary>Equality operator.</summary>
        public static bool operator ==(CanFrameEvent left, CanFrameEvent right) => left.Equals(right);

        /// <summary>Inequality operator.</summary>
        public static bool operator !=(CanFrameEvent left, CanFrameEvent right) => !left.Equals(right);
    }
}
