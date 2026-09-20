namespace CanKit.Pro.CANopen.Sdo;

/// <summary>
/// Which side ended the transfer that an <see cref="SdoAbortException"/> reports. The abort
/// code alone cannot say: the peer sends 0504 0000h ("SDO protocol timed out", CiA 301 Table 22)
/// when <em>its</em> timer expired, and this node sends the same code to the peer when its own
/// request timer did. A caller retrying on "we gave up" and giving up on "the peer refused" needs
/// the distinction, and so does anyone reading a log.
/// </summary>
public enum SdoAbortOrigin
{
    /// <summary>
    /// The peer sent the SDO abort transfer (CiA 301 §7.2.4.3.17);
    /// <see cref="SdoAbortException.AbortCode"/> is the code it chose.
    /// </summary>
    Peer = 0,

    /// <summary>
    /// This node ended the transfer — its request timer expired, it detected a protocol
    /// violation, or it refused the transfer up front — and sent the abort to the peer;
    /// <see cref="SdoAbortException.AbortCode"/> is the code it chose.
    /// </summary>
    Local = 1,
}
