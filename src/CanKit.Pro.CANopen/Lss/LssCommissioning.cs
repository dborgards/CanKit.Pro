using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CanKit.Pro.CANopen.Lss;

/// <summary>
/// Why <see cref="LssCommissioning.ScanUnconfiguredAsync"/> stopped asking for another slave.
/// </summary>
public enum LssScanStop
{
    /// <summary>The Fastscan reset step drew no response. No unconfigured slave remains.</summary>
    NoUnconfiguredSlave,

    /// <summary>Fastscan proposed a pattern and no slave confirmed it.</summary>
    NoMatch,

    /// <summary>The scan found as many slaves as <see cref="LssUnconfiguredScan.Limit"/> allows.</summary>
    LimitReached,

    /// <summary>The next node-id would be above 127, so no further slave was selected.</summary>
    NodeIdsExhausted,

    /// <summary>
    /// The last slave was found, and configure node-id, bit timing or store did not succeed.
    /// That slave's result carries the code. It has been switched back to waiting.
    /// </summary>
    SlaveRejected,
}

/// <summary>
/// How <see cref="LssCommissioning.ScanUnconfiguredAsync"/> treats each unconfigured slave.
/// A null scan finds every unconfigured slave and changes nothing.
/// </summary>
public sealed class LssUnconfiguredScan
{
    /// <summary>
    /// Fastscan request. <see langword="null"/> (the default) scans all four LSS numbers from
    /// bit 31, so the numerically smallest address is found first.
    /// </summary>
    public LssFastscanRequest? Fastscan { get; init; }

    /// <summary>
    /// First node-id to assign, then the next integer for each further slave. <see langword="null"/>
    /// (the default) only lists addresses. Must be in 1..127 when set.
    /// </summary>
    public byte? AssignFromNodeId { get; init; }

    /// <summary>
    /// How many slaves to take. <see langword="null"/> (the default) runs until Fastscan finds
    /// no more, or until node-ids run out when <see cref="AssignFromNodeId"/> is set.
    /// </summary>
    public int? Limit { get; init; }

    /// <summary>
    /// When <see langword="true"/>, and a node-id is being assigned, store after a successful
    /// configure. Requires <see cref="AssignFromNodeId"/>.
    /// </summary>
    public bool Store { get; init; }

    /// <summary>Table selector passed to configure bit timing. Set together with
    /// <see cref="BitTimingIndex"/>, and only when a node-id is assigned.</summary>
    public byte? BitTimingSelector { get; init; }

    /// <summary>Table index passed to configure bit timing.</summary>
    public byte? BitTimingIndex { get; init; }

    /// <summary>
    /// Switch delay passed to activate bit timing, in milliseconds. Requires the table selector
    /// and index. <see langword="null"/> (the default) configures the timing and does not activate it.
    /// </summary>
    public ushort? ActivateDelayMilliseconds { get; init; }

    /// <summary>
    /// Bit rate passed to the master's <c>BitRateSwitch</c> during activate. The open channel is
    /// not reconfigured. Requires <see cref="ActivateDelayMilliseconds"/>.
    /// </summary>
    public int? ActivateBitRate { get; init; }
}

/// <summary>
/// What <see cref="LssCommissioning.ConfigureSlaveAsync"/> writes to one slave, after switch
/// state selective.
/// </summary>
public sealed class LssSlaveConfiguration
{
    /// <summary>Pending node-id. <see langword="null"/> leaves the node-id unchanged.</summary>
    public byte? NodeId { get; init; }

    /// <summary>When <see langword="true"/>, store after the other services succeed.</summary>
    public bool Store { get; init; }

    /// <summary>Table selector. Set together with <see cref="BitTimingIndex"/>.</summary>
    public byte? BitTimingSelector { get; init; }

    /// <summary>Table index.</summary>
    public byte? BitTimingIndex { get; init; }

    /// <summary>Switch delay of activate bit timing, in milliseconds. Requires the table.</summary>
    public ushort? ActivateDelayMilliseconds { get; init; }

    /// <summary>Bit rate passed to the master's switch callback during activate.</summary>
    public int? ActivateBitRate { get; init; }

