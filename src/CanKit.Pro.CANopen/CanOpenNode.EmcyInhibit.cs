using System;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Pro.Actor;
using CanKit.Pro.CANopen.Nmt;

namespace CanKit.Pro.CANopen;

// CiA 301 §4.4.1, §7.5.2.18: the minimum interval between EMCY transmission service
// invocations. Count conservatively from send completion, so delayed driver handoffs or
// echo confirmations cannot compress the actual invocation interval.
// Optional 1029h also uses this queue's Stopped/reset gate, without enabling inhibit timing, and so
// does a profile EMCY released behind a boot-up, whatever the options (EmitEmcy, #549). Every other
// EMCY uses its Stopped wait, without the reset drop (SendWhenNotStoppedAsync, #565).
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

    /// <summary>
    /// The step of an EMCY that takes no gated path — neither EMCY inhibit nor error behaviour is
    /// enabled, and it is not a profile's. It runs on the actor in the turn the chain gives it and
    /// checks the NMT state then: in Stopped it waits in <see cref="WaitForEmcySlot"/>, which without
    /// inhibit time waits for nothing else, until the node has left Stopped, as the gated path does
    /// (CiA 301 v4.2.0 §7.3.2.2.4, Table 37: no EMCY in Stopped; #565). Its task completes once the
    /// frame has gone out, and cancelling its token while it waits — for the node to leave Stopped,
    /// or for a boot-up (below) — ends it without a frame or a <c>1001h</c> write.
    /// <para>
    /// An NMT reset ends the wait, and what happens then depends on who ordered the EMCY. Two kinds,
    /// and this is where the rule lives:
    /// </para>
    /// <list type="bullet">
    /// <item>An EMCY the application ordered with <c>SendEmcyAsync</c> survives the reset, as an EMCY
    /// queued across a reset always has (3eb49f3e): the step looks again once the reset is done and
    /// goes out on the COB-ID active then (<c>transmit</c> reads it), behind the boot-up that reset
    /// ordered, so that it does not overtake it.</item>
    /// <item>An EMCY the node raises for its own error state (<paramref name="dropOnReset"/>: a
    /// communication error and its <c>0000h</c>) is dropped, as the gated path drops it: the reset
    /// ended the error it reports without an EMCY (Figure 37, transitions 5 and 0). It writes neither
    /// <c>1001h</c> nor <c>1003h</c>; the reset has written <c>1001h</c> itself.</item>
    /// </list>
    /// <para>
    /// Only a node EMCY that waited in Stopped is dropped. One merely queued behind a slow send when
    /// the reset comes is decided after the reset, as before this rule existed.
    /// </para>
    /// </summary>
    private async Task SendWhenNotStoppedAsync(Func<Task> transmit, CancellationToken cancellationToken, bool dropOnReset,
        bool waited = false)
    {
        // No disposal check of its own, as on the gated path: a step that runs after the disposal finds
        // the actor refusing the redispatch (DispatchQueuedEmcy) or the send refusing the frame.
        var slot = WaitForEmcySlot(cancellationToken);
        if (!slot.IsCompleted)
        {
            // True: the node left Stopped. False: a reset or the disposal ended the wait. A node EMCY
            // ends there; any other step looks again, on the actor.
            if (!await slot.ConfigureAwait(false) && dropOnReset) return;
            await DispatchQueuedEmcy(() => SendWhenNotStoppedAsync(transmit, cancellationToken, dropOnReset, waited: true)).ConfigureAwait(false);
            return;
        }
        // Completed at once: not Stopped (true), or a token cancelled already, which throws here.
        await slot.ConfigureAwait(false);
        if (waited && !_bootUpSend.IsCompleted)
        {
            HeldEmcysWaitForBootUpForTests?.Invoke();
            await WaitForBootUpSendAsync(_bootUpSend, cancellationToken).ConfigureAwait(false);
            await DispatchQueuedEmcy(() => SendWhenNotStoppedAsync(transmit, cancellationToken, dropOnReset, waited: true)).ConfigureAwait(false);
            return;
        }
        await transmit().ConfigureAwait(false);
    }

    /// <summary>Completes when <paramref name="bootUp"/>'s send has ended, however it ended, or
    /// throws <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is
    /// cancelled first. Only the wait is cancelled, never the boot-up (Codex on #574).</summary>
    private static async Task WaitForBootUpSendAsync(Task bootUp, CancellationToken cancellationToken)
    {
        var sent = bootUp.ContinueWith(static ended => { _ = ended.Exception; },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(() => cancelled.TrySetCanceled(cancellationToken)))
            await (await Task.WhenAny(sent, cancelled.Task).ConfigureAwait(false)).ConfigureAwait(false);
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
        // Build the actor work once, outside the registration callback. Nesting these lambdas
        // generates a lazy delegate-cache branch that cannot recur: cancellation runs only once.
        Action cancel = () =>
        {
            if (!ReferenceEquals(waiting, _emcyInhibitWait)) return;
            ReleaseEmcyInhibitWait();
            waiting.Completion.TrySetCanceled(cancellationToken);
        };
        waiting.Cancellation = cancellationToken.Register(() => RunOnActor(cancel));
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
