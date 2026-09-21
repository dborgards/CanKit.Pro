using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Pro.IsoTp;

namespace CanKit.Pro.Uds;

/// <summary>
/// UDS over functional addressing (ISO 14229-1 §7.5.4): one request on the functional CAN
/// identifier, every ECU in the response range may answer. Built on
/// <see cref="IsoTpFunctionalClient"/>, which carries only Single Frames both ways; a request
/// that needs more than one frame, or an ECU whose answer does, is not for this path (#57).
/// </summary>
/// <remarks>
/// The typical uses are the keep-alive to everyone (<c>3E 80</c>) and a session change or
/// short read broadcast to a set of ECUs. A negative response is one ECU's and does not fault
/// the call: it is returned beside the others as a <see cref="UdsFunctionalResponse"/> with
/// <see cref="UdsFunctionalResponse.IsNegative"/> set.
/// </remarks>
public sealed class UdsFunctionalClient : IDisposable
{
    private const byte SuppressPositiveResponseBit = 0x80;
    private const byte NegativeResponseSid = 0x7F;
    private const byte NrcResponsePending = 0x78;
    private const byte ReadDataByPeriodicIdentifierSid = 0x2A;

    private readonly IsoTpFunctionalClient _client;
    private readonly bool _ownsClient;
    private readonly TimeSpan _responseWindow;
    private readonly TimeSpan _responsePendingWindow;
    private readonly SuppressedResponseWindows _openWindows = new();
    // Per service: the standing subscription that hears the window out, and the task reading it.
    private readonly Dictionary<byte, (IsoTpFunctionalListener Ears, Task Run)> _listeners = new();
    // One request on the wire at a time, its collection window included: overlapping calls
    // with the same SID would each collect the other's answers (Codex on #150), as the
    // physical client's request lock prevents there.
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    // Cancels a wait on the lock, and a send or window in progress, when the client is
    // disposed: a call queued behind another must not go out on a disposed client (Codex and
    // Bugbot on #150).
    private readonly CancellationTokenSource _lifetimeCts = new();
    private int _disposed;

