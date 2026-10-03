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
using CanKit.Pro.CANopen.Nmt;
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

    public static TheoryData<string, string> Contexts => new()
    {
        { "Dispose", "an event handler" },
        { "DisposeAsync", "an event handler" },
        { "Dispose", "ApplicationReset" },
        { "DisposeAsync", "ApplicationReset" },
        { "Dispose", "a reader failure report" },
        { "DisposeAsync", "a reader failure report" },
    };

    // A subscriber disposes the node from each of the three threads that call subscribers: the
    // event pump (an ordinary event), the actor (ApplicationReset is raised on it) and the reader
    // task (it reports a failed subscription). The subscriber blocks on the call, as a handler that
    // is not async has to, so a call that waits for the thread it is on takes its whole join
    // timeout (two seconds; the actor's is five). This bound sits between that and milliseconds.
    [Theory]
    [MemberData(nameof(Contexts))]
    public async Task A_Node_Can_Be_Disposed_From_Its_Own_Subscriber_Without_Waiting_For_Itself(string call, string context)
    {
        var elapsed = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        ICanOpenNode? node = null;
        void DisposeNode()
        {
            var watch = Stopwatch.StartNew();
            if (call == "Dispose") node!.Dispose();
            else node!.DisposeAsync().AsTask().Wait();
            elapsed.TrySetResult(watch.Elapsed);
        }

        ICanBus? busA = null, rawBus = null;
        StarvedReaderBusService? starved = null;
        try
        {
            if (context == "a reader failure report")
            {
                starved = new StarvedReaderBusService { FramesFault = new InvalidOperationException("the demux broke") };
                node = new CanOpenNode(starved, 0x01, new CanOpenNodeOptions(), ownsService: false, new ManualTimeSource());
                node.BackgroundExceptionOccurred += (_, _) => DisposeNode();
                starved.WakeReader();
            }
            else
            {
                var session = NewSession();
                busA = Open(session, 1);
                rawBus = Open(session, 2);
                node = CanOpen.OpenNode(busA, nodeId: 0x01);
                if (context == "ApplicationReset")
                {
                    node.ApplicationReset += (_, _) => DisposeNode();
                    rawBus.Transmit(CanFrame.Classic(unchecked((int)CanOpenCobId.NmtCommand),
                        new byte[] { (byte)NmtCommand.ResetNode, 0x01 }, isExtendedFrame: false));
                }
                else
                {
                    node.HeartbeatReceived += (_, _) => DisposeNode();
                    SendHeartbeat(rawBus, 0x11, 0x05);
                }
            }

            (await elapsed.Task.WithTimeoutAsync(ShortTimeout)).Should().BeLessThan(NoStall);
        }
        finally
        {
            node?.Dispose();
            busA?.Dispose();
            rawBus?.Dispose();
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
