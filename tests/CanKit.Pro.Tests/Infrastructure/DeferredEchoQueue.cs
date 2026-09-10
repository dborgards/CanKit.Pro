using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can.Definitions;

namespace CanKit.Pro.Tests.Infrastructure;

/// <summary>
/// The parking lot for TX echoes of a <see cref="ControllableBus"/> running in
/// <see cref="EchoDelivery.Deferred"/> mode: <c>Transmit</c> hands the frame here instead of
/// echoing it, and the test decides when — and in which order — each echo reaches
/// <c>FrameObserved</c>.
///
/// <para>
/// Why this exists: a real echo-mode adapter delivers the echo synchronously from inside
/// <c>Transmit</c>, and <c>CanBusService.SendWithEchoConfirmAsync</c> transmits while holding its
/// pending-send lock. A synchronous echo therefore re-enters that lock <em>on the transmitting
/// thread</em>, so the pending list can never hold more than the one entry that thread just
/// registered. Every property that is only observable with two or more entries queued for the same
/// key — FIFO matching of byte-identical concurrent sends (SRS FR-RAW-031), an expired pending
/// send poisoning the FIFO for a later one — is therefore untestable against a synchronous echo:
/// the assertion holds no matter what the matching code does. Deferring the echo is what puts the
/// second entry in the list.
/// </para>
///
/// <para>
/// Everything here is deliberately explicit rather than time-based: <see cref="WaitForEnqueuedAsync"/>
/// is the only wait, and it waits on a transmit actually having happened rather than on a delay
/// that "should be long enough". Nothing in this class starts a timer, a thread, or a task — an
/// echo moves only when the test says so, on the test's own thread.
/// </para>
/// </summary>
/// <remarks>
/// Reusable beyond FR-RAW-031: any scenario that needs a pending send to still be pending while a
/// second one is registered (a late echo arriving after its own send already timed out, an echo
/// that never arrives at all while later ones do — see <see cref="DiscardNext"/>) is expressed by
/// parking, then releasing or discarding, in whatever order the scenario calls for.
/// </remarks>
public sealed class DeferredEchoQueue
{
    private readonly Action<CanFrame> _deliver;

    private readonly object _gate = new();
    private readonly List<CanFrame> _parked = new();
    private readonly List<(int Threshold, TaskCompletionSource Tcs)> _waiters = new();

    // Monotonic: counts every frame ever parked, so a waiter's threshold cannot be un-met by a
    // subsequent release. "Two sends have been transmitted" must stay true once it is true.
    private int _enqueued;

    internal DeferredEchoQueue(Action<CanFrame> deliver) => _deliver = deliver;

    /// <summary>Echoes parked and not yet released or discarded, oldest first.</summary>
    public int Count
    {
        get { lock (_gate) return _parked.Count; }
    }

    /// <summary>
    /// Total number of echoes ever parked — i.e. accepted transmits observed while in
    /// <see cref="EchoDelivery.Deferred"/> mode. Never decreases.
    /// </summary>
    public int Enqueued
    {
        get { lock (_gate) return _enqueued; }
    }

    /// <summary>
    /// Completes once <see cref="Enqueued"/> has reached <paramref name="count"/> — the
    /// deterministic replacement for "sleep a bit and hope the sends got that far".
    /// </summary>
    /// <remarks>
    /// A transmit is parked from inside <c>ICanBus.Transmit</c>, which
    /// <c>CanBusService.SendWithEchoConfirmAsync</c> calls after it has registered its pending
    /// entry and while still holding the pending-send lock. So "n echoes parked" is a hard
    /// guarantee that n pending sends are registered, not an approximation of it.
    /// </remarks>
    /// <param name="count">Number of parked echoes to wait for.</param>
    /// <param name="timeout">How long to wait before failing; a bound against a hang, not a
    /// scheduling assumption.</param>
    /// <exception cref="TimeoutException">Fewer than <paramref name="count"/> echoes were parked
    /// within <paramref name="timeout"/>.</exception>
    public async Task WaitForEnqueuedAsync(int count, TimeSpan timeout)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count), count, "Count must be positive.");

        Task wait;
        lock (_gate)
        {
            if (_enqueued >= count) return;
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((count, tcs));
            wait = tcs.Task;
        }

        try
        {
            await wait.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                $"Only {Enqueued} of {count} expected echoes were parked within {timeout}.");
        }
    }

    /// <summary>
    /// Delivers the oldest parked echo through the bus's <c>FrameObserved</c> event, on the
    /// calling thread. Returns <c>false</c> when nothing is parked.
    /// </summary>
    public bool ReleaseNext()
    {
        CanFrame frame;
        lock (_gate)
        {
            if (_parked.Count == 0) return false;
            frame = _parked[0];
            _parked.RemoveAt(0);
        }

        // Delivered outside the lock: the echo runs the service's whole match-and-complete path
        // (and any continuation it resumes) on this thread, and none of that may be serialized
        // against a concurrent Transmit parking the next echo.
        _deliver(frame);
        return true;
    }

    /// <summary>
    /// Delivers every parked echo, oldest first. Returns how many were delivered.
    /// </summary>
    public int ReleaseAll()
    {
        var released = 0;
        while (ReleaseNext()) released++;
        return released;
    }

    /// <summary>
    /// Drops the oldest parked echo without ever delivering it — the frame reached the wire but
    /// its echo is lost, while later echoes still arrive normally. Returns <c>false</c> when
    /// nothing is parked.
    /// </summary>
    public bool DiscardNext()
    {
        lock (_gate)
        {
            if (_parked.Count == 0) return false;
            _parked.RemoveAt(0);
            return true;
        }
    }

    /// <summary>Parks <paramref name="frame"/>; called by <see cref="ControllableBus.Transmit"/>.</summary>
    internal void Park(in CanFrame frame)
    {
        (int Threshold, TaskCompletionSource Tcs)[]? satisfied = null;
        lock (_gate)
        {
            _parked.Add(frame);
            _enqueued++;

            if (_waiters.Count > 0)
            {
                var met = _waiters.FindAll(w => w.Threshold <= _enqueued);
                if (met.Count > 0)
                {
                    satisfied = met.ToArray();
                    _waiters.RemoveAll(w => w.Threshold <= _enqueued);
                }
            }
        }

        // Never complete a TCS under the lock: the waiter's continuation may call straight back
        // into ReleaseNext/Count, and Park runs inside the service's pending-send lock.
        if (satisfied is null) return;
        foreach (var (_, tcs) in satisfied)
            tcs.TrySetResult();
    }
}
