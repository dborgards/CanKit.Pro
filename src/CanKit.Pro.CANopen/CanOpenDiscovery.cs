using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.RawCan;

namespace CanKit.Pro.CANopen;

/// <summary>What showed that a node is on the bus. A node may carry more than one.</summary>
[Flags]
public enum CanOpenPresenceEvidence
{
    /// <summary>Nothing. Not reported for a discovered node.</summary>
    None = 0,

    /// <summary>A heartbeat: state byte <c>04h</c> (Stopped), <c>05h</c> (Operational) or
    /// <c>7Fh</c> (Pre-operational) on <c>700h</c> + node-id.</summary>
    Heartbeat = 1,

    /// <summary>A boot-up: state byte <c>00h</c> on <c>700h</c> + node-id.</summary>
    BootUp = 2,

    /// <summary>The node answered the SDO upload of <c>1000h:00</c> that
    /// <see cref="CanOpenDiscovery.ScanAsync"/> sent, with the value or with an SDO abort.</summary>
    SdoResponse = 4,
}

/// <summary>One node <see cref="CanOpenDiscovery"/> found.</summary>
public sealed class CanOpenDiscoveredNode
{
    /// <summary>Constructs a result.</summary>
    public CanOpenDiscoveredNode(byte nodeId, CanOpenPresenceEvidence evidence, NmtState? heartbeatState,
        uint? deviceType)
    {
        CanOpenCobId.ValidateNodeId(nodeId);
        if (evidence == CanOpenPresenceEvidence.None)
            throw new ArgumentException("A discovered node has at least one kind of evidence.", nameof(evidence));
        NodeId = nodeId;
        Evidence = evidence;
        HeartbeatState = heartbeatState;
        DeviceType = deviceType;
    }

    /// <summary>Node-id, 1..127.</summary>
    public byte NodeId { get; }

    /// <summary>What was observed. Any one flag is enough for the node to be reported.</summary>
    public CanOpenPresenceEvidence Evidence { get; }

    /// <summary>The state in the last heartbeat heard, or <see langword="null"/> when only a
    /// boot-up or an SDO response was seen.</summary>
    public NmtState? HeartbeatState { get; }

    /// <summary><c>1000h:00</c> as the node returned it to <see cref="CanOpenDiscovery.ScanAsync"/>,
    /// or <see langword="null"/> when it was not read, the node answered with an abort, or the value
    /// was not exactly four bytes.</summary>
    public uint? DeviceType { get; }
}

/// <summary>
/// Finds the CANopen nodes on a bus (#131 decision 3; FR-CO-033, FR-CO-034). Listening is the
/// default; scanning is a separate call, made only when the caller asks for it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ListenAsync(ICanBus, TimeSpan?, CancellationToken)"/> opens no node and transmits
/// nothing. It collects heartbeats and boot-ups for a window the caller chooses
/// (<see cref="DefaultListenWindow"/> if not). One of the two is enough for a node to count as
/// present.
/// </para>
/// <para>
/// <see cref="ScanAsync"/> uses a node the caller has opened. It sends an SDO upload of
/// <c>1000h:00</c> to every node-id 1..127 except the client's own and the ones the caller
/// passes, typically those a listen already found. That is the only request it sends: the pair
/// is one the peer-SDO gate allows without a peer file, and with a peer file bound only if the
/// file lists it. A node that stays silent also receives the SDO client's usual timeout abort
/// (<c>0504 0000h</c>), which lets a node that answers late drop its half-open transfer.
/// </para>
/// </remarks>
public static class CanOpenDiscovery
{
    /// <summary>The listen window when the caller does not choose one: 2 seconds.</summary>
    public static TimeSpan DefaultListenWindow { get; } = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan MaxListenWindow = TimeSpan.FromMilliseconds(int.MaxValue);

    /// <summary>
    /// Listens on <paramref name="bus"/> for heartbeats and boot-ups and returns the nodes heard,
    /// ordered by node-id. Opens no CANopen node and transmits nothing.
    /// </summary>
    /// <param name="bus">The bus. It is not disposed.</param>
    /// <param name="window">How long to listen. Any positive duration up to
    /// <see cref="int.MaxValue"/> milliseconds; <see langword="null"/> is
    /// <see cref="DefaultListenWindow"/>.</param>
    /// <param name="cancellationToken">Ends the listen early with
    /// <see cref="OperationCanceledException"/>.</param>
    public static Task<IReadOnlyList<CanOpenDiscoveredNode>> ListenAsync(ICanBus bus, TimeSpan? window = null,
        CancellationToken cancellationToken = default)
    {
        if (bus is null) throw new ArgumentNullException(nameof(bus));
        var duration = ValidateWindow(window);
        return ListenOwnedAsync(new CanBusService(bus), duration, cancellationToken);
    }

