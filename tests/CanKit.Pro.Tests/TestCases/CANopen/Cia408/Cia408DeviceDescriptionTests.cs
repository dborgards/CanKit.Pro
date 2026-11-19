using System;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Application;
using CanKit.Pro.CANopen.Cia408;
using CanKit.Pro.CANopen.Nmt;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Cia408;

/// <summary>
/// A CiA 408 device built from the node's device description (<see cref="Cia408Device.FromDescription"/>):
/// the file wins. The device adopts what the file declares, with the file's types, access and values,
/// and takes its resolution and modes from the file's device type and the mode objects it declares; an
/// optional object the file leaves out is not added; a catalogue object declared with another data type
/// or kind fails the open; the mandatory objects of §9.3 must be there. A device built from a
/// configuration refuses a node whose description declares objects of its logical device. Indices,
/// device types and abort codes are written out from the norm here, not taken from
/// <see cref="Cia408Objects"/>.
/// </summary>
/// <remarks>The description is <c>Fixtures/cia408-pq-valve.eds</c> (a 16-bit pQ valve), edited in
/// memory where a test needs a variant (<see cref="Eds"/>): each test then says in one place what its
/// file differs in, and the base file stays the one <c>Cia408PeerTests</c> holds to the catalogue and
/// to a configured device. Every variant must parse without a diagnostic.</remarks>
public class Cia408DeviceDescriptionTests
{
    private const uint AttemptWriteReadOnly = 0x0601_0002;
    private const uint ValueRangeExceeded = 0x0609_0030;

    /// <summary>CiA 408 v2.2.0 §6.3, Table 1: input current too high.</summary>
    private const ushort InputCurrentTooHigh = 0x2110;

    private static Eds Fixture => Eds.Of("cia408-pq-valve.eds");

    private static Cia408Rig Open(Eds eds, int logicalDevice = 0)
        => new(new[] { Cia408Device.FromDescription(logicalDevice) }, eds.Parse());

    // ---- the device the file describes -------------------------------------------------------------

    [Fact]
    public async Task The_Fixture_Yields_The_Pq_Valve_It_Describes()
    {
        using var rig = Open(Fixture);
        rig.Settle();

        rig.Od.ReadUnsigned(0x1000, 0x00).Should().Be(0x2200_0198u, "the file's device type stays");
        rig.Device.State.Should().Be(Cia408State.Init);
        rig.Od.ReadUnsigned(0x6041, 0x00).Should().Be(0x0008u, "INIT, remote control (Table 4)");
        rig.Od.ReadUnsigned(0x6045, 0x00).Should().Be(0x0000_0008u, "both status words show the one state");
        rig.Device.CommandEntries.Should().Equal(
            new CanOpenObjectAddress(0x6040, 0x00), new CanOpenObjectAddress(0x6044, 0x00), new CanOpenObjectAddress(0x604F, 0x00));

        await rig.WriteU16Async(0x6040, 0x00, 0x0007);
        rig.Settle();
        rig.Device.State.Should().Be(Cia408State.Active, "D2, D3, D4 from one control word");
        rig.Od.ReadUnsigned(0x6041, 0x00).Should().Be(0x000Fu);

        rig.Device.SetActualValue(Cia408Objects.SpoolPositionActualValue, 321);
        rig.Settle();
        rig.Od.ReadSigned(0x6301, 0x01).Should().Be(321, "the actual values of control mode 5 are the device's");
        FluentActions.Invoking(() => rig.Device.SetActualValue(Cia408Objects.DscActualValue, 1))
            .Should().Throw<ArgumentException>("the file declares no speed control");
        AbortCode(await rig.DownloadU8Async(0x6043, 0x00, 5)).Should().Be(AttemptWriteReadOnly, "the file's 6043h is ro");
    }

    [Fact]
    public void The_Files_Values_Win_Over_The_Catalogue_Defaults()
    {
        using var rig = Open(Fixture
            .Set("6300sub1", "DefaultValue", "100")
            .Set("6301sub2", "DefaultValue", "0xA2")
            .Set("604F", "DefaultValue", "1"));
        rig.Settle();

        rig.Od.ReadSigned(0x6300, 0x01).Should().Be(100, "a setpoint is a parameter: the file's value");
        rig.Od.ReadRaw(0x6301, 0x02).Should().Equal(new byte[] { 0xA2 }, "the file's unit");
        rig.Od.ReadUnsigned(0x6041, 0x00).Should().Be(0x0018u, "604Fh = 1 from the file: local control, status bit 4");
    }

