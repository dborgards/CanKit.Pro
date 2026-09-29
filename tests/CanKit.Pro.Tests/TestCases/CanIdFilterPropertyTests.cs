using System;
using System.Collections.Generic;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core.Definitions;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.TestCases.Properties;
using FluentAssertions;
using Xunit;

namespace CanKit.Pro.Tests.TestCases;

/// <summary>
/// Seeded differential tests (issue #210) of <see cref="CanIdFilter.Overlaps"/> and the bit-witness
/// search behind <c>TryGetSharedIdRange</c> against brute force over <see cref="CanIdFilter.Matches"/>:
/// the 11-bit space exhaustively, the 29-bit space on filters constrained enough that the IDs each
/// one accepts can be enumerated.
/// </summary>
public class CanIdFilterPropertyTests
{
    private const uint Std = 0x7FF;
    private const uint Ext = 0x1FFFFFFF;

    // A 29-bit filter accepts at most 2^16 IDs, so enumerating one side is cheap.
    private const int MaxFreeBits29 = 16;

    // The generator remembers what it built: CanIdFilter keeps its state private.
    // A plain struct rather than a record struct: init-only members need IsExternalInit on net48.
    private readonly struct Gen
    {
        public Gen(CanIdFilter filter, bool isRange, uint a, uint b)
        {
            Filter = filter;
            IsRange = isRange;
            A = a;
            B = b;
        }

        public CanIdFilter Filter { get; }

        public bool IsRange { get; }

        public uint A { get; }

        public uint B { get; }

        public override string ToString()
            => IsRange ? $"Range(0x{A:X}..0x{B:X},{Filter.IdType})" : $"Mask(code=0x{A:X},mask=0x{B:X},{Filter.IdType})";
    }

    private static CanFrameView View(uint id, CanFilterIDType type)
        => new(CanFrameType.Can20, (int)id, ReadOnlyMemory<byte>.Empty,
            type == CanFilterIDType.Extend ? FrameFlags.Ext : FrameFlags.None);

    // A mask leaving at most maxFree bits of the space unconstrained. Sometimes it also reaches above
    // the space, which is legal: it only demands those bits be zero, which every ID meets.
    private static uint RandomMask(SeededRun run, uint space, int maxFree)
    {
        uint mask;
        do
        {
            mask = run.Rng.Next(4) switch
            {
                0 => 0u,
                1 => run.NextUInt(),
                _ => (run.NextUInt() & run.NextUInt()) | (run.NextUInt() & run.NextUInt()),
            };
            if (run.Rng.Next(3) != 0) mask &= space;
        }
        while (SeededRun.PopCount(~mask & space) > maxFree);
        return mask;
    }

    // Mostly aims at a shared anchor ID, so that overlaps are common rather than vanishing.
    private static Gen RandomFilter(SeededRun run, uint anchor, CanFilterIDType type, int maxFree)
    {
        uint space = type == CanFilterIDType.Extend ? Ext : Std;
        bool aimed = run.Rng.Next(5) != 0;
        if (run.Rng.Next(2) == 0)
        {
            uint width = type == CanFilterIDType.Extend ? (1u << run.Rng.Next(0, MaxFreeBits29 + 1)) - 1 : (uint)run.Rng.Next(0, (int)space + 1);
            uint centre = aimed ? anchor : run.NextUInt() & space;
            uint lo = centre - Math.Min(centre, (uint)(run.Rng.NextDouble() * width));
            uint hi = (uint)Math.Min((ulong)space, (ulong)lo + width);
            return new Gen(CanIdFilter.Range(lo, hi, type), true, lo, hi);
        }

        uint mask = RandomMask(run, space, maxFree);
        uint code = aimed ? anchor : run.NextUInt() & space;
        code &= space; // a code bit inside the mask must stay in the space, or Mask() rejects it
        if (run.Rng.Next(2) == 0) code |= run.NextUInt() & ~mask; // bits outside the mask are noise
        return new Gen(CanIdFilter.Mask(code, mask, type), false, code, mask);
    }

