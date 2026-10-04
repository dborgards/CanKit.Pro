using System;
using System.Threading.Tasks;

namespace CanKit.Pro.Uds;

/// <summary>Asynchronous disposal for <see cref="IUdsClient"/>.</summary>
/// <remarks>
/// <see cref="IUdsClient"/> itself stays <see cref="IDisposable"/>: adding
/// <see cref="IAsyncDisposable"/> to a published interface would break everyone who implements it.
/// The clients this library creates implement it, and <see cref="DisposeAsync(IUdsClient)"/> reaches
/// it without a cast.
/// </remarks>
public static class UdsClientExtensions
{
    /// <summary>
    /// Disposes <paramref name="client"/> without holding a thread while it waits for the
    /// TesterPresent keep-alive loop and for an in-flight request to let go of the request lock,
    /// which <see cref="IDisposable.Dispose"/> does for up to its timeouts. Both are bounded: a
    /// request or loop that needs the channel's actor while a callback of the channel holds it
    /// (a caller disposing from <c>BackgroundExceptionOccurred</c> of the channel, say) ends when
    /// the cancellation reaches it, not by being waited for for ever. A second call while the first
    /// is running returns when the disposal has finished. A client that is not one of this
    /// library's is disposed on the thread pool.
    /// </summary>
    /// <param name="client">The client to dispose.</param>
    public static ValueTask DisposeAsync(this IUdsClient client)
    {
        if (client is null) throw new ArgumentNullException(nameof(client));
        return client is IAsyncDisposable asynchronous
            ? asynchronous.DisposeAsync()
            : new ValueTask(Task.Run(client.Dispose));
    }
}
