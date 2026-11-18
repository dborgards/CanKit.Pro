using System;
using System.Collections.Generic;
using CanKit.Pro.CANopen.Application;

namespace CanKit.Pro.CANopen.Cia401;

// The analog input block, 16-bit access (CiA 401-1 §5.3, §6.2.8, §7.2.4, §8.4.2, §8.6.2 to §8.6.9).
internal sealed partial class Cia401Device
{
    /// <summary>Error code <c>0080h</c>, "Warning: Analog inputs disabled" (CiA 401-1 §5.3, §5.4
    /// Table 1). An error code, not an object index.</summary>
    private const ushort AnalogInputsDisabledWarning = 0x0080;

    /// <summary>The channels one sub-index of <c>6422h</c>, an UNSIGNED32 "interrupt source bank",
    /// carries (CiA 401-1 §8.6.3).</summary>
    private const int ChannelsPerSourceBank = 32;

    // The bits of 6421h (CiA 401-1 §8.6.2, Figure 8 and Table 148).
    private const byte UpperLimitSelected = 0x01;
    private const byte LowerLimitSelected = 0x02;
    private const byte DeltaSelected = 0x04;
    private const byte NegativeDeltaSelected = 0x08;
    private const byte PositiveDeltaSelected = 0x10;
    private const byte ReservedSelectionBits = 0xE0;

    // Null when the configuration has no analog inputs.
    private readonly AnalogInputBlock? _analogInputs;

