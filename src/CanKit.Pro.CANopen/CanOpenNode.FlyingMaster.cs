using System;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.Reliability;

namespace CanKit.Pro.CANopen;

/// <summary>
/// NMT flying master, bound to CiA 302-2 version 4.1.0 (historically DSP 302 clause 5.5).
/// Objects <c>1F80h</c> and <c>1F90h</c>, and the services on CAN-IDs <c>0x071</c>, <c>0x072</c>,
/// <c>0x073</c> and <c>0x076</c>. Once this node is the active master, <see cref="BeginBootUp"/>
/// takes the network list in <c>1F81h</c>.
/// </summary>
internal sealed partial class CanOpenNode
{
    // 1F80h. Bit 0 marks an NMT-master-capable device, bit 5 selects the flying-master process.
    // Both are required. The other bits are the boot-up manager's, with the polarity of the
    // open implementation that cites CiA 302-2 v4.1.0: a set bit 2 or bit 3 suppresses the
    // corresponding start, a clear bit allows it.
    private const uint NmtMasterBit = 0x0000_0001;
    private const uint NmtStartAllNodesBit = 0x0000_0002;
    private const uint NmtSuppressSelfStartBit = 0x0000_0004;
    private const uint NmtSuppressSlaveStartBit = 0x0000_0008;
    private const uint NmtResetAllOnErrorBit = 0x0000_0010;
    private const uint FlyingMasterBit = 0x0000_0020;
    private const uint NmtStopAllOnErrorBit = 0x0000_0040;

    // 1F81h, one UNSIGNED32 per node-id. Bit 0 assigns the slave, bit 2 lets the master boot
    // it, bit 3 marks it mandatory, bit 4 is keep-alive (Reset Communication is not sent).
    private const uint SlaveAssignedBit = 0x0000_0001;
    private const uint SlaveBootBit = 0x0000_0004;
    private const uint SlaveMandatoryBit = 0x0000_0008;
    private const uint SlaveKeepAliveBit = 0x0000_0010;

    private const byte TimingTimeout = 0x01;
    private const byte TimingDelay = 0x02;
    private const byte TimingPriority = 0x03;
    private const byte TimingPrioritySlot = 0x04;
    private const byte TimingDeviceSlot = 0x05;
    private const byte TimingDetectCycle = 0x06;

    private FlyingMasterRole _flyingMasterRole = FlyingMasterRole.Inactive;
    private bool _flyingMasterFromPowerOn = true;
    private byte? _activeFlyingMasterNodeId;
    private ushort? _activeFlyingMasterPriority;
    private TimeSpan? _flyingMasterHeartbeatTimeout;
    private byte? _flyingMasterInstalledWatch;
    private bool _flyingMasterReclaimStarted;
    private int _flyingMasterGeneration;
    private IDeadline? _flyingMasterDeadline;

    /// <inheritdoc />
    public FlyingMasterRole FlyingMasterRole
    {
        get
        {
            if (_actor.IsOnCurrentActor) return _flyingMasterRole;
            return _actor.PostAsync(() => _flyingMasterRole).GetAwaiter().GetResult();
        }
    }

    /// <inheritdoc />
    public byte? ActiveFlyingMasterNodeId
    {
        get
        {
            if (_actor.IsOnCurrentActor) return _activeFlyingMasterNodeId;
            return _actor.PostAsync(() => _activeFlyingMasterNodeId).GetAwaiter().GetResult();
        }
    }

    /// <inheritdoc />
    public ushort? ActiveFlyingMasterPriority
    {
        get
        {
            if (_actor.IsOnCurrentActor) return _activeFlyingMasterPriority;
            return _actor.PostAsync(() => _activeFlyingMasterPriority).GetAwaiter().GetResult();
        }
    }

    /// <inheritdoc />
    public event EventHandler<FlyingMasterChangedEventArgs>? FlyingMasterChanged;

