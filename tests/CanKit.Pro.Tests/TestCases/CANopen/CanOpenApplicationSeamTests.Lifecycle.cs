using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

// The node's NMT state and its resets, as an attached application sees them, and the one thing an
// application may ask of the state: to leave Operational.
public partial class CanOpenApplicationSeamTests
{
    private const uint SlaveHeartbeat = 0x700 + Slave;

    private static IEnumerable<string> StateChanges(RecordingApplication app)
        => app.Log.Where(e => e.StartsWith("nmt ", StringComparison.Ordinal));

    private static byte[] ResetCommand(NmtCommand command) => new[] { (byte)command, Slave };

    /// <summary>Resets the slave from the wire and returns once the reset is complete: the boot-up is
    /// out, and the actor has finished everything it queued behind it.</summary>
    private static async Task ResetFromWireAsync(EntryRig rig, NmtCommand command)
    {
        int bootups = rig.Wire.Count(SlaveHeartbeat);
        rig.Wire.Transmit(0x000, ResetCommand(command));
        await rig.Wire.WaitForCountAsync(SlaveHeartbeat, bootups + 1);
        Settle(rig.Node);
    }

    // -----------------------------------------------------------------------------------------
    // State changes.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void The_Start_Up_Is_Reported()
    {
        using var bus = Open(NewSession(), 1);
        var app = new RecordingApplication("recorder");

        using var node = OpenNode(bus, With(app));
        Settle(node);

        app.Log.Should().Equal("attach", "nmt Initializing>PreOperational");
    }

    [Fact]
    public async Task A_Start_Command_Is_Reported()
    {
        var app = new RecordingApplication("recorder");
        using var rig = new EntryRig(With(app));

        await rig.StartAsync();
        Settle(rig.Node);

        StateChanges(app).Should().Equal("nmt Initializing>PreOperational", "nmt PreOperational>Operational");
    }

    [Fact]
    public async Task A_Reset_Node_Is_Reported_In_Order()
    {
        var app = Entries();
        uint declaredInReset = 0;
        app.WhenReset = _ => declaredInReset = app.Host!.ObjectDictionary.ReadUnsigned(0x6000, 0x01);
        using var rig = new EntryRig(With(app));
        await rig.StartAsync();
        rig.Od.WriteUnsigned(0x6000, 0x01, 9);
        await rig.Wire.WaitForCountAsync(SlaveHeartbeat, 1);
        Settle(rig.Node);
        int mark = app.Log.Count;

        await ResetFromWireAsync(rig, NmtCommand.ResetNode);

        app.Log.Skip(mark).Should().Equal("nmt Operational>Initializing", "reset ResetNode", "nmt Initializing>PreOperational");
        declaredInReset.Should().Be(5u, "the application's objects are restored before it is told about the reset");
    }

    [Fact]
    public async Task A_Reset_Communication_Is_Reported_With_Its_Command()
    {
        var app = Entries();
        using var rig = new EntryRig(With(app));
        await rig.StartAsync();
        await rig.Wire.WaitForCountAsync(SlaveHeartbeat, 1);
        Settle(rig.Node);
        int mark = app.Log.Count;

        await ResetFromWireAsync(rig, NmtCommand.ResetCommunication);

        app.Log.Skip(mark).Should().Equal(
            "nmt Operational>Initializing", "reset ResetCommunication", "nmt Initializing>PreOperational");
    }

    [Fact]
    public async Task A_Reset_Reaches_The_Profile_Before_The_Application()
    {
        var app = Entries();
        using var rig = new EntryRig(With(app));
        rig.Node.ApplicationReset += (_, _) => app.Note("application reset");
        await rig.StartAsync();
        await rig.Wire.WaitForCountAsync(SlaveHeartbeat, 1);
        Settle(rig.Node);
        int mark = app.Log.Count;

        await ResetFromWireAsync(rig, NmtCommand.ResetNode);

        app.Log.Skip(mark).Should().ContainInOrder("reset ResetNode", "application reset");
    }

    [Fact]
    public async Task A_Reset_From_PreOperational_Is_Reported_From_There()
    {
        var app = Entries();
        using var rig = new EntryRig(With(app));
        await rig.Wire.WaitForCountAsync(SlaveHeartbeat, 1);
        Settle(rig.Node);
        int mark = app.Log.Count;

        await ResetFromWireAsync(rig, NmtCommand.ResetNode);

        app.Log.Skip(mark).Should().Equal(
            "nmt PreOperational>Initializing", "reset ResetNode", "nmt Initializing>PreOperational");
    }

