using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.Tests.Infrastructure;
using EdsDcfNet.Models;
using Xunit;
using static CanKit.Pro.Tests.TestCases.CANopen.FlyingMasterRig;

namespace CanKit.Pro.Tests.TestCases.CANopen;

public partial class CanOpenBootIdentityTests
{
    private static MasterRig OpenDownloadGate(NmtCommand command, out CanOpenFlyingMasterTests.ColdResetGate gate)
    {
        var session = NewSession();
        var bus = Open(session, 0);
        var peer = Open(session, 1);
        var clock = new ManualTimeSource();
        gate = new CanOpenFlyingMasterTests.ColdResetGate(new ClockedCanBusService(bus, clock, sendGate: clock))
        { ResetTarget = Slave, ResetCommand = command };
        var node = new CanOpenNode(gate, LeftId, DownloadOptions(), ownsService: true, timeSource: clock);
        return new MasterRig(clock, node, new ActorWitness(node, peer, WitnessForLeft, gate.Confirmations), new FrameLog(bus, peer), peer, bus);
    }

    private static Task ConfigurationResetCompletion(MasterRig rig)
    {
        var reset = ((Array)BootField(rig.Node, "_slaveConfigurationBootUps")).GetValue(Slave)!;
        return ((TaskCompletionSource<bool>)reset.GetType().GetField("Completion")!.GetValue(reset)!).Task;
    }

    private sealed class ConfigurationWithoutBootUp : IDisposable
    {
        private readonly ICanBus _bus;
        public ConfigurationWithoutBootUp(ICanBus bus) { _bus = bus; _bus.FrameObserved += Receive; }
        private void Receive(object? sender, CanReceiveDataView view)
        {
            var frame = view.CanFrame;
            if (frame.ID != 0x600 + Slave) return;
            var data = frame.Data.ToArray();
            if (data.Length != 8) return;
            byte[]? reply = data[0] == 0x40 ? BootReply(data, 0)
                : (data[0] & 0xE0) == 0x20 ? new byte[] { 0x60, data[1], data[2], data[3], 0, 0, 0, 0 } : null;
            if (reply is not null) _bus.Transmit(CanFrame.Classic(0x580 + Slave, reply, isExtendedFrame: false));
        }
        public void Dispose() => _bus.FrameObserved -= Receive;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Boot_Update_Requires_A_Bound_Description_Even_For_An_Empty_Plan(bool empty)
    {
        using var rig = OpenMaster(DownloadOptions());
        using var peer = rig.OpenPeerNode(Slave);
        ConfigureDownload(rig, mandatory: false);
        if (empty) CanOpenNodeExtensions.SetSlaveBootConfiguration(rig.Node, Slave, new byte[4]);
        rig.Node.UnbindPeerDeviceDescription(Slave);
        await ElectActive(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Result(rig).Status == SlaveBootStatus.ConfigurationDownloadFailed, 300, "boot writes require an explicit description");
        Result(rig).Error.Should().BeOfType<InvalidOperationException>();
        Result(rig).Index.Should().BeNull();
        rig.Log.Snapshot().Should().NotContain(f => IsDownload(f, 0x2000));
    }

    [Fact]
    public async Task Explicit_Empty_Boot_Plan_Completes_Without_Writes()
    {
        using var rig = OpenMaster(DownloadOptions());
        using var peer = rig.OpenPeerNode(Slave);
        ConfigureDownload(rig);
        CanOpenNodeExtensions.SetSlaveBootConfiguration(rig.Node, Slave, new byte[4]);
        await ElectActive(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Started(rig, Slave), 300, "explicit empty plan is a successful configuration");
        rig.Log.Snapshot().Should().NotContain(f => IsDownload(f, 0x2000));
    }

