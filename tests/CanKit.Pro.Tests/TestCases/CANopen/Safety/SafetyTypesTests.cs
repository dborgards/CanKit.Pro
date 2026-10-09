using System;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Pdo;
using CanKit.Pro.CANopen.Safety;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Safety;

/// <summary>The value types of CiA DSP 304 V1.0: the pre-defined connection set (§8.3.3
/// Table 4), the mapping builder (§8.4.2.2, 8 byte-aligned objects) and the node options.</summary>
public class SafetyTypesTests
{
    [Theory]
    [InlineData(1, 0x101u, 0x102u)]
    [InlineData(32, 0x13Fu, 0x140u)]
    [InlineData(33, 0x141u, 0x142u)]
    [InlineData(64, 0x17Fu, 0x180u)]
    public void Default_CobIds_Follow_Table_4(byte nodeId, uint cobId1, uint cobId2)
    {
        CanOpenCobId.SrdoDefaultCobId1(nodeId).Should().Be(cobId1, "COB-ID 1 = FFh + 2 × node-id");
        CanOpenCobId.SrdoDefaultCobId2(nodeId).Should().Be(cobId2, "COB-ID 2 = 100h + 2 × node-id");
    }

    [Theory]
    [InlineData(65)]
    [InlineData(127)]
    [InlineData(0)]
    public void Node_Ids_Above_64_Have_No_Default_CobId(byte nodeId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CanOpenCobId.SrdoDefaultCobId1(nodeId));
        Assert.Throws<ArgumentOutOfRangeException>(() => CanOpenCobId.SrdoDefaultCobId2(nodeId));
    }

    [Fact]
    public void Mapping_Holds_At_Most_Eight_Byte_Aligned_Objects()
    {
        var mapping = new SrdoMapping();
        for (ushort i = 0; i < 8; i++) mapping.Add((ushort)(0x2000 + i), 0x00, 8);
        mapping.TotalBytes.Should().Be(8);
        Assert.Throws<InvalidOperationException>(() => mapping.Add(0x2008, 0x00, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SrdoMapping().Add(0x2000, 0x00, 12));
        Assert.Throws<InvalidOperationException>(() => new SrdoMapping().Add(0x2000, 0x00, 64).Add(0x2001, 0x00, 8));
    }

    [Fact]
    public void Options_Validate_SrdoCount()
    {
        new CanOpenNodeOptions().SrdoCount.Should().Be(0);
        new CanOpenNodeOptions().EnableChangeOfStateSrdo.Should().BeTrue();
        new CanOpenNodeOptions { SrdoCount = 64 }.With(enableChangeOfStateSrdo: false).SrdoCount.Should().Be(64);
        Assert.Throws<ArgumentOutOfRangeException>(() => new CanOpenNodeOptions { SrdoCount = 65 }.With());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CanOpenNodeOptions { SrdoCount = -1 }.With());
    }

    /// <summary>A caller compiled against 1.3.0 binds to <c>With</c> by its exact 13-parameter
    /// signature; adding the SRDO parameters to that method would have been a binary break. The
    /// kept overload carries the SRDO options of the record over.</summary>
    [Fact]
    public void The_1_3_0_With_Signature_Still_Exists_And_Keeps_The_Srdo_Options()
    {
        var signature = new[]
        {
            typeof(TimeSpan?), typeof(TimeSpan?), typeof(TimeSpan?), typeof(int?), typeof(int?), typeof(int?),
            typeof(byte?), typeof(bool?), typeof(int?), typeof(bool?), typeof(bool?), typeof(bool?),
            typeof(CanOpenNodeProfile?),
        };
        var method = typeof(CanOpenNodeOptions).GetMethod(nameof(CanOpenNodeOptions.With), signature);
        method.Should().NotBeNull("the 1.3.0 signature of With is what compiled callers reference");

        var source = new CanOpenNodeOptions { SrdoCount = 3, EnableChangeOfStateSrdo = false };
        var copy = (CanOpenNodeOptions)method!.Invoke(source, new object?[] { null, null, null, null, null, null, null, null, null, null, null, null, null })!;
        copy.SrdoCount.Should().Be(3);
        copy.EnableChangeOfStateSrdo.Should().BeFalse();
    }

    [Fact]
    public void State_And_Events_Carry_Their_Values()
    {
        var ts = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
        var received = new SrdoReceivedEventArgs(3, 0x105, new byte[] { 1, 2 }, ts);
        received.SrdoNumber.Should().Be(3);
        received.CobId.Should().Be(0x105u);
        received.Payload.Should().Equal(1, 2);
        var changed = new SrdoStateChangedEventArgs(3, false, SrdoInvalidReason.Mismatch, ts);
        changed.IsValid.Should().BeFalse();
        changed.Reason.Should().Be(SrdoInvalidReason.Mismatch);
        new GlobalFailsafeCommandReceivedEventArgs(ts).Timestamp.Should().Be(ts);
    }
}
