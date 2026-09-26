using System;
using System.Collections.Generic;
using CanKit.Pro.Reliability;

namespace CanKit.Pro.CANopen.Heartbeat;

/// <summary>
/// Watches heartbeat producers named by <c>1016h</c>. It does not send heartbeats, and it does
/// not know whether a <see cref="HeartbeatProducer"/> exists.
/// </summary>
/// <remarks>
/// Actor loop only. Each watched node-id has its own deadline, armed as soon as the watch is
/// installed, so a producer that never appears times out too. A received heartbeat rearms that
/// one watch. <see cref="TimedOut"/> is the only signal; a flying master subscribes to it
/// instead of sharing the producer's timer.
/// </remarks>
internal interface IHeartbeatConsumer : IDisposable
{
    /// <summary>True when <paramref name="producerNodeId"/> is in the current watch set.</summary>
    bool IsWatching(byte producerNodeId);

    /// <summary>
    /// Replaces the watch set. An entry whose node-id and timeout are unchanged keeps its
    /// deadline. Everything else is cancelled, and each new entry is armed immediately.
    /// </summary>
    void Replace(IReadOnlyDictionary<byte, TimeSpan> watches);

    /// <summary>A heartbeat arrived from <paramref name="producerNodeId"/>. Rearms that watch when one exists.</summary>
    void NoteReceived(byte producerNodeId);

    /// <summary>
    /// Raised on the actor when a watched producer stays silent for its timeout. The watch is
    /// rearmed before the event, so a producer that stays silent times out again.
    /// </summary>
    event Action<byte, TimeSpan>? TimedOut;
}

/// <summary>Default <see cref="IHeartbeatConsumer"/>: one deadline per watched producer.</summary>
internal sealed class HeartbeatConsumer : IHeartbeatConsumer
{
    private readonly IDeadlineScheduler _scheduler;
    private readonly Dictionary<byte, Watch> _watches = new();

    public HeartbeatConsumer(IDeadlineScheduler scheduler)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
    }

    public event Action<byte, TimeSpan>? TimedOut;

    public bool IsWatching(byte producerNodeId) => _watches.ContainsKey(producerNodeId);

    public void Replace(IReadOnlyDictionary<byte, TimeSpan> watches)
    {
        if (watches is null) throw new ArgumentNullException(nameof(watches));

        List<byte>? stale = null;
        foreach (var kv in _watches)
        {
            if (!watches.TryGetValue(kv.Key, out var timeout) || timeout != kv.Value.Timeout)
            {
                stale ??= new List<byte>();
                stale.Add(kv.Key);
            }
        }
        if (stale is not null)
        {
            foreach (var nodeId in stale)
            {
                _watches[nodeId].Deadline?.Dispose();
                _watches.Remove(nodeId);
            }
        }

        foreach (var kv in watches)
        {
            if (_watches.ContainsKey(kv.Key)) continue;
            var producer = kv.Key;
            var watch = new Watch(kv.Value);
            watch.Deadline = _scheduler.Arm(kv.Value, () => OnMissed(producer));
            _watches[producer] = watch;
        }
    }

    public void NoteReceived(byte producerNodeId)
    {
        if (!_watches.TryGetValue(producerNodeId, out var watch)) return;
        var deadline = watch.Deadline;
        if (deadline is null || deadline.IsExpired || deadline.IsCancelled || !deadline.Rearm(watch.Timeout))
        {
            deadline?.Dispose();
            watch.Deadline = _scheduler.Arm(watch.Timeout, () => OnMissed(producerNodeId));
        }
    }

    public void Dispose()
    {
        foreach (var watch in _watches.Values)
            watch.Deadline?.Dispose();
        _watches.Clear();
    }

    private void OnMissed(byte producerNodeId)
    {
        if (!_watches.TryGetValue(producerNodeId, out var watch)) return;
        watch.Deadline?.Dispose();
        watch.Deadline = _scheduler.Arm(watch.Timeout, () => OnMissed(producerNodeId));
        TimedOut?.Invoke(producerNodeId, watch.Timeout);
    }

    private sealed class Watch
    {
        public Watch(TimeSpan timeout) => Timeout = timeout;

        public TimeSpan Timeout { get; }
        public IDeadline? Deadline { get; set; }
    }
}
