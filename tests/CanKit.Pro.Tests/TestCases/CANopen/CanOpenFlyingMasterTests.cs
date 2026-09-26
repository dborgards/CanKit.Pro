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
/// NMT flying master, on the procedure public descriptions agree on for CiA 302-2 (historically
/// DSP 302 clause 5.5): <c>1F80h</c> bits 0 and 5, the times in <c>1F90h</c>, and the services on
/// <c>0x071</c>, <c>0x072</c>, <c>0x073</c> and <c>0x076</c>. The clock is virtual. A step moves
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

    private static string NewSession() => $"canopen-fm-{Guid.NewGuid():N}";

    private static ICanBus Open(string session, int channel) => CanBus.Open(
        $"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(VirtualAdapterFixture.Bitrate));

    private static CanOpenNode OpenClockedNode(ICanBus bus, byte nodeId, ManualTimeSource clock,
        CanOpenDeviceDescription? description = null)
        => new(new CanBusService(bus), nodeId, new CanOpenNodeOptions(), ownsService: true,
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
        signals.Should().Contain(e => e.Signal == FlyingMasterSignal.BecameStandby && e.OtherNodeId == LeftId);
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
        var path = Path.Combine(Path.GetTempPath(), $"flying-master-{Guid.NewGuid():N}.eds");
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
