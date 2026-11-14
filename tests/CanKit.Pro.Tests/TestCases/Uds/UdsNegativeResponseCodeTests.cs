using System;
using System.Linq;
using AwesomeAssertions;
using CanKit.Pro.Uds;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.Uds;

/// <summary>
/// FR-UDS-010: <see cref="UdsNegativeResponseCode"/> names every code ISO 14229-1:2026 Table A.1
/// names, and nothing in the ranges the table reserves.
/// </summary>
public class UdsNegativeResponseCodeTests
{
    // The codes Table A.1 names, by value. 0x00 (positiveResponse) is no negative response.
    private static readonly byte[] Named =
    {
        0x10, 0x11, 0x12, 0x13, 0x14,
        0x21, 0x22, 0x24, 0x25, 0x26,
        0x31, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A,
        0x50, 0x51, 0x52, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x5B, 0x5C, 0x5D,
        0x70, 0x71, 0x72, 0x73, 0x78, 0x7E, 0x7F,
        0x81, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89, 0x8A, 0x8B, 0x8C, 0x8D, 0x8F,
        0x90, 0x91, 0x92, 0x93, 0x94,
    };

    [Fact]
    public void The_Enum_Names_Exactly_The_Codes_Of_Table_A1()
    {
        Enum.GetValues(typeof(UdsNegativeResponseCode)).Cast<UdsNegativeResponseCode>()
            .Select(c => (byte)c).Should().BeEquivalentTo(Named);
    }

    [Theory]
    [InlineData(0x14, UdsNegativeResponseCode.ResponseTooLong)]
    [InlineData(0x26, UdsNegativeResponseCode.FailurePreventsExecutionOfRequestedAction)]
    [InlineData(0x34, UdsNegativeResponseCode.AuthenticationRequired)]
    [InlineData(0x5D, UdsNegativeResponseCode.DeAuthenticationFailed)]
    [InlineData(0x8F, UdsNegativeResponseCode.BrakeSwitchesNotClosed)]
    [InlineData(0x94, UdsNegativeResponseCode.ResourceTemporarilyNotAvailable)]
    public void A_Named_Code_Maps_To_Its_Member(byte code, UdsNegativeResponseCode expected)
    {
        var ex = new UdsNegativeResponseException(UdsServiceId.RoutineControl, code);

        ex.CodeAsEnum.Should().Be(expected);
        ex.CodeName.Should().Be(expected.ToString());
        ex.Message.Should().Contain(expected.ToString());
    }

    [Theory]
    [InlineData(0x01)]
    [InlineData(0x23)]
    [InlineData(0x5E)]
    [InlineData(0x8E)]
    [InlineData(0x95)]
    [InlineData(0xF0)]
    [InlineData(0xFF)]
    public void A_Reserved_Or_Manufacturer_Code_Has_No_Member(byte code)
    {
        var ex = new UdsNegativeResponseException(UdsServiceId.RoutineControl, code);

        ex.CodeAsEnum.Should().BeNull();
        ex.CodeName.Should().Be($"0x{code:X2}");
    }
}
