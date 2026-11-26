using System;
using System.Collections.Generic;
using System.Threading;
using CanKit.Pro.CANopen.Application;
using CanKit.Pro.CANopen.Pdo;

namespace CanKit.Pro.CANopen;

// PDOs by number for an application (#558): it hears of each RPDO of its logical devices that the
// node took (ICanOpenApplication.OnRpdoReceived), and sends one of its TPDOs by number
// (ICanOpenApplicationHost.RequestTransmissionOfTpdo). On a node without applications nothing here
// runs: the receive path calls in only when an application is attached, and the request is a
// member of the host only an application holds.
internal sealed partial class CanOpenNode
{
    // The application that set the device type of each logical device, by logical device; null
    // until the device types are composed, and null where none was set. Written once, on the
    // constructing thread, before the start-up job is posted; read on the actor loop.
    private ApplicationSlot?[]? _logicalDeviceOwners;

    // The RPDOs taken and not yet told, oldest first, under _applicationGate. Told by the
    // application round after its writes (EvaluateApplicationEntriesOnActor).
    private List<(ApplicationSlot Slot, int PdoNumber)>? _queuedReceptions;

    // Changed event-driven TPDOs awaiting their matching reception callback in this actor round.
    private HashSet<int>? _deferredApplicationTpdos;

    private void FlushDeferredApplicationTpdos()
    {
        var pending = _deferredApplicationTpdos;
        if (pending is null) return;
        _deferredApplicationTpdos = null;
        foreach (int pdo in pending) RequestApplicationEventTpdo(pdo);
    }

    /// <summary>The application whose logical device uses PDO number <paramref name="pdoNumber"/>, or
    /// <see langword="null"/>: logical device <c>n</c> uses <c>n·64 + 1</c> to <c>n·64 + 64</c>
    /// (CiA 301 v4.2.0 §7.2.2.1, Table 3).</summary>
    private ApplicationSlot? PdoOwnerOf(int pdoNumber)
    {
        if (_logicalDeviceOwners is not { } owners) return null;
        for (int device = 0; device < owners.Length; device++)
        {
            if (owners[device] is not { } owner) continue;
            if (pdoNumber >= CanOpenLogicalDeviceAddressing.PdoNumber(device, 1)
                && pdoNumber <= CanOpenLogicalDeviceAddressing.PdoNumber(device, CanOpenLogicalDevices.PdosPerLogicalDevice))
                return owner;
        }
        return null;
    }

    /// <summary>
    /// RPDO <paramref name="pdoNumber"/> was taken and its data written (<see cref="ActuateRpdo"/>):
    /// queued for the application whose logical device uses the number, and told in the application
    /// round, after the command writes and the entries that round tells, the RPDO's own among them.
    /// Actor loop. Called only when an application is attached.
    /// </summary>
    private void QueueRpdoReceivedForApplications(int pdoNumber)
    {
        if (PdoOwnerOf(pdoNumber) is not { } owner || Volatile.Read(ref _disposed) != 0) return;
        bool post;
        lock (_applicationGate)
        {
            (_queuedReceptions ??= new()).Add((owner, pdoNumber));
            post = !_applicationEvaluationPosted;
            _applicationEvaluationPosted = true;
        }
        if (post) PostApplicationEvaluation();
    }

    /// <summary>Tells the RPDOs taken since the last round, in the order taken, each through the
    /// dispatcher. Actor loop, last in the application round.</summary>
    private void DeliverReceptions(List<(ApplicationSlot Slot, int PdoNumber)> receptions)
    {
        foreach (var (slot, pdoNumber) in receptions)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            InvokeApplication(slot, application => application.OnRpdoReceived(pdoNumber));
        }
    }

    /// <summary>
    /// <see cref="ICanOpenApplicationHost.RequestTransmissionOfTpdo"/>: TPDO <paramref name="pdoNumber"/>
    /// of <paramref name="slot"/>'s logical devices is sent as for any event — only in Operational,
    /// only when it exists, is no MPDO and has an event-driven transmission type (<c>FEh</c>,
    /// <c>FFh</c>; CiA 301 v4.2.0 §7.5.2.37, Table 72), subject to its inhibit time
    /// (<see cref="RequestEventDrivenTransmission"/>), and otherwise not at all. Unlike
    /// <see cref="TriggerTpdoOnActor"/> it does not latch an acyclic synchronous TPDO for the next
    /// SYNC: the application asks for a transmission now, not for a SYNC's. Actor loop.
    /// </summary>
    /// <exception cref="ArgumentException">The number lies in no PDO range of a logical device the
    /// application set a device type for.</exception>
    private void TransmitApplicationTpdo(ApplicationSlot slot, int pdoNumber)
    {
        if (!ReferenceEquals(PdoOwnerOf(pdoNumber), slot))
        {
            throw new ArgumentException(
                $"Application '{slot.Application.Name}' asks for TPDO {pdoNumber}, which lies in no PDO range of a logical " +
                "device it set the device type for: logical device n uses PDO numbers n·64 + 1 to n·64 + 64.",
                nameof(pdoNumber));
        }
        _deferredApplicationTpdos?.Remove(pdoNumber);
        RequestApplicationEventTpdo(pdoNumber);
    }

    private void RequestApplicationEventTpdo(int pdoNumber)
    {
        // Outside Operational RequestEventDrivenTransmission sends nothing, as for every event.
        if (_disposed != 0 || pdoNumber > _pdoCount) return;
        var rt = _tpdos[pdoNumber];
        // FEh (source) and FFh (destination) in the mapping's sub-index 00h make the TPDO an MPDO.
        if (rt is null || !rt.Valid || rt.MappingKind >= (byte)MpdoAddressMode.Source) return;
        if (CanOpenTransmissionType.IsEventDriven(rt.TransmissionType)) RequestEventDrivenTransmission(rt);
    }
}
