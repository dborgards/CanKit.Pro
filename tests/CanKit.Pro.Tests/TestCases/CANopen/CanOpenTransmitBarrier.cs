using System;
using System.Reflection;
using System.Threading.Tasks;
using CanKit.Pro.CANopen;

namespace CanKit.Pro.Tests.TestCases.CANopen;

/// <summary>
/// The barrier a test waits on before it asserts what a node did <em>not</em> send, or that it sent
/// exactly so many frames.
/// </summary>
/// <remarks>
/// <para>
/// What it guarantees, and why. A node decides a transmission on its actor loop and reserves the
/// send there (<c>GateTransmit</c>), synchronously, in the same job; the frame then leaves on the
/// thread pool (<c>SendControlFrame</c>'s <c>Task.Run</c>) and the reservation is released once the
/// driver call has returned. The barrier first runs two empty jobs through the actor, so every job
/// queued before the call, and every job one of those queued in turn, has run and has reserved what
/// it sends; then it waits until the node holds no reservation (<c>HasLssTransmitsInFlight</c>). On
/// the virtual adapter (CanKit v0.5.6 <c>VirtualTransceiver.Transmit</c> →
/// <c>VirtualBusHub.Broadcast</c>) the driver call delivers the frame to every other channel of the
/// session, synchronously, through its <c>FrameObserved</c> handlers, before it returns. So when the
/// barrier returns, every such frame has already been seen by a peer channel that was subscribed
/// before it was sent.
/// </para>
/// <para>
/// What it does not cover: frames the node decides later than those two jobs — on a timer or a
/// deadline that has not fired yet (an inhibit time, an event timer, a clock the test has not
/// moved), or behind a chain of more than two jobs. A frame held before its send by a test seam
/// (<c>BeforeControlFrameSendForTests</c>) keeps its reservation, so the barrier waits for it rather
/// than returning without it. The wait is bounded by a hang guard, which asserts nothing about time.
/// </para>
/// <para>
/// The EMCY chain is not covered by <see cref="DrainAsync"/>; <see cref="DrainEmcyAsync"/> is the
/// barrier for EMCYs.
/// </para>
/// <para>
/// A witness frame alone — a TPDO triggered last, whose arrival was taken to mean that everything
/// before it is on the wire — does not give this: each frame takes its own trip through the thread
/// pool, and a later one can overtake an earlier one.
/// </para>
/// </remarks>
internal static class CanOpenTransmitBarrier
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

    internal static async Task DrainAsync(CanOpenNode node)
    {
        await node.PostToActorAsync(() => { });
        await node.PostToActorAsync(() => { });
        var deadline = DateTime.UtcNow + HangGuard;
        while (node.HasLssTransmitsInFlight)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Timed out waiting for the node's sends to drain.");
            await Task.Delay(1);
        }
    }

    private static readonly FieldInfo EmcySendChain =
        typeof(CanOpenNode).GetField("_emcySendChain", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("CanOpenNode has no _emcySendChain field any more.");

    /// <summary>
    /// The barrier before asserting what EMCYs were <em>not</em> sent, or exactly how many were:
    /// <see cref="DrainAsync"/>, so that every job queued before the call has run and ordered its
    /// EMCYs, then a wait until every EMCY ordered so far has been through the node's EMCY chain, and
    /// <see cref="DrainAsync"/> again. A step of the chain completes once its frame's send has
    /// completed (the driver call has returned, so on the virtual adapter a peer channel has seen it),
    /// or once it decided to send nothing (the EMCY switched off, a reset in between); with the EMCY
    /// switched off it has written <c>1001h</c> by then.
    /// </summary>
    /// <remarks>
    /// <see cref="DrainAsync"/> alone does not cover the chain: each step goes from a thread-pool
    /// continuation back to the actor before it reserves its send, so an EMCY ordered in the last job
    /// can still be on its way when the actor is idle and nothing is reserved. The chain is a field
    /// the actor extends, so it is read on the actor and awaited until it is the same task twice. A
    /// step that waits — for its inhibit time, for the node to leave Stopped, for a boot-up held by a
    /// test seam — keeps the chain from completing, and the hang guard ends the wait with a
    /// <see cref="TimeoutException"/>: assert that such an EMCY was not sent by its own state
    /// instead. The guard asserts nothing about time.
    /// </remarks>
    internal static async Task DrainEmcyAsync(CanOpenNode node)
    {
        await DrainAsync(node);
        Task? last = null;
        while (true)
        {
            Task? current = null;
            await node.PostToActorAsync(() => current = (Task)EmcySendChain.GetValue(node)!);
            if (await Task.WhenAny(current!, Task.Delay(HangGuard)) != current)
                throw new TimeoutException("Timed out waiting for the EMCY chain.");
            if (ReferenceEquals(current, last)) break;
            last = current;
        }
        await DrainAsync(node);
    }
}
