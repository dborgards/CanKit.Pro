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

    // The status word as last written to 6041h and 6045h. Actor loop, and Attach.
    private uint _status;

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

    /// <summary>Applies a control word written over the bus (an SDO download, an RPDO) or locally
    /// through the dictionary, one call per committed write and in commit order (issue #557):
    /// with the word before it, it makes the edges of Table 7. Ignored under local control
    /// (<c>604Fh</c> = 1, §9.3.9), and then not taken as the previous word either. Actor loop.</summary>
    /// <remarks>Whether the source is local is read when the device is told of the write, one actor
    /// job after it was committed (<see cref="ICanOpenApplication.OnCommandWritten"/>): a write
    /// committed just before <c>604Fh</c> changed is judged by the new value.</remarks>
    public void OnCommandWritten(CanOpenObjectAddress entry, byte[] value)
    {
        var host = AttachedHost();
        if (IsLocal(host.ObjectDictionary)) return;
        ApplyControlWord(host, Decode(value));
    }

    /// <summary>The control word from the device's local source, used while <c>604Fh</c> is 1
    /// (§9.3.9: "Control word local"). Any thread; applied on the node's actor loop, at once when
    /// called from inside one of the device's callbacks.</summary>
    /// <remarks>Under bus control (<c>604Fh</c> = 0) the word is ignored when it is applied, and not
    /// taken as the previous word; the source is read then, not when this is called. The word is not
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
            if (!IsLocal(host.ObjectDictionary)) return;
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
        if (_faultCauses.Count > 0 && _machine.IsFaultReset(previous, word)) return;

        var before = _machine.State;
        var taken = _machine.ApplyControlWord(previous, word);
        Changed(before, taken);
        Publish(host, errorCodeChanged: false);
        ThrowIfAny(RaisePendingChanges());
    }

    /// <summary>Whether the control word comes from the local source: <c>604Fh</c> = 1 (§9.3.9).</summary>
    private bool IsLocal(ObjectDictionary od) => od.ReadRaw(_local, 0x00)[0] == 1;

    /// <summary>
    /// The status word as the device's state makes it, the same value for <c>6041h</c> and, widened,
    /// <c>6045h</c>: bits 3..0 (R M H D) the state (§7.6, Table 4;
    /// <see cref="Cia408StateMachine.StatusBits"/>), bit 4 the source of the control word
    /// (<c>604Fh</c>), bit 5 whether a warning is active (<see cref="SetWarning"/>). The other bits are
    /// the process model's and are clear in this slice (<see cref="Cia408StatusWord"/>).
    /// </summary>
    /// <remarks>Composed from the device's state, never read back from the dictionary: the status
    /// words are read-only on the bus, and the device is their only writer.</remarks>
    private uint ComposeStatus(ObjectDictionary od)
        => Cia408StateMachine.StatusBits(_machine.State)
            | (IsLocal(od) ? Cia408StatusWord.Local : 0u)
            | (_warnings.Count > 0 ? Cia408StatusWord.Warning : 0u);

    /// <summary>Rewrites both status words when the composed value differs from what they hold, and
    /// asks for one transmission of what changed: the status words, and <c>604Eh</c> when
    /// <paramref name="errorCodeChanged"/>. The node sends each TPDO that maps one of them once
    /// (<see cref="ICanOpenApplicationHost.RequestTransmission"/>), in Operational only. Actor
    /// loop.</summary>
    private void Publish(ICanOpenApplicationHost host, bool errorCodeChanged)
    {
        var status = ComposeStatus(host.ObjectDictionary);
        bool statusChanged = status != _status;
        if (!statusChanged && !errorCodeChanged) return;

        var send = new List<CanOpenObjectAddress>(3);
        if (statusChanged)
        {
            _status = status;
            WriteOwn(host, _statusWord16, Encode(status, 2));
            WriteOwn(host, _statusWord32, Encode(status, 4));
            send.Add(new CanOpenObjectAddress(_statusWord16, 0x00));
            send.Add(new CanOpenObjectAddress(_statusWord32, 0x00));
        }

        if (errorCodeChanged) send.Add(new CanOpenObjectAddress(_errorCode, 0x00));
        host.RequestTransmission(send);
    }
}
