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
/// Verifies the actor's timer machinery itself (CanKit.Pro.Actor, SRS FR-RAW-022/024): which clock
/// deadlines are measured against, that the mailbox cannot starve them, that cancelled entries are
/// reclaimed rather than accumulated, and that a shutdown which cannot be joined says so.
/// </summary>
/// <remarks>
/// Every protocol timeout in the stack above (ISO-TP N_Bs/N_Cr, J1939 TP, CANopen SDO and
/// heartbeat, UDS P2/P2*) is one <see cref="IProtocolActor.Schedule"/> call, so these are the
/// properties that decide whether any of those timings mean anything.
/// </remarks>
public class ProtocolActorTimerTests
{
    private static readonly TimeSpan Bounded = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Timer_Due_Times_Ignore_The_Wall_Clock_And_Follow_The_Monotonic_Source()
    {
        // Regression (#20): due-times used to be `DateTime.UtcNow + delay`, i.e. wall-clock
        // absolute points. A -1 h NTP step then made every armed deadline in the stack fire an
        // hour late, and +1 h made all of them fire at once.
        //
        // The demonstration works in both directions with one frozen monotonic source:
        //
        //   (1) Real wall-clock time advances by many multiples of the delay while the monotonic
        //       source stands still, and the timer must NOT fire. Wall time advancing by 10x the
        //       delay is arithmetically the same event as a +NTP step of that size -- both are
        //       "UtcNow is now far past the due point". Measured against the wall clock the
        //       callback fires here; measured against elapsed monotonic ticks it cannot.
        //
        //   (2) The monotonic source then moves past the due point without any wall time passing,
        //       and the timer must fire immediately -- which is what a -NTP step looks like from
        //       the other side: the wall clock is behind, and the deadline is due anyway.
        var clock = new ManualTimeSource();
        using var actor = new ProtocolActor(ActorExecutionMode.DedicatedThread, null, clock, null);
        var fired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var handle = actor.Schedule(TimeSpan.FromMilliseconds(50), () => fired.TrySetResult(true));

        var wallClockBefore = DateTime.UtcNow;
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        var wallClockAdvance = DateTime.UtcNow - wallClockBefore;

        // Round-trip through the loop so we know it has been awake and past its timer check while
        // all that wall-clock time was elapsing, rather than merely asserting on a sleeping actor.
        await actor.PostAsync(() => 0);

        wallClockAdvance.Should().BeGreaterThan(TimeSpan.FromMilliseconds(200),
            "the premise of the test is that the wall clock really did run far past the 50 ms deadline");
        fired.Task.IsCompleted.Should().BeFalse(
            "the wall clock moving past the due point -- whether by elapsing or by an NTP step -- must not fire a deadline");

        clock.Advance(TimeSpan.FromMilliseconds(50));
        actor.Post(() => { }); // wake the loop; it is asleep on a deadline that just became due

        (await Task.WhenAny(fired.Task, Task.Delay(Bounded))).Should().Be(fired.Task,
            "the deadline is due once the monotonic source says 50 ms of it elapsed, independently of the wall clock");
    }

    [Fact]
    public async Task A_Continuously_Refilled_Mailbox_Does_Not_Starve_Timers()
    {
        // Regression (#21): the loop used to drain the mailbox `while (TryDequeue(...))` and only
        // then look at timers. Every RX reader posts one work item per frame, so on a saturated
        // bus (~8000 frames/s) the queue never empties and no deadline fires at all.
        //
        // The load generator is a work item that re-posts itself. That is a deterministic stand-in
        // for the saturated-bus producer -- the mailbox is provably never empty at the moment the
        // drain looks, on any runner, with no dependence on whether a producer thread can outrun a
        // consumer -- and it is bounded in memory, unlike a real flood.
        using var actor = new ProtocolActor();
        var stop = 0;
        var fired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var handle = actor.Schedule(TimeSpan.FromMilliseconds(100), () => fired.TrySetResult(true));

        void Refill()
        {
            if (Volatile.Read(ref stop) == 0)
                actor.Post(Refill);
        }

        actor.Post(Refill);

        var outcome = await Task.WhenAny(fired.Task, Task.Delay(Bounded));

        // Stop the flood before asserting, so a failing run tears down in milliseconds instead of
        // leaving the loop spinning through Dispose.
        Volatile.Write(ref stop, 1);
        await actor.PostAsync(() => 0);

        outcome.Should().Be(fired.Task,
            "an armed deadline must still fire while the mailbox is permanently non-empty");
    }

