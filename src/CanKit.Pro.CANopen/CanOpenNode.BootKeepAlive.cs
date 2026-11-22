using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Pro.Actor;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.Reliability;

namespace CanKit.Pro.CANopen;

internal sealed partial class CanOpenNode
{
    private readonly bool[] _slaveKeepAliveChecked = new bool[CanOpenCobId.MaxNodeId + 1];
    private readonly bool[] _slaveKeepAliveChecking = new bool[CanOpenCobId.MaxNodeId + 1];
    private readonly bool[] _slaveKeepAliveFailed = new bool[CanOpenCobId.MaxNodeId + 1];
    private readonly bool[] _slaveInitiallyOperational = new bool[CanOpenCobId.MaxNodeId + 1];
    private readonly long[] _keepAliveHeartbeatArrival = new long[CanOpenCobId.MaxNodeId + 1];
    private readonly byte[] _keepAliveHeartbeatState = new byte[CanOpenCobId.MaxNodeId + 1];
    private readonly KeepAliveProbe?[] _slaveKeepAliveProbes = new KeepAliveProbe?[CanOpenCobId.MaxNodeId + 1];

    private sealed class KeepAliveProbe
    {
        public readonly TaskCompletionSource<byte> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long Handoff;
        public long Due;
        public IDeadline? Deadline;
        private readonly List<(long Arrival, byte State)> _buffered = new();

        public void Receive(long arrival, byte state)
        {
            if (Handoff > 0)
            {
                if (arrival >= Handoff && arrival < Due) Completion.TrySetResult(state);
            }
            else if (_buffered.Count < 128) _buffered.Add((arrival, state));
            else Completion.TrySetException(new InvalidOperationException("Too many state replies before keep-alive TX confirmation."));
        }

        public void Confirm(long handoff, long due)
        {
            Handoff = handoff;
            Due = due;
            foreach (var reply in _buffered) Receive(reply.Arrival, reply.State);
            _buffered.Clear();
        }
    }

    private bool RequiresKeepAliveCheck(byte id) => _options.EnableBootKeepAlive && KeepsAlive(id);
    private bool IsMandatoryKeepAlivePending(byte id)
        => IsMandatory(id) && RequiresKeepAliveCheck(id) && !_slaveKeepAliveChecked[id]
           && (Assignment(id) & SlaveBootBit) != 0 && (ReadStartup() & NmtSuppressSlaveStartBit) == 0;

    private bool HasPendingMandatoryKeepAlive()
    {
        for (byte id = 1; id <= CanOpenCobId.MaxNodeId; id++)
            if (IsMandatoryKeepAlivePending(id)) return true;
        return false;
    }

    private void ClearKeepAliveChecks()
    {
        Array.Clear(_slaveKeepAliveChecked, 0, _slaveKeepAliveChecked.Length);
        Array.Clear(_slaveKeepAliveChecking, 0, _slaveKeepAliveChecking.Length);
        Array.Clear(_slaveKeepAliveFailed, 0, _slaveKeepAliveFailed.Length);
        Array.Clear(_slaveInitiallyOperational, 0, _slaveInitiallyOperational.Length);
        // Heartbeats describe the running peer, not a boot generation. Keep their real
        // timestamps across master takeover; eligibility is checked against 1016h at use.
    }

