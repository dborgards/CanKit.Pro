using System;
using System.Collections.Generic;
using System.Threading;
using CanKit.Pro.Actor;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.Reliability;

namespace CanKit.Pro.CANopen;

internal sealed partial class CanOpenNode
{
    private bool BootErrorControlEnabled => _options.EnableBootErrorControl || _options.EnableNmtErrorHandler || _options.EnableNmtBootupHandler;

    private sealed class BootHeartbeatWait(long started, long due)
    {
        public readonly long Started = started;
        public readonly long Due = due;
        public IDeadline? Deadline;
    }

    private IReadOnlyDictionary<byte, TimeSpan> _bootHeartbeatConfiguration = new Dictionary<byte, TimeSpan>();
    private bool _bootHeartbeatRestartPending;
    private int _bootHeartbeatRebuildDepth;
    private readonly BootHeartbeatWait?[] _slaveBootHeartbeatWaits = new BootHeartbeatWait?[CanOpenCobId.MaxNodeId + 1];
    private readonly bool[] _slaveErrorControlVerified = new bool[CanOpenCobId.MaxNodeId + 1];
    private readonly bool[] _slaveErrorControlFailed = new bool[CanOpenCobId.MaxNodeId + 1];
    private readonly NodeGuardingConsumer?[] _bootGuardingConsumers = new NodeGuardingConsumer?[CanOpenCobId.MaxNodeId + 1];

    // Capture before publishing heartbeat-error recovery, whose application callbacks may
    // edit 1016h again. The caller retains the restart across nested rebuilds.
    private bool CaptureBootHeartbeatConfiguration(IReadOnlyDictionary<byte, TimeSpan> desired)
    {
        if (!BootErrorControlEnabled) return false;
        var previous = _bootHeartbeatConfiguration;
        _bootHeartbeatConfiguration = desired;
        for (byte id = 1; id <= CanOpenCobId.MaxNodeId; id++)
        {
            if (!IsAssignedSlave(id) || (Assignment(id) & SlaveBootBit) == 0) continue;
            previous.TryGetValue(id, out var before);
            desired.TryGetValue(id, out var after);
            if (before != after) return true;
        }
        return false;
    }

    private bool IsMandatoryErrorControlPending(byte nodeId)
        => BootErrorControlEnabled && IsMandatory(nodeId)
           && (ReadStartup() & NmtSuppressSlaveStartBit) == 0
           && (Assignment(nodeId) & SlaveBootBit) != 0 && !_slaveErrorControlVerified[nodeId];

    private bool HasUnverifiedMandatoryErrorControl()
    {
        for (byte id = 1; id <= CanOpenCobId.MaxNodeId; id++)
            if (IsMandatoryErrorControlPending(id)) return true;
        return false;
    }

    private bool BeginSlaveErrorControl(byte nodeId)
    {
        if (_slaveBootHeartbeatWaits[nodeId] is not null) return false;
        // This opt-in manages guarding for the assigned, boot-enabled slave. Heartbeat
        // takes priority; disabled guarding settings must not leave an earlier poller alive.
        StopNodeGuardingConsumerCore(nodeId);
        if (_bootHeartbeatConfiguration.TryGetValue(nodeId, out var timeout))
        {
            long now = _actor.TimeSource.GetTimestamp();
            var wait = new BootHeartbeatWait(now, TickMath.DueAt(now, timeout, _actor.TimeSource.Frequency));
            _slaveBootHeartbeatWaits[nodeId] = wait;
            _slaveBootResults[nodeId] = new SlaveBootResult(nodeId, SlaveBootStatus.WaitingForErrorControl);
            // §4.1 starts this timer with the process itself, independently of the general
            // heartbeat consumer's earlier watch or last received announcement.
            wait.Deadline = _deadlines.Arm(timeout, () => OnBootHeartbeatTimeout(nodeId, wait));
            return false;
        }

        uint assignment = Assignment(nodeId);
        ushort guardMs = (ushort)(assignment >> 16);
        byte retries = (byte)(assignment >> 8);
        if (guardMs != 0 && retries != 0)
        {
            var token = SlaveBootToken(nodeId);
            _bootGuardingConsumers[nodeId] = StartNodeGuardingConsumerCore(nodeId,
                TimeSpan.FromMilliseconds(guardMs), lifeTimeFactor: 1, retries, token);
        }
        // §4.1 does not wait for a guarding response. No configured monitoring is also OK.
        _slaveErrorControlVerified[nodeId] = true;
        _slaveBootResults[nodeId] = new SlaveBootResult(nodeId, SlaveBootStatus.ErrorControlReady);
        return true;
    }

