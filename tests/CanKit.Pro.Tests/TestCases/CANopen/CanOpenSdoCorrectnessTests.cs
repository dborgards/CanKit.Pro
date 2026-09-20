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
