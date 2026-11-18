using System;
using System.Collections.Generic;
using CanKit.Pro.CANopen.Application;

namespace CanKit.Pro.CANopen.Cia401;

// The digital input block, 8-bit access (CiA 401-1 §7.2.2, §8.2.1 to §8.2.7).
internal sealed partial class Cia401Device
{
    private const int ChannelsPerGroup = 8;

    // Null when the configuration has no digital inputs; for a device built from a description,
    // null until Attach finds 6000h. Written before the host is published.
    private DigitalInputBlock? _digitalInputs;

    /// <summary>Sets the physical level of one digital input (CiA 401-1 §7.2.2 Figure 4: what the
    /// sensor delivers, before the polarity of <c>6002h</c>). Any thread; applied on the node's
    /// actor loop, at once when called from inside one of the device's callbacks.</summary>
    /// <param name="channel">The input, <c>1</c> to the number of digital inputs.</param>
    /// <param name="level">The physical level.</param>
    /// <remarks>
    /// <para>
    /// <c>6000h</c> shows the level XOR the polarity bit of <c>6002h</c> (§8.2.1, §8.2.2). When that
    /// changes it, the change is an interrupt for the bit when <c>6005h</c> is TRUE and <c>6006h</c>
    /// has the bit (§8.2.5), or the logical edge is rising (0 to 1) and <c>6007h</c> has it (§8.2.6),
    /// or falling (1 to 0) and <c>6008h</c> has it (§8.2.7), the masks in an OR connection and
    /// <c>6005h</c> over all of them (§8.2.4 to §8.2.7). The edges are those of the logical value, so
    /// with the input inverted a physical falling edge is a rising one (§8.2.6, §8.2.7). An
    /// interrupt asks the node to transmit the changed sub-index of <c>6000h</c>, which sends the
    /// TPDOs that map it, in Operational only. Without one, <c>6000h</c> changes and nothing is sent.
    /// </para>
    /// <para>
    /// The filter of <c>6003h</c> is stored and has no effect: its type and constant are
    /// manufacturer-specific (§8.2.3), and the level set here is taken to be filtered already.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="channel"/> is not an input of
    /// the device.</exception>
    /// <exception cref="InvalidOperationException">No node has attached the device yet, or a device
    /// built from a description has no digital inputs.</exception>
    public void SetDigitalInput(int channel, bool level)
    {
        var block = ApplicationBlock(ref _digitalInputs, "digital inputs");
        if (block is null || channel < 1 || channel > block.Channels)
        {
            throw new ArgumentOutOfRangeException(nameof(channel), channel,
                $"The device has {block?.Channels ?? 0} digital inputs, numbered from 1.");
        }

        var host = RequireHost();
        int group = (channel - 1) / ChannelsPerGroup;
        var bit = (byte)(1 << ((channel - 1) % ChannelsPerGroup));
        host.Post(() => ApplyDigitalInputs(host, block, group, bit, level ? bit : (byte)0));
    }

    /// <summary>Sets the physical levels of the eight inputs of one group, bit 0 being the lowest
    /// channel of the group (CiA 401-1 §7.2.1.3). Any thread; applied as
    /// <see cref="SetDigitalInput"/> is, all eight at once.</summary>
    /// <param name="group">The group, <c>1</c> to the number of started groups of eight: the
    /// sub-index of <c>6000h</c> that shows it.</param>
    /// <param name="levels">The physical levels. The bits of channels the device does not have (21 to
    /// 24 of a device with 20) are ignored: such a channel's physical level is always 0.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="group"/> is not a group of the
    /// device.</exception>
    /// <exception cref="InvalidOperationException">No node has attached the device yet.</exception>
    public void SetDigitalInputs(int group, byte levels)
    {
        var block = RequireGroup(group);
        var host = RequireHost();
        int g = group - 1;
        host.Post(() => ApplyDigitalInputs(host, block, g, block.ChannelMask(g), levels));
    }

    /// <summary>What <c>6000h</c> shows for one group: the physical levels after the polarity of
    /// <c>6002h</c> (CiA 401-1 §8.2.1). Any thread.</summary>
    /// <remarks>The value last applied on the node's actor loop: a set that is still queued there,
    /// made from another thread a moment ago, is not visible yet; one made from inside a callback of
    /// the device is. After the node is disposed it is the last value the node held. During Reset Node, between the node's restore and the device's rewrite in
    /// <see cref="OnReset"/> (one actor job), a reader on another thread can briefly see the restored
    /// value, a default or what <c>1010h</c> stored.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="group"/> is not a group of the
    /// device.</exception>
    /// <exception cref="InvalidOperationException">No node has attached the device yet.</exception>
    public byte ReadDigitalInputs(int group)
    {
        var block = RequireGroup(group);
        return ReadByte(RequireHost(), block.Read.Index, (byte)group);
    }

