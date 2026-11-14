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
/// Raised when a reception is aborted because a Consecutive Frame carries a sequence number
/// other than the expected one (SRS FR-TP-002; ISO 15765-2:2024 §9.6.4.4, §9.8.2 Table 23:
/// CTL_WRONG_SN). Surfaced through <see cref="IIsoTpChannel.BackgroundExceptionOccurred"/> and to
/// a pending <see cref="IIsoTpChannel.ReceiveAsync"/>.
/// </summary>
/// <remarks>
/// Its <see cref="CanKitException.ErrorCode"/> is the one <see cref="IsoTpException"/> carried
/// for this case before the type existed, so code that checks the code is unaffected.
/// </remarks>
public sealed class IsoTpWrongSequenceNumberException : IsoTpException
{
    /// <summary>The sequence number the reception expected next (0..15).</summary>
    public byte ExpectedSequenceNumber { get; }

    /// <summary>The sequence number the Consecutive Frame carried (0..15).</summary>
    public byte ReceivedSequenceNumber { get; }

    /// <summary>Creates a wrong-sequence-number exception for the two values.</summary>
    public IsoTpWrongSequenceNumberException(byte expectedSequenceNumber, byte receivedSequenceNumber)
        : base($"ISO-TP CF sequence-number mismatch: expected {expectedSequenceNumber}, got {receivedSequenceNumber}.")
    {
        ExpectedSequenceNumber = expectedSequenceNumber;
        ReceivedSequenceNumber = receivedSequenceNumber;
    }
}

/// <summary>
/// Raised when a reception in progress is aborted because a Single Frame or a new First Frame
/// arrived in its place (ISO 15765-2:2024 §9.8.3, Table 24: CTL_UNEXP_PDU). The new frame is
/// processed as the start of a new PDU. Surfaced through
/// <see cref="IIsoTpChannel.BackgroundExceptionOccurred"/> and to a pending
/// <see cref="IIsoTpChannel.ReceiveAsync"/>.
/// </summary>
/// <remarks>
/// Its <see cref="CanKitException.ErrorCode"/> is the one <see cref="IsoTpException"/> carried
/// for this case before the type existed, so code that checks the code is unaffected.
/// </remarks>
public sealed class IsoTpUnexpectedPduException : IsoTpException
{
    /// <summary>The kind of frame that arrived (<see cref="PciType.SingleFrame"/> or <see cref="PciType.FirstFrame"/>).</summary>
    public PciType ReceivedFrameType { get; }

    /// <summary>Creates an unexpected-PDU exception naming the frame that arrived.</summary>
    public IsoTpUnexpectedPduException(PciType receivedFrameType)
        : base(receivedFrameType == PciType.SingleFrame
            ? "ISO-TP Single Frame aborted in-flight multi-frame reception (unexpected N_PCI)."
            : "ISO-TP First Frame aborted in-flight multi-frame reception.")
    {
        ReceivedFrameType = receivedFrameType;
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
