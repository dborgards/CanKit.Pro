using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Pdo;
using CanKit.Pro.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

/// <summary>
/// Observing a PDO that belongs to another node (#163): the payload is split with that node's
/// live mapping record when the peer file lists the sub-indexes, and with the file when the
/// live read aborts or times out. The bytes go to the caller's sink. This node's object
/// dictionary is left alone, and a frame that is not one of its RPDOs still is.
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
            ((ushort)0x1A00, (byte)0x00),
            ((ushort)0x1A00, (byte)0x01),
            ((ushort)0x1A00, (byte)0x02));
        uploads.Uploads.Should().NotContain(pair => pair.Index == 0x1800 || pair.Index == 0x1400);
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
    public async Task A_live_count_that_names_an_unlisted_subindex_falls_back_without_reading_it()
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
        var uploads = new SdoTap(observer, Peer);
        var sink = new ListSink();

        var result = await tool.ObserveForeignPdoAsync(Peer, Tpdo1, new byte[] { 0xAB, 0xCD }, narrow, sink)
            .WithTimeoutAsync(ShortTimeout);

        result.Observations[0].Origin.Should().Be(ForeignPdoMappingOrigin.DeviceDescription);
        sink.Signals.Should().ContainSingle();
        sink.Signals[0].Index.Should().Be(0x2000);
        sink.Signals[0].Value.Should().Equal(0xAB, 0xCD);
        uploads.Uploads.Should().Contain(pair => pair.Index == 0x1A00 && pair.Sub == 0);
        uploads.Uploads.Should().NotContain(pair => pair.Sub == 2);
    }

    [Fact]
    public async Task A_mapping_record_the_file_does_not_list_is_not_read_over_SDO()
    {
        var session = NewSession();
        using var peerBus = Open(session, 0);
        using var toolBus = Open(session, 1);
        using var observer = Open(session, 2);
        using var peer = CanOpen.OpenNode(peerBus, Peer);
        using var tool = CanOpen.OpenNode(toolBus, Tool);
        await WaitForStateAsync(peer, NmtState.PreOperational);
        var file = CanOpenDeviceDescription.ParseEds(Eds(
            optional: @"SupportedObjects=1
1=0x1800",
            manufacturer: "SupportedObjects=0",
            sections: Comm("1800", "$NODEID+0x180", tpdo: true)));
        var uploads = new SdoTap(observer, Peer);
        var sink = new ListSink();

        var result = await tool.ObserveForeignPdoAsync(Peer, Tpdo1, new byte[] { 0x11, 0x22 }, file, sink)
            .WithTimeoutAsync(ShortTimeout);

        result.Decoded.Should().BeFalse();
        result.Observations.Should().ContainSingle();
        result.Observations[0].Reason.Should().Contain("not in the peer description");
        sink.Signals.Should().BeEmpty();

        await tool.SdoUploadAsync(Peer, 0x1001, 0x00).WithTimeoutAsync(ShortTimeout);
        uploads.Uploads.Should().Contain(pair => pair.Index == 0x1001);
        uploads.Uploads.Should().NotContain(pair =>
            pair.Index == 0x1A00 || pair.Index == 0x1800 || pair.Index == 0x1600 || pair.Index == 0x1400);
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
        var uploads = new SdoTap(observer, Peer);
        var sink = new ListSink();

        var unknown = await tool.ObserveForeignPdoAsync(Peer, 0x192, new byte[] { 0x11, 0x22 }, file, sink)
            .WithTimeoutAsync(ShortTimeout);
        unknown.Decoded.Should().BeFalse();
        unknown.Observations.Should().BeEmpty();
        unknown.Reason.Should().Contain("COB-ID");

        var invalid = await tool.ObserveForeignPdoAsync(Peer, Tpdo1, new byte[] { 0x11, 0x22 }, file, sink)
            .WithTimeoutAsync(ShortTimeout);
        invalid.Decoded.Should().BeFalse();
        invalid.Observations.Should().BeEmpty();
        sink.Signals.Should().BeEmpty();

        await tool.SdoUploadAsync(Peer, 0x1001, 0x00).WithTimeoutAsync(ShortTimeout);
        uploads.Uploads.Should().OnlyContain(pair => pair.Index == 0x1001);
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
        var text = $@"
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
";
        for (int i = 0; i < entries.Length; i++)
        {
            text += $@"
[{index}sub{i + 1}]
ParameterName=Mapping entry {i + 1}
ObjectType=0x7
DataType=0x0007
AccessType=rw
DefaultValue={entries[i]}
PDOMapping=0
";
        }
        return text;
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
