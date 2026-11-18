using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Application;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

// What an application's callback changes while the node is still acting on what it told it: a
// callback that ends the condition it was told about, and one that disposes the node, in front of
// the work the node had lined up behind the callback.
public partial class CanOpenApplicationSeamTests
{
    private static readonly byte[] HeartbeatTimeoutCode = { 0x30, 0x81 };

    /// <summary>Error behaviour and the guarding EMCY: a heartbeat timeout or a life-guarding lapse
    /// is a communication failure, and it raises EMCY <c>8130h</c> as well.</summary>
    private static CanOpenNodeOptions WithGuardingEmcy(params ICanOpenApplication[] applications)
        => applications.Aggregate(new CanOpenNodeOptions { EnableErrorBehavior = true, EmitGuardingErrorEmcy = true },
            (options, application) => options.WithApplication(application));

    private static bool CarriesCode(byte[] emcy, byte[] code) => emcy[0] == code[0] && emcy[1] == code[1];

    private static Task<bool> FailureActiveAsync(CanOpenNode node, RecordingApplication app)
        => node.ActorForTests.PostAsync(() => app.Host!.CommunicationFailureActive);

    /// <summary>Runs <paramref name="name"/>, a private method of the node, as one actor job: the
    /// task completes once the job has run, whether or not a callback disposed the node in it.</summary>
    private static Task RunPrivateOnActor(CanOpenNode node, string name, params object[] arguments)
        => node.ActorForTests.PostAsync(() => typeof(CanOpenNode)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(node, arguments));

    // -----------------------------------------------------------------------------------------
    // A callback that ends the communication failure it was told about.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_Watch_Dropped_From_The_Failure_Callback_Leaves_No_Heartbeat_Error()
    {
        var app = Watcher();
        app.WhenFailureChanged = active =>
        {
            if (active) app.Host!.ObjectDictionary.WriteUnsigned(0x1016, 1, 0);
        };
        using var watch = new ProducerWatchRig(WithGuardingEmcy(app));
        await watch.BeatAsync();

        watch.Silence();
        await EmcyChainSettledAsync(watch.Rig.Node);

        Failures(app).Should().Equal("failure True", "failure False");
        watch.Rig.Wire.Payloads(SlaveEmcy).Should().NotContain(emcy => CarriesCode(emcy, HeartbeatTimeoutCode),
            "the producer is no longer watched, so nothing would ever end its error");
        ErrorRegister(watch.Rig.Node).Should().Be(0);
        (await FailureActiveAsync(watch.Rig.Node, app)).Should().BeFalse();
    }

    [Fact]
    public async Task A_Watch_Dropped_From_The_State_Change_The_Failure_Caused_Leaves_No_Heartbeat_Error()
    {
        // The error behaviour takes the node to Pre-Operational, and the profile drops the watch when
        // it hears of that change: after the publish, before the node would raise the error.
        var app = new RecordingApplication("leaver");
        app.WhenNmtChanged = (previous, _) =>
        {
            if (previous == NmtState.Operational) app.Host!.ObjectDictionary.WriteUnsigned(0x1016, 1, 0);
        };
        using var watch = new ProducerWatchRig(WithGuardingEmcy(app));
        watch.Rig.Od.WriteUnsigned(0x1029, 1, 0);
        await watch.Rig.StartAsync();
        Settle(watch.Rig.Node);
        await watch.BeatAsync();

        watch.Silence();
        await EmcyChainSettledAsync(watch.Rig.Node);

        watch.Rig.Node.State.Should().Be(NmtState.PreOperational, "the failure was there when the node acted on it");
        watch.Rig.Wire.Payloads(SlaveEmcy).Should().NotContain(emcy => CarriesCode(emcy, HeartbeatTimeoutCode));
        ErrorRegister(watch.Rig.Node).Should().Be(0);
    }

    [Fact]
    public async Task Life_Guarding_Switched_Off_From_The_Failure_Callback_Leaves_No_Error()
    {
        var app = Watcher();
        app.WhenFailureChanged = active =>
        {
            if (active) app.Host!.ObjectDictionary.WriteUnsigned(0x100D, 0, 0);
        };
        var clock = new ManualTimeSource();
        using var rig = new EntryRig(WithGuardingEmcy(app), clock);
        rig.Od.WriteUnsigned(0x100C, 0, 50);
        rig.Od.WriteUnsigned(0x100D, 0, 2);
        Settle(rig.Node);
        await PollLifeGuarding(rig.Node);

        Advance(clock, rig.Node, TimeSpan.FromMilliseconds(101));
        await EmcyChainSettledAsync(rig.Node);

        Failures(app).Should().Equal("failure True", "failure False");
        rig.Wire.Payloads(SlaveEmcy).Should().NotContain(emcy => CarriesCode(emcy, HeartbeatTimeoutCode),
            "life guarding is off, so no poll would ever end its error");
        ErrorRegister(rig.Node).Should().Be(0);
        (await FailureActiveAsync(rig.Node, app)).Should().BeFalse();
    }

