using System;
using System.Collections.Generic;
using CanKit.Pro.J1939;
using FluentAssertions;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.J1939;

/// <summary>
/// Verifies the SPN catalog (FR-J1939-002 convenience layer): the built-in SAE J1939-71
/// definitions decode crafted payloads to the expected physical values, custom SPNs can be
/// registered, and unknown SPNs fail loudly.
/// </summary>
public class J1939SpnCatalogTests
{
    [Fact]
    public void Default_Catalog_Decodes_Eec1_EngineSpeed()
    {
        // EEC1 with SPN 190 (Engine Speed) = 2500 rpm at byte offset 3 (0.125 rpm/bit).
        var payload = new byte[8];
        var raw = (ushort)(2500.0 / 0.125);
        payload[3] = (byte)(raw & 0xFF);
        payload[4] = (byte)(raw >> 8);

        var rpm = J1939SpnCatalog.Default.Extract(payload, 190);

        rpm.IsValid.Should().BeTrue();
        rpm.Value.Should().BeApproximately(2500.0, 0.01);
    }

    [Fact]
    public void Default_Catalog_Reports_NotAvailable_EngineSpeed_As_Indicator()
    {
        // An EEC1 frame from an ECU that does not have SPN 190: every bit set. The defect this
        // guards (issue #37) reported 0xFFFF as 8191.875 rpm.
        var payload = new byte[8];
        for (int i = 0; i < payload.Length; i++) payload[i] = 0xFF;

        var rpm = J1939SpnCatalog.Default.Extract(payload, 190);

        rpm.Kind.Should().Be(J1939SpnValueKind.NotAvailable);
        rpm.IsValid.Should().BeFalse();
        rpm.Raw.Should().Be(0xFFFFUL);
        var act = () => rpm.Value;
        act.Should().Throw<InvalidOperationException>();
        rpm.GetValueOrDefault().Should().Be(double.NaN);
    }

    [Fact]
    public void Default_Catalog_Registers_Signed_Definitions()
    {
        // A vendor SPN declared as a signed SLOT: raw 0xFFFF is "not available" (leading byte
        // 0xFF, sign bit set), while 0x8000 is the most negative measurement, not an indicator.
        var catalog = new J1939SpnCatalog();
        catalog.Register(new J1939SpnDefinition(
            Spn: 4201, Name: "Vendor Steering Angle", Pgn: 0xFE01,
            ByteOffset: 0, StartBit: 0, BitLength: 16, Resolution: 0.1, Offset: 0.0,
            Unit: "deg", IsSigned: true));

        catalog.TryGet(4201, out var definition).Should().BeTrue();
        definition!.IsSigned.Should().BeTrue();

        // -100 deg => raw -1000 => 0xFC18 two's complement. Leading byte 0xFC is *not* an
        // indicator for a signed SPN — those sit at 0x7B..0x7F.
        var negative = catalog.Extract(new byte[] { 0x18, 0xFC }, 4201);
        negative.IsValid.Should().BeTrue();
        negative.Value.Should().BeApproximately(-100.0, 0.001);

        catalog.Extract(new byte[] { 0xFF, 0x7F }, 4201).Kind
            .Should().Be(J1939SpnValueKind.NotAvailable);
    }

    // A definition is not only an extraction recipe — it is how a caller labels a reading in a
    // UI or a log. Those descriptive members had no assertion at all, so a catalog entry could
    // carry the wrong PGN or unit and every decode test would still pass.
    [Fact]
    public void A_Catalog_Definition_Describes_The_Parameter_As_Well_As_Decoding_It()
    {
        J1939SpnCatalog.Default.TryGet(190, out var engineSpeed).Should().BeTrue();

        engineSpeed!.Spn.Should().Be(190);
        engineSpeed.Name.Should().NotBeNullOrWhiteSpace();
        engineSpeed.Pgn.Should().Be(0xF004);          // EEC1
        engineSpeed.Unit.Should().Be("rpm");
        engineSpeed.BitLength.Should().Be(16);
        engineSpeed.Resolution.Should().Be(0.125);
        engineSpeed.IsSigned.Should().BeFalse();      // SPN 190 is an unsigned SLOT

        // The same three descriptive members survive a round trip through Register.
        var catalog = new J1939SpnCatalog();
        catalog.Register(new J1939SpnDefinition(
            Spn: 4202, Name: "Vendor Coolant Level", Pgn: 0xFE02,
            ByteOffset: 0, StartBit: 0, BitLength: 8, Resolution: 0.4, Offset: 0.0, Unit: "%"));

        catalog.TryGet(4202, out var vendor).Should().BeTrue();
        vendor!.Name.Should().Be("Vendor Coolant Level");
        vendor.Pgn.Should().Be(0xFE02u);
        vendor.Unit.Should().Be("%");
    }

    [Fact]
    public void Default_Catalog_Decodes_Torque_And_Pedal_And_VehicleSpeed()
    {
        // EEC1: SPN 513 Actual Engine Percent Torque = 40 % (raw 165 with -125 offset).
        var eec1 = new byte[8];
        eec1[2] = 165;
        J1939SpnCatalog.Default.Extract(eec1, 513).Value.Should().BeApproximately(40.0, 0.01);

        // EEC2: SPN 91 Accelerator Pedal Position 1 = 50 % (raw 125 at 0.4 %/bit).
        var eec2 = new byte[8];
        eec2[1] = 125;
        J1939SpnCatalog.Default.Extract(eec2, 91).Value.Should().BeApproximately(50.0, 0.01);

        // CCVS: SPN 84 Wheel-Based Vehicle Speed = 90 km/h (raw 90*256 LE at offset 1).
        var ccvs = new byte[8];
        var raw = (ushort)(90 * 256);
        ccvs[1] = (byte)(raw & 0xFF);
        ccvs[2] = (byte)(raw >> 8);
        J1939SpnCatalog.Default.Extract(ccvs, 84).Value.Should().BeApproximately(90.0, 0.01);
    }

    [Fact]
    public void Register_CustomSpn_Then_Extract_By_Number()
    {
        var catalog = new J1939SpnCatalog();
        catalog.Register(new J1939SpnDefinition(
            Spn: 4200, Name: "Vendor Oil Pressure", Pgn: 0xFE00,
            ByteOffset: 0, StartBit: 4, BitLength: 8, Resolution: 0.5, Offset: 0.0, Unit: "bar"));

        // Raw field: 8 bits at startBit 4 of byte 0 => value bits are payload[0] >> 4.
        var payload = new byte[] { 0x50, 0x00 }; // raw = 5 => 2.5 bar
        catalog.Extract(payload, 4200).Value.Should().BeApproximately(2.5, 0.001);
    }

    [Fact]
    public void Extract_UnknownSpn_Throws_KeyNotFound()
    {
        var act = () => J1939SpnCatalog.Default.Extract(new byte[8], 9999);
        act.Should().Throw<KeyNotFoundException>();
    }
}
