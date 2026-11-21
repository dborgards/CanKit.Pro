using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using Xunit;
using static CanKit.Pro.Tests.TestCases.CANopen.FlyingMasterRig;

namespace CanKit.Pro.Tests.TestCases.CANopen;

// FR-CO-070: start error control after configuration, before accepting Operational/Start.
public partial class CanOpenBootIdentityTests
{
    private static CanOpenNodeOptions ErrorControlOptions(bool active = false) => new()
    {
        EnableBootErrorControl = true,
        ActiveSlaveBootRetryInterval = active ? TimeSpan.FromMilliseconds(100) : null,
        SdoTimeout = TimeSpan.FromMilliseconds(100)
    };

    private static void ConfigureErrorControl(MasterRig rig, int heartbeatMs = 100, bool mandatory = true,
        bool simultaneous = false, ushort guardMs = 0, byte retries = 0)
    {
        Configure(rig, mandatory, simultaneous);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F81, Slave,
            rig.Node.ObjectDictionary.ReadUnsigned(0x1F81, Slave) | ((uint)guardMs << 16) | ((uint)retries << 8));
        if (heartbeatMs > 0) rig.Node.AddHeartbeatConsumer(Slave, TimeSpan.FromMilliseconds(heartbeatMs));
    }

    private static async Task EnterHeartbeatStage(MasterRig rig)
    {
        await ElectActive(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0);
        await QuiesceAsync(rig.Witness, null);
        Result(rig).Status.Should().Be(SlaveBootStatus.WaitingForErrorControl);
    }

    [Theory]
    [InlineData(0x04)]
    [InlineData(0x05)]
    [InlineData(0x7F)]
    public async Task Boot_Error_Control_Requires_A_Fresh_Heartbeat_Before_Start_Or_Operational(byte state)
    {
        using var rig = OpenMaster(ErrorControlOptions());
        ConfigureErrorControl(rig, simultaneous: true);
        await EnterHeartbeatStage(rig);
        rig.Node.State.Should().Be(NmtState.PreOperational);
        Started(rig, Slave).Should().BeFalse();
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(50));
        Result(rig).Status.Should().Be(SlaveBootStatus.WaitingForErrorControl);
        TransmitHeartbeat(rig.Peer, Slave, state);
        await QuiesceAsync(rig.Witness, null);
        Result(rig).Status.Should().Be(state == 5 ? SlaveBootStatus.AlreadyOperational : SlaveBootStatus.StartRequested);
        Started(rig, Slave).Should().Be(state != 5);
        Started(rig, 0).Should().BeFalse();
        rig.Node.State.Should().Be(NmtState.Operational);
        rig.Node.ObjectDictionary.ReadUnsigned(0x1016, 1).Should().Be(((uint)Slave << 16) | 100,
            "the cold election reset must retain the heartbeat configuration used for boot");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task Malformed_Or_NonHeartbeat_Frames_Do_Not_Complete_Error_Control(int kind)
    {
        using var rig = OpenMaster(ErrorControlOptions());
        ConfigureErrorControl(rig);
        await EnterHeartbeatStage(rig);
        var frame = kind switch
        {
            0 => CanFrame.Classic(0x700 + Slave, new byte[] { 0 }, false),
            1 => CanFrame.Classic(0x700 + Slave, new byte[] { 0x80 }, false),
            2 => CanFrame.Classic(0x700 + Slave, new byte[] { 0x02 }, false),
            3 => CanFrame.Classic(0x700 + Slave, new byte[] { 0x7F, 0 }, false),
            4 => CanFrame.Classic(0x700 + Slave, ReadOnlyMemory<byte>.Empty, false, true),
            5 => CanFrame.Fd(0x700 + Slave, new byte[] { 0x7F }, false, false, false),
            6 => CanFrame.Classic(0x700 + Slave, new byte[] { 0x7F }, true),
            _ => CanFrame.Classic(0x700 + Slave + 1, new byte[] { 0x7F }, false)
        };
        rig.Peer.Transmit(frame);
        await QuiesceAsync(rig.Witness, null);
        Result(rig).Status.Should().Be(SlaveBootStatus.WaitingForErrorControl);
        Started(rig, Slave).Should().BeFalse();
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await QuiesceAsync(rig.Witness, null);
        Started(rig, Slave).Should().BeTrue();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task Missing_Boot_Heartbeat_Uses_The_Existing_Failure_Policy(bool mandatory, bool simultaneous)
    {
        using var rig = OpenMaster(ErrorControlOptions());
        ConfigureErrorControl(rig, mandatory: mandatory, simultaneous: simultaneous);
        await EnterHeartbeatStage(rig);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(100));
        Result(rig).Status.Should().Be(SlaveBootStatus.ErrorControlFailed);
        Result(rig).Index.Should().Be(0x1016);
        Result(rig).Error.Should().BeOfType<TimeoutException>();
        Started(rig, Slave).Should().BeFalse();
        Started(rig, 0).Should().BeFalse();
        rig.Node.State.Should().Be(mandatory ? NmtState.PreOperational : NmtState.Operational);
        rig.Log.Snapshot().Any(f => IsNmt(f, NmtCommand.ResetNode, Slave)).Should().Be(mandatory || simultaneous);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await QuiesceAsync(rig.Witness, null);
        Started(rig, Slave).Should().BeFalse("a failed attempt is not revived by a late heartbeat");
    }

    [Fact]
    public async Task Mandatory_Boot_Deadline_Includes_Error_Control_Wait()
    {
        using var rig = OpenMaster(ErrorControlOptions());
        ConfigureErrorControl(rig, heartbeatMs: 500);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F89, 0, 100);
        await EnterHeartbeatStage(rig);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(100));
        Result(rig).Status.Should().Be(SlaveBootStatus.BootTimeout);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(500));
        Result(rig).Status.Should().Be(SlaveBootStatus.BootTimeout, "the cancelled heartbeat deadline cannot replace the boot failure");
        Started(rig, Slave).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Optional_Error_Control_Failure_Retries_With_A_New_Heartbeat_Window(bool simultaneous)
    {
        using var rig = OpenMaster(ErrorControlOptions(active: true));
        using var peer = new SilentBootPeer(rig.Peer, Slave, r => BootReply(r, 123));
        ConfigureErrorControl(rig, mandatory: false, simultaneous: simultaneous);
        await ElectActive(rig);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Result(rig).Status == SlaveBootStatus.ErrorControlFailed,
            300, "the first attempt times out");
        rig.Node.State.Should().Be(NmtState.Operational);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Result(rig).Status == SlaveBootStatus.WaitingForErrorControl,
            200, "the next attempt owns a fresh window");
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await QuiesceAsync(rig.Witness, null);
        Started(rig, Slave).Should().BeTrue();
        peer.Requests.Should().Be(2);
        Started(rig, 0).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Error_Control_Option_Is_Independent_And_Copied(bool enabled)
    {
        var options = enabled ? ErrorControlOptions() : new CanOpenNodeOptions();
        options.With().EnableBootErrorControl.Should().Be(enabled);
        using var rig = OpenMaster(options.With());
        ConfigureErrorControl(rig);
        await ElectActive(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0);
        await QuiesceAsync(rig.Witness, null);
        Result(rig).Status.Should().Be(enabled ? SlaveBootStatus.WaitingForErrorControl : SlaveBootStatus.StartRequested);
        rig.Log.Snapshot().Should().NotContain(f => f.Id == 0x600 + Slave,
            "this option does not imply an identity or configuration upload");
    }

    private sealed class BootGuardProbe : IDisposable
    {
        private readonly ICanBus _bus;
        private int _requests;
        private bool _toggle;
        public bool Respond { get; set; }
        public int Requests => Volatile.Read(ref _requests);
        public TaskCompletionSource<bool> FirstPoll { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BootGuardProbe(ICanBus bus) { _bus = bus; bus.FrameObserved += OnFrame; }

        private void OnFrame(object? sender, CanReceiveDataView view)
        {
            var frame = view.CanFrame;
            if (!frame.IsRemoteFrame || frame.ID != 0x700 + Slave || frame.IsExtendedFrame) return;
            Interlocked.Increment(ref _requests);
            if (Respond)
            {
                TransmitHeartbeat(_bus, Slave, (byte)(_toggle ? 0xFF : 0x7F));
                _toggle = !_toggle;
            }
            FirstPoll.TrySetResult(true);
        }

        public void Dispose() => _bus.FrameObserved -= OnFrame;
    }

    [Theory]
    [InlineData(40, 1, true)]
    [InlineData(0, 1, false)]
    [InlineData(40, 0, false)]
    public async Task Guard_Time_And_Retry_Factor_Control_Automatic_Guarding(ushort guardMs, byte retries, bool guarded)
    {
        using var rig = OpenMaster(ErrorControlOptions());
        using var probe = new BootGuardProbe(rig.Peer);
        ConfigureErrorControl(rig, heartbeatMs: 0, guardMs: guardMs, retries: retries);
        await ElectActive(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0);
        await QuiesceAsync(rig.Witness, null);
        Started(rig, Slave).Should().BeTrue("guarding startup does not wait for a first response");
        if (guarded) await probe.FirstPoll.Task.WaitAsync(ShortTimeout);
        probe.Requests.Should().Be(guarded ? 1 : 0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(255)]
    public async Task Boot_Guarding_Retry_Factor_Counts_Additional_Missed_Windows(byte retries)
    {
        using var rig = OpenMaster(ErrorControlOptions());
        using var probe = new BootGuardProbe(rig.Peer);
        int timeouts = 0;
        rig.Node.NodeGuardingTimeout += (_, e) => { if (e.ProducerNodeId == Slave) Interlocked.Increment(ref timeouts); };
        ConfigureErrorControl(rig, heartbeatMs: 0, guardMs: 10, retries: retries);
        await ElectActive(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0);
        await QuiesceAsync(rig.Witness, null);
        await probe.FirstPoll.Task.WaitAsync(ShortTimeout);
        for (int i = 0; i < retries; i++)
            await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(10));
        Volatile.Read(ref timeouts).Should().Be(0);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(10));
        Volatile.Read(ref timeouts).Should().Be(1);
    }

    [Fact]
    public async Task Heartbeat_Has_Priority_Over_Existing_And_Configured_Guarding()
    {
        using var rig = OpenMaster(ErrorControlOptions());
        using var probe = new BootGuardProbe(rig.Peer);
        ConfigureErrorControl(rig, guardMs: 20, retries: 1);
        await ElectActive(rig);
        rig.Node.StartNodeGuardingConsumer(Slave, TimeSpan.FromMilliseconds(20), 1);
        await probe.FirstPoll.Task.WaitAsync(ShortTimeout);
        TransmitHeartbeat(rig.Peer, Slave, 0);
        await QuiesceAsync(rig.Witness, null);
        int polls = probe.Requests;
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(50));
        probe.Requests.Should().Be(polls, "heartbeat startup removes the old guarding poller");
        Result(rig).Status.Should().Be(SlaveBootStatus.WaitingForErrorControl);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await QuiesceAsync(rig.Witness, null);
        Started(rig, Slave).Should().BeTrue();
    }
}
