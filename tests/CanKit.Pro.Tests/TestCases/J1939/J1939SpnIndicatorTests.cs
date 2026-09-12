using System;
using CanKit.Pro.J1939;
using FluentAssertions;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.J1939;

/// <summary>
/// Known-answer tests for the SAE J1939-71 §5.1.1 indicator ranges (SRS FR-J1939-002, issue #37).
/// Every SPN reserves the top of its raw range for "not available", "error", reserved and
/// parameter-specific codes; extraction must report those as indicators instead of scaling them
/// into plausible measurements. Each width is pinned at both boundaries of every band, so a
/// classifier that is off by one raw count fails here.
/// </summary>
public class J1939SpnIndicatorTests
{
    // -------------------------------------------------------------------------------------
    // Unsigned: the codes live in the leading byte for 1/2/4-byte fields.
    // -------------------------------------------------------------------------------------

    [Theory]
    // 1 byte: valid 0x00..0xFA, 0xFB parameter specific, 0xFC..0xFD reserved, 0xFE error,
    // 0xFF not available.
    [InlineData(0x00UL, 8, J1939SpnValueKind.Valid)]
    [InlineData(0xFAUL, 8, J1939SpnValueKind.Valid)]
    [InlineData(0xFBUL, 8, J1939SpnValueKind.ParameterSpecific)]
    [InlineData(0xFCUL, 8, J1939SpnValueKind.Reserved)]
    [InlineData(0xFDUL, 8, J1939SpnValueKind.Reserved)]
    [InlineData(0xFEUL, 8, J1939SpnValueKind.Error)]
    [InlineData(0xFFUL, 8, J1939SpnValueKind.NotAvailable)]
    // 2 bytes: the same codes in the leading byte, so each band is 256 raw counts wide.
    [InlineData(0x0000UL, 16, J1939SpnValueKind.Valid)]
    [InlineData(0xFAFFUL, 16, J1939SpnValueKind.Valid)]
    [InlineData(0xFB00UL, 16, J1939SpnValueKind.ParameterSpecific)]
    [InlineData(0xFBFFUL, 16, J1939SpnValueKind.ParameterSpecific)]
    [InlineData(0xFC00UL, 16, J1939SpnValueKind.Reserved)]
    [InlineData(0xFDFFUL, 16, J1939SpnValueKind.Reserved)]
    [InlineData(0xFE00UL, 16, J1939SpnValueKind.Error)]
    [InlineData(0xFEFFUL, 16, J1939SpnValueKind.Error)]
    [InlineData(0xFF00UL, 16, J1939SpnValueKind.NotAvailable)]
    [InlineData(0xFFFFUL, 16, J1939SpnValueKind.NotAvailable)]
    // 4 bytes.
    [InlineData(0x00000000UL, 32, J1939SpnValueKind.Valid)]
    [InlineData(0xFAFFFFFFUL, 32, J1939SpnValueKind.Valid)]
    [InlineData(0xFB000000UL, 32, J1939SpnValueKind.ParameterSpecific)]
    [InlineData(0xFBFFFFFFUL, 32, J1939SpnValueKind.ParameterSpecific)]
    [InlineData(0xFC000000UL, 32, J1939SpnValueKind.Reserved)]
    [InlineData(0xFDFFFFFFUL, 32, J1939SpnValueKind.Reserved)]
    [InlineData(0xFE000000UL, 32, J1939SpnValueKind.Error)]
    [InlineData(0xFEFFFFFFUL, 32, J1939SpnValueKind.Error)]
    [InlineData(0xFF000000UL, 32, J1939SpnValueKind.NotAvailable)]
    [InlineData(0xFFFFFFFFUL, 32, J1939SpnValueKind.NotAvailable)]
    public void Classify_Unsigned_ByteWidths(ulong raw, int bitLength, J1939SpnValueKind expected)
        => J1939Spn.Classify(raw, bitLength).Should().Be(expected);

