using System;
using CanKit.Core.Exceptions;
using CanKit.Pro.RawCan;

namespace CanKit.Pro.CANopen.Sdo;

/// <summary>
/// Thrown when an SDO transfer terminates with an Abort frame (CiA 301 §7.2.4.3.17). Carries
/// the failing (<see cref="Index"/>, <see cref="Subindex"/>) and the raw 32-bit
/// <see cref="AbortCode"/> — matched against <see cref="SdoAbortCode"/> when possible.
/// Derives from <see cref="CanKitException"/> so L2/L3/L4 failures can be caught uniformly
/// across the library (NFR-006 error architecture, arc42 ADR-12).
/// </summary>
/// <remarks>
/// Both the client and the server produce this exception. On the client side, the exception
/// wraps a peer-sent Abort frame (or a locally-triggered timeout/abort). On the server side,
/// the abort is emitted onto the bus and the exception is used only for internal control flow.
/// </remarks>
public sealed class SdoAbortException : CanKitException
{
    /// <summary>Object index that the failing SDO transfer targeted.</summary>
    public ushort Index { get; }

    /// <summary>Object subindex that the failing SDO transfer targeted.</summary>
    public byte Subindex { get; }

    /// <summary>Raw 32-bit abort code (little-endian in the wire frame).</summary>
    public uint AbortCode { get; }

    /// <summary>
    /// Which side ended the transfer: <see cref="SdoAbortOrigin.Peer"/> when the abort frame
    /// came from the peer, <see cref="SdoAbortOrigin.Local"/> when this node sent it — on its
    /// own timeout, on a protocol violation it detected, or because it refused the transfer up
    /// front. <see cref="AbortCode"/> is the code the originating side chose either way.
    /// </summary>
    public SdoAbortOrigin Origin { get; }

    /// <summary>Constructs a new abort exception with an explicit message, reporting an abort
    /// sent by the peer (<see cref="SdoAbortOrigin.Peer"/>).</summary>
    public SdoAbortException(ushort index, byte subindex, uint abortCode, string message)
        : this(index, subindex, abortCode, SdoAbortOrigin.Peer, message)
    {
    }

    /// <summary>Constructs a new abort exception with an explicit message and origin.</summary>
    public SdoAbortException(ushort index, byte subindex, uint abortCode, SdoAbortOrigin origin,
        string message)
        : base(ErrorCodeFor(origin, abortCode), message)
    {
        Index = index;
        Subindex = subindex;
        AbortCode = abortCode;
        Origin = origin;
    }

    /// <summary>Constructs a new abort exception with a message derived from
    /// <paramref name="abortCode"/>, reporting an abort sent by the peer
    /// (<see cref="SdoAbortOrigin.Peer"/>).</summary>
    public SdoAbortException(ushort index, byte subindex, SdoAbortCode abortCode)
        : this(index, subindex, abortCode, SdoAbortOrigin.Peer)
    {
    }

    /// <summary>Constructs a new abort exception with a message derived from
    /// <paramref name="abortCode"/> and <paramref name="origin"/>.</summary>
    public SdoAbortException(ushort index, byte subindex, SdoAbortCode abortCode, SdoAbortOrigin origin)
        : this(index, subindex, (uint)abortCode, origin,
            (origin == SdoAbortOrigin.Local ? "This node aborted" : "Peer aborted")
            + $" SDO transfer for 0x{index:X4}:{subindex:X2} with code 0x{(uint)abortCode:X8} ({abortCode}).")
    {
    }

    /// <summary>
    /// The <see cref="CanKitException.ErrorCode"/> for an abort. The code names the mechanism
    /// that ended the exchange, and <see cref="Origin"/> names the side; the one case where the
    /// mechanism itself differs is this node's own request timer expiring, which is
    /// <see cref="ProtocolErrorCodes.ProtocolTimeout"/> — "a protocol timer expired on a
    /// still-active exchange" — exactly as an ISO-TP N_Cr expiry or a J1939-TP T1 expiry is.
    /// Every other abort, sent or received, is an SDO abort transfer ending the exchange and
    /// stays <see cref="ProtocolErrorCodes.ProtocolPeerAbort"/>, the code that names it: nothing
    /// in <see cref="ProtocolErrorCodes"/> describes "this node detected a protocol violation",
    /// and <c>J1939TpAbortException</c> draws the same line for a Connection Abort it issues. A
    /// peer's 0504 0000h is a peer abort whose reason happens to be the peer's timer; the reason
    /// is in <see cref="AbortCode"/>.
    /// </summary>
    private static CanKitErrorCode ErrorCodeFor(SdoAbortOrigin origin, uint abortCode)
        => origin == SdoAbortOrigin.Local && abortCode == (uint)SdoAbortCode.SdoProtocolTimedOut
            ? ProtocolErrorCodes.ProtocolTimeout
            : ProtocolErrorCodes.ProtocolPeerAbort;
}
