using System;
using System.Linq;
using CanKit.Pro.CANopen.Emcy;
using CanKit.Pro.CANopen.Sdo;
using FluentAssertions;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

/// <summary>
/// Unit tests for the CANopen codecs. EMCY, <see cref="SdoFrames"/> and <see cref="SdoBlockFrames"/>
/// are checked here on the bytes themselves, without a node. The CRC-16/XMODEM check value
/// ("123456789" → 0x31C3) lives next to the block-transfer tests that use it.
/// </summary>
public class CanOpenCodecTests
{
    // FR-CO-011: EMCY round-trip preserves error code, register byte and manufacturer field.
    [Fact]
    public void Emcy_Encode_Decode_RoundTrip()
    {
        var msg = new EmcyMessage(producerNodeId: 0x21, errorCode: 0x8110,
            errorRegister: 0x01, manufacturerSpecific: new byte[] { 0xAA, 0xBB, 0xCC });

        var wire = msg.Encode();
        wire.Should().HaveCount(EmcyMessage.WireSize);
        wire[0].Should().Be(0x10);
        wire[1].Should().Be(0x81);
        wire[2].Should().Be(0x01);
        wire[3].Should().Be(0xAA);
        wire[7].Should().Be(0x00); // zero-padded

        var decoded = EmcyMessage.Decode(producerNodeId: 0x21, wire);
        decoded.ErrorCode.Should().Be(0x8110);
        decoded.ErrorRegister.Should().Be(0x01);
        decoded.ProducerNodeId.Should().Be(0x21);
        decoded.ManufacturerSpecific.Should().Equal(0xAA, 0xBB, 0xCC, 0x00, 0x00);
    }

    // FR-CO-011: the CiA 301 "no error / reset" code (0x0000) round-trips like any other value.
    [Fact]
    public void Emcy_ErrorReset_RoundTrips()
    {
        var msg = new EmcyMessage(producerNodeId: 0x21, errorCode: 0x0000, errorRegister: 0x00);
        var wire = msg.Encode();
        var decoded = EmcyMessage.Decode(producerNodeId: 0x21, wire);
        decoded.ErrorCode.Should().Be(0x0000);
        decoded.ErrorRegister.Should().Be(0x00);
    }

