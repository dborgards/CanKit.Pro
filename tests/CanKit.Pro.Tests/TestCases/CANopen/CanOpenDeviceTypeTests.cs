using AwesomeAssertions;
using CanKit.Pro.CANopen;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

/// <summary>
/// The device-type word (object <c>1000h</c>) splits into the profile number in its low 16 bits
/// and the additional information in its high 16 bits.
/// </summary>
public class CanOpenDeviceTypeTests
{
    [Theory]
    [InlineData(0x000F0191u, 401, 0x000F, true, false)]   // CiA 401, four function bits
    [InlineData(0xFFFF0198u, 408, 0xFFFF, true, true)]    // CiA 408 first, more follow
    [InlineData(0x00000000u, 0, 0x0000, false, false)]
    [InlineData(0xFFFF0000u, 0, 0xFFFF, false, true)]     // §7.5.2.1: no profile, further device
    public void The_Word_Splits_Into_Profile_And_Additional_Information(
        uint value, int profile, int additional, bool standardized, bool further)
    {
        var type = new CanOpenDeviceType(value);
        type.ProfileNumber.Should().Be((ushort)profile);
        type.AdditionalInformation.Should().Be((ushort)additional);
        type.FollowsStandardizedProfile.Should().Be(standardized);
        type.HasFurtherLogicalDevices.Should().Be(further);
    }

    [Fact]
    public void Four_Little_Endian_Bytes_Parse_And_Any_Other_Length_Does_Not()
    {
        CanOpenDeviceType.TryParse(new byte[] { 0x91, 0x01, 0x0F, 0x00 }, out var type).Should().BeTrue();
        type.Value.Should().Be(0x000F0191u);
        CanOpenDeviceType.TryParse(new byte[3], out _).Should().BeFalse();
        CanOpenDeviceType.TryParse(new byte[5], out _).Should().BeFalse();
    }

    [Fact]
    public void ToString_Names_The_Profile_In_Decimal()
    {
        new CanOpenDeviceType(0x000F0191u).ToString().Should().Be("0x000F0191 (profile 401)");
        new CanOpenDeviceType(0u).ToString().Should().Be("0x00000000 (no profile)");
    }
}
