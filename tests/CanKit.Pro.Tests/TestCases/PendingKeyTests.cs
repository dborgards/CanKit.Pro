using System;
using System.Collections.Generic;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Pro.RawCan;
using FluentAssertions;
using Xunit;

namespace CanKit.Pro.Tests.TestCases;

/// <summary>
/// Direct tests for <see cref="PendingKey"/>, the equality contract that decides which arriving
/// echo confirms which pending send (FR-RAW-031).
///
/// These have to call <c>Equals</c> directly, which is why <c>CanKit.Pro.RawCan</c> makes its
/// internals visible here. Driving the same type through <see cref="CanBusService"/> exercises
/// only the dictionary path, and a dictionary compares hashes first: two keys that differ in ID,
/// flags or frame kind land in different buckets and <c>Equals</c> is never called at all. So the
/// bus-level echo tests -- which do cover the *behaviour* -- cannot reach the short-circuit arms
/// of the comparison that implements it, and a bug in one of them would only ever surface as a
/// hash collision resolving to the wrong send: rare, non-deterministic, and diagnosed on the wire.
/// </summary>
public class PendingKeyTests
{
    private static readonly byte[] Payload = { 0x11, 0x22, 0x33, 0x44 };

    private static PendingKey Key(
        int id = 0x123,
        byte[]? payload = null,
        FrameFlags flags = FrameFlags.None,
        CanFrameType kind = CanFrameType.Can20)
        => PendingKey.ForPendingSend(id, payload ?? Payload, flags, kind);

    [Fact]
    public void Two_Sends_Of_The_Same_Frame_Have_Equal_Keys()
    {
        // Separate payload arrays on purpose: the key must compare content, not reference, or a
        // caller's second send of identical bytes would never match its own echo.
        var a = Key(payload: new byte[] { 0x11, 0x22, 0x33, 0x44 });
        var b = Key(payload: new byte[] { 0x11, 0x22, 0x33, 0x44 });

        a.Equals(b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    // Each case differs from the reference key in exactly one component, which is what pins the
    // short-circuit arms: a comparison that forgot to test that component would return true here.
    // The theory carries a discriminator rather than the key itself -- PendingKey is internal and
    // cannot appear in a public signature, and MemberData needs one.
    [Theory]
    [InlineData("id")]
    [InlineData("extended vs standard")]
    [InlineData("remote vs data")]
    [InlineData("error vs data")]
    [InlineData("frame kind")]
    [InlineData("payload content")]
    [InlineData("payload length")]
    [InlineData("empty payload")]
    public void Keys_Differing_In_One_Component_Are_Not_Equal(string component)
    {
        var other = component switch
        {
            "id" => Key(id: 0x124),
            "extended vs standard" => Key(flags: FrameFlags.Ext),
            "remote vs data" => Key(flags: FrameFlags.Rtr),
            "error vs data" => Key(flags: FrameFlags.Error),
            "frame kind" => Key(kind: CanFrameType.CanFd),
            "payload content" => Key(payload: new byte[] { 0x11, 0x22, 0x33, 0x45 }),
            "payload length" => Key(payload: new byte[] { 0x11, 0x22, 0x33 }),
            "empty payload" => Key(payload: Array.Empty<byte>()),
            _ => throw new ArgumentOutOfRangeException(nameof(component), component, null),
        };

        Key().Equals(other).Should().BeFalse();
        other.Equals(Key()).Should().BeFalse(); // symmetric
    }

    [Fact]
    public void Brs_And_Esi_Do_Not_Change_Identity()
    {
        // The counterpart to the cases above: these two flags are link-layer transmission
        // attributes, and an adapter may echo a frame whose BRS/ESI differ from what was asked
        // for. Including them would turn a matched echo into a spurious timeout.
        Key().Equals(Key(flags: FrameFlags.Brs)).Should().BeTrue();
        Key().Equals(Key(flags: FrameFlags.Esi)).Should().BeTrue();
        Key().Equals(Key(flags: FrameFlags.Brs | FrameFlags.Esi)).Should().BeTrue();
    }

    [Fact]
    public void The_Lookup_Key_Matches_The_Stored_Key_For_The_Same_Frame()
    {
        // ForEchoLookup aliases the caller's buffer instead of copying it, so that an arriving
        // echo costs no allocation on the dispatch hot path. The two factories must still agree,
        // or no echo would ever confirm anything.
        var stored = PendingKey.ForPendingSend(0x321, Payload, FrameFlags.Ext, CanFrameType.CanFd);
        var lookup = PendingKey.ForEchoLookup(0x321, Payload, FrameFlags.Ext, CanFrameType.CanFd);

        stored.Equals(lookup).Should().BeTrue();
        stored.GetHashCode().Should().Be(lookup.GetHashCode());
    }

    [Fact]
    public void ForPendingSend_Copies_The_Payload_So_A_Reused_Buffer_Cannot_Change_The_Key()
    {
        // The stored key outlives the caller's frame; on the RX side the adapter's lease behind
        // it may be recycled. If the key aliased that buffer, mutating it after the send would
        // silently repoint the key at a frame nobody sent.
        var buffer = new byte[] { 0x01, 0x02 };
        var stored = PendingKey.ForPendingSend(0x100, buffer, FrameFlags.None, CanFrameType.Can20);

        buffer[1] = 0xFF;

        stored.Equals(PendingKey.ForEchoLookup(0x100, new byte[] { 0x01, 0x02 },
            FrameFlags.None, CanFrameType.Can20)).Should().BeTrue();
        stored.Equals(PendingKey.ForEchoLookup(0x100, buffer,
            FrameFlags.None, CanFrameType.Can20)).Should().BeFalse();
    }

    [Fact]
    public void Equals_Object_Follows_The_Typed_Comparison()
    {
        // A Dictionary<PendingKey, ...> always takes the generic IEquatable path, so this
        // override is only reached by a non-generic caller. It still has to agree.
        object same = Key();
        object different = Key(id: 0x999);

        Key().Equals(same).Should().BeTrue();
        Key().Equals(different).Should().BeFalse();
        Key().Equals("not a key").Should().BeFalse();
        Key().Equals(null).Should().BeFalse();
    }

    [Fact]
    public void Distinct_Keys_Stay_Distinct_As_Dictionary_Entries()
    {
        // The property the FIFO actually relies on, asserted end to end over the components
        // above rather than through the bus.
        var map = new Dictionary<PendingKey, string>
        {
            [Key()] = "reference",
            [Key(id: 0x124)] = "other id",
            [Key(flags: FrameFlags.Ext)] = "extended",
            [Key(kind: CanFrameType.CanFd)] = "fd",
            [Key(payload: new byte[] { 0x11, 0x22, 0x33, 0x45 })] = "other payload",
        };

        map.Should().HaveCount(5);
        map[PendingKey.ForEchoLookup(0x123, Payload, FrameFlags.None, CanFrameType.Can20)]
            .Should().Be("reference");
    }
}
