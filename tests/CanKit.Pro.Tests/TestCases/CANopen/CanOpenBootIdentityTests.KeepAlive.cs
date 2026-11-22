using System;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using Xunit;
using static CanKit.Pro.Tests.TestCases.CANopen.FlyingMasterRig;

namespace CanKit.Pro.Tests.TestCases.CANopen;

// FR-CO-073: current state, guarding fallback, and the Operational keep-alive branch.
public partial class CanOpenBootIdentityTests
{
    private static CanOpenNodeOptions KeepAliveOptions(bool active = false) => new()
    {
        EnableBootKeepAlive = true,
        ActiveSlaveBootRetryInterval = active ? TimeSpan.FromMilliseconds(100) : null,
        SdoTimeout = TimeSpan.FromMilliseconds(100)
    };

    [Fact]
    public void KeepAlive_Is_Default_Off_And_Copied()
    {
        new CanOpenNodeOptions().EnableBootKeepAlive.Should().BeFalse();
        KeepAliveOptions().With().EnableBootKeepAlive.Should().BeTrue();
    }

    [Fact]
    public async Task KeepAlive_Operational_Heartbeat_Skips_Configuration_And_Start()
    {
        using var rig = OpenMaster(new CanOpenNodeOptions { EnableBootKeepAlive = true, RequireBootConfigurationMatch = true });
        ConfigureErrorControl(rig);
        await ElectActive(rig);
        rig.Log.Clear();
        TransmitHeartbeat(rig.Peer, Slave, 5);
        await QuiesceAsync(rig.Witness, null);
        Result(rig).Status.Should().Be(SlaveBootStatus.WaitingForErrorControl);
        TransmitHeartbeat(rig.Peer, Slave, 5);
        await QuiesceAsync(rig.Witness, null);
        Result(rig).Status.Should().Be(SlaveBootStatus.InitiallyOperational);
        rig.Node.State.Should().Be(NmtState.Operational);
        rig.Log.Snapshot().Should().NotContain(f => f.Id == 0 || f.Id == 0x600 + Slave);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeepAlive_Guarding_Confirms_Operational_Without_Reset(bool toggle)
    {
        using var rig = OpenMaster(KeepAliveOptions());
        using var probe = new BootGuardProbe(rig.Peer);
        Configure(rig);
        await ElectActive(rig);
        rig.Log.Clear();
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await QuiesceAsync(rig.Witness, null);
        await probe.FirstPoll.Task.WaitAsync(ShortTimeout);
        Result(rig).Status.Should().Be(SlaveBootStatus.CheckingKeepAlive);
        TransmitHeartbeat(rig.Peer, Slave, (byte)(toggle ? 0x85 : 5));
        await QuiesceAsync(rig.Witness, null);
        Result(rig).Status.Should().Be(SlaveBootStatus.InitiallyOperational);
        probe.Requests.Should().Be(1);
        rig.Log.Snapshot().Should().NotContain(f => f.Id == 0);
    }

    [Fact]
    public async Task KeepAlive_Guarding_Expires_At_100_Milliseconds()
    {
        using var rig = OpenMaster(KeepAliveOptions());
        using var probe = new BootGuardProbe(rig.Peer);
        Configure(rig, mandatory: false);
        await ElectActive(rig);
        rig.Log.Clear();
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await QuiesceAsync(rig.Witness, null);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(99));
        Result(rig).Status.Should().Be(SlaveBootStatus.CheckingKeepAlive);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(1));
        Result(rig).Status.Should().Be(SlaveBootStatus.KeepAliveFailed);
        Result(rig).Error.Should().BeOfType<TimeoutException>();
        TransmitHeartbeat(rig.Peer, Slave, 5);
        await QuiesceAsync(rig.Witness, null);
        Started(rig, Slave).Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0x80)]
    [InlineData(0x06)]
    public async Task KeepAlive_Guarding_Non_State_Bytes_Do_Not_Approve_Operational(byte value)
    {
        using var rig = OpenMaster(KeepAliveOptions());
        Configure(rig, mandatory: false);
        await ElectActive(rig);
        rig.Log.Clear();
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await QuiesceAsync(rig.Witness, null);
        TransmitHeartbeat(rig.Peer, Slave, value);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(100));
        if (value == 0)
        {
            // A genuine boot-up cancels the probe and resumes normal boot. It does not
            // approve an Operational shortcut; this peer still requires explicit Start.
            Result(rig).Status.Should().Be(SlaveBootStatus.StartRequested);
            Started(rig, Slave).Should().BeTrue();
        }
        else
        {
            Result(rig).Status.Should().Be(SlaveBootStatus.KeepAliveFailed);
            Started(rig, Slave).Should().BeFalse();
        }
    }

    [Theory]
    [InlineData(0x04)]
    [InlineData(0x7F)]
    public async Task KeepAlive_Nonoperational_Peer_Requires_Reset_And_Bootup(byte state)
    {
        using var rig = OpenMaster(KeepAliveOptions());
        ConfigureErrorControl(rig);
        await ElectActive(rig);
        rig.Log.Clear();
        TransmitHeartbeat(rig.Peer, Slave, state);
        await QuiesceAsync(rig.Witness, null);
        rig.Log.Snapshot().Should().ContainSingle(f => IsNmt(f, NmtCommand.ResetCommunication, Slave));
        Result(rig).Status.Should().Be(SlaveBootStatus.CheckingKeepAlive);
        TransmitHeartbeat(rig.Peer, Slave, 5);
        await QuiesceAsync(rig.Witness, null);
        Result(rig).Status.Should().Be(SlaveBootStatus.CheckingKeepAlive, "a heartbeat is not reset boot-up");
        TransmitHeartbeat(rig.Peer, Slave, 0);
        await QuiesceAsync(rig.Witness, null);
        Result(rig).Status.Should().Be(SlaveBootStatus.WaitingForErrorControl);
        TransmitHeartbeat(rig.Peer, Slave, 5);
        await QuiesceAsync(rig.Witness, null);
        Result(rig).Status.Should().Be(SlaveBootStatus.StartRequested);
        Started(rig, Slave).Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task KeepAlive_Guarding_Cannot_Outlive_Role_Or_Assignment(int action)
    {
        using var rig = OpenMaster(KeepAliveOptions());
        Configure(rig);
        await ElectActive(rig);
        rig.Log.Clear();
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await QuiesceAsync(rig.Witness, null);
        if (action == 0) rig.Node.StopFlyingMaster();
        else if (action == 1) await rig.Node.PostToActorAsync(() => InvokeBoot(rig.Node, "EnterStandby", (ushort)0, (byte)1));
        else await rig.Node.PostToActorAsync(() => rig.Node.ObjectDictionary.WriteUnsigned(0x1F81, Slave, 0));
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(200));
        rig.Node.GetSlaveBootResults().Should().NotContain(r => r.NodeId == Slave);
        if (action == 2)
        {
            // Removing the assignment starts a new pass; this ID is now an unlisted target.
            rig.Log.Snapshot().Should().ContainSingle(f => IsNmt(f, NmtCommand.ResetCommunication, Slave));
            rig.Log.Snapshot().Should().NotContain(f => f.Id == 0 && f.Data[0] == (byte)NmtCommand.Start);
        }
        else rig.Log.Snapshot().Should().NotContain(f => f.Id == 0);
    }
}
