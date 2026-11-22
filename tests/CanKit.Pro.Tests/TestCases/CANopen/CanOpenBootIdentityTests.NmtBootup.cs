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

// FR-CO-072: the opt-in boot-up handler invalidates only the rebooted peer's approvals.
public partial class CanOpenBootIdentityTests
{
    private static CanOpenNodeOptions BootupOptions(bool enabled = true) => new()
    {
        EnableNmtBootupHandler = enabled,
        EnableBootErrorControl = !enabled,
        SdoTimeout = TimeSpan.FromMilliseconds(100)
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bootup_Handler_Is_Default_Off_And_Copied(bool enabled)
    {
        new CanOpenNodeOptions().EnableNmtBootupHandler.Should().BeFalse();
        var options = BootupOptions(enabled);
        options.With().EnableNmtBootupHandler.Should().Be(enabled);
        using var rig = OpenMaster(options);
        ConfigureErrorControl(rig);
        await StartMonitoredSlave(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0);
        await QuiesceAsync(rig.Witness, null);
        Result(rig).Status.Should().Be(enabled ? SlaveBootStatus.WaitingForErrorControl : SlaveBootStatus.StartRequested);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await QuiesceAsync(rig.Witness, null);
        Started(rig, Slave).Should().Be(enabled);
    }

    [Fact]
    public async Task Bootup_Rechecks_Only_The_Rebooted_Peer_Without_Another_Reset()
    {
        using var rig = OpenMaster(BootupOptions());
        ConfigureErrorControl(rig);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F81, 6, Assigned | BootSlave | 0x10u);
        await StartMonitoredSlave(rig);
        TransmitHeartbeat(rig.Peer, 6, 0);
        await QuiesceAsync(rig.Witness, null);
        var other = rig.Node.GetSlaveBootResults().Single(r => r.NodeId == 6);
        rig.Log.Clear();
        TransmitHeartbeat(rig.Peer, Slave, 0);
        await QuiesceAsync(rig.Witness, null);
        Result(rig).Status.Should().Be(SlaveBootStatus.WaitingForErrorControl);
        Started(rig, Slave).Should().BeFalse();
        TransmitHeartbeat(rig.Peer, Slave, 5);
        await QuiesceAsync(rig.Witness, null);
        Result(rig).Status.Should().Be(SlaveBootStatus.StartRequested, "the pre-reboot Operational state cannot bypass the fresh boot gates");
        rig.Log.Snapshot().Where(f => f.Id == 0).Should().ContainSingle(f => IsNmt(f, NmtCommand.Start, Slave));
        rig.Node.GetSlaveBootResults().Single(r => r.NodeId == 6).Should().BeSameAs(other);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bootup_Repeats_Identity_And_Configuration(bool configuration)
    {
        using var rig = OpenMaster(new CanOpenNodeOptions
        { EnableNmtBootupHandler = true, RequireBootConfigurationMatch = configuration, SdoTimeout = TimeSpan.FromMilliseconds(100) });
        bool valid = true;
        using var peer = new SilentBootPeer(rig.Peer, Slave, r => valid ? ConfigurationReply(r) : BootReply(r, 456));
        if (configuration)
        {
            ConfigureVersion(rig);
            rig.Node.AddHeartbeatConsumer(Slave, TimeSpan.FromMilliseconds(100));
        }
        else
        {
            ConfigureErrorControl(rig);
            rig.Node.ObjectDictionary.WriteUnsigned(0x1F84, Slave, 123);
        }
        await StartMonitoredSlave(rig);
        valid = false;
        TransmitHeartbeat(rig.Peer, Slave, 0);
        await QuiesceAsync(rig.Witness, null);
        Result(rig).Status.Should().Be(configuration ? SlaveBootStatus.ConfigurationRequired : SlaveBootStatus.IdentityMismatch);
        Started(rig, Slave).Should().BeFalse();
        rig.Log.Snapshot().Should().Contain(f => f.Id == 0x600 + Slave);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bootup_Only_Informs_For_Application_Owned_Peers(bool suppress)
    {
        using var rig = OpenMaster(BootupOptions());
        ConfigureErrorControl(rig);
        if (suppress) rig.Node.ObjectDictionary.WriteUnsigned(Startup, 0, SuppressSlaveStart);
        else rig.Node.ObjectDictionary.WriteUnsigned(0x1F81, Slave, Assigned | MandatorySlave | 0x10u);
        await ElectActive(rig);
        int events = 0;
        rig.Node.FlyingMasterChanged += (_, e) => { if (e.Signal == FlyingMasterSignal.SlaveBootupReceived) Interlocked.Increment(ref events); };
        rig.Log.Clear();
        TransmitHeartbeat(rig.Peer, Slave, 0);
        TransmitHeartbeat(rig.Peer, 6, 0); // Not in the master's list: inform the application too.
        await QuiesceAsync(rig.Witness, null);
        Volatile.Read(ref events).Should().Be(2);
        Result(rig).Status.Should().Be(SlaveBootStatus.Announced);
        rig.Node.GetSlaveBootResults().Should().NotContain(r => r.NodeId == 6);
        rig.Log.Snapshot().Should().NotContain(f => f.Id == 0 || f.Id == 0x600 + Slave);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bootup_After_Runtime_Error_Respects_The_Expected_Reset_Handshake(bool handler)
    {
        using var rig = OpenMaster(new CanOpenNodeOptions
        { EnableNmtErrorHandler = true, EnableNmtBootupHandler = handler, SdoTimeout = TimeSpan.FromMilliseconds(100) });
        ConfigureErrorControl(rig);
        await StartMonitoredSlave(rig);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(100));
        TransmitHeartbeat(rig.Peer, Slave, 0);
        await QuiesceAsync(rig.Witness, null);
        Result(rig).Status.Should().Be(SlaveBootStatus.WaitingForErrorControl);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await QuiesceAsync(rig.Witness, null);
        Started(rig, Slave).Should().BeTrue();
        rig.Log.Snapshot().Should().ContainSingle(f => IsNmt(f, NmtCommand.ResetNode, Slave));
    }

    [Fact]
    public async Task Fresh_Bootup_Releases_A_Peer_Recovery_Failure_But_Not_A_Network_Halt()
    {
        using var rig = OpenMaster(new CanOpenNodeOptions
        { EnableNmtErrorHandler = true, EnableNmtBootupHandler = true, SdoTimeout = TimeSpan.FromMilliseconds(50) });
        ConfigureErrorControl(rig);
        await StartMonitoredSlave(rig);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(100));
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(50));
        Result(rig).Status.Should().Be(SlaveBootStatus.ErrorRecoveryFailed);
        TransmitHeartbeat(rig.Peer, Slave, 0);
        await QuiesceAsync(rig.Witness, null);
        Result(rig).Status.Should().Be(SlaveBootStatus.WaitingForErrorControl);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await QuiesceAsync(rig.Witness, null);
        Started(rig, Slave).Should().BeTrue();
        await rig.Node.PostToActorAsync(() => InvokeBoot(rig.Node, "HaltBootWork"));
        rig.Log.Clear();
        TransmitHeartbeat(rig.Peer, Slave, 0);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await QuiesceAsync(rig.Witness, null);
        Started(rig, Slave).Should().BeFalse();
    }
}
