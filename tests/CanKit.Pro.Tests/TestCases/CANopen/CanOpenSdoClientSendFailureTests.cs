using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
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
}
