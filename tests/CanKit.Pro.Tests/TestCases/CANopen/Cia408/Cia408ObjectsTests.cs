using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Cia408;
using Xunit;
using static CanKit.Pro.CANopen.OdDataType;

namespace CanKit.Pro.Tests.TestCases.CANopen.Cia408;

/// <summary>
/// The CiA 408 v2.2.0 object catalogue (clause 9). These tests are the independent check on
/// <see cref="Cia408Objects"/>: every index is written out here. The list of indices with their
/// object kind and value type was derived from the headings and object descriptions of the
/// norm's text, with Annex A filling two gaps (<c>6216h</c>, <c>6382h</c>), which is a different
/// derivation from the one that produced the cell values of the catalogue; the definitions
/// pinned in full were read off the rendered pages by hand.
/// </summary>
public class Cia408ObjectsTests
{
    private const bool R = true;  // RECORD: a value record of one of the types 0080h to 0087h
    private const bool V = false; // VAR

    private static Cia408ObjectDefinition Get(ushort index)
    {
        Cia408Objects.TryGet(index, out var definition).Should().BeTrue($"{index:X4}h is a CiA 408 object");
        return definition!;
    }

    // ---- shape of the catalogue ---------------------------------------------------------------

    [Theory]
    [InlineData("DeviceControl", 8)]                  // §9.3
    [InlineData("DeviceInformation", 8)]              // §9.4, 605Fh reserved
    [InlineData("ValveActualValueConditioning", 62)]  // §9.5
    [InlineData("DriveActualValueConditioning", 52)]  // §9.6
    [InlineData("ActuatorConditioning", 35)]          // §9.7, 6280h reserved
    [InlineData("ValveSpoolPosition", 53)]            // §9.8 to §9.11: 13 + 23 + 8 + 9
    [InlineData("ValvePressure", 48)]                 // §9.12 to §9.15: 9 + 23 + 8 + 9, 63D8h once
    [InlineData("ValvePower", 11)]                    // §9.16, §9.20: 2 + 9
    [InlineData("ValveFlow", 51)]                     // §9.21 to §9.25, §9.31: 9 + 23 + 8 + 9 + 2
    [InlineData("DriveOpenLoop", 14)]                 // §9.32, §9.33: 3 + 11
    [InlineData("DriveVelocity", 33)]                 // §9.37 to §9.41: 6 + 13 + 8 + 6
    [InlineData("DrivePressure", 36)]                 // §9.42 to §9.46: 11 + 11 + 8 + 6
    [InlineData("DrivePosition", 44)]                 // §9.47 to §9.51: 16 + 14 + 8 + 6
    public void Each_Area_Holds_The_Objects_Of_Its_Clauses(string area, int expected)
    {
        // By name: the enum is internal and a public test method cannot take it.
        var parsed = (Cia408Area)Enum.Parse(typeof(Cia408Area), area);
        Cia408Objects.All.Count(d => d.Area == parsed).Should().Be(expected);
    }

    [Fact]
    public void All_Holds_455_Definitions_72_Variables_And_383_Value_Records()
    {
        Cia408Objects.All.Should().HaveCount(455);
        Cia408Objects.All.Count(d => !d.IsValueRecord).Should().Be(72);
        Cia408Objects.All.Count(d => d.IsValueRecord).Should().Be(383);
    }

    [Fact]
    public void Indices_Are_Unique_Ascending_And_Inside_The_Profile_Range_Of_Figure_4()
    {
        Cia408Objects.All.Select(d => d.Index).Should().OnlyHaveUniqueItems();
        Cia408Objects.All.Select(d => d.Index).Should().BeInAscendingOrder();
        Cia408Objects.All.Should().OnlyContain(d => d.Index >= 0x6040 && d.Index <= 0x67FE);
    }

    [Fact]
    public void Every_Value_Record_Has_The_Value_Type_Of_One_Of_The_Eight_Records_0080h_To_0087h()
    {
        // §8.7.1: 0080h UNSIGNED8, 0081h UNSIGNED16, 0082h UNSIGNED32, 0083h INTEGER8,
        // 0084h INTEGER16, 0085h INTEGER32, 0086h REAL32, 0087h REAL64.
        var eight = new[] { Unsigned8, Unsigned16, Unsigned32, Integer8, Integer16, Integer32, Real32, Real64 };
        Cia408Objects.All.Where(d => d.IsValueRecord).Should().OnlyContain(d => eight.Contains(d.ValueType));

        // No object of clause 9 uses the REAL records; the variables are integers or strings.
        Cia408Objects.All.Should().NotContain(d => d.ValueType == Real32 || d.ValueType == Real64);
        var variableTypes = new[] { Unsigned8, Unsigned16, Unsigned32, Integer8, VisibleString };
        Cia408Objects.All.Where(d => !d.IsValueRecord).Should().OnlyContain(d => variableTypes.Contains(d.ValueType));
        Cia408Objects.All.Where(d => d.ValueType == VisibleString).Select(d => (int)d.Index)
            .Should().Equal(0x6050, 0x6052, 0x6053, 0x6054, 0x6055, 0x6057);
    }

    [Fact]
    public void Reserved_Holds_The_Sixteen_Stubs_And_None_Of_Them_Is_Defined()
    {
        var stubs = new ushort[]
        {
            0x605F,                                                          // §9.4.9
            0x6280,                                                          // §9.7.2 (Annex A defines it: reserved wins)
            0x63A4, 0x63C0, 0x63C1, 0x63C2, 0x63C3, 0x63C4, 0x63C5, 0x63C6,  // §9.14
            0x63E0, 0x63E1, 0x63E2,                                          // §9.14
            0x6460, 0x6461, 0x6462,                                          // §9.18
        };
        Cia408Objects.Reserved.Should().Equal(stubs);
        foreach (ushort index in stubs)
            Cia408Objects.TryGet(index, out _).Should().BeFalse($"{index:X4}h is reserved");
        Cia408Objects.All.Select(d => d.Index).Should().NotIntersectWith(stubs);
    }

    [Theory]
    [InlineData(0x1000)]
    [InlineData(0x1029)]
    [InlineData(0x6000)]
    [InlineData(0x6046)]
    [InlineData(0x6058)]
    [InlineData(0x6106)]
    [InlineData(0x6680)]
    [InlineData(0x67FE)]
    [InlineData(0x67FF)]
    public void An_Index_The_Norm_Does_Not_Define_Is_Not_In_The_Catalogue(ushort index)
        => Cia408Objects.TryGet(index, out _).Should().BeFalse();

