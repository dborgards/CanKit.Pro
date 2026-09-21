using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Pro.IsoTp;

namespace CanKit.Pro.Uds;

/// <summary>
/// Default <see cref="IUdsClient"/> implementation. Owns a single request lock so at most one
/// UDS request is on the wire at any time (ISO 14229-1 §7.3), plus the P2/P2* wait loop that
/// implements NRC 0x78 handling.
/// </summary>
/// <remarks>
/// <para>
/// The client makes no assumptions about how the underlying <see cref="IIsoTpChannel"/> handles
/// concurrency — every write is a single <see cref="IIsoTpChannel.SendAsync"/> and every read
/// is a single <see cref="IIsoTpChannel.ReceiveAsync"/>. Because we hold the request lock
/// across the send + wait, we know the next reassembled PDU on the channel belongs to the
/// current request (the ECU only speaks in response to a request, ISO 14229-1 §7.3).
/// Multi-step services such as SecurityAccess and DownloadAsync keep that same lock across
/// every on-the-wire exchange so keep-alive traffic cannot interleave.
/// </para>
/// <para>
/// Before each send (and on abort paths) the client calls
/// <see cref="IIsoTpChannel.DiscardPendingPdus"/> so a late ECU reply from a cancelled or
/// timed-out wait cannot be consumed as the answer to a later request. SID correlation during
/// the wait loop remains as a second line of defense for stray frames that arrive while a
/// request is still outstanding.
/// </para>
/// </remarks>
internal sealed class UdsClientImpl : IUdsClient
{
    private const byte NegativeResponseSid = 0x7F;
    private const byte PositiveResponseOffset = 0x40;
    private const byte NrcResponsePending = 0x78;
    private const byte SuppressPositiveResponseBit = 0x80;
    private const byte NrcBusyRepeatRequest = 0x21;

    /// <summary>How long Dispose waits for a request in flight to release the lock.</summary>
    internal TimeSpan DisposeLockTimeout { get; set; } = TimeSpan.FromSeconds(5);

    // A suppressed send draws no positive response but may still draw a negative one, up to P2
    // after it went out. A following request for the same service would take that negative
    // response as its own; it waits until the window is over instead (Codex on #150).
    private readonly SuppressedResponseWindows _suppressedWindows = new();

    private readonly IIsoTpChannel _channel;
    private readonly bool _ownsChannel;
    private readonly UdsClientOptions _options;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();

    private byte _currentSession = (byte)UdsSessionType.Default;
    private TesterPresentKeepAlive? _keepAlive;
    private int _disposed;

