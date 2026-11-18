using System;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Application;
using CanKit.Pro.CANopen.Lss;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Pdo;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

// Default PDOs: what an application sets in Attach with SetDefaultTpdo / SetDefaultRpdo, and what
// the PDO records and the engine hold once the node has opened.
public partial class CanOpenApplicationSeamTests
{
    private const byte Node22 = 0x22;
    private const uint Tpdo1Of22 = 0x1A2;
    private const uint Rpdo1Of22 = 0x222;
    private const uint BootUpOf22 = 0x722;

    /// <summary>The claimant of <see cref="Entries"/>, asking for <paramref name="pdoCount"/> PDOs and
    /// calling <paramref name="set"/> in <c>Attach</c> after it has declared its entries.</summary>
    private static RecordingApplication Defaults(Action<ICanOpenApplicationHost> set, int pdoCount = 1, string name = "defaults")
    {
        var app = Entries(name);
        var declare = app.OnAttach!;
        app.OnAttach = host =>
        {
            declare(host);
            set(host);
        };
        app.Requirements = new CanOpenApplicationRequirements { PdoCount = pdoCount };
        return app;
    }

    private static PdoMapping Map(ushort index, byte subindex) => new PdoMapping().Add(index, subindex, 8);

    /// <summary>NMT Start to every node: the rig's node is not always <see cref="Slave"/> here.</summary>
    private static async Task StartAnyAsync(EntryRig rig)
    {
        rig.Wire.Transmit(0x000, new[] { (byte)NmtCommand.Start, (byte)0 });
        await WaitUntilAsync(() => rig.Node.State == NmtState.Operational, "Operational");
    }

    [Fact]
    public async Task A_Default_Tpdo_Holds_The_Profile_Values_And_Goes_Out_On_Request()
    {
        var app = Defaults(host => host.SetDefaultTpdo(1, Map(0x6000, 0x01)));
        app.WhenWritten = entries => app.Host!.RequestTransmission(entries);
        using var rig = new EntryRig(With(app), nodeId: Node22);

        rig.Od.ReadUnsigned(0x1800, 0x01).Should().Be(0x000001A2u, "the pre-defined COB-ID of TPDO 1, the PDO valid");
        rig.Od.ReadUnsigned(0x1800, 0x02).Should().Be(255u, "the profile-specific event-driven type, not the manufacturer's 254");
        rig.Od.ReadUnsigned(0x1A00, 0x00).Should().Be(1u);
        rig.Od.ReadUnsigned(0x1A00, 0x01).Should().Be(0x60000108u);

        rig.Node.ConfigureTpdo(2, Map(0x2101, 0x00));
        await StartAnyAsync(rig);
        rig.Od.WriteUnsigned(0x6000, 0x01, 7);
        await rig.Wire.WaitForCountAsync(Tpdo1Of22, 1);
        await rig.WitnessAsync();

        AssertSingleFrame(rig.Wire.Payloads(Tpdo1Of22), 7);
    }

