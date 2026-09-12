namespace CanKit.Pro.CANopen.Sdo;

/// <summary>
/// Selects which CiA 301 SDO transport the <c>SdoUploadAsync</c> /
/// <c>SdoDownloadAsync</c> APIs on <see cref="ICanOpenNode"/> should use for a single transfer.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Auto"/> is the recommended default. The choice between the expedited codec
/// (CiA 301 §7.2.4.3.3) and the segmented codec (CiA 301 §7.2.4.3.5..14) is <b>not</b> a caller
/// decision — it follows from the payload length, exactly as CiA 301 prescribes:
/// </para>
/// <list type="bullet">
///   <item><description><b>Download.</b> Payloads of 1..4 bytes use the expedited codec,
///   payloads from 5 bytes up to (but excluding)
///   <see cref="CanOpenNodeOptions.SdoBlockThresholdBytes"/> use the segmented codec, and
///   payloads at or above that threshold switch to block transfer
///   (CiA 301 §7.2.4.3.15).</description></item>
///   <item><description><b>Upload.</b> The payload length is unknown until the server replies,
///   so the server dictates the codec: it answers the initiate with either an expedited or a
///   segmented response and the client follows either transparently. <see cref="Auto"/>
///   therefore cannot apply the block heuristic on upload — pass <see cref="Block"/> explicitly
///   for a block upload.</description></item>
/// </list>
/// <para>
/// <see cref="Block"/> is the only transport a caller can force, because it is the only one the
/// client actually decides: it is negotiated in the initiate frame instead of being derived from
/// a payload length that is either already known (download) or not yet known (upload).
/// </para>
/// </remarks>
public enum SdoTransferMode
{
    /// <summary>
    /// Select the transport from the payload length (download) or from the server's initiate
    /// response (upload), using <see cref="CanOpenNodeOptions.SdoBlockThresholdBytes"/> as the
    /// download block-transfer threshold.
    /// </summary>
    Auto = 0,

    /// <summary>Force block transfer (CiA 301 §7.2.4.3.15).</summary>
    Block = 1,
}
