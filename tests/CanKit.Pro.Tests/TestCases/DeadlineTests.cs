using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Pro.Actor;
using CanKit.Pro.Reliability;
using FluentAssertions;
using Xunit;

namespace CanKit.Pro.Tests.TestCases;

/// <summary>
/// Verifies the L2 deadline/timeout primitive (CanKit.Pro.Reliability, arc42 §5.3 / ADR-11,
/// SRS FR-RAW-050) built on top of CanKit.Pro.Actor: an armed deadline's expiry is actually
/// scheduled and checked on the actor's loop (regression against Review §1.1 Punkt 10
/// "Deadlines werden gepflegt, aber nie geprüft"), with a single race-free Pending → terminal
/// resolution.
/// </summary>
public class DeadlineTests
{
    private static readonly TimeSpan Bounded = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Deadline_Fires_OnExpired_After_The_Timeout_Elapses()
    {
        using var actor = new ProtocolActor();
        var scheduler = new DeadlineScheduler(actor);
        var fired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var deadline = scheduler.Arm(TimeSpan.FromMilliseconds(30), () => fired.TrySetResult(true));

        (await Task.WhenAny(fired.Task, Task.Delay(Bounded))).Should().Be(fired.Task,
            "an armed deadline must actually be scheduled and fire, not sit as never-checked data");
        deadline.IsExpired.Should().BeTrue();
    }

    [Fact]
    public async Task Complete_Before_Expiry_Prevents_OnExpired_And_Is_Idempotent()
    {
        using var actor = new ProtocolActor();
        var scheduler = new DeadlineScheduler(actor);
        var fired = false;

        var deadline = scheduler.Arm(TimeSpan.FromMilliseconds(200), () => fired = true);

        deadline.Complete().Should().BeTrue("Complete wins the race well before the deadline would expire");
        deadline.IsCompleted.Should().BeTrue();

        // A second Complete and a following Dispose are idempotent no-ops that must not change the
        // already-decided terminal outcome.
        deadline.Complete().Should().BeFalse("a second Complete cannot win an already-resolved deadline");
        deadline.Dispose();
        deadline.IsCompleted.Should().BeTrue();
        deadline.IsCancelled.Should().BeFalse();

        // Let the original timer's due point pass and round-trip through the loop; onExpired must
        // never fire for a completed deadline.
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        await actor.PostAsync(() => 0);
        fired.Should().BeFalse();
    }

    [Fact]
    public async Task Cancel_Before_Expiry_Prevents_OnExpired()
    {
        using var actor = new ProtocolActor();
        var scheduler = new DeadlineScheduler(actor);
        var fired = false;

        var deadline = scheduler.Arm(TimeSpan.FromMilliseconds(200), () => fired = true);
        deadline.Dispose(); // Dispose == Cancel
        deadline.IsCancelled.Should().BeTrue();

        await Task.Delay(TimeSpan.FromMilliseconds(300));
        await actor.PostAsync(() => 0);
        fired.Should().BeFalse();
    }

    [Fact]
    public void Rearm_Before_Original_Expiry_Extends_The_Deadline()
    {
        using var actor = new ProtocolActor();
        var scheduler = new DeadlineScheduler(actor);

        // #130. Task.Delay completes on the thread pool, and a saturated net48 pool injects
        // threads only one or two per second. Delay(850) can still be pending when the rearmed
        // 2000 ms deadline fires on the actor's dedicated thread, so WhenAny reports that fire
        // and the assertion blames the superseded timer. These waits block the calling thread.
        // The windows stay 50 / 850 / 2000 ms. The generation guard itself is
        // Rearm_Leaves_An_Already_Dispatched_Callback_Unable_To_Expire.
        using var fired = new ManualResetEventSlim(false);
        using var deadline = scheduler.Arm(TimeSpan.FromMilliseconds(600), () => fired.Set());

        Thread.Sleep(TimeSpan.FromMilliseconds(50));
        deadline.Rearm(TimeSpan.FromMilliseconds(2000)).Should().BeTrue("re-arming a still-pending deadline succeeds");

        // Past the original 600 ms, and still more than a second short of the rearmed deadline.
        fired.Wait(TimeSpan.FromMilliseconds(850)).Should().BeFalse(
            "the original timeout must have been superseded by Rearm");
        deadline.IsExpired.Should().BeFalse();

        fired.Wait(Bounded).Should().BeTrue(
            "the re-armed timeout must still fire at its new deadline");
        deadline.IsExpired.Should().BeTrue();
    }

    [Fact]
    public void Rearm_Leaves_An_Already_Dispatched_Callback_Unable_To_Expire()
    {
        // Schedule's dispose is best-effort: a callback the loop has already taken off its timer
        // list still runs. Cancelling the handle is not what stops it — Fire's generation check
        // is. This actor keeps that callback reachable after Dispose, which is the in-flight case
        // ProtocolActor.FireDueTimers never enters once TryCancel has won.
        var actor = new HoldingActor();
        var scheduler = new DeadlineScheduler(actor);
        var fired = 0;

        using var deadline = scheduler.Arm(TimeSpan.FromMilliseconds(600), () => fired++);
        deadline.Rearm(TimeSpan.FromMilliseconds(2000)).Should().BeTrue();

        var stale = actor.Armed[0];
        var replacement = actor.Armed[1];
        stale.WasDisposed.Should().BeTrue("Rearm disposes the handle it supersedes");
        replacement.WasDisposed.Should().BeFalse();

        stale.Deliver();
        fired.Should().Be(0, "a callback captured before Rearm must lose the generation check");
        deadline.IsExpired.Should().BeFalse();

        replacement.Deliver();
        fired.Should().Be(1, "the callback captured by Rearm is the one that expires the deadline");
        deadline.IsExpired.Should().BeTrue();
    }

