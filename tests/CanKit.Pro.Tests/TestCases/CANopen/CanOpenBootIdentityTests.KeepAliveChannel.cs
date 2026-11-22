using System;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using Xunit;
using static CanKit.Pro.Tests.TestCases.CANopen.FlyingMasterRig;

namespace CanKit.Pro.Tests.TestCases.CANopen;

// FR-CO-073: channel contention must not turn an old heartbeat into a reset decision.
public partial class CanOpenBootIdentityTests
{
    [Fact]
    public async Task KeepAlive_Bootup_Cancels_A_Heartbeat_Check_Before_Channel_Release()
    {
        using var rig = OpenMaster(new CanOpenNodeOptions
        { EnableBootKeepAlive = true, RequireBootConfigurationMatch = true });
        using var peer = new SilentBootPeer(rig.Peer, Slave, ConfigurationReply);
        ConfigureVersion(rig);
        rig.Node.AddHeartbeatConsumer(Slave, TimeSpan.FromMilliseconds(100));
        await ElectActive(rig);
        rig.Log.Clear();
        using (rig.Witness.HoldPeerSdoChannel(Slave, ConfigurationChannel(rig)))
        {
            TransmitHeartbeat(rig.Peer, Slave, 0x7F);
            await QuiesceAsync(rig.Witness, null);
            Result(rig).Status.Should().Be(SlaveBootStatus.CheckingKeepAlive);
            TransmitHeartbeat(rig.Peer, Slave, 0);
            TransmitHeartbeat(rig.Peer, Slave, 5);
            await QuiesceAsync(rig.Witness, null);
        }
        await QuiesceAsync(rig.Witness, null);
        ConfigurationRequests(rig).Should().Equal((0x1020, (byte)1), (0x1020, (byte)2));
        Result(rig).Status.Should().Be(SlaveBootStatus.WaitingForErrorControl);
        rig.Node.State.Should().Be(NmtState.PreOperational);
        TransmitHeartbeat(rig.Peer, Slave, 5);
        await QuiesceAsync(rig.Witness, null);
        Result(rig).Status.Should().Be(SlaveBootStatus.StartRequested);
        rig.Log.Snapshot().Should().NotContain(f => IsNmt(f, NmtCommand.ResetCommunication, Slave));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task KeepAlive_Rechecks_Heartbeat_After_A_Channel_Wait(int change)
    {
        using var rig = OpenMaster(KeepAliveOptions());
        ConfigureErrorControl(rig, mandatory: false);
        await ElectActive(rig);
        rig.Log.Clear();
        using (rig.Witness.HoldPeerSdoChannel(Slave, ConfigurationChannel(rig)))
        {
            TransmitHeartbeat(rig.Peer, Slave, 0x7F);
            await QuiesceAsync(rig.Witness, null);
            Result(rig).Status.Should().Be(SlaveBootStatus.CheckingKeepAlive);
            if (change == 0) TransmitHeartbeat(rig.Peer, Slave, 5);
            else if (change == 1) await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(100));
            else TransmitHeartbeat(rig.Peer, Slave, 0);
            await QuiesceAsync(rig.Witness, null);
        }
        await QuiesceAsync(rig.Witness, null);
        rig.Log.Snapshot().Should().NotContain(f => IsNmt(f, NmtCommand.ResetCommunication, Slave),
            "the pre-wait heartbeat cannot authorize a reset after its state or validity changed");
        if (change is 0 or 2)
        {
            Result(rig).Status.Should().Be(SlaveBootStatus.WaitingForErrorControl);
            TransmitHeartbeat(rig.Peer, Slave, 5);
            await QuiesceAsync(rig.Witness, null);
            Result(rig).Status.Should().Be(change == 0 ? SlaveBootStatus.InitiallyOperational : SlaveBootStatus.StartRequested);
        }
        else
        {
            Result(rig).Status.Should().Be(SlaveBootStatus.KeepAliveFailed);
            Result(rig).Error.Should().BeOfType<TimeoutException>();
        }
        Started(rig, Slave).Should().Be(change == 2, "an observed reboot resumes normal boot and requires explicit Start");
    }
}
