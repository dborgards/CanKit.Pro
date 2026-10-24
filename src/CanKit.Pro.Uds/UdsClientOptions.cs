using System;

namespace CanKit.Pro.Uds;

/// <summary>
/// Configuration for an <see cref="IUdsClient"/>. All values are captured at construction and
/// treated as immutable for the client's lifetime.
/// </summary>
/// <remarks>
/// <para>Timing terminology follows ISO 14229-1 §7.3:</para>
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
    /// §7.5 "suppressPositiveResponse" bit (sub-function <c>0x80</c>) so the ECU stays silent.
    /// Defaults to <c>true</c> to keep the bus quiet.
    /// </summary>
    public bool KeepAliveSuppressPositiveResponse { get; init; } = true;

    /// <summary>
    /// How many times a request is repeated after NRC 0x21 (busyRepeatRequest) before the
    /// negative response is surfaced. The NRC exists to ask for a repeat (ISO 14229-1 §A.1),
    /// so the default repeats; zero surfaces the first 0x21 as an
    /// <see cref="UdsNegativeResponseException"/> (#57). Default 3.
    /// </summary>
    public int MaxBusyRepeatRequests { get; init; } = 3;

    /// <summary>
    /// The pause before a request is repeated after NRC 0x21. Default zero: the request is
    /// repeated at once, with a fresh P2 budget. Set it where the server's documentation asks
    /// for a delay before the repeat; the client does not derive one (#354).
    /// </summary>
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
    /// Convenience clone that returns a new instance with the provided overrides. Useful for
    /// tests that only want to tweak one field of a shared default template. Settings it takes
    /// no parameter for -- <see cref="QuarantineLateResponses"/> -- are carried over unchanged.
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
        };
}
