using System;

namespace CanKit.Pro.Actor;

/// <summary>
/// Converts between <see cref="TimeSpan"/> and the ticks of an <see cref="ITimeSource"/> exactly.
/// </summary>
/// <remarks>
/// A <see cref="TimeSpan"/> is a whole number of 100 ns ticks and a source's frequency a whole
/// number per second, so every conversion here is a product and a division by 10^7, both exact in
/// <see cref="decimal"/>. Done through a <see cref="double"/> instead (<c>TotalSeconds * frequency</c>,
/// <c>TimeSpan.FromSeconds</c>), about one whole millisecond in twelve came out one tick off in
/// each direction, and <c>FromSeconds</c> rounds to whole milliseconds on .NET Framework, which
/// hid it there. A delay or a remaining time is a floor ("not before"), so those round up; an
/// elapsed time is "at least", so that rounds down.
/// </remarks>
internal static class TickMath
{
    /// <summary>The source ticks <paramref name="span"/> spans, rounded up, saturating at
    /// <see cref="long.MaxValue"/> for a span no counter will ever reach.</summary>
    internal static long ToTicks(TimeSpan span, long frequency)
    {
        var ticks = Math.Ceiling((decimal)span.Ticks * frequency / TimeSpan.TicksPerSecond);
        return ticks >= long.MaxValue ? long.MaxValue : (long)ticks;
    }

    /// <summary>As <see cref="ToTicks(TimeSpan, long)"/>, for a span measured from
    /// <paramref name="now"/>: saturates the sum, not the span.</summary>
    internal static long DueAt(long now, TimeSpan span, long frequency)
    {
        var ticks = Math.Ceiling((decimal)span.Ticks * frequency / TimeSpan.TicksPerSecond);
        return ticks >= long.MaxValue - now ? long.MaxValue : now + (long)ticks;
    }

    /// <summary>The span <paramref name="sourceTicks"/> of a source covers, rounded up: a time
    /// still to wait.</summary>
    internal static TimeSpan RemainingFromTicks(long sourceTicks, long frequency)
        => TimeSpan.FromTicks((long)Math.Ceiling((decimal)sourceTicks * TimeSpan.TicksPerSecond / frequency));

    /// <summary>The span <paramref name="sourceTicks"/> of a source covers, rounded down: a time
    /// that has at least passed.</summary>
    internal static TimeSpan ElapsedFromTicks(long sourceTicks, long frequency)
        => TimeSpan.FromTicks((long)Math.Floor((decimal)sourceTicks * TimeSpan.TicksPerSecond / frequency));
}
