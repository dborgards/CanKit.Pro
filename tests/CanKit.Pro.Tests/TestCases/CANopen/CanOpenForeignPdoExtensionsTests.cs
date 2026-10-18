using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Pdo;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

public partial class CanOpenForeignPdoObserveTests
{
    [Fact]
    public async Task Observe_Extended_Bit_Mapping_And_Mpdo_From_The_File()
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var tool = CanOpen.OpenNode(bus, Tool);
        var file = FileWith(Comm("1800", "0x20123456", true)
            + Map("1A00", "3", "0x20000001", "0x00010001", "0x20010008"));
        var sink = new ListSink();
        // No bound peer description: both COB-ID and mapping fall back without bus traffic.
        var result = await tool.ObserveForeignPdoAsync(Peer, 0x20123456, new byte[] { 0xA9, 2 }, file, sink);
        result.Decoded.Should().BeTrue();
        sink.Signals.Should().HaveCount(2);
        sink.Signals[0].BitLength.Should().Be(1);
        sink.Signals[0].Value.Should().Equal(1);
        sink.Signals[1].BitLength.Should().Be(8);
        sink.Signals[1].Value.Should().Equal(0xAA);
        file = FileWith(Comm("1800", "$NODEID+0x180", true) + Map("1A00", "254"));
        sink = new ListSink();
        result = await tool.ObserveForeignPdoAsync(Peer, Tpdo1,
            new byte[] { Peer, 0x00, 0x21, 3, 0x34, 0x12, 0, 0 }, file, sink);
        result.Decoded.Should().BeTrue();
        sink.Signals.Should().ContainSingle();
        sink.Signals[0].Index.Should().Be(0x2100);
        sink.Signals[0].SubIndex.Should().Be(3);
        sink.Signals[0].Value.Should().Equal(0x34, 0x12, 0, 0);
    }
}