    /// <summary>
    /// Listens on an existing <paramref name="service"/>, shared with other protocols on the
    /// same bus (FR-CO-012). The service is not disposed. Otherwise as
    /// <see cref="ListenAsync(ICanBus, TimeSpan?, CancellationToken)"/>.
    /// </summary>
    public static Task<IReadOnlyList<CanOpenDiscoveredNode>> ListenAsync(ICanBusService service,
        TimeSpan? window = null, CancellationToken cancellationToken = default)
    {
        if (service is null) throw new ArgumentNullException(nameof(service));
        var duration = ValidateWindow(window);
        return ListenCoreAsync(service, duration, Task.Delay, cancellationToken);
    }

    /// <summary>
    /// Sends an SDO upload of <c>1000h:00</c> through <paramref name="client"/> to each node-id
    /// 1..127 other than <see cref="ICanOpenNode.NodeId"/> and <paramref name="skipNodeIds"/>,
    /// and returns the nodes that answered, ordered by node-id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A node counts as present when it answers: with the value, which lands in
    /// <see cref="CanOpenDiscoveredNode.DeviceType"/>, with an SDO abort
    /// (<see cref="SdoAbortOrigin.Peer"/>), or with a response the client aborts as malformed.
    /// The client's own timeout after <see cref="CanOpenNodeOptions.SdoTimeout"/> is absence.
    /// </para>
    /// <para>
    /// No request goes out for a node-id whose bound peer description does not list
    /// <c>1000h:00</c> (<see cref="PeerSdoAccessException"/>), nor for one the client already
    /// has an SDO transfer with. Neither is reported, since nothing was asked. The requests to
    /// the different node-ids run concurrently. A failure that is none of these, such as a
    /// transport fault, ends the scan with that exception.
    /// </para>
    /// </remarks>
    /// <param name="client">An opened node whose SDO client does the reads.</param>
    /// <param name="skipNodeIds">Node-ids not to ask, typically the result of
    /// <see cref="ListenAsync(ICanBus, TimeSpan?, CancellationToken)"/>. May be
    /// <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the outstanding requests.</param>
    public static async Task<IReadOnlyList<CanOpenDiscoveredNode>> ScanAsync(ICanOpenNode client,
        IEnumerable<byte>? skipNodeIds = null, CancellationToken cancellationToken = default)
    {
        if (client is null) throw new ArgumentNullException(nameof(client));
        return await ScanCoreAsync(
            client.NodeId,
            skipNodeIds,
            (nodeId, token) => client.SdoUploadAsync(nodeId, DeviceTypeIndex, 0x00, token),
            cancellationToken).ConfigureAwait(false);
    }

    private const ushort DeviceTypeIndex = 0x1000;

    /// <summary>
    /// The scan. <paramref name="uploadDeviceType"/> reads <c>1000h:00</c> of one node-id; the
    /// public entry point passes the client's SDO upload, a test passes its own.
    /// </summary>
    internal static async Task<IReadOnlyList<CanOpenDiscoveredNode>> ScanCoreAsync(byte clientNodeId,
        IEnumerable<byte>? skipNodeIds, Func<byte, CancellationToken, Task<byte[]>> uploadDeviceType,
        CancellationToken cancellationToken)
    {
        var skip = new HashSet<byte>(skipNodeIds ?? Array.Empty<byte>()) { clientNodeId };
        var targets = Enumerable.Range(CanOpenCobId.MinNodeId, CanOpenCobId.MaxNodeId)
            .Select(id => (byte)id)
            .Where(id => !skip.Contains(id))
            .ToArray();
        var probes = await Task.WhenAll(targets.Select(id => ProbeAsync(uploadDeviceType, id, cancellationToken)))
            .ConfigureAwait(false);

        return probes.Where(p => p is not null).Select(p => p!).ToArray();
    }

    internal static TimeSpan ValidateWindow(TimeSpan? window)
    {
        var duration = window ?? DefaultListenWindow;
        if (duration <= TimeSpan.Zero || duration > MaxListenWindow)
        {
            throw new ArgumentOutOfRangeException(nameof(window), duration,
                "The listen window must be positive and at most int.MaxValue milliseconds.");
        }

        return duration;
    }

