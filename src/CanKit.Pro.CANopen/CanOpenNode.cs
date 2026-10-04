using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Pro.Actor;
using CanKit.Pro.CANopen.Emcy;
using CanKit.Pro.CANopen.Heartbeat;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Pdo;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.RawCan;
using CanKit.Pro.Reliability;

namespace CanKit.Pro.CANopen;

/// <summary>
/// The default <see cref="ICanOpenNode"/> implementation. Composed on the
/// CanKit.Pro L2 pipeline (<see cref="ICanBusService"/> for RX demux and TX confirmation,
/// <see cref="IProtocolActor"/> for single-writer per-node state, <see cref="DeadlineScheduler"/>
/// for SDO/heartbeat/SYNC timers) exactly like the other Pro protocol stacks
/// (arc42 §8.3, ADR-6; FR-CO-012).
/// </summary>
/// <remarks>
/// <para>
/// The node subscribes to the tight set of CANopen 11-bit COB-IDs it can actually receive
/// (NMT master, SYNC, EMCY(any), SDO Rx for its own id, SDO Tx from any peer, heartbeat/bootup
/// from any peer, and every configured RPDO). It never competes on
/// <see cref="ICanBus.ReceiveAsync"/> — RX flows entirely through
/// <see cref="ISubscription.Frames"/>.
/// </para>
/// <para>
/// All state (NMT slave state machine, SDO client/server sessions, the heartbeat producer and
/// consumer modules, PDO tables, timer handles) lives inside the actor and is only touched from posted callbacks;
/// public methods marshal work in via <see cref="IProtocolActor.PostAsync{T}"/>. This is the
/// same threading model that the J1939-TP / IsoTp / UDS clients rely on.
/// </para>
/// </remarks>
internal sealed partial class CanOpenNode : ICanOpenNode, IAsyncDisposable
{
    private readonly ICanBusService _service;
    private readonly bool _ownsService;
    private readonly byte _nodeId;
    private readonly CanOpenNodeOptions _options;

    private readonly ProtocolActor _actor;
    private readonly DeadlineScheduler _deadlines;
    private readonly ISubscription _subscription;
    private readonly Task _readerTask;
    private readonly CancellationTokenSource _readerCts = new();

    // User-event queue that decouples delivery from the actor loop. Events are enqueued from
    // the actor thread and drained by a single dispatcher task, so a slow subscriber can never
    // stall the protocol loop and handlers for different events never run concurrently.
    //
    // Ordinary events (heartbeat / SYNC / RPDO / NMT / node-guarding reception, life guarding)
    // are bounded by CanOpenNodeOptions.EventQueueCapacity with drop-oldest. HeartbeatTimeout,
    // NodeGuardingTimeout and EmcyReceived share that queue — same order, same thread — but are
    // never the event that is discarded: a guarding consumer that misses one treats a silent
    // peer as alive (#170). They can make the queue longer than the capacity, but not without
    // bound (#201): a timeout already waiting for the same producer absorbs the next one (the
    // handler will learn the peer is silent either way), an EMCY identical to one already
    // waiting is absorbed likewise, but only while nothing else about that producer has been
    // queued since -- error, reset, error is three events, not two -- and a producer that keeps a further EventQueueCapacity
    // distinct EMCYs waiting has the surplus discarded and counted, reported once per burst on
    // BackgroundExceptionOccurred. The wake-up is a single coalesced signal, not one token per
    // event: a drop removes the event and leaves no token behind, and a burst while the pump is
    // busy does not allocate. BackgroundExceptionOccurred stays synchronous and is not part of
    // this queue.
    private readonly object _eventLock = new();
    private readonly LinkedList<PendingEvent> _pendingEvents = new();
    private readonly SemaphoreSlim _eventWake = new(0, 1);
    private LinkedListNode<PendingEvent>? _oldestNonCritical;
    private int _pendingNonCritical;
    private long _submittedEventCount;
    private readonly Dictionary<(byte, byte, ulong), LinkedListNode<PendingEvent>> _pendingKeyed = new();
    // Per producer node-id: the last event still waiting that says something about the producer's
    // state -- a heartbeat or guarding response, an EMCY, but not a timeout. A waiting critical
    // event absorbs an identical one only while no such event was queued behind it, so "error,
    // reset, error" and "silent, alive, silent" reach the handler as three events and not as two,
    // while a heartbeat timeout and a node-guarding timeout for the same node do not keep each
    // other apart. An event that was dropped to make room is no longer waiting and does not count:
    // the handler will never see it (#201).
    private readonly LinkedListNode<PendingEvent>?[] _lastForProducer = new LinkedListNode<PendingEvent>?[256];
    private long _enqueueOrdinal;
    private readonly int[] _pendingEmcyPerProducer = new int[256];
    private readonly bool[] _emcyOverflowReported = new bool[256];
    private long _coalescedEventCount;
    private long _emcyOverflowCount;
    private bool _eventPumpCompleted;
    private readonly Task _eventPumpTask;

    private readonly ObjectDictionary _od = new();

    // -----------------------------------------------------------------------------------------
    // State touched only on the actor loop.
    // -----------------------------------------------------------------------------------------
    private NmtState _state = NmtState.Initializing;

    // SDO server: at most one outstanding segmented transfer against our own OD at a time (a
    // second Initiate from the same peer supersedes any previous open transfer per CiA 301
    // §7.2.4.3.4).
    private SdoServerSession? _sdoServer;

    // SDO client: at most one in-flight client-side transfer per remote server (keyed by the
    // remote node-id we send to). One client concurrently talking to multiple servers is
    // supported (each has its own state entry).
    private readonly Dictionary<byte, SdoClientSession> _sdoClients = new();

    // SDO block-transfer sessions (FR-CO-004). Block transfer has enough state and enough
    // dispatch differences (segment stream vs. control frames) that we keep it in a dedicated
    // partial file (CanOpenNode.SdoBlock.cs) with its own session types. A client-side or
    // server-side block session for a given peer occupies the same "one transfer at a time
    // per peer" slot as the classical SDO client / server; the two are mutually exclusive by
    // construction (BeginSdoBlockDownload / BeginSdoBlockUpload each check both dictionaries).
    private readonly Dictionary<byte, SdoBlockClientSession> _sdoBlockClients = new();
    private SdoBlockServerSession? _sdoBlockServer;

    // Node-guarding consumers (FR-CO-009): keyed by producer node-id, each with its own
    // guardTime deadline (RTR poll) and life-time deadline (timeout).
    private readonly Dictionary<byte, NodeGuardingConsumer> _nodeGuardingConsumers = new();

    // Heartbeat. Two modules that do not know about each other: the producer sends, the
    // consumer watches. The flying master composes them; it does not keep a timer of its own.
    private readonly IHeartbeatProducer _heartbeatProducer;
    private readonly IHeartbeatConsumer _heartbeatConsumer;

    // SYNC producer.
    private IDisposable? _syncProducerHandle;
    private TimeSpan _syncProducerInterval;

    // Node-guarding producer toggle bit (FR-CO-009). CiA 301 §7.2.8.3.3 requires the producer
    // to start with toggle=0 and flip it on every reply so the consumer can distinguish a
    // fresh answer from a stale duplicate.
    private bool _nodeGuardingProducerToggle;

    private int _disposed;
    private bool _pumpStopRequested;
    private readonly TaskCompletionSource<bool> _disposeDone = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <inheritdoc />
    public byte NodeId => _nodeId;

    /// <inheritdoc />
    public CanOpenNodeOptions Options => _options;

    /// <inheritdoc />
    public ObjectDictionary ObjectDictionary => _od;

    /// <inheritdoc />
    public NmtState State
    {
        get
        {
            // When the caller is already on the actor loop (e.g. reading State from a
            // SyncReceived / NmtCommandReceived / RpdoReceived handler that the actor itself
            // is currently running), synchronously waiting on PostAsync would deadlock the loop
            // against itself -- the posted item cannot execute until the current callback
            // returns, but the current callback is blocked here waiting for it. Run the read
            // inline instead: it is the same single-writer thread that any other State write
            // would come from, so no coordination is needed. External callers still marshal
            // through the mailbox to see a value consistent with in-flight transitions.
            if (_actor.IsOnCurrentActor) return _state;
            return _actor.PostAsync(() => _state).GetAwaiter().GetResult();
        }
    }

    /// <inheritdoc />
    public event EventHandler<HeartbeatReceivedEventArgs>? HeartbeatReceived;
    /// <inheritdoc />
    public event EventHandler<HeartbeatTimeoutEventArgs>? HeartbeatTimeout;
    /// <inheritdoc />
    public event EventHandler<EmcyReceivedEventArgs>? EmcyReceived;
    /// <inheritdoc />
    public event EventHandler<SyncReceivedEventArgs>? SyncReceived;
    /// <inheritdoc />
    public event EventHandler<RpdoReceivedEventArgs>? RpdoReceived;
    /// <inheritdoc />
    public event EventHandler<NmtCommandReceivedEventArgs>? NmtCommandReceived;

    /// <inheritdoc />
    public event EventHandler<NmtResetEventArgs>? ApplicationReset;
    /// <inheritdoc />
    public event EventHandler<NodeGuardingReceivedEventArgs>? NodeGuardingReceived;
    /// <inheritdoc />
    public event EventHandler<NodeGuardingTimeoutEventArgs>? NodeGuardingTimeout;
    /// <inheritdoc />
    public event EventHandler<LifeGuardingEventArgs>? LifeGuardingEvent;
    /// <inheritdoc />
    public event EventHandler<Exception>? BackgroundExceptionOccurred;

    internal CanOpenNode(ICanBusService service, byte nodeId, CanOpenNodeOptions options,
        bool ownsService, ITimeSource? timeSource = null, CanOpenDeviceDescription? description = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        CanOpenCobId.ValidateNodeId(nodeId);
        _nodeId = nodeId;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _ownsService = ownsService;

        // The time source is a test seam (#113): inhibit times, event timers and every deadline
        // are measured against the actor's monotonic source, which a test can drive by hand.
        _actor = new ProtocolActor(ActorExecutionMode.DedicatedThread, synchronizationContext: null,
            timeSource, shutdownTimeout: null);
        _actor.BackgroundExceptionOccurred += (_, ex) => RaiseBackgroundException(ex);
        _deadlines = new DeadlineScheduler(_actor);
        _heartbeatProducer = new HeartbeatProducer(_actor, () => _disposed == 0,
            () => { _ = EmitHeartbeat((byte)_state); });
        _heartbeatConsumer = new HeartbeatConsumer(_deadlines);
        _heartbeatConsumer.TimedOut += (producer, timeout) => RaiseHeartbeatTimeout(producer, timeout);
        AttachFlyingMasterHeartbeatWatch();

        // The communication-profile objects at their CiA 301 defaults, plus the OD hooks that
        // validate writes to them and carry accepted values into the runtime
        // (CanOpenNode.CommunicationProfile.cs).
        PopulateCommunicationProfile();
        // A device description shapes the dictionary on top of that, before the node is on the
        // bus (CanOpenNode.DeviceDescription.cs).
        if (description is not null) ApplyDeviceDescription(description);

        // Change-of-state TPDOs (FR-CO-006): application-originated OD writes trigger
        // event-driven TPDOs whose mapping contains the written entry.
        _od.EntryWritten += OnOdEntryWrittenForCoS;

        // The pump has to be running before the subscription exists: a frame can be dispatched
        // as soon as Subscribe returns, and construction failure below signals the pump to
        // exit so it does not outlive a node that never finished opening.
        _eventPumpTask = Task.Run(RunEventPumpAsync);

        try
        {
            // Subscribe once to the tight COB-ID range the node cares about:
            //   0x000 (NMT), 0x080..0x0FF (SYNC + EMCY range),
            //   0x180..0x77F (PDOs + SDO Rx/Tx + heartbeat range).
            // We evaluate the actual routing in the actor since the RPDO table changes at
            // runtime, but pre-filtering at the subscription reduces per-frame delegate calls
            // on the demux side.
            // Echoes are asked for on purpose, and not filtered further. `IsEcho` says "this
            // HOST transmitted it", not "this NODE transmitted it", and CanOpen.OpenNode
            // documents that several nodes with different node-ids may share one service to
            // multiplex CANopen identities over one bus. Dropping host echoes would cut a local
            // master off from a local slave -- their SDO transfers, PDOs, heartbeats and NMT
            // commands are all genuine peer traffic to each other.
            //
            // It also keeps `ICanOpenNode.SyncReceived`'s promise ("either from a remote producer
            // or from this node's own producer if echo is on"): HandleSync is the only path that
            // raises it *and* emits the synchronous TPDOs -- ScheduleSyncProducerTick only puts
            // the frame on the wire -- so a SYNC producer that never sees its own SYNC stops
            // emitting its own synchronous TPDOs.
            //
            // What this does NOT do is filter out this node's own traffic. That is not left
            // undone -- HandleIncoming does it per message class, where the node id inside the
            // COB-ID can be read and where the classes that must keep hearing themselves (SYNC,
            // NMT, both SDO directions, RPDOs, a consumer configured for the local id) can be
            // exempted individually (#95). It cannot be done here, because at this point a frame
            // is only an id in a range.
            _subscription = _service.Subscribe(f =>
            {
                var frame = f.Frame;
                if (frame.IsExtendedFrame) return false;
                uint id = (uint)frame.ID;
                // 0x000 NMT master, 0x080..0x77F everything else CANopen.
                // 0x071..0x076 are the flying-master services, inside the identifier range CiA 301
                // reserves and this subscription otherwise skips.
                return id == CanOpenCobId.NmtCommand
                    || id is CanOpenCobId.FlyingMasterClaim or CanOpenCobId.FlyingMasterTrigger
                        or CanOpenCobId.FlyingMasterDetect or CanOpenCobId.FlyingMasterForce
                    || (id >= 0x080 && id <= 0x77F);
            }, includeEcho: true);
        }
        catch
        {
            CompleteEventQueue();
            _actor.Dispose();
            throw;
        }

        _readerTask = Task.Run(RunReaderAsync);

        // Enter Pre-Operational immediately and announce a bootup on the wire. Bootup is a
        // one-shot 1-byte frame with data[0] == 0 on COB-ID 0x700 + nodeId
        // (CiA 301 §7.2.8.3.2). We do it here at construction so tests can observe it.
        _actor.Post(() =>
        {
            ApplyAllCommunicationObjects();
            _state = NmtState.PreOperational;
            _ = EmitHeartbeat(0x00);
        });
    }

    /// <inheritdoc />
    public Task SendNmtCommandAsync(NmtCommand command, byte targetNodeId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var payload = new byte[] { (byte)command, targetNodeId };
        return SendControlFrame(CanOpenCobId.NmtCommand, payload, cancellationToken);
    }

    /// <inheritdoc />
    public void StartHeartbeatProducer(TimeSpan interval)
    {
        ThrowIfDisposed();
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        // 1017h producer heartbeat time (CiA 301 §7.5.2.20); the runtime follows the OD.
        _od.WriteUnsigned(Co.ProducerHeartbeat, 0x00, ToMilliseconds16(interval, nameof(interval), allowZero: false));
    }

    /// <inheritdoc />
    public void StopHeartbeatProducer()
    {
        if (_disposed != 0) return;
        _od.WriteUnsigned(Co.ProducerHeartbeat, 0x00, 0);
    }

    /// <inheritdoc />
    public void AddHeartbeatConsumer(byte producerNodeId, TimeSpan timeout)
        => AddHeartbeatConsumer(producerNodeId, timeout, releaseInstalledWatch: true);