    [Theory]
    [InlineData(NmtCommand.EnterPreOperational, false)]
    [InlineData(NmtCommand.ResetNode, false)]
    [InlineData(NmtCommand.ResetNode, true)]
    public async Task Rejected_Configuration_Nmt_Blocks_Download(NmtCommand command, bool cancelled)
    {
        using var rig = OpenDownloadGate(command, out var gate);
        using var peer = rig.OpenPeerNode(Slave);
        peer.ObjectDictionary.AddDomain(0x2000, 0, Array.Empty<byte>());
        ConfigureDownload(rig, mandatory: false, restore: true);
        gate.Reject = !cancelled;
        gate.Cancel = cancelled;
        await ElectActive(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Result(rig).Status == SlaveBootStatus.ConfigurationDownloadFailed, 300, "unconfirmed NMT prevents configuration");
        Result(rig).Error.Should().BeAssignableTo<Exception>();
        peer.ObjectDictionary.ReadRaw(0x2000, 0).Should().BeEmpty();
        Started(rig, Slave).Should().BeFalse();
        rig.Node.PeerSdoChannelIsFreeForTests(Slave).Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Boot_Download_Waits_For_Confirmed_Nmt_And_Cancellation_Releases_The_Channel(bool cancel)
    {
        using var rig = OpenDownloadGate(NmtCommand.EnterPreOperational, out var gate);
        using var peer = rig.OpenPeerNode(Slave);
        IDisposable? allowed = null;
        gate.BeforeHold = () => allowed ??= rig.Witness.AllowPeerVerificationWait(Slave);
        try
        {
            peer.ObjectDictionary.AddDomain(0x2000, 0, Array.Empty<byte>());
            ConfigureDownload(rig);
            await ElectActive(rig);
            TransmitHeartbeat(rig.Peer, Slave, 0x7F);
            await UntilAsync(rig.Clock, rig.Witness, null, () => gate.Entered.IsCompleted, 300, "NMT send is held");
            peer.ObjectDictionary.ReadRaw(0x2000, 0).Should().BeEmpty();
            rig.Node.PeerSdoChannelIsFreeForTests(Slave).Should().BeFalse();
            if (cancel)
            {
                rig.Node.StopFlyingMaster();
                await UntilAsync(rig.Clock, rig.Witness, null, () => rig.Node.PeerSdoChannelIsFreeForTests(Slave), 100, "cancellation releases the lease before held TX returns");
            }
            gate.Release();
            if (!cancel) await UntilAsync(rig.Clock, rig.Witness, null, () => Started(rig, Slave), 300, "confirmed NMT permits download");
            else
            {
                await QuiesceAsync(rig.Witness, null);
                rig.Log.Snapshot().Should().NotContain(f => IsNmt(f, NmtCommand.EnterPreOperational, Slave) || IsDownload(f, 0x2000));
                Started(rig, Slave).Should().BeFalse();
            }
        }
        finally { gate.Release(); allowed?.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restore_Reset_Requires_BootUp_And_Respects_The_Mandatory_Deadline(bool mandatory)
    {
        using var rig = OpenMaster(DownloadOptions());
        using var peer = new ConfigurationWithoutBootUp(rig.Peer);
        ConfigureDownload(rig, mandatory: mandatory, restore: true);
        if (mandatory) rig.Node.ObjectDictionary.WriteUnsigned(0x1F89, 0, 50);
        await ElectActive(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        var expected = mandatory ? SlaveBootStatus.BootTimeout : SlaveBootStatus.ConfigurationDownloadFailed;
        await UntilAsync(rig.Clock, rig.Witness, null, () => Result(rig).Status == expected, 300, "no post-reset boot-up");
        if (!mandatory) Result(rig).Error.Should().BeOfType<TimeoutException>();
        rig.Log.Snapshot().Should().NotContain(f => IsDownload(f, 0x2000));
        Started(rig, Slave).Should().BeFalse();
        await UntilAsync(rig.Clock, rig.Witness, null, () => rig.Node.PeerSdoChannelIsFreeForTests(Slave), 100, "timed-out update releases its lease");
    }

    [Theory]
    [InlineData(128)]
    [InlineData(255)]
    public async Task Configuration_Reset_Observation_Does_Not_Change_Manual_Nmt_Targets(byte target)
    {
        using var rig = OpenMaster();
        await rig.Node.SendNmtCommandAsync(NmtCommand.ResetNode, target);
        await QuiesceAsync(rig.Witness, null);
        rig.Log.Snapshot().Should().Contain(f => IsNmt(f, NmtCommand.ResetNode, target));
    }

    [Fact]
    public async Task Reset_Send_Delay_Does_Not_Spend_The_Post_Reset_BootUp_Timeout()
    {
        using var rig = OpenDownloadGate(NmtCommand.ResetNode, out var gate);
        using var peer = rig.OpenPeerNode(Slave);
        IDisposable? allowed = null;
        gate.BeforeHold = () => allowed ??= rig.Witness.AllowPeerVerificationWait(Slave);
        peer.ObjectDictionary.AddDomain(0x2000, 0, Array.Empty<byte>());
        ConfigureDownload(rig, mandatory: false, restore: true);
        try
        {
            await ElectActive(rig);
            TransmitHeartbeat(rig.Peer, Slave, 0x7F);
            await UntilAsync(rig.Clock, rig.Witness, null, () => gate.Entered.IsCompleted, 300, "Reset Node is held before transmission");
            await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(250));
            peer.ObjectDictionary.ReadRaw(0x2000, 0).Should().BeEmpty();
        }
        finally { gate.Release(); allowed?.Dispose(); }
        await UntilAsync(rig.Clock, rig.Witness, null, () => Started(rig, Slave), 300,
            "a promptly booting slave still has its full post-reset timeout after a long send hold");
        peer.ObjectDictionary.ReadRaw(0x2000, 0).Should().Equal(42);
    }

    [Fact]
    public async Task A_BootUp_Before_Reset_Transmission_Does_Not_Release_The_Download()
    {
        using var rig = OpenDownloadGate(NmtCommand.ResetNode, out var gate);
        using var peer = new ConfigurationWithoutBootUp(rig.Peer);
        IDisposable? allowed = null;
        gate.BeforeHold = () => allowed ??= rig.Witness.AllowPeerVerificationWait(Slave);
        ConfigureDownload(rig, mandatory: false, restore: true);
        Task waiter;
        try
        {
            await ElectActive(rig);
            TransmitHeartbeat(rig.Peer, Slave, 0x7F);
            await UntilAsync(rig.Clock, rig.Witness, null, () => gate.Entered.IsCompleted, 300, "new reset still waits for transmission");
            waiter = ConfigurationResetCompletion(rig);
            TransmitHeartbeat(rig.Peer, Slave, 0); // Late announcement from an earlier reset.
            await QuiesceAsync(rig.Witness, null);
            waiter.IsCompleted.Should().BeFalse("a pre-transmission boot-up cannot prove this reset completed");
            // RawCan timestamps use this rig's manual clock. Separate the two wire instants
            // explicitly; host scheduling must not be the source of their ordering.
            await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(1));
            var capture = typeof(CanOpenNode).GetMethod("CaptureConfigurationReset", BindingFlags.Instance | BindingFlags.NonPublic)!;
            capture.Invoke(rig.Node, new object[] { CanFrame.Classic(0, new byte[] { 0x81, Slave }, false), CancellationToken.None })
                .Should().BeNull("an unrelated NMT sender cannot supply this attempt's handoff");
        }
        finally { gate.Release(); allowed?.Dispose(); }
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => ((bool[])BootField(rig.Node, "_slaveConfigurationAwaitingBootUp"))[Slave] || waiter.IsCompleted, 100, "this reset was confirmed");
        waiter.IsCompleted.Should().BeFalse("the reset still needs its own boot-up");
        await rig.Node.PostToActorAsync(() => InvokeBoot(rig.Node, "ObserveConfigurationBootUp", (uint)(0x700 + Slave), new byte[] { 0 }, false, false, 0L));
        waiter.IsCompleted.Should().BeFalse("an RX without a comparable arrival timestamp is not evidence");
        rig.Log.Snapshot().Should().NotContain(f => IsDownload(f, 0x2000));
        TransmitHeartbeat(rig.Peer, Slave, 0);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Started(rig, Slave), 200, "a post-transmission announcement permits the download");
    }

