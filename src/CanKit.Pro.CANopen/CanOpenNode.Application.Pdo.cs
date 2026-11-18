using System;
using System.Collections.Generic;
using System.Linq;
using CanKit.Pro.CANopen.Application;
using CanKit.Pro.CANopen.Pdo;

namespace CanKit.Pro.CANopen;

// The default PDOs of a node with profiles: what an application sets in Attach, and what the PDO
// records hold once every application has attached.
internal sealed partial class CanOpenNode
{
    // What the applications set in Attach, in the order they set it; null until one does. Written
    // into the PDO records once every application has attached (ComposeDefaultPdos). Constructing
    // thread only.
    private List<(bool IsTpdo, int Number, PdoMappingEntry[] Entries, bool Enabled, ApplicationSlot Setter)>? _defaultPdos;

    private static string PdoKind(bool isTpdo) => isTpdo ? "TPDO" : "RPDO";

    /// <summary>
    /// Records the default <paramref name="slot"/>'s application gives one of its PDOs. Only the
    /// attaching application may, only for a PDO number up to the
    /// <see cref="CanOpenApplicationRequirements.PdoCount"/> it asked for, and only with entries in
    /// ranges it claims (dummy entries aside, which are no object). The mapping is copied here. A PDO
    /// set twice is not refused here but when the defaults are written, so that an application that
    /// swallows the exception cannot keep the open from failing.
    /// </summary>
    private void SetApplicationDefaultPdo(ApplicationSlot slot, bool isTpdo, int pdoNumber, PdoMapping mapping, bool enabled)
    {
        if (!ReferenceEquals(_attachingSlot, slot))
        {
            throw new InvalidOperationException(
                $"{(isTpdo ? nameof(ICanOpenApplicationHost.SetDefaultTpdo) : nameof(ICanOpenApplicationHost.SetDefaultRpdo))} " +
                "is for the application's Attach: the default PDOs are written into the PDO records when the node opens.");
        }
        if (mapping is null) throw new ArgumentNullException(nameof(mapping));
        var name = slot.Application.Name;
        if (pdoNumber < 1 || pdoNumber > slot.PdoCount)
        {
            throw new ArgumentOutOfRangeException(nameof(pdoNumber), pdoNumber,
                $"Application '{name}' sets the default of {PdoKind(isTpdo)} {pdoNumber}, but asks for {slot.PdoCount} PDOs " +
                "in CanOpenApplicationRequirements.PdoCount: the PDOs it configures are numbered 1 to that count.");
        }

        // A copy: a mapping the application changes after the call moves nothing.
        var entries = mapping.ToArray();
        foreach (var entry in entries)
        {
            if (entry.IsDummy || slot.Claims.Any(range => range.Contains(entry.Index))) continue;
            throw new ArgumentException(
                $"Application '{name}' maps {entry.Index:X4}:{entry.Subindex:X2} into {PdoKind(isTpdo)} {pdoNumber}, " +
                "which lies in no range it claims: a profile maps the objects it owns.", nameof(mapping));
        }
        (_defaultPdos ??= new()).Add((isTpdo, pdoNumber, entries, enabled, slot));
    }

    /// <summary>
    /// Writes the default PDOs the applications set into the PDO records, at the end of the attach
    /// phase and before the power-on values are taken again, so the defaults are among them. On the
    /// constructing thread, as the device description's records are written: the applies the writes
    /// post and the full rebuild the constructor posts after them bring the PDO engine in line, as
    /// <c>ConfigureTpdo</c> does.
    /// </summary>
    /// <remarks>
    /// Each through <see cref="WritePdoRecords"/>, the re-mapping procedure <c>ConfigureTpdo</c>
    /// runs: the mapping is validated as it would be over SDO. The transmission type is <c>FFh</c>,
    /// the profile-specific event-driven type (CiA 301 Table 72), which CiA 401-1 §6.2.5 and §6.2.6
    /// give the default PDOs, not the manufacturer-specific <c>FEh</c> of
    /// <see cref="TpdoTransmission.EventDriven"/>. A PDO a device description declared (either of
    /// its records) is left as the file has it: the description is the node's identity. A PDO the
    /// description leaves out does not exist (§7.3.3), and a default for it fails the open.
    /// </remarks>
    /// <exception cref="ArgumentException">Two applications set the same PDO, or the records refuse
    /// the default; the parameter is <c>options</c> and the message names the application.</exception>
    private void ComposeDefaultPdos()
    {
        var set = _defaultPdos;
        _defaultPdos = null;
        if (set is null) return;

        var setters = new Dictionary<(bool IsTpdo, int Number), ApplicationSlot>();
        foreach (var (isTpdo, number, _, _, setter) in set)
        {
            if (setters.TryGetValue((isTpdo, number), out var first))
            {
                throw OptionsError($"{PdoKind(isTpdo)} {number} gets its default from both " +
                    $"'{first.Application.Name}' and '{setter.Application.Name}': one application sets it, once.");
            }
            setters[(isTpdo, number)] = setter;
        }

        foreach (var (isTpdo, number, entries, enabled, setter) in set)
        {
            var comm = (ushort)((isTpdo ? Co.TpdoComm : Co.RpdoComm) + number - 1);
            var map = (ushort)((isTpdo ? Co.TpdoMap : Co.RpdoMap) + number - 1);
            if (_describedObjects.Contains(comm) || _describedObjects.Contains(map)) continue;
            try
            {
                WritePdoRecords(comm, map, entries, DefaultApplicationPdoCobId(comm, number, isTpdo, enabled),
                    CanOpenTransmissionType.EventDrivenProfile, inhibit: 0, eventTimerMs: 0, isTpdo, number);
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException(
                    $"Application '{setter.Application.Name}' sets the default of {PdoKind(isTpdo)} {number}: {ex.Message}",
                    "options", ex);
            }
        }
    }

    /// <summary>
    /// The COB-ID word of a default PDO. Up to PDO 4 the pre-defined connection set of the node's
    /// node-id (<see cref="DefaultPdoCobId"/>, as <c>ConfigureTpdo</c> uses), with bit 31 set when the
    /// PDO is not enabled. Above PDO 4 there is no pre-defined COB-ID, so the PDO stays invalid on the
    /// word the record holds, the node's placeholder without a CAN-ID: the record refuses any other
    /// invalid word without a CAN-ID, as it would over SDO.
    /// </summary>
    private uint DefaultApplicationPdoCobId(ushort comm, int number, bool isTpdo, bool enabled)
    {
        if (number > 4)
        {
            // An absent record (a description that leaves the PDO out) is refused by WritePdoRecords.
            return (_od.TryReadUnsigned(comm, 0x01, out var placeholder) ? placeholder : 0) | CanOpenCobId.InvalidBit;
        }
        var word = DefaultPdoCobId(number, isTpdo);
        return enabled ? word : word | CanOpenCobId.InvalidBit;
    }
}
