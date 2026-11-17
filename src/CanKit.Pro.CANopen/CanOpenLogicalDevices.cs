using System;

namespace CanKit.Pro.CANopen;

/// <summary>
/// One logical device of a CANopen node: its position among the node's up to eight logical
/// devices and the device-type word that describes it.
/// </summary>
public sealed class CanOpenLogicalDevice
{
    /// <summary>Creates the description of one logical device.</summary>
    /// <param name="index">The position of the logical device, <c>0</c> to <c>7</c>.</param>
    /// <param name="deviceType">The device-type word of this logical device.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside
    /// <c>0</c> to <c>7</c>.</exception>
    public CanOpenLogicalDevice(int index, CanOpenDeviceType deviceType)
    {
        // The static helper validates the index, so the range is defined in one place.
        ObjectAreaStart = CanOpenLogicalDevices.ObjectAreaStart(index);
        Index = index;
        DeviceType = deviceType;
    }

    /// <summary>The position of this logical device, <c>0</c> to <c>7</c>.</summary>
    public int Index { get; }

    /// <summary>The device-type word of this logical device.</summary>
    public CanOpenDeviceType DeviceType { get; }

    /// <summary>The first object index of this logical device's profile area, see
    /// <see cref="CanOpenLogicalDevices.ObjectAreaStart(int)"/>.</summary>
    public ushort ObjectAreaStart { get; }

    /// <summary>The number to add to a profile's PDO numbers for this logical device, see
    /// <see cref="CanOpenLogicalDevices.PdoNumberOffset(int)"/>.</summary>
    public int PdoNumberOffset => CanOpenLogicalDevices.PdoNumberOffset(Index);
}

/// <summary>
/// The arithmetic that places a logical device in the object dictionary. A node holds up to
/// <see cref="MaxCount"/> logical devices. Logical device <c>n</c> owns the <c>800h</c> objects
/// from <c>6000h + n·800h</c>, so a profile that defines an object at <c>6xxxh</c> finds it
/// <c>n·800h</c> higher, and its PDO numbers are <c>n·64</c> higher.
/// </summary>
public static class CanOpenLogicalDevices
{
    /// <summary>The number of logical devices a node can hold.</summary>
    public const int MaxCount = 8;

    private const ushort FirstArea = 0x6000;
    private const int AreaSize = 0x800;
    private const int PdosPerLogicalDevice = 64;

    /// <summary>The first object index of a logical device, <c>6000h + index·800h</c>.</summary>
    /// <param name="index">The logical device, <c>0</c> to <c>7</c>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside
    /// <c>0</c> to <c>7</c>.</exception>
    public static ushort ObjectAreaStart(int index)
    {
        Validate(index);
        return (ushort)(FirstArea + (index * AreaSize));
    }

    /// <summary>The index of the object that holds a logical device's device type,
    /// <c>67FFh + index·800h</c>.</summary>
    /// <param name="index">The logical device, <c>0</c> to <c>7</c>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside
    /// <c>0</c> to <c>7</c>.</exception>
    public static ushort DeviceTypeIndex(int index)
    {
        Validate(index);
        return (ushort)(FirstArea + AreaSize - 1 + (index * AreaSize));
    }

    /// <summary>The offset of a logical device's PDO numbers, <c>index·64</c>.</summary>
    /// <param name="index">The logical device, <c>0</c> to <c>7</c>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside
    /// <c>0</c> to <c>7</c>.</exception>
    public static int PdoNumberOffset(int index)
    {
        Validate(index);
        return index * PdosPerLogicalDevice;
    }

    /// <summary>Moves an object index that a profile defines in <c>6000h</c> to <c>67FFh</c> to
    /// where the given logical device holds it.</summary>
    /// <param name="index">The logical device, <c>0</c> to <c>7</c>.</param>
    /// <param name="profileIndex">The object index as the profile defines it.</param>
    /// <returns><paramref name="profileIndex"/> plus <c>index·800h</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside
    /// <c>0</c> to <c>7</c>, or <paramref name="profileIndex"/> is outside <c>6000h</c> to
    /// <c>67FFh</c>.</exception>
    public static ushort ToAbsoluteIndex(int index, ushort profileIndex)
    {
        Validate(index);
        if (profileIndex < FirstArea || profileIndex >= FirstArea + AreaSize)
        {
            throw new ArgumentOutOfRangeException(nameof(profileIndex), profileIndex,
                "A profile object index lies in 6000h to 67FFh.");
        }

        return (ushort)(profileIndex + (index * AreaSize));
    }

    private static void Validate(int index)
    {
        if (index < 0 || index >= MaxCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index,
                "A node has logical devices 0 to 7.");
        }
    }
}
