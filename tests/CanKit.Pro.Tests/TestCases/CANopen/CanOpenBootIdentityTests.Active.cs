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
using CanKit.Pro.CANopen.Sdo;
using Xunit;
using static CanKit.Pro.Tests.TestCases.CANopen.FlyingMasterRig;

namespace CanKit.Pro.Tests.TestCases.CANopen;

// FR-CO-066: active attempts and retries use protocol time and actor/send barriers.
public partial class CanOpenBootIdentityTests
{
    private static CanOpenNodeOptions ActiveBootOptions() => new()
    {
        ActiveSlaveBootRetryInterval = TimeSpan.FromMilliseconds(100),
        SdoTimeout = TimeSpan.FromMilliseconds(50)
    };

    private static async Task ElectActive(MasterRig rig)
    {
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Node.FlyingMasterRole == FlyingMasterRole.Active, 800, "the active master begins probing");
    }

    // This peer never emits a boot-up or heartbeat, even in response to Reset Communication.
    private sealed class SilentBootPeer : IDisposable
    {
        private readonly ICanBus _bus;
        private readonly byte _nodeId;
        private readonly Func<byte[], byte[]?> _reply;
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);

        public SilentBootPeer(ICanBus bus, byte nodeId, Func<byte[], byte[]?> reply)
        {
            _bus = bus;
            _nodeId = nodeId;
            _reply = reply;
            _bus.FrameObserved += OnFrame;
        }

        private void OnFrame(object? sender, CanReceiveDataView view)
        {
            var frame = view.CanFrame;
            if (frame.ID != 0x600 + _nodeId || frame.IsRemoteFrame || frame.IsExtendedFrame) return;
            var request = frame.Data.ToArray();
            if (request.Length != 8 || request[0] != 0x40) return;
            Interlocked.Increment(ref _requests);
            if (_reply(request) is { } response)
                _bus.Transmit(CanFrame.Classic(0x580 + _nodeId, response, isExtendedFrame: false));
        }

        public void Dispose() => _bus.FrameObserved -= OnFrame;
    }

    private static byte[] BootReply(byte[] request, uint value, bool abort = false)
        => new byte[] { abort ? (byte)0x80 : (byte)0x43, request[1], request[2], request[3],
            (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24) };

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Active_Boot_Starts_A_Silent_Slave_Without_Fabricating_An_Nmt_State(bool keepAlive, bool simultaneous)
    {
        using var rig = OpenMaster(ActiveBootOptions());
        using var peer = new SilentBootPeer(rig.Peer, Slave, r => BootReply(r, 123));
        Configure(rig, simultaneous: simultaneous);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F81, Slave, Assigned | BootSlave | MandatorySlave | (keepAlive ? 0x10u : 0));
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F89, 0, 500);
        await ElectActive(rig);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Started(rig, Slave), 300, "SDO reachability suffices without an announcement");
        peer.Requests.Should().Be(1);
        Result(rig).Status.Should().Be(SlaveBootStatus.StartRequested);
        rig.Node.ObjectDictionary.ReadUnsigned(0x1F82, Slave).Should().Be(0);
        rig.Node.State.Should().Be(NmtState.Operational);
        Started(rig, 0).Should().BeFalse();
        var frames = rig.Log.Snapshot();
        if (keepAlive) frames.Should().NotContain(f => IsNmt(f, NmtCommand.ResetCommunication, Slave));
        else frames.FindIndex(f => IsNmt(f, NmtCommand.ResetCommunication, Slave)).Should()
                .BeLessThan(frames.FindIndex(f => f.Id == 0x600 + Slave));
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(600));
        peer.Requests.Should().Be(1, "success stops retries and satisfies the mandatory deadline");
        Result(rig).Status.Should().Be(SlaveBootStatus.StartRequested);
    }

    [Fact]
    public async Task Mandatory_No_Response_Retries_After_The_Interval_Then_Starts()
    {
        using var rig = OpenMaster(ActiveBootOptions());
        bool respond = false;
        using var peer = new SilentBootPeer(rig.Peer, Slave, r => respond ? BootReply(r, 123) : null);
        Configure(rig);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F89, 0, 1000);
        await ElectActive(rig);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Result(rig).Status == SlaveBootStatus.IdentityReadFailed, 100, "the first upload times out");
        peer.Requests.Should().Be(1);
        rig.Node.State.Should().Be(NmtState.PreOperational);
        respond = true;
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(99));
        peer.Requests.Should().Be(1, "an announcement must not bypass the retry interval");
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(1));
        await UntilAsync(rig.Clock, rig.Witness, null, () => Started(rig, Slave), 50, "the second attempt reaches the peer");
        peer.Requests.Should().Be(2);
    }

    [Fact]
    public async Task Zero_Boot_Time_Allows_Mandatory_Retries_Until_Success()
    {
        using var rig = OpenMaster(ActiveBootOptions());
        int attempts = 0;
        using var peer = new SilentBootPeer(rig.Peer, Slave, r => Interlocked.Increment(ref attempts) >= 4 ? BootReply(r, 123) : null);
        Configure(rig);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F89, 0, 0);
        await ElectActive(rig);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Started(rig, Slave), 600, "zero does not impose a boot deadline");
        peer.Requests.Should().Be(4);
        Result(rig).Status.Should().Be(SlaveBootStatus.StartRequested);
    }

    [Fact]
    public async Task Active_Boot_Deadline_Cancels_An_Attempt_Waiting_For_The_Peer_Channel()
    {
        using var rig = OpenMaster(ActiveBootOptions());
        using var peer = new SilentBootPeer(rig.Peer, Slave, r => BootReply(r, 123));
        Configure(rig);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F89, 0, 100);
        var channel = (SemaphoreSlim)typeof(CanOpenNode).GetMethod("PeerSdoChannel", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(rig.Node, new object[] { Slave })!;
        using var held = rig.Witness.HoldPeerSdoChannel(Slave, channel);
        await ElectActive(rig);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Result(rig).Status == SlaveBootStatus.BootTimeout, 150, "the deadline includes the channel wait");
        peer.Requests.Should().Be(0);
        held.Dispose();
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(300));
        peer.Requests.Should().Be(0);
        Started(rig, Slave).Should().BeFalse();
    }

    [Fact]
    public async Task Mandatory_Deadline_Stops_Future_Attempts_And_Late_Success()
    {
        using var rig = OpenMaster(ActiveBootOptions());
        bool respond = false;
        using var peer = new SilentBootPeer(rig.Peer, Slave, r => respond ? BootReply(r, 123) : null);
        Configure(rig);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F89, 0, 220);
        await ElectActive(rig);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Result(rig).Status == SlaveBootStatus.BootTimeout, 300, "the total boot deadline ends the retries");
        peer.Requests.Should().Be(2);
        respond = true;
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromSeconds(1));
        peer.Requests.Should().Be(2);
        Result(rig).Status.Should().Be(SlaveBootStatus.BootTimeout);
        Started(rig, Slave).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Optional_Failures_Retry_After_The_Mandatory_Set_Has_Started(bool mismatch)
    {
        using var rig = OpenMaster(ActiveBootOptions());
        bool respond = false;
        using var optional = new SilentBootPeer(rig.Peer, Slave, r => respond ? BootReply(r, 123) : mismatch ? BootReply(r, 456) : null);
        using var mandatory = new SilentBootPeer(rig.Peer, 6, r => BootReply(r, 123));
        Configure(rig, mandatory: false, simultaneous: true);
        if (mismatch) rig.Node.ObjectDictionary.WriteUnsigned(0x1F84, Slave, 123);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F81, 6, Assigned | BootSlave | MandatorySlave | 0x10u);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F89, 0, 80);
        await ElectActive(rig);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Started(rig, 6), 100, "the first optional failure does not halt simultaneous start");
        rig.Node.State.Should().Be(NmtState.Operational);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(500));
        optional.Requests.Should().BeGreaterThan(1, "optional retries outlive 1F89h");
        Started(rig, Slave).Should().BeFalse();
        Started(rig, 0).Should().BeFalse();
        respond = true;
        await UntilAsync(rig.Clock, rig.Witness, null, () => Started(rig, Slave), 250, "the optional slave can join later");
    }

    [Theory]
    [InlineData(0x05040000u, false)] // peer timeout is an answer, not local error B
    [InlineData(0x06020000u, false)]
    [InlineData(456u, true)]
    public async Task Mandatory_Answered_Failures_Do_Not_Retry_As_No_Response(uint value, bool mismatch)
    {
        using var rig = OpenMaster(ActiveBootOptions());
        using var peer = new SilentBootPeer(rig.Peer, Slave, r => BootReply(r, value, abort: !mismatch));
        Configure(rig);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F84, Slave, 123);
        await ElectActive(rig);
        await QuiesceAsync(rig.Witness, null);
        Result(rig).Status.Should().Be(mismatch ? SlaveBootStatus.IdentityMismatch : SlaveBootStatus.IdentityReadFailed);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromSeconds(1));
        peer.Requests.Should().Be(1);
        Started(rig, Slave).Should().BeFalse();
    }

    [Fact]
    public async Task An_In_Flight_Optional_Retry_Does_Not_Hold_The_Mandatory_Start_Moment()
    {
        using var rig = OpenMaster(ActiveBootOptions());
        int attempts = 0;
        using var optional = new SilentBootPeer(rig.Peer, Slave, r => Interlocked.Increment(ref attempts) == 1 ? BootReply(r, 456) : null);
        using var mandatory = new SilentBootPeer(rig.Peer, 6, r => BootReply(r, 123));
        Configure(rig, mandatory: false, simultaneous: true);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F84, Slave, 123);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F81, 6, Assigned | BootSlave | MandatorySlave | 0x10u);
        var channel = (SemaphoreSlim)typeof(CanOpenNode).GetMethod("PeerSdoChannel", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(rig.Node, new object[] { (byte)6 })!;
        using var held = rig.Witness.HoldPeerSdoChannel(6, channel);
        await ElectActive(rig);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Result(rig).Status == SlaveBootStatus.IdentityMismatch, 50, "the optional first attempt finished");
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(110));
        optional.Requests.Should().Be(2);
        Result(rig).Status.Should().Be(SlaveBootStatus.VerifyingIdentity);
        held.Dispose();
        await QuiesceAsync(rig.Witness, null);
        Started(rig, 6).Should().BeTrue("the optional first attempt already released its boot obligation");
        rig.Node.State.Should().Be(NmtState.Operational);
        Started(rig, Slave).Should().BeFalse();
    }

    [Fact]
    public async Task A_Timeout_At_1018_Is_Not_A_Retryable_Device_Type_Timeout()
    {
        using var rig = OpenMaster(ActiveBootOptions());
        using var peer = new SilentBootPeer(rig.Peer, Slave, r => r[1] == 0 ? BootReply(r, 123) : null);
        Configure(rig);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F85, Slave, 123);
        await ElectActive(rig);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Result(rig).Status == SlaveBootStatus.IdentityReadFailed, 100, "the identity upload times out");
        Result(rig).Index.Should().Be(0x1018);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(500));
        peer.Requests.Should().Be(2);
        Started(rig, Slave).Should().BeFalse();
    }

    [Theory]
    [InlineData(null, 0, false)]
    [InlineData(0x1000, null, false)]
    [InlineData(0x1000, 1, false)]
    [InlineData(0x1000, 0, true)]
    public void A_Local_Timeout_Requires_A_Known_Device_Type_Pair_For_Retry(int? index, int? subindex, bool retry)
    {
        var result = new SlaveBootResult(Slave, SlaveBootStatus.IdentityReadFailed,
            (ushort?)index, (byte?)subindex,
            error: new SdoAbortException(0x1000, 0, SdoAbortCode.SdoProtocolTimedOut, SdoAbortOrigin.Local));
        var classify = typeof(CanOpenNode).GetMethod("IsDeviceTypeNoResponse", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        ((bool)classify.Invoke(null, new object[] { result })!).Should().Be(retry);
    }

    [Fact]
    public async Task Role_Loss_Cancels_A_Scheduled_Retry()
    {
        using var rig = OpenMaster(ActiveBootOptions());
        using var peer = new SilentBootPeer(rig.Peer, Slave, _ => null);
        Configure(rig);
        await ElectActive(rig);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Result(rig).Status == SlaveBootStatus.IdentityReadFailed, 100, "a retry is scheduled");
        rig.Node.StopFlyingMaster();
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromSeconds(1));
        peer.Requests.Should().Be(1);
        rig.Node.GetSlaveBootResults().Should().BeEmpty();
    }

    [Fact]
    public async Task A_New_Boot_Does_Not_Reuse_An_Old_Operational_Byte()
    {
        using var rig = OpenMaster(ActiveBootOptions());
        using var peer = new SilentBootPeer(rig.Peer, Slave, r => BootReply(r, 123));
        Configure(rig);
        await ElectActive(rig);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Started(rig, Slave), 100, "the first boot starts");
        TransmitHeartbeat(rig.Peer, Slave, 5);
        await QuiesceAsync(rig.Witness, null);
        rig.Log.Clear();
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F84, Slave, 123);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Started(rig, Slave), 100, "an earlier Operational byte is not a current announcement");
        Result(rig).Status.Should().Be(SlaveBootStatus.StartRequested);
        rig.Node.ObjectDictionary.ReadUnsigned(0x1F82, Slave).Should().Be(5, "the actual last NMT state stays available");
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(Assigned)]
    [InlineData(Assigned | BootSlave)]
    public async Task Active_Boot_Does_Not_Probe_Excluded_Slaves(uint assignment)
    {
        using var rig = OpenMaster(ActiveBootOptions());
        using var peer = new SilentBootPeer(rig.Peer, Slave, r => BootReply(r, 123));
        Configure(rig);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F81, Slave, assignment | 0x10u);
        if ((assignment & BootSlave) != 0) rig.Node.ObjectDictionary.WriteUnsigned(Startup, 0, SuppressSlaveStart);
        await ElectActive(rig);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromSeconds(1));
        peer.Requests.Should().Be(0);
        Started(rig, Slave).Should().BeFalse();
    }

    [Fact]
    public void Active_Boot_Options_Are_Opt_In_Positive_And_Preserved_By_Copying()
    {
        new CanOpenNodeOptions().ActiveSlaveBootRetryInterval.Should().BeNull();
        var options = ActiveBootOptions();
        options.With().ActiveSlaveBootRetryInterval.Should().Be(options.ActiveSlaveBootRetryInterval);
        options.WithLss(null).ActiveSlaveBootRetryInterval.Should().Be(options.ActiveSlaveBootRetryInterval);
        options.WithErrorHistoryCapacity(4).ActiveSlaveBootRetryInterval.Should().Be(options.ActiveSlaveBootRetryInterval);
        options.WithSrdoConfigurationCheckInterval(TimeSpan.FromSeconds(1)).ActiveSlaveBootRetryInterval.Should().Be(options.ActiveSlaveBootRetryInterval);
        foreach (var interval in new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(-1) })
        {
            var invalid = new CanOpenNodeOptions { ActiveSlaveBootRetryInterval = interval };
            Assert.Throws<ArgumentOutOfRangeException>(() => invalid.With()).ParamName.Should().Be(nameof(CanOpenNodeOptions.ActiveSlaveBootRetryInterval));
        }
    }
}
