using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Pdo;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

/// <summary>
/// Observing a PDO that belongs to another node (#163): the COB-ID and the mapping are read
/// from the peer over SDO when the peer-SDO gate allows the pair. The file is used when that
/// read aborts, times out, or is refused. The bytes go to the caller's sink. This node's
/// object dictionary is left alone, and a frame that is not one of its RPDOs still is.
/// </summary>
public class CanOpenForeignPdoObserveTests : IClassFixture<VirtualAdapterFixture>
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);

    private const byte Peer = 0x11;
    private const byte Tool = 0x01;

    private static uint Tpdo1 => 0x180 + Peer;
    private static uint Tpdo4 => 0x480 + Peer;
    private static uint Rpdo1 => 0x200 + Peer;

    private static string NewSession() => VirtualAdapterFixture.NewSession("canopen-observe");

    private static ICanBus Open(string session, int channel) => VirtualAdapterFixture.Open(session, channel);

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

    private static void Settle(ICanOpenNode node)
    {
        _ = node.State;
        _ = node.State;
    }

    /// <summary>The peer the live-mapping tests reconfigure. TPDO1's file mapping is a 16-bit
    /// <c>2000h</c> plus a dummy; the test then points the live record at an 8-bit <c>2001h</c>
    /// in the slots the file already lists. TPDO4 and RPDO1 each have one listed slot whose
    /// file value is the 8-bit object.</summary>
    private static CanOpenDeviceDescription PeerFile() => CanOpenDeviceDescription.ParseEds(Eds(
        optional: @"SupportedObjects=6
1=0x1400
2=0x1600
3=0x1800
4=0x1A00
5=0x1803
6=0x1A03",
        manufacturer: @"SupportedObjects=2
1=0x2000
2=0x2001",
        sections: @"
" + Comm("1400", "$NODEID+0x200", tpdo: false) + @"
" + Map("1600", "1", "0x20010008") + @"
" + Comm("1800", "$NODEID+0x180", tpdo: true) + @"
" + Map("1A00", "2", "0x20000010", "0x00050008") + @"
" + Comm("1803", "$NODEID+0x480", tpdo: true) + @"
" + Map("1A03", "1", "0x20010008") + @"
" + Var("2000", "0x0006", "0x1234") + @"
" + Var("2001", "0x0005", "7")));

    [Fact]
    public async Task Live_mapping_splits_a_peer_TPDO_and_the_local_dictionary_is_not_written()
    {
        var session = NewSession();
        using var peerBus = Open(session, 0);
        using var toolBus = Open(session, 1);
        using var observer = Open(session, 2);
        var file = PeerFile();
        using var peer = CanOpen.OpenNode(peerBus, Peer, file);
        using var tool = CanOpen.OpenNode(toolBus, Tool);
        await WaitForStateAsync(peer, NmtState.PreOperational);
        peer.ConfigureTpdo(1, new PdoMapping()
            .Add(0x0005, 0x00, 8)
            .Add(0x2001, 0x00, 8));

        var uploads = new SdoTap(observer, Peer);
        var sink = new ListSink();
        var background = new List<Exception>();
        tool.BackgroundExceptionOccurred += (_, ex) => background.Add(ex);

        peerBus.Transmit(CanFrame.Classic(unchecked((int)Tpdo1), new byte[] { 0x00, 0x5A, 0xFF }));
        var seen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        tool.HeartbeatReceived += (_, e) =>
        {
            if (e.ProducerNodeId == 0x22) seen.TrySetResult(true);
        };
        peerBus.Transmit(CanFrame.Classic(unchecked((int)CanOpenCobId.Heartbeat(0x22)), new byte[] { 0x05 }));
        await seen.Task.WithTimeoutAsync(ShortTimeout);
        Settle(tool);

        tool.ObjectDictionary.ContainsIndex(0x2000).Should().BeFalse();
        tool.ObjectDictionary.ContainsIndex(0x2001).Should().BeFalse();
        tool.ObjectDictionary.ReadUnsigned(0x1001, 0x00).Should().Be(0u);
        background.Should().BeEmpty();
        tool.BindPeerDeviceDescription(Peer, file);

        var result = await tool.ObserveForeignPdoAsync(Peer, Tpdo1, new byte[] { 0x00, 0x5A, 0xFF }, file, sink)
            .WithTimeoutAsync(ShortTimeout);

        result.Decoded.Should().BeTrue();
        result.Reason.Should().BeNull();
        result.Observations.Should().ContainSingle();
        var observation = result.Observations[0];
        observation.Kind.Should().Be(ForeignPdoKind.Tpdo);
        observation.PdoNumber.Should().Be(1);
        observation.Origin.Should().Be(ForeignPdoMappingOrigin.LiveMapping);
        observation.SignalsWritten.Should().Be(1);
        sink.Signals.Should().ContainSingle();
        var signal = sink.Signals[0];
        signal.PeerNodeId.Should().Be(Peer);
        signal.Kind.Should().Be(ForeignPdoKind.Tpdo);
        signal.PdoNumber.Should().Be(1);
        signal.CobId.Should().Be(Tpdo1);
        signal.Index.Should().Be(0x2001);
        signal.SubIndex.Should().Be(0);
        signal.Value.Should().Equal(0x5A);
        signal.Origin.Should().Be(ForeignPdoMappingOrigin.LiveMapping);
        tool.ObjectDictionary.ContainsIndex(0x2001).Should().BeFalse();
        background.Should().BeEmpty();

        uploads.Uploads.Should().Equal(
            ((ushort)0x1800, (byte)0x01),
            ((ushort)0x1803, (byte)0x01),
            ((ushort)0x1400, (byte)0x01),
            ((ushort)0x1A00, (byte)0x00),
            ((ushort)0x1A00, (byte)0x01),
            ((ushort)0x1A00, (byte)0x02));
    }

    [Fact]
    public async Task Live_mapping_of_TPDO4_and_RPDO1_is_read_from_those_records()
    {
        var session = NewSession();
        using var peerBus = Open(session, 0);
        using var toolBus = Open(session, 1);
        var file = PeerFile();
        using var peer = CanOpen.OpenNode(peerBus, Peer, file);
        using var tool = CanOpen.OpenNode(toolBus, Tool);
        await WaitForStateAsync(peer, NmtState.PreOperational);
        peer.ConfigureTpdo(4, new PdoMapping().Add(0x2000, 0x00, 16));
        peer.ConfigureRpdo(1, new PdoMapping().Add(0x2000, 0x00, 16));
        tool.BindPeerDeviceDescription(Peer, file);

        var tpdo = new ListSink();
        var tpdoResult = await tool.ObserveForeignPdoAsync(Peer, Tpdo4, new byte[] { 0x34, 0x12 }, file, tpdo)
            .WithTimeoutAsync(ShortTimeout);
        tpdoResult.Observations.Should().ContainSingle();
        tpdoResult.Observations[0].Kind.Should().Be(ForeignPdoKind.Tpdo);
        tpdoResult.Observations[0].PdoNumber.Should().Be(4);
        tpdoResult.Observations[0].Origin.Should().Be(ForeignPdoMappingOrigin.LiveMapping);
        tpdo.Signals.Should().ContainSingle();
        tpdo.Signals[0].Index.Should().Be(0x2000);
        tpdo.Signals[0].Value.Should().Equal(0x34, 0x12);

        var rpdo = new ListSink();
        var rpdoResult = await tool.ObserveForeignPdoAsync(Peer, Rpdo1, new byte[] { 0x78, 0x56 }, file, rpdo)
            .WithTimeoutAsync(ShortTimeout);
        rpdoResult.Observations.Should().ContainSingle();
        rpdoResult.Observations[0].Kind.Should().Be(ForeignPdoKind.Rpdo);
        rpdoResult.Observations[0].PdoNumber.Should().Be(1);
        rpdoResult.Observations[0].Origin.Should().Be(ForeignPdoMappingOrigin.LiveMapping);
        rpdo.Signals[0].Index.Should().Be(0x2000);
        rpdo.Signals[0].Value.Should().Equal(0x78, 0x56);
    }

    [Fact]
    public async Task An_empty_live_mapping_is_used_and_does_not_fall_back_to_the_file()
    {
        var session = NewSession();
        using var peerBus = Open(session, 0);
        using var toolBus = Open(session, 1);
        var file = PeerFile();
        using var peer = CanOpen.OpenNode(peerBus, Peer, file);
        using var tool = CanOpen.OpenNode(toolBus, Tool);
        await WaitForStateAsync(peer, NmtState.PreOperational);
        peer.ConfigureTpdo(4, new PdoMapping());
        tool.BindPeerDeviceDescription(Peer, file);

        var sink = new ListSink();
        var result = await tool.ObserveForeignPdoAsync(Peer, Tpdo4, new byte[] { 0x11, 0x22 }, file, sink)
            .WithTimeoutAsync(ShortTimeout);

        result.Observations.Should().ContainSingle();
        result.Observations[0].Decoded.Should().BeTrue();
        result.Observations[0].Origin.Should().Be(ForeignPdoMappingOrigin.LiveMapping);
        result.Observations[0].SignalsWritten.Should().Be(0);
        sink.Signals.Should().BeEmpty();
    }

    [Fact]
    public async Task A_short_payload_against_a_live_mapping_writes_nothing()
    {
        var session = NewSession();
        using var peerBus = Open(session, 0);
        using var toolBus = Open(session, 1);
        var file = PeerFile();
        using var peer = CanOpen.OpenNode(peerBus, Peer, file);
        using var tool = CanOpen.OpenNode(toolBus, Tool);
        await WaitForStateAsync(peer, NmtState.PreOperational);
        peer.ObjectDictionary.ReadUnsigned(0x1A00, 0x00).Should().Be(2u);
        tool.BindPeerDeviceDescription(Peer, file);

        var sink = new ListSink();
        var result = await tool.ObserveForeignPdoAsync(Peer, Tpdo1, new byte[] { 0x11 }, file, sink)
            .WithTimeoutAsync(ShortTimeout);

        result.Decoded.Should().BeFalse();
        result.Observations.Should().ContainSingle();
        result.Observations[0].Decoded.Should().BeFalse();
        result.Observations[0].Reason.Should().Contain("payload");
        sink.Signals.Should().BeEmpty();
    }

    [Fact]
    public async Task An_SDO_abort_falls_back_to_the_mapping_in_the_EDS()
    {
        var session = NewSession();
        using var peerBus = Open(session, 0);
        using var toolBus = Open(session, 1);
        using var observer = Open(session, 2);
        using var peer = CanOpen.OpenNode(peerBus, Peer, MinimalEds());
        using var tool = CanOpen.OpenNode(toolBus, Tool);
        await WaitForStateAsync(peer, NmtState.PreOperational);
        var file = PeerFile();
        tool.BindPeerDeviceDescription(Peer, file);
        var uploads = new SdoTap(observer, Peer);
        var sink = new ListSink();

        var result = await tool.ObserveForeignPdoAsync(Peer, Tpdo1, new byte[] { 0x34, 0x12, 0x00 }, file, sink)
            .WithTimeoutAsync(ShortTimeout);

        result.Observations.Should().ContainSingle();
        result.Observations[0].Decoded.Should().BeTrue();
        result.Observations[0].Origin.Should().Be(ForeignPdoMappingOrigin.DeviceDescription);
        sink.Signals.Should().ContainSingle();
        sink.Signals[0].Index.Should().Be(0x2000);
        sink.Signals[0].Value.Should().Equal(0x34, 0x12);
        sink.Signals[0].Origin.Should().Be(ForeignPdoMappingOrigin.DeviceDescription);
        uploads.Uploads.Should().Contain(pair => pair.Index == 0x1A00);
    }

    [Fact]
    public async Task An_SDO_timeout_falls_back_to_the_mapping_in_the_EDS()
    {
        var session = NewSession();
        using var peerBus = Open(session, 0);
        using var toolBus = Open(session, 1);
        var file = PeerFile();
        using var peer = CanOpen.OpenNode(peerBus, Peer, file);
        using var tool = CanOpen.OpenNode(toolBus, Tool, new CanOpenNodeOptions { SdoTimeout = TimeSpan.FromMilliseconds(200) });
        await WaitForStateAsync(peer, NmtState.PreOperational);
        peer.ConfigureTpdo(4, new PdoMapping().Add(0x2000, 0x00, 16));
        await tool.SendNmtCommandAsync(NmtCommand.Stop, Peer);
        await WaitForStateAsync(peer, NmtState.Stopped);
        tool.BindPeerDeviceDescription(Peer, file);

        var sink = new ListSink();
        var watch = Stopwatch.StartNew();
        var result = await tool.ObserveForeignPdoAsync(Peer, Tpdo4, new byte[] { 0x5A }, file, sink)
            .WithTimeoutAsync(ShortTimeout);
        watch.Stop();

        result.Observations.Should().ContainSingle();
        result.Observations[0].Origin.Should().Be(ForeignPdoMappingOrigin.DeviceDescription);
        sink.Signals.Should().ContainSingle();
        sink.Signals[0].Index.Should().Be(0x2001);
        sink.Signals[0].Value.Should().Equal(0x5A);
        watch.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(150));
    }

    [Fact]
    public async Task A_DCF_parameter_value_is_the_fallback_mapping()
    {
        var session = NewSession();
        using var peerBus = Open(session, 0);
        using var toolBus = Open(session, 1);
        using var peer = CanOpen.OpenNode(peerBus, Peer, MinimalEds());
        using var tool = CanOpen.OpenNode(toolBus, Tool);
        await WaitForStateAsync(peer, NmtState.PreOperational);
        var file = CanOpenDeviceDescription.ParseDcf(Dcf());
        var sink = new ListSink();

        var result = await tool.ObserveForeignPdoAsync(Peer, Tpdo1, new byte[] { 0x34, 0x12 }, file, sink)
            .WithTimeoutAsync(ShortTimeout);

        result.Observations[0].Origin.Should().Be(ForeignPdoMappingOrigin.DeviceDescription);
        sink.Signals.Should().ContainSingle();
        sink.Signals[0].Index.Should().Be(0x2000);
        sink.Signals[0].Value.Should().Equal(0x34, 0x12);
    }

    [Fact]
    public async Task A_subindex_the_bound_description_omits_falls_back_to_the_file()
    {
        var session = NewSession();
        using var peerBus = Open(session, 0);
        using var toolBus = Open(session, 1);
        var narrow = CanOpenDeviceDescription.ParseEds(Eds(
            optional: @"SupportedObjects=2
1=0x1800
2=0x1A00",
            manufacturer: @"SupportedObjects=2
1=0x2000
2=0x2001",
            sections: Comm("1800", "$NODEID+0x180", tpdo: true) + Map("1A00", "1", "0x20000010")
                      + Var("2000", "0x0006", "0") + Var("2001", "0x0005", "0")));
        using var observer = Open(session, 2);
        using var peer = CanOpen.OpenNode(peerBus, Peer, narrow);
        using var tool = CanOpen.OpenNode(toolBus, Tool);
        await WaitForStateAsync(peer, NmtState.PreOperational);
        peer.ConfigureTpdo(1, new PdoMapping().Add(0x2001, 0x00, 8).Add(0x2001, 0x00, 8));
        tool.BindPeerDeviceDescription(Peer, narrow);
        var uploads = new SdoTap(observer, Peer);
        var sink = new ListSink();

        var result = await tool.ObserveForeignPdoAsync(Peer, Tpdo1, new byte[] { 0xAB, 0xCD }, narrow, sink)
            .WithTimeoutAsync(ShortTimeout);

        result.Observations.Should().ContainSingle();
        result.Observations[0].Origin.Should().Be(ForeignPdoMappingOrigin.DeviceDescription);
        sink.Signals.Should().ContainSingle();
        sink.Signals[0].Index.Should().Be(0x2000);
        sink.Signals[0].Value.Should().Equal(0xAB, 0xCD);
        uploads.Uploads.Should().Contain(pair => pair.Index == 0x1A00 && pair.Sub == 0);
        uploads.Uploads.Should().Contain(pair => pair.Index == 0x1A00 && pair.Sub == 1);
        uploads.Uploads.Should().NotContain(pair => pair.Index == 0x1A00 && pair.Sub == 2);
    }

    [Fact]
    public async Task A_mapping_record_the_bound_description_omits_is_not_read_over_SDO()
    {
        var session = NewSession();
        using var peerBus = Open(session, 0);
        using var toolBus = Open(session, 1);
        using var observer = Open(session, 2);
        using var peer = CanOpen.OpenNode(peerBus, Peer, PeerFile());
        using var tool = CanOpen.OpenNode(toolBus, Tool);
        await WaitForStateAsync(peer, NmtState.PreOperational);
        peer.ConfigureTpdo(1, new PdoMapping().Add(0x2001, 0x00, 8));
        var file = CanOpenDeviceDescription.ParseEds(Eds(
            optional: @"SupportedObjects=1
1=0x1800",
            manufacturer: "SupportedObjects=0",
            sections: Comm("1800", "$NODEID+0x180", tpdo: true)));
        tool.BindPeerDeviceDescription(Peer, file);
        var uploads = new SdoTap(observer, Peer);
        var sink = new ListSink();

        var result = await tool.ObserveForeignPdoAsync(Peer, Tpdo1, new byte[] { 0x5A }, file, sink)
            .WithTimeoutAsync(ShortTimeout);

        result.Decoded.Should().BeFalse();
        result.Observations.Should().ContainSingle();
        result.Observations[0].Decoded.Should().BeFalse();
        result.Observations[0].Reason.Should().Contain("not in the peer description");
        sink.Signals.Should().BeEmpty();
        uploads.Uploads.Should().Contain(pair => pair.Index == 0x1800 && pair.Sub == 1);
        uploads.Uploads.Should().NotContain(pair => pair.Index == 0x1A00);
    }

    [Fact]
    public async Task A_live_COB_ID_is_used_ahead_of_the_one_in_the_file()
    {
        var session = NewSession();
        using var peerBus = Open(session, 0);
        using var toolBus = Open(session, 1);
        var file = PeerFile();
        using var peer = CanOpen.OpenNode(peerBus, Peer, file);
        using var tool = CanOpen.OpenNode(toolBus, Tool);
        await WaitForStateAsync(peer, NmtState.PreOperational);
        const uint moved = 0x222;
        peer.ConfigureTpdo(1, new PdoMapping().Add(0x2001, 0x00, 8), cobId: moved);
        tool.BindPeerDeviceDescription(Peer, file);

        var onTheFileId = new ListSink();
        var missed = await tool.ObserveForeignPdoAsync(Peer, Tpdo1, new byte[] { 0x5A }, file, onTheFileId)
            .WithTimeoutAsync(ShortTimeout);
        missed.Observations.Should().BeEmpty();
        missed.Reason.Should().Contain("COB-ID");
        onTheFileId.Signals.Should().BeEmpty();

        var onTheLiveId = new ListSink();
        var hit = await tool.ObserveForeignPdoAsync(Peer, moved, new byte[] { 0x5A }, file, onTheLiveId)
            .WithTimeoutAsync(ShortTimeout);
        hit.Observations.Should().ContainSingle();
        hit.Observations[0].PdoNumber.Should().Be(1);
        hit.Observations[0].Origin.Should().Be(ForeignPdoMappingOrigin.LiveMapping);
        onTheLiveId.Signals.Should().ContainSingle();
        onTheLiveId.Signals[0].CobId.Should().Be(moved);
        onTheLiveId.Signals[0].Index.Should().Be(0x2001);
        onTheLiveId.Signals[0].Value.Should().Equal(0x5A);
    }

    [Fact]
    public async Task A_live_invalid_COB_ID_is_not_replaced_by_the_file()
    {
        var session = NewSession();
        using var peerBus = Open(session, 0);
        using var toolBus = Open(session, 1);
        using var observer = Open(session, 2);
        using var peer = CanOpen.OpenNode(peerBus, Peer);
        using var tool = CanOpen.OpenNode(toolBus, Tool);
        await WaitForStateAsync(peer, NmtState.PreOperational);
        var file = CanOpenDeviceDescription.ParseEds(Eds(
            optional: @"SupportedObjects=2
1=0x1800
2=0x1A00",
            manufacturer: "SupportedObjects=0",
            sections: Comm("1800", "$NODEID+0x180", tpdo: true) + Map("1A00", "1", "0x20000010")));
        tool.BindPeerDeviceDescription(Peer, file);
        var uploads = new SdoTap(observer, Peer);
        var sink = new ListSink();

        var result = await tool.ObserveForeignPdoAsync(Peer, Tpdo1, new byte[] { 0x11, 0x22 }, file, sink)
            .WithTimeoutAsync(ShortTimeout);

        result.Decoded.Should().BeFalse();
        result.Observations.Should().BeEmpty();
        result.Reason.Should().Contain("COB-ID");
        sink.Signals.Should().BeEmpty();
        uploads.Uploads.Should().Contain(pair => pair.Index == 0x1800 && pair.Sub == 1);
        uploads.Uploads.Should().NotContain(pair => pair.Index == 0x1A00);
    }

    [Fact]
    public async Task An_unknown_COB_ID_and_an_invalid_PDO_are_not_decoded()
    {
        var session = NewSession();
        using var peerBus = Open(session, 0);
        using var toolBus = Open(session, 1);
        using var observer = Open(session, 2);
        using var peer = CanOpen.OpenNode(peerBus, Peer);
        using var tool = CanOpen.OpenNode(toolBus, Tool);
        await WaitForStateAsync(peer, NmtState.PreOperational);
        var file = CanOpenDeviceDescription.ParseEds(Eds(
            optional: @"SupportedObjects=2
1=0x1800
2=0x1A00",
            manufacturer: "SupportedObjects=0",
            sections: Comm("1800", "0x80000191", tpdo: true) + Map("1A00", "1", "0x20000010")));
        tool.BindPeerDeviceDescription(Peer, file);
        var uploads = new SdoTap(observer, Peer);
        var sink = new ListSink();

        var unknown = await tool.ObserveForeignPdoAsync(Peer, 0x192, new byte[] { 0x11, 0x22 }, file, sink)
            .WithTimeoutAsync(ShortTimeout);
        unknown.CobId.Should().Be(0x192u);
        unknown.Decoded.Should().BeFalse();
        unknown.Observations.Should().BeEmpty();
        unknown.Reason.Should().Contain("COB-ID");

        var invalid = await tool.ObserveForeignPdoAsync(Peer, Tpdo1, new byte[] { 0x11, 0x22 }, file, sink)
            .WithTimeoutAsync(ShortTimeout);
        invalid.Decoded.Should().BeFalse();
        invalid.Observations.Should().BeEmpty();
        sink.Signals.Should().BeEmpty();

        await tool.SdoUploadAsync(Peer, 0x1001, 0x00).WithTimeoutAsync(ShortTimeout);
        uploads.Uploads.Should().Contain(pair => pair.Index == 0x1800 && pair.Sub == 1);
        uploads.Uploads.Should().NotContain(pair => pair.Index == 0x1400);
        uploads.Uploads.Should().NotContain(pair => pair.Index == 0x1A00 || pair.Index == 0x1600);
        uploads.Uploads.Should().Contain(pair => pair.Index == 0x1001);
    }

    [Fact]
    public async Task A_file_whose_mapping_cannot_be_read_stays_undecoded_when_the_live_read_fails()
    {
        var session = NewSession();
        using var peerBus = Open(session, 0);
        using var toolBus = Open(session, 1);
        using var peer = CanOpen.OpenNode(peerBus, Peer, MinimalEds());
        using var tool = CanOpen.OpenNode(toolBus, Tool);
        await WaitForStateAsync(peer, NmtState.PreOperational);
        var file = CanOpenDeviceDescription.ParseEds(Eds(
            optional: @"SupportedObjects=2
1=0x1800
2=0x1A00",
            manufacturer: "SupportedObjects=0",
            sections: Comm("1800", "$NODEID+0x180", tpdo: true) + @"
[1A00]
ParameterName=TPDO1 mapping
SubNumber=1
ObjectType=0x9
[1A00sub0]
ParameterName=Number of mapped objects
ObjectType=0x7
DataType=0x0005
AccessType=rw
DefaultValue=nope
PDOMapping=0
"));
        tool.BindPeerDeviceDescription(Peer, file);
        var sink = new ListSink();

        var result = await tool.ObserveForeignPdoAsync(Peer, Tpdo1, new byte[] { 0x11, 0x22 }, file, sink)
            .WithTimeoutAsync(ShortTimeout);

        result.Decoded.Should().BeFalse();
        result.Observations.Should().ContainSingle();
        result.Observations[0].Decoded.Should().BeFalse();
        result.Observations[0].Reason.Should().Contain("could not be read");
        sink.Signals.Should().BeEmpty();
    }

    [Fact]
    public void A_signal_rejects_a_null_value()
    {
        var act = () => new ForeignPdoSignal(Peer, ForeignPdoKind.Tpdo, 1, Tpdo1, 0x2000, 0, null!,
            ForeignPdoMappingOrigin.LiveMapping);
        act.Should().Throw<ArgumentNullException>().WithParameterName("value");
    }

    [Fact]
    public async Task A_file_that_is_not_a_usable_COB_ID_or_mapping_is_not_decoded()
    {
        var session = NewSession();
        using var peerBus = Open(session, 0);
        using var toolBus = Open(session, 1);
        using var peer = CanOpen.OpenNode(peerBus, Peer, MinimalEds());
        using var tool = CanOpen.OpenNode(toolBus, Tool);
        await WaitForStateAsync(peer, NmtState.PreOperational);

        async Task<ForeignPdoObserveResult> Observe(CanOpenDeviceDescription file, byte[] payload)
        {
            var sink = new ListSink();
            var result = await tool.ObserveForeignPdoAsync(Peer, Tpdo1, payload, file, sink)
                .WithTimeoutAsync(ShortTimeout);
            sink.Signals.Should().BeEmpty();
            return result;
        }

        var extended = await Observe(FileWith(Comm("1800", "0x20000191", tpdo: true) + Map("1A00", "1", "0x20010008")), new byte[] { 0x5A });
        extended.Observations.Should().BeEmpty();
        extended.Reason.Should().Contain("COB-ID");

        var stray = await Observe(FileWith(Comm("1800", "0x800", tpdo: true) + Map("1A00", "1", "0x20010008")), new byte[] { 0x5A });
        stray.Observations.Should().BeEmpty();

        var restricted = await tool.ObserveForeignPdoAsync(Peer, 0x000, new byte[] { 0x5A },
            FileWith(Comm("1800", "0x000", tpdo: true) + Map("1A00", "1", "0x20010008")), new ListSink())
            .WithTimeoutAsync(ShortTimeout);
        restricted.Observations.Should().BeEmpty();
        restricted.Reason.Should().Contain("COB-ID");

        var sdoRange = await tool.ObserveForeignPdoAsync(Peer, 0x581, new byte[] { 0x5A },
            FileWith(Comm("1800", "0x581", tpdo: true) + Map("1A00", "1", "0x20010008")), new ListSink())
            .WithTimeoutAsync(ShortTimeout);
        sdoRange.Observations.Should().BeEmpty();
        sdoRange.Reason.Should().Contain("COB-ID");

        var unreadableCob = await Observe(FileWith(Comm("1800", "nope", tpdo: true) + Map("1A00", "1", "0x20010008")), new byte[] { 0x5A });
        unreadableCob.Observations.Should().BeEmpty();

        var blankCob = await Observe(FileWith(Comm("1800", "", tpdo: true) + Map("1A00", "1", "0x20010008")), new byte[] { 0x5A });
        blankCob.Observations.Should().BeEmpty();

        var missingSub = await Observe(FileWith(@"
[1800]
ParameterName=PDO communication
SubNumber=1
ObjectType=0x9
[1800sub0]
ParameterName=Highest sub-index supported
ObjectType=0x7
DataType=0x0005
AccessType=ro
DefaultValue=0
PDOMapping=0
" + Map("1A00", "1", "0x20010008")), new byte[] { 0x5A });
        missingSub.Observations.Should().BeEmpty();

        var bareCob = await Observe(FileWith(@"
[1800]
ParameterName=COB-ID
ObjectType=0x7
DataType=0x0007
AccessType=rw
DefaultValue=0x191
PDOMapping=0
" + Map("1A00", "1", "0x20010008")), new byte[] { 0x5A });
        bareCob.Observations.Should().BeEmpty();

        async Task Undecoded(string sections, string reason)
        {
            var result = await Observe(FileWith(Comm("1800", "$NODEID+0x180", tpdo: true) + sections), new byte[] { 0x5A });
            result.Observations.Should().ContainSingle();
            result.Observations[0].Decoded.Should().BeFalse();
            result.Observations[0].Reason.Should().Contain(reason);
        }

        await Undecoded("", "not in the peer description");
        await Undecoded(Map("1A00", "9", "0x20010008"), "at most 8");
        await Undecoded(Map("1A00", "2", "0x20010008"), "no sub-index 2");
        await Undecoded(Map("1A00", "1", "0x0"), "could not be read");
        await Undecoded(Map("1A00", "1", "nope"), "could not be read");
        await Undecoded(Map("1A00", "1", "0x20010004"), "byte-aligned");
        await Undecoded(Map("1A00", "2", "0x20000040", "0x20010008"), "longer than 8");
        await Undecoded(@"
[1A00]
ParameterName=TPDO1 mapping
SubNumber=1
ObjectType=0x9
[1A00sub1]
ParameterName=Mapping entry 1
ObjectType=0x7
DataType=0x0007
AccessType=rw
DefaultValue=0x20010008
PDOMapping=0
", "no sub-index 0");
        await Undecoded(@"
[1A00]
ParameterName=TPDO1 mapping
ObjectType=0x7
DataType=0x0005
AccessType=rw
DefaultValue=1
PDOMapping=0
", "no sub-index 1");

        var empty = await tool.ObserveForeignPdoAsync(Peer, Tpdo1, new byte[] { 0x5A },
            FileWith(Comm("1800", "$NODEID+0x180", tpdo: true) + @"
[1A00]
ParameterName=TPDO1 mapping
ObjectType=0x7
DataType=0x0005
AccessType=rw
DefaultValue=0
PDOMapping=0
"), new ListSink()).WithTimeoutAsync(ShortTimeout);
        empty.Observations.Should().ContainSingle();
        empty.Observations[0].Decoded.Should().BeTrue();
        empty.Observations[0].Origin.Should().Be(ForeignPdoMappingOrigin.DeviceDescription);
        empty.Observations[0].SignalsWritten.Should().Be(0);
    }

    [Fact]
    public async Task A_live_read_that_is_not_a_usable_COB_ID_or_mapping_falls_back_to_the_file()
    {
        var session = NewSession();
        using var peerBus = Open(session, 0);
        using var toolBus = Open(session, 1);
        using var responder = Open(session, 2);
        using var tool = CanOpen.OpenNode(toolBus, Tool);
        var file = PeerFile();
        tool.BindPeerDeviceDescription(Peer, file);
        byte[]? cob = null;
        string mapping = "ok";
        using var server = new ScriptedUploadServer(responder, Peer, (index, sub) =>
        {
            if (index is >= 0x1400 and <= 0x1403 or >= 0x1800 and <= 0x1803)
            {
                if (index == 0x1800 && sub == 1 && cob is not null) return cob;
                return null;
            }
            if (index != 0x1A00) return null;
            return mapping switch
            {
                "short-count" => sub == 0 ? Array.Empty<byte>() : null,
                "wide-count" => sub == 0 ? new byte[] { 9 } : null,
                "short-entry" => sub == 0 ? new byte[] { 1 } : new byte[] { 0x08 },
                "zero-entry" => sub == 0 ? new byte[] { 1 } : new byte[4],
                "odd-length" => sub == 0 ? new byte[] { 1 } : U32(0x20010004),
                "too-long" => sub switch
                {
                    0 => new byte[] { 2 },
                    1 => U32(0x20000040),
                    _ => U32(0x20010008),
                },
                "abort-entry" => sub == 0 ? new byte[] { 1 } : null,
                _ => null,
            };
        });

        async Task<ForeignPdoObserveResult> Observe(string mode, byte[]? liveCob = null)
        {
            mapping = mode;
            cob = liveCob;
            var sink = new ListSink();
            return await tool.ObserveForeignPdoAsync(Peer, Tpdo1, new byte[] { 0x5A, 0x00, 0x00 }, file, sink)
                .WithTimeoutAsync(ShortTimeout);
        }

        var extended = await Observe("ok", U32(0x20000191));
        extended.Observations.Should().BeEmpty();

        var stray = await Observe("ok", U32(0x800));
        stray.Observations.Should().BeEmpty();

        var shortCob = await Observe("ok", new byte[] { 0x91 });
        shortCob.Observations.Should().ContainSingle();
        shortCob.Observations[0].Origin.Should().Be(ForeignPdoMappingOrigin.DeviceDescription);
        shortCob.Observations[0].Decoded.Should().BeTrue();

        foreach (var mode in new[] { "short-count", "wide-count", "short-entry", "zero-entry", "odd-length", "too-long", "abort-entry" })
        {
            var result = await Observe(mode);
            result.Observations.Should().ContainSingle();
            result.Observations[0].Origin.Should().Be(ForeignPdoMappingOrigin.DeviceDescription);
            result.Observations[0].Decoded.Should().BeTrue();
        }

        cob = U32(0x000);
        var restrictedSink = new ListSink();
        var restricted = await tool.ObserveForeignPdoAsync(Peer, 0x000, new byte[] { 0x5A, 0x00, 0x00 },
            FileWith(Comm("1800", "0x000", tpdo: true) + Map("1A00", "1", "0x20010008")), restrictedSink)
            .WithTimeoutAsync(ShortTimeout);
        restricted.Observations.Should().BeEmpty();
        restrictedSink.Signals.Should().BeEmpty();
    }

    [Fact]
    public async Task Overlapping_observations_of_one_peer_both_read_the_live_mapping()
    {
        var session = NewSession();
        using var responder = Open(session, 0);
        using var toolBus = Open(session, 1);
        using var tool = CanOpen.OpenNode(toolBus, Tool);
        var file = PeerFile();
        tool.BindPeerDeviceDescription(Peer, file);
        using var entered = new SemaphoreSlim(0, 1);
        using var release = new SemaphoreSlim(0, 1);
        var held = 0;
        using var server = new ScriptedUploadServer(responder, Peer, (index, sub) =>
        {
            if (index == 0x1800 && sub == 1 && System.Threading.Interlocked.Exchange(ref held, 1) == 0)
            {
                entered.Release();
                release.Wait(ShortTimeout);
            }
            if (index is >= 0x1400 and <= 0x1403 or >= 0x1800 and <= 0x1803)
                return index == 0x1800 && sub == 1 ? U32(Tpdo1) : null;
            if (index != 0x1A00) return null;
            return sub == 0 ? new byte[] { 1 } : U32(0x20010008);
        });

        var firstSink = new ListSink();
        var secondSink = new ListSink();
        var first = tool.ObserveForeignPdoAsync(Peer, Tpdo1, new byte[] { 0x5A }, file, firstSink);
        entered.Wait(ShortTimeout).Should().BeTrue();
        var second = tool.ObserveForeignPdoAsync(Peer, Tpdo1, new byte[] { 0xA5 }, file, secondSink);
        await Task.Delay(50);
        release.Release();

        var firstResult = await first.WithTimeoutAsync(ShortTimeout);
        var secondResult = await second.WithTimeoutAsync(ShortTimeout);
        firstResult.Observations.Should().ContainSingle();
        firstResult.Observations[0].Origin.Should().Be(ForeignPdoMappingOrigin.LiveMapping);
        firstSink.Signals.Should().ContainSingle();
        firstSink.Signals[0].Value.Should().Equal(0x5A);
        secondResult.Observations.Should().ContainSingle();
        secondResult.Observations[0].Origin.Should().Be(ForeignPdoMappingOrigin.LiveMapping);
        secondSink.Signals.Should().ContainSingle();
        secondSink.Signals[0].Value.Should().Equal(0xA5);
    }

    [Fact]
    public async Task An_SDO_already_in_flight_falls_back_to_the_file_instead_of_throwing()
    {
        var session = NewSession();
        using var responder = Open(session, 0);
        using var toolBus = Open(session, 1);
        using var observer = Open(session, 2);
        using var tool = CanOpen.OpenNode(toolBus, Tool, new CanOpenNodeOptions { SdoTimeout = TimeSpan.FromSeconds(2) });
        var file = PeerFile();
        tool.BindPeerDeviceDescription(Peer, file);
        using var server = new ScriptedUploadServer(responder, Peer, (_, _) => null, (index, _) => index == 0x1001);
        var uploads = new SdoTap(observer, Peer);
        using var cancel = new CancellationTokenSource();
        var pending = tool.SdoUploadAsync(Peer, 0x1001, 0x00, cancel.Token);

        var deadline = DateTime.UtcNow + ShortTimeout;
        while (!uploads.Uploads.Any(pair => pair.Index == 0x1001))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("the in-flight upload was not sent");
            await Task.Delay(5);
        }

        var sink = new ListSink();
        var result = await tool.ObserveForeignPdoAsync(Peer, Tpdo1, new byte[] { 0x34, 0x12, 0x00 }, file, sink)
            .WithTimeoutAsync(ShortTimeout);
        cancel.Cancel();

        result.Observations.Should().ContainSingle();
        result.Observations[0].Origin.Should().Be(ForeignPdoMappingOrigin.DeviceDescription);
        result.Observations[0].Decoded.Should().BeTrue();
        sink.Signals.Should().ContainSingle();
        sink.Signals[0].Index.Should().Be(0x2000);
        Func<Task> wait = () => pending;
        await wait.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Observe_rejects_a_missing_file_a_missing_sink_and_a_payload_that_is_not_a_PDO()
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var tool = CanOpen.OpenNode(bus, Tool);
        var file = MinimalEds();
        var sink = new ListSink();

        var missingFile = () => tool.ObserveForeignPdoAsync(Peer, Tpdo1, new byte[] { 0x11 }, null!, sink);
        var missingSink = () => tool.ObserveForeignPdoAsync(Peer, Tpdo1, new byte[] { 0x11 }, file, null!);
        var badNode = () => tool.ObserveForeignPdoAsync(0, Tpdo1, new byte[] { 0x11 }, file, sink);
        var badCob = () => tool.ObserveForeignPdoAsync(Peer, 0x800, new byte[] { 0x11 }, file, sink);
        var longPayload = () => tool.ObserveForeignPdoAsync(Peer, Tpdo1, new byte[9], file, sink);

        await missingFile.Should().ThrowAsync<ArgumentNullException>();
        await missingSink.Should().ThrowAsync<ArgumentNullException>();
        await badNode.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await badCob.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await longPayload.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    private static CanOpenDeviceDescription MinimalEds() => CanOpenDeviceDescription.ParseEds(Eds(
        optional: "SupportedObjects=0",
        manufacturer: "SupportedObjects=0",
        sections: ""));

    private static string Dcf() => @"
[FileInfo]
FileName=peer.dcf
FileVersion=1
FileRevision=0
EDSVersion=4.0
Description=peer
CreationTime=10:00AM
CreationDate=09-26-2026
CreatedBy=CanKit.Pro tests
LastEDS=peer.eds

[DeviceComissioning]
NodeID=17
NodeName=peer
Baudrate=500
NetNumber=1
NetworkName=test
CANopenManager=0
" + DeviceInfo + @"
[OptionalObjects]
SupportedObjects=2
1=0x1800
2=0x1A00

[ManufacturerObjects]
SupportedObjects=0
" + Comm("1800", "$NODEID+0x180", tpdo: true) + @"
[1A00]
ParameterName=TPDO1 mapping
SubNumber=2
ObjectType=0x9
[1A00sub0]
ParameterName=Number of mapped objects
ObjectType=0x7
DataType=0x0005
AccessType=rw
DefaultValue=1
ParameterValue=1
PDOMapping=0
[1A00sub1]
ParameterName=Mapping entry 1
ObjectType=0x7
DataType=0x0007
AccessType=rw
DefaultValue=0x20010008
ParameterValue=0x20000010
PDOMapping=0
";

    private static string Eds(string optional, string manufacturer, string sections) => @"
[FileInfo]
FileName=peer.eds
FileVersion=1
FileRevision=0
EDSVersion=4.0
Description=peer
CreationTime=10:00AM
CreationDate=09-26-2026
CreatedBy=CanKit.Pro tests
" + DeviceInfo + @"
[MandatoryObjects]
SupportedObjects=3
1=0x1000
2=0x1001
3=0x1018

[1000]
ParameterName=Device type
ObjectType=0x7
DataType=0x0007
AccessType=ro
DefaultValue=0
PDOMapping=0

[1001]
ParameterName=Error register
ObjectType=0x7
DataType=0x0005
AccessType=ro
DefaultValue=0
PDOMapping=0

[1018]
ParameterName=Identity
SubNumber=2
ObjectType=0x9

[1018sub0]
ParameterName=Highest sub-index supported
ObjectType=0x7
DataType=0x0005
AccessType=ro
DefaultValue=1
PDOMapping=0

[1018sub1]
ParameterName=Vendor-ID
ObjectType=0x7
DataType=0x0007
AccessType=ro
DefaultValue=0
PDOMapping=0

[OptionalObjects]
" + optional + @"

[ManufacturerObjects]
" + manufacturer + @"
" + sections;

    private const string DeviceInfo = @"
[DeviceInfo]
VendorName=CanKit.Pro
VendorNumber=0x100
ProductName=Peer
ProductNumber=1
RevisionNumber=1
OrderCode=P
BaudRate_500=1
SimpleBootUpMaster=0
SimpleBootUpSlave=1
Granularity=8
DynamicChannelsSupported=0
GroupMessaging=0
NrOfRXPDO=4
NrOfTXPDO=4
LSS_Supported=0
";

    private static string Comm(string index, string cobId, bool tpdo)
    {
        if (!tpdo)
        {
            return $@"
[{index}]
ParameterName=PDO communication
SubNumber=3
ObjectType=0x9
[{index}sub0]
ParameterName=Highest sub-index supported
ObjectType=0x7
DataType=0x0005
AccessType=ro
DefaultValue=2
PDOMapping=0
[{index}sub1]
ParameterName=COB-ID
ObjectType=0x7
DataType=0x0007
AccessType=rw
DefaultValue={cobId}
PDOMapping=0
[{index}sub2]
ParameterName=Transmission type
ObjectType=0x7
DataType=0x0005
AccessType=rw
DefaultValue=254
PDOMapping=0
";
        }
        return $@"
[{index}]
ParameterName=PDO communication
SubNumber=6
ObjectType=0x9
[{index}sub0]
ParameterName=Highest sub-index supported
ObjectType=0x7
DataType=0x0005
AccessType=ro
DefaultValue=5
PDOMapping=0
[{index}sub1]
ParameterName=COB-ID
ObjectType=0x7
DataType=0x0007
AccessType=rw
DefaultValue={cobId}
PDOMapping=0
[{index}sub2]
ParameterName=Transmission type
ObjectType=0x7
DataType=0x0005
AccessType=rw
DefaultValue=254
PDOMapping=0
[{index}sub3]
ParameterName=Inhibit time
ObjectType=0x7
DataType=0x0006
AccessType=rw
DefaultValue=0
PDOMapping=0
[{index}sub5]
ParameterName=Event timer
ObjectType=0x7
DataType=0x0006
AccessType=rw
DefaultValue=0
PDOMapping=0
";
    }

    private static string Map(string index, string count, params string[] entries)
    {
        var text = new StringBuilder($@"
[{index}]
ParameterName=PDO mapping
SubNumber={entries.Length + 1}
ObjectType=0x9
[{index}sub0]
ParameterName=Number of mapped objects
ObjectType=0x7
DataType=0x0005
AccessType=rw
DefaultValue={count}
PDOMapping=0
");
        for (int i = 0; i < entries.Length; i++)
        {
            text.Append($@"
[{index}sub{i + 1}]
ParameterName=Mapping entry {i + 1}
ObjectType=0x7
DataType=0x0007
AccessType=rw
DefaultValue={entries[i]}
PDOMapping=0
");
        }
        return text.ToString();
    }

    private static CanOpenDeviceDescription FileWith(string sections)
    {
        bool mapping = sections.Contains("[1A00]", StringComparison.Ordinal);
        return CanOpenDeviceDescription.ParseEds(Eds(
            optional: mapping
                ? @"SupportedObjects=2
1=0x1800
2=0x1A00"
                : @"SupportedObjects=1
1=0x1800",
            manufacturer: "SupportedObjects=0",
            sections: sections));
    }

    private static byte[] U32(uint value)
        => new[] { (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24) };

    /// <summary>Answers SDO uploads for one server. A null payload is an abort. An empty payload
    /// is a segmented upload of no bytes. Anything else is an expedited upload of those bytes.</summary>
    private sealed class ScriptedUploadServer : IDisposable
    {
        private readonly ICanBus _bus;
        private readonly byte _server;
        private readonly Func<ushort, byte, byte[]?> _answer;
        private readonly Func<ushort, byte, bool>? _suppress;
        private readonly EventHandler<CanKit.Abstractions.API.Common.Definitions.CanReceiveDataView> _onFrame;

        public ScriptedUploadServer(ICanBus bus, byte server, Func<ushort, byte, byte[]?> answer,
            Func<ushort, byte, bool>? suppress = null)
        {
            _bus = bus;
            _server = server;
            _answer = answer;
            _suppress = suppress;
            _onFrame = OnFrame;
            _bus.FrameObserved += _onFrame;
        }

        public void Dispose() => _bus.FrameObserved -= _onFrame;

        private void OnFrame(object? sender, CanKit.Abstractions.API.Common.Definitions.CanReceiveDataView e)
        {
            var frame = e.CanFrame;
            if (frame.IsExtendedFrame || frame.IsRemoteFrame || (uint)frame.ID != CanOpenCobId.SdoRx(_server)) return;
            var data = frame.Data.ToArray();
            if (data.Length < 1) return;
            if ((data[0] & ~SdoFrames.ToggleBit) == SdoFrames.CcsUploadSegmentBase)
            {
                Reply(EmptySegment(data[0]));
                return;
            }
            if (data[0] != SdoFrames.CcsUploadInit || data.Length < 4) return;
            var index = (ushort)(data[1] | (data[2] << 8));
            var sub = data[3];
            if (_suppress?.Invoke(index, sub) == true) return;
            var payload = _answer(index, sub);
            if (payload is null)
                Reply(SdoFrames.BuildAbort(index, sub, (uint)SdoAbortCode.ObjectDoesNotExist));
            else if (payload.Length == 0)
                Reply(SegmentedEmptyInit(index, sub));
            else
                Reply(Expedited(index, sub, payload));
        }

        private void Reply(byte[] data)
            => _bus.Transmit(CanFrame.Classic(unchecked((int)CanOpenCobId.SdoTx(_server)), data));

        private static byte[] Expedited(ushort index, byte sub, byte[] payload)
        {
            int unused = 4 - payload.Length;
            var buf = new byte[8];
            buf[0] = (byte)(SdoFrames.ScsUploadInitExpeditedBase | ((unused & 0x03) << 2) | 0x03);
            buf[1] = (byte)index;
            buf[2] = (byte)(index >> 8);
            buf[3] = sub;
            payload.CopyTo(buf, 4);
            return buf;
        }

        private static byte[] SegmentedEmptyInit(ushort index, byte sub)
            => new byte[] { 0x40, (byte)index, (byte)(index >> 8), sub, 0, 0, 0, 0 };

        private static byte[] EmptySegment(byte request)
        {
            byte toggle = (byte)(request & SdoFrames.ToggleBit);
            return new byte[] { (byte)(SdoFrames.ContinueBit | (7 << 1) | toggle), 0, 0, 0, 0, 0, 0, 0 };
        }
    }

    private static string Var(string index, string dataType, string value) => $@"
[{index}]
ParameterName=Object {index}
ObjectType=0x7
DataType={dataType}
AccessType=rw
DefaultValue={value}
PDOMapping=1
";

    private sealed class ListSink : IForeignPdoSink
    {
        public List<ForeignPdoSignal> Signals { get; } = new();

        public void Write(ForeignPdoSignal signal) => Signals.Add(signal);
    }

    /// <summary>SDO upload initiates addressed to one server, in the order they were sent.</summary>
    private sealed class SdoTap
    {
        private readonly List<(ushort Index, byte Sub)> _uploads = new();
        private readonly object _gate = new();

        public SdoTap(ICanBus bus, byte server)
        {
            uint cobId = CanOpenCobId.SdoRx(server);
            bus.FrameObserved += (_, e) =>
            {
                var frame = e.CanFrame;
                if (frame.IsExtendedFrame || frame.IsRemoteFrame || (uint)frame.ID != cobId) return;
                var data = frame.Data.ToArray();
                if (data.Length < 4 || data[0] != 0x40) return;
                var index = (ushort)(data[1] | (data[2] << 8));
                lock (_gate) _uploads.Add((index, data[3]));
            };
        }

        public (ushort Index, byte Sub)[] Uploads
        {
            get
            {
                lock (_gate) return _uploads.ToArray();
            }
        }
    }
}
