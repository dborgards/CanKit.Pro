using System;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.Reliability;

namespace CanKit.Pro.CANopen;

internal sealed partial class CanOpenNode
{
    private readonly CancellationTokenSource?[] _slaveNmtErrorTokens = new CancellationTokenSource?[CanOpenCobId.MaxNodeId + 1];
    private readonly bool[] _slaveNmtErrorRecovering = new bool[CanOpenCobId.MaxNodeId + 1];
    private readonly bool[] _slaveNmtErrorResetting = new bool[CanOpenCobId.MaxNodeId + 1];
    private readonly bool[] _slaveNmtErrorFailed = new bool[CanOpenCobId.MaxNodeId + 1];
    private readonly IDeadline?[] _slaveNmtErrorDeadlines = new IDeadline?[CanOpenCobId.MaxNodeId + 1];

    private bool PeerBootCancellationEnabled => _options.EnableNmtErrorHandler || _options.EnableNmtBootupHandler || _options.EnableBootKeepAlive;

    private CancellationToken SlaveBootToken(byte nodeId)
    {
        var parent = (_slaveVerificationCts ??= new CancellationTokenSource()).Token;
        if (!PeerBootCancellationEnabled) return parent;
        return (_slaveNmtErrorTokens[nodeId] ??= CancellationTokenSource.CreateLinkedTokenSource(parent)).Token;
    }

    // The initial boot owns errors until that peer has completed its gates. Application-owned
    // slaves have no such completion, but §4.2 still resets them without configuring/starting them.
    private void HandleRuntimeNmtError(byte nodeId, int generation)
    {
        if (!_options.EnableNmtErrorHandler || _disposed != 0 || _coldResetPending || _bootHalted
            || generation != _bootGeneration || _flyingMasterRole != FlyingMasterRole.Active
            || !IsAssignedSlave(nodeId) || _slaveNmtErrorRecovering[nodeId]) return;
        bool bootSlave = (Assignment(nodeId) & SlaveBootBit) != 0 && (ReadStartup() & NmtSuppressSlaveStartBit) == 0;
        if (bootSlave && !_slaveStarted[nodeId]) return;

        // Queue the application's indication before requesting the NMT action. Event delivery
        // remains asynchronous, as for HeartbeatTimeout and NodeGuardingTimeout themselves.
        RaiseFlyingMaster(FlyingMasterSignal.SlaveErrorControlEvent, nodeId, null);
        uint startup = ReadStartup();
        bool stopAll = IsMandatory(nodeId) && (startup & NmtStopAllOnErrorBit) != 0;
        bool resetAll = IsMandatory(nodeId) && !stopAll && (startup & NmtResetAllOnErrorBit) != 0;
        if (stopAll || resetAll)
        {
            HaltBootWork();
            _slaveBootResults[nodeId] = new SlaveBootResult(nodeId, SlaveBootStatus.ErrorControlReactionRequested);
            // Figure 12 specifies target zero, including devices outside this master's list.
            // The active master already ignores its own broadcast NMT echo.
            // Halt cancelled the boot parent; this reaction owns a fresh token so a later
            // role/configuration change still invalidates its queued broadcast.
            var reaction = _slaveNmtErrorTokens[nodeId] = new CancellationTokenSource();
            _ = EnqueueNmt(stopAll ? NmtCommand.Stop : NmtCommand.ResetNode, 0, reaction.Token);
            return;
        }

        RestartSlaveBootChecks(nodeId, recovering: true);
        _slaveNmtErrorResetting[nodeId] = true;
        _slaveBootResults[nodeId] = new SlaveBootResult(nodeId, SlaveBootStatus.ErrorControlReactionRequested);
        var token = SlaveBootToken(nodeId);
        ArmPeerRecoveryDeadline(nodeId);
        _ = ResetAfterNmtErrorAsync(nodeId, generation, bootSlave, token);
    }

    // Shared by runtime errors and observed peer boot-ups. Other peers retain their tokens.
    private void RestartSlaveBootChecks(byte nodeId, bool recovering)
    {
        FinishNmtErrorRecovery(nodeId);
        _slaveNmtErrorTokens[nodeId]?.Cancel();
        _slaveNmtErrorTokens[nodeId]?.Dispose();
        _slaveNmtErrorTokens[nodeId] = null;
        _slaveBootRetries[nodeId]?.Dispose();
        _slaveBootRetries[nodeId] = null;
        _slaveBootResetPending[nodeId] = false;
        _slaveIdentityVerifying[nodeId] = false;
        _slaveConfigurationVerifying[nodeId] = false;
        _slaveVerifying[nodeId] = false;
        _slaveNmtErrorRecovering[nodeId] = recovering;
        _slaveNmtErrorResetting[nodeId] = false;
        _slaveNmtErrorFailed[nodeId] = false;
        _slaveStarted[nodeId] = false;
        _slaveSeen[nodeId] = false;
        // A known reboot already proves that the peer was not kept Operational.
        _slaveKeepAliveChecked[nodeId] = true;
        _slaveInitiallyOperational[nodeId] = false;
        _slaveKeepAliveChecking[nodeId] = false;
        _slaveKeepAliveFailed[nodeId] = false;
        _slaveIdentityVerified[nodeId] = false;
        _slaveIdentityFailed[nodeId] = false;
        _slaveConfigurationVerified[nodeId] = false;
        _slaveConfigurationFailed[nodeId] = false;
        _slaveConfigurationNeedsStart[nodeId] = true;
        _slaveErrorControlVerified[nodeId] = false;
        _slaveErrorControlFailed[nodeId] = false;
        _slaveVerified[nodeId] = false;
        _slaveBootResetConfirmed[nodeId] = true; // The peer reboot replaces an additional Reset Communication.
        EndBootHeartbeatWait(nodeId);
        StopOwnedBootGuarding(nodeId);
    }

