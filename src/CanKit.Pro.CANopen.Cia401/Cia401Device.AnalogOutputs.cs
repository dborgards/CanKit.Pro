using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using CanKit.Pro.CANopen.Application;

namespace CanKit.Pro.CANopen.Cia401;

// The analog output block, 16-bit access (CiA 401-1 §6.2.7, §7.2.5, §8.5.2, §8.7.4, §8.7.5). Its
// error state is the one of the digital outputs, in Cia401Device.Errors.cs.
internal sealed partial class Cia401Device
{
    /// <summary>The value of <c>6443h</c>, analog output error mode, that takes the error value
    /// (CiA 401-1 §8.7.4); 0h keeps the actual value, and the values above 1h are reserved.</summary>
    private const byte AnalogErrorModeErrorValue = 0x01;

    // Null when the configuration has no analog outputs.
    private readonly AnalogOutputBlock? _analogOutputs;

    // The analog terminal values as they were when the running callback first changed them; null
    // while no change waits to be reported. Actor loop.
    private short[]? _analogOutputsBefore;

    /// <summary>A seam for tests: every set of analog terminal values the device publishes after
    /// Attach, at the moment it becomes visible to <see cref="ReadAnalogOutput"/>. Actor loop.</summary>
    internal event Action<IReadOnlyList<short>>? AnalogLevelsPublishedForTests;

    /// <summary>The value at the terminal of one analog output: the 16-bit value as <c>6411h</c>
    /// carries it (CiA 401-1 §8.5.2), or its error value. Any thread.</summary>
    /// <param name="channel">The output, <c>1</c> to the number of analog outputs: the sub-index of
    /// <c>6411h</c> that addresses it.</param>
    /// <remarks>
    /// <para>
    /// How a written value reaches the terminal (CiA 401-1 §7.2.5 Figure 7, §8.5.2, §8.7.4, §8.7.5):
    /// </para>
    /// <list type="number">
    /// <item><description>A write to <c>6411h</c>, by RPDO, SDO or locally, is stored there and,
    /// outside the error state, becomes the output's value. §8.5.2 has a value of less than 16 bits
    /// left adjusted, its unused low bits zero; that is the writer's business, and the device drives
    /// the 16-bit value it is given. Analog outputs have no polarity and no filter mask. A local
    /// write from inside a callback of the device is the device's own write and is never applied,
    /// as for the digital outputs (see <see cref="ReadDigitalOutputs"/>).</description></item>
    /// <item><description>In the error state (<see cref="OutputsInErrorState"/>) each output with
    /// <c>6443h</c> = 1h takes its error value and each with 0h keeps the value it has (§8.7.4);
    /// writes to <c>6411h</c> are stored and reach no terminal. <c>6443h</c> and <c>6444h</c> written
    /// while it lasts apply at once. <c>6443h</c> takes 0h and 1h only: §8.7.4 reserves the others,
    /// and a write of one is refused with <c>06090030h</c>, over SDO and locally.</description></item>
    /// </list>
    /// <para>
    /// <b>The error value.</b> <c>6444h</c> is an INTEGER32, the output a 16-bit value. §8.7.5 does
    /// not say how the one becomes the other; Figure 7 marks the <c>6444h</c> input of the error
    /// switch "(1) *", INTEGER32 left adjusted, and the figure decides: the output takes the upper
    /// word of <c>6444h</c> (<see cref="Cia401LeftAdjusted.Narrow"/>), the reading the analog input limits
    /// rest on (§8.6.5, §8.6.6). So 1234h is written <c>1234_0000h</c>, and the low word, a fraction
    /// a 16-bit output cannot show, is ignored: <c>1234_5678h</c> drives 1234h.
    /// </para>
    /// <para>
    /// <b>Figure 7 and the clauses.</b> The figure has the structure of Figure 5 of the digital
    /// outputs: <c>6411h</c> before the error switch, which writes nothing back into it, and the 0h
    /// branch of <c>6443h</c> feeding back the output's own value, the "actual value rest" of §8.7.4.
    /// The device follows it as it follows Figure 5 (see <see cref="OutputsInErrorState"/>). Where the
    /// figure and a clause disagree, the clause is followed. The figure labels the switch "Switch if
    /// Device Failure" only, where §8.7.4 names the device failure and the "'Stop remote node'
    /// indication": the causes are those of the digital outputs. And the figure draws offset and
    /// scaling (<c>6441h</c>, <c>6442h</c>, <c>6446h</c>, <c>6447h</c>) after the error switch, which
    /// would offset and scale the error value, where the NOTE of §8.7.5 says it is "not scaled and
    /// not offset"; this slice builds neither offset nor scaling, so nothing turns on it yet.
    /// </para>
    /// <para>
    /// The value last applied on the node's actor loop: an RPDO or a write still queued there is not
    /// visible yet. After the node is disposed it is the last value the device drove.
    /// </para>
    /// <para>
    /// Not built in this slice: the 8-bit, 32-bit, float and manufacturer-specific outputs
    /// (<c>6410h</c>, <c>6412h</c> to <c>6414h</c>), offset, scaling, the float error value and the
    /// SI unit (<c>6441h</c>, <c>6442h</c>, <c>6445h</c> to <c>6450h</c>); the device does not
    /// declare them.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="channel"/> is not an analog
    /// output of the device.</exception>
    /// <exception cref="InvalidOperationException">No node has attached the device yet.</exception>
    public short ReadAnalogOutput(int channel)
    {
        var block = _analogOutputs;
        if (block is null || channel < 1 || channel > block.Channels)
        {
            throw new ArgumentOutOfRangeException(nameof(channel), channel,
                $"The device has {block?.Channels ?? 0} analog outputs, numbered from 1.");
        }

        RequireHost();
        return block.Terminal[channel - 1];
    }

