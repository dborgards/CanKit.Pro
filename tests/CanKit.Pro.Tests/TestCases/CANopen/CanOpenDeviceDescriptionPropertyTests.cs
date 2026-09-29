using System;
using System.IO;
using System.Text;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.Tests.TestCases.Properties;
using EdsDcfNet.Exceptions;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

/// <summary>
/// Seeded mutation tests for the EDS/DCF loader (issue #210): whatever a fixture is mutated into,
/// loading either succeeds or throws the documented <see cref="EdsParseException"/>, and a
/// successful load can be queried without throwing.
/// </summary>
public class CanOpenDeviceDescriptionPropertyTests
{
    // Path.Combine is avoided on purpose: CodeQL flags it ("may silently drop its earlier arguments")
    // even when every later segment is a literal. Only known fixture names are accepted, and the
    // path is joined with the directory separator, which cannot drop the base directory.
    private static string Fixture(string name)
    {
        if (name is not ("device.eds" or "quirky.eds" or "device.dcf"))
            throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown fixture.");
        var sep = Path.DirectorySeparatorChar;
        var dir = AppContext.BaseDirectory.TrimEnd(sep, Path.AltDirectorySeparatorChar);
        return File.ReadAllText(dir + sep + "TestCases" + sep + "CANopen" + sep + "Fixtures" + sep + name);
    }

    private static string Mutate(SeededRun run, string text)
    {
        var rounds = run.Rng.Next(1, 4);
        for (int r = 0; r < rounds; r++)
        {
            var lines = text.Split('\n');
            switch (run.Rng.Next(8))
            {
                case 0: // truncate anywhere
                    text = text.Substring(0, run.Rng.Next(0, text.Length + 1));
                    break;
                case 1: // drop a line
                    text = string.Join("\n", Without(lines, run.Rng.Next(lines.Length)));
                    break;
                case 2: // duplicate a line
                    {
                        int at = run.Rng.Next(lines.Length);
                        text = string.Join("\n", Insert(lines, at, lines[at]));
                        break;
                    }
                case 3: // swap two lines
                    {
                        int x = run.Rng.Next(lines.Length), y = run.Rng.Next(lines.Length);
                        (lines[x], lines[y]) = (lines[y], lines[x]);
                        text = string.Join("\n", lines);
                        break;
                    }
                case 4: // flip a character to a random printable or control character
                    if (text.Length > 0)
                    {
                        var sb = new StringBuilder(text);
                        sb[run.Rng.Next(sb.Length)] = (char)run.Rng.Next(0, 0x250);
                        text = sb.ToString();
                    }
                    break;
                case 5: // replace the value of a random key with garbage
                    {
                        int at = run.Rng.Next(lines.Length);
                        int eq = lines[at].IndexOf('=');
                        if (eq >= 0)
                        {
                            string[] garbage = { "", "0x", "0xZZ", "-1", "99999999999999999999", "$NODEID+", "1e9", "\0", "0x100000000" };
                            lines[at] = lines[at].Substring(0, eq + 1) + garbage[run.Rng.Next(garbage.Length)];
                        }
                        text = string.Join("\n", lines);
                        break;
                    }
                case 6: // corrupt a section header
                    {
                        int at = run.Rng.Next(lines.Length);
                        if (lines[at].StartsWith("[", StringComparison.Ordinal))
                            lines[at] = run.Rng.Next(3) switch { 0 => "[", 1 => "[]", _ => "[" + lines[at].Substring(1).Replace("]", "") };
                        text = string.Join("\n", lines);
                        break;
                    }
                default: // insert junk bytes
                    {
                        int at = run.Rng.Next(0, text.Length + 1);
                        var junk = new StringBuilder();
                        for (int k = run.Rng.Next(1, 20); k > 0; k--) junk.Append((char)run.Rng.Next(1, 0x100));
                        text = text.Insert(at, junk.ToString());
                        break;
                    }
            }
        }
        return text;
    }

    private static string[] Without(string[] lines, int index)
    {
        var result = new string[lines.Length - 1];
        Array.Copy(lines, 0, result, 0, index);
        Array.Copy(lines, index + 1, result, index, lines.Length - index - 1);
        return result;
    }

    private static string[] Insert(string[] lines, int index, string line)
    {
        var result = new string[lines.Length + 1];
        Array.Copy(lines, 0, result, 0, index);
        result[index] = line;
        Array.Copy(lines, index, result, index + 1, lines.Length - index);
        return result;
    }

    private static void LoadsOrThrowsDocumented(Func<CanOpenDeviceDescription> load, SeededRun run, int iteration, string mutated)
    {
        var because = run.Tag(iteration, "mutated input:\n" + mutated);
        CanOpenDeviceDescription? description = null;
        var thrown = Record.Exception(() => description = load());

        if (thrown is EdsParseException)
            return; // the documented failure

        if (thrown is not null)
            Assert.Fail($"{thrown.GetType().FullName} escaped the loader instead of EdsParseException: {thrown.Message}\n{because}");
        var loaded = description!;

        var query = () =>
        {
            _ = loaded.Objects.Objects.Count;
            _ = loaded.DeviceInfo;
            _ = loaded.ParseDiagnostics.Count;
            _ = loaded.Contains(0x1000, 0);
            _ = loaded.Contains(0x2000, 1);
        };
        query.Should().NotThrow(because);
    }

    [Theory]
    [InlineData("device.eds", false, 210_301)]
    [InlineData("quirky.eds", false, 210_302)]
    [InlineData("device.dcf", true, 210_303)]
    public void Mutated_Fixtures_Load_Or_Throw_The_Documented_Exception(string fixture, bool isDcf, int seed)
    {
        var original = Fixture(fixture);
        var run = new SeededRun(seed);

        // The unmutated fixture must load, or the mutation loop below proves nothing.
        var baseline = isDcf ? CanOpenDeviceDescription.ParseDcf(original) : CanOpenDeviceDescription.ParseEds(original);
        baseline.Objects.Objects.Should().NotBeEmpty();

        for (int i = 0; i < SeededRun.Iterations; i++)
        {
            var mutated = Mutate(run, original);
            LoadsOrThrowsDocumented(
                () => isDcf ? CanOpenDeviceDescription.ParseDcf(mutated) : CanOpenDeviceDescription.ParseEds(mutated),
                run, i, mutated);
        }
    }
}