    [Fact]
    public async Task Rearm_After_The_Deadline_Already_Resolved_Returns_False()
    {
        using var actor = new ProtocolActor();
        var scheduler = new DeadlineScheduler(actor);

        var completed = scheduler.Arm(TimeSpan.FromMilliseconds(500), () => { });
        completed.Complete().Should().BeTrue();
        completed.Rearm(TimeSpan.FromMilliseconds(500)).Should().BeFalse("a completed deadline cannot be re-armed");

        var cancelled = scheduler.Arm(TimeSpan.FromMilliseconds(500), () => { });
        cancelled.Dispose();
        cancelled.Rearm(TimeSpan.FromMilliseconds(500)).Should().BeFalse("a cancelled deadline cannot be re-armed");

        var expiredFired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var expired = scheduler.Arm(TimeSpan.FromMilliseconds(30), () => expiredFired.TrySetResult(true));
        (await Task.WhenAny(expiredFired.Task, Task.Delay(Bounded))).Should().Be(expiredFired.Task);
        expired.Rearm(TimeSpan.FromMilliseconds(100)).Should().BeFalse("an expired deadline cannot be re-armed");
    }

    [Fact]
    public void Rearm_After_The_Actor_Is_Disposed_Marks_The_Deadline_Cancelled_Instead_Of_Orphaning_It()
    {
        var actor = new ProtocolActor();
        var scheduler = new DeadlineScheduler(actor);

        using var deadline = scheduler.Arm(TimeSpan.FromSeconds(30), () => { });
        actor.Dispose();

        // Rearm disposes the old (already-inert) timer and tries to arm a new one on the now-dead
        // actor; Schedule throws ObjectDisposedException. The deadline must not be left stuck at
        // Pending forever with no active timer -- it has no way to resolve itself anymore, so it is
        // forced to the Cancelled terminal state instead of becoming an unobservable zombie.
        Action rearm = () => deadline.Rearm(TimeSpan.FromMilliseconds(50));

        rearm.Should().Throw<ObjectDisposedException>();
        deadline.IsCancelled.Should().BeTrue(
            "a deadline whose Rearm failed because its actor is gone can never resolve on its own and must report a terminal state");
        deadline.IsExpired.Should().BeFalse();
        deadline.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task Exception_From_OnExpired_Surfaces_Via_The_Actor_Background_Exception_Channel()
    {
        using var actor = new ProtocolActor();
        var scheduler = new DeadlineScheduler(actor);
        Exception? observed = null;
        using var gate = new SemaphoreSlim(0);
        actor.BackgroundExceptionOccurred += (_, ex) => { observed = ex; gate.Release(); };

        using var deadline = scheduler.Arm(
            TimeSpan.FromMilliseconds(30),
            () => throw new InvalidOperationException("deadline boom"));

        (await gate.WaitAsync(Bounded)).Should().BeTrue(
            "onExpired throwing must surface via the actor's BackgroundExceptionOccurred (FR-RAW-023), not as an unobserved exception");
        observed.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("deadline boom");
    }

    [Fact]
    public async Task Disposing_The_Owning_Actor_While_Pending_Never_Fires_The_Deadline_And_Escapes_No_Exception()
    {
        var actor = new ProtocolActor();
        var scheduler = new DeadlineScheduler(actor);
        var backgroundFaulted = false;
        actor.BackgroundExceptionOccurred += (_, _) => backgroundFaulted = true;
        var fired = false;

        using var deadline = scheduler.Arm(TimeSpan.FromMilliseconds(200), () => fired = true);

        // The actor's FinalDrain discards not-yet-due Schedule callbacks, so a deadline that was
        // still Pending simply never resolves (documented best-effort behavior).
        actor.Dispose();

        await Task.Delay(TimeSpan.FromMilliseconds(300));
        fired.Should().BeFalse("a pending deadline whose actor is disposed must never fire");
        deadline.IsExpired.Should().BeFalse();
        backgroundFaulted.Should().BeFalse("disposing the actor under a pending deadline must not raise any exception");
    }

    [Fact]
    public void Arm_Validates_Its_Arguments()
    {
        using var actor = new ProtocolActor();
        var scheduler = new DeadlineScheduler(actor);

        Action nullCallback = () => scheduler.Arm(TimeSpan.FromMilliseconds(10), null!);
        Action negativeTimeout = () => scheduler.Arm(TimeSpan.FromMilliseconds(-1), () => { });

        nullCallback.Should().Throw<ArgumentNullException>();
        negativeTimeout.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// Records every <see cref="IProtocolActor.Schedule"/> callback and still delivers it after
    /// the handle is disposed. That is the actor's documented "already in flight" case: disposal
    /// only stops a callback the loop has not taken yet.
    /// </summary>
    private sealed class HoldingActor : IProtocolActor
    {
        private readonly List<HeldCallback> _armed = new();

        public IReadOnlyList<HeldCallback> Armed => _armed;

        public IDisposable Schedule(TimeSpan delay, Action callback)
        {
            var held = new HeldCallback(callback);
            _armed.Add(held);
            return held;
        }

        public void Post(Action work) => throw new NotSupportedException();

        public Task PostAsync(Action work) => throw new NotSupportedException();

        public Task<T> PostAsync<T>(Func<T> work) => throw new NotSupportedException();

#pragma warning disable CS0067
        public event EventHandler<Exception>? BackgroundExceptionOccurred;
#pragma warning restore CS0067

        public void Dispose() { }

        public sealed class HeldCallback : IDisposable
        {
            private readonly Action _callback;

            public HeldCallback(Action callback) => _callback = callback;

            public bool WasDisposed { get; private set; }

            public void Dispose() => WasDisposed = true;

            public void Deliver() => _callback();
        }
    }
}
