using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Sdo;

namespace CanKit.Pro.CANopen;

internal sealed partial class CanOpenNode
{
    private sealed class BootConfigurationPlan(List<ConciseDcfWrite> writes)
    {
        public readonly List<ConciseDcfWrite> Writes = writes;
        // A partial stream may already have written 1020h. Only completing this plan may
        // clear that uncertainty, including after role loss or a local NMT reset.
        public volatile bool Incomplete;
    }

    private sealed class BootConfigurationReset(CancellationToken token, NmtCommand command)
    {
        public readonly CancellationToken Token = token;
        public readonly NmtCommand Command = command;
        public readonly TaskCompletionSource<bool> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _handoff;
        private long _latestBootUp;

        // Actor only. An RX already buffered while TX confirmation was outstanding is
        // eligible only if it arrived after this particular reset's driver handoff.
        public void Receive(long arrival)
        {
            _latestBootUp = Math.Max(_latestBootUp, arrival);
            TryComplete();
        }

        public void Sent(long handoff)
        {
            if (handoff <= 0)
            {
                Completion.TrySetException(new InvalidOperationException("Configuration restore/reset requires a host handoff timestamp from the bus service."));
                return;
            }
            _handoff = handoff;
            TryComplete();
        }

        private void TryComplete()
        {
            if (_handoff > 0 && _latestBootUp >= _handoff) Completion.TrySetResult(true);
        }
    }

    private BootConfigurationReset? CaptureConfigurationReset(CanFrame frame, CancellationToken token)
    {
        // Extended PDO ID zero is not NMT. Internally generated standard NMT frames
        // contain command and target; an application-selected target can be outside our
        // slave table. Observation must not change transmission of either kind of frame.
        if (frame.IsExtendedFrame || frame.ID != CanOpenCobId.NmtCommand || frame.Data.Length != 2) return null;
        byte target = frame.Data.Span[1];
        if (target > CanOpenCobId.MaxNodeId) return null;
        // Capture this exact attempt before awaiting the service; a late confirmation
        // cannot affect a newer attempt installed after this one was cancelled.
        var reset = Volatile.Read(ref _slaveConfigurationBootUps[target]);
        return reset is not null && reset.Token == token && frame.Data.Span[0] == (byte)reset.Command ? reset : null;
    }

    private void ObserveConfigurationBootUp(uint cobId, byte[] data, bool isRtr, bool isFd, long arrival)
    {
        if (cobId <= CanOpenCobId.HeartbeatBase || cobId > CanOpenCobId.HeartbeatBase + CanOpenCobId.MaxNodeId) return;
        if (isRtr || isFd || data.Length != 1 || data[0] != 0) return;
        _slaveConfigurationBootUps[cobId - CanOpenCobId.HeartbeatBase]?.Receive(arrival);
    }

    private readonly BootConfigurationPlan?[] _slaveBootConfigurations = new BootConfigurationPlan?[CanOpenCobId.MaxNodeId + 1];
    private readonly BootConfigurationReset?[] _slaveConfigurationBootUps = new BootConfigurationReset?[CanOpenCobId.MaxNodeId + 1];
    private readonly bool[] _slaveConfigurationAwaitingBootUp = new bool[CanOpenCobId.MaxNodeId + 1];
    private readonly bool[] _slaveConfigurationNeedsStart = new bool[CanOpenCobId.MaxNodeId + 1];
    private bool BootConfigurationEnabled => _options.RequireBootConfigurationMatch || _options.EnableBootConfigurationDownload;

    internal void SetSlaveBootConfiguration(byte nodeId, ReadOnlyMemory<byte>? conciseDcf)
    {
        ThrowIfDisposed();
        CanOpenCobId.ValidateNodeId(nodeId);
        if (!_options.EnableBootConfigurationDownload)
            throw new InvalidOperationException("EnableBootConfigurationDownload is required to register boot configuration.");
        var plan = conciseDcf.HasValue ? new BootConfigurationPlan(ParseConciseDcf(conciseDcf.Value.ToArray(), _options.MaxSdoTransferBytes)) : null;
        RunOnActorAndWait(() =>
        {
            _slaveBootConfigurations[nodeId] = plan;
            if (_flyingMasterRole == FlyingMasterRole.Active) BeginBootUp();
        });
    }

