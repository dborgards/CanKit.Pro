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
        var pair = overlaps[0];
        new[] { pair.First, pair.Second }.Should().BeEquivalentTo(new[] { a, b });
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
