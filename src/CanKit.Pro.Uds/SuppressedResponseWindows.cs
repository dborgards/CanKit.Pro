using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace CanKit.Pro.Uds;

/// <summary>
/// The response windows still open from earlier sends, per service: how long a peer may still
/// answer a request whose answer nobody is waiting for -- a suppressed send, or a functional
/// request whose collection window ended before the peer's P2 did. The next request for the
/// same service waits such a window out rather than taking the late answer as its own (Codex
/// on #150). One entry per service, since a send for another service in between must not
/// shorten the first's window; a later window for the same service replaces an earlier one
/// only if it ends later.
/// </summary>
internal sealed class SuppressedResponseWindows
{
    private readonly Dictionary<byte, long> _until = new();
    private readonly object _gate = new();

    /// <summary>Notes that a request for <paramref name="sid"/> went out at
    /// <paramref name="sentTimestamp"/> and may be answered for <paramref name="window"/>.</summary>
    public void Note(byte sid, long sentTimestamp, TimeSpan window)
        => Extend(sid, sentTimestamp + (long)(window.TotalSeconds * Stopwatch.Frequency));

    /// <summary>Moves the window for <paramref name="sid"/> out to <paramref name="until"/>, if later.</summary>
    public void Extend(byte sid, long until)
    {
        lock (_gate)
        {
            if (!_until.TryGetValue(sid, out var existing) || existing < until)
                _until[sid] = until;
        }
    }

    /// <summary>The instant the window for <paramref name="sid"/> ends, if one is open.</summary>
    public bool TryGetDeadline(byte sid, out long until)
    {
        lock (_gate) return _until.TryGetValue(sid, out until);
    }

    /// <summary>Closes the window for <paramref name="sid"/>.</summary>
    public void Forget(byte sid)
    {
        lock (_gate) _until.Remove(sid);
    }

    /// <summary>How long from now until <paramref name="until"/>, or zero if it has passed.</summary>
    public static TimeSpan Remaining(long until)
    {
        var ticks = until - Stopwatch.GetTimestamp();
        return ticks <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)ticks / Stopwatch.Frequency);
    }
}
