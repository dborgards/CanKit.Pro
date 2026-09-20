using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

/// <summary>
/// SDO protocol-correctness tests (FR-CO-002 classic SDO, FR-CO-003 segmented, FR-CO-004 block
/// transfer) that drive one side of the exchange with raw frames so the assertion is about what
/// went on the wire, not only about the round-trip result. Companion to
/// <see cref="CanOpenNodeIntegrationTests"/> (happy paths) and
/// <see cref="CanOpenBlockAndGuardingTests"/> (block round-trips and retransmission).
/// </summary>
public class CanOpenSdoCorrectnessTests : IClassFixture<VirtualAdapterFixture>
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);

    private static string NewSession() => $"canopen-{Guid.NewGuid():N}";

    private static ICanBus Open(string session, int channel) => CanBus.Open(
        $"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(VirtualAdapterFixture.Bitrate));

    private static void Send(ICanBus bus, uint cobId, byte[] payload)
        => bus.Transmit(CanFrame.Classic(unchecked((int)cobId), payload, isExtendedFrame: false));

    // -----------------------------------------------------------------------------------------
    // FR-CO-004 — CiA 301 v4.2.0 §7.2.4.3.16 defines the block-transfer CRC by its parameters
    // (x^16 + x^12 + x^5 + 1, 16 bit, initial value 0000h) and supplies one check value: the CRC
    // of "123456789" is 31C3h. Pinning that value is what turns "we implement CRC-16/XMODEM"
    // from a claim into a measurement: a wrong polynomial, a wrong initial value, a reflected
    // variant or a final XOR would each produce a different check value.
    // -----------------------------------------------------------------------------------------
    [Fact]
    public void Crc16_Matches_The_CiA301_Check_Value()
    {
        var crc = SdoBlockFrames.ComputeCrc16Xmodem(Encoding.ASCII.GetBytes("123456789"));
        crc.Should().Be(0x31C3, "CiA 301 §7.2.4.3.16 gives 31C3h as the CRC of \"123456789\"");
    }

    // -----------------------------------------------------------------------------------------
    // FR-CO-004 (#17): the block-upload server's deadline must measure how long the *peer* has
    // been silent, and against SdoServerTimeout, the value the options document for it. Before
    // the fix the upload path armed SdoTimeout once at the initiate and never re-armed, so the
    // deadline measured the whole transfer and any upload longer than one second aborted while
    // perfectly healthy.
    //
    // Driven by a raw-frame fake client so the ACK spacing is the test's to choose. Two things
    // are asserted through one transfer: every gap is longer than SdoTimeout (50 ms here), which
    // pins the option choice, and the sum of the gaps is longer than SdoServerTimeout (2 s here),
    // which pins the re-arm — with the re-arm alone missing, the 2 s armed at the initiate expires
    // while segments are still flowing.
    //
    // Clock analysis, per the working agreement: the quantity the host can perturb is the length
    // of a gap (Task.Delay overshoot plus actor starvation; #92 records about 250 ms of the
    // latter on a CI runner). A longer gap can only strengthen "longer than SdoTimeout"; it can
    // weaken the transfer only by exceeding SdoServerTimeout, and the margin for that is
    // 2 s - 100 ms = 1.9 s per gap, roughly eight times the recorded perturbation. The delays
    // are the subject of the test, not a wait for the code to catch up.
    // -----------------------------------------------------------------------------------------
    [Fact]
    public async Task Sdo_BlockUpload_Server_Deadline_Measures_Peer_Idle_Time_Not_The_Whole_Transfer()
    {
        var session = NewSession();
        using var busB = Open(session, 1);
        using var rawBus = Open(session, 2);

        var serverTimeout = TimeSpan.FromSeconds(2);
        var gap = TimeSpan.FromMilliseconds(100);
        var opts = new CanOpenNodeOptions().With(
            sdoTimeout: TimeSpan.FromMilliseconds(50), sdoServerTimeout: serverTimeout);
        using var server = CanOpen.OpenNode(busB, nodeId: 0x02, opts);

        // blksize 1 makes every 7-byte segment its own sub-block, i.e. one ACK gap per segment:
        // 25 segments -> 25 ACK gaps plus the start gap, 2.6 s nominal, more than serverTimeout.
        const int segments = 25;
        var payload = Enumerable.Range(0, segments * 7).Select(i => (byte)(i * 13)).ToArray();
        server.ObjectDictionary.AddDomain(0x2B10, 0x00, payload, OdAccess.ReadOnly);
        using var tap = new FrameTap(rawBus, CanOpenCobId.SdoTx(0x02));

        Send(rawBus, CanOpenCobId.SdoRx(0x02), SdoBlockFrames.BuildBlockUploadInit(
            0x2B10, 0x00, clientCrcSupported: false, blockSize: 1, pst: 0));
        var initResp = tap.Next(ShortTimeout);
        (initResp[0] & 0xE0).Should().Be(SdoBlockFrames.ScsBlockUploadInitResponseBase);

        await Task.Delay(gap); // first idle period: initiate response -> start
        Send(rawBus, CanOpenCobId.SdoRx(0x02),
            SdoBlockFrames.BuildEndResponse(SdoBlockFrames.CcsBlockUploadStart));

        var received = new List<byte>();
        var subBlocks = 0;
        while (true)
        {
            var seg = tap.Next(ShortTimeout);
            seg[0].Should().NotBe(SdoFrames.CsAbort,
                "the server must not time out a transfer whose peer keeps answering");
            (seg[0] & 0x7F).Should().Be(1, "blksize 1 restarts the seqno at 1 for every sub-block");
            received.AddRange(seg.Skip(1));
            subBlocks++;
            bool last = (seg[0] & 0x80) != 0;

            await Task.Delay(gap); // idle period: segment -> our sub-block ACK
            Send(rawBus, CanOpenCobId.SdoRx(0x02), SdoBlockFrames.BuildSubBlockAck(
                SdoBlockFrames.CcsBlockUploadSubBlockAck, lastAckedSeq: 1, nextBlockSize: 1));
            if (last) break;
        }
        subBlocks.Should().Be(segments);

        var end = tap.Next(ShortTimeout);
        (end[0] & 0xE3).Should().Be(SdoBlockFrames.ScsBlockUploadEndBase);
        SdoBlockFrames.ReadEndUnusedBytes(end[0]).Should().Be(0, "175 bytes fill 25 segments exactly");
        Send(rawBus, CanOpenCobId.SdoRx(0x02),
            SdoBlockFrames.BuildEndResponse(SdoBlockFrames.CcsBlockUploadEndResponse));

        received.Should().Equal(payload);
    }

    // -----------------------------------------------------------------------------------------
    // FR-CO-002 (#18): a success response that names a different object is not this session's.
    // Before the fix the client matched an upload response by command specifier only, so a late
    // answer to a request that had already timed out — or a second master's exchange with the
    // same server, since 0x580 + id is seen by every client on the bus — completed the next
    // request with the wrong object's value and no exception.
    //
    // The stray frame is injected ahead of the right one on the same COB-ID from the same bus,
    // and the node's actor processes frames in arrival order (the existing
    // A_Bootup_Does_Not_Become_The_Toggle_Baseline relies on the same ordering). So the final
    // value is the discriminator: had the stray completed the session, the result would be its
    // bytes; had it aborted the session, the task would throw. No "is it still pending" probe is
    // used because such a probe can pass under the bug as well.
    // -----------------------------------------------------------------------------------------
    [Fact]
    public async Task Sdo_Client_Ignores_An_Upload_Response_That_Names_Another_Object()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var rawBus = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: 0x01);
        using var tap = new FrameTap(rawBus, CanOpenCobId.SdoRx(0x11));

        var upload = master.SdoUploadAsync(serverNodeId: 0x11, index: 0x2001, subindex: 0x00);
        var init = tap.Next(ShortTimeout);
        init[0].Should().Be(SdoFrames.CcsUploadInit);
        SdoFrames.ReadIndex(init).Should().Be(((ushort)0x2001, (byte)0x00));

        // Expedited upload response (scs=2, e=1, s=1, n=0) for 0x2000:00 — well-formed, wrong object.
        Send(rawBus, CanOpenCobId.SdoTx(0x11), new byte[] { 0x43, 0x00, 0x20, 0x00, 0xAA, 0xBB, 0xCC, 0xDD });
        // The response the session is waiting for.
        Send(rawBus, CanOpenCobId.SdoTx(0x11), new byte[] { 0x43, 0x01, 0x20, 0x00, 0x11, 0x22, 0x33, 0x44 });

        var raw = await upload.WithTimeoutAsync(ShortTimeout);
        raw.Should().Equal(new byte[] { 0x11, 0x22, 0x33, 0x44 },
            "the response for 0x2000 must neither complete nor abort a session waiting on 0x2001");
    }

    // FR-CO-002 (#18): the download initiate response (0x60) carries the multiplexer too and
    // used to be accepted for any object — for an expedited download that is a silent success
    // with nothing written. The discriminator is positive on both sides: after the stray ack the
    // fake server aborts the object the session actually asked about. With the fix the abort
    // reaches a still-open session and the task throws with that code; under the bug the stray
    // ack has already completed the task successfully, and the abort finds no session.
    [Fact]
    public async Task Sdo_Client_Ignores_A_Download_Ack_That_Names_Another_Object()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var rawBus = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: 0x01);
        using var tap = new FrameTap(rawBus, CanOpenCobId.SdoRx(0x11));

        var download = master.SdoDownloadAsync(serverNodeId: 0x11, index: 0x2001, subindex: 0x00,
            new byte[] { 0xDE, 0xAD });
        var init = tap.Next(ShortTimeout);
        (init[0] & 0xE0).Should().Be(SdoFrames.CcsDownloadInitExpeditedBase);
        SdoFrames.ReadIndex(init).Should().Be(((ushort)0x2001, (byte)0x00));

        // Download initiate response for 0x2000:00 — wrong object — then an abort for 0x2001:00.
        Send(rawBus, CanOpenCobId.SdoTx(0x11), new byte[] { SdoFrames.ScsDownloadInitAck, 0x00, 0x20, 0x00, 0, 0, 0, 0 });
        Send(rawBus, CanOpenCobId.SdoTx(0x11),
            SdoFrames.BuildAbort(0x2001, 0x00, (uint)SdoAbortCode.AttemptWriteReadOnly));

        var ex = await Assert.ThrowsAsync<SdoAbortException>(() => download.WithTimeoutAsync(ShortTimeout));
        ex.AbortCode.Should().Be((uint)SdoAbortCode.AttemptWriteReadOnly,
            "the session must still be open when its own object's abort arrives, i.e. the ack for 0x2000 must not have completed it");
        ex.Index.Should().Be(0x2001);
    }

    // FR-CO-003 (#18): the other half of attribution is the phase. A segmented download client
    // that has accepted its initiate response exchanges segment acks, which carry no multiplexer;
    // an initiate response arriving now — a duplicate, or a late one — belongs to no phase the
    // session is in and must be ignored. Before the fix a second 0x60 pushed another segment out
    // regardless. The discriminator is on the wire: the second segment goes out only after the
    // fake server acknowledged the first, so it must carry toggle 1. Under the bug the duplicate
    // ack triggers it back to back with the first, still with toggle 0, before this test's own
    // segment ack could even be processed — deterministic, whatever the timing.
    [Fact]
    public async Task Sdo_Client_Ignores_A_Duplicate_Download_Ack_In_The_Segment_Phase()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var rawBus = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: 0x01);
        using var tap = new FrameTap(rawBus, CanOpenCobId.SdoRx(0x11));

        var payload = Enumerable.Range(1, 10).Select(i => (byte)(0x10 * i)).ToArray(); // 7 + 3
        var download = master.SdoDownloadAsync(serverNodeId: 0x11, index: 0x2001, subindex: 0x00, payload);
        var init = tap.Next(ShortTimeout);
        init[0].Should().Be(SdoFrames.CcsDownloadInitSegmented);
        SdoFrames.ReadIndex(init).Should().Be(((ushort)0x2001, (byte)0x00));

        // The initiate response for our object — twice.
        var ack = new byte[] { SdoFrames.ScsDownloadInitAck, 0x01, 0x20, 0x00, 0, 0, 0, 0 };
        Send(rawBus, CanOpenCobId.SdoTx(0x11), ack);
        Send(rawBus, CanOpenCobId.SdoTx(0x11), ack);

        var seg1 = SdoFrames.ReadSegment(tap.Next(ShortTimeout));
        seg1.Toggle.Should().BeFalse();
        seg1.LastSegment.Should().BeFalse();
        seg1.Data.Should().Equal(payload.Take(7));

        Send(rawBus, CanOpenCobId.SdoTx(0x11), new byte[] { SdoFrames.ScsDownloadSegmentBase, 0, 0, 0, 0, 0, 0, 0 });
        var seg2 = SdoFrames.ReadSegment(tap.Next(ShortTimeout));
        seg2.Toggle.Should().BeTrue(
            "the second segment goes out only after our segment ack, so it must carry the alternated toggle; " +
            "a segment already on the wire with toggle 0 was triggered by the duplicate initiate ack");
        seg2.LastSegment.Should().BeTrue();
        seg2.Data.Should().Equal(payload.Skip(7));

        Send(rawBus, CanOpenCobId.SdoTx(0x11),
            new byte[] { SdoFrames.ScsDownloadSegmentBase | SdoFrames.ToggleBit, 0, 0, 0, 0, 0, 0, 0 });
        await download.WithTimeoutAsync(ShortTimeout);
    }

    // FR-CO-004 (#18): the block upload initiate response carries the multiplexer (CiA 301
    // §7.2.4.3.13, Figure 31). Under the bug the stray response moves the client into its
    // segment-receiving phase, where the right response (0xC2: c=1, seqno 66) is mistaken for
    // an out-of-order segment and NACKed; with the fix the client's next frame after both
    // responses is the "start upload" request, and it ACKs the one real segment with ackseq 1.
    [Fact]
    public async Task Sdo_BlockClient_Ignores_An_Upload_Initiate_Response_That_Names_Another_Object()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var rawBus = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: 0x01);
        using var tap = new FrameTap(rawBus, CanOpenCobId.SdoRx(0x11));

        var upload = master.SdoUploadAsync(serverNodeId: 0x11, index: 0x2001, subindex: 0x00,
            mode: SdoTransferMode.Block);
        var init = tap.Next(ShortTimeout);
        (init[0] & 0xE3).Should().Be(SdoBlockFrames.CcsBlockUploadInitBase);
        SdoFrames.ReadIndex(init).Should().Be(((ushort)0x2001, (byte)0x00));

        var payload = new byte[] { 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77 };
        Send(rawBus, CanOpenCobId.SdoTx(0x11), SdoBlockFrames.BuildBlockUploadInitResponse(
            0x2000, 0x00, serverCrcSupported: false, sizeIndicated: true, totalSize: 7));
        Send(rawBus, CanOpenCobId.SdoTx(0x11), SdoBlockFrames.BuildBlockUploadInitResponse(
            0x2001, 0x00, serverCrcSupported: false, sizeIndicated: true, totalSize: 7));

        tap.Next(ShortTimeout)[0].Should().Be(SdoBlockFrames.CcsBlockUploadStart,
            "the client answers the response for its own object, and only that one");

        Send(rawBus, CanOpenCobId.SdoTx(0x11), SdoBlockFrames.BuildSegment(seqno: 1, isLastSegment: true, payload));
        var ack = tap.Next(ShortTimeout);
        ack[0].Should().Be(SdoBlockFrames.CcsBlockUploadSubBlockAck);
        SdoBlockFrames.ReadSubBlockAck(ack).AckSeq.Should().Be(1,
            "a session still awaiting its initiate response accepts seqno 1 as the first segment");

        Send(rawBus, CanOpenCobId.SdoTx(0x11), SdoBlockFrames.BuildEnd(SdoBlockFrames.ScsBlockUploadEndBase,
            unusedBytesInLastSegment: 0, crc: 0));
        tap.Next(ShortTimeout)[0].Should().Be(SdoBlockFrames.CcsBlockUploadEndResponse);

        var raw = await upload.WithTimeoutAsync(ShortTimeout);
        raw.Should().Equal(payload);
    }

    // FR-CO-004 (#18): the block download initiate response carries the multiplexer (CiA 301
    // §7.2.4.3.9, Figure 27). Under the bug the stray response starts the sub-block, and the
    // right response then lands in the ACK-await phase, where its command specifier aborts the
    // transfer; with the fix the transfer completes.
    [Fact]
    public async Task Sdo_BlockClient_Ignores_A_Download_Initiate_Response_That_Names_Another_Object()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var rawBus = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: 0x01);
        using var tap = new FrameTap(rawBus, CanOpenCobId.SdoRx(0x11));

        var payload = Enumerable.Range(0, 20).Select(i => (byte)(0x30 + i)).ToArray();
        var download = master.SdoDownloadAsync(serverNodeId: 0x11, index: 0x2001, subindex: 0x00, payload,
            mode: SdoTransferMode.Block);
        var init = tap.Next(ShortTimeout);
        (init[0] & 0xE1).Should().Be(SdoBlockFrames.CcsBlockDownloadInitBase);
        SdoFrames.ReadIndex(init).Should().Be(((ushort)0x2001, (byte)0x00));

        Send(rawBus, CanOpenCobId.SdoTx(0x11), SdoBlockFrames.BuildBlockDownloadInitResponse(
            0x2000, 0x00, serverCrcSupported: false, blockSize: 3));
        Send(rawBus, CanOpenCobId.SdoTx(0x11), SdoBlockFrames.BuildBlockDownloadInitResponse(
            0x2001, 0x00, serverCrcSupported: false, blockSize: 3));

        var segs = new List<byte[]>();
        for (var i = 0; i < 3; i++) segs.Add(tap.Next(ShortTimeout));
        segs.Select(s => s[0] & 0x7F).Should().Equal(1, 2, 3);
        (segs[2][0] & 0x80).Should().Be(0x80, "20 bytes fit in three segments, so the third is the last");

        Send(rawBus, CanOpenCobId.SdoTx(0x11), SdoBlockFrames.BuildSubBlockAck(
            SdoBlockFrames.ScsBlockDownloadSubBlockAck, lastAckedSeq: 3, nextBlockSize: 3));
        var end = tap.Next(ShortTimeout);
        (end[0] & 0xE3).Should().Be(SdoBlockFrames.CcsBlockDownloadEndBase);
        Send(rawBus, CanOpenCobId.SdoTx(0x11),
            SdoBlockFrames.BuildEndResponse(SdoBlockFrames.ScsBlockDownloadEndResponse));

        await download.WithTimeoutAsync(ShortTimeout);
        segs.SelectMany(s => s.Skip(1)).Take(payload.Length).Should().Equal(payload);
    }

    // Raw-frame tap: queues every frame on a given COB-ID so the test body can drive a fake
    // peer deterministically from its own thread (no in-handler transmits).
    private sealed class FrameTap : IDisposable
    {
        private readonly ICanBus _bus;
        private readonly uint _cobId;
        private readonly System.Collections.Concurrent.BlockingCollection<byte[]> _frames = new();

        public FrameTap(ICanBus bus, uint cobId)
        {
            _bus = bus;
            _cobId = cobId;
            _bus.FrameObserved += OnFrame;
        }

        private void OnFrame(object? sender, CanReceiveDataView e)
        {
            if ((uint)e.CanFrame.ID == _cobId)
            {
                _frames.Add(e.CanFrame.Data.ToArray());
            }
        }

        public byte[] Next(TimeSpan timeout)
        {
            if (!_frames.TryTake(out var frame, timeout))
            {
                throw new TimeoutException($"No frame on COB-ID 0x{_cobId:X3} within {timeout}.");
            }
            return frame;
        }

        public void Dispose() => _bus.FrameObserved -= OnFrame;
    }
}
