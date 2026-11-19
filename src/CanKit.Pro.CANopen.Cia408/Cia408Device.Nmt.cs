using CanKit.Pro.CANopen.Application;
using CanKit.Pro.CANopen.Nmt;

namespace CanKit.Pro.CANopen.Cia408;

// The coupling of the device state machine to NMT (CiA 408 v2.2.0 §7.8, Table 10, Figure 2; §7.6,
// Table 6).
internal sealed partial class Cia408Device
{
    // A fault asked for the 1029h:02 state while the node was in Initialisation, where the node takes
    // no request — a reset raised a fault the application still reports, or a fault was reported
    // there: the request waits until the node leaves Initialisation. Actor loop.
    private bool _requestOnBoot;

    /// <summary>
    /// NMT Stopped forces the fault reaction (§7.8, Table 10: C5 and C8 force D8), without an EMCY.
    /// Leaving Initialisation after a fault asked for the NMT state <c>1029h:02</c> names while the
    /// node was there makes that request now (see <see cref="OnReset"/>). Every other change moves
    /// nothing. Actor loop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>From which device states Table 10 applies.</b> §7.8 introduces Table 10 with "if the device
    /// state machine is in mode DEVICE_MODE_ACTIVE, transitions in the communication state machine force
    /// the reactions" — a state the device state machine does not have — while Figure 2 joins every
    /// state but NOT_READY to the D8 line, and the rows of Table 10 name D8 without a state. The figure
    /// is taken: Stopped, Reset Communication and a communication failure force D8 from every resting
    /// state, and Reset Node restarts the machine from every state. The norm's sentence is read as
    /// meaning "while the device state machine runs".
    /// </para>
    /// <para>
    /// <b>The forced fault.</b> Table 10 forces D8 and names no error, so the device sends no EMCY,
    /// leaves <c>604Eh</c> as it is, and asks NMT for nothing — the event is NMT's own. The fault state
    /// is the one Table 9 bounds the reaction by from the present state, the least change that leaves
    /// the device safe: FAULT_HOLD from HOLD and ACTIVE (D9), FAULT_DISABLED from DISABLED (D12),
    /// FAULT_INIT from INIT (D13); in a fault state the device stays where it is and raises no change.
    /// The same holds for Reset Communication (<see cref="OnReset"/>) and a communication failure
    /// (<see cref="OnCommunicationFailureChanged"/>).
    /// </para>
    /// <para>
    /// <b>When the reset edge counts again.</b> Stopped and Reset Communication are events, not
    /// causes: once they have forced the fault, a fault reset (Table 7) is accepted as soon as no cause
    /// is active (<see cref="ReportFault"/>) — in practice once the master has started the node again
    /// and writes the edge, or at once from the local source. A communication failure is a cause while
    /// it lasts.
    /// </para>
    /// </remarks>
    public void OnNmtStateChanged(NmtState previous, NmtState current)
    {
        var host = AttachedHost();
        if (current == NmtState.Stopped)
        {
            ForceFault(host);
            return;
        }

        // Asked from here, the node takes the request once this round of notifications is told.
        if (previous == NmtState.Initializing && _requestOnBoot)
        {
            _requestOnBoot = false;
            _nmtRequestPending = true;
            FinishChanges(host);
        }
    }

