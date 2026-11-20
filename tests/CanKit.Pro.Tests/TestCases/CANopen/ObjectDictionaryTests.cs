using System;
using System.Collections.Generic;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

/// <summary>
/// Unit tests for the runtime local Object Dictionary (SRS FR-CO-001). No bus, no async — the
/// dictionary is a pure in-process data structure so it can be exercised in isolation before
/// the SDO server / PDO layer wire it into the node loop.
/// </summary>
public class ObjectDictionaryTests
{
    // FR-CO-001: round-trip typed writes read back with the correct value.
    [Fact]
    public void U32_RoundTrip_ReturnsWrittenValue()
    {
        var od = new ObjectDictionary();
        od.AddU32(0x2000, 0x00, 0xDEADBEEFu);

        od.ReadUnsigned(0x2000, 0x00).Should().Be(0xDEADBEEFu);
        od.ReadRaw(0x2000, 0x00).Should().Equal(0xEF, 0xBE, 0xAD, 0xDE);
    }

    // FR-CO-001: typed writes into a fixed-width entry must respect the declared width.
    [Fact]
    public void U16_TypedWrite_RejectsOverflow()
    {
        var od = new ObjectDictionary();
        od.AddU16(0x2001, 0x00, 0);
        Assert.Throws<InvalidOperationException>(() =>
            od.WriteUnsigned(0x2001, 0x00, 0x1_0000u));
    }

    // FR-CO-001: typed read against the wrong data-type family raises rather than silently
    // returning garbage.
    [Fact]
    public void SignedRead_ThrowsOnUnsignedEntry()
    {
        var od = new ObjectDictionary();
        od.AddU32(0x2002, 0x00, 42);
        Assert.Throws<InvalidOperationException>(() => od.ReadSigned(0x2002, 0x00));
    }

    // FR-CO-001: missing entries are visible via TryGet without raising.
    [Fact]
    public void TryGet_MissingEntry_ReturnsFalse()
    {
        var od = new ObjectDictionary();
        od.TryGet(0x1234, 0x00, out _).Should().BeFalse();
        Assert.Throws<KeyNotFoundException>(() => od.ReadRaw(0x1234, 0x00));
    }

    // FR-CO-001: DOMAIN entries can grow/shrink on each raw write.
    [Fact]
    public void Domain_Roundtrip_AllowsResize()
    {
        var od = new ObjectDictionary();
        od.AddDomain(0x2100, 0x00, new byte[] { 1, 2, 3 });
        od.ReadRaw(0x2100, 0x00).Should().Equal(1, 2, 3);

        od.WriteRaw(0x2100, 0x00, new byte[] { 9, 8, 7, 6, 5 });
        od.ReadRaw(0x2100, 0x00).Should().Equal(9, 8, 7, 6, 5);
    }

    // FR-CO-001: read-only / write-only access flags survive round-trip and are visible to
    // higher-level code (they are what the SDO server uses to emit the right abort code).
    [Fact]
    public void AccessFlags_Preserved()
    {
        var od = new ObjectDictionary();
        od.AddU16(0x1000, 0x00, 0x1234, OdAccess.ReadOnly);
        od.AddU16(0x1000, 0x01, 0x5678, OdAccess.WriteOnly);
        od.TryGet(0x1000, 0x00, out var ro).Should().BeTrue();
        ro.Access.Should().Be(OdAccess.ReadOnly);
        od.TryGet(0x1000, 0x01, out var wo).Should().BeTrue();
        wo.Access.Should().Be(OdAccess.WriteOnly);
    }

    // FR-CO-014 (#133 review) — a write's validation and its store are one transaction. The
    // validators read the dictionary (the node's 1016h rule refuses a second entry for a node-id
    // another slot already monitors), so two writes validating against the same state could both
    // pass a rule only one of them may. Two threads released together, many rounds: exactly one
    // write per round may succeed. Without the write gate both usually succeed.
    [Fact]
    public void Validation_And_Store_Are_One_Transaction()
    {
        var od = new ObjectDictionary();
        od.AddU32(0x1016, 0x01, 0);
        od.AddU32(0x1016, 0x02, 0);
        od.WriteValidator = (index, subindex, value) =>
        {
            uint entry = ObjectDictionary.DecodeU32(value);
            byte nodeId = (byte)((entry >> 16) & 0xFF);
            for (byte s = 1; s <= 2; s++)
            {
                if (s == subindex) continue;
                uint other = od.ReadUnsigned(index, s);
                if ((ushort)(other & 0xFFFF) != 0 && (byte)((other >> 16) & 0xFF) == nodeId)
                    return OdWriteDecision.Reject(CanKit.Pro.CANopen.Sdo.SdoAbortCode.GeneralParameterIncompatibility);
            }
            return OdWriteDecision.Accept;
        };

        const uint sameProducer = (0x11u << 16) | 100u;
        for (int round = 0; round < 200; round++)
        {
            od.WriteRaw(0x1016, 0x01, new byte[4]);
            od.WriteRaw(0x1016, 0x02, new byte[4]);
            using var start = new System.Threading.Barrier(2);
            int succeeded = 0;
            void Write(byte slot)
            {
                start.SignalAndWait();
                try
                {
                    od.WriteUnsigned(0x1016, slot, sameProducer);
                    System.Threading.Interlocked.Increment(ref succeeded);
                }
                catch (ArgumentException)
                {
                    // rejected with 0604 0043h: the other slot already holds the producer
                }
            }
            var first = new System.Threading.Thread(() => Write(0x01));
            var second = new System.Threading.Thread(() => Write(0x02));
            first.Start();
            second.Start();
            first.Join();
            second.Join();
            succeeded.Should().Be(1, $"round {round}: one of two simultaneous writes for the same producer passes, never both");
        }
    }

    // #566: a validator may store a normalised value in place of the written one, of the same length.
    [Fact]
    public void A_Replacement_Is_Stored_In_Place_Of_The_Written_Value()
    {
        var od = new ObjectDictionary();
        od.AddU8(0x2000, 0x00, 0);
        od.WriteValidator = (_, _, value) => OdWriteDecision.AcceptAs(new[] { (byte)(value[0] & 0x0F) });
        var written = new byte[] { 0xAB };
        od.WriteRaw(0x2000, 0x00, written);
        od.ReadUnsigned(0x2000, 0x00).Should().Be(0x0Bu);
        written.Should().Equal(new byte[] { 0xAB }, "the caller's array is not changed");
    }

    // A replacement of another length than the written value is a programming error of the validator.
    // A fixed-width entry would refuse it on its own; a domain takes any length, so the check is the
    // dictionary's.
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void A_Replacement_Of_Another_Length_Is_Refused(int length)
    {
        var od = new ObjectDictionary();
        od.AddDomain(0x2000, 0x00, new byte[] { 0x07, 0x07 });
        od.WriteValidator = (_, _, _) => OdWriteDecision.AcceptAs(new byte[length]);
        Action write = () => od.WriteRaw(0x2000, 0x00, new byte[] { 0x01, 0x02 });
        write.Should().Throw<InvalidOperationException>().WithMessage("*validator*");
        od.ReadRaw(0x2000, 0x00).Should().Equal(new byte[] { 0x07, 0x07 }, "nothing was stored");
    }
}