    /// <summary>Sets the value of one analog input: the 16-bit value as <c>6401h</c> shows it
    /// (CiA 401-1 §8.4.2). Any thread; applied on the node's actor loop, at once when called from
    /// inside one of the device's callbacks.</summary>
    /// <param name="channel">The input, <c>1</c> to the number of analog inputs.</param>
    /// <param name="value">The value. §8.4.2 has a value narrower than 16 bits left adjusted, its
    /// unused low bits zero; that is the application's business: the device stores what it is
    /// given.</param>
    /// <remarks>
    /// <para>
    /// <b>Interrupts.</b> A value that changes <c>6401h</c> is an interrupt for the channel when
    /// <c>6423h</c> is TRUE (§8.6.4) and one of the conditions <c>6421h</c> selects for the channel
    /// holds (§8.6.2): bit 0, the value is equal to or above the upper limit <c>6424h</c> (§8.6.5);
    /// bit 1, it is below the lower limit <c>6425h</c> (§8.6.6); bit 2, it differs from the last
    /// communicated value by more than <c>6426h</c> (§8.6.7); bit 3, it is more than <c>6427h</c>
    /// below that value (§8.6.8); bit 4, more than <c>6428h</c> above it (§8.6.9). The selected
    /// conditions combine in an OR: "If more than one interrupt condition is enabled; the PDO is
    /// transmitted if one of these conditions is fulfilled" (§6.2.8). A channel with <c>6421h</c> = 0
    /// never interrupts. A value equal to what <c>6401h</c> holds is no change and interrupts
    /// nothing; while a limit holds, every change interrupts (§8.6.5, §8.6.6). With the defaults
    /// (07h, limits and delta 0) every change interrupts: every value is at or above 0 or below it.
    /// </para>
    /// <para>
    /// Figure 6 (§7.2.4) and one sentence of §8.6.5 and §8.6.6 read otherwise, and are not followed.
    /// The figure joins the two limit comparisons in an XOR, ANDs the result with the delta and
    /// shows no <c>6421h</c>; §8.6.5 and §8.6.6 say a limit interrupts on every change "if there is
    /// no additional trigger condition, e.g. an input interrupt delta", which also implies an AND.
    /// The device follows §6.2.8 and the per-channel selection of §8.6.2, which states the
    /// combination explicitly: with upper limit and positive delta selected, a rise of more than
    /// the delta interrupts below the limit too.
    /// </para>
    /// <para>
    /// <b>Limits, left adjusted.</b> §8.6.5 and §8.6.6 say the INTEGER32 limit "shall always be
    /// left adjusted". The device reads that as: the limit is a value in the 32-bit left-adjusted
    /// representation of the input, in which the 16-bit value <c>v</c> is <c>v · 2^16</c>, its
    /// low 16 bits zero (§8.4.3 defines the left-adjusted 32-bit input that way). So the comparison
    /// is <c>v · 2^16 ≥ 6424h</c> and <c>v · 2^16 &lt; 6425h</c>, as INTEGER32 numbers
    /// (<see cref="LeftAdjusted.Widen"/>): a limit of 1000 counts is written <c>1000 · 2^16</c>
    /// (<c>03E8_0000h</c>), and the low 16 bits of a limit take part, so an upper limit of
    /// <c>03E8_0001h</c> is first reached at 1001. Figure 6 marks only the 16-bit value mapped into
    /// the TPDO as left adjusted and taps the comparators before it; the clause, which names the
    /// limit, is followed.
    /// </para>
    /// <para>
    /// <b>Deltas, plain.</b> §8.6.7 to §8.6.9 say nothing of left adjustment, where §8.6.5 and
    /// §8.6.6, written to the same pattern beside them, say it each time; the NOTE they share with
    /// the limits ("not scaled and not offset") puts them before the scaling, not into one number
    /// format. So a delta is a difference of the 16-bit values: a delta of 50 counts is written 50.
    /// "By more than" (Table 148) is strictly greater.
    /// </para>
    /// <para>
    /// <b>The last communicated value</b> of a channel, which the deltas are measured from, is its
    /// value at its last interrupt, at the node's last entry into Operational, and when <c>6423h</c>
    /// last went from FALSE to TRUE — without the last, the first change after enabling would be
    /// measured from a value long gone.
    /// </para>
    /// <para>
    /// <b>What an interrupt does.</b> It sets the channel's bit in <c>6422h</c>, 32 channels per
    /// sub-index, channel <c>c</c> being bit <c>(c − 1) mod 32</c> of sub-index
    /// <c>(c − 1) div 32 + 1</c> (§8.6.3), and asks the node to transmit <c>6401h</c>:c, which sends
    /// the TPDOs that map it, in Operational only. The interrupts of one call (one channel here,
    /// several in <see cref="SetAnalogInputs"/>) are one request, so a TPDO goes out once for them.
    /// Interrupts are evaluated in every NMT state; outside Operational nothing is sent, and the
    /// bits and the last communicated values they leave are reset on entering Operational.
    /// </para>
    /// <para>
    /// <b><c>6422h</c> deviates from §8.6.3</b>, which resets the bits "automatically after read by
    /// SDO or transmitted by means of a PDO": the application seam reports neither a read nor a
    /// completed transmission to the device, and clearing a bit when the device asks for the
    /// transmission would clear it before any master could read it. The bits accumulate instead,
    /// and are cleared when <c>6423h</c> is written (either value), when the node enters
    /// Operational, and on every NMT reset.
    /// </para>
    /// <para>
    /// <b>The warning of §5.3.</b> When the node enters Operational with <c>6423h</c> FALSE, the
    /// device raises error <c>0080h</c> (§5.4 Table 1) with no register bit of its own; the node sets
    /// the generic bit for every active error, so the EMCY carries <c>01h</c> in its error register
    /// byte, and the node's NMT state is untouched, as §5.3 demands. §5.3 ties the warning to the
    /// transition, so writing FALSE while Operational raises nothing. The norm does not say when it
    /// ends; the device ends it, with the error reset <c>0000h</c> (CiA 301 v4.2.0 §7.2.7.1), when
    /// <c>6423h</c> is written TRUE or the node leaves Operational, and raises it again on the next
    /// entry with <c>6423h</c> FALSE. A reset ends it without an EMCY, as it ends every error.
    /// </para>
    /// <para>
    /// <b>Entering Operational</b> asks for every channel's <c>6401h</c>, in the same request as the
    /// digital inputs, whatever <c>6423h</c> says: §6.1 sends every TPDO of transmission type 255
    /// then, and <c>6423h</c> governs interrupts only (§8.6.4). TPDOs 2 to 4 are disabled by default
    /// (<see cref="Cia401Configuration.DefaultPdos"/>), so the request reaches the bus only once a
    /// master has created them.
    /// </para>
    /// <para>
    /// Not built in this slice: the 8-bit, 32-bit, float and manufacturer-specific inputs
    /// (<c>6400h</c>, <c>6402h</c> to <c>6404h</c>), the float limits and deltas, offset, scaling
    /// and SI unit (<c>6429h</c> to <c>6432h</c>); the device does not declare them.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="channel"/> is not an analog
    /// input of the device.</exception>
    /// <exception cref="InvalidOperationException">No node has attached the device yet.</exception>
    public void SetAnalogInput(int channel, short value)
    {
        var block = RequireAnalogInput(channel, nameof(channel));
        var host = RequireHost();
        var values = new[] { value };
        host.Post(() => ApplyAnalogInputs(host, block, channel, values));
    }

