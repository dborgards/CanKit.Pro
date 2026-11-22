using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;
using static CanKit.Pro.Tests.TestCases.CANopen.FlyingMasterRig;

namespace CanKit.Pro.Tests.TestCases.CANopen;

// FR-CO-032: §3.1 d includes IDs outside 1F81h in the individual startup reset pass.
public partial class CanOpenBootIdentityTests
{
    private static async Task BeginWarmStartupAsync(MasterRig rig)
    {
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await QuiesceAsync(rig.Witness, null);
        TransmitNmt(rig.Peer, NmtCommand.ResetCommunication, LeftId);
        await QuiesceAsync(rig.Witness, null);
        rig.Log.Clear();
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Node.FlyingMasterRole == FlyingMasterRole.Active, 800, "warm takeover reaches startup");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Warm_Startup_Resets_Unlisted_Ids_And_Excludes_Self_And_KeepAlive(bool suppress)
    {
        using var rig = OpenMaster(KeepAliveOptions());
        Configure(rig);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F81, LeftId, Assigned | BootSlave);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F81, 6, Assigned | BootSlave);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F81, 7, 0x10); // keep-alive also protects an unassigned entry
        rig.Node.ObjectDictionary.WriteUnsigned(Startup, 0, suppress ? SuppressSlaveStart : 0);
        rig.Node.AddHeartbeatConsumer(Slave, TimeSpan.FromSeconds(10));
        TransmitHeartbeat(rig.Peer, Slave, 5);
        await QuiesceAsync(rig.Witness, null);
        await BeginWarmStartupAsync(rig);
        var resets = rig.Log.Snapshot().Where(f => f.Id == 0 && f.Data[0] == (byte)NmtCommand.ResetCommunication)
            .Select(f => (int)f.Data[1]).ToArray();
        if (suppress) resets.Should().BeEmpty();
        else resets.Should().BeEquivalentTo(Enumerable.Range(1, 127).Except(new[] { (int)LeftId, Slave, 7 }),
            "the cold-election broadcast cannot hide omitted node IDs during a warm takeover");
        rig.Node.GetSlaveBootResults().Should().NotContain(r => r.NodeId == 1 || r.NodeId == LeftId);
    }

    private static MasterRig OpenUnlistedResetMaster(out CanOpenFlyingMasterTests.ColdResetGate gate)
    {
        var session = NewSession();
        var bus = Open(session, 0);
        var peer = Open(session, 1);
        var clock = new ManualTimeSource();
        gate = new CanOpenFlyingMasterTests.ColdResetGate(new ClockedCanBusService(bus, clock, sendGate: clock))
        { ResetTarget = 1 };
        var node = new CanOpenNode(gate, LeftId, new CanOpenNodeOptions(), ownsService: true, timeSource: clock);
        return new MasterRig(clock, node, new ActorWitness(node, peer, WitnessForLeft, gate.Confirmations),
            new FrameLog(bus, peer), peer, bus);
    }

    [Fact]
    public async Task Warm_Startup_Reveals_An_Unlisted_Peer_Without_Starting_It()
    {
        using var rig = OpenMaster(new CanOpenNodeOptions { EnableNmtBootupHandler = true, EnableBootKeepAlive = true });
        using var unknown = rig.OpenPeerNode(1);
        using var keepAlive = rig.OpenPeerNode(Slave);
        Configure(rig);
        await rig.Node.SendNmtCommandAsync(NmtCommand.Start, 1);
        await rig.Node.SendNmtCommandAsync(NmtCommand.Start, Slave);
        await QuiesceAsync(rig.Witness, null);
        unknown.State.Should().Be(NmtState.Operational);
        keepAlive.State.Should().Be(NmtState.Operational);
        var announced = new ConcurrentQueue<byte>();
        rig.Node.FlyingMasterChanged += (_, e) =>
        {
            if (e.Signal == FlyingMasterSignal.SlaveBootupReceived && e.OtherNodeId is { } id) announced.Enqueue(id);
        };
        await BeginWarmStartupAsync(rig);
        announced.Should().Contain((byte)1);
        unknown.State.Should().Be(NmtState.PreOperational);
        keepAlive.State.Should().Be(NmtState.Operational);
        Started(rig, 1).Should().BeFalse();
        rig.Node.GetSlaveBootResults().Should().NotContain(r => r.NodeId == 1);
    }

    [Fact]
    public async Task A_Start_Cannot_Pass_An_Unconfirmed_Unlisted_Startup_Reset()
    {
        using var rig = OpenUnlistedResetMaster(out var gate);
        Configure(rig);
        await BeginWarmStartupAsync(rig);
        gate.Entered.IsCompleted.Should().BeTrue();
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await QuiesceAsync(rig.Witness, null);
        Started(rig, Slave).Should().BeFalse();
        var queue = rig.Node.NmtQueueForTests;
        gate.Release();
        await queue.WaitAsync(ShortTimeout);
        await QuiesceAsync(rig.Witness, null);
        var frames = rig.Log.Snapshot();
        int start = frames.FindIndex(f => IsNmt(f, NmtCommand.Start, Slave));
        start.Should().BeGreaterThan(frames.FindLastIndex(f => f.Id == 0 && f.Data[0] == (byte)NmtCommand.ResetCommunication));
        Started(rig, Slave).Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Unlisted_Startup_Resets_Are_Cancelled_With_Their_Boot(int cancellation)
    {
        using var rig = OpenUnlistedResetMaster(out var gate);
        Configure(rig);
        await BeginWarmStartupAsync(rig);
        gate.Entered.IsCompleted.Should().BeTrue("startup must include unlisted node 1");
        var queue = rig.Node.NmtQueueForTests;
        if (cancellation == 0) rig.Node.StopFlyingMaster();
        else if (cancellation == 3) rig.Node.Dispose();
        else await rig.Node.PostToActorAsync(() => InvokeBoot(rig.Node,
            cancellation == 1 ? "EnterStandby" : "HaltBootWork",
            cancellation == 1 ? new object[] { (ushort)0, (byte)2 } : Array.Empty<object>()));
        if (cancellation != 3) await rig.Node.PostToActorAsync(() => { });
        gate.Release();
        Func<Task> finishOldQueue = async () => await queue.WaitAsync(ShortTimeout);
        await finishOldQueue.Should().ThrowAsync<OperationCanceledException>();
        if (cancellation != 3) await QuiesceAsync(rig.Witness, null);
        rig.Log.Snapshot().Should().NotContain(f => f.Id == 0 && f.Data.Length == 2
            && f.Data[0] == (byte)NmtCommand.ResetCommunication && f.Data[1] > 1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Failed_Unlisted_Reset_Does_Not_Create_A_Slave_Result_Or_Break_The_Queue(int failure)
    {
        using var rig = OpenUnlistedResetMaster(out var gate);
        Configure(rig);
        gate.Reject = failure == 0;
        gate.Cancel = failure == 1;
        gate.Fault = failure == 2 ? new InvalidOperationException("unlisted reset failed") : null;
        var errors = new ConcurrentQueue<Exception>();
        rig.Node.BackgroundExceptionOccurred += (_, e) => errors.Enqueue(e);
        await BeginWarmStartupAsync(rig);
        gate.Entered.IsCompleted.Should().BeTrue();
        rig.Log.Snapshot().Should().ContainSingle(f => IsNmt(f, NmtCommand.ResetCommunication, 127));
        rig.Node.GetSlaveBootResults().Should().NotContain(r => r.NodeId == 1);
        if (failure == 1) errors.Should().BeEmpty("cancellation is not a transport failure");
        else errors.Should().ContainSingle();
    }
}
