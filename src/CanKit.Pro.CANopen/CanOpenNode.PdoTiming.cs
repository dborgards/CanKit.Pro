using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Pro.Actor;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Pdo;
using CanKit.Pro.CANopen.Sdo;

namespace CanKit.Pro.CANopen;

// FR-CO-052/053/054: CiA 301 §7.2.5, §7.5.2.7, §7.5.2.22, §7.5.2.35/37.
internal sealed partial class CanOpenNode : ICanOpenPdoMonitoring
{
    private byte _syncCounterOverflow;
    private byte _nextSyncCounter = 1;
    private long _syncCounterGeneration;
    private uint _configuredSyncCyclePeriod;
    private Task _syncSendChain = Task.CompletedTask;
    internal Task SyncSendChainForTests => _syncSendChain;
    private long _syncProducerGeneration;
    private TimeSpan _syncWindow;
    private long _lastSync;
    private bool _hasSync;
    private long _syncWindowGeneration;

    /// <inheritdoc />
    public event EventHandler<RpdoTimeoutEventArgs>? RpdoTimeout;

    private OdWriteDecision ValidateSyncCounterOverflow(byte value)
    {
        if (value == 1 || value > 240) return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);
        if (value != _od.ReadUnsigned(Co.SyncCounterOverflow, 0) && _od.ReadUnsigned(Co.CyclePeriod, 0) != 0)
            return OdWriteDecision.Reject(SdoAbortCode.DataCannotBeTransferredDeviceState);
        return OdWriteDecision.Accept;
    }

    private void ApplySyncTimingConfiguration()
    {
        byte overflow = (byte)_od.ReadUnsigned(Co.SyncCounterOverflow, 0);
        if (_syncCounterOverflow != overflow)
        {
            _syncCounterOverflow = overflow;
            ResetSyncCounter();
            foreach (var rt in _tpdos.Where(rt => rt is not null))
            {
                rt!.SyncStarted = false;
                rt.SyncCounter = 0;
            }
        }
        var window = TimeSpan.FromTicks(_od.ReadUnsigned(Co.SyncWindow, 0) * (TimeSpan.TicksPerMillisecond / 1000));
        if (window != _syncWindow) Interlocked.Increment(ref _syncWindowGeneration);
        _syncWindow = window;
        EndCommunicationError(CommunicationError.SyncLength);
    }

    private void ResetSyncCounter()
    {
        _nextSyncCounter = 1;
        _syncCounterGeneration++;
    }

    private Task SendNextSync(CancellationToken cancellationToken = default, bool periodic = false)
    {
        // There is no value in retaining missed periodic ticks: after a driver stall the
        // next tick resumes the cadence. Manual requests still retain their send order.
        if (periodic && !_syncSendChain.IsCompleted) return Task.CompletedTask;
        long generation = _syncProducerGeneration;
        long queuedAt = _actor.TimeSource.GetTimestamp();
        var period = _syncProducerInterval;
        return _syncSendChain = SendSyncAfterAsync(_syncSendChain, periodic, generation, queuedAt, period, cancellationToken);
    }

    private async Task SendSyncAfterAsync(Task previous, bool periodic, long generation,
        long queuedAt, TimeSpan period, CancellationToken cancellationToken)
    {
        // Observe a preceding failure without carrying it into the next request. The wire
        // helper already reports transport failures; cancellation belongs to its caller.
        await previous.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).ConfigureAwait(false);
        try
        {
            byte counter = 0;
            byte overflow = 0;
            long counterGeneration = 0;
            var send = await _actor.PostAsync(() =>
            {
                // SendControlFrame checks disposal under the shared transmit gate.
                bool CanSendPeriodic()
                {
                    if (_disposed != 0 || !_syncGenerate) return false;
                    if (_state is NmtState.Stopped or NmtState.Initializing) return false;
                    if (generation != Interlocked.Read(ref _syncProducerGeneration)) return false;
                    return TickMath.ElapsedFromTicks(_actor.TimeSource.GetTimestamp() - queuedAt, _actor.TimeSource.Frequency) < period;
                }
                if (periodic && !CanSendPeriodic()) return Task.FromResult(false);
                byte[] payload = Array.Empty<byte>();
                if (_syncCounterOverflow > 1)
                {
                    counter = _nextSyncCounter;
                    overflow = _syncCounterOverflow;
                    counterGeneration = _syncCounterGeneration;
                    payload = new[] { counter };
                }
                return SendControlFrame(_syncCobId, payload, cancellationToken,
                    canTransmit: periodic ? CanSendPeriodic : null);
            }, cancellationToken).ConfigureAwait(false);
            if (await send.ConfigureAwait(false) && counter != 0)
            {
                // The chain does not release the next request until this actor commit. A
                // cancelled/rejected send leaves the value available; a configuration reset
                // retires this send's counter generation even if its confirmation arrives late.
                await _actor.PostAsync(() =>
                {
                    if (counterGeneration != _syncCounterGeneration) return;
                    _nextSyncCounter = counter >= overflow ? (byte)1 : (byte)(counter + 1);
                }).ConfigureAwait(false);
            }
        }
        catch (ObjectDisposedException) when (_disposed != 0)
        {
            // Disposal retired the actor before this queued request could run.
            return;
        }
    }

    private bool IsInsideSyncWindow()
        => _syncWindow == TimeSpan.Zero || (_hasSync
            && TickMath.ElapsedFromTicks(_actor.TimeSource.GetTimestamp() - _lastSync, _actor.TimeSource.Frequency) < _syncWindow);

    private bool TryAcceptSync(byte[] payload, out byte? counter)
    {
        counter = null;
        int length = _syncCounterOverflow > 1 ? 1 : 0;
        if (payload.Length != length)
        {
            RaiseCommunicationError(CommunicationError.SyncLength, 0, 0x8240);
            return false;
        }
        EndCommunicationError(CommunicationError.SyncLength);
        if (length != 0)
        {
            // A consumer ignores its configured overflow value (§7.5.2.22), but reserved
            // counter values are not a SYNC counter (§7.2.5.3.1).
            if (payload[0] is 0 or > 240) return false;
            counter = payload[0];
        }
        _lastSync = _actor.TimeSource.GetTimestamp();
        _hasSync = true;
        Interlocked.Increment(ref _syncWindowGeneration);
        return true;
    }

    private Task SendPdoPayload(TpdoRuntime rt, byte[] payload)
    {
        if (_syncWindow == TimeSpan.Zero || (!CanOpenTransmissionType.IsSynchronous(rt.TransmissionType)
            && rt.TransmissionType != CanOpenTransmissionType.RtrOnlySynchronous))
            return SendControlFrame(rt.CobId, payload);
        long generation = _syncWindowGeneration;
        long start = _lastSync;
        var window = _syncWindow;
        // Check again at the send hand-off: a congested thread pool must not send a queued
        // synchronous PDO after its window has closed or a later SYNC superseded its sample.
        return SendControlFrame(rt.CobId, payload, canTransmit: () => _disposed == 0
            && _state == NmtState.Operational && generation == Interlocked.Read(ref _syncWindowGeneration)
            && TickMath.ElapsedFromTicks(_actor.TimeSource.GetTimestamp() - start, _actor.TimeSource.Frequency) < window);
    }

    private void RestartRpdoDeadline(RpdoRuntime rp)
    {
        // A reception ends the timeout the deadline reported.
        EndCommunicationError(CommunicationError.RpdoTimeout, rp.PdoIndex);
        rp.Deadline?.Dispose();
        rp.Deadline = null;
        if (rp.EventTimer == TimeSpan.Zero) return;
        rp.Deadline = _actor.Schedule(rp.EventTimer, () =>
        {
            rp.Deadline = null;
            if (_disposed != 0 || _state != NmtState.Operational || !rp.Valid) return;
            RaiseCommunicationError(CommunicationError.RpdoTimeout, rp.PdoIndex, 0x8250);
            var args = new RpdoTimeoutEventArgs(rp.PdoIndex, rp.CobId, rp.EventTimer);
            EnqueueEvent(() => DeliverToSubscribers(RpdoTimeout, args), critical: true, EventKey.RpdoTimeout(rp.PdoIndex), emcyProducer: -1, producer: EventKey.RpdoProducerSlot(rp.PdoIndex));
        });
    }
}
