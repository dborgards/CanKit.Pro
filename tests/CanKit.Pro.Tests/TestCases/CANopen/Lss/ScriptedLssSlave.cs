using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Lss;
using CanKit.Pro.RawCan;

namespace CanKit.Pro.Tests.TestCases.CANopen.Lss;

/// <summary>
/// An LSS slave that speaks CiA 305 on the virtual bus so the master can be tested without a
/// <see cref="CanOpenNode"/>. The state rules match the ones the node-hosted slave follows:
/// waiting and configuration, Fastscan only while unconfigured and waiting, confirmed services
/// only in configuration, and the pending node-id becoming active at once when an unconfigured
/// slave is given a node-id in 1..127.
/// </summary>
internal sealed class ScriptedLssSlave : IDisposable
{
    private readonly object _gate = new();
    private readonly ICanBusService _service;
    private readonly bool _ownsBus;
    private readonly ISubscription _subscription;
    private readonly CancellationTokenSource _readerCts = new();
    private readonly Task _reader;
    private readonly List<byte> _commands = new();
    private CancellationTokenSource? _activation;
    private TaskCompletionSource<bool>? _silenceEntered;

    private byte _pendingNodeId;
    private byte? _activeNodeId;
    private byte _persistentNodeId;
    private byte _pendingSelector;
    private byte _pendingIndex;
    private byte _activeSelector;
    private byte _activeIndex;
    private byte _persistentSelector;
    private byte _persistentIndex;
    private int _fastscanPos;
    private int _selectiveStep;
    private int _identifyStep;
    private uint _revisionLow;
    private uint _revisionHigh;
    private uint _serialLow;
    private int _nextResponseLength = LssFrames.Length;

    public ScriptedLssSlave(ICanBus bus, LssAddress address, byte persistentNodeId = CanOpenCobId.UnconfiguredNodeId,
        bool ownsBus = true)
    {
        if (bus is null) throw new ArgumentNullException(nameof(bus));
        Address = address;
        _ownsBus = ownsBus;
        _persistentNodeId = persistentNodeId;
        _pendingNodeId = persistentNodeId;
        if (CanOpenCobId.IsActiveNodeId(persistentNodeId))
            _activeNodeId = persistentNodeId;
        _persistentIndex = 2;
        _pendingIndex = 2;
        _activeIndex = 2;
        _service = new CanBusService(bus);
        _subscription = _service.Subscribe(frame =>
        {
            var can = frame.Frame;
            return !can.IsExtendedFrame && (uint)can.ID == CanOpenCobId.LssMasterMessage;
        });
        _reader = Task.Run(ReadAsync);
    }

    public LssAddress Address { get; }

    public LssState State { get; private set; } = LssState.Waiting;

    public bool NodeIdConfigurationSupported { get; set; } = true;

    public bool BitTimingSupported { get; set; } = true;

    public bool StoreSupported { get; set; } = true;

    public bool StoreMediaFails { get; set; }

    /// <summary>When set, store replies with error <c>FFh</c> and this spec-error.</summary>
    public byte? StoreSpecError { get; set; }

    /// <summary>When set, a legal configure-node-id replies with error <c>FFh</c> and this spec-error.</summary>
    public byte? NodeIdSpecError { get; set; }

    public Func<byte, byte[], byte[]?>? Manufacturer { get; set; }

    public bool IsSilent { get; private set; }

    public int SuppressedResponses { get; private set; }

    public byte PendingNodeId { get { lock (_gate) return _pendingNodeId; } }

    public byte? ActiveNodeId { get { lock (_gate) return _activeNodeId; } }

    public byte PersistentNodeId { get { lock (_gate) return _persistentNodeId; } }

    public byte ActiveTableIndex { get { lock (_gate) return _activeIndex; } }

    public byte PersistentTableSelector { get { lock (_gate) return _persistentSelector; } }

    public byte PersistentTableIndex { get { lock (_gate) return _persistentIndex; } }

    public IReadOnlyList<byte> Commands
    {
        get { lock (_gate) return _commands.ToArray(); }
    }

    /// <summary>The next slave response is sent with this length. 8 is the normal LSS DLC.</summary>
    public void ReplyWithLength(int length)
    {
        lock (_gate) _nextResponseLength = length;
    }

    /// <summary>
    /// Manufacturer-specific bit timings, keyed by table selector and index. Selector 0 is the
    /// CiA 301 table and is not stored here.
    /// </summary>
    public Dictionary<(byte Selector, byte Index), int> ManufacturerRates { get; } = new();

