using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.Actor;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases;

public class ClockTimeoutTests
{
    [Fact]
    public async Task Expiry_Uses_The_Absolute_Deadline_And_Cancels_Outside_The_Actor()
    {
        using var clock = new VirtualClock();
        var actor = clock.NewActor();
        long deadline = TimeSpan.FromSeconds(1).Ticks;
        clock.Advance(TimeSpan.FromMilliseconds(400));
        using var timeout = new ClockTimeout(actor, deadline);
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = timeout.Token.Register(() => cancelled.TrySetResult(actor.IsOnCurrentActor));
        await clock.WaitUntilTimerArmedAsync(actor, TimeSpan.FromMilliseconds(600), TimeSpan.FromSeconds(5));

        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(600) - TimeSpan.FromTicks(1));
        timeout.IsCancellationRequested.Should().BeFalse();
        await clock.AdvanceAsync(TimeSpan.FromTicks(1));

        (await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeFalse();
        timeout.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task Caller_Cancellation_Still_Wins_When_The_Deadline_Has_Already_Elapsed()
    {
        using var clock = new VirtualClock();
        var actor = clock.NewActor();
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var wait = async () => await ClockTimeout.WaitUntilAsync(actor, 0, caller.Token);

        await wait.Should().ThrowAsync<OperationCanceledException>();
        (await actor.NextTimerDelayAsync()).Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Waiting_Uses_An_Absolute_Deadline_And_Withdraws_Its_Timer_On_Cancellation(bool cancel)
    {
        using var clock = new VirtualClock();
        var actor = clock.NewActor();
        using var caller = new CancellationTokenSource();
        clock.Advance(TimeSpan.FromMilliseconds(400));
        var waiting = ClockTimeout.WaitUntilAsync(actor, TimeSpan.FromSeconds(1).Ticks, caller.Token);
        await clock.WaitUntilTimerArmedAsync(actor, TimeSpan.FromMilliseconds(600), TimeSpan.FromSeconds(5));
        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(600) - TimeSpan.FromTicks(1));
        waiting.IsCompleted.Should().BeFalse();

        if (cancel)
        {
            caller.Cancel();
            var cancelled = async () => await waiting.WaitAsync(TimeSpan.FromSeconds(5));
            (await cancelled.Should().ThrowAsync<OperationCanceledException>())
                .Which.CancellationToken.Should().Be(caller.Token);
        }
        else
        {
            await clock.AdvanceAsync(TimeSpan.FromTicks(1));
            await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        }

        (await actor.NextTimerDelayAsync()).Should().BeNull();
    }
}
