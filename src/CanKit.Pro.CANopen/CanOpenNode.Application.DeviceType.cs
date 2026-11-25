using System;
using System.Collections.Generic;
using System.Linq;
using CanKit.Pro.CANopen.Application;

namespace CanKit.Pro.CANopen;

// The device type of a node with profiles: what an application sets in Attach, and what 1000h and
// the chain of 67FFh objects hold once every application has attached.
internal sealed partial class CanOpenNode
{
    // =========================================================================================
    // Device type: 1000h and the chain of 67FFh, 6FFFh... (CiA 301 v4.2.0 §7.5.2.1).
    // =========================================================================================

    // The application whose Attach is running, or null. Constructing thread only.
    private ApplicationSlot? _attachingSlot;

    // What the applications set in Attach, in the order they set it; null until one does. Composed
    // into the dictionary once every application has attached (ComposeDeviceTypes). Constructing
    // thread only.
    private List<(int LogicalDevice, CanOpenDeviceType DeviceType, ApplicationSlot Setter)>? _deviceTypes;

    // Whether the node wrote 1000h from what the applications set (no device description declared
    // it). Written once, on the constructing thread, before the power-on values are taken and before
    // anything else reads it: IsRestorableObject reads it on the actor and, for a 1010h save, on the
    // writer's thread.
    private bool _composedDeviceType;

    /// <summary>
    /// Records the device type <paramref name="slot"/>'s application gives one of its logical
    /// devices. Only the attaching application may, and only for a device whose <c>67FFh + n·800h</c>
    /// lies in a range it claims. A device set twice is not refused here but when the types are
    /// composed, so that an application that swallows the exception cannot keep the open from failing.
    /// </summary>
    private void SetApplicationDeviceType(ApplicationSlot slot, int logicalDevice, CanOpenDeviceType deviceType)
    {
        if (!ReferenceEquals(_attachingSlot, slot))
        {
            throw new InvalidOperationException(
                "SetDeviceType is for the application's Attach: the device types are composed into " +
                "1000h and the chain of 67FFh objects when the node opens.");
        }
        // Checks the number as well: ArgumentOutOfRangeException, parameter logicalDevice.
        var index = DeviceTypeObjectOf(logicalDevice);
        if (!slot.Claims.Any(range => range.Contains(index)))
        {
            throw new ArgumentException(
                $"Application '{slot.Application.Name}' sets the device type of logical device {logicalDevice}, " +
                $"which lives in 0x{index:X4}: it does not claim that index.", nameof(logicalDevice));
        }
        (_deviceTypes ??= new()).Add((logicalDevice, deviceType, slot));
    }