    public async Task WaitForSilenceAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        Task task;
        while (true)
        {
            lock (_gate)
            {
                if (_silenceEntered is { } entered)
                {
                    task = entered.Task;
                    break;
                }
            }
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Bit-timing activation did not start.");
            await Task.Delay(1).ConfigureAwait(false);
        }
        await task.WaitAsync(timeout).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _readerCts.Cancel();
        _activation?.Cancel();
        _subscription.Dispose();
        try
        {
            _reader.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The reader stops because it was cancelled.
        }
        _service.Dispose();
        if (_ownsBus) _service.Bus.Dispose();
        _readerCts.Dispose();
    }

    private async Task ReadAsync()
    {
        try
        {
            await foreach (var frameEvent in _subscription.Frames.WithCancellation(_readerCts.Token)
                               .ConfigureAwait(false))
            {
                var data = frameEvent.Frame.Data;
                if (data.Length != LssFrames.Length) continue;
                var copy = data.ToArray();
                byte[]? response;
                var length = LssFrames.Length;
                lock (_gate)
                {
                    response = Handle(copy);
                    if (response is not null && IsSilent)
                    {
                        SuppressedResponses++;
                        response = null;
                    }
                    else if (response is not null)
                    {
                        length = _nextResponseLength;
                        _nextResponseLength = LssFrames.Length;
                    }
                }
                if (response is null) continue;
                await SendAsync(response, length).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Dispose.
        }
    }

    private byte[]? Handle(byte[] data)
    {
            _commands.Add(data[0]);
            switch (data[0])
            {
                case LssCommand.SwitchStateGlobal:
                    if (data[1] is LssCommand.ModeWaiting or LssCommand.ModeConfiguration)
                    {
                        State = (LssState)data[1];
                        _selectiveStep = 0;
                        _identifyStep = 0;
                    }
                    return null;
                case LssCommand.ConfigureNodeId:
                    return ConfigureNodeId(data[1]);
                case LssCommand.ConfigureBitTiming:
                    return ConfigureBitTiming(data[1], data[2]);
                case LssCommand.ActivateBitTiming:
                    return Activate(LssFrames.ReadSwitchDelay(data));
                case LssCommand.StoreConfiguration:
                    return Store();
                case LssCommand.SwitchSelectiveVendor:
                case LssCommand.SwitchSelectiveProduct:
                case LssCommand.SwitchSelectiveRevision:
                case LssCommand.SwitchSelectiveSerial:
                    return Selective(data);
                case >= LssCommand.IdentifyRemoteVendor and <= LssCommand.IdentifyRemoteSerialHigh:
                    return Identify(data);
                case LssCommand.IdentifyNonConfiguredRemote:
                    return Unconfigured && State is LssState.Waiting or LssState.Configuration
                        ? LssFrames.Command(LssCommand.IdentifyNonConfiguredSlave)
                        : null;
                case LssCommand.Fastscan:
                    return Fastscan(data);
                case LssCommand.InquireVendorId:
                case LssCommand.InquireProductCode:
                case LssCommand.InquireRevisionNumber:
                case LssCommand.InquireSerialNumber:
                    return Inquire(data[0]);
                case LssCommand.InquireNodeId:
                    if (State != LssState.Configuration) return null;
                    return LssFrames.InquireNodeId(_activeNodeId ?? _pendingNodeId);
                default:
                    if (!LssCommand.IsManufacturerSpecific(data[0]) || State != LssState.Configuration)
                        return null;
                    return Manufacturer?.Invoke(data[0], data);
            }
    }

    private bool Unconfigured => _pendingNodeId == CanOpenCobId.UnconfiguredNodeId && _activeNodeId is null;

    private byte[]? ConfigureNodeId(byte nodeId)
    {
        if (State != LssState.Configuration || !NodeIdConfigurationSupported) return null;
        if (!CanOpenCobId.IsLayerSettingNodeId(nodeId))
            return LssFrames.Confirmed(LssCommand.ConfigureNodeId, LssCommand.NodeIdOutOfRange, 0);
        if (NodeIdSpecError is byte spec)
            return LssFrames.Confirmed(LssCommand.ConfigureNodeId, LssCommand.ManufacturerSpecific, spec);
        _pendingNodeId = nodeId;
        if (_activeNodeId is null && CanOpenCobId.IsActiveNodeId(nodeId))
            _activeNodeId = nodeId;
        return LssFrames.Confirmed(LssCommand.ConfigureNodeId, LssCommand.NodeIdOk, 0);
    }

    private byte[]? ConfigureBitTiming(byte selector, byte index)
    {
        if (State != LssState.Configuration || !BitTimingSupported) return null;
        var supported = selector == LssBitTiming.Cia301Table
            ? LssBitTiming.TryGetCia301Rate(index, out _)
            : ManufacturerRates.ContainsKey((selector, index));
        if (!supported)
            return LssFrames.Confirmed(LssCommand.ConfigureBitTiming, LssCommand.BitTimingNotSupported, 0);
        _pendingSelector = selector;
        _pendingIndex = index;
        return LssFrames.Confirmed(LssCommand.ConfigureBitTiming, LssCommand.BitTimingOk, 0);
    }

    private byte[]? Activate(ushort delay)
    {
        if (State != LssState.Configuration || !BitTimingSupported) return null;
        _activation?.Cancel();
        _activation = new CancellationTokenSource();
        var token = _activation.Token;
        var selector = _pendingSelector;
        var index = _pendingIndex;
        if (delay == 0)
        {
            _activeSelector = selector;
            _activeIndex = index;
            return null;
        }
        _silenceEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = _silenceEntered;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
                lock (_gate)
                {
                    _activeSelector = selector;
                    _activeIndex = index;
                    IsSilent = true;
                }
                entered.TrySetResult(true);
                await Task.Delay(delay, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                entered.TrySetCanceled(token);
            }
            finally
            {
                lock (_gate) IsSilent = false;
            }
        }, token);
        return null;
    }

    private byte[]? Inquire(byte commandSpecifier)
    {
        if (State != LssState.Configuration) return null;
        uint value = commandSpecifier switch
        {
            LssCommand.InquireVendorId => Address.VendorId,
            LssCommand.InquireProductCode => Address.ProductCode,
            LssCommand.InquireRevisionNumber => Address.RevisionNumber,
            _ => Address.SerialNumber,
        };
        return LssFrames.WithU32(commandSpecifier, value);
    }

    private byte[]? Store()
    {
        if (State != LssState.Configuration) return null;
        if (StoreSpecError is byte spec)
            return LssFrames.Confirmed(LssCommand.StoreConfiguration, LssCommand.ManufacturerSpecific, spec);
        if (!StoreSupported)
            return LssFrames.Confirmed(LssCommand.StoreConfiguration, LssCommand.StoreNotSupported, 0);
        if (StoreMediaFails)
            return LssFrames.Confirmed(LssCommand.StoreConfiguration, LssCommand.StoreMediaError, 0);
        _persistentNodeId = _pendingNodeId;
        _persistentSelector = _pendingSelector;
        _persistentIndex = _pendingIndex;
        return LssFrames.Confirmed(LssCommand.StoreConfiguration, LssCommand.StoreOk, 0);
    }

    private byte[]? Selective(byte[] data)
    {
        if (State != LssState.Waiting)
        {
            _selectiveStep = 0;
            return null;
        }
        var value = LssFrames.ReadU32(data);
        var expected = data[0] - LssCommand.SwitchSelectiveVendor;
        if (_selectiveStep == expected && value == Address[expected])
            _selectiveStep++;
        else
            _selectiveStep = 0;
        if (data[0] != LssCommand.SwitchSelectiveSerial || _selectiveStep != 4)
            return null;
        _selectiveStep = 0;
        State = LssState.Configuration;
        return LssFrames.Command(LssCommand.SwitchSelectiveAcknowledge);
    }

    private byte[]? Identify(byte[] data)
    {
        var value = LssFrames.ReadU32(data);
        switch (data[0])
        {
            case LssCommand.IdentifyRemoteVendor:
                _identifyStep = value == Address.VendorId ? 1 : 0;
                return null;
            case LssCommand.IdentifyRemoteProduct:
                _identifyStep = _identifyStep == 1 && value == Address.ProductCode ? 2 : 0;
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
                var matches = Address.RevisionNumber >= _revisionLow
                    && Address.RevisionNumber <= _revisionHigh
                    && Address.SerialNumber >= _serialLow
                    && Address.SerialNumber <= value;
                return matches ? LssFrames.Command(LssCommand.IdentifySlave) : null;
        }
    }

    private byte[]? Fastscan(byte[] data)
    {
        if (State != LssState.Waiting || !Unconfigured) return null;
        var bitCheck = data[5];
        var lssSub = data[6];
        var lssNext = data[7];
        if (lssSub > 3 || lssNext > 3) return null;
        if (bitCheck == LssCommand.FastscanReset)
        {
            _fastscanPos = 0;
            return LssFrames.Command(LssCommand.IdentifySlave);
        }
        if (bitCheck > 31 || _fastscanPos != lssSub) return null;
        uint mask = 0xFFFF_FFFFu << bitCheck;
        var idNumber = LssFrames.ReadU32(data);
        if ((Address[lssSub] & mask) != (idNumber & mask)) return null;
        _fastscanPos = lssNext;
        if (bitCheck == 0 && lssNext < lssSub)
            State = LssState.Configuration;
        return LssFrames.Command(LssCommand.IdentifySlave);
    }

    private async Task SendAsync(byte[] payload, int length)
    {
        if (length != payload.Length)
        {
            var sized = new byte[length];
            Array.Copy(payload, sized, Math.Min(length, payload.Length));
            payload = sized;
        }
        var frame = CanFrame.Classic(unchecked((int)CanOpenCobId.LssSlaveMessage), payload, isExtendedFrame: false);
        try
        {
            await _service.SendConfirmedAsync(frame).ConfigureAwait(false);
        }
        finally
        {
            frame.Dispose();
        }
    }
}
