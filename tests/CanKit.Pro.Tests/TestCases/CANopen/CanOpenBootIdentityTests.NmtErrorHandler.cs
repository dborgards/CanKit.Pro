using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using Xunit;
using static CanKit.Pro.Tests.TestCases.CANopen.FlyingMasterRig;

namespace CanKit.Pro.Tests.TestCases.CANopen;

// FR-CO-071: runtime error-control reactions, distinct from initial boot failures.
public partial class CanOpenBootIdentityTests
{
    private static CanOpenNodeOptions RuntimeErrorOptions(bool enabled = true) => new()
    {
        EnableNmtErrorHandler = enabled,
        EnableBootErrorControl = !enabled,
        SdoTimeout = TimeSpan.FromMilliseconds(100)
    };

    private static async Task StartMonitoredSlave(MasterRig rig)
    {
        await EnterHeartbeatStage(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await QuiesceAsync(rig.Witness, null);
        Result(rig).Status.Should().Be(SlaveBootStatus.StartRequested);
        rig.Log.Clear();
    }

    [Theory]
    [InlineData(true, 0u, 5, NmtCommand.ResetNode)]
    [InlineData(true, 16u, 0, NmtCommand.ResetNode)]
    [InlineData(true, 64u, 0, NmtCommand.Stop)]
    [InlineData(true, 80u, 0, NmtCommand.Stop)]
    [InlineData(false, 16u, 5, NmtCommand.ResetNode)]
    [InlineData(false, 64u, 5, NmtCommand.ResetNode)]
    [InlineData(false, 80u, 5, NmtCommand.ResetNode)]
    public async Task Runtime_Error_Uses_Mandatory_And_Startup_Bits(bool mandatory, uint startup, byte target, NmtCommand command)
    {
        using var rig = OpenMaster(RuntimeErrorOptions());
        ConfigureErrorControl(rig, mandatory: mandatory);
        rig.Node.ObjectDictionary.WriteUnsigned(Startup, 0, startup);
        int events = 0;
        rig.Node.FlyingMasterChanged += (_, e) => { if (e.Signal == FlyingMasterSignal.SlaveErrorControlEvent) Interlocked.Increment(ref events); };
        await StartMonitoredSlave(rig);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(100));
        var commands = rig.Log.Snapshot().Where(f => f.Id == 0).ToArray();
        commands.Should().ContainSingle(f => IsNmt(f, command, target));
        commands.Should().HaveCount(1);
        Volatile.Read(ref events).Should().Be(1);
        Result(rig).Status.Should().Be(SlaveBootStatus.ErrorControlReactionRequested);
        if (target == 0)
        {
            TransmitHeartbeat(rig.Peer, Slave, 0);
            TransmitHeartbeat(rig.Peer, Slave, 0x7F);
            await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(200));
            rig.Log.Snapshot().Count(f => f.Id == 0).Should().Be(1, "broadcast error reaction halts boot once");
            rig.Node.State.Should().Be(NmtState.Operational, "the active master ignores its broadcast echo");
        }
    }

    [Fact]
    public async Task Individual_Error_Recovery_Reboots_Only_The_Failed_Slave_And_Requires_A_New_Heartbeat()
    {
        using var rig = OpenMaster(RuntimeErrorOptions());
        ConfigureErrorControl(rig);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F81, 6, Assigned | BootSlave | 0x10u);
        await StartMonitoredSlave(rig);
        TransmitHeartbeat(rig.Peer, 6, 0);
        await QuiesceAsync(rig.Witness, null);
        var other = rig.Node.GetSlaveBootResults().Single(r => r.NodeId == 6);
        rig.Log.Clear();
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(100));
        rig.Log.Snapshot().Should().Contain(f => IsNmt(f, NmtCommand.ResetNode, Slave));
        TransmitHeartbeat(rig.Peer, Slave, 5); // Operational alone cannot satisfy Reset Node's boot-up handshake.
        await QuiesceAsync(rig.Witness, null);
        Started(rig, Slave).Should().BeFalse();
        TransmitHeartbeat(rig.Peer, Slave, 0);
        await QuiesceAsync(rig.Witness, null);
        Result(rig).Status.Should().Be(SlaveBootStatus.WaitingForErrorControl);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await QuiesceAsync(rig.Witness, null);
        Started(rig, Slave).Should().BeTrue();
        rig.Log.Snapshot().Where(f => f.Id == 0).Should().OnlyContain(f => f.Data[1] == Slave);
        rig.Node.GetSlaveBootResults().Single(r => r.NodeId == 6).Should().BeSameAs(other);
        // Successful recovery releases the episode latch: another genuine loss reacts again.
        rig.Log.Clear();
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(100));
        rig.Log.Snapshot().Should().ContainSingle(f => IsNmt(f, NmtCommand.ResetNode, Slave));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Runtime_Error_Option_Is_Independent_Default_Off_And_Copied(bool enabled)
    {
        new CanOpenNodeOptions().EnableNmtErrorHandler.Should().BeFalse();
        var options = RuntimeErrorOptions(enabled);
        options.With().EnableNmtErrorHandler.Should().Be(enabled);
        using var rig = OpenMaster(options);
        ConfigureErrorControl(rig);
        await StartMonitoredSlave(rig);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(100));
        rig.Log.Snapshot().Any(f => IsNmt(f, NmtCommand.ResetNode, Slave)).Should().Be(enabled);
    }

    [Fact]
    public async Task Initial_Error_Control_Failure_Retains_The_Existing_Boot_Policy()
    {
        using var rig = OpenMaster(RuntimeErrorOptions());
        ConfigureErrorControl(rig);
        await EnterHeartbeatStage(rig);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(100));
        Result(rig).Status.Should().Be(SlaveBootStatus.ErrorControlFailed);
        rig.Log.Snapshot().Should().ContainSingle(f => IsNmt(f, NmtCommand.ResetNode, Slave));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Application_Owned_Slaves_Are_Reset_But_Not_Configured_Or_Started(bool suppress)
    {
        using var rig = OpenMaster(RuntimeErrorOptions());
        ConfigureErrorControl(rig);
        if (suppress) rig.Node.ObjectDictionary.WriteUnsigned(Startup, 0, SuppressSlaveStart);
        else rig.Node.ObjectDictionary.WriteUnsigned(0x1F81, Slave, Assigned | MandatorySlave | 0x10u);
        await ElectActive(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await QuiesceAsync(rig.Witness, null);
        rig.Log.Clear();
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(100));
        rig.Log.Snapshot().Should().ContainSingle(f => IsNmt(f, NmtCommand.ResetNode, Slave));
        TransmitHeartbeat(rig.Peer, Slave, 0);
        await QuiesceAsync(rig.Witness, null);
        Started(rig, Slave).Should().BeFalse();
        rig.Log.Snapshot().Should().NotContain(f => f.Id >= 0x600 && f.Id <= 0x67F);
        Result(rig).Status.Should().Be(SlaveBootStatus.Announced);
    }

    [Fact]
    public async Task Failed_Recovery_Is_Latched_And_Does_Not_Flood_The_Peer_With_Resets()
    {
        using var rig = OpenMaster(RuntimeErrorOptions());
        ConfigureErrorControl(rig);
        await StartMonitoredSlave(rig);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(100));
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(100));
        Result(rig).Status.Should().Be(SlaveBootStatus.ErrorRecoveryFailed);
        Result(rig).Error.Should().BeOfType<TimeoutException>();
        for (int i = 0; i < 3; i++) await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(100));
        TransmitHeartbeat(rig.Peer, Slave, 0);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await QuiesceAsync(rig.Witness, null);
        rig.Log.Snapshot().Should().ContainSingle(f => IsNmt(f, NmtCommand.ResetNode, Slave));
        Started(rig, Slave).Should().BeFalse();
        Result(rig).Status.Should().Be(SlaveBootStatus.ErrorRecoveryFailed);
    }

    [Fact]
    public async Task Automatic_Guarding_Exhaustion_Uses_The_Same_Runtime_Recovery()
    {
        using var rig = OpenMaster(RuntimeErrorOptions());
        using var probe = new BootGuardProbe(rig.Peer);
        ConfigureErrorControl(rig, heartbeatMs: 0, guardMs: 20, retries: 1);
        await ElectActive(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0);
        await QuiesceAsync(rig.Witness, null);
        await probe.FirstPoll.Task.WaitAsync(ShortTimeout);
        rig.Log.Clear();
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(20));
        rig.Log.Snapshot().Should().NotContain(f => IsNmt(f, NmtCommand.ResetNode, Slave));
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(20));
        rig.Log.Snapshot().Should().ContainSingle(f => IsNmt(f, NmtCommand.ResetNode, Slave));
        TransmitHeartbeat(rig.Peer, Slave, 0);
        await QuiesceAsync(rig.Witness, null);
        Started(rig, Slave).Should().BeTrue();
    }
}
