using System;
using System.Collections.Concurrent;
using CanKit.Pro.CANopen.Safety;

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
        if (description.NodeId is { } commissioned && commissioned != nodeId)
        {
            throw new ArgumentException(
                $"The DCF is commissioned for node 0x{commissioned:X2} and cannot be bound for node 0x{nodeId:X2}.",
                nameof(description));
        }

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
    /// <paramref name="serverNodeId"/>, the pair must be one that description declares, or one
    /// it implies as an SRDO record (<see cref="IsImpliedSrdoRecordEntry"/>). With none bound,
    /// only <see cref="PeerSdoAccessException.IsAllowedWithoutPeerDescription"/> passes.
    /// </summary>
    private void EnsurePeerSdoAccess(byte serverNodeId, ushort index, byte subindex)
    {
        if (_peerDescriptions.TryGetValue(serverNodeId, out var description))
        {
            if (description.Contains(index, subindex) || IsImpliedSrdoRecordEntry(description, index, subindex)) return;
            throw PeerSdoAccessException.NotInDescription(serverNodeId, index, subindex);
        }

        if (PeerSdoAccessException.IsAllowedWithoutPeerDescription(index, subindex)) return;
        throw PeerSdoAccessException.NoDescription(serverNodeId, index, subindex);
    }

    /// <summary>
    /// CiA DSP 304 §8.4.2.2: 13FFh:00 is the number of SRDOs, so a file whose highest SRDO record
    /// is N implies the records of SRDOs 1..N, declared or not — and a device loading that file
    /// provides an undeclared one at its defaults, deleted. The gate lets through what the device
    /// provides there: sub-indices 00h–06h of 1301h–(1300h + N) and 00h–10h of
    /// 1381h–(1380h + N). Nothing else the file leaves out, and nothing for a file without an
    /// SRDO record (FR-CO-029).
    /// </summary>
    private static bool IsImpliedSrdoRecordEntry(CanOpenDeviceDescription description, ushort index, byte subindex)
    {
        if (SrdoRecords.SrdoNumberOf(index) is not { } n || n > DescribedSrdoCount(description)) return false;
        return SrdoRecords.IsCommunicationRecord(index) ? subindex <= 0x06 : subindex <= SrdoRecords.MappingSubindices;
    }
}
