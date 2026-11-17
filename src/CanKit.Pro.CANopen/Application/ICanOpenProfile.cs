using System;

namespace CanKit.Pro.CANopen.Application;

/// <summary>The tool side of a device profile: what a profile gives a node that reads a peer. It
/// names the profile and makes the typed access to one logical device of one peer. It does not
/// describe the node's own side, which is <see cref="ICanOpenApplication"/>.</summary>
internal interface ICanOpenProfile
{
    /// <summary>The device profile number the descriptor answers to, for example <c>401</c> or
    /// <c>408</c>. Never <c>0</c>: a device-type word without a profile number names no profile.</summary>
    ushort ProfileNumber { get; }

    /// <summary>The profile's name for messages, for example <c>CiA 401</c>.</summary>
    string Name { get; }

    /// <summary>Makes the access to one logical device of a peer. Performs no bus access: whatever the
    /// peer reads, it reads through <c>ICanOpenNode.SdoUploadAsync</c>, so it stays behind the
    /// SDO client's peer-description gate.</summary>
    /// <param name="node">The node that reads the peer.</param>
    /// <param name="peerNodeId">The node-id of the peer, <c>1</c> to <c>127</c>.</param>
    /// <param name="logicalDevice">The logical device of the peer, with the device-type word it
    /// reported.</param>
    ICanOpenProfilePeer CreatePeer(ICanOpenNode node, byte peerNodeId, CanOpenLogicalDevice logicalDevice);
}

/// <summary>The typed access to one logical device of one peer, made by its
/// <see cref="ICanOpenProfile"/>.</summary>
internal interface ICanOpenProfilePeer
{
    /// <summary>The profile that made this access.</summary>
    ICanOpenProfile Profile { get; }

    /// <summary>The node-id of the peer.</summary>
    byte PeerNodeId { get; }

    /// <summary>The logical device of the peer this access reads.</summary>
    CanOpenLogicalDevice LogicalDevice { get; }
}
