using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Pro.RawCan;

namespace CanKit.Pro.IsoTp;

/// <summary>
/// A standing subscription to a functional client's response range, from which Single-Frame
/// responses are collected window by window. Unlike
/// <see cref="IsoTpFunctionalClient.CollectResponsesAsync"/>, which subscribes for each call,
/// the subscription is made once, by <see cref="IsoTpFunctionalClient.Listen"/>, and kept until
/// the listener is disposed: a response that arrives between two collections is buffered and
/// returned by the next one, and one that arrives before the first collection begins is not
/// missed. Obtain it before the request goes out, and no fast reply is lost.
/// </summary>
/// <remarks>
/// Collections are sequential: call <see cref="CollectAsync"/> again only after the previous
/// call completed. The subscription buffer is bounded and drops the oldest frame when full, so
/// a listener left uncollected for long may lose the earliest of what arrived.
/// </remarks>
public sealed class IsoTpFunctionalListener : IDisposable
{
    private readonly ISubscription _subscription;
    private int _disposed;

    internal IsoTpFunctionalListener(ISubscription subscription)
    {
        _subscription = subscription;
    }

    /// <summary>
    /// Collects the Single-Frame responses that arrive on the subscription within
    /// <paramref name="window"/>, together with those buffered since the previous collection.
    /// </summary>
    /// <param name="window">Duration to collect responses.</param>
    /// <param name="cancellationToken">Cancels the collection; what was collected is lost.</param>
    /// <returns>The responses, in arrival order; empty if none arrived.</returns>
    /// <exception cref="ObjectDisposedException">The listener was disposed.</exception>
    public async Task<IReadOnlyList<IsoTpFunctionalResponse>> CollectAsync(
        TimeSpan window, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(IsoTpFunctionalListener));
        var responses = new List<IsoTpFunctionalResponse>();
        using var windowCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        windowCts.CancelAfter(window);
        try
        {
            // Ends when the window runs out, or when the subscription is completed underneath
            // -- the service disposed -- in which case what is buffered is still read.
            while (await _subscription.WaitToReadAsync(windowCts.Token).ConfigureAwait(false))
                TakeBuffered(responses);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The window expired: a frame that arrived on the subscription before the deadline
            // may still sit unread, and belongs to this collection. The subscription stays
            // open, so this is a read of the buffer, not a drain of a closed one.
            TakeBuffered(responses);
        }
        return responses.AsReadOnly();
    }

    private void TakeBuffered(List<IsoTpFunctionalResponse> responses)
    {
        while (_subscription.TryRead(out var frameEvent))
        {
            if (IsoTpFunctionalClient.TryParseFunctionalResponse(frameEvent, out var response))
                responses.Add(response!);
        }
    }

    /// <summary>Ends the subscription. A collection in progress ends with it.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _subscription.Dispose();
    }
}
