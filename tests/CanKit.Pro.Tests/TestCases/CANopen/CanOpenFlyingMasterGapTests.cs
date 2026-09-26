using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Core.Exceptions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

public partial class CanOpenFlyingMasterTests
{
    [Fact]
    public async Task The_Role_Is_Readable_On_The_Actor_And_Stop_After_Dispose_Does_Nothing()
    {
        using var rig = OpenMaster();
        Tighten(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Node.FlyingMasterRole == FlyingMasterRole.Active, 800,
            "the master is active");

        await rig.Node.PostToActorAsync(() =>
        {
            rig.Node.FlyingMasterRole.Should().Be(FlyingMasterRole.Active);
            rig.Node.ActiveFlyingMasterNodeId.Should().Be(LeftId);
            rig.Node.ActiveFlyingMasterPriority.Should().Be(0);
        });

        rig.Node.Dispose();
        Action stop = () => rig.Node.StopFlyingMaster();
        stop.Should().NotThrow();
    }

    [Fact]
    public async Task A_Trigger_Or_Force_Before_The_Election_Does_Not_Start_It()
    {
        using var rig = OpenMaster();
        Transmit(rig.Peer, CanOpenCobId.FlyingMasterTrigger);
        Transmit(rig.Peer, CanOpenCobId.FlyingMasterForce);
        Transmit(rig.Peer, CanOpenCobId.FlyingMasterDetect);
        await QuiesceAsync(rig.Witness, null);

        rig.Node.FlyingMasterRole.Should().Be(FlyingMasterRole.Inactive);
        rig.Log.Snapshot().Should().NotContain(f => f.Id == CanOpenCobId.FlyingMasterClaim);
    }

    [Fact]
    public async Task A_Claim_That_Names_This_Node_Or_No_Node_Is_Ignored()
    {
        using var rig = OpenMaster();
        Tighten(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Node.FlyingMasterRole == FlyingMasterRole.Detecting, 200,
            "the node is asking who is master");

        Transmit(rig.Peer, CanOpenCobId.FlyingMasterClaim, 0, LeftId);
        Transmit(rig.Peer, CanOpenCobId.FlyingMasterClaim, 0, 0);
        Transmit(rig.Peer, CanOpenCobId.FlyingMasterClaim, 0, 200);
        await QuiesceAsync(rig.Witness, null);

        rig.Node.FlyingMasterRole.Should().Be(FlyingMasterRole.Detecting);
    }

    [Fact]
    public async Task The_Same_Claim_Again_Does_Not_Announce_Standby_Twice()
    {
        using var pair = OpenPair();
        var (clock, left, right, leftWitness, rightWitness, _) = pair;
        var standby = 0;
        right.FlyingMasterChanged += (_, e) =>
        {
            if (e.Signal == FlyingMasterSignal.BecameStandby) standby++;
        };
        Tighten(left);
        Tighten(right);
        left.StartFlyingMaster(0, Heartbeat);
        right.StartFlyingMaster(2, Heartbeat);
        await UntilAsync(clock, leftWitness, rightWitness,
            () => right.FlyingMasterRole == FlyingMasterRole.Standby, 1200,
            "the worse priority stands by");
        standby.Should().Be(1);

        pair.Transmit(CanOpenCobId.FlyingMasterClaim, 0, LeftId);
        await QuiesceAsync(leftWitness, rightWitness);
        standby.Should().Be(1, "the same master claimed again is still the one we are standing by for");
        right.ActiveFlyingMasterNodeId.Should().Be(LeftId);

        pair.Transmit(CanOpenCobId.FlyingMasterClaim, 0, 0x05);
        await QuiesceAsync(leftWitness, rightWitness);
        standby.Should().Be(2);
        right.ActiveFlyingMasterNodeId.Should().Be(0x05);
    }

    [Fact]
    public async Task An_Application_Consumer_For_The_Winner_Is_Left_In_Place()
    {
        using var pair = OpenPair();
        var (clock, left, right, leftWitness, rightWitness, _) = pair;
        right.AddHeartbeatConsumer(LeftId, TimeSpan.FromMilliseconds(2000));
        Tighten(left);
        Tighten(right);
        // The winner's cold reset restores power-on values. Store first, or that reset deletes
        // the consumer and standby installs its own.
        right.StoreParameters();
        left.StartFlyingMaster(0, Heartbeat);
        right.StartFlyingMaster(2, Heartbeat);
        await UntilAsync(clock, leftWitness, rightWitness,
            () => right.FlyingMasterRole == FlyingMasterRole.Standby, 1200,
            "the worse priority stands by");

        uint watched = right.ObjectDictionary.ReadUnsigned(0x1016, 0x01);
        right.StopFlyingMaster();
        await QuiesceAsync(leftWitness, rightWitness);
        right.ObjectDictionary.ReadUnsigned(0x1016, 0x01).Should().Be(watched,
            "the application was already watching the winner, so standby does not own that consumer");
    }

    [Fact]
    public async Task Bits_Alone_Elect_A_Master_Without_A_Heartbeat()
    {
        using var pair = OpenPair();
        var (clock, left, right, leftWitness, rightWitness, _) = pair;
        Tighten(left);
        Tighten(right);
        left.ObjectDictionary.WriteUnsigned(Startup, 0x00, MasterBits);
        right.ObjectDictionary.WriteUnsigned(Startup, 0x00, MasterBits);
        // Record the bits before the clock moves. The cold reset restores this snapshot; without
        // it the reset would clear 1F80h and the election would stop.
        left.StoreParameters();
        right.StoreParameters();
        await UntilAsync(clock, leftWitness, rightWitness,
            () => left.FlyingMasterRole == FlyingMasterRole.Active
                && right.FlyingMasterRole == FlyingMasterRole.Standby, 1200,
            "equal priority, so the lower node-id wins");

        left.ObjectDictionary.ReadUnsigned(0x1017, 0x00).Should().Be(0u,
            "no standby timeout was configured, so the winner does not invent a producer heartbeat");
        right.ObjectDictionary.ReadUnsigned(0x1016, 0x01).Should().Be(0u,
            "standby has no timeout to watch the winner with");
    }

    [Fact]
    public async Task A_Producer_Heartbeat_Set_By_The_Application_Is_Kept()
    {
        using var rig = OpenMaster();
        Tighten(rig.Node);
        rig.Node.StartHeartbeatProducer(TimeSpan.FromMilliseconds(400));
        rig.Node.StoreParameters();
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Node.FlyingMasterRole == FlyingMasterRole.Active, 800,
            "the master is active");