    [Fact]
    public async Task A_Valve_Whose_File_Says_Local_Ignores_A_Bus_Control_Word()
    {
        using var rig = Open(Fixture.Set("604F", "DefaultValue", "1"));

        await rig.WriteU16Async(0x6040, 0x00, 0x0001);
        rig.Settle();

        rig.Device.State.Should().Be(Cia408State.Init, "under local control the bus word is ignored (§9.3.9)");
    }

    [Fact]
    public void What_Shows_The_State_Is_The_Devices_From_The_Start()
    {
        using var rig = Open(Fixture
            .Set("6040", "DefaultValue", "7")
            .Set("6041", "DefaultValue", "0x000F")
            .Set("604E", "DefaultValue", "0x2110")
            .Set("6301sub1", "DefaultValue", "55"));
        rig.Settle();

        rig.Od.ReadUnsigned(0x6040, 0x00).Should().Be(0u, "the history of the control word starts at 0, as after Reset Node");
        rig.Od.ReadUnsigned(0x6041, 0x00).Should().Be(0x0008u, "the status is composed from the state, INIT");
        rig.Od.ReadUnsigned(0x604E, 0x00).Should().Be(0u, "no fault has occurred");
        rig.Od.ReadSigned(0x6301, 0x01).Should().Be(0, "the actual value is the device's, not the file's");
        rig.Device.State.Should().Be(Cia408State.Init);
    }

    [Fact]
    public void Without_A_Device_Type_The_Device_Composes_One_From_Its_Objects()
    {
        using var rig = Open(Fixture.Remove(0x1000));

        // 6300h to 6381h are the setpoints and actual values of modes 1 to 5 in 16 bit (Tables 13 and
        // 32): capability bits 23 (1, 2), 24 (3, 4) and 25 (5), resolution 01b (§8.6.2, Table 53).
        rig.Od.ReadUnsigned(0x1000, 0x00).Should().Be(0x2380_0198u);
    }

    // ---- what the file leaves out ------------------------------------------------------------------

    [Fact]
    public async Task Optional_Objects_The_File_Leaves_Out_Are_Not_Added_And_The_Device_Works_Without_Them()
    {
        using var rig = Open(Fixture.Remove(0x6043).Remove(0x604E).Remove(0x604F).Remove(0x6044).Remove(0x6045));
        int reports = 0;
        rig.Node.BackgroundExceptionOccurred += (_, _) => System.Threading.Interlocked.Increment(ref reports);
        rig.Settle();

        foreach (ushort index in new ushort[] { 0x6043, 0x604E, 0x604F, 0x6044, 0x6045 })
            rig.Od.ContainsIndex(index).Should().BeFalse($"the file does not declare {index:X4}h");
        rig.Device.CommandEntries.Should().Equal(new CanOpenObjectAddress(0x6040, 0x00));

        // Without 604Fh a valve takes the control word from the bus (§9.3.9).
        await rig.WriteU16Async(0x6040, 0x00, 0x0003);
        rig.Settle();
        rig.Device.State.Should().Be(Cia408State.Hold);

        // A fault: its EMCY, the fault state in the status word, and no 604Eh to write.
        int emcys = rig.Count(CanOpenProfileRig.Emcy);
        rig.Device.ReportFault(InputCurrentTooHigh, Cia408FaultState.FaultHold);
        await rig.WaitForCountAsync(CanOpenProfileRig.Emcy, emcys + 1);
        rig.Settle();
        rig.Device.State.Should().Be(Cia408State.FaultHold);
        rig.Od.ReadUnsigned(0x6041, 0x00).Should().Be(0x0003u);
        rig.Device.SetWarning(0x5000, true);
        rig.Settle();
        rig.Od.ReadUnsigned(0x6041, 0x00).Should().Be(0x0023u, "W, status bit 5");

        await rig.ResetAsync(NmtCommand.ResetNode);
        await rig.FlushBackgroundReportsAsync();

        reports.Should().Be(0, "Reset Node rewrites only what the device has");
        rig.Od.ContainsIndex(0x6044).Should().BeFalse();
        rig.Device.State.Should().Be(Cia408State.FaultInit, "the cause the application still reports is entered again");
    }

