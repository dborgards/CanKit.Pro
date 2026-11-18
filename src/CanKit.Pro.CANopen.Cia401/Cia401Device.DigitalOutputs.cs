using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.ExceptionServices;
using System.Threading;
using CanKit.Pro.CANopen.Application;

namespace CanKit.Pro.CANopen.Cia401;

// The digital output block, 8-bit access (CiA 401-1 §7.2.3, §8.3.1 to §8.3.5). Its error state is
// in Cia401Device.Errors.cs.
internal sealed partial class Cia401Device
{
    // Null when the configuration has no digital outputs.
    private readonly DigitalOutputBlock? _digitalOutputs;

    // The terminal levels as they were when the running callback first changed them; null while no
    // change waits to be reported. Actor loop.
    private byte[]? _outputsBefore;

    // True while OutputsChanged is being raised: a change a handler makes then is left pending, and
    // the raise that is running reports it once every subscriber has had the event before it.
    // Actor loop.
    private bool _raisingOutputsChanged;

    /// <summary>The level at the terminal of a digital output changed. Raised on the node's actor
    /// loop, at the end of the callback that changed it, once the device's state is consistent, and
    /// only when a level actually changed: a write of the value already there raises nothing.</summary>
    /// <remarks>
    /// <para>
    /// One event per callback, however many entries and groups changed in it:
    /// <see cref="Cia401OutputsChangedEventArgs.Group"/> and <see cref="Cia401OutputsChangedEventArgs.Channel"/>
    /// name the lowest that changed, and <see cref="Cia401OutputsChangedEventArgs.Levels"/> holds every
    /// group. The device works out a callback's levels before it publishes any: the levels after the
    /// callback are compared with those before it, a callback that leaves every level where it was
    /// publishes and raises nothing, and no level a callback would pass through on the way is ever
    /// visible to <see cref="ReadDigitalOutputs"/>.
    /// </para>
    /// <para>
    /// The handler runs inside a callback of the device, so the device's methods called from it apply
    /// at once (<see cref="ICanOpenApplicationHost.Post"/>): a handler that loops an output back to an
    /// input with <see cref="SetDigitalInputs"/> sees it in <see cref="ReadDigitalInputs"/> before it
    /// returns, and one that calls <see cref="ReportDeviceFailure"/> changes the outputs there and
    /// then. Such a change is not raised inside the running event: every subscriber is given the
    /// event in hand first, and then the change, as an event of its own, so each subscriber sees the
    /// changes in the order they happened and ends on the latest levels. It must not block: it holds
    /// up the node.
    /// </para>
    /// <para>
    /// Each subscriber is called on its own, so one that throws keeps no other from the event. What
    /// they throw is reported on <see cref="ICanOpenNode.BackgroundExceptionOccurred"/> once every
    /// subscriber has been called, as anything thrown out of an application's callback is, and the
    /// device carries on (several exceptions as one <see cref="AggregateException"/>); when the event
    /// is raised inside a <see cref="ReportDeviceFailure"/> that was itself called from a callback of
    /// the device, the exception reaches that caller instead. Either way the device's state is
    /// complete before the first subscriber runs.
    /// </para>
    /// </remarks>
    public event EventHandler<Cia401OutputsChangedEventArgs>? OutputsChanged;

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
    /// read when the write is applied. A local write the application makes from inside a callback of
    /// the device (an <see cref="OutputsChanged"/> handler, say) is the device's own write under the
    /// node's rule, is not reported back to it (<see cref="ICanOpenApplication.OnEntriesWritten"/>)
    /// and so is stored and never applied, to <c>6200h</c> and to <c>6202h</c>, <c>6206h</c> and
    /// <c>6207h</c> alike; the device has no method that sets an output instead, the outputs being
    /// the master's to write. Make such a write from outside the device's callbacks.</description></item>
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
    /// device.</exception>
    /// <exception cref="InvalidOperationException">No node has attached the device yet.</exception>
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
    /// the device.</exception>
    /// <exception cref="InvalidOperationException">No node has attached the device yet.</exception>
    public bool ReadDigitalOutput(int channel)
    {
        var block = _digitalOutputs;
        if (block is null || channel < 1 || channel > block.Channels)
        {
            throw new ArgumentOutOfRangeException(nameof(channel), channel,
                $"The device has {block?.Channels ?? 0} digital outputs, numbered from 1.");
        }

        RequireHost();
        int g = (channel - 1) / ChannelsPerGroup;
        var bit = 1 << ((channel - 1) % ChannelsPerGroup);
        return (block.Terminal[g] & bit) != 0;
    }

