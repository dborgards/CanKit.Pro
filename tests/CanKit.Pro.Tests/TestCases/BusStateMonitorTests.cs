using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Pro.Actor;
using CanKit.Pro.Reliability;
using CanKit.Pro.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace CanKit.Pro.Tests.TestCases;

/// <summary>
/// Verifies the L2 bus-state monitor (CanKit.Pro.Reliability, arc42 §5.3 / ADR-11,
/// SRS FR-RAW-051): a self-rearming poll driven through the actor's loop pushes edge-triggered
/// BusState transitions, plus the pure IsTransmitBlocked/IsDegraded helpers.
///
/// The controller state is driven through <see cref="ControllableBus"/>. No CAN adapter exposes a
/// public way to force a real controller into BusOff — reaching one that does would mean either
/// real broken hardware or reflection into an adapter's private statics, and the monitor's
/// contract is about <c>ICanBus.BusState</c>, not about how any one adapter arrives there.
/// </summary>
public class BusStateMonitorTests : IClassFixture<VirtualAdapterFixture>
{
    private static readonly TimeSpan Bounded = TimeSpan.FromSeconds(5);

    private static ControllableBus OpenBus() => ControllableBus.EchoCapable(VirtualAdapterFixture.NewSession("busmonitor"));

    [Fact]
    public void CurrentState_Reflects_The_Bus_State_At_Construction()
    {
        using var bus = OpenBus();
        using var actor = new ProtocolActor();

        // Establish a known, non-default state before the monitor is even constructed.
        bus.BusState = BusState.ErrPassive;

        using var monitor = new BusStateMonitor(bus, actor);

        monitor.CurrentState.Should().Be(BusState.ErrPassive,
            "the monitor baselines CurrentState synchronously from the bus in its constructor");
    }

    [Fact]
    public async Task StateChanged_Is_Not_Raised_While_The_State_Is_Unchanged()
    {
        using var bus = OpenBus();
        using var actor = new ProtocolActor();
        using var monitor = new BusStateMonitor(bus, actor, TimeSpan.FromMilliseconds(20));

        var changes = 0;
        monitor.StateChanged += (_, _) => Interlocked.Increment(ref changes);

        // Let many poll ticks run without ever changing the bus state.
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        Volatile.Read(ref changes).Should().Be(0, "an unchanged state must never raise an edge-triggered event");
    }

    [Fact]
    public async Task StateChanged_Fires_On_A_Transition_Observed_By_The_Poll()
    {
        using var bus = OpenBus();
        using var actor = new ProtocolActor();
        using var monitor = new BusStateMonitor(bus, actor, TimeSpan.FromMilliseconds(20));

        var busOff = new TaskCompletionSource<BusStateChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.StateChanged += (_, e) => { if (e.Current == BusState.BusOff) busOff.TrySetResult(e); };

        bus.BusState = BusState.BusOff;

        (await Task.WhenAny(busOff.Task, Task.Delay(Bounded))).Should().Be(busOff.Task,
            "the self-rearming poll must observe the BusOff transition");
        var args = await busOff.Task;
        args.Previous.Should().Be(BusState.ErrActive);
        args.Current.Should().Be(BusState.BusOff);
        args.Current.IsTransmitBlocked().Should().BeTrue();
        monitor.CurrentState.Should().Be(BusState.BusOff);
    }

    [Fact]
    public async Task StateChanged_Fires_On_Recovery_Back_Down_From_BusOff()
    {
        using var bus = OpenBus();
        using var actor = new ProtocolActor();

        bus.BusState = BusState.BusOff;
        using var monitor = new BusStateMonitor(bus, actor, TimeSpan.FromMilliseconds(20));

        var recovered = new TaskCompletionSource<BusStateChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.StateChanged += (_, e) => { if (e.Current == BusState.ErrActive) recovered.TrySetResult(e); };

        // Recovery (BusOff -> ErrActive) matters too: protocols need to know when to resume.
        bus.BusState = BusState.ErrActive;

        (await Task.WhenAny(recovered.Task, Task.Delay(Bounded))).Should().Be(recovered.Task,
            "recovering transitions must be reported, not only degrading ones");
        var args = await recovered.Task;
        args.Previous.Should().Be(BusState.BusOff);
        args.Current.Should().Be(BusState.ErrActive);
    }