    private void ObserveBootErrorControlHeartbeat(uint cobId, byte[] data, bool isRtr, bool isFd, long arrival)
    {
        if (cobId <= CanOpenCobId.HeartbeatBase || cobId > CanOpenCobId.HeartbeatBase + CanOpenCobId.MaxNodeId) return;
        byte nodeId = (byte)(cobId - CanOpenCobId.HeartbeatBase);
        if (_slaveBootHeartbeatWaits[nodeId] is not { } wait) return;
        // Boot-up, guarding toggles, reserved states and malformed frames are not the
        // heartbeat indication required by §4.1. The raw timestamp excludes buffered old RX.
        if (isRtr || isFd || data.Length != 1 || data[0] is not (0x04 or 0x05 or 0x7F)
            || _nodeGuardingConsumers.ContainsKey(nodeId)
            || arrival <= 0 || arrival < wait.Started || arrival >= wait.Due) return;
        EndBootHeartbeatWait(nodeId);
        if (_coldResetPending) return; // An abandoned reset starts a fresh window on resume.
        _slaveErrorControlVerified[nodeId] = true;
        _slaveBootResults[nodeId] = new SlaveBootResult(nodeId, SlaveBootStatus.ErrorControlReady);
        // HandleIncomingFrame next records this heartbeat's actual NMT state and resumes
        // ConsiderStart. Do not start from the preceding announcement's cached state here.
    }

    private void OnBootHeartbeatTimeout(byte nodeId, BootHeartbeatWait wait)
    {
        if (!ReferenceEquals(_slaveBootHeartbeatWaits[nodeId], wait)) return;
        EndBootHeartbeatWait(nodeId);
        if (_coldResetPending) return;
        _slaveErrorControlFailed[nodeId] = true;
        _slaveBootResults[nodeId] = new SlaveBootResult(nodeId, SlaveBootStatus.ErrorControlFailed,
            Co.ConsumerHeartbeat, error: new TimeoutException("No valid heartbeat received during boot error-control startup."));
        if (ActiveSlaveBootEnabled && !IsMandatory(nodeId))
        {
            ScheduleActiveSlaveRetry(nodeId, FlyingMasterSignal.SlaveErrorControlFailed);
            return;
        }
        uint startup = ReadStartup();
        bool simultaneous = (startup & NmtStartAllNodesBit) != 0 && (startup & NmtSuppressSelfStartBit) == 0;
        if (IsMandatory(nodeId) || simultaneous)
            ApplyBootErrorReaction(new[] { nodeId }, FlyingMasterSignal.SlaveErrorControlFailed);
        else
        {
            RaiseFlyingMaster(FlyingMasterSignal.SlaveErrorControlFailed, nodeId, null);
            TryFinishBoot();
        }
    }

    private void EndBootHeartbeatWait(byte nodeId)
    {
        var wait = _slaveBootHeartbeatWaits[nodeId];
        _slaveBootHeartbeatWaits[nodeId] = null;
        wait?.Deadline?.Dispose();
    }

    private void StopBootErrorControl()
    {
        for (byte id = 1; id <= CanOpenCobId.MaxNodeId; id++)
        {
            EndBootHeartbeatWait(id);
            StopOwnedBootGuarding(id);
        }
    }

    private void StopOwnedBootGuarding(byte nodeId)
    {
        var owned = _bootGuardingConsumers[nodeId];
        _bootGuardingConsumers[nodeId] = null;
        // A later explicit API call owns its replacement, even after this boot ends.
        if (owned is not null && _nodeGuardingConsumers.TryGetValue(nodeId, out var current)
            && ReferenceEquals(current, owned)) StopNodeGuardingConsumerCore(nodeId);
    }
}
