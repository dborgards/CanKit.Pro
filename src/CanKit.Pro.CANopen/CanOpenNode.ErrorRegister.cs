using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Pro.CANopen.Emcy;
using CanKit.Pro.CANopen.Nmt;

namespace CanKit.Pro.CANopen;

// FR-CO-011, #379: the error register (1001h) and the EMCY error state of CiA 301 §7.2.7.1.
//
// One rule for every EMCY the node raises itself — RPDO and MPDO length 8210h, MPDO destination
// 8230h, SYNC length 8240h, RPDO timeout 8250h, SRDO length 8210h, and opt-in heartbeat/life-
// guarding timeout 8130h, controller error-passive 8120h, overrun 8110h or recovery 8140h.
// Each is a communication error
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
// An attached device profile (CanOpenNode.Application.cs) reports errors of its own through the
// host: those are the node's part of 1001h too, not the application's, and are kept beside the
// communication errors (NodeErrorBits). A profile error is recorded even while the EMCY is
// switched off, because the register must show it; it ends when the profile clears it, and its
// EMCY and its 0000h go out through the same chain and the same Stopped rule as the rest. Unlike a
// communication error, 1001h shows it from its raise to its clear, not from its EMCY: the raise and
// the clear write the register at once, and every later write takes the profile errors as they
// are then, so an EMCY waiting for its inhibit time neither hides it nor brings it back once it
// ended (ProfileErrorBits, #550). A profile error raised in Initialisation — from a reset's
// callbacks, or while an LSS slave waits for its node-id — is recorded and shown at once, but its
// EMCY is held until the boot-up is ordered and then goes out behind it, with the register of that
// moment (CanOpenNode.Application.cs, ReleaseProfileEmcysAfterBootUp): in Initialisation only the
// boot-up object exists (§7.3.2.2.1). An error the profile clears while its EMCY is still held
// leaves nothing on the bus. The node's
// communication errors raised in Initialisation — the heartbeat-consumer timeout of an LSS slave
// waiting for its node-id; its controller and overrun monitors start only with Pre-Operational —
// are held the same way, in the same list, and go out behind the boot-up with 1001h showing them
// from that EMCY, as every communication error shows from its EMCY. One whose cause ends while it is
// held leaves nothing on the bus, 0000h included (#547).
// A warning a profile sends (host SendWarning, error codes 0001h to 00FFh, the class "error reset or
// no error" of Table 25) is no active error: it sets no bit, writes nothing to 1001h, and its frame
// carries the register as it is when the frame goes out (1003h enters it as any EMCY). It goes
// through the same chain, inhibit time, boot-up hold and reset as a profile error's EMCY. Sent in
// Stopped it is dropped, since Stopped keeps the most recent active EMCY reason (§7.3.2.2.4) and a
// warning is none (#556).
// A reset (transition 5, then 0) ends every communication error and every profile error without
// an EMCY, and drops a profile EMCY that has not gone out: one still held, and one already released
// behind a boot-up but not yet transmitted (a master that resets the node on seeing its boot-up).
//
// The condition the error behaviour of 1029h reacts to — bus-off, heartbeat timeout, life guarding
// — is also what an application that asks for it is told about (PublishCommunicationFailure).
// The operands of that condition are mutated in several files; each mutation is followed by that
// call, ahead of anything that reacts to it, so a profile hears of the failure before the node
// leaves Operational because of it. An NMT reset is the exception: it publishes once, after its
// whole clearing (CanOpenNode.Application.cs, DeferFailurePublishForReset).
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
        ControllerPassive = 8,
        ControllerRecovery = 9,
        ControllerOverrun = 10,
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

    // Last timeout of each still-silent watched producer, also used by optional 1029h.
    // Retained while EMCY is disabled so re-enable can report an active error. Actor loop only.
    private readonly Dictionary<byte, TimeSpan> _heartbeatEmcyTimeouts = new();

    private void NoteHeartbeatForMonitoring(byte producer)
    {
        _heartbeatConsumer.NoteReceived(producer);
        if (!_options.EmitGuardingErrorEmcy && !_options.EnableErrorBehavior) return;
        _heartbeatEmcyTimeouts.Remove(producer);
        PublishCommunicationFailure();
        EndCommunicationError(CommunicationError.HeartbeatTimeout, producer);
    }

    private void ReconcileHeartbeatEmcyWatches(IReadOnlyDictionary<byte, TimeSpan> desired)
    {
        if (!_options.EmitGuardingErrorEmcy && !_options.EnableErrorBehavior) return;
        foreach (var previous in _heartbeatEmcyTimeouts
                     .Where(WatchChanged)
                     .ToList())
        {
            _heartbeatEmcyTimeouts.Remove(previous.Key);
            PublishCommunicationFailure();
            EndCommunicationError(CommunicationError.HeartbeatTimeout, previous.Key);
        }

        bool WatchChanged(KeyValuePair<byte, TimeSpan> previous)
            => !desired.TryGetValue(previous.Key, out var timeout) || timeout != previous.Value;
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
    /// the register was kept. In Initialisation — an LSS slave waiting for its node-id — the EMCY is
    /// held until the boot-up has been ordered and goes out behind it, as a profile's error raised
    /// there does (ReleaseProfileEmcysAfterBootUp, #547): only the boot-up object exists in that
    /// state (CiA 301 v4.2.0 §7.3.2.2.1), and before, the error was used up without any EMCY ever
    /// being sent for it. Actor loop only.</summary>
    private void RaiseCommunicationError(CommunicationError kind, int number, ushort errorCode)
    {
        int key = CommunicationErrorKey(kind, number);
        if (!_emcyValid || !_communicationErrors.Add(key)) return;
        if (_state == NmtState.Initializing)
        {
            (_heldEmcys ??= new()).Add((Slot: null, errorCode, Array.Empty<byte>(), Warning: false, Communication: key));
            return;
        }
        EmitNodeEmcy(errorCode);
    }

    /// <summary>The cause of the error has ended. The last one to end sends <c>0000h</c>. In
    /// Initialisation the error's EMCY is still held: it is dropped instead, and nothing is sent,
    /// <c>0000h</c> included, since the bus never saw the error. Every communication error active in
    /// Initialisation was raised there — a reset ends them all before it enters the state — so none
    /// of them was announced and no <c>0000h</c> is owed.</summary>
    private void EndCommunicationError(CommunicationError kind, int number = 0)
    {
        int key = CommunicationErrorKey(kind, number);
        if (!_communicationErrors.Remove(key)) return;
        if (_state == NmtState.Initializing)
        {
            _heldEmcys?.RemoveAll(held => held.Communication == key);
            return;
        }
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
    // When one of the errors it ends had its EMCY released behind a boot-up, the 0000h takes that
    // EMCY's path: gated, so that a reset that drops the released EMCY before its turn drops the
    // 0000h behind it too, and no 0000h reaches the bus for an error the bus never saw (review of
    // #547). Actor loop only.
    private void EmitErrorReset()
    {
        bool released = _communicationErrorReleased;
        _communicationErrorReleased = false;
        EmitNodeEmcy(ErrorResetCode, profile: released);
    }

    // Whether an error among the active communication errors had its EMCY held in Initialisation and
    // released behind the boot-up (ReleaseProfileEmcysAfterBootUp). Cleared by the 0000h that ends
    // them and by a reset. Actor loop only.
    private bool _communicationErrorReleased;

    // profile: an EMCY that takes the rules of a profile's EMCY — a profile's raise or clear, or the
    // 0000h of communication errors released behind a boot-up (EmitErrorReset). It takes the gated path
    // of the chain (EmitEmcy), so that its turn is decided in the state the node is in then: it waits in
    // Stopped, and a reset drops it, whatever it waited behind (#549, #561). Held in Stopped, it keeps
    // that when it is released (_pendingEmcyIsProfile, EmitPendingEmcy).
    private void EmitNodeEmcy(ushort errorCode, byte[]? manufacturerSpecific = null, bool profile = false)
    {
        // The register byte is filled in when the frame is transmitted (EmitEmcy).
        var msg = new EmcyMessage(_nodeId, errorCode, 0, manufacturerSpecific);
        if (_state == NmtState.Stopped)
        {
            // §7.3.2.2.4, as for the application's EMCY: held, the most recent one goes out after
            // the transition into another NMT state.
            _pendingEmcy = msg;
            _pendingEmcyIsApplications = false;
            _pendingEmcyIsProfile = profile;
            _heldNodeEmcy = true;
            return;
        }
        _ = EmitEmcy(msg, gate: profile);
    }

    // Whether _pendingEmcy takes the rules of a profile's EMCY (EmitNodeEmcy): released, it takes the
    // gated path and waits for a pending boot-up, as it would have outside Stopped (EmitPendingEmcy).
    // Actor loop only.
    private bool _pendingEmcyIsProfile;

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
        bool profile = _pendingEmcyIsProfile;
        _pendingEmcyIsProfile = false;
        // A held node EMCY that the application's replaced still updates 1001h, ahead of it on
        // the chain: with the EMCY switched off meanwhile, the application's sends nothing and
        // writes nothing, and an error that ended in Stopped would be read on (Codex on #417).
        if (applications && _heldNodeEmcy) QueueErrorRegisterWrite(CommunicationErrorBits);
        _heldNodeEmcy = false;
        // A profile's EMCY keeps the rules of every profile EMCY: behind a boot-up whose send is
        // still pending, and on the gated path, so that a reset before its turn drops it (#549, #561).
        if (profile) EmcyChainWaitsForBootUp();
        _ = EmitEmcy(pending, applicationErrorRegister: applications ? pending.ErrorRegister : null, gate: profile);
    }

    /// <summary>NMT reset: transitions 5 and 0 of Figure 37 — "After NMT state INITIALISATION the
    /// CANopen device enters the error free state" (Corrigendum 3 wording; nothing there asks for
    /// an EMCY). The application's bits stay, as <c>1001h</c> is not restored.</summary>
    private void EndCommunicationErrorsForReset()
    {
        // Whether 1001h shows profile errors now: they end here, and so does their showing (below).
        bool profileErrorsShown = _profileErrors.Count > 0;
        _communicationErrors.Clear();
        _communicationErrorReleased = false;
        EndProfileErrorsForReset();
        _heartbeatEmcyTimeouts.Clear();
        InvalidateControllerOverrunMonitoring();
        InvalidateControllerErrorBaseline();
        _heldNodeEmcy = false;
        // 1001h shows a profile error from its raise to its clear, and a reset is a clear (#550): its
        // bits leave the register in this job, before the reset tells the applications and before a
        // master can read the register after the boot-up, not when the step queued below gets its
        // turn behind EMCYs still on the chain (Bugbot on #567). The communication bits and the
        // application's part stay as they are until that step. Never on a node without applications.
        if (profileErrorsShown) UpdateErrorRegister(passed: null, _registerCommunicationBits);
        // What a profile error raised from the reset's callbacks writes before the step below runs.
        _registerCommunicationBits = 0;
        if (_errorRegisterQueued) QueueErrorRegisterWrite(communicationBits: 0);
    }

    // The communication bits of the last write of 1001h on the EMCY chain: what the register shows of
    // the communication errors until the next EMCY step writes it. A profile error's raise or clear
    // writes the register between two steps (ShowProfileErrorsInRegister) and keeps these. Actor
    // loop only.
    private byte _registerCommunicationBits;

    /// <summary>Writes the application's part, as of its place on the EMCY chain, ORed with
    /// <paramref name="communicationBits"/> and the profile errors active then to <c>1001h</c>
    /// without a frame, so an EMCY queued before it cannot overwrite it afterwards.</summary>
    private void QueueErrorRegisterWrite(byte communicationBits)
    {
        _errorRegisterQueued = true;
        _ = AppendToEmcyChain(() =>
        {
            UpdateErrorRegister(passed: null, communicationBits);
            return Task.CompletedTask;
        });
    }

    /// <summary>One write of <c>1001h</c>: the application's part — the register
    /// <paramref name="passed"/> by its EMCY, which replaces a dictionary write before it, else
    /// the part taken so far — ORed with <paramref name="communicationBits"/>, the communication
    /// errors as of the EMCY this step belongs to, and with the profile errors as they are now
    /// (<see cref="ProfileErrorBits"/>, 0 without applications). All of it runs as one dictionary
    /// transaction, so an application write cannot land between the take and the write and be
    /// overwritten while its notice is consumed later for the composed value (Codex and Bugbot on
    /// #417). Returns the application's part it took. Actor loop only.</summary>
    private byte UpdateErrorRegister(byte? passed, byte communicationBits)
    {
        byte application = 0;
        _od.Transaction(() =>
        {
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
            _registerCommunicationBits = communicationBits;
            WriteErrorRegister((byte)(application | communicationBits | ProfileErrorBits));
        });
        return application;
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

    /// <summary>The error register with its reserved bit 6 cleared: CiA 301 v4.2.0 §7.5.2.2, Table 54
    /// fixes it at 0b. What <see cref="SendEmcyAsync"/> and a write of <c>1001h</c> through the
    /// dictionary pass in is cleared here, not refused (#566): a new exception on the path an
    /// application reports its errors through, delivered by a patch release, would suppress the EMCY
    /// it reports, and a gateway that passes a register through would hit it. Refusing it is left to
    /// the next major version. An attached profile's <c>RaiseError</c> refuses the bit since #554:
    /// that host is internal, its callers are this repository's profiles, and a profile that sets
    /// the bit has a bug to be told of.</summary>
    private static byte WithoutReservedErrorRegisterBit(byte register)
        => (byte)(register & ~ReservedErrorRegisterBit);

    /// <summary>Validation of a write of <c>1001h</c>, local or by SDO where a description made it
    /// writable, or a described default: a value with bit 6 set is stored with it cleared
    /// (<see cref="WithoutReservedErrorRegisterBit"/>) and the write counts as accepted. The caller's
    /// array is not changed. Writing thread, under the dictionary's write gate, which the store
    /// below enters again.</summary>
    private OdWriteDecision ClearReservedErrorRegisterBit(byte[] value)
    {
        // 1001h is UNSIGNED8 (§7.5.2.2), and the size was checked before validation.
        if ((value[0] & ReservedErrorRegisterBit) == 0) return OdWriteDecision.Accept;
        _od.WriteRawUnchecked(Co.ErrorRegister, 0x00, new[] { WithoutReservedErrorRegisterBit(value[0]) });
        return OdWriteDecision.Handled;
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
