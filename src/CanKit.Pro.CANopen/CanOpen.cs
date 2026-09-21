using System;
using CanKit.Abstractions.API.Can;
using CanKit.Pro.RawCan;

namespace CanKit.Pro.CANopen;

/// <summary>
/// Factory entry point for opening <see cref="ICanOpenNode"/> instances. Non-instantiable.
/// </summary>
/// <remarks>
/// Mirrors the ownership patterns used by <c>CanKit.Pro.IsoTp</c>, <c>CanKit.Pro.J1939Tp</c> and
/// <c>CanKit.Pro.Uds</c>:
/// <list type="bullet">
///   <item><description><see cref="OpenNode(ICanBus, byte, CanOpenNodeOptions?)"/> — the node
///   owns a private <see cref="ICanBusService"/> wrapping the supplied bus and disposes it on
///   dispose. Convenient for single-protocol callers.</description></item>
///   <item><description><see cref="OpenNode(ICanBusService, byte, CanOpenNodeOptions?, bool)"/> —
///   the caller supplies an existing service (shared with other protocols on the same bus per
///   FR-CO-012); with <c>leaveOpen=true</c> the service outlives the node.</description></item>
/// </list>
/// </remarks>
public static class CanOpen
{
    /// <summary>Opens a node that owns a private <see cref="CanBusService"/> around
    /// <paramref name="bus"/>. Disposing the node disposes that service and detaches from
    /// <paramref name="bus"/>; the bus itself is not disposed.</summary>
    public static ICanOpenNode OpenNode(ICanBus bus, byte nodeId, CanOpenNodeOptions? options = null)
    {
        if (bus is null) throw new ArgumentNullException(nameof(bus));
        var service = new CanBusService(bus);
        try
        {
            return new CanOpenNode(service, nodeId, options ?? new CanOpenNodeOptions(),
                ownsService: true);
        }
        catch
        {
            service.Dispose();
            throw;
        }
    }

    /// <summary>Opens a node bound to an already-existing <paramref name="service"/>. Multiple
    /// nodes with different node-ids may share the same service to multiplex several CANopen
    /// identities over one physical bus, or to co-exist with other protocol layers (ISO-TP,
    /// J1939, ...).</summary>
    /// <param name="service">The demux service. Must not be null.</param>
    /// <param name="nodeId">CANopen node-id 1..127 this node identifies as.</param>
    /// <param name="options">Node options; defaults to a fresh <see cref="CanOpenNodeOptions"/>.</param>
    /// <param name="leaveOpen">When <c>true</c> (default) disposing the node does not dispose
    /// <paramref name="service"/>; when <c>false</c> the node takes ownership and disposes the
    /// service on its own <see cref="IDisposable.Dispose"/>.</param>
    public static ICanOpenNode OpenNode(ICanBusService service, byte nodeId,
        CanOpenNodeOptions? options = null, bool leaveOpen = true)
    {
        if (service is null) throw new ArgumentNullException(nameof(service));
        return new CanOpenNode(service, nodeId, options ?? new CanOpenNodeOptions(),
            ownsService: !leaveOpen);
    }

    /// <summary>
    /// Opens a node shaped by a device description: its object dictionary and its PDO, SYNC,
    /// EMCY, heartbeat and guarding configuration come from <paramref name="description"/>,
    /// with <c>$NODEID</c> resolved to <paramref name="nodeId"/>. What the node could not take
    /// as written is in <see cref="ICanOpenNode.DeviceDescription"/>.
    /// </summary>
    public static ICanOpenNode OpenNode(ICanBus bus, byte nodeId, CanOpenDeviceDescription description,
        CanOpenNodeOptions? options = null)
    {
        if (bus is null) throw new ArgumentNullException(nameof(bus));
        if (description is null) throw new ArgumentNullException(nameof(description));
        var service = new CanBusService(bus);
        try
        {
            return new CanOpenNode(service, nodeId, options ?? new CanOpenNodeOptions(),
                ownsService: true, timeSource: null, description);
        }
        catch
        {
            service.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens a node shaped by a DCF, as the node-id the file was commissioned for
    /// (<c>[DeviceComissioning] NodeID</c>).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="description"/> is an EDS, which
    /// describes a device type and carries no node-id.</exception>
    public static ICanOpenNode OpenNode(ICanBus bus, CanOpenDeviceDescription description,
        CanOpenNodeOptions? options = null)
    {
        if (description is null) throw new ArgumentNullException(nameof(description));
        if (description.NodeId is not { } nodeId)
            throw new ArgumentException("An EDS carries no node-id; pass the node-id, or open the node from a DCF.", nameof(description));
        return OpenNode(bus, nodeId, description, options);
    }

    /// <summary>Opens a node shaped by a device description on an existing service (see
    /// <see cref="OpenNode(ICanBusService, byte, CanOpenNodeOptions?, bool)"/> for ownership).</summary>
    public static ICanOpenNode OpenNode(ICanBusService service, byte nodeId, CanOpenDeviceDescription description,
        CanOpenNodeOptions? options = null, bool leaveOpen = true)
    {
        if (service is null) throw new ArgumentNullException(nameof(service));
        if (description is null) throw new ArgumentNullException(nameof(description));
        return new CanOpenNode(service, nodeId, options ?? new CanOpenNodeOptions(),
            ownsService: !leaveOpen, timeSource: null, description);
    }
}
