using System;
using System.ComponentModel;

namespace CanKit.Pro.IsoTp;

/// <summary>
/// Configuration for an <see cref="IIsoTpChannel"/>.
/// </summary>
/// <remarks>
/// All values are set at channel creation time and treated as immutable for the channel's
/// lifetime. The timers keep the ISO 15765-2:2016 names (N_As, N_Ar, N_Bs, N_Cr); ISO 15765-2:2024
/// calls them t_TL_As, t_TL_Ar, t_TL_Bs and t_TL_Cr (§9.8.1, Table 22). Their defaults are the
/// standard's timeout values (1000 ms each).
/// <para>
/// Clause numbers in this package refer to ISO 15765-2:2024. The package README lists how the
/// channel reads the standard where it leaves a choice, and where it is narrower (#353).
/// </para>
/// </remarks>
public sealed class IsoTpChannelOptions
{
    /// <summary>Default value for <see cref="NAs"/>, <see cref="NAr"/>, <see cref="NBs"/>, <see cref="NCr"/>.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// <c>true</c> to build ISO-TP frames as CAN-FD (up to 64-byte per frame); <c>false</c> for
    /// classic CAN (8-byte per frame). Must match the underlying <see cref="Abstractions.API.Can.ICanBus"/>
    /// mode; if the bus is classic-CAN, <c>true</c> here will produce frames the driver rejects.
    /// The sender then fills frames up to <see cref="TxDl"/> bytes (64 by default). Receiving
    /// adapts to whatever TX_DL the peer uses (§9.5.2). The bit-rate-switch (BRS) flag is not set
    /// by the channel; the data bit rate is a bus/adapter setting, outside ISO 15765-2.
    /// On a CAN FD channel a reception started with CAN FD frames is not continued or ended by a
    /// classic frame, and the other way round (§9.5.2).
    /// </summary>
    public bool UseCanFd { get; init; }

    /// <summary>
    /// <c>true</c> to pad each frame to the next valid CAN/CAN-FD DLC step with
    /// <see cref="PaddingByte"/>; <c>false</c> to send the exact minimum payload. Many ISO 15765-2
    /// stacks expect padding on classic CAN. On CAN-FD the data length of a frame can only be 0 to
    /// 8, 12, 16, 20, 24, 32, 48 or 64 bytes, so a frame that falls between two steps is always
    /// padded up to the next one (ISO 15765-2:2024 §11.3.2.3 requires that rounding), whatever this property
    /// says; it only decides whether frames of 8 bytes or fewer are padded as well.
    /// </summary>
    public bool UsePadding { get; init; } = true;

    /// <summary>
    /// Byte used to pad SF/CF/FC frames when <see cref="UsePadding"/> is <c>true</c>, and to round
    /// a CAN-FD frame longer than 8 bytes up to its next valid data length when it is <c>false</c>
    /// (see <see cref="UsePadding"/>). Defaults to
    /// <see cref="IsoTpFrameCodec.DefaultPaddingByte"/> (<c>0xCC</c>).
    /// </summary>
    public byte PaddingByte { get; init; } = IsoTpFrameCodec.DefaultPaddingByte;

    /// <summary>
    /// Block size (BS) advertised on Flow-Control frames sent by this channel while receiving
    /// multi-frame PDUs. <c>0</c> means "no additional FCs needed, send all remaining CFs".
    /// </summary>
    public byte LocalBlockSize { get; init; }

