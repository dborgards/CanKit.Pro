using System;
using System.Collections.Generic;
using CanKit.Pro.CANopen.Application;

namespace CanKit.Pro.CANopen.Cia408;

// The control word that drives the device state machine (CiA 408 v2.2.0 §7.7, Table 7; §9.3.2,
// §9.3.6), its source (§9.3.9), and the status word that shows the state (§7.6, Table 4; §9.3.3,
// §9.3.7).
internal sealed partial class Cia408Device
{
    // The control word last fed to the machine, from either object or the local source: the
    // "previous" word of the next one. Kept across every state, NOT_READY and the fault states
    // included. Written on the actor loop, read from any thread.
    private volatile uint _controlWord;

    // Whether the control word comes from the local source, 604Fh = 1 (§9.3.9), as of the last
    // write of 604Fh the device was told of: followed in commit order with the control words
    // (OnCommandWritten). Actor loop, and Attach.
    private bool _localSource;

    /// <summary>The control word the device applied last: every bit of it, those without behaviour
    /// in this slice included (bits 6 to 11 and 13 to 15, 13 to 23 in the 32-bit word; see
    /// <see cref="Cia408ControlWord"/>), which the device stores and hands on here. 0 until a word has
    /// been applied. Any thread.</summary>
    /// <remarks>
    /// <para>
    /// <b>One history.</b> The two control words are one control word: a write to <c>6040h</c> or to
    /// <c>6044h</c> is judged against the word applied before it, whichever object or source that came
    /// from, and the fault-reset edge of Table 7 can be made across them. Neither object is mirrored
    /// into the other: each holds what was written to it. Under local control the source is
    /// <see cref="SetLocalControlWord"/>, and a word from it is judged against the last word applied,
    /// a bus word included; switching the source applies nothing by itself. The norm has one control
    /// word per device and does not say how a second pair or a change of source combines with the
    /// edge; one history is the reading under which a master that keeps to one object and one source
    /// sees exactly Table 7.
    /// </para>
    /// <para>
    /// <b>Consumed, whatever it moved.</b> Every word the device takes becomes the previous word of
    /// the next, also when the machine did nothing with it (NOT_READY, a word that meets no row) and
    /// when the device refused the reset it carried (<see cref="ReportFault"/>). A fault leaves it as
    /// it was, so the word before the fault is the first word of the reset edge.
    /// </para>
    /// </remarks>
    public uint ControlWord => _controlWord;

    /// <summary>A write of a command entry (<see cref="CommandEntries"/>), one call per committed
    /// write and in commit order (issue #557). A control word, from the bus (an SDO download, an
    /// RPDO) or written locally through the dictionary, is applied with the word before it, which
    /// makes the edges of Table 7; under local control (<c>604Fh</c> = 1, §9.3.9) it is ignored, and
    /// not taken as the previous word either. A write of <c>604Fh</c> switches the source and
    /// rewrites the status words, whose bit 4 mirrors it. Actor loop.</summary>
    /// <remarks>The source is followed in the same order as the control words: a control word
    /// committed before a write of <c>604Fh</c> is judged under the source it was written under,
    /// however many writes the node tells in one round.</remarks>
    public void OnCommandWritten(CanOpenObjectAddress entry, byte[] value)
    {
        var host = AttachedHost();
        if (entry.Index == _local)
        {
            _localSource = value[0] == 1;
            Publish(host, errorCodeChanged: false);
            return;
        }

        if (_localSource) return;
        ApplyControlWord(host, Decode(value));
    }

    /// <summary>The control word from the device's local source, used while <c>604Fh</c> is 1
    /// (§9.3.9: "Control word local"). Any thread; applied on the node's actor loop, at once when
    /// called from inside one of the device's callbacks.</summary>
    /// <remarks>Under bus control (<c>604Fh</c> = 0) the word is ignored when it is applied, and not
    /// taken as the previous word; the source is the one in force when the word is applied, not when
    /// this is called. The word is not
    /// written into <c>6040h</c> or <c>6044h</c>, which hold what the bus wrote: §9.3.9 switches
    /// "between control words via CAN and local control".</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="controlWord"/> sets a reserved
    /// bit: 4, 5, 12 or 24 to 31 (§9.3.6, Table 85; <see cref="Cia408ControlWord.Reserved32"/>), which a
    /// bus write would have refused.</exception>
    /// <exception cref="InvalidOperationException">No node has attached the device yet.</exception>
    public void SetLocalControlWord(uint controlWord)
    {
        if ((controlWord & Cia408ControlWord.Reserved32) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(controlWord), controlWord,
                "Bits 4, 5, 12 and 24 to 31 of the control word are reserved, always 0 (CiA 408 §9.3.6, Table 85).");
        }

