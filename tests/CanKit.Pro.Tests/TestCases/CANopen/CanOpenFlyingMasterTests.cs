using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

/// <summary>
/// NMT flying master and the boot-up that follows a win, bound to CiA 302-2 version 4.1.0
/// (historically DSP 302 clause 5.5): <c>1F80h</c>, <c>1F81h</c>, <c>1F82h</c>, <c>1F89h</c>,
/// <c>1F90h</c>, and the services on <c>0x071</c>, <c>0x072</c>, <c>0x073</c> and <c>0x076</c>.
/// The clock is virtual. A step moves
/// it, then waits until both actors have fired what became due and until a control frame those
/// timers handed to <c>Task.Run</c> has come back, because the negotiation echo restarts the
/// timeslot and has to land before the next step consumes it.
/// </summary>
public class CanOpenFlyingMasterTests : IClassFixture<VirtualAdapterFixture>
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Heartbeat = TimeSpan.FromMilliseconds(2000);

    private const byte LeftId = 0x10;
    private const byte RightId = 0x20;
    private const byte WitnessForLeft = 0x7E;
    private const byte WitnessForRight = 0x7D;

    private const ushort Startup = 0x1F80;
    private const ushort Timing = 0x1F90;
    private const uint MasterBits = 0x21;
    private const uint SuppressSelfStart = 0x04;
    private const uint SuppressSlaveStart = 0x08;
    private const uint Assigned = 0x01;
    private const uint BootSlave = 0x04;
    private const uint MandatorySlave = 0x08;

    private static string NewSession() => $"canopen-fm-{Guid.NewGuid():N}";

    private static ICanBus Open(string session, int channel) => CanBus.Open(
        $"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(VirtualAdapterFixture.Bitrate));

    private static CanOpenNode OpenClockedNode(ICanBus bus, byte nodeId, ManualTimeSource clock,
        CanOpenDeviceDescription? description = null, CanOpenNodeOptions? options = null)
        => new(new CanBusService(bus), nodeId, options ?? new CanOpenNodeOptions(), ownsService: true,
            timeSource: clock, description: description);

    [Fact]
    public void FlyingMaster_Objects_Exist_And_The_Node_Stays_Inactive_Until_Started()
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        var clock = new ManualTimeSource();
        using var node = OpenClockedNode(bus, LeftId, clock);

        node.FlyingMasterRole.Should().Be(FlyingMasterRole.Inactive);
        node.ActiveFlyingMasterNodeId.Should().BeNull();
        var od = node.ObjectDictionary;
        od.ReadUnsigned(Startup, 0x00).Should().Be(0u);
        od.ReadUnsigned(Timing, 0x00).Should().Be(6u);
        od.ReadUnsigned(Timing, 0x01).Should().Be(100u);
        od.ReadUnsigned(Timing, 0x02).Should().Be(500u);
        od.ReadUnsigned(Timing, 0x03).Should().Be(2u);
        od.ReadUnsigned(Timing, 0x04).Should().Be(1500u);
        od.ReadUnsigned(Timing, 0x05).Should().Be(10u);
        od.ReadUnsigned(Timing, 0x06).Should().Be((uint)(4000 + 10 * LeftId));
        od.ReadUnsigned(0x1F81, 0x00).Should().Be(127u);
        od.ReadUnsigned(0x1F81, 0x01).Should().Be(0u);
        od.ReadUnsigned(0x1F82, 0x00).Should().Be(128u);
        od.ReadUnsigned(0x1F82, 0x01).Should().Be(0u);
        od.ReadUnsigned(0x1F89, 0x00).Should().Be(0u);
    }

    [Fact]
    public void FlyingMaster_Rejects_A_Priority_Above_2_And_A_Timeslot_Pair_That_Does_Not_Separate_Levels()
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        var clock = new ManualTimeSource();
        using var node = OpenClockedNode(bus, LeftId, clock);
        var od = node.ObjectDictionary;

        Action priority = () => node.StartFlyingMaster(3, Heartbeat);
        priority.Should().Throw<ArgumentOutOfRangeException>();
        node.FlyingMasterRole.Should().Be(FlyingMasterRole.Inactive);

        Action stored = () => od.WriteUnsigned(Timing, 0x03, 3);
        stored.Should().Throw<ArgumentException>().WithMessage("*06090030*");

        Action deviceSlot = () => od.WriteUnsigned(Timing, 0x05, 0);
        deviceSlot.Should().Throw<ArgumentException>().WithMessage("*06090030*");

        // The public write path cannot store a pair that breaks the rule. The start-up check
        // still has to refuse one that was put there underneath it.
        od.WriteRawUnchecked(Timing, 0x05, new byte[] { 20, 0 });
        Action start = () => node.StartFlyingMaster(1, Heartbeat);
        start.Should().Throw<ArgumentException>();
        (od.ReadUnsigned(Startup, 0x00) & MasterBits).Should().Be(0u);
    }

    [Fact]
    public async Task A_Node_Alone_On_The_Bus_Wins_After_The_Cold_Reset_And_Keeps_1F80h()
    {
        var session = NewSession();
        using var nodeBus = Open(session, 0);
        using var peerBus = Open(session, 1);
        var clock = new ManualTimeSource();
        using var node = OpenClockedNode(nodeBus, LeftId, clock);
        var witness = new ActorWitness(node, peerBus, WitnessForLeft);
        using var log = new FrameLog(peerBus);
        var signals = new ConcurrentQueue<FlyingMasterSignal>();
        node.FlyingMasterChanged += (_, e) => signals.Enqueue(e.Signal);

        Tighten(node);
        node.StartFlyingMaster(0, Heartbeat);
        (node.ObjectDictionary.ReadUnsigned(Startup, 0x00) & MasterBits).Should().Be(MasterBits);

        await UntilAsync(clock, witness, null,
            () => node.FlyingMasterRole == FlyingMasterRole.Active, 800,
            "the only master becomes active");

        node.ActiveFlyingMasterNodeId.Should().Be(LeftId);
        node.ActiveFlyingMasterPriority.Should().Be(0);
        node.ObjectDictionary.ReadUnsigned(0x1017, 0x00).Should().Be(1000u,
            "1017h was 0, so the winner produces a heartbeat at half the standby timeout");
        (node.ObjectDictionary.ReadUnsigned(Startup, 0x00) & MasterBits).Should().Be(MasterBits,
            "the cold Reset Communication restores power-on values, and the start recorded 1F80h there");
        signals.Should().Contain(FlyingMasterSignal.BecameActive);

        var frames = log.Snapshot();
        frames.Should().Contain(f => f.Id == CanOpenCobId.FlyingMasterDetect);
        frames.Should().Contain(f => f.Id == CanOpenCobId.NmtCommand && f.Data.Length >= 2
            && f.Data[0] == (byte)NmtCommand.ResetCommunication && f.Data[1] == 0);
        frames.Should().Contain(f => f.Id == CanOpenCobId.FlyingMasterTrigger);
        frames.Should().Contain(f => f.Id == CanOpenCobId.FlyingMasterClaim
            && f.Data.Length >= 2 && f.Data[0] == 0 && f.Data[1] == LeftId);
    }

    [Fact]
    public async Task A_Worse_Priority_Yields_And_Does_Not_Force_A_New_Election()
    {
        using var pair = OpenPair();
        var (clock, left, right, leftWitness, rightWitness, log) = pair;
        var signals = new ConcurrentQueue<FlyingMasterChangedEventArgs>();
        right.FlyingMasterChanged += (_, e) => signals.Enqueue(e);

        Tighten(left);
        Tighten(right);
        left.StartFlyingMaster(0, Heartbeat);
        right.StartFlyingMaster(2, Heartbeat);

        await UntilAsync(clock, leftWitness, rightWitness,
            () => left.FlyingMasterRole == FlyingMasterRole.Active
                && right.FlyingMasterRole == FlyingMasterRole.Standby, 1200,
            "priority 0 takes the mastership and priority 2 stands by");

        right.ActiveFlyingMasterNodeId.Should().Be(LeftId);
        right.ActiveFlyingMasterPriority.Should().Be(0);
        log.Snapshot().Should().NotContain(f => f.Id == CanOpenCobId.FlyingMasterForce);
        signals.Should().Contain(e =>
            e.Signal == FlyingMasterSignal.BecameStandby
            && e.Role == FlyingMasterRole.Standby
            && e.OtherNodeId == LeftId
            && e.OtherPriority == 0);
    }

    [Fact]
    public async Task A_Better_Priority_Forces_The_Active_Master_To_Restart_And_Then_Wins()
    {
        using var pair = OpenPair();
        var (clock, incumbent, challenger, incumbentWitness, challengerWitness, _) = pair;

        Tighten(incumbent);
        Tighten(challenger);
        incumbent.StartFlyingMaster(2, Heartbeat);
        await UntilAsync(clock, incumbentWitness, challengerWitness,
            () => incumbent.FlyingMasterRole == FlyingMasterRole.Active, 800,
            "the first master wins unopposed");

        var forced = new ConcurrentQueue<FlyingMasterSignal>();
        challenger.FlyingMasterChanged += (_, e) => forced.Enqueue(e.Signal);
        // The incumbent's cold Reset Communication restored the challenger to factory times.
        // Put the short times back now, so Start records them as the power-on values the
        // forced reset will restore.
        Tighten(challenger);
        challenger.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(clock, incumbentWitness, challengerWitness,
            () => challenger.FlyingMasterRole == FlyingMasterRole.Active
                && incumbent.FlyingMasterRole == FlyingMasterRole.Standby, 1200,
            "the better priority deposes the active master");

        challenger.ActiveFlyingMasterNodeId.Should().Be(RightId);
        incumbent.ActiveFlyingMasterNodeId.Should().Be(RightId);
        forced.Should().Contain(FlyingMasterSignal.ForcedRenegotiation);
    }

    [Fact]
    public async Task An_Equal_Claim_Does_Not_Depose_The_Active_Master()
    {
        var session = NewSession();
        using var nodeBus = Open(session, 0);
        using var peerBus = Open(session, 1);
        var clock = new ManualTimeSource();
        using var node = OpenClockedNode(nodeBus, LeftId, clock);
        var witness = new ActorWitness(node, peerBus, WitnessForLeft);

        Tighten(node);
        node.StartFlyingMaster(1, Heartbeat);
        await UntilAsync(clock, witness, null,
            () => node.FlyingMasterRole == FlyingMasterRole.Active, 800,
            "the node becomes the active master");

        peerBus.Transmit(CanFrame.Classic(unchecked((int)CanOpenCobId.FlyingMasterClaim),
            new byte[] { 1, 0x05 }, isExtendedFrame: false));
        await QuiesceAsync(witness, null);

        node.FlyingMasterRole.Should().Be(FlyingMasterRole.Active);
        node.ActiveFlyingMasterNodeId.Should().Be(LeftId);
    }

    [Fact]
    public async Task The_Lower_Node_Id_Wins_When_The_Priority_Is_Equal()
    {
        using var pair = OpenPair(lowerId: 0x10, higherId: 0x30);
        var (clock, lower, higher, lowerWitness, higherWitness, log) = pair;

        Tighten(lower);
        Tighten(higher);
        lower.StartFlyingMaster(1, Heartbeat);
        higher.StartFlyingMaster(1, Heartbeat);

        await UntilAsync(clock, lowerWitness, higherWitness,
            () => lower.FlyingMasterRole == FlyingMasterRole.Active
                && higher.FlyingMasterRole == FlyingMasterRole.Standby, 1200,
            "the same priority level is decided by the node-id term of the timeslot");

        higher.ActiveFlyingMasterNodeId.Should().Be(0x10);
        log.Snapshot().Should().NotContain(f => f.Id == CanOpenCobId.FlyingMasterForce);
    }

    [Fact]
    public async Task Losing_The_Active_Masters_Heartbeat_Starts_A_Warm_Election()
    {
        using var pair = OpenPair();
        var (clock, winner, standby, winnerWitness, standbyWitness, log) = pair;
        var lost = new ConcurrentQueue<FlyingMasterSignal>();
        standby.FlyingMasterChanged += (_, e) => lost.Enqueue(e.Signal);

        var timeout = TimeSpan.FromMilliseconds(300);
        Tighten(winner);
        Tighten(standby);
        winner.StartFlyingMaster(0, timeout);
        standby.StartFlyingMaster(2, timeout);
        await UntilAsync(clock, winnerWitness, standbyWitness,
            () => winner.FlyingMasterRole == FlyingMasterRole.Active
                && standby.FlyingMasterRole == FlyingMasterRole.Standby, 1200,
            "priority 0 wins and priority 2 watches it");

        var resets = Resets(log);
        winner.StopHeartbeatProducer();
        winner.StopFlyingMaster();
        await QuiesceAsync(winnerWitness, standbyWitness);

        await UntilAsync(clock, winnerWitness, standbyWitness,
            () => standby.FlyingMasterRole == FlyingMasterRole.Active
                && winner.FlyingMasterRole == FlyingMasterRole.Inactive, 800,
            "the standby claims the mastership when the winner's heartbeat times out");

        lost.Should().Contain(FlyingMasterSignal.ActiveMasterLost);
        Resets(log).Should().Be(resets, "a warm election does not broadcast Reset Communication again");
        standby.ActiveFlyingMasterNodeId.Should().Be(RightId);
    }

    [Fact]
    public async Task An_Inactive_Node_Does_Not_Answer_And_Stop_Leaves_The_Election()
    {
        var session = NewSession();
        using var nodeBus = Open(session, 0);
        using var peerBus = Open(session, 1);
        var clock = new ManualTimeSource();
        using var node = OpenClockedNode(nodeBus, LeftId, clock);
        var witness = new ActorWitness(node, peerBus, WitnessForLeft);
        using var log = new FrameLog(peerBus);

        peerBus.Transmit(CanFrame.Classic(unchecked((int)CanOpenCobId.FlyingMasterDetect),
            Array.Empty<byte>(), isExtendedFrame: false));
        await QuiesceAsync(witness, null);
        log.Snapshot().Should().NotContain(f => f.Id == CanOpenCobId.FlyingMasterClaim);

        Tighten(node);
        node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(clock, witness, null,
            () => node.FlyingMasterRole == FlyingMasterRole.Active, 800, "the node wins");

        node.StopFlyingMaster();
        await QuiesceAsync(witness, null);
        node.FlyingMasterRole.Should().Be(FlyingMasterRole.Inactive);
        (node.ObjectDictionary.ReadUnsigned(Startup, 0x00) & MasterBits).Should().Be(0u);

        var before = log.Snapshot().Count(f => f.Id == CanOpenCobId.FlyingMasterClaim);
        peerBus.Transmit(CanFrame.Classic(unchecked((int)CanOpenCobId.FlyingMasterDetect),
            Array.Empty<byte>(), isExtendedFrame: false));
        await QuiesceAsync(witness, null);
        log.Snapshot().Count(f => f.Id == CanOpenCobId.FlyingMasterClaim).Should().Be(before);
    }

    [Fact]
    public async Task A_Worse_Claim_During_The_Race_Is_A_Configuration_Error()
    {
        var session = NewSession();
        using var nodeBus = Open(session, 0);
        using var peerBus = Open(session, 1);
        var clock = new ManualTimeSource();
        using var node = OpenClockedNode(nodeBus, LeftId, clock);
        var witness = new ActorWitness(node, peerBus, WitnessForLeft);
        using var log = new FrameLog(peerBus);
        var signals = new ConcurrentQueue<FlyingMasterSignal>();
        node.FlyingMasterChanged += (_, e) => signals.Enqueue(e.Signal);

        Tighten(node);
        node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(clock, witness, null,
            () => log.Snapshot().Any(f => f.Id == CanOpenCobId.FlyingMasterTrigger), 800,
            "the warm boot reaches the timeslot race");

        peerBus.Transmit(CanFrame.Classic(unchecked((int)CanOpenCobId.FlyingMasterClaim),
            new byte[] { 2, 0x05 }, isExtendedFrame: false));
        await QuiesceAsync(witness, null);

        signals.Should().Contain(FlyingMasterSignal.ConfigurationError);
        log.Snapshot().Should().Contain(f => f.Id == CanOpenCobId.FlyingMasterForce);
        node.FlyingMasterRole.Should().Be(FlyingMasterRole.Delaying);
    }

    [Fact]
    public async Task A_Device_Description_Feeds_1F80h_And_1F90h_Through_The_Validated_Path()
    {
        var fileName = Path.GetFileName($"flying-master-{Guid.NewGuid():N}.eds");
        var path = Path.Combine(Path.GetTempPath(), fileName);
        File.WriteAllText(path, FlyingMasterEds);
        try
        {
            var description = CanOpenDeviceDescription.Load(path);
            var session = NewSession();
            using var bus = Open(session, 0);
            using var peer = Open(session, 1);
            var clock = new ManualTimeSource();
            using var node = OpenClockedNode(bus, LeftId, clock, description);
            var witness = new ActorWitness(node, peer, WitnessForLeft);
            await witness.SettleAsync();

            var report = node.DeviceDescription!;
            report.Findings.Should().NotContain(f =>
                (f.Index == Startup || f.Index == Timing) && f.Outcome == DeviceDescriptionOutcome.NotImplemented);

            var od = node.ObjectDictionary;
            od.ReadUnsigned(Startup, 0x00).Should().Be(MasterBits);
            od.ReadUnsigned(Timing, 0x00).Should().Be(6u, "sub-index 00h stays the constant 6");
            od.ReadUnsigned(Timing, 0x03).Should().Be(2u, "priority 9 is outside 0..2, so the default is kept");
            od.ReadUnsigned(Timing, 0x04).Should().Be(200u);
            od.ReadUnsigned(Timing, 0x05).Should().Be(1u);
            od.TryGet(Timing, 0x07, out _).Should().BeFalse();
            node.FlyingMasterRole.Should().Be(FlyingMasterRole.Delaying,
                "bits 0 and 5 start the election, and the described delay has not elapsed");

            Finding(Timing, 0x00).Outcome.Should().Be(DeviceDescriptionOutcome.Corrected);
            Finding(Timing, 0x03).Outcome.Should().Be(DeviceDescriptionOutcome.Corrected);
            Finding(Timing, 0x03).AbortCode.Should().Be(SdoAbortCode.ValueRangeExceeded);
            Finding(Timing, 0x07).Outcome.Should().Be(DeviceDescriptionOutcome.Omitted);

            DeviceDescriptionFinding Finding(ushort index, byte subindex)
                => report.Findings.Single(f => f.Index == index && f.Subindex == subindex);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task The_Active_Master_Enters_Operational_When_Nothing_Suppresses_It()
    {
        using var rig = OpenMaster();
        Tighten(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);

        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Node.FlyingMasterRole == FlyingMasterRole.Active, 800,
            "the only master becomes active");

        rig.Node.State.Should().Be(NmtState.Operational,
            "bit 2 of 1F80h is clear, so the active master starts itself");
    }

    [Fact]
    public async Task An_Assigned_Slave_Is_Reset_Then_Started_And_Its_State_Is_Tracked()
    {
        const byte slave = 0x22;
        using var rig = OpenMaster();
        var od = rig.Node.ObjectDictionary;
        // Bit 2 set: do not enter Operational locally, so the only Start on the bus is the slave's.
        od.WriteUnsigned(Startup, 0x00, SuppressSelfStart);
        od.WriteUnsigned(0x1F81, slave, Assigned | BootSlave);

        Tighten(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Node.FlyingMasterRole == FlyingMasterRole.Active, 800,
            "the master is active before the slave is booted");

        od.ReadUnsigned(0x1F81, slave).Should().Be(Assigned | BootSlave,
            "the cold reset restores the network list recorded at start");
        rig.Log.Snapshot().Should().Contain(f => IsNmt(f, NmtCommand.ResetCommunication, slave));

        rig.Log.Clear();
        TransmitHeartbeat(rig.Peer, slave, 0x00);
        await QuiesceAsync(rig.Witness, null);

        rig.Log.Snapshot().Should().Contain(f => IsNmt(f, NmtCommand.Start, slave));
        od.ReadUnsigned(0x1F82, slave).Should().Be(0u, "the boot-up byte is the tracked state");

        TransmitHeartbeat(rig.Peer, slave, (byte)NmtState.Operational);
        await QuiesceAsync(rig.Witness, null);
        od.ReadUnsigned(0x1F82, slave).Should().Be((uint)NmtState.Operational);
    }

    [Fact]
    public async Task Slaves_Are_Left_Alone_When_Bit_3_Of_1F80h_Suppresses_The_Start()
    {
        const byte slave = 0x22;
        using var rig = OpenMaster();
        var od = rig.Node.ObjectDictionary;
        od.WriteUnsigned(Startup, 0x00, SuppressSelfStart | SuppressSlaveStart);
        od.WriteUnsigned(0x1F81, slave, Assigned | BootSlave);

        Tighten(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Node.FlyingMasterRole == FlyingMasterRole.Active, 800,
            "the master is active");

        TransmitHeartbeat(rig.Peer, slave, 0x00);
        await QuiesceAsync(rig.Witness, null);

        rig.Log.Snapshot().Should().NotContain(f => IsNmt(f, NmtCommand.Start, slave));
        rig.Log.Snapshot().Should().NotContain(f => IsNmt(f, NmtCommand.ResetCommunication, slave));
        rig.Node.State.Should().Be(NmtState.PreOperational);
    }

    [Fact]
    public async Task A_Mandatory_Slave_That_Never_Answers_Times_Out()
    {
        const byte slave = 0x22;
        using var rig = OpenMaster();
        var timedOut = new ConcurrentQueue<byte>();
        rig.Node.FlyingMasterChanged += (_, e) =>
        {
            if (e.Signal == FlyingMasterSignal.SlaveBootTimeout && e.OtherNodeId is { } id)
                timedOut.Enqueue(id);
        };

        var od = rig.Node.ObjectDictionary;
        od.WriteUnsigned(0x1F81, slave, Assigned | BootSlave | MandatorySlave);
        od.WriteUnsigned(0x1F89, 0x00, 100);

        Tighten(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Node.FlyingMasterRole == FlyingMasterRole.Active, 800,
            "the master is active");

        await UntilAsync(rig.Clock, rig.Witness, null, () => timedOut.Contains(slave), 400,
            "1F89h elapses without a heartbeat from the mandatory slave");

        rig.Log.Snapshot().Should().Contain(f => IsNmt(f, NmtCommand.ResetNode, slave),
            "with bits 4 and 6 of 1F80h clear, the missing slave is reset on its own");
        rig.Node.State.Should().Be(NmtState.PreOperational,
            "a missing mandatory slave halts the master's own start");
    }

    [Fact]
    public async Task Request_Nmt_Sends_The_State_It_Names_And_Only_While_Active()
    {
        const byte slave = 0x22;
        using var rig = OpenMaster();
        var od = rig.Node.ObjectDictionary;
        od.WriteUnsigned(0x1F81, slave, Assigned);

        Action tooSoon = () => od.WriteUnsigned(0x1F82, slave, (byte)NmtState.Operational);
        tooSoon.Should().Throw<ArgumentException>().WithMessage("*08000022*");

        od.WriteUnsigned(Startup, 0x00, SuppressSelfStart | SuppressSlaveStart);
        Tighten(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Node.FlyingMasterRole == FlyingMasterRole.Active, 800,
            "the master is active");

        rig.Log.Clear();
        od.WriteUnsigned(0x1F82, slave, (byte)NmtState.Operational);
        await QuiesceAsync(rig.Witness, null);
        rig.Log.Snapshot().Should().Contain(f => IsNmt(f, NmtCommand.Start, slave));
        od.ReadUnsigned(0x1F82, slave).Should().Be(0u, "the request does not overwrite the tracked state");

        Action commandSpecifier = () => od.WriteUnsigned(0x1F82, slave, (byte)NmtCommand.Start);
        commandSpecifier.Should().Throw<ArgumentException>().WithMessage("*06090030*");

        Action count = () => od.WriteUnsigned(0x1F82, 0x00, 1);
        count.Should().Throw<ArgumentException>().WithMessage("*06010002*");

        rig.Log.Clear();
        od.WriteUnsigned(0x1F82, 0x80, (byte)NmtState.Stopped);
        await QuiesceAsync(rig.Witness, null);
        rig.Log.Snapshot().Should().Contain(f => IsNmt(f, NmtCommand.Stop, 0));
    }

    [Fact]
    public void A_Tool_Starts_With_Self_Start_And_Slave_Start_Suppressed()
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        var clock = new ManualTimeSource();
        using var node = OpenClockedNode(bus, LeftId, clock,
            options: new CanOpenNodeOptions { Profile = CanOpenNodeProfile.Tool });

        node.ObjectDictionary.ReadUnsigned(Startup, 0x00)
            .Should().Be(SuppressSelfStart | SuppressSlaveStart);
    }

    [Fact]
    public async Task A_Tool_Does_Not_Start_Itself_Or_Its_Slaves_Until_The_Bits_Are_Cleared()
    {
        const byte slave = 0x22;
        var session = NewSession();
        using var nodeBus = Open(session, 0);
        using var peer = Open(session, 1);
        var clock = new ManualTimeSource();
        using var node = OpenClockedNode(nodeBus, LeftId, clock,
            options: new CanOpenNodeOptions { Profile = CanOpenNodeProfile.Tool });
        var witness = new ActorWitness(node, peer, WitnessForLeft);
        using var log = new FrameLog(nodeBus, peer);

        node.ObjectDictionary.WriteUnsigned(0x1F81, slave, Assigned | BootSlave);
        Tighten(node);
        node.StartFlyingMaster(0, Heartbeat);
        (node.ObjectDictionary.ReadUnsigned(Startup, 0x00) & (SuppressSelfStart | SuppressSlaveStart))
            .Should().Be(SuppressSelfStart | SuppressSlaveStart, "starting the election keeps the tool's suppress bits");

        await UntilAsync(clock, witness, null,
            () => node.FlyingMasterRole == FlyingMasterRole.Active, 800, "the tool wins the election");

        node.State.Should().Be(NmtState.PreOperational);
        log.Snapshot().Should().NotContain(f => IsNmt(f, NmtCommand.ResetCommunication, slave));
        log.Snapshot().Should().NotContain(f => IsNmt(f, NmtCommand.Start, slave));
    }

    [Fact]
    public async Task The_Active_Master_Ignores_Nmt_Addressed_To_Itself_And_A_Broadcast_Reset()
    {
        using var rig = OpenMaster();
        Tighten(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Node.FlyingMasterRole == FlyingMasterRole.Active, 800,
            "the master is active and has started itself");
        rig.Node.State.Should().Be(NmtState.Operational);

        TransmitNmt(rig.Peer, NmtCommand.ResetCommunication, LeftId);
        await QuiesceAsync(rig.Witness, null);
        rig.Node.FlyingMasterRole.Should().Be(FlyingMasterRole.Active);
        rig.Node.State.Should().Be(NmtState.Operational);

        TransmitNmt(rig.Peer, NmtCommand.Stop, LeftId);
        await QuiesceAsync(rig.Witness, null);
        rig.Node.State.Should().Be(NmtState.Operational);

        TransmitNmt(rig.Peer, NmtCommand.ResetNode, 0);
        await QuiesceAsync(rig.Witness, null);
        rig.Node.FlyingMasterRole.Should().Be(FlyingMasterRole.Active,
            "a broadcast reset does not take the active master down");
        rig.Node.State.Should().Be(NmtState.Operational);
    }

    [Fact]
    public async Task Simultaneous_Start_Is_Sent_After_The_Per_Slave_Reset()
    {
        const byte slave = 0x22;
        const uint startAll = 0x02;
        using var rig = OpenMaster();
        var od = rig.Node.ObjectDictionary;
        od.WriteUnsigned(Startup, 0x00, startAll);
        od.WriteUnsigned(0x1F81, slave, Assigned | BootSlave);
        Tighten(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Node.FlyingMasterRole == FlyingMasterRole.Active, 800,
            "the master is active");

        var frames = rig.Log.Snapshot();
        int resetAt = frames.FindIndex(f => IsNmt(f, NmtCommand.ResetCommunication, slave));
        int startAt = frames.FindIndex(f => IsNmt(f, NmtCommand.Start, 0));
        resetAt.Should().BeGreaterOrEqualTo(0);
        startAt.Should().BeGreaterThan(resetAt);
    }

    [Fact]
    public async Task A_Detect_Cycle_Does_Not_Reset_Assigned_Slaves_Again()
    {
        const byte slave = 0x22;
        using var rig = OpenMaster();
        var od = rig.Node.ObjectDictionary;
        od.WriteUnsigned(Startup, 0x00, SuppressSelfStart);
        od.WriteUnsigned(0x1F81, slave, Assigned | BootSlave);
        Tighten(rig.Node);
        od.WriteUnsigned(Timing, 0x06, 30);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Node.FlyingMasterRole == FlyingMasterRole.Active, 800,
            "the master is active");

        int resets = rig.Log.Snapshot().Count(f => IsNmt(f, NmtCommand.ResetCommunication, slave));
        resets.Should().BeGreaterThan(0);
        int claims = rig.Log.Snapshot().Count(f => f.Id == CanOpenCobId.FlyingMasterClaim);
        int triggers = rig.Log.Snapshot().Count(f => f.Id == CanOpenCobId.FlyingMasterTrigger);

        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Log.Snapshot().Count(f => f.Id == CanOpenCobId.FlyingMasterTrigger) > triggers,
            80, "the detect cycle puts a trigger on the bus");
        rig.Node.FlyingMasterRole.Should().Be(FlyingMasterRole.Active,
            "the confirm keeps the master active, so boot-up and NMT self-ignore still apply");

        TransmitHeartbeat(rig.Peer, slave, (byte)NmtState.Operational);
        TransmitNmt(rig.Peer, NmtCommand.ResetCommunication, LeftId);
        await QuiesceAsync(rig.Witness, null);
        od.ReadUnsigned(0x1F82, slave).Should().Be((uint)NmtState.Operational,
            "a slave seen while the detect cycle is running is still tracked");
        rig.Node.State.Should().Be(NmtState.PreOperational);

        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Log.Snapshot().Count(f => f.Id == CanOpenCobId.FlyingMasterClaim) > claims,
            200, "the detect cycle claims the mastership again");

        rig.Node.FlyingMasterRole.Should().Be(FlyingMasterRole.Active);
        rig.Log.Snapshot().Count(f => IsNmt(f, NmtCommand.ResetCommunication, slave)).Should().Be(resets);
    }

    [Fact]
    public async Task An_Equal_Claim_During_The_Detect_Cycle_Yields_To_The_Lower_Node_Id()
    {
        using var rig = OpenMaster();
        Tighten(rig.Node);
        rig.Node.ObjectDictionary.WriteUnsigned(Timing, 0x06, 30);
        rig.Node.StartFlyingMaster(1, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Node.FlyingMasterRole == FlyingMasterRole.Active, 800,
            "the master is active");

        int triggers = rig.Log.Snapshot().Count(f => f.Id == CanOpenCobId.FlyingMasterTrigger);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Log.Snapshot().Count(f => f.Id == CanOpenCobId.FlyingMasterTrigger) > triggers,
            80, "the detect cycle has started its timeslot");
        rig.Node.FlyingMasterRole.Should().Be(FlyingMasterRole.Active);

        rig.Peer.Transmit(CanFrame.Classic(unchecked((int)CanOpenCobId.FlyingMasterClaim),
            new byte[] { 1, 0x05 }, isExtendedFrame: false));
        await QuiesceAsync(rig.Witness, null);

        rig.Node.FlyingMasterRole.Should().Be(FlyingMasterRole.Standby,
            "the detect cycle is a race, so an equal claim already on the bus wins");
        rig.Node.ActiveFlyingMasterNodeId.Should().Be(0x05);
    }

    [Fact]
    public async Task A_Live_Slave_Edit_Is_Not_Restored_As_The_Power_On_Assignment()
    {
        const byte slave = 0x22;
        using var rig = OpenMaster();
        var od = rig.Node.ObjectDictionary;
        od.WriteUnsigned(Startup, 0x00, SuppressSelfStart | SuppressSlaveStart);
        od.WriteUnsigned(0x1F81, slave, Assigned);
        Tighten(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Node.FlyingMasterRole == FlyingMasterRole.Active, 800,
            "the master is active");

        od.WriteUnsigned(0x1F81, slave, Assigned | BootSlave | MandatorySlave);
        od.WriteUnsigned(0x1F89, 0x00, 250);
        rig.Peer.Transmit(CanFrame.Classic(unchecked((int)CanOpenCobId.FlyingMasterForce),
            Array.Empty<byte>(), isExtendedFrame: false));
        await QuiesceAsync(rig.Witness, null);

        od.ReadUnsigned(0x1F81, slave).Should().Be(Assigned,
            "the reset restores the assignment recorded at start, not the live edit");
        od.ReadUnsigned(0x1F89, 0x00).Should().Be(0u);
    }

    [Fact]
    public async Task A_Reset_Rearms_The_Election_From_The_Stored_Flying_Master_Delay()
    {
        using var rig = OpenMaster();
        var od = rig.Node.ObjectDictionary;
        Tighten(rig.Node);
        od.WriteUnsigned(Timing, 0x02, 800);
        od.WriteUnsigned(Startup, 0x00, MasterBits);
        rig.Node.StoreParameters();
        od.WriteUnsigned(Timing, 0x02, 40);

        TransmitNmt(rig.Peer, NmtCommand.ResetCommunication, LeftId);
        await QuiesceAsync(rig.Witness, null);
        rig.Node.FlyingMasterRole.Should().Be(FlyingMasterRole.Delaying);

        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(50));
        rig.Node.FlyingMasterRole.Should().Be(FlyingMasterRole.Delaying,
            "the restored delay is 800 ms, so 50 ms is still inside it");

        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Node.FlyingMasterRole == FlyingMasterRole.Detecting, 900,
            "the stored delay elapses and the node asks who is master");
    }

    [Fact]
    public async Task Rewriting_1F80h_While_Active_Boots_The_Slaves_Again()
    {
        const byte slave = 0x22;
        using var rig = OpenMaster();
        var od = rig.Node.ObjectDictionary;
        od.WriteUnsigned(Startup, 0x00, SuppressSelfStart);
        od.WriteUnsigned(0x1F81, slave, Assigned | BootSlave);
        Tighten(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Node.FlyingMasterRole == FlyingMasterRole.Active, 800,
            "the master is active");

        int resets = rig.Log.Snapshot().Count(f => IsNmt(f, NmtCommand.ResetCommunication, slave));
        resets.Should().BeGreaterThan(0);
        od.WriteUnsigned(Startup, 0x00, MasterBits | SuppressSelfStart);
        await QuiesceAsync(rig.Witness, null);

        rig.Node.FlyingMasterRole.Should().Be(FlyingMasterRole.Active);
        rig.Log.Snapshot().Count(f => IsNmt(f, NmtCommand.ResetCommunication, slave))
            .Should().BeGreaterThan(resets, "1F80h still enables the flying master, so boot-up runs again");
    }

    [Fact]
    public async Task Clearing_1F80h_Stops_An_Election_That_Is_Already_Running()
    {
        using var rig = OpenMaster();
        Tighten(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        rig.Node.FlyingMasterRole.Should().Be(FlyingMasterRole.Delaying);

        rig.Node.ObjectDictionary.WriteUnsigned(Startup, 0x00, 0);
        await QuiesceAsync(rig.Witness, null);
        rig.Node.FlyingMasterRole.Should().Be(FlyingMasterRole.Inactive);

        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(200));
        rig.Node.FlyingMasterRole.Should().Be(FlyingMasterRole.Inactive);
        rig.Log.Snapshot().Should().NotContain(f => f.Id == CanOpenCobId.FlyingMasterDetect);
    }

    [Fact]
    public async Task A_Force_While_Detecting_Restarts_The_Delay()
    {
        using var rig = OpenMaster();
        Tighten(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Log.Snapshot().Any(f => f.Id == CanOpenCobId.FlyingMasterDetect), 800,
            "the node is asking who is master");
        rig.Node.FlyingMasterRole.Should().Be(FlyingMasterRole.Detecting);

        rig.Peer.Transmit(CanFrame.Classic(unchecked((int)CanOpenCobId.FlyingMasterClaim),
            new byte[] { 1 }, isExtendedFrame: false));
        await QuiesceAsync(rig.Witness, null);
        rig.Node.FlyingMasterRole.Should().Be(FlyingMasterRole.Detecting,
            "a claim shorter than the priority and the node-id is ignored");

        rig.Peer.Transmit(CanFrame.Classic(unchecked((int)CanOpenCobId.FlyingMasterForce),
            Array.Empty<byte>(), isExtendedFrame: false));
        await QuiesceAsync(rig.Witness, null);
        rig.Node.FlyingMasterRole.Should().Be(FlyingMasterRole.Delaying,
            "a force before this node is active starts the delay again");
    }

    [Fact]
    public async Task A_Trigger_During_The_Race_Restarts_The_Timeslot()
    {
        using var rig = OpenMaster();
        Tighten(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Log.Snapshot().Any(f => f.Id == CanOpenCobId.FlyingMasterTrigger), 800,
            "the warm boot has reached the timeslot race");
        rig.Node.FlyingMasterRole.Should().Be(FlyingMasterRole.Negotiating);

        int triggers = rig.Log.Snapshot().Count(f => f.Id == CanOpenCobId.FlyingMasterTrigger);
        rig.Peer.Transmit(CanFrame.Classic(unchecked((int)CanOpenCobId.FlyingMasterTrigger),
            Array.Empty<byte>(), isExtendedFrame: false));
        await QuiesceAsync(rig.Witness, null);
        rig.Log.Snapshot().Count(f => f.Id == CanOpenCobId.FlyingMasterTrigger).Should().BeGreaterThan(triggers);

        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(10));
        rig.Node.FlyingMasterRole.Should().Be(FlyingMasterRole.Negotiating,
            "the timeslot started again when the trigger arrived");
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Node.FlyingMasterRole == FlyingMasterRole.Active, 40,
            "the restarted timeslot elapses and this node wins");
    }

    [Fact]
    public async Task A_Trigger_During_The_Detect_Cycle_Keeps_The_Master_Active()
    {
        using var rig = OpenMaster();
        Tighten(rig.Node);
        rig.Node.ObjectDictionary.WriteUnsigned(Timing, 0x06, 30);
        rig.Node.StartFlyingMaster(1, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Node.FlyingMasterRole == FlyingMasterRole.Active, 800,
            "the master is active");

        int triggers = rig.Log.Snapshot().Count(f => f.Id == CanOpenCobId.FlyingMasterTrigger);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Log.Snapshot().Count(f => f.Id == CanOpenCobId.FlyingMasterTrigger) > triggers,
            80, "the detect cycle has put its trigger on the bus");

        rig.Peer.Transmit(CanFrame.Classic(unchecked((int)CanOpenCobId.FlyingMasterTrigger),
            Array.Empty<byte>(), isExtendedFrame: false));
        await QuiesceAsync(rig.Witness, null);
        rig.Node.FlyingMasterRole.Should().Be(FlyingMasterRole.Active);

        rig.Peer.Transmit(CanFrame.Classic(unchecked((int)CanOpenCobId.FlyingMasterClaim),
            new byte[] { 1, 0x05 }, isExtendedFrame: false));
        await QuiesceAsync(rig.Witness, null);
        rig.Node.FlyingMasterRole.Should().Be(FlyingMasterRole.Standby,
            "the foreign trigger restarted the confirm timeslot, so an equal claim still wins the race");
    }

    [Fact]
    public async Task A_Time_Slot_Pair_That_No_Longer_Separates_Levels_Aborts_The_Race()
    {
        using var rig = OpenMaster();
        var signals = new ConcurrentQueue<FlyingMasterSignal>();
        rig.Node.FlyingMasterChanged += (_, e) => signals.Enqueue(e.Signal);
        Tighten(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Node.FlyingMasterRole == FlyingMasterRole.Negotiating, 800,
            "the timeslot race has started");

        // The validated write path refuses this pair. The race checks the pair again when a
        // trigger restarts it, which is the path a restored dictionary can still reach.
        rig.Node.ObjectDictionary.WriteRawUnchecked(Timing, 0x05, new byte[] { 100, 0 });
        rig.Peer.Transmit(CanFrame.Classic(unchecked((int)CanOpenCobId.FlyingMasterTrigger),
            Array.Empty<byte>(), isExtendedFrame: false));
        await QuiesceAsync(rig.Witness, null);

        signals.Should().Contain(FlyingMasterSignal.ConfigurationError);
        rig.Node.FlyingMasterRole.Should().Be(FlyingMasterRole.Inactive);
    }

    [Fact]
    public void A_Priority_Slot_Shorter_Than_Every_Node_Id_Is_Rejected()
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        var clock = new ManualTimeSource();
        using var node = OpenClockedNode(bus, LeftId, clock);
        var od = node.ObjectDictionary;

        Action slot = () => od.WriteUnsigned(Timing, 0x04, 100);
        slot.Should().Throw<ArgumentException>().WithMessage("*06090030*");
        od.ReadUnsigned(Timing, 0x04).Should().Be(1500u);

        Action device = () => od.WriteUnsigned(Timing, 0x05, 0);
        device.Should().Throw<ArgumentException>().WithMessage("*06090030*");
        od.ReadUnsigned(Timing, 0x05).Should().Be(10u);

        Action count = () => od.WriteUnsigned(Timing, 0x00, 6);
        count.Should().Throw<ArgumentException>().WithMessage("*06010002*");
    }

    [Fact]
    public async Task A_Boot_Timeout_After_The_Mandatory_Slave_Was_Seen_Does_Not_Reset_It()
    {
        const byte slave = 0x22;
        using var rig = OpenMaster();
        var timedOut = new ConcurrentQueue<byte>();
        rig.Node.FlyingMasterChanged += (_, e) =>
        {
            if (e.Signal == FlyingMasterSignal.SlaveBootTimeout && e.OtherNodeId is { } id)
                timedOut.Enqueue(id);
        };

        var od = rig.Node.ObjectDictionary;
        od.WriteUnsigned(0x1F81, slave, Assigned | BootSlave | MandatorySlave);
        od.WriteUnsigned(0x1F89, 0x00, 100);
        Tighten(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Node.FlyingMasterRole == FlyingMasterRole.Active, 800,
            "the master is active");

        TransmitHeartbeat(rig.Peer, slave, 0x00);
        await QuiesceAsync(rig.Witness, null);
        rig.Node.State.Should().Be(NmtState.Operational, "the mandatory slave has been seen");

        rig.Log.Clear();
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(150));
        timedOut.Should().BeEmpty("the boot timeout finds the mandatory slave already seen");
        rig.Log.Snapshot().Should().NotContain(f => IsNmt(f, NmtCommand.ResetNode, slave));
        rig.Node.State.Should().Be(NmtState.Operational);
    }

    [Fact]
    public async Task A_Mandatory_Timeout_Resets_Every_Assigned_Slave_When_Bit_4_Is_Set()
    {
        const byte missing = 0x22;
        const byte other = 0x23;
        using var rig = OpenMaster();
        var timedOut = new ConcurrentQueue<byte>();
        rig.Node.FlyingMasterChanged += (_, e) =>
        {
            if (e.Signal == FlyingMasterSignal.SlaveBootTimeout && e.OtherNodeId is { } id)
                timedOut.Enqueue(id);
        };

        var od = rig.Node.ObjectDictionary;
        od.WriteUnsigned(Startup, 0x00, 0x10);
        od.WriteUnsigned(0x1F81, missing, Assigned | MandatorySlave);
        od.WriteUnsigned(0x1F81, other, Assigned);
        od.WriteUnsigned(0x1F89, 0x00, 100);
        Tighten(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null, () => timedOut.Contains(missing), 800,
            "1F89h elapses without the mandatory slave");

        rig.Log.Snapshot().Should().Contain(f => IsNmt(f, NmtCommand.ResetNode, missing));
        rig.Log.Snapshot().Should().Contain(f => IsNmt(f, NmtCommand.ResetNode, other),
            "bit 4 resets every assigned slave, not only the one that timed out");
        timedOut.Should().NotContain(other);
    }

    [Fact]
    public async Task A_Mandatory_Timeout_Stops_Every_Assigned_Slave_When_Bit_6_Is_Set()
    {
        const byte missing = 0x22;
        const byte other = 0x23;
        using var rig = OpenMaster();
        var timedOut = new ConcurrentQueue<byte>();
        rig.Node.FlyingMasterChanged += (_, e) =>
        {
            if (e.Signal == FlyingMasterSignal.SlaveBootTimeout && e.OtherNodeId is { } id)
                timedOut.Enqueue(id);
        };

        var od = rig.Node.ObjectDictionary;
        od.WriteUnsigned(Startup, 0x00, 0x40);
        od.WriteUnsigned(0x1F81, missing, Assigned | MandatorySlave);
        od.WriteUnsigned(0x1F81, other, Assigned);
        od.WriteUnsigned(0x1F89, 0x00, 100);
        Tighten(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null, () => timedOut.Contains(missing), 800,
            "1F89h elapses without the mandatory slave");

        rig.Log.Snapshot().Should().Contain(f => IsNmt(f, NmtCommand.Stop, missing));
        rig.Log.Snapshot().Should().Contain(f => IsNmt(f, NmtCommand.Stop, other));
        rig.Log.Snapshot().Should().NotContain(f => IsNmt(f, NmtCommand.ResetNode, missing));
    }

    [Fact]
    public async Task Request_Nmt_Rejects_This_Node_And_Sends_Reset_And_Pre_Operational()
    {
        const byte slave = 0x22;
        const byte stranger = 0x23;
        using var rig = OpenMaster();
        var od = rig.Node.ObjectDictionary;
        od.WriteUnsigned(Startup, 0x00, SuppressSelfStart | SuppressSlaveStart);
        od.WriteUnsigned(0x1F81, slave, Assigned);
        Tighten(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Node.FlyingMasterRole == FlyingMasterRole.Active, 800,
            "the master is active");

        Action own = () => od.WriteUnsigned(0x1F82, LeftId, 0x06);
        own.Should().Throw<ArgumentException>().WithMessage("*06090030*");
        Action unassigned = () => od.WriteUnsigned(0x1F82, stranger, 0x06);
        unassigned.Should().Throw<ArgumentException>().WithMessage("*06090030*");

        rig.Log.Clear();
        od.WriteUnsigned(0x1F82, slave, 0x06);
        await QuiesceAsync(rig.Witness, null);
        rig.Log.Snapshot().Should().Contain(f => IsNmt(f, NmtCommand.ResetNode, slave));

        rig.Log.Clear();
        od.WriteUnsigned(0x1F82, slave, 0x07);
        await QuiesceAsync(rig.Witness, null);
        rig.Log.Snapshot().Should().Contain(f => IsNmt(f, NmtCommand.ResetCommunication, slave));

        rig.Log.Clear();
        od.WriteUnsigned(0x1F82, slave, 0x7F);
        await QuiesceAsync(rig.Witness, null);
        rig.Log.Snapshot().Should().Contain(f => IsNmt(f, NmtCommand.EnterPreOperational, slave));
        od.ReadUnsigned(0x1F82, slave).Should().Be(0u, "the request does not replace the tracked state");
    }

    [Fact]
    public async Task A_Boot_Time_With_No_Mandatory_Slave_Does_Not_Time_Out()
    {
        using var rig = OpenMaster();
        var timedOut = new ConcurrentQueue<FlyingMasterSignal>();
        rig.Node.FlyingMasterChanged += (_, e) => timedOut.Enqueue(e.Signal);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F89, 0x00, 100);
        Tighten(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Node.FlyingMasterRole == FlyingMasterRole.Active, 800,
            "the master is active");
        rig.Node.State.Should().Be(NmtState.Operational);

        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(200));
        timedOut.Should().NotContain(FlyingMasterSignal.SlaveBootTimeout);
        rig.Node.State.Should().Be(NmtState.Operational);
    }

    [Fact]
    public async Task Simultaneous_Start_Does_Not_Broadcast_When_No_Slave_May_Be_Booted()
    {
        const byte slave = 0x22;
        using var rig = OpenMaster();
        var od = rig.Node.ObjectDictionary;
        od.WriteUnsigned(Startup, 0x00, 0x02);
        od.WriteUnsigned(0x1F81, slave, Assigned);
        Tighten(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Node.FlyingMasterRole == FlyingMasterRole.Active, 800,
            "the master is active");

        rig.Node.State.Should().Be(NmtState.Operational);
        rig.Log.Snapshot().Should().NotContain(f => IsNmt(f, NmtCommand.Start, 0),
            "bit 1 is set, but no assigned slave has the boot bit");
    }

    [Fact]
    public async Task A_Device_Description_Stores_The_Network_List_And_The_Tracked_State()
    {
        var description = CanOpenDeviceDescription.ParseEds(NetworkListEds(accepted: true));
        var session = NewSession();
        using var bus = Open(session, 0);
        using var peer = Open(session, 1);
        var clock = new ManualTimeSource();
        using var node = OpenClockedNode(bus, LeftId, clock, description);
        var witness = new ActorWitness(node, peer, WitnessForLeft);
        await witness.SettleAsync();

        var od = node.ObjectDictionary;
        od.ReadUnsigned(0x1F81, 0x00).Should().Be(127u);
        od.ReadUnsigned(0x1F81, 0x22).Should().Be(Assigned | BootSlave);
        od.ReadUnsigned(0x1F82, 0x00).Should().Be(128u);
        od.ReadUnsigned(0x1F82, 0x22).Should().Be(4u, "a described 1F82h value is the tracked state, not a command");
        od.ReadUnsigned(0x1F89, 0x00).Should().Be(100u);
        od.ReadUnsigned(Timing, 0x04).Should().Be(2000u);
        od.ReadUnsigned(Timing, 0x05).Should().Be(1u);
        node.FlyingMasterRole.Should().Be(FlyingMasterRole.Inactive);

        var report = node.DeviceDescription!;
        report.Findings.Should().NotContain(f =>
            (f.Index == 0x1F81 || f.Index == 0x1F82 || f.Index == 0x1F89)
            && f.Outcome != DeviceDescriptionOutcome.NotImplemented);
    }

    [Fact]
    public async Task A_Device_Description_Corrects_1F81h_And_1F82h_It_Cannot_Apply()
    {
        var description = CanOpenDeviceDescription.ParseEds(NetworkListEds(accepted: false));
        var session = NewSession();
        using var bus = Open(session, 0);
        using var peer = Open(session, 1);
        var clock = new ManualTimeSource();
        using var node = OpenClockedNode(bus, LeftId, clock, description);
        var witness = new ActorWitness(node, peer, WitnessForLeft);
        await witness.SettleAsync();

        var od = node.ObjectDictionary;
        od.ReadUnsigned(0x1F81, 0x00).Should().Be(127u, "sub-index 00h stays the constant 127");
        od.TryGet(0x1F81, 0x81, out _).Should().BeFalse();
        od.ReadUnsigned(0x1F82, 0x00).Should().Be(128u);
        od.ReadUnsigned(0x1F82, 0x22).Should().Be(0u, "999 does not fit in the UNSIGNED8 state");
        od.TryGet(0x1F82, 0x81, out _).Should().BeFalse();

        var report = node.DeviceDescription!;
        Finding(0x1F81, 0x00).Outcome.Should().Be(DeviceDescriptionOutcome.Corrected);
        Finding(0x1F81, 0x81).Outcome.Should().Be(DeviceDescriptionOutcome.Omitted);
        Finding(0x1F82, 0x00).Outcome.Should().Be(DeviceDescriptionOutcome.Corrected);
        Finding(0x1F82, 0x22).Outcome.Should().Be(DeviceDescriptionOutcome.Corrected);
        Finding(0x1F82, 0x81).Outcome.Should().Be(DeviceDescriptionOutcome.Omitted);

        DeviceDescriptionFinding Finding(ushort index, byte subindex)
            => report.Findings.Single(f => f.Index == index && f.Subindex == subindex);
    }

    /// <summary>Short times, still ordered so a better priority level always waits less than a
    /// worse one: the device slot is narrowed before the priority slot, or the write is rejected.</summary>
    private static void Tighten(CanOpenNode node)
    {
        var od = node.ObjectDictionary;
        od.WriteUnsigned(Timing, 0x01, 20);
        od.WriteUnsigned(Timing, 0x02, 40);
        od.WriteUnsigned(Timing, 0x05, 1);
        od.WriteUnsigned(Timing, 0x04, 200);
        od.WriteUnsigned(Timing, 0x06, 0);
    }

    private static int Resets(FrameLog log) => log.Snapshot().Count(f =>
        f.Id == CanOpenCobId.NmtCommand && f.Data.Length >= 2
        && f.Data[0] == (byte)NmtCommand.ResetCommunication && f.Data[1] == 0);

    private static bool IsNmt((uint Id, byte[] Data) frame, NmtCommand command, byte target)
        => frame.Id == CanOpenCobId.NmtCommand && frame.Data.Length >= 2
           && frame.Data[0] == (byte)command && frame.Data[1] == target;

    private static void TransmitHeartbeat(ICanBus peer, byte nodeId, byte state)
        => peer.Transmit(CanFrame.Classic(unchecked((int)CanOpenCobId.Heartbeat(nodeId)),
            new[] { state }, isExtendedFrame: false));

    private static void TransmitNmt(ICanBus peer, NmtCommand command, byte target)
        => peer.Transmit(CanFrame.Classic(unchecked((int)CanOpenCobId.NmtCommand),
            new[] { (byte)command, target }, isExtendedFrame: false));

    private static MasterRig OpenMaster()
    {
        var session = NewSession();
        var nodeBus = Open(session, 0);
        var peer = Open(session, 1);
        var clock = new ManualTimeSource();
        var node = OpenClockedNode(nodeBus, LeftId, clock);
        return new MasterRig(clock, node, new ActorWitness(node, peer, WitnessForLeft),
            new FrameLog(nodeBus, peer), peer, nodeBus);
    }

    private sealed class MasterRig : IDisposable
    {
        private readonly ICanBus _nodeBus;

        public MasterRig(ManualTimeSource clock, CanOpenNode node, ActorWitness witness, FrameLog log,
            ICanBus peer, ICanBus nodeBus)
        {
            Clock = clock;
            Node = node;
            Witness = witness;
            Log = log;
            Peer = peer;
            _nodeBus = nodeBus;
        }

        public ManualTimeSource Clock { get; }
        public CanOpenNode Node { get; }
        public ActorWitness Witness { get; }
        public FrameLog Log { get; }
        public ICanBus Peer { get; }

        public void Dispose()
        {
            Node.Dispose();
            Log.Dispose();
            Peer.Dispose();
            _nodeBus.Dispose();
        }
    }

    private static Pair OpenPair(byte lowerId = LeftId, byte higherId = RightId)
    {
        var session = NewSession();
        var lowerBus = Open(session, 0);
        var higherBus = Open(session, 1);
        var clock = new ManualTimeSource();
        var lower = OpenClockedNode(lowerBus, lowerId, clock);
        var higher = OpenClockedNode(higherBus, higherId, clock);
        return new Pair(clock, lower, higher,
            new ActorWitness(lower, higherBus, WitnessForLeft),
            new ActorWitness(higher, lowerBus, WitnessForRight),
            new FrameLog(lowerBus, higherBus), lowerBus, higherBus);
    }

    private sealed class Pair : IDisposable
    {
        private readonly ICanBus _lowerBus;
        private readonly ICanBus _higherBus;

        public Pair(ManualTimeSource clock, CanOpenNode lower, CanOpenNode higher, ActorWitness lowerWitness,
            ActorWitness higherWitness, FrameLog log, ICanBus lowerBus, ICanBus higherBus)
        {
            Clock = clock;
            Lower = lower;
            Higher = higher;
            LowerWitness = lowerWitness;
            HigherWitness = higherWitness;
            Log = log;
            _lowerBus = lowerBus;
            _higherBus = higherBus;
        }

        public ManualTimeSource Clock { get; }
        public CanOpenNode Lower { get; }
        public CanOpenNode Higher { get; }
        public ActorWitness LowerWitness { get; }
        public ActorWitness HigherWitness { get; }
        public FrameLog Log { get; }

        public void Deconstruct(out ManualTimeSource clock, out CanOpenNode lower, out CanOpenNode higher,
            out ActorWitness lowerWitness, out ActorWitness higherWitness, out FrameLog log)
        {
            clock = Clock;
            lower = Lower;
            higher = Higher;
            lowerWitness = LowerWitness;
            higherWitness = HigherWitness;
            log = Log;
        }

        public void Dispose()
        {
            Lower.Dispose();
            Higher.Dispose();
            Log.Dispose();
            _lowerBus.Dispose();
            _higherBus.Dispose();
        }
    }

    private static async Task UntilAsync(ManualTimeSource clock, ActorWitness left, ActorWitness? right,
        Func<bool> done, int virtualMs, string why)
    {
        for (int elapsed = 0; elapsed < virtualMs && !done(); elapsed += 10)
            await AdvanceAsync(clock, left, right, TimeSpan.FromMilliseconds(10));
        done().Should().BeTrue(why);
    }

    private static async Task AdvanceAsync(ManualTimeSource clock, ActorWitness left, ActorWitness? right, TimeSpan by)
    {
        clock.Advance(by);
        await QuiesceAsync(left, right);
    }

    /// <summary>Two actor iterations, then a real wait for <c>SendControlFrame</c>'s send task,
    /// then two more so a peer handles that frame before the clock moves again.</summary>
    private static async Task QuiesceAsync(ActorWitness left, ActorWitness? right)
    {
        await left.SettleAsync();
        if (right is not null) await right.SettleAsync();
        await Task.Delay(40);
        await left.SettleAsync();
        if (right is not null) await right.SettleAsync();
    }

    /// <summary>
    /// <paramref name="accepted"/> stores a legal network list and a priority slot written
    /// before the device slot. Otherwise the count, the extra sub-index and the state byte are
    /// values the node refuses.
    /// </summary>
    private static string NetworkListEds(bool accepted) => $$"""
        [FileInfo]
        FileName=network-list.eds
        FileVersion=1
        FileRevision=0
        EDSVersion=4.0
        Description=Network list loader test
        CreationTime=10:00AM
        CreationDate=09-26-2026
        CreatedBy=CanKit.Pro tests

        [DeviceInfo]
        VendorName=CanKit.Pro
        VendorNumber=0
        ProductName=Network list
        ProductNumber=0
        RevisionNumber=0
        BaudRate_500=1
        SimpleBootUpSlave=1
        Granularity=8
        NrOfRXPDO=0
        NrOfTXPDO=0

        [MandatoryObjects]
        SupportedObjects=1
        1=0x1000

        [1000]
        ParameterName=Device type
        ObjectType=0x7
        DataType=0x0007
        AccessType=ro
        DefaultValue=0
        PDOMapping=0

        [OptionalObjects]
        SupportedObjects={{(accepted ? 4 : 2)}}
        1=0x1F81
        2=0x1F82
        {{(accepted ? "3=0x1F89\n        4=0x1F90" : "")}}

        [1F81]
        ParameterName=NMT slave assignment
        SubNumber=130
        ObjectType=0x8

        [1F81sub0]
        ParameterName=Highest sub-index supported
        ObjectType=0x7
        DataType=0x0005
        AccessType=ro
        DefaultValue={{(accepted ? 127 : 3)}}
        PDOMapping=0

        [1F81sub22]
        ParameterName=Node 34
        ObjectType=0x7
        DataType=0x0007
        AccessType=rw
        DefaultValue={{(accepted ? 5 : 1)}}
        PDOMapping=0
        {{(accepted ? "" : """

        [1F81sub81]
        ParameterName=Past the array
        ObjectType=0x7
        DataType=0x0007
        AccessType=rw
        DefaultValue=1
        PDOMapping=0
        """)}}

        [1F82]
        ParameterName=Request NMT
        SubNumber=130
        ObjectType=0x8

        [1F82sub0]
        ParameterName=Highest sub-index supported
        ObjectType=0x7
        DataType=0x0005
        AccessType=ro
        DefaultValue={{(accepted ? 128 : 1)}}
        PDOMapping=0

        [1F82sub22]
        ParameterName=Node 34
        ObjectType=0x7
        DataType=0x0005
        AccessType=rw
        DefaultValue={{(accepted ? 4 : 999)}}
        PDOMapping=0
        {{(accepted ? "" : """

        [1F82sub81]
        ParameterName=Past the array
        ObjectType=0x7
        DataType=0x0005
        AccessType=rw
        DefaultValue=4
        PDOMapping=0
        """)}}
        {{(accepted ? """

        [1F89]
        ParameterName=Boot time
        ObjectType=0x7
        DataType=0x0007
        AccessType=rw
        DefaultValue=100
        PDOMapping=0

        [1F90]
        ParameterName=Flying master timing
        SubNumber=6
        ObjectType=0x8

        [1F90sub4]
        ParameterName=Priority time slot
        ObjectType=0x7
        DataType=0x0006
        AccessType=rw
        DefaultValue=2000
        PDOMapping=0

        [1F90sub5]
        ParameterName=Device time slot
        ObjectType=0x7
        DataType=0x0006
        AccessType=rw
        DefaultValue=1
        PDOMapping=0
        """ : "")}}
        """;

    private const string FlyingMasterEds = """
        [FileInfo]
        FileName=flying-master.eds
        FileVersion=1
        FileRevision=0
        EDSVersion=4.0
        Description=Flying master loader test
        CreationTime=10:00AM
        CreationDate=09-26-2026
        CreatedBy=CanKit.Pro tests

        [DeviceInfo]
        VendorName=CanKit.Pro
        VendorNumber=0
        ProductName=Flying master
        ProductNumber=0
        RevisionNumber=0
        BaudRate_500=1
        SimpleBootUpSlave=1
        Granularity=8
        NrOfRXPDO=0
        NrOfTXPDO=0

        [MandatoryObjects]
        SupportedObjects=3
        1=0x1000
        2=0x1001
        3=0x1018

        [1000]
        ParameterName=Device type
        ObjectType=0x7
        DataType=0x0007
        AccessType=ro
        DefaultValue=0
        PDOMapping=0

        [1001]
        ParameterName=Error register
        ObjectType=0x7
        DataType=0x0005
        AccessType=ro
        DefaultValue=0
        PDOMapping=0

        [1018]
        ParameterName=Identity
        SubNumber=2
        ObjectType=0x9

        [1018sub0]
        ParameterName=Highest sub-index supported
        ObjectType=0x7
        DataType=0x0005
        AccessType=ro
        DefaultValue=1
        PDOMapping=0

        [1018sub1]
        ParameterName=Vendor-ID
        ObjectType=0x7
        DataType=0x0007
        AccessType=ro
        DefaultValue=0
        PDOMapping=0

        [OptionalObjects]
        SupportedObjects=2
        1=0x1F80
        2=0x1F90

        [1F80]
        ParameterName=NMT startup
        ObjectType=0x7
        DataType=0x0007
        AccessType=rw
        DefaultValue=0x21
        PDOMapping=0

        [1F90]
        ParameterName=Flying master timing
        SubNumber=8
        ObjectType=0x8

        [1F90sub0]
        ParameterName=Highest sub-index supported
        ObjectType=0x7
        DataType=0x0005
        AccessType=ro
        DefaultValue=4
        PDOMapping=0

        [1F90sub1]
        ParameterName=Timeout
        ObjectType=0x7
        DataType=0x0006
        AccessType=rw
        DefaultValue=20
        PDOMapping=0

        [1F90sub2]
        ParameterName=Delay
        ObjectType=0x7
        DataType=0x0006
        AccessType=rw
        DefaultValue=5000
        PDOMapping=0

        [1F90sub3]
        ParameterName=Priority
        ObjectType=0x7
        DataType=0x0006
        AccessType=rw
        DefaultValue=9
        PDOMapping=0

        [1F90sub4]
        ParameterName=Priority time slot
        ObjectType=0x7
        DataType=0x0006
        AccessType=rw
        DefaultValue=200
        PDOMapping=0

        [1F90sub5]
        ParameterName=Device time slot
        ObjectType=0x7
        DataType=0x0006
        AccessType=rw
        DefaultValue=1
        PDOMapping=0

        [1F90sub6]
        ParameterName=Detect cycle
        ObjectType=0x7
        DataType=0x0006
        AccessType=rw
        DefaultValue=0
        PDOMapping=0

        [1F90sub7]
        ParameterName=Not part of the array
        ObjectType=0x7
        DataType=0x0006
        AccessType=rw
        DefaultValue=1
        PDOMapping=0
        """;

    /// <summary>Queues data frames observed on one bus.</summary>
    private sealed class FrameLog : IDisposable
    {
        private readonly ICanBus[] _buses;
        private readonly object _gate = new();
        private readonly List<(uint Id, byte[] Data)> _frames = new();

        public FrameLog(params ICanBus[] buses)
        {
            _buses = buses;
            foreach (var bus in _buses) bus.FrameObserved += OnFrame;
        }

        public List<(uint Id, byte[] Data)> Snapshot()
        {
            lock (_gate) return _frames.ToList();
        }

        public void Clear()
        {
            lock (_gate) _frames.Clear();
        }

        private void OnFrame(object? sender, CanReceiveDataView e)
        {
            var frame = e.CanFrame;
            if (frame.IsExtendedFrame || frame.IsRemoteFrame) return;
            lock (_gate) _frames.Add(((uint)frame.ID, frame.Data.ToArray()));
        }

        public void Dispose()
        {
            foreach (var bus in _buses) bus.FrameObserved -= OnFrame;
        }
    }

    /// <summary>Same two-round-trip witness as the life-guarding tests: a heartbeat from a peer
    /// nobody consumes, delivered twice, proves the actor has fired the timers the clock made due.</summary>
    private sealed class ActorWitness
    {
        private readonly ICanBus _peerBus;
        private readonly byte _witnessPeer;
        private readonly SemaphoreSlim _seen = new(0, int.MaxValue);

        public ActorWitness(ICanOpenNode node, ICanBus peerBus, byte witnessPeer)
        {
            _peerBus = peerBus;
            _witnessPeer = witnessPeer;
            node.HeartbeatReceived += (_, e) =>
            {
                if (e.ProducerNodeId == witnessPeer) _seen.Release();
            };
        }

        public async Task SettleAsync()
        {
            await PokeAsync();
            await PokeAsync();
        }

        private async Task PokeAsync()
        {
            _peerBus.Transmit(CanFrame.Classic(unchecked((int)CanOpenCobId.Heartbeat(_witnessPeer)),
                new[] { (byte)NmtState.PreOperational }, isExtendedFrame: false));
            if (!await _seen.WaitAsync(ShortTimeout).ConfigureAwait(false))
                throw new TimeoutException($"The node did not report the witness heartbeat within {ShortTimeout}.");
        }
    }
}
