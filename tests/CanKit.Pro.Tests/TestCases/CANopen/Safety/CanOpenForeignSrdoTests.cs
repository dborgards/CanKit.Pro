using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
/// CiA DSP 304): live before file, the pair checked, the plain data split into the sink
/// (FR-CO-046).</summary>
public class CanOpenForeignSrdoTests : IClassFixture<VirtualAdapterFixture>
{
    private const byte Master = 0x01;
    private const byte Device = 0x05;
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);

    private static ICanBus Open(string session, int channel) => CanBus.Open(
        $"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(VirtualAdapterFixture.Bitrate));

    private static string PeerFileText()
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestCases", "CANopen", "Fixtures", "safety.dcf"));

    private static CanOpenDeviceDescription PeerFile() => CanOpenDeviceDescription.ParseDcf(PeerFileText());

    /// <summary>Replaces the first <paramref name="old"/> after <paramref name="section"/> in the fixture.</summary>
    private static CanOpenDeviceDescription PeerFilePatched(string section, string old, string replacement)
    {
        var text = PeerFileText();
        int at = text.IndexOf(section, StringComparison.Ordinal);
        at.Should().BeGreaterThan(-1, section);
        int hit = text.IndexOf(old, at, StringComparison.Ordinal);
        hit.Should().BeGreaterThan(-1, old);
        return CanOpenDeviceDescription.ParseDcf(text[..hit] + replacement + text[(hit + old.Length)..]);
    }

    /// <summary>The fixture without the sections 1381h:05 .. 1381h:16: a record that declares at most two mapped objects.</summary>
    private static CanOpenDeviceDescription PeerFileWithShortSrdoMapping()
    {
        var text = PeerFileText();
        for (int sub = 5; sub <= 16; sub++)
        {
            string header = $"[1381sub{sub}]";
            int at = text.IndexOf(header, StringComparison.Ordinal);
            if (at < 0) continue;
            int next = text.IndexOf("\n[", at + header.Length, StringComparison.Ordinal);
            text = text[..at] + (next < 0 ? "" : text[(next + 1)..]);
        }
        return CanOpenDeviceDescription.ParseDcf(text);
    }

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

    /// <summary>A deleted record keeps its COB-IDs, and another SRDO may take them over: the
    /// device refuses only the ids of an existing SRDO (§8.4.2.2). The pair belongs to the SRDO
    /// that exists, so a record whose live direction is 0 does not match.</summary>
    [Fact]
    public async Task A_Deleted_Record_That_Kept_The_Cob_Id_Does_Not_Match()
    {
        var session = VirtualAdapterFixture.NewSession("co-foreign-srdo");
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var master = CanOpen.OpenNode(busA, Master);
        using var device = CanOpen.OpenNode(busB, Device, new CanOpenNodeOptions { SrdoCount = 2, WritableCommunicationParameters = true });
        device.ObjectDictionary.AddU16(0x2000, 0x00, 0x1234);
        device.ObjectDictionary.AddU8(0x2001, 0x00, 0x5A);
        var safety = device.Safety();
        safety.ConfigureSrdoProducer(1, new SrdoMapping().Add(0x2001, 0x00, 8), TimeSpan.FromMilliseconds(30), 0x109, 0x10A);
        safety.DeleteSrdo(1);
        safety.ConfigureSrdoProducer(2, new SrdoMapping().Add(0x2000, 0x00, 16), TimeSpan.FromMilliseconds(30), 0x109, 0x10A);
        device.ObjectDictionary.ReadUnsigned(0x1301, 5).Should().Be(0x109u, "the deleted record kept its COB-ID 1");
        master.BindPeerDeviceDescription(Device, PeerFile());
        var sink = new ListSink();
        var result = await master.Safety().ObserveForeignSrdoAsync(Device, 0x109,
            new byte[] { 0x34, 0x12 }, new byte[] { 0xCB, 0xED }, PeerFile(), sink).WithTimeoutAsync(ShortTimeout);
        result.Observation!.PdoNumber.Should().Be(2, "SRDO 2 is the one that exists on 0x109");
        result.Observation.Decoded.Should().BeTrue(result.Observation.Reason);
        result.Observation.Origin.Should().Be(ForeignPdoMappingOrigin.LiveMapping);
        sink.Signals.Should().ContainSingle();
        sink.Signals[0].Index.Should().Be((ushort)0x2000);
        sink.Signals[0].Value.Should().Equal(0x34, 0x12);
    }

    /// <summary>The file is the fallback for the direction as for the COB-ID: a record the file
    /// declares deleted does not match when the peer does not answer.</summary>
    [Fact]
    public async Task A_Record_The_File_Declares_Deleted_Does_Not_Match_When_The_Peer_Does_Not_Answer()
    {
        var session = VirtualAdapterFixture.NewSession("co-foreign-srdo");
        using var busA = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, Master, new CanOpenNodeOptions { SdoTimeout = TimeSpan.FromMilliseconds(100) });
        var file = PeerFilePatched("[1301sub1]", "ParameterValue=1", "ParameterValue=0");
        master.BindPeerDeviceDescription(Device, file);
        var sink = new ListSink();
        var result = await master.Safety().ObserveForeignSrdoAsync(Device, 0x109,
            new byte[] { 0x34, 0x12, 0x5A }, new byte[] { 0xCB, 0xED, 0xA5 }, file, sink).WithTimeoutAsync(TimeSpan.FromSeconds(30));
        result.Observation.Should().BeNull(result.Observation?.Reason);
        result.Reason.Should().NotBeNull();
        sink.Signals.Should().BeEmpty();
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

    [Fact]
    public async Task A_Live_Record_On_Another_Id_Is_Not_Overridden_By_The_File()
    {
        var session = VirtualAdapterFixture.NewSession("co-foreign-srdo");
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var master = CanOpen.OpenNode(busA, Master);
        using var device = OpenDeviceDivergingFromFile(busB);
        master.BindPeerDeviceDescription(Device, PeerFile());
        var sink = new ListSink();
        // 0x109 is what the file says for SRDO 1; the device answers that it is on 0x111 now.
        var result = await master.Safety().ObserveForeignSrdoAsync(Device, 0x109,
            new byte[] { 0x34, 0x12, 0x5A }, new byte[] { 0xCB, 0xED, 0xA5 }, PeerFile(), sink).WithTimeoutAsync(ShortTimeout);
        result.Observation.Should().BeNull();
        result.Reason.Should().Contain("no SRDO");
        sink.Signals.Should().BeEmpty();
    }

    [Fact]
    public async Task A_Live_Mapping_Beyond_The_Slots_The_File_Declares_Is_Read_In_Full()
    {
        var session = VirtualAdapterFixture.NewSession("co-foreign-srdo");
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var master = CanOpen.OpenNode(busA, Master);
        using var device = CanOpen.OpenNode(busB, PeerFile(), new CanOpenNodeOptions { WritableCommunicationParameters = true });
        device.ObjectDictionary.AddU8(0x2002, 0x00, 0, OdAccess.ReadOnly);
        device.Safety().ConfigureSrdoProducer(1, new SrdoMapping().Add(0x2000, 0x00, 16).Add(0x2001, 0x00, 8).Add(0x2002, 0x00, 8),
            TimeSpan.FromMilliseconds(25), 0x111, 0x112);
        device.Safety().CommitSafetyConfiguration();
        // The master's bound description knows 1381h only up to 1381h:04: two objects, the device
        // has three. The gate implies the SRDO record's slots all the same (§8.4.2.2), so the live
        // mapping is read in full and the safety data is split by what the device uses.
        var shortFile = PeerFileWithShortSrdoMapping();
        shortFile.Contains(0x1381, 0x05).Should().BeFalse();
        master.BindPeerDeviceDescription(Device, shortFile);
        var sink = new ListSink();
        var result = await master.Safety().ObserveForeignSrdoAsync(Device, 0x111,
            new byte[] { 1, 2, 3, 4 }, new byte[] { 0xFE, 0xFD, 0xFC, 0xFB }, PeerFile(), sink).WithTimeoutAsync(ShortTimeout);
        result.Observation.Should().NotBeNull(result.Reason);
        result.Observation!.Decoded.Should().BeTrue(result.Observation.Reason);
        result.Observation.Origin.Should().Be(ForeignPdoMappingOrigin.LiveMapping);
        sink.Signals.Select(s => s.Index).Should().Equal((ushort)0x2000, (ushort)0x2001, (ushort)0x2002);
        sink.Signals[2].Value.Should().Equal(4);
    }

    [Fact]
    public async Task A_Dummy_Entry_In_The_Mapping_Is_Not_Decoded()
    {
        var session = VirtualAdapterFixture.NewSession("co-foreign-srdo");
        using var busA = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, Master, new CanOpenNodeOptions { SdoTimeout = TimeSpan.FromMilliseconds(100) });
        var file = PeerFilePatched("[1381sub1]", "0x20000010", "0x00050010");
        master.BindPeerDeviceDescription(Device, file);
        var sink = new ListSink();
        var result = await master.Safety().ObserveForeignSrdoAsync(Device, 0x109,
            new byte[] { 0x34, 0x12, 0x5A }, new byte[] { 0xCB, 0xED, 0xA5 }, file, sink).WithTimeoutAsync(TimeSpan.FromSeconds(30));
        result.Observation!.Decoded.Should().BeFalse();
        result.Observation.Reason.Should().Contain("dummy");
        sink.Signals.Should().BeEmpty();
    }

    [Fact]
    public async Task A_Cob_Id_Word_With_A_Bit_Above_The_Id_Does_Not_Match()
    {
        var session = VirtualAdapterFixture.NewSession("co-foreign-srdo");
        using var busA = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, Master, new CanOpenNodeOptions { SdoTimeout = TimeSpan.FromMilliseconds(100) });
        var file = PeerFilePatched("[1301sub5]", "ParameterValue=0x109", "ParameterValue=0x80000109");
        master.BindPeerDeviceDescription(Device, file);
        var result = await master.Safety().ObserveForeignSrdoAsync(Device, 0x109,
            new byte[] { 0x34, 0x12, 0x5A }, new byte[] { 0xCB, 0xED, 0xA5 }, file, new ListSink()).WithTimeoutAsync(TimeSpan.FromSeconds(30));
        result.Observation.Should().BeNull();
        result.Reason.Should().Contain("no SRDO");
    }

    [Fact]
    public async Task An_Over_Long_Frame_Names_The_Parameter()
    {
        var session = VirtualAdapterFixture.NewSession("co-foreign-srdo");
        using var busA = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, Master);
        var nine = new byte[9];
        var ex1 = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => master.Safety().ObserveForeignSrdoAsync(Device, 0x109, nine, new byte[8], PeerFile(), new ListSink()));
        ex1.ParamName.Should().Be("frame1");
        var ex2 = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => master.Safety().ObserveForeignSrdoAsync(Device, 0x109, new byte[8], nine, PeerFile(), new ListSink()));
        ex2.ParamName.Should().Be("frame2");
    }
}