    [Fact]
    public void The_Indices_Kinds_And_Value_Types_Are_Exactly_Those_Of_Clause_9()
    {
        // Document order, one line per object definition; 63D8h once (§9.14.19, see the ruling
        // below), 65D1h and 6605h by their headings.
        var expected = new (int Index, bool Record, OdDataType Type)[]
        {
            // §9.3
            (0x6040, V, Unsigned16),
            (0x6041, V, Unsigned16),
            (0x6042, V, Integer8),
            (0x6043, V, Integer8),
            (0x6044, V, Unsigned32),
            (0x6045, V, Unsigned32),
            (0x604E, V, Unsigned16),
            (0x604F, V, Integer8),
            // §9.4
            (0x6050, V, VisibleString),
            (0x6051, V, Unsigned16),
            (0x6052, V, VisibleString),
            (0x6053, V, VisibleString),
            (0x6054, V, VisibleString),
            (0x6055, V, VisibleString),
            (0x6056, V, Unsigned8),
            (0x6057, V, VisibleString),
            // §9.5
            (0x6100, V, Unsigned8),
            (0x6101, V, Unsigned8),
            (0x6102, V, Integer8),
            (0x6103, V, Integer8),
            (0x6104, R, Integer16),
            (0x6105, R, Integer32),
            (0x6110, R, Integer16),
            (0x6111, R, Integer16),
            (0x6112, R, Integer16),
            (0x6113, R, Integer16),
            (0x6114, R, Integer16),
            (0x6115, R, Integer16),
            (0x6116, R, Integer16),
            (0x6117, R, Integer16),
            (0x6118, R, Integer32),
            (0x6119, R, Integer32),
            (0x611A, R, Integer32),
            (0x611B, R, Integer32),
            (0x611C, R, Integer32),
            (0x611D, R, Integer32),
            (0x611E, R, Integer32),
            (0x611F, R, Integer32),
            (0x6120, R, Integer16),
            (0x6121, R, Integer16),
            (0x6122, R, Integer16),
            (0x6123, R, Integer16),
            (0x6124, R, Integer16),
            (0x6125, R, Integer16),
            (0x6128, R, Integer16),
            (0x6129, R, Integer16),
            (0x612B, R, Integer16),
            (0x612C, R, Integer32),
            (0x612D, R, Integer32),
            (0x6130, R, Integer32),
            (0x6131, R, Integer32),
            (0x6132, R, Unsigned32),
            (0x6133, R, Integer32),
            (0x6134, R, Integer32),
            (0x6140, R, Integer32),
            (0x6141, R, Integer32),
            (0x6142, R, Integer32),
            (0x6143, V, Unsigned8),
            (0x6144, R, Integer32),
            (0x6145, V, Integer8),
            (0x6146, R, Unsigned8),
            (0x6147, R, Unsigned8),
            (0x6170, R, Integer16),
            (0x6171, R, Integer16),
            (0x6172, R, Integer16),
            (0x6173, R, Integer16),
            (0x6174, R, Integer16),
            (0x6175, R, Integer16),
            (0x6176, R, Integer16),
            (0x6177, R, Integer16),
            (0x6178, R, Integer32),
            (0x6179, R, Integer32),
            (0x617A, R, Integer32),
            (0x617B, R, Integer32),
            (0x617C, R, Integer32),
            (0x617D, R, Integer32),
            (0x617E, R, Integer32),
            (0x617F, R, Integer32),
            // §9.6
            (0x6200, V, Unsigned8),
            (0x6201, V, Unsigned8),
            (0x6202, V, Integer8),
            (0x6203, V, Integer8),
            (0x6204, R, Integer32),
            (0x6210, R, Integer32),
            (0x6211, R, Integer32),
            (0x6212, R, Integer32),
            (0x6213, R, Integer32),
            (0x6214, R, Integer32),
            (0x6215, R, Integer32),
            (0x6216, R, Integer32),
            (0x6217, R, Integer32),
            (0x6220, R, Integer32),
            (0x6221, R, Integer32),
            (0x6222, R, Integer32),
            (0x6223, R, Integer32),
            (0x6224, R, Integer32),
            (0x6225, R, Integer32),
            (0x6228, R, Integer16),
            (0x6229, R, Integer16),
            (0x622B, R, Integer16),
            (0x622C, R, Integer32),
            (0x622D, R, Integer32),
            (0x6230, R, Integer32),
            (0x6231, R, Integer32),
            (0x6232, R, Unsigned32),
            (0x6233, R, Integer32),
            (0x6234, R, Integer32),
            (0x6240, R, Integer32),
            (0x6241, R, Integer32),
            (0x6242, R, Integer32),
            (0x6243, V, Unsigned8),
            (0x6244, R, Integer32),
            (0x6245, V, Integer8),
            (0x6246, R, Unsigned8),
            (0x6247, R, Unsigned8),
            (0x6250, R, Unsigned32),
            (0x6251, R, Integer16),
            (0x6252, R, Integer32),
            (0x6253, R, Integer16),
            (0x6254, R, Integer16),
            (0x6255, R, Integer32),
            (0x6256, R, Integer16),
            (0x6278, R, Integer32),
            (0x6279, R, Integer32),
            (0x627A, R, Integer32),
            (0x627B, R, Integer32),
            (0x627C, R, Integer32),
            (0x627D, R, Integer32),
            (0x627E, R, Integer32),
            (0x627F, R, Integer32),
            // §9.7
            (0x6281, R, Integer32),
            (0x6282, R, Integer32),
            (0x6290, V, Integer8),
            (0x6291, R, Unsigned32),
            (0x6292, R, Integer32),
            (0x6293, R, Unsigned32),
            (0x6294, V, Unsigned8),
            (0x6295, R, Unsigned32),
            (0x62A0, V, Integer8),
            (0x62A1, V, Unsigned32),
            (0x62A2, V, Integer8),
            (0x62A3, R, Unsigned32),
            (0x62B0, V, Integer8),
            (0x62B1, R, Integer32),
            (0x62B2, R, Integer32),
            (0x62B3, R, Integer32),
            (0x62C0, R, Integer32),
            (0x62D0, V, Integer8),
            (0x62D1, R, Integer32),
            (0x62D2, R, Unsigned32),
            (0x62E0, R, Integer32),
            (0x62E1, R, Integer32),
            (0x62F0, V, Integer8),
            (0x6324, R, Integer16),
            (0x6340, V, Integer8),
            (0x6341, V, Unsigned32),
            (0x6342, V, Integer8),
            (0x6343, R, Integer16),
            (0x6344, R, Integer16),
            (0x6345, R, Integer16),
            (0x6346, V, Integer8),
            (0x6347, R, Unsigned32),
            (0x6360, V, Integer8),
            (0x6361, R, Unsigned16),
            (0x6362, R, Unsigned16),
            // §9.8
            (0x6300, R, Integer16),
            (0x6301, R, Integer16),
            (0x6302, V, Unsigned8),
            (0x6308, R, Integer32),
            (0x6309, R, Integer32),
            (0x630A, R, Integer32),
            (0x6310, R, Integer16),
            (0x6312, R, Integer16),
            (0x6313, R, Integer16),
            (0x631E, R, Integer32),
            (0x631F, R, Integer32),
            (0x6350, R, Integer16),
            (0x635B, R, Integer32),
            // §9.9
            (0x6311, R, Integer16),
            (0x6314, R, Integer16),
            (0x6318, R, Integer32),
            (0x631B, R, Integer32),
            (0x6320, R, Integer16),
            (0x6321, R, Integer16),
            (0x6322, V, Unsigned32),
            (0x6323, R, Integer16),
            (0x6328, R, Integer32),
            (0x6329, R, Integer32),
            (0x6330, V, Integer8),
            (0x6331, R, Unsigned16),
            (0x6332, R, Unsigned16),
            (0x6333, R, Unsigned16),
            (0x6334, R, Unsigned16),
            (0x6335, R, Unsigned16),
            (0x6336, R, Unsigned16),
            (0x6338, R, Unsigned32),
            (0x6339, R, Unsigned32),
            (0x633A, R, Unsigned32),
            (0x633B, R, Unsigned32),
            (0x633C, R, Unsigned32),
            (0x633D, R, Unsigned32),
            // §9.10
            (0x6351, V, Integer8),
            (0x6352, R, Unsigned16),
            (0x6353, R, Integer16),
            (0x6354, R, Integer16),
            (0x6355, R, Integer16),
            (0x6358, R, Integer32),
            (0x6359, R, Integer32),
            (0x635A, R, Integer32),
            // §9.11
            (0x6370, V, Integer8),
            (0x6371, R, Unsigned16),
            (0x6372, R, Unsigned16),
            (0x6373, R, Integer16),
            (0x6374, R, Integer16),
            (0x6375, R, Integer16),
            (0x6378, R, Integer32),
            (0x6379, R, Integer32),
            (0x637A, R, Integer32),
            // §9.12
            (0x6380, R, Integer16),
            (0x6381, R, Integer16),
            (0x6382, V, Unsigned8),
            (0x6388, R, Integer32),
            (0x6389, R, Integer32),
            (0x638A, R, Integer32),
            (0x6390, R, Integer16),
            (0x63D0, R, Integer16),
            // §9.13
            (0x6391, R, Integer16),
            (0x6394, R, Integer16),
            (0x6398, R, Integer32),
            (0x639B, R, Integer32),
            (0x63A0, R, Integer16),
            (0x63A1, R, Integer16),
            (0x63A2, V, Unsigned32),
            (0x63A3, R, Integer16),
            (0x63A8, R, Integer32),
            (0x63A9, R, Integer32),
            (0x63B0, V, Integer8),
            (0x63B1, R, Unsigned16),
            (0x63B2, R, Unsigned16),
            (0x63B3, R, Unsigned16),
            (0x63B4, R, Unsigned16),
            (0x63B5, R, Unsigned16),
            (0x63B6, R, Unsigned16),
            (0x63B8, R, Unsigned32),
            (0x63B9, R, Unsigned32),
            (0x63BA, R, Unsigned32),
            (0x63BB, R, Unsigned32),
            (0x63BC, R, Unsigned32),
            (0x63BD, R, Unsigned32),
            // §9.14
            (0x63D1, V, Integer8),
            (0x63D2, R, Unsigned16),
            (0x63D3, R, Integer16),
            (0x63D4, R, Integer16),
            (0x63D5, R, Integer16),
            (0x63D8, R, Integer32),
            (0x63D9, R, Integer32),
            (0x63DA, R, Integer32),
            // §9.15
            (0x63F0, V, Integer8),
            (0x63F1, R, Unsigned16),
            (0x63F2, R, Unsigned16),
            (0x63F3, R, Integer16),
            (0x63F4, R, Integer16),
            (0x63F5, R, Integer16),
            (0x63F8, R, Integer32),
            (0x63F9, R, Integer32),
            (0x63FA, R, Integer32),
            // §9.16
            (0x640D, V, Unsigned32),
            (0x640E, R, Integer16),
            // §9.20
            (0x6470, V, Integer8),
            (0x6471, R, Unsigned16),
            (0x6472, R, Unsigned16),
            (0x6473, R, Integer16),
            (0x6474, R, Integer16),
            (0x6475, R, Integer16),
            (0x6478, R, Integer32),
            (0x6479, R, Integer32),
            (0x647A, R, Integer32),
            // §9.21
            (0x6700, R, Integer16),
            (0x6701, R, Integer16),
            (0x6702, V, Unsigned8),
            (0x6708, R, Integer32),
            (0x6709, R, Integer32),
            (0x670A, R, Integer32),
            (0x6710, R, Integer16),
            (0x6750, R, Integer16),
            (0x6758, R, Integer32),
            // §9.22
            (0x6711, R, Integer16),
            (0x6714, R, Integer16),
            (0x6718, R, Integer32),
            (0x671B, R, Integer32),
            (0x6720, R, Integer16),
            (0x6721, R, Integer16),
            (0x6722, V, Unsigned32),
            (0x6723, R, Integer16),
            (0x6728, R, Integer32),
            (0x6729, R, Integer32),
            (0x6730, V, Integer8),
            (0x6731, R, Unsigned16),
            (0x6732, R, Unsigned16),
            (0x6733, R, Unsigned16),
            (0x6734, R, Unsigned16),
            (0x6735, R, Unsigned16),
            (0x6736, R, Unsigned16),
            (0x6738, R, Unsigned32),
            (0x6739, R, Unsigned32),
            (0x673A, R, Unsigned32),
            (0x673B, R, Unsigned32),
            (0x673C, R, Unsigned32),
            (0x673D, R, Unsigned32),
            // §9.24
            (0x6751, V, Integer8),
            (0x6752, R, Unsigned16),
            (0x6753, R, Integer16),
            (0x6754, R, Integer16),
            (0x6755, R, Integer16),
            (0x6759, R, Integer32),
            (0x675A, R, Integer32),
            (0x675B, R, Integer32),
            // §9.25
            (0x6770, V, Integer8),
            (0x6771, R, Unsigned16),
            (0x6772, R, Unsigned16),
            (0x6773, R, Integer16),
            (0x6774, R, Integer16),
            (0x6775, R, Integer16),
            (0x6778, R, Integer32),
            (0x6779, R, Integer32),
            (0x677A, R, Integer32),
            // §9.31
            (0x6780, R, Integer32),
            (0x6781, R, Integer32),
            // §9.32
            (0x6480, R, Integer32),
            (0x6490, R, Integer32),
            (0x6494, R, Integer32),
            // §9.33
            (0x6492, R, Integer32),
            (0x6493, R, Integer32),
            (0x64A0, R, Integer32),
            (0x64A1, R, Integer32),
            (0x64B0, V, Integer8),
            (0x64B1, R, Unsigned32),
            (0x64B2, R, Unsigned32),
            (0x64B3, R, Unsigned32),
            (0x64B4, R, Unsigned32),
            (0x64B5, R, Unsigned32),
            (0x64B6, R, Unsigned32),
            // §9.37
            (0x6500, R, Integer32),
            (0x6501, R, Integer32),
            (0x6502, V, Unsigned8),
            (0x6510, R, Integer32),
            (0x6514, R, Integer32),
            (0x6550, R, Integer32),
            // §9.38
            (0x6503, R, Unsigned32),
            (0x6504, R, Unsigned32),
            (0x6512, R, Integer32),
            (0x6513, R, Integer32),
            (0x6520, R, Integer32),
            (0x6521, R, Integer32),
            (0x6530, V, Integer8),
            (0x6531, R, Unsigned32),
            (0x6532, R, Unsigned32),
            (0x6533, R, Unsigned32),
            (0x6534, R, Unsigned32),
            (0x6535, R, Unsigned32),
            (0x6536, R, Unsigned32),
            // §9.40
            (0x6551, V, Integer8),
            (0x6552, R, Unsigned32),
            (0x6553, R, Integer32),
            (0x6554, R, Integer32),
            (0x6555, R, Integer32),
            (0x6556, R, Integer32),
            (0x6557, R, Integer32),
            (0x6558, R, Integer32),
            // §9.41
            (0x6570, V, Integer8),
            (0x6571, R, Unsigned32),
            (0x6572, R, Unsigned32),
            (0x6573, R, Integer32),
            (0x6574, R, Integer32),
            (0x6575, R, Integer32),
            // §9.42
            (0x6580, R, Integer32),
            (0x6581, R, Integer32),
            (0x6582, V, Unsigned8),
            (0x6590, R, Integer32),
            (0x6594, R, Integer32),
            (0x65D0, R, Integer32),
            (0x6583, R, Unsigned32),
            (0x6584, R, Unsigned32),
            (0x6585, R, Unsigned32),
            (0x6586, R, Unsigned32),
            (0x6587, R, Unsigned32),
            // §9.43
            (0x6592, R, Integer32),
            (0x6593, R, Integer32),
            (0x65A0, R, Integer32),
            (0x65A1, R, Integer32),
            (0x65B0, V, Integer8),
            (0x65B1, R, Unsigned32),
            (0x65B2, R, Unsigned32),
            (0x65B3, R, Unsigned32),
            (0x65B4, R, Unsigned32),
            (0x65B5, R, Unsigned32),
            (0x65B6, R, Unsigned32),
            // §9.45
            (0x65D1, V, Integer8),
            (0x65D2, R, Unsigned32),
            (0x65D3, R, Integer32),
            (0x65D4, R, Integer32),
            (0x65D5, R, Integer32),
            (0x65D6, R, Integer32),
            (0x65D7, R, Integer32),
            (0x65D8, R, Integer32),
            // §9.46
            (0x65F0, V, Integer8),
            (0x65F1, R, Unsigned32),
            (0x65F2, R, Unsigned32),
            (0x65F3, R, Integer32),
            (0x65F4, R, Integer32),
            (0x65F5, R, Integer32),
            // §9.47
            (0x6600, R, Integer32),
            (0x6601, R, Integer32),
            (0x6602, V, Unsigned8),
            (0x6603, R, Unsigned32),
            (0x6604, R, Unsigned32),
            (0x6605, R, Unsigned32),
            (0x6608, V, Integer8),
            (0x6609, R, Unsigned32),
            (0x660A, R, Unsigned32),
            (0x660C, R, Unsigned32),
            (0x660D, R, Unsigned32),
            (0x660E, R, Unsigned32),
            (0x660F, R, Unsigned32),
            (0x6610, R, Integer32),
            (0x6614, R, Integer32),
            (0x6650, R, Integer32),
            // §9.48
            (0x6612, R, Integer32),
            (0x6613, R, Integer32),
            (0x6620, R, Integer32),
            (0x6621, R, Integer32),
            (0x6630, V, Integer8),
            (0x6631, R, Unsigned32),
            (0x6632, R, Unsigned32),
            (0x6633, R, Unsigned32),
            (0x6634, R, Unsigned32),
            (0x6635, R, Unsigned32),
            (0x6636, R, Unsigned32),
            (0x6637, R, Integer32),
            (0x6638, R, Unsigned32),
            (0x6639, R, Unsigned32),
            // §9.50
            (0x6651, V, Integer8),
            (0x6652, R, Unsigned32),
            (0x6653, R, Integer32),
            (0x6654, R, Integer32),
            (0x6655, R, Integer32),
            (0x6656, R, Integer32),
            (0x6657, R, Integer32),
            (0x6658, R, Integer32),
            // §9.51
            (0x6670, V, Integer8),
            (0x6671, R, Unsigned32),
            (0x6672, R, Unsigned32),
            (0x6673, R, Integer32),
            (0x6674, R, Integer32),
            (0x6675, R, Integer32),
        };

        expected.Select(e => e.Index).Should().OnlyHaveUniqueItems();
        var actual = Cia408Objects.All.Select(d => (Index: (int)d.Index, Record: d.IsValueRecord, Type: d.ValueType));
        actual.Should().Equal(expected.OrderBy(e => e.Index));
    }

