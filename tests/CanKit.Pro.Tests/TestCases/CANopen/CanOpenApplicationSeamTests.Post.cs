using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Application;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

// ICanOpenApplicationHost.Post: the one way an application's own API, called from any thread or
// from inside the application's callbacks, reaches the actor loop.
public partial class CanOpenApplicationSeamTests
{
    [Fact]
    public void Posts_From_Another_Thread_Run_In_Order_As_Callbacks_Of_The_Application()
    {
        using var bus = Open(NewSession(), 1);
        var app = Entries();
        using var node = OpenNode(bus, With(app), new ManualTimeSource());
        Settle(node);
        var ran = new List<int>();
        var onActor = new List<bool>();

        for (int i = 0; i < 50; i++)
        {
            int n = i;
            app.Host!.Post(() =>
            {
                ran.Add(n);
                onActor.Add(app.Host.IsOnActor);
                app.Host.ObjectDictionary.WriteUnsigned(0x6000, 0x01, (uint)n);
            });
        }
        Settle(node);
        Settle(node);

        ran.Should().Equal(Enumerable.Range(0, 50), "the actions one thread queues run in the order it queued them");
        onActor.Should().AllSatisfy(b => b.Should().BeTrue("a posted action runs on the actor loop"));
        node.ObjectDictionary.ReadUnsigned(0x6000, 0x01).Should().Be(49u);
        Written(app).Should().BeEmpty("a posted action is a callback of the application: its writes are its own");
    }

    [Fact]
    public async Task IsOnActor_Identifies_The_Node_Whose_Actor_Is_Running()
    {
        using var firstBus = Open(NewSession(), 1);
        using var secondBus = Open(NewSession(), 1);
        var first = Entries();
        var second = Entries();
        using var firstNode = OpenNode(firstBus, With(first), new ManualTimeSource());
        using var secondNode = OpenNode(secondBus, With(second), new ManualTimeSource());

        first.Host!.IsOnActor.Should().BeFalse();
        await secondNode.PostToActorAsync(() =>
        {
            second.Host!.IsOnActor.Should().BeTrue();
            first.Host.IsOnActor.Should().BeFalse();
        });
    }

    [Fact]
    public async Task A_Post_Inside_The_Applications_Own_Callback_Runs_Before_It_Returns()
    {
        using var bus = Open(NewSession(), 1);
        var app = Entries();
        var order = new List<string>();
        app.WhenWritten = _ =>
        {
            order.Add("before");
            app.Host!.Post(() => order.Add("posted"));
            order.Add("after");
        };
        using var node = OpenNode(bus, With(app), new ManualTimeSource());

        node.ObjectDictionary.WriteUnsigned(0x6000, 0x01, 7);
        await WaitUntilAsync(() => Written(app).Any(), "the claimant to be told");
        Settle(node);

        order.Should().Equal("before", "posted", "after");
    }

    [Fact]
    public async Task A_Throw_From_An_Inline_Post_Reaches_The_Caller()
    {
        using var bus = Open(NewSession(), 1);
        var app = Entries();
        Exception? caught = null;
        app.WhenWritten = _ =>
        {
            try
            {
                app.Host!.Post(() => throw new InvalidOperationException("inline failed"));
            }
            catch (InvalidOperationException ex)
            {
                caught = ex;
            }
        };
        using var node = OpenNode(bus, With(app), new ManualTimeSource());
        var reported = new List<Exception>();
        node.BackgroundExceptionOccurred += (_, ex) => { lock (reported) reported.Add(ex); };

        node.ObjectDictionary.WriteUnsigned(0x6000, 0x01, 7);
        await WaitUntilAsync(() => Written(app).Any(), "the claimant to be told");
        Settle(node);

        caught.Should().NotBeNull().And.Subject.As<Exception>().Message.Should().Be("inline failed");
        lock (reported) reported.Should().BeEmpty("the caller caught it");
    }

