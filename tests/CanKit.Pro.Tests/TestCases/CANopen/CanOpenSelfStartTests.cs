using System;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using Xunit;
using static CanKit.Pro.Tests.TestCases.CANopen.FlyingMasterRig;

namespace CanKit.Pro.Tests.TestCases.CANopen;

public partial class CanOpenFlyingMasterTests
{
    private const ushort SelfStartTiming = 0x1F91;

    [Theory]
    [InlineData(true, 2, true)]
    [InlineData(true, 8, false)]
    [InlineData(true, 10, false)]
    [InlineData(false, 2, false)]
    public async Task SelfStart_At_Boot_Uses_Exact_Table_16_Modes(bool enabled, uint startup, bool broadcast)
    {
        var options = new CanOpenNodeOptions { EnableSelfStartingNodes = enabled }.WithLss(null);
        using var rig = OpenMaster(options);
        var od = rig.Node.ObjectDictionary;
        od.TryGet(SelfStartTiming, 0, out _).Should().Be(enabled);
        if (enabled)
        {
            od.ReadUnsigned(SelfStartTiming, 0).Should().Be(3);
            od.ReadUnsigned(SelfStartTiming, 1).Should().Be(100);
            od.ReadUnsigned(SelfStartTiming, 2).Should().Be(500);
            od.ReadUnsigned(SelfStartTiming, 3).Should().Be(15);
            Action writeCount = () => od.WriteUnsigned(SelfStartTiming, 0, 2);
            writeCount.Should().Throw<ArgumentException>();
        }
        await BootSelfStarter(rig, startup);
        // Responses before our request have no bearing on this boot.
        rig.Peer.Transmit(CanFrame.Classic(0x074, Array.Empty<byte>()));
        await StepSelfStart(rig, 499);
        rig.Log.Snapshot().Should().NotContain(f => f.Id == 0x075);
        await StepSelfStart(rig, 1);
        rig.Log.Snapshot().Any(f => f.Id == 0x075 && f.Data.Length == 0).Should().Be(broadcast);
        // LeftId is 16, so 100 ms + 16 × 15 ms must pass after the request.
        await StepSelfStart(rig, 339);
        rig.Log.Snapshot().Should().NotContain(f => IsNmt(f, NmtCommand.Start, 0));
        await StepSelfStart(rig, 1);
        rig.Log.Snapshot().Count(f => IsNmt(f, NmtCommand.Start, 0)).Should().Be(broadcast ? 1 : 0);
        rig.Node.State.Should().Be(broadcast || (enabled && startup == 8) ? NmtState.Operational : NmtState.PreOperational);
    }

    [Theory]
    [InlineData("reply")]
    [InlineData("slot")]
    [InlineData("stop")]
    [InlineData("role")]
    [InlineData("reset")]
    [InlineData("malformed")]
    public async Task SelfStart_Only_A_Live_Request_Can_Start_The_Network(string action)
    {
        using var rig = OpenMaster(new CanOpenNodeOptions { EnableSelfStartingNodes = true });
        var od = rig.Node.ObjectDictionary;
        od.WriteUnsigned(SelfStartTiming, 1, 10);
        od.WriteUnsigned(SelfStartTiming, 2, 50);
        od.WriteUnsigned(SelfStartTiming, 3, 2);
        await BootSelfStarter(rig, 2);
        await StepSelfStart(rig, 50);
        rig.Log.Snapshot().Should().ContainSingle(f => f.Id == 0x075);
        switch (action)
        {
            case "slot":
                await StepSelfStart(rig, 11);
                goto case "reply";
            case "reply":
                rig.Peer.Transmit(CanFrame.Classic(0x074, Array.Empty<byte>()));
                break;
            case "stop":
                TransmitNmt(rig.Peer, NmtCommand.Stop, LeftId);
                break;
            case "role":
                od.WriteUnsigned(Startup, 0, 1);
                break;
            case "reset":
                TransmitNmt(rig.Peer, NmtCommand.ResetCommunication, LeftId);
                break;
            default:
                rig.Peer.Transmit(CanFrame.Classic(0x074, new byte[] { 0 }));
                rig.Peer.Transmit(CanFrame.Classic(0x074, Array.Empty<byte>(), false, true));
                rig.Peer.Transmit(CanFrame.Fd(0x074, Array.Empty<byte>(), false, false, false));
                rig.Peer.Transmit(CanFrame.Classic(0x074, Array.Empty<byte>(), true));
                break;
        }
        await StepSelfStart(rig, 41);
        rig.Log.Snapshot().Should().NotContain(f => IsNmt(f, NmtCommand.Start, 0));
        await StepSelfStart(rig, 1);
        rig.Log.Snapshot().Any(f => IsNmt(f, NmtCommand.Start, 0)).Should().Be(action == "malformed");
        if (action == "reset")
        {
            await StepSelfStart(rig, 8);
            rig.Log.Snapshot().Count(f => f.Id == 0x075).Should().Be(2);
        }
    }

