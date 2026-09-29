using System;
using AwesomeAssertions;
using CanKit.Pro.J1939;
using CanKit.Pro.Tests.TestCases.Properties;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.J1939;

/// <summary>
/// Seeded differential tests (issue #210): <see cref="J1939Spn.ExtractRaw"/> and
/// <see cref="J1939Spn.ExtractRawSigned"/> against a naive bit-by-bit reference.
/// </summary>
public class J1939SpnExtractPropertyTests
{
    // Field bit k is bit (startBit + k) of the byte string starting at byteOffset, little-endian.
    private static ulong ReferenceRaw(byte[] payload, int byteOffset, int startBit, int bitLength)
    {
        ulong value = 0;
        for (int k = 0; k < bitLength; k++)
        {
            int absolute = startBit + k;
            int bit = (payload[byteOffset + absolute / 8] >> (absolute % 8)) & 1;
            value |= (ulong)bit << k;
        }
        return value;
    }

    private static long ReferenceSigned(ulong raw, int bitLength)
    {
        bool negative = ((raw >> (bitLength - 1)) & 1) != 0;
        long result = 0;
        for (int k = 0; k < 64; k++)
        {
            bool bit = k < bitLength ? ((raw >> k) & 1) != 0 : negative;
            if (bit) result |= 1L << k;
        }
        return result;
    }

    [Fact]
    public void ExtractRaw_Matches_A_BitByBit_Reference()
    {
        var run = new SeededRun(210_101);
        for (int i = 0; i < SeededRun.Iterations * 5; i++)
        {
            var payload = run.Bytes(run.Rng.Next(1, 25));
            int startBit = run.Rng.Next(0, 8);
            int bitLength = run.Rng.Next(3) == 0 ? run.Rng.Next(57, 65) : run.Rng.Next(1, 65);
            int maxOffset = payload.Length - ((startBit + bitLength + 7) >> 3);
            if (maxOffset < 0) continue;
            int byteOffset = run.Rng.Next(0, maxOffset + 1);
            var because = run.Tag(i, $"offset={byteOffset} startBit={startBit} bitLength={bitLength} payload={SeededRun.Hex(payload)}");

            ulong expected = ReferenceRaw(payload, byteOffset, startBit, bitLength);

            J1939Spn.ExtractRaw(payload, byteOffset, startBit, bitLength).Should().Be(expected, because);
            J1939Spn.ExtractRawSigned(payload, byteOffset, startBit, bitLength)
                .Should().Be(ReferenceSigned(expected, bitLength), because + " signed");
        }
    }

    [Fact]
    public void ExtractRaw_Rejects_Every_Out_Of_Range_Argument_With_ArgumentOutOfRange()
    {
        var run = new SeededRun(210_102);
        for (int i = 0; i < SeededRun.Iterations; i++)
        {
            var payload = run.Bytes(run.Rng.Next(0, 12));
            int byteOffset = run.Rng.Next(-3, 14);
            int startBit = run.Rng.Next(-3, 12);
            int bitLength = run.Rng.Next(-3, 70);
            var because = run.Tag(i, $"len={payload.Length} offset={byteOffset} startBit={startBit} bitLength={bitLength}");

            bool valid = byteOffset >= 0 && startBit is >= 0 and <= 7 && bitLength is >= 1 and <= 64
                && byteOffset + ((startBit + bitLength + 7) >> 3) <= payload.Length;

            if (valid)
            {
                J1939Spn.ExtractRaw(payload, byteOffset, startBit, bitLength)
                    .Should().Be(ReferenceRaw(payload, byteOffset, startBit, bitLength), because);
            }
            else
            {
                var act = () => J1939Spn.ExtractRaw(payload, byteOffset, startBit, bitLength);
                act.Should().Throw<ArgumentOutOfRangeException>(because);
            }
        }
    }
}
