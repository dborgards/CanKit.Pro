using System;
using System.Diagnostics;

namespace CanKit.Pro.Actor
{
    /// <summary>
    /// The monotonic tick source the actor measures timer due-times against. Internal on purpose:
    /// it is a seam, not a feature — the public <see cref="IProtocolActor"/> surface keeps talking
    /// in <see cref="TimeSpan"/> delays, and no consumer ever names this type.
    /// </summary>
    /// <remarks>
    /// Deliberately shaped like <see cref="Stopwatch"/> (a raw tick counter plus its frequency)
    /// rather than like a clock returning <see cref="DateTime"/>: a due-time is an <i>elapsed</i>
    /// quantity, and the only way to keep it one is to never let a wall-clock reading into the
    /// arithmetic in the first place. A type that could hand out a wall-clock time would make the
    /// bug this seam exists to prevent expressible again.
    /// </remarks>
    internal interface ITimeSource
    {
        /// <summary>
        /// Current value of the monotonic counter, in units of <see cref="Frequency"/> per second.
        /// Never decreases and is unaffected by wall-clock adjustments (NTP steps, DST, a user
        /// setting the system clock).
        /// </summary>
        long GetTimestamp();

        /// <summary>Ticks of <see cref="GetTimestamp"/> per second.</summary>
        long Frequency { get; }
    }

    /// <summary>
    /// Production <see cref="ITimeSource"/>: <see cref="Stopwatch.GetTimestamp"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="Stopwatch"/> rather than <c>Environment.TickCount64</c> because this assembly
    /// also ships <c>netstandard2.0</c>, where <c>TickCount64</c> does not exist — and rather than
    /// the 32-bit <c>Environment.TickCount</c>, which wraps every ~49.7 days and would turn a
    /// long-lived gateway process into a source of the very same "deadline fires at the wrong
    /// time" class of bug. <see cref="Stopwatch.GetTimestamp"/> is monotonic on every supported
    /// platform and available on every target framework we build.
    /// </remarks>
    internal sealed class MonotonicTimeSource : ITimeSource
    {
        /// <summary>
        /// The single instance every actor uses unless a test substitutes one. Stateless, so
        /// sharing it costs nothing and saves an allocation per actor.
        /// </summary>
        public static readonly MonotonicTimeSource Instance = new MonotonicTimeSource();

        private MonotonicTimeSource() { }

        /// <inheritdoc />
        public long GetTimestamp() => Stopwatch.GetTimestamp();

        /// <inheritdoc />
        public long Frequency => Stopwatch.Frequency;
    }
}
