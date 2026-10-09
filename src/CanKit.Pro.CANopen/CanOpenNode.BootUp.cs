using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Core.Exceptions;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Safety;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.Reliability;

namespace CanKit.Pro.CANopen;

/// <summary>
/// Boot-up of the network list once this node is the active NMT master (CiA 302-2 version 4.1.0).
/// The reading of <c>1F80h</c>, <c>1F81h</c>, <c>1F82h</c> and <c>1F89h</c> follows the open
/// implementation that cites that edition. Configuration of a slave (identity <c>1F84h</c> to
/// <c>1F88h</c>, concise DCF) is not part of it; the package README says what is left open.
/// </summary>
internal sealed partial class CanOpenNode
{
    // 1F82h is written with the NMT state byte, not with the command specifier.
    private const byte RequestStopped = 0x04;
    private const byte RequestOperational = 0x05;
    private const byte RequestResetNode = 0x06;
    private const byte RequestResetCommunication = 0x07;
    private const byte RequestPreOperational = 0x7F;
    private const byte RequestAllNodes = 0x80;

    private readonly bool[] _slaveSeen = new bool[CanOpenCobId.MaxNodeId + 1];
    private readonly bool[] _slaveStarted = new bool[CanOpenCobId.MaxNodeId + 1];
    // 1F80h bit 1: the moment the simultaneous start happens has passed — every mandatory slave
    // seen and no verification running. Without a safety slave that is one Start to node 0;
    // with one, every seen slave is started on its own then (see TryFinishBoot).
    private bool _bootStartMoment;
    private bool _bootHalted;
    private bool _bootSelfStarted;
    private IDeadline? _bootDeadline;

    // CiA DSP 304 §8.3.1 step D. A slave whose bound DCF declares SRDOs is verified before it is
    // started; the verification is SDO traffic and runs off the actor, its result is posted back.
    private readonly bool[] _slaveVerifying = new bool[CanOpenCobId.MaxNodeId + 1];
    private readonly bool[] _slaveVerified = new bool[CanOpenCobId.MaxNodeId + 1];
    private CancellationTokenSource? _slaveVerificationCts;

    // Counts the boots: CancelBootUp moves it on. A verification carries the value it began
    // under, so a result that was already posted when its boot was cancelled is not taken for
    // the verification a newer boot started for the same slave.
    private int _bootGeneration;

    // NMT the master sends, in the order it was asked for. Each frame waits for the previous
    // send to finish, so a simultaneous Start cannot pass the Reset Communication that was
    // queued ahead of it. Only the tail is kept, so the chain does not retain every frame.
    private Task _nmtOrder = Task.CompletedTask;

    /// <summary>
    /// Takes the network list. Runs only while this node is the active master, and again when
    /// <c>1F80h</c>, <c>1F81h</c> or <c>1F89h</c> change in that role. A slave that is not
    /// keep-alive is reset individually. A broadcast Reset is not used: the cold election
    /// already broadcast one, and the active master does not apply a broadcast reset to itself.
    /// </summary>
    private void BeginBootUp()
    {
        CancelBootUp();

        if ((ReadStartup() & NmtSuppressSlaveStartBit) == 0)
        {
            for (byte id = 1; id <= CanOpenCobId.MaxNodeId; id++)
            {
                if (!IsAssignedSlave(id) || KeepsAlive(id)) continue;
                SendNmt(NmtCommand.ResetCommunication, id);
            }
        }

        // 1F89h is UNSIGNED32. A signed cast would skip the deadline for every value from
        // 0x80000000 upward, and a mandatory slave would then stay unseen.
        ArmBootDeadline();
        TryFinishBoot();
    }

    private void ArmBootDeadline()
    {
        _bootDeadline?.Dispose();
        _bootDeadline = null;
        uint bootMs = _od.ReadUnsigned(Co.BootTime, 0x00);
        if (bootMs == 0 || !HasMandatorySlave()) return;
        // Cancelling disposes the deadline, and the actor does not run a disposed timer, so this
        // callback is only the one just armed.
        _bootDeadline = _deadlines.Arm(TimeSpan.FromMilliseconds(bootMs), OnBootTimeout);
    }

