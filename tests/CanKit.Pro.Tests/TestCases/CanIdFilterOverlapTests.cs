using System;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core;
using CanKit.Core.Definitions;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace CanKit.Pro.Tests.TestCases;

/// <summary>
/// Verifies <see cref="CanIdFilter.Overlaps"/> and
/// <see cref="ICanBusService.FindOverlappingFilterSubscriptions"/> (arc42 "Adressierungs-Helfer",
/// SRS FR-RAW-041).
/// </summary>
public class CanIdFilterOverlapTests : IClassFixture<VirtualAdapterFixture>
{
    private static string NewSession() => VirtualAdapterFixture.NewSession("overlap");

    private static ICanBus Open(string session, int channel) => VirtualAdapterFixture.Open(session, channel);

    [Fact]
    public void Range_Filters_That_Overlap_Are_Detected()
    {
        var a = CanIdFilter.Range(0x100, 0x1FF);
        var b = CanIdFilter.Range(0x180, 0x2FF);

        a.Overlaps(b).Should().BeTrue();
        b.Overlaps(a).Should().BeTrue("overlap must be symmetric");
    }

    [Fact]
    public void Range_Filters_That_Are_Disjoint_Are_Not_Detected_As_Overlapping()
    {
        var a = CanIdFilter.Range(0x100, 0x1FF);
        var b = CanIdFilter.Range(0x200, 0x2FF);

        a.Overlaps(b).Should().BeFalse();
    }

    [Fact]
    public void Range_Filters_On_Different_Id_Types_Never_Overlap_Even_With_Numerically_Identical_Bounds()
    {
        var std = CanIdFilter.Range(0x100, 0x1FF, CanFilterIDType.Standard);
        var ext = CanIdFilter.Range(0x100, 0x1FF, CanFilterIDType.Extend);

        std.Overlaps(ext).Should().BeFalse();
    }

    [Fact]
    public void Mask_Filters_That_Overlap_Are_Detected()
    {
        // Both accept any ID with bit 0x100 set; they disagree only on bits neither one masks.
        var a = CanIdFilter.Mask(accCode: 0x100, accMask: 0x100);
        var b = CanIdFilter.Mask(accCode: 0x100, accMask: 0x300);

        a.Overlaps(b).Should().BeTrue();
    }

    [Fact]
    public void Mask_Filters_That_Disagree_On_A_Commonly_Masked_Bit_Do_Not_Overlap()
    {
        var a = CanIdFilter.Mask(accCode: 0x000, accMask: 0x100); // bit 0x100 must be 0
        var b = CanIdFilter.Mask(accCode: 0x100, accMask: 0x100); // bit 0x100 must be 1

        a.Overlaps(b).Should().BeFalse();
    }

    [Fact]
    public void Range_And_Mask_Filters_That_Overlap_Are_Detected()
    {
        var range = CanIdFilter.Range(0x100, 0x10F);
        // Mask matches 0x1F0..0x1FF (bits 0x1F0 fixed, low 4 bits free) -- no overlap with the range.
        var nonOverlappingMask = CanIdFilter.Mask(accCode: 0x1F0, accMask: 0x7F0);
        // Mask matches 0x100..0x10F (bits 0x1F0 fixed to 0x100, low 4 bits free) -- overlaps the range.
        var overlappingMask = CanIdFilter.Mask(accCode: 0x100, accMask: 0x7F0);

        range.Overlaps(nonOverlappingMask).Should().BeFalse();
        range.Overlaps(overlappingMask).Should().BeTrue();
        overlappingMask.Overlaps(range).Should().BeTrue("overlap must be symmetric regardless of argument order");
    }

    // An acceptance mask may reach above the ID space: those bits are then required to be *zero*,
    // which every real CAN ID already satisfies, so the filter stays perfectly usable. Only a
    // filter that requires an out-of-space bit to be one is impossible, and that one is rejected
    // at construction (see the tests below).
    [Fact]
    public void A_Mask_Reaching_Above_The_Id_Space_Still_Overlaps_A_Range_It_Shares_Ids_With()
    {
        var range = CanIdFilter.Range(0x100, 0x10F, CanFilterIDType.Extend);
        var mask = CanIdFilter.Mask(accCode: 0x100, accMask: 0x20000700, idType: CanFilterIDType.Extend);

        range.Overlaps(mask).Should().BeTrue();
        mask.Overlaps(range).Should().BeTrue("overlap must be symmetric regardless of argument order");
    }

