using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Pro.RawCan;

namespace CanKit.Pro.Tests.Infrastructure;

/// <summary>
/// Wraps an <see cref="ICanBusService"/> and makes its <c>Subscribe</c> throw on a chosen call,
/// so a protocol type's construction can be failed at a chosen point.
/// </summary>
/// <remarks>
/// For testing what a constructor does when it cannot finish. A protocol type that creates its own
/// actor must dispose it on the way out; one handed an actor must not, because the caller may still
/// be running other components on it. Both are claims #113 makes about its new seam, and neither
/// was checked until Codecov pointed at the unexecuted <c>catch</c> blocks.
///
/// The call index matters because a node builds more than one subscription: J1939 opens its
/// transport channel first and subscribes for itself second, so failing the first call and failing
/// the second reach different <c>catch</c> blocks.
/// </remarks>
internal sealed class ThrowingSubscribeService : ICanBusService
{
    private readonly ICanBusService _inner;
    private readonly int _failOnCall;
    private int _calls;

    /// <param name="inner">The real service; the test owns and disposes it.</param>
    /// <param name="failOnCall">1-based index of the <c>Subscribe</c> call that should throw.</param>
    public ThrowingSubscribeService(ICanBusService inner, int failOnCall)
    {
        _inner = inner;
        _failOnCall = failOnCall;
    }

    /// <summary>How many times <c>Subscribe</c> has been called, including the one that threw.</summary>
    public int SubscribeCalls => Volatile.Read(ref _calls);

    public ICanBus Bus => _inner.Bus;

    public int SubscriptionCount => _inner.SubscriptionCount;

    public event EventHandler<Exception>? BackgroundExceptionOccurred
    {
        add => _inner.BackgroundExceptionOccurred += value;
        remove => _inner.BackgroundExceptionOccurred -= value;
    }

    public ISubscription Subscribe(Func<CanFrameEvent, bool>? predicate = null,
        int? bufferCapacity = null, bool includeEcho = false)
    {
        ThrowIfThisIsTheOne();
        return _inner.Subscribe(predicate, bufferCapacity, includeEcho);
    }

    public ISubscription Subscribe(CanIdFilter filter, int? bufferCapacity = null,
        bool includeEcho = false)
    {
        ThrowIfThisIsTheOne();
        return _inner.Subscribe(filter, bufferCapacity, includeEcho);
    }

    public IReadOnlyList<FilterOverlap> FindOverlappingFilterSubscriptions()
        => _inner.FindOverlappingFilterSubscriptions();

    public Task<TxConfirmation> SendConfirmed(CanFrame frame, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => _inner.SendConfirmed(frame, timeout, cancellationToken);

    public void Dispose() { /* wrapper: the test owns and disposes the inner service */ }

    private void ThrowIfThisIsTheOne()
    {
        if (Interlocked.Increment(ref _calls) == _failOnCall)
            throw new InvalidOperationException(
                $"Subscribe call {_failOnCall} fails by request of the test.");
    }
}
