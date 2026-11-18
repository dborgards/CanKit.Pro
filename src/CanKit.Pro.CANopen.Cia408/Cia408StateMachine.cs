using System;
using System.Collections.Generic;
using System.Globalization;

namespace CanKit.Pro.CANopen.Cia408;

/// <summary>
/// The CiA 408 device state machine (CiA 408 v2.2.0 §7.6, Figure 1, Tables 4 and 6; §7.7,
/// Tables 7 and 9) as a pure class: state in, event in, state and the transitions taken out. It
/// knows no node and no dictionary; the device feeds it control words and faults and acts on the
/// D-numbers it returns, which are the only record of what happened — there are no events.
/// </summary>
/// <remarks>
/// <para>The norm gives per-transition conditions and is silent or contradictory on how they
/// combine. The readings taken here:</para>
/// <list type="bullet">
/// <item><description><b>A control word is followed to the stable state.</b> Table 7 states, per
/// transition, the bits 3..0 (R M H D) a control word must have; it does not say whether one write
/// takes one step. Here the conditions are evaluated again after every step until none applies:
/// <c>0111b</c> in INIT takes D2, D3 and D4 and ends in ACTIVE, <c>0000b</c> in ACTIVE takes D5, D6
/// and D7 and ends in INIT. The conditions overlap — D5 (<c>x0xx</c>) and D6 (<c>x00x</c>) and D7
/// (<c>x000</c>) — but never within one state, so the order of evaluation does not matter.</description></item>
/// <item><description><b>A fault reset is a change between two words, as Table 7 writes it.</b>
/// Table 7 gives D10, D11 and D14 as a change from one word to another and marks them only "toggle
/// bit 3" (NOTE 2); its rows fix both words: the previous one with R = 0 and bits 2..0 at the fault
/// state's own pattern, the written one with R = 1 and the same pattern — <c>0001b</c> to
/// <c>1001b</c> for FAULT_DISABLED (D10), <c>0011b</c> to <c>1011b</c> for FAULT_HOLD (D11),
/// <c>0000b</c> to <c>1000b</c> for FAULT_INIT (D14). The note alone would ask for no more than a
/// rising bit 3; the rows are read literally, so both words must match (FAULT_HOLD with
/// <c>0000b</c> then <c>1011b</c> does not reset), and bit 3 held at 1 does nothing in a fault
/// state. Table 8 gives D14's written word as <c>x000</c>, contradicting the toggle; Table 7's
/// <c>1000</c> is taken.</description></item>
/// <item><description><b>A reset is evaluated once, in the state the write arrives in, before any
/// level transition.</b> Tables 7 and 8 give each transition's condition and no order between
/// them; the norm is silent on whether one write may take a fall within the fault states and then
/// a reset. Here it may not: <c>0001b</c> then <c>1001b</c> in FAULT_HOLD takes D15 only and stays
/// in FAULT_DISABLED, where a following <c>0001b</c>, <c>1001b</c> pair resets. After a reset taken
/// on arrival the written word is followed on as above; since it carries the pattern of the state
/// the reset leads to, it never moves further (<c>1011b</c> after D11 stays in HOLD; ACTIVE would
/// need bit 2).</description></item>
/// <item><description><b>Down within the fault states by level</b>, bit 3 ignored: D15
/// (FAULT_HOLD to FAULT_DISABLED, <c>x001b</c>) and D16 (FAULT_DISABLED to FAULT_INIT,
/// <c>x000b</c>), from Tables 7 and 8. Table 6 gives both the same event, a command leading to INIT
/// after FAULT_INIT, which fits neither; the tables and Figure 1 are taken. FAULT_HOLD with <c>x000b</c> takes
/// no transition: D15 needs <c>x001b</c> and Table 7 has nothing else from FAULT_HOLD.</description></item>
/// <item><description><b>Leaving FAULT_REACTION is the device's decision.</b> Table 4 makes the
/// resulting fault state manufacturer-specific and Table 9 bounds it by the state the fault came
/// from; Table 7 also lists control-word patterns for D12 and D13 and Table 8 alone one for D9.
/// <see cref="Fault"/> takes D8 and the exit in one call, so FAULT_REACTION is never a resting
/// state of this machine and no control word ever meets it.</description></item>
/// <item><description>Table 8 (the hardware enable signal) is not built.</description></item>
/// </list>
/// <para>Not thread-safe: the device confines an instance to the node's loop.</para>
/// </remarks>
internal sealed class Cia408StateMachine
{
    // Bits 3..0 of the control word (CiA 408 v2.2.0 §9.3.2, Table 73): R reset fault, M device
    // mode active enable, H hold enable, D disabled. Bits 4 and up never influence the machine.
    private const uint MachineBits = 0b1111;
    private const uint ResetFaultBit = 0b1000;