    /// <summary>Sets the values of the consecutive analog inputs from <paramref name="firstChannel"/>
    /// on, all in one step: applied as <see cref="SetAnalogInput"/> is, and the interrupts they
    /// raise are one request, so a TPDO that maps several of them goes out once. Any thread; the
    /// values are copied before the call returns.</summary>
    /// <param name="firstChannel">The first input, <c>1</c> to the number of analog inputs.</param>
    /// <param name="values">The values of <paramref name="firstChannel"/> and the inputs after it;
    /// empty sets nothing.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="firstChannel"/> is not an analog
    /// input of the device, or <paramref name="values"/> runs past the last one.</exception>
    /// <exception cref="InvalidOperationException">No node has attached the device yet.</exception>
    public void SetAnalogInputs(int firstChannel, ReadOnlySpan<short> values)
    {
        var block = RequireAnalogInput(firstChannel, nameof(firstChannel));
        if (values.Length > block.Channels - firstChannel + 1)
        {
            throw new ArgumentOutOfRangeException(nameof(values), values.Length,
                $"{values.Length} values from channel {firstChannel} run past the device's {block.Channels} analog inputs.");
        }

        var host = RequireHost();
        if (values.IsEmpty) return;
        var copy = values.ToArray();
        host.Post(() => ApplyAnalogInputs(host, block, firstChannel, copy));
    }

    /// <summary>What <c>6401h</c> shows for one analog input (CiA 401-1 §8.4.2). Any thread.</summary>
    /// <remarks>The value last applied on the node's actor loop: a set that is still queued there,
    /// made from another thread a moment ago, is not visible yet; one made from inside a callback of
    /// the device is. After the node is disposed it is the last value the node held.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="channel"/> is not an analog
    /// input of the device.</exception>
    /// <exception cref="InvalidOperationException">No node has attached the device yet.</exception>
    public short ReadAnalogInput(int channel)
    {
        var block = RequireAnalogInput(channel, nameof(channel));
        return ReadAnalogValue(RequireHost(), block, (byte)channel);
    }

    private AnalogInputBlock RequireAnalogInput(int channel, string parameter)
    {
        var block = _analogInputs;
        if (block is null || channel < 1 || channel > block.Channels)
        {
            throw new ArgumentOutOfRangeException(parameter, channel,
                $"The device has {block?.Channels ?? 0} analog inputs, numbered from 1.");
        }

        return block;
    }

    private static short ReadAnalogValue(ICanOpenApplicationHost host, AnalogInputBlock block, byte sub)
        => (short)host.ObjectDictionary.ReadSigned(block.Read, sub);

    /// <summary>A set from the application side: each value into <c>6401h</c> from channel
    /// <paramref name="first"/> on, then one request for every interrupt among them. Actor loop, in
    /// a callback of the device.</summary>
    private void ApplyAnalogInputs(ICanOpenApplicationHost host, AnalogInputBlock block, int first, short[] values)
    {
        bool enabled = ReadByte(host, block.GlobalInterruptEnable, 0) != 0;
        var send = new List<CanOpenObjectAddress>(values.Length);
        for (int i = 0; i < values.Length; i++) ApplyAnalogInput(host, block, (byte)(first + i), values[i], enabled, send);
        RequestTransmission(host, send);
    }

