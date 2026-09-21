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
        if (request.Length >= 2 && HasSubFunction(request.Span[0])
            && (request.Span[1] & SuppressPositiveResponseBit) != 0)
        {
            await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            _openWindows.Note(request.Span[0], Stopwatch.GetTimestamp(), _responseWindow);
            return Array.Empty<UdsFunctionalResponse>();
        }

        // A read for more than one DID is answered with all of them in one PDU, which a Single
        // Frame cannot hold with their data; and only the first DID would be correlated here,
        // so two such reads sharing it could take each other's late answers (Codex on #150).
        if (request.Span[0] == (byte)UdsServiceId.ReadDataByIdentifier && request.Length > 3)
            throw new ArgumentException(
                "A functional ReadDataByIdentifier reads one DID: a Single Frame cannot carry more, and only one is correlated.",
                nameof(request));

        // Noted before the send, so a collection that is cancelled or fails still leaves a
        // window in place (Bugbot on #150) -- a lower bound, since the send is later. Once the
        // collection is over, the window is moved out to the send's own instant plus P2: the
        // collection ran for `window` from the transmit confirmation, so that instant is at
        // least now less `window`, however long the confirmation took (Codex on #150).
        byte sid = request.Span[0];
        _openWindows.Note(sid, Stopwatch.GetTimestamp(), _responseWindow);
        var raw = await _client.SendAndCollectAsync(request, window, cancellationToken)
            .ConfigureAwait(false);
        _openWindows.Note(sid, Stopwatch.GetTimestamp() - Ticks(window), _responseWindow);
        var req = request.Span;
        byte positiveSid = (byte)(sid + 0x40);
        // A positive response echoes the request's leading parameter bytes -- the sub-function
        // (bit 7 cleared), a DID, a routine identifier, a block counter -- so a late answer to
        // an earlier request for another parameter, arriving in this window, is told apart
        // (Codex on #150, twice). How many bytes, per service, is in EchoedRequestBytes.
        int echoed = Math.Min(EchoedRequestBytes(sid), req.Length - 1);
        var responses = new List<UdsFunctionalResponse>(raw.Count);
        foreach (var r in raw)
        {
            var data = r.Data;
            bool positive = data.Length >= 1 + echoed && data[0] == positiveSid && EchoMatches(req, data, echoed)
                && (sid != ReadDataByPeriodicIdentifierSid || NamesARequestedPeriodicIdentifier(req, data));
            bool negative = data.Length >= 3 && data[0] == NegativeResponseSid && data[1] == sid;
            if (IsResponsePending(data, sid))
                _openWindows.Extend(sid, Stopwatch.GetTimestamp() + (long)(_responsePendingWindow.TotalSeconds * Stopwatch.Frequency));
            if (positive || negative) responses.Add(new UdsFunctionalResponse(r.SourceCanId, data));
        }
        return responses;
    }

    // Under the request lock. Waits out the window still open for this service, listening the
    // while: an NRC 0x78 in it says an ECU's final answer is still coming and moves the window
    // out by P2* (Codex on #150). Everything heard belongs to the earlier request and is dropped.
    private async Task WaitOutOpenWindowAsync(byte sid, CancellationToken cancellationToken)
    {
        if (!_openWindows.TryGetDeadline(sid, out var until)) return;
        bool waitedOut = false;
        try
        {
            while (true)
            {
                var remaining = SuppressedResponseWindows.Remaining(until);
                if (remaining <= TimeSpan.Zero) break;
                var heard = await _client.CollectResponsesAsync(remaining, cancellationToken).ConfigureAwait(false);
                if (heard.Any(r => IsResponsePending(r.Data, sid)))
                    until = Math.Max(until, Stopwatch.GetTimestamp() + (long)(_responsePendingWindow.TotalSeconds * Stopwatch.Frequency));
            }
            waitedOut = true;
        }
        finally
        {
            // A cancelled wait keeps what remains of the window, extensions included, for the
            // next call (Bugbot on #150).
            if (waitedOut) _openWindows.Forget(sid);
            else _openWindows.Extend(sid, until);
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
    // service): the sub-function where there is one, then a DID (0x22 the first, 0x2E), the
    // routine identifier (0x31), the block sequence counter (0x36).
    private static int EchoedRequestBytes(byte sid) => sid switch
    {
        0x22 or 0x2E => 2,
        0x31 => 3,
        0x36 => 1,
        _ => HasSubFunction(sid) ? 1 : 0,
    };

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
        => data.Length >= 3 && data[0] == NegativeResponseSid && data[1] == sid && data[2] == NrcResponsePending;

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
