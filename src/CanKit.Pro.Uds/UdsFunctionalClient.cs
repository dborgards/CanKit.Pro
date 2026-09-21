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

    private readonly IsoTpFunctionalClient _client;
    private readonly bool _ownsClient;
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
    /// answer that arrives within <paramref name="window"/>. A request with
    /// suppressPosRspMsgIndication set is sent and not collected for: an empty list comes back
    /// as soon as the frame is confirmed.
    /// </summary>
    public async Task<IReadOnlyList<UdsFunctionalResponse>> SendRawAsync(ReadOnlyMemory<byte> request,
        TimeSpan window, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (request.Length == 0)
            throw new ArgumentException("Request must contain at least a SID byte.", nameof(request));

        if (request.Length >= 2 && HasSubFunction(request.Span[0])
            && (request.Span[1] & SuppressPositiveResponseBit) != 0)
        {
            await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return Array.Empty<UdsFunctionalResponse>();
        }

        var raw = await _client.SendAndCollectAsync(request, window, cancellationToken)
            .ConfigureAwait(false);
        var responses = new UdsFunctionalResponse[raw.Count];
        for (int i = 0; i < raw.Count; i++)
            responses[i] = new UdsFunctionalResponse(raw[i].SourceCanId, raw[i].Data);
        return responses;
    }

    /// <summary>
    /// TesterPresent to everyone (<c>3E 80</c>): the keep-alive that reaches every ECU with one
    /// frame. With the positive response suppressed, the default, nothing is collected.
    /// </summary>
    public async Task<IReadOnlyList<UdsFunctionalResponse>> TesterPresentAsync(
        bool suppressPositiveResponse = true, TimeSpan? window = null,
        CancellationToken cancellationToken = default)
    {
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
        if (_ownsClient) _client.Dispose();
    }
}