    /// <summary>
    /// Reset Communication forces the fault reaction, as Stopped does (see
    /// <see cref="OnNmtStateChanged"/>; §7.8, Table 10: C12 to C14 force D8). Reset Node restarts the
    /// machine (D255, D0, D1 into INIT; §7.6, Table 6), gives the device's objects back as the device
    /// powers on, and enters again every fault the application reported and has not cleared. Actor
    /// loop, in Initialisation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Reset Node: Table 6 over Table 10 and Figure 2.</b> Table 10 sends a reset of the application
    /// "to INIT" in a row labelled D13, and Figure 2 draws it into FAULT_INIT; Table 6 has the reset
    /// application leave the machine (D255) and start it again (D0, D1). Table 6 is taken
    /// (<see cref="Cia408StateMachine.Restart"/>): the device comes up as after power on, in INIT. The
    /// restart is raised on <see cref="StateChanged"/> also from INIT, since the machine was left.
    /// </para>
    /// <para>
    /// <b>The objects after Reset Node</b> (the plan's Review Focus 5). The node has restored the
    /// device's objects to their power-on values, or to the values <c>1010h</c> stored, before this
    /// call. The parameters keep what the node restored: <c>6042h</c>, <c>6043h</c> and <c>604Fh</c>,
    /// whose stored value becomes the source of the control word. What shows the device's state is
    /// written again from it, so a stored value never shows: both control words 0, as at power on,
    /// and the history of the control word starts afresh (<see cref="ControlWord"/>); <c>604Eh</c> the
    /// code of the last fault entered again, else of the last warning still active, else 0; the status
    /// words composed from the state (<see cref="ComposeStatus"/>); the actual values from what the
    /// device holds, the values the model or the application set last. The setpoints are parameters
    /// and keep what the node restored, a default or a stored value.
    /// </para>
    /// <para>
    /// <b>Faults that persist.</b> The node has ended every error of the application, without an EMCY
    /// (<see cref="ICanOpenApplication.OnReset"/>). A fault the application reported and has not
    /// cleared is its statement that the cause still exists, so the device enters it again, in the
    /// order reported: its EMCY again (held until the boot-up, then sent), its code in
    /// <c>604Eh</c>, and its fault reaction from INIT, which Table 9 bounds to FAULT_INIT. The
    /// <c>1029h:02</c> request waits until the node has left Initialisation (see
    /// <see cref="OnNmtStateChanged"/>). Active warnings stay active, as the application reported
    /// them.
    /// </para>
    /// <para>
    /// <b>Reset Communication</b> restores <c>1000h</c> to <c>1FFFh</c> only: the device's objects, the
    /// history of the control word and <c>604Eh</c> stay as they are. It ends every error of the
    /// application as Reset Node does, so a fault the application still reports raises its EMCY again
    /// (held until the boot-up), and its <c>1029h:02</c> request is made once the node has left
    /// Initialisation; the state stays where the forced D8 left it, which is never above the cause's
    /// reaction, since a fault reset is refused while the cause lasts. A control word committed before
    /// the reset and not yet told reaches the device after this call, in the fault state the reset
    /// forced, and is judged by Table 7 there like any other — so a reset edge whose two words were both
    /// written before the reset (<c>0003h</c> then <c>000Bh</c> in HOLD, say) acknowledges the forced
    /// D8, as an edge may run across a fault (see <see cref="ControlWord"/>).
    /// </para>
    /// </remarks>
    public void OnReset(NmtCommand command)
    {
        var host = AttachedHost();
        if (command == NmtCommand.ResetNode)
        {
            Restart(host);
            return;
        }

        ForceFault(host);
        foreach (var cause in _faultCauses) host.RaiseError(cause.Code, cause.ErrorRegisterBits);
        _requestOnBoot |= _faultCauses.Count > 0;
    }

    /// <summary>A communication failure began (<see langword="true"/>) or ended. When it begins it
    /// forces the fault reaction, as Stopped does (see <see cref="OnNmtStateChanged"/>), without an
    /// EMCY of the device's own; while it lasts it is a cause for which a fault reset is refused
    /// (<see cref="ReportFault"/>). When it ends the state stays until the master resets the fault. The
    /// node acts on <c>1029h:01</c> itself, after this call. Actor loop.</summary>
    /// <remarks>The norm does not tie a communication failure to the device state machine: Table 10
    /// names NMT transitions only. Treating the loss of the master's heartbeat or guarding like the
    /// Stopped it often leads to is this device's reading — a valve that no longer hears its master
    /// should not keep following the last setpoint — and so is keeping the fault reset refused while
    /// the failure lasts.</remarks>
    public void OnCommunicationFailureChanged(bool active)
    {
        var host = AttachedHost();
        _communicationFailure = active;
        if (active) ForceFault(host);
    }

    /// <summary>D8 and the highest fault state Table 9 allows from the present state, without an EMCY,
    /// <c>604Eh</c> or an NMT request; the status rewritten and the change raised. Asking for
    /// FAULT_HOLD gives exactly that, since <see cref="Cia408StateMachine.Fault"/> lowers a request to
    /// what Table 9 allows. Actor loop.</summary>
    private void ForceFault(ICanOpenApplicationHost host)
    {
        var before = _machine.State;
        var taken = _machine.Fault(Cia408FaultState.FaultHold);
        Changed(before, taken);
        Publish(host, errorCodeChanged: false);
        FinishChanges(host);
    }

    /// <summary>Reset Node: see <see cref="OnReset"/>. Actor loop, in Initialisation.</summary>
    private void Restart(ICanOpenApplicationHost host)
    {
        var persisting = _faultCauses.ToArray();
        var before = _machine.State;
        var taken = _machine.Restart();
        Changed(before, taken, evenWithoutMove: true);

        _controlWord = 0;
        WriteOwn(host, _controlWord16, Encode(0, 2));
        WriteOwn(host, _controlWord32, Encode(0, 4));
        _localSource = host.ObjectDictionary.ReadRaw(_local, 0x00)[0] == 1;
        ushort errorCode = _warnings.Count > 0 ? _warnings[_warnings.Count - 1] : (ushort)0;
        WriteOwn(host, _errorCode, Encode(errorCode, 2));

        RewriteActualValues(host);

        // The restart is published and raised on its own, so its subscribers read INIT; the faults that
        // persist follow as changes of their own.
        Publish(host, errorCodeChanged: false);
        FinishChanges(host);

        if (persisting.Length == 0) return;
        foreach (var cause in persisting) EnterFault(host, cause);
        _requestOnBoot = true;
        Publish(host, errorCodeChanged: false);
        FinishChanges(host);
    }
}