    private void CancelBootUp()
    {
        _bootDeadline?.Dispose();
        _bootDeadline = null;
        Array.Clear(_slaveSeen, 0, _slaveSeen.Length);
        Array.Clear(_slaveStarted, 0, _slaveStarted.Length);
        _bootStartMoment = false;
        _bootHalted = false;
        _bootSelfStarted = false;
        Array.Clear(_slaveVerifying, 0, _slaveVerifying.Length);
        Array.Clear(_slaveVerified, 0, _slaveVerified.Length);
        _slaveVerificationCts?.Cancel();
        _slaveVerificationCts?.Dispose();
        _slaveVerificationCts = null;
        _bootGeneration++;
    }

    private void NoteSlaveNmtState(byte nodeId, byte state)
    {
        if (_flyingMasterRole != FlyingMasterRole.Active || _disposed != 0) return;
        if (nodeId == _nodeId || !IsAssignedSlave(nodeId)) return;

        // The announcement is kept while a forced Reset Communication is still unconfirmed.
        // Dropping it would leave a mandatory slave unseen, and 1F89h would then reset a node
        // that already checked in. A start, or finishing boot, still waits: that command must
        // not go out behind the reset.
        _od.WriteRawUnchecked(Co.RequestNmt, nodeId, new[] { state });
        _slaveSeen[nodeId] = true;
        if (_coldResetPending) return;

        ConsiderStart(nodeId, state);
        TryFinishBoot();
    }

    /// <summary>
    /// Starts slaves that announced while a forced reset was still unconfirmed. The failed send
    /// does not reset them; the boot record from the hold is what they are started from.
    /// </summary>
    private void ResumeHeldBoot()
    {
        for (byte id = 1; id <= CanOpenCobId.MaxNodeId; id++)
        {
            if (!_slaveSeen[id]) continue;
            byte state = (byte)_od.ReadUnsigned(Co.RequestNmt, id);
            ConsiderStart(id, state);
        }
        TryFinishBoot();
    }

    private void ConsiderStart(byte nodeId, byte state)
    {
        if (_bootHalted || _slaveStarted[nodeId]) return;
        uint startup = ReadStartup();
        if ((startup & NmtSuppressSlaveStartBit) != 0) return;
        if ((Assignment(nodeId) & (SlaveAssignedBit | SlaveBootBit)) != (SlaveAssignedBit | SlaveBootBit)) return;

        // Already operational: nothing to send. Boot-up, pre-operational and stopped are the
        // states from which the master starts the slave.
        if (state == RequestOperational)
        {
            _slaveStarted[nodeId] = true;
            return;
        }
        if (state is not (0x00 or RequestStopped or RequestPreOperational)) return;

        // Step D before step E: a safety slave is started only once its configuration verified.
        // A safety slave that is already Operational when first seen — because it was running
        // before this master took over, or is keep-alive — is not started by this master
        // (returned above) and is therefore not verified; step D is "before NMT Start". This
        // master never starts one unseen: with a safety slave assigned it sends no Start to node 0.
        if (!_slaveVerified[nodeId])
        {
            if (_slaveVerifying[nodeId]) return;
            if (SafetyExpectationOf(nodeId) is { } expected)
            {
                BeginSlaveVerification(nodeId, expected);
                return;
            }
        }

        // Bit 1 waits for the start moment, and only when this node may enter Operational too.
        // Self-start is applied locally; the active master does not take the broadcast as its own.
        bool simultaneous = (startup & NmtStartAllNodesBit) != 0 && (startup & NmtSuppressSelfStartBit) == 0;
        if (simultaneous && !_bootStartMoment) return;

        _slaveStarted[nodeId] = true;
        SendNmt(NmtCommand.Start, nodeId);
    }

