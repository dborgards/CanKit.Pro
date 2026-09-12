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
    /// That loss was not cosmetic: the echo flag and the timestamp were simply unavailable above
    /// the demux, however much a caller wanted them. What it is <em>not</em> is a replacement for
    /// the self-traffic checks the protocol layers carry. <see cref="IsEcho"/> is host-scoped and
    /// only as reliable as the adapter that sets it, so J1939 still compares NAMEs, the J1939
    /// transport still compares source addresses, and CANopen's actor-provenance guard answers a
    /// different question entirely (which thread applied an OD write, not which node sent a
    /// frame). All three are retained deliberately — see the remarks on <see cref="IsEcho"/> for
    /// why the flag cannot stand in for them.
    /// </para>
    /// <para>
    /// A CanKit.Pro type rather than the upstream <c>CanReceiveDataView</c>: the payload of a
    /// buffered frame has to be copied (the adapter may return the RX lease to its pool while a
    /// subscriber has not read it yet), and rebuilding an upstream event payload around a copied
    /// frame would tie this package's public surface to a type it does not own. Note that the
    /// copy is made for buffered events only — see <see cref="Frame"/> for the two lifetimes.
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

        /// <summary>The frame.</summary>
        /// <remarks>
        /// <b>Two lifetimes, depending on where you received this event.</b> An event read from
        /// <see cref="ISubscription.Frames"/> or <see cref="ISubscription.TryRead"/> owns its
        /// payload buffer and stays valid indefinitely — the demux copies the payload before
        /// buffering it, precisely so the adapter may release the RX lease meanwhile.
        /// <para>
        /// An event handed to a <b>subscription predicate</b> does not. The predicate runs on the
        /// dispatch thread before the copy is made, so its <see cref="Frame"/> aliases the
        /// adapter's RX lease and must not be retained past the call — a rejected frame is never
        /// copied at all, which is what keeps filtering allocation-free. Inspect it, return, and
        /// keep nothing.
        /// </para>
        /// </remarks>
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

        /// <summary>
        /// Value equality over the frame's kind, ID, flags and <em>payload bytes</em>, plus
        /// <see cref="IsEcho"/> and <see cref="ReceiveTimestamp"/>.
        /// </summary>
        /// <remarks>
        /// Deliberately not delegating to <see cref="CanFrameView"/>'s own equality. That type is
        /// a record struct holding a <see cref="ReadOnlyMemory{T}"/>, whose generated comparison
        /// tests the memory segment — the backing object, offset and length — rather than the
        /// bytes. Since the demux allocates a fresh array per delivered frame, two events carrying
        /// an identical frame to two subscriptions would otherwise never compare equal, which is
        /// the opposite of what a caller writing <c>a == b</c> means.
        /// <para>
        /// The cost is that comparison is O(payload), up to 64 bytes for CAN FD. That is the right
        /// trade for a type callers compare in assertions and deduplication, but it makes this a
        /// poor dictionary key on a hot path.
        /// </para>
        /// </remarks>
        public bool Equals(CanFrameEvent other)
            => IsEcho == other.IsEcho
               && ReceiveTimestamp == other.ReceiveTimestamp
               && Frame.FrameKind == other.Frame.FrameKind
               && Frame.ID == other.Frame.ID
               && Frame.Flags == other.Frame.Flags
               && Frame.Data.Span.SequenceEqual(other.Frame.Data.Span);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is CanFrameEvent other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            // Built from the same values Equals compares. The payload contributes its bytes, not
            // its buffer identity, so equal frames hash equally however they were allocated.
            var hash = ((int)Frame.FrameKind * 397) ^ Frame.ID;
            hash = (hash * 397) ^ (int)Frame.Flags;
            var data = Frame.Data.Span;
            for (var i = 0; i < data.Length; i++)
                hash = (hash * 31) ^ data[i];
            hash = (hash * 397) ^ IsEcho.GetHashCode();
            return (hash * 397) ^ ReceiveTimestamp.GetHashCode();
        }

        /// <summary>Equality operator.</summary>
        public static bool operator ==(CanFrameEvent left, CanFrameEvent right) => left.Equals(right);

        /// <summary>Inequality operator.</summary>
        public static bool operator !=(CanFrameEvent left, CanFrameEvent right) => !left.Equals(right);
    }
}
