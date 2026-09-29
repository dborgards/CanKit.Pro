using System;

namespace CanKit.Pro.Tests.TestCases.Properties;

/// <summary>
/// Hand-written, seeded generator loops (issue #210). No property-testing library: a fixed-seed
/// <see cref="Random"/> makes every run identical, and <see cref="Tag"/> puts the seed and the
/// iteration into each assertion message so a red run is reproduced by re-running the one test.
/// </summary>
internal sealed class SeededRun
{
    /// <summary>Iterations per property: enough to reach the corners, well under a second.</summary>
    public const int Iterations = 400;

    public SeededRun(int seed)
    {
        Seed = seed;
        Rng = new Random(seed);
    }

    public int Seed { get; }

    public Random Rng { get; }

    public string Tag(int iteration, string? detail = null)
        => $"seed={Seed} iteration={iteration}" + (detail is null ? "" : " " + detail);

    public byte[] Bytes(int length)
    {
        var b = new byte[length];
        Rng.NextBytes(b);
        return b;
    }

    // The tests project also targets net48, which has neither BitOperations nor Convert.ToHexString.
    public static int PopCount(uint value)
    {
        int count = 0;
        for (; value != 0; value &= value - 1) count++;
        return count;
    }

    public static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "");

    public uint NextUInt()
        => ((uint)Rng.Next(0, 1 << 16) << 16) | (uint)Rng.Next(0, 1 << 16);
}
