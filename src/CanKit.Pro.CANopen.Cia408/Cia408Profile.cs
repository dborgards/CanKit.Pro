using System;
using System.Globalization;
using CanKit.Pro.CANopen.Application;

namespace CanKit.Pro.CANopen.Cia408;

/// <summary>The descriptor of the CiA 408 device profile: it names the profile the way the
/// device-type word of object <c>1000h</c> does (CiA 301 v4.2.0 §7.5.2.1) and makes the tool side's
/// access to a CiA 408 logical device of a peer (<see cref="Cia408Peer"/>). The device side is
/// <see cref="Cia408Device"/>.</summary>
internal sealed class Cia408Profile : ICanOpenProfile
{
    /// <summary>The device profile number <c>408</c> that the device-type word of object
    /// <c>1000h</c> carries in its low 16 bits (CiA 408 v2.2.0 §8.6.2, Figure 3).</summary>
    internal const ushort Number = 408;

    /// <inheritdoc />
    public ushort ProfileNumber => Number;

    /// <inheritdoc />
    public string Name => "CiA 408";

    /// <summary>Makes a <see cref="Cia408Peer"/> for <paramref name="logicalDevice"/> of the peer
    /// <paramref name="peerNodeId"/>. Performs no bus access. A device type with the reserved
    /// resolution code 00b is accepted (see <see cref="Cia408Peer.Resolution"/>): the tool reads what
    /// the device has rather than refusing to look.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="node"/> or
    /// <paramref name="logicalDevice"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="peerNodeId"/> is outside 1 to
    /// 127.</exception>
    /// <exception cref="ArgumentException">The device type of <paramref name="logicalDevice"/> names
    /// another profile than 408 (CiA 408 v2.2.0 §8.6.2).</exception>
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

        return new Cia408Peer(this, node, peerNodeId, logicalDevice);
    }
}
