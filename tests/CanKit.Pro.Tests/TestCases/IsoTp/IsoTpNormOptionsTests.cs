using System;
using System.Collections.Generic;
using System.Linq;
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
/// The configurable TX_DL (ISO 15765-2:2024 §9.5.1) and <see cref="IsoTpChannelOptions.StrictReceive"/>
/// (§9.6.2.2, §11.3.2.1).
/// </summary>
public class IsoTpNormOptionsTests : IClassFixture<VirtualAdapterFixture>
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(30);

    private static string NewSession() => $"isotp-norm-{Guid.NewGuid():N}";

    private static ICanBus OpenClassic(string session, int channel) => CanBus.Open(
        $"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(VirtualAdapterFixture.Bitrate));

    private static ICanBus OpenCanFd(string session, int channel) => CanBus.Open(
        $"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.CanFd).Fd(VirtualAdapterFixture.Bitrate, VirtualAdapterFixture.DataBitrate));

    private static IsoTpChannelOptions Options(bool useCanFd, bool usePadding = true, int txDl = 64,
        bool strictReceive = false)
        => new()
        {
            UseCanFd = useCanFd,
            UsePadding = usePadding,
            TxDl = txDl,
            StrictReceive = strictReceive,
            NAs = TimeSpan.FromMilliseconds(500),
            NBs = TimeSpan.FromMilliseconds(500),
            NCr = TimeSpan.FromMilliseconds(500),
        };

    // ---------------------------------------------------------------------------------------
    // TX_DL
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void TxDl_Defaults_To_64_And_StrictReceive_To_False()
    {
        var options = new IsoTpChannelOptions();
        options.TxDl.Should().Be(64);
        options.StrictReceive.Should().BeFalse();
    }

    // Every frame the sender builds is at most TX_DL long, the First Frame exactly TX_DL; the
    // PDU arrives whole at a receiver with the default options, which adapts to the TX_DL used.
    // 9 bytes at TX_DL 12: an escape SF in a 12-byte frame. 11 bytes at TX_DL 12: one byte more
    // than an SF takes there, so FF + CF. 4200 bytes: the escape First Frame (6-byte PCI).
    [Theory]
    [InlineData(8, 7, false)]
    [InlineData(8, 100, false)]
    [InlineData(8, 4200, false)]
    [InlineData(12, 9, false)]
    [InlineData(12, 11, false)]
    [InlineData(20, 300, false)]
    [InlineData(32, 200, true)]
    [InlineData(48, 4200, false)]
    [InlineData(64, 200, false)]
    public async Task Fd_Sender_Holds_Every_Frame_To_TxDl(int txDl, int pduLength, bool extended)
    {
        var session = NewSession();
        using var busA = OpenCanFd(session, 0);
        using var busB = OpenCanFd(session, 1);
        using var snifferBus = OpenCanFd(session, 2);

        var senderEp = extended
            ? IsoTpEndpoint.Extended(0x7E0, 0x7E8, sourceAddress: 0x10, targetAddress: 0x20)
            : IsoTpEndpoint.Normal(0x7E0, 0x7E8);
        var receiverEp = extended
            ? IsoTpEndpoint.Extended(0x7E8, 0x7E0, sourceAddress: 0x20, targetAddress: 0x10)
            : IsoTpEndpoint.Normal(0x7E8, 0x7E0);
        using var sender = IsoTpFactory.Open(busA, senderEp, Options(useCanFd: true, txDl: txDl));
        using var receiver = IsoTpFactory.Open(busB, receiverEp, Options(useCanFd: true));

        int ext = extended ? 1 : 0;
        int sfMax = (txDl == 8 ? 7 : txDl - 2) - ext;
        int ffData = txDl - (pduLength > 4095 ? 6 : 2) - ext;
        int cfData = txDl - 1 - ext;
        int expectedFrames = pduLength <= sfMax ? 1 : 1 + (pduLength - ffData + cfData - 1) / cfData;

        var lengths = new List<int>();
        var sniffedAll = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        snifferBus.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != 0x7E0) return;
            lock (lengths)
            {
                lengths.Add(e.CanFrame.Data.Length);
                if (lengths.Count >= expectedFrames) sniffedAll.TrySetResult(true);
            }
        };

        var pdu = Enumerable.Range(0, pduLength).Select(i => (byte)(i * 7)).ToArray();
        using var receiveTimeout = new CancellationTokenSource(LongTimeout);
        var recvTask = receiver.ReceiveAsync(receiveTimeout.Token);
        await sender.SendAsync(pdu).WaitAsync(LongTimeout);
        (await recvTask).Should().Equal(pdu);

        await sniffedAll.Task.WaitAsync(ShortTimeout);
        lock (lengths)
        {
            lengths.Should().HaveCount(expectedFrames);
            lengths.Should().OnlyContain(l => l <= txDl, "no frame may exceed TX_DL");
            if (expectedFrames > 1)
                lengths[0].Should().Be(txDl, "a First Frame fills TX_DL");
        }
    }

    [Fact]
    public async Task TxDl_Is_Ignored_On_A_Classic_Channel()
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);
        using var sender = IsoTpFactory.Open(busA, IsoTpEndpoint.Normal(0x7E0, 0x7E8), Options(useCanFd: false, txDl: 32));
        using var receiver = IsoTpFactory.Open(busB, IsoTpEndpoint.Normal(0x7E8, 0x7E0), Options(useCanFd: false));

        var pdu = Enumerable.Range(0, 40).Select(i => (byte)i).ToArray();
        using var receiveTimeout = new CancellationTokenSource(ShortTimeout);
        var recvTask = receiver.ReceiveAsync(receiveTimeout.Token);
        await sender.SendAsync(pdu).WaitAsync(ShortTimeout);
        (await recvTask).Should().Equal(pdu);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    [InlineData(48)]
    [InlineData(64)]
    public void Every_Permitted_TxDl_Opens_A_Channel(int txDl)
    {
        var session = NewSession();
        using var bus = OpenCanFd(session, 0);
        using var channel = IsoTpFactory.Open(bus, IsoTpEndpoint.Normal(0x7E0, 0x7E8), Options(useCanFd: true, txDl: txDl));
        channel.Options.TxDl.Should().Be(txDl);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(true, 7)]
    [InlineData(true, 9)]
    [InlineData(true, 63)]
    [InlineData(true, 65)]
    [InlineData(false, -1)]
    [InlineData(false, 10)]
    public void An_Invalid_TxDl_Fails_Channel_Creation(bool useCanFd, int txDl)
    {
        var session = NewSession();
        using var bus = useCanFd ? OpenCanFd(session, 0) : OpenClassic(session, 0);

        Action act = () => IsoTpFactory.Open(bus, IsoTpEndpoint.Normal(0x7E0, 0x7E8),
            Options(useCanFd, txDl: txDl)).Dispose();

        act.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be(nameof(IsoTpChannelOptions.TxDl));
    }

    // ---------------------------------------------------------------------------------------
    // StrictReceive
    // ---------------------------------------------------------------------------------------

    // A 3-byte unpadded Single Frame (CAN_DL 4), then a padded one. With StrictReceive on a
    // padded link the first is ignored and the second is what arrives; otherwise the first is
    // taken as before.
    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, true)]
    public async Task StrictReceive_Ignores_An_Unpadded_Single_Frame_On_A_Padded_Link(
        bool strictReceive, bool usePadding, bool unpaddedIsDelivered)
    {
        var session = NewSession();
        using var peerBus = OpenClassic(session, 0);
        using var receiverBus = OpenClassic(session, 1);
        var peer = IsoTpEndpoint.Normal(0x7E0, 0x7E8);
        using var receiver = IsoTpFactory.Open(receiverBus, IsoTpEndpoint.Normal(0x7E8, 0x7E0),
            Options(useCanFd: false, usePadding: usePadding, strictReceive: strictReceive));

        var unpadded = IsoTpFrameCodec.BuildSingleFrame(peer, new byte[] { 0x11, 0x22, 0x33 },
            isCanFd: false, padding: false);
        unpadded.Should().HaveCount(4);
        var padded = IsoTpFrameCodec.BuildSingleFrame(peer, new byte[] { 0x44 },
            isCanFd: false, padding: true);

        using var receiveTimeout = new CancellationTokenSource(ShortTimeout);
        peerBus.Transmit(CanFrame.Classic(0x7E0, unpadded));
        peerBus.Transmit(CanFrame.Classic(0x7E0, padded));

        var first = await receiver.ReceiveAsync(receiveTimeout.Token);
        if (unpaddedIsDelivered)
        {
            first.Should().Equal(0x11, 0x22, 0x33);
            (await receiver.ReceiveAsync(receiveTimeout.Token)).Should().Equal(0x44);
        }
        else
        {
            first.Should().Equal(new byte[] { 0x44 }, "the unpadded Single Frame is ignored");
        }
    }

    // The ignored frame is ignored outright: it does not abort a reception in progress, which a
    // Single Frame taken as such would (CTL_UNEXP_PDU).
    [Fact]
    public async Task StrictReceive_An_Ignored_Single_Frame_Leaves_A_Reception_Running()
    {
        var session = NewSession();
        using var peerBus = OpenClassic(session, 0);
        using var receiverBus = OpenClassic(session, 1);
        var peer = IsoTpEndpoint.Normal(0x7E0, 0x7E8);
        using var receiver = IsoTpFactory.Open(receiverBus, IsoTpEndpoint.Normal(0x7E8, 0x7E0),
            Options(useCanFd: false, strictReceive: true));
        var faults = new List<Exception>();
        receiver.BackgroundExceptionOccurred += (_, ex) => { lock (faults) faults.Add(ex); };

        var fcSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        peerBus.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID == 0x7E8 && e.CanFrame.Data.Length > 0 && (e.CanFrame.Data.Span[0] >> 4) == 0x3)
                fcSeen.TrySetResult(true);
        };

        var pdu = Enumerable.Range(0, 10).Select(i => (byte)(0x80 + i)).ToArray();
        using var receiveTimeout = new CancellationTokenSource(ShortTimeout);
        var recvTask = receiver.ReceiveAsync(receiveTimeout.Token);
        peerBus.Transmit(CanFrame.Classic(0x7E0, IsoTpFrameCodec.BuildFirstFrame(peer, pdu.Length, pdu.AsSpan(0, 6), isCanFd: false)));
        await fcSeen.Task.WaitAsync(ShortTimeout);

        peerBus.Transmit(CanFrame.Classic(0x7E0, IsoTpFrameCodec.BuildSingleFrame(peer, new byte[] { 0x01 },
            isCanFd: false, padding: false)));
        peerBus.Transmit(CanFrame.Classic(0x7E0, IsoTpFrameCodec.BuildConsecutiveFrame(peer, 1, pdu.AsSpan(6),
            isCanFd: false, padding: true)));

        (await recvTask).Should().Equal(pdu);
        lock (faults) faults.Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------------------
    // With()
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void With_Overrides_TxDl_And_StrictReceive_And_The_Older_Overloads_Keep_Them()
    {
        var template = new IsoTpChannelOptions { TxDl = 16, StrictReceive = true, NCr = TimeSpan.FromMilliseconds(90) };

        var changed = template.With(txDl: 24, strictReceive: false);
        changed.TxDl.Should().Be(24);
        changed.StrictReceive.Should().BeFalse();
        changed.NCr.Should().Be(TimeSpan.FromMilliseconds(90));

        // Twelve arguments: the overload from before TxDl and StrictReceive.
        var twelve = template.With(null, null, null, null, null, null, null, TimeSpan.FromMilliseconds(30), null, null, null,
            TimeSpan.FromMilliseconds(40));
        twelve.TxDl.Should().Be(16);
        twelve.StrictReceive.Should().BeTrue();
        twelve.NCr.Should().Be(TimeSpan.FromMilliseconds(30));
        twelve.NAr.Should().Be(TimeSpan.FromMilliseconds(40));

        // Eleven arguments: the overload from before NAr.
        var eleven = template.With(null, null, null, null, null, null, null, null, null, null, null);
        eleven.TxDl.Should().Be(16);
        eleven.StrictReceive.Should().BeTrue();
    }
}
