using System;
using System.Linq;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Cia401;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Cia401;

/// <summary>
/// What a CiA 401 device with a given set of channels is, as pure computation: its device-type word
/// (CiA 401-1 v3.1.0 §6.2.1) and the default PDOs that follow from it (§6.1, §6.2.5 to §6.2.12).
/// Every index, bit and number is written out here from the norm, not taken from
/// <see cref="Cia401Objects"/>, so a wrong one in the source cannot agree with itself.
/// </summary>
public class Cia401ConfigurationTests
{
    private static Cia401Configuration Config(
        int di = 0, int @do = 0, int ai = 0, int ao = 0, int logicalDevice = 0) => new()
        {
            DigitalInputs = di,
            DigitalOutputs = @do,
            AnalogInputs = ai,
            AnalogOutputs = ao,
            LogicalDevice = logicalDevice,
        };

    private static Cia401DefaultPdo Pdo(Cia401Configuration configuration, int number, bool isTpdo)
    {
        var found = configuration.DefaultPdos.Where(p => p.Number == number && p.IsTpdo == isTpdo).ToList();
        found.Should().ContainSingle($"{(isTpdo ? "TPDO" : "RPDO")} {number} has a default");
        return found[0];
    }

    private static bool Has(Cia401Configuration configuration, int number, bool isTpdo)
        => configuration.DefaultPdos.Any(p => p.Number == number && p.IsTpdo == isTpdo);

    // ---- device type, §6.2.1 ------------------------------------------------------------------

    [Theory]
    [InlineData(8, 0, 0, 0, 0x00010191u)]    // bit 16: digital inputs
    [InlineData(0, 8, 0, 0, 0x00020191u)]    // bit 17: digital outputs
    [InlineData(0, 0, 4, 0, 0x00040191u)]    // bit 18: analog inputs
    [InlineData(0, 0, 0, 4, 0x00080191u)]    // bit 19: analog outputs
    [InlineData(1, 1, 1, 1, 0x000F0191u)]
    [InlineData(2032, 2032, 254, 254, 0x000F0191u)]
    [InlineData(8, 0, 4, 0, 0x00050191u)]
    public void The_Device_Type_Names_Profile_401_And_The_Implemented_Functions(
        int di, int @do, int ai, int ao, uint expected)
    {
        var deviceType = Config(di, @do, ai, ao).DeviceType;

        deviceType.Value.Should().Be(expected);
        deviceType.ProfileNumber.Should().Be(401);
        deviceType.FollowsStandardizedProfile.Should().BeTrue();
        deviceType.HasFurtherLogicalDevices.Should().BeFalse();
        deviceType.Should().Be(new CanOpenDeviceType(expected));
    }

    [Fact]
    public void The_Device_Type_Is_The_Same_In_Every_Logical_Device()
    {
        // §6.2.1 describes the functionality of the device the word belongs to; a logical device
        // does not change the word, only where the object sits.
        Config(8, 0, 0, 0, logicalDevice: 3).DeviceType.Value.Should().Be(0x00010191u);
    }

    // ---- digital inputs, TPDO 1, §6.2.6 ---------------------------------------------------------

    [Fact]
    public void Twenty_Digital_Inputs_Fill_Three_Bytes_Of_The_First_TPDO()
    {
        var tpdo = Pdo(Config(di: 20), number: 1, isTpdo: true);

        tpdo.Enabled.Should().BeTrue();
        tpdo.Entries.Should().Equal(
            ((ushort)0x6000, (byte)1, (byte)8),
            ((ushort)0x6000, (byte)2, (byte)8),
            ((ushort)0x6000, (byte)3, (byte)8));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(7, 1)]
    [InlineData(8, 1)]
    [InlineData(9, 2)]      // a started group of eight counts
    [InlineData(16, 2)]
    [InlineData(17, 3)]
    [InlineData(63, 8)]
    [InlineData(64, 8)]
    [InlineData(65, 8)]     // §6.1: beyond 64 channels no default PDO carries the rest
    [InlineData(2032, 8)]
    public void The_First_TPDO_Carries_One_Byte_Per_Started_Group_Of_Eight_Inputs_Up_To_Eight(int inputs, int entries)
    {
        var tpdo = Pdo(Config(di: inputs), number: 1, isTpdo: true);

        tpdo.Entries.Should().HaveCount(entries);
        tpdo.Entries.Select(e => e.Subindex).Should().Equal(Enumerable.Range(1, entries).Select(i => (byte)i));
        tpdo.Entries.Should().OnlyContain(e => e.Index == 0x6000 && e.Bits == 8);
    }