    [Fact]
    public void Without_6043h_The_Process_Model_Runs_In_The_Default_Control_Mode()
    {
        var model = new ControlModeRecorder();
        using var rig = new Cia408Rig(new[] { Cia408Device.FromDescription(0, model) }, Fixture.Remove(0x6043).Parse());
        rig.Settle();

        rig.Advance(Cia408Configuration.DefaultProcessInterval);

        model.Seen.Should().NotBeEmpty();
        model.Seen.Should().OnlyContain(mode => mode == Cia408ControlMode.PQControl, "pQ is the one mode the file's device type announces");
    }

    private sealed class ControlModeRecorder : ICia408Process
    {
        private readonly object _gate = new();
        private readonly System.Collections.Generic.List<Cia408ControlMode> _seen = new();

        internal Cia408ControlMode[] Seen
        {
            get
            {
                lock (_gate) return _seen.ToArray();
            }
        }

        public void Step(Cia408ProcessContext context)
        {
            lock (_gate) _seen.Add(context.ControlMode);
        }
    }

    // ---- what fails the open -----------------------------------------------------------------------

    [Theory]
    [InlineData((ushort)0x6040, "9.3.2")]
    [InlineData((ushort)0x6041, "9.3.3")]
    [InlineData((ushort)0x6042, "9.3.4")]
    public void A_File_Without_A_Mandatory_Object_Of_Clause_9_3_Fails_The_Open(ushort index, string clause)
    {
        var open = () => Open(Fixture.Remove(index));

        open.Should().Throw<ArgumentException>()
            .Where(e => e.ParamName == "options")
            .Where(e => e.Message.Contains($"{index:X4}h") && e.Message.Contains("§" + clause) && e.Message.Contains("mandatory"));
    }

    [Fact]
    public void A_32_Bit_Device_Type_Requires_The_32_Bit_Pair()
    {
        // dtprs 10b (§8.6.2, Table 53) with pQ, whose mapping has no 32-bit row: nothing else is needed.
        var open = () => Open(Fixture.Set("1000", "DefaultValue", "0x42000198").Remove(0x6044));

        open.Should().Throw<ArgumentException>()
            .Where(e => e.ParamName == "options")
            .Where(e => e.Message.Contains("6044h") && e.Message.Contains("§9.3.6"));
    }

    [Theory]
    [InlineData((ushort)0x6045, "6044h", "6045h")]
    [InlineData((ushort)0x6044, "6045h", "6044h")]
    public void A_File_That_Declares_One_Half_Of_The_32_Bit_Pair_Fails_The_Open(ushort removed, string kept, string missing)
    {
        var open = () => Open(Fixture.Remove(removed));

        open.Should().Throw<ArgumentException>()
            .Where(e => e.ParamName == "options")
            .Where(e => e.Message.Contains(kept) && e.Message.Contains(missing) && e.Message.Contains("pair"));
    }

    [Fact]
    public void A_16_Bit_Device_Type_Needs_No_32_Bit_Pair()
    {
        using var rig = Open(Fixture.Remove(0x6044).Remove(0x6045));

        rig.Od.ContainsIndex(0x6044).Should().BeFalse();
        rig.Device.State.Should().Be(Cia408State.Init);
    }

    [Fact]
    public void A_Device_Type_With_The_Reserved_Resolution_Fails_The_Open()
    {
        var open = () => Open(Fixture.Set("1000", "DefaultValue", "0x02000198"));

        open.Should().Throw<ArgumentException>()
            .Where(e => e.ParamName == "options")
            .Where(e => e.Message.Contains("00b"));
    }

