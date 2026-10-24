using System;
using System.Collections.Generic;
using System.Threading;

namespace CanKit.Pro.Actor;

/// <summary>
/// Delivers a protocol layer's <c>BackgroundExceptionOccurred</c> reports off the thread that
/// made them: on the thread pool, one report at a time and in the order they were made, each
/// subscriber called on its own.
/// </summary>
/// <remarks>
/// Most reports are made on the layer's actor loop, inside the callback that failed. Raised there,
/// a subscriber that waits on the layer -- a receive, a send, a disposal -- waits for the loop it
/// is blocking, and a J1939-TP reassembly abort was even raised before the fault it announced had
/// been written to the inbox the subscriber would wait on (#356, T-2). Queued here, the loop goes
/// on, and whatever the report announces is in place before any subscriber hears of it: the
/// layer writes it first, then raises.
/// <para>
/// One drain at a time keeps the order and keeps a subscriber from running concurrently with
/// itself, which the loop guaranteed for the reports made there and which a subscriber written
/// for it may rely on. A subscriber that blocks holds up the reports behind it, not the loop.
/// </para>
/// <para>
/// A multicast <c>Invoke</c> stops at the first subscriber that throws, so one <c>try</c> around
/// it hid the report from every subscriber registered after a throwing one;
/// <see cref="InvokeEach"/> calls them one by one (#381), as <c>CanOpenNode</c> and
/// <see cref="ProtocolActor"/> do.
/// </para>
/// </remarks>
internal sealed class BackgroundExceptionDispatcher
{
    private readonly object _gate = new();
    private readonly Queue<Action> _pending = new();
    private bool _draining;

    /// <summary>
    /// Queues the report of <paramref name="exception"/> to the subscribers of
    /// <paramref name="handler"/> as they are at this call. Returns at once.
    /// </summary>
    internal void Raise(object sender, EventHandler<Exception>? handler, Exception exception)
    {
        if (handler is null) return;
        Post(() => InvokeEach(handler, sender, exception));
    }

    /// <summary>
    /// Queues <paramref name="delivery"/> behind every report queued so far. For a layer whose
    /// round has rules of its own (<c>CanOpenNode</c>'s disposal); others use <see cref="Raise"/>.
    /// </summary>
    internal void Post(Action delivery)
    {
        lock (_gate)
        {
            _pending.Enqueue(delivery);
            if (_draining) return;
            _draining = true;
        }

        // No ExecutionContext is flowed: one drain serves reports made on many threads, and the
        // context of whichever report started it belongs to none of the others.
        ThreadPool.UnsafeQueueUserWorkItem(static state => ((BackgroundExceptionDispatcher)state!).Drain(), this);
    }

    /// <summary>
    /// Calls every subscriber of <paramref name="handler"/> with <paramref name="exception"/>,
    /// each in its own <c>try</c>, until <paramref name="endsRound"/> (checked before each one)
    /// says the round is over.
    /// </summary>
    internal static void InvokeEach(EventHandler<Exception> handler, object sender, Exception exception,
        Func<bool>? endsRound = null)
    {
        foreach (var subscriber in handler.GetInvocationList())
        {
            if (endsRound is not null && endsRound()) return;
            try
            {
                ((EventHandler<Exception>)subscriber)(sender, exception);
            }
            catch
            {
                // A misbehaving subscriber must not keep the report from the others, nor end
                // the drain.
            }
        }
    }

    private void Drain()
    {
        while (true)
        {
            Action delivery;
            lock (_gate)
            {
                if (_pending.Count == 0)
                {
                    _draining = false;
                    return;
                }
                delivery = _pending.Dequeue();
            }

            try
            {
                delivery();
            }
            catch
            {
                // InvokeEach does not throw; a delivery of a layer's own must not end the drain
                // and strand the reports behind it either.
            }
        }
    }
}