    // -----------------------------------------------------------------------------------------
    // FR-CO-002 / FR-CO-003 — the expedited-vs-segmented split is a property of the payload
    // length (CiA 301 §7.2.4.3.3 vs. §7.2.4.3.5), not of anything the caller passes in. These
    // tests pin that rule directly on the initiate encoder, so it cannot silently drift back
    // into being a caller-selectable option.
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1, 0x2F)] // n = 3 unused bytes → 0x20 | (3 << 2) | 0x03
    [InlineData(2, 0x2B)] // n = 2
    [InlineData(3, 0x27)] // n = 1
    [InlineData(4, 0x23)] // n = 0, all four bytes valid
    public void SdoDownloadInit_UpToFourBytes_UsesExpeditedEncoding(int length, byte expectedCs)
    {
        var data = new byte[length];
        for (int i = 0; i < length; i++) data[i] = (byte)(0xA0 + i);

        var frame = SdoFrames.BuildDownloadInit(0x2000, 0x01, data);

        frame.Should().HaveCount(8);
        frame[0].Should().Be(expectedCs);
        frame[1].Should().Be(0x00); // index low
        frame[2].Should().Be(0x20); // index high
        frame[3].Should().Be(0x01); // subindex
        // The payload itself rides in bytes 4..7; unused bytes stay zero.
        frame.Skip(4).Take(length).Should().Equal(data);
        frame.Skip(4 + length).Should().AllBeEquivalentTo<byte>(0x00);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(17)]
    [InlineData(1024)]
    public void SdoDownloadInit_AboveFourBytes_UsesSegmentedEncodingWithSizeIndicated(int length)
    {
        var frame = SdoFrames.BuildDownloadInit(0x2100, 0x00, new byte[length]);

        frame.Should().HaveCount(8);
        frame[0].Should().Be(SdoFrames.CcsDownloadInitSegmented); // 0x21, "size indicated"
        // Bytes 4..7 carry the little-endian total length rather than payload.
        var advertised = (uint)(frame[4] | (frame[5] << 8) | (frame[6] << 16) | (frame[7] << 24));
        advertised.Should().Be((uint)length);
    }

    // -----------------------------------------------------------------------------------------
    // SdoFrames — command bytes, multiplexer, expedited length, segmented size bit, segments.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void SdoAbort_RoundTrips_Index_Subindex_And_Code()
    {
        var frame = SdoFrames.BuildAbort(0x2100, 0x05, 0x06090011);

        frame.Should().Equal(0x80, 0x00, 0x21, 0x05, 0x11, 0x00, 0x09, 0x06);
        SdoFrames.ReadIndex(frame).Should().Be(((ushort)0x2100, (byte)0x05));
        SdoFrames.ReadAbortCode(frame).Should().Be(0x06090011u);
    }

    [Fact]
    public void SdoUploadInit_Is_The_Request_Command_And_The_Multiplexer()
    {
        var frame = SdoFrames.BuildUploadInit(0x1018, 0x04);

        frame.Should().Equal(0x40, 0x18, 0x10, 0x04, 0x00, 0x00, 0x00, 0x00);
    }

    [Fact]
    public void SdoExpeditedPayload_Follows_The_Unused_Byte_Count()
    {
        // n is the count of unused bytes in the four-byte data field: 0x4F/0x4B/0x47/0x43.
        SdoFrames.ReadExpeditedPayload(new byte[] { 0x4F, 0x00, 0x10, 0x00, 0x7E, 0x00, 0x00, 0x00 })
            .Should().Equal(0x7E);
        SdoFrames.ReadExpeditedPayload(new byte[] { 0x4B, 0x00, 0x10, 0x00, 0x34, 0x12, 0x00, 0x00 })
            .Should().Equal(0x34, 0x12);
        SdoFrames.ReadExpeditedPayload(new byte[] { 0x47, 0x00, 0x10, 0x00, 0x01, 0x02, 0x03, 0x00 })
            .Should().Equal(0x01, 0x02, 0x03);
        SdoFrames.ReadExpeditedPayload(new byte[] { 0x43, 0x00, 0x10, 0x00, 0x01, 0x02, 0x03, 0x04 })
            .Should().Equal(0x01, 0x02, 0x03, 0x04);
    }

    [Fact]
    public void SdoExpeditedPayload_Without_A_Size_Bit_Keeps_All_Four_Bytes()
    {
        // e=1, s=0: the size is not indicated, so the data is the whole four-byte field.
        var frame = new byte[] { 0x42, 0x00, 0x20, 0x01, 0xAA, 0xBB, 0xCC, 0xDD };

        SdoFrames.ReadExpeditedPayload(frame).Should().Equal(0xAA, 0xBB, 0xCC, 0xDD);
    }

    [Fact]
    public void SdoSegmentedLength_Is_Zero_When_The_Size_Bit_Is_Clear()
    {
        // 0x41 carries a little-endian length. 0x40 is the same command with s=0: bytes 4..7
        // are reserved, and a decoder that reads them anyway reports a multi-gigabyte transfer.
        var indicated = new byte[] { 0x41, 0x00, 0x21, 0x00, 0x00, 0x01, 0x00, 0x00 };
        var notIndicated = new byte[] { 0x40, 0x00, 0x21, 0x00, 0xFF, 0xFF, 0xFF, 0x7F };

        SdoFrames.ReadSegmentedTotalLength(indicated).Should().Be(0x100u);
        SdoFrames.ReadSegmentedTotalLength(notIndicated).Should().Be(0u,
            "s = 0 means the size is not in this frame, whatever bytes 4..7 hold");
    }

    [Fact]
    public void SdoSegment_RoundTrips_Toggle_Last_And_Unused_Bytes()
    {
        var full = SdoFrames.BuildSegment(SdoFrames.CcsDownloadSegmentBase, toggle: false,
            lastSegment: false, new byte[] { 1, 2, 3, 4, 5, 6, 7 });
        full[0].Should().Be(0x00);
        var fullRead = SdoFrames.ReadSegment(full);
        fullRead.Data.Should().Equal(1, 2, 3, 4, 5, 6, 7);
        fullRead.LastSegment.Should().BeFalse();
        fullRead.Toggle.Should().BeFalse();

        var tail = SdoFrames.BuildSegment(SdoFrames.ScsUploadSegmentBase, toggle: true,
            lastSegment: true, new byte[] { 0xAB });
        // n = 6 unused bytes of the 7-byte window, toggle set, last set: 0x10 | (6 << 1) | 0x01.
        tail[0].Should().Be(0x1D);
        tail.Skip(2).Should().AllBeEquivalentTo<byte>(0x00);

        var read = SdoFrames.ReadSegment(tail);
        read.Data.Should().Equal(0xAB);
        read.LastSegment.Should().BeTrue();
        read.Toggle.Should().BeTrue();
    }

    [Fact]
    public void SdoSegment_Rejects_More_Than_Seven_Data_Bytes()
    {
        var act = () => SdoFrames.BuildSegment(0x00, toggle: false, lastSegment: true, new byte[8]);

        act.Should().Throw<ArgumentException>();
    }

    // -----------------------------------------------------------------------------------------
    // SdoBlockFrames — initiate bits, segments, sub-block ack, end-of-block n and CRC.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void BlockDownloadInit_Sets_Crc_And_Size_Bits_And_Omits_The_Size_When_Not_Indicated()
    {
        var sized = SdoBlockFrames.BuildBlockDownloadInit(0x2100, 0x03,
            clientCrcSupported: true, sizeIndicated: true, totalSize: 0x01020304);
        sized[0].Should().Be(0xC6, "ccs=6, cc=1, s=1");
        sized.Skip(1).Take(3).Should().Equal(0x00, 0x21, 0x03);
        sized.Skip(4).Should().Equal(0x04, 0x03, 0x02, 0x01);
        SdoBlockFrames.ReadCrcSupportedBit(sized[0]).Should().BeTrue();

        var unsized = SdoBlockFrames.BuildBlockDownloadInit(0x2100, 0x03,
            clientCrcSupported: false, sizeIndicated: false, totalSize: 99);
        unsized[0].Should().Be(SdoBlockFrames.CcsBlockDownloadInitBase);
        unsized.Skip(4).Should().AllBeEquivalentTo<byte>(0x00,
            "a size that was not indicated does not ride in the frame");
        SdoBlockFrames.ReadCrcSupportedBit(unsized[0]).Should().BeFalse();
    }

    [Fact]
    public void BlockDownloadInitResponse_Carries_The_Block_Size()
    {
        var frame = SdoBlockFrames.BuildBlockDownloadInitResponse(0x1800, 0x02,
            serverCrcSupported: true, blockSize: 127);

        frame[0].Should().Be(0xA4, "scs=5, sc=1");
        SdoFrames.ReadIndex(frame).Should().Be(((ushort)0x1800, (byte)0x02));
        frame[4].Should().Be(127);
        frame.Skip(5).Should().AllBeEquivalentTo<byte>(0x00);
    }

    [Fact]
    public void BlockSegment_RoundTrips_Sequence_And_Last_Bit()
    {
        var frame = SdoBlockFrames.BuildSegment(seqno: 3, isLastSegment: true, new byte[] { 0x10, 0x20 });

        frame[0].Should().Be(0x83, "c=1, seqno=3");
        frame[1].Should().Be(0x10);
        frame[2].Should().Be(0x20);
        frame.Skip(3).Should().AllBeEquivalentTo<byte>(0x00);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(128)]
    public void BlockSegment_Rejects_A_Sequence_Number_Outside_1_To_127(int seqno)
    {
        var act = () => SdoBlockFrames.BuildSegment((byte)seqno, isLastSegment: false, new byte[] { 1 });

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void BlockSegment_Rejects_More_Than_Seven_Data_Bytes()
    {
        var act = () => SdoBlockFrames.BuildSegment(1, isLastSegment: false, new byte[8]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BlockSubBlockAck_RoundTrips_AckSeq_And_Next_Block_Size()
    {
        var frame = SdoBlockFrames.BuildSubBlockAck(SdoBlockFrames.ScsBlockDownloadSubBlockAck,
            lastAckedSeq: 14, nextBlockSize: 8);

        frame[0].Should().Be(0xA2);
        SdoBlockFrames.ReadSubBlockAck(frame).Should().Be(((byte)14, (byte)8));
    }

    [Fact]
    public void BlockEnd_RoundTrips_Unused_Bytes_And_Crc()
    {
        var frame = SdoBlockFrames.BuildEnd(SdoBlockFrames.CcsBlockDownloadEndBase,
            unusedBytesInLastSegment: 5, crc: 0x31C3);

        frame[0].Should().Be(0xD5, "ccs=6, n=5, cs=1: 0xC1 | (5 << 2)");
        SdoBlockFrames.ReadEndUnusedBytes(frame[0]).Should().Be(5);
        SdoBlockFrames.ReadEndCrc(frame).Should().Be(0x31C3);

        var response = SdoBlockFrames.BuildEndResponse(SdoBlockFrames.ScsBlockDownloadEndResponse);
        response.Should().Equal(0xA1, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00);
    }

    [Fact]
    public void BlockEnd_Rejects_An_Unused_Count_Above_Seven()
    {
        var act = () => SdoBlockFrames.BuildEnd(SdoBlockFrames.CcsBlockDownloadEndBase,
            unusedBytesInLastSegment: 8, crc: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void BlockUploadInit_Carries_Block_Size_And_Protocol_Switch_Threshold()
    {
        var frame = SdoBlockFrames.BuildBlockUploadInit(0x2000, 0x01,
            clientCrcSupported: true, blockSize: 16, pst: 0);

        frame[0].Should().Be(0xA4);
        SdoFrames.ReadIndex(frame).Should().Be(((ushort)0x2000, (byte)0x01));
        frame[4].Should().Be(16);
        frame[5].Should().Be(0);
    }

    [Fact]
    public void BlockUploadInitResponse_Size_Is_Zero_When_The_Size_Bit_Is_Clear()
    {
        var indicated = SdoBlockFrames.BuildBlockUploadInitResponse(0x2100, 0x00,
            serverCrcSupported: false, sizeIndicated: true, totalSize: 175);
        indicated[0].Should().Be(0xC2, "scs=6, s=1");
        SdoBlockFrames.ReadUploadTotalSize(indicated).Should().Be(175u);

        var frame = new byte[] { 0xC0, 0x00, 0x21, 0x00, 0xFF, 0xFF, 0xFF, 0x7F };
        SdoBlockFrames.ReadUploadTotalSize(frame).Should().Be(0u,
            "s = 0 means bytes 4..7 are not a length");
        SdoBlockFrames.ReadCrcSupportedBit(frame[0]).Should().BeFalse();
    }
}