    private static IEnumerable<uint> AcceptedIds(Gen g, uint space)
    {
        if (g.IsRange)
        {
            for (ulong id = g.A; id <= Math.Min(g.B, space); id++) yield return (uint)id;
            yield break;
        }

        uint free = ~g.B & space;
        uint fixedBits = g.A & g.B & space;
        uint sub = 0;
        while (true)
        {
            yield return fixedBits | sub;
            if (sub == free) yield break;
            sub = (sub - free) & free; // next subset of the free bits
        }
    }

    [Fact]
    public void Standard_11Bit_Overlap_And_Shared_Range_Match_An_Exhaustive_Sweep()
    {
        var run = new SeededRun(210_201);
        int overlapping = 0;
        for (int i = 0; i < SeededRun.Iterations; i++)
        {
            uint anchor = run.NextUInt() & Std;
            var a = RandomFilter(run, anchor, CanFilterIDType.Standard, 11);
            var b = RandomFilter(run, anchor, CanFilterIDType.Standard, 11);
            var because = run.Tag(i, $"a={a} b={b}");

            uint? lowest = null, highest = null;
            for (uint id = 0; id <= Std; id++)
            {
                var view = View(id, CanFilterIDType.Standard);
                if (!a.Filter.Matches(view) || !b.Filter.Matches(view)) continue;
                lowest ??= id;
                highest = id;
            }

            a.Filter.Overlaps(b.Filter).Should().Be(lowest is not null, because);
            b.Filter.Overlaps(a.Filter).Should().Be(lowest is not null, because + " (symmetry)");
            a.Filter.TryGetSharedIdRange(b.Filter, out var lo, out var hi).Should().Be(lowest is not null, because);
            if (lowest is null) continue;
            overlapping++;
            lo.Should().Be(lowest.Value, because);
            hi.Should().Be(highest!.Value, because);
        }

        overlapping.Should().BeGreaterThan(SeededRun.Iterations / 4, $"seed={run.Seed}: too few overlapping pairs generated");
    }

    [Fact]
    public void Extended_29Bit_Overlap_And_Shared_Range_Match_Brute_Force_On_Enumerable_Filters()
    {
        var run = new SeededRun(210_202);
        int overlapping = 0;
        for (int i = 0; i < SeededRun.Iterations; i++)
        {
            uint anchor = run.NextUInt() & Ext;
            var a = RandomFilter(run, anchor, CanFilterIDType.Extend, MaxFreeBits29);
            var b = RandomFilter(run, anchor, CanFilterIDType.Extend, MaxFreeBits29);
            var because = run.Tag(i, $"a={a} b={b}");

            // Every shared ID is accepted by a, so enumerating a's IDs and asking b finds them all.
            uint? lowest = null, highest = null;
            foreach (var id in AcceptedIds(a, Ext))
            {
                var view = View(id, CanFilterIDType.Extend);
                if (!a.Filter.Matches(view) || !b.Filter.Matches(view)) continue;
                if (lowest is null || id < lowest) lowest = id;
                if (highest is null || id > highest) highest = id;
            }

            a.Filter.Overlaps(b.Filter).Should().Be(lowest is not null, because);
            b.Filter.Overlaps(a.Filter).Should().Be(lowest is not null, because + " (symmetry)");
            a.Filter.TryGetSharedIdRange(b.Filter, out var lo, out var hi).Should().Be(lowest is not null, because);
            if (lowest is null) continue;
            overlapping++;
            lo.Should().Be(lowest.Value, because);
            hi.Should().Be(highest!.Value, because);
        }

        overlapping.Should().BeGreaterThan(SeededRun.Iterations / 4, $"seed={run.Seed}: too few overlapping pairs generated");
    }

    [Fact]
    public void Filters_Of_Different_Id_Spaces_Never_Overlap()
    {
        var run = new SeededRun(210_203);
        for (int i = 0; i < SeededRun.Iterations; i++)
        {
            uint anchor = run.NextUInt() & Std;
            var std = RandomFilter(run, anchor, CanFilterIDType.Standard, 11);
            var ext = RandomFilter(run, anchor, CanFilterIDType.Extend, MaxFreeBits29);
            var because = run.Tag(i, $"std={std} ext={ext}");

            std.Filter.Overlaps(ext.Filter).Should().BeFalse(because);
            ext.Filter.Overlaps(std.Filter).Should().BeFalse(because);
        }
    }
}
