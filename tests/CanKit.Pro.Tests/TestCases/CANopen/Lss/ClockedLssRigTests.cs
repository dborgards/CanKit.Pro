using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Lss;

/// <summary>#362: a progressing scan has no total wall-clock bound; stalled and endless
/// operations still fail. The scheduling clock is controlled independently of protocol time.</summary>
public class ClockedLssRigTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(15);

    private static async Task<int> TickAsync(ClockedLssRig rig, ManualTimeSource scheduling,
        TimeSpan period, TimeSpan hostDelayPerTick, int? count, CancellationToken token)
    {
        int ticks = 0;
        while (!count.HasValue || ticks < count.Value)
        {
            token.ThrowIfCancellationRequested();
            var elapsed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancelled = token.Register(() => elapsed.TrySetCanceled());
            using var timer = rig.Timers.Schedule(period, () =>
            {
                scheduling.Advance(hostDelayPerTick);
                elapsed.TrySetResult(true);
            });
            await elapsed.Task;
            ticks++;
        }
        return ticks;
    }

    [Fact]
    public async Task Progressing_Operations_Can_Exceed_The_Total_Host_Budget()
    {
        var scheduling = new ManualTimeSource();
        using var rig = new ClockedLssRig(schedulingTime: scheduling);
        var operation = TickAsync(rig, scheduling, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(4),
            count: 23, CancellationToken.None);
        (await rig.RunAsync(operation).WaitAsync(Watchdog)).Should().Be(23);
        scheduling.GetTimestamp().Should().BeGreaterThan(LssTestTiming.SchedulingBudget.Ticks);
    }

    [Fact]
    public void A_Zero_Delay_Loop_Cannot_Keep_Refreshing_The_Progress_Guard()
    {
        var scheduling = new ManualTimeSource();
        var guard = new ClockedOperationGuard(scheduling, TimeSpan.Zero, LssTestTiming.SchedulingBudget);
        scheduling.Advance(TimeSpan.FromSeconds(4));
        guard.NoteAdvance(TimeSpan.Zero);
        scheduling.Advance(TimeSpan.FromSeconds(4));
        Action fail = () => guard.Check(TimeSpan.Zero);
        fail.Should().Throw<TimeoutException>().WithMessage("*no progress*");
    }

    [Fact]
    public async Task A_Progressing_Infinite_Loop_Hits_The_Virtual_Cap()
    {
        var scheduling = new ManualTimeSource();
        using var rig = new ClockedLssRig(schedulingTime: scheduling);
        using var stop = new CancellationTokenSource();
        var operation = TickAsync(rig, scheduling, TimeSpan.FromSeconds(1), TimeSpan.Zero, null, stop.Token);
        var run = rig.RunAsync(operation);
        try
        {
            Func<Task> fail = async () => await run.WaitAsync(Watchdog);
            (await fail.Should().ThrowAsync<TimeoutException>()).WithMessage("*virtual time budget*");
        }
        finally
        {
            stop.Cancel();
            try { await operation.WaitAsync(Watchdog); }
            catch (OperationCanceledException)
            {
                operation.IsCanceled.Should().BeTrue("cleanup cancels the timer loop");
            }
            try { await run.WaitAsync(Watchdog); }
            catch (OperationCanceledException)
            {
                run.IsCanceled.Should().BeTrue("the loop cancellation may finish the rig operation");
            }
            catch (TimeoutException error)
            {
                run.IsFaulted.Should().BeTrue("cleanup must observe the guard failure, not time out waiting for the rig");
                run.Exception!.InnerExceptions.Should().Contain(error,
                    "only the timeout recorded by the guard is expected during cleanup");
            }
        }
    }
}
