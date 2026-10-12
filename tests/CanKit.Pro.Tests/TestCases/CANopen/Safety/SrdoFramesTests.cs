using AwesomeAssertions;
using CanKit.Pro.CANopen.Safety;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Safety;

/// <summary>CiA DSP 304 V1.0 §8.1: the second frame is the first "inverted bitwise"; Figure 7 /
/// Table 4: COB-ID 1 is odd in 257..383, COB-ID 2 even in 258..384 (FR-CO-036, FR-CO-039,
/// FR-CO-040).</summary>
public class SrdoFramesTests
{
    [Fact]
    public void Invert_Flips_Every_Bit_And_Keeps_The_Length()
    {
        SrdoFrames.Invert(new byte[] { 0x00, 0xFF, 0x5A }).Should().Equal(0xFF, 0x00, 0xA5);
        SrdoFrames.Invert(new byte[0]).Should().BeEmpty();
    }

    [Fact]
    public void A_Pair_Is_Equal_Length_And_Bitwise_Inverse()
    {
        SrdoFrames.IsInversePair(new byte[] { 0x12, 0x34 }, new byte[] { 0xED, 0xCB }).Should().BeTrue();
        SrdoFrames.IsInversePair(new byte[] { 0x12, 0x34 }, new byte[] { 0xED, 0xCA }).Should().BeFalse("one bit differs");
        SrdoFrames.IsInversePair(new byte[] { 0x12, 0x34 }, new byte[] { 0xED }).Should().BeFalse("lengths differ");
        SrdoFrames.IsInversePair(new byte[0], new byte[0]).Should().BeTrue("L = 0 is allowed (§8.1.3.1)");
    }

    [Theory]
    [InlineData(0x101u, true, false)]
    [InlineData(0x102u, false, true)]
    [InlineData(0x17Fu, true, false)]
    [InlineData(0x180u, false, true)]
    [InlineData(0x0FFu, false, false)]
    [InlineData(0x181u, false, false)]
    [InlineData(0x182u, false, false)]
    public void CobId_Rules_Of_Figure_7(uint canId, bool isFirst, bool isSecond)
    {
        SrdoFrames.IsCobId1(canId).Should().Be(isFirst);
        SrdoFrames.IsCobId2(canId).Should().Be(isSecond);
    }
}
