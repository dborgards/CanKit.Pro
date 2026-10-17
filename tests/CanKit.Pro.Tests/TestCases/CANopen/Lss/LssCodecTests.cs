using System;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Lss;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Lss;

/// <summary>
/// FR-CO-047: the LSS frame layout and the value types around it, with no bus.
/// </summary>
public class LssCodecTests
{
    [Fact]
    public void CobIds_Are_The_Lss_Pair_And_Are_Restricted()
    {
        CanOpenCobId.LssSlaveMessage.Should().Be(0x7E4);
        CanOpenCobId.LssMasterMessage.Should().Be(0x7E5);
        CanOpenCobId.UnconfiguredNodeId.Should().Be(0xFF);
        CanOpenCobId.IsRestricted(CanOpenCobId.LssSlaveMessage).Should().BeTrue();
        CanOpenCobId.IsRestricted(CanOpenCobId.LssMasterMessage).Should().BeTrue();
        CanOpenCobId.IsActiveNodeId(1).Should().BeTrue();
        CanOpenCobId.IsActiveNodeId(127).Should().BeTrue();
        CanOpenCobId.IsActiveNodeId(0).Should().BeFalse();
        CanOpenCobId.IsActiveNodeId(0xFF).Should().BeFalse();
        CanOpenCobId.IsLayerSettingNodeId(0xFF).Should().BeTrue();
        CanOpenCobId.IsLayerSettingNodeId(0).Should().BeFalse();
        var act = () => CanOpenCobId.ValidateNodeId(CanOpenCobId.UnconfiguredNodeId);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Frames_Lay_Out_Every_Command()
    {
        LssFrames.SwitchGlobal(LssState.Configuration).Should().Equal(0x04, 0x01, 0, 0, 0, 0, 0, 0);
        LssFrames.ConfigureNodeId(0xFF).Should().Equal(0x11, 0xFF, 0, 0, 0, 0, 0, 0);
        LssFrames.ConfigureBitTiming(0x80, 0x02).Should().Equal(0x13, 0x80, 0x02, 0, 0, 0, 0, 0);
        LssFrames.ActivateBitTiming(0x1234).Should().Equal(0x15, 0x34, 0x12, 0, 0, 0, 0, 0);
        LssFrames.ReadSwitchDelay(LssFrames.ActivateBitTiming(0x1234)).Should().Be(0x1234);
        LssFrames.Confirmed(LssCommand.StoreConfiguration, LssCommand.ManufacturerSpecific, 0x2A)
            .Should().Equal(0x17, 0xFF, 0x2A, 0, 0, 0, 0, 0);
        LssFrames.InquireNodeId(0x0B).Should().Equal(0x5E, 0x0B, 0, 0, 0, 0, 0, 0);

        var word = LssFrames.WithU32(LssCommand.InquireVendorId, 0xA1B2C3D4);
        word.Should().Equal(0x5A, 0xD4, 0xC3, 0xB2, 0xA1, 0, 0, 0);
        LssFrames.ReadU32(word).Should().Be(0xA1B2C3D4);

        LssFrames.Fastscan(0x01000000, 0x80, 1, 2)
            .Should().Equal(0x51, 0x00, 0x00, 0x00, 0x01, 0x80, 0x01, 0x02);
    }

    [Fact]
    public void Manufacturer_Frame_Rejects_A_Standard_Specifier_And_A_Long_Payload()
    {
        var act = () => LssFrames.Manufacturer(0x11, new byte[] { 1 });
        act.Should().Throw<ArgumentOutOfRangeException>();
        var tooLong = () => LssFrames.Manufacturer(0x80, new byte[8]);
        tooLong.Should().Throw<ArgumentException>();
        LssFrames.Manufacturer(0x80, new byte[] { 0x11, 0x22 }).Should().Equal(0x80, 0x11, 0x22, 0, 0, 0, 0, 0);
        LssCommand.IsManufacturerSpecific(127).Should().BeFalse();
        LssCommand.IsManufacturerSpecific(128).Should().BeTrue();
        LssCommand.IsManufacturerSpecific(255).Should().BeTrue();
    }

    [Fact]
    public void Address_And_Range_Compare_By_Value()
    {
        var address = new LssAddress(1, 2, 3, 4);
        address[0].Should().Be(1);
        address[3].Should().Be(4);
        var act = () => address[4];
        act.Should().Throw<ArgumentOutOfRangeException>();
        (address == new LssAddress(1, 2, 3, 4)).Should().BeTrue();
        (address != new LssAddress(1, 2, 3, 5)).Should().BeTrue();
        address.Equals((object)new LssAddress(1, 2, 3, 4)).Should().BeTrue();
        object foreign = new object();
        address.Equals(foreign).Should().BeFalse();
        address.GetHashCode().Should().Be(new LssAddress(1, 2, 3, 4).GetHashCode());
        address.ToString().Should().Contain("00000001");

        var range = new LssIdentityRange(address);
        range.Contains(address).Should().BeTrue();
        range.Contains(new LssAddress(1, 2, 3, 5)).Should().BeFalse();
        var wide = new LssIdentityRange(1, 2, 0, 10, 4, 4);
        wide.Contains(address).Should().BeTrue();
        (wide == new LssIdentityRange(1, 2, 0, 10, 4, 4)).Should().BeTrue();
        (wide != range).Should().BeTrue();
        wide.Equals((object)range).Should().BeFalse();
        wide.Equals((object?)null).Should().BeFalse();
        wide.GetHashCode().Should().Be(new LssIdentityRange(1, 2, 0, 10, 4, 4).GetHashCode());
        new LssIdentityRange(9, 2, 0, 10, 4, 4).Contains(address).Should().BeFalse();
        new LssIdentityRange(1, 9, 0, 10, 4, 4).Contains(address).Should().BeFalse();
        new LssIdentityRange(1, 2, 4, 10, 4, 4).Contains(address).Should().BeFalse();
        new LssIdentityRange(1, 2, 0, 2, 4, 4).Contains(address).Should().BeFalse();
        new LssIdentityRange(1, 2, 0, 10, 5, 6).Contains(address).Should().BeFalse();
        address.Equals(new LssAddress(9, 2, 3, 4)).Should().BeFalse();
        address.Equals(new LssAddress(1, 9, 3, 4)).Should().BeFalse();
        address.Equals(new LssAddress(1, 2, 9, 4)).Should().BeFalse();
        var inverted = () => new LssIdentityRange(1, 2, 5, 4, 0, 0);
        inverted.Should().Throw<ArgumentException>();
        var invertedSerial = () => new LssIdentityRange(1, 2, 0, 0, 9, 1);
        invertedSerial.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ServiceResult_Reports_Success_And_A_Manufacturer_Code()
    {
        LssServiceResult.Success.Succeeded.Should().BeTrue();
        LssServiceResult.Success.ToString().Should().Be("success");
        var failed = new LssServiceResult(LssCommand.ManufacturerSpecific, 0x2A);
        failed.IsManufacturerSpecific.Should().BeTrue();
        failed.SpecError.Should().Be(0x2A);
        failed.ToString().Should().Contain("FF");
        (failed == new LssServiceResult(0xFF, 0x2A)).Should().BeTrue();
        (failed != LssServiceResult.Success).Should().BeTrue();
        failed.Equals((object)LssServiceResult.Success).Should().BeFalse();
        failed.Equals((object?)null).Should().BeFalse();
        object other = new LssAddress(0, 0, 0, 0);
        failed.Equals(other).Should().BeFalse();
        failed.GetHashCode().Should().Be(new LssServiceResult(0xFF, 0x2A).GetHashCode());

        LssFastscanResult.Found(new LssAddress(1, 0, 0, 1)).Succeeded.Should().BeTrue();
        LssFastscanResult.NoUnconfiguredSlave().Status.Should().Be(LssFastscanStatus.NoUnconfiguredSlave);
        LssFastscanResult.NoMatch(default).Succeeded.Should().BeFalse();
    }

    [Fact]
    public void Cia301_Table_Maps_The_Named_Rates_And_Not_The_Reserved_Indexes()
    {
        LssBitTiming.TryGetCia301Rate(2, out var rate).Should().BeTrue();
        rate.Should().Be(500_000);
        LssBitTiming.TryGetCia301Index(1_000_000, out var index).Should().BeTrue();
        index.Should().Be(0);
        LssBitTiming.TryGetCia301Rate(5, out _).Should().BeFalse();
        LssBitTiming.TryGetCia301Rate(LssBitTiming.AutomaticIndex, out _).Should().BeFalse();
        LssBitTiming.TryGetCia301Rate(10, out _).Should().BeFalse();
        LssBitTiming.TryGetCia301Index(123, out _).Should().BeFalse();
        LssBitTiming.Cia301Rates.Should().HaveCount(8);
        LssBitTiming.IsManufacturerTable(0x80).Should().BeTrue();
        LssBitTiming.IsManufacturerTable(0xFE).Should().BeTrue();
        LssBitTiming.IsManufacturerTable(0x7F).Should().BeFalse();
        LssBitTiming.IsManufacturerTable(0xFF).Should().BeFalse();
    }
}