    private void AddHeartbeatConsumer(byte producerNodeId, TimeSpan timeout, bool releaseInstalledWatch)
    {
        ThrowIfDisposed();
        CanOpenCobId.ValidateNodeId(producerNodeId);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        ushort ms = ToMilliseconds16(timeout, nameof(timeout), allowZero: false);

        // The dictionary write and, when the application is taking the watch over, the ownership
        // clear are one actor job. A stop queued around this call then either removes the old
        // consumer before the new value is stored, or sees the watch already given up and leaves
        // the new value alone. Two jobs let stop land between them and delete what was just claimed.
        RunOnActorAndWait(() =>
        {
            // 1016h consumer heartbeat time (CiA 301 §7.5.2.19): reuse the sub-index already
            // monitoring this producer, else the first unused one, else grow the array. One
            // transaction on the dictionary, so two callers cannot both grow into the same sub-index
            // and a direct write of 1016h on another thread waits for the find-or-grow to finish.
            // The write is a method group: it runs on this actor before the next job, and a
            // capturing lambda would leave a compiler branch that nothing takes.
            _heartbeatSlotProducer = producerNodeId;
            _heartbeatSlotMilliseconds = ms;
            _od.Transaction(WriteHeartbeatConsumerSlot);
            if (releaseInstalledWatch && _flyingMasterInstalledWatch == producerNodeId)
                _flyingMasterInstalledWatch = null;
        });
    }

    private byte _heartbeatSlotProducer;
    private ushort _heartbeatSlotMilliseconds;

    private void WriteHeartbeatConsumerSlot()
    {
        byte producerNodeId = _heartbeatSlotProducer;
        ushort ms = _heartbeatSlotMilliseconds;
        byte count = (byte)_od.ReadUnsigned(Co.ConsumerHeartbeat, 0x00);
        int slot = FindHeartbeatConsumerSlot(producerNodeId, count);
        if (slot < 0)
        {
            for (int s = 1; s <= count; s++)
            {
                if (_od.TryReadUnsigned(Co.ConsumerHeartbeat, (byte)s, out var v) && (ushort)(v & 0xFFFF) == 0)
                {
                    slot = s;
                    break;
                }
            }
        }
        if (slot < 0)
        {
            if (count >= 0x7F)
                throw new InvalidOperationException("1016h holds at most 127 consumer heartbeat times (CiA 301 §7.5.2.19).");
            slot = count + 1;
            // The sub-index may already exist: an NMT reset restores sub-index 00h to the
            // stored count and zeroes the entries the array had grown by since, but keeps
            // them, so growing again reuses such an entry rather than re-declaring it.
            if (!_od.TryGet(Co.ConsumerHeartbeat, (byte)slot, out _))
                _od.Declare(Co.ConsumerHeartbeat, (byte)slot, OdDataType.Unsigned32, OdAccess.ReadWrite, new byte[4], pdoMappable: false);
            // The entry first, while the slot is still outside the count — what a hidden slot
            // held is being replaced, not brought back — then the count, which is validated
            // against the entry it will show (Codex on #133).
            _od.WriteUnsigned(Co.ConsumerHeartbeat, (byte)slot, ((uint)producerNodeId << 16) | ms);
            _od.WriteUnsigned(Co.ConsumerHeartbeat, 0x00, (uint)slot);
            return;
        }
        _od.WriteUnsigned(Co.ConsumerHeartbeat, (byte)slot, ((uint)producerNodeId << 16) | ms);
    }

    /// <inheritdoc />
    public void RemoveHeartbeatConsumer(byte producerNodeId)
    {
        if (_disposed != 0) return;
        _od.Transaction(() =>
        {
            byte count = (byte)_od.ReadUnsigned(Co.ConsumerHeartbeat, 0x00);
            int slot = FindHeartbeatConsumerSlot(producerNodeId, count);
            if (slot > 0) _od.WriteUnsigned(Co.ConsumerHeartbeat, (byte)slot, 0);
        });
    }

    private int FindHeartbeatConsumerSlot(byte producerNodeId, byte count)
    {
        for (int s = 1; s <= count; s++)
        {
            if (!_od.TryReadUnsigned(Co.ConsumerHeartbeat, (byte)s, out var v)) continue;
            if ((ushort)(v & 0xFFFF) != 0 && (byte)((v >> 16) & 0xFF) == producerNodeId) return s;
        }
        return -1;
    }

    /// <inheritdoc />
    public void StartSyncProducer(TimeSpan interval)
    {
        ThrowIfDisposed();
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        // 1006h communication cycle period, then bit 30 of 1005h (CiA 301 §7.5.2.5 / §7.5.2.6).
        _od.WriteUnsigned(Co.CyclePeriod, 0x00, ToMicroseconds32(interval, nameof(interval)));
        _od.WriteUnsigned(Co.SyncCobId, 0x00, _od.ReadUnsigned(Co.SyncCobId, 0x00) | CanOpenCobId.SyncGenerateBit);
    }

    /// <inheritdoc />
    public void StopSyncProducer()
    {
        if (_disposed != 0) return;
        _od.WriteUnsigned(Co.SyncCobId, 0x00, _od.ReadUnsigned(Co.SyncCobId, 0x00) & ~CanOpenCobId.SyncGenerateBit);
    }

