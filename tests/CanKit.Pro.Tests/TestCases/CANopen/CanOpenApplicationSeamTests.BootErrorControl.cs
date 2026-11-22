using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using Xunit;
using FM = CanKit.Pro.Tests.TestCases.CANopen.FlyingMasterRig;

namespace CanKit.Pro.Tests.TestCases.CANopen;

public partial class CanOpenApplicationSeamTests
{
    // FR-CO-071: the synchronous application callback can invalidate the timeout's boot.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Runtime_Timeout_Does_Not_React_After_The_Application_Invalidates_It(int action)
    {
        var app = Watcher();
        using var rig = FM.OpenMaster(new CanOpenNodeOptions
        {
            EnableNmtErrorHandler = true,
            EnableErrorBehavior = true
        }.WithApplication(app));
        const byte slave = 5;
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F81, slave, FM.Assigned | FM.BootSlave | FM.MandatorySlave | 0x10u);
        rig.Node.AddHeartbeatConsumer(slave, TimeSpan.FromMilliseconds(100));
        FM.Tighten(rig.Node);
        rig.Node.StartFlyingMaster(0, FM.Heartbeat);
        await FM.UntilAsync(rig.Clock, rig.Witness, null, () => rig.Node.FlyingMasterRole == FlyingMasterRole.Active,
            1000, "master election completes");
        FM.TransmitHeartbeat(rig.Peer, slave, 0);
        await FM.QuiesceAsync(rig.Witness, null);
        FM.TransmitHeartbeat(rig.Peer, slave, 0x7F);
        await FM.QuiesceAsync(rig.Witness, null);
        rig.Node.GetSlaveBootResults().Single(r => r.NodeId == slave).Status.Should().Be(SlaveBootStatus.StartRequested);
        rig.Log.Clear();
        bool called = false;
        app.WhenFailureChanged = active =>
        {
            if (!active) return;
            called = true;
            if (action == 0) rig.Node.RemoveHeartbeatConsumer(slave);
            else if (action == 1) rig.Node.AddHeartbeatConsumer(slave, TimeSpan.FromMilliseconds(200));
            else if (action == 2) rig.Node.StopFlyingMaster();
            else rig.Node.Dispose();
        };
        if (action == 3)
        {
            // The witness cannot post its barrier after the callback disposes the actor.
            await rig.Node.PostToActorAsync(() => typeof(CanOpenNode)
                .GetMethod("RaiseHeartbeatTimeout", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(rig.Node, new object[] { slave, TimeSpan.FromMilliseconds(100) }));
        }
        else await FM.AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(100));
        called.Should().BeTrue();
        rig.Log.Snapshot().Should().NotContain(f => f.Id == 0 && f.Data[0] == (byte)NmtCommand.ResetNode);
    }

    // FR-CO-070: recovery callbacks may change another watch while a boot restart is pending.
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    public async Task A_Reentrant_Heartbeat_Edit_Preserves_The_Pending_Boot_Restart(bool sameSlave, int endRole)
    {
        var app = Watcher();
        using var rig = FM.OpenMaster(new CanOpenNodeOptions
        {
            EnableBootErrorControl = true,
            EnableErrorBehavior = true
        }.WithApplication(app));
        const byte slave = 5;
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F81, slave, FM.Assigned | FM.BootSlave | FM.MandatorySlave | 0x10u);
        rig.Node.AddHeartbeatConsumer(slave, TimeSpan.FromMilliseconds(100));
        rig.Node.AddHeartbeatConsumer(6, TimeSpan.FromMilliseconds(2000));
        FM.Tighten(rig.Node);
        rig.Node.StartFlyingMaster(0, FM.Heartbeat);
        await FM.UntilAsync(rig.Clock, rig.Witness, null, () => rig.Node.FlyingMasterRole == FlyingMasterRole.Active,
            1000, "master election completes");
        FM.TransmitHeartbeat(rig.Peer, slave, 0);
        await FM.QuiesceAsync(rig.Witness, null);
        FM.TransmitHeartbeat(rig.Peer, slave, 0x7F);
        await FM.QuiesceAsync(rig.Witness, null);
        rig.Node.GetSlaveBootResults().Single(r => r.NodeId == slave).Status.Should().Be(SlaveBootStatus.StartRequested);
        await FM.AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(100));
        app.Host!.CommunicationFailureActive.Should().BeTrue();

        var field = typeof(CanOpenNode).GetField("_bootGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!;
        int generation = (int)field.GetValue(rig.Node)!;
        int endedGeneration = -1;
        app.WhenFailureChanged = active =>
        {
            if (active) return;
            if (endRole != 0)
            {
                if (endRole == 1) rig.Node.StopFlyingMaster();
                else rig.Node.Dispose();
                endedGeneration = (int)field.GetValue(rig.Node)!;
                return;
            }
            app.Host.ObjectDictionary.WriteUnsigned(0x1016, sameSlave ? (byte)1 : (byte)2,
                ((uint)(sameSlave ? slave : 6) << 16) | 300u);
        };
        rig.Node.AddHeartbeatConsumer(slave, TimeSpan.FromMilliseconds(200));
        if (endRole != 0)
        {
            ((int)field.GetValue(rig.Node)!).Should().Be(endedGeneration,
                "the recovery callback ended the master role or disposed the node");
            return;
        }
        ((int)field.GetValue(rig.Node)!).Should().Be(generation + 1,
            "the outer change owes exactly one restart, even when recovery edits an unrelated watch");
        rig.Node.GetSlaveBootResults().Single(r => r.NodeId == slave).Status.Should().Be(SlaveBootStatus.WaitingForAnnouncement);
        FM.TransmitHeartbeat(rig.Peer, slave, 0);
        await FM.QuiesceAsync(rig.Witness, null);
        await FM.AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(sameSlave ? 250 : 150));
        rig.Node.GetSlaveBootResults().Single(r => r.NodeId == slave).Status.Should().Be(SlaveBootStatus.WaitingForErrorControl,
            "the new boot uses the final consumer snapshot and a full new window");
        FM.TransmitHeartbeat(rig.Peer, slave, 0x7F);
        await FM.QuiesceAsync(rig.Witness, null);
        rig.Node.GetSlaveBootResults().Single(r => r.NodeId == slave).Status.Should().Be(SlaveBootStatus.StartRequested);
    }
}