    [Fact]
    public async Task Dispose_Stops_Further_StateChanged_Events_And_Is_Idempotent()
    {
        using var bus = OpenBus();
        using var actor = new ProtocolActor();
        var monitor = new BusStateMonitor(bus, actor, TimeSpan.FromMilliseconds(20));

        var changes = 0;
        monitor.StateChanged += (_, _) => Interlocked.Increment(ref changes);

        monitor.Dispose();
        monitor.Dispose(); // idempotent

        // Change the state only after disposing: with the poll stopped, no event may arrive.
        bus.BusState = BusState.BusOff;
        await Task.Delay(TimeSpan.FromMilliseconds(150)); // several poll intervals

        Volatile.Read(ref changes).Should().Be(0, "a disposed monitor must stop polling and raising events");
    }

    [Theory]
    [InlineData(BusState.None, false)]
    [InlineData(BusState.ErrActive, false)]
    [InlineData(BusState.ErrWarning, false)]
    [InlineData(BusState.ErrPassive, false)]
    [InlineData(BusState.BusOff, true)]
    [InlineData(BusState.Unknown, false)]
    public void IsTransmitBlocked_Is_True_Only_For_BusOff(BusState state, bool expected)
        => state.IsTransmitBlocked().Should().Be(expected);

    [Theory]
    [InlineData(BusState.None, false)]
    [InlineData(BusState.ErrActive, false)]
    [InlineData(BusState.ErrWarning, true)]
    [InlineData(BusState.ErrPassive, true)]
    [InlineData(BusState.BusOff, true)]
    // Unknown means "we could not determine the controller state", and answering a health
    // question with "healthy" on the strength of no information is the one answer that can never
    // be justified -- a caller told "healthy" proceeds as if a bus that may already be off were
    // fine. None stays healthy: it is the "no error condition" reading, not an absence of one.
    [InlineData(BusState.Unknown, true)]
    public void IsDegraded_Is_True_For_Warning_Passive_BusOff_And_Unknown(BusState state, bool expected)
        => state.IsDegraded().Should().Be(expected);

    // --- Error-frame storms (issue #22) ------------------------------------------------------

    [Fact]
    public void An_Error_Frame_Storm_Coalesces_Into_One_Pending_Actor_Post()
    {
        using var bus = OpenBus();
        using var actor = new MailboxActor();
        using var monitor = new BusStateMonitor(bus, actor);

        // Drain the constructor's own post (it arms the first poll) so everything counted below is
        // storm traffic and nothing else.
        actor.RunQueuedWork();
        var beforeStorm = actor.PostCount;

        // What a shorted or unterminated bus does: error frames at line rate. A thousand of them is
        // a few milliseconds of a real storm, and the point is that the number is irrelevant to the
        // mailbox — one un-run recheck already carries everything a level sample can learn.
        const int stormSize = 1000;
        for (var i = 0; i < stormSize; i++)
            bus.RaiseErrorFrame();

        (actor.PostCount - beforeStorm).Should().Be(1,
            "{0} error frames must coalesce into a single recheck post rather than {0} of them: the mailbox is the protocol instance's only queue (FR-RAW-020..022), so flooding it starves the very protocol work a BusOff exists to abort",
            stormSize);
        actor.MailboxDepth.Should().Be(1,
            "the mailbox depth a storm can drive the monitor to is the property under test, and it is bounded at one");
    }

    [Fact]
    public void The_Coalesced_Recheck_Reports_The_Transition_And_Reopens_The_Gate()
    {
        using var bus = OpenBus();
        using var actor = new MailboxActor();
        using var monitor = new BusStateMonitor(bus, actor);
        actor.RunQueuedWork();

        // Handlers run inline on whichever thread drives MailboxActor.RunQueuedWork — here the test
        // thread — so a plain List is the right recorder, and the order it captures is the order a
        // subscriber on the real loop would see.
        var transitions = new List<BusStateChangedEventArgs>();
        monitor.StateChanged += (_, e) => transitions.Add(e);

        bus.BusState = BusState.BusOff;
        for (var i = 0; i < 1000; i++)
            bus.RaiseErrorFrame();

        var afterFirstStorm = actor.PostCount;
        actor.RunQueuedWork().Should().Be(1, "the storm queued exactly one recheck");

        transitions.Should().ContainSingle("coalescing drops redundant samples, never a sampled edge (FR-RAW-051)");
        transitions[0].Previous.Should().Be(BusState.ErrActive);
        transitions[0].Current.Should().Be(BusState.BusOff);
        monitor.CurrentState.Should().Be(BusState.BusOff);

        // The gate is a coalescing window, not a one-shot latch: once the recheck has run, the next
        // storm must be able to post again — otherwise the recovery is only ever seen by a poll.
        bus.BusState = BusState.ErrActive;
        for (var i = 0; i < 1000; i++)
            bus.RaiseErrorFrame();

        (actor.PostCount - afterFirstStorm).Should().Be(1,
            "a storm arriving after the previous recheck has run posts one fresh recheck");
        actor.RunQueuedWork();

        transitions.Should().HaveCount(2);
        transitions[1].Previous.Should().Be(BusState.BusOff,
            "every reported edge chains onto the last reported state, so a subscriber never sees a gap or a re-ordering");
        transitions[1].Current.Should().Be(BusState.ErrActive);
    }