    [Fact]
    public async Task A_Node_Disposed_As_A_Reset_Begins_Goes_No_Further()
    {
        var app = Entries();
        CanOpenNode? node = null;
        app.WhenNmtChanged = (_, current) =>
        {
            if (current == NmtState.Initializing) node!.Dispose();
        };
        bool applicationReset = false;
        var reported = new List<Exception>();
        using var rig = new EntryRig(With(app));
        node = rig.Node;
        rig.Node.ApplicationReset += (_, _) => applicationReset = true;
        rig.Node.BackgroundExceptionOccurred += (_, ex) =>
        {
            lock (reported) reported.Add(ex);
        };
        await rig.Wire.WaitForCountAsync(SlaveHeartbeat, 1);
        rig.Od.WriteUnsigned(0x6000, 0x01, 9);
        Settle(rig.Node);

        // The whole reset is one actor job: the task completes once it has run, disposed node or not.
        await rig.Node.ActorForTests.PostAsync(() =>
            rig.Node.HandleIncoming(CanOpenCobId.NmtCommand, ResetCommand(NmtCommand.ResetNode), isRtr: false));

        rig.Od.ReadUnsigned(0x6000, 0x01).Should().Be(9u, "the reset ends where the node was disposed: nothing is restored");
        app.Log.Should().NotContain("reset ResetNode");
        applicationReset.Should().BeFalse();
        rig.Wire.Count(SlaveHeartbeat).Should().Be(1, "a disposed node announces nothing");
        lock (reported) reported.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------------------------
    // Asking for a state.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_Profile_Can_Take_Its_Node_Out_Of_Operational()
    {
        var app = Entries();
        app.WhenWritten = _ =>
        {
            app.Host!.RequestNmtState(NmtState.PreOperational);
            app.Note($"request returned in {app.Host.State}");
        };
        using var rig = new EntryRig(With(app), new ManualTimeSource());
        rig.Node.StartHeartbeatProducer(TimeSpan.FromMilliseconds(100));
        await rig.StartAsync();
        await WaitUntilAsync(() => rig.Wire.Payloads(SlaveHeartbeat).LastOrDefault()?.SequenceEqual(new byte[] { 0x05 }) == true,
            "the state-change heartbeat of Operational");
        int heartbeats = rig.Wire.Count(SlaveHeartbeat);

        await rig.Wire.DownloadU8Async(0x6000, 0x01, 7);
        await rig.Wire.WaitForCountAsync(SlaveHeartbeat, heartbeats + 1);
        Settle(rig.Node);

        rig.Wire.Payloads(SlaveHeartbeat).Last().Should().Equal(new byte[] { 0x7F });
        rig.Node.State.Should().Be(NmtState.PreOperational);
        app.Log.Should().ContainInOrder("nmt Operational>PreOperational", "request returned in PreOperational");
    }

    [Fact]
    public async Task A_Profile_Cannot_Start_Its_Node()
    {
        var app = Entries();
        app.WhenWritten = _ => app.Host!.RequestNmtState(NmtState.Operational);
        using var rig = new EntryRig(With(app));
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Node.BackgroundExceptionOccurred += (_, ex) => reported.TrySetResult(ex);

        await rig.Wire.DownloadU8Async(0x6000, 0x01, 7);

        var failure = await reported.Task.WithTimeoutAsync(ShortTimeout);
        failure.Should().BeOfType<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("target");
        Settle(rig.Node);
        rig.Node.State.Should().Be(NmtState.PreOperational, "a profile does not start its own node");
        StateChanges(app).Should().Equal("nmt Initializing>PreOperational");
    }

    [Fact]
    public async Task A_Request_For_The_Current_State_Does_Nothing()
    {
        var app = Entries();
        bool asked = false;
        app.WhenWritten = _ =>
        {
            app.Host!.RequestNmtState(NmtState.PreOperational);
            asked = true;
        };
        using var rig = new EntryRig(With(app), new ManualTimeSource());
        rig.Node.StartHeartbeatProducer(TimeSpan.FromMilliseconds(100));
        // A round trip through the node: everything it ordered before now is on the wire.
        await rig.Wire.UploadAsync(0x6000, 0x01);
        int heartbeats = rig.Wire.Count(SlaveHeartbeat);

        await rig.Wire.DownloadU8Async(0x6000, 0x01, 7);
        Settle(rig.Node);
        await rig.Wire.UploadAsync(0x6000, 0x01);

        asked.Should().BeTrue("the application's request ran");
        rig.Wire.Count(SlaveHeartbeat).Should().Be(heartbeats, "the node was already there, so it announces nothing");
        StateChanges(app).Should().Equal("nmt Initializing>PreOperational");
    }

    [Fact]
    public async Task A_Request_During_A_Reset_Is_Ignored()
    {
        var app = Entries();
        app.WhenReset = _ => app.Host!.RequestNmtState(NmtState.Stopped);
        using var rig = new EntryRig(With(app));
        await rig.Wire.WaitForCountAsync(SlaveHeartbeat, 1);
        Settle(rig.Node);
        int mark = app.Log.Count;

        await ResetFromWireAsync(rig, NmtCommand.ResetNode);

        app.Log.Skip(mark).Should().Equal(
            "nmt PreOperational>Initializing", "reset ResetNode", "nmt Initializing>PreOperational");
        rig.Node.State.Should().Be(NmtState.PreOperational, "the reset ends in Pre-Operational by itself");
    }

    [Fact]
    public void A_Request_From_Outside_The_Actor_Is_Refused()
    {
        using var bus = Open(NewSession(), 1);
        var app = new RecordingApplication("recorder");
        using var node = OpenNode(bus, With(app));
        Settle(node);

        Action ask = () => app.Host!.RequestNmtState(NmtState.Stopped);

        ask.Should().Throw<InvalidOperationException>("the state belongs to the actor loop");
        node.State.Should().Be(NmtState.PreOperational);
    }

    // -----------------------------------------------------------------------------------------
    // A callback that changes the state while the node is still telling applications about the
    // previous change.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_Application_Sees_The_Changes_In_The_Order_They_Happened()
    {
        var first = new RecordingApplication("first");
        var second = new RecordingApplication("second");
        first.WhenNmtChanged = (_, current) =>
        {
            if (current == NmtState.Operational)
            {
                first.Host!.RequestNmtState(NmtState.Stopped);
                first.Note($"request returned in {first.Host.State}");
            }
        };
        second.WhenNmtChanged = (_, current) =>
        {
            if (current == NmtState.Operational) second.Note($"reporting Operational in {second.Host!.State}");
        };
        using var rig = new EntryRig(With(first, second));

        // Not StartAsync: that waits for Operational, which the first application ends at once.
        rig.Wire.Transmit(0x000, new[] { (byte)NmtCommand.Start, Slave });
        await WaitUntilAsync(() => rig.Node.State == NmtState.Stopped, "Stopped");
        Settle(rig.Node);

        string[] expected =
        {
            "nmt Initializing>PreOperational", "nmt PreOperational>Operational", "nmt Operational>Stopped",
        };
        first.Log.Should().ContainInOrder("nmt PreOperational>Operational", "request returned in Stopped", "nmt Operational>Stopped");
        second.Log.Should().ContainInOrder("nmt PreOperational>Operational", "reporting Operational in Stopped", "nmt Operational>Stopped");
        first.Log.Where(e => e.StartsWith("nmt ", StringComparison.Ordinal)).Should().Equal(expected);
        second.Log.Where(e => e.StartsWith("nmt ", StringComparison.Ordinal)).Should().Equal(expected,
            "the second application is told of Operational before the change out of it, although the first asked for it");
    }

    [Fact]
    public async Task Each_Change_Announces_Its_Own_State_When_A_Callback_Changes_It_Again()
    {
        var app = new RecordingApplication("leaver");
        app.WhenNmtChanged = (_, current) =>
        {
            if (current == NmtState.Operational) app.Host!.RequestNmtState(NmtState.Stopped);
        };
        // A clock that does not move: the producer sends state-change heartbeats only.
        using var rig = new EntryRig(With(app), new ManualTimeSource());
        rig.Node.StartHeartbeatProducer(TimeSpan.FromMilliseconds(100));
        await rig.Wire.WaitForCountAsync(SlaveHeartbeat, 1);
        Settle(rig.Node);
        int mark = rig.Wire.Count(SlaveHeartbeat);

        rig.Wire.Transmit(0x000, new[] { (byte)NmtCommand.Start, Slave });
        await WaitUntilAsync(() => rig.Node.State == NmtState.Stopped, "Stopped");
        await rig.Wire.WaitForCountAsync(SlaveHeartbeat, mark + 2);

        rig.Wire.Payloads(SlaveHeartbeat).Skip(mark).Select(frame => frame[0]).Should().Equal(
            new byte[] { (byte)NmtState.Operational, (byte)NmtState.Stopped },
            "the node was in Operational before the profile took it out, and the master is told both, in order");
    }
}
