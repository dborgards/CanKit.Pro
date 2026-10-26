using System;
using CanKit.Pro.Actor;

namespace CanKit.Pro.Tests.TestCases.CANopen.Lss;

/// <summary>Shared hang guard for clocked LSS rigs: positive virtual progress refreshes the
/// scheduling budget, zero-delay work does not, and infinite progress has a virtual cap.</summary>
internal sealed class ClockedOperationGuard
{
    private static readonly TimeSpan MaxVirtualRun = TimeSpan.FromMinutes(5);
    private readonly ITimeSource _clock;
    private readonly long _stallTicks;
    private readonly TimeSpan _started;
    private long _lastProgress;

    internal ClockedOperationGuard(ITimeSource clock, TimeSpan started, TimeSpan stallBudget)
    {
        _clock = clock;
        _started = started;
        _stallTicks = TickMath.ToTicks(stallBudget, clock.Frequency);
        _lastProgress = clock.GetTimestamp();
    }

    internal void Check(TimeSpan virtualTime)
    {
        if (_clock.GetTimestamp() - _lastProgress >= _stallTicks)
            throw new TimeoutException("The clocked LSS operation made no progress.");
        if (virtualTime - _started >= MaxVirtualRun)
            throw new TimeoutException("The clocked LSS operation exhausted its virtual time budget.");
    }

    internal void NoteAdvance(TimeSpan delay)
    {
        if (delay > TimeSpan.Zero) _lastProgress = _clock.GetTimestamp();
    }
}