    [Fact]
    public async Task A_Fast_BootUp_Is_Retained_Until_Delayed_Transmit_Confirmation()
    {
        using var rig = OpenDownloadGate(NmtCommand.ResetNode, out var gate);
        using var peer = rig.OpenPeerNode(Slave);
        gate.HoldAfterTransmit = true;
        IDisposable? allowed = null;
        gate.BeforeHold = () => allowed ??= rig.Witness.AllowPeerVerificationWait(Slave);
        peer.ObjectDictionary.AddDomain(0x2000, 0, Array.Empty<byte>());
        ConfigureDownload(rig, mandatory: false, restore: true);
        try
        {
            await ElectActive(rig);
            rig.Log.Clear();
            TransmitHeartbeat(rig.Peer, Slave, 0x7F);
            await UntilAsync(rig.Clock, rig.Witness, null,
                () => rig.Log.Snapshot().Any(f => f.Id == 0x700 + Slave && f.Data[0] == 0), 300,
                "the peer has rebooted while local reset confirmation is still held");
            ConfigurationResetCompletion(rig).IsCompleted.Should().BeFalse("the handoff has not been supplied yet");
            peer.ObjectDictionary.ReadRaw(0x2000, 0).Should().BeEmpty();
            await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(150));
        }
        finally { gate.Release(); allowed?.Dispose(); }
        await UntilAsync(rig.Clock, rig.Witness, null, () => Started(rig, Slave), 300,
            "the buffered post-handoff boot-up is eligible after confirmation");
        peer.ObjectDictionary.ReadRaw(0x2000, 0).Should().Equal(42);
    }