    /// <summary>
    /// STmin advertised on Flow-Control frames sent by this channel while receiving multi-frame
    /// PDUs. Encoded per ISO 15765-2:2024 §9.6.5.4 (0..127 ms in 1-ms steps, 100..900 µs in
    /// 100-µs steps) by <see cref="IsoTpFrameCodec.EncodeStMin"/>, which rounds a value between
    /// two codes up -- 190 µs is advertised as 200 µs; see there.
    /// Defaults to <see cref="TimeSpan.Zero"/> (no minimum separation requested).
    /// Must be non-negative — a negative value fails channel open via
    /// <see cref="IsoTpFrameCodec.EncodeStMin"/>.
    /// </summary>
    public TimeSpan LocalStMin { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// N_As — maximum time between the sender handing an SF/FF/CF to the driver and the driver
    /// confirming it was sent (ISO 15765-2:2024 §9.8.1, t_TL_As; FR-TP-010 acceptance
    /// criterion). Modelled here as the TX-confirmation timeout the channel uses when calling
    /// <see cref="RawCan.ICanBusService.SendConfirmedAsync"/>. It bounds the wait for the echo on an
    /// echo-capable bus and the wait for the driver to accept the frame on any other, so a stalled
    /// frame send always ends within it instead of blocking later sends on the channel.
    /// The Flow Control this channel sends while receiving has its own timer, <see cref="NAr"/>.
    /// Defaults to <see cref="DefaultTimeout"/>.
    /// </summary>
    public TimeSpan NAs { get; init; } = DefaultTimeout;

    /// <summary>
    /// N_Ar — maximum time between the receiver handing a Flow Control to the driver and the
    /// driver confirming it was sent (ISO 15765-2:2024 §9.8.1, t_TL_Ar). If it expires, or the
    /// driver definitely fails the Flow Control, the reception is aborted and a pending
    /// <see cref="IIsoTpChannel.ReceiveAsync"/> faults with <see cref="IsoTpTimeoutException"/>
    /// (<see cref="IsoTpTimer.NAr"/>) or <see cref="IsoTpException"/> (§9.8.2, Table 23,
    /// CTL_TIMEOUT_A). A Consecutive Frame arriving first shows the peer has the Flow Control and
    /// stops the timer. <c>FC(OVFLW)</c> starts no reception and is sent without it.
    /// Defaults to <see cref="DefaultTimeout"/>.
    /// </summary>
    public TimeSpan NAr { get; init; } = DefaultTimeout;

    /// <summary>
    /// N_Bs — maximum time between the sender's FF/last-CF-of-a-block and the peer's Flow Control
    /// (ISO 15765-2:2024 §9.8.1, t_TL_Bs). Restarted by every <c>FC(WAIT)</c>. Expiring aborts the
    /// outgoing PDU with a timeout error (FR-TP-010).
    /// Defaults to <see cref="DefaultTimeout"/>.
    /// </summary>
    public TimeSpan NBs { get; init; } = DefaultTimeout;

    /// <summary>
    /// N_Cr — maximum time between two Consecutive Frames on the receive side (ISO 15765-2:2024
    /// §9.8.1, t_TL_Cr). After a Flow Control it starts when the driver confirms that Flow Control
    /// (until then <see cref="NAr"/> runs), or at the next Consecutive Frame if that arrives first.
    /// Expiring aborts the incoming reassembly and faults a pending
    /// <see cref="IIsoTpChannel.ReceiveAsync"/> with <see cref="IsoTpTimeoutException"/>
    /// (FR-TP-010). Defaults to <see cref="DefaultTimeout"/>.
    /// </summary>
    public TimeSpan NCr { get; init; } = DefaultTimeout;

    /// <summary>
    /// WFTmax — maximum number of consecutive <see cref="FlowStatus.Wait"/> FCs the sender is
    /// willing to accept before aborting (FR-TP-011). Defaults to 10.
    /// </summary>
    /// <remarks>
    /// ISO 15765-2:2024 §9.7 makes WFTmax a receiver parameter: the limit on the Waits a
    /// <em>receiver</em> sends. This channel never sends <c>FC(WAIT)</c>, and applies the value as
    /// a sender-side guard instead, so a peer cannot hold a transfer open forever with Waits. A
    /// conforming peer that needs more Waits is cut off by it; raise the value for such a peer.
    /// <para>
    /// The default of 10 is this library's choice: the standard leaves WFTmax to the receiver and
    /// gives no default, and a sender has no way to learn the peer's value. Ten Waits, each
    /// restarting <see cref="NBs"/>, bound the wait at about ten times N_Bs (10 s with the
    /// defaults) before the send ends with <see cref="IsoTpWaitFrameLimitExceededException"/>.
    /// </para>
    /// </remarks>
    public int WftMax { get; init; } = 10;

    /// <summary>
    /// TX_DL — the largest CAN FD frame, in bytes, the sender builds (ISO 15765-2:2024 §9.5.1,
    /// Table 7). Must be one of 8, 12, 16, 20, 24, 32, 48 or 64; any other value fails channel
    /// creation with <see cref="ArgumentOutOfRangeException"/>. Defaults to 64.
    /// </summary>
    /// <remarks>
    /// Applies only with <see cref="UseCanFd"/>: Single, First and Consecutive Frames then carry
    /// at most TX_DL bytes, so a CAN FD network whose nodes take only shorter frames can be
    /// served. TX_DL 8 sends CAN FD frames of classic size (Single Frames in the short form only).
    /// A classic CAN channel always uses 8. Receiving is not affected: the channel adapts to the
    /// TX_DL the peer uses (§9.5.2).
    /// </remarks>
    public int TxDl { get; init; } = IsoTpFrameCodec.CanFdMaxData;

    /// <summary>
    /// <c>true</c> to ignore received Single Frames whose CAN frame length does not follow the
    /// padding rule the channel is configured for (ISO 15765-2:2024 §9.6.2.2, §11.3.2.1). Defaults to
    /// <c>false</c>, which takes such frames as before.
    /// </summary>
    /// <remarks>
    /// Checked only with <see cref="UsePadding"/>: a padded link uses 8-byte frames for anything
    /// up to 8 bytes, so a Single Frame received in a frame of fewer than 8 bytes is dropped
    /// without a reply, as an invalid frame. Frames longer than 8 bytes are already held to the CAN FD data length
    /// steps by the bus and the codec.
    /// </remarks>
    public bool StrictReceive { get; init; }

    /// <summary>
    /// Bounded capacity of the internal receive buffer that holds fully reassembled PDUs waiting
    /// for the consumer. When full, the oldest PDU is dropped so the RX loop never stalls.
    /// Defaults to 64.
    /// </summary>
    /// <remarks>
    /// The drop is silent: no <see cref="IIsoTpChannel.BackgroundExceptionOccurred"/> is raised
    /// and nothing counts it, and a reassembly-abort fault queued for a receiver is dropped the
    /// same way. A channel nobody reads from therefore loses PDUs without a trace once this many
    /// have accumulated; a consumer that cannot keep up sizes the buffer or drains it.
    /// </remarks>
    public int ReceiveBufferCapacity { get; init; } = 64;

    /// <summary>
    /// Largest PDU this channel reassembles. A First Frame announcing more is answered with
    /// <c>FC(OVFLW)</c> and nothing is allocated for it (ISO 15765-2:2024 §9.6.3.2, Table 19),
    /// so six bytes from any bus participant cannot make the process reserve the up to 4 GB a
    /// CAN-FD escape First Frame may announce (#26). Defaults to
    /// 65 535 bytes; classic CAN transfers are bounded by the codec at 4095 regardless.
    /// Outbound <see cref="IIsoTpChannel.SendAsync"/> is not bounded by this.
    /// </summary>
    public int MaxReceivePduLength { get; init; } = 0xFFFF;

    /// <summary>
    /// Convenience clone that returns a new instance with the provided overrides. Useful for
    /// tests that want to tweak one field of a shared default template.
    /// </summary>
    public IsoTpChannelOptions With(
        bool? useCanFd = null,
        bool? usePadding = null,
        byte? paddingByte = null,
        byte? localBlockSize = null,
        TimeSpan? localStMin = null,
        TimeSpan? nAs = null,
        TimeSpan? nBs = null,
        TimeSpan? nCr = null,
        int? wftMax = null,
        int? receiveBufferCapacity = null,
        int? maxReceivePduLength = null,
        TimeSpan? nAr = null,
        int? txDl = null,
        bool? strictReceive = null)
        => new()
        {
            UseCanFd = useCanFd ?? UseCanFd,
            UsePadding = usePadding ?? UsePadding,
            PaddingByte = paddingByte ?? PaddingByte,
            LocalBlockSize = localBlockSize ?? LocalBlockSize,
            LocalStMin = localStMin ?? LocalStMin,
            NAs = nAs ?? NAs,
            NAr = nAr ?? NAr,
            NBs = nBs ?? NBs,
            NCr = nCr ?? NCr,
            WftMax = wftMax ?? WftMax,
            ReceiveBufferCapacity = receiveBufferCapacity ?? ReceiveBufferCapacity,
            MaxReceivePduLength = maxReceivePduLength ?? MaxReceivePduLength,
            TxDl = txDl ?? TxDl,
            StrictReceive = strictReceive ?? StrictReceive,
        };

    /// <summary>
    /// The <see cref="With(bool?, bool?, byte?, byte?, TimeSpan?, TimeSpan?, TimeSpan?, TimeSpan?, int?, int?, int?, TimeSpan?, int?, bool?)"/>
    /// overload from before <see cref="TxDl"/> and <see cref="StrictReceive"/>, kept so code
    /// compiled against it still binds.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public IsoTpChannelOptions With(
        bool? useCanFd,
        bool? usePadding,
        byte? paddingByte,
        byte? localBlockSize,
        TimeSpan? localStMin,
        TimeSpan? nAs,
        TimeSpan? nBs,
        TimeSpan? nCr,
        int? wftMax,
        int? receiveBufferCapacity,
        int? maxReceivePduLength,
        TimeSpan? nAr)
        => With(useCanFd, usePadding, paddingByte, localBlockSize, localStMin, nAs, nBs, nCr,
            wftMax, receiveBufferCapacity, maxReceivePduLength, nAr, txDl: null, strictReceive: null);

    /// <summary>
    /// The <see cref="With(bool?, bool?, byte?, byte?, TimeSpan?, TimeSpan?, TimeSpan?, TimeSpan?, int?, int?, int?, TimeSpan?, int?, bool?)"/>
    /// overload from before <see cref="NAr"/>, kept so code compiled against it still binds.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public IsoTpChannelOptions With(
        bool? useCanFd,
        bool? usePadding,
        byte? paddingByte,
        byte? localBlockSize,
        TimeSpan? localStMin,
        TimeSpan? nAs,
        TimeSpan? nBs,
        TimeSpan? nCr,
        int? wftMax,
        int? receiveBufferCapacity,
        int? maxReceivePduLength)
        => With(useCanFd, usePadding, paddingByte, localBlockSize, localStMin, nAs, nBs, nCr,
            wftMax, receiveBufferCapacity, maxReceivePduLength, nAr: null, txDl: null,
            strictReceive: null);
}
