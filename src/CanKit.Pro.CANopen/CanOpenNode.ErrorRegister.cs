using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Pro.CANopen.Emcy;
using CanKit.Pro.CANopen.Nmt;

namespace CanKit.Pro.CANopen;

// FR-CO-011, #379: the error register (1001h) and the EMCY error state of CiA 301 §7.2.7.1.
//
// One rule for every EMCY the node raises itself — RPDO and MPDO length 8210h, MPDO destination
// 8230h, SYNC length 8240h, RPDO timeout 8250h, SRDO length 8210h. Each is a communication error
// (Table 26, 82xxh "Protocol error" under 80xxh "Monitoring"), so while one is active the register
// carries bit 4, "Communication error", and bit 0, because "The generic error shall be signaled at
// any error situation" (§7.5.2.2, Table 54). The error is active from the EMCY that reports it —
// "transmitted only once per 'error event'" — until its cause ends: a frame that fits, a SYNC of
// the expected length, a target written, a reception after the timeout, or a reconfiguration of
// the PDO or SRDO. When the last one ends, the node transmits error code 0000h with whatever the
// register still holds: transition 4 of Figure 37 ("All errors are repaired ... transmits an
// emergency object with the error code 'reset error / no error'") when that is 0, transition 2
// ("may be transmitted together with the remaining errors in the error register") when the
// application still reports its own. The application's bits are the register its last
// transmitted SendEmcyAsync passed (a cancelled or discarded one changes nothing); the node ORs
// its own in and never clears what the application set.
// A reset (transition 5, then 0) ends every communication error without an EMCY.
internal sealed partial class CanOpenNode
{
    private const byte GenericErrorBit = 0x01;
    private const byte CommunicationErrorBit = 0x10;
    private const ushort ErrorResetCode = 0x0000;

    /// <summary>The kinds of communication error the node detects. With the PDO or SRDO number
    /// they key <see cref="_communicationErrors"/>.</summary>
    private enum CommunicationError
    {
        PdoLength = 1,
        MpdoTarget = 2,
        RpdoTimeout = 3,
        SyncLength = 4,
        SrdoLength = 5,
    }

    // The register the application's last SendEmcyAsync passed. Actor loop only.
    private byte _applicationErrorRegister;

    // The communication errors the node reported and whose cause has not ended. Actor loop only.
    private readonly HashSet<int> _communicationErrors = new();

    // The register the last queued EMCY or register write carries, null before the first: once
    // one was queued, a reset and a held EMCY discarded with the EMCY off write the register they
    // leave behind, in their place on the chain. Actor loop only.
    private byte? _queuedErrorRegister;

    private static int CommunicationErrorKey(CommunicationError kind, int number = 0) => ((int)kind << 16) | number;

    /// <summary>The register an EMCY decided now carries: the application's bits, and bits 0
    /// and 4 while a communication error is active.</summary>
    private byte ComposeErrorRegister() => ComposeErrorRegister(_applicationErrorRegister);

    private byte ComposeErrorRegister(byte applicationErrorRegister)
        => (byte)(applicationErrorRegister
                  | (_communicationErrors.Count > 0 ? GenericErrorBit | CommunicationErrorBit : 0));

    /// <summary>Reports a communication error once per error event. Nothing is raised, and the
    /// event is not used up, while the EMCY is switched off (bit 31 of <c>1014h</c>), as before
    /// the register was kept. Actor loop only.</summary>
    private void RaiseCommunicationError(CommunicationError kind, int number, ushort errorCode)
    {
        if (!_emcyValid || !_communicationErrors.Add(CommunicationErrorKey(kind, number))) return;
        EmitNodeEmcy(errorCode);
    }

    /// <summary>The cause of the error has ended. The last one to end sends <c>0000h</c>.</summary>
    private void EndCommunicationError(CommunicationError kind, int number = 0)
    {
        if (!_communicationErrors.Remove(CommunicationErrorKey(kind, number))) return;
        if (_communicationErrors.Count == 0) EmitErrorReset();
    }

