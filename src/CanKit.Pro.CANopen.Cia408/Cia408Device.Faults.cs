using System;
using System.Collections.Generic;
using CanKit.Pro.CANopen.Application;

namespace CanKit.Pro.CANopen.Cia408;

// Faults and warnings the application reports (CiA 408 v2.2.0 §6, §7.6 Table 4, §7.7 Table 9,
// §8.6.8, §9.3.8).
internal sealed partial class Cia408Device
{
    /// <summary>The bits of the error register an application may not set with a fault: bit 5, "device
    /// profile specific", which CiA 408 v2.2.0 §8.6.3 reserves, and bit 6, reserved by CiA 301 v4.2.0
    /// §7.5.2.2, Table 54.</summary>
    private const byte ReservedErrorRegisterBits = 0x20 | 0x40;

    // The fault causes the application reported and has not cleared, in the order reported, with
    // what each asked for: Reset Node re-enters them (OnReset). Actor loop.
    private readonly List<FaultCause> _faultCauses = new();

    // The warnings the application reported active, by error code, in the order they began. Actor
    // loop.
    private readonly List<ushort> _warnings = new();

    // Whether a communication failure is active (OnCommunicationFailureChanged). Actor loop.
    private bool _communicationFailure;

    /// <summary>Whether a cause of a fault is active, for which the device refuses a fault reset: a
    /// fault the application reported and has not cleared (<see cref="ReportFault"/>), or a
    /// communication failure (<see cref="OnCommunicationFailureChanged"/>). NMT Stopped and Reset
    /// Communication are none: they force the fault when they happen and do not last
    /// (<see cref="OnNmtStateChanged"/>). Actor loop.</summary>
    private bool HasActiveCause => _faultCauses.Count > 0 || _communicationFailure;

