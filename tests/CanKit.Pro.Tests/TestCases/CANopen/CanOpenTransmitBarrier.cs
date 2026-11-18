using System;
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
}
