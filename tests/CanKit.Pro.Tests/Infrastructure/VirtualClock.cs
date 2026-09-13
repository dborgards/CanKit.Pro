using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CanKit.Pro.Actor;

namespace CanKit.Pro.Tests.Infrastructure;

/// <summary>
/// A monotonic clock the test owns, plus the actors that measure their timers against it (#92
/// step 2). Advancing it is deterministic: when <see cref="AdvanceAsync"/> returns, every timer
/// that became due has already run its callback.
/// </summary>
/// <remarks>
/// <para>
/// The point is not that it is faster than sleeping. It is that a test asserting "the sender
/// waited STmin before the next frame" is otherwise asserting something the code explicitly does
/// not promise — the ISO-TP README concedes that effective spacing is "STmin + OS scheduling
/// latency … with no hard real-time guarantee under load", and #92 counts the tests that have
/// gone red for want of that distinction. With the clock in the test's hands, the property under
/// test becomes the one the code actually implements: <em>the interval the sender waits for is
/// STmin</em>, measured on the clock it schedules against.
/// </para>
/// <para>
/// <b>Why two round-trips.</b> <see cref="ProtocolActor"/>'s loop runs
/// <c>wait → DrainMailbox → DrainPendingTimerInserts → FireDueTimers</c>. A single
/// <see cref="IProtocolActor.PostAsync(Action)"/> completes during the drain, so awaiting it can
/// resume while that same iteration is still inside <c>FireDueTimers</c>. The second one is
/// drained in the <em>next</em> iteration, which the loop reaches only after the first
/// iteration's timer callbacks have returned — so awaiting it is a proof that they did, resting
/// on the loop being sequential rather than on any delay. Anything a callback then hands to the
/// thread pool (an emission, a send) is still asynchronous and must be awaited on its own
/// observable effect; what is deterministic here is <em>when the decision was taken</em>.
/// </para>
/// <para>
/// <b>What it must not do.</b> A virtual clock makes work free unless the work is made to cost
/// something, and that can silently destroy a test's reason for existing: the J1939 fixed-rate
/// test tells anchor scheduling from send-then-delay only because a send takes time, and on a
/// clock nobody advances during a send both implementations land on the grid. Hence
/// <see cref="Advance"/> is public and callable from inside a bus double — the cost of work is
/// modelled explicitly rather than assumed away. #112 is the cautionary case: a virtual clock
/// would have hidden that defect instead of fixing it.
/// </para>
/// </remarks>
internal sealed class VirtualClock : IDisposable
{
    private readonly ManualTimeSource _source = new();
    private readonly List<ProtocolActor> _actors = new();
    private readonly object _gate = new();

    /// <summary>How often the actors have read the clock. See <see cref="ManualTimeSource.ReadCount"/>.</summary>
    public long ReadCount => _source.ReadCount;

    /// <summary>
    /// Elapsed virtual time since this clock was created. Read it at the moment an effect is
    /// observed to learn which virtual instant it belongs to.
    /// </summary>
    public TimeSpan Elapsed => TimeSpan.FromTicks(_source.GetTimestamp());

    /// <summary>
    /// A new actor measuring its timers against this clock, tracked so <see cref="AdvanceAsync"/>
    /// wakes it and <see cref="Dispose"/> tears it down. Hand it to the component under test.
    /// </summary>
    public ProtocolActor NewActor()
    {
        var actor = new ProtocolActor(ActorExecutionMode.DedicatedThread, null, _source, null);
        lock (_gate) _actors.Add(actor);
        return actor;
    }

    /// <summary>
    /// Moves the clock forward without waiting for anyone to notice. For use from inside a double
    /// that is modelling work which costs time — a driver call, a transmission — where the caller
    /// is mid-operation and cannot await anything.
    /// </summary>
    public void Advance(TimeSpan by) => _source.Advance(by);

    /// <summary>
    /// Moves the clock forward and returns once every actor has fired the timers that became due.
    /// </summary>
    public async Task AdvanceAsync(TimeSpan by)
    {
        _source.Advance(by);

        ProtocolActor[] actors;
        lock (_gate) actors = _actors.ToArray();

        // Poke every loop first: each is asleep on a wait computed from the clock as it was
        // before the advance, and nothing about advancing a counter wakes it.
        foreach (var actor in actors)
            await actor.PostAsync(() => 0).ConfigureAwait(false);

        // Second round-trip: see the remarks. This one is drained in the iteration after the one
        // that fired the newly due timers, so it cannot return while a callback is still running.
        foreach (var actor in actors)
            await actor.PostAsync(() => 0).ConfigureAwait(false);
    }

    /// <summary>
    /// Lets every actor reach a quiescent point without moving the clock — the same two
    /// round-trips, for when a test needs work already posted to have been processed.
    /// </summary>
    public Task SettleAsync() => AdvanceAsync(TimeSpan.Zero);

    public void Dispose()
    {
        ProtocolActor[] actors;
        lock (_gate)
        {
            actors = _actors.ToArray();
            _actors.Clear();
        }

        foreach (var actor in actors)
        {
            try { actor.Dispose(); }
            catch (ObjectDisposedException) { /* already torn down by its owner */ }
        }
    }
}
