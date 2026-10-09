using System;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Pro.CANopen.Emcy;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Pdo;
using CanKit.Pro.CANopen.Safety;
using CanKit.Pro.CANopen.Sdo;

namespace CanKit.Pro.CANopen;

/// <summary>
/// CANopen Safety (CiA DSP 304 V1.0) on <see cref="CanOpenNode"/>: the objects 1300h–13FFh as
/// managed communication-profile objects, their validation, and — in later parts of this file —
/// the safety facade and the wiring of the SRDO engine.
/// Opt-in: with <c>_srdoCount == 0</c> none of this exists in the dictionary.
/// </summary>
internal sealed partial class CanOpenNode : ICanOpenSafety, ISrdoEngineHost
{
    private readonly int _srdoCount;

    // True while RestoreValues runs, under the dictionary's write gate: the 13FEh auto-reset is
    // for configuration writes, not for putting a stored configuration back.
    private bool _restoringValues;

    /// <summary>Opt-in: without SRDOs none of 1300h-13FFh belongs to the node, and an SRDO record
    /// above the count is not created, so an application may declare it itself.</summary>
    private bool IsManagedSafetyObject(ushort index)
        => _srdoCount > 0
           && (index == Co.GfcParameter || index == Co.SrdoConfigurationValid || index == Co.SrdoChecksum
               || (SrdoRecords.SrdoNumberOf(index) is { } n && n <= _srdoCount));

    /// <summary>The highest SRDO record a description declares (1301h–1340h, 1381h–13C0h); 0 without one.</summary>
    private static int DescribedSrdoCount(CanOpenDeviceDescription? description)
    {
        if (description is null) return 0;
        int highest = 0;
        foreach (var index in description.Objects.Objects.Keys)
        {
            if (SrdoRecords.SrdoNumberOf(index) is { } n) highest = Math.Max(highest, n);
        }
        return highest;
    }

    /// <summary>§8.4.2.2 defaults. Bus access follows WritableCommunicationParameters, as the PDO
    /// records do (Table 6 footnote: "These may be read only"); sub0 and sub4 are const.</summary>
    private void PopulateSafetyObjects()
    {
        var acc = _options.WritableCommunicationParameters ? OdAccess.ReadWrite : OdAccess.ReadOnly;
        const OdAccess ro = OdAccess.ReadOnly;
        const bool nm = false;
        _od.AddU8(Co.GfcParameter, 0x00, 0, acc, nm);
        for (int n = 1; n <= _srdoCount; n++)
        {
            var comm = SrdoRecords.CommIndex(n);
            var map = SrdoRecords.MapIndex(n);
            bool predefined = n == 1 && _nodeId <= 64;
            _od.AddU8(comm, 0x00, 6, ro, nm);
            _od.AddU8(comm, 0x01, (byte)SrdoDirection.None, acc, nm);
            _od.AddU16(comm, 0x02, 25, acc, nm);
            _od.AddU8(comm, 0x03, 20, acc, nm);
            _od.AddU8(comm, 0x04, SrdoRecords.TransmissionType, ro, nm);
            _od.AddU32(comm, 0x05, predefined ? CanOpenCobId.SrdoDefaultCobId1(_nodeId) : 0u, acc, nm);
            _od.AddU32(comm, 0x06, predefined ? CanOpenCobId.SrdoDefaultCobId2(_nodeId) : 0u, acc, nm);
            _od.AddU8(map, 0x00, 0, acc, nm);
            for (byte s = 1; s <= SrdoRecords.MappingSubindices; s++) _od.AddU32(map, s, 0, acc, nm);
        }
        _od.AddU8(Co.SrdoConfigurationValid, 0x00, 0, acc, nm);
        _od.AddU8(Co.SrdoChecksum, 0x00, (byte)_srdoCount, ro, nm);
        for (byte n = 1; n <= _srdoCount; n++) _od.AddU16(Co.SrdoChecksum, n, 0, acc, nm);
    }

