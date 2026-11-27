using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using CanKit.Pro.CANopen.Application;

namespace CanKit.Pro.CANopen.Cia401;

// The digital output block, 8-bit access (CiA 401-1 §7.2.3, §8.3.1 to §8.3.5). Its error state is
// in Cia401Device.Errors.cs.
internal sealed partial class Cia401Device
{
    // Null when the configuration has no digital outputs; for a device built from a description,
    // null until Attach finds 6200h. Written before the host is published.
    private DigitalOutputBlock? _digitalOutputs;

    // The terminal levels as they were when the running callback first changed them; null while no
    // change waits to be reported. Actor loop.
    private byte[]? _outputsBefore;

    /// <summary>A seam for tests: every set of terminal levels the device publishes after Attach,
    /// at the moment it becomes visible to <see cref="ReadDigitalOutputs"/>. Actor loop.</summary>
    internal event Action<IReadOnlyList<byte>>? LevelsPublishedForTests;

    /// <summary>The levels at the terminals of the eight outputs of one group, bit 0 being the lowest
    /// channel of the group (CiA 401-1 §7.2.1.3). Any thread.</summary>
    /// <param name="group">The group, <c>1</c> to the number of started groups of eight: the
    /// sub-index of <c>6200h</c> that addresses it.</param>
    /// <remarks>
    /// <para>
    /// The level is what the device drives, after the filter mask, the error state and the polarity,
    /// and so not always what <c>6200h</c> holds: <c>6200h</c> is the value last written, before all
    /// three. A channel the device does not have (13 to 16 of a device with 12) shows 0.
    /// </para>
    /// <para>
    /// How a written value reaches the terminal (CiA 401-1 §7.2.3 Figure 5, §8.3.1 to §8.3.5):
    /// </para>
    /// <list type="number">
    /// <item><description>A write to <c>6200h</c>, by RPDO, SDO or locally, is stored there, and
    /// replaces the device's held logical value in the bits <c>6208h</c> has set; the others keep
    /// theirs (§8.3.5: the received value is neglected and the old output value kept). The mask is
    /// read when the write is applied. Local dictionary writes made by an <see cref="OutputsChanged"/>
    /// handler are applied in a subsequent notification round.</description></item>
    /// <item><description>In the error state (<see cref="OutputsInErrorState"/>) the held value is
    /// the error switch's: each bit with <c>6206h</c> = 1 takes the bit of <c>6207h</c>, each bit with
    /// <c>6206h</c> = 0 keeps the value it had (§8.3.3, §8.3.4). Writes to <c>6200h</c> are stored
    /// and reach no terminal (see <see cref="OutputsInErrorState"/>).</description></item>
    /// <item><description>The terminal shows the held value XOR <c>6202h</c> (§8.3.2). Polarity is the
    /// function "nearest to the sensors and actuators" (§7.2.1.1), and Table 37 lists the error mode
    /// among the commands it applies to: <c>6207h</c> is a logical value, inverted at the terminal like
    /// any other.</description></item>
    /// </list>
    /// <para>
    /// Figure 5 draws the filter mask after the polarity, nearest the output, which would also hold
    /// the error value back from the bits the mask clears and keep a polarity change from reaching
    /// them. The clauses say otherwise — §7.2.1.1 puts the polarity nearest the actuator, and §8.3.5
    /// has the mask act on the received value — and the clauses are followed: the mask filters
    /// received values and nothing else. Where the figure and the clauses agree (<c>6200h</c> first,
    /// then the error switch, then the polarity) the figure is followed.
    /// </para>
    /// <para>
    /// The value last applied on the node's actor loop: an RPDO or a write still queued there is not
    /// visible yet. After the node is disposed it is the last level the device drove.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="group"/> is not a group of the
    /// device; a device built from a configuration also throws this when it has no
    /// digital outputs.</exception>
    /// <exception cref="InvalidOperationException">No node has attached the device yet, or a device
    /// built from a description has no digital outputs (its description declares none).</exception>
    public byte ReadDigitalOutputs(int group)
    {
        var block = RequireOutputGroup(group);
        RequireHost();
        return block.Terminal[group - 1];
    }

    /// <summary>The level at the terminal of one digital output, as <see cref="ReadDigitalOutputs"/>
    /// shows it. Any thread.</summary>
    /// <param name="channel">The output, <c>1</c> to the number of digital outputs.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="channel"/> is not an output of
    /// the device; a device built from a configuration also throws this when it has no
    /// digital outputs.</exception>
    /// <exception cref="InvalidOperationException">No node has attached the device yet, or a device
    /// built from a description has no digital outputs (its description declares none).</exception>
    public bool ReadDigitalOutput(int channel)
    {
        var block = ApplicationBlock(ref _digitalOutputs, "digital outputs");
        if (block is null || channel < 1 || channel > block.Channels)
        {
            throw new ArgumentOutOfRangeException(nameof(channel), channel,
                $"The device has {block?.Channels ?? 0} digital outputs, numbered from 1.");
        }

        RequireHost();
        int g = (channel - 1) / Cia401Configuration.ChannelsPerDigitalGroup;
        var bit = 1 << ((channel - 1) % Cia401Configuration.ChannelsPerDigitalGroup);
        return (block.Terminal[g] & bit) != 0;
    }