    /// <summary>One channel: a value that changes <c>6401h</c> is written, and when it interrupts,
    /// it becomes the last communicated value, sets its bit in <c>6422h</c> and is added to
    /// <paramref name="send"/>. Actor loop.</summary>
    private void ApplyAnalogInput(ICanOpenApplicationHost host, AnalogInputBlock block, byte sub, short value,
        bool enabled, List<CanOpenObjectAddress> send)
    {
        if (ReadAnalogValue(host, block, sub) == value) return;
        WriteOwn(host, block.Read, sub, Encode(OdDataType.Integer16, value));
        if (!enabled || !AnalogInterrupt(host.ObjectDictionary, block, sub, value, block.LastCommunicated[sub - 1])) return;

        block.LastCommunicated[sub - 1] = value;
        SetInterruptSource(host, block, sub);
        send.Add(new CanOpenObjectAddress(block.Read, sub));
    }

    /// <summary>Whether <paramref name="value"/>, a change of channel <paramref name="sub"/>, meets
    /// a condition <c>6421h</c> selects for it, the conditions in an OR (CiA 401-1 §6.2.8, §8.6.2 to
    /// §8.6.9; the readings are on <see cref="SetAnalogInput"/>).</summary>
    private static bool AnalogInterrupt(ObjectDictionary od, AnalogInputBlock block, byte sub, short value, short last)
    {
        int selection = od.ReadRaw(block.TriggerSelection, sub)[0];
        if (selection == 0) return false;

        int leftAdjusted = LeftAdjusted.Widen(value);
        long change = (long)value - last;
        return ((selection & UpperLimitSelected) != 0 && leftAdjusted >= od.ReadSigned(block.UpperLimit, sub))
            || ((selection & LowerLimitSelected) != 0 && leftAdjusted < od.ReadSigned(block.LowerLimit, sub))
            || ((selection & DeltaSelected) != 0 && Math.Abs(change) > od.ReadUnsigned(block.Delta, sub))
            || ((selection & NegativeDeltaSelected) != 0 && -change > od.ReadUnsigned(block.NegativeDelta, sub))
            || ((selection & PositiveDeltaSelected) != 0 && change > od.ReadUnsigned(block.PositiveDelta, sub));
    }

    /// <summary>Sets channel <paramref name="sub"/>'s bit in its bank of <c>6422h</c> (§8.6.3).</summary>
    private void SetInterruptSource(ICanOpenApplicationHost host, AnalogInputBlock block, byte sub)
    {
        var bank = (byte)(((sub - 1) / ChannelsPerSourceBank) + 1);
        uint bit = 1u << ((sub - 1) % ChannelsPerSourceBank);
        uint bits = host.ObjectDictionary.ReadUnsigned(block.InterruptSource, bank);
        if ((bits & bit) == 0) WriteOwn(host, block.InterruptSource, bank, Encode(OdDataType.Unsigned32, bits | bit));
    }

    /// <summary>Clears every bank of <c>6422h</c>: the deviation from §8.6.3 described on
    /// <see cref="SetAnalogInput"/>.</summary>
    private void ClearInterruptSource(ICanOpenApplicationHost host, AnalogInputBlock block)
    {
        for (int bank = 1; bank <= block.Banks; bank++)
        {
            if (host.ObjectDictionary.ReadUnsigned(block.InterruptSource, (byte)bank) != 0)
                WriteOwn(host, block.InterruptSource, (byte)bank, Encode(OdDataType.Unsigned32, 0));
        }
    }

    /// <summary>Every channel's current value becomes its last communicated one.</summary>
    private static void TakeCurrentAsLastCommunicated(ICanOpenApplicationHost host, AnalogInputBlock block)
    {
        for (int c = 1; c <= block.Channels; c++) block.LastCommunicated[c - 1] = ReadAnalogValue(host, block, (byte)c);
    }

