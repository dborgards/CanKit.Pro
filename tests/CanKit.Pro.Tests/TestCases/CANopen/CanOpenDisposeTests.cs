using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core;
using CanKit.Pro.CANopen;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

/// <summary>
/// Disposing a node from one of its own event handlers (#251). The handler runs on the event pump,
/// and a blocking Dispose used to wait for the pump to finish, that is for itself, until its join
/// timeout ran out.
/// </summary>
public class CanOpenDisposeTests : IClassFixture<VirtualAdapterFixture>
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);

    // Dispose's join timeout is two seconds. A call that waited for itself takes all of it; one
    // that did not takes milliseconds. This bound sits between the two with room on both sides.
    private static readonly TimeSpan NoStall = TimeSpan.FromSeconds(1.5);

    private static string NewSession() => $"canopen-dispose-{Guid.NewGuid():N}";

    private static ICanBus Open(string session, int channel) => CanBus.Open(
        $"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(VirtualAdapterFixture.Bitrate));

    private static void SendHeartbeat(ICanBus bus, byte producer, byte state)
        => bus.Transmit(CanFrame.Classic(unchecked((int)(CanOpenCobId.HeartbeatBase + producer)),
            new[] { state }, isExtendedFrame: false));

    [Fact]
    public async Task Dispose_From_An_Event_Handler_Does_Not_Wait_For_The_Pump_It_Runs_On()
    {
        var session = NewSession();
        using var busA = Open(session, 1);
        using var rawBus = Open(session, 2);
        var node = CanOpen.OpenNode(busA, nodeId: 0x01);
        var elapsed = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        node.HeartbeatReceived += (_, _) =>
        {
            var watch = Stopwatch.StartNew();
            node.Dispose();
            elapsed.TrySetResult(watch.Elapsed);
        };

        SendHeartbeat(rawBus, 0x11, 0x05);

        (await elapsed.Task.WithTimeoutAsync(ShortTimeout)).Should().BeLessThan(NoStall);
    }

    // Whether the continuation after DisposeAsync's first await changes thread depends on whether
    // the reader task had finished by then, so one run does not always reach the case that matters:
    // the pump's own thread has to be recognised before that await. Repeated, a mistake in that
    // shows up; a correct implementation passes every time.
    [Fact]
    public async Task DisposeAsync_From_An_Event_Handler_Does_Not_Wait_For_The_Pump_It_Runs_On()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var session = NewSession();
            using var busA = Open(session, 1);
            using var rawBus = Open(session, 2);
            var node = CanOpen.OpenNode(busA, nodeId: 0x01);
            var elapsed = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
            node.HeartbeatReceived += (_, _) =>
            {
                var watch = Stopwatch.StartNew();
                node.DisposeAsync().AsTask().Wait();
                elapsed.TrySetResult(watch.Elapsed);
            };

            SendHeartbeat(rawBus, 0x11, 0x05);

            (await elapsed.Task.WithTimeoutAsync(ShortTimeout)).Should().BeLessThan(NoStall);
        }
    }

    [Fact]
    public async Task DisposeAsync_Completes_Open_Transfers_And_Is_Idempotent()
    {
        var session = NewSession();
        using var busA = Open(session, 1);
        var node = CanOpen.OpenNode(busA, nodeId: 0x01);
        PeerSdoLaboratory.Bind(node, 0x02);
        var upload = node.SdoUploadAsync(0x02, 0x2100, 0x00); // nobody answers

        await node.DisposeAsync();
        await node.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => upload.WithTimeoutAsync(ShortTimeout));
        Assert.Throws<ObjectDisposedException>(() => _ = node.State);
    }

    [Fact]
    public async Task A_Node_Can_Be_Used_With_Await_Using()
    {
        var session = NewSession();
        using var busA = Open(session, 1);
        ICanOpenNode captured;
        await using (var node = CanOpen.OpenNode(busA, nodeId: 0x01))
        {
            captured = node;
            node.State.Should().NotBe(default);
        }

        Assert.Throws<ObjectDisposedException>(() => _ = captured.State);
    }
}
