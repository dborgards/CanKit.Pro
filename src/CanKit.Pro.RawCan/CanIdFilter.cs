using System;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;

namespace CanKit.Pro.RawCan
{
    /// <summary>
    /// Allocation-free ID-range/mask filter for a <see cref="ISubscription"/> (FR-RAW-013,
    /// "Should"). This is the fast path for the common case "one 11/29-bit CAN-ID range (or
    /// acceptance code/mask) per protocol instance": it is evaluated directly against the
    /// read-only <see cref="CanFrameView"/> that the demux layer carries, without allocating or
    /// invoking a generic <see cref="Func{T,TResult}"/> delegate per frame.
    /// </summary>
    /// <remarks>
    /// The match logic (ID-type/extended guard + inclusive range / acceptance-mask compare)
    /// intentionally mirrors <c>CanKit.Core.Definitions.FilterRule.Range</c> and
    /// <c>FilterRule.Mask</c> so this package does not introduce a parallel filter vocabulary.
    /// It is replicated here rather than reused because those rules compile to a
    /// <c>Func&lt;CanFrame, bool&gt;</c> that operates on the disposable <see cref="CanFrame"/>,
    /// whereas the demux layer only ever exposes the non-owning <see cref="CanFrameView"/>; the
    /// range/mask check itself is a couple of integer comparisons, so replicating it is both
    /// simpler and cheaper than converting a view back into a frame per match. The
    /// <see cref="CanFilterIDType"/> vocabulary is reused as-is.
    /// </remarks>
    public readonly struct CanIdFilter
    {
        private enum Kind : byte
        {
            Range = 0,
            Mask = 1,
        }

        // Largest valid standard (11-bit) / extended (29-bit) CAN ID, matching the masks
        // CanFrameView.ID applies before Matches ever sees an ID.
        private const uint ID_STD_MASK = 0x7FF;
        private const uint ID_EXT_MASK = 0x1FFFFFFF;

        private readonly Kind _kind;

        // Range: [_a.._b] inclusive. Mask: _a = acceptance code, _b = acceptance mask.
        private readonly uint _a;
        private readonly uint _b;

        private CanIdFilter(Kind kind, uint a, uint b, CanFilterIDType idType)
        {
            _kind = kind;
            _a = a;
            _b = b;
            IdType = idType;
        }

        /// <summary>
        /// ID space this filter targets (standard 11-bit vs. extended 29-bit). Frames of the other
        /// ID space never match.
        /// </summary>
        public CanFilterIDType IdType { get; }

        /// <summary>
        /// Creates an inclusive ID-range filter [<paramref name="from"/>..<paramref name="to"/>].
        /// </summary>
        /// <param name="from">Minimum ID, inclusive.</param>
        /// <param name="to">Maximum ID, inclusive.</param>
        /// <param name="idType">Standard or extended ID space.</param>
        /// <exception cref="ArgumentException"><paramref name="to"/> is below <paramref name="from"/>.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// A bound lies outside <paramref name="idType"/>'s ID space (0x7FF for standard,
        /// 0x1FFFFFFF for extended). Such a filter can never match anything, because
        /// <see cref="Matches"/> only ever sees IDs already clipped to that space — the usual
        /// cause is a 29-bit ID passed without <see cref="CanFilterIDType.Extend"/>.
        /// </exception>
        public static CanIdFilter Range(uint from, uint to, CanFilterIDType idType = CanFilterIDType.Standard)
        {
            if (to < from) throw new ArgumentException("'to' must be greater than or equal to 'from'.", nameof(to));

            // Fail loudly rather than never matching. A filter built from an out-of-space bound --
            // Range(0x18FEF100, ...) with the idType forgotten is the canonical one -- silently
            // accepted no frames and reported nothing, which is the most expensive way for this
            // kind of mistake to be found.
            var maxId = MaxId(idType);
            if (from > maxId) throw OutOfIdSpace(nameof(from), from, idType, maxId);
            if (to > maxId) throw OutOfIdSpace(nameof(to), to, idType, maxId);

            return new CanIdFilter(Kind.Range, from, to, idType);
        }

        /// <summary>
        /// Creates an acceptance-code/mask filter: a frame matches when
        /// <c>(id &amp; accMask) == (accCode &amp; accMask)</c>.
        /// </summary>
        /// <param name="accCode">Acceptance code.</param>
        /// <param name="accMask">Acceptance mask; only the set bits are compared.</param>
        /// <param name="idType">Standard or extended ID space.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The pair requires a bit outside <paramref name="idType"/>'s ID space to be set --
        /// <c>(accCode &amp; accMask)</c> reaches above 0x7FF (standard) or 0x1FFFFFFF (extended).
        /// No real CAN ID has those bits set, so the filter could never match. A mask that reaches
        /// above the ID space is fine on its own: it then merely requires those bits to be zero,
        /// which every ID already satisfies.
        /// </exception>
        public static CanIdFilter Mask(uint accCode, uint accMask, CanFilterIDType idType = CanFilterIDType.Standard)
        {
            var maxId = MaxId(idType);
            var required = accCode & accMask;
            if ((required & ~maxId) != 0)
                throw new ArgumentOutOfRangeException(nameof(accCode), accCode,
                    $"This filter requires ID bits outside the {idType} ID space to be set " +
                    $"(accCode & accMask = 0x{required:X}, the space ends at 0x{maxId:X}), so no frame could ever " +
                    "match it. Pass CanFilterIDType.Extend for a 29-bit ID.");

            return new CanIdFilter(Kind.Mask, accCode, accMask, idType);
        }

        private static uint MaxId(CanFilterIDType idType)
            => idType == CanFilterIDType.Extend ? ID_EXT_MASK : ID_STD_MASK;

        private static ArgumentOutOfRangeException OutOfIdSpace(string paramName, uint value, CanFilterIDType idType, uint maxId)
            => new(paramName, value,
                $"0x{value:X} is outside the {idType} ID space (0x0..0x{maxId:X}), so no frame could ever match " +
                "this filter. Pass CanFilterIDType.Extend for a 29-bit ID.");

        /// <summary>
        /// Returns true when <paramref name="frame"/> matches this filter.
        /// </summary>
        public bool Matches(in CanFrameView frame)
        {
            // Mirrors FilterRule.Range/.Mask: reject frames from the other ID space first, then
            // compare the (flag-stripped) ID.
            if ((IdType == CanFilterIDType.Extend) != frame.IsExtendedFrame)
                return false;

            var id = (uint)frame.ID;
            return _kind == Kind.Range
                ? id >= _a && id <= _b
                : (id & _b) == (_a & _b);
        }

        /// <summary>
        /// True if some CAN ID exists that both this filter and <paramref name="other"/> would
        /// match (FR-RAW-041, "Should") -- a diagnostic for catching misconfigured protocol
        /// instances whose subscriptions were meant to have disjoint ID spaces. Filters targeting
        /// different <see cref="IdType"/> spaces (Standard vs. Extended) never overlap, since a
        /// frame is never both.
        /// </summary>
        public bool Overlaps(CanIdFilter other)
        {
            if (IdType != other.IdType) return false;

            // Matches() only ever sees IDs already clipped to the ID space (via
            // CanFrameView.ID's masking), so the intersection must be clipped the same way --
            // otherwise a range/mask that reaches past 0x7FF (standard) or 0x1FFFFFFF (extended)
            // can be reported as overlapping another filter purely on the out-of-space portion,
            // which no real frame could ever match.
            //
            // Range and Mask now reject the inputs that made this load-bearing, so the clipping
            // below and the full-width walk in RangeIntersectsMask are the second line of defence
            // rather than the first. They are kept deliberately: they are what makes this correct
            // independently of the factories, and an acceptance mask reaching above the ID space
            // is still perfectly legal (it constrains those bits to zero, which every ID meets).
            var maxId = MaxId(IdType);

            return (_kind, other._kind) switch
            {
                (Kind.Range, Kind.Range) => Math.Max(_a, other._a) <= Math.Min(Math.Min(_b, other._b), maxId),
                // Two acceptance-mask filters overlap iff, on every bit position both masks
                // constrain, the two required bit patterns agree, and some in-space ID exists
                // that also satisfies whichever bits either mask alone constrains -- bit
                // positions constrained by neither filter are always satisfiable by some ID.
                (Kind.Mask, Kind.Mask) => (_a & _b & other._b) == (other._a & _b & other._b)
                    && RangeIntersectsMask(0, maxId, (_a & _b) | (other._a & other._b), _b | other._b),
                (Kind.Range, Kind.Mask) => RangeIntersectsMask(_a, Math.Min(_b, maxId), other._a, other._b),
                (Kind.Mask, Kind.Range) => RangeIntersectsMask(other._a, Math.Min(other._b, maxId), _a, _b),
                _ => false,
            };
        }

        // Does some ID in [lo, hi] satisfy (id & mask) == (code & mask)? Bit-by-bit existence
        // search from the MSB down, tracking whether the prefix built so far is still exactly
        // equal to lo's/hi's prefix ("tight"); once neither bound is tight anymore, every
        // remaining ID satisfying the (now unconstrained-by-range) mask trivially exists, so the
        // search terminates early rather than enumerating actual ID values. Runs in O(bit-width):
        // at most one branch stays "tight" past any given level, so this never actually branches
        // into an exponential search despite the naive-looking recursion.
        //
        // Walks the full 32 bits (not just the 29 bits of a valid extended CAN ID): Matches()
        // performs a plain (id & _b) == (_a & _b) with no restriction on which bits of _b/_a are
        // set, and Mask/Mask overlap likewise compares the full mask, so an accMask/accCode pair
        // with bits set above bit 28 -- which can never be satisfied by any real CAN ID, since
        // id's high bits are always 0 -- must be honored here too, or a range/mask pair could be
        // reported as overlapping when no ID in the range could actually satisfy Matches.
        private static bool RangeIntersectsMask(uint lo, uint hi, uint code, uint mask)
        {
            if (lo > hi) return false; // empty range once clipped to the ID space

            return Exists(31, true, true);

            bool Exists(int bit, bool loTight, bool hiTight)
            {
                if (bit < 0) return true;
                if (!loTight && !hiTight) return true;

                var b = 1u << bit;
                var loBit = (lo & b) != 0;
                var hiBit = (hi & b) != 0;
                var masked = (mask & b) != 0;
                var forcedBit = (code & b) != 0;

                bool TryBit(bool v)
                {
                    if (loTight && !v && loBit) return false; // would fall below lo while still tight
                    if (hiTight && v && !hiBit) return false; // would exceed hi while still tight
                    return Exists(bit - 1, loTight && v == loBit, hiTight && v == hiBit);
                }

                return masked ? TryBit(forcedBit) : TryBit(false) || TryBit(true);
            }
        }
    }
}
