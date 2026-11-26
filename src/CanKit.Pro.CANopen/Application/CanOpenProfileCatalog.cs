using System;
using System.Collections.Generic;

namespace CanKit.Pro.CANopen.Application;

/// <summary>The profiles a tool knows, by profile number, and the way from the logical devices of a
/// peer to typed access to each. It holds what it was given: there is no global registry and no
/// assembly scan, and it performs no bus access. A device whose profile it does not hold is not an
/// error, it has no access.</summary>
internal sealed class CanOpenProfileCatalog
{
    private readonly Dictionary<ushort, ICanOpenProfile> _byNumber = new();

    /// <summary>A catalogue of <paramref name="profiles"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="profiles"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An element is <see langword="null"/>, a descriptor has profile
    /// number <c>0</c>, or two descriptors have the same profile number.</exception>
    public CanOpenProfileCatalog(IEnumerable<ICanOpenProfile> profiles)
    {
        if (profiles is null)
            throw new ArgumentNullException(nameof(profiles));

        var ordered = new List<ICanOpenProfile>();
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
            ordered.Add(profile);
        }

        // A read-only wrapper around the list would expose the list through ICollection.SyncRoot.
        Profiles = [.. ordered];
    }

    /// <summary>A catalogue of <paramref name="profiles"/>, see
    /// <see cref="CanOpenProfileCatalog(IEnumerable{ICanOpenProfile})"/>.</summary>
    public CanOpenProfileCatalog(params ICanOpenProfile[] profiles)
        : this((IEnumerable<ICanOpenProfile>)profiles)
    {
    }

    /// <summary>A read-only snapshot of the descriptors in registration order.</summary>
    public IReadOnlyCollection<ICanOpenProfile> Profiles { get; }

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
    /// <exception cref="InvalidOperationException">A registered descriptor returned a null peer.</exception>
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
    /// <remarks>A read that ended in another way than <see cref="CanOpenLogicalDeviceReadEnd.Complete"/>
    /// is mapped as far as it got, except where the list cannot be trusted to name the profile of the
    /// first logical device: <c>67FFh</c> names another profile than <c>1000h</c> (#552), so the first
    /// device in the list and <see cref="CanOpenLogicalDeviceReadResult.DeviceType"/> disagree and
    /// either word could be the wrong one. Selecting a profile from that list would pick one of them
    /// silently, so the result is refused; <see cref="CreatePeer"/> maps a device the caller has
    /// chosen. A peer that announces several logical devices and ends the chain before the second also
    /// ends as <see cref="CanOpenLogicalDeviceReadEnd.Inconsistent"/>, but there <c>1000h</c> and the
    /// device read agree on the first device's profile: that one device is mapped, as the devices read
    /// before a <see cref="CanOpenLogicalDeviceReadEnd.Failed"/> read are. The rule looks at the words,
    /// not at <see cref="CanOpenLogicalDeviceReadResult.End"/>, and uses public members only, so a
    /// caller can apply it to its own catalogue.</remarks>
    /// <param name="node">The node that reads the peer.</param>
    /// <param name="peerNodeId">The node-id of the peer, <c>1</c> to <c>127</c>.</param>
    /// <param name="result">The logical devices of the peer, see <see cref="CanOpenLogicalDevices.ReadAsync"/>.</param>
    /// <returns>One element per device of <paramref name="result"/>, in its order; <see langword="null"/>
    /// where the profile is unknown.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="node"/> or <paramref name="result"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="peerNodeId"/> is outside <c>1</c> to
    /// <c>127</c>.</exception>
    /// <exception cref="ArgumentException">The first device of <paramref name="result"/> has another profile
    /// number than its <c>1000h</c>; the message carries its
    /// <see cref="CanOpenLogicalDeviceReadResult.Failure"/>.</exception>
    /// <exception cref="InvalidOperationException">A registered descriptor returned a null peer.</exception>
    public IReadOnlyList<ICanOpenProfilePeer?> CreatePeers(ICanOpenNode node, byte peerNodeId,
        CanOpenLogicalDeviceReadResult result)
    {
        if (node is null)
            throw new ArgumentNullException(nameof(node));
        CanOpenCobId.ValidateNodeId(peerNodeId);
        if (result is null)
            throw new ArgumentNullException(nameof(result));
        if (result.Devices[0].DeviceType.ProfileNumber != result.DeviceType.ProfileNumber)
        {
            throw new ArgumentException(
                $"The first logical device of node {peerNodeId} has profile {result.Devices[0].DeviceType.ProfileNumber}, but " +
                $"its 1000h names profile {result.DeviceType.ProfileNumber}, so no profile is selected from the list: " +
                $"{result.Failure?.Message}", nameof(result));
        }

        var peers = new ICanOpenProfilePeer?[result.Devices.Count];
        for (var i = 0; i < peers.Length; i++)
            peers[i] = CreatePeerCore(node, peerNodeId, result.Devices[i]);
        return peers;
    }

    private ICanOpenProfilePeer? CreatePeerCore(ICanOpenNode node, byte peerNodeId, CanOpenLogicalDevice logicalDevice)
    {
        var number = logicalDevice.DeviceType.ProfileNumber;
        if (!_byNumber.TryGetValue(number, out var profile)) return null;

        return profile.CreatePeer(node, peerNodeId, logicalDevice) ?? throw new InvalidOperationException(
            $"Descriptor '{profile.Name}' for profile {number} returned a null peer for node {peerNodeId}, logical device {logicalDevice.Index}.");
    }
}
