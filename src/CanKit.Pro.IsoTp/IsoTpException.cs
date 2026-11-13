using System;
using CanKit.Core.Exceptions;
using CanKit.Pro.RawCan;

namespace CanKit.Pro.IsoTp;

/// <summary>
/// Base type for ISO-TP protocol errors surfaced by <see cref="IIsoTpChannel"/>. Derives from
/// <see cref="CanKitException"/> so L2/L3/L4 failures can be caught uniformly across the
/// library (NFR-006 error architecture, arc42 ADR-12).
/// </summary>
public class IsoTpException : CanKitException
{
    /// <summary>Creates a new <see cref="IsoTpException"/>.</summary>
    public IsoTpException(string message)
        : base(CanKitErrorCode.TransportOperationFailed, message) { }

    /// <summary>Creates a new <see cref="IsoTpException"/> with an inner exception.</summary>
    public IsoTpException(string message, Exception innerException)
        : base(CanKitErrorCode.TransportOperationFailed, message, innerException: innerException) { }

    /// <summary>Creates a new <see cref="IsoTpException"/> with a specific library error code.</summary>
    protected IsoTpException(CanKitErrorCode errorCode, string message) : base(errorCode, message) { }
}

/// <summary>
/// Raised when an ISO-TP timeout (N_As, N_Ar, N_Bs or N_Cr) expires on a still-active PDU
/// (SRS FR-TP-010; ISO 15765-2:2024 §9.8.2, Table 23: CTL_TIMEOUT_A, CTL_TIMEOUT_Bs,
/// CTL_TIMEOUT_Cr).
/// </summary>
public sealed class IsoTpTimeoutException : IsoTpException
{
    /// <summary>Which ISO 15765-2 timer expired.</summary>
    public IsoTpTimer Timer { get; }

    /// <summary>Creates a timeout exception naming which timer expired.</summary>
    public IsoTpTimeoutException(IsoTpTimer timer, string message)
        : base(ProtocolErrorCodes.ProtocolTimeout, message)
    {
        Timer = timer;
    }
}

/// <summary>
/// Raised when the peer sends <see cref="FlowStatus.Overflow"/> in response to a First Frame
/// (SRS FR-TP-012).
/// </summary>
public sealed class IsoTpOverflowException : IsoTpException
{
    /// <summary>Creates an overflow exception.</summary>
    public IsoTpOverflowException(string message) : base(ProtocolErrorCodes.ProtocolPeerAbort, message) { }
}

/// <summary>
/// Raised when the peer answers with a Flow Control whose FlowStatus is reserved (3..F) while the
/// sender waits for one: the transmission is aborted at once (ISO 15765-2:2024 §9.6.5.2,
/// CTL_INVALID_FS).
/// </summary>
public sealed class IsoTpInvalidFlowStatusException : IsoTpException
{
    /// <summary>The raw FlowStatus nibble the peer sent.</summary>
    public byte FlowStatus { get; }

    /// <summary>Creates an invalid-FlowStatus exception for the raw value received.</summary>
    public IsoTpInvalidFlowStatusException(byte flowStatus)
        : base(ProtocolErrorCodes.ProtocolPeerAbort,
            $"Peer sent a Flow Control with reserved FlowStatus 0x{flowStatus:X1}; transmission aborted.")
    {
        FlowStatus = flowStatus;
    }
}

/// <summary>
/// Raised when the sender receives more than <see cref="IsoTpChannelOptions.WftMax"/>
/// consecutive <see cref="FlowStatus.Wait"/> Flow-Control frames (SRS FR-TP-011).
/// </summary>
public sealed class IsoTpWaitFrameLimitExceededException : IsoTpException
{
    /// <summary>Number of Wait frames received before the limit was hit.</summary>
    public int WaitFramesReceived { get; }

    /// <summary>Configured WFTmax.</summary>
    public int Limit { get; }

    /// <summary>Creates a WFTmax exception.</summary>
    public IsoTpWaitFrameLimitExceededException(int received, int limit)
        : base(ProtocolErrorCodes.ProtocolPeerAbort,
            $"Peer sent {received} consecutive Flow-Control Wait frames, exceeding WFTmax={limit}.")
    {
        WaitFramesReceived = received;
        Limit = limit;
    }
}

/// <summary>
/// Raised when the driver rejected a frame the channel tried to transmit
/// (<see cref="RawCan.TxConfirmFailureReason.Rejected"/>).
/// </summary>
public sealed class IsoTpSendRejectedException : IsoTpException
{
    /// <summary>Creates a send-rejected exception.</summary>
    public IsoTpSendRejectedException(string message) : base(message) { }
}

/// <summary>
/// ISO 15765-2 timers observed by <see cref="IsoTpTimeoutException"/>. The names are the 2016
/// ones; ISO 15765-2:2024 calls them t_TL_As, t_TL_Ar, t_TL_Bs and t_TL_Cr (§9.8.1, Table 22).
/// </summary>
public enum IsoTpTimer
{
    /// <summary>N_As — sender's TX-confirmation timer (SF/FF/CF -> driver acknowledgment).</summary>
    NAs,

    /// <summary>N_Bs — sender's Flow-Control-wait timer (last CF-of-block -> next FC).</summary>
    NBs,

    /// <summary>N_Cr — receiver's Consecutive-Frame timer (FC confirmation or CF -> next CF).</summary>
    NCr,

    /// <summary>
    /// N_Ar — receiver's TX-confirmation timer for the Flow Control it sends (FC -> driver
    /// acknowledgment). Expiry aborts the reception (CTL_TIMEOUT_A).
    /// </summary>
    NAr,
}
