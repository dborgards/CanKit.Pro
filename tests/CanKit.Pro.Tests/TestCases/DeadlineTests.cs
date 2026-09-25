using System;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Pro.Actor;
using CanKit.Pro.Reliability;
using CanKit.Pro.Tests.Infrastructure;
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

        // The windows are deliberately far apart rather than merely different (#130). They are
        // also waited out on this thread, not with Task.Delay. Task.Delay completes on the
        // thread pool, and the .NET Framework pool only injects one or two threads a second once
        // it is saturated. On the net48 CI leg a Delay(50) can therefore land after the original
        // 600 ms deadline, and a Delay(850) can still be pending when the rearmed 2000 ms
        // deadline fires on the actor's dedicated thread. WhenAny then returns the fired task
        // and the assertion blames the superseded timer for a callback that was the new
        // deadline, on time. Sleeping and waiting on the callback's event block in the kernel.
        // They keep the same windows and do not depend on that pool. The generation property is pinned
        // without a wall clock by Rearm_Supersedes_The_Original_Timer_On_A_Manual_Clock.
        using var fired = new ManualResetEventSlim(false);
        using var deadline = scheduler.Arm(TimeSpan.FromMilliseconds(600), () => fired.Set());

        // Re-arm to a much longer window, well before the original 600 ms would elapse.
        Thread.Sleep(TimeSpan.FromMilliseconds(50));
        deadline.Rearm(TimeSpan.FromMilliseconds(2000)).Should().BeTrue("re-arming a still-pending deadline succeeds");

        // The ORIGINAL timer (600 ms from arm) must NOT fire -- Rearm superseded it, and the stale
        // pre-Rearm timer must be generation-guarded out rather than double-firing. Waiting 850 ms
        // takes us comfortably past when it would have, and still more than a second short of the
        // rearmed deadline. Wait is on the event the callback sets, so a stalled thread pool
        // cannot stretch this window out to the rearmed deadline.
        fired.Wait(TimeSpan.FromMilliseconds(850)).Should().BeFalse(
            "the original timeout must have been superseded by Rearm");
        deadline.IsExpired.Should().BeFalse();

        // ...but the re-armed timer (2000 ms from Rearm) eventually does fire.
        fired.Wait(Bounded).Should().BeTrue(
            "the re-armed timeout must still fire at its new deadline");
        deadline.IsExpired.Should().BeTrue();
    }

    [Fact]
    public async Task Rearm_Supersedes_The_Original_Timer_On_A_Manual_Clock()
    {
        // The wall-clock test above can only see "did a callback happen inside this window".
        // Which timer produced it is an inference. Here the clock does not move unless the test
        // says so, so the original due point and the rearmed one are not two guesses about a
        // loaded runner: the superseded entry is already past due, the replacement is not, and
        // the callback must stay quiet until the replacement's own due point.
        var clock = new ManualTimeSource();
        using var actor = new ProtocolActor(ActorExecutionMode.DedicatedThread, null, clock, null);
        var scheduler = new DeadlineScheduler(actor);
        var fired = 0;

        using var deadline = scheduler.Arm(TimeSpan.FromMilliseconds(600), () => Volatile.Write(ref fired, 1));
        // The arm is only real once the loop has inserted it. A Rearm that cancels an entry the
        // loop has not yet seen is a different (and easier) path.
        await actor.PostAsync(() => 0);
        await actor.PostAsync(() => 0);

        clock.Advance(TimeSpan.FromMilliseconds(50));
        deadline.Rearm(TimeSpan.FromMilliseconds(2000)).Should().BeTrue();
        await actor.PostAsync(() => 0);
        await actor.PostAsync(() => 0);

        // now = 850 ms. The original deadline was 600 ms and is past due; the rearmed one is
        // 2000 ms from the rearm at 50 ms, so it is not due until 2050 ms.
        clock.Advance(TimeSpan.FromMilliseconds(800));
        await actor.PostAsync(() => 0);
        await actor.PostAsync(() => 0);

        Volatile.Read(ref fired).Should().Be(0,
            "a timer Rearm already superseded must not fire once its original due point has passed");
        deadline.IsExpired.Should().BeFalse();

        // now = 2150 ms, past the rearmed due point.
        clock.Advance(TimeSpan.FromMilliseconds(1300));
        await actor.PostAsync(() => 0);
        await actor.PostAsync(() => 0);

        Volatile.Read(ref fired).Should().Be(1,
            "the rearmed deadline must still fire when its own due point is reached");
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
}