    private DigitalInputBlock RequireGroup(int group)
    {
        var block = ApplicationBlock(ref _digitalInputs, "digital inputs");
        if (block is null || group < 1 || group > block.Groups)
        {
            throw new ArgumentOutOfRangeException(nameof(group), group,
                $"The device has {block?.Groups ?? 0} groups of digital inputs, numbered from 1.");
        }

        return block;
    }

    /// <summary>A set from the application side: the bits of <paramref name="mask"/> in group
    /// <paramref name="g"/> (0-based) take the physical <paramref name="levels"/>. Actor loop, in a
    /// callback of the device.</summary>
    private void ApplyDigitalInputs(ICanOpenApplicationHost host, DigitalInputBlock block, int g, byte mask, byte levels)
    {
        block.Physical[g] = (byte)((block.Physical[g] & ~mask) | (levels & mask));
        var send = new List<CanOpenObjectAddress>(1);
        RefreshDigitalInputGroup(host, block, g, send);
        RequestTransmission(host, send);
    }

    /// <summary>A written polarity changes what <c>6000h</c> shows at once (CiA 401-1 §7.2.2 Figure
    /// 4), and that change is an edge like any other. The masks and <c>6005h</c> are read when an
    /// edge happens, and <c>6003h</c> has no effect, so a write to them needs nothing here.</summary>
    private void DigitalInputsWritten(ICanOpenApplicationHost host, CanOpenObjectAddress entry, List<CanOpenObjectAddress> send)
    {
        var block = _digitalInputs;
        if (block is null || entry.Index != block.Polarity.Index) return;
        if (entry.Subindex < 1 || entry.Subindex > block.Groups) return;
        RefreshDigitalInputGroup(host, block, entry.Subindex - 1, send);
    }

    /// <summary>Reset Node: <c>6000h</c> of every group is rewritten as the physical levels XOR the
    /// polarity of <c>6002h</c> as the node restored it, for the value the node restored to
    /// <c>6000h</c>, a default or what <c>1010h</c> stored, is not what the sensors deliver. No
    /// interrupt is evaluated: in Initialisation nothing is sent, and entering Operational sends
    /// every group (§6.1). Actor loop.</summary>
    private void DigitalInputsReset(ICanOpenApplicationHost host)
    {
        var block = _digitalInputs;
        if (block is null) return;
        for (int g = 0; g < block.Groups; g++)
        {
            var sub = (byte)(g + 1);
            var logical = (byte)(block.Physical[g] ^ block.Polarity.ReadByte(host.ObjectDictionary, sub));
            if (logical != ReadByte(host, block.Read.Index, sub)) WriteOwn(host, block.Read.Index, sub, logical);
        }
    }

    /// <summary>Every sub-index of <c>6000h</c>, for the transmission on entering Operational.</summary>
    private void DigitalInputsToSendOnStart(List<CanOpenObjectAddress> send)
    {
        var block = _digitalInputs;
        if (block is null) return;
        for (int sub = 1; sub <= block.Groups; sub++) send.Add(new CanOpenObjectAddress(block.Read.Index, (byte)sub));
    }

    /// <summary>
    /// Brings <c>6000h</c> of group <paramref name="g"/> (0-based) to physical XOR polarity, and when
    /// that changed it and a changed bit raises an interrupt, adds the sub-index to
    /// <paramref name="send"/>. The one place a digital input changes, from a set and from a polarity
    /// write alike. Actor loop, in a callback of the device.
    /// </summary>
    private void RefreshDigitalInputGroup(ICanOpenApplicationHost host, DigitalInputBlock block, int g, List<CanOpenObjectAddress> send)
    {
        var sub = (byte)(g + 1);
        byte physical = block.Physical[g];
        byte logical = (byte)(physical ^ block.Polarity.ReadByte(host.ObjectDictionary, sub));
        byte previous = ReadByte(host, block.Read.Index, sub);
        if (logical == previous) return;

        WriteOwn(host, block.Read.Index, sub, logical);

        // The edges of the logical value, not of the physical one (§8.2.6, §8.2.7).
        var changed = (byte)(previous ^ logical);
        var rising = (byte)(changed & logical);
        var falling = (byte)(changed & ~logical);
        if (!DigitalInterrupt(host.ObjectDictionary, block, sub, changed, rising, falling)) return;
        send.Add(new CanOpenObjectAddress(block.Read.Index, sub));
    }

