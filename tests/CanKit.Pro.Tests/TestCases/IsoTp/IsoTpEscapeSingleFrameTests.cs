using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core;
using CanKit.Pro.IsoTp;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;
using IsoTpFactory = CanKit.Pro.IsoTp.IsoTp;

namespace CanKit.Pro.Tests.TestCases.IsoTp;

/// <summary>
/// The CAN_DL bounds of a received escape Single Frame (ISO 15765-2:2024 §9.6.2.2, Table 14):
/// the escape form only above CAN_DL 8, and only with an SF_DL the next smaller CAN_DL could not
/// have carried.
/// </summary>
public class IsoTpEscapeSingleFrameTests : IClassFixture<VirtualAdapterFixture>
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(10);

    private static string NewSession() => $"isotp-esc-{Guid.NewGuid():N}";

    private static ICanBus OpenCanFd(string session, int channel) => CanBus.Open(
        $"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.CanFd).Fd(VirtualAdapterFixture.Bitrate, VirtualAdapterFixture.DataBitrate));

    private static IsoTpChannelOptions Options() => new()
    {
        UseCanFd = true,
        NAs = TimeSpan.FromMilliseconds(500),
        NBs = TimeSpan.FromMilliseconds(500),
        NCr = TimeSpan.FromMilliseconds(500),
    };

    [Theory]
    // CAN_DL 8 or less: the escape form is not allowed at all.
    [InlineData(8, false, 3, false)]
    [InlineData(8, false, 6, false)]
    // CAN_DL 12: SF_DL 8..10 (normal), 7..9 (extended).
    [InlineData(12, false, 7, false)]
    [InlineData(12, false, 8, true)]
    [InlineData(12, false, 10, true)]
    [InlineData(12, false, 11, false)]
    [InlineData(12, true, 6, false)]
    [InlineData(12, true, 7, true)]
    [InlineData(12, true, 9, true)]
    [InlineData(12, true, 10, false)]
    // CAN_DL 16: SF_DL 11..14 (normal), 10..13 (extended).
    [InlineData(16, false, 10, false)]
    [InlineData(16, false, 11, true)]
    [InlineData(16, true, 9, false)]
    [InlineData(16, true, 10, true)]
    // CAN_DL 64: SF_DL 47..62 (normal), 46..61 (extended).
    [InlineData(64, false, 46, false)]
    [InlineData(64, false, 47, true)]
    [InlineData(64, false, 62, true)]
    [InlineData(64, true, 45, false)]
    [InlineData(64, true, 46, true)]
    [InlineData(64, true, 61, true)]
    public void TryParsePci_Escape_Single_Frame_Must_Need_Its_CAN_DL(int canDl, bool extended, int sfDl, bool valid)
    {
        var ep = extended
            ? IsoTpEndpoint.Extended(0x1, 0x2, sourceAddress: 0x10, targetAddress: 0x20)
            : IsoTpEndpoint.Normal(0x1, 0x2);
        var frame = new byte[canDl];
        int pci = extended ? 1 : 0;
        if (extended) frame[0] = 0x10;
        frame[pci] = 0x00;
        frame[pci + 1] = (byte)sfDl;

        bool parsed = IsoTpFrameCodec.TryParsePci(frame, ep, isCanFd: true, out var result);

        parsed.Should().Be(valid);
        if (valid)
        {
            result.Type.Should().Be(PciType.SingleFrame);
            result.Length.Should().Be(sfDl);
        }
    }

    // Every Single Frame the codec builds stays within those bounds, so a peer applying them
    // takes all of them.
    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void Every_Built_Fd_Single_Frame_Parses(bool extended, bool padding)
    {
        var ep = extended
            ? IsoTpEndpoint.Extended(0x1, 0x2, sourceAddress: 0x10, targetAddress: 0x20)
            : IsoTpEndpoint.Normal(0x1, 0x2);
        int max = IsoTpFrameCodec.SingleFrameMaxDataLength(isCanFd: true, extended);
        for (int len = 1; len <= max; len++)
        {
            var frame = IsoTpFrameCodec.BuildSingleFrame(ep, new byte[len], isCanFd: true, padding);
            IsoTpFrameCodec.TryParsePci(frame, ep, isCanFd: true, out var pci).Should().BeTrue($"SF_DL {len}");
            pci.Length.Should().Be(len);
        }
    }

    // On the channel: an escape Single Frame whose SF_DL would have fit a smaller frame is
    // ignored, and the next valid one is delivered.
    [Fact]
    public async Task An_Escape_Single_Frame_In_A_Too_Large_Frame_Is_Ignored()
    {
        var session = NewSession();
        using var peerBus = OpenCanFd(session, 0);
        using var receiverBus = OpenCanFd(session, 1);
        using var receiver = IsoTpFactory.Open(receiverBus, IsoTpEndpoint.Normal(0x7E8, 0x7E0), Options());

        var tooShort = new byte[16];
        tooShort[1] = 3;
        tooShort[2] = 0xDE;
        peerBus.Transmit(CanFrame.Fd(0x7E0, tooShort));
        var valid = IsoTpFrameCodec.BuildSingleFrame(IsoTpEndpoint.Normal(0x7E0, 0x7E8), new byte[] { 0x42 },
            isCanFd: true, padding: true);
        peerBus.Transmit(CanFrame.Fd(0x7E0, valid));

        using var receiveTimeout = new CancellationTokenSource(ShortTimeout);
        (await receiver.ReceiveAsync(receiveTimeout.Token)).Should().Equal(0x42);
    }
}