    /// <summary>
    /// Puts the device types the applications set into the dictionary, at the end of the attach
    /// phase and before the power-on values are taken again. One logical device: its word is
    /// <c>1000h</c>. More: <c>1000h</c> holds the profile number of device 0 and <c>FFFFh</c> above
    /// it, and every device's full word is the read-only <c>67FFh + n·800h</c>, so device 0's
    /// additional information lives there only (§7.5.2.1). The logical devices must be 0 to n-1: a
    /// reader stops at the first object that does not exist, so a gap would hide the devices behind
    /// it. An object a device description declared is not written; its profile number must be the
    /// one the application set, and a described <c>1000h</c> must announce the logical devices the
    /// node has (<see cref="RequireDescribedTopology"/>). The words themselves are checked against
    /// the value definition first (<see cref="RequireValidWords"/>), #551, and a chain object behind
    /// the attached devices that a reader reaches is checked last (<see cref="RequireReadableChain"/>),
    /// #575.
    /// </summary>
    private void ComposeDeviceTypes()
    {
        var set = _deviceTypes;
        _deviceTypes = null;
        if (set is null) return;

        var types = new CanOpenDeviceType[CanOpenLogicalDeviceAddressing.MaxLogicalDevices];
        var setters = new ApplicationSlot?[CanOpenLogicalDeviceAddressing.MaxLogicalDevices];
        foreach (var (device, type, setter) in set)
        {
            if (setters[device] is { } first)
            {
                throw OptionsError($"Logical device {device} gets its device type from both " +
                    $"'{first.Application.Name}' and '{setter.Application.Name}': one application sets it, once.");
            }
            types[device] = type;
            setters[device] = setter;
        }

        int count = 0;
        while (count < setters.Length && setters[count] is not null) count++;
        for (int later = count + 1; later < setters.Length; later++)
        {
            if (setters[later] is not { } setter) continue;
            throw OptionsError($"Logical device {count} has no device type, but logical device {later} has one " +
                $"(set by '{setter.Application.Name}'): the logical devices are numbered from 0 without a gap, " +
                "or a reader of the chain would stop at the missing one and never see the rest.");
        }

        RequireValidWords(types, setters, count);
        // Whose RPDOs and TPDOs a PDO number names (OnRpdoReceived, RequestTransmissionOfTpdo).
        _logicalDeviceOwners = setters;

        if (_describedObjects.Contains(Co.DeviceType))
        {
            RequireDescribedProfile(Co.DeviceType, setters[0]!, types[0]);
            RequireDescribedTopology(types, setters, count);
        }
        else
        {
            var word = count == 1
                ? types[0].Value
                : types[0].ProfileNumber | ((uint)CanOpenDeviceType.FurtherLogicalDevices << 16);
            _od.WriteRawUnchecked(Co.DeviceType, 0x00, ObjectDictionary.EncodeU32(word));
            // The node holds this identity now, and for a single logical device 1000h is the only
            // place it lives: 1000h is restorable from here on (IsRestorableObject). Before
            // AttachApplications retakes the power-on values, so the composed word is among them.
            _composedDeviceType = true;
        }

        for (int device = 0; device < count; device++)
        {
            var index = DeviceTypeObjectOf(device);
            if (_describedObjects.Contains(index))
            {
                RequireDescribedProfile(index, setters[device]!, types[device]);
            }
            else if (count > 1)
            {
                // Not silently replaced: the application declared it, and the node declares it
                // from the type the application set.
                if (_od.ContainsIndex(index))
                {
                    throw OptionsError($"Application '{setters[device]!.Application.Name}' declares object 0x{index:X4} itself, " +
                        "but the node declares it from SetDeviceType when a node has several logical devices: " +
                        "leave the object out and set the device type.");
                }
                _od.Declare(index, 0x00, OdDataType.Unsigned32, OdAccess.ReadOnly,
                    ObjectDictionary.EncodeU32(types[device].Value), pdoMappable: false);
            }
        }

        RequireReadableChain(count);
    }

    /// <summary>
    /// The words the applications set, against the value definition of CiA 301 v4.2.0 §7.5.2.1,
    /// before anything is written: a profile number of <c>0000h</c> ("does not follow a standardized
    /// profile") takes additional information <c>0000h</c> or <c>FFFFh</c> and nothing else, in
    /// <c>1000h</c> and, by "the very same value definition", in a chain object; and the word of a
    /// node's only logical device, which becomes <c>1000h</c>, does not carry <c>FFFFh</c>, which says
    /// a further logical device is implemented. A reader would follow it to a <c>67FFh</c> the node
    /// does not declare and take the word of <c>1000h</c>, <c>FFFFh</c> included, for the device's own.
    /// Any other additional information is the profile's (§7.5.2.1) and is kept as set.
    /// </summary>
    private static void RequireValidWords(CanOpenDeviceType[] types, ApplicationSlot?[] setters, int count)
    {
        for (int device = 0; device < count; device++)
        {
            var type = types[device];
            var setter = setters[device]!.Application.Name;
            if (type.HasReservedAdditionalInformation)
            {
                throw OptionsError($"Application '{setter}' sets logical device {device} to 0x{type.Value:X8}: profile " +
                    "number 0000h is a device without a standardized profile, whose additional information is 0000h, " +
                    "or FFFFh when a further logical device follows (CiA 301 v4.2.0 §7.5.2.1).");
            }
        }
        if (count == 1 && types[0].HasFurtherLogicalDevices)
        {
            throw OptionsError($"Application '{setters[0]!.Application.Name}' sets logical device 0 to 0x{types[0].Value:X8}, " +
                "whose additional information FFFFh announces a further logical device, but the node has only this one " +
                "(CiA 301 v4.2.0 §7.5.2.1): a reader of 1000h would look for the next one in 67FFh.");
        }
    }

