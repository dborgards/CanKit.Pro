using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Pro.CANopen.Safety;

namespace CanKit.Pro.CANopen;

internal sealed partial class CanOpenNode
{
    private readonly ConcurrentDictionary<byte, CanOpenDeviceDescription> _peerDescriptions = new();

    // One SDO channel per peer. A public SDO call takes it for the length of the call without
    // waiting — a busy channel is the "already in flight" refusal callers have always had; a
    // safety transaction (ConfigurePeerSafetyAsync, VerifyPeerSafetyConfigurationAsync) waits for
    // it and holds it for the whole transaction, so a call during the transaction is refused as in
    // flight and nothing reaches the peer between two of its transfers. Not re-entrant — a path
    // holding it calls the ...CoreAsync transfers, never the public ones.
    private readonly ConcurrentDictionary<byte, SemaphoreSlim> _peerSdoChannels = new();

    private SemaphoreSlim PeerSdoChannel(byte serverNodeId)
        => _peerSdoChannels.GetOrAdd(serverNodeId, static _ => new SemaphoreSlim(1, 1));

    /// <summary>Test seam, any thread: whether nothing holds the peer's SDO channel.</summary>
    internal bool PeerSdoChannelIsFreeForTests(byte serverNodeId) => PeerSdoChannel(serverNodeId).CurrentCount == 1;

    /// <summary>Runs <paramref name="transfer"/> holding the peer's SDO channel, taken without
    /// waiting. A busy channel — a transfer in flight, or a safety transaction — fails the call with
    /// the "already in flight" refusal before anything reaches the bus. The transfer gets the
    /// release, which runs when it ends and before its task completes (see
    /// <see cref="NewSdoTransfer"/>): a caller that has seen a call complete finds the channel free
    /// for its next one.</summary>
    private static Task<T> InPeerSdoChannelAsync<T>(byte serverNodeId, SemaphoreSlim channel, Func<Action, Task<T>> transfer)
    {
        if (!channel.Wait(0))
        {
            return Task.FromException<T>(new InvalidOperationException(
                $"An SDO transfer with server 0x{serverNodeId:X2} is already in flight."));
        }
        return StartHoldingChannel(channel, transfer);
    }

    /// <summary>Starts <paramref name="transfer"/> with the channel held; the release it is given
    /// runs once, whether the transfer ends or fails to start.</summary>
    private static Task<T> StartHoldingChannel<T>(SemaphoreSlim channel, Func<Action, Task<T>> transfer)
    {
        int released = 0;
        void Release()
        {
            if (Interlocked.Exchange(ref released, 1) == 0) channel.Release();
        }
        try
        {
            return transfer(Release);
        }
        catch
        {
            Release();
            throw;
        }
    }

    /// <summary>
    /// The completion source an SDO transfer is ended through, and the task handed out for it.
    /// The source has synchronous continuations, so what has to happen the moment the transfer
    /// ends — <paramref name="onEnded"/> releasing the peer's SDO channel, the cancellation
    /// registration being dropped — happens there, before the handed-out task completes. That task
    /// runs its own continuations asynchronously, so no caller's code runs on the thread that ended
    /// the transfer (the actor, most of the time).
    /// </summary>
    private static TaskCompletionSource<byte[]> NewSdoTransfer(Action? onEnded, out Task<byte[]> handedOut)
    {
        var transfer = new TaskCompletionSource<byte[]>();
        var outcome = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = transfer.Task.ContinueWith(static (ended, state) =>
        {
            var (outcome, onEnded) = ((TaskCompletionSource<byte[]>, Action?))state!;
            onEnded?.Invoke();
            if (ended.Status == TaskStatus.RanToCompletion)
            {
                outcome.TrySetResult(ended.Result);
                return;
            }
            try
            {
                ended.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException ex) when (ended.IsCanceled)
            {
                outcome.TrySetCanceled(ex.CancellationToken);
            }
            catch
            {
                outcome.TrySetException(ended.Exception!.InnerExceptions);
            }
        }, (outcome, onEnded), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        handedOut = outcome.Task;
        return transfer;
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