    private DigitalOutputBlock RequireOutputGroup(int group)
    {
        var block = _digitalOutputs;
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

        if (entry.Index == block.Write)
        {
            if (_inErrorState) return;
            block.TakeReceived(od, g);
        }
        else if (entry.Index == block.ErrorMode || entry.Index == block.ErrorValue)
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

    /// <summary>
    /// Raises <see cref="OutputsChanged"/> for what changed since the running callback first changed
    /// a level, if anything did. Called last in each callback, so the device's state is complete when
    /// the subscribers run. Called while the event is being raised already (a subscriber changed the
    /// outputs), it leaves the change pending, and the running raise reports it after the event in
    /// hand has reached every subscriber, and so on until nothing is pending. Each subscriber is
    /// called on its own; what they throw is thrown from here once nothing is pending. Actor loop.
    /// </summary>
    private void RaiseOutputsChanged()
    {
        var block = _digitalOutputs;
        if (block is null || _raisingOutputsChanged) return;

        List<Exception>? failures = null;
        _raisingOutputsChanged = true;
        try
        {
            while (_outputsBefore is { } before)
            {
                _outputsBefore = null;
                var after = block.Terminal;
                var args = ChangeBetween(before, after);
                if (args is null || OutputsChanged is not { } handler) continue;
                foreach (var subscriber in handler.GetInvocationList())
                {
                    try
                    {
                        ((EventHandler<Cia401OutputsChangedEventArgs>)subscriber)(this, args);
                    }
                    // Everything, deliberately: one subscriber's failure must not keep the event from
                    // the others; it is thrown once they all have it.
                    catch (Exception ex)
                    {
                        (failures ??= new List<Exception>()).Add(ex);
                    }
                }
            }
        }
        finally
        {
            _raisingOutputsChanged = false;
        }

        if (failures is null) return;
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        throw new AggregateException(failures);
    }

    /// <summary>The event for the change from <paramref name="before"/> to
    /// <paramref name="after"/>, naming the lowest group and channel that changed; null when none
    /// did.</summary>
    private static Cia401OutputsChangedEventArgs? ChangeBetween(byte[] before, byte[] after)
    {
        for (int g = 0; g < after.Length; g++)
        {
            int changed = before[g] ^ after[g];
            if (changed == 0) continue;
            int bit = 0;
            while ((changed & (1 << bit)) == 0) bit++;
            return new Cia401OutputsChangedEventArgs(Cia401FunctionBlock.DigitalOutput,
                g + 1, (g * ChannelsPerGroup) + bit + 1, new ReadOnlyCollection<byte>(after));
        }

        return null;
    }

    /// <summary>The digital outputs of one device: their indices in its logical device, the held
    /// logical values, and the levels at the terminals.</summary>
    private sealed class DigitalOutputBlock
    {
        private readonly int _logicalDevice;

        // Published whole and never changed afterwards, so a reader on any thread sees one
        // consistent set of levels.
        private byte[] _terminal;

        internal DigitalOutputBlock(int channels, int logicalDevice)
        {
            Channels = channels;
            Groups = (channels + ChannelsPerGroup - 1) / ChannelsPerGroup;
            Held = new byte[Groups];
            _terminal = new byte[Groups];
            Write = Shift(logicalDevice, Cia401Objects.WriteDigitalOutput8);
            Polarity = Shift(logicalDevice, Cia401Objects.ChangePolarityDigitalOutput8);
            ErrorMode = Shift(logicalDevice, Cia401Objects.ErrorModeDigitalOutput8);
            ErrorValue = Shift(logicalDevice, Cia401Objects.ErrorValueDigitalOutput8);
            FilterMask = Shift(logicalDevice, Cia401Objects.FilterMaskDigitalOutput8);
            _logicalDevice = logicalDevice;
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

        internal ushort Write { get; }

        internal ushort Polarity { get; }

        internal ushort ErrorMode { get; }

        internal ushort ErrorValue { get; }

        internal ushort FilterMask { get; }

        /// <summary>Declares <c>6200h</c>, <c>6202h</c>, <c>6206h</c>, <c>6207h</c> and <c>6208h</c>
        /// as the catalogue gives them, then takes the held values from <c>6200h</c> and the levels
        /// from those. Constructing thread, in Attach.</summary>
        internal void Declare(ObjectDictionary od)
        {
            DeclareGroupArray(od, _logicalDevice, Groups, Cia401Objects.WriteDigitalOutput8);
            DeclareGroupArray(od, _logicalDevice, Groups, Cia401Objects.ChangePolarityDigitalOutput8);
            DeclareGroupArray(od, _logicalDevice, Groups, Cia401Objects.ErrorModeDigitalOutput8);
            DeclareGroupArray(od, _logicalDevice, Groups, Cia401Objects.ErrorValueDigitalOutput8);
            DeclareGroupArray(od, _logicalDevice, Groups, Cia401Objects.FilterMaskDigitalOutput8);
            for (int g = 0; g < Groups; g++) LoadHeld(od, g);
            Publish(ComputeTerminal(od));
        }

        /// <summary>Held = <c>6200h</c>: after power-on and a reset the outputs are what the object
        /// holds, its default or a stored value (§6.2.5 NOTE).</summary>
        internal void LoadHeld(ObjectDictionary od, int g) => Held[g] = Byte(od, Write, g);

        /// <summary>The received value of <c>6200h</c> in the bits <c>6208h</c> has set (§8.3.5).</summary>
        internal void TakeReceived(ObjectDictionary od, int g)
        {
            int filter = Byte(od, FilterMask, g);
            Held[g] = (byte)((Held[g] & ~filter) | (Byte(od, Write, g) & filter));
        }

        /// <summary>The bits with <c>6206h</c> = 1 take <c>6207h</c>; the others keep their value
        /// (§8.3.3, §8.3.4).</summary>
        internal void ApplyErrorValue(ObjectDictionary od, int g)
        {
            int mode = Byte(od, ErrorMode, g);
            Held[g] = (byte)((Held[g] & ~mode) | (Byte(od, ErrorValue, g) & mode));
        }

        /// <summary>Held XOR <c>6202h</c> (§8.3.2), the channels the device lacks at 0.</summary>
        internal byte[] ComputeTerminal(ObjectDictionary od)
        {
            var levels = new byte[Groups];
            for (int g = 0; g < Groups; g++)
            {
                levels[g] = (byte)((Held[g] ^ Byte(od, Polarity, g)) & GroupChannelMask(Channels, g));
            }

            return levels;
        }

        internal void Publish(byte[] levels) => Volatile.Write(ref _terminal, levels);

        private static byte Byte(ObjectDictionary od, ushort index, int g) => od.ReadRaw(index, (byte)(g + 1))[0];
    }
}