    /// <summary>
    /// A described <c>1000h</c> wins over the composed word, so it has to announce the logical devices
    /// the node has (CiA 301 v4.2.0 §7.5.2.1): <c>FFFFh</c> in its upper half when there are several, or
    /// a reader of <c>1000h</c> stops at the first and never sees the rest; and with <c>FFFFh</c>, a
    /// <c>67FFh</c> for a reader to find, declared by the file or the application, or the reader takes
    /// the word of <c>1000h</c> for the only device's; and behind it a <c>6FFFh</c>, since
    /// <c>FFFFh</c> announces several devices and a reader that finds one calls the chain
    /// contradictory (#575). A <c>67FFh</c> the application declared is
    /// checked here like a described one (<see cref="RequireDescribedProfile"/>): with one logical
    /// device nothing else refuses or checks it.
    /// </summary>
    private void RequireDescribedTopology(CanOpenDeviceType[] types, ApplicationSlot?[] setters, int count)
    {
        // RequireDescribedProfile has read it as an UNSIGNED32 already.
        _od.TryReadRaw(Co.DeviceType, 0x00, out var raw);
        CanOpenDeviceType.TryParse(raw, out var described);
        if (count > 1 && !described.HasFurtherLogicalDevices)
        {
            var hidden = string.Join(", ", Enumerable.Range(1, count - 1).Select(device =>
                $"logical device {device} ('{setters[device]!.Application.Name}', profile {types[device].ProfileNumber})"));
            throw OptionsError($"The device description's object 0x1000 holds 0x{described.Value:X8}, which announces one " +
                $"logical device, but {count} are attached: {hidden} would be hidden behind it. With several logical " +
                "devices 1000h holds FFFFh in its upper half (CiA 301 v4.2.0 §7.5.2.1).");
        }
        if (count == 1 && described.HasFurtherLogicalDevices && !_od.ContainsIndex(DeviceTypeObjectOf(0)))
        {
            throw OptionsError($"The device description's object 0x1000 holds 0x{described.Value:X8}, which announces further " +
                $"logical devices, but neither the file nor application '{setters[0]!.Application.Name}' declares 0x67FF, " +
                "where a reader looks for the first of them (CiA 301 v4.2.0 §7.5.2.1).");
        }
        // Then the reader takes 67FFh for the device's type. A described one is checked with the chain
        // (ComposeDeviceTypes); one the application declared itself is checked here, by the same rules:
        // with one logical device the node leaves it alone and checks nothing else about it.
        var first = DeviceTypeObjectOf(0);
        if (count == 1 && described.HasFurtherLogicalDevices && !_describedObjects.Contains(first))
            RequireDescribedProfile(first, setters[0]!, types[0], $"Application '{setters[0]!.Application.Name}'");
        // FFFFh says the node is a module of several logical devices, so behind 67FFh a reader looks
        // for a second at 6FFFh; with none it calls the chain contradictory (CanOpenLogicalDevices,
        // #552). With one attached device that second one can only be the file's or an object the
        // application declared; RequireReadableChain checks its declaration (#575).
        if (count == 1 && described.HasFurtherLogicalDevices && !_od.ContainsIndex(DeviceTypeObjectOf(1)))
        {
            throw OptionsError($"The device description's object 0x1000 holds 0x{described.Value:X8}, which announces several " +
                $"logical devices, but only logical device 0 ('{setters[0]!.Application.Name}', profile {types[0].ProfileNumber}) " +
                "is attached and neither the file nor an application declares 0x6FFF, the device type of a second one: a reader " +
                "of the chain would find a single device behind FFFFh (CiA 301 v4.2.0 §7.5.2.1).");
        }
    }

