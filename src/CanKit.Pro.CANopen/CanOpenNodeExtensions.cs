using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CanKit.Pro.CANopen.Nmt;

namespace CanKit.Pro.CANopen;

/// <summary>Additional operations for <see cref="ICanOpenNode"/> without widening its interface.</summary>
/// <remarks>
/// <see cref="ICanOpenNode"/> itself stays <see cref="IDisposable"/>: adding
/// <see cref="IAsyncDisposable"/> to a published interface would break everyone who implements it.
/// The nodes this library creates implement it, and <see cref="DisposeAsync(ICanOpenNode)"/> reaches
/// it without a cast. It cannot be used with <c>await using</c> on an <see cref="ICanOpenNode"/>
/// (that needs the interface); cast to <see cref="IAsyncDisposable"/> for that.
/// </remarks>
public static class CanOpenNodeExtensions
{
    /// <summary>Returns immutable snapshots of the assigned slaves' latest boot steps in
    /// node-id order. A new boot clears earlier results; leaving the master role clears them.
    /// StartRequested means a command was queued, not that the slave acknowledged it.
    /// Identity checking is enabled by nonzero values in 1F84h–1F88h; configuration checking
    /// by CanOpenNodeOptions.RequireBootConfigurationMatch with 1F26h/1F27h expectations.</summary>
    /// <exception cref="ArgumentNullException">The node is null.</exception>
    /// <exception cref="NotSupportedException">The node was not created by this library.</exception>
    /// <exception cref="ObjectDisposedException">The node has been disposed.</exception>
    public static IReadOnlyList<SlaveBootResult> GetSlaveBootResults(this ICanOpenNode node)
    {
        if (node is null) throw new ArgumentNullException(nameof(node));
        if (node is not CanOpenNode local)
            throw new NotSupportedException("Boot results require a node created by CanKit.Pro.");
        return local.GetSlaveBootResults();
    }

    /// <summary>
    /// Acknowledges the local node's latched controller-overrun cause (<c>8110h</c>), including
    /// both RxOverflow and TxOverflow. Idempotent when no cause is active or
    /// <see cref="CanOpenNodeOptions.EmitControllerOverrunEmcy"/> is disabled. The application
    /// decides when the consequences of lost objects have been handled; this call does not
    /// repair the adapter, restore lost objects or clear another communication-error cause.
    /// </summary>
    /// <remarks>
    /// Returns after acknowledgement is processed on the node actor, with any <c>0000h</c>
    /// queued through the existing EMCY chain; it does not wait for transmission. A reset is
    /// queued only when the last reported node communication-error cause ends. Application bits
    /// are preserved. Notifications ordered after this acknowledgement on the actor may begin a
    /// new error event, including a notification whose driver callback raced this call.
    /// NMT reset also ends the cause, without an error-reset EMCY. This application acknowledgement
    /// contract is a library choice, not a controller repair inferred from a healthy bus state.
    /// </remarks>
    /// <param name="node">The local node created by this library.</param>
    /// <exception cref="ArgumentNullException"><paramref name="node"/> is null.</exception>
    /// <exception cref="NotSupportedException">The node is a third-party implementation.</exception>
    /// <exception cref="ObjectDisposedException">The node has been disposed.</exception>
    public static void AcknowledgeControllerOverrun(this ICanOpenNode node)
    {
        if (node is null) throw new ArgumentNullException(nameof(node));
        if (node is not CanOpenNode local)
            throw new NotSupportedException("Controller-overrun acknowledgement requires a node created by CanKit.Pro.");
        local.AcknowledgeControllerOverrun();
    }

    /// <summary>
    /// Disposes <paramref name="node"/> without holding a thread while it waits for the node's
    /// reader and event pump to finish, which <see cref="IDisposable.Dispose"/> does for up to two
    /// seconds. Either call may be made from a subscriber of the node (an event,
    /// <c>ApplicationReset</c>, <c>BackgroundExceptionOccurred</c>): the task that subscriber runs
    /// on is then not waited for. A second call while the first is running returns when the
    /// disposal has finished, except from a subscriber of the node, which cannot wait for a
    /// disposal that is waiting for it and returns at once. A node that is not one of this
    /// library's is disposed on the thread pool.
    /// </summary>
    /// <param name="node">The node to dispose.</param>
    public static ValueTask DisposeAsync(this ICanOpenNode node)
    {
        if (node is null) throw new ArgumentNullException(nameof(node));
        return node is IAsyncDisposable asynchronous
            ? asynchronous.DisposeAsync()
            : new ValueTask(Task.Run(node.Dispose));
    }
}
