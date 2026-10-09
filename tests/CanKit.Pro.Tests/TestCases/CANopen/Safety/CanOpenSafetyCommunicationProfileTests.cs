using System;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Safety;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Safety;

/// <summary>The safety objects 1300h–13FFh as managed communication-profile objects (CiA DSP 304
/// V1.0 §8.4.2): their defaults, the value rules a write is held to on every path, the 13FEh
/// auto-reset, and that a node without SRDOs has none of them (FR-CO-035, FR-CO-036).</summary>
public class CanOpenSafetyCommunicationProfileTests : IClassFixture<VirtualAdapterFixture>
{
    private const byte Device = 0x11;

    private static ICanBus Open(string session, int channel) => CanBus.Open(
        $"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(VirtualAdapterFixture.Bitrate));

    private static ICanOpenNode OpenDevice(ICanBus bus, int srdoCount = 2, byte nodeId = Device)
        => CanOpen.OpenNode(bus, nodeId, new CanOpenNodeOptions { SrdoCount = srdoCount, WritableCommunicationParameters = true });

    private static SdoAbortCode Rejected(Action write)
    {
        var ex = Assert.Throws<ArgumentException>(write);
        const string marker = "SDO abort 0x";
        var hex = ex.Message.Substring(ex.Message.IndexOf(marker, StringComparison.Ordinal) + marker.Length, 8);
        return (SdoAbortCode)Convert.ToUInt32(hex, 16);
    }

    private static void SendNmt(ICanBus master, NmtCommand command, byte nodeId)
        => master.Transmit(CanFrame.Classic(unchecked((int)CanOpenCobId.NmtCommand),
            new[] { (byte)command, nodeId }, isExtendedFrame: false));

    private static async Task WaitForStateAsync(ICanOpenNode node, NmtState state)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (node.State != state)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Node 0x{node.NodeId:X2} did not reach {state}; it is in {node.State}.");
            await Task.Delay(5);
        }
    }

    [Fact]
    public void Without_Srdos_No_Safety_Object_Exists()
    {
        var session = VirtualAdapterFixture.NewSession("co-safety-od");
        using var bus = Open(session, 1);
        using var node = CanOpen.OpenNode(bus, Device);
        node.ObjectDictionary.ContainsIndex(0x1300).Should().BeFalse();
        node.ObjectDictionary.ContainsIndex(0x1301).Should().BeFalse();
        node.ObjectDictionary.ContainsIndex(0x13FE).Should().BeFalse();
        node.ObjectDictionary.ContainsIndex(0x13FF).Should().BeFalse();
    }

    [Fact]
    public void Defaults_Of_Section_8_4_2_2()
    {
        var session = VirtualAdapterFixture.NewSession("co-safety-od");
        using var bus = Open(session, 1);
        using var node = OpenDevice(bus, srdoCount: 2);
        var od = node.ObjectDictionary;
        od.ReadUnsigned(0x1300, 0).Should().Be(0u);
        od.ReadUnsigned(0x1301, 0).Should().Be(6u);
        od.ReadUnsigned(0x1301, 1).Should().Be(0u, "not valid until configured");
        od.ReadUnsigned(0x1301, 2).Should().Be(25u);
        od.ReadUnsigned(0x1301, 3).Should().Be(20u);
        od.ReadUnsigned(0x1301, 4).Should().Be(254u);
        od.ReadUnsigned(0x1301, 5).Should().Be(0x0FFu + 2 * Device);
        od.ReadUnsigned(0x1301, 6).Should().Be(0x100u + 2 * Device);
        od.ReadUnsigned(0x1302, 5).Should().Be(0u, "only the first SRDO has pre-defined COB-IDs");
        od.ReadUnsigned(0x1302, 6).Should().Be(0u);
        od.ReadUnsigned(0x1381, 0).Should().Be(0u);
        od.TryGet(0x1381, 16, out _).Should().BeTrue();
        od.TryGet(0x1381, 17, out _).Should().BeFalse("8 objects, plain and inverted");
        od.ReadUnsigned(0x13FE, 0).Should().Be(0u);
        od.ReadUnsigned(0x13FF, 0).Should().Be(2u);
        od.ReadUnsigned(0x13FF, 2).Should().Be(0u);
        od.ContainsIndex(0x1303).Should().BeFalse();
        od.ContainsIndex(0x1383).Should().BeFalse();
    }

    [Fact]
    public void A_Node_Above_64_Has_No_Default_CobIds()
    {
        var session = VirtualAdapterFixture.NewSession("co-safety-od");
        using var bus = Open(session, 1);
        using var node = OpenDevice(bus, srdoCount: 1, nodeId: 0x7F);
        node.ObjectDictionary.ReadUnsigned(0x1301, 5).Should().Be(0u);
        node.ObjectDictionary.ReadUnsigned(0x1301, 6).Should().Be(0u);
    }

    [Theory]
    [InlineData(0x1301, 1, 3u, SdoAbortCode.ValueRangeExceeded)]
    [InlineData(0x1301, 2, 0u, SdoAbortCode.ValueRangeExceeded)]
    [InlineData(0x1301, 3, 0u, SdoAbortCode.ValueRangeExceeded)]
    [InlineData(0x1301, 4, 254u, SdoAbortCode.ValueRangeExceeded)]
    [InlineData(0x1301, 5, 0x102u, SdoAbortCode.ValueRangeExceeded)]   // even
    [InlineData(0x1301, 5, 0x0FFu, SdoAbortCode.ValueRangeExceeded)]   // below the range
    [InlineData(0x1301, 6, 0x101u, SdoAbortCode.ValueRangeExceeded)]   // odd
    [InlineData(0x1301, 6, 0x182u, SdoAbortCode.ValueRangeExceeded)]   // above the range
    [InlineData(0x1301, 6, 0x104u, SdoAbortCode.ValueRangeExceeded)]   // not COB-ID 1 + 1 (default 0x123)
    [InlineData(0x1301, 5, 0x8000_0121u, SdoAbortCode.ValueRangeExceeded)] // bits 31..11 reserved (Figure 7)
    public void Communication_Record_Value_Rules(ushort index, byte sub, uint value, SdoAbortCode expected)
    {
        var session = VirtualAdapterFixture.NewSession("co-safety-od");
        using var bus = Open(session, 1);
        using var node = OpenDevice(bus);
        Rejected(() => node.ObjectDictionary.WriteUnsigned(index, sub, value)).Should().Be(expected);
    }

    [Fact]
    public void CobIds_Cannot_Change_While_The_Srdo_Exists()
    {
        var session = VirtualAdapterFixture.NewSession("co-safety-od");
        using var bus = Open(session, 1);
        using var node = OpenDevice(bus);
        var od = node.ObjectDictionary;
        od.WriteUnsigned(0x1301, 1, 1);
        Rejected(() => od.WriteUnsigned(0x1301, 5, 0x103)).Should().Be(SdoAbortCode.ValueRangeExceeded);
        Rejected(() => od.WriteUnsigned(0x1301, 6, 0x104)).Should().Be(SdoAbortCode.ValueRangeExceeded);
        od.WriteUnsigned(0x1301, 1, 0);
        od.WriteUnsigned(0x1301, 5, 0x103);
        od.WriteUnsigned(0x1301, 6, 0x104);
        od.ReadUnsigned(0x1301, 5).Should().Be(0x103u);
    }

    [Fact]
    public void Two_Srdos_Cannot_Share_A_CobId()
    {
        var session = VirtualAdapterFixture.NewSession("co-safety-od");
        using var bus = Open(session, 1);
        using var node = OpenDevice(bus);
        var od = node.ObjectDictionary;
        // SRDO 2 is given SRDO 1's ids while SRDO 1 does not exist yet (the ids may then be
        // written); creating SRDO 1 and then SRDO 2 must be refused on the shared ids.
        od.WriteUnsigned(0x1302, 5, 0x0FFu + 2 * Device);
        od.WriteUnsigned(0x1302, 6, 0x100u + 2 * Device);
        od.WriteUnsigned(0x1301, 1, 1);
        Rejected(() => od.WriteUnsigned(0x1302, 1, 2)).Should().Be(SdoAbortCode.ValueRangeExceeded,
            "SRDO 1 exists on these ids; one CAN-ID carries one communication object");
    }

    [Fact]
    public void A_CobId_Of_Another_Existing_Srdo_Is_Refused_On_The_Write()
    {
        var session = VirtualAdapterFixture.NewSession("co-safety-od");
        using var bus = Open(session, 1);
        using var node = OpenDevice(bus);
        var od = node.ObjectDictionary;
        od.WriteUnsigned(0x1301, 1, 1);
        // 0x121 is COB-ID 1 of the existing SRDO 1: SRDO 2 may not take it (0609 0030h).
        Rejected(() => od.WriteUnsigned(0x1302, 5, 0x0FFu + 2 * Device)).Should().Be(SdoAbortCode.ValueRangeExceeded);
        od.WriteUnsigned(0x1302, 5, 0x0FFu + 2 * Device + 2);
        od.ReadUnsigned(0x1302, 5).Should().Be(0x0FFu + 2 * Device + 2, "a free id is accepted");
    }

    [Fact]
    public void Without_Srdos_The_Application_Declares_1301h_Itself()
    {
        var session = VirtualAdapterFixture.NewSession("co-safety-od");
        using var bus = Open(session, 1);
        using var node = CanOpen.OpenNode(bus, Device);
        var od = node.ObjectDictionary;
        od.AddU8(0x1301, 0x01, 0);
        od.AddU32(0x13FE, 0x00, 0);
        od.WriteUnsigned(0x1301, 1, 7);
        od.ReadUnsigned(0x1301, 1).Should().Be(7u, "an application object, not validated as an SRDO record");
    }

    [Fact]
    public void With_Two_Srdos_Only_Their_Records_Are_Reserved()
    {
        var session = VirtualAdapterFixture.NewSession("co-safety-od");
        using var bus = Open(session, 1);
        using var node = OpenDevice(bus, srdoCount: 2);
        var od = node.ObjectDictionary;
        Assert.Throws<InvalidOperationException>(() => od.AddU8(0x1301, 0x01, 0));
        Assert.Throws<InvalidOperationException>(() => od.AddU8(0x1382, 0x01, 0));
        Assert.Throws<InvalidOperationException>(() => od.AddU8(0x13FE, 0x00, 0));
        od.AddU8(0x1303, 0x01, 0);
        od.AddU8(0x1383, 0x01, 0);
        od.WriteUnsigned(0x1303, 1, 5);
        od.ReadUnsigned(0x1303, 1).Should().Be(5u);
    }

    [Fact]
    public async Task Writes_To_The_Safety_Entries_Are_Refused_In_Operational_Except_1300h()
    {
        var session = VirtualAdapterFixture.NewSession("co-safety-od");
        using var bus = Open(session, 1);
        using var master = Open(session, 2);
        using var node = OpenDevice(bus);
        var od = node.ObjectDictionary;
        SendNmt(master, NmtCommand.Start, Device);
        await WaitForStateAsync(node, NmtState.Operational);
        Rejected(() => od.WriteUnsigned(0x1301, 2, 30)).Should().Be(SdoAbortCode.DataCannotBeTransferredDeviceState);
        Rejected(() => od.WriteUnsigned(0x13FE, 0, 0xA5)).Should().Be(SdoAbortCode.DataCannotBeTransferredDeviceState);
        od.ReadUnsigned(0x1301, 2).Should().Be(25u, "reading stays allowed");
        od.WriteUnsigned(0x1300, 0, 1);
        od.ReadUnsigned(0x1300, 0).Should().Be(1u);
    }

    [Fact]
    public async Task A_Stored_Valid_Configuration_Survives_An_Nmt_Reset()
    {
        var session = VirtualAdapterFixture.NewSession("co-safety-od");
        using var bus = Open(session, 1);
        using var master = Open(session, 2);
        using var node = OpenDevice(bus);
        var od = node.ObjectDictionary;
        od.AddU8(0x2000, 0x00, 0);
        od.WriteUnsigned(0x1381, 1, 0x2000_0008);
        od.WriteUnsigned(0x1381, 2, 0x2000_0008);
        od.WriteUnsigned(0x1381, 0, 2);
        od.WriteUnsigned(0x1301, 1, 1);
        od.WriteUnsigned(0x13FF, 1, 0x1234);
        od.WriteUnsigned(0x13FE, 0, 0xA5);
        node.StoreParameters();

        od.WriteUnsigned(0x13FE, 0, 0);
        od.WriteUnsigned(0x1301, 2, 40);
        od.WriteUnsigned(0x13FF, 1, 0x4321);

        // Operational first, so that the wait below ends on the PreOperational the reset enters
        // after it has restored the dictionary, not on the state the node was in before it.
        SendNmt(master, NmtCommand.Start, Device);
        await WaitForStateAsync(node, NmtState.Operational);
        SendNmt(master, NmtCommand.ResetCommunication, Device);
        await WaitForStateAsync(node, NmtState.PreOperational);

        od.ReadUnsigned(0x1301, 2).Should().Be(25u, "the reset restored the stored values");
        od.ReadUnsigned(0x13FF, 1).Should().Be(0x1234u);
        od.ReadUnsigned(0x13FE, 0).Should().Be(0xA5u, "restoring is not a write to a safety-relevant parameter");
    }

    /// <summary>A valid configuration on the device, stored with "save", then changed.</summary>
    private static void StoreAValidConfigurationThenChangeIt(ObjectDictionary od, ICanOpenNode node)
    {
        od.AddU8(0x2000, 0x00, 0);
        od.WriteUnsigned(0x1381, 1, 0x2000_0008);
        od.WriteUnsigned(0x1381, 2, 0x2000_0008);
        od.WriteUnsigned(0x1381, 0, 2);
        od.WriteUnsigned(0x1301, 1, 1);
        od.WriteUnsigned(0x13FF, 1, 0x1234);
        od.WriteUnsigned(0x13FE, 0, 0xA5);
        node.StoreParameters();

        od.WriteUnsigned(0x1301, 2, 40);
        od.WriteUnsigned(0x13FF, 1, 0x4321);
        od.WriteUnsigned(0x13FE, 0, 0);
    }

    [Fact]
    public async Task Reset_Node_Restores_The_Stored_Safety_Objects()
    {
        var session = VirtualAdapterFixture.NewSession("co-safety-od");
        using var bus = Open(session, 1);
        using var master = Open(session, 2);
        using var node = OpenDevice(bus);
        var od = node.ObjectDictionary;
        StoreAValidConfigurationThenChangeIt(od, node);

        // Operational first, so that the wait below ends on the PreOperational the reset enters.
        SendNmt(master, NmtCommand.Start, Device);
        await WaitForStateAsync(node, NmtState.Operational);
        SendNmt(master, NmtCommand.ResetNode, Device);
        await WaitForStateAsync(node, NmtState.PreOperational);

        od.ReadUnsigned(0x1301, 1).Should().Be(1u);
        od.ReadUnsigned(0x1301, 2).Should().Be(25u, "Reset Node restored the stored values");
        od.ReadUnsigned(0x13FF, 1).Should().Be(0x1234u);
        od.ReadUnsigned(0x13FE, 0).Should().Be(0xA5u);
    }

    [Fact]
    public async Task Load_Then_Reset_Brings_Back_The_Safety_Defaults()
    {
        var session = VirtualAdapterFixture.NewSession("co-safety-od");
        using var bus = Open(session, 1);
        using var master = Open(session, 2);
        using var node = OpenDevice(bus);
        var od = node.ObjectDictionary;
        StoreAValidConfigurationThenChangeIt(od, node);
        node.RestoreDefaultParameters();   // 1011h:01 "load": the defaults become valid with the reset

        SendNmt(master, NmtCommand.Start, Device);
        await WaitForStateAsync(node, NmtState.Operational);
        SendNmt(master, NmtCommand.ResetCommunication, Device);
        await WaitForStateAsync(node, NmtState.PreOperational);

        od.ReadUnsigned(0x1301, 1).Should().Be(0u, "the factory default: no SRDO");
        od.ReadUnsigned(0x1301, 2).Should().Be(25u);
        od.ReadUnsigned(0x1381, 0).Should().Be(0u);
        od.ReadUnsigned(0x13FF, 1).Should().Be(0u);
        od.ReadUnsigned(0x13FE, 0).Should().Be(0u);
    }

    [Fact]
    public void A_Pdo_Cannot_Take_An_Srdo_CobId()
    {
        // CiA 301 Table 40 restricts 101h–180h; the PDO validator already refuses them. Pinned
        // here because it is the mirror image of Two_Srdos_Cannot_Share_A_CobId.
        var session = VirtualAdapterFixture.NewSession("co-safety-od");
        using var bus = Open(session, 1);
        using var node = OpenDevice(bus);
        Rejected(() => node.ObjectDictionary.WriteUnsigned(0x1400, 1, 0x123)).Should().Be(SdoAbortCode.ValueRangeExceeded);
        Rejected(() => node.ObjectDictionary.WriteUnsigned(0x1005, 0, 0x124)).Should().Be(SdoAbortCode.ValueRangeExceeded);
    }

    [Fact]
    public void Mapping_Rules()
    {
        var session = VirtualAdapterFixture.NewSession("co-safety-od");
        using var bus = Open(session, 1);
        using var node = OpenDevice(bus);
        var od = node.ObjectDictionary;
        od.AddU16(0x2000, 0x00, 0);
        od.AddU8(0x2001, 0x00, 0);
        od.AddU32(0x2002, 0x00, 0, OdAccess.ReadWrite, pdoMappable: false);
        // Pairs: odd plain, even inverted, equal.
        od.WriteUnsigned(0x1381, 1, 0x2000_0010);
        Rejected(() => od.WriteUnsigned(0x1381, 2, 0x2001_0008)).Should().Be(SdoAbortCode.ObjectCannotBeMapped, "sub 2 must repeat sub 1");
        od.WriteUnsigned(0x1381, 2, 0x2000_0010);
        Rejected(() => od.WriteUnsigned(0x1381, 0, 1)).Should().Be(SdoAbortCode.ValueRangeExceeded, "odd count");
        Rejected(() => od.WriteUnsigned(0x1381, 0, 4)).Should().Be(SdoAbortCode.ObjectDoesNotExist, "slots 3 and 4 are empty");
        od.WriteUnsigned(0x1381, 0, 2);
        // Not mappable, non-existent, wrong width, comm-profile area, dummy.
        od.WriteUnsigned(0x1381, 0, 0);
        Rejected(() => od.WriteUnsigned(0x1381, 3, 0x2002_0020)).Should().Be(SdoAbortCode.ObjectCannotBeMapped);
        Rejected(() => od.WriteUnsigned(0x1381, 3, 0x2FFF_0008)).Should().Be(SdoAbortCode.ObjectDoesNotExist);
        Rejected(() => od.WriteUnsigned(0x1381, 3, 0x2001_000C)).Should().Be(SdoAbortCode.DataTypeLengthMismatch);
        Rejected(() => od.WriteUnsigned(0x1381, 3, 0x1017_0010)).Should().Be(SdoAbortCode.ObjectCannotBeMapped);
        Rejected(() => od.WriteUnsigned(0x1381, 3, 0x0005_0008)).Should().Be(SdoAbortCode.ObjectCannotBeMapped, "no dummies in safety data");
        // Length: 8 bytes fit, a ninth does not.
        for (byte s = 1; s <= 16; s += 2) { od.WriteUnsigned(0x1381, s, 0x2001_0008); od.WriteUnsigned(0x1381, (byte)(s + 1), 0x2001_0008); }
        od.WriteUnsigned(0x1381, 0, 16);
        od.WriteUnsigned(0x1381, 0, 0);
        od.WriteUnsigned(0x1381, 1, 0x2000_0010); od.WriteUnsigned(0x1381, 2, 0x2000_0010);
        Rejected(() => od.WriteUnsigned(0x1381, 0, 16)).Should().Be(SdoAbortCode.PdoMappingLengthExceeded, "2 + 7 bytes");
        // Not while the SRDO exists.
        od.WriteUnsigned(0x1381, 0, 2);
        od.WriteUnsigned(0x1301, 1, 1);
        Rejected(() => od.WriteUnsigned(0x1381, 0, 0)).Should().Be(SdoAbortCode.UnsupportedAccess);
        Rejected(() => od.WriteUnsigned(0x1381, 1, 0x2001_0008)).Should().Be(SdoAbortCode.UnsupportedAccess);
    }

    [Fact]
    public void Direction_Checks_The_Mapped_Objects_Access()
    {
        var session = VirtualAdapterFixture.NewSession("co-safety-od");
        using var bus = Open(session, 1);
        using var node = OpenDevice(bus);
        var od = node.ObjectDictionary;
        od.AddU8(0x2000, 0x00, 0, OdAccess.ReadOnly);
        od.WriteUnsigned(0x1381, 1, 0x2000_0008); od.WriteUnsigned(0x1381, 2, 0x2000_0008); od.WriteUnsigned(0x1381, 0, 2);
        od.WriteUnsigned(0x1301, 1, 1);
        od.WriteUnsigned(0x1301, 1, 0);
        Rejected(() => od.WriteUnsigned(0x1301, 1, 2)).Should().Be(SdoAbortCode.ObjectCannotBeMapped, "a consumer writes the object; it is read-only");
    }

    /// <summary>"Two following COB-IDs" is checked again at the creation: COB-ID 1 can be moved
    /// after COB-ID 2 was accepted against the old one.</summary>
    [Fact]
    public void Creating_An_Srdo_Needs_Consecutive_CobIds()
    {
        var session = VirtualAdapterFixture.NewSession("co-safety-od");
        using var bus = Open(session, 1);
        using var node = OpenDevice(bus);
        var od = node.ObjectDictionary;
        od.ReadUnsigned(0x1301, 6).Should().Be(0x122u, "the pre-defined COB-ID 2 of node 11h");
        od.WriteUnsigned(0x1301, 5, 0x111);
        Rejected(() => od.WriteUnsigned(0x1301, 1, 1)).Should().Be(SdoAbortCode.ValueRangeExceeded, "122h does not follow 111h");
        od.WriteUnsigned(0x1301, 6, 0x112);
        od.WriteUnsigned(0x1301, 1, 1);
    }

    /// <summary>The count checks every pair again: an inverted slot written while its plain
    /// slot was still empty is compared when the mapping is enabled.</summary>
    [Fact]
    public void The_Count_Checks_That_Each_Inverted_Slot_Repeats_Its_Plain_Slot()
    {
        var session = VirtualAdapterFixture.NewSession("co-safety-od");
        using var bus = Open(session, 1);
        using var node = OpenDevice(bus);
        var od = node.ObjectDictionary;
        od.AddU16(0x2000, 0x00, 0);
        od.AddU8(0x2001, 0x00, 0);
        od.WriteUnsigned(0x1381, 2, 0x2001_0008);
        od.WriteUnsigned(0x1381, 1, 0x2000_0010);
        Rejected(() => od.WriteUnsigned(0x1381, 0, 2)).Should().Be(SdoAbortCode.ObjectCannotBeMapped, "sub 2 does not repeat sub 1");
        od.WriteUnsigned(0x1381, 2, 0x2000_0010);
        od.WriteUnsigned(0x1381, 0, 2);
    }

    [Fact]
    public void Creating_An_Srdo_Needs_Both_CobIds()
    {
        var session = VirtualAdapterFixture.NewSession("co-safety-od");
        using var bus = Open(session, 1);
        using var node = OpenDevice(bus);
        Rejected(() => node.ObjectDictionary.WriteUnsigned(0x1302, 1, 1)).Should().Be(SdoAbortCode.ValueRangeExceeded, "COB-IDs of SRDO 2 are 0 (disabled)");
    }

    [Fact]
    public void Checksum_Write_Clears_Configuration_Valid()
    {
        var session = VirtualAdapterFixture.NewSession("co-safety-od");
        using var bus = Open(session, 1);
        using var node = OpenDevice(bus);
        var od = node.ObjectDictionary;
        od.WriteUnsigned(0x13FE, 0, 0xA5);
        od.ReadUnsigned(0x13FE, 0).Should().Be(0xA5u);
        od.WriteUnsigned(0x13FF, 1, 0x1234);
        od.ReadUnsigned(0x13FE, 0).Should().Be(0u, "§8.4.2.2: automatically 0 after a write to a safety-relevant parameter");
        od.WriteUnsigned(0x13FE, 0, 0xA5);
        od.WriteUnsigned(0x1301, 2, 30);
        od.ReadUnsigned(0x13FE, 0).Should().Be(0u);
        od.WriteUnsigned(0x13FE, 0, 0xA5);
        od.WriteUnsigned(0x1381, 1, 0);
        od.ReadUnsigned(0x13FE, 0).Should().Be(0u);
        od.WriteUnsigned(0x13FE, 0, 0xA5);
        od.WriteUnsigned(0x1300, 0, 1);
        od.ReadUnsigned(0x13FE, 0).Should().Be(0xA5u, "1300h is not CRC-covered");
        Rejected(() => od.WriteUnsigned(0x13FF, 0, 3)).Should().Be(SdoAbortCode.AttemptWriteReadOnly);
        Rejected(() => od.WriteUnsigned(0x1300, 0, 2)).Should().Be(SdoAbortCode.ValueRangeExceeded);
    }

    [Fact]
    public void Read_Only_On_The_Bus_Without_WritableCommunicationParameters()
    {
        var session = VirtualAdapterFixture.NewSession("co-safety-od");
        using var bus = Open(session, 1);
        using var node = CanOpen.OpenNode(bus, Device, new CanOpenNodeOptions { SrdoCount = 1 });
        node.ObjectDictionary.TryGet(0x1301, 1, out var entry).Should().BeTrue();
        entry.Access.Should().Be(OdAccess.ReadOnly);
        node.ObjectDictionary.TryGet(0x13FE, 0, out entry);
        entry.Access.Should().Be(OdAccess.ReadOnly);
    }
}
