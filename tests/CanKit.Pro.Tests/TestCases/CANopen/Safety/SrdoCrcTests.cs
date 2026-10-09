using System;
using System.Text;
using AwesomeAssertions;
using CanKit.Pro.CANopen.Safety;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Safety;

/// <summary>13FFh (CiA DSP 304 V1.0 §8.4.2.2): generator x^16+x^12+x^5+1 over the listed
/// field order. V1.0 names no initial value and no byte order; the record (spec decision) is
/// CRC-16/XMODEM, multi-byte fields MSB-first. The canonical sequence is pinned here as a
/// golden vector so that #289 can tell a deliberate change from an accident (FR-CO-037).</summary>
public class SrdoCrcTests
{
    private static readonly SrdoCommunicationParameter Parameter = new(
        SrdoDirection.Transmit, TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(20), 0x101, 0x102);

    [Fact]
    public void Primitive_Is_Crc16_Xmodem()
    {
        SrdoCrc.Crc16Xmodem(Encoding.ASCII.GetBytes("123456789")).Should().Be(0x31C3);
        SrdoCrc.Crc16Xmodem(ReadOnlySpan<byte>.Empty).Should().Be(0x0000);
    }

    [Fact]
    public void Canonical_Sequence_Follows_The_Listed_Order()
    {
        var mapping = new SrdoMapping().Add(0x2000, 0x01, 16);
        SrdoCrc.CanonicalBytes(Parameter, mapping).Should().Equal(
            0x01,                         // a) direction
            0x00, 0x19,                   // b) refresh time 25 ms, MSB first
            0x14,                         // c) SRVT 20 ms
            0x00, 0x00, 0x01, 0x01,       // d) COB-ID 1
            0x00, 0x00, 0x01, 0x02,       // e) COB-ID 2
            0x02,                         // f) mapping sub0: two entries (one object, plain + inverted)
            0x01, 0x20, 0x00, 0x01, 0x10, // g1/h1) sub-index 1, 0x20000110
            0x02, 0x20, 0x00, 0x01, 0x10);// g2/h2) sub-index 2, same object, inverted slot
    }

    [Fact]
    public void Compute_Is_The_Primitive_Over_The_Canonical_Sequence()
    {
        var mapping = new SrdoMapping().Add(0x2000, 0x01, 16);
        SrdoCrc.Compute(Parameter, mapping).Should().Be(SrdoCrc.Crc16Xmodem(SrdoCrc.CanonicalBytes(Parameter, mapping)));
        SrdoCrc.Compute(Parameter, new SrdoMapping()).Should().Be(SrdoCrc.Crc16Xmodem(new byte[] { 0x01, 0x00, 0x19, 0x14, 0, 0, 1, 1, 0, 0, 1, 2, 0x00 }));
    }

    /// <summary>Exact to the tick: TimeSpan.FromMilliseconds rounds to whole milliseconds on .NET Framework.</summary>
    private static TimeSpan Ms(double milliseconds) => TimeSpan.FromTicks((long)Math.Round(milliseconds * TimeSpan.TicksPerMillisecond));

    /// <summary>The fields are UNSIGNED16 and UNSIGNED8 milliseconds and a direction 0..2
    /// (§8.4.2.2): a value outside is refused rather than cut to the field, which would checksum
    /// a record nobody wrote. 0 is allowed — the device path checksums what its dictionary holds.
    /// The bound applies to the rounded value.</summary>
    [Theory]
    [InlineData(65535.0, 20.0, SrdoDirection.Transmit, true)]
    [InlineData(65535.4, 20.0, SrdoDirection.Transmit, true)]
    [InlineData(65535.5, 20.0, SrdoDirection.Transmit, false)]
    [InlineData(65536.0, 20.0, SrdoDirection.Transmit, false)]
    [InlineData(25.0, 255.0, SrdoDirection.Receive, true)]
    [InlineData(25.0, 255.4, SrdoDirection.Receive, true)]
    [InlineData(25.0, 255.5, SrdoDirection.Receive, false)]
    [InlineData(25.0, 256.0, SrdoDirection.Receive, false)]
    [InlineData(0.0, 0.0, SrdoDirection.None, true)]
    [InlineData(-0.4, -0.4, SrdoDirection.Transmit, true)]
    [InlineData(-1.0, 20.0, SrdoDirection.Transmit, false)]
    [InlineData(25.0, -1.0, SrdoDirection.Transmit, false)]
    [InlineData(25.0, 20.0, (SrdoDirection)3, false)]
    [InlineData(25.0, 20.0, (SrdoDirection)255, false)]
    public void Compute_Refuses_A_Field_Outside_Its_Range(double cycleMs, double validationMs, SrdoDirection direction, bool accepted)
    {
        var parameter = new SrdoCommunicationParameter(direction, Ms(cycleMs), Ms(validationMs), 0x101, 0x102);
        var compute = () => SrdoCrc.Compute(parameter, new SrdoMapping());
        if (accepted) compute.Should().NotThrow();
        else compute.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("parameter");
    }

    /// <summary>The times go in as whole milliseconds, rounded half to even (Math.Round).</summary>
    [Fact]
    public void Times_Are_Rounded_To_Whole_Milliseconds_Half_To_Even()
    {
        static ushort Crc(double cycleMs, double validationMs) => SrdoCrc.Compute(
            Parameter with { RefreshOrSafeguardCycleTime = Ms(cycleMs), ValidationTime = Ms(validationMs) },
            new SrdoMapping());
        Crc(24.5, 20).Should().Be(Crc(24, 20));
        Crc(25.5, 20).Should().Be(Crc(26, 20));
        Crc(25, 20.5).Should().Be(Crc(25, 20));
        Crc(25, 21.5).Should().Be(Crc(25, 22));
    }

    [Fact]
    public void Every_Field_Changes_The_Checksum()
    {
        var mapping = new SrdoMapping().Add(0x2000, 0x01, 16);
        ushort reference = SrdoCrc.Compute(Parameter, mapping);
        SrdoCrc.Compute(Parameter with { Direction = SrdoDirection.Receive }, mapping).Should().NotBe(reference);
        SrdoCrc.Compute(Parameter with { RefreshOrSafeguardCycleTime = TimeSpan.FromMilliseconds(26) }, mapping).Should().NotBe(reference);
        SrdoCrc.Compute(Parameter with { ValidationTime = TimeSpan.FromMilliseconds(21) }, mapping).Should().NotBe(reference);
        SrdoCrc.Compute(Parameter with { CobId1 = 0x103 }, mapping).Should().NotBe(reference);
        SrdoCrc.Compute(Parameter with { CobId2 = 0x104 }, mapping).Should().NotBe(reference);
        SrdoCrc.Compute(Parameter, new SrdoMapping().Add(0x2000, 0x02, 16)).Should().NotBe(reference);
    }
}
