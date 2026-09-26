using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

/// <summary>
/// #131 decision 3, as the bus-scan sample uses it: hearing heartbeats and boot-up must not
/// open a node. <see cref="CanOpen.OpenNode(ICanBus, byte, CanOpenNodeOptions?)"/> transmits a
/// boot-up and answers NMT, SDO and node guarding; <see cref="CanOpenHeartbeatObserver"/> does
/// none of that.
/// </summary>
public class CanOpenHeartbeatObserverTests : IClassFixture<VirtualAdapterFixture>
{
    private static readonly TimeSpan ReplyWindow = TimeSpan.FromMilliseconds(500);

    [Fact]
    public async Task Listening_Does_Not_Transmit_And_Still_Hears_Heartbeats_And_Bootup()
    {
        var session = $"canopen-listen-{Guid.NewGuid():N}";
        using var busObserver = Open(session, 0);
        using var busPeer = Open(session, 1);

        var sent = new ConcurrentDictionary<string, byte>();
        var foreign = new ConcurrentBag<string>();
        busPeer.FrameObserved += (_, e) =>
        {
            var key = Describe(e.CanFrame);
            if (!sent.ContainsKey(key)) foreign.Add(key);
        };

        using (var observer = CanOpenHeartbeatObserver.Open(busObserver))
        {
            var bootup = new TaskCompletionSource<NmtState>(TaskCreationOptions.RunContinuationsAsynchronously);
            var heartbeat = new TaskCompletionSource<NmtState>(TaskCreationOptions.RunContinuationsAsynchronously);
            observer.HeartbeatReceived += (_, e) =>
            {
                if (e.ProducerNodeId != 0x11) return;
                if (e.State == NmtState.Initializing) bootup.TrySetResult(e.State);
                if (e.State == NmtState.Operational) heartbeat.TrySetResult(e.State);
            };

            Send(busPeer, sent, CanFrame.Classic(
                unchecked((int)CanOpenCobId.Heartbeat(0x11)), new byte[] { 0x00 }));
            (await bootup.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2))).Should().Be(NmtState.Initializing);

