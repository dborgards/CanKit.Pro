using System;
using System.Collections.Generic;
using System.Diagnostics;
using CanKit.Pro.Actor;

namespace CanKit.Pro.Uds;

/// <summary>
/// The response windows still open from earlier sends, per service: how long a peer may still
/// answer a request whose answer nobody is waiting for -- a suppressed send, a functional
/// request whose collection window ended before the peer's P2 did, or, with
/// <see cref="UdsClientOptions.QuarantineLateResponses"/>, a physical request that timed out or
/// was cancelled (#340). The next request for the
/// same service waits such a window out rather than taking the late answer as its own.
/// One entry per service, since a send for another service in between must not
/// shorten the first's window; a later window for the same service replaces an earlier one
/// only if it ends later.
/// </summary>
internal sealed class SuppressedResponseWindows
{
    private readonly Dictionary<byte, long> _until = new();
    private readonly Dictionary<byte, TimeSpan> _pendingExtension = new();
    private readonly object _gate = new();

    /// <summary>Notes that a request for <paramref name="sid"/> went out at
    /// <paramref name="sentTimestamp"/> and may be answered for <paramref name="window"/>.</summary>
    public void Note(byte sid, long sentTimestamp, TimeSpan window)
        => Note(sid, sentTimestamp, window, Stopwatch.Frequency);

    /// <summary>
    /// As <see cref="Note(byte, long, TimeSpan)"/>, counting <paramref name="window"/> in
    /// <paramref name="ticksPerSecond"/> so the deadline is on the same clock as
    /// <paramref name="sentTimestamp"/> (#171).
    /// </summary>
    public void Note(byte sid, long sentTimestamp, TimeSpan window, long ticksPerSecond)
        => Extend(sid, sentTimestamp + Ticks(window, ticksPerSecond));

    /// <summary>
    /// As <see cref="Note(byte, long, TimeSpan, long)"/>, noting with the window how far an NRC
    /// 0x78 heard inside it moves it out: the P2* in force when the request went out. The
    /// physical client's P2* changes with the session (#340), and a window opened under a longer
    /// one must not be moved out by a shorter one negotiated since -- the late answer still has
    /// the P2* its own session gave it (Codex on #382). A window noted again while it is still
    /// open keeps the longer; one that had already closed when the new request went out passes
    /// nothing on, so the new window has its own -- a window left to run out unconsumed, a
    /// suppressed send with no request for its service after it, must not lend a later session
    /// its P2* (Codex on #382).
    /// </summary>
    public void Note(byte sid, long sentTimestamp, TimeSpan window, long ticksPerSecond, TimeSpan pendingExtension)
    {
        lock (_gate)
        {
            // Open as ExtendIfOpenAt counts it: up to and including its deadline.
            bool stillOpen = _until.TryGetValue(sid, out var previousUntil) && sentTimestamp <= previousUntil;
            Note(sid, sentTimestamp, window, ticksPerSecond);
            if (!stillOpen || !_pendingExtension.TryGetValue(sid, out var kept) || kept < pendingExtension)
                _pendingExtension[sid] = pendingExtension;
        }
    }

    /// <summary>How far an NRC 0x78 heard inside the window for <paramref name="sid"/> moves it
    /// out, if that was noted with it.</summary>
    public bool TryGetPendingExtension(byte sid, out TimeSpan extension)
    {
        lock (_gate) return _pendingExtension.TryGetValue(sid, out extension);
    }

    internal static long Ticks(TimeSpan window, long ticksPerSecond)
        => TickMath.ToTicks(window, ticksPerSecond);

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
    /// <summary>
    /// Extends the window for <paramref name="sid"/> to <paramref name="until"/> only if one is
    /// noted and was still open at <paramref name="arrival"/>: a 0x78 that arrived after a
    /// window's deadline answers nothing the window still covers, and must not revive it
    ///.
    /// </summary>
    public void ExtendIfOpenAt(byte sid, long arrival, long until)
    {
        lock (_gate)
        {
            if (_until.TryGetValue(sid, out var existing) && arrival <= existing && existing < until)
                _until[sid] = until;
        }
    }

    /// <summary>
    /// Puts the window for <paramref name="sid"/> back to what <see cref="TryGetDeadline"/>
    /// reported before a note that turned out to be for nothing -- a send the channel refused
    /// before transmitting.
    /// </summary>
    public void Restore(byte sid, bool had, long until)
    {
        lock (_gate)
        {
            if (had) _until[sid] = until;
            else
            {
                _until.Remove(sid);
                _pendingExtension.Remove(sid);
            }
        }
    }

    /// <summary>
    /// As <see cref="Restore(byte, bool, long)"/>, putting back what
    /// <see cref="TryGetPendingExtension"/> reported before the note as well.
    /// </summary>
    public void Restore(byte sid, bool had, long until, bool hadExtension, TimeSpan extension)
    {
        lock (_gate)
        {
            Restore(sid, had, until);
            if (hadExtension) _pendingExtension[sid] = extension;
            else _pendingExtension.Remove(sid);
        }
    }

    public bool TryGetDeadline(byte sid, out long until)
    {
        lock (_gate) return _until.TryGetValue(sid, out until);
    }

    /// <summary>Closes the window for <paramref name="sid"/>.</summary>
    public void Forget(byte sid)
    {
        lock (_gate)
        {
            _until.Remove(sid);
            _pendingExtension.Remove(sid);
        }
    }

    /// <summary>How long from now until <paramref name="until"/>, or zero if it has passed.</summary>
    public static TimeSpan Remaining(long until)
        => Remaining(until, Stopwatch.GetTimestamp(), Stopwatch.Frequency);

    /// <summary>
    /// As <see cref="Remaining(long)"/>, measured from <paramref name="now"/> on a clock whose
    /// frequency is <paramref name="ticksPerSecond"/>.
    /// </summary>
    public static TimeSpan Remaining(long until, long now, long ticksPerSecond)
    {
        var ticks = until - now;
        return ticks <= 0 ? TimeSpan.Zero : TickMath.RemainingFromTicks(ticks, ticksPerSecond);
    }
}