    [Fact]
    public async Task SelfStart_Failed_Probe_Cannot_Authorize_A_Start()
    {
        using var rig = OpenMaster(new CanOpenNodeOptions { EnableSelfStartingNodes = true });
        rig.Node.BeforeControlFrameSendForTests = id => id == 0x075
            ? Task.FromException(new InvalidOperationException("driver unavailable"))
            : Task.CompletedTask;
        await BootSelfStarter(rig, 2);
        await StepSelfStart(rig, 500);
        await StepSelfStart(rig, 1000);
        rig.Log.Snapshot().Should().NotContain(f => f.Id == 0x075 || IsNmt(f, NmtCommand.Start, 0));
        rig.Node.State.Should().Be(NmtState.PreOperational);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelfStart_Loads_Timing_And_Probes_A_Real_Master_At_Power_On(bool dcf)
    {
        using var rig = OpenMaster();
        rig.Node.ObjectDictionary.WriteUnsigned(Startup, 0, 1);
        string header = FlyingMasterEds.Substring(0, FlyingMasterEds.IndexOf("[OptionalObjects]", StringComparison.Ordinal));
        var file = header + """
            [OptionalObjects]
            SupportedObjects=2
            1=0x1F80
            2=0x1F91
            [1F80]
            ParameterName=Startup
            ObjectType=0x7
            DataType=0x0007
            AccessType=rw
            DefaultValue=2
            [1F91]
            ParameterName=Timing
            ObjectType=0x8
            SubNumber=5
            [1F91sub0]
            ParameterName=Count
            DataType=0x0005
            AccessType=ro
            DefaultValue=4
            [1F91sub1]
            ParameterName=Timeout
            DataType=0x0006
            AccessType=wo
            DefaultValue=100
            [1F91sub2]
            ParameterName=Delay
            DataType=0x0006
            AccessType=ro
            DefaultValue=31
            [1F91sub3]
            ParameterName=Slot
            DataType=0x0006
            AccessType=rw
            DefaultValue=65535
            [1F91sub4]
            ParameterName=Unsupported
            DataType=0x0006
            AccessType=rw
            DefaultValue=0
            """;
        if (dcf) file = file.Replace("DefaultValue=31", "DefaultValue=31\nParameterValue=41");
        var description = dcf ? CanOpenDeviceDescription.ParseDcf(file) : CanOpenDeviceDescription.ParseEds(file);
        using var requester = rig.OpenPeerNode(RightId, description, new CanOpenNodeOptions { EnableSelfStartingNodes = true });
        await QuiesceAsync(rig.Witness, null);
        requester.ObjectDictionary.TryGet(SelfStartTiming, 1, out var timeout).Should().BeTrue();
        timeout.Access.Should().Be(OdAccess.WriteOnly);
        requester.ObjectDictionary.TryGet(SelfStartTiming, 2, out var delay).Should().BeTrue();
        delay.Access.Should().Be(OdAccess.ReadWrite);
        requester.ObjectDictionary.ReadUnsigned(SelfStartTiming, 0).Should().Be(3);
        requester.ObjectDictionary.ReadUnsigned(SelfStartTiming, 3).Should().Be(65535);
        requester.ObjectDictionary.TryGet(SelfStartTiming, 4, out _).Should().BeFalse();
        var findings = requester.DeviceDescription!.Findings.Where(f => f.Index == SelfStartTiming).ToList();
        findings.Should().Contain(f => f.Subindex == 0 && f.Outcome == DeviceDescriptionOutcome.Corrected);
        findings.Should().Contain(f => f.Subindex == 2 && f.Outcome == DeviceDescriptionOutcome.Corrected);
        findings.Should().Contain(f => f.Subindex == 4 && f.Outcome == DeviceDescriptionOutcome.Omitted);

        await StepSelfStart(rig, dcf ? 40 : 30);
        rig.Log.Snapshot().Should().NotContain(f => f.Id == 0x075);
        await StepSelfStart(rig, 1);
        await requester.SelfStartSendOutcomeForTests.WaitAsync(ShortTimeout);
        await QuiesceAsync(rig.Witness, null);
        rig.Log.Snapshot().Should().ContainSingle(f => f.Id == 0x075 && f.Data.Length == 0);
        rig.Log.Snapshot().Should().ContainSingle(f => f.Id == 0x074 && f.Data.Length == 0);
        await StepSelfStart(rig, 100 + RightId * 65535);
        rig.Log.Snapshot().Should().NotContain(f => IsNmt(f, NmtCommand.Start, 0));
        requester.State.Should().Be(NmtState.PreOperational);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelfStart_Held_Request_Cannot_Start_Early_Or_Survive_An_Nmt_Stop(bool stop)
    {
        using var rig = OpenMaster(new CanOpenNodeOptions { EnableSelfStartingNodes = true });
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Node.BeforeControlFrameSendForTests = id => id == 0x075 && entered.TrySetResult(true)
            ? release.Task : Task.CompletedTask;
        await BootSelfStarter(rig, 2);
        try
        {
            rig.Clock.Advance(TimeSpan.FromMilliseconds(500));
            await entered.Task.WaitAsync(ShortTimeout);
            rig.Clock.Advance(TimeSpan.FromMilliseconds(2000));
            if (stop) TransmitNmt(rig.Peer, NmtCommand.Stop, LeftId);
            await rig.Witness.SettleAsync();
            rig.Log.Snapshot().Should().NotContain(f => IsNmt(f, NmtCommand.Start, 0));
        }
        finally { release.TrySetResult(true); }
        await rig.Node.SelfStartSendOutcomeForTests.WaitAsync(ShortTimeout);
        await StepSelfStart(rig, 339);
        rig.Log.Snapshot().Should().NotContain(f => IsNmt(f, NmtCommand.Start, 0));
        await StepSelfStart(rig, 1);
        rig.Log.Snapshot().Any(f => IsNmt(f, NmtCommand.Start, 0)).Should().Be(!stop);
    }

    [Fact]
    public async Task SelfStart_Response_Withdraws_A_Broadcast_Queued_Behind_Nmt_Inhibit()
    {
        using var rig = OpenMaster(new CanOpenNodeOptions { EnableSelfStartingNodes = true, EnableNmtInhibit = true });
        var od = rig.Node.ObjectDictionary;
        od.WriteUnsigned(SelfStartTiming, 1, 0);
        od.WriteUnsigned(SelfStartTiming, 2, 1);
        od.WriteUnsigned(SelfStartTiming, 3, 0);
        od.WriteUnsigned(0x102A, 0, 1000); // 100 ms
        await BootSelfStarter(rig, 2);
        await rig.Node.SendNmtCommandAsync(NmtCommand.Stop, 1).WaitAsync(ShortTimeout);
        var previous = rig.Node.NmtQueueForTests;
        rig.Clock.Advance(TimeSpan.FromMilliseconds(1));
        // Stay at 1 ms until the zero timeout and slot have queued Start behind the inhibit.
        var stalled = System.Diagnostics.Stopwatch.StartNew();
        while (ReferenceEquals(previous, rig.Node.NmtQueueForTests))
        {
            if (stalled.Elapsed > ShortTimeout) throw new TimeoutException("Start did not reach the NMT queue.");
            await rig.Witness.SettleAsync();
        }
        rig.Node.NmtQueueForTests.IsCompleted.Should().BeFalse();
        rig.Peer.Transmit(CanFrame.Classic(0x074, Array.Empty<byte>()));
        await rig.Witness.SettleAsync();
        await rig.Node.NmtQueueForTests.WaitAsync(ShortTimeout);
        await StepSelfStart(rig, 200);
        rig.Log.Snapshot().Should().NotContain(f => IsNmt(f, NmtCommand.Start, 0));
        rig.Node.State.Should().Be(NmtState.PreOperational);
    }

    [Theory]
    [InlineData("dispose")]
    [InlineData("stop")]
    [InlineData("role")]
    public async Task SelfStart_Dispatched_Callbacks_Recheck_Their_Authority(string change)
    {
        using var rig = OpenMaster(new CanOpenNodeOptions { EnableSelfStartingNodes = true });
        await BootSelfStarter(rig, 2);
        await StepSelfStart(rig, 500);
        var lastSend = rig.Node.SelfStartSendOutcomeForTests;
        if (change == "stop")
        {
            TransmitNmt(rig.Peer, NmtCommand.Stop, LeftId);
            await QuiesceAsync(rig.Witness, null);
        }
        if (change == "role") rig.Node.ObjectDictionary.WriteUnsigned(Startup, 0, 1);
        await rig.Node.PostToActorAsync(() =>
        {
            // A timer can already be dispatched when the configuration changes, and Dispose
            // publishes its flag before cleanup. Deliver the callbacks in that exact window.
            if (change == "dispose") DisposedField.SetValue(rig.Node, 1);
            try
            {
                InvokeActor(rig.Node, "BeginSelfStart");
                InvokeActor(rig.Node, "RequestMasterDetection");
                InvokeActor(rig.Node, "StartNetworkIfUnmanaged");
                rig.Node.SelfStartSendOutcomeForTests.Should().BeSameAs(lastSend,
                    "neither a probe nor a broadcast may even be queued");
            }
            finally
            {
                if (change == "dispose") DisposedField.SetValue(rig.Node, 0);
            }
        });
    }

    private static async Task BootSelfStarter(MasterRig rig, uint startup)
    {
        rig.Node.ObjectDictionary.WriteUnsigned(Startup, 0, startup);
        rig.Node.StoreParameters();
        TransmitNmt(rig.Peer, NmtCommand.ResetCommunication, LeftId);
        await QuiesceAsync(rig.Witness, null);
        rig.Log.Clear();
    }

    private static async Task StepSelfStart(MasterRig rig, int milliseconds)
    {
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(milliseconds));
        await rig.Node.SelfStartSendOutcomeForTests.WaitAsync(ShortTimeout);
        await QuiesceAsync(rig.Witness, null);
    }
}
