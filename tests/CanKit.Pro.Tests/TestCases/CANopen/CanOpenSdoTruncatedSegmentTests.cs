using System;
using System.Collections.Concurrent;
using System.Linq;
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
/// #203: an SDO frame shorter than 8 bytes is padded with zeros so its header can be read. For
/// an initiate or a control frame those zeros are the unused bytes. For a segment they are data:
/// a segment shorter than its own <c>n</c> field declares used to have the missing bytes made up
/// and copied into the transfer, and classic SDO has no CRC to notice. Such a segment is now
/// refused with <see cref="SdoAbortCode.DataTypeLengthMismatch"/>; a short frame that is
/// complete for its <c>n</c> is still accepted.
/// </summary>
public class CanOpenSdoTruncatedSegmentTests : IClassFixture<VirtualAdapterFixture>
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);

    private static string NewSession() => $"canopen-{Guid.NewGuid():N}";

    private static ICanBus Open(string session, int channel) => CanBus.Open(
        $"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(VirtualAdapterFixture.Bitrate));

    private static uint AbortCode(byte[] frame)
        => (uint)(frame[4] | (frame[5] << 8) | (frame[6] << 16) | (frame[7] << 24));

    [Fact]
    public void Server_Refuses_A_Download_Segment_Shorter_Than_Its_N_Field_Declares()
    {
        var session = NewSession();
        using var slaveBus = Open(session, 1);
        using var rawBus = Open(session, 2);
        using var slave = CanOpen.OpenNode(slaveBus, nodeId: 0x11);
        slave.ObjectDictionary.AddDomain(0x2100, 0x00, new byte[20]);
        using var tap = new FrameTap(rawBus, CanOpenCobId.SdoTx(0x11));

        rawBus.Transmit(CanFrame.Classic(unchecked((int)CanOpenCobId.SdoRx(0x11)),
            new byte[] { 0x21, 0x00, 0x21, 0x00, 0x14, 0x00, 0x00, 0x00 }));
        tap.Next(ShortTimeout)[0].Should().Be(0x60, "the segmented initiate is acknowledged");

        // n = 0 declares seven data bytes, but only four arrive (DLC 5).
        rawBus.Transmit(CanFrame.Classic(unchecked((int)CanOpenCobId.SdoRx(0x11)),
            new byte[] { 0x00, 1, 2, 3, 4 }));

        var abort = tap.Next(ShortTimeout);
        abort[0].Should().Be(0x80);
        AbortCode(abort).Should().Be((uint)SdoAbortCode.DataTypeLengthMismatch);
        slave.ObjectDictionary.ReadRaw(0x2100, 0x00).Should().OnlyContain(b => b == 0,
            "the four bytes that did arrive are not committed either");
    }

    [Fact]
    public void Server_Accepts_A_Short_Final_Segment_That_Is_Complete_For_Its_N_Field()
    {
        var session = NewSession();
        using var slaveBus = Open(session, 1);
        using var rawBus = Open(session, 2);
        using var slave = CanOpen.OpenNode(slaveBus, nodeId: 0x11);
        slave.ObjectDictionary.AddDomain(0x2100, 0x00, new byte[20]);
        using var tap = new FrameTap(rawBus, CanOpenCobId.SdoTx(0x11));
        var data = Enumerable.Range(1, 20).Select(i => (byte)i).ToArray();

        void Send(params byte[] frame)
            => rawBus.Transmit(CanFrame.Classic(unchecked((int)CanOpenCobId.SdoRx(0x11)), frame));

        Send(0x21, 0x00, 0x21, 0x00, 0x14, 0x00, 0x00, 0x00);
        tap.Next(ShortTimeout)[0].Should().Be(0x60);
        Send(new byte[] { 0x00 }.Concat(data.Skip(0).Take(7)).ToArray());
        tap.Next(ShortTimeout)[0].Should().Be(0x20);
        Send(new byte[] { 0x10 }.Concat(data.Skip(7).Take(7)).ToArray());
        tap.Next(ShortTimeout)[0].Should().Be(0x30);
        // Six bytes left: n = 1, last, toggle 0 -> cs 0x03, and seven bytes on the wire.
        Send(new byte[] { 0x03 }.Concat(data.Skip(14).Take(6)).ToArray());
        var last = tap.Next(ShortTimeout);
        last[0].Should().Be(0x20, "the short final segment is complete for n = 1 and is acknowledged");

        slave.ObjectDictionary.ReadRaw(0x2100, 0x00).Should().Equal(data);
    }

    [Fact]
    public async Task Client_Aborts_An_Upload_Segment_Shorter_Than_Its_N_Field_Declares()
    {
        var session = NewSession();
        using var masterBus = Open(session, 0);
        using var rawBus = Open(session, 2);
        using var master = CanOpen.OpenNode(masterBus, nodeId: 0x01);
        PeerSdoLaboratory.Bind(master, 0x11);
        using var tap = new FrameTap(rawBus, CanOpenCobId.SdoRx(0x11));

        var upload = master.SdoUploadAsync(serverNodeId: 0x11, index: 0x2B00, subindex: 0x00);
        tap.Next(ShortTimeout)[0].Should().Be(0x40, "the upload initiate");

        // Segmented response declaring 20 bytes, then, on request, a segment of four bytes
        // that claims seven.
        rawBus.Transmit(CanFrame.Classic(unchecked((int)CanOpenCobId.SdoTx(0x11)),
            new byte[] { 0x41, 0x00, 0x2B, 0x00, 0x14, 0x00, 0x00, 0x00 }));
        (tap.Next(ShortTimeout)[0] & 0xE0).Should().Be(0x60, "the client asks for the first segment");
        rawBus.Transmit(CanFrame.Classic(unchecked((int)CanOpenCobId.SdoTx(0x11)),
            new byte[] { 0x00, 1, 2, 3 }));

        var ex = await Assert.ThrowsAsync<SdoAbortException>(() => upload.WithTimeoutAsync(ShortTimeout));
        ex.AbortCode.Should().Be((uint)SdoAbortCode.DataTypeLengthMismatch);
        AbortCode(tap.Next(ShortTimeout)).Should().Be((uint)SdoAbortCode.DataTypeLengthMismatch);
    }

    [Fact]
    public void Block_Server_Refuses_A_Sub_Block_Segment_Shorter_Than_Eight_Bytes()
    {
        var session = NewSession();
        using var slaveBus = Open(session, 1);
        using var rawBus = Open(session, 2);
        using var slave = CanOpen.OpenNode(slaveBus, nodeId: 0x11);
        slave.ObjectDictionary.AddDomain(0x2100, 0x00, new byte[20]);
        using var tap = new FrameTap(rawBus, CanOpenCobId.SdoTx(0x11));

        rawBus.Transmit(CanFrame.Classic(unchecked((int)CanOpenCobId.SdoRx(0x11)),
            SdoBlockFrames.BuildBlockDownloadInit(0x2100, 0x00, clientCrcSupported: false,
                sizeIndicated: true, totalSize: 20)));
        (tap.Next(ShortTimeout)[0] & 0xE0).Should().Be(SdoBlockFrames.ScsBlockDownloadInitResponseBase);

        // Segment 1, four bytes on the wire.
        rawBus.Transmit(CanFrame.Classic(unchecked((int)CanOpenCobId.SdoRx(0x11)),
            new byte[] { 0x01, 1, 2, 3 }));

        AbortCode(tap.Next(ShortTimeout)).Should().Be((uint)SdoAbortCode.DataTypeLengthMismatch);
    }

    [Fact]
    public async Task Block_Client_Aborts_A_Sub_Block_Segment_Shorter_Than_Eight_Bytes()
    {
        var session = NewSession();
        using var masterBus = Open(session, 0);
        using var rawBus = Open(session, 2);
        using var master = CanOpen.OpenNode(masterBus, nodeId: 0x01);
        PeerSdoLaboratory.Bind(master, 0x11);
        using var tap = new FrameTap(rawBus, CanOpenCobId.SdoRx(0x11));

        var upload = master.SdoUploadAsync(serverNodeId: 0x11, index: 0x2B00, subindex: 0x00,
            mode: SdoTransferMode.Block);
        (tap.Next(ShortTimeout)[0] & 0xE0).Should().Be(SdoBlockFrames.CcsBlockUploadInitBase);

        rawBus.Transmit(CanFrame.Classic(unchecked((int)CanOpenCobId.SdoTx(0x11)),
            SdoBlockFrames.BuildBlockUploadInitResponse(0x2B00, 0x00, serverCrcSupported: false,
                sizeIndicated: true, totalSize: 20)));
        tap.Next(ShortTimeout)[0].Should().Be(SdoBlockFrames.CcsBlockUploadStart);

        rawBus.Transmit(CanFrame.Classic(unchecked((int)CanOpenCobId.SdoTx(0x11)),
            new byte[] { 0x01, 1, 2, 3 }));

        var ex = await Assert.ThrowsAsync<SdoAbortException>(() => upload.WithTimeoutAsync(ShortTimeout));
        ex.AbortCode.Should().Be((uint)SdoAbortCode.DataTypeLengthMismatch);
    }

    [Theory]
    [InlineData(8, 0x00, true)]   // n = 0, full frame
    [InlineData(7, 0x00, false)]  // n = 0 but a byte short
    [InlineData(7, 0x02, true)]   // n = 1: six data bytes plus the header
    [InlineData(2, 0x0C, true)]   // n = 6: one data byte
    [InlineData(1, 0x0C, false)]  // n = 6 but the data byte is missing
    [InlineData(1, 0x0E, true)]   // n = 7: no data byte at all
    public void SegmentIsComplete_Compares_The_Wire_Length_With_The_N_Field(int wireLength, byte cs, bool expected)
        => SdoFrames.SegmentIsComplete(wireLength, cs).Should().Be(expected);

    private sealed class FrameTap : IDisposable
    {
        private readonly ICanBus _bus;
        private readonly uint _cobId;
        private readonly BlockingCollection<byte[]> _frames = new();

        public FrameTap(ICanBus bus, uint cobId)
        {
            _bus = bus;
            _cobId = cobId;
            _bus.FrameObserved += OnFrame;
        }

        private void OnFrame(object? sender, CanReceiveDataView e)
        {
            if ((uint)e.CanFrame.ID == _cobId)
                _frames.Add(e.CanFrame.Data.ToArray());
        }

        public byte[] Next(TimeSpan timeout)
        {
            if (!_frames.TryTake(out var frame, timeout))
                throw new TimeoutException($"No frame on COB-ID 0x{_cobId:X3} within {timeout}.");
            return frame;
        }

        public void Dispose() => _bus.FrameObserved -= OnFrame;
    }
}
