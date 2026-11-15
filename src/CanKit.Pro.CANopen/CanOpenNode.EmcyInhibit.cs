using System;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Pro.Actor;
using CanKit.Pro.CANopen.Nmt;

namespace CanKit.Pro.CANopen;

// CiA 301 §4.4.1, §7.5.2.18: the minimum interval between EMCY transmission service
// invocations. Count conservatively from send completion, so delayed driver handoffs or
// echo confirmations cannot compress the actual invocation interval.
internal sealed partial class CanOpenNode
{
    private object _emcyInhibitEpoch = new();
    private EmcyCompletion? _emcyLastCompletion;
    private EmcyInhibitWait? _emcyInhibitWait;
    private TimeSpan _emcyInhibitTime;

    private sealed class EmcyCompletion
    {
        internal EmcyCompletion(object epoch, long timestamp) { Epoch = epoch; Timestamp = timestamp; }
        internal readonly object Epoch;
        internal readonly long Timestamp;
    }

    private sealed class EmcyInhibitWait
    {
        internal readonly TaskCompletionSource<bool> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal IDisposable? Timer;
        internal CancellationTokenRegistration Cancellation;
    }

    private void ApplyEmcyInhibitConfiguration()
    {
        if (!_options.EnableEmcyInhibit) return;
        _emcyInhibitTime = TimeSpan.FromTicks(_od.ReadUnsigned(Co.EmcyInhibitTime, 0) * 1000L);
        WakeEmcyInhibitWait();
    }

    private async Task RecordEmcyCompletionAsync(Task send, object epoch)
    {
        try { await send.ConfigureAwait(false); }
        finally
        {
            // A late pre-reset completion may publish its old stamp, but no new epoch uses it.
            Volatile.Write(ref _emcyLastCompletion, new EmcyCompletion(epoch, _actor.TimeSource.GetTimestamp()));
        }
    }

    private async Task SendWithEmcyInhibitAsync(Func<Task> transmit, CancellationToken cancellationToken, object epoch)
    {
        if (!ReferenceEquals(epoch, _emcyInhibitEpoch)) return;
        if (!await WaitForEmcySlot(cancellationToken).ConfigureAwait(false)) return;
        if (!_actor.IsOnCurrentActor)
        {
            // Recheck configuration/state on the actor after waking: they may have changed
            // again while the continuation was waiting to run.
            await DispatchQueuedEmcy(() => SendWithEmcyInhibitAsync(transmit, cancellationToken, epoch)).ConfigureAwait(false);
            return;
        }
        await transmit().ConfigureAwait(false);
    }

    private Task<bool> WaitForEmcySlot(CancellationToken cancellationToken)
    {
        // Disabled producers still run the existing register-only step without waiting.
        if (!_emcyValid || _emcyCobId == 0) return Task.FromResult(true);
        TimeSpan remaining = TimeSpan.Zero;
        var previous = Volatile.Read(ref _emcyLastCompletion);
        if (previous is not null && ReferenceEquals(previous.Epoch, _emcyInhibitEpoch))
            remaining = _emcyInhibitTime - TickMath.ElapsedFromTicks(
                _actor.TimeSource.GetTimestamp() - previous.Timestamp, _actor.TimeSource.Frequency);
        if (_state != NmtState.Stopped && remaining <= TimeSpan.Zero) return Task.FromResult(true);
        var waiting = new EmcyInhibitWait();
        _emcyInhibitWait = waiting;
        waiting.Cancellation = cancellationToken.Register(() => RunOnActor(() =>
        {
            if (!ReferenceEquals(waiting, _emcyInhibitWait)) return;
            ReleaseEmcyInhibitWait();
            waiting.Completion.TrySetCanceled(cancellationToken);
        }));
        // Register may have run inline for an already cancelled token.
        if (waiting.Completion.Task.IsCompleted) waiting.Cancellation.Dispose();
        else if (_state != NmtState.Stopped)
            waiting.Timer = _actor.Schedule(remaining, WakeEmcyInhibitWait);
        return waiting.Completion.Task;
    }

    private EmcyInhibitWait? ReleaseEmcyInhibitWait()
    {
        var waiting = _emcyInhibitWait;
        _emcyInhibitWait = null;
        if (waiting is not null)
        {
            waiting.Timer?.Dispose();
            waiting.Cancellation.Dispose();
        }
        return waiting;
    }

    private void WakeEmcyInhibitWait() => ReleaseEmcyInhibitWait()?.Completion.TrySetResult(true);

    private void ResetEmcyInhibit()
    {
        _emcyInhibitEpoch = new object();
        Volatile.Write(ref _emcyLastCompletion, null);
        ReleaseEmcyInhibitWait()?.Completion.TrySetResult(false);
    }
}
