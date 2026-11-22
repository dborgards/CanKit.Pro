using System;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;
using static CanKit.Pro.Tests.TestCases.CANopen.FlyingMasterRig;

namespace CanKit.Pro.Tests.TestCases.CANopen;

public partial class CanOpenBootIdentityTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task KeepAlive_Guarding_Waits_For_Confirmation_And_Cancels_Its_Lease(bool fastReply, bool cancel)
    {
        string session = NewSession();
        using var bus = Open(session, 0);
        using var peer = Open(session, 1);
        var clock = new ManualTimeSource();
        using var confirmations = new ClockedCanBusService(bus, clock, sendGate: clock);
        using var gate = new GuardPollGate(confirmations, fastReply);
        using var node = new CanOpenNode(gate, LeftId, KeepAliveOptions(), ownsService: false, timeSource: clock);
        using var rig = new MasterRig(clock, node, new ActorWitness(node, peer, WitnessForLeft, confirmations),
            new FrameLog(bus, peer), peer, bus);
        using var allowed = rig.Witness.AllowPeerVerificationWait(Slave);
        Configure(rig);
        await ElectActive(rig);
        rig.Log.Clear();
        try
        {
            TransmitHeartbeat(peer, Slave, 0x7F);
            await gate.Entered.Task.WaitAsync(ShortTimeout);
            TransmitHeartbeat(peer, Slave, 5);
            await AdvanceAsync(clock, rig.Witness, null, TimeSpan.FromMilliseconds(101));
            Result(rig).Status.Should().Be(SlaveBootStatus.CheckingKeepAlive);
            if (fastReply)
            {
                // A later, out-of-window reply must not overwrite the timely reply buffered
                // before local confirmation. Guarding cannot identify messages by transaction ID.
                TransmitHeartbeat(peer, Slave, 0x7F);
                await QuiesceAsync(rig.Witness, null);
            }
            if (cancel)
            {
                node.StopFlyingMaster();
                await gate.Cancelled.Task.WaitAsync(ShortTimeout);
                await UntilAsync(clock, rig.Witness, null, () => node.PeerSdoChannelIsFreeForTests(Slave), 20,
                    "cancelled keep-alive releases the channel while TX is still held");
            }
            gate.Release.TrySetResult(true);
            allowed.Dispose();
            await QuiesceAsync(rig.Witness, null);
            if (cancel) node.GetSlaveBootResults().Should().BeEmpty();
            else if (fastReply) Result(rig).Status.Should().Be(SlaveBootStatus.InitiallyOperational,
                "a timely actual reply remains valid while local TX confirmation is held");
            else
            {
                Result(rig).Status.Should().Be(SlaveBootStatus.CheckingKeepAlive,
                    "an unsolicited state before handoff is not a reply to this request");
                TransmitHeartbeat(peer, Slave, 5);
                await QuiesceAsync(rig.Witness, null);
                Result(rig).Status.Should().Be(SlaveBootStatus.InitiallyOperational);
            }
            rig.Log.Snapshot().Should().NotContain(f => f.Id == 0);
        }
        finally { gate.Release.TrySetResult(true); }
    }

    [Fact]
    public async Task KeepAlive_Active_Probe_Retries_Optional_Failure_After_Identity()
    {
        using var rig = OpenMaster(KeepAliveOptions(active: true));
        using var peer = new SilentBootPeer(rig.Peer, Slave, r => BootReply(r, 123));
        using var probe = new BootGuardProbe(rig.Peer);
        Configure(rig, mandatory: false, simultaneous: true);
        await ElectActive(rig);
        await QuiesceAsync(rig.Witness, null);
        peer.Requests.Should().Be(1);
        Result(rig).Status.Should().Be(SlaveBootStatus.CheckingKeepAlive);
        rig.Node.State.Should().Be(NmtState.PreOperational);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(100));
        Result(rig).Status.Should().Be(SlaveBootStatus.KeepAliveFailed);
        rig.Node.State.Should().Be(NmtState.Operational);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(100));
        peer.Requests.Should().Be(2);
        probe.Requests.Should().Be(2);
        TransmitHeartbeat(rig.Peer, Slave, 5);
        await QuiesceAsync(rig.Witness, null);
        Result(rig).Status.Should().Be(SlaveBootStatus.InitiallyOperational);
        Started(rig, 0).Should().BeFalse();
        Started(rig, Slave).Should().BeFalse();
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task KeepAlive_Guarding_Fails_On_Invalid_Confirmation_Or_Buffer_Overflow(int mode)
    {
        string session = NewSession();
        using var bus = Open(session, 0);
        using var peer = Open(session, 1);
        var clock = new ManualTimeSource();
        using var confirmations = new ClockedCanBusService(bus, clock, sendGate: clock);
        using var gate = new GuardPollGate(confirmations) { FailureMode = mode };
        using var node = new CanOpenNode(gate, LeftId, KeepAliveOptions(), ownsService: false, timeSource: clock);
        using var rig = new MasterRig(clock, node, new ActorWitness(node, peer, WitnessForLeft, confirmations),
            new FrameLog(bus, peer), peer, bus);
        using var allowed = rig.Witness.AllowPeerVerificationWait(Slave);
        Configure(rig, mandatory: false);
        await ElectActive(rig);
        try
        {
            TransmitHeartbeat(peer, Slave, 0x7F);
            if (mode == 3)
            {
                await gate.Entered.Task.WaitAsync(ShortTimeout);
                await node.PostToActorAsync(() =>
                {
                    for (int i = 0; i < 129; i++)
                        InvokeBoot(node, "ObserveKeepAliveState", (uint)(0x700 + Slave), new byte[] { 5 }, false, false, clock.GetTimestamp());
                });
            }
            await UntilAsync(clock, rig.Witness, null, () => Result(rig).Status == SlaveBootStatus.KeepAliveFailed, 20,
                "a failed or untimed transmission and bounded-buffer overflow cannot approve the peer");
            Result(rig).Error.Should().BeOfType<InvalidOperationException>();
            if (mode == 3) await gate.Cancelled.Task.WaitAsync(ShortTimeout);
            node.PeerSdoChannelIsFreeForTests(Slave).Should().BeTrue();
            Started(rig, Slave).Should().BeFalse();
        }
        finally { gate.Release.TrySetResult(true); }
    }

}
