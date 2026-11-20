using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Lss;
using CanKit.Pro.CANopen.Nmt;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

// FR-CO-011, #547: a communication error raised on an LSS slave that waits for its node-id, in
// Initialisation, where only the boot-up object exists (CiA 301 v4.2.0 §7.3.2.2.1), is held and
// announced behind the boot-up once the node has its node-id, with 1001h showing it from that EMCY
// on — the rule #546 gave the errors of a profile raised there. An error whose cause ends while it is
// held leaves nothing on the bus, 0000h included. The heartbeat-consumer timeout (8130h with
// EmitGuardingErrorEmcy) is the one communication error the slave detects there: the controller
// and overrun monitors start only on entering Pre-Operational, which
// Controller_Emcy_Lss_Reset_Invalidates_An_Already_Queued_Baseline and
// Overrun_Emcy_Does_Not_Replay_Notifications_From_Unconfigured_Lss_Initialisation pin.
public partial class CanOpenPdoEngineTests
{
    /// <summary>Records the COB-ID of every frame the node hands the driver, in order.</summary>
    private static List<uint> RecordTransmitOrder(ControllerEmcyRig rig)
    {
        var order = new List<uint>();
        var forward = rig.Bus.OnTransmitting!;
        rig.Bus.OnTransmitting = frame =>
        {
            lock (order) order.Add((uint)frame.ID);
            forward(frame);
        };
        return order;
    }

    private static async Task TimeOutHeartbeatsWhileUnconfiguredAsync(ControllerEmcyRig rig, params byte[] producers)
    {
        var timedOut = new HashSet<byte>();
        var all = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Node.HeartbeatTimeout += (_, e) =>
        {
            lock (timedOut)
            {
                timedOut.Add(e.ProducerNodeId);
                if (producers.All(timedOut.Contains)) all.TrySetResult(true);
            }
        };
        foreach (var producer in producers) rig.Node.AddHeartbeatConsumer(producer, TimeSpan.FromMilliseconds(100));
        Advance(rig.Clock, rig.Node, TimeSpan.FromMilliseconds(101));
        await all.Task.WaitAsync(ShortTimeout);
        rig.Node.State.Should().Be(NmtState.Initializing);
        rig.Node.NodeId.Should().Be(CanOpenCobId.UnconfiguredNodeId);
    }

    private static async Task ConfigureNodeIdAsync(ControllerEmcyRig rig)
    {
        await Inject(rig.Node, CanOpenCobId.LssMasterMessage, LssCommand.SwitchStateGlobal, 1, 0, 0, 0, 0, 0, 0);
        await Inject(rig.Node, CanOpenCobId.LssMasterMessage, LssCommand.ConfigureNodeId, Device, 0, 0, 0, 0, 0, 0);
        await rig.Wire.WaitForCountAsync(CanOpenCobId.Heartbeat(Device), 1);
    }

    [Fact]
    public async Task Communication_Error_On_An_Unconfigured_Lss_Slave_Is_Announced_Behind_The_Boot_Up()
    {
        using var rig = new ControllerEmcyRig(enabled: false, unconfiguredLss: true);
        var order = RecordTransmitOrder(rig);
        await TimeOutHeartbeatsWhileUnconfiguredAsync(rig, 1);
        rig.Node.ObjectDictionary.ReadUnsigned(0x1001, 0).Should().Be(0, "the error's EMCY is held");

        // The boot-up's send is held in the driver. The EMCY chain either starts waiting for it, or
        // (if it did not wait) transmits an EMCY ahead of it: whichever happens first decides.
        var bootUpEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var emcyTransmitted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var chainWaits = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseBootUp = new System.Threading.ManualResetEventSlim();
        var forward = rig.Bus.OnTransmitting!;
        rig.Bus.OnTransmitting = frame =>
        {
            if ((uint)frame.ID == EmcyId) emcyTransmitted.TrySetResult(true);
            if ((uint)frame.ID == CanOpenCobId.Heartbeat(Device) && frame.Data.Span[0] == 0x00)
            {
                bootUpEntered.TrySetResult(true);
                releaseBootUp.Wait(ShortTimeout);
            }
            forward(frame);
        };
        ((CanOpenNode)rig.Node).HeldEmcysWaitForBootUpForTests = () => chainWaits.TrySetResult(true);
        try
        {
            await Inject(rig.Node, CanOpenCobId.LssMasterMessage, LssCommand.SwitchStateGlobal, 1, 0, 0, 0, 0, 0, 0);
            await Inject(rig.Node, CanOpenCobId.LssMasterMessage, LssCommand.ConfigureNodeId, Device, 0, 0, 0, 0, 0, 0);
            await bootUpEntered.Task.WaitAsync(ShortTimeout);
            var first = await Task.WhenAny(chainWaits.Task, emcyTransmitted.Task).WaitAsync(ShortTimeout);
            first.Should().BeSameAs(chainWaits.Task, "in Initialisation only the boot-up object exists, so the EMCY waits for it");
            rig.Node.ObjectDictionary.ReadUnsigned(0x1001, 0).Should().Be(0,
                "a communication error shows in 1001h from its EMCY, which has not gone out yet");
        }
        finally { releaseBootUp.Set(); }

        // The EMCY's own step writes 1001h, right before its frame.
        await ExpectEmcyAsync(rig.Wire, 1, 0x8130, Communication);
        rig.Node.ObjectDictionary.ReadUnsigned(0x1001, 0).Should().Be(Communication);
        // The application EMCY is the ordering witness: had the error not been announced, it would be first.
        await rig.Node.SendEmcyAsync(0x1000, 0).WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 1, 0x8130, Communication);
        await ExpectEmcyAsync(rig.Wire, 2, 0x1000, Communication);
        rig.Node.ObjectDictionary.ReadUnsigned(0x1001, 0).Should().Be(Communication);
        lock (order)
            order.IndexOf(CanOpenCobId.Heartbeat(Device)).Should().BeLessThan(order.IndexOf(EmcyId));

