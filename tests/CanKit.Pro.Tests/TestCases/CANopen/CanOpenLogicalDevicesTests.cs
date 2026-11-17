using System;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

/// <summary>
/// A node holds up to eight logical devices. Each owns an 800h-wide window of the object
/// dictionary starting at 6000h, and its PDO numbers are 64 apart.
/// </summary>
public class CanOpenLogicalDevicesTests
{
    [Theory]
    [InlineData(0, 0x6000, 0x67FF, 0)]
    [InlineData(1, 0x6800, 0x6FFF, 64)]
    [InlineData(7, 0x9800, 0x9FFF, 448)]
    public void Each_Logical_Device_Has_Its_Object_Area_Device_Type_Object_And_Pdo_Offset(
        int index, int areaStart, int deviceTypeIndex, int pdoOffset)
    {
        CanOpenLogicalDevices.ObjectAreaStart(index).Should().Be((ushort)areaStart);
        CanOpenLogicalDevices.DeviceTypeIndex(index).Should().Be((ushort)deviceTypeIndex);
        CanOpenLogicalDevices.PdoNumberOffset(index).Should().Be(pdoOffset);
    }

    [Fact]
    public void A_Profile_Index_Moves_By_800h_Per_Logical_Device()
    {
        CanOpenLogicalDevices.ToAbsoluteIndex(0, 0x6200).Should().Be(0x6200);
        CanOpenLogicalDevices.ToAbsoluteIndex(2, 0x6200).Should().Be(0x7200);
        FluentActions.Invoking(() => CanOpenLogicalDevices.ToAbsoluteIndex(0, 0x5FFF)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => CanOpenLogicalDevices.ToAbsoluteIndex(0, 0x6800)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void A_Logical_Device_Carries_Its_Device_Type_And_Its_Place_In_The_Dictionary()
    {
        var deviceType = new CanOpenDeviceType(0xFFFF0198u);

        var device = new CanOpenLogicalDevice(3, deviceType);

        device.Index.Should().Be(3);
        device.DeviceType.Should().Be(deviceType);
        device.ObjectAreaStart.Should().Be(0x7800);
        device.PdoNumberOffset.Should().Be(192);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(8)]
    public void There_Are_Eight_Logical_Devices_And_No_More(int index)
    {
        FluentActions.Invoking(() => CanOpenLogicalDevices.ObjectAreaStart(index)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new CanOpenLogicalDevice(index, default)).Should().Throw<ArgumentOutOfRangeException>();
    }
}
