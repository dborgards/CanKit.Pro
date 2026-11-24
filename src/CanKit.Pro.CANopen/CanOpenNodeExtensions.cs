using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Sdo;

namespace CanKit.Pro.CANopen;

/// <summary>Additional operations for <see cref="ICanOpenNode"/> without widening its interface.</summary>
/// <remarks>
/// <see cref="ICanOpenNode"/> itself stays <see cref="IDisposable"/>: adding
/// <see cref="IAsyncDisposable"/> to a published interface would break everyone who implements it.
/// The nodes this library creates implement it, and <see cref="DisposeAsync(ICanOpenNode)"/> reaches
/// it without a cast. It cannot be used with <c>await using</c> on an <see cref="ICanOpenNode"/>
/// (that needs the interface); cast to <see cref="IAsyncDisposable"/> for that.
/// </remarks>
public static class CanOpenNodeExtensions
{
    /// <summary>Downloads a CiA 302-3 §3.3 concise-DCF stream to one peer, in record order.</summary>
    /// <remarks>
    /// Copies and validates the complete little-endian stream (U32 count, then U16 index,
    /// U8 sub-index, U32 byte length and data) before sending. Repeated pairs and empty values
    /// retain their order; a zero entry count is a no-op. Each value must fit MaxSdoTransferBytes.
    /// Waits for and holds the peer SDO channel for the entire sequence. A bound EDS/DCF is
    /// required for nonempty streams; every pair and its declared write access are checked
    /// against one snapshot acquired with the channel before any write. Rebinding later affects
    /// the next call. SDO Auto mode selects expedited, segmented or block downloads.
    /// Stops on the first error or cancellation, preserving the SDO exception and its object pair.
    /// Acknowledged writes are not rolled back. Disposal cancels a waiting or running sequence.
    /// This explicit tool operation sends only the supplied writes. It adds no restore/store
    /// requests, NMT commands, 1F22h management, identity/signature checks or automatic boot.
    /// Safety commissioning still requires ConfigurePeerSafetyAsync and its verification steps.
    /// </remarks>
    /// <param name="node">The local node created by this library.</param>
    /// <param name="peerNodeId">The peer node-id, 1–127.</param>
    /// <param name="conciseDcf">The entire binary concise-DCF data stream, including its entry count.</param>
    /// <param name="cancellationToken">Cancels the channel wait and subsequent SDOs.</param>
    /// <exception cref="ArgumentNullException">The node is null.</exception>
    /// <exception cref="NotSupportedException">The node is a third-party implementation.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The peer node-id is invalid.</exception>
    /// <exception cref="System.IO.InvalidDataException">The stream is malformed or a value exceeds the configured limit.</exception>
    /// <exception cref="InvalidOperationException">A peer description is missing, omits a pair or declares it read-only.</exception>
    /// <exception cref="ObjectDisposedException">The node has already been disposed.</exception>
    public static Task DownloadConciseDcfAsync(this ICanOpenNode node, byte peerNodeId,
        ReadOnlyMemory<byte> conciseDcf, CancellationToken cancellationToken = default)
    {
        if (node is null) throw new ArgumentNullException(nameof(node));
        if (node is not CanOpenNode local)
            throw new NotSupportedException("Concise DCF download requires a node created by CanKit.Pro.");
        return local.DownloadConciseDcfAsync(peerNodeId, conciseDcf, cancellationToken);
    }

