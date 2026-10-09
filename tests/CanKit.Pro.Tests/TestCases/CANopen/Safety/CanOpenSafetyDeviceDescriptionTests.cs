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
/// every deviation is a finding (FR-CO-025..028 applied to CiA 304: FR-CO-043).</summary>
public class CanOpenSafetyDeviceDescriptionTests : IClassFixture<VirtualAdapterFixture>
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "TestCases", "CANopen", "Fixtures", name);

    private static ICanBus Open(string session, int channel) => CanBus.Open(
        $"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(VirtualAdapterFixture.Bitrate));

    private static string SafetyDcfText() => File.ReadAllText(Fixture("safety.dcf"));

    /// <summary>The fixture with its second SRDO renumbered to 3 and created (10Bh/10Ch, empty
    /// mapping), and 13FFh declared for three: SRDO 2 is the gap the file does not declare.</summary>
    internal static string GappedSafetyDcfText()
    {
        var text = SafetyDcfText().Replace("[1302", "[1303").Replace("[1382", "[1383")
            .Replace("=0x1302\n", "=0x1303\n").Replace("=0x1382\n", "=0x1383\n");
        text = Patch(text, "[1303sub1]", "DefaultValue=0", "DefaultValue=1");
        text = Patch(text, "[1303sub5]", "DefaultValue=0", "DefaultValue=0x10B");
        text = Patch(text, "[1303sub6]", "DefaultValue=0", "DefaultValue=0x10C");
        text = Patch(text, "[13FF]", "SubNumber=3", "SubNumber=4");
        text = Patch(text, "[13FFsub0]", "DefaultValue=2", "DefaultValue=3");
        text = text.Replace("[ManufacturerObjects]",
            "[13FFsub3]\nParameterName=Signature SRDO 3\nObjectType=0x7\nDataType=0x0006\nAccessType=rw\nDefaultValue=0\nPDOMapping=0\n\n[ManufacturerObjects]");
        text.Should().NotContain("[1302").And.NotContain("[1382");
        return text;
    }

    /// <summary>The fixture with the optional-object list replaced (the list is read by position, so
    /// leaving a gap would hide the entries behind it).</summary>
    private static string WithOptional(string text, params ushort[] indices)
    {
        int start = text.IndexOf("[OptionalObjects]", StringComparison.Ordinal);
        int end = text.IndexOf("[1300]", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1);
        end.Should().BeGreaterThan(start);
        var list = $"[OptionalObjects]\nSupportedObjects={indices.Length}\n"
            + string.Concat(indices.Select((index, i) => $"{i + 1}=0x{index:X4}\n")) + "\n";
        return text[..start] + list + text[end..];
    }

    /// <summary>Replaces the first <paramref name="old"/> after <paramref name="section"/>.</summary>
    private static string Patch(string text, string section, string old, string replacement)
    {
        int at = text.IndexOf(section, StringComparison.Ordinal);
        at.Should().BeGreaterThan(-1, section);
        int hit = text.IndexOf(old, at, StringComparison.Ordinal);
        hit.Should().BeGreaterThan(-1, old);
        return text[..hit] + replacement + text[(hit + old.Length)..];
    }

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
        // The engine takes the loaded records on the actor, in the turn the constructor posted; a
        // State read is a round trip queued behind it, so the snapshot below is the loaded one.
        _ = node.State;
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

    /// <summary>The file declares SRDO 1 only; the SrdoCount option asks for 4. The option is a
    /// floor: the count is 4, 13FFh:00 says so, and the records above the file's exist.</summary>
    [Fact]
    public void The_SrdoCount_Option_Is_A_Floor_And_Its_Records_Exist_Above_The_Files()
    {
        var text = SafetyDcfText().Replace("3=0x1302\n", "").Replace("5=0x1382\n", "").Replace("SupportedObjects=7", "SupportedObjects=5");
        var session = VirtualAdapterFixture.NewSession("co-safety-dcf");
        using var bus = Open(session, 1);
        using var node = CanOpen.OpenNode(bus, CanOpenDeviceDescription.ParseDcf(text), new CanOpenNodeOptions { SrdoCount = 4 });
        node.Safety().SrdoCount.Should().Be(4, "the option is a floor");
        node.ObjectDictionary.ContainsIndex(0x1302).Should().BeTrue("the option created it");
        node.ObjectDictionary.ReadUnsigned(0x13FF, 0).Should().Be(4u);
    }

    /// <summary>13FFh:00 is the number of SRDOs (§8.4.2.2), so records 1..n exist: one below the
    /// highest declared that the file leaves out is provided at its defaults, the SRDO deleted,
    /// and reported. A master configuring or verifying the node reaches every record up to the count.</summary>
    [Fact]
    public void A_Record_Below_The_Highest_Declared_One_That_The_File_Omits_Exists_Deleted()
    {
        var session = VirtualAdapterFixture.NewSession("co-safety-dcf");
        using var bus = Open(session, 1);
        using var node = CanOpen.OpenNode(bus, CanOpenDeviceDescription.ParseDcf(GappedSafetyDcfText()));
        var od = node.ObjectDictionary;
        node.Safety().SrdoCount.Should().Be(3);
        od.ReadUnsigned(0x13FF, 0).Should().Be(3u);
        od.ContainsIndex(0x1302).Should().BeTrue();
        od.ContainsIndex(0x1382).Should().BeTrue();
        od.ReadUnsigned(0x1302, 1).Should().Be(0u, "the undeclared SRDO is deleted");
        od.ReadUnsigned(0x1302, 2).Should().Be(25u, "at the §8.4.2.2 defaults");
        od.ReadUnsigned(0x1303, 1).Should().Be(1u, "SRDO 3 is created as declared");
        foreach (ushort record in new ushort[] { 0x1302, 0x1382 })
            node.DeviceDescription!.Findings.Should().ContainSingle(f => f.Index == record && f.Subindex == 0 && f.Outcome == DeviceDescriptionOutcome.SuppliedDefault);
        node.DeviceDescription!.Findings.Should().NotContain(f => f.Index == 0x1301 || f.Index == 0x1303, "declared records are not reported");
    }

    /// <summary>A mapping record without its communication record: the communication record
    /// exists all the same (13FFh:00 = 1), deleted and reported, and the mapping is applied as
    /// described; no SRDO is created, because the file gives it no direction.</summary>
    [Fact]
    public void A_Mapping_Record_Without_Its_Communication_Record_Leaves_The_Srdo_Deleted()
    {
        var text = WithOptional(SafetyDcfText(), 0x1300, 0x1381, 0x13FE, 0x13FF);
        var session = VirtualAdapterFixture.NewSession("co-safety-dcf");
        using var bus = Open(session, 1);
        using var node = CanOpen.OpenNode(bus, CanOpenDeviceDescription.ParseDcf(text));
        node.Safety().SrdoCount.Should().Be(1);
        node.ObjectDictionary.ReadUnsigned(0x1301, 1).Should().Be(0u);
        node.ObjectDictionary.ReadUnsigned(0x1381, 0).Should().Be(4u, "the mapping is applied as described");
        node.DeviceDescription!.Findings.Should().ContainSingle(f => f.Index == 0x1301 && f.Subindex == 0 && f.Outcome == DeviceDescriptionOutcome.SuppliedDefault);
        node.DeviceDescription.Findings.Should().NotContain(f => f.Index == 0x1381);
    }

    [Fact]
    public void A_Rejected_Mapping_Entry_Keeps_The_Mapping_Disabled_And_The_Srdo_Deleted()
    {
        // 1017h is in the communication-profile area: no SRDO maps it.
        var text = Patch(SafetyDcfText(), "[1381sub1]", "ParameterValue=0x20000010", "ParameterValue=0x10170010");
        var session = VirtualAdapterFixture.NewSession("co-safety-dcf");
        using var bus = Open(session, 1);
        using var node = CanOpen.OpenNode(bus, CanOpenDeviceDescription.ParseDcf(text));
        var od = node.ObjectDictionary;
        var entry = node.DeviceDescription!.Findings.Single(f => f.Index == 0x1381 && f.Subindex == 1);
        entry.Outcome.Should().Be(DeviceDescriptionOutcome.Corrected);
        entry.AbortCode.Should().NotBeNull();
        od.ReadUnsigned(0x1381, 0).Should().Be(0u, "the mapping stays disabled");
        od.ReadUnsigned(0x1301, 1).Should().Be(0u, "an SRDO is not created over a mapping that failed");
        node.DeviceDescription.Findings.Should().Contain(f => f.Index == 0x1301 && f.Subindex == 1 && f.Outcome == DeviceDescriptionOutcome.Corrected);
        // The engine takes the loaded records on the actor, in the turn the constructor posted; a
        // State read is a round trip queued behind it, so the snapshot below is the loaded one.
        _ = node.State;
        node.Safety().GetSrdoState(1).Direction.Should().Be(SrdoDirection.None);
    }

    [Fact]
    public void An_Odd_Mapping_Count_Is_A_Finding_And_The_Srdo_Stays_Deleted()
    {
        var text = Patch(SafetyDcfText(), "[1381sub0]", "ParameterValue=4", "ParameterValue=3");
        var session = VirtualAdapterFixture.NewSession("co-safety-dcf");
        using var bus = Open(session, 1);
        using var node = CanOpen.OpenNode(bus, CanOpenDeviceDescription.ParseDcf(text));
        var od = node.ObjectDictionary;
        var count = node.DeviceDescription!.Findings.Single(f => f.Index == 0x1381 && f.Subindex == 0);
        count.Outcome.Should().Be(DeviceDescriptionOutcome.Corrected);
        count.AbortCode.Should().Be(SdoAbortCode.ValueRangeExceeded);
        od.ReadUnsigned(0x1381, 0).Should().Be(0u);
        od.ReadUnsigned(0x1301, 1).Should().Be(0u);
        node.DeviceDescription.Findings.Should().Contain(f => f.Index == 0x1301 && f.Subindex == 1);
    }

    /// <summary>A mapping count above 16 is a finding, as for a PDO, and is not cut to a byte:
    /// 0x100 would become an empty mapping and 0x104 the file's four sub-indices.</summary>
    [Theory]
    [InlineData("0x100")]
    [InlineData("0x104")]
    public void A_Mapping_Count_Above_Sixteen_Is_A_Finding_And_The_Srdo_Stays_Deleted(string count)
    {
        var text = Patch(SafetyDcfText(), "[1381sub0]", "ParameterValue=4", "ParameterValue=" + count);
        var session = VirtualAdapterFixture.NewSession("co-safety-dcf");
        using var bus = Open(session, 1);
        using var node = CanOpen.OpenNode(bus, CanOpenDeviceDescription.ParseDcf(text));
        var od = node.ObjectDictionary;
        var finding = node.DeviceDescription!.Findings.Single(f => f.Index == 0x1381 && f.Subindex == 0);
        finding.Outcome.Should().Be(DeviceDescriptionOutcome.Corrected);
        finding.Reason.Should().Contain("16");
        od.ReadUnsigned(0x1381, 0).Should().Be(0u, "the mapping stays disabled");
        od.ReadUnsigned(0x1301, 1).Should().Be(0u, "an SRDO is not created over a mapping that failed");
        node.DeviceDescription.Findings.Should().Contain(f => f.Index == 0x1301 && f.Subindex == 1 && f.Outcome == DeviceDescriptionOutcome.Corrected);
    }

    [Fact]
    public void A_Communication_Record_Without_A_Mapping_Record_Is_Not_Created_As_An_Srdo()
    {
        var text = WithOptional(SafetyDcfText(), 0x1300, 0x1301, 0x13FE, 0x13FF);
        var session = VirtualAdapterFixture.NewSession("co-safety-dcf");
        using var bus = Open(session, 1);
        using var node = CanOpen.OpenNode(bus, CanOpenDeviceDescription.ParseDcf(text));
        node.ObjectDictionary.ReadUnsigned(0x1301, 1).Should().Be(0u);
        node.DeviceDescription!.Findings.Should().Contain(f => f.Index == 0x1301 && f.Subindex == 1 && f.Outcome == DeviceDescriptionOutcome.Corrected);
    }

    [Fact]
    public void An_Empty_Declared_Mapping_Still_Creates_The_Srdo()
    {
        var text = Patch(SafetyDcfText(), "[1381sub0]", "ParameterValue=4", "ParameterValue=0");
        var session = VirtualAdapterFixture.NewSession("co-safety-dcf");
        using var bus = Open(session, 1);
        using var node = CanOpen.OpenNode(bus, CanOpenDeviceDescription.ParseDcf(text));
        node.ObjectDictionary.ReadUnsigned(0x1301, 1).Should().Be(1u, "an empty SRDO is legal (§8.1.3.1)");
    }

    [Fact]
    public void A_Wrong_Checksum_In_The_File_Loads_Without_A_Finding_But_The_Configuration_Is_Not_Valid()
    {
        // The fixture as shipped: 13FFh:01 is 0x0000, not the checksum of SRDO 1, and 13FEh says A5h.
        var session = VirtualAdapterFixture.NewSession("co-safety-dcf");
        using var bus = Open(session, 1);
        using var node = CanOpen.OpenNode(bus, CanOpenDeviceDescription.ParseDcf(SafetyDcfText()));
        var od = node.ObjectDictionary;
        od.ReadUnsigned(0x13FE, 0).Should().Be(0xA5u);
        node.DeviceDescription!.Findings.Where(f => f.Index is >= 0x1300 and <= 0x13FF).Should().BeEmpty();
        SrdoRecords.IsConfigurationValid(od, 1).Should().BeFalse("the stored checksum is not the one of the records");
    }

    [Fact]
    public void A_Node_Id_Formula_In_A_Default_Value_Is_Evaluated()
    {
        // Distinguishable from the node's own pre-defined ids (0x109/0x10A for node 5).
        var text = SafetyDcfText();
        text = Patch(text, "[1301sub5]", "DefaultValue=$NODEID+0x104\nParameterValue=0x109\n", "DefaultValue=$NODEID+0x124\n");
        text = Patch(text, "[1301sub6]", "DefaultValue=$NODEID+0x105\nParameterValue=0x10A\n", "DefaultValue=$NODEID+0x125\n");
        var session = VirtualAdapterFixture.NewSession("co-safety-dcf");
        using var bus = Open(session, 1);
        using var node = CanOpen.OpenNode(bus, CanOpenDeviceDescription.ParseDcf(text));
        node.ObjectDictionary.ReadUnsigned(0x1301, 5).Should().Be(0x129u);
        node.ObjectDictionary.ReadUnsigned(0x1301, 6).Should().Be(0x12Au);
        node.DeviceDescription!.Findings.Where(f => f.Index == 0x1301).Should().BeEmpty(string.Join("\n", node.DeviceDescription.Findings));
    }

    [Fact]
    public void Peer_Configuration_Leaves_Out_A_Record_With_An_Odd_Mapping_Count()
    {
        var text = Patch(SafetyDcfText(), "[1381sub0]", "ParameterValue=4", "ParameterValue=3");
        PeerSafetyConfiguration.FromDeviceDescription(CanOpenDeviceDescription.ParseDcf(text), 5).Srdos.Should().BeEmpty();
    }

    [Fact]
    public void Peer_Configuration_Add_Refuses_A_Direction_None_And_A_Number_Out_Of_Range()
    {
        var none = new SrdoCommunicationParameter(SrdoDirection.None, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(20), 0x109, 0x10A);
        var transmit = new SrdoCommunicationParameter(SrdoDirection.Transmit, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(20), 0x109, 0x10A);
        var configuration = new PeerSafetyConfiguration();
        configuration.Invoking(c => c.Add(1, none, new SrdoMapping())).Should().Throw<ArgumentException>();
        configuration.Invoking(c => c.Add(0, transmit, new SrdoMapping())).Should().Throw<ArgumentOutOfRangeException>();
        configuration.Invoking(c => c.Add(65, transmit, new SrdoMapping())).Should().Throw<ArgumentOutOfRangeException>();
        configuration.DeclaresAnySrdo.Should().BeFalse();
    }

    /// <summary>The fixture without sub-index <paramref name="sub"/> of <paramref name="index"/>:
    /// its section goes and the record's SubNumber drops by one.</summary>
    private static string WithoutSubIndex(string text, ushort index, byte sub)
    {
        var section = $"[{index:X4}sub{sub:X}]\n";
        int start = text.IndexOf(section, StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, section);
        int end = text.IndexOf("\n[", start + section.Length, StringComparison.Ordinal) + 1;
        text = text[..start] + text[end..];
        int record = text.IndexOf($"[{index:X4}]\n", StringComparison.Ordinal);
        int at = text.IndexOf("SubNumber=", record, StringComparison.Ordinal);
        int lineEnd = text.IndexOf('\n', at);
        int count = int.Parse(text[(at + "SubNumber=".Length)..lineEnd], System.Globalization.CultureInfo.InvariantCulture);
        return text[..at] + $"SubNumber={count - 1}" + text[lineEnd..];
    }

    /// <summary>The variants of <see cref="Peer_Configuration_Agrees_With_The_Device_Loading_The_Same_File"/>.</summary>
    private static string Variant(string name) => name switch
    {
        "as shipped" => SafetyDcfText(),
        "no mapping record" => WithOptional(SafetyDcfText(), 0x1300, 0x1301, 0x1302, 0x1382, 0x13FE, 0x13FF),
        "no refresh time" => WithoutSubIndex(SafetyDcfText(), 0x1301, 2),
        "no COB-ID 1" => WithoutSubIndex(SafetyDcfText(), 0x1301, 5),
        "no COB-ID 2" => WithoutSubIndex(SafetyDcfText(), 0x1301, 6),
        "no COB-IDs" => WithoutSubIndex(WithoutSubIndex(SafetyDcfText(), 0x1301, 5), 0x1301, 6),
        "inverted slot differs" => Patch(SafetyDcfText(), "[1381sub2]", "ParameterValue=0x20000010", "ParameterValue=0x20010008"),
        "inverted slot missing" => WithoutSubIndex(SafetyDcfText(), 0x1381, 2),
        "mapping count unreadable" => Patch(SafetyDcfText(), "[1381sub0]", "ParameterValue=4", "ParameterValue=four"),
        "COB-ID 1 unreadable" => Patch(SafetyDcfText(), "[1301sub5]", "ParameterValue=0x109", "ParameterValue=not-a-number"),
        // SRDO 2 created on 10Bh/10Ch: the pre-defined pair of SRDO 1 at node-id 6.
        "SRDO 2 not declared, SRDO 3 is" => GappedSafetyDcfText(),
        "SRDO 2 on the ids SRDO 1 defaults to" => Patch(Patch(Patch(WithoutSubIndex(WithoutSubIndex(SafetyDcfText(), 0x1301, 5), 0x1301, 6),
            "[1302sub1]", "DefaultValue=0", "DefaultValue=1"), "[1302sub5]", "DefaultValue=0", "DefaultValue=0x10B"), "[1302sub6]", "DefaultValue=0", "DefaultValue=0x10C"),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    /// <summary>Step D compares a slave with the expectation built from its own DCF, so the
    /// expectation must be what a device loading that file holds: every SRDO the device creates
    /// is expected with the records it holds, and one it leaves deleted is not expected. Each row
    /// also pins the outcome (SRDO 1 created or not and its COB-IDs, SRDO 2 created or not), so
    /// that a rule broken on both sides at once still fails here. Node-id 5 has the fixture's ids
    /// 109h/10Ah as its pre-defined pair (§8.3.3), node-id 6 has 10Bh/10Ch, node-id 70 none.</summary>
    [Theory]
    [InlineData("as shipped", 5, true, 0x109, 0x10A)]
    [InlineData("as shipped", 6, true, 0x109, 0x10A)]
    [InlineData("no mapping record", 5, false, 0, 0)]
    [InlineData("no refresh time", 5, true, 0x109, 0x10A)]
    [InlineData("no COB-ID 1", 5, true, 0x109, 0x10A)]
    [InlineData("no COB-ID 2", 5, true, 0x109, 0x10A)]
    [InlineData("no COB-IDs", 5, true, 0x109, 0x10A)]
    [InlineData("no COB-ID 1", 6, true, 0x10B, 0x10C)]                       // the declared 10Ah is refused against 10Bh + 1
    [InlineData("no COB-ID 2", 6, false, 0, 0)]                              // 109h with the default 10Ch is no pair
    [InlineData("no COB-IDs", 6, true, 0x10B, 0x10C)]
    [InlineData("no COB-ID 1", 70, false, 0, 0)]
    [InlineData("no COB-ID 2", 70, false, 0, 0)]
    [InlineData("COB-ID 1 unreadable", 5, true, 0x109, 0x10A)]              // the default is kept
    [InlineData("COB-ID 1 unreadable", 6, true, 0x10B, 0x10C)]
    [InlineData("SRDO 2 on the ids SRDO 1 defaults to", 6, true, 0x10B, 0x10C, false)] // SRDO 2 is refused at its creation
    [InlineData("SRDO 2 not declared, SRDO 3 is", 5, true, 0x109, 0x10A)]
    [InlineData("inverted slot differs", 5, false, 0, 0)]
    [InlineData("inverted slot missing", 5, false, 0, 0)]
    [InlineData("mapping count unreadable", 5, false, 0, 0)]
    public void Peer_Configuration_Agrees_With_The_Device_Loading_The_Same_File(string variant, byte nodeId, bool created, int cobId1, int cobId2,
        bool srdo2Created = false)
    {
        var dcf = CanOpenDeviceDescription.ParseDcf(Variant(variant));
        var expected = PeerSafetyConfiguration.FromDeviceDescription(dcf, nodeId);
        var session = VirtualAdapterFixture.NewSession("co-safety-dcf");
        using var bus = Open(session, 1);
        using var device = CanOpen.OpenNode(bus, nodeId, dcf);
        var od = device.ObjectDictionary;
        var findings = string.Join("\n", device.DeviceDescription!.Findings);
        (od.ReadUnsigned(0x1301, 1) != 0).Should().Be(created, findings);
        if (created)
        {
            od.ReadUnsigned(0x1301, 5).Should().Be((uint)cobId1, findings);
            od.ReadUnsigned(0x1301, 6).Should().Be((uint)cobId2, findings);
        }
        (od.ReadUnsigned(0x1302, 1) != 0).Should().Be(srdo2Created, findings);
        for (int n = 1; n <= device.Safety().SrdoCount; n++)
        {
            bool exists = od.ReadUnsigned(SrdoRecords.CommIndex(n), 1) != 0;
            expected.Srdos.ContainsKey(n).Should().Be(exists, $"the expectation has SRDO {n} exactly when the device created it");
            if (!exists) continue;
            SrdoRecords.TryReadCommunication(od, n, out var held).Should().BeTrue();
            var (parameter, mapping) = expected.Srdos[n];
            parameter.Should().Be(held);
            SrdoCrc.Compute(parameter, mapping).Should().Be(SrdoCrc.Compute(held, SrdoMapping.FromEntries(SrdoRecords.ReadMapping(od, n))),
                "the checksum step D expects is the one of the records the device holds");
        }
        expected.Srdos.Keys.Should().OnlyContain(n => n <= device.Safety().SrdoCount);
    }

    /// <summary>The configuration keeps the mapping it was given: a later change to the
    /// caller's instance does not change what is written or verified.</summary>
    [Fact]
    public void Peer_Configuration_Add_Keeps_A_Copy_Of_The_Mapping()
    {
        var transmit = new SrdoCommunicationParameter(SrdoDirection.Transmit, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(20), 0x109, 0x10A);
        var mapping = new SrdoMapping().Add(0x2001, 0x00, 8);
        var configuration = new PeerSafetyConfiguration().Add(1, transmit, mapping);
        mapping.Add(0x2000, 0x00, 16);
        configuration.Srdos[1].Mapping.Entries.Select(e => (e.Index, e.Subindex, e.BitLength)).Should().Equal(((ushort)0x2001, (byte)0, (byte)8));
        configuration.Srdos[1].Mapping.Should().NotBeSameAs(mapping);
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
