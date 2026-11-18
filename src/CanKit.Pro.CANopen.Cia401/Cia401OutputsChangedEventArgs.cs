using System;
using System.Collections.Generic;

namespace CanKit.Pro.CANopen.Cia401;

/// <summary>The levels at the terminals of a function block's outputs changed
/// (<see cref="Cia401Device.OutputsChanged"/>). Names the block and its lowest change, and carries
/// the terminals of both output blocks as they are after the callback that changed them.</summary>
internal sealed class Cia401OutputsChangedEventArgs : EventArgs
{
    internal Cia401OutputsChangedEventArgs(Cia401FunctionBlock block, int group, int channel,
        IReadOnlyList<byte> levels, IReadOnlyList<short> analogLevels)
    {
        Block = block;
        Group = group;
        Channel = channel;
        Levels = levels;
        AnalogLevels = analogLevels;
    }

    /// <summary>The function block whose outputs changed: <see cref="Cia401FunctionBlock.DigitalOutput"/>
    /// or <see cref="Cia401FunctionBlock.AnalogOutput"/>.</summary>
    public Cia401FunctionBlock Block { get; }

    /// <summary>The lowest group, numbered from 1, in which a level of <see cref="Block"/> changed:
    /// the sub-index of the write object that addresses it, <c>6200h</c> for the digital outputs and
    /// <c>6411h</c> for the analog ones, where the group is the channel itself.</summary>
    public int Group { get; }

    /// <summary>The lowest channel of <see cref="Block"/>, numbered from 1, whose level changed.</summary>
    public int Channel { get; }

    /// <summary>The levels at the digital terminals of every group, group 1 first, bit 0 being the
    /// lowest channel of the group (CiA 401-1 §7.2.1.3); empty for a device without digital outputs.
    /// The set of levels the device published, which never changes afterwards: a later change
    /// publishes a new one.</summary>
    public IReadOnlyList<byte> Levels { get; }

    /// <summary>The 16-bit values at the analog terminals, channel 1 first
    /// (<see cref="Cia401Device.ReadAnalogOutput"/>); empty for a device without analog outputs. As
    /// <see cref="Levels"/>, a published set that never changes afterwards.</summary>
    public IReadOnlyList<short> AnalogLevels { get; }
}