    /// <inheritdoc />
    public Task SendSyncAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        // The dictionary, not the actor's copy of it: a caller that has just written 1005h sees
        // its own write here, while the runtime copy is updated by a posted apply that may not
        // have run yet. The value was validated when the dictionary accepted it.
        uint cobId = _od.ReadUnsigned(Co.SyncCobId, 0x00) & CanOpenCobId.CanIdMask;
        return SendControlFrame(cobId, Array.Empty<byte>(), cancellationToken);
    }

    /// <inheritdoc />
    public Task SendEmcyAsync(ushort errorCode, byte errorRegister,
        ReadOnlyMemory<byte> manufacturerSpecific = default,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var msg = new EmcyMessage(_nodeId, errorCode, errorRegister, manufacturerSpecific.Span);
        return _actor.PostAsync(() =>
        {
            if (!_emcyValid)
                throw new InvalidOperationException("EMCY is disabled: bit 31 of 1014h (COB-ID EMCY) is set.");
            if (_state == NmtState.Stopped)
            {
                // §7.3.2.2.4: EMCY triggered in Stopped is pending; the most recent one goes out
                // after the transition into another NMT state.
                _pendingEmcy = msg;
                return Task.CompletedTask;
            }
            return EmitEmcy(msg, cancellationToken);
        }).Unwrap();
    }

    // Every EMCY this node transmits goes out through one chain, so the wire order is the order
    // the actor decided. 1001h is "a part of an emergency object" (CiA 301 §7.5.2.2): the
    // register a master reads is the one the last EMCY on the bus carried, so it is written
    // inside the chain, right before its frame is transmitted — not when the call was posted,
    // nor for an EMCY that is disabled, held or cancelled and never reaches the bus (Codex on
    // #133). Actor loop only.
    private Task _emcySendChain = Task.CompletedTask;

    // Every frame of this node on 0x700 + id — boot-up, state change, producer tick, guarding
    // reply — goes out through one chain likewise, so a boot-up ordered by a reset is on the bus
    // before the tick that became due while the application's reset hook ran, and before the
    // reply to a poll that was already in the mailbox (Codex and Bugbot on #133). Actor loop only.
    private Task _heartbeatSendChain = Task.CompletedTask;

    private Task EmitHeartbeat(byte payload)
    {
        uint cobId = CanOpenCobId.Heartbeat(_nodeId);
        var frame = new[] { payload };
        var link = _heartbeatSendChain.ContinueWith(
            _ => SendControlFrame(cobId, frame),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        _heartbeatSendChain = link;
        return link;
    }

    private Task EmitEmcy(EmcyMessage msg, CancellationToken cancellationToken = default)
    {
        uint cobId = _emcyCobId;
        var frame = msg.Encode();
        byte errorRegister = msg.ErrorRegister;
        var link = _emcySendChain.ContinueWith(
            _ =>
            {
                if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);
                _od.WriteUnsigned(Co.ErrorRegister, 0x00, errorRegister);
                return SendControlFrame(cobId, frame, cancellationToken);
            },
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        _emcySendChain = link;
        return link;
    }

    /// <inheritdoc />
    public Task<byte[]> SdoUploadAsync(byte serverNodeId, ushort index, byte subindex,
        CancellationToken cancellationToken)
        => SdoUploadAsync(serverNodeId, index, subindex, SdoTransferMode.Auto, cancellationToken);

    /// <summary>
    /// Rejects an <see cref="SdoTransferMode"/> value that is not a defined member.
    /// </summary>
    /// <remarks>
    /// Both transfer entry points route "not <see cref="SdoTransferMode.Block"/>" to the classic
    /// client, so an undefined value used to select a transport silently. That is the wrong
    /// failure for a value that can only arrive from a bug: an assembly still compiled against
    /// 1.2.x supplies the literal <c>1</c> or <c>2</c> for the removed <c>Expedited</c> and
    /// <c>Segmented</c> members, and a caller following an out-of-date migration note could pass
    /// the same. Throwing names the problem where it happens instead of leaving a wrong transport
    /// to be diagnosed on the wire.
    /// <para>
    /// The message is careful not to say the removed members did nothing. They chose no codec,
    /// but at or above <see cref="CanOpenNodeOptions.SdoBlockThresholdBytes"/> they suppressed
    /// the <see cref="SdoTransferMode.Auto"/>-to-<see cref="SdoTransferMode.Block"/> switch. A
    /// caller who was relying on that and merely drops the argument moves onto block transfer,
    /// which is the one migration step that can hang against a peer without block support — so
    /// the message names the threshold rather than only saying "drop it".
    /// </para>
    /// </remarks>
    private static void ValidateTransferMode(SdoTransferMode mode, string paramName)
    {
        if (mode is not (SdoTransferMode.Auto or SdoTransferMode.Block))
        {
            throw new ArgumentOutOfRangeException(paramName, mode,
                $"Unknown {nameof(SdoTransferMode)} value. Only {nameof(SdoTransferMode.Auto)} " +
                $"and {nameof(SdoTransferMode.Block)} are defined. The removed Expedited (1) and " +
                "Segmented (2) members never chose a codec -- that follows from the payload " +
                "length -- so below CanOpenNodeOptions.SdoBlockThresholdBytes the argument can " +
                "simply be dropped and the same frames go out. At or above the threshold they " +
                "did have one effect: they suppressed the Auto-to-Block switch, so a download " +
                "that relied on that must raise SdoBlockThresholdBytes to keep the classic " +
                "transport rather than drop the argument alone.");
        }
    }

    /// <inheritdoc />
    public Task<byte[]> SdoUploadAsync(byte serverNodeId, ushort index, byte subindex,
        SdoTransferMode mode = SdoTransferMode.Auto,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        CanOpenCobId.ValidateNodeId(serverNodeId);
        ValidateTransferMode(mode, nameof(mode));
        EnsurePeerSdoAccess(serverNodeId, index, subindex);
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        RegisterSdoCancellation(tcs, cancellationToken, serverNodeId);
        if (mode == SdoTransferMode.Block)
        {
            // Block upload path — a separate client-side session shape from expedited /
            // segmented, but ownership rules (one in-flight transfer per remote server) are
            // shared with the existing SDO client and enforced inside BeginSdoBlockUpload.
            _actor.Post(() => BeginSdoBlockUpload(serverNodeId, index, subindex, tcs));
        }
        else
        {
            // For Auto uploads we do not know the size up front, so the classic client is the
            // conservative default; the SDO server chooses expedited-vs-segmented for us and
            // the client handles both response encodings transparently. The Auto → Block
            // auto-switch is applied on the *download* path where we know the payload length.
            _actor.Post(() => BeginSdoUpload(serverNodeId, index, subindex, tcs));
        }
        return tcs.Task;
    }

    /// <inheritdoc />
    public Task SdoDownloadAsync(byte serverNodeId, ushort index, byte subindex,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
        => SdoDownloadAsync(serverNodeId, index, subindex, data, SdoTransferMode.Auto, cancellationToken);

    /// <inheritdoc />
    public Task SdoDownloadAsync(byte serverNodeId, ushort index, byte subindex,
        ReadOnlyMemory<byte> data,
        SdoTransferMode mode = SdoTransferMode.Auto,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        CanOpenCobId.ValidateNodeId(serverNodeId);
        ValidateTransferMode(mode, nameof(mode));
        if (data.Length == 0)
        {
            // The expedited encoding steals bit-pair "n" from the CS byte to advertise how many
            // of the four payload bytes are valid (n = 4 - length, 2 bits). Length 0 collapses
            // onto the same wire encoding as length 4 (n = 0), so a length-0 expedited download
            // is indistinguishable from a length-4 one on the receiver. Rather than silently
            // sending a bogus 4-byte frame that either a) writes four zero bytes on the peer or
            // b) trips a length-mismatch abort, we reject empty downloads here with a clear
            // exception. Callers wanting to touch an OD entry without changing its value should
            // use a segmented transport (e.g. by embedding at least one meaningful byte).
            throw new ArgumentException(
                "SDO download payload must contain at least one byte; the CiA 301 expedited " +
                "encoding cannot represent a zero-length download and empty payloads are " +
                "rejected rather than being silently misencoded as four zero bytes.",
                nameof(data));
        }
        EnsurePeerSdoAccess(serverNodeId, index, subindex);
        var payload = data.ToArray();
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        RegisterSdoCancellation(tcs, cancellationToken, serverNodeId);

        // Auto-select rules (FR-CO-004):
        //   * Block was explicitly requested → always block.
        //   * Auto and payload ≥ SdoBlockThresholdBytes → block.
        //   * Auto and payload < threshold → classic client, which picks expedited for 1..4
        //     bytes and segmented above that in BuildDownloadInit. That split is dictated by
        //     CiA 301 §7.2.4.3.3/§7.2.4.3.5 and is deliberately not caller-selectable.
        bool useBlock = mode == SdoTransferMode.Block
                        || (mode == SdoTransferMode.Auto && payload.Length >= _options.SdoBlockThresholdBytes);
        if (useBlock)
        {
            _actor.Post(() => BeginSdoBlockDownload(serverNodeId, index, subindex, payload, tcs));
        }
        else
        {
            _actor.Post(() => BeginSdoDownload(serverNodeId, index, subindex, payload, tcs));
        }
        return tcs.Task;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // From the pump, this call ends the delivery whether it wins the disposal or not: the
        // other caller cannot stop the pump before its own join has run out.
        if (OnEventPump) Volatile.Write(ref _pumpStopRequested, true);
        if (!BeginDispose())
        {
            // Another caller won. From the actor this call still has to leave the transfers ended:
            // the winner's cleanup is queued behind the callback this one is made from, and the
            // callback may be waiting for it. (Idempotent; the winner runs it again.)
            if (_actor.IsOnCurrentActor) CleanUpOnActor();
            return;
        }
        // A subscriber that disposes the node runs on the thread of the task being joined: the
        // reader when it reports a failed subscription, the event pump when it delivers an event.
        // Neither can finish while it waits for itself, so it does not wait; the task ends when
        // the subscriber returns. (The actor's own reentrant Dispose is ProtocolActor's.)
        if (!OnReader)
        {
            try { _readerTask.Wait(DisposeJoinTimeout); } catch (AggregateException) { /* observed via task; not fatal */ }
        }

        // Complete the queue so the pump exits after draining anything still queued. An event
        // accepted before this point is delivered (unless the subscriber itself hangs); one that
        // arrives afterwards is dropped, timeout and EMCY included. Nothing is delivered past
        // Dispose.
        CompleteEventQueue();
        // From the pump the events still queued are not delivered once this subscriber returns
        // (the flag is set above), and the pump is not waited for.
        if (!OnEventPump)
        {
            try { _eventPumpTask.Wait(DisposeJoinTimeout); } catch (AggregateException) { /* observed via task; not fatal */ }
            StopPumpIfStillRunning();
        }

        FinishDispose();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        // From inside one of the node's own callbacks (a subscriber on the pump, on the reader's
        // failure report or on the actor) the caller holds the thread the waits below would need,
        // and the continuation of an awaited join would run elsewhere, away from the context
        // Dispose recognises. The blocking path knows how to not wait for itself, and the caller
        // is on a node thread already, so nothing is lost by taking it.
        if (OnEventPump || OnReader || OnDisposal || _actor.IsOnCurrentActor)
        {
            Dispose();
            return default;
        }

        return DisposeCoreAsync();
    }

    private async ValueTask DisposeCoreAsync()
    {
        // A second caller awaits the disposal that is running: returning at once would tell it
        // that producers are stopped and the owned service released while the first caller is
        // still waiting for the reader.
        if (!BeginDispose())
        {
            await _disposeDone.Task.ConfigureAwait(false);
            return;
        }
        await JoinAsync(_readerTask).ConfigureAwait(false);
        CompleteEventQueue();
        await JoinAsync(_eventPumpTask).ConfigureAwait(false);
        StopPumpIfStillRunning();
        FinishDispose();
    }

    private static readonly TimeSpan DisposeJoinTimeout = TimeSpan.FromSeconds(2);

    /// <summary>A subscriber that kept the pump past the join timeout does not get the events still
    /// queued behind it: nothing is delivered past Dispose.</summary>
    private void StopPumpIfStillRunning()
    {
        if (!_eventPumpTask.IsCompleted) Volatile.Write(ref _pumpStopRequested, true);
    }

    /// <summary>Waits for <paramref name="task"/> for at most <see cref="DisposeJoinTimeout"/>
    /// without holding a thread. Neither task joined here faults (each reports its own failures
    /// through <see cref="BackgroundExceptionOccurred"/>), so there is nothing to observe.</summary>
    private static async Task JoinAsync(Task task)
    {
        using var cts = new CancellationTokenSource();
        await Task.WhenAny(task, Task.Delay(DisposeJoinTimeout, cts.Token)).ConfigureAwait(false);
        cts.Cancel();
    }

    /// <summary>True on the thread that is delivering an event to a subscriber.</summary>
    private bool OnEventPump => Callbacks.Contains(CallbackKind.Delivering, this);

    /// <summary>True on the thread that is finishing the disposal: a subscriber the actor calls
    /// there (its shutdown-timeout report) is waited for by that disposal, so it cannot wait for it.</summary>
    private bool OnDisposal => Callbacks.Contains(CallbackKind.Finishing, this);

    /// <summary>True on the thread of the reader task while it reports a failed subscription.</summary>
    private bool OnReader => Callbacks.Contains(CallbackKind.Reporting, this);

    // The nodes whose callbacks the current thread is inside, per kind. A set, not one slot: a
    // subscriber of A may dispose B, and B's own callback may then ask A to dispose again, with
    // both of them active on the same thread at once.
    private enum CallbackKind { Delivering, Reporting, Finishing }

    private static class Callbacks
    {
        [ThreadStatic]
        private static System.Collections.Generic.List<CanOpenNode>?[]? s_nodes;

        public static void Enter(CallbackKind kind, CanOpenNode node) => Of(kind).Add(node);

        // Entering and leaving are paired, and the nodes of one thread are all the same object
        // when they repeat, so removing the first match is removing the right one.
        public static void Leave(CallbackKind kind, CanOpenNode node) => Of(kind).Remove(node);

        public static bool Contains(CallbackKind kind, CanOpenNode node)
        {
            var list = s_nodes?[(int)kind];
            if (list is null) return false;
            foreach (var marked in list)
            {
                if (ReferenceEquals(marked, node)) return true;
            }
            return false;
        }

        private static System.Collections.Generic.List<CanOpenNode> Of(CallbackKind kind)
        {
            var all = s_nodes ??= new System.Collections.Generic.List<CanOpenNode>?[3];
            return all[(int)kind] ??= new System.Collections.Generic.List<CanOpenNode>(2);
        }
    }

    /// <summary>Flips the disposed flag and posts the cleanup; false when already disposed.</summary>
    private bool BeginDispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return false;
        try { _readerCts.Cancel(); } catch (AggregateException) { /* a registered callback threw; nothing else to do */ }

        // On the actor already (a subscriber disposing from ApplicationReset, say) the cleanup runs
        // here: a post would wait for that subscriber to return, and the call would hand back
        // with the transfers still open.
        try
        {
            if (_actor.IsOnCurrentActor) CleanUpOnActor();
            else _actor.Post(CleanUpOnActor);
        }
        catch (ObjectDisposedException)
        {
            // actor already gone; nothing more to do
        }
        return true;
    }

    /// <summary>Stops the producers and consumers and fails every open transfer. Actor-only.</summary>
    private void CleanUpOnActor()
    {
        _heartbeatProducer.Dispose();
        _heartbeatConsumer.Dispose();
        _syncProducerHandle?.Dispose();
        _syncProducerHandle = null;
        DisposePdoRuntime();
        _lifeGuardingDeadline?.Dispose();
        _lifeGuardingDeadline = null;
        CancelFlyingMasterDeadline();
        CancelBootUp();

        Release(_sdoServer?.Deadline);
        _sdoServer = null;
        foreach (var kv in _sdoClients)
        {
            Release(kv.Value.Deadline);
            kv.Value.Tcs.TrySetException(new ObjectDisposedException(nameof(CanOpenNode)));
        }
        _sdoClients.Clear();

        Release(_sdoBlockServer?.Deadline);
        _sdoBlockServer = null;
        foreach (var kv in _sdoBlockClients)
        {
            Release(kv.Value.Deadline);
            kv.Value.Tcs.TrySetException(new ObjectDisposedException(nameof(CanOpenNode)));
        }
        _sdoBlockClients.Clear();

        foreach (var kv in _nodeGuardingConsumers)
        {
            Release(kv.Value.PollHandle);
            Release(kv.Value.LifeTimeDeadline);
        }
        _nodeGuardingConsumers.Clear();
    }

    private static void Release(IDisposable? handle) => handle?.Dispose();

    private void FinishDispose()
    {
        Callbacks.Enter(CallbackKind.Finishing, this);
        try
        {
            _subscription.Dispose();
            _actor.Dispose();
            _readerCts.Dispose();

            if (_ownsService) _service.Dispose();
        }
        finally
        {
            Callbacks.Leave(CallbackKind.Finishing, this);
            // Whatever a Dispose above threw, a caller waiting for this disposal is released.
            _disposeDone.TrySetResult(true);
        }
    }

    // =========================================================================================
    // Subscription reader -- hands off to the actor loop.
    // =========================================================================================
    private async Task RunReaderAsync()
    {
        try
        {
            await foreach (var frameEvent in _subscription.Frames.WithCancellation(_readerCts.Token)
                .ConfigureAwait(false))
            {
                var frame = frameEvent.Frame;
                if (frame.IsExtendedFrame) continue;
                uint id = (uint)frame.ID;
                // Node-guarding (FR-CO-009) piggy-backs on the heartbeat COB-ID and uses a
                // *remote* frame from consumer → producer. Preserve the RTR flag through to
                // the actor loop so HandleIncoming can distinguish an RTR poll from a genuine
                // heartbeat / node-guarding data frame that happens to share the same COB-ID.
                bool isRtr = frame.IsRemoteFrame;
                // The copy stays, and #103 asked for the reason rather than the reflex: this
                // array is captured into a post that runs later on the actor loop, and the
                // handlers below take byte[] and hold it -- an SDO segment lands in a transfer
                // that spans many frames, an RPDO's bytes are unpacked into the object
                // dictionary. Removing it means threading ReadOnlyMemory<byte> through the whole
                // dispatch, which is a different change from the hot-path tidy-up #103 describes.
                //
                // Unlike the J1939-TP reader one layer over, there is no cheap filter to put in
                // front of it: HandleIncoming's first act is to classify the COB-ID, and it needs
                // the payload for almost every class it can land in.
                var data = frame.Data.ToArray();
                _actor.Post(() => HandleIncoming(id, data, isRtr));
            }
        }
        catch (OperationCanceledException) { /* Dispose */ }
        catch (Exception ex)
        {
            // A subscriber of the report may dispose the node; see Dispose.
            Callbacks.Enter(CallbackKind.Reporting, this);
            try { RaiseBackgroundException(ex); }
            finally { Callbacks.Leave(CallbackKind.Reporting, this); }
        }
    }

    // =========================================================================================
    // Event pump — dispatches queued RPDO / EMCY / heartbeat / SYNC / NMT events on its own
    // task so that a slow subscriber never blocks the actor loop or the RX reader. Runs a
    // single reader so events are delivered in enqueue order.
    // =========================================================================================
    private async Task RunEventPumpAsync()
    {
        while (true)
        {
            await _eventWake.WaitAsync().ConfigureAwait(false);
            // One signal covers every event queued by the time we look. A subscriber that
            // throws is reported and the loop continues: a timeout or EMCY already waiting
            // must still be delivered. Anything outside the delegate is still a bug in the pump.
            // Marks the thread for the whole batch: nothing in it awaits, so a subscriber that
            // disposes the node runs on this very thread (see Dispose).
            Callbacks.Enter(CallbackKind.Delivering, this);
            try
            {
                while (!Volatile.Read(ref _pumpStopRequested) && TryDequeueEvent() is { } raise)
                {
                    try
                    {
                        raise();
                    }
                    catch (Exception ex)
                    {
                        RaiseBackgroundException(ex);
                    }
                }
                // Stopped with events still queued: they are dropped, not left linked to a node
                // nobody will deliver for again.
                if (Volatile.Read(ref _pumpStopRequested))
                {
                    Action? skipped;
                    do { skipped = TryDequeueEvent(); }
                    while (skipped is not null);
                }
            }
            finally
            {
                Callbacks.Leave(CallbackKind.Delivering, this);
            }
            // The queue was just drained. Closure is the completed flag alone: an event
            // accepted before completion is still in the list and was delivered above, and
            // one accepted after completion never enters the list.
            if (EventQueueClosed()) return;
        }
    }

    private void EnqueueEvent(Action raise, bool critical = false)
        => EnqueueEvent(raise, critical, key: null, emcyProducer: -1);

    /// <param name="raise">Delivers the event to its subscribers.</param>
    /// <param name="critical">A timeout or an EMCY: never discarded to make room.</param>
    /// <param name="key">For a critical event: an identical event still waiting absorbs this
    /// one. Null for events that must each be delivered.</param>
    /// <param name="emcyProducer">The EMCY producer this event belongs to, or -1. At most
    /// <see cref="CanOpenNodeOptions.EventQueueCapacity"/> events per producer wait at once.</param>
    /// <param name="producer">The node-id this event is about, or -1. An event that is not folded
    /// moves that node's sequence on, which is what stops a later identical one from folding
    /// into an earlier one across it.</param>
    private void EnqueueEvent(Action raise, bool critical, EventKey? key, int emcyProducer, int producer = -1)
    {
        int reportOverflowOf = -1;
        bool discarded = false;
        lock (_eventLock)
        {
            if (_eventPumpCompleted) return;
            if (!critical)
            {
                // Drop the oldest ordinary event, never a timeout or an EMCY sitting in front
                // of it. The newcomer still takes a slot, so the ordinary count stays at the cap.
                if (_pendingNonCritical >= _options.EventQueueCapacity && _oldestNonCritical is { } drop)
                {
                    var next = NextNonCritical(drop.Next);
                    _pendingEvents.Remove(drop);
                    _pendingNonCritical--;
                    _oldestNonCritical = next;
                    // What separated two identical critical events may be exactly what was
                    // dropped: the handler will never see it, so they are one event now.
                    if (drop.Value.Producer is >= 0 and var dropped)
                        ReconcileProducer(dropped);
                }
                var added = _pendingEvents.AddLast(new PendingEvent(raise, critical: false, key: null, emcyProducer: -1, producer, ++_enqueueOrdinal));
                _oldestNonCritical ??= added;
                _pendingNonCritical++;
                if (producer >= 0) _lastForProducer[producer] = added;
            }
            else
            {
                // An identical event is already waiting: the handler will see it, and nothing
                // it could learn from a second copy is lost (#201).
                if (key is { } k && producer >= 0
                    && _pendingKeyed.TryGetValue(k.Identity, out var waiting)
                    && (_lastForProducer[producer] is not { } newest || newest.Value.Ordinal <= waiting.Value.Ordinal))
                {
                    _coalescedEventCount++;
                    // A timeout reports the consumer's current settings. If they changed while it
                    // waited, the handler should see the newest, not the ones that were superseded;
                    // an EMCY that folds is identical, so there is nothing to update.
                    if (k.IsTimeout) waiting.Value = waiting.Value.WithRaise(raise);
                    return;
                }
                if (emcyProducer >= 0)
                {
                    if (_pendingEmcyPerProducer[emcyProducer] >= _options.EventQueueCapacity)
                    {
                        _emcyOverflowCount++;
                        if (!_emcyOverflowReported[emcyProducer])
                        {
                            _emcyOverflowReported[emcyProducer] = true;
                            reportOverflowOf = emcyProducer;
                        }
                        discarded = true;
                    }
                    else
                    {
                        _pendingEmcyPerProducer[emcyProducer]++;
                    }
                }
                if (!discarded)
                {
                    // A timeout is looked up by producer but does not itself say anything new about
                    // the producer's state, so it is not recorded against it.
                    int tracked = key is { IsTimeout: true } ? -1 : producer;
                    var queued = _pendingEvents.AddLast(new PendingEvent(raise, critical: true, key, emcyProducer, tracked, ++_enqueueOrdinal));
                    if (key is { } keyed) _pendingKeyed[keyed.Identity] = queued;
                    if (tracked >= 0) _lastForProducer[tracked] = queued;
                }
            }
            if (!discarded)
            {
                _submittedEventCount++;
                SignalEventQueue();
            }
        }

        // Outside the lock: the subscriber runs synchronously on this thread.
        if (reportOverflowOf >= 0)
            RaiseBackgroundException(new InvalidOperationException(
                $"EMCY producer 0x{reportOverflowOf:X2} has {_options.EventQueueCapacity} emergencies waiting for a " +
                "slow EmcyReceived handler; further ones from it are discarded until its backlog drains."));
    }

    /// <summary>Wakes the pump if it is idle. Called under <see cref="_eventLock"/>. A second
    /// call while a wake is already pending does nothing: the pump drains the whole queue,
    /// so one signal is enough and dropped events leave nothing behind.</summary>
    private void SignalEventQueue()
    {
        if (_eventWake.CurrentCount == 0)
            _eventWake.Release();
    }

    private bool EventQueueClosed()
    {
        lock (_eventLock)
            return _eventPumpCompleted;
    }

    private Action? TryDequeueEvent()
    {
        lock (_eventLock)
        {
            var node = _pendingEvents.First;
            if (node is null) return null;
            _pendingEvents.RemoveFirst();
            // A newer identical event may have replaced this one's entry; only remove our own.
            if (node.Value.Key is { } key && _pendingKeyed.TryGetValue(key.Identity, out var entry)
                && ReferenceEquals(entry, node))
                _pendingKeyed.Remove(key.Identity);
            if (node.Value.Producer is >= 0 and var about && ReferenceEquals(_lastForProducer[about], node))
                _lastForProducer[about] = null;
            if (node.Value.EmcyProducer is >= 0 and var producer
                && --_pendingEmcyPerProducer[producer] == 0)
                _emcyOverflowReported[producer] = false;
            if (!node.Value.Critical)
            {
                _pendingNonCritical--;
                // The dequeued node was the first non-critical in the list, so the oldest
                // pointer moves to the next one rather than being left on a removed node.
                _oldestNonCritical = NextNonCritical(_pendingEvents.First);
            }
            return node.Value.Raise;
        }
    }

    /// <summary>
    /// Re-applies the folding rule to what is waiting for <paramref name="producer"/> after an
    /// event about it was dropped to make room. Walks the queue in order: an identical critical
    /// event that no heartbeat, guarding response or EMCY now separates from an earlier one is
    /// folded into it, exactly as it would have been had the dropped event never been queued.
    /// Also records the newest event left that says something about the producer's state. Called
    /// under <see cref="_eventLock"/>; cost is one pass over the queue, and only when the event
    /// dropped was about a producer.
    /// </summary>
    private void ReconcileProducer(int producer)
    {
        var absorbers = new Dictionary<(byte, byte, ulong), LinkedListNode<PendingEvent>>();
        var newest = new Dictionary<(byte, byte, ulong), LinkedListNode<PendingEvent>>();
        LinkedListNode<PendingEvent>? lastTracked = null;

        for (var node = _pendingEvents.First; node is not null;)
        {
            var next = node.Next;
            var waiting = node.Value;
            if (waiting.Key is { } key && key.ProducerId == producer)
            {
                if (absorbers.TryGetValue(key.Identity, out var survivor))
                {
                    _pendingEvents.Remove(node);
                    _coalescedEventCount++;
                    // As when folding at enqueue time: the survivor reports the newest settings.
                    if (key.IsTimeout) survivor.Value = survivor.Value.WithRaise(waiting.Raise);
                    // A survivor always remains for this key, so the producer's count cannot reach
                    // zero here and the burst report stays as it was.
                    if (waiting.EmcyProducer is >= 0 and var emcy)
                        --_pendingEmcyPerProducer[emcy];
                }
                else
                {
                    bool tracked = !key.IsTimeout;
                    if (tracked)
                    {
                        absorbers.Clear();
                        lastTracked = node;
                    }
                    absorbers[key.Identity] = node;
                    newest[key.Identity] = node;
                }
            }
            else if (waiting.Producer == producer)
            {
                absorbers.Clear();
                lastTracked = node;
            }
            node = next;
        }

        foreach (var entry in newest)
            _pendingKeyed[entry.Key] = entry.Value;
        _lastForProducer[producer] = lastTracked;
    }

    private static LinkedListNode<PendingEvent>? NextNonCritical(LinkedListNode<PendingEvent>? node)
    {
        while (node is { Value.Critical: true }) node = node.Next;
        return node;
    }

    private void CompleteEventQueue()
    {
        lock (_eventLock)
        {
            _eventPumpCompleted = true;
            SignalEventQueue();
        }
    }

    /// <summary>Test seam: queues <paramref name="raise"/> the way a late actor callback would.
    /// After the queue is completed this returns without running <paramref name="raise"/>.</summary>
    internal void SubmitEventForTests(Action raise) => EnqueueEvent(raise);

    /// <summary>Test seam: events accepted into the dispatch queue since construction, counting
    /// an ordinary event that was later dropped to make room. Stops moving once the queue is
    /// completed.</summary>
    internal long SubmittedEventCount
    {
        get { lock (_eventLock) return _submittedEventCount; }
    }

    /// <summary>Test seam: events waiting for the dispatcher, not including one whose handler
    /// is already running.</summary>
    internal int QueuedEventCount
    {
        get { lock (_eventLock) return _pendingEvents.Count; }
    }

    /// <summary>Test seam: critical events absorbed by an identical one already waiting.</summary>
    internal long CoalescedEventCount
    {
        get { lock (_eventLock) return _coalescedEventCount; }
    }

    /// <summary>Test seam: EMCYs discarded because their producer already had
    /// <see cref="CanOpenNodeOptions.EventQueueCapacity"/> waiting.</summary>
    internal long EmcyOverflowCount
    {
        get { lock (_eventLock) return _emcyOverflowCount; }
    }

    private readonly struct PendingEvent
    {
        public PendingEvent(Action raise, bool critical, EventKey? key, int emcyProducer, int producer, long ordinal)
        {
            Raise = raise;
            Critical = critical;
            Key = key;
            EmcyProducer = emcyProducer;
            Producer = producer;
            Ordinal = ordinal;
        }

        public PendingEvent WithRaise(Action raise)
            => new(raise, Critical, Key, EmcyProducer, Producer, Ordinal);

        public Action Raise { get; }
        public bool Critical { get; }
        public EventKey? Key { get; }
        public int EmcyProducer { get; }
        public int Producer { get; }
        public long Ordinal { get; }
    }

    /// <summary>Identity of a critical event for coalescing: what it is, who raised it and, for
    /// an EMCY, the whole 8-byte payload.</summary>
    private readonly struct EventKey
    {
        private readonly byte _kind;
        private readonly byte _producer;
        private readonly ulong _payload;

        private EventKey(byte kind, byte producer, ulong payload)
        {
            _kind = kind;
            _producer = producer;
            _payload = payload;
        }

        public bool IsTimeout => _kind is 1 or 2;

        public byte ProducerId => _producer;

        public static EventKey HeartbeatTimeout(byte producer) => new(1, producer, 0);

        public static EventKey NodeGuardingTimeout(byte producer) => new(2, producer, 0);

        public static EventKey Emcy(EmcyMessage msg)
        {
            ulong payload = msg.ErrorCode | ((ulong)msg.ErrorRegister << 16);
            var mfr = msg.ManufacturerSpecific;
            for (int i = 0; i < mfr.Length && i < EmcyMessage.ManufacturerFieldLength; i++)
                payload |= (ulong)mfr[i] << (24 + 8 * i);
            return new EventKey(3, msg.ProducerNodeId, payload);
        }

        /// <summary>The fields that make two keys the same event, as a tuple so that keys can be
        /// compared and hashed without members of their own.</summary>
        public (byte, byte, ulong) Identity => (_kind, _producer, _payload);
    }

    // Self-traffic guards (#95) are per message class rather than one test at the top, because
    // only some COB-IDs identify this node as the *source*. Deliberately unguarded:
    //   * NMT 0x000 and SYNC 0x080 carry no node id, and a node is documented to act on both from
    //     its own producer -- ICanOpenNode.SyncReceived says so, and #93 silenced a node's own
    //     synchronous TPDOs by getting this wrong.
    //   * 0x600 + id names the destination, so such a frame is ours to serve regardless of sender.
    //   * an RPDO's COB-ID is whatever the application configured, possibly on purpose our own
    //     TPDO; overriding that from here is the narrowing #93 had to take back out.
    private void HandleIncoming(uint cobId, byte[] data, bool isRtr)
    {
        try
        {
            if (cobId == CanOpenCobId.NmtCommand)
            {
                HandleNmtCommand(data);
                return;
            }
            // Flying-master services are broadcasts with no node-id in the CAN-ID. A claim names
            // its sender in the payload, which is how a node ignores the echo of its own claim.
            if (cobId is CanOpenCobId.FlyingMasterClaim or CanOpenCobId.FlyingMasterTrigger
                or CanOpenCobId.FlyingMasterDetect or CanOpenCobId.FlyingMasterForce)
            {
                if (!isRtr) HandleFlyingMasterFrame(cobId, data);
                return;
            }
            // SYNC on the COB-ID configured in 1005h (0x080 unless a device description or a
            // master moved it).
            if (cobId == _syncCobId && !isRtr)
            {
                HandleSync();
                return;
            }
            // A remote frame is either a PDO read request for our valid TPDOs on that COB-ID
            // (CiA 301 §7.2.2.5.2) — two valid records may share one, and then each is read
            // (Codex on #133) — or a node-guarding poll addressed to us; nothing else answers an RTR.
            if (isRtr)
            {
                bool requested = false;
                for (int n = 1; n <= Co.PdoCount; n++)
                {
                    if (_tpdos[n] is { Valid: true } tpdo && tpdo.CobId == cobId)
                    {
                        HandleTpdoRtr(tpdo);
                        requested = true;
                    }
                }
                if (requested) return;
                if (cobId == CanOpenCobId.Heartbeat(_nodeId)) HandleNodeGuardingRtrForSelf();
                return;
            }
            // A valid RPDO's COB-ID is whatever the application or a master configured, possibly
            // on purpose our own TPDO (#93); an explicitly configured consumer outranks the
            // range-based guesses below. Two valid RPDOs may share a COB-ID — the dictionary holds
            // both records and CiA 301 does not forbid it — and then each is actuated (Codex on #133).
            bool consumed = false;
            for (int n = 1; n <= Co.PdoCount; n++)
            {
                if (_rpdos[n] is { Valid: true } rpdo && rpdo.CobId == cobId)
                {
                    HandleRpdo(rpdo, data);
                    consumed = true;
                }
            }
            if (consumed) return;
            // A frame that matched no RPDO of this node is not unpacked into this dictionary.
            // ObserveForeignPdoAsync splits a peer PDO into the caller's sink.
            // EMCY 0x081..0x0FF.
            if (cobId is >= 0x081 and <= 0x0FF)
            {
                // Our own, on a bus that echoes (#95): the EMCY COB-ID names the *producer*, so
                // this is the node's own emergency coming back. Raising it through EmcyReceived
                // would report us to ourselves as a peer in fault.
                if (cobId == _emcyCobId) return;
                HandleEmcy(cobId, data);
                return;
            }
            // Heartbeat / bootup / node-guarding 0x701..0x77F. Both heartbeat producers and
            // node-guarding producers share this COB-ID range; the distinction is: an RTR
            // targeting *our* node-id asks the node-guarding producer to reply, and a data
            // frame is either a heartbeat / bootup broadcast from a peer or a node-guarding
            // response we requested. Bit 7 of the data byte carries the node-guarding toggle
            // bit — the existing heartbeat consumer already masks it off via <c>data[0] &amp; 0x7F</c>.
            if (cobId is >= 0x701 and <= 0x77F)
            {
                byte producer = (byte)(cobId - CanOpenCobId.HeartbeatBase);
                // Our own heartbeat / bootup / node-guarding response, echoed back (#95). Three
                // things it must not swallow:
                //   * an *RTR* at this COB-ID, which is a consumer polling us and was answered
                //     above rather than here;
                //   * a node-guarding consumer registered for our own id;
                //   * a heartbeat consumer registered for our own id.
                // Both of those APIs take a node id and accept the local one, and on an echo bus
                // that is a working configuration -- the node's own producer feeds its own
                // consumer. Dropping the frame starves the deadline and the timeout fires while
                // the frames are arriving (#119, Codex). Same rule the RPDO branch below already
                // states: an explicitly configured consumer outranks a guess about the sender.
                if (producer == _nodeId
                    && !_nodeGuardingConsumers.ContainsKey(producer)
                    && !_heartbeatConsumer.IsWatching(producer))
                    return;
                // Consumer role (FR-CO-009): if we have a node-guarding consumer registered
                // for this producer, treat the incoming data frame as a node-guarding reply
                // (toggle + state). Otherwise fall through to the heartbeat consumer, which is
                // wire-compatible (both frames are 1 byte on 0x700 + node-id, and the toggle
                // bit is bit 7). Node-guarding consumers take precedence because a node that
                // was set up for node-guarding still wants heartbeat handling elsewhere gated.
                if (_nodeGuardingConsumers.ContainsKey(producer))
                {
                    HandleNodeGuardingResponse(producer, data);
                    // Heartbeats and boot-up use this COB-ID too. Returning here used to hide
                    // them from the standby watch of the active master and from boot-up, so the
                    // watch expired while the master was still producing and an assigned slave
                    // stayed unseen.
                    if (data.Length >= 1)
                    {
                        byte guardedState = (byte)(data[0] & 0x7F);
                        NoteSlaveNmtState(producer, guardedState);
                        _heartbeatConsumer.NoteReceived(producer);
                    }
                    return;
                }
                HandleHeartbeat(cobId, data);
                return;
            }
            // SDO client request: 0x600 + our nodeId — targeted at *our* SDO server.
            if (cobId == CanOpenCobId.SdoRx(_nodeId))
            {
                // Block-download server-side session (if any) intercepts incoming frames while
                // it is in a "receiving segments" phase, because a raw block segment's byte 0
                // overlaps ordinary segmented-download client-segment / control-frame values.
                // The block-server handler returns true if it consumed the frame; false means
                // fall through to the plain SDO server.
                if (HandleSdoServerRequestBlock(data)) return;
                HandleSdoServerRequest(data);
                return;
            }
            // SDO server response: 0x580 + serverNodeId — our SDO *client* is the recipient.
            if (cobId is >= CanOpenCobId.SdoTxBase + CanOpenCobId.MinNodeId
                and <= CanOpenCobId.SdoTxBase + CanOpenCobId.MaxNodeId)
            {
                byte serverNodeId = (byte)(cobId - CanOpenCobId.SdoTxBase);
                // Neither SDO direction is self-guarded (#95), and the reason is the same both
                // ways: nothing here reports a peer to the application, so there is nothing for
                // an echo to misreport. 0x600 + id names the *destination*, so such a frame is
                // ours to serve whoever sent it. 0x580 + id does name us as the sender, but both
                // client handlers open with a lookup keyed on that id -- _sdoClients and
                // _sdoBlockClients -- and an id with no session is already dropped.
                //
                // A guard here was written and taken back out: it could only subtract. When no
                // session is keyed to our own id it does what the lookup already does, and when
                // one is -- a node running an SDO transfer against its own server on an echo bus
                // -- it drops the very response that transfer is waiting for.
                // Symmetric to the server-side path above: while a client-side block session
                // (upload or download) is in a "receiving segments" phase, incoming frames on
                // this COB-ID are block segments rather than ordinary SDO responses.
                if (HandleSdoClientResponseBlock(serverNodeId, data)) return;
                HandleSdoClientResponse(serverNodeId, data);
                return;
            }
        }
        catch (Exception ex)
        {
            RaiseBackgroundException(ex);
        }
    }

    // =========================================================================================
    // NMT slave state machine (FR-CO-007)
    // =========================================================================================
    private void HandleNmtCommand(byte[] data)
    {
        if (data.Length < 2) return;
        var cmd = (NmtCommand)data[0];
        byte target = data[1];
        // 0 = broadcast (all nodes); otherwise apply only when the target is us.
        // Raise NmtCommandReceived only for matching targets — ICanOpenNode documents the
        // event for commands that address this node (or broadcast), not every peer NMT on a
        // shared bus.
        bool forUs = target == 0 || target == _nodeId;
        if (!forUs) return;
        if (ShouldIgnoreOwnNmt(cmd, target)) return;
        // The cold broadcast is this node's own reset. Counting it here means the send
        // completion does not apply that reset a second time.
        if (target == 0 && cmd == NmtCommand.ResetCommunication && _coldResetPending)
            _coldResetPending = false;
        RaiseNmtCommandReceived(cmd, target);

        // The transitions themselves live in CanOpenNode.CommunicationProfile.cs, next to the
        // reset that restores the communication-profile objects (CiA 301 §7.3.2).
        switch (cmd)
        {
            case NmtCommand.Start:
                ApplyNmtTransition(NmtState.Operational);
                break;
            case NmtCommand.Stop:
                ApplyNmtTransition(NmtState.Stopped);
                break;
            case NmtCommand.EnterPreOperational:
                ApplyNmtTransition(NmtState.PreOperational);
                break;
            case NmtCommand.ResetNode:
                PerformNmtReset(communicationOnly: false);
                break;
            case NmtCommand.ResetCommunication:
                PerformNmtReset(communicationOnly: true);
                break;
        }
    }

    /// <summary>
    /// The active flying master does not obey NMT addressed to its own node-id, and it does not
    /// reset or stop itself because a broadcast it sent came back. A broadcast Start still
    /// applies: that is how a simultaneous start is specified, and self-start is also applied
    /// locally when bit 2 of <c>1F80h</c> allows it.
    /// </summary>
    private bool ShouldIgnoreOwnNmt(NmtCommand cmd, byte target)
    {
        // Active, and the short confirm that stays active while the timeslot runs again.
        bool holding = _flyingMasterRole == FlyingMasterRole.Active || _confirmingActiveMaster;
        if (target == _nodeId && holding)
            return true;

        bool reset = cmd is NmtCommand.ResetNode or NmtCommand.ResetCommunication;
        if (target == 0 && reset && _ignoreBroadcastResetEcho)
        {
            _ignoreBroadcastResetEcho = false;
            return true;
        }

        if (target != 0 || !holding)
            return false;

        return reset || cmd == NmtCommand.Stop;
    }

    // =========================================================================================
    // SYNC (FR-CO-010)
    // =========================================================================================
    // HandleSync lives in CanOpenNode.Pdo.cs: the SYNC is the trigger of every synchronous PDO.

    private void ScheduleSyncProducerTick()
    {
        if (_syncProducerInterval <= TimeSpan.Zero) return;
        _syncProducerHandle = _actor.Schedule(_syncProducerInterval, () =>
        {
            try
            {
                if (_disposed != 0) return;
                // CiA 301 Table 37: SYNC is not active in Stopped; the producer keeps its cycle
                // and resumes transmitting when the node leaves Stopped.
                if (_state is NmtState.Stopped or NmtState.Initializing) return;
                _ = SendControlFrame(_syncCobId, Array.Empty<byte>());
            }
            finally
            {
                if (_disposed == 0 && _syncProducerInterval > TimeSpan.Zero)
                    ScheduleSyncProducerTick();
            }
        });
    }

    // =========================================================================================
    // EMCY (FR-CO-011)
    // =========================================================================================
    private void HandleEmcy(uint cobId, byte[] data)
    {
        if (data.Length < EmcyMessage.WireSize) return;
        byte producer = (byte)(cobId - CanOpenCobId.EmcyBase);
        var msg = EmcyMessage.Decode(producer, data);
        RaiseEmcyReceived(msg, DateTime.UtcNow);
    }

    // =========================================================================================
    // Heartbeat (FR-CO-008)
    // =========================================================================================

    /// <summary>The NMT state a heartbeat or guarding state byte (bit 7 already masked) reports;
    /// false for the values CiA 301 §7.2.8.3.2 reserves.</summary>
    internal static bool TryDecodeHeartbeatState(byte stateByte, out NmtState state)
    {
        switch (stateByte)
        {
            case 0x00: state = NmtState.Initializing; return true; // Bootup frame.
            case 0x04: state = NmtState.Stopped; return true;
            case 0x05: state = NmtState.Operational; return true;
            case 0x7F: state = NmtState.PreOperational; return true;
            default: state = default; return false;
        }
    }

    private void HandleHeartbeat(uint cobId, byte[] data)
    {
        if (data.Length < 1) return;
        byte producer = (byte)(cobId - CanOpenCobId.HeartbeatBase);
        // Bit 7 is the node-guarding toggle; a heartbeat carries 0 there (§7.2.8.3.2.2, "r:
        // reserved (always 0)"), and a guarding reply is routed to HandleNodeGuardingResponse
        // before it gets here, so the bit is masked rather than interpreted.
        byte stateByte = (byte)(data[0] & 0x7F);
        // A reserved state byte still shows the producer alive and is still recorded for the NMT
        // master, but it is not reported: as Initializing it would be indistinguishable from a
        // boot-up and read as a restart that did not happen (#255).
        // Bit 7 is reserved and always 0 in a heartbeat (§7.2.8.3.2.2), so a frame that sets it is
        // not one: a bystander's guarding reply (toggle) or a malformed frame. It is not reported,
        // which also keeps 0x80 (state 0 with the toggle set) from reading as a boot-up (#266).
        // It still counts as a sign of life below, as before.
        if ((data[0] & 0x80) == 0 && TryDecodeHeartbeatState(stateByte, out var state))
            RaiseHeartbeatReceived(producer, state, DateTime.UtcNow);
        NoteSlaveNmtState(producer, stateByte);
        _heartbeatConsumer.NoteReceived(producer);
    }

    // =========================================================================================
    // SDO server (FR-CO-002 / FR-CO-003 / FR-CO-001 access-check)
    // =========================================================================================
    private void HandleSdoServerRequest(byte[] data)
    {
        // CiA 301: SDO is not available in Stopped (or while still Initializing). Drop the
        // request rather than serving a transfer that should be offline.
        if (_state is NmtState.Stopped or NmtState.Initializing)
            return;

        if (data.Length == 0) return; // nothing to look at — can't even read the CS byte
        int wireLength = data.Length;
        if (data.Length < 8)
        {
            // Match the symmetric client-side handling (see HandleSdoClientResponse): pad
            // trailing-zero-stripped SDO frames to 8 bytes rather than dropping them. That is
            // right for the initiate and control frames, whose unused bytes are zero by
            // definition. It is not right for a segment, whose bytes are data: a segment shorter
            // than its own n field declares is refused below instead of being completed with
            // bytes the peer never sent (#203). Prior behaviour left a well-formed but short
            // client initiate lingering on the wire until its client-side timeout, which is
            // exactly the bug Copilot flagged for the client path — this keeps the server path
            // from having the mirror-image problem.
            var padded = new byte[8];
            Buffer.BlockCopy(data, 0, padded, 0, data.Length);
            data = padded;
        }
        byte cs = data[0];

        // Abort from the peer ends any active server session.
        if (cs == SdoFrames.CsAbort)
        {
            _sdoServer?.Deadline?.Dispose();
            _sdoServer = null;
            return;
        }

        // Client's segment for an in-flight download.
        if ((cs & 0xE0) == SdoFrames.CcsDownloadSegmentBase && _sdoServer is { InDownload: true } dl)
        {
            if (!SdoFrames.SegmentIsComplete(wireLength, cs))
            {
                SendSdoServerAbort(dl.Index, dl.Subindex, SdoAbortCode.DataTypeLengthMismatch);
                return;
            }
            HandleServerDownloadSegment(dl, data);
            return;
        }

        // Client's segment ack for an in-flight upload.
        if ((cs & 0xE0) == SdoFrames.CcsUploadSegmentBase && _sdoServer is { InDownload: false } ul)
        {
            HandleServerUploadSegmentRequest(ul, data);
            return;
        }

        var (index, subindex) = SdoFrames.ReadIndex(data);

        // Every object, the PDO communication and mapping records included, is served by the
        // generic OD path: the dictionary validates a write against the object's CiA 301 rules
        // and the runtime follows the dictionary (CanOpenNode.CommunicationProfile.cs).
        _od.TryGet(index, subindex, out var entry);

        // Upload init (client → server).
        if (cs == SdoFrames.CcsUploadInit)
        {
            HandleServerUploadInit(index, subindex, entry);
            return;
        }
        // Download init (client → server). ccs = 1 covers expedited (e = 1) and segmented
        // (e = 0), and the segmented form is legal with or without a size indicator (0x21 / 0x20).
        if ((cs & 0xE0) == SdoFrames.CcsDownloadInitExpeditedBase)
        {
            HandleServerDownloadInit(index, subindex, entry, cs, data);
            return;
        }

        // Anything else is a protocol error → abort.
        SendSdoServerAbort(index, subindex, SdoAbortCode.CommandSpecifierInvalid);
    }

    private void HandleServerUploadInit(ushort index, byte subindex, OdEntry? entry)
    {
        // Per CiA 301 §7.2.4.3.4 a fresh initiate implicitly supersedes any previously open
        // server-side transfer. Emit an abort on the wire for that stale (index, subindex) so
        // its remote client does not have to wait for its own SDO timeout to notice the
        // supersede. Done up-front so this holds even when we go on to reject the new initiate
        // below (missing entry, wrong access, ...). Classic and block sessions share the same
        // SDO TX COB-ID, so both must be cleared.
        AbortSupersededServerSession();
        AbortSupersededBlockServerSession();

        if (entry is null)
        {
            // CiA 301 Table 22 distinguishes an unknown object (0602 0000h) from an unknown
            // sub-index of a known one (0609 0011h) — the latter is also what §7.5.2.37 prescribes
            // for the reserved sub-index 04h of a PDO communication record.
            SendSdoServerAbort(index, subindex, _od.ContainsIndex(index)
                ? SdoAbortCode.SubIndexDoesNotExist : SdoAbortCode.ObjectDoesNotExist);
            return;
        }
        if ((entry.Access & OdAccess.ReadOnly) == 0)
        {
            SendSdoServerAbort(index, subindex, SdoAbortCode.AttemptReadWriteOnly);
            return;
        }

        // Snapshot the raw value under the OD's internal lock (via TryReadRaw) instead of
        // calling OdEntry.GetRawValue() on the reference we already have: a concurrent
        // application-side ObjectDictionary.WriteRaw on another thread swaps the entry's
        // backing array without any coordination with the actor loop, and the unlocked
        // copy path used to read _value.Length and then re-dereference _value byte-by-byte,
        // which can tear when the write lands between those two reads.
        if (!_od.TryReadRaw(index, subindex, out var value))
        {
            // Race: entry was removed between the TryGet above and this locked snapshot.
            // Treat that as ObjectDoesNotExist rather than crashing with a KeyNotFound.
            SendSdoServerAbort(index, subindex, SdoAbortCode.ObjectDoesNotExist);
            return;
        }
        // Expedited only makes sense for 1..4 bytes: the 2-bit "n" field in the CS byte cannot
        // distinguish a 0-byte payload from a 4-byte payload (both encode as n=0), so a
        // length-0 OD value would decode as four zeros on the peer. Route empty values through
        // the segmented path where the size indicator is a full 32-bit little-endian field,
        // and a "last-segment / n=7" segment cleanly carries zero data bytes to the client.
        if (value.Length is >= 1 and <= 4)
        {
            // Expedited upload response — no server-side session survives the initiate, since
            // the whole value fits in the response frame. (Supersede handling already ran at
            // the top of the method, so no stale session remains.)
            var buf = new byte[8];
            buf[0] = (byte)(SdoFrames.ScsUploadInitExpeditedBase | (((4 - value.Length) & 0x03) << 2) | 0x03);
            buf[1] = (byte)(index & 0xFF);
            buf[2] = (byte)((index >> 8) & 0xFF);
            buf[3] = subindex;
            for (int i = 0; i < value.Length; i++) buf[4 + i] = value[i];
            _ = SendControlFrame(CanOpenCobId.SdoTx(_nodeId), buf);
            return;
        }

        // Segmented upload: reply with 0x41 + size, then serve segments as the client acks.
        // Also used for value.Length == 0 (the "empty OD value" case): declared length 0, and
        // the first segment request from the client will be answered with a "last, 0-byte"
        // segment (n=7, c=1) which decodes cleanly to an empty payload.
        var initBuf = new byte[8];
        initBuf[0] = SdoFrames.ScsUploadInitSegmented;
        initBuf[1] = (byte)(index & 0xFF);
        initBuf[2] = (byte)((index >> 8) & 0xFF);
        initBuf[3] = subindex;
        uint len = (uint)value.Length;
        initBuf[4] = (byte)(len & 0xFF);
        initBuf[5] = (byte)((len >> 8) & 0xFF);
        initBuf[6] = (byte)((len >> 16) & 0xFF);
        initBuf[7] = (byte)((len >> 24) & 0xFF);

        var session = new SdoServerSession(inDownload: false, index, subindex, value, offset: 0, toggle: false);
        session.Deadline = _deadlines.Arm(_options.SdoServerTimeout, OnSdoServerTimeout);
        _sdoServer = session;
        _ = SendControlFrame(CanOpenCobId.SdoTx(_nodeId), initBuf);
    }

    private void HandleServerUploadSegmentRequest(SdoServerSession session, byte[] data)
    {
        byte cs = data[0];
        bool toggleReq = (cs & SdoFrames.ToggleBit) != 0;
        if (toggleReq != session.Toggle)
        {
            SendSdoServerAbort(session.Index, session.Subindex, SdoAbortCode.ToggleBitNotAlternated);
            _sdoServer = null;
            return;
        }
        RearmSdoServerDeadline(session);

        int remaining = session.Buffer.Length - session.Offset;
        int chunk = Math.Min(7, remaining);
        var payload = new byte[chunk];
        Buffer.BlockCopy(session.Buffer, session.Offset, payload, 0, chunk);
        bool last = (session.Offset + chunk) >= session.Buffer.Length;
        var seg = SdoFrames.BuildSegment(SdoFrames.ScsUploadSegmentBase, session.Toggle, last, payload);
        _ = SendControlFrame(CanOpenCobId.SdoTx(_nodeId), seg);

        session.Offset += chunk;
        session.Toggle = !session.Toggle;
        if (last)
            ClearSdoServerSession();
    }

    private void HandleServerDownloadInit(ushort index, byte subindex, OdEntry? entry, byte cs, byte[] data)
    {
        // Per CiA 301 §7.2.4.3.4 a fresh initiate implicitly supersedes any previously open
        // server-side transfer. Emit an abort on the wire for that stale (index, subindex) so
        // its remote client does not have to wait for its own SDO timeout to notice the
        // supersede. Done up-front so this holds even when we go on to reject the new initiate
        // below (missing entry, wrong access, length mismatch, ...). Classic and block
        // sessions share the same SDO TX COB-ID, so both must be cleared.
        AbortSupersededServerSession();
        AbortSupersededBlockServerSession();

        if (entry is null)
        {
            // CiA 301 Table 22 distinguishes an unknown object (0602 0000h) from an unknown
            // sub-index of a known one (0609 0011h) — the latter is also what §7.5.2.37 prescribes
            // for the reserved sub-index 04h of a PDO communication record.
            SendSdoServerAbort(index, subindex, _od.ContainsIndex(index)
                ? SdoAbortCode.SubIndexDoesNotExist : SdoAbortCode.ObjectDoesNotExist);
            return;
        }
        if ((entry.Access & OdAccess.WriteOnly) == 0)
        {
            SendSdoServerAbort(index, subindex, SdoAbortCode.AttemptWriteReadOnly);
            return;
        }

        // e = 0 is a segmented (normal) transfer whether or not s is set. 0x21 carries the
        // length in bytes 4..7; 0x20 leaves those bytes reserved and the length is whatever
        // the segments deliver (#38). Reading 0x20 as expedited committed four bytes out of
        // that reserved field and then aborted the segments that followed.
        if ((cs & 0x02) == 0)
        {
            bool sizeIndicated = (cs & 0x01) != 0;
            uint declaredLen = 0;
            if (sizeIndicated)
            {
                declaredLen = (uint)(data[4] | (data[5] << 8) | (data[6] << 16) | (data[7] << 24));

                // Cap the initiator's 32-bit declared length before the `new byte[declaredLen]`
                // below: a hostile / buggy peer can otherwise drive us into an unbounded allocation
                // (up to 4 GiB) purely by choosing the bytes in the init frame's size field. Beyond
                // the option cap the CiA 301 "out of memory" abort code (0x05040005) is the right
                // signal to send back to the peer.
                if (declaredLen > (uint)_options.MaxSdoTransferBytes)
                {
                    SendSdoServerAbort(index, subindex, SdoAbortCode.OutOfMemory);
                    return;
                }

                // For fixed-width types the declared length must equal the OD's declared width.
                // With no size indicated the length is not known until the last segment, so that
                // check waits until then.
                int fixedSize = OdEntryLayout.FixedSize(entry.DataType);
                if (fixedSize > 0 && declaredLen != fixedSize)
                {
                    var reason = declaredLen > fixedSize
                        ? SdoAbortCode.LengthTooHigh : SdoAbortCode.LengthTooLow;
                    SendSdoServerAbort(index, subindex, reason);
                    return;
                }
            }

            // Supersede handling already ran at the top of the method; install the fresh
            // segmented-download session cleanly here.
            var session = new SdoServerSession(inDownload: true, index, subindex,
                sizeIndicated ? new byte[declaredLen] : Array.Empty<byte>(),
                offset: 0, toggle: false, sizeIndicated: sizeIndicated);
            session.Deadline = _deadlines.Arm(_options.SdoServerTimeout, OnSdoServerTimeout);
            _sdoServer = session;
            var ack = new byte[8];
            ack[0] = SdoFrames.ScsDownloadInitAck;
            ack[1] = (byte)(index & 0xFF);
            ack[2] = (byte)((index >> 8) & 0xFF);
            ack[3] = subindex;
            _ = SendControlFrame(CanOpenCobId.SdoTx(_nodeId), ack);
            return;
        }

        // Expedited download (e = 1). s = 0 means all four data bytes are valid.
        var payload = SdoFrames.ReadExpeditedPayload(data);
        int required = OdEntryLayout.FixedSize(entry.DataType);
        if (required > 0 && payload.Length != required)
        {
            var reason = payload.Length > required
                ? SdoAbortCode.LengthTooHigh : SdoAbortCode.LengthTooLow;
            SendSdoServerAbort(index, subindex, reason);
            return;
        }
        // Commit the value BEFORE acknowledging so a client task that unblocks on our ACK
        // and then reads the OD (on any thread) is guaranteed to observe the new value.
        // Route the write through ObjectDictionary.WriteRaw so it lands under the OD's
        // internal lock, giving readers on other threads a proper acquire/release pairing
        // rather than relying on OdEntry field-level memory ordering.
        if (!_od.TryWriteRaw(index, subindex, payload, out var abort))
        {
            SendSdoServerAbort(index, subindex, abort ?? SdoAbortCode.General);
            return;
        }

        // Expedited download owns no ongoing segmented state — no server-side session survives
        // the initiate. Supersede handling for any previously open segmented transfer already
        // ran at the top of the method (see AbortSupersededServerSession there); nothing left
        // to clear here beyond acknowledging the write on the wire.
        var respBuf = new byte[8];
        respBuf[0] = SdoFrames.ScsDownloadInitAck;
        respBuf[1] = (byte)(index & 0xFF);
        respBuf[2] = (byte)((index >> 8) & 0xFF);
        respBuf[3] = subindex;
        _ = SendControlFrame(CanOpenCobId.SdoTx(_nodeId), respBuf);
    }

    private void HandleServerDownloadSegment(SdoServerSession session, byte[] data)
    {
        var (payload, last, toggle) = SdoFrames.ReadSegment(data);
        if (toggle != session.Toggle)
        {
            SendSdoServerAbort(session.Index, session.Subindex, SdoAbortCode.ToggleBitNotAlternated);
            _sdoServer = null;
            return;
        }
        int incoming = session.Offset + payload.Length;
        if (!session.SizeIndicated)
        {
            // No declared length (#38): the buffer is capacity and Offset is the logical
            // length. Capacity doubles so a transfer near the cap does not recopy every
            // preceding byte on each segment, and it never exceeds the same cap a sized
            // initiate is held to. A segment past that cap is refused before any allocation,
            // not copied and then trimmed.
            if (incoming > _options.MaxSdoTransferBytes)
            {
                SendSdoServerAbort(session.Index, session.Subindex, SdoAbortCode.OutOfMemory);
                _sdoServer = null;
                return;
            }
            if (incoming > session.Buffer.Length)
            {
                int max = _options.MaxSdoTransferBytes;
                int capacity = session.Buffer.Length == 0 ? 8 : session.Buffer.Length;
                while (capacity < incoming)
                {
                    // Doubling past the cap would allocate more than we are willing to keep.
                    // Clamp and stop; incoming is already known to fit in max.
                    if (capacity > max / 2)
                    {
                        capacity = max;
                        break;
                    }
                    capacity *= 2;
                }
                if (capacity > max)
                {
                    capacity = max;
                }
                var grown = new byte[capacity];
                Buffer.BlockCopy(session.Buffer, 0, grown, 0, session.Offset);
                session.Buffer = grown;
            }
        }
        else if (incoming > session.Buffer.Length)
        {
            SendSdoServerAbort(session.Index, session.Subindex, SdoAbortCode.LengthTooHigh);
            _sdoServer = null;
            return;
        }
        RearmSdoServerDeadline(session);
        Buffer.BlockCopy(payload, 0, session.Buffer, session.Offset, payload.Length);
        session.Offset += payload.Length;

        // On the last segment, commit the accumulated buffer to the OD BEFORE ACKing.
        //
        // SendControlFrame dispatches the wire TX through Task.Run, so the ACK can be
        // delivered to the peer on a ThreadPool thread concurrently with this actor
        // continuing. If we ACK first, the client's SdoDownloadAsync task completes as
        // soon as the ACK arrives, which lets its caller read the OD before the actor
        // reaches the SetRawValue below -- exactly the race that used to leave the
        // segmented-download round-trip test intermittently observing all zeros on net48.
        //
        // Routing the write through ObjectDictionary.WriteRaw also takes the OD's lock,
        // giving readers on other threads a proper release/acquire pairing rather than
        // relying on field-level memory ordering of OdEntry._value.
        if (last)
        {
            if (session.SizeIndicated && session.Offset != session.Buffer.Length)
            {
                SendSdoServerAbort(session.Index, session.Subindex, SdoAbortCode.LengthTooLow);
                _sdoServer = null;
                return;
            }
            // The size was not in the initiate, so a fixed-width object can only be checked now.
            if (!session.SizeIndicated
                && _od.TryGet(session.Index, session.Subindex, out var entry))
            {
                int fixedSize = OdEntryLayout.FixedSize(entry.DataType);
                if (fixedSize > 0 && session.Offset != fixedSize)
                {
                    var reason = session.Offset > fixedSize
                        ? SdoAbortCode.LengthTooHigh : SdoAbortCode.LengthTooLow;
                    SendSdoServerAbort(session.Index, session.Subindex, reason);
                    _sdoServer = null;
                    return;
                }
            }
            var final = new byte[session.Offset];
            Buffer.BlockCopy(session.Buffer, 0, final, 0, session.Offset);
            if (!_od.TryWriteRaw(session.Index, session.Subindex, final, out var abort))
            {
                SendSdoServerAbort(session.Index, session.Subindex, abort ?? SdoAbortCode.General);
                return;
            }
        }

        // Server segment ack -- sent after any OD commit so the client can never observe
        // "download finished" before the OD is up to date.
        byte cs = SdoFrames.ScsDownloadSegmentBase;
        if (session.Toggle) cs |= SdoFrames.ToggleBit;
        var ack = new byte[8];
        ack[0] = cs;
        _ = SendControlFrame(CanOpenCobId.SdoTx(_nodeId), ack);
        session.Toggle = !session.Toggle;

        if (last) ClearSdoServerSession();
    }

    private void SendSdoServerAbort(ushort index, byte subindex, SdoAbortCode code)
    {
        _sdoServer?.Deadline?.Dispose();
        _sdoServer = null;
        _ = SendControlFrame(CanOpenCobId.SdoTx(_nodeId),
            SdoFrames.BuildAbort(index, subindex, (uint)code));
    }

    /// <summary>
    /// Aborts any currently-open SDO server transfer on the wire and clears the local session.
    /// Called before installing or omitting a new server session on any client-initiated
    /// initiate (upload or download, expedited or segmented). Per CiA 301 §7.2.4.3.4 the new
    /// initiate implicitly supersedes the previously open transfer, but staying silent leaves
    /// the remote client blocked on its own SDO timer for that superseded transfer. Emitting a
    /// General abort against the previously open (index, subindex) unblocks that peer
    /// immediately, matching what compliant CiA 301 servers do (e.g. CANopenNode).
    /// </summary>
    private void AbortSupersededServerSession()
    {
        var stale = _sdoServer;
        if (stale is null) return;
        stale.Deadline?.Dispose();
        _sdoServer = null;
        _ = SendControlFrame(CanOpenCobId.SdoTx(_nodeId),
            SdoFrames.BuildAbort(stale.Index, stale.Subindex, (uint)SdoAbortCode.General));
    }

    /// <summary>
    /// Drops the open server-side session and releases its idle deadline. Used by the natural
    /// completion paths (last segment), which do not go through <see cref="SendSdoServerAbort"/>.
    /// </summary>
    private void ClearSdoServerSession()
    {
        _sdoServer?.Deadline?.Dispose();
        _sdoServer = null;
    }

    /// <summary>
    /// Rearms (or initially arms) the server-side session idle timeout after any segment
    /// activity, mirroring the block-transfer server's guard
    /// (<c>OnSdoBlockServerTimeout</c> in CanOpenNode.SdoBlock.cs): a client that starts a
    /// segmented transfer and then goes silent must not pin the server's single session slot
    /// forever. Fires on the actor loop via <see cref="DeadlineScheduler"/>.
    /// </summary>
    private void RearmSdoServerDeadline(SdoServerSession session)
    {
        var deadline = session.Deadline;
        if (deadline is null || deadline.IsExpired || deadline.IsCancelled
            || !deadline.Rearm(_options.SdoServerTimeout))
        {
            deadline?.Dispose();
            session.Deadline = _deadlines.Arm(_options.SdoServerTimeout, OnSdoServerTimeout);
        }
    }

    private void OnSdoServerTimeout()
    {
        var stale = _sdoServer;
        if (stale is null) return;
        stale.Deadline?.Dispose();
        _sdoServer = null;
        // Tell a late-returning client the session is gone (mirrors the client-timeout path
        // OnSdoClientTimeout) instead of letting it discover the loss via its own retry logic.
        _ = SendControlFrame(CanOpenCobId.SdoTx(_nodeId),
            SdoFrames.BuildAbort(stale.Index, stale.Subindex, (uint)SdoAbortCode.SdoProtocolTimedOut));
    }

    // =========================================================================================
    // SDO client (FR-CO-002 / FR-CO-003)
    // =========================================================================================
    private void RegisterSdoCancellation<T>(TaskCompletionSource<T> tcs, CancellationToken ct,
        byte serverNodeId)
    {
        if (!ct.CanBeCanceled) return;
        var registration = ct.Register(static state =>
        {
            var (self, sid, boxed, token) = ((CanOpenNode, byte, object, CancellationToken))state!;
            try
            {
                self._actor.Post(() => self.CancelSdoClient(sid, boxed, token));
            }
            catch (ObjectDisposedException)
            {
                if (boxed is TaskCompletionSource<byte[]> tcs1) tcs1.TrySetCanceled(token);
            }
        }, (this, serverNodeId, (object)tcs, ct));

        // The registration holds this transfer's tcs through its state for as long as the token
        // lives, and an application-lifetime token shared by every request is the normal case:
        // without a release, each transfer left one registration and, through it, one completed
        // task with its result behind, for as long as the token lived (#59). The transfer's own
        // task is the one thing every ending goes through — result, peer abort, local abort,
        // timeout, cancellation, disposal — so the release rides on it as a continuation rather
        // than on each of those paths.
        tcs.Task.ContinueWith(
            static (_, state) => ((CancellationTokenRegistration)state!).Dispose(),
            registration,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void CancelSdoClient(byte serverNodeId, object tcsBoxed, CancellationToken token)
    {
        if (_sdoClients.TryGetValue(serverNodeId, out var session) && ReferenceEquals(session.Tcs, tcsBoxed))
        {
            _sdoClients.Remove(serverNodeId);
            session.Deadline?.Dispose();
            _ = SendControlFrame(CanOpenCobId.SdoRx(serverNodeId),
                SdoFrames.BuildAbort(session.Index, session.Subindex, (uint)SdoAbortCode.General));
        }
        else if (_sdoBlockClients.TryGetValue(serverNodeId, out var blockSession)
                 && ReferenceEquals(blockSession.Tcs, tcsBoxed))
        {
            _sdoBlockClients.Remove(serverNodeId);
            blockSession.Deadline?.Dispose();
            _ = SendControlFrame(CanOpenCobId.SdoRx(serverNodeId),
                SdoFrames.BuildAbort(blockSession.Index, blockSession.Subindex, (uint)SdoAbortCode.General));
        }
        if (tcsBoxed is TaskCompletionSource<byte[]> tcs) tcs.TrySetCanceled(token);
    }

    private void BeginSdoUpload(byte serverNodeId, ushort index, byte subindex,
        TaskCompletionSource<byte[]> tcs)
    {
        if (_disposed != 0)
        {
            tcs.TrySetException(new ObjectDisposedException(nameof(CanOpenNode)));
            return;
        }
        if (tcs.Task.IsCompleted) return;
        if (_sdoClients.ContainsKey(serverNodeId) || _sdoBlockClients.ContainsKey(serverNodeId))
        {
            tcs.TrySetException(new InvalidOperationException(
                $"An SDO transfer with server 0x{serverNodeId:X2} is already in flight."));
            return;
        }
        var session = new SdoClientSession(serverNodeId, index, subindex, isDownload: false,
            payload: null, tcs);
        _sdoClients[serverNodeId] = session;
        session.Deadline = _deadlines.Arm(_options.SdoTimeout, () => OnSdoClientTimeout(serverNodeId));
        SendSdoClientRequest(session, SdoFrames.BuildUploadInit(index, subindex));
    }

    private void BeginSdoDownload(byte serverNodeId, ushort index, byte subindex,
        byte[] payload, TaskCompletionSource<byte[]> tcs)
    {
        if (_disposed != 0)
        {
            tcs.TrySetException(new ObjectDisposedException(nameof(CanOpenNode)));
            return;
        }
        if (tcs.Task.IsCompleted) return;
        if (_sdoClients.ContainsKey(serverNodeId) || _sdoBlockClients.ContainsKey(serverNodeId))
        {
            tcs.TrySetException(new InvalidOperationException(
                $"An SDO transfer with server 0x{serverNodeId:X2} is already in flight."));
            return;
        }
        var session = new SdoClientSession(serverNodeId, index, subindex, isDownload: true,
            payload, tcs);
        _sdoClients[serverNodeId] = session;
        session.Deadline = _deadlines.Arm(_options.SdoTimeout, () => OnSdoClientTimeout(serverNodeId));
        SendSdoClientRequest(session, SdoFrames.BuildDownloadInit(index, subindex, payload));
    }

    private void OnSdoClientTimeout(byte serverNodeId)
    {
        if (!_sdoClients.TryGetValue(serverNodeId, out var session)) return;
        if (session.LatestSendPending)
        {
            // A request is still unconfirmed. If it fails, nothing ever reached the server and
            // the transfer must end with that failure, not a timeout that reads as a silent
            // server; if it is confirmed, the timeout stands. OnSdoClientSendCompleted decides.
            // The deadline has fired and needs no disposing.
            session.TimedOut = true;
            return;
        }

        CompleteSdoClientTimeout(session);
    }

    private void CompleteSdoClientTimeout(SdoClientSession session)
    {
        byte serverNodeId = session.ServerNodeId;
        _sdoClients.Remove(serverNodeId);
        session.Deadline?.Dispose();
        // Send a client-side abort so the server knows to drop any lingering state.
        _ = SendControlFrame(CanOpenCobId.SdoRx(serverNodeId),
            SdoFrames.BuildAbort(session.Index, session.Subindex, (uint)SdoAbortCode.SdoProtocolTimedOut));
        session.Tcs.TrySetException(new SdoAbortException(session.Index, session.Subindex,
            SdoAbortCode.SdoProtocolTimedOut, SdoAbortOrigin.Local));
    }

    private void HandleSdoClientResponse(byte serverNodeId, byte[] data)
    {
        if (!_sdoClients.TryGetValue(serverNodeId, out var session)) return;
        if (session.TimedOut) return; // decided already; only the pending send's outcome is awaited
        if (data.Length == 0) return; // nothing to look at — can't even read the CS byte
        int wireLength = data.Length;
        if (data.Length < 8)
        {
            // Real-world ECUs sometimes strip trailing zero bytes under DLC-padding rules
            // (esp. on CAN-FD gateways that translate short SDO responses). SdoFrames.Read*
            // is happy to decode as long as it can reach the fields it needs, so pad the
            // frame to 8 bytes rather than silently dropping it and letting the client hang
            // on its SDO deadline. Trailing zeros are semantically the same as "unused
            // bytes" in the index/sub, abort-code and expedited-payload layouts. They are not
            // in a segment, whose bytes are data: one that is shorter than its own n field
            // declares is refused in the upload-segment branch below (#203).
            var padded = new byte[8];
            Buffer.BlockCopy(data, 0, padded, 0, data.Length);
            data = padded;
        }
        byte cs = data[0];

        if (cs == SdoFrames.CsAbort)
        {
            var (idx, sub) = SdoFrames.ReadIndex(data);
            uint code = SdoFrames.ReadAbortCode(data);
            // The abort may target a DIFFERENT transfer than ours: a compliant CiA 301 §7.2.4.3.4
            // server emits an abort for a previously open transfer (potentially from another
            // client, or an abandoned older initiate from us) when a new initiate supersedes it.
            // Only fail our own session when the abort references the (index, subindex) we are
            // actually asking about; otherwise it is bookkeeping for a transfer that is not ours
            // and we ignore it so the response for our real request can still complete us.
            if (idx != session.Index || sub != session.Subindex) return;
            _sdoClients.Remove(serverNodeId);
            session.Deadline?.Dispose();
            session.Tcs.TrySetException(new SdoAbortException(idx, sub, code,
                $"Peer server 0x{serverNodeId:X2} aborted SDO transfer 0x{idx:X4}:{sub:X2} with code 0x{code:X8}."));
            return;
        }

        // Attribution before anything else (#18). A frame is this session's only when the
        // phase, the command specifier and — while an initiate response is awaited — the
        // multiplexer all agree with it. Only an attributed frame re-arms the deadline or moves
        // the session; every other frame is ignored, not aborted: a stray response must not kill
        // a healthy transfer.
        if (!session.InSegmentPhase)
        {
            // Every legitimate frame in this phase carries the object it answers for in bytes
            // 1..3 — the download initiate response (CiA 301 §7.2.4.3.3, Figure 21), the upload
            // initiate response, expedited or segmented (§7.2.4.3.6, Figure 23). A frame naming
            // another object is somebody else's response: a late answer to a request of ours
            // that already timed out, or the reply to a second client on the same server (the
            // default SDO channel 0x580 + id is seen by every client on the bus). Accepting it
            // completed the session with the wrong object's value and no exception.
            var (idx, sub) = SdoFrames.ReadIndex(data);
            if (idx != session.Index || sub != session.Subindex) return;

            if (session.IsDownload)
            {
                if (cs != SdoFrames.ScsDownloadInitAck) return;
                // For expedited download this completes the transfer. For segmented, start
                // sending segments (or complete if the payload is empty, though we always used
                // expedited for zero-length data).
                if (session.Payload!.Length <= 4)
                {
                    session.Deadline?.Dispose();
                    _sdoClients.Remove(serverNodeId);
                    session.Tcs.TrySetResult(Array.Empty<byte>());
                    return;
                }
                RearmSdoClientDeadline(session);
                session.InSegmentPhase = true;
                SendNextClientDownloadSegment(session);
                return;
            }

            // Upload path. e = 1: expedited (0x43/0x47/0x4B/0x4F, or e = 1 and s = 0, which
            // carries four data bytes). e = 0: segmented, with a size (0x41) or without one
            // (0x40). 0x40 is a normal transfer whose length arrives with the segments (#38);
            // matching only 0x41 dropped it and the transfer ran into its timeout.
            if ((cs & 0xE0) == SdoFrames.ScsUploadInitExpeditedBase && (cs & 0x02) != 0)
            {
                // Expedited upload complete.
                var value = SdoFrames.ReadExpeditedPayload(data);
                session.Deadline?.Dispose();
                _sdoClients.Remove(serverNodeId);
                session.Tcs.TrySetResult(value);
                return;
            }
            if ((cs & 0xE0) == SdoFrames.ScsUploadInitExpeditedBase && (cs & 0x02) == 0)
            {
                uint declared = SdoFrames.ReadSegmentedTotalLength(data);
                // Cap the server's 32-bit declared length before the `new byte[declared]`
                // below. Mirrors the server-side cap in HandleServerDownloadInit above: a
                // hostile / buggy server can otherwise coax the client into an unbounded
                // allocation just by choosing the size bytes in the segmented upload-init
                // response. Abort back to the server with the CiA 301 "out of memory" code
                // (0x05040005) and fail the client task with the same abort. See Bugbot
                // 3600644166.
                if (declared > (uint)_options.MaxSdoTransferBytes)
                {
                    AbortClient(session, SdoAbortCode.OutOfMemory);
                    return;
                }
                RearmSdoClientDeadline(session);
                session.InSegmentPhase = true;
                session.Payload = declared > 0 ? new byte[declared] : Array.Empty<byte>();
                session.DeclaredTotalSize = declared;
                session.SizeIndicated = (cs & 0x01) != 0;
                session.Offset = 0;
                session.Toggle = false;
                SendNextClientUploadSegmentRequest(session);
                return;
            }
            return;
        }

        // Segment phase. Segment frames and segment acks carry no multiplexer (§7.2.4.3.4,
        // §7.2.4.3.7), so they are matched by phase and command specifier alone; an initiate
        // response arriving now (a duplicate ack, a late answer to an earlier request) does not
        // belong to this phase and is ignored.
        if (session.IsDownload)
        {
            // Server segment ack.
            if ((cs & 0xE0) != SdoFrames.ScsDownloadSegmentBase) return;
            bool toggleAck = (cs & SdoFrames.ToggleBit) != 0;
            if (toggleAck != session.Toggle)
            {
                AbortClient(session, SdoAbortCode.ToggleBitNotAlternated);
                return;
            }
            session.Toggle = !session.Toggle;
            if (session.Offset >= session.Payload!.Length)
            {
                session.Deadline?.Dispose();
                _sdoClients.Remove(serverNodeId);
                session.Tcs.TrySetResult(Array.Empty<byte>());
                return;
            }
            RearmSdoClientDeadline(session);
            SendNextClientDownloadSegment(session);
            return;
        }

        // Upload segment 0x00/0x10/0x0X/0x1X.
        if ((cs & 0xE0) != SdoFrames.ScsUploadSegmentBase) return;
        {
            if (!SdoFrames.SegmentIsComplete(wireLength, cs))
            {
                AbortClient(session, SdoAbortCode.DataTypeLengthMismatch);
                return;
            }
            var (payload, last, toggle) = SdoFrames.ReadSegment(data);
            if (toggle != session.Toggle)
            {
                AbortClient(session, SdoAbortCode.ToggleBitNotAlternated);
                return;
            }
            // If we did not know the length up front (declared=0), grow lazily — but still
            // enforce MaxSdoTransferBytes so a zero-size init cannot bypass the cap by
            // streaming unbounded segments.
            int needed = session.Offset + payload.Length;
            // More than the server announced is a protocol error, as it is for the server's own
            // receive path and for the block-upload client (#253). It is judged before the cap,
            // so that a server announcing exactly the cap and sending more is told what it did
            // wrong. Without an indicated size the buffer grows, which is what that case is for;
            // an indicated size of zero is a size.
            if (session.SizeIndicated && needed > session.DeclaredTotalSize)
            {
                AbortClient(session, SdoAbortCode.LengthTooHigh);
                return;
            }
            if (needed > _options.MaxSdoTransferBytes)
            {
                AbortClient(session, SdoAbortCode.OutOfMemory);
                return;
            }
            if (session.Payload!.Length < needed)
            {
                var grown = new byte[GrowCapacity(session.Payload.Length, needed, _options.MaxSdoTransferBytes)];
                Buffer.BlockCopy(session.Payload, 0, grown, 0, session.Offset);
                session.Payload = grown;
            }
            Buffer.BlockCopy(payload, 0, session.Payload, session.Offset, payload.Length);
            session.Offset += payload.Length;
            session.Toggle = !session.Toggle;
            if (last)
            {
                var final = session.Payload;
                if (session.Offset != final.Length)
                {
                    // The buffer is larger than the data: slack from geometric growth when no
                    // size was announced, or a server that announced more than it sent.
                    var trimmed = new byte[session.Offset];
                    Buffer.BlockCopy(final, 0, trimmed, 0, session.Offset);
                    final = trimmed;
                }
                session.Deadline?.Dispose();
                _sdoClients.Remove(serverNodeId);
                // Fewer bytes than announced (a device that announces the maximum length of a
                // VISIBLE_STRING and sends what it holds) is accepted, but no longer silently:
                // the caller gets the data, and the shortfall is reported (#253).
                if (session.SizeIndicated && session.Offset < session.DeclaredTotalSize)
                {
                    RaiseBackgroundException(new CanOpenTransportException(
                        $"SDO upload of 0x{session.Index:X4}:{session.Subindex:X2} from node {serverNodeId} announced {session.DeclaredTotalSize} byte(s) and delivered {session.Offset}; the shorter data was returned."));
                }
                session.Tcs.TrySetResult(final);
                return;
            }
            RearmSdoClientDeadline(session);
            SendNextClientUploadSegmentRequest(session);
        }
    }

    /// <summary>
    /// Next capacity for a receive buffer that has to grow to <paramref name="needed"/> bytes:
    /// doubled, so a transfer of N bytes copies O(N) in total instead of O(N²), but never past
    /// <paramref name="ceiling"/> (the transfer cap, plus the slack the caller's final segment
    /// may overshoot it by) and never below <paramref name="needed"/>.
    /// </summary>
    internal static int GrowCapacity(int current, int needed, long ceiling)
    {
        long doubled = Math.Max(8L, (long)current * 2);
        var capacity = (int)Math.Min(doubled, Math.Min(ceiling, int.MaxValue));
        return Math.Max(capacity, needed);
    }

    /// <summary>
    /// Restarts the client's request timer after a frame that was attributed to
    /// <paramref name="session"/> and leaves it open. Not called for frames the session ignores
    /// (#18): a stray response must not keep a transfer alive that its own server has gone
    /// silent on.
    /// </summary>
    private void RearmSdoClientDeadline(SdoClientSession session)
    {
        var deadline = session.Deadline;
        if (deadline is null || deadline.IsExpired || deadline.IsCancelled
            || !deadline.Rearm(_options.SdoTimeout))
        {
            deadline?.Dispose();
            byte serverNodeId = session.ServerNodeId;
            session.Deadline = _deadlines.Arm(_options.SdoTimeout, () => OnSdoClientTimeout(serverNodeId));
        }
    }

    private void SendNextClientDownloadSegment(SdoClientSession session)
    {
        int remaining = session.Payload!.Length - session.Offset;
        int chunk = Math.Min(7, remaining);
        var payload = new byte[chunk];
        Buffer.BlockCopy(session.Payload, session.Offset, payload, 0, chunk);
        bool last = (session.Offset + chunk) >= session.Payload.Length;
        session.Offset += chunk;
        var seg = SdoFrames.BuildSegment(SdoFrames.CcsDownloadSegmentBase, session.Toggle, last, payload);
        SendSdoClientRequest(session, seg);
    }

    private void SendNextClientUploadSegmentRequest(SdoClientSession session)
    {
        byte cs = SdoFrames.CcsUploadSegmentBase;
        if (session.Toggle) cs |= SdoFrames.ToggleBit;
        var req = new byte[8];
        req[0] = cs;
        SendSdoClientRequest(session, req);
    }

    private void AbortClient(SdoClientSession session, SdoAbortCode code)
    {
        _sdoClients.Remove(session.ServerNodeId);
        session.Deadline?.Dispose();
        _ = SendControlFrame(CanOpenCobId.SdoRx(session.ServerNodeId),
            SdoFrames.BuildAbort(session.Index, session.Subindex, (uint)code));
        session.Tcs.TrySetException(new SdoAbortException(session.Index, session.Subindex, code,
            SdoAbortOrigin.Local));
    }

    // =========================================================================================
    // Wire helpers
    // =========================================================================================
    private Task SendControlFrame(uint cobId, byte[] payload, CancellationToken cancellationToken = default,
        Action<CanOpenTransportException?>? onSendCompleted = null)
    {
        // Classic 11-bit CAN frame; no extended bit. We do not await SendConfirmedAsync for
        // fire-and-forget flows (SYNC / heartbeat producer / TPDO / EMCY / SDO) because their
        // callers do not need per-frame confirmation. A failed send always surfaces via
        // BackgroundExceptionOccurred. An SDO client request also passes onSendCompleted, which
        // is told once how the send ended -- null when confirmed or cancelled, the failure
        // otherwise -- so the transfer can fail at once instead of by the SDO timeout (#197).
        var frame = CanFrame.Classic(unchecked((int)cobId), payload, isExtendedFrame: false);
        return Task.Run(async () =>
        {
            CanOpenTransportException? failure = null;
            try
            {
                var conf = await _service.SendConfirmedAsync(frame, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                if (!conf.Confirmed)
                {
                    failure = new CanOpenTransportException(
                        $"CANopen frame TX on COB-ID 0x{cobId:X3} failed: {conf.FailureReason}.");
                    RaiseBackgroundException(failure);
                }
            }
            catch (OperationCanceledException oce)
            {
                // A cancellation this caller did not ask for -- the service was disposed under a
                // pending send -- means the frame was not sent, and an SDO transfer must hear
                // that as a transport failure rather than as a confirmed send (#197).
                if (!cancellationToken.IsCancellationRequested)
                {
                    failure = new CanOpenTransportException(
                        $"CANopen frame TX on COB-ID 0x{cobId:X3} was cancelled before it was confirmed.", oce);
                    RaiseBackgroundException(failure);
                }

                // Propagate so SendNmtCommandAsync / SendSyncAsync / SendEmcyAsync (and any
                // other awaiters of this Task) observe cancellation instead of a silent
                // success.
                throw;
            }
            catch (Exception ex)
            {
                // The event carries the same failure the transfer is failed with; a thrown
                // exception of another type is its InnerException (#197).
                failure = ex as CanOpenTransportException
                    ?? new CanOpenTransportException(
                        $"CANopen frame TX on COB-ID 0x{cobId:X3} failed: {ex.Message}", ex);
                RaiseBackgroundException(failure);
            }
            finally
            {
                onSendCompleted?.Invoke(failure);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Sends a request of a live SDO client transfer. If the bus rejects it or does not confirm
    /// it, the transfer fails with that <see cref="CanOpenTransportException"/> as soon as the
    /// failure is known: without the request on the wire no answer can come, and a timeout would
    /// read as a silent server (#197). While the send is still unconfirmed, the SDO timeout does
    /// not decide the transfer either (<see cref="OnSdoClientTimeout"/>).
    /// </summary>
    /// <remarks>
    /// Only the latest send can decide. SDO is request and response: the client sends again
    /// only after the server has answered, and an answer proves the request before it reached
    /// the server, whatever its confirmation later says -- a lost echo, not a lost frame.
    /// </remarks>
    private void SendSdoClientRequest(SdoClientSession session, byte[] payload)
    {
        var sendId = ++session.LatestSendId;
        session.LatestSendPending = true;
        _ = SendControlFrame(CanOpenCobId.SdoRx(session.ServerNodeId), payload,
            onSendCompleted: failure => PostSdoClientSendOutcome(
                () => OnSdoClientSendCompleted(session, sendId, failure)));
    }

    /// <summary>Actor side of <see cref="SendSdoClientRequest"/>: send
    /// <paramref name="sendId"/> of <paramref name="session"/> has ended, with
    /// <paramref name="failure"/> or confirmed.</summary>
    private void OnSdoClientSendCompleted(SdoClientSession session, int sendId, CanOpenTransportException? failure)
    {
        if (!_sdoClients.TryGetValue(session.ServerNodeId, out var live) || !ReferenceEquals(live, session))
            return;
        if (sendId != session.LatestSendId) return; // answered since: it reached the server
        session.LatestSendPending = false;
        if (failure is not null)
        {
            _sdoClients.Remove(session.ServerNodeId);
            session.Deadline!.Dispose(); // armed before the session's first send
            session.Tcs.TrySetException(failure);
            return;
        }

        if (session.TimedOut) CompleteSdoClientTimeout(session);
    }

    /// <summary>As <see cref="SendSdoClientRequest"/>, for a block transfer.</summary>
    private void SendSdoBlockClientRequest(SdoBlockClientSession session, byte[] payload)
        => _ = SendControlFrame(CanOpenCobId.SdoRx(session.ServerNodeId), payload,
            onSendCompleted: TrackSdoBlockClientSend(session));

    /// <summary>
    /// Makes the send about to start the latest one of <paramref name="session"/> and returns
    /// the reaction to its outcome. As in <see cref="SendSdoClientRequest"/>, only the latest
    /// send decides: the block client, too, sends again only after the server has answered.
    /// </summary>
    private Action<CanOpenTransportException?> TrackSdoBlockClientSend(SdoBlockClientSession session)
    {
        var sendId = ++session.LatestSendId;
        session.LatestSendPending = true;
        return failure => PostSdoClientSendOutcome(() =>
        {
            if (!IsLiveBlockSend(session, sendId)) return; // ended, or answered since: it reached the server
            session.LatestSendPending = false;
            if (failure is not null)
            {
                _sdoBlockClients.Remove(session.ServerNodeId);
                session.Deadline!.Dispose(); // armed before the session's first send
                session.Tcs.TrySetException(failure);
                return;
            }

            if (session.TimedOut) CompleteSdoBlockClientTimeout(session);
        });
    }

    /// <summary>
    /// Whether <paramref name="sendId"/> is still the latest send of a block-client session that
    /// is still open. Actor-only: it reads the session table.
    /// </summary>
    private bool IsLiveBlockSend(SdoBlockClientSession session, int sendId)
        => _sdoBlockClients.TryGetValue(session.ServerNodeId, out var live)
           && ReferenceEquals(live, session)
           && sendId == session.LatestSendId;

    /// <summary>
    /// Runs a send-outcome reaction on the actor, where the session tables live. After disposal
    /// there is nothing left to report to: disposal has already completed every open transfer.
    /// </summary>
    private void PostSdoClientSendOutcome(Action react)
    {
        try
        {
            _actor.Post(react);
        }
        catch (ObjectDisposedException)
        {
            // The actor is disposed only with the node, and disposing the node has completed the
            // transfer this outcome belonged to, so there is nothing left to report it to.
            return;
        }
    }

    /// <summary>
    /// Sends control frames in order on a single background task so Reset bootup (0x00) cannot
    /// race behind the subsequent Pre-Operational heartbeat.
    /// </summary>
    private Task SendOrderedControlFrames(params (uint CobId, byte[] Payload)[] frames)
        => SendOrderedControlFrames(onSendCompleted: null, frames);

    /// <summary>
    /// As above. <paramref name="onSendCompleted"/>, when given, is told once how the batch ended,
    /// as in <see cref="SendControlFrame"/>, and the batch stops at its first failed frame: the
    /// transfer those frames belong to is failed then, and its remaining frames would reach the
    /// peer after the caller may already have started the next transfer with it (#197).
    /// </summary>
    private Task SendOrderedControlFrames(Action<CanOpenTransportException?>? onSendCompleted,
        params (uint CobId, byte[] Payload)[] frames)
        => SendOrderedControlFrames(onSendCompleted, shouldStop: null, frames);

    /// <summary>
    /// As above. <paramref name="shouldStop"/>, when given, is asked before each frame and ends the
    /// batch without sending the rest: a block transfer sends a whole sub-block as one batch, and
    /// when the transfer ends meanwhile (the server aborted, the caller cancelled) the segments
    /// still to go would reach a server that has no session for them any more, and read as
    /// command specifiers. It runs on the sending task, so it must read thread-safe state only.
    /// </summary>
    private Task SendOrderedControlFrames(Action<CanOpenTransportException?>? onSendCompleted,
        Func<bool>? shouldStop, params (uint CobId, byte[] Payload)[] frames)
        => SendOrderedControlFrames(onSendCompleted, shouldStop, onFrameConfirmed: null, frames);

    /// <summary>
    /// As above. <paramref name="onFrameConfirmed"/>, when given, is called on the sending task
    /// after each frame that was confirmed, so the owner of a long batch can tell progress from
    /// silence. It must be cheap and thread-safe.
    /// </summary>
    private Task SendOrderedControlFrames(Action<CanOpenTransportException?>? onSendCompleted,
        Func<bool>? shouldStop, Action? onFrameConfirmed, params (uint CobId, byte[] Payload)[] frames)
    {
        return Task.Run(async () =>
        {
            CanOpenTransportException? failure = null;
            try
            {
                foreach (var (cobId, payload) in frames)
                {
                    if (shouldStop?.Invoke() == true) return;
                    try
                    {
                        var frame = CanFrame.Classic(unchecked((int)cobId), payload, isExtendedFrame: false);
                        var conf = await _service.SendConfirmedAsync(frame).ConfigureAwait(false);
                        if (!conf.Confirmed)
                        {
                            var unconfirmed = new CanOpenTransportException(
                                $"CANopen frame TX on COB-ID 0x{cobId:X3} failed: {conf.FailureReason}.");
                            RaiseBackgroundException(unconfirmed);
                            if (onSendCompleted is not null)
                            {
                                failure = unconfirmed;
                                return;
                            }
                        }
                        else
                        {
                            onFrameConfirmed?.Invoke();
                        }
                    }
                    catch (Exception ex)
                    {
                        // As in SendControlFrame: the event carries the transfer's failure.
                        failure = ex as CanOpenTransportException
                            ?? new CanOpenTransportException(
                                $"CANopen frame TX on COB-ID 0x{cobId:X3} failed: {ex.Message}", ex);
                        RaiseBackgroundException(failure);
                        return;
                    }
                }
            }
            finally
            {
                onSendCompleted?.Invoke(failure);
            }
        });
    }

    /// <summary>
    /// Calls the subscribers of an event one after the other, ending the round when one of them
    /// has disposed the node from the event pump: nothing is delivered past Dispose.
    /// </summary>
    private void DeliverToSubscribers<T>(EventHandler<T>? handler, T args)
    {
        if (handler is null) return;
        foreach (var subscriber in handler.GetInvocationList())
        {
            if (Volatile.Read(ref _pumpStopRequested)) break;
            ((EventHandler<T>)subscriber)(this, args);
        }
    }

    private void RaiseBackgroundException(Exception ex)
    {
        var handler = BackgroundExceptionOccurred;
        if (handler is null) return;
        // Nothing is reported once the disposal has finished: a subscriber or a timed-out send that
        // outlived it and fails late has nobody to tell.
        if (_disposeDone.Task.IsCompleted) return;
        // One subscriber that disposes the node ends the round for the rest, as for the other
        // events; a node that was disposed before the report still reports it to all.
        bool disposedBefore = Volatile.Read(ref _disposed) != 0;
        foreach (var subscriber in handler.GetInvocationList())
        {
            if (!disposedBefore && Volatile.Read(ref _disposed) != 0) break;
            try { ((EventHandler<Exception>)subscriber)(this, ex); }
            catch { /* subscriber must not tear down the node */ }
        }
    }

    private void RaiseHeartbeatReceived(byte producer, NmtState state, DateTime ts)
    {
        var args = new HeartbeatReceivedEventArgs(producer, state, ts);
        EnqueueEvent(() =>
        {
            try { DeliverToSubscribers(HeartbeatReceived, args); }
            catch (Exception ex) { RaiseBackgroundException(ex); }
        }, critical: false, key: null, emcyProducer: -1, producer);
    }

    private void RaiseHeartbeatTimeout(byte producer, TimeSpan timeout)
    {
        var args = new HeartbeatTimeoutEventArgs(producer, timeout);
        EnqueueEvent(() =>
        {
            try { DeliverToSubscribers(HeartbeatTimeout, args); }
            catch (Exception ex) { RaiseBackgroundException(ex); }
        }, critical: true, EventKey.HeartbeatTimeout(producer), emcyProducer: -1, producer);
    }

    private void RaiseEmcyReceived(EmcyMessage msg, DateTime ts)
    {
        var args = new EmcyReceivedEventArgs(msg, ts);
        EnqueueEvent(() =>
        {
            try { DeliverToSubscribers(EmcyReceived, args); }
            catch (Exception ex) { RaiseBackgroundException(ex); }
        }, critical: true, EventKey.Emcy(msg), msg.ProducerNodeId, msg.ProducerNodeId);
    }

    private void RaiseSyncReceived(DateTime ts)
    {
        var args = new SyncReceivedEventArgs(ts);
        EnqueueEvent(() =>
        {
            try { DeliverToSubscribers(SyncReceived, args); }
            catch (Exception ex) { RaiseBackgroundException(ex); }
        });
    }

    private void RaiseRpdoReceived(int pdoIndex, uint cobId, byte[] payload)
    {
        var args = new RpdoReceivedEventArgs(pdoIndex, cobId, payload);
        EnqueueEvent(() =>
        {
            try { DeliverToSubscribers(RpdoReceived, args); }
            catch (Exception ex) { RaiseBackgroundException(ex); }
        });
    }

    /// <summary>Synchronous, on the actor loop, before the boot-up: the application restores
    /// its own objects here (the event-queue notification can arrive after the boot-up). A
    /// handler's exception is reported, not propagated: the reset completes.</summary>
    private void RaiseApplicationReset(NmtCommand command)
    {
        var handler = ApplicationReset;
        if (handler is null) return;
        var args = new NmtResetEventArgs(command);
        // Each subscriber on its own: one that throws is reported and the others still restore
        // their objects, so the boot-up never announces a device only some of them reset
        // (Codex on #133).
        foreach (var subscriber in handler.GetInvocationList())
        {
            // One that disposed the node ends the round: the rest are not called on a node that
            // is gone.
            if (Volatile.Read(ref _disposed) != 0) break;
            try { ((EventHandler<NmtResetEventArgs>)subscriber)(this, args); }
            catch (Exception ex) { RaiseBackgroundException(ex); }
        }
    }

    private void RaiseNmtCommandReceived(NmtCommand cmd, byte target)
    {
        var args = new NmtCommandReceivedEventArgs(cmd, target);
        EnqueueEvent(() =>
        {
            try { DeliverToSubscribers(NmtCommandReceived, args); }
            catch (Exception ex) { RaiseBackgroundException(ex); }
        });
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(CanOpenNode));
    }

    // =========================================================================================
    // Nested state objects.
    // =========================================================================================

    private sealed class SdoServerSession
    {
        public SdoServerSession(bool inDownload, ushort index, byte subindex, byte[] buffer,
            int offset, bool toggle, bool sizeIndicated = true)
        {
            InDownload = inDownload;
            Index = index;
            Subindex = subindex;
            Buffer = buffer;
            Offset = offset;
            Toggle = toggle;
            SizeIndicated = sizeIndicated;
        }

        public bool InDownload { get; }
        public ushort Index { get; }
        public byte Subindex { get; }
        public byte[] Buffer { get; set; }
        /// <summary>False for a segmented download whose initiate did not carry a size (#38).
        /// The buffer then grows with each segment instead of being allocated up front.</summary>
        public bool SizeIndicated { get; }
        public int Offset { get; set; }
        public bool Toggle { get; set; }
        public IDeadline? Deadline { get; set; }
    }

    private sealed class SdoClientSession
    {
        public SdoClientSession(byte serverNodeId, ushort index, byte subindex, bool isDownload,
            byte[]? payload, TaskCompletionSource<byte[]> tcs)
        {
            ServerNodeId = serverNodeId;
            Index = index;
            Subindex = subindex;
            IsDownload = isDownload;
            Payload = payload;
            Tcs = tcs;
        }

        public byte ServerNodeId { get; }
        public ushort Index { get; }
        public byte Subindex { get; }
        public bool IsDownload { get; }

        /// <summary>For download: the caller's data. For upload: filled during segmented
        /// transfer.</summary>
        public byte[]? Payload { get; set; }
        public int Offset { get; set; }
        public uint DeclaredTotalSize { get; set; }
        public bool SizeIndicated { get; set; }
        public bool Toggle { get; set; }

        /// <summary>Numbers this transfer's sends; only the latest can still decide it (#197).</summary>
        public int LatestSendId { get; set; }

        /// <summary>The latest send has not ended yet.</summary>
        public bool LatestSendPending { get; set; }

        /// <summary>The SDO timeout elapsed while a request was still unconfirmed; the
        /// transfer ends when that send does.</summary>
        public bool TimedOut { get; set; }

        /// <summary>
        /// False while the initiate response is awaited, true once it has been accepted and
        /// segments (or segment acks) are exchanged. Decides which frames can belong to the
        /// session at all (#18): initiate responses carry a multiplexer and are checked against
        /// <see cref="Index"/>/<see cref="Subindex"/>; segment-phase frames carry none and are
        /// matched by phase and command specifier.
        /// </summary>
        public bool InSegmentPhase { get; set; }
        public TaskCompletionSource<byte[]> Tcs { get; }
        public IDeadline? Deadline { get; set; }
    }
}