    [Fact]
    public async Task A_Sub_Millisecond_Remaining_Delay_Waits_Instead_Of_Busy_Spinning()
    {
        // Regression (#54): the remaining wait was computed with `(int)ms`, so anything under a
        // millisecond truncated to a 0 ms wait. The loop then spun on the semaphore -- burning a
        // core for up to 1 ms before *every* timer in the process -- without firing any earlier,
        // since a timer that is not due is not due. Rounding up costs one millisecond and nothing
        // else.
        //
        // Freezing the clock at a sub-millisecond remainder makes that spin observable and
        // bounded: the number of times the loop asks for the time is the loop's iteration count.
        var clock = new ManualTimeSource();
        using var actor = new ProtocolActor(ActorExecutionMode.DedicatedThread, null, clock, null);

        using var handle = actor.Schedule(TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond / 2), () => { });

        // Let the actor settle into its wait before measuring, so the arming round-trip is not
        // counted.
        await actor.PostAsync(() => 0);
        clock.ResetReadCount();
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        // Sleeping 1 ms at a time gives ~300 iterations and a handful of reads each; a 0 ms wait
        // gives as many iterations as the CPU can manage, which is orders of magnitude more. The
        // threshold sits between the two by a wide margin so a slow or fast runner cannot flip it.
        clock.ReadCount.Should().BeLessThan(20_000,
            "a sub-millisecond remainder must round up to a real 1 ms wait, not truncate to a busy spin");
    }

    [Fact]
    public async Task Repeated_Rearms_Do_Not_Leave_Cancelled_Entries_In_The_Timer_List()
    {
        // Regression (#54): a cancelled entry was only dropped once it reached the head of the
        // sorted list, i.e. no sooner than its own original due time. A protocol that refreshes a
        // long deadline often (ISO-TP N_Cr per consecutive frame, a CANopen heartbeat consumer)
        // while some earlier timer stays pending therefore carried one corpse per re-arm, each of
        // which the O(n) sorted insert then had to walk past.
        //
        // `blocker` is what makes the leak visible: it is due *before* every corpse, so the cheap
        // head-trim can never reach them and only a real sweep can.
        using var actor = new ProtocolActor();
        var scheduler = new DeadlineScheduler(actor);

        using var blocker = actor.Schedule(TimeSpan.FromSeconds(20), () => { });
        using var deadline = scheduler.Arm(TimeSpan.FromSeconds(60), () => { });

        const int rearms = 200;
        for (var i = 0; i < rearms; i++)
        {
            deadline.Rearm(TimeSpan.FromSeconds(60)).Should().BeTrue();

            // Two round-trips, not one: a work item is drained before the pending timer inserts of
            // the same iteration, so a single round-trip only proves the *previous* insert landed.
            // Both are needed for every re-arm to have actually reached the list before the next
            // one cancels it -- otherwise this would measure the queue, not the list.
            await actor.PostAsync(() => 0);
            await actor.PostAsync(() => 0);
        }

        // Read on the loop thread, the only thread allowed to look at the timer list.
        var pending = await actor.PostAsync(() => actor.PendingTimerCount);

        pending.Should().BeLessThan(32,
            $"{rearms} re-arms must not leave {rearms} cancelled entries behind for the sorted insert to walk past");
    }

    [Fact]
    public void Dispose_Reports_A_TimeoutException_When_The_Loop_Cannot_Be_Joined()
    {
        // Regression (#54): Dispose waited five seconds for the loop and then returned silently,
        // so a caller could not tell a clean shutdown from an abandoned loop still running a
        // callback -- and still mutating state the caller now believes it owns exclusively.
        // The shutdown timeout is shortened through the internal constructor purely so this test
        // does not have to spend five seconds proving it.
        var actor = new ProtocolActor(ActorExecutionMode.DedicatedThread, null, null, TimeSpan.FromMilliseconds(100));
        using var callbackEntered = new ManualResetEventSlim(false);
        using var releaseCallback = new ManualResetEventSlim(false);
        Exception? observed = null;
        actor.BackgroundExceptionOccurred += (_, ex) => observed ??= ex;

        actor.Post(() =>
        {
            callbackEntered.Set();
            releaseCallback.Wait(TimeSpan.FromSeconds(30));
        });

        callbackEntered.Wait(Bounded).Should().BeTrue();
        actor.Dispose();

        observed.Should().BeOfType<TimeoutException>(
            "a Dispose that gave up on the loop must say so through the one defined background channel (FR-RAW-023)");

        // Let the stuck callback finish so the loop tears itself down instead of leaking a thread.
        releaseCallback.Set();
    }

    [Fact]
    public async Task A_Clean_Dispose_Reports_Nothing()
    {
        // Guards the test above from passing for the wrong reason: the timeout must be reported
        // only when the loop genuinely could not be joined, never on every Dispose.
        var actor = new ProtocolActor(ActorExecutionMode.DedicatedThread, null, null, TimeSpan.FromMilliseconds(500));
        Exception? observed = null;
        actor.BackgroundExceptionOccurred += (_, ex) => observed ??= ex;

        await actor.PostAsync(() => 0);
        actor.Dispose();

        observed.Should().BeNull();
    }
}
