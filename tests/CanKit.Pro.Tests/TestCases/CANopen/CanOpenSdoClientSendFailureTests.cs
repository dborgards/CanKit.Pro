using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

/// <summary>
/// A client SDO request the bus does not send fails its transfer at once with
/// <see cref="CanOpenTransportException"/>, rather than by the SDO timeout (#197, FR-CO-034). The
/// failure is still raised on <see cref="ICanOpenNode.BackgroundExceptionOccurred"/> as well.
/// </summary>
/// <remarks>
/// The SDO timeout is set far past the test's own bound, so a transfer that is left to time out
/// fails the test instead of passing it late.
/// </remarks>
public class CanOpenSdoClientSendFailureTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);

    public static TheoryData<string> Transfers => new() { "upload", "download", "block upload" };

    [Theory]
    [MemberData(nameof(Transfers))]
    public async Task A_Rejected_Request_Fails_The_Transfer_With_The_Transport_Error(string transfer)
    {
        using var bus = ControllableBus.EchoCapable($"canopen-sdo-send-fail-{Guid.NewGuid():N}");
        using var client = CanOpen.OpenNode(bus, 0x7F, new CanOpenNodeOptions { SdoTimeout = TimeSpan.FromSeconds(30) });
        var reported = new ConcurrentQueue<Exception>();
        client.BackgroundExceptionOccurred += (_, ex) => reported.Enqueue(ex);
        bus.AcceptTransmit = false;

        Func<Task> act = transfer switch
        {
            "upload" => () => client.SdoUploadAsync(0x11, 0x1000, 0x00),
            "download" => () => client.SdoDownloadAsync(0x11, 0x1000, 0x00, new byte[] { 1, 2, 3, 4 }),
            _ => () => client.SdoUploadAsync(0x11, 0x1000, 0x00, SdoTransferMode.Block),
        };

        await FluentActions.Awaiting(() => act().WithTimeoutAsync(ShortTimeout))
            .Should().ThrowAsync<CanOpenTransportException>();
        reported.Should().Contain(ex => ex is CanOpenTransportException);
    }

    public static TheoryData<string> Uploads => new() { "upload", "block upload" };

    [Theory]
    [MemberData(nameof(Uploads))]
    public async Task A_Confirmation_That_Fails_After_The_Sdo_Timeout_Still_Ends_The_Transfer(string transfer)
    {
        // The confirmation window (CanBusService.DefaultConfirmTimeout) is longer than this SDO
        // timeout, and the echo is never delivered: the timeout elapses first, then the send
        // fails. Nothing reached the server, so the transfer must end with the send failure.
        using var bus = ControllableBus.DeferredEchoCapable($"canopen-sdo-late-fail-{Guid.NewGuid():N}");
        using var client = CanOpen.OpenNode(bus, 0x7F, new CanOpenNodeOptions { SdoTimeout = TimeSpan.FromMilliseconds(50) });
        await bus.DeferredEchoes.WaitForEnqueuedAsync(1, ShortTimeout); // the boot-up
        bus.DeferredEchoes.ReleaseAll();

        Func<Task> act = transfer == "upload"
            ? () => client.SdoUploadAsync(0x11, 0x1000, 0x00)
            : () => client.SdoUploadAsync(0x11, 0x1000, 0x00, SdoTransferMode.Block);

        await FluentActions.Awaiting(() => act().WithTimeoutAsync(ShortTimeout))
            .Should().ThrowAsync<CanOpenTransportException>();
    }

    [Fact]
    public async Task A_Confirmation_That_Arrives_After_The_Sdo_Timeout_Leaves_The_Timeout_Standing()
    {
        using var bus = ControllableBus.DeferredEchoCapable($"canopen-sdo-late-ok-{Guid.NewGuid():N}");
        using var client = CanOpen.OpenNode(bus, 0x7F, new CanOpenNodeOptions { SdoTimeout = TimeSpan.FromMilliseconds(50) });
        await bus.DeferredEchoes.WaitForEnqueuedAsync(1, ShortTimeout); // the boot-up
        bus.DeferredEchoes.ReleaseAll();

        var upload = client.SdoUploadAsync(0x11, 0x1000, 0x00);
        await bus.DeferredEchoes.WaitForEnqueuedAsync(2, ShortTimeout);
        // Whether or not the SDO timeout has elapsed by now, a confirmed request that nobody
        // answers ends in the timeout: the send outcome only overrides it when the send failed.
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        bus.DeferredEchoes.ReleaseAll();

        var abort = (await FluentActions.Awaiting(() => upload.WithTimeoutAsync(ShortTimeout))
            .Should().ThrowAsync<SdoAbortException>()).Which;
        abort.AbortCode.Should().Be((uint)SdoAbortCode.SdoProtocolTimedOut);
        abort.Origin.Should().Be(SdoAbortOrigin.Local);
    }

    [Fact]
    public async Task A_Rejected_Block_Segment_Stops_The_Sub_Block()
    {
        using var bus = ControllableBus.EchoCapable($"canopen-sdo-block-seg-fail-{Guid.NewGuid():N}");
        using var client = CanOpen.OpenNode(bus, 0x7F, new CanOpenNodeOptions { SdoTimeout = TimeSpan.FromSeconds(30) });
        var attemptsBeforeSegments = -1;
        bus.OnTransmitting = frame =>
        {
            if ((uint)frame.ID != CanOpenCobId.SdoRx(0x11)
                || (frame.Data.Span[0] & 0xE1) != SdoBlockFrames.CcsBlockDownloadInitBase)
            {
                return;
            }

            // The server accepts the block download; from here on every send is rejected, so
            // the first segment fails and none after it may be attempted.
            _ = Task.Run(() =>
            {
                bus.AcceptTransmit = false;
                Volatile.Write(ref attemptsBeforeSegments, bus.TransmitCount);
                bus.RaiseObserved(CanFrame.Classic(unchecked((int)CanOpenCobId.SdoTx(0x11)),
                    SdoBlockFrames.BuildBlockDownloadInitResponse(0x1000, 0x00, serverCrcSupported: false, blockSize: 127)),
                    isEcho: false);
            });
        };

        // 20 bytes are three segments in one sub-block.
        var payload = new byte[20];
        await FluentActions.Awaiting(() => client.SdoDownloadAsync(0x11, 0x1000, 0x00, payload, SdoTransferMode.Block)
                .WithTimeoutAsync(ShortTimeout))
            .Should().ThrowAsync<CanOpenTransportException>();

        // No event marks "no further segment": give a sender that kept going the time to show it.
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        (bus.TransmitCount - Volatile.Read(ref attemptsBeforeSegments)).Should().Be(1,
            "the send loop stops at the first segment the bus rejects");
    }
}
