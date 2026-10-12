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
        => CanOpenDeviceDescription.ParseDcf(PatchedText(PeerFileText(), section, old, replacement));

    /// <summary>Replaces the first <paramref name="old"/> after <paramref name="section"/> in <paramref name="text"/>.</summary>
    private static string PatchedText(string text, string section, string old, string replacement)
    {
        int at = text.IndexOf(section, StringComparison.Ordinal);
        at.Should().BeGreaterThan(-1, section);
        int hit = text.IndexOf(old, at, StringComparison.Ordinal);
        hit.Should().BeGreaterThan(-1, old);
        return text[..hit] + replacement + text[(hit + old.Length)..];
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

    /// <summary>The fixture without the section <paramref name="header"/>.</summary>
    private static CanOpenDeviceDescription PeerFileWithout(string header)
    {
        var text = PeerFileText();
        int at = text.IndexOf(header, StringComparison.Ordinal);
        at.Should().BeGreaterThan(-1, header);
        int next = text.IndexOf("\n[", at + header.Length, StringComparison.Ordinal);
        return CanOpenDeviceDescription.ParseDcf(text[..at] + (next < 0 ? "" : text[(next + 1)..]));
    }

    /// <summary>The fixture without every section of the object <paramref name="index"/>: its
    /// header and all its sub-index headers.</summary>
    private static string TextWithoutObject(string text, string index)
    {
        foreach (var header in new[] { $"[{index}]", $"[{index}sub" })
        {
            int at;
            while ((at = text.IndexOf(header, StringComparison.Ordinal)) >= 0)
            {
                int next = text.IndexOf("\n[", at + header.Length, StringComparison.Ordinal);
                text = text[..at] + (next < 0 ? "" : text[(next + 1)..]);
            }
        }
        return text;
    }

    /// <summary>Every node of these tests runs on a virtual clock: an SDO client deadline runs on
    /// the node's time source, which only the test moves, so a transfer is not ended by a host
    /// stall (fix report, round 15 addendum; #92).</summary>
    private static CanOpenNode OpenOnVirtualClock(ICanBus bus, byte nodeId, CanOpenNodeOptions options, CanOpenDeviceDescription? description = null)
        => new(new CanKit.Pro.RawCan.CanBusService(bus), nodeId, options, ownsService: true, new ManualTimeSource(), description);

    /// <summary>A master on a virtual clock for the tests in which the peer does not answer: every
    /// SDO request it sends to <see cref="Device"/> is seen on a third channel, and the clock is
    /// then moved past the SDO timeout, so that request's deadline fires. The deadline is armed on
    /// the actor before the request is handed to the bus, so the move always finds it; nothing is
    /// waited for on the wall clock.</summary>
    private sealed class UnansweredPeerRig : IDisposable
    {
        private static readonly TimeSpan SdoTimeout = TimeSpan.FromMilliseconds(100);
        private readonly ManualTimeSource _clock = new();
        private readonly ICanBus _observer;

        public UnansweredPeerRig(string session, ICanBus masterBus)
        {
            Master = new CanOpenNode(new CanKit.Pro.RawCan.CanBusService(masterBus), CanOpenForeignSrdoTests.Master,
                new CanOpenNodeOptions { SdoTimeout = SdoTimeout }, ownsService: true, _clock);
            _observer = Open(session, 3);
            _observer.FrameObserved += (_, e) =>
            {
                var f = e.CanFrame;
                if (f.IsExtendedFrame || f.IsRemoteFrame || (uint)f.ID != CanOpenCobId.SdoRx(Device) || f.Data.Length < 1) return;
                if (f.Data.Span[0] == 0x80) return; // the client's own abort after a timeout
                _ = Task.Run(async () =>
                {
                    // Settle, move, settle: the actor sees the moved clock on its next turn.
                    await Master.PostToActorAsync(() => { }).ConfigureAwait(false);
                    _clock.Advance(SdoTimeout + TimeSpan.FromMilliseconds(1));
                    await Master.PostToActorAsync(() => { }).ConfigureAwait(false);
                });
            };
        }

        public CanOpenNode Master { get; }

        public void Dispose()
        {
            _observer.Dispose();
            Master.Dispose();
        }
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
        var file = PeerFile();
        var device = OpenOnVirtualClock(bus, file.NodeId!.Value, new CanOpenNodeOptions { WritableCommunicationParameters = true }, file);
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
        using var master = OpenOnVirtualClock(busA, Master, new CanOpenNodeOptions());
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

    /// <summary>The live path applies the rule the file path applies (§8.4.2.2, 1381h–13C0h: the
    /// even sub-index carries the inverted object of the odd one before it): every slot up to the
    /// count is read, and a live even slot that was read and differs from its odd slot makes the
    /// pair not decoded, with a reason naming the slot. The live record was read, so it wins: the
    /// file is no fallback for it. The device's own validator would refuse such a record, so the
    /// test writes it past the validator (WriteRawUnchecked).</summary>
    [Fact]
    public async Task A_Live_Mapping_Whose_Inverted_Slot_Does_Not_Repeat_Its_Plain_Slot_Is_Not_Decoded()
    {
        var session = VirtualAdapterFixture.NewSession("co-foreign-srdo");
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var master = OpenOnVirtualClock(busA, Master, new CanOpenNodeOptions());
        using var device = OpenDeviceDivergingFromFile(busB);
        device.ObjectDictionary.WriteRawUnchecked(0x1381, 0x02, ObjectDictionary.EncodeU32(0x2000_0010));
        master.BindPeerDeviceDescription(Device, PeerFile());
        var sink = new ListSink();
        var result = await master.Safety().ObserveForeignSrdoAsync(Device, 0x111,
            new byte[] { 0x78, 0x56, 0x34, 0x12 }, new byte[] { 0x87, 0xA9, 0xCB, 0xED }, PeerFile(), sink).WithTimeoutAsync(ShortTimeout);
        result.Observation.Should().NotBeNull(result.Reason);
        result.Observation!.Decoded.Should().BeFalse();
        result.Observation.Origin.Should().Be(ForeignPdoMappingOrigin.LiveMapping, "the live record was read; the file is no fallback for it");
        result.Observation.Reason.Should().Contain("entry 2").And.Contain("does not repeat entry 1");
        sink.Signals.Should().BeEmpty();
    }

    /// <summary>A live even slot that cannot be read is treated like an unreadable odd slot: the
    /// live mapping counts as not read, and the whole mapping comes from the file. The slot is
    /// removed on the device, whose SDO server then aborts its upload.</summary>
    [Fact]
    public async Task A_Live_Inverted_Slot_That_Cannot_Be_Read_Falls_Back_To_The_File()
    {
        var session = VirtualAdapterFixture.NewSession("co-foreign-srdo");
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var master = OpenOnVirtualClock(busA, Master, new CanOpenNodeOptions());
        using var device = OpenDeviceDivergingFromFile(busB);
        device.ObjectDictionary.Remove(0x1381, 0x02).Should().BeTrue();
        master.BindPeerDeviceDescription(Device, PeerFile());
        var sink = new ListSink();
        var result = await master.Safety().ObserveForeignSrdoAsync(Device, 0x111,
            new byte[] { 0x78, 0x56, 0x34, 0x12 }, new byte[] { 0x87, 0xA9, 0xCB, 0xED }, PeerFile(), sink).WithTimeoutAsync(ShortTimeout);
        result.Observation.Should().NotBeNull(result.Reason);
        result.Observation!.Decoded.Should().BeTrue(result.Observation.Reason);
        result.Observation.Origin.Should().Be(ForeignPdoMappingOrigin.DeviceDescription, "the live mapping could not be read in full");
        sink.Signals.Select(s => s.Index).Should().Equal((ushort)0x2000, (ushort)0x2001);
    }

    /// <summary>The live mapping must have the widths of its types: sub-index 00h exactly one byte
    /// (UNSIGNED8), every slot exactly four (UNSIGNED32). A response of another length is not cut
    /// or read by its low bytes: the live mapping counts as not read, and the file decides. The
    /// device's records are re-declared past the node's guard (ObjectDictionary.Declare) so that
    /// its SDO server answers with those lengths.</summary>
    [Theory]
    [InlineData("2-byte count")]
    [InlineData("5-byte slot")]
    [InlineData("3-byte slot")]
    [InlineData("5-byte inverted slot")]
    public async Task A_Live_Mapping_Value_Of_Another_Length_Falls_Back_To_The_File(string fault)
    {
        var session = VirtualAdapterFixture.NewSession("co-foreign-srdo");
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var master = OpenOnVirtualClock(busA, Master, new CanOpenNodeOptions());
        using var device = OpenDeviceDivergingFromFile(busB);
        var od = device.ObjectDictionary;
        switch (fault)
        {
            case "2-byte count": od.Declare(0x1381, 0x00, OdDataType.Unsigned16, OdAccess.ReadWrite, new byte[] { 0x02, 0x00 }, pdoMappable: false); break;
            case "5-byte slot": od.Declare(0x1381, 0x01, OdDataType.Domain, OdAccess.ReadWrite, new byte[] { 0x20, 0x00, 0x02, 0x20, 0x00 }, pdoMappable: false); break;
            case "3-byte slot": od.Declare(0x1381, 0x01, OdDataType.Domain, OdAccess.ReadWrite, new byte[] { 0x20, 0x00, 0x02 }, pdoMappable: false); break;
            // Its low four bytes repeat 1381h:01 (20020020h) exactly; only the width is wrong.
            default: od.Declare(0x1381, 0x02, OdDataType.Domain, OdAccess.ReadWrite, new byte[] { 0x20, 0x00, 0x02, 0x20, 0x00 }, pdoMappable: false); break;
        }
        master.BindPeerDeviceDescription(Device, PeerFile());
        var sink = new ListSink();
        var result = await master.Safety().ObserveForeignSrdoAsync(Device, 0x111,
            new byte[] { 0x78, 0x56, 0x34, 0x12 }, new byte[] { 0x87, 0xA9, 0xCB, 0xED }, PeerFile(), sink).WithTimeoutAsync(ShortTimeout);
        result.Observation.Should().NotBeNull(result.Reason);
        result.Observation!.Decoded.Should().BeTrue(result.Observation.Reason);
        result.Observation.Origin.Should().Be(ForeignPdoMappingOrigin.DeviceDescription, "a live value of another length is not read");
        sink.Signals.Select(s => s.Index).Should().Equal((ushort)0x2000, (ushort)0x2001);
    }

    /// <summary>The communication record is read at its exact widths too: sub-index 05h exactly
    /// four bytes (UNSIGNED32), sub-index 01h exactly one (UNSIGNED8). A value of another length
    /// counts as not read — the file decides — instead of being read by its low bytes. The
    /// device's live COB-ID 1 is 111h and the file's 109h, so which one decided is visible.</summary>
    [Theory]
    [InlineData("5-byte COB-ID 1")]
    [InlineData("2-byte direction")]
    public async Task A_Live_Communication_Value_Of_Another_Length_Is_Not_Read(string fault)
    {
        var session = VirtualAdapterFixture.NewSession("co-foreign-srdo");
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var master = OpenOnVirtualClock(busA, Master, new CanOpenNodeOptions());
        using var device = OpenDeviceDivergingFromFile(busB);
        var od = device.ObjectDictionary;
        uint cobId1;
        if (fault == "5-byte COB-ID 1")
        {
            // Read by its low four bytes this would be 111h, the live id; not read, the file's 109h decides.
            od.Declare(0x1301, 0x05, OdDataType.Domain, OdAccess.ReadWrite, new byte[] { 0x11, 0x01, 0x00, 0x00, 0x00 }, pdoMappable: false);
            cobId1 = 0x109;
        }
        else
        {
            // Read by its low byte this would be 0, a deleted record; not read, the file's 1 decides.
            od.Declare(0x1301, 0x01, OdDataType.Unsigned16, OdAccess.ReadWrite, new byte[] { 0x00, 0x00 }, pdoMappable: false);
            cobId1 = 0x111;
        }
        master.BindPeerDeviceDescription(Device, PeerFile());
        var sink = new ListSink();
        var result = await master.Safety().ObserveForeignSrdoAsync(Device, cobId1,
            new byte[] { 0x78, 0x56, 0x34, 0x12 }, new byte[] { 0x87, 0xA9, 0xCB, 0xED }, PeerFile(), sink).WithTimeoutAsync(ShortTimeout);
        result.Observation.Should().NotBeNull(result.Reason);
        result.Observation!.PdoNumber.Should().Be(1);
        result.Observation.Decoded.Should().BeTrue(result.Observation.Reason);
    }

    /// <summary>A record exists only with direction 1 (tx) or 2 (rx), the values the device's
    /// validator accepts (§8.4.2.2, sub-index 1): a live reserved direction 3..255 is no existing
    /// record, and the pair on its COB-ID 1 is not matched. The device's validator would refuse
    /// such a value, so the test writes it past the validator.</summary>
    [Theory]
    [InlineData((byte)3)]
    [InlineData((byte)255)]
    public async Task A_Live_Reserved_Direction_Is_No_Existing_Record(byte direction)
    {
        var session = VirtualAdapterFixture.NewSession("co-foreign-srdo");
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var master = OpenOnVirtualClock(busA, Master, new CanOpenNodeOptions());
        using var device = OpenDeviceDivergingFromFile(busB);
        device.ObjectDictionary.WriteRawUnchecked(0x1301, 0x01, new[] { direction });
        master.BindPeerDeviceDescription(Device, PeerFile());
        var result = await master.Safety().ObserveForeignSrdoAsync(Device, 0x111,
            new byte[] { 0x78, 0x56, 0x34, 0x12 }, new byte[] { 0x87, 0xA9, 0xCB, 0xED }, PeerFile(), new ListSink()).WithTimeoutAsync(ShortTimeout);
        result.Observation.Should().BeNull(result.Observation?.Reason);
        result.Reason.Should().Contain("no SRDO");
    }

    /// <summary>The file's direction decides likewise when the peer does not answer: a reserved
    /// value is no existing record.</summary>
    [Fact]
    public async Task A_Reserved_Direction_In_The_File_Is_No_Existing_Record_When_The_Peer_Does_Not_Answer()
    {
        var session = VirtualAdapterFixture.NewSession("co-foreign-srdo");
        using var busA = Open(session, 1);
        using var rig = new UnansweredPeerRig(session, busA);
        var master = rig.Master;
        var file = PeerFilePatched("[1301sub1]", "ParameterValue=1", "ParameterValue=3");
        master.BindPeerDeviceDescription(Device, file);
        var result = await master.Safety().ObserveForeignSrdoAsync(Device, 0x109,
            new byte[] { 0x34, 0x12, 0x5A }, new byte[] { 0xCB, 0xED, 0xA5 }, file, new ListSink()).WithTimeoutAsync(TimeSpan.FromSeconds(30));
        result.Observation.Should().BeNull(result.Observation?.Reason);
        result.Reason.Should().Contain("no SRDO");
    }

    [Fact]
    public async Task Falls_Back_To_The_File_When_The_Peer_Does_Not_Answer()
    {
        var session = VirtualAdapterFixture.NewSession("co-foreign-srdo");
        using var busA = Open(session, 1);
        using var rig = new UnansweredPeerRig(session, busA);
        var master = rig.Master;
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
        using var master = OpenOnVirtualClock(busA, Master, new CanOpenNodeOptions());
        using var device = OpenOnVirtualClock(busB, Device, new CanOpenNodeOptions { SrdoCount = 2, WritableCommunicationParameters = true });
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
        using var rig = new UnansweredPeerRig(session, busA);
        var master = rig.Master;
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
        using var master = OpenOnVirtualClock(busA, Master, new CanOpenNodeOptions());
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
        using var master = OpenOnVirtualClock(busA, Master, new CanOpenNodeOptions());
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
        using var master = OpenOnVirtualClock(busA, Master, new CanOpenNodeOptions());
        var deviceFile = PeerFile();
        using var device = OpenOnVirtualClock(busB, deviceFile.NodeId!.Value, new CanOpenNodeOptions { WritableCommunicationParameters = true }, deviceFile);
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

    /// <summary>A description that declares the mapping records 1381h and 1382h but neither
    /// communication record implies 1301h and 1302h all the same (FR-CO-029): the loader
    /// provides them, and the peer gate lets a master configure them (§9.2). The match of a
    /// pair searches the records the description implies, not only those it declares — else the
    /// peer could be configured and its valid pair not observed (FR-CO-046).</summary>
    [Fact]
    public async Task A_Record_Implied_By_A_Mapping_Declaration_Is_Matched()
    {
        var session = VirtualAdapterFixture.NewSession("co-foreign-srdo");
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var master = OpenOnVirtualClock(busA, Master, new CanOpenNodeOptions());
        var file = CanOpenDeviceDescription.ParseDcf(TextWithoutObject(TextWithoutObject(PeerFileText(), "1301"), "1302"));
        file.Contains(0x1301, 0x05).Should().BeFalse();
        file.Contains(0x1381, 0x01).Should().BeTrue();
        using var device = OpenOnVirtualClock(busB, file.NodeId!.Value, new CanOpenNodeOptions { WritableCommunicationParameters = true }, file);
        master.BindPeerDeviceDescription(Device, file);
        var configuration = new PeerSafetyConfiguration { GlobalFailsafeCommandEnabled = true }
            .Add(1, new SrdoCommunicationParameter(SrdoDirection.Transmit, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(20), 0x109, 0x10A),
                new SrdoMapping().Add(0x2000, 0x00, 16).Add(0x2001, 0x00, 8));
        var configured = await master.Safety().ConfigurePeerSafetyAsync(Device, configuration).WithTimeoutAsync(ShortTimeout);
        configured.Succeeded.Should().BeTrue(string.Join("\n", configured.Mismatches));
        device.ObjectDictionary.ReadUnsigned(0x1301, 5).Should().Be(0x109u);
        var sink = new ListSink();
        var result = await master.Safety().ObserveForeignSrdoAsync(Device, 0x109,
            new byte[] { 0x34, 0x12, 0x5A }, new byte[] { 0xCB, 0xED, 0xA5 }, file, sink).WithTimeoutAsync(ShortTimeout);
        result.Observation.Should().NotBeNull(result.Reason);
        result.Observation!.Decoded.Should().BeTrue(result.Observation.Reason);
        result.Observation.PdoNumber.Should().Be(1);
        result.Observation.Origin.Should().Be(ForeignPdoMappingOrigin.LiveMapping);
        sink.Signals.Select(s => s.Index).Should().Equal((ushort)0x2000, (ushort)0x2001);
    }

    /// <summary>The file fallback applies the rule the device's own loader applies (§8.4.2.2,
    /// 1381h–13C0h: the even sub-index carries the inverted object of the odd one before it):
    /// a slot that is missing, or that does not repeat its plain slot, is no mapping to decode
    /// with, and the reason names the slot. The shipped fixture still decodes
    /// (<see cref="Falls_Back_To_The_File_When_The_Peer_Does_Not_Answer"/>).</summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("different")]
    public async Task A_File_Mapping_Whose_Inverted_Slot_Does_Not_Repeat_Its_Plain_Slot_Is_Not_Decoded(string fault)
    {
        var session = VirtualAdapterFixture.NewSession("co-foreign-srdo");
        using var busA = Open(session, 1);
        using var rig = new UnansweredPeerRig(session, busA);
        var master = rig.Master;
        var file = fault == "missing"
            ? PeerFileWithout("[1381sub4]")
            : PeerFilePatched("[1381sub4]", "0x20010008", "0x20020008");
        master.BindPeerDeviceDescription(Device, file);
        var sink = new ListSink();
        var result = await master.Safety().ObserveForeignSrdoAsync(Device, 0x109,
            new byte[] { 0x34, 0x12, 0x5A }, new byte[] { 0xCB, 0xED, 0xA5 }, file, sink).WithTimeoutAsync(TimeSpan.FromSeconds(30));
        result.Observation!.Decoded.Should().BeFalse();
        result.Observation.Origin.Should().BeNull();
        result.Observation.Reason.Should().Contain("entry 4");
        result.Observation.Reason.Should().Contain(fault == "missing" ? "missing" : "does not repeat entry 3");
        sink.Signals.Should().BeEmpty();
    }

    [Fact]
    public async Task A_Dummy_Entry_In_The_Mapping_Is_Not_Decoded()
    {
        var session = VirtualAdapterFixture.NewSession("co-foreign-srdo");
        using var busA = Open(session, 1);
        using var rig = new UnansweredPeerRig(session, busA);
        var master = rig.Master;
        var file = CanOpenDeviceDescription.ParseDcf(PatchedText(PatchedText(PeerFileText(),
            "[1381sub1]", "0x20000010", "0x00050010"), "[1381sub2]", "0x20000010", "0x00050010"));
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
        using var rig = new UnansweredPeerRig(session, busA);
        var master = rig.Master;
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
        using var master = OpenOnVirtualClock(busA, Master, new CanOpenNodeOptions());
        var nine = new byte[9];
        var ex1 = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => master.Safety().ObserveForeignSrdoAsync(Device, 0x109, nine, new byte[8], PeerFile(), new ListSink()));
        ex1.ParamName.Should().Be("frame1");
        var ex2 = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => master.Safety().ObserveForeignSrdoAsync(Device, 0x109, new byte[8], nine, PeerFile(), new ListSink()));
        ex2.ParamName.Should().Be("frame2");
    }
}