        rig.Node.ObjectDictionary.ReadUnsigned(0x1017, 0x00).Should().Be(400u);
    }

    [Fact]
    public async Task A_Keep_Alive_Slave_Is_Not_Reset_And_A_Foreign_Heartbeat_Does_Not_Start_It()
    {
        const byte alive = 0x22;
        const byte plain = 0x23;
        const uint keepAlive = 0x10;
        using var rig = OpenMaster();
        var od = rig.Node.ObjectDictionary;
        od.WriteUnsigned(Startup, 0x00, SuppressSelfStart);
        od.WriteUnsigned(0x1F81, alive, Assigned | keepAlive);
        od.WriteUnsigned(0x1F81, plain, Assigned);
        Tighten(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Node.FlyingMasterRole == FlyingMasterRole.Active, 800,
            "the master is active");

        rig.Log.Snapshot().Should().Contain(f => IsNmt(f, NmtCommand.ResetCommunication, plain));
        rig.Log.Snapshot().Should().NotContain(f => IsNmt(f, NmtCommand.ResetCommunication, alive));

        TransmitHeartbeat(rig.Peer, 0x24, (byte)NmtState.PreOperational);
        TransmitHeartbeat(rig.Peer, LeftId, (byte)NmtState.PreOperational);
        TransmitHeartbeat(rig.Peer, plain, 0x01);
        await QuiesceAsync(rig.Witness, null);
        rig.Log.Snapshot().Should().NotContain(f => IsNmt(f, NmtCommand.Start, plain),
            "state 0x01 is not boot-up, stopped or pre-operational");
        od.ReadUnsigned(0x1F82, plain).Should().Be(0x01u);
    }

    [Fact]
    public async Task A_Slave_Is_Started_Once_And_Not_Again_From_The_Same_Boot()
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

        TransmitHeartbeat(rig.Peer, slave, 0x00);
        await QuiesceAsync(rig.Witness, null);
        int starts = rig.Log.Snapshot().Count(f => IsNmt(f, NmtCommand.Start, slave));
        starts.Should().BeGreaterThan(0);

        TransmitHeartbeat(rig.Peer, slave, (byte)NmtState.PreOperational);
        await QuiesceAsync(rig.Witness, null);
        rig.Log.Snapshot().Count(f => IsNmt(f, NmtCommand.Start, slave)).Should().Be(starts);
    }

    [Fact]
    public async Task An_Assigned_Slave_Without_The_Boot_Bit_Is_Not_Started()
    {
        const byte slave = 0x22;
        using var rig = OpenMaster();
        var od = rig.Node.ObjectDictionary;
        od.WriteUnsigned(Startup, 0x00, SuppressSelfStart);
        od.WriteUnsigned(0x1F81, slave, Assigned);
        Tighten(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Node.FlyingMasterRole == FlyingMasterRole.Active, 800,
            "the master is active");

        TransmitHeartbeat(rig.Peer, slave, (byte)NmtState.PreOperational);
        await QuiesceAsync(rig.Witness, null);
        rig.Log.Snapshot().Should().NotContain(f => IsNmt(f, NmtCommand.Start, slave));
        od.ReadUnsigned(0x1F82, slave).Should().Be((uint)NmtState.PreOperational);
    }

    [Fact]
    public async Task Simultaneous_Start_Waits_For_The_Mandatory_Slave()
    {
        const byte mandatory = 0x22;
        const byte bootable = 0x23;
        using var rig = OpenMaster();
        var od = rig.Node.ObjectDictionary;
        od.WriteUnsigned(Startup, 0x00, 0x02);
        od.WriteUnsigned(0x1F81, mandatory, Assigned | MandatorySlave);
        od.WriteUnsigned(0x1F81, bootable, Assigned | BootSlave);
        od.WriteUnsigned(0x1F89, 0x00, 400);
        Tighten(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Node.FlyingMasterRole == FlyingMasterRole.Active, 800,
            "the master is active");

        TransmitHeartbeat(rig.Peer, bootable, (byte)NmtState.PreOperational);
        await QuiesceAsync(rig.Witness, null);
        rig.Log.Snapshot().Should().NotContain(f => IsNmt(f, NmtCommand.Start, 0),
            "one mandatory slave has not been seen, so the broadcast start waits");
        rig.Log.Snapshot().Should().NotContain(f => IsNmt(f, NmtCommand.Start, bootable));

        TransmitHeartbeat(rig.Peer, mandatory, 0x00);
        await QuiesceAsync(rig.Witness, null);
        rig.Log.Snapshot().Should().Contain(f => IsNmt(f, NmtCommand.Start, 0));
        rig.Node.State.Should().Be(NmtState.Operational);

        int starts = rig.Log.Snapshot().Count(f => IsNmt(f, NmtCommand.Start, 0));
        TransmitHeartbeat(rig.Peer, bootable, (byte)NmtState.Stopped);
        await QuiesceAsync(rig.Witness, null);
        rig.Log.Snapshot().Count(f => IsNmt(f, NmtCommand.Start, 0)).Should().Be(starts,
            "the broadcast already went out, so a later slave does not start the network again");
    }

    [Fact]
    public async Task Booting_Again_Does_Not_Start_An_Operational_Master()
    {
        using var rig = OpenMaster();
        Tighten(rig.Node);
        rig.Node.ObjectDictionary.WriteUnsigned(Startup, 0x00, MasterBits);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Node.FlyingMasterRole == FlyingMasterRole.Active, 800,
            "the master is active");
        rig.Node.State.Should().Be(NmtState.Operational);

        rig.Node.ObjectDictionary.WriteUnsigned(Startup, 0x00, MasterBits);
        await QuiesceAsync(rig.Witness, null);
        rig.Node.State.Should().Be(NmtState.Operational);
        rig.Node.FlyingMasterRole.Should().Be(FlyingMasterRole.Active);
    }

    [Fact]
    public async Task A_Mandatory_Slave_Already_Seen_Is_Not_Reset_When_Another_Times_Out()
    {
        const byte missing = 0x22;
        const byte seen = 0x23;
        using var rig = OpenMaster();
        var timedOut = new System.Collections.Concurrent.ConcurrentQueue<byte>();
        rig.Node.FlyingMasterChanged += (_, e) =>
        {
            if (e.Signal == FlyingMasterSignal.SlaveBootTimeout && e.OtherNodeId is { } id)
                timedOut.Enqueue(id);
        };
        var od = rig.Node.ObjectDictionary;
        od.WriteUnsigned(0x1F81, missing, Assigned | MandatorySlave);
        od.WriteUnsigned(0x1F81, seen, Assigned | MandatorySlave);
        od.WriteUnsigned(0x1F89, 0x00, 150);
        Tighten(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Node.FlyingMasterRole == FlyingMasterRole.Active, 800,
            "the master is active");

        TransmitHeartbeat(rig.Peer, seen, 0x00);
        await QuiesceAsync(rig.Witness, null);
        await UntilAsync(rig.Clock, rig.Witness, null, () => timedOut.Contains(missing), 400,
            "the slave that stayed silent times out");

        timedOut.Should().NotContain(seen);
        rig.Log.Snapshot().Should().Contain(f => IsNmt(f, NmtCommand.ResetNode, missing));
        rig.Log.Snapshot().Should().NotContain(f => IsNmt(f, NmtCommand.ResetNode, seen));

        rig.Log.Clear();
        TransmitHeartbeat(rig.Peer, missing, (byte)NmtState.PreOperational);
        await QuiesceAsync(rig.Witness, null);
        rig.Log.Snapshot().Should().NotContain(f => IsNmt(f, NmtCommand.Start, missing),
            "the boot is halted, so a late heartbeat does not start the slave");
    }

    [Fact]
    public async Task A_Slave_Is_Started_From_Boot_Up_Stopped_And_PreOperational()
    {
        const byte booting = 0x21;
        const byte stopped = 0x22;
        const byte preop = 0x23;
        const byte other = 0x24;
        using var rig = OpenMaster();
        var od = rig.Node.ObjectDictionary;
        od.WriteUnsigned(Startup, 0x00, SuppressSelfStart);
        foreach (var id in new byte[] { booting, stopped, preop, other })
            od.WriteUnsigned(0x1F81, id, Assigned | BootSlave);
        Tighten(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Node.FlyingMasterRole == FlyingMasterRole.Active, 800,
            "the master is active");

        TransmitHeartbeat(rig.Peer, booting, 0x00);
        TransmitHeartbeat(rig.Peer, stopped, (byte)NmtState.Stopped);
        TransmitHeartbeat(rig.Peer, preop, (byte)NmtState.PreOperational);
        TransmitHeartbeat(rig.Peer, other, 0x01);
        await QuiesceAsync(rig.Witness, null);

        rig.Log.Snapshot().Should().Contain(f => IsNmt(f, NmtCommand.Start, booting));
        rig.Log.Snapshot().Should().Contain(f => IsNmt(f, NmtCommand.Start, stopped));
        rig.Log.Snapshot().Should().Contain(f => IsNmt(f, NmtCommand.Start, preop));
        rig.Log.Snapshot().Should().NotContain(f => IsNmt(f, NmtCommand.Start, other));
    }

    [Fact]
    public async Task This_Nodes_Own_Heartbeat_Is_Not_A_Slave()
    {
        using var rig = OpenMaster();
        Tighten(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Node.FlyingMasterRole == FlyingMasterRole.Active, 800,
            "the master is active");

        // Own heartbeats are dropped unless this node watches its own id. Watching it lets the
        // boot-up see the frame and ignore it as a slave.
        rig.Node.AddHeartbeatConsumer(LeftId, TimeSpan.FromMilliseconds(500));
        await QuiesceAsync(rig.Witness, null);
        TransmitHeartbeat(rig.Peer, LeftId, (byte)NmtState.Stopped);
        await QuiesceAsync(rig.Witness, null);
        rig.Node.State.Should().Be(NmtState.Operational);
        rig.Node.ObjectDictionary.ReadUnsigned(0x1F82, LeftId).Should().Be(0u);
    }

    [Fact]
    public async Task A_Heartbeat_From_Another_Node_Does_Not_Reclaim_The_Master()
    {
        using var pair = OpenPair();
        var (clock, left, right, leftWitness, rightWitness, _) = pair;
        var lost = 0;
        right.FlyingMasterChanged += (_, e) =>
        {
            if (e.Signal == FlyingMasterSignal.ActiveMasterLost) lost++;
        };
        Tighten(left);
        Tighten(right);
        left.StartFlyingMaster(0, Heartbeat);
        right.StartFlyingMaster(2, Heartbeat);
        await UntilAsync(clock, leftWitness, rightWitness,
            () => right.FlyingMasterRole == FlyingMasterRole.Standby, 1200,
            "the worse priority stands by");

        right.AddHeartbeatConsumer(0x22, TimeSpan.FromMilliseconds(30));
        await AdvanceAsync(clock, leftWitness, rightWitness, TimeSpan.FromMilliseconds(40));
        right.FlyingMasterRole.Should().Be(FlyingMasterRole.Standby);
        right.ActiveFlyingMasterNodeId.Should().Be(LeftId);
        lost.Should().Be(0);

        left.AddHeartbeatConsumer(0x22, TimeSpan.FromMilliseconds(30));
        await AdvanceAsync(clock, leftWitness, rightWitness, TimeSpan.FromMilliseconds(40));
        left.FlyingMasterRole.Should().Be(FlyingMasterRole.Active);
    }

    [Fact]
    public async Task Clearing_The_Detect_Cycle_During_The_Confirm_Does_Not_Arm_Another()
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

        rig.Node.ObjectDictionary.WriteUnsigned(Timing, 0x06, 0);
        int after = rig.Log.Snapshot().Count(f => f.Id == CanOpenCobId.FlyingMasterTrigger);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(250));
        rig.Node.FlyingMasterRole.Should().Be(FlyingMasterRole.Active);
        rig.Log.Snapshot().Count(f => f.Id == CanOpenCobId.FlyingMasterTrigger).Should().Be(after,
            "the detect cycle was cleared, so the confirm does not arm another one");
    }

    [Fact]
    public async Task A_Zero_Device_Slot_Planted_During_The_Race_Aborts_It()
    {
        using var rig = OpenMaster();
        var signals = new System.Collections.Concurrent.ConcurrentQueue<FlyingMasterSignal>();
        rig.Node.FlyingMasterChanged += (_, e) => signals.Enqueue(e.Signal);
        Tighten(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Node.FlyingMasterRole == FlyingMasterRole.Negotiating, 800,
            "the timeslot race has started");

        rig.Node.ObjectDictionary.WriteRawUnchecked(Timing, 0x05, new byte[] { 0, 0 });
        Transmit(rig.Peer, CanOpenCobId.FlyingMasterTrigger);
        await QuiesceAsync(rig.Witness, null);

        signals.Should().Contain(FlyingMasterSignal.ConfigurationError);
        rig.Node.FlyingMasterRole.Should().Be(FlyingMasterRole.Inactive);
    }

    [Fact]
    public void Dictionary_Writes_Outside_The_Flying_Master_Arrays_Are_Refused_Or_Ignored()
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        var clock = new ManualTimeSource();
        using var node = OpenClockedNode(bus, LeftId, clock);
        var od = node.ObjectDictionary;

        Action count = () => od.WriteUnsigned(0x1F81, 0x00, 1);
        count.Should().Throw<ArgumentException>().WithMessage("*06010002*");
        od.Declare(0x1F81, 0x80, OdDataType.Unsigned32, OdAccess.ReadWrite, new byte[4], pdoMappable: false);
        Action past = () => od.WriteUnsigned(0x1F81, 0x80, 1);
        past.Should().Throw<ArgumentException>().WithMessage("*06090011*");
        od.Declare(0x1F81, 0x01, OdDataType.Domain, OdAccess.ReadWrite, Array.Empty<byte>(), pdoMappable: false);
        od.WriteRaw(0x1F81, 0x01, new byte[] { 1, 2 });
        od.ReadRaw(0x1F81, 0x01).Should().Equal(1, 2);

        od.Declare(0x1F89, 0x01, OdDataType.Unsigned32, OdAccess.ReadWrite, new byte[4], pdoMappable: false);
        Action bootSub = () => od.WriteUnsigned(0x1F89, 0x01, 1);
        bootSub.Should().Throw<ArgumentException>().WithMessage("*06090011*");

        od.Declare(0x1F82, 0x81, OdDataType.Unsigned8, OdAccess.ReadWrite, new byte[1], pdoMappable: false);
        Action request = () => od.WriteUnsigned(0x1F82, 0x81, 4);
        request.Should().Throw<ArgumentException>().WithMessage("*06090011*");
        od.Declare(0x1F82, 0x22, OdDataType.Domain, OdAccess.ReadWrite, Array.Empty<byte>(), pdoMappable: false);
        od.WriteRaw(0x1F82, 0x22, new byte[] { 4, 5 });
        od.ReadRaw(0x1F82, 0x22).Should().Equal(4, 5);

        od.Declare(Timing, 0x07, OdDataType.Unsigned16, OdAccess.ReadWrite, new byte[2], pdoMappable: false);
        Action timing = () => od.WriteUnsigned(Timing, 0x07, 1);
        timing.Should().Throw<ArgumentException>().WithMessage("*06090011*");
        od.Declare(Timing, 0x01, OdDataType.Domain, OdAccess.ReadWrite, Array.Empty<byte>(), pdoMappable: false);
        od.WriteRaw(Timing, 0x01, new byte[] { 9 });
        od.ReadRaw(Timing, 0x01).Should().Equal(9);

        // The device-slot write of 0 is rejected before the pair check. A priority-slot write
        // still has to see a device slot that was planted at 0 underneath the validator.
        od.Declare(Timing, 0x05, OdDataType.Unsigned16, OdAccess.ReadWrite, new byte[] { 1, 0 }, pdoMappable: false);
        od.WriteRawUnchecked(Timing, 0x05, new byte[] { 0, 0 });
        Action priority = () => od.WriteUnsigned(Timing, 0x04, 1500);
        priority.Should().Throw<ArgumentException>().WithMessage("*06090030*");
    }

    [Fact]
    public async Task A_Described_1F90h_Count_Of_Six_Skips_A_Slot_It_Cannot_Read()
    {
        var description = CanOpenDeviceDescription.ParseEds(TimingEds);
        var session = NewSession();
        using var bus = Open(session, 0);
        using var peer = Open(session, 1);
        var clock = new ManualTimeSource();
        using var node = OpenClockedNode(bus, LeftId, clock, description);
        var witness = new ActorWitness(node, peer, WitnessForLeft);
        await witness.SettleAsync();

        var od = node.ObjectDictionary;
        od.ReadUnsigned(Timing, 0x00).Should().Be(6u);
        od.ReadUnsigned(Timing, 0x05).Should().Be(10u, "1F90h:05 is not in the file, so the default device slot stays");
        node.DeviceDescription!.Findings.Should().NotContain(f =>
            f.Index == Timing && f.Subindex == 0 && f.Outcome == DeviceDescriptionOutcome.Corrected);
        node.DeviceDescription.Findings.Should().Contain(f =>
            f.Index == Timing && f.Subindex == 0x04 && f.Outcome == DeviceDescriptionOutcome.Corrected);

        var omitted = CanOpenDeviceDescription.ParseEds(TimingEds.Replace(
            """
        [1F90sub5]
        ParameterName=Device time slot
        ObjectType=0x7
        DataType=0x0006
        AccessType=rw
        DefaultValue=later
        PDOMapping=0
        """, ""));
        using var bus2 = Open(session + "b", 0);
        using var peer2 = Open(session + "b", 1);
        using var node2 = OpenClockedNode(bus2, LeftId, clock, omitted);
        var witness2 = new ActorWitness(node2, peer2, WitnessForLeft);
        await witness2.SettleAsync();
        node2.ObjectDictionary.ReadUnsigned(Timing, 0x05).Should().Be(10u,
            "1F90h:05 is absent, so the default device slot stays");
    }

    [Fact]
    public async Task A_Restore_Skips_A_Width_That_Changed_And_Zeroes_A_Grown_Consumer()
    {
        var description = CanOpenDeviceDescription.ParseEds(RestoreEds);
        var session = NewSession();
        using var bus = Open(session, 0);
        using var peer = Open(session, 1);
        var clock = new ManualTimeSource();
        using var node = OpenClockedNode(bus, LeftId, clock, description);
        var witness = new ActorWitness(node, peer, WitnessForLeft);
        await witness.SettleAsync();
        var od = node.ObjectDictionary;

        od.Declare(0x2000, 0x01, OdDataType.Unsigned16, OdAccess.ReadWrite, new byte[] { 7, 0 }, pdoMappable: false);
        od.Declare(0x2000, 0x02, OdDataType.Unsigned8, OdAccess.ReadWrite, new byte[] { 9 }, pdoMappable: false);
        od.WriteRaw(0x2100, 0x00, new byte[] { (byte)'x' });
        node.AddHeartbeatConsumer(0x11, TimeSpan.FromMilliseconds(50));
        node.AddHeartbeatConsumer(0x12, TimeSpan.FromMilliseconds(50));
        od.ReadUnsigned(0x1016, 0x02).Should().NotBe(0u);

        node.RestoreDefaultParameters();
        await QuiesceAsync(witness, null);
        TransmitNmt(peer, NmtCommand.ResetNode, LeftId);
        await QuiesceAsync(witness, null);

        od.ReadRaw(0x2000, 0x01).Should().Equal(new byte[] { 7, 0 },
            "the stored width no longer matches, so the reset leaves the entry");
        od.ReadRaw(0x2000, 0x02).Should().Equal(new byte[] { 9 },
            "an application sub-index the snapshot does not hold is left alone");
        od.ReadRaw(0x2100, 0x00).Should().Equal((byte)'h', (byte)'i');
        od.ReadUnsigned(0x1016, 0x02).Should().Be(0u, "a consumer slot grown after the snapshot is zeroed");
    }

    [Fact]
    public async Task Frames_The_Subscription_Does_Not_Want_Are_Dropped()
    {
        using var rig = OpenMaster();
        Transmit(rig.Peer, 0x001, 0);
        Transmit(rig.Peer, CanOpenCobId.FlyingMasterClaim, 1, 0x05);
        Transmit(rig.Peer, CanOpenCobId.FlyingMasterTrigger);
        Transmit(rig.Peer, CanOpenCobId.FlyingMasterDetect);
        Transmit(rig.Peer, CanOpenCobId.FlyingMasterForce);
        Transmit(rig.Peer, 0x181, 0);
        Transmit(rig.Peer, 0x780, 0);
        rig.Peer.Transmit(CanFrame.Classic(0x123, new byte[] { 1 }, isExtendedFrame: true));
        await QuiesceAsync(rig.Witness, null);
        rig.Node.FlyingMasterRole.Should().Be(FlyingMasterRole.Inactive);
    }

    [Fact]
    public async Task The_Active_Master_Ignores_A_Broadcast_Stop_And_Takes_A_Broadcast_Start()
    {
        using var rig = OpenMaster();
        Tighten(rig.Node);
        rig.Node.ObjectDictionary.WriteUnsigned(Startup, 0x00, MasterBits | SuppressSelfStart);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Node.FlyingMasterRole == FlyingMasterRole.Active, 800,
            "the master is active");
        rig.Node.State.Should().Be(NmtState.PreOperational);

        TransmitNmt(rig.Peer, NmtCommand.Stop, 0);
        await QuiesceAsync(rig.Witness, null);
        rig.Node.State.Should().Be(NmtState.PreOperational, "a broadcast stop does not stop the active master");
        rig.Node.FlyingMasterRole.Should().Be(FlyingMasterRole.Active);

        TransmitNmt(rig.Peer, NmtCommand.Start, 0);
        await QuiesceAsync(rig.Witness, null);
        rig.Node.State.Should().Be(NmtState.Operational, "a broadcast start still applies");

        TransmitNmt(rig.Peer, NmtCommand.ResetCommunication, 0);
        await QuiesceAsync(rig.Witness, null);
        rig.Node.FlyingMasterRole.Should().Be(FlyingMasterRole.Active);
        rig.Node.State.Should().Be(NmtState.Operational);
    }

    [Fact]
    public async Task A_Broadcast_Reset_After_Force_Is_Ignored_Once()
    {
        using var rig = OpenMaster();
        var od = rig.Node.ObjectDictionary;
        Tighten(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Node.FlyingMasterRole == FlyingMasterRole.Active, 800,
            "the master is active");

        Transmit(rig.Peer, CanOpenCobId.FlyingMasterForce);
        await QuiesceAsync(rig.Witness, null);
        rig.Node.FlyingMasterRole.Should().Be(FlyingMasterRole.Delaying,
            "the force reset the active master and started a warm election");
        od.ReadUnsigned(0x1F89, 0x00).Should().Be(0u);

        od.WriteUnsigned(0x1F89, 0x00, 321);
        TransmitNmt(rig.Peer, NmtCommand.ResetCommunication, 0);
        await QuiesceAsync(rig.Witness, null);
        od.ReadUnsigned(0x1F89, 0x00).Should().Be(321u,
            "the reset the force just sent is the echo, and it does not reset the node again");
        rig.Node.FlyingMasterRole.Should().Be(FlyingMasterRole.Delaying);

        TransmitNmt(rig.Peer, NmtCommand.ResetNode, 0);
        await QuiesceAsync(rig.Witness, null);
        od.ReadUnsigned(0x1F89, 0x00).Should().Be(0u,
            "the echo flag is one shot, so the next broadcast reset applies");
    }

    [Fact]
    public async Task An_Nmt_Request_Queued_Behind_A_Stop_Is_Not_Sent()
    {
        const byte slave = 0x22;
        using var rig = OpenMaster();
        var od = rig.Node.ObjectDictionary;
        od.WriteUnsigned(0x1F81, slave, Assigned);
        od.WriteUnsigned(Startup, 0x00, SuppressSelfStart | SuppressSlaveStart);
        Tighten(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Node.FlyingMasterRole == FlyingMasterRole.Active, 800,
            "the master is active");

        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = rig.Node.PostToActorAsync(() =>
        {
            entered.TrySetResult(true);
            release.Task.Wait(TimeSpan.FromSeconds(5));
        });
        (await entered.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();

        // The stop is queued while the loop is held, and the request is accepted on this thread
        // because the role is still Active. The request's send then runs after the stop.
        var stop = rig.Node.PostToActorAsync(() => rig.Node.StopFlyingMaster());
        rig.Log.Clear();
        od.WriteUnsigned(0x1F82, slave, (byte)NmtState.Operational);
        release.TrySetResult(true);
        await held.WaitAsync(TimeSpan.FromSeconds(5));
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        await QuiesceAsync(rig.Witness, null);

        rig.Node.FlyingMasterRole.Should().Be(FlyingMasterRole.Inactive);
        rig.Log.Snapshot().Should().NotContain(f => IsNmt(f, NmtCommand.Start, slave),
            "the stop landed before the request, so the request does not start the slave");
    }

    [Fact]
    public async Task A_Frame_During_The_Cold_Reset_Is_Dropped_And_Dispose_Swallows_The_Completion()
    {
        var session = NewSession();
        using var nodeBus = Open(session, 0);
        using var peer = Open(session, 1);
        var clock = new ManualTimeSource();
        var gate = new ColdResetGate(new CanBusService(nodeBus));
        using var node = new CanOpenNode(gate, LeftId, new CanOpenNodeOptions(), ownsService: true, timeSource: clock);
        var witness = new ActorWitness(node, peer, WitnessForLeft);

        Tighten(node);
        node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(clock, witness, null,
            () => node.FlyingMasterRole == FlyingMasterRole.Detecting, 200,
            "the node is asking who is master");

        await AdvanceAsync(clock, witness, null, TimeSpan.FromMilliseconds(30));
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(5));

        Transmit(peer, CanOpenCobId.FlyingMasterClaim, 0, 0x05);
        Transmit(peer, CanOpenCobId.FlyingMasterTrigger);
        await QuiesceAsync(witness, null);
        node.FlyingMasterRole.Should().Be(FlyingMasterRole.Detecting,
            "a claim in the cold-reset window must not move the node to standby");

        await Task.Delay(200);
    }

    [Fact]
    public async Task A_Broadcast_Reset_While_The_Cold_Send_Is_Held_Applies_Once()
    {
        var session = NewSession();
        using var nodeBus = Open(session, 0);
        using var peer = Open(session, 1);
        var clock = new ManualTimeSource();
        using var gate = new ColdResetGate(new CanBusService(nodeBus));
        using var node = new CanOpenNode(gate, LeftId, new CanOpenNodeOptions(), ownsService: true, timeSource: clock);
        var witness = new ActorWitness(node, peer, WitnessForLeft);
        var resets = 0;
        node.ApplicationReset += (_, _) => resets++;

        Tighten(node);
        node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(clock, witness, null,
            () => node.FlyingMasterRole == FlyingMasterRole.Detecting, 200,
            "the node is asking who is master");

        await AdvanceAsync(clock, witness, null, TimeSpan.FromMilliseconds(30));
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(5));

        Transmit(peer, CanOpenCobId.NmtCommand, (byte)NmtCommand.ResetCommunication, 0);
        await QuiesceAsync(witness, null);

        resets.Should().Be(1, "the broadcast reset applies while the cold send is still held");
        node.FlyingMasterRole.Should().Be(FlyingMasterRole.Delaying,
            "that reset is the warm boot, so the election delay starts from it");

        gate.Release();
        await QuiesceAsync(witness, null);
        resets.Should().Be(1,
            "the broadcast cleared the pending reset, so the confirmation does not apply it again");
        node.FlyingMasterRole.Should().Be(FlyingMasterRole.Delaying);
    }

    [Fact]
    public async Task A_Cancelled_Cold_Reset_Send_Does_Not_Start_The_Election()
    {
        var background = await ColdResetSendDoesNotElect(gate => gate.Cancel = true);
        background.Should().BeNull("cancellation is not a background transport failure");
    }

    [Fact]
    public Task An_Unconfirmed_Cold_Reset_Send_Does_Not_Start_The_Election()
        => ColdResetSendDoesNotElect(gate => gate.Reject = true);

    [Fact]
    public async Task A_Disposed_Service_On_The_Cold_Reset_Is_Reported_And_Does_Not_Elect()
    {
        var background = await ColdResetSendDoesNotElect(
            gate => gate.Fault = new ObjectDisposedException(nameof(CanBusService)));
        background.Should().BeOfType<ObjectDisposedException>();
    }

    [Fact]
    public async Task An_Adapter_Bus_Fault_On_The_Cold_Reset_Is_Reported_And_Does_Not_Elect()
    {
        var background = await ColdResetSendDoesNotElect(
            gate => gate.Fault = new CanBusException(CanKitErrorCode.NativeCallFailed, "driver rejected the frame"));
        background.Should().BeOfType<CanBusException>();
    }

    [Fact]
    public async Task Any_Non_Cancellation_Throw_On_The_Cold_Reset_Is_Reported()
    {
        var background = await ColdResetSendDoesNotElect(
            gate => gate.Fault = new InvalidOperationException("driver failed the reset"));
        background.Should().BeOfType<InvalidOperationException>();
    }

    private async Task<Exception?> ColdResetSendDoesNotElect(Action<ColdResetGate> arrange)
    {
        var session = NewSession();
        using var nodeBus = Open(session, 0);
        using var peer = Open(session, 1);
        var clock = new ManualTimeSource();
        using var gate = new ColdResetGate(new CanBusService(nodeBus));
        arrange(gate);
        using var node = new CanOpenNode(gate, LeftId, new CanOpenNodeOptions(), ownsService: true, timeSource: clock);
        Exception? background = null;
        node.BackgroundExceptionOccurred += (_, ex) => background ??= ex;
        var witness = new ActorWitness(node, peer, WitnessForLeft);
        using var log = new FrameLog(peer);

        Tighten(node);
        node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(clock, witness, null,
            () => node.FlyingMasterRole == FlyingMasterRole.Detecting, 200,
            "the node is asking who is master");

        await AdvanceAsync(clock, witness, null, TimeSpan.FromMilliseconds(30));
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        // The send task has already finished. Give its continuation time to reach the actor
        // before the clock runs far enough for a warm election to win.
        await Task.Delay(200);
        await QuiesceAsync(witness, null);
        await AdvanceAsync(clock, witness, null, TimeSpan.FromMilliseconds(800));

        node.FlyingMasterRole.Should().Be(FlyingMasterRole.Detecting,
            "a cold reset that was not confirmed must not start the warm election");
        log.Snapshot().Should().NotContain(f => f.Id == CanOpenCobId.FlyingMasterTrigger);
        return background;
    }

    [Fact]
    public async Task A_Boot_Timeout_At_Or_Above_2_31_Milliseconds_Still_Fires()
    {
        const byte slave = 0x22;
        const uint bootMs = 0x80000000;
        using var rig = OpenMaster();
        var timedOut = new System.Collections.Concurrent.ConcurrentQueue<byte>();
        rig.Node.FlyingMasterChanged += (_, e) =>
        {
            if (e.Signal == FlyingMasterSignal.SlaveBootTimeout && e.OtherNodeId is { } id)
                timedOut.Enqueue(id);
        };

        var od = rig.Node.ObjectDictionary;
        od.WriteUnsigned(0x1F81, slave, Assigned | MandatorySlave);
        od.WriteUnsigned(0x1F89, 0x00, bootMs);
        Tighten(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Node.FlyingMasterRole == FlyingMasterRole.Active, 800,
            "the master is active");

        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(200));
        timedOut.Should().BeEmpty("200 ms is inside an unsigned timeout of 0x80000000 ms");

        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(bootMs));
        timedOut.Should().Contain(slave, "1F89h is unsigned, so 0x80000000 still arms the deadline");
    }

    [Fact]
    public async Task Master_Heartbeats_Reach_The_Standby_Watch_Past_Node_Guarding()
    {
        using var pair = OpenPair();
        var (clock, winner, standby, winnerWitness, standbyWitness, _) = pair;
        var lost = new System.Collections.Concurrent.ConcurrentQueue<FlyingMasterSignal>();
        standby.FlyingMasterChanged += (_, e) => lost.Enqueue(e.Signal);

        var timeout = TimeSpan.FromMilliseconds(80);
        standby.StartNodeGuardingConsumer(LeftId, TimeSpan.FromSeconds(30), 2);
        await QuiesceAsync(winnerWitness, standbyWitness);

        Tighten(winner);
        Tighten(standby);
        winner.StartFlyingMaster(0, timeout);
        standby.StartFlyingMaster(2, timeout);
        await UntilAsync(clock, winnerWitness, standbyWitness,
            () => winner.FlyingMasterRole == FlyingMasterRole.Active
                && standby.FlyingMasterRole == FlyingMasterRole.Standby, 1200,
            "priority 0 wins and priority 2 watches it");

        for (int elapsed = 0; elapsed < 300; elapsed += 10)
            await AdvanceAsync(clock, winnerWitness, standbyWitness, TimeSpan.FromMilliseconds(10));

        standby.FlyingMasterRole.Should().Be(FlyingMasterRole.Standby,
            "the winner's heartbeats rearm the watch even though a node-guarding consumer owns that COB-ID");
        lost.Should().NotContain(FlyingMasterSignal.ActiveMasterLost);
    }

    [Fact]
    public async Task An_Assigned_Slave_Heartbeat_Is_Seen_Past_Node_Guarding()
    {
        const byte slave = 0x22;
        using var rig = OpenMaster();
        var timedOut = new System.Collections.Concurrent.ConcurrentQueue<byte>();
        rig.Node.FlyingMasterChanged += (_, e) =>
        {
            if (e.Signal == FlyingMasterSignal.SlaveBootTimeout && e.OtherNodeId is { } id)
                timedOut.Enqueue(id);
        };

        var od = rig.Node.ObjectDictionary;
        od.WriteUnsigned(0x1F81, slave, Assigned | MandatorySlave);
        od.WriteUnsigned(0x1F89, 0x00, 100);
        rig.Node.StartNodeGuardingConsumer(slave, TimeSpan.FromSeconds(30), 2);
        await QuiesceAsync(rig.Witness, null);

        Tighten(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Node.FlyingMasterRole == FlyingMasterRole.Active, 800,
            "the master is active");

        TransmitHeartbeat(rig.Peer, slave, (byte)NmtState.PreOperational);
        await QuiesceAsync(rig.Witness, null);
        rig.Node.State.Should().Be(NmtState.Operational,
            "the slave heartbeat marks it seen even though node guarding routed the COB-ID");

        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(150));
        timedOut.Should().BeEmpty();
    }

    [Fact]
    public async Task Stop_Records_Only_The_Startup_Bits()
    {
        const byte slave = 0x22;
        using var rig = OpenMaster();
        var od = rig.Node.ObjectDictionary;
        od.WriteUnsigned(0x1F81, slave, Assigned);
        od.WriteUnsigned(0x1F89, 0x00, 100);
        Tighten(rig.Node);
        uint delay = od.ReadUnsigned(Timing, 0x02);

        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Node.FlyingMasterRole == FlyingMasterRole.Active, 800,
            "the master is active");

        od.WriteUnsigned(0x1F81, slave, Assigned | BootSlave | MandatorySlave);
        od.WriteUnsigned(0x1F89, 0x00, 250);
        od.WriteUnsigned(Timing, 0x02, delay + 50);
        rig.Node.StopFlyingMaster();
        await QuiesceAsync(rig.Witness, null);

        TransmitNmt(rig.Peer, NmtCommand.ResetCommunication, LeftId);
        await QuiesceAsync(rig.Witness, null);

        (od.ReadUnsigned(Startup, 0x00) & MasterBits).Should().Be(0u,
            "stop records the cleared 1F80h, so the reset does not rejoin");
        od.ReadUnsigned(0x1F81, slave).Should().Be(Assigned,
            "stop does not snapshot the live network list");
        od.ReadUnsigned(0x1F89, 0x00).Should().Be(100u,
            "stop does not snapshot the live boot timeout");
        od.ReadUnsigned(Timing, 0x02).Should().Be(delay,
            "stop does not snapshot the live flying-master timing");
        rig.Node.FlyingMasterRole.Should().Be(FlyingMasterRole.Inactive);
    }

    private static void Transmit(ICanBus peer, uint id, params byte[] data)
        => peer.Transmit(CanFrame.Classic(unchecked((int)id), data, isExtendedFrame: false));

    private const string TimingEds = """
        [FileInfo]
        FileName=timing.eds
        FileVersion=1
        FileRevision=0
        EDSVersion=4.0
        Description=1F90 count
        CreationTime=10:00AM
        CreationDate=09-26-2026
        CreatedBy=CanKit.Pro tests

        [DeviceInfo]
        VendorName=CanKit.Pro
        VendorNumber=0
        ProductName=Timing
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
        SupportedObjects=1
        1=0x1F90

        [1F90]
        ParameterName=Flying master timing
        SubNumber=5
        ObjectType=0x8

        [1F90sub0]
        ParameterName=Highest sub-index supported
        ObjectType=0x7
        DataType=0x0005
        AccessType=ro
        DefaultValue=6
        PDOMapping=0

        [1F90sub4]
        ParameterName=Priority time slot
        ObjectType=0x7
        DataType=0x0006
        AccessType=rw
        DefaultValue=70000
        PDOMapping=0

        [1F90sub5]
        ParameterName=Device time slot
        ObjectType=0x7
        DataType=0x0006
        AccessType=rw
        DefaultValue=later
        PDOMapping=0
        """;

    private const string RestoreEds = """
        [FileInfo]
        FileName=restore.eds
        FileVersion=1
        FileRevision=0
        EDSVersion=4.0
        Description=Restore width
        CreationTime=10:00AM
        CreationDate=09-26-2026
        CreatedBy=CanKit.Pro tests

        [DeviceInfo]
        VendorName=CanKit.Pro
        VendorNumber=0
        ProductName=Restore
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
        SupportedObjects=2
        1=0x2000
        2=0x2100

        [2000]
        ParameterName=Application
        SubNumber=2
        ObjectType=0x8

        [2000sub0]
        ParameterName=Highest sub-index supported
        ObjectType=0x7
        DataType=0x0005
        AccessType=ro
        DefaultValue=1
        PDOMapping=0

        [2000sub1]
        ParameterName=Wide
        ObjectType=0x7
        DataType=0x0007
        AccessType=rw
        DefaultValue=0x11223344
        PDOMapping=0

        [2100]
        ParameterName=Name
        ObjectType=0x7
        DataType=0x0009
        AccessType=rw
        DefaultValue=hi
        PDOMapping=0
        """;

    [Fact]
    public async Task A_Force_Does_Not_Restart_The_Election_Before_The_Reset_Is_Confirmed()
    {
        var session = NewSession();
        using var nodeBus = Open(session, 0);
        using var peer = Open(session, 1);
        var clock = new ManualTimeSource();
        using var gate = new ColdResetGate(new CanBusService(nodeBus)) { PassResets = 1 };
        using var node = new CanOpenNode(gate, LeftId, new CanOpenNodeOptions(), ownsService: true, timeSource: clock);
        var witness = new ActorWitness(node, peer, WitnessForLeft);

        Tighten(node);
        node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(clock, witness, null,
            () => node.FlyingMasterRole == FlyingMasterRole.Active, 800,
            "the cold reset was confirmed and the node won");

        Transmit(peer, CanOpenCobId.FlyingMasterForce);
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        await AdvanceAsync(clock, witness, null, TimeSpan.FromMilliseconds(80));
        node.FlyingMasterRole.Should().Be(FlyingMasterRole.Active,
            "the warm election waits until Reset Communication is confirmed");

        gate.Release();
        await QuiesceAsync(witness, null);
        node.FlyingMasterRole.Should().Be(FlyingMasterRole.Delaying,
            "the confirmed reset applies locally and starts the warm election");
    }

    [Fact]
    public async Task A_Detect_Cycle_Does_Not_Run_While_The_Force_Reset_Is_Held()
    {
        var session = NewSession();
        using var nodeBus = Open(session, 0);
        using var peer = Open(session, 1);
        var clock = new ManualTimeSource();
        using var gate = new ColdResetGate(new CanBusService(nodeBus)) { PassResets = 1 };
        using var node = new CanOpenNode(gate, LeftId, new CanOpenNodeOptions(), ownsService: true, timeSource: clock);
        var witness = new ActorWitness(node, peer, WitnessForLeft);
        using var log = new FrameLog(peer);

        Tighten(node);
        node.ObjectDictionary.WriteUnsigned(Timing, 0x06, 30);
        node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(clock, witness, null,
            () => node.FlyingMasterRole == FlyingMasterRole.Active, 800,
            "the node is the active master");

        Transmit(peer, CanOpenCobId.FlyingMasterForce);
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        await QuiesceAsync(witness, null);
        int triggers = log.Snapshot().Count(f => f.Id == CanOpenCobId.FlyingMasterTrigger);
        int claims = log.Snapshot().Count(f => f.Id == CanOpenCobId.FlyingMasterClaim);

        await AdvanceAsync(clock, witness, null, TimeSpan.FromMilliseconds(80));
        node.FlyingMasterRole.Should().Be(FlyingMasterRole.Active,
            "the master stays active until the reset is confirmed, but it does not keep electing");
        log.Snapshot().Count(f => f.Id == CanOpenCobId.FlyingMasterTrigger).Should().Be(triggers,
            "the detect cycle is cancelled while Reset Communication is still held");
        log.Snapshot().Count(f => f.Id == CanOpenCobId.FlyingMasterClaim).Should().Be(claims);

        gate.Release();
        await QuiesceAsync(witness, null);
        node.FlyingMasterRole.Should().Be(FlyingMasterRole.Delaying);
    }

    [Fact]
    public async Task Stopping_During_The_Held_Cold_Reset_Drops_It()
    {
        var session = NewSession();
        using var nodeBus = Open(session, 0);
        using var peer = Open(session, 1);
        var clock = new ManualTimeSource();
        using var gate = new ColdResetGate(new CanBusService(nodeBus));
        using var node = new CanOpenNode(gate, LeftId, new CanOpenNodeOptions(), ownsService: true, timeSource: clock);
        var witness = new ActorWitness(node, peer, WitnessForLeft);
        var resets = 0;
        node.ApplicationReset += (_, _) => resets++;

        Tighten(node);
        node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(clock, witness, null,
            () => node.FlyingMasterRole == FlyingMasterRole.Detecting, 200,
            "the node is asking who is master");
        await AdvanceAsync(clock, witness, null, TimeSpan.FromMilliseconds(30));
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(5));

        node.StopFlyingMaster();
        await QuiesceAsync(witness, null);
        gate.Release();
        await QuiesceAsync(witness, null);

        node.FlyingMasterRole.Should().Be(FlyingMasterRole.Inactive,
            "stop clears the pending reset, so the later confirmation does not start the election");
        resets.Should().Be(0);
    }

    [Fact]
    public async Task Stopping_During_The_Held_Force_Reset_Drops_It()
    {
        var session = NewSession();
        using var nodeBus = Open(session, 0);
        using var peer = Open(session, 1);
        var clock = new ManualTimeSource();
        using var gate = new ColdResetGate(new CanBusService(nodeBus)) { PassResets = 1 };
        using var node = new CanOpenNode(gate, LeftId, new CanOpenNodeOptions(), ownsService: true, timeSource: clock);
        var witness = new ActorWitness(node, peer, WitnessForLeft);
        var resets = 0;
        node.ApplicationReset += (_, _) => resets++;

        Tighten(node);
        node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(clock, witness, null,
            () => node.FlyingMasterRole == FlyingMasterRole.Active, 800,
            "the node is the active master");

        Transmit(peer, CanOpenCobId.FlyingMasterForce);
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        int applied = resets;

        node.StopFlyingMaster();
        await QuiesceAsync(witness, null);
        gate.Release();
        await QuiesceAsync(witness, null);

        node.FlyingMasterRole.Should().Be(FlyingMasterRole.Inactive,
            "stop clears the pending reset, so the later confirmation does not apply it");
        resets.Should().Be(applied);
    }

    [Fact]
    public async Task A_Force_Reset_Completion_After_Dispose_Is_Swallowed()
    {
        var session = NewSession();
        using var nodeBus = Open(session, 0);
        using var peer = Open(session, 1);
        var clock = new ManualTimeSource();
        using var gate = new ColdResetGate(new CanBusService(nodeBus)) { PassResets = 1 };
        using var node = new CanOpenNode(gate, LeftId, new CanOpenNodeOptions(), ownsService: true, timeSource: clock);
        var witness = new ActorWitness(node, peer, WitnessForLeft);

        Tighten(node);
        node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(clock, witness, null,
            () => node.FlyingMasterRole == FlyingMasterRole.Active, 800,
            "the node is the active master");

        Transmit(peer, CanOpenCobId.FlyingMasterForce);
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(5));

        // Dispose drops the actor and then releases the held send. The completion posts back
        // onto a node that is already gone.
        node.Dispose();
        await Task.Delay(200);
    }

    [Fact]
    public async Task An_Unconfirmed_Force_Reset_Does_Not_Restart_The_Election()
    {
        var session = NewSession();
        using var nodeBus = Open(session, 0);
        using var peer = Open(session, 1);
        var clock = new ManualTimeSource();
        using var gate = new ColdResetGate(new CanBusService(nodeBus)) { PassResets = 1, Reject = true };
        using var node = new CanOpenNode(gate, LeftId, new CanOpenNodeOptions(), ownsService: true, timeSource: clock);
        var witness = new ActorWitness(node, peer, WitnessForLeft);

        Tighten(node);
        node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(clock, witness, null,
            () => node.FlyingMasterRole == FlyingMasterRole.Active, 800,
            "the node is the active master");

        Transmit(peer, CanOpenCobId.FlyingMasterForce);
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(200);
        await QuiesceAsync(witness, null);
        await AdvanceAsync(clock, witness, null, TimeSpan.FromMilliseconds(80));
        node.FlyingMasterRole.Should().Be(FlyingMasterRole.Active,
            "a forced reset that was not confirmed does not reset this node or start the delay");
    }

    [Fact]
    public async Task A_Heartbeat_During_A_Held_Force_Does_Not_Start_The_Slave_Again()
    {
        const byte slave = 0x22;
        var session = NewSession();
        using var nodeBus = Open(session, 0);
        using var peer = Open(session, 1);
        var clock = new ManualTimeSource();
        using var gate = new ColdResetGate(new CanBusService(nodeBus)) { PassResets = 1 };
        using var node = new CanOpenNode(gate, LeftId, new CanOpenNodeOptions(), ownsService: true, timeSource: clock);
        var witness = new ActorWitness(node, peer, WitnessForLeft);
        using var log = new FrameLog(peer);

        node.ObjectDictionary.WriteUnsigned(Startup, 0x00, SuppressSelfStart);
        node.ObjectDictionary.WriteUnsigned(0x1F81, slave, Assigned | BootSlave);
        Tighten(node);
        node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(clock, witness, null,
            () => node.FlyingMasterRole == FlyingMasterRole.Active, 800,
            "the node is the active master");

        TransmitHeartbeat(peer, slave, 0x7F);
        await QuiesceAsync(witness, null);
        int starts = log.Snapshot().Count(f => IsNmt(f, NmtCommand.Start, slave));
        starts.Should().BeGreaterThan(0);

        Transmit(peer, CanOpenCobId.FlyingMasterForce);
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        TransmitHeartbeat(peer, slave, 0x7F);
        await QuiesceAsync(witness, null);

        log.Snapshot().Count(f => IsNmt(f, NmtCommand.Start, slave)).Should().Be(starts,
            "boot stays as it was, so a heartbeat during the held reset does not start the slave again");
        node.FlyingMasterRole.Should().Be(FlyingMasterRole.Active);
    }

    [Fact]
    public async Task An_Unconfirmed_Force_Does_Not_Reset_Assigned_Slaves_Again()
    {
        const byte slave = 0x22;
        var session = NewSession();
        using var nodeBus = Open(session, 0);
        using var peer = Open(session, 1);
        var clock = new ManualTimeSource();
        using var gate = new ColdResetGate(new CanBusService(nodeBus)) { PassResets = 1, Reject = true };
        using var node = new CanOpenNode(gate, LeftId, new CanOpenNodeOptions(), ownsService: true, timeSource: clock);
        var witness = new ActorWitness(node, peer, WitnessForLeft);
        using var log = new FrameLog(peer);

        node.ObjectDictionary.WriteUnsigned(Startup, 0x00, SuppressSelfStart);
        node.ObjectDictionary.WriteUnsigned(0x1F81, slave, Assigned | BootSlave);
        Tighten(node);
        node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(clock, witness, null,
            () => node.FlyingMasterRole == FlyingMasterRole.Active, 800,
            "the node is the active master");
        await QuiesceAsync(witness, null);
        int resets = log.Snapshot().Count(f => IsNmt(f, NmtCommand.ResetCommunication, slave));
        resets.Should().BeGreaterThan(0);

        Transmit(peer, CanOpenCobId.FlyingMasterForce);
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(200);
        await QuiesceAsync(witness, null);

        log.Snapshot().Count(f => IsNmt(f, NmtCommand.ResetCommunication, slave)).Should().Be(resets,
            "an unconfirmed force leaves the slaves where they were");
        node.FlyingMasterRole.Should().Be(FlyingMasterRole.Active);
    }

    [Fact]
    public async Task An_Application_Takeover_Of_The_Installed_Watch_Survives_Stop()
    {
        using var pair = OpenPair();
        var (clock, winner, standby, winnerWitness, standbyWitness, _) = pair;
        var timeout = TimeSpan.FromMilliseconds(80);
        Tighten(winner);
        Tighten(standby);
        winner.StartFlyingMaster(0, timeout);
        standby.StartFlyingMaster(2, timeout);
        await UntilAsync(clock, winnerWitness, standbyWitness,
            () => standby.FlyingMasterRole == FlyingMasterRole.Standby, 1200,
            "priority 2 is watching the winner");

        standby.AddHeartbeatConsumer(LeftId, TimeSpan.FromMilliseconds(1500));
        await QuiesceAsync(winnerWitness, standbyWitness);
        uint taken = standby.ObjectDictionary.ReadUnsigned(0x1016, 0x01);
        ((taken >> 16) & 0xFF).Should().Be(LeftId);
        (taken & 0xFFFF).Should().Be(1500u);

        standby.StopFlyingMaster();
        await QuiesceAsync(winnerWitness, standbyWitness);
        standby.ObjectDictionary.ReadUnsigned(0x1016, 0x01).Should().Be(taken,
            "the application replaced the installed watch, so stop does not delete it");
    }

    [Fact]
    public async Task A_Takeover_And_Its_Ownership_Clear_Are_One_Actor_Job()
    {
        using var pair = OpenPair();
        var (clock, winner, standby, winnerWitness, standbyWitness, _) = pair;
        var timeout = TimeSpan.FromMilliseconds(80);
        Tighten(winner);
        Tighten(standby);
        winner.StartFlyingMaster(0, timeout);
        standby.StartFlyingMaster(2, timeout);
        await UntilAsync(clock, winnerWitness, standbyWitness,
            () => standby.FlyingMasterRole == FlyingMasterRole.Standby, 1200,
            "priority 2 is watching the winner");

        uint installed = standby.ObjectDictionary.ReadUnsigned(0x1016, 0x01);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = standby.PostToActorAsync(() =>
        {
            entered.TrySetResult(true);
            release.Task.Wait(TimeSpan.FromSeconds(5));
        });
        (await entered.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();

        Exception? addError = null;
        var addThread = new Thread(() =>
        {
            try { standby.AddHeartbeatConsumer(LeftId, TimeSpan.FromMilliseconds(1500)); }
            catch (Exception ex) { addError = ex; }
        });
        addThread.Start();
        var blockedSince = DateTime.UtcNow;
        bool settled = SpinWait.SpinUntil(() =>
        {
            if (!addThread.IsAlive || standby.ObjectDictionary.ReadUnsigned(0x1016, 0x01) != installed)
                return true;
            bool waiting = (addThread.ThreadState & ThreadState.WaitSleepJoin) != 0;
            if (!waiting) blockedSince = DateTime.UtcNow;
            return waiting && DateTime.UtcNow - blockedSince > TimeSpan.FromMilliseconds(30);
        }, TimeSpan.FromSeconds(2));
        settled.Should().BeTrue();
        addThread.IsAlive.Should().BeTrue("the dictionary write waits with the ownership clear");
        standby.ObjectDictionary.ReadUnsigned(0x1016, 0x01).Should().Be(installed);

        var stop = standby.PostToActorAsync(() => standby.StopFlyingMaster());
        release.TrySetResult(true);
        addThread.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
        addError.Should().BeNull();
        await held.WaitAsync(TimeSpan.FromSeconds(5));
        await stop.WaitAsync(TimeSpan.FromSeconds(5));

        uint taken = standby.ObjectDictionary.ReadUnsigned(0x1016, 0x01);
        ((taken >> 16) & 0xFF).Should().Be(LeftId);
        (taken & 0xFFFF).Should().Be(1500u,
            "stop was queued behind the takeover, so it cannot delete the consumer just claimed");
    }

    /// <summary>Holds the cold Reset Communication so a test can use the window before it completes,
    /// or cancels that one send.</summary>
    private sealed class ColdResetGate : ICanBusService
    {
        private readonly CanBusService _inner;
        private readonly TaskCompletionSource<bool> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ColdResetGate(CanBusService inner) => _inner = inner;

        public bool Cancel { get; set; }
        public bool Reject { get; set; }
        public Exception? Fault { get; set; }
        /// <summary>How many broadcast resets pass straight through before one is held or failed.</summary>
        public int PassResets { get; set; }
        public Task Entered => _entered.Task;
        public void Release() => _release.TrySetResult(true);
        private int _resetsSeen;

        public ICanBus Bus => _inner.Bus;
        public int SubscriptionCount => _inner.SubscriptionCount;
        public event EventHandler<Exception>? BackgroundExceptionOccurred
        {
            add => _inner.BackgroundExceptionOccurred += value;
            remove => _inner.BackgroundExceptionOccurred -= value;
        }

        public ISubscription Subscribe(Func<CanFrameEvent, bool>? predicate = null, int? bufferCapacity = null, bool includeEcho = false)
            => _inner.Subscribe(predicate, bufferCapacity, includeEcho);

        public ISubscription Subscribe(CanIdFilter filter, int? bufferCapacity = null, bool includeEcho = false)
            => _inner.Subscribe(filter, bufferCapacity, includeEcho);

        public IReadOnlyList<FilterOverlap> FindOverlappingFilterSubscriptions()
            => _inner.FindOverlappingFilterSubscriptions();

        public async Task<TxConfirmation> SendConfirmed(CanFrame frame, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            if (IsBroadcastReset(frame) && ++_resetsSeen > PassResets)
            {
                _entered.TrySetResult(true);
                if (Cancel) throw new OperationCanceledException();
                if (Fault is not null) throw Fault;
                if (Reject)
                    return new TxConfirmation { Confirmed = false, FailureReason = TxConfirmFailureReason.Rejected };
                await _release.Task.ConfigureAwait(false);
            }
            return await _inner.SendConfirmed(frame, timeout, cancellationToken).ConfigureAwait(false);
        }

        public void Dispose()
        {
            _release.TrySetResult(true);
            _inner.Dispose();
        }

        private static bool IsBroadcastReset(CanFrame frame)
        {
            var data = frame.Data.ToArray();
            return frame.ID == 0 && !frame.IsExtendedFrame && data.Length >= 2
                && data[0] == (byte)NmtCommand.ResetCommunication && data[1] == 0;
        }
    }
}
