using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Cia401;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Cia401;

/// <summary>
/// The CiA 401-1 v3.1.0 object catalogue (clause 8). These tests are the independent check on
/// <see cref="Cia401Objects"/>: every index is written out here, read from the norm and not from
/// the catalogue, so a wrong index, data type or default in the catalogue cannot agree with
/// itself.
/// </summary>
public class Cia401ObjectsTests
{
    private static Cia401ObjectDefinition Get(ushort index)
    {
        Cia401Objects.TryGet(index, out var definition).Should().BeTrue($"{index:X4}h is a CiA 401 object");
        return definition!;
    }

    // ---- shape of the catalogue ---------------------------------------------------------------

    [Theory]
    [InlineData("DigitalInput", 67)]   // 6000h..6128h: 7 + 6 ranges of 8 + 12
    [InlineData("DigitalOutput", 55)]  // 6200h..6328h: 5 + 5 ranges of 8 + 10
    [InlineData("AnalogInput", 23)]    // 6400h..6404h + 6421h..6432h
    [InlineData("AnalogOutput", 13)]   // 6410h..6414h + 6441h..6447h, 6450h
    [InlineData("General", 1)]         // 67FFh
    public void Each_Function_Block_Holds_The_Objects_Of_Its_Clause(string block, int expected)
    {
        // By name: the enum is internal and a public test method cannot take it.
        var parsed = (Cia401FunctionBlock)Enum.Parse(typeof(Cia401FunctionBlock), block);
        Cia401Objects.All.Count(d => d.Block == parsed).Should().Be(expected);
    }

