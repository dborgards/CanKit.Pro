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
    public async Task A_Time_Outside_Its_Range_Is_Refused_Before_Any_Frame_Is_Sent(SrdoDirection direction, int cycleMs, int validationMs)
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var master = CanOpen.OpenNode(busA, Master);
        using var device = OpenDevice(busB);
        master.BindPeerDeviceDescription(Device, PeerFile());
        var bad = new PeerSafetyConfiguration { GlobalFailsafeCommandEnabled = true }
            .Add(1, new SrdoCommunicationParameter(direction, TimeSpan.FromMilliseconds(cycleMs), TimeSpan.FromMilliseconds(validationMs), 0x109, 0x10A),
                new SrdoMapping().Add(0x2000, 0x00, 16).Add(0x2001, 0x00, 8));
        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => master.Safety().ConfigurePeerSafetyAsync(Device, bad).WithTimeoutAsync(ShortTimeout));
        ex.Message.Should().Contain("SRDO 1");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => master.Safety().VerifyPeerSafetyConfigurationAsync(Device, bad).WithTimeoutAsync(ShortTimeout));
        device.ObjectDictionary.ReadUnsigned(0x1300, 0).Should().Be(0u, "nothing was sent");
        device.ObjectDictionary.ReadUnsigned(0x1301, 1).Should().Be(0u, "nothing was sent");
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
        if (removeSub3)
            text = (text[..start] + text[end..]).Replace("SRDO communication parameter 1\nSubNumber=7", "SRDO communication parameter 1\nSubNumber=6");
        else
            text = text[..start] + text[start..end].Replace("DefaultValue=20", "DefaultValue=0") + text[end..]; // declared as 0
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
        master.BindPeerDeviceDescription(Device, PeerFile());
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

    /// <summary>An SDO server for expedited transfers only: stores downloads, answers uploads
    /// with what was stored (13FFh:00 reads 2, or nothing when asked to), and answers one upload with the stored value
    /// plus one so the readback differs.</summary>
    private sealed class FakeExpeditedServer : IDisposable
    {
        private readonly ICanBus _bus;
        private readonly byte _nodeId;
        private readonly (ushort, byte) _lieAt;
        public readonly Dictionary<(ushort, byte), byte[]> Written = new();
        public FakeExpeditedServer(string session, int channel, byte nodeId, (ushort, byte) lieAt, bool emptyCount = false)
        {
            _nodeId = nodeId;
            _lieAt = lieAt;
            Written[(0x13FF, 0)] = emptyCount ? Array.Empty<byte>() : new byte[] { 2 };
            _bus = Open(session, channel);
            _bus.FrameObserved += (_, e) =>
            {
                var f = e.CanFrame;
                if (f.IsExtendedFrame || f.IsRemoteFrame || f.ID != 0x600 + _nodeId || f.Data.Length < 4) return;
                var d = f.Data.ToArray();
                ushort index = (ushort)(d[1] | (d[2] << 8));
                byte sub = d[3];
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
                _bus.Transmit(CanFrame.Classic(0x580 + _nodeId, reply, isExtendedFrame: false));
            };
        }
        public void Dispose() => _bus.Dispose();
    }
}
