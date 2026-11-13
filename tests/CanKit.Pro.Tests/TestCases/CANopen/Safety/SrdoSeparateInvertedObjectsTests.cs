using System;
using System.Linq;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Pdo;
using CanKit.Pro.CANopen.Safety;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Safety;

/// <summary>An SRDO mapping whose even slots name objects of their own (EN 50325-5:2010 §6.4.1.4,
/// Table 8; #506): the model, the peer rule, the checksum over the stored even entries
/// (§6.4.1.5, Table 9), the expectation read off a file, and the monitor's configuration.</summary>
public class SrdoSeparateInvertedObjectsTests
{
    private static readonly SrdoCommunicationParameter Parameter = new(
        SrdoDirection.Transmit, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(20), 0x109, 0x10A);

    [Fact]
    public void A_Mapping_Holds_A_Separate_Inverted_Object_Per_Entry()
    {
        var mapping = new SrdoMapping()
            .Add(new PdoMappingEntry(0x2000, 0, 16), new PdoMappingEntry(0x2003, 0, 16))
            .Add(0x2001, 0, 8);
        mapping.Entries.Select(e => e.Index).Should().Equal((ushort)0x2000, (ushort)0x2001);
        mapping.InvertedEntries.Select(e => e.Index).Should().Equal((ushort)0x2003, (ushort)0x2001);
        mapping.HasSeparateInvertedObjects.Should().BeTrue();
        mapping.TotalBytes.Should().Be(3, "the inverted objects are the second frame, not more data");

        var repeated = new SrdoMapping().Add(0x2000, 0, 16);
        repeated.InvertedEntries.Should().Equal(repeated.Entries);
        repeated.HasSeparateInvertedObjects.Should().BeFalse();
    }

