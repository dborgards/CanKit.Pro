using System;
using System.Threading;
using System.Threading.Tasks;

namespace CanKit.Pro.Actor;

/// <summary>A cancellation deadline driven by the same clock as the operation it bounds.</summary>
/// <remarks>
/// The clock-driven forms (<see cref="ClockTimeout(ProtocolActor, long)"/> and
/// <see cref="WaitUntilAsync"/>) are timers on <c>clock</c>, and disposing that actor discards
/// every timer not yet due. After that, <see cref="Token"/> is never cancelled and
/// <see cref="WaitUntilAsync"/> never completes on its own: only the caller's token can end
/// the wait. A caller whose operation may outlive the actor must therefore link a token that
/// the actor's owner cancels on disposal (a lifetime token), as <c>UdsClientImpl</c> and
/// <c>LssMaster</c> do; the deadline alone does not bound it. On an actor that is already
/// disposed, arming the timer is refused instead: the constructor throws
/// <see cref="ObjectDisposedException"/>, and <see cref="WaitUntilAsync"/> returns a task faulted
/// with it unless the deadline has already passed.
/// </remarks>
internal sealed class ClockTimeout : IDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly IDisposable? _timer;

    internal ClockTimeout(TimeSpan remaining)
        => _cts = new CancellationTokenSource(remaining);

    internal ClockTimeout(ProtocolActor clock, long deadline)
    {
        _cts = new CancellationTokenSource();
        // Cancellation runs channel registrations. Dispatch it off the actor so a continuation
        // cannot block the loop that must deliver the next frame or timer.
        _timer = clock.ScheduleAt(deadline, () => ThreadPool.QueueUserWorkItem(static state =>
            ((CancellationTokenSource)state!).Cancel(), _cts));
    }

    internal CancellationToken Token => _cts.Token;

    internal bool IsCancellationRequested => _cts.IsCancellationRequested;

    internal static async Task WaitUntilAsync(ProtocolActor clock, long deadline, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (clock.TimeSource.GetTimestamp() >= deadline) return;
        var elapsed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => elapsed.TrySetCanceled(cancellationToken));
        using var timer = clock.ScheduleAt(deadline, () => elapsed.TrySetResult(true));
        await elapsed.Task.ConfigureAwait(false);
    }

    public void Dispose()
    {
        // A scheduled cancellation may already be queued on the pool. Its source has no timer
        // or linked registrations to release and must remain usable until that callback runs.
        if (_timer is null) _cts.Dispose();
        else _timer.Dispose();
    }
}