    [Theory]
    // 4-bit table: 0x0..0xA valid, 0xB parameter specific, 0xC..0xD reserved, 0xE error,
    // 0xF not available.
    [InlineData(0x0UL, 4, J1939SpnValueKind.Valid)]
    [InlineData(0xAUL, 4, J1939SpnValueKind.Valid)]
    [InlineData(0xBUL, 4, J1939SpnValueKind.ParameterSpecific)]
    [InlineData(0xCUL, 4, J1939SpnValueKind.Reserved)]
    [InlineData(0xDUL, 4, J1939SpnValueKind.Reserved)]
    [InlineData(0xEUL, 4, J1939SpnValueKind.Error)]
    [InlineData(0xFUL, 4, J1939SpnValueKind.NotAvailable)]
    // 2-bit status table: 00/01 valid, 10 error, 11 not available. No reserved band exists.
    [InlineData(0b00UL, 2, J1939SpnValueKind.Valid)]
    [InlineData(0b01UL, 2, J1939SpnValueKind.Valid)]
    [InlineData(0b10UL, 2, J1939SpnValueKind.Error)]
    [InlineData(0b11UL, 2, J1939SpnValueKind.NotAvailable)]
    // A single bit has no room for an indicator: both states are the signal.
    [InlineData(0UL, 1, J1939SpnValueKind.Valid)]
    [InlineData(1UL, 1, J1939SpnValueKind.Valid)]
    public void Classify_Unsigned_SubByteWidths(ulong raw, int bitLength, J1939SpnValueKind expected)
        => J1939Spn.Classify(raw, bitLength).Should().Be(expected);

    [Theory]
    // Widths J1939-71 does not tabulate follow the same leading-group rule: 12 bits use the
    // leading byte, so the bands are 16 raw counts wide.
    [InlineData(0xFAFUL, 12, J1939SpnValueKind.Valid)]
    [InlineData(0xFB0UL, 12, J1939SpnValueKind.ParameterSpecific)]
    [InlineData(0xFDFUL, 12, J1939SpnValueKind.Reserved)]
    [InlineData(0xFE0UL, 12, J1939SpnValueKind.Error)]
    [InlineData(0xFEFUL, 12, J1939SpnValueKind.Error)]
    [InlineData(0xFF0UL, 12, J1939SpnValueKind.NotAvailable)]
    [InlineData(0xFFFUL, 12, J1939SpnValueKind.NotAvailable)]
    // 64 bits: the leading byte again.
    [InlineData(0xFAFF_FFFF_FFFF_FFFFUL, 64, J1939SpnValueKind.Valid)]
    [InlineData(0xFE00_0000_0000_0000UL, 64, J1939SpnValueKind.Error)]
    [InlineData(0xFFFF_FFFF_FFFF_FFFFUL, 64, J1939SpnValueKind.NotAvailable)]
    public void Classify_Unsigned_NonTabulatedWidths(ulong raw, int bitLength, J1939SpnValueKind expected)
        => J1939Spn.Classify(raw, bitLength).Should().Be(expected);