    [Fact]
    public void An_Inverted_Object_Of_Another_Width_Or_A_Dummy_Is_Refused()
    {
        var mapping = new SrdoMapping();
        Action wider = () => mapping.Add(new PdoMappingEntry(0x2000, 0, 8), new PdoMappingEntry(0x2003, 0, 16));
        wider.Should().Throw<ArgumentException>().WithMessage("*7.1.1*");
        Action dummy = () => mapping.Add(new PdoMappingEntry(0x2000, 0, 8), new PdoMappingEntry(0x0005, 0, 8));
        dummy.Should().Throw<InvalidOperationException>();
        Action plainDummy = () => mapping.Add(new PdoMappingEntry(0x0005, 0, 8), new PdoMappingEntry(0x2000, 0, 8));
        plainDummy.Should().Throw<InvalidOperationException>();
        mapping.Entries.Should().BeEmpty();
        mapping.InvertedEntries.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0x2000_0010u, 0x2000_0010u, true)]   // the same object: also the device rule
    [InlineData(0x2000_0010u, 0x2003_0010u, true)]   // an object of its own, as wide
    [InlineData(0x2000_0010u, 0x2003_0008u, false)]  // another width
    [InlineData(0x2000_0010u, 0u, false)]            // not set
    [InlineData(0x2000_0010u, 0x0006_0010u, false)]  // a dummy
    [InlineData(0x2000_0010u, 0x0006_0110u, true)]   // 0006h:01 is no dummy: only sub-index 0 is
    [InlineData(0x0000_0008u, 0x0000_0008u, true)]   // index 0 sub 0: below the dummy range
    [InlineData(0x0008_0010u, 0x0008_0010u, true)]   // index 8 sub 0: above the dummy range
    public void The_Peer_Rule_Takes_An_Object_Of_Its_Own_As_Wide_As_The_Plain_One(uint plain, uint inverted, bool accepted)
    {
        SrdoRecords.IsPeerInvertedSlotOf(plain, inverted).Should().Be(accepted);
        SrdoRecords.IsInvertedSlotOf(plain, inverted).Should().Be(inverted != 0 && inverted == plain, "the device rule is unchanged");
    }

    /// <summary>EN 50325-5:2010 §6.4.1.5, Table 9: the stored even entry is part of the checksum,
    /// so a separate inverted object gives another value than the repeated entry. The byte image
    /// and both values are computed by hand (CRC-16, polynomial 1021h, seed 0000h).</summary>
    [Fact]
    public void The_Checksum_Covers_The_Stored_Even_Entries()
    {
        var separate = new SrdoMapping().Add(new PdoMappingEntry(0x6000, 1, 8), new PdoMappingEntry(0x6001, 1, 8));
        var repeated = new SrdoMapping().Add(new PdoMappingEntry(0x6000, 1, 8));
        SrdoCrc.CanonicalBytes(Parameter, separate).Should().Equal(
            0x01, 0x1E, 0x00, 0x14,
            0x09, 0x01, 0x00, 0x00, 0x0A, 0x01, 0x00, 0x00,
            0x02,
            0x01, 0x08, 0x01, 0x00, 0x60,   // sub 1: 6000h:01, 8 bits
            0x02, 0x08, 0x01, 0x01, 0x60);  // sub 2: 6001h:01, 8 bits
        SrdoCrc.Compute(Parameter, separate).Should().Be(0xBF48);
        SrdoCrc.Compute(Parameter, repeated).Should().Be(0x8C79);
    }

    [Fact]
    public void The_Record_Checksum_Equals_The_Configuration_Checksum_With_Separate_Inverted_Objects()
    {
        var od = new ObjectDictionary();
        od.AddU8(0x1301, 1, 1); od.AddU16(0x1301, 2, 30); od.AddU8(0x1301, 3, 20);
        od.AddU32(0x1301, 5, 0x109); od.AddU32(0x1301, 6, 0x10A);
        od.AddU8(0x1381, 0, 2); od.AddU32(0x1381, 1, 0x6000_0108); od.AddU32(0x1381, 2, 0x6001_0108);
        var mapping = new SrdoMapping().Add(new PdoMappingEntry(0x6000, 1, 8), new PdoMappingEntry(0x6001, 1, 8));
        SrdoRecords.RecordChecksum(od, 1).Should().Be(SrdoCrc.Compute(Parameter, mapping));
        SrdoRecords.ReadMappingPairs(od, 1).Should().Equal((new PdoMappingEntry(0x6000, 1, 8), new PdoMappingEntry(0x6001, 1, 8)));
    }

    [Fact]
    public void Read_Mapping_Pairs_Is_Empty_When_An_Even_Slot_Is_Empty_Or_Malformed()
    {
        var od = new ObjectDictionary();
        od.AddU8(0x1381, 0, 2); od.AddU32(0x1381, 1, 0x6000_0108); od.AddU32(0x1381, 2, 0);
        SrdoRecords.ReadMappingPairs(od, 1).Should().BeEmpty();
        od.WriteUnsigned(0x1381, 2, 0x6001_0100); // 0 bits: no mapping entry
        SrdoRecords.ReadMappingPairs(od, 1).Should().BeEmpty();
        od.WriteUnsigned(0x1381, 0, 0);
        SrdoRecords.ReadMappingPairs(od, 1).Should().BeEmpty();

        var withoutEvenSlot = new ObjectDictionary();
        withoutEvenSlot.AddU8(0x1381, 0, 2); withoutEvenSlot.AddU32(0x1381, 1, 0x6000_0108);
        SrdoRecords.ReadMappingPairs(withoutEvenSlot, 1).Should().BeEmpty();
    }

    /// <summary>The tool role reads a peer's file with the peer rule: the separate objects are the
    /// expectation, and its checksum is over them.</summary>
    [Fact]
    public void A_Configuration_From_A_File_Keeps_The_Separate_Inverted_Objects()
    {
        var configuration = PeerSafetyConfiguration.FromDeviceDescription(SeparateInvertedObjectsFixture.File(), 5);
        var (parameter, mapping) = configuration.Srdos[1];
        mapping.Entries.Should().Equal(new PdoMappingEntry(0x2000, 0, 16), new PdoMappingEntry(0x2001, 0, 8));
        mapping.InvertedEntries.Should().Equal(new PdoMappingEntry(0x2003, 0, 16), new PdoMappingEntry(0x2004, 0, 8));
        SrdoCrc.Compute(parameter, mapping).Should().NotBe(SrdoCrc.Compute(parameter, SrdoMapping.FromEntries(mapping.ToArray())));
    }

    [Fact]
    public void A_File_Whose_Inverted_Object_Has_Another_Width_Leaves_The_Srdo_Deleted()
    {
        var text = SeparateInvertedObjectsFixture.Patch(SeparateInvertedObjectsFixture.Text(),
            "[1381sub2]", "ParameterValue=0x20030010", "ParameterValue=0x20040008");
        var configuration = PeerSafetyConfiguration.FromDeviceDescription(CanOpenDeviceDescription.ParseDcf(text), 5);
        configuration.Srdos.Should().NotContainKey(1, "the mapping record could not be applied, so the SRDO stays deleted");
    }

    [Fact]
    public void A_Configuration_Keeps_A_Copy_Of_The_Pairs()
    {
        var mapping = new SrdoMapping().Add(new PdoMappingEntry(0x2000, 0, 16), new PdoMappingEntry(0x2003, 0, 16));
        var configuration = new PeerSafetyConfiguration().Add(1, Parameter, mapping);
        mapping.Add(0x2001, 0, 8);
        configuration.Srdos[1].Mapping.InvertedEntries.Should().Equal(new PdoMappingEntry(0x2003, 0, 16));
    }

    [Fact]
    public void The_Monitor_Configuration_Keeps_The_Inverted_Objects_And_Decodes_The_Plain_Ones()
    {
        var mapping = new SrdoMapping().Add(new PdoMappingEntry(0x2000, 1, 16), new PdoMappingEntry(0x2003, 1, 16));
        var config = ForeignSrdoConfiguration.Create(5, 1, 0x101, 0x102, mapping,
            TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(100), "DCF rev 3");
        config.Mapping.Should().Equal(new PdoMappingEntry(0x2000, 1, 16));
        config.InvertedMapping.Should().Equal(new PdoMappingEntry(0x2003, 1, 16));
        Action missing = () => ForeignSrdoConfiguration.Create(5, 1, 0x101, 0x102, null!,
            TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(100), "DCF rev 3");
        missing.Should().Throw<ArgumentNullException>();

        var plainOnly = new ForeignSrdoConfiguration(5, 1, 0x101, 0x102, new[] { new PdoMappingEntry(0x2000, 1, 16) },
            TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(100), "DCF rev 3");
        plainOnly.InvertedMapping.Should().Equal(plainOnly.Mapping);
    }
}
