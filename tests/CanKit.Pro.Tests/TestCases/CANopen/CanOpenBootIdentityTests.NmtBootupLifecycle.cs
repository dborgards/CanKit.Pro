using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using Xunit;
using static CanKit.Pro.Tests.TestCases.CANopen.FlyingMasterRig;

namespace CanKit.Pro.Tests.TestCases.CANopen;

public partial class CanOpenBootIdentityTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task Only_A_Valid_Peer_Bootup_Triggers_The_Handler(int kind)
    {
        using var rig = OpenMaster(BootupOptions());
        ConfigureErrorControl(rig);
        await StartMonitoredSlave(rig);
        int events = 0;
        rig.Node.FlyingMasterChanged += (_, e) => { if (e.Signal == FlyingMasterSignal.SlaveBootupReceived) Interlocked.Increment(ref events); };
        var frame = kind switch
        {
            0 => CanFrame.Classic(0x700 + Slave, new byte[] { 0x80 }, false),
            1 => CanFrame.Classic(0x700 + Slave, new byte[] { 0, 0 }, false),
            2 => CanFrame.Classic(0x700 + Slave, ReadOnlyMemory<byte>.Empty, false),
            3 => CanFrame.Classic(0x700 + Slave, ReadOnlyMemory<byte>.Empty, false, true),
            4 => CanFrame.Fd(0x700 + Slave, new byte[] { 0 }, false, false, false),
            5 => CanFrame.Classic(0x700 + Slave, new byte[] { 0 }, true),
            6 => CanFrame.Classic(0x700 + LeftId, new byte[] { 0 }, false),
            7 => CanFrame.Classic(0x700, new byte[] { 0 }, false),
            _ => CanFrame.Classic(0x780, new byte[] { 0 }, false)
        };
        rig.Peer.Transmit(frame);
        await QuiesceAsync(rig.Witness, null);
        Volatile.Read(ref events).Should().Be(0);
        Result(rig).Status.Should().Be(SlaveBootStatus.StartRequested);
        Started(rig, Slave).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bootup_Cancels_Queued_Identity_Or_Configuration_Results(bool configuration)
    {
        using var rig = OpenMaster(new CanOpenNodeOptions
        { EnableNmtBootupHandler = true, RequireBootConfigurationMatch = configuration });
        if (configuration) ConfigureVersion(rig);
        else
        {
            Configure(rig);
            rig.Node.ObjectDictionary.WriteUnsigned(0x1F84, Slave, 123);
        }
        await ElectActive(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0);
        await QuiesceAsync(rig.Witness, null);
        var expected = configuration ? SlaveBootStatus.VerifyingConfiguration : SlaveBootStatus.VerifyingIdentity;
        Result(rig).Status.Should().Be(expected);
        await rig.Node.PostToActorAsync(() =>
        {
            var token = ((CancellationTokenSource?[])BootField(rig.Node, "_slaveNmtErrorTokens"))[Slave]!.Token;
            int generation = (int)BootField(rig.Node, "_bootGeneration");
            // A completed worker queued its result immediately before the new boot-up.
            InvokeBoot(rig.Node, configuration ? "PostSlaveConfigurationResult" : "PostSlaveIdentityResult",
                Slave, generation, new SlaveBootResult(Slave,
                    configuration ? SlaveBootStatus.ConfigurationVerified : SlaveBootStatus.IdentityVerified), token);
            InvokeBoot(rig.Node, "ObserveNmtBootup", (uint)(0x700 + Slave), new byte[] { 0 }, false, false);
            InvokeBoot(rig.Node, "NoteSlaveNmtState", Slave, (byte)0);
            token.IsCancellationRequested.Should().BeTrue();
        });
        await QuiesceAsync(rig.Witness, null);
        Result(rig).Status.Should().Be(expected, "the old result cannot approve the new attempt in the same network boot");
        Started(rig, Slave).Should().BeFalse();
    }

    [Fact]
    public async Task Bootup_Cancels_Only_The_Rebooted_Peers_Queued_Start()
    {
        using var rig = OpenDownloadGate(NmtCommand.Start, out var gate, BootupOptions());
        ConfigureErrorControl(rig);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F81, 6, Assigned | BootSlave | 0x10u);
        IDisposable? allowed = null;
        gate.BeforeHold = () => allowed ??= rig.Witness.AllowPeerVerificationWait(Slave);
        try
        {
            await StartMonitoredSlave(rig);
            await gate.Entered.WaitAsync(ShortTimeout);
            TransmitHeartbeat(rig.Peer, 6, 0);
            TransmitHeartbeat(rig.Peer, Slave, 0);
            await QuiesceAsync(rig.Witness, null);
            Result(rig).Status.Should().Be(SlaveBootStatus.WaitingForErrorControl);
        }
        finally { gate.Release(); allowed?.Dispose(); }
        await QuiesceAsync(rig.Witness, null);
        Started(rig, Slave).Should().BeFalse();
        Started(rig, 6).Should().BeTrue();
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await QuiesceAsync(rig.Witness, null);
        rig.Log.Snapshot().Should().ContainSingle(f => IsNmt(f, NmtCommand.Start, Slave));
    }

    [Fact]
    public async Task Repeated_Initial_Bootups_Do_Not_Extend_The_Initial_Deadline()
    {
        using var rig = OpenMaster(new CanOpenNodeOptions
        { EnableNmtBootupHandler = true, SdoTimeout = TimeSpan.FromMilliseconds(500) });
        Configure(rig);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F84, Slave, 123);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F89, 0, 100);
        await ElectActive(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(50));
        TransmitHeartbeat(rig.Peer, Slave, 0);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(50));
        Result(rig).Status.Should().Be(SlaveBootStatus.BootTimeout);
        Started(rig, Slave).Should().BeFalse();
    }

    [Fact]
    public async Task Runtime_Bootup_Owns_A_Fresh_Deadline_Independent_Of_The_Initial_One()
    {
        using var rig = OpenMaster(BootupOptions());
        ConfigureErrorControl(rig, heartbeatMs: 1000);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F89, 0, 150);
        await StartMonitoredSlave(rig);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(100));
        TransmitHeartbeat(rig.Peer, Slave, 0);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(50));
        Result(rig).Status.Should().Be(SlaveBootStatus.WaitingForErrorControl);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(100));
        Result(rig).Status.Should().Be(SlaveBootStatus.ErrorRecoveryFailed);
        Started(rig, Slave).Should().BeFalse();
        rig.Log.Snapshot().Should().NotContain(f => f.Id == 0);
    }

    [Fact]
    public async Task Bootup_During_Configuration_Restore_Remains_In_The_Existing_Handshake()
    {
        using var rig = OpenMaster(new CanOpenNodeOptions
        { EnableNmtBootupHandler = true, EnableBootConfigurationDownload = true, SdoTimeout = TimeSpan.FromMilliseconds(100) });
        using var peer = rig.OpenPeerNode(Slave);
        peer.ObjectDictionary.AddDomain(0x2000, 0, new byte[] { 1 });
        ConfigureDownload(rig, restore: true);
        await ElectActive(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Started(rig, Slave), 500, "restore/reset/download completes once");
        peer.ObjectDictionary.ReadRaw(0x2000, 0).Should().Equal(42);
        rig.Log.Snapshot().Should().ContainSingle(f => IsNmt(f, NmtCommand.ResetNode, Slave));
        rig.Log.Snapshot().Should().ContainSingle(f => IsDownload(f, 0x1011));
    }

    [Fact]
    public async Task Bootup_Held_Behind_A_Master_Reset_Resumes_With_Fresh_Checks()
    {
        using var rig = OpenMaster(BootupOptions());
        ConfigureErrorControl(rig);
        await StartMonitoredSlave(rig);
        await rig.Node.PostToActorAsync(() => SetBootField(rig.Node, "_coldResetPending", true));
        TransmitHeartbeat(rig.Peer, Slave, 0);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(150));
        Result(rig).Status.Should().Be(SlaveBootStatus.Announced);
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bootup_Observer_Cannot_Approve_A_Runtime_Reset_Before_Confirmation(bool fastReply)
    {
        using var rig = OpenDownloadGate(NmtCommand.ResetNode, out var gate, new CanOpenNodeOptions
        { EnableNmtErrorHandler = true, EnableNmtBootupHandler = true, SdoTimeout = TimeSpan.FromMilliseconds(100) });
        ConfigureErrorControl(rig);
        gate.HoldAfterTransmit = fastReply;
        IDisposable? allowed = null;
        gate.BeforeHold = () => allowed ??= rig.Witness.AllowPeerVerificationWait(Slave);
        try
        {
            await StartMonitoredSlave(rig);
            await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(100));
            await gate.Entered.WaitAsync(ShortTimeout);
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
        if (fastReply) Result(rig).Status.Should().Be(SlaveBootStatus.WaitingForErrorControl);
        else
        {
            Result(rig).Status.Should().Be(SlaveBootStatus.ErrorControlReactionRequested);
            await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(100));
            Result(rig).Status.Should().Be(SlaveBootStatus.ErrorRecoveryFailed);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Ending_The_Bootup_Handlers_Role_Cancels_Its_Current_Attempt(int action)
    {
        using var rig = OpenMaster(BootupOptions());
        Configure(rig);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F84, Slave, 123);
        await ElectActive(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0);
        await QuiesceAsync(rig.Witness, null);
        var token = ((CancellationTokenSource?[])BootField(rig.Node, "_slaveNmtErrorTokens"))[Slave]!.Token;
        if (action == 0) rig.Node.StopFlyingMaster();
        else if (action == 1) await rig.Node.PostToActorAsync(() => rig.Node.ObjectDictionary.WriteUnsigned(0x1F81, Slave, 0));
        else rig.Node.Dispose();
        token.IsCancellationRequested.Should().BeTrue();
        if (action != 2)
        {
            TransmitHeartbeat(rig.Peer, Slave, 0);
            await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(200));
            rig.Node.GetSlaveBootResults().Should().NotContain(r => r.NodeId == Slave);
        }
        Started(rig, Slave).Should().BeFalse();
    }

    [Fact]
    public async Task Bootup_Replays_A_Partially_Acknowledged_Configuration_Download()
    {
        using var rig = OpenMaster(new CanOpenNodeOptions
        { EnableNmtBootupHandler = true, EnableBootConfigurationDownload = true, SdoTimeout = TimeSpan.FromMilliseconds(100) });
        using var peer = rig.OpenPeerNode(Slave);
        peer.ObjectDictionary.AddDomain(0x2000, 0, Array.Empty<byte>());
        peer.ObjectDictionary.AddDomain(0x2001, 0, Array.Empty<byte>());
        ConfigureDownload(rig);
        CanOpenNodeExtensions.SetSlaveBootConfiguration(rig.Node, Slave,
            BootDcf((0x2000, 0, new byte[] { 1 }), (0x2001, 0, new byte[] { 2 })));
        var pause = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        IDisposable? allowed = null;
        rig.Node.AfterConciseDcfWriteForTests = () =>
        {
            allowed ??= rig.Witness.AllowPeerVerificationWait(Slave);
            pause.TrySetResult(true);
            return resume.Task;
        };
        try
        {
            await ElectActive(rig);
            TransmitHeartbeat(rig.Peer, Slave, 0);
            await UntilAsync(rig.Clock, rig.Witness, null, () => pause.Task.IsCompleted, 300, "first write acknowledged");
            TransmitHeartbeat(rig.Peer, Slave, 0);
            await QuiesceAsync(rig.Witness, null);
            peer.ObjectDictionary.ReadRaw(0x2001, 0).Should().BeEmpty();
            Started(rig, Slave).Should().BeFalse();
        }
        finally
        {
            rig.Node.AfterConciseDcfWriteForTests = null;
            resume.TrySetResult(true);
            allowed?.Dispose();
        }
        await UntilAsync(rig.Clock, rig.Witness, null, () => Started(rig, Slave), 300, "fresh download finishes");
        peer.ObjectDictionary.ReadRaw(0x2001, 0).Should().Equal(2);
        rig.Log.Snapshot().Count(f => IsDownload(f, 0x2000)).Should().Be(2);
        rig.Log.Snapshot().Count(f => IsDownload(f, 0x2001)).Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bootup_During_Active_Reset_Still_Waits_For_Transmission_Confirmation(bool fastReply)
    {
        using var rig = OpenDownloadGate(NmtCommand.ResetCommunication, out var gate, new CanOpenNodeOptions
        { EnableNmtBootupHandler = true, ActiveSlaveBootRetryInterval = TimeSpan.FromMilliseconds(100), SdoTimeout = TimeSpan.FromMilliseconds(100) });
        using var peer = new SilentBootPeer(rig.Peer, Slave, ConfigurationReply);
        ConfigureErrorControl(rig);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F81, Slave, Assigned | BootSlave | MandatorySlave);
        gate.HoldAfterTransmit = fastReply;
        IDisposable? allowed = null;
        gate.BeforeHold = () => allowed ??= rig.Witness.AllowPeerVerificationWait(Slave);
        try
        {
            await ElectActive(rig);
            await gate.Entered.WaitAsync(ShortTimeout);
            TransmitHeartbeat(rig.Peer, Slave, 0);
            await QuiesceAsync(rig.Witness, null);
            rig.Log.Snapshot().Should().NotContain(f => f.Id == 0x600 + Slave);
            Started(rig, Slave).Should().BeFalse();
        }
        finally { gate.Release(); allowed?.Dispose(); }
        await QuiesceAsync(rig.Witness, null);
        Result(rig).Status.Should().Be(SlaveBootStatus.WaitingForErrorControl);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await QuiesceAsync(rig.Witness, null);
        Started(rig, Slave).Should().BeTrue();
        rig.Log.Snapshot().Should().ContainSingle(f => IsNmt(f, NmtCommand.ResetCommunication, Slave));
    }


    [Fact]
    public async Task Bootup_Cancels_An_Optional_Peers_Pending_Retry()
    {
        using var rig = OpenMaster(new CanOpenNodeOptions
        { EnableNmtBootupHandler = true, ActiveSlaveBootRetryInterval = TimeSpan.FromMilliseconds(100), SdoTimeout = TimeSpan.FromMilliseconds(100) });
        bool valid = false;
        using var peer = new SilentBootPeer(rig.Peer, Slave, r => BootReply(r, valid ? 123u : 456u));
        Configure(rig, mandatory: false);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F84, Slave, 123);
        await ElectActive(rig);
        Result(rig).Status.Should().Be(SlaveBootStatus.IdentityMismatch);
        valid = true;
        TransmitHeartbeat(rig.Peer, Slave, 0);
        await QuiesceAsync(rig.Witness, null);
        Started(rig, Slave).Should().BeTrue();
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(100));
        ((bool[])BootField(rig.Node, "_slaveIdentityVerified"))[Slave].Should().BeTrue(
            "the obsolete retry must not erase the fresh boot-up's approval");
    }

    [Fact]
    public async Task Bootup_Cannot_Replace_A_Runtime_Reset_Waiting_For_The_Peer_Channel()
    {
        using var rig = OpenMaster(new CanOpenNodeOptions
        { EnableNmtBootupHandler = true, EnableNmtErrorHandler = true, SdoTimeout = TimeSpan.FromMilliseconds(100) });
        ConfigureErrorControl(rig);
        await StartMonitoredSlave(rig);
        using (rig.Witness.HoldPeerSdoChannel(Slave, ConfigurationChannel(rig)))
        {
            await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(100));
            TransmitHeartbeat(rig.Peer, Slave, 0);
            await QuiesceAsync(rig.Witness, null);
            Result(rig).Status.Should().Be(SlaveBootStatus.ErrorControlReactionRequested);
            rig.Log.Snapshot().Should().NotContain(f => IsNmt(f, NmtCommand.ResetNode, Slave));
        }
        await QuiesceAsync(rig.Witness, null);
        rig.Log.Snapshot().Should().ContainSingle(f => IsNmt(f, NmtCommand.ResetNode, Slave));
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(100));
        Result(rig).Status.Should().Be(SlaveBootStatus.ErrorRecoveryFailed);
        Started(rig, Slave).Should().BeFalse();
    }

}
