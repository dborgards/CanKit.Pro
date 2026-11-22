using System;
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
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task KeepAlive_Requires_A_Current_Untoggled_Heartbeat(int kind)
    {
        using var rig = OpenMaster(KeepAliveOptions());
        ConfigureErrorControl(rig, mandatory: false);
        await ElectActive(rig);
        rig.Log.Clear();
        long now = rig.Clock.GetTimestamp();
        await rig.Node.PostToActorAsync(() =>
        {
            InvokeBoot(rig.Node, "ObserveKeepAliveState", (uint)(0x700 + Slave), new byte[] { kind == 3 ? (byte)0x85 : (byte)5 }, false, false,
                kind == 0 ? 0L : kind == 2 ? now + rig.Clock.Frequency : now);
        });
        if (kind == 1) await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(100));
        await rig.Node.PostToActorAsync(() => InvokeBoot(rig.Node, "BeginKeepAliveCheck", Slave));
        Result(rig).Status.Should().Be(SlaveBootStatus.KeepAliveFailed);
        Started(rig, Slave).Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task KeepAlive_Guarding_Rejects_Malformed_And_Stale_Replies(int kind)
    {
        using var rig = OpenMaster(KeepAliveOptions());
        Configure(rig, mandatory: false);
        await ElectActive(rig);
        rig.Log.Clear();
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await QuiesceAsync(rig.Witness, null);
        var probe = ((Array)BootField(rig.Node, "_slaveKeepAliveProbes")).GetValue(Slave)!;
        long handoff = (long)probe.GetType().GetField("Handoff")!.GetValue(probe)!;
        long due = (long)probe.GetType().GetField("Due")!.GetValue(probe)!;
        await rig.Node.PostToActorAsync(() => InvokeBoot(rig.Node, "ObserveKeepAliveState",
            (uint)(0x700 + Slave), kind == 2 ? new byte[] { 5, 0 } : new byte[] { 5 }, kind == 0, kind == 1,
            kind == 3 ? handoff - 1 : kind == 4 ? due : handoff));
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(100));
        Result(rig).Status.Should().Be(SlaveBootStatus.KeepAliveFailed);
        Started(rig, Slave).Should().BeFalse();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task KeepAlive_Reset_Requires_Confirmed_Transmission_And_A_Post_Handoff_Bootup(bool fastBootup, bool heartbeat)
    {
        using var rig = OpenDownloadGate(NmtCommand.ResetCommunication, out var gate, KeepAliveOptions());
        ConfigureErrorControl(rig, heartbeatMs: heartbeat ? 100 : 0);
        gate.HoldAfterTransmit = fastBootup;
        IDisposable? allowed = null;
        gate.BeforeHold = () => allowed ??= rig.Witness.AllowPeerVerificationWait(Slave);
        try
        {
            await ElectActive(rig);
            rig.Log.Clear();
            TransmitHeartbeat(rig.Peer, Slave, 0x7F);
            if (!heartbeat)
            {
                await QuiesceAsync(rig.Witness, null);
                TransmitHeartbeat(rig.Peer, Slave, 0x7F);
            }
            await UntilAsync(rig.Clock, rig.Witness, null, () => gate.Entered.IsCompleted, 100, "keep-alive reset is held");
            TransmitHeartbeat(rig.Peer, Slave, 0);
            await QuiesceAsync(rig.Witness, null);
            Result(rig).Status.Should().Be(SlaveBootStatus.CheckingKeepAlive);
            await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(1));
            gate.Release();
            allowed?.Dispose();
            allowed = null;
            await QuiesceAsync(rig.Witness, null);
            if (!fastBootup)
            {
                Result(rig).Status.Should().Be(SlaveBootStatus.CheckingKeepAlive);
                TransmitHeartbeat(rig.Peer, Slave, 0);
                await QuiesceAsync(rig.Witness, null);
            }
            if (heartbeat)
            {
                Result(rig).Status.Should().Be(SlaveBootStatus.WaitingForErrorControl);
                TransmitHeartbeat(rig.Peer, Slave, 0x7F);
                await QuiesceAsync(rig.Witness, null);
            }
            Started(rig, Slave).Should().BeTrue();
        }
        finally { gate.Release(); allowed?.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeepAlive_Reset_Failure_Cannot_Release_Start(bool rejected)
    {
        using var rig = OpenDownloadGate(NmtCommand.ResetCommunication, out var gate, KeepAliveOptions());
        ConfigureErrorControl(rig, mandatory: false);
        gate.Reject = rejected;
        gate.Release();
        await ElectActive(rig);
        rig.Log.Clear();
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Result(rig).Status == SlaveBootStatus.KeepAliveFailed,
            200, "rejected reset or missing boot-up fails keep-alive");
        Started(rig, Slave).Should().BeFalse();
        rig.Node.PeerSdoChannelIsFreeForTests(Slave).Should().BeTrue();
    }

    [Fact]
    public async Task KeepAlive_Bootup_Handler_Rechecks_A_Running_Peer_Without_An_Extra_Reset()
    {
        using var rig = OpenMaster(new CanOpenNodeOptions { EnableBootKeepAlive = true, EnableNmtBootupHandler = true });
        ConfigureErrorControl(rig);
        await ElectActive(rig);
        rig.Log.Clear();
        TransmitHeartbeat(rig.Peer, Slave, 5);
        await QuiesceAsync(rig.Witness, null);
        TransmitHeartbeat(rig.Peer, Slave, 5);
        await QuiesceAsync(rig.Witness, null);
        Result(rig).Status.Should().Be(SlaveBootStatus.InitiallyOperational);
        TransmitHeartbeat(rig.Peer, Slave, 0);
        await QuiesceAsync(rig.Witness, null);
        Result(rig).Status.Should().Be(SlaveBootStatus.WaitingForErrorControl);
        TransmitHeartbeat(rig.Peer, Slave, 5);
        await QuiesceAsync(rig.Witness, null);
        Started(rig, Slave).Should().BeTrue();
        rig.Log.Snapshot().Should().NotContain(f => IsNmt(f, NmtCommand.ResetCommunication, Slave) || IsNmt(f, NmtCommand.ResetNode, Slave));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeepAlive_A_Deferred_Result_Never_Approves_An_Unsent_Reset(bool stillHeld)
    {
        using var rig = OpenMaster(KeepAliveOptions());
        Configure(rig);
        await ElectActive(rig);
        rig.Log.Clear();
        await rig.Node.PostToActorAsync(() =>
        {
            SetBootField(rig.Node, "_coldResetPending", stillHeld);
            InvokeBoot(rig.Node, "CompleteKeepAliveCheck", Slave, (byte)0, null!);
            ((bool[])BootField(rig.Node, "_slaveKeepAliveChecked"))[Slave].Should().BeFalse();
            if (stillHeld)
            {
                SetBootField(rig.Node, "_coldResetPending", false);
                InvokeBoot(rig.Node, "ConsiderStart", Slave, (byte)0x7F);
            }
        });
        await QuiesceAsync(rig.Witness, null);
        Result(rig).Status.Should().Be(SlaveBootStatus.CheckingKeepAlive);
        TransmitHeartbeat(rig.Peer, Slave, 5);
        await QuiesceAsync(rig.Witness, null);
        Result(rig).Status.Should().Be(SlaveBootStatus.InitiallyOperational);
        rig.Log.Snapshot().Should().NotContain(f => f.Id == 0);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task KeepAlive_Failure_Halts_Mandatory_Or_Simultaneous_Announcement_Boot(bool mandatory, bool simultaneous)
    {
        using var rig = OpenMaster(KeepAliveOptions());
        Configure(rig, mandatory, simultaneous);
        await ElectActive(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(100));
        Result(rig).Status.Should().Be(SlaveBootStatus.KeepAliveFailed);
        ((bool)BootField(rig.Node, "_bootHalted")).Should().BeTrue();
        if (mandatory) rig.Node.State.Should().Be(NmtState.PreOperational);
        TransmitHeartbeat(rig.Peer, Slave, 5);
        await QuiesceAsync(rig.Witness, null);
        Started(rig, Slave).Should().BeFalse();
        Started(rig, 0).Should().BeFalse();
    }

    [Fact]
    public async Task KeepAlive_Cached_Heartbeat_Is_Not_Replaced_By_An_Older_Frame_Or_Kept_After_An_Edit()
    {
        using var rig = OpenMaster(KeepAliveOptions());
        ConfigureErrorControl(rig, mandatory: false);
        await ElectActive(rig);
        await rig.Node.PostToActorAsync(() =>
        {
            long now = rig.Clock.GetTimestamp();
            InvokeBoot(rig.Node, "ObserveKeepAliveState", (uint)(0x700 + Slave), new byte[] { 5 }, false, false, now);
            InvokeBoot(rig.Node, "ObserveKeepAliveState", (uint)(0x700 + Slave), new byte[] { 0x7F }, false, false, now - 1);
            InvokeBoot(rig.Node, "ObserveKeepAliveState", (uint)(0x700 + Slave), new byte[] { 0 }, false, false, now - 1);
            InvokeBoot(rig.Node, "ObserveKeepAliveState", (uint)(0x700 + LeftId), new byte[] { 5 }, false, false, now);
            ((byte[])BootField(rig.Node, "_keepAliveHeartbeatState"))[Slave].Should().Be(5);
            ((long[])BootField(rig.Node, "_keepAliveHeartbeatArrival"))[Slave].Should().Be(now);
        });
        rig.Node.AddHeartbeatConsumer(Slave, TimeSpan.FromMilliseconds(200));
        await rig.Node.PostToActorAsync(() => InvokeBoot(rig.Node, "BeginKeepAliveCheck", Slave));
        Result(rig).Status.Should().Be(SlaveBootStatus.KeepAliveFailed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeepAlive_Respects_An_Existing_Guarding_Consumer(bool heartbeatConfigured)
    {
        using var rig = OpenMaster(KeepAliveOptions());
        ConfigureErrorControl(rig, heartbeatMs: heartbeatConfigured ? 100 : 0, mandatory: false);
        await ElectActive(rig);
        if (heartbeatConfigured)
            await rig.Node.PostToActorAsync(() => InvokeBoot(rig.Node, "ObserveKeepAliveState",
                (uint)(0x700 + Slave), new byte[] { 5 }, false, false, rig.Clock.GetTimestamp()));
        rig.Node.StartNodeGuardingConsumer(Slave, TimeSpan.FromSeconds(1), 1);
        await QuiesceAsync(rig.Witness, null);
        if (heartbeatConfigured)
        {
            await rig.Node.PostToActorAsync(() => InvokeBoot(rig.Node, "BeginKeepAliveCheck", Slave));
            Result(rig).Status.Should().Be(SlaveBootStatus.KeepAliveFailed,
                "a guarding consumer must not supply the heartbeat cache");
        }
        else
        {
            TransmitHeartbeat(rig.Peer, Slave, 5);
            await QuiesceAsync(rig.Witness, null);
            TransmitHeartbeat(rig.Peer, Slave, 5);
            await QuiesceAsync(rig.Witness, null);
            Result(rig).Status.Should().Be(SlaveBootStatus.CheckingKeepAlive, "the previous toggle cannot answer the new probe");
            TransmitHeartbeat(rig.Peer, Slave, 0x85);
            await QuiesceAsync(rig.Witness, null);
            Result(rig).Status.Should().Be(SlaveBootStatus.InitiallyOperational);
        }
        Started(rig, Slave).Should().BeFalse();
    }

}
