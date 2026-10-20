using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Core.Exceptions;
using CanKit.Pro.CANopen.Lss;
using EdsDcfNet.Models;

namespace CanKit.Pro.CANopen;

/// <summary>
/// Layer setting services slave (CiA 305) inside <see cref="CanOpenNode"/>. Off unless
/// <see cref="CanOpenNodeOptions.Lss"/> enables it or the device description sets
/// <c>LSS_Supported</c>. The active node-id is what COB-IDs and NMT use; the pending node-id
/// is what configure node-id writes, and it becomes active on NMT reset communication (§8.3).
/// </summary>
internal sealed partial class CanOpenNode : ILssSlave
{
    private readonly bool _lssEnabled;
    private readonly uint _table0Mask;

    private LssState _lssState = LssState.Waiting;
    private byte _pendingNodeId;
    private byte _persistentNodeId;
    private byte _pendingSelector;
    private byte _pendingIndex;
    private byte _activeSelector;
    private byte _activeIndex;
    private byte _persistentSelector;
    private byte _persistentIndex;

    // Node-id the restorable pre-defined COB-IDs currently encode, and the one 1200h encodes.
    // 1200h is not restored by an NMT reset, so the two diverge after the first retarget.
    private byte _profileCobIdNodeId;
    private byte _sdoCobIdNodeId;
    private byte _powerOnNodeId;
    private readonly byte _factoryNodeId;

    private int _fastscanPos;
    private int _selectiveStep;
    private int _identifyStep;
    private uint _revisionLow;
    private uint _revisionHigh;
    private uint _serialLow;

    private int _lssActivationGeneration;
    private IDisposable? _lssActivation;
    private int _lssSilent;
    private Task _lssBitRateSwitch = Task.CompletedTask;
    private LssSwitchCancellation? _lssCurrentSwitchCancellation;
    private readonly CancellationTokenSource _lssBitRateLifetime = new();
    private Task _lssBitRateCancellation = Task.CompletedTask;
    private bool _lssBitRateCleanupStarted;
    private bool _lssDelayElapsed;
    // Sends that passed the silence check and have not reached ReleaseTransmit. BeginLssSilence
    // closes the gate before waiting for these reservations to drain, so no later frame can
    // prolong the wait or reach the driver before the adapter switch finishes.
    private int _lssTxInFlight;
    private int _nodeIdGeneration;
    private int _lssWaitingForTransmits;
    private readonly Queue<HeldTransmit> _lssHeldTx = new();
    private readonly object _lssTxGate = new();

    // Internal synchronization seams for protocol tests; no private field names are required.
    internal bool HasLssTransmitsInFlight
    {
        get { lock (_lssTxGate) return _lssTxInFlight != 0; }
    }

    internal bool HasHeldLssTransmits
    {
        get { lock (_lssTxGate) return _lssHeldTx.Count != 0; }
    }

    internal int LssActivationGeneration => Volatile.Read(ref _lssActivationGeneration);
    internal Task LssBitRateSwitchCompletion => _lssBitRateSwitch;
    internal bool IsWaitingForLssTransmits => Volatile.Read(ref _lssWaitingForTransmits) != 0;
    internal TaskScheduler LssCancellationScheduler { get; set; } = TaskScheduler.Default;

    /// <inheritdoc />
    LssState ILssSlave.State
    {
        get
        {
            if (!_lssEnabled) return LssState.Waiting;
            if (_actor.IsOnCurrentActor) return _lssState;
            return _actor.PostAsync(() => _lssState).GetAwaiter().GetResult();
        }
    }

    private (bool Enabled, uint Table0, byte NodeId, byte Selector, byte Index) ResolveLayerSettings(
        byte requestedNodeId, CanOpenDeviceDescription? description)
    {
        bool enabled = _options.Lss is { } configured
            ? configured.Enabled
            : description?.DeviceInfo.LssSupported == true;
        if (requestedNodeId == CanOpenCobId.UnconfiguredNodeId)
        {
            if (!enabled)
                throw new ArgumentOutOfRangeException("nodeId", requestedNodeId,
                    $"CANopen node-id must be in [{CanOpenCobId.MinNodeId}, {CanOpenCobId.MaxNodeId}].");
        }
        else
        {
            CanOpenCobId.ValidateNodeId(requestedNodeId);
        }

        byte id = requestedNodeId;
        byte selector = LssBitTiming.Cia301Table;
        byte index = 2; // 500 kbit/s, the in-memory default when nothing was stored
        if (enabled && _options.Lss?.Store is { } store
            && store.TryLoad(out var storedId, out var storedSelector, out var storedIndex))
        {
            if (!CanOpenCobId.IsLayerSettingNodeId(storedId))
                throw new ArgumentOutOfRangeException("nodeId", storedId,
                    "A stored LSS node-id must be 1..127 or 0xFF.");
            id = storedId;
            selector = storedSelector;
            index = storedIndex;
        }

        return (enabled, enabled ? CaptureTable0(description) : 0u, id, selector, index);
    }

