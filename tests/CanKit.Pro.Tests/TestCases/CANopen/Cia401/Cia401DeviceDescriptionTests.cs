using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Cia401;
using CanKit.Pro.CANopen.Nmt;
using EdsDcfNet.Models;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Cia401;

/// <summary>
/// A CiA 401 device built from the node's device description (<see cref="Cia401Device.FromDescription"/>):
/// the file wins. The device adopts what the file declares, with the file's values and access, and
/// derives its function blocks from it; a first-slice parameter the file leaves out is not added and
/// behaves as its default (CiA 401-1 v3.1.0 §8.1); a catalogue object the file declares with another
/// data type fails the open. A device built from a configuration refuses a node whose description
/// declares objects of its logical device instead of replacing them. Indices, defaults and abort codes
/// are written out from the norm here, not taken from <see cref="Cia401Objects"/>.
/// </summary>
/// <remarks>
/// The descriptions are <c>Fixtures/cia401.eds</c> (16 DI, 16 DO, 2 AI, 2 AO) and
/// <c>Fixtures/cia401-digital.eds</c> (16 DI, 16 DO), edited in memory where a test needs a variant
/// (<see cref="Eds"/>): each test then says in one place what its file differs in, and the base file
/// stays the one <c>Cia401PeerTests</c> holds to the catalogue. Every variant must parse without a
/// diagnostic, so that a malformed edit cannot pass a test by accident.
/// </remarks>
public class Cia401DeviceDescriptionTests
{
    private const uint AttemptWriteReadOnly = 0x06010002;
    private const uint ObjectDoesNotExist = 0x06020000;
    private const uint SubIndexDoesNotExist = 0x06090011;

    // ---- function blocks from the file ------------------------------------------------------------