    /// <summary>Stores one complete binary concise DCF on a remote configuration manager
    /// at 1F22h (CiA 302-3 §3.3). Use <see cref="RequestConfigurationAsync"/> to request application separately.</summary>
    /// <param name="node">The local SDO client.</param>
    /// <param name="managerNodeId">Node-id of the configuration manager, 1–127.</param>
    /// <param name="targetNodeId">Node-id selecting the manager's storage slot, 1–127.
    /// The manager's own node-id selects its self-configuration.</param>
    /// <param name="conciseDcf">Complete little-endian data stream including its four-byte record count.</param>
    /// <param name="cancellationToken">Cancellation token for the SDO download.</param>
    /// <returns>A task that completes when the manager acknowledges the DOMAIN download.</returns>
    /// <exception cref="System.IO.InvalidDataException">The stream is malformed or its total size
    /// exceeds the client's <see cref="CanOpenNodeOptions.MaxSdoTransferBytes"/>.</exception>
    public static Task StoreConciseDcfAsync(this ICanOpenNode node, byte managerNodeId, byte targetNodeId,
        ReadOnlyMemory<byte> conciseDcf, CancellationToken cancellationToken = default)
    {
        if (node is null) throw new ArgumentNullException(nameof(node));
        CanOpenCobId.ValidateNodeId(managerNodeId);
        CanOpenCobId.ValidateNodeId(targetNodeId);
        cancellationToken.ThrowIfCancellationRequested();
        int limit = node.Options.MaxSdoTransferBytes;
        CheckStoredDcfLength(conciseDcf.Length, limit);
        var stream = conciseDcf.ToArray();
        CanOpenNode.ReadConciseDcf(stream, limit);
        return node.SdoDownloadAsync(managerNodeId, 0x1F22, targetNodeId, stream, cancellationToken);
    }

    /// <summary>Reads the binary concise DCF stored in a remote manager's readable 1F22h slot
    /// and checks its record structure (CiA 302-3 §3.3).</summary>
    /// <param name="node">The local SDO client.</param>
    /// <param name="managerNodeId">Node-id of the configuration manager, 1–127.</param>
    /// <param name="targetNodeId">Node-id selecting the storage slot, 1–127, including the
    /// manager's own node-id for its self-configuration.</param>
    /// <param name="mode">Auto for a classic SDO upload, or Block to request a block upload.</param>
    /// <param name="cancellationToken">Cancellation token for the SDO upload.</param>
    /// <returns>The complete validated binary stream, including its four-byte entry count.</returns>
    /// <exception cref="System.IO.InvalidDataException">The returned stream is malformed or
    /// exceeds <see cref="CanOpenNodeOptions.MaxSdoTransferBytes"/>.</exception>
    /// <exception cref="SdoAbortException">The SDO transfer was aborted, for example by a
    /// manager with a write-only slot.</exception>
    public static async Task<byte[]> ReadStoredConciseDcfAsync(this ICanOpenNode node, byte managerNodeId,
        byte targetNodeId, SdoTransferMode mode = SdoTransferMode.Auto, CancellationToken cancellationToken = default)
    {
        if (node is null) throw new ArgumentNullException(nameof(node));
        CanOpenCobId.ValidateNodeId(managerNodeId);
        CanOpenCobId.ValidateNodeId(targetNodeId);
        cancellationToken.ThrowIfCancellationRequested();
        var stream = await node.SdoUploadAsync(managerNodeId, 0x1F22, targetNodeId, mode, cancellationToken)
            .ConfigureAwait(false);
        int limit = node.Options.MaxSdoTransferBytes;
        CheckStoredDcfLength(stream.Length, limit);
        CanOpenNode.ReadConciseDcf(stream, limit);
        return stream;
    }

    private static void CheckStoredDcfLength(int length, int limit)
    {
        if (length > limit)
            throw new System.IO.InvalidDataException("The complete concise DCF exceeds MaxSdoTransferBytes.");
    }

    /// <summary>Asks a remote configuration manager to apply its stored configuration for one node
    /// (CiA 302-3 §3.6, 1F25h).</summary>
    /// <param name="node">The local SDO client.</param>
    /// <param name="managerNodeId">Node-id of the configuration manager, 1–127.</param>
    /// <param name="targetNodeId">Node-id whose stored configuration is requested, 1–127.
    /// A manager ignores a request for its own node-id.</param>
    /// <param name="cancellationToken">Cancellation token for the SDO download.</param>
    /// <returns>A task that completes when the manager acknowledges the request. The manager
    /// applies the stored DCF independently and can abort the request if it has no DCF for the target.</returns>
    public static Task RequestConfigurationAsync(this ICanOpenNode node, byte managerNodeId, byte targetNodeId,
        CancellationToken cancellationToken = default)
    {
        CanOpenCobId.ValidateNodeId(targetNodeId);
        return WriteConfigurationRequestAsync(node, managerNodeId, targetNodeId, cancellationToken);
    }