    /// <summary>
    /// Bits of CiA 301 table 0 the description allows. Every named rate when the file names
    /// none of them. Index 5 and 9 are never set.
    /// </summary>
    private static uint CaptureTable0(CanOpenDeviceDescription? description)
    {
        const uint all = (1u << 0) | (1u << 1) | (1u << 2) | (1u << 3) | (1u << 4)
            | (1u << 6) | (1u << 7) | (1u << 8);
        BaudRates? rates = description?.DeviceInfo.SupportedBaudRates;
        if (rates is null) return all;
        bool any = rates.BaudRate10 || rates.BaudRate20 || rates.BaudRate50 || rates.BaudRate125
            || rates.BaudRate250 || rates.BaudRate500 || rates.BaudRate800 || rates.BaudRate1000;
        if (!any) return all;
        uint mask = 0;
        if (rates.BaudRate1000) mask |= 1u << 0;
        if (rates.BaudRate800) mask |= 1u << 1;
        if (rates.BaudRate500) mask |= 1u << 2;
        if (rates.BaudRate250) mask |= 1u << 3;
        if (rates.BaudRate125) mask |= 1u << 4;
        if (rates.BaudRate50) mask |= 1u << 6;
        if (rates.BaudRate20) mask |= 1u << 7;
        if (rates.BaudRate10) mask |= 1u << 8;
        return mask;
    }

    private bool LayerSettingsBlockBoot()
        => _lssEnabled && !CanOpenCobId.IsActiveNodeId(_nodeId);

    private bool IsLssSilent() => Volatile.Read(ref _lssSilent) != 0;

    /// <summary>Test seams: whether the slave is in its switch silence, and how many frames it holds.</summary>
    internal bool IsLssSilentForTests => IsLssSilent();
    internal int HeldTransmitCountForTests { get { lock (_lssTxGate) return _lssHeldTx.Count; } }

    /// <summary>
    /// One decision for this frame, under <see cref="_lssTxGate"/>. A held or suppressed frame
    /// comes back as the task the caller waits on. A frame that may leave now reserves an
    /// in-flight slot and comes back as null: the caller sends, then <see cref="ReleaseTransmit"/>.
    /// Silence cannot start while that slot is open, and a holdable frame that arrives once
    /// silence has started is queued rather than dropped.
    /// </summary>
    private Task<bool>? GateTransmit(uint cobId, byte[] payload, Action<CanOpenTransportException?>? onSendCompleted,
        CancellationToken cancellationToken, bool rejectSuppression = false, int? nodeIdGeneration = null)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<bool>(cancellationToken);
        Action? reportSuppressed = null;
        Task<bool>? finished = null;
        lock (_lssTxGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return rejectSuppression
                    ? Task.FromException<bool>(new ObjectDisposedException(nameof(CanOpenNode)))
                    : Task.FromResult(false);
            if (nodeIdGeneration is { } generation && generation != _nodeIdGeneration)
                return Task.FromResult(false);
            // While the flush of a silence is still draining -- confirmations come back
            // asynchronously on hardware -- a holdable frame joins the queue behind what is
            // still in it, so a reply to a command that arrived just after the delay cannot
            // overtake a reply held by it (Codex on #331). The flush ends only when it finds
            // the queue empty under this gate, so nothing joins a flush that is gone.
            if (!IsLssSilent() && _lssFlushing && HoldsAcrossLssSilence(cobId))
                return EnqueueHeldTransmit(cobId, payload, cancellationToken);
            if (IsLssSilent())
            {
                if (HoldsAcrossLssSilence(cobId))
                    return EnqueueHeldTransmit(cobId, payload, cancellationToken);
                if (rejectSuppression)
                    return Task.FromException<bool>(new CanOpenTransportException(
                        $"CANopen frame TX on COB-ID 0x{cobId:X3} is suppressed by the LSS bit-rate switch."));
                reportSuppressed = () => onSendCompleted?.Invoke(null);
                finished = Task.FromResult(false);
            }
            else
            {
                _lssTxInFlight++;
            }
        }

