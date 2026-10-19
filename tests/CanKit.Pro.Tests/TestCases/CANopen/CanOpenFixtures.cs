using System;
using System.IO;

namespace CanKit.Pro.Tests.TestCases.CANopen;

/// <summary>The files under <c>TestCases/CANopen/Fixtures</c>, read as text. Path.Combine is
/// avoided on purpose: CodeQL flags it ("may silently drop its earlier arguments") even when every
/// later segment is a literal. Only known fixture names are accepted, and the path is joined with
/// the directory separator, which cannot drop the base directory.</summary>
internal static class CanOpenFixtures
{
    public static string Text(string name)
    {
        if (name is not ("device.eds" or "quirky.eds" or "device.dcf" or "safety.dcf"))
            throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown fixture.");
        var sep = Path.DirectorySeparatorChar;
        var dir = AppContext.BaseDirectory.TrimEnd(sep, Path.AltDirectorySeparatorChar);
        return File.ReadAllText(dir + sep + "TestCases" + sep + "CANopen" + sep + "Fixtures" + sep + name);
    }
}