            Send(busPeer, sent, CanFrame.Classic(
                unchecked((int)CanOpenCobId.Heartbeat(0x11)), new byte[] { 0x05 }));
            (await heartbeat.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2))).Should().Be(NmtState.Operational);

            // What an opened node at 0x7F would answer: NMT, an SDO upload of 1000h, a guarding RTR.
            Send(busPeer, sent, CanFrame.Classic(
                unchecked((int)CanOpenCobId.NmtCommand), new byte[] { 0x01, 0x7F }));
            Send(busPeer, sent, CanFrame.Classic(
                unchecked((int)CanOpenCobId.SdoRx(0x7F)),
                new byte[] { 0x40, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00 }));
            Send(busPeer, sent, CanFrame.Classic(
                unchecked((int)CanOpenCobId.Heartbeat(0x7F)),
                ReadOnlyMemory<byte>.Empty,
                isRemoteFrame: true));

            // No event to wait for: a reply or a boot-up would already be on the peer.
            await Task.Delay(ReplyWindow);
            foreign.Should().BeEmpty("a listen-only observer transmits no boot-up and answers nothing");
        }

        var nodeBootup = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        busPeer.FrameObserved += (_, e) =>
        {
            var frame = e.CanFrame;
            if (frame.IsRemoteFrame || (uint)frame.ID != CanOpenCobId.Heartbeat(0x22) || frame.Data.Length < 1)
            {
                return;
            }

            if (frame.Data.Span[0] == 0x00) nodeBootup.TrySetResult(true);
        };

        using (CanOpen.OpenNode(busObserver, nodeId: 0x22))
        {
            await nodeBootup.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public void Open_Rejects_A_Null_Bus()
    {
        var act = () => CanOpenHeartbeatObserver.Open(null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("bus");
    }

    [Fact]
    public void Dispose_Is_Idempotent()
    {
        using var bus = ControllableBus.EchoCapable($"canopen-listen-dispose-{Guid.NewGuid():N}");
        // `using` as well as the explicit call: the explicit one is the idempotence check,
        // the `using` makes sure disposal still happens if that call throws.
        using var observer = CanOpenHeartbeatObserver.Open(bus);
        observer.Dispose();
    }

    [Fact]
    public void A_Failed_Subscribe_Disposes_The_Service()
    {
        using var bus = ControllableBus.EchoCapable($"canopen-listen-fail-{Guid.NewGuid():N}");
        // Same shape: the explicit Dispose is what Attach must observe, and `using` still
        // runs if that Dispose throws.
        using var service = new CanBusService(bus);
        service.Dispose();

        var act = () => CanOpenHeartbeatObserver.Attach(service);
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task Every_Heartbeat_State_Is_Reported_And_Other_Frames_Are_Ignored()
    {
        using var bus = ControllableBus.EchoCapable($"canopen-listen-states-{Guid.NewGuid():N}");
        using var observer = CanOpenHeartbeatObserver.Open(bus);
        observer.OnFrame(Event(0x711, new byte[] { 0x05 }));

        var seen = new ConcurrentQueue<NmtState>();
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        observer.HeartbeatReceived += (_, e) =>
        {
            if (e.ProducerNodeId != 0x11) return;
            seen.Enqueue(e.State);
            if (seen.Count >= 3) done.TrySetResult(true);
        };

        bus.RaiseObserved(
            CanFrame.Classic(unchecked((int)CanOpenCobId.Heartbeat(0x11)), new byte[] { 0x05 }, isExtendedFrame: true),
            isEcho: false);
        bus.RaiseObserved(
            CanFrame.Classic(unchecked((int)CanOpenCobId.Heartbeat(0x11)), ReadOnlyMemory<byte>.Empty, isRemoteFrame: true),
            isEcho: false);
        bus.RaiseObserved(Heartbeat(0x11, payload: null), isEcho: false);
        bus.RaiseObserved(Heartbeat(0x00, 0x05), isEcho: false);
        bus.RaiseObserved(CanFrame.Classic(0x780, new byte[] { 0x05 }), isEcho: false);

        bus.RaiseObserved(Heartbeat(0x11, 0x04), isEcho: false);
        bus.RaiseObserved(Heartbeat(0x11, 0x7F), isEcho: false);
        bus.RaiseObserved(Heartbeat(0x11, 0x01), isEcho: false);

        await done.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));
        seen.Should().Equal(NmtState.Stopped, NmtState.PreOperational, NmtState.Initializing);
    }

    private static CanFrame Heartbeat(byte nodeId, byte? payload)
    {
        var data = payload is { } state ? new byte[] { state } : Array.Empty<byte>();
        return CanFrame.Classic(unchecked((int)CanOpenCobId.Heartbeat(nodeId)), data);
    }

    private static CanFrameEvent Event(int id, byte[] data)
        => new(new CanFrameView(CanFrameType.Can20, id, data, FrameFlags.None), isEcho: false, TimeSpan.Zero);

    private static ICanBus Open(string session, int channel) => CanBus.Open(
        $"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(VirtualAdapterFixture.Bitrate));

    private static void Send(ICanBus bus, ConcurrentDictionary<string, byte> sent, CanFrame frame)
    {
        sent.TryAdd(Describe(frame), 0);
        bus.Transmit(frame);
    }

    private static string Describe(CanFrame frame)
        => Describe((uint)frame.ID, frame.IsRemoteFrame, frame.Data.Span);

    private static string Describe(CanFrameView frame)
        => Describe((uint)frame.ID, frame.IsRemoteFrame, frame.Data.Span);

    private static string Describe(uint id, bool remote, ReadOnlySpan<byte> data)
        => $"{id:X3}:{(remote ? "R" : "D")}:{Convert.ToHexString(data)}";
}
