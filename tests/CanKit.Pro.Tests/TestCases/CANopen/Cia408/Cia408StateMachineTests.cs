using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AwesomeAssertions;
using CanKit.Pro.CANopen.Cia408;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Cia408;

/// <summary>
/// The CiA 408 v2.2.0 device state machine (§7.6 Figure 1, Tables 4 and 6; §7.7 Tables 7 and 9),
/// tested as the norm's tables. Every expected successor and D-list below is written out by hand
/// from the rendered tables, not computed: a test that recomputed the rule would agree with any
/// implementation of it, right or wrong. Each start state is reached through public calls only,
/// so the machine has no way into a state that the norm's transitions do not lead to.
/// </summary>
public class Cia408StateMachineTests
{
    // ---- reaching a start state -----------------------------------------------------------------

    private static Cia408StateMachine In(Cia408State state)
    {
        var machine = new Cia408StateMachine();
        switch (state)
        {
            case Cia408State.NotReady:
                break;
            case Cia408State.Init:
                machine.Start();
                break;
            case Cia408State.Disabled:
                machine.Start();
                machine.ApplyControlWord(0b0000, 0b0001);
                break;
            case Cia408State.Hold:
                machine.Start();
                machine.ApplyControlWord(0b0000, 0b0011);
                break;
            case Cia408State.Active:
                machine.Start();
                machine.ApplyControlWord(0b0000, 0b0111);
                break;
            case Cia408State.FaultInit:
                machine.Start();
                machine.Fault(Cia408FaultState.FaultInit);
                break;
            case Cia408State.FaultDisabled:
                machine.Start();
                machine.ApplyControlWord(0b0000, 0b0001);
                machine.Fault(Cia408FaultState.FaultDisabled);
                break;
            case Cia408State.FaultHold:
                machine.Start();
                machine.ApplyControlWord(0b0000, 0b0011);
                machine.Fault(Cia408FaultState.FaultHold);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, "not a resting state");
        }

