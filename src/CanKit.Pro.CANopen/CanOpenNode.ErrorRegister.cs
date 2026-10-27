using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Pro.CANopen.Emcy;
using CanKit.Pro.CANopen.Nmt;

namespace CanKit.Pro.CANopen;

// FR-CO-011, #379: the error register (1001h) and the EMCY error state of CiA 301 §7.2.7.1.
//
// One rule for every EMCY the node raises itself — RPDO and MPDO length 8210h, MPDO destination
// 8230h, SYNC length 8240h, RPDO timeout 8250h, SRDO length 8210h, and opt-in heartbeat/life-
// guarding timeout 8130h. Each is a communication error
// (Table 26, 81xxh/82xxh under 80xxh "Monitoring"), so while one is active the register
// carries bit 4, "Communication error", and bit 0, because "The generic error shall be signaled at
// any error situation" (§7.5.2.2, Table 54). The error is active from the EMCY that reports it —
// "transmitted only once per 'error event'" — until its cause ends: a frame that fits, a SYNC of
// the expected length, a target written, a reception after the timeout, or a reconfiguration of
// the PDO or SRDO. When the last one ends, the node transmits error code 0000h with whatever the
// register still holds: transition 4 of Figure 37 ("All errors are repaired ... transmits an
// emergency object with the error code 'reset error / no error'") when that is 0, transition 2
// ("may be transmitted together with the remaining errors in the error register") when the
// application still reports its own. The application's bits are the register its last
// transmitted SendEmcyAsync passed (a cancelled or discarded one changes nothing), or a value it
// wrote to 1001h through the dictionary since; the node ORs its own in and never clears what the
// application set.
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
        HeartbeatTimeout = 6,
        LifeGuardingTimeout = 7,
    }

    // The application's part of 1001h: what its last transmitted SendEmcyAsync passed, or what it
    // (or a device description) put into 1001h through the dictionary since. Actor loop only.
    private byte _applicationErrorRegister;

    // 1 when 1001h was written by someone other than the node since the node last took it; 1 from
    // the start, so the value 1001h was created or described with is the application's. Set from
    // the dictionary's write notification on the writer's thread, taken on the actor.
    private int _applicationWroteErrorRegister = 1;

    // The managed thread id of the node's own write to 1001h while it runs, else 0. The
    // dictionary raises EntryWritten synchronously on the writing thread, under its write gate,
    // so this tells the node's write from any other, whatever the value (Codex on #417).
    private int _errorRegisterWriterThread;

    // The communication errors the node reported and whose cause has not ended. Actor loop only.
    private readonly HashSet<int> _communicationErrors = new();

    // Last timeout of each still-silent watched producer; retained while EMCY is disabled so
    // enabling the producer can report an error that is still active. Actor loop only.
    private readonly Dictionary<byte, TimeSpan> _heartbeatEmcyTimeouts = new();

    private void NoteHeartbeatForMonitoring(byte producer)
    {
        _heartbeatConsumer.NoteReceived(producer);
        if (!_options.EmitGuardingErrorEmcy) return;
        _heartbeatEmcyTimeouts.Remove(producer);
        EndCommunicationError(CommunicationError.HeartbeatTimeout, producer);
    }

    private void ReconcileHeartbeatEmcyWatches(IReadOnlyDictionary<byte, TimeSpan> desired)
    {
        if (!_options.EmitGuardingErrorEmcy) return;
        foreach (var previous in new List<KeyValuePair<byte, TimeSpan>>(_heartbeatEmcyTimeouts))
        {
            if (desired.TryGetValue(previous.Key, out var timeout) && timeout == previous.Value) continue;
            _heartbeatEmcyTimeouts.Remove(previous.Key);
            EndCommunicationError(CommunicationError.HeartbeatTimeout, previous.Key);
        }
    }

    private void ReportActiveGuardingErrors()
    {
        if (!_options.EmitGuardingErrorEmcy || !_emcyValid) return;
        foreach (var producer in _heartbeatEmcyTimeouts.Keys)
            RaiseCommunicationError(CommunicationError.HeartbeatTimeout, producer, 0x8130);
        if (_lifeGuardingOccurred)
            RaiseCommunicationError(CommunicationError.LifeGuardingTimeout, 0, 0x8130);
    }


    // Whether an EMCY was ever queued: from then on, a reset writes the register it leaves
    // behind, in its place on the chain. Before, 1001h holds what the node created it with, or
    // what the application wrote to it. Actor loop only.
    private bool _errorRegisterQueued;

    private static int CommunicationErrorKey(CommunicationError kind, int number = 0) => ((int)kind << 16) | number;

    /// <summary>The node's part of the register: bits 0 and 4 while a communication error is
    /// active. An EMCY carries it ORed with the application's part.</summary>
    private byte CommunicationErrorBits
        => _communicationErrors.Count > 0 ? (byte)(GenericErrorBit | CommunicationErrorBit) : (byte)0;

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

    // With the EMCY switched off — now, or by the time the frame's turn on the chain comes —
    // there is no frame, but the node's EMCY still writes the register (EmitEmcy), so the master
    // does not keep reading an error that has ended.
    private void EmitErrorReset() => EmitNodeEmcy(ErrorResetCode);

    private void EmitNodeEmcy(ushort errorCode)
    {
        // The register byte is filled in when the frame is transmitted (EmitEmcy).
        var msg = new EmcyMessage(_nodeId, errorCode, 0);
        if (_state == NmtState.Stopped)
        {
            // §7.3.2.2.4, as for the application's EMCY: held, the most recent one goes out after
            // the transition into another NMT state.
            _pendingEmcy = msg;
            _pendingEmcyIsApplications = false;
            _heldNodeEmcy = true;
            return;
        }
        _ = EmitEmcy(msg);
    }

    // Whether _pendingEmcy came from SendEmcyAsync: its register is then the application's part,
    // which the node takes when the EMCY is transmitted. Actor loop only.
    private bool _pendingEmcyIsApplications;

    // Whether a node EMCY was held in Stopped since the held EMCY was last released or dropped.
    // An application EMCY that replaces it takes the frame, not the node's register update. Actor
    // loop only.
    private bool _heldNodeEmcy;

    /// <summary>Leaving Stopped (§7.3.2.2.4): the held EMCY goes out with the register of that
    /// moment. With the EMCY switched off meanwhile no frame goes out, but a held error reset of
    /// the node's still takes the ended error out of <c>1001h</c> (EmitEmcy).</summary>
    private void EmitPendingEmcy(EmcyMessage pending)
    {
        bool applications = _pendingEmcyIsApplications;
        _pendingEmcyIsApplications = false;
        // A held node EMCY that the application's replaced still updates 1001h, ahead of it on
        // the chain: with the EMCY switched off meanwhile, the application's sends nothing and
        // writes nothing, and an error that ended in Stopped would be read on (Codex on #417).
        if (applications && _heldNodeEmcy) QueueErrorRegisterWrite(CommunicationErrorBits);
        _heldNodeEmcy = false;
        _ = EmitEmcy(pending, applicationErrorRegister: applications ? pending.ErrorRegister : null);
    }

    /// <summary>NMT reset: transitions 5 and 0 of Figure 37 — "After NMT state INITIALISATION the
    /// CANopen device enters the error free state" (Corrigendum 3 wording; nothing there asks for
    /// an EMCY). The application's bits stay, as <c>1001h</c> is not restored.</summary>
    private void EndCommunicationErrorsForReset()
    {
        _communicationErrors.Clear();
        _heartbeatEmcyTimeouts.Clear();
        _heldNodeEmcy = false;
        if (_errorRegisterQueued) QueueErrorRegisterWrite(nodeBits: 0);
    }

    /// <summary>Writes the application's part, as of its place on the EMCY chain, ORed with
    /// <paramref name="nodeBits"/> to <c>1001h</c> without a frame, so an EMCY queued before it
    /// cannot overwrite it afterwards.</summary>
    private void QueueErrorRegisterWrite(byte nodeBits)
    {
        _errorRegisterQueued = true;
        _ = AppendToEmcyChain(() =>
        {
            UpdateErrorRegister(passed: null, nodeBits);
            return Task.CompletedTask;
        });
    }

    /// <summary>One step of the EMCY chain on <c>1001h</c>: the application's part — the register
    /// <paramref name="passed"/> by its EMCY, which replaces a dictionary write before it, else
    /// the part taken so far — ORed with <paramref name="nodeBits"/> and written. All of it runs
    /// as one dictionary transaction, so an application write cannot land between the take and
    /// the write and be overwritten while its notice is consumed later for the composed value
    /// (Codex and Bugbot on #417). Returns the register written. Actor loop only.</summary>
    private byte UpdateErrorRegister(byte? passed, byte nodeBits)
    {
        byte composed = 0;
        _od.Transaction(() =>
        {
            byte application;
            if (passed is { } register)
            {
                Interlocked.Exchange(ref _applicationWroteErrorRegister, 0);
                application = register;
            }
            else
            {
                application = TakeApplicationErrorRegister();
            }
            _applicationErrorRegister = application;
            composed = (byte)(application | nodeBits);
            WriteErrorRegister(composed);
        });
        return composed;
    }

    /// <summary>The application's part at this step of the EMCY chain. A value in <c>1001h</c>
    /// that the node did not write there is the application's — written through the dictionary,
    /// or given by a device description — and replaces the part its last EMCY passed (review of
    /// #417). Actor loop only.</summary>
    private byte TakeApplicationErrorRegister()
    {
        if (Interlocked.Exchange(ref _applicationWroteErrorRegister, 0) != 0)
            _applicationErrorRegister = (byte)_od.ReadUnsigned(Co.ErrorRegister, 0x00);
        return _applicationErrorRegister;
    }

    private void WriteErrorRegister(byte value)
    {
        Volatile.Write(ref _errorRegisterWriterThread, Environment.CurrentManagedThreadId);
        try
        {
            _od.WriteUnsigned(Co.ErrorRegister, 0x00, value);
        }
        finally
        {
            Volatile.Write(ref _errorRegisterWriterThread, 0);
        }
    }

    /// <summary>EntryWritten, on the writer's thread and under the dictionary's write gate: a write of <c>1001h</c> that is not the
    /// node's own makes the value the application's part at the next step of the EMCY chain.</summary>
    private void NoteErrorRegisterWritten(ushort index, byte subindex)
    {
        // 1001h is a VAR: sub-index 00h is its only entry.
        if (index == Co.ErrorRegister
            && Volatile.Read(ref _errorRegisterWriterThread) != Environment.CurrentManagedThreadId)
            Volatile.Write(ref _applicationWroteErrorRegister, 1);
    }
}
