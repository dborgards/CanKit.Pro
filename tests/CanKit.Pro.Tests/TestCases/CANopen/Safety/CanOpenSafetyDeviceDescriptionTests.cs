using System;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Safety;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Safety;

/// <summary>The safety objects in an EDS/DCF (CiA 306) are loaded like the PDO records: the
/// records the file declares exist, their values are taken through the validated path, and
/// every deviation is a finding (FR-CO-025..028 applied to CiA 304).</summary>
public class CanOpenSafetyDeviceDescriptionTests : IClassFixture<VirtualAdapterFixture>
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "TestCases", "CANopen", "Fixtures", name);

    private static ICanBus Open(string session, int channel) => CanBus.Open(
        $"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(VirtualAdapterFixture.Bitrate));

    private static string SafetyDcfText() => File.ReadAllText(Fixture("safety.dcf"));

    /// <summary>The fixture with 13FFh:01 set to the checksum this implementation computes.</summary>
    private static CanOpenDeviceDescription SafetyDcf()
    {
        var parameter = new SrdoCommunicationParameter(SrdoDirection.Transmit, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(20), 0x109, 0x10A);
        var mapping = new SrdoMapping().Add(0x2000, 0x00, 16).Add(0x2001, 0x00, 8);
        ushort crc = SrdoCrc.Compute(parameter, mapping);
        var text = SafetyDcfText().Replace("ParameterValue=0x0000", $"ParameterValue=0x{crc:X4}");
        return CanOpenDeviceDescription.ParseDcf(text);
    }

    [Fact]
    public void Srdo_Count_Follows_The_Highest_Declared_Record()
    {
        var session = VirtualAdapterFixture.NewSession("co-safety-dcf");
        using var bus = Open(session, 1);
        using var node = CanOpen.OpenNode(bus, SafetyDcf());
        node.NodeId.Should().Be(5);
        node.Safety().SrdoCount.Should().Be(2);
        node.ObjectDictionary.ContainsIndex(0x1302).Should().BeTrue();
        node.ObjectDictionary.ContainsIndex(0x1303).Should().BeFalse();
    }

    [Fact]
    public void Described_Values_Reach_The_Records_And_The_Configuration_Is_Valid()
    {
        var session = VirtualAdapterFixture.NewSession("co-safety-dcf");
        using var bus = Open(session, 1);
        using var node = CanOpen.OpenNode(bus, SafetyDcf());
        var od = node.ObjectDictionary;
        od.ReadUnsigned(0x1300, 0).Should().Be(1u);
        od.ReadUnsigned(0x1301, 1).Should().Be(1u);
        od.ReadUnsigned(0x1301, 2).Should().Be(30u);
        od.ReadUnsigned(0x1301, 5).Should().Be(0x109u);
        od.ReadUnsigned(0x1301, 6).Should().Be(0x10Au);
        od.ReadUnsigned(0x1381, 0).Should().Be(4u);
        od.ReadUnsigned(0x1381, 3).Should().Be(0x2001_0008u);
        od.ReadUnsigned(0x1302, 1).Should().Be(0u);
        od.ReadUnsigned(0x13FE, 0).Should().Be(0xA5u, "the file is the power-on state; 13FEh is applied last");
        SrdoRecords.IsConfigurationValid(od, 1).Should().BeTrue();
        node.DeviceDescription!.Findings.Where(f => f.Index is >= 0x1300 and <= 0x13FF).Should().BeEmpty(
            string.Join("\n", node.DeviceDescription.Findings));
        node.Safety().GetSrdoState(1).Direction.Should().Be(SrdoDirection.Transmit);
    }

    [Fact]
    public void A_Rejected_Value_Is_A_Finding_With_Its_Abort_Code()
    {
        var text = SafetyDcfText().Replace("ParameterValue=0x109", "ParameterValue=0x108");
        var session = VirtualAdapterFixture.NewSession("co-safety-dcf");
        using var bus = Open(session, 1);
        using var node = CanOpen.OpenNode(bus, CanOpenDeviceDescription.ParseDcf(text));
        var finding = node.DeviceDescription!.Findings.Single(f => f.Index == 0x1301 && f.Subindex == 5);
        finding.Outcome.Should().Be(DeviceDescriptionOutcome.Corrected);
        finding.AbortCode.Should().Be(SdoAbortCode.ValueRangeExceeded);
        node.ObjectDictionary.ReadUnsigned(0x1301, 5).Should().Be(0x109u, "the default for node 5 is kept");
        node.ObjectDictionary.ReadUnsigned(0x1301, 1).Should().Be(1u, "sub1 is written last and the kept ids are consistent");
    }

    [Fact]
    public void Undeclared_Srdo_Records_Do_Not_Exist_And_SrdoCount_Option_Adds_None()
    {
        var text = SafetyDcfText().Replace("3=0x1302\n", "").Replace("5=0x1382\n", "").Replace("SupportedObjects=7", "SupportedObjects=5");
        var session = VirtualAdapterFixture.NewSession("co-safety-dcf");
        using var bus = Open(session, 1);
        using var node = CanOpen.OpenNode(bus, CanOpenDeviceDescription.ParseDcf(text), new CanOpenNodeOptions { SrdoCount = 4 });
        node.Safety().SrdoCount.Should().Be(4, "the option is a floor");
        node.ObjectDictionary.ContainsIndex(0x1302).Should().BeTrue("the option created it");
        node.ObjectDictionary.ReadUnsigned(0x13FF, 0).Should().Be(4u);
    }

    [Fact]
    public void A_Record_Below_The_Highest_Declared_One_That_The_File_Omits_Is_Removed()
    {
        // 1301h/1381h are not declared, 1302h is: the node holds two SRDOs of which only the second exists.
        var text = SafetyDcfText().Replace("2=0x1301\n", "").Replace("4=0x1381\n", "").Replace("SupportedObjects=7", "SupportedObjects=5");
        var session = VirtualAdapterFixture.NewSession("co-safety-dcf");
        using var bus = Open(session, 1);
        using var node = CanOpen.OpenNode(bus, CanOpenDeviceDescription.ParseDcf(text));
        node.Safety().SrdoCount.Should().Be(2);
        node.ObjectDictionary.ContainsIndex(0x1301).Should().BeFalse();
        node.ObjectDictionary.ContainsIndex(0x1381).Should().BeFalse();
        node.ObjectDictionary.ContainsIndex(0x1302).Should().BeTrue();
        node.ObjectDictionary.ContainsIndex(0x1382).Should().BeTrue();
    }

    [Fact]
    public void Peer_Configuration_From_A_Dcf()
    {
        var configuration = PeerSafetyConfiguration.FromDeviceDescription(SafetyDcf(), 5);
        configuration.GlobalFailsafeCommandEnabled.Should().BeTrue();
        configuration.Srdos.Should().ContainKey(1);
        configuration.Srdos.Should().NotContainKey(2, "direction 0 is deleted");
        var (parameter, mapping) = configuration.Srdos[1];
        parameter.Should().Be(new SrdoCommunicationParameter(SrdoDirection.Transmit, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(20), 0x109, 0x10A));
        mapping.Entries.Select(e => (e.Index, e.Subindex, e.BitLength)).Should().Equal(((ushort)0x2000, (byte)0, (byte)16), ((ushort)0x2001, (byte)0, (byte)8));
        configuration.DeclaresAnySrdo.Should().BeTrue();
        PeerSafetyConfiguration.FromDeviceDescription(CanOpenDeviceDescription.Load(Fixture("device.dcf")), 5).DeclaresAnySrdo.Should().BeFalse();
    }
}