    // -------------------------------------------------------------------------------------
    // Signed: the same codes sit at the top of the *signed* range, i.e. with the sign bit
    // clear. Everything with the sign bit set is a measurement.
    // -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0x00UL, 8, J1939SpnValueKind.Valid)]
    [InlineData(0x7AUL, 8, J1939SpnValueKind.Valid)]          // +122, the largest measurement
    [InlineData(0x7BUL, 8, J1939SpnValueKind.ParameterSpecific)]
    [InlineData(0x7CUL, 8, J1939SpnValueKind.Reserved)]
    [InlineData(0x7DUL, 8, J1939SpnValueKind.Reserved)]
    [InlineData(0x7EUL, 8, J1939SpnValueKind.Error)]
    [InlineData(0x7FUL, 8, J1939SpnValueKind.NotAvailable)]
    [InlineData(0x80UL, 8, J1939SpnValueKind.Valid)]          // -128, not an indicator
    [InlineData(0xFEUL, 8, J1939SpnValueKind.Valid)]          // -2: unsigned 0xFE is "error",
    [InlineData(0xFFUL, 8, J1939SpnValueKind.Valid)]          // -1: unsigned 0xFF is "n/a"
    [InlineData(0x7AFFUL, 16, J1939SpnValueKind.Valid)]
    [InlineData(0x7B00UL, 16, J1939SpnValueKind.ParameterSpecific)]
    [InlineData(0x7C00UL, 16, J1939SpnValueKind.Reserved)]
    [InlineData(0x7DFFUL, 16, J1939SpnValueKind.Reserved)]
    [InlineData(0x7E00UL, 16, J1939SpnValueKind.Error)]
    [InlineData(0x7EFFUL, 16, J1939SpnValueKind.Error)]
    [InlineData(0x7F00UL, 16, J1939SpnValueKind.NotAvailable)]
    [InlineData(0x7FFFUL, 16, J1939SpnValueKind.NotAvailable)]
    [InlineData(0x8000UL, 16, J1939SpnValueKind.Valid)]
    [InlineData(0xFFFFUL, 16, J1939SpnValueKind.Valid)]
    public void Classify_Signed_UsesTopOfSignedRange(ulong raw, int bitLength, J1939SpnValueKind expected)
        => J1939Spn.Classify(raw, bitLength, isSigned: true).Should().Be(expected);

