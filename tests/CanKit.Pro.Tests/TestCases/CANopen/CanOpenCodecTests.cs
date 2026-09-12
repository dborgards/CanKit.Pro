using System.Linq;
using CanKit.Pro.CANopen.Emcy;
using CanKit.Pro.CANopen.Sdo;
using FluentAssertions;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

/// <summary>
/// Unit tests for CANopen codec primitives (EMCY encoding / decoding, plus the SDO download
/// initiate frame's expedited-vs-segmented selection). The remaining SDO frame codec coverage
/// comes from the integration tests, which round-trip encoded frames through the SDO
/// server + client state machines end-to-end.
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
}
