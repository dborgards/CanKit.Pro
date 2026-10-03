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
using CanKit.Pro.CANopen.Sdo;
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
        using var resources = new DisposeBag();
        var elapsed = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transferEnded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ICanOpenNode? node = null;
        Task? openTransfer = null;
        void DisposeNode()
        {
            var watch = Stopwatch.StartNew();
            if (call == "Dispose") node!.Dispose();
            else node!.DisposeAsync().AsTask().Wait();
            // What a caller may rely on when the call returns: its open transfers have ended. From
            // the actor this needs the cleanup to run in place, a post waits for this very handler.
            transferEnded.TrySetResult(openTransfer?.IsCompleted ?? true);
            elapsed.TrySetResult(watch.Elapsed);
        }

        if (context == "a reader failure report")
        {
            var starved = new StarvedReaderBusService { FramesFault = new InvalidOperationException("the demux broke") };
            node = resources.Add(new CanOpenNode(starved, 0x01, new CanOpenNodeOptions(), ownsService: false, new ManualTimeSource()));
            node.BackgroundExceptionOccurred += (_, _) => DisposeNode();
            starved.WakeReader();
        }
        else
        {
            var session = NewSession();
            var busA = resources.Add(Open(session, 1));
            var rawBus = resources.Add(Open(session, 2));
            node = resources.Add(CanOpen.OpenNode(busA, nodeId: 0x01));
            if (context == "ApplicationReset")
            {
                PeerSdoLaboratory.Bind(node, 0x02);
                openTransfer = node.SdoUploadAsync(0x02, 0x2100, 0x00); // nobody answers
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
        (await transferEnded.Task.WithTimeoutAsync(ShortTimeout)).Should().BeTrue("the open transfer ended before the call returned");
    }

    // A second DisposeAsync that arrives while the first is still waiting for the reader returns
    // when the disposal has finished, not at once: the owned service is released by then.
    [Fact]
    public async Task A_Concurrent_DisposeAsync_Returns_When_The_Disposal_Has_Finished()
    {
        var starved = new StarvedReaderBusService();
        var node = new CanOpenNode(starved, 0x01, new CanOpenNodeOptions(), ownsService: true, new ManualTimeSource());

        var first = node.DisposeAsync();
        var second = node.DisposeAsync();
        await second.AsTask().WithTimeoutAsync(ShortTimeout);

        starved.IsDisposed.Should().BeTrue();
        await first.AsTask().WithTimeoutAsync(ShortTimeout);
    }

    // An outside caller wins the disposal while an ApplicationReset subscriber is running on the
    // actor; the winner's cleanup is queued behind that subscriber. The subscriber then disposes
    // too, loses, and must still leave the open transfer ended when its call returns.
    [Fact]
    public async Task A_Subscriber_On_The_Actor_That_Loses_The_Disposal_Race_Still_Ends_The_Open_Transfers()
    {
        using var resources = new DisposeBag();
        var session = NewSession();
        var busA = resources.Add(Open(session, 1));
        var rawBus = resources.Add(Open(session, 2));
        var node = CanOpen.OpenNode(busA, nodeId: 0x01);
        PeerSdoLaboratory.Bind(node, 0x02);
        var openTransfer = node.SdoUploadAsync(0x02, 0x2100, 0x00); // nobody answers
        var inHandler = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var outsideHasWon = new ManualResetEventSlim();
        node.ApplicationReset += (_, _) =>
        {
            inHandler.TrySetResult(true);
            outsideHasWon.Wait(ShortTimeout);
            node.DisposeAsync().AsTask().Wait();
            ended.TrySetResult(openTransfer.IsCompleted);
        };
        rawBus.Transmit(CanFrame.Classic(unchecked((int)CanOpenCobId.NmtCommand),
            new byte[] { (byte)NmtCommand.ResetNode, 0x01 }, isExtendedFrame: false));
        await inHandler.Task.WithTimeoutAsync(ShortTimeout);

        var outside = Task.Run(node.Dispose); // wins; the actor is busy, so its cleanup waits
        var deadline = DateTime.UtcNow + ShortTimeout;
        while (true)
        {
            try { _ = node.SendNmtCommandAsync(NmtCommand.Start, 0x7F); }
            catch (ObjectDisposedException) { break; } // the outside call has begun
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The outside Dispose never began.");
            await Task.Delay(5);
        }

        outsideHasWon.Set();

        (await ended.Task.WithTimeoutAsync(ShortTimeout)).Should().BeTrue("the subscriber's call ended the transfer");
        await outside.WithTimeoutAsync(ShortTimeout);
    }

    // The reset goes on after its subscribers: it announces the node with a boot-up. A subscriber
    // that disposed the node ends it there, as a node that is gone announces nothing. The service
    // is the caller's, so a boot-up that was wrongly sent would reach the bus. (With the guard
    // removed this test still passes: nothing observable happens past the subscriber today, so it
    // pins the behaviour and covers the line, and proves no mutation.)
    [Fact]
    public async Task A_Reset_Does_Not_Announce_A_Node_That_Its_Subscriber_Disposed()
    {
        using var resources = new DisposeBag();
        var session = NewSession();
        var busA = resources.Add(Open(session, 1));
        var rawBus = resources.Add(Open(session, 2));
        var bootups = 0;
        rawBus.FrameObserved += (_, e) =>
        {
            if ((uint)e.CanFrame.ID == CanOpenCobId.HeartbeatBase + 0x01) Interlocked.Increment(ref bootups);
        };
        var service = resources.Add(new CanBusService(busA));
        var node = CanOpen.OpenNode(service, nodeId: 0x01, leaveOpen: true);
        var disposed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reports = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        node.BackgroundExceptionOccurred += (_, ex) => reports.Enqueue(ex);
        node.ApplicationReset += (_, _) =>
        {
            node.Dispose();
            disposed.TrySetResult(true);
        };
        var atOpen = SpinWait.SpinUntil(() => Volatile.Read(ref bootups) >= 1, ShortTimeout);
        atOpen.Should().BeTrue("the node announces itself when it opens");

        rawBus.Transmit(CanFrame.Classic(unchecked((int)CanOpenCobId.NmtCommand),
            new byte[] { (byte)NmtCommand.ResetNode, 0x01 }, isExtendedFrame: false));
        await disposed.Task.WithTimeoutAsync(ShortTimeout);

        // No signal says "nothing more is coming", so this is a negative window: it can only pass
        // falsely on a slow host, never fail falsely.
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Volatile.Read(ref bootups).Should().Be(1, "only the boot-up from opening; the reset's was not sent");
        reports.Should().BeEmpty("the reset stopped at the subscriber instead of arming timers on a disposed node");
    }

    // Subscribers of ApplicationReset are called one after the other; one that disposes the node
    // ends the round.
    [Fact]
    public async Task A_Reset_Does_Not_Call_The_Next_Subscriber_After_One_Disposed_The_Node()
    {
        using var resources = new DisposeBag();
        var session = NewSession();
        var busA = resources.Add(Open(session, 1));
        var rawBus = resources.Add(Open(session, 2));
        var node = CanOpen.OpenNode(busA, nodeId: 0x01);
        var first = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCalled = 0;
        node.ApplicationReset += (_, _) =>
        {
            node.Dispose();
            first.TrySetResult(true);
        };
        node.ApplicationReset += (_, _) => Interlocked.Increment(ref secondCalled);

        rawBus.Transmit(CanFrame.Classic(unchecked((int)CanOpenCobId.NmtCommand),
            new byte[] { (byte)NmtCommand.ResetNode, 0x01 }, isExtendedFrame: false));
        await first.Task.WithTimeoutAsync(ShortTimeout);

        // A negative window: it can only pass falsely on a slow host, never fail falsely.
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Volatile.Read(ref secondCalled).Should().Be(0);
    }

    // Events still queued when a subscriber disposes the node are dropped, not delivered after
    // the call has returned. The first subscriber holds the pump until the second heartbeat is
    // certainly queued behind it.
    [Fact]
    public async Task Events_Queued_Behind_A_Subscriber_That_Disposes_The_Node_Are_Not_Delivered()
    {
        using var resources = new DisposeBag();
        var session = NewSession();
        var busA = resources.Add(Open(session, 1));
        var rawBus = resources.Add(Open(session, 2));
        var node = CanOpen.OpenNode(busA, nodeId: 0x01);
        var delivered = 0;
        var disposed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        node.HeartbeatReceived += (_, _) =>
        {
            if (Interlocked.Increment(ref delivered) == 1)
            {
                Thread.Sleep(300); // the second frame is queued behind this event by now
                node.Dispose();
                disposed.TrySetResult(true);
            }
        };

        SendHeartbeat(rawBus, 0x11, 0x05);
        SendHeartbeat(rawBus, 0x12, 0x05);
        await disposed.Task.WithTimeoutAsync(ShortTimeout);

        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Volatile.Read(ref delivered).Should().Be(1, "the second heartbeat was queued but the node was disposed");
    }

    // The service's Dispose throws: the first call faults, and a second call that was waiting for
    // that disposal is released instead of waiting for ever.
    [Fact]
    public async Task A_Disposal_That_Throws_Still_Releases_A_Concurrent_DisposeAsync()
    {
        var starved = new StarvedReaderBusService { DisposeFault = new InvalidOperationException("the service would not dispose") };
        var node = new CanOpenNode(starved, 0x01, new CanOpenNodeOptions(), ownsService: true, new ManualTimeSource());

        var first = node.DisposeAsync();
        var second = node.DisposeAsync();

        await second.AsTask().WithTimeoutAsync(ShortTimeout);
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.AsTask().WithTimeoutAsync(ShortTimeout));
    }

    // Everything a node can have open when it is disposed: a segmented and a block download as a
    // server, a classic and a block upload as a client, and node-guarding consumers with and
    // without a reply. Each is torn down, and the client's transfers end with ObjectDisposedException.
    [Fact]
    public async Task Disposing_A_Node_With_Open_Sessions_And_Consumers_Ends_Them()
    {
        using var resources = new DisposeBag();
        var session = NewSession();
        var rawBus = resources.Add(Open(session, 5));
        var reports = new System.Collections.Concurrent.ConcurrentQueue<Exception>();

        var busOfClassicServer = resources.Add(Open(session, 1));
        using var classicServer = CanOpen.OpenNode(busOfClassicServer, nodeId: 0x01);
        var busOfBlockServer = resources.Add(Open(session, 2));
        using var blockServer = CanOpen.OpenNode(busOfBlockServer, nodeId: 0x02);
        var busOfClient = resources.Add(Open(session, 3));
        using var client = CanOpen.OpenNode(busOfClient, nodeId: 0x03);
        foreach (var node in new[] { classicServer, blockServer, client })
            node.BackgroundExceptionOccurred += (_, ex) => reports.Enqueue(ex);
        classicServer.ObjectDictionary.AddDomain(0x2100, 0x00, new byte[4]);
        blockServer.ObjectDictionary.AddDomain(0x2100, 0x00, new byte[4]);

        var classicTap = new FrameTap(rawBus, CanOpenCobId.SdoTx(0x01));
        var blockTap = new FrameTap(rawBus, CanOpenCobId.SdoTx(0x02));
        rawBus.Transmit(CanFrame.Classic(unchecked((int)CanOpenCobId.SdoRx(0x01)),
            SdoFrames.BuildDownloadInit(0x2100, 0x00, new byte[20]), isExtendedFrame: false));
        classicTap.Next(ShortTimeout);
        rawBus.Transmit(CanFrame.Classic(unchecked((int)CanOpenCobId.SdoRx(0x02)),
            SdoBlockFrames.BuildBlockDownloadInit(0x2100, 0x00, clientCrcSupported: false, sizeIndicated: true, totalSize: 20),
            isExtendedFrame: false));
        blockTap.Next(ShortTimeout);

        PeerSdoLaboratory.Bind(client, 0x11);
        PeerSdoLaboratory.Bind(client, 0x12);
        var classicUpload = client.SdoUploadAsync(0x11, 0x2100, 0x00);
        var blockUpload = client.SdoUploadAsync(0x12, 0x2100, 0x00, SdoTransferMode.Block);
        var replied = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.NodeGuardingReceived += (_, e) =>
        {
            if (e.ProducerNodeId == 0x22) replied.TrySetResult(true);
        };
        client.StartNodeGuardingConsumer(0x21, TimeSpan.FromSeconds(30), 3); // never answers
        client.StartNodeGuardingConsumer(0x22, TimeSpan.FromSeconds(30), 3);
        rawBus.Transmit(CanFrame.Classic(unchecked((int)(CanOpenCobId.HeartbeatBase + 0x22)),
            new byte[] { 0x7F }, isExtendedFrame: false));
        await replied.Task.WithTimeoutAsync(ShortTimeout); // its life time is armed now

        classicServer.Dispose();
        blockServer.Dispose();
        client.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => classicUpload.WithTimeoutAsync(ShortTimeout));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => blockUpload.WithTimeoutAsync(ShortTimeout));
        reports.Should().BeEmpty();
    }

    private sealed class DisposeBag : IDisposable
    {
        private readonly System.Collections.Generic.List<IDisposable> _items = new();

        public T Add<T>(T item) where T : IDisposable
        {
            _items.Add(item);
            return item;
        }

        public void Dispose()
        {
            for (var i = _items.Count - 1; i >= 0; i--) _items[i].Dispose();
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
    public async Task A_Node_Can_Be_Used_With_Await_Using_Through_IAsyncDisposable()
    {
        var session = NewSession();
        using var busA = Open(session, 1);
        ICanOpenNode captured;
        await using (var node = (IAsyncDisposable)CanOpen.OpenNode(busA, nodeId: 0x01))
        {
            captured = (ICanOpenNode)node;
            captured.State.Should().NotBe(default);
        }

        Assert.Throws<ObjectDisposedException>(() => _ = captured.State);
    }

#if NET5_0_OR_GREATER
    // An ICanOpenNode that is not this library's (an interface is public, anyone may implement it)
    // has no DisposeAsync of its own; the extension disposes it on the thread pool.
    [Fact]
    public async Task DisposeAsync_On_A_Node_That_Is_Not_This_Librarys_Disposes_It_On_The_Thread_Pool()
    {
        var foreign = System.Reflection.DispatchProxy.Create<ICanOpenNode, ForeignNode>();

        await foreign.DisposeAsync();

        ((ForeignNode)foreign).Disposed.Should().BeTrue();
    }

    private class ForeignNode : System.Reflection.DispatchProxy
    {
        public bool Disposed { get; private set; }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IDisposable.Dispose)) Disposed = true;
            return null;
        }
    }
#endif

    [Fact]
    public async Task DisposeAsync_Rejects_A_Null_Node()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await CanOpenNodeExtensions.DisposeAsync(null!));
    }

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
            if ((uint)e.CanFrame.ID == _cobId) _frames.Add(e.CanFrame.Data.ToArray());
        }

        public byte[] Next(TimeSpan timeout)
            => _frames.TryTake(out var frame, timeout)
                ? frame
                : throw new TimeoutException($"No frame on COB-ID 0x{_cobId:X3} within {timeout}.");

        public void Dispose()
        {
            _bus.FrameObserved -= OnFrame;
            _frames.Dispose();
        }
    }
}
