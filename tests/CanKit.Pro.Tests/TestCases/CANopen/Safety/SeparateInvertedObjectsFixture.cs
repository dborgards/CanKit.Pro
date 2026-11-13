using System;
using AwesomeAssertions;
using CanKit.Pro.CANopen;

namespace CanKit.Pro.Tests.TestCases.CANopen.Safety;

/// <summary>The safety fixture of node 5 with SRDO 1 keeping its inverted data in objects of their
/// own (EN 50325-5:2010 §6.4.1.4, Table 8; #506): 1381h:02 names 2003h:00 (16 bits, the inverted
/// copy of 2000h:00) and 1381h:04 names 2004h:00 (8 bits, of 2001h:00).</summary>
internal static class SeparateInvertedObjectsFixture
{
    public const uint Plain1 = 0x2000_0010, Inverted1 = 0x2003_0010, Plain2 = 0x2001_0008, Inverted2 = 0x2004_0008;

    public static string Text()
    {
        var text = CanOpenFixtures.Text("safety.dcf").Replace("\r\n", "\n");
        text = Patch(text, "[1381sub2]", "ParameterValue=0x20000010", "ParameterValue=0x20030010");
        text = Patch(text, "[1381sub4]", "ParameterValue=0x20010008", "ParameterValue=0x20040008");
        text = Patch(text, "[ManufacturerObjects]", "SupportedObjects=2\n1=0x2000\n2=0x2001\n",
            "SupportedObjects=4\n1=0x2000\n2=0x2001\n3=0x2003\n4=0x2004\n");
        return text.TrimEnd('\n') + "\n"
            + "\n[2003]\nParameterName=Safe process value (inverted)\nObjectType=0x7\nDataType=0x0006\nAccessType=ro\nDefaultValue=0xEDCB\nPDOMapping=1\n"
            + "\n[2004]\nParameterName=Safe flags (inverted)\nObjectType=0x7\nDataType=0x0005\nAccessType=ro\nDefaultValue=0xA5\nPDOMapping=1\n";
    }

    public static CanOpenDeviceDescription File() => CanOpenDeviceDescription.ParseDcf(Text());

    /// <summary>Replaces the first <paramref name="old"/> inside <paramref name="section"/>.</summary>
    public static string Patch(string text, string section, string old, string replacement)
    {
        int at = text.IndexOf(section, StringComparison.Ordinal);
        at.Should().BeGreaterThan(-1, section);
        int hit = text.IndexOf(old, at, StringComparison.Ordinal);
        hit.Should().BeGreaterThan(-1, old);
        return text[..hit] + replacement + text[(hit + old.Length)..];
    }
}
