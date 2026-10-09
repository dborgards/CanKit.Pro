using System;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Safety;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Safety;

/// <summary>Reading an SRDO's records out of a plain dictionary, without a node.</summary>
public class SrdoRecordsTests
{
    private static ObjectDictionary RecordFor(int n, byte direction, ushort cycle, byte srvt, uint cob1, uint cob2, params uint[] mappingSlots)
    {
        var od = new ObjectDictionary();
        var comm = SrdoRecords.CommIndex(n);
        var map = SrdoRecords.MapIndex(n);
        od.AddU8(comm, 0x00, 6); od.AddU8(comm, 0x01, direction); od.AddU16(comm, 0x02, cycle);
        od.AddU8(comm, 0x03, srvt); od.AddU8(comm, 0x04, 254); od.AddU32(comm, 0x05, cob1); od.AddU32(comm, 0x06, cob2);
        od.AddU8(map, 0x00, (byte)mappingSlots.Length);
        for (byte s = 1; s <= 16; s++) od.AddU32(map, s, s <= mappingSlots.Length ? mappingSlots[s - 1] : 0);
        od.AddU8(SrdoRecords.ConfigurationValid, 0, 0);
        od.AddU8(SrdoRecords.Checksum, 0, 1);
        od.AddU16(SrdoRecords.Checksum, (byte)n, 0);
        return od;
    }

    [Fact]
    public void Indices_And_Classification()
    {
        SrdoRecords.CommIndex(1).Should().Be((ushort)0x1301);
        SrdoRecords.CommIndex(64).Should().Be((ushort)0x1340);
        SrdoRecords.MapIndex(1).Should().Be((ushort)0x1381);
        SrdoRecords.MapIndex(64).Should().Be((ushort)0x13C0);
        SrdoRecords.SrdoNumberOf(0x1302).Should().Be(2);
        SrdoRecords.SrdoNumberOf(0x13C0).Should().Be(64);
        SrdoRecords.SrdoNumberOf(0x1300).Should().BeNull();
        SrdoRecords.SrdoNumberOf(0x13FE).Should().BeNull();
        SrdoRecords.IsSafetyObject(0x1300).Should().BeTrue();
        SrdoRecords.IsSafetyObject(0x1341).Should().BeFalse();
        SrdoRecords.IsSafetyObject(0x13FF).Should().BeTrue();
        SrdoRecords.IsStateGated(0x1300).Should().BeFalse("1300h is not a safety entry in the sense of §8.3.2.4 note 1");
        SrdoRecords.IsStateGated(0x13FE).Should().BeTrue();
        SrdoRecords.IsChecksummed(0x13FE).Should().BeFalse();
        SrdoRecords.IsChecksummed(0x13FF).Should().BeTrue();
        SrdoRecords.IsChecksummed(0x1381).Should().BeTrue();
    }

    [Fact]
    public void Reads_A_Record_Into_Typed_Values()
    {
        var od = RecordFor(1, 2, 50, 20, 0x101, 0x102, 0x20000110, 0x20000110);
        SrdoRecords.TryReadCommunication(od, 1, out var p).Should().BeTrue();
        p.Should().Be(new SrdoCommunicationParameter(SrdoDirection.Receive, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(20), 0x101, 0x102));
        var mapping = SrdoRecords.ReadMapping(od, 1);
        mapping.Should().HaveCount(1);
        mapping[0].Index.Should().Be((ushort)0x2000);
        mapping[0].Subindex.Should().Be((byte)0x01);
        mapping[0].BitLength.Should().Be((byte)16);
    }

    [Fact]
    public void Configuration_Is_Valid_Only_With_A5h_And_A_Matching_Checksum()
    {
        var od = RecordFor(1, 2, 50, 20, 0x101, 0x102, 0x20000110, 0x20000110);
        SrdoRecords.TryReadCommunication(od, 1, out var p);
        ushort crc = SrdoCrc.Compute(p, SrdoMapping.FromEntries(SrdoRecords.ReadMapping(od, 1)));
        SrdoRecords.IsConfigurationValid(od, 1).Should().BeFalse("13FEh is 0");
        od.WriteUnsigned(SrdoRecords.ConfigurationValid, 0, 0xA5);
        SrdoRecords.IsConfigurationValid(od, 1).Should().BeFalse("13FFh is 0");
        od.WriteUnsigned(SrdoRecords.Checksum, 1, crc);
        SrdoRecords.IsConfigurationValid(od, 1).Should().BeTrue();
        od.WriteUnsigned(SrdoRecords.Checksum, 1, (uint)(crc ^ 1));
        SrdoRecords.IsConfigurationValid(od, 1).Should().BeFalse("one bit of the checksum is wrong");
    }

    [Fact]
    public void A_Record_Without_A_Direction_Entry_Is_Absent()
    {
        SrdoRecords.TryReadCommunication(new ObjectDictionary(), 1, out _).Should().BeFalse();
        SrdoRecords.ReadMapping(new ObjectDictionary(), 1).Should().BeEmpty();
    }
}