    /// <summary>
    /// The device-type objects behind the attached logical devices that a reader of an announced chain
    /// reaches: from <c>67FFh + count·800h</c> up to the first object that does not exist. No
    /// application set their words, so there is no profile to compare, but a reader takes each for a
    /// device's type, so each is declared as a device type is (#553): a read-only UNSIGNED32 at
    /// sub-index 0. Anything else is read as a value that is not four bytes, or could be overwritten
    /// over the network (#575). Nothing is checked when <c>1000h</c> announces no chain, since nothing
    /// is read then.
    /// </summary>
    private void RequireReadableChain(int count)
    {
        if (!_od.TryReadRaw(Co.DeviceType, 0x00, out var raw) || !CanOpenDeviceType.TryParse(raw, out var published)
            || !published.HasFurtherLogicalDevices)
        {
            return;
        }

        for (int device = count; device < CanOpenLogicalDeviceAddressing.MaxLogicalDevices; device++)
        {
            var index = DeviceTypeObjectOf(device);
            if (!_od.ContainsIndex(index)) return;
            if (_od.TryGet(index, 0x00, out var entry) && entry.DataType == OdDataType.Unsigned32
                && entry.Access == OdAccess.ReadOnly)
            {
                continue;
            }

            var declarer = _describedObjects.Contains(index) ? "The device description" : "An application";
            var declared = _od.TryGet(index, 0x00, out var found)
                ? $"as {found.DataType} with access {found.Access}"
                : "without sub-index 0";
            throw OptionsError($"{declarer} declares object 0x{index:X4}, the device type of logical device {device}, " +
                $"{declared}, which no application attaches: 1000h holds 0x{published.Value:X8}, so a reader of the chain " +
                "asks for it, and a device-type object is a read-only UNSIGNED32 at sub-index 0 (CiA 301 v4.2.0 §7.5.2.1).");
        }
    }

    /// <summary>The device-type object of a logical device: <c>67FFh + x·800h</c>, x the logical device
    /// counted from 0 (CiA 301 v4.2.0 §7.5.2.1).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="logicalDevice"/> is outside
    /// <c>0</c> to <c>7</c>; the parameter is <c>logicalDevice</c>.</exception>
    private static ushort DeviceTypeObjectOf(int logicalDevice) => CanOpenLogicalDeviceAddressing.Index(logicalDevice, 0x67FF);

    /// <summary>The device description is the node's identity and wins over the application: it
    /// stays as it is, and the profile number it holds in <paramref name="index"/> must be the one
    /// the application set, or the file describes another device than the one attached. The file
    /// must declare the object as the read-only UNSIGNED32 a device type is (#553). The same rules
    /// check a <c>67FFh</c> that <paramref name="declarer"/>, an application, declared itself where a
    /// described <c>1000h</c> sends a reader to it (<see cref="RequireDescribedTopology"/>).</summary>
    private void RequireDescribedProfile(ushort index, ApplicationSlot setter, CanOpenDeviceType set,
        string declarer = "The device description")
    {
        // The declaration first, then the value: four bytes of an INTEGER32, or a word a master may
        // overwrite, are not a device type (CiA 301 v4.2.0 §7.5.2.1: UNSIGNED32, ro; a chain object has
        // "the very same value definition"), #553.
        if (!_od.TryGet(index, 0x00, out var entry) || entry.DataType != OdDataType.Unsigned32
            || !_od.TryReadRaw(index, 0x00, out var raw) || !CanOpenDeviceType.TryParse(raw, out var described))
        {
            throw OptionsError($"{declarer} declares object 0x{index:X4}, which is not an UNSIGNED32 " +
                $"at sub-index 0: application '{setter.Application.Name}' sets the device type {set}.");
        }
        if (entry.Access != OdAccess.ReadOnly)
        {
            throw OptionsError($"{declarer} declares object 0x{index:X4} with access {entry.Access}, but a " +
                $"device-type object is read-only (CiA 301 v4.2.0 §7.5.2.1): application '{setter.Application.Name}' " +
                $"sets the device type {set}, and a write over the network must not replace it.");
        }
        if (described.ProfileNumber == set.ProfileNumber) return;
        throw OptionsError($"{declarer} declares object 0x{index:X4} holding profile {described.ProfileNumber} " +
            $"(0x{described.ProfileNumber:X4}), but application '{setter.Application.Name}' sets profile " +
            $"{set.ProfileNumber} (0x{set.ProfileNumber:X4}): the object describes another device than the one attached.");
    }
}