    /// <inheritdoc />
    public void StartFlyingMaster(ushort priorityLevel, TimeSpan activeMasterHeartbeatTimeout)
    {
        ThrowIfDisposed();
        if (priorityLevel > 2)
            throw new ArgumentOutOfRangeException(nameof(priorityLevel), priorityLevel,
                "Flying-master priority level is 0, 1 or 2; 0 is the highest.");
        ushort heartbeatMs = ToMilliseconds16(activeMasterHeartbeatTimeout, nameof(activeMasterHeartbeatTimeout), allowZero: false);
        RunOnActorAndWait(() =>
        {
            if (!TimingSeparatesPriorities())
                throw new ArgumentException(
                    "1F90h:04 (priority time slot) must be greater than 127 times 1F90h:05 (device time slot), so a better priority level always waits less than a worse one.");
            _flyingMasterHeartbeatTimeout = TimeSpan.FromMilliseconds(heartbeatMs);
            // A fresh start is a cold boot of the election: the first detection that finds no
            // master broadcasts Reset Communication, which is why the configuration is recorded
            // as a power-on value before that reset can restore the dictionary.
            _flyingMasterFromPowerOn = true;
            StopFlyingMasterCore();
            _od.WriteUnsigned(Co.FlyingMasterTiming, TimingPriority, priorityLevel);
            uint startup = _od.ReadUnsigned(Co.NmtStartup, 0x00);
            _od.WriteUnsigned(Co.NmtStartup, 0x00, startup | NmtMasterBit | FlyingMasterBit);
            RememberFlyingMasterPowerOn();
        });
    }

    /// <inheritdoc />
    public void StopFlyingMaster()
    {
        if (_disposed != 0) return;
        RunOnActorAndWait(() =>
        {
            StopFlyingMasterCore();
            uint startup = _od.ReadUnsigned(Co.NmtStartup, 0x00);
            _od.WriteUnsigned(Co.NmtStartup, 0x00, startup & ~(NmtMasterBit | FlyingMasterBit));
            RememberFlyingMasterPowerOn();
        });
    }

    private bool FlyingMasterEnabled
    {
        get
        {
            uint startup = _od.ReadUnsigned(Co.NmtStartup, 0x00);
            return (startup & NmtMasterBit) != 0 && (startup & FlyingMasterBit) != 0;
        }
    }

    private void ApplyFlyingMasterStartup()
    {
        if (_disposed != 0) return;
        if (FlyingMasterEnabled)
        {
            if (_flyingMasterRole == FlyingMasterRole.Inactive)
                BeginFlyingMaster();
            else if (_flyingMasterRole == FlyingMasterRole.Active)
                BeginBootUp();
            return;
        }
        if (_flyingMasterRole != FlyingMasterRole.Inactive)
            StopFlyingMasterCore();
    }

    /// <summary>Drops the in-flight election so an NMT reset can restore <c>1F80h</c> and start
    /// a warm one. A reset is the warm boot of the procedure: it does not broadcast another
    /// Reset Communication of its own.</summary>
    private void SuspendFlyingMasterForReset()
    {
        if (_flyingMasterRole == FlyingMasterRole.Inactive && _flyingMasterDeadline is null)
        {
            _flyingMasterFromPowerOn = false;
            return;
        }
        StopFlyingMasterCore();
        _flyingMasterFromPowerOn = false;
    }

    private void BeginFlyingMaster()
    {
        if (!FlyingMasterEnabled || _disposed != 0) return;
        CancelFlyingMasterDeadline();
        _flyingMasterRole = FlyingMasterRole.Delaying;
        _flyingMasterReclaimStarted = false;
        _activeFlyingMasterNodeId = null;
        _activeFlyingMasterPriority = null;
        ArmFlyingMaster(TimeSpan.FromMilliseconds(ReadTiming(TimingDelay)), OnFlyingMasterDelayElapsed);
    }

    private void OnFlyingMasterDelayElapsed()
    {
        if (_flyingMasterRole != FlyingMasterRole.Delaying || !FlyingMasterEnabled) return;
        _flyingMasterRole = FlyingMasterRole.Detecting;
        _ = SendControlFrame(CanOpenCobId.FlyingMasterDetect, Array.Empty<byte>());
        ArmFlyingMaster(TimeSpan.FromMilliseconds(ReadTiming(TimingTimeout)), OnFlyingMasterDetectionTimeout);
    }

    private void OnFlyingMasterDetectionTimeout()
    {
        if (_flyingMasterRole != FlyingMasterRole.Detecting || !FlyingMasterEnabled) return;
        if (_flyingMasterFromPowerOn)
        {
            // Cold boot and nobody is master: Reset Communication takes every flying master
            // through a warm boot together, so the timeslot race is not racing PDO traffic or
            // staggered startup. The echo of that command restarts this node; Begin below covers
            // the same restart when the echo has not been seen yet.
            _flyingMasterFromPowerOn = false;
            _ = SendControlFrame(CanOpenCobId.NmtCommand, new[] { (byte)NmtCommand.ResetCommunication, (byte)0 });
            BeginFlyingMaster();
            return;
        }
        BeginNegotiation(transmitTrigger: true);
    }

