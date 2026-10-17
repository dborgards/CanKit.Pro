using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
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
    private byte _factoryNodeId;

    private int _fastscanPos;
    private int _selectiveStep;
    private int _identifyStep;
    private uint _revisionLow;
    private uint _revisionHigh;
    private uint _serialLow;

    private int _lssActivationGeneration;
    private IDisposable? _lssActivation;
    private long _lssSilentUntil;
    private byte[]? _lssDeferredReply;

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

    private bool IsLssSilent()
    {
        long until = Volatile.Read(ref _lssSilentUntil);
        return until != 0 && Stopwatch.GetTimestamp() < until;
    }

    private void CancelLssActivation()
    {
        _lssActivationGeneration++;
        _lssActivation?.Dispose();
        _lssActivation = null;
        _lssDeferredReply = null;
        Volatile.Write(ref _lssSilentUntil, 0);
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
        if (_lssState != LssState.Configuration || _options.Lss?.NodeIdConfigurationSupported == false)
            return null;
        if (!CanOpenCobId.IsLayerSettingNodeId(nodeId))
            return LssFrames.Confirmed(LssCommand.ConfigureNodeId, LssCommand.NodeIdOutOfRange, 0);
        _pendingNodeId = nodeId;
        // §8.3.1: an unconfigured slave that receives a valid pending node-id enters NMT
        // reset-communication Ready on its own and boots, and stays in LSS configuration.
        if (!CanOpenCobId.IsActiveNodeId(_nodeId) && CanOpenCobId.IsActiveNodeId(nodeId))
        {
            ApplyActiveNodeId(nodeId);
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
        return _options.Lss?.ManufacturerRates?.ContainsKey((selector, index)) == true;
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
        _lssActivation = _actor.Schedule(delay, () =>
        {
            if (generation != _lssActivationGeneration || Volatile.Read(ref _disposed) != 0) return;
            _activeSelector = _pendingSelector;
            _activeIndex = _pendingIndex;
            // Silence starts before the rate callback, so a fast callback still observes it.
            long ticks = delayMilliseconds * Stopwatch.Frequency / 1000;
            Volatile.Write(ref _lssSilentUntil, Stopwatch.GetTimestamp() + Math.Max(ticks, 1));
            InvokeBitRateSwitch(_activeSelector, _activeIndex);
            _lssActivation = _actor.Schedule(delay, () =>
            {
                if (generation != _lssActivationGeneration) return;
                Volatile.Write(ref _lssSilentUntil, 0);
                FlushDeferredLssReply();
            });
        });
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
        _ = Task.Run(async () =>
        {
            try
            {
                await hook(rate, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RaiseBackgroundException(ex);
            }
        });
    }

    private bool TryResolveBitRate(byte selector, byte index, out int bitsPerSecond)
    {
        if (selector == LssBitTiming.Cia301Table)
            return LssBitTiming.TryGetCia301Rate(index, out bitsPerSecond);
        if (_options.Lss?.ManufacturerRates is { } rates && rates.TryGetValue((selector, index), out bitsPerSecond))
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
        if (lssSub > 3 || lssNext > 3) return null;
        if (bitCheck == LssCommand.FastscanReset)
        {
            _fastscanPos = 0;
            return LssFrames.Command(LssCommand.IdentifySlave);
        }
        if (bitCheck > 31 || _fastscanPos != lssSub) return null;
        uint mask = 0xFFFF_FFFFu << bitCheck;
        uint idNumber = LssFrames.ReadU32(data);
        if ((LssIdentityPart(lssSub) & mask) != (idNumber & mask)) return null;
        _fastscanPos = lssNext;
        if (bitCheck == 0 && lssNext < lssSub)
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
        _nodeId = nodeId;
        _srdo.SetNodeId(nodeId);
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
            RetargetDefaultSrdo(profileFrom, nodeId);
        }
        if (sdoFrom != nodeId)
        {
            RetargetCobId(Co.SdoServer, 0x01, CanOpenCobId.SdoRx(sdoFrom), CanOpenCobId.SdoRx(nodeId));
            RetargetCobId(Co.SdoServer, 0x02, CanOpenCobId.SdoTx(sdoFrom), CanOpenCobId.SdoTx(nodeId));
        }
        _profileCobIdNodeId = nodeId;
        _sdoCobIdNodeId = nodeId;
    }

    private void RetargetFlyingMasterSlot(byte from, byte to)
    {
        if (!_od.TryReadUnsigned(Co.FlyingMasterTiming, 0x06, out var slot)) return;
        if (slot != (uint)(4000 + 10 * from)) return;
        uint updated = (uint)(4000 + 10 * to);
        _od.WriteRawUnchecked(Co.FlyingMasterTiming, 0x06, new[] { (byte)updated, (byte)(updated >> 8) });
    }

    private void RetargetDefaultSrdo(byte from, byte to)
    {
        if (from > 64 || to > 64 || _srdoCount < 1) return;
        var comm = Safety.SrdoRecords.CommIndex(1);
        RetargetCobId(comm, 0x05, CanOpenCobId.SrdoDefaultCobId1(from), CanOpenCobId.SrdoDefaultCobId1(to));
        RetargetCobId(comm, 0x06, CanOpenCobId.SrdoDefaultCobId2(from), CanOpenCobId.SrdoDefaultCobId2(to));
    }

    private void RetargetCobId(ushort index, byte subindex, uint fromCanId, uint toCanId)
    {
        if (!_od.TryReadUnsigned(index, subindex, out var word)) return;
        if ((word & CanOpenCobId.CanIdMask) != fromCanId) return;
        uint updated = (word & ~CanOpenCobId.CanIdMask) | toCanId;
        _od.WriteRawUnchecked(index, subindex, new[]
        {
            (byte)updated,
            (byte)(updated >> 8),
            (byte)(updated >> 16),
            (byte)(updated >> 24),
        });
    }

    /// <summary>The node-id the power-on snapshot of the communication profile was taken at.</summary>
    private void NoteLayerSettingsPowerOn(byte profileNodeId)
    {
        if (_lssEnabled) _powerOnNodeId = profileNodeId;
    }
}
