using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.Actor;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases;

public class ProtocolActorShutdownPolicyTests
{
    [Theory]
    [InlineData(ActorExecutionMode.DedicatedThread, null)]
    [InlineData(ActorExecutionMode.ThreadPool, null)]
    [InlineData(ActorExecutionMode.SynchronizationContext, null)]
    [InlineData(ActorExecutionMode.DedicatedThread, ActorShutdownPolicy.DrainDueTimers)]
    [InlineData(ActorExecutionMode.ThreadPool, ActorShutdownPolicy.DrainDueTimers)]
    [InlineData(ActorExecutionMode.SynchronizationContext, ActorShutdownPolicy.DrainDueTimers)]
    [InlineData(ActorExecutionMode.DedicatedThread, ActorShutdownPolicy.DiscardPendingTimers)]
    [InlineData(ActorExecutionMode.ThreadPool, ActorShutdownPolicy.DiscardPendingTimers)]
    [InlineData(ActorExecutionMode.SynchronizationContext, ActorShutdownPolicy.DiscardPendingTimers)]
    public async Task Reentrant_Stop_Finishes_Accepted_Mailbox_Work_And_Applies_Only_The_Selected_Timer_Policy(
        ActorExecutionMode mode, ActorShutdownPolicy? policy)
    {
        var context = ContextFor(mode);
        using var actor = policy.HasValue ? new ProtocolActor(mode, context, policy.Value) : new ProtocolActor(mode, context);
        var seen = new List<string>();
        var errors = new List<Exception>();
        actor.BackgroundExceptionOccurred += (_, ex) => errors.Add(ex);
        Task? queued = null;
        using var cancelled = await actor.PostAsync(() =>
        {
            queued = actor.PostAsync(() => seen.Add("accepted"));
            actor.Schedule(TimeSpan.Zero, () =>
            {
                seen.Add("due");
                actor.Schedule(TimeSpan.Zero, () => seen.Add("rearmed"));
            });
            actor.Schedule(TimeSpan.FromDays(1), () => seen.Add("future"));
            var handle = actor.Schedule(TimeSpan.Zero, () => seen.Add("cancelled"));
            handle.Dispose();
            actor.Dispose();
            ((Action)(() => actor.Post(() => { }))).Should().Throw<ObjectDisposedException>();
            ((Action)(() => actor.Schedule(TimeSpan.Zero, () => { }))).Should().Throw<ObjectDisposedException>();
            return handle;
        }).WaitAsync(TimeSpan.FromSeconds(10));
        actor.Dispose(); // Joins the loop, witnessing every callback and the final drain.
        await queued!.WaitAsync(TimeSpan.FromSeconds(10));
        if (policy == ActorShutdownPolicy.DiscardPendingTimers)
        {
            seen.Should().Equal("accepted");
            errors.Should().BeEmpty();
            actor.PendingTimerCount.Should().Be(0, "the joined loop has retired and released pending timers");
        }
        else
        {
            seen.Should().Equal("accepted", "due");
            errors.Should().ContainSingle().Which.Should().BeOfType<ObjectDisposedException>();
        }
        // The using declaration disposes the retired handle a second time, including on assertion failure.

    }

    [Theory]
    [InlineData(ActorExecutionMode.DedicatedThread)]
    [InlineData(ActorExecutionMode.ThreadPool)]
    [InlineData(ActorExecutionMode.SynchronizationContext)]
    public async Task OptIn_Allows_Live_Timers_But_Stops_Selecting_Timers_After_A_Timer_Disposes_The_Actor(ActorExecutionMode mode)
    {
        using var actor = new ProtocolActor(mode, ContextFor(mode), ActorShutdownPolicy.DiscardPendingTimers);
        var seen = new List<string>();
        var firstTimer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? accepted = null;
        await actor.PostAsync(() =>
        {
            actor.Schedule(TimeSpan.Zero, () =>
            {
                seen.Add("first");
                accepted = actor.PostAsync(() => seen.Add("accepted"));
                actor.Dispose();
                firstTimer.TrySetResult(true);
            });
            actor.Schedule(TimeSpan.Zero, () => seen.Add("second"));
        }).WaitAsync(TimeSpan.FromSeconds(10));
        await firstTimer.Task.WaitAsync(TimeSpan.FromSeconds(10));
        actor.Dispose();
        await accepted!.WaitAsync(TimeSpan.FromSeconds(10));
        seen.Should().Equal("first", "accepted");
    }

