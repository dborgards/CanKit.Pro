using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

/// <summary>
/// Discovery (#131 decision 3, questions 1, 2, 6 and 8). FR-CO-033: listening opens no node and
/// transmits nothing; a heartbeat or a boot-up is each enough; the window is the caller's, 2 s
/// when not given. FR-CO-034: the scan runs only when called, sends an SDO upload of
/// <c>1000h:00</c> and nothing else, skips the client's own node-id and the ones passed in, and
/// counts any answer from the node as presence.
/// </summary>
/// <remarks>
/// The listen tests close the window themselves, after the bus has shown them the last frame
/// they sent, so no test waits on a clock for a frame to be counted. The scan's outcome classes
/// are driven through <see cref="CanOpenDiscovery.ScanCoreAsync"/> with a scripted upload; the
/// bus tests only use nodes that answer, so none of them depends on an SDO timeout.
/// </remarks>
public class CanOpenDiscoveryTests : IClassFixture<VirtualAdapterFixture>
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReplyWindow = TimeSpan.FromMilliseconds(500);

    // ---- FR-CO-033: listen ---------------------------------------------------------------------

    [Fact]
    public async Task Listening_Transmits_Nothing_And_Reports_Heartbeat_Or_BootUp_Alone()
    {
        var session = $"canopen-discovery-{Guid.NewGuid():N}";
        using var busListener = Open(session, 0);
        using var busPeer = Open(session, 1);

        var sent = new ConcurrentDictionary<string, byte>();
        var foreign = new ConcurrentBag<string>();
        busPeer.FrameObserved += (_, e) =>
        {
            var key = Describe(e.CanFrame);
            if (!sent.ContainsKey(key)) foreign.Add(key);
        };

        using var service = new CanBusService(busListener);
        var window = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        TimeSpan? requested = null;
        var listen = CanOpenDiscovery.ListenCoreAsync(service, TimeSpan.FromSeconds(7), (w, _) =>
        {
            requested = w;
            return window.Task;
        }, CancellationToken.None);

        // Registered after the service, so the service has buffered a frame before this sees it.
        var marker = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        busListener.FrameObserved += (_, e) =>
        {
            if ((uint)e.CanFrame.ID == CanOpenCobId.Heartbeat(0x30)) marker.TrySetResult(true);
        };

        Send(busPeer, sent, Heartbeat(0x11, 0x00));   // boot-up only
        Send(busPeer, sent, Heartbeat(0x12, 0x05));   // heartbeat only
        Send(busPeer, sent, Heartbeat(0x13, 0x00));   // boot-up, then heartbeats
        Send(busPeer, sent, Heartbeat(0x13, 0x7F));
        Send(busPeer, sent, Heartbeat(0x13, 0x04));
        Send(busPeer, sent, Heartbeat(0x14, 0x01));   // neither: not a state byte
        // What an opened node at 0x7F would answer: NMT, an SDO upload of 1000h, a guarding RTR.
        Send(busPeer, sent, CanFrame.Classic(unchecked((int)CanOpenCobId.NmtCommand), new byte[] { 0x01, 0x7F }));
        Send(busPeer, sent, CanFrame.Classic(unchecked((int)CanOpenCobId.SdoRx(0x7F)),
            new byte[] { 0x40, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00 }));
        Send(busPeer, sent, CanFrame.Classic(unchecked((int)CanOpenCobId.Heartbeat(0x7F)),
            ReadOnlyMemory<byte>.Empty, isRemoteFrame: true));
        Send(busPeer, sent, Heartbeat(0x30, 0x05));
        await marker.Task.WithTimeoutAsync(ShortTimeout);

        window.SetResult(true);
        var nodes = await listen.WithTimeoutAsync(ShortTimeout);

        requested.Should().Be(TimeSpan.FromSeconds(7), "the window is the caller's");
        nodes.Select(n => n.NodeId).Should().Equal(0x11, 0x12, 0x13, 0x30);
        nodes[0].Evidence.Should().Be(CanOpenPresenceEvidence.BootUp);
        nodes[0].HeartbeatState.Should().BeNull();
        nodes[1].Evidence.Should().Be(CanOpenPresenceEvidence.Heartbeat);
        nodes[1].HeartbeatState.Should().Be(NmtState.Operational);
        nodes[2].Evidence.Should().Be(CanOpenPresenceEvidence.BootUp | CanOpenPresenceEvidence.Heartbeat);
        nodes[2].HeartbeatState.Should().Be(NmtState.Stopped, "the last heartbeat names the state");
        nodes.Should().OnlyContain(n => n.DeviceType == null);

        // No event to wait for: a reply or a boot-up would already be on the peer.
        await Task.Delay(ReplyWindow);
        foreign.Should().BeEmpty("listening transmits no boot-up and answers nothing");
    }

    [Fact]
    public void Record_Classifies_The_State_Byte_And_Ignores_What_Is_Not_Heartbeat_Or_BootUp()
    {
        var heard = new Dictionary<byte, (CanOpenPresenceEvidence Evidence, NmtState? State)>();
        CanOpenDiscovery.Record(heard, View(0x711, 0x85));                          // toggle bit masked
        CanOpenDiscovery.Record(heard, View(0x712, 0xFF));
        CanOpenDiscovery.Record(heard, View(0x713, 0x00));
        CanOpenDiscovery.Record(heard, View(0x714, 0x02));                          // not a state
        CanOpenDiscovery.Record(heard, View(0x715));                                // no data
        CanOpenDiscovery.Record(heard, View(0x700, 0x05));                          // node-id 0
        CanOpenDiscovery.Record(heard, View(0x780, 0x05));                          // past 127
        CanOpenDiscovery.Record(heard, View(0x716, 0x05, extended: true));
        CanOpenDiscovery.Record(heard, View(0x717, remote: true));

        heard.Keys.Should().BeEquivalentTo(new byte[] { 0x11, 0x12, 0x13 });
        heard[0x11].Should().Be((CanOpenPresenceEvidence.Heartbeat, (NmtState?)NmtState.Operational));
        heard[0x12].Should().Be((CanOpenPresenceEvidence.Heartbeat, (NmtState?)NmtState.PreOperational));
        heard[0x13].Should().Be((CanOpenPresenceEvidence.BootUp, null));

        // A boot-up after a heartbeat adds the flag and keeps the state the heartbeat gave.
        CanOpenDiscovery.Record(heard, View(0x711, 0x00));
        heard[0x11].Should().Be((CanOpenPresenceEvidence.Heartbeat | CanOpenPresenceEvidence.BootUp,
            (NmtState?)NmtState.Operational));
    }

    [Fact]
    public void The_Window_Defaults_To_2_Seconds_And_Is_Otherwise_The_Callers()
    {
        CanOpenDiscovery.DefaultListenWindow.Should().Be(TimeSpan.FromSeconds(2));
        CanOpenDiscovery.ValidateWindow(null).Should().Be(TimeSpan.FromSeconds(2));
        CanOpenDiscovery.ValidateWindow(TimeSpan.FromMilliseconds(1)).Should().Be(TimeSpan.FromMilliseconds(1));
        CanOpenDiscovery.ValidateWindow(TimeSpan.FromSeconds(10)).Should().Be(TimeSpan.FromSeconds(10));

        foreach (var act in new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(-1), TimeSpan.FromDays(30) }
                     .Select(bad => (Action)(() => CanOpenDiscovery.ValidateWindow(bad))))
        {
            act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("window");
        }
    }

    [Fact]
    public async Task ListenAsync_Rejects_Null_And_A_Bad_Window_Before_Subscribing()
    {
        using var bus = ControllableBus.EchoCapable($"canopen-discovery-args-{Guid.NewGuid():N}");
        await FluentActions.Awaiting(() => CanOpenDiscovery.ListenAsync((ICanBus)null!))
            .Should().ThrowAsync<ArgumentNullException>().WithParameterName("bus");
        await FluentActions.Awaiting(() => CanOpenDiscovery.ListenAsync((ICanBusService)null!))
            .Should().ThrowAsync<ArgumentNullException>().WithParameterName("service");
        await FluentActions.Awaiting(() => CanOpenDiscovery.ListenAsync(bus, TimeSpan.Zero))
            .Should().ThrowAsync<ArgumentOutOfRangeException>().WithParameterName("window");
    }

    [Fact]
    public async Task A_Cancelled_Listen_Throws_And_Leaves_A_Shared_Service_Open()
    {
        using var bus = ControllableBus.EchoCapable($"canopen-discovery-cancel-{Guid.NewGuid():N}");
        using var service = new CanBusService(bus);
        using var cancellation = new CancellationTokenSource();

        var listen = CanOpenDiscovery.ListenAsync(service, TimeSpan.FromMinutes(5), cancellation.Token);
        cancellation.Cancel();

        await FluentActions.Awaiting(() => listen.WithTimeoutAsync(ShortTimeout))
            .Should().ThrowAsync<OperationCanceledException>();
        using var stillUsable = service.Subscribe();
    }

    [Fact]
    public async Task ListenAsync_On_A_Bus_Uses_The_Window_And_Detaches_Afterwards()
    {
        using var bus = ControllableBus.EchoCapable($"canopen-discovery-bus-{Guid.NewGuid():N}");
        var nodes = await CanOpenDiscovery.ListenAsync(bus, TimeSpan.FromMilliseconds(1))
            .WithTimeoutAsync(ShortTimeout);
        nodes.Should().BeEmpty();
        bus.TransmitCount.Should().Be(0);
    }

    // ---- FR-CO-034: scan -----------------------------------------------------------------------

    [Fact]
    public async Task The_Scan_Asks_Every_Node_Id_Except_The_Client_And_The_Skipped_Ones_For_1000h()
    {
        var asked = new ConcurrentBag<byte>();
        var nodes = await CanOpenDiscovery.ScanCoreAsync(0x7F, new byte[] { 0x05, 0x06 }, (id, _) =>
        {
            asked.Add(id);
            return Task.FromException<byte[]>(Timeout(id));
        }, CancellationToken.None);

        nodes.Should().BeEmpty("a timeout is absence");
        asked.OrderBy(i => i).Should().Equal(Enumerable.Range(1, 127).Select(i => (byte)i)
            .Where(i => i is not (0x7F or 0x05 or 0x06)));
    }

    [Fact]
    public async Task Any_Answer_Is_Presence_And_A_Timeout_A_Refusal_Or_A_Busy_Client_Is_Not()
    {
        var script = new Dictionary<byte, Func<Task<byte[]>>>
        {
            [0x01] = () => Task.FromResult(new byte[] { 0x91, 0x01, 0x0F, 0x00 }),
            [0x02] = () => Task.FromResult(new byte[] { 0x91 }),
            [0x03] = () => Task.FromException<byte[]>(new SdoAbortException(0x1000, 0,
                (uint)SdoAbortCode.ObjectDoesNotExist, SdoAbortOrigin.Peer, "peer abort")),
            [0x04] = () => Task.FromException<byte[]>(new SdoAbortException(0x1000, 0,
                (uint)SdoAbortCode.CommandSpecifierInvalid, SdoAbortOrigin.Local, "malformed response")),
            [0x05] = () => Task.FromException<byte[]>(Timeout(0x05)),
            [0x06] = () => throw new PeerSdoAccessException(0x06, 0x1000, 0, true, "not in the file"),
            [0x07] = () => Task.FromException<byte[]>(new InvalidOperationException("already in flight")),
            [0x08] = () => Task.FromResult(new byte[] { 0x91, 0x01, 0x0F, 0x00, 0x55 }),
        };

        var nodes = await CanOpenDiscovery.ScanCoreAsync(0x7F, Skip(except: script.Keys),
            (id, _) => script[id](), CancellationToken.None);

        nodes.Select(n => n.NodeId).Should().Equal(0x01, 0x02, 0x03, 0x04, 0x08);
        nodes.Should().OnlyContain(n => n.Evidence == CanOpenPresenceEvidence.SdoResponse && n.HeartbeatState == null);
        nodes[0].DeviceType.Should().Be(0x000F0191u);
        nodes[1].DeviceType.Should().BeNull("a value shorter than UNSIGNED32 is an answer, not a device type");
        nodes[2].DeviceType.Should().BeNull();
        nodes[3].DeviceType.Should().BeNull();
        nodes[4].DeviceType.Should().BeNull("a longer value is an answer too, but its first four bytes are not the device type");
    }

    [Fact]
    public async Task A_Disposed_Client_Or_A_Transport_Fault_Ends_The_Scan()
    {
        await FluentActions.Awaiting(() => CanOpenDiscovery.ScanCoreAsync(0x7F, Skip(except: new byte[] { 0x01 }),
                (_, _) => Task.FromException<byte[]>(new ObjectDisposedException("node")), CancellationToken.None))
            .Should().ThrowAsync<ObjectDisposedException>();
        await FluentActions.Awaiting(() => CanOpenDiscovery.ScanCoreAsync(0x7F, Skip(except: new byte[] { 0x01 }),
                (_, _) => Task.FromException<byte[]>(new CanOpenTransportException("bus off")), CancellationToken.None))
            .Should().ThrowAsync<CanOpenTransportException>();
        await FluentActions.Awaiting(() => CanOpenDiscovery.ScanAsync(null!))
            .Should().ThrowAsync<ArgumentNullException>().WithParameterName("client");
    }

    [Fact]
    public async Task The_Scan_Finds_Answering_Nodes_And_Honours_A_Peer_File_Without_1000h()
    {
        var session = $"canopen-discovery-scan-{Guid.NewGuid():N}";
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var busC = Open(session, 2);
        using var withType = CanOpen.OpenNode(busA, 0x21);
        using var plain = CanOpen.OpenNode(busB, 0x22);
        // Long enough that no answer from a virtual node can miss it; every node asked answers.
        using var client = CanOpen.OpenNode(busC, 0x7F, new CanOpenNodeOptions { SdoTimeout = TimeSpan.FromSeconds(30) });
        withType.ObjectDictionary.WriteUnsigned(0x1000, 0, 0x000F0191);
        await WaitForStateAsync(withType, NmtState.PreOperational);
        await WaitForStateAsync(plain, NmtState.PreOperational);

        var nodes = await CanOpenDiscovery.ScanAsync(client, Skip(except: new byte[] { 0x21, 0x22 }))
            .WithTimeoutAsync(ShortTimeout);
        nodes.Select(n => (n.NodeId, n.DeviceType)).Should().Equal(((byte)0x21, (uint?)0x000F0191u), ((byte)0x22, (uint?)0u));

        // A peer file that does not list 1000h keeps the request off the bus. Had it gone out,
        // 0x22 would have answered and been reported.
        client.BindPeerDeviceDescription(0x22, PeerSdoLaboratory.Variables(0x1001));
        nodes = await CanOpenDiscovery.ScanAsync(client, Skip(except: new byte[] { 0x21, 0x22 }))
            .WithTimeoutAsync(ShortTimeout);
        nodes.Select(n => n.NodeId).Should().Equal(0x21);
    }

    [Fact]
    public async Task Without_A_Skip_List_Only_The_Client_Is_Left_Out()
    {
        var asked = new ConcurrentBag<byte>();
        await CanOpenDiscovery.ScanCoreAsync(0x01, null, (id, _) =>
        {
            asked.Add(id);
            return Task.FromException<byte[]>(Timeout(id));
        }, CancellationToken.None);

        asked.OrderBy(i => i).Should().Equal(Enumerable.Range(2, 126).Select(i => (byte)i));
    }

    [Fact]
    public void A_Discovered_Node_Needs_A_Valid_Node_Id_And_Some_Evidence()
    {
        var node = new CanOpenDiscoveredNode(0x7F, CanOpenPresenceEvidence.BootUp, null, 0x191);
        node.NodeId.Should().Be(0x7F);
        node.DeviceType.Should().Be(0x191u);

        var none = () => new CanOpenDiscoveredNode(0x10, CanOpenPresenceEvidence.None, null, null);
        none.Should().Throw<ArgumentException>().WithParameterName("evidence");
        var zero = () => new CanOpenDiscoveredNode(0x00, CanOpenPresenceEvidence.Heartbeat, NmtState.Operational, null);
        zero.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static IEnumerable<byte> Skip(IEnumerable<byte> except)
    {
        var keep = new HashSet<byte>(except);
        return Enumerable.Range(1, 127).Select(i => (byte)i).Where(i => !keep.Contains(i));
    }

    private static SdoAbortException Timeout(byte nodeId)
        => new(0x1000, 0, (uint)SdoAbortCode.SdoProtocolTimedOut, SdoAbortOrigin.Local, $"0x{nodeId:X2} timed out");

    private static async Task WaitForStateAsync(ICanOpenNode node, NmtState state)
    {
        var deadline = DateTime.UtcNow + ShortTimeout;
        while (node.State != state)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Node 0x{node.NodeId:X2} did not reach {state}; it is in {node.State}.");
            await Task.Delay(5);
        }
    }

    private static CanFrame Heartbeat(byte nodeId, byte state)
        => CanFrame.Classic(unchecked((int)CanOpenCobId.Heartbeat(nodeId)), new[] { state });

    private static CanFrameView View(int id, byte? state = null, bool extended = false, bool remote = false)
    {
        var data = state is { } s ? new[] { s } : Array.Empty<byte>();
        var flags = (extended ? FrameFlags.Ext : FrameFlags.None)
            | (remote ? FrameFlags.Rtr : FrameFlags.None);
        return new CanFrameView(CanFrameType.Can20, id, data, flags);
    }

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