    [Fact]
    public void All_Holds_One_Definition_Per_Index()
    {
        Cia401Objects.All.Should().HaveCount(159);
        Cia401Objects.All.Select(d => d.Index).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Every_Index_Lies_In_The_Profile_Range_And_All_Is_Ordered()
    {
        Cia401Objects.All.Should().OnlyContain(d => d.Index >= 0x6000 && d.Index <= 0x67FF);
        Cia401Objects.All.Select(d => d.Index).Should().BeInAscendingOrder();
    }

    [Theory]
    [InlineData(0x6420)] // reserved for compatibility reasons (§8.6.1)
    [InlineData(0x6440)] // reserved for compatibility reasons (§8.7.1)
    [InlineData(0x6001)]
    [InlineData(0x6004)]
    [InlineData(0x6129)]
    [InlineData(0x6201)]
    [InlineData(0x6405)]
    [InlineData(0x6433)]
    [InlineData(0x6451)]
    [InlineData(0x1000)]
    public void An_Index_The_Norm_Does_Not_Define_Is_Not_In_The_Catalogue(ushort index)
        => Cia401Objects.TryGet(index, out _).Should().BeFalse();

    [Fact]
    public void The_Indices_Of_The_Norm_Are_Exactly_The_Catalogue()
    {
        var expected = new List<int>();
        expected.AddRange(new[] { 0x6000, 0x6002, 0x6003, 0x6005, 0x6006, 0x6007, 0x6008 });
        foreach (int start in new[] { 0x6020, 0x6030, 0x6038, 0x6050, 0x6060, 0x6070 })
            expected.AddRange(Enumerable.Range(start, 8));
        expected.AddRange(new[] { 0x6100, 0x6102, 0x6103, 0x6106, 0x6107, 0x6108 });
        expected.AddRange(new[] { 0x6120, 0x6122, 0x6123, 0x6126, 0x6127, 0x6128 });
        expected.AddRange(new[] { 0x6200, 0x6202, 0x6206, 0x6207, 0x6208 });
        foreach (int start in new[] { 0x6220, 0x6240, 0x6250, 0x6260, 0x6270 })
            expected.AddRange(Enumerable.Range(start, 8));
        expected.AddRange(new[] { 0x6300, 0x6302, 0x6306, 0x6307, 0x6308 });
        expected.AddRange(new[] { 0x6320, 0x6322, 0x6326, 0x6327, 0x6328 });
        expected.AddRange(Enumerable.Range(0x6400, 5));
        expected.AddRange(Enumerable.Range(0x6410, 5));
        expected.AddRange(Enumerable.Range(0x6421, 0x6432 - 0x6421 + 1));
        expected.AddRange(Enumerable.Range(0x6441, 7));
        expected.Add(0x6450);
        expected.Add(0x67FF);

        Cia401Objects.All.Select(d => (int)d.Index).Should().Equal(expected.OrderBy(i => i));
    }

    [Fact]
    public void Every_Named_Member_Is_In_The_Catalogue()
    {
        var members = typeof(Cia401Objects)
            .GetProperties(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(p => p.PropertyType == typeof(Cia401ObjectDefinition))
            .ToList();
        members.Should().NotBeEmpty();
        foreach (var member in members)
            Cia401Objects.All.Should().Contain((Cia401ObjectDefinition)member.GetValue(null)!, member.Name);

        var ranges = typeof(Cia401Objects)
            .GetProperties(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(p => p.PropertyType == typeof(IReadOnlyList<Cia401ObjectDefinition>) && p.Name != nameof(Cia401Objects.All))
            .ToList();
        ranges.Should().HaveCount(11); // six digital input, five digital output
        foreach (var range in ranges)
        {
            var list = (IReadOnlyList<Cia401ObjectDefinition>)range.GetValue(null)!;
            list.Should().HaveCount(8, range.Name);
            Cia401Objects.All.Should().Contain(list, range.Name);
        }
    }

    [Fact]
    public void The_Named_Members_Carry_The_Index_Of_The_Norm()
    {
        Cia401Objects.ReadDigitalInput8.Index.Should().Be(0x6000);
        Cia401Objects.WriteDigitalOutput8.Index.Should().Be(0x6200);
        Cia401Objects.ReadAnalogInput16.Index.Should().Be(0x6401);
        Cia401Objects.WriteAnalogOutput16.Index.Should().Be(0x6411);
        Cia401Objects.AnalogInputGlobalInterruptEnable.Index.Should().Be(0x6423);
        Cia401Objects.AnalogOutputErrorMode.Index.Should().Be(0x6443);
        Cia401Objects.AnalogOutputErrorValueInteger.Index.Should().Be(0x6444);
        Cia401Objects.DeviceType.Index.Should().Be(0x67FF);
        Cia401Objects.ReadDigitalInputBit.Select(d => (int)d.Index).Should().Equal(Enumerable.Range(0x6020, 8));
        Cia401Objects.WriteDigitalOutputBit.Select(d => (int)d.Index).Should().Equal(Enumerable.Range(0x6220, 8));
    }

    // ---- ten objects, in full -----------------------------------------------------------------

    private static void Check(
        ushort index, string name, Cia401FunctionBlock block, bool isArray, OdDataType dataType,
        OdAccess access, Cia401PdoMapping pdo, long? defaultValue, int bits, string clause)
    {
        var d = Get(index);
        d.Index.Should().Be(index);
        d.Name.Should().Be(name);
        d.Block.Should().Be(block);
        d.IsArray.Should().Be(isArray);
        d.DataType.Should().Be(dataType);
        d.IsManufacturerSpecific.Should().BeFalse();
        d.Access.Should().Be(access);
        d.PdoMapping.Should().Be(pdo);
        d.DefaultValue.Should().Be(defaultValue);
        d.BitsPerElement.Should().Be(bits);
        d.Clause.Should().Be(clause);
    }

    [Fact]
    public void Object_6000h_Reads_Eight_Inputs_Per_Sub_Index()
        => Check(0x6000, "Read digital input 8-bit", Cia401FunctionBlock.DigitalInput, true, OdDataType.Unsigned8,
            OdAccess.ReadOnly, Cia401PdoMapping.Tpdo, null, 8, "8.2.1");

    [Fact]
    public void Object_6005h_Is_A_Boolean_Variable_Enabled_By_Default()
        => Check(0x6005, "Global interrupt enable digital 8-bit", Cia401FunctionBlock.DigitalInput, false,
            OdDataType.Boolean, OdAccess.ReadWrite, Cia401PdoMapping.No, 1, 0, "8.2.4");

    [Fact]
    public void Object_6020h_Reads_Single_Inputs_1_To_128()
    {
        Check(0x6020, "Read digital input bit 1 to 128", Cia401FunctionBlock.DigitalInput, true, OdDataType.Boolean,
            OdAccess.ReadOnly, Cia401PdoMapping.Optional, null, 1, "8.2.8");
        Get(0x6020).FirstChannel.Should().Be(1);
        Get(0x6020).MaxSubindex.Should().Be(0x80);
    }

    [Fact]
    public void Object_6127h_Is_The_32_Bit_Low_To_High_Mask_Off_By_Default()
        => Check(0x6127, "Interrupt mask digital input low-to-high 32-bit", Cia401FunctionBlock.DigitalInput, true,
            OdDataType.Unsigned32, OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0, 32, "8.2.24");

    [Fact]
    public void Object_6200h_Writes_Eight_Outputs_Per_Sub_Index_And_Is_Mapped_By_Default()
        => Check(0x6200, "Write digital output 8-bit", Cia401FunctionBlock.DigitalOutput, true, OdDataType.Unsigned8,
            OdAccess.ReadWrite, Cia401PdoMapping.Default, 0, 8, "8.3.1");

    [Fact]
    public void Object_6206h_Sends_Every_Output_To_Its_Error_Value_By_Default()
        => Check(0x6206, "Error mode digital output 8-bit", Cia401FunctionBlock.DigitalOutput, true,
            OdDataType.Unsigned8, OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0xFF, 8, "8.3.3");

    [Fact]
    public void Object_6401h_Reads_Sixteen_Bit_Inputs_And_Is_Mapped_By_Default()
        => Check(0x6401, "Read analog input 16-bit", Cia401FunctionBlock.AnalogInput, true, OdDataType.Integer16,
            OdAccess.ReadOnly, Cia401PdoMapping.Default, null, 0, "8.4.2");

    [Fact]
    public void Object_6423h_Is_A_Boolean_Variable_Disabled_By_Default()
        => Check(0x6423, "Analog input global interrupt enable", Cia401FunctionBlock.AnalogInput, false,
            OdDataType.Boolean, OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0, 0, "8.6.4");

    [Fact]
    public void Object_6443h_Defaults_To_Error_Mode_One()
        => Check(0x6443, "Analog output error mode", Cia401FunctionBlock.AnalogOutput, true, OdDataType.Unsigned8,
            OdAccess.ReadWrite, Cia401PdoMapping.Optional, 1, 0, "8.7.4");

    [Fact]
    public void Object_6444h_Defaults_To_Error_Value_Zero()
        => Check(0x6444, "Analog output error value integer", Cia401FunctionBlock.AnalogOutput, true,
            OdDataType.Integer32, OdAccess.ReadWrite, Cia401PdoMapping.Optional, 0, 0, "8.7.5");

    [Fact]
    public void The_Manufacturer_Specific_Analog_Objects_Are_Domains_Without_Default()
    {
        foreach (ushort index in new ushort[] { 0x6404, 0x6414 })
        {
            var d = Get(index);
            d.IsManufacturerSpecific.Should().BeTrue();
            d.DataType.Should().Be(OdDataType.Domain);
            d.IsArray.Should().BeTrue();
            d.DefaultValue.Should().BeNull();
        }

        Get(0x6404).Access.Should().Be(OdAccess.ReadOnly);
        Get(0x6414).Access.Should().Be(OdAccess.ReadWrite);
        Cia401Objects.All.Where(d => d.IsManufacturerSpecific).Select(d => d.Index)
            .Should().Equal(new ushort[] { 0x6404, 0x6414 });
    }

    [Fact]
    public void The_Device_Type_Object_Is_A_Read_Only_Unsigned32_Variable()
    {
        var d = Get(0x67FF);
        d.Block.Should().Be(Cia401FunctionBlock.General);
        d.IsArray.Should().BeFalse();
        d.DataType.Should().Be(OdDataType.Unsigned32);
        d.Access.Should().Be(OdAccess.ReadOnly);
        d.DefaultValue.Should().BeNull();
    }

    // ---- families -----------------------------------------------------------------------------

    [Fact]
    public void The_Range_Objects_Address_128_Channels_Each()
    {
        var starts = new[]
        {
            Cia401Objects.ReadDigitalInputBit, Cia401Objects.PolarityDigitalInputBit,
            Cia401Objects.FilterConstantDigitalInputBit, Cia401Objects.InterruptMaskDigitalInputBitAnyChange,
            Cia401Objects.InterruptMaskDigitalInputBitLowToHigh, Cia401Objects.InterruptMaskDigitalInputBitHighToLow,
            Cia401Objects.WriteDigitalOutputBit, Cia401Objects.ChangePolarityDigitalOutputBit,
            Cia401Objects.ErrorModeDigitalOutputBit, Cia401Objects.ErrorValueDigitalOutputBit,
            Cia401Objects.FilterMaskDigitalOutputBit,
        };
        foreach (var range in starts)
        {
            for (int k = 0; k < 8; k++)
            {
                range[k].FirstChannel.Should().Be(128 * k + 1);
                range[k].BitsPerElement.Should().Be(1);
                range[k].MaxSubindex.Should().Be(0x80);
                range[k].DataType.Should().Be(OdDataType.Boolean);
                range[k].Index.Should().Be((ushort)(range[0].Index + k));
                range[k].Clause.Should().Be(range[0].Clause);
            }
        }

        Get(0x6027).Name.Should().Be("Read digital input bit 897 to 1024");
        Get(0x6221).Name.Should().Be("Write digital output bit 129 to 256");
    }

    [Theory]
    [InlineData(0x6000, 8, OdDataType.Unsigned8)]
    [InlineData(0x6100, 16, OdDataType.Unsigned16)]
    [InlineData(0x6120, 32, OdDataType.Unsigned32)]
    [InlineData(0x6200, 8, OdDataType.Unsigned8)]
    [InlineData(0x6300, 16, OdDataType.Unsigned16)]
    [InlineData(0x6320, 32, OdDataType.Unsigned32)]
    public void The_Digital_Width_Families_Differ_In_Index_And_Width_Only(ushort read, int bits, OdDataType type)
    {
        // The siblings of a family sit at fixed offsets from the "read"/"write" object: polarity +2,
        // filter enable +3, then the three interrupt masks (input side) or error mode, error value
        // and filter mask (output side) — each with the width of the family.
        var offsets = read < 0x6200 ? new[] { 0, 2, 3, 6, 7, 8 } : new[] { 0, 2, 6, 7, 8 };
        foreach (int offset in offsets)
        {
            var d = Get((ushort)(read + offset));
            d.BitsPerElement.Should().Be(bits);
            d.DataType.Should().Be(type);
            d.MaxSubindex.Should().Be(0xFE);
        }
    }

    [Fact]
    public void The_Defaults_That_Are_Not_Zero_Are_Exactly_Those_Of_The_Norm()
    {
        var nonZero = new Dictionary<int, long>
        {
            [0x6005] = 1,
            [0x6006] = 0xFF,
            [0x6050] = 1,
            [0x6051] = 1,
            [0x6052] = 1,
            [0x6053] = 1,
            [0x6054] = 1,
            [0x6055] = 1,
            [0x6056] = 1,
            [0x6057] = 1,
            [0x6106] = 0xFFFF,
            [0x6126] = 0xFFFFFFFFL,
            [0x6206] = 0xFF,
            [0x6208] = 0xFF,
            [0x6250] = 1,
            [0x6251] = 1,
            [0x6252] = 1,
            [0x6253] = 1,
            [0x6254] = 1,
            [0x6255] = 1,
            [0x6256] = 1,
            [0x6257] = 1,
            [0x6270] = 1,
            [0x6271] = 1,
            [0x6272] = 1,
            [0x6273] = 1,
            [0x6274] = 1,
            [0x6275] = 1,
            [0x6276] = 1,
            [0x6277] = 1,
            [0x6306] = 0xFFFF,
            [0x6308] = 0xFFFF,
            [0x6326] = 0xFFFFFFFFL,
            [0x6328] = 0xFFFFFFFFL,
            [0x6421] = 0x07,
            [0x642F] = 1,   // pre-scaling float 1.0
            [0x6432] = 1,
            [0x6442] = 1,   // scaling float 1.0
            [0x6443] = 1,
            [0x6447] = 1,
        };
        var noDefault = new HashSet<int>
        {
            0x6000, 0x6100, 0x6120,
            0x6020, 0x6021, 0x6022, 0x6023, 0x6024, 0x6025, 0x6026, 0x6027,
            0x6400, 0x6401, 0x6402, 0x6403, 0x6404, 0x6414,
            0x6422, 0x6430, 0x6450, 0x67FF,
        };

        foreach (var d in Cia401Objects.All)
        {
            if (noDefault.Contains(d.Index))
                d.DefaultValue.Should().BeNull($"{d.Index:X4}h has no default value in the norm");
            else if (nonZero.TryGetValue(d.Index, out long expected))
                d.DefaultValue.Should().Be(expected, $"{d.Index:X4}h");
            else
                d.DefaultValue.Should().Be(0, $"{d.Index:X4}h defaults to zero / FALSE / 0.0");
        }
    }

    [Fact]
    public void The_Access_Is_Read_Only_Exactly_For_The_Inputs_The_Source_Of_The_Interrupt_And_The_Device_Type()
    {
        var readOnly = new HashSet<int>
        {
            0x6000, 0x6100, 0x6120,
            0x6020, 0x6021, 0x6022, 0x6023, 0x6024, 0x6025, 0x6026, 0x6027,
            0x6400, 0x6401, 0x6402, 0x6403, 0x6404,
            0x6422, 0x67FF,
        };
        foreach (var d in Cia401Objects.All)
            d.Access.Should().Be(readOnly.Contains(d.Index) ? OdAccess.ReadOnly : OdAccess.ReadWrite, $"{d.Index:X4}h");
    }

    [Fact]
    public void The_PDO_Mapping_Is_Optional_Except_Where_The_Norm_Says_Otherwise()
    {
        var other = new Dictionary<int, Cia401PdoMapping>
        {
            [0x6000] = Cia401PdoMapping.Tpdo,
            [0x6200] = Cia401PdoMapping.Default,
            [0x6401] = Cia401PdoMapping.Default,
            [0x6411] = Cia401PdoMapping.Default,
            [0x6005] = Cia401PdoMapping.No,
            [0x6122] = Cia401PdoMapping.No,
            [0x6302] = Cia401PdoMapping.No,   // sub-index 01h; 02h on is "Optional" (§8.3.12)
            [0x6322] = Cia401PdoMapping.No,   // sub-index 01h; 02h on is "Optional" (§8.3.17)
            [0x67FF] = Cia401PdoMapping.No,
        };
        foreach (var d in Cia401Objects.All)
            d.PdoMapping.Should().Be(other.TryGetValue(d.Index, out var expected) ? expected : Cia401PdoMapping.Optional, $"{d.Index:X4}h");
    }

    [Fact]
    public void The_Analog_Data_Types_Follow_The_Width_In_The_Name()
    {
        Get(0x6400).DataType.Should().Be(OdDataType.Integer8);
        Get(0x6401).DataType.Should().Be(OdDataType.Integer16);
        Get(0x6402).DataType.Should().Be(OdDataType.Integer32);
        Get(0x6403).DataType.Should().Be(OdDataType.Real32);
        Get(0x6410).DataType.Should().Be(OdDataType.Integer8);
        Get(0x6411).DataType.Should().Be(OdDataType.Integer16);
        Get(0x6412).DataType.Should().Be(OdDataType.Integer32);
        Get(0x6413).DataType.Should().Be(OdDataType.Real32);

        foreach (ushort index in new ushort[] { 0x6429, 0x642A, 0x642B, 0x642C, 0x642D, 0x642E, 0x642F, 0x6441, 0x6442, 0x6445 })
            Get(index).DataType.Should().Be(OdDataType.Real32, $"{index:X4}h is a float object");
        foreach (ushort index in new ushort[] { 0x6424, 0x6425, 0x6431, 0x6432, 0x6444, 0x6446, 0x6447 })
            Get(index).DataType.Should().Be(OdDataType.Integer32, $"{index:X4}h is an integer object");
        foreach (ushort index in new ushort[] { 0x6422, 0x6426, 0x6427, 0x6428, 0x6430, 0x6450 })
            Get(index).DataType.Should().Be(OdDataType.Unsigned32, $"{index:X4}h is an unsigned object");
        Get(0x6421).DataType.Should().Be(OdDataType.Unsigned8);
        Get(0x6443).DataType.Should().Be(OdDataType.Unsigned8);
    }

    [Fact]
    public void The_Only_Variables_Are_The_Two_Global_Interrupt_Enables_And_The_Device_Type()
        => Cia401Objects.All.Where(d => !d.IsArray).Select(d => d.Index)
            .Should().Equal(new ushort[] { 0x6005, 0x6423, 0x67FF });

    // ---- Describe -----------------------------------------------------------------------------

    [Theory]
    [InlineData(0x6000, 0, "Read digital input 8-bit, highest sub-index")]
    [InlineData(0x6000, 1, "Read digital input 8-bit, inputs 1–8")]
    [InlineData(0x6000, 2, "Read digital input 8-bit, inputs 9–16")]
    [InlineData(0x6000, 0xFE, "Read digital input 8-bit, inputs 2025–2032")]
    [InlineData(0x6100, 3, "Read digital input 16-bit, inputs 33–48")]
    [InlineData(0x6320, 2, "Write digital output 32-bit, outputs 33–64")]
    [InlineData(0x6200, 9, "Write digital output 8-bit, outputs 65–72")]
    [InlineData(0x6020, 5, "Read digital input bit 1 to 128, input 5")]
    [InlineData(0x6021, 5, "Read digital input bit 129 to 256, input 133")]
    [InlineData(0x6227, 0x80, "Write digital output bit 897 to 1024, output 1024")]
    [InlineData(0x6401, 3, "Read analog input 16-bit, analog input 3")]
    [InlineData(0x6411, 1, "Write analog output 16-bit, analog output 1")]
    [InlineData(0x6422, 2, "Analog input interrupt source, bank 2")]
    [InlineData(0x6005, 0, "Global interrupt enable digital 8-bit")]
    [InlineData(0x67FF, 0, "Device type")]
    public void Describe_Names_The_Channels_Of_A_Sub_Index(ushort index, byte subindex, string expected)
        => Cia401Objects.Describe(index, subindex).Should().Be(expected);

    [Theory]
    [InlineData(0x6000, 0xFF)]   // FFh is not a sub-index of an array
    [InlineData(0x6020, 0x81)]   // a range object ends at 80h
    [InlineData(0x6422, 9)]      // eight interrupt source banks
    [InlineData(0x6005, 1)]      // a variable has sub-index 0 only
    public void Describe_Marks_A_Sub_Index_The_Norm_Does_Not_Define(ushort index, byte subindex)
        => Cia401Objects.Describe(index, subindex).Should().EndWith($"sub-index {subindex:X2}h (not defined)");

    [Fact]
    public void Describe_Of_A_Foreign_Index_Names_Index_And_Sub_Index_Only()
        => Cia401Objects.Describe(0x6420, 1).Should().Be("6420h sub-index 01h (not a CiA 401 object)");

    // ---- the clause of every object -----------------------------------------------------------

    [Fact]
    public void Every_Object_Cites_A_Clause_Of_Chapter_8()
    {
        foreach (var d in Cia401Objects.All.Where(d => d.Block != Cia401FunctionBlock.General))
            d.Clause.Should().MatchRegex(@"^8\.[2-7]\.\d{1,2}$", $"{d.Index:X4}h");

        // The norm numbers the general device profile objects "8.3" and the digital outputs "8.3"
        // as well, so 67FFh is cited by the title of its clause and not by a number that is
        // already taken by 6200h.
        Get(0x67FF).Clause.Should().Be("General device profile objects");
    }
}