        await Inject(rig.Node, CanOpenCobId.Heartbeat(1), (byte)NmtState.PreOperational);
        await ExpectEmcyAsync(rig.Wire, 3, ErrorReset, 0);
        rig.Wire.Count(EmcyId).Should().Be(3);
    }

    // Review of #547 (F1): the error's EMCY is released behind the boot-up, its cause ends before that
    // EMCY has gone out, and a Reset Communication then drops the EMCY. The 0000h that ended it must go
    // with it: the bus never saw the error.
    [Fact]
    public async Task Error_Reset_Of_A_Released_Communication_Error_Is_Dropped_With_It_By_A_Reset()
    {
        using var rig = new ControllerEmcyRig(enabled: false, unconfiguredLss: true);
        await TimeOutHeartbeatsWhileUnconfiguredAsync(rig, 1);
        var bootUpEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var chainWaits = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseBootUp = new System.Threading.ManualResetEventSlim();
        var forward = rig.Bus.OnTransmitting!;
        rig.Bus.OnTransmitting = frame =>
        {
            if ((uint)frame.ID == CanOpenCobId.Heartbeat(Device) && frame.Data.Span[0] == 0x00
                && bootUpEntered.TrySetResult(true))
                releaseBootUp.Wait(ShortTimeout);
            forward(frame);
        };
        ((CanOpenNode)rig.Node).HeldEmcysWaitForBootUpForTests = () => chainWaits.TrySetResult(true);
        try
        {
            await Inject(rig.Node, CanOpenCobId.LssMasterMessage, LssCommand.SwitchStateGlobal, 1, 0, 0, 0, 0, 0, 0);
            await Inject(rig.Node, CanOpenCobId.LssMasterMessage, LssCommand.ConfigureNodeId, Device, 0, 0, 0, 0, 0, 0);
            await bootUpEntered.Task.WaitAsync(ShortTimeout);
            await chainWaits.Task.WaitAsync(ShortTimeout);
            // The cause ends while the 8130h waits for the boot-up: its 0000h is ordered behind it.
            await Inject(rig.Node, CanOpenCobId.Heartbeat(1), (byte)NmtState.PreOperational);
            await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)NmtCommand.ResetCommunication, Device);
        }
        finally { releaseBootUp.Set(); }

        await rig.Node.SendEmcyAsync(0x1000, 0).WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 1, 0x1000, 0);
        rig.Wire.Count(EmcyId).Should().Be(1, "neither the 8130h the reset dropped nor its 0000h");
    }

    [Fact]
    public async Task Communication_Error_That_Ends_Before_The_Unconfigured_Slave_Boots_Leaves_Nothing_On_The_Bus()
    {
        using var rig = new ControllerEmcyRig(enabled: false, unconfiguredLss: true);
        await TimeOutHeartbeatsWhileUnconfiguredAsync(rig, 1, 2);
        await Inject(rig.Node, CanOpenCobId.Heartbeat(1), (byte)NmtState.PreOperational);
        await ConfigureNodeIdAsync(rig);

        // Producer 2 is still silent: its error alone is announced, and producer 1's neither raises
        // nor resets anything.
        await rig.Node.SendEmcyAsync(0x1000, 0).WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 1, 0x8130, Communication);
        await ExpectEmcyAsync(rig.Wire, 2, 0x1000, Communication);
        await Inject(rig.Node, CanOpenCobId.Heartbeat(2), (byte)NmtState.PreOperational);
        await ExpectEmcyAsync(rig.Wire, 3, ErrorReset, 0);
        rig.Wire.Count(EmcyId).Should().Be(3);
    }

    [Fact]
    public async Task Communication_Error_Held_On_An_Unconfigured_Slave_Ends_Without_An_Emcy_Before_The_Boot_Up()
    {
        using var rig = new ControllerEmcyRig(enabled: false, unconfiguredLss: true);
        await TimeOutHeartbeatsWhileUnconfiguredAsync(rig, 1);
        await Inject(rig.Node, CanOpenCobId.Heartbeat(1), (byte)NmtState.PreOperational);
        await ConfigureNodeIdAsync(rig);

        await rig.Node.SendEmcyAsync(0x1000, 0).WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 1, 0x1000, 0);
        rig.Wire.Count(EmcyId).Should().Be(1, "neither 8130h nor 0000h for an error the bus never saw");
        rig.Node.ObjectDictionary.ReadUnsigned(0x1001, 0).Should().Be(0);
    }

    [Theory]
    [InlineData(NmtCommand.ResetCommunication)]
    [InlineData(NmtCommand.ResetNode)]
    public async Task Communication_Error_Held_On_An_Unconfigured_Slave_Is_Dropped_By_A_Reset(NmtCommand reset)
    {
        using var rig = new ControllerEmcyRig(enabled: false, unconfiguredLss: true);
        await TimeOutHeartbeatsWhileUnconfiguredAsync(rig, 1);
        // An unconfigured slave obeys a broadcast NMT command, and stays unconfigured through it.
        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)reset, 0);
        rig.Node.NodeId.Should().Be(CanOpenCobId.UnconfiguredNodeId);
        await ConfigureNodeIdAsync(rig);

        await rig.Node.SendEmcyAsync(0x1000, 0).WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 1, 0x1000, 0);
        rig.Wire.Count(EmcyId).Should().Be(1, "the reset ended the error, and its held EMCY with it");
    }
}
