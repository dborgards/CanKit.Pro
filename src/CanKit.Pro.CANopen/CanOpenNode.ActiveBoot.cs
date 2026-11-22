using System;
using System.Threading.Tasks;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.Reliability;

namespace CanKit.Pro.CANopen;

internal sealed partial class CanOpenNode
{
    private readonly IDeadline?[] _slaveBootRetries = new IDeadline?[CanOpenCobId.MaxNodeId + 1];
    private readonly bool[] _slaveBootResetPending = new bool[CanOpenCobId.MaxNodeId + 1];
    private readonly bool[] _slaveBootResetConfirmed = new bool[CanOpenCobId.MaxNodeId + 1];
    private readonly bool[] _slaveBootRetrying = new bool[CanOpenCobId.MaxNodeId + 1];

    private bool ActiveSlaveBootEnabled => _options.ActiveSlaveBootRetryInterval.HasValue;

    private bool CanActivelyBootSlave(byte nodeId)
        => ActiveSlaveBootEnabled && IsAssignedSlave(nodeId)
           && (Assignment(nodeId) & SlaveBootBit) != 0
           && (ReadStartup() & NmtSuppressSlaveStartBit) == 0;

    // Reachability is not an NMT state. Never write a synthetic announcement to 1F82h,
    // or reuse an Operational byte left there by an earlier boot.
    private bool HasSlaveBootContact(byte nodeId)
        => !_slaveNmtErrorFailed[nodeId] && (_slaveSeen[nodeId] || (CanActivelyBootSlave(nodeId) && _slaveIdentityVerified[nodeId]));

    private byte SlaveStateForBoot(byte nodeId)
        => _slaveConfigurationNeedsStart[nodeId] ? RequestPreOperational
            : _slaveSeen[nodeId] ? (byte)_od.ReadUnsigned(Co.RequestNmt, nodeId) : (byte)0;

    private void ResumeActiveSlaveBoots()
    {
        if (!ActiveSlaveBootEnabled) return;
        for (byte id = 1; id <= CanOpenCobId.MaxNodeId; id++) StartActiveSlaveAttempt(id);
    }

    private void StartActiveSlaveAttempt(byte nodeId)
    {
        if (!CanActivelyBootSlave(nodeId) || _disposed != 0 || _bootHalted || _coldResetPending
            || _flyingMasterRole != FlyingMasterRole.Active) return;
        if (_slaveStarted[nodeId] || _slaveBootResetPending[nodeId] || _slaveIdentityVerifying[nodeId]
            || _slaveVerifying[nodeId] || _slaveConfigurationVerifying[nodeId] || _slaveBootHeartbeatWaits[nodeId] is not null
            || _slaveBootRetries[nodeId] is not null) return;
        if (!_slaveBootResetConfirmed[nodeId])
        {
            BeginActiveSlaveReset(nodeId);
            return;
        }
        ConsiderStart(nodeId, SlaveStateForBoot(nodeId));
    }

    private void BeginActiveSlaveReset(byte nodeId)
    {
        _slaveBootResetPending[nodeId] = true;
        int generation = _bootGeneration;
        var reset = EnqueueNmt(NmtCommand.ResetCommunication, nodeId);
        _ = reset.ContinueWith(send => PostActiveSlaveResetResult(nodeId, generation, send),
            System.Threading.CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private void PostActiveSlaveResetResult(byte nodeId, int generation, Task<bool> send)
    {
        bool confirmed = send.Status == TaskStatus.RanToCompletion && send.Result;
        // Observe a fault even if this boot was cancelled. EnqueueNmt normally reports
        // transport failures separately and completes false, or cancels its task.
        _ = send.Exception;
        try { _actor.Post(() => OnActiveSlaveResetResult(nodeId, generation, confirmed)); }
        catch (ObjectDisposedException)
        {
            // The node closed after its in-flight send completed; there is no boot to resume.
            return;
        }
    }

    private void OnActiveSlaveResetResult(byte nodeId, int generation, bool confirmed)
    {
        if (generation != _bootGeneration || !_slaveBootResetPending[nodeId]) return;
        _slaveBootResetPending[nodeId] = false;
        if (_disposed != 0 || _bootHalted || _flyingMasterRole != FlyingMasterRole.Active) return;
        _slaveBootResetConfirmed[nodeId] = confirmed;
        if (_coldResetPending) return;
        if (confirmed)
        {
            StartActiveSlaveAttempt(nodeId);
            return;
        }
        _slaveBootResults[nodeId] = new SlaveBootResult(nodeId, SlaveBootStatus.ResetCommunicationFailed,
            error: new InvalidOperationException("Reset Communication was not confirmed by the adapter."));
        ScheduleActiveSlaveRetry(nodeId, FlyingMasterSignal.SlaveBootResetFailed);
    }

    private static bool IsDeviceTypeNoResponse(SlaveBootResult result)
        => result.Status == SlaveBootStatus.IdentityReadFailed && result.Index == Co.DeviceType
           && result.Subindex == 0 && result.Error is SdoAbortException
           {
               Origin: SdoAbortOrigin.Local,
               AbortCode: (uint)SdoAbortCode.SdoProtocolTimedOut
           };

    private void ScheduleActiveSlaveRetry(byte nodeId, FlyingMasterSignal signal)
    {
        _slaveBootRetrying[nodeId] = true;
        // Only one attempt can complete per slave. Its retry clears this slot before
        // starting the next attempt, so there is no older retry to replace here.
        _slaveBootRetries[nodeId] = _deadlines.Arm(_options.ActiveSlaveBootRetryInterval!.Value, () =>
        {
            // The actor skips disposed deadlines; cancelling the boot disposes every retry.
            _slaveBootRetries[nodeId] = null;
            _slaveKeepAliveChecked[nodeId] = false;
            _slaveKeepAliveFailed[nodeId] = false;
            _slaveInitiallyOperational[nodeId] = false;
            _slaveIdentityFailed[nodeId] = false;
            _slaveIdentityVerified[nodeId] = false;
            _slaveConfigurationVerified[nodeId] = false;
            _slaveConfigurationFailed[nodeId] = false;
            _slaveErrorControlVerified[nodeId] = false;
            _slaveErrorControlFailed[nodeId] = false;
            _slaveVerified[nodeId] = false;
            StartActiveSlaveAttempt(nodeId);
        });
        RaiseFlyingMaster(signal, nodeId, null);
        // Optional slaves have completed a try; a delayed retry need not hold up the
        // mandatory set, including the simultaneous start moment (CiA 302-2 §3.3).
        TryFinishBoot();
    }

    private void CancelActiveSlaveRetries()
    {
        for (int i = 1; i < _slaveBootRetries.Length; i++)
        {
            _slaveBootRetries[i]?.Dispose();
            _slaveBootRetries[i] = null;
        }
        Array.Clear(_slaveBootResetPending, 0, _slaveBootResetPending.Length);
        Array.Clear(_slaveBootRetrying, 0, _slaveBootRetrying.Length);
    }
}