    private void ArmPeerRecoveryDeadline(byte nodeId)
    {
        uint bootMs = _od.ReadUnsigned(Co.BootTime, 0);
        if (bootMs != 0)
            _slaveNmtErrorDeadlines[nodeId] = _deadlines.Arm(TimeSpan.FromMilliseconds(bootMs),
                () => FailNmtErrorRecovery(nodeId, new TimeoutException("The error-recovery boot deadline expired.")));
    }

    private async Task ResetAfterNmtErrorAsync(byte nodeId, int generation, bool bootSlave, CancellationToken token)
    {
        Exception? failure = null;
        var channel = PeerSdoChannel(nodeId);
        bool held = false;
        try
        {
            await channel.WaitAsync(token).ConfigureAwait(false);
            held = true;
            // Reuse the confirmed-reset/fresh-boot-up handshake, including its driver handoff
            // timestamp and timeout after confirmation. The channel serializes reset listeners.
            await ResetConfiguredSlaveAsync(nodeId, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (Exception ex) { failure = ex; }
        finally { if (held) channel.Release(); }
        if (token.IsCancellationRequested) return;
        try
        {
            _actor.Post(() =>
            {
                if (token.IsCancellationRequested || generation != _bootGeneration || _disposed != 0
                    || _bootHalted || _flyingMasterRole != FlyingMasterRole.Active) return;
                _slaveNmtErrorResetting[nodeId] = false;
                if (failure is not null) { FailNmtErrorRecovery(nodeId, failure); return; }
                // A real post-handoff boot-up was received. Re-run the enabled gates even if
                // another Operational announcement arrived while confirmation was outstanding.
                _slaveSeen[nodeId] = true;
                _slaveBootResults[nodeId] = new SlaveBootResult(nodeId, SlaveBootStatus.Announced);
                if (bootSlave)
                {
                    if (_coldResetPending) return;
                    ConsiderStart(nodeId, RequestPreOperational);
                }
                else FinishNmtErrorRecovery(nodeId);
                TryFinishBoot();
            });
        }
        catch (ObjectDisposedException) { return; }
    }

    private void FailNmtErrorRecovery(byte nodeId, Exception failure)
    {
        if (!_slaveNmtErrorRecovering[nodeId] || _slaveNmtErrorFailed[nodeId]) return;
        _slaveNmtErrorFailed[nodeId] = true;
        _slaveKeepAliveChecking[nodeId] = false;
        _slaveNmtErrorResetting[nodeId] = false;
        _slaveNmtErrorDeadlines[nodeId]?.Dispose();
        _slaveNmtErrorDeadlines[nodeId] = null;
        _slaveNmtErrorTokens[nodeId]?.Cancel();
        _slaveBootRetries[nodeId]?.Dispose();
        _slaveBootRetries[nodeId] = null;
        _slaveIdentityVerifying[nodeId] = false;
        _slaveConfigurationVerifying[nodeId] = false;
        _slaveVerifying[nodeId] = false;
        EndBootHeartbeatWait(nodeId);
        StopOwnedBootGuarding(nodeId);
        _slaveBootResults[nodeId] = new SlaveBootResult(nodeId, SlaveBootStatus.ErrorRecoveryFailed, error: failure);
        RaiseFlyingMaster(FlyingMasterSignal.SlaveErrorRecoveryFailed, nodeId, null);
        // Keep this episode latched: repeated consumer timeouts must not flood a silent peer
        // with resets. A new boot/configuration or role cycle explicitly clears the failure.
    }

    private void FinishNmtErrorRecovery(byte nodeId)
    {
        _slaveNmtErrorDeadlines[nodeId]?.Dispose();
        _slaveNmtErrorDeadlines[nodeId] = null;
        _slaveNmtErrorRecovering[nodeId] = false;
    }

    private void CancelNmtErrorRecovery()
    {
        for (byte id = 1; id <= CanOpenCobId.MaxNodeId; id++)
        {
            FinishNmtErrorRecovery(id);
            _slaveNmtErrorResetting[id] = false;
            _slaveNmtErrorFailed[id] = false;
            _slaveNmtErrorTokens[id]?.Cancel();
            _slaveNmtErrorTokens[id]?.Dispose();
            _slaveNmtErrorTokens[id] = null;
        }
    }
}
