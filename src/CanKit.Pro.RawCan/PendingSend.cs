using System;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;

namespace CanKit.Pro.RawCan
{
    /// <summary>
    /// Identifies a pending echo-matched send by the fields an echo frame is matched against: CAN
    /// ID, payload content, frame kind, and the flags that decide *which frame this is* on the
    /// wire. Multiple concurrent sends with an identical key are matched strictly FIFO (oldest
    /// pending send first) against arriving echoes with the same key — see
    /// <see cref="CanBusService"/>'s pending-send tracking (FR-RAW-031). This mirrors, at the
    /// L2 echo-matching layer, the exact class of bug the review flagged for the ISO-TP prototype's
    /// deadline queue crashing on identical in-flight frames.
    /// </summary>
    internal readonly struct PendingKey : IEquatable<PendingKey>
    {
        // The flags that make two frames different frames rather than the same frame transmitted
        // differently. Ext decides which ID space the ID lives in -- without it a standard 0x100
        // and an extended 0x100 with the same payload match each other, and one send's echo
        // confirms the other's caller. Rtr separates a remote request from a data frame with the
        // same (empty) payload, and Error an error frame from a data frame.
        //
        // Brs and Esi are deliberately *not* part of the identity: they are link-layer
        // transmission attributes, and an adapter may report an echo whose BRS/ESI differ from
        // what the caller asked for (ESI in particular reflects the controller's error state, not
        // the caller's request). Two sends that differ in nothing else are interchangeable for
        // confirmation purposes anyway -- the FIFO hands each of them exactly one echo -- so
        // including these bits could only turn a matched echo into a spurious timeout, which is
        // the very failure this key exists to prevent.
        private const FrameFlags IdentityFlags = FrameFlags.Ext | FrameFlags.Rtr | FrameFlags.Error;

        private readonly int _id;
        private readonly byte[] _payload;
        private readonly FrameFlags _flags;
        private readonly CanFrameType _frameKind;
        private readonly int _hash;

        public PendingKey(int id, ReadOnlyMemory<byte> payload, FrameFlags flags, CanFrameType frameKind)
        {
            _id = id;
            _payload = payload.ToArray();
            _flags = flags & IdentityFlags;
            _frameKind = frameKind;

            // Manual combine: System.HashCode isn't available on netstandard2.0 without an extra
            // package reference, and this hash never needs to be cryptographically strong.
            unchecked
            {
                var hash = 17;
                hash = hash * 31 + _id;
                hash = hash * 31 + (int)_flags;
                hash = hash * 31 + (int)_frameKind;
                foreach (var b in _payload)
                    hash = hash * 31 + b;
                _hash = hash;
            }
        }

        public bool Equals(PendingKey other)
            => _id == other._id
               && _flags == other._flags
               && _frameKind == other._frameKind
               && _payload.AsSpan().SequenceEqual(other._payload);

        public override bool Equals(object? obj) => obj is PendingKey other && Equals(other);

        public override int GetHashCode() => _hash;
    }

    /// <summary>
    /// One outstanding <see cref="ICanBusService.SendConfirmed"/> call awaiting an echo match.
    /// Owns the node reference into its <see cref="PendingKey"/>'s FIFO list so it can remove
    /// itself in O(1) on any resolution path (match, timeout, cancellation, bus-off, or service
    /// disposal) without scanning. Exactly one of those paths ever completes <see cref="Tcs"/>;
    /// all use <c>TrySet*</c>, so a race between two paths (e.g. an echo arriving the same instant
    /// the timeout fires) resolves harmlessly to whichever wins first.
    /// </summary>
    internal sealed class PendingSend
    {
        public PendingSend(PendingKey key)
        {
            Key = key;
        }

        public PendingKey Key { get; }

        public TaskCompletionSource<TxConfirmation> Tcs { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Node in the owning <see cref="System.Collections.Generic.LinkedList{T}"/> for this
        /// pending send's <see cref="Key"/>. Only ever touched under
        /// <see cref="CanBusService"/>'s pending-registry lock. Null once removed.
        /// </summary>
        public System.Collections.Generic.LinkedListNode<PendingSend>? Node { get; set; }
    }
}