        var host = RequireHost();
        host.Post(() =>
        {
            if (!_localSource) return;
            ApplyControlWord(host, controlWord);
        });
    }

    /// <summary>Feeds <paramref name="word"/> to the machine with the word before it, unless it is a
    /// fault reset the device refuses (<see cref="ReportFault"/>); then rewrites and transmits the
    /// status words if they changed, and raises the change. Actor loop.</summary>
    private void ApplyControlWord(ICanOpenApplicationHost host, uint word)
    {
        var previous = _controlWord;
        _controlWord = word;

        // A refused reset is refused whole. Exact, not a shortcut: the written word of each reset row
        // carries its own fault state's pattern (Table 7: 1001b for FAULT_DISABLED, 1011b for
        // FAULT_HOLD, 1000b for FAULT_INIT), which meets no level row in that state (D15 needs x001b
        // in FAULT_HOLD, D16 x000b in FAULT_DISABLED, FAULT_INIT has none), so the machine would take
        // the reset and nothing else.
        if (HasActiveCause && _machine.IsFaultReset(previous, word)) return;

        var before = _machine.State;
        var taken = _machine.ApplyControlWord(previous, word);
        Changed(before, taken);
        Publish(host, errorCodeChanged: false);
        FinishChanges(host);
    }

    /// <summary>
    /// The status word as the device's state makes it, the same value for <c>6041h</c> and, widened,
    /// <c>6045h</c>: bits 3..0 (R M H D) the state (§7.6, Table 4;
    /// <see cref="Cia408StateMachine.StatusBits"/>), bit 4 the source of the control word
    /// (<c>604Fh</c>, as followed in commit order), bit 5 whether a warning is active (<see cref="SetWarning"/>). The other bits are
    /// the process model's and are clear in this slice (<see cref="Cia408StatusWord"/>).
    /// </summary>
    /// <remarks>Composed from the device's state, never read back from the dictionary: the status
    /// words are read-only on the bus, and the device is their only writer.</remarks>
    private uint ComposeStatus()
        => Cia408StateMachine.StatusBits(_machine.State)
            | (_localSource ? Cia408StatusWord.Local : 0u)
            | (_warnings.Count > 0 ? Cia408StatusWord.Warning : 0u);

    /// <summary>Rewrites each status word whose value in the dictionary differs from the composed
    /// one (<see cref="ComposeStatus"/>), and asks for one transmission of what changed: both status
    /// words when either was rewritten, and <c>604Eh</c> when <paramref name="errorCodeChanged"/>. The
    /// node sends each TPDO that maps one of them once
    /// (<see cref="ICanOpenApplicationHost.RequestTransmission"/>), in Operational only. Actor
    /// loop.</summary>
    /// <remarks>Compared with the dictionary, not with what the device wrote last, so a value the
    /// node restored there (Reset Node, a stored value) is replaced however the state moved.</remarks>
    private void Publish(ICanOpenApplicationHost host, bool errorCodeChanged)
    {
        var status = ComposeStatus();
        var od = host.ObjectDictionary;
        var status16 = Encode(status, 2);
        var status32 = Encode(status, 4);
        bool changed16 = !Equal(od.ReadRaw(_statusWord16, 0x00), status16);
        bool changed32 = !Equal(od.ReadRaw(_statusWord32, 0x00), status32);
        if (!changed16 && !changed32 && !errorCodeChanged) return;

        var send = new List<CanOpenObjectAddress>(3);
        if (changed16 || changed32)
        {
            if (changed16) WriteOwn(host, _statusWord16, status16);
            if (changed32) WriteOwn(host, _statusWord32, status32);
            send.Add(new CanOpenObjectAddress(_statusWord16, 0x00));
            send.Add(new CanOpenObjectAddress(_statusWord32, 0x00));
        }

        if (errorCodeChanged) send.Add(new CanOpenObjectAddress(_errorCode, 0x00));
        host.RequestTransmission(send);
    }

    private static bool Equal(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i]) return false;
        }

        return true;
    }
}
