using System;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Pdo;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

public partial class CanOpenForeignPdoObserveTests
{
    [Theory]
    [InlineData(254)]
    [InlineData(255)]
    public async Task Live_Mpdo_Mapping_Is_Read_Instead_Of_The_File_Mapping(byte mode)
    {
        var session = NewSession();
        using var peerBus = Open(session, 0);
        using var toolBus = Open(session, 1);
        var file = PeerFile();
        using var peer = CanOpen.OpenNode(peerBus, Peer, file);
        using var tool = CanOpen.OpenNode(toolBus, Tool);
        peer.Pdo().ConfigureMultiplexedTpdo(1, (MpdoAddressMode)mode, Tpdo1);
        tool.BindPeerDeviceDescription(Peer, file);
        var sink = new ListSink();
        var payload = new byte[] { mode == 254 ? Peer : (byte)0x80, 1, 0x20, 0, 42, 0, 0, 0 };
        var result = await tool.ObserveForeignPdoAsync(Peer, Tpdo1, payload, file, sink);
        result.Decoded.Should().BeTrue();
        result.Observations.Single().Origin.Should().Be(ForeignPdoMappingOrigin.LiveMapping);
        sink.Signals.Single().Index.Should().Be(0x2001);
    }

    [Fact]
    public void Description_Reports_Too_Many_Mapping_Slots_And_Reserved_Counts()
    {
        var entries = Enumerable.Repeat("0x00010001", 65).ToArray();
        var file = FileWith(Comm("1800", "$NODEID+0x180", true) + Map("1A00", "65", entries));
        var session = NewSession();
        using var bus = Open(session, 0);
        using var node = CanOpen.OpenNode(bus, Tool, file);
        node.ObjectDictionary.TryGet(0x1A00, 65, out _).Should().BeFalse();
        node.ObjectDictionary.ReadUnsigned(0x1A00, 0).Should().Be(0);
        (node.ObjectDictionary.ReadUnsigned(0x1800, 1) & CanOpenCobId.InvalidBit).Should().NotBe(0);
    }
    [Theory]
    [InlineData(0x1002, false)]
    [InlineData(0x1008, false)]
    [InlineData(0x1009, false)]
    [InlineData(0x100A, false)]
    [InlineData(0x1FA0, false)]
    [InlineData(0x1FFF, false)]
    [InlineData(0x2000, false)]
    [InlineData(0x1003, true)]
    [InlineData(0x1F9F, true)]
    public void Description_Only_Reports_Unimplemented_Behaviour_For_Unknown_Profile_Objects(ushort index, bool reported)
    {
        var file = CanOpenDeviceDescription.ParseEds(Eds($"SupportedObjects=1\n1=0x{index:X4}",
            "SupportedObjects=0", $"[{index:X4}]\nParameterName=Data\nObjectType=0x7\nDataType=0x0007\nAccessType=ro\nDefaultValue=0\nPDOMapping=0\n"));
        var session = NewSession();
        using var bus = Open(session, 0);
        using var node = CanOpen.OpenNode(bus, Tool, file);
        node.DeviceDescription!.Findings.Any(f => f.Index == index && f.Outcome == DeviceDescriptionOutcome.NotImplemented)
            .Should().Be(reported);
        node.ObjectDictionary.ContainsIndex(index).Should().BeTrue();
    }
}
