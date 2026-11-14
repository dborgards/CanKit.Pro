using System;

namespace CanKit.Pro.Uds;

/// <summary>
/// Configuration for an <see cref="IUdsClient"/>. All values are captured at construction and
/// treated as immutable for the client's lifetime.
/// </summary>
/// <remarks>
/// <para>Timing terminology follows ISO 14229-2 (session layer), where P2/P2*/S3 are defined:</para>
/// <list type="bullet">
///   <item><description><see cref="P2ClientMax"/> is the default response window a tester waits
///   after transmitting a request before treating the ECU as unresponsive. The specification's
///   default is 50 ms; MVP defaults to 1 s to survive slow simulated ECUs and Windows-CI jitter,
///   real-world callers should override.</description></item>
///   <item><description><see cref="P2StarClientMax"/> is the extended window used after the ECU
///   replied with NRC 0x78 (requestCorrectlyReceived-ResponsePending). Restarted for every
///   further 0x78 (SRS FR-UDS-009). Defaults to 5 s.</description></item>
///   <item><description><see cref="MaxResponsePendingCount"/> bounds how many 0x78 NRCs the
///   client is willing to accept before it gives up. Defaults to 100 (large enough that any
///   healthy ECU will finish first, small enough that a stuck ECU eventually fails loudly).
///   </description></item>
/// </list>
/// </remarks>
public sealed class UdsClientOptions
{
    /// <summary>Default <see cref="P2ClientMax"/> (1 second).</summary>
    public static readonly TimeSpan DefaultP2 = TimeSpan.FromSeconds(1);

    /// <summary>Default <see cref="P2StarClientMax"/> (5 seconds).</summary>
    public static readonly TimeSpan DefaultP2Star = TimeSpan.FromSeconds(5);

    /// <summary>Default <see cref="TesterPresentPeriod"/> (2 seconds; below the ISO 14229 S3
    /// default of 5 seconds).</summary>
    public static readonly TimeSpan DefaultTesterPresentPeriod = TimeSpan.FromSeconds(2);

    /// <summary>
    /// What the client adds to the P2Server_max and P2*Server_max a successful
    /// DiagnosticSessionControl declares to obtain its own P2 and P2* for that session
    /// (50 ms; #340). ISO 14229-2 has P2Client_max = P2Server_max + ΔP2 and
    /// P2*Client_max = P2*Server_max + ΔP2_response, ΔP2 being the network's latency for the
    /// request and the response, and leaves its value to the network. 50 ms covers a CAN frame's
    /// time on the wire many times over and leaves room for a USB adapter's round trip and the
    /// host's scheduling. It is added to both, which for P2* is more than the standard asks for,
    /// and the result only ever lengthens <see cref="P2ClientMax"/> / <see cref="P2StarClientMax"/>,
    /// never shortens them.
    /// </summary>
    public static readonly TimeSpan SessionTimingMargin = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// P2_Client_max — maximum time the client waits between sending a request and observing the
    /// first response frame from the ECU (positive, negative, or 0x78 responsePending). Expiring
    /// raises <see cref="UdsTimeoutException"/> with <see cref="UdsTimeoutTimer.P2"/>
    /// (SRS FR-UDS-008). A successful DiagnosticSessionControl whose response declares a longer
    /// P2Server_max lengthens it for that session (see <see cref="SessionTimingMargin"/>); it
    /// never shortens it.
    /// </summary>
    public TimeSpan P2ClientMax { get; init; } = DefaultP2;

    /// <summary>
    /// P2*_Client_max — maximum time the client waits after receiving NRC 0x78 before treating
    /// the ECU as unresponsive. Restarted for every further 0x78 (SRS FR-UDS-009); once the
    /// budget is exhausted <see cref="UdsTimeoutException"/> with <see cref="UdsTimeoutTimer.P2Star"/>
    /// is raised. A successful DiagnosticSessionControl whose response declares a longer
    /// P2*Server_max lengthens it for that session (see <see cref="SessionTimingMargin"/>); it
    /// never shortens it.
    /// </summary>
    public TimeSpan P2StarClientMax { get; init; } = DefaultP2Star;