    private DigitalOutputBlock RequireOutputGroup(int group)
    {
        var block = ApplicationBlock(ref _digitalOutputs, "digital outputs");
        if (block is null || group < 1 || group > block.Groups)
        {
            throw new ArgumentOutOfRangeException(nameof(group), group,
                $"The device has {block?.Groups ?? 0} groups of digital outputs, numbered from 1.");
        }

        return block;
    }

    /// <summary>
    /// A written entry of the output block, into the held values only: the caller refreshes the
    /// levels once for all the entries of its callback (<see cref="RefreshDigitalOutputs"/>), so that
    /// no level computed from half of them is published. <c>6200h</c> takes the received value into
    /// the held one through the filter mask, outside the error state only (see
    /// <see cref="OutputsInErrorState"/>). <c>6206h</c> and <c>6207h</c> written in the error state
    /// apply at once, the error switch of Figure 5 (§7.2.3) being live while the failure lasts;
    /// <c>6202h</c> is read by the refresh, in every state; <c>6208h</c> is read when a write is
    /// applied and needs nothing. Coalesced (<see cref="ICanOpenApplication.OnEntriesWritten"/>):
    /// what <c>6200h</c> holds now is applied, not each value it held in between. Actor loop.
    /// </summary>
    private void DigitalOutputsWritten(ICanOpenApplicationHost host, CanOpenObjectAddress entry)
    {
        var block = _digitalOutputs;
        if (block is null || entry.Subindex < 1 || entry.Subindex > block.Groups) return;
        int g = entry.Subindex - 1;
        var od = host.ObjectDictionary;

        if (entry.Index == block.Write.Index)
        {
            if (_inErrorState) return;
            block.TakeReceived(od, g);
        }
        else if (entry.Index == block.ErrorMode.Index || entry.Index == block.ErrorValue.Index)
        {
            if (!_inErrorState) return;
            block.ApplyErrorValue(od, g);
        }
    }

    /// <summary>Entering the error state: every bit with <c>6206h</c> = 1 takes <c>6207h</c>
    /// (§8.3.3, §8.3.4). Actor loop.</summary>
    private void DigitalOutputsEnterErrorState(ICanOpenApplicationHost host)
    {
        var block = _digitalOutputs;
        if (block is null) return;
        for (int g = 0; g < block.Groups; g++) block.ApplyErrorValue(host.ObjectDictionary, g);
        RefreshDigitalOutputs(host);
    }

    /// <summary>Reset Node drops the held values, which are volatile: they are taken from
    /// <c>6200h</c> as the node restored it, its default or a stored value ("after power-on and
    /// application reset", CiA 401-1 §6.2.5 NOTE), and a device failure that outlasts the reset puts
    /// them in the error state again. Not for Reset Communication, which resets no application and
    /// restores no <c>6200h</c>: reloading there would move an output the master never wrote when the
    /// reset ends a communication failure. Actor loop.</summary>
    private void DigitalOutputsReset(ICanOpenApplicationHost host)
    {
        var block = _digitalOutputs;
        if (block is null) return;
        var od = host.ObjectDictionary;
        for (int g = 0; g < block.Groups; g++)
        {
            block.LoadHeld(od, g);
            if (_inErrorState) block.ApplyErrorValue(od, g);
        }

        RefreshDigitalOutputs(host);
    }

    /// <summary>Brings the terminal levels to the held values XOR <c>6202h</c>, and when that
    /// changed one, remembers the levels before it for <see cref="RaiseOutputsChanged"/>. The one
    /// place a terminal level changes after Attach; called once the held values of a step are
    /// complete. Actor loop.</summary>
    private void RefreshDigitalOutputs(ICanOpenApplicationHost host)
    {
        var block = _digitalOutputs;
        if (block is null) return;
        var current = block.Terminal;
        var levels = block.ComputeTerminal(host.ObjectDictionary);
        if (SameLevels(levels, current)) return;
        _outputsBefore ??= current;
        block.Publish(levels);
        LevelsPublishedForTests?.Invoke(new ReadOnlyCollection<byte>(levels));
    }

    private static bool SameLevels(byte[] a, byte[] b)
    {
        for (int g = 0; g < a.Length; g++)
        {
            if (a[g] != b[g]) return false;
        }

        return true;
    }

    /// <summary>The lowest group and channel (1-based) whose level differs between
    /// <paramref name="before"/> and <paramref name="after"/>; null when none does.</summary>
    private static (int Group, int Channel)? FirstDigitalChange(byte[] before, byte[] after)
    {
        for (int g = 0; g < after.Length; g++)
        {
            int changed = before[g] ^ after[g];
            if (changed == 0) continue;
            int bit = 0;
            while ((changed & (1 << bit)) == 0) bit++;
            return (g + 1, (g * Cia401Configuration.ChannelsPerDigitalGroup) + bit + 1);
        }

        return null;
    }