    [Theory]
    [InlineData(ActorExecutionMode.DedicatedThread)]
    [InlineData(ActorExecutionMode.ThreadPool)]
    [InlineData(ActorExecutionMode.SynchronizationContext)]
    public async Task OffLoop_Stop_Drains_Accepted_Requests_Without_Firing_A_Timer_That_Becomes_Due_During_A_Callback(ActorExecutionMode mode)
    {
        var time = new ManualTimeSource();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var stopping = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var actor = new ProtocolActor(mode, ContextFor(mode), time, null,
            beforeStopRequested: _ => stopping.TrySetResult(true), shutdownPolicy: ActorShutdownPolicy.DiscardPendingTimers);
        var seen = new List<string>();
        actor.Post(() =>
        {
            actor.Schedule(TimeSpan.FromSeconds(1), () => seen.Add("due"));
            entered.Set();
            release.Wait(); // No automatic release can make the ordering assertion vacuous.
            seen.Add("running-completed");
        });
        Task? disposal = null;
        try
        {
            entered.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            var plain = actor.PostAsync(() => { seen.Add("plain"); });
            var generic = actor.PostAsync(() => { seen.Add("generic"); return 42; });
            using var cancellation = new CancellationTokenSource();
            var withdrawn = actor.PostAsync(() => seen.Add("withdrawn"), cancellation.Token);
            cancellation.Cancel();
            time.Advance(TimeSpan.FromSeconds(2));
            disposal = Task.Run(actor.Dispose);
            await stopping.Task.WaitAsync(TimeSpan.FromSeconds(10));
            release.Set();
            await disposal.WaitAsync(TimeSpan.FromSeconds(10));
            await plain.WaitAsync(TimeSpan.FromSeconds(10));
            (await generic.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be(42);
            withdrawn.IsCanceled.Should().BeTrue();
            seen.Should().Equal("running-completed", "plain", "generic");
        }
        finally
        {
            release.Set();
            if (disposal is not null) await disposal.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Theory]
    [InlineData(ActorExecutionMode.DedicatedThread)]
    [InlineData(ActorExecutionMode.ThreadPool)]
    [InlineData(ActorExecutionMode.SynchronizationContext)]
    public async Task OptIn_Discards_Pending_Inserts_When_Stopped_Before_The_First_Loop_Pass(ActorExecutionMode mode)
    {
        var seen = new List<string>();
        Task? accepted = null;
        using var actor = new ProtocolActor(mode, ContextFor(mode), new ManualTimeSource(), null,
            beforeLoopStart: a =>
            {
                a.Schedule(TimeSpan.Zero, () => seen.Add("due"));
                a.Schedule(TimeSpan.FromDays(1), () => seen.Add("future"));
                var cancelled = a.Schedule(TimeSpan.Zero, () => seen.Add("cancelled"));
                cancelled.Dispose();
                accepted = a.PostAsync(() => seen.Add("accepted"));
                a.Dispose();
            }, shutdownPolicy: ActorShutdownPolicy.DiscardPendingTimers);
        actor.Dispose();
        await accepted!.WaitAsync(TimeSpan.FromSeconds(10));
        seen.Should().Equal("accepted");
        actor.PendingTimerCount.Should().Be(0, "the joined loop released due and future timer entries");
    }

    [Fact]
    public async Task OptIn_Completes_A_Timer_Already_Selected_For_Context_Dispatch_Without_Holding_The_Disposal_Gate()
    {
        using var context = new HeldTimerContext();
        var stopping = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var actor = new ProtocolActor(ActorExecutionMode.SynchronizationContext, context, new ManualTimeSource(), null,
            beforeStopRequested: _ => stopping.TrySetResult(true), shutdownPolicy: ActorShutdownPolicy.DiscardPendingTimers);
        var seen = new List<string>();
        Task? disposal = null;
        Task? accepted = null;
        try
        {
            await actor.PostAsync(() =>
            {
                // All timestamps are zero, so this accepted request is not older than the timer
                // and remains for the next mailbox batch while Send holds the selected callback.
                accepted = actor.PostAsync(() => seen.Add("accepted"));
                actor.Schedule(TimeSpan.Zero, () => seen.Add("selected"));
                actor.Schedule(TimeSpan.Zero, () => seen.Add("pending"));
            }).WaitAsync(TimeSpan.FromSeconds(10));
            await context.TimerSelected.Task.WaitAsync(TimeSpan.FromSeconds(10));
            disposal = Task.Run(actor.Dispose);
            await stopping.Task.WaitAsync(TimeSpan.FromSeconds(10));
            context.Release.Set();
            await disposal.WaitAsync(TimeSpan.FromSeconds(10));
            await accepted!.WaitAsync(TimeSpan.FromSeconds(10));
            seen.Should().Equal("selected", "accepted");
        }
        finally
        {
            context.Release.Set();
            if (disposal is not null) await disposal.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void Undefined_Policies_Are_Rejected(int value)
    {
        var create = () => new ProtocolActor(ActorExecutionMode.DedicatedThread, null, (ActorShutdownPolicy)value);
        create.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("shutdownPolicy");
    }

    private static SynchronizationContext? ContextFor(ActorExecutionMode mode) =>
        mode == ActorExecutionMode.SynchronizationContext ? new InlineContext() : null;

    private sealed class HeldTimerContext : SynchronizationContext, IDisposable
    {
        private int _sends;
        internal TaskCompletionSource<bool> TimerSelected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new();

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (Interlocked.Increment(ref _sends) == 2)
            {
                TimerSelected.TrySetResult(true);
                Release.Wait();
            }
            d(state);
        }

        public void Dispose() => Release.Dispose();
    }

    // The actor must still execute all callbacks through Send, including the shutdown drain.
    private sealed class InlineContext : SynchronizationContext
    {
        public override void Send(SendOrPostCallback d, object? state) => d(state);
    }
}