    /// <summary>Reports a fault the device detected (CiA 408 v2.2.0 §6.1): raises its EMCY, shows its
    /// code in <c>604Eh</c>, takes the state machine into the fault state
    /// <paramref name="reaction"/> asks for, and then asks the node for the NMT state <c>1029h:02</c>,
    /// internal device error, names. Any thread; applied on the node's actor loop, at once when called
    /// from inside one of the device's callbacks.</summary>
    /// <remarks>
    /// <para>
    /// <b>The order.</b> The EMCY (<see cref="ICanOpenApplicationHost.RaiseError"/>, with
    /// <paramref name="errorRegisterBits"/> and the generic bit); <c>604Eh</c> = the code (§9.3.8: "In
    /// case the device transits to warning state or fault state, the fault occurred shall be
    /// indicated"); D8 and the exit Table 9 allows from the present state, never higher than
    /// <paramref name="reaction"/> (<see cref="Cia408StateMachine.Fault"/>); the status words, with R =
    /// 0, rewritten and transmitted with <c>604Eh</c>; <see cref="StateChanged"/>; last the NMT request,
    /// so that a TPDO carrying the fault state goes out while the node is still Operational, and every
    /// subscriber has seen the fault before the node changes state — also when the fault was reported
    /// from inside a <see cref="StateChanged"/> handler, whose change is raised after the running event
    /// and whose request waits until then.
    /// </para>
    /// <para>
    /// <b>1029h:02.</b> §8.6.8 has the internal device error class "valid when the device is in error
    /// state", and Table 10 (§7.8) lets the fault reaction drive NMT: 00h asks for Pre-operational,
    /// only from Operational; 01h, the default, asks for nothing; 02h asks for Stopped, from any state
    /// but Initialisation (<see cref="ICanOpenApplicationHost.ErrorBehaviorTarget"/>). The request is
    /// made for every fault reported, also when the device was in a fault state already.
    /// </para>
    /// <para>
    /// <b>Several causes.</b> Each error code is a cause of its own, active from this call until
    /// <see cref="ClearFaultCause"/>. Reporting a code that is active already does nothing at all: it
    /// is the same fault, and <see cref="ICanOpenApplicationHost.RaiseError"/> sends no second EMCY for
    /// it either. A further code, while another is active, raises its own EMCY, sets <c>604Eh</c> to
    /// it, and enters the fault reaction again from the fault state the device is in, which Table 9
    /// lets only stay or fall (D8 from FAULT_INIT ends in FAULT_INIT and raises no change).
    /// </para>
    /// <para>
    /// <b>The reset is refused while a cause is active.</b> The norm does not say whether a fault reset
    /// (D10, D11, D14; Table 7) may succeed while the fault it acknowledges persists. The device
    /// refuses it: the master's reset edge is taken as the previous control word (the edge is
    /// consumed, see <see cref="ControlWord"/>) and the state stays. The causes are the faults the
    /// application reported and a communication failure while it lasts (see
    /// <see cref="OnCommunicationFailureChanged"/>). Once every cause is cleared, the
    /// state still stays until the master makes a fresh edge — pattern with R = 0, then with R = 1.
    /// The fault states still fall by level meanwhile (D15, D16), which is no reset.
    /// </para>
    /// <para>
    /// A fault always meets a started machine: <see cref="Attach"/> starts it before it publishes the
    /// host to the application side, and before that this method throws.
    /// </para>
    /// </remarks>
    /// <param name="errorCode">The EMCY error code: one of CiA 301 v4.2.0 Table 25, or of CiA 408
    /// v2.2.0 §6.3, Table 1.</param>
    /// <param name="reaction">The fault state the device's fault reaction ends in, within Table 9.</param>
    /// <param name="errorRegisterBits">The error register bits of the fault (CiA 301 v4.2.0 Table 54)
    /// besides the generic bit, which the node sets with any error.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="errorCode"/> is of the class
    /// <c>00xxh</c>, "error reset or no error" (CiA 301 v4.2.0 §7.2.7.1, Table 25), which names no
    /// fault; <paramref name="reaction"/> is no member of <see cref="Cia408FaultState"/>; or
    /// <paramref name="errorRegisterBits"/> sets bit 5, which §8.6.3 reserves, or bit 6, reserved by
    /// CiA 301.</exception>
    /// <exception cref="InvalidOperationException">No node has attached the device yet.</exception>
    public void ReportFault(ushort errorCode, Cia408FaultState reaction, byte errorRegisterBits = 0)
    {
        if (errorCode <= 0x00FF)
        {
            throw new ArgumentOutOfRangeException(nameof(errorCode), errorCode,
                "00xxh is the class \"error reset or no error\" of CiA 301 Table 25, not a fault.");
        }
        if (reaction is not (Cia408FaultState.FaultInit or Cia408FaultState.FaultDisabled or Cia408FaultState.FaultHold))
            throw new ArgumentOutOfRangeException(nameof(reaction), reaction, "Not a fault state of CiA 408 §7.7, Table 9.");
        if ((errorRegisterBits & ReservedErrorRegisterBits) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(errorRegisterBits), errorRegisterBits,
                "Bit 5 of the error register is reserved by CiA 408 §8.6.3 and bit 6 by CiA 301 §7.5.2.2.");
        }

