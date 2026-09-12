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
        public bool Overlaps(CanIdFilter other) => TryGetSharedIdRange(other, out _, out _);

        /// <summary>
        /// As <see cref="Overlaps"/>, but also reports *where* the two filters collide: the
        /// smallest and largest CAN ID both accept. For two range filters every ID in between is
        /// shared too; for acceptance-mask filters the pair is an inclusive hull, since a mask
        /// filter accepts a scattered set rather than a contiguous run.
        /// </summary>
        /// <remarks>
        /// Internal because <see cref="FilterOverlap"/> is what carries this outward today. It can
        /// be promoted to the public surface later without breaking anything if callers want to
        /// ask two bare filters directly.
        /// </remarks>
        internal bool TryGetSharedIdRange(CanIdFilter other, out uint lowestSharedId, out uint highestSharedId)
        {
            lowestSharedId = 0;
            highestSharedId = 0;

            if (IdType != other.IdType) return false;

            // Matches() only ever sees IDs already clipped to the ID space (via
            // CanFrameView.ID's masking), so the intersection must be clipped the same way --
            // otherwise a range/mask that reaches past 0x7FF (standard) or 0x1FFFFFFF (extended)
            // can be reported as overlapping another filter purely on the out-of-space portion,
            // which no real frame could ever match.
            //
            // Range and Mask now reject the inputs that made this load-bearing, so the clipping
            // below and the full-width walk in SharedIdsIn are the second line of defence rather
            // than the first. They are kept deliberately: they are what makes this correct
            // independently of the factories, and an acceptance mask reaching above the ID space
            // is still perfectly legal (it constrains those bits to zero, which every ID meets).
            var maxId = MaxId(IdType);

            // Every combination reduces to the same question -- which IDs in [lo, hi] satisfy
            // (id & mask) == (code & mask) -- with a range contributing bounds and an acceptance
            // filter contributing a code/mask pair. A range/range pair constrains no bits, so it
            // passes an all-zero mask and the bounds answer on their own.
            return (_kind, other._kind) switch
            {
                (Kind.Range, Kind.Range) => SharedIdsIn(
                    Math.Max(_a, other._a), Math.Min(Math.Min(_b, other._b), maxId),
                    code: 0, mask: 0, out lowestSharedId, out highestSharedId),
                // Two acceptance-mask filters overlap iff, on every bit position both masks
                // constrain, the two required bit patterns agree, and some in-space ID exists
                // that also satisfies whichever bits either mask alone constrains -- bit
                // positions constrained by neither filter are always satisfiable by some ID.
                (Kind.Mask, Kind.Mask) => (_a & _b & other._b) == (other._a & _b & other._b)
                    && SharedIdsIn(0, maxId, (_a & _b) | (other._a & other._b), _b | other._b,
                        out lowestSharedId, out highestSharedId),
                (Kind.Range, Kind.Mask) => SharedIdsIn(
                    _a, Math.Min(_b, maxId), other._a, other._b, out lowestSharedId, out highestSharedId),
                (Kind.Mask, Kind.Range) => SharedIdsIn(
                    other._a, Math.Min(other._b, maxId), _a, _b, out lowestSharedId, out highestSharedId),
                _ => false,
            };
        }

        // Does some ID in [lo, hi] satisfy (id & mask) == (code & mask), and if so, which is the
        // smallest and which the largest? Both are the same search (see FindWitness) run with
        // opposite preferences, so answering "where do they collide" costs a second O(bit-width)
        // walk over what "do they collide at all" already had to establish.
        private static bool SharedIdsIn(uint lo, uint hi, uint code, uint mask, out uint lowest, out uint highest)
        {
            lowest = 0;
            highest = 0;

            if (lo > hi) return false; // empty range once clipped to the ID space
            if (!FindWitness(lo, hi, code, mask, preferHigh: false, out lowest)) return false;

            // Cannot fail once the low witness exists: same feasibility, opposite preference.
            FindWitness(lo, hi, code, mask, preferHigh: true, out highest);
            return true;
        }

        // Is there an ID in [lo, hi] satisfying (id & mask) == (code & mask), and what is the
        // smallest (preferHigh: false) or largest (preferHigh: true) such ID? Bit-by-bit search
        // from the MSB down, tracking whether the prefix built so far is still exactly equal to
        // lo's/hi's prefix ("tight"); once neither bound is tight anymore the range constrains
        // nothing further, so the remaining bits are filled in directly -- the mask's required
        // bits from the code, the free ones all 0 for the smallest ID and all 1 for the largest --
        // rather than enumerating actual ID values. Runs in O(bit-width): at most one branch stays
        // "tight" past any given level, so this never actually branches into an exponential search
        // despite the naive-looking recursion.
        //
        // Preferring a value per bit and falling back to the other is what makes the result the
        // true minimum/maximum rather than just some witness: at the most significant bit still
        // free, a 0 (respectively 1) beats every completion that puts the opposite bit there, so
        // taking it whenever *any* completion below exists is exact.
        //
        // Walks the full 32 bits (not just the 29 bits of a valid extended CAN ID): Matches()
        // performs a plain (id & _b) == (_a & _b) with no restriction on which bits of _b/_a are
        // set, and Mask/Mask overlap likewise compares the full mask, so an accMask/accCode pair
        // with bits set above bit 28 -- which can never be satisfied by any real CAN ID, since
        // id's high bits are always 0 -- must be honored here too, or a range/mask pair could be
        // reported as overlapping when no ID in the range could actually satisfy Matches.
        private static bool FindWitness(uint lo, uint hi, uint code, uint mask, bool preferHigh, out uint witness)
        {
            return Search(31, true, true, 0u, out witness);

            bool Search(int bit, bool loTight, bool hiTight, uint prefix, out uint result)
            {
                if (bit < 0)
                {
                    result = prefix;
                    return true;
                }

                if (!loTight && !hiTight)
                {
                    // Neither bound constrains the remaining bits any more, so fill them in.
                    var remaining = bit >= 31 ? uint.MaxValue : (1u << (bit + 1)) - 1;
                    var required = code & mask & remaining;
                    result = prefix | (preferHigh ? required | (remaining & ~mask) : required);
                    return true;
                }

                var b = 1u << bit;
                var loBit = (lo & b) != 0;
                var hiBit = (hi & b) != 0;
                var masked = (mask & b) != 0;
                var forcedBit = (code & b) != 0;

                bool TryBit(bool v, out uint r)
                {
                    r = 0;
                    if (loTight && !v && loBit) return false; // would fall below lo while still tight
                    if (hiTight && v && !hiBit) return false; // would exceed hi while still tight
                    return Search(bit - 1, loTight && v == loBit, hiTight && v == hiBit,
                        v ? prefix | b : prefix, out r);
                }

                if (masked) return TryBit(forcedBit, out result);
                return TryBit(preferHigh, out result) || TryBit(!preferHigh, out result);
            }
        }
    }
}
