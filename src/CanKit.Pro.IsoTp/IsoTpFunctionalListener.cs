using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    // Arrived after a collection's deadline but before its drain read the buffer: kept for the
    // next collection, whose window they are in (Codex on #150).
    private readonly Queue<IsoTpFunctionalResponse> _carried = new();
    private bool _ended;
    private int _disposed;

    internal IsoTpFunctionalListener(ISubscription subscription)
    {
        _subscription = subscription;
    }

    /// <summary>
    /// Collects the Single-Frame responses that arrive on the subscription within
    /// <paramref name="window"/>, together with those buffered since the previous collection.
    /// A response that arrives after the window ends but before this call returns is kept for
    /// the next collection, not returned by this one.
    /// </summary>
    /// <param name="window">Duration to collect responses.</param>
    /// <param name="cancellationToken">Cancels the collection; what was collected is lost.</param>
    /// <returns>The responses, in arrival order; empty if none arrived.</returns>
    /// <exception cref="ObjectDisposedException">
    /// The listener was disposed, or its subscription ended underneath it -- the service was
    /// disposed -- which a collection in progress reports by returning what was buffered, and
    /// every collection after it by this exception.
    /// </exception>
    public async Task<IReadOnlyList<IsoTpFunctionalResponse>> CollectAsync(
        TimeSpan window, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0 || _ended)
            throw new ObjectDisposedException(nameof(IsoTpFunctionalListener),
                _ended ? "The subscription ended: the service was disposed." : null);
        long deadline = Stopwatch.GetTimestamp() + (long)(window.TotalSeconds * Stopwatch.Frequency);
        var responses = new List<IsoTpFunctionalResponse>(_carried);
        _carried.Clear();
        using var windowCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        windowCts.CancelAfter(window);
        try
        {
            // Ends when the window runs out, or when the subscription is completed underneath
            // -- the service disposed -- in which case what is buffered is still read, and
            // the next collection throws rather than return empty at once: a loop that
            // collects while a deadline lasts would otherwise spin on it (Bugbot on #150).
            while (await _subscription.WaitToReadAsync(windowCts.Token).ConfigureAwait(false))
                TakeBuffered(responses, deadline);
            _ended = true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The window expired: a frame that arrived on the subscription before the deadline
            // may still sit unread, and belongs to this collection; one that arrived after it,
            // in the instant between the timer and this read, is the next collection's. The
            // subscription stays open, so this is a read of the buffer, not a drain of a
            // closed one.
            TakeBuffered(responses, deadline);
        }
        return responses.AsReadOnly();
    }

    private void TakeBuffered(List<IsoTpFunctionalResponse> responses, long deadline)
    {
        while (_subscription.TryRead(out var frameEvent))
        {
            if (!IsoTpFunctionalClient.TryParseFunctionalResponse(frameEvent, out var response)) continue;
            if (response!.HostArrivalTimestamp <= deadline) responses.Add(response);
            else _carried.Enqueue(response);
        }
    }

    /// <summary>Ends the subscription. A collection in progress ends with it.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _subscription.Dispose();
    }
}
