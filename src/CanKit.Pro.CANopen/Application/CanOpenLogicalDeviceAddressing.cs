using System;

namespace CanKit.Pro.CANopen.Application;

/// <summary>
/// Where a profile written for logical device 0 finds its objects and PDOs in logical device
/// <c>n</c>: indices <c>n·800h</c> higher, each logical device having its own <c>800h</c> objects
/// of the standardized profile area from <c>6000h</c> (CiA 301 v4.2.0 §7.4.1, Table 41), and PDO
/// numbers <c>n·64</c> higher (CiA 301 v4.2.0 §7.2.2.1, Table 3). The one place the seam and the
/// profiles shift by a logical device; the arithmetic itself is <see cref="CanOpenLogicalDevices"/>'s.
/// </summary>
internal static class CanOpenLogicalDeviceAddressing
{
    /// <summary>The number of logical devices a node holds at most, <c>0</c> to <c>7</c>: up to eight
    /// (CiA 301 v4.2.0 §4.1).</summary>
    public const int MaxLogicalDevices = CanOpenLogicalDevices.MaxCount;

    /// <summary>The index <paramref name="indexInDevice0"/> of logical device 0 as logical device
    /// <paramref name="logicalDevice"/> holds it: <c>+ logicalDevice·800h</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="logicalDevice"/> is outside
    /// <c>0</c> to <c>7</c>, or <paramref name="indexInDevice0"/> outside <c>6000h</c> to
    /// <c>67FFh</c>.</exception>
    public static ushort Index(int logicalDevice, ushort indexInDevice0)
    {
        CanOpenLogicalDevices.Validate(logicalDevice);
        if (indexInDevice0 < CanOpenLogicalDevices.FirstArea
            || indexInDevice0 >= CanOpenLogicalDevices.FirstArea + CanOpenLogicalDevices.AreaSize)
        {
            throw new ArgumentOutOfRangeException(nameof(indexInDevice0), indexInDevice0,
                "An index of logical device 0 lies in 6000h to 67FFh.");
        }
        return CanOpenLogicalDevices.ToAbsoluteIndex(logicalDevice, indexInDevice0);
    }

    /// <summary>The PDO number <paramref name="pdoInDevice0"/> of logical device 0 as logical device
    /// <paramref name="logicalDevice"/> uses it: <c>+ logicalDevice·64</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="logicalDevice"/> is outside
    /// <c>0</c> to <c>7</c>, or <paramref name="pdoInDevice0"/> outside <c>1</c> to <c>64</c>.</exception>
    public static int PdoNumber(int logicalDevice, int pdoInDevice0)
    {
        CanOpenLogicalDevices.Validate(logicalDevice);
        if (pdoInDevice0 is < 1 or > CanOpenLogicalDevices.PdosPerLogicalDevice)
        {
            throw new ArgumentOutOfRangeException(nameof(pdoInDevice0), pdoInDevice0,
                "A PDO number of logical device 0 lies in 1 to 64.");
        }
        return pdoInDevice0 + CanOpenLogicalDevices.PdoNumberOffset(logicalDevice);
    }
}