    /// <summary>
    /// A written entry of the analog output block, into the held values only: the caller refreshes
    /// once for all the entries of its callback (<see cref="RefreshAnalogOutputs"/>). <c>6411h</c>
    /// becomes the held value outside the error state only; <c>6443h</c> and <c>6444h</c> written in
    /// the error state apply at once, the error switch of Figure 7 (§7.2.5) being live while the
    /// failure lasts. Coalesced: what <c>6411h</c> holds now is applied. Actor loop.
    /// </summary>
    private void AnalogOutputsWritten(ICanOpenApplicationHost host, CanOpenObjectAddress entry)
    {
        var block = _analogOutputs;
        if (block is null || entry.Subindex < 1 || entry.Subindex > block.Channels) return;
        int c = entry.Subindex - 1;
        var od = host.ObjectDictionary;

        if (entry.Index == block.Write)
        {
            if (_inErrorState) return;
            block.LoadHeld(od, c);
        }
        else if (entry.Index == block.ErrorMode || entry.Index == block.ErrorValue)
        {
            if (!_inErrorState) return;
            block.ApplyErrorValue(od, c);
        }
    }

    /// <summary>Entering the error state: every output with <c>6443h</c> = 1h takes its error value
    /// (§8.7.4, §8.7.5). Actor loop.</summary>
    private void AnalogOutputsEnterErrorState(ICanOpenApplicationHost host)
    {
        var block = _analogOutputs;
        if (block is null) return;
        for (int c = 0; c < block.Channels; c++) block.ApplyErrorValue(host.ObjectDictionary, c);
        RefreshAnalogOutputs();
    }

    /// <summary>Reset Node drops the held values: they are taken from <c>6411h</c> as the node
    /// restored it, its default or a stored value (§6.2.7 NOTE), and a device failure that outlasts
    /// the reset puts them in the error state again. Not for Reset Communication, as for the digital
    /// outputs (<see cref="DigitalOutputsReset"/>). Actor loop.</summary>
    private void AnalogOutputsReset(ICanOpenApplicationHost host)
    {
        var block = _analogOutputs;
        if (block is null) return;
        var od = host.ObjectDictionary;
        for (int c = 0; c < block.Channels; c++)
        {
            block.LoadHeld(od, c);
            if (_inErrorState) block.ApplyErrorValue(od, c);
        }

        RefreshAnalogOutputs();
    }

    /// <summary>Brings the terminal values to the held ones, and when that changed one, remembers
    /// the values before it for <see cref="RaiseOutputsChanged"/>. The one place an analog terminal
    /// value changes after Attach; called once the held values of a step are complete. Actor
    /// loop.</summary>
    private void RefreshAnalogOutputs()
    {
        var block = _analogOutputs;
        if (block is null) return;
        var current = block.Terminal;
        var values = (short[])block.Held.Clone();
        if (SameValues(values, current)) return;
        _analogOutputsBefore ??= current;
        block.Publish(values);
        AnalogLevelsPublishedForTests?.Invoke(new ReadOnlyCollection<short>(values));
    }

