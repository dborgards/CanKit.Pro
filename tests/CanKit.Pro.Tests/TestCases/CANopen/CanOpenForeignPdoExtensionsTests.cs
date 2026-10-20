using System;
using System.Text;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Pdo;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

public partial class CanOpenForeignPdoObserveTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Offline_Peer_With_512_Pdos_Uses_One_Failure_Per_Snapshot(bool transportFailure)
    {
        var optional = new StringBuilder("SupportedObjects=1025\n");
        var sections = new StringBuilder();
        for (int n = 0; n < 512; n++)
        {
            string tx = (0x1800 + n).ToString("X4");
            string rx = (0x1400 + n).ToString("X4");
            optional.Append($"{2 * n + 1}=0x{tx}\n{2 * n + 2}=0x{rx}\n");
            sections.Append(Comm(tx, n == 511 ? "$NODEID+0x180" : "0x80000191", true));
            sections.Append(Comm(rx, "0x80000211", false));
        }
        optional.Append("1025=0x1BFF\n");
        sections.Append(Map("1BFF", "1", "0x20010008"));
        var file = CanOpenDeviceDescription.ParseEds(Eds(optional.ToString(), "SupportedObjects=0", sections.ToString()));
        using var service = new StarvedReaderBusService();
        var clock = new ManualTimeSource();
        using var tool = new CanOpenNode(service, Tool,
            new CanOpenNodeOptions { SdoTimeout = TimeSpan.FromMilliseconds(100) }, ownsService: false, clock);
        tool.BindPeerDeviceDescription(Peer, file);
        int requests = 0;
        async Task ExpireRequest()
        {
            await tool.PostToActorAsync(() => { }).ConfigureAwait(false);
            clock.Advance(TimeSpan.FromMilliseconds(101));
            await tool.PostToActorAsync(() => { }).ConfigureAwait(false);
        }
        service.ConfirmSendAsync = (frame, ct) =>
        {
            bool request = frame.ID == CanOpenCobId.SdoRx(Peer) && frame.Data.Span[0] == 0x40;
            if (!request) return Task.FromResult(new TxConfirmation { Confirmed = true });
            System.Threading.Interlocked.Increment(ref requests);
            if (!transportFailure) _ = Task.Run(ExpireRequest);
            return Task.FromResult(new TxConfirmation { Confirmed = !transportFailure });
        };
        for (int snapshot = 1; snapshot <= 2; snapshot++)
        {
            var sink = new ListSink();
            var result = await tool.ObserveForeignPdoAsync(Peer, Tpdo1, new byte[] { 42 }, file, sink)
                .WithTimeoutAsync(ShortTimeout);
            result.Decoded.Should().BeTrue();
            result.Observations.Should().ContainSingle();
            result.Observations[0].PdoNumber.Should().Be(512);
            result.Observations[0].Origin.Should().Be(ForeignPdoMappingOrigin.DeviceDescription);
            sink.Signals.Should().ContainSingle();
            sink.Signals[0].Value.Should().Equal(42);
            System.Threading.Volatile.Read(ref requests).Should().Be(snapshot);
        }
    }

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
    [Theory]
    [InlineData(254, 0, true, false)] // no SAM producer
    [InlineData(254, 2, true, false)] // another SAM producer
    [InlineData(254, 0x91, true, false)] // wrong addressing mode
    [InlineData(254, 0x11, true, true)]
    [InlineData(255, 0x82, false, false)] // another DAM consumer
    [InlineData(255, 0x91, false, true)]
    [InlineData(255, 0x80, false, true)] // broadcast
    public async Task Observe_Mpdo_Validates_Addressing_And_Additional_Pdo_Number(byte mode, byte address, bool tpdo, bool decoded)
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var tool = CanOpen.OpenNode(bus, Tool);
        var comm = tpdo ? "1804" : "1404";
        var map = tpdo ? "1A04" : "1604";
        var file = CanOpenDeviceDescription.ParseEds(Eds(
            optional: $"SupportedObjects=2\n1=0x{comm}\n2=0x{map}", manufacturer: "SupportedObjects=0",
            sections: Comm(comm, "0x20000050", tpdo) + Map(map, mode.ToString())));
        var sink = new ListSink();
        var result = await tool.ObserveForeignPdoAsync(Peer, 0x20000050,
            new byte[] { address, 0, 0x21, 3, 0xAA, 0, 0, 0 }, file, sink);
        result.Decoded.Should().Be(decoded);
        result.Observations.Should().ContainSingle();
        result.Observations[0].PdoNumber.Should().Be(5);
        if (decoded) sink.Signals.Should().ContainSingle(); else sink.Signals.Should().BeEmpty();
        var shortFrame = await tool.ObserveForeignPdoAsync(Peer, 0x20000050, new byte[7], file, new ListSink());
        shortFrame.Decoded.Should().BeFalse();
    }

}