    [Theory]
    [InlineData(0b11UL, 2)]
    [InlineData(0xFUL, 4)]
    [InlineData(0x7FUL, 7)]
    public void Classify_Signed_SubByte_HasNoIndicators(ulong raw, int bitLength)
    {
        // J1939-71 tabulates no signed sub-byte SLOTs. Borrowing the unsigned codes would report
        // real measurements as missing, so every pattern stays a measurement.
        J1939Spn.Classify(raw, bitLength, isSigned: true).Should().Be(J1939SpnValueKind.Valid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    [InlineData(-1)]
    public void Classify_RejectsBitLengthOutOfRange(int bitLength)
    {
        var act = () => J1939Spn.Classify(0, bitLength);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // -------------------------------------------------------------------------------------
    // End-to-end through Extract: the regression from issue #37 and its neighbours.
    // -------------------------------------------------------------------------------------

    [Fact]
    public void Extract_EngineSpeed_NotAvailable_IsNotReportedAs_8191_875_Rpm()
    {
        // SPN 190 at 0.125 rpm/bit. 0xFFFF * 0.125 = 8191.875 — the value this issue is about.
        var payload = new byte[] { 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };

        var speed = J1939Spn.Extract(payload, byteOffset: 3, startBit: 0,
            bitLength: 16, resolution: 0.125, offset: 0.0);

        speed.Kind.Should().Be(J1939SpnValueKind.NotAvailable);
        speed.IsNotAvailable.Should().BeTrue();
        speed.IsValid.Should().BeFalse();
        speed.Raw.Should().Be(0xFFFFUL);
        speed.TryGetValue(out _).Should().BeFalse();
        speed.GetValueOrDefault(-1.0).Should().Be(-1.0);

        var act = () => speed.Value;
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*NotAvailable*");
    }

    [Fact]
    public void Extract_EngineSpeed_TopOfValidBand_IsStillAMeasurement()
    {
        // 0xFAFF is the last valid raw for a 2-byte SPN: 64255 * 0.125 = 8031.875 rpm.
        var payload = new byte[8];
        payload[3] = 0xFF;
        payload[4] = 0xFA;

        var speed = J1939Spn.Extract(payload, byteOffset: 3, startBit: 0,
            bitLength: 16, resolution: 0.125, offset: 0.0);

        speed.IsValid.Should().BeTrue();
        speed.Value.Should().BeApproximately(8031.875, 1e-9);
        speed.Raw.Should().Be(0xFAFFUL);

        // One raw count further and it is the parameter-specific indicator, not 8032.0 rpm.
        payload[3] = 0x00;
        payload[4] = 0xFB;
        J1939Spn.Extract(payload, byteOffset: 3, startBit: 0, bitLength: 16,
            resolution: 0.125, offset: 0.0).Kind
            .Should().Be(J1939SpnValueKind.ParameterSpecific);
    }

    [Theory]
    [InlineData((byte)0xFB, J1939SpnValueKind.ParameterSpecific)]
    [InlineData((byte)0xFC, J1939SpnValueKind.Reserved)]
    [InlineData((byte)0xFD, J1939SpnValueKind.Reserved)]
    [InlineData((byte)0xFE, J1939SpnValueKind.Error)]
    [InlineData((byte)0xFF, J1939SpnValueKind.NotAvailable)]
    public void Extract_OneByteSpn_Indicators_AreNotScaled(byte raw, J1939SpnValueKind expected)
    {
        // SPN 513 shape: 1 %/bit with a -125 % offset. Scaling 0xFF would give a plausible 130 %.
        var payload = new byte[8];
        payload[2] = raw;

        var torque = J1939Spn.Extract(payload, byteOffset: 2, bitLength: 8,
            resolution: 1.0, offset: -125.0);

        torque.Kind.Should().Be(expected);
        torque.Raw.Should().Be((ulong)raw);
    }

    [Fact]
    public void Extract_FourByteSpn_ErrorBand_SpansTheWholeLeadingByte()
    {
        // 0xFE000000 and 0xFEFFFFFF are both "error" — the band is not a single raw value.
        var low = new byte[] { 0x00, 0x00, 0x00, 0xFE };
        var high = new byte[] { 0xFF, 0xFF, 0xFF, 0xFE };
        var justBelow = new byte[] { 0xFF, 0xFF, 0xFF, 0xFD };

        J1939Spn.Extract(low, byteOffset: 0, bitLength: 32, resolution: 1.0, offset: 0.0)
            .Kind.Should().Be(J1939SpnValueKind.Error);
        J1939Spn.Extract(high, byteOffset: 0, bitLength: 32, resolution: 1.0, offset: 0.0)
            .Kind.Should().Be(J1939SpnValueKind.Error);
        J1939Spn.Extract(justBelow, byteOffset: 0, bitLength: 32, resolution: 1.0, offset: 0.0)
            .Kind.Should().Be(J1939SpnValueKind.Reserved);
    }

    [Fact]
    public void Extract_SubByteField_IsClassifiedAgainstItsOwnWidth()
    {
        // A 4-bit field of 0xF is "not available", not 15 * 0.5 = 7.5.
        var payload = new byte[] { 0xF0 };

        var value = J1939Spn.Extract(payload, byteOffset: 0, startBit: 4, bitLength: 4,
            resolution: 0.5, offset: 0.0);

        value.Kind.Should().Be(J1939SpnValueKind.NotAvailable);

        payload[0] = 0xA0;   // 0xA is the last valid nibble.
        J1939Spn.Extract(payload, byteOffset: 0, startBit: 4, bitLength: 4,
            resolution: 0.5, offset: 0.0).Value.Should().Be(5.0);
    }

    // -------------------------------------------------------------------------------------
    // Signed extraction.
    // -------------------------------------------------------------------------------------

    [Theory]
    [InlineData((byte)0x00, (byte)0x00, 0.0)]
    [InlineData((byte)0x18, (byte)0xFC, -100.0)]   // raw -1000 at 0.1/bit
    [InlineData((byte)0xE8, (byte)0x03, 100.0)]    // raw +1000 at 0.1/bit
    [InlineData((byte)0x00, (byte)0x80, -3276.8)]  // most negative 16-bit two's complement
    [InlineData((byte)0xFF, (byte)0xFF, -0.1)]     // -1: an ordinary measurement when signed
    public void Extract_Signed_SignExtendsBeforeScaling(byte low, byte high, double expected)
    {
        var value = J1939Spn.Extract(new[] { low, high }, byteOffset: 0, bitLength: 16,
            resolution: 0.1, offset: 0.0, isSigned: true);

        value.IsValid.Should().BeTrue();
        value.Value.Should().BeApproximately(expected, 1e-9);
    }

    [Fact]
    public void Extract_Signed_KeepsTheUnsignedBitPatternInRaw()
    {
        var value = J1939Spn.Extract(new byte[] { 0x18, 0xFC }, byteOffset: 0, bitLength: 16,
            resolution: 0.1, offset: 0.0, isSigned: true);

        value.Raw.Should().Be(0xFC18UL, "Raw is the pattern off the wire, not the signed number");
        value.Value.Should().BeApproximately(-100.0, 1e-9);
    }

    [Theory]
    [InlineData((byte)0x00, (byte)0x7F, J1939SpnValueKind.NotAvailable)]
    [InlineData((byte)0xFF, (byte)0x7F, J1939SpnValueKind.NotAvailable)]
    [InlineData((byte)0x00, (byte)0x7E, J1939SpnValueKind.Error)]
    [InlineData((byte)0x00, (byte)0x7B, J1939SpnValueKind.ParameterSpecific)]
    [InlineData((byte)0xFF, (byte)0x7A, J1939SpnValueKind.Valid)]
    public void Extract_Signed_Indicators(byte low, byte high, J1939SpnValueKind expected)
    {
        J1939Spn.Extract(new[] { low, high }, byteOffset: 0, bitLength: 16,
            resolution: 0.1, offset: 0.0, isSigned: true).Kind.Should().Be(expected);
    }

    [Theory]
    [InlineData((byte)0x00, (byte)0x00, 0L)]
    [InlineData((byte)0xFF, (byte)0x7F, 32767L)]
    [InlineData((byte)0x00, (byte)0x80, -32768L)]
    [InlineData((byte)0xFF, (byte)0xFF, -1L)]
    public void ExtractRawSigned_SignExtends(byte low, byte high, long expected)
        => J1939Spn.ExtractRawSigned(new[] { low, high }, byteOffset: 0, startBit: 0, bitLength: 16)
            .Should().Be(expected);

    [Fact]
    public void ExtractRawSigned_HandlesFullWidthAndSubByteFields()
    {
        var all = new byte[8];
        for (int i = 0; i < all.Length; i++) all[i] = 0xFF;
        J1939Spn.ExtractRawSigned(all, byteOffset: 0, startBit: 0, bitLength: 64).Should().Be(-1L);

        // 4-bit field 0b1001 = -7 sign-extended.
        J1939Spn.ExtractRawSigned(new byte[] { 0x90 }, byteOffset: 0, startBit: 4, bitLength: 4)
            .Should().Be(-7L);
    }

    // -------------------------------------------------------------------------------------
    // The value type itself.
    // -------------------------------------------------------------------------------------

    [Fact]
    public void Default_J1939SpnValue_IsNotAvailable_NotAValidZero()
    {
        // An uninitialised reading must not look like a measurement of zero.
        default(J1939SpnValue).Kind.Should().Be(J1939SpnValueKind.NotAvailable);
        default(J1939SpnValue).IsValid.Should().BeFalse();
    }

    [Fact]
    public void J1939SpnValue_Factories_And_Equality()
    {
        var a = J1939SpnValue.FromPhysical(1000, 125.0);
        var b = J1939SpnValue.FromPhysical(1000, 125.0);
        var c = J1939SpnValue.FromIndicator(J1939SpnValueKind.Error, 0xFE00);

        a.Should().Be(b);
        a.Should().NotBe(c);
        a.Value.Should().Be(125.0);
        c.IsError.Should().BeTrue();
        c.Raw.Should().Be(0xFE00UL);

        var act = () => J1939SpnValue.FromIndicator(J1939SpnValueKind.Valid, 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void J1939SpnValue_ToString_NamesTheIndicator()
    {
        J1939SpnValue.FromIndicator(J1939SpnValueKind.NotAvailable, 0xFFFF).ToString()
            .Should().Be("NotAvailable (raw 0xFFFF)");
        J1939SpnValue.FromPhysical(0x10, 2.0).ToString().Should().Be("2");
    }
}
