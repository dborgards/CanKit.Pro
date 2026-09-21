using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace CanKit.Pro.Uds;

/// <summary>
/// The response windows still open after suppressed sends, per service. A suppressed send
/// draws no positive response but may still draw a negative one, up to P2 after it went out;
/// the next request for the same service waits that window out rather than taking the
/// negative response as its own (Codex on #150). One entry per service, since a suppressed
/// send for another service in between must not shorten the first's window.
/// </summary>
internal sealed class SuppressedResponseWindows
{
    private readonly Dictionary<byte, long> _until = new();
    private readonly object _gate = new();

    /// <summary>Notes that a suppressed send for <paramref name="sid"/> went out at
    /// <paramref name="sentTimestamp"/> and may be answered negatively for <paramref name="window"/>.</summary>
    public void Note(byte sid, long sentTimestamp, TimeSpan window)
    {
        var until = sentTimestamp + (long)(window.TotalSeconds * Stopwatch.Frequency);
        lock (_gate)
        {
            if (!_until.TryGetValue(sid, out var existing) || existing < until)
                _until[sid] = until;
        }
    }

    /// <summary>Waits until every window for <paramref name="sid"/> has closed, then forgets it.</summary>
    public async Task WaitOutAsync(byte sid, CancellationToken cancellationToken)
    {
        long until;
        lock (_gate)
        {
            if (!_until.TryGetValue(sid, out until)) return;
        }
        var ticks = until - Stopwatch.GetTimestamp();
        if (ticks > 0)
            await Task.Delay(TimeSpan.FromSeconds((double)ticks / Stopwatch.Frequency), cancellationToken)
                .ConfigureAwait(false);
        lock (_gate)
        {
            if (_until.TryGetValue(sid, out var current) && current == until) _until.Remove(sid);
        }
    }
}