    /// <summary>A write to <c>6423h</c> clears <c>6422h</c>; TRUE ends the warning of §5.3, and
    /// FALSE to TRUE takes the current values as the last communicated ones. FALSE raises nothing
    /// (§5.3 names the transition into Operational). The limits, deltas and the selection are read
    /// when a value changes and need nothing here. Actor loop.</summary>
    private void AnalogInputsWritten(ICanOpenApplicationHost host, CanOpenObjectAddress entry)
    {
        var block = _analogInputs;
        if (block is null || entry.Index != block.GlobalInterruptEnable || entry.Subindex != 0) return;

        ClearInterruptSource(host, block);
        bool enabled = ReadByte(host, block.GlobalInterruptEnable, 0) != 0;
        if (enabled && !block.GlobalInterruptEnableSeen) TakeCurrentAsLastCommunicated(host, block);
        block.GlobalInterruptEnableSeen = enabled;
        if (enabled) host.ClearError(AnalogInputsDisabledWarning);
    }

    /// <summary>Entering Operational: the interrupt state starts afresh (the last communicated
    /// values are the current ones, <c>6422h</c> is cleared), every channel's <c>6401h</c> joins the
    /// request of §6.1, and with <c>6423h</c> FALSE the warning of §5.3 is raised. Actor loop.</summary>
    private void AnalogInputsEnterOperational(ICanOpenApplicationHost host, List<CanOpenObjectAddress> send)
    {
        var block = _analogInputs;
        if (block is null) return;

        TakeCurrentAsLastCommunicated(host, block);
        ClearInterruptSource(host, block);
        for (int c = 1; c <= block.Channels; c++) send.Add(new CanOpenObjectAddress(block.Read, (byte)c));
        if (ReadByte(host, block.GlobalInterruptEnable, 0) == 0) host.RaiseError(AnalogInputsDisabledWarning, 0);
    }

    /// <summary>Leaving Operational ends the warning of §5.3; in Initialisation (a reset) the node
    /// has ended it already, without an EMCY. Actor loop.</summary>
    private void AnalogInputsLeaveOperational(ICanOpenApplicationHost host)
    {
        if (_analogInputs is null) return;
        host.ClearError(AnalogInputsDisabledWarning);
    }

    /// <summary>An NMT reset clears <c>6422h</c>, which Reset Communication does not restore, and
    /// reads <c>6423h</c> afresh, which Reset Node restores without reporting it. Actor loop.</summary>
    private void AnalogInputsReset(ICanOpenApplicationHost host)
    {
        var block = _analogInputs;
        if (block is null) return;
        ClearInterruptSource(host, block);
        block.GlobalInterruptEnableSeen = ReadByte(host, block.GlobalInterruptEnable, 0) != 0;
    }

    /// <summary>Whether a write is a trigger selection with a reserved bit set: bits 5 to 7 of
    /// <c>6421h</c> are "reserved for future use", 0b only (CiA 401-1 §8.6.2, Table 148). Writing
    /// thread, under the dictionary's write gate.</summary>
    private bool IsReservedTriggerSelection(ushort index, byte subindex, byte[] value)
    {
        var block = _analogInputs;
        return block is not null && index == block.TriggerSelection && subindex >= 1
            && value.Length == 1 && (value[0] & ReservedSelectionBits) != 0;
    }

    /// <summary>
    /// The one reading of "left adjusted" for the INTEGER32 objects that stand for a 16-bit analog
    /// value (CiA 401-1 §8.4.2, §8.4.3, §8.6.5, §8.6.6, and Figure 7's legend for <c>6444h</c>): the
    /// 16-bit value <c>v</c> is the INTEGER32 <c>v · 2^16</c>, its low 16 bits zero, as §8.4.3
    /// defines a left-adjusted 32-bit input.
    /// </summary>
    /// <remarks>What the low 16 bits of an INTEGER32 do depends on the use. A limit is compared
    /// with the widened input as an INTEGER32, so its low bits take part (see
    /// <see cref="SetAnalogInput"/>). A value that has to become a 16-bit output (<c>6444h</c>, the
    /// analog output error value) is narrowed to its upper word, and the low bits, which a 16-bit
    /// value cannot carry, are dropped.</remarks>
    internal static class LeftAdjusted
    {
        /// <summary><paramref name="value"/> · 2^16: the 16-bit value left adjusted in 32 bits.</summary>
        internal static int Widen(short value) => value << 16;

        /// <summary>The upper word of <paramref name="value"/>: the 16-bit value a left-adjusted
        /// INTEGER32 stands for, its low 16 bits dropped.</summary>
        internal static short Narrow(int value) => (short)(value >> 16);
    }

