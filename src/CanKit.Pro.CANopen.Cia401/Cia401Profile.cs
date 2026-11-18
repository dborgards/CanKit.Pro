using System;
using System.Globalization;
using CanKit.Pro.CANopen.Application;

namespace CanKit.Pro.CANopen.Cia401;

/// <summary>The descriptor of the CiA 401 device profile: it names the profile the way the
/// device-type word of object <c>1000h</c> does (CiA 301 §7.5.2.1) and makes the tool side's access
/// to a CiA 401 logical device of a peer (<see cref="Cia401Peer"/>). The device side is
/// <see cref="Cia401Device"/>.</summary>
internal sealed class Cia401Profile : ICanOpenProfile
{
    /// <summary>The device profile number <c>401</c> that the device-type word of object
    /// <c>1000h</c> carries in its low 16 bits (CiA 401-1 v3.1.0 §6.2.1).</summary>
    internal const ushort Number = 401;

    /// <inheritdoc />
    public ushort ProfileNumber => Number;

    /// <inheritdoc />
    public string Name => "CiA 401";

    /// <summary>Makes a <see cref="Cia401Peer"/> for <paramref name="logicalDevice"/> of the peer
    /// <paramref name="peerNodeId"/>. Performs no bus access.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="node"/> or
    /// <paramref name="logicalDevice"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="peerNodeId"/> is outside 1 to
    /// 127.</exception>
    /// <exception cref="ArgumentException">The device type of <paramref name="logicalDevice"/> names
    /// another profile than 401 (CiA 401-1 §6.2.1).</exception>
    public ICanOpenProfilePeer CreatePeer(ICanOpenNode node, byte peerNodeId, CanOpenLogicalDevice logicalDevice)
    {
        if (node is null) throw new ArgumentNullException(nameof(node));
        CanOpenCobId.ValidateNodeId(peerNodeId);
        if (logicalDevice is null) throw new ArgumentNullException(nameof(logicalDevice));
        if (logicalDevice.DeviceType.ProfileNumber != Number)
        {
            throw new ArgumentException(string.Format(CultureInfo.InvariantCulture,
                "Logical device {0} has the device type {1}, not one of profile {2}.",
                logicalDevice.Index, logicalDevice.DeviceType, Number), nameof(logicalDevice));
        }

        return new Cia401Peer(this, node, peerNodeId, logicalDevice);
    }
}