    private void ObserveKeepAliveState(uint cobId, byte[] data, bool isRtr, bool isFd, long arrival)
    {
        if (!_options.EnableBootKeepAlive || cobId <= CanOpenCobId.HeartbeatBase
            || cobId > CanOpenCobId.HeartbeatBase + CanOpenCobId.MaxNodeId
            || isRtr || isFd || data.Length != 1 || arrival <= 0 || arrival > _actor.TimeSource.GetTimestamp()) return;
        byte id = (byte)(cobId - CanOpenCobId.HeartbeatBase);
        if (id == _nodeId) return;
        byte state = (byte)(data[0] & 0x7F);
        if (data[0] == 0 && arrival >= _keepAliveHeartbeatArrival[id]) _keepAliveHeartbeatArrival[id] = 0;
        if (state is not (RequestStopped or RequestOperational or RequestPreOperational)) return;
        if (_bootHeartbeatConfiguration.ContainsKey(id))
        {
            if ((data[0] & 0x80) != 0 || _nodeGuardingConsumers.ContainsKey(id)) return;
            if (arrival >= _keepAliveHeartbeatArrival[id])
            {
                _keepAliveHeartbeatArrival[id] = arrival;
                _keepAliveHeartbeatState[id] = state;
            }
        }
        else if (_slaveKeepAliveProbes[id] is { } probe)
        {
            if (_nodeGuardingConsumers.TryGetValue(id, out var consumer) && consumer.HasSeenResponse
                && consumer.LastToggle == ((data[0] & 0x80) != 0)) return;
            probe.Receive(arrival, state);
        }
    }