    [Fact]
    public async Task A_Disabled_Default_Tpdo_Is_Not_Sent()
    {
        var app = Defaults(host => host.SetDefaultTpdo(1, Map(0x6000, 0x01), enabled: false));
        app.WhenWritten = entries => app.Host!.RequestTransmission(entries);
        using var rig = new EntryRig(With(app), nodeId: Node22);

        rig.Od.ReadUnsigned(0x1800, 0x01).Should().Be(0x800001A2u, "bit 31 set, the CAN-ID the pre-defined one");
        rig.Od.ReadUnsigned(0x1A00, 0x01).Should().Be(0x60000108u, "the mapping is in place for the master to enable");

        rig.Node.ConfigureTpdo(2, Map(0x2101, 0x00));
        await StartAnyAsync(rig);
        rig.Od.WriteUnsigned(0x6000, 0x01, 7);
        await WaitUntilAsync(() => app.Log.Contains("written 6000:01"), "the claimant to be told");
        await rig.WitnessAsync();

        rig.Wire.Count(Tpdo1Of22).Should().Be(0, "a PDO that does not exist is not sent");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_Default_Pdo_Above_4_Has_The_Mapping_And_An_Invalid_Cob_Id(bool enabled)
    {
        using var bus = Open(NewSession(), 1);
        var app = Defaults(host =>
        {
            host.SetDefaultTpdo(5, Map(0x6000, 0x01), enabled);
            host.SetDefaultRpdo(5, Map(0x6000, 0x02));
        }, pdoCount: 5);

        using var node = OpenNode(bus, With(app).With(pdoCount: 5), nodeId: Node22);

        var od = node.ObjectDictionary;
        // The invalid COB-ID the node writes for an unassigned TPDO: bit 31, bit 30 (no RTR), no CAN-ID.
        od.ReadUnsigned(0x1804, 0x01).Should().Be(0xC0000000u,
            "CiA 301 has no default COB-ID above PDO 4, so the PDO stays invalid whatever enabled says");
        od.ReadUnsigned(0x1804, 0x02).Should().Be(255u);
        od.ReadUnsigned(0x1A04, 0x00).Should().Be(1u);
        od.ReadUnsigned(0x1A04, 0x01).Should().Be(0x60000108u);
        od.ReadUnsigned(0x1404, 0x01).Should().Be(0x80000000u);
        od.ReadUnsigned(0x1404, 0x02).Should().Be(255u);
        od.ReadUnsigned(0x1604, 0x00).Should().Be(1u);
        od.ReadUnsigned(0x1604, 0x01).Should().Be(0x60000208u);
    }

    [Fact]
    public async Task A_Default_Rpdo_Holds_The_Profile_Values_And_Is_Received()
    {
        // TPDO 1 and RPDO 1 of one application: the same number in the two directions is no clash.
        var app = Defaults(host =>
        {
            host.SetDefaultTpdo(1, Map(0x6000, 0x01));
            host.SetDefaultRpdo(1, Map(0x6000, 0x02));
        });
        using var rig = new EntryRig(With(app), nodeId: Node22);

        rig.Od.ReadUnsigned(0x1400, 0x01).Should().Be(0x00000222u, "the pre-defined COB-ID of RPDO 1, the PDO valid");
        rig.Od.ReadUnsigned(0x1400, 0x02).Should().Be(255u, "CiA 401-1 §6.2.5 gives the RPDOs 255 as well");
        rig.Od.ReadUnsigned(0x1600, 0x00).Should().Be(1u);
        rig.Od.ReadUnsigned(0x1600, 0x01).Should().Be(0x60000208u);

        await StartAnyAsync(rig);
        rig.Wire.Transmit(Rpdo1Of22, new byte[] { 9 });

        await WaitUntilAsync(() => rig.Od.ReadUnsigned(0x6000, 0x02) == 9, "the RPDO to land in 6000h:02");
    }

    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    public void A_Pdo_Number_Outside_The_Requirement_Is_Refused(bool tpdo, int number)
    {
        // The node has four PDOs, the application asks for one: the requirement is the bound.
        var app = Defaults(host =>
        {
            if (tpdo) host.SetDefaultTpdo(number, Map(0x6000, 0x01));
            else host.SetDefaultRpdo(number, Map(0x6000, 0x02));
        });

        var failure = OpenThrows<ArgumentOutOfRangeException>(With(app));

        failure.ParamName.Should().Be("pdoNumber");
        failure.Message.Should().Contain("CanOpenApplicationRequirements.PdoCount");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_Mapped_Entry_Outside_The_Claims_Is_Refused(bool tpdo)
    {
        // 2100h exists and is mappable both ways: only the claim stands in the way.
        var app = Defaults(host =>
        {
            if (tpdo) host.SetDefaultTpdo(1, Map(0x2100, 0x00));
            else host.SetDefaultRpdo(1, Map(0x2100, 0x00));
        }, name: "mapper");

        var failure = OpenThrows<ArgumentException>(With(app));

        failure.GetType().Should().Be<ArgumentException>();
        failure.ParamName.Should().Be("mapping");
        failure.Message.Should().Contain("mapper").And.Contain("2100");
    }

    [Fact]
    public void A_Described_Pdo_Keeps_What_The_Description_Says()
    {
        using var bus = Open(NewSession(), 1);
        var description = CanOpenDeviceDescription.Load(CanOpenDeviceDescriptionTests.Fixture("device.eds"));
        var app = Defaults(host =>
        {
            host.SetDefaultTpdo(1, Map(0x6000, 0x01));
            host.SetDefaultRpdo(1, Map(0x6000, 0x02));
        });

        using var node = OpenNode(bus, With(app), description: description);

        var od = node.ObjectDictionary;
        od.ReadUnsigned(0x1800, 0x02).Should().Be(254u, "the file wins: device.eds gives TPDO 1 type 254");
        od.ReadUnsigned(0x1A00, 0x00).Should().Be(1u);
        od.ReadUnsigned(0x1A00, 0x01).Should().Be(0x20000010u, "device.eds maps 2000h into TPDO 1");
        od.ReadUnsigned(0x1600, 0x01).Should().Be(0x20010008u, "device.eds maps 2001h into RPDO 1");
    }

    [Fact]
    public void Two_Applications_Setting_The_Same_Pdo_Fail_The_Open()
    {
        var alpha = Defaults(host => host.SetDefaultTpdo(1, Map(0x6000, 0x01)), name: "alpha");
        var beta = new RecordingApplication("beta", new CanOpenIndexRange(0x6800, 0x6FFF))
        {
            Requirements = new CanOpenApplicationRequirements { PdoCount = 1 },
            OnAttach = host =>
            {
                host.ObjectDictionary.AddU8(0x6800, 0x01, 0);
                host.SetDefaultTpdo(1, Map(0x6800, 0x01));
            },
        };

        var failure = OptionsRefused(With(alpha, beta));

        failure.Message.Should().Contain("alpha").And.Contain("beta").And.Contain("TPDO 1");
    }

    [Fact]
    public void Two_Applications_Setting_The_Same_Rpdo_Fail_The_Open()
    {
        var alpha = Defaults(host => host.SetDefaultRpdo(1, Map(0x6000, 0x02)), name: "alpha");
        var beta = new RecordingApplication("beta", new CanOpenIndexRange(0x6800, 0x6FFF))
        {
            Requirements = new CanOpenApplicationRequirements { PdoCount = 1 },
            OnAttach = host =>
            {
                host.ObjectDictionary.AddU8(0x6800, 0x01, 0);
                host.SetDefaultRpdo(1, Map(0x6800, 0x01));
            },
        };

        var failure = OptionsRefused(With(alpha, beta));

        failure.Message.Should().Contain("alpha").And.Contain("beta").And.Contain("RPDO 1");
    }

    [Fact]
    public void One_Application_Setting_The_Same_Pdo_Twice_Fails_The_Open()
    {
        var app = Defaults(host =>
        {
            host.SetDefaultTpdo(1, Map(0x6000, 0x01));
            host.SetDefaultTpdo(1, Map(0x6000, 0x02));
        }, name: "solo");

        var failure = OptionsRefused(With(app));

        failure.Message.Should().Contain("TPDO 1").And.Contain("twice");
        failure.Message.Split("'solo'").Length.Should().Be(2, "it names the application once, not as two setters");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_Null_Mapping_Is_Refused(bool tpdo)
    {
        var app = Defaults(host =>
        {
            if (tpdo) host.SetDefaultTpdo(1, null!);
            else host.SetDefaultRpdo(1, null!);
        });

        OpenThrows<ArgumentNullException>(With(app)).ParamName.Should().Be("mapping");
    }

    [Fact]
    public void A_Mapping_The_Records_Refuse_Fails_The_Open_With_One_Clean_Message()
    {
        // 6000h:03 is the application's, but not mappable: the mapping record refuses it, as over SDO.
        var app = Defaults(host =>
        {
            host.ObjectDictionary.AddU8(0x6000, 0x03, 0, pdoMappable: false);
            host.SetDefaultTpdo(1, Map(0x6000, 0x03));
        }, name: "unmappable");

        var failure = OptionsRefused(With(app));

        failure.Message.Should().Contain("unmappable").And.Contain("TPDO 1").And.Contain("1A00:01");
        failure.InnerException.Should().BeAssignableTo<ArgumentException>("the record's refusal is kept as the cause");
        // The suffix ArgumentException appends for a parameter, in this runtime's wording: only the
        // one for "options" belongs in the message, not those of the exceptions it wraps.
        failure.Message.Should().NotContain(new ArgumentException(string.Empty, "value").Message.Trim());
    }

    [Fact]
    public void A_Default_Pdo_The_Description_Leaves_Out_Is_Skipped()
    {
        // device.eds declares TPDO 1 and no TPDO 2: the file describes a device without TPDO 2.
        using var bus = Open(NewSession(), 1);
        var description = CanOpenDeviceDescription.Load(CanOpenDeviceDescriptionTests.Fixture("device.eds"));
        var app = Defaults(host => host.SetDefaultTpdo(2, Map(0x6000, 0x01)), pdoCount: 2);

        using var node = OpenNode(bus, With(app), description: description);

        node.ObjectDictionary.ContainsIndex(0x1801).Should().BeFalse("the file wins: there is no TPDO 2");
        node.ObjectDictionary.ContainsIndex(0x1A01).Should().BeFalse();
    }

    [Fact]
    public async Task A_Default_Rpdo_Of_A_Slave_Without_A_Node_Id_Does_Not_Take_The_Sdo_Response_Of_Node_127()
    {
        // Without a node-id RPDO 4's COB-ID is 5FFh, the CAN-ID node 127 answers an SDO request on.
        // The RPDO cannot act before the node has a node-id, so the frame goes where it went before.
        const byte Node127 = 0x7F;
        var app = Defaults(host => host.SetDefaultRpdo(4, Map(0x6000, 0x02)), pdoCount: 4);
        using var rig = new EntryRig(With(app).WithLss(new LssSlaveOptions()), nodeId: CanOpenCobId.UnconfiguredNodeId);
        Settle(rig.Node);

        var upload = rig.Node.SdoUploadAsync(Node127, 0x1000, 0x00);
        await rig.Wire.WaitForCountAsync(0x600u + Node127, 1);
        rig.Wire.Transmit(0x580u + Node127, new byte[] { 0x43, 0x00, 0x10, 0x00, 0x91, 0x01, 0x0F, 0x00 });

        (await upload.WithTimeoutAsync(ShortTimeout)).Should().Equal(0x91, 0x01, 0x0F, 0x00);
    }

    [Fact]
    public async Task Reset_Communication_Restores_The_Default_Tpdo()
    {
        var app = Defaults(host => host.SetDefaultTpdo(1, Map(0x6000, 0x01)));
        using var rig = new EntryRig(With(app), nodeId: Node22);
        await rig.Wire.WaitForCountAsync(BootUpOf22, 1);
        rig.Od.WriteUnsigned(0x1800, 0x02, 1);
        rig.Od.ReadUnsigned(0x1800, 0x02).Should().Be(1u, "precondition: the local write took");

        rig.Wire.Transmit(0x000, new[] { (byte)NmtCommand.ResetCommunication, Node22 });
        await rig.Wire.WaitForCountAsync(BootUpOf22, 2);
        Settle(rig.Node);

        rig.Od.ReadUnsigned(0x1800, 0x02).Should().Be(255u, "the default is part of the power-on values");
    }

    [Fact]
    public async Task The_Default_Cob_Id_Follows_A_Node_Id_Assigned_By_Lss_At_Reset_Communication()
    {
        // A configured slave keeps its active node-id until Reset Communication copies the pending
        // one over (CiA 305 §4.2), so that is where the COB-ID moves.
        const byte NewNodeId = 0x23;
        var app = Defaults(host => host.SetDefaultTpdo(1, Map(0x6000, 0x01)));
        using var rig = new EntryRig(With(app).WithLss(new LssSlaveOptions()), nodeId: Node22);
        await rig.Wire.WaitForCountAsync(BootUpOf22, 1);

        int replies = rig.Wire.Count(CanOpenCobId.LssSlaveMessage);
        rig.Wire.Transmit(CanOpenCobId.LssMasterMessage, new byte[] { LssCommand.SwitchStateGlobal, 1, 0, 0, 0, 0, 0, 0 });
        rig.Wire.Transmit(CanOpenCobId.LssMasterMessage, new byte[] { LssCommand.ConfigureNodeId, NewNodeId, 0, 0, 0, 0, 0, 0 });
        await rig.Wire.WaitForCountAsync(CanOpenCobId.LssSlaveMessage, replies + 1);
        rig.Wire.Transmit(0x000, new[] { (byte)NmtCommand.ResetCommunication, Node22 });
        await rig.Wire.WaitForCountAsync(0x700u + NewNodeId, 1);
        Settle(rig.Node);

        rig.Od.ReadUnsigned(0x1800, 0x01).Should().Be(0x000001A3u,
            "the node retargets a COB-ID that still names the pre-defined connection set of the node-id it leaves");
    }

    [Fact]
    public async Task An_Lss_Slave_Without_A_Node_Id_Takes_Its_Default_Pdos_To_The_Node_Id_It_Is_Given()
    {
        // Without a node-id the pre-defined COB-ID of RPDO 4 is 500h + FFh = 5FFh, a restricted
        // CAN-ID: the default is written all the same, and follows the node-id LSS assigns. An
        // unconfigured slave takes the node-id as soon as it is given one (CiA 305 §4.2), and keeps
        // the moved COB-IDs across a later Reset Communication.
        var app = Defaults(host =>
        {
            host.SetDefaultRpdo(4, Map(0x6000, 0x02));
            host.SetDefaultTpdo(1, Map(0x6000, 0x01));
        }, pdoCount: 4);
        app.WhenWritten = entries => app.Host!.RequestTransmission(entries);
        using var rig = new EntryRig(With(app).WithLss(new LssSlaveOptions()), nodeId: CanOpenCobId.UnconfiguredNodeId);
        Settle(rig.Node);

        int replies = rig.Wire.Count(CanOpenCobId.LssSlaveMessage);
        rig.Wire.Transmit(CanOpenCobId.LssMasterMessage, new byte[] { LssCommand.SwitchStateGlobal, 1, 0, 0, 0, 0, 0, 0 });
        rig.Wire.Transmit(CanOpenCobId.LssMasterMessage, new byte[] { LssCommand.ConfigureNodeId, Node22, 0, 0, 0, 0, 0, 0 });
        await rig.Wire.WaitForCountAsync(CanOpenCobId.LssSlaveMessage, replies + 1);
        await rig.Wire.WaitForCountAsync(BootUpOf22, 1);
        Settle(rig.Node);
        rig.Od.ReadUnsigned(0x1403, 0x01).Should().Be(0x00000522u, "moved when the node-id took effect, at the first boot-up");
        rig.Od.ReadUnsigned(0x1800, 0x01).Should().Be(0x000001A2u, "moved when the node-id took effect, at the first boot-up");

        rig.Wire.Transmit(0x000, new[] { (byte)NmtCommand.ResetCommunication, Node22 });
        await rig.Wire.WaitForCountAsync(BootUpOf22, 2);
        Settle(rig.Node);

        rig.Od.ReadUnsigned(0x1403, 0x01).Should().Be(0x00000522u, "RPDO 4 on the pre-defined COB-ID of node-id 22h");
        rig.Od.ReadUnsigned(0x1800, 0x01).Should().Be(0x000001A2u, "TPDO 1 on the pre-defined COB-ID of node-id 22h");

        rig.Node.ConfigureTpdo(2, Map(0x2101, 0x00));
        await StartAnyAsync(rig);
        rig.Wire.Transmit(0x522, new byte[] { 9 });
        await WaitUntilAsync(() => rig.Od.ReadUnsigned(0x6000, 0x02) == 9, "RPDO 4 to land in 6000h:02");
        rig.Od.WriteUnsigned(0x6000, 0x01, 7);
        await rig.Wire.WaitForCountAsync(Tpdo1Of22, 1);
        await rig.WitnessAsync();

        AssertSingleFrame(rig.Wire.Payloads(Tpdo1Of22), 7);
    }

    [Fact]
    public async Task Setting_A_Default_Pdo_After_Attach_Is_Refused()
    {
        var app = Defaults(_ => { });
        app.WhenWritten = _ => app.Host!.SetDefaultTpdo(1, Map(0x6000, 0x01));
        using var rig = new EntryRig(With(app));
        var cobIdBefore = rig.Od.ReadUnsigned(0x1800, 0x01);
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Node.BackgroundExceptionOccurred += (_, ex) => reported.TrySetResult(ex);

        rig.Od.WriteUnsigned(0x6000, 0x01, 7);

        var failure = await reported.Task.WithTimeoutAsync(ShortTimeout);
        failure.Should().BeOfType<InvalidOperationException>();
        rig.Od.ReadUnsigned(0x1800, 0x01).Should().Be(cobIdBefore, "the refused call changed nothing");
        rig.Od.ReadUnsigned(0x1A00, 0x00).Should().Be(0u);
    }

    // -----------------------------------------------------------------------------------------
    // The arithmetic of a logical device.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void A_Logical_Device_Shifts_Indices_By_800h_And_Pdo_Numbers_By_64()
    {
        CanOpenLogicalDeviceAddressing.MaxLogicalDevices.Should().Be(8);
        CanOpenLogicalDeviceAddressing.Index(0, 0x6000).Should().Be(0x6000);
        CanOpenLogicalDeviceAddressing.Index(1, 0x6000).Should().Be(0x6800);
        CanOpenLogicalDeviceAddressing.Index(7, 0x67FF).Should().Be(0x9FFF);
        CanOpenLogicalDeviceAddressing.PdoNumber(0, 1).Should().Be(1);
        CanOpenLogicalDeviceAddressing.PdoNumber(1, 1).Should().Be(65);
        CanOpenLogicalDeviceAddressing.PdoNumber(7, 64).Should().Be(512);
    }

    [Theory]
    [InlineData(-1, 0x6000, "logicalDevice")]
    [InlineData(8, 0x6000, "logicalDevice")]
    [InlineData(0, 0x5FFF, "indexInDevice0")]
    [InlineData(0, 0x6800, "indexInDevice0")]
    public void An_Index_Outside_Its_Range_Is_Refused(int logicalDevice, int index, string parameter)
    {
        FluentActions.Invoking(() => CanOpenLogicalDeviceAddressing.Index(logicalDevice, (ushort)index))
            .Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be(parameter);
    }

    [Theory]
    [InlineData(-1, 1, "logicalDevice")]
    [InlineData(8, 1, "logicalDevice")]
    [InlineData(0, 0, "pdoInDevice0")]
    [InlineData(0, 65, "pdoInDevice0")]
    public void A_Pdo_Number_Outside_Its_Range_Is_Refused(int logicalDevice, int pdo, string parameter)
    {
        FluentActions.Invoking(() => CanOpenLogicalDeviceAddressing.PdoNumber(logicalDevice, pdo))
            .Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be(parameter);
    }
}