    [Theory]
    [InlineData("6040", "DataType", "0x0007", "6040h", "Unsigned16")]   // the control word as UNSIGNED32
    [InlineData("6042", "DataType", "0x0005", "6042h", "Integer8")]     // the device mode as UNSIGNED8
    [InlineData("6300sub1", "DataType", "0x0004", "6300h", "Integer16")] // the value of a record as INTEGER32
    [InlineData("6301sub2", "DataType", "0x0006", "6301h", "Unsigned8")] // the unit as UNSIGNED16
    [InlineData("6381sub3", "DataType", "0x0005", "6381h", "Integer8")]  // the prefix as UNSIGNED8
    public void A_Catalogue_Object_Declared_With_Another_Type_Fails_The_Open(
        string section, string key, string value, string index, string expected)
    {
        var open = () => Open(Fixture.Set(section, key, value));

        open.Should().Throw<ArgumentException>()
            .Where(e => e.ParamName == "options")
            .Where(e => e.Message.Contains(index) && e.Message.Contains(expected));
    }

    [Fact]
    public void A_Value_Record_Declared_As_A_Variable_Fails_The_Open()
    {
        var open = () => Open(Fixture.Remove(0x6300)
            .Add("[6300]\nParameterName=q setpoint\nObjectType=0x7\nDataType=0x0003\nAccessType=rw\nDefaultValue=0\nPDOMapping=1\n"));

        open.Should().Throw<ArgumentException>()
            .Where(e => e.ParamName == "options")
            .Where(e => e.Message.Contains("6300h") && e.Message.Contains("value record"));
    }

    [Fact]
    public void A_Variable_Declared_As_A_Record_Fails_The_Open()
    {
        var open = () => Open(Fixture.Remove(0x6042).Add(
            "[6042]\nParameterName=Device mode\nSubNumber=2\nObjectType=0x9\n\n"
            + "[6042sub0]\nParameterName=Highest sub-index supported\nObjectType=0x7\nDataType=0x0005\nAccessType=ro\nDefaultValue=1\nPDOMapping=0\n\n"
            + "[6042sub1]\nParameterName=Device mode\nObjectType=0x7\nDataType=0x0002\nAccessType=rw\nDefaultValue=1\nPDOMapping=0\n"));

        open.Should().Throw<ArgumentException>()
            .Where(e => e.ParamName == "options")
            .Where(e => e.Message.Contains("6042h") && e.Message.Contains("VAR"));
    }

    [Fact]
    public void A_Control_Mode_Announced_Without_Its_Objects_Fails_The_Open()
    {
        // pQ (bit 25) needs the pressure setpoint 6380h with the flow setpoint (Table 32, mapping 4).
        var open = () => Open(Fixture.Remove(0x6380));

        open.Should().Throw<ArgumentException>()
            .Where(e => e.ParamName == "options")
            .Where(e => e.Message.Contains("6380h") && e.Message.Contains("control mode 5"));
    }

    [Fact]
    public void The_File_Must_Declare_The_408_Error_Behaviour_Record()
    {
        var open = () => Open(Fixture.RemoveSection("1029sub2").Set("1029", "SubNumber", "2"));

        open.Should().Throw<ArgumentException>()
            .Where(e => e.ParamName == "options")
            .Where(e => e.Message.Contains("1029h") && e.Message.Contains("02h"));
    }

    [Fact]
    public void A_Configured_Device_Refuses_A_File_That_Declares_Its_Objects()
    {
        var open = () => new Cia408Rig(new[] { new Cia408Device(Cia408ToolRig.FixtureConfiguration) }, Fixture.Parse());

        open.Should().Throw<ArgumentException>()
            .Where(e => e.ParamName == "options")
            .Where(e => e.Message.Contains("FromDescription"));
    }

    [Fact]
    public void FromDescription_Refuses_A_Logical_Device_Outside_0_To_7()
    {
        FluentActions.Invoking(() => Cia408Device.FromDescription(8)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => Cia408Device.FromDescription(-1)).Should().Throw<ArgumentOutOfRangeException>();
    }

    // ---- modes from the file ------------------------------------------------------------------------

    [Fact]
    public async Task Bit_23_Gives_The_Closed_Loop_Mode_Where_The_File_Declares_Its_Actual_Value()
    {
        // Bit 23 alone names modes 1 and 2 (§8.6.2, Table 53); 6301h, the actual value of closed loop
        // (§9.8.3), is declared, so both are supported, and 6043h may switch between them in INIT.
        using var rig = Open(Fixture.Set("1000", "DefaultValue", "0x20800198")
            .Set("6043", "AccessType", "rw").Set("6043", "DefaultValue", "2"));

        await rig.WriteU8Async(0x6043, 0x00, 1);
        AbortCode(await rig.DownloadU8Async(0x6043, 0x00, 5)).Should().Be(ValueRangeExceeded, "pQ is not announced");
        await rig.WriteU8Async(0x6043, 0x00, 2);
    }