    [Fact]
    public async Task Restore_Refuses_A_Confirmation_Without_Handoff_Metadata()
    {
        using var rig = OpenDownloadGate(NmtCommand.ResetNode, out var gate);
        using var peer = rig.OpenPeerNode(Slave);
        gate.OmitHandoffTimestamp = true;
        gate.Release();
        ConfigureDownload(rig, mandatory: false, restore: true);
        await ElectActive(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Result(rig).Status == SlaveBootStatus.ConfigurationDownloadFailed,
            300, "missing timing evidence cannot approve the reset");
        Result(rig).Error.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("handoff timestamp");
        rig.Log.Snapshot().Should().NotContain(f => IsDownload(f, 0x2000));
        Started(rig, Slave).Should().BeFalse();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Only_A_One_Byte_Zero_Announcement_Completes_The_Restore_Reset(bool guarding, bool extraByte)
    {
        using var rig = OpenMaster(DownloadOptions());
        using var peer = new ConfigurationWithoutBootUp(rig.Peer);
        ConfigureDownload(rig, mandatory: false, restore: true);
        await ElectActive(rig);
        if (guarding) rig.Node.StartNodeGuardingConsumer(Slave, TimeSpan.FromSeconds(10), 1);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Log.Snapshot().Any(f => IsNmt(f, NmtCommand.ResetNode, Slave)), 300, "the reset is sent");
        var waiter = ConfigurationResetCompletion(rig);
        Assert.NotNull(waiter);
        byte[] malformed = extraByte ? new byte[] { 0, 0 } : new byte[] { 0x80 };
        rig.Peer.Transmit(CanFrame.Classic(0x700 + Slave, malformed, isExtendedFrame: false));
        await QuiesceAsync(rig.Witness, null);
        waiter.IsCompleted.Should().BeFalse("a toggle bit or wrong DLC is not a valid boot-up frame");
        rig.Log.Snapshot().Should().NotContain(f => IsDownload(f, 0x2000));
        TransmitHeartbeat(rig.Peer, Slave, 0);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Started(rig, Slave), 200, "a genuine boot-up releases the download");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Boot_Update_Pins_Description_And_Cancels_Between_Acknowledged_Writes(bool cancel)
    {
        using var rig = OpenMaster(DownloadOptions());
        using var peer = rig.OpenPeerNode(Slave);
        IDisposable? allowed = null;
        peer.ObjectDictionary.AddDomain(0x2000, 0, Array.Empty<byte>());
        peer.ObjectDictionary.AddDomain(0x2001, 0, Array.Empty<byte>());
        ConfigureDownload(rig);
        var description = DownloadDescription();
        rig.Node.BindPeerDeviceDescription(Slave, description);
        CanOpenNodeExtensions.SetSlaveBootConfiguration(rig.Node, Slave,
            BootDcf((0x2000, 0, new byte[] { 1 }), (0x2001, 0, new byte[] { 2 })));
        var pause = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Node.AfterConciseDcfWriteForTests = () =>
        {
            allowed ??= rig.Witness.AllowPeerVerificationWait(Slave);
            pause.TrySetResult(true);
            return resume.Task;
        };
        try
        {
            await ElectActive(rig);
            TransmitHeartbeat(rig.Peer, Slave, 0x7F);
            await UntilAsync(rig.Clock, rig.Witness, null, () => pause.Task.IsCompleted, 300, "first write acknowledged");
            rig.Node.PeerSdoChannelIsFreeForTests(Slave).Should().BeFalse();
            await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Node.SdoUploadAsync(Slave, 0x2000, 0));
            description.Objects.Objects[0x2001].AccessType = AccessType.ReadOnly;
            rig.Node.UnbindPeerDeviceDescription(Slave);
            if (cancel) rig.Node.StopFlyingMaster();
        }
        finally { resume.TrySetResult(true); allowed?.Dispose(); }
        await UntilAsync(rig.Clock, rig.Witness, null, () => rig.Node.PeerSdoChannelIsFreeForTests(Slave), 300, "update releases the channel");
        if (cancel)
        {
            peer.ObjectDictionary.ReadRaw(0x2001, 0).Should().BeEmpty();
            Started(rig, Slave).Should().BeFalse();
        }
        else
        {
            await UntilAsync(rig.Clock, rig.Witness, null, () => Started(rig, Slave), 300, "pinned description permits second write");
            peer.ObjectDictionary.ReadRaw(0x2001, 0).Should().Equal(2);
        }
    }

    [Fact]
    public async Task An_Optional_Download_Abort_Retries_The_Configuration_Check_And_Plan()
    {
        using var rig = OpenMaster(DownloadOptions(active: true));
        using var peer = rig.OpenPeerNode(Slave);
        ConfigureDownload(rig, mandatory: false);
        await ElectActive(rig);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Result(rig).Status == SlaveBootStatus.ConfigurationDownloadFailed, 300, "the first download aborts");
        peer.ObjectDictionary.AddDomain(0x2000, 0, Array.Empty<byte>());
        await UntilAsync(rig.Clock, rig.Witness, null, () => Started(rig, Slave), 400, "retry downloads the same plan successfully");
        peer.ObjectDictionary.ReadRaw(0x2000, 0).Should().Equal(42);
        ConfigurationRequests(rig).Count(r => r.Item1 == 0x1000).Should().Be(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_Partial_Download_With_Updated_Signature_Must_Replay_Before_Start(bool restart)
    {
        using var rig = OpenMaster(DownloadOptions(active: true));
        using var peer = rig.OpenPeerNode(Slave);
        peer.ObjectDictionary.AddU32(0x1020, 1, 1);
        peer.ObjectDictionary.AddU32(0x1020, 2, ConfigurationTime);
        ConfigureDownload(rig, mandatory: false, date: ConfigurationDate, time: ConfigurationTime);
        CanOpenNodeExtensions.SetSlaveBootConfiguration(rig.Node, Slave, BootDcf(
            (0x1020, 1, new byte[] { 0x78, 0x56, 0x34, 0x12 }),
            (0x2000, 0, new byte[] { 42 })));
        await ElectActive(rig);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Result(rig).Status == SlaveBootStatus.ConfigurationDownloadFailed,
            300, "the signature write succeeds but the later parameter aborts");
        peer.ObjectDictionary.ReadUnsigned(0x1020, 1).Should().Be(ConfigurationDate);
        Started(rig, Slave).Should().BeFalse();
        peer.ObjectDictionary.AddDomain(0x2000, 0, Array.Empty<byte>());
        if (restart)
        {
            rig.Node.StopFlyingMaster();
            await ElectActive(rig);
        }
        await UntilAsync(rig.Clock, rig.Witness, null, () => Started(rig, Slave), 400,
            "matching signatures cannot excuse the known incomplete plan");
        peer.ObjectDictionary.ReadRaw(0x2000, 0).Should().Equal(42);
        rig.Log.Snapshot().Count(f => IsDownload(f, 0x1020)).Should().Be(2);
    }

    [Fact]
    public async Task Configuration_Read_Error_Does_Not_Start_A_Registered_Update()
    {
        using var rig = OpenMaster(DownloadOptions());
        using var peer = rig.OpenPeerNode(Slave); // No 1020h: the upload aborts.
        ConfigureDownload(rig, mandatory: false, restore: true, date: ConfigurationDate, time: ConfigurationTime);
        await ElectActive(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Result(rig).Status == SlaveBootStatus.ConfigurationReadFailed,
            300, "an unreadable signature is not permission to overwrite configuration");
        rig.Log.Snapshot().Should().NotContain(f => IsDownload(f, 0x1011) || IsDownload(f, 0x2000));
        Started(rig, Slave).Should().BeFalse();
    }

    [Fact]
    public void Restore_Array_Is_Opt_In_Unsigned8_And_Range_Checked()
    {
        using var legacy = OpenMaster(ConfigurationOptions());
        legacy.Node.ObjectDictionary.TryGet(0x1F8A, Slave, out _).Should().BeFalse();
        using var rig = OpenMaster(DownloadOptions());
        rig.Node.ObjectDictionary.ReadUnsigned(0x1F8A, 0).Should().Be(127);
        rig.Node.ObjectDictionary.ReadRaw(0x1F8A, Slave).Should().Equal(0);
        Action count = () => rig.Node.ObjectDictionary.WriteUnsigned(0x1F8A, 0, 1);
        count.Should().Throw<ArgumentException>();
        Action range = () => rig.Node.ObjectDictionary.WriteUnsigned(0x1F8A, Slave, 128);
        range.Should().Throw<ArgumentException>();
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F8A, Slave, 127);
        rig.Node.ObjectDictionary.ReadRaw(0x1F8A, Slave).Should().Equal(127);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Restore_Array_Loads_From_Eds_And_Dcf_As_Unsigned8(bool dcf)
    {
        string text = ConfigurationDescription(true).Replace("1F26", "1F8A").Replace("DataType=0x0007", "DataType=0x0005")
            .Replace("0x12345678", "1").Replace("0x9ABCDEF0", "0");
        using var rig = OpenMaster();
        var description = dcf ? CanOpenDeviceDescription.ParseDcf(text) : CanOpenDeviceDescription.ParseEds(text);
        using var peer = rig.OpenPeerNode(Slave, description, DownloadOptions());
        peer.ObjectDictionary.ReadRaw(0x1F8A, 5).Should().Equal(1);
    }

    [Fact]
    public async Task Restore_Selection_Live_Edit_Starts_A_New_Boot()
    {
        using var rig = OpenMaster(DownloadOptions());
        using var peer = rig.OpenPeerNode(Slave);
        peer.ObjectDictionary.AddDomain(0x2000, 0, Array.Empty<byte>());
        ConfigureDownload(rig, restore: true, restoreSub: 0);
        await ElectActive(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Started(rig, Slave), 300, "first boot without restore");
        rig.Log.Clear();
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F8A, Slave, 1);
        TransmitHeartbeat(rig.Peer, Slave, 5);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Started(rig, Slave), 400, "live restore selection starts fresh boot");
        rig.Log.Snapshot().Should().Contain(f => IsDownload(f, 0x1011));
    }

    [Fact]
    public async Task Disposal_Between_Boot_Writes_Cancels_The_Remaining_Plan()
    {
        using var rig = OpenMaster(DownloadOptions());
        using var peer = rig.OpenPeerNode(Slave);
        IDisposable? allowed = null;
        peer.ObjectDictionary.AddDomain(0x2000, 0, Array.Empty<byte>());
        peer.ObjectDictionary.AddDomain(0x2001, 0, Array.Empty<byte>());
        ConfigureDownload(rig);
        CanOpenNodeExtensions.SetSlaveBootConfiguration(rig.Node, Slave,
            BootDcf((0x2000, 0, new byte[] { 1 }), (0x2001, 0, new byte[] { 2 })));
        var pause = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Node.AfterConciseDcfWriteForTests = () =>
        {
            allowed ??= rig.Witness.AllowPeerVerificationWait(Slave);
            pause.TrySetResult(true);
            return resume.Task;
        };
        try
        {
            await ElectActive(rig);
            TransmitHeartbeat(rig.Peer, Slave, 0x7F);
            await UntilAsync(rig.Clock, rig.Witness, null, () => pause.Task.IsCompleted, 300, "first acknowledged write");
            rig.Node.Dispose();
        }
        finally { resume.TrySetResult(true); allowed?.Dispose(); }
        var channel = ConfigurationChannel(rig);
        (await channel.WaitAsync(ShortTimeout)).Should().BeTrue("disposal cancels the boot worker and releases its lease");
        channel.Release();
        peer.ObjectDictionary.ReadRaw(0x2001, 0).Should().BeEmpty();
        Started(rig, Slave).Should().BeFalse();
    }

#if NET5_0_OR_GREATER
    [Fact]
    public void Foreign_Nodes_Cannot_Register_Boot_Configuration()
    {
        var foreign = DispatchProxy.Create<ICanOpenNode, ForeignBootConfigurationNode>();
        Assert.Throws<NotSupportedException>(() => foreign.SetSlaveBootConfiguration(Slave, new byte[4]));
    }
    private class ForeignBootConfigurationNode : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? arguments) => throw new InvalidOperationException();
    }
#endif
}
