using System;

namespace CanKit.Pro.Tests.TestCases.CANopen.Lss;

internal static class LssTestTiming
{
    // Budget for virtual-bus hops, readers and thread-pool progress on shared runners.
    // Protocol boundaries are measured separately with the virtual clock.
    public static readonly TimeSpan SchedulingBudget = TimeSpan.FromSeconds(5);
}