    private async Task<SlaveBootResult> DownloadSlaveConfigurationAsync(byte nodeId,
        BootConfigurationPlan plan, byte restore, CancellationToken token)
    {
        var writes = plan.Writes;
        ushort? index = null;
        byte? subindex = null;
        var gate = PeerSdoChannel(nodeId);
        bool held = false;
        try
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            held = true;
            token.ThrowIfCancellationRequested();
            var targets = new HashSet<ushort>(writes.Select(static w => w.Index)) { Co.RestoreDefaults };
            var binding = new PeerDescriptionBinding(CaptureBoundPeerDescription(nodeId), Array.Empty<(ushort, byte)>(), targets);
            if (!binding.IsBound) throw new InvalidOperationException("Boot configuration requires a bound peer EDS or DCF.");
            // Validate the entire plan before changing NMT state or resetting factory defaults.
            foreach (var write in writes)
            {
                index = write.Index;
                subindex = write.Subindex;
                EnsureConfigurationWrite(nodeId, write.Index, write.Subindex, binding);
            }
            if (restore != 0)
            {
                index = Co.RestoreDefaults;
                subindex = restore;
                EnsureConfigurationWrite(nodeId, Co.RestoreDefaults, restore, binding);
            }
            index = null;
            subindex = null;
            plan.Incomplete = true;
            await SendBootConfigurationNmtAsync(nodeId, NmtCommand.EnterPreOperational, token).ConfigureAwait(false);
            // An Operational announcement from before the update must not bypass final Start.
            await _actor.PostAsync(() =>
            {
                token.ThrowIfCancellationRequested();
                _slaveConfigurationNeedsStart[nodeId] = true;
            }).ConfigureAwait(false);
            if (restore != 0)
            {
                index = Co.RestoreDefaults;
                subindex = restore;
                await SdoDownloadCoreAsync(nodeId, Co.RestoreDefaults, restore, LoadSignature,
                    SdoTransferMode.Auto, token, transaction: binding).ConfigureAwait(false);
                index = null;
                subindex = null;
                await ResetConfiguredSlaveAsync(nodeId, token).ConfigureAwait(false);
            }
            foreach (var write in writes)
            {
                token.ThrowIfCancellationRequested();
                index = write.Index;
                subindex = write.Subindex;
                await SdoDownloadCoreAsync(nodeId, write.Index, write.Subindex, write.Value,
                    SdoTransferMode.Auto, token, transaction: binding).ConfigureAwait(false);
                if (AfterConciseDcfWriteForTests is { } pause) await pause().ConfigureAwait(false);
            }
            plan.Incomplete = false;
            return new SlaveBootResult(nodeId, SlaveBootStatus.ConfigurationDownloaded);
        }
        catch (Exception ex)
        {
            // Same boot error boundary as identity/configuration reads: retain the original
            // abort or transport error for callers, and let the actor apply mandatory/retry policy.
            return new SlaveBootResult(nodeId, SlaveBootStatus.ConfigurationDownloadFailed, index, subindex, error: ex);
        }
        finally
        {
            if (held) gate.Release();
        }
    }

    private void EnsureConfigurationWrite(byte nodeId, ushort index, byte subindex, PeerDescriptionBinding binding)
    {
        EnsurePeerSdoAccess(nodeId, index, subindex, binding);
        if (binding.DeclaresReadOnly(index, subindex))
            throw new InvalidOperationException($"The peer description declares {index:X4}h:{subindex:X2}h read-only. Nothing was sent.");
    }

    private async Task SendBootConfigurationNmtAsync(byte nodeId, NmtCommand command, CancellationToken token)
    {
        var send = await _actor.PostAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            return EnqueueNmt(command, nodeId, token);
        }).ConfigureAwait(false);
        // Cancel our waiter even if an older NMT send still owns the queue. The queued
        // command also carries the token, so an abandoned boot cannot transmit it later.
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (token.Register(() => cancelled.TrySetCanceled()))
        {
            var completed = await Task.WhenAny(send, cancelled.Task).ConfigureAwait(false);
            if (!await completed.ConfigureAwait(false))
                throw new InvalidOperationException($"Boot configuration {command} was not confirmed by the adapter.");
        }
    }

    private async Task ResetConfiguredSlaveAsync(byte nodeId, CancellationToken token, NmtCommand command = NmtCommand.ResetNode)
    {
        var reset = new BootConfigurationReset(token, command);
        var bootUp = reset.Completion;
        using var cancellation = token.Register(() => bootUp.TrySetCanceled());
        Action timeout = () => bootUp.TrySetException(new TimeoutException("No boot-up announcement after configuration restore/reset."));
        await _actor.PostAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            _slaveConfigurationBootUps[nodeId] = reset;
        }).ConfigureAwait(false);
        try
        {
            // Listen before sending: a fast peer can boot before local TX confirmation arrives.
            await SendBootConfigurationNmtAsync(nodeId, command, token).ConfigureAwait(false);
            // Queueing and LSS/adapter holds are not remote boot time. A boot-up already
            // received during confirmation remains recorded in the installed listener.
            using var deadline = await _actor.PostAsync(() =>
            {
                token.ThrowIfCancellationRequested();
                _slaveConfigurationAwaitingBootUp[nodeId] = true;
                return _deadlines.Arm(_options.SdoTimeout, timeout);
            }).ConfigureAwait(false);
            await bootUp.Task.ConfigureAwait(false);
        }
        finally
        {
            // Settle the waiter before observing it, including when the send failed first.
            bootUp.TrySetCanceled();
            _ = bootUp.Task.Exception;
            // The channel lease is still held. This cleanup is queued before releasing it,
            // so a later update cannot install its waiter until after this actor turn.
            RunOnActor(() =>
            {
                _slaveConfigurationAwaitingBootUp[nodeId] = false;
                _slaveConfigurationBootUps[nodeId] = null;
            });
        }
    }
}