    [Fact]
    public void Without_Its_Actual_Value_Bit_23_Gives_The_Open_Loop_Mode_Only()
    {
        var open = () => Open(Fixture.Set("1000", "DefaultValue", "0x20800198")
            .Remove(0x6301).Remove(0x1801).Remove(0x1A01)
            .Set("6043", "DefaultValue", "2"));

        open.Should().Throw<ArgumentException>()
            .Where(e => e.ParamName == "options")
            .Where(e => e.Message.Contains("6043h") && e.Message.Contains("value 2"));
    }

    [Fact]
    public async Task A_Writable_Device_Mode_Takes_What_Table_79_Gives_A_Valve_And_The_Files_Own_Value()
    {
        using var rig = Open(Fixture.Set("6042", "DefaultValue", "-1"));

        rig.Od.ReadRaw(0x6042, 0x00).Should().Equal(new byte[] { 0xFF }, "the file's value, a manufacturer-specific mode");
        await rig.WriteU8Async(0x6042, 0x00, 2);
        AbortCode(await rig.DownloadU8Async(0x6042, 0x00, 3)).Should().Be(ValueRangeExceeded, "3 to 6 are drive modes (Table 79)");
        await rig.WriteU8Async(0x6042, 0x00, 0xFF);
    }

    // ---- logical devices -------------------------------------------------------------------------------

    [Fact]
    public async Task A_Description_Device_Of_Logical_Device_1_Takes_Its_Objects_From_6840h()
    {
        // Two logical devices: the file's 1000h announces further ones and 6FFFh gives the second its
        // type (CiA 301 v4.2.0 §7.5.2.1); the PDO records of logical device 0 are left out.
        var eds = Fixture.Remove(0x1400).Remove(0x1401).Remove(0x1600).Remove(0x1601)
            .Remove(0x1800).Remove(0x1801).Remove(0x1A00).Remove(0x1A01)
            .Set("1000", "DefaultValue", "0xFFFF0198")
            .Set("6043", "AccessType", "rw")
            .Add("[6FFF]\nParameterName=Device type\nObjectType=0x7\nDataType=0x0007\nAccessType=ro\nDefaultValue=0x22000198\nPDOMapping=0\n");
        foreach (ushort index in new ushort[] { 0x6040, 0x6041, 0x6042, 0x6043, 0x6044, 0x6045, 0x604E, 0x604F, 0x6300, 0x6301, 0x6380, 0x6381 })
            eds.Move(index, (ushort)(index + 0x800));
        var configured = new Cia408Device(new Cia408Configuration
        {
            Resolution = Cia408Resolution.Valve16,
            ControlModes = new[] { Cia408ControlMode.SpoolPositionClosedLoop },
        });
        var described = Cia408Device.FromDescription(1);
        using var rig = new Cia408Rig(new[] { configured, described }, eds.Parse());

        rig.Od.ReadUnsigned(0x6FFF, 0x00).Should().Be(0x2200_0198u);
        // 6FFFh announces pQ alone; the objects would allow modes 1 to 5 as well (see
        // Without_A_Device_Type_The_Device_Composes_One_From_Its_Objects), so this tells that the device
        // read its type from 6FFFh.
        AbortCode(await rig.DownloadU8Async(0x6843, 0x00, 1)).Should().Be(ValueRangeExceeded);
        await rig.WriteU16Async(0x6840, 0x00, 0x0007);
        rig.Settle();

        described.State.Should().Be(Cia408State.Active);
        configured.State.Should().Be(Cia408State.Init, "logical device 0 is the configured one");
        rig.Od.ReadUnsigned(0x6841, 0x00).Should().Be(0x000Fu);
        rig.Od.ReadUnsigned(0x6041, 0x00).Should().Be(0x0008u);
    }

    private static uint AbortCode(byte[] response) => CanOpenProfileRig.AbortCode(response);
}