    [Fact]
    public async Task A_File_With_Digital_Inputs_And_Outputs_Only_Yields_A_Device_With_Exactly_Those()
    {
        using var rig = Open(Eds.Of("cia401-digital.eds"));

        rig.Od.ReadUnsigned(0x1000, 0x00).Should().Be(0x0003_0191u, "profile 401 with digital inputs (bit 16) and outputs (bit 17)");
        rig.Od.ContainsIndex(0x6401).Should().BeFalse();
        rig.Od.ContainsIndex(0x6411).Should().BeFalse();

        // Task 5: an input set by the application, read back and sent in TPDO 1.
        await rig.StartAndTakeFirstTpdoAsync();
        int before = rig.Count(Cia401Rig.Tpdo1);
        rig.Device.SetDigitalInput(9, true);
        await rig.WaitForCountAsync(Cia401Rig.Tpdo1, before + 1);
        rig.Payloads(Cia401Rig.Tpdo1)[before].Should().Equal(0x00, 0x01);
        rig.Device.ReadDigitalInputs(2).Should().Be(0x01);

        // Task 6: an output written by RPDO 1 reaches the terminal.
        await rig.SendRpdo1Async(0x05, 0x80);
        rig.Device.ReadDigitalOutputs(1).Should().Be(0x05);
        rig.Device.ReadDigitalOutputs(2).Should().Be(0x80);

        FluentActions.Invoking(() => rig.Device.SetAnalogInput(1, 1)).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => rig.Device.SetAnalogInputs(1, new short[] { 1 })).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => rig.Device.ReadAnalogInput(1)).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => rig.Device.ReadAnalogOutput(1)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Without_A_Device_Type_In_The_File_The_Device_Sets_The_One_Its_Blocks_Give()
    {
        using var rig = Open(Eds.Of("cia401-digital.eds").Remove(0x1000));

        rig.Od.ReadUnsigned(0x1000, 0x00).Should().Be(0x0003_0191u,
            "the device sets profile 401 with bits 16 and 17 for the blocks it found (CiA 401-1 §6.2.1)");
    }

    [Fact]
    public void A_Device_Type_The_File_Declares_Is_Kept_Even_Where_Its_Function_Bits_Disagree()
    {
        using var rig = Open(Eds.Of("cia401-digital.eds").Set("1000", "DefaultValue", "0x000F0191"));

        rig.Od.ReadUnsigned(0x1000, 0x00).Should().Be(0x000F_0191u, "the file wins; the device neither fails nor corrects it");
        FluentActions.Invoking(() => rig.Device.ReadAnalogOutput(1)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task A_Block_Exists_Only_Where_The_File_Declares_Its_Mandatory_Object()
    {
        // 6443h and 6444h without 6411h: no analog outputs; the two stay as stored objects.
        using var rig = Open(Eds.Of("cia401.eds").Remove(0x6411).Remove(0x1401).Remove(0x1601));

        FluentActions.Invoking(() => rig.Device.ReadAnalogOutput(1)).Should().Throw<InvalidOperationException>();
        rig.Od.ReadRaw(0x6443, 0x01).Should().Equal(0x01);
        rig.Device.SetAnalogInput(2, 7);
        rig.Settle();
        rig.Device.ReadAnalogInput(2).Should().Be(7);

        // A stored object, as its access allows: the reserved values of §8.7.4 are a rule of the
        // analog output block, which the device does not have.
        await rig.WriteU8Async(0x6443, 0x01, 0x05);
        (await rig.ReadU8Async(0x6443, 0x01)).Should().Be(0x05);
    }

    [Fact]
    public async Task An_Index_Of_The_Range_The_Catalogue_Does_Not_Know_Is_Stored_As_Its_Access_Allows()
    {
        using var rig = Open(Eds.Of("cia401-digital.eds").Add(VariableSection(0x6500, "Manufacturer object", "0x0005", "0x11")));

        (await rig.ReadU8Async(0x6500, 0x00)).Should().Be(0x11);
        await rig.WriteU8Async(0x6500, 0x00, 0x42);
        (await rig.ReadU8Async(0x6500, 0x00)).Should().Be(0x42);
        rig.Device.ReadDigitalInputs(1).Should().Be(0x00, "the object has no behaviour");
        rig.Device.ReadDigitalOutputs(1).Should().Be(0x00);
    }

    [Fact]
    public void An_Optional_Array_Declared_Empty_Is_Accepted()
    {
        using var rig = Open(Eds.Of("cia401-digital.eds").Truncate(0x6006, 0));

        rig.Od.ReadRaw(0x6006, 0x00).Should().Equal(new byte[] { 0x00 });
        rig.Od.TryGet(0x6006, 0x01, out _).Should().BeFalse();
    }

    [Fact]
    public async Task An_Optional_Array_Declared_Empty_Behaves_As_Not_Supported()
    {
        using var rig = Open(Eds.Of("cia401-digital.eds").Truncate(0x6006, 0));

        // 6007h and 6008h are 0: only 6006h at its default FFh makes the change an interrupt.
        await rig.StartAndTakeFirstTpdoAsync();
        int before = rig.Count(Cia401Rig.Tpdo1);
        rig.Device.SetDigitalInput(2, true);
        await rig.WaitForCountAsync(Cia401Rig.Tpdo1, before + 1);
        rig.Payloads(Cia401Rig.Tpdo1)[before].Should().Equal(0x02, 0x00);
    }

    [Fact]
    public void A_Mandatory_Object_Declared_Empty_Fails_The_Open_For_A_Block_Without_A_Channel()
    {
        var open = () => Open(Eds.Of("cia401-digital.eds").Truncate(0x6000, 0));

        open.Should().Throw<ArgumentException>()
            .Where(e => e.ParamName == "options")
            .Where(e => e.Message.Contains("6000h") && e.Message.Contains("no channel"));
    }

    [Fact]
    public void A_Sub_Index_00h_Above_The_Maximum_Fails_The_Open()
    {
        var open = () => Open(Eds.Of("cia401-digital.eds").Set("6000sub0", "DefaultValue", "255"));

        open.Should().Throw<ArgumentException>()
            .Where(e => e.ParamName == "options")
            .Where(e => e.Message.Contains("6000h") && e.Message.Contains("1 to 254"));
    }

    [Fact]
    public void A_Parameter_Longer_Than_Its_Block_Has_Its_Surplus_Stored_And_Ignored()
    {
        // 6000h has two groups, 6002h three.
        using var rig = Open(Eds.Of("cia401-digital.eds")
            .Set("6002", "SubNumber", "4")
            .Set("6002sub0", "DefaultValue", "3")
            .Add("[6002sub3]\nParameterName=Polarity digital input 8-bit group 3\nObjectType=0x7\nDataType=0x0005\n"
                + "AccessType=rw\nDefaultValue=0xFF\nPDOMapping=1\n"));

        rig.Od.ReadRaw(0x6002, 0x03).Should().Equal(new byte[] { 0xFF });
        rig.Device.ReadDigitalInputs(2).Should().Be(0x00);
        FluentActions.Invoking(() => rig.Device.ReadDigitalInputs(3))
            .Should().Throw<ArgumentOutOfRangeException>("the block has the two groups of 6000h");
    }

    // ---- logical devices -------------------------------------------------------------------------

    [Fact]
    public void A_Description_Device_Of_Logical_Device_1_Takes_Its_Objects_From_6800h()
    {
        var eds = Eds.Of("cia401-digital.eds").Remove(0x1400).Remove(0x1600).Remove(0x1800).Remove(0x1A00);
        foreach (ushort index in DigitalObjects) eds.Move(index, (ushort)(index + 0x800));
        var configured = new Cia401Device(new Cia401Configuration { DigitalInputs = 8 });
        var described = Cia401Device.FromDescription(1);
        using var rig = new Cia401Rig(new[] { configured, described }, eds.Parse(), options: null);

        rig.Od.ReadUnsigned(0x6FFF, 0x00).Should().Be(0x0003_0191u, "logical device 1 has the blocks of 6800h and 6A00h");
        described.SetDigitalInput(9, true);
        rig.Settle();
        rig.Od.ReadRaw(0x6800, 0x02).Should().Equal(new byte[] { 0x01 });
        rig.Od.ReadRaw(0x6000, 0x01).Should().Equal(new byte[] { 0x00 }, "logical device 0 is the configured one");
        described.ReadDigitalOutputs(2).Should().Be(0x00);
        FluentActions.Invoking(() => configured.ReadDigitalInputs(2)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void A_Described_Logical_Device_0_And_A_Configured_Logical_Device_1_Each_See_Their_Own_Range()
    {
        var described = Cia401Device.FromDescription(0);
        var configured = new Cia401Device(new Cia401Configuration { DigitalInputs = 8, LogicalDevice = 1 });
        using var rig = new Cia401Rig(new[] { described, configured }, Eds.Of("cia401-digital.eds").Parse(), options: null);

        rig.Od.ReadRaw(0x6800, 0x00).Should().Equal(new byte[] { 0x01 }, "the configured device declares one group at 6800h");
        rig.Od.ReadRaw(0x6806, 0x01).Should().Equal(new byte[] { 0xFF });
        described.SetDigitalInput(1, true);
        rig.Settle();
        rig.Od.ReadRaw(0x6000, 0x01).Should().Equal(new byte[] { 0x01 });
        rig.Od.ReadRaw(0x6800, 0x01).Should().Equal(new byte[] { 0x00 });
        configured.SetDigitalInput(2, true);
        rig.Settle();
        rig.Od.ReadRaw(0x6800, 0x01).Should().Equal(new byte[] { 0x02 });
        rig.Od.ReadRaw(0x6000, 0x01).Should().Equal(new byte[] { 0x01 });
    }

    // ---- resets, stored values, device type, PDOs ---------------------------------------------------

    [Fact]
    public async Task A_Save_And_Reset_Node_Bring_Back_The_Stored_Values_Of_The_Adopted_Objects()
    {
        using var rig = Open(Eds.Of("cia401-digital.eds")
            .Set("6206sub1", "DefaultValue", "0x0F")
            .Set("6206sub2", "DefaultValue", "0x0F"));

        await rig.WriteU8Async(0x6200, 0x01, 0x0F);
        await rig.WriteU8Async(0x6206, 0x01, 0x33);
        await rig.SaveAsync();
        await rig.WriteU8Async(0x6200, 0x01, 0xF0);
        await rig.WriteU8Async(0x6206, 0x01, 0x44);
        rig.Device.ReadDigitalOutputs(1).Should().Be(0xF0);

        await rig.ResetAsync(NmtCommand.ResetNode);

        (await rig.ReadU8Async(0x6206, 0x01)).Should().Be(0x33, "the stored value overwrites the file's (CiA 401-1 §6.2.5 NOTE)");
        rig.Device.ReadDigitalOutputs(1).Should().Be(0x0F, "the output comes up with the stored 6200h");
    }

    [Fact]
    public async Task Reset_Communication_Leaves_The_Adopted_Objects_And_The_Outputs_As_They_Are()
    {
        // 6208h 0Fh: the device holds 0Fh while 6200h holds FFh.
        using var rig = Open(Eds.Of("cia401-digital.eds").Set("6208sub1", "DefaultValue", "0x0F"));

        await rig.WriteU8Async(0x6200, 0x01, 0xFF);
        await rig.WriteU8Async(0x6206, 0x01, 0x33);
        rig.Device.ReadDigitalOutputs(1).Should().Be(0x0F);

        await rig.ResetAsync(NmtCommand.ResetCommunication);

        (await rig.ReadU8Async(0x6206, 0x01)).Should().Be(0x33, "Reset Communication restores 1000h to 1FFFh only");
        rig.Device.ReadDigitalOutputs(1).Should().Be(0x0F, "Reset Communication resets no application");
    }

    [Fact]
    public void A_File_Whose_Device_Type_Names_Another_Profile_Fails_The_Open()
    {
        var open = () => Open(Eds.Of("cia401-digital.eds").Set("1000", "DefaultValue", "0x00030192"));

        open.Should().Throw<ArgumentException>()
            .Where(e => e.ParamName == "options")
            .Where(e => e.Message.Contains("profile 402") && e.Message.Contains("profile 401"));
    }

    [Fact]
    public async Task A_File_Without_Pdo_Records_Gives_A_Device_Without_Pdos()
    {
        using var rig = Open(Eds.Of("cia401-digital.eds").Remove(0x1400).Remove(0x1600).Remove(0x1800).Remove(0x1A00));
        int reports = 0;
        rig.Node.BackgroundExceptionOccurred += (_, _) => System.Threading.Interlocked.Increment(ref reports);

        rig.Od.ContainsIndex(0x1400).Should().BeFalse();
        rig.Od.ContainsIndex(0x1800).Should().BeFalse();
        await rig.WriteU8Async(0x6200, 0x01, 0x05);
        rig.Device.ReadDigitalOutputs(1).Should().Be(0x05, "the outputs are written over SDO");

        await rig.StartAsync();
        rig.Device.SetDigitalInput(1, true);
        await rig.FlushBackgroundReportsAsync();
        await rig.DrainAsync();

        reports.Should().Be(0);
        rig.Count(Cia401Rig.Tpdo1).Should().Be(0);
        (await rig.ReadU8Async(0x6000, 0x01)).Should().Be(0x01);
    }

    [Fact]
    public void The_Plant_Model_Is_First_Called_With_The_Files_Output_Levels()
    {
        var model = new FirstCall();
        using var rig = new Cia401Rig(Cia401Device.FromDescription(0, model),
            Eds.Of("cia401-digital.eds").Set("6200sub1", "DefaultValue", "0x0A").Parse());
        rig.Settle();

        model.Calls.Should().Be(1);
        model.FirstLevels.Should().Be(0x0A);
    }

    // ---- a configuration device on a described node, the device-type object ------------------------

    [Fact]
    public void A_Described_Device_Type_Object_Alone_Does_Not_Refuse_A_Configuration_Device()
    {
        // device.eds: 1000h 00030191h and nothing in 6000h-67FFh; 67FFh added.
        var description = Eds.Of("device.eds")
            .Add("[67FF]\nParameterName=Device type\nObjectType=0x7\nDataType=0x0007\nAccessType=ro\nDefaultValue=0x00030191\nPDOMapping=0\n")
            .Parse();
        using var rig = new Cia401Rig(new Cia401Device(new Cia401Configuration { DigitalInputs = 8, DigitalOutputs = 8 }), description);

        rig.Od.ReadUnsigned(0x67FF, 0x00).Should().Be(0x0003_0191u, "the node keeps the described object");
        rig.Od.ReadUnsigned(0x1000, 0x00).Should().Be(0x0003_0191u);
        rig.Od.ReadRaw(0x6006, 0x01).Should().Equal(new byte[] { 0xFF }, "the configuration declares its objects");
    }

    // ---- the digital fixture ------------------------------------------------------------------------

    [Fact]
    public void The_Digital_Fixture_Describes_What_A_Description_Device_Declares()
    {
        var description = Eds.Of("cia401-digital.eds").Parse();
        using var rig = new Cia401Rig(Cia401Device.FromDescription(), description);
        using var configured = new Cia401Rig(new Cia401Configuration { DigitalInputs = 16, DigitalOutputs = 16 });
        var od = rig.Od;

        description.Objects.Objects[0x1000].DefaultValue.Should().Be(
            "0x" + new Cia401Configuration { DigitalInputs = 16, DigitalOutputs = 16 }.DeviceType.Value.ToString("X8", CultureInfo.InvariantCulture));

        int described = 0;
        foreach (var obj in description.Objects.Objects.Values.Where(o => o.Index is >= 0x6000 and <= 0x67FF))
        {
            Cia401Objects.TryGet(obj.Index, out var definition).Should().BeTrue($"{obj.Index:X4}h is a CiA 401 object");
            var subs = obj.SubObjects.Count == 0
                ? new[] { (Sub: (byte)0, Type: (ushort?)obj.DataType, Access: obj.AccessType) }
                : obj.SubObjects.Values.Select(s => (Sub: s.SubIndex, Type: (ushort?)s.DataType, Access: s.AccessType)).ToArray();
            foreach (var (sub, type, access) in subs)
            {
                bool highest = definition.IsArray && sub == 0;
                type.Should().Be((ushort)(highest ? OdDataType.Unsigned8 : definition.DataType), $"the type of {obj.Index:X4}h:{sub:X2}h");
                access.Should().Be(highest || definition.Access == OdAccess.ReadOnly ? AccessType.ReadOnly : AccessType.ReadWrite,
                    $"the access of {obj.Index:X4}h:{sub:X2}h");
                od.TryGet(obj.Index, sub, out var entry).Should().BeTrue($"the device has {obj.Index:X4}h:{sub:X2}h");
                entry.DataType.Should().Be(highest ? OdDataType.Unsigned8 : definition.DataType);
                entry.Access.Should().Be(access == AccessType.ReadOnly ? OdAccess.ReadOnly : OdAccess.ReadWrite);
                described++;
            }
        }

        int declared = 0;
        foreach (var definition in Cia401Objects.All)
        {
            for (int sub = 0; sub <= 0xFF; sub++)
            {
                if (!od.TryGet(definition.Index, (byte)sub, out var entry)) continue;
                description.Contains(definition.Index, (byte)sub).Should().BeTrue($"the fixture describes {definition.Index:X4}h:{sub:X2}h");
                configured.Od.TryGet(definition.Index, (byte)sub, out var expected).Should()
                    .BeTrue($"a device configured for 16 DI and 16 DO has {definition.Index:X4}h:{sub:X2}h");
                entry.DataType.Should().Be(expected.DataType);
                entry.Access.Should().Be(expected.Access);
                entry.GetRawValue().Should().Equal(expected.GetRawValue(), $"the fixture's {definition.Index:X4}h:{sub:X2}h is the catalogue's default");
                declared++;
            }
        }

        declared.Should().Be(described, "the fixture and the device have the same entries in 6000h to 67FFh");
        described.Should().BeGreaterThan(0);
        configured.Od.SnapshotKeys().Count(k => k >> 8 is >= 0x6000 and <= 0x67FF).Should().Be(declared,
            "the configured device has no entry the fixture leaves out");
    }

    [Fact]
    public void A_File_Without_Any_Function_Block_Fails_The_Open()
    {
        var open = () => new Cia401Rig(Cia401Device.FromDescription(), CanOpenDeviceDescription.ParseEds(CanOpenFixtures.Text("device.eds")));

        open.Should().Throw<ArgumentException>()
            .Where(e => e.ParamName == "options")
            .Where(e => e.Message.Contains("no CiA 401 function block") && e.Message.Contains("logical device 0"));
    }

    [Fact]
    public void A_Mandatory_Object_That_Lacks_A_Sub_Index_Its_Count_Announces_Fails_The_Open()
    {
        var open = () => Open(Eds.Of("cia401-digital.eds").RemoveSection("6000sub2"));

        open.Should().Throw<ArgumentException>()
            .Where(e => e.ParamName == "options")
            .Where(e => e.Message.Contains("6000h") && e.Message.Contains("02h"));
    }

    // ---- what the file declares ---------------------------------------------------------------------

    [Fact]
    public async Task Without_6006h_Any_Change_Interrupts_And_6006h_Is_Absent_Over_Sdo()
    {
        using var rig = Open(Eds.Of("cia401-digital.eds").Remove(0x6006));

        rig.Od.ContainsIndex(0x6006).Should().BeFalse("a parameter the file leaves out is not added");
        Cia401Rig.AbortCode(await rig.SdoAsync(Upload(0x6006, 0x01))).Should().Be(ObjectDoesNotExist);

        // 6007h and 6008h are 0, so only 6006h at its default FFh (CiA 401-1 §8.2.5) makes this an interrupt.
        await rig.StartAndTakeFirstTpdoAsync();
        int before = rig.Count(Cia401Rig.Tpdo1);
        rig.Device.SetDigitalInput(1, true);
        await rig.WaitForCountAsync(Cia401Rig.Tpdo1, before + 1);
        rig.Payloads(Cia401Rig.Tpdo1)[before].Should().Equal(0x01, 0x00);
    }

    [Fact]
    public async Task Without_6206h_To_6208h_The_Outputs_Behave_As_Their_Defaults()
    {
        // 6208h FFh lets every bit through (§8.3.5); 6206h FFh puts every bit to 6207h, 0 (§8.3.3, §8.3.4).
        using var rig = Open(Eds.Of("cia401-digital.eds").Remove(0x6206).Remove(0x6207).Remove(0x6208));

        await rig.StartAsync();
        await rig.SendRpdo1Async(0xA5, 0x00);
        rig.Device.ReadDigitalOutputs(1).Should().Be(0xA5);

        await rig.StopAsync();
        rig.Device.ReadDigitalOutputs(1).Should().Be(0x00);
    }

    [Fact]
    public async Task Without_6443h_An_Analog_Output_Takes_Its_Error_Value_In_The_Error_State()
    {
        // 6443h defaults to 1h, "error value" (§8.7.4); 6444h is left adjusted (Figure 7).
        using var rig = Open(Eds.Of("cia401.eds").Remove(0x6443).Set("6444sub1", "DefaultValue", "0x12340000"));

        await rig.WriteI16Async(0x6411, 0x01, 100);
        rig.Device.ReadAnalogOutput(1).Should().Be(100);

        await rig.StopAsync();
        rig.Device.ReadAnalogOutput(1).Should().Be(0x1234);
    }

    [Fact]
    public async Task Without_6422h_The_Device_Keeps_No_Interrupt_Source()
    {
        using var rig = Open(Eds.Of("cia401.eds").Remove(0x6422));
        int reports = 0;
        rig.Node.BackgroundExceptionOccurred += (_, _) => System.Threading.Interlocked.Increment(ref reports);

        // The write clears the interrupt source; with the defaults (07h, limits and deltas 0) the set
        // interrupts, which sets it.
        await rig.WriteU8Async(0x6423, 0x00, 1);
        rig.Device.SetAnalogInput(1, 500);
        await rig.FlushBackgroundReportsAsync();

        reports.Should().Be(0, "neither the clear nor the set touches an object the file leaves out");
        rig.Device.ReadAnalogInput(1).Should().Be(500);
        Cia401Rig.AbortCode(await rig.SdoAsync(Upload(0x6422, 0x01))).Should().Be(ObjectDoesNotExist);
    }

    [Fact]
    public async Task The_Files_Default_Beats_The_Catalogues_6206h_0Fh_Is_The_Error_Mode()
    {
        using var rig = Open(Eds.Of("cia401-digital.eds")
            .Set("6206sub1", "DefaultValue", "0x0F")
            .Set("6206sub2", "DefaultValue", "0x0F"));

        (await rig.ReadU8Async(0x6206, 0x01)).Should().Be(0x0F);
        await rig.WriteU8Async(0x6200, 0x01, 0xFF);
        rig.Device.ReadDigitalOutputs(1).Should().Be(0xFF);

        // Stopped is a cause of the error state (CiA 401-1 §8.3.3): the bits with 6206h = 1 take
        // 6207h (0), the others keep their value.
        await rig.StopAsync();
        rig.Device.ReadDigitalOutputs(1).Should().Be(0xF0);
    }

    [Fact]
    public async Task Reset_Node_Returns_An_Adopted_Object_To_The_Files_Value()
    {
        using var rig = Open(Eds.Of("cia401-digital.eds")
            .Set("6206sub1", "DefaultValue", "0x0F")
            .Set("6206sub2", "DefaultValue", "0x0F"));

        await rig.WriteU8Async(0x6206, 0x01, 0xFF);
        (await rig.ReadU8Async(0x6206, 0x01)).Should().Be(0xFF);
        await rig.ResetAsync(NmtCommand.ResetNode);

        (await rig.ReadU8Async(0x6206, 0x01)).Should().Be(0x0F, "the power-on value is the file's");
    }

    [Fact]
    public async Task The_Files_Access_Is_Kept_And_The_Device_Still_Writes_Its_Inputs()
    {
        // 6000h ro as the norm has it; 6002h const, which the norm's rw allows (CiA 401-1 §8.1).
        using var rig = Open(Eds.Of("cia401-digital.eds")
            .Set("6002sub1", "AccessType", "const")
            .Set("6002sub2", "AccessType", "const"));

        Cia401Rig.AbortCode(await rig.DownloadU8Async(0x6002, 0x01, 0xFF)).Should().Be(AttemptWriteReadOnly);
        Cia401Rig.AbortCode(await rig.DownloadU8Async(0x6000, 0x01, 0xFF)).Should().Be(AttemptWriteReadOnly);

        rig.Device.SetDigitalInput(1, true);
        rig.Settle();
        (await rig.ReadU8Async(0x6000, 0x01)).Should().Be(0x01, "the device's own write passes the read-only entry");
    }

    [Fact]
    public async Task A_Parameter_Shorter_Than_Its_Block_Leaves_The_Groups_Beyond_It_At_The_Default()
    {
        // 6000h has two groups, 6002h one: group 2 is not inverted (default 0).
        using var rig = Open(Eds.Of("cia401-digital.eds")
            .Truncate(0x6002, 1)
            .Set("6002sub1", "DefaultValue", "0xFF"));

        rig.Device.SetDigitalInputs(1, 0x00);
        rig.Device.SetDigitalInputs(2, 0x00);
        rig.Settle();

        rig.Device.ReadDigitalInputs(1).Should().Be(0xFF);
        rig.Device.ReadDigitalInputs(2).Should().Be(0x00);
        Cia401Rig.AbortCode(await rig.SdoAsync(Upload(0x6002, 0x02))).Should().Be(SubIndexDoesNotExist);
    }

    [Fact]
    public void The_Inputs_Show_The_Devices_Levels_From_The_Start_Not_The_Files_Values()
    {
        using var rig = Open(Eds.Of("cia401.eds")
            .Set("6000sub1", "DefaultValue", "0x55")
            .Set("6002sub1", "DefaultValue", "0x0F")
            .Set("6401sub1", "DefaultValue", "1234"));

        rig.Od.ReadRaw(0x6000, 0x01).Should().Equal(new byte[] { 0x0F }, "physical 0 under the file's polarity 0Fh");
        rig.Od.ReadSigned(0x6401, 0x01).Should().Be(0, "the application has set no value yet");
        rig.Device.ReadDigitalInputs(1).Should().Be(0x0F);
    }

    [Fact]
    public void The_Outputs_Power_On_With_The_Values_A_Dcf_Gives_Them()
    {
        var dcf = Eds.Of("cia401.eds")
            .Set("6200sub1", "ParameterValue", "0x0A")
            .Set("6202sub1", "ParameterValue", "0x03")
            .Set("6411sub1", "ParameterValue", "100")
            .ParseDcf(Cia401Rig.DeviceId);
        using var rig = new Cia401Rig(Cia401Device.FromDescription(), dcf);

        rig.Device.ReadDigitalOutputs(1).Should().Be(0x09, "6200h 0Ah through the polarity 03h (CiA 401-1 §8.3.2)");
        rig.Device.ReadAnalogOutput(1).Should().Be(100);
    }

    [Fact]
    public async Task A_Tpdo_The_File_Remaps_Keeps_The_Files_Mapping()
    {
        using var rig = Open(Eds.Of("cia401-digital.eds")
            .Set("1A00sub0", "DefaultValue", "1")
            .Set("1A00sub1", "DefaultValue", "0x60000208"));

        rig.Od.ReadUnsigned(0x1A00, 0x00).Should().Be(1u);
        rig.Od.ReadUnsigned(0x1A00, 0x01).Should().Be(0x6000_0208u);

        await rig.StartAndTakeFirstTpdoAsync();
        int before = rig.Count(Cia401Rig.Tpdo1);
        rig.Device.SetDigitalInputs(2, 0x03);
        await rig.WaitForCountAsync(Cia401Rig.Tpdo1, before + 1);
        rig.Payloads(Cia401Rig.Tpdo1)[before].Should().Equal(0x03);
    }

    [Fact]
    public async Task An_Object_The_First_Slice_Does_Not_Build_Stays_As_A_Stored_Object()
    {
        using var rig = Open(Eds.Of("cia401-digital.eds").Add(ArraySections(0x6100, "Read digital input 16-bit", "0x0006", "ro", "0x1234")));

        (await rig.ReadI16Async(0x6100, 0x01)).Should().Be(0x1234, "the file's value, stored and served");
        Cia401Rig.AbortCode(await rig.DownloadU8Async(0x6100, 0x01, 0x01)).Should().Be(AttemptWriteReadOnly);
    }

    // ---- what the file declares with another type --------------------------------------------------

    [Fact]
    public void A_Catalogue_Object_Declared_With_Another_Data_Type_Fails_The_Open()
    {
        var open = () => Open(Eds.Of("cia401-digital.eds")
            .Set("6000sub1", "DataType", "0x0006")
            .Set("6000sub2", "DataType", "0x0006"));

        open.Should().Throw<ArgumentException>()
            .Where(e => e.ParamName == "options")
            .Where(e => e.Message.Contains("6000h") && e.Message.Contains("01h")
                && e.Message.Contains("Unsigned16") && e.Message.Contains("Unsigned8"));
    }

    [Fact]
    public void An_Array_Whose_Sub_Index_0_Is_Not_Unsigned8_Fails_The_Open()
    {
        var open = () => Open(Eds.Of("cia401-digital.eds").Set("6002sub0", "DataType", "0x0006"));

        open.Should().Throw<ArgumentException>()
            .Where(e => e.ParamName == "options")
            .Where(e => e.Message.Contains("6002h") && e.Message.Contains("00h") && e.Message.Contains("Unsigned16"));
    }

    [Fact]
    public void An_Array_Declared_As_A_Variable_Fails_The_Open()
    {
        var open = () => Open(Eds.Of("cia401-digital.eds").Remove(0x6002).Add(VariableSection(0x6002, "Polarity digital input 8-bit", "0x0005", "0x0F")));

        open.Should().Throw<ArgumentException>()
            .Where(e => e.ParamName == "options")
            .Where(e => e.Message.Contains("6002h") && e.Message.Contains("array"));
    }

    [Fact]
    public void A_Variable_Declared_As_An_Array_Fails_The_Open()
    {
        var open = () => Open(Eds.Of("cia401-digital.eds").Remove(0x6005).Add(ArraySections(0x6005, "Global interrupt enable digital 8-bit", "0x0001", "rw", "1")));

        open.Should().Throw<ArgumentException>()
            .Where(e => e.ParamName == "options")
            .Where(e => e.Message.Contains("6005h") && e.Message.Contains("as an array"));
    }

    [Fact]
    public void An_Object_The_First_Slice_Does_Not_Build_Is_Still_Checked_Against_The_Catalogue()
    {
        var open = () => Open(Eds.Of("cia401-digital.eds").Add(ArraySections(0x6100, "Read digital input 16-bit", "0x0005", "ro", "0")));

        open.Should().Throw<ArgumentException>()
            .Where(e => e.ParamName == "options")
            .Where(e => e.Message.Contains("6100h") && e.Message.Contains("Unsigned8") && e.Message.Contains("Unsigned16"));
    }

    // ---- a device built from a configuration on a described node ------------------------------------

    [Fact]
    public void A_Configuration_Device_Refuses_A_Node_Whose_Description_Declares_Its_Objects()
    {
        var open = () => new Cia401Rig(new Cia401Device(Cia401ToolRig.FixtureConfiguration), Cia401ToolRig.FixtureDescription);

        open.Should().Throw<ArgumentException>()
            .Where(e => e.ParamName == "options")
            .Where(e => e.Message.Contains("logical device 0") && e.Message.Contains("FromDescription"));
    }

    [Fact]
    public void A_Configuration_Device_On_A_Described_Node_Without_Its_Objects_Works_As_Before()
    {
        // device.eds declares no object in 6000h-67FFh, and 1000h with profile 401.
        using var rig = new Cia401Rig(new Cia401Device(new Cia401Configuration { DigitalInputs = 8, DigitalOutputs = 8 }),
            CanOpenDeviceDescription.ParseEds(CanOpenFixtures.Text("device.eds")));

        rig.Od.ReadRaw(0x6006, 0x01).Should().Equal(new byte[] { 0xFF }, "the configuration declares the catalogue's default");
        rig.Device.SetDigitalInput(3, true);
        rig.Settle();
        rig.Device.ReadDigitalInputs(1).Should().Be(0x04);
    }

    // ---- the application side and the options ------------------------------------------------------

    [Fact]
    public void A_Description_Device_Asks_For_No_Pdos_And_For_Failure_Monitoring()
    {
        var device = Cia401Device.FromDescription();

        device.Requirements.PdoCount.Should().Be(0, "the file decides the PDOs");
        device.Requirements.CommunicationFailureMonitoring.Should().BeTrue();
        var options = Cia401Device.AddTo(new CanOpenNodeOptions { PdoCount = 3 }, device);
        options.PdoCount.Should().Be(3, "AddTo neither raises nor lowers it for a description device");
        options.EnableErrorBehavior.Should().BeTrue();
        options.Applications.Should().ContainSingle().Which.Should().BeSameAs(device);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(8)]
    public void A_Logical_Device_Out_Of_Range_Is_Refused(int logicalDevice)
    {
        FluentActions.Invoking(() => Cia401Device.FromDescription(logicalDevice))
            .Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("logicalDevice");
    }

    [Fact]
    public void Before_It_Is_Attached_A_Description_Device_Refuses_Every_Set_And_Read()
    {
        var device = Cia401Device.FromDescription();

        FluentActions.Invoking(() => device.SetDigitalInput(1, true)).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => device.ReadDigitalInputs(1)).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => device.ReadDigitalOutputs(1)).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => device.SetAnalogInput(1, 1)).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => device.ReadAnalogOutput(1)).Should().Throw<InvalidOperationException>();
    }

    // ---- helpers -----------------------------------------------------------------------------------

    /// <summary>The objects of <c>cia401-digital.eds</c> in logical device 0.</summary>
    private static readonly ushort[] DigitalObjects =
        { 0x6000, 0x6002, 0x6003, 0x6005, 0x6006, 0x6007, 0x6008, 0x6200, 0x6202, 0x6206, 0x6207, 0x6208 };

    /// <summary>A plant model that records its calls and the digital outputs at the first.</summary>
    private sealed class FirstCall : ICia401Process
    {
        private int _calls;
        private int _first = -1;

        internal int Calls => System.Threading.Volatile.Read(ref _calls);

        internal int FirstLevels => System.Threading.Volatile.Read(ref _first);

        public void OnOutputsChanged(Cia401Device device)
        {
            if (System.Threading.Interlocked.Increment(ref _calls) == 1) System.Threading.Volatile.Write(ref _first, device.ReadDigitalOutputs(1));
        }
    }

    private static Cia401Rig Open(Eds eds) => new(Cia401Device.FromDescription(), eds.Parse());

    private static byte[] Upload(ushort index, byte subindex)
        => new byte[] { 0x40, (byte)index, (byte)(index >> 8), subindex, 0, 0, 0, 0 };

    /// <summary>The sections of a one-element array <paramref name="index"/>.</summary>
    private static string ArraySections(ushort index, string name, string dataType, string access, string value)
        => string.Format(CultureInfo.InvariantCulture,
            "[{0:X4}]\nParameterName={1}\nSubNumber=2\nObjectType=0x8\n\n"
            + "[{0:X4}sub0]\nParameterName=Highest sub-index supported\nObjectType=0x7\nDataType=0x0005\nAccessType=ro\nDefaultValue=1\nPDOMapping=0\n\n"
            + "[{0:X4}sub1]\nParameterName={1} 1\nObjectType=0x7\nDataType={2}\nAccessType={3}\nDefaultValue={4}\nPDOMapping=1\n\n",
            index, name, dataType, access, value);

    /// <summary>The section of a read-write variable <paramref name="index"/>.</summary>
    private static string VariableSection(ushort index, string name, string dataType, string value)
        => string.Format(CultureInfo.InvariantCulture,
            "[{0:X4}]\nParameterName={1}\nObjectType=0x7\nDataType={2}\nAccessType=rw\nDefaultValue={3}\nPDOMapping=0\n\n",
            index, name, dataType, value);

    /// <summary>
    /// An EDS as text, edited by section. <see cref="ToString"/> lists every object section in
    /// <c>[MandatoryObjects]</c> (<c>1000h</c>, <c>1001h</c>, <c>1018h</c>) or <c>[OptionalObjects]</c>,
    /// so a removed or added object leaves no stale entry in either list.
    /// </summary>
    private sealed class Eds
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
                "NodeName=CiA 401 test device",
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
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
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
}