    private void BeginKeepAliveCheck(byte id)
    {
        _slaveKeepAliveChecking[id] = true;
        _slaveBootResults[id] = new SlaveBootResult(id, SlaveBootStatus.CheckingKeepAlive);
        bool useHeartbeat = _bootHeartbeatConfiguration.ContainsKey(id);
        if (useHeartbeat)
        {
            try
            {
                if (ReadCurrentKeepAliveHeartbeat(id) == RequestOperational)
                {
                    CompleteKeepAliveCheck(id, RequestOperational, null);
                    return;
                }
            }
            catch (TimeoutException ex)
            {
                CompleteKeepAliveCheck(id, 0, ex);
                return;
            }
        }
        int generation = _bootGeneration;
        var token = SlaveBootToken(id);
        _ = Task.Run(async () =>
        {
            byte state = 0;
            Exception? failure = null;
            var channel = PeerSdoChannel(id);
            bool held = false;
            try
            {
                // Serialize the optional reset with configuration/runtime reset listeners.
                await channel.WaitAsync(token).ConfigureAwait(false);
                held = true;
                // A heartbeat may change or expire while another operation owns the channel.
                // Re-read it on the actor after acquisition, before deciding to reset the peer.
                state = useHeartbeat ? await _actor.PostAsync(() =>
                {
                    token.ThrowIfCancellationRequested();
                    return ReadCurrentKeepAliveHeartbeat(id);
                }).ConfigureAwait(false) : await ProbeKeepAliveStateAsync(id, token).ConfigureAwait(false);
                if (state != RequestOperational)
                {
                    if (await _actor.PostAsync(() => _coldResetPending).ConfigureAwait(false)) state = 0;
                    else await ResetConfiguredSlaveAsync(id, token, NmtCommand.ResetCommunication).ConfigureAwait(false);
                }
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
                    CompleteKeepAliveCheck(id, state, failure);
                });
            }
            catch (ObjectDisposedException) { return; }
        });
    }

    private byte ReadCurrentKeepAliveHeartbeat(byte id)
    {
        long arrival = _keepAliveHeartbeatArrival[id];
        long now = _actor.TimeSource.GetTimestamp();
        if (!_bootHeartbeatConfiguration.TryGetValue(id, out var timeout)
            || arrival <= 0 || arrival > now || now >= TickMath.DueAt(arrival, timeout, _actor.TimeSource.Frequency)
            || _nodeGuardingConsumers.ContainsKey(id))
            throw new TimeoutException("No current heartbeat for keep-alive.");
        return _keepAliveHeartbeatState[id];
    }

    private async Task<byte> ProbeKeepAliveStateAsync(byte id, CancellationToken token)
    {
        var probe = new KeepAliveProbe();
        using var poll = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var cancellation = token.Register(() => probe.Completion.TrySetCanceled());
        await _actor.PostAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            _slaveKeepAliveProbes[id] = probe;
        }).ConfigureAwait(false);
        try
        {
            var frame = CanFrame.Classic((int)CanOpenCobId.Heartbeat(id), ReadOnlyMemory<byte>.Empty,
                isExtendedFrame: false, isRemoteFrame: true);
            if (!TryReserveTransmit()) throw new InvalidOperationException("Keep-alive probe suppressed during LSS silence.");
            var send = SendReservedFrame(frame, poll.Token);
            // An adapter may retain its in-flight confirmation despite cancellation. Release
            // this boot's channel/listener immediately; the send still owns its reservation.
            if (await Task.WhenAny(send, probe.Completion.Task).ConfigureAwait(false) != send)
            {
                _ = send.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                await probe.Completion.Task.ConfigureAwait(false);
            }
            var confirmation = await send.ConfigureAwait(false);
            if (!confirmation.Confirmed || confirmation.HostHandoffTimestamp <= 0)
                throw new InvalidOperationException("Keep-alive probe requires confirmed transmission with a host handoff timestamp.");
            await _actor.PostAsync(() =>
            {
                token.ThrowIfCancellationRequested();
                long handoff = confirmation.HostHandoffTimestamp;
                probe.Confirm(handoff, TickMath.DueAt(handoff, TimeSpan.FromMilliseconds(100), _actor.TimeSource.Frequency));
                long now = _actor.TimeSource.GetTimestamp();
                if (now >= probe.Due) probe.Completion.TrySetException(new TimeoutException("No guarding reply within 100 ms."));
                else probe.Deadline = _deadlines.Arm(TimeSpan.FromSeconds((double)(probe.Due - now) / _actor.TimeSource.Frequency),
                    () => probe.Completion.TrySetException(new TimeoutException("No guarding reply within 100 ms.")));
            }).ConfigureAwait(false);
            return await probe.Completion.Task.ConfigureAwait(false);
        }
        finally
        {
            poll.Cancel();
            probe.Completion.TrySetCanceled();
            _ = probe.Completion.Task.Exception;
            RunOnActor(() =>
            {
                probe.Deadline?.Dispose();
                if (ReferenceEquals(_slaveKeepAliveProbes[id], probe)) _slaveKeepAliveProbes[id] = null;
            });
        }
    }

    private void CompleteKeepAliveCheck(byte id, byte state, Exception? failure)
    {
        _slaveKeepAliveChecking[id] = false;
        if (_coldResetPending) return; // ResumeHeldBoot repeats the state check.
        if (failure is not null)
        {
            _slaveKeepAliveFailed[id] = true;
            _slaveBootResults[id] = new SlaveBootResult(id, SlaveBootStatus.KeepAliveFailed, error: failure);
            if (ActiveSlaveBootEnabled && !IsMandatory(id)) ScheduleActiveSlaveRetry(id, FlyingMasterSignal.SlaveKeepAliveFailed);
            else if (IsMandatory(id) || ((ReadStartup() & NmtStartAllNodesBit) != 0 && (ReadStartup() & NmtSuppressSelfStartBit) == 0))
                ApplyBootErrorReaction(new[] { id }, FlyingMasterSignal.SlaveKeepAliveFailed);
            else
            {
                RaiseFlyingMaster(FlyingMasterSignal.SlaveKeepAliveFailed, id, null);
                TryFinishBoot();
            }
            return;
        }
        // Zero here means the reset was deferred, never an approved remote state. The
        // hold may have ended before this callback; its ResumeHeldBoot then saw this worker
        // still running, so explicitly retry now instead of losing the wake-up or approving it.
        if (state == 0)
        {
            ConsiderStart(id, SlaveStateForBoot(id));
            return;
        }
        _slaveKeepAliveChecked[id] = true;
        _slaveInitiallyOperational[id] = state == RequestOperational;
        // An actual guarding response/heartbeat or the reset boot-up established contact.
        _slaveSeen[id] = true;
        if (state != RequestOperational) _slaveConfigurationNeedsStart[id] = true;
        ConsiderStart(id, SlaveStateForBoot(id));
        TryFinishBoot();
    }
}
