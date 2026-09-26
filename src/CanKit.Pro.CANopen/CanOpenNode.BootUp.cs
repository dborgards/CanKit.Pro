using System;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Core.Exceptions;
using CanKit.Pro.CANopen.Nmt;
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
    private bool _bootBroadcastSent;
    private bool _bootHalted;
    private bool _bootSelfStarted;
    private int _bootGeneration;
    private IDeadline? _bootDeadline;

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
        if (_flyingMasterRole != FlyingMasterRole.Active || _disposed != 0) return;

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
        uint bootMs = _od.ReadUnsigned(Co.BootTime, 0x00);
        if (bootMs > 0 && HasMandatorySlave())
        {
            int generation = _bootGeneration;
            _bootDeadline = _deadlines.Arm(TimeSpan.FromMilliseconds(bootMs), () =>
            {
                if (generation != _bootGeneration || _disposed != 0
                    || _flyingMasterRole != FlyingMasterRole.Active) return;
                OnBootTimeout();
            });
        }
        TryFinishBoot();
    }

    private void CancelBootUp()
    {
        _bootGeneration++;
        _bootDeadline?.Dispose();
        _bootDeadline = null;
        Array.Clear(_slaveSeen, 0, _slaveSeen.Length);
        Array.Clear(_slaveStarted, 0, _slaveStarted.Length);
        _bootBroadcastSent = false;
        _bootHalted = false;
        _bootSelfStarted = false;
    }

    private void NoteSlaveNmtState(byte nodeId, byte state)
    {
        if (_flyingMasterRole != FlyingMasterRole.Active || _disposed != 0) return;
        if (nodeId == _nodeId || nodeId > CanOpenCobId.MaxNodeId || !IsAssignedSlave(nodeId)) return;

        _od.WriteRawUnchecked(Co.RequestNmt, nodeId, new[] { state });
        _slaveSeen[nodeId] = true;
        ConsiderStart(nodeId, state);
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

        // Bit 1 waits for one broadcast, and only when this node may enter Operational too.
        // Self-start is applied locally; the active master does not take the broadcast as its own.
        bool simultaneous = (startup & NmtStartAllNodesBit) != 0 && (startup & NmtSuppressSelfStartBit) == 0;
        if (simultaneous && !_bootBroadcastSent) return;

        _slaveStarted[nodeId] = true;
        SendNmt(NmtCommand.Start, nodeId);
    }

    private void TryFinishBoot()
    {
        if (_flyingMasterRole != FlyingMasterRole.Active || _bootHalted || _disposed != 0) return;
        if (HasUnseenMandatorySlave()) return;

        uint startup = ReadStartup();
        bool mayStartSlaves = (startup & NmtSuppressSlaveStartBit) == 0;
        bool simultaneous = mayStartSlaves
            && (startup & NmtStartAllNodesBit) != 0
            && (startup & NmtSuppressSelfStartBit) == 0;
        if (simultaneous && !_bootBroadcastSent && HasBootableSlave())
        {
            _bootBroadcastSent = true;
            SendNmt(NmtCommand.Start, 0);
        }

        if ((startup & NmtSuppressSelfStartBit) == 0 && !_bootSelfStarted)
        {
            _bootSelfStarted = true;
            if (_state != NmtState.Operational)
                ApplyNmtTransition(NmtState.Operational);
        }
    }

    private void OnBootTimeout()
    {
        if (_disposed != 0) return;
        if (!HasUnseenMandatorySlave())
        {
            TryFinishBoot();
            return;
        }

        _bootHalted = true;
        uint startup = ReadStartup();
        bool stopAll = (startup & NmtStopAllOnErrorBit) != 0;
        bool resetAll = !stopAll && (startup & NmtResetAllOnErrorBit) != 0;
        if (stopAll || resetAll)
        {
            var command = stopAll ? NmtCommand.Stop : NmtCommand.ResetNode;
            for (byte id = 1; id <= CanOpenCobId.MaxNodeId; id++)
            {
                if (!IsAssignedSlave(id)) continue;
                SendNmt(command, id);
            }
        }

        for (byte id = 1; id <= CanOpenCobId.MaxNodeId; id++)
        {
            if (!IsMandatory(id) || _slaveSeen[id]) continue;
            if (!stopAll && !resetAll)
                SendNmt(NmtCommand.ResetNode, id);
            RaiseFlyingMaster(FlyingMasterSignal.SlaveBootTimeout, id, null);
        }
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
        previous.ContinueWith(
            _ =>
            {
                SendNmtReporting(payload).ContinueWith(
                    send =>
                    {
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
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
        return done.Task;
    }

    private static bool IsNmtCancellation(Task send)
    {
        if (send.IsCanceled) return true;
        if (!send.IsFaulted || send.Exception is null) return false;

        // The async send reports cancellation as TaskStatus.Canceled, handled above.
        // TrySetException(OperationCanceledException) faults the task instead. That shape is
        // the same outcome: every inner exception is cancellation, and it is not a transport failure.
        var onlyCancellation = true;
        foreach (var ex in send.Exception.InnerExceptions)
            if (ex is not OperationCanceledException)
                onlyCancellation = false;
        return onlyCancellation;
    }

    /// <summary>
    /// Sends one NMT frame. An unconfirmed send (timeout, bus-off, rejection) is reported here
    /// and comes back as <see langword="false"/>. A throw other than cancellation leaves this
    /// task faulted; <see cref="EnqueueNmt"/> reports it and still completes with false.
    /// <c>SendConfirmed</c> throws <see cref="ObjectDisposedException"/> for a disposed service
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
                var conf = await _service.SendConfirmed(frame).ConfigureAwait(false);
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
