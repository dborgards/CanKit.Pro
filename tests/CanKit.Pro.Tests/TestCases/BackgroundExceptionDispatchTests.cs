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
using CanKit.Pro.Actor;
using CanKit.Pro.Addressing;
using CanKit.Pro.CANopen;
using CanKit.Pro.IsoTp;
using CanKit.Pro.J1939;
using CanKit.Pro.J1939Tp;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;
using IsoTpFactory = CanKit.Pro.IsoTp.IsoTp;
using J1939TpFactory = CanKit.Pro.J1939Tp.J1939Tp;

namespace CanKit.Pro.Tests.TestCases;

/// <summary>
/// <c>BackgroundExceptionOccurred</c> in every protocol layer -- J1939-TP, ISO-TP, the J1939 node,
/// the CANopen node -- is raised off the layer's actor loop, after whatever the report announces
/// is in place, and to each subscriber on its own (#356 T-2, #381).
/// </summary>
/// <remarks>
/// Every wait below is a signal; <see cref="HangGuard"/> bounds a hang and measures nothing. The
/// one negative window (<see cref="Reports_Do_Not_Overlap"/>) can only pass falsely on a slow
/// host, never fail falsely.
/// </remarks>
public class BackgroundExceptionDispatchTests : IClassFixture<VirtualAdapterFixture>
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    private static string NewSession() => $"bgex-{Guid.NewGuid():N}";

    private static ICanBus Open(string session, int channel) => CanBus.Open(
        $"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(VirtualAdapterFixture.Bitrate));

    private static async Task<T> Within<T>(Task<T> task)
    {
        if (await Task.WhenAny(task, Task.Delay(HangGuard)).ConfigureAwait(false) != task)
            throw new TimeoutException($"Nothing arrived within {HangGuard}: the actor is stuck, or the report was lost.");
        return await task.ConfigureAwait(false);
    }

    private static TaskCompletionSource<T> NewSignal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // =========================================================================================
    // The dispatcher itself
    // =========================================================================================

    [Fact]
    public async Task A_Report_Is_Delivered_Off_The_Thread_That_Made_It()
    {
        var dispatcher = new BackgroundExceptionDispatcher();
        var deliveredOn = NewSignal<int>();
        EventHandler<Exception> handler = (_, _) => deliveredOn.TrySetResult(Environment.CurrentManagedThreadId);

        dispatcher.Raise(this, handler, new InvalidOperationException("report"));

        (await Within(deliveredOn.Task)).Should().NotBe(Environment.CurrentManagedThreadId);
    }

    [Fact]
    public async Task A_Throwing_Subscriber_Does_Not_Keep_The_Report_From_The_Next()
    {
        var dispatcher = new BackgroundExceptionDispatcher();
        var second = NewSignal<Exception>();
        EventHandler<Exception> handler = (_, _) => throw new InvalidOperationException("first subscriber");
        handler += (_, ex) => second.TrySetResult(ex);
        var report = new InvalidOperationException("report");

        dispatcher.Raise(this, handler, report);

        (await Within(second.Task)).Should().BeSameAs(report);
    }

    [Fact]
    public void Nothing_Is_Queued_Without_A_Subscriber()
    {
        var dispatcher = new BackgroundExceptionDispatcher();
        dispatcher.Invoking(d => d.Raise(this, null, new InvalidOperationException("nobody listens")))
            .Should().NotThrow();
    }

    [Fact]
    public async Task A_Delivery_That_Throws_Does_Not_Strand_The_Ones_Behind_It()
    {
        var dispatcher = new BackgroundExceptionDispatcher();
        var after = NewSignal<bool>();

        dispatcher.Post(() => throw new InvalidOperationException("a layer's own delivery failed"));
        dispatcher.Post(() => after.TrySetResult(true));

        (await Within(after.Task)).Should().BeTrue();
    }

    // Reports made while one is still being delivered wait for it and keep their order.
    [Fact]
    public async Task Reports_Are_Delivered_In_The_Order_They_Were_Made()
    {
        var dispatcher = new BackgroundExceptionDispatcher();
        using var release = new ManualResetEventSlim();
        var holding = NewSignal<bool>();
        var seen = new List<int>();
        var last = NewSignal<bool>();
        const int Count = 50;

        dispatcher.Post(() =>
        {
            holding.TrySetResult(true);
            release.Wait(HangGuard);
        });
        await Within(holding.Task);
        for (int i = 0; i < Count; i++)
        {
            int n = i;
            dispatcher.Post(() =>
            {
                lock (seen) seen.Add(n);
                if (n == Count - 1) last.TrySetResult(true);
            });
        }
        release.Set();

        await Within(last.Task);
        lock (seen) seen.Should().Equal(Enumerable.Range(0, Count));
    }

    // A report made from inside a subscriber is delivered after that subscriber has returned, not
    // beside it: the reports made on an actor loop never overlapped, and a subscriber written for
    // that may rely on it. A negative window: it can only pass falsely on a slow host.
    [Fact]
    public async Task Reports_Do_Not_Overlap()
    {
        var dispatcher = new BackgroundExceptionDispatcher();
        using var secondRan = new ManualResetEventSlim();
        var overlapped = NewSignal<bool>();
        EventHandler<Exception> second = (_, _) => secondRan.Set();

        dispatcher.Post(() =>
        {
            dispatcher.Raise(this, second, new InvalidOperationException("made during the first"));
            overlapped.TrySetResult(secondRan.Wait(TimeSpan.FromMilliseconds(300)));
        });

        (await Within(overlapped.Task)).Should().BeFalse();
        secondRan.Wait(HangGuard).Should().BeTrue("it is delivered once the first has returned");
    }

    // =========================================================================================
    // J1939-TP
    // =========================================================================================

    private const byte TpPeerSa = 0x77;
    private const uint TpPgn = 0xFED3u;

    private static J1939TpOptions TpOptions() => new J1939TpOptions().With(
        t1: TimeSpan.FromSeconds(30), t2: TimeSpan.FromSeconds(30), t3: TimeSpan.FromSeconds(30));

    // A BAM whose first TP.DT carries sequence number 2: the receiver aborts the reassembly.
    private static void SendBrokenBam(ICanBus peer)
    {
        var payload = new byte[12];
        peer.Transmit(CanFrame.Classic(unchecked((int)J1939Id.ComposePgn(7, J1939Pgn.TpCm, TpPeerSa)),
            J1939TpFrames.BuildBam(payload.Length, J1939TpFrames.TotalPackets(payload.Length), TpPgn), isExtendedFrame: true));
        peer.Transmit(CanFrame.Classic(unchecked((int)J1939Id.ComposePgn(7, J1939Pgn.TpDt, TpPeerSa)),
            J1939TpFrames.BuildDt(sn: 2, payload, J1939TpFrames.DtDataBytes), isExtendedFrame: true));
    }

    private static void SendBam(ICanBus peer, byte[] payload)
    {
        int packets = J1939TpFrames.TotalPackets(payload.Length);
        peer.Transmit(CanFrame.Classic(unchecked((int)J1939Id.ComposePgn(7, J1939Pgn.TpCm, TpPeerSa)),
            J1939TpFrames.BuildBam(payload.Length, packets, TpPgn), isExtendedFrame: true));
        for (int sn = 1; sn <= packets; sn++)
        {
            peer.Transmit(CanFrame.Classic(unchecked((int)J1939Id.ComposePgn(7, J1939Pgn.TpDt, TpPeerSa)),
                J1939TpFrames.BuildDt((byte)sn, payload, (sn - 1) * J1939TpFrames.DtDataBytes), isExtendedFrame: true));
        }
    }

    // #356, T-2: the abort used to be raised on the actor, before its fault was in the inbox. A
    // handler that answered it by receiving waited for an item written only after it returned,
    // and one that waited for the next datagram held the loop that would deliver it.
    [Fact]
    public async Task J1939Tp_A_Handler_That_Receives_Gets_The_Abort_And_The_Next_Datagram()
    {
        var session = NewSession();
        using var receiverBus = Open(session, 0);
        using var peerBus = Open(session, 1);
        using var receiver = J1939TpFactory.Open(receiverBus, sourceAddress: 0x31, options: TpOptions());
        var fault = NewSignal<Exception?>();
        var next = NewSignal<J1939TpDatagram>();
        receiver.BackgroundExceptionOccurred += (_, ex) =>
        {
            if (ex is not J1939TpAbortException) return;
            try
            {
                // Synchronously, as a handler written against a blocking API would.
                fault.TrySetResult(Record.Exception(() => receiver.ReceiveAsync().GetAwaiter().GetResult()));
                next.TrySetResult(receiver.ReceiveAsync().GetAwaiter().GetResult());
            }
            catch (Exception e)
            {
                next.TrySetException(e);
            }
        };

        SendBrokenBam(peerBus);
        (await Within(fault.Task)).Should().BeOfType<J1939TpAbortException>("the fault was in the inbox before the report");

        var payload = Enumerable.Range(1, 12).Select(i => (byte)i).ToArray();
        SendBam(peerBus, payload);
        (await Within(next.Task)).Payload.Should().Equal(payload, "the actor went on while the handler waited");
    }

    [Fact]
    public async Task J1939Tp_A_Throwing_Handler_Does_Not_Hide_The_Report_From_The_Next()
    {
        var session = NewSession();
        using var receiverBus = Open(session, 0);
        using var peerBus = Open(session, 1);
        using var receiver = J1939TpFactory.Open(receiverBus, sourceAddress: 0x31, options: TpOptions());
        var second = NewSignal<Exception>();
        receiver.BackgroundExceptionOccurred += (_, _) => throw new InvalidOperationException("first handler");
        receiver.BackgroundExceptionOccurred += (_, ex) => second.TrySetResult(ex);

        SendBrokenBam(peerBus);

        (await Within(second.Task)).Should().BeOfType<J1939TpAbortException>();
    }

    // =========================================================================================
    // ISO-TP
    // =========================================================================================

    private static readonly IsoTpEndpoint IsoTpReceiver = IsoTpEndpoint.Normal(txCanId: 0x318, rxCanId: 0x310);
    private static readonly IsoTpEndpoint IsoTpPeer = IsoTpEndpoint.Normal(txCanId: 0x310, rxCanId: 0x318);

    private static IsoTpChannelOptions IsoTpOptions() => new()
    {
        UsePadding = true,
        NCr = TimeSpan.FromSeconds(30), // the abort is the sequence number's, not N_Cr's
    };

    // A First Frame, and once the receiver has answered it, a Consecutive Frame with sequence
    // number 2 instead of 1: the receiver aborts the reassembly.
    private static async Task SendBrokenMultiFrame(ICanBus peer)
    {
        var flowControl = NewSignal<bool>();
        EventHandler<CanReceiveDataView> onFrame = (_, e) =>
        {
            if (e.CanFrame.ID != unchecked((int)IsoTpReceiver.TxCanId)) return;
            var data = e.CanFrame.Data.ToArray();
            if (data.Length > 0 && (data[0] >> 4) == 0x3) flowControl.TrySetResult(true);
        };
        peer.FrameObserved += onFrame;
        try
        {
            var pdu = Enumerable.Range(1, 20).Select(i => (byte)i).ToArray();
            int ffData = IsoTpFrameCodec.FirstFrameMaxDataLength(isCanFd: false, usesAddressExtension: false, useLongLength: false);
            peer.Transmit(CanFrame.Classic(unchecked((int)IsoTpPeer.TxCanId),
                IsoTpFrameCodec.BuildFirstFrame(IsoTpPeer, pdu.Length, pdu.AsSpan(0, ffData), isCanFd: false)));
            await Within(flowControl.Task);
        }
        finally
        {
            peer.FrameObserved -= onFrame;
        }

        peer.Transmit(CanFrame.Classic(unchecked((int)IsoTpPeer.TxCanId),
            IsoTpFrameCodec.BuildConsecutiveFrame(IsoTpPeer, sequenceNumber: 2, new byte[] { 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16 },
                isCanFd: false, padding: true)));
    }

    // #356, T-2 (ISO-TP): the fault was already in the inbox before the report, but the report was
    // raised on the actor, so a handler that waited for the next PDU held the loop that would
    // deliver it.
    [Fact]
    public async Task IsoTp_A_Handler_That_Receives_Gets_The_Abort_And_The_Next_Pdu()
    {
        var session = NewSession();
        using var peerBus = Open(session, 0);
        using var receiverBus = Open(session, 1);
        using var receiver = IsoTpFactory.Open(receiverBus, IsoTpReceiver, IsoTpOptions());
        var fault = NewSignal<Exception?>();
        var next = NewSignal<byte[]>();
        receiver.BackgroundExceptionOccurred += (_, ex) =>
        {
            if (ex is not IsoTpException) return;
            try
            {
                fault.TrySetResult(Record.Exception(() => receiver.ReceiveAsync().GetAwaiter().GetResult()));
                next.TrySetResult(receiver.ReceiveAsync().GetAwaiter().GetResult());
            }
            catch (Exception e)
            {
                next.TrySetException(e);
            }
        };

        await SendBrokenMultiFrame(peerBus);
        (await Within(fault.Task)).Should().BeOfType<IsoTpException>()
            .Which.Message.Should().Contain("sequence-number mismatch");

        peerBus.Transmit(CanFrame.Classic(unchecked((int)IsoTpPeer.TxCanId),
            IsoTpFrameCodec.BuildSingleFrame(IsoTpPeer, new byte[] { 0x55, 0x66 }, isCanFd: false, padding: true)));
        (await Within(next.Task)).Should().Equal(new byte[] { 0x55, 0x66 }, "the actor went on while the handler waited");
    }

    [Fact]
    public async Task IsoTp_A_Throwing_Handler_Does_Not_Hide_The_Report_From_The_Next()
    {
        var session = NewSession();
        using var peerBus = Open(session, 0);
        using var receiverBus = Open(session, 1);
        using var receiver = IsoTpFactory.Open(receiverBus, IsoTpReceiver, IsoTpOptions());
        var second = NewSignal<Exception>();
        receiver.BackgroundExceptionOccurred += (_, _) => throw new InvalidOperationException("first handler");
        receiver.BackgroundExceptionOccurred += (_, ex) => second.TrySetResult(ex);

        await SendBrokenMultiFrame(peerBus);

        (await Within(second.Task)).Should().BeOfType<IsoTpException>()
            .Which.Message.Should().Contain("sequence-number mismatch");
    }

    // =========================================================================================
    // J1939 node
    // =========================================================================================

    private static J1939Name NodeName() => new(identityNumber: 0x0000B6, manufacturerCode: 0x100, ecuInstance: 0,
        functionInstance: 0, function: 0x81, reserved: false, vehicleSystem: 0, vehicleSystemInstance: 0,
        industryGroup: 0, arbitraryAddressCapable: false);

    // A failure on the node's actor -- here a work item that throws -- used to be reported there,
    // so a handler that waited for the actor waited for itself.
    [Fact]
    public async Task J1939Node_A_Handler_That_Waits_For_The_Actor_Does_Not_Deadlock_It()
    {
        using var bus = Open(NewSession(), 0);
        using var service = new CanBusService(bus);
        using var actor = new ProtocolActor();
        using var node = new J1939NodeImpl(service, new J1939NodeOptions(NodeName()), ownsService: false, actor);
        var waited = NewSignal<bool>();
        node.BackgroundExceptionOccurred += (_, ex) =>
        {
            if (ex.Message != "posted work failed") return;
            waited.TrySetResult(actor.PostAsync(() => { }).Wait(HangGuard));
        };

        actor.Post(() => throw new InvalidOperationException("posted work failed"));

        (await Within(waited.Task)).Should().BeTrue("the handler did not run on the actor it waited for");
    }

    [Fact]
    public async Task J1939Node_A_Throwing_Handler_Does_Not_Hide_The_Report_From_The_Next()
    {
        using var bus = Open(NewSession(), 0);
        using var service = new CanBusService(bus);
        using var actor = new ProtocolActor();
        using var node = new J1939NodeImpl(service, new J1939NodeOptions(NodeName()), ownsService: false, actor);
        var second = NewSignal<Exception>();
        node.BackgroundExceptionOccurred += (_, _) => throw new InvalidOperationException("first handler");
        node.BackgroundExceptionOccurred += (_, ex) => second.TrySetResult(ex);
        var failure = new InvalidOperationException("posted work failed");

        actor.Post(() => throw failure);

        (await Within(second.Task)).Should().BeSameAs(failure);
    }
}