    // ---- named members ------------------------------------------------------------------------

    [Fact]
    public void Every_Named_Member_Is_In_The_Catalogue()
    {
        var members = typeof(Cia408Objects)
            .GetProperties(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(p => p.PropertyType == typeof(Cia408ObjectDefinition))
            .ToList();
        members.Should().HaveCount(30);
        foreach (var member in members)
        {
            var value = (Cia408ObjectDefinition?)member.GetValue(null);
            value.Should().NotBeNull(member.Name);
            Cia408Objects.All.Should().Contain(value!, member.Name);
        }
    }

    [Fact]
    public void The_Named_Members_Carry_The_Index_Of_The_Norm()
    {
        // §9.3
        Cia408Objects.DeviceControlWord16.Index.Should().Be(0x6040);
        Cia408Objects.DeviceStatusWord16.Index.Should().Be(0x6041);
        Cia408Objects.DeviceMode.Index.Should().Be(0x6042);
        Cia408Objects.DeviceControlMode.Index.Should().Be(0x6043);
        Cia408Objects.DeviceControlWord32.Index.Should().Be(0x6044);
        Cia408Objects.DeviceStatusWord32.Index.Should().Be(0x6045);
        Cia408Objects.DeviceErrorCode.Index.Should().Be(0x604E);
        Cia408Objects.VpqcHydrostaticActualPower.Index.Should().Be(0x640E);
        Cia408Objects.VpflcplPowerLimit.Index.Should().Be(0x6780);
        Cia408Objects.VpflcplPowerActualValue.Index.Should().Be(0x6781);
        Cia408Objects.DeviceLocal.Index.Should().Be(0x604F);

        // The setpoints and actual values the default PDOs map (Tables 13 and 32).
        Cia408Objects.QSetpoint.Index.Should().Be(0x6300);
        Cia408Objects.SpoolPositionActualValue.Index.Should().Be(0x6301);
        Cia408Objects.QSetpoint32.Index.Should().Be(0x6308);
        Cia408Objects.SpoolPositionActualValue32.Index.Should().Be(0x6309);
        Cia408Objects.VprcSetpoint.Index.Should().Be(0x6380);
        Cia408Objects.VprcActualValue.Index.Should().Be(0x6381);
        Cia408Objects.VprcSetpoint32.Index.Should().Be(0x6388);
        Cia408Objects.VprcActualValue32.Index.Should().Be(0x6389);
        Cia408Objects.DcolSetpoint.Index.Should().Be(0x6480);
        Cia408Objects.DscSetpoint.Index.Should().Be(0x6500);
        Cia408Objects.DscActualValue.Index.Should().Be(0x6501);
        Cia408Objects.DfpcSetpoint.Index.Should().Be(0x6580);
        Cia408Objects.DfpcActualValue.Index.Should().Be(0x6581);
        Cia408Objects.DpcSetpoint.Index.Should().Be(0x6600);
        Cia408Objects.DpcActualValue.Index.Should().Be(0x6601);
        Cia408Objects.VflcSetpoint.Index.Should().Be(0x6700);
        Cia408Objects.VflcActualValue.Index.Should().Be(0x6701);
        Cia408Objects.VflcSetpoint32.Index.Should().Be(0x6708);
        Cia408Objects.VflcActualValue32.Index.Should().Be(0x6709);
    }

    // ---- definitions in full ------------------------------------------------------------------

    /// <summary>
    /// Every field of a definition, as the rendered page shows it. <paramref name="pdo"/> is
    /// "most/least permissive" of <see cref="Cia408PdoAccess"/>. A default is a number, a
    /// <see cref="Cia408DefaultKind"/> name, or <c>Text:…</c>; a unit is a CiA 303-2 code in hex,
    /// <c>ir</c>, <c>ir32</c> or a kind name; a prefix is a number or a kind name.
    /// </summary>
    [Theory]
    [InlineData(0x6040, "Device control word 16-bit", "9.3.2", "DeviceControl", false, OdDataType.Unsigned16, OdAccess.ReadWrite, false, "Both/Receive", "Mandatory", null, "0", "NotApplicable", "NotApplicable")]
    [InlineData(0x6041, "Device status word 16-bit", "9.3.3", "DeviceControl", false, OdDataType.Unsigned16, OdAccess.ReadOnly, false, "Transmit/Transmit", "Mandatory", null, "None", "NotApplicable", "NotApplicable")]
    [InlineData(0x6042, "Device mode", "9.3.4", "DeviceControl", false, OdDataType.Integer8, OdAccess.ReadWrite, true, "Both/No", "Mandatory", null, "1", "NotApplicable", "NotApplicable")]
    [InlineData(0x6043, "Device control mode", "9.3.5", "DeviceControl", false, OdDataType.Integer8, OdAccess.ReadWrite, true, "Both/No", "Optional", null, "ManufacturerSpecific", "NotApplicable", "NotApplicable")]
    [InlineData(0x6044, "Device control word 32-bit", "9.3.6", "DeviceControl", false, OdDataType.Unsigned32, OdAccess.ReadWrite, false, "Both/Receive", "Mandatory", null, "0", "NotApplicable", "NotApplicable")]
    [InlineData(0x6045, "Device status word 32-bit", "9.3.7", "DeviceControl", false, OdDataType.Unsigned32, OdAccess.ReadOnly, false, "Transmit/Transmit", "Mandatory", null, "None", "NotApplicable", "NotApplicable")]
    [InlineData(0x604E, "Device error code", "9.3.8", "DeviceControl", false, OdDataType.Unsigned16, OdAccess.ReadOnly, false, "Transmit/Transmit", "Optional", null, "None", "NotApplicable", "NotApplicable")]
    [InlineData(0x604F, "Device local", "9.3.9", "DeviceControl", false, OdDataType.Integer8, OdAccess.ReadWrite, true, "Both/No", "Optional", null, "DeviceKindSpecific", "NotApplicable", "NotApplicable")]
    [InlineData(0x6050, "Device version", "9.4.1", "DeviceInformation", false, OdDataType.VisibleString, OdAccess.ReadOnly, false, "No/No", "Optional", null, "ManufacturerSpecific", "NotApplicable", "NotApplicable")]
    [InlineData(0x6053, "Device description", "9.4.4", "DeviceInformation", false, OdDataType.VisibleString, OdAccess.ReadWrite, false, "No/No", "Optional", null, "Text:0", "NotApplicable", "NotApplicable")]
    [InlineData(0x6057, "Device vendor name", "9.4.8", "DeviceInformation", false, OdDataType.VisibleString, OdAccess.ReadOnly, false, "No/No", "Optional", null, "ManufacturerSpecific", "NotApplicable", "NotApplicable")]
    [InlineData(0x6103, "vlv actual value conditioning sign", "9.5.5", "ValveActualValueConditioning", false, OdDataType.Integer8, OdAccess.ReadWrite, false, "Both/No", "Optional", null, "1", "NotApplicable", "NotApplicable")]
    [InlineData(0x6122, "vlv actual value conditioning area", "9.5.26", "ValveActualValueConditioning", true, OdDataType.Integer16, OdAccess.ReadWrite, false, "Both/No", "Conditional", "actual value conditioning is implemented and transducer type is 2", "None", "A7h", "-6")]
    [InlineData(0x6125, "vlv actual value conditioning max transducer signal", "9.5.29", "ValveActualValueConditioning", true, OdDataType.Integer16, OdAccess.ReadWrite, false, "Both/No", "Conditional", "actual value conditioning is implemented and transducer type is 2", "10", "26h", "0")]
    [InlineData(0x6129, "vlv actual value conditioning max general transducer", "9.5.31", "ValveActualValueConditioning", true, OdDataType.Integer16, OdAccess.ReadWrite, false, "Both/No", "Conditional", "actual value conditioning is implemented and transducer type is 5", "0", "26h", "0")]
    [InlineData(0x6146, "vlv pressure actual value referenced by interface A", "9.5.46", "ValveActualValueConditioning", true, OdDataType.Unsigned8, OdAccess.ReadOnly, false, "Transmit/Transmit", "Conditional", "actual value conditioning is implemented and transducer type is 3", "None", "ManufacturerSpecific", "ManufacturerSpecific")]
    [InlineData(0x6203, "drv actual value conditioning sign", "9.6.5", "DriveActualValueConditioning", false, OdDataType.Integer8, OdAccess.ReadWrite, false, "Both/No", "Optional", null, "1", "NotApplicable", "NotApplicable")]
    [InlineData(0x6210, "drv actual value conditioning actual value 1", "9.6.7", "DriveActualValueConditioning", true, OdDataType.Integer32, OdAccess.ReadOnly, false, "Transmit/Transmit", "Optional", null, "None", "TransducerSpecific", "TransducerSpecific")]
    [InlineData(0x6222, "drv actual value conditioning area", "9.6.17", "DriveActualValueConditioning", true, OdDataType.Integer32, OdAccess.ReadWrite, false, "Both/No", "Conditional", "actual value conditioning is implemented and transducer type is 2", "ManufacturerSpecific", "A7h", "-6")]
    [InlineData(0x6223, "drv actual value conditioning pressure offset", "9.6.18", "DriveActualValueConditioning", true, OdDataType.Integer32, OdAccess.ReadWrite, false, "Both/No", "Conditional", "actual value conditioning is implemented and transducer type is 2", "0", "4Eh", "0")]
    [InlineData(0x6291, "actuator conditioning filter T1 32-bit", "9.7.6", "ActuatorConditioning", true, OdDataType.Unsigned32, OdAccess.ReadWrite, false, "Both/No", "Conditional", "filter type 1 is implemented", "0", "03h", "-3")]
    [InlineData(0x62A3, "actuator conditioning directional dependent gain factor 2 32-bit", "9.7.14", "ActuatorConditioning", true, OdDataType.Unsigned32, OdAccess.ReadWrite, false, "Both/No", "Conditional", "directional dependent gain factor type 2 is implemented", "1000", "00h", "-6")]
    [InlineData(0x62E1, "actuator conditioning lower limit 32-bit", "9.7.24", "ActuatorConditioning", true, OdDataType.Integer32, OdAccess.ReadWrite, false, "Both/No", "Conditional", "limit function is implemented", "0", "ControlModeSpecific", "ControlModeSpecific")]
    [InlineData(0x6343, "actuator conditioning dead band compensation A side", "9.7.30", "ActuatorConditioning", true, OdDataType.Integer16, OdAccess.ReadWrite, false, "Both/No", "Conditional", "dead band compensation type is 1 or 2", "0", "ir", "0")]
    [InlineData(0x6347, "actuator conditioning directional dependent gain type 2 factor", "9.7.34", "ActuatorConditioning", true, OdDataType.Unsigned32, OdAccess.ReadWrite, false, "Both/No", "Conditional", "directional dependent gain type is 2", "1000", "00h", "-3")]
    [InlineData(0x6300, "q setpoint", "9.8.2", "ValveSpoolPosition", true, OdDataType.Integer16, OdAccess.ReadWrite, false, "Both/No", "Conditional", "16-bit parameters and control mode is 1 or 2", "0", "ir", "0")]
    [InlineData(0x6301, "Spool position actual value", "9.8.3", "ValveSpoolPosition", true, OdDataType.Integer16, OdAccess.ReadOnly, false, "Transmit/Transmit", "Conditional", "16-bit parameters and control mode is 2", "None", "ir", "0")]
    [InlineData(0x6308, "q setpoint 32-bit", "9.8.5", "ValveSpoolPosition", true, OdDataType.Integer32, OdAccess.ReadWrite, false, "Both/No", "Conditional", "32-bit parameters and control mode is 1 or 2", "0", "ir32", "0")]
    [InlineData(0x6309, "Spool actual value 32-bit", "9.8.6", "ValveSpoolPosition", true, OdDataType.Integer32, OdAccess.ReadOnly, false, "Transmit/Transmit", "Conditional", "32-bit parameters and control mode is 2", "None", "ir", "0")]
    [InlineData(0x6312, "Spool position setpoint", "9.8.9", "ValveSpoolPosition", true, OdDataType.Integer16, OdAccess.ReadOnly, false, "Transmit/No", "Optional", null, "0", "ir", "0")]
    [InlineData(0x6314, "vpoc setpoint conditioning hold setpoint", "9.9.3", "ValveSpoolPosition", true, OdDataType.Integer16, OdAccess.ReadWrite, false, "Both/No", "Optional", null, "0", "ir", "0")]
    [InlineData(0x6336, "vpoc setpoint conditioning ramp deceleration time negative", "9.9.18", "ValveSpoolPosition", true, OdDataType.Unsigned16, OdAccess.ReadWrite, false, "Both/No", "Conditional", "ramp type is 3", "0", "03h", "-3")]
    [InlineData(0x635A, "vpoc control monitoring lower threshold 32-bit", "9.10.8", "ValveSpoolPosition", true, OdDataType.Integer32, OdAccess.ReadWrite, false, "Both/No", "Conditional", "control monitoring type is 1", "DeviceSpecific", "ControlModeSpecific", "ControlModeSpecific")]
    [InlineData(0x6380, "vprc setpoint", "9.12.2", "ValvePressure", true, OdDataType.Integer16, OdAccess.ReadWrite, false, "Both/No", "Conditional", "16-bit parameters and control mode is 3 or 4", "0", "ir", "0")]
    [InlineData(0x6381, "vprc actual value", "9.12.3", "ValvePressure", true, OdDataType.Integer16, OdAccess.ReadOnly, false, "Transmit/Transmit", "Conditional", "16-bit parameters and control mode is 4", "None", "ir", "0")]
    [InlineData(0x6388, "vprc setpoint 32-bit", "9.12.5", "ValvePressure", true, OdDataType.Integer32, OdAccess.ReadWrite, false, "Both/No", "Conditional", "32-bit parameters and control mode is 3 or 4", "0", "ir32", "0")]
    [InlineData(0x6389, "vprc actual value 32-bit", "9.12.6", "ValvePressure", true, OdDataType.Integer32, OdAccess.ReadOnly, false, "Transmit/Transmit", "Conditional", "32-bit parameters and control mode is 3 or 4", "0", "ir", "0")]
    [InlineData(0x6394, "vprc setpoint conditioning hold setpoint", "9.13.3", "ValvePressure", true, OdDataType.Integer16, OdAccess.ReadWrite, false, "Both/No", "Optional", null, "0", "ir", "0")]
    [InlineData(0x63D8, "vprc control monitoring threshold 32-bit", "9.14.19", "ValvePressure", true, OdDataType.Integer32, OdAccess.ReadWrite, false, "Both/No", "Conditional", "control monitoring type is 2", "DeviceSpecific", "ControlModeSpecific", "ControlModeSpecific")]
    [InlineData(0x640D, "vpqc power limit factor", "9.16.2", "ValvePower", false, OdDataType.Unsigned32, OdAccess.ReadWrite, false, "Both/No", "Conditional", "16-bit parameters and control mode is 5", "65537", "NotApplicable", "NotApplicable")]
    [InlineData(0x647A, "vpqc target window monitoring lower threshold 32-bit", "9.20.10", "ValvePower", true, OdDataType.Integer32, OdAccess.ReadWrite, false, "Both/No", "Conditional", "target window monitoring type is 1", "DeviceSpecific", "ControlModeSpecific", "ControlModeSpecific")]
    [InlineData(0x6700, "vflc setpoint", "9.21.2", "ValveFlow", true, OdDataType.Integer16, OdAccess.ReadWrite, false, "Both/No", "Conditional", "16-bit parameters and control mode is 13, 14 or 15", "0", "A9h", "-3")]
    [InlineData(0x6701, "vflc actual value", "9.21.3", "ValveFlow", true, OdDataType.Integer16, OdAccess.ReadOnly, false, "Transmit/Transmit", "Conditional", "16-bit parameters and control mode is 13, 14 or 15", "None", "A9h", "-3")]
    [InlineData(0x6708, "vflc setpoint 32-bit", "9.21.5", "ValveFlow", true, OdDataType.Integer32, OdAccess.ReadWrite, false, "Both/No", "Conditional", "32-bit parameters and control mode is 13, 14 or 15", "0", "A9h", "-3")]
    [InlineData(0x6709, "vflc actual value 32-bit", "9.21.6", "ValveFlow", true, OdDataType.Integer32, OdAccess.ReadOnly, false, "Transmit/Transmit", "Conditional", "32-bit parameters and control mode is 13, 14 or 15", "None", "A9h", "-3")]
    [InlineData(0x6714, "vflc setpoint conditioning hold setpoint", "9.22.3", "ValveFlow", true, OdDataType.Integer16, OdAccess.ReadWrite, false, "Both/No", "Optional", null, "0", "ir", "0")]
    [InlineData(0x6780, "vpflcpl power limit", "9.31.2", "ValveFlow", true, OdDataType.Integer32, OdAccess.ReadWrite, false, "Both/No", "Conditional", "control mode 15 is supported", "0", "24h", "-3")]
    [InlineData(0x6781, "vpflcpl power actual value", "9.31.3", "ValveFlow", true, OdDataType.Integer32, OdAccess.ReadOnly, false, "Transmit/Transmit", "Conditional", "control mode 15 is supported", "None", "24h", "-3")]
    [InlineData(0x6480, "dcol setpoint", "9.32.2", "DriveOpenLoop", true, OdDataType.Integer32, OdAccess.ReadWrite, false, "Both/No", "Conditional", "control mode 6 is supported", "0", "A1h", "-3")]
    [InlineData(0x6492, "dcol setpoint conditioning reference A value", "9.33.2", "DriveOpenLoop", true, OdDataType.Integer32, OdAccess.ReadWrite, false, "Both/No", "Optional", null, "0", "ControlModeSpecific", "ControlModeSpecific")]
    [InlineData(0x6500, "dsc setpoint", "9.37.2", "DriveVelocity", true, OdDataType.Integer32, OdAccess.ReadWrite, false, "Both/No", "Conditional", "control mode is 7", "0", "A1h", "-3")]
    [InlineData(0x6501, "dsc actual value", "9.37.3", "DriveVelocity", true, OdDataType.Integer32, OdAccess.ReadOnly, false, "Transmit/Transmit", "Conditional", "16-bit parameters and control mode is 7", "None", "A1h", "-3")]
    [InlineData(0x6520, "dsc setpoint conditioning upper limit", "9.38.6", "DriveVelocity", true, OdDataType.Integer32, OdAccess.ReadWrite, false, "Both/No", "Conditional", "limit function is implemented", "2147483647", "ControlModeSpecific", "ControlModeSpecific")]
    [InlineData(0x6580, "dfpc setpoint", "9.42.2", "DrivePressure", true, OdDataType.Integer32, OdAccess.ReadWrite, false, "Both/No", "Conditional", "control mode is 8", "0", "4Eh", "-3")]
    [InlineData(0x6581, "dfpc actual value", "9.42.3", "DrivePressure", true, OdDataType.Integer32, OdAccess.ReadOnly, false, "Transmit/Transmit", "Conditional", "16-bit parameters and control mode is 8", "None", "4Eh", "-3")]
    [InlineData(0x6587, "dfpc pressure sample time", "9.42.12", "DrivePressure", true, OdDataType.Unsigned32, OdAccess.ReadWrite, false, "Both/No", "Conditional", "16-bit parameters and control mode is 8", "ManufacturerSpecific", "03h", "-6")]
    [InlineData(0x65D1, "dfpc control monitoring type", "9.45.2", "DrivePressure", false, OdDataType.Integer8, OdAccess.ReadWrite, false, "Both/No", "Conditional", "control monitoring is implemented", "0", "NotApplicable", "NotApplicable")]
    [InlineData(0x65D3, "dfpc control monitoring threshold", "9.45.4", "DrivePressure", true, OdDataType.Integer32, OdAccess.ReadWrite, false, "Both/No", "Conditional", "control monitoring type is 2", "DeviceSpecific", "ControlModeSpecific", "ControlModeSpecific")]
    [InlineData(0x6600, "dpc setpoint", "9.47.2", "DrivePosition", true, OdDataType.Integer32, OdAccess.ReadWrite, false, "Both/No", "Conditional", "control mode is 9", "0", "01h", "-6")]
    [InlineData(0x6601, "dpc actual value", "9.47.3", "DrivePosition", true, OdDataType.Integer32, OdAccess.ReadOnly, false, "Transmit/Transmit", "Conditional", "16-bit parameters and control mode is 9", "None", "01h", "-6")]
    [InlineData(0x6603, "dpc Kp", "9.47.5", "DrivePosition", true, OdDataType.Unsigned32, OdAccess.ReadWrite, false, "Both/No", "Conditional", "16-bit parameters and control mode is 9", "0", "A3h", "-3")]
    [InlineData(0x6605, "dpc T1", "9.47.7", "DrivePosition", true, OdDataType.Unsigned32, OdAccess.ReadWrite, false, "Both/No", "Conditional", "16-bit parameters and control mode is 9", "0", "03h", "-3")]
    [InlineData(0x6614, "dpc setpoint conditioning hold setpoint", "9.47.16", "DrivePosition", true, OdDataType.Integer32, OdAccess.ReadWrite, false, "Both/No", "Optional", null, "0", "ControlModeSpecific", "ControlModeSpecific")]
    [InlineData(0x62B1, "actuator conditioning dead band compensation A side 32-bit", "9.7.16", "ActuatorConditioning", true, OdDataType.Integer32, OdAccess.ReadWrite, false, "Both/No", "Conditional", "dead band compensation type is 1 or 2", "0", "00h", "-6")]
    [InlineData(0x62B2, "actuator conditioning dead band compensation B side 32-bit", "9.7.17", "ActuatorConditioning", true, OdDataType.Integer32, OdAccess.ReadWrite, false, "Both/No", "Conditional", "dead band compensation type is 1 or 2", "0", "00h", "-6")]
    [InlineData(0x62B3, "actuator conditioning dead band compensation threshold 32-bit", "9.7.18", "ActuatorConditioning", true, OdDataType.Integer32, OdAccess.ReadWrite, false, "Both/No", "Conditional", "dead band compensation type is 1 or 2", "DeviceSpecific", "00h", "-6")]
    [InlineData(0x62C0, "actuator conditioning zero correction offset 32-bit", "9.7.19", "ActuatorConditioning", true, OdDataType.Integer32, OdAccess.ReadWrite, false, "Both/No", "Conditional", "zero correction function is implemented", "0", "00h", "-6")]
    [InlineData(0x62D1, "actuator conditioning dither amplitude 32-bit", "9.7.21", "ActuatorConditioning", true, OdDataType.Integer32, OdAccess.ReadWrite, false, "Both/No", "Conditional", "dither function type is 1, 2 or 3", "0", "00h", "-6")]
    [InlineData(0x6324, "actuator conditioning zero correction offset", "9.7.26", "ActuatorConditioning", true, OdDataType.Integer16, OdAccess.ReadWrite, false, "Both/No", "Conditional", "zero correction function is implemented", "0", "ir", "0")]
    [InlineData(0x670A, "vflc setpoint conditioning demand value 32-bit", "9.21.7", "ValveFlow", true, OdDataType.Integer32, OdAccess.ReadOnly, false, "Transmit/Transmit", "Optional", null, "None", "ControlModeSpecific", "ControlModeSpecific")]
    [InlineData(0x677A, "vflc target window monitoring lower threshold 32-bit", "9.25.10", "ValveFlow", true, OdDataType.Integer32, OdAccess.ReadWrite, false, "Both/No", "Conditional", "target window monitoring type is 1", "DeviceSpecific", "ControlModeSpecific", "ControlModeSpecific")]
    public void A_Definition_Holds_What_The_Page_Shows(
        ushort index, string name, string clause, string area, bool isValueRecord, OdDataType valueType,
        OdAccess access, bool accessMayBeReadOnly, string pdo, string category, string? condition,
        string defaultValue, string defaultUnit, string defaultPrefix)
    {
        var d = Get(index);
        d.Index.Should().Be(index);
        d.Name.Should().Be(name);
        d.Clause.Should().Be(clause);
        d.Area.Should().Be((Cia408Area)Enum.Parse(typeof(Cia408Area), area));
        d.IsValueRecord.Should().Be(isValueRecord);
        d.ValueType.Should().Be(valueType);
        d.Access.Should().Be(access);
        d.AccessMayBeReadOnly.Should().Be(accessMayBeReadOnly);
        var pdoParts = pdo.Split('/');
        d.PdoAccess.Should().Be((Cia408PdoAccess)Enum.Parse(typeof(Cia408PdoAccess), pdoParts[0]));
        d.PdoAccessMinimum.Should().Be((Cia408PdoAccess)Enum.Parse(typeof(Cia408PdoAccess), pdoParts[1]));
        d.Category.Should().Be((Cia408Category)Enum.Parse(typeof(Cia408Category), category));
        d.Condition.Should().Be(condition);

        ParseDefault(defaultValue, out var valueKind, out var value, out var text);
        d.DefaultValueKind.Should().Be(valueKind, "the kind of the default value");
        d.DefaultValue.Should().Be(value, "the default value");
        d.DefaultText.Should().Be(text, "the default text");

        ParseUnit(defaultUnit, out var unitKind, out var unit);
        d.DefaultUnitKind.Should().Be(unitKind, "the kind of the default unit");
        d.DefaultUnit.Should().Be(unit, "the default unit");

        ParsePrefix(defaultPrefix, out var prefixKind, out var prefix);
        d.DefaultPrefixKind.Should().Be(prefixKind, "the kind of the default prefix");
        d.DefaultPrefix.Should().Be(prefix, "the default prefix");
    }

    private static void ParseDefault(string token, out Cia408DefaultKind kind, out long? value, out string? text)
    {
        value = null;
        text = null;
        if (token.StartsWith("Text:", StringComparison.Ordinal))
        {
            kind = Cia408DefaultKind.Value;
            text = token.Substring(5);
        }
        else if (char.IsLetter(token[0]))
            kind = (Cia408DefaultKind)Enum.Parse(typeof(Cia408DefaultKind), token);
        else
        {
            kind = Cia408DefaultKind.Value;
            value = long.Parse(token, CultureInfo.InvariantCulture);
        }
    }

    private static void ParseUnit(string token, out Cia408DefaultKind kind, out byte? unit)
    {
        unit = null;
        if (token == "ir")
            kind = Cia408DefaultKind.InternalResolution;
        else if (token == "ir32")
            kind = Cia408DefaultKind.InternalResolution32;
        else if (token.EndsWith("h", StringComparison.Ordinal))
        {
            kind = Cia408DefaultKind.Value;
            unit = byte.Parse(token.Substring(0, token.Length - 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        else
            kind = (Cia408DefaultKind)Enum.Parse(typeof(Cia408DefaultKind), token);
    }

    private static void ParsePrefix(string token, out Cia408DefaultKind kind, out sbyte? prefix)
    {
        prefix = null;
        if (sbyte.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
        {
            kind = Cia408DefaultKind.Value;
            prefix = number;
        }
        else
            kind = (Cia408DefaultKind)Enum.Parse(typeof(Cia408DefaultKind), token);
    }

    // ---- rules over every definition ----------------------------------------------------------

    [Fact]
    public void A_Variable_Has_No_Unit_Or_Prefix_And_A_Value_Record_Always_Says_Something_About_Both()
    {
        foreach (var d in Cia408Objects.All)
        {
            if (d.IsValueRecord)
            {
                d.DefaultUnitKind.Should().NotBe(Cia408DefaultKind.NotApplicable, $"{d.Index:X4}h");
                d.DefaultPrefixKind.Should().NotBe(Cia408DefaultKind.NotApplicable, $"{d.Index:X4}h");
            }
            else
            {
                d.DefaultUnitKind.Should().Be(Cia408DefaultKind.NotApplicable, $"{d.Index:X4}h");
                d.DefaultPrefixKind.Should().Be(Cia408DefaultKind.NotApplicable, $"{d.Index:X4}h");
            }
        }
    }

    [Fact]
    public void A_Value_Is_Present_Exactly_Where_The_Kind_Says_Value()
    {
        foreach (var d in Cia408Objects.All)
        {
            bool text = d.DefaultText != null;
            (d.DefaultValue.HasValue || text).Should().Be(d.DefaultValueKind == Cia408DefaultKind.Value, $"{d.Index:X4}h");
            (text && d.ValueType != VisibleString).Should().BeFalse($"{d.Index:X4}h");
            d.DefaultUnit.HasValue.Should().Be(d.DefaultUnitKind == Cia408DefaultKind.Value, $"{d.Index:X4}h");
            d.DefaultPrefix.HasValue.Should().Be(d.DefaultPrefixKind == Cia408DefaultKind.Value, $"{d.Index:X4}h");
        }
    }

    [Fact]
    public void A_Default_Value_Lies_In_The_Range_Of_The_Value_Type()
    {
        foreach (var d in Cia408Objects.All.Where(d => d.DefaultValue.HasValue))
        {
            long v = d.DefaultValue!.Value;
            var (min, max) = d.ValueType switch
            {
                Unsigned8 => (0L, (long)byte.MaxValue),
                Unsigned16 => (0L, ushort.MaxValue),
                Unsigned32 => (0L, uint.MaxValue),
                Integer8 => (sbyte.MinValue, sbyte.MaxValue),
                Integer16 => (short.MinValue, short.MaxValue),
                Integer32 => (int.MinValue, (long)int.MaxValue),
                _ => throw new InvalidOperationException($"{d.Index:X4}h has a numeric default and type {d.ValueType}"),
            };
            v.Should().BeInRange(min, max, $"{d.Index:X4}h");
        }
    }

    [Fact]
    public void Conditional_Objects_And_Only_Those_Carry_A_Condition()
    {
        foreach (var d in Cia408Objects.All)
            (d.Condition != null).Should().Be(d.Category == Cia408Category.Conditional, $"{d.Index:X4}h");
        Cia408Objects.All.Count(d => d.Category == Cia408Category.Conditional).Should().Be(313); // 63D8h counts once, as the Conditional §9.14.19 object
        Cia408Objects.All.Where(d => d.Category == Cia408Category.Mandatory).Select(d => (int)d.Index)
            .Should().Equal(0x6040, 0x6041, 0x6042, 0x6044, 0x6045);
    }

    [Fact]
    public void The_Least_Permissive_Access_Never_Exceeds_The_Most_Permissive()
    {
        foreach (var d in Cia408Objects.All)
        {
            ((d.PdoAccessMinimum & ~d.PdoAccess) == 0).Should().BeTrue($"{d.Index:X4}h");
            if (d.AccessMayBeReadOnly)
                d.Access.Should().Be(OdAccess.ReadWrite, $"{d.Index:X4}h");
        }

        Cia408Objects.All.Where(d => d.AccessMayBeReadOnly).Select(d => (int)d.Index)
            .Should().Equal(0x6042, 0x6043, 0x604F);
    }

    [Fact]
    public void Every_Object_Cites_A_Clause_Of_Chapter_9_And_Has_A_Name()
    {
        foreach (var d in Cia408Objects.All)
        {
            d.Clause.Should().MatchRegex(@"^9\.\d{1,2}\.\d{1,2}(\.\d)?$", $"{d.Index:X4}h");
            d.Name.Should().NotBeNullOrWhiteSpace();
        }

        Get(0x6359).Clause.Should().Be("9.10.7.1"); // the norm's own level-4 number
    }

    // ---- the rulings on the norm's own defects ------------------------------------------------

    [Fact]
    public void Index_63D8h_Is_The_Pressure_Control_Monitoring_Threshold_Of_Clause_9_14_19()
    {
        // §9.12.10 (control deviation 32-bit) and §9.14.19 (control monitoring threshold 32-bit)
        // both define 63D8h. Annex A (Table 1027) and CiA 408-C Table A.2 give it the threshold's
        // access, rw / TR: the catalogue holds §9.14.19.
        var d = Get(0x63D8);
        d.Name.Should().Be("vprc control monitoring threshold 32-bit");
        d.Clause.Should().Be("9.14.19");
        d.Category.Should().Be(Cia408Category.Conditional);
        d.Access.Should().Be(OdAccess.ReadWrite);
        d.PdoAccess.Should().Be(Cia408PdoAccess.Both);
        Cia408Objects.All.Should().ContainSingle(o => o.Index == 0x63D8);
        Cia408Objects.All.Should().NotContain(o => o.Name == "vprc control deviation 32-bit");
    }

    [Fact]
    public void The_Six_Prefixes_Printed_FAh_Mili_Hold_The_Printed_Code_Micro()
    {
        // 62A3h, 62B1h to 62B3h, 62C0h, 62D1h print "FAh, mili": the code is taken, FAh = -6.
        foreach (ushort index in new ushort[] { 0x62A3, 0x62B1, 0x62B2, 0x62B3, 0x62C0, 0x62D1 })
        {
            Get(index).DefaultPrefixKind.Should().Be(Cia408DefaultKind.Value, $"{index:X4}h");
            Get(index).DefaultPrefix.Should().Be(-6, $"{index:X4}h");
        }

        Get(0x6347).DefaultPrefix.Should().Be(-3); // the 16-bit sibling of 62A3h prints FDh (mili)
    }

    [Fact]
    public void The_Two_Headings_Whose_Tables_Carry_Another_Index_Are_Catalogued_By_The_Heading()
    {
        Get(0x65D1).Name.Should().Be("dfpc control monitoring type"); // table: 63D1h
        Get(0x6605).Name.Should().Be("dpc T1");                       // table: 6604h
        Get(0x63D1).Clause.Should().Be("9.14.14");                     // the real 63D1h
        Get(0x6604).Name.Should().Be("dpc Td");                       // the real 6604h
    }

    [Fact]
    public void The_Power_Limitation_Objects_Of_Clause_9_31_Are_Defined_Although_Figure_4_Reserves_Their_Range()
    {
        Get(0x6780).Area.Should().Be(Cia408Area.ValveFlow);
        Get(0x6781).Area.Should().Be(Cia408Area.ValveFlow);
    }
}
