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
    /// runs (CiA 301 v4.2.0 §7.5.2.36 and §7.5.2.38): the mapping is validated as it would be over
    /// SDO. The transmission type is <c>FFh</c>, event-driven and device-profile specific (CiA 301
    /// v4.2.0 §7.5.2.35 Table 68, §7.5.2.37 Table 72), the default CiA 401-1 v3.1.0 §6.2.5 and
    /// §6.2.6 give, not the manufacturer-specific <c>FEh</c> of <see cref="TpdoTransmission.EventDriven"/>.
    /// The file wins: a PDO a device description declared (either of its records) is left as the
    /// file has it, and a PDO the description leaves out, whose records the loader removed, is left
    /// out (a CANopen device provides CAN-IDs only for the communication objects it supports, CiA 301
    /// v4.2.0 §7.3.3).
    /// </remarks>
    /// <exception cref="ArgumentException">Two applications set the same PDO, one sets it twice, or
    /// the records refuse the default; the parameter is <c>options</c> and the message names the
    /// application.</exception>
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
                throw OptionsError(ReferenceEquals(first, setter)
                    ? $"Application '{setter.Application.Name}' sets the default of {PdoKind(isTpdo)} {number} twice: " +
                      "one application sets it, once."
                    : $"{PdoKind(isTpdo)} {number} gets its default from both " +
                      $"'{first.Application.Name}' and '{setter.Application.Name}': one application sets it, once.");
            }
            setters[(isTpdo, number)] = setter;
        }

        foreach (var (isTpdo, number, entries, enabled, setter) in set)
        {
            var comm = (ushort)((isTpdo ? Co.TpdoComm : Co.RpdoComm) + number - 1);
            var map = (ushort)((isTpdo ? Co.TpdoMap : Co.RpdoMap) + number - 1);
            if (_describedObjects.Contains(comm) || _describedObjects.Contains(map)) continue;
            if (_deviceDescription is not null && (!_od.ContainsIndex(comm) || !_od.ContainsIndex(map))) continue;
            var cobId = DefaultApplicationPdoCobId(comm, number, isTpdo, enabled);
            // An LSS slave without a node-id: the pre-defined COB-ID names node-id FFh, and for RPDO 4
            // (500h + FFh = 5FFh) that is a restricted CAN-ID, which the record refuses. Such a slave
            // stays in Initialisation until it is given a node-id (CiA 305 §4.2), PDOs exist only in
            // Operational (CiA 301 §7.3.2.2.5, Table 37), an RPDO on a restricted CAN-ID takes no frame
            // meanwhile (HandleIncomingFrame), and ApplyActiveNodeId moves the COB-ID to the node-id it is
            // given, as it does for the node's own placeholders. So the PDO is written invalid
            // through the procedure and made valid without the record's check. Only here and only
            // then: the record's validation of every other write stays as it is.
            bool validWithoutNodeId = number <= 4 && !CanOpenCobId.IsActiveNodeId(_nodeId)
                && (cobId & CanOpenCobId.InvalidBit) == 0;
            try
            {
                WritePdoRecords(comm, map, entries, validWithoutNodeId ? cobId | CanOpenCobId.InvalidBit : cobId,
                    CanOpenTransmissionType.EventDrivenProfile, inhibit: 0, eventTimerMs: 0, isTpdo, number);
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException(
                    $"Application '{setter.Application.Name}' sets the default of {PdoKind(isTpdo)} {number}, " +
                    $"which the PDO records refuse: {WithoutParameterSuffix(InnermostArgumentException(ex))}",
                    "options", ex);
            }
            if (validWithoutNodeId) _od.WriteRawUnchecked(comm, 0x01, ObjectDictionary.EncodeU32(cobId));
        }
    }

    /// <summary>The refusal that caused <paramref name="ex"/>: <see cref="WritePdoRecords"/> wraps the
    /// dictionary's, whose message is the one that says what was refused.</summary>
    private static ArgumentException InnermostArgumentException(ArgumentException ex)
    {
        var innermost = ex;
        while (innermost.InnerException is ArgumentException inner) innermost = inner;
        return innermost;
    }

    /// <summary>The message of <paramref name="ex"/> without the parameter suffix the runtime appends
    /// to it, in the runtime's own wording, so a wrapping exception carries one suffix, its own.</summary>
    private static string WithoutParameterSuffix(ArgumentException ex)
    {
        var message = ex.Message;
        if (string.IsNullOrEmpty(ex.ParamName)) return message;
        var suffix = new ArgumentException(string.Empty, ex.ParamName).Message;
        return message.EndsWith(suffix, StringComparison.Ordinal)
            ? message.Substring(0, message.Length - suffix.Length)
            : message;
    }

    /// <summary>
    /// The COB-ID word of a default PDO. Up to PDO 4 the pre-defined connection set of the node's
    /// node-id (CiA 301 v4.2.0 §7.3.3, Table 39; <see cref="DefaultPdoCobId"/>, as <c>ConfigureTpdo</c>
    /// uses), with bit 31 set when the PDO is not enabled. The set covers at most four RPDOs and four
    /// TPDOs (§7.3.3), so above PDO 4 the PDO stays invalid on the word the record holds, the node's
    /// placeholder without a CAN-ID: the record refuses any other invalid word without a CAN-ID, as it
    /// would over SDO.
    /// </summary>
    private uint DefaultApplicationPdoCobId(ushort comm, int number, bool isTpdo, bool enabled)
    {
        if (number > 4)
        {
            return (_od.TryReadUnsigned(comm, 0x01, out var placeholder) ? placeholder : 0) | CanOpenCobId.InvalidBit;
        }
        var word = DefaultPdoCobId(number, isTpdo);
        return enabled ? word : word | CanOpenCobId.InvalidBit;
    }
}