    /// <summary>
    /// Upper bound on consecutive NRC 0x78 responses; when the ECU sends more, the client fails
    /// with a <see cref="UdsProtocolException"/> instead of waiting indefinitely. Defaults to
    /// <c>100</c>.
    /// </summary>
    /// <remarks>
    /// It bounds a response window as well: an NRC 0x78 heard inside the window a suppressed
    /// send -- or, with <see cref="QuarantineLateResponses"/>, an abandoned request -- left for
    /// its service moves the window out by P2*, and at most this many do; the next request for
    /// the service is then held up for a bounded time by a peer that answers 0x78 for ever
    /// (#354).
    /// </remarks>
    public int MaxResponsePendingCount { get; init; } = DefaultMaxResponsePendingCount;

    // Also the bound UdsFunctionalClient puts on the 0x78s that move one of its windows out.
    internal const int DefaultMaxResponsePendingCount = 100;

    /// <summary>
    /// Default period for <see cref="IUdsClient.StartTesterPresentKeepAlive"/>. Defaults to 2 s
    /// (below the ISO 14229 default S3 timeout of 5 s so a single missed frame does not drop
    /// the session).
    /// </summary>
    public TimeSpan TesterPresentPeriod { get; init; } = DefaultTesterPresentPeriod;

    /// <summary>
    /// When <c>true</c>, TesterPresent frames sent by the keep-alive helper use the ISO 14229-1
    /// suppressPosRspMsgIndicationBit (§8.2.2, Table 10; sub-function <c>0x80</c>) so the ECU
    /// stays silent. Defaults to <c>true</c> to keep the bus quiet. While
    /// <see cref="IUdsClient.SecurityAccessAsync"/> runs its <c>computeKey</c> callback the
    /// keep-alive always sets the bit.
    /// </summary>
    /// <remarks>
    /// The keep-alive goes out physically addressed, on the client's own channel. ISO 14229-1:2026
    /// §7.7.6 describes the keep-alive as a functionally addressed TesterPresent handled by the
    /// server's bypass logic; a physical one keeps the session of the one server this client
    /// talks to alive as well, and needs no functional identifier. Send a functional one with
    /// <see cref="UdsFunctionalClient"/> where every server on the bus has to be kept awake.
    /// </remarks>
    public bool KeepAliveSuppressPositiveResponse { get; init; } = true;

    /// <summary>
    /// How many times a request is repeated after NRC 0x21 (busyRepeatRequest) before the
    /// negative response is surfaced. ISO 14229-1:2026 §A.1 (Table A.1, NRC 21h) has the
    /// client repeat the request, so the default repeats; zero surfaces the first 0x21 as an
    /// <see cref="UdsNegativeResponseException"/> (#57), which is an opt-out from that rule.
    /// Default 3.
    /// </summary>
    public int MaxBusyRepeatRequests { get; init; } = 3;