    private void TryFinishBoot()
    {
        if (_bootHalted || _disposed != 0) return;
        if (HasUnseenMandatorySlave()) return;

        uint startup = ReadStartup();
        bool mayStartSlaves = (startup & NmtSuppressSlaveStartBit) == 0;
        bool simultaneous = mayStartSlaves
            && (startup & NmtStartAllNodesBit) != 0
            && (startup & NmtSuppressSelfStartBit) == 0;
        if (simultaneous && !_bootStartMoment && HasBootableSlave() && !AnySlaveVerifying())
        {
            _bootStartMoment = true;
            if (HasAssignedSafetySlave())
            {
                // CiA DSP 304 §8.3.1 step D is "before NMT Start" for every safety slave, and a
                // Start to node 0 reaches one that has not announced yet — keep-alive, running
                // before this master took over, or with its boot-up still in flight — unverified.
                // Every seen slave is started on its own instead, a safety slave only once
                // verified; one announcing later is started on its own as it announces.
                for (byte id = 1; id <= CanOpenCobId.MaxNodeId; id++)
                {
                    if (_slaveSeen[id]) ConsiderStart(id, (byte)_od.ReadUnsigned(Co.RequestNmt, id));
                }
            }
            else
            {
                SendNmt(NmtCommand.Start, 0);
            }
        }

        if ((startup & NmtSuppressSelfStartBit) == 0 && !_bootSelfStarted && !AnySlaveVerifying())
        {
            _bootSelfStarted = true;
            if (_state != NmtState.Operational)
                ApplyNmtTransition(NmtState.Operational);
        }
    }

    private void OnBootTimeout()
    {
        // A halt already applied its reaction (a failed safety verification, say). The network is
        // not started past it, so a second stop-all or reset-all would only repeat it.
        if (_bootHalted) return;
        if (_coldResetPending)
        {
            // This tick landed in the wait. Arm the same timeout again instead of commanding
            // slaves while the broadcast reset is still held.
            ArmBootDeadline();
            return;
        }
        if (!HasUnseenMandatorySlave())
        {
            TryFinishBoot();
            return;
        }

        var unseenMandatory = new List<byte>();
        for (byte id = 1; id <= CanOpenCobId.MaxNodeId; id++)
        {
            if (IsMandatory(id) && !_slaveSeen[id]) unseenMandatory.Add(id);
        }
        ApplyBootErrorReaction(unseenMandatory, FlyingMasterSignal.SlaveBootTimeout);
    }

    /// <summary>The 1F80h bit 6 / bit 4 reaction, else Reset Node to the failing slave; then the
    /// signal. Used by the boot timeout (SlaveBootTimeout for each unseen mandatory slave) and by
    /// a failed safety verification (SlaveSafetyConfigurationInvalid).</summary>
    private void ApplyBootErrorReaction(IEnumerable<byte> failedSlaves, FlyingMasterSignal signal)
    {
        _bootHalted = true;
        // One reaction per boot: a halt from a failed verification must not be followed by the
        // boot timeout's own for a mandatory slave that is still unseen.
        _bootDeadline?.Dispose();
        _bootDeadline = null;
        uint startup = ReadStartup();
        bool stopAll = (startup & NmtStopAllOnErrorBit) != 0;
        bool resetAll = !stopAll && (startup & NmtResetAllOnErrorBit) != 0;
        if (stopAll || resetAll)
        {
            var command = stopAll ? NmtCommand.Stop : NmtCommand.ResetNode;
            for (byte id = 1; id <= CanOpenCobId.MaxNodeId; id++)
            {
                if (IsAssignedSlave(id)) SendNmt(command, id);
            }
        }
        foreach (var id in failedSlaves)
        {
            if (!stopAll && !resetAll) SendNmt(NmtCommand.ResetNode, id);
            RaiseFlyingMaster(signal, id, null);
        }
    }

