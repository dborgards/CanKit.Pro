using System;
using System.Threading;
using System.Threading.Tasks;

namespace CanKit.Pro.CANopen.Lss;

/// <summary>
/// LSS master (CiA 305). Exactly one master may be active in a CANopen network (§6.1); this
/// type does not police other processes. It sends on <see cref="CanOpenCobId.LssMasterMessage"/>
/// and receives on <see cref="CanOpenCobId.LssSlaveMessage"/>. Commands run one at a time.
/// </summary>
/// <remarks>
/// <para>
/// The master is responsible for having exactly one slave in LSS configuration before a service
/// whose response carries a value (configure, store, inquire). Switch state global with
/// <see cref="LssState.Configuration"/> puts every slave there; use it only when the network has
/// a single LSS slave (§8.2.7).
/// </para>
/// <para>
/// A missing response of a confirmed service is <see cref="LssTimeoutException"/>. A missing
/// response of switch state selective, identify, or the Fastscan reset step is a normal result:
/// no slave matched.
/// </para>
/// </remarks>
public interface ILssMaster : IDisposable, IAsyncDisposable
{
    /// <summary>Options this master was opened with.</summary>
    LssMasterOptions Options { get; }

    /// <summary>
    /// Switches every LSS slave to <paramref name="state"/> (§6.3.2, §7.4.1). The service is
    /// unconfirmed: the call completes when the frame has been handed to the bus.
    /// </summary>
    Task SwitchStateGlobalAsync(LssState state, CancellationToken cancellationToken = default);

    /// <summary>
    /// Switches the slave with <paramref name="address"/> to LSS configuration (§6.3.3, §7.4.2).
    /// Returns <see langword="false"/> when no slave confirms within
    /// <see cref="LssMasterOptions.ResponseTimeout"/>.
    /// </summary>
    Task<bool> SwitchStateSelectiveAsync(LssAddress address, CancellationToken cancellationToken = default);

