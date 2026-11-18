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
    /// one the application set.
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

        if (_describedObjects.Contains(Co.DeviceType))
        {
            RequireDescribedProfile(Co.DeviceType, setters[0]!, types[0]);
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
    }

    /// <summary>The device-type object of a logical device: <c>67FFh</c> of logical device 0, shifted
    /// (§7.5.2.1).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="logicalDevice"/> is outside
    /// <c>0</c> to <c>7</c>; the parameter is <c>logicalDevice</c>.</exception>
    private static ushort DeviceTypeObjectOf(int logicalDevice) => CanOpenLogicalDeviceAddressing.Index(logicalDevice, 0x67FF);

    /// <summary>The device description is the node's identity and wins over the application: it
    /// stays as it is, and the profile number it holds in <paramref name="index"/> must be the one
    /// the application set, or the file describes another device than the one attached.</summary>
    private void RequireDescribedProfile(ushort index, ApplicationSlot setter, CanOpenDeviceType set)
    {
        if (!_od.TryReadRaw(index, 0x00, out var raw) || !CanOpenDeviceType.TryParse(raw, out var described))
        {
            throw OptionsError($"The device description declares object 0x{index:X4}, which is not an UNSIGNED32 " +
                $"at sub-index 0: application '{setter.Application.Name}' sets the device type {set}.");
        }
        if (described.ProfileNumber == set.ProfileNumber) return;
        throw OptionsError($"The device description's object 0x{index:X4} holds profile {described.ProfileNumber} " +
            $"(0x{described.ProfileNumber:X4}), but application '{setter.Application.Name}' sets profile " +
            $"{set.ProfileNumber} (0x{set.ProfileNumber:X4}): the file describes another device than the one attached.");
    }
}
