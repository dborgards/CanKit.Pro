using System;
using System.Collections.Generic;

namespace CanKit.Pro.CANopen.Cia408;

/// <summary>What <see cref="Cia408Device.StateChanged"/> reports: the state the device machine left,
/// the state it rests in now, and the transitions of CiA 408 v2.2.0 §7.6, Figure 1, it took to get
/// there, by their D-numbers.</summary>
internal sealed class Cia408StateChangedEventArgs : EventArgs
{
    /// <summary>A change from <paramref name="previous"/> to <paramref name="current"/> by
    /// <paramref name="transitions"/>, kept as given.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="transitions"/> is null.</exception>
    public Cia408StateChangedEventArgs(Cia408State previous, Cia408State current, IReadOnlyList<int> transitions)
    {
        Previous = previous;
        Current = current;
        Transitions = transitions ?? throw new ArgumentNullException(nameof(transitions));
    }

    /// <summary>The state before the change.</summary>
    public Cia408State Previous { get; }

    /// <summary>The state after it: never <see cref="Cia408State.FaultReaction"/>, which the machine
    /// passes through within one call (<see cref="Cia408StateMachine.Fault"/>).</summary>
    public Cia408State Current { get; }

    /// <summary>The D-numbers taken, in order, as the state machine returned them: <c>[0, 1]</c> for
    /// the start, <c>[2, 3, 4]</c> for one control word that walks INIT to ACTIVE, <c>[8, 9]</c> for
    /// a fault that ends in FAULT_HOLD.</summary>
    public IReadOnlyList<int> Transitions { get; }
}