    public UdsClientImpl(IIsoTpChannel channel, UdsClientOptions options, bool ownsChannel)
    {
        _channel = channel;
        _options = options;
        _ownsChannel = ownsChannel;

        if (options.P2ClientMax <= TimeSpan.Zero)
            throw new ArgumentException("P2ClientMax must be positive.", nameof(options));
        if (options.P2StarClientMax <= TimeSpan.Zero)
            throw new ArgumentException("P2StarClientMax must be positive.", nameof(options));
        if (options.MaxBusyRepeatRequests < 0)
            throw new ArgumentOutOfRangeException(nameof(options),
                "MaxBusyRepeatRequests must be >= 0 (0 disables the repeat).");
        if (options.BusyRepeatRequestDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options),
                "BusyRepeatRequestDelay must not be negative.");
        if (options.MaxResponsePendingCount < 0)
            throw new ArgumentException("MaxResponsePendingCount must be non-negative.", nameof(options));
        if (options.TesterPresentPeriod <= TimeSpan.Zero)
            throw new ArgumentException("TesterPresentPeriod must be positive.", nameof(options));
    }

    public IIsoTpChannel Channel => _channel;
    public UdsClientOptions Options => _options;
    public byte CurrentSession => Volatile.Read(ref _currentSession);

    // ---------------------------------------------------------------------------------------
    // Public service methods (see IUdsClient for XML docs).
    // ---------------------------------------------------------------------------------------

    public async Task<byte[]> DiagnosticSessionControlAsync(UdsSessionType session,
        CancellationToken cancellationToken = default)
        => await DiagnosticSessionControlAsync((byte)session, cancellationToken).ConfigureAwait(false);

    public async Task<byte[]> DiagnosticSessionControlAsync(byte sessionType,
        CancellationToken cancellationToken = default)
    {
        // The sub-function byte's bit 7 is suppressPosRspMsgIndication, not part of the session
        // type; a caller passing 0x83 meant something this method does not do (it waits for
        // the response), and masking it to 0x03 hid that (#57). 0x00 is ISOSAEReserved.
        if (sessionType == 0 || (sessionType & SuppressPositiveResponseBit) != 0)
            throw new ArgumentOutOfRangeException(nameof(sessionType), sessionType,
                "Session type must be 0x01..0x7F; bit 7 is suppressPosRspMsgIndication and is not accepted here.");
        byte sub = sessionType;
        var request = new byte[] { (byte)UdsServiceId.DiagnosticSessionControl, sub };
        var response = await ExecuteAsync(UdsServiceId.DiagnosticSessionControl, request,
            cancellationToken).ConfigureAwait(false);

        // Positive response layout (ISO 14229-1 §9.2.2.4):
        //   [0]=0x50 [1]=sessionType [2..5]=sessionParameterRecord (P2_server/P2*_server timing)
        // Some ECUs omit the parameter record on legacy sessions; we accept >= 2 bytes.
        if (response.Length < 2 || response[1] != sub)
            throw new UdsProtocolException(
                $"DiagnosticSessionControl response sub-function mismatch (expected 0x{sub:X2}, got payload length {response.Length}).");

        Volatile.Write(ref _currentSession, sub);
        int recordLength = response.Length - 2;
        var record = new byte[recordLength];
        if (recordLength > 0) Buffer.BlockCopy(response, 2, record, 0, recordLength);
        return record;
    }

    public async Task<byte[]> EcuResetAsync(UdsEcuResetType resetType,
        CancellationToken cancellationToken = default)
    {
        var request = new byte[] { (byte)UdsServiceId.EcuReset, (byte)resetType };
        var response = await ExecuteAsync(UdsServiceId.EcuReset, request,
            cancellationToken).ConfigureAwait(false);

        // Positive response layout: [0]=0x51 [1]=resetType [2]?=powerDownTime.
        if (response.Length < 2 || response[1] != (byte)resetType)
            throw new UdsProtocolException(
                $"ECUReset response reset-type mismatch (expected 0x{(byte)resetType:X2}, got payload length {response.Length}).");

        // ISO 14229-1: after a successful ECUReset the server returns to the default session.
        // Mirror that in CurrentSession so later calls do not assume a stale negotiated session.
        Volatile.Write(ref _currentSession, (byte)UdsSessionType.Default);

        int tail = response.Length - 2;
        var record = new byte[tail];
        if (tail > 0) Buffer.BlockCopy(response, 2, record, 0, tail);
        return record;
    }

    public async Task<byte[]> ReadDataByIdentifierAsync(ushort dataIdentifier,
        CancellationToken cancellationToken = default)
    {
        var request = new byte[]
        {
            (byte)UdsServiceId.ReadDataByIdentifier,
            (byte)(dataIdentifier >> 8),
            (byte)(dataIdentifier & 0xFF),
        };
        var response = await ExecuteAsync(UdsServiceId.ReadDataByIdentifier, request,
            cancellationToken).ConfigureAwait(false);

        // Positive response: [0]=0x62 [1..2]=DID [3..]=dataRecord.
        if (response.Length < 3)
            throw new UdsProtocolException(
                $"ReadDataByIdentifier response too short ({response.Length} bytes).");
        ushort echoed = (ushort)((response[1] << 8) | response[2]);
        if (echoed != dataIdentifier)
            throw new UdsProtocolException(
                $"ReadDataByIdentifier response DID mismatch (requested 0x{dataIdentifier:X4}, got 0x{echoed:X4}).");
        int len = response.Length - 3;
        var data = new byte[len];
        if (len > 0) Buffer.BlockCopy(response, 3, data, 0, len);
        return data;
    }

    public async Task<IReadOnlyDictionary<ushort, byte[]>> ReadDataByIdentifierAsync(
        IReadOnlyList<ushort> dataIdentifiers,
        IReadOnlyDictionary<ushort, int> dataRecordLengths,
        CancellationToken cancellationToken = default)
    {
        if (dataIdentifiers is null) throw new ArgumentNullException(nameof(dataIdentifiers));
        if (dataRecordLengths is null) throw new ArgumentNullException(nameof(dataRecordLengths));
        if (dataIdentifiers.Count == 0)
            throw new ArgumentException("At least one DID is required.", nameof(dataIdentifiers));

        var requested = new HashSet<ushort>();
        foreach (var did in dataIdentifiers)
        {
            if (!requested.Add(did))
                throw new ArgumentException(
                    $"Duplicate DID 0x{did:X4} in multi-DID request.", nameof(dataIdentifiers));
            if (!dataRecordLengths.TryGetValue(did, out int len))
                throw new ArgumentException(
                    $"Missing dataRecord length for DID 0x{did:X4}.", nameof(dataRecordLengths));
            if (len < 0)
                throw new ArgumentException(
                    $"dataRecord length for DID 0x{did:X4} must be non-negative.",
                    nameof(dataRecordLengths));
        }

        var request = new byte[1 + dataIdentifiers.Count * 2];
        request[0] = (byte)UdsServiceId.ReadDataByIdentifier;
        for (int i = 0; i < dataIdentifiers.Count; i++)
        {
            request[1 + i * 2] = (byte)(dataIdentifiers[i] >> 8);
            request[2 + i * 2] = (byte)(dataIdentifiers[i] & 0xFF);
        }

        var response = await ExecuteAsync(UdsServiceId.ReadDataByIdentifier, request,
            cancellationToken).ConfigureAwait(false);

        // Multi-DID positive response (ISO 14229-1 §9.3.4.4): [0]=0x62 then
        // (DID[2 bytes] + dataRecord[len bytes])* for every returned DID. Lengths are not on
        // the wire — parse strictly from the caller-supplied DID definition so payload bytes
        // that happen to match another DID are never treated as record boundaries.
        var result = new Dictionary<ushort, byte[]>(dataIdentifiers.Count);
        int cursor = 1;
        while (cursor < response.Length)
        {
            if (cursor + 2 > response.Length)
                throw new UdsProtocolException(
                    $"Multi-DID ReadDataByIdentifier response truncated while reading DID at offset {cursor}.");

            ushort did = (ushort)((response[cursor] << 8) | response[cursor + 1]);
            if (!requested.Contains(did))
                throw new UdsProtocolException(
                    $"Multi-DID ReadDataByIdentifier response contains unexpected DID 0x{did:X4}.");
            if (result.ContainsKey(did))
                throw new UdsProtocolException(
                    $"Multi-DID ReadDataByIdentifier response contains duplicate DID 0x{did:X4}.");

            int recLen = dataRecordLengths[did];
            int dataStart = cursor + 2;
            if (dataStart + recLen > response.Length)
                throw new UdsProtocolException(
                    $"Multi-DID ReadDataByIdentifier response truncated for DID 0x{did:X4} " +
                    $"(expected {recLen} data bytes, {response.Length - dataStart} remain).");

            var rec = new byte[recLen];
            if (recLen > 0) Buffer.BlockCopy(response, dataStart, rec, 0, recLen);
            result[did] = rec;
            cursor = dataStart + recLen;
        }

        if (result.Count != dataIdentifiers.Count)
        {
            var missing = new List<string>();
            foreach (var did in dataIdentifiers.Where(did => !result.ContainsKey(did)))
                missing.Add($"0x{did:X4}");
            throw new UdsProtocolException(
                $"Multi-DID ReadDataByIdentifier response missing DIDs: {string.Join(", ", missing)}.");
        }

        return result;
    }

    public async Task WriteDataByIdentifierAsync(ushort dataIdentifier, ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        var request = new byte[3 + data.Length];
        request[0] = (byte)UdsServiceId.WriteDataByIdentifier;
        request[1] = (byte)(dataIdentifier >> 8);
        request[2] = (byte)(dataIdentifier & 0xFF);
        if (data.Length > 0) data.Span.CopyTo(request.AsSpan(3));

        var response = await ExecuteAsync(UdsServiceId.WriteDataByIdentifier, request,
            cancellationToken).ConfigureAwait(false);

        // Positive response: [0]=0x6E [1..2]=DID.
        if (response.Length < 3)
            throw new UdsProtocolException(
                $"WriteDataByIdentifier response too short ({response.Length} bytes).");
        ushort echoed = (ushort)((response[1] << 8) | response[2]);
        if (echoed != dataIdentifier)
            throw new UdsProtocolException(
                $"WriteDataByIdentifier response DID mismatch (wrote 0x{dataIdentifier:X4}, got 0x{echoed:X4}).");
    }

    public async Task<byte[]> RoutineControlAsync(UdsRoutineControlType routineType,
        ushort routineIdentifier, ReadOnlyMemory<byte> routineControlOptionRecord = default,
        CancellationToken cancellationToken = default)
    {
        var request = new byte[4 + routineControlOptionRecord.Length];
        request[0] = (byte)UdsServiceId.RoutineControl;
        request[1] = (byte)routineType;
        request[2] = (byte)(routineIdentifier >> 8);
        request[3] = (byte)(routineIdentifier & 0xFF);
        if (routineControlOptionRecord.Length > 0)
            routineControlOptionRecord.Span.CopyTo(request.AsSpan(4));

        var response = await ExecuteAsync(UdsServiceId.RoutineControl, request,
            cancellationToken).ConfigureAwait(false);

        // Positive response: [0]=0x71 [1]=sub [2..3]=routineId [4..]=routineInfo+statusRecord.
        if (response.Length < 4 || response[1] != (byte)routineType)
            throw new UdsProtocolException(
                $"RoutineControl response sub-function mismatch (expected 0x{(byte)routineType:X2}).");
        ushort echoed = (ushort)((response[2] << 8) | response[3]);
        if (echoed != routineIdentifier)
            throw new UdsProtocolException(
                $"RoutineControl response routineId mismatch (requested 0x{routineIdentifier:X4}, got 0x{echoed:X4}).");
        int tail = response.Length - 4;
        var info = new byte[tail];
        if (tail > 0) Buffer.BlockCopy(response, 4, info, 0, tail);
        return info;
    }

    public async Task SecurityAccessAsync(byte requestSeedLevel, Func<byte[], byte[]> computeKey,
        CancellationToken cancellationToken = default)
    {
        if (computeKey is null) throw new ArgumentNullException(nameof(computeKey));
        if (requestSeedLevel == 0 || requestSeedLevel >= 0x7F || (requestSeedLevel & 0x01) == 0)
            throw new ArgumentOutOfRangeException(nameof(requestSeedLevel),
                "SecurityAccess requestSeedLevel must be an odd byte in 0x01..0x7F.");

        byte sendKeyLevel = (byte)(requestSeedLevel + 1);

        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetimeCts.Token);
        var linkedToken = linked.Token;

        // Hold the request lock across seed + sendKey so TesterPresent keep-alive (or another
        // UDS call) cannot interleave and break the ISO 14229-1 security-access sequence
        // (NRC requestSequenceError on real ECUs).
        await _requestLock.WaitAsync(linkedToken).ConfigureAwait(false);
        try
        {
            var seedRequest = new byte[] { (byte)UdsServiceId.SecurityAccess, requestSeedLevel };
            var seedResponse = await ExecuteCoreAsync(UdsServiceId.SecurityAccess, seedRequest,
                linkedToken).ConfigureAwait(false);

            // Positive response: [0]=0x67 [1]=requestSeedLevel [2..]=seed. A seed of all zeroes
            // means "already unlocked" per ISO 14229-1 §9.4.5.3 -- the length is the server's
            // usual seed length, the bytes are 0x00 -- and the client MUST NOT send the key
            // (#29): a key computed for that seed gets NRC 0x24 back. A zero-length seed is kept
            // as the defensive reading of the same answer.
            if (seedResponse.Length < 2 || seedResponse[1] != requestSeedLevel)
                throw new UdsProtocolException(
                    $"SecurityAccess seed response sub-function mismatch (expected 0x{requestSeedLevel:X2}).");
            int seedLen = seedResponse.Length - 2;
            if (seedLen == 0) return;
            if (IsAllZero(seedResponse, 2, seedLen)) return;

            var seed = new byte[seedLen];
            Buffer.BlockCopy(seedResponse, 2, seed, 0, seedLen);
            byte[] key = computeKey(seed)
                ?? throw new UdsProtocolException("SecurityAccess computeKey callback returned null.");
            if (key.Length == 0)
                throw new UdsProtocolException("SecurityAccess computeKey callback returned an empty key.");

            var keyRequest = new byte[2 + key.Length];
            keyRequest[0] = (byte)UdsServiceId.SecurityAccess;
            keyRequest[1] = sendKeyLevel;
            Buffer.BlockCopy(key, 0, keyRequest, 2, key.Length);

            var keyResponse = await ExecuteCoreAsync(UdsServiceId.SecurityAccess, keyRequest,
                linkedToken).ConfigureAwait(false);
            if (keyResponse.Length < 2 || keyResponse[1] != sendKeyLevel)
                throw new UdsProtocolException(
                    $"SecurityAccess sendKey response sub-function mismatch (expected 0x{sendKeyLevel:X2}).");
        }
        finally
        {
            _requestLock.Release();
        }
    }

    public async Task TesterPresentAsync(bool suppressPositiveResponse = true,
        CancellationToken cancellationToken = default)
    {
        byte sub = suppressPositiveResponse ? SuppressPositiveResponseBit : (byte)0x00;
        var request = new byte[] { (byte)UdsServiceId.TesterPresent, sub };

        if (suppressPositiveResponse)
        {
            await SendWithoutResponseAsync(request, cancellationToken).ConfigureAwait(false);
            return;
        }

        var response = await ExecuteAsync(UdsServiceId.TesterPresent, request,
            cancellationToken).ConfigureAwait(false);
        if (response.Length < 2 || response[1] != 0x00)
            throw new UdsProtocolException(
                $"TesterPresent response sub-function mismatch (expected 0x00, got payload length {response.Length}).");
    }

    public IDisposable StartTesterPresentKeepAlive(TimeSpan? period = null)
    {
        ThrowIfDisposed();
        var interval = period ?? _options.TesterPresentPeriod;
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(period), "TesterPresent period must be positive.");

        var candidate = new TesterPresentKeepAlive(this, interval,
            _options.KeepAliveSuppressPositiveResponse);
        if (Interlocked.CompareExchange(ref _keepAlive, candidate, null) is not null)
        {
            candidate.Dispose();
            throw new InvalidOperationException(
                "A TesterPresent keep-alive is already running; dispose it before starting another.");
        }
        candidate.Start();
        return candidate;
    }

    public async Task<byte[]> SendRawAsync(ReadOnlyMemory<byte> request,
        CancellationToken cancellationToken = default)
    {
        if (request.Length == 0)
            throw new ArgumentException("Request must contain at least a SID byte.", nameof(request));
        var sid = (UdsServiceId)request.Span[0];
        var copy = new byte[request.Length];
        request.Span.CopyTo(copy);

        // A request with suppressPosRspMsgIndication set gets no positive response; waiting P2
        // for one ended in a timeout every time (#57). Sent the way a suppressed TesterPresent
        // is, and an empty response returned. A negative response the server may still send is
        // not waited for either -- the next request's discard drops it.
        if (copy.Length >= 2 && HasSubFunction(sid) && (copy[1] & SuppressPositiveResponseBit) != 0)
        {
            await SendWithoutResponseAsync(copy, cancellationToken).ConfigureAwait(false);
            return Array.Empty<byte>();
        }

        return await ExecuteAsync(sid, copy, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Fire-and-forget under the request lock: the frame goes out without interleaving a real
    /// request, and no response is waited for. The lifetime token is linked so Dispose()
    /// cancels a wait or send still in progress (Bugbot 3596586770), as ExecuteAsync does.
    /// </summary>
    private async Task SendWithoutResponseAsync(byte[] request, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetimeCts.Token);
        var linkedToken = linked.Token;

        await _requestLock.WaitAsync(linkedToken).ConfigureAwait(false);
        try
        {
            // Noted before the send as well: cancelled between the driver's acceptance and the
            // confirmation, the frame is on the bus and may still be answered (Codex on #150).
            // Moved out to the transmit stamp afterwards.
            _suppressedWindows.Note(request[0], Stopwatch.GetTimestamp(), _options.P2ClientMax);
            var stamps = await _channel.SendWithTransmitStampAsync(request, linkedToken).ConfigureAwait(false);
            var sent = stamps.LastFrameTransmitTimestamp > 0 ? stamps.LastFrameTransmitTimestamp : Stopwatch.GetTimestamp();
            _suppressedWindows.Note(request[0], sent, _options.P2ClientMax);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    // Under the request lock. A request for a service with a suppressed send still open waits
    // out that send's P2, reading what arrives: it is the suppressed send's and is dropped --
    // except NRC 0x78, which says the peer's final answer is still coming and moves the window
    // out by P2* (Codex on #150). So a late negative response to the suppressed send cannot
    // be taken for this request's.
    private async Task WaitOutSuppressedResponseWindowAsync(UdsServiceId serviceId, CancellationToken linkedToken)
    {
        byte sid = (byte)serviceId;
        if (!_suppressedWindows.TryGetDeadline(sid, out var until)) return;
        bool waitedOut = false;
        try
        {
            while (true)
            {
                var remaining = SuppressedResponseWindows.Remaining(until);
                if (remaining <= TimeSpan.Zero)
                {
                    // The window is over as measured now -- but a 0x78 may be queued already,
                    // and it moves the window out (Bugbot on #150). Only an empty inbox ends it.
                    if (DrainExtends(sid, ref until)) continue;
                    break;
                }
                using var slice = new CancellationTokenSource(remaining);
                using var combined = CancellationTokenSource.CreateLinkedTokenSource(linkedToken, slice.Token);
                IsoTpReceivedPdu pdu;
                try
                {
                    pdu = await _channel.ReceiveWithArrivalAsync(combined.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (slice.IsCancellationRequested && !linkedToken.IsCancellationRequested)
                {
                    if (DrainExtends(sid, ref until)) continue;
                    break;
                }
                ExtendOnPending(sid, pdu, ref until);
            }
            waitedOut = true;
        }
        finally
        {
            // A cancelled wait keeps what remains of the window, extensions included, for the
            // next request (Bugbot on #150).
            if (waitedOut) _suppressedWindows.Forget(sid);
            else _suppressedWindows.Extend(sid, until);
        }
    }

    // Reads whatever is queued; true when a 0x78 among it moved the window out.
    private bool DrainExtends(byte sid, ref long until)
    {
        bool extended = false;
        while (_channel.TryReceiveWithArrival(out var queued))
            extended |= ExtendOnPending(sid, queued, ref until);
        return extended;
    }

    private bool ExtendOnPending(byte sid, in IsoTpReceivedPdu pdu, ref long until)
    {
        var data = pdu.Pdu;
        if (data.Length < 3 || data[0] != NegativeResponseSid || data[1] != sid || data[2] != NrcResponsePending)
            return false;
        var extendedUntil = pdu.FirstFrameArrivalTimestamp + (long)(_options.P2StarClientMax.TotalSeconds * Stopwatch.Frequency);
        if (extendedUntil <= until) return false;
        until = extendedUntil;
        return true;
    }

    // The services whose second byte is a sub-function parameter, and so carry the
    // suppressPosRspMsgIndication bit (ISO 14229-1 table 2, "sub-function" column). Not 0x2A:
    // its transmissionMode is a plain parameter (Codex on #150).
    private static bool HasSubFunction(UdsServiceId sid) => (byte)sid switch
    {
        0x10 or 0x11 or 0x19 or 0x27 or 0x28 or 0x29 or 0x2C or 0x31 or 0x3E
            or 0x83 or 0x85 or 0x86 or 0x87 => true,
        _ => false,
    };

    // ---------------------------------------------------------------------------------------
    // Upload / Download (SRS FR-UDS-012, ISO 14229-1 §14).
    // ---------------------------------------------------------------------------------------

    public async Task<UdsDownloadResponse> RequestDownloadAsync(
        byte dataFormatIdentifier,
        byte addressAndLengthFormatIdentifier,
        ReadOnlyMemory<byte> memoryAddress,
        ReadOnlyMemory<byte> memorySize,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetimeCts.Token);
        var linkedToken = linked.Token;

        await _requestLock.WaitAsync(linkedToken).ConfigureAwait(false);
        try
        {
            return await RequestTransferSetupCoreAsync(
                UdsServiceId.RequestDownload,
                dataFormatIdentifier,
                addressAndLengthFormatIdentifier,
                memoryAddress,
                memorySize,
                (lfid, maxBlock) => new UdsDownloadResponse(lfid, maxBlock),
                linkedToken).ConfigureAwait(false);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    public async Task<UdsUploadResponse> RequestUploadAsync(
        byte dataFormatIdentifier,
        byte addressAndLengthFormatIdentifier,
        ReadOnlyMemory<byte> memoryAddress,
        ReadOnlyMemory<byte> memorySize,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetimeCts.Token);
        var linkedToken = linked.Token;

        await _requestLock.WaitAsync(linkedToken).ConfigureAwait(false);
        try
        {
            return await RequestTransferSetupCoreAsync(
                UdsServiceId.RequestUpload,
                dataFormatIdentifier,
                addressAndLengthFormatIdentifier,
                memoryAddress,
                memorySize,
                (lfid, maxBlock) => new UdsUploadResponse(lfid, maxBlock),
                linkedToken).ConfigureAwait(false);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    /// <summary>
    /// Assumes <see cref="_requestLock"/> is already held. Builds the 0x34/0x35 request, hands
    /// it to <see cref="ExecuteCoreAsync"/>, and parses the maxNumberOfBlockLength.
    /// </summary>
    private async Task<TResult> RequestTransferSetupCoreAsync<TResult>(
        UdsServiceId serviceId,
        byte dataFormatIdentifier,
        byte addressAndLengthFormatIdentifier,
        ReadOnlyMemory<byte> memoryAddress,
        ReadOnlyMemory<byte> memorySize,
        Func<byte, ulong, TResult> project,
        CancellationToken linkedToken)
    {
        int addressWidth = addressAndLengthFormatIdentifier & 0x0F;
        int sizeWidth = (addressAndLengthFormatIdentifier >> 4) & 0x0F;
        if (addressWidth == 0)
            throw new ArgumentOutOfRangeException(nameof(addressAndLengthFormatIdentifier),
                "memoryAddress width nibble (low nibble) must be non-zero.");
        if (sizeWidth == 0)
            throw new ArgumentOutOfRangeException(nameof(addressAndLengthFormatIdentifier),
                "memorySize width nibble (high nibble) must be non-zero.");
        if (memoryAddress.Length != addressWidth)
            throw new ArgumentOutOfRangeException(nameof(memoryAddress),
                $"memoryAddress length ({memoryAddress.Length}) does not match the width nibble ({addressWidth}) in addressAndLengthFormatIdentifier.");
        if (memorySize.Length != sizeWidth)
            throw new ArgumentOutOfRangeException(nameof(memorySize),
                $"memorySize length ({memorySize.Length}) does not match the width nibble ({sizeWidth}) in addressAndLengthFormatIdentifier.");

        var request = new byte[3 + addressWidth + sizeWidth];
        request[0] = (byte)serviceId;
        request[1] = dataFormatIdentifier;
        request[2] = addressAndLengthFormatIdentifier;
        memoryAddress.Span.CopyTo(request.AsSpan(3));
        memorySize.Span.CopyTo(request.AsSpan(3 + addressWidth));

        var response = await ExecuteCoreAsync(serviceId, request, linkedToken).ConfigureAwait(false);

        // Positive response layout (ISO 14229-1 §14.2.2.4 / §14.1.2.4):
        //   [0]=respSid  [1]=lengthFormatIdentifier  [2..]=maxNumberOfBlockLength (big-endian).
        if (response.Length < 2)
            throw new UdsProtocolException(
                $"{serviceId} response too short ({response.Length} bytes).");

        byte lengthFormatIdentifier = response[1];
        int maxBlockWidth = (lengthFormatIdentifier >> 4) & 0x0F;
        if (maxBlockWidth == 0)
            throw new UdsProtocolException(
                $"{serviceId} response lengthFormatIdentifier 0x{lengthFormatIdentifier:X2} has zero maxNumberOfBlockLength width.");
        if (maxBlockWidth > 8)
            throw new UdsProtocolException(
                $"{serviceId} response lengthFormatIdentifier 0x{lengthFormatIdentifier:X2} claims {maxBlockWidth} bytes of maxNumberOfBlockLength; the client caps this at 8 (ulong).");
        if (response.Length < 2 + maxBlockWidth)
            throw new UdsProtocolException(
                $"{serviceId} response truncated (need {2 + maxBlockWidth} bytes for maxNumberOfBlockLength, got {response.Length}).");

        ulong maxNumberOfBlockLength = 0;
        for (int i = 0; i < maxBlockWidth; i++)
            maxNumberOfBlockLength = (maxNumberOfBlockLength << 8) | response[2 + i];

        return project(lengthFormatIdentifier, maxNumberOfBlockLength);
    }

    public async Task<byte[]> TransferDataAsync(byte blockSequenceCounter,
        ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetimeCts.Token);
        var linkedToken = linked.Token;

        await _requestLock.WaitAsync(linkedToken).ConfigureAwait(false);
        try
        {
            return await TransferDataCoreAsync(blockSequenceCounter, data, linkedToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    /// <summary>
    /// Assumes <see cref="_requestLock"/> is already held. Sends one 0x36 request and validates
    /// the echoed block-sequence counter.
    /// </summary>
    private async Task<byte[]> TransferDataCoreAsync(byte blockSequenceCounter,
        ReadOnlyMemory<byte> data, CancellationToken linkedToken)
    {
        var request = new byte[2 + data.Length];
        request[0] = (byte)UdsServiceId.TransferData;
        request[1] = blockSequenceCounter;
        if (data.Length > 0) data.Span.CopyTo(request.AsSpan(2));

        var response = await ExecuteCoreAsync(UdsServiceId.TransferData, request,
            linkedToken).ConfigureAwait(false);

        // Positive response: [0]=0x76 [1]=blockSequenceCounter [2..]=transferResponseParameterRecord.
        if (response.Length < 2)
            throw new UdsProtocolException(
                $"TransferData response too short ({response.Length} bytes).");
        if (response[1] != blockSequenceCounter)
            throw new UdsProtocolException(
                $"TransferData response blockSequenceCounter mismatch (sent 0x{blockSequenceCounter:X2}, got 0x{response[1]:X2}).");

        int tail = response.Length - 2;
        var record = new byte[tail];
        if (tail > 0) Buffer.BlockCopy(response, 2, record, 0, tail);
        return record;
    }

    public async Task RequestTransferExitAsync(
        ReadOnlyMemory<byte> transferRequestParameterRecord = default,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetimeCts.Token);
        var linkedToken = linked.Token;

        await _requestLock.WaitAsync(linkedToken).ConfigureAwait(false);
        try
        {
            await RequestTransferExitCoreAsync(transferRequestParameterRecord, linkedToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    /// <summary>
    /// Assumes <see cref="_requestLock"/> is already held. Sends one 0x37 request; the
    /// vendor-specific transferResponseParameterRecord is discarded (SID correlation is done
    /// by <see cref="ExecuteCoreAsync"/>).
    /// </summary>
    private async Task RequestTransferExitCoreAsync(
        ReadOnlyMemory<byte> transferRequestParameterRecord,
        CancellationToken linkedToken)
    {
        var request = new byte[1 + transferRequestParameterRecord.Length];
        request[0] = (byte)UdsServiceId.RequestTransferExit;
        if (transferRequestParameterRecord.Length > 0)
            transferRequestParameterRecord.Span.CopyTo(request.AsSpan(1));

        _ = await ExecuteCoreAsync(UdsServiceId.RequestTransferExit, request,
            linkedToken).ConfigureAwait(false);
    }

    public async Task DownloadAsync(
        byte dataFormatIdentifier,
        byte addressAndLengthFormatIdentifier,
        ReadOnlyMemory<byte> memoryAddress,
        ReadOnlyMemory<byte> memorySize,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetimeCts.Token);
        var linkedToken = linked.Token;

        // Hold the request lock across the entire 0x34 → 0x36…0x36 → 0x37 sequence so
        // TesterPresent keep-alive (or any other UDS call) cannot interleave mid-download and
        // desynchronise the ECU's block-sequence counter (ISO 14229-1 §14.3). Mirror of
        // SecurityAccessAsync: acquire the lock once, then call the *Core helpers that assume
        // the lock is held. The public RequestDownload/TransferData/RequestTransferExit APIs
        // remain unchanged for single-step callers.
        await _requestLock.WaitAsync(linkedToken).ConfigureAwait(false);
        try
        {
            var download = await RequestTransferSetupCoreAsync(
                UdsServiceId.RequestDownload,
                dataFormatIdentifier,
                addressAndLengthFormatIdentifier,
                memoryAddress,
                memorySize,
                (lfid, maxBlock) => new UdsDownloadResponse(lfid, maxBlock),
                linkedToken).ConfigureAwait(false);

            // TransferData request layout: [0]=0x36 [1]=BSC [2..]=payload.
            // The ECU-reported maxNumberOfBlockLength is the TOTAL request size in bytes
            // (including the SID and the BSC byte), so each chunk carries at most
            // maxNumberOfBlockLength - 2 payload bytes.
            ulong maxBlock = download.MaxNumberOfBlockLength;
            if (maxBlock <= 2)
                throw new UdsProtocolException(
                    $"ECU-reported maxNumberOfBlockLength={maxBlock} leaves no room for TransferData payload " +
                    "(need at least 3 to carry SID + BSC + one payload byte).");

            // Cap the chunk size at int.MaxValue so we can slice ReadOnlyMemory<byte>. Real ECUs
            // report block lengths that fit in a few kB; the cap is defensive against absurd LFIs.
            int chunkSize = maxBlock - 2 > int.MaxValue ? int.MaxValue : (int)(maxBlock - 2);

            int offset = 0;
            byte bsc = 0x01; // ISO 14229-1 §14.3.2: first TransferData uses BSC=0x01.
            while (offset < data.Length)
            {
                int remaining = data.Length - offset;
                int take = remaining < chunkSize ? remaining : chunkSize;
                var chunk = data.Slice(offset, take);
                _ = await TransferDataCoreAsync(bsc, chunk, linkedToken).ConfigureAwait(false);
                offset += take;
                unchecked { bsc++; } // Wraps 0xFF → 0x00 → 0x01 … as required by ISO 14229-1 §14.3.2.
            }

            await RequestTransferExitCoreAsync(default, linkedToken).ConfigureAwait(false);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    public async Task<byte[]> UploadAsync(
        byte dataFormatIdentifier,
        byte addressAndLengthFormatIdentifier,
        ReadOnlyMemory<byte> memoryAddress,
        ReadOnlyMemory<byte> memorySize,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetimeCts.Token);
        var linkedToken = linked.Token;

        // Declared byte count to read: big-endian, same wire layout as in RequestDownload/Upload.
        ulong totalBytes = 0;
        for (int i = 0; i < memorySize.Length; i++)
            totalBytes = (totalBytes << 8) | memorySize.Span[i];
        if (totalBytes > int.MaxValue)
            throw new UdsProtocolException(
                $"Declared memorySize {totalBytes} exceeds what a single upload can buffer.");

        // Same lock discipline as DownloadAsync: one continuous 0x35 → 0x36…0x36 → 0x37
        // sequence, so keep-alive traffic cannot desynchronise the ECU's block-sequence counter.
        await _requestLock.WaitAsync(linkedToken).ConfigureAwait(false);
        try
        {
            _ = await RequestTransferSetupCoreAsync(
                UdsServiceId.RequestUpload,
                dataFormatIdentifier,
                addressAndLengthFormatIdentifier,
                memoryAddress,
                memorySize,
                (lfid, maxBlock) => new UdsUploadResponse(lfid, maxBlock),
                linkedToken).ConfigureAwait(false);

            var result = new List<byte>();
            byte bsc = 0x01; // ISO 14229-1 §14.3.2: first TransferData uses BSC=0x01.
            while ((ulong)result.Count < totalBytes)
            {
                var chunk = await TransferDataCoreAsync(bsc, ReadOnlyMemory<byte>.Empty,
                    linkedToken).ConfigureAwait(false);
                if (chunk.Length == 0)
                    throw new UdsProtocolException(
                        "ECU returned an empty TransferData payload before the declared memorySize was reached.");
                result.AddRange(chunk);
                unchecked { bsc++; } // Wraps 0xFF → 0x00 → 0x01 … per ISO 14229-1 §14.3.2.
            }

            await RequestTransferExitCoreAsync(default, linkedToken).ConfigureAwait(false);

            // The final block may carry padding beyond the declared size; trim defensively.
            var bytes = result.ToArray();
            if ((ulong)bytes.Length > totalBytes)
                Array.Resize(ref bytes, (int)totalBytes);
            return bytes;
        }
        finally
        {
            _requestLock.Release();
        }
    }

    // ---------------------------------------------------------------------------------------
    // Shared request/response engine (P2/P2* + NRC 0x78 loop + structured NRC surfacing).
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Sends <paramref name="request"/>, waits for the first response inside P2, then keeps
    /// waiting inside P2* for as long as the ECU replies with NRC 0x78. Validates that the
    /// positive response SID matches (request SID + 0x40) and unpacks structured NRCs.
    /// </summary>
    private async Task<byte[]> ExecuteAsync(UdsServiceId serviceId, byte[] request,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetimeCts.Token);
        var linkedToken = linked.Token;

        await _requestLock.WaitAsync(linkedToken).ConfigureAwait(false);
        try
        {
            return await ExecuteCoreAsync(serviceId, request, linkedToken).ConfigureAwait(false);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    /// <summary>
    /// Request/response engine that assumes <see cref="_requestLock"/> is already held.
    /// Used by <see cref="ExecuteAsync"/> and by multi-step services (SecurityAccess) that must
    /// keep the lock across more than one on-the-wire exchange.
    /// </summary>
    private async Task<byte[]> ExecuteCoreAsync(UdsServiceId serviceId, byte[] request,
        CancellationToken linkedToken)
    {
        // NRC 0x21 (busyRepeatRequest) asks for exactly that: the request is repeated, up to
        // MaxBusyRepeatRequests times, each with a fresh P2 (#57). Anything else the exchange
        // produces -- data, another NRC, a timeout -- passes through.
        for (int repeats = 0; ; repeats++)
        {
            try
            {
                return await ExchangeOnceAsync(serviceId, request, linkedToken).ConfigureAwait(false);
            }
            catch (UdsNegativeResponseException ex)
                when (ex.Code == NrcBusyRepeatRequest && repeats < _options.MaxBusyRepeatRequests)
            {
                if (_options.BusyRepeatRequestDelay > TimeSpan.Zero)
                    await Task.Delay(_options.BusyRepeatRequestDelay, linkedToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<byte[]> ExchangeOnceAsync(UdsServiceId serviceId, byte[] request,
        CancellationToken linkedToken)
    {
        await WaitOutSuppressedResponseWindowAsync(serviceId, linkedToken).ConfigureAwait(false);

        // Drop any late reply left over from a previous aborted/timed-out wait before we put a
        // new request on the wire. SID correlation alone is insufficient when the next request
        // uses the same service (the stale positive response SID would match).
        DiscardStalePdus();

        // The stamp the channel took as the request's last frame went to the bus -- not a reading
        // taken here. P2 starts when the request was transmitted, and this continuation resumes
        // an unbounded time after that: behind the bus TX confirmation, an actor hop and the
        // thread pool. Reading the clock here therefore starts the budget late, and a response
        // that was late against the real P2 measures as punctual and is accepted -- the same
        // defect as the one below, entered from the other end of the interval. On a four-core box
        // under 3x load the gap reached 74.8 ms against an 80 ms P2; #92's CI failures are what a
        // starved runner does with it.
        //
        // A raw monotonic reading rather than a Stopwatch instance, because the budget is
        // compared against the *arrival* stamp the channel takes at enqueue, and both must come
        // from the same source (Stopwatch.GetTimestamp) for the subtraction to mean anything.
        // Read before the request is handed to the channel, as the fallback for a channel that
        // reports no handoff instant: nothing that reached the wire after this reading can be
        // an earlier request's response.
        var requestStarted = Stopwatch.GetTimestamp();
        var stamps = await _channel.SendWithTransmitStampAsync(request, linkedToken)
            .ConfigureAwait(false);
        var transmitStamp = stamps.LastFrameTransmitTimestamp;

        // Zero means the channel reported no transmit instant. Falling back to now is the old
        // behaviour, which is worse but not broken; treating zero as a timestamp would read as
        // infinitely long ago and time out every request.
        var budgetStart = transmitStamp > 0 ? transmitStamp : Stopwatch.GetTimestamp();
        // A response whose first frame arrived before this is an earlier request's (Codex on
        // #143). The bound is the channel's handoff of the request's *last* frame, taken just
        // before the driver call: a peer answers only a complete request, so nothing on the
        // wire before that can be its answer. The transmit stamp cannot serve -- it is "no
        // later than the driver accepted the frame", taken after a synchronous delivery or a
        // completion callback, and a fast peer's answer can be stamped by the demux before it
        // (#146) -- and a reading taken here before entering the channel, or the first frame's
        // handoff, would leave the channel's own transmission as a window a late response to
        // the previous request passes through (Codex on #147).
        var notBefore = stamps.LastFrameHandoffTimestamp > 0
            ? stamps.LastFrameHandoffTimestamp
            : requestStarted;
        var timeout = _options.P2ClientMax;
        var timerKind = UdsTimeoutTimer.P2;
        int pendingCount = 0;

        try
        {
            while (true)
            {
                var received = await ReceiveWithTimeoutAsync(
                    serviceId, timerKind, timeout, budgetStart, notBefore, linkedToken)
                    .ConfigureAwait(false);

                // The budget is enforced here, not by the cancellation that raced it.
                //
                // ReceiveWithTimeoutAsync arms a CancellationTokenSource for the remaining
                // budget and waits on the channel; whichever of the two completes the wait
                // first decides the outcome. That is a race, and a host that delays the
                // deadline callback past the response's arrival lets the response win --
                // whereupon the client returns data for a request it had already given up on,
                // and specifically the stale record that the next request would then have to
                // discard. #92 recorded five CI failures of exactly that shape.
                //
                // Checking the arrival stamp rather than "am I past the deadline now" is the
                // point: the second question is answered by when this code got scheduled, so a
                // punctual response observed late would be rejected -- swapping a rare wrong
                // accept for a frequent wrong reject under precisely the load that causes the
                // bug.
                // Against the response's *first* frame: ISO 14229-2 ends P2 (and P2*) with the
                // first frame of the response, and leaves the rest of a multi-frame transfer to
                // the transport's timers (#28). Measured against the last frame, every response
                // that spends longer on the wire than P2 -- a 4 KB record at STmin 5 ms takes
                // seconds -- would time out although the server answered in time.
                // And a response whose first frame predates the request is not this request's
                // at all -- it began before the request was handed to the channel, so it answers
                // an earlier one (Codex on #143). ElapsedSince clamps a negative interval to
                // zero, which would read as "punctual"; it is a stray, and the wait goes on.
                if (received.FirstFrameArrivalTimestamp < notBefore)
                    continue;
                var arrival = ElapsedSince(budgetStart, received.FirstFrameArrivalTimestamp);
                if (arrival > timeout)
                    throw new UdsTimeoutException(serviceId, timerKind, timeout);

                byte[] response = received.Pdu;

                if (response.Length == 0)
                    throw new UdsProtocolException(
                        $"Empty UDS response received for service 0x{(byte)serviceId:X2}.");

                // Negative response layout: [0]=0x7F [1]=requestSid [2]=NRC.
                if (response[0] == NegativeResponseSid)
                {
                    if (response.Length < 3)
                        throw new UdsProtocolException(
                            $"Malformed negative response (length={response.Length}).");
                    var echoed = (UdsServiceId)response[1];
                    if (echoed != serviceId)
                    {
                        // Stray NRC for a different SID — treat as background noise and keep
                        // waiting inside the same budget.
                        continue;
                    }

                    byte nrc = response[2];
                    if (nrc == NrcResponsePending)
                    {
                        pendingCount++;
                        if (pendingCount > _options.MaxResponsePendingCount)
                            throw new UdsProtocolException(
                                $"ECU sent {pendingCount} consecutive NRC 0x78 responses, exceeding MaxResponsePendingCount={_options.MaxResponsePendingCount}.");

                        // Restart the wait budget on P2* (SRS FR-UDS-009, ISO 14229-1 §7.3.3)
                        // from when the pending response *arrived*, not from now. Restarting at
                        // "now" would hand the ECU whatever scheduling delay this client just
                        // suffered on top of its P2* budget: a 0x78 that arrived at 50 ms but is
                        // processed at 200 ms would make a final response that arrived at 150 ms
                        // -- 100 ms after the 0x78, against an 80 ms P2* -- measure as zero and
                        // be accepted. Same scheduling independence as the check above, and for
                        // the same reason.
                        budgetStart = received.FirstFrameArrivalTimestamp;
                        notBefore = received.FirstFrameArrivalTimestamp;
                        timeout = _options.P2StarClientMax;
                        timerKind = UdsTimeoutTimer.P2Star;
                        continue;
                    }

                    throw new UdsNegativeResponseException(serviceId, nrc);
                }

                byte expectedPositiveSid = (byte)((byte)serviceId + PositiveResponseOffset);
                if (response[0] != expectedPositiveSid)
                {
                    // Stray positive response for a different request; discard and keep waiting.
                    continue;
                }

                return response;
            }
        }
        catch
        {
            // Best-effort: if a PDU is already sitting in the inbox when we abort (e.g. cancel
            // raced with arrival), drop it under the lock so it cannot poison the next caller.
            DiscardStalePdus();
            throw;
        }
    }

    private static bool IsAllZero(byte[] data, int offset, int count)
    {
        for (int i = offset; i < offset + count; i++)
        {
            if (data[i] != 0) return false;
        }
        return true;
    }

    private void DiscardStalePdus()
    {
        try
        {
            _channel.DiscardPendingPdus();
        }
        catch (ObjectDisposedException)
        {
            // Channel is going away; nothing left to drain.
        }
    }

    /// <summary>
    /// Waits on <see cref="IIsoTpChannel.ReceiveAsync"/> with the currently applicable
    /// (P2 or P2*) timeout, taking already-elapsed time into account so a single wait budget
    /// isn't re-set to full when the loop iterates for a stray frame. A multi-frame response
    /// whose First Frame arrived inside the budget is waited for beyond it: the budget ended
    /// with that frame (ISO 14229-2), and the transport's N_Cr bounds the rest (#28).
    /// </summary>
    private async Task<IsoTpReceivedPdu> ReceiveWithTimeoutAsync(UdsServiceId serviceId,
        UdsTimeoutTimer timerKind, TimeSpan budget, long budgetStart, long notBefore,
        CancellationToken linkedToken)
    {
        // How long to wait and whether what turns up was in time are two questions, and only the
        // first one is answered here. The second belongs to the caller's arrival check, because
        // the answer must not depend on when this client got scheduled -- so neither exit below
        // may discard a PDU unread on the strength of a clock reading taken now.
        var elapsedInBudget = ElapsedSince(budgetStart);
        var remaining = budget - elapsedInBudget;
        if (remaining <= TimeSpan.Zero)
        {
            // No budget left as measured from now -- but the budget started when the previous
            // PDU *arrived*, and a client descheduled past the deadline can find the answer
            // already queued. Awaiting with an expired token would not find it: an
            // already-cancelled token wins against a queued item. Take what is there and let the
            // caller judge its stamp; only an empty inbox means nothing arrived in time
            // (Bugbot on #112).
            return await TakeQueuedOrInProgressAsync(serviceId, timerKind, budget, budgetStart,
                notBefore, linkedToken).ConfigureAwait(false);
        }

        using var timeoutCts = new CancellationTokenSource(remaining);
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(
            linkedToken, timeoutCts.Token);

        try
        {
            return await _channel.ReceiveWithArrivalAsync(combined.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested
                                                 && !linkedToken.IsCancellationRequested)
        {
            // The deadline callback won the race -- which says nothing about whether a punctual
            // PDU was enqueued just before it fired. Same rule as the zero-remaining exit: look
            // before declaring a timeout.
            return await TakeQueuedOrInProgressAsync(serviceId, timerKind, budget, budgetStart,
                notBefore, linkedToken).ConfigureAwait(false);
        }
    }

    // How long a wait for a reception in progress runs before it re-checks that the reception
    // is still there. The channel publishes a First Frame when it is read off the bus and
    // withdraws it if the actor then refuses the frame -- with nothing put in the inbox -- so
    // a wait on it must not be unbounded. The re-check costs nothing when the PDU arrives:
    // completion or abort puts an item in the inbox and the wait returns at once.
    private static readonly TimeSpan InProgressRecheck = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// The budget is spent. What can still be returned is a PDU already in the inbox, or the
    /// response being waited for if its First Frame arrived inside the budget: P2 ended there
    /// (ISO 14229-2), and the remainder of the transfer is the transport's, bounded by N_Cr.
    /// Anything else is a timeout.
    /// </summary>
    private async Task<IsoTpReceivedPdu> TakeQueuedOrInProgressAsync(UdsServiceId serviceId,
        UdsTimeoutTimer timerKind, TimeSpan budget, long budgetStart, long notBefore,
        CancellationToken linkedToken)
    {
        while (true)
        {
            // The in-progress check goes first: the channel withdraws the record only after the
            // completed PDU (or the abort's error item) is in the inbox, so a reception seen in
            // progress here is found by the wait below, and one not seen is either absent or
            // already queued for the peek.
            if (!ResponseBeganInTime(serviceId, budget, budgetStart, notBefore))
            {
                if (_channel.TryReceiveWithArrival(out var queued))
                    return queued;

                throw new UdsTimeoutException(serviceId, timerKind, budget);
            }

            using var recheck = new CancellationTokenSource(InProgressRecheck);
            using var combined = CancellationTokenSource.CreateLinkedTokenSource(
                linkedToken, recheck.Token);
            try
            {
                return await _channel.ReceiveWithArrivalAsync(combined.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (recheck.IsCancellationRequested
                                                     && !linkedToken.IsCancellationRequested)
            {
                // Still nothing in the inbox: loop, and let the check above decide whether the
                // reception is still in progress.
            }
        }
    }

    // A multi-frame response is being reassembled, its First Frame arrived inside the budget,
    // and it is *this* request's response -- its first byte is the positive response SID. Any
    // other transfer (a late answer to an earlier request, an unsolicited response for another
    // service) does not extend the budget: the peer is busy with it, so this request's answer
    // cannot start in time anyway, and waiting it out would only delay the timeout by the length
    // of a transfer that is then discarded as stray (Codex on #143). A negative response is a
    // Single Frame and never gets here.
    private bool ResponseBeganInTime(UdsServiceId serviceId, TimeSpan budget, long budgetStart,
        long notBefore)
    {
        byte positiveSid = (byte)((byte)serviceId + PositiveResponseOffset);
        // Several can be pending when the channel's actor is behind the bus; any one that is
        // this request's response and began in time keeps the wait going.
        foreach (var reception in _channel.GetReceptionsInProgress())
        {
            // Inside the budget on both ends: one that began before the request was handed
            // over answers an earlier request, however long it takes to finish (Codex on #143).
            if (reception.FirstFrameArrivalTimestamp >= notBefore
                && ElapsedSince(budgetStart, reception.FirstFrameArrivalTimestamp) <= budget
                && reception.FirstFrameData.Length > 0
                && reception.FirstFrameData.Span[0] == positiveSid)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Elapsed time between two <see cref="Stopwatch.GetTimestamp"/> readings, defaulting the
    /// second to now. Kept in one place so the pre-check (how much budget is left) and the
    /// post-check (was this PDU inside it) can never drift onto different clocks.
    /// </summary>
    private static TimeSpan ElapsedSince(long startTimestamp, long? endTimestamp = null)
    {
        var end = endTimestamp ?? Stopwatch.GetTimestamp();
        var ticks = end - startTimestamp;
        if (ticks <= 0) return TimeSpan.Zero;
        return TimeSpan.FromSeconds((double)ticks / Stopwatch.Frequency);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(UdsClient));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        Interlocked.Exchange(ref _keepAlive, null)?.Dispose();

        // Cancel in-flight ExecuteAsync / SecurityAccessAsync / suppress-TesterPresent first,
        // then wait for the request lock so their finally blocks can Release before we dispose
        // the semaphore (Bugbot 3596444327 / 3596586770). Disposing while a waiter still holds
        // the lock races WaitAsync/Release.
        try { _lifetimeCts.Cancel(); } catch { /* already disposed */ }

        // If the holder does not let go in time -- an operation ignoring the cancellation --
        // the semaphore stays undisposed: its Release on the holder's thread would otherwise
        // throw ObjectDisposedException into an operation that was merely slow (#57). A
        // SemaphoreSlim without a wait handle holds nothing that needs disposing.
        bool lockAcquired = false;
        try
        {
            lockAcquired = _requestLock.Wait(DisposeLockTimeout);
            if (lockAcquired) _requestLock.Release();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down on another path.
        }

        _lifetimeCts.Dispose();
        if (lockAcquired) _requestLock.Dispose();

        if (_ownsChannel)
        {
            try { _channel.Dispose(); } catch { /* Dispose should not throw */ }
        }
    }

    // ---------------------------------------------------------------------------------------
    // TesterPresent keep-alive helper.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Background timer that periodically calls <see cref="TesterPresentAsync"/> until disposed.
    /// Uses <see cref="Task.Delay(System.TimeSpan, System.Threading.CancellationToken)"/> rather
    /// than a <see cref="System.Threading.Timer"/> to keep the state machine linear and share
    /// the client's request lock naturally.
    /// </summary>
    private sealed class TesterPresentKeepAlive : IDisposable
    {
        private readonly UdsClientImpl _owner;
        private readonly TimeSpan _period;
        private readonly bool _suppress;
        private readonly CancellationTokenSource _cts = new();
        private Task? _loop;
        private int _disposed;

        public TesterPresentKeepAlive(UdsClientImpl owner, TimeSpan period, bool suppress)
        {
            _owner = owner;
            _period = period;
            _suppress = suppress;
        }

        public void Start()
        {
            _loop = Task.Run(() => LoopAsync(_cts.Token));
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(_period, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { return; }

                    if (ct.IsCancellationRequested) return;

                    try
                    {
                        await _owner.TesterPresentAsync(_suppress, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { return; }
                    catch (ObjectDisposedException) { return; }
                    catch
                    {
                        // Swallow individual failures so a transient hiccup doesn't tear down
                        // the whole keep-alive; the next tick tries again. Callers who need to
                        // observe keep-alive failures can subscribe to the underlying channel's
                        // BackgroundExceptionOccurred instead.
                    }
                }
            }
            finally
            {
                Interlocked.CompareExchange(ref _owner._keepAlive, null, this);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _cts.Cancel(); } catch { /* ignored */ }
            try { _loop?.GetAwaiter().GetResult(); } catch { /* ignored */ }
            _cts.Dispose();
        }
    }
}