    private void BeginNegotiation(bool transmitTrigger)
    {
        if (!FlyingMasterEnabled || _disposed != 0) return;
        _flyingMasterFromPowerOn = false;
        _flyingMasterRole = FlyingMasterRole.Negotiating;
        if (transmitTrigger)
            _ = SendControlFrame(CanOpenCobId.FlyingMasterTrigger, Array.Empty<byte>());
        ArmNegotiationWait();
    }

    private void ArmNegotiationWait()
    {
        if (!TimingSeparatesPriorities())
        {
            StopFlyingMasterCore();
            RaiseFlyingMaster(FlyingMasterSignal.ConfigurationError, null, null);
            return;
        }
        ArmFlyingMaster(NegotiationWait(), OnFlyingMasterNegotiationElapsed);
    }

    private void OnFlyingMasterNegotiationElapsed()
    {
        if (_flyingMasterRole != FlyingMasterRole.Negotiating || !FlyingMasterEnabled) return;
        TransmitClaim();
        BecomeActive();
    }

    private void OnFlyingMasterTrigger()
    {
        if (!FlyingMasterEnabled || _flyingMasterRole == FlyingMasterRole.Inactive) return;
        // A trigger we did not send, and our own echo, both start or restart the wait. Restarting
        // on the echo measures the wait from the moment the trigger is on the bus.
        if (_flyingMasterRole == FlyingMasterRole.Negotiating)
            ArmNegotiationWait();
        else
            BeginNegotiation(transmitTrigger: false);
    }

    private void OnFlyingMasterClaim(ushort priority, byte nodeId)
    {
        if (nodeId == _nodeId || nodeId is < CanOpenCobId.MinNodeId or > CanOpenCobId.MaxNodeId) return;
        if (_flyingMasterRole is FlyingMasterRole.Inactive or FlyingMasterRole.Delaying) return;

        ushort ours = OurPriority();
        if (priority <= ours)
        {
            // Better, or equal. An equal claim does not depose the master that already holds the
            // role; during the race the claim that arrived first wins, which is this one.
            if (_flyingMasterRole == FlyingMasterRole.Active && priority == ours) return;
            EnterStandby(priority, nodeId);
            return;
        }

        if (_flyingMasterRole == FlyingMasterRole.Detecting)
        {
            _ = SendControlFrame(CanOpenCobId.FlyingMasterForce, Array.Empty<byte>());
            _flyingMasterFromPowerOn = false;
            BeginFlyingMaster();
            RaiseFlyingMaster(FlyingMasterSignal.ForcedRenegotiation, nodeId, priority);
            return;
        }

        _ = SendControlFrame(CanOpenCobId.FlyingMasterForce, Array.Empty<byte>());
        _flyingMasterFromPowerOn = false;
        BeginFlyingMaster();
        RaiseFlyingMaster(FlyingMasterSignal.ConfigurationError, nodeId, priority);
    }

    private void OnFlyingMasterForce()
    {
        if (!FlyingMasterEnabled || _flyingMasterRole == FlyingMasterRole.Inactive) return;
        _flyingMasterFromPowerOn = false;
        if (_flyingMasterRole == FlyingMasterRole.Active)
        {
            // The active master is who restarts the network. Apply it here as well as on the
            // wire: a node does act on its own NMT when the echo comes back, and applying it
            // now means a late echo cannot leave this node active across the next detection.
            // The echo, if it arrives, is a second warm boot of the same election.
            PerformNmtReset(communicationOnly: true);
            _ = SendControlFrame(CanOpenCobId.NmtCommand, new[] { (byte)NmtCommand.ResetCommunication, (byte)0 });
            return;
        }
        BeginFlyingMaster();
    }

    private void OnFlyingMasterDetectRequest()
    {
        if (_flyingMasterRole != FlyingMasterRole.Active) return;
        TransmitClaim();
    }

    private void OnFlyingMasterDetectCycle()
    {
        if (_flyingMasterRole != FlyingMasterRole.Active || !FlyingMasterEnabled) return;
        BeginNegotiation(transmitTrigger: true);
    }

    private void TransmitClaim()
    {
        _ = SendControlFrame(CanOpenCobId.FlyingMasterClaim, new[] { (byte)OurPriority(), _nodeId });
    }

