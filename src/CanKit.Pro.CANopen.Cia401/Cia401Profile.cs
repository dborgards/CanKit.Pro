using System;
using CanKit.Pro.CANopen.Application;

namespace CanKit.Pro.CANopen.Cia401;

/// <summary>The descriptor of the CiA 401 device profile: it names the profile the way the
/// device-type word of object <c>1000h</c> does (CiA 301 §7.5.2.1). The device side and the tool
/// side arrive with their own types.</summary>
internal sealed class Cia401Profile : ICanOpenProfile
{
    /// <summary>The device profile number <c>401</c> that the device-type word of object
    /// <c>1000h</c> carries in its low 16 bits (CiA 401-1 v3.1.0 §6.2.1).</summary>
    internal const ushort Number = 401;

    /// <inheritdoc />
    public ushort ProfileNumber => Number;

    /// <inheritdoc />
    public string Name => "CiA 401";

    /// <inheritdoc />
    public ICanOpenProfilePeer CreatePeer(ICanOpenNode node, byte peerNodeId, CanOpenLogicalDevice logicalDevice)
        => throw new NotSupportedException("The tool side of CiA 401 is not built yet.");
}
