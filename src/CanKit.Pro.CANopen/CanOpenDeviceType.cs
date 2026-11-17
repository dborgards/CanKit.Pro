using System;
using System.Globalization;

namespace CanKit.Pro.CANopen;

/// <summary>
/// The device-type word of a CANopen node (object <c>1000h</c>), split into its two halves: the
/// profile number in the low 16 bits and the additional information in the high 16 bits.
/// </summary>
/// <param name="Value">The 32-bit device-type word as read from <c>1000h:00</c>.</param>
public readonly record struct CanOpenDeviceType(uint Value)
{
    /// <summary>The additional-information value <c>FFFFh</c>: the node holds further logical
    /// devices beyond the one this word describes.</summary>
    public const ushort FurtherLogicalDevices = 0xFFFF;

    /// <summary>The device profile number, bits 0 to 15 of <see cref="Value"/>.</summary>
    public ushort ProfileNumber => (ushort)(Value & 0xFFFF);

    /// <summary>The additional information, bits 16 to 31 of <see cref="Value"/>. Its meaning is
    /// defined by the profile.</summary>
    public ushort AdditionalInformation => (ushort)(Value >> 16);

    /// <summary>Whether the node names a standardized device profile, that is, whether
    /// <see cref="ProfileNumber"/> is not zero.</summary>
    public bool FollowsStandardizedProfile => ProfileNumber != 0;

    /// <summary>Whether <see cref="AdditionalInformation"/> is <see cref="FurtherLogicalDevices"/>
    /// (<c>FFFFh</c>), which marks a node with more than one logical device.</summary>
    public bool HasFurtherLogicalDevices => AdditionalInformation == FurtherLogicalDevices;

    /// <summary>Reads the device-type word from the four bytes an SDO upload of <c>1000h:00</c>
    /// returns, least significant byte first.</summary>
    /// <param name="littleEndian">The uploaded bytes.</param>
    /// <param name="deviceType">The parsed word, or the default value when this returns
    /// <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when exactly four bytes were given.</returns>
    public static bool TryParse(ReadOnlySpan<byte> littleEndian, out CanOpenDeviceType deviceType)
    {
        if (littleEndian.Length != 4)
        {
            deviceType = default;
            return false;
        }

        deviceType = new CanOpenDeviceType(
            littleEndian[0]
            | ((uint)littleEndian[1] << 8)
            | ((uint)littleEndian[2] << 16)
            | ((uint)littleEndian[3] << 24));
        return true;
    }

    /// <summary>The word in hexadecimal with the profile number in decimal, for example
    /// <c>0x000F0191 (profile 401)</c>, or <c>(no profile)</c> when the profile number is zero.</summary>
    public override string ToString()
    {
        var hex = "0x" + Value.ToString("X8", CultureInfo.InvariantCulture);
        return FollowsStandardizedProfile
            ? hex + " (profile " + ProfileNumber.ToString(CultureInfo.InvariantCulture) + ")"
            : hex + " (no profile)";
    }
}
