using System;
using System.Collections.Generic;

namespace CanKit.Pro.CANopen.Application;

/// <summary>The profiles a tool knows, by profile number, and the way from the logical devices of a
/// peer to typed access to each. It holds what it was given: there is no global registry and no
/// assembly scan, and it performs no bus access. A device whose profile it does not hold is not an
/// error, it has no access.</summary>
internal sealed class CanOpenProfileCatalog
{
    private readonly List<ICanOpenProfile> _profiles = new();
    private readonly Dictionary<ushort, ICanOpenProfile> _byNumber = new();

    /// <summary>A catalogue of <paramref name="profiles"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="profiles"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An element is <see langword="null"/>, a descriptor has profile
    /// number <c>0</c>, or two descriptors have the same profile number.</exception>
    public CanOpenProfileCatalog(IEnumerable<ICanOpenProfile> profiles)
    {
        if (profiles is null)
            throw new ArgumentNullException(nameof(profiles));

        foreach (var profile in profiles)
        {
            if (profile is null)
                throw new ArgumentException("A profile descriptor is null.", nameof(profiles));
            if (profile.ProfileNumber == 0)
                throw new ArgumentException(
                    $"The descriptor '{profile.Name}' has profile number 0, which names no profile.", nameof(profiles));
            if (_byNumber.TryGetValue(profile.ProfileNumber, out var first))
                throw new ArgumentException(
                    $"Profile number {profile.ProfileNumber} is described twice: by '{first.Name}' and by '{profile.Name}'.",
                    nameof(profiles));

            _byNumber.Add(profile.ProfileNumber, profile);
            _profiles.Add(profile);
        }
    }

    /// <summary>A catalogue of <paramref name="profiles"/>, see
    /// <see cref="CanOpenProfileCatalog(IEnumerable{ICanOpenProfile})"/>.</summary>
    public CanOpenProfileCatalog(params ICanOpenProfile[] profiles)
        : this((IEnumerable<ICanOpenProfile>)profiles)
    {
    }

    /// <summary>The descriptors, in the order they were given.</summary>
    public IReadOnlyCollection<ICanOpenProfile> Profiles => _profiles;

    /// <summary>The descriptor of a profile number.</summary>
    /// <param name="profileNumber">The profile number.</param>
    /// <param name="profile">The descriptor, or <see langword="null"/> when this returns
    /// <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the catalogue holds a descriptor for the number.</returns>
    public bool TryGetProfile(ushort profileNumber, out ICanOpenProfile? profile)
    {
        if (_byNumber.TryGetValue(profileNumber, out var found))
        {
            profile = found;
            return true;
        }

        profile = null;
        return false;
    }

    /// <summary>The peer access for one logical device. Performs no bus access.</summary>
    /// <param name="node">The node that reads the peer.</param>
    /// <param name="peerNodeId">The node-id of the peer, <c>1</c> to <c>127</c>.</param>
    /// <param name="logicalDevice">The logical device, with its device-type word.</param>
    /// <returns>The access, or <see langword="null"/> when the profile number of the device is <c>0</c> or
    /// not in the catalogue.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="node"/> or
    /// <paramref name="logicalDevice"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="peerNodeId"/> is outside <c>1</c> to
    /// <c>127</c>.</exception>
    public ICanOpenProfilePeer? CreatePeer(ICanOpenNode node, byte peerNodeId, CanOpenLogicalDevice logicalDevice)
    {
        if (node is null)
            throw new ArgumentNullException(nameof(node));
        CanOpenCobId.ValidateNodeId(peerNodeId);
        if (logicalDevice is null)
            throw new ArgumentNullException(nameof(logicalDevice));

        return CreatePeerCore(node, peerNodeId, logicalDevice);
    }

    /// <summary>The peer access for every logical device a read found. Performs no bus access.</summary>
    /// <param name="node">The node that reads the peer.</param>
    /// <param name="peerNodeId">The node-id of the peer, <c>1</c> to <c>127</c>.</param>
    /// <param name="result">The logical devices of the peer, see <see cref="CanOpenLogicalDevices.ReadAsync"/>.</param>
    /// <returns>One element per device of <paramref name="result"/>, in its order; <see langword="null"/>
    /// where the profile is unknown.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="node"/> or <paramref name="result"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="peerNodeId"/> is outside <c>1</c> to
    /// <c>127</c>.</exception>
    public IReadOnlyList<ICanOpenProfilePeer?> CreatePeers(ICanOpenNode node, byte peerNodeId,
        CanOpenLogicalDeviceReadResult result)
    {
        if (node is null)
            throw new ArgumentNullException(nameof(node));
        CanOpenCobId.ValidateNodeId(peerNodeId);
        if (result is null)
            throw new ArgumentNullException(nameof(result));

        var peers = new ICanOpenProfilePeer?[result.Devices.Count];
        for (var i = 0; i < peers.Length; i++)
            peers[i] = CreatePeerCore(node, peerNodeId, result.Devices[i]);
        return peers;
    }

    private ICanOpenProfilePeer? CreatePeerCore(ICanOpenNode node, byte peerNodeId, CanOpenLogicalDevice logicalDevice)
        => _byNumber.TryGetValue(logicalDevice.DeviceType.ProfileNumber, out var profile)
            ? profile.CreatePeer(node, peerNodeId, logicalDevice)
            : null;
}