    /// <summary>The digital outputs of one device: their indices in its logical device, the held
    /// logical values, and the levels at the terminals.</summary>
    private sealed class DigitalOutputBlock
    {
        // Published whole and never changed afterwards, so a reader on any thread sees one
        // consistent set of levels.
        private byte[] _terminal;

        internal DigitalOutputBlock(int channels, int logicalDevice)
        {
            Channels = channels;
            Groups = (channels + Cia401Configuration.ChannelsPerDigitalGroup - 1) / Cia401Configuration.ChannelsPerDigitalGroup;
            Held = new byte[Groups];
            _terminal = new byte[Groups];
            Write = new ProfileObject(logicalDevice, Cia401Objects.WriteDigitalOutput8);
            Polarity = new ProfileObject(logicalDevice, Cia401Objects.ChangePolarityDigitalOutput8);
            ErrorMode = new ProfileObject(logicalDevice, Cia401Objects.ErrorModeDigitalOutput8);
            ErrorValue = new ProfileObject(logicalDevice, Cia401Objects.ErrorValueDigitalOutput8);
            FilterMask = new ProfileObject(logicalDevice, Cia401Objects.FilterMaskDigitalOutput8);
        }

        internal int Channels { get; }

        /// <summary>Started groups of eight: the highest sub-index of the 8-bit objects (§7.2.1.3).</summary>
        internal int Groups { get; }

        /// <summary>The logical value per group that drives the terminal before the polarity: what
        /// the filter mask let through, or what the error switch set. Actor loop only.</summary>
        internal byte[] Held { get; }

        /// <summary>The levels at the terminals, per group. Any thread; the array is not to be
        /// changed.</summary>
        internal byte[] Terminal => Volatile.Read(ref _terminal);

        internal ProfileObject Write { get; }

        internal ProfileObject Polarity { get; }

        internal ProfileObject ErrorMode { get; }

        internal ProfileObject ErrorValue { get; }

        internal ProfileObject FilterMask { get; }

        /// <summary>Declares <c>6200h</c>, <c>6202h</c>, <c>6206h</c>, <c>6207h</c> and <c>6208h</c>
        /// as the catalogue gives them, for a device built from a configuration. Constructing thread,
        /// in Attach.</summary>
        internal void Declare(ObjectDictionary od)
        {
            DeclareArray(od, Write, Groups);
            DeclareArray(od, Polarity, Groups);
            DeclareArray(od, ErrorMode, Groups);
            DeclareArray(od, ErrorValue, Groups);
            DeclareArray(od, FilterMask, Groups);
        }

        /// <summary>The power-on levels, whether the objects were declared or adopted: the held values
        /// from <c>6200h</c> and the levels from those, published without an event. Constructing
        /// thread, in Attach.</summary>
        internal void Start(ObjectDictionary od)
        {
            for (int g = 0; g < Groups; g++) LoadHeld(od, g);
            Publish(ComputeTerminal(od));
        }

        /// <summary>Held = <c>6200h</c>: after power-on and a reset the outputs are what the object
        /// holds, its default or a stored value (§6.2.5 NOTE).</summary>
        internal void LoadHeld(ObjectDictionary od, int g) => Held[g] = Written(od, g);

        /// <summary>The received value of <c>6200h</c> in the bits <c>6208h</c> has set (§8.3.5).</summary>
        internal void TakeReceived(ObjectDictionary od, int g)
        {
            int filter = FilterMask.ReadByte(od, Sub(g));
            Held[g] = (byte)((Held[g] & ~filter) | (Written(od, g) & filter));
        }

        /// <summary>The bits with <c>6206h</c> = 1 take <c>6207h</c>; the others keep their value
        /// (§8.3.3, §8.3.4).</summary>
        internal void ApplyErrorValue(ObjectDictionary od, int g)
        {
            int mode = ErrorMode.ReadByte(od, Sub(g));
            Held[g] = (byte)((Held[g] & ~mode) | (ErrorValue.ReadByte(od, Sub(g)) & mode));
        }

        /// <summary>Held XOR <c>6202h</c> (§8.3.2), the channels the device lacks at 0.</summary>
        internal byte[] ComputeTerminal(ObjectDictionary od)
        {
            var levels = new byte[Groups];
            for (int g = 0; g < Groups; g++)
            {
                levels[g] = (byte)((Held[g] ^ Polarity.ReadByte(od, Sub(g))) & GroupChannelMask(Channels, g));
            }

            return levels;
        }

        internal void Publish(byte[] levels) => Volatile.Write(ref _terminal, levels);

        /// <summary>What <c>6200h</c> holds for group <paramref name="g"/> (0-based).</summary>
        private byte Written(ObjectDictionary od, int g) => od.ReadRaw(Write.Index, Sub(g))[0];

        private static byte Sub(int g) => (byte)(g + 1);
    }
}
