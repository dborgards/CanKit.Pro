using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Cia408;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Cia408;

/// <summary>
/// What a CiA 408 device with a given resolution and set of control modes is, as pure computation:
/// its device-type word (CiA 408 v2.2.0 §8.6.2, Table 53) and the default PDOs that follow from it
/// (§8.4, Table 13; §8.5, Table 32). Every word, index, sub-index and bit length is written out here
/// from the norm — mapping entries in the norm's own packed notation, <c>6041 00 10h</c> =
/// <c>0x60410010</c> — and not taken from <see cref="Cia408Objects"/>, so a wrong one in the source
/// cannot agree with itself.
/// </summary>
/// <remarks>
/// Control modes travel through the theories as their <c>6043h</c> values and resolutions as member
/// names: the enums are internal, and a theory method of a public class cannot take them.
/// </remarks>
public class Cia408ConfigurationTests
{
    private const uint ControlWord16 = 0x60400010;
    private const uint StatusWord16 = 0x60410010;
    private const uint ControlWord32 = 0x60440020;
    private const uint StatusWord32 = 0x60450020;

    private static readonly int[] EveryValveMode = [1, 2, 3, 4, 5, 13, 14, 15];
    private static readonly int[] EveryMode = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    private static Cia408Configuration Config(string resolution, int[] modes, int logicalDevice = 0) => new()
    {
        Resolution = (Cia408Resolution)Enum.Parse(typeof(Cia408Resolution), resolution),
        ControlModes = modes.Select(m => (Cia408ControlMode)m).ToArray(),
        LogicalDevice = logicalDevice,
    };

    private static uint Packed((ushort Index, byte Subindex, byte Bits) entry)
        => ((uint)entry.Index << 16) | ((uint)entry.Subindex << 8) | entry.Bits;

    private static uint[] Entries(Cia408Configuration configuration, int number, bool isTpdo)
    {
        var found = configuration.DefaultPdos.Where(p => p.Number == number && p.IsTpdo == isTpdo).ToList();
        found.Should().ContainSingle($"{(isTpdo ? "TPDO" : "RPDO")} {number} has a default");
        return found[0].Entries.Select(Packed).ToArray();
    }

    private static bool Has(Cia408Configuration configuration, int number, bool isTpdo)
        => configuration.DefaultPdos.Any(p => p.Number == number && p.IsTpdo == isTpdo);

    // ---- the enums carry the norm's values, Tables 79 and 82 ------------------------------------

    [Theory]
    [InlineData("SpoolPositionOpenLoop", 1)]
    [InlineData("SpoolPositionClosedLoop", 2)]
    [InlineData("PressureOpenLoop", 3)]
    [InlineData("PressureClosedLoop", 4)]
    [InlineData("PQControl", 5)]
    [InlineData("OpenLoopMovement", 6)]
    [InlineData("VelocityControlAxis", 7)]
    [InlineData("PressureControlAxis", 8)]
    [InlineData("PositionControlAxis", 9)]
    [InlineData("PositionalDependentDeceleration", 10)]
    [InlineData("PositionControlAxisWithPressureOverride", 11)]
    [InlineData("VelocityControlAxisWithPressureOverride", 12)]
    [InlineData("FlowControl", 13)]
    [InlineData("PFlowControl", 14)]
    [InlineData("PFlowWithPowerLimitation", 15)]
    public void The_Control_Modes_Carry_The_Values_Of_6043h(string name, int value)
        => ((int)(Cia408ControlMode)Enum.Parse(typeof(Cia408ControlMode), name)).Should().Be(value);

    [Fact]
    public void The_Control_Modes_Are_The_Fifteen_Of_Table_82_And_No_Other()
        => Enum.GetValues(typeof(Cia408ControlMode)).Cast<Cia408ControlMode>().Select(m => (int)m).Should().Equal(EveryMode);

    [Theory]
    [InlineData("NoDeviceMode", 0)]
    [InlineData("SetpointViaBus", 1)]
    [InlineData("SetpointLocal", 2)]
    [InlineData("InstallMode", 3)]
    [InlineData("ReferenceMode", 4)]
    [InlineData("Automatic", 5)]
    [InlineData("AutomaticSingleStep", 6)]
    public void The_Device_Modes_Carry_The_Values_Of_6042h(string name, int value)
        => ((int)(Cia408DeviceMode)Enum.Parse(typeof(Cia408DeviceMode), name)).Should().Be(value);

