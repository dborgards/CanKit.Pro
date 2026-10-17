using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
    private byte[]? _lssDeferredReply;
    private readonly Queue<HeldTransmit> _lssHeldTx = new();
    private readonly object _lssTxGate = new();

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

    /// <summary>
    /// CiA 305 §6.5. Every transmit path asks this before it touches the driver: during the
    /// second switch delay the frame is not sent. An in-flight SDO send is reported confirmed
    /// so its timeout can still decide it. NMT and heartbeat are not dropped that way; they
    /// are held until the delay ends.
    /// </summary>
    private bool LssTransmitSuppressed(Action<CanOpenTransportException?>? onSendCompleted = null)
    {
        if (!IsLssSilent()) return false;
        onSendCompleted?.Invoke(null);
        return true;
    }

    /// <summary>
    /// NMT and the heartbeat COB-ID (boot-up, producer, state change, guarding reply) have to
    /// leave once the slave may transmit again. Reporting them confirmed while they are still
    /// held would apply a flying-master reset that never reached the bus.
    /// </summary>
    private static bool HoldsAcrossLssSilence(uint cobId)
    {
        if (cobId == CanOpenCobId.NmtCommand) return true;
        // 0x700 is not a heartbeat. Subtracting the minimum node-id wraps it, and every
        // other non-heartbeat COB-ID, past 127 in the same way, so one comparison is enough.
        uint nodeId = cobId - CanOpenCobId.HeartbeatBase;
        return nodeId - CanOpenCobId.MinNodeId
            <= (uint)(CanOpenCobId.MaxNodeId - CanOpenCobId.MinNodeId);
    }

    private bool TryHoldThroughSilence(uint cobId, byte[] payload, out Task<bool> held)
    {
        lock (_lssTxGate)
        {
            if (!IsLssSilent())
            {
                held = Task.FromResult(false);
                return false;
            }

            var copy = new byte[payload.Length];
            Buffer.BlockCopy(payload, 0, copy, 0, payload.Length);
            var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _lssHeldTx.Enqueue(new HeldTransmit(cobId, copy, signal));
            held = signal.Task;
            return true;
        }
    }

    private void EndLssSilence()
    {
        HeldTransmit[] held;
        lock (_lssTxGate)
        {
            // The flag and the queue move together. A send that arrives as the delay ends
            // either joins this flush or sees the flag clear and goes out on its own.
            Volatile.Write(ref _lssSilent, 0);
            if (_lssHeldTx.Count == 0)
                held = Array.Empty<HeldTransmit>();
            else
            {
                held = _lssHeldTx.ToArray();
                _lssHeldTx.Clear();
            }
        }

        FlushDeferredLssReply();
        if (held.Length != 0)
            _ = SendHeldInOrder(held);
    }

    private async Task SendHeldInOrder(HeldTransmit[] held)
    {
        foreach (var item in held)
        {
            // Every outcome completes the signal. SendNmtPayload can surface a driver exception
            // that is not a CanKitException; leaving the signal unfinished would stall the
            // flying-master NMT chain, and the frames queued behind this one would stay held.
            Task<bool> send = item.CobId == CanOpenCobId.NmtCommand
                ? SendNmtPayload(item.Payload)
                : SendHeldControl(item.CobId, item.Payload);
            await send.ContinueWith(
                static (task, state) => CompleteHeld((TaskCompletionSource<bool>)state!, task),
                item.Signal,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default).ConfigureAwait(false);
        }
    }

    private async Task<bool> SendHeldControl(uint cobId, byte[] payload)
    {
        await SendControlFrame(cobId, payload).ConfigureAwait(false);
        return true;
    }

    private static void CompleteHeld(TaskCompletionSource<bool> signal, Task<bool> send)
    {
        if (send.Status == TaskStatus.RanToCompletion)
        {
            signal.TrySetResult(send.Result);
            return;
        }

        if (IsNmtCancellation(send))
        {
            signal.TrySetCanceled();
            return;
        }

        signal.TrySetException(send.Exception!.InnerExceptions);
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

    private readonly struct HeldTransmit
    {
        public HeldTransmit(uint cobId, byte[] payload, TaskCompletionSource<bool> signal)
        {
            CobId = cobId;
            Payload = payload;
            Signal = signal;
        }

        public uint CobId { get; }
        public byte[] Payload { get; }
        public TaskCompletionSource<bool> Signal { get; }
    }

    private void CancelLssActivation()
    {
        _lssActivationGeneration++;
        _lssActivation?.Dispose();
        _lssActivation = null;
        _lssDeferredReply = null;
        // The node may transmit again. Disposal must not put a frame on a bus that is going away.
        if (Volatile.Read(ref _disposed) != 0)
            DropHeldTransmits();
        else
            EndLssSilence();
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
        // A command that arrives during the second switch delay is applied, and the response
        // goes out once the slave may transmit again. Dropping it would make the master time out
        // on a service the slave already performed.
        if (IsLssSilent())
        {
            _lssDeferredReply = payload;
            return;
        }
        _ = SendControlFrame(CanOpenCobId.LssSlaveMessage, payload);
    }

    private void FlushDeferredLssReply()
    {
        var payload = _lssDeferredReply;
        _lssDeferredReply = null;
        if (payload is null) return;
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
        if (delayMilliseconds == 0)
        {
            CommitPendingBitTiming();
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
        Volatile.Write(ref _lssSilent, 1);
        InvokeBitRateSwitch(_activeSelector, _activeIndex);
        _lssActivation = _actor.Schedule(delay, () => CompleteLssSilence(generation));
    }

    /// <summary>Ends the switch silence when this activation is still the current one.</summary>
    internal void CompleteLssSilence(int generation)
    {
        if (generation != _lssActivationGeneration) return;
        EndLssSilence();
    }

    private void CommitPendingBitTiming()
    {
        _activeSelector = _pendingSelector;
        _activeIndex = _pendingIndex;
        InvokeBitRateSwitch(_activeSelector, _activeIndex);
    }

    private void InvokeBitRateSwitch(byte selector, byte index)
    {
        var hook = _options.Lss?.BitRateSwitch;
        if (hook is null || !TryResolveBitRate(selector, index, out var rate)) return;
        _ = Task.Run(() => hook(rate, CancellationToken.None)).ContinueWith(
            static (task, state) => ((CanOpenNode)state!).RaiseBackgroundException(task.Exception!.GetBaseException()),
            this,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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
                for (int n = 1; n <= Co.PdoCount; n++)
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
