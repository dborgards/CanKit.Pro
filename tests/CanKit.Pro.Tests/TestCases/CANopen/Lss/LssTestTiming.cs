using System;

namespace CanKit.Pro.Tests.TestCases.CANopen.Lss;

internal static class LssTestTiming
{
    // Budget for virtual-bus hops, readers and thread-pool progress on shared runners.
    // Protocol boundaries are measured separately with the virtual clock.
    //
    // This is a hang detector, not a property under test: no assertion depends on its value, it
    // only decides how long a deadlocked rig takes to fail. It covers a whole clocked operation
    // (a Fastscan is many actor and reader hand-offs), so a process that gets a small share of a
    // core needs far more wall time for the same virtual work: a scan that takes 0.3 s idle took
    // tens of seconds with the test host pinned to one core and 15 busy loops (#315). 5 s turned
    // that starvation into failures; 30 s still reports a real hang within half a minute.
    public static readonly TimeSpan SchedulingBudget = TimeSpan.FromSeconds(30);
}
