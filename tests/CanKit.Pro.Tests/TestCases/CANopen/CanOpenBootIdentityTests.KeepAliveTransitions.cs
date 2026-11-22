using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;
using static CanKit.Pro.Tests.TestCases.CANopen.FlyingMasterRig;

namespace CanKit.Pro.Tests.TestCases.CANopen;

// FR-CO-073: an Operational shortcut is valid only while the peer stays Operational.
public partial class CanOpenBootIdentityTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task KeepAlive_Bootup_Invalidates_A_Pending_Guarding_Result(bool replied, bool staleBootup)
    {
        string session = NewSession();
        using var bus = Open(session, 0);
        using var peer = Open(session, 1);
        var clock = new ManualTimeSource();
        using var confirmations = new ClockedCanBusService(bus, clock, sendGate: clock);
        using var gate = new GuardPollGate(confirmations, holdAfterTransmit: true);
        using var node = new CanOpenNode(gate, LeftId, new CanOpenNodeOptions
        { EnableBootKeepAlive = true, RequireBootConfigurationMatch = true }, ownsService: false, timeSource: clock);
        using var rig = new MasterRig(clock, node, new ActorWitness(node, peer, WitnessForLeft, confirmations),
            new FrameLog(bus, peer), peer, bus);
        using var responder = new SilentBootPeer(peer, Slave, r => BootReply(r, 42));
        using var allowed = rig.Witness.AllowPeerVerificationWait(Slave);
        ConfigureVersion(rig);
        await ElectActive(rig);
        rig.Log.Clear();
        try
        {
            TransmitHeartbeat(peer, Slave, 0x7F);
            await gate.Entered.Task.WaitAsync(ShortTimeout);
            long beforeProbe = clock.GetTimestamp() - 1;
            if (replied) TransmitHeartbeat(peer, Slave, 5);
            await QuiesceAsync(rig.Witness, null);
            await AdvanceAsync(clock, rig.Witness, null, System.TimeSpan.FromMilliseconds(1));
            if (staleBootup)
                await node.PostToActorAsync(() => InvokeBoot(node, "ObserveKeepAliveState",
                    (uint)(0x700 + Slave), new byte[] { 0 }, false, false, beforeProbe));
            else TransmitHeartbeat(peer, Slave, 0);
            await QuiesceAsync(rig.Witness, null);
            gate.Release.TrySetResult(true);
            allowed.Dispose();
            await QuiesceAsync(rig.Witness, null);
            if (staleBootup)
            {
                Result(rig).Status.Should().Be(SlaveBootStatus.InitiallyOperational);
                ConfigurationRequests(rig).Should().BeEmpty();
                rig.Log.Snapshot().Should().NotContain(f => f.Id == 0);
            }
            else
            {
                Result(rig).Status.Should().Be(SlaveBootStatus.ConfigurationRequired,
                    "a pre-reboot guarding result cannot bypass the new boot's configuration gate");
                ConfigurationRequests(rig).Should().Contain((0x1020, (byte)1));
                node.State.Should().Be(NmtState.PreOperational);
                // The configured boot-error policy may send Reset Node for the mismatch.
                rig.Log.Snapshot().Should().NotContain(f => IsNmt(f, NmtCommand.Start, Slave)
                    || IsNmt(f, NmtCommand.ResetCommunication, Slave));
            }
            node.PeerSdoChannelIsFreeForTests(Slave).Should().BeTrue();
        }
        finally { gate.Release.TrySetResult(true); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0x04)]
    [InlineData(0x7F)]
    public async Task KeepAlive_State_Change_During_Error_Control_Resumes_Normal_Boot(byte state)
    {
        using var rig = OpenMaster(new CanOpenNodeOptions
        { EnableBootKeepAlive = true, RequireBootConfigurationMatch = true });
        using var peer = new SilentBootPeer(rig.Peer, Slave, ConfigurationReply);
        ConfigureVersion(rig);
        rig.Node.AddHeartbeatConsumer(Slave, System.TimeSpan.FromMilliseconds(100));
        await ElectActive(rig);
        rig.Log.Clear();
        TransmitHeartbeat(rig.Peer, Slave, 5);
        await QuiesceAsync(rig.Witness, null);
        Result(rig).Status.Should().Be(SlaveBootStatus.WaitingForErrorControl);
        TransmitHeartbeat(rig.Peer, Slave, state);
        await QuiesceAsync(rig.Witness, null);
        rig.Node.State.Should().Be(NmtState.PreOperational);
        Started(rig, Slave).Should().BeFalse();
        if (state != 0)
        {
            Result(rig).Status.Should().Be(SlaveBootStatus.CheckingKeepAlive);
            rig.Log.Snapshot().Should().ContainSingle(f => IsNmt(f, NmtCommand.ResetCommunication, Slave));
            TransmitHeartbeat(rig.Peer, Slave, 0);
            await QuiesceAsync(rig.Witness, null);
        }
        else rig.Log.Snapshot().Should().NotContain(f => f.Id == 0);
        ConfigurationRequests(rig).Should().Equal((0x1020, (byte)1), (0x1020, (byte)2));
        Result(rig).Status.Should().Be(SlaveBootStatus.WaitingForErrorControl);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await QuiesceAsync(rig.Witness, null);
        Result(rig).Status.Should().Be(SlaveBootStatus.StartRequested);
        Started(rig, Slave).Should().BeTrue();
        rig.Node.State.Should().Be(NmtState.Operational);
    }
}
