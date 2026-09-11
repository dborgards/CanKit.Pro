using System;
using System.Threading;
using CanKit.Pro.Actor;

namespace CanKit.Pro.Tests.Infrastructure;

/// <summary>
/// A monotonic tick source the test drives by hand, substituted into <see cref="ProtocolActor"/>
/// through its internal constructor.
/// </summary>
/// <remarks>
/// <para>
/// Two things become testable that otherwise are not. First, <b>which</b> clock the actor measures
/// deadlines against: while this source is frozen, real wall-clock time keeps running, so a timer
/// that fires anyway can only have been measured against the wall clock — which is exactly the
/// NTP-step bug (a wall clock jumping forward by many multiples of the delay is indistinguishable,
/// from the timer arithmetic's point of view, from wall time simply elapsing). Second,
/// <b>determinism</b>: advancing time by an exact amount replaces "sleep and hope the CI runner
/// was not busy".
/// </para>
/// <para>
/// The frequency is <see cref="TimeSpan.TicksPerSecond"/> so a <see cref="TimeSpan"/> maps to
/// ticks one-to-one and the arithmetic under test is not obscured by a scaling factor of its own.
/// </para>
/// </remarks>
internal sealed class ManualTimeSource : ITimeSource
{
    private long _timestamp;
    private long _reads;

    /// <inheritdoc />
    public long Frequency => TimeSpan.TicksPerSecond;

    /// <summary>
    /// How often the actor has asked for the time since the last <see cref="ResetReadCount"/>.
    /// A loop that busy-spins instead of sleeping shows up here as several orders of magnitude
    /// more reads, which is the only externally visible symptom a rounding bug in the wait
    /// computation has.
    /// </summary>
    public long ReadCount => Interlocked.Read(ref _reads);

    /// <inheritdoc />
    public long GetTimestamp()
    {
        Interlocked.Increment(ref _reads);
        return Interlocked.Read(ref _timestamp);
    }

    /// <summary>Moves the monotonic counter forward. Never backwards — that is the whole point.</summary>
    public void Advance(TimeSpan by)
    {
        if (by < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(by), "A monotonic clock cannot go backwards.");
        Interlocked.Add(ref _timestamp, by.Ticks);
    }

    public void ResetReadCount() => Interlocked.Exchange(ref _reads, 0);
}
