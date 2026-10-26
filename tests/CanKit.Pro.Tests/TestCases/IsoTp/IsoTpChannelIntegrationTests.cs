using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core;
using CanKit.Pro.Actor;
using CanKit.Pro.IsoTp;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
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

        public Task<TxConfirmation> SendConfirmedAsync(CanFrame frame, TimeSpan? timeout = null,
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

        // #171: "each Wait FC is processed within 20 ms before the next is sent" was a
        // Task.Delay(20) standing in for two facts: the frame reached the sender's bus, and the
        // sender's actor took it off the mailbox. fcArrived is the first (a frame on the wire,
        // observed on busA where the sender listens); SettleAsync is the second (an actor
        // round-trip after that frame was delivered). Sending the next Wait FC before both have
        // happened would let two Waits collapse into the actor's mailbox as one delivery.
        using var fcArrived = new SemaphoreSlim(0);
        busA.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != 0x401) return;
            var payload = e.CanFrame.Data.ToArray();
            if (payload.Length > 0 && (payload[0] >> 4) == 0x3) fcArrived.Release();
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
            (await fcArrived.WaitAsync(ShortTimeout)).Should().BeTrue(
                $"the sender's bus must see Wait FC #{i + 1}");
            await sender.SettleAsync().WaitAsync(ShortTimeout);
        }

        Func<Task> act = () => sendTask;
        var ex = (await act.Should().ThrowAsync<IsoTpWaitFrameLimitExceededException>()).Which;
        ex.Limit.Should().Be(wftMax);
        ex.WaitFramesReceived.Should().BeGreaterThan(wftMax);
    }

    // --------------------------------------------------------------------------------
    // Test gap of the 2026-10-21 review (#353, mutation isotp-M1): Wait → CTS resume. The WFT
    // counter is reset by every CTS, so a peer that answers each block with WftMax Waits and
    // then a CTS is conforming, and the transfer completes. Without the reset the second
    // block's first Wait would already exceed the limit. The sender runs on a clock the test
    // never moves, so N_Bs cannot expire however loaded the host (#92). BS = 1: a FC after the
    // FF and after each CF, which gives two blocks to send Waits against.
    // --------------------------------------------------------------------------------
    [Fact]
    public async Task MultiFrame_Send_Resumes_After_Waits_And_Counts_Them_Per_Block()
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);

        var epAB = IsoTpEndpoint.Normal(0x410, 0x411);
        var epBA = IsoTpEndpoint.Normal(0x411, 0x410);

        const int wftMax = 2;
        using var clock = new VirtualClock();
        using var service = new CanBusService(busA);
        using var sender = new IsoTpChannel(service, epAB, FastOptions(wftMax: wftMax),
            ownsService: false, clock.NewActor());

        // The peer by hand: every FF or CF it sees is signalled; the test answers them.
        using var blockEnded = new SemaphoreSlim(0);
        var framesFromSender = new List<byte[]>();
        busB.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != 0x410) return;
            var payload = e.CanFrame.Data.ToArray();
            if (payload.Length == 0) return;
            lock (framesFromSender) framesFromSender.Add(payload);
            if ((payload[0] >> 4) is 0x1 or 0x2) blockEnded.Release();
        };
        using var fcArrived = new SemaphoreSlim(0);
        busA.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != 0x411) return;
            var payload = e.CanFrame.Data.ToArray();
            if (payload.Length > 0 && (payload[0] >> 4) == 0x3) fcArrived.Release();
        };

        async Task AnswerAsync(FlowStatus status)
        {
            var fc = IsoTpFrameCodec.BuildFlowControl(epBA, status, blockSize: 1, stMinRaw: 0,
                isCanFd: false, padding: true);
            busB.Transmit(CanFrame.Classic(0x411, fc));
            (await fcArrived.WaitAsync(ShortTimeout)).Should().BeTrue($"the sender's bus must see FC({status})");
            await sender.SettleAsync().WaitAsync(ShortTimeout);
        }

        // 20 bytes classic: FF(6) + CF1(7) + CF2(7); two FCs to answer, after the FF and after CF1.
        byte[] pdu = Enumerable.Range(0, 20).Select(i => (byte)(i + 1)).ToArray();
        var sendTask = sender.SendAsync(pdu);

        for (int block = 1; block <= 2; block++)
        {
            (await blockEnded.WaitAsync(ShortTimeout)).Should().BeTrue($"the frame ending block {block} must be on the wire");
            for (int wait = 1; wait <= wftMax; wait++)
                await AnswerAsync(FlowStatus.Wait);
            await AnswerAsync(FlowStatus.ClearToSend);
        }

        await sendTask.WaitAsync(ShortTimeout);
        lock (framesFromSender)
            framesFromSender.Select(f => f[0] >> 4).Should().Equal(new[] { 0x1, 0x2, 0x2 }, "FF, CF1, CF2 and nothing else");
    }

    // --------------------------------------------------------------------------------
    // Test gap of the 2026-10-21 review (#353, mutation isotp-M2): a single Wait re-arms N_Bs.
    // The WftMax test above sends Waits until the send aborts, and the deferred-Wait test
    // re-arms through the tail of ApplyDeferredFlowControlsOrArmNBs; a lone Wait met in
    // WaitFcInitial, then silence, has to end in an N_Bs timeout rather than a hang. The sender
    // runs on a clock the test drives: the FF confirmation is known to have armed N_Bs before
    // the Wait is sent (the actor is asked, not the wire), the re-armed timer is read back off
    // the actor, and the clock is then moved past it.
    // --------------------------------------------------------------------------------
    [Fact]
    public async Task A_Single_Wait_Rearms_NBs_And_Silence_After_It_Times_Out()
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);

        var epAB = IsoTpEndpoint.Normal(0x420, 0x421);
        var epBA = IsoTpEndpoint.Normal(0x421, 0x420);

        var nBs = TimeSpan.FromMilliseconds(500);
        using var clock = new VirtualClock();
        using var service = new CanBusService(busA);
        var senderActor = clock.NewActor();
        using var sender = new IsoTpChannel(service, epAB, FastOptions(nBs: nBs),
            ownsService: false, senderActor);

        using var fcArrived = new SemaphoreSlim(0);
        busA.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != 0x421) return;
            var payload = e.CanFrame.Data.ToArray();
            if (payload.Length > 0 && (payload[0] >> 4) == 0x3) fcArrived.Release();
        };

        byte[] pdu = Enumerable.Range(0, 20).Select(i => (byte)i).ToArray();
        var sendTask = sender.SendAsync(pdu);

        // N_Bs armed from the FF's confirmation: the sender is in WaitFcInitial, so the Wait
        // below is handled at once rather than deferred behind the confirmation.
        await clock.WaitUntilTimerArmedAsync(senderActor, nBs, ShortTimeout);

        var fc = IsoTpFrameCodec.BuildFlowControl(epBA, FlowStatus.Wait, blockSize: 0, stMinRaw: 0,
            isCanFd: false, padding: true);
        busB.Transmit(CanFrame.Classic(0x421, fc));
        (await fcArrived.WaitAsync(ShortTimeout)).Should().BeTrue("the sender's bus must see the Wait");
        await sender.SettleAsync().WaitAsync(ShortTimeout);

        // The Wait completed the running N_Bs and armed a fresh one; the clock has not moved,
        // so the fresh one is the full interval away. (A completed entry is skipped, not read.)
        (await senderActor.NextTimerDelayAsync()).Should().Be(nBs, "a Wait re-arms N_Bs");

        await clock.AdvanceAsync(nBs + TimeSpan.FromMilliseconds(1));
        Func<Task> act = () => sendTask.WaitAsync(ShortTimeout);
        (await act.Should().ThrowAsync<IsoTpTimeoutException>()).Which.Timer.Should().Be(IsoTpTimer.NBs);
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
    // #56 — a First Frame announcing what a Single Frame of the same CAN_DL could carry is not
    // a First Frame: ISO 15765-2 §9.6.3.1 has the receiver ignore it. No Flow Control goes out
    // and nothing is delivered -- the previous behaviour, answering it and completing a PDU
    // from it, was pinned by the test this one replaces.
    // --------------------------------------------------------------------------------
    [Theory]
    [InlineData(6)] // fills the First Frame's own six data bytes
    [InlineData(7)] // the Single Frame capacity at CAN_DL 8
    public async Task A_First_Frame_That_Fits_A_Single_Frame_Is_Ignored(int announced)
    {
        var ep = IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8);
        using var service = new StarvedReaderBusService();
        using var actor = new ProtocolActor();
        using var channel = new IsoTpChannel(service, ep, FastOptions(), ownsService: false, actor);

        byte[] ff = { 0x10, (byte)announced, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 };
        service.Deliver(new CanFrameView(CanFrameType.Can20, 0x7E8, ff, FrameFlags.None));

        // Pumped on this thread, then settled on the actor.
        channel.GetReceptionsInProgress().Should().BeEmpty();
        await actor.PostAsync(() => { }).WaitAsync(ShortTimeout);

        lock (service.Sent)
            service.Sent.Should().BeEmpty("an ignored First Frame gets no Flow Control");
        channel.TryReceiveWithArrival(out _).Should().BeFalse("an ignored First Frame delivers nothing");
        channel.GetReceptionsInProgress().Should().BeEmpty();
    }

    // #207 -- the same for a First Frame in the CAN-FD escape form announcing a length the short
    // form carries (up to 4095): the codec refuses it, and the channel, which reads every frame
    // through the codec, never starts a reception for it. Without the codec's rule the frame
    // would be taken for a First Frame of a 100-byte PDU (it does not fit a Single Frame of its
    // CAN_DL, so #56's rule does not catch it) and a reassembly would begin.
    [Theory]
    [InlineData(100)]
    [InlineData(4095)] // the largest length the short form carries
    public async Task An_Escape_First_Frame_Announcing_A_Short_Form_Length_Starts_No_Reception(int announced)
    {
        var ep = IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8);
        using var service = new StarvedReaderBusService();
        using var actor = new ProtocolActor();
        using var channel = new IsoTpChannel(service, ep, FastOptions(), ownsService: false, actor);

        byte[] ff =
        {
            0x10, 0x00,
            (byte)(announced >> 24), (byte)(announced >> 16), (byte)(announced >> 8), (byte)announced,
            0x11, 0x22,
        };
        service.Deliver(new CanFrameView(CanFrameType.CanFd, 0x7E8, ff, FrameFlags.None));

        // Pumped on this thread, then settled on the actor.
        channel.GetReceptionsInProgress().Should().BeEmpty("the frame is not a First Frame");
        await actor.PostAsync(() => { }).WaitAsync(ShortTimeout);

        lock (service.Sent)
            service.Sent.Should().BeEmpty("a frame that is not a First Frame gets no Flow Control");
        channel.TryReceiveWithArrival(out _).Should().BeFalse();
        channel.GetReceptionsInProgress().Should().BeEmpty();
    }

    // Codex on #150: a Single Frame the demux has buffered is not in the inbox until the reader
    // task and the actor have taken it, and neither runs on the caller's schedule; SettleAsync
    // takes it through on the caller's, so a look at the inbox after it is a look at every
    // frame that arrived before the call.
    [Fact]
    public async Task Settle_Takes_A_Buffered_Single_Frame_Through_To_The_Inbox()
    {
        var ep = IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8);
        using var service = new StarvedReaderBusService();
        using var actor = new ProtocolActor();
        using var channel = new IsoTpChannel(service, ep, FastOptions(), ownsService: false, actor);

        byte[] sf = { 0x03, 0x7F, 0x3E, 0x78, 0x00, 0x00, 0x00, 0x00 };
        long arrival = Stopwatch.GetTimestamp();
        service.Deliver(new CanFrameView(CanFrameType.Can20, 0x7E8, sf, FrameFlags.None), arrival);
        // Buffered, but nothing has taken it yet -- the reader task is starved by construction
        // (StarvedReaderBusService.WaitToReadAsync never completes). #171: that starvation is
        // deterministic, not a race, so what proves "nothing has taken it yet" is not "enough
        // wall time for a non-starved reader" but an actor round-trip: the actor has nothing
        // queued for this frame because nothing pumped the subscription for it.
        await actor.PostAsync(() => { }).WaitAsync(ShortTimeout);
        channel.TryReceiveWithArrival(out _).Should().BeFalse("the reader task has not run");

        await channel.SettleAsync().WaitAsync(ShortTimeout);

        channel.TryReceiveWithArrival(out var pdu).Should().BeTrue("settling took the frame through");
        pdu.Pdu.Should().Equal(0x7F, 0x3E, 0x78);
        pdu.FirstFrameArrivalTimestamp.Should().Be(arrival, "the stamp is the demux's, not the settle's");
    }

    // The token ends the wait for the actor, not the settling: a caller that gives up on the wait
    // (its own deadline passed, its operation was cancelled) is not held behind whatever the
    // channel's actor is busy with.
    [Fact]
    public async Task Settle_With_An_Already_Cancelled_Token_Is_Cancelled()
    {
        var ep = IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8);
        using var service = new StarvedReaderBusService();
        using var actor = new ProtocolActor();
        using var channel = new IsoTpChannel(service, ep, FastOptions(), ownsService: false, actor);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await Record.ExceptionAsync(() => channel.SettleAsync(cts.Token).WaitAsync(ShortTimeout));

        ex.Should().BeAssignableTo<OperationCanceledException>();
    }

    [Fact]
    public async Task Settle_Cancelled_While_The_Actor_Is_Busy_Releases_The_Caller_At_Once()
    {
        var ep = IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8);
        using var service = new StarvedReaderBusService();
        using var actor = new ProtocolActor(ActorExecutionMode.DedicatedThread);
        using var channel = new IsoTpChannel(service, ep, FastOptions(), ownsService: false, actor);
        using var release = new ManualResetEventSlim();
        using var busy = new ManualResetEventSlim();
        actor.Post(() => { busy.Set(); release.Wait(); });
        busy.Wait(ShortTimeout).Should().BeTrue("the actor must be inside the blocking item");

        using var cts = new CancellationTokenSource();
        var settle = channel.SettleAsync(cts.Token);
        settle.IsCompleted.Should().BeFalse("the settle waits behind the item the actor is busy with");

        cts.Cancel();

        // Released while the actor is still blocked: not when it eventually gets to the wait.
        var ex = await Record.ExceptionAsync(() => settle.WaitAsync(ShortTimeout));
        ex.Should().BeAssignableTo<OperationCanceledException>();

        release.Set();
    }

    [Fact]
    public async Task Settle_With_A_Live_Token_Takes_A_Buffered_Frame_Through_Like_Without_One()
    {
        var ep = IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8);
        using var service = new StarvedReaderBusService();
        using var actor = new ProtocolActor();
        using var channel = new IsoTpChannel(service, ep, FastOptions(), ownsService: false, actor);
        using var cts = new CancellationTokenSource();

        byte[] sf = { 0x03, 0x7F, 0x3E, 0x78, 0x00, 0x00, 0x00, 0x00 };
        service.Deliver(new CanFrameView(CanFrameType.Can20, 0x7E8, sf, FrameFlags.None), Stopwatch.GetTimestamp());

        await channel.SettleAsync(cts.Token).WaitAsync(ShortTimeout);

        channel.TryReceiveWithArrival(out var pdu).Should().BeTrue("settling took the frame through");
        pdu.Pdu.Should().Equal(0x7F, 0x3E, 0x78);
    }

    // Codex on #150: a discard given the caller's stamp drops what arrived before it and keeps
    // what arrived since -- so a caller that read the inbox after taking the stamp has seen
    // everything the discard drops, and a frame from between the read and the discard is not
    // lost.
    [Fact]
    public async Task A_Discard_Given_A_Stamp_Keeps_What_Arrived_After_It()
    {
        var ep = IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8);
        using var service = new StarvedReaderBusService();
        using var actor = new ProtocolActor();
        using var channel = new IsoTpChannel(service, ep, FastOptions(), ownsService: false, actor);

        long stamp = Stopwatch.GetTimestamp();
        // #171: Task.Delay(5) was a guess at how long it takes Stopwatch to tick past `stamp`.
        // What the test actually needs is a later reading, which a busy check gets deterministically
        // and without depending on the runner's scheduler granularity at all.
        while (Stopwatch.GetTimestamp() == stamp) { }
        byte[] sf = { 0x03, 0x7F, 0x3E, 0x78, 0x00, 0x00, 0x00, 0x00 };
        service.Deliver(new CanFrameView(CanFrameType.Can20, 0x7E8, sf, FrameFlags.None), Stopwatch.GetTimestamp());

        channel.DiscardPendingPdus(stamp).Should().Be(0, "the frame arrived after the stamp");
        await channel.SettleAsync().WaitAsync(ShortTimeout);
        channel.TryReceiveWithArrival(out var pdu).Should().BeTrue("a frame from after the stamp is kept");
        pdu.Pdu.Should().Equal(0x7F, 0x3E, 0x78);

        // The parameterless discard drops what arrived up to now -- by the actor refusing the
        // pumped frame, which never reaches the inbox and so is not in the count.
        service.Deliver(new CanFrameView(CanFrameType.Can20, 0x7E8, sf, FrameFlags.None), Stopwatch.GetTimestamp());
        channel.DiscardPendingPdus();
        await channel.SettleAsync().WaitAsync(ShortTimeout);
        channel.TryReceiveWithArrival(out _).Should().BeFalse("a frame from before the discard is dropped");
    }

    // Bugbot on #147: ignored means no effect at all -- a reassembly in flight survives a
    // stray First Frame that fits a Single Frame, and a short one (#27).
    [Theory]
    [InlineData(new byte[] { 0x10, 0x06, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 })] // fits a Single Frame
    [InlineData(new byte[] { 0x10, 0x14, 0x11, 0x22, 0x33 })]                   // short
    public async Task An_Ignored_First_Frame_Leaves_A_Reassembly_In_Flight_Alone(byte[] stray)
    {
        var ep = IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8);
        using var service = new StarvedReaderBusService();
        using var actor = new ProtocolActor();
        using var channel = new IsoTpChannel(service, ep, FastOptions(), ownsService: false, actor);

        var faults = new List<Exception>();
        channel.BackgroundExceptionOccurred += (_, ex) => { lock (faults) faults.Add(ex); };

        int ffData = IsoTpFrameCodec.FirstFrameMaxDataLength(isCanFd: false, usesAddressExtension: false, useLongLength: false);
        byte[] pdu = Enumerable.Range(0x40, 12).Select(i => (byte)i).ToArray();
        var peer = IsoTpEndpoint.Normal(0x7E8, 0x7E0);
        service.Deliver(new CanFrameView(CanFrameType.Can20, 0x7E8, IsoTpFrameCodec.BuildFirstFrame(peer, pdu.Length, pdu.AsSpan(0, ffData), isCanFd: false), FrameFlags.None));
        service.Deliver(new CanFrameView(CanFrameType.Can20, 0x7E8, stray, FrameFlags.None));
        service.Deliver(new CanFrameView(CanFrameType.Can20, 0x7E8, IsoTpFrameCodec.BuildConsecutiveFrame(peer, sequenceNumber: 1, pdu.AsSpan(ffData), isCanFd: false, padding: true, paddingByte: 0xCC), FrameFlags.None));
        channel.GetReceptionsInProgress();
        await actor.PostAsync(() => { }).WaitAsync(ShortTimeout);

        channel.TryReceiveWithArrival(out var received).Should().BeTrue("the transfer completed past the stray frame");
        received.Pdu.Should().Equal(pdu);
        lock (faults) faults.Should().BeEmpty();
    }

    // The boundary from the other side: one byte more than a Single Frame carries is a First
    // Frame, answered with Flow Control and reassembled.
    //
    // #362: this went red on a loaded host for two reasons that had nothing to do with the
    // boundary. The Flow Control is sent from the thread pool, not from the actor, so a round
    // trip on the actor said nothing about whether it had reached the double yet; and N_Cr ran
    // on the wall clock, so a stall past its 500 ms withdrew the reception before the second
    // count. The FC is now awaited on the double's own signal, and N_Cr is armed on a clock
    // nobody moves, so the reception can only be withdrawn by the actor refusing the frame.
    [Fact]
    public async Task A_First_Frame_One_Byte_Over_Single_Frame_Capacity_Is_Accepted()
    {
        var ep = IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8);
        using var service = new StarvedReaderBusService();
        var flowControlSent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.OnSendConfirmed = () => flowControlSent.TrySetResult(true);
        using var clock = new VirtualClock();
        var actor = clock.NewActor();
        using var channel = new IsoTpChannel(service, ep, FastOptions(), ownsService: false, actor);

        byte[] ff = { 0x10, 0x08, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 };
        service.Deliver(new CanFrameView(CanFrameType.Can20, 0x7E8, ff, FrameFlags.None));
        channel.GetReceptionsInProgress().Should().HaveCount(1);
        await clock.SettleAsync().WaitAsync(ShortTimeout);
        await flowControlSent.Task.WaitAsync(ShortTimeout);

        lock (service.Sent)
            service.Sent.Should().ContainSingle().Which[0].Should().Be(0x30, "FC.CTS");
        channel.GetReceptionsInProgress().Should().HaveCount(1,
            "the actor accepted the First Frame, and N_Cr cannot have run out on a clock that has not moved");
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

        // #171: "the trailing CF was processed within 50 ms" was a Task.Delay(50) guessing at
        // cross-bus delivery. staleCfSeen is a frame on the wire (the receiver's own bus,
        // busB); the SettleAsync after it is the actor round-trip that proves the receiver has
        // taken it off the mailbox -- both before the fresh exchange below can be attributed to
        // whichever run first.
        var staleCfSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        busB.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != unchecked((int)epPeer.TxCanId)) return;
            var data = e.CanFrame.Data.ToArray();
            if (data.Length > 0 && (data[0] >> 4) == 0x2) staleCfSeen.TrySetResult(true);
        };

        byte[] chunk = stalePayload.AsSpan(ffData).ToArray();
        var cf = IsoTpFrameCodec.BuildConsecutiveFrame(epPeer, sequenceNumber: 1, chunk,
            isCanFd: false, padding: true);
        busA.Transmit(CanFrame.Classic(unchecked((int)epPeer.TxCanId), cf));
        await staleCfSeen.Task.WaitAsync(ShortTimeout);
        await receiver.SettleAsync().WaitAsync(ShortTimeout);

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

    // The 2026-09-30 review (I6): a channel opened over a shared service (leaveOpen) outlives the
    // service when the owner disposes it first. The channel's reader ended quietly, and a receiver
    // waiting on the inbox waited for ever; a send, by contrast, failed at once. The inbox is
    // completed with the reason: what is buffered stays readable, then every receiver gets it.
    [Fact]
    public async Task A_Service_Disposed_Under_The_Channel_Faults_Every_Waiting_Receive()
    {
        var session = NewSession();
        using var bus = OpenClassic(session, 0);
        using var service = new CanBusService(bus);
        using var channel = IsoTpFactory.Open(service, IsoTpEndpoint.Normal(0x123, 0x321), FastOptions(), leaveOpen: true);
        var reported = new BackgroundReports();
        channel.BackgroundExceptionOccurred += reported.Add;

        var first = channel.ReceiveAsync();
        var second = channel.ReceiveAsync();
        service.Dispose();

        static async Task FaultsWithTheLossAsync(Task<byte[]> waiting)
        {
            Func<Task> act = () => waiting.WaitAsync(ShortTimeout);
            var thrown = (await act.Should().ThrowAsync<ObjectDisposedException>()).Which;
            thrown.ObjectName.Should().Be(nameof(ICanBusService));
        }

        await FaultsWithTheLossAsync(first);
        await FaultsWithTheLossAsync(second);

        // And a receive after the loss ends at once with the same reason instead of waiting.
        Func<Task> next = () => channel.ReceiveAsync().WaitAsync(ShortTimeout);
        await next.Should().ThrowAsync<ObjectDisposedException>();
        // Delivered on the thread pool (#356, T-2): awaited, then nothing else came before it.
        await reported.WaitForAsync(ex => ex is ObjectDisposedException);
        reported.Seen.Should().ContainSingle();
    }

    // A PDU that had already arrived is not lost with the service: it stays readable, and the
    // failure follows it.
    [Fact]
    public async Task A_PDU_Received_Before_The_Service_Goes_Is_Still_Delivered_First()
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);
        using var service = new CanBusService(busA);
        using var channel = IsoTpFactory.Open(service, IsoTpEndpoint.Normal(0x123, 0x321), FastOptions(), leaveOpen: true);

        // EmitPdu enqueues before it raises the event, so the event proves the PDU is in the inbox.
        var arrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.DatagramReceived += (_, _) => arrived.TrySetResult(true);
        busB.Transmit(CanFrame.Classic(0x321, new byte[] { 0x02, 0x0A, 0x0B, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC }));
        await arrived.Task.WaitAsync(ShortTimeout);

        service.Dispose();

        (await channel.ReceiveAsync().WaitAsync(ShortTimeout)).Should().Equal(0x0A, 0x0B);
        Func<Task> next = () => channel.ReceiveAsync().WaitAsync(ShortTimeout);
        await next.Should().ThrowAsync<ObjectDisposedException>();
    }

    // Review on #261: a discard that keeps the items from after its stamp writes them back, and a
    // completed inbox takes no writes. The PDUs that had arrived are still delivered, then the loss.
    [Fact]
    public async Task A_Discard_After_The_Service_Goes_Keeps_The_Pdus_From_After_Its_Stamp()
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);
        using var service = new CanBusService(busA);
        using var channel = IsoTpFactory.Open(service, IsoTpEndpoint.Normal(0x123, 0x321), FastOptions(), leaveOpen: true);

        var arrived = 0;
        var both = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.DatagramReceived += (_, _) => { if (Interlocked.Increment(ref arrived) == 2) both.TrySetResult(true); };
        busB.Transmit(CanFrame.Classic(0x321, new byte[] { 0x02, 0x0A, 0x0B, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC }));
        busB.Transmit(CanFrame.Classic(0x321, new byte[] { 0x02, 0x0C, 0x0D, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC }));
        await both.Task.WaitAsync(ShortTimeout);

        // The loss is reported after the inbox is completed with it: the discard below must meet
        // that state, not race the reader noticing the service went.
        var lost = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.BackgroundExceptionOccurred += (_, _) => lost.TrySetResult(true);
        service.Dispose();
        await lost.Task.WaitAsync(ShortTimeout);

        // A stamp of 1 is before everything: nothing is discarded, everything is kept.
        channel.DiscardPendingPdus(arrivedBefore: 1).Should().Be(0);

        (await channel.ReceiveAsync().WaitAsync(ShortTimeout)).Should().Equal(0x0A, 0x0B);
        (await channel.ReceiveAsync().WaitAsync(ShortTimeout)).Should().Equal(0x0C, 0x0D);
        Func<Task> next = () => channel.ReceiveAsync().WaitAsync(ShortTimeout);
        await next.Should().ThrowAsync<ObjectDisposedException>();
    }

    // The streaming receive follows the same order: what arrived, then the loss.
    [Fact]
    public async Task ReceiveAll_Yields_The_Buffered_Pdu_And_Then_Throws_The_Loss()
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);
        using var service = new CanBusService(busA);
        using var channel = IsoTpFactory.Open(service, IsoTpEndpoint.Normal(0x123, 0x321), FastOptions(), leaveOpen: true);

        var arrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.DatagramReceived += (_, _) => arrived.TrySetResult(true);
        busB.Transmit(CanFrame.Classic(0x321, new byte[] { 0x02, 0x0A, 0x0B, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC }));
        await arrived.Task.WaitAsync(ShortTimeout);
        service.Dispose();

        using var cts = new CancellationTokenSource(ShortTimeout);
        var seen = new List<byte[]>();
        Func<Task> act = async () =>
        {
            await foreach (var pdu in channel.ReceiveAllAsync(cts.Token))
                seen.Add(pdu);
        };
        await act.Should().ThrowAsync<ObjectDisposedException>();
        seen.Should().ContainSingle().Which.Should().Equal(0x0A, 0x0B);
    }

    // Review on #261: a discard after the loss has the retained items out of the inbox for a
    // moment. A receiver arriving then must not read the empty inbox as the end and throw the
    // loss ahead of the PDU that is about to be put back.
    [Fact]
    public async Task A_Receive_During_A_Discard_After_The_Loss_Still_Gets_The_Retained_Pdu_First()
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);
        using var service = new CanBusService(busA);
        using var channel = IsoTpFactory.Open(service, IsoTpEndpoint.Normal(0x123, 0x321), FastOptions(), leaveOpen: true);
        var concrete = (IsoTpChannel)channel;

        var arrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.DatagramReceived += (_, _) => arrived.TrySetResult(true);
        busB.Transmit(CanFrame.Classic(0x321, new byte[] { 0x02, 0x0A, 0x0B, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC }));
        await arrived.Task.WaitAsync(ShortTimeout);
        var lost = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.BackgroundExceptionOccurred += (_, _) => lost.TrySetResult(true);
        service.Dispose();
        await lost.Task.WaitAsync(ShortTimeout);

        Task<byte[]>? receive = null;
        concrete.DiscardGapObserver = () =>
        {
            // On the actor, with the retained PDU out of the inbox: a receiver starts now and
            // gets a moment to meet the empty inbox. No signal says "it looked and waited", so
            // this is a negative window: it can only pass falsely on a slow host.
            receive = Task.Run(() => channel.ReceiveAsync());
            Thread.Sleep(300);
            receive.IsCompleted.Should().BeFalse("the retained PDU is about to be put back");
        };
        channel.DiscardPendingPdus(arrivedBefore: 1).Should().Be(0);

        (await receive!.WaitAsync(ShortTimeout)).Should().Equal(0x0A, 0x0B);
    }

    // I-3 of the 2026-10-21 review (#353): a discard has the PDUs it retains out of the inbox for
    // a moment. TryReceiveWithArrival from another thread in that moment reported "empty", though
    // the PDU that arrived after the discard's stamp was about to be put back; the async receive
    // waits the gap out. The observer holds the gap open from the actor, so the call below is
    // made with the inbox empty for certain, and the gap is released only from behind the call:
    // an answer given before the write-back can only be "false", and the delay merely lengthens
    // the wait of a call that does wait.
    [Fact]
    public async Task A_TryReceive_During_A_Discard_Waits_For_The_Pdu_The_Discard_Retains()
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);
        using var service = new CanBusService(busA);
        using var channel = IsoTpFactory.Open(service, IsoTpEndpoint.Normal(0x123, 0x321), FastOptions(), leaveOpen: true);
        var concrete = (IsoTpChannel)channel;

        var arrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.DatagramReceived += (_, _) => arrived.TrySetResult(true);
        busB.Transmit(CanFrame.Classic(0x321, new byte[] { 0x02, 0x0A, 0x0B, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC }));
        await arrived.Task.WaitAsync(ShortTimeout);

        using var inGap = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        concrete.DiscardGapObserver = () =>
        {
            inGap.Set();
            release.Wait(ShortTimeout);
        };
        var discard = Task.Run(() => channel.DiscardPendingPdus(arrivedBefore: 1));
        inGap.Wait(ShortTimeout).Should().BeTrue("the discard reached its gap");

        var releasing = Task.Delay(TimeSpan.FromMilliseconds(100)).ContinueWith(_ => release.Set());
        bool taken = channel.TryReceiveWithArrival(out var pdu);
        await releasing;
        taken.Should().BeTrue("the PDU the discard retains is pending, not gone");
        pdu.Pdu.Should().Equal(0x0A, 0x0B);
        (await discard.WaitAsync(ShortTimeout)).Should().Be(0);
    }

    // Runs `read` on a thread of its own, started now, and returns once that read has either
    // finished or blocked -- a thread blocked on a lock is in WaitSleepJoin, and nothing before the
    // inbox's read gate on a receive path waits. A signal rather than a delay: called while a
    // discard holds the gate, it tells "the read waits for the discard" from "the read ran past
    // it" without a timing margin (#362). The returned task carries the read's outcome.
    private static Task<T> StartReadAndLetItReachTheGate<T>(Func<T> read)
    {
        var task = new Task<T>(read);
        var thread = new Thread(() => task.RunSynchronously());
        thread.IsBackground = true;
        thread.Start();
        SpinWait.SpinUntil(() => task.IsCompleted || (thread.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0,
            ShortTimeout).Should().BeTrue("the read either finished or blocked");
        return task;
    }

    // #383 (Codex on #376): a receive was not serialised with a discard's drain. One made while
    // the discard was reading the inbox took the PDU the discard was about to drop -- a stale
    // response reached the caller, and the discard's count was one short. The observer starts the
    // receive from the actor, once the discard holds the inbox and before it reads anything, and
    // lets the discard go on only once the receive has finished or blocked. Serialised, it blocks
    // until the discard is done and then finds only what the discard kept.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_Receive_Concurrent_With_A_Discard_Takes_Nothing_The_Discard_Drops(bool awaiting)
    {
        var ep = IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8);
        using var service = new StarvedReaderBusService();
        using var actor = new ProtocolActor();
        using var channel = new IsoTpChannel(service, ep, FastOptions(), ownsService: false, actor);

        // Stamped either side of the discard's own stamp, 200: the first is what it drops.
        service.Deliver(new CanFrameView(CanFrameType.Can20, 0x7E8,
            new byte[] { 0x02, 0xAA, 0x01, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC }, FrameFlags.None), 100);
        service.Deliver(new CanFrameView(CanFrameType.Can20, 0x7E8,
            new byte[] { 0x02, 0xBB, 0x02, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC }, FrameFlags.None), 300);
        await channel.SettleAsync().WaitAsync(ShortTimeout);

        Task<byte[]?>? read = null;
        channel.DiscardDrainStartingObserver = () => read = StartReadAndLetItReachTheGate<byte[]?>(() =>
            awaiting
                ? channel.ReceiveWithArrivalAsync().GetAwaiter().GetResult().Pdu
                : channel.TryReceiveWithArrival(out var pdu) ? pdu.Pdu : null);

        channel.DiscardPendingPdus(arrivedBefore: 200).Should().Be(1, "the PDU stamped before the discard is dropped by it");
        channel.DiscardDrainStartingObserver = null;

        (await read!.WaitAsync(ShortTimeout)).Should().Equal(new byte[] { 0xBB, 0x02 },
            "a receive concurrent with the discard reads after it, and finds what it kept");
        channel.TryReceiveWithArrival(out _).Should().BeFalse("nothing else was kept");
    }

    // Codex on #394: a receive whose token is cancelled while it waits for a discard must not then
    // take a PDU. The token is checked before the read; checked only before the wait for the
    // discard, a cancellation made during the wait was missed and the retained PDU consumed.
    [Fact]
    public async Task A_Receive_Cancelled_While_It_Waits_For_A_Discard_Takes_Nothing()
    {
        var ep = IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8);
        using var service = new StarvedReaderBusService();
        using var actor = new ProtocolActor();
        using var channel = new IsoTpChannel(service, ep, FastOptions(), ownsService: false, actor);

        service.Deliver(new CanFrameView(CanFrameType.Can20, 0x7E8,
            new byte[] { 0x02, 0xBB, 0x02, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC }, FrameFlags.None), 300);
        await channel.SettleAsync().WaitAsync(ShortTimeout);

        using var cts = new CancellationTokenSource();
        Task<byte[]>? read = null;
        channel.DiscardGapObserver = () =>
        {
            // The token is live when the receive starts; it is cancelled only once the receive
            // waits at the gate the discard holds.
            read = StartReadAndLetItReachTheGate(() =>
                channel.ReceiveWithArrivalAsync(cts.Token).GetAwaiter().GetResult().Pdu);
            cts.Cancel();
        };

        channel.DiscardPendingPdus(arrivedBefore: 200).Should().Be(0, "the PDU arrived after the discard's stamp");
        channel.DiscardGapObserver = null;

        Func<Task> act = () => read!.WaitAsync(ShortTimeout);
        await act.Should().ThrowAsync<OperationCanceledException>();
        channel.TryReceiveWithArrival(out var left).Should().BeTrue("the cancelled receive took nothing");
        left.Pdu.Should().Equal(0xBB, 0x02);
    }

    // #262: a Dispose that arrives while a discard has the retained PDU out of the inbox must not
    // lose it. The inbox used to be completed from the disposing thread, and the write-back that
    // followed found it completed.
    [Fact]
    public async Task A_Dispose_During_A_Discard_Does_Not_Drop_The_Pdu_The_Discard_Retains()
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);
        using var service = new CanBusService(busA);
        var channel = IsoTpFactory.Open(service, IsoTpEndpoint.Normal(0x123, 0x321), FastOptions(), leaveOpen: true);
        var concrete = (IsoTpChannel)channel;

        var arrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.DatagramReceived += (_, _) => arrived.TrySetResult(true);
        busB.Transmit(CanFrame.Classic(0x321, new byte[] { 0x02, 0x0A, 0x0B, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC }));
        await arrived.Task.WaitAsync(ShortTimeout);

        Task? disposal = null;
        concrete.DiscardGapObserver = () =>
        {
            // On the actor, with the retained PDU out of the inbox: another thread disposes the
            // channel now. No signal says "it got as far as it will", so this is a negative
            // window: it can only pass falsely on a slow host.
            disposal = Task.Run(channel.Dispose);
            Thread.Sleep(300);
        };
        channel.DiscardPendingPdus(arrivedBefore: 1).Should().Be(0);
        await disposal!.WaitAsync(ShortTimeout);

        // What is left can still be read after the disposal: the PDU the discard retained.
        (await channel.ReceiveAsync().WaitAsync(ShortTimeout)).Should().Equal(0x0A, 0x0B);
    }

    // Review on #275: the receivers are let go by the disposal itself, not by the actor getting
    // round to it. With the actor busy (a callback that does not return), a pending receive must
    // still end when the channel is disposed.
    [Fact]
    public async Task A_Pending_Receive_Ends_On_Dispose_Although_The_Actor_Is_Busy()
    {
        var session = NewSession();
        using var bus = OpenClassic(session, 0);
        using var service = new CanBusService(bus);
        using var actor = new ProtocolActor();
        var channel = new IsoTpChannel(service, IsoTpEndpoint.Normal(0x123, 0x321), FastOptions(),
            ownsService: false, actor);
        using var release = new ManualResetEventSlim();
        var busy = actor.PostAsync(() => release.Wait(TimeSpan.FromSeconds(30)));
        var receiving = channel.ReceiveAsync();

        var disposal = Task.Run(channel.Dispose);
        try
        {
            // The actor cannot run the cleanup Dispose posts to it, and the receive ends anyway.
            await Assert.ThrowsAsync<InvalidOperationException>(() => receiving.WaitAsync(ShortTimeout));
        }
        finally
        {
            release.Set();
            await busy;
            await disposal;
        }
    }

    // #449: the channel's own actor, disposed while a callback still holds its loop, gives up on
    // the join after its shutdown bound and reports that as a TimeoutException through its own
    // BackgroundExceptionOccurred. The channel forwards it only while its handler is still
    // attached, so the handler comes off after that Dispose, not before (as #437 for the J1939-TP
    // channel). The callback is held until the finally, so the bound is always exceeded: nothing
    // is asserted against the clock.
    [Fact]
    public async Task The_Shutdown_Timeout_Of_The_Channels_Own_Actor_Reaches_Its_Subscribers()
    {
        var session = NewSession();
        using var bus = OpenClassic(session, 0);
        using var service = new CanBusService(bus);
        using var release = new ManualResetEventSlim(); // declared first, so it outlives the channel
        var actor = new ProtocolActor(ActorExecutionMode.DedicatedThread, null, null, shutdownTimeout: TimeSpan.FromMilliseconds(50));
        using var channel = new IsoTpChannel(service, IsoTpEndpoint.Normal(0x123, 0x321), FastOptions(),
            ownsService: false, actor, ownsActor: true); // disposes the actor it owns
        // Reported on the thread pool (#356, T-2), so possibly after Dispose has returned: awaited
        // on a signal. A report the channel never forwards leaves the signal unset and the wait fails.
        var timedOut = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.BackgroundExceptionOccurred += (_, ex) => { if (ex is TimeoutException) timedOut.TrySetResult(ex); };

        var held = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            actor.Post(() =>
            {
                held.TrySetResult(true);
                release.Wait(TimeSpan.FromSeconds(30));
            });
            await held.Task.WaitAsync(ShortTimeout);
            channel.Dispose(); // the actor's join gives up on the held callback and reports it

            (await timedOut.Task.WaitAsync(ShortTimeout)).Should().BeOfType<TimeoutException>(
                "the channel owns that actor, and its shutdown timeout is a background failure of the channel's");
        }
        finally
        {
            release.Set();
        }
    }

    // Review on #275: the inbox's writer stays open now, so a producer callback that passed the
    // disposed guard before the disposal and writes after it must not put anything in it. The
    // callback is simulated by calling the producers directly after the disposal.
    [Fact]
    public async Task A_Producer_That_Writes_After_The_Disposal_Does_Not_Queue_Anything()
    {
        var session = NewSession();
        using var bus = OpenClassic(session, 0);
        var channel = IsoTpFactory.Open(bus, IsoTpEndpoint.Normal(0x123, 0x321), FastOptions());
        var concrete = (IsoTpChannel)channel;
        channel.Dispose();

        var emit = typeof(IsoTpChannel).GetMethod("EmitPdu", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            null, new[] { typeof(byte[]), typeof(long) }, null)!;
        var abort = typeof(IsoTpChannel).GetMethod("AbortRx", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        emit.Invoke(concrete, new object[] { new byte[] { 0x0A, 0x0B }, 1L });
        abort.Invoke(concrete, new object[] { new IsoTpException("a reception that a late callback aborts") });

        // The end of the channel, not the PDU or the abort that came after it.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => channel.ReceiveAsync().WaitAsync(ShortTimeout));
        ex.Message.Should().Contain("disposed");
    }

    // The streaming receive ends gracefully when the channel is disposed under it.
    [Fact]
    public async Task ReceiveAll_Ends_When_The_Channel_Is_Disposed()
    {
        var session = NewSession();
        using var bus = OpenClassic(session, 0);
        using var channel = IsoTpFactory.Open(bus, IsoTpEndpoint.Normal(0x123, 0x321), FastOptions());

        using var cts = new CancellationTokenSource(ShortTimeout);
        var seen = 0;
        var reading = Task.Run(async () =>
        {
            await foreach (var _ in channel.ReceiveAllAsync(cts.Token)) seen++;
        });
        await Task.Delay(50); // the enumeration is waiting on the empty inbox
        channel.Dispose();

        await reading.WaitAsync(ShortTimeout);
        seen.Should().Be(0);
    }

    // Review on #261: a canceled token must not consume a PDU that is already buffered.
    [Fact]
    public async Task A_Canceled_Token_Does_Not_Consume_A_Buffered_Pdu()
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);
        using var channel = IsoTpFactory.Open(busA, IsoTpEndpoint.Normal(0x123, 0x321), FastOptions());

        var arrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.DatagramReceived += (_, _) => arrived.TrySetResult(true);
        busB.Transmit(CanFrame.Classic(0x321, new byte[] { 0x02, 0x0A, 0x0B, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC }));
        await arrived.Task.WaitAsync(ShortTimeout);

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Func<Task> receive = () => channel.ReceiveAsync(canceled.Token);
        await receive.Should().ThrowAsync<OperationCanceledException>();
        Func<Task> all = async () =>
        {
            await foreach (var _ in channel.ReceiveAllAsync(canceled.Token)) { }
        };
        await all.Should().ThrowAsync<OperationCanceledException>();

        // Still there for a receive that is not canceled.
        (await channel.ReceiveAsync().WaitAsync(ShortTimeout)).Should().Equal(0x0A, 0x0B);
    }

    // Review on #261: a caller that pumps the subscription (GetReceptionsInProgress, a settle, a
    // discard) can have taken the last frame and not yet posted it when the subscription ends. The
    // loss must not overtake that frame, or a receiver throws it before a PDU that was received.
    [Fact]
    public async Task The_Loss_Does_Not_Overtake_A_Frame_A_Pumping_Caller_Is_About_To_Post()
    {
        var service = new StarvedReaderBusService { EndSubscriptionOnWake = true };
        using var actor = new ProtocolActor();
        using var channel = new IsoTpChannel(service, IsoTpEndpoint.Normal(0x123, 0x321), FastOptions(),
            ownsService: false, actor);
        using var gate = new ManualResetEventSlim(false);
        service.TryReadGate = gate;

        var receive = channel.ReceiveAsync();
        service.Deliver(new CanFrameView(CanFrameType.Can20, 0x321,
            new byte[] { 0x02, 0x0A, 0x0B, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC }, FrameFlags.None));

        // A caller pumps: it takes the frame and is held before it posts it. Awaited on the
        // double's signal, not on a delay (#362): with Task.Delay(100) a pump the thread pool had
        // not started yet let the subscription end first, the loss was posted ahead of the
        // frame, and the receive threw it -- a red that said nothing about the code.
        var pump = Task.Run(() => channel.GetReceptionsInProgress());
        await service.FrameHeldAtGate.WaitAsync(ShortTimeout);
        service.WakeReader(); // the subscription ends
        // No signal says "the reader reached the post and waited for the lock", so this is a
        // negative window: it can only pass falsely on a slow host.
        await Task.Delay(200);

        gate.Set(); // the pump posts its frame
        await pump.WaitAsync(ShortTimeout);
        (await receive.WaitAsync(ShortTimeout)).Should().Equal(0x0A, 0x0B);
    }

    // The reader itself failing is the same loss: the inbox ends with that failure, for every
    // receiver, and it is reported once.
    [Fact]
    public async Task A_Failing_Subscription_Ends_The_Inbox_With_Its_Failure()
    {
        var service = new StarvedReaderBusService { ReaderFault = new InvalidOperationException("the demux broke") };
        using var actor = new ProtocolActor();
        using var channel = new IsoTpChannel(service, IsoTpEndpoint.Normal(0x123, 0x321), FastOptions(),
            ownsService: false, actor);
        var reported = new List<Exception>();
        var firstReport = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.BackgroundExceptionOccurred += (_, ex) =>
        {
            lock (reported) reported.Add(ex);
            firstReport.TrySetResult(true);
        };

        var waiting = channel.ReceiveAsync();
        service.WakeReader();

        Func<Task> act = () => waiting.WaitAsync(ShortTimeout);
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be("the demux broke");
        // The reader posts the loss to the inbox and raises the report after it: the receive can
        // complete first, so the report is awaited rather than read at once (#270).
        await firstReport.Task.WaitAsync(ShortTimeout);
        lock (reported) reported.Should().ContainSingle().Which.Message.Should().Be("the demux broke");
    }

    // I-2 of the 2026-10-21 review (#353): the reader took every OperationCanceledException for
    // the channel's own Dispose and returned without publishing anything. The shipped service
    // only cancels the wait for the channel's token, but IsoTp.Open takes any ICanBusService,
    // and one that ends its subscription by cancelling the wait left every receiver waiting for
    // ever -- I6 again, through another exception type. It is the loss like any other failure.
    [Fact]
    public async Task A_Subscription_Ending_With_A_Foreign_Cancellation_Ends_The_Inbox_With_It()
    {
        var fault = new OperationCanceledException("the service shut down");
        var service = new StarvedReaderBusService { ReaderFault = fault };
        using var actor = new ProtocolActor();
        using var channel = new IsoTpChannel(service, IsoTpEndpoint.Normal(0x123, 0x321), FastOptions(),
            ownsService: false, actor);
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.BackgroundExceptionOccurred += (_, ex) => reported.TrySetResult(ex);

        var waiting = channel.ReceiveAsync();
        service.WakeReader();

        Func<Task> act = () => waiting.WaitAsync(ShortTimeout);
        (await act.Should().ThrowAsync<OperationCanceledException>()).Which.Should().BeSameAs(fault);
        (await reported.Task.WaitAsync(ShortTimeout)).Should().BeSameAs(fault);

        Func<Task> later = () => channel.ReceiveAsync().WaitAsync(ShortTimeout);
        (await later.Should().ThrowAsync<OperationCanceledException>()).Which.Should().BeSameAs(fault);
    }

    // The reader's generic-catch filter does not handle these. They are still reader failures:
    // a receive that is already waiting, and one that starts afterwards, both have to observe
    // the same exception, and it has to be raised once. The subscription throws the instance,
    // so a passing test is the inbox-loss signal, not a stand-in for it. StackOverflowException
    // and AccessViolationException are left out: an explicit throw is catchable, but a real
    // stack overflow aborts the process. ThreadAbortException cannot be constructed here.
    [Theory]
    [InlineData(nameof(OutOfMemoryException))]
    [InlineData(nameof(BadImageFormatException))]
    [InlineData(nameof(AppDomainUnloadedException))]
    [InlineData(nameof(CannotUnloadAppDomainException))]
    public async Task An_Excluded_Reader_Failure_Still_Wakes_Waiting_And_Later_Receives(string kind)
    {
        var fault = ExcludedReaderFailure(kind);
        var service = new StarvedReaderBusService { ReaderFault = fault };
        using var actor = new ProtocolActor();
        using var channel = new IsoTpChannel(service, IsoTpEndpoint.Normal(0x123, 0x321), FastOptions(),
            ownsService: false, actor);
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.BackgroundExceptionOccurred += (_, ex) => reported.TrySetResult(ex);

        var first = channel.ReceiveAsync();
        var second = channel.ReceiveAsync();
        service.WakeReader();

        Func<Task> firstAct = () => first.WaitAsync(ShortTimeout);
        (await firstAct.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(fault);
        Func<Task> secondAct = () => second.WaitAsync(ShortTimeout);
        (await secondAct.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(fault);

        (await reported.Task.WaitAsync(ShortTimeout)).Should().BeSameAs(fault);

        Func<Task> later = () => channel.ReceiveAsync().WaitAsync(ShortTimeout);
        (await later.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(fault);

        // Publishing the loss must not swallow the failure: the reader task still faults
        // with the same instance. WaitAsync observes that fault, or times out if it was swallowed.
        var reader = (Task)typeof(IsoTpChannel).GetField("_readerTask",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(channel)!;
        Func<Task> readerFaulted = () => reader.WaitAsync(ShortTimeout);
        (await readerFaulted.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(fault);
    }

    private static Exception ExcludedReaderFailure(string kind) => kind switch
    {
        nameof(OutOfMemoryException) => new OutOfMemoryException("the reader failed"),
        nameof(BadImageFormatException) => new BadImageFormatException("the reader failed"),
        nameof(AppDomainUnloadedException) => new AppDomainUnloadedException("the reader failed"),
        nameof(CannotUnloadAppDomainException) => new CannotUnloadAppDomainException("the reader failed"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not an excluded reader failure under test."),
    };

    // A reassembly under way dies with the subscription: it is withdrawn, so
    // GetReceptionsInProgress does not keep reporting a transfer nobody will complete.
    [Fact]
    public async Task A_Reception_Under_Way_Is_Withdrawn_When_The_Service_Goes()
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);
        using var service = new CanBusService(busA);
        using var channel = IsoTpFactory.Open(service, IsoTpEndpoint.Normal(0x123, 0x321), FastOptions(), leaveOpen: true);

        var waiting = channel.ReceiveAsync();
        busB.Transmit(CanFrame.Classic(0x321, new byte[] { 0x10, 0x14, 1, 2, 3, 4, 5, 6 })); // first frame of 20 bytes
        (await WaitForReceptionsAsync(channel, 1)).Should().HaveCount(1);

        service.Dispose();

        Func<Task> act = () => waiting.WaitAsync(ShortTimeout);
        await act.Should().ThrowAsync<ObjectDisposedException>();
        channel.GetReceptionsInProgress().Should().BeEmpty();
    }

    // The actor an owner disposed first cannot write the inbox, but the loss is still reported.
    [Fact]
    public async Task The_Loss_Is_Still_Reported_When_The_Injected_Actor_Is_Already_Gone()
    {
        var session = NewSession();
        using var bus = OpenClassic(session, 0);
        using var service = new CanBusService(bus);
        using var actor = new ProtocolActor();
        using var channel = new IsoTpChannel(service, IsoTpEndpoint.Normal(0x123, 0x321), FastOptions(),
            ownsService: false, actor);
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.BackgroundExceptionOccurred += (_, ex) => reported.TrySetResult(ex);

        actor.Dispose();
        service.Dispose();

        (await reported.Task.WaitAsync(ShortTimeout)).Should().BeOfType<ObjectDisposedException>();
    }

    // I7: Dispose can fall between SendAsync's disposed check and the post of the send to the
    // actor. Its cleanup is then queued ahead of the send, finds nothing in flight, and the send
    // that runs afterwards used to begin on a disposed channel: a frame on the bus, no one to
    // complete the call. The actor double below makes Dispose run at exactly that point.
    [Fact]
    public async Task A_Dispose_Between_The_Check_And_The_Post_Fails_The_Send_Before_It_Reaches_The_Bus()
    {
        using var bus = ControllableBus.EchoCapable(NewSession());
        using var service = new CanBusService(bus);
        using var inner = new ProtocolActor();
        using var actor = new DisposeOnFirstPostActor(inner);
        using var channel = new IsoTpChannel(service, IsoTpEndpoint.Normal(0x123, 0x321), FastOptions(),
            ownsService: false, actor);
        actor.Channel = channel;

        Func<Task> act = () => channel.SendAsync(new byte[] { 0x01, 0x02 }).WaitAsync(ShortTimeout);

        await act.Should().ThrowAsync<ObjectDisposedException>();
        bus.TransmitCount.Should().Be(0, "a send that began after the dispose would have put its frame on the bus");
    }

    /// <summary>
    /// Runs the channel's Dispose on another thread when the first send is posted and holds that
    /// post until the dispose has posted its own cleanup, so the cleanup is queued first.
    /// </summary>
    private sealed class DisposeOnFirstPostActor : IProtocolActor
    {
        private readonly IProtocolActor _inner;
        private readonly ManualResetEventSlim _cleanupPosted = new();
        private int _posts;

        public DisposeOnFirstPostActor(IProtocolActor inner) => _inner = inner;

        public IDisposable? Channel { get; set; }

        public event EventHandler<Exception>? BackgroundExceptionOccurred
        {
            add => _inner.BackgroundExceptionOccurred += value;
            remove => _inner.BackgroundExceptionOccurred -= value;
        }

        public void Post(Action work)
        {
            if (Interlocked.Increment(ref _posts) == 1)
            {
                var channel = Channel!;
                _ = Task.Run(channel.Dispose);
                if (!_cleanupPosted.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Dispose did not post its cleanup.");
                _inner.Post(work);
                return;
            }

            _inner.Post(work);
            _cleanupPosted.Set();
        }

        public Task PostAsync(Action work, CancellationToken cancellationToken = default)
            => _inner.PostAsync(work, cancellationToken);

        public Task<T> PostAsync<T>(Func<T> work, CancellationToken cancellationToken = default)
            => _inner.PostAsync(work, cancellationToken);

        public IDisposable Schedule(TimeSpan delay, Action callback) => _inner.Schedule(delay, callback);

        public void Dispose() => _cleanupPosted.Dispose();
    }

    // I8: a negative (or zero) protocol timer made the deadline scheduler throw after the send was
    // on the wire or the reception begun, leaving it with no deadline; reject it where LocalStMin
    // already is.
    [Theory]
    [InlineData(nameof(IsoTpChannelOptions.NAs), 0)]
    [InlineData(nameof(IsoTpChannelOptions.NAs), -1)]
    [InlineData(nameof(IsoTpChannelOptions.NBs), 0)]
    [InlineData(nameof(IsoTpChannelOptions.NBs), -1)]
    [InlineData(nameof(IsoTpChannelOptions.NCr), 0)]
    [InlineData(nameof(IsoTpChannelOptions.NCr), -1)]
    public void A_Protocol_Timer_That_Cannot_Arm_Is_Rejected_At_Open(string option, int milliseconds)
    {
        var session = NewSession();
        using var bus = OpenClassic(session, 0);
        var value = TimeSpan.FromMilliseconds(milliseconds);
        var options = option switch
        {
            nameof(IsoTpChannelOptions.NAs) => new IsoTpChannelOptions { NAs = value },
            nameof(IsoTpChannelOptions.NBs) => new IsoTpChannelOptions { NBs = value },
            _ => new IsoTpChannelOptions { NCr = value },
        };

        Action act = () => IsoTpFactory.Open(bus, IsoTpEndpoint.Normal(0x250, 0x251), options);
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName(option);
    }

    // #352 (R-4): N_As is handed to SendConfirmedAsync as the confirm timeout. A channel that sets it
    // to TimeSpan.MaxValue as "no N_As limit" opens, and its sends must go out -- RawCan clamps a
    // timeout above the timer's reach instead of refusing it. Single frame and multi-frame, since
    // the First Frame and the Consecutive Frames are confirmed on separate sites.
    [Theory]
    [InlineData(3)]
    [InlineData(20)]
    public async Task A_Channel_With_An_Unbounded_NAs_Sends(int length)
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);
        var unbounded = new IsoTpChannelOptions { UseCanFd = false, UsePadding = true, NAs = TimeSpan.MaxValue };

        using var sender = IsoTpFactory.Open(busA, IsoTpEndpoint.Normal(0x252, 0x253), unbounded);
        using var receiver = IsoTpFactory.Open(busB, IsoTpEndpoint.Normal(0x253, 0x252), FastOptions());
        using var cts = new CancellationTokenSource(ShortTimeout);
        var receive = receiver.ReceiveAsync(cts.Token);

        var pdu = Enumerable.Range(1, length).Select(i => (byte)i).ToArray();
        await sender.SendAsync(pdu).WaitAsync(ShortTimeout);

        (await receive.WaitAsync(ShortTimeout)).Should().Equal(pdu);
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
    // taken ownership of the PDU (here: the bus layer throws out of SendConfirmedAsync), the channel
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

        // #171: "give any straggling actor work time to hit the wire" was a Task.Delay(100). A
        // round-trip on the actor we parked and released proves [begin, cleanup] has run. An
        // errant transmit would still leave through SendFrameOnBus's Task.Run, which the
        // round-trip cannot see, so the original window follows it.
        await actor.PostAsync(() => { }).WaitAsync(ShortTimeout);
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

    // Test gap of the 2026-10-21 review (#353, mutation isotp-M4): the test above exchanges
    // Single Frames only, so the address-extension byte of the Flow Control -- the one frame
    // the *receiver* builds -- was pinned by nothing at channel level. A multi-frame transfer in
    // each direction needs the FC to carry the sender's source address, or the sender drops it
    // and times out on N_Bs. The sniffer checks the byte itself, so a swap that happened to
    // round-trip would still be caught.
    [Fact]
    public async Task ExtendedAddressing_With_Distinct_Source_And_Target_Transfers_MultiFrame_Both_Ways()
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);
        using var snifferBus = OpenClassic(session, 2);

        var alice = IsoTpEndpoint.Extended(txCanId: 0x310, rxCanId: 0x311,
            sourceAddress: 0xAA, targetAddress: 0xBB);
        var bob = IsoTpEndpoint.Extended(txCanId: 0x311, rxCanId: 0x310,
            sourceAddress: 0xBB, targetAddress: 0xAA);

        var flowControls = new List<(uint CanId, byte AddressExtension)>();
        var bothSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        snifferBus.FrameObserved += (_, e) =>
        {
            var payload = e.CanFrame.Data.ToArray();
            if (payload.Length >= 2 && (payload[1] >> 4) == 0x3)
            {
                lock (flowControls)
                {
                    flowControls.Add(((uint)e.CanFrame.ID, payload[0]));
                    if (flowControls.Count >= 2) bothSeen.TrySetResult(true);
                }
            }
        };

        using var aliceChannel = IsoTpFactory.Open(busA, alice, FastOptions());
        using var bobChannel = IsoTpFactory.Open(busB, bob, FastOptions());

        // 30 bytes with an AE byte: FF carries 5, CFs 6 each -- five frames and one FC per way.
        byte[] a2b = Enumerable.Range(0, 30).Select(i => (byte)i).ToArray();
        using var bobTimeout = new CancellationTokenSource(ShortTimeout);
        var recvOnBob = bobChannel.ReceiveAsync(bobTimeout.Token);
        await aliceChannel.SendAsync(a2b).WaitAsync(ShortTimeout);
        (await recvOnBob).Should().Equal(a2b);

        byte[] b2a = Enumerable.Range(100, 30).Select(i => (byte)i).ToArray();
        using var aliceTimeout = new CancellationTokenSource(ShortTimeout);
        var recvOnAlice = aliceChannel.ReceiveAsync(aliceTimeout.Token);
        await bobChannel.SendAsync(b2a).WaitAsync(ShortTimeout);
        (await recvOnAlice).Should().Equal(b2a);

        // Bob's FC addresses Alice (AE = Alice's source address, Bob's target); Alice's FC
        // addresses Bob. The sniffer is one more hub delivery, ordered against nothing above: its
        // second frame is waited for rather than assumed, and the two are compared as a set --
        // the sniffer has recorded them in either order (#362). Each pair still ties the byte to
        // the identifier that carried it, so a swapped address extension is caught either way.
        await bothSeen.Task.WaitAsync(ShortTimeout);
        lock (flowControls)
            flowControls.Should().BeEquivalentTo(new[] { (0x311u, (byte)0xAA), (0x310u, (byte)0xBB) });
    }

    // Test gap of the 2026-10-21 review (#353, carried over from 2026-09-30 §3): Mixed and
    // NormalFixed addressing had no channel-level test, only codec and property tests. One
    // record per frame a sniffer saw: identifier, identifier format, and the first two payload
    // bytes -- where the address extension and the PCI sit under the two modes.
    private readonly record struct SniffedFrame(uint CanId, bool IsExtendedId, byte Byte0, byte Byte1);

    // Records every frame the sniffer sees and completes once it has seen `expected` of them. The
    // sniffer is one more hub delivery, ordered against nothing the channels do: the frames are
    // waited for, and compared as a multiset rather than in order (#362).
    private static (List<SniffedFrame> Frames, Task AllSeen) Sniff(ICanBus sniffer, int expected)
    {
        var frames = new List<SniffedFrame>();
        var allSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        sniffer.FrameObserved += (_, e) =>
        {
            var payload = e.CanFrame.Data.ToArray();
            lock (frames)
            {
                frames.Add(new SniffedFrame((uint)e.CanFrame.ID, e.CanFrame.IsExtendedFrame,
                    payload.Length > 0 ? payload[0] : (byte)0, payload.Length > 1 ? payload[1] : (byte)0));
                if (frames.Count >= expected) allSeen.TrySetResult(true);
            }
        };
        return (frames, allSeen.Task);
    }

    // Mixed addressing: one address-extension byte, the same in both directions, first on every
    // frame -- the Flow Control the receiver builds included -- with the PCI behind it; 11-bit and
    // 29-bit identifiers alike. A Single Frame one way, then a multi-frame transfer each way.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedAddressing_Transfers_Single_And_MultiFrame_Both_Ways(bool extendedIds)
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);
        using var snifferBus = OpenClassic(session, 2);

        uint aliceTx = extendedIds ? 0x18CE20F1u : 0x320u;
        uint bobTx = extendedIds ? 0x18CEF120u : 0x321u;
        var alice = IsoTpEndpoint.Mixed(txCanId: aliceTx, rxCanId: bobTx, addressExtension: 0x5A, isExtendedCanId: extendedIds);
        var bob = IsoTpEndpoint.Mixed(txCanId: bobTx, rxCanId: aliceTx, addressExtension: 0x5A, isExtendedCanId: extendedIds);

        // 30 bytes behind the AE byte: FF carries 5, CFs 6 each -- FF, five CFs and one FC per way.
        var expected = new List<SniffedFrame> { new(aliceTx, extendedIds, 0x5A, 0x03) };
        foreach (var (sender, receiver) in new[] { (aliceTx, bobTx), (bobTx, aliceTx) })
        {
            expected.Add(new(sender, extendedIds, 0x5A, 0x10));
            expected.Add(new(receiver, extendedIds, 0x5A, 0x30));
            for (byte sn = 1; sn <= 5; sn++)
                expected.Add(new(sender, extendedIds, 0x5A, (byte)(0x20 | sn)));
        }
        var (frames, allSeen) = Sniff(snifferBus, expected.Count);

        using var aliceChannel = IsoTpFactory.Open(busA, alice, FastOptions());
        using var bobChannel = IsoTpFactory.Open(busB, bob, FastOptions());

        byte[] single = { 0x11, 0x22, 0x33 };
        using var singleTimeout = new CancellationTokenSource(ShortTimeout);
        var singleOnBob = bobChannel.ReceiveAsync(singleTimeout.Token);
        await aliceChannel.SendAsync(single).WaitAsync(ShortTimeout);
        (await singleOnBob).Should().Equal(single);

        byte[] a2b = Enumerable.Range(0, 30).Select(i => (byte)i).ToArray();
        using var bobTimeout = new CancellationTokenSource(ShortTimeout);
        var recvOnBob = bobChannel.ReceiveAsync(bobTimeout.Token);
        await aliceChannel.SendAsync(a2b).WaitAsync(ShortTimeout);
        (await recvOnBob).Should().Equal(a2b);

        byte[] b2a = Enumerable.Range(100, 30).Select(i => (byte)i).ToArray();
        using var aliceTimeout = new CancellationTokenSource(ShortTimeout);
        var recvOnAlice = aliceChannel.ReceiveAsync(aliceTimeout.Token);
        await bobChannel.SendAsync(b2a).WaitAsync(ShortTimeout);
        (await recvOnAlice).Should().Equal(b2a);

        await allSeen.WaitAsync(ShortTimeout);
        lock (frames)
            frames.Should().BeEquivalentTo(expected);
    }

    // Mixed addressing filters on the address-extension byte as Extended does: a frame on the
    // channel's identifier carrying another byte belongs to another node and is not received.
    [Fact]
    public async Task MixedAddressing_Ignores_A_Frame_With_Another_Address_Extension()
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);
        using var channel = IsoTpFactory.Open(busA,
            IsoTpEndpoint.Mixed(txCanId: 0x320, rxCanId: 0x321, addressExtension: 0x5A), FastOptions());

        // Both Single Frames from one transmitter, the foreign one first: by the time the second
        // is received, the first has been taken through the channel too.
        busB.Transmit(CanFrame.Classic(0x321, new byte[] { 0x5B, 0x02, 0xEE, 0xEE, 0xCC, 0xCC, 0xCC, 0xCC }));
        busB.Transmit(CanFrame.Classic(0x321, new byte[] { 0x5A, 0x02, 0x0A, 0x0B, 0xCC, 0xCC, 0xCC, 0xCC }));

        using var timeout = new CancellationTokenSource(ShortTimeout);
        (await channel.ReceiveAsync(timeout.Token)).Should().Equal(0x0A, 0x0B);
        await channel.SettleAsync().WaitAsync(ShortTimeout);
        channel.TryReceiveWithArrival(out _).Should().BeFalse("the frame with another address extension is not this channel's");
    }

    // NormalFixed addressing: 29-bit identifiers and no address-extension byte -- the PCI is the
    // first payload byte of every frame, the Flow Control included. A Single Frame one way, then a
    // multi-frame transfer each way.
    [Fact]
    public async Task NormalFixedAddressing_Transfers_Single_And_MultiFrame_Both_Ways()
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);
        using var snifferBus = OpenClassic(session, 2);

        const uint aliceTx = 0x18DA10F1u; // N_TA 0x10, N_SA 0xF1
        const uint bobTx = 0x18DAF110u;
        var alice = IsoTpEndpoint.NormalFixed(txCanId: aliceTx, rxCanId: bobTx);
        var bob = IsoTpEndpoint.NormalFixed(txCanId: bobTx, rxCanId: aliceTx);

        // 30 bytes: FF carries 6, CFs 7 each -- FF, four CFs and one FC per way. Byte 1 is the
        // first data byte here (the FF's length low byte, 30; the FC's BS; a CF's payload), so
        // the PCI byte in byte 0 is what is checked, with byte 1 where it is fixed.
        var expected = new List<SniffedFrame> { new(aliceTx, true, 0x03, 0x11) };
        foreach (var (sender, receiver, first) in new[] { (aliceTx, bobTx, 0), (bobTx, aliceTx, 100) })
        {
            expected.Add(new(sender, true, 0x10, 30));
            expected.Add(new(receiver, true, 0x30, 0x00));
            for (int sn = 1; sn <= 4; sn++)
                expected.Add(new(sender, true, (byte)(0x20 | sn), (byte)(first + 6 + (sn - 1) * 7)));
        }
        var (frames, allSeen) = Sniff(snifferBus, expected.Count);

        using var aliceChannel = IsoTpFactory.Open(busA, alice, FastOptions());
        using var bobChannel = IsoTpFactory.Open(busB, bob, FastOptions());

        byte[] single = { 0x11, 0x22, 0x33 };
        using var singleTimeout = new CancellationTokenSource(ShortTimeout);
        var singleOnBob = bobChannel.ReceiveAsync(singleTimeout.Token);
        await aliceChannel.SendAsync(single).WaitAsync(ShortTimeout);
        (await singleOnBob).Should().Equal(single);

        byte[] a2b = Enumerable.Range(0, 30).Select(i => (byte)i).ToArray();
        using var bobTimeout = new CancellationTokenSource(ShortTimeout);
        var recvOnBob = bobChannel.ReceiveAsync(bobTimeout.Token);
        await aliceChannel.SendAsync(a2b).WaitAsync(ShortTimeout);
        (await recvOnBob).Should().Equal(a2b);

        byte[] b2a = Enumerable.Range(100, 30).Select(i => (byte)i).ToArray();
        using var aliceTimeout = new CancellationTokenSource(ShortTimeout);
        var recvOnAlice = aliceChannel.ReceiveAsync(aliceTimeout.Token);
        await bobChannel.SendAsync(b2a).WaitAsync(ShortTimeout);
        (await recvOnAlice).Should().Equal(b2a);

        await allSeen.WaitAsync(ShortTimeout);
        lock (frames)
            frames.Should().BeEquivalentTo(expected);
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
            "SendConfirmedAsync must be parked so Dispose races an in-flight SendAsync");

        // Dispose while SendAsync still holds _sendGate (awaiting confirmation / idle drain).
        Action dispose = () => sender.Dispose();
        dispose.Should().NotThrow("Dispose must leave the send gate to its holder (#339: it is never disposed)");

        holdConfirm.Release();

        Func<Task> send = () => sendTask.WaitAsync(ShortTimeout);
        // Clean shutdown: FailTx's ObjectDisposedException, not a secondary ODE from Release.
        (await send.Should().ThrowAsync<ObjectDisposedException>())
            .Which.ObjectName.Should().Be("IsoTpChannel");
    }

    // --------------------------------------------------------------------------------
    // #339 (deep review I-1/I-6) — a SendAsync queued on the send gate behind an in-flight send
    // must fail with ObjectDisposedException as soon as the channel is disposed, even with
    // CancellationToken.None, and even while the actor cannot run the cleanup Dispose posts to
    // it. Before the fix, Dispose waited 2 s for the gate and then disposed it, and
    // SemaphoreSlim.Dispose does not complete pending WaitAsync waiters: the queued send hung
    // for ever (here: send2.WaitAsync(ShortTimeout) threw TimeoutException).
    //
    // The actor is an injected one held busy, so the in-flight send keeps the gate for the whole
    // test and the queued send can only be released by the disposal itself. Both variants of the
    // gate wait are covered: a caller without a token, and one whose (never cancelled) token is
    // linked to the channel's lifetime.
    // --------------------------------------------------------------------------------
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_Send_Queued_On_The_Gate_Fails_On_Dispose_Although_The_Actor_Is_Busy(
        bool queuedSendHasToken)
    {
        var session = NewSession();
        using var bus = OpenClassic(session, 0);
        using var service = new CanBusService(bus);
        using var actor = new ProtocolActor();
        var channel = new IsoTpChannel(service, IsoTpEndpoint.Normal(0x123, 0x321), FastOptions(),
            ownsService: false, actor);
        using var release = new ManualResetEventSlim();
        using var callerCts = new CancellationTokenSource();
        var busy = actor.PostAsync(() => release.Wait(TimeSpan.FromSeconds(30)));

        // send1 takes the gate synchronously and parks on the actor; send2 parks on the gate --
        // SendAsync returns only once it is queued there, so no sleep is needed.
        var send1 = channel.SendAsync(new byte[] { 0x01 });
        var send2 = channel.SendAsync(new byte[] { 0x02 },
            queuedSendHasToken ? callerCts.Token : CancellationToken.None);
        send1.IsCompleted.Should().BeFalse();
        send2.IsCompleted.Should().BeFalse();

        var disposal = Task.Run(channel.Dispose);
        try
        {
            // Released by the disposal, not by the actor: the actor is still held here.
            var queued = await Assert.ThrowsAsync<ObjectDisposedException>(() => send2.WaitAsync(ShortTimeout));
            queued.ObjectName.Should().Be(nameof(IsoTpChannel));
        }
        finally
        {
            release.Set();
            await busy;
            await disposal.WaitAsync(ShortTimeout);
        }

        // The in-flight send ends with the channel once the actor runs the cleanup.
        var inFlight = await Assert.ThrowsAsync<ObjectDisposedException>(() => send1.WaitAsync(ShortTimeout));
        inFlight.ObjectName.Should().Be(nameof(IsoTpChannel));
    }

    // Review on #364 (#339): Dispose releases the sends queued on the gate by cancelling the
    // channel's lifetime from its own thread. On .NET Framework, SemaphoreSlim completes a
    // cancelled waiter inline from that Cancel(), so without a hop the released send -- and every
    // continuation its caller queued after it -- ran synchronously inside Dispose; called from an
    // actor callback, on the actor loop. SendAsync now hops to the thread pool before it fails.
    //
    // Not a timing test: the continuation below is synchronous, so it runs on whichever thread
    // completes the send, and it records whether that is the disposing thread while Dispose is
    // still running on it. .NET 10's SemaphoreSlim already forces the waiter asynchronous, so the
    // net10.0 leg passes either way; the net48 leg is the one that pins the hop.
    [Fact]
    public async Task A_Send_Released_By_Dispose_Does_Not_Complete_On_The_Disposing_Thread()
    {
        var session = NewSession();
        using var bus = OpenClassic(session, 0);
        using var service = new CanBusService(bus);
        using var actor = new ProtocolActor();
        var channel = new IsoTpChannel(service, IsoTpEndpoint.Normal(0x123, 0x321), FastOptions(),
            ownsService: false, actor);
        using var release = new ManualResetEventSlim();
        var busy = actor.PostAsync(() => release.Wait(TimeSpan.FromSeconds(30)));

        var send1 = channel.SendAsync(new byte[] { 0x01 });
        var send2 = channel.SendAsync(new byte[] { 0x02 });
        send2.IsCompleted.Should().BeFalse();

        var disposingThread = -1;
        var disposing = 0;
        var completedInsideDispose = -1;
        var observed = send2.ContinueWith(
            _ => Volatile.Write(ref completedInsideDispose,
                Volatile.Read(ref disposing) == 1
                && Environment.CurrentManagedThreadId == Volatile.Read(ref disposingThread) ? 1 : 0),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        var disposal = Task.Run(() =>
        {
            Volatile.Write(ref disposingThread, Environment.CurrentManagedThreadId);
            Volatile.Write(ref disposing, 1);
            try { channel.Dispose(); }
            finally { Volatile.Write(ref disposing, 0); }
        });
        try
        {
            var queued = await Assert.ThrowsAsync<ObjectDisposedException>(() => send2.WaitAsync(ShortTimeout));
            queued.ObjectName.Should().Be(nameof(IsoTpChannel));
            await observed.WaitAsync(ShortTimeout);
            completedInsideDispose.Should().Be(0,
                "a send released by Dispose must not continue on the disposing thread inside Dispose");
        }
        finally
        {
            release.Set();
            await busy;
            await disposal.WaitAsync(ShortTimeout);
        }

        await Assert.ThrowsAsync<ObjectDisposedException>(() => send1.WaitAsync(ShortTimeout));
    }

    // Review on #339: the gate wait is linked to the channel's lifetime now, and the linked
    // token is not the caller's. A send queued on the gate that its caller cancels must still
    // end with the caller's OperationCanceledException -- carrying the caller's token, as it did
    // when the wait took that token directly, and as CancelInFlightSend reports a cancellation
    // after the gate -- and must not be taken for a disposal.
    [Fact]
    public Task A_Send_Queued_On_The_Gate_That_Its_Caller_Cancels_Reports_The_Callers_Token()
        => CallerCancelsASendQueuedOnTheGateAsync(disposeAsLateAsPossible: false);

    // #400: the test above released the actor right after starting the disposal, so the send
    // holding the gate could begin -- and complete -- before Dispose had marked the channel
    // disposed, and then no ObjectDisposedException was due. This runs it with the disposal
    // started as late as the test lets it (see LateDisposal), which forces that interleaving
    // whenever the actor is released before the disposal is seen to have begun.
    [Fact]
    public Task A_Send_Queued_On_The_Gate_That_Its_Caller_Cancels_Reports_The_Callers_Token_However_Late_The_Disposal_Runs()
        => CallerCancelsASendQueuedOnTheGateAsync(disposeAsLateAsPossible: true);

    [Fact]
    public async Task A_Reader_Cancellation_Callback_Failure_Is_Reported_Without_Skipping_Channel_Cleanup()
    {
        using var bus = OpenClassic(NewSession(), 0);
        using var service = new CanBusService(bus);
        using var channel = new IsoTpChannel(service, IsoTpEndpoint.Normal(0x123, 0x321), FastOptions(), ownsService: false);
        var cancellation = (CancellationTokenSource)typeof(IsoTpChannel)
            .GetField("_readerCts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(channel)!;
        var failure = new InvalidOperationException("subscription cancellation callback failed");
        using var registration = cancellation.Token.Register(() => throw failure);
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.BackgroundExceptionOccurred += (_, error) => reported.TrySetResult(error);

        channel.Dispose();

        (await reported.Task.WaitAsync(ShortTimeout)).Should().BeOfType<AggregateException>()
            .Which.InnerExceptions.Should().ContainSingle().Which.Should().BeSameAs(failure);
        service.SubscriptionCount.Should().Be(0, "the callback failure must not bypass subscription disposal");
    }

    private static async Task CallerCancelsASendQueuedOnTheGateAsync(bool disposeAsLateAsPossible)
    {
        var session = NewSession();
        using var bus = OpenClassic(session, 0);
        using var service = new CanBusService(bus);
        using var actor = new ProtocolActor();
        var channel = new IsoTpChannel(service, IsoTpEndpoint.Normal(0x123, 0x321), FastOptions(),
            ownsService: false, actor);
        using var release = new ManualResetEventSlim();
        using var callerCts = new CancellationTokenSource();
        var busy = actor.PostAsync(() => release.Wait(TimeSpan.FromSeconds(30)));

        var send1 = channel.SendAsync(new byte[] { 0x01 });
        var send2 = channel.SendAsync(new byte[] { 0x02 }, callerCts.Token);
        send2.IsCompleted.Should().BeFalse();
        // Ends once Dispose is under way, and so after its first line, which marks the channel
        // disposed: from then on the actor fails send1 instead of beginning it.
        var receive = channel.ReceiveAsync();
        var late = new LateDisposal(disposeAsLateAsPossible, send1);

        try
        {
            callerCts.Cancel();
            var queued = await Assert.ThrowsAsync<OperationCanceledException>(() => send2.WaitAsync(ShortTimeout));
            queued.CancellationToken.Should().Be(callerCts.Token);
        }
        finally
        {
            var disposal = Task.Run(async () =>
            {
                await late.MayStartAsync().ConfigureAwait(false);
                channel.Dispose();
            });
            // Released only once the disposal is seen to have begun (#400): released earlier,
            // send1 can begin and complete before Dispose reaches it, and then it owes no
            // ObjectDisposedException.
            await late.WaitForAsync(receive);
            late.ActorReleased();
            release.Set();
            await busy;
            await disposal.WaitAsync(ShortTimeout);
        }

        await Assert.ThrowsAsync<ObjectDisposedException>(() => send1.WaitAsync(ShortTimeout));
    }

    /// <summary>Starts a disposal as late as the test lets it: not before the test waits on it,
    /// and, if by then the actor has been released, not before the send the actor was holding
    /// back has ended. A scheduler that runs the disposing thread last, made deterministic.</summary>
    private sealed class LateDisposal
    {
        private readonly bool _enabled;
        private readonly Task _heldSend;
        private readonly TaskCompletionSource<bool> _testWaits = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _actorReleased;

        public LateDisposal(bool enabled, Task heldSend)
        {
            _enabled = enabled;
            _heldSend = heldSend;
        }

        public void ActorReleased() => Volatile.Write(ref _actorReleased, 1);

        /// <summary>Waits, with the hang guard, on <paramref name="signal"/> -- a sign of the
        /// disposal -- letting the disposal start first.</summary>
        public async Task WaitForAsync(Task signal)
        {
            _testWaits.TrySetResult(true);
            await Assert.ThrowsAsync<InvalidOperationException>(() => signal.WaitAsync(ShortTimeout));
        }

        public async Task MayStartAsync()
        {
            if (!_enabled) return;
            await _testWaits.Task.ConfigureAwait(false);
            if (Volatile.Read(ref _actorReleased) == 0) return;
            // Its end, not its outcome, which is the test's to assert.
            await _heldSend.ContinueWith(static _ => { }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).ConfigureAwait(false);
        }
    }

    // --------------------------------------------------------------------------------
    // Bugbot 3596212788 (HIGH) — cancelling SendAsync must not release _sendGate while a
    // SendConfirmedAsync started by SendFrameOnBus is still outstanding. Otherwise a subsequent
    // SendAsync can put a new PDU on the wire while the aborted PDU's frame is still TX'ing.
    //
    // Arrangement: wrap the bus service so the first SendConfirmedAsync blocks until we release it;
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
            "the first SendConfirmedAsync must be parked inside our delaying wrapper");

        cts.Cancel();

        // cancelledSend awaits WaitForBusTxIdleAsync after the OCE, so it must NOT complete while
        // we still hold the first SendConfirmedAsync — that is the gate-hold under test.
        var cancelFinished = cancelledSend.WaitAsync(TimeSpan.FromMilliseconds(200));
        Func<Task> stillHeld = () => cancelFinished;
        await stillHeld.Should().ThrowAsync<TimeoutException>(
            "cancelled SendAsync must keep the send gate until its in-flight SendConfirmedAsync finishes");

        // Second send starts while the aborted PDU's SendConfirmedAsync is still held. Under the bug
        // the gate is already free and this SF (DL=3) hits the bus immediately; under the fix it
        // must wait until we release the first confirmation.
        var secondSend = sender.SendAsync(new byte[] { 0x11, 0x22, 0x33 });

        // #171: "no SF may hit the peer" was checked after a Task.Delay(100). SendAsync acquires
        // _sendGate before ever posting to the actor (IsoTpChannel.cs), so while the gate is
        // held secondSend cannot even reach the actor's mailbox at all -- under the fix nothing
        // is scheduled, ever, and the round-trip below is a complete, deterministic proof of
        // that. A regression that skipped the gate would instead post BeginSendOnLoop, whose own
        // transmit is dispatched onto the thread pool (SendFrameOnBus's Task.Run) rather than
        // run inline on the actor, so the round-trip alone only proves the actor's *decision*,
        // not that dispatch's completion; the residual wait below covers only that one
        // thread-pool hop, unchanged from the original margin, not lengthened for this.
        var actorField = sender.GetType().GetField("_actor",
            BindingFlags.Instance | BindingFlags.NonPublic);
        actorField.Should().NotBeNull("IsoTpChannel must keep an _actor field for this race test");
        var actor = (IProtocolActor)actorField!.GetValue(sender)!;
        await actor.PostAsync(() => { }).WaitAsync(ShortTimeout);
        await Task.Delay(100);
        framesToPeer.Should().Be(0,
            "no SF may hit the peer while the aborted send's SendConfirmedAsync is still parked");

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

        // #171: "ensure peer FC is processed into DeferredFcs" was a Task.Delay(30). fcArrived
        // is a frame on the wire (the sender's own bus, busA, where epAB.RxCanId = 0x261); the
        // SettleAsync after it is the actor round-trip that proves the sender has taken it off
        // the mailbox before the held CF confirm is released.
        using var fcArrived = new SemaphoreSlim(0);
        busA.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != 0x261) return;
            var payload = e.CanFrame.Data.ToArray();
            if (payload.Length > 0 && (payload[0] >> 4) == 0x3) fcArrived.Release();
        };

        // 20 bytes classic: FF(6) + CF1(7) + CF2(7). BS=1 => wait for FC after FF and after CF1.
        byte[] pdu = Enumerable.Range(0, 20).Select(i => (byte)(i + 1)).ToArray();
        var sendTask = sender.SendAsync(pdu);

        // The FC answering the FF comes first -- CF1 is not sent without it -- and is not one of
        // the deferred FCs below; its permit is taken here so each wait in the loop is for the FC
        // the peer sent while that CF's confirmation was parked (Codex on #185).
        (await fcArrived.WaitAsync(ShortTimeout)).Should().BeTrue("the sender's bus must see the FC answering the FF");

        // Two block-ending CFs (CF1 then CF2): for each, wait until confirm is parked (FC already
        // sent by the peer handler above), then release so deferred FC is applied.
        for (int i = 0; i < 2; i++)
        {
            cfConfirmParked.Wait(TimeSpan.FromSeconds(3)).Should().BeTrue(
                $"CF confirm #{i + 1} must park after transmit so peer FC can defer");
            cfConfirmParked.Reset();
            (await fcArrived.WaitAsync(ShortTimeout)).Should().BeTrue(
                $"the sender's bus must see the FC deferred for block #{i + 1}");
            await sender.SettleAsync().WaitAsync(ShortTimeout);
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

        // #171: "each Wait FC is processed within 20 ms" was a Task.Delay(20). fcArrived is a
        // frame on the wire (the sender's own bus, busA, where epAB.RxCanId = 0x271); the
        // SettleAsync after it is the actor round-trip that proves it has been queued as its own
        // DeferredFc before the next one goes out, which is exactly what this test guards.
        using var fcArrived = new SemaphoreSlim(0);
        busA.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != 0x271) return;
            var payload = e.CanFrame.Data.ToArray();
            if (payload.Length > 0 && (payload[0] >> 4) == 0x3) fcArrived.Release();
        };

        byte[] pdu = Enumerable.Range(0, 30).Select(i => (byte)i).ToArray();
        var sendTask = sender.SendAsync(pdu);

        confirmStarted.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue(
            "FF SendConfirmedAsync must be parked so Wait FCs arrive during SingleOrFirstInFlight");
        await ffSeen.Task.WaitAsync(ShortTimeout);

        // Pump WftMax+1 Wait FCs while FF confirm is still held — all must be queued and
        // counted when confirm completes (under the bug only the last Wait survived).
        for (int i = 0; i < wftMax + 1; i++)
        {
            var fc = IsoTpFrameCodec.BuildFlowControl(epBA, FlowStatus.Wait,
                blockSize: 0, stMinRaw: 0, isCanFd: false, padding: true);
            busB.Transmit(CanFrame.Classic(0x271, fc));
            (await fcArrived.WaitAsync(ShortTimeout)).Should().BeTrue(
                $"the sender's bus must see Wait FC #{i + 1}");
            await sender.SettleAsync().WaitAsync(ShortTimeout);
        }

        holdConfirm.Release();

        Func<Task> act = () => sendTask.WaitAsync(ShortTimeout);
        var ex = (await act.Should().ThrowAsync<IsoTpWaitFrameLimitExceededException>()).Which;
        ex.Limit.Should().Be(wftMax);
        ex.WaitFramesReceived.Should().BeGreaterThan(wftMax);
    }

    // --------------------------------------------------------------------------------
    // Test gap of the 2026-10-21 review (#353): an FC(OVFLW) that arrives while the FF's
    // TX-confirm is still outstanding is deferred like a Wait, and applied when the confirm
    // completes. The Wait path above is pinned; the Overflow entry of the same list was not.
    // The send must fail with the overflow, not time out on N_Bs.
    // --------------------------------------------------------------------------------
    [Fact]
    public async Task MultiFrame_Send_Applies_An_Overflow_FlowControl_Deferred_During_Ff_Confirm()
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);

        var epAB = IsoTpEndpoint.Normal(0x280, 0x281);
        var epBA = IsoTpEndpoint.Normal(0x281, 0x280);

        using var inner = new CanBusService(busA);
        using var holdConfirm = new SemaphoreSlim(0, 1);
        using var confirmStarted = new ManualResetEventSlim(false);
        var delaying = new DelayingConfirmService(inner, holdConfirm, confirmStarted,
            holdAfterTransmit: true);
        using var sender = IsoTpFactory.Open(delaying, epAB,
            FastOptions(nBs: TimeSpan.FromSeconds(2)), leaveOpen: true);

        var ffSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        busB.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != 0x280) return;
            var payload = e.CanFrame.Data.ToArray();
            if (payload.Length > 0 && (payload[0] >> 4) == 0x1)
                ffSeen.TrySetResult(true);
        };
        using var fcArrived = new SemaphoreSlim(0);
        busA.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != 0x281) return;
            var payload = e.CanFrame.Data.ToArray();
            if (payload.Length > 0 && (payload[0] >> 4) == 0x3) fcArrived.Release();
        };

        byte[] pdu = Enumerable.Range(0, 30).Select(i => (byte)i).ToArray();
        var sendTask = sender.SendAsync(pdu);

        confirmStarted.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue(
            "FF SendConfirmedAsync must be parked so the Overflow arrives during SingleOrFirstInFlight");
        await ffSeen.Task.WaitAsync(ShortTimeout);

        var fc = IsoTpFrameCodec.BuildFlowControl(epBA, FlowStatus.Overflow,
            blockSize: 0, stMinRaw: 0, isCanFd: false, padding: true);
        busB.Transmit(CanFrame.Classic(0x281, fc));
        (await fcArrived.WaitAsync(ShortTimeout)).Should().BeTrue("the sender's bus must see the Overflow FC");
        // The actor round-trip after the frame: it is queued as a deferred FC before the confirm
        // is released, which is what makes this the deferred path and not the direct one.
        await sender.SettleAsync().WaitAsync(ShortTimeout);

        holdConfirm.Release();

        Func<Task> act = () => sendTask.WaitAsync(ShortTimeout);
        await act.Should().ThrowAsync<IsoTpOverflowException>();
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

        // #171: "an illegal FC would have been sent within 100 ms" was a Task.Delay(100).
        // hugeFfSeen is a frame on the wire (the receiver's own bus, busA); SettleAsync after it
        // is the actor round-trip that proves the receiver has taken it off the mailbox and made
        // its (silent) decision before anyFc is read.
        var hugeFfSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        busA.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID == 0x7E8) hugeFfSeen.TrySetResult(true);
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

        await hugeFfSeen.Task.WaitAsync(ShortTimeout);
        await receiver.SettleAsync().WaitAsync(ShortTimeout);
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
        // #171: the trailing Task.Delay(100) ("a frame the timer released would be on the wire
        // by now") added a wall-clock guess on top of an already-virtual clock. AdvanceAsync
        // returns once the due timer's callback has run, and SettleAsync's round-trip proves the
        // actor-side decision -- SendNextConsecutiveFrame's stale-tx check (IsoTpChannel.cs) --
        // is deterministic under the clock: a released timer for a superseded transfer decides
        // not to build a frame at all, and nothing is ever handed to the wire. What neither
        // proves is that a frame the timer *did* release has finished transmitting: SendFrameOnBus
        // hands the actual bus write to the thread pool (Task.Run), a hop with no actor-side
        // observable, so the residual wait below covers only that one hop -- unchanged from the
        // original margin, not lengthened for this.
        await clock.AdvanceAsync(stMin);
        await clock.SettleAsync();
        await Task.Delay(100);
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
        // #171: "all three FCs have been counted within 50 ms of receive completing" was a
        // Task.Delay(50). thirdFcSeen is set from inside the handler itself once the count this
        // assertion needs has actually been observed on the sniffer's own (independent)
        // subscription, which a fixed window could only ever approximate.
        var thirdFcSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        snifferBus.FrameObserved += (_, view) =>
        {
            if (view.CanFrame.ID != 0x7E8) return;
            var data = view.CanFrame.Data.Span;
            if (data.Length > 0 && (data[0] & 0xF0) == 0x30)
                if (Interlocked.Increment(ref fcCount) == 3) thirdFcSeen.TrySetResult(true);
        };

        // 38 bytes => FF (6) + 5 CFs (7,7,7,7,4): with BS=2 the receiver sends its initial
        // FC plus one FC after CF #2 and one after CF #4 => 3 FCs in total.
        byte[] pdu = Enumerable.Range(0, 38).Select(i => (byte)(i & 0xFF)).ToArray();
        var recvTask = receiver.ReceiveAsync(new CancellationTokenSource(ShortTimeout).Token);
        await sender.SendAsync(pdu);
        var got = await recvTask;
        got.Should().Equal(pdu);

        await thirdFcSeen.Task.WaitAsync(ShortTimeout);
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

    // #56 -- the one endpoint shape where the host echo flag *is* the only thing that tells the
    // channel's frame from the peer's: transmit and receive on the same identifier. There the
    // echo is withheld, or the channel would receive every PDU it sends.
    [Fact]
    public async Task A_Channel_On_One_Identifier_Does_Not_Receive_Its_Own_Frames()
    {
        using var bus = ControllableBus.EchoCapable(NewSession());
        using var service = new CanBusService(bus);
        using var channel = IsoTpFactory.Open(
            service, IsoTpEndpoint.Normal(txCanId: 0x7DF, rxCanId: 0x7DF));

        await channel.SendAsync(new byte[] { 0x3E, 0x80 });

        // The echo, if it were delivered, would be in the inbox by the time the send confirmed
        // and the actor settled; a real peer frame on the same identifier still arrives.
        using var cts = new CancellationTokenSource(ShortTimeout);
        channel.DiscardPendingPdus().Should().Be(0, "the channel's own echo is not a received PDU");
        // A peer's frame on the same identifier: observed, not echoed.
        bus.RaiseObserved(CanFrame.Classic(0x7DF, new byte[] { 0x02, 0x7E, 0x80 }), isEcho: false);
        (await channel.ReceiveAsync(cts.Token)).Should().Equal(0x7E, 0x80);
    }

    // Codex on #147: on one identifier, Extended addressing still tells the directions apart
    // by the address-extension byte, so a reciprocal pair sharing an echo-capable service must
    // keep exchanging -- the echo is withheld only when the extension byte coincides too.
    [Fact]
    public async Task Reciprocal_Extended_Channels_On_One_Identifier_Still_Exchange()
    {
        using var bus = ControllableBus.EchoCapable(NewSession());
        using var service = new CanBusService(bus);
        using var tester = IsoTpFactory.Open(
            service, IsoTpEndpoint.Extended(txCanId: 0x7DF, rxCanId: 0x7DF, sourceAddress: 0xF1, targetAddress: 0x10));
        using var ecu = IsoTpFactory.Open(
            service, IsoTpEndpoint.Extended(txCanId: 0x7DF, rxCanId: 0x7DF, sourceAddress: 0x10, targetAddress: 0xF1));

        using var cts = new CancellationTokenSource(ShortTimeout);
        var ecuReceive = ecu.ReceiveAsync(cts.Token);
        await tester.SendAsync(new byte[] { 0x22, 0xF1, 0x90 });
        (await ecuReceive).Should().Equal(0x22, 0xF1, 0x90);

        var testerReceive = tester.ReceiveAsync(cts.Token);
        await ecu.SendAsync(new byte[] { 0x62, 0xF1, 0x90, 0xAA });
        (await testerReceive).Should().Equal(0x62, 0xF1, 0x90, 0xAA);
    }

    // #56 -- BS and STmin are the first FC.CTS's for the whole transfer; a later FC.CTS's
    // values are ignored (ISO 15765-2). The peer grants two frames per block, then says one:
    // the sender must still send two before waiting, or it waits for a Flow Control the peer
    // never sends and N_Bs faults the send.
    [Fact]
    public async Task Flow_Parameters_Come_From_The_First_Flow_Control_Only()
    {
        var session = NewSession();
        using var busSender = OpenClassic(session, 0);
        using var busPeer = OpenClassic(session, 1);

        var epSender = IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8);
        using var sender = IsoTpFactory.Open(busSender, epSender, FastOptions(nBs: TimeSpan.FromMilliseconds(300)));

        int cfs = 0;
        var epPeer = IsoTpEndpoint.Normal(txCanId: 0x7E8, rxCanId: 0x7E0);
        busPeer.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != 0x7E0) return;
            var data = e.CanFrame.Data.Span;
            if (data.Length == 0) return;
            switch (data[0] >> 4)
            {
                case 0x1: // First Frame: grant two per block, STmin 0
                    busPeer.Transmit(CanFrame.Classic(0x7E8, IsoTpFrameCodec.BuildFlowControl(epPeer, FlowStatus.ClearToSend, blockSize: 2, stMinRaw: 0, isCanFd: false, padding: true, paddingByte: 0xCC)));
                    break;
                case 0x2: // Consecutive Frame
                    int n = Interlocked.Increment(ref cfs);
                    // After the first block, a second FC.CTS that says one per block -- to be ignored.
                    if (n == 2)
                        busPeer.Transmit(CanFrame.Classic(0x7E8, IsoTpFrameCodec.BuildFlowControl(epPeer, FlowStatus.ClearToSend, blockSize: 1, stMinRaw: 0, isCanFd: false, padding: true, paddingByte: 0xCC)));
                    break;
            }
        };

        // 34 bytes: FF (6) + 4 CFs. Blocks of two: CF1, CF2, FC, CF3, CF4 -- done.
        byte[] pdu = Enumerable.Range(0, 34).Select(i => (byte)i).ToArray();
        await sender.SendAsync(pdu).WaitAsync(ShortTimeout);
        cfs.Should().Be(4);
    }

    // #56 -- DiscardPendingPdus called from a BackgroundExceptionOccurred handler, which runs on
    // the actor: the post-and-wait would wait for the loop it is on. It runs inline there.
    [Fact]
    public async Task Discard_From_A_Background_Exception_Handler_Does_Not_Deadlock()
    {
        var ep = IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8);
        using var service = new StarvedReaderBusService();
        using var actor = new ProtocolActor();
        using var channel = new IsoTpChannel(service, ep, FastOptions(), ownsService: false, actor);

        var discardedFromHandler = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        // A discard that threw would leave the source unset and the wait below would time out:
        // still a failure, only less specific.
        channel.BackgroundExceptionOccurred += (_, _) =>
            discardedFromHandler.TrySetResult(channel.DiscardPendingPdus());

        // A reassembly, then a Consecutive Frame out of sequence: AbortRx raises the event on
        // the actor.
        int ffData = IsoTpFrameCodec.FirstFrameMaxDataLength(isCanFd: false, usesAddressExtension: false, useLongLength: false);
        byte[] pdu = Enumerable.Range(0x40, 20).Select(i => (byte)i).ToArray();
        service.Deliver(new CanFrameView(CanFrameType.Can20, 0x7E8, IsoTpFrameCodec.BuildFirstFrame(IsoTpEndpoint.Normal(0x7E8, 0x7E0), pdu.Length, pdu.AsSpan(0, ffData), isCanFd: false), FrameFlags.None));
        service.Deliver(new CanFrameView(CanFrameType.Can20, 0x7E8, new byte[] { 0x25, 1, 2, 3, 4, 5, 6, 7 }, FrameFlags.None));
        channel.GetReceptionsInProgress(); // pumps both frames to the actor

        var discarded = await discardedFromHandler.Task.WaitAsync(ShortTimeout);
        discarded.Should().Be(1, "the abort's error item was in the inbox when the handler discarded");
        await actor.PostAsync(() => { }).WaitAsync(ShortTimeout);
        channel.TryReceiveWithArrival(out _).Should().BeFalse();
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

    // Codex on #147 -- the handoff instant is taken inside the service's send lock, immediately
    // before the driver call. A reading the channel takes before calling the service precedes
    // another sender's lock wait: with channel A parked inside its driver call, channel B's
    // frame waits on the lock, and B's handoff instant must be after A's release, not before.
    [Fact]
    public async Task Handoff_Instant_Is_Taken_Inside_The_Service_Lock_After_Another_Senders_Call()
    {
        using var bus = ControllableBus.EchoCapable(NewSession());
        using var inner = new CanBusService(bus);
        using var service = new FrameConsumptionCountingBusService(inner);
        using var a = IsoTpFactory.Open(service, IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8));
        using var b = IsoTpFactory.Open(service, IsoTpEndpoint.Normal(txCanId: 0x7E1, rxCanId: 0x7E9));
        using var bHandedToService = new ManualResetEventSlim();
        service.OnSendConfirmed = frame =>
        {
            if (frame.ID == 0x7E1) bHandedToService.Set();
        };

        using var aTransmitting = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        bus.OnTransmitting = frame =>
        {
            if (frame.ID != 0x7E0) return;
            aTransmitting.Set();
            // Held for as long as the test says, with no deadline of its own to race B's arrival
            // (Codex on #191); the finally below releases it whatever the outcome.
            release.Wait();
        };

        var sendA = a.SendWithTransmitStampAsync(new byte[] { 0x3E, 0x00 });
        IsoTpTransmitStamps stampsB;
        long releasedAt;
        try
        {
            aTransmitting.Wait(ShortTimeout).Should().BeTrue("A's frame must reach the driver call");

            // B is handed to the service, whose lock A holds across its driver call (#171: this
            // was a 50 ms wait for B to get there). Any reading B's channel takes before that
            // hand-off -- the pre-call stamp a channel that ignores the service's own would keep --
            // is therefore before the instant below; a reading inside the lock can only be after it.
            var sendB = b.SendWithTransmitStampAsync(new byte[] { 0x3E, 0x00 });
            bHandedToService.Wait(ShortTimeout).Should().BeTrue("B's frame must reach the service");
            releasedAt = Stopwatch.GetTimestamp();
            release.Set();

            await sendA.WaitAsync(ShortTimeout);
            stampsB = await sendB.WaitAsync(ShortTimeout);
        }
        finally
        {
            release.Set();
        }

        stampsB.LastFrameHandoffTimestamp.Should().BeGreaterThan(releasedAt,
            "B's handoff instant is taken inside the lock, which A held until the instant above");
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
        long enteredAt = 0;
        bus.OnTransmitting = _ =>
        {
            enteredAt = Stopwatch.GetTimestamp();
            transmitting.Set();
            release.Wait(ShortTimeout);
        };

        var send = channel.SendWithTransmitStampAsync(new byte[] { 0x22, 0xF1, 0x90 });
        transmitting.Wait(ShortTimeout).Should().BeTrue("the frame must reach the driver call");

        var releasedAt = Stopwatch.GetTimestamp();
        release.Set();

        var stamps = await send.WaitAsync(ShortTimeout);

        stamps.LastFrameTransmitTimestamp.Should().BeGreaterThan(releasedAt,
            "the stamp must be taken after the driver accepted the frame, and this test held the "
            + "driver call open until the instant above");
        // Codex on #147: the last frame's handoff is stamped before the driver call, so a
        // caller's stale-response cutoff cannot be later than the frame's wire instant.
        stamps.LastFrameHandoffTimestamp.Should().BeGreaterThan(0)
            .And.BeLessThanOrEqualTo(enteredAt,
                "the handoff instant precedes the driver call this test observed entering");
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
    /// Test double: the first <see cref="ICanBusService.SendConfirmedAsync"/> call throws the supplied
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

        public Task<TxConfirmation> SendConfirmedAsync(CanFrame frame, TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1) throw _failure;
            return _inner.SendConfirmedAsync(frame, timeout, cancellationToken);
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

        public async Task<TxConfirmation> SendConfirmedAsync(CanFrame frame, TimeSpan? timeout = null,
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
    /// parks <see cref="ICanBusService.SendConfirmedAsync"/> until <paramref name="release"/> is
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

        public async Task<TxConfirmation> SendConfirmedAsync(CanFrame frame, TimeSpan? timeout = null,
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

            var result = await _inner.SendConfirmedAsync(frame, timeout, cancellationToken).ConfigureAwait(false);

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
    /// Parks <see cref="ICanBusService.SendConfirmedAsync"/> only for Consecutive Frames, and only
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

        public async Task<TxConfirmation> SendConfirmedAsync(CanFrame frame, TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            // Copy PCI before await — ReadOnlySpan cannot live across await points.
            byte[] payload = frame.Data.ToArray();
            bool isCf = payload.Length > 0 && (payload[0] >> 4) == 0x2;

            var result = await _inner.SendConfirmedAsync(frame, timeout, cancellationToken).ConfigureAwait(false);

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

    // #202: a bus whose asynchronous TransmitAsync never completes must not wedge the channel.
    // The frame's SendConfirmedAsync is bounded by N_As on the approximated path too, so the send
    // faults with the N_As timeout, drains the bus-TX bookkeeping, and releases the send gate --
    // the next SendAsync goes through.
    [Fact]
    public async Task A_Stalled_Asynchronous_Transmit_Times_Out_And_Does_Not_Wedge_The_Channel()
    {
        var session = NewSession();
        using var busB = OpenClassic(session, 1);
        using var bus = ControllableBus.Plain(session);
        bus.StallTransmitAsync = true;
        using var wire = OpenClassic(session, 2);
        bus.OnTransmitting = frame => wire.Transmit(frame); // what the sender puts on the wire

        var epA = IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8);
        var epB = IsoTpEndpoint.Normal(txCanId: 0x7E8, rxCanId: 0x7E0);
        var options = new IsoTpChannelOptions
        {
            UseCanFd = false,
            UsePadding = true,
            NAs = TimeSpan.FromMilliseconds(50),
        };
        using var sender = IsoTpFactory.Open(bus, epA, options);
        using var receiver = IsoTpFactory.Open(busB, epB, FastOptions());

        var first = sender.SendAsync(new byte[] { 1, 2, 3 });
        var ex = await Assert.ThrowsAsync<IsoTpTimeoutException>(() => first.WaitAsync(ShortTimeout));
        ex.Timer.Should().Be(IsoTpTimer.NAs);

        bus.StallTransmitAsync = false;
        await sender.SendAsync(new byte[] { 4, 5, 6 }).WaitAsync(ShortTimeout);
        using var cts = new CancellationTokenSource(ShortTimeout);
        (await receiver.ReceiveAsync(cts.Token)).Should().Equal(4, 5, 6);
    }

    // #205: an injected actor the caller disposed first must not make Dispose throw, and must not
    // cost it the rest of its cleanup -- the demux subscription is released all the same.
    [Fact]
    public void Dispose_After_The_Injected_Actor_Was_Disposed_Does_Not_Throw_And_Releases_The_Subscription()
    {
        using var bus = OpenClassic(NewSession(), 0);
        using var service = new CanBusService(bus);
        using var actor = new ProtocolActor();
        using var channel = new IsoTpChannel(service,
            IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8), FastOptions(), ownsService: false, actor);
        service.SubscriptionCount.Should().Be(1);

        actor.Dispose();

        channel.Invoking(c => c.Dispose()).Should().NotThrow();
        service.SubscriptionCount.Should().Be(0, "the rest of Dispose must still run");
        channel.Invoking(c => c.Dispose()).Should().NotThrow("Dispose is idempotent");
    }

    // Bugbot on #216: with the injected actor already disposed, Dispose must still release the
    // SendAsync that is awaiting its outcome -- without reaching into actor state, whose loop the
    // owner's Dispose may still be joining.
    [Fact]
    public async Task Dispose_After_The_Injected_Actor_Was_Disposed_Fails_The_InFlight_Send()
    {
        var session = NewSession();
        using var bus = ControllableBus.Plain(session);
        bus.StallTransmitAsync = true;
        using var service = new CanBusService(bus);
        using var actor = new ProtocolActor();
        var options = new IsoTpChannelOptions { UseCanFd = false, UsePadding = true, NAs = TimeSpan.FromMinutes(5) };
        using var channel = new IsoTpChannel(service,
            IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8), options, ownsService: false, actor);

        var send = channel.SendAsync(new byte[] { 1, 2, 3 });
        await bus.FirstAsyncTransmit.WaitAsync(ShortTimeout); // the frame is out, its confirm stalled

        actor.Dispose();
        channel.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => send.WaitAsync(ShortTimeout));
        bus.ReleaseStalledTransmits();
    }

    // The other await Dispose's fallback must release: SendAsync canceled while a frame's confirm
    // is still outstanding waits for the bus to go idle, and with the actor gone nothing will
    // ever say so. The wait is armed on the actor, which is observed (by state, not by time)
    // before the actor is disposed.
    [Fact]
    public async Task Dispose_After_The_Injected_Actor_Was_Disposed_Releases_The_Bus_Idle_Wait()
    {
        var session = NewSession();
        using var bus = ControllableBus.Plain(session);
        bus.StallTransmitAsync = true;
        using var service = new CanBusService(bus);
        using var actor = new ProtocolActor();
        var options = new IsoTpChannelOptions { UseCanFd = false, UsePadding = true, NAs = TimeSpan.FromMinutes(5) };
        using var channel = new IsoTpChannel(service,
            IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8), options, ownsService: false, actor);
        using var cts = new CancellationTokenSource();

        var send = channel.SendAsync(new byte[] { 1, 2, 3 }, cts.Token);
        await bus.FirstAsyncTransmit.WaitAsync(ShortTimeout);
        cts.Cancel(); // completes the send's outcome; the bus-idle wait is what remains

        var waiterField = typeof(IsoTpChannel).GetField("_busTxIdleWaiter", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var deadline = Stopwatch.StartNew();
        while (!await actor.PostAsync(() => waiterField.GetValue(channel) is not null))
        {
            deadline.Elapsed.Should().BeLessThan(ShortTimeout, "the canceled send must arm the bus-idle wait");
            await Task.Yield();
        }

        actor.Dispose();
        channel.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send.WaitAsync(ShortTimeout));
        bus.ReleaseStalledTransmits();
    }

    // Bugbot on #216: a First Frame announced but not yet handled when the channel is disposed
    // must not stay reported as a reception in progress.
    [Fact]
    public async Task A_First_Frame_Announced_Before_Dispose_Is_Withdrawn_When_Handled_After_It()
    {
        var epRecv = IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8);
        using var service = new StarvedReaderBusService();
        using var actor = new ProtocolActor();
        using var channel = new IsoTpChannel(service, epRecv, FastOptions(), ownsService: false, actor);

        using var gate = new SemaphoreSlim(0);
        var held = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        actor.Post(() =>
        {
            held.TrySetResult(true);
            gate.Wait();
        });
        await held.Task.WaitAsync(ShortTimeout);

        int ffData = IsoTpFrameCodec.FirstFrameMaxDataLength(isCanFd: false, usesAddressExtension: false, useLongLength: false);
        byte[] pdu = Enumerable.Range(0x30, 20).Select(i => (byte)i).ToArray();
        var ff = IsoTpFrameCodec.BuildFirstFrame(IsoTpEndpoint.Normal(0x7E8, 0x7E0), pdu.Length, pdu.AsSpan(0, ffData), isCanFd: false);
        service.Deliver(new CanFrameView(CanFrameType.Can20, 0x7E8, ff, FrameFlags.None));
        channel.GetReceptionsInProgress().Should().HaveCount(1, "announced when taken from the subscription");

        channel.Dispose();
        gate.Release();
        await actor.PostAsync(() => { }).WaitAsync(ShortTimeout);

        channel.GetReceptionsInProgress().Should().BeEmpty();
    }

    // Codex on #216: a reassembly already under way when the channel is disposed is abandoned
    // with it, not reported as in progress for ever.
    [Fact]
    public async Task A_Reception_Under_Way_Is_Withdrawn_When_The_Channel_Is_Disposed()
    {
        using var service = new StarvedReaderBusService();
        using var actor = new ProtocolActor();
        using var channel = new IsoTpChannel(service,
            IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8), FastOptions(), ownsService: false, actor);

        int ffData = IsoTpFrameCodec.FirstFrameMaxDataLength(isCanFd: false, usesAddressExtension: false, useLongLength: false);
        byte[] pdu = Enumerable.Range(0x30, 20).Select(i => (byte)i).ToArray();
        var ff = IsoTpFrameCodec.BuildFirstFrame(IsoTpEndpoint.Normal(0x7E8, 0x7E0), pdu.Length, pdu.AsSpan(0, ffData), isCanFd: false);
        service.Deliver(new CanFrameView(CanFrameType.Can20, 0x7E8, ff, FrameFlags.None));
        channel.GetReceptionsInProgress(); // takes the First Frame to the actor
        await actor.PostAsync(() => { }).WaitAsync(ShortTimeout);
        channel.GetReceptionsInProgress().Should().HaveCount(1, "the actor accepted the First Frame");

        channel.Dispose();
        await actor.PostAsync(() => { }).WaitAsync(ShortTimeout);

        channel.GetReceptionsInProgress().Should().BeEmpty();
    }

    // Codex on #216: the same, when the injected actor was disposed first and can no longer run
    // the channel's cleanup -- the published records are withdrawn without it.
    [Fact]
    public async Task A_Reception_Under_Way_Is_Withdrawn_When_The_Injected_Actor_Was_Disposed_First()
    {
        using var service = new StarvedReaderBusService();
        using var actor = new ProtocolActor();
        using var channel = new IsoTpChannel(service,
            IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8), FastOptions(), ownsService: false, actor);

        int ffData = IsoTpFrameCodec.FirstFrameMaxDataLength(isCanFd: false, usesAddressExtension: false, useLongLength: false);
        byte[] pdu = Enumerable.Range(0x30, 20).Select(i => (byte)i).ToArray();
        var ff = IsoTpFrameCodec.BuildFirstFrame(IsoTpEndpoint.Normal(0x7E8, 0x7E0), pdu.Length, pdu.AsSpan(0, ffData), isCanFd: false);
        service.Deliver(new CanFrameView(CanFrameType.Can20, 0x7E8, ff, FrameFlags.None));
        channel.GetReceptionsInProgress();
        await actor.PostAsync(() => { }).WaitAsync(ShortTimeout);
        channel.GetReceptionsInProgress().Should().HaveCount(1);

        actor.Dispose();
        channel.Dispose();

        channel.GetReceptionsInProgress().Should().BeEmpty();
    }

    // Codex on #216: a First Frame that arrives once the channel is disposed must not be
    // announced by a later caller-side pump -- with the actor gone, nothing would withdraw it.
    [Fact]
    public void A_First_Frame_Arriving_After_Dispose_Is_Not_Announced()
    {
        using var service = new StarvedReaderBusService();
        using var actor = new ProtocolActor();
        using var channel = new IsoTpChannel(service,
            IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8), FastOptions(), ownsService: false, actor);
        actor.Dispose();
        channel.Dispose();

        int ffData = IsoTpFrameCodec.FirstFrameMaxDataLength(isCanFd: false, usesAddressExtension: false, useLongLength: false);
        byte[] pdu = Enumerable.Range(0x30, 20).Select(i => (byte)i).ToArray();
        var ff = IsoTpFrameCodec.BuildFirstFrame(IsoTpEndpoint.Normal(0x7E8, 0x7E0), pdu.Length, pdu.AsSpan(0, ffData), isCanFd: false);
        service.Deliver(new CanFrameView(CanFrameType.Can20, 0x7E8, ff, FrameFlags.None));

        channel.GetReceptionsInProgress().Should().BeEmpty();
    }

    // I-4 of the 2026-10-21 review (#353): an injected actor its owner disposed while the channel
    // lives (#205). A caller-side pump that ingested a First Frame then announced it and failed
    // to post it, and nothing withdrew the record until the channel's own Dispose: a deadline
    // check would have kept finding a transfer nobody will complete. The reader is starved, so
    // the pump below is the one that ingests the frame.
    [Fact]
    public void A_First_Frame_Pumped_After_The_Injected_Actor_Is_Gone_Is_Not_Left_Announced()
    {
        using var service = new StarvedReaderBusService();
        using var actor = new ProtocolActor();
        using var channel = new IsoTpChannel(service,
            IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8), FastOptions(), ownsService: false, actor);
        actor.Dispose();

        int ffData = IsoTpFrameCodec.FirstFrameMaxDataLength(isCanFd: false, usesAddressExtension: false, useLongLength: false);
        byte[] pdu = Enumerable.Range(0x30, 20).Select(i => (byte)i).ToArray();
        var ff = IsoTpFrameCodec.BuildFirstFrame(IsoTpEndpoint.Normal(0x7E8, 0x7E0), pdu.Length, pdu.AsSpan(0, ffData), isCanFd: false);
        service.Deliver(new CanFrameView(CanFrameType.Can20, 0x7E8, ff, FrameFlags.None));

        channel.GetReceptionsInProgress().Should().BeEmpty("nothing will handle a frame the actor cannot take");
        channel.GetReceptionsInProgress().Should().BeEmpty("and a later look finds nothing either");
    }

    // #206: a frame that was already on its way to the actor when the channel was disposed
    // completes no PDU and raises no DatagramReceived. The channel is disposed with the actor
    // held, and the frame -- pumped into the mailbox beforehand -- runs only afterwards.
    // There is no signal for "the handler was not called", so the absence is observed over a
    // bounded wait; a false pass needs the handler's thread-pool item to be delayed past it,
    // a false failure is impossible.
    [Fact]
    public async Task A_Frame_Handled_After_Dispose_Raises_No_DatagramReceived()
    {
        var ep = IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8);
        using var service = new StarvedReaderBusService();
        using var actor = new ProtocolActor();
        using var channel = new IsoTpChannel(service, ep, FastOptions(), ownsService: false, actor);
        var raised = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.DatagramReceived += (_, _) => raised.TrySetResult(true);

        using var gate = new SemaphoreSlim(0);
        var held = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        actor.Post(() =>
        {
            held.TrySetResult(true);
            gate.Wait();
        });
        await held.Task.WaitAsync(ShortTimeout);

        // A Single Frame, taken off the subscription and posted behind the held actor.
        service.Deliver(new CanFrameView(CanFrameType.Can20, 0x7E8,
            new byte[] { 0x03, 0xAA, 0xBB, 0xCC, 0, 0, 0, 0 }, FrameFlags.None));
        channel.GetReceptionsInProgress();

        channel.Dispose();
        gate.Release();
        await actor.PostAsync(() => { }).WaitAsync(ShortTimeout);

        var winner = await Task.WhenAny(raised.Task, Task.Delay(TimeSpan.FromMilliseconds(500)));
        winner.Should().NotBeSameAs(raised.Task, "no handler may start once the channel is disposed");
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
