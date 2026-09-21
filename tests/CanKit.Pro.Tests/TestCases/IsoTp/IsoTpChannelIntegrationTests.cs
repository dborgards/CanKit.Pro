using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core;
using CanKit.Pro.Actor;
using CanKit.Pro.IsoTp;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using FluentAssertions;
using Xunit;
// Alias the CanKit.Pro.IsoTp namespace root to avoid clashing with this test namespace's
// trailing "IsoTp" segment, which would otherwise shadow the static factory class.
using IsoTpFactory = CanKit.Pro.IsoTp.IsoTp;

namespace CanKit.Pro.Tests.TestCases.IsoTp;

/// <summary>
/// End-to-end integration tests for <see cref="IIsoTpChannel"/> against the Virtual loopback
/// adapter. These tests exercise the actor-driven runtime (subscription + demux + deadlines +
/// send-confirmed) rather than the pure codec.
/// </summary>
/// <remarks>
/// Traceability to SRS:
/// <list type="bullet">
///   <item><description>FR-TP-001 / FR-TP-002 / FR-TP-008 — SF and multi-frame round-trips (SN=1..)</description></item>
///   <item><description>FR-TP-009 — multi-frame TX actually starts (FF sent, waits for FC, sends CFs)</description></item>
///   <item><description>FR-TP-010 — N_Bs timeout: SendAsync faults when peer never sends FC</description></item>
///   <item><description>FR-TP-011 — WFTmax: too many Wait FCs abort the send</description></item>
///   <item><description>FR-TP-012 — Overflow FC aborts the send</description></item>
///   <item><description>FR-TP-016 — event-driven scheduling (no busy loop; disposed cleanly)</description></item>
///   <item><description>FR-TP-018 — multiple channels on the same bus with disjoint endpoints</description></item>
/// </list>
/// </remarks>
public class IsoTpChannelIntegrationTests : IClassFixture<VirtualAdapterFixture>
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);

    private static string NewSession() => $"isotp-{Guid.NewGuid():N}";

    private static ICanBus OpenClassic(string session, int channel) => CanBus.Open(
        $"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(VirtualAdapterFixture.Bitrate));

    private static ICanBus OpenCanFd(string session, int channel) => CanBus.Open(
        $"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.CanFd).Fd(VirtualAdapterFixture.Bitrate, VirtualAdapterFixture.DataBitrate));

    // Fast timings so protocol-timeout tests don't spend seconds each. Classic-CAN.
    private static IsoTpChannelOptions FastOptions(byte localBs = 0, TimeSpan? localStMin = null,
        TimeSpan? nBs = null, TimeSpan? nCr = null, int wftMax = 10, bool useCanFd = false)
        => new()
        {
            UseCanFd = useCanFd,
            UsePadding = true,
            LocalBlockSize = localBs,
            LocalStMin = localStMin ?? TimeSpan.Zero,
            NAs = TimeSpan.FromMilliseconds(500),
            NBs = nBs ?? TimeSpan.FromMilliseconds(500),
            NCr = nCr ?? TimeSpan.FromMilliseconds(500),
            WftMax = wftMax,
        };

    // --------------------------------------------------------------------------------
    // #112 — the arrival stamp reports when the PDU arrived, not when it was collected.
    // A deadline built on it (UDS P2) is only as good as that distinction: a stamp taken at
    // delivery would make every late reader look like a late peer.
    // --------------------------------------------------------------------------------
    [Fact]
    public async Task ArrivalStamp_Is_Taken_On_Arrival_Not_On_Collection()
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);

        var epAB = IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8);
        var epBA = IsoTpEndpoint.Normal(txCanId: 0x7E8, rxCanId: 0x7E0);

        using var sender = IsoTpFactory.Open(busA, epAB, FastOptions());
        using var receiver = IsoTpFactory.Open(busB, epBA, FastOptions());

        var arrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.DatagramReceived += (_, _) => arrived.TrySetResult(true);

        // Multi-frame on purpose: the stamp must follow the *last* frame, and a multi-frame PDU
        // is the case where reassembly sits between arrival and delivery.
        var payload = new byte[64];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)i;

        await sender.SendAsync(payload).WaitAsync(ShortTimeout);
        await arrived.Task.WaitAsync(ShortTimeout);

        // Collect late, deliberately. Nothing about this delay is the peer's fault, so none of
        // it may show up in the stamp.
        var collectionDelay = TimeSpan.FromMilliseconds(300);
        await Task.Delay(collectionDelay);

        var received = await receiver.ReceiveWithArrivalAsync(CancellationToken.None)
            .WaitAsync(ShortTimeout);
        var collectedAt = Stopwatch.GetTimestamp();

        received.Pdu.Should().Equal(payload);

        var age = TimeSpan.FromSeconds(
            (double)(collectedAt - received.ArrivalTimestamp) / Stopwatch.Frequency);
        age.Should().BeGreaterThan(TimeSpan.FromMilliseconds(250),
            "the PDU arrived before the deliberate {0} collection delay, so the stamp must be "
            + "that much older than the read — a stamp taken at delivery would read ~0",
            collectionDelay);
    }

    // --------------------------------------------------------------------------------
    // #112 — an ICanBusService that does not stamp its events still yields usable arrival
    // times. IsoTp.Open(ICanBusService, …) is public, so an event carrying no host stamp is
    // reachable from outside this repository; a zero read as a timestamp would mean
    // "infinitely old" and make every deadline reject every PDU.
    // --------------------------------------------------------------------------------
    [Fact]
    public async Task Unstamped_Events_From_A_Foreign_Bus_Service_Fall_Back_To_Now()
    {
        var ep = IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8);
        using var service = new UnstampingBusService();
        using var channel = IsoTpFactory.Open(service, ep, FastOptions());

        var before = Stopwatch.GetTimestamp();

        // Single Frame, Normal addressing: low nibble of byte 0 is the length.
        service.Deliver(new CanFrameView(CanFrameType.Can20, 0x7E8,
            new byte[] { 0x03, 0xAA, 0xBB, 0xCC }, FrameFlags.None));

        var received = await channel.ReceiveWithArrivalAsync(CancellationToken.None)
            .WaitAsync(ShortTimeout);
        var after = Stopwatch.GetTimestamp();

        received.Pdu.Should().Equal(new byte[] { 0xAA, 0xBB, 0xCC });
        received.ArrivalTimestamp.Should().BeInRange(before, after,
            "an unstamped event must be treated as having arrived now, not at tick zero");
    }

    /// <summary>
    /// The smallest <see cref="ICanBusService"/> that an ISO-TP channel will run on, delivering
    /// events built without a host arrival stamp — what any implementation outside this
    /// repository would produce.
    /// </summary>
    private sealed class UnstampingBusService : ICanBusService
    {
        private readonly Channel<CanFrameEvent> _frames =
            Channel.CreateUnbounded<CanFrameEvent>();

        public void Deliver(CanFrameView frame) => _frames.Writer.TryWrite(
            new CanFrameEvent(frame, isEcho: false, TimeSpan.Zero));

        public ICanBus Bus => throw new NotSupportedException();

        public int SubscriptionCount => 1;

        public event EventHandler<Exception>? BackgroundExceptionOccurred
        {
            add { }
            remove { }
        }

        public ISubscription Subscribe(Func<CanFrameEvent, bool>? predicate = null,
            int? bufferCapacity = null, bool includeEcho = false)
            => new Sub(_frames);

        public ISubscription Subscribe(CanIdFilter filter, int? bufferCapacity = null,
            bool includeEcho = false)
            => new Sub(_frames);

        public Task<TxConfirmation> SendConfirmed(CanFrame frame, TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IReadOnlyList<FilterOverlap> FindOverlappingFilterSubscriptions()
            => Array.Empty<FilterOverlap>();

        public void Dispose() => _frames.Writer.TryComplete();

        private sealed class Sub : ISubscription
        {
            private readonly Channel<CanFrameEvent> _frames;
            public Sub(Channel<CanFrameEvent> frames) => _frames = frames;

            public IAsyncEnumerable<CanFrameEvent> Frames => _frames.Reader.ReadAllAsync();

            public bool TryRead(out CanFrameEvent frameEvent)
                => _frames.Reader.TryRead(out frameEvent);

            public ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
                => _frames.Reader.WaitToReadAsync(cancellationToken);

            public void Reconfigure(CanIdFilter filter) { }

            public void Reconfigure(Func<CanFrameEvent, bool>? predicate) { }

            public void Dispose() { }
        }
    }

    // --------------------------------------------------------------------------------
    // FR-TP-001 — SF round-trip on classic CAN via the actor runtime + Virtual loopback.
    // --------------------------------------------------------------------------------
    [Fact]
    public async Task SingleFrame_RoundTrips_On_Virtual_Loopback()
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);

        var epAB = IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8);
        var epBA = IsoTpEndpoint.Normal(txCanId: 0x7E8, rxCanId: 0x7E0);

        using var sender = IsoTpFactory.Open(busA, epAB, FastOptions());
        using var receiver = IsoTpFactory.Open(busB, epBA, FastOptions());

        var receiveTask = receiver.ReceiveAsync(new CancellationTokenSource(ShortTimeout).Token);

        byte[] pdu = { 0x22, 0xF1, 0x89 }; // e.g. UDS ReadDataByIdentifier(0xF189)
        await sender.SendAsync(pdu);

        var got = await receiveTask;
        got.Should().Equal(pdu);
    }

    // --------------------------------------------------------------------------------
    // FR-TP-001 / FR-TP-002 / FR-TP-008 / FR-TP-009 — multi-frame classic-CAN round-trip
    // exercises FF -> FC -> CFs -> reassembly, with SN starting at 1 and wrapping 0..15 across
    // more than 16 CFs so the wrap logic is also touched.
    // --------------------------------------------------------------------------------
    [Fact]
    public async Task MultiFrame_ClassicCan_RoundTrips_20_Bytes()
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);

        var epAB = IsoTpEndpoint.Normal(0x7E0, 0x7E8);
        var epBA = IsoTpEndpoint.Normal(0x7E8, 0x7E0);

        using var sender = IsoTpFactory.Open(busA, epAB, FastOptions());
        using var receiver = IsoTpFactory.Open(busB, epBA, FastOptions());

        byte[] pdu = Enumerable.Range(0, 20).Select(i => (byte)(i + 1)).ToArray();
        var recvTask = receiver.ReceiveAsync(new CancellationTokenSource(ShortTimeout).Token);
        await sender.SendAsync(pdu);
        var got = await recvTask;
        got.Should().Equal(pdu);
    }

    [Fact]
    public async Task MultiFrame_ClassicCan_RoundTrips_Long_Payload_With_SN_Wrap()
    {
        // A payload of 200 bytes on classic CAN yields ~29 CFs (7 data bytes each after FF's 6),
        // so SN wraps 0..15 at least once. Exercises FR-TP-008 SN wrap and FR-TP-009's "FF starts
        // and completes within a bounded time".
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);

        using var sender = IsoTpFactory.Open(busA, IsoTpEndpoint.Normal(0x101, 0x102), FastOptions());
        using var receiver = IsoTpFactory.Open(busB, IsoTpEndpoint.Normal(0x102, 0x101), FastOptions());

        byte[] pdu = Enumerable.Range(0, 200).Select(i => (byte)(i & 0xFF)).ToArray();
        var recv = receiver.ReceiveAsync(new CancellationTokenSource(ShortTimeout).Token);
        await sender.SendAsync(pdu);
        var got = await recv;
        got.Should().Equal(pdu);
    }

    // --------------------------------------------------------------------------------
    // FR-TP-010 — N_Bs timeout: peer never sends FC after our FF -> SendAsync must fault with
    // IsoTpTimeoutException (not hang, not swallow) within N_Bs.
    // --------------------------------------------------------------------------------
    [Fact]
    public async Task MultiFrame_Send_Times_Out_When_Peer_Does_Not_Send_FlowControl()
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1); // opened just so the hub actually has a peer

        var opts = FastOptions(nBs: TimeSpan.FromMilliseconds(150));
        using var sender = IsoTpFactory.Open(busA, IsoTpEndpoint.Normal(0x300, 0x301), opts);
        // Note: no IsoTpChannel on busB, so the FF is delivered to the bus but nothing sends FC.

        byte[] pdu = Enumerable.Range(0, 30).Select(i => (byte)i).ToArray();

        Func<Task> act = () => sender.SendAsync(pdu);
        (await act.Should().ThrowAsync<IsoTpTimeoutException>()
            .WithMessage("*N_Bs*")).Which.Timer.Should().Be(IsoTpTimer.NBs);
    }

    // --------------------------------------------------------------------------------
    // FR-TP-011 — WFTmax exceeded: peer keeps sending Wait FCs.
    // --------------------------------------------------------------------------------
    [Fact]
    public async Task MultiFrame_Send_Aborts_When_Peer_Exceeds_WftMax()
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);

        var epAB = IsoTpEndpoint.Normal(0x400, 0x401);
        var epBA = IsoTpEndpoint.Normal(0x401, 0x400);

        int wftMax = 2;
        using var sender = IsoTpFactory.Open(busA, epAB,
            FastOptions(nBs: TimeSpan.FromSeconds(2), wftMax: wftMax));

        // Peer implemented "by hand" on busB: for every FF we get, keep answering FC(Wait,...).
        int waitFcSent = 0;
        var peerReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        busB.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != 0x400) return;
            var payload = e.CanFrame.Data.ToArray();
            if (payload.Length == 0) return;
            int typeNibble = payload[0] >> 4;
            if (typeNibble == 0x1) // First Frame -> reply with FC(Wait)
            {
                var fc = IsoTpFrameCodec.BuildFlowControl(epBA, FlowStatus.Wait,
                    blockSize: 0, stMinRaw: 0, isCanFd: false, padding: true);
                var frame = CanFrame.Classic(0x401, fc);
                busB.Transmit(frame);
                Interlocked.Increment(ref waitFcSent);
                peerReady.TrySetResult(true);
            }
        };

        byte[] pdu = Enumerable.Range(0, 30).Select(i => (byte)i).ToArray();

        var sendTask = sender.SendAsync(pdu);
        // Trigger further Wait FCs by transmitting extra WaitFC frames from busB directly (the
        // peer handler above only fires on FF; keep the sender waiting by feeding more Waits
        // until it exceeds WftMax).
        await peerReady.Task.WaitAsync(ShortTimeout);
        for (int i = 0; i < wftMax + 2; i++)
        {
            var fc = IsoTpFrameCodec.BuildFlowControl(epBA, FlowStatus.Wait,
                blockSize: 0, stMinRaw: 0, isCanFd: false, padding: true);
            busB.Transmit(CanFrame.Classic(0x401, fc));
            await Task.Delay(20);
        }

        Func<Task> act = () => sendTask;
        var ex = (await act.Should().ThrowAsync<IsoTpWaitFrameLimitExceededException>()).Which;
        ex.Limit.Should().Be(wftMax);
        ex.WaitFramesReceived.Should().BeGreaterThan(wftMax);
    }

    // --------------------------------------------------------------------------------
    // FR-TP-012 — Overflow FC aborts the send with a reported failure.
    // --------------------------------------------------------------------------------
    [Fact]
    public async Task MultiFrame_Send_Aborts_On_Overflow_FlowControl()
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);

        var epAB = IsoTpEndpoint.Normal(0x500, 0x501);
        var epBA = IsoTpEndpoint.Normal(0x501, 0x500);

        using var sender = IsoTpFactory.Open(busA, epAB, FastOptions(nBs: TimeSpan.FromSeconds(2)));

        busB.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != 0x500) return;
            var payload = e.CanFrame.Data.ToArray();
            if (payload.Length == 0) return;
            if ((payload[0] >> 4) == 0x1) // FF -> reply Overflow
            {
                var fc = IsoTpFrameCodec.BuildFlowControl(epBA, FlowStatus.Overflow,
                    blockSize: 0, stMinRaw: 0, isCanFd: false, padding: true);
                busB.Transmit(CanFrame.Classic(0x501, fc));
            }
        };

        byte[] pdu = Enumerable.Range(0, 30).Select(i => (byte)i).ToArray();
        Func<Task> act = () => sender.SendAsync(pdu);
        await act.Should().ThrowAsync<IsoTpOverflowException>();
    }

    // --------------------------------------------------------------------------------
    // First-Frame that already carries the full announced length must complete without
    // waiting for a Consecutive Frame (otherwise N_Cr fires and the PDU is never emitted).
    // Classic CAN: inject FF with DL=6 so the 6 data bytes fill the frame.
    // --------------------------------------------------------------------------------
    [Fact]
    public async Task FirstFrame_With_Full_Payload_Completes_Without_ConsecutiveFrame()
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);

        var epRecv = IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8);
        using var receiver = IsoTpFactory.Open(busA, epRecv,
            FastOptions(nCr: TimeSpan.FromMilliseconds(300)));

        var receiveTask = receiver.ReceiveAsync(new CancellationTokenSource(ShortTimeout).Token);

        // FF PCI 0x10 0x06 + 6 data bytes — announced length equals FF data capacity.
        byte[] ff = { 0x10, 0x06, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 };
        busB.Transmit(CanFrame.Classic(0x7E8, ff));

        var got = await receiveTask;
        got.Should().Equal(0x11, 0x22, 0x33, 0x44, 0x55, 0x66);
    }

    // --------------------------------------------------------------------------------
    // DiscardPendingPdus drains completed PDUs and AbortRx faults so a later ReceiveAsync
    // is not poisoned by leftover inbox items after a higher-layer cancel/timeout.
    // --------------------------------------------------------------------------------
    // #92 step 2. This used Task.Delay as a synchronisation primitive twice -- 50 ms for "the SF
    // has surely been buffered by now" and 200 ms for "N_Cr has surely expired by now" -- and
    // both are bets that a shared runner does a bounded amount of work in a fixed window. It lost
    // them at 4 runs in 6 under 2x load, on this branch and equally on its base, which is how it
    // was identified rather than assumed.
    //
    // Neither delay is replaced by a longer one. The first becomes a signal (EmitPdu enqueues
    // before raising DatagramReceived, so the event proves the inbox is non-empty) and the second
    // disappears entirely: N_Cr is armed on the receiver's actor, so a clock this test owns makes
    // its expiry a fact established rather than waited for.
    [Fact]
    public async Task DiscardPendingPdus_Drains_Completed_Pdus_And_Abort_Faults()
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);

        var epRecv = IsoTpEndpoint.Normal(txCanId: 0x7E8, rxCanId: 0x7E0);
        var epPeer = IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8);

        var nCr = TimeSpan.FromMilliseconds(80);
        using var clock = new VirtualClock();
        using var serviceRecv = new CanBusService(busB);
        using var servicePeer = new CanBusService(busA);
        var receiverActor = clock.NewActor();
        using var receiver = new IsoTpChannel(serviceRecv, epRecv, FastOptions(nCr: nCr),
            ownsService: false, receiverActor);
        using var sender = new IsoTpChannel(servicePeer, epPeer, FastOptions(),
            ownsService: false, clock.NewActor());

        // Buffer a completed SF without a waiter. The event is raised after the enqueue, so it
        // says the inbox holds it -- which is what the 50 ms delay used to assume.
        var singleFrameQueued = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.DatagramReceived += (_, _) => singleFrameQueued.TrySetResult(true);

        await sender.SendAsync(new byte[] { 0x22, 0xF1, 0x90 });
        await singleFrameQueued.Task.WaitAsync(ShortTimeout);

        // Queue an AbortRx fault via N_Cr (FF then silence).
        //
        // The receiver's flow control on the wire is NOT proof that N_Cr is armed, though the
        // first draft of this used it as one: HandleRxFirstFrame calls SendUnsequencedFrame --
        // fire-and-forget -- and only afterwards assigns _rx and calls ArmNCr. The frame can
        // therefore be observed while the handler is still several statements short of arming,
        // and advancing there would arm the timer from the new reading and it would never expire.
        // Codex and Bugbot both caught that; the code order is at IsoTpChannel.cs:968 and :981.
        byte[] ffPayload = Enumerable.Range(0, 20).Select(i => (byte)i).ToArray();
        int ffData = IsoTpFrameCodec.FirstFrameMaxDataLength(isCanFd: false, usesAddressExtension: false, useLongLength: false);
        var ff = IsoTpFrameCodec.BuildFirstFrame(epPeer, ffPayload.Length, ffPayload.AsSpan(0, ffData), isCanFd: false);
        busA.Transmit(CanFrame.Classic(unchecked((int)epPeer.TxCanId), ff));

        // Ask the actor instead. This is true exactly when N_Cr is armed and the clock has not
        // moved since, which is the state the advance below requires.
        await clock.WaitUntilTimerArmedAsync(receiverActor, nCr, ShortTimeout);

        // N_Cr expires because the clock says so. AdvanceAsync returns only once the callback has
        // run, and AbortRx enqueues its fault on the actor, so the item is in the inbox here --
        // no window, nothing to wait out.
        await clock.AdvanceAsync(nCr + TimeSpan.FromMilliseconds(1));

        int discarded = receiver.DiscardPendingPdus();
        discarded.Should().BeGreaterThanOrEqualTo(2,
            "at least the SF PDU and the N_Cr abort fault must be drained");

        // Fresh receive after drain must succeed (not throw the discarded N_Cr fault).
        var recv = receiver.ReceiveAsync(new CancellationTokenSource(ShortTimeout).Token);
        byte[] ok = { 0x3E, 0x00 };
        await sender.SendAsync(ok);
        (await recv).Should().Equal(ok);
    }

    // --------------------------------------------------------------------------------
    // Bugbot 3596444314 — DiscardPendingPdus must also abort in-flight multi-frame
    // reassembly. Otherwise leftover CFs can finish and enqueue a full PDU that a
    // higher layer (UDS) may treat as the next response.
    // --------------------------------------------------------------------------------
    [Fact]
    public async Task DiscardPendingPdus_Aborts_InFlight_Reassembly()
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);

        var epRecv = IsoTpEndpoint.Normal(txCanId: 0x7E8, rxCanId: 0x7E0);
        var epPeer = IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8);

        using var receiver = IsoTpFactory.Open(busB, epRecv, FastOptions());

        var fcSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        busA.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != unchecked((int)epRecv.TxCanId)) return;
            var data = e.CanFrame.Data.ToArray();
            if (data.Length > 0 && (data[0] >> 4) == 0x3)
                fcSeen.TrySetResult(true);
        };

        // 13 bytes => FF carries 6, one CF carries the remaining 7 (classic CAN).
        byte[] stalePayload = Enumerable.Range(0, 13).Select(i => (byte)(i + 0xA0)).ToArray();
        int ffData = IsoTpFrameCodec.FirstFrameMaxDataLength(isCanFd: false, usesAddressExtension: false, useLongLength: false);
        var ff = IsoTpFrameCodec.BuildFirstFrame(epPeer, stalePayload.Length, stalePayload.AsSpan(0, ffData), isCanFd: false);
        busA.Transmit(CanFrame.Classic(unchecked((int)epPeer.TxCanId), ff));

        await fcSeen.Task.WaitAsync(ShortTimeout);

        // Mid-reassembly reset — must clear _rx so the trailing CF cannot complete a PDU.
        receiver.DiscardPendingPdus();

        byte[] chunk = stalePayload.AsSpan(ffData).ToArray();
        var cf = IsoTpFrameCodec.BuildConsecutiveFrame(epPeer, sequenceNumber: 1, chunk,
            isCanFd: false, padding: true);
        busA.Transmit(CanFrame.Classic(unchecked((int)epPeer.TxCanId), cf));
        await Task.Delay(50);

        // Stale multi-frame must not appear; a fresh SF must be the next ReceiveAsync result.
        using var sender = IsoTpFactory.Open(busA, epPeer, FastOptions());
        var recv = receiver.ReceiveAsync(new CancellationTokenSource(ShortTimeout).Token);
        byte[] fresh = { 0x62, 0xF1, 0x90 };
        await sender.SendAsync(fresh);
        (await recv).Should().Equal(fresh);
    }

    // --------------------------------------------------------------------------------
    // FR-TP-018 — Two ISO-TP channels on the *same* bus with disjoint endpoints work
    // independently.
    // --------------------------------------------------------------------------------
    [Fact]
    public async Task Two_Channels_On_Same_Bus_Are_Independent()
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);

        // Share one service on busA multiplexing two ISO-TP endpoints (per FR-TP-018).
        using var svcA = new CanBusService(busA);
        using var svcB = new CanBusService(busB);

        using var sendX = IsoTpFactory.Open(svcA, IsoTpEndpoint.Normal(0x600, 0x601), FastOptions(), leaveOpen: true);
        using var recvX = IsoTpFactory.Open(svcB, IsoTpEndpoint.Normal(0x601, 0x600), FastOptions(), leaveOpen: true);

        using var sendY = IsoTpFactory.Open(svcA, IsoTpEndpoint.Normal(0x700, 0x701), FastOptions(), leaveOpen: true);
        using var recvY = IsoTpFactory.Open(svcB, IsoTpEndpoint.Normal(0x701, 0x700), FastOptions(), leaveOpen: true);

        var recvXTask = recvX.ReceiveAsync(new CancellationTokenSource(ShortTimeout).Token);
        var recvYTask = recvY.ReceiveAsync(new CancellationTokenSource(ShortTimeout).Token);

        byte[] pduX = new byte[] { 1, 2, 3, 4, 5 };
        byte[] pduY = Enumerable.Range(0x80, 30).Select(i => (byte)i).ToArray();

        // Send both concurrently to prove independence.
        await Task.WhenAll(sendX.SendAsync(pduX), sendY.SendAsync(pduY));

        (await recvXTask).Should().Equal(pduX);
        (await recvYTask).Should().Equal(pduY);
    }

    // --------------------------------------------------------------------------------
    // FR-TP-016 / FR-RAW-021 — Dispose is thread-safe / idempotent and unblocks a hanging
    // ReceiveAsync (channel end => ReceiveAsync throws or returns cleanly).
    // --------------------------------------------------------------------------------
    [Fact]
    public async Task Dispose_Unblocks_Pending_ReceiveAsync()
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);

        // `using` on top of the two explicit calls below: the point of this test is that Dispose
        // is idempotent, so a third call at scope exit changes nothing -- but without it a
        // ReceiveAsync that throws would leave the channel open for the rest of the run
        // (CodeQL cs/dispose-not-called-on-throw).
        using var channel = IsoTpFactory.Open(busA, IsoTpEndpoint.Normal(0x123, 0x321), FastOptions());
        var recvTask = channel.ReceiveAsync();
        // Idempotent dispose
        channel.Dispose();
        channel.Dispose();

        // Pinned to the exact failure ReceiveAsync documents for a disposed channel. "Any
        // exception" would also have accepted the two outcomes this test exists to rule out: a
        // ChannelClosedException leaking the inbox implementation to the caller, and an
        // OperationCanceledException from the reader's own CTS, which callers would reasonably
        // treat as "my token was cancelled, retry" rather than "this channel is finished".
        // WaitAsync bounds the wait so a Dispose that fails to unblock the reader fails this
        // test instead of hanging the run.
        Func<Task> act = () => recvTask.WaitAsync(ShortTimeout);
        var thrown = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        // BeOfType, not the ThrowAsync<T> above: ObjectDisposedException derives from
        // InvalidOperationException and would satisfy it.
        thrown.Should().BeOfType<InvalidOperationException>();
        thrown.Message.Should().Contain("disposed");
    }

    // --------------------------------------------------------------------------------
    // FR-TP-016 — DatagramReceived event fires for a SF PDU.
    // --------------------------------------------------------------------------------
    [Fact]
    public async Task DatagramReceived_Event_Fires_For_Received_Sf()
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);

        using var sender = IsoTpFactory.Open(busA, IsoTpEndpoint.Normal(0x111, 0x222), FastOptions());
        using var receiver = IsoTpFactory.Open(busB, IsoTpEndpoint.Normal(0x222, 0x111), FastOptions());

        var eventFired = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.DatagramReceived += (_, e) => eventFired.TrySetResult(e.Data);

        byte[] pdu = { 0xAA, 0xBB, 0xCC };
        await sender.SendAsync(pdu);

        var got = await eventFired.Task.WaitAsync(ShortTimeout);
        got.Should().Equal(pdu);
    }

    // --------------------------------------------------------------------------------
    // Bugbot 3596134684 / FR-TP-010 — N_Cr expiry must fault a blocked ReceiveAsync (not only
    // raise BackgroundExceptionOccurred), and the channel must remain usable afterward.
    // --------------------------------------------------------------------------------
    [Fact]
    public async Task MultiFrame_Receive_Faults_On_NCr_Timeout_And_Channel_Remains_Usable()
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);

        var epRecv = IsoTpEndpoint.Normal(txCanId: 0x7E8, rxCanId: 0x7E0);
        var epPeer = IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8);

        var opts = FastOptions(nCr: TimeSpan.FromMilliseconds(120));
        using var receiver = IsoTpFactory.Open(busB, epRecv, opts);

        var bgFault = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.BackgroundExceptionOccurred += (_, ex) => bgFault.TrySetResult(ex);

        var recvTask = receiver.ReceiveAsync(new CancellationTokenSource(ShortTimeout).Token);

        // Peer sends FF for a multi-frame PDU, then never sends CFs -> receiver arms N_Cr and
        // must abort with IsoTpTimeoutException rather than leaving ReceiveAsync hung.
        byte[] ffPayload = Enumerable.Range(0, 20).Select(i => (byte)i).ToArray();
        int ffData = IsoTpFrameCodec.FirstFrameMaxDataLength(isCanFd: false, usesAddressExtension: false, useLongLength: false);
        var ff = IsoTpFrameCodec.BuildFirstFrame(epPeer, ffPayload.Length, ffPayload.AsSpan(0, ffData), isCanFd: false);
        busA.Transmit(CanFrame.Classic(unchecked((int)epPeer.TxCanId), ff));

        Func<Task> act = () => recvTask;
        var timeout = (await act.Should().ThrowAsync<IsoTpTimeoutException>()).Which;
        timeout.Timer.Should().Be(IsoTpTimer.NCr);

        var bg = await bgFault.Task.WaitAsync(ShortTimeout);
        bg.Should().BeOfType<IsoTpTimeoutException>()
            .Which.Timer.Should().Be(IsoTpTimer.NCr);

        // Channel remains usable for a subsequent SF after the abort.
        using var sender = IsoTpFactory.Open(busA, epPeer, FastOptions());
        var recv2 = receiver.ReceiveAsync(new CancellationTokenSource(ShortTimeout).Token);
        byte[] ok = { 0x22, 0xF1, 0x90 };
        await sender.SendAsync(ok);
        (await recv2).Should().Equal(ok);
    }

    // --------------------------------------------------------------------------------
    // Bugbot 3596134684 — CF sequence-number mismatch aborts reassembly and faults
    // ReceiveAsync (same FailTx-style path as N_Cr).
    // --------------------------------------------------------------------------------
    [Fact]
    public async Task MultiFrame_Receive_Faults_On_SequenceNumber_Mismatch()
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);

        var epRecv = IsoTpEndpoint.Normal(txCanId: 0x318, rxCanId: 0x310);
        var epPeer = IsoTpEndpoint.Normal(txCanId: 0x310, rxCanId: 0x318);

        using var receiver = IsoTpFactory.Open(busB, epRecv, FastOptions());

        // Wait until the receiver has answered the FF with FC before injecting a bad CF.
        var fcSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        busA.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != unchecked((int)epRecv.TxCanId)) return;
            var data = e.CanFrame.Data.ToArray();
            if (data.Length > 0 && (data[0] >> 4) == 0x3)
                fcSeen.TrySetResult(true);
        };

        var recvTask = receiver.ReceiveAsync(new CancellationTokenSource(ShortTimeout).Token);

        byte[] ffPayload = Enumerable.Range(0, 20).Select(i => (byte)(i + 1)).ToArray();
        int ffData = IsoTpFrameCodec.FirstFrameMaxDataLength(isCanFd: false, usesAddressExtension: false, useLongLength: false);
        var ff = IsoTpFrameCodec.BuildFirstFrame(epPeer, ffPayload.Length, ffPayload.AsSpan(0, ffData), isCanFd: false);
        busA.Transmit(CanFrame.Classic(unchecked((int)epPeer.TxCanId), ff));

        await fcSeen.Task.WaitAsync(ShortTimeout);

        // Expected SN after FF is 1; send SN=2 to force mismatch abort.
        byte[] chunk = { 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16 };
        var badCf = IsoTpFrameCodec.BuildConsecutiveFrame(epPeer, sequenceNumber: 2, chunk,
            isCanFd: false, padding: true);
        busA.Transmit(CanFrame.Classic(unchecked((int)epPeer.TxCanId), badCf));

        Func<Task> act = () => recvTask;
        (await act.Should().ThrowAsync<IsoTpException>())
            .WithMessage("*sequence-number mismatch*");
    }

    // --------------------------------------------------------------------------------
    // Bugbot 3596527680 — a superseding SF / FF (or FF→OVFLW) must AbortRx so a blocked
    // ReceiveAsync does not hang after silent _rx clear.
    // --------------------------------------------------------------------------------
    [Fact]
    public async Task MultiFrame_Receive_Faults_When_Superseded_By_SingleFrame()
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);

        var epRecv = IsoTpEndpoint.Normal(txCanId: 0x338, rxCanId: 0x330);
        var epPeer = IsoTpEndpoint.Normal(txCanId: 0x330, rxCanId: 0x338);

        using var receiver = IsoTpFactory.Open(busB, epRecv, FastOptions());

        var bgFault = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.BackgroundExceptionOccurred += (_, ex) => bgFault.TrySetResult(ex);

        var fcSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        busA.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != unchecked((int)epRecv.TxCanId)) return;
            var data = e.CanFrame.Data.ToArray();
            if (data.Length > 0 && (data[0] >> 4) == 0x3)
                fcSeen.TrySetResult(true);
        };

        var recvTask = receiver.ReceiveAsync(new CancellationTokenSource(ShortTimeout).Token);

        byte[] ffPayload = Enumerable.Range(0, 20).Select(i => (byte)i).ToArray();
        int ffData = IsoTpFrameCodec.FirstFrameMaxDataLength(isCanFd: false, usesAddressExtension: false, useLongLength: false);
        var ff = IsoTpFrameCodec.BuildFirstFrame(epPeer, ffPayload.Length, ffPayload.AsSpan(0, ffData), isCanFd: false);
        busA.Transmit(CanFrame.Classic(unchecked((int)epPeer.TxCanId), ff));
        await fcSeen.Task.WaitAsync(ShortTimeout);

        // Racing SF aborts the half-built multi-frame; waiter must see the abort fault first.
        var sf = IsoTpFrameCodec.BuildSingleFrame(epPeer, new byte[] { 0x11, 0x22 },
            isCanFd: false, padding: true);
        busA.Transmit(CanFrame.Classic(unchecked((int)epPeer.TxCanId), sf));

        Func<Task> act = () => recvTask;
        (await act.Should().ThrowAsync<IsoTpException>())
            .WithMessage("*Single Frame aborted in-flight*");

        var bg = await bgFault.Task.WaitAsync(ShortTimeout);
        bg.Should().BeOfType<IsoTpException>().Which.Message.Should().Contain("Single Frame aborted");

        // The superseding SF is still delivered as the next PDU.
        var next = await receiver.ReceiveAsync(new CancellationTokenSource(ShortTimeout).Token);
        next.Should().Equal(0x11, 0x22);
    }

    [Fact]
    public async Task MultiFrame_Receive_Faults_When_Superseded_By_FirstFrame_Then_Overflow()
    {
        var session = NewSession();
        // CAN-FD bus so the escape-form oversized FF is delivered; channel stays classic-capped
        // (UseCanFd=false → MaxPduLength=4095) so the escape length triggers OVFLW.
        using var busA = OpenCanFd(session, 0);
        using var busB = OpenCanFd(session, 1);

        var epRecv = IsoTpEndpoint.Normal(txCanId: 0x348, rxCanId: 0x340);
        var epPeer = IsoTpEndpoint.Normal(txCanId: 0x340, rxCanId: 0x348);

        using var receiver = IsoTpFactory.Open(busB, epRecv, FastOptions());

        var fcSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int overflowFc = 0;
        busA.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != unchecked((int)epRecv.TxCanId)) return;
            var data = e.CanFrame.Data.ToArray();
            if (data.Length == 0) return;
            if ((data[0] >> 4) == 0x3)
            {
                if ((data[0] & 0x0F) == (byte)FlowStatus.Overflow)
                    Interlocked.Increment(ref overflowFc);
                else
                    fcSeen.TrySetResult(true);
            }
        };

        var recvTask = receiver.ReceiveAsync(new CancellationTokenSource(ShortTimeout).Token);

        // Start a valid multi-frame reception so _rx + N_Cr are armed (short-form FF on FD bus).
        byte[] ffPayload = Enumerable.Range(0, 20).Select(i => (byte)(i + 3)).ToArray();
        int ffData = IsoTpFrameCodec.FirstFrameMaxDataLength(isCanFd: false, usesAddressExtension: false, useLongLength: false);
        var ff = IsoTpFrameCodec.BuildFirstFrame(epPeer, ffPayload.Length, ffPayload.AsSpan(0, ffData), isCanFd: false);
        busA.Transmit(CanFrame.Fd(unchecked((int)epPeer.TxCanId), ff));
        await fcSeen.Task.WaitAsync(ShortTimeout);

        // Oversized escape FF supersedes then refuses with OVFLW — no replacement session.
        byte[] hugeFf =
        {
            0x10, 0x00,
            0x01, 0x00, 0x00, 0x00, // length = 16_777_216
            0x00, 0x00,
        };
        busA.Transmit(CanFrame.Fd(unchecked((int)epPeer.TxCanId), hugeFf));

        Func<Task> act = () => recvTask;
        (await act.Should().ThrowAsync<IsoTpException>())
            .WithMessage("*First Frame aborted in-flight*");

        for (int i = 0; i < 50 && Volatile.Read(ref overflowFc) == 0; i++)
            await Task.Delay(20);
        overflowFc.Should().Be(1, "superseding oversized FF must still reply FC(OVFLW)");

        // Channel remains usable for a subsequent SF.
        var recv2 = receiver.ReceiveAsync(new CancellationTokenSource(ShortTimeout).Token);
        var okSf = IsoTpFrameCodec.BuildSingleFrame(epPeer, new byte[] { 0x55 },
            isCanFd: false, padding: true);
        busA.Transmit(CanFrame.Fd(unchecked((int)epPeer.TxCanId), okSf));
        (await recv2).Should().Equal(0x55);
    }

    // --------------------------------------------------------------------------------
    // Bugbot 3596378393 — an empty CF (PCI only, zero user bytes) must not advance
    // ExpectedSn / BS / N_Cr; a subsequent valid CF with the same SN must still complete
    // reassembly instead of mismatch-aborting or stalling until N_Cr.
    // --------------------------------------------------------------------------------
    [Fact]
    public async Task MultiFrame_Receive_Ignores_Empty_ConsecutiveFrame_Without_Advancing_Sn()
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);

        var epRecv = IsoTpEndpoint.Normal(txCanId: 0x328, rxCanId: 0x320);
        var epPeer = IsoTpEndpoint.Normal(txCanId: 0x320, rxCanId: 0x328);

        using var receiver = IsoTpFactory.Open(busB, epRecv, FastOptions());

        var fcSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        busA.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != unchecked((int)epRecv.TxCanId)) return;
            var data = e.CanFrame.Data.ToArray();
            if (data.Length > 0 && (data[0] >> 4) == 0x3)
                fcSeen.TrySetResult(true);
        };

        var recvTask = receiver.ReceiveAsync(new CancellationTokenSource(ShortTimeout).Token);

        // 13 bytes => FF carries 6, one CF carries the remaining 7 (classic CAN).
        byte[] ffPayload = Enumerable.Range(0, 13).Select(i => (byte)(i + 0x40)).ToArray();
        int ffData = IsoTpFrameCodec.FirstFrameMaxDataLength(isCanFd: false, usesAddressExtension: false, useLongLength: false);
        var ff = IsoTpFrameCodec.BuildFirstFrame(epPeer, ffPayload.Length, ffPayload.AsSpan(0, ffData), isCanFd: false);
        busA.Transmit(CanFrame.Classic(unchecked((int)epPeer.TxCanId), ff));

        await fcSeen.Task.WaitAsync(ShortTimeout);

        // PCI-only CF (SN=1) with no user data — previously advanced ExpectedSn to 2.
        var emptyCf = IsoTpFrameCodec.BuildConsecutiveFrame(epPeer, sequenceNumber: 1,
            ReadOnlySpan<byte>.Empty, isCanFd: false, padding: false);
        busA.Transmit(CanFrame.Classic(unchecked((int)epPeer.TxCanId), emptyCf));

        // Valid CF still carrying SN=1 must complete reassembly.
        byte[] chunk = ffPayload.AsSpan(ffData).ToArray();
        chunk.Length.Should().Be(7);
        var goodCf = IsoTpFrameCodec.BuildConsecutiveFrame(epPeer, sequenceNumber: 1, chunk,
            isCanFd: false, padding: true);
        busA.Transmit(CanFrame.Classic(unchecked((int)epPeer.TxCanId), goodCf));

        (await recvTask).Should().Equal(ffPayload);
    }

    // --------------------------------------------------------------------------------
    // A PDU longer than this channel's frame kind can address is rejected by SendAsync's own
    // pre-check, before the send-gate is taken and before anything reaches the actor. That is
    // the entire behaviour here, so it is asserted exactly: the specific exception, the
    // parameter it names, and that nothing was put on the wire.
    //
    // This test used to be named for the codec throw inside BeginSendOnLoop (Bugbot 3594960783)
    // and accepted any of three exception types. It never reached that code: SendAsync's
    // pre-check enforces the same limit the codec does (MaxClassicFirstFrameLength = 4095), so
    // an oversized PDU is refused two layers above the actor, and "any of three exception types"
    // could not distinguish the layer it came from. The actor-side failure contract that Bugbot
    // finding is about is asserted by the next test, over a failure that is actually reachable.
    // --------------------------------------------------------------------------------
    [Fact]
    public async Task SendAsync_Rejects_Oversized_Pdu_Before_Anything_Reaches_The_Bus()
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);

        var epAB = IsoTpEndpoint.Normal(0x210, 0x211);
        var epBA = IsoTpEndpoint.Normal(0x211, 0x210);

        using var sender = IsoTpFactory.Open(busA, epAB, FastOptions());
        using var receiver = IsoTpFactory.Open(busB, epBA, FastOptions());

        var framesOnWire = 0;
        busB.FrameObserved += (_, _) => Interlocked.Increment(ref framesOnWire);

        // One byte more than the 12-bit classic First-Frame length field can announce.
        byte[] oversized = new byte[IsoTpFrameCodec.MaxClassicFirstFrameLength + 1];

        Func<Task> act = () => sender.SendAsync(oversized).WaitAsync(ShortTimeout);
        var thrown = (await act.Should().ThrowAsync<ArgumentOutOfRangeException>()).Which;
        thrown.ParamName.Should().Be("pdu");

        Volatile.Read(ref framesOnWire).Should().Be(0,
            "the length check runs before the send-gate, so no frame is ever built or transmitted");

        // The rejected call must not have consumed the send-gate: a normal send still works.
        var recvTask = receiver.ReceiveAsync(new CancellationTokenSource(ShortTimeout).Token);
        byte[] normal = { 0x11, 0x22, 0x33 };
        await sender.SendAsync(normal).WaitAsync(ShortTimeout);
        (await recvTask).Should().Equal(normal);
    }

    // --------------------------------------------------------------------------------
    // Bugbot 3594960783 (HIGH), the reachable half — when a send fails *after* the actor has
    // taken ownership of the PDU (here: the bus layer throws out of SendConfirmed), the channel
    // must (1) fault the awaiting SendAsync with that exact exception, (2) release the send-gate
    // and clear _tx, and (3) stay usable — rather than reporting the failure only through
    // BackgroundExceptionOccurred and hanging every future SendAsync behind the gate.
    //
    // The failure is injected at the bus layer because that is the only way in: SendAsync's
    // pre-check duplicates the codec's own length limit, so no PDU that reaches BeginSendOnLoop
    // can make the codec throw. Asserting the exact exception is what gives this test teeth --
    // a channel that swallowed it, rewrote it as a generic IsoTpException, or completed the send
    // anyway all fail here.
    // --------------------------------------------------------------------------------
    [Fact]
    public async Task Send_Faults_With_The_Bus_Layer_Exception_And_Channel_Remains_Usable()
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);

        var epAB = IsoTpEndpoint.Normal(0x210, 0x211);
        var epBA = IsoTpEndpoint.Normal(0x211, 0x210);

        using var svcA = new CanBusService(busA);
        var failing = new ThrowOnFirstConfirmService(svcA, new InvalidOperationException("bus-layer boom"));

        using var sender = IsoTpFactory.Open(failing, epAB, FastOptions(), leaveOpen: true);
        using var receiver = IsoTpFactory.Open(busB, epBA, FastOptions());

        Func<Task> act = () => sender.SendAsync(new byte[] { 1, 2, 3 }).WaitAsync(ShortTimeout);
        var thrown = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        thrown.Message.Should().Be("bus-layer boom",
            "the failure the bus layer reported must reach the caller unrewritten");

        // Gate free and _tx cleared: the very next send goes through end to end.
        // Owned rather than inline, so the timer it holds is released when the test ends and
        // not when the finalizer gets round to it (CodeQL cs/local-not-disposed).
        using var recvCts = new CancellationTokenSource(ShortTimeout);
        var recvTask = receiver.ReceiveAsync(recvCts.Token);
        byte[] normal = { 0x11, 0x22, 0x33 };
        await sender.SendAsync(normal).WaitAsync(ShortTimeout);
        (await recvTask).Should().Equal(normal);
    }

    // --------------------------------------------------------------------------------
    // Bugbot 3594960794 (HIGH) — a SendAsync whose token is cancelled AFTER BeginSendOnLoop
    // is posted but BEFORE the actor picks it up must NOT emit any CAN frame. Under the bug,
    // BeginSendOnLoop just plowed ahead and pushed a Single-Frame onto the wire even though
    // the TCS had already (or was about to be) cancelled.
    //
    // We arrange the race deterministically by parking the sender's ProtocolActor mailbox
    // directly (Post a blocking work item). DatagramReceived is raised off-actor
    // (Bugbot 3596580061), so the older "throw in DatagramReceived + block in
    // BackgroundExceptionOccurred" trick no longer freezes the actor and let begin race
    // ahead of Cancel on CI.
    // --------------------------------------------------------------------------------
    [Fact]
    public async Task Send_Cancelled_Before_Actor_Delivery_Emits_No_Frame_And_Channel_Remains_Usable()
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);

        var epAB = IsoTpEndpoint.Normal(0x220, 0x221);
        var epBA = IsoTpEndpoint.Normal(0x221, 0x220);

        using var sender = IsoTpFactory.Open(busA, epAB, FastOptions());
        using var receiver = IsoTpFactory.Open(busB, epBA, FastOptions());

        // Frame counter: was the cancelled SF payload ever put on the wire?
        int framesToPeer = 0;
        busB.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID == 0x220) Interlocked.Increment(ref framesToPeer);
        };

        var actorField = sender.GetType().GetField("_actor",
            BindingFlags.Instance | BindingFlags.NonPublic);
        actorField.Should().NotBeNull("IsoTpChannel must keep an _actor field for this race test");
        var actor = (IProtocolActor)actorField!.GetValue(sender)!;

        using var actorParked = new ManualResetEventSlim(false);
        using var releaseActor = new ManualResetEventSlim(false);
        actor.Post(() =>
        {
            actorParked.Set();
            releaseActor.Wait(TimeSpan.FromSeconds(10));
        });
        actorParked.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue(
            "the sender's actor must be sitting inside our parked work item by now");

        // Sender's actor is now blocked. Queue BeginSendOnLoop, then cancel the token (which
        // synchronously completes the send TCS and posts actor-side TX cleanup). Begin +
        // cleanup sit in the mailbox until we release.
        using var cts = new CancellationTokenSource();
        var sendTask = sender.SendAsync(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, cts.Token);
        cts.Cancel();

        // Release the parked actor; it now drains [begin, cleanup]. Under the fix, begin sees
        // tcs.Task.IsCompleted / ct.IsCancellationRequested and emits nothing.
        releaseActor.Set();

        Func<Task> act = () => sendTask.WaitAsync(ShortTimeout);
        await act.Should().ThrowAsync<OperationCanceledException>();

        // Give any straggling actor work time to (incorrectly) hit the wire under the bug.
        await Task.Delay(100);
        framesToPeer.Should().Be(0,
            "a send cancelled before the actor delivers BeginSendOnLoop must never put a frame on the bus");

        // One-in-flight guarantee: the send gate must be released and the actor usable.
        var recvTask2 = receiver.ReceiveAsync(new CancellationTokenSource(ShortTimeout).Token);
        byte[] normal2 = { 0x01, 0x02, 0x03 };
        await sender.SendAsync(normal2).WaitAsync(ShortTimeout);
        (await recvTask2).Should().Equal(normal2);
    }

    // --------------------------------------------------------------------------------
    // Bugbot 3594960802 (MEDIUM) — Extended addressing round-trip when sourceAddress and
    // targetAddress DIFFER. Under the bug, IsoTpEndpoint.Extended stored only the target
    // address as AddressExtension, so the RX filter compared inbound frames against the
    // outbound TX address-extension byte and dropped every legitimate reply.
    // --------------------------------------------------------------------------------
    [Fact]
    public async Task ExtendedAddressing_With_Distinct_Source_And_Target_Round_Trips()
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);

        // Alice: SA=0xAA, TA=0xBB. Alice's outbound AE=0xBB; Alice expects inbound AE=0xAA.
        // Bob:   SA=0xBB, TA=0xAA. Bob's outbound AE=0xAA;   Bob expects inbound AE=0xBB.
        var alice = IsoTpEndpoint.Extended(txCanId: 0x300, rxCanId: 0x301,
            sourceAddress: 0xAA, targetAddress: 0xBB);
        var bob = IsoTpEndpoint.Extended(txCanId: 0x301, rxCanId: 0x300,
            sourceAddress: 0xBB, targetAddress: 0xAA);

        // Sanity-check the endpoint values themselves so the test still catches the bug even if
        // the runtime later stops using RxAddressExtension.
        alice.AddressExtension.Should().Be(0xBB);
        alice.RxAddressExtension.Should().Be(0xAA);
        bob.AddressExtension.Should().Be(0xAA);
        bob.RxAddressExtension.Should().Be(0xBB);

        using var sender = IsoTpFactory.Open(busA, alice, FastOptions());
        using var receiver = IsoTpFactory.Open(busB, bob, FastOptions());

        // A -> B: Alice writes AE=0xBB, Bob expects AE=0xBB -> match.
        var recvOnBob = receiver.ReceiveAsync(new CancellationTokenSource(ShortTimeout).Token);
        byte[] a2b = { 0xC0, 0xDE };
        await sender.SendAsync(a2b);
        (await recvOnBob).Should().Equal(a2b);

        // B -> A: Bob writes AE=0xAA, Alice expects AE=0xAA -> match.
        // Under the bug Alice's RX filter compared against 0xBB (target) and dropped the frame.
        var recvOnAlice = sender.ReceiveAsync(new CancellationTokenSource(ShortTimeout).Token);
        byte[] b2a = { 0xBE, 0xEF };
        await receiver.SendAsync(b2a);
        (await recvOnAlice).Should().Equal(b2a);
    }

    // --------------------------------------------------------------------------------
    // Bugbot 3596468541 (MEDIUM) — Dispose must not tear down _sendGate while an in-flight
    // SendAsync still holds it. Otherwise Release in SendAsync's finally throws
    // ObjectDisposedException (or worse) instead of a clean ObjectDisposedException from FailTx.
    // --------------------------------------------------------------------------------
    [Fact]
    public async Task Dispose_During_InFlight_Send_Does_Not_Race_SendGate_Release()
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);

        var epAB = IsoTpEndpoint.Normal(0x240, 0x241);

        using var inner = new CanBusService(busA);
        using var holdConfirm = new SemaphoreSlim(0, 1);
        using var confirmStarted = new ManualResetEventSlim(false);
        var delaying = new DelayingConfirmService(inner, holdConfirm, confirmStarted);
        var sender = IsoTpFactory.Open(delaying, epAB, FastOptions(), leaveOpen: true);

        var sendTask = sender.SendAsync(new byte[] { 0xAA, 0xBB });
        confirmStarted.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue(
            "SendConfirmed must be parked so Dispose races an in-flight SendAsync");

        // Dispose while SendAsync still holds _sendGate (awaiting confirmation / idle drain).
        Action dispose = () => sender.Dispose();
        dispose.Should().NotThrow("Dispose must wait out the send-gate holder before disposing it");

        holdConfirm.Release();

        Func<Task> send = () => sendTask.WaitAsync(ShortTimeout);
        // Clean shutdown: FailTx's ObjectDisposedException, not a secondary ODE from Release.
        (await send.Should().ThrowAsync<ObjectDisposedException>())
            .Which.ObjectName.Should().Be("IsoTpChannel");
    }

    // --------------------------------------------------------------------------------
    // Bugbot 3596212788 (HIGH) — cancelling SendAsync must not release _sendGate while a
    // SendConfirmed started by SendFrameOnBus is still outstanding. Otherwise a subsequent
    // SendAsync can put a new PDU on the wire while the aborted PDU's frame is still TX'ing.
    //
    // Arrangement: wrap the bus service so the first SendConfirmed blocks until we release it;
    // cancel the in-flight SF send, start a second SendAsync concurrently, and assert the
    // second PDU does not appear on the peer until the first confirmation is released.
    // --------------------------------------------------------------------------------
    [Fact]
    public async Task Cancelled_Send_Holds_Gate_Until_InFlight_Bus_Tx_Completes()
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);

        var epAB = IsoTpEndpoint.Normal(0x230, 0x231);
        var epBA = IsoTpEndpoint.Normal(0x231, 0x230);

        using var inner = new CanBusService(busA);
        using var holdFirstConfirm = new SemaphoreSlim(0, 1);
        using var firstConfirmStarted = new ManualResetEventSlim(false);
        var delaying = new DelayingConfirmService(inner, holdFirstConfirm, firstConfirmStarted);
        using var sender = IsoTpFactory.Open(delaying, epAB, FastOptions(), leaveOpen: true);
        using var receiver = IsoTpFactory.Open(busB, epBA, FastOptions());

        int framesToPeer = 0;
        byte? lastSfDl = null;
        busB.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != 0x230) return;
            var payload = e.CanFrame.Data.ToArray();
            if (payload.Length == 0) return;
            if ((payload[0] >> 4) == 0x0) // SF
            {
                Interlocked.Increment(ref framesToPeer);
                lastSfDl = (byte)(payload[0] & 0x0F);
            }
        };

        using var cts = new CancellationTokenSource();
        var cancelledSend = sender.SendAsync(new byte[] { 0xAA, 0xBB }, cts.Token);

        firstConfirmStarted.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue(
            "the first SendConfirmed must be parked inside our delaying wrapper");

        cts.Cancel();

        // cancelledSend awaits WaitForBusTxIdleAsync after the OCE, so it must NOT complete while
        // we still hold the first SendConfirmed — that is the gate-hold under test.
        var cancelFinished = cancelledSend.WaitAsync(TimeSpan.FromMilliseconds(200));
        Func<Task> stillHeld = () => cancelFinished;
        await stillHeld.Should().ThrowAsync<TimeoutException>(
            "cancelled SendAsync must keep the send gate until its in-flight SendConfirmed finishes");

        // Second send starts while the aborted PDU's SendConfirmed is still held. Under the bug
        // the gate is already free and this SF (DL=3) hits the bus immediately; under the fix it
        // must wait until we release the first confirmation.
        var secondSend = sender.SendAsync(new byte[] { 0x11, 0x22, 0x33 });
        await Task.Delay(100);
        framesToPeer.Should().Be(0,
            "no SF may hit the peer while the aborted send's SendConfirmed is still parked");

        holdFirstConfirm.Release();

        Func<Task> cancelled = () => cancelledSend.WaitAsync(ShortTimeout);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        await secondSend.WaitAsync(ShortTimeout);

        // Both SFs eventually appear: the already-submitted cancelled frame, then the follow-up.
        for (int i = 0; i < 50 && Volatile.Read(ref framesToPeer) < 2; i++)
            await Task.Delay(20);
        framesToPeer.Should().Be(2, "follow-up SendAsync must TX only after the aborted bus TX drains");
        lastSfDl.Should().Be(3);
    }

    // --------------------------------------------------------------------------------
    // Bugbot 3597312227 (HIGH) — negative LocalStMin used to throw EncodeStMin on the actor
    // loop when building FC after FF, so _rx/N_Cr never started and ReceiveAsync hung. Open
    // must now reject the options up front.
    // --------------------------------------------------------------------------------
    [Fact]
    public void Open_With_Negative_LocalStMin_Throws()
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);

        var opts = FastOptions(localStMin: TimeSpan.FromMilliseconds(-1));
        Action act = () => IsoTpFactory.Open(busA, IsoTpEndpoint.Normal(0x250, 0x251), opts);
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("value");
    }

    // --------------------------------------------------------------------------------
    // Bugbot 3597408323 (HIGH) — FC arriving while the last CF of a block awaits TX-confirm
    // (State==SendingCf) must not be dropped. Under the bug the sender entered WaitFcBlock,
    // armed N_Bs, and timed out even though the peer had already answered.
    // --------------------------------------------------------------------------------
    [Fact]
    public async Task MultiFrame_Send_Accepts_FlowControl_Arriving_During_Last_Cf_Confirm()
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);

        var epAB = IsoTpEndpoint.Normal(0x260, 0x261);
        var epBA = IsoTpEndpoint.Normal(0x261, 0x260);

        using var inner = new CanBusService(busA);
        using var holdCfConfirm = new SemaphoreSlim(0, 8);
        using var cfConfirmParked = new ManualResetEventSlim(false);
        // Hold only CF TX-confirms (after the frame is on the wire). FF confirms normally so the
        // peer can answer the initial FC; the bug window is SendingCf for the last CF of a block.
        var delaying = new HoldConsecutiveFrameConfirmService(inner, holdCfConfirm, cfConfirmParked);
        using var sender = IsoTpFactory.Open(delaying, epAB,
            FastOptions(nBs: TimeSpan.FromMilliseconds(300)), leaveOpen: true);

        // Manual peer: CTS with BS=1 after FF and after each CF. FC is sent while CF confirm is
        // still parked so it lands in State==SendingCf (the drop window Bugbot flagged).
        busB.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != 0x260) return;
            var payload = e.CanFrame.Data.ToArray();
            if (payload.Length == 0) return;
            int type = payload[0] >> 4;
            if (type is 0x1 or 0x2) // FF or CF
            {
                var fc = IsoTpFrameCodec.BuildFlowControl(epBA, FlowStatus.ClearToSend,
                    blockSize: 1, stMinRaw: 0, isCanFd: false, padding: true);
                busB.Transmit(CanFrame.Classic(0x261, fc));
            }
        };

        // 20 bytes classic: FF(6) + CF1(7) + CF2(7). BS=1 => wait for FC after FF and after CF1.
        byte[] pdu = Enumerable.Range(0, 20).Select(i => (byte)(i + 1)).ToArray();
        var sendTask = sender.SendAsync(pdu);

        // Two block-ending CFs (CF1 then CF2): for each, wait until confirm is parked (FC already
        // sent by the peer handler above), then release so deferred FC is applied.
        for (int i = 0; i < 2; i++)
        {
            cfConfirmParked.Wait(TimeSpan.FromSeconds(3)).Should().BeTrue(
                $"CF confirm #{i + 1} must park after transmit so peer FC can defer");
            cfConfirmParked.Reset();
            await Task.Delay(30); // ensure peer FC is processed into DeferredFcs
            holdCfConfirm.Release();
        }

        // Under the bug this times out on N_Bs; under the fix deferred FC resumes the block.
        await sendTask.WaitAsync(ShortTimeout);
    }

    // --------------------------------------------------------------------------------
    // Bugbot 3597408331 (HIGH) — multiple Wait FCs during FF TX-confirm must each count
    // toward WftMax. A single DeferredFc slot used to keep only the last Wait.
    // --------------------------------------------------------------------------------
    [Fact]
    public async Task MultiFrame_Send_Counts_Wait_FlowControls_Deferred_During_Ff_Confirm()
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);

        var epAB = IsoTpEndpoint.Normal(0x270, 0x271);
        var epBA = IsoTpEndpoint.Normal(0x271, 0x270);

        int wftMax = 2;
        using var inner = new CanBusService(busA);
        using var holdConfirm = new SemaphoreSlim(0, 1);
        using var confirmStarted = new ManualResetEventSlim(false);
        var delaying = new DelayingConfirmService(inner, holdConfirm, confirmStarted,
            holdAfterTransmit: true);
        using var sender = IsoTpFactory.Open(delaying, epAB,
            FastOptions(nBs: TimeSpan.FromSeconds(2), wftMax: wftMax), leaveOpen: true);

        var ffSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        busB.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != 0x270) return;
            var payload = e.CanFrame.Data.ToArray();
            if (payload.Length > 0 && (payload[0] >> 4) == 0x1)
                ffSeen.TrySetResult(true);
        };

        byte[] pdu = Enumerable.Range(0, 30).Select(i => (byte)i).ToArray();
        var sendTask = sender.SendAsync(pdu);

        confirmStarted.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue(
            "FF SendConfirmed must be parked so Wait FCs arrive during SingleOrFirstInFlight");
        await ffSeen.Task.WaitAsync(ShortTimeout);

        // Pump WftMax+1 Wait FCs while FF confirm is still held — all must be queued and
        // counted when confirm completes (under the bug only the last Wait survived).
        for (int i = 0; i < wftMax + 1; i++)
        {
            var fc = IsoTpFrameCodec.BuildFlowControl(epBA, FlowStatus.Wait,
                blockSize: 0, stMinRaw: 0, isCanFd: false, padding: true);
            busB.Transmit(CanFrame.Classic(0x271, fc));
            await Task.Delay(20);
        }

        holdConfirm.Release();

        Func<Task> act = () => sendTask.WaitAsync(ShortTimeout);
        var ex = (await act.Should().ThrowAsync<IsoTpWaitFrameLimitExceededException>()).Which;
        ex.Limit.Should().Be(wftMax);
        ex.WaitFramesReceived.Should().BeGreaterThan(wftMax);
    }

    // --------------------------------------------------------------------------------
    // Bugbot 3596212802 (MEDIUM) — HandleRxFirstFrame must not allocate new byte[pci.Length]
    // from an uncapped FF length. A classic-configured channel (MaxPduLength=4095) must reply
    // FC(OVFLW) when a CAN-FD escape FF announces a larger PDU. The frame itself must be FD:
    // TryParsePci rejects the escape header on classic frames (develop codec API).
    // --------------------------------------------------------------------------------
    [Fact]
    public async Task Rx_FirstFrame_Above_Classic_Max_Sends_Overflow_And_Does_Not_Allocate()
    {
        var session = NewSession();
        // FD bus delivers the escape-form FF; classic channel options keep MaxPduLength at 4095.
        using var busA = OpenCanFd(session, 0);
        using var busB = OpenCanFd(session, 1);

        var epRecv = IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8);
        using var receiver = IsoTpFactory.Open(busA, epRecv, FastOptions());

        int overflowFc = 0;
        busB.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != 0x7E0) return;
            var payload = e.CanFrame.Data.ToArray();
            if (payload.Length >= 1 && (payload[0] >> 4) == 0x3
                && (payload[0] & 0x0F) == (byte)FlowStatus.Overflow)
            {
                Interlocked.Increment(ref overflowFc);
            }
        };

        // CAN-FD escape FF announcing 0x01000000 bytes — far above classic MaxClassicFirstFrameLength.
        // Under the bug this would attempt new byte[0x01000000] (or worse with int.MaxValue).
        byte[] hugeFf =
        {
            0x10, 0x00,
            0x01, 0x00, 0x00, 0x00, // length = 16_777_216
            0x00, 0x00,
        };
        busB.Transmit(CanFrame.Fd(0x7E8, hugeFf));

        for (int i = 0; i < 50 && Volatile.Read(ref overflowFc) == 0; i++)
            await Task.Delay(20);
        overflowFc.Should().Be(1, "receiver must reply FC(OVFLW) without allocating the announced buffer");

        // Channel remains usable for a normal SF afterwards.
        var recvTask = receiver.ReceiveAsync(new CancellationTokenSource(ShortTimeout).Token);
        byte[] okSf = { 0x01, 0x99 }; // SF DL=1, data 0x99 — build via peer transmit
        busB.Transmit(CanFrame.Fd(0x7E8, okSf));
        (await recvTask).Should().Equal(0x99);
    }

    // --------------------------------------------------------------------------------
    // Bugbot 3594958445 / 3596212802 — on classic CAN the CAN-FD First-Frame escape form
    // (0x10 0x00 + 4-byte length) is invalid and must be rejected in TryParsePci, not
    // mis-parsed as a huge FF_DL that would allocate before FC(OVFLW). Silent drop keeps
    // the channel usable. Complements the FD-bus OVFLW test above (per-frame isCanFd).
    // --------------------------------------------------------------------------------
    [Fact]
    public async Task Rx_Classic_Rejects_CanFd_Escape_FirstFrame_Without_Allocating()
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);

        var epRecv = IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8);
        using var receiver = IsoTpFactory.Open(busA, epRecv, FastOptions());

        int anyFc = 0;
        busB.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != 0x7E0) return;
            var payload = e.CanFrame.Data.ToArray();
            if (payload.Length >= 1 && (payload[0] >> 4) == 0x3)
                Interlocked.Increment(ref anyFc);
        };

        // CAN-FD escape FF announcing 0x01000000 bytes. Classic TryParsePci must reject it
        // (isCanFd: false) so HandleRxFirstFrame never sees pci.Length = 16_777_216.
        byte[] hugeFf =
        {
            0x10, 0x00,
            0x01, 0x00, 0x00, 0x00, // length = 16_777_216
            0x00, 0x00,
        };
        busB.Transmit(CanFrame.Classic(0x7E8, hugeFf));

        await Task.Delay(100);
        anyFc.Should().Be(0, "classic channels must drop CAN-FD escape FFs without FC reply");

        // Channel remains usable for a normal SF afterwards.
        var recvTask = receiver.ReceiveAsync(new CancellationTokenSource(ShortTimeout).Token);
        byte[] okSf = { 0x01, 0x99 }; // SF DL=1, data 0x99 — build via peer transmit
        busB.Transmit(CanFrame.Classic(0x7E8, okSf));
        (await recvTask).Should().Equal(0x99);
    }

    // --------------------------------------------------------------------------------
    // FR-TP-002 (#27, ISO 15765-2 §9.8): a Consecutive Frame whose CAN_DL is not the First
    // Frame's RX_DL — unless it is the last one — is ignored, not accepted with fewer bytes.
    // Under the bug the shortened CF's 4 bytes were copied and everything after them shifted;
    // the conforming CF that follows under the same SN completes the PDU intact.
    // --------------------------------------------------------------------------------
    [Fact]
    public async Task MultiFrame_Receive_Ignores_A_Shortened_ConsecutiveFrame_That_Is_Not_The_Last()
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);

        var epRecv = IsoTpEndpoint.Normal(txCanId: 0x328, rxCanId: 0x320);
        var epPeer = IsoTpEndpoint.Normal(txCanId: 0x320, rxCanId: 0x328);
        using var receiver = IsoTpFactory.Open(busB, epRecv, FastOptions());

        var fcSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        busA.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != unchecked((int)epRecv.TxCanId)) return;
            var data = e.CanFrame.Data.ToArray();
            if (data.Length > 0 && (data[0] >> 4) == 0x3) fcSeen.TrySetResult(true);
        };
        var recvTask = receiver.ReceiveAsync(new CancellationTokenSource(ShortTimeout).Token);

        // 20 bytes => FF carries 6, CF1 carries 7, CF2 carries the last 7.
        byte[] payload = Enumerable.Range(0, 20).Select(i => (byte)(i + 0x40)).ToArray();
        int ffData = IsoTpFrameCodec.FirstFrameMaxDataLength(isCanFd: false, usesAddressExtension: false, useLongLength: false);
        busA.Transmit(CanFrame.Classic(unchecked((int)epPeer.TxCanId),
            IsoTpFrameCodec.BuildFirstFrame(epPeer, payload.Length, payload.AsSpan(0, ffData), isCanFd: false)));
        await fcSeen.Task.WaitAsync(ShortTimeout);

        // A shortened CF1: SN 1 with only 4 of its 7 bytes, CAN_DL 5 — not the last CF, so it
        // does not conform and is ignored.
        var shortCf1 = IsoTpFrameCodec.BuildConsecutiveFrame(epPeer, sequenceNumber: 1,
            payload.AsSpan(ffData, 4), isCanFd: false, padding: false);
        shortCf1.Length.Should().Be(5);
        busA.Transmit(CanFrame.Classic(unchecked((int)epPeer.TxCanId), shortCf1));

        // The conforming CF1 and CF2 complete the PDU as sent.
        busA.Transmit(CanFrame.Classic(unchecked((int)epPeer.TxCanId),
            IsoTpFrameCodec.BuildConsecutiveFrame(epPeer, sequenceNumber: 1, payload.AsSpan(ffData, 7), isCanFd: false, padding: true)));
        busA.Transmit(CanFrame.Classic(unchecked((int)epPeer.TxCanId),
            IsoTpFrameCodec.BuildConsecutiveFrame(epPeer, sequenceNumber: 2, payload.AsSpan(ffData + 7), isCanFd: false, padding: true)));

        (await recvTask).Should().Equal(payload, "the shortened CF was ignored, not copied");
    }

    // --------------------------------------------------------------------------------
    // #26: a First Frame announcing more than MaxReceivePduLength is answered with FC(OVFLW)
    // and nothing is allocated for it; the default is 65 535 bytes, and the option raises it.
    // The FD bus carries the escape First Frame that can announce ~2 GB.
    // --------------------------------------------------------------------------------
    [Theory]
    [InlineData(null, FlowStatus.Overflow)]     // default: 65 536 bytes is one too many
    [InlineData(100_000, FlowStatus.ClearToSend)] // raised: the same First Frame is accepted
    public async Task Rx_FirstFrame_Above_MaxReceivePduLength_Sends_Overflow(int? maxReceivePduLength, FlowStatus expected)
    {
        var session = NewSession();
        using var busA = OpenCanFd(session, 0);
        using var busB = OpenCanFd(session, 1);

        var epRecv = IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8);
        var epPeer = IsoTpEndpoint.Normal(txCanId: 0x7E8, rxCanId: 0x7E0);
        var options = FastOptions(useCanFd: true).With(maxReceivePduLength: maxReceivePduLength);
        options.MaxReceivePduLength.Should().Be(maxReceivePduLength ?? 0xFFFF);
        using var receiver = IsoTpFactory.Open(busA, epRecv, options);

        var fc = new TaskCompletionSource<FlowStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        busB.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != 0x7E0) return;
            var data = e.CanFrame.Data.ToArray();
            if (data.Length >= 1 && (data[0] >> 4) == 0x3) fc.TrySetResult((FlowStatus)(data[0] & 0x0F));
        };

        // Escape First Frame announcing 65 536 bytes, with a full 64-byte first chunk.
        int ffData = IsoTpFrameCodec.FirstFrameMaxDataLength(isCanFd: true, usesAddressExtension: false, useLongLength: true);
        var ff = IsoTpFrameCodec.BuildFirstFrame(epPeer, 0x1_0000, new byte[ffData], isCanFd: true);
        busB.Transmit(CanFrame.Fd(0x7E8, ff));

        (await fc.Task.WaitAsync(ShortTimeout)).Should().Be(expected);
    }

    // --------------------------------------------------------------------------------
    // #25: the STmin timer belongs to its transfer. A send cancelled while waiting out STmin
    // and replaced at once by another SendAsync must not have the old timer send a Consecutive
    // Frame of the new PDU under the old sequence number. The sender runs on a clock the test
    // owns: STmin is armed but cannot elapse before the cancel has been applied, and the moment
    // the stale timer would fire is a clock advance the test makes after the new transfer's
    // First Frame — after which nothing may follow until the peer's Flow Control.
    // --------------------------------------------------------------------------------
    [Fact]
    public async Task A_Stale_StMin_Timer_Does_Not_Send_A_ConsecutiveFrame_Of_The_Next_Transfer()
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);
        using var clock = new VirtualClock();
        using var serviceA = new CanBusService(busA);
        var senderActor = clock.NewActor();
        var stMin = TimeSpan.FromMilliseconds(100); // encodable as STmin raw 0x64; 300 ms would clamp to 127

        var epSender = IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8);
        var epPeer = IsoTpEndpoint.Normal(txCanId: 0x7E8, rxCanId: 0x7E0);
        using var sender = new IsoTpChannel(serviceA, epSender, FastOptions(nBs: TimeSpan.FromSeconds(3)), ownsService: false, senderActor);

        var fromSender = Channel.CreateUnbounded<byte[]>();
        busB.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID == 0x7E0) fromSender.Writer.TryWrite(e.CanFrame.Data.ToArray());
        };
        async Task<byte[]> NextAsync() => await fromSender.Reader.ReadAsync(new CancellationTokenSource(ShortTimeout).Token);

        byte[] first = Enumerable.Range(0, 20).Select(i => (byte)(0xA0 + i)).ToArray();
        byte[] second = Enumerable.Range(0, 20).Select(i => (byte)(0xB0 + i)).ToArray();

        using var cancel = new CancellationTokenSource();
        var firstSend = sender.SendAsync(first, cancel.Token);
        (await NextAsync())[0].Should().Be(0x10, "FF of the first PDU");
        busB.Transmit(CanFrame.Classic(0x7E8, IsoTpFrameCodec.BuildFlowControl(epPeer, FlowStatus.ClearToSend,
            blockSize: 0, stMinRaw: IsoTpFrameCodec.EncodeStMin(stMin), isCanFd: false, padding: true)));
        await clock.WaitUntilTimerArmedAsync(senderActor, stMin, ShortTimeout); // STmin armed, on a clock that has not moved

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstSend);
        await clock.SettleAsync(); // the cancel's cleanup has run on the actor

        var secondSend = sender.SendAsync(second, new CancellationTokenSource(ShortTimeout).Token);
        var ff2 = await NextAsync();
        ff2[0].Should().Be(0x10, "FF of the second PDU");
        ff2.Skip(2).Take(6).Should().Equal(second.Take(6));

        // Now the old STmin elapses. Nothing may follow the FF: the peer has not sent Flow Control.
        await clock.AdvanceAsync(stMin);
        await clock.SettleAsync();
        await Task.Delay(100); // a frame the timer released would be on the wire by now
        fromSender.Reader.TryRead(out var stray).Should().BeFalse(
            $"no Consecutive Frame may go out before the peer's Flow Control, but one did: {(stray is null ? "" : BitConverter.ToString(stray))}");

        busB.Transmit(CanFrame.Classic(0x7E8, IsoTpFrameCodec.BuildFlowControl(epPeer, FlowStatus.ClearToSend,
            blockSize: 0, stMinRaw: 0, isCanFd: false, padding: true)));
        var cf1 = await NextAsync();
        var cf2 = await NextAsync();
        cf1[0].Should().Be(0x21);
        cf2[0].Should().Be(0x22);
        cf1.Skip(1).Take(7).Concat(cf2.Skip(1).Take(7)).Should().Equal(second.Skip(6));
        await secondSend.WaitAsync(ShortTimeout);
    }

    // --------------------------------------------------------------------------------
    // FR-TP-010 (N_As): the TX-confirm of the First Frame never resolves -> SendAsync must
    // fault with IsoTpTimeoutException(Timer = NAs) instead of hanging. N_As was previously
    // only covered indirectly via the L2 echo-timeout test.
    // --------------------------------------------------------------------------------
    [Fact]
    public async Task MultiFrame_Send_Times_Out_With_NAs_When_TxConfirm_Never_Resolves()
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1); // hub peer; no channel needed

        using var rawService = new CanBusService(busA);
        var neverConfirm = new NeverConfirmService(rawService);
        using var sender = IsoTpFactory.Open(neverConfirm, IsoTpEndpoint.Normal(0x700, 0x708),
            FastOptions(), leaveOpen: false);

        byte[] pdu = Enumerable.Range(0, 30).Select(i => (byte)i).ToArray();
        Func<Task> act = () => sender.SendAsync(pdu);
        var ex = (await act.Should().ThrowAsync<IsoTpTimeoutException>()
            .WithMessage("*N_As*")).Which;
        ex.Timer.Should().Be(IsoTpTimer.NAs);
    }

    // --------------------------------------------------------------------------------
    // Receiver-side block flow control (LocalBlockSize > 0): the receiver must emit a fresh
    // FC after every full block of CFs, and reassembly must stay loss-free. This RX path
    // (IsoTpChannel's per-block FC generation) had no test before.
    // --------------------------------------------------------------------------------
    [Fact]
    public async Task Receiver_With_LocalBlockSize_Emits_FlowControl_After_Each_Full_Block()
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);
        using var snifferBus = OpenClassic(session, 2);

        using var sender = IsoTpFactory.Open(busA, IsoTpEndpoint.Normal(0x7E0, 0x7E8), FastOptions());
        using var receiver = IsoTpFactory.Open(busB, IsoTpEndpoint.Normal(0x7E8, 0x7E0),
            FastOptions(localBs: 2));

        var fcCount = 0;
        snifferBus.FrameObserved += (_, view) =>
        {
            if (view.CanFrame.ID != 0x7E8) return;
            var data = view.CanFrame.Data.Span;
            if (data.Length > 0 && (data[0] & 0xF0) == 0x30)
                Interlocked.Increment(ref fcCount);
        };

        // 38 bytes => FF (6) + 5 CFs (7,7,7,7,4): with BS=2 the receiver sends its initial
        // FC plus one FC after CF #2 and one after CF #4 => 3 FCs in total.
        byte[] pdu = Enumerable.Range(0, 38).Select(i => (byte)(i & 0xFF)).ToArray();
        var recvTask = receiver.ReceiveAsync(new CancellationTokenSource(ShortTimeout).Token);
        await sender.SendAsync(pdu);
        var got = await recvTask;
        got.Should().Equal(pdu);

        await Task.Delay(50);
        Volatile.Read(ref fcCount).Should().Be(3);
    }

    // Regression for #23: two reciprocal channels sharing one service must still hear each other
    // on a bus whose echoes are flagged.
    //
    // `IsoTp.Open(ICanBusService, ...)` documents that channels with disjoint endpoints may share
    // one service (FR-TP-018) — a tester and a simulated ECU in one process is the ordinary shape.
    // On a flagging adapter every frame either channel sends carries IsEcho, because the flag
    // marks the host and not the channel, so withholding echoes made the receiver miss the Single
    // Frame entirely and the sender time out.
    //
    // Every other ISO-TP test here opens two separate buses, which is why none of them covered
    // this: with two buses there is no host echo to mis-filter.
    [Fact]
    public async Task Reciprocal_Channels_Sharing_One_Service_Still_Exchange_On_A_Flagging_Echo_Bus()
    {
        using var bus = ControllableBus.EchoCapable(NewSession());
        using var service = new CanBusService(bus);

        using var tester = IsoTpFactory.Open(
            service, IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8));
        using var ecu = IsoTpFactory.Open(
            service, IsoTpEndpoint.Normal(txCanId: 0x7E8, rxCanId: 0x7E0));

        var request = new byte[] { 0x22, 0xF1, 0x90 };

        using var receiveCts = new CancellationTokenSource(ShortTimeout);
        var ecuReceive = ecu.ReceiveAsync(receiveCts.Token);
        await tester.SendAsync(request);

        (await ecuReceive).ToArray().Should().Equal(
            request,
            "the peer channel's request is genuine traffic even though the host echo flag marks "
            + "it exactly like the tester's own transmissions");
    }

    // #112 -- the non-blocking take, on the real channel. The UDS client reaches for it exactly
    // when its budget is spent, and every test of that behaviour runs on a channel stub, so the
    // real implementation's hand-over path had no coverage at all: the branch that returns a
    // queued PDU is the load-bearing half, and it is the half the stub replaces.
    //
    // EmitPdu enqueues before raising DatagramReceived, so the event is a sound signal that the
    // inbox is non-empty -- no polling and no sleep.
    [Fact]
    public async Task TryReceiveWithArrival_Hands_Over_A_Queued_Pdu_And_Then_Reports_Empty()
    {
        using var bus = ControllableBus.EchoCapable(NewSession());
        using var service = new CanBusService(bus);
        using var tester = IsoTpFactory.Open(
            service, IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8));
        using var ecu = IsoTpFactory.Open(
            service, IsoTpEndpoint.Normal(txCanId: 0x7E8, rxCanId: 0x7E0));

        var arrived = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        tester.DatagramReceived += (_, _) => arrived.TrySetResult(true);

        var response = new byte[] { 0x62, 0xF1, 0x90, 0xAA };

        tester.TryReceiveWithArrival(out _).Should().BeFalse("nothing has been sent yet");

        var before = Stopwatch.GetTimestamp();
        await ecu.SendAsync(response);
        await arrived.Task.WaitAsync(ShortTimeout);
        var after = Stopwatch.GetTimestamp();

        tester.TryReceiveWithArrival(out var taken).Should().BeTrue(
            "the PDU is queued, and taking it must not require waiting");
        taken.Pdu.Should().Equal(response);
        taken.ArrivalTimestamp.Should().BeInRange(before, after,
            "the stamp is the frame's arrival, which happened during this exchange");

        tester.TryReceiveWithArrival(out _).Should().BeFalse(
            "the one queued PDU has been taken");
    }

    // #112 -- the transmit stamp must come from the bus's own hand-off to the driver, not from
    // anywhere upstream of it. The UDS-level tests for this run on a channel stub, which by
    // construction cannot say where in the real path the reading is taken (Codex on #112); this
    // one runs the whole chain and pins the placement by ordering.
    //
    // ControllableBus.OnTransmitting runs on the transmitting thread inside Transmit, so blocking
    // there parks the frame mid-hand-off. Every candidate instant upstream of the driver call --
    // the channel's send task starting, the actor hop, acquiring the pending-send lock -- happens
    // before this test releases it; the correct one happens after. No tolerance, no duration.
    [Fact]
    public async Task Transmit_Stamp_Comes_From_The_Bus_Hand_Off_Not_From_Upstream()
    {
        using var bus = ControllableBus.EchoCapable(NewSession());
        using var service = new CanBusService(bus);
        using var channel = IsoTpFactory.Open(
            service, IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8));

        using var transmitting = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        bus.OnTransmitting = _ =>
        {
            transmitting.Set();
            release.Wait(ShortTimeout);
        };

        var send = channel.SendWithTransmitStampAsync(new byte[] { 0x22, 0xF1, 0x90 });
        transmitting.Wait(ShortTimeout).Should().BeTrue("the frame must reach the driver call");

        var releasedAt = Stopwatch.GetTimestamp();
        release.Set();

        var transmitStamp = await send.WaitAsync(ShortTimeout);

        transmitStamp.Should().BeGreaterThan(releasedAt,
            "the stamp must be taken after the driver accepted the frame, and this test held the "
            + "driver call open until the instant above");
    }

    // #113 -- who disposes the actor when construction fails. The seam's contract is that a
    // channel disposes only an actor it created, because an injected one may be running other
    // channels; the constructor's catch block is where that is decided and it had never been
    // executed. Codecov is what noticed -- I had asserted the rule in the pull request and in
    // three review replies without once running the path.
    //
    // The self-created half is observed through ProtocolActor.RunningLoopCount, because nothing
    // else can see it: construction failed, so there is no channel to ask and its actor was never
    // reachable. The first revision watched inner.SubscriptionCount, which is zero whether or not
    // that actor was disposed -- Codex found that, and it is the same "assertion that cannot
    // fail" this branch was written to stop shipping.
    [Fact]
    public async Task A_Failed_Construction_Disposes_Its_Own_Actor_But_Never_An_Injected_One()
    {
        var session = NewSession();
        using var bus = OpenClassic(session, 0);
        using var inner = new CanBusService(bus);
        var endpoint = IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8);

        // Injected: the channel must leave it alone on the way out.
        using var clock = new VirtualClock();
        var injected = clock.NewActor();
        var failing = new ThrowingSubscribeService(inner, failOnCall: 1);

        // After the injected actor exists, so it is the baseline both attempts return to.
        var loopsBefore = ProtocolActor.RunningLoopCount;

        Action construct = () => new IsoTpChannel(failing, endpoint, FastOptions(),
            ownsService: false, injected);
        construct.Should().Throw<InvalidOperationException>();

        (await injected.PostAsync(() => 42).WaitAsync(ShortTimeout)).Should().Be(42,
            "an injected actor belongs to the caller and must survive a construction that failed");
        ProtocolActor.RunningLoopCount.Should().Be(loopsBefore,
            "the channel created no actor here, so it must not have ended one either");

        // Self-created: the channel owns it, so it must be gone. Dispose joins the loop before
        // returning, so a count still above the baseline is a leaked actor thread and not one
        // that has yet to notice -- no wait belongs here.
        var failingAgain = new ThrowingSubscribeService(inner, failOnCall: 1);
        Action constructOwning = () => new IsoTpChannel(failingAgain, endpoint, FastOptions(),
            ownsService: false);
        constructOwning.Should().Throw<InvalidOperationException>();
        ProtocolActor.RunningLoopCount.Should().Be(loopsBefore,
            "an actor the channel created for itself must not outlive the construction that "
            + "failed");
        inner.SubscriptionCount.Should().Be(0, "neither attempt got as far as subscribing");
    }

    /// <summary>
    /// Test double: the first <see cref="ICanBusService.SendConfirmed"/> call throws the supplied
    /// exception instead of transmitting; every later call is forwarded to the inner service
    /// untouched. Models the L2/driver layer failing outright — as opposed to reporting a
    /// <see cref="TxConfirmation"/> that says the send failed — which is the one failure a
    /// channel's send can hit *after* the actor already owns the PDU.
    /// </summary>
    private sealed class ThrowOnFirstConfirmService : ICanBusService
    {
        private readonly ICanBusService _inner;
        private readonly Exception _failure;
        private int _calls;

        public ThrowOnFirstConfirmService(ICanBusService inner, Exception failure)
        {
            _inner = inner;
            _failure = failure;
        }

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
            if (Interlocked.Increment(ref _calls) == 1) throw _failure;
            return _inner.SendConfirmed(frame, timeout, cancellationToken);
        }

        public void Dispose() { /* wrapper: the test owns and disposes the inner service */ }
    }

    /// <summary>
    /// Test double: puts frames on the wire for real but never delivers an echo, letting the
    /// requested confirm timeout (the channel's N_As) expire as a Timeout failure — the exact
    /// L2 outcome that maps to <see cref="IsoTpTimeoutException"/> with timer N_As (FR-TP-010).
    /// </summary>
    private sealed class NeverConfirmService : ICanBusService
    {
        private readonly ICanBusService _inner;

        public NeverConfirmService(ICanBusService inner) => _inner = inner;

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

        public async Task<TxConfirmation> SendConfirmed(CanFrame frame, TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            // Put the frame on the wire for real, then let the requested confirm timeout
            // (the channel's N_As) expire without an echo — exactly the L2 contract the
            // channel maps to IsoTpTimeoutException(NAs).
            _ = _inner.Bus.Transmit(new[] { frame });
            var effective = timeout ?? TimeSpan.FromSeconds(1);
            await Task.Delay(effective, cancellationToken).ConfigureAwait(false);
            return new TxConfirmation
            {
                Confirmed = false,
                IsApproximated = false,
                Timestamp = DateTime.UtcNow,
                FailureReason = TxConfirmFailureReason.Timeout,
            };
        }

        public void Dispose() { /* wrapper: the channel owns and disposes the inner service */ }
    }

    /// <summary>
    /// Test double: forwards every <see cref="ICanBusService"/> call to an inner service, but
    /// parks <see cref="ICanBusService.SendConfirmed"/> until <paramref name="release"/> is
    /// signaled so cancel/gate / deferred-FC races are deterministic.
    /// </summary>
    /// <remarks>
    /// Default holds only the first confirm <em>before</em> transmitting (cancel/gate tests).
    /// Pass <paramref name="holdAfterTransmit"/> to transmit first, then park — so peers can
    /// answer FC while TX-confirm is still outstanding (deferred-FC / WftMax tests).
    /// </remarks>
    private sealed class DelayingConfirmService : ICanBusService
    {
        private readonly ICanBusService _inner;
        private readonly SemaphoreSlim _release;
        private readonly ManualResetEventSlim _started;
        private readonly bool _holdAfterTransmit;
        private int _confirmCount;

        public DelayingConfirmService(ICanBusService inner, SemaphoreSlim release,
            ManualResetEventSlim started, bool holdAfterTransmit = false)
        {
            _inner = inner;
            _release = release;
            _started = started;
            _holdAfterTransmit = holdAfterTransmit;
        }

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

        public async Task<TxConfirmation> SendConfirmed(CanFrame frame, TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            bool hold = Interlocked.Increment(ref _confirmCount) == 1;

            if (hold && !_holdAfterTransmit)
            {
                _started.Set();
                // Do not honor cancellationToken here: the point of the test is that IsoTpChannel
                // still waits for this bus TX even after the caller's SendAsync token is cancelled.
                if (!_release.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("test release gate was never signaled");
            }

            var result = await _inner.SendConfirmed(frame, timeout, cancellationToken).ConfigureAwait(false);

            if (hold && _holdAfterTransmit)
            {
                _started.Set();
                if (!_release.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("test release gate was never signaled");
            }

            return result;
        }

        public void Dispose() { /* leaveOpen: inner disposed by test */ }
    }

    /// <summary>
    /// Parks <see cref="ICanBusService.SendConfirmed"/> only for Consecutive Frames, and only
    /// after the frame has been transmitted — so a peer FC can arrive while TX state is still
    /// <c>SendingCf</c> (Bugbot 3597408323).
    /// </summary>
    private sealed class HoldConsecutiveFrameConfirmService : ICanBusService
    {
        private readonly ICanBusService _inner;
        private readonly SemaphoreSlim _release;
        private readonly ManualResetEventSlim _parked;

        public HoldConsecutiveFrameConfirmService(ICanBusService inner, SemaphoreSlim release,
            ManualResetEventSlim parked)
        {
            _inner = inner;
            _release = release;
            _parked = parked;
        }

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

        public async Task<TxConfirmation> SendConfirmed(CanFrame frame, TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            // Copy PCI before await — ReadOnlySpan cannot live across await points.
            byte[] payload = frame.Data.ToArray();
            bool isCf = payload.Length > 0 && (payload[0] >> 4) == 0x2;

            var result = await _inner.SendConfirmed(frame, timeout, cancellationToken).ConfigureAwait(false);

            if (isCf)
            {
                _parked.Set();
                if (!_release.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("CF confirm release gate was never signaled");
            }

            return result;
        }

        public void Dispose() { /* leaveOpen: inner disposed by test */ }
    }

    // -----------------------------------------------------------------------------------
    // #28, Codex on #143 — a reception in progress is reported from the First Frame's arrival,
    // not from its processing: the reader publishes it before the actor sees the frame. The
    // actor then withdraws a record for a frame it refuses and confirms one it accepts.
    // -----------------------------------------------------------------------------------
    [Fact]
    public async Task A_First_Frame_Is_In_Progress_While_The_Actor_Is_Behind_And_Withdrawn_When_Refused()
    {
        var session = NewSession();
        using var busPeer = OpenClassic(session, 0);
        using var busRecv = OpenClassic(session, 1);

        var epRecv = IsoTpEndpoint.Normal(txCanId: 0x7E8, rxCanId: 0x7E0);
        var epPeer = IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8);

        using var serviceRecv = new CanBusService(busRecv);
        using var actor = new ProtocolActor();
        var options = new IsoTpChannelOptions
        {
            UseCanFd = false,
            UsePadding = true,
            MaxReceivePduLength = 16,
            NCr = TimeSpan.FromSeconds(5),
        };
        using var receiver = new IsoTpChannel(serviceRecv, epRecv, options, ownsService: false, actor);

        // Hold the actor: everything the reader posts from here on waits in the mailbox.
        using var gate = new SemaphoreSlim(0);
        var held = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        actor.Post(() =>
        {
            held.TrySetResult(true);
            gate.Wait();
        });
        await held.Task.WaitAsync(ShortTimeout);

        // A First Frame announcing more than the channel accepts: published on arrival ...
        int ffData = IsoTpFrameCodec.FirstFrameMaxDataLength(isCanFd: false, usesAddressExtension: false, useLongLength: false);
        byte[] tooLong = Enumerable.Range(0x30, 20).Select(i => (byte)i).ToArray();
        var ffTooLong = IsoTpFrameCodec.BuildFirstFrame(epPeer, tooLong.Length, tooLong.AsSpan(0, ffData), isCanFd: false);
        busPeer.Transmit(CanFrame.Classic(unchecked((int)epPeer.TxCanId), ffTooLong));

        var seen = await WaitForReceptionsAsync(receiver, count: 1);
        seen.Should().HaveCount(1, "the reader publishes a First Frame before the actor processes it");
        seen[0].AnnouncedLength.Should().Be(20);
        seen[0].FirstFrameData.ToArray().Should().Equal(tooLong.Take(ffData));

        // ... and withdrawn once the actor refuses it with FC(OVFLW).
        gate.Release();
        await actor.PostAsync(() => { }).WaitAsync(ShortTimeout);
        receiver.GetReceptionsInProgress().Should().BeEmpty("the actor refused the frame");

        // A First Frame the channel accepts stays in progress after the actor has run.
        byte[] fits = Enumerable.Range(0x40, 12).Select(i => (byte)i).ToArray();
        var ffFits = IsoTpFrameCodec.BuildFirstFrame(epPeer, fits.Length, fits.AsSpan(0, ffData), isCanFd: false);
        busPeer.Transmit(CanFrame.Classic(unchecked((int)epPeer.TxCanId), ffFits));
        (await WaitForReceptionsAsync(receiver, count: 1)).Should().HaveCount(1);
        await actor.PostAsync(() => { }).WaitAsync(ShortTimeout);
        seen = receiver.GetReceptionsInProgress();
        seen.Should().HaveCount(1, "the actor accepted the frame");
        seen[0].AnnouncedLength.Should().Be(12);
    }

    // Codex on #143, second round: with the actor behind, a complete multi-frame PDU can sit
    // in the mailbox when the next First Frame is read. Both are receptions in progress, and
    // the newer must not hide the older -- its PDU has yet to be delivered.
    [Fact]
    public async Task Two_First_Frames_Read_Ahead_Of_The_Actor_Are_Both_In_Progress_Until_Their_Outcomes()
    {
        var session = NewSession();
        using var busPeer = OpenClassic(session, 0);
        using var busRecv = OpenClassic(session, 1);

        var epRecv = IsoTpEndpoint.Normal(txCanId: 0x7E8, rxCanId: 0x7E0);
        var epPeer = IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8);

        using var serviceRecv = new CanBusService(busRecv);
        using var actor = new ProtocolActor();
        using var receiver = new IsoTpChannel(serviceRecv, epRecv, FastOptions(), ownsService: false, actor);

        using var gate = new SemaphoreSlim(0);
        var held = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        actor.Post(() =>
        {
            held.TrySetResult(true);
            gate.Wait();
        });
        await held.Task.WaitAsync(ShortTimeout);

        // PDU A complete on the wire (FF + one CF), then PDU B's First Frame -- all in the
        // mailbox behind the held actor. (No Flow Control is answered while it is held; the
        // peer here does not wait for one.)
        int ffData = IsoTpFrameCodec.FirstFrameMaxDataLength(isCanFd: false, usesAddressExtension: false, useLongLength: false);
        byte[] a = Enumerable.Range(0x62, 12).Select(i => (byte)i).ToArray();
        byte[] b = Enumerable.Range(0x59, 12).Select(i => (byte)i).ToArray();
        int canId = unchecked((int)epPeer.TxCanId);
        busPeer.Transmit(CanFrame.Classic(canId, IsoTpFrameCodec.BuildFirstFrame(epPeer, a.Length, a.AsSpan(0, ffData), isCanFd: false)));
        busPeer.Transmit(CanFrame.Classic(canId, IsoTpFrameCodec.BuildConsecutiveFrame(epPeer, sequenceNumber: 1, a.AsSpan(ffData), isCanFd: false, padding: true, paddingByte: 0xCC)));
        busPeer.Transmit(CanFrame.Classic(canId, IsoTpFrameCodec.BuildFirstFrame(epPeer, b.Length, b.AsSpan(0, ffData), isCanFd: false)));

        var seen = await WaitForReceptionsAsync(receiver, count: 2);
        seen.Should().HaveCount(2, "the second First Frame must not replace the first's record");
        seen[0].FirstFrameData.Span[0].Should().Be(0x62);
        seen[1].FirstFrameData.Span[0].Should().Be(0x59);

        // Let the actor run: A completes into the inbox and is withdrawn; B stays in progress.
        gate.Release();
        await actor.PostAsync(() => { }).WaitAsync(ShortTimeout);
        using var receiveCts = new CancellationTokenSource(ShortTimeout);
        var delivered = await receiver.ReceiveAsync(receiveCts.Token);
        delivered.Should().Equal(a);
        seen = receiver.GetReceptionsInProgress();
        seen.Should().HaveCount(1);
        seen[0].FirstFrameData.Span[0].Should().Be(0x59);
    }

    // Codex on #143, third round: the hop from the demux buffer to the reader task is
    // scheduling too. A First Frame the demux has buffered is reported in progress on demand,
    // pumped on the caller's thread, even while the reader task is starved.
    [Fact]
    public void A_Buffered_First_Frame_Is_In_Progress_While_The_Reader_Task_Is_Starved()
    {
        var ep = IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8);
        using var service = new StarvedReaderBusService();
        using var actor = new ProtocolActor();
        using var channel = new IsoTpChannel(service, ep, FastOptions(), ownsService: false, actor);

        int ffData = IsoTpFrameCodec.FirstFrameMaxDataLength(isCanFd: false, usesAddressExtension: false, useLongLength: false);
        byte[] pdu = Enumerable.Range(0x62, 20).Select(i => (byte)i).ToArray();
        var ff = IsoTpFrameCodec.BuildFirstFrame(IsoTpEndpoint.Normal(0x7E8, 0x7E0), pdu.Length, pdu.AsSpan(0, ffData), isCanFd: false);
        service.Deliver(new CanFrameView(CanFrameType.Can20, 0x7E8, ff, FrameFlags.None));

        var seen = channel.GetReceptionsInProgress();
        seen.Should().HaveCount(1, "the buffered First Frame is pumped on the caller's thread");
        seen[0].AnnouncedLength.Should().Be(20);
        seen[0].FirstFrameData.Span[0].Should().Be(0x62);
    }

    // And a frame the demux has buffered when DiscardPendingPdus is called is part of what
    // the discard drops: it does not surface to the next receiver once the reader task runs.
    [Fact]
    public async Task A_Frame_Buffered_At_Discard_Time_Does_Not_Surface_Afterwards()
    {
        var ep = IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8);
        using var service = new StarvedReaderBusService();
        using var actor = new ProtocolActor();
        using var channel = new IsoTpChannel(service, ep, FastOptions(), ownsService: false, actor);

        // A complete Single Frame, buffered while the reader task is starved.
        service.Deliver(new CanFrameView(CanFrameType.Can20, 0x7E8,
            new byte[] { 0x03, 0x62, 0xF1, 0x90 }, FrameFlags.None));

        channel.DiscardPendingPdus();

        // Now let the reader task run and settle everything it posts.
        service.ResetDrained();
        service.WakeReader();
        await service.PumpDrained.WaitAsync(ShortTimeout);
        await actor.PostAsync(() => { }).WaitAsync(ShortTimeout);

        channel.TryReceiveWithArrival(out _).Should().BeFalse(
            "the frame was buffered before the discard and belongs to what it dropped");
    }

    // Bugbot on #143: a First Frame buffered at discard time must be dropped *unanswered*. A
    // Flow Control for it would invite the rest of a transfer nobody waits for, whose
    // Consecutive Frames then collide with the next reception.
    [Fact]
    public async Task A_First_Frame_Buffered_At_Discard_Time_Gets_No_Flow_Control()
    {
        var ep = IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8);
        using var service = new StarvedReaderBusService();
        using var actor = new ProtocolActor();
        using var channel = new IsoTpChannel(service, ep, FastOptions(), ownsService: false, actor);

        int ffData = IsoTpFrameCodec.FirstFrameMaxDataLength(isCanFd: false, usesAddressExtension: false, useLongLength: false);
        byte[] pdu = Enumerable.Range(0x62, 20).Select(i => (byte)i).ToArray();
        var ff = IsoTpFrameCodec.BuildFirstFrame(IsoTpEndpoint.Normal(0x7E8, 0x7E0), pdu.Length, pdu.AsSpan(0, ffData), isCanFd: false);
        service.Deliver(new CanFrameView(CanFrameType.Can20, 0x7E8, ff, FrameFlags.None));

        channel.DiscardPendingPdus();
        await actor.PostAsync(() => { }).WaitAsync(ShortTimeout);

        channel.GetReceptionsInProgress().Should().BeEmpty("the frame arrived before the discard");
        lock (service.Sent)
            service.Sent.Should().BeEmpty("a dropped First Frame is not answered with Flow Control");
    }

    /// <summary>
    /// A bus service whose subscription's <c>WaitToReadAsync</c> stays pending until
    /// <see cref="WakeReader"/>, so the channel's reader task is starved by construction and
    /// only a caller-side pump sees what <see cref="Deliver"/> buffered.
    /// </summary>
    private sealed class StarvedReaderBusService : ICanBusService
    {
        private readonly Channel<CanFrameEvent> _frames = Channel.CreateUnbounded<CanFrameEvent>();
        private readonly TaskCompletionSource<bool> _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<bool> _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _wakesServed;

        public void Deliver(CanFrameView frame) => _frames.Writer.TryWrite(
            new CanFrameEvent(frame, isEcho: false, TimeSpan.Zero));

        /// <summary>Lets the reader task's wait complete once; every later wait stays pending.</summary>
        public void WakeReader() => _wake.TrySetResult(true);

        /// <summary>Completes when a pump has emptied the buffer (a TryRead returned false).</summary>
        public Task PumpDrained => _drained.Task;

        public void ResetDrained() => _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ICanBus Bus => throw new NotSupportedException();

        public int SubscriptionCount => 1;

        public event EventHandler<Exception>? BackgroundExceptionOccurred
        {
            add { }
            remove { }
        }

        public ISubscription Subscribe(Func<CanFrameEvent, bool>? predicate = null,
            int? bufferCapacity = null, bool includeEcho = false)
            => new Sub(this);

        public ISubscription Subscribe(CanIdFilter filter, int? bufferCapacity = null,
            bool includeEcho = false)
            => new Sub(this);

        /// <summary>Every frame the channel put on the wire, in order.</summary>
        public List<byte[]> Sent { get; } = new();

        public Task<TxConfirmation> SendConfirmed(CanFrame frame, TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            lock (Sent) Sent.Add(frame.Data.ToArray());
            return Task.FromResult(new TxConfirmation { Confirmed = true });
        }

        public IReadOnlyList<FilterOverlap> FindOverlappingFilterSubscriptions()
            => Array.Empty<FilterOverlap>();

        public void Dispose() => _frames.Writer.TryComplete();

        private sealed class Sub : ISubscription
        {
            private readonly StarvedReaderBusService _owner;
            public Sub(StarvedReaderBusService owner) => _owner = owner;

            public IAsyncEnumerable<CanFrameEvent> Frames => _owner._frames.Reader.ReadAllAsync();

            public bool TryRead(out CanFrameEvent frameEvent)
            {
                bool ok = _owner._frames.Reader.TryRead(out frameEvent);
                if (!ok) _owner._drained.TrySetResult(true);
                return ok;
            }

            public async ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
            {
                if (Interlocked.Exchange(ref _owner._wakesServed, 1) == 0)
                    return await _owner._wake.Task.WaitAsync(cancellationToken);
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return false;
            }

            public void Reconfigure(CanIdFilter filter) { }

            public void Reconfigure(Func<CanFrameEvent, bool>? predicate) { }

            public void Dispose() { }
        }
    }

    private static async Task<IReadOnlyList<IsoTpReceptionInProgress>> WaitForReceptionsAsync(
        IIsoTpChannel channel, int count)
    {
        var deadline = Stopwatch.StartNew();
        var seen = channel.GetReceptionsInProgress();
        while (seen.Count < count && deadline.Elapsed < ShortTimeout)
        {
            await Task.Delay(5);
            seen = channel.GetReceptionsInProgress();
        }
        return seen;
    }
}