    /// <summary>Asks a remote configuration manager to apply its stored configurations for all nodes
    /// with one SDO download to 1F25h:80h (CiA 302-3 §3.6).</summary>
    /// <param name="node">The local SDO client.</param>
    /// <param name="managerNodeId">Node-id of the configuration manager, 1–127.</param>
    /// <param name="cancellationToken">Cancellation token for the SDO download.</param>
    /// <returns>A task that completes when the manager acknowledges the request.</returns>
    public static Task RequestAllConfigurationsAsync(this ICanOpenNode node, byte managerNodeId,
        CancellationToken cancellationToken = default)
        => WriteConfigurationRequestAsync(node, managerNodeId, 0x80, cancellationToken);

    private static Task WriteConfigurationRequestAsync(ICanOpenNode node, byte managerNodeId, byte subindex,
        CancellationToken cancellationToken)
    {
        if (node is null) throw new ArgumentNullException(nameof(node));
        CanOpenCobId.ValidateNodeId(managerNodeId);
        return node.SdoDownloadAsync(managerNodeId, 0x1F25, subindex, "conf"u8.ToArray(), cancellationToken);
    }

    /// <summary>Registers or removes the concise DCF used by automatic boot for one slave.</summary>
    /// <remarks>Requires EnableBootConfigurationDownload. Copies and validates the entire
    /// stream before replacing the plan; null removes it, a four-byte zero count is an explicit
    /// empty plan. Plans are local application configuration, not managed 1F22h storage, and
    /// survive local NMT resets. A live change restarts boot and cancels the previous generation.
    /// The plan is used only for zero/mismatching configuration expectations, after identity
    /// verification. A bound EDS/DCF must permit every write and any selected 1011h restore.
    /// Restore is selected by 1F81h bit 7 and nonzero 1F8Ah; Reset Node then waits for boot-up
    /// within SdoTimeout after confirmation. Restore requires comparable host RX/handoff
    /// timestamps from the bus service (provided by CanBusService); missing TX metadata fails
    /// the update. Buffered boot-up frames older than the reset handoff cannot release it.
    /// The peer SDO lease spans preflight, NMT, restore/reset and all writes.
    /// No automatic store, signature update or safety commissioning is added. Failures retain
    /// acknowledged writes, block Start and follow the existing mandatory/optional boot policy.
    /// Once an update starts, its plan must finish even if partially written 1020h values match
    /// on a retry. This incomplete marker survives local resets until success or plan replacement.</remarks>
    /// <param name="node">A local node created by this library.</param>
    /// <param name="peerNodeId">The slave node-id, 1–127; the local node's slot is ignored by boot.</param>
    /// <param name="conciseDcf">Complete binary concise-DCF stream, or null to remove the plan.</param>
    /// <exception cref="ArgumentNullException">The node is null.</exception>
    /// <exception cref="NotSupportedException">The node is a third-party implementation.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The node-id is invalid.</exception>
    /// <exception cref="InvalidOperationException">Automatic configuration download is disabled.</exception>
    /// <exception cref="System.IO.InvalidDataException">The stream is malformed or a value exceeds MaxSdoTransferBytes.</exception>
    /// <exception cref="ObjectDisposedException">The node is disposed.</exception>
    public static void SetSlaveBootConfiguration(this ICanOpenNode node, byte peerNodeId, ReadOnlyMemory<byte>? conciseDcf)
    {
        if (node is null) throw new ArgumentNullException(nameof(node));
        if (node is not CanOpenNode local)
            throw new NotSupportedException("Boot configuration requires a node created by CanKit.Pro.");
        local.SetSlaveBootConfiguration(peerNodeId, conciseDcf);
    }