    [Fact]
    public void Digital_Inputs_Alone_Have_No_Other_Default_PDO()
    {
        var configuration = Config(di: 64);

        configuration.DefaultPdos.Should().ContainSingle();
        Has(configuration, 1, isTpdo: true).Should().BeTrue();
    }

    // ---- digital outputs, RPDO 1, §6.2.5 --------------------------------------------------------

    [Fact]
    public void Twenty_Digital_Outputs_Fill_Three_Bytes_Of_The_First_RPDO()
    {
        var rpdo = Pdo(Config(@do: 20), number: 1, isTpdo: false);

        rpdo.Enabled.Should().BeTrue();
        rpdo.Entries.Should().Equal(
            ((ushort)0x6200, (byte)1, (byte)8),
            ((ushort)0x6200, (byte)2, (byte)8),
            ((ushort)0x6200, (byte)3, (byte)8));
    }

    [Theory]
    [InlineData(8, 1)]
    [InlineData(9, 2)]
    [InlineData(64, 8)]
    [InlineData(65, 8)]
    [InlineData(2032, 8)]
    public void The_First_RPDO_Carries_One_Byte_Per_Started_Group_Of_Eight_Outputs_Up_To_Eight(int outputs, int entries)
    {
        var rpdo = Pdo(Config(@do: outputs), number: 1, isTpdo: false);

        rpdo.Entries.Should().HaveCount(entries);
        rpdo.Entries.Should().OnlyContain(e => e.Index == 0x6200 && e.Bits == 8);
    }

    [Fact]
    public void Digital_Outputs_Alone_Have_No_Other_Default_PDO()
    {
        var configuration = Config(@do: 8);

        configuration.DefaultPdos.Should().ContainSingle();
        Has(configuration, 1, isTpdo: false).Should().BeTrue();
    }

    // ---- analog inputs, TPDO 2 to 4, §6.2.8, §6.2.10, §6.2.12 -----------------------------------

    [Fact]
    public void Five_Analog_Inputs_Fill_The_Second_TPDO_And_Start_The_Third_Both_Disabled()
    {
        var configuration = Config(ai: 5);

        var tpdo2 = Pdo(configuration, number: 2, isTpdo: true);
        tpdo2.Enabled.Should().BeFalse();
        tpdo2.Entries.Should().Equal(
            ((ushort)0x6401, (byte)1, (byte)16),
            ((ushort)0x6401, (byte)2, (byte)16),
            ((ushort)0x6401, (byte)3, (byte)16),
            ((ushort)0x6401, (byte)4, (byte)16));

        var tpdo3 = Pdo(configuration, number: 3, isTpdo: true);
        tpdo3.Enabled.Should().BeFalse();
        tpdo3.Entries.Should().Equal(((ushort)0x6401, (byte)5, (byte)16));

        Has(configuration, 4, isTpdo: true).Should().BeFalse();
        configuration.DefaultPdos.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(1, 1, 0, 0)]
    [InlineData(4, 4, 0, 0)]
    [InlineData(5, 4, 1, 0)]
    [InlineData(8, 4, 4, 0)]
    [InlineData(9, 4, 4, 1)]
    [InlineData(12, 4, 4, 4)]
    [InlineData(13, 4, 4, 4)]      // §6.1: beyond twelve no default PDO carries the rest
    [InlineData(254, 4, 4, 4)]
    public void Analog_Inputs_Spread_Over_Up_To_Three_TPDOs_Of_Four_Channels(int inputs, int in2, int in3, int in4)
    {
        var configuration = Config(ai: inputs);
        var expected = new[] { in2, in3, in4 };

        for (var i = 0; i < 3; i++)
        {
            var number = 2 + i;
            if (expected[i] == 0)
            {
                Has(configuration, number, isTpdo: true).Should().BeFalse($"no channel lives in TPDO {number}");
                continue;
            }

            var tpdo = Pdo(configuration, number, isTpdo: true);
            tpdo.Enabled.Should().BeFalse($"TPDO {number} carries analog inputs");
            tpdo.Entries.Should().HaveCount(expected[i]);
            tpdo.Entries.Select(e => e.Subindex).Should().Equal(
                Enumerable.Range(1 + (4 * i), expected[i]).Select(s => (byte)s));
            tpdo.Entries.Should().OnlyContain(e => e.Index == 0x6401 && e.Bits == 16);
        }
    }