    // A filter outside its own ID space never matched anything and reported nothing, which made a
    // forgotten idType (a 29-bit ID left on the Standard default) as good as invisible. Both
    // factories reject it instead. Matches() only ever sees IDs already clipped to the space, so
    // there is no reading under which such a filter could have been meant.
    [Fact]
    public void Range_Rejects_Bounds_Outside_The_Standard_11Bit_Space()
    {
        var forgottenIdType = () => CanIdFilter.Range(0x18FEF100, 0x18FEF1FF);
        forgottenIdType.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*Extend*", "the message must name the fix, not just the fault");

        var upperBoundEscapes = () => CanIdFilter.Range(0x7F0, 0x900);
        upperBoundEscapes.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Range_Rejects_Bounds_Outside_The_Extended_29Bit_Space()
    {
        var act = () => CanIdFilter.Range(0x1FFFFFF0, 0x20000100, CanFilterIDType.Extend);
        act.Should().Throw<ArgumentOutOfRangeException>();

        var entirelyOutside = () => CanIdFilter.Range(0x20000000, 0x20000010, CanFilterIDType.Extend);
        entirelyOutside.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Mask_Rejects_A_Code_Requiring_A_Bit_Outside_The_Id_Space()
    {
        // Bit 29 set in both code and mask: no CAN ID has that bit, so nothing could ever match.
        var extended = () => CanIdFilter.Mask(accCode: 0x20000100, accMask: 0x20000700, idType: CanFilterIDType.Extend);
        extended.Should().Throw<ArgumentOutOfRangeException>();

        var standard = () => CanIdFilter.Mask(accCode: 0x18FEF100, accMask: 0x1FFFFF00);
        standard.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void FindOverlappingFilterSubscriptions_Reports_Overlapping_Registered_Subscriptions()
    {
        using var bus = Open(NewSession(), 0);
        using var service = new CanBusService(bus);

        using var a = service.Subscribe(CanIdFilter.Range(0x100, 0x1FF));
        using var b = service.Subscribe(CanIdFilter.Range(0x180, 0x2FF));
        using var c = service.Subscribe(CanIdFilter.Range(0x300, 0x3FF)); // disjoint from both

        var overlaps = service.FindOverlappingFilterSubscriptions();

        overlaps.Should().ContainSingle();
        var overlap = overlaps[0];
        new[] { overlap.A, overlap.B }.Should().BeEquivalentTo(new[] { a, b });

        // The point of the named type over the old (First, Second) tuple: it can say *where* they
        // collide, which is what someone looking at an unexpected overlap wants to know.
        overlap.LowestSharedId.Should().Be(0x180);
        overlap.HighestSharedId.Should().Be(0x1FF);

        // The two subscriptions still destructure directly, for callers that only want the pair.
        var (first, second) = overlap;
        first.Should().BeSameAs(overlap.A);
        second.Should().BeSameAs(overlap.B);
    }

    // Two acceptance-mask filters accept scattered ID sets, so the reported range is the inclusive
    // hull: both bounds are shared, and every shared ID lies between them, but the IDs in between
    // need not be.
    [Fact]
    public void An_Overlap_Between_Mask_Filters_Reports_The_Hull_Of_The_Shared_Ids()
    {
        using var bus = Open(NewSession(), 0);
        using var service = new CanBusService(bus);

        // Shared IDs are exactly those with 0x100 set and 0x200 clear: 0x100..0x1FF and
        // 0x500..0x5FF (bit 0x400 is unconstrained by either filter).
        using var a = service.Subscribe(CanIdFilter.Mask(accCode: 0x100, accMask: 0x100));
        using var b = service.Subscribe(CanIdFilter.Mask(accCode: 0x000, accMask: 0x200));

        var overlap = service.FindOverlappingFilterSubscriptions().Should().ContainSingle().Subject;

        overlap.LowestSharedId.Should().Be(0x100);
        overlap.HighestSharedId.Should().Be(0x5FF);
    }

    // Overlaps and the shared-ID range are decided by a bit walk that never looks at an actual ID,
    // and both now come out of one search. This checks that search against the definition: sweep
    // the entire standard 11-bit ID space, ask Matches directly, and compare. 13 filters, every
    // ordered pair, 2048 IDs each.
    //
    // Driven through the service rather than the filters, because the range is reported on
    // FilterOverlap; Reconfigure re-points the same two subscriptions instead of opening 169 buses.
    [Fact]
    public void Overlap_And_Reported_Range_Agree_With_A_Brute_Force_Sweep_Of_The_Id_Space()
    {
        var filters = new[]
        {
            CanIdFilter.Range(0x000, 0x7FF),
            CanIdFilter.Range(0x100, 0x1FF),
            CanIdFilter.Range(0x180, 0x2FF),
            CanIdFilter.Range(0x300, 0x3FF),
            CanIdFilter.Range(0x000, 0x000),
            CanIdFilter.Range(0x7FF, 0x7FF),
            CanIdFilter.Mask(accCode: 0x000, accMask: 0x000), // constrains nothing: matches every ID
            CanIdFilter.Mask(accCode: 0x100, accMask: 0x100),
            CanIdFilter.Mask(accCode: 0x000, accMask: 0x200),
            CanIdFilter.Mask(accCode: 0x123, accMask: 0x7FF), // exactly one ID
            CanIdFilter.Mask(accCode: 0x100, accMask: 0x700),
            CanIdFilter.Mask(accCode: 0x555, accMask: 0x555),
            CanIdFilter.Mask(accCode: 0x040, accMask: 0x0C0),
        };

        using var bus = Open(NewSession(), 0);
        using var service = new CanBusService(bus);
        using var subA = service.Subscribe(filters[0]);
        using var subB = service.Subscribe(filters[0]);

        for (var i = 0; i < filters.Length; i++)
        {
            for (var j = 0; j < filters.Length; j++)
            {
                var a = filters[i];
                var b = filters[j];
                subA.Reconfigure(a);
                subB.Reconfigure(b);

                uint? lowest = null;
                uint? highest = null;
                for (uint id = 0; id <= 0x7FF; id++)
                {
                    var view = new CanFrameView(CanFrameType.Can20, (int)id, ReadOnlyMemory<byte>.Empty, FrameFlags.None);
                    if (!a.Matches(view) || !b.Matches(view)) continue;
                    lowest ??= id;
                    highest = id;
                }

                var overlaps = service.FindOverlappingFilterSubscriptions();
                var because = $"filters[{i}] and filters[{j}]";

                if (lowest is null)
                {
                    overlaps.Should().BeEmpty($"no ID matches both of {because}");
                    continue;
                }

                overlaps.Should().ContainSingle(because);
                overlaps[0].LowestSharedId.Should().Be(lowest.Value, $"lowest shared ID of {because}");
                overlaps[0].HighestSharedId.Should().Be(highest!.Value, $"highest shared ID of {because}");
            }
        }
    }

    [Fact]
    public void FindOverlappingFilterSubscriptions_Ignores_Predicate_Based_Subscriptions()
    {
        using var bus = Open(NewSession(), 0);
        using var service = new CanBusService(bus);

        using var predicateSub = service.Subscribe(e => e.Frame.ID == 0x150); // would numerically overlap 'range' but is opaque
        using var range = service.Subscribe(CanIdFilter.Range(0x100, 0x1FF));

        service.FindOverlappingFilterSubscriptions().Should().BeEmpty();
    }

    [Fact]
    public void FindOverlappingFilterSubscriptions_Returns_Empty_When_Nothing_Overlaps()
    {
        using var bus = Open(NewSession(), 0);
        using var service = new CanBusService(bus);

        using var a = service.Subscribe(CanIdFilter.Range(0x100, 0x1FF));
        using var b = service.Subscribe(CanIdFilter.Range(0x200, 0x2FF));

        service.FindOverlappingFilterSubscriptions().Should().BeEmpty();
    }
}
