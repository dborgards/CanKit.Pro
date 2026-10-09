using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Pro.CANopen.Safety;

namespace CanKit.Pro.CANopen;

internal sealed partial class CanOpenNode
{
    private readonly ConcurrentDictionary<byte, CanOpenDeviceDescription> _peerDescriptions = new();

    // One SDO channel per peer. A public SDO call holds it for the length of the call, a safety
    // transaction (ConfigurePeerSafetyAsync, VerifyPeerSafetyConfigurationAsync) for the whole
    // transaction: nothing else of this node reaches that peer between two of its transfers. Not
    // re-entrant — a path holding it calls the ...CoreAsync transfers, never the public ones.
    private readonly ConcurrentDictionary<byte, SemaphoreSlim> _peerSdoChannels = new();

    private SemaphoreSlim PeerSdoChannel(byte serverNodeId)
        => _peerSdoChannels.GetOrAdd(serverNodeId, static _ => new SemaphoreSlim(1, 1));

    /// <summary>Runs <paramref name="transfer"/> holding the peer's SDO channel; the wait honours
    /// <paramref name="cancellationToken"/>. With the channel free the transfer's own task is
    /// returned, so the call completes exactly when the transfer does (a disposal that fails it
    /// is visible at once), and a synchronous refusal is thrown synchronously as before.</summary>
    private Task<T> InPeerSdoChannelAsync<T>(byte serverNodeId, CancellationToken cancellationToken, Func<Task<T>> transfer)
    {
        var channel = PeerSdoChannel(serverNodeId);
        var wait = channel.WaitAsync(cancellationToken);
        return wait.Status == TaskStatus.RanToCompletion
            ? RunHoldingChannel(channel, transfer)
            : WaitThenRunAsync(channel, wait, transfer);
    }

    private static async Task<T> WaitThenRunAsync<T>(SemaphoreSlim channel, Task wait, Func<Task<T>> transfer)
    {
        await wait.ConfigureAwait(false);
        return await RunHoldingChannel(channel, transfer).ConfigureAwait(false);
    }

    /// <summary>Starts <paramref name="transfer"/> with the channel held and releases it when the
    /// transfer has ended, however it ended.</summary>
    private static Task<T> RunHoldingChannel<T>(SemaphoreSlim channel, Func<Task<T>> transfer)
    {
        Task<T> task;
        try
        {
            task = transfer();
        }
        catch
        {
            channel.Release();
            throw;
        }
        _ = task.ContinueWith(static (_, state) => ((SemaphoreSlim)state!).Release(), channel,
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    /// <summary>An upload for the observers (ObserveForeignPdoAsync, ObserveForeignSrdoAsync): it
    /// does not wait for the peer's SDO channel. A busy channel — another SDO call or a safety
    /// transaction with that peer — is reported as the "already in flight" refusal, which the
    /// observers take as a live read that is unavailable and fall back to the file (FR-CO-030).</summary>
    private Task<byte[]> SdoUploadForObserverAsync(byte serverNodeId, ushort index, byte subindex, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        CanOpenCobId.ValidateNodeId(serverNodeId);
        EnsurePeerSdoAccess(serverNodeId, index, subindex);
        var channel = PeerSdoChannel(serverNodeId);
        if (!channel.Wait(0))
        {
            return Task.FromException<byte[]>(new InvalidOperationException(
                $"An SDO transfer with server 0x{serverNodeId:X2} is already in flight."));
        }
        return RunHoldingChannel(channel, () => SdoUploadCoreAsync(serverNodeId, index, subindex, Sdo.SdoTransferMode.Auto, cancellationToken));
    }

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
