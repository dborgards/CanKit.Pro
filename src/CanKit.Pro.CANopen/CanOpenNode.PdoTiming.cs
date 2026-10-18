using System;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Pro.Actor;
using CanKit.Pro.CANopen.Emcy;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Pdo;
using CanKit.Pro.CANopen.Sdo;

namespace CanKit.Pro.CANopen;

// FR-CO-047/048/049: CiA 301 §7.2.5, §7.5.2.7, §7.5.2.22, §7.5.2.35/37.
internal sealed partial class CanOpenNode : ICanOpenPdoMonitoring
{
    private byte _syncCounterOverflow;
    private byte _nextSyncCounter = 1;
    private Task _syncSendChain = Task.CompletedTask;
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
            _nextSyncCounter = 1;
            foreach (var rt in _tpdos)
            {
                if (rt is null) continue;
                rt.SyncStarted = false;
                rt.SyncCounter = 0;
            }
        }
        var window = TimeSpan.FromTicks(_od.ReadUnsigned(Co.SyncWindow, 0) * (TimeSpan.TicksPerMillisecond / 1000));
        if (window != _syncWindow) Interlocked.Increment(ref _syncWindowGeneration);
        _syncWindow = window;
    }

    private Task SendNextSync(CancellationToken cancellationToken = default, bool periodic = false)
    {
        long generation = _syncProducerGeneration;
        return _syncSendChain = SendSyncAfterAsync(_syncSendChain, periodic, generation, cancellationToken);
    }

    private async Task SendSyncAfterAsync(Task previous, bool periodic, long generation, CancellationToken cancellationToken)
    {
        try { await previous.ConfigureAwait(false); }
        catch (Exception) { /* A failed transmission must not stop subsequent SYNCs. */ }
        try
        {
            var send = await _actor.PostAsync(() =>
            {
                if (_disposed != 0) return Task.CompletedTask;
                if (periodic && (generation != _syncProducerGeneration || !_syncGenerate
                    || _state is NmtState.Stopped or NmtState.Initializing)) return Task.CompletedTask;
                byte[] payload = Array.Empty<byte>();
                if (_syncCounterOverflow > 1)
                {
                    payload = new[] { _nextSyncCounter };
                    _nextSyncCounter = _nextSyncCounter >= _syncCounterOverflow ? (byte)1 : (byte)(_nextSyncCounter + 1);
                }
                return SendControlFrame(_syncCobId, payload, cancellationToken, canTransmit: periodic
                    ? () => _disposed == 0 && _state is not (NmtState.Stopped or NmtState.Initializing)
                        && generation == Interlocked.Read(ref _syncProducerGeneration)
                    : null);
            }, cancellationToken).ConfigureAwait(false);
            await send.ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (_disposed != 0) { }
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
            if (_emcyValid)
                _ = EmitEmcy(new EmcyMessage(_nodeId, 0x8240, (byte)_od.ReadUnsigned(Co.ErrorRegister, 0)));
            return false;
        }
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
        rp.Deadline?.Dispose();
        rp.Deadline = null;
        if (rp.EventTimer == TimeSpan.Zero) return;
        rp.Deadline = _actor.Schedule(rp.EventTimer, () =>
        {
            rp.Deadline = null;
            if (_disposed != 0 || _state != NmtState.Operational || !rp.Valid) return;
            var args = new RpdoTimeoutEventArgs(rp.PdoIndex, rp.CobId, rp.EventTimer);
            EnqueueEvent(() =>
            {
                try { DeliverToSubscribers(RpdoTimeout, args); }
                catch (Exception ex) { RaiseBackgroundException(ex); }
            }, critical: true, EventKey.RpdoTimeout(rp.PdoIndex), emcyProducer: -1, producer: EventKey.RpdoProducerSlot(rp.PdoIndex));
        });
    }
}