    /// <summary>The analog inputs of one device: their indices in its logical device, and the
    /// interrupt state that is not in the dictionary.</summary>
    private sealed class AnalogInputBlock
    {
        private readonly int _logicalDevice;

        internal AnalogInputBlock(int channels, int logicalDevice)
        {
            Channels = channels;
            Banks = (channels + ChannelsPerSourceBank - 1) / ChannelsPerSourceBank;
            LastCommunicated = new short[channels];
            Read = Shift(logicalDevice, Cia401Objects.ReadAnalogInput16);
            TriggerSelection = Shift(logicalDevice, Cia401Objects.AnalogInputInterruptTriggerSelection);
            InterruptSource = Shift(logicalDevice, Cia401Objects.AnalogInputInterruptSource);
            GlobalInterruptEnable = Shift(logicalDevice, Cia401Objects.AnalogInputGlobalInterruptEnable);
            UpperLimit = Shift(logicalDevice, Cia401Objects.AnalogInputInterruptUpperLimitInteger);
            LowerLimit = Shift(logicalDevice, Cia401Objects.AnalogInputInterruptLowerLimitInteger);
            Delta = Shift(logicalDevice, Cia401Objects.AnalogInputInterruptDeltaUnsigned);
            NegativeDelta = Shift(logicalDevice, Cia401Objects.AnalogInputInterruptNegativeDeltaUnsigned);
            PositiveDelta = Shift(logicalDevice, Cia401Objects.AnalogInputInterruptPositiveDeltaUnsigned);
            _logicalDevice = logicalDevice;
        }

        internal int Channels { get; }

        /// <summary>The sub-indices of <c>6422h</c>: 32 channels each (§8.6.3).</summary>
        internal int Banks { get; }

        /// <summary>The last communicated value per channel. Actor loop only.</summary>
        internal short[] LastCommunicated { get; }

        /// <summary><c>6423h</c> as the device last saw it, to tell a write of TRUE that enables
        /// interrupts from one that finds them enabled. Actor loop only.</summary>
        internal bool GlobalInterruptEnableSeen { get; set; }

        internal ushort Read { get; }

        internal ushort TriggerSelection { get; }

        internal ushort InterruptSource { get; }

        internal ushort GlobalInterruptEnable { get; }

        internal ushort UpperLimit { get; }

        internal ushort LowerLimit { get; }

        internal ushort Delta { get; }

        internal ushort NegativeDelta { get; }

        internal ushort PositiveDelta { get; }

        /// <summary>Declares <c>6401h</c>, <c>6421h</c> to <c>6428h</c> with the data type, access,
        /// PDO mapping and default the catalogue gives them, one sub-index per channel (one per bank
        /// of 32 for <c>6422h</c>). Constructing thread, in Attach.</summary>
        internal void Declare(ObjectDictionary od)
        {
            DeclareArray(od, _logicalDevice, Channels, Cia401Objects.ReadAnalogInput16);
            DeclareArray(od, _logicalDevice, Channels, Cia401Objects.AnalogInputInterruptTriggerSelection);
            DeclareArray(od, _logicalDevice, Banks, Cia401Objects.AnalogInputInterruptSource);
            DeclareVariable(od, _logicalDevice, Cia401Objects.AnalogInputGlobalInterruptEnable);
            DeclareArray(od, _logicalDevice, Channels, Cia401Objects.AnalogInputInterruptUpperLimitInteger);
            DeclareArray(od, _logicalDevice, Channels, Cia401Objects.AnalogInputInterruptLowerLimitInteger);
            DeclareArray(od, _logicalDevice, Channels, Cia401Objects.AnalogInputInterruptDeltaUnsigned);
            DeclareArray(od, _logicalDevice, Channels, Cia401Objects.AnalogInputInterruptNegativeDeltaUnsigned);
            DeclareArray(od, _logicalDevice, Channels, Cia401Objects.AnalogInputInterruptPositiveDeltaUnsigned);
            GlobalInterruptEnableSeen = od.ReadRaw(GlobalInterruptEnable, 0x00)[0] != 0;
        }
    }
}
