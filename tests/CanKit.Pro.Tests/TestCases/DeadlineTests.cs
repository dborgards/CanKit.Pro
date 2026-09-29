using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.Actor;
using CanKit.Pro.Reliability;
using CanKit.Pro.Tests.Infrastructure;
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
        using var clock = new VirtualClock();
        var actor = clock.NewActor();
        var scheduler = new DeadlineScheduler(actor);
        var fired = false;

        var timeout = TimeSpan.FromMilliseconds(200);
        var deadline = scheduler.Arm(timeout, () => fired = true);
        // Arm before you advance: prove the deadline really was scheduled for the configured
        // timeout before Complete races ahead of it.
        await clock.WaitUntilTimerArmedAsync(actor, timeout, Bounded);

        deadline.Complete().Should().BeTrue("Complete wins the race well before the deadline would expire");
        deadline.IsCompleted.Should().BeTrue();

        // A second Complete and a following Dispose are idempotent no-ops that must not change the
        // already-decided terminal outcome.
        deadline.Complete().Should().BeFalse("a second Complete cannot win an already-resolved deadline");
        deadline.Dispose();
        deadline.IsCompleted.Should().BeTrue();
        deadline.IsCancelled.Should().BeFalse();

        // Move the clock past the original timer's due point and let the loop settle; onExpired
        // must never fire for a completed deadline.
        await clock.AdvanceAsync(timeout + TimeSpan.FromMilliseconds(100));
        await clock.SettleAsync();
        fired.Should().BeFalse();
    }

    [Fact]
    public async Task Cancel_Before_Expiry_Prevents_OnExpired()
    {
        using var clock = new VirtualClock();
        var actor = clock.NewActor();
        var scheduler = new DeadlineScheduler(actor);
        var fired = false;

        var timeout = TimeSpan.FromMilliseconds(200);
        var deadline = scheduler.Arm(timeout, () => fired = true);
        await clock.WaitUntilTimerArmedAsync(actor, timeout, Bounded);

        deadline.Dispose(); // Dispose == Cancel
        deadline.IsCancelled.Should().BeTrue();

        await clock.AdvanceAsync(timeout + TimeSpan.FromMilliseconds(100));
        await clock.SettleAsync();
        fired.Should().BeFalse();
    }

    [Fact]
    public async Task Rearm_Before_Original_Expiry_Extends_The_Deadline()
    {
        // Previously #130 kept this test on the wall clock: a saturated net48 thread pool injects
        // threads only one or two per second, so Task.Delay(850) could still be pending when the
        // rearmed 2000 ms deadline fired, and WhenAny would report that fire and blame the
        // superseded timer instead. A VirtualClock sidesteps the thread pool race entirely -- the
        // deadline's due points are moments this test itself schedules, so there is nothing left
        // for the pool to starve. The generation guard itself is covered separately by
        // Rearm_Leaves_An_Already_Dispatched_Callback_Unable_To_Expire.
        using var clock = new VirtualClock();
        var actor = clock.NewActor();
        var scheduler = new DeadlineScheduler(actor);
        var fired = false;

        var original = TimeSpan.FromMilliseconds(600);
        var extended = TimeSpan.FromMilliseconds(2000);
        using var deadline = scheduler.Arm(original, () => fired = true);
        await clock.WaitUntilTimerArmedAsync(actor, original, Bounded);

        deadline.Rearm(extended).Should().BeTrue("re-arming a still-pending deadline succeeds");
        // The rearmed timer must now be armed for the extended interval, not the remainder of the
        // original one -- this is the generation guard's counterpart on the happy path.
        await clock.WaitUntilTimerArmedAsync(actor, extended, Bounded);

        // Bracket from both sides. First: past the original due point, well short of the
        // rearmed one.
        await clock.AdvanceAsync(original);
        await clock.SettleAsync();
        fired.Should().BeFalse("the original timeout must have been superseded by Rearm");
        deadline.IsExpired.Should().BeFalse();

        // One tick short of the rearmed due point: still must not have fired.
        var epsilon = TimeSpan.FromMilliseconds(1);
        await clock.AdvanceAsync(extended - original - epsilon);
        await clock.SettleAsync();
        fired.Should().BeFalse("the rearmed deadline has not reached its own due point yet");
        deadline.IsExpired.Should().BeFalse();

        // The last tick reaches it.
        await clock.AdvanceAsync(epsilon);
        await clock.SettleAsync();
        fired.Should().BeTrue("the re-armed timeout must still fire at its new deadline");
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
        using var clock = new VirtualClock();
        var actor = clock.NewActor();
        var scheduler = new DeadlineScheduler(actor);
        var backgroundFaulted = false;
        actor.BackgroundExceptionOccurred += (_, _) => backgroundFaulted = true;
        var fired = false;

        var timeout = TimeSpan.FromMilliseconds(200);
        using var deadline = scheduler.Arm(timeout, () => fired = true);
        await clock.WaitUntilTimerArmedAsync(actor, timeout, Bounded);

        // The actor's FinalDrain discards not-yet-due Schedule callbacks, so a deadline that was
        // still Pending simply never resolves (documented best-effort behavior). actor.Dispose()
        // joins the dedicated thread and only returns once FinalDrain has already run, so the
        // outcome is settled the moment this call returns -- no wait, virtual or otherwise, is
        // needed to observe it.
        actor.Dispose();

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

        public Task PostAsync(Action work, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<T> PostAsync<T>(Func<T> work, CancellationToken cancellationToken = default) => throw new NotSupportedException();

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