    /// <summary>Whether a change raises an interrupt: <c>6005h</c> TRUE (§8.2.4), and a changed bit in
    /// <c>6006h</c> (§8.2.5), or a rising one, logical 0 to 1, in <c>6007h</c> (§8.2.6), or a falling
    /// one, logical 1 to 0, in <c>6008h</c> (§8.2.7), the masks in an OR connection (§8.2.6, §8.2.7).</summary>
    /// <remarks>The directions are the clauses' and the object names' (CiA 401-1 v3.1.0 §8.2.6, §8.2.7).
    /// Figure 4 of §7.2.2, looked at in the PDF, labels them the other way round: <c>6007h</c>
    /// "High-to-Low" with a falling-edge symbol and <c>6008h</c> "Low-to-High" with a rising one. The
    /// figure is taken for the wiring only (the polarity before <c>6000h</c>, the masks in an OR, and
    /// <c>6005h</c> closing the transmission), not for the directions.</remarks>
    private static bool DigitalInterrupt(ObjectDictionary od, DigitalInputBlock block, byte sub,
        byte changed, byte rising, byte falling)
    {
        if (block.GlobalInterruptEnable.ReadByte(od, 0) == 0) return false;
        int any = changed & block.AnyChange.ReadByte(od, sub);
        int lowToHigh = rising & block.LowToHigh.ReadByte(od, sub);
        int highToLow = falling & block.HighToLow.ReadByte(od, sub);
        return (any | lowToHigh | highToLow) != 0;
    }

    /// <summary>The digital inputs of one device: their indices in its logical device, and the
    /// physical levels the application set.</summary>
    private sealed class DigitalInputBlock
    {
        internal DigitalInputBlock(int channels, int logicalDevice)
        {
            Channels = channels;
            Groups = (channels + ChannelsPerGroup - 1) / ChannelsPerGroup;
            Physical = new byte[Groups];
            Read = new ProfileObject(logicalDevice, Cia401Objects.ReadDigitalInput8);
            Polarity = new ProfileObject(logicalDevice, Cia401Objects.PolarityDigitalInput8);
            FilterEnable = new ProfileObject(logicalDevice, Cia401Objects.DigitalFilterEnable8);
            GlobalInterruptEnable = new ProfileObject(logicalDevice, Cia401Objects.GlobalInterruptEnableDigital8);
            AnyChange = new ProfileObject(logicalDevice, Cia401Objects.DigitalInterruptMaskAnyChange8);
            LowToHigh = new ProfileObject(logicalDevice, Cia401Objects.DigitalInterruptMaskLowToHigh8);
            HighToLow = new ProfileObject(logicalDevice, Cia401Objects.DigitalInterruptMaskHighToLow8);
        }

        internal int Channels { get; }

        /// <summary>Started groups of eight: the highest sub-index of the 8-bit objects (§7.2.1.3).</summary>
        internal int Groups { get; }

        /// <summary>The physical levels per group, as the application set them. Actor loop only.</summary>
        internal byte[] Physical { get; }

        internal ProfileObject Read { get; }

        internal ProfileObject Polarity { get; }

        internal ProfileObject FilterEnable { get; }

        internal ProfileObject GlobalInterruptEnable { get; }

        internal ProfileObject AnyChange { get; }

        internal ProfileObject LowToHigh { get; }

        internal ProfileObject HighToLow { get; }

        /// <summary>The bits of group <paramref name="g"/> (0-based) that are channels of the device:
        /// all eight but in a last group that is not full.</summary>
        internal byte ChannelMask(int g) => GroupChannelMask(Channels, g);

        /// <summary>Declares <c>6000h</c>, <c>6002h</c>, <c>6003h</c>, <c>6005h</c> to <c>6008h</c> with
        /// the data type, access, PDO mapping and default the catalogue gives them, and the number of
        /// groups at sub-index 0 of each array. Constructing thread, in Attach.</summary>
        internal void Declare(ObjectDictionary od)
        {
            DeclareArray(od, Read, Groups);
            DeclareArray(od, Polarity, Groups);
            DeclareArray(od, FilterEnable, Groups);
            DeclareVariable(od, GlobalInterruptEnable);
            DeclareArray(od, AnyChange, Groups);
            DeclareArray(od, LowToHigh, Groups);
            DeclareArray(od, HighToLow, Groups);
        }
    }
}