    private static async Task<IReadOnlyList<CanOpenDiscoveredNode>> ListenOwnedAsync(CanBusService service,
        TimeSpan window, CancellationToken cancellationToken)
    {
        using (service)
        {
            return await ListenCoreAsync(service, window, Task.Delay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The listen. <paramref name="delay"/> is the window; a test passes one it completes itself,
    /// and <paramref name="pumpStarted"/> hands it the drain task to check it has ended.
    /// Frames are drained on a pump for the whole window, so a busy bus does not overflow the
    /// subscription buffer, and a frame already buffered when the window closes is still counted.
    /// </summary>
    internal static async Task<IReadOnlyList<CanOpenDiscoveredNode>> ListenCoreAsync(ICanBusService service,
        TimeSpan window, Func<TimeSpan, CancellationToken, Task> delay, CancellationToken cancellationToken,
        Action<Task>? pumpStarted = null)
    {
        var heard = new Dictionary<byte, (CanOpenPresenceEvidence Evidence, NmtState? State)>();
        Task pump;
        Task closed;
        // Disposing the subscription completes Frames after the frames already buffered, so the
        // pump is awaited only once the window has closed. A cancelled window closes it too:
        // WhenAny waits without throwing, the pump is drained, and only then does the
        // cancellation surface, so a caller disposing the service afterwards never races it.
        // Echoes included: a node running in this process on the same bus or service is on the
        // bus too, and an adapter marks its heartbeats as this host's echoes. The listen itself
        // sends nothing, so an echo can only be another node's frame.
        using (var subscription = service.Subscribe(
            CanIdFilter.Range(
                CanOpenCobId.HeartbeatBase + CanOpenCobId.MinNodeId,
                CanOpenCobId.HeartbeatBase + CanOpenCobId.MaxNodeId),
            includeEcho: true))
        {
            pump = Task.Run(async () =>
            {
                await foreach (var frameEvent in subscription.Frames.ConfigureAwait(false))
                {
                    Record(heard, frameEvent.Frame);
                }
            });
            pumpStarted?.Invoke(pump);
            closed = delay(window, cancellationToken);
            await Task.WhenAny(closed).ConfigureAwait(false);
        }

        await pump.ConfigureAwait(false);
        await closed.ConfigureAwait(false);
        return heard
            .OrderBy(pair => pair.Key)
            .Select(pair => new CanOpenDiscoveredNode(pair.Key, pair.Value.Evidence, pair.Value.State, deviceType: null))
            .ToArray();
    }

    /// <summary>
    /// Classifies one error-control frame. Same wire mapping as the node's heartbeat consumer:
    /// bit 7 is the guarding toggle and is masked. A byte that is neither a boot-up nor one of the
    /// three heartbeat states is not evidence of either and is dropped.
    /// </summary>
    internal static void Record(Dictionary<byte, (CanOpenPresenceEvidence Evidence, NmtState? State)> heard,
        CanFrameView frame)
    {
        if (frame.IsExtendedFrame || frame.IsRemoteFrame || frame.Data.Length < 1) return;
        uint id = (uint)frame.ID;
        if (id < CanOpenCobId.HeartbeatBase + CanOpenCobId.MinNodeId
            || id > CanOpenCobId.HeartbeatBase + CanOpenCobId.MaxNodeId)
        {
            return;
        }

        byte nodeId = (byte)(id - CanOpenCobId.HeartbeatBase);
        CanOpenPresenceEvidence evidence;
        NmtState? state = null;
        switch (frame.Data.Span[0] & 0x7F)
        {
            case 0x00:
                evidence = CanOpenPresenceEvidence.BootUp;
                break;
            case 0x04:
                evidence = CanOpenPresenceEvidence.Heartbeat;
                state = NmtState.Stopped;
                break;
            case 0x05:
                evidence = CanOpenPresenceEvidence.Heartbeat;
                state = NmtState.Operational;
                break;
            case 0x7F:
                evidence = CanOpenPresenceEvidence.Heartbeat;
                state = NmtState.PreOperational;
                break;
            default:
                return;
        }

        heard.TryGetValue(nodeId, out var previous);
        heard[nodeId] = (previous.Evidence | evidence, state ?? previous.State);
    }

    private static async Task<CanOpenDiscoveredNode?> ProbeAsync(
        Func<byte, CancellationToken, Task<byte[]>> uploadDeviceType, byte nodeId,
        CancellationToken cancellationToken)
    {
        try
        {
            var data = await uploadDeviceType(nodeId, cancellationToken).ConfigureAwait(false);
            // 1000h:00 is UNSIGNED32. Any other length is an answer, and so presence, but not a
            // device type: taking four bytes of a longer value would report one the node never gave.
            uint? deviceType = data.Length == 4
                ? (uint)(data[0] | data[1] << 8 | data[2] << 16 | data[3] << 24)
                : null;
            return new CanOpenDiscoveredNode(nodeId, CanOpenPresenceEvidence.SdoResponse, null, deviceType);
        }
        catch (SdoAbortException abort)
            when (abort.Origin == SdoAbortOrigin.Local && abort.AbortCode == (uint)SdoAbortCode.SdoProtocolTimedOut)
        {
            // The client's own timer: nothing came back.
            return null;
        }
        catch (SdoAbortException)
        {
            // The peer aborted, or it answered with a response the client had to abort. Either
            // way a frame came back from 580h + node-id.
            return new CanOpenDiscoveredNode(nodeId, CanOpenPresenceEvidence.SdoResponse, null, deviceType: null);
        }
        catch (PeerSdoAccessException)
        {
            return null;
        }
        catch (InvalidOperationException ex) when (ex.GetType() == typeof(InvalidOperationException))
        {
            // Another SDO transfer with this server is already in flight on the client. The
            // exact type keeps ObjectDisposedException and the gate's refusal out of this arm.
            return null;
        }
    }
}
