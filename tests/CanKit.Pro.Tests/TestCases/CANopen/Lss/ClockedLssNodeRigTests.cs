using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Lss;

/// <summary>
/// The hang guard of <see cref="ClockedLssNodeRig.RunAsync{T}"/> (#315): it has to end an operation
/// that stops making progress, however that happens, instead of spinning with it. Each case is an
/// operation that never completes; the surrounding <see cref="Task.WhenAny(Task[])"/> turns a guard
/// that does not fire into a failed assertion and not into a hung test run.
/// </summary>
public class ClockedLssNodeRigTests
{
    private static readonly TimeSpan Stall = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan GiveUp = TimeSpan.FromSeconds(15);

    /// <summary>Arms a timer of <paramref name="delay"/> on the rig's virtual clock, waits for it, and repeats.</summary>
    private static async Task<int> ReArmAsync(ClockedLssNodeRig rig, TimeSpan delay, Func<bool> stop)
    {
        while (!stop())
        {
            var fired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var timer = rig.Timers.Schedule(delay, () => fired.TrySetResult(true));
            await fired.Task;
        }

        return 0;
    }

    [Fact]
    public async Task A_Loop_Re_Arming_A_Zero_Delay_Timer_Is_Reported_As_Making_No_Progress()
    {
        // A zero-delay timer is due at once, so the clock never moves. Counting every pass as
        // progress would refresh the stall clock forever and the detector would spin with the loop.
        using var rig = new ClockedLssNodeRig();
        var stop = false;
        var run = rig.RunAsync(ReArmAsync(rig, TimeSpan.Zero, () => Volatile.Read(ref stop)), Stall);

        var finished = await Task.WhenAny(run, Task.Delay(GiveUp));
        Volatile.Write(ref stop, true);

        finished.Should().BeSameAs(run, "the guard has to end a zero-delay loop and not spin with it");
        var failure = async () => await run;
        (await failure.Should().ThrowAsync<TimeoutException>()).WithMessage("*no progress*");
    }

    [Fact]
    public async Task A_Loop_That_Keeps_Advancing_Virtual_Time_Hits_The_Virtual_Cap()
    {
        // The clock moves on every pass, so the stall clock keeps being refreshed; only the cap on
        // virtual time ends this one, and it does so without waiting for any wall time.
        using var rig = new ClockedLssNodeRig();
        var stop = false;
        var run = rig.RunAsync(ReArmAsync(rig, TimeSpan.FromSeconds(1), () => Volatile.Read(ref stop)));

        var finished = await Task.WhenAny(run, Task.Delay(GiveUp));
        Volatile.Write(ref stop, true);

        finished.Should().BeSameAs(run, "five virtual minutes is a few hundred passes, not a wall-clock wait");
        var failure = async () => await run;
        (await failure.Should().ThrowAsync<TimeoutException>()).WithMessage("*virtual time budget*");
    }

    [Fact]
    public async Task An_Operation_That_Completes_Is_Returned_Whatever_The_Clock_Did()
    {
        using var rig = new ClockedLssNodeRig();
        var passes = 0;
        var result = await rig.RunAsync(ReArmAsync(rig, TimeSpan.FromMilliseconds(250), () => ++passes > 20), Stall);

        result.Should().Be(0);
        passes.Should().Be(21);
    }
}
