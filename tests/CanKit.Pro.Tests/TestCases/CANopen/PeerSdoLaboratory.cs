using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CanKit.Pro.CANopen;

namespace CanKit.Pro.Tests.TestCases.CANopen;

/// <summary>
/// Peer descriptions for tests. <see cref="Bind"/> attaches one description that lists every
/// object the existing SDO suites transfer, so those suites still reach the server. The gate
/// tests build their own, smaller, files.
/// </summary>
internal static class PeerSdoLaboratory
{
    public static CanOpenDeviceDescription Description { get; } = CanOpenDeviceDescription.ParseEds(Build());

    public static void Bind(ICanOpenNode client, params byte[] serverNodeIds)
    {
        foreach (var serverNodeId in serverNodeIds)
            client.BindPeerDeviceDescription(serverNodeId, Description);
    }

    /// <summary>An EDS whose only objects are variables at sub-index 0.</summary>
    public static CanOpenDeviceDescription Variables(params ushort[] indices)
    {
        var mandatory = new List<ushort>();
        var optional = new List<ushort>();
        var manufacturer = new List<ushort>();
        foreach (var index in indices)
        {
            if (index is 0x1000 or 0x1001 or 0x1018) mandatory.Add(index);
            else if (index < 0x2000) optional.Add(index);
            else manufacturer.Add(index);
        }

        var text = new StringBuilder();
        text.Append(Header);
        AppendList(text, "MandatoryObjects", mandatory);
        AppendList(text, "OptionalObjects", optional);
        AppendList(text, "ManufacturerObjects", manufacturer);
        foreach (var index in indices)
            AppendVariable(text, index);
        return CanOpenDeviceDescription.ParseEds(text.ToString());
    }

    private static string Build()
    {
        var records = new List<(ushort Index, int HighestSubindex)>
        {
            (0x1010, 1),
            (0x1011, 1),
            (0x1016, 4),
            (0x1018, 4),
            (0x1200, 2),
            (0x2001, 5),
        };
        for (ushort index = 0x1400; index <= 0x1403; index++) records.Add((index, 5));
        for (ushort index = 0x1600; index <= 0x1603; index++) records.Add((index, 8));
        for (ushort index = 0x1800; index <= 0x1803; index++) records.Add((index, 5));
        for (ushort index = 0x1A00; index <= 0x1A03; index++) records.Add((index, 8));

        var variables = new ushort[]
        {
            0x1000, 0x1001, 0x1005, 0x1006, 0x1008, 0x100C, 0x100D, 0x1014, 0x1017, 0x1900,
            0x2000, 0x2002, 0x2100, 0x2101, 0x2200, 0x2222,
            0x2500, 0x2600, 0x2700, 0x2800, 0x2900,
            0x2A00, 0x2A01, 0x2A02, 0x2A10,
            0x2B00, 0x2B01, 0x2B20, 0x2B21, 0x2FFF,
        };

        var mandatory = new List<ushort> { 0x1000, 0x1001, 0x1018 };
        var optional = new List<ushort>();
        var manufacturer = new List<ushort>();
        void Classify(ushort index)
        {
            if (index is 0x1000 or 0x1001 or 0x1018) return;
            if (index < 0x2000) optional.Add(index);
            else manufacturer.Add(index);
        }

        foreach (var index in variables) Classify(index);
        foreach (var (index, _) in records) Classify(index);

        var text = new StringBuilder();
        text.Append(Header);
        AppendList(text, "MandatoryObjects", mandatory);
        AppendList(text, "OptionalObjects", optional);
        AppendList(text, "ManufacturerObjects", manufacturer);
        foreach (var index in variables) AppendVariable(text, index);
        foreach (var (index, highest) in records) AppendRecord(text, index, highest);
        return text.ToString();
    }

    private const string Header =
        "[FileInfo]\n" +
        "FileName=laboratory.eds\n" +
        "FileVersion=1\n" +
        "FileRevision=0\n" +
        "EDSVersion=4.0\n" +
        "Description=SDO client test peer\n" +
        "CreationTime=10:00AM\n" +
        "CreationDate=09-26-2026\n" +
        "CreatedBy=CanKit.Pro tests\n" +
        "[DeviceInfo]\n" +
        "VendorName=CanKit.Pro\n" +
        "VendorNumber=0\n" +
        "ProductName=Laboratory peer\n" +
        "ProductNumber=0\n" +
        "RevisionNumber=0\n" +
        "OrderCode=LAB\n" +
        "BaudRate_500=1\n" +
        "SimpleBootUpMaster=0\n" +
        "SimpleBootUpSlave=1\n" +
        "Granularity=8\n" +
        "DynamicChannelsSupported=0\n" +
        "GroupMessaging=0\n" +
        "NrOfRXPDO=4\n" +
        "NrOfTXPDO=4\n" +
        "LSS_Supported=0\n";

    private static void AppendList(StringBuilder text, string section, List<ushort> indices)
    {
        text.Append('[').Append(section).Append("]\n");
        text.Append("SupportedObjects=").Append(indices.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
        for (int i = 0; i < indices.Count; i++)
        {
            text.Append((i + 1).ToString(CultureInfo.InvariantCulture));
            text.Append("=0x");
            text.Append(indices[i].ToString("X4", CultureInfo.InvariantCulture));
            text.Append('\n');
        }
    }

    private static void AppendVariable(StringBuilder text, ushort index)
    {
        text.Append('[').Append(index.ToString("X4", CultureInfo.InvariantCulture)).Append("]\n");
        text.Append("ParameterName=Object\n");
        text.Append("ObjectType=0x7\n");
        text.Append("DataType=0x0007\n");
        text.Append("AccessType=rw\n");
        text.Append("DefaultValue=0\n");
        text.Append("PDOMapping=0\n");
    }

    private static void AppendRecord(StringBuilder text, ushort index, int highestSubindex)
    {
        var hex = index.ToString("X4", CultureInfo.InvariantCulture);
        text.Append('[').Append(hex).Append("]\n");
        text.Append("ParameterName=Record\n");
        text.Append("ObjectType=0x9\n");
        text.Append("SubNumber=").Append(highestSubindex.ToString(CultureInfo.InvariantCulture)).Append('\n');
        for (int sub = 0; sub <= highestSubindex; sub++)
        {
            text.Append('[').Append(hex).Append("sub").Append(sub.ToString("X", CultureInfo.InvariantCulture)).Append("]\n");
            text.Append("ParameterName=Sub\n");
            text.Append("ObjectType=0x7\n");
            text.Append("DataType=0x0007\n");
            text.Append("AccessType=rw\n");
            text.Append("DefaultValue=0\n");
            text.Append("PDOMapping=0\n");
        }
    }
}
