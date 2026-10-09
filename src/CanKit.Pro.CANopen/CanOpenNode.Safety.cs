using System;
using System.Collections.Generic;
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
    private bool IsManagedSafetyObject(ushort index) => IsManagedSafetyObject(_srdoCount, index);

    private static bool IsManagedSafetyObject(int srdoCount, ushort index)
        => srdoCount > 0
           && (index == Co.GfcParameter || index == Co.SrdoConfigurationValid || index == Co.SrdoChecksum
               || (SrdoRecords.SrdoNumberOf(index) is { } n && n <= srdoCount));

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
    private void PopulateSafetyObjects() => PopulateSafetyObjects(_od, _nodeId, _srdoCount, _options.WritableCommunicationParameters);

    /// <summary>Static, as every rule of this file down to the mapping entry, so that
    /// <see cref="LoadDescribedSafetyObjects"/> runs the same code over a dictionary of its own.</summary>
    private static void PopulateSafetyObjects(ObjectDictionary od, byte nodeId, int srdoCount, bool writable)
    {
        var acc = writable ? OdAccess.ReadWrite : OdAccess.ReadOnly;
        const OdAccess ro = OdAccess.ReadOnly;
        const bool nm = false;
        od.AddU8(Co.GfcParameter, 0x00, 0, acc, nm);
        for (int n = 1; n <= srdoCount; n++)
        {
            var comm = SrdoRecords.CommIndex(n);
            var map = SrdoRecords.MapIndex(n);
            bool predefined = n == 1 && nodeId <= 64;
            od.AddU8(comm, 0x00, 6, ro, nm);
            od.AddU8(comm, 0x01, (byte)SrdoDirection.None, acc, nm);
            od.AddU16(comm, 0x02, 25, acc, nm);
            od.AddU8(comm, 0x03, 20, acc, nm);
            od.AddU8(comm, 0x04, SrdoRecords.TransmissionType, ro, nm);
            od.AddU32(comm, 0x05, predefined ? CanOpenCobId.SrdoDefaultCobId1(nodeId) : 0u, acc, nm);
            od.AddU32(comm, 0x06, predefined ? CanOpenCobId.SrdoDefaultCobId2(nodeId) : 0u, acc, nm);
            od.AddU8(map, 0x00, 0, acc, nm);
            for (byte s = 1; s <= SrdoRecords.MappingSubindices; s++) od.AddU32(map, s, 0, acc, nm);
        }
        od.AddU8(Co.SrdoConfigurationValid, 0x00, 0, acc, nm);
        od.AddU8(Co.SrdoChecksum, 0x00, (byte)srdoCount, ro, nm);
        for (byte n = 1; n <= srdoCount; n++) od.AddU16(Co.SrdoChecksum, n, 0, acc, nm);
    }

    // =========================================================================================
    // Validation (writing thread, before the store). Every rule cites DSP 304 V1.0.
    // =========================================================================================

    private OdWriteDecision ValidateSafetyWrite(ushort index, byte subindex, byte[] value)
        => ValidateSafetyWrite(_od, _srdoCount, _state == NmtState.Operational, index, subindex, value);

    private static OdWriteDecision ValidateSafetyWrite(ObjectDictionary od, int srdoCount, bool operational,
        ushort index, byte subindex, byte[] value)
    {
        // §8.3.2.4 note 1: "Writing to a safety entry in the OPERATIONAL state leads to an abort
        // message (abort code: 0800 0022h). Reading … is allowed." This runs under the write gate,
        // and the transition into Operational publishes the state under the same gate
        // (ApplyNmtTransition), so the node cannot enter Operational between this read and the
        // store: a write that saw another state is stored before the engine arms, and the arming
        // sees it. Other transitions take no gate; none of them makes a write unsafe.
        if (SrdoRecords.IsStateGated(index) && operational)
            return OdWriteDecision.Reject(SdoAbortCode.DataCannotBeTransferredDeviceState);
        if (index == Co.GfcParameter)
            return value[0] <= 1 ? OdWriteDecision.Accept : OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded); // 0 not valid, 1 valid
        if (index == Co.SrdoConfigurationValid)
            return OdWriteDecision.Accept; // any value; only A5h means valid
        if (index == Co.SrdoChecksum)
            return subindex == 0 ? OdWriteDecision.Reject(SdoAbortCode.AttemptWriteReadOnly) : OdWriteDecision.Accept;
        if (SrdoRecords.IsCommunicationRecord(index))
            return ValidateSrdoCommunicationWrite(od, srdoCount, index, subindex, value);
        return ValidateSrdoMappingWrite(od, index, subindex, value);
    }

    private static OdWriteDecision ValidateSrdoCommunicationWrite(ObjectDictionary od, int srdoCount, ushort index, byte subindex, byte[] value)
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
                    uint cob1 = od.ReadUnsigned(index, 0x05) & CanOpenCobId.CanIdMask;
                    uint cob2 = od.ReadUnsigned(index, 0x06) & CanOpenCobId.CanIdMask;
                    if (!SrdoFrames.IsCobIdPair(cob1, cob2))
                        return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);
                    if (IsCanIdOfAnExistingSrdo(od, srdoCount, cob1, n) || IsCanIdOfAnExistingSrdo(od, srdoCount, cob2, n))
                        return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);
                    foreach (var entry in SrdoRecords.ReadMapping(od, n))
                    {
                        uint raw = ((uint)entry.Index << 16) | ((uint)entry.Subindex << 8) | entry.BitLength;
                        if (ValidateSrdoMappingEntry(od, raw, (SrdoDirection)direction) is { } abort) return OdWriteDecision.Reject(abort);
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
                    uint current = od.ReadUnsigned(index, subindex);
                    // "It is not allowed to change the COB-ID 1 or COB-ID 2 while the SRDO exists."
                    if (od.ReadUnsigned(index, 0x01) != 0 && word != current) return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);
                    if (word == 0) return OdWriteDecision.Accept; // disabled
                    bool inRange = subindex == 0x05 ? SrdoFrames.IsCobId1(word) : SrdoFrames.IsCobId2(word);
                    if (!inRange) return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);
                    if (subindex == 0x06 && !SrdoFrames.IsCobIdPair(od.ReadUnsigned(index, 0x05) & CanOpenCobId.CanIdMask, word))
                        return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded); // "two following COB-IDs", the one rule Add applies too
                    if (IsCanIdOfAnExistingSrdo(od, srdoCount, word, n)) return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);
                    return OdWriteDecision.Accept;
                }
            default:
                return OdWriteDecision.Reject(SdoAbortCode.SubIndexDoesNotExist);
        }
    }

    /// <summary>One CAN-ID carries one communication object (#133). PDO, SYNC and EMCY
    /// cannot sit on 101h–180h at all: CiA 301 Table 40 restricts the range and their validators
    /// refuse it, so only SRDO against SRDO is checked here.</summary>
    private static bool IsCanIdOfAnExistingSrdo(ObjectDictionary od, int srdoCount, uint canId, int exceptSrdo)
    {
        for (int other = 1; other <= srdoCount; other++)
        {
            if (other == exceptSrdo) continue;
            var comm = SrdoRecords.CommIndex(other);
            if (!od.TryReadUnsigned(comm, 0x01, out var direction) || direction == 0) continue;
            if ((od.ReadUnsigned(comm, 0x05) & CanOpenCobId.CanIdMask) == canId) return true;
            if ((od.ReadUnsigned(comm, 0x06) & CanOpenCobId.CanIdMask) == canId) return true;
        }
        return false;
    }

    private static OdWriteDecision ValidateSrdoMappingWrite(ObjectDictionary od, ushort index, byte subindex, byte[] value)
    {
        int n = index - SrdoRecords.MappingBase;
        // "For changing the SRDO mapping first the SRDO shall be deleted." — same code as the
        // PDO re-mapping procedure uses for a live PDO.
        if (od.TryReadUnsigned(SrdoRecords.CommIndex(n), 0x01, out var direction) && direction != 0)
            return OdWriteDecision.Reject(SdoAbortCode.UnsupportedAccess);
        if (subindex == 0x00)
        {
            byte count = value[0];
            if (count == 0) return OdWriteDecision.Accept; // deactivated
            if ((count & 1) != 0 || count > SrdoRecords.MappingSubindices) return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded); // 2, 4 … 16
            int total = 0;
            for (byte s = 1; s <= count; s++)
            {
                if (!od.TryReadUnsigned(index, s, out var raw) || raw == 0) return OdWriteDecision.Reject(SdoAbortCode.ObjectDoesNotExist);
                if ((s & 1) == 0)
                {
                    if (raw != od.ReadUnsigned(index, (byte)(s - 1))) return OdWriteDecision.Reject(SdoAbortCode.ObjectCannotBeMapped); // inverted slot repeats the plain slot
                    continue;
                }
                if (ValidateSrdoMappingEntry(od, raw, SrdoDirection.None) is { } abort) return OdWriteDecision.Reject(abort);
                total += (int)(raw & 0xFF) / 8;
            }
            return total > 8 ? OdWriteDecision.Reject(SdoAbortCode.PdoMappingLengthExceeded) : OdWriteDecision.Accept;
        }
        if (od.ReadUnsigned(index, 0x00) != 0) return OdWriteDecision.Reject(SdoAbortCode.UnsupportedAccess);
        uint entry = ObjectDictionary.DecodeU32(value);
        if (entry == 0) return OdWriteDecision.Accept;
        if (ValidateSrdoMappingEntry(od, entry, SrdoDirection.None) is { } reason) return OdWriteDecision.Reject(reason);
        if ((subindex & 1) == 0)
        {
            uint plain = od.ReadUnsigned(index, (byte)(subindex - 1));
            return plain != 0 && plain != entry ? OdWriteDecision.Reject(SdoAbortCode.ObjectCannotBeMapped) : OdWriteDecision.Accept;
        }
        int precedingBytes = 0;
        for (byte s = 1; s < subindex; s += 2)
        {
            if (od.TryReadUnsigned(index, s, out var earlier)) precedingBytes += (int)(earlier & 0xFF) / 8;
        }
        return precedingBytes + (int)(entry & 0xFF) / 8 > 8
            ? OdWriteDecision.Reject(SdoAbortCode.PdoMappingLengthExceeded)
            : OdWriteDecision.Accept;
    }

    /// <summary>Like <see cref="ValidateMappingEntry"/> for PDOs, minus dummies (safety data is
    /// never padding) and with the access check deferred to the direction write when
    /// <paramref name="direction"/> is None (the mapping is written while the SRDO is deleted).</summary>
    private static SdoAbortCode? ValidateSrdoMappingEntry(ObjectDictionary od, uint raw, SrdoDirection direction)
    {
        var entryIndex = (ushort)((raw >> 16) & 0xFFFF);
        var entrySub = (byte)((raw >> 8) & 0xFF);
        var bitLength = (byte)(raw & 0xFF);
        if (bitLength == 0 || bitLength > 64 || bitLength % 8 != 0) return SdoAbortCode.DataTypeLengthMismatch;
        if (entryIndex is >= 0x1000 and <= 0x1FFF) return SdoAbortCode.ObjectCannotBeMapped;
        if (entrySub == 0 && PdoMappingEntry.DummyBitLength(entryIndex) != 0) return SdoAbortCode.ObjectCannotBeMapped;
        if (!od.TryGet(entryIndex, entrySub, out var target)) return SdoAbortCode.ObjectDoesNotExist;
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
            var writes = new List<(ushort Index, byte Subindex, uint Value)>
            {
                (comm, 0x01, 0),
                (map, 0x00, 0),
            };
            for (byte s = 1; s <= SrdoRecords.MappingSubindices; s++)
            {
                int i = (s - 1) / 2;
                writes.Add((map, s, i < entries.Length ? EncodeMappingEntry(entries[i]) : 0u));
            }
            writes.Add((map, 0x00, (uint)(2 * entries.Length)));
            writes.Add((comm, 0x02, cycleMs));
            if (direction == SrdoDirection.Receive) writes.Add((comm, 0x03, srvtMs));
            writes.Add((comm, 0x05, id1));
            writes.Add((comm, 0x06, id2));
            writes.Add((comm, 0x01, (byte)direction));
            try
            {
                _od.Transaction(() =>
                {
                    // The dictionary has no rollback: a write refused half-way — a mapped object
                    // the direction cannot use, COB-IDs another SRDO holds — would leave the SRDO
                    // deleted and 13FEh cleared. The same writes therefore run first through the
                    // same validator against a copy, and the dictionary is written only when every
                    // one of them was accepted there. The writes below are still validated.
                    var trial = SafetyTrialCopy();
                    foreach (var (index, subindex, value) in writes) trial.WriteUnsigned(index, subindex, value);
                    foreach (var (index, subindex, value) in writes) _od.WriteUnsigned(index, subindex, value);
                });
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"SRDO{srdoNumber} configuration rejected: {ex.Message}", ex);
            }
        });
    }

    /// <summary>A copy of the dictionary — every entry, so that a mapped object is found with its
    /// type, access and mappability — whose writes run through the safety validator as in
    /// Pre-Operational, the state a configuration is written in. Called under the write gate.</summary>
    private ObjectDictionary SafetyTrialCopy()
    {
        var copy = new ObjectDictionary();
        foreach (var key in _od.SnapshotKeys())
        {
            ushort index = (ushort)(key >> 8);
            byte subindex = (byte)key;
            if (_od.TryGet(index, subindex, out var entry))
                copy.Declare(index, subindex, entry.DataType, entry.Access, entry.GetRawValue(), entry.PdoMappable);
        }
        int srdoCount = _srdoCount;
        copy.WriteValidator = (index, subindex, value) => IsManagedSafetyObject(srdoCount, index)
            ? ValidateSafetyWrite(copy, srdoCount, operational: false, index, subindex, value)
            : OdWriteDecision.Accept;
        return copy;
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
    // ArgumentOutOfRangeException like any other number outside 1..SrdoCount. A disposed node
    // refuses, as every other member does: its last snapshot may still say valid.
    public SrdoState GetSrdoState(int srdoNumber)
    {
        ThrowIfDisposed();
        return _srdo.GetState(srdoNumber);
    }

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

    // The Operational period a link on the chain belongs to, as a token: leaving Operational and
    // disposing cancel it (actor only). A link checks it before each of its two frames and hands
    // each frame over with it, so a link of an ended period starts no further send — including a
    // frame whose thread had passed the check but whose send task had not started: the token is
    // honoured until that task starts. A send task already running — waiting for the bus
    // service's pending gate, say — still hands its frame to the driver, because the service does
    // not check the token before the driver call (#294). §8.3.2.2 has no safety communication
    // outside Operational. That covers the pairs queued behind a held one —
    // one per SRDO — and the inverted half of the held one: a consumer that times out on its SRVT
    // is safer than one that refreshes its SCT on a stale pair. Created with the period's first
    // send, a pair or the GFC. A frame the service has already taken goes out, as on any controller.
    private CancellationTokenSource? _srdoSendPeriod;

    /// <summary>The token of the current Operational period, the one place the SRDO pairs and the
    /// GFC take it from; the period's source is created with its first send. Actor only.</summary>
    private CancellationToken CurrentSrdoSendPeriod() => (_srdoSendPeriod ??= new CancellationTokenSource()).Token;

    // The GFC is not an SRDO and does not wait behind them (§8.2: it is the highest-priority
    // safety message); it goes out at once. It is sent only in Operational, so it is handed over
    // with the token of the Operational period, as an SRDO frame is: leaving Operational or
    // disposing cancels it, honoured until the send task starts. A send task already running
    // still hands its frame to the driver (#294).
    void ISrdoEngineHost.Send(uint cobId, byte[] payload) => _gfcSend = SendInPeriodAsync(cobId, payload, CurrentSrdoSendPeriod());

    private Task? _gfcSend;

    /// <summary>Test seam, actor only: the last GFC send.</summary>
    internal Task? GfcSendForTests => _gfcSend;

    /// <summary>A send cancelled by its period's own token ends quietly; any other outcome is
    /// SendControlFrame's, which reports a failure itself (a service-dispose cancellation as a
    /// transport failure).</summary>
    private async Task SendInPeriodAsync(uint cobId, byte[] payload, CancellationToken period)
    {
        try
        {
            await SendControlFrame(cobId, payload, period).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (period.IsCancellationRequested)
        {
        }
    }

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
        var period = CurrentSrdoSendPeriod();
        var link = _srdoSendChain.ContinueWith(
            _ => SendSrdoPairAsync(pair, period),
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

    /// <summary>Leaving Operational: the engine stops, a pair still pending for the bus is
    /// dropped with it, and the links already on the chain send nothing more. Actor only.</summary>
    private void LeaveSrdoOperational()
    {
        _srdo.LeaveOperational();
        Array.Clear(_srdoPending, 0, _srdoPending.Length);
        EndSrdoSendPeriod();
    }

    /// <summary>The links queued so far belong to a period that has ended: their sends are
    /// cancelled, and the next pair starts a period of its own. Actor only.</summary>
    private void EndSrdoSendPeriod()
    {
        var period = _srdoSendPeriod;
        _srdoSendPeriod = null;
        if (period is null) return;
        // Cancelled, not disposed: a link may still hand its token to the service after this
        // turn, and registering on a disposed source throws on some runtimes. A source without a
        // timer or wait handle holds nothing that needs disposing.
        period.Cancel();
    }

    /// <summary>Test seam, any thread: the NMT state as last published, read without a round trip
    /// through the actor (which a test holding the write gate may be keeping busy).</summary>
    internal NmtState StateForTests => _state;

    /// <summary>Test seam, actor only: the tail of the SRDO send chain.</summary>
    internal Task SrdoSendChainForTests => _srdoSendChain;

    /// <summary>Test seam, actor only: whether a pair of SRDO <paramref name="srdoNumber"/> is in flight.</summary>
    internal bool SrdoPairInFlightForTests(int srdoNumber) => _srdoInFlight[srdoNumber];

    // A failed or unconfirmed frame is reported by SendControlFrame itself and the inverted frame
    // still follows, as it would on the controller; only a cancelled send (the service is being
    // disposed) or the end of the Operational period the pair belongs to ends the pair early. A
    // send cancelled by the period's own token is no failure: the pair ends quietly.
    private async Task SendSrdoPairAsync(SrdoPair pair, CancellationToken period)
    {
        try
        {
            if (period.IsCancellationRequested) return;
            await SendControlFrame(pair.CobId1, pair.Plain, period).ConfigureAwait(false);
            if (period.IsCancellationRequested) return;
            await SendControlFrame(pair.CobId2, pair.Inverted, period).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (period.IsCancellationRequested)
        {
        }
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