    private static bool SameValues(short[] a, short[] b)
    {
        for (int c = 0; c < a.Length; c++)
        {
            if (a[c] != b[c]) return false;
        }

        return true;
    }

    /// <summary>The lowest channel (1-based) whose value differs between <paramref name="before"/>
    /// and <paramref name="after"/>; null when none does.</summary>
    private static int? FirstAnalogChange(short[] before, short[] after)
    {
        for (int c = 0; c < after.Length; c++)
        {
            if (before[c] != after[c]) return c + 1;
        }

        return null;
    }

    /// <summary>Whether a write is an analog output error mode other than 0h and 1h, which §8.7.4
    /// reserves. Writing thread, under the dictionary's write gate.</summary>
    private bool IsReservedAnalogErrorMode(ushort index, byte subindex, byte[] value)
    {
        var block = _analogOutputs;
        return block is not null && index == block.ErrorMode && subindex >= 1
            && value.Length == 1 && value[0] > AnalogErrorModeErrorValue;
    }

    /// <summary>The analog outputs of one device: their indices in its logical device, the held
    /// values, and the values at the terminals.</summary>
    private sealed class AnalogOutputBlock
    {
        private readonly int _logicalDevice;

        // Published whole and never changed afterwards, so a reader on any thread sees one
        // consistent set of values.
        private short[] _terminal;

        internal AnalogOutputBlock(int channels, int logicalDevice)
        {
            Channels = channels;
            Held = new short[channels];
            _terminal = new short[channels];
            Write = Shift(logicalDevice, Cia401Objects.WriteAnalogOutput16);
            ErrorMode = Shift(logicalDevice, Cia401Objects.AnalogOutputErrorMode);
            ErrorValue = Shift(logicalDevice, Cia401Objects.AnalogOutputErrorValueInteger);
            _logicalDevice = logicalDevice;
        }

        internal int Channels { get; }

        /// <summary>The value per channel that drives the terminal: what <c>6411h</c> gave, or what
        /// the error switch set. Actor loop only.</summary>
        internal short[] Held { get; }

        /// <summary>The values at the terminals, per channel. Any thread; the array is not to be
        /// changed.</summary>
        internal short[] Terminal => Volatile.Read(ref _terminal);

        internal ushort Write { get; }

        internal ushort ErrorMode { get; }

        internal ushort ErrorValue { get; }

        /// <summary>Declares <c>6411h</c>, <c>6443h</c> and <c>6444h</c> as the catalogue gives
        /// them, one sub-index per channel, then takes the held values from <c>6411h</c> and the
        /// terminals from those. Constructing thread, in Attach.</summary>
        internal void Declare(ObjectDictionary od)
        {
            DeclareArray(od, _logicalDevice, Channels, Cia401Objects.WriteAnalogOutput16);
            DeclareArray(od, _logicalDevice, Channels, Cia401Objects.AnalogOutputErrorMode);
            DeclareArray(od, _logicalDevice, Channels, Cia401Objects.AnalogOutputErrorValueInteger);
            for (int c = 0; c < Channels; c++) LoadHeld(od, c);
            Publish((short[])Held.Clone());
        }

        /// <summary>Held = <c>6411h</c>: after power-on and Reset Node the outputs are what the
        /// object holds, its default or a stored value (§6.2.7 NOTE).</summary>
        internal void LoadHeld(ObjectDictionary od, int c) => Held[c] = (short)od.ReadSigned(Write, (byte)(c + 1));

        /// <summary>With <c>6443h</c> = 1h the upper word of <c>6444h</c>; with 0h the value is kept
        /// (§8.7.4, §8.7.5, Figure 7).</summary>
        internal void ApplyErrorValue(ObjectDictionary od, int c)
        {
            var sub = (byte)(c + 1);
            if (od.ReadRaw(ErrorMode, sub)[0] != AnalogErrorModeErrorValue) return;
            Held[c] = Cia401LeftAdjusted.Narrow(od.ReadSigned(ErrorValue, sub));
        }

        internal void Publish(short[] values) => Volatile.Write(ref _terminal, values);
    }
}