    /// <summary>
    /// When <see langword="true"/> (the default), switch state global returns the slave to
    /// waiting after the services, whether they succeeded or not.
    /// </summary>
    public bool ReleaseToWaiting { get; init; } = true;
}

/// <summary>One slave a commissioning call selected, configured, or read.</summary>
public sealed class LssCommissioningResult
{
    /// <summary>Constructs a result.</summary>
    public LssCommissioningResult(
        bool selected,
        LssAddress address,
        byte? nodeId = null,
        LssServiceResult? nodeIdResult = null,
        LssServiceResult? bitTimingResult = null,
        LssServiceResult? storeResult = null)
    {
        Selected = selected;
        Address = address;
        NodeId = nodeId;
        NodeIdResult = nodeIdResult;
        BitTimingResult = bitTimingResult;
        StoreResult = storeResult;
    }

    /// <summary>
    /// <see langword="false"/> when switch state selective drew no confirmation. The other
    /// fields then describe nothing the bus changed. A Fastscan hit and an SDO identity read
    /// are <see langword="true"/> in the sense that the address was obtained; selective is the
    /// call that can miss.
    /// </summary>
    public bool Selected { get; }

    /// <summary>LSS address. The requested address when <see cref="Selected"/> is
    /// <see langword="false"/>.</summary>
    public LssAddress Address { get; }

    /// <summary>Node-id that was assigned, or the one inquire or SDO reported.</summary>
    public byte? NodeId { get; }

    /// <summary>Configure node-id, when that service ran.</summary>
    public LssServiceResult? NodeIdResult { get; }

    /// <summary>Configure bit timing, when that service ran.</summary>
    public LssServiceResult? BitTimingResult { get; }

    /// <summary>Store configuration, when that service ran.</summary>
    public LssServiceResult? StoreResult { get; }

    /// <summary>
    /// Every service that ran succeeded. A selective miss is not a success. A result that only
    /// lists an address, with no service, is a success.
    /// </summary>
    public bool Succeeded
    {
        get
        {
            if (!Selected) return false;
            if (NodeIdResult is { Succeeded: false }) return false;
            if (BitTimingResult is { Succeeded: false }) return false;
            if (StoreResult is { Succeeded: false }) return false;
            return true;
        }
    }
}

/// <summary>Outcome of a Fastscan over the unconfigured slaves.</summary>
public sealed class LssUnconfiguredScanResult
{
    /// <summary>Constructs a scan outcome.</summary>
    public LssUnconfiguredScanResult(IReadOnlyList<LssCommissioningResult> slaves, LssScanStop stop)
    {
        if (slaves is null) throw new ArgumentNullException(nameof(slaves));
        Slaves = slaves;
        Stop = stop;
    }

    /// <summary>Slaves found, in the order Fastscan returned them.</summary>
    public IReadOnlyList<LssCommissioningResult> Slaves { get; }

    /// <summary>Why the scan did not take another slave.</summary>
    public LssScanStop Stop { get; }
}

/// <summary>
/// Commissioning on top of an <see cref="ILssMaster"/> (CiA 305 §6). The master is not owned.
/// Exactly one LSS master may be active in the network; this type does not add another.
/// </summary>
/// <remarks>
/// A scan that only lists slaves leaves each one in configuration until the scan ends, so the
/// next Fastscan cannot return it again, and then switches every slave to waiting. A scan that
/// assigns node-ids switches back to waiting after each slave, before the next Fastscan, so
/// configure node-id reaches only the slave just found. Switch state global also releases any
/// other slave that was already in configuration.
/// </remarks>
public sealed class LssCommissioning
{
    private readonly ILssMaster _master;

    /// <summary>Uses <paramref name="master"/> for every service. The caller disposes it.</summary>
    public LssCommissioning(ILssMaster master)
    {
        if (master is null) throw new ArgumentNullException(nameof(master));
        _master = master;
    }

    /// <summary>The master this tool drives.</summary>
    public ILssMaster Master => _master;