    /// <summary>Returns immutable snapshots of the assigned slaves' latest boot steps in
    /// node-id order. A new boot clears earlier results; leaving the master role clears them.
    /// StartRequested means a command was queued, not that the slave acknowledged it.
    /// Identity checking is enabled by nonzero values in 1F84h–1F88h; configuration checking
    /// by RequireBootConfigurationMatch or EnableBootConfigurationDownload with
    /// 1F26h/1F27h expectations.</summary>
    /// <exception cref="ArgumentNullException">The node is null.</exception>
    /// <exception cref="NotSupportedException">The node was not created by this library.</exception>
    /// <exception cref="ObjectDisposedException">The node has been disposed.</exception>
    public static IReadOnlyList<SlaveBootResult> GetSlaveBootResults(this ICanOpenNode node)
    {
        if (node is null) throw new ArgumentNullException(nameof(node));
        if (node is not CanOpenNode local)
            throw new NotSupportedException("Boot results require a node created by CanKit.Pro.");
        return local.GetSlaveBootResults();
    }

    /// <summary>
    /// Acknowledges the local node's latched controller-overrun cause (<c>8110h</c>), including
    /// both RxOverflow and TxOverflow. Idempotent when no cause is active or
    /// <see cref="CanOpenNodeOptions.EmitControllerOverrunEmcy"/> is disabled. The application
    /// decides when the consequences of lost objects have been handled; this call does not
    /// repair the adapter, restore lost objects or clear another communication-error cause.
    /// </summary>
    /// <remarks>
    /// Returns after acknowledgement is processed on the node actor, with any <c>0000h</c>
    /// queued through the existing EMCY chain; it does not wait for transmission. A reset is
    /// queued only when the last reported node communication-error cause ends. Application bits
    /// are preserved. Notifications ordered after this acknowledgement on the actor may begin a
    /// new error event, including a notification whose driver callback raced this call.
    /// NMT reset also ends the cause, without an error-reset EMCY. This application acknowledgement
    /// contract is a library choice, not a controller repair inferred from a healthy bus state.
    /// </remarks>
    /// <param name="node">The local node created by this library.</param>
    /// <exception cref="ArgumentNullException"><paramref name="node"/> is null.</exception>
    /// <exception cref="NotSupportedException">The node is a third-party implementation.</exception>
    /// <exception cref="ObjectDisposedException">The node has been disposed.</exception>
    public static void AcknowledgeControllerOverrun(this ICanOpenNode node)
    {
        if (node is null) throw new ArgumentNullException(nameof(node));
        if (node is not CanOpenNode local)
            throw new NotSupportedException("Controller-overrun acknowledgement requires a node created by CanKit.Pro.");
        local.AcknowledgeControllerOverrun();
    }

    /// <summary>
    /// Disposes <paramref name="node"/> without holding a thread while it waits for the node's
    /// reader and event pump to finish, which <see cref="IDisposable.Dispose"/> does for up to two
    /// seconds. Either call may be made from a subscriber of the node (an event,
    /// <c>ApplicationReset</c>, <c>BackgroundExceptionOccurred</c>): the task that subscriber runs
    /// on is then not waited for. A second call while the first is running returns when the
    /// disposal has finished, except from a subscriber of the node, which cannot wait for a
    /// disposal that is waiting for it and returns at once. A node that is not one of this
    /// library's is disposed on the thread pool.
    /// </summary>
    /// <param name="node">The node to dispose.</param>
    public static ValueTask DisposeAsync(this ICanOpenNode node)
    {
        if (node is null) throw new ArgumentNullException(nameof(node));
        return node is IAsyncDisposable asynchronous
            ? asynchronous.DisposeAsync()
            : new ValueTask(Task.Run(node.Dispose));
    }
}
