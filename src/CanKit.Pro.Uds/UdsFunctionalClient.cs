using System;
using System.Collections.Generic;
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

    private readonly IsoTpFunctionalClient _client;
    private readonly bool _ownsClient;
    // One request on the wire at a time, its collection window included: overlapping calls
    // with the same SID would each collect the other's answers (Codex on #150), as the
    // physical client's request lock prevents there.
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    // Cancels a wait on the lock, and a send or window in progress, when the client is
    // disposed: a call queued behind another must not go out on a disposed client (Codex and
    // Bugbot on #150).
    private readonly CancellationTokenSource _lifetimeCts = new();
    private int _disposed;

    private UdsFunctionalClient(IsoTpFunctionalClient client, bool ownsClient)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ownsClient = ownsClient;
    }

    /// <summary>
    /// Wraps an open <see cref="IsoTpFunctionalClient"/>. With <paramref name="ownsClient"/>,
    /// disposing this client disposes it.
    /// </summary>
    public static UdsFunctionalClient Create(IsoTpFunctionalClient client, bool ownsClient = false)
        => new(client, ownsClient);

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
            return Array.Empty<UdsFunctionalResponse>();
        }

        var raw = await _client.SendAndCollectAsync(request, window, cancellationToken)
            .ConfigureAwait(false);
        byte sid = request.Span[0];
        byte positiveSid = (byte)(sid + 0x40);
        // A service with a sub-function echoes it in the positive response (bit 7 cleared), so
        // a late answer to an earlier request for another sub-function -- a session change to
        // Extended answered during the next one to Default -- is told apart (Codex on #150).
        int subFunction = HasSubFunction(sid) && request.Length >= 2
            ? request.Span[1] & ~SuppressPositiveResponseBit
            : -1;
        var responses = new List<UdsFunctionalResponse>(raw.Count);
        foreach (var r in raw)
        {
            // Correlated to this request the way the physical client correlates: the positive
            // response SID (and sub-function), or a negative response echoing the request's SID.
            var data = r.Data;
            bool positive = data.Length >= 1 && data[0] == positiveSid
                && (subFunction < 0 || data.Length >= 2 && data[1] == subFunction);
            bool negative = data.Length >= 3 && data[0] == NegativeResponseSid && data[1] == sid;
            if (positive || negative) responses.Add(new UdsFunctionalResponse(r.SourceCanId, data));
        }
        return responses;
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

    // Mirrors UdsClientImpl.HasSubFunction (ISO 14229-1 table 2).
    private static bool HasSubFunction(byte sid) => sid switch
    {
        0x10 or 0x11 or 0x19 or 0x27 or 0x28 or 0x29 or 0x2A or 0x2C or 0x31 or 0x3E
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