        var host = RequireHost();
        host.Post(() => ApplyFault(host, errorCode, reaction, errorRegisterBits));
    }

    /// <summary>Actor loop, in a callback of the device: see <see cref="ReportFault"/>.</summary>
    private void ApplyFault(ICanOpenApplicationHost host, ushort errorCode, Cia408FaultState reaction, byte errorRegisterBits)
    {
        if (IndexOfCause(errorCode) >= 0) return;

        var cause = new FaultCause(errorCode, reaction, errorRegisterBits);
        _faultCauses.Add(cause);
        EnterFault(host, cause);
        Publish(host, errorCodeChanged: true);
        _nmtRequestPending = true;
        FinishChanges(host);
    }

    /// <summary>The EMCY of <paramref name="cause"/>, its code in <c>604Eh</c>, and the fault reaction
    /// it asks for, queued as a change. Actor loop: <see cref="ApplyFault"/>, and Reset Node re-entering
    /// a cause that persists (<see cref="OnReset"/>).</summary>
    private void EnterFault(ICanOpenApplicationHost host, FaultCause cause)
    {
        host.RaiseError(cause.Code, cause.ErrorRegisterBits);
        if (_hasErrorCode) WriteOwn(host, _errorCode, CanOpenValueCodec.Encode(OdDataType.Unsigned16, cause.Code));
        var before = _machine.State;
        var taken = _machine.Fault(cause.Reaction);
        Changed(before, taken);
    }

    private int IndexOfCause(ushort errorCode) => _faultCauses.FindIndex(cause => cause.Code == errorCode);

    /// <summary>The cause <paramref name="errorCode"/> reported with <see cref="ReportFault"/> is gone:
    /// its error ends (<see cref="ICanOpenApplicationHost.ClearError"/>, an EMCY <c>0000h</c>). The
    /// state stays until the master resets the fault, which it now can once no other cause is active
    /// (see <see cref="ReportFault"/>). <c>604Eh</c> keeps the code (see <see cref="SetWarning"/>). A
    /// code that is not active does nothing. Any thread; applied on the node's actor loop.</summary>
    /// <exception cref="InvalidOperationException">No node has attached the device yet.</exception>
    public void ClearFaultCause(ushort errorCode)
    {
        var host = RequireHost();
        host.Post(() =>
        {
            int index = IndexOfCause(errorCode);
            if (index < 0) return;
            _faultCauses.RemoveAt(index);
            host.ClearError(errorCode);
        });
    }

    /// <summary>Reports that the warning <paramref name="errorCode"/> began (<see langword="true"/>)
    /// or ended: status bit 5, W, is set while any warning is active (§9.3.3, Table 76), and a warning
    /// that begins shows its code in <c>604Eh</c> (§9.3.8). The state machine does not move. Any
    /// thread; applied on the node's actor loop.</summary>
    /// <remarks>
    /// <para>
    /// A level per code: reporting what is reported already does nothing. When the status word changes
    /// it is rewritten and transmitted, as for a change of state, with <c>604Eh</c> when that changed.
    /// </para>
    /// <para>
    /// <b>No EMCY.</b> The norm makes W a "collective manufacturer-specific warning" whose type a
    /// manufacturer-specific parameter gives, and asks for an EMCY only for errors (§6.1); CiA 301 has
    /// no active warning state to clear (<see cref="ICanOpenApplicationHost.SendWarning"/> is an event
    /// of class 00xxh only). An application that wants one sends it itself.
    /// </para>
    /// <para>
    /// <b>604Eh records what occurred last.</b> §9.3.8 says the object indicates the fault that
    /// occurred when the device enters a warning or fault state, and says nothing about the end of
    /// one. The device writes the code when a warning begins or a fault is reported, and does not
    /// clear it: neither the end of a warning nor <see cref="ClearFaultCause"/> nor a fault reset by the
    /// master changes it, and with several causes it holds the latest. Reset Node gives the object
    /// back as the device powers on (<see cref="OnReset"/>): the code of the last fault it re-enters,
    /// else of the last warning still active, else 0.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="errorCode"/> is <c>0000h</c>,
    /// "no error".</exception>
    /// <exception cref="InvalidOperationException">No node has attached the device yet.</exception>
    public void SetWarning(ushort errorCode, bool active)
    {
        if (errorCode == 0x0000)
            throw new ArgumentOutOfRangeException(nameof(errorCode), errorCode, "0000h is \"no error\", not a warning.");

        var host = RequireHost();
        host.Post(() =>
        {
            bool began = false;
            if (active && !_warnings.Contains(errorCode))
            {
                _warnings.Add(errorCode);
                began = true;
            }
            else if (!active)
            {
                _warnings.Remove(errorCode);
            }

            if (began && _hasErrorCode) WriteOwn(host, _errorCode, CanOpenValueCodec.Encode(OdDataType.Unsigned16, errorCode));
            Publish(host, errorCodeChanged: began);
        });
    }

    /// <summary>A fault the application reported: its code, the reaction it asked for, and its error
    /// register bits.</summary>
    private readonly struct FaultCause
    {
        internal FaultCause(ushort code, Cia408FaultState reaction, byte errorRegisterBits)
        {
            Code = code;
            Reaction = reaction;
            ErrorRegisterBits = errorRegisterBits;
        }

        internal ushort Code { get; }

        internal Cia408FaultState Reaction { get; }

        internal byte ErrorRegisterBits { get; }
    }
}
