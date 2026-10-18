using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Safety;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Safety;

/// <summary>A master configures a device's safety parameters over SDO (CiA DSP 304 V1.0 §9.2,
/// Figure 9) as the configuration entity of CiA 319 v2.0.0 clause 5 — identity first, the steps
/// of §5.3, the storage of step 5, the SRVT rule of §6.2 — and verifies them (§8.3.1 step D).
/// The device is a real node; the mismatch path uses a fake expedited SDO server on a raw
/// channel that answers one readback wrongly (FR-CO-044, FR-CO-047).</summary>
public class CanOpenPeerSafetyTests : IClassFixture<VirtualAdapterFixture>
{
    private const byte Master = 0x01;
    private const byte Device = 0x05;
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);

    private static ICanBus Open(string session, int channel) => CanBus.Open(
        $"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(VirtualAdapterFixture.Bitrate));

    /// <summary>The fixture's text, line endings normalised: what the files below derive from.</summary>
    private static string FixtureText()
        => System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "TestCases", "CANopen", "Fixtures", "safety.dcf")).Replace("\r\n", "\n");

    /// <summary>The file that lists every safety object of node 5 (the peer gate reads it).</summary>
    private static CanOpenDeviceDescription PeerFile() => CanOpenDeviceDescription.ParseDcf(FixtureText());

    /// <summary>The master on a virtual clock: its SDO client deadline runs on the node's time
    /// source, which only the test moves, so a transfer waits for the peer's reply however long the
    /// host stalls instead of ending with 0504 0000h. Why that matters is measured in the fix report
    /// (round 15 addendum) and tracked in #92.</summary>
    private static CanOpenNode OpenMasterOnVirtualClock(ICanBus bus) => OpenOnVirtualClock(bus, Master, new CanOpenNodeOptions());

    /// <summary>Any node of these tests on a virtual clock (see <see cref="OpenMasterOnVirtualClock"/>).</summary>
    private static CanOpenNode OpenOnVirtualClock(ICanBus bus, byte nodeId, CanOpenNodeOptions options, CanOpenDeviceDescription? description = null)
        => new(new CanKit.Pro.RawCan.CanBusService(bus), nodeId, options, ownsService: true, new ManualTimeSource(), description);

    /// <summary>What a transaction has read when it refuses at the count: a configuration the
    /// identity first (CiA 319 §5.2), a verification only the count.</summary>
    private static (ushort, byte)[] ReadsUpToTheCount(bool verify) => verify
        ? new[] { ((ushort)0x13FF, (byte)0x00) }
        : new[] { ((ushort)0x1018, (byte)0x01), ((ushort)0x1018, (byte)0x02), ((ushort)0x1018, (byte)0x03), ((ushort)0x13FF, (byte)0x00) };

    private static PeerSafetyConfiguration Configuration() => new PeerSafetyConfiguration { GlobalFailsafeCommandEnabled = true }
        .Add(1, new SrdoCommunicationParameter(SrdoDirection.Transmit, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(20), 0x109, 0x10A),
            new SrdoMapping().Add(0x2000, 0x00, 16).Add(0x2001, 0x00, 8));

    /// <summary>The identity the fixture's <c>[DeviceInfo]</c> gives node 5 — what CiA 319 §5.2
    /// compares 1018h:01–03 with before anything is configured.</summary>
    private const uint VendorId = 0x0000_0100, ProductCode = 0x0000_1001, RevisionNumber = 0x0001_0000;

    private static ICanOpenNode OpenDevice(ICanBus bus)
    {
        var device = OpenOnVirtualClock(bus, Device, new CanOpenNodeOptions { SrdoCount = 2, WritableCommunicationParameters = true });
        device.ObjectDictionary.AddU32(0x1018, 0x01, VendorId, OdAccess.ReadOnly);
        device.ObjectDictionary.AddU32(0x1018, 0x02, ProductCode, OdAccess.ReadOnly);
        device.ObjectDictionary.AddU32(0x1018, 0x03, RevisionNumber, OdAccess.ReadOnly);
        device.ObjectDictionary.AddU16(0x2000, 0x00, 0x1234);
        device.ObjectDictionary.AddU8(0x2001, 0x00, 0x5A);
        return device;
    }

    /// <summary>The fixture with 1010h declared — sub-indices 0 and 1 ("save all parameters"), and
    /// 2 ("save communication parameters") when asked — as a device that stores on command
    /// lists it. The test device implements sub-index 1 only (CiA 301 §7.5.2.13).</summary>
    private static CanOpenDeviceDescription PeerFileWith1010(bool withSub2)
    {
        var text = FixtureText();
        text = text.Replace("[OptionalObjects]\nSupportedObjects=7\n", "[OptionalObjects]\nSupportedObjects=8\n").Replace("7=0x13FF\n", "7=0x13FF\n8=0x1010\n");
        text.Should().Contain("8=0x1010");
        var sections = new System.Text.StringBuilder();
        sections.Append($"\n[1010]\nParameterName=Store parameters\nObjectType=0x8\nSubNumber={(withSub2 ? 3 : 2)}\n");
        sections.Append($"\n[1010sub0]\nParameterName=Highest sub-index supported\nObjectType=0x7\nDataType=0x0005\nAccessType=ro\nDefaultValue={(withSub2 ? 2 : 1)}\nPDOMapping=0\n");
        sections.Append("\n[1010sub1]\nParameterName=Save all parameters\nObjectType=0x7\nDataType=0x0007\nAccessType=rw\nDefaultValue=1\nPDOMapping=0\n");
        if (withSub2) sections.Append("\n[1010sub2]\nParameterName=Save communication parameters\nObjectType=0x7\nDataType=0x0007\nAccessType=rw\nDefaultValue=1\nPDOMapping=0\n");
        var file = CanOpenDeviceDescription.ParseDcf(text.TrimEnd('\n') + "\n" + sections);
        file.Contains(0x1010, 0x01).Should().BeTrue();
        file.Contains(0x1010, 0x02).Should().Be(withSub2);
        return file;
    }

    /// <summary>The fixture with its <c>[DeviceInfo]</c> numbers at 0 — what EdsDcfNet reads for a
    /// file that leaves them out — so it describes no device.</summary>
    private static CanOpenDeviceDescription PeerFileWithoutAnIdentity()
    {
        var text = FixtureText().Replace("VendorNumber=0x00000100", "VendorNumber=0").Replace("ProductNumber=0x00001001", "ProductNumber=0").Replace("RevisionNumber=0x00010000", "RevisionNumber=0");
        var file = CanOpenDeviceDescription.ParseDcf(text);
        (file.DeviceInfo.VendorNumber, file.DeviceInfo.ProductNumber, file.DeviceInfo.RevisionNumber).Should().Be((0u, 0u, 0u));
        return file;
    }

    /// <summary>The fixture with record 1301h declared without sub-index 3: a device that does
    /// not implement the SRVT of its tx SRDO 1 (CiA 319 §6.2) — or whose file says so.</summary>
    private static CanOpenDeviceDescription PeerFileWithoutSrvtOfSrdo1()
    {
        var text = FixtureText();
        int start = text.IndexOf("[1301sub3]", StringComparison.Ordinal);
        int end = text.IndexOf("[1301sub4]", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1);
        end.Should().BeGreaterThan(start);
        text = (text[..start] + text[end..]).Replace("SRDO communication parameter 1\nSubNumber=7", "SRDO communication parameter 1\nSubNumber=6");
        var file = CanOpenDeviceDescription.ParseDcf(text);
        file.Contains(0x1301, 0x03).Should().BeFalse();
        file.Contains(0x1301, 0x02).Should().BeTrue();
        return file;
    }

    [Fact]
    public async Task Configure_Downloads_Reads_Back_And_Acknowledges()
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var master = OpenMasterOnVirtualClock(busA);
        using var device = OpenDevice(busB);
        master.BindPeerDeviceDescription(Device, PeerFile());
        var result = await master.Safety().ConfigurePeerSafetyAsync(Device, Configuration()).WithTimeoutAsync(ShortTimeout);
        result.Succeeded.Should().BeTrue(string.Join("\n", result.Mismatches));
        var od = device.ObjectDictionary;
        od.ReadUnsigned(0x1300, 0).Should().Be(1u);
        od.ReadUnsigned(0x1301, 1).Should().Be(1u);
        od.ReadUnsigned(0x1301, 2).Should().Be(30u);
        od.ReadUnsigned(0x1301, 5).Should().Be(0x109u);
        od.ReadUnsigned(0x1381, 0).Should().Be(4u);
        od.ReadUnsigned(0x1302, 1).Should().Be(0u, "an SRDO the configuration does not name is deleted");
        od.ReadUnsigned(0x13FF, 2).Should().Be(0u);
        od.ReadUnsigned(0x13FE, 0).Should().Be(0xA5u);
        SrdoRecords.IsConfigurationValid(od, 1).Should().BeTrue();
    }

    /// <summary>CiA 319 §5.2: "Prior to the configuration of an SRD, the configuration entity
    /// shall perform a verification of the identity of the SRD based on object 1018h", entries
    /// 01h–03h, against the EDS. A device that is not the one the bound file describes is
    /// reported with the entry that differs, and nothing is written to it.</summary>
    [Theory]
    [InlineData((byte)0x01)]
    [InlineData((byte)0x02)]
    [InlineData((byte)0x03)]
    public async Task Configure_Verifies_The_Peers_Identity_Before_Writing_Anything(byte subindex)
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var master = OpenMasterOnVirtualClock(busA);
        using var device = OpenDevice(busB);
        device.ObjectDictionary.AddU32(0x1018, subindex, 0x0BAD_0000, OdAccess.ReadOnly); // another device
        master.BindPeerDeviceDescription(Device, PeerFile());
        var result = await master.Safety().ConfigurePeerSafetyAsync(Device, Configuration()).WithTimeoutAsync(ShortTimeout);
        result.Succeeded.Should().BeFalse();
        result.Stored.Should().BeFalse();
        var mismatch = result.Mismatches.Should().ContainSingle().Which;
        mismatch.Index.Should().Be(0x1018);
        mismatch.Subindex.Should().Be(subindex);
        mismatch.Expected.Should().Equal(BitConverter.GetBytes(subindex switch { 1 => VendorId, 2 => ProductCode, _ => RevisionNumber }));
        mismatch.Actual.Should().Equal(0x00, 0x00, 0xAD, 0x0B);
        var od = device.ObjectDictionary;
        od.ReadUnsigned(0x1300, 0).Should().Be(0u, "nothing was written");
        od.ReadUnsigned(0x1301, 2).Should().Be(25u, "nothing was written");
        od.ReadUnsigned(0x13FE, 0).Should().Be(0u);
    }

    /// <summary>All three identity entries are read and reported, and they are the only transfers
    /// of a configuration that ends there: no count, no write.</summary>
    [Fact]
    public async Task An_Identity_That_Differs_Is_The_Only_Thing_Read_And_Every_Difference_Is_Reported()
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var master = OpenMasterOnVirtualClock(busA);
        using var fake = new FakeExpeditedServer(session, 2, Device, lieAt: (0, 0));
        lock (fake.Written)
        {
            fake.Written[(0x1018, 1)] = BitConverter.GetBytes(VendorId + 1);
            fake.Written[(0x1018, 2)] = BitConverter.GetBytes(ProductCode + 1);
            fake.Written[(0x1018, 3)] = new byte[] { 0x00, 0x00, 0x01 }; // three bytes, not UNSIGNED32: still a difference
        }
        master.BindPeerDeviceDescription(Device, PeerFile());
        var result = await master.Safety().ConfigurePeerSafetyAsync(Device, Configuration()).WithTimeoutAsync(ShortTimeout);
        result.Succeeded.Should().BeFalse();
        result.Mismatches.Select(m => (m.Index, m.Subindex)).Should().Equal(((ushort)0x1018, (byte)1), ((ushort)0x1018, (byte)2), ((ushort)0x1018, (byte)3));
        result.Mismatches[2].Actual.Should().Equal(0x00, 0x00, 0x01);
        lock (fake.Requests) fake.Requests.Should().Equal(((ushort)0x1018, (byte)1), ((ushort)0x1018, (byte)2), ((ushort)0x1018, (byte)3));
        lock (fake.Written) fake.Written.Keys.Where(k => k.Item1 != 0x1018 && k != (0x13FF, 0)).Should().BeEmpty("nothing was written");
    }

    /// <summary>CiA 319 §5.2 needs all three identity entries, which CiA 301 makes optional past
    /// the vendor-ID: a bound file that leaves 1018h:02 out is refused before any frame, as the
    /// gate would refuse that upload — one frame in, otherwise.</summary>
    [Fact]
    public async Task A_File_That_Leaves_An_Identity_Entry_Out_Is_Refused_Before_Any_Frame()
    {
        var text = FixtureText();
        int start = text.IndexOf("[1018sub2]", StringComparison.Ordinal);
        int end = text.IndexOf("[1018sub3]", StringComparison.Ordinal);
        (start > -1 && end > start).Should().BeTrue();
        var file = CanOpenDeviceDescription.ParseDcf(text[..start] + text[end..]);
        file.Contains(0x1018, 0x02).Should().BeFalse();
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var master = OpenMasterOnVirtualClock(busA);
        using var fake = new FakeExpeditedServer(session, 2, Device, lieAt: (0, 0));
        master.BindPeerDeviceDescription(Device, file);
        var ex = await Assert.ThrowsAsync<PeerSdoAccessException>(() => master.Safety().ConfigurePeerSafetyAsync(Device, Configuration()).WithTimeoutAsync(ShortTimeout));
        (ex.Index, ex.Subindex, ex.PeerDescriptionLoaded).Should().Be(((ushort)0x1018, (byte)0x02, true));
        lock (fake.Requests) fake.Requests.Should().BeEmpty("nothing was sent");
    }

    /// <summary>A file whose VendorNumber, ProductNumber and RevisionNumber are all 0 — what
    /// EdsDcfNet reads for keys the file leaves out — describes no device: comparing a peer with
    /// it would prove nothing, or blame the peer for the file. Refused before any frame.</summary>
    [Fact]
    public async Task A_File_Without_An_Identity_Is_Refused_Before_Any_Frame()
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var master = OpenMasterOnVirtualClock(busA);
        using var fake = new FakeExpeditedServer(session, 2, Device, lieAt: (0, 0));
        master.BindPeerDeviceDescription(Device, PeerFileWithoutAnIdentity());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => master.Safety().ConfigurePeerSafetyAsync(Device, Configuration()).WithTimeoutAsync(ShortTimeout));
        ex.Message.Should().Contain("no identity").And.Contain("CiA 319 §5.2");
        lock (fake.Requests) fake.Requests.Should().BeEmpty("nothing was sent");
    }

    /// <summary>CiA 319 §5.3 step 5: with <see cref="PeerSafetyConfiguration.Storage"/> asked for,
    /// the "save" signature goes to 1010h after the acknowledgement, so what the peer stores
    /// includes 13FEh = A5h, and a reset of the peer brings the configuration back. Without it,
    /// the reset brings the power-on values back.</summary>
    [Theory]
    [InlineData(PeerSafetyStorage.AllParameters)]
    [InlineData(PeerSafetyStorage.None)]
    public async Task Configure_Stores_The_Acknowledged_Configuration_When_Asked(PeerSafetyStorage storage)
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var master = OpenMasterOnVirtualClock(busA);
        using var device = OpenDevice(busB);
        master.BindPeerDeviceDescription(Device, PeerFileWith1010(withSub2: false));
        var configuration = new PeerSafetyConfiguration { GlobalFailsafeCommandEnabled = true, Storage = storage }
            .Add(1, new SrdoCommunicationParameter(SrdoDirection.Transmit, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(20), 0x109, 0x10A),
                new SrdoMapping().Add(0x2000, 0x00, 16).Add(0x2001, 0x00, 8));
        var result = await master.Safety().ConfigurePeerSafetyAsync(Device, configuration).WithTimeoutAsync(ShortTimeout);
        result.Succeeded.Should().BeTrue(string.Join("\n", result.Mismatches));
        result.Stored.Should().Be(storage != PeerSafetyStorage.None);
        var od = device.ObjectDictionary;
        od.ReadUnsigned(0x13FE, 0).Should().Be(0xA5u);

        var reset = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        device.NmtCommandReceived += (_, e) => { if (e.Command == NmtCommand.ResetCommunication) reset.TrySetResult(true); }; // raised after the node acted on it
        await master.SendNmtCommandAsync(NmtCommand.ResetCommunication, Device);
        await reset.Task.WithTimeoutAsync(ShortTimeout);
        device.State.Should().Be(NmtState.PreOperational);
        if (storage == PeerSafetyStorage.None)
        {
            od.ReadUnsigned(0x1301, 1).Should().Be(0u, "the power-on values are back: nothing was stored");
            od.ReadUnsigned(0x13FE, 0).Should().Be(0u);
        }
        else
        {
            od.ReadUnsigned(0x1301, 1).Should().Be(1u, "the stored configuration is back");
            od.ReadUnsigned(0x1301, 2).Should().Be(30u);
            od.ReadUnsigned(0x1300, 0).Should().Be(1u);
            od.ReadUnsigned(0x13FE, 0).Should().Be(0xA5u, "the acknowledgement was stored with it");
            SrdoRecords.IsConfigurationValid(od, 1).Should().BeTrue();
        }
    }

    /// <summary>The store is the last transfer: it is not sent when the readback differs, and
    /// a configuration that ends there reports neither acknowledged nor stored.</summary>
    [Fact]
    public async Task No_Store_Without_The_Acknowledgement()
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var master = OpenMasterOnVirtualClock(busA);
        using var fake = new FakeExpeditedServer(session, 2, Device, lieAt: (0x1301, 0x02));
        master.BindPeerDeviceDescription(Device, PeerFileWith1010(withSub2: false));
        var configuration = new PeerSafetyConfiguration { GlobalFailsafeCommandEnabled = true, Storage = PeerSafetyStorage.AllParameters }
            .Add(1, new SrdoCommunicationParameter(SrdoDirection.Transmit, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(20), 0x109, 0x10A),
                new SrdoMapping().Add(0x2000, 0x00, 16).Add(0x2001, 0x00, 8));
        var result = await master.Safety().ConfigurePeerSafetyAsync(Device, configuration).WithTimeoutAsync(ShortTimeout);
        result.Succeeded.Should().BeFalse();
        result.Stored.Should().BeFalse();
        lock (fake.Written)
        {
            fake.Written.Should().NotContainKey((0x13FE, 0));
            fake.Written.Should().NotContainKey((0x1010, 1));
        }
    }

    /// <summary>A storage sub-index the bound file does not declare is refused before any frame,
    /// as the gate would refuse the transfer — but then, after the acknowledgement.</summary>
    [Fact]
    public async Task A_Storage_The_Bound_File_Does_Not_Declare_Is_Refused_Before_Any_Frame()
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var master = OpenMasterOnVirtualClock(busA);
        using var fake = new FakeExpeditedServer(session, 2, Device, lieAt: (0, 0));
        master.BindPeerDeviceDescription(Device, PeerFileWith1010(withSub2: false));
        var configuration = new PeerSafetyConfiguration { Storage = PeerSafetyStorage.CommunicationParameters }
            .Add(1, new SrdoCommunicationParameter(SrdoDirection.Transmit, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(20), 0x109, 0x10A),
                new SrdoMapping().Add(0x2000, 0x00, 16).Add(0x2001, 0x00, 8));
        var ex = await Assert.ThrowsAsync<PeerSdoAccessException>(() => master.Safety().ConfigurePeerSafetyAsync(Device, configuration).WithTimeoutAsync(ShortTimeout));
        (ex.Index, ex.Subindex, ex.PeerDescriptionLoaded).Should().Be(((ushort)0x1010, (byte)0x02, true));
        lock (fake.Requests) fake.Requests.Should().BeEmpty("nothing was sent");
    }

    /// <summary>A store the peer refuses — here 1010h:02, which the device does not implement —
    /// propagates as an abort, after the acknowledgement: the peer holds its configuration
    /// valid, unstored.</summary>
    [Fact]
    public async Task A_Store_The_Peer_Refuses_Propagates_After_The_Acknowledgement()
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var master = OpenMasterOnVirtualClock(busA);
        using var device = OpenDevice(busB);
        master.BindPeerDeviceDescription(Device, PeerFileWith1010(withSub2: true));
        var configuration = new PeerSafetyConfiguration { Storage = PeerSafetyStorage.CommunicationParameters }
            .Add(1, new SrdoCommunicationParameter(SrdoDirection.Transmit, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(20), 0x109, 0x10A),
                new SrdoMapping().Add(0x2000, 0x00, 16).Add(0x2001, 0x00, 8));
        var ex = await Assert.ThrowsAsync<SdoAbortException>(() => master.Safety().ConfigurePeerSafetyAsync(Device, configuration).WithTimeoutAsync(ShortTimeout));
        ex.AbortCode.Should().Be((uint)SdoAbortCode.SubIndexDoesNotExist);
        device.ObjectDictionary.ReadUnsigned(0x13FE, 0).Should().Be(0xA5u, "the acknowledgement stands; only the store failed");
        SrdoRecords.IsConfigurationValid(device.ObjectDictionary, 1).Should().BeTrue();
    }

    [Fact]
    public void A_Storage_That_Is_No_Sub_Index_Of_1010h_Is_Refused_When_Set()
    {
        Action set = () => _ = new PeerSafetyConfiguration { Storage = (PeerSafetyStorage)3 };
        set.Should().Throw<ArgumentOutOfRangeException>();
        new PeerSafetyConfiguration().Storage.Should().Be(PeerSafetyStorage.None);
    }

    /// <summary>CiA 319 §6.2: "SRVT for SRDOs with Tx transmission direction – For the calculation
    /// the default value of 20d shall be used" when the SRD does not implement it, and the EDS
    /// "should include the SRVT parameter for all supported SRDOs". A tx SRDO whose record the
    /// bound file declares without sub-index 3 says 20 ms, and the sub-index is written and read
    /// back all the same: a peer that aborts it with 0609 0011h does not implement it, which both
    /// tolerate; one that accepts it holds the 20 ms. The checksum is over 20 ms either way. A
    /// consumer's SRVT is not optional: written whatever the file says.</summary>
    [Theory]
    [InlineData(SrdoDirection.Transmit, true)]
    [InlineData(SrdoDirection.Transmit, false)]
    [InlineData(SrdoDirection.Receive, false)]
    public async Task A_Transmit_Srdos_Srvt_The_File_Does_Not_Declare_Is_Written_As_Optional_And_Checksummed_At_The_Default(SrdoDirection direction, bool peerLacksSrvt)
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var master = OpenMasterOnVirtualClock(busA);
        using var fake = new FakeExpeditedServer(session, 2, Device, lieAt: (0, 0));
        if (peerLacksSrvt) fake.AbortAt[(0x1301, 3)] = (uint)SdoAbortCode.SubIndexDoesNotExist;
        master.BindPeerDeviceDescription(Device, PeerFileWithoutSrvtOfSrdo1());
        var parameter = new SrdoCommunicationParameter(direction, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(20), 0x109, 0x10A);
        // The fixture's process objects are ro, which a consumer cannot map; an empty mapping is allowed (L = 0).
        var mapping = direction == SrdoDirection.Transmit ? new SrdoMapping().Add(0x2000, 0x00, 16).Add(0x2001, 0x00, 8) : new SrdoMapping();
        var configuration = new PeerSafetyConfiguration().Add(1, parameter, mapping);
        var result = await master.Safety().ConfigurePeerSafetyAsync(Device, configuration).WithTimeoutAsync(ShortTimeout);
        result.Succeeded.Should().BeTrue(string.Join("\n", result.Mismatches));
        ushort crc = SrdoCrc.Compute(parameter, mapping);
        lock (fake.Written)
        {
            fake.Written.ContainsKey((0x1301, 3)).Should().Be(!peerLacksSrvt, "a peer that holds the sub-index gets the 20 ms");
            if (!peerLacksSrvt) fake.Written[(0x1301, 3)].Should().Equal(20);
            fake.Written[(0x13FF, 1)].Should().Equal((byte)crc, (byte)(crc >> 8));
        }
        lock (fake.Requests) fake.Requests.Count(r => r == (0x1301, 3)).Should().Be(2, "written and read back, whatever the peer answers");
        lock (fake.Requests) fake.Requests.Clear();
        var verified = await master.Safety().VerifyPeerSafetyConfigurationAsync(Device, configuration).WithTimeoutAsync(ShortTimeout);
        verified.Succeeded.Should().BeTrue(string.Join("\n", verified.Mismatches));
        lock (fake.Requests) fake.Requests.Should().Contain((0x1301, 3));
    }

    /// <summary>Only the SRVT of a tx SRDO whose record the file declares without it is optional:
    /// the abort of a consumer's SRVT, or of one the file declares, propagates as every abort does.</summary>
    [Theory]
    [InlineData(true)]   // a consumer, on a file without sub-index 3
    [InlineData(false)]  // a producer, on the file that declares it
    public async Task An_Srvt_The_File_Declares_Or_A_Consumer_Needs_Is_Not_Optional(bool consumer)
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var master = OpenMasterOnVirtualClock(busA);
        using var fake = new FakeExpeditedServer(session, 2, Device, lieAt: (0, 0));
        fake.AbortAt[(0x1301, 3)] = (uint)SdoAbortCode.SubIndexDoesNotExist;
        master.BindPeerDeviceDescription(Device, consumer ? PeerFileWithoutSrvtOfSrdo1() : PeerFile());
        var configuration = consumer
            ? new PeerSafetyConfiguration().Add(1, new SrdoCommunicationParameter(SrdoDirection.Receive, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(20), 0x109, 0x10A), new SrdoMapping())
            : Configuration();
        var ex = await Assert.ThrowsAsync<SdoAbortException>(() => master.Safety().ConfigurePeerSafetyAsync(Device, configuration).WithTimeoutAsync(ShortTimeout));
        ex.AbortCode.Should().Be((uint)SdoAbortCode.SubIndexDoesNotExist);
    }

    /// <summary>The file is not trusted alone. A device that holds sub-index 3 although its file
    /// leaves it out — here at 30 ms from an earlier configuration, which a delete (sub-index 1
    /// only) does not clear — gets the 20 ms written that the checksum covers, so its own
    /// checksum agrees with the one it was given.</summary>
    [Fact]
    public async Task A_Device_Holding_An_Srvt_Its_File_Leaves_Out_Gets_The_Default_Written()
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var master = OpenMasterOnVirtualClock(busA);
        using var device = OpenDevice(busB);
        device.ObjectDictionary.WriteUnsigned(0x1301, 3, 30);
        master.BindPeerDeviceDescription(Device, PeerFileWithoutSrvtOfSrdo1());
        var result = await master.Safety().ConfigurePeerSafetyAsync(Device, Configuration()).WithTimeoutAsync(ShortTimeout);
        result.Succeeded.Should().BeTrue(string.Join("\n", result.Mismatches));
        device.ObjectDictionary.ReadUnsigned(0x1301, 3).Should().Be(20u, "written although the file leaves it out");
        SrdoRecords.IsConfigurationValid(device.ObjectDictionary, 1).Should().BeTrue("the device checksums the 20 ms it holds");
        var verified = await master.Safety().VerifyPeerSafetyConfigurationAsync(Device, Configuration()).WithTimeoutAsync(ShortTimeout);
        verified.Succeeded.Should().BeTrue(string.Join("\n", verified.Mismatches));
    }

    /// <summary>An SRVT other than 20 ms for a tx SRDO the peer holds no SRVT for cannot be
    /// written, and a checksum over it would not be the peer's: refused before any frame, by the
    /// configuration and the verification alike.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Another_Srvt_For_A_Transmit_Srdo_Without_One_On_The_Peer_Is_Refused_Before_Any_Frame(bool verify)
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var master = OpenMasterOnVirtualClock(busA);
        using var fake = new FakeExpeditedServer(session, 2, Device, lieAt: (0, 0));
        master.BindPeerDeviceDescription(Device, PeerFileWithoutSrvtOfSrdo1());
        var configuration = new PeerSafetyConfiguration()
            .Add(1, new SrdoCommunicationParameter(SrdoDirection.Transmit, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(30), 0x109, 0x10A),
                new SrdoMapping().Add(0x2000, 0x00, 16).Add(0x2001, 0x00, 8));
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => (verify
            ? master.Safety().VerifyPeerSafetyConfigurationAsync(Device, configuration)
            : master.Safety().ConfigurePeerSafetyAsync(Device, configuration)).WithTimeoutAsync(ShortTimeout));
        ex.ParamName.Should().Be(verify ? "expected" : "configuration");
        ex.Message.Should().Contain("CiA 319 §6.2");
        lock (fake.Requests) fake.Requests.Should().BeEmpty("nothing was sent");
    }

    [Fact]
    public async Task Configure_Is_Refused_Without_A_Bound_Description()
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var master = OpenMasterOnVirtualClock(busA);
        using var device = OpenDevice(busB);
        var ex = await Assert.ThrowsAsync<PeerSdoAccessException>(() => master.Safety().ConfigurePeerSafetyAsync(Device, Configuration()).WithTimeoutAsync(ShortTimeout));
        (ex.Index, ex.Subindex, ex.PeerDescriptionLoaded).Should().Be(((ushort)0x1018, (byte)0x01, false), "the first transfer, which has nothing to be compared with");
        ex.Message.Should().Contain("CiA 319 §5.2");
        device.ObjectDictionary.ReadUnsigned(0x1301, 1).Should().Be(0u, "nothing was sent");
    }

    [Fact]
    public async Task Configure_Aborts_When_The_Peer_Is_Operational()
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var master = OpenMasterOnVirtualClock(busA);
        using var device = OpenDevice(busB);
        master.BindPeerDeviceDescription(Device, PeerFile());
        device.ObjectDictionary.WriteUnsigned(0x13FE, 0, 0xA5);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        device.NmtCommandReceived += (_, e) => { if (e.Command == NmtCommand.Start) started.TrySetResult(true); }; // raised after the node acted on it
        await master.SendNmtCommandAsync(NmtCommand.Start, Device);
        await started.Task.WithTimeoutAsync(ShortTimeout);
        device.State.Should().Be(NmtState.Operational);
        var ex = await Assert.ThrowsAsync<SdoAbortException>(() => master.Safety().ConfigurePeerSafetyAsync(Device, Configuration()).WithTimeoutAsync(ShortTimeout));
        ex.AbortCode.Should().Be((uint)SdoAbortCode.DataCannotBeTransferredDeviceState);
        device.ObjectDictionary.ReadUnsigned(0x13FE, 0).Should().Be(0xA5u,
            "the first write was refused, so no parameter changed and nothing cleared 13FEh");
    }

    [Fact]
    public async Task Verify_Succeeds_After_Configure_And_Fails_On_A_Tampered_Checksum()
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var master = OpenMasterOnVirtualClock(busA);
        using var device = OpenDevice(busB);
        master.BindPeerDeviceDescription(Device, PeerFile());
        (await master.Safety().ConfigurePeerSafetyAsync(Device, Configuration()).WithTimeoutAsync(ShortTimeout)).Succeeded.Should().BeTrue();
        var verified = await master.Safety().VerifyPeerSafetyConfigurationAsync(Device, Configuration()).WithTimeoutAsync(ShortTimeout);
        verified.Succeeded.Should().BeTrue(string.Join("\n", verified.Mismatches));
        device.ObjectDictionary.WriteUnsigned(0x13FF, 1, device.ObjectDictionary.ReadUnsigned(0x13FF, 1) ^ 0x0100);
        device.ObjectDictionary.WriteUnsigned(0x13FE, 0, 0xA5);
        var failed = await master.Safety().VerifyPeerSafetyConfigurationAsync(Device, Configuration()).WithTimeoutAsync(ShortTimeout);
        failed.Succeeded.Should().BeFalse();
        failed.Mismatches.Should().ContainSingle(m => m.Index == 0x13FF && m.Subindex == 1);
        device.ObjectDictionary.WriteUnsigned(0x13FE, 0, 0);
        var notValid = await master.Safety().VerifyPeerSafetyConfigurationAsync(Device, Configuration()).WithTimeoutAsync(ShortTimeout);
        notValid.Mismatches.Should().Contain(m => m.Index == 0x13FE);
    }

    [Fact]
    public async Task Verify_Reports_A_Record_That_Differs_From_The_Expectation()
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var master = OpenMasterOnVirtualClock(busA);
        using var device = OpenDevice(busB);
        master.BindPeerDeviceDescription(Device, PeerFile());
        await master.Safety().ConfigurePeerSafetyAsync(Device, Configuration()).WithTimeoutAsync(ShortTimeout);
        var other = new PeerSafetyConfiguration { GlobalFailsafeCommandEnabled = true }
            .Add(1, new SrdoCommunicationParameter(SrdoDirection.Transmit, TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(20), 0x109, 0x10A),
                new SrdoMapping().Add(0x2000, 0x00, 16).Add(0x2001, 0x00, 8));
        var result = await master.Safety().VerifyPeerSafetyConfigurationAsync(Device, other).WithTimeoutAsync(ShortTimeout);
        result.Succeeded.Should().BeFalse();
        result.Mismatches.Should().Contain(m => m.Index == 0x1301 && m.Subindex == 2);
        result.Mismatches.Should().Contain(m => m.Index == 0x13FF && m.Subindex == 1);
    }

    [Fact]
    public async Task Configure_Does_Not_Acknowledge_When_The_Readback_Differs()
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var master = OpenMasterOnVirtualClock(busA);
        using var fake = new FakeExpeditedServer(session, 2, Device, lieAt: (0x1301, 0x02));
        master.BindPeerDeviceDescription(Device, PeerFile());
        var result = await master.Safety().ConfigurePeerSafetyAsync(Device, Configuration()).WithTimeoutAsync(ShortTimeout);
        result.Succeeded.Should().BeFalse();
        result.Mismatches.Should().ContainSingle(m => m.Index == 0x1301 && m.Subindex == 2);
        fake.Written.Should().NotContainKey((0x13FE, 0), "A5h is written only after a clean readback");
    }

    [Theory]
    [InlineData(SrdoDirection.Transmit, 70000, 20)]
    [InlineData(SrdoDirection.Transmit, 0, 20)]
    [InlineData(SrdoDirection.Transmit, 30, 0)]
    [InlineData(SrdoDirection.Transmit, 30, 256)]
    [InlineData(SrdoDirection.Receive, 30, 0)]
    [InlineData(SrdoDirection.Receive, 30, 256)]
    public void A_Time_Outside_Its_Range_Is_Refused_When_The_Srdo_Is_Added(SrdoDirection direction, int cycleMs, int validationMs)
    {
        // Refused by Add, so no configuration that could reach a peer carries it (§8.4.2.2:
        // sub2 1..65535 ms, sub3 1..255 ms for every direction, because the checksum covers it).
        var configuration = new PeerSafetyConfiguration { GlobalFailsafeCommandEnabled = true };
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => configuration
            .Add(1, new SrdoCommunicationParameter(direction, TimeSpan.FromMilliseconds(cycleMs), TimeSpan.FromMilliseconds(validationMs), 0x109, 0x10A),
                new SrdoMapping().Add(0x2000, 0x00, 16).Add(0x2001, 0x00, 8)));
        ex.Message.Should().Contain("SRDO 1");
        ex.ParamName.Should().Be("parameter");
        configuration.Srdos.Should().BeEmpty("nothing was added");
    }

    /// <summary>What the device's validator would refuse regardless of the peer's state is
    /// refused by Add, before a configuration can delete the peer's SRDOs (§8.4.2.2): a direction
    /// other than tx or rx, COB-IDs outside the odd 101h..17Fh / even 102h..180h pair (a bit above
    /// bit 10 included), and a second SRDO of the configuration on the same COB-IDs.</summary>
    [Theory]
    [InlineData(3, 0x109u, 0x10Au)]
    [InlineData(255, 0x109u, 0x10Au)]
    [InlineData(1, 0x109u, 0x10Cu)]   // COB-ID 2 even and in range, but not COB-ID 1 + 1
    [InlineData(1, 0x10Au, 0x10Bu)]   // COB-ID 1 even
    [InlineData(1, 0x181u, 0x182u)]   // above 17Fh / 180h
    [InlineData(1, 0x0FFu, 0x100u)]   // below 101h / 102h
    [InlineData(1, 0x909u, 0x90Au)]   // a bit above bit 10
    public void Add_Refuses_What_The_Device_Would_Refuse_Anyway(int direction, uint cobId1, uint cobId2)
    {
        var configuration = new PeerSafetyConfiguration()
            .Add(2, new SrdoCommunicationParameter(SrdoDirection.Transmit, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(20), 0x141, 0x142),
                new SrdoMapping().Add(0x2001, 0x00, 8));
        var bad = new SrdoCommunicationParameter((SrdoDirection)direction, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(20), cobId1, cobId2);
        var ex = Assert.ThrowsAny<ArgumentException>(() => configuration.Add(1, bad, new SrdoMapping().Add(0x2001, 0x00, 8)));
        ex.ParamName.Should().Be("parameter");
        ex.Message.Should().Contain("8.4.2.2");
        configuration.Srdos.Keys.Should().Equal(new[] { 2 }, "nothing was added");
    }

    [Fact]
    public void Add_Refuses_A_Second_Srdo_On_The_Same_Cob_Ids()
    {
        var parameter = new SrdoCommunicationParameter(SrdoDirection.Transmit, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(20), 0x109, 0x10A);
        var configuration = new PeerSafetyConfiguration().Add(1, parameter, new SrdoMapping().Add(0x2001, 0x00, 8));
        var ex = Assert.Throws<ArgumentException>(() => configuration.Add(2, parameter, new SrdoMapping().Add(0x2001, 0x00, 8)));
        ex.Message.Should().Contain("SRDO 1");
        configuration.Srdos.Keys.Should().Equal(new[] { 1 }, "nothing was added");
        configuration.Add(1, parameter with { RefreshOrSafeguardCycleTime = TimeSpan.FromMilliseconds(40) }, new SrdoMapping().Add(0x2001, 0x00, 8));
        configuration.Srdos[1].Parameter.RefreshOrSafeguardCycleTime.Should().Be(TimeSpan.FromMilliseconds(40), "replacing an SRDO with its own ids is no collision");
    }

    [Fact]
    public async Task A_Producers_Validation_Time_Is_Written_Because_The_Checksum_Covers_It()
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var master = OpenMasterOnVirtualClock(busA);
        using var device = OpenDevice(busB);
        master.BindPeerDeviceDescription(Device, PeerFile());
        var configuration = new PeerSafetyConfiguration { GlobalFailsafeCommandEnabled = true }
            .Add(1, new SrdoCommunicationParameter(SrdoDirection.Transmit, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(30), 0x109, 0x10A),
                new SrdoMapping().Add(0x2000, 0x00, 16).Add(0x2001, 0x00, 8));
        var result = await master.Safety().ConfigurePeerSafetyAsync(Device, configuration).WithTimeoutAsync(ShortTimeout);
        result.Succeeded.Should().BeTrue(string.Join("\n", result.Mismatches));
        device.ObjectDictionary.ReadUnsigned(0x1301, 3).Should().Be(30u, "the device recomputes the checksum from its stored sub-index 3");
        SrdoRecords.IsConfigurationValid(device.ObjectDictionary, 1).Should().BeTrue();
        var verified = await master.Safety().VerifyPeerSafetyConfigurationAsync(Device, configuration).WithTimeoutAsync(ShortTimeout);
        verified.Succeeded.Should().BeTrue(string.Join("\n", verified.Mismatches));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_Description_Without_A_Validation_Time_Yields_The_Default_And_Configures_The_Peer(bool removeSub3)
    {
        var text = System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "TestCases", "CANopen", "Fixtures", "safety.dcf"));
        int start = text.IndexOf("[1301sub3]", StringComparison.Ordinal);
        int end = text.IndexOf("[1301sub4]", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1);
        end.Should().BeGreaterThan(start);
        text = removeSub3
            ? (text[..start] + text[end..]).Replace("SRDO communication parameter 1\nSubNumber=7", "SRDO communication parameter 1\nSubNumber=6")
            : text[..start] + text[start..end].Replace("DefaultValue=20", "DefaultValue=0") + text[end..]; // declared as 0
        var configuration = PeerSafetyConfiguration.FromDeviceDescription(CanOpenDeviceDescription.ParseDcf(text), Device);
        configuration.Srdos[1].Parameter.ValidationTime.Should().Be(TimeSpan.FromMilliseconds(20));

        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var master = OpenMasterOnVirtualClock(busA);
        using var device = OpenDevice(busB);
        master.BindPeerDeviceDescription(Device, PeerFile());
        var result = await master.Safety().ConfigurePeerSafetyAsync(Device, configuration).WithTimeoutAsync(ShortTimeout);
        result.Succeeded.Should().BeTrue(string.Join("\n", result.Mismatches));
        SrdoRecords.IsConfigurationValid(device.ObjectDictionary, 1).Should().BeTrue();
    }

    [Fact]
    public async Task A_Consumer_With_A_Full_Mapping_Is_Configured_And_Verified()
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var master = OpenMasterOnVirtualClock(busA);
        using var device = OpenDevice(busB);
        var mapping = new SrdoMapping();
        for (byte i = 0; i < 8; i++)
        {
            device.ObjectDictionary.AddU8((ushort)(0x2010 + i), 0x00, 0);
            mapping.Add((ushort)(0x2010 + i), 0x00, 8);
        }
        // The peer's file declares the eight objects, rw and mappable, as the device has them:
        // the tool checks the mapping against it before the first frame.
        master.BindPeerDeviceDescription(Device, PeerFileWith(Enumerable.Range(0x2010, 8).Select(i => ((ushort)i, "0x0005", "rw", true)).ToArray()));
        var configuration = new PeerSafetyConfiguration { GlobalFailsafeCommandEnabled = true }
            .Add(1, new SrdoCommunicationParameter(SrdoDirection.Receive, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(35), 0x109, 0x10A), mapping);
        var result = await master.Safety().ConfigurePeerSafetyAsync(Device, configuration).WithTimeoutAsync(ShortTimeout);
        result.Succeeded.Should().BeTrue(string.Join("\n", result.Mismatches));
        var od = device.ObjectDictionary;
        od.ReadUnsigned(0x1301, 1).Should().Be(2u);
        od.ReadUnsigned(0x1301, 2).Should().Be(50u);
        od.ReadUnsigned(0x1301, 3).Should().Be(35u);
        od.ReadUnsigned(0x1381, 0).Should().Be(16u);
        od.ReadUnsigned(0x1381, 16).Should().Be(0x20170008u, "the eighth object, inverted slot");
        SrdoRecords.IsConfigurationValid(od, 1).Should().BeTrue();
        var verified = await master.Safety().VerifyPeerSafetyConfigurationAsync(Device, configuration).WithTimeoutAsync(ShortTimeout);
        verified.Succeeded.Should().BeTrue(string.Join("\n", verified.Mismatches));
    }

    [Fact]
    public async Task An_Existing_Configuration_Is_Replaced_Because_Every_Srdo_Is_Deleted_First()
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var master = OpenMasterOnVirtualClock(busA);
        using var device = OpenDevice(busB);
        master.BindPeerDeviceDescription(Device, PeerFile());
        (await master.Safety().ConfigurePeerSafetyAsync(Device, Configuration()).WithTimeoutAsync(ShortTimeout)).Succeeded.Should().BeTrue();
        var replacement = new PeerSafetyConfiguration { GlobalFailsafeCommandEnabled = true }
            .Add(1, new SrdoCommunicationParameter(SrdoDirection.Transmit, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(20), 0x10B, 0x10C),
                new SrdoMapping().Add(0x2001, 0x00, 8));
        var result = await master.Safety().ConfigurePeerSafetyAsync(Device, replacement).WithTimeoutAsync(ShortTimeout);
        result.Succeeded.Should().BeTrue(string.Join("\n", result.Mismatches));
        var od = device.ObjectDictionary;
        od.ReadUnsigned(0x1301, 5).Should().Be(0x10Bu);
        od.ReadUnsigned(0x1301, 6).Should().Be(0x10Cu);
        od.ReadUnsigned(0x1381, 0).Should().Be(2u);
        SrdoRecords.IsConfigurationValid(od, 1).Should().BeTrue();
    }

    [Fact]
    public async Task Two_Srdos_Can_Swap_Their_Cob_Ids_Because_All_Are_Deleted_Before_Any_Is_Written()
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var master = OpenMasterOnVirtualClock(busA);
        using var device = OpenDevice(busB);
        master.BindPeerDeviceDescription(Device, PeerFile());
        static PeerSafetyConfiguration Two(uint first, uint second) => new PeerSafetyConfiguration { GlobalFailsafeCommandEnabled = true }
            .Add(1, new SrdoCommunicationParameter(SrdoDirection.Transmit, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(20), first, first + 1),
                new SrdoMapping().Add(0x2000, 0x00, 16).Add(0x2001, 0x00, 8))
            .Add(2, new SrdoCommunicationParameter(SrdoDirection.Transmit, TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(20), second, second + 1),
                new SrdoMapping().Add(0x2001, 0x00, 8));
        var initial = await master.Safety().ConfigurePeerSafetyAsync(Device, Two(0x109, 0x10B)).WithTimeoutAsync(ShortTimeout);
        initial.Succeeded.Should().BeTrue(string.Join("\n", initial.Mismatches));
        var swapped = await master.Safety().ConfigurePeerSafetyAsync(Device, Two(0x10B, 0x109)).WithTimeoutAsync(ShortTimeout);
        swapped.Succeeded.Should().BeTrue(string.Join("\n", swapped.Mismatches));
        var od = device.ObjectDictionary;
        od.ReadUnsigned(0x1301, 5).Should().Be(0x10Bu);
        od.ReadUnsigned(0x1302, 5).Should().Be(0x109u);
        SrdoRecords.IsConfigurationValid(od, 1).Should().BeTrue();
        SrdoRecords.IsConfigurationValid(od, 2).Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_Srdo_Above_The_Peers_Count_Is_Refused_Before_Anything_Is_Sent(bool verify)
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var master = OpenMasterOnVirtualClock(busA);
        using var device = OpenDevice(busB); // SrdoCount = 2
        master.BindPeerDeviceDescription(Device, PeerFile());
        var tooMany = Configuration()
            .Add(3, new SrdoCommunicationParameter(SrdoDirection.Transmit, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(20), 0x10D, 0x10E),
                new SrdoMapping().Add(0x2001, 0x00, 8));
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => verify
            ? master.Safety().VerifyPeerSafetyConfigurationAsync(Device, tooMany).WithTimeoutAsync(ShortTimeout)
            : master.Safety().ConfigurePeerSafetyAsync(Device, tooMany).WithTimeoutAsync(ShortTimeout));
        ex.Message.Should().Contain("SRDO 3").And.Contain("2 SRDO");
        device.ObjectDictionary.ReadUnsigned(0x1300, 0).Should().Be(0u, "nothing was sent");
        device.ObjectDictionary.ReadUnsigned(0x1301, 1).Should().Be(0u, "nothing was sent");
    }

    /// <summary>13FFh:00 is the number of SRDOs, at most 64 (§8.4.2.2). A peer reporting more is
    /// not taken for 64: what it claims beyond could never be configured or verified, so the call
    /// fails before any further frame.</summary>
    [Theory]
    [InlineData((byte)65, false)]
    [InlineData((byte)255, false)]
    [InlineData((byte)65, true)]
    [InlineData((byte)255, true)]
    public async Task A_Count_Above_64_Is_An_Error_Not_64_Srdos(byte count, bool verify)
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var master = OpenMasterOnVirtualClock(busA);
        using var fake = new FakeExpeditedServer(session, 2, Device, lieAt: (0, 0), srdoCount: count);
        master.BindPeerDeviceDescription(Device, PeerFile());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => verify
            ? master.Safety().VerifyPeerSafetyConfigurationAsync(Device, Configuration()).WithTimeoutAsync(ShortTimeout)
            : master.Safety().ConfigurePeerSafetyAsync(Device, Configuration()).WithTimeoutAsync(ShortTimeout));
        ex.Message.Should().Contain(count.ToString(System.Globalization.CultureInfo.InvariantCulture)).And.Contain("64");
        lock (fake.Requests) fake.Requests.Should().Equal(ReadsUpToTheCount(verify), "nothing after the count");
    }

    /// <summary>64 is the maximum and is taken: the verification reaches 1340h with a file whose
    /// highest SRDO record is 64, which implies every record below it.</summary>
    [Fact]
    public async Task A_Count_Of_64_Is_Taken()
    {
        var text = System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "TestCases", "CANopen", "Fixtures", "safety.dcf"))
            .Replace("[1302", "[1340").Replace("[1382", "[13C0").Replace("=0x1302\n", "=0x1340\n").Replace("=0x1382\n", "=0x13C0\n");
        var file = CanOpenDeviceDescription.ParseDcf(text);
        file.Contains(0x1340, 0x01).Should().BeTrue();
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var master = OpenMasterOnVirtualClock(busA);
        using var fake = new FakeExpeditedServer(session, 2, Device, lieAt: (0, 0), srdoCount: 64);
        master.BindPeerDeviceDescription(Device, file);
        var result = await master.Safety().VerifyPeerSafetyConfigurationAsync(Device, new PeerSafetyConfiguration()).WithTimeoutAsync(ShortTimeout);
        lock (fake.Requests) fake.Requests.Should().Contain(((ushort)0x1340, (byte)0x01), "every SRDO up to the count is verified");
        result.Mismatches.Should().ContainSingle(m => m.Index == 0x13FE, "the fake holds no A5h; every record reads 0, as a deleted SRDO should");
    }

    [Fact]
    public async Task A_Missing_Srdo_Count_Is_An_Error_Not_Zero_Srdos()
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var master = OpenMasterOnVirtualClock(busA);
        using var fake = new FakeExpeditedServer(session, 2, Device, lieAt: (0, 0), emptyCount: true);
        master.BindPeerDeviceDescription(Device, PeerFile());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => master.Safety().ConfigurePeerSafetyAsync(Device, Configuration()).WithTimeoutAsync(ShortTimeout));
        ex.Message.Should().Contain("13FFh:00");
        fake.Written.Keys.Should().OnlyContain(k => k.Item1 == 0x13FF || k.Item1 == 0x1018, "nothing was written");
    }

    /// <summary>13FFh:00 is UNSIGNED8 (§8.4.2.2): a response of any other length — none, two or
    /// four bytes — is refused with InvalidOperationException naming the length, before any
    /// further frame, by Configure and Verify alike; its low byte is not taken as the count.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(4, false)]
    [InlineData(0, true)]
    [InlineData(2, true)]
    [InlineData(4, true)]
    public async Task A_Srdo_Count_That_Is_Not_One_Byte_Is_Refused_Before_Any_Further_Frame(int length, bool verify)
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var master = OpenMasterOnVirtualClock(busA); // no SDO deadline on the wall clock (see the helper)
        using var fake = new FakeExpeditedServer(session, 2, Device, lieAt: (0, 0), emptyCount: length == 0);
        if (length > 0)
        {
            var count = new byte[length];
            count[0] = 2;
            lock (fake.Written) fake.Written[(0x13FF, 0)] = count;
        }
        master.BindPeerDeviceDescription(Device, PeerFile());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => verify
            ? master.Safety().VerifyPeerSafetyConfigurationAsync(Device, Configuration()).WithTimeoutAsync(ShortTimeout)
            : master.Safety().ConfigurePeerSafetyAsync(Device, Configuration()).WithTimeoutAsync(ShortTimeout));
        ex.Message.Should().Contain("13FFh:00").And.Contain($"{length} byte");
        lock (fake.Requests) fake.Requests.Should().Equal(ReadsUpToTheCount(verify), "nothing after the count");
    }

    /// <summary>A safety transaction uses one description for its whole length: the one bound at
    /// its start, which its pre-check used. Unbinding the peer, or rebinding it to a file that
    /// declares less (here: no 1300h), while the transaction is held changes nothing for it — it
    /// writes and reads back everything it started with — and takes effect for the next call.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_Rebind_During_A_Safety_Transaction_Takes_Effect_For_The_Next_Call(bool rebind)
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var master = OpenMasterOnVirtualClock(busA);
        using var fake = new FakeExpeditedServer(session, 2, Device, lieAt: (0, 0)) { HoldAt = (0x1301, 0x01) };
        var configure = await HeldConfigurationAsync(master, fake);
        if (rebind) master.BindPeerDeviceDescription(Device, PeerFileWithout1300());
        else master.UnbindPeerDeviceDescription(Device);
        fake.ReleaseHeld();
        var result = await configure.WithTimeoutAsync(ShortTimeout);
        result.Succeeded.Should().BeTrue("the transaction keeps the description it started with");
        lock (fake.Requests)
        {
            fake.Requests.Should().Contain(((ushort)0x1300, (byte)0x00), "1300h was written although the new binding does not allow it");
            fake.Requests.Last().Should().Be(((ushort)0x13FE, (byte)0x00), "the transaction ran to its acknowledgement");
        }
        await Assert.ThrowsAsync<PeerSdoAccessException>(() => master.SdoUploadAsync(Device, 0x1300, 0x00).WithTimeoutAsync(ShortTimeout));
    }

    /// <summary>The transaction works from the description as it was when it started, not from
    /// the bound instance, which is publicly mutable: editing it in place while the transaction is
    /// held — 1300h removed, the PDOMapping flag of a mapped object cleared, the SRDO records
    /// removed — changes nothing for it, and takes effect for the next call.</summary>
    [Fact]
    public async Task An_In_Place_Edit_Of_The_Bound_Description_During_A_Safety_Transaction_Takes_Effect_For_The_Next_Call()
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var master = OpenMasterOnVirtualClock(busA);
        using var fake = new FakeExpeditedServer(session, 2, Device, lieAt: (0, 0)) { HoldAt = (0x1301, 0x01) };
        var configure = await HeldConfigurationAsync(master, fake);
        var bound = master.GetPeerDeviceDescription(Device)!;
        bound.Objects.Objects.Remove(0x1300).Should().BeTrue();
        bound.Objects.Objects[0x2000].PdoMapping = false;
        foreach (ushort index in bound.Objects.Objects.Keys.Where(i => i is > 0x1300 and < 0x13FE).ToList())
            bound.Objects.Objects.Remove(index);
        fake.ReleaseHeld();
        var result = await configure.WithTimeoutAsync(ShortTimeout);
        result.Succeeded.Should().BeTrue("the transaction keeps the description as it was when it started");
        lock (fake.Requests)
        {
            fake.Requests.Should().Contain(((ushort)0x1300, (byte)0x00));
            fake.Requests.Should().Contain(((ushort)0x1381, (byte)0x01));
            fake.Requests.Last().Should().Be(((ushort)0x13FE, (byte)0x00), "the transaction ran to its acknowledgement");
        }
        await Assert.ThrowsAsync<PeerSdoAccessException>(() => master.SdoUploadAsync(Device, 0x1300, 0x00).WithTimeoutAsync(ShortTimeout));
        await Assert.ThrowsAsync<PeerSdoAccessException>(() => master.SdoUploadAsync(Device, 0x1381, 0x01).WithTimeoutAsync(ShortTimeout));
    }

    /// <summary>The fixture without object 1300h (section and optional-object entry).</summary>
    private static CanOpenDeviceDescription PeerFileWithout1300()
    {
        var text = System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "TestCases", "CANopen", "Fixtures", "safety.dcf")).Replace("\r\n", "\n");
        int at = text.IndexOf("\n[1300]\n", StringComparison.Ordinal);
        at.Should().BeGreaterThan(-1);
        int next = text.IndexOf("\n[", at + 1, StringComparison.Ordinal);
        text = text[..at] + text[next..];
        var file = CanOpenDeviceDescription.ParseDcf(text);
        file.Contains(0x1300, 0x00).Should().BeFalse();
        return file;
    }

    /// <summary>The fixture with mapping record 1381h declaring only sub-indices 0..4 — the two
    /// objects of <see cref="Configuration"/>, plain and inverted — as a conforming DCF may: the
    /// peer gate then refuses every slot above them.</summary>
    private static CanOpenDeviceDescription PeerFileDeclaringOnlyTheUsedMappingSlots()
    {
        var text = System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "TestCases", "CANopen", "Fixtures", "safety.dcf"));
        var sb = new System.Text.StringBuilder();
        bool skip = false, in1381 = false;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith('['))
            {
                in1381 = line == "[1381]";
                skip = System.Text.RegularExpressions.Regex.IsMatch(line, @"^\[1381sub([5-9A-F]|10)\]$");
            }
            if (skip) continue;
            sb.Append(in1381 && line.StartsWith("SubNumber=", StringComparison.Ordinal) ? "SubNumber=5" : line).Append('\n');
        }
        var narrow = CanOpenDeviceDescription.ParseDcf(sb.ToString());
        narrow.Contains(0x1381, 0x04).Should().BeTrue();
        narrow.Contains(0x1381, 0x05).Should().BeFalse();
        return narrow;
    }

    /// <summary>§9.2 writes the mapping the configuration has — the count and its 2k slots — and
    /// nothing above it: a slot above the count is not part of the mapping (§8.4.2.2), and a peer
    /// whose file declares only the used slots refuses the others at the gate.</summary>
    [Fact]
    public async Task Configure_Needs_Only_The_Mapping_Slots_It_Uses()
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var master = OpenMasterOnVirtualClock(busA);
        using var device = OpenDevice(busB);
        master.BindPeerDeviceDescription(Device, PeerFileDeclaringOnlyTheUsedMappingSlots());
        var result = await master.Safety().ConfigurePeerSafetyAsync(Device, Configuration()).WithTimeoutAsync(ShortTimeout);
        result.Succeeded.Should().BeTrue(string.Join("\n", result.Mismatches));
        device.ObjectDictionary.ReadUnsigned(0x13FE, 0).Should().Be(0xA5u);
        SrdoRecords.IsConfigurationValid(device.ObjectDictionary, 1).Should().BeTrue();
    }

    /// <summary>Step D reads what §9.2 wrote, so the same file is enough to verify the peer.</summary>
    [Fact]
    public async Task Verify_Needs_Only_The_Mapping_Slots_It_Uses()
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var master = OpenMasterOnVirtualClock(busA);
        using var device = OpenDevice(busB);
        master.BindPeerDeviceDescription(Device, PeerFile());
        (await master.Safety().ConfigurePeerSafetyAsync(Device, Configuration()).WithTimeoutAsync(ShortTimeout)).Succeeded.Should().BeTrue();
        master.BindPeerDeviceDescription(Device, PeerFileDeclaringOnlyTheUsedMappingSlots());
        var verified = await master.Safety().VerifyPeerSafetyConfigurationAsync(Device, Configuration()).WithTimeoutAsync(ShortTimeout);
        verified.Succeeded.Should().BeTrue(string.Join("\n", verified.Mismatches));
    }

    /// <summary>A non-zero slot above the count is no part of the SRDO: the device neither uses
    /// nor checksums it (§8.4.2.2 field g covers sub-indices 1..sub0), so step D does not fail on it.</summary>
    [Fact]
    public async Task Verify_Ignores_A_Leftover_Mapping_Slot_Above_The_Count()
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var master = OpenMasterOnVirtualClock(busA);
        using var device = OpenDevice(busB);
        master.BindPeerDeviceDescription(Device, PeerFile());
        (await master.Safety().ConfigurePeerSafetyAsync(Device, Configuration()).WithTimeoutAsync(ShortTimeout)).Succeeded.Should().BeTrue();
        var od = device.ObjectDictionary;
        od.WriteUnsigned(0x1301, 1, 0);
        od.WriteUnsigned(0x1381, 0, 0);
        od.WriteUnsigned(0x1381, 5, 0x20010008);
        od.WriteUnsigned(0x1381, 6, 0x20010008);
        od.WriteUnsigned(0x1381, 0, 4);
        od.WriteUnsigned(0x1301, 1, 1);
        od.WriteUnsigned(0x13FE, 0, 0xA5);
        SrdoRecords.IsConfigurationValid(od, 1).Should().BeTrue("the device itself finds SRDO 1 valid with the leftover slots");
        var verified = await master.Safety().VerifyPeerSafetyConfigurationAsync(Device, Configuration()).WithTimeoutAsync(ShortTimeout);
        verified.Succeeded.Should().BeTrue(string.Join("\n", verified.Mismatches));
    }

    /// <summary>Step D checks the SRDOs it expects. A deleted SRDO's checksum is not compared:
    /// the device checks the checksums of existing SRDOs only (§9.5, last rule), so a stale one
    /// is harmless; that the SRDO is deleted is still verified through its sub-index 1.</summary>
    [Fact]
    public async Task Verify_Ignores_The_Checksum_Of_A_Deleted_Srdo()
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var master = OpenMasterOnVirtualClock(busA);
        using var device = OpenDevice(busB);
        master.BindPeerDeviceDescription(Device, PeerFile());
        (await master.Safety().ConfigurePeerSafetyAsync(Device, Configuration()).WithTimeoutAsync(ShortTimeout)).Succeeded.Should().BeTrue();
        var od = device.ObjectDictionary;
        od.WriteUnsigned(0x13FF, 2, 0x1234);
        od.WriteUnsigned(0x13FE, 0, 0xA5);
        var verified = await master.Safety().VerifyPeerSafetyConfigurationAsync(Device, Configuration()).WithTimeoutAsync(ShortTimeout);
        verified.Succeeded.Should().BeTrue(string.Join("\n", verified.Mismatches));

        od.WriteUnsigned(0x1302, 5, 0x10B);
        od.WriteUnsigned(0x1302, 6, 0x10C);
        od.WriteUnsigned(0x1302, 1, 1);
        od.WriteUnsigned(0x13FE, 0, 0xA5);
        var created = await master.Safety().VerifyPeerSafetyConfigurationAsync(Device, Configuration()).WithTimeoutAsync(ShortTimeout);
        created.Succeeded.Should().BeFalse("SRDO 2 exists on the peer although the expectation has it deleted");
        created.Mismatches.Should().ContainSingle(m => m.Index == 0x1302 && m.Subindex == 0x01);
    }

    /// <summary>A DCF that declares SRDOs 1 and 3 but not 2: the device holds 1302h deleted and
    /// 13FFh:00 = 3, and a master configures and verifies it with the expectation from the same
    /// file — every record up to the count is reached.</summary>
    [Fact]
    public async Task A_Device_Whose_File_Leaves_An_Srdo_Number_Out_Is_Configured_And_Verified()
    {
        var dcf = CanOpenDeviceDescription.ParseDcf(CanOpenSafetyDeviceDescriptionTests.GappedSafetyDcfText());
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var master = OpenMasterOnVirtualClock(busA);
        using var device = OpenOnVirtualClock(busB, dcf.NodeId!.Value, new CanOpenNodeOptions { WritableCommunicationParameters = true }, dcf);
        device.ObjectDictionary.ReadUnsigned(0x13FF, 0).Should().Be(3u);
        device.ObjectDictionary.ReadUnsigned(0x1302, 1).Should().Be(0u);
        master.BindPeerDeviceDescription(Device, dcf);
        var expected = PeerSafetyConfiguration.FromDeviceDescription(dcf, Device);
        expected.Srdos.Keys.Should().BeEquivalentTo(new[] { 1, 3 });
        var configured = await master.Safety().ConfigurePeerSafetyAsync(Device, expected).WithTimeoutAsync(ShortTimeout);
        configured.Succeeded.Should().BeTrue(string.Join("\n", configured.Mismatches));
        var verified = await master.Safety().VerifyPeerSafetyConfigurationAsync(Device, expected).WithTimeoutAsync(ShortTimeout);
        verified.Succeeded.Should().BeTrue(string.Join("\n", verified.Mismatches));
        SrdoRecords.IsConfigurationValid(device.ObjectDictionary, 3).Should().BeTrue();
    }

    /// <summary>The peer-SDO gate implies what 13FFh:00 = N makes exist (§8.4.2.2): for a file
    /// whose highest SRDO record is N, the sub-indices the device provides for 1301h–(1300h + N)
    /// and 1381h–(1380h + N), declared or not. Nothing beyond: not above N, not past sub-index 6
    /// or 16, and nothing else the file leaves out (FR-CO-029).</summary>
    [Theory]
    [InlineData((ushort)0x1302, (byte)0x01, true)]   // the undeclared SRDO 2
    [InlineData((ushort)0x1302, (byte)0x06, true)]
    [InlineData((ushort)0x1302, (byte)0x00, true)]
    [InlineData((ushort)0x1382, (byte)0x10, true)]
    [InlineData((ushort)0x1382, (byte)0x00, true)]
    [InlineData((ushort)0x1302, (byte)0x07, false)]  // no such sub-index
    [InlineData((ushort)0x1382, (byte)0x11, false)]
    [InlineData((ushort)0x1304, (byte)0x01, false)]  // above the highest declared SRDO
    [InlineData((ushort)0x1384, (byte)0x01, false)]
    [InlineData((ushort)0x1003, (byte)0x00, false)]  // anything else the file leaves out
    public void The_Gate_Implies_The_Srdo_Records_Up_To_The_Highest_Declared_One(ushort index, byte subindex, bool allowed)
    {
        var dcf = CanOpenDeviceDescription.ParseDcf(CanOpenSafetyDeviceDescriptionTests.GappedSafetyDcfText());
        dcf.Contains(index, subindex).Should().BeFalse("the file does not declare it");
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var master = OpenMasterOnVirtualClock(busA);
        master.BindPeerDeviceDescription(Device, dcf);
        var ensure = typeof(CanOpenNode).GetMethod("EnsurePeerSdoAccess", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var ex = Record.Exception(() => ensure.Invoke(master, new object[] { Device, index, subindex }));
        if (allowed) ex.Should().BeNull();
        else ex.Should().BeOfType<System.Reflection.TargetInvocationException>().Which.InnerException.Should().BeOfType<PeerSdoAccessException>();
    }

    [Fact]
    public void Without_An_Srdo_Record_In_The_File_The_Gate_Implies_None()
    {
        var plain = CanOpenDeviceDescription.Load(System.IO.Path.Combine(AppContext.BaseDirectory, "TestCases", "CANopen", "Fixtures", "device.dcf"));
        plain.Contains(0x1301, 0x01).Should().BeFalse();
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var master = OpenMasterOnVirtualClock(busA);
        master.BindPeerDeviceDescription(Device, plain);
        var ensure = typeof(CanOpenNode).GetMethod("EnsurePeerSdoAccess", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var ex = Record.Exception(() => ensure.Invoke(master, new object[] { Device, (ushort)0x1301, (byte)0x01 }));
        ex.Should().BeOfType<System.Reflection.TargetInvocationException>().Which.InnerException.Should().BeOfType<PeerSdoAccessException>();
    }

    /// <summary>The fixture with further application objects declared: index, data type, access type, PDOMapping.</summary>
    private static CanOpenDeviceDescription PeerFileWith(params (ushort Index, string DataType, string Access, bool Mappable)[] objects)
    {
        var text = System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "TestCases", "CANopen", "Fixtures", "safety.dcf")).Replace("\r\n", "\n");
        var list = new System.Text.StringBuilder("[ManufacturerObjects]\nSupportedObjects=" + (2 + objects.Length) + "\n1=0x2000\n2=0x2001\n");
        var sections = new System.Text.StringBuilder();
        for (int i = 0; i < objects.Length; i++)
        {
            var o = objects[i];
            list.Append(i + 3).Append("=0x").Append(o.Index.ToString("X4", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
            var dataType = o.DataType.Length == 0 ? "" : $"DataType={o.DataType}\n";   // "" leaves the key out
            sections.Append($"\n[{o.Index:X4}]\nParameterName=Object {o.Index:X4}\nObjectType=0x7\n{dataType}AccessType={o.Access}\nDefaultValue=0\nPDOMapping={(o.Mappable ? 1 : 0)}\n");
        }
        text = text.Replace("[ManufacturerObjects]\nSupportedObjects=2\n1=0x2000\n2=0x2001\n", list.ToString());
        text.Should().Contain("SupportedObjects=" + (2 + objects.Length));
        return CanOpenDeviceDescription.ParseDcf(text.TrimEnd('\n') + "\n" + sections);
    }

    /// <summary>§9.2 deletes every SRDO of the peer first, so a mapping the peer will refuse
    /// would destroy its configuration. What the bound file shows is checked before the first
    /// frame: the object is declared, flagged mappable (PDOMapping), of a fixed width (no string,
    /// no domain) equal to the one the entry claims, and accessible in the SRDO's direction. What only the peer can judge stays the
    /// peer's.</summary>
    [Theory]
    [InlineData("not mappable")]     // 1300h:00, declared with PDOMapping=0
    [InlineData("not declared")]     // 2005h:00
    [InlineData("sub not declared")] // 2001h:01; 2001h is a VAR
    [InlineData("wrong width")]      // 2000h:00 is UNSIGNED16
    [InlineData("not writable")]     // a consumer on 2001h:00, which is ro
    [InlineData("not readable")]     // a producer on 2007h:00, which is wo
    [InlineData("flag absent")]      // 2006h:00 declared without PDOMapping
    [InlineData("visible string")]   // 2008h:00, VISIBLE_STRING: no fixed width
    [InlineData("domain")]           // 2009h:00, DOMAIN: no fixed width
    [InlineData("no data type")]     // 200Ah:00 declared without DataType: width unknown
    [InlineData("unknown type")]     // 200Bh:00, DataType 0x0010 (INTEGER24), a code this implementation does not map
    public async Task Configure_Refuses_A_Mapping_The_Bound_File_Rules_Out_Before_Any_Frame(string what)
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var master = OpenMasterOnVirtualClock(busA);
        using var fake = new FakeExpeditedServer(session, 2, Device, lieAt: (0, 0));
        var file = PeerFileWith(((ushort)0x2006, "0x0005", "rw", false), ((ushort)0x2007, "0x0005", "wo", true),
            ((ushort)0x2008, "0x0009", "rw", true), ((ushort)0x2009, "0x000F", "rw", true),
            ((ushort)0x200A, "", "rw", true), ((ushort)0x200B, "0x0010", "rw", true));
        if (what == "flag absent")
        {
            var text = System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "TestCases", "CANopen", "Fixtures", "safety.dcf")).Replace("\r\n", "\n")
                .Replace("[ManufacturerObjects]\nSupportedObjects=2\n1=0x2000\n2=0x2001\n", "[ManufacturerObjects]\nSupportedObjects=3\n1=0x2000\n2=0x2001\n3=0x2006\n");
            file = CanOpenDeviceDescription.ParseDcf(text.TrimEnd('\n') + "\n\n[2006]\nParameterName=No flag\nObjectType=0x7\nDataType=0x0005\nAccessType=rw\nDefaultValue=0\n");
            file.Contains(0x2006, 0x00).Should().BeTrue();
        }
        master.BindPeerDeviceDescription(Device, file);
        var (direction, mapping) = what switch
        {
            "not mappable" => (SrdoDirection.Transmit, new SrdoMapping().Add(0x1300, 0x00, 8)),
            "not declared" => (SrdoDirection.Transmit, new SrdoMapping().Add(0x2005, 0x00, 8)),
            "sub not declared" => (SrdoDirection.Transmit, new SrdoMapping().Add(0x2001, 0x01, 8)),
            "wrong width" => (SrdoDirection.Transmit, new SrdoMapping().Add(0x2000, 0x00, 8)),
            "not writable" => (SrdoDirection.Receive, new SrdoMapping().Add(0x2001, 0x00, 8)),
            "not readable" => (SrdoDirection.Transmit, new SrdoMapping().Add(0x2007, 0x00, 8)),
            "visible string" => (SrdoDirection.Transmit, new SrdoMapping().Add(0x2008, 0x00, 16)),
            "domain" => (SrdoDirection.Transmit, new SrdoMapping().Add(0x2009, 0x00, 16)),
            "no data type" => (SrdoDirection.Transmit, new SrdoMapping().Add(0x200A, 0x00, 8)),
            "unknown type" => (SrdoDirection.Transmit, new SrdoMapping().Add(0x200B, 0x00, 24)),
            _ => (SrdoDirection.Transmit, new SrdoMapping().Add(0x2006, 0x00, 8)),
        };
        var configuration = new PeerSafetyConfiguration { GlobalFailsafeCommandEnabled = true }
            .Add(1, new SrdoCommunicationParameter(direction, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(20), 0x109, 0x10A), mapping);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => master.Safety().ConfigurePeerSafetyAsync(Device, configuration).WithTimeoutAsync(ShortTimeout));
        ex.Message.Should().Contain("SRDO 1").And.Contain($"{mapping.Entries[0].Index:X4}h");
        if (what is "visible string" or "domain" or "no data type" or "unknown type") ex.Message.Should().Contain("fixed width");
        lock (fake.Requests) fake.Requests.Should().BeEmpty("nothing was sent, not even the count read");
    }

    /// <summary>A configuration held at its first write (1301h:01), the transaction's SDO channel
    /// to the peer reserved.</summary>
    private static async Task<Task<PeerSafetyResult>> HeldConfigurationAsync(ICanOpenNode master, FakeExpeditedServer fake)
    {
        master.BindPeerDeviceDescription(Device, PeerFile());
        var configure = master.Safety().ConfigurePeerSafetyAsync(Device, Configuration());
        await fake.Holding.WithTimeoutAsync(ShortTimeout); // the transaction is held at 1301h:01
        return configure;
    }

    /// <summary>The channel is free the moment an SDO call is seen completed: the release runs
    /// when the transfer ends, before the call's task completes. A caller's next call to the same
    /// server is therefore not refused as in flight, and an observer right after a call reads live
    /// instead of falling back (FR-CO-030).</summary>
    [Fact]
    public async Task The_Channel_Is_Free_When_An_Sdo_Call_Is_Seen_Completed()
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var master = OpenMasterOnVirtualClock(busA);
        using var fake = new FakeExpeditedServer(session, 2, Device, lieAt: (0, 0));
        master.BindPeerDeviceDescription(Device, PeerFile());
        var node = master;
        for (int i = 0; i < 50; i++)
        {
            await master.SdoUploadAsync(Device, 0x1000, 0x00).WithTimeoutAsync(ShortTimeout);
            node.PeerSdoChannelIsFreeForTests(Device).Should().BeTrue($"call {i} has completed, so its channel is released");
            await master.SdoDownloadAsync(Device, 0x2001, 0x00, new byte[] { (byte)i }).WithTimeoutAsync(ShortTimeout);
            node.PeerSdoChannelIsFreeForTests(Device).Should().BeTrue($"download {i} has completed, so its channel is released");
        }
    }

    /// <summary>The order behind the test above, measured without timing: the release runs while
    /// the handed-out task is still incomplete, for every way a transfer ends.</summary>
    [Theory]
    [InlineData("result")]
    [InlineData("fault")]
    [InlineData("cancel")]
    public void A_Transfer_Releases_Its_Channel_Before_Its_Task_Completes(string ending)
    {
        var newTransfer = typeof(CanOpenNode).GetMethod("NewSdoTransfer", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        Task<byte[]>? handedOut = null;
        bool? completedAtRelease = null;
        Action onEnded = () => completedAtRelease = handedOut!.IsCompleted;
        var args = new object?[] { onEnded, null };
        var transfer = (System.Threading.Tasks.TaskCompletionSource<byte[]>)newTransfer.Invoke(null, args)!;
        handedOut = (Task<byte[]>)args[1]!;
        _ = ending switch
        {
            "result" => transfer.TrySetResult(new byte[] { 1 }),
            "fault" => transfer.TrySetException(new InvalidOperationException("ended")),
            _ => transfer.TrySetCanceled(),
        };
        completedAtRelease.Should().Be(false, "the release ran, and ran before the handed-out task completed");
        handedOut.IsCompleted.Should().BeTrue("the handed-out task completes right after, on the same call");
        handedOut.Status.Should().Be(ending switch { "result" => TaskStatus.RanToCompletion, "fault" => TaskStatus.Faulted, _ => TaskStatus.Canceled });
    }

    /// <summary>Only the channel to that peer is reserved: a call to another server goes out at once.</summary>
    [Fact]
    public async Task An_Sdo_Call_To_Another_Peer_Does_Not_Wait_For_The_Transaction()
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var master = OpenMasterOnVirtualClock(busA);
        using var fake = new FakeExpeditedServer(session, 2, Device, lieAt: (0, 0)) { HoldAt = (0x1301, 0x01) };
        using var other = new FakeExpeditedServer(session, 3, 0x06, lieAt: (0, 0));
        var configure = await HeldConfigurationAsync(master, fake);

        await master.SdoUploadAsync(0x06, 0x1000, 0x00).WithTimeoutAsync(ShortTimeout);
        fake.IsHolding.Should().BeTrue("the transaction is still held");
        fake.ReleaseHeld();
        (await configure.WithTimeoutAsync(ShortTimeout)).Succeeded.Should().BeTrue();
    }

    /// <summary>The peer's SDO channel is reserved for the whole safety transaction: a public SDO
    /// call to that peer during it is refused at once as already in flight — the refusal a call
    /// has always met during a running transfer — and nothing of it reaches the peer between two
    /// of the transaction's transfers.</summary>
    [Fact]
    public async Task An_Sdo_Call_To_The_Peer_During_A_Safety_Transaction_Is_Refused_As_In_Flight()
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var master = OpenMasterOnVirtualClock(busA);
        using var fake = new FakeExpeditedServer(session, 2, Device, lieAt: (0, 0)) { HoldAt = (0x1301, 0x01) };
        var configure = await HeldConfigurationAsync(master, fake);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => master.SdoUploadAsync(Device, 0x1000, 0x00).WithTimeoutAsync(ShortTimeout));
        refused.Message.Should().Contain("already in flight");
        await Assert.ThrowsAsync<InvalidOperationException>(() => master.SdoDownloadAsync(Device, 0x2001, 0x00, new byte[] { 0x5A }).WithTimeoutAsync(ShortTimeout));
        fake.IsHolding.Should().BeTrue("refused while the transaction is still held");
        fake.ReleaseHeld();
        (await configure.WithTimeoutAsync(ShortTimeout)).Succeeded.Should().BeTrue();
        lock (fake.Requests)
            fake.Requests.Should().NotContain(r => r.Index == 0x1000 || r.Index == 0x2001, "only the transaction's requests reached the server");
    }

    /// <summary>The finding's case: between the readback and the 13FEh = A5h write no transfer of
    /// the transaction is in flight, but the channel is still reserved — a write to a safety
    /// parameter there is refused, so the acknowledged checksum cannot have gone stale.</summary>
    [Fact]
    public async Task A_Write_Between_The_Readback_And_The_Acknowledgement_Is_Refused()
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var master = OpenMasterOnVirtualClock(busA);
        using var fake = new FakeExpeditedServer(session, 2, Device, lieAt: (0, 0));
        master.BindPeerDeviceDescription(Device, PeerFile());
        var paused = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        master.AfterSafetyReadbackForTests = () => { paused.TrySetResult(true); return resume.Task; };
        var configure = master.Safety().ConfigurePeerSafetyAsync(Device, Configuration());
        await paused.Task.WithTimeoutAsync(ShortTimeout);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => master.SdoDownloadAsync(Device, 0x1301, 0x02, new byte[] { 40, 0 }).WithTimeoutAsync(ShortTimeout));
        refused.Message.Should().Contain("already in flight");
        resume.TrySetResult(true);
        (await configure.WithTimeoutAsync(ShortTimeout)).Succeeded.Should().BeTrue();
        lock (fake.Written) fake.Written[(0x1301, 0x02)].Should().Equal(new byte[] { 30, 0 }, "the acknowledged value is the one the transaction wrote");
        lock (fake.Requests) fake.Requests.Count(r => r.Index == 0x1301 && r.Subindex == 0x02).Should().Be(2, "the transaction's write and readback, nothing else");
    }

    /// <summary>A transaction started while a public call to the peer is in flight waits for that
    /// call to end and only then sends its first frame.</summary>
    [Fact]
    public async Task A_Safety_Transaction_Waits_For_A_Call_In_Flight()
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var master = OpenMasterOnVirtualClock(busA);
        using var fake = new FakeExpeditedServer(session, 2, Device, lieAt: (0, 0)) { HoldAt = (0x1000, 0x00) };
        master.BindPeerDeviceDescription(Device, PeerFile());
        var upload = master.SdoUploadAsync(Device, 0x1000, 0x00);
        await fake.Holding.WithTimeoutAsync(ShortTimeout); // the public upload is held

        var configure = master.Safety().ConfigurePeerSafetyAsync(Device, Configuration());
        fake.ReleaseHeld();
        await upload.WithTimeoutAsync(ShortTimeout);
        (await configure.WithTimeoutAsync(ShortTimeout)).Succeeded.Should().BeTrue();
        lock (fake.Requests)
        {
            fake.Requests[0].Should().Be(((ushort)0x1000, (byte)0x00));
            fake.Requests[1].Should().Be(((ushort)0x1018, (byte)0x01), "the transaction's first frame — the identity — comes after the call it waited for");
        }
    }

    /// <summary>A transaction starts when it owns the peer's channel: the snapshot of the
    /// description, and the pre-check against it, are taken then, not when the call was made. An
    /// edit landing while it waits for a call in flight is therefore seen — here the PDOMapping flag
    /// of a mapped object cleared, which the pre-check refuses — and nothing of it is sent.</summary>
    [Fact]
    public async Task An_Edit_While_The_Transaction_Waits_For_The_Channel_Is_Seen_By_It()
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var master = OpenMasterOnVirtualClock(busA);
        using var fake = new FakeExpeditedServer(session, 2, Device, lieAt: (0, 0)) { HoldAt = (0x1000, 0x00) };
        master.BindPeerDeviceDescription(Device, PeerFile());
        var upload = master.SdoUploadAsync(Device, 0x1000, 0x00);
        await fake.Holding.WithTimeoutAsync(ShortTimeout); // the public upload holds the channel

        var configure = master.Safety().ConfigurePeerSafetyAsync(Device, Configuration()); // waits for the channel
        master.GetPeerDeviceDescription(Device)!.Objects.Objects[0x2000].PdoMapping = false;
        fake.ReleaseHeld();
        await upload.WithTimeoutAsync(ShortTimeout);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => configure.WithTimeoutAsync(ShortTimeout));
        ex.Message.Should().Contain("2000h").And.Contain("PDOMapping");
        lock (fake.Requests) fake.Requests.Should().Equal(new (ushort, byte)[] { (0x1000, 0x00) }, "the refused transaction sent nothing");
        master.PeerSdoChannelIsFreeForTests(Device).Should().BeTrue("the refusal released the channel");
    }

    /// <summary>The transaction works from the configuration as it was when it started — when it
    /// owns the peer's channel, before its first frame: an SRDO added to the caller's instance
    /// while the count upload is held (for a configuration, after its three identity reads) is
    /// neither checked, written nor compared, for Configure and Verify alike.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_Srdo_Added_During_A_Safety_Transaction_Is_Not_Part_Of_It(bool verify)
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var master = OpenMasterOnVirtualClock(busA);
        using var fake = new FakeExpeditedServer(session, 2, Device, lieAt: (0, 0)) { HoldAt = (0x13FF, 0x00) };
        master.BindPeerDeviceDescription(Device, PeerFile());
        var configuration = Configuration();
        var transaction = verify
            ? master.Safety().VerifyPeerSafetyConfigurationAsync(Device, configuration)
            : master.Safety().ConfigurePeerSafetyAsync(Device, configuration);
        await fake.Holding.WithTimeoutAsync(ShortTimeout); // the count upload is held

        configuration.Add(2, new SrdoCommunicationParameter(SrdoDirection.Transmit, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(20), 0x10B, 0x10C),
            new SrdoMapping().Add(0x2001, 0x00, 8));
        fake.ReleaseHeld();
        var result = await transaction.WithTimeoutAsync(ShortTimeout);
        if (!verify) result.Succeeded.Should().BeTrue();
        lock (fake.Requests)
        {
            fake.Requests.Should().NotContain(((ushort)0x1302, (byte)0x05), "SRDO 2 was added after the transaction started");
            fake.Requests.Should().NotContain(r => r.Index == 0x1382, "nor its mapping");
        }
        if (!verify)
            lock (fake.Written)
            {
                fake.Written[(0x1302, 0x01)].Should().Equal(new byte[] { 0 }, "SRDO 2 stays deleted, as the configuration was at the start");
                fake.Written[(0x13FF, 0x02)].Should().Equal(new byte[] { 0, 0 });
            }
    }

    /// <summary>The transaction's own token cancels its wait for the channel; nothing of it is sent.</summary>
    [Fact]
    public async Task A_Safety_Transaction_Waiting_For_The_Channel_Is_Cancelled_By_Its_Token()
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var master = OpenMasterOnVirtualClock(busA);
        using var fake = new FakeExpeditedServer(session, 2, Device, lieAt: (0, 0)) { HoldAt = (0x1000, 0x00) };
        master.BindPeerDeviceDescription(Device, PeerFile());
        var upload = master.SdoUploadAsync(Device, 0x1000, 0x00);
        await fake.Holding.WithTimeoutAsync(ShortTimeout); // the public upload is held

        using var cts = new System.Threading.CancellationTokenSource();
        var configure = master.Safety().ConfigurePeerSafetyAsync(Device, Configuration(), cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => configure.WithTimeoutAsync(ShortTimeout));
        fake.IsHolding.Should().BeTrue("cancelled while the call it waited for is still held");
        fake.ReleaseHeld();
        await upload.WithTimeoutAsync(ShortTimeout);
        lock (fake.Requests) fake.Requests.Should().Equal(new (ushort, byte)[] { (0x1000, 0x00) }, "the cancelled transaction sent nothing");
    }

    /// <summary>An observer during a safety transaction does not reach the peer either: its live
    /// read meets the busy channel and it falls back to the file (FR-CO-030).</summary>
    [Fact]
    public async Task An_Observer_During_A_Safety_Transaction_Falls_Back_To_The_File()
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var master = OpenMasterOnVirtualClock(busA);
        using var fake = new FakeExpeditedServer(session, 2, Device, lieAt: (0, 0)) { HoldAt = (0x1301, 0x01) };
        var configure = await HeldConfigurationAsync(master, fake);
        int before;
        lock (fake.Requests) before = fake.Requests.Count;

        var sink = new ListSink();
        var result = await master.Safety().ObserveForeignSrdoAsync(Device, 0x109, new byte[] { 0x34, 0x12, 0x5A }, new byte[] { 0xCB, 0xED, 0xA5 }, PeerFile(), sink)
            .WithTimeoutAsync(ShortTimeout);
        result.Observation!.Decoded.Should().BeTrue(result.Observation.Reason);
        result.Observation.Origin.Should().Be(ForeignPdoMappingOrigin.DeviceDescription);
        lock (fake.Requests) fake.Requests.Count.Should().Be(before, "the observer sent nothing during the transaction");
        fake.ReleaseHeld();
        (await configure.WithTimeoutAsync(ShortTimeout)).Succeeded.Should().BeTrue();
    }

    /// <summary>The client's side of a zero-byte upload, pinned: an expedited response cannot carry
    /// 0 bytes (s = 1 encodes 1..4, s = 0 means four), so the fake answers with CiA 301's segmented
    /// upload: an initiate with size indicated 0 (41h), then, on the client's one segment request,
    /// a last segment with no data (c = 1, n = 7). The client completes with an empty value
    /// after exactly one segment request; on a virtual clock no deadline can end it first.</summary>
    [Fact]
    public async Task A_Zero_Byte_Upload_Completes_After_One_Empty_Last_Segment()
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var master = OpenMasterOnVirtualClock(busA);
        using var fake = new FakeExpeditedServer(session, 2, Device, lieAt: (0, 0), emptyCount: true);
        master.BindPeerDeviceDescription(Device, PeerFile());
        var value = await master.SdoUploadAsync(Device, 0x13FF, 0x00).WithTimeoutAsync(ShortTimeout);
        value.Should().BeEmpty();
        fake.SegmentRequests.Should().Be(1);
    }

    private sealed class ListSink : IForeignPdoSink
    {
        public readonly List<ForeignPdoSignal> Signals = new();
        public void Write(ForeignPdoSignal signal) => Signals.Add(signal);
    }

    /// <summary>An SDO server for expedited transfers only: stores downloads, answers uploads
    /// with what was stored (13FFh:00 reads 2, or nothing when asked to), and answers one upload with the stored value
    /// plus one so the readback differs.</summary>
    private sealed class FakeExpeditedServer : IDisposable
    {
        private readonly ICanBus _bus;
        private readonly byte _nodeId;
        private readonly (ushort, byte) _lieAt;
        public readonly Dictionary<(ushort, byte), byte[]> Written = new();
        public readonly List<(ushort Index, byte Subindex)> Requests = new();
        /// <summary>Pairs whose download and upload are answered with this abort code: what the fake does not implement.</summary>
        public readonly Dictionary<(ushort, byte), uint> AbortAt = new();
        /// <summary>The first request to this pair is answered only on <see cref="ReleaseHeld"/>.</summary>
        public (ushort, byte)? HoldAt { get; init; }
        private Action? _heldReply;
        private bool _held;
        public bool IsHolding { get { lock (Requests) return _heldReply is not null; } }
        private readonly TaskCompletionSource<bool> _holding = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Completes when the first request to <see cref="HoldAt"/> is being held.</summary>
        public Task Holding => _holding.Task;
        private int _segmentRequests;
        /// <summary>Upload segment requests seen (ccs 3).</summary>
        public int SegmentRequests => Volatile.Read(ref _segmentRequests);
        public void ReleaseHeld()
        {
            Action? reply;
            lock (Requests) { reply = _heldReply; _heldReply = null; }
            reply?.Invoke();
        }
        public FakeExpeditedServer(string session, int channel, byte nodeId, (ushort, byte) lieAt, bool emptyCount = false, byte srdoCount = 2)
        {
            _nodeId = nodeId;
            _lieAt = lieAt;
            Written[(0x13FF, 0)] = emptyCount ? Array.Empty<byte>() : new byte[] { srdoCount };
            // The identity of the fixture: CiA 319 §5.2 reads it before anything else.
            Written[(0x1018, 1)] = BitConverter.GetBytes(VendorId);
            Written[(0x1018, 2)] = BitConverter.GetBytes(ProductCode);
            Written[(0x1018, 3)] = BitConverter.GetBytes(RevisionNumber);
            _bus = Open(session, channel);
            _bus.FrameObserved += (_, e) =>
            {
                var f = e.CanFrame;
                if (f.IsExtendedFrame || f.IsRemoteFrame || f.ID != 0x600 + _nodeId || f.Data.Length < 4) return;
                var d = f.Data.ToArray();
                ushort index = (ushort)(d[1] | (d[2] << 8));
                byte sub = d[3];
                if ((d[0] & 0xE0) is 0x20 or 0x40) lock (Requests) Requests.Add((index, sub));
                byte[] reply;
                uint abort;
                lock (Requests) AbortAt.TryGetValue((index, sub), out abort);
                if ((d[0] & 0xE0) is 0x20 or 0x40 && abort != 0)
                    reply = new byte[] { 0x80, d[1], d[2], d[3], (byte)abort, (byte)(abort >> 8), (byte)(abort >> 16), (byte)(abort >> 24) };
                else if ((d[0] & 0xE0) == 0x20)
                {
                    int n = (d[0] & 0x01) != 0 ? 4 - ((d[0] >> 2) & 0x03) : 4;
                    lock (Written) Written[(index, sub)] = d.Skip(4).Take(n).ToArray();
                    reply = new byte[] { 0x60, d[1], d[2], d[3], 0, 0, 0, 0 };
                }
                else if ((d[0] & 0xEF) == 0x60)
                {
                    Interlocked.Increment(ref _segmentRequests);
                    // Segment request for the one empty value: last segment, no data bytes (n = 7, c = 1).
                    reply = new byte[] { (byte)(0x0F | (d[0] & 0x10)), 0, 0, 0, 0, 0, 0, 0 };
                }
                else if (d[0] == 0x40)
                {
                    byte[] value;
                    lock (Written) value = Written.TryGetValue((index, sub), out var v) ? (byte[])v.Clone() : new byte[] { 0 };
                    if (value.Length == 0)
                    {
                        // A segmented upload of zero bytes (size indicated, 0).
                        _bus.Transmit(CanFrame.Classic(0x580 + _nodeId, new byte[] { 0x41, d[1], d[2], d[3], 0, 0, 0, 0 }, isExtendedFrame: false));
                        return;
                    }
                    if ((index, sub) == _lieAt) value[0]++;
                    reply = new byte[8];
                    reply[0] = (byte)(0x43 | ((4 - value.Length) << 2));
                    reply[1] = d[1]; reply[2] = d[2]; reply[3] = d[3];
                    Array.Copy(value, 0, reply, 4, value.Length);
                }
                else return;
                lock (Requests)
                {
                    if (!_held && HoldAt == (index, sub) && (d[0] & 0xE0) is 0x20 or 0x40)
                    {
                        _held = true;
                        _heldReply = () => _bus.Transmit(CanFrame.Classic(0x580 + _nodeId, reply, isExtendedFrame: false));
                        _holding.TrySetResult(true);
                        return;
                    }
                }
                _bus.Transmit(CanFrame.Classic(0x580 + _nodeId, reply, isExtendedFrame: false));
            };
        }
        public void Dispose() => _bus.Dispose();
    }
}
