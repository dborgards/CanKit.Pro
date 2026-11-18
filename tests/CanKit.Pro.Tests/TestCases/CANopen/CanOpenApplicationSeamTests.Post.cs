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
                onActor.Add(TryRequestTransmission(app.Host));
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

    /// <summary>Whether an actor-loop member of the host accepts the call: <c>RequestTransmission</c>
    /// with an empty list does nothing on the loop and throws off it.</summary>
    private static bool TryRequestTransmission(ICanOpenApplicationHost host)
    {
        try
        {
            host.RequestTransmission(Array.Empty<CanOpenObjectAddress>());
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
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
        var node = OpenNode(bus, With(app), new ManualTimeSource());
        Settle(node);
        node.Dispose();
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
