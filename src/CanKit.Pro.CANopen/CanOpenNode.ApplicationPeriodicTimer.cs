using System;

namespace CanKit.Pro.CANopen;

internal sealed partial class CanOpenNode
{
    private sealed class ApplicationPeriodicTimer : IDisposable
    {
        private readonly CanOpenNode _node;
        private readonly ApplicationSlot _slot;
        private readonly Action _onTick;
        private readonly long _start;
        private readonly decimal _periodTicks;
        private readonly object _gate = new();
        private IDisposable? _pending;

        internal ApplicationPeriodicTimer(CanOpenNode node, ApplicationSlot slot, TimeSpan period, Action onTick)
        {
            if (period <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(period));
            _onTick = onTick ?? throw new ArgumentNullException(nameof(onTick));
            _node = node;
            _slot = slot;
            var clock = node._actor.TimeSource;
            _periodTicks = (decimal)period.Ticks * clock.Frequency / TimeSpan.TicksPerSecond;
            _start = clock.GetTimestamp();
            lock (_gate) _pending = ScheduleNext(_start);
        }

        private IDisposable? ScheduleNext(long now)
        {
            // Keep the phase even on a clock with a fractional number of ticks per period.
            // Round only the absolute due time, and stop at the end of the clock's range.
            var elapsed = (decimal)now - _start;
            var next = Math.Ceiling(now + (_periodTicks - elapsed % _periodTicks));
            return next <= long.MaxValue ? _node._actor.ScheduleAt((long)next, OnDue) : null;
        }

        private void OnDue() => _node.RunApplicationDeadline(_slot, Tick);

        private void Tick()
        {
            lock (_gate)
            {
                // Dispose can win while a due tick is held behind startup.
                if (_pending is null) return;
                _pending = ScheduleNext(_node._actor.TimeSource.GetTimestamp());
            }
            _onTick();
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _pending?.Dispose();
                _pending = null;
            }
        }
    }
}