        reportSuppressed?.Invoke();
        return finished;
    }

    /// <summary>
    /// Reserves a send that is dropped, not held, when the slave is silent (SDO, PDO, RTR).
    /// False means the frame is suppressed. True means the caller sends and then
    /// <see cref="ReleaseTransmit"/>.
    /// </summary>
    private bool TryReserveTransmit(int? nodeIdGeneration = null)
    {
        lock (_lssTxGate)
        {
            if (Volatile.Read(ref _disposed) != 0 || IsLssSilent()
                || (nodeIdGeneration is { } generation && generation != _nodeIdGeneration)) return false;
            _lssTxInFlight++;
            return true;
        }
    }

    private void ReleaseTransmit()
    {
        lock (_lssTxGate)
        {
            _lssTxInFlight--;
            if (_lssTxInFlight == 0)
                Monitor.PulseAll(_lssTxGate);
        }
    }

    private void WaitForTransmitsToDrain()
    {
        lock (_lssTxGate)
        {
            Volatile.Write(ref _lssSilent, 1);
            while (_lssTxInFlight != 0)
            {
                Volatile.Write(ref _lssWaitingForTransmits, 1);
                Monitor.Wait(_lssTxGate);
            }
            Volatile.Write(ref _lssWaitingForTransmits, 0);
        }
    }

    /// <summary>
    /// NMT and the heartbeat COB-ID (boot-up, producer, state change, guarding reply) have to
    /// leave once the slave may transmit again. Reporting them confirmed while they are still
    /// held would apply a flying-master reset that never reached the bus. So does an LSS reply:
    /// a command that arrives during the second switch delay is performed, and its reply goes
    /// out once the slave may transmit, in the order the commands came (ReplyLss) — dropping it
    /// would make the master time out on a service the slave carried out (#320).
    /// </summary>
    private static bool HoldsAcrossLssSilence(uint cobId)
    {
        if (cobId == CanOpenCobId.NmtCommand || cobId == CanOpenCobId.LssSlaveMessage) return true;
        // 0x700 is not a heartbeat. Subtracting the minimum node-id wraps it, and every
        // other non-heartbeat COB-ID, past 127 in the same way, so one comparison is enough.
        uint nodeId = cobId - CanOpenCobId.HeartbeatBase;
        return nodeId - CanOpenCobId.MinNodeId
            <= (uint)(CanOpenCobId.MaxNodeId - CanOpenCobId.MinNodeId);
    }

    private Task<bool> EnqueueHeldTransmit(uint cobId, byte[] payload, CancellationToken cancellationToken)
    {
        var copy = new byte[payload.Length];
        Buffer.BlockCopy(payload, 0, copy, 0, payload.Length);
        var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _lssHeldTx.Enqueue(new HeldTransmit(cobId, copy, signal, cancellationToken, _nodeIdGeneration));
        return cancellationToken.CanBeCanceled ? WaitForHeldTransmit(signal, cancellationToken) : signal.Task;
    }

    private static async Task<bool> WaitForHeldTransmit(TaskCompletionSource<bool> signal, CancellationToken cancellationToken)
    {
        using (cancellationToken.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(), signal))
            return await signal.Task.ConfigureAwait(false);
    }

    private void EndLssSilence()
    {
        bool start;
        lock (_lssTxGate)
        {
            // The flag and the queue move together. A send that arrives as the delay ends
            // either joins the flush, still queued, or sees the flag clear and goes out on its own.
            Volatile.Write(ref _lssSilent, 0);
            start = _lssHeldTx.Count != 0 && !_lssFlushing;
            if (start) _lssFlushing = true;
        }
        if (start) _ = FlushHeldAsync();
    }

    // Whether FlushHeldAsync is running. Under _lssTxGate. One flush at a time: a silence that
    // ends while the previous flush is still draining leaves the draining to it.
    private bool _lssFlushing;

    // The one consumer of the held queue. Each frame is taken out under the gate together with
    // the reservation of its send, so a silence that begins meanwhile finds it either sent or
    // still at the head of the queue, ahead of whatever that silence holds — never re-held
    // behind a newer frame, which is what a snapshot of the queue sent one by one allowed
    // (Codex on #331). Each outcome completes the frame's signal: SendConfirmedAsync can
    // surface a driver exception that is not a CanKitException, and a signal left unfinished
    // would stall the flying-master NMT chain and everything queued behind it.
    private async Task FlushHeldAsync()
    {
        while (true)
        {
            HeldTransmit item;
            lock (_lssTxGate)
            {
                if (IsLssSilent() || Volatile.Read(ref _disposed) != 0 || _lssHeldTx.Count == 0)
                {
                    _lssFlushing = false;
                    return;
                }
                item = _lssHeldTx.Dequeue();
                if (item.Signal.Task.IsCanceled) continue;
                // A reset may have changed the identity while this heartbeat or boot-up was
                // held: like EmitHeartbeat, resolve it when reached and discard it if no
                // identity is active.
                if (item.IsHeartbeat && (item.NodeIdGeneration != _nodeIdGeneration || !CanOpenCobId.IsActiveNodeId(_nodeId)))
                {
                    item.Signal.TrySetResult(false);
                    continue;
                }
                _lssTxInFlight++;
            }
            await SendHeldAsync(item).ConfigureAwait(false);
            BetweenHeldSendsForTests?.Invoke();
        }
    }

    /// <summary>Test seam: runs after each held frame has completed and before the next is taken
    /// from the queue — the window in which a new silence can begin (Codex on #331).</summary>
    internal Action? BetweenHeldSendsForTests;

    private async Task SendHeldAsync(HeldTransmit item)
    {
        uint cobId = item.IsNmt ? CanOpenCobId.NmtCommand
            : item.IsLssReply ? CanOpenCobId.LssSlaveMessage
            : CanOpenCobId.Heartbeat(_nodeId);
        try
        {
            var frame = CanFrame.Classic(unchecked((int)cobId), item.Payload, isExtendedFrame: false);
            var conf = await SendReservedFrame(frame, item.CancellationToken).ConfigureAwait(false);
            if (!conf.Confirmed)
                RaiseBackgroundException(new CanOpenTransportException(
                    $"CANopen frame TX on COB-ID 0x{cobId:X3} failed: {conf.FailureReason}."));
            item.Signal.TrySetResult(conf.Confirmed);
        }
        catch (OperationCanceledException)
        {
            item.Signal.TrySetCanceled();
        }
        catch (Exception ex)
        {
            // A held heartbeat or LSS reply has nobody awaiting its signal, so a driver throw is
            // reported here, as SendControlFrame reports it for a frame that was not held; the
            // held NMT's caller observes the signal and reports it itself (Codex and Bugbot on
            // #331).
            if (!item.IsNmt)
                RaiseBackgroundException(ex as CanOpenTransportException ?? new CanOpenTransportException(
                    $"CANopen frame TX on COB-ID 0x{cobId:X3} failed: {ex.Message}", ex));
            item.Signal.TrySetException(ex);
        }
    }

    private void DropHeldTransmits()
    {
        HeldTransmit[] held;
        lock (_lssTxGate)
        {
            Volatile.Write(ref _lssSilent, 0);
            if (_lssHeldTx.Count == 0) return;
            held = _lssHeldTx.ToArray();
            _lssHeldTx.Clear();
        }

        foreach (var item in held)
            item.Signal.TrySetResult(false);
    }

    private void CompleteLssTransmitHold()
    {
        if (Volatile.Read(ref _disposed) != 0) DropHeldTransmits();
        else EndLssSilence();
    }

    private readonly struct HeldTransmit
    {
        public HeldTransmit(uint cobId, byte[] payload, TaskCompletionSource<bool> signal, CancellationToken cancellationToken,
            int nodeIdGeneration)
        {
            IsNmt = cobId == CanOpenCobId.NmtCommand;
            IsLssReply = cobId == CanOpenCobId.LssSlaveMessage;
            IsHeartbeat = !IsNmt && !IsLssReply;
            Payload = payload;
            Signal = signal;
            CancellationToken = cancellationToken;
            NodeIdGeneration = nodeIdGeneration;
        }

        public bool IsNmt { get; }
        public bool IsLssReply { get; }
        public bool IsHeartbeat { get; }
        public byte[] Payload { get; }
        public TaskCompletionSource<bool> Signal { get; }
        public CancellationToken CancellationToken { get; }
        public int NodeIdGeneration { get; }
    }

    private void CancelLssActivation()
    {
        _lssActivationGeneration++;
        _lssActivation?.Dispose();
        _lssActivation = null;
        _lssDelayElapsed = false;
        // The node may transmit again. Disposal must not put a frame on a bus that is going away.
        if (Volatile.Read(ref _disposed) != 0 || _lssBitRateSwitch.Status == TaskStatus.RanToCompletion)
            CompleteLssTransmitHold();
    }

    // Actor thread. Invalid or unsupported commands draw no response (§7.3), except store,
    // which has an explicit "not supported" error.
    private void HandleLss(byte[] data)
    {
        switch (data[0])
        {
            case LssCommand.SwitchStateGlobal:
                if (data[1] is LssCommand.ModeWaiting or LssCommand.ModeConfiguration)
                {
                    _lssState = (LssState)data[1];
                    _selectiveStep = 0;
                    _identifyStep = 0;
                }
                return;
            case LssCommand.ConfigureNodeId:
                ReplyLss(ConfigureLssNodeId(data[1]));
                return;
            case LssCommand.ConfigureBitTiming:
                ReplyLss(ConfigureLssBitTiming(data[1], data[2]));
                return;
            case LssCommand.ActivateBitTiming:
                ActivateLssBitTiming(LssFrames.ReadSwitchDelay(data));
                return;
            case LssCommand.StoreConfiguration:
                ReplyLss(StoreLssConfiguration());
                return;
            case LssCommand.SwitchSelectiveVendor:
            case LssCommand.SwitchSelectiveProduct:
            case LssCommand.SwitchSelectiveRevision:
            case LssCommand.SwitchSelectiveSerial:
                ReplyLss(SelectiveLss(data));
                return;
            case >= LssCommand.IdentifyRemoteVendor and <= LssCommand.IdentifyRemoteSerialHigh:
                ReplyLss(IdentifyLssRemote(data));
                return;
            case LssCommand.IdentifyNonConfiguredRemote:
                if (IsLssUnconfigured())
                    ReplyLss(LssFrames.Command(LssCommand.IdentifyNonConfiguredSlave));
                return;
            case LssCommand.Fastscan:
                ReplyLss(FastscanLss(data));
                return;
            case LssCommand.InquireVendorId:
            case LssCommand.InquireProductCode:
            case LssCommand.InquireRevisionNumber:
            case LssCommand.InquireSerialNumber:
                ReplyLss(InquireLssIdentity(data[0]));
                return;
            case LssCommand.InquireNodeId:
                if (_lssState != LssState.Configuration) return;
                byte reported = CanOpenCobId.IsActiveNodeId(_nodeId) ? _nodeId : _pendingNodeId;
                ReplyLss(LssFrames.InquireNodeId(reported));
                return;
            default:
                if (!LssCommand.IsManufacturerSpecific(data[0]) || _lssState != LssState.Configuration)
                    return;
                var handler = _options.Lss?.Manufacturer;
                if (handler is null) return;
                var parameters = new byte[7];
                Buffer.BlockCopy(data, 1, parameters, 0, 7);
                ReplyLss(NormalizeLssResponse(handler(data[0], parameters)));
                return;
        }
    }

    private void ReplyLss(byte[]? payload)
    {
        if (payload is null) return;
        // During the second switch delay the send is held, in the queue that FlushHeldAsync
        // drains in order once the slave may transmit again (HoldsAcrossLssSilence). Started
        // here, on the actor, so the reply takes its place before anything the actor sends next.
        _ = SendControlFrame(CanOpenCobId.LssSlaveMessage, payload);
    }

    private static byte[]? NormalizeLssResponse(byte[]? response)
    {
        if (response is null || response.Length == 0) return null;
        if (response.Length == LssFrames.Length) return response;
        var frame = new byte[LssFrames.Length];
        Buffer.BlockCopy(response, 0, frame, 0, Math.Min(response.Length, LssFrames.Length));
        return frame;
    }

    private bool IsLssUnconfigured()
        => _pendingNodeId == CanOpenCobId.UnconfiguredNodeId && !CanOpenCobId.IsActiveNodeId(_nodeId);

    private byte[]? ConfigureLssNodeId(byte nodeId)
    {
        var lss = _options.Lss;
        if (_lssState != LssState.Configuration || (lss is not null && !lss.NodeIdConfigurationSupported))
            return null;
        if (!CanOpenCobId.IsLayerSettingNodeId(nodeId))
            return LssFrames.Confirmed(LssCommand.ConfigureNodeId, LssCommand.NodeIdOutOfRange, 0);
        _pendingNodeId = nodeId;
        // §8.3.1: an unconfigured slave that receives a valid pending node-id enters NMT
        // reset-communication Ready on its own and boots, and stays in LSS configuration.
        if (!CanOpenCobId.IsActiveNodeId(_nodeId) && CanOpenCobId.IsActiveNodeId(nodeId))
        {
            ApplyActiveNodeId(nodeId);
            // The node-id is active now, so a flying master configured while this slave was
            // unconfigured can claim with it. Before this, startup had no node-id to use.
            ApplyFlyingMasterStartup();
            EnterPreOperationalFromReset();
        }
        return LssFrames.Confirmed(LssCommand.ConfigureNodeId, LssCommand.NodeIdOk, 0);
    }

    private byte[]? ConfigureLssBitTiming(byte selector, byte index)
    {
        if (_lssState != LssState.Configuration || _options.Lss?.BitTimingSupported == false)
            return null;
        if (!IsBitTimingSupported(selector, index))
            return LssFrames.Confirmed(LssCommand.ConfigureBitTiming, LssCommand.BitTimingNotSupported, 0);
        _pendingSelector = selector;
        _pendingIndex = index;
        return LssFrames.Confirmed(LssCommand.ConfigureBitTiming, LssCommand.BitTimingOk, 0);
    }

    private bool IsBitTimingSupported(byte selector, byte index)
    {
        if (selector == LssBitTiming.Cia301Table)
            return index < 32 && (_table0Mask & (1u << index)) != 0 && LssBitTiming.TryGetCia301Rate(index, out _);
        if (!LssBitTiming.IsManufacturerTable(selector)) return false;
        var lss = _options.Lss;
        if (lss is null || lss.ManufacturerRates is null) return false;
        return lss.ManufacturerRates.ContainsKey((selector, index));
    }

    private void ActivateLssBitTiming(ushort delayMilliseconds)
    {
        if (_lssState != LssState.Configuration || _options.Lss?.BitTimingSupported == false)
            return;
        CancelLssActivation();
        _lssCurrentSwitchCancellation?.Cancel();
        if (delayMilliseconds == 0)
        {
            BeginLssSilence(_lssActivationGeneration, TimeSpan.Zero);
            return;
        }

        int generation = _lssActivationGeneration;
        var delay = TimeSpan.FromMilliseconds(delayMilliseconds);
        _lssActivation = _actor.Schedule(delay, () => BeginLssSilence(generation, delay));
    }

    /// <summary>
    /// Starts the second switch delay. A generation that lost to a later activate, or a node
    /// that is already disposed, does nothing: the timer may still run after it was superseded.
    /// </summary>
    internal void BeginLssSilence(int generation, TimeSpan delay)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (generation != _lssActivationGeneration) return;
        _activeSelector = _pendingSelector;
        _activeIndex = _pendingIndex;
        // Silence is the actor's second delay, not a wall-clock window: a late actor
        // keeps the slave quiet until that callback, which is the clock Schedule uses.
        // The flag is read under this gate by a transmit on another thread, and cleared
        // under it, so the write that starts the silence takes the same gate.
        // Close admission under the gate before draining existing driver reservations.
        // Already reserved sends finish first; later frames are held or dropped immediately.
        WaitForTransmitsToDrain();
        // Disposal can begin on another thread while the actor waits for the driver.
        if (Volatile.Read(ref _disposed) != 0) return;
        _lssDelayElapsed = false;
        var switching = InvokeBitRateSwitch(_activeSelector, _activeIndex, generation);
        Action resume = () => TryEndLssSilence(generation);
        _ = switching.ContinueWith(
            _ => RunOnActor(resume),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        _lssActivation = _actor.Schedule(delay, () => CompleteLssSilence(generation));
    }

    /// <summary>Records the end of the delay; an asynchronous adapter switch may still be pending.</summary>
    internal void CompleteLssSilence(int generation)
    {
        if (generation != _lssActivationGeneration) return;
        _lssDelayElapsed = true;
        TryEndLssSilence(generation);
    }

    private void TryEndLssSilence(int generation)
    {
        if (generation != _lssActivationGeneration || Volatile.Read(ref _disposed) != 0
            || !_lssDelayElapsed || _lssBitRateSwitch.Status != TaskStatus.RanToCompletion) return;
        _lssDelayElapsed = false;
        EndLssSilence();
    }

    private Task InvokeBitRateSwitch(byte selector, byte index, int generation)
    {
        var hook = _options.Lss?.BitRateSwitch;
        if (hook is null || !TryResolveBitRate(selector, index, out var rate)) return _lssBitRateSwitch;
        // Repeated activate commands must not run two adapter reconfigurations concurrently.
        var previous = _lssBitRateSwitch;
        _lssCurrentSwitchCancellation?.Cancel();
        var cancellation = new LssSwitchCancellation(_lssBitRateLifetime.Token, LssCancellationScheduler);
        _lssCurrentSwitchCancellation = cancellation;
        var cancellationToken = cancellation.Token;
        var switching = Task.Run(async () =>
        {
            await previous.ContinueWith(static completed => { _ = completed.Exception; },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).ConfigureAwait(false);
            // These decisions must not wait for the asynchronous token cancellation worker.
            if (Volatile.Read(ref _disposed) != 0 || generation != Volatile.Read(ref _lssActivationGeneration))
                throw new OperationCanceledException("The LSS bit-rate switch is no longer current.");
            cancellation.ThrowIfCancellationRequested();
            await hook(rate, cancellationToken).ConfigureAwait(false);
        });
        _lssBitRateSwitch = switching;
        _ = switching.ContinueWith(
            static (finished, state) =>
            {
                _ = finished.Exception;
                ((LssSwitchCancellation)state!).Finish();
            }, cancellation, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        _ = switching.ContinueWith(
            task =>
            {
                var error = task.IsCanceled
                    ? new OperationCanceledException("The LSS bit-rate switch was cancelled.")
                    : task.Exception!.GetBaseException();
                if (Volatile.Read(ref _disposed) == 0) RaiseBackgroundException(error);
            },
            CancellationToken.None,
            TaskContinuationOptions.NotOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return switching;
    }

    private sealed class LssSwitchCancellation
    {
        private readonly CancellationTokenSource _source = new();
        private readonly CancellationTokenRegistration _lifetimeRegistration;
        private readonly TaskScheduler _scheduler;
        private readonly object _gate = new();
        private Task? _cancellation;
        private bool _finished;

        public LssSwitchCancellation(CancellationToken lifetime, TaskScheduler scheduler)
        {
            _scheduler = scheduler;
            _lifetimeRegistration = lifetime.Register(static state => ((LssSwitchCancellation)state!).Cancel(), this);
        }

        public CancellationToken Token => _source.Token;

        public void ThrowIfCancellationRequested()
        {
            lock (_gate)
            {
                // Cancel publishes this task before returning, even if the worker has not run.
                if (_cancellation is not null) throw new OperationCanceledException(_source.Token);
            }
        }

        public void Cancel()
        {
            lock (_gate)
            {
                if (_finished || _cancellation is not null) return;
                // Supersession runs on the actor; disposal can race it on another thread.
                // Neither caller may run application cancellation handlers or dispose their source.
                _cancellation = Task.Factory.StartNew(() =>
                {
                    try { _source.Cancel(); }
                    catch (AggregateException) { /* every cancellation callback was invoked */ }
                }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, _scheduler);
            }
        }

        public void Finish()
        {
            Task cancellation;
            lock (_gate)
            {
                _finished = true;
                cancellation = _cancellation ?? Task.CompletedTask;
            }
            _lifetimeRegistration.Dispose();
            // The hook may finish while a cancellation handler still runs. Keep its token usable.
            _ = cancellation.ContinueWith(static (_, state) => ((CancellationTokenSource)state!).Dispose(),
                _source, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private void CancelLssBitRateSwitch()
    {
        // Application cancellation callbacks may block or throw. Never run them on the actor
        // or on the thread disposing the node.
        _lssBitRateCancellation = Task.Factory.StartNew(() => _lssBitRateLifetime.Cancel(),
            CancellationToken.None, TaskCreationOptions.DenyChildAttach, LssCancellationScheduler);
    }

    private void DisposeLssBitRateLifetime()
    {
        if (_lssBitRateCleanupStarted) return;
        _lssBitRateCleanupStarted = true;
        // Keep the source usable until both the hook chain and cancellation callbacks finish.
        _ = Task.WhenAll(_lssBitRateSwitch, _lssBitRateCancellation).ContinueWith(
            finished =>
            {
                _ = finished.Exception;
                _lssBitRateLifetime.Dispose();
            },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private bool TryResolveBitRate(byte selector, byte index, out int bitsPerSecond)
    {
        if (selector == LssBitTiming.Cia301Table)
            return LssBitTiming.TryGetCia301Rate(index, out bitsPerSecond);
        // The hook that calls this lives on the same options object, so it is present here.
        var rates = _options.Lss!.ManufacturerRates;
        if (rates is not null && rates.TryGetValue((selector, index), out bitsPerSecond))
            return true;
        bitsPerSecond = 0;
        return false;
    }

    private byte[]? StoreLssConfiguration()
    {
        if (_lssState != LssState.Configuration) return null;
        var store = _options.Lss?.Store;
        if (store is null)
            return LssFrames.Confirmed(LssCommand.StoreConfiguration, LssCommand.StoreNotSupported, 0);
        if (!store.TryStore(_pendingNodeId, _pendingSelector, _pendingIndex, out var specError))
        {
            byte code = specError == 0 ? LssCommand.StoreMediaError : LssCommand.ManufacturerSpecific;
            return LssFrames.Confirmed(LssCommand.StoreConfiguration, code, specError);
        }
        _persistentNodeId = _pendingNodeId;
        _persistentSelector = _pendingSelector;
        _persistentIndex = _pendingIndex;
        return LssFrames.Confirmed(LssCommand.StoreConfiguration, LssCommand.StoreOk, 0);
    }

    private byte[]? SelectiveLss(byte[] data)
    {
        if (_lssState != LssState.Waiting)
        {
            _selectiveStep = 0;
            return null;
        }
        uint value = LssFrames.ReadU32(data);
        int expected = data[0] - LssCommand.SwitchSelectiveVendor;
        // A vendor command always starts a new sequence. Otherwise an interrupted switch
        // would ignore the next master's first frame and fail the whole attempt.
        if (expected == 0)
            _selectiveStep = value == LssIdentityPart(0) ? 1 : 0;
        else if (_selectiveStep == expected && value == LssIdentityPart(expected))
            _selectiveStep++;
        else
            _selectiveStep = 0;
        if (data[0] != LssCommand.SwitchSelectiveSerial || _selectiveStep != 4)
            return null;
        _selectiveStep = 0;
        _lssState = LssState.Configuration;
        return LssFrames.Command(LssCommand.SwitchSelectiveAcknowledge);
    }

    private byte[]? IdentifyLssRemote(byte[] data)
    {
        uint value = LssFrames.ReadU32(data);
        switch (data[0])
        {
            case LssCommand.IdentifyRemoteVendor:
                _identifyStep = value == LssIdentityPart(0) ? 1 : 0;
                return null;
            case LssCommand.IdentifyRemoteProduct:
                _identifyStep = _identifyStep == 1 && value == LssIdentityPart(1) ? 2 : 0;
                return null;
            case LssCommand.IdentifyRemoteRevisionLow:
                _revisionLow = value;
                _identifyStep = _identifyStep == 2 ? 3 : 0;
                return null;
            case LssCommand.IdentifyRemoteRevisionHigh:
                _revisionHigh = value;
                _identifyStep = _identifyStep == 3 ? 4 : 0;
                return null;
            case LssCommand.IdentifyRemoteSerialLow:
                _serialLow = value;
                _identifyStep = _identifyStep == 4 ? 5 : 0;
                return null;
            default:
                if (_identifyStep != 5)
                {
                    _identifyStep = 0;
                    return null;
                }
                _identifyStep = 0;
                uint revision = LssIdentityPart(2);
                uint serial = LssIdentityPart(3);
                bool matches = revision >= _revisionLow && revision <= _revisionHigh
                    && serial >= _serialLow && serial <= value;
                return matches ? LssFrames.Command(LssCommand.IdentifySlave) : null;
        }
    }

    private uint LssIdentityPart(int part)
    {
        byte sub = (byte)(part + 1);
        return _od.TryReadUnsigned(Co.Identity, sub, out var value) ? value : 0;
    }

    private byte[]? FastscanLss(byte[] data)
    {
        if (_lssState != LssState.Waiting || !IsLssUnconfigured()) return null;
        byte bitCheck = data[5];
        byte lssSub = data[6];
        byte lssNext = data[7];
        // LSSNext 4 is the corrigendum's compatibility value for the last LSS number.
        if (lssSub > 3 || (lssNext > 3 && lssNext != 4)) return null;
        if (bitCheck == LssCommand.FastscanReset)
        {
            _fastscanPos = 0;
            return LssFrames.Command(LssCommand.IdentifySlave);
        }
        if (bitCheck > 31 || _fastscanPos != lssSub) return null;
        uint mask = 0xFFFF_FFFFu << bitCheck;
        uint idNumber = LssFrames.ReadU32(data);
        if ((LssIdentityPart(lssSub) & mask) != (idNumber & mask)) return null;
        _fastscanPos = lssNext == 4 ? 0 : lssNext;
        // LSSNext 0 switches only once it is below the current number. A vendor-id probe
        // whose LSSNext is also 0 (LSSSub is 0) therefore stays in the scan, which is what a
        // master that copies LSSSub into LSSNext for every bit relies on. LSSNext 4 is the
        // same completion as LSSNext 0 on the serial number.
        if (bitCheck == 0 && (lssNext < lssSub || (lssNext == 4 && lssSub == 3)))
            _lssState = LssState.Configuration;
        return LssFrames.Command(LssCommand.IdentifySlave);
    }

    private byte[]? InquireLssIdentity(byte commandSpecifier)
    {
        if (_lssState != LssState.Configuration) return null;
        int part = commandSpecifier - LssCommand.InquireVendorId;
        return LssFrames.WithU32(commandSpecifier, LssIdentityPart(part));
    }

    /// <summary>
    /// Copies <paramref name="nodeId"/> into the active node-id and rewrites pre-defined COB-IDs
    /// that still name the previous one. A COB-ID the application changed is left as it is.
    /// Actor only.
    /// </summary>
    private void ApplyActiveNodeId(byte nodeId)
    {
        byte profileFrom = _profileCobIdNodeId;
        byte sdoFrom = _sdoCobIdNodeId;
        // One hold of the write gate, so a 1010h save cannot snapshot a mix of the old and new
        // COB-IDs or record a baseline that does not name that snapshot. The active node-id is
        // published last: a reader that observes it already sees every retargeted COB-ID.
        _od.Transaction(() =>
        {
            if (profileFrom != nodeId)
            {
                RetargetCobId(Co.EmcyCobId, 0x00, CanOpenCobId.Emcy(profileFrom), CanOpenCobId.Emcy(nodeId));
                for (int n = 1; n <= Math.Min(4, _pdoCount); n++)
                {
                    RetargetCobId((ushort)(Co.RpdoComm + n - 1), 0x01,
                        CanOpenCobId.RpdoDefault(profileFrom, n), CanOpenCobId.RpdoDefault(nodeId, n));
                    RetargetCobId((ushort)(Co.TpdoComm + n - 1), 0x01,
                        CanOpenCobId.TpdoDefault(profileFrom, n), CanOpenCobId.TpdoDefault(nodeId, n));
                }
                RetargetFlyingMasterSlot(profileFrom, nodeId);
            }
            // Before the SRDO records are rewritten, so a rebuild inside that write sees this id.
            _srdo.SetNodeId(nodeId);
            if (profileFrom != nodeId)
                RetargetDefaultSrdo(profileFrom, nodeId);
            if (sdoFrom != nodeId)
            {
                RetargetCobId(Co.SdoServer, 0x01, CanOpenCobId.SdoRx(sdoFrom), CanOpenCobId.SdoRx(nodeId));
                RetargetCobId(Co.SdoServer, 0x02, CanOpenCobId.SdoTx(sdoFrom), CanOpenCobId.SdoTx(nodeId));
            }
            _profileCobIdNodeId = nodeId;
            _sdoCobIdNodeId = nodeId;
            _nodeId = nodeId;
            // 1014h is rewritten only when the id changed. Assigning the same id again, after a
            // reset cleared the cached producer, must still follow the record or EMCY goes out
            // on 000h.
            ApplyEmcyConfiguration();
        });
    }

    private void RetargetFlyingMasterSlot(byte from, byte to)
    {
        // 1F90h:06 is created with the communication profile, so a missing entry is a broken node.
        uint slot = _od.ReadUnsigned(Co.FlyingMasterTiming, 0x06);
        if (slot != (uint)(4000 + 10 * from)) return;
        uint updated = (uint)(4000 + 10 * to);
        _od.WriteRawUnchecked(Co.FlyingMasterTiming, 0x06, new[] { (byte)updated, (byte)(updated >> 8) });
    }

    private void RetargetDefaultSrdo(byte from, byte to)
    {
        if (_srdoCount < 1) return;
        // Node-ids are 1..127 or 255. Subtracting one folds the "below 1" check into the
        // same comparison as "above 64", which is the only split this method can observe.
        bool fromPredefined = (uint)(from - 1) <= 63;
        bool toPredefined = (uint)(to - 1) <= 63;
        if (!fromPredefined && !toPredefined) return;
        var comm = Safety.SrdoRecords.CommIndex(1);
        uint from1 = fromPredefined ? CanOpenCobId.SrdoDefaultCobId1(from) : 0;
        uint from2 = fromPredefined ? CanOpenCobId.SrdoDefaultCobId2(from) : 0;
        uint to1 = toPredefined ? CanOpenCobId.SrdoDefaultCobId1(to) : 0;
        uint to2 = toPredefined ? CanOpenCobId.SrdoDefaultCobId2(to) : 0;
        // A node-id outside 1..64 has no pre-defined pair (CiA DSP 304 §8.3.3). Populate wrote
        // 0 for it, and leaving the set puts a still-default pair back to 0. A pair the
        // application changed does not match and is left alone.
        // 13FEh is created with the SRDO records. This method already returned when there are none.
        bool wasValid = _od.ReadUnsigned(Safety.SrdoRecords.ConfigurationValid, 0)
            == Safety.SrdoRecords.ConfigurationValidValue;
        bool changed = RetargetCobId(comm, 0x05, from1, to1);
        changed |= RetargetCobId(comm, 0x06, from2, to2);
        if (!wasValid || !changed) return;
        // Those writes clear 13FEh. A committed producer or consumer has to follow the new
        // COB-IDs with a matching 13FFh, then A5h again, or the next operational period
        // treats the SRDO as invalid and it neither produces nor consumes. COB-ID 0 is not
        // a pair: the checksum is refreshed and the signature stays clear.
        // A missing direction reads as "not active". The record is always present when this runs;
        // treating a failed read as no direction keeps the signature path the same either way.
        Safety.SrdoRecords.TryReadCommunication(_od, 1, out var parameter);
        bool active = parameter.Direction != Safety.SrdoDirection.None;
        if (active)
        {
            ushort crc = Safety.SrdoCrc.Compute(parameter,
                Safety.SrdoMapping.FromEntries(Safety.SrdoRecords.ReadMapping(_od, 1)));
            _od.WriteRawUnchecked(Safety.SrdoRecords.Checksum, 1, new[] { (byte)crc, (byte)(crc >> 8) });
            if (!toPredefined) return;
        }
        _od.WriteRawUnchecked(Safety.SrdoRecords.ConfigurationValid, 0,
            new[] { Safety.SrdoRecords.ConfigurationValidValue });
    }

    internal bool RetargetCobId(ushort index, byte subindex, uint fromCanId, uint toCanId)
    {
        if (!_od.TryReadUnsigned(index, subindex, out var word)) return false;
        if ((word & CanOpenCobId.CanIdMask) != fromCanId) return false;
        uint updated = (word & ~CanOpenCobId.CanIdMask) | toCanId;
        _od.WriteRawUnchecked(index, subindex, new[]
        {
            (byte)updated,
            (byte)(updated >> 8),
            (byte)(updated >> 16),
            (byte)(updated >> 24),
        });
        return true;
    }

    /// <summary>The node-id the power-on snapshot of the communication profile was taken at.</summary>
    private void NoteLayerSettingsPowerOn(byte profileNodeId)
    {
        if (_lssEnabled) _powerOnNodeId = profileNodeId;
    }
}