    // ---- analog outputs, RPDO 2 to 4, §6.2.7, §6.2.9, §6.2.11 -----------------------------------

    [Theory]
    [InlineData(1, 1, 0, 0)]
    [InlineData(5, 4, 1, 0)]
    [InlineData(9, 4, 4, 1)]
    [InlineData(12, 4, 4, 4)]
    [InlineData(254, 4, 4, 4)]
    public void Analog_Outputs_Spread_Over_Up_To_Three_RPDOs_Of_Four_Channels_All_Enabled(
        int outputs, int out2, int out3, int out4)
    {
        var configuration = Config(ao: outputs);
        var expected = new[] { out2, out3, out4 };

        for (var i = 0; i < 3; i++)
        {
            var number = 2 + i;
            if (expected[i] == 0)
            {
                Has(configuration, number, isTpdo: false).Should().BeFalse($"no channel lives in RPDO {number}");
                continue;
            }

            var rpdo = Pdo(configuration, number, isTpdo: false);
            rpdo.Enabled.Should().BeTrue();
            rpdo.Entries.Should().HaveCount(expected[i]);
            rpdo.Entries.Select(e => e.Subindex).Should().Equal(
                Enumerable.Range(1 + (4 * i), expected[i]).Select(s => (byte)s));
            rpdo.Entries.Should().OnlyContain(e => e.Index == 0x6411 && e.Bits == 16);
        }
    }

    // ---- the whole layout ------------------------------------------------------------------------

    [Fact]
    public void A_Device_With_Everything_Has_Eight_Default_PDOs_Ordered_By_Number_Receive_First()
    {
        var configuration = Config(di: 16, @do: 16, ai: 12, ao: 12);

        configuration.DefaultPdos.Select(p => (p.Number, p.IsTpdo)).Should().Equal(
            (1, false), (1, true),
            (2, false), (2, true),
            (3, false), (3, true),
            (4, false), (4, true));
        configuration.DefaultPdos.Where(p => !p.IsTpdo).Should().OnlyContain(p => p.Enabled);
        configuration.DefaultPdos.Where(p => p.IsTpdo && p.Number == 1).Should().OnlyContain(p => p.Enabled);
        configuration.DefaultPdos.Where(p => p.IsTpdo && p.Number > 1).Should().OnlyContain(p => !p.Enabled);
    }

    [Fact]
    public void An_Analog_Only_Device_Leaves_The_Digital_PDOs_Unused()
    {
        var configuration = Config(ai: 4, ao: 4);

        configuration.DefaultPdos.Select(p => (p.Number, p.IsTpdo)).Should().Equal((2, false), (2, true));
    }

    // ---- required PDO count ----------------------------------------------------------------------

    [Theory]
    [InlineData(8, 0, 0, 0, 1)]
    [InlineData(0, 8, 0, 0, 1)]
    [InlineData(0, 0, 1, 0, 2)]
    [InlineData(0, 0, 0, 1, 2)]
    [InlineData(0, 0, 5, 0, 3)]
    [InlineData(0, 0, 0, 9, 4)]
    [InlineData(2032, 2032, 12, 12, 4)]
    [InlineData(2032, 2032, 254, 254, 4)]
    [InlineData(8, 8, 4, 0, 2)]
    public void The_Required_PDO_Count_Is_The_Highest_Number_With_A_Default(
        int di, int @do, int ai, int ao, int expected)
        => Config(di, @do, ai, ao).RequiredPdoCount.Should().Be(expected);

    [Fact]
    public void The_Required_PDO_Count_Includes_The_Offset_Of_The_Logical_Device()
    {
        Config(di: 8, logicalDevice: 1).RequiredPdoCount.Should().Be(65);
        Config(ao: 5, logicalDevice: 1).RequiredPdoCount.Should().Be(67);
        Config(ai: 12, logicalDevice: 7).RequiredPdoCount.Should().Be((7 * 64) + 4);
    }

    // ---- logical devices, CiA 401-1 §6.2, CiA 301 §7.2.2.1 and §7.4.1 ----------------------------

