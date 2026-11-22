using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using Xunit;
using static CanKit.Pro.Tests.TestCases.CANopen.FlyingMasterRig;

namespace CanKit.Pro.Tests.TestCases.CANopen;

// FR-CO-073: an Operational shortcut is valid only while the peer stays Operational.
public partial class CanOpenBootIdentityTests
{
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