        machine.State.Should().Be(state, "the arrangement must reach the start state it names");
        return machine;
    }

    /// <summary>The eight states a machine can rest in. FAULT_REACTION is not one of them.</summary>
    private static readonly Cia408State[] RestingStates =
    {
        Cia408State.NotReady, Cia408State.Init, Cia408State.Disabled, Cia408State.Hold,
        Cia408State.Active, Cia408State.FaultInit, Cia408State.FaultDisabled, Cia408State.FaultHold,
    };

    private static Cia408State Parse(string state) => (Cia408State)Enum.Parse(typeof(Cia408State), state);

    // ---- Review Focus 4: the 9 x 16 x 2 table ---------------------------------------------------

    // Read from Table 7 (CiA 408 v2.2.0 §7.7, p26-27). A reset is evaluated once, in the state the
    // write arrives in, and needs both words as Table 7 writes them; then the level transitions are
    // followed until none applies:
    //   D2 INIT->DISABLED xxx1   D3 DISABLED->HOLD xx11   D4 HOLD->ACTIVE x111
    //   D5 ACTIVE->HOLD x0xx     D6 HOLD->DISABLED x00x   D7 DISABLED->INIT x000
    //   D10 FAULT_DISABLED->DISABLED 0001 to 1001   D11 FAULT_HOLD->HOLD 0011 to 1011
    //   D14 FAULT_INIT->INIT 0000 to 1000           D15 FAULT_HOLD->FAULT_DISABLED x001
    //   D16 FAULT_DISABLED->FAULT_INIT x000
    //
    // Outside the fault states: one line per (start state, bits 3..0 of the written control word,
    // R M H D). The two columns are the outcome with bit 3 of the previous control word at 0 and at
    // 1: successor state and the D-numbers taken, in order. The previous word's bits 2..0 equal the
    // written word's; no transition from these states looks at the previous word.
    private static readonly string[] ControlWordTable =
    {
        // state          bits   previous bit 3 = 0         previous bit 3 = 1
        "NotReady         0000   NotReady []                NotReady []",
        "NotReady         0001   NotReady []                NotReady []",
        "NotReady         0010   NotReady []                NotReady []",
        "NotReady         0011   NotReady []                NotReady []",
        "NotReady         0100   NotReady []                NotReady []",
        "NotReady         0101   NotReady []                NotReady []",
        "NotReady         0110   NotReady []                NotReady []",
        "NotReady         0111   NotReady []                NotReady []",
        "NotReady         1000   NotReady []                NotReady []",
        "NotReady         1001   NotReady []                NotReady []",
        "NotReady         1010   NotReady []                NotReady []",
        "NotReady         1011   NotReady []                NotReady []",
        "NotReady         1100   NotReady []                NotReady []",
        "NotReady         1101   NotReady []                NotReady []",
        "NotReady         1110   NotReady []                NotReady []",
        "NotReady         1111   NotReady []                NotReady []",

        "Init             0000   Init []                    Init []",
        "Init             0001   Disabled [2]               Disabled [2]",
        "Init             0010   Init []                    Init []",
        "Init             0011   Hold [2,3]                 Hold [2,3]",
        "Init             0100   Init []                    Init []",
        "Init             0101   Disabled [2]               Disabled [2]",
        "Init             0110   Init []                    Init []",
        "Init             0111   Active [2,3,4]             Active [2,3,4]",
        "Init             1000   Init []                    Init []",
        "Init             1001   Disabled [2]               Disabled [2]",
        "Init             1010   Init []                    Init []",
        "Init             1011   Hold [2,3]                 Hold [2,3]",
        "Init             1100   Init []                    Init []",
        "Init             1101   Disabled [2]               Disabled [2]",
        "Init             1110   Init []                    Init []",
        "Init             1111   Active [2,3,4]             Active [2,3,4]",

        "Disabled         0000   Init [7]                   Init [7]",
        "Disabled         0001   Disabled []                Disabled []",
        "Disabled         0010   Disabled []                Disabled []",
        "Disabled         0011   Hold [3]                   Hold [3]",
        "Disabled         0100   Disabled []                Disabled []",
        "Disabled         0101   Disabled []                Disabled []",
        "Disabled         0110   Disabled []                Disabled []",
        "Disabled         0111   Active [3,4]               Active [3,4]",
        "Disabled         1000   Init [7]                   Init [7]",
        "Disabled         1001   Disabled []                Disabled []",
        "Disabled         1010   Disabled []                Disabled []",
        "Disabled         1011   Hold [3]                   Hold [3]",
        "Disabled         1100   Disabled []                Disabled []",
        "Disabled         1101   Disabled []                Disabled []",
        "Disabled         1110   Disabled []                Disabled []",
        "Disabled         1111   Active [3,4]               Active [3,4]",

        "Hold             0000   Init [6,7]                 Init [6,7]",
        "Hold             0001   Disabled [6]               Disabled [6]",
        "Hold             0010   Hold []                    Hold []",
        "Hold             0011   Hold []                    Hold []",
        "Hold             0100   Hold []                    Hold []",
        "Hold             0101   Hold []                    Hold []",
        "Hold             0110   Hold []                    Hold []",
        "Hold             0111   Active [4]                 Active [4]",
        "Hold             1000   Init [6,7]                 Init [6,7]",
        "Hold             1001   Disabled [6]               Disabled [6]",
        "Hold             1010   Hold []                    Hold []",
        "Hold             1011   Hold []                    Hold []",
        "Hold             1100   Hold []                    Hold []",
        "Hold             1101   Hold []                    Hold []",
        "Hold             1110   Hold []                    Hold []",
        "Hold             1111   Active [4]                 Active [4]",

        "Active           0000   Init [5,6,7]               Init [5,6,7]",
        "Active           0001   Disabled [5,6]             Disabled [5,6]",
        "Active           0010   Hold [5]                   Hold [5]",
        "Active           0011   Hold [5]                   Hold [5]",
        "Active           0100   Active []                  Active []",
        "Active           0101   Active []                  Active []",
        "Active           0110   Active []                  Active []",
        "Active           0111   Active []                  Active []",
        "Active           1000   Init [5,6,7]               Init [5,6,7]",
        "Active           1001   Disabled [5,6]             Disabled [5,6]",
        "Active           1010   Hold [5]                   Hold [5]",
        "Active           1011   Hold [5]                   Hold [5]",
        "Active           1100   Active []                  Active []",
        "Active           1101   Active []                  Active []",
        "Active           1110   Active []                  Active []",
        "Active           1111   Active []                  Active []",
    };

    // The fault states: one line per (start state, bits 3..0 of the written word). A reset looks
    // at the previous word as well, so it varies in bits 3..0 here: bit 3 at 0 or 1, and bits 2..0
    // at the state's own pattern P or at another pattern Q (see FaultPatterns). Columns: previous
    // 0P, 1P, 0Q, 1Q. Q is the pattern a fall from the state would leave behind where there is one
    // (FAULT_HOLD -> 001, FAULT_DISABLED -> 000), so the rows also show that a fall and a reset are
    // never taken by one write; FAULT_INIT has no fall and takes 001.
    private static readonly string[] FaultStateTable =
    {
        // state          bits   0P                     1P                     0Q                     1Q
        "FaultInit        0000   FaultInit []           FaultInit []           FaultInit []           FaultInit []",
        "FaultInit        0001   FaultInit []           FaultInit []           FaultInit []           FaultInit []",
        "FaultInit        0010   FaultInit []           FaultInit []           FaultInit []           FaultInit []",
        "FaultInit        0011   FaultInit []           FaultInit []           FaultInit []           FaultInit []",
        "FaultInit        0100   FaultInit []           FaultInit []           FaultInit []           FaultInit []",
        "FaultInit        0101   FaultInit []           FaultInit []           FaultInit []           FaultInit []",
        "FaultInit        0110   FaultInit []           FaultInit []           FaultInit []           FaultInit []",
        "FaultInit        0111   FaultInit []           FaultInit []           FaultInit []           FaultInit []",
        "FaultInit        1000   Init [14]              FaultInit []           FaultInit []           FaultInit []",
        "FaultInit        1001   FaultInit []           FaultInit []           FaultInit []           FaultInit []",
        "FaultInit        1010   FaultInit []           FaultInit []           FaultInit []           FaultInit []",
        "FaultInit        1011   FaultInit []           FaultInit []           FaultInit []           FaultInit []",
        "FaultInit        1100   FaultInit []           FaultInit []           FaultInit []           FaultInit []",
        "FaultInit        1101   FaultInit []           FaultInit []           FaultInit []           FaultInit []",
        "FaultInit        1110   FaultInit []           FaultInit []           FaultInit []           FaultInit []",
        "FaultInit        1111   FaultInit []           FaultInit []           FaultInit []           FaultInit []",

        "FaultDisabled    0000   FaultInit [16]         FaultInit [16]         FaultInit [16]         FaultInit [16]",
        "FaultDisabled    0001   FaultDisabled []       FaultDisabled []       FaultDisabled []       FaultDisabled []",
        "FaultDisabled    0010   FaultDisabled []       FaultDisabled []       FaultDisabled []       FaultDisabled []",
        "FaultDisabled    0011   FaultDisabled []       FaultDisabled []       FaultDisabled []       FaultDisabled []",
        "FaultDisabled    0100   FaultDisabled []       FaultDisabled []       FaultDisabled []       FaultDisabled []",
        "FaultDisabled    0101   FaultDisabled []       FaultDisabled []       FaultDisabled []       FaultDisabled []",
        "FaultDisabled    0110   FaultDisabled []       FaultDisabled []       FaultDisabled []       FaultDisabled []",
        "FaultDisabled    0111   FaultDisabled []       FaultDisabled []       FaultDisabled []       FaultDisabled []",
        "FaultDisabled    1000   FaultInit [16]         FaultInit [16]         FaultInit [16]         FaultInit [16]",
        "FaultDisabled    1001   Disabled [10]          FaultDisabled []       FaultDisabled []       FaultDisabled []",
        "FaultDisabled    1010   FaultDisabled []       FaultDisabled []       FaultDisabled []       FaultDisabled []",
        "FaultDisabled    1011   FaultDisabled []       FaultDisabled []       FaultDisabled []       FaultDisabled []",
        "FaultDisabled    1100   FaultDisabled []       FaultDisabled []       FaultDisabled []       FaultDisabled []",
        "FaultDisabled    1101   FaultDisabled []       FaultDisabled []       FaultDisabled []       FaultDisabled []",
        "FaultDisabled    1110   FaultDisabled []       FaultDisabled []       FaultDisabled []       FaultDisabled []",
        "FaultDisabled    1111   FaultDisabled []       FaultDisabled []       FaultDisabled []       FaultDisabled []",

        "FaultHold        0000   FaultHold []           FaultHold []           FaultHold []           FaultHold []",
        "FaultHold        0001   FaultDisabled [15]     FaultDisabled [15]     FaultDisabled [15]     FaultDisabled [15]",
        "FaultHold        0010   FaultHold []           FaultHold []           FaultHold []           FaultHold []",
        "FaultHold        0011   FaultHold []           FaultHold []           FaultHold []           FaultHold []",
        "FaultHold        0100   FaultHold []           FaultHold []           FaultHold []           FaultHold []",
        "FaultHold        0101   FaultHold []           FaultHold []           FaultHold []           FaultHold []",
        "FaultHold        0110   FaultHold []           FaultHold []           FaultHold []           FaultHold []",
        "FaultHold        0111   FaultHold []           FaultHold []           FaultHold []           FaultHold []",
        "FaultHold        1000   FaultHold []           FaultHold []           FaultHold []           FaultHold []",
        "FaultHold        1001   FaultDisabled [15]     FaultDisabled [15]     FaultDisabled [15]     FaultDisabled [15]",
        "FaultHold        1010   FaultHold []           FaultHold []           FaultHold []           FaultHold []",
        "FaultHold        1011   Hold [11]              FaultHold []           FaultHold []           FaultHold []",
        "FaultHold        1100   FaultHold []           FaultHold []           FaultHold []           FaultHold []",
        "FaultHold        1101   FaultHold []           FaultHold []           FaultHold []           FaultHold []",
        "FaultHold        1110   FaultHold []           FaultHold []           FaultHold []           FaultHold []",
        "FaultHold        1111   FaultHold []           FaultHold []           FaultHold []           FaultHold []",

        // FAULT_REACTION is not reachable as a resting state: Fault() resolves it within the same
        // call (Table 9), so no control word ever meets it. Pinned by
        // No_Public_Call_Leaves_The_Machine_In_Fault_Reaction below.
    };

    // P and Q of each fault state, bits 2..0 (Table 7: D14 000, D10 001, D11 011).
    private static readonly Dictionary<Cia408State, (uint Pattern, uint Other)> FaultPatterns = new()
    {
        [Cia408State.FaultInit] = (0b000, 0b001),
        [Cia408State.FaultDisabled] = (0b001, 0b000),
        [Cia408State.FaultHold] = (0b011, 0b001),
    };

    public static TheoryData<string, int> ControlWordRows()
    {
        var data = new TheoryData<string, int>();
        foreach (var line in ControlWordTable)
        {
            data.Add(line, 0);
            data.Add(line, 1);
        }

        return data;
    }

    public static TheoryData<string, int> FaultStateRows()
    {
        var data = new TheoryData<string, int>();
        foreach (var line in FaultStateTable)
        {
            for (var column = 0; column < 4; column++) data.Add(line, column);
        }

        return data;
    }

    [Fact]
    public void The_Table_Covers_Every_Resting_State_And_Every_Value_Of_Bits_3_To_0()
    {
        var keys = ControlWordTable.Concat(FaultStateTable)
            .Select(line => line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            .Select(cells => (Parse(cells[0]), Convert.ToUInt32(cells[1], 2)))
            .ToList();

        keys.Should().OnlyHaveUniqueItems();
        keys.Should().HaveCount(RestingStates.Length * 16);
        keys.Select(k => k.Item1).Distinct().Should().BeEquivalentTo(RestingStates);
    }

    [Theory]
    [MemberData(nameof(ControlWordRows))]
    public void A_Control_Word_Takes_Exactly_The_Transitions_Of_Table_7(string row, int previousBit3)
    {
        // state, bits, then (successor, D-list) for previous bit 3 = 0 and for previous bit 3 = 1.
        var cells = row.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var start = Parse(cells[0]);
        var current = Convert.ToUInt32(cells[1], 2);
        var expectedState = Parse(cells[2 + 2 * previousBit3]);
        var expectedSteps = ParseSteps(cells[3 + 2 * previousBit3]);
        var previous = ((uint)previousBit3 << 3) | (current & 0b0111);

        var machine = In(start);
        var taken = machine.ApplyControlWord(previous, current);

        taken.Should().Equal(expectedSteps);
        machine.State.Should().Be(expectedState);
    }

    [Theory]
    [MemberData(nameof(FaultStateRows))]
    public void A_Control_Word_In_A_Fault_State_Takes_Exactly_The_Transitions_Of_Table_7(string row, int column)
    {
        // state, bits, then (successor, D-list) for the previous words 0P, 1P, 0Q, 1Q.
        var cells = row.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var start = Parse(cells[0]);
        var current = Convert.ToUInt32(cells[1], 2);
        var expectedState = Parse(cells[2 + 2 * column]);
        var expectedSteps = ParseSteps(cells[3 + 2 * column]);
        var (pattern, other) = FaultPatterns[start];
        var previous = (column % 2 == 1 ? 0b1000u : 0u) | (column < 2 ? pattern : other);

        var machine = In(start);
        var taken = machine.ApplyControlWord(previous, current);

        taken.Should().Equal(expectedSteps);
        machine.State.Should().Be(expectedState);
    }

    private static int[] ParseSteps(string cell)
    {
        var inner = cell.Trim('[', ']');
        return inner.Length == 0
            ? Array.Empty<int>()
            : inner.Split(',').Select(n => int.Parse(n, CultureInfo.InvariantCulture)).ToArray();
    }

    // ---- the readings, named --------------------------------------------------------------------

    [Fact]
    public void A_Control_Word_Is_Followed_Up_To_The_Stable_State()
    {
        var machine = In(Cia408State.Init);

        machine.ApplyControlWord(0b0000, 0b0111).Should().Equal(2, 3, 4);
        machine.State.Should().Be(Cia408State.Active);
    }

    [Fact]
    public void A_Control_Word_Is_Followed_Down_To_The_Stable_State()
    {
        var machine = In(Cia408State.Active);

        machine.ApplyControlWord(0b0111, 0b0000).Should().Equal(5, 6, 7);
        machine.State.Should().Be(Cia408State.Init);
    }

    [Fact]
    public void Leaving_Active_With_Hold_Still_Requested_Stops_In_Hold()
    {
        // D5 (x0xx) applies; D6 (x00x) does not, because H is still 1.
        var machine = In(Cia408State.Active);

        machine.ApplyControlWord(0b0111, 0b0011).Should().Equal(5);
        machine.State.Should().Be(Cia408State.Hold);
    }

    [Fact]
    public void A_Fault_Reset_Is_The_Edge_Of_Bit_3_Not_Its_Level()
    {
        var machine = In(Cia408State.FaultHold);

        machine.ApplyControlWord(0b1011, 0b1011).Should().BeEmpty("bit 3 stayed at 1: no edge");
        machine.State.Should().Be(Cia408State.FaultHold);

        machine.ApplyControlWord(0b0011, 0b1011).Should().Equal(11);
        machine.State.Should().Be(Cia408State.Hold, "1011b holds HOLD; bit 2 would be needed for ACTIVE");
    }

    [Fact]
    public void A_Fault_Reset_Needs_The_Fault_States_Own_Low_Pattern()
    {
        // FAULT_HOLD resets with 011 in bits 2..0 only (Table 7, D11); 111 is not its pattern.
        var machine = In(Cia408State.FaultHold);

        machine.ApplyControlWord(0b0011, 0b1111).Should().BeEmpty();
        machine.State.Should().Be(Cia408State.FaultHold);
    }

    [Fact]
    public void A_Fault_Reset_Needs_The_Previous_Word_At_The_Pattern_Too()
    {
        // Table 7 writes D11 as 0011 to 1011: the first word is part of the condition.
        var machine = In(Cia408State.FaultHold);

        machine.ApplyControlWord(0b0000, 0b1011).Should().BeEmpty();
        machine.State.Should().Be(Cia408State.FaultHold);

        machine.ApplyControlWord(0b0011, 0b1011).Should().Equal(11);
        machine.State.Should().Be(Cia408State.Hold);
    }

    [Fact]
    public void Fault_Disabled_Is_Acknowledged_By_0001h_Then_0009h()
    {
        // Issue #557: the two writes in order reset FAULT_DISABLED (D10, 0001 to 1001).
        var machine = In(Cia408State.FaultDisabled);

        machine.ApplyControlWord(0x0003, 0x0001).Should().BeEmpty();
        machine.ApplyControlWord(0x0001, 0x0009).Should().Equal(10);
        machine.State.Should().Be(Cia408State.Disabled);
    }

    [Fact]
    public void A_Reset_Is_Not_Taken_After_A_Fall_In_The_Same_Write()
    {
        // 0001 then 1001 in FAULT_HOLD: D15 falls to FAULT_DISABLED, whose reset D10 the same
        // write would meet; the reset is evaluated only in the state the write arrived in.
        var machine = In(Cia408State.FaultHold);

        machine.ApplyControlWord(0b0001, 0b1001).Should().Equal(15);
        machine.State.Should().Be(Cia408State.FaultDisabled);

        machine.ApplyControlWord(0b1001, 0b0001).Should().BeEmpty();
        machine.ApplyControlWord(0b0001, 0b1001).Should().Equal(10);
        machine.State.Should().Be(Cia408State.Disabled);
    }

    [Theory]
    [InlineData("FaultDisabled", 0b1001u, "Disabled", 10)]
    [InlineData("FaultHold", 0b1011u, "Hold", 11)]
    [InlineData("FaultInit", 0b1000u, "Init", 14)]
    public void Each_Fault_State_Resets_On_Its_Own_Edge_And_Goes_No_Further(string fault, uint current, string successor, int transition)
    {
        var machine = In(Parse(fault));

        // The written word is followed on after the reset, but it carries the pattern of the
        // reset's target, so no level transition of Table 7 applies to it there.
        machine.ApplyControlWord(current & 0b0111, current).Should().Equal(transition);
        machine.State.Should().Be(Parse(successor));
        machine.ApplyControlWord(current, current).Should().BeEmpty();
        machine.State.Should().Be(Parse(successor));
    }

    [Fact]
    public void Down_Within_The_Fault_States_Is_By_Level()
    {
        var machine = In(Cia408State.FaultHold);
        machine.ApplyControlWord(0b0001, 0b0001).Should().Equal(15);
        machine.State.Should().Be(Cia408State.FaultDisabled);

        machine.ApplyControlWord(0b0000, 0b0000).Should().Equal(16);
        machine.State.Should().Be(Cia408State.FaultInit);
    }

    [Fact]
    public void Fault_Hold_Does_Not_Fall_Straight_To_Fault_Init()
    {
        // D15 needs x001; there is no transition from FAULT_HOLD for x000 in Table 7.
        var machine = In(Cia408State.FaultHold);

        machine.ApplyControlWord(0b0000, 0b0000).Should().BeEmpty();
        machine.State.Should().Be(Cia408State.FaultHold);
    }

    [Fact]
    public void In_One_State_No_Two_Transitions_Of_The_Table_Can_Apply_To_The_Same_Words()
    {
        // In particular a reset edge and a down transition of the same fault state never meet: the
        // patterns differ (D10 001 vs D16 000; D11 011 vs D15 001). Across states they could
        // (FAULT_HOLD 0001 to 1001 meets D15, then D10), which is why a reset is evaluated only in
        // the state a write arrives in.
        foreach (var state in Enum.GetValues(typeof(Cia408State)).Cast<Cia408State>())
        {
            var from = Cia408StateMachine.ControlWordTransitions.Where(t => t.From == state).ToList();
            for (uint previous = 0; previous < 16; previous++)
            {
                for (uint current = 0; current < 16; current++)
                {
                    from.Count(t => t.Matches(previous, current)).Should().BeLessThanOrEqualTo(1,
                        $"{state}, previous {Convert.ToString(previous, 2)}, current {Convert.ToString(current, 2)}");
                }
            }
        }
    }

    [Fact]
    public void Bits_4_To_31_Never_Change_An_Outcome()
    {
        // Tables 7 and 8 give bits 7..4 as "0 or 1" in every row, and bits 8 and up are not part
        // of either table. Every resting state and every pair of low nibbles, so each reset pair is
        // met, each with random high bits on both words.
        var random = new Random(408);
        foreach (var state in RestingStates)
        {
            for (uint previousLow = 0; previousLow < 16; previousLow++)
            {
                for (uint currentLow = 0; currentLow < 16; currentLow++)
                {
                    var previousHigh = (uint)random.Next() << 4;
                    var currentHigh = (uint)random.Next() << 4;

                    var plain = In(state);
                    var plainTaken = plain.ApplyControlWord(previousLow, currentLow);
                    var noisy = In(state);
                    var noisyTaken = noisy.ApplyControlWord(previousLow | previousHigh, currentLow | currentHigh);

                    noisyTaken.Should().Equal(plainTaken, $"{state}, {previousLow}->{currentLow}");
                    noisy.State.Should().Be(plain.State);
                }
            }
        }
    }

    // ---- Start, Restart, Fault ------------------------------------------------------------------

    [Fact]
    public void A_New_Machine_Is_Not_Ready()
    {
        new Cia408StateMachine().State.Should().Be(Cia408State.NotReady);
    }

    [Fact]
    public void Start_Takes_D0_And_D1_Into_Init()
    {
        var machine = new Cia408StateMachine();

        machine.Start().Should().Equal(0, 1);
        machine.State.Should().Be(Cia408State.Init);
    }

    [Theory]
    [InlineData("Init")]
    [InlineData("Disabled")]
    [InlineData("Hold")]
    [InlineData("Active")]
    [InlineData("FaultInit")]
    [InlineData("FaultDisabled")]
    [InlineData("FaultHold")]
    public void Start_Does_Nothing_Once_Started(string state)
    {
        var machine = In(Parse(state));

        machine.Start().Should().BeEmpty();
        machine.State.Should().Be(Parse(state));
    }

    [Theory]
    [InlineData("NotReady")]
    [InlineData("Init")]
    [InlineData("Disabled")]
    [InlineData("Hold")]
    [InlineData("Active")]
    [InlineData("FaultInit")]
    [InlineData("FaultDisabled")]
    [InlineData("FaultHold")]
    public void Restart_Leaves_Every_State_For_Init(string state)
    {
        var machine = In(Parse(state));

        machine.Restart().Should().Equal(255, 0, 1);
        machine.State.Should().Be(Cia408State.Init);
    }

    // Table 9 (CiA 408 v2.2.0 §7.7, p28): the fault states a fault may end in, by the state it came
    // from; a request outside that set is clamped to the highest allowed state not above it
    // (FAULT_HOLD > FAULT_DISABLED > FAULT_INIT). Exits: D9 FAULT_HOLD, D12 FAULT_DISABLED,
    // D13 FAULT_INIT.
    [Theory]
    [InlineData("Init", "FaultInit", "FaultInit", 13)]
    [InlineData("Init", "FaultDisabled", "FaultInit", 13)]
    [InlineData("Init", "FaultHold", "FaultInit", 13)]
    [InlineData("Disabled", "FaultInit", "FaultInit", 13)]
    [InlineData("Disabled", "FaultDisabled", "FaultDisabled", 12)]
    [InlineData("Disabled", "FaultHold", "FaultDisabled", 12)]
    [InlineData("Hold", "FaultInit", "FaultInit", 13)]
    [InlineData("Hold", "FaultDisabled", "FaultDisabled", 12)]
    [InlineData("Hold", "FaultHold", "FaultHold", 9)]
    [InlineData("Active", "FaultInit", "FaultInit", 13)]
    [InlineData("Active", "FaultDisabled", "FaultDisabled", 12)]
    [InlineData("Active", "FaultHold", "FaultHold", 9)]
    [InlineData("FaultHold", "FaultInit", "FaultInit", 13)]
    [InlineData("FaultHold", "FaultDisabled", "FaultDisabled", 12)]
    [InlineData("FaultHold", "FaultHold", "FaultHold", 9)]
    [InlineData("FaultDisabled", "FaultInit", "FaultInit", 13)]
    [InlineData("FaultDisabled", "FaultDisabled", "FaultDisabled", 12)]
    [InlineData("FaultDisabled", "FaultHold", "FaultDisabled", 12)]
    [InlineData("FaultInit", "FaultInit", "FaultInit", 13)]
    [InlineData("FaultInit", "FaultDisabled", "FaultInit", 13)]
    [InlineData("FaultInit", "FaultHold", "FaultInit", 13)]
    public void A_Fault_Ends_Where_Table_9_Allows(string origin, string requested, string resolved, int exit)
    {
        var machine = In(Parse(origin));

        var taken = machine.Fault((Cia408FaultState)Enum.Parse(typeof(Cia408FaultState), requested));

        taken.Should().Equal(8, exit);
        machine.State.Should().Be(Parse(resolved));
    }

    [Fact]
    public void A_Fault_Before_Start_Does_Nothing()
    {
        // Figure 1 draws no D8 from NOT_READY.
        var machine = new Cia408StateMachine();

        machine.Fault(Cia408FaultState.FaultHold).Should().BeEmpty();
        machine.State.Should().Be(Cia408State.NotReady);
    }

    [Fact]
    public void A_Fault_Request_Outside_The_Enumeration_Is_Rejected()
    {
        var machine = In(Cia408State.Hold);

        var act = () => machine.Fault((Cia408FaultState)7);

        act.Should().Throw<ArgumentOutOfRangeException>();
        machine.State.Should().Be(Cia408State.Hold);
    }

    [Fact]
    public void No_Public_Call_Leaves_The_Machine_In_Fault_Reaction()
    {
        var requests = new[] { Cia408FaultState.FaultInit, Cia408FaultState.FaultDisabled, Cia408FaultState.FaultHold };
        foreach (var state in RestingStates)
        {
            var started = In(state);
            started.Start();
            started.State.Should().NotBe(Cia408State.FaultReaction);

            var restarted = In(state);
            restarted.Restart();
            restarted.State.Should().NotBe(Cia408State.FaultReaction);

            foreach (var requested in requests)
            {
                var faulted = In(state);
                faulted.Fault(requested);
                faulted.State.Should().NotBe(Cia408State.FaultReaction, $"{state}, fault {requested}");
            }

            for (uint previous = 0; previous < 16; previous++)
            {
                for (uint current = 0; current < 16; current++)
                {
                    var written = In(state);
                    written.ApplyControlWord(previous, current);
                    written.State.Should().NotBe(Cia408State.FaultReaction, $"{state}, {previous}->{current}");
                }
            }
        }
    }

    // ---- status bits ----------------------------------------------------------------------------

    // Table 4 / Figure 1 (CiA 408 v2.2.0 §7.6, p24): RMHD of the status word, bits 3..0.
    [Theory]
    [InlineData("NotReady", 0b0000)]       // no encoding in the norm; documented reading
    [InlineData("Init", 0b1000)]
    [InlineData("Disabled", 0b1001)]
    [InlineData("Hold", 0b1011)]
    [InlineData("Active", 0b1111)]
    [InlineData("FaultInit", 0b0000)]
    [InlineData("FaultDisabled", 0b0001)]
    [InlineData("FaultHold", 0b0011)]
    [InlineData("FaultReaction", 0b0000)]  // never observable in this machine
    public void Each_State_Has_Its_Status_Bits(string state, int expected)
    {
        Cia408StateMachine.StatusBits(Parse(state)).Should().Be((byte)expected);
    }

    [Fact]
    public void A_State_Outside_The_Enumeration_Has_No_Status_Bits()
    {
        var act = () => Cia408StateMachine.StatusBits((Cia408State)42);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
