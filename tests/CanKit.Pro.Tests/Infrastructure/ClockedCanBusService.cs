using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Pro.Actor;
using CanKit.Pro.RawCan;

namespace CanKit.Pro.Tests.Infrastructure;

/// <summary>
/// A real RawCan service whose confirmation deadlines share the rig's manual clock. Matching
/// echoes, driver acceptance, cancellation and faults still go through CanBusService unchanged.
/// Each send owns its timer, so a completed confirmation leaves no deadline in the rig's queue.
/// The rig must settle PendingConfirmations before advancing, except for deliberately held sends.
/// </summary>
internal sealed class ClockedCanBusService : ICanBusService
{
    private readonly CanBusService _inner;
    private readonly AsyncLocal<SendDeadline?> _deadline = new();
    private int _pending;
    private int _disposed;

    public ClockedCanBusService(ICanBus bus, ManualTimeSource clock)
    {
        Timers = new ProtocolActor(ActorExecutionMode.DedicatedThread, null, clock, null);
        _inner = new CanBusService(bus, clock.GetTimestamp);
        _inner.ArmConfirmTimeout = (source, timeout) =>
            (_deadline.Value ?? throw new InvalidOperationException("A confirmation must belong to a send."))
            .Arm(Timers, source, timeout);
    }

    public ProtocolActor Timers { get; }
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    public int PendingConfirmations => Volatile.Read(ref _pending);
    public ICanBus Bus => _inner.Bus;
    public int SubscriptionCount => _inner.SubscriptionCount;
    public event EventHandler<Exception>? BackgroundExceptionOccurred
    {
        add => _inner.BackgroundExceptionOccurred += value;
        remove => _inner.BackgroundExceptionOccurred -= value;
    }

    public ISubscription Subscribe(Func<CanFrameEvent, bool>? predicate = null, int? bufferCapacity = null,
        bool includeEcho = false) => _inner.Subscribe(predicate, bufferCapacity, includeEcho);
    public ISubscription Subscribe(CanIdFilter filter, int? bufferCapacity = null, bool includeEcho = false)
        => _inner.Subscribe(filter, bufferCapacity, includeEcho);
    public IReadOnlyList<FilterOverlap> FindOverlappingFilterSubscriptions()
        => _inner.FindOverlappingFilterSubscriptions();

    public async Task<TxConfirmation> SendConfirmedAsync(CanFrame frame, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var deadline = new SendDeadline();
        var previous = _deadline.Value;
        _deadline.Value = deadline;
        Interlocked.Increment(ref _pending);
        try
        {
            return await _inner.SendConfirmedAsync(frame, timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Dispose before publishing quiescence: the next clock step must not see a stale timer.
            deadline.Dispose();
            _deadline.Value = previous;
            Interlocked.Decrement(ref _pending);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _inner.Dispose(); }
        finally { Timers.Dispose(); }
    }

    private sealed class SendDeadline : IDisposable
    {
        private readonly object _gate = new();
        private IDisposable? _timer;
        private bool _disposed;

        public void Arm(ProtocolActor actor, CancellationTokenSource source, TimeSpan timeout)
        {
            lock (_gate)
            {
                if (_disposed) return;
                _timer = actor.Schedule(timeout, () =>
                {
                    try { source.Cancel(); }
                    catch (ObjectDisposedException) { /* completion won the deadline race */ }
                });
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _timer?.Dispose();
                _timer = null;
            }
        }
    }
}
