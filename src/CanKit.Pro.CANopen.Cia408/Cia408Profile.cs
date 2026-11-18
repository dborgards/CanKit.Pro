using System;
using CanKit.Pro.CANopen.Application;

namespace CanKit.Pro.CANopen.Cia408;

/// <summary>The descriptor of the CiA 408 device profile: it names the profile the way the
/// device-type word of object <c>1000h</c> does (CiA 301 v4.2.0 §7.5.2.1). The device side and
/// the tool side arrive with their own types.</summary>
internal sealed class Cia408Profile : ICanOpenProfile
{
    /// <summary>The device profile number <c>408</c> that the device-type word of object
    /// <c>1000h</c> carries in its low 16 bits (CiA 408 v2.2.0 §8.6.2, Figure 3).</summary>
    internal const ushort Number = 408;

    /// <inheritdoc />
    public ushort ProfileNumber => Number;

    /// <inheritdoc />
    public string Name => "CiA 408";

    /// <inheritdoc />
    public ICanOpenProfilePeer CreatePeer(ICanOpenNode node, byte peerNodeId, CanOpenLogicalDevice logicalDevice)
        => throw new NotSupportedException("The tool side of CiA 408 is not built yet.");
}
