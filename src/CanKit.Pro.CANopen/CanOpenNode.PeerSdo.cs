using System;
using System.Collections.Concurrent;

namespace CanKit.Pro.CANopen;

internal sealed partial class CanOpenNode
{
    private readonly ConcurrentDictionary<byte, CanOpenDeviceDescription> _peerDescriptions = new();

    /// <inheritdoc />
    public void BindPeerDeviceDescription(byte nodeId, CanOpenDeviceDescription description)
    {
        ThrowIfDisposed();
        if (description is null) throw new ArgumentNullException(nameof(description));
        CanOpenCobId.ValidateNodeId(nodeId);
        _peerDescriptions[nodeId] = description;
    }

    /// <inheritdoc />
    public CanOpenDeviceDescription? GetPeerDeviceDescription(byte nodeId)
    {
        ThrowIfDisposed();
        CanOpenCobId.ValidateNodeId(nodeId);
        return _peerDescriptions.TryGetValue(nodeId, out var description) ? description : null;
    }

    /// <inheritdoc />
    public void UnbindPeerDeviceDescription(byte nodeId)
    {
        ThrowIfDisposed();
        CanOpenCobId.ValidateNodeId(nodeId);
        _peerDescriptions.TryRemove(nodeId, out _);
    }

    /// <summary>
    /// Refuses a client SDO the peer description does not allow, before the transfer is posted
    /// to the actor and therefore before any frame is sent. With a description bound for
    /// <paramref name="serverNodeId"/>, the pair must be one that description declares. With
    /// none bound, only <see cref="PeerSdoAccessException.IsAllowedWithoutPeerDescription"/>
    /// passes.
    /// </summary>
    private void EnsurePeerSdoAccess(byte serverNodeId, ushort index, byte subindex)
    {
        if (_peerDescriptions.TryGetValue(serverNodeId, out var description))
        {
            if (description.Contains(index, subindex)) return;
            throw PeerSdoAccessException.NotInDescription(serverNodeId, index, subindex);
        }

        if (PeerSdoAccessException.IsAllowedWithoutPeerDescription(index, subindex)) return;
        throw PeerSdoAccessException.NoDescription(serverNodeId, index, subindex);
    }
}
