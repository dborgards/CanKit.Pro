using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core;
using CanKit.Pro.Addressing;
using CanKit.Pro.J1939Tp;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using FluentAssertions;
using Xunit;
// The factory class and its namespace share a name, and this test now lives under CanKit.Pro,
// so plain `J1939Tp` binds to the namespace. Aliasing is what the ISO-TP tests already do.
using J1939TpFactory = CanKit.Pro.J1939Tp.J1939Tp;

namespace CanKit.Pro.Tests.TestCases;

/// <summary>
/// Virtual-loopback integration tests for the J1939-21 §5.10 Transport Protocol implementation
/// in <c>CanKit.Pro.J1939Tp</c> (SRS FR-TP-030..035). Uses the same
/// <c>CanKit.Adapter.Virtual</c> pattern the other L2/L3 tests use, so a real bus is never
/// required and the tests are portable across every CI runner.
/// </summary>
public class J1939TpTests : IClassFixture<VirtualAdapterFixture>
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);

    private static string NewSession() => $"j1939tp-{Guid.NewGuid():N}";

    // Both nodes share the same virtual bus (session-scoped) but appear as different channels of
    // the same VirtualBusHub, so a Transmit on one is seen by the other's FrameObserved. Every
    // test pins its own session to guarantee isolation.
    private static ICanBus Open(string session, int channel) => CanBus.Open(
        $"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(VirtualAdapterFixture.Bitrate));

    private static byte[] RandomPayload(int length, int seed)
    {
        var rng = new Random(seed);
        var buf = new byte[length];
        rng.NextBytes(buf);
        return buf;
    }

    // Regression for #23: two channels sharing one service must still hear each other on a bus
    // whose echoes *are* flagged.
    //
    // `J1939Tp.Open(ICanBusService, ...)` documents that "multiple channels with different source
    // addresses may share the same service". On a flagging adapter every frame either channel
    // sends is marked IsEcho — the flag identifies the host, not the channel — so withholding
    // echoes cut the siblings off from each other entirely. The channels now opt in, and the
    // existing `fields.SourceAddress == _sourceAddress` check in RunReaderAsync does the
    // instance-level filtering the echo bit cannot.
    [Fact]
    public async Task Two_Channels_Sharing_One_Service_Still_Hear_Each_Other_On_A_Flagging_Echo_Bus()
    {
        using var bus = ControllableBus.EchoCapable(NewSession());
        using var service = new CanBusService(bus);

        var opts = new J1939TpOptions().With(bamPacketSpacing: TimeSpan.FromMilliseconds(5));
        using var sender = J1939TpFactory.Open(service, sourceAddress: 0x10, options: opts);
        using var receiver = J1939TpFactory.Open(service, sourceAddress: 0x20, options: opts);

        var payload = RandomPayload(100, seed: 20);

        var receiveTask = receiver.ReceiveAsync().AsTaskWithTimeout(ShortTimeout);
        await sender.SendBamAsync(0xFECBu, payload).WithTimeout(ShortTimeout);

        var datagram = await receiveTask;
        datagram.SourceAddress.Should().Be(
            0x10,
            "a sibling channel's BAM is peer traffic to this channel, even though the host echo "
            + "flag marks it exactly like this channel's own transmissions");
        datagram.Payload.Should().Equal(payload);
    }

    // Regression for #23: a TP channel must never receive its own broadcast, even on an adapter
    // whose echoes are not flagged.
    //
    // #23 gave subscriptions a real IsEcho bit and withheld echoes by default, and the
    // source-address self-check in J1939TpChannel.RunReaderAsync was deleted as redundant. It is
    // not: the echo gate can only drop what the adapter flags, and CanKit.Adapter.Virtual in
    // ChannelWorkMode.Echo echoes without setting IsEcho (VirtualBusHub.Broadcast builds its
    // CanReceiveData without it, and the remarks on ControllableBus record the same fact). Without
    // the check, SendBamAsync consumed its own globally addressed BAM and DT frames and
    // republished the outbound payload as an inbound datagram.
    //
    // Deliberately the real Virtual adapter and not ControllableBus: the whole point is the
    // behaviour of an adapter that does not flag its echo, so substituting a double that does
    // would test the opposite of what this pins.
    [Fact]
    public async Task Bam_Sender_On_An_Unflagged_Echo_Bus_Does_Not_Receive_Its_Own_Broadcast()
    {
        var session = NewSession();
        using var bus = VirtualAdapterFixture.Open(session, 0, ChannelWorkMode.Echo);

        var opts = new J1939TpOptions().With(bamPacketSpacing: TimeSpan.FromMilliseconds(5));
        using var sender = J1939TpFactory.Open(bus, sourceAddress: 0x11, options: opts);

        var payload = RandomPayload(100, seed: 23);

        // Start listening before sending: if the channel does route its own echo, the datagram
        // shows up here rather than being missed by a late subscriber.
        var selfReceive = sender.ReceiveAsync();
        await sender.SendBamAsync(0xFECAu, payload).WithTimeout(ShortTimeout);

        var settled = await Task.WhenAny(selfReceive, Task.Delay(TimeSpan.FromMilliseconds(500)));
        settled.Should().NotBeSameAs(
            selfReceive,
            "a TP channel must not reassemble its own broadcast; the echo gate cannot drop what "
            + "the Virtual adapter never flagged, so the source-address check has to catch it");
    }

    // FR-TP-030 + FR-TP-032 + FR-TP-033: TP.BAM sender broadcasts a 100-byte PDU; the receiver
    // reassembles it identically from TP.CM(BAM) + TP.DT 1..15.
    [Fact]
    public async Task Bam_Roundtrip_ReceiverReassemblesIdenticalPayload()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);

        // Shorten Th so the test runs in <1s while still exercising the timer.
        var opts = new J1939TpOptions().With(bamPacketSpacing: TimeSpan.FromMilliseconds(5));

        using var sender = J1939TpFactory.Open(busA, sourceAddress: 0x11, options: opts);
        using var receiver = J1939TpFactory.Open(busB, sourceAddress: 0x22, options: opts);

        var payload = RandomPayload(100, seed: 42);
        var pgn = 0xFECAu; // arbitrary PDU2 broadcast PGN

        var receiveTask = receiver.ReceiveAsync().AsTaskWithTimeout(ShortTimeout);
        await sender.SendBamAsync(pgn, payload).WithTimeout(ShortTimeout);

        var datagram = await receiveTask;
        datagram.Kind.Should().Be(J1939TpKind.Bam);
        datagram.SourceAddress.Should().Be(0x11);
        datagram.DestinationAddress.Should().Be(0xFF);
        datagram.Pgn.Should().Be(pgn);
        datagram.Payload.Should().Equal(payload);
    }

    // FR-TP-031 + FR-TP-032 + FR-TP-033: TP.CM sender emits RTS -> receiver replies CTS ->
    // sender streams TP.DT -> receiver reassembles and sends EndOfMsgAck -> sender's task
    // completes.
    [Fact]
    public async Task Cm_Roundtrip_ReceiverReassemblesAndAcks()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);

        using var sender = J1939TpFactory.Open(busA, sourceAddress: 0x01);
        using var receiver = J1939TpFactory.Open(busB, sourceAddress: 0x02);

        var payload = RandomPayload(300, seed: 7);
        var pgn = 0xEF00u; // arbitrary PDU1 destination-addressed PGN

        var receiveTask = receiver.ReceiveAsync().AsTaskWithTimeout(ShortTimeout);
        await sender.SendCmAsync(pgn, destinationAddress: 0x02, payload).WithTimeout(ShortTimeout);

        var datagram = await receiveTask;
        datagram.Kind.Should().Be(J1939TpKind.Cm);
        datagram.SourceAddress.Should().Be(0x01);
        datagram.DestinationAddress.Should().Be(0x02);
        datagram.Pgn.Should().Be(pgn);
        datagram.Payload.Should().Equal(payload);
    }

    // FR-TP-032 + FR-TP-033: exact 7*N boundary payload -- one full block of TP.DT frames --
    // reassembles correctly.
    [Fact]
    public async Task Cm_ExactBoundaryPayload_Reassembles()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);

        using var sender = J1939TpFactory.Open(busA, sourceAddress: 0x03);
        using var receiver = J1939TpFactory.Open(busB, sourceAddress: 0x04);

        // 7 * 16 = 112 bytes -- one full CTS block at default MaxPacketsPerCts=16.
        var payload = RandomPayload(112, seed: 99);
        var receiveTask = receiver.ReceiveAsync().AsTaskWithTimeout(ShortTimeout);

        await sender.SendCmAsync(0xFF10, destinationAddress: 0x04, payload).WithTimeout(ShortTimeout);
        var datagram = await receiveTask;
        datagram.Payload.Should().Equal(payload);
    }

    // FR-TP-034/035: two independent TP.CM sessions to different destinations run in parallel
    // over the same physical bus, plus a concurrent TP.BAM broadcast, all reassembled correctly
    // by the intended recipients.
    [Fact]
    public async Task Parallel_Bam_And_TwoCm_Sessions_Do_Not_Interfere()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var busC = Open(session, 2);

        // Th shortened, as every other BAM test in this file does. This was the one that did
        // not, and the omission is what made it the file's CI flake (#92, #114): a 200-byte BAM
        // is 29 TP.DT packets, so the default 50 ms hold-off is 28 mandatory gaps -- 1400 ms of
        // pacing the test cannot avoid -- inside a 5 s ShortTimeout. That leaves roughly 107 ms
        // of slack per scheduled hop, and the gaps are actor Schedule callbacks, so a loaded
        // runner eats it. At 5 ms the same 28 gaps cost 140 ms and the margin is ~35x.
        var opts = new J1939TpOptions().With(bamPacketSpacing: TimeSpan.FromMilliseconds(5));
        using var sender = J1939TpFactory.Open(busA, sourceAddress: 0x10, options: opts);
        using var receiverB = J1939TpFactory.Open(busB, sourceAddress: 0xB0, options: opts);
        using var receiverC = J1939TpFactory.Open(busC, sourceAddress: 0xC0, options: opts);

        var payloadBam = RandomPayload(200, seed: 1);
        var payloadCmB = RandomPayload(400, seed: 2);
        var payloadCmC = RandomPayload(250, seed: 3);

        var pgnBam = 0xFEF0u;
        var pgnCmB = 0xFE10u;
        var pgnCmC = 0xFE20u;

        // Collect BAM on both receivers, CM only on its target.
        var collectB = CollectAsync(receiverB, count: 2, ShortTimeout);
        var collectC = CollectAsync(receiverC, count: 2, ShortTimeout);

        var t1 = sender.SendBamAsync(pgnBam, payloadBam);
        var t2 = sender.SendCmAsync(pgnCmB, destinationAddress: 0xB0, payloadCmB);
        var t3 = sender.SendCmAsync(pgnCmC, destinationAddress: 0xC0, payloadCmC);
        await Task.WhenAll(t1, t2, t3).WithTimeout(ShortTimeout);

        var listB = await collectB;
        var listC = await collectC;

        // ReceiverB must see the BAM (broadcast) and its own CM.
        listB.Should().HaveCount(2);
        var bamOnB = listB.Single(d => d.Kind == J1939TpKind.Bam);
        var cmOnB = listB.Single(d => d.Kind == J1939TpKind.Cm);
        bamOnB.Pgn.Should().Be(pgnBam);
        bamOnB.Payload.Should().Equal(payloadBam);
        cmOnB.Pgn.Should().Be(pgnCmB);
        cmOnB.Payload.Should().Equal(payloadCmB);

        // ReceiverC must see the BAM and its own CM (not the CM sent to B).
        listC.Should().HaveCount(2);
        var bamOnC = listC.Single(d => d.Kind == J1939TpKind.Bam);
        var cmOnC = listC.Single(d => d.Kind == J1939TpKind.Cm);
        bamOnC.Pgn.Should().Be(pgnBam);
        bamOnC.Payload.Should().Equal(payloadBam);
        cmOnC.Pgn.Should().Be(pgnCmC);
        cmOnC.Payload.Should().Equal(payloadCmC);
    }

    // FR-TP-032 negative path: with no peer to answer, the TP.CM sender's own T3 timer expires
    // and its SendCmAsync task faults with a J1939TpAbortException(Reason=Timeout).
    [Fact]
    public async Task Cm_NoPeer_TimesOutWithAbortException()
    {
        var session = NewSession();
        using var bus = Open(session, 0);

        // Only the sender is present; nobody will reply with CTS. Shorten T3 so the test runs
        // in ~200 ms instead of the standard-recommended 1250 ms.
        var opts = new J1939TpOptions().With(
            t2: TimeSpan.FromMilliseconds(150),
            t3: TimeSpan.FromMilliseconds(150));

        using var sender = J1939TpFactory.Open(bus, sourceAddress: 0x30, options: opts);

        var send = sender.SendCmAsync(0xFE30, destinationAddress: 0x99,
            RandomPayload(50, seed: 5));

        Func<Task> act = async () => await send.WithTimeout(ShortTimeout);
        var ex = (await act.Should().ThrowAsync<J1939TpAbortException>()).Which;
        ex.Reason.Should().Be(J1939TpAbortReason.Timeout);
        ex.Pgn.Should().Be(0xFE30u);
    }

    // FR-TP-030 lower-bound check: a 9-byte payload (the smallest legal J1939-TP payload;
    // anything ≤ 8 must use single-frame per §5.10.1) still round-trips correctly.
    [Fact]
    public async Task Bam_MinimumPayload_Roundtrip()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);

        var opts = new J1939TpOptions().With(bamPacketSpacing: TimeSpan.FromMilliseconds(5));
        using var sender = J1939TpFactory.Open(busA, sourceAddress: 0x40, options: opts);
        using var receiver = J1939TpFactory.Open(busB, sourceAddress: 0x41, options: opts);

        var payload = RandomPayload(9, seed: 11);
        var receiveTask = receiver.ReceiveAsync().AsTaskWithTimeout(ShortTimeout);
        await sender.SendBamAsync(0xFEEE, payload).WithTimeout(ShortTimeout);

        var datagram = await receiveTask;
        datagram.Payload.Should().Equal(payload);
    }

    // FR-TP-030 upper-bound check: the largest legal J1939-TP payload (1785 bytes = 255 * 7)
    // still round-trips via BAM. Exercises SN wrap all the way to 255 without off-by-one bugs.
    [Fact]
    public async Task Bam_MaximumPayload_Roundtrip()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);

        // Th=0: the subject here is the sequence number reaching 255 without an off-by-one, not
        // pacing, and the maximum payload is 255 TP.DT packets -- so any non-zero Th puts 254
        // scheduled waits on the path. That is not 254 ms. A Schedule(1 ms) parks the actor loop,
        // and a parked loop on a contended host wakes when the scheduler gets round to it: under
        // 8x load on four cores the same transfer took 48-55 s against this test's 10 s budget,
        // and failed 6 of 6. At Th=0 nothing is ever not-yet-due, the loop drains instead of
        // parking, and the same load costs 1-3 s (#114).
        //
        // Paced BAM is covered by the other BAM tests in this file, which is why it can go here.
        var opts = new J1939TpOptions().With(bamPacketSpacing: TimeSpan.Zero);
        using var sender = J1939TpFactory.Open(busA, sourceAddress: 0x50, options: opts);
        using var receiver = J1939TpFactory.Open(busB, sourceAddress: 0x51, options: opts);

        var payload = RandomPayload(1785, seed: 13);
        var receiveTask = receiver.ReceiveAsync().AsTaskWithTimeout(TimeSpan.FromSeconds(10));
        await sender.SendBamAsync(0xFEED, payload).WithTimeout(TimeSpan.FromSeconds(10));

        var datagram = await receiveTask;
        datagram.Payload.Should().Equal(payload);
    }

    // Bugbot 3595010737 (PR #25): TP.DT frames carry no PGN, so an RX-session map keyed by
    // (SA, PGN) forces the DT handler to guess a session by (SA, kind) alone, which corrupts
    // reassembly if two overlapping CM sessions from the same source (different PGNs) coexist.
    // J1939-21 §5.10.3 requires exactly one CM connection per (SA, DA) pair, so the fix is to
    // refuse the second RTS with SessionAlreadyOpen and keep the first session's DT stream
    // running to completion. This test replays that scenario end-to-end via raw frame injection.
    [Fact]
    public async Task SecondRtsFromSamePeer_DifferentPgn_IsAbortedAndDtRoutesToActiveSession()
    {
        var session = NewSession();
        using var receiverBus = Open(session, 0);
        using var peerBus = Open(session, 1);

        const byte receiverSa = 0x22;
        const byte peerSa = 0x11;
        const uint activePgn = 0xFBCDu;
        const uint intruderPgn = 0xF876u;

        // 14-byte payload = exactly 2 TP.DT frames -> minimal, deterministic size.
        var payload = RandomPayload(14, seed: 314);

        // Long T2: after CTS the receiver arms T2 waiting for the first DT. This test intentionally
        // injects a second RTS and asserts the Abort *before* sending DTs; a short T2 could
        // expire on a slow Windows/net48 runner and emit Abort(Timeout) for the active PGN, which
        // is correct protocol behavior but unrelated to the intruder-RTS rejection under test.
        var opts = new J1939TpOptions().With(t2: TimeSpan.FromSeconds(5));
        using var receiver = J1939TpFactory.Open(receiverBus, sourceAddress: receiverSa, options: opts);

        // Observe every TP.CM frame the receiver emits so we can inspect CTS / EOM / Abort.
        var observed = new List<(uint canId, byte[] data)>();
        var cmFrames = new List<(uint canId, byte[] data)>();
        var frameReady = new SemaphoreSlim(0);
        peerBus.FrameObserved += (_, e) =>
        {
            var frame = e.CanFrame;
            if (!frame.IsExtendedFrame) return;
            var fields = J1939Id.Decompose((uint)frame.ID);
            if (fields.SourceAddress != receiverSa) return; // only interested in what the SUT emits
            var data = frame.Data.ToArray();
            lock (observed)
            {
                observed.Add(((uint)frame.ID, data));
                if (J1939Pgn.IsTransportCm(fields.Pgn))
                    cmFrames.Add(((uint)frame.ID, data));
            }
            frameReady.Release();
        };

        async Task<byte[]> WaitForCmFrameAsync(Func<byte[], bool> predicate, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            while (true)
            {
                // Must lock the same object the FrameObserved handler uses when mutating
                // cmFrames (lock (observed)); locking cmFrames alone races Add vs. foreach.
                lock (observed)
                {
                    foreach (var (_, data) in cmFrames)
                        if (predicate(data)) return data;
                }
                await frameReady.WaitAsync(cts.Token).ConfigureAwait(false);
            }
        }

        static byte[] BuildFrame(byte[] payload) => payload; // clarity alias

        // --- 1. Send RTS #1 from peer for activePgn (2 packets, 14 bytes) ---
        var rts1 = J1939TpFrames.BuildRts(totalBytes: 14, totalPackets: 2, maxPacketsPerCts: 0xFF, dataPgn: activePgn);
        var rts1Id = J1939Id.ComposePgn(priority: 7, pgn: J1939Pgn.TpCm, sourceAddress: peerSa, destinationAddress: receiverSa);
        peerBus.Transmit(CanFrame.Classic((int)rts1Id, BuildFrame(rts1), isExtendedFrame: true));

        // --- 2. Wait for CTS from the receiver for activePgn ---
        var cts1 = await WaitForCmFrameAsync(
            d => d.Length >= 8 && d[0] == J1939TpFrames.ControlCts
                 && J1939TpFrames.ReadDataPgn(d) == activePgn,
            ShortTimeout);
        cts1[1].Should().BeGreaterThan(0, "receiver must grant at least one packet");
        cts1[2].Should().Be(1, "next expected SN is 1");

        // --- 3. Inject RTS #2 from *same* peer SA but for a *different* PGN ---
        var rts2 = J1939TpFrames.BuildRts(totalBytes: 14, totalPackets: 2, maxPacketsPerCts: 0xFF, dataPgn: intruderPgn);
        peerBus.Transmit(CanFrame.Classic((int)rts1Id, BuildFrame(rts2), isExtendedFrame: true));

        // --- 4. Assert receiver refuses the intruder with SessionAlreadyOpen tagged with the
        //         intruder's PGN, without disturbing the active session ---
        var abort = await WaitForCmFrameAsync(
            d => d.Length >= 8 && d[0] == J1939TpFrames.ControlAbort
                 && J1939TpFrames.ReadDataPgn(d) == intruderPgn,
            ShortTimeout);
        abort[1].Should().Be(1, "J1939-21 table 7: already in a session (#33)");

        // Receiver must not have started tearing down / re-CTSing the active session.
        lock (observed)
        {
            cmFrames.Should().NotContain(t =>
                t.data[0] == J1939TpFrames.ControlAbort && J1939TpFrames.ReadDataPgn(t.data) == activePgn,
                "the active session must remain untouched by the rejected intruder");
        }

        // --- 5. Send the two TP.DT frames for the *active* session and verify they route
        //         correctly (and not to the just-rejected intruder). ---
        var dtId = J1939Id.ComposePgn(priority: 7, pgn: J1939Pgn.TpDt, sourceAddress: peerSa, destinationAddress: receiverSa);
        var dt1 = J1939TpFrames.BuildDt(sn: 1, pdu: payload, offset: 0);
        var dt2 = J1939TpFrames.BuildDt(sn: 2, pdu: payload, offset: 7);
        peerBus.Transmit(CanFrame.Classic((int)dtId, BuildFrame(dt1), isExtendedFrame: true));
        peerBus.Transmit(CanFrame.Classic((int)dtId, BuildFrame(dt2), isExtendedFrame: true));

        // --- 6. Receiver must produce an EndOfMsgAck for the *active* PGN and hand up the PDU ---
        var eom = await WaitForCmFrameAsync(
            d => d.Length >= 8 && d[0] == J1939TpFrames.ControlEomAck
                 && J1939TpFrames.ReadDataPgn(d) == activePgn,
            ShortTimeout);
        (eom[1] | (eom[2] << 8)).Should().Be(14);
        eom[3].Should().Be(2);

        var datagram = await receiver.ReceiveAsync().AsTaskWithTimeout(ShortTimeout);
        datagram.Kind.Should().Be(J1939TpKind.Cm);
        datagram.SourceAddress.Should().Be(peerSa);
        datagram.DestinationAddress.Should().Be(receiverSa);
        datagram.Pgn.Should().Be(activePgn);
        datagram.Payload.Should().Equal(payload);
    }

    // Companion coverage for the DT-routing invariant: two peers sending concurrent CM sessions
    // for *different* PGNs each get their DTs routed to *their own* session, even though the
    // second peer's PGN differs from the first peer's PGN. This directly exercises the
    // (SA, kind)-keyed rxSessions map (pre-fix, the DT handler picked the first (SA,*) match --
    // which happened to work by accident when only one peer is active, but broke reassembly
    // for concurrent transfers).
    [Fact]
    public async Task TwoPeers_ConcurrentCm_DifferentPgns_EachReassembledByPeerSa()
    {
        var session = NewSession();
        using var receiverBus = Open(session, 0);
        using var peerABus = Open(session, 1);
        using var peerBBus = Open(session, 2);

        const byte receiverSa = 0x22;
        const byte peerASa = 0x11;
        const byte peerBSa = 0x33;
        const uint pgnA = 0xFE10u;
        const uint pgnB = 0xFE20u;

        var payloadA = RandomPayload(14, seed: 1);
        var payloadB = RandomPayload(14, seed: 2);

        using var receiver = J1939TpFactory.Open(receiverBus, sourceAddress: receiverSa);

        // Interleave DTs from the two peers to force the (SA, kind) routing to demux correctly.
        async Task DriveAsync(ICanBus bus, byte peerSa, uint pgn, byte[] pdu)
        {
            var rts = J1939TpFrames.BuildRts(pdu.Length, J1939TpFrames.TotalPackets(pdu.Length), 0xFF, pgn);
            var cmId = J1939Id.ComposePgn(7, J1939Pgn.TpCm, peerSa, receiverSa);
            var dtId = J1939Id.ComposePgn(7, J1939Pgn.TpDt, peerSa, receiverSa);
            bus.Transmit(CanFrame.Classic((int)cmId, rts, isExtendedFrame: true));
            // Small gap so both RTSs land before either DT stream begins.
            await Task.Delay(20).ConfigureAwait(false);
            for (byte sn = 1; sn <= J1939TpFrames.TotalPackets(pdu.Length); sn++)
            {
                var dt = J1939TpFrames.BuildDt(sn, pdu, (sn - 1) * J1939TpFrames.DtDataBytes);
                bus.Transmit(CanFrame.Classic((int)dtId, dt, isExtendedFrame: true));
                await Task.Delay(5).ConfigureAwait(false);
            }
        }

        var driveA = DriveAsync(peerABus, peerASa, pgnA, payloadA);
        var driveB = DriveAsync(peerBBus, peerBSa, pgnB, payloadB);
        await Task.WhenAll(driveA, driveB).WithTimeout(ShortTimeout);

        var received = await CollectAsync(receiver, count: 2, ShortTimeout);
        received.Should().HaveCount(2);
        var fromA = received.Single(d => d.SourceAddress == peerASa);
        var fromB = received.Single(d => d.SourceAddress == peerBSa);
        fromA.Pgn.Should().Be(pgnA);
        fromA.Payload.Should().Equal(payloadA);
        fromB.Pgn.Should().Be(pgnB);
        fromB.Payload.Should().Equal(payloadB);
    }

    // Bugbot 3596183535: BAM announce TX rejection must fail SendBamAsync (not only raise
    // BackgroundExceptionOccurred) and must not proceed to TP.DT after Th.
    [Fact]
    public async Task Bam_AnnounceTxRejected_FailsSendAndDoesNotEmitDt()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);

        using var inner = new CanBusService(busA);
        using var rejecting = new RejectTpCmBusService(inner);
        var opts = new J1939TpOptions().With(bamPacketSpacing: TimeSpan.FromMilliseconds(5));
        using var sender = J1939TpFactory.Open(rejecting, sourceAddress: 0x51, options: opts, leaveOpen: true);

        var dtSeen = 0;
        busB.FrameObserved += (_, e) =>
        {
            if (!e.CanFrame.IsExtendedFrame) return;
            var fields = J1939Id.Decompose((uint)e.CanFrame.ID);
            if (fields.SourceAddress == 0x51 && J1939Pgn.IsTransportDt(fields.Pgn))
                Interlocked.Increment(ref dtSeen);
        };

        var bgSeen = 0;
        sender.BackgroundExceptionOccurred += (_, _) => Interlocked.Increment(ref bgSeen);

        Func<Task> act = async () => await sender.SendBamAsync(0xFE51, RandomPayload(20, seed: 51))
            .WithTimeout(ShortTimeout);
        await act.Should().ThrowAsync<J1939TpSendRejectedException>();

        // Give Th a chance to fire if DT were incorrectly scheduled after a rejected BAM.
        await Task.Delay(80);
        Volatile.Read(ref dtSeen).Should().Be(0, "rejected BAM announce must not schedule TP.DT");
        Volatile.Read(ref bgSeen).Should().Be(0, "CM TX failure must fail the send TCS, not only BackgroundExceptionOccurred");
    }

    // Bugbot 3596025915: canceling before BeginTxOnLoop runs must not emit TP.CM/TP.DT.
    [Fact]
    public async Task SendCm_CanceledBeforeStart_DoesNotTransmit()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);

        using var sender = J1939TpFactory.Open(busA, sourceAddress: 0x61);
        using var _ = J1939TpFactory.Open(busB, sourceAddress: 0x62);

        var seen = 0;
        busB.FrameObserved += (_, e) =>
        {
            if (!e.CanFrame.IsExtendedFrame) return;
            var fields = J1939Id.Decompose((uint)e.CanFrame.ID);
            if (fields.SourceAddress == 0x61 &&
                (J1939Pgn.IsTransportCm(fields.Pgn) || J1939Pgn.IsTransportDt(fields.Pgn)))
                Interlocked.Increment(ref seen);
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = async () => await sender.SendCmAsync(0xFE61, destinationAddress: 0x62,
            RandomPayload(50, seed: 61), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        // Give the actor a beat to drain any incorrectly queued BeginTx work.
        await Task.Delay(100);
        Volatile.Read(ref seen).Should().Be(0, "canceled send must not emit TP.CM/TP.DT");
    }

    // Bugbot 3596025922: canceling an in-flight TP.CM after RTS must send Connection Abort.
    [Fact]
    public async Task SendCm_CancelInFlight_SendsConnectionAbort()
    {
        var session = NewSession();
        using var senderBus = Open(session, 0);
        using var peerBus = Open(session, 1);

        const byte senderSa = 0x71;
        const byte peerSa = 0x72;
        const uint pgn = 0xFE71u;

        using var sender = J1939TpFactory.Open(senderBus, sourceAddress: senderSa);

        var rtsSeen = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var abortSeen = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        peerBus.FrameObserved += (_, e) =>
        {
            if (!e.CanFrame.IsExtendedFrame) return;
            var fields = J1939Id.Decompose((uint)e.CanFrame.ID);
            if (fields.SourceAddress != senderSa || !J1939Pgn.IsTransportCm(fields.Pgn)) return;
            var data = e.CanFrame.Data.ToArray();
            if (data.Length < 8 || J1939TpFrames.ReadDataPgn(data) != pgn) return;
            if (data[0] == J1939TpFrames.ControlRts) rtsSeen.TrySetResult(data);
            if (data[0] == J1939TpFrames.ControlAbort) abortSeen.TrySetResult(data);
        };

        // Peer never replies with CTS, so the session stays open after RTS until we cancel.
        using var cts = new CancellationTokenSource();
        var send = sender.SendCmAsync(pgn, destinationAddress: peerSa, RandomPayload(50, seed: 71), cts.Token);

        // Cancel only once the RTS is actually on the wire, i.e. the actor has registered the TX
        // session there is something to abort. Waiting for the frame instead of 50 ms removes
        // both failure modes of the delay: cancelling too early on a loaded machine (nothing
        // registered yet, so no Connection Abort is due and the test fails for the wrong reason),
        // and spending 50 ms per run when the RTS is out in microseconds.
        await rtsSeen.Task.AsTaskWithTimeout(ShortTimeout);
        cts.Cancel();

        Func<Task> act = async () => await send.WithTimeout(ShortTimeout);
        await act.Should().ThrowAsync<OperationCanceledException>();

        var abort = await abortSeen.Task.AsTaskWithTimeout(ShortTimeout);
        abort[1].Should().Be((byte)J1939TpAbortReason.NoResourcesAvailable);
    }

    // Bugbot 3596025929: MaxPacketsPerCts=0 must fail fast, not crash later in BuildCts.
    [Fact]
    public void Open_MaxPacketsPerCtsZero_Throws()
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        var opts = new J1939TpOptions { MaxPacketsPerCts = 0 };

        Action act = () => J1939TpFactory.Open(bus, sourceAddress: 0x81, options: opts);
        act.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be(nameof(J1939TpOptions.MaxPacketsPerCts));
    }

    [Fact]
    public void Options_With_MaxPacketsPerCtsZero_Throws()
    {
        Action act = () => new J1939TpOptions().With(maxPacketsPerCts: 0);
        act.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be("maxPacketsPerCts");
    }

    // Bugbot 3596025934 / 3596396508: T2 (CTS → first DT) must abort on the wire, raise
    // BackgroundExceptionOccurred, and fault a blocked ReceiveAsync (IsoTp AbortRx pattern).
    [Fact]
    public async Task Cm_Receiver_T2Timeout_AbortsWhenNoDtAfterCts()
    {
        var session = NewSession();
        using var receiverBus = Open(session, 0);
        using var peerBus = Open(session, 1);

        const byte receiverSa = 0x82;
        const byte peerSa = 0x83;
        const uint pgn = 0xFE82u;

        var opts = new J1939TpOptions().With(
            t2: TimeSpan.FromMilliseconds(80),
            t1: TimeSpan.FromSeconds(5)); // T1 must not fire first

        using var receiver = J1939TpFactory.Open(receiverBus, sourceAddress: receiverSa, options: opts);

        var abortSeen = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bgAbort = new TaskCompletionSource<J1939TpAbortException>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.BackgroundExceptionOccurred += (_, ex) =>
        {
            if (ex is J1939TpAbortException abort) bgAbort.TrySetResult(abort);
        };
        peerBus.FrameObserved += (_, e) =>
        {
            if (!e.CanFrame.IsExtendedFrame) return;
            var fields = J1939Id.Decompose((uint)e.CanFrame.ID);
            if (fields.SourceAddress != receiverSa || !J1939Pgn.IsTransportCm(fields.Pgn)) return;
            var data = e.CanFrame.Data.ToArray();
            if (data.Length >= 8 && data[0] == J1939TpFrames.ControlAbort
                && J1939TpFrames.ReadDataPgn(data) == pgn)
                abortSeen.TrySetResult(data);
        };

        var recvTask = receiver.ReceiveAsync().AsTaskWithTimeout(ShortTimeout);

        var rts = J1939TpFrames.BuildRts(totalBytes: 14, totalPackets: 2, maxPacketsPerCts: 0xFF, dataPgn: pgn);
        var rtsId = J1939Id.ComposePgn(7, J1939Pgn.TpCm, peerSa, receiverSa);
        peerBus.Transmit(CanFrame.Classic((int)rtsId, rts, isExtendedFrame: true));
        // Do not send any TP.DT — T2 must expire and abort.

        var abortFrame = await abortSeen.Task.AsTaskWithTimeout(ShortTimeout);
        abortFrame[1].Should().Be((byte)J1939TpAbortReason.Timeout);

        Func<Task> act = () => recvTask;
        var recvEx = (await act.Should().ThrowAsync<J1939TpAbortException>()).Which;
        recvEx.Reason.Should().Be(J1939TpAbortReason.Timeout);
        recvEx.Message.Should().Contain("T2");

        var ex = await bgAbort.Task.AsTaskWithTimeout(ShortTimeout);
        ex.Reason.Should().Be(J1939TpAbortReason.Timeout);
        ex.Message.Should().Contain("T2");
    }

    // Bugbot 3596396508: mismatched TP.DT SN must AbortRx — fault blocked ReceiveAsync and raise
    // BackgroundExceptionOccurred (wire Abort for CM). Channel stays usable afterward.
    [Fact]
    public async Task Cm_Receiver_BadDtSequence_FaultsReceiveAsync()
    {
        var session = NewSession();
        using var receiverBus = Open(session, 0);
        using var peerBus = Open(session, 1);

        const byte receiverSa = 0x84;
        const byte peerSa = 0x85;
        const uint pgn = 0xFE84u;
        var payload = RandomPayload(14, seed: 99);

        using var receiver = J1939TpFactory.Open(receiverBus, sourceAddress: receiverSa);

        var ctsSeen = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var abortSeen = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bgAbort = new TaskCompletionSource<J1939TpAbortException>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.BackgroundExceptionOccurred += (_, ex) =>
        {
            if (ex is J1939TpAbortException abort) bgAbort.TrySetResult(abort);
        };
        peerBus.FrameObserved += (_, e) =>
        {
            if (!e.CanFrame.IsExtendedFrame) return;
            var fields = J1939Id.Decompose((uint)e.CanFrame.ID);
            if (fields.SourceAddress != receiverSa || !J1939Pgn.IsTransportCm(fields.Pgn)) return;
            var data = e.CanFrame.Data.ToArray();
            if (data.Length < 8 || J1939TpFrames.ReadDataPgn(data) != pgn) return;
            if (data[0] == J1939TpFrames.ControlCts) ctsSeen.TrySetResult(data);
            else if (data[0] == J1939TpFrames.ControlAbort) abortSeen.TrySetResult(data);
        };

        var recvTask = receiver.ReceiveAsync().AsTaskWithTimeout(ShortTimeout);

        var rts = J1939TpFrames.BuildRts(totalBytes: 14, totalPackets: 2, maxPacketsPerCts: 0xFF, dataPgn: pgn);
        var rtsId = J1939Id.ComposePgn(7, J1939Pgn.TpCm, peerSa, receiverSa);
        peerBus.Transmit(CanFrame.Classic((int)rtsId, rts, isExtendedFrame: true));

        await ctsSeen.Task.AsTaskWithTimeout(ShortTimeout);

        // Inject SN=2 while SN=1 was expected.
        var dtId = J1939Id.ComposePgn(7, J1939Pgn.TpDt, peerSa, receiverSa);
        var badDt = J1939TpFrames.BuildDt(sn: 2, pdu: payload, offset: 7);
        peerBus.Transmit(CanFrame.Classic((int)dtId, badDt, isExtendedFrame: true));

        var abortFrame = await abortSeen.Task.AsTaskWithTimeout(ShortTimeout);
        abortFrame[1].Should().Be(7, "J1939-21 table 7: bad sequence number (#33)");

        Func<Task> act = () => recvTask;
        var recvEx = (await act.Should().ThrowAsync<J1939TpAbortException>()).Which;
        recvEx.Reason.Should().Be(J1939TpAbortReason.BadSequenceNumber);
        recvEx.Pgn.Should().Be(pgn);
        recvEx.Message.Should().Contain("unexpected TP.DT sequence number");

        var bgEx = await bgAbort.Task.AsTaskWithTimeout(ShortTimeout);
        bgEx.Reason.Should().Be(J1939TpAbortReason.BadSequenceNumber);

        // Channel remains usable for a subsequent BAM after the abort (fault consumed once).
        var opts = new J1939TpOptions().With(bamPacketSpacing: TimeSpan.FromMilliseconds(5));
        using var senderBus = Open(session, 2);
        using var sender = J1939TpFactory.Open(senderBus, sourceAddress: 0x11, options: opts);
        var okPayload = RandomPayload(14, seed: 123);
        var recv2 = receiver.ReceiveAsync().AsTaskWithTimeout(ShortTimeout);
        await sender.SendBamAsync(0xFECAu, okPayload).WithTimeout(ShortTimeout);
        var datagram = await recv2;
        datagram.Kind.Should().Be(J1939TpKind.Bam);
        datagram.Payload.Should().Equal(okPayload);
    }

    // Bugbot 3596475712: ReadDataPgn must mask reserved bits in TP.CM byte 7 (18-bit PGN).
    // Without the mask, BuildCts throws after the RX session is registered and ArmTr never runs,
    // leaving a timerless orphan that blocks further CM from that source (§5.10.3).
    [Fact]
    public void ReadDataPgn_MasksReservedBitsInByte7()
    {
        const uint pgn = 0x1F345u; // fits in 18 bits
        var rts = J1939TpFrames.BuildRts(totalBytes: 14, totalPackets: 2, maxPacketsPerCts: 0xFF, dataPgn: pgn);
        rts[7] |= 0xFC; // set reserved upper 6 bits (would yield > MaxValue if unmasked)

        J1939TpFrames.ReadDataPgn(rts).Should().Be(pgn);
        Action act = () => J1939TpFrames.BuildCts(numPackets: 1, nextPacketSn: 1, dataPgn: J1939TpFrames.ReadDataPgn(rts));
        act.Should().NotThrow();
    }

    [Fact]
    public async Task Cm_Receiver_RtsWithReservedPgnBits_RepliesCtsAndArmsT2()
    {
        var session = NewSession();
        using var receiverBus = Open(session, 0);
        using var peerBus = Open(session, 1);

        const byte receiverSa = 0x88;
        const byte peerSa = 0x89;
        const uint pgn = 0xFE88u;

        var opts = new J1939TpOptions().With(
            t2: TimeSpan.FromMilliseconds(80),
            t1: TimeSpan.FromSeconds(5));

        using var receiver = J1939TpFactory.Open(receiverBus, sourceAddress: receiverSa, options: opts);

        var ctsSeen = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var abortSeen = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        peerBus.FrameObserved += (_, e) =>
        {
            if (!e.CanFrame.IsExtendedFrame) return;
            var fields = J1939Id.Decompose((uint)e.CanFrame.ID);
            if (fields.SourceAddress != receiverSa || !J1939Pgn.IsTransportCm(fields.Pgn)) return;
            var data = e.CanFrame.Data.ToArray();
            if (data.Length < 8 || J1939TpFrames.ReadDataPgn(data) != pgn) return;
            if (data[0] == J1939TpFrames.ControlCts) ctsSeen.TrySetResult(data);
            else if (data[0] == J1939TpFrames.ControlAbort) abortSeen.TrySetResult(data);
        };

        var rts = J1939TpFrames.BuildRts(totalBytes: 14, totalPackets: 2, maxPacketsPerCts: 0xFF, dataPgn: pgn);
        rts[7] |= 0xFC; // reserved bits set — must not orphan the CM RX session
        var rtsId = J1939Id.ComposePgn(7, J1939Pgn.TpCm, peerSa, receiverSa);
        peerBus.Transmit(CanFrame.Classic((int)rtsId, rts, isExtendedFrame: true));

        var cts = await ctsSeen.Task.AsTaskWithTimeout(ShortTimeout);
        J1939TpFrames.ReadDataPgn(cts).Should().Be(pgn);

        // T2 must be armed: with no DT, the receiver aborts for timeout (not a timerless orphan).
        var abortFrame = await abortSeen.Task.AsTaskWithTimeout(ShortTimeout);
        abortFrame[1].Should().Be((byte)J1939TpAbortReason.Timeout);
    }

    // Bugbot 3596396508 (BAM path): bad SN must fault a blocked ReceiveAsync (not only raise
    // BackgroundExceptionOccurred). Cancel() disposes T1, so inbox fault is the unblock path.
    [Fact]
    public async Task Bam_Receiver_BadDtSequence_FaultsReceiveAsync()
    {
        var session = NewSession();
        using var receiverBus = Open(session, 0);
        using var peerBus = Open(session, 1);

        const byte receiverSa = 0x86;
        const byte peerSa = 0x87;
        const uint pgn = 0xFECBu;
        var payload = RandomPayload(14, seed: 100);

        // Long T1 so a hang would outlive the test timeout if we failed to notify.
        var opts = new J1939TpOptions().With(t1: TimeSpan.FromSeconds(30));
        using var receiver = J1939TpFactory.Open(receiverBus, sourceAddress: receiverSa, options: opts);

        var bgAbort = new TaskCompletionSource<J1939TpAbortException>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.BackgroundExceptionOccurred += (_, ex) =>
        {
            if (ex is J1939TpAbortException abort) bgAbort.TrySetResult(abort);
        };

        var recvTask = receiver.ReceiveAsync().AsTaskWithTimeout(ShortTimeout);

        var bam = J1939TpFrames.BuildBam(totalBytes: 14, totalPackets: 2, dataPgn: pgn);
        var bamId = J1939Id.ComposePgn(7, J1939Pgn.TpCm, peerSa, J1939Pgn.GlobalAddress);
        peerBus.Transmit(CanFrame.Classic((int)bamId, bam, isExtendedFrame: true));

        // Virtual hub delivers synchronously; BAM is armed before we inject the bad DT.
        var dtId = J1939Id.ComposePgn(7, J1939Pgn.TpDt, peerSa, J1939Pgn.GlobalAddress);
        var badDt = J1939TpFrames.BuildDt(sn: 2, pdu: payload, offset: 7);
        peerBus.Transmit(CanFrame.Classic((int)dtId, badDt, isExtendedFrame: true));

        Func<Task> act = () => recvTask;
        var recvEx = (await act.Should().ThrowAsync<J1939TpAbortException>()).Which;
        recvEx.Reason.Should().Be(J1939TpAbortReason.BadSequenceNumber);
        recvEx.Pgn.Should().Be(pgn);
        recvEx.Message.Should().Contain("Bam");
        recvEx.Message.Should().Contain("unexpected TP.DT sequence number");

        var bgEx = await bgAbort.Task.AsTaskWithTimeout(ShortTimeout);
        bgEx.Reason.Should().Be(J1939TpAbortReason.BadSequenceNumber);
    }

    // Bugbot 3596617262: peer Connection Abort during outbound TP.CM must fail SendCmAsync
    // immediately — not leave the send task open until T2/T3/T4 expires.
    [Fact]
    public async Task Cm_Sender_PeerAbort_FailsSendImmediately()
    {
        var session = NewSession();
        using var senderBus = Open(session, 0);
        using var peerBus = Open(session, 1);

        const byte senderSa = 0x8D;
        const byte peerSa = 0x8E;
        const uint pgn = 0xFE8Du;
        var payload = RandomPayload(50, seed: 0x8D);

        // Long T3 so a missed abort would hang well past ShortTimeout.
        var opts = new J1939TpOptions().With(t3: TimeSpan.FromSeconds(30));
        using var sender = J1939TpFactory.Open(senderBus, sourceAddress: senderSa, options: opts);

        var rtsSeen = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        peerBus.FrameObserved += (_, e) =>
        {
            if (!e.CanFrame.IsExtendedFrame) return;
            var fields = J1939Id.Decompose((uint)e.CanFrame.ID);
            if (fields.SourceAddress != senderSa || !J1939Pgn.IsTransportCm(fields.Pgn)) return;
            var data = e.CanFrame.Data.ToArray();
            if (data.Length >= 8 && data[0] == J1939TpFrames.ControlRts
                && J1939TpFrames.ReadDataPgn(data) == pgn)
                rtsSeen.TrySetResult(data);
        };

        var sendTask = sender.SendCmAsync(pgn, destinationAddress: peerSa, payload);
        await rtsSeen.Task.AsTaskWithTimeout(ShortTimeout);

        var abort = J1939TpFrames.BuildAbort(J1939TpAbortReason.NoResourcesAvailable, pgn);
        var cmId = J1939Id.ComposePgn(7, J1939Pgn.TpCm, peerSa, senderSa);
        peerBus.Transmit(CanFrame.Classic((int)cmId, abort, isExtendedFrame: true));

        Func<Task> act = () => sendTask.WithTimeout(ShortTimeout);
        var ex = (await act.Should().ThrowAsync<J1939TpAbortException>()).Which;
        ex.Reason.Should().Be(J1939TpAbortReason.NoResourcesAvailable);
        ex.Pgn.Should().Be(pgn);
    }

    // Bugbot 3596489078: a stray/early EndOfMsgAck while still waiting for CTS must abort
    // SendCmAsync — not complete it successfully before any DT has been sent.
    [Fact]
    public async Task Cm_Sender_PrematureEom_FailsSend()
    {
        var session = NewSession();
        using var senderBus = Open(session, 0);
        using var peerBus = Open(session, 1);

        const byte senderSa = 0x91;
        const byte peerSa = 0x92;
        const uint pgn = 0xFE91u;
        var payload = RandomPayload(14, seed: 91);

        using var sender = J1939TpFactory.Open(senderBus, sourceAddress: senderSa);

        var rtsSeen = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var abortSeen = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        peerBus.FrameObserved += (_, e) =>
        {
            if (!e.CanFrame.IsExtendedFrame) return;
            var fields = J1939Id.Decompose((uint)e.CanFrame.ID);
            if (fields.SourceAddress != senderSa || !J1939Pgn.IsTransportCm(fields.Pgn)) return;
            var data = e.CanFrame.Data.ToArray();
            if (data.Length < 8 || J1939TpFrames.ReadDataPgn(data) != pgn) return;
            if (data[0] == J1939TpFrames.ControlRts) rtsSeen.TrySetResult(data);
            else if (data[0] == J1939TpFrames.ControlAbort) abortSeen.TrySetResult(data);
        };

        var sendTask = sender.SendCmAsync(pgn, destinationAddress: peerSa, payload);

        await rtsSeen.Task.AsTaskWithTimeout(ShortTimeout);

        // Inject EOM before any CTS — sender is still in WaitCts.
        var eom = J1939TpFrames.BuildEomAck(payload.Length, J1939TpFrames.TotalPackets(payload.Length), pgn);
        var cmId = J1939Id.ComposePgn(7, J1939Pgn.TpCm, peerSa, senderSa);
        peerBus.Transmit(CanFrame.Classic((int)cmId, eom, isExtendedFrame: true));

        Func<Task> act = () => sendTask.WithTimeout(ShortTimeout);
        var ex = (await act.Should().ThrowAsync<J1939TpAbortException>()).Which;
        ex.Reason.Should().Be(J1939TpAbortReason.BadSequenceNumber);
        ex.Message.Should().Contain("WaitEom");

        var abortFrame = await abortSeen.Task.AsTaskWithTimeout(ShortTimeout);
        abortFrame[1].Should().Be(7, "an EndOfMsgAck out of turn is a sequence the software cannot recover from (#33)");
    }

    // Bugbot 3596489078: EOM totals that disagree with the session must fail SendCmAsync
    // (not complete successfully with only a BackgroundExceptionOccurred).
    [Fact]
    public async Task Cm_Sender_EomSizeMismatch_FailsSend()
    {
        var session = NewSession();
        using var senderBus = Open(session, 0);
        using var peerBus = Open(session, 1);

        const byte senderSa = 0x93;
        const byte peerSa = 0x94;
        const uint pgn = 0xFE93u;
        var payload = RandomPayload(14, seed: 93); // 2 packets

        using var sender = J1939TpFactory.Open(senderBus, sourceAddress: senderSa);

        var rtsSeen = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lastDtSeen = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);
        peerBus.FrameObserved += (_, e) =>
        {
            if (!e.CanFrame.IsExtendedFrame) return;
            var fields = J1939Id.Decompose((uint)e.CanFrame.ID);
            if (fields.SourceAddress != senderSa) return;
            var data = e.CanFrame.Data.ToArray();
            if (J1939Pgn.IsTransportCm(fields.Pgn) && data.Length >= 8
                && data[0] == J1939TpFrames.ControlRts && J1939TpFrames.ReadDataPgn(data) == pgn)
                rtsSeen.TrySetResult(data);
            else if (J1939Pgn.IsTransportDt(fields.Pgn) && data.Length >= 1 && data[0] == 2)
                lastDtSeen.TrySetResult(data[0]);
        };

        var sendTask = sender.SendCmAsync(pgn, destinationAddress: peerSa, payload);

        await rtsSeen.Task.AsTaskWithTimeout(ShortTimeout);

        // Grant both packets so the sender reaches WaitEom after DT SN=2 is confirmed.
        var cts = J1939TpFrames.BuildCts(numPackets: 2, nextPacketSn: 1, dataPgn: pgn);
        var cmId = J1939Id.ComposePgn(7, J1939Pgn.TpCm, peerSa, senderSa);
        peerBus.Transmit(CanFrame.Classic((int)cmId, cts, isExtendedFrame: true));

        await lastDtSeen.Task.AsTaskWithTimeout(ShortTimeout);
        // Small settle so OnCmDtConfirmed arms WaitEom before we inject the bad EOM.
        await Task.Delay(20);

        var badEom = J1939TpFrames.BuildEomAck(payload.Length, J1939TpFrames.TotalPackets(payload.Length), pgn);
        badEom[1] = (byte)(payload.Length + 1); // mismatch totals vs session
        peerBus.Transmit(CanFrame.Classic((int)cmId, badEom, isExtendedFrame: true));

        Func<Task> act = () => sendTask.WithTimeout(ShortTimeout);
        var ex = (await act.Should().ThrowAsync<J1939TpAbortException>()).Which;
        ex.Reason.Should().Be(J1939TpAbortReason.BadSequenceNumber);
        ex.Message.Should().Contain("EOM ack size mismatch");
    }

    // Bugbot 3596489082: an invalid BAM must not cancel an in-progress BAM from the same source
    // and leave ReceiveAsync hung with no session and no inbox fault.
    [Fact]
    public async Task Bam_Receiver_InvalidBamDoesNotSupersedeInProgress()
    {
        var session = NewSession();
        using var receiverBus = Open(session, 0);
        using var peerBus = Open(session, 1);

        const byte receiverSa = 0x95;
        const byte peerSa = 0x96;
        const uint pgn = 0xFECDu;
        var payload = RandomPayload(14, seed: 95);

        var opts = new J1939TpOptions().With(t1: TimeSpan.FromSeconds(5));
        using var receiver = J1939TpFactory.Open(receiverBus, sourceAddress: receiverSa, options: opts);

        var recvTask = receiver.ReceiveAsync().AsTaskWithTimeout(ShortTimeout);

        var bamId = J1939Id.ComposePgn(7, J1939Pgn.TpCm, peerSa, J1939Pgn.GlobalAddress);
        var goodBam = J1939TpFrames.BuildBam(totalBytes: 14, totalPackets: 2, dataPgn: pgn);
        peerBus.Transmit(CanFrame.Classic((int)bamId, goodBam, isExtendedFrame: true));

        // Malformed BAM (bytes/packets disagree) — must not tear down the good session.
        var badBam = J1939TpFrames.BuildBam(totalBytes: 14, totalPackets: 2, dataPgn: pgn);
        badBam[3] = 3; // claim 3 packets for 14 bytes
        peerBus.Transmit(CanFrame.Classic((int)bamId, badBam, isExtendedFrame: true));

        var dtId = J1939Id.ComposePgn(7, J1939Pgn.TpDt, peerSa, J1939Pgn.GlobalAddress);
        peerBus.Transmit(CanFrame.Classic((int)dtId,
            J1939TpFrames.BuildDt(sn: 1, pdu: payload, offset: 0), isExtendedFrame: true));
        peerBus.Transmit(CanFrame.Classic((int)dtId,
            J1939TpFrames.BuildDt(sn: 2, pdu: payload, offset: 7), isExtendedFrame: true));

        var datagram = await recvTask;
        datagram.Kind.Should().Be(J1939TpKind.Bam);
        datagram.Pgn.Should().Be(pgn);
        datagram.Payload.Should().Equal(payload);
    }

    private static async Task<List<J1939TpDatagram>> CollectAsync(IJ1939TpChannel channel, int count, TimeSpan timeout)
    {
        var list = new List<J1939TpDatagram>();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await foreach (var d in channel.ReceiveAllAsync(cts.Token))
            {
                list.Add(d);
                if (list.Count >= count) break;
            }
        }
        catch (OperationCanceledException)
        {
            // timeout -> return what we have
        }
        return list;
    }

    // FR-TP-032 (T1, BAM): receiver gets the BAM announce and the first TP.DT, then the peer
    // goes silent — the T1 DT-gap timer must expire the session and fault the pending
    // ReceiveAsync (AbortRx). BAM has no wire abort (connection-less), so the fault and the
    // BackgroundExceptionOccurred signal are the observable outcomes.
    [Fact]
    public async Task Bam_Receiver_T1Timeout_FaultsReceiveAsyncWhenDtStops()
    {
        var session = NewSession();
        using var receiverBus = Open(session, 0);
        using var peerBus = Open(session, 1);

        const byte receiverSa = 0x90;
        const byte peerSa = 0x91;
        const uint pgn = 0xFE90u;

        var opts = new J1939TpOptions().With(t1: TimeSpan.FromMilliseconds(120));
        using var receiver = J1939TpFactory.Open(receiverBus, sourceAddress: receiverSa, options: opts);

        var bgAbort = new TaskCompletionSource<J1939TpAbortException>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.BackgroundExceptionOccurred += (_, ex) =>
        {
            if (ex is J1939TpAbortException abort) bgAbort.TrySetResult(abort);
        };

        var recvTask = receiver.ReceiveAsync().AsTaskWithTimeout(ShortTimeout);

        var pdu = RandomPayload(21, seed: 11);
        peerBus.Transmit(CanFrame.Classic(
            (int)J1939Id.ComposePgn(7, J1939Pgn.TpCm, peerSa, J1939TpFrames.GlobalDestinationAddress),
            J1939TpFrames.BuildBam(pdu.Length, totalPackets: 3, dataPgn: pgn),
            isExtendedFrame: true));
        peerBus.Transmit(CanFrame.Classic(
            (int)J1939Id.ComposePgn(7, J1939Pgn.TpDt, peerSa, J1939TpFrames.GlobalDestinationAddress),
            J1939TpFrames.BuildDt(1, pdu, 0),
            isExtendedFrame: true));
        // No further TP.DT — T1 (DT gap) must fire.

        Func<Task> act = () => recvTask;
        var ex = (await act.Should().ThrowAsync<J1939TpAbortException>()).Which;
        ex.Reason.Should().Be(J1939TpAbortReason.Timeout);
        ex.Message.Should().Contain("T1");

        var bg = await bgAbort.Task.AsTaskWithTimeout(ShortTimeout);
        bg.Reason.Should().Be(J1939TpAbortReason.Timeout);
    }

    // FR-TP-032 (T1, CM): after the RTS/CTS handshake the peer delivers only the first DT of
    // the granted block, then goes silent — T1 must expire the session, fault ReceiveAsync and
    // emit a wire Connection Abort (CM is connection-oriented).
    [Fact]
    public async Task Cm_Receiver_T1Timeout_AbortsWhenDtStopsMidBlock()
    {
        var session = NewSession();
        using var receiverBus = Open(session, 0);
        using var peerBus = Open(session, 1);

        const byte receiverSa = 0x92;
        const byte peerSa = 0x93;
        const uint pgn = 0xFE92u;

        var opts = new J1939TpOptions().With(
            t1: TimeSpan.FromMilliseconds(120),
            t2: TimeSpan.FromSeconds(5)); // keep T2 out of the way so T1 is the timer under test
        using var receiver = J1939TpFactory.Open(receiverBus, sourceAddress: receiverSa, options: opts);

        var ctsSeen = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var abortSeen = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        peerBus.FrameObserved += (_, e) =>
        {
            if (!e.CanFrame.IsExtendedFrame) return;
            var fields = J1939Id.Decompose((uint)e.CanFrame.ID);
            if (fields.SourceAddress != receiverSa || !J1939Pgn.IsTransportCm(fields.Pgn)) return;
            var data = e.CanFrame.Data.ToArray();
            if (data.Length < 8 || J1939TpFrames.ReadDataPgn(data) != pgn) return;
            if (data[0] == J1939TpFrames.ControlCts) ctsSeen.TrySetResult(null);
            if (data[0] == J1939TpFrames.ControlAbort) abortSeen.TrySetResult(data);
        };

        var recvTask = receiver.ReceiveAsync().AsTaskWithTimeout(ShortTimeout);

        var pdu = RandomPayload(21, seed: 12);
        peerBus.Transmit(CanFrame.Classic(
            (int)J1939Id.ComposePgn(7, J1939Pgn.TpCm, peerSa, receiverSa),
            J1939TpFrames.BuildRts(pdu.Length, totalPackets: 3, maxPacketsPerCts: 0xFF, dataPgn: pgn),
            isExtendedFrame: true));

        // Deliver only the first DT of the granted block after the receiver's CTS arrived.
        await ctsSeen.Task.AsTaskWithTimeout(ShortTimeout);
        peerBus.Transmit(CanFrame.Classic(
            (int)J1939Id.ComposePgn(7, J1939Pgn.TpDt, peerSa, receiverSa),
            J1939TpFrames.BuildDt(1, pdu, 0),
            isExtendedFrame: true));
        // Block not complete and no further DT — T1 must fire.

        var abortFrame = await abortSeen.Task.AsTaskWithTimeout(ShortTimeout);
        abortFrame[1].Should().Be((byte)J1939TpAbortReason.Timeout);

        Func<Task> act = () => recvTask;
        var ex = (await act.Should().ThrowAsync<J1939TpAbortException>()).Which;
        ex.Reason.Should().Be(J1939TpAbortReason.Timeout);
        ex.Message.Should().Contain("T1");
    }

    // FR-TP-032 (T2): the peer grants a short block (CTS(2)) and then never sends the
    // follow-up CTS — the sender's post-block CTS gap timer T2 must abort the send.
    [Fact]
    public async Task Cm_Sender_T3Timeout_WhenFollowUpCtsMissing()
    {
        var session = NewSession();
        using var senderBus = Open(session, 0);
        using var peerBus = Open(session, 1);

        const byte senderSa = 0x01;
        const byte peerSa = 0x02;
        const uint pgn = 0xFF01u;

        // T3 is the originator's timer after the last packet of a block as well as after the
        // RTS (§5.10.2.4, #31); T2 is the receiver's and plays no part on this side.
        var opts = new J1939TpOptions().With(
            t2: TimeSpan.FromSeconds(5),
            t3: TimeSpan.FromMilliseconds(150),
            t4: TimeSpan.FromSeconds(5));
        using var sender = J1939TpFactory.Open(senderBus, sourceAddress: senderSa, options: opts);

        var rtsSeen = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        peerBus.FrameObserved += (_, e) =>
        {
            if (!e.CanFrame.IsExtendedFrame) return;
            var fields = J1939Id.Decompose((uint)e.CanFrame.ID);
            if (fields.SourceAddress != senderSa || !J1939Pgn.IsTransportCm(fields.Pgn)) return;
            var data = e.CanFrame.Data.Span;
            if (data.Length < 8 || data[0] != J1939TpFrames.ControlRts) return;
            if (J1939TpFrames.ReadDataPgn(data) != pgn) return;
            rtsSeen.TrySetResult(null);
        };

        var send = sender.SendCmAsync(pgn, destinationAddress: peerSa, RandomPayload(21, seed: 13));

        await rtsSeen.Task.AsTaskWithTimeout(ShortTimeout);
        // Grant only 2 of the 3 packets, then go silent: after the block drains the sender
        // waits for the follow-up CTS on T3.
        peerBus.Transmit(CanFrame.Classic(
            (int)J1939Id.ComposePgn(7, J1939Pgn.TpCm, peerSa, senderSa),
            J1939TpFrames.BuildCts(numPackets: 2, nextPacketSn: 1, dataPgn: pgn),
            isExtendedFrame: true));

        Func<Task> act = async () => await send.WithTimeout(ShortTimeout);
        var ex = (await act.Should().ThrowAsync<J1939TpAbortException>()).Which;
        ex.Reason.Should().Be(J1939TpAbortReason.Timeout);
        ex.Message.Should().Contain("T3");
    }

    // FR-TP-032 (T3 waiting for EndOfMsgAck): the peer grants the whole message but never
    // sends EndOfMsgAck after the last TP.DT — T3 must abort the send.
    [Fact]
    public async Task Cm_Sender_T3Timeout_WhenEomAckMissing()
    {
        var session = NewSession();
        using var senderBus = Open(session, 0);
        using var peerBus = Open(session, 1);

        const byte senderSa = 0x03;
        const byte peerSa = 0x04;
        const uint pgn = 0xFF03u;

        var opts = new J1939TpOptions().With(
            t2: TimeSpan.FromSeconds(5),
            t3: TimeSpan.FromMilliseconds(150),
            t4: TimeSpan.FromSeconds(5));
        using var sender = J1939TpFactory.Open(senderBus, sourceAddress: senderSa, options: opts);

        var rtsSeen = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        peerBus.FrameObserved += (_, e) =>
        {
            if (!e.CanFrame.IsExtendedFrame) return;
            var fields = J1939Id.Decompose((uint)e.CanFrame.ID);
            if (fields.SourceAddress != senderSa || !J1939Pgn.IsTransportCm(fields.Pgn)) return;
            var data = e.CanFrame.Data.Span;
            if (data.Length < 8 || data[0] != J1939TpFrames.ControlRts) return;
            if (J1939TpFrames.ReadDataPgn(data) != pgn) return;
            rtsSeen.TrySetResult(null);
        };

        var send = sender.SendCmAsync(pgn, destinationAddress: peerSa, RandomPayload(21, seed: 14));

        await rtsSeen.Task.AsTaskWithTimeout(ShortTimeout);
        // Grant all 3 packets; after the last DT the sender waits for EndOfMsgAck on T3.
        peerBus.Transmit(CanFrame.Classic(
            (int)J1939Id.ComposePgn(7, J1939Pgn.TpCm, peerSa, senderSa),
            J1939TpFrames.BuildCts(numPackets: 3, nextPacketSn: 1, dataPgn: pgn),
            isExtendedFrame: true));

        Func<Task> act = async () => await send.WithTimeout(ShortTimeout);
        var ex = (await act.Should().ThrowAsync<J1939TpAbortException>()).Which;
        ex.Reason.Should().Be(J1939TpAbortReason.Timeout);
        ex.Message.Should().Contain("EndOfMsgAck");
    }

    // FR-TP-032 (T4): the peer answers the RTS with CTS(0) ("hold connection open") and never
    // follows up — the originator-side hold timer T4 must abort the send. BuildCts rejects
    // numPackets=0 by design, so the hold frame is crafted manually here.
    [Fact]
    public async Task Cm_Sender_T4Timeout_WhenPeerHoldsWithCtsZero()
    {
        var session = NewSession();
        using var senderBus = Open(session, 0);
        using var peerBus = Open(session, 1);

        const byte senderSa = 0x05;
        const byte peerSa = 0x06;
        const uint pgn = 0xFF05u;

        var opts = new J1939TpOptions().With(
            t2: TimeSpan.FromSeconds(5),
            t3: TimeSpan.FromSeconds(5),
            t4: TimeSpan.FromMilliseconds(150));
        using var sender = J1939TpFactory.Open(senderBus, sourceAddress: senderSa, options: opts);

        var rtsSeen = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        peerBus.FrameObserved += (_, e) =>
        {
            if (!e.CanFrame.IsExtendedFrame) return;
            var fields = J1939Id.Decompose((uint)e.CanFrame.ID);
            if (fields.SourceAddress != senderSa || !J1939Pgn.IsTransportCm(fields.Pgn)) return;
            var data = e.CanFrame.Data.Span;
            if (data.Length < 8 || data[0] != J1939TpFrames.ControlRts) return;
            if (J1939TpFrames.ReadDataPgn(data) != pgn) return;
            rtsSeen.TrySetResult(null);
        };

        var send = sender.SendCmAsync(pgn, destinationAddress: peerSa, RandomPayload(21, seed: 15));

        await rtsSeen.Task.AsTaskWithTimeout(ShortTimeout);
        // CTS with numPackets=0 = "hold connection open" (J1939-21 §5.10.3.1), nextSn=1.
        var hold = new byte[8];
        hold[0] = J1939TpFrames.ControlCts;
        hold[1] = 0x00;
        hold[2] = 0x01;
        hold[3] = 0xFF;
        hold[4] = 0xFF;
        hold[5] = (byte)(pgn & 0xFF);
        hold[6] = (byte)((pgn >> 8) & 0xFF);
        hold[7] = (byte)((pgn >> 16) & 0xFF);
        peerBus.Transmit(CanFrame.Classic(
            (int)J1939Id.ComposePgn(7, J1939Pgn.TpCm, peerSa, senderSa), hold,
            isExtendedFrame: true));

        Func<Task> act = async () => await send.WithTimeout(ShortTimeout);
        var ex = (await act.Should().ThrowAsync<J1939TpAbortException>()).Which;
        ex.Reason.Should().Be(J1939TpAbortReason.Timeout);
        ex.Message.Should().Contain("T4");
    }

    // FR-TP-031: the receiver must cap its CTS grant at the originator's RTS-advertised
    // maximum (here 2), even though its own MaxPacketsPerCts (16) is larger — and keep the
    // cap on every subsequent block's CTS.
    [Fact]
    public async Task Cm_Receiver_CapsCtsGrant_AtPeerRtsMaximum()
    {
        var session = NewSession();
        using var receiverBus = Open(session, 0);
        using var peerBus = Open(session, 1);

        const byte receiverSa = 0x96;
        const byte peerSa = 0x97;
        const uint pgn = 0xFE96u;

        using var receiver = J1939TpFactory.Open(receiverBus, sourceAddress: receiverSa); // cap 16 by default

        var grants = new List<byte>();
        var firstGrantSeen = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondGrantSeen = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        peerBus.FrameObserved += (_, e) =>
        {
            if (!e.CanFrame.IsExtendedFrame) return;
            var fields = J1939Id.Decompose((uint)e.CanFrame.ID);
            if (fields.SourceAddress != receiverSa || !J1939Pgn.IsTransportCm(fields.Pgn)) return;
            var data = e.CanFrame.Data.Span;
            if (data.Length < 8 || data[0] != J1939TpFrames.ControlCts) return;
            if (J1939TpFrames.ReadDataPgn(data) != pgn) return;
            lock (grants)
            {
                grants.Add(data[1]);
                if (grants.Count >= 1) firstGrantSeen.TrySetResult(null);
                if (grants.Count >= 2) secondGrantSeen.TrySetResult(null);
            }
        };

        var pdu = RandomPayload(42, seed: 16); // 6 packets
        peerBus.Transmit(CanFrame.Classic(
            (int)J1939Id.ComposePgn(7, J1939Pgn.TpCm, peerSa, receiverSa),
            J1939TpFrames.BuildRts(pdu.Length, totalPackets: 6, maxPacketsPerCts: 2, dataPgn: pgn),
            isExtendedFrame: true));

        // Wait for the first (already capped) grant before feeding the first block's DTs —
        // deterministic, no fixed delay that could race the receiver's RTS processing.
        await firstGrantSeen.Task.AsTaskWithTimeout(ShortTimeout);
        peerBus.Transmit(CanFrame.Classic(
            (int)J1939Id.ComposePgn(7, J1939Pgn.TpDt, peerSa, receiverSa),
            J1939TpFrames.BuildDt(1, pdu, 0), isExtendedFrame: true));
        peerBus.Transmit(CanFrame.Classic(
            (int)J1939Id.ComposePgn(7, J1939Pgn.TpDt, peerSa, receiverSa),
            J1939TpFrames.BuildDt(2, pdu, 7), isExtendedFrame: true));

        await secondGrantSeen.Task.AsTaskWithTimeout(ShortTimeout);
        lock (grants)
        {
            grants.Should().HaveCountGreaterOrEqualTo(2);
            grants.Should().OnlyContain(g => g == 2,
                "every CTS grant must be capped at the originator's RTS-advertised maximum of 2");
        }
    }

    // -----------------------------------------------------------------------------------------
    // #30 -- TP.CM control frames carry a destination, and only a BAM's is the global address.
    // The reader admits frames addressed to us or to 0xFF; HandleRxTpCm decides which of the two
    // each control byte may carry. J1939-21 addresses RTS, CTS, EndOfMsgAck and Abort to one
    // node; an RTS to 0xFF would open a session on every node and each would answer (FR-TP-031).
    // -----------------------------------------------------------------------------------------
    [Fact]
    public async Task Rts_To_The_Global_Address_Does_Not_Open_A_Session()
    {
        var session = NewSession();
        using var receiverBus = Open(session, 0);
        using var peerBus = Open(session, 1);

        const byte receiverSa = 0x22;
        const byte peerSa = 0x11;
        const uint globalPgn = 0xFBCDu;
        const uint directedPgn = 0xF876u;

        using var receiver = J1939TpFactory.Open(receiverBus, sourceAddress: receiverSa,
            options: new J1939TpOptions().With(t2: TimeSpan.FromSeconds(5)));

        var cmFrames = new List<byte[]>();
        using var frameReady = new SemaphoreSlim(0);
        peerBus.FrameObserved += (_, e) =>
        {
            var frame = e.CanFrame;
            if (!frame.IsExtendedFrame) return;
            var fields = J1939Id.Decompose((uint)frame.ID);
            if (fields.SourceAddress != receiverSa || !J1939Pgn.IsTransportCm(fields.Pgn)) return;
            lock (cmFrames) cmFrames.Add(frame.Data.ToArray());
            frameReady.Release();
        };

        // An RTS to the global address, then a directed one. The receiver handles them in
        // arrival order on its actor, so the CTS for the directed RTS is the witness that the
        // global one has been processed too -- and produced nothing.
        var globalRts = J1939TpFrames.BuildRts(totalBytes: 14, totalPackets: 2, maxPacketsPerCts: 0xFF, dataPgn: globalPgn);
        peerBus.Transmit(CanFrame.Classic(
            (int)J1939Id.ComposePgn(7, J1939Pgn.TpCm, peerSa, J1939Pgn.GlobalAddress), globalRts, isExtendedFrame: true));
        var directedRts = J1939TpFrames.BuildRts(totalBytes: 14, totalPackets: 2, maxPacketsPerCts: 0xFF, dataPgn: directedPgn);
        peerBus.Transmit(CanFrame.Classic(
            (int)J1939Id.ComposePgn(7, J1939Pgn.TpCm, peerSa, receiverSa), directedRts, isExtendedFrame: true));

        using var deadline = new CancellationTokenSource(ShortTimeout);
        while (true)
        {
            lock (cmFrames)
            {
                if (cmFrames.Any(d => d[0] == J1939TpFrames.ControlCts && J1939TpFrames.ReadDataPgn(d) == directedPgn))
                    break;
            }
            await frameReady.WaitAsync(deadline.Token);
        }

        lock (cmFrames)
        {
            cmFrames.Should().NotContain(d => J1939TpFrames.ReadDataPgn(d) == globalPgn,
                "an RTS sent to the global address is not addressed to this node: no CTS, and no abort either -- "
                + "an answer to a global RTS would be one more frame of the storm it would cause");
            cmFrames.Should().ContainSingle(d => d[0] == J1939TpFrames.ControlCts,
                "only the directed RTS opens a session");
        }
    }

    [Fact]
    public async Task Cts_To_The_Global_Address_Does_Not_Drive_A_Tx_Session()
    {
        var session = NewSession();
        using var senderBus = Open(session, 0);
        using var peerBus = Open(session, 1);

        const byte senderSa = 0x10;
        const byte peerSa = 0x20;
        const uint pgn = 0xFECAu;
        var payload = RandomPayload(21, seed: 30); // 3 TP.DT frames

        // Long T3: the sender waits for its CTS without timing out while the test injects frames.
        using var sender = J1939TpFactory.Open(senderBus, sourceAddress: senderSa,
            options: new J1939TpOptions().With(t3: TimeSpan.FromSeconds(5)));

        var fromSender = new List<(uint pgn, byte[] data)>();
        using var frameReady = new SemaphoreSlim(0);
        peerBus.FrameObserved += (_, e) =>
        {
            var frame = e.CanFrame;
            if (!frame.IsExtendedFrame) return;
            var fields = J1939Id.Decompose((uint)frame.ID);
            if (fields.SourceAddress != senderSa) return;
            lock (fromSender) fromSender.Add((fields.Pgn, frame.Data.ToArray()));
            frameReady.Release();
        };

        async Task WaitForAsync(Func<uint, byte[], bool> predicate)
        {
            using var deadline = new CancellationTokenSource(ShortTimeout);
            while (true)
            {
                lock (fromSender)
                {
                    if (fromSender.Any(f => predicate(f.pgn, f.data))) return;
                }
                await frameReady.WaitAsync(deadline.Token);
            }
        }

        var send = sender.SendCmAsync(pgn, destinationAddress: peerSa, payload);
        await WaitForAsync((p, d) => J1939Pgn.IsTransportCm(p) && d[0] == J1939TpFrames.ControlRts);

        // A CTS for the whole message, but sent to the global address: not for us. Under the
        // defect the sender started its DTs on it, and the properly addressed CTS that follows
        // then arrived with an unexpected sequence number and aborted the session.
        var cts = J1939TpFrames.BuildCts(numPackets: 3, nextPacketSn: 1, dataPgn: pgn);
        peerBus.Transmit(CanFrame.Classic(
            (int)J1939Id.ComposePgn(7, J1939Pgn.TpCm, peerSa, J1939Pgn.GlobalAddress), cts, isExtendedFrame: true));
        peerBus.Transmit(CanFrame.Classic(
            (int)J1939Id.ComposePgn(7, J1939Pgn.TpCm, peerSa, senderSa), cts, isExtendedFrame: true));

        await WaitForAsync((p, d) => J1939Pgn.IsTransportDt(p) && d[0] == 3);
        lock (fromSender)
        {
            fromSender.Count(f => J1939Pgn.IsTransportDt(f.pgn)).Should().Be(3,
                "exactly one CTS -- the directed one -- released the block");
            fromSender.Should().NotContain(f => J1939Pgn.IsTransportCm(f.pgn) && f.data[0] == J1939TpFrames.ControlAbort,
                "the global CTS was ignored, so the directed CTS carried the expected sequence number");
        }

        peerBus.Transmit(CanFrame.Classic(
            (int)J1939Id.ComposePgn(7, J1939Pgn.TpCm, peerSa, senderSa),
            J1939TpFrames.BuildEomAck(payload.Length, totalPackets: 3, dataPgn: pgn), isExtendedFrame: true));
        await send.WithTimeout(ShortTimeout);
    }

    // -----------------------------------------------------------------------------------------
    // #32 -- sends to one destination go one after another. Two BAMs both go to the global
    // address, and TP.DT carries no PGN, so interleaved DTs are indistinguishable to a receiver;
    // J1939-21 §5.10.3 allows one BAM per source at a time (FR-TP-030).
    // -----------------------------------------------------------------------------------------
    [Fact]
    public async Task Parallel_Bam_Sends_Are_Transmitted_One_After_Another()
    {
        var session = NewSession();
        using var senderBus = Open(session, 0);
        using var receiverBus = Open(session, 1);

        const byte senderSa = 0x10;
        const uint firstPgn = 0xFECAu;
        const uint secondPgn = 0xFECBu;
        var firstPayload = RandomPayload(21, seed: 321);  // 3 TP.DT
        var secondPayload = RandomPayload(35, seed: 322); // 5 TP.DT

        var opts = new J1939TpOptions().With(bamPacketSpacing: TimeSpan.FromMilliseconds(5));
        using var sender = J1939TpFactory.Open(senderBus, sourceAddress: senderSa, options: opts);
        using var receiver = J1939TpFactory.Open(receiverBus, sourceAddress: 0x20, options: opts);

        var wire = new List<(bool isCm, byte[] data)>();
        receiverBus.FrameObserved += (_, e) =>
        {
            var frame = e.CanFrame;
            if (!frame.IsExtendedFrame) return;
            var fields = J1939Id.Decompose((uint)frame.ID);
            if (fields.SourceAddress != senderSa) return;
            lock (wire) wire.Add((J1939Pgn.IsTransportCm(fields.Pgn), frame.Data.ToArray()));
        };

        var first = sender.SendBamAsync(firstPgn, firstPayload);
        var second = sender.SendBamAsync(secondPgn, secondPayload);
        await Task.WhenAll(first, second).WithTimeout(ShortTimeout);

        var datagram1 = await receiver.ReceiveAsync().AsTaskWithTimeout(ShortTimeout);
        var datagram2 = await receiver.ReceiveAsync().AsTaskWithTimeout(ShortTimeout);
        datagram1.Pgn.Should().Be(firstPgn);
        datagram1.Payload.Should().Equal(firstPayload);
        datagram2.Pgn.Should().Be(secondPgn);
        datagram2.Payload.Should().Equal(secondPayload,
            "a receiver keeps one BAM per source; interleaved DTs would have corrupted or superseded it");

        // The wire order: BAM(first), its 3 DTs, BAM(second), its 5 DTs -- nothing interleaved.
        List<(bool isCm, byte[] data)> frames;
        lock (wire) frames = wire.ToList();
        var announces = frames.Select((f, i) => (f, i)).Where(t => t.f.isCm).Select(t => t.i).ToList();
        announces.Should().HaveCount(2);
        J1939TpFrames.ReadDataPgn(frames[announces[0]].data).Should().Be(firstPgn);
        J1939TpFrames.ReadDataPgn(frames[announces[1]].data).Should().Be(secondPgn);
        (announces[1] - announces[0] - 1).Should().Be(3,
            "every DT of the first BAM is on the wire before the second BAM is announced");
        frames.Skip(announces[0] + 1).Take(3).Select(f => f.data[0]).Should().Equal(new byte[] { 1, 2, 3 });
        frames.Skip(announces[1] + 1).Select(f => f.data[0]).Should().Equal(new byte[] { 1, 2, 3, 4, 5 });
    }

    [Fact]
    public async Task A_Queued_Send_Can_Be_Cancelled_Before_It_Reaches_The_Wire()
    {
        var session = NewSession();
        using var senderBus = Open(session, 0);
        using var receiverBus = Open(session, 1);

        const byte senderSa = 0x10;
        var opts = new J1939TpOptions().With(bamPacketSpacing: TimeSpan.FromMilliseconds(5));
        using var sender = J1939TpFactory.Open(senderBus, sourceAddress: senderSa, options: opts);
        using var receiver = J1939TpFactory.Open(receiverBus, sourceAddress: 0x20, options: opts);

        var announced = new List<uint>();
        receiverBus.FrameObserved += (_, e) =>
        {
            var frame = e.CanFrame;
            if (!frame.IsExtendedFrame) return;
            var fields = J1939Id.Decompose((uint)frame.ID);
            if (fields.SourceAddress != senderSa || !J1939Pgn.IsTransportCm(fields.Pgn)) return;
            lock (announced) announced.Add(J1939TpFrames.ReadDataPgn(frame.Data.Span));
        };

        using var cancel = new CancellationTokenSource();
        var first = sender.SendBamAsync(0xFEC1u, RandomPayload(35, seed: 1));
        var queued = sender.SendBamAsync(0xFEC2u, RandomPayload(21, seed: 2), cancel.Token);
        cancel.Cancel();

        Func<Task> act = async () => await queued.WithTimeout(ShortTimeout);
        await act.Should().ThrowAsync<OperationCanceledException>();
        await first.WithTimeout(ShortTimeout);

        // The slot is free again: a third send is announced right after the first, and the
        // cancelled one never reaches the wire.
        await sender.SendBamAsync(0xFEC3u, RandomPayload(21, seed: 3)).WithTimeout(ShortTimeout);
        (await receiver.ReceiveAsync().AsTaskWithTimeout(ShortTimeout)).Pgn.Should().Be(0xFEC1u);
        (await receiver.ReceiveAsync().AsTaskWithTimeout(ShortTimeout)).Pgn.Should().Be(0xFEC3u);
        lock (announced) announced.Should().Equal(0xFEC1u, 0xFEC3u);
    }

    [Fact]
    public async Task A_Second_Send_For_The_Same_Destination_And_Pgn_Is_Refused_While_One_Waits()
    {
        var session = NewSession();
        using var senderBus = Open(session, 0);
        using var sender = J1939TpFactory.Open(senderBus, sourceAddress: 0x10,
            options: new J1939TpOptions().With(bamPacketSpacing: TimeSpan.FromMilliseconds(5)));

        var first = sender.SendBamAsync(0xFEC1u, RandomPayload(35, seed: 1));
        var waiting = sender.SendBamAsync(0xFEC2u, RandomPayload(21, seed: 2));
        Func<Task> duplicate = async () => await sender.SendBamAsync(0xFEC2u, RandomPayload(21, seed: 3)).WithTimeout(ShortTimeout);

        await duplicate.Should().ThrowAsync<InvalidOperationException>(
            "one send per (destination, PGN) is in flight or waiting at a time, as before the queue");
        await Task.WhenAll(first, waiting).WithTimeout(ShortTimeout);
    }
    // -----------------------------------------------------------------------------------
    // #33 — the Connection Abort reason byte on the wire is J1939-21 table 7's, not this
    // stack's. Peer stacks log and react to the code; before this, "session already open"
    // went out as 7 (bad sequence number) and a bad sequence number as 5 (retransmit limit).
    // -----------------------------------------------------------------------------------
    [Theory]
    [InlineData(J1939TpAbortReason.SessionAlreadyOpen, 1)]
    [InlineData(J1939TpAbortReason.NoResourcesAvailable, 2)]
    [InlineData(J1939TpAbortReason.Timeout, 3)]
    [InlineData(J1939TpAbortReason.CtsReceivedDuringDataTransfer, 4)]
    [InlineData(J1939TpAbortReason.MaximumRetransmitRequestsReached, 5)]
    [InlineData(J1939TpAbortReason.UnexpectedDataTransferPacket, 6)]
    [InlineData(J1939TpAbortReason.BadSequenceNumber, 7)]
    [InlineData(J1939TpAbortReason.DuplicateSequenceNumber, 8)]
    [InlineData(J1939TpAbortReason.MessageSizeExceeded, 9)]
    public void Abort_Reason_Goes_On_The_Wire_As_Table_7_Assigns_It(J1939TpAbortReason reason, byte code)
    {
        var frame = J1939TpFrames.BuildAbort(reason, dataPgn: 0xFECAu);
        frame[0].Should().Be(J1939TpFrames.ControlAbort);
        frame[1].Should().Be(code);
    }

    // A CTS asking for a packet already sent is a retransmit request; with
    // MaxRetransmitRequests = 0 this stack serves none, so the limit is reached at once
    // (table 7, code 5). Before #33 it went out as 5 by coincidence of a different meaning
    // ("unexpected CTS sequence number"); since #58 the default serves two, covered by
    // A_Retransmit_Request_Is_Served_Until_The_Limit.
    [Fact]
    public async Task Cm_Sender_CtsForAPacketAlreadySent_AbortsWithRetransmitLimit()
    {
        var session = NewSession();
        using var senderBus = Open(session, 0);
        using var peerBus = Open(session, 1);

        const byte senderSa = 0xA1;
        const byte peerSa = 0xA2;
        const uint pgn = 0xFEA1u;
        var payload = RandomPayload(21, seed: 161); // 3 packets

        using var sender = J1939TpFactory.Open(senderBus, sourceAddress: senderSa,
            options: new J1939TpOptions().With(maxRetransmitRequests: 0));

        var rtsSeen = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstDtSeen = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var abortSeen = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        peerBus.FrameObserved += (_, e) =>
        {
            if (!e.CanFrame.IsExtendedFrame) return;
            var fields = J1939Id.Decompose((uint)e.CanFrame.ID);
            if (fields.SourceAddress != senderSa) return;
            var data = e.CanFrame.Data.ToArray();
            if (J1939Pgn.IsTransportCm(fields.Pgn) && data.Length >= 8 && J1939TpFrames.ReadDataPgn(data) == pgn)
            {
                if (data[0] == J1939TpFrames.ControlRts) rtsSeen.TrySetResult(null);
                else if (data[0] == J1939TpFrames.ControlAbort) abortSeen.TrySetResult(data);
            }
            else if (J1939Pgn.IsTransportDt(fields.Pgn) && data.Length >= 1 && data[0] == 1)
                firstDtSeen.TrySetResult(null);
        };

        var send = sender.SendCmAsync(pgn, destinationAddress: peerSa, payload);
        await rtsSeen.Task.AsTaskWithTimeout(ShortTimeout);

        var cmId = (int)J1939Id.ComposePgn(7, J1939Pgn.TpCm, peerSa, senderSa);
        // Grant one packet, take it, then ask for it again.
        peerBus.Transmit(CanFrame.Classic(cmId, J1939TpFrames.BuildCts(numPackets: 1, nextPacketSn: 1, dataPgn: pgn), isExtendedFrame: true));
        await firstDtSeen.Task.AsTaskWithTimeout(ShortTimeout);
        peerBus.Transmit(CanFrame.Classic(cmId, J1939TpFrames.BuildCts(numPackets: 1, nextPacketSn: 1, dataPgn: pgn), isExtendedFrame: true));

        Func<Task> act = async () => await send.WithTimeout(ShortTimeout);
        var ex = (await act.Should().ThrowAsync<J1939TpAbortException>()).Which;
        ex.Reason.Should().Be(J1939TpAbortReason.MaximumRetransmitRequestsReached);

        var abort = await abortSeen.Task.AsTaskWithTimeout(ShortTimeout);
        abort[1].Should().Be(5, "J1939-21 table 7: maximum retransmit request limit reached (#33)");
    }

    // A packet already received, repeated while a later one is expected, is a duplicate
    // (table 7, code 8) -- not only the one just received (Codex on #145).
    [Fact]
    public async Task Cm_Receiver_RepeatOfAnEarlierPacket_AbortsAsDuplicate()
    {
        var session = NewSession();
        using var receiverBus = Open(session, 0);
        using var peerBus = Open(session, 1);

        const byte receiverSa = 0xA3;
        const byte peerSa = 0xA4;
        const uint pgn = 0xFEA3u;
        var payload = RandomPayload(21, seed: 163); // 3 packets

        using var receiver = J1939TpFactory.Open(receiverBus, sourceAddress: receiverSa);

        var ctsSeen = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var abortSeen = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        peerBus.FrameObserved += (_, e) =>
        {
            if (!e.CanFrame.IsExtendedFrame) return;
            var fields = J1939Id.Decompose((uint)e.CanFrame.ID);
            if (fields.SourceAddress != receiverSa || !J1939Pgn.IsTransportCm(fields.Pgn)) return;
            var data = e.CanFrame.Data.ToArray();
            if (data.Length < 8 || J1939TpFrames.ReadDataPgn(data) != pgn) return;
            if (data[0] == J1939TpFrames.ControlCts) ctsSeen.TrySetResult(null);
            else if (data[0] == J1939TpFrames.ControlAbort) abortSeen.TrySetResult(data);
        };

        var recvTask = receiver.ReceiveAsync().AsTaskWithTimeout(ShortTimeout);

        var rts = J1939TpFrames.BuildRts(totalBytes: payload.Length, totalPackets: 3, maxPacketsPerCts: 0xFF, dataPgn: pgn);
        peerBus.Transmit(CanFrame.Classic((int)J1939Id.ComposePgn(7, J1939Pgn.TpCm, peerSa, receiverSa), rts, isExtendedFrame: true));
        await ctsSeen.Task.AsTaskWithTimeout(ShortTimeout);

        var dtId = (int)J1939Id.ComposePgn(7, J1939Pgn.TpDt, peerSa, receiverSa);
        peerBus.Transmit(CanFrame.Classic(dtId, J1939TpFrames.BuildDt(sn: 1, pdu: payload, offset: 0), isExtendedFrame: true));
        peerBus.Transmit(CanFrame.Classic(dtId, J1939TpFrames.BuildDt(sn: 2, pdu: payload, offset: 7), isExtendedFrame: true));
        // Expecting 3; packet 1 again.
        peerBus.Transmit(CanFrame.Classic(dtId, J1939TpFrames.BuildDt(sn: 1, pdu: payload, offset: 0), isExtendedFrame: true));

        var abort = await abortSeen.Task.AsTaskWithTimeout(ShortTimeout);
        abort[1].Should().Be(8, "J1939-21 table 7: duplicate sequence number (#33)");

        Func<Task> act = () => recvTask;
        var ex = (await act.Should().ThrowAsync<J1939TpAbortException>()).Which;
        ex.Reason.Should().Be(J1939TpAbortReason.DuplicateSequenceNumber);
    }

    // A CTS asking for packet 0 asks for a packet no message has: a bad sequence number
    // (code 7), not a retransmit request (Codex on #145).
    [Fact]
    public async Task Cm_Sender_CtsForPacketZero_AbortsAsBadSequenceNumber()
    {
        var session = NewSession();
        using var senderBus = Open(session, 0);
        using var peerBus = Open(session, 1);

        const byte senderSa = 0xA5;
        const byte peerSa = 0xA6;
        const uint pgn = 0xFEA5u;
        var payload = RandomPayload(14, seed: 165); // 2 packets

        using var sender = J1939TpFactory.Open(senderBus, sourceAddress: senderSa);

        var rtsSeen = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var abortSeen = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        peerBus.FrameObserved += (_, e) =>
        {
            if (!e.CanFrame.IsExtendedFrame) return;
            var fields = J1939Id.Decompose((uint)e.CanFrame.ID);
            if (fields.SourceAddress != senderSa || !J1939Pgn.IsTransportCm(fields.Pgn)) return;
            var data = e.CanFrame.Data.ToArray();
            if (data.Length < 8 || J1939TpFrames.ReadDataPgn(data) != pgn) return;
            if (data[0] == J1939TpFrames.ControlRts) rtsSeen.TrySetResult(null);
            else if (data[0] == J1939TpFrames.ControlAbort) abortSeen.TrySetResult(data);
        };

        var send = sender.SendCmAsync(pgn, destinationAddress: peerSa, payload);
        await rtsSeen.Task.AsTaskWithTimeout(ShortTimeout);

        // The codec refuses to build this CTS; a nonconforming peer sends it anyway.
        var cts = J1939TpFrames.BuildCts(numPackets: 1, nextPacketSn: 1, dataPgn: pgn);
        cts[2] = 0;
        peerBus.Transmit(CanFrame.Classic(
            (int)J1939Id.ComposePgn(7, J1939Pgn.TpCm, peerSa, senderSa), cts, isExtendedFrame: true));

        Func<Task> act = async () => await send.WithTimeout(ShortTimeout);
        var ex = (await act.Should().ThrowAsync<J1939TpAbortException>()).Which;
        ex.Reason.Should().Be(J1939TpAbortReason.BadSequenceNumber);

        var abort = await abortSeen.Task.AsTaskWithTimeout(ShortTimeout);
        abort[1].Should().Be(7, "J1939-21 table 7: bad sequence number (#33)");
    }

    // -----------------------------------------------------------------------------------
    // #31 — the window from the receiver's CTS to the first TP.DT is T2 (1250 ms), not Tr
    // (200 ms): Tr is the time a node has to *send* a response it owes. A conforming but slow
    // originator that needs 300 ms to get its first DT out must not be rejected.
    // -----------------------------------------------------------------------------------
    [Fact]
    public async Task Cm_Receiver_Survives_A_First_Dt_That_Arrives_300ms_After_Cts()
    {
        var session = NewSession();
        using var receiverBus = Open(session, 0);
        using var peerBus = Open(session, 1);

        const byte receiverSa = 0xB1;
        const byte peerSa = 0xB2;
        const uint pgn = 0xFEB1u;
        var payload = RandomPayload(14, seed: 177); // 2 packets

        // Defaults: T2 = 1250 ms is the timer under test. The 300 ms below is a lower bound on
        // the delay, so a loaded host only widens the gap it must survive, up to T2's margin.
        using var receiver = J1939TpFactory.Open(receiverBus, sourceAddress: receiverSa);

        var ctsSeen = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        peerBus.FrameObserved += (_, e) =>
        {
            if (!e.CanFrame.IsExtendedFrame) return;
            var fields = J1939Id.Decompose((uint)e.CanFrame.ID);
            if (fields.SourceAddress != receiverSa || !J1939Pgn.IsTransportCm(fields.Pgn)) return;
            var data = e.CanFrame.Data.Span;
            if (data.Length >= 8 && data[0] == J1939TpFrames.ControlCts && J1939TpFrames.ReadDataPgn(data) == pgn)
                ctsSeen.TrySetResult(null);
        };

        var recvTask = receiver.ReceiveAsync().AsTaskWithTimeout(ShortTimeout);

        var rts = J1939TpFrames.BuildRts(totalBytes: payload.Length, totalPackets: 2, maxPacketsPerCts: 0xFF, dataPgn: pgn);
        peerBus.Transmit(CanFrame.Classic((int)J1939Id.ComposePgn(7, J1939Pgn.TpCm, peerSa, receiverSa), rts, isExtendedFrame: true));
        await ctsSeen.Task.AsTaskWithTimeout(ShortTimeout);

        await Task.Delay(300);

        var dtId = (int)J1939Id.ComposePgn(7, J1939Pgn.TpDt, peerSa, receiverSa);
        peerBus.Transmit(CanFrame.Classic(dtId, J1939TpFrames.BuildDt(sn: 1, pdu: payload, offset: 0), isExtendedFrame: true));
        peerBus.Transmit(CanFrame.Classic(dtId, J1939TpFrames.BuildDt(sn: 2, pdu: payload, offset: 7), isExtendedFrame: true));

        var datagram = await recvTask;
        datagram.Payload.ToArray().Should().Equal(payload);
    }

    // -----------------------------------------------------------------------------------
    // #36 — the per-send registration on the caller's token is released when the send
    // completes. With an application-wide shutdown token, an undisposed registration keeps
    // the send's completion source -- and so the Task handed to the caller -- reachable from
    // the token for the life of the process. Observed through a weak reference to that Task.
    // -----------------------------------------------------------------------------------
    [Fact]
    public async Task A_Completed_Send_Is_Not_Kept_Alive_By_The_Callers_Token()
    {
        var session = NewSession();
        using var senderBus = Open(session, 0);
        using var receiverBus = Open(session, 1);
        using var sender = J1939TpFactory.Open(senderBus, sourceAddress: 0xC1);
        using var receiver = J1939TpFactory.Open(receiverBus, sourceAddress: 0xC2);
        using var shutdown = new CancellationTokenSource();

        var weak = await SendAndForgetAsync(sender, receiver, shutdown.Token);

        for (int i = 0; i < 5 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        weak.IsAlive.Should().BeFalse("the token's registration for the send was disposed with it");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> SendAndForgetAsync(IJ1939TpChannel sender,
        IJ1939TpChannel receiver, CancellationToken token)
    {
        var recv = receiver.ReceiveAsync().AsTaskWithTimeout(ShortTimeout);
        var send = sender.SendCmAsync(0xFEC1u, destinationAddress: 0xC2, RandomPayload(21, seed: 193), token);
        await send.WithTimeout(ShortTimeout);
        await recv;
        return new WeakReference(send);
    }

    // ---------------------------------------------------------------------------------------
    // #58: the four transport findings from the repository review.
    // ---------------------------------------------------------------------------------------

    // A raw peer on its own bus, observing every TP.CM the subject emits and able to answer.
    private sealed class RawPeer : IDisposable
    {
        private readonly List<byte[]> _cm = new();
        private readonly List<byte[]> _dt = new();
        private readonly SemaphoreSlim _ready = new(0);
        public ICanBus Bus { get; }
        public byte SubjectSa { get; }

        public RawPeer(ICanBus bus, byte subjectSa)
        {
            Bus = bus;
            SubjectSa = subjectSa;
            bus.FrameObserved += (_, e) =>
            {
                var frame = e.CanFrame;
                if (!frame.IsExtendedFrame) return;
                var fields = J1939Id.Decompose((uint)frame.ID);
                if (fields.SourceAddress != subjectSa) return;
                var data = frame.Data.ToArray();
                lock (_cm)
                {
                    if (J1939Pgn.IsTransportCm(fields.Pgn)) _cm.Add(data);
                    else if (fields.Pgn == J1939Pgn.TpDt) _dt.Add(data);
                }
                _ready.Release();
            };
        }

        public int DtCount { get { lock (_cm) return _dt.Count; } }

        public async Task<byte[]> WaitForCmAsync(Func<byte[], bool> predicate, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            while (true)
            {
                lock (_cm)
                {
                    foreach (var d in _cm) if (predicate(d)) return d;
                }
                await _ready.WaitAsync(cts.Token).ConfigureAwait(false);
            }
        }

        public async Task WaitForDtCountAsync(int count, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            while (DtCount < count) await _ready.WaitAsync(cts.Token).ConfigureAwait(false);
        }

        public void SendCm(byte peerSa, byte[] payload)
            => Bus.Transmit(CanFrame.Classic((int)J1939Id.ComposePgn(7, J1939Pgn.TpCm, peerSa, SubjectSa), payload, isExtendedFrame: true));

        public void SendDt(byte peerSa, byte[] payload)
            => Bus.Transmit(CanFrame.Classic((int)J1939Id.ComposePgn(7, J1939Pgn.TpDt, peerSa, SubjectSa), payload, isExtendedFrame: true));

        public void Dispose() => _ready.Dispose();
    }

    // #58: a peer that writes the destination address into the low byte of a PDU1 PGN in its
    // TP.CM names the same group; its CTS must still reach the originator's session.
    [Fact]
    public async Task A_Cts_With_The_Destination_In_The_Pgns_Low_Byte_Reaches_The_Session()
    {
        var session = NewSession();
        using var subjectBus = Open(session, 0);
        using var peerBus = Open(session, 1);
        const byte subjectSa = 0x10, peerSa = 0x20;
        const uint pgn = 0xC800u; // PDU1: PF 0xC8, PS 0
        var payload = RandomPayload(14, seed: 58);
        using var sender = J1939TpFactory.Open(subjectBus, sourceAddress: subjectSa);
        using var peer = new RawPeer(peerBus, subjectSa);

        using var cts = new CancellationTokenSource(ShortTimeout);
        var send = sender.SendCmAsync(pgn, peerSa, payload, cts.Token);
        await peer.WaitForCmAsync(d => d[0] == J1939TpFrames.ControlRts, ShortTimeout);

        // The CTS's PGN field carries our address in the low byte: 0xF810 rather than 0xC800.
        var ctsFrame = J1939TpFrames.BuildCts(numPackets: 2, nextPacketSn: 1, dataPgn: pgn | subjectSa);
        peer.SendCm(peerSa, ctsFrame);
        await peer.WaitForDtCountAsync(2, ShortTimeout);
        peer.SendCm(peerSa, J1939TpFrames.BuildEomAck(14, 2, pgn | subjectSa));
        await send.WaitAsync(ShortTimeout);
    }

    // #58: a PDU1 PGN with its low byte set is not a PGN -- the destination is the address
    // argument -- and is refused before anything goes out, as J1939Id.ComposePgn refuses it
    // (#55); the session is keyed on what the peer names in its CTS.
    [Fact]
    public async Task A_Pdu1_Pgn_With_A_Low_Byte_Is_Refused_Before_Anything_Goes_Out()
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var sender = J1939TpFactory.Open(bus, sourceAddress: 0x10);
        int transmitted = 0;
        bus.FrameObserved += (_, e) => { if (e.CanFrame.IsExtendedFrame) Interlocked.Increment(ref transmitted); };

        Func<Task> cm = () => sender.SendCmAsync(0xEE8Du, 0x20, RandomPayload(14, seed: 1));
        await cm.Should().ThrowAsync<ArgumentOutOfRangeException>().WithParameterName("pgn");
        Func<Task> bam = () => sender.SendBamAsync(0xEE8Du, RandomPayload(14, seed: 1));
        await bam.Should().ThrowAsync<ArgumentOutOfRangeException>().WithParameterName("pgn");
        await Task.Delay(50);
        transmitted.Should().Be(0, "nothing was transmitted");
    }

    // #58: an RTS that allows no packet per CTS can never be served -- every CTS would be a
    // hold -- and is not "no limit"; no session is opened, and the next well-formed RTS from
    // the same peer is served.
    [Fact]
    public async Task An_Rts_Allowing_No_Packet_Per_Cts_Opens_No_Session()
    {
        var session = NewSession();
        using var subjectBus = Open(session, 0);
        using var peerBus = Open(session, 1);
        const byte subjectSa = 0x22, peerSa = 0x11;
        const uint pgn = 0xFBCDu;
        using var receiver = J1939TpFactory.Open(subjectBus, sourceAddress: subjectSa);
        using var peer = new RawPeer(peerBus, subjectSa);

        peer.SendCm(peerSa, J1939TpFrames.BuildRts(totalBytes: 14, totalPackets: 2, maxPacketsPerCts: 0, dataPgn: pgn));
        Func<Task> none = () => peer.WaitForCmAsync(d => d[0] == J1939TpFrames.ControlCts, TimeSpan.FromMilliseconds(300));
        await none.Should().ThrowAsync<OperationCanceledException>("no CTS answers an RTS that permits none");

        // Served, because no session lingers for the malformed one.
        peer.SendCm(peerSa, J1939TpFrames.BuildRts(totalBytes: 14, totalPackets: 2, maxPacketsPerCts: 0xFF, dataPgn: pgn));
        var ctsFrame = await peer.WaitForCmAsync(d => d[0] == J1939TpFrames.ControlCts, ShortTimeout);
        ctsFrame[1].Should().BeGreaterThan(0);
    }

    // #58: a CTS for a packet already sent asks for it again and is served, up to
    // MaxRetransmitRequests times; the next one reaches table 7's limit (reason 5).
    [Fact]
    public async Task A_Retransmit_Request_Is_Served_Until_The_Limit()
    {
        var session = NewSession();
        using var subjectBus = Open(session, 0);
        using var peerBus = Open(session, 1);
        const byte subjectSa = 0x10, peerSa = 0x20;
        const uint pgn = 0xFECAu;
        var payload = RandomPayload(21, seed: 5); // three packets
        var opts = new J1939TpOptions().With(maxRetransmitRequests: 1);
        using var sender = J1939TpFactory.Open(subjectBus, sourceAddress: subjectSa, options: opts);
        using var peer = new RawPeer(peerBus, subjectSa);

        using var cts = new CancellationTokenSource(ShortTimeout);
        var send = sender.SendCmAsync(pgn, peerSa, payload, cts.Token);
        await peer.WaitForCmAsync(d => d[0] == J1939TpFrames.ControlRts, ShortTimeout);

        peer.SendCm(peerSa, J1939TpFrames.BuildCts(numPackets: 3, nextPacketSn: 1, dataPgn: pgn));
        await peer.WaitForDtCountAsync(3, ShortTimeout);

        // Packet 2 again: served -- one more DT, with SN 2.
        peer.SendCm(peerSa, J1939TpFrames.BuildCts(numPackets: 1, nextPacketSn: 2, dataPgn: pgn));
        await peer.WaitForDtCountAsync(4, ShortTimeout);

        // And again: the limit of one is reached, reason 5.
        peer.SendCm(peerSa, J1939TpFrames.BuildCts(numPackets: 1, nextPacketSn: 2, dataPgn: pgn));
        var abort = await peer.WaitForCmAsync(d => d[0] == J1939TpFrames.ControlAbort, ShortTimeout);
        abort[1].Should().Be((byte)J1939TpAbortReason.MaximumRetransmitRequestsReached);
        Func<Task> failed = () => send;
        await failed.Should().ThrowAsync<J1939TpAbortException>();
    }

    // Bugbot on #152: a receiver that asked for one packet again and then has the whole
    // message sends EndOfMsgAck, not another CTS; the originator, every packet sent at least
    // once, completes on it.
    [Fact]
    public async Task An_End_Of_Message_After_A_Partial_Retransmit_Completes_The_Send()
    {
        var session = NewSession();
        using var subjectBus = Open(session, 0);
        using var peerBus = Open(session, 1);
        const byte subjectSa = 0x10, peerSa = 0x20;
        const uint pgn = 0xFEC5u;
        var payload = RandomPayload(21, seed: 6); // three packets
        using var sender = J1939TpFactory.Open(subjectBus, sourceAddress: subjectSa);
        using var peer = new RawPeer(peerBus, subjectSa);

        using var cts = new CancellationTokenSource(ShortTimeout);
        var send = sender.SendCmAsync(pgn, peerSa, payload, cts.Token);
        await peer.WaitForCmAsync(d => d[0] == J1939TpFrames.ControlRts, ShortTimeout);
        peer.SendCm(peerSa, J1939TpFrames.BuildCts(numPackets: 3, nextPacketSn: 1, dataPgn: pgn));
        await peer.WaitForDtCountAsync(3, ShortTimeout);

        peer.SendCm(peerSa, J1939TpFrames.BuildCts(numPackets: 1, nextPacketSn: 2, dataPgn: pgn)); // packet 2 again
        await peer.WaitForDtCountAsync(4, ShortTimeout);
        peer.SendCm(peerSa, J1939TpFrames.BuildEomAck(21, 3, pgn));
        await send.WaitAsync(ShortTimeout);
    }

    // Codex on #152: a 255-packet message wraps the byte NextSn to 0 once every packet is
    // sent; a retransmit request for its last packet must still read as one, and be served.
    [Fact]
    public async Task A_Retransmit_Request_For_The_Last_Of_255_Packets_Is_Served()
    {
        var session = NewSession();
        using var subjectBus = Open(session, 0);
        using var peerBus = Open(session, 1);
        const byte subjectSa = 0x10, peerSa = 0x20;
        const uint pgn = 0xFEC0u;
        var payload = RandomPayload(J1939TpFrames.MaxTpPayloadLength, seed: 255); // 255 packets
        using var sender = J1939TpFactory.Open(subjectBus, sourceAddress: subjectSa);
        using var peer = new RawPeer(peerBus, subjectSa);

        using var cts = new CancellationTokenSource(ShortTimeout);
        var send = sender.SendCmAsync(pgn, peerSa, payload, cts.Token);
        await peer.WaitForCmAsync(d => d[0] == J1939TpFrames.ControlRts, ShortTimeout);
        peer.SendCm(peerSa, J1939TpFrames.BuildCts(numPackets: 255, nextPacketSn: 1, dataPgn: pgn));
        await peer.WaitForDtCountAsync(255, ShortTimeout);

        peer.SendCm(peerSa, J1939TpFrames.BuildCts(numPackets: 1, nextPacketSn: 255, dataPgn: pgn));
        await peer.WaitForDtCountAsync(256, ShortTimeout);
        peer.SendCm(peerSa, J1939TpFrames.BuildEomAck(J1939TpFrames.MaxTpPayloadLength, 255, pgn));
        await send.WaitAsync(ShortTimeout);
    }

    // #58: the datagram is in the inbox before DatagramReceived is raised, and the event is
    // raised off the actor -- so a handler that waits on ReceiveAsync gets the datagram
    // rather than deadlocking the channel, as an ISO-TP handler does.
    [Fact]
    public async Task DatagramReceived_Finds_The_Datagram_Already_Receivable()
    {
        var session = NewSession();
        using var senderBus = Open(session, 0);
        using var receiverBus = Open(session, 1);
        var opts = new J1939TpOptions().With(bamPacketSpacing: TimeSpan.FromMilliseconds(5));
        using var sender = J1939TpFactory.Open(senderBus, sourceAddress: 0x30, options: opts);
        using var receiver = J1939TpFactory.Open(receiverBus, sourceAddress: 0x31, options: opts);
        var payload = RandomPayload(14, seed: 9);

        var fromHandler = new TaskCompletionSource<J1939TpDatagram>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.DatagramReceived += (_, d) =>
        {
            try
            {
                // A synchronous wait on the channel from inside its own event.
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                fromHandler.TrySetResult(receiver.ReceiveAsync(wait.Token).GetAwaiter().GetResult());
            }
            catch (OperationCanceledException ex)
            {
                fromHandler.TrySetException(ex); // the wait timed out: the datagram was not receivable
            }
        };

        await sender.SendBamAsync(0xFECAu, payload).WaitAsync(ShortTimeout);
        var datagram = await fromHandler.Task.WaitAsync(ShortTimeout);
        datagram.Payload.Should().Equal(payload);
    }
}

