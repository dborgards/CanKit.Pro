using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using CanKit.Pro.CANopen;

namespace CanKit.Pro.Tests.TestCases.CANopen;

/// <summary>
/// An EDS as text, edited by section. <see cref="ToString"/> lists every object section in
/// <c>[MandatoryObjects]</c> (<c>1000h</c>, <c>1001h</c>, <c>1018h</c>) or <c>[OptionalObjects]</c>,
/// so a removed or added object leaves no stale entry in either list.
/// </summary>
internal sealed class Eds
{
    private static readonly ushort[] Mandatory = { 0x1000, 0x1001, 0x1018 };

    private readonly List<(string Name, List<string> Lines)> _sections;

    private Eds(List<(string Name, List<string> Lines)> sections) => _sections = sections;

    internal static Eds Of(string fixture) => new(Sections(CanOpenFixtures.Text(fixture)));

    /// <summary>Removes object <paramref name="index"/> with all its sub-index sections.</summary>
    internal Eds Remove(ushort index)
    {
        string name = Hex(index);
        _sections.RemoveAll(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
            || s.Name.StartsWith(name + "sub", StringComparison.OrdinalIgnoreCase));
        return this;
    }

    /// <summary>Renames object <paramref name="from"/> with all its sub-index sections to
    /// <paramref name="to"/>.</summary>
    internal Eds Move(ushort from, ushort to)
    {
        string f = Hex(from), t = Hex(to);
        for (int i = 0; i < _sections.Count; i++)
        {
            var (name, lines) = _sections[i];
            if (name.Equals(f, StringComparison.OrdinalIgnoreCase)) _sections[i] = (t, lines);
            else if (name.StartsWith(f + "sub", StringComparison.OrdinalIgnoreCase)) _sections[i] = (t + name.Substring(f.Length), lines);
        }

        return this;
    }

    internal Eds RemoveSection(string section)
    {
        _sections.RemoveAll(s => s.Name.Equals(section, StringComparison.OrdinalIgnoreCase)).Should().Be(1, section);
        return this;
    }

    /// <summary>Sets <paramref name="key"/> of <paramref name="section"/>, replacing the line that
    /// has it or adding one.</summary>
    internal Eds Set(string section, string key, string value)
    {
        var lines = _sections.Single(s => s.Name.Equals(section, StringComparison.OrdinalIgnoreCase)).Lines;
        int at = lines.FindIndex(l => l.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));
        if (at >= 0) lines[at] = key + "=" + value;
        else lines.Add(key + "=" + value);
        return this;
    }

    /// <summary>Keeps the first <paramref name="elements"/> elements of array <paramref name="index"/>.</summary>
    internal Eds Truncate(ushort index, int elements)
    {
        string name = Hex(index);
        _sections.RemoveAll(s => s.Name.StartsWith(name + "sub", StringComparison.OrdinalIgnoreCase)
            && int.Parse(s.Name.Substring(name.Length + 3), NumberStyles.HexNumber, CultureInfo.InvariantCulture) > elements);
        Set(name, "SubNumber", (elements + 1).ToString(CultureInfo.InvariantCulture));
        return Set(name + "sub0", "DefaultValue", elements.ToString(CultureInfo.InvariantCulture));
    }

    internal Eds Add(string sections)
    {
        _sections.AddRange(Sections(sections).Where(s => s.Name.Length > 0));
        return this;
    }

    /// <summary>The edited file, parsed; it must parse without a diagnostic.</summary>
    internal CanOpenDeviceDescription Parse()
    {
        var description = CanOpenDeviceDescription.ParseEds(ToString());
        description.ParseDiagnostics.Should().BeEmpty("the edited file is well-formed");
        return description;
    }

    /// <summary>The edited file as a DCF commissioned for <paramref name="nodeId"/>.</summary>
    internal CanOpenDeviceDescription ParseDcf(byte nodeId)
    {
        _sections.Insert(_sections.FindIndex(s => s.Name == "DeviceInfo"), ("DeviceComissioning", new List<string>
        {
            "NodeID=0x" + nodeId.ToString("X2", CultureInfo.InvariantCulture),
            "NodeName=Profile test device",
            "Baudrate=500",
            "NetNumber=1",
            "NetworkName=test",
            "CANopenManager=0",
        }));
        var description = CanOpenDeviceDescription.ParseDcf(ToString());
        description.ParseDiagnostics.Should().BeEmpty("the edited file is well-formed");
        return description;
    }

    public override string ToString()
    {
        var objects = _sections.Select(s => s.Name)
            .Where(n => n.Length == 4 && ushort.TryParse(n, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            .Select(n => ushort.Parse(n, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
            .OrderBy(i => i)
            .ToList();
        var text = new StringBuilder();
        foreach (var (name, lines) in _sections)
        {
            if (name.Length > 0) text.Append('[').Append(name).Append("]\n");
            if (name == "MandatoryObjects") List(text, objects.Where(i => Mandatory.Contains(i)));
            else if (name == "OptionalObjects") List(text, objects.Where(i => !Mandatory.Contains(i)));
            else foreach (var line in lines) text.Append(line).Append('\n');
            text.Append('\n');
        }

        return text.ToString();
    }

    private static void List(StringBuilder text, IEnumerable<ushort> indices)
    {
        var list = indices.ToList();
        text.Append(string.Format(CultureInfo.InvariantCulture, "SupportedObjects={0}\n", list.Count));
        for (int i = 0; i < list.Count; i++)
            text.Append(string.Format(CultureInfo.InvariantCulture, "{0}=0x{1:X4}\n", i + 1, list[i]));
    }

    private static string Hex(ushort index) => index.ToString("X4", CultureInfo.InvariantCulture);

    private static List<(string Name, List<string> Lines)> Sections(string text)
    {
        var sections = new List<(string Name, List<string> Lines)> { (string.Empty, new List<string>()) };
        foreach (var line in text.Split('\n').Select(raw => raw.TrimEnd('\r')))
        {
            if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
            {
                sections.Add((line.Substring(1, line.Length - 2), new List<string>()));
                continue;
            }

            if (line.Length > 0) sections[sections.Count - 1].Lines.Add(line);
        }

        return sections;
    }
}