    [Fact]
    public void The_Device_Modes_Are_The_Seven_Of_Table_79_And_No_Other()
        => Enum.GetValues(typeof(Cia408DeviceMode)).Cast<Cia408DeviceMode>().Select(m => (int)m).Should().Equal(0, 1, 2, 3, 4, 5, 6);

    // ---- device type, §8.6.2 Table 53 ------------------------------------------------------------

    [Theory]
    [InlineData("Valve16", new[] { 1, 2 }, 0x20800198u)]           // 16-bit Q valve: bit 23, dtprs 01b
    [InlineData("Valve16", new[] { 1 }, 0x20800198u)]
    [InlineData("Valve16", new[] { 2 }, 0x20800198u)]
    [InlineData("Valve16", new[] { 3, 4 }, 0x21000198u)]           // bit 24
    [InlineData("Valve16", new[] { 4 }, 0x21000198u)]
    [InlineData("Valve16", new[] { 5 }, 0x22000198u)]              // pQ valve: bit 25
    [InlineData("Valve16", new[] { 13 }, 0x24000198u)]             // bit 26
    [InlineData("Valve16", new[] { 14 }, 0x28000198u)]             // bit 27
    [InlineData("Valve16", new[] { 15 }, 0x30000198u)]             // bit 28
    [InlineData("Valve32Drive32", new[] { 6 }, 0x40010198u)]       // bit 16, dtprs 10b
    [InlineData("Valve32Drive32", new[] { 7 }, 0x40020198u)]       // bit 17
    [InlineData("Valve32Drive32", new[] { 8 }, 0x40040198u)]       // bit 18
    [InlineData("Valve32Drive32", new[] { 9 }, 0x40080198u)]       // 32-bit position drive: bit 19
    [InlineData("Valve32Drive32", new[] { 10 }, 0x40100198u)]      // bit 20
    [InlineData("Valve32Drive32", new[] { 11 }, 0x40200198u)]      // bit 21
    [InlineData("Valve32Drive32", new[] { 12 }, 0x40400198u)]      // bit 22
    [InlineData("Valve32Drive32", new[] { 2 }, 0x40800198u)]       // a 32-bit valve
    [InlineData("Valve16Drive32", new[] { 9 }, 0x60080198u)]       // dtprs 11b
    [InlineData("Valve16Drive32", new[] { 2, 9 }, 0x60880198u)]
    [InlineData("Valve16", new[] { 1, 2, 3, 4, 5, 13, 14, 15 }, 0x3F800198u)]
    [InlineData("Valve32Drive32", new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 }, 0x5FFF0198u)]
    [InlineData("Valve16Drive32", new[] { 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1 }, 0x7FFF0198u)]
    public void The_Device_Type_Names_Profile_408_The_Control_Modes_And_The_Resolution(
        string resolution, int[] modes, uint expected)
    {
        var deviceType = Config(resolution, modes).DeviceType;

        deviceType.Value.Should().Be(expected);
        deviceType.ProfileNumber.Should().Be(408);
        deviceType.HasFurtherLogicalDevices.Should().BeFalse();
        (deviceType.Value & 0x80000000u).Should().Be(0u, "bit 31 is reserved");
    }

    [Fact]
    public void The_Device_Type_Is_The_Same_In_Every_Logical_Device()
    {
        Config("Valve16", [1, 2], logicalDevice: 1).DeviceType.Value.Should().Be(0x20800198u);
        Config("Valve32Drive32", [9], logicalDevice: 7).DeviceType.Value.Should().Be(0x40080198u);
    }

    // ---- default PDOs, §8.4 Table 13 and §8.5 Table 32 ---------------------------------------------

    /// <summary>One row per control mode and resolution the mode can run in, the mode being the only
    /// (and so the default) one: PDO 1 carries mapping 1, PDO 2 the mapping of the mode where the
    /// table has a row for it in the width in use, <see langword="null"/> where it has none. The last
    /// two columns are the number of default PDOs and the required PDO count.</summary>
    [Theory]
    // valve modes, 16-bit (Valve16 and Valve16Drive32 run a valve mode in 16 bit alike)
    [InlineData(1, "Valve16", ControlWord16, StatusWord16, new uint[] { ControlWord16, 0x63000110 }, null, 3, 2)]
    [InlineData(1, "Valve16Drive32", ControlWord16, StatusWord16, new uint[] { ControlWord16, 0x63000110 }, null, 3, 2)]
    [InlineData(2, "Valve16", ControlWord16, StatusWord16, new uint[] { ControlWord16, 0x63000110 }, new uint[] { StatusWord16, 0x63010110 }, 4, 2)]
    [InlineData(2, "Valve16Drive32", ControlWord16, StatusWord16, new uint[] { ControlWord16, 0x63000110 }, new uint[] { StatusWord16, 0x63010110 }, 4, 2)]
    [InlineData(3, "Valve16", ControlWord16, StatusWord16, new uint[] { ControlWord16, 0x63800110 }, null, 3, 2)]
    [InlineData(3, "Valve16Drive32", ControlWord16, StatusWord16, new uint[] { ControlWord16, 0x63800110 }, null, 3, 2)]
    [InlineData(4, "Valve16", ControlWord16, StatusWord16, new uint[] { ControlWord16, 0x63800110 }, new uint[] { StatusWord16, 0x63810110 }, 4, 2)]
    [InlineData(4, "Valve16Drive32", ControlWord16, StatusWord16, new uint[] { ControlWord16, 0x63800110 }, new uint[] { StatusWord16, 0x63810110 }, 4, 2)]
    [InlineData(5, "Valve16", ControlWord16, StatusWord16, new uint[] { ControlWord16, 0x63000110, 0x63800110 }, new uint[] { StatusWord16, 0x63010110, 0x63810110 }, 4, 2)]
    [InlineData(5, "Valve16Drive32", ControlWord16, StatusWord16, new uint[] { ControlWord16, 0x63000110, 0x63800110 }, new uint[] { StatusWord16, 0x63010110, 0x63810110 }, 4, 2)]
    [InlineData(13, "Valve16", ControlWord16, StatusWord16, new uint[] { ControlWord16, 0x67000110 }, new uint[] { StatusWord16, 0x67010110 }, 4, 2)]
    [InlineData(13, "Valve16Drive32", ControlWord16, StatusWord16, new uint[] { ControlWord16, 0x67000110 }, new uint[] { StatusWord16, 0x67010110 }, 4, 2)]
    [InlineData(14, "Valve16", ControlWord16, StatusWord16, new uint[] { ControlWord16, 0x67000110, 0x63800110 }, new uint[] { StatusWord16, 0x67010110, 0x63810110 }, 4, 2)]
    [InlineData(14, "Valve16Drive32", ControlWord16, StatusWord16, new uint[] { ControlWord16, 0x67000110, 0x63800110 }, new uint[] { StatusWord16, 0x67010110, 0x63810110 }, 4, 2)]
    [InlineData(15, "Valve16", ControlWord16, StatusWord16, null, null, 2, 1)]
    [InlineData(15, "Valve16Drive32", ControlWord16, StatusWord16, null, null, 2, 1)]
    // valve modes, 32-bit; mappings 4 (pQ), TPDO 9 and RPDO 10 (pFlow) have no 32-bit row
    [InlineData(1, "Valve32Drive32", ControlWord32, StatusWord32, new uint[] { ControlWord32, 0x63080120 }, null, 3, 2)]
    [InlineData(2, "Valve32Drive32", ControlWord32, StatusWord32, new uint[] { ControlWord32, 0x63080120 }, new uint[] { StatusWord32, 0x63090120 }, 4, 2)]
    [InlineData(3, "Valve32Drive32", ControlWord32, StatusWord32, new uint[] { ControlWord32, 0x63880120 }, null, 3, 2)]
    [InlineData(4, "Valve32Drive32", ControlWord32, StatusWord32, new uint[] { ControlWord32, 0x63880120 }, new uint[] { StatusWord32, 0x63890120 }, 4, 2)]
    [InlineData(5, "Valve32Drive32", ControlWord32, StatusWord32, null, null, 2, 1)]
    [InlineData(13, "Valve32Drive32", ControlWord32, StatusWord32, new uint[] { ControlWord32, 0x67080120 }, new uint[] { StatusWord32, 0x67090120 }, 4, 2)]
    [InlineData(14, "Valve32Drive32", ControlWord32, StatusWord32, null, null, 2, 1)]
    [InlineData(15, "Valve32Drive32", ControlWord32, StatusWord32, null, null, 2, 1)]
    // drive modes, 32-bit in both resolutions that have a drive
    [InlineData(6, "Valve32Drive32", ControlWord32, StatusWord32, new uint[] { ControlWord32, 0x64800120 }, null, 3, 2)]
    [InlineData(6, "Valve16Drive32", ControlWord32, StatusWord32, new uint[] { ControlWord32, 0x64800120 }, null, 3, 2)]
    [InlineData(7, "Valve32Drive32", ControlWord32, StatusWord32, new uint[] { ControlWord32, 0x65000120 }, new uint[] { StatusWord32, 0x65010120 }, 4, 2)]
    [InlineData(7, "Valve16Drive32", ControlWord32, StatusWord32, new uint[] { ControlWord32, 0x65000120 }, new uint[] { StatusWord32, 0x65010120 }, 4, 2)]
    [InlineData(8, "Valve32Drive32", ControlWord32, StatusWord32, new uint[] { ControlWord32, 0x65800120 }, new uint[] { StatusWord32, 0x65810120 }, 4, 2)]
    [InlineData(8, "Valve16Drive32", ControlWord32, StatusWord32, new uint[] { ControlWord32, 0x65800120 }, new uint[] { StatusWord32, 0x65810120 }, 4, 2)]
    [InlineData(9, "Valve32Drive32", ControlWord32, StatusWord32, new uint[] { ControlWord32, 0x66000120 }, new uint[] { StatusWord32, 0x66010120 }, 4, 2)]
    [InlineData(9, "Valve16Drive32", ControlWord32, StatusWord32, new uint[] { ControlWord32, 0x66000120 }, new uint[] { StatusWord32, 0x66010120 }, 4, 2)]
    [InlineData(10, "Valve32Drive32", ControlWord32, StatusWord32, new uint[] { ControlWord32, 0x66000120 }, new uint[] { StatusWord32, 0x66010120 }, 4, 2)]
    [InlineData(10, "Valve16Drive32", ControlWord32, StatusWord32, new uint[] { ControlWord32, 0x66000120 }, new uint[] { StatusWord32, 0x66010120 }, 4, 2)]
    [InlineData(11, "Valve32Drive32", ControlWord32, StatusWord32, null, null, 2, 1)]
    [InlineData(11, "Valve16Drive32", ControlWord32, StatusWord32, null, null, 2, 1)]
    [InlineData(12, "Valve32Drive32", ControlWord32, StatusWord32, null, null, 2, 1)]
    [InlineData(12, "Valve16Drive32", ControlWord32, StatusWord32, null, null, 2, 1)]
    public void The_Default_PDOs_Are_Mapping_1_And_The_Mapping_Of_The_Default_Control_Mode(
        int mode, string resolution, uint controlWord, uint statusWord, uint[]? rpdo2, uint[]? tpdo2,
        int pdoCount, int requiredPdoCount)
    {
        var configuration = Config(resolution, [mode]);

        Entries(configuration, 1, isTpdo: false).Should().Equal(controlWord);
        Entries(configuration, 1, isTpdo: true).Should().Equal(statusWord);

        if (rpdo2 is null)
        {
            Has(configuration, 2, isTpdo: false).Should().BeFalse($"Table 32 has no row for control mode {mode} in this width");
        }
        else
        {
            Entries(configuration, 2, isTpdo: false).Should().Equal(rpdo2);
        }

        if (tpdo2 is null)
        {
            Has(configuration, 2, isTpdo: true).Should().BeFalse($"Table 13 has no row for control mode {mode} in this width");
        }
        else
        {
            Entries(configuration, 2, isTpdo: true).Should().Equal(tpdo2);
        }

        configuration.DefaultPdos.Should().HaveCount(pdoCount);
        configuration.RequiredPdoCount.Should().Be(requiredPdoCount);
    }

    [Fact]
    public void The_Default_PDOs_Are_Ordered_By_Number_Receive_First()
    {
        Config("Valve16", [2]).DefaultPdos.Select(p => (p.Number, p.IsTpdo)).Should().Equal(
            (1, false), (1, true), (2, false), (2, true));
        Config("Valve16", [1]).DefaultPdos.Select(p => (p.Number, p.IsTpdo)).Should().Equal(
            (1, false), (1, true), (2, false));
    }

    [Fact]
    public void Only_The_First_Control_Mode_Decides_The_Layout()
    {
        // The first control mode is the default of 6043h. A Valve16Drive32 device runs a valve mode
        // in 16 bit and a drive mode in 32 bit, so the order of the same two modes swaps both pairs.
        var driveFirst = Config("Valve16Drive32", [9, 2]);
        Entries(driveFirst, 1, isTpdo: false).Should().Equal(ControlWord32);
        Entries(driveFirst, 1, isTpdo: true).Should().Equal(StatusWord32);
        Entries(driveFirst, 2, isTpdo: false).Should().Equal(ControlWord32, 0x66000120u);
        Entries(driveFirst, 2, isTpdo: true).Should().Equal(StatusWord32, 0x66010120u);

        var valveFirst = Config("Valve16Drive32", [2, 9]);
        Entries(valveFirst, 1, isTpdo: false).Should().Equal(ControlWord16);
        Entries(valveFirst, 1, isTpdo: true).Should().Equal(StatusWord16);
        Entries(valveFirst, 2, isTpdo: false).Should().Equal(ControlWord16, 0x63000110u);
        Entries(valveFirst, 2, isTpdo: true).Should().Equal(StatusWord16, 0x63010110u);

        // A supported mode without a row does not take a PDO either.
        var noRowFirst = Config("Valve16", [15, 2]);
        noRowFirst.DefaultPdos.Select(p => p.Number).Should().OnlyContain(n => n == 1);
    }

    // ---- logical devices, CiA 301 v4.2.0 §7.2.2.1 and §7.4.1 ---------------------------------------

    [Fact]
    public void Logical_Device_One_Shifts_The_PDO_Numbers_By_64_And_The_Indices_By_800h()
    {
        var drive = Config("Valve32Drive32", [9], logicalDevice: 1);

        drive.DefaultPdos.Select(p => (p.Number, p.IsTpdo)).Should().Equal(
            (65, false), (65, true), (66, false), (66, true));
        Entries(drive, 65, isTpdo: false).Should().Equal(0x68440020u);
        Entries(drive, 65, isTpdo: true).Should().Equal(0x68450020u);
        Entries(drive, 66, isTpdo: false).Should().Equal(0x68440020u, 0x6E000120u);
        Entries(drive, 66, isTpdo: true).Should().Equal(0x68450020u, 0x6E010120u);
        drive.RequiredPdoCount.Should().Be(66);

        var valve = Config("Valve16", [2], logicalDevice: 1);
        Entries(valve, 65, isTpdo: false).Should().Equal(0x68400010u);
        Entries(valve, 66, isTpdo: false).Should().Equal(0x68400010u, 0x6B000110u);
        Entries(valve, 66, isTpdo: true).Should().Equal(0x68410010u, 0x6B010110u);
    }

    [Fact]
    public void Logical_Device_Seven_Reaches_The_Last_Area()
    {
        // 6040h + 7 * 800h = 9840h, 6700h + 7 * 800h = 9F00h; PDO 2 is 7 * 64 + 2 = 450.
        var configuration = Config("Valve16", [13], logicalDevice: 7);

        Entries(configuration, 450, isTpdo: false).Should().Equal(0x98400010u, 0x9F000110u);
        Entries(configuration, 450, isTpdo: true).Should().Equal(0x98410010u, 0x9F010110u);
        configuration.RequiredPdoCount.Should().Be(450);
    }

    [Fact]
    public void A_Device_Without_A_Second_Mapping_Needs_One_PDO_Plus_The_Offset()
        => Config("Valve16", [15], logicalDevice: 1).RequiredPdoCount.Should().Be(65);

    [Fact]
    public void The_Entries_Of_A_Default_Pdo_Cannot_Be_Written_Through_A_Cast()
    {
        foreach (var pdo in Config("Valve16", [5]).DefaultPdos)
        {
            var collection = pdo.Entries.Should().BeAssignableTo<ICollection<(ushort Index, byte Subindex, byte Bits)>>().Subject;
            collection.IsReadOnly.Should().BeTrue();
            ((Action)(() => collection.Add(((ushort)0x6040, (byte)0, (byte)16)))).Should().Throw<NotSupportedException>();
            pdo.Entries.Should().NotBeAssignableTo<List<(ushort Index, byte Subindex, byte Bits)>>();
        }
    }

    [Fact]
    public void The_List_Of_Default_Pdos_Cannot_Be_Written_Through_A_Cast()
    {
        var pdos = Config("Valve16", [2]).DefaultPdos;

        var collection = pdos.Should().BeAssignableTo<ICollection<Cia408DefaultPdo>>().Subject;
        collection.IsReadOnly.Should().BeTrue();
        ((Action)(() => collection.Clear())).Should().Throw<NotSupportedException>();
        pdos.Should().NotBeAssignableTo<List<Cia408DefaultPdo>>();
    }

    // ---- Validate ------------------------------------------------------------------------------------

    [Fact]
    public void The_Device_Modes_Default_To_Setpoint_Via_Bus()
        => new Cia408Configuration().DeviceModes.Should().Equal(Cia408DeviceMode.SetpointViaBus);

    [Fact]
    public void No_Control_Mode_Is_Rejected()
    {
        var act = () => Config("Valve16", []).Validate();

        act.Should().ThrowExactly<ArgumentException>().WithMessage("*at least one control mode*");
    }

    [Fact]
    public void Missing_Lists_Are_Rejected_And_Named()
    {
        var noControlModes = () => new Cia408Configuration { Resolution = Cia408Resolution.Valve16, ControlModes = null! }.Validate();
        noControlModes.Should().ThrowExactly<ArgumentNullException>().Which.ParamName.Should().Be("ControlModes");

        var noDeviceModes = () => new Cia408Configuration
        {
            Resolution = Cia408Resolution.Valve16,
            ControlModes = [Cia408ControlMode.SpoolPositionOpenLoop],
            DeviceModes = null!,
        }.Validate();
        noDeviceModes.Should().ThrowExactly<ArgumentNullException>().Which.ParamName.Should().Be("DeviceModes");
    }

    [Fact]
    public void A_Control_Mode_Named_Twice_Is_Rejected()
    {
        var act = () => Config("Valve16", [2, 1, 2]).Validate();

        act.Should().ThrowExactly<ArgumentException>().WithMessage("*SpoolPositionClosedLoop*more than once*");
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    public void A_Drive_Mode_On_A_Device_Without_A_Drive_Is_Rejected(int driveMode)
    {
        // Table 53: dtprs 01b is a valve with 16-bit parameters, drive not supported.
        var act = () => Config("Valve16", [2, driveMode]).Validate();

        act.Should().ThrowExactly<ArgumentException>().WithMessage("*drive*Valve16*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(127)]
    [InlineData(-1)]
    [InlineData(-128)]
    public void A_Control_Mode_Outside_Table_82_Is_Rejected_And_Named(int value)
    {
        var act = () => Config("Valve32Drive32", [2, value]).Validate();

        act.Should().ThrowExactly<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("ControlModes");
    }

    [Fact]
    public void No_Device_Mode_Is_Rejected()
    {
        var act = () => new Cia408Configuration
        {
            Resolution = Cia408Resolution.Valve16,
            ControlModes = [Cia408ControlMode.SpoolPositionOpenLoop],
            DeviceModes = [],
        }.Validate();

        act.Should().ThrowExactly<ArgumentException>().WithMessage("*at least one device mode*");
    }

    [Fact]
    public void A_Device_Mode_Named_Twice_Is_Rejected()
    {
        var act = () => new Cia408Configuration
        {
            Resolution = Cia408Resolution.Valve16,
            ControlModes = [Cia408ControlMode.SpoolPositionOpenLoop],
            DeviceModes = [Cia408DeviceMode.SetpointViaBus, Cia408DeviceMode.SetpointLocal, Cia408DeviceMode.SetpointViaBus],
        }.Validate();

        act.Should().ThrowExactly<ArgumentException>().WithMessage("*SetpointViaBus*more than once*");
    }

    [Theory]
    [InlineData(7)]
    [InlineData(127)]
    [InlineData(-1)]
    [InlineData(-128)]
    public void A_Device_Mode_Outside_Table_79_Is_Rejected_And_Named(int value)
    {
        var act = () => new Cia408Configuration
        {
            Resolution = Cia408Resolution.Valve16,
            ControlModes = [Cia408ControlMode.SpoolPositionOpenLoop],
            DeviceModes = [Cia408DeviceMode.SetpointViaBus, (Cia408DeviceMode)value],
        }.Validate();

        act.Should().ThrowExactly<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("DeviceModes");
    }

    [Theory]
    [InlineData(0)]     // dtprs 00b is reserved
    [InlineData(4)]
    [InlineData(-1)]
    public void A_Resolution_Outside_Table_53_Is_Rejected_And_Named(int value)
    {
        var act = () => new Cia408Configuration
        {
            Resolution = (Cia408Resolution)value,
            ControlModes = [Cia408ControlMode.SpoolPositionOpenLoop],
        }.Validate();

        act.Should().ThrowExactly<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("Resolution");
    }

    [Fact]
    public void A_Configuration_Without_A_Resolution_Is_Rejected()
    {
        // The default of the enum is the reserved dtprs 00b: a device has to say what it is.
        var act = () => new Cia408Configuration { ControlModes = [Cia408ControlMode.SpoolPositionOpenLoop] }.Validate();

        act.Should().ThrowExactly<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("Resolution");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(8)]
    public void A_Logical_Device_Out_Of_Range_Is_Rejected_And_Named(int logicalDevice)
    {
        var act = () => Config("Valve16", [1], logicalDevice).Validate();

        act.Should().ThrowExactly<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("LogicalDevice");
    }

    [Fact]
    public void The_Limits_Themselves_Are_Accepted()
    {
        ((Action)(() => Config("Valve16Drive32", EveryMode, logicalDevice: 7).Validate())).Should().NotThrow();
        ((Action)(() => Config("Valve16", EveryValveMode, logicalDevice: 0).Validate())).Should().NotThrow();
        ((Action)(() => new Cia408Configuration
        {
            Resolution = Cia408Resolution.Valve32Drive32,
            ControlModes = [Cia408ControlMode.PositionControlAxis],
            DeviceModes = [Cia408DeviceMode.NoDeviceMode, Cia408DeviceMode.SetpointViaBus, Cia408DeviceMode.SetpointLocal,
                Cia408DeviceMode.InstallMode, Cia408DeviceMode.ReferenceMode, Cia408DeviceMode.Automatic, Cia408DeviceMode.AutomaticSingleStep],
        }.Validate())).Should().NotThrow();
    }

    [Fact]
    public void The_Derived_Values_Validate_The_Configuration_Themselves()
    {
        // A getter that computed from an invalid configuration would answer something that no
        // device could have; each one throws what Validate throws.
        var invalid = Config("Valve16", [9]);

        ((Action)(() => _ = invalid.DeviceType)).Should().ThrowExactly<ArgumentException>();
        ((Action)(() => _ = invalid.DefaultPdos)).Should().ThrowExactly<ArgumentException>();
        ((Action)(() => _ = invalid.RequiredPdoCount)).Should().ThrowExactly<ArgumentException>();

        var empty = new Cia408Configuration();
        ((Action)(() => _ = empty.DeviceType)).Should().Throw<ArgumentException>();
        ((Action)(() => _ = empty.DefaultPdos)).Should().Throw<ArgumentException>();
        ((Action)(() => _ = empty.RequiredPdoCount)).Should().Throw<ArgumentException>();
    }
}
