using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Safety;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Safety;

/// <summary>Decoding a peer's SRDO pair from its live records (FR-CO-030's rules applied to
/// CiA DSP 304): live before file, the pair checked, the plain data split into the sink.</summary>
public class CanOpenForeignSrdoTests : IClassFixture<VirtualAdapterFixture>
{
    private const byte Master = 0x01;
    private const byte Device = 0x05;
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);

    private static ICanBus Open(string session, int channel) => CanBus.Open(
        $"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(VirtualAdapterFixture.Bitrate));

    private static CanOpenDeviceDescription PeerFile()
        => CanOpenDeviceDescription.ParseDcf(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestCases", "CANopen", "Fixtures", "safety.dcf")));

    private sealed class ListSink : IForeignPdoSink
    {
        public readonly List<ForeignPdoSignal> Signals = new();
        public void Write(ForeignPdoSignal signal) => Signals.Add(signal);
    }

    /// <summary>The device from the file, but with its live SRDO 1 moved to 0x111/0x112 and a
    /// different mapping than the file — live must win.</summary>
    private static ICanOpenNode OpenDeviceDivergingFromFile(ICanBus bus)
    {
        var device = CanOpen.OpenNode(bus, PeerFile(), new CanOpenNodeOptions { WritableCommunicationParameters = true });
        device.ObjectDictionary.AddU32(0x2002, 0x00, 0, OdAccess.ReadOnly);
        device.Safety().ConfigureSrdoProducer(1, new SrdoMapping().Add(0x2002, 0x00, 32), TimeSpan.FromMilliseconds(25), 0x111, 0x112);
        device.Safety().CommitSafetyConfiguration();
        return device;
    }

    [Fact]
    public async Task Decodes_With_The_Live_Record_Ahead_Of_The_File()
    {
        var session = VirtualAdapterFixture.NewSession("co-foreign-srdo");
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var master = CanOpen.OpenNode(busA, Master);
        using var device = OpenDeviceDivergingFromFile(busB);
        master.BindPeerDeviceDescription(Device, PeerFile());
        var sink = new ListSink();
        var result = await master.Safety().ObserveForeignSrdoAsync(Device, 0x111,
            new byte[] { 0x78, 0x56, 0x34, 0x12 }, new byte[] { 0x87, 0xA9, 0xCB, 0xED }, PeerFile(), sink).WithTimeoutAsync(ShortTimeout);
        result.Observation.Should().NotBeNull(result.Reason);
        result.Observation!.Decoded.Should().BeTrue(result.Observation.Reason);
        result.Observation.Kind.Should().Be(ForeignPdoKind.Srdo);
        result.Observation.PdoNumber.Should().Be(1);
        result.Observation.Origin.Should().Be(ForeignPdoMappingOrigin.LiveMapping);
        sink.Signals.Should().ContainSingle();
        sink.Signals[0].Index.Should().Be((ushort)0x2002);
        sink.Signals[0].Value.Should().Equal(0x78, 0x56, 0x34, 0x12);
        sink.Signals[0].Kind.Should().Be(ForeignPdoKind.Srdo);
        master.ObjectDictionary.ContainsIndex(0x2002).Should().BeFalse("nothing is written here");
    }

    [Fact]
    public async Task Falls_Back_To_The_File_When_The_Peer_Does_Not_Answer()
    {
        var session = VirtualAdapterFixture.NewSession("co-foreign-srdo");
        using var busA = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, Master, new CanOpenNodeOptions { SdoTimeout = TimeSpan.FromMilliseconds(100) });
        master.BindPeerDeviceDescription(Device, PeerFile());
        var sink = new ListSink();
        var result = await master.Safety().ObserveForeignSrdoAsync(Device, 0x109,
            new byte[] { 0x34, 0x12, 0x5A }, new byte[] { 0xCB, 0xED, 0xA5 }, PeerFile(), sink).WithTimeoutAsync(TimeSpan.FromSeconds(30));
        result.Observation!.Decoded.Should().BeTrue(result.Observation.Reason);
        result.Observation.Origin.Should().Be(ForeignPdoMappingOrigin.DeviceDescription);
        sink.Signals.Should().HaveCount(2);
        sink.Signals[0].Index.Should().Be((ushort)0x2000);
        sink.Signals[0].Value.Should().Equal(0x34, 0x12);
        sink.Signals[1].Index.Should().Be((ushort)0x2001);
        sink.Signals[1].Value.Should().Equal(0x5A);
    }

    [Fact]
    public async Task A_Bad_Pair_Or_An_Unknown_Id_Is_Not_Decoded()
    {
        var session = VirtualAdapterFixture.NewSession("co-foreign-srdo");
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var master = CanOpen.OpenNode(busA, Master);
        using var device = OpenDeviceDivergingFromFile(busB);
        master.BindPeerDeviceDescription(Device, PeerFile());
        var sink = new ListSink();
        var mismatch = await master.Safety().ObserveForeignSrdoAsync(Device, 0x111,
            new byte[] { 0x78, 0x56, 0x34, 0x12 }, new byte[] { 0x87, 0xA9, 0xCB, 0xEC }, PeerFile(), sink).WithTimeoutAsync(ShortTimeout);
        mismatch.Observation!.Decoded.Should().BeFalse();
        mismatch.Observation.Reason.Should().Contain("inverse");
        var unknown = await master.Safety().ObserveForeignSrdoAsync(Device, 0x141,
            new byte[] { 1 }, new byte[] { 0xFE }, PeerFile(), sink).WithTimeoutAsync(ShortTimeout);
        unknown.Observation.Should().BeNull();
        unknown.Reason.Should().Contain("no SRDO");
        sink.Signals.Should().BeEmpty();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => master.Safety().ObserveForeignSrdoAsync(Device, 0x112, new byte[0], new byte[0], PeerFile(), sink));
    }
}
