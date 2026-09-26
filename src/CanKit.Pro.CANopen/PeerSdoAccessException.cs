using System;

namespace CanKit.Pro.CANopen;

/// <summary>
/// An SDO client upload or download was refused locally, before any frame was sent, because
/// the (index, sub-index) is not one this node may transfer to that server.
/// </summary>
/// <remarks>
/// <para>
/// A peer EDS or DCF bound with <see cref="ICanOpenNode.BindPeerDeviceDescription"/> is the list
/// of objects the client may touch on that node. A pair the file does not declare is refused,
/// including <c>1000h</c>, <c>1001h</c> and <c>1018h</c> when the file leaves them out.
/// </para>
/// <para>
/// With no description bound, only the CiA 301 mandatory base objects are transferred
/// (<see cref="IsAllowedWithoutPeerDescription"/>): <c>1000h:00</c> device type, <c>1001h:00</c>
/// error register, and the whole identity object <c>1018h:00</c>–<c>04</c> (highest sub-index,
/// vendor-id, product code, revision, serial number). Anything past <c>1018h:04</c>, and any
/// optional object such as <c>1003h</c>, stays blocked until a peer file is bound.
/// </para>
/// </remarks>
public sealed class PeerSdoAccessException : InvalidOperationException
{
    /// <summary>Constructs the exception for one refused transfer.</summary>
    public PeerSdoAccessException(byte serverNodeId, ushort index, byte subindex,
        bool peerDescriptionLoaded, string message)
        : base(message ?? throw new ArgumentNullException(nameof(message)))
    {
        ServerNodeId = serverNodeId;
        Index = index;
        Subindex = subindex;
        PeerDescriptionLoaded = peerDescriptionLoaded;
    }

    /// <summary>The server node-id the transfer addressed.</summary>
    public byte ServerNodeId { get; }

    /// <summary>Object index.</summary>
    public ushort Index { get; }

    /// <summary>Sub-index.</summary>
    public byte Subindex { get; }

    /// <summary>
    /// <see langword="true"/> when a peer description was bound for <see cref="ServerNodeId"/> and
    /// the pair is absent from it; <see langword="false"/> when nothing was bound and the pair is
    /// not one of <see cref="IsAllowedWithoutPeerDescription"/>.
    /// </summary>
    public bool PeerDescriptionLoaded { get; }

    /// <summary>
    /// Whether an SDO to this pair may proceed when no peer EDS or DCF is bound for the server.
    /// </summary>
    /// <remarks>
    /// <c>1000h</c> and <c>1001h</c> at sub-index 0, and <c>1018h</c> at sub-indices <c>00h</c>
    /// through <c>04h</c>. <c>1018h:05</c> and above, <c>1003h</c>, and every other index are
    /// not included.
    /// </remarks>
    public static bool IsAllowedWithoutPeerDescription(ushort index, byte subindex) => index switch
    {
        0x1000 or 0x1001 => subindex == 0x00,
        0x1018 => subindex <= 0x04,
        _ => false,
    };

    internal static PeerSdoAccessException NoDescription(byte serverNodeId, ushort index, byte subindex)
        => new(serverNodeId, index, subindex, peerDescriptionLoaded: false,
            $"SDO 0x{index:X4}:{subindex:X2} on node 0x{serverNodeId:X2} needs a peer EDS or DCF " +
            "bound with BindPeerDeviceDescription. Without one, only 1000h:00 (device type), " +
            "1001h:00 (error register) and 1018h:00-04 (identity) may be transferred.");

    internal static PeerSdoAccessException NotInDescription(byte serverNodeId, ushort index, byte subindex)
        => new(serverNodeId, index, subindex, peerDescriptionLoaded: true,
            $"SDO 0x{index:X4}:{subindex:X2} is not declared by the EDS or DCF bound for node " +
            $"0x{serverNodeId:X2}. A loaded peer description allows only the pairs it contains; " +
            "1000h, 1001h and 1018h are not exempt from that list.");
}