    /// <summary>The transitions caused by a control word (CiA 408 v2.2.0 §7.7, Table 7), one row
    /// each: from, to, and the bits 3..0 of the current word under a mask. A reset row also needs
    /// the previous word at its pattern with bit 3 at 0 (<see cref="Transition.Matches"/>) and is
    /// evaluated only in the state a write arrives in.</summary>
    internal static IReadOnlyList<Transition> ControlWordTransitions { get; } = new Transition[]
    {
        //  D    from                         to                           mask    value   reset edge
        new(2,  Cia408State.Init,            Cia408State.Disabled,        0b0001, 0b0001, false), // xxx1
        new(3,  Cia408State.Disabled,        Cia408State.Hold,            0b0011, 0b0011, false), // xx11
        new(4,  Cia408State.Hold,            Cia408State.Active,          0b0111, 0b0111, false), // x111
        new(5,  Cia408State.Active,          Cia408State.Hold,            0b0100, 0b0000, false), // x0xx
        new(6,  Cia408State.Hold,            Cia408State.Disabled,        0b0110, 0b0000, false), // x00x
        new(7,  Cia408State.Disabled,        Cia408State.Init,            0b0111, 0b0000, false), // x000
        new(10, Cia408State.FaultDisabled,   Cia408State.Disabled,        0b1111, 0b1001, true),  // 0001 to 1001
        new(11, Cia408State.FaultHold,       Cia408State.Hold,            0b1111, 0b1011, true),  // 0011 to 1011
        new(14, Cia408State.FaultInit,       Cia408State.Init,            0b1111, 0b1000, true),  // 0000 to 1000
        new(15, Cia408State.FaultHold,       Cia408State.FaultDisabled,   0b0111, 0b0001, false), // x001
        new(16, Cia408State.FaultDisabled,   Cia408State.FaultInit,       0b0111, 0b0000, false), // x000
    };