    [Fact]
    public async Task A_Post_From_ValidateWrite_Inside_The_Applications_Own_Callback_Is_Queued()
    {
        using var bus = Open(NewSession(), 1);
        var app = Entries();
        var order = new List<string>();
        app.Validate = (index, subindex, _) =>
        {
            if (index == 0x6000 && subindex == 0x02)
            {
                app.Host!.Post(() => order.Add("posted"));
                order.Add("validated");
            }
            return OdWriteDecision.Accept;
        };
        app.WhenWritten = _ =>
        {
            order.Add("before");
            // The application's own write, validated on the actor under the write gate.
            app.Host!.ObjectDictionary.WriteUnsigned(0x6000, 0x02, 1);
            order.Add("after");
        };
        using var node = OpenNode(bus, With(app), new ManualTimeSource());

        node.ObjectDictionary.WriteUnsigned(0x6000, 0x01, 7);
        await WaitUntilAsync(() => Written(app).Any(), "the claimant to be told");
        Settle(node);

        order.Should().Equal(new[] { "before", "validated", "after", "posted" },
            "ValidateWrite is not a callback the action may run inline in, even nested in one that is");
    }

    [Fact]
    public async Task A_Post_Inside_Another_Applications_Callback_Is_Queued()
    {
        using var bus = Open(NewSession(), 1);
        var first = Entries("first");
        var second = new RecordingApplication("second", new CanOpenIndexRange(0x6800, 0x6FFF))
        {
            OnAttach = host => host.ObjectDictionary.AddU8(0x6800, 0x01, 0),
        };
        var order = new List<string>();
        first.WhenWritten = _ =>
        {
            order.Add("before");
            second.Host!.Post(() =>
            {
                order.Add("posted");
                // The second application's own write: not reported back to it.
                second.Host.ObjectDictionary.WriteUnsigned(0x6800, 0x01, 3);
            });
            order.Add("after");
        };
        using var node = OpenNode(bus, With(first, second), new ManualTimeSource());

        node.ObjectDictionary.WriteUnsigned(0x6000, 0x01, 7);
        await WaitUntilAsync(() => Written(first).Any(), "the first application to be told");
        Settle(node);

        order.Should().Equal(new[] { "before", "after", "posted" },
            "inside another application's callback the action is not this application's to run inline");
        node.ObjectDictionary.ReadUnsigned(0x6800, 0x01).Should().Be(3u);
        Written(second).Should().BeEmpty("the queued action ran as a callback of the application that posted it");
    }

    [Fact]
    public async Task A_Post_On_The_Actor_Outside_A_Callback_Is_Queued()
    {
        using var bus = Open(NewSession(), 1);
        var app = Entries();
        using var node = OpenNode(bus, With(app), new ManualTimeSource());
        var order = new List<string>();

        await node.PostToActorAsync(() =>
        {
            order.Add("before");
            app.Host!.Post(() => order.Add("posted"));
            order.Add("after");
        });
        Settle(node);

        order.Should().Equal("before", "after", "posted");
    }

    [Fact]
    public void A_Post_From_Attach_Runs_Once_The_Node_Has_Opened()
    {
        using var bus = Open(NewSession(), 1);
        NmtState? seen = null;
        var app = Entries();
        var declare = app.OnAttach!;
        app.OnAttach = host =>
        {
            declare(host);
            host.Post(() =>
            {
                seen = host.State;
                host.ObjectDictionary.WriteUnsigned(0x6000, 0x01, 9);
            });
        };

        using var node = OpenNode(bus, With(app), new ManualTimeSource());
        Settle(node);

        seen.Should().Be(NmtState.PreOperational, "the action is held until the node has ordered its start-up");
        node.ObjectDictionary.ReadUnsigned(0x6000, 0x01).Should().Be(9u);
        Written(app).Should().BeEmpty("the held action ran as a callback of the application");
    }

    // -----------------------------------------------------------------------------------------
    // A deadline armed before the node has opened (#561).
    // -----------------------------------------------------------------------------------------