    /// <summary>Every error of an RPDO ends with its reconfiguration, with one <c>0000h</c> at most.</summary>
    private void EndRpdoCommunicationErrors(int pdoIndex)
    {
        bool removed = _communicationErrors.Remove(CommunicationErrorKey(CommunicationError.PdoLength, pdoIndex));
        removed |= _communicationErrors.Remove(CommunicationErrorKey(CommunicationError.MpdoTarget, pdoIndex));
        removed |= _communicationErrors.Remove(CommunicationErrorKey(CommunicationError.RpdoTimeout, pdoIndex));
        if (removed && _communicationErrors.Count == 0) EmitErrorReset();
    }

    private void EmitErrorReset()
    {
        // With the EMCY switched off there is no frame to carry the register, but the master
        // must not keep reading an error that has ended.
        if (!_emcyValid)
        {
            QueueErrorRegisterWrite(ComposeErrorRegister());
            return;
        }
        EmitNodeEmcy(ErrorResetCode);
    }

    private void EmitNodeEmcy(ushort errorCode)
    {
        var msg = new EmcyMessage(_nodeId, errorCode, ComposeErrorRegister());
        if (_state == NmtState.Stopped)
        {
            // §7.3.2.2.4, as for the application's EMCY: held, the most recent one goes out after
            // the transition into another NMT state.
            _pendingEmcy = msg;
            _pendingEmcyIsApplications = false;
            return;
        }
        _ = EmitEmcy(msg);
    }

    // Whether _pendingEmcy came from SendEmcyAsync: its register is then the application's part,
    // which the node takes when the EMCY is transmitted. Actor loop only.
    private bool _pendingEmcyIsApplications;

    /// <summary>An EMCY carries the register of the moment it is decided: the application's
    /// part it brings (or the one already taken) and the node's bits.</summary>
    private EmcyMessage WithCurrentErrorRegister(EmcyMessage msg, byte applicationErrorRegister)
        => new(msg.ProducerNodeId, msg.ErrorCode, ComposeErrorRegister(applicationErrorRegister), msg.ManufacturerSpecific);

    /// <summary>Leaving Stopped (§7.3.2.2.4): the held EMCY goes out with the register of that
    /// moment. With the EMCY switched off meanwhile it is discarded, but an error that ended
    /// while it was held must still leave <c>1001h</c>.</summary>
    private void EmitPendingEmcy(EmcyMessage pending)
    {
        bool applications = _pendingEmcyIsApplications;
        _pendingEmcyIsApplications = false;
        if (!_emcyValid)
        {
            if (_queuedErrorRegister is not null) QueueErrorRegisterWrite(ComposeErrorRegister());
            return;
        }
        byte application = applications ? pending.ErrorRegister : _applicationErrorRegister;
        _ = EmitEmcy(WithCurrentErrorRegister(pending, application),
            applicationErrorRegister: applications ? application : null);
    }

    /// <summary>NMT reset: transitions 5 and 0 of Figure 37 — the node is error free, "No error
    /// message is sent". The application's bits stay, as <c>1001h</c> is not restored.</summary>
    private void EndCommunicationErrorsForReset()
    {
        _communicationErrors.Clear();
        if (_queuedErrorRegister is not null) QueueErrorRegisterWrite(_applicationErrorRegister);
    }

    /// <summary>Writes <c>1001h</c> without a frame, in its place on the EMCY chain, so an EMCY
    /// queued before it cannot overwrite it afterwards.</summary>
    private void QueueErrorRegisterWrite(byte errorRegister)
    {
        _queuedErrorRegister = errorRegister;
        var link = _emcySendChain.ContinueWith(
            _ => Volatile.Read(ref _disposed) != 0 ? Task.CompletedTask : DispatchQueuedEmcy(() =>
            {
                _od.WriteUnsigned(Co.ErrorRegister, 0x00, errorRegister);
                return Task.CompletedTask;
            }),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        _emcySendChain = link;
    }
}
