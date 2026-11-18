using System;

namespace CanKit.Pro.CANopen.Application;

/// <summary>An inclusive range of object indices, as an application claims one for the objects it owns.</summary>
internal readonly record struct CanOpenIndexRange
{
    /// <summary>The range <paramref name="first"/> to <paramref name="last"/>, both included.</summary>
    /// <exception cref="ArgumentException"><paramref name="first"/> is above <paramref name="last"/>.</exception>
    public CanOpenIndexRange(ushort first, ushort last)
    {
        if (first > last)
            throw new ArgumentException($"The range starts at 0x{first:X4} and ends at 0x{last:X4}: it ends before it starts.", nameof(first));
        First = first;
        Last = last;
    }

    /// <summary>The lowest index of the range.</summary>
    public ushort First { get; }

    /// <summary>The highest index of the range.</summary>
    public ushort Last { get; }

    /// <summary>Whether <paramref name="index"/> lies in the range.</summary>
    public bool Contains(ushort index) => index >= First && index <= Last;

    /// <summary>Whether the two ranges have an index in common.</summary>
    public bool Overlaps(CanOpenIndexRange other) => First <= other.Last && other.First <= Last;

    /// <summary>The standardized profile area of a logical device: <c>6000h</c> to <c>67FFh</c> for the
    /// first (CiA 301 v4.2.0 §7.4.1, Table 41), shifted through
    /// <see cref="CanOpenLogicalDeviceAddressing.Index"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="logicalDevice"/> is outside <c>0</c> to <c>7</c>.</exception>
    public static CanOpenIndexRange ForLogicalDevice(int logicalDevice)
        => new(CanOpenLogicalDeviceAddressing.Index(logicalDevice, 0x6000),
            CanOpenLogicalDeviceAddressing.Index(logicalDevice, 0x67FF));

    /// <inheritdoc />
    public override string ToString() => $"0x{First:X4}-0x{Last:X4}";
}