    [Fact]
    public async Task A_Watch_Dropped_From_The_Callback_Of_A_Changed_Consumer_Time_Stays_Dropped()
    {
        // Changing the consumer time of the silent producer ends its failure; the profile drops the
        // watch when it hears of that. The node's own rebuild of the watches, which the change began,
        // must not put the watch back afterwards.
        var app = Watcher();
        app.WhenFailureChanged = active =>
        {
            if (!active) app.Host!.ObjectDictionary.WriteUnsigned(0x1016, 1, 0);
        };
        using var watch = new ProducerWatchRig(app);
        await watch.BeatAsync();
        watch.Silence();
        Failures(app).Should().Equal("failure True");

        watch.Rig.Od.WriteUnsigned(0x1016, 1, ((uint)ProducerNode << 16) | 200);
        Settle(watch.Rig.Node);
        // Past both consumer times, the 100 ms the watch had and the 200 ms it was given.
        Advance(watch.Clock, watch.Rig.Node, TimeSpan.FromMilliseconds(500));

        Failures(app).Should().Equal(new[] { "failure True", "failure False" },
            "the producer is no longer watched, so it cannot time out again");
        watch.Rig.Od.ReadUnsigned(0x1016, 1).Should().Be(0u);
        (await FailureActiveAsync(watch.Rig.Node, app)).Should().BeFalse();
    }

    // -----------------------------------------------------------------------------------------
    // A callback that disposes the node: the applications after it are not called.
    // -----------------------------------------------------------------------------------------

    private static RecordingApplication SecondClaimant() => new("second", new CanOpenIndexRange(0x6800, 0x6FFF))
    {
        OnAttach = host => host.ObjectDictionary.AddU8(0x6800, 0x01, 0),
    };

    [Fact]
    public async Task A_Callback_That_Disposes_The_Node_Ends_The_Round_Of_Written_Entries()
    {
        var first = Entries("first");
        var second = SecondClaimant();
        CanOpenNode? node = null;
        first.WhenWritten = _ => node!.Dispose();
        using var rig = new EntryRig(With(first, second));
        node = rig.Node;
        Settle(rig.Node);

        // Both writes and their evaluation in one actor job: one batch with an entry of each
        // claimant, told before the job ends.
        await rig.Node.ActorForTests.PostAsync(() =>
        {
            rig.Od.WriteUnsigned(0x6000, 0x01, 7);
            rig.Od.WriteUnsigned(0x6800, 0x01, 7);
            typeof(CanOpenNode).GetMethod("EvaluateApplicationEntriesOnActor", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(rig.Node, null);
        });

        Written(first).Should().Equal("written 6000:01");
        Written(second).Should().BeEmpty("the node was gone before the second claimant's turn");
    }

    [Fact]
    public async Task A_Reset_Callback_That_Disposes_The_Node_Ends_The_Round()
    {
        var first = new RecordingApplication("first");
        var second = new RecordingApplication("second");
        CanOpenNode? node = null;
        first.WhenReset = _ => node!.Dispose();
        bool applicationReset = false;
        using var rig = new EntryRig(With(first, second));
        node = rig.Node;
        rig.Node.ApplicationReset += (_, _) => applicationReset = true;
        Settle(rig.Node);

        await RunPrivateOnActor(rig.Node, "HandleIncoming",
            CanOpenCobId.NmtCommand, ResetCommand(NmtCommand.ResetNode), false);

        first.Log.Should().Contain("reset ResetNode");
        second.Log.Should().NotContain("reset ResetNode", "the node was gone before the second application's turn");
        applicationReset.Should().BeFalse();
    }

    [Fact]
    public async Task A_State_Change_Callback_That_Disposes_The_Node_Ends_The_Round()
    {
        var first = new RecordingApplication("first");
        var second = new RecordingApplication("second");
        CanOpenNode? node = null;
        first.WhenNmtChanged = (_, current) =>
        {
            if (current == NmtState.Operational) node!.Dispose();
        };
        using var rig = new EntryRig(With(first, second));
        node = rig.Node;
        Settle(rig.Node);

        await RunPrivateOnActor(rig.Node, "HandleIncoming",
            CanOpenCobId.NmtCommand, new[] { (byte)NmtCommand.Start, Slave }, false);

        first.Log.Should().Contain("nmt PreOperational>Operational");
        second.Log.Should().NotContain("nmt PreOperational>Operational", "the node was gone before the second application's turn");
    }

    [Fact]
    public async Task A_Failure_Callback_That_Disposes_The_Node_Ends_The_Round()
    {
        var first = Watcher("first");
        var second = Watcher("second");
        CanOpenNode? node = null;
        first.WhenFailureChanged = _ => node!.Dispose();
        using var rig = new EntryRig(WithErrorBehavior(first, second));
        node = rig.Node;
        Settle(rig.Node);

        await RunPrivateOnActor(rig.Node, "RaiseHeartbeatTimeout", ProducerNode, TimeSpan.FromMilliseconds(100));

        Failures(first).Should().Equal("failure True");
        Failures(second).Should().BeEmpty("the node was gone before the second application's turn");
    }
}