/// <summary>
/// Test double: rejects every TP.CM frame at SendConfirmed, forwards everything else.
/// Used to prove BAM/RTS TX failure fails the send TCS (Bugbot 3596183535).
/// </summary>
internal sealed class RejectTpCmBusService : ICanBusService
{
    private readonly ICanBusService _inner;

    public RejectTpCmBusService(ICanBusService inner) => _inner = inner;

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

    public Task<TxConfirmation> SendConfirmed(CanFrame frame, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (frame.IsExtendedFrame)
        {
            var fields = J1939Id.Decompose((uint)frame.ID);
            if (J1939Pgn.IsTransportCm(fields.Pgn))
            {
                return Task.FromResult(new TxConfirmation
                {
                    Confirmed = false,
                    IsApproximated = false,
                    Timestamp = DateTime.UtcNow,
                    FailureReason = TxConfirmFailureReason.Rejected,
                });
            }
        }

        return _inner.SendConfirmed(frame, timeout, cancellationToken);
    }

    public void Dispose() { /* leaveOpen wrappers do not own the inner service */ }
}

internal static class J1939TpTestExtensions
{
    public static async Task<T> AsTaskWithTimeout<T>(this Task<T> task, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout));
        if (completed != task) throw new TimeoutException($"Operation timed out after {timeout}.");
        return await task;
    }

    public static async Task WithTimeout(this Task task, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout));
        if (completed != task) throw new TimeoutException($"Operation timed out after {timeout}.");
        await task;
    }
}
