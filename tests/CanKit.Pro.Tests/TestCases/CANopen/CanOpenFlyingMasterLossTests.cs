using System;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;
using static CanKit.Pro.Tests.TestCases.CANopen.FlyingMasterRig;

namespace CanKit.Pro.Tests.TestCases.CANopen;

// FR-CO-031: CiA 302-2 §5.5.2 starts recovery with Reset Communication, target zero.
public partial class CanOpenFlyingMasterTests
{
    private static MasterRig OpenMasterLossGate(out ColdResetGate gate, CanOpenNodeOptions? options = null)
    {
        string session = NewSession();
        var bus = Open(session, 0);
        var peer = Open(session, 1);
        var clock = new ManualTimeSource();
        gate = new ColdResetGate(new ClockedCanBusService(bus, clock, sendGate: clock));
        var node = new CanOpenNode(gate, LeftId, options ?? new CanOpenNodeOptions(), ownsService: true, timeSource: clock);
        return new MasterRig(clock, node, new ActorWitness(node, peer, WitnessForLeft, gate.Confirmations),
            new FrameLog(bus, peer), peer, bus);
    }

    private static async Task AwaitMasterLossAsync(MasterRig rig)
    {
        Tighten(rig.Node);
        rig.Node.StartFlyingMaster(1, TimeSpan.FromMilliseconds(100));
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Node.FlyingMasterRole == FlyingMasterRole.Detecting, 100, "initial detection starts");
        Transmit(rig.Peer, CanOpenCobId.FlyingMasterClaim, 0, RightId);
        TransmitHeartbeat(rig.Peer, RightId, 5);
        await QuiesceAsync(rig.Witness, null);
        rig.Node.FlyingMasterRole.Should().Be(FlyingMasterRole.Standby);
        rig.Log.Clear();
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(100));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task A_Lost_Master_Requires_A_Confirmed_Reset_Before_Reelection(int outcome)
    {
        using var rig = OpenMasterLossGate(out var gate);
        gate.Reject = outcome == 1;
        gate.Cancel = outcome == 2;
        gate.Fault = outcome == 3 ? new InvalidOperationException("reset failed") : null;
        int resets = 0, losses = 0;
        rig.Node.ApplicationReset += (_, _) => resets++;
        rig.Node.FlyingMasterChanged += (_, e) => { if (e.Signal == FlyingMasterSignal.ActiveMasterLost) losses++; };
        await AwaitMasterLossAsync(rig);
        gate.Entered.IsCompleted.Should().BeTrue("master loss must request a broadcast reset");
        rig.Node.ActiveFlyingMasterNodeId.Should().BeNull();
        rig.Node.FlyingMasterRole.Should().Be(FlyingMasterRole.Detecting);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(800));
        rig.Log.Snapshot().Should().NotContain(f => f.Id == CanOpenCobId.FlyingMasterDetect
            || f.Id == CanOpenCobId.FlyingMasterTrigger
            || f.Id == CanOpenCobId.FlyingMasterClaim);
        resets.Should().Be(0, "a held or failed send must not reset this node locally");
        losses.Should().Be(1);
        if (outcome != 0)
        {
            rig.Node.FlyingMasterRole.Should().Be(FlyingMasterRole.Detecting);
            return;
        }
        await ReleaseHeldResetAsync(gate, rig.Node, rig.Witness);
        rig.Node.FlyingMasterRole.Should().Be(FlyingMasterRole.Delaying);
        resets.Should().Be(1);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Node.FlyingMasterRole == FlyingMasterRole.Active, 600, "confirmed reset starts the warm election");
        var frames = rig.Log.Snapshot();
        frames.Count(f => IsNmt(f, NmtCommand.ResetCommunication, 0)).Should().Be(1);
        frames.FindIndex(f => IsNmt(f, NmtCommand.ResetCommunication, 0)).Should()
            .BeLessThan(frames.FindIndex(f => f.Id == CanOpenCobId.FlyingMasterTrigger));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_Lost_Master_Reset_Cannot_Restart_A_Stopped_Or_Disposed_Node(bool dispose)
    {
        using var rig = OpenMasterLossGate(out var gate);
        int resets = 0;
        rig.Node.ApplicationReset += (_, _) => resets++;
        await AwaitMasterLossAsync(rig);
        gate.Entered.IsCompleted.Should().BeTrue();
        if (dispose) await AssertResetOutcomeSwallowedOnDisposeAsync(rig.Node);
        else
        {
            rig.Node.StopFlyingMaster();
            await ReleaseHeldResetAsync(gate, rig.Node, rig.Witness);
            await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(800));
            rig.Node.FlyingMasterRole.Should().Be(FlyingMasterRole.Inactive);
        }
        resets.Should().Be(0);
        rig.Log.Snapshot().Should().NotContain(f => f.Id == CanOpenCobId.FlyingMasterTrigger
            || f.Id == CanOpenCobId.FlyingMasterClaim);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeepAlive_Does_Not_Suppress_A_Required_Election_Reset(bool powerOn)
    {
        using var rig = OpenMasterLossGate(out var gate, new CanOpenNodeOptions { EnableBootKeepAlive = true });
        const byte slave = 0x22;
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F81, slave, Assigned | BootSlave | 0x10u);
        rig.Node.AddHeartbeatConsumer(slave, TimeSpan.FromSeconds(1));
        TransmitHeartbeat(rig.Peer, slave, 5);
        await QuiesceAsync(rig.Witness, null);
        if (powerOn)
        {
            Tighten(rig.Node);
            rig.Node.StartFlyingMaster(1, TimeSpan.FromMilliseconds(100));
            await UntilAsync(rig.Clock, rig.Witness, null,
                () => rig.Node.FlyingMasterRole == FlyingMasterRole.Detecting, 100, "cold detection starts");
            await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(20));
        }
        else await AwaitMasterLossAsync(rig);
        gate.Entered.IsCompleted.Should().BeTrue("§3.7 d and §5.5.2 require target zero before the warm election");
        await ReleaseHeldResetAsync(gate, rig.Node, rig.Witness);
        rig.Log.Snapshot().Should().ContainSingle(f => IsNmt(f, NmtCommand.ResetCommunication, 0));
    }
}
