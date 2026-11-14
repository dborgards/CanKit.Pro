using System;

namespace CanKit.Pro.IsoTp;

/// <summary>
/// Configuration for an <see cref="IsoTpFunctionalClient"/>.
/// </summary>
/// <remarks>
/// Functional (1:N) addressing uses Normal ISO-TP addressing (no address-extension byte) and
/// restricts outbound messages to Single Frames only (ISO 15765-2:2024 §8.3.2.4 / ISO 14229-1 §7.7.1).
/// Options that only apply to multi-frame sessions (N_Bs, N_Cr, WftMax, LocalBlockSize,
/// LocalStMin) are therefore absent.
/// </remarks>
public sealed class IsoTpFunctionalOptions
{
    /// <summary>Default N_As TX-confirm timeout (1 second, matching <see cref="IsoTpChannelOptions.DefaultTimeout"/>).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// <c>true</c> when the functional TX CAN identifier and the response-range CAN identifiers
    /// are 29-bit extended; <c>false</c> for 11-bit standard identifiers. Defaults to
    /// <c>false</c>.
    /// </summary>
    public bool IsExtendedCanId { get; init; }

    /// <summary>
    /// <c>true</c> to build the outbound Single Frame as a CAN-FD frame (up to 62 user bytes in
    /// the escape form); <c>false</c> for classic CAN (up to 7 user bytes). Must match the
    /// underlying bus mode. Defaults to <c>false</c>.
    /// </summary>
    public bool UseCanFd { get; init; }

    /// <summary>
    /// TX_DL — the largest CAN FD frame, in bytes, the outbound Single Frame may use (ISO
    /// 15765-2:2024 §9.5.1, Table 7). Must be one of 8, 12, 16, 20, 24, 32, 48 or 64; any other
    /// value fails client creation with <see cref="ArgumentOutOfRangeException"/>. Defaults to
    /// 64.
    /// </summary>
    /// <remarks>
    /// Applies only with <see cref="UseCanFd"/>; it bounds the Single Frame payload to TX_DL - 2
    /// bytes (7 at TX_DL 8). A classic CAN client always uses 8, and validates the value all the
    /// same, as <see cref="IsoTpChannelOptions.TxDl"/> does.
    /// </remarks>
    public int TxDl { get; init; } = IsoTpFrameCodec.CanFdMaxData;

    /// <summary>
    /// <c>true</c> to pad the outbound Single Frame to the next valid CAN/CAN-FD DLC step.
    /// On CAN-FD a Single Frame longer than 8 bytes is rounded up to its next valid data length
    /// (12, 16, 20, 24, 32, 48, 64) whatever this says, since no other length exists.
    /// Defaults to <c>true</c>.
    /// </summary>
    public bool UsePadding { get; init; } = true;

    /// <summary>
    /// Byte used to pad the outbound Single Frame when <see cref="UsePadding"/> is
    /// <c>true</c>, and for the rounding bytes of a CAN-FD frame longer than 8 bytes when it is
    /// not (see <see cref="UsePadding"/>). Defaults to <see cref="IsoTpFrameCodec.DefaultPaddingByte"/> (<c>0xCC</c>).
    /// </summary>
    public byte PaddingByte { get; init; } = IsoTpFrameCodec.DefaultPaddingByte;

    /// <summary>
    /// N_As — maximum time the sender waits for the CAN driver to confirm the Single Frame was
    /// accepted. Defaults to <see cref="DefaultTimeout"/> (1 second).
    /// </summary>
    public TimeSpan NAs { get; init; } = DefaultTimeout;

    /// <summary>
    /// <c>true</c> to ignore received Single Frame responses whose CAN frame length does not fit
    /// their form (ISO 15765-2:2024 §9.6.2.2), with the same checks as
    /// <see cref="IsoTpChannelOptions.StrictReceive"/>. Defaults to <c>false</c>, which takes
    /// such frames as before.
    /// </summary>
    /// <remarks>
    /// Responses use Normal addressing, so there is no address extension. With it, a response
    /// Single Frame is not collected when:
    /// <list type="bullet">
    ///   <item><description>it is 8 bytes or shorter and <see cref="UsePadding"/> is on, but the
    ///   frame is not 8 bytes long (§11.3.2.1);</description></item>
    ///   <item><description>it is 8 bytes or shorter and <see cref="UsePadding"/> is off, but
    ///   the frame is not exactly the PCI and the SF_DL data bytes long (§11.3.2.2, Table 13);
    ///   </description></item>
    ///   <item><description>it is longer than 8 bytes but uses the short PCI form.</description></item>
    /// </list>
    /// <see cref="UsePadding"/> thus describes the link in both directions, as it does for the
    /// channel. An escape-form Single Frame whose SF_DL does not need its CAN_DL (Table 14) is
    /// ignored whatever this says. Applies to <see cref="IsoTpFunctionalClient.Listen"/> too.
    /// </remarks>
    public bool StrictReceive { get; init; }
}