    /// <summary>The fault states a fault reaction may end in, by the state the fault came from
    /// (CiA 408 v2.2.0 §7.7, Table 9), each listed from the highest to the lowest.</summary>
    private static Cia408FaultState[] AllowedFaultStates(Cia408State origin) => origin switch
    {
        Cia408State.Init => [Cia408FaultState.FaultInit],
        Cia408State.Disabled => [Cia408FaultState.FaultDisabled, Cia408FaultState.FaultInit],
        Cia408State.Hold => [Cia408FaultState.FaultHold, Cia408FaultState.FaultDisabled, Cia408FaultState.FaultInit],
        Cia408State.Active => [Cia408FaultState.FaultHold, Cia408FaultState.FaultDisabled, Cia408FaultState.FaultInit],
        Cia408State.FaultHold => [Cia408FaultState.FaultHold, Cia408FaultState.FaultDisabled, Cia408FaultState.FaultInit],
        Cia408State.FaultDisabled => [Cia408FaultState.FaultDisabled, Cia408FaultState.FaultInit],
        Cia408State.FaultInit => [Cia408FaultState.FaultInit],
        _ => throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
            "Table 9 has no row for {0}.", origin)),
    };

    /// <summary>Makes a machine in NOT_READY: D0, the entry after power on, is the construction
    /// itself. <see cref="Start"/> reports it together with D1, because Table 6 gives the two one
    /// row and one event.</summary>
    public Cia408StateMachine() => State = Cia408State.NotReady;

    /// <summary>The state the machine rests in. Never <see cref="Cia408State.FaultReaction"/>.</summary>
    public Cia408State State { get; private set; }

    /// <summary>Completes the start after power on: D0 and D1 into INIT (CiA 408 v2.2.0 §7.6,
    /// Figure 1, Table 6). Only from NOT_READY; in any other state nothing happens.</summary>
    /// <returns><c>[0, 1]</c>, or empty when the machine was already started.</returns>
    public IReadOnlyList<int> Start()
    {
        if (State != Cia408State.NotReady) return Array.Empty<int>();
        State = Cia408State.Init;
        return [0, 1];
    }

    /// <summary>Applies a write of the control word (<c>6040h</c> or <c>6044h</c>): a fault reset
    /// of Table 7 (CiA 408 v2.2.0 §7.7) if <paramref name="previous"/> and
    /// <paramref name="current"/> make one in the state the write arrives in, then the level
    /// transitions of Table 7 that <paramref name="current"/> meets, followed until none applies
    /// (see the remarks on this class). Only bits 3..0 count. In NOT_READY nothing happens. The
    /// machine keeps no copy of the control word: the caller passes the one it replaces.</summary>
    /// <param name="previous">The control word before this write.</param>
    /// <param name="current">The control word written.</param>
    /// <returns>The D-numbers taken, in order; empty when the write moves nothing.</returns>
    public IReadOnlyList<int> ApplyControlWord(uint previous, uint current)
    {
        List<int>? taken = null;
        if (Next(previous, current, reset: true) is { } reset)
        {
            taken = [reset.Number];
            State = reset.To;
        }

        while (Next(previous, current, reset: false) is { } transition)
        {
            taken ??= new List<int>();
            if (taken.Count == ControlWordTransitions.Count)
            {
                // A cycle in the table: impossible for Table 7, where every chain ends.
                throw new InvalidOperationException("The control-word transitions do not settle.");
            }

            taken.Add(transition.Number);
            State = transition.To;
        }

        return taken is null ? Array.Empty<int>() : taken;
    }

    private Transition? Next(uint previous, uint current, bool reset)
    {
        foreach (var transition in ControlWordTransitions)
        {
            if (transition.ResetEdge == reset && transition.From == State && transition.Matches(previous, current)) return transition;
        }

        return null;
    }

    /// <summary>A fault detected by the device: D8 into FAULT_REACTION and, within the same call,
    /// the exit into the fault state <paramref name="requested"/> — D9 FAULT_HOLD, D12
    /// FAULT_DISABLED, D13 FAULT_INIT (CiA 408 v2.2.0 §7.6, Figure 1, Table 4; §7.7, Table 9).
    /// When Table 9 does not allow <paramref name="requested"/> from the present state, the highest
    /// allowed fault state below it is taken: a fault is never answered by a less severe state
    /// than asked for, and a fault in a fault state never moves up. Figure 1 draws D8 from every
    /// state but NOT_READY; there nothing happens.</summary>
    /// <returns><c>[8, exit]</c>, or empty in NOT_READY.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="requested"/> is not a member
    /// of <see cref="Cia408FaultState"/>.</exception>
    public IReadOnlyList<int> Fault(Cia408FaultState requested)
    {
        if (requested is not (Cia408FaultState.FaultInit or Cia408FaultState.FaultDisabled or Cia408FaultState.FaultHold))
        {
            throw new ArgumentOutOfRangeException(nameof(requested), requested, "Not a fault state of Table 9.");
        }

        if (State == Cia408State.NotReady) return Array.Empty<int>();

        var resolved = Cia408FaultState.FaultInit;
        foreach (var allowed in AllowedFaultStates(State))
        {
            if (allowed <= requested)
            {
                resolved = allowed;
                break;
            }
        }

        // D8 passes through FAULT_REACTION, which nothing can observe between the two steps.
        State = Cia408State.FaultReaction;
        switch (resolved)
        {
            case Cia408FaultState.FaultHold:
                State = Cia408State.FaultHold;
                return [8, 9];
            case Cia408FaultState.FaultDisabled:
                State = Cia408State.FaultDisabled;
                return [8, 12];
            default:
                State = Cia408State.FaultInit;
                return [8, 13];
        }
    }

    /// <summary>A reset of the application (NMT reset application, or a local restart): D255 out
    /// of whatever state the machine is in, then D0 and D1 into INIT (CiA 408 v2.2.0 §7.6,
    /// Figure 1, Table 6).</summary>
    /// <returns><c>[255, 0, 1]</c>.</returns>
    public IReadOnlyList<int> Restart()
    {
        State = Cia408State.Init;
        return [255, 0, 1];
    }

    /// <summary>The bits 3..0 (R M H D) of the status word in <paramref name="state"/>
    /// (CiA 408 v2.2.0 §7.6, Figure 1, Table 4). The norm gives NOT_READY no encoding; it reads
    /// <c>0000b</c> here. FAULT_REACTION is given as <c>0xxx</c> in Figure 1, while Table 4 asks bits
    /// 0 to 3 to keep the previous state's values; it reads <c>0000b</c> here and is never
    /// observable, since the machine does not rest in it.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="state"/> is not a member of
    /// <see cref="Cia408State"/>.</exception>
    public static byte StatusBits(Cia408State state) => state switch
    {
        Cia408State.NotReady => 0b0000,
        Cia408State.Init => 0b1000,
        Cia408State.Disabled => 0b1001,
        Cia408State.Hold => 0b1011,
        Cia408State.Active => 0b1111,
        Cia408State.FaultInit => 0b0000,
        Cia408State.FaultDisabled => 0b0001,
        Cia408State.FaultHold => 0b0011,
        Cia408State.FaultReaction => 0b0000,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Not a state of Figure 1."),
    };

    /// <summary>One row of Table 7 (CiA 408 v2.2.0 §7.7).</summary>
    internal readonly struct Transition
    {
        public Transition(int number, Cia408State from, Cia408State to, uint mask, uint value, bool resetEdge)
        {
            Number = number;
            From = from;
            To = to;
            Mask = mask;
            Value = value;
            ResetEdge = resetEdge;
        }

        /// <summary>The D-number of Figure 1.</summary>
        public int Number { get; }

        public Cia408State From { get; }

        public Cia408State To { get; }

        /// <summary>The bits 3..0 of the current control word that the condition fixes.</summary>
        public uint Mask { get; }

        /// <summary>Their required values.</summary>
        public uint Value { get; }

        /// <summary>A fault reset: bit 3 must rise from <c>previous</c> to <c>current</c>, both
        /// words carrying the fault state's pattern in bits 2..0.</summary>
        public bool ResetEdge { get; }

        /// <summary>Whether a write from <paramref name="previous"/> to <paramref name="current"/>
        /// meets this row. A reset row needs the previous word's bits 3..0 as Table 7's first word
        /// gives them: bit 3 at 0, bits 2..0 at the pattern of <see cref="Value"/>.</summary>
        public bool Matches(uint previous, uint current)
        {
            if ((current & MachineBits & Mask) != Value) return false;
            return !ResetEdge || (previous & MachineBits) == (Value & ~ResetFaultBit);
        }
    }
}