    /// <summary>
    /// Configures the pending node-id of the single slave in LSS configuration (§6.4.2, §7.5.1).
    /// <paramref name="nodeId"/> is sent as given, including values the protocol reserves, so the
    /// slave's error code can be observed. <see cref="CanOpenCobId.UnconfiguredNodeId"/> is a
    /// legal pending value.
    /// </summary>
    Task<LssServiceResult> ConfigureNodeIdAsync(byte nodeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Configures the pending bit timing of the single slave in LSS configuration (§6.4.3,
    /// §7.5.2). Table selector 0 is the CiA 301 table (<see cref="LssBitTiming"/>); selectors
    /// <see cref="LssBitTiming.ManufacturerTableFirst"/>..<see cref="LssBitTiming.ManufacturerTableLast"/>
    /// address a table the slave defines.
    /// </summary>
    Task<LssServiceResult> ConfigureBitTimingAsync(byte tableSelector, byte tableIndex,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Tells every slave in LSS configuration to copy the pending bit rate to the active one
    /// after <paramref name="switchDelayMilliseconds"/>, and to stay silent for a second period
    /// of the same length (§6.4.4, §7.5.3). Both periods are measured from the activation
    /// transmission; delayed confirmations do not restart them. This call waits out both periods. When
    /// <paramref name="bitsPerSecond"/> and <see cref="LssMasterOptions.BitRateSwitch"/> are both
    /// set, the switch runs after the first period, before slaves may transmit again.
    /// When <paramref name="returnToWaiting"/> is <see langword="true"/>, switch state global
    /// (waiting) is sent while both ends still share a bitrate. A positive delay sends it
    /// before either delay when it can be confirmed as transmitted before the first deadline.
    /// If that window was missed, the host switches rate and sends waiting after the second
    /// period; without a host switch the call fails after both periods have elapsed. A zero
    /// delay has already moved the slave, so the frame is sent
    /// after <see cref="LssMasterOptions.BitRateSwitch"/>, on the new rate. Pass
    /// <paramref name="bitsPerSecond"/> and set that callback for a zero delay; without the
    /// switch the master is still on the old rate and the slave will not hear the frame.
    /// </summary>
    /// <exception cref="ArgumentException">A zero delay with returnToWaiting requires both a bit rate and a switch callback.</exception>
    /// <exception cref="CanOpenTransportException">The old-rate waiting window was missed and no host bit-rate switch is available.</exception>
    Task ActivateBitTimingAsync(ushort switchDelayMilliseconds, int? bitsPerSecond = null,
        bool returnToWaiting = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the single slave in LSS configuration to copy pending node-id and bit timing to
    /// persistent storage (§6.4.5, §7.5.4).
    /// </summary>
    Task<LssServiceResult> StoreConfigurationAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads <c>1018h:01</c> from the single slave in LSS configuration (§7.6.1.2).</summary>
    Task<uint> InquireVendorIdAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads <c>1018h:02</c> from the single slave in LSS configuration (§7.6.1.3).</summary>
    Task<uint> InquireProductCodeAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads <c>1018h:03</c> from the single slave in LSS configuration (§7.6.1.4).</summary>
    Task<uint> InquireRevisionNumberAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads <c>1018h:04</c> from the single slave in LSS configuration (§7.6.1.5).</summary>
    Task<uint> InquireSerialNumberAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads the whole LSS address by the four inquire services (§6.5.2).</summary>
    Task<LssAddress> InquireAddressAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the active node-id of the single slave in LSS configuration (§7.6.2). A slave that
    /// is still in NMT initialization and has no active node-id reports its pending node-id,
    /// which may be <see cref="CanOpenCobId.UnconfiguredNodeId"/>.
    /// </summary>
    Task<byte> InquireNodeIdAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks every slave whose LSS address lies in <paramref name="range"/> to identify itself
    /// (§6.6.2, §7.7.1). Returns <see langword="true"/> when at least one does. The call waits
    /// out <see cref="LssMasterOptions.IdentificationTimeout"/> before it returns, so a later
    /// reply is not left for the next command.
    /// </summary>
    Task<bool> IdentifyRemoteSlavesAsync(LssIdentityRange range, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks every non-configured slave (pending node-id <see cref="CanOpenCobId.UnconfiguredNodeId"/>,
    /// no active node-id, NMT initialization) to identify itself (§6.6.4, §7.7.3). The call waits
    /// out <see cref="LssMasterOptions.IdentificationTimeout"/>, as
    /// <see cref="IdentifyRemoteSlavesAsync"/> does.
    /// </summary>
    Task<bool> IdentifyNonConfiguredSlavesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs LSS Fastscan for one unconfigured slave (§6.7, §8.2.3). A null
    /// <paramref name="request"/> scans all four LSS numbers from bit 31. On success that slave
    /// is in LSS configuration and the service LSS switch state selective is not required.
    /// </summary>
    /// <exception cref="ArgumentException">The vendor-id is skipped, or more than two LSS numbers
    /// are skipped. A partial scan must still select one slave (§6.7, annex A.3.4).</exception>
    Task<LssFastscanResult> FastscanAsync(LssFastscanRequest? request = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a manufacturer-specific command (specifier 128..255, §7.3) and returns the first
    /// slave frame with the same specifier, or <see langword="null"/> when none arrives within
    /// <see cref="LssMasterOptions.ResponseTimeout"/>. The returned buffer is 8 bytes.
    /// </summary>
    Task<byte[]?> TransceiveManufacturerAsync(byte commandSpecifier, ReadOnlyMemory<byte> parameters,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Which parts of the LSS address <see cref="ILssMaster.FastscanAsync"/> should scan, match or
/// skip (CiA 305 §6.7 and annex A.3.4).
/// </summary>
public enum LssFastscanFieldMode
{
    /// <summary>Determine the LSS number bit by bit. Bit 0 has priority over bit 1.</summary>
    Scan,

    /// <summary>The number is known. One check confirms it and advances the slave.</summary>
    Match,

    /// <summary>
    /// Do not look at this LSS number. At most two numbers may be skipped, and the vendor-id
    /// may not. The caller has to know the resulting address still selects one slave.
    /// </summary>
    Skip,
}

/// <summary>One of the four LSS numbers in a <see cref="LssFastscanRequest"/>.</summary>
public sealed class LssFastscanField
{
    /// <summary>How this number is treated. Defaults to <see cref="LssFastscanFieldMode.Scan"/>.</summary>
    public LssFastscanFieldMode Mode { get; init; } = LssFastscanFieldMode.Scan;

    /// <summary>
    /// The number to match, or the known high bits when <see cref="KnownHighBitCount"/> is set
    /// on a scan.
    /// </summary>
    public uint Value { get; init; }

    /// <summary>
    /// How many most-significant bits of <see cref="Value"/> are already known (0..32) when
    /// <see cref="Mode"/> is <see cref="LssFastscanFieldMode.Scan"/>. The scan checks that prefix
    /// in one step and then determines only the remaining bits (annex A.3.4). 32 is a match.
    /// </summary>
    public int KnownHighBitCount { get; init; }
}

/// <summary>What <see cref="ILssMaster.FastscanAsync"/> should look for. Defaults scan every bit.</summary>
public sealed class LssFastscanRequest
{
    /// <summary>Vendor-id. Cannot be <see cref="LssFastscanFieldMode.Skip"/>.</summary>
    public LssFastscanField VendorId { get; init; } = new();

    /// <summary>Product code.</summary>
    public LssFastscanField ProductCode { get; init; } = new();

    /// <summary>Revision number.</summary>
    public LssFastscanField RevisionNumber { get; init; } = new();

    /// <summary>Serial number.</summary>
    public LssFastscanField SerialNumber { get; init; } = new();
}