    /// <summary>
    /// Finds unconfigured slaves by Fastscan (§6.7). With <see cref="LssUnconfiguredScan.AssignFromNodeId"/>,
    /// each one is given the next node-id and, when asked, a bit timing and a store, then
    /// released to waiting. Without it, slaves are parked in configuration for the duration of
    /// the scan and released together at the end.
    /// </summary>
    public async Task<LssUnconfiguredScanResult> ScanUnconfiguredAsync(
        LssUnconfiguredScan? scan = null,
        CancellationToken cancellationToken = default)
    {
        scan ??= new LssUnconfiguredScan();
        ValidateScan(scan);
        bool assigning = scan.AssignFromNodeId.HasValue;
        int nextId = scan.AssignFromNodeId ?? 0;
        var slaves = new List<LssCommissioningResult>();
        LssUnconfiguredScanResult? completed = null;
        try
        {
            while (scan.Limit is null || slaves.Count < scan.Limit.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (assigning && nextId > CanOpenCobId.MaxNodeId)
                {
                    completed = new LssUnconfiguredScanResult(slaves, LssScanStop.NodeIdsExhausted);
                    return completed;
                }

                var found = await _master.FastscanAsync(scan.Fastscan, cancellationToken).ConfigureAwait(false);
                if (found.Status == LssFastscanStatus.NoUnconfiguredSlave)
                    break;
                if (found.Status == LssFastscanStatus.NoMatch)
                {
                    // A miss that parked nobody must not switch other slaves out of configuration.
                    completed = await Finish(slaves, LssScanStop.NoMatch, releaseParked: slaves.Count > 0 && !assigning)
                        .ConfigureAwait(false);
                    return completed;
                }

                if (!assigning)
                {
                    slaves.Add(new LssCommissioningResult(selected: true, found.Address));
                    continue;
                }

                var result = await ConfigureFoundSlaveAsync(
                    found.Address, (byte)nextId, scan, cancellationToken).ConfigureAwait(false);
                slaves.Add(result);
                if (!result.Succeeded)
                {
                    completed = new LssUnconfiguredScanResult(slaves, LssScanStop.SlaveRejected);
                    return completed;
                }
                nextId++;
            }

            var stop = scan.Limit is not null && slaves.Count >= scan.Limit.Value
                ? LssScanStop.LimitReached
                : LssScanStop.NoUnconfiguredSlave;
            completed = await Finish(slaves, stop, releaseParked: slaves.Count > 0 && !assigning)
                .ConfigureAwait(false);
            return completed;
        }
        finally
        {
            // Cancellation or a transport failure leaves the parked slaves in configuration,
            // where the next Fastscan will not see them.
            if (completed is null && slaves.Count > 0 && !assigning)
                await ReleaseToWaitingAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Switches the slave with <paramref name="address"/> to configuration and applies
    /// <paramref name="configuration"/>. Returns a result with <see cref="LssCommissioningResult.Selected"/>
    /// <see langword="false"/> when no slave confirms.
    /// </summary>
    public async Task<LssCommissioningResult> ConfigureSlaveAsync(
        LssAddress address,
        LssSlaveConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        if (configuration is null) throw new ArgumentNullException(nameof(configuration));
        ValidateServices(
            configuration.NodeId,
            configuration.BitTimingSelector,
            configuration.BitTimingIndex,
            configuration.ActivateDelayMilliseconds,
            configuration.ActivateBitRate,
            nameof(configuration));

        if (!await _master.SwitchStateSelectiveAsync(address, cancellationToken).ConfigureAwait(false))
            return new LssCommissioningResult(selected: false, address);

        try
        {
            return await ApplyAsync(
                address,
                configuration.NodeId,
                configuration.Store,
                configuration.BitTimingSelector,
                configuration.BitTimingIndex,
                configuration.ActivateDelayMilliseconds,
                configuration.ActivateBitRate,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (configuration.ReleaseToWaiting)
                await ReleaseToWaitingAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Switches the slave with <paramref name="address"/> to configuration and reads its LSS
    /// address and node-id (§6.5). The slave is returned to waiting. A selective miss is
    /// <see cref="LssCommissioningResult.Selected"/> <see langword="false"/>.
    /// </summary>
    public async Task<LssCommissioningResult> InquireSlaveAsync(
        LssAddress address,
        CancellationToken cancellationToken = default)
    {
        if (!await _master.SwitchStateSelectiveAsync(address, cancellationToken).ConfigureAwait(false))
            return new LssCommissioningResult(selected: false, address);

        try
        {
            var read = await _master.InquireAddressAsync(cancellationToken).ConfigureAwait(false);
            byte nodeId = await _master.InquireNodeIdAsync(cancellationToken).ConfigureAwait(false);
            return new LssCommissioningResult(selected: true, read, nodeId);
        }
        finally
        {
            await ReleaseToWaitingAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads <c>1018h:01</c>..<c>04</c> of each booted node in <paramref name="nodeIds"/> through
    /// <paramref name="client"/>. A node that does not answer, or aborts the vendor-id, is
    /// skipped. A missing later sub-index counts as 0. This is how a configured slave, which
    /// does not take part in Fastscan, is listed.
    /// </summary>
    public async Task<IReadOnlyList<LssCommissioningResult>> ReadIdentitiesAsync(
        ICanOpenNode client,
        IEnumerable<byte> nodeIds,
        CancellationToken cancellationToken = default)
    {
        if (client is null) throw new ArgumentNullException(nameof(client));
        if (nodeIds is null) throw new ArgumentNullException(nameof(nodeIds));
        var results = new List<LssCommissioningResult>();
        foreach (byte nodeId in nodeIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CanOpenCobId.IsActiveNodeId(nodeId) || nodeId == client.NodeId)
                continue;
            if (await TryReadIdentityAsync(client, nodeId, cancellationToken).ConfigureAwait(false) is not { } address)
                continue;
            results.Add(new LssCommissioningResult(selected: true, address, nodeId));
        }

        return results;
    }

    private async Task<LssCommissioningResult> ConfigureFoundSlaveAsync(
        LssAddress address,
        byte nodeId,
        LssUnconfiguredScan scan,
        CancellationToken cancellationToken)
    {
        // Fastscan already left this slave in configuration. Release it before the next scan
        // even when a service fails or throws, so a later attempt can find it again.
        try
        {
            return await ApplyAsync(
                address,
                nodeId,
                scan.Store,
                scan.BitTimingSelector,
                scan.BitTimingIndex,
                scan.ActivateDelayMilliseconds,
                scan.ActivateBitRate,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await ReleaseToWaitingAsync().ConfigureAwait(false);
        }
    }

    private async Task<LssCommissioningResult> ApplyAsync(
        LssAddress address,
        byte? nodeId,
        bool store,
        byte? selector,
        byte? index,
        ushort? activateDelay,
        int? activateRate,
        CancellationToken cancellationToken)
    {
        LssServiceResult? nodeIdResult = null;
        LssServiceResult? timingResult = null;
        LssServiceResult? storeResult = null;
        if (nodeId is { } id)
        {
            nodeIdResult = await _master.ConfigureNodeIdAsync(id, cancellationToken).ConfigureAwait(false);
            if (!nodeIdResult.Value.Succeeded)
                return new LssCommissioningResult(true, address, id, nodeIdResult);
        }

        if (selector is { } table && index is { } tableIndex && (nodeIdResult is null || nodeIdResult.Value.Succeeded))
        {
            timingResult = await _master.ConfigureBitTimingAsync(table, tableIndex, cancellationToken)
                .ConfigureAwait(false);
        }

        // Store while both ends are still on the current bitrate. Activation is the last
        // configuration service: after it, the slave transmits at the new rate (§6.5).
        bool servicesOk = (nodeIdResult is null || nodeIdResult.Value.Succeeded)
            && (timingResult is null || timingResult.Value.Succeeded);
        if (store && servicesOk)
            storeResult = await _master.StoreConfigurationAsync(cancellationToken).ConfigureAwait(false);

        if (timingResult is { Succeeded: true } && activateDelay is { } delay)
            await _master.ActivateBitTimingAsync(delay, activateRate, cancellationToken).ConfigureAwait(false);

        return new LssCommissioningResult(true, address, nodeId, nodeIdResult, timingResult, storeResult);
    }

    private async Task<LssUnconfiguredScanResult> Finish(
        List<LssCommissioningResult> slaves,
        LssScanStop stop,
        bool releaseParked)
    {
        if (releaseParked)
            await ReleaseToWaitingAsync().ConfigureAwait(false);
        return new LssUnconfiguredScanResult(slaves, stop);
    }

    /// <summary>
    /// Returns every slave to waiting. Uses its own token: the caller's token is often already
    /// cancelled when this runs from a <c>finally</c>, and the waiting frame would never be sent.
    /// </summary>
    private async Task ReleaseToWaitingAsync()
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await _master.SwitchStateGlobalAsync(LssState.Waiting, cleanup.Token).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The master went away with the scan.
        }
    }

    private static async Task<LssAddress?> TryReadIdentityAsync(
        ICanOpenNode client, byte nodeId, CancellationToken cancellationToken)
    {
        byte[] vendor;
        try
        {
            vendor = await client.SdoUploadAsync(nodeId, 0x1018, 0x01, cancellationToken).ConfigureAwait(false);
        }
        catch (Sdo.SdoAbortException)
        {
            return null;
        }

        uint product = await ReadOrZeroAsync(client, nodeId, 0x02, cancellationToken).ConfigureAwait(false);
        uint revision = await ReadOrZeroAsync(client, nodeId, 0x03, cancellationToken).ConfigureAwait(false);
        uint serial = await ReadOrZeroAsync(client, nodeId, 0x04, cancellationToken).ConfigureAwait(false);
        return new LssAddress(ToUInt32(vendor), product, revision, serial);
    }

    private static async Task<uint> ReadOrZeroAsync(
        ICanOpenNode client, byte nodeId, byte subindex, CancellationToken cancellationToken)
    {
        try
        {
            var data = await client.SdoUploadAsync(nodeId, 0x1018, subindex, cancellationToken).ConfigureAwait(false);
            return ToUInt32(data);
        }
        catch (Sdo.SdoAbortException)
        {
            return 0;
        }
    }

    private static uint ToUInt32(byte[] data)
    {
        uint value = 0;
        int length = Math.Min(data.Length, 4);
        for (int i = 0; i < length; i++)
            value |= (uint)data[i] << (8 * i);
        return value;
    }

    private static void ValidateScan(LssUnconfiguredScan scan)
    {
        if (scan.Limit is { } limit && limit < 1)
            throw new ArgumentOutOfRangeException(nameof(scan), limit, "The scan limit must be at least 1.");
        if (scan.AssignFromNodeId is { } first && !CanOpenCobId.IsActiveNodeId(first))
            throw new ArgumentOutOfRangeException(nameof(scan), first, "The first node-id must be in 1..127.");
        if (scan.AssignFromNodeId is null && (scan.Store || scan.BitTimingSelector is not null || scan.BitTimingIndex is not null))
            throw new ArgumentException(
                "Storing or changing bit timing during a scan needs AssignFromNodeId, so each service hits one slave.",
                nameof(scan));
        ValidateServices(
            scan.AssignFromNodeId,
            scan.BitTimingSelector,
            scan.BitTimingIndex,
            scan.ActivateDelayMilliseconds,
            scan.ActivateBitRate,
            nameof(scan));
    }

    private static void ValidateServices(
        byte? nodeId,
        byte? selector,
        byte? index,
        ushort? activateDelay,
        int? activateRate,
        string paramName)
    {
        if (nodeId is { } id && id != CanOpenCobId.UnconfiguredNodeId && !CanOpenCobId.IsActiveNodeId(id))
            throw new ArgumentOutOfRangeException(paramName, id, "A node-id must be 1..127 or 0xFF.");
        if (selector.HasValue != index.HasValue)
            throw new ArgumentException("Bit timing needs both a table selector and a table index.", paramName);
        if (activateDelay is not null && selector is null)
            throw new ArgumentException("Activating bit timing needs a table selector and index.", paramName);
        if (activateRate is not null && activateDelay is null)
            throw new ArgumentException("A bit rate to activate needs the switch delay.", paramName);
    }
}
