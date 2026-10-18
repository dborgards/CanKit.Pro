using System;
using System.Threading;
using System.Threading.Tasks;

namespace CanKit.Pro.Actor;

/// <summary>A cancellation deadline driven by the same clock as the operation it bounds.</summary>
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
        using var registration = cancellationToken.Register(() => elapsed.TrySetCanceled());
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
