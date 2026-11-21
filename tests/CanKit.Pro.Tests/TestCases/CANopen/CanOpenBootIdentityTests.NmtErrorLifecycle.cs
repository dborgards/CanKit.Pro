using System;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using Xunit;
using static CanKit.Pro.Tests.TestCases.CANopen.FlyingMasterRig;

namespace CanKit.Pro.Tests.TestCases.CANopen;

public partial class CanOpenBootIdentityTests
{
    [Theory]
    [InlineData(NmtCommand.Stop, false)]
    [InlineData(NmtCommand.Stop, true)]
    [InlineData(NmtCommand.ResetNode, false)]
    [InlineData(NmtCommand.ResetNode, true)]
    public async Task Runtime_Broadcast_Reaction_Cannot_Outlive_Its_Master_Role(NmtCommand command, bool unassign)
    {
        using var rig = OpenDownloadGate(command, out var gate, RuntimeErrorOptions(), target: 0);
        ConfigureErrorControl(rig);
        rig.Node.ObjectDictionary.WriteUnsigned(Startup, 0, command == NmtCommand.Stop ? 64u : 16u);
        await StartMonitoredSlave(rig);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(100));
        await gate.Entered.WaitAsync(ShortTimeout);
        try
        {
            if (unassign) rig.Node.ObjectDictionary.WriteUnsigned(0x1F81, Slave, 0);
            else rig.Node.StopFlyingMaster();
        }
        finally { gate.Release(); }
        await QuiesceAsync(rig.Witness, null);
        rig.Log.Snapshot().Should().NotContain(f => IsNmt(f, command, 0));
    }

    [Fact]
    public async Task Runtime_Recovery_Deadline_Cancels_Post_Reset_Verification()
    {
        using var rig = OpenMaster(RuntimeErrorOptions());
        bool respond = true;
        using var peer = new SilentBootPeer(rig.Peer, Slave, r => respond ? BootReply(r, 123) : null);
        ConfigureErrorControl(rig);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F84, Slave, 123);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F89, 0, 50);
        await StartMonitoredSlave(rig);
        respond = false;
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(100));
        TransmitHeartbeat(rig.Peer, Slave, 0);
        await QuiesceAsync(rig.Witness, null);
        Result(rig).Status.Should().Be(SlaveBootStatus.VerifyingIdentity);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(50));
        Result(rig).Status.Should().Be(SlaveBootStatus.ErrorRecoveryFailed);
        Result(rig).Error.Should().BeOfType<TimeoutException>();
        respond = true;
        TransmitHeartbeat(rig.Peer, Slave, 0);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(200));
        Result(rig).Status.Should().Be(SlaveBootStatus.ErrorRecoveryFailed);
        Started(rig, Slave).Should().BeFalse();
        ConfigurationChannel(rig).CurrentCount.Should().Be(1, "the cancelled verification releases its channel");
    }

    [Fact]
    public async Task Runtime_Error_Cancels_Only_The_Failed_Peers_Queued_Start()
    {
        using var rig = OpenDownloadGate(NmtCommand.Start, out var gate, RuntimeErrorOptions());
        ConfigureErrorControl(rig);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F81, 6, Assigned | BootSlave | 0x10u);
        IDisposable? allowed = null;
        gate.BeforeHold = () => allowed ??= rig.Witness.AllowPeerVerificationWait(Slave);
        try
        {
            await StartMonitoredSlave(rig);
            await gate.Entered.WaitAsync(ShortTimeout);
            TransmitHeartbeat(rig.Peer, 6, 0);
            await QuiesceAsync(rig.Witness, null);
            await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(100));
            Result(rig).Status.Should().Be(SlaveBootStatus.ErrorControlReactionRequested);
        }
        finally { gate.Release(); allowed?.Dispose(); }
        await QuiesceAsync(rig.Witness, null);
        Started(rig, Slave).Should().BeFalse("the old queued Start was invalidated by its peer's error");
        Started(rig, 6).Should().BeTrue("another peer's queued Start still owns a live token");
        rig.Log.Snapshot().Should().ContainSingle(f => IsNmt(f, NmtCommand.ResetNode, Slave));
        TransmitHeartbeat(rig.Peer, Slave, 0);
        await QuiesceAsync(rig.Witness, null);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await QuiesceAsync(rig.Witness, null);
        rig.Log.Snapshot().Should().ContainSingle(f => IsNmt(f, NmtCommand.Start, Slave));
    }

    [Fact]
    public async Task Runtime_Recovery_Resumes_After_An_Abandoned_Forced_Reset()
    {
        using var rig = OpenMaster(RuntimeErrorOptions());
        ConfigureErrorControl(rig);
        await StartMonitoredSlave(rig);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(100));
        await rig.Node.PostToActorAsync(() => SetBootField(rig.Node, "_coldResetPending", true));
        TransmitHeartbeat(rig.Peer, Slave, 0);
        await QuiesceAsync(rig.Witness, null);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(150));
        Result(rig).Status.Should().Be(SlaveBootStatus.Announced, "verification remains paused behind the forced reset");
        Started(rig, Slave).Should().BeFalse();
        await rig.Node.PostToActorAsync(() =>
        {
            SetBootField(rig.Node, "_coldResetPending", false);
            InvokeBoot(rig.Node, "ResumeHeldBoot");
        });
        Result(rig).Status.Should().Be(SlaveBootStatus.WaitingForErrorControl);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await QuiesceAsync(rig.Witness, null);
        Started(rig, Slave).Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Cancelled_Runtime_Recovery_Cannot_Send_Its_Held_Reset(int action)
    {
        using var rig = OpenDownloadGate(NmtCommand.ResetNode, out var gate, RuntimeErrorOptions());
        ConfigureErrorControl(rig);
        IDisposable? allowed = null;
        gate.BeforeHold = () => allowed ??= rig.Witness.AllowPeerVerificationWait(Slave);
        try
        {
            await StartMonitoredSlave(rig);
            await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(100));
            await gate.Entered.WaitAsync(ShortTimeout);
            if (action == 0) rig.Node.StopFlyingMaster();
            else if (action == 1) await rig.Node.PostToActorAsync(() => InvokeBoot(rig.Node, "EnterStandby", (ushort)0, (byte)1));
            else if (action == 2) rig.Node.ObjectDictionary.WriteUnsigned(0x1F81, Slave, 0);
            else rig.Node.Dispose();
        }
        finally { gate.Release(); allowed?.Dispose(); }
        try { await rig.Node.NmtQueueForTests.WaitAsync(ShortTimeout); }
        catch (OperationCanceledException) { /* The held reset carries the cancelled peer token. */ }
        if (action != 3)
        {
            TransmitHeartbeat(rig.Peer, Slave, 0);
            TransmitHeartbeat(rig.Peer, Slave, 0x7F);
            await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(200));
            rig.Node.GetSlaveBootResults().Should().NotContain(r => r.NodeId == Slave);
        }
        rig.Log.Snapshot().Should().NotContain(f => IsNmt(f, NmtCommand.ResetNode, Slave));
        Started(rig, Slave).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Runtime_Reset_Uses_Fresh_BootUp_And_Timeout_After_Confirmation(bool fastReply)
    {
        using var rig = OpenDownloadGate(NmtCommand.ResetNode, out var gate, RuntimeErrorOptions());
        ConfigureErrorControl(rig);
        gate.HoldAfterTransmit = fastReply;
        IDisposable? allowed = null;
        gate.BeforeHold = () => allowed ??= rig.Witness.AllowPeerVerificationWait(Slave);
        try
        {
            await StartMonitoredSlave(rig);
            await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(100));
            await gate.Entered.WaitAsync(ShortTimeout);
            // Even the fast-reply case waits for the actual driver confirmation before recovery.
            if (fastReply)
                await UntilAsync(rig.Clock, rig.Witness, null,
                    () => rig.Log.Snapshot().Any(f => IsNmt(f, NmtCommand.ResetNode, Slave)), 100, "reset reached the bus");
            TransmitHeartbeat(rig.Peer, Slave, 0);
            await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(250));
            Result(rig).Status.Should().Be(SlaveBootStatus.ErrorControlReactionRequested);
            Started(rig, Slave).Should().BeFalse();
        }
        finally { gate.Release(); allowed?.Dispose(); }
        await QuiesceAsync(rig.Witness, null);
        if (fastReply)
        {
            Result(rig).Status.Should().Be(SlaveBootStatus.WaitingForErrorControl);
            TransmitHeartbeat(rig.Peer, Slave, 0x7F);
            await QuiesceAsync(rig.Witness, null);
            Started(rig, Slave).Should().BeTrue();
        }
        else
        {
            Result(rig).Status.Should().Be(SlaveBootStatus.ErrorControlReactionRequested,
                "the buffered pre-transmission boot-up cannot approve this reset");
            await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(100));
            Result(rig).Status.Should().Be(SlaveBootStatus.ErrorRecoveryFailed);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Runtime_Recovery_Deadline_Includes_Channel_And_Transmission_Waits(bool holdChannel)
    {
        using var rig = OpenDownloadGate(NmtCommand.ResetNode, out var gate, RuntimeErrorOptions());
        ConfigureErrorControl(rig);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F89, 0, 50);
        await StartMonitoredSlave(rig);
        IDisposable? allowed = null;
        if (holdChannel) allowed = rig.Witness.HoldPeerSdoChannel(Slave, ConfigurationChannel(rig));
        else gate.BeforeHold = () => allowed ??= rig.Witness.AllowPeerVerificationWait(Slave);
        try
        {
            await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(100));
            await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(50));
            Result(rig).Status.Should().Be(SlaveBootStatus.ErrorRecoveryFailed);
            Result(rig).Error.Should().BeOfType<TimeoutException>();
        }
        finally { gate.Release(); allowed?.Dispose(); }
        await QuiesceAsync(rig.Witness, null);
        rig.Log.Snapshot().Should().NotContain(f => IsNmt(f, NmtCommand.ResetNode, Slave));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Runtime_Reset_Send_Failure_Is_Reported_And_Does_Not_Start(int kind)
    {
        using var rig = OpenDownloadGate(NmtCommand.ResetNode, out var gate, RuntimeErrorOptions());
        ConfigureErrorControl(rig);
        gate.Reject = kind == 0;
        gate.Cancel = kind == 1;
        if (kind == 2) gate.Fault = new InvalidOperationException("driver unavailable");
        await StartMonitoredSlave(rig);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(100));
        Result(rig).Status.Should().Be(SlaveBootStatus.ErrorRecoveryFailed);
        Result(rig).Error.Should().NotBeNull();
        rig.Log.Snapshot().Should().NotContain(f => IsNmt(f, NmtCommand.ResetNode, Slave));
        Started(rig, Slave).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Runtime_Recovery_Rechecks_Identity_And_Configuration(bool configuration)
    {
        using var rig = OpenMaster(new CanOpenNodeOptions
        { EnableNmtErrorHandler = true, RequireBootConfigurationMatch = configuration, SdoTimeout = TimeSpan.FromMilliseconds(100) });
        bool valid = true;
        using var peer = new SilentBootPeer(rig.Peer, Slave, r => valid
            ? ConfigurationReply(r) : BootReply(r, 456));
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
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(100));
        TransmitHeartbeat(rig.Peer, Slave, 0);
        await QuiesceAsync(rig.Witness, null);
        Result(rig).Status.Should().Be(configuration ? SlaveBootStatus.ConfigurationRequired : SlaveBootStatus.IdentityMismatch);
        Started(rig, Slave).Should().BeFalse();
        rig.Log.Snapshot().Should().Contain(f => f.Id == 0x600 + Slave);
    }
}
