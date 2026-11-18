using System;
using System.Collections.Generic;

namespace CanKit.Pro.CANopen.Cia401;

/// <summary>The levels at the terminals of a function block's outputs changed
/// (<see cref="Cia401Device.OutputsChanged"/>).</summary>
internal sealed class Cia401OutputsChangedEventArgs : EventArgs
{
    internal Cia401OutputsChangedEventArgs(Cia401FunctionBlock block, int group, int channel, IReadOnlyList<byte> levels)
    {
        Block = block;
        Group = group;
        Channel = channel;
        Levels = levels;
    }

    /// <summary>The function block whose outputs changed: <see cref="Cia401FunctionBlock.DigitalOutput"/>.</summary>
    public Cia401FunctionBlock Block { get; }

    /// <summary>The lowest group, numbered from 1, in which a level changed: the sub-index of
    /// <c>6200h</c> that addresses it.</summary>
    public int Group { get; }

    /// <summary>The lowest channel, numbered from 1, whose level changed.</summary>
    public int Channel { get; }

    /// <summary>The levels at the terminals of every group after the change, group 1 first, bit 0
    /// being the lowest channel of the group (CiA 401-1 §7.2.1.3). A copy: it does not change
    /// afterwards.</summary>
    public IReadOnlyList<byte> Levels { get; }
}