    private void BecomeActive()
    {
        CancelFlyingMasterDeadline();
        bool changed = _flyingMasterRole != FlyingMasterRole.Active || _activeFlyingMasterNodeId != _nodeId;
        _flyingMasterFromPowerOn = false;
        _flyingMasterRole = FlyingMasterRole.Active;
        _flyingMasterReclaimStarted = false;
        _activeFlyingMasterNodeId = _nodeId;
        _activeFlyingMasterPriority = OurPriority();
        ReleaseInstalledWatch();
        EnsureHeartbeatProducer();
        if (changed) RaiseFlyingMaster(FlyingMasterSignal.BecameActive, null, null);
        int cycle = ReadTiming(TimingDetectCycle);
        if (cycle > 0)
            ArmFlyingMaster(TimeSpan.FromMilliseconds(cycle), OnFlyingMasterDetectCycle);
        BeginBootUp();
    }

    private void EnterStandby(ushort priority, byte nodeId)
    {
        CancelBootUp();
        CancelFlyingMasterDeadline();
        _flyingMasterFromPowerOn = false;
        bool changed = _flyingMasterRole != FlyingMasterRole.Standby
            || _activeFlyingMasterNodeId != nodeId
            || _activeFlyingMasterPriority != priority;
        _flyingMasterRole = FlyingMasterRole.Standby;
        _flyingMasterReclaimStarted = false;
        _activeFlyingMasterNodeId = nodeId;
        _activeFlyingMasterPriority = priority;
        WatchActiveMaster(nodeId);
        if (changed) RaiseFlyingMaster(FlyingMasterSignal.BecameStandby, nodeId, priority);
    }

    private void NoteFlyingMasterHeartbeatLost(byte producerNodeId)
    {
        if (_flyingMasterRole != FlyingMasterRole.Standby || _activeFlyingMasterNodeId != producerNodeId) return;
        if (_flyingMasterReclaimStarted) return;
        _flyingMasterReclaimStarted = true;
        byte lost = producerNodeId;
        ushort? priority = _activeFlyingMasterPriority;
        _flyingMasterFromPowerOn = false;
        BeginFlyingMaster();
        RaiseFlyingMaster(FlyingMasterSignal.ActiveMasterLost, lost, priority);
    }

    private void EnsureHeartbeatProducer()
    {
        if (_flyingMasterHeartbeatTimeout is not { } timeout) return;
        if (_od.ReadUnsigned(Co.ProducerHeartbeat, 0x00) != 0) return;
        int half = Math.Max(1, (int)timeout.TotalMilliseconds / 2);
        _od.WriteUnsigned(Co.ProducerHeartbeat, 0x00, (ushort)half);
    }

    private void WatchActiveMaster(byte nodeId)
    {
        if (_flyingMasterHeartbeatTimeout is not { } timeout) return;
        if (_flyingMasterInstalledWatch == nodeId) return;
        ReleaseInstalledWatch();
        if (_heartbeatConsumers.ContainsKey(nodeId)) return;
        AddHeartbeatConsumer(nodeId, timeout);
        _flyingMasterInstalledWatch = nodeId;
    }

    private void ReleaseInstalledWatch()
    {
        if (_flyingMasterInstalledWatch is not byte nodeId) return;
        _flyingMasterInstalledWatch = null;
        RemoveHeartbeatConsumer(nodeId);
    }

    private void StopFlyingMasterCore()
    {
        CancelBootUp();
        CancelFlyingMasterDeadline();
        _flyingMasterRole = FlyingMasterRole.Inactive;
        _activeFlyingMasterNodeId = null;
        _activeFlyingMasterPriority = null;
        _flyingMasterReclaimStarted = false;
        ReleaseInstalledWatch();
    }

    private void HandleFlyingMasterFrame(uint cobId, byte[] data)
    {
        switch (cobId)
        {
            case CanOpenCobId.FlyingMasterClaim:
                if (data.Length < 2) return;
                OnFlyingMasterClaim(data[0], data[1]);
                return;
            case CanOpenCobId.FlyingMasterTrigger:
                OnFlyingMasterTrigger();
                return;
            case CanOpenCobId.FlyingMasterDetect:
                OnFlyingMasterDetectRequest();
                return;
            case CanOpenCobId.FlyingMasterForce:
                OnFlyingMasterForce();
                return;
        }
    }