    private UdsFunctionalClient(IsoTpFunctionalClient client, bool ownsClient, TimeSpan responseWindow,
        TimeSpan responsePendingWindow)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        if (responseWindow <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(responseWindow), "The window must be positive.");
        if (responsePendingWindow <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(responsePendingWindow), "The window must be positive.");
        _ownsClient = ownsClient;
        _responseWindow = responseWindow;
        _responsePendingWindow = responsePendingWindow;
    }

    /// <summary>
    /// Wraps an open <see cref="IsoTpFunctionalClient"/>. With <paramref name="ownsClient"/>,
    /// disposing this client disposes it. <paramref name="responseWindow"/> is how long after a
    /// request an ECU may still answer it -- P2 -- and so how long the next call for the same
    /// service waits before it collects, whether the request was suppressed or its collection
    /// window simply ended sooner; <paramref name="responsePendingWindow"/> is what an NRC 0x78
    /// seen in that time extends it by -- P2*. The defaults are
    /// <see cref="UdsClientOptions.DefaultP2"/> and <see cref="UdsClientOptions.DefaultP2Star"/>.
    /// </summary>
    public static UdsFunctionalClient Create(IsoTpFunctionalClient client, bool ownsClient = false,
        TimeSpan? responseWindow = null, TimeSpan? responsePendingWindow = null)
        => new(client, ownsClient, responseWindow ?? UdsClientOptions.DefaultP2,
            responsePendingWindow ?? UdsClientOptions.DefaultP2Star);

    /// <summary>The underlying ISO-TP functional client.</summary>
    public IsoTpFunctionalClient Channel => _client;

    /// <summary>
    /// Sends <paramref name="request"/> on the functional identifier and collects every ECU's
    /// answer to <em>it</em> that arrives within <paramref name="window"/>: a positive response
    /// to the request's service, or a negative response naming it. Other traffic on the
    /// response identifiers -- another tester's answers, a late answer to an earlier request --
    /// is not attributed to this call. A request with suppressPosRspMsgIndication set is sent
    /// and not collected for: an empty list comes back as soon as the frame is confirmed.
    /// </summary>
    public async Task<IReadOnlyList<UdsFunctionalResponse>> SendRawAsync(ReadOnlyMemory<byte> request,
        TimeSpan window, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (request.Length == 0)
            throw new ArgumentException("Request must contain at least a SID byte.", nameof(request));

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetimeCts.Token);
        var linkedToken = linked.Token;

        await _requestLock.WaitAsync(linkedToken).ConfigureAwait(false);
        try
        {
            // Disposed while queued behind another call: the lock is released, not used.
            ThrowIfDisposed();
            // An earlier request for this service may still be answered -- a suppressed send,
            // or one whose collection window ended before the ECU's P2 did; that answer must
            // not land in this call's window (Codex on #150).
            await WaitOutOpenWindowAsync(request.Span[0], linkedToken).ConfigureAwait(false);
            return await SendRawLockedAsync(request, window, linkedToken).ConfigureAwait(false);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    private async Task<IReadOnlyList<UdsFunctionalResponse>> SendRawLockedAsync(ReadOnlyMemory<byte> request,
        TimeSpan window, CancellationToken cancellationToken)
    {
        byte sid = request.Span[0];
        if (request.Length >= 2 && HasSubFunction(sid)
            && (request.Span[1] & SuppressPositiveResponseBit) != 0)
        {
            // The listener is up before the frame goes out, so nothing an ECU sends back --
            // a negative answer, a 0x78 -- goes unobserved, and a send cancelled between the
            // driver's acceptance and the confirmation is covered (Codex on #150). Its window
            // is moved out to the confirmation afterwards.
            StartListening(sid, Stopwatch.GetTimestamp());
            await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            // Through StartListening again: a confirmation that outlasted the window has let
            // the listener retire, and the moved-out window needs one (Codex on #150).
            StartListening(sid, Stopwatch.GetTimestamp());
            return Array.Empty<UdsFunctionalResponse>();
        }

        // Checked before anything is transmitted: a window the collector would reject must
        // not leave a session change on every ECU behind an argument error (Codex on #150).
        if (window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window), window,
                "The collection window must be positive for a request that is answered.");

        // A read for more than one DID is answered with all of them in one PDU, which a Single
        // Frame cannot hold with their data; and only the first DID would be correlated here,
        // so two such reads sharing it could take each other's late answers (Codex on #150).
        if (sid == (byte)UdsServiceId.ReadDataByIdentifier && request.Length > 3)
            throw new ArgumentException(
                "A functional ReadDataByIdentifier reads one DID: a Single Frame cannot carry more, and only one is correlated.",
                nameof(request));

        // The listener is up before the send and outlives the collection: what the ECUs may
        // still send after the window ends is the remainder of their P2 from the request, and
        // a collection the caller cancels loses nothing the listener hears. Once the collection
        // is over, the window is moved out to the send's own instant plus P2: the collection
        // ran for `window` from the transmit confirmation, so that instant is at least now
        // less `window`, however long the confirmation took (Codex on #150).
        StartListening(sid, Stopwatch.GetTimestamp());
        IReadOnlyList<IsoTpFunctionalResponse> raw;
        try
        {
            raw = await _client.SendAndCollectAsync(request, window, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // A collection that ends in a cancellation or a transport fault may still have put
            // the request on the bus, and a confirmation that outlasted the window has let the
            // pre-send listener retire; the ECUs' P2 from the transmission is at most P2 from
            // now, so that window is noted before the exception leaves (Codex on #150).
            StartListening(sid, Stopwatch.GetTimestamp());
            throw;
        }
        var req = request.Span;
        byte positiveSid = (byte)(sid + 0x40);
        // A positive response echoes the request's leading parameter bytes -- the sub-function
        // (bit 7 cleared), a DID, a routine identifier, a block counter, an address and size --
        // so a late answer to an earlier request for another parameter, arriving in this
        // window, is told apart
        // (Codex on #150, twice). How many bytes, per service, is in EchoedRequestBytes.
        int echoed = Math.Min(EchoedRequestBytes(req), req.Length - 1);
        // The window as anchored at the transmission -- the collection ran for `window` from
        // the transmit confirmation, so the send was at least `window` ago -- noted before the
        // 0x78s are read: a listener that retired while the confirmation outlasted the
        // provisional window has forgotten it, and a 0x78 inside the ECU's P2 must still move
        // it out (Codex on #150).
        long transmitted = Stopwatch.GetTimestamp() - Ticks(window);
        lock (_listeners) _openWindows.Note(sid, transmitted, _responseWindow);
        var responses = new List<UdsFunctionalResponse>(raw.Count);
        foreach (var r in raw)
        {
            var data = r.Data;
            bool positive = data.Length >= 1 + echoed && data[0] == positiveSid && EchoMatches(req, data, echoed)
                && (sid != ReadDataByPeriodicIdentifierSid || NamesARequestedPeriodicIdentifier(req, data));
            bool negative = data.Length >= 3 && data[0] == NegativeResponseSid && data[1] == sid;
            // P2* runs from the 0x78's arrival, which the response carries. The listener sees
            // the same frame; the earlier of the two to act moves the window, the later is
            // idle. Only a 0x78 that arrived while the window was open: a collection window
            // longer than P2 may hold one from after it, which revives nothing (Codex on #150).
            if (IsResponsePending(data, sid))
                Extend(sid, r.HostArrivalTimestamp, r.HostArrivalTimestamp + Ticks(_responsePendingWindow));
            if (positive || negative) responses.Add(new UdsFunctionalResponse(r.SourceCanId, data));
        }
        // Through StartListening again, after the 0x78s above moved the window: a confirmation
        // that outlasted the window has let the listener retire, and the moved-out window
        // needs one (Codex on #150).
        StartListening(sid, transmitted);
        return responses;
    }

    // One listener per service, alive for the whole window: subscribed before the send so no
    // frame in the window goes unobserved, and owning the window so a caller's cancellation
    // loses nothing (Codex on #150). It moves the window out on every 0x78 it hears -- for its
    // own service in the table and for any other service with a window open -- and ends when
    // the window has run out, forgetting it. Started under the request lock.
    //
    // The subscription is made here, on the caller's stack, before StartListening returns and
    // so before the send: a 0x78 an ECU answers the instant the request is on the bus is
    // buffered for the listener's first collection, not lost to a listener still being
    // scheduled (Codex and Bugbot on #150). And it is one subscription for the listener's whole
    // life, so nothing arriving between two of its collections is lost either.
    //
    // Noting the window, starting a listener and retiring one happen under the listeners lock,
    // so a note that moves the window out either finds the listener still reading -- and it
    // re-reads the deadline before retiring -- or finds none and starts one; a listener cannot
    // retire, forgetting the window, between the note and the check (Codex on #150).
    private void StartListening(byte sid, long from)
    {
        lock (_listeners)
        {
            _openWindows.Note(sid, from, _responseWindow);
            EnsureListener(sid);
        }
    }

    // Under the listeners lock. Starts a listener for the service's window if the window is
    // open and none is reading it; forgets a window already over -- a collection that outlasted
    // P2 -- because a listener for it would only retire on its first read (Bugbot on #150).
    private void EnsureListener(byte sid)
    {
        if (_listeners.ContainsKey(sid)) return;
        if (!_openWindows.TryGetDeadline(sid, out var until)
            || SuppressedResponseWindows.Remaining(until) <= TimeSpan.Zero)
        {
            _openWindows.Forget(sid);
            return;
        }
        var ears = _client.Listen();
        // Started off this stack: its retirement takes this lock, so it runs after the entry.
        _listeners[sid] = (ears, Task.Run(() => ListenAsync(sid, ears)));
    }

    private void Extend(byte sid, long arrival, long until)
    {
        lock (_listeners) _openWindows.ExtendIfOpenAt(sid, arrival, until);
    }

    private async Task ListenAsync(byte sid, IsoTpFunctionalListener ears)
    {
        try
        {
            while (true)
            {
                TimeSpan remaining;
                lock (_listeners)
                {
                    // Retirement is decided under the lock that notes and moves the window,
                    // and the entry goes with it: a note after this reads no listener and
                    // starts one, a note before it moved the deadline this reads.
                    if (!_openWindows.TryGetDeadline(sid, out var until)
                        || (remaining = SuppressedResponseWindows.Remaining(until)) <= TimeSpan.Zero)
                    {
                        Retire(sid, ears);
                        return;
                    }
                }
                var heard = await ears.CollectAsync(remaining, _lifetimeCts.Token).ConfigureAwait(false);
                foreach (var pending in heard.Where(r => IsResponsePending(r.Data)))
                {
                    byte pendingSid = pending.Data[1];
                    // Its own window or another service's, but only one still open when the
                    // 0x78 arrived: a window that had run out, whose listener has not retired
                    // yet, is not revived for a full P2* by a late frame (Codex on #150).
                    lock (_listeners)
                    {
                        _openWindows.ExtendIfOpenAt(pendingSid, pending.HostArrivalTimestamp,
                            pending.HostArrivalTimestamp + Ticks(_responsePendingWindow));
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // disposed: the window dies with the client
            lock (_listeners) Retire(sid, ears);
        }
        catch (ObjectDisposedException)
        {
            // likewise
            lock (_listeners) Retire(sid, ears);
        }
    }

    // Under the listeners lock. Removes this listener's entry -- not a successor's -- and the
    // window with it, and ends its subscription.
    private void Retire(byte sid, IsoTpFunctionalListener ears)
    {
        if (_listeners.TryGetValue(sid, out var entry) && ReferenceEquals(entry.Ears, ears))
        {
            _listeners.Remove(sid);
            _openWindows.Forget(sid);
        }
        ears.Dispose();
    }

    // Under the request lock. Waits for the listener still open for this service, if any: an
    // earlier request may still be answered -- a suppressed send, or one whose collection
    // window ended before the ECU's P2 did -- and that answer must not land in this call's
    // window. The listener is not cancelled with the caller; it keeps the window. A window
    // moved out by a 0x78 another service's listener heard after this service's own retired
    // has no listener yet; it gets one here, and is waited out the same.
    private async Task WaitOutOpenWindowAsync(byte sid, CancellationToken cancellationToken)
    {
        Task? listener;
        lock (_listeners)
        {
            EnsureListener(sid);
            listener = _listeners.TryGetValue(sid, out var entry) ? entry.Run : null;
        }
        if (listener is null) return;
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), cancelled))
        {
            if (await Task.WhenAny(listener, cancelled.Task).ConfigureAwait(false) != listener)
                cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>
    /// TesterPresent to everyone (<c>3E 80</c>): the keep-alive that reaches every ECU with one
    /// frame. With the positive response suppressed, the default, nothing is collected and
    /// <paramref name="window"/> is ignored; without it, every ECU answers, and a
    /// <paramref name="window"/> to collect them in is required (Codex on #150).
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="suppressPositiveResponse"/> is <c>false</c> and no window was given.
    /// </exception>
    public async Task<IReadOnlyList<UdsFunctionalResponse>> TesterPresentAsync(
        bool suppressPositiveResponse = true, TimeSpan? window = null,
        CancellationToken cancellationToken = default)
    {
        if (!suppressPositiveResponse && window is null)
            throw new ArgumentNullException(nameof(window),
                "An unsuppressed TesterPresent is answered by every ECU; give a window to collect the answers in.");
        byte sub = suppressPositiveResponse ? SuppressPositiveResponseBit : (byte)0x00;
        return await SendRawAsync(new byte[] { (byte)UdsServiceId.TesterPresent, sub },
            window ?? TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// DiagnosticSessionControl to everyone, collecting each ECU's answer within
    /// <paramref name="window"/>. Bit 7 of the session type is not accepted, as on
    /// <see cref="IUdsClient.DiagnosticSessionControlAsync(byte, CancellationToken)"/>.
    /// </summary>
    public async Task<IReadOnlyList<UdsFunctionalResponse>> DiagnosticSessionControlAsync(
        UdsSessionType session, TimeSpan window, CancellationToken cancellationToken = default)
    {
        byte sub = (byte)session;
        if (sub == 0 || (sub & SuppressPositiveResponseBit) != 0)
            throw new ArgumentOutOfRangeException(nameof(session), session, "Session type must be 0x01..0x7F.");
        return await SendRawAsync(new byte[] { (byte)UdsServiceId.DiagnosticSessionControl, sub },
            window, cancellationToken).ConfigureAwait(false);
    }

    // How many request bytes after the SID a positive response repeats, for the services
    // whose response layout starts with them (ISO 14229-1, the response tables of each
    // service): the sub-function where there is one, then a DID (0x22 the first, 0x24, 0x2E,
    // 0x2F -- Codex on #150), the sub-function and DID (0x2C), the routine identifier (0x31),
    // the block sequence counter (0x36), and for WriteMemoryByAddress (0x3D) the
    // addressAndLengthFormatIdentifier with the address and size it sizes -- its low nibble
    // the address's bytes, its high nibble the size's (Codex on #150).
    private static int EchoedRequestBytes(ReadOnlySpan<byte> request)
    {
        byte sid = request[0];
        return sid switch
        {
            0x22 or 0x24 or 0x2E or 0x2F => 2,
            0x2C or 0x31 => 3,
            0x36 => 1,
            0x3D => request.Length < 2 ? 0 : 1 + (request[1] & 0x0F) + (request[1] >> 4),
            _ => HasSubFunction(sid) ? 1 : 0,
        };
    }

    // 0x2A echoes nothing: its positive response starts with the periodic identifier it carries
    // data for, which must be one the request asked for (bytes after the transmission mode).
    private static bool NamesARequestedPeriodicIdentifier(ReadOnlySpan<byte> request, byte[] response)
    {
        if (response.Length < 2) return false;
        for (int i = 2; i < request.Length; i++)
        {
            if (request[i] == response[1]) return true;
        }
        return false;
    }

    private static bool EchoMatches(ReadOnlySpan<byte> request, byte[] response, int echoed)
    {
        for (int i = 0; i < echoed; i++)
        {
            byte expected = request[1 + i];
            if (i == 0 && HasSubFunction(request[0])) expected &= unchecked((byte)~SuppressPositiveResponseBit);
            if (response[1 + i] != expected) return false;
        }
        return true;
    }

    private static long Ticks(TimeSpan span) => (long)(span.TotalSeconds * Stopwatch.Frequency);

    private static bool IsResponsePending(byte[] data, byte sid)
        => IsResponsePending(data) && data[1] == sid;

    private static bool IsResponsePending(byte[] data)
        => data.Length >= 3 && data[0] == NegativeResponseSid && data[2] == NrcResponsePending;

    // Mirrors UdsClientImpl.HasSubFunction (ISO 14229-1 table 2). Not 0x2A: its
    // transmissionMode is a plain parameter, and its response echoes nothing (Codex on #150).
    private static bool HasSubFunction(byte sid) => sid switch
    {
        0x10 or 0x11 or 0x19 or 0x27 or 0x28 or 0x29 or 0x2C or 0x31 or 0x3E
            or 0x83 or 0x85 or 0x86 or 0x87 => true,
        _ => false,
    };

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(UdsFunctionalClient));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _lifetimeCts.Cancel(); } catch (ObjectDisposedException) { /* torn down elsewhere */ }
        if (_ownsClient) _client.Dispose();
        _lifetimeCts.Dispose();
        // The lock is not disposed: a call still inside its window releases it on the way
        // out, and a SemaphoreSlim without a wait handle holds nothing that needs disposing.
    }
}