    [Fact]
    public void A_Hint_Arriving_While_The_Recheck_Is_In_Flight_Posts_A_Follow_Up()
    {
        using var bus = OpenBus();
        using var actor = new MailboxActor();
        using var monitor = new BusStateMonitor(bus, actor);
        actor.RunQueuedWork();

        // Raising the second hint from inside StateChanged lands it in the one window that decides
        // whether coalescing can lose a transition: the recheck has been dequeued and is running.
        // If the gate were released only *after* the sample, this hint would be dropped and the
        // state change it announces would have to wait for a poll tick — exactly the latency the
        // hints exist to remove. Released before the sample, it posts a follow-up instead.
        var reentered = false;
        monitor.StateChanged += (_, _) =>
        {
            if (reentered) return;
            reentered = true;
            bus.BusState = BusState.ErrActive;
            bus.RaiseErrorFrame();
        };

        bus.BusState = BusState.BusOff;
        bus.RaiseErrorFrame();

        actor.RunQueuedWork().Should().Be(1, "the first hint queued one recheck");
        actor.MailboxDepth.Should().Be(1,
            "a hint raised while the recheck was in flight was not sampled by it, so it must post a follow-up");

        actor.RunQueuedWork().Should().Be(1);
        monitor.CurrentState.Should().Be(BusState.ErrActive,
            "the follow-up recheck observes the state the in-flight hint was announcing");
    }

    [Fact]
    public void Constructor_Rejects_A_Nonpositive_Poll_Interval()
    {
        using var bus = OpenBus();
        using var actor = new ProtocolActor();

        Action zero = () => new BusStateMonitor(bus, actor, TimeSpan.Zero);
        Action negative = () => new BusStateMonitor(bus, actor, TimeSpan.FromMilliseconds(-1));

        zero.Should().Throw<ArgumentOutOfRangeException>();
        negative.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// An <see cref="IProtocolActor"/> that runs nothing by itself: <see cref="Post"/> only queues,
    /// the test decides when to drain with <see cref="RunQueuedWork"/>, and <see cref="Schedule"/>
    /// hands back a handle whose callback never becomes due.
    ///
    /// <para>
    /// Both halves are load-bearing. Counting posts is the assertion — "how deep did the storm
    /// drive the mailbox" is the quantity issue #22 is about, and it is only answerable while
    /// nothing drains the queue behind the test's back. The inert timer takes the self-rearming
    /// poll out of the picture, so every post counted is hint-driven and no assertion depends on
    /// how fast the machine is: the same test waiting for real poll ticks would be a coin toss on
    /// the Windows runner.
    /// </para>
    /// </summary>
    private sealed class MailboxActor : IProtocolActor
    {
        private readonly ConcurrentQueue<Action> _mailbox = new();
        private int _posts;

        /// <summary>Every <see cref="Post"/> ever made, including work that has already run.</summary>
        public int PostCount => Volatile.Read(ref _posts);

        /// <summary>Work queued and not yet run.</summary>
        public int MailboxDepth => _mailbox.Count;

        public void Post(Action work)
        {
            Interlocked.Increment(ref _posts);
            _mailbox.Enqueue(work);
        }

        /// <summary>
        /// Runs the work queued at entry, in order, on the calling thread, and returns how much ran.
        /// The depth is snapshotted first on purpose: work that posts more work (a recheck that
        /// re-arms, a hint raised from a StateChanged handler) has to leave that follow-up queued
        /// for the test to observe instead of having it swallowed by the same drain.
        /// </summary>
        public int RunQueuedWork()
        {
            var due = _mailbox.Count;
            var ran = 0;
            while (ran < due && _mailbox.TryDequeue(out var work))
            {
                work();
                ran++;
            }
            return ran;
        }

        public IDisposable Schedule(TimeSpan delay, Action callback) => new NeverDue();

        public Task PostAsync(Action work)
            => throw new NotSupportedException("The monitor only uses Post and Schedule; an ask would need a real loop.");

        public Task<T> PostAsync<T>(Func<T> work)
            => throw new NotSupportedException("The monitor only uses Post and Schedule; an ask would need a real loop.");

#pragma warning disable CS0067 // Nothing is run on the caller's behalf here, so this never fires.
        public event EventHandler<Exception>? BackgroundExceptionOccurred;
#pragma warning restore CS0067

        public void Dispose() { }

        private sealed class NeverDue : IDisposable
        {
            public void Dispose() { }
        }
    }
}