    /// <summary>The expectation of a safety slave: what its bound DCF says, when that file
    /// declares at least one SRDO (spec decision 4). An EDS has no parameter values and never
    /// makes a safety slave. Building it runs the device loader over a dictionary of its own, so
    /// it is kept per slave and per bound description instance. Actor only.</summary>
    private PeerSafetyConfiguration? SafetyExpectationOf(byte nodeId)
    {
        if (!_peerDescriptions.TryGetValue(nodeId, out var description)) return null;
        if (_safetyExpectations.TryGetValue(nodeId, out var kept) && ReferenceEquals(kept.Description, description)) return kept.Expected;
        PeerSafetyConfiguration? expected = null;
        if (description.IsConfigurationFile)
        {
            var built = PeerSafetyConfiguration.FromDeviceDescription(description, nodeId);
            if (built.DeclaresAnySrdo) expected = built;
        }
        _safetyExpectations[nodeId] = (description, expected);
        return expected;
    }

    // No invalidation: the expectation depends only on the description and the node-id, and a
    // rebind replaces the instance, which the reference check above notices.
    private readonly Dictionary<byte, (CanOpenDeviceDescription Description, PeerSafetyConfiguration? Expected)> _safetyExpectations = new();

    /// <summary>Whether any assigned slave is a safety slave: then the master sends no Start to node 0.</summary>
    private bool HasAssignedSafetySlave()
    {
        for (byte id = 1; id <= CanOpenCobId.MaxNodeId; id++)
        {
            if (IsAssignedSlave(id) && SafetyExpectationOf(id) is not null) return true;
        }
        return false;
    }

    private bool AnySlaveVerifying()
    {
        for (byte id = 1; id <= CanOpenCobId.MaxNodeId; id++) if (_slaveVerifying[id]) return true;
        return false;
    }

    private void BeginSlaveVerification(byte nodeId, PeerSafetyConfiguration expected)
    {
        _slaveVerifying[nodeId] = true;
        var cts = _slaveVerificationCts ??= new CancellationTokenSource();
        var token = cts.Token;
        int generation = _bootGeneration;
        _ = Task.Run(async () =>
        {
            bool verified = false;
            Exception? failure = null;
            try
            {
                verified = (await VerifyPeerSafetyConfigurationAsync(nodeId, expected, token).ConfigureAwait(false)).Succeeded;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception ex) { failure = ex; } // a cancellation of anything else is a failure
            try { _actor.Post(() => OnSlaveVerified(nodeId, generation, verified, failure)); }
            catch (ObjectDisposedException) { }
        });
    }

    private void OnSlaveVerified(byte nodeId, int generation, bool verified, Exception? failure)
    {
        // Cancelled by a reset, a role change or dispose; or begun by a boot that has been
        // cancelled since, while a newer boot is verifying the same slave.
        if (generation != _bootGeneration || !_slaveVerifying[nodeId]) return;
        _slaveVerifying[nodeId] = false;
        if (_disposed != 0 || _flyingMasterRole != FlyingMasterRole.Active || _bootHalted) return;
        if (_coldResetPending)
        {
            // A forced Reset Communication is held: nothing is started, reset or signalled in the
            // window, as for an announcement. A confirmed reset cancels this boot; an abandoned
            // one resumes it, which starts a verified slave and verifies a failed one again.
            if (verified) _slaveVerified[nodeId] = true;
            return;
        }
        if (failure is not null) RaiseBackgroundException(failure);
        if (verified)
        {
            _slaveVerified[nodeId] = true;
            byte state = (byte)_od.ReadUnsigned(Co.RequestNmt, nodeId);
            ConsiderStart(nodeId, state);
            TryFinishBoot();
            return;
        }
        uint startup = ReadStartup();
        bool simultaneous = (startup & NmtStartAllNodesBit) != 0 && (startup & NmtSuppressSelfStartBit) == 0;
        if (IsMandatory(nodeId) || simultaneous)
        {
            // The simultaneous start is one moment for the whole network, and a mandatory slave
            // gates the network: the boot halts, with the 1F80h error reaction, as on a boot timeout.
            ApplyBootErrorReaction(new[] { nodeId }, FlyingMasterSignal.SlaveSafetyConfigurationInvalid);
            return;
        }
        _slaveStarted[nodeId] = true; // skipped: never started by this boot
        RaiseFlyingMaster(FlyingMasterSignal.SlaveSafetyConfigurationInvalid, nodeId, null);
        TryFinishBoot();
    }

