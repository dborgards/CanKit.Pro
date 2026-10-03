using System;
using System.Threading.Tasks;

namespace CanKit.Pro.CANopen;

/// <summary>Asynchronous disposal for <see cref="ICanOpenNode"/>.</summary>
/// <remarks>
/// <see cref="ICanOpenNode"/> itself stays <see cref="IDisposable"/>: adding
/// <see cref="IAsyncDisposable"/> to a published interface would break everyone who implements it.
/// The nodes this library creates implement it, and <see cref="DisposeAsync(ICanOpenNode)"/> reaches
/// it without a cast. It cannot be used with <c>await using</c> on an <see cref="ICanOpenNode"/>
/// (that needs the interface); cast to <see cref="IAsyncDisposable"/> for that.
/// </remarks>
public static class CanOpenNodeExtensions
{
    /// <summary>
    /// Disposes <paramref name="node"/> without holding a thread while it waits for the node's
    /// reader and event pump to finish, which <see cref="IDisposable.Dispose"/> does for up to two
    /// seconds. Either call may be made from a subscriber of the node (an event,
    /// <c>ApplicationReset</c>, <c>BackgroundExceptionOccurred</c>): the task that subscriber runs
    /// on is then not waited for. A second call while the first is running returns when the
    /// disposal has finished. A node that is not one of this library's is disposed on the thread
    /// pool.
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