    [Fact]
    public void Logical_Device_One_Shifts_The_PDO_Numbers_By_64_And_The_Indices_By_800h()
    {
        var configuration = Config(di: 8, @do: 8, ai: 5, ao: 5, logicalDevice: 1);

        var tpdo1 = Pdo(configuration, number: 65, isTpdo: true);
        tpdo1.Enabled.Should().BeTrue();
        tpdo1.Entries.Should().Equal(((ushort)0x6800, (byte)1, (byte)8));

        Pdo(configuration, number: 65, isTpdo: false).Entries.Should()
            .Equal(((ushort)0x6A00, (byte)1, (byte)8));

        var tpdo2 = Pdo(configuration, number: 66, isTpdo: true);
        tpdo2.Enabled.Should().BeFalse();
        tpdo2.Entries.Select(e => e.Index).Should().OnlyContain(i => i == 0x6C01);
        tpdo2.Entries.Should().HaveCount(4);
        Pdo(configuration, number: 67, isTpdo: true).Entries.Should()
            .Equal(((ushort)0x6C01, (byte)5, (byte)16));

        var rpdo2 = Pdo(configuration, number: 66, isTpdo: false);
        rpdo2.Enabled.Should().BeTrue();
        rpdo2.Entries.Select(e => e.Index).Should().OnlyContain(i => i == 0x6C11);
        Pdo(configuration, number: 67, isTpdo: false).Entries.Should()
            .Equal(((ushort)0x6C11, (byte)5, (byte)16));

        configuration.DefaultPdos.Select(p => p.Number).Should().OnlyContain(n => n >= 65 && n <= 128);
    }

    [Fact]
    public void Logical_Device_Seven_Reaches_The_Last_Area_Below_The_General_Device_Object()
    {
        // 6000h + 7 * 800h = 9800h
        var tpdo = Pdo(Config(di: 8, logicalDevice: 7), number: 449, isTpdo: true);

        tpdo.Entries.Should().Equal(((ushort)0x9800, (byte)1, (byte)8));
    }

    // ---- Validate ---------------------------------------------------------------------------------

    [Fact]
    public void Nothing_Implemented_Is_Rejected()
    {
        // §6.2.1: at least one I/O functionality of bits 16 to 19 shall be implemented.
        var act = () => Config().Validate();

        act.Should().Throw<ArgumentException>().WithMessage("*at least one*");
    }

    [Theory]
    [InlineData(-1, 0, 0, 0, "DigitalInputs")]
    [InlineData(2033, 0, 0, 0, "DigitalInputs")]
    [InlineData(1, -1, 0, 0, "DigitalOutputs")]
    [InlineData(1, 2033, 0, 0, "DigitalOutputs")]
    [InlineData(1, 0, -1, 0, "AnalogInputs")]
    [InlineData(1, 0, 255, 0, "AnalogInputs")]
    [InlineData(1, 0, 0, -1, "AnalogOutputs")]
    [InlineData(1, 0, 0, 255, "AnalogOutputs")]
    public void A_Count_Out_Of_Range_Is_Rejected_And_Named(int di, int @do, int ai, int ao, string name)
    {
        var act = () => Config(di, @do, ai, ao).Validate();

        act.Should().Throw<ArgumentException>().Which.Message.Should().Contain(name);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(8)]
    public void A_Logical_Device_Out_Of_Range_Is_Rejected_And_Named(int logicalDevice)
    {
        var act = () => Config(di: 1, logicalDevice: logicalDevice).Validate();

        act.Should().Throw<ArgumentException>().Which.Message.Should().Contain("LogicalDevice");
    }

    [Theory]
    [InlineData(2032, 2032, 254, 254, 7)]
    [InlineData(1, 0, 0, 0, 0)]
    [InlineData(0, 0, 0, 1, 7)]
    public void The_Limits_Themselves_Are_Accepted(int di, int @do, int ai, int ao, int logicalDevice)
    {
        var act = () => Config(di, @do, ai, ao, logicalDevice).Validate();

        act.Should().NotThrow();
    }

    [Fact]
    public void The_Derived_Values_Validate_The_Configuration_Themselves()
    {
        // A getter that computed from an invalid configuration would answer something that no
        // device could have; each one throws what Validate throws.
        var invalid = Config(di: 2033);

        ((Action)(() => _ = invalid.DeviceType)).Should().Throw<ArgumentException>();
        ((Action)(() => _ = invalid.DefaultPdos)).Should().Throw<ArgumentException>();
        ((Action)(() => _ = invalid.RequiredPdoCount)).Should().Throw<ArgumentException>();

        var empty = Config();
        ((Action)(() => _ = empty.DeviceType)).Should().Throw<ArgumentException>();
        ((Action)(() => _ = empty.DefaultPdos)).Should().Throw<ArgumentException>();
        ((Action)(() => _ = empty.RequiredPdoCount)).Should().Throw<ArgumentException>();
    }
}