    private OdWriteDecision ValidateSlaveAssignmentWrite(byte subindex, byte[] value)
    {
        if (subindex == 0) return OdWriteDecision.Reject(SdoAbortCode.AttemptWriteReadOnly);
        if (subindex > CanOpenCobId.MaxNodeId) return OdWriteDecision.Reject(SdoAbortCode.SubIndexDoesNotExist);
        if (value.Length != 4) return OdWriteDecision.Accept;
        return OdWriteDecision.Accept;
    }

    private OdWriteDecision ValidateBootTimeWrite(byte subindex, byte[] value)
    {
        if (subindex != 0) return OdWriteDecision.Reject(SdoAbortCode.SubIndexDoesNotExist);
        if (value.Length != 4) return OdWriteDecision.Accept;
        return OdWriteDecision.Accept;
    }

    /// <summary>
    /// A write to <c>1F82h</c> requests an NMT service and does not replace the tracked state.
    /// The values are the state bytes: 4 stopped, 5 operational, 6 reset node, 7 reset
    /// communication, 127 pre-operational. Sub-index <c>80h</c> addresses every node.
    /// </summary>
    private OdWriteDecision ValidateRequestNmtWrite(byte subindex, byte[] value)
    {
        if (subindex == 0) return OdWriteDecision.Reject(SdoAbortCode.AttemptWriteReadOnly);
        if (subindex > RequestAllNodes) return OdWriteDecision.Reject(SdoAbortCode.SubIndexDoesNotExist);
        if (value.Length != 1) return OdWriteDecision.Accept;
        if (_flyingMasterRole != FlyingMasterRole.Active)
            return OdWriteDecision.Reject(SdoAbortCode.DataCannotBeTransferredDeviceState);

        byte target = subindex == RequestAllNodes ? (byte)0 : subindex;
        if (target != 0 && (target == _nodeId || !IsAssignedSlave(target)))
            return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);

