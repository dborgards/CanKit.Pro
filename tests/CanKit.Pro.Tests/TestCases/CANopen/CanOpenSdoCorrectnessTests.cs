using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.RawCan;
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
        PeerSdoLaboratory.Bind(master, 0x11);
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
        PeerSdoLaboratory.Bind(master, 0x11);
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
        // #59: the abort frame came from the peer, and the exception says so.
        ex.Origin.Should().Be(SdoAbortOrigin.Peer);
        ex.ErrorCode.Should().Be(ProtocolErrorCodes.ProtocolPeerAbort);
        ex.Message.Should().StartWith("Peer server 0x11 aborted");
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
        PeerSdoLaboratory.Bind(master, 0x11);
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
        PeerSdoLaboratory.Bind(master, 0x11);
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
        PeerSdoLaboratory.Bind(master, 0x11);
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

    // FR-CO-004 (#167): the multiplexer check above only drops a well-formed initiate response
    // for another object. A frame that names THIS object but carries a command specifier the
    // phase is not waiting for — a classic response on 0x580+server, or a block frame from an
    // earlier phase — used to be re-armed and then aborted with 0504 0001h, which fails a
    // healthy client. The classic client ignores that frame and keeps waiting. 0504 0001h
    // answers a request whose specifier the peer does not implement; it is not required here.
    // The discriminator is the wire: under the bug the next client frame is the abort, and the
    // task faults with that code before the real response can finish the transfer.
    [Fact]
    public async Task Sdo_BlockClient_Ignores_A_Stray_Command_Specifier_During_A_Download()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var rawBus = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: 0x01);
        PeerSdoLaboratory.Bind(master, 0x11);
        using var tap = new FrameTap(rawBus, CanOpenCobId.SdoRx(0x11));

        var payload = Enumerable.Range(0, 20).Select(i => (byte)(0x30 + i)).ToArray();
        var download = master.SdoDownloadAsync(serverNodeId: 0x11, index: 0x2001, subindex: 0x00, payload,
            mode: SdoTransferMode.Block);
        var init = tap.Next(ShortTimeout);
        (init[0] & 0xE1).Should().Be(SdoBlockFrames.CcsBlockDownloadInitBase);
        SdoFrames.ReadIndex(init).Should().Be(((ushort)0x2001, (byte)0x00));

        // Classic download initiate response (0x60) for the object this session asked about.
        // Right multiplexer, wrong specifier for a block download waiting on 0xA0.
        Send(rawBus, CanOpenCobId.SdoTx(0x11),
            new byte[] { SdoFrames.ScsDownloadInitAck, 0x01, 0x20, 0x00, 0, 0, 0, 0 });
        Send(rawBus, CanOpenCobId.SdoTx(0x11), SdoBlockFrames.BuildBlockDownloadInitResponse(
            0x2001, 0x00, serverCrcSupported: false, blockSize: 3));

        var segs = new List<byte[]> { tap.Next(ShortTimeout) };
        segs[0][0].Should().NotBe(SdoFrames.CsAbort,
            "a classic ack that names this object must not abort the block download");
        for (var i = 0; i < 2; i++) segs.Add(tap.Next(ShortTimeout));
        segs.Select(s => s[0] & 0x7F).Should().Equal(1, 2, 3);

        Send(rawBus, CanOpenCobId.SdoTx(0x11), SdoBlockFrames.BuildSubBlockAck(
            SdoBlockFrames.ScsBlockDownloadSubBlockAck, lastAckedSeq: 3, nextBlockSize: 3));
        var end = tap.Next(ShortTimeout);
        (end[0] & 0xE3).Should().Be(SdoBlockFrames.CcsBlockDownloadEndBase);

        // Sub-block ack (0xA2) arriving once the client is waiting for the end response (0xA1).
        Send(rawBus, CanOpenCobId.SdoTx(0x11), SdoBlockFrames.BuildSubBlockAck(
            SdoBlockFrames.ScsBlockDownloadSubBlockAck, lastAckedSeq: 3, nextBlockSize: 3));
        Send(rawBus, CanOpenCobId.SdoTx(0x11),
            SdoBlockFrames.BuildEndResponse(SdoBlockFrames.ScsBlockDownloadEndResponse));

        await download.WithTimeoutAsync(ShortTimeout);
        segs.SelectMany(s => s.Skip(1)).Take(payload.Length).Should().Equal(payload);
    }

    // FR-CO-004 (#167): same stray-specifier rule on the upload client. An expedited classic
    // upload (0x43) for this object must not complete or abort a block upload that is still
    // waiting for its initiate response, and a duplicate initiate response must not abort the
    // transfer once it is waiting for the end frame.
    [Fact]
    public async Task Sdo_BlockClient_Ignores_A_Stray_Command_Specifier_During_An_Upload()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var rawBus = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: 0x01);
        PeerSdoLaboratory.Bind(master, 0x11);
        using var tap = new FrameTap(rawBus, CanOpenCobId.SdoRx(0x11));

        var upload = master.SdoUploadAsync(serverNodeId: 0x11, index: 0x2001, subindex: 0x00,
            mode: SdoTransferMode.Block);
        var init = tap.Next(ShortTimeout);
        (init[0] & 0xE3).Should().Be(SdoBlockFrames.CcsBlockUploadInitBase);
        SdoFrames.ReadIndex(init).Should().Be(((ushort)0x2001, (byte)0x00));

        var payload = new byte[] { 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77 };
        // Expedited upload of this same object: the specifier a classic client would accept.
        Send(rawBus, CanOpenCobId.SdoTx(0x11), new byte[] { 0x43, 0x01, 0x20, 0x00, 0xAA, 0xBB, 0xCC, 0xDD });
        Send(rawBus, CanOpenCobId.SdoTx(0x11), SdoBlockFrames.BuildBlockUploadInitResponse(
            0x2001, 0x00, serverCrcSupported: false, sizeIndicated: true, totalSize: 7));

        var start = tap.Next(ShortTimeout);
        start[0].Should().Be(SdoBlockFrames.CcsBlockUploadStart,
            "the expedited response must neither abort the block upload nor be taken as its result");

        Send(rawBus, CanOpenCobId.SdoTx(0x11), SdoBlockFrames.BuildSegment(seqno: 1, isLastSegment: true, payload));
        var ack = tap.Next(ShortTimeout);
        ack[0].Should().Be(SdoBlockFrames.CcsBlockUploadSubBlockAck);

        Send(rawBus, CanOpenCobId.SdoTx(0x11), SdoBlockFrames.BuildBlockUploadInitResponse(
            0x2001, 0x00, serverCrcSupported: false, sizeIndicated: true, totalSize: 7));
        Send(rawBus, CanOpenCobId.SdoTx(0x11), SdoBlockFrames.BuildEnd(SdoBlockFrames.ScsBlockUploadEndBase,
            unusedBytesInLastSegment: 0, crc: 0));
        tap.Next(ShortTimeout)[0].Should().Be(SdoBlockFrames.CcsBlockUploadEndResponse,
            "a duplicate initiate response while waiting for the end frame must not abort the upload");

        var raw = await upload.WithTimeoutAsync(ShortTimeout);
        raw.Should().Equal(payload);
    }

    // FR-CO-004 (#167): the matcher names only the phases a block client waits in. The download
    // switch still has a branch for every other value of the phase enum — SendingSegments,
    // which the client passes through without reading the bus, and AwaitEnd, which a download
    // never uses. A frame delivered in either must be ignored, not aborted with 0504 0001h.
    // The session is moved on its actor and the frame is handed to the same incoming path the
    // reader uses, so the phase and the frame are one turn.
    [Fact]
    public async Task Sdo_BlockClient_Ignores_A_Download_Frame_In_A_Phase_With_No_Specifier()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var rawBus = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: 0x01);
        PeerSdoLaboratory.Bind(master, 0x11);
        using var tap = new FrameTap(rawBus, CanOpenCobId.SdoRx(0x11));

        var payload = new byte[] { 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37 };
        var download = master.SdoDownloadAsync(serverNodeId: 0x11, index: 0x2001, subindex: 0x00, payload,
            mode: SdoTransferMode.Block);
        var init = tap.Next(ShortTimeout);
        (init[0] & 0xE1).Should().Be(SdoBlockFrames.CcsBlockDownloadInitBase);

        var stray = new byte[] { SdoFrames.ScsDownloadInitAck, 0x01, 0x20, 0x00, 0, 0, 0, 0 };
        // In the jump table (SendingSegments = 1) and past it (AwaitEnd = 5).
        DeliverBlockFrameWhilePhase(master, 0x11, "SendingSegments", "AwaitInitResponse", stray);
        DeliverBlockFrameWhilePhase(master, 0x11, "AwaitEnd", "AwaitInitResponse", stray);
        download.IsCompleted.Should().BeFalse("a frame in a phase with no specifier must not finish the download");

        Send(rawBus, CanOpenCobId.SdoTx(0x11), SdoBlockFrames.BuildBlockDownloadInitResponse(
            0x2001, 0x00, serverCrcSupported: false, blockSize: 1));
        var seg = tap.Next(ShortTimeout);
        seg[0].Should().NotBe(SdoFrames.CsAbort);
        (seg[0] & 0x7F).Should().Be(1);

        Send(rawBus, CanOpenCobId.SdoTx(0x11), SdoBlockFrames.BuildSubBlockAck(
            SdoBlockFrames.ScsBlockDownloadSubBlockAck, lastAckedSeq: 1, nextBlockSize: 1));
        (tap.Next(ShortTimeout)[0] & 0xE3).Should().Be(SdoBlockFrames.CcsBlockDownloadEndBase);
        Send(rawBus, CanOpenCobId.SdoTx(0x11),
            SdoBlockFrames.BuildEndResponse(SdoBlockFrames.ScsBlockDownloadEndResponse));
        await download.WithTimeoutAsync(ShortTimeout);
    }

    // FR-CO-004 (#167): the upload matcher names AwaitInitResponse, ReceivingSegments and
    // AwaitEnd. The other three phase values are the download client's. A frame that arrives
    // in one of them is ignored the same way.
    [Fact]
    public async Task Sdo_BlockClient_Ignores_An_Upload_Frame_In_A_Phase_With_No_Specifier()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var rawBus = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: 0x01);
        PeerSdoLaboratory.Bind(master, 0x11);
        using var tap = new FrameTap(rawBus, CanOpenCobId.SdoRx(0x11));

        var upload = master.SdoUploadAsync(serverNodeId: 0x11, index: 0x2001, subindex: 0x00,
            mode: SdoTransferMode.Block);
        var init = tap.Next(ShortTimeout);
        (init[0] & 0xE3).Should().Be(SdoBlockFrames.CcsBlockUploadInitBase);

        var stray = new byte[] { 0x43, 0x01, 0x20, 0x00, 0xAA, 0xBB, 0xCC, 0xDD };
        DeliverBlockFrameWhilePhase(master, 0x11, "SendingSegments", "AwaitInitResponse", stray);
        DeliverBlockFrameWhilePhase(master, 0x11, "AwaitSubBlockAck", "AwaitInitResponse", stray);
        DeliverBlockFrameWhilePhase(master, 0x11, "AwaitEndResponse", "AwaitInitResponse", stray);
        upload.IsCompleted.Should().BeFalse("a frame in a phase with no specifier must not finish the upload");

        var payload = new byte[] { 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77 };
        Send(rawBus, CanOpenCobId.SdoTx(0x11), SdoBlockFrames.BuildBlockUploadInitResponse(
            0x2001, 0x00, serverCrcSupported: false, sizeIndicated: true, totalSize: 7));
        tap.Next(ShortTimeout)[0].Should().Be(SdoBlockFrames.CcsBlockUploadStart);

        Send(rawBus, CanOpenCobId.SdoTx(0x11), SdoBlockFrames.BuildSegment(seqno: 1, isLastSegment: true, payload));
        tap.Next(ShortTimeout)[0].Should().Be(SdoBlockFrames.CcsBlockUploadSubBlockAck);
        Send(rawBus, CanOpenCobId.SdoTx(0x11), SdoBlockFrames.BuildEnd(SdoBlockFrames.ScsBlockUploadEndBase,
            unusedBytesInLastSegment: 0, crc: 0));
        tap.Next(ShortTimeout)[0].Should().Be(SdoBlockFrames.CcsBlockUploadEndResponse);
        (await upload.WithTimeoutAsync(ShortTimeout)).Should().Equal(payload);
    }

    // The phase is private and a block client never leaves it sitting across a bus frame, so the
    // frame is delivered on the actor with the phase already set. Restoring the phase in that
    // same turn is what lets the real response, sent afterwards, belong to the transfer again.
    private static bool IsFatal(Exception ex) =>
        ex is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or AppDomainUnloadedException
            or BadImageFormatException
            or CannotUnloadAppDomainException
            or InvalidProgramException
            or ThreadAbortException;

    private static void DeliverBlockFrameWhilePhase(ICanOpenNode node, byte serverNodeId, string phase,
        string restore, byte[] data)
    {
        var nodeType = node.GetType();
        var incoming = nodeType.GetMethod("HandleIncoming", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var phaseType = nodeType.GetNestedType("SdoBlockClientPhase", BindingFlags.NonPublic)!;
        var clientsField = nodeType.GetField("_sdoBlockClients", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var actor = nodeType.GetField("_actor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(node)!;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action work = () =>
        {
            try
            {
                var clients = (IDictionary)clientsField.GetValue(node)!;
                object? session = null;
                foreach (DictionaryEntry entry in clients)
                    session = entry.Value;
                var phaseProperty = session!.GetType().GetProperty("Phase")!;
                phaseProperty.SetValue(session, Enum.Parse(phaseType, phase));
                incoming.Invoke(node, new object[] { CanOpenCobId.SdoTx(serverNodeId), data, false });
                phaseProperty.SetValue(session, Enum.Parse(phaseType, restore));
                done.TrySetResult();
            }
            catch (Exception ex) when (!IsFatal(ex))
            {
                done.TrySetException(ex);
            }
        };
        actor.GetType().GetMethod("Post", new[] { typeof(Action) })!.Invoke(actor, new object[] { work });
        done.Task.GetAwaiter().GetResult();
    }

    // -----------------------------------------------------------------------------------------
    // FR-CO-004 (#39): one lost segment must draw exactly one confirm, at the end of the
    // sub-block, carrying the last good seqno. CiA 301 §7.2.4.3.10 (Figure 28) confirms a
    // sub-block with a single frame whose "ackseq: sequence number of last segment that was
    // received successfully during the last block download" tells the client where to resume;
    // before the fix every segment after the gap drew its own confirm — up to 126 for one lost
    // frame. The discriminator is the *second* control frame the server sends: with the fix it
    // is the confirm of the retransmitted remainder (ackseq 5); under the bug it is the second
    // NACK (ackseq 2), already on the wire before the retransmission started.
    // -----------------------------------------------------------------------------------------
    [Fact]
    public async Task Sdo_BlockDownload_Server_Confirms_A_Damaged_SubBlock_Once_And_Completes_After_Retransmission()
    {
        var session = NewSession();
        using var busB = Open(session, 1);
        using var rawBus = Open(session, 2);

        var opts = new CanOpenNodeOptions().With(sdoBlockSize: 5);
        using var server = CanOpen.OpenNode(busB, nodeId: 0x02, opts);
        var payload = Enumerable.Range(0, 35).Select(i => (byte)(0x40 + i)).ToArray(); // 5 segments
        server.ObjectDictionary.AddDomain(0x2100, 0x00, new byte[35]);
        using var tap = new FrameTap(rawBus, CanOpenCobId.SdoTx(0x02));

        Send(rawBus, CanOpenCobId.SdoRx(0x02), SdoBlockFrames.BuildBlockDownloadInit(
            0x2100, 0x00, clientCrcSupported: false, sizeIndicated: true, totalSize: 35));
        var initResp = tap.Next(ShortTimeout);
        (initResp[0] & 0xE3).Should().Be(SdoBlockFrames.ScsBlockDownloadInitResponseBase);
        initResp[4].Should().Be(5, "the server announces its blksize");

        byte[] Segment(int seqno, bool lastOverall) => SdoBlockFrames.BuildSegment(
            (byte)seqno, isLastSegment: lastOverall, payload.AsSpan((seqno - 1) * 7, 7));

        // Sub-block with segment 3 lost: 1, 2, 4, 5 (c = 1).
        Send(rawBus, CanOpenCobId.SdoRx(0x02), Segment(1, false));
        Send(rawBus, CanOpenCobId.SdoRx(0x02), Segment(2, false));
        Send(rawBus, CanOpenCobId.SdoRx(0x02), Segment(4, false));
        Send(rawBus, CanOpenCobId.SdoRx(0x02), Segment(5, true));

        var confirm = tap.Next(ShortTimeout);
        confirm[0].Should().Be(SdoBlockFrames.ScsBlockDownloadSubBlockAck);
        SdoBlockFrames.ReadSubBlockAck(confirm).Should().Be(((byte)2, (byte)5),
            "ackseq is the last segment received successfully, and the blksize is unchanged");

        // Retransmission from ackseq + 1 with the original numbering.
        Send(rawBus, CanOpenCobId.SdoRx(0x02), Segment(3, false));
        Send(rawBus, CanOpenCobId.SdoRx(0x02), Segment(4, false));
        Send(rawBus, CanOpenCobId.SdoRx(0x02), Segment(5, true));

        var confirm2 = tap.Next(ShortTimeout);
        confirm2[0].Should().Be(SdoBlockFrames.ScsBlockDownloadSubBlockAck);
        SdoBlockFrames.ReadSubBlockAck(confirm2).AckSeq.Should().Be(5,
            "the damaged sub-block drew exactly one confirm, so the next control frame confirms the retransmission");

        Send(rawBus, CanOpenCobId.SdoRx(0x02), SdoBlockFrames.BuildEnd(
            SdoBlockFrames.CcsBlockDownloadEndBase, unusedBytesInLastSegment: 0, crc: 0));
        tap.Next(ShortTimeout)[0].Should().Be(SdoBlockFrames.ScsBlockDownloadEndResponse);
        server.ObjectDictionary.ReadRaw(0x2100, 0x00).Should().Equal(payload);
    }

    // FR-CO-004 (#39): in the segment phase, byte 0 is (c << 7) | seqno and nothing else. A
    // segment whose byte 0 happens to spell a classic initiate — 0x21 is seqno 33 — used to be
    // handed to the classic server, which superseded the running transfer with an abort. It is
    // an out-of-order segment like any other: the sub-block is confirmed once with the last
    // good seqno, the sender resumes, and the transfer completes.
    [Fact]
    public async Task Sdo_BlockDownload_Server_Reads_A_Segment_Spelling_An_Initiate_As_A_Segment()
    {
        var session = NewSession();
        using var busB = Open(session, 1);
        using var rawBus = Open(session, 2);

        using var server = CanOpen.OpenNode(busB, nodeId: 0x02); // blksize 127: seqno 33 is in range
        var payload = Enumerable.Range(0, 21).Select(i => (byte)(0x90 + i)).ToArray(); // 3 segments
        server.ObjectDictionary.AddDomain(0x2100, 0x00, new byte[21]);
        using var tap = new FrameTap(rawBus, CanOpenCobId.SdoTx(0x02));

        Send(rawBus, CanOpenCobId.SdoRx(0x02), SdoBlockFrames.BuildBlockDownloadInit(
            0x2100, 0x00, clientCrcSupported: false, sizeIndicated: true, totalSize: 21));
        (tap.Next(ShortTimeout)[0] & 0xE3).Should().Be(SdoBlockFrames.ScsBlockDownloadInitResponseBase);

        byte[] Segment(int seqno, bool lastOverall) => SdoBlockFrames.BuildSegment(
            (byte)seqno, isLastSegment: lastOverall, payload.AsSpan((seqno - 1) * 7, 7));

        Send(rawBus, CanOpenCobId.SdoRx(0x02), Segment(1, false));
        // Segment 2 is lost; what arrives instead is a segment numbered 33, whose byte 0 (0x21)
        // is also the classic "initiate download, segmented" command specifier. Bytes 1..3 would
        // read as index 0x2000:00 if anyone mistook it for one.
        Send(rawBus, CanOpenCobId.SdoRx(0x02), new byte[] { 0x21, 0x00, 0x20, 0x00, 0x11, 0x22, 0x33, 0x44 });
        Send(rawBus, CanOpenCobId.SdoRx(0x02), Segment(2, false));
        Send(rawBus, CanOpenCobId.SdoRx(0x02), Segment(3, true));

        var confirm = tap.Next(ShortTimeout);
        confirm[0].Should().Be(SdoBlockFrames.ScsBlockDownloadSubBlockAck,
            "the session must survive a segment that merely looks like an initiate");
        SdoBlockFrames.ReadSubBlockAck(confirm).AckSeq.Should().Be(1);

        Send(rawBus, CanOpenCobId.SdoRx(0x02), Segment(2, false));
        Send(rawBus, CanOpenCobId.SdoRx(0x02), Segment(3, true));
        SdoBlockFrames.ReadSubBlockAck(tap.Next(ShortTimeout)).AckSeq.Should().Be(3);

        Send(rawBus, CanOpenCobId.SdoRx(0x02), SdoBlockFrames.BuildEnd(
            SdoBlockFrames.CcsBlockDownloadEndBase, unusedBytesInLastSegment: 0, crc: 0));
        tap.Next(ShortTimeout)[0].Should().Be(SdoBlockFrames.ScsBlockDownloadEndResponse);
        server.ObjectDictionary.ReadRaw(0x2100, 0x00).Should().Equal(payload);
    }

    // FR-CO-004 (#39): CiA 301 §7.2.4.3.10 requires "0 < seqno < 128" and a sub-block never
    // numbers past blksize (§7.2.4.2.10). Seqno 0 with c = 0 (byte 0x00) and a seqno beyond
    // the announced blksize are protocol errors, answered with Table 22 0504 0003h against the
    // transfer's own object. Byte 0x80, seqno 0 with c = 1, is the abort and is handled as one.
    [Theory]
    [InlineData((byte)0x00)] // seqno 0
    [InlineData((byte)0x06)] // seqno 6 with blksize 5
    public async Task Sdo_BlockDownload_Server_Aborts_An_Invalid_Sequence_Number(byte byte0)
    {
        var session = NewSession();
        using var busB = Open(session, 1);
        using var rawBus = Open(session, 2);

        var opts = new CanOpenNodeOptions().With(sdoBlockSize: 5);
        using var server = CanOpen.OpenNode(busB, nodeId: 0x02, opts);
        server.ObjectDictionary.AddDomain(0x2100, 0x00, new byte[35]);
        using var tap = new FrameTap(rawBus, CanOpenCobId.SdoTx(0x02));

        Send(rawBus, CanOpenCobId.SdoRx(0x02), SdoBlockFrames.BuildBlockDownloadInit(
            0x2100, 0x00, clientCrcSupported: false, sizeIndicated: true, totalSize: 35));
        (tap.Next(ShortTimeout)[0] & 0xE3).Should().Be(SdoBlockFrames.ScsBlockDownloadInitResponseBase);

        Send(rawBus, CanOpenCobId.SdoRx(0x02), new byte[] { byte0, 1, 2, 3, 4, 5, 6, 7 });

        var abort = tap.Next(ShortTimeout);
        abort[0].Should().Be(SdoFrames.CsAbort);
        SdoFrames.ReadIndex(abort).Should().Be(((ushort)0x2100, (byte)0x00), "the abort names the transfer's object");
        SdoFrames.ReadAbortCode(abort).Should().Be((uint)SdoAbortCode.InvalidSequenceNumber);
        await Task.CompletedTask;
    }

    // FR-CO-004 (#39): the block-upload client receives segments the same way, with its own
    // confirm (CiA 301 §7.2.4.3.14, Figure 32), and had the same storm. Same discriminator as
    // the server test: the second control frame after the damaged sub-block confirms the
    // retransmission (ackseq 5), not the gap again.
    [Fact]
    public async Task Sdo_BlockUpload_Client_Confirms_A_Damaged_SubBlock_Once_And_Completes_After_Retransmission()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var rawBus = Open(session, 1);

        var opts = new CanOpenNodeOptions().With(sdoBlockSize: 5);
        using var master = CanOpen.OpenNode(busA, nodeId: 0x01, opts);
        PeerSdoLaboratory.Bind(master, 0x11);
        using var tap = new FrameTap(rawBus, CanOpenCobId.SdoRx(0x11));
        var payload = Enumerable.Range(0, 35).Select(i => (byte)(0x60 + i)).ToArray(); // 5 segments

        var upload = master.SdoUploadAsync(serverNodeId: 0x11, index: 0x2100, subindex: 0x00,
            mode: SdoTransferMode.Block);
        var init = tap.Next(ShortTimeout);
        (init[0] & 0xE3).Should().Be(SdoBlockFrames.CcsBlockUploadInitBase);
        init[4].Should().Be(5, "the client announces its blksize");

        Send(rawBus, CanOpenCobId.SdoTx(0x11), SdoBlockFrames.BuildBlockUploadInitResponse(
            0x2100, 0x00, serverCrcSupported: false, sizeIndicated: true, totalSize: 35));
        tap.Next(ShortTimeout)[0].Should().Be(SdoBlockFrames.CcsBlockUploadStart);

        byte[] Segment(int seqno, bool lastOverall) => SdoBlockFrames.BuildSegment(
            (byte)seqno, isLastSegment: lastOverall, payload.AsSpan((seqno - 1) * 7, 7));

        Send(rawBus, CanOpenCobId.SdoTx(0x11), Segment(1, false));
        Send(rawBus, CanOpenCobId.SdoTx(0x11), Segment(2, false));
        Send(rawBus, CanOpenCobId.SdoTx(0x11), Segment(4, false));
        Send(rawBus, CanOpenCobId.SdoTx(0x11), Segment(5, true));

        var confirm = tap.Next(ShortTimeout);
        confirm[0].Should().Be(SdoBlockFrames.CcsBlockUploadSubBlockAck);
        SdoBlockFrames.ReadSubBlockAck(confirm).Should().Be(((byte)2, (byte)5));

        Send(rawBus, CanOpenCobId.SdoTx(0x11), Segment(3, false));
        Send(rawBus, CanOpenCobId.SdoTx(0x11), Segment(4, false));
        Send(rawBus, CanOpenCobId.SdoTx(0x11), Segment(5, true));

        var confirm2 = tap.Next(ShortTimeout);
        confirm2[0].Should().Be(SdoBlockFrames.CcsBlockUploadSubBlockAck);
        SdoBlockFrames.ReadSubBlockAck(confirm2).AckSeq.Should().Be(5,
            "the damaged sub-block drew exactly one confirm, so the next control frame confirms the retransmission");

        Send(rawBus, CanOpenCobId.SdoTx(0x11), SdoBlockFrames.BuildEnd(
            SdoBlockFrames.ScsBlockUploadEndBase, unusedBytesInLastSegment: 0, crc: 0));
        tap.Next(ShortTimeout)[0].Should().Be(SdoBlockFrames.CcsBlockUploadEndResponse);
        (await upload.WithTimeoutAsync(ShortTimeout)).Should().Equal(payload);
    }

    // FR-CO-004 (#39): the block-upload client aborts an invalid seqno with 0504 0003h on the
    // wire, and the caller's task fails with that same code.
    [Theory]
    [InlineData((byte)0x00)] // seqno 0
    [InlineData((byte)0x06)] // seqno 6 with blksize 5
    public async Task Sdo_BlockUpload_Client_Aborts_An_Invalid_Sequence_Number(byte byte0)
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var rawBus = Open(session, 1);

        var opts = new CanOpenNodeOptions().With(sdoBlockSize: 5);
        using var master = CanOpen.OpenNode(busA, nodeId: 0x01, opts);
        PeerSdoLaboratory.Bind(master, 0x11);
        using var tap = new FrameTap(rawBus, CanOpenCobId.SdoRx(0x11));

        var upload = master.SdoUploadAsync(serverNodeId: 0x11, index: 0x2100, subindex: 0x00,
            mode: SdoTransferMode.Block);
        (tap.Next(ShortTimeout)[0] & 0xE3).Should().Be(SdoBlockFrames.CcsBlockUploadInitBase);
        Send(rawBus, CanOpenCobId.SdoTx(0x11), SdoBlockFrames.BuildBlockUploadInitResponse(
            0x2100, 0x00, serverCrcSupported: false, sizeIndicated: true, totalSize: 35));
        tap.Next(ShortTimeout)[0].Should().Be(SdoBlockFrames.CcsBlockUploadStart);

        Send(rawBus, CanOpenCobId.SdoTx(0x11), new byte[] { byte0, 1, 2, 3, 4, 5, 6, 7 });

        var abort = tap.Next(ShortTimeout);
        abort[0].Should().Be(SdoFrames.CsAbort);
        SdoFrames.ReadAbortCode(abort).Should().Be((uint)SdoAbortCode.InvalidSequenceNumber);
        var ex = await Assert.ThrowsAsync<SdoAbortException>(() => upload.WithTimeoutAsync(ShortTimeout));
        ex.AbortCode.Should().Be((uint)SdoAbortCode.InvalidSequenceNumber);
        // #59: this node detected the violation and sent the abort; the code names the mechanism
        // (an SDO abort ended the exchange) and Origin names the side.
        ex.Origin.Should().Be(SdoAbortOrigin.Local);
        ex.ErrorCode.Should().Be(ProtocolErrorCodes.ProtocolPeerAbort);
        ex.Message.Should().StartWith("This node aborted");
    }

    // -----------------------------------------------------------------------------------------
    // FR-CO-004 (#59): MaxSdoTransferBytes is the boundary the option names, so a block
    // transfer of exactly that many bytes succeeds and one byte more is refused with
    // OutOfMemory. Before the fix both block receivers refused a segment whenever its whole
    // seven-byte window would not fit under the cap, which for 1024 = 146 * 7 + 2 aborted the
    // transfer at its last segment — the one whose two data bytes were within the cap.
    // Both nodes are real; the receiving side carries the cap.
    // -----------------------------------------------------------------------------------------
    [Fact]
    public async Task Sdo_BlockDownload_Of_Exactly_MaxSdoTransferBytes_Succeeds_And_One_More_Is_Refused()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: 0x01);
        PeerSdoLaboratory.Bind(master, 0x11);
        using var slave = CanOpen.OpenNode(busB, nodeId: 0x11, new CanOpenNodeOptions().With(maxSdoTransferBytes: 1024));
        PeerSdoLaboratory.Bind(slave, 0x11);

        var exact = Enumerable.Range(0, 1024).Select(i => (byte)(i * 7)).ToArray();
        slave.ObjectDictionary.AddDomain(0x2A10, 0x00, new byte[1024]);
        await master.SdoDownloadAsync(0x11, 0x2A10, 0x00, exact, mode: SdoTransferMode.Block)
            .WithTimeoutAsync(ShortTimeout);
        slave.ObjectDictionary.ReadRaw(0x2A10, 0x00).Should().Equal(exact);

        var ex = await Assert.ThrowsAsync<SdoAbortException>(() => master
            .SdoDownloadAsync(0x11, 0x2A10, 0x00, new byte[1025], mode: SdoTransferMode.Block)
            .WithTimeoutAsync(ShortTimeout));
        ex.AbortCode.Should().Be((uint)SdoAbortCode.OutOfMemory);
    }

    [Fact]
    public async Task Sdo_BlockUpload_Of_Exactly_MaxSdoTransferBytes_Succeeds_And_One_More_Is_Refused()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: 0x01, new CanOpenNodeOptions().With(maxSdoTransferBytes: 1024));
        PeerSdoLaboratory.Bind(master, 0x11);
        using var slave = CanOpen.OpenNode(busB, nodeId: 0x11);
        PeerSdoLaboratory.Bind(slave, 0x11);

        var exact = Enumerable.Range(0, 1024).Select(i => (byte)(i * 11)).ToArray();
        slave.ObjectDictionary.AddDomain(0x2B20, 0x00, exact, OdAccess.ReadOnly);
        slave.ObjectDictionary.AddDomain(0x2B21, 0x00, new byte[1025], OdAccess.ReadOnly);

        var raw = await master.SdoUploadAsync(0x11, 0x2B20, 0x00, mode: SdoTransferMode.Block)
            .WithTimeoutAsync(ShortTimeout);
        raw.Should().Equal(exact);

        var ex = await Assert.ThrowsAsync<SdoAbortException>(() => master
            .SdoUploadAsync(0x11, 0x2B21, 0x00, mode: SdoTransferMode.Block)
            .WithTimeoutAsync(ShortTimeout));
        ex.AbortCode.Should().Be((uint)SdoAbortCode.OutOfMemory);
    }

    // -----------------------------------------------------------------------------------------
    // FR-CO-004 (#59): CiA 301 §7.2.4.3.13 (Figure 31) defines the block-upload initiate's
    // blksize as "0 < blksize < 128". A value outside that range is answered with Table 22
    // 0504 0002h, invalid block size, against the requested object — not repaired: 0 used to
    // be replaced by the server's own default and 128 and above clamped to 127, so a client
    // sending an invalid initiate got a transfer it never asked for.
    // -----------------------------------------------------------------------------------------
    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)128)]
    public async Task Sdo_BlockUpload_Server_Aborts_An_Initiate_With_An_Invalid_Blksize(byte blksize)
    {
        var session = NewSession();
        using var busB = Open(session, 1);
        using var rawBus = Open(session, 2);
        using var server = CanOpen.OpenNode(busB, nodeId: 0x02);
        server.ObjectDictionary.AddDomain(0x2100, 0x00, new byte[20], OdAccess.ReadOnly);
        using var tap = new FrameTap(rawBus, CanOpenCobId.SdoTx(0x02));

        Send(rawBus, CanOpenCobId.SdoRx(0x02), SdoBlockFrames.BuildBlockUploadInit(
            0x2100, 0x00, clientCrcSupported: false, blockSize: blksize, pst: 0));

        var reply = tap.Next(ShortTimeout);
        reply[0].Should().Be(SdoFrames.CsAbort, "an invalid blksize is refused, not repaired");
        SdoFrames.ReadIndex(reply).Should().Be(((ushort)0x2100, (byte)0x00));
        SdoFrames.ReadAbortCode(reply).Should().Be((uint)SdoAbortCode.InvalidBlockSize);
        await Task.CompletedTask;
    }

    // -----------------------------------------------------------------------------------------
    // FR-CO-002 (#59): the per-transfer CancellationTokenRegistration was never disposed. On a
    // long-lived token — an application-lifetime CancellationTokenSource shared by every
    // request — each transfer left one registration behind, and through the registration's
    // state one completed task with its result, for as long as the token lived. Once a transfer
    // is over the registration is the only thing that still references its task, so
    // reachability is the discriminator: with the token still alive, the completed task must
    // become collectable.
    //
    // The release runs as a continuation on the task, which the TaskCompletionSource schedules
    // asynchronously, so "collectable" is an eventual property: it is polled with full
    // collections up to ShortTimeout, the bound the wire taps already use for asynchronous
    // effects, against a thread-pool hop measured in microseconds. Under the defect the task
    // stays reachable however long one waits, so the bound cannot make a broken build pass.
    // -----------------------------------------------------------------------------------------
    [Fact]
    public void Sdo_Transfer_Releases_Its_Cancellation_Registration_When_It_Completes()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: 0x01);
        PeerSdoLaboratory.Bind(master, 0x11);
        using var slave = CanOpen.OpenNode(busB, nodeId: 0x11);
        PeerSdoLaboratory.Bind(slave, 0x11);
        slave.ObjectDictionary.AddU32(0x1000, 0x00, 0x00030191u, OdAccess.ReadOnly);
        using var cts = new CancellationTokenSource(); // outlives the transfer, as an app-lifetime token does

        var completed = RunOneUpload(master, cts.Token);

        BecomesCollectable(completed).Should().BeTrue(
            "a finished transfer must not stay reachable from a token that outlives it");
        GC.KeepAlive(cts);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RunOneUpload(ICanOpenNode master, CancellationToken token)
    {
        var task = master.SdoUploadAsync(serverNodeId: 0x11, index: 0x1000, subindex: 0x00, cancellationToken: token);
        task.Wait(ShortTimeout).Should().BeTrue("the upload completes on a healthy virtual bus");
        task.Result.Should().Equal(0x91, 0x01, 0x03, 0x00);
        return new WeakReference(task);
    }

    private static bool BecomesCollectable(WeakReference target)
    {
        var bound = DateTime.UtcNow + ShortTimeout;
        do
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (!target.IsAlive) return true;
            Thread.Yield();
        } while (DateTime.UtcNow < bound);
        return false;
    }

    // -----------------------------------------------------------------------------------------
    // FR-CO-002 (#59): SdoAbortException could not tell "the peer aborted" from "we gave up" —
    // both carried ProtocolPeerAbort, and 0504 0000h is the code either side sends when its own
    // timer expires. Origin names the side; the ErrorCode follows the mechanism, so this node's
    // own request timer expiring is ProtocolTimeout, as an ISO-TP or J1939-TP timer expiry is.
    // The transfer targets a server that does not exist, so nothing but the client's own timer
    // can end it; the wait is for that signal, bounded by ShortTimeout, not a measurement.
    // Its sibling assertions: a peer's abort is Origin.Peer / ProtocolPeerAbort in
    // Sdo_Client_Ignores_A_Download_Ack_That_Names_Another_Object, and a violation this node
    // detects is Origin.Local / ProtocolPeerAbort in
    // Sdo_BlockUpload_Client_Aborts_An_Invalid_Sequence_Number.
    // -----------------------------------------------------------------------------------------
    [Fact]
    public async Task Sdo_Client_Timeout_Is_Reported_As_A_Local_Abort_With_ProtocolTimeout()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var master = CanOpen.OpenNode(busA, nodeId: 0x01,
            new CanOpenNodeOptions().With(sdoTimeout: TimeSpan.FromMilliseconds(50)));
        PeerSdoLaboratory.Bind(master, 0x7E);

        var ex = await Assert.ThrowsAsync<SdoAbortException>(() => master
            .SdoUploadAsync(serverNodeId: 0x7E, index: 0x1000, subindex: 0x00)
            .WithTimeoutAsync(ShortTimeout));

        ex.AbortCode.Should().Be((uint)SdoAbortCode.SdoProtocolTimedOut);
        ex.Origin.Should().Be(SdoAbortOrigin.Local, "nobody answered; this node gave up");
        ex.ErrorCode.Should().Be(ProtocolErrorCodes.ProtocolTimeout,
            "a request timer expiring on a still-active exchange is a protocol timeout, whatever the layer");
        ex.Message.Should().StartWith("This node aborted");
    }

    // -----------------------------------------------------------------------------------------
    // #38 — CiA 301 separates e (expedited) from s (size indicated). 0x20 is a segmented
    // download whose bytes 4..7 are reserved, not data; 0x40 is a segmented upload response
    // whose length arrives with the segments. Before the fix the server committed four bytes
    // out of a 0x20 initiate and aborted the segments, and the client ignored 0x40.
    // -----------------------------------------------------------------------------------------
    [Fact]
    public void Sdo_Server_Treats_Download_Initiate_0x20_As_Segmented_Not_Expedited()
    {
        var session = NewSession();
        using var busB = Open(session, 1);
        using var rawBus = Open(session, 2);
        using var server = CanOpen.OpenNode(busB, nodeId: 0x02);
        var original = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
        server.ObjectDictionary.AddDomain(0x2100, 0x00, original);
        using var tap = new FrameTap(rawBus, CanOpenCobId.SdoTx(0x02));

        // ccs=1, e=0, s=0. Bytes 4..7 are reserved; an expedited reading takes them as the value.
        Send(rawBus, CanOpenCobId.SdoRx(0x02), new byte[] { 0x20, 0x00, 0x21, 0x00, 0x11, 0x22, 0x33, 0x44 });
        var initAck = tap.Next(ShortTimeout);
        initAck[0].Should().Be(SdoFrames.ScsDownloadInitAck, "a segmented download is acknowledged, not aborted");
        SdoFrames.ReadIndex(initAck).Should().Be(((ushort)0x2100, (byte)0x00));
        server.ObjectDictionary.ReadRaw(0x2100, 0x00).Should().Equal(original,
            "nothing is committed at the initiate: the four reserved bytes are not an expedited payload");

        var payload = Enumerable.Range(0, 10).Select(i => (byte)(0xA0 + i)).ToArray();
        Send(rawBus, CanOpenCobId.SdoRx(0x02), SdoFrames.BuildSegment(
            SdoFrames.CcsDownloadSegmentBase, toggle: false, lastSegment: false, payload.AsSpan(0, 7)));
        tap.Next(ShortTimeout)[0].Should().Be(SdoFrames.ScsDownloadSegmentBase,
            "the segment is part of the transfer, not a protocol error against an expedited write");

        Send(rawBus, CanOpenCobId.SdoRx(0x02), SdoFrames.BuildSegment(
            SdoFrames.CcsDownloadSegmentBase, toggle: true, lastSegment: true, payload.AsSpan(7)));
        tap.Next(ShortTimeout)[0].Should().Be((byte)(SdoFrames.ScsDownloadSegmentBase | SdoFrames.ToggleBit));

        server.ObjectDictionary.ReadRaw(0x2100, 0x00).Should().Equal(payload,
            "the value is the segments, in order, and not the reserved bytes of the 0x20 initiate");
    }

    [Fact]
    public async Task Sdo_Client_Treats_Upload_Response_0x40_As_Segmented_Not_Ignored()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var rawBus = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: 0x01);
        PeerSdoLaboratory.Bind(master, 0x11);
        using var tap = new FrameTap(rawBus, CanOpenCobId.SdoRx(0x11));

        var upload = master.SdoUploadAsync(serverNodeId: 0x11, index: 0x2001, subindex: 0x05);
        var init = tap.Next(ShortTimeout);
        init[0].Should().Be(SdoFrames.CcsUploadInit);
        SdoFrames.ReadIndex(init).Should().Be(((ushort)0x2001, (byte)0x05));

        // scs=2, e=0, s=0. Bytes 4..7 would be a 2 GiB length if the size bit were ignored,
        // and four data bytes if the frame were read as expedited.
        Send(rawBus, CanOpenCobId.SdoTx(0x11), new byte[] { 0x40, 0x01, 0x20, 0x05, 0xFF, 0xFF, 0xFF, 0x7F });

        var segReq = tap.Next(ShortTimeout);
        segReq[0].Should().Be(SdoFrames.CcsUploadSegmentBase,
            "0x40 opens the segment phase. An expedited reading completes with no further frame, " +
            "and taking the reserved bytes as a size aborts the transfer as too large");
        upload.IsCompleted.Should().BeFalse("the value has not arrived yet");

        var payload = Enumerable.Range(0, 10).Select(i => (byte)(0x50 + i)).ToArray();
        Send(rawBus, CanOpenCobId.SdoTx(0x11), SdoFrames.BuildSegment(
            SdoFrames.ScsUploadSegmentBase, toggle: false, lastSegment: false, payload.AsSpan(0, 7)));
        tap.Next(ShortTimeout)[0].Should().Be((byte)(SdoFrames.CcsUploadSegmentBase | SdoFrames.ToggleBit));

        Send(rawBus, CanOpenCobId.SdoTx(0x11), SdoFrames.BuildSegment(
            SdoFrames.ScsUploadSegmentBase, toggle: true, lastSegment: true, payload.AsSpan(7)));

        var raw = await upload.WithTimeoutAsync(ShortTimeout);
        raw.Should().Equal(payload, "the client assembles the segments; the reserved bytes of 0x40 are not the value");
    }

    // A frame that names the object this upload is waiting on, but is not an upload initiate
    // response, is not this phase. The value that arrives afterwards is the discriminator: a
    // download ack must neither complete the upload nor abort it.
    [Fact]
    public async Task Sdo_Client_Ignores_A_Download_Ack_That_Names_The_Upload_It_Is_Waiting_On()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var rawBus = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: 0x01);
        PeerSdoLaboratory.Bind(master, 0x11);
        using var tap = new FrameTap(rawBus, CanOpenCobId.SdoRx(0x11));

        var upload = master.SdoUploadAsync(serverNodeId: 0x11, index: 0x2001, subindex: 0x05);
        var init = tap.Next(ShortTimeout);
        init[0].Should().Be(SdoFrames.CcsUploadInit);
        SdoFrames.ReadIndex(init).Should().Be(((ushort)0x2001, (byte)0x05));

        Send(rawBus, CanOpenCobId.SdoTx(0x11), new byte[] { SdoFrames.ScsDownloadInitAck, 0x01, 0x20, 0x05, 0, 0, 0, 0 });
        Send(rawBus, CanOpenCobId.SdoTx(0x11), new byte[] { 0x43, 0x01, 0x20, 0x05, 0x11, 0x22, 0x33, 0x44 });

        var raw = await upload.WithTimeoutAsync(ShortTimeout);
        raw.Should().Equal(new byte[] { 0x11, 0x22, 0x33, 0x44 },
            "the download ack is not an upload response, so the expedited value that follows is the result");
    }

    // Size-less server growth (#38, review on the exact-size realloc). The buffer starts empty
    // and the first allocation is 8 bytes, then capacity doubles until the next double would
    // pass MaxSdoTransferBytes, where it clamps to that cap. Offset stays the logical length,
    // so slack past it is not part of the value. Segment sizes below are chosen so one transfer
    // takes each of those steps: 7 bytes lands in the seed, 14 doubles 8→16, 21 clamps 16→24
    // (16 is already past half of 24, so doubling would allocate 32), and the last 3 fit.
    [Fact]
    public void Sdo_Server_Sizeless_Download_Doubles_Then_Clamps_To_The_Cap()
    {
        var session = NewSession();
        using var busB = Open(session, 1);
        using var rawBus = Open(session, 2);
        using var server = CanOpen.OpenNode(busB, nodeId: 0x02,
            new CanOpenNodeOptions().With(maxSdoTransferBytes: 24));
        var original = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
        server.ObjectDictionary.AddDomain(0x2100, 0x00, original);
        using var tap = new FrameTap(rawBus, CanOpenCobId.SdoTx(0x02));

        SendSizelessDownloadInit(rawBus, 0x02, 0x2100, 0x00);
        tap.Next(ShortTimeout)[0].Should().Be(SdoFrames.ScsDownloadInitAck);
        server.ObjectDictionary.ReadRaw(0x2100, 0x00).Should().Equal(original);

        var payload = Enumerable.Range(0, 24).Select(i => (byte)i).ToArray();
        SendDownloadSegment(rawBus, 0x02, toggle: false, last: false, payload.AsSpan(0, 7));
        tap.Next(ShortTimeout)[0].Should().Be(SdoFrames.ScsDownloadSegmentBase);
        SendDownloadSegment(rawBus, 0x02, toggle: true, last: false, payload.AsSpan(7, 7));
        tap.Next(ShortTimeout)[0].Should().Be((byte)(SdoFrames.ScsDownloadSegmentBase | SdoFrames.ToggleBit));
        SendDownloadSegment(rawBus, 0x02, toggle: false, last: false, payload.AsSpan(14, 7));
        tap.Next(ShortTimeout)[0].Should().Be(SdoFrames.ScsDownloadSegmentBase);
        SendDownloadSegment(rawBus, 0x02, toggle: true, last: true, payload.AsSpan(21, 3));
        tap.Next(ShortTimeout)[0].Should().Be((byte)(SdoFrames.ScsDownloadSegmentBase | SdoFrames.ToggleBit));

        server.ObjectDictionary.ReadRaw(0x2100, 0x00).Should().Equal(payload,
            "the value is the 24 segment bytes; capacity left above Offset is not written");
    }

    [Fact]
    public void Sdo_Server_Sizeless_Download_Aborts_OutOfMemory_Before_Passing_The_Cap()
    {
        var session = NewSession();
        using var busB = Open(session, 1);
        using var rawBus = Open(session, 2);
        using var server = CanOpen.OpenNode(busB, nodeId: 0x02,
            new CanOpenNodeOptions().With(maxSdoTransferBytes: 24));
        var original = new byte[] { 0xEE, 0xEE, 0xEE, 0xEE };
        server.ObjectDictionary.AddDomain(0x2100, 0x00, original);
        using var tap = new FrameTap(rawBus, CanOpenCobId.SdoTx(0x02));

        SendSizelessDownloadInit(rawBus, 0x02, 0x2100, 0x00);
        tap.Next(ShortTimeout)[0].Should().Be(SdoFrames.ScsDownloadInitAck);

        var chunk = new byte[] { 1, 2, 3, 4, 5, 6, 7 };
        SendDownloadSegment(rawBus, 0x02, toggle: false, last: false, chunk);
        tap.Next(ShortTimeout)[0].Should().Be(SdoFrames.ScsDownloadSegmentBase);
        SendDownloadSegment(rawBus, 0x02, toggle: true, last: false, chunk);
        tap.Next(ShortTimeout)[0].Should().Be((byte)(SdoFrames.ScsDownloadSegmentBase | SdoFrames.ToggleBit));
        SendDownloadSegment(rawBus, 0x02, toggle: false, last: false, chunk);
        tap.Next(ShortTimeout)[0].Should().Be(SdoFrames.ScsDownloadSegmentBase);

        // 21 accepted bytes plus another 7 would be 28, past the cap of 24. The segment is
        // refused before it is copied, and the session does not stay open to be finished later.
        SendDownloadSegment(rawBus, 0x02, toggle: true, last: false, chunk);
        var abort = tap.Next(ShortTimeout);
        abort[0].Should().Be(SdoFrames.CsAbort);
        SdoFrames.ReadAbortCode(abort).Should().Be((uint)SdoAbortCode.OutOfMemory);
        server.ObjectDictionary.ReadRaw(0x2100, 0x00).Should().Equal(original);

        SendDownloadSegment(rawBus, 0x02, toggle: false, last: true, new byte[] { 0x01, 0x02, 0x03 });
        var stray = tap.Next(ShortTimeout);
        stray[0].Should().Be(SdoFrames.CsAbort);
        SdoFrames.ReadAbortCode(stray).Should().Be((uint)SdoAbortCode.CommandSpecifierInvalid,
            "the over-cap segment closed the transfer; a later segment is not a continuation");
        server.ObjectDictionary.ReadRaw(0x2100, 0x00).Should().Equal(original);
    }

    // A cap below the 8-byte seed must clamp the first allocation rather than allocate the seed
    // and then trim. Four bytes in one last segment is the whole transfer.
    [Fact]
    public void Sdo_Server_Sizeless_Download_Clamps_The_First_Allocation_Below_The_Growth_Seed()
    {
        var session = NewSession();
        using var busB = Open(session, 1);
        using var rawBus = Open(session, 2);
        using var server = CanOpen.OpenNode(busB, nodeId: 0x02,
            new CanOpenNodeOptions().With(maxSdoTransferBytes: 4));
        server.ObjectDictionary.AddDomain(0x2100, 0x00, new byte[] { 0x00 });
        using var tap = new FrameTap(rawBus, CanOpenCobId.SdoTx(0x02));

        SendSizelessDownloadInit(rawBus, 0x02, 0x2100, 0x00);
        tap.Next(ShortTimeout)[0].Should().Be(SdoFrames.ScsDownloadInitAck);

        var payload = new byte[] { 0x10, 0x20, 0x30, 0x40 };
        SendDownloadSegment(rawBus, 0x02, toggle: false, last: true, payload);
        tap.Next(ShortTimeout)[0].Should().Be(SdoFrames.ScsDownloadSegmentBase);
        server.ObjectDictionary.ReadRaw(0x2100, 0x00).Should().Equal(payload);
    }

    // With no size in the initiate, a fixed-width object can be checked only when the last
    // segment says how long the value is. A domain (no fixed width) is the other side of that
    // check and is covered by the transfers above.
    [Fact]
    public void Sdo_Server_Sizeless_Download_Accepts_A_Fixed_Width_Object_At_Its_Exact_Length()
    {
        var session = NewSession();
        using var busB = Open(session, 1);
        using var rawBus = Open(session, 2);
        using var server = CanOpen.OpenNode(busB, nodeId: 0x02);
        server.ObjectDictionary.AddU16(0x2100, 0x00, 0xBEEF);
        using var tap = new FrameTap(rawBus, CanOpenCobId.SdoTx(0x02));

        SendSizelessDownloadInit(rawBus, 0x02, 0x2100, 0x00);
        tap.Next(ShortTimeout)[0].Should().Be(SdoFrames.ScsDownloadInitAck);

        SendDownloadSegment(rawBus, 0x02, toggle: false, last: true, new byte[] { 0x34, 0x12 });
        tap.Next(ShortTimeout)[0].Should().Be(SdoFrames.ScsDownloadSegmentBase);
        server.ObjectDictionary.ReadUnsigned(0x2100, 0x00).Should().Be(0x1234u);
    }

    [Fact]
    public void Sdo_Server_Sizeless_Download_Aborts_When_A_Fixed_Width_Object_Comes_Out_Short()
    {
        var session = NewSession();
        using var busB = Open(session, 1);
        using var rawBus = Open(session, 2);
        using var server = CanOpen.OpenNode(busB, nodeId: 0x02);
        server.ObjectDictionary.AddU16(0x2100, 0x00, 0xBEEF);
        using var tap = new FrameTap(rawBus, CanOpenCobId.SdoTx(0x02));

        SendSizelessDownloadInit(rawBus, 0x02, 0x2100, 0x00);
        tap.Next(ShortTimeout)[0].Should().Be(SdoFrames.ScsDownloadInitAck);

        SendDownloadSegment(rawBus, 0x02, toggle: false, last: true, new byte[] { 0x34 });
        var abort = tap.Next(ShortTimeout);
        abort[0].Should().Be(SdoFrames.CsAbort);
        SdoFrames.ReadAbortCode(abort).Should().Be((uint)SdoAbortCode.LengthTooLow);
        server.ObjectDictionary.ReadUnsigned(0x2100, 0x00).Should().Be(0xBEEFu);
    }

    [Fact]
    public void Sdo_Server_Sizeless_Download_Aborts_When_A_Fixed_Width_Object_Comes_Out_Long()
    {
        var session = NewSession();
        using var busB = Open(session, 1);
        using var rawBus = Open(session, 2);
        using var server = CanOpen.OpenNode(busB, nodeId: 0x02);
        server.ObjectDictionary.AddU8(0x2100, 0x00, 0x5A);
        using var tap = new FrameTap(rawBus, CanOpenCobId.SdoTx(0x02));

        SendSizelessDownloadInit(rawBus, 0x02, 0x2100, 0x00);
        tap.Next(ShortTimeout)[0].Should().Be(SdoFrames.ScsDownloadInitAck);

        SendDownloadSegment(rawBus, 0x02, toggle: false, last: true, new byte[] { 0x01, 0x02 });
        var abort = tap.Next(ShortTimeout);
        abort[0].Should().Be(SdoFrames.CsAbort);
        SdoFrames.ReadAbortCode(abort).Should().Be((uint)SdoAbortCode.LengthTooHigh);
        server.ObjectDictionary.ReadUnsigned(0x2100, 0x00).Should().Be(0x5Au);
    }

    // A sized initiate still allocates exactly the declared length. Too few bytes at the last
    // segment, or a segment that runs past that buffer, are the two length aborts on that path.
    [Fact]
    public void Sdo_Server_Sized_Download_Aborts_LengthTooLow_When_The_Last_Segment_Is_Short()
    {
        var session = NewSession();
        using var busB = Open(session, 1);
        using var rawBus = Open(session, 2);
        using var server = CanOpen.OpenNode(busB, nodeId: 0x02);
        var original = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
        server.ObjectDictionary.AddDomain(0x2100, 0x00, original);
        using var tap = new FrameTap(rawBus, CanOpenCobId.SdoTx(0x02));

        Send(rawBus, CanOpenCobId.SdoRx(0x02), SizedDownloadInit(0x2100, 0x00, 10));
        tap.Next(ShortTimeout)[0].Should().Be(SdoFrames.ScsDownloadInitAck);

        SendDownloadSegment(rawBus, 0x02, toggle: false, last: true, new byte[] { 1, 2, 3, 4, 5, 6, 7 });
        var abort = tap.Next(ShortTimeout);
        abort[0].Should().Be(SdoFrames.CsAbort);
        SdoFrames.ReadAbortCode(abort).Should().Be((uint)SdoAbortCode.LengthTooLow);
        server.ObjectDictionary.ReadRaw(0x2100, 0x00).Should().Equal(original);
    }

    [Fact]
    public void Sdo_Server_Sized_Download_Aborts_LengthTooHigh_When_A_Segment_Overruns_The_Buffer()
    {
        var session = NewSession();
        using var busB = Open(session, 1);
        using var rawBus = Open(session, 2);
        using var server = CanOpen.OpenNode(busB, nodeId: 0x02);
        var original = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
        server.ObjectDictionary.AddDomain(0x2100, 0x00, original);
        using var tap = new FrameTap(rawBus, CanOpenCobId.SdoTx(0x02));

        Send(rawBus, CanOpenCobId.SdoRx(0x02), SizedDownloadInit(0x2100, 0x00, 4));
        tap.Next(ShortTimeout)[0].Should().Be(SdoFrames.ScsDownloadInitAck);

        SendDownloadSegment(rawBus, 0x02, toggle: false, last: false, new byte[] { 1, 2, 3, 4, 5, 6, 7 });
        var abort = tap.Next(ShortTimeout);
        abort[0].Should().Be(SdoFrames.CsAbort);
        SdoFrames.ReadAbortCode(abort).Should().Be((uint)SdoAbortCode.LengthTooHigh);
        server.ObjectDictionary.ReadRaw(0x2100, 0x00).Should().Equal(original);
    }

    // The declared length of a sized initiate is checked against a fixed-width object before
    // any segment. UNSIGNED64 is eight bytes, so a matching initiate is segmented rather than
    // expedited, and a length on either side of eight aborts at the initiate.
    [Fact]
    public void Sdo_Server_Sized_Download_Of_A_Fixed_Width_Object_Requires_The_Declared_Length()
    {
        var session = NewSession();
        using var busB = Open(session, 1);
        using var rawBus = Open(session, 2);
        using var server = CanOpen.OpenNode(busB, nodeId: 0x02);
        var original = new byte[8];
        server.ObjectDictionary.AddRaw(0x2100, 0x00, OdDataType.Unsigned64, original);
        using var tap = new FrameTap(rawBus, CanOpenCobId.SdoTx(0x02));

        Send(rawBus, CanOpenCobId.SdoRx(0x02), SizedDownloadInit(0x2100, 0x00, 1));
        var tooLow = tap.Next(ShortTimeout);
        tooLow[0].Should().Be(SdoFrames.CsAbort);
        SdoFrames.ReadAbortCode(tooLow).Should().Be((uint)SdoAbortCode.LengthTooLow);

        Send(rawBus, CanOpenCobId.SdoRx(0x02), SizedDownloadInit(0x2100, 0x00, 9));
        var tooHigh = tap.Next(ShortTimeout);
        tooHigh[0].Should().Be(SdoFrames.CsAbort);
        SdoFrames.ReadAbortCode(tooHigh).Should().Be((uint)SdoAbortCode.LengthTooHigh);
        server.ObjectDictionary.ReadRaw(0x2100, 0x00).Should().Equal(original);

        var payload = Enumerable.Range(0, 8).Select(i => (byte)(0x80 + i)).ToArray();
        Send(rawBus, CanOpenCobId.SdoRx(0x02), SizedDownloadInit(0x2100, 0x00, 8));
        tap.Next(ShortTimeout)[0].Should().Be(SdoFrames.ScsDownloadInitAck);
        SendDownloadSegment(rawBus, 0x02, toggle: false, last: false, payload.AsSpan(0, 7));
        tap.Next(ShortTimeout)[0].Should().Be(SdoFrames.ScsDownloadSegmentBase);
        SendDownloadSegment(rawBus, 0x02, toggle: true, last: true, payload.AsSpan(7, 1));
        tap.Next(ShortTimeout)[0].Should().Be((byte)(SdoFrames.ScsDownloadSegmentBase | SdoFrames.ToggleBit));
        server.ObjectDictionary.ReadRaw(0x2100, 0x00).Should().Equal(payload);
    }

    // The declared length is capped before the buffer is allocated. A 0x21 whose size field is
    // past MaxSdoTransferBytes aborts with OutOfMemory and installs no session.
    [Fact]
    public void Sdo_Server_Sized_Download_Aborts_OutOfMemory_When_The_Declared_Length_Exceeds_The_Cap()
    {
        var session = NewSession();
        using var busB = Open(session, 1);
        using var rawBus = Open(session, 2);
        using var server = CanOpen.OpenNode(busB, nodeId: 0x02,
            new CanOpenNodeOptions().With(maxSdoTransferBytes: 16));
        var original = new byte[] { 0xFF, 0xFF };
        server.ObjectDictionary.AddDomain(0x2100, 0x00, original);
        using var tap = new FrameTap(rawBus, CanOpenCobId.SdoTx(0x02));

        Send(rawBus, CanOpenCobId.SdoRx(0x02), SizedDownloadInit(0x2100, 0x00, 17));
        var abort = tap.Next(ShortTimeout);
        abort[0].Should().Be(SdoFrames.CsAbort);
        SdoFrames.ReadAbortCode(abort).Should().Be((uint)SdoAbortCode.OutOfMemory);
        server.ObjectDictionary.ReadRaw(0x2100, 0x00).Should().Equal(original);

        SendDownloadSegment(rawBus, 0x02, toggle: false, last: true, new byte[] { 0x01 });
        var stray = tap.Next(ShortTimeout);
        stray[0].Should().Be(SdoFrames.CsAbort);
        SdoFrames.ReadAbortCode(stray).Should().Be((uint)SdoAbortCode.CommandSpecifierInvalid,
            "the over-cap initiate was refused before a session existed");
        server.ObjectDictionary.ReadRaw(0x2100, 0x00).Should().Equal(original);
    }

    private static void SendSizelessDownloadInit(ICanBus rawBus, byte nodeId, ushort index, byte subindex)
        => Send(rawBus, CanOpenCobId.SdoRx(nodeId), new byte[]
        {
            0x20,
            (byte)(index & 0xFF), (byte)((index >> 8) & 0xFF), subindex,
            0x11, 0x22, 0x33, 0x44,
        });

    private static byte[] SizedDownloadInit(ushort index, byte subindex, uint length) => new byte[]
    {
        0x21,
        (byte)(index & 0xFF), (byte)((index >> 8) & 0xFF), subindex,
        (byte)length, (byte)(length >> 8), (byte)(length >> 16), (byte)(length >> 24),
    };

    private static void SendDownloadSegment(ICanBus rawBus, byte nodeId, bool toggle, bool last, ReadOnlySpan<byte> payload)
        => Send(rawBus, CanOpenCobId.SdoRx(nodeId), SdoFrames.BuildSegment(
            SdoFrames.CcsDownloadSegmentBase, toggle, last, payload));

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
