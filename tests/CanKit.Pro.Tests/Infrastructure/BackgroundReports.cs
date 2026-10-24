using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CanKit.Pro.Tests.Infrastructure;

/// <summary>
/// Collects the reports a protocol layer raises on <c>BackgroundExceptionOccurred</c>, and lets a
/// test wait for the one it expects.
/// </summary>
/// <remarks>
/// J1939-TP, ISO-TP, the J1939 node and the CANopen node deliver those reports on the thread pool
/// (#356, T-2), so a report can reach its handlers after the call that caused it -- a failed
/// transfer, a <c>Dispose</c> -- has returned. A list read straight after that call is a race;
/// <see cref="WaitForAsync"/> is a signal. Reports from one layer arrive in the order they were
/// made, so once the awaited one is here, every one made before it is too.
/// </remarks>
internal sealed class BackgroundReports
{
    private static readonly TimeSpan DefaultHangGuard = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly List<Exception> _seen = new();
    private readonly List<(Func<Exception, bool> Match, TaskCompletionSource<Exception> Signal)> _waiters = new();

    /// <summary>The handler to subscribe: <c>layer.BackgroundExceptionOccurred += reports.Add;</c></summary>
    public void Add(object? sender, Exception exception)
    {
        List<TaskCompletionSource<Exception>> hit;
        lock (_gate)
        {
            _seen.Add(exception);
            hit = _waiters.Where(w => w.Match(exception)).Select(w => w.Signal).ToList();
            _waiters.RemoveAll(w => hit.Contains(w.Signal));
        }

        foreach (var signal in hit) signal.TrySetResult(exception);
    }

    /// <summary>Every report seen so far, in the order it arrived.</summary>
    public IReadOnlyList<Exception> Seen
    {
        get { lock (_gate) return _seen.ToList(); }
    }

    /// <summary>
    /// Completes with the first report, seen already or arriving later, that
    /// <paramref name="match"/> accepts. <paramref name="hangGuard"/> bounds a hang and measures
    /// nothing: a report that never comes fails the wait with <see cref="TimeoutException"/>.
    /// </summary>
    public async Task<Exception> WaitForAsync(Func<Exception, bool> match, TimeSpan? hangGuard = null)
    {
        var signal = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            var already = _seen.FirstOrDefault(match);
            if (already is not null) return already;
            _waiters.Add((match, signal));
        }

        var guard = hangGuard ?? DefaultHangGuard;
        if (await Task.WhenAny(signal.Task, Task.Delay(guard)).ConfigureAwait(false) != signal.Task)
            throw new TimeoutException($"No matching BackgroundExceptionOccurred report within {guard}.");
        return await signal.Task.ConfigureAwait(false);
    }
}