    // =========================================================================================
    // Validation (writing thread, before the store). Every rule cites DSP 304 V1.0.
    // =========================================================================================

    private OdWriteDecision ValidateSafetyWrite(ushort index, byte subindex, byte[] value)
    {
        // §8.3.2.4 note 1: "Writing to a safety entry in the OPERATIONAL state leads to an abort
        // message (abort code: 0800 0022h). Reading … is allowed."
        if (SrdoRecords.IsStateGated(index) && _state == NmtState.Operational)
            return OdWriteDecision.Reject(SdoAbortCode.DataCannotBeTransferredDeviceState);
        if (index == Co.GfcParameter)
            return value[0] <= 1 ? OdWriteDecision.Accept : OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded); // 0 not valid, 1 valid
        if (index == Co.SrdoConfigurationValid)
            return OdWriteDecision.Accept; // any value; only A5h means valid
        if (index == Co.SrdoChecksum)
            return subindex == 0 ? OdWriteDecision.Reject(SdoAbortCode.AttemptWriteReadOnly) : OdWriteDecision.Accept;
        if (SrdoRecords.IsCommunicationRecord(index))
            return ValidateSrdoCommunicationWrite(index, subindex, value);
        return ValidateSrdoMappingWrite(index, subindex, value);
    }

    private OdWriteDecision ValidateSrdoCommunicationWrite(ushort index, byte subindex, byte[] value)
    {
        int n = index - SrdoRecords.CommunicationBase;
        switch (subindex)
        {
            case 0x00:
                return OdWriteDecision.Reject(SdoAbortCode.AttemptWriteReadOnly);
            case 0x01:
                {
                    byte direction = value[0];
                    if (direction > 2) return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded); // 3..255 reserved
                    if (direction == 0) return OdWriteDecision.Accept;
                    // Creating: both COB-IDs must be set and free, and the mapped objects must be
                    // accessible in this direction (a producer reads them, a consumer writes them).
                    uint cob1 = _od.ReadUnsigned(index, 0x05) & CanOpenCobId.CanIdMask;
                    uint cob2 = _od.ReadUnsigned(index, 0x06) & CanOpenCobId.CanIdMask;
                    if (!SrdoFrames.IsCobId1(cob1) || !SrdoFrames.IsCobId2(cob2) || cob2 != cob1 + 1)
                        return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);
                    if (IsCanIdOfAnExistingSrdo(cob1, n) || IsCanIdOfAnExistingSrdo(cob2, n))
                        return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);
                    foreach (var entry in SrdoRecords.ReadMapping(_od, n))
                    {
                        uint raw = ((uint)entry.Index << 16) | ((uint)entry.Subindex << 8) | entry.BitLength;
                        if (ValidateSrdoMappingEntry(raw, (SrdoDirection)direction) is { } abort) return OdWriteDecision.Reject(abort);
                    }
                    return OdWriteDecision.Accept;
                }
            case 0x02:
                return (value[0] | (value[1] << 8)) == 0 ? OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded) : OdWriteDecision.Accept; // 1..65535
            case 0x03:
                return value[0] == 0 ? OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded) : OdWriteDecision.Accept; // 1..255
            case 0x04:
                // "On an attempt to change the value of the transmission type an abort message
                // (abort code: 0609 0030h) is generated."
                return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);
            case 0x05:
            case 0x06:
                {
                    uint word = ObjectDictionary.DecodeU32(value);
                    if ((word & ~CanOpenCobId.CanIdMask) != 0) return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded); // Figure 7: bits 31..11 reserved (= 0)
                    uint current = _od.ReadUnsigned(index, subindex);
                    // "It is not allowed to change the COB-ID 1 or COB-ID 2 while the SRDO exists."
                    if (_od.ReadUnsigned(index, 0x01) != 0 && word != current) return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);
                    if (word == 0) return OdWriteDecision.Accept; // disabled
                    bool inRange = subindex == 0x05 ? SrdoFrames.IsCobId1(word) : SrdoFrames.IsCobId2(word);
                    if (!inRange) return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);
                    if (subindex == 0x06 && word != (_od.ReadUnsigned(index, 0x05) & CanOpenCobId.CanIdMask) + 1)
                        return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded); // "two following COB-IDs"
                    if (IsCanIdOfAnExistingSrdo(word, n)) return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);
                    return OdWriteDecision.Accept;
                }
            default:
                return OdWriteDecision.Reject(SdoAbortCode.SubIndexDoesNotExist);
        }
    }

    /// <summary>One CAN-ID carries one communication object (#133). PDO, SYNC and EMCY
    /// cannot sit on 101h–180h at all: CiA 301 Table 40 restricts the range and their validators
    /// refuse it, so only SRDO against SRDO is checked here.</summary>
    private bool IsCanIdOfAnExistingSrdo(uint canId, int exceptSrdo)
    {
        for (int other = 1; other <= _srdoCount; other++)
        {
            if (other == exceptSrdo) continue;
            var comm = SrdoRecords.CommIndex(other);
            if (!_od.TryReadUnsigned(comm, 0x01, out var direction) || direction == 0) continue;
            if ((_od.ReadUnsigned(comm, 0x05) & CanOpenCobId.CanIdMask) == canId) return true;
            if ((_od.ReadUnsigned(comm, 0x06) & CanOpenCobId.CanIdMask) == canId) return true;
        }
        return false;
    }

    private OdWriteDecision ValidateSrdoMappingWrite(ushort index, byte subindex, byte[] value)
    {
        int n = index - SrdoRecords.MappingBase;
        // "For changing the SRDO mapping first the SRDO shall be deleted." — same code as the
        // PDO re-mapping procedure uses for a live PDO.
        if (_od.TryReadUnsigned(SrdoRecords.CommIndex(n), 0x01, out var direction) && direction != 0)
            return OdWriteDecision.Reject(SdoAbortCode.UnsupportedAccess);
        if (subindex == 0x00)
        {
            byte count = value[0];
            if (count == 0) return OdWriteDecision.Accept; // deactivated
            if ((count & 1) != 0 || count > SrdoRecords.MappingSubindices) return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded); // 2, 4 … 16
            int total = 0;
            for (byte s = 1; s <= count; s++)
            {
                if (!_od.TryReadUnsigned(index, s, out var raw) || raw == 0) return OdWriteDecision.Reject(SdoAbortCode.ObjectDoesNotExist);
                if ((s & 1) == 0)
                {
                    if (raw != _od.ReadUnsigned(index, (byte)(s - 1))) return OdWriteDecision.Reject(SdoAbortCode.ObjectCannotBeMapped); // inverted slot repeats the plain slot
                    continue;
                }
                if (ValidateSrdoMappingEntry(raw, SrdoDirection.None) is { } abort) return OdWriteDecision.Reject(abort);
                total += (int)(raw & 0xFF) / 8;
            }
            return total > 8 ? OdWriteDecision.Reject(SdoAbortCode.PdoMappingLengthExceeded) : OdWriteDecision.Accept;
        }
        if (_od.ReadUnsigned(index, 0x00) != 0) return OdWriteDecision.Reject(SdoAbortCode.UnsupportedAccess);
        uint entry = ObjectDictionary.DecodeU32(value);
        if (entry == 0) return OdWriteDecision.Accept;
        if (ValidateSrdoMappingEntry(entry, SrdoDirection.None) is { } reason) return OdWriteDecision.Reject(reason);
        if ((subindex & 1) == 0)
        {
            uint plain = _od.ReadUnsigned(index, (byte)(subindex - 1));
            return plain != 0 && plain != entry ? OdWriteDecision.Reject(SdoAbortCode.ObjectCannotBeMapped) : OdWriteDecision.Accept;
        }
        int precedingBytes = 0;
        for (byte s = 1; s < subindex; s += 2)
        {
            if (_od.TryReadUnsigned(index, s, out var earlier)) precedingBytes += (int)(earlier & 0xFF) / 8;
        }
        return precedingBytes + (int)(entry & 0xFF) / 8 > 8
            ? OdWriteDecision.Reject(SdoAbortCode.PdoMappingLengthExceeded)
            : OdWriteDecision.Accept;
    }

    /// <summary>Like <see cref="ValidateMappingEntry"/> for PDOs, minus dummies (safety data is
    /// never padding) and with the access check deferred to the direction write when
    /// <paramref name="direction"/> is None (the mapping is written while the SRDO is deleted).</summary>
    private SdoAbortCode? ValidateSrdoMappingEntry(uint raw, SrdoDirection direction)
    {
        var entryIndex = (ushort)((raw >> 16) & 0xFFFF);
        var entrySub = (byte)((raw >> 8) & 0xFF);
        var bitLength = (byte)(raw & 0xFF);
        if (bitLength == 0 || bitLength > 64 || bitLength % 8 != 0) return SdoAbortCode.DataTypeLengthMismatch;
        if (entryIndex is >= 0x1000 and <= 0x1FFF) return SdoAbortCode.ObjectCannotBeMapped;
        if (entrySub == 0 && PdoMappingEntry.DummyBitLength(entryIndex) != 0) return SdoAbortCode.ObjectCannotBeMapped;
        if (!_od.TryGet(entryIndex, entrySub, out var target)) return SdoAbortCode.ObjectDoesNotExist;
        if (!target.PdoMappable) return SdoAbortCode.ObjectCannotBeMapped;
        if (direction == SrdoDirection.Transmit && (target.Access & OdAccess.ReadOnly) == 0) return SdoAbortCode.ObjectCannotBeMapped;
        if (direction == SrdoDirection.Receive && (target.Access & OdAccess.WriteOnly) == 0) return SdoAbortCode.ObjectCannotBeMapped;
        int fixedSize = OdEntryLayout.FixedSize(target.DataType);
        if (fixedSize > 0 && bitLength / 8 != fixedSize) return SdoAbortCode.ObjectCannotBeMapped;
        return null;
    }

    private readonly SrdoEngine _srdo;   // constructed in CanOpenNode's constructor, see below

    // ---- ICanOpenSafety ----------------------------------------------------------------------

    public event EventHandler<SrdoReceivedEventArgs>? SrdoReceived;
    public event EventHandler<SrdoStateChangedEventArgs>? SrdoStateChanged;
    public event EventHandler<GlobalFailsafeCommandReceivedEventArgs>? GlobalFailsafeCommandReceived;

    public int SrdoCount => _srdoCount;

    public void ConfigureSrdoProducer(int srdoNumber, SrdoMapping mapping, TimeSpan refreshTime, uint? cobId1 = null, uint? cobId2 = null)
        => ConfigureSrdo(srdoNumber, SrdoDirection.Transmit, mapping, refreshTime, null, cobId1, cobId2, nameof(refreshTime));

    public void ConfigureSrdoConsumer(int srdoNumber, SrdoMapping mapping, TimeSpan safeguardCycleTime, TimeSpan validationTime, uint? cobId1 = null, uint? cobId2 = null)
        => ConfigureSrdo(srdoNumber, SrdoDirection.Receive, mapping, safeguardCycleTime, validationTime, cobId1, cobId2, nameof(safeguardCycleTime));

    private void ConfigureSrdo(int srdoNumber, SrdoDirection direction, SrdoMapping mapping, TimeSpan cycle, TimeSpan? validation,
        uint? cobId1, uint? cobId2, string cycleParamName)
    {
        ThrowIfDisposed();
        RequireSrdo(srdoNumber);
        if (mapping is null) throw new ArgumentNullException(nameof(mapping));
        ushort cycleMs = ToMilliseconds16(cycle, cycleParamName, allowZero: false);
        byte srvtMs = 0;
        if (validation is { } v)
        {
            long ms = (long)Math.Round(v.TotalMilliseconds);
            if (ms is < 1 or > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(validation), v, "SRVT must be 1 ms .. 255 ms: 1301h:03 is an UNSIGNED8 in ms (CiA DSP 304 §8.4.2.2).");
            srvtMs = (byte)ms;
        }
        uint id1 = cobId1 ?? (srdoNumber == 1 && _nodeId <= 64
            ? CanOpenCobId.SrdoDefaultCobId1(_nodeId)
            : throw new ArgumentException("No pre-defined COB-ID for this SRDO (only SRDO 1 of a node-id 1..64 has one, CiA DSP 304 §8.3.3); pass cobId1.", nameof(cobId1)));
        uint id2 = cobId2 ?? id1 + 1;
        var entries = mapping.ToArray();
        RunOnActorAndWait(() =>
        {
            RequireNotOperationalForSafetyWrite();
            var comm = SrdoRecords.CommIndex(srdoNumber);
            var map = SrdoRecords.MapIndex(srdoNumber);
            try
            {
                _od.Transaction(() =>
                {
                    _od.WriteUnsigned(comm, 0x01, 0);
                    _od.WriteUnsigned(map, 0x00, 0);
                    for (byte s = 1; s <= SrdoRecords.MappingSubindices; s++)
                    {
                        int i = (s - 1) / 2;
                        _od.WriteUnsigned(map, s, i < entries.Length ? EncodeMappingEntry(entries[i]) : 0u);
                    }
                    _od.WriteUnsigned(map, 0x00, (uint)(2 * entries.Length));
                    _od.WriteUnsigned(comm, 0x02, cycleMs);
                    if (direction == SrdoDirection.Receive) _od.WriteUnsigned(comm, 0x03, srvtMs);
                    _od.WriteUnsigned(comm, 0x05, id1);
                    _od.WriteUnsigned(comm, 0x06, id2);
                    _od.WriteUnsigned(comm, 0x01, (byte)direction);
                });
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"SRDO{srdoNumber} configuration rejected: {ex.Message}", ex);
            }
        });
    }

    public void DeleteSrdo(int srdoNumber)
    {
        ThrowIfDisposed();
        RequireSrdo(srdoNumber);
        RunOnActorAndWait(() =>
        {
            RequireNotOperationalForSafetyWrite();
            _od.WriteUnsigned(SrdoRecords.CommIndex(srdoNumber), 0x01, 0);
        });
    }

    public void CommitSafetyConfiguration()
    {
        ThrowIfDisposed();
        if (_srdoCount == 0) throw NoSrdos();
        RunOnActorAndWait(() => _od.Transaction(() =>
        {
            RequireNotOperationalForSafetyWrite();
            // Every 13FFh:n write clears 13FEh, so the checksums go first and A5h last (§9.2).
            for (int n = 1; n <= _srdoCount; n++)
            {
                ushort crc = 0;
                if (SrdoRecords.TryReadCommunication(_od, n, out var p) && p.Direction != SrdoDirection.None)
                    crc = SrdoCrc.Compute(p, SrdoMapping.FromEntries(SrdoRecords.ReadMapping(_od, n)));
                _od.WriteUnsigned(Co.SrdoChecksum, (byte)n, crc);
            }
            _od.WriteUnsigned(Co.SrdoConfigurationValid, 0x00, SrdoRecords.ConfigurationValidValue);
        }));
    }

    public Task TriggerSrdoAsync(int srdoNumber, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RequireSrdo(srdoNumber);
        return _actor.PostAsync(() => _srdo.Trigger(srdoNumber), cancellationToken);
    }

    // Range check only (the engine's): a node without SRDOs has no number in range, which is an
    // ArgumentOutOfRangeException like any other number outside 1..SrdoCount.
    public SrdoState GetSrdoState(int srdoNumber) => _srdo.GetState(srdoNumber);

    public Task SendGlobalFailsafeCommandAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _actor.PostAsync(() =>
        {
            if (!_srdo.TrySendGfc())
                throw new InvalidOperationException("The global failsafe command needs 1300h = 1 and NMT state Operational (CiA DSP 304 §8.2).");
        }, cancellationToken);
    }

    private void RequireSrdo(int srdoNumber)
    {
        if (_srdoCount == 0) throw NoSrdos();
        if (srdoNumber < 1 || srdoNumber > _srdoCount)
            throw new ArgumentOutOfRangeException(nameof(srdoNumber), srdoNumber, $"SRDO number must be 1..{_srdoCount}.");
    }

    private static InvalidOperationException NoSrdos()
        => new("This node has no SRDOs: open it with CanOpenNodeOptions.SrdoCount > 0 or with a device description that declares SRDO records.");

    /// <summary>§8.3.2.4 note 1, surfaced as the exception the caller can act on. Called on the
    /// actor loop, where the NMT state is authoritative: no transition can come between the check
    /// and the writes that follow it in the same actor turn.</summary>
    private void RequireNotOperationalForSafetyWrite()
    {
        if (_state == NmtState.Operational)
            throw new InvalidOperationException("Safety parameters cannot be written in NMT state Operational (abort 0800 0022h, CiA DSP 304 §8.3.2.4).");
    }

    // ---- ISrdoEngineHost (actor loop) ----------------------------------------------------------

    // Every SRDO pair of this node goes out through one chain, plain frame then inverted frame,
    // in the order the engine sent them on the actor: §8.1 "the redundant transmission is sent
    // after the first transmission", and §9.5 has the consumer refuse a pair whose inverted frame
    // comes first. Two independent SendControlFrame calls are two Task.Runs that can overtake
    // each other. Actor loop only.
    private Task _srdoSendChain = Task.CompletedTask;

    // At most one pair per SRDO on that chain, plus one waiting. A link ends when both frames
    // are confirmed or have failed or timed out, and on a bus where confirmations stall the cycles
    // would otherwise queue pairs faster than they drain, and the backlog would later reach the
    // consumer as current data. A pair due while the previous one of the same SRDO is still in
    // flight becomes that SRDO's pending pair, replacing any pending one (the latest data wins),
    // and goes on the chain the moment the in-flight pair completes: an event-driven
    // transmission is §8.1's "fast reaction after a safety critical change" and must not wait a
    // whole refresh cycle. Under a stall the drained backlog is therefore at most two pairs per
    // SRDO. Actor-only, like every other piece of runtime state: both are set and read on the
    // actor, and the continuation that ends a link posts its completion there, so no cycle can
    // see a half-updated state and no lock is needed.
    private readonly bool[] _srdoInFlight;
    private readonly SrdoPair?[] _srdoPending;

    private sealed record SrdoPair(uint CobId1, byte[] Plain, uint CobId2, byte[] Inverted);

    // The GFC is not an SRDO and does not wait behind them (§8.2: it is the highest-priority
    // safety message); it goes out at once, as every other control frame of the node.
    void ISrdoEngineHost.Send(uint cobId, byte[] payload) => _ = SendControlFrame(cobId, payload);

    void ISrdoEngineHost.SendPair(int srdoNumber, uint cobId1, byte[] plain, uint cobId2, byte[] inverted)
    {
        var pair = new SrdoPair(cobId1, plain, cobId2, inverted);
        if (_srdoInFlight[srdoNumber])
        {
            _srdoPending[srdoNumber] = pair;
            return;
        }
        QueueSrdoPair(srdoNumber, pair);
    }

    private void QueueSrdoPair(int srdoNumber, SrdoPair pair)
    {
        _srdoInFlight[srdoNumber] = true;
        var link = _srdoSendChain.ContinueWith(
            _ => SendSrdoPairAsync(pair),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        _srdoSendChain = link;
        _ = link.ContinueWith(
            _ => RunOnActor(() => OnSrdoPairCompleted(srdoNumber)),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    // The pending pair goes only while the SRDOs still run: leaving Operational (or disposing)
    // between its coming due and this completion means it must not reach the bus (§8.3.2.2).
    private void OnSrdoPairCompleted(int srdoNumber)
    {
        var pending = _srdoPending[srdoNumber];
        _srdoPending[srdoNumber] = null;
        if (pending is not null && _srdo.IsOperational && Volatile.Read(ref _disposed) == 0)
            QueueSrdoPair(srdoNumber, pending);
        else
            _srdoInFlight[srdoNumber] = false;
    }

    /// <summary>Leaving Operational: the engine stops, and a pair still pending for the bus is
    /// dropped with it. Actor only.</summary>
    private void LeaveSrdoOperational()
    {
        _srdo.LeaveOperational();
        Array.Clear(_srdoPending, 0, _srdoPending.Length);
    }

    /// <summary>Test seam, actor only: the tail of the SRDO send chain.</summary>
    internal Task SrdoSendChainForTests => _srdoSendChain;

    /// <summary>Test seam, actor only: whether a pair of SRDO <paramref name="srdoNumber"/> is in flight.</summary>
    internal bool SrdoPairInFlightForTests(int srdoNumber) => _srdoInFlight[srdoNumber];

    // A failed or unconfirmed frame is reported by SendControlFrame itself and the inverted frame
    // still follows, as it would on the controller; only a cancelled send (the service is being
    // disposed) ends the pair early.
    private async Task SendSrdoPairAsync(SrdoPair pair)
    {
        await SendControlFrame(pair.CobId1, pair.Plain).ConfigureAwait(false);
        await SendControlFrame(pair.CobId2, pair.Inverted).ConfigureAwait(false);
    }

    void ISrdoEngineHost.EmitEmcy(ushort errorCode)
    {
        if (!_emcyValid) return;
        var errorRegister = (byte)_od.ReadUnsigned(Co.ErrorRegister, 0x00);
        _ = EmitEmcy(new EmcyMessage(_nodeId, errorCode, errorRegister));
    }

    void ISrdoEngineHost.SrdoReceived(int srdoNumber, uint cobId, byte[] payload) => RaiseSrdoReceived(srdoNumber, cobId, payload);
    void ISrdoEngineHost.SrdoStateChanged(int srdoNumber, bool isValid, SrdoInvalidReason? reason) => RaiseSrdoStateChanged(srdoNumber, isValid, reason);
    void ISrdoEngineHost.GlobalFailsafeCommandReceived() => RaiseGlobalFailsafeCommandReceived();
    void ISrdoEngineHost.ReportBackgroundException(Exception exception) => RaiseBackgroundException(exception);

    private void RaiseSrdoReceived(int srdoNumber, uint cobId, byte[] payload)
    {
        var args = new SrdoReceivedEventArgs(srdoNumber, cobId, payload, DateTime.UtcNow);
        EnqueueEvent(() =>
        {
            try { DeliverToSubscribers(SrdoReceived, args); }
            catch (Exception ex) { RaiseBackgroundException(ex); }
        });
    }

    private void RaiseSrdoStateChanged(int srdoNumber, bool isValid, SrdoInvalidReason? reason)
    {
        var args = new SrdoStateChangedEventArgs(srdoNumber, isValid, reason, DateTime.UtcNow);
        EnqueueEvent(() =>
        {
            try { DeliverToSubscribers(SrdoStateChanged, args); }
            catch (Exception ex) { RaiseBackgroundException(ex); }
        }, critical: true, EventKey.SrdoState(srdoNumber, isValid, reason), emcyProducer: -1,
            producer: EventKey.SrdoProducerSlot(srdoNumber));
    }

    private void RaiseGlobalFailsafeCommandReceived()
    {
        var args = new GlobalFailsafeCommandReceivedEventArgs(DateTime.UtcNow);
        EnqueueEvent(() =>
        {
            try { DeliverToSubscribers(GlobalFailsafeCommandReceived, args); }
            catch (Exception ex) { RaiseBackgroundException(ex); }
        }, critical: true, EventKey.GlobalFailsafeCommand(), emcyProducer: -1,
            producer: EventKey.GfcProducerSlot);
    }
}
