using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
/// Producer-side life guarding (FR-CO-021: CiA 301 §7.2.8.2.2 / §7.2.8.3.2.1, objects
/// <c>100Ch</c> and <c>100Dh</c>) and what the NMT state does to the communication objects
/// (FR-CO-022: CiA 301 §7.3.2.2.4 and Table 37, plus the guarding toggle of §7.2.8.3.2.1), on a
/// Virtual bus with the test acting as the NMT master from a bus of its own.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here is gated on wall-clock time. The node life time is measured against a
/// <see cref="ManualTimeSource"/> the test moves by hand through the node's internal constructor
/// (#113), and every claim that something did <em>not</em> happen rests on an ordering witness:
/// an event or frame that the node can only produce after the point in question, so that its
/// arrival proves the earlier silence rather than a sleep suggesting it.
/// </para>
/// <para>
/// <see cref="ActorWitness"/> is that witness for the clocked tests, and its two-round-trip
/// rule is borrowed from <see cref="VirtualClock"/>: the actor loop runs
/// <c>wait → DrainMailbox → DrainPendingTimerInserts → FireDueTimers</c> and
/// <c>DrainMailbox</c> bounds itself by the mailbox count at its start, so a frame sent after
/// the previous frame's event was delivered is handled in a <em>later</em> iteration -- after
/// the timers that became due have fired. Its event then sits behind theirs in the node's
/// single-reader event pump, which delivers in enqueue order.
/// </para>
/// </remarks>
public class CanOpenLifeGuardingAndNmtStateTests : IClassFixture<VirtualAdapterFixture>
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The node under test throughout; the test's own bus plays the NMT master.</summary>
    private const byte NodeId = 0x11;

    /// <summary>A peer that exists only as a heartbeat the witness sends; nothing consumes it.</summary>
    private const byte WitnessPeer = 0x7E;

    private const ushort GuardTimeIndex = 0x100C;
    private const ushort LifeTimeFactorIndex = 0x100D;

    private static string NewSession() => $"canopen-{Guid.NewGuid():N}";

    private static ICanBus Open(string session, int channel) => CanBus.Open(
        $"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(VirtualAdapterFixture.Bitrate));

    // =========================================================================================
    // FR-CO-021 -- producer-side life guarding.
    //
    // CiA 301 §7.2.8.2.2: "If life guarding (the NMT slave guarding the NMT master) is supported,
    // the NMT slave uses the guard time and life time factor from its object dictionary to
    // determine its node lifetime. If the NMT slave is not guarded within its lifetime, the NMT
    // slave informs its local application about that event. If guard time and life time factor
    // are 0 (default values), the NMT slave does not guard the NMT master. Guarding starts for
    // the NMT slave when the first RTR for its guarding CAN-ID is received."
    // =========================================================================================

    // FR-CO-021: §7.5.2.11 / §7.5.2.12 give both objects a default of 0, and §7.2.8.2.2 says
    // that with both at 0 the slave does not guard the master. However far the clock moves after
    // a poll, no event. The OD assertion pins the defaults themselves.
    [Fact]
    public async Task LifeGuarding_Is_Disabled_By_Default_However_Long_The_Clock_Runs()
    {
        var session = NewSession();
        using var nodeBus = Open(session, 1);
        using var masterBus = Open(session, 2);
        var clock = new ManualTimeSource();
        using var node = OpenClockedNode(nodeBus, NodeId, clock);
        var witness = new ActorWitness(node, masterBus, clock);
        var events = new AsyncQueue<LifeGuardingEventArgs>();
        node.LifeGuardingEvent += (_, e) => events.Add(e);

        node.ObjectDictionary.ReadUnsigned(GuardTimeIndex, 0x00).Should().Be(0u, "100Ch defaults to 0000h (§7.5.2.11)");
        node.ObjectDictionary.ReadUnsigned(LifeTimeFactorIndex, 0x00).Should().Be(0u, "100Dh defaults to 00h (§7.5.2.12)");

        SendRtr(masterBus, NodeId);
        await witness.SettleAsync();
        await witness.AdvanceAsync(TimeSpan.FromHours(1));
        await witness.AdvanceAsync(TimeSpan.FromDays(1));

        events.Count.Should().Be(0, "with 100Ch and 100Dh at 0 the node does not guard the master, whatever the clock does");
    }

    // FR-CO-021: the node life time is 100Ch x 100Dh (§7.2.8.3.2.1: "The node lifetime is given
    // by the guard time multiplied by the lifetime factor"), it starts with the first RTR
    // (§7.2.8.2.2), a lapse is indicated exactly once, and "the event resolved is indicated when
    // after the event occurred a subsequent remote request is received" (§7.2.8.2.2.2). The
    // clock is stopped one millisecond short of the life time first, so the boundary is a
    // measurement and not a wide tolerance: 50 ms x 3 = 150 ms, due at 150 and not at 149.
    [Fact]
    public async Task LifeGuarding_Starts_With_The_First_Rtr_And_Indicates_A_Lapse_Once_Until_The_Next_Poll()
    {
        var session = NewSession();
        using var nodeBus = Open(session, 1);
        using var masterBus = Open(session, 2);
        var clock = new ManualTimeSource();
        using var node = OpenClockedNode(nodeBus, NodeId, clock);
        var witness = new ActorWitness(node, masterBus, clock);
        var events = new AsyncQueue<LifeGuardingEventArgs>();
        node.LifeGuardingEvent += (_, e) => events.Add(e);

        node.ObjectDictionary.WriteUnsigned(GuardTimeIndex, 0x00, 50);
        node.ObjectDictionary.WriteUnsigned(LifeTimeFactorIndex, 0x00, 3);
        await witness.SettleAsync();

        // Configured but not yet polled: "Guarding starts for the NMT slave when the first RTR
        // for its guarding CAN-ID is received" (§7.2.8.2.2).
        await witness.AdvanceAsync(TimeSpan.FromHours(1));
        events.Count.Should().Be(0, "guarding has not started before the first RTR");

        SendRtr(masterBus, NodeId);
        await witness.SettleAsync();

        await witness.AdvanceAsync(TimeSpan.FromMilliseconds(149));
        events.Count.Should().Be(0, "the node life time of 50 ms x 3 has not elapsed at 149 ms");

        await witness.AdvanceAsync(TimeSpan.FromMilliseconds(1));
        var occurred = await events.NextAsync(ShortTimeout, "life guarding event");
        occurred.State.Should().Be(LifeGuardingState.Occurred);
        occurred.GuardTime.Should().Be(TimeSpan.FromMilliseconds(50), "the event reports the guard time in effect (100Ch)");
        occurred.LifeTimeFactor.Should().Be(3, "the event reports the life time factor in effect (100Dh)");

        await witness.AdvanceAsync(TimeSpan.FromMilliseconds(300));
        events.Count.Should().Be(0, "a lapse is indicated once; the clock does not repeat it");

        SendRtr(masterBus, NodeId);
        var resolved = await events.NextAsync(ShortTimeout, "life guarding event");
        resolved.State.Should().Be(LifeGuardingState.Resolved, "a subsequent remote request resolves the event (§7.2.8.2.2.2)");
        resolved.GuardTime.Should().Be(TimeSpan.FromMilliseconds(50));
        resolved.LifeTimeFactor.Should().Be(3);

        // The cycle repeats: the resolving poll re-armed the life time.
        await witness.SettleAsync();
        await witness.AdvanceAsync(TimeSpan.FromMilliseconds(150));
        (await events.NextAsync(ShortTimeout, "life guarding event")).State.Should().Be(LifeGuardingState.Occurred);
        SendRtr(masterBus, NodeId);
        (await events.NextAsync(ShortTimeout, "life guarding event")).State.Should().Be(LifeGuardingState.Resolved);
    }

    // FR-CO-021: the same configuration written by a master over SDO takes effect the same way
    // as the local write above -- the object dictionary is the single source of truth in both
    // directions. The master only configures; the poll comes from the test's bus so the clocked
    // life time is not raced by a real-time consumer.
    [Fact]
    public async Task LifeGuarding_Configured_Over_Sdo_By_A_Master_Takes_Effect()
    {
        var session = NewSession();
        using var masterNodeBus = Open(session, 0);
        using var nodeBus = Open(session, 1);
        using var masterBus = Open(session, 2);
        var clock = new ManualTimeSource();
        using var node = OpenClockedNode(nodeBus, NodeId, clock);
        using var master = CanOpen.OpenNode(masterNodeBus, nodeId: 0x01);
        var witness = new ActorWitness(node, masterBus, clock);
        var events = new AsyncQueue<LifeGuardingEventArgs>();
        node.LifeGuardingEvent += (_, e) => events.Add(e);

        // 100Ch is UNSIGNED16, 100Dh UNSIGNED8 (§7.5.2.11 / §7.5.2.12); both go expedited.
        await master.SdoDownloadAsync(NodeId, GuardTimeIndex, 0x00, new byte[] { 50, 0x00 }).WithTimeoutAsync(ShortTimeout);
        await master.SdoDownloadAsync(NodeId, LifeTimeFactorIndex, 0x00, new byte[] { 3 }).WithTimeoutAsync(ShortTimeout);
        node.ObjectDictionary.ReadUnsigned(GuardTimeIndex, 0x00).Should().Be(50u);
        node.ObjectDictionary.ReadUnsigned(LifeTimeFactorIndex, 0x00).Should().Be(3u);
        await witness.SettleAsync();

        SendRtr(masterBus, NodeId);
        await witness.SettleAsync();
        await witness.AdvanceAsync(TimeSpan.FromMilliseconds(150));

        var occurred = await events.NextAsync(ShortTimeout, "life guarding event");
        occurred.State.Should().Be(LifeGuardingState.Occurred);
        occurred.GuardTime.Should().Be(TimeSpan.FromMilliseconds(50));
        occurred.LifeTimeFactor.Should().Be(3);
    }

    // FR-CO-021: the life time is measured from the last poll, not the first. Three polls
    // 100 ms apart span 300 ms without a lapse (each re-arms 150 ms), and the lapse then comes
    // 150 ms after the third -- the positive half proves the third poll armed it.
    [Fact]
    public async Task LifeGuarding_Every_Rtr_Before_Expiry_Rearms_The_Life_Time()
    {
        var session = NewSession();
        using var nodeBus = Open(session, 1);
        using var masterBus = Open(session, 2);
        var clock = new ManualTimeSource();
        using var node = OpenClockedNode(nodeBus, NodeId, clock);
        var witness = new ActorWitness(node, masterBus, clock);
        var events = new AsyncQueue<LifeGuardingEventArgs>();
        node.LifeGuardingEvent += (_, e) => events.Add(e);

        node.ObjectDictionary.WriteUnsigned(GuardTimeIndex, 0x00, 50);
        node.ObjectDictionary.WriteUnsigned(LifeTimeFactorIndex, 0x00, 3);
        await witness.SettleAsync();

        SendRtr(masterBus, NodeId);
        await witness.SettleAsync();
        await witness.AdvanceAsync(TimeSpan.FromMilliseconds(100));
        SendRtr(masterBus, NodeId);
        await witness.SettleAsync();
        await witness.AdvanceAsync(TimeSpan.FromMilliseconds(100));
        SendRtr(masterBus, NodeId);
        await witness.SettleAsync();
        await witness.AdvanceAsync(TimeSpan.FromMilliseconds(100));

        events.Count.Should().Be(0, "300 ms passed since the first poll but never 150 ms since the last one");

        await witness.AdvanceAsync(TimeSpan.FromMilliseconds(50));
        (await events.NextAsync(ShortTimeout, "life guarding event")).State.Should().Be(LifeGuardingState.Occurred,
            "150 ms after the last poll the life time armed by that poll elapses");
    }

    // FR-CO-021: §7.2.8.3.2.2 -- "It is not allowed to use both error control mechanisms
    // guarding protocol and heartbeat protocol on one NMT slave at the same time. If the
    // heartbeat producer time is unequal 0 the heartbeat protocol is used." So with 1017h ≠ 0 an
    // RTR is neither answered nor does it start the life time. The witness for "not answered"
    // is a frame the node sends on the very same COB-ID afterwards: the heartbeat that
    // announces NMT Start while the producer is active, which reports Operational (05h) where a
    // guarding reply would have reported Pre-operational (7Fh). Once the producer stops, the
    // next RTR is answered -- with toggle 0, because the ignored one did not flip it -- and
    // guarding starts.
    [Fact]
    public async Task LifeGuarding_And_Guarding_Replies_Are_Off_While_The_Heartbeat_Producer_Runs()
    {
        var session = NewSession();
        using var nodeBus = Open(session, 1);
        using var masterBus = Open(session, 2);
        using var heartbeatTap = new FrameTap(masterBus, CanOpenCobId.Heartbeat(NodeId));
        var clock = new ManualTimeSource();
        using var node = OpenClockedNode(nodeBus, NodeId, clock);
        var witness = new ActorWitness(node, masterBus, clock);
        var events = new AsyncQueue<LifeGuardingEventArgs>();
        node.LifeGuardingEvent += (_, e) => events.Add(e);

        (await heartbeatTap.NextAsync())[0].Should().Be(0x00, "the node announces itself with a boot-up first");

        node.ObjectDictionary.WriteUnsigned(GuardTimeIndex, 0x00, 50);
        node.ObjectDictionary.WriteUnsigned(LifeTimeFactorIndex, 0x00, 3);
        // On the frozen clock the periodic heartbeat is never due; only the protocol's being in
        // use matters here.
        node.StartHeartbeatProducer(TimeSpan.FromSeconds(1));
        await witness.SettleAsync();

        SendRtr(masterBus, NodeId);
        SendNmt(masterBus, NmtCommand.Start, NodeId);
        (await heartbeatTap.NextAsync())[0].Should().Be((byte)NmtState.Operational,
            "the next frame on 0x700 + id is the state-change heartbeat, not a reply to the RTR sent before the Start");

        await witness.AdvanceAsync(TimeSpan.FromMilliseconds(150));
        events.Count.Should().Be(0, "an RTR ignored under the heartbeat protocol does not start the life time");

        node.StopHeartbeatProducer();
        await witness.SettleAsync();
        SendRtr(masterBus, NodeId);
        (await heartbeatTap.NextAsync())[0].Should().Be((byte)NmtState.Operational,
            "with 1017h back at 0 the RTR is answered, and with toggle 0: the ignored RTR did not consume a toggle");

        await witness.AdvanceAsync(TimeSpan.FromMilliseconds(150));
        (await events.NextAsync(ShortTimeout, "life guarding event")).State.Should().Be(LifeGuardingState.Occurred,
            "the answered RTR started guarding");
    }

    // FR-CO-021: "The value of 0000h shall disable the life guarding" (§7.5.2.11). A guarded node
    // whose 100Ch goes to 0 stops being guarded mid-life-time; re-enabling does not resume it,
    // because guarding starts with a poll (§7.2.8.2.2), and the next poll then does.
    [Fact]
    public async Task LifeGuarding_Stops_When_The_Guard_Time_Is_Cleared_And_Waits_For_A_Poll_When_Set_Again()
    {
        var session = NewSession();
        using var nodeBus = Open(session, 1);
        using var masterBus = Open(session, 2);
        var clock = new ManualTimeSource();
        using var node = OpenClockedNode(nodeBus, NodeId, clock);
        var witness = new ActorWitness(node, masterBus, clock);
        var events = new AsyncQueue<LifeGuardingEventArgs>();
        node.LifeGuardingEvent += (_, e) => events.Add(e);

        node.ObjectDictionary.WriteUnsigned(GuardTimeIndex, 0x00, 50);
        node.ObjectDictionary.WriteUnsigned(LifeTimeFactorIndex, 0x00, 3);
        await witness.SettleAsync();
        SendRtr(masterBus, NodeId);
        await witness.SettleAsync();
        await witness.AdvanceAsync(TimeSpan.FromMilliseconds(100));

        node.ObjectDictionary.WriteUnsigned(GuardTimeIndex, 0x00, 0);
        await witness.SettleAsync();
        await witness.AdvanceAsync(TimeSpan.FromHours(1));
        events.Count.Should().Be(0, "100Ch = 0 disabled life guarding while its life time was running");

        node.ObjectDictionary.WriteUnsigned(GuardTimeIndex, 0x00, 50);
        await witness.SettleAsync();
        await witness.AdvanceAsync(TimeSpan.FromHours(1));
        events.Count.Should().Be(0, "re-enabling does not start guarding; the next RTR does");

        SendRtr(masterBus, NodeId);
        await witness.SettleAsync();
        await witness.AdvanceAsync(TimeSpan.FromMilliseconds(150));
        (await events.NextAsync(ShortTimeout, "life guarding event")).State.Should().Be(LifeGuardingState.Occurred);
    }

    // FR-CO-021: a change of 100Ch while the life time is running gives the running life time
    // the new length, measured from the change. 50 x 3 armed at 0 would lapse at 150; at 100 the
    // guard time becomes 100, so the life time is 300 from there and lapses at 400.
    [Fact]
    public async Task LifeGuarding_A_Running_Life_Time_Takes_A_New_Guard_Time()
    {
        var session = NewSession();
        using var nodeBus = Open(session, 1);
        using var masterBus = Open(session, 2);
        var clock = new ManualTimeSource();
        using var node = OpenClockedNode(nodeBus, NodeId, clock);
        var witness = new ActorWitness(node, masterBus, clock);
        var events = new AsyncQueue<LifeGuardingEventArgs>();
        node.LifeGuardingEvent += (_, e) => events.Add(e);

        node.ObjectDictionary.WriteUnsigned(GuardTimeIndex, 0x00, 50);
        node.ObjectDictionary.WriteUnsigned(LifeTimeFactorIndex, 0x00, 3);
        await witness.SettleAsync();
        SendRtr(masterBus, NodeId);
        await witness.SettleAsync();
        await witness.AdvanceAsync(TimeSpan.FromMilliseconds(100));

        node.ObjectDictionary.WriteUnsigned(GuardTimeIndex, 0x00, 100);
        await witness.SettleAsync();

        await witness.AdvanceAsync(TimeSpan.FromMilliseconds(250));
        events.Count.Should().Be(0, "the old 150 ms life time was replaced by 300 ms from the change at 100 ms");

        await witness.AdvanceAsync(TimeSpan.FromMilliseconds(50));
        var occurred = await events.NextAsync(ShortTimeout, "life guarding event");
        occurred.State.Should().Be(LifeGuardingState.Occurred);
        occurred.GuardTime.Should().Be(TimeSpan.FromMilliseconds(100), "the event reports the guard time now in effect");
    }

    // =========================================================================================
    // FR-CO-022 -- NMT state versus communication objects (CiA 301 §7.3.2.2.4, Table 37) and
    // the guarding toggle across a reset (§7.2.8.3.2.1).
    //
    // Table 37: SDO, SYNC and EMCY are active in Pre-operational and Operational only; node
    // control and error control in all three states; PDO in Operational only. §7.3.2.2.4: in
    // Stopped a device "is forced to stop the communication altogether (except node guarding
    // and heartbeat, if active)", and "If there are EMCY messages triggered in this NMT state
    // they are pending. The most recent active EMCY reason may be transmitted after the CANopen
    // device transits into another NMT state."
    // =========================================================================================

    // FR-CO-022 (a): an SDO transfer open on the server cannot continue once the node is
    // stopped (Table 37: no SDO in Stopped), so the server tells the client why -- abort code
    // 0800 0022h, "data cannot be transferred or stored to the application because of the
    // present device state" (Table 22) -- and the client's task faults with that code.
    //
    // The transfer is held mid-flight deterministically: the client node lives on a
    // ControllableBus and the test is its wire. Every frame the client transmits is handed to
    // the test, which forwards it onto the server's Virtual bus or does not; every frame the
    // server transmits is injected into the client's bus by the test, when it chooses. The
    // client's first segment is simply never forwarded, so the server's session stays open,
    // waiting for it, until the Stop arrives.
    [Fact]
    public async Task Stopping_The_Node_Aborts_An_Open_Segmented_Download_With_0800_0022h()
    {
        var session = NewSession();
        using var serverBus = Open(session, 1);
        using var masterBus = Open(session, 2);
        // A session of its own: the double only borrows a real bus for its configuration and
        // never transmits on the hub, so the client is reachable through the test alone.
        using var clientBus = ControllableBus.EchoCapable(NewSession());

        using var server = CanOpen.OpenNode(serverBus, NodeId);
        using var client = CanOpen.OpenNode(clientBus, nodeId: 0x01,
            new CanOpenNodeOptions().With(sdoTimeout: TimeSpan.FromSeconds(30)));
        server.ObjectDictionary.AddDomain(0x2000, 0x00, new byte[16]);

        var clientRequests = new AsyncQueue<byte[]>();
        clientBus.OnTransmitting = f =>
        {
            if (!f.IsExtendedFrame && (uint)f.ID == CanOpenCobId.SdoRx(NodeId)) clientRequests.Add(f.Data.ToArray());
        };
        using var serverResponses = new FrameTap(masterBus, CanOpenCobId.SdoTx(NodeId));
        var nmt = new AsyncQueue<NmtCommand>();
        server.NmtCommandReceived += (_, e) => nmt.Add(e.Command);

        var payload = Enumerable.Range(0, 16).Select(i => (byte)(0xC0 + i)).ToArray();
        var download = client.SdoDownloadAsync(NodeId, 0x2000, 0x00, payload);

        // Initiate (segmented: 16 bytes are above the expedited range) -> forwarded -> acked.
        var initiate = await clientRequests.NextAsync(ShortTimeout, "SDO request from the client");
        initiate[0].Should().Be(0x21, "16 bytes go as a segmented download");
        masterBus.Transmit(CanFrame.Classic(unchecked((int)CanOpenCobId.SdoRx(NodeId)), initiate, isExtendedFrame: false));
        var ack = await serverResponses.NextAsync();
        ack[0].Should().Be(0x60, "the server accepted the initiate and now holds an open download session");
        clientBus.RaiseObserved(CanFrame.Classic(unchecked((int)CanOpenCobId.SdoTx(NodeId)), ack, isExtendedFrame: false), isEcho: false);

        // The client answers with its first segment. It is held here: the server's session is
        // now open and waiting, which is the state the Stop has to find.
        var firstSegment = await clientRequests.NextAsync(ShortTimeout, "first download segment from the client");
        (firstSegment[0] & 0xE0).Should().Be(0x00, "a download segment");

        SendNmt(masterBus, NmtCommand.Stop, NodeId);
        (await nmt.NextAsync(ShortTimeout, "NMT command on the server")).Should().Be(NmtCommand.Stop);

        var abort = await serverResponses.NextAsync();
        abort[0].Should().Be(0x80, "the open session is aborted on the wire");
        SdoFrames.ReadIndex(abort).Should().Be(((ushort)0x2000, (byte)0x00), "the abort names the session's object");
        SdoFrames.ReadAbortCode(abort).Should().Be((uint)SdoAbortCode.DataCannotBeTransferredDeviceState,
            "0800 0022h: data cannot be transferred because of the present device state (Table 22)");
        server.State.Should().Be(NmtState.Stopped);

        clientBus.RaiseObserved(CanFrame.Classic(unchecked((int)CanOpenCobId.SdoTx(NodeId)), abort, isExtendedFrame: false), isEcho: false);
        var ex = await Assert.ThrowsAsync<SdoAbortException>(() => download.WithTimeoutAsync(ShortTimeout));
        ex.AbortCode.Should().Be((uint)SdoAbortCode.DataCannotBeTransferredDeviceState);
        ex.Index.Should().Be(0x2000);
        server.ObjectDictionary.ReadRaw(0x2000, 0x00).Should().Equal(new byte[16], "nothing of the aborted download reached the OD");
    }

    // FR-CO-022 (b): in Stopped an SDO request gets no answer; after Start it does. Two clients
    // share the server so the two requests are independent tasks. The first client's request is
    // seen on the wire before the Start is sent, so the server received it while Stopped; the
    // second client's transfer completing is the ordering witness that the server is answering
    // again -- and at that moment the first task is still pending, which it never stops being.
    //
    // The second request is a download on purpose: its answer (a 60h initiate response) is
    // not a frame the first client's open upload session would take for its own.
    [Fact]
    public async Task In_Stopped_An_Sdo_Request_Is_Not_Answered_And_After_Start_It_Is()
    {
        var session = NewSession();
        using var firstClientBus = Open(session, 0);
        using var serverBus = Open(session, 1);
        using var masterBus = Open(session, 2);
        using var secondClientBus = Open(session, 3);

        using var server = CanOpen.OpenNode(serverBus, NodeId);
        // A long client timeout so the unanswered request is still pending when the witness lands.
        using var firstClient = CanOpen.OpenNode(firstClientBus, nodeId: 0x01,
            new CanOpenNodeOptions().With(sdoTimeout: TimeSpan.FromSeconds(30)));
        using var secondClient = CanOpen.OpenNode(secondClientBus, nodeId: 0x02);
        server.ObjectDictionary.AddU8(0x2000, 0x00, 0);

        var nmt = new AsyncQueue<NmtCommand>();
        server.NmtCommandReceived += (_, e) => nmt.Add(e.Command);
        using var requests = new FrameTap(masterBus, CanOpenCobId.SdoRx(NodeId));

        SendNmt(masterBus, NmtCommand.Stop, NodeId);
        (await nmt.NextAsync(ShortTimeout, "NMT command on the server")).Should().Be(NmtCommand.Stop);

        using var cancelFirst = new CancellationTokenSource();
        var first = firstClient.SdoUploadAsync(NodeId, 0x1000, 0x00, cancelFirst.Token);
        (await requests.NextAsync())[0].Should().Be(0x40, "the upload request reached the bus while the server was Stopped");

        SendNmt(masterBus, NmtCommand.Start, NodeId);
        (await nmt.NextAsync(ShortTimeout, "NMT command on the server")).Should().Be(NmtCommand.Start);

        await secondClient.SdoDownloadAsync(NodeId, 0x2000, 0x00, new byte[] { 0x5A }).WithTimeoutAsync(ShortTimeout);

        first.IsCompleted.Should().BeFalse("the request received in Stopped was dropped, not queued: no answer came (Table 37)");
        server.ObjectDictionary.ReadUnsigned(0x2000, 0x00).Should().Be(0x5Au, "the request after Start was served");

        cancelFirst.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WithTimeoutAsync(ShortTimeout));
    }

    // FR-CO-022 (c): SYNC is not active in Stopped but is in Pre-operational (Table 37). Frames
    // from one bus are handled in order, and the node's events are delivered in order, so the
    // event log itself is the witness: the SYNC sent in Stopped would sit between the two NMT
    // events, and the one sent in Pre-operational is the first SyncReceived to appear.
    [Fact]
    public async Task Sync_Is_Not_Indicated_In_Stopped_But_Is_In_PreOperational()
    {
        var session = NewSession();
        using var nodeBus = Open(session, 1);
        using var masterBus = Open(session, 2);
        using var node = CanOpen.OpenNode(nodeBus, NodeId);

        var log = new AsyncQueue<string>();
        node.NmtCommandReceived += (_, e) => log.Add($"nmt:{e.Command}");
        node.SyncReceived += (_, _) => log.Add("sync");

        SendNmt(masterBus, NmtCommand.Stop, NodeId);
        SendSync(masterBus);
        SendNmt(masterBus, NmtCommand.EnterPreOperational, NodeId);
        SendSync(masterBus);

        (await log.NextAsync(ShortTimeout, "node event")).Should().Be("nmt:Stop");
        (await log.NextAsync(ShortTimeout, "node event")).Should().Be("nmt:EnterPreOperational",
            "the SYNC received in Stopped raises nothing (Table 37)");
        (await log.NextAsync(ShortTimeout, "node event")).Should().Be("sync",
            "the SYNC received in Pre-operational is indicated");
        node.State.Should().Be(NmtState.PreOperational);
    }

    // FR-CO-022 (d): a SYNC producer keeps its cycle through Stopped but transmits nothing
    // there, and resumes after Start (Table 37; ICanOpenNode.StartSyncProducer). On the hand
    // clock every cycle boundary is crossed on purpose: one before the Stop (a SYNC), three
    // while Stopped (each tick ran -- the settle proves it -- and sent nothing), one after the
    // Start (a SYNC). The second SYNC is the witness; anything transmitted while Stopped would
    // be sitting in the tap ahead of it.
    [Fact]
    public async Task A_Sync_Producer_Transmits_Nothing_While_Stopped_And_Resumes_After_Start()
    {
        var session = NewSession();
        using var nodeBus = Open(session, 1);
        using var masterBus = Open(session, 2);
        using var syncTap = new FrameTap(masterBus, CanOpenCobId.Sync);
        var clock = new ManualTimeSource();
        using var node = OpenClockedNode(nodeBus, NodeId, clock);
        var witness = new ActorWitness(node, masterBus, clock);
        var nmt = new AsyncQueue<NmtCommand>();
        node.NmtCommandReceived += (_, e) => nmt.Add(e.Command);

        var cycle = TimeSpan.FromMilliseconds(10);
        node.StartSyncProducer(cycle);
        await witness.SettleAsync(); // the first cycle is armed on the clock as it stands
        SendNmt(masterBus, NmtCommand.Start, NodeId);
        (await nmt.NextAsync(ShortTimeout, "NMT command")).Should().Be(NmtCommand.Start);

        await witness.AdvanceAsync(cycle);
        (await syncTap.NextAsync()).Should().BeEmpty("a SYNC carries no data");

        SendNmt(masterBus, NmtCommand.Stop, NodeId);
        (await nmt.NextAsync(ShortTimeout, "NMT command")).Should().Be(NmtCommand.Stop);
        for (var tick = 0; tick < 3; tick++) await witness.AdvanceAsync(cycle);

        SendNmt(masterBus, NmtCommand.Start, NodeId);
        (await nmt.NextAsync(ShortTimeout, "NMT command")).Should().Be(NmtCommand.Start);
        await witness.AdvanceAsync(cycle);
        (await syncTap.NextAsync()).Should().BeEmpty("the producer resumed with the first cycle after Start");

        syncTap.Count.Should().Be(0, "three cycles elapsed in Stopped and none of them transmitted a SYNC (Table 37)");
    }

    // FR-CO-022 (e): "If there are EMCY messages triggered in this NMT state they are pending.
    // The most recent active EMCY reason may be transmitted after the CANopen device transits
    // into another NMT state" (§7.3.2.2.4). SendEmcyAsync completes in Stopped without a frame;
    // of two held EMCYs only the last goes out, on the transition to Pre-operational.
    //
    // "Without a frame" is exact rather than witnessed: when SendEmcyAsync does transmit, the
    // task it returns is the transmission's own confirmation, so a frame sent in Stopped would
    // be on the bus before the await returned. The guarding reply after the transition is the
    // witness that no second EMCY followed the one that was due.
    [Fact]
    public async Task Emcy_In_Stopped_Is_Held_And_The_Most_Recent_One_Goes_Out_On_Leaving_Stopped()
    {
        var session = NewSession();
        using var nodeBus = Open(session, 1);
        using var masterBus = Open(session, 2);
        using var heartbeatTap = new FrameTap(masterBus, CanOpenCobId.Heartbeat(NodeId));
        using var emcyTap = new FrameTap(masterBus, CanOpenCobId.Emcy(NodeId));
        using var node = CanOpen.OpenNode(nodeBus, NodeId);
        var nmt = new AsyncQueue<NmtCommand>();
        node.NmtCommandReceived += (_, e) => nmt.Add(e.Command);
        (await heartbeatTap.NextAsync())[0].Should().Be(0x00, "boot-up");

        SendNmt(masterBus, NmtCommand.Stop, NodeId);
        (await nmt.NextAsync(ShortTimeout, "NMT command")).Should().Be(NmtCommand.Stop);

        await node.SendEmcyAsync(errorCode: 0x1000, errorRegister: 0x01).WithTimeoutAsync(ShortTimeout);
        await node.SendEmcyAsync(errorCode: 0x2000, errorRegister: 0x03).WithTimeoutAsync(ShortTimeout);
        emcyTap.Count.Should().Be(0, "EMCY triggered in Stopped is pending, not transmitted (§7.3.2.2.4)");

        SendNmt(masterBus, NmtCommand.EnterPreOperational, NodeId);
        var emcy = await emcyTap.NextAsync();
        (emcy[0] | (emcy[1] << 8)).Should().Be(0x2000, "the most recent EMCY reason is the one transmitted after the transition");
        emcy[2].Should().Be(0x03, "with the error register it carried");

        SendRtr(masterBus, NodeId);
        (await heartbeatTap.NextAsync())[0].Should().Be((byte)NmtState.PreOperational, "guarding reply: the witness after the transition");
        emcyTap.Count.Should().Be(0, "the earlier EMCY was superseded, not queued behind the last one");
        node.ObjectDictionary.ReadUnsigned(0x1001, 0x00).Should().Be(0x03u, "1001h mirrors the last EMCY's error register");
    }

    // FR-CO-022 (f), producer off: the heartbeat that announces a state change belongs to the
    // heartbeat protocol (§7.2.8.3.2.2), so with 1017h = 0 an NMT Start puts nothing on
    // 0x700 + id. The witness is two guarding replies: the first is the Start's Operational
    // with toggle 0 -- on the wire the same byte a leaked state-change heartbeat would be, which
    // is exactly the ambiguity #43 records -- and the second, toggle 1, is what tells them
    // apart: had a heartbeat leaked, it would be the first frame and the toggle-0 reply the
    // second.
    [Fact]
    public async Task Nmt_Start_Without_A_Heartbeat_Producer_Puts_Nothing_On_The_Heartbeat_CobId()
    {
        var session = NewSession();
        using var nodeBus = Open(session, 1);
        using var masterBus = Open(session, 2);
        using var heartbeatTap = new FrameTap(masterBus, CanOpenCobId.Heartbeat(NodeId));
        using var node = CanOpen.OpenNode(nodeBus, NodeId);
        (await heartbeatTap.NextAsync())[0].Should().Be(0x00, "boot-up");

        SendNmt(masterBus, NmtCommand.Start, NodeId);
        SendRtr(masterBus, NodeId);
        SendRtr(masterBus, NodeId);

        (await heartbeatTap.NextAsync())[0].Should().Be(0x05, "the first frame after the Start is the reply: Operational, toggle 0");
        (await heartbeatTap.NextAsync())[0].Should().Be(0x85, "the second is the next reply, toggle 1: no heartbeat sat in front of them");
        node.State.Should().Be(NmtState.Operational);
    }

    // FR-CO-022 (f), producer on: with 1017h ≠ 0 the state change is announced at once, ahead
    // of the periodic heartbeat -- which on the hand clock is not due until the clock says so.
    [Fact]
    public async Task Nmt_Start_With_A_Heartbeat_Producer_Announces_The_State_Before_The_Periodic_Heartbeat()
    {
        var session = NewSession();
        using var nodeBus = Open(session, 1);
        using var masterBus = Open(session, 2);
        using var heartbeatTap = new FrameTap(masterBus, CanOpenCobId.Heartbeat(NodeId));
        var clock = new ManualTimeSource();
        using var node = OpenClockedNode(nodeBus, NodeId, clock);
        var witness = new ActorWitness(node, masterBus, clock);
        (await heartbeatTap.NextAsync())[0].Should().Be(0x00, "boot-up");

        var period = TimeSpan.FromMilliseconds(500);
        node.StartHeartbeatProducer(period);
        await witness.SettleAsync(); // the producer's first period is armed
        heartbeatTap.Count.Should().Be(0, "starting the producer does not transmit; the first heartbeat is due after one period");

        SendNmt(masterBus, NmtCommand.Start, NodeId);
        (await heartbeatTap.NextAsync())[0].Should().Be((byte)NmtState.Operational,
            "the state change is announced without the clock moving");
        await witness.SettleAsync();
        heartbeatTap.Count.Should().Be(0, "the periodic heartbeat is not due yet");

        await witness.AdvanceAsync(period);
        (await heartbeatTap.NextAsync())[0].Should().Be((byte)NmtState.Operational, "the periodic heartbeat, one period later");
    }

    // FR-CO-022 (g): "The toggle bit in the guarding protocol shall be reset to 0 when the NMT
    // sub-state reset communication is passed (no other change of NMT state resets the toggle
    // bit)" (§7.2.8.3.2.1). One reply before the reset is the row that can tell: the next reply
    // would otherwise carry toggle 1. After two replies the alternation is back at 0 anyway, so
    // that row states the norm's "previous reply had toggle 1" case without discriminating.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Reset_Communication_Resets_The_Guarding_Toggle_To_Zero(int repliesBeforeReset)
    {
        var session = NewSession();
        using var nodeBus = Open(session, 1);
        using var masterBus = Open(session, 2);
        using var heartbeatTap = new FrameTap(masterBus, CanOpenCobId.Heartbeat(NodeId));
        using var node = CanOpen.OpenNode(nodeBus, NodeId);
        (await heartbeatTap.NextAsync())[0].Should().Be(0x00, "boot-up");

        for (var n = 1; n <= repliesBeforeReset; n++)
        {
            SendRtr(masterBus, NodeId);
            var expected = (byte)(NmtState.PreOperational) | (n % 2 == 0 ? 0x80 : 0x00);
            (await heartbeatTap.NextAsync())[0].Should().Be((byte)expected, "reply {0} alternates the toggle", n);
        }

        SendNmt(masterBus, NmtCommand.ResetCommunication, NodeId);
        (await heartbeatTap.NextAsync())[0].Should().Be(0x00, "reset communication passes Initialisation and sends boot-up");

        SendRtr(masterBus, NodeId);
        (await heartbeatTap.NextAsync())[0].Should().Be((byte)NmtState.PreOperational,
            "the first reply after reset communication carries toggle 0 (§7.2.8.3.2.1)");
    }

    // FR-CO-022 (h): a node-guarding consumer registered for the node's own id (a closed loop on
    // an echo bus, #119) receives replies that report the node's actual NMT state -- the
    // assertion #43 asks for, made here rather than in the peer test's sibling. Before any
    // command that is Pre-operational; after the node starts itself, Operational, and once an
    // Operational reply has been seen no Pre-operational one follows.
    [Theory]
    [MemberData(nameof(EchoWorldFixture.Both), MemberType = typeof(EchoWorldFixture))]
    public async Task A_NodeGuarding_Consumer_For_The_Local_Id_Reports_The_Nodes_Own_Nmt_State(EchoWorld world)
    {
        using var echo = EchoWorldFixture.Create(world, NewSession());
        using var node = CanOpen.OpenNode(echo.Bus, nodeId: 0x01);

        var replies = new AsyncQueue<NodeGuardingReceivedEventArgs>();
        node.NodeGuardingReceived += (_, e) => { if (e.ProducerNodeId == 0x01) replies.Add(e); };
        var nmt = new AsyncQueue<NmtCommand>();
        node.NmtCommandReceived += (_, e) => nmt.Add(e.Command);

        node.StartNodeGuardingConsumer(producerNodeId: 0x01, guardTime: TimeSpan.FromMilliseconds(30), lifeTimeFactor: 3);

        var first = await replies.NextAsync(ShortTimeout, "guarding reply");
        first.State.Should().Be(NmtState.PreOperational, "a fresh node answers with the state it is in");
        first.State.Should().Be(node.State);

        await node.SendNmtCommandAsync(NmtCommand.Start, targetNodeId: 0x01).WithTimeoutAsync(ShortTimeout);
        (await nmt.NextAsync(ShortTimeout, "NMT command")).Should().Be(NmtCommand.Start);
        node.State.Should().Be(NmtState.Operational);

        // Replies to polls the node answered before it saw the Start may still be in flight and
        // report Pre-operational; they are finite. The bound is on the whole wait, not per
        // reply, because a reply every guard time that never reports Operational must fail this
        // test rather than feed it indefinitely.
        var seen = new List<NmtState>();
        var deadline = DateTime.UtcNow + ShortTimeout;
        NodeGuardingReceivedEventArgs reply;
        do
        {
            reply = await replies.NextAsync(deadline - DateTime.UtcNow, "guarding reply reporting Operational");
            seen.Add(reply.State);
        }
        while (reply.State != NmtState.Operational);
        node.StopNodeGuardingConsumer(0x01);

        seen.Should().OnlyContain(s => s == NmtState.PreOperational || s == NmtState.Operational,
            "every reply reports one of the two states the node has been in");
        seen.SkipWhile(s => s == NmtState.PreOperational).Should().OnlyContain(s => s == NmtState.Operational,
            "replies answered before the Start report Pre-operational and every later one Operational");
    }

    // =========================================================================================
    // Helpers.
    // =========================================================================================

    /// <summary>The node under test on the clock the test drives (#113).</summary>
    private static CanOpenNode OpenClockedNode(ICanBus bus, byte nodeId, ManualTimeSource clock)
        => new(new CanBusService(bus), nodeId, new CanOpenNodeOptions(), ownsService: true, timeSource: clock);

    /// <summary>A node-guarding poll: an RTR on <c>0x700 + nodeId</c>. Same shape the node's own
    /// consumer sends (CanOpenNode.NodeGuarding.cs, SendNodeGuardingRtr), DLC 0 included.</summary>
    private static void SendRtr(ICanBus bus, byte nodeId)
        => bus.Transmit(CanFrame.Classic(unchecked((int)CanOpenCobId.Heartbeat(nodeId)),
            ReadOnlyMemory<byte>.Empty, isExtendedFrame: false, isRemoteFrame: true));

    private static void SendNmt(ICanBus bus, NmtCommand command, byte targetNodeId)
        => bus.Transmit(CanFrame.Classic(unchecked((int)CanOpenCobId.NmtCommand),
            new[] { (byte)command, targetNodeId }, isExtendedFrame: false));

    private static void SendSync(ICanBus bus)
        => bus.Transmit(CanFrame.Classic(unchecked((int)CanOpenCobId.Sync), Array.Empty<byte>(), isExtendedFrame: false));

    /// <summary>
    /// A queue the test awaits one item at a time. Handlers add from the node's event pump or a
    /// bus callback; the test body takes with a bound, so a missing item is a timeout with a
    /// name rather than a hang.
    /// </summary>
    private sealed class AsyncQueue<T>
    {
        private readonly ConcurrentQueue<T> _items = new();
        private readonly SemaphoreSlim _available = new(0, int.MaxValue);

        public int Count => _items.Count;

        public void Add(T item)
        {
            _items.Enqueue(item);
            _available.Release();
        }

        public async Task<T> NextAsync(TimeSpan timeout, string what)
        {
            if (timeout <= TimeSpan.Zero || !await _available.WaitAsync(timeout).ConfigureAwait(false))
                throw new TimeoutException($"No {what} within {timeout}.");
            _items.TryDequeue(out var item).Should().BeTrue();
            return item!;
        }
    }

    /// <summary>Queues the payload of every data frame on one COB-ID seen by a bus; remote frames
    /// (the test's own RTRs share the guarding COB-ID) are not frames the node sent.</summary>
    private sealed class FrameTap : IDisposable
    {
        private readonly ICanBus _bus;
        private readonly uint _cobId;
        private readonly AsyncQueue<byte[]> _frames = new();

        public FrameTap(ICanBus bus, uint cobId)
        {
            _bus = bus;
            _cobId = cobId;
            _bus.FrameObserved += OnFrame;
        }

        public int Count => _frames.Count;

        public Task<byte[]> NextAsync() => _frames.NextAsync(ShortTimeout, $"frame on COB-ID 0x{_cobId:X3}");

        private void OnFrame(object? sender, CanReceiveDataView e)
        {
            var frame = e.CanFrame;
            if (frame.IsExtendedFrame || frame.IsRemoteFrame || (uint)frame.ID != _cobId) return;
            _frames.Add(frame.Data.ToArray());
        }

        public void Dispose() => _bus.FrameObserved -= OnFrame;
    }

    /// <summary>
    /// The ordering witness for a node on the hand clock. A poke is a heartbeat from a peer
    /// nobody consumes, sent from the test's bus; the node's <c>HeartbeatReceived</c> for it is
    /// proof that the actor has drained everything posted before the frame -- a configuration
    /// method's OD apply included -- and that the event pump has delivered everything enqueued
    /// before it.
    /// </summary>
    /// <remarks>
    /// <see cref="SettleAsync"/> pokes twice, for the reason <see cref="VirtualClock"/> gives:
    /// the first poke's event is enqueued from inside an iteration's <c>DrainMailbox</c>, whose
    /// budget was fixed when it started, so a second poke sent after that event arrived lands
    /// in a later iteration -- after the first iteration's <c>FireDueTimers</c>. Every timer the
    /// clock made due has therefore fired, and whatever it enqueued precedes the second poke's
    /// event. Awaiting that event is the proof; nothing here waits for time to pass.
    /// </remarks>
    private sealed class ActorWitness
    {
        private readonly ICanBus _peerBus;
        private readonly ManualTimeSource _clock;
        private readonly SemaphoreSlim _seen = new(0, int.MaxValue);

        public ActorWitness(ICanOpenNode node, ICanBus peerBus, ManualTimeSource clock)
        {
            _peerBus = peerBus;
            _clock = clock;
            node.HeartbeatReceived += (_, e) =>
            {
                if (e.ProducerNodeId == WitnessPeer) _seen.Release();
            };
        }

        public async Task PokeAsync()
        {
            _peerBus.Transmit(CanFrame.Classic(unchecked((int)CanOpenCobId.Heartbeat(WitnessPeer)),
                new[] { (byte)NmtState.PreOperational }, isExtendedFrame: false));
            if (!await _seen.WaitAsync(ShortTimeout).ConfigureAwait(false))
                throw new TimeoutException($"The node did not report the witness heartbeat within {ShortTimeout}.");
        }

        public async Task SettleAsync()
        {
            await PokeAsync().ConfigureAwait(false);
            await PokeAsync().ConfigureAwait(false);
        }

        /// <summary>Moves the clock and returns once every timer that became due has fired.</summary>
        public async Task AdvanceAsync(TimeSpan by)
        {
            _clock.Advance(by);
            await SettleAsync().ConfigureAwait(false);
        }
    }
}
