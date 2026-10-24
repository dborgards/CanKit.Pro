using System;
using System.Collections.Generic;
using AwesomeAssertions;
using CanKit.Pro.Actor;
using CanKit.Pro.Uds;
using Xunit;

namespace CanKit.Pro.Tests.TestCases;

/// <summary>
/// The conversions between <see cref="TimeSpan"/> and the ticks of a time source are exact. They
/// used to go through a double, which put about one whole millisecond in twelve a tick off, and
/// .NET Framework's <c>TimeSpan.FromSeconds</c> rounds to milliseconds and hid it there until the
/// actor's own conversion became exact (the 2026-09-30 review, A1).
/// </summary>
public class TickMathTests
{
    public static TheoryData<long> Frequencies => new() { 10_000_000L, 1_000_000_000L, 1_000_000L, 1_000L };

    [Theory]
    [MemberData(nameof(Frequencies))]
    public void A_Span_Of_Whole_Milliseconds_Converts_To_Exactly_Its_Ticks(long frequency)
    {
        var wrong = new List<int>();
        for (var ms = 1; ms <= 2000; ms++)
        {
            var expected = (decimal)ms * frequency / 1000m;
            if (TickMath.ToTicks(TimeSpan.FromMilliseconds(ms), frequency) != (long)Math.Ceiling(expected))
                wrong.Add(ms);
        }

        wrong.Should().BeEmpty();
    }

    [Theory]
    [InlineData(10_000_000L)]
    [InlineData(1_000_000_000L)]
    public void Converting_There_And_Back_Returns_The_Span(long frequency)
    {
        var wrong = new List<int>();
        for (var ms = 1; ms <= 2000; ms++)
        {
            var span = TimeSpan.FromMilliseconds(ms);
            var ticks = TickMath.ToTicks(span, frequency);
            if (TickMath.RemainingFromTicks(ticks, frequency) != span || TickMath.ElapsedFromTicks(ticks, frequency) != span)
                wrong.Add(ms);
        }

        wrong.Should().BeEmpty();
    }

    // The UDS client's response windows are noted and measured in source ticks; the window of
    // 100 ms the failing net48 test used is one of the values the double conversion got wrong.
    [Theory]
    [InlineData(10_000_000L)]
    [InlineData(1_000_000_000L)]
    public void A_Response_Window_Is_Counted_And_Measured_In_Exact_Ticks(long frequency)
    {
        var wrong = new List<int>();
        for (var ms = 1; ms <= 2000; ms++)
        {
            var window = TimeSpan.FromMilliseconds(ms);
            var ticks = SuppressedResponseWindows.Ticks(window, frequency);
            if (ticks != (long)((decimal)ms * frequency / 1000m)
                || SuppressedResponseWindows.Remaining(ticks, 0, frequency) != window)
                wrong.Add(ms);
        }

        wrong.Should().BeEmpty();
    }

    [Fact]
    public void A_Remaining_Time_Rounds_Up_And_An_Elapsed_Time_Rounds_Down()
    {
        // 1 tick of a 10^9 Hz source is 1 ns: a tenth of a TimeSpan tick.
        TickMath.RemainingFromTicks(1, 1_000_000_000).Should().Be(TimeSpan.FromTicks(1));
        TickMath.ElapsedFromTicks(1, 1_000_000_000).Should().Be(TimeSpan.Zero);
        TickMath.RemainingFromTicks(101, 1_000_000_000).Should().Be(TimeSpan.FromTicks(2));
        TickMath.ElapsedFromTicks(101, 1_000_000_000).Should().Be(TimeSpan.FromTicks(1));
    }

    [Fact]
    public void A_Span_No_Counter_Reaches_Saturates_Instead_Of_Overflowing()
    {
        TickMath.ToTicks(TimeSpan.MaxValue, 1_000_000_000).Should().Be(long.MaxValue);
        TickMath.DueAt(5, TimeSpan.MaxValue, 1_000_000_000).Should().Be(long.MaxValue);
        TickMath.DueAt(5, TimeSpan.FromMilliseconds(35), 10_000_000).Should().Be(5 + 350_000);
    }

    // A-6 (2026-10-21 deep review): the saturation test was `ticks >= long.MaxValue - now`, and for a
    // negative now that subtraction overflows to a large negative number, so every delay counted as
    // unreachable and came back as long.MaxValue. No real counter reads negative, but ITimeSource
    // does not forbid it and a test source may be seeded there.
    [Theory]
    [InlineData(-5L)]
    [InlineData(long.MinValue)]
    public void A_Due_Instant_From_A_Negative_Reading_Is_The_Sum_Not_Saturation(long now)
    {
        TickMath.DueAt(now, TimeSpan.FromMilliseconds(35), 10_000_000).Should().Be(now + 350_000);
        TickMath.DueAt(now, TimeSpan.Zero, 10_000_000).Should().Be(now);
        TickMath.DueAt(-5, TimeSpan.MaxValue, 1_000_000_000).Should().Be(long.MaxValue,
            "a span no counter reaches still saturates from a negative reading");
    }
}