    /// <summary>The node an application's host belongs to: the constructor has not returned it yet
    /// while the applications attach.</summary>
    private static CanOpenNode NodeOf(ICanOpenApplicationHost host)
        => (CanOpenNode)host.GetType().GetField("_node", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(host)!;

    /// <summary>
    /// An application to attach after one that arms a deadline due at once in its <c>Attach</c>: its own
    /// <c>Attach</c> holds the constructor and runs two jobs through the node's actor, by which time the
    /// actor has fired a deadline that was due before the first job, and passes what
    /// <paramref name="ran"/> says then to <paramref name="ranWhileHeld"/>.
    /// </summary>
    private static RecordingApplication HoldsTheConstructorAfter(Func<bool> ran, Action<bool> ranWhileHeld)
        => new("holder")
        {
            OnAttach = host =>
            {
                var node = NodeOf(host);
                node.PostToActorAsync(() => { }).GetAwaiter().GetResult();
                node.PostToActorAsync(() => { }).GetAwaiter().GetResult();
                ranWhileHeld(ran());
            },
        };

    [Fact] // #561
    public void A_Deadline_Due_During_Attach_Runs_Once_The_Node_Has_Opened()
    {
        using var bus = Open(NewSession(), 1);
        int ran = 0;
        bool? ranWhileHeld = null;
        NmtState? seen = null;
        var early = Entries("early");
        var declare = early.OnAttach!;
        early.OnAttach = host =>
        {
            declare(host);
            host.Arm(TimeSpan.Zero, () =>
            {
                seen = host.State;
                host.ObjectDictionary.WriteUnsigned(0x6000, 0x01, 9);
                Interlocked.Increment(ref ran);
            });
        };
        var holder = HoldsTheConstructorAfter(() => Volatile.Read(ref ran) > 0, held => ranWhileHeld = held);

        using var node = OpenNode(bus, With(early, holder), new ManualTimeSource());
        Settle(node);

        ranWhileHeld.Should().BeFalse("the deadline is held while the constructor runs (#561)");
        Volatile.Read(ref ran).Should().Be(1, "it runs once the node has opened");
        seen.Should().Be(NmtState.PreOperational, "behind the start-up, as a post from Attach");
        node.ObjectDictionary.ReadUnsigned(0x6000, 0x01).Should().Be(9u);
        Written(early).Should().BeEmpty("it ran as a callback of the application");
    }

    [Fact] // #561
    public async Task A_Deadline_The_Actor_Reaches_Only_After_The_Open_Still_Runs_Behind_The_Start_Up()
    {
        // The first application keeps the actor busy from its Attach until the open has returned, so
        // the deadline the second arms, due at once, is fired by the actor only after the hold has been
        // released, but ahead of the start-up job: a timer due before a job was posted does not wait
        // for it (ProtocolActor.FireDueTimers). It still runs behind the start-up.
        using var bus = Open(NewSession(), 1);
        using var busy = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        Task? blocked = null;
        NmtState? seen = null;
        // The blocking job is running before Attach returns, so the actor took its batch of work with
        // that job alone: the start-up job, posted later, waits for the next batch, which the deadline
        // does not wait for.
        var blocker = new RecordingApplication("blocker")
        {
            OnAttach = host =>
            {
                blocked = NodeOf(host).PostToActorAsync(() =>
                {
                    started.Set();
                    busy.Wait(ShortTimeout);
                });
                started.Wait(ShortTimeout).Should().BeTrue("the actor runs the blocking job");
            },
        };
        var early = new RecordingApplication("early")
        {
            OnAttach = host => host.Arm(TimeSpan.Zero, () => seen = host.State),
        };

        using var node = OpenNode(bus, With(blocker, early), new ManualTimeSource());
        busy.Set();
        await blocked!.WithTimeoutAsync(ShortTimeout);
        Settle(node);

        seen.Should().Be(NmtState.PreOperational, "the deadline runs behind the start-up job, not ahead of it");
    }

    /// <remarks>
    /// What this proves is that the callback is held, not run, up to the failure: the second
    /// application reads the count after the actor has fired the deadline, then fails the open. It
    /// also fails if the failed open released the held work to the actor, whose shutdown drains its
    /// mailbox and would run it. That the work is dropped rather than merely left held
    /// (<c>CloseApplicationPosts</c>, the closed check in <c>RunApplicationDeadline</c>) is not
    /// observable: nothing releases held work after a failed open, so removing the drop alone does
    /// not fail this test.
    /// </remarks>
    [Fact] // #561
    public void A_Deadline_Due_During_An_Open_That_Fails_Does_Not_Run_Before_The_Failure()
    {
        using var bus = Open(NewSession(), 1);
        using var service = new CanBusService(bus);
        int ran = 0;
        int ranBeforeTheFailure = -1;
        var early = new RecordingApplication("early")
        {
            OnAttach = host => host.Arm(TimeSpan.Zero, () => Interlocked.Increment(ref ran)),
        };
        // Holds the constructor until the actor has fired the deadline, reads the count, then fails the open.
        var broken = new RecordingApplication("broken")
        {
            OnAttach = host =>
            {
                var node = NodeOf(host);
                node.PostToActorAsync(() => { }).GetAwaiter().GetResult();
                node.PostToActorAsync(() => { }).GetAwaiter().GetResult();
                ranBeforeTheFailure = Volatile.Read(ref ran);
                throw new InvalidOperationException("no device");
            },
        };

        Assert.Throws<InvalidOperationException>(() => new CanOpenNode(service, Slave, With(early, broken), ownsService: false, new ManualTimeSource()));

        ranBeforeTheFailure.Should().Be(0, "the deadline was held while the node was being opened");
        Volatile.Read(ref ran).Should().Be(0, "the failed open ended the actor, and the held callback with it");
    }

    [Fact] // #561, Codecov on #567
    public async Task A_Deadline_The_Actor_Fires_Only_After_The_Open_Failed_Is_Dropped_Quietly()
    {
        // The first application keeps the actor busy from its Attach, so the deadline the second arms,
        // due at once, is not fired while the node is being opened. The third fails the open. Only once
        // the failed open has closed the held work is the actor let go: it fires the deadline as it
        // shuts down (its due timers are drained), and the callback must be dropped without a trace.
        using var bus = Open(NewSession(), 1);
        using var service = new CanBusService(bus);
        using var busy = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        int ran = 0;
        var reported = new List<Exception>();
        CanOpenNode? opening = null;
        var blocker = new RecordingApplication("blocker")
        {
            OnAttach = host =>
            {
                opening = NodeOf(host);
                opening.BackgroundExceptionOccurred += (_, ex) => { lock (reported) reported.Add(ex); };
                _ = opening.PostToActorAsync(() =>
                {
                    started.Set();
                    busy.Wait(ShortTimeout);
                });
                started.Wait(ShortTimeout).Should().BeTrue("the actor runs the blocking job");
            },
        };
        var early = new RecordingApplication("early")
        {
            OnAttach = host => host.Arm(TimeSpan.Zero, () => Interlocked.Increment(ref ran)),
        };
        var broken = new RecordingApplication("broken") { OnAttach = _ => throw new InvalidOperationException("no device") };
        var closed = typeof(CanOpenNode).GetField("_applicationPostsClosed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var letGo = Task.Run(async () =>
        {
            await WaitUntilAsync(() => opening is not null && (bool)closed.GetValue(opening)!, "the failed open to close the held work");
            busy.Set();
        });

        Assert.Throws<InvalidOperationException>(() => new CanOpenNode(service, Slave, With(blocker, early, broken), ownsService: false, new ManualTimeSource()));
        await letGo.WithTimeoutAsync(ShortTimeout);
        // The constructor's failure path joined the actor, whose shutdown drained the due deadline; a
        // marker behind every background report made so far is delivered after the last of them.
        var dispatcher = (CanKit.Pro.Actor.BackgroundExceptionDispatcher)typeof(CanOpenNode)
            .GetField("_backgroundReports", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(opening!)!;
        var delivered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.Post(() => delivered.TrySetResult(true));
        await delivered.Task.WithTimeoutAsync(ShortTimeout);

        Volatile.Read(ref ran).Should().Be(0, "a deadline of an open that failed is dropped");
        lock (reported) reported.Should().BeEmpty("it is dropped quietly, not refused by an actor that is shutting down");
    }

    [Fact] // #561
    public async Task A_Deadline_Due_Before_The_Start_Up_Of_A_Node_Disposed_Meanwhile_Is_Dropped_Quietly()
    {
        // The first application keeps the actor busy from its Attach, so the start-up job waits behind
        // it and the deadline the second arms, due at once on the manual clock, is not fired. The node
        // opens and is disposed; once its actor refuses work, the actor is let go: it fires the deadline
        // before the start-up job, as it shuts down, and the callback must be dropped without a trace.
        using var bus = Open(NewSession(), 1);
        using var busy = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        int ran = 0;
        var reported = new List<Exception>();
        var blocker = new RecordingApplication("blocker")
        {
            OnAttach = host =>
            {
                var opening = NodeOf(host);
                opening.BackgroundExceptionOccurred += (_, ex) => { lock (reported) reported.Add(ex); };
                _ = opening.PostToActorAsync(() =>
                {
                    started.Set();
                    busy.Wait(ShortTimeout);
                });
                started.Wait(ShortTimeout).Should().BeTrue("the actor runs the blocking job");
            },
        };
        var early = new RecordingApplication("early")
        {
            OnAttach = host => host.Arm(TimeSpan.Zero, () => Interlocked.Increment(ref ran)),
        };
        var node = OpenNode(bus, With(blocker, early), new ManualTimeSource());
        var actor = typeof(CanOpenNode).GetField("_actor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(node)!;
        var actorDisposed = actor.GetType().GetField("_disposedFlag", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var dispatcher = (CanKit.Pro.Actor.BackgroundExceptionDispatcher)typeof(CanOpenNode)
            .GetField("_backgroundReports", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(node)!;

        var disposing = Task.Run(node.Dispose);
        await WaitUntilAsync(() => (int)actorDisposed.GetValue(actor)! != 0, "the actor to refuse work");
        busy.Set();
        await disposing.WithTimeoutAsync(ShortTimeout);
        var delivered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.Post(() => delivered.TrySetResult(true));
        await delivered.Task.WithTimeoutAsync(ShortTimeout);

        Volatile.Read(ref ran).Should().Be(0, "the node was disposed before the callback's turn");
        lock (reported) reported.Should().BeEmpty("it is dropped quietly, as a post queued before the disposal is");
    }

    [Fact]
    public void A_Post_Held_By_An_Open_That_Fails_Is_Dropped()
    {
        using var bus = Open(NewSession(), 1);
        using var service = new CanBusService(bus);
        bool ran = false;
        var first = Entries("first");
        var declare = first.OnAttach!;
        first.OnAttach = host =>
        {
            declare(host);
            host.Post(() => ran = true);
        };
        var broken = new RecordingApplication("broken") { OnAttach = _ => throw new InvalidOperationException("no device") };

        Assert.Throws<InvalidOperationException>(() => new CanOpenNode(service, Slave, With(first, broken), ownsService: false));
        first.Host!.Post(() => ran = true);

        // The node's actor ended with the failed open: nothing can run either action any more.
        ran.Should().BeFalse();
    }

    [Fact]
    public void A_Post_After_Dispose_Is_Dropped_Without_A_Throw()
    {
        using var bus = Open(NewSession(), 1);
        var app = Entries();
        using (var node = OpenNode(bus, With(app), new ManualTimeSource()))
        {
            Settle(node);
        }

        bool ran = false;

        FluentActions.Invoking(() => app.Host!.Post(() => ran = true)).Should().NotThrow();

        // Dispose has ended the actor: an action not run by now is never run.
        ran.Should().BeFalse();
    }

    [Fact]
    public async Task A_Throwing_Post_Is_A_Background_Exception_And_The_Node_Keeps_Working()
    {
        using var bus = Open(NewSession(), 1);
        var app = Entries();
        using var node = OpenNode(bus, With(app), new ManualTimeSource());
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        node.BackgroundExceptionOccurred += (_, ex) => reported.TrySetResult(ex);
        bool next = false;

        app.Host!.Post(() => throw new InvalidOperationException("posted failed"));
        app.Host.Post(() => next = true);

        var failure = await reported.Task.WithTimeoutAsync(ShortTimeout);
        Settle(node);
        failure.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("posted failed");
        next.Should().BeTrue("one failing action does not keep the next from running");
    }

    [Fact]
    public void Post_Refuses_A_Null_Action()
    {
        using var bus = Open(NewSession(), 1);
        var app = Entries();
        using var node = OpenNode(bus, With(app), new ManualTimeSource());

        FluentActions.Invoking(() => app.Host!.Post(null!))
            .Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be("action");
    }
}