    /// <summary>
    /// The pause before a request is repeated after NRC 0x21. Default zero: the request is
    /// repeated at once, with a fresh P2 budget. Set it from the ECU's or OEM's specification.
    /// </summary>
    /// <remarks>
    /// ISO 14229-1:2026 §A.1 (Table A.1, NRC 21h) has the repeat delayed by a time given in the
    /// implementation documents -- the vehicle manufacturer's or ECU's specification, or the
    /// data-link part such as ISO 14229-3 for CAN -- and gives no value itself. The client has no
    /// such document, so it defaults to zero; a non-zero default would not be a value from the
    /// standard either, and would change when every caller's repeat goes out (#354).
    /// </remarks>
    public TimeSpan BusyRepeatRequestDelay { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// When <c>true</c>, a request that times out (<see cref="UdsTimeoutException"/>) or is
    /// cancelled after it may have reached the bus quarantines its service for the P2* budget in
    /// force, counted from the abort: the next request for that service is not sent until the
    /// window has passed, and whatever arrives meanwhile is discarded -- among it the aborted
    /// request's late positive answer, which for a repeated request (a polled DID) echoes the
    /// same bytes as the next request's own and could not be told from it, and its NRC 0x78,
    /// which would otherwise restart P2* for the next request. A 0x78 for the service inside
    /// the window moves the window's end out by P2* from the 0x78's arrival: the server has
    /// said its answer is still coming. Requests for other services are not held up (#340).
    /// </summary>
    /// <remarks>
    /// Defaults to <c>false</c>. The cost is a delay of up to P2* -- 5 s by default -- before
    /// the next request for a service whose previous request was abandoned. A late answer
    /// that arrives after the window is over is taken for the next request's, as it is
    /// without the quarantine.
    /// </remarks>
    public bool QuarantineLateResponses { get; init; }

    /// <summary>
    /// How many times <see cref="IUdsClient.DownloadAsync"/> and <see cref="IUdsClient.UploadAsync"/>
    /// repeat a TransferData block whose answer did not arrive, before the failure surfaces.
    /// Default 0: no block is repeated (#426).
    /// </summary>
    /// <remarks>
    /// <para>
    /// ISO 14229-1:2026 §14.4 (Table 451) lets a client repeat a TransferData request with the
    /// same blockSequenceCounter when its positive response did not arrive, whether or not the
    /// server processed the request: a server recognises the repeat by the counter, answers a
    /// download block without writing it again and sends an upload block again. A block is
    /// repeated, byte for byte and with the same counter, after a P2 or P2* timeout
    /// (<see cref="UdsTimeoutException"/>) and after an ISO-TP transport error on its request or
    /// response (an <see cref="IsoTp.IsoTpException"/> other than
    /// <see cref="IsoTp.IsoTpOverflowException"/>, which says the request is too large for the
    /// server and would only be refused again).
    /// </para>
    /// <para>
    /// A negative response is never repeated this way: NRC 0x73 (wrongBlockSequenceCounter),
    /// 0x24, 0x71, 0x72 and the others surface at once, and NRC 0x21 keeps its own repeat
    /// (<see cref="MaxBusyRepeatRequests"/>), which does not count against this one. Nor are
    /// cancellation, disposal, or an exception that is not an ISO-TP one, such as the bus
    /// layer's own when a driver call fails. The budget is per block: every
    /// block may be repeated this many times, each attempt with a fresh P2. When the budget is
    /// spent, the last failure surfaces and no RequestTransferExit is sent, as without repeats.
    /// </para>
    /// <para>
    /// With <see cref="QuarantineLateResponses"/>, the repeat waits out the abandoned attempt's
    /// window first. Without it, a late positive answer to the abandoned attempt can be taken
    /// for the repeat's: it echoes the same counter, and the server treats both requests as the
    /// same block, so either answer is the block's. An upload appends each block's data once.
    /// <see cref="IUdsClient.TransferDataAsync"/> called on its own is not repeated. Must not be
    /// negative.
    /// </para>
    /// </remarks>
    public int TransferDataRetryCount { get; init; }

    /// <summary>
    /// Convenience clone that returns a new instance with the provided overrides. Useful for
    /// tests that only want to tweak one field of a shared default template. Settings it takes
    /// no parameter for -- <see cref="QuarantineLateResponses"/> and
    /// <see cref="TransferDataRetryCount"/> -- are carried over unchanged.
    /// </summary>
    public UdsClientOptions With(
        TimeSpan? p2ClientMax = null,
        TimeSpan? p2StarClientMax = null,
        int? maxResponsePendingCount = null,
        TimeSpan? testerPresentPeriod = null,
        bool? keepAliveSuppressPositiveResponse = null,
        int? maxBusyRepeatRequests = null,
        TimeSpan? busyRepeatRequestDelay = null)
        => new()
        {
            P2ClientMax = p2ClientMax ?? P2ClientMax,
            P2StarClientMax = p2StarClientMax ?? P2StarClientMax,
            MaxResponsePendingCount = maxResponsePendingCount ?? MaxResponsePendingCount,
            TesterPresentPeriod = testerPresentPeriod ?? TesterPresentPeriod,
            KeepAliveSuppressPositiveResponse = keepAliveSuppressPositiveResponse ?? KeepAliveSuppressPositiveResponse,
            MaxBusyRepeatRequests = maxBusyRepeatRequests ?? MaxBusyRepeatRequests,
            BusyRepeatRequestDelay = busyRepeatRequestDelay ?? BusyRepeatRequestDelay,
            // Not a parameter: one more optional parameter would change the method's signature,
            // which is a binary break since 1.3.0 (package validation).
            QuarantineLateResponses = QuarantineLateResponses,
            TransferDataRetryCount = TransferDataRetryCount,
        };
}