        NmtCommand? command = value[0] switch
        {
            RequestStopped => NmtCommand.Stop,
            RequestOperational => NmtCommand.Start,
            RequestResetNode => NmtCommand.ResetNode,
            RequestResetCommunication => NmtCommand.ResetCommunication,
            RequestPreOperational => NmtCommand.EnterPreOperational,
            _ => null,
        };
        if (command is not { } nmt)
            return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);

        RunOnActor(() =>
        {
            if (_flyingMasterRole != FlyingMasterRole.Active || _disposed != 0) return;
            SendNmt(nmt, target);
        });
        return OdWriteDecision.Handled;
    }

    private uint ReadStartup() => _od.ReadUnsigned(Co.NmtStartup, 0x00);

    private uint Assignment(byte nodeId) => _od.ReadUnsigned(Co.SlaveAssignment, nodeId);

    private bool IsAssignedSlave(byte nodeId)
        => nodeId != _nodeId
           && nodeId is >= CanOpenCobId.MinNodeId and <= CanOpenCobId.MaxNodeId
           && (Assignment(nodeId) & SlaveAssignedBit) != 0;

    private bool KeepsAlive(byte nodeId) => (Assignment(nodeId) & SlaveKeepAliveBit) != 0;

    private bool IsMandatory(byte nodeId)
        => IsAssignedSlave(nodeId) && (Assignment(nodeId) & SlaveMandatoryBit) != 0;

    private bool HasMandatorySlave()
    {
        for (byte id = 1; id <= CanOpenCobId.MaxNodeId; id++)
            if (IsMandatory(id)) return true;
        return false;
    }

    private bool HasUnseenMandatorySlave()
    {
        for (byte id = 1; id <= CanOpenCobId.MaxNodeId; id++)
            if (IsMandatory(id) && !_slaveSeen[id]) return true;
        return false;
    }

    private bool HasBootableSlave()
    {
        for (byte id = 1; id <= CanOpenCobId.MaxNodeId; id++)
        {
            if (!IsAssignedSlave(id)) continue;
            if ((Assignment(id) & (SlaveAssignedBit | SlaveBootBit)) == (SlaveAssignedBit | SlaveBootBit))
                return true;
        }
        return false;
    }

    private void SendNmt(NmtCommand command, byte target)
        => _ = EnqueueNmt(command, target);

    /// <summary>Queues one NMT frame behind the previous one. The task completes with
    /// <see langword="true"/> only when the adapter confirmed the send. Cancellation faults
    /// the task. Every other failure is reported through <c>BackgroundExceptionOccurred</c>
    /// and completes with <see langword="false"/>, including an unconfirmed send.</summary>
    private Task<bool> EnqueueNmt(NmtCommand command, byte target)
    {
        var payload = new[] { (byte)command, target };
        var previous = _nmtOrder;
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _nmtOrder = done.Task;
        // Method groups, not capturing lambdas: a fresh closure would leave the compiler's
        // delegate cache on the untaken side of a branch.
        previous.ContinueWith(
            SendNextNmt,
            (payload, done),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
        return done.Task;
    }

    private void SendNextNmt(Task completed, object? state)
    {
        _ = completed;
        var (payload, done) = ((byte[], TaskCompletionSource<bool>))state!;
        SendNmtReporting(payload).ContinueWith(
            FinishNmt,
            done,
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private void FinishNmt(Task<bool> send, object? state)
    {
        var done = (TaskCompletionSource<bool>)state!;
        if (IsNmtCancellation(send))
            done.TrySetCanceled();
        else if (send.IsFaulted)
        {
            // Same report as SendControlFrame. Completing with false, rather than
            // faulting this task, is what a fire-and-forget SendNmt can observe:
            // the exception is no longer sitting on a task nobody awaits.
            RaiseBackgroundException(send.Exception!.GetBaseException());
            done.TrySetResult(false);
        }
        else done.TrySetResult(send.Result);
    }

    private static bool IsNmtCancellation(Task send)
    {
        if (send.IsCanceled) return true;
        if (!send.IsFaulted || send.Exception is null) return false;

        // The async send reports cancellation as TaskStatus.Canceled, handled above.
        // TrySetException(OperationCanceledException) faults the task instead. That shape is
        // the same outcome: every inner exception is cancellation, and it is not a transport failure.
        return send.Exception.InnerExceptions.All(static ex => ex is OperationCanceledException);
    }

    /// <summary>
    /// Sends one NMT frame. An unconfirmed send (timeout, bus-off, rejection) is reported here
    /// and comes back as <see langword="false"/>. A throw other than cancellation leaves this
    /// task faulted; <see cref="EnqueueNmt"/> reports it and still completes with false.
    /// <c>SendConfirmedAsync</c> throws <see cref="ObjectDisposedException"/> for a disposed service
    /// and rethrows the bus (<see cref="CanKitException"/> from a device adapter, and whatever
    /// else the driver raised, including <see cref="InvalidOperationException"/>).
    /// </summary>
    private Task<bool> SendNmtReporting(byte[] payload)
    {
        var frame = CanFrame.Classic(unchecked((int)CanOpenCobId.NmtCommand), payload, isExtendedFrame: false);
        return Task.Run(async () =>
        {
            try
            {
                var conf = await _service.SendConfirmedAsync(frame).ConfigureAwait(false);
                if (conf.Confirmed) return true;
                RaiseBackgroundException(new CanOpenTransportException(
                    $"CANopen frame TX on COB-ID 0x{CanOpenCobId.NmtCommand:X3} failed: {conf.FailureReason}."));
                return false;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        });
    }
}