    private void ArmFlyingMaster(TimeSpan due, Action onDue)
    {
        _flyingMasterDeadline?.Dispose();
        int generation = ++_flyingMasterGeneration;
        if (due < TimeSpan.Zero) due = TimeSpan.Zero;
        _flyingMasterDeadline = _deadlines.Arm(due, () =>
        {
            if (generation != _flyingMasterGeneration || _disposed != 0) return;
            onDue();
        });
    }

    private void CancelFlyingMasterDeadline()
    {
        _flyingMasterGeneration++;
        _flyingMasterDeadline?.Dispose();
        _flyingMasterDeadline = null;
    }

    private ushort OurPriority() => (ushort)ReadTiming(TimingPriority);

    private int ReadTiming(byte subindex) => (int)_od.ReadUnsigned(Co.FlyingMasterTiming, subindex);

    private TimeSpan NegotiationWait()
    {
        long ms = (long)ReadTiming(TimingPriority) * ReadTiming(TimingPrioritySlot)
            + (long)_nodeId * ReadTiming(TimingDeviceSlot);
        if (ms < 0) ms = 0;
        return TimeSpan.FromMilliseconds(ms);
    }

    /// <summary>
    /// The priority slot has to be longer than every node-id slot of the next-worse level, so
    /// level 0 always finishes before level 1 whatever the node-ids are.
    /// </summary>
    private bool TimingSeparatesPriorities()
    {
        int deviceSlot = ReadTiming(TimingDeviceSlot);
        int prioritySlot = ReadTiming(TimingPrioritySlot);
        return deviceSlot > 0 && prioritySlot > 127 * deviceSlot;
    }

    private void RememberFlyingMasterPowerOn()
    {
        // The factory snapshot and the power-on snapshot start as one dictionary. Copying before
        // the first edit keeps "load" (1011h) able to return to flying master off.
        if (ReferenceEquals(_powerOnValues, _factoryDefaults))
        {
            var copy = new System.Collections.Generic.Dictionary<uint, byte[]>(_factoryDefaults.Count);
            foreach (var kv in _factoryDefaults)
                copy[kv.Key] = (byte[])kv.Value.Clone();
            _powerOnValues = copy;
        }
        RememberOne(Co.NmtStartup, 0x00);
        for (byte sub = 0; sub <= TimingDetectCycle; sub++)
            RememberOne(Co.FlyingMasterTiming, sub);
        // The cold Reset Communication would otherwise drop the network list and the boot
        // timeout, and the master that just won would start nobody.
        RememberOne(Co.BootTime, 0x00);
        for (byte sub = 0; sub <= CanOpenCobId.MaxNodeId; sub++)
            RememberOne(Co.SlaveAssignment, sub);
    }

    private void RememberOne(ushort index, byte subindex)
    {
        var raw = _od.ReadRaw(index, subindex);
        _powerOnValues[((uint)index << 8) | subindex] = (byte[])raw.Clone();
    }

    private void RaiseFlyingMaster(FlyingMasterSignal signal, byte? otherNodeId, ushort? otherPriority)
    {
        var args = new FlyingMasterChangedEventArgs(signal, _flyingMasterRole, otherNodeId, otherPriority);
        EnqueueEvent(() =>
        {
            try { FlyingMasterChanged?.Invoke(this, args); }
            catch (Exception ex) { RaiseBackgroundException(ex); }
        });
    }

    private OdWriteDecision ValidateFlyingMasterTimingWrite(byte subindex, byte[] value)
    {
        if (subindex == 0) return OdWriteDecision.Reject(SdoAbortCode.AttemptWriteReadOnly);
        if (subindex > TimingDetectCycle) return OdWriteDecision.Reject(SdoAbortCode.SubIndexDoesNotExist);
        if (value.Length != 2) return OdWriteDecision.Accept;
        ushort proposed = (ushort)(value[0] | (value[1] << 8));
        if (subindex == TimingPriority && proposed > 2)
            return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);
        if (subindex == TimingDeviceSlot && proposed == 0)
            return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);
        int deviceSlot = subindex == TimingDeviceSlot ? proposed : ReadTiming(TimingDeviceSlot);
        int prioritySlot = subindex == TimingPrioritySlot ? proposed : ReadTiming(TimingPrioritySlot);
        if ((subindex == TimingDeviceSlot || subindex == TimingPrioritySlot)
            && (deviceSlot <= 0 || prioritySlot <= 127 * deviceSlot))
            return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);
        return OdWriteDecision.Accept;
    }
}
