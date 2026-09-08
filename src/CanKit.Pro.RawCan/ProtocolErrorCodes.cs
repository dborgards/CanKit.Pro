using CanKit.Core.Exceptions;

namespace CanKit.Pro.RawCan;

/// <summary>
/// The <see cref="CanKitErrorCode"/> values CanKit.Pro's protocol layers report (NFR-006,
/// arc42 ADR-12): every L3/L4 failure is a <see cref="CanKitException"/>, and its
/// <see cref="CanKitException.ErrorCode"/> says which *kind* of protocol failure it was, not
/// merely that a transport operation failed.
///
/// <para>
/// <b>Why these are declared here rather than used from the enum.</b> CanKit reserves the 6000
/// range for transport and protocol errors and defines <c>TransportOperationFailed = 6001</c>.
/// The four values below continue that range, but they are not (yet) members of the published
/// <see cref="CanKitErrorCode"/> — they were added inside the CanKit.Pro.legacy fork, and
/// CanKit.Pro no longer forks CanKit. Declaring them as constants of the enum type keeps the
/// numeric contract identical to what the fork produced and to what upstream would produce:
/// should CanKit adopt these codes, <c>ErrorCode == CanKitErrorCode.ProtocolTimeout</c> starts
/// being true for already-compiled callers, with nothing here to change but this file's removal.
/// </para>
///
/// <para>
/// The trade-off is that <c>ErrorCode.ToString()</c> renders the number rather than a name until
/// that happens. Callers who want a name have the exception type itself, which is always more
/// specific than the code (<c>IsoTpTimeoutException</c> even names which ISO 15765-2 timer
/// expired). Upstreaming these four members to CanKit is tracked as follow-up work; see
/// docs/upstream-candidates.md.
/// </para>
///
/// <para>
/// <b>Why in CanKit.Pro.RawCan.</b> These codes are shared by ISO-TP, J1939-TP, CANopen, J1939
/// and UDS, and RawCan is the one package all five already depend on — so putting them here adds
/// no dependency edge. If CanKit.Pro grows more cross-cutting types, they belong together in a
/// small foundation package instead.
/// </para>
/// </summary>
public static class ProtocolErrorCodes
{
    /// <summary>
    /// A protocol timer expired on a still-active exchange: ISO-TP N_As/N_Bs/N_Cr, a J1939-TP
    /// session timeout, or a UDS P2/P2* window.
    /// </summary>
    public const CanKitErrorCode ProtocolTimeout = (CanKitErrorCode)6002;

    /// <summary>
    /// The peer aborted the exchange: an ISO-TP Overflow or WFTmax breach, a J1939-TP Abort, or a
    /// CANopen SDO abort.
    /// </summary>
    public const CanKitErrorCode ProtocolPeerAbort = (CanKitErrorCode)6003;

    /// <summary>
    /// The server answered with a negative response (UDS service 0x7F).
    /// </summary>
    public const CanKitErrorCode ProtocolNegativeResponse = (CanKitErrorCode)6004;

    /// <summary>
    /// A J1939 address claim did not succeed: the claim was lost and no arbitrary address was
    /// available, or the node has no address to transmit from.
    /// </summary>
    public const CanKitErrorCode AddressClaimFailed = (CanKitErrorCode)6005;
}
