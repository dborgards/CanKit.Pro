using System;
using System.Collections.Generic;
using System.Linq;
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
/// Figure 9) and verifies them (§8.3.1 step D). The device is a real node; the mismatch path
/// uses a fake expedited SDO server on a raw channel that answers one readback wrongly
/// (FR-CO-044).</summary>
public class CanOpenPeerSafetyTests : IClassFixture<VirtualAdapterFixture>
{
    private const byte Master = 0x01;
    private const byte Device = 0x05;
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);

    private static ICanBus Open(string session, int channel) => CanBus.Open(
        $"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(VirtualAdapterFixture.Bitrate));

    /// <summary>The file that lists every safety object of node 5 (the peer gate reads it).</summary>
    private static CanOpenDeviceDescription PeerFile()
        => CanOpenDeviceDescription.ParseDcf(System.IO.File.ReadAllText(
            System.IO.Path.Combine(AppContext.BaseDirectory, "TestCases", "CANopen", "Fixtures", "safety.dcf")));

    private static PeerSafetyConfiguration Configuration() => new PeerSafetyConfiguration { GlobalFailsafeCommandEnabled = true }
        .Add(1, new SrdoCommunicationParameter(SrdoDirection.Transmit, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(20), 0x109, 0x10A),
            new SrdoMapping().Add(0x2000, 0x00, 16).Add(0x2001, 0x00, 8));

    private static ICanOpenNode OpenDevice(ICanBus bus)
    {
        var device = CanOpen.OpenNode(bus, Device, new CanOpenNodeOptions { SrdoCount = 2, WritableCommunicationParameters = true });
        device.ObjectDictionary.AddU16(0x2000, 0x00, 0x1234);
        device.ObjectDictionary.AddU8(0x2001, 0x00, 0x5A);
        return device;
    }

    [Fact]
    public async Task Configure_Downloads_Reads_Back_And_Acknowledges()
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var master = CanOpen.OpenNode(busA, Master);
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

    [Fact]
    public async Task Configure_Is_Refused_Without_A_Bound_Description()
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var master = CanOpen.OpenNode(busA, Master);
        using var device = OpenDevice(busB);
        await Assert.ThrowsAsync<PeerSdoAccessException>(() => master.Safety().ConfigurePeerSafetyAsync(Device, Configuration()).WithTimeoutAsync(ShortTimeout));
        device.ObjectDictionary.ReadUnsigned(0x1301, 1).Should().Be(0u, "nothing was sent");
    }

    [Fact]
    public async Task Configure_Aborts_When_The_Peer_Is_Operational()
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var master = CanOpen.OpenNode(busA, Master);
        using var device = OpenDevice(busB);
        master.BindPeerDeviceDescription(Device, PeerFile());
        device.ObjectDictionary.WriteUnsigned(0x13FE, 0, 0xA5);
        await master.SendNmtCommandAsync(NmtCommand.Start, Device);
        var deadline = DateTime.UtcNow + ShortTimeout;
        while (device.State != NmtState.Operational && DateTime.UtcNow < deadline) await Task.Delay(5);
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
        using var master = CanOpen.OpenNode(busA, Master);
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
        using var master = CanOpen.OpenNode(busA, Master);
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
        using var master = CanOpen.OpenNode(busA, Master);
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
        using var master = CanOpen.OpenNode(busA, Master);
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
        using var master = CanOpen.OpenNode(busA, Master);
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
        using var master = CanOpen.OpenNode(busA, Master);
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
        using var master = CanOpen.OpenNode(busA, Master);
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
        using var master = CanOpen.OpenNode(busA, Master);
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
        using var master = CanOpen.OpenNode(busA, Master);
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
        using var master = CanOpen.OpenNode(busA, Master);
        using var fake = new FakeExpeditedServer(session, 2, Device, lieAt: (0, 0), srdoCount: count);
        master.BindPeerDeviceDescription(Device, PeerFile());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => verify
            ? master.Safety().VerifyPeerSafetyConfigurationAsync(Device, Configuration()).WithTimeoutAsync(ShortTimeout)
            : master.Safety().ConfigurePeerSafetyAsync(Device, Configuration()).WithTimeoutAsync(ShortTimeout));
        ex.Message.Should().Contain(count.ToString(System.Globalization.CultureInfo.InvariantCulture)).And.Contain("64");
        lock (fake.Requests) fake.Requests.Should().Equal(new[] { ((ushort)0x13FF, (byte)0) }, "nothing after the count");
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
        using var master = CanOpen.OpenNode(busA, Master);
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
        using var master = CanOpen.OpenNode(busA, Master);
        using var fake = new FakeExpeditedServer(session, 2, Device, lieAt: (0, 0), emptyCount: true);
        master.BindPeerDeviceDescription(Device, PeerFile());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => master.Safety().ConfigurePeerSafetyAsync(Device, Configuration()).WithTimeoutAsync(ShortTimeout));
        ex.Message.Should().Contain("13FFh:00");
        fake.Written.Keys.Should().OnlyContain(k => k.Item1 == 0x13FF, "nothing was written");
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
    /// nothing above it: a slot above the count is not part of the mapping (§8.4.2.3), and a peer
    /// whose file declares only the used slots refuses the others at the gate.</summary>
    [Fact]
    public async Task Configure_Needs_Only_The_Mapping_Slots_It_Uses()
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var master = CanOpen.OpenNode(busA, Master);
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
        using var master = CanOpen.OpenNode(busA, Master);
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
        using var master = CanOpen.OpenNode(busA, Master);
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
        using var master = CanOpen.OpenNode(busA, Master);
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
        using var master = CanOpen.OpenNode(busA, Master);
        using var device = CanOpen.OpenNode(busB, dcf, new CanOpenNodeOptions { WritableCommunicationParameters = true });
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
        using var master = CanOpen.OpenNode(busA, Master);
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
        using var master = CanOpen.OpenNode(busA, Master);
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
            sections.Append($"\n[{o.Index:X4}]\nParameterName=Object {o.Index:X4}\nObjectType=0x7\nDataType={o.DataType}\nAccessType={o.Access}\nDefaultValue=0\nPDOMapping={(o.Mappable ? 1 : 0)}\n");
        }
        text = text.Replace("[ManufacturerObjects]\nSupportedObjects=2\n1=0x2000\n2=0x2001\n", list.ToString());
        text.Should().Contain("SupportedObjects=" + (2 + objects.Length));
        return CanOpenDeviceDescription.ParseDcf(text.TrimEnd('\n') + "\n" + sections);
    }

    /// <summary>§9.2 deletes every SRDO of the peer first, so a mapping the peer will refuse
    /// would destroy its configuration. What the bound file shows is checked before the first
    /// frame: the object is declared, flagged mappable (PDOMapping), of the width the entry
    /// claims, and accessible in the SRDO's direction. What only the peer can judge stays the
    /// peer's.</summary>
    [Theory]
    [InlineData("not mappable")]     // 1300h:00, declared with PDOMapping=0
    [InlineData("not declared")]     // 2005h:00
    [InlineData("sub not declared")] // 2001h:01; 2001h is a VAR
    [InlineData("wrong width")]      // 2000h:00 is UNSIGNED16
    [InlineData("not writable")]     // a consumer on 2001h:00, which is ro
    [InlineData("not readable")]     // a producer on 2007h:00, which is wo
    [InlineData("flag absent")]      // 2006h:00 declared without PDOMapping
    public async Task Configure_Refuses_A_Mapping_The_Bound_File_Rules_Out_Before_Any_Frame(string what)
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, Master);
        using var fake = new FakeExpeditedServer(session, 2, Device, lieAt: (0, 0));
        var file = PeerFileWith(((ushort)0x2006, "0x0005", "rw", false), ((ushort)0x2007, "0x0005", "wo", true));
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
            _ => (SrdoDirection.Transmit, new SrdoMapping().Add(0x2006, 0x00, 8)),
        };
        var configuration = new PeerSafetyConfiguration { GlobalFailsafeCommandEnabled = true }
            .Add(1, new SrdoCommunicationParameter(direction, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(20), 0x109, 0x10A), mapping);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => master.Safety().ConfigurePeerSafetyAsync(Device, configuration).WithTimeoutAsync(ShortTimeout));
        ex.Message.Should().Contain("SRDO 1").And.Contain($"{mapping.Entries[0].Index:X4}h");
        lock (fake.Requests) fake.Requests.Should().BeEmpty("nothing was sent, not even the count read");
    }

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + ShortTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(what);
            await Task.Delay(5);
        }
    }

    /// <summary>A configuration held at its first write (1301h:01), the transaction's SDO channel
    /// to the peer reserved.</summary>
    private static async Task<Task<PeerSafetyResult>> HeldConfigurationAsync(ICanOpenNode master, FakeExpeditedServer fake)
    {
        master.BindPeerDeviceDescription(Device, PeerFile());
        var configure = master.Safety().ConfigurePeerSafetyAsync(Device, Configuration());
        await UntilAsync(() => fake.IsHolding, "the transaction is held at 1301h:01");
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
        using var master = CanOpen.OpenNode(busA, Master);
        using var fake = new FakeExpeditedServer(session, 2, Device, lieAt: (0, 0));
        master.BindPeerDeviceDescription(Device, PeerFile());
        var node = (CanOpenNode)master;
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
        using var master = CanOpen.OpenNode(busA, Master);
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
        using var master = CanOpen.OpenNode(busA, Master);
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
        using var master = CanOpen.OpenNode(busA, Master);
        using var fake = new FakeExpeditedServer(session, 2, Device, lieAt: (0, 0));
        master.BindPeerDeviceDescription(Device, PeerFile());
        var paused = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ((CanOpenNode)master).AfterSafetyReadbackForTests = () => { paused.TrySetResult(true); return resume.Task; };
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
        using var master = CanOpen.OpenNode(busA, Master);
        using var fake = new FakeExpeditedServer(session, 2, Device, lieAt: (0, 0)) { HoldAt = (0x1000, 0x00) };
        master.BindPeerDeviceDescription(Device, PeerFile());
        var upload = master.SdoUploadAsync(Device, 0x1000, 0x00);
        await UntilAsync(() => fake.IsHolding, "the public upload is held");

        var configure = master.Safety().ConfigurePeerSafetyAsync(Device, Configuration());
        fake.ReleaseHeld();
        await upload.WithTimeoutAsync(ShortTimeout);
        (await configure.WithTimeoutAsync(ShortTimeout)).Succeeded.Should().BeTrue();
        lock (fake.Requests)
        {
            fake.Requests[0].Should().Be(((ushort)0x1000, (byte)0x00));
            fake.Requests[1].Should().Be(((ushort)0x13FF, (byte)0x00), "the transaction's first frame comes after the call it waited for");
        }
    }

    /// <summary>The transaction's own token cancels its wait for the channel; nothing of it is sent.</summary>
    [Fact]
    public async Task A_Safety_Transaction_Waiting_For_The_Channel_Is_Cancelled_By_Its_Token()
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, Master);
        using var fake = new FakeExpeditedServer(session, 2, Device, lieAt: (0, 0)) { HoldAt = (0x1000, 0x00) };
        master.BindPeerDeviceDescription(Device, PeerFile());
        var upload = master.SdoUploadAsync(Device, 0x1000, 0x00);
        await UntilAsync(() => fake.IsHolding, "the public upload is held");

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
        using var master = CanOpen.OpenNode(busA, Master);
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
        /// <summary>The first request to this pair is answered only on <see cref="ReleaseHeld"/>.</summary>
        public (ushort, byte)? HoldAt { get; init; }
        private Action? _heldReply;
        private bool _held;
        public bool IsHolding { get { lock (Requests) return _heldReply is not null; } }
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
                if ((d[0] & 0xE0) == 0x20)
                {
                    int n = (d[0] & 0x01) != 0 ? 4 - ((d[0] >> 2) & 0x03) : 4;
                    lock (Written) Written[(index, sub)] = d.Skip(4).Take(n).ToArray();
                    reply = new byte[] { 0x60, d[1], d[2], d[3], 0, 0, 0, 0 };
                }
                else if ((d[0] & 0xEF) == 0x60)
                {
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
                        return;
                    }
                }
                _bus.Transmit(CanFrame.Classic(0x580 + _nodeId, reply, isExtendedFrame: false));
            };
        }
        public void Dispose() => _bus.Dispose();
    }
}
