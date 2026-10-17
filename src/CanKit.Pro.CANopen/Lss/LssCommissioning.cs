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
    /// and index, and <see cref="Limit"/> 1. <see langword="null"/> (the default) configures the
    /// timing and does not activate it. A scan that can select more than one slave must not
    /// activate: that slave would switch rate while the next one is still found at the old rate.
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
    /// waiting after the services, whether they succeeded or not. Activation sends that frame
    /// before the switch delay, while the slave is still on the current bitrate.
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
/// next Fastscan cannot return it again, and then switches every slave to waiting. Configure
/// and inquire switch every slave to waiting and then select only the addressed one, so a slave
/// that was already in configuration does not take the same service. An assigning scan switches
/// every slave to waiting first. When Fastscan read the whole address, the found slave is
/// selected again before it is configured. A skipped LSS number is not in that address, so the
/// slave Fastscan left in configuration is configured there; selecting again would miss it.
/// Activating bit timing in a scan is limited to one slave.
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
    /// released to waiting. Bit timing is activated only when <see cref="LssUnconfiguredScan.Limit"/>
    /// is 1. Without an assignment, slaves are parked in configuration for the duration of
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
            // Fastscan switches only the slave it found. Clearing configuration first means an
            // assigning scan can configure that slave even when a skipped number is not in the
            // address and cannot be selected again.
            if (assigning)
                await _master.SwitchStateGlobalAsync(LssState.Waiting, cancellationToken).ConfigureAwait(false);

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
            // Cancellation or a transport failure can leave a slave in configuration. Fastscan
            // switches that slave before this method records it, so an empty list is not proof
            // that nobody was parked, and the next Fastscan would not see it.
            if (completed is null)
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

        if (!await SelectOnlyAsync(address, cancellationToken).ConfigureAwait(false))
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
                configuration.ReleaseToWaiting,
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
        if (!await SelectOnlyAsync(address, cancellationToken).ConfigureAwait(false))
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
        // Fastscan left this slave in configuration. When the address is complete, waiting
        // and selecting again drops any other slave that entered configuration during the
        // scan. A skipped number is stored as 0, so selecting that address would miss the
        // slave whose real number is not 0; the scan already left only that slave in
        // configuration.
        try
        {
            if (FastscanAddressIsComplete(scan.Fastscan))
            {
                await _master.SwitchStateGlobalAsync(LssState.Waiting, cancellationToken).ConfigureAwait(false);
                if (!await SelectAsync(address, cancellationToken).ConfigureAwait(false))
                    return new LssCommissioningResult(selected: false, address);
            }

            return await ApplyAsync(
                address,
                nodeId,
                scan.Store,
                scan.BitTimingSelector,
                scan.BitTimingIndex,
                scan.ActivateDelayMilliseconds,
                scan.ActivateBitRate,
                returnToWaiting: true,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await ReleaseToWaitingAsync().ConfigureAwait(false);
        }
    }

    private static bool FastscanAddressIsComplete(LssFastscanRequest? request)
    {
        if (request is null) return true;
        return request.ProductCode.Mode != LssFastscanFieldMode.Skip
            && request.RevisionNumber.Mode != LssFastscanFieldMode.Skip
            && request.SerialNumber.Mode != LssFastscanFieldMode.Skip;
    }

    private async Task<LssCommissioningResult> ApplyAsync(
        LssAddress address,
        byte? nodeId,
        bool store,
        byte? selector,
        byte? index,
        ushort? activateDelay,
        int? activateRate,
        bool returnToWaiting,
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

        // A rejected store must not switch the live rate: the timing was not persisted, and the
        // master's switch callback would leave the rest of the network at the old rate.
        bool stored = storeResult is null || storeResult.Value.Succeeded;
        if (stored && timingResult is { Succeeded: true } && activateDelay is { } delay)
            await _master.ActivateBitTimingAsync(delay, activateRate, cancellationToken, returnToWaiting)
                .ConfigureAwait(false);

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
    /// Selective switch, and a release if it throws after the slave may already be in configuration.
    /// A miss returns <see langword="false"/> and leaves other slaves where they are.
    /// </summary>
    private async Task<bool> SelectAsync(LssAddress address, CancellationToken cancellationToken)
    {
        try
        {
            return await _master.SwitchStateSelectiveAsync(address, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await ReleaseToWaitingAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Puts every slave in waiting, then selects <paramref name="address"/>. Selective switch
    /// does not move a slave that is already in configuration, so the services that follow
    /// would otherwise reach all of them. A miss has already returned the others to waiting:
    /// the addressed slave was not there, and leaving the rest in configuration would make the
    /// next service hit them.
    /// </summary>
    private async Task<bool> SelectOnlyAsync(LssAddress address, CancellationToken cancellationToken)
    {
        await _master.SwitchStateGlobalAsync(LssState.Waiting, cancellationToken).ConfigureAwait(false);
        return await SelectAsync(address, cancellationToken).ConfigureAwait(false);
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
        if (scan.ActivateDelayMilliseconds is not null && scan.Limit != 1)
            throw new ArgumentException(
                "Activating bit timing during a scan is only allowed when Limit is 1, so one slave does not switch rate before the next is found.",
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
