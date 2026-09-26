using System;
using CanKit.Pro.Actor;

namespace CanKit.Pro.CANopen.Heartbeat;

/// <summary>
/// Sends this node's heartbeat on <c>1017h</c>'s interval. It does not watch anyone, and it
/// does not know whether a <see cref="HeartbeatConsumer"/> exists.
/// </summary>
/// <remarks>
/// Actor loop only. The object dictionary stays the configuration: the node reads <c>1017h</c>
/// and calls <see cref="Apply"/>. A flying master starts the producer the same way, by writing
/// <c>1017h</c>, rather than by owning a second timer.
/// </remarks>
internal interface IHeartbeatProducer : IDisposable
{
    /// <summary>The interval currently in force. <see cref="TimeSpan.Zero"/> means the producer is off.</summary>
    TimeSpan Interval { get; }

    /// <summary>
    /// Applies a new interval. Zero stops the producer. The same positive interval, already
    /// running, is left on its current period.
    /// </summary>
    /// <returns>True when a producer is now running and the schedule actually changed.</returns>
    bool Apply(TimeSpan interval);

    /// <summary>
    /// Drops the pending tick and arms a full period from now. Used after boot-up so the
    /// cycle starts from that heartbeat.
    /// </summary>
    void RestartCycle();
}

/// <summary>Default <see cref="IHeartbeatProducer"/>: one actor timer, rescheduled from its own callback.</summary>
internal sealed class HeartbeatProducer : IHeartbeatProducer
{
    private readonly IProtocolActor _actor;
    private readonly Func<bool> _canEmit;
    private readonly Action _emit;
    private IDisposable? _handle;
    private TimeSpan _interval;

    // A callback already handed to the actor can still run after <see cref="Apply"/> disposes
    // its handle. The generation keeps that callback from emitting and from arming a second tick.
    private int _generation;

    public HeartbeatProducer(IProtocolActor actor, Func<bool> canEmit, Action emit)
    {
        _actor = actor ?? throw new ArgumentNullException(nameof(actor));
        _canEmit = canEmit ?? throw new ArgumentNullException(nameof(canEmit));
        _emit = emit ?? throw new ArgumentNullException(nameof(emit));
    }

    public TimeSpan Interval => _interval;

    public bool Apply(TimeSpan interval)
    {
        if (interval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "A heartbeat interval cannot be negative.");
        if (interval == _interval && (interval == TimeSpan.Zero || _handle is not null))
            return false;

        _generation++;
        _handle?.Dispose();
        _handle = null;
        _interval = interval;
        if (interval <= TimeSpan.Zero)
            return false;
        Schedule();
        return true;
    }

    public void RestartCycle()
    {
        if (_interval <= TimeSpan.Zero) return;
        _generation++;
        _handle?.Dispose();
        _handle = null;
        Schedule();
    }

    public void Dispose()
    {
        _generation++;
        _handle?.Dispose();
        _handle = null;
        _interval = TimeSpan.Zero;
    }

    private void Schedule()
    {
        if (_interval <= TimeSpan.Zero) return;
        int generation = _generation;
        _handle = _actor.Schedule(_interval, () =>
        {
            try
            {
                if (generation != _generation || !_canEmit()) return;
                _emit();
            }
            finally
            {
                if (generation == _generation && _canEmit() && _interval > TimeSpan.Zero)
                    Schedule();
            }
        });
    }
}
