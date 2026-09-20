using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Pro.CANopen.Emcy;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Pdo;
using CanKit.Pro.CANopen.Sdo;

namespace CanKit.Pro.CANopen;

/// <summary>
/// The PDO engine (FR-CO-005 / FR-CO-006, CiA 301 §7.2.2 and §7.5.2.35–§7.5.2.38) of
/// <see cref="CanOpenNode"/>. Every TPDO and RPDO is derived from its communication and mapping
/// records in the object dictionary: <see cref="RebuildTpdo"/> / <see cref="RebuildRpdo"/> run
/// whenever one of those records changes, so a value the dictionary accepted — from
/// <c>ConfigureTpdo</c>, from a master over SDO, or from the application through the dictionary —
/// is the value the engine runs with.
/// </summary>
/// <remarks>
/// <para>
/// TPDO transmission types (Table 72): <c>00h</c> synchronous-acyclic (a change of state or a
/// <c>TriggerTpdoAsync</c> latches a transmission for the next SYNC), <c>01h</c>–<c>F0h</c>
/// cyclic every n-th SYNC, <c>FCh</c> RTR-only synchronous (sampled at every SYNC, the sample
/// answers an RTR), <c>FDh</c> RTR-only event-driven (sampled and sent on RTR), <c>FEh</c>/<c>FFh</c>
/// event-driven (change of state, <c>TriggerTpdoAsync</c>, and the event timer <c>1800h:05</c> as
/// the maximum interval), rate-limited by the inhibit time <c>1800h:03</c>, which — as
/// §7.5.2.37 states — applies to <c>FEh</c>/<c>FFh</c> only. RPDO transmission types (Table 68):
/// <c>00h</c>–<c>F0h</c> synchronous (received data is actuated with the next SYNC), <c>FEh</c>/<c>FFh</c>
/// event-driven (actuated immediately).
/// </para>
/// <para>
/// All state here is actor-confined. The only cross-thread entry is the change-of-state
/// pre-filter (<see cref="OnOdEntryWrittenForCoS"/>), which runs on the writing thread and
/// coalesces into one posted evaluation.
/// </para>
/// </remarks>
internal sealed partial class CanOpenNode
{
    private readonly TpdoRuntime?[] _tpdos = new TpdoRuntime?[Co.PdoCount + 1];
    private readonly Dictionary<uint, TpdoRuntime> _tpdosByCobId = new();
    private readonly RpdoRuntime?[] _rpdos = new RpdoRuntime?[Co.PdoCount + 1];
    private readonly Dictionary<uint, RpdoRuntime> _rpdosByCobId = new();

    // Change-of-state TPDO support (FR-CO-006): volatile pre-filter snapshot of OD entries
    // mapped in at least one event-driven or synchronous-acyclic TPDO (rebuilt on the actor by
    // RebuildCosRelevantEntries), plus the dirty-set coalescing state that bounds CoS posts to
    // at most one queued evaluation (see OnOdEntryWrittenForCoS).
    private volatile HashSet<uint> _cosRelevantEntries = new();
    private readonly object _cosGate = new();
    private HashSet<uint>? _cosDirty;
    private bool _cosPosted;

    // =========================================================================================
    // Public API — writes to the PDO records; the engine follows.
    // =========================================================================================

    /// <inheritdoc />
    public void ConfigureTpdo(int pdoIndex, PdoMapping mapping,
        TpdoTransmission transmission = TpdoTransmission.EventDriven, uint? cobId = null,
        TimeSpan? eventTimerInterval = null, TimeSpan? inhibitTime = null)
    {
        ThrowIfDisposed();
        if (pdoIndex is < 1 or > Co.PdoCount)
            throw new ArgumentOutOfRangeException(nameof(pdoIndex), pdoIndex, "PDO index must be 1..4.");
        if (mapping is null) throw new ArgumentNullException(nameof(mapping));
        byte type = CanOpenTransmissionType.FromTpdoTransmission(transmission);
        uint word = ValidateCobIdArgument(cobId ?? CanOpenCobId.TpdoDefault(_nodeId, pdoIndex));
        ushort timerMs = transmission == TpdoTransmission.EventTimer
            ? ToMilliseconds16(eventTimerInterval ?? _options.DefaultTpdoEventTimerInterval, nameof(eventTimerInterval), allowZero: false)
            : (ushort)0;
        ushort inhibit = inhibitTime is { } ih ? ToHundredMicroseconds16(ih, nameof(inhibitTime)) : (ushort)0;

        RunOnActorAndWait(() => WritePdoRecords((ushort)(Co.TpdoComm + pdoIndex - 1), (ushort)(Co.TpdoMap + pdoIndex - 1),
            mapping.ToArray(), word, type, inhibit, timerMs, isTpdo: true, pdoIndex));
    }

    /// <inheritdoc />
    public void ConfigureRpdo(int pdoIndex, PdoMapping mapping, uint? cobId = null,
        RpdoTransmission transmission = RpdoTransmission.EventDriven)
    {
        ThrowIfDisposed();
        if (pdoIndex is < 1 or > Co.PdoCount)
            throw new ArgumentOutOfRangeException(nameof(pdoIndex), pdoIndex, "PDO index must be 1..4.");
        if (mapping is null) throw new ArgumentNullException(nameof(mapping));
        byte type = CanOpenTransmissionType.FromRpdoTransmission(transmission);
        uint word = ValidateCobIdArgument(cobId ?? CanOpenCobId.RpdoDefault(_nodeId, pdoIndex));

        RunOnActorAndWait(() => WritePdoRecords((ushort)(Co.RpdoComm + pdoIndex - 1), (ushort)(Co.RpdoMap + pdoIndex - 1),
            mapping.ToArray(), word, type, inhibit: 0, eventTimerMs: 0, isTpdo: false, pdoIndex));
    }

    /// <inheritdoc />
    public Task TriggerTpdoAsync(int pdoIndex, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _actor.PostAsync(() => TriggerTpdoOnActor(pdoIndex));
    }

    private static uint ValidateCobIdArgument(uint word)
    {
        if ((word & CanOpenCobId.ExtendedFrameBit) != 0)
            throw new ArgumentException("29-bit COB-IDs (bit 29) are not supported: this node sends and receives CAN base frames only.", "cobId");
        if ((word & 0x1FFF_F800) != 0)
            throw new ArgumentException("Bits 28..11 of an 11-bit COB-ID must be zero.", "cobId");
        return word;
    }

    /// <summary>
    /// The CiA 301 §7.5.2.38 re-mapping procedure as OD writes: destroy the PDO (bit 31), disable
    /// the mapping (sub0 = 0), write the entries, enable the mapping (sub0 = N), set the
    /// communication parameters, create the PDO. Each write is validated like an SDO download;
    /// a rejection surfaces as <see cref="ArgumentException"/> and leaves the PDO destroyed.
    /// Runs on the actor loop (<see cref="RunOnActorAndWait"/>), so the sequence is one
    /// transaction: the SDO server writes on the same loop and a second configuration queues
    /// behind the whole sequence, neither can interleave with it (Codex on #133).
    /// </summary>
    private void WritePdoRecords(ushort comm, ushort map, PdoMappingEntry[] entries, uint cobIdWord,
        byte transmissionType, ushort inhibit, ushort eventTimerMs, bool isTpdo, int pdoIndex)
    {
        try
        {
            uint current = _od.ReadUnsigned(comm, 0x01);
            _od.WriteUnsigned(comm, 0x01, current | CanOpenCobId.InvalidBit);
            _od.WriteUnsigned(map, 0x00, 0);
            for (byte s = 1; s <= PdoMapping.MaxEntries; s++)
            {
                _od.WriteUnsigned(map, s, s <= entries.Length ? EncodeMappingEntry(entries[s - 1]) : 0u);
            }
            _od.WriteUnsigned(map, 0x00, (uint)entries.Length);
            _od.WriteUnsigned(comm, 0x02, transmissionType);
            if (isTpdo)
            {
                _od.WriteUnsigned(comm, 0x03, inhibit);
                _od.WriteUnsigned(comm, 0x05, eventTimerMs);
            }
            _od.WriteUnsigned(comm, 0x01, cobIdWord);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException(
                $"{(isTpdo ? "TPDO" : "RPDO")}{pdoIndex} configuration rejected: {ex.Message}", ex);
        }
    }

    private static uint EncodeMappingEntry(PdoMappingEntry e)
        => ((uint)e.Index << 16) | ((uint)e.Subindex << 8) | e.BitLength;

    // =========================================================================================
    // Validation of the PDO records (runs on the writing thread, before the store).
    // =========================================================================================

    private OdWriteDecision ValidatePdoCommunicationWrite(ushort index, byte subindex, byte[] value, bool isTpdo)
    {
        switch (subindex)
        {
            case 0x01:
                {
                    uint v = ObjectDictionary.DecodeU32(value);
                    if (!IsUsableCobIdWord(v)) return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);
                    // §7.5.2.35 / §7.5.2.37: "It is not allowed to change bit 0 to 29 while the PDO
                    // exists and is valid (bit 31 = 0b)." Bit 30 (RTR / reserved) may change.
                    uint current = _od.ReadUnsigned(index, 0x01);
                    const uint lower30 = 0x3FFF_FFFF;
                    if ((current & CanOpenCobId.InvalidBit) == 0 && (v & CanOpenCobId.InvalidBit) == 0
                        && (current & lower30) != (v & lower30))
                        return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);
                    return OdWriteDecision.Accept;
                }
            case 0x02:
                {
                    // Tables 68 and 72: "An attempt to change the value of the transmission type to
                    // any not supported value shall be responded with … 0609 0030h."
                    byte type = value[0];
                    bool reserved = isTpdo
                        ? CanOpenTransmissionType.IsReservedForTpdo(type)
                        : CanOpenTransmissionType.IsReservedForRpdo(type);
                    return reserved ? OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded) : OdWriteDecision.Accept;
                }
            case 0x03:
                {
                    // §7.5.2.37: "The value shall not be changed while the PDO exists (bit 31 of
                    // sub-index 01h is set to 0b)."
                    uint current = _od.ReadUnsigned(index, 0x01);
                    return (current & CanOpenCobId.InvalidBit) == 0
                        ? OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded)
                        : OdWriteDecision.Accept;
                }
            default:
                return OdWriteDecision.Accept;
        }
    }

    private OdWriteDecision ValidatePdoMappingWrite(ushort index, byte subindex, byte[] value, bool isTpdo)
    {
        // Steps 1 and 5 of the re-mapping procedure (§7.5.2.36 / §7.5.2.38) bracket every
        // mapping change with the PDO destroyed: bit 31 of the communication record set. A
        // mapping written into a live PDO would rebuild it mid-transfer — a TPDO with an
        // empty mapping transmits empty frames — so the count and the entries are accepted
        // only while the PDO does not exist. ConfigureTpdo / ConfigureRpdo follow the
        // procedure; a master must too.
        var commIndex = (ushort)(index - (isTpdo ? Co.TpdoMap : Co.RpdoMap) + (isTpdo ? Co.TpdoComm : Co.RpdoComm));
        if ((_od.ReadUnsigned(commIndex, 0x01) & CanOpenCobId.InvalidBit) == 0)
            return OdWriteDecision.Reject(SdoAbortCode.UnsupportedAccess);

        if (subindex == 0x00)
        {
            byte count = value[0];
            if (count == 0) return OdWriteDecision.Accept; // "mapping disabled"
            // Tables 69 / 73: 41h..FDh reserved, FEh/FFh MPDO (not supported); 09h..40h cannot
            // fit a byte-aligned mapping into 8 bytes.
            if (count > 0x40) return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);
            if (count > PdoMapping.MaxEntries) return OdWriteDecision.Reject(SdoAbortCode.PdoMappingLengthExceeded);
            // Step 4 of the re-mapping procedure: "If … the device detects that the mapping is
            // not valid or not possible … 0602 0000h or 0604 0042h."
            int totalBytes = 0;
            for (byte s = 1; s <= count; s++)
            {
                if (!_od.TryReadUnsigned(index, s, out var raw) || raw == 0)
                    return OdWriteDecision.Reject(SdoAbortCode.ObjectDoesNotExist);
                var abort = ValidateMappingEntry(raw, isTpdo);
                if (abort is { } code) return OdWriteDecision.Reject(code);
                totalBytes += (int)(raw & 0xFF) / 8;
            }
            return totalBytes > 8
                ? OdWriteDecision.Reject(SdoAbortCode.PdoMappingLengthExceeded)
                : OdWriteDecision.Accept;
        }

        // Entries may only change while the mapping is disabled (sub0 = 0); the procedure of
        // §7.5.2.36 / §7.5.2.38 requires that step first.
        if (_od.ReadUnsigned(index, 0x00) != 0) return OdWriteDecision.Reject(SdoAbortCode.UnsupportedAccess);
        uint entry = ObjectDictionary.DecodeU32(value);
        if (entry == 0) return OdWriteDecision.Accept; // clears the slot
        var reason = ValidateMappingEntry(entry, isTpdo);
        if (reason is { } r) return OdWriteDecision.Reject(r);
        // "a wrong length for the PDO at all" is a step-3 rejection too: with the entries before
        // this slot, the mapping would already exceed 8 bytes. Slots after it may still hold a
        // previous mapping and do not count.
        int precedingBytes = 0;
        for (byte s = 1; s < subindex; s++)
        {
            if (_od.TryReadUnsigned(index, s, out var earlier)) precedingBytes += (int)(earlier & 0xFF) / 8;
        }
        return precedingBytes + (int)(entry & 0xFF) / 8 > 8
            ? OdWriteDecision.Reject(SdoAbortCode.PdoMappingLengthExceeded)
            : OdWriteDecision.Accept;
    }

    /// <summary>
    /// Step 3 of the re-mapping procedure: the mapped object must exist (else <c>0602 0000h</c>),
    /// be mappable, accessible in the PDO's direction and of the mapped width (else
    /// <c>0604 0041h</c>); the width itself must be a byte multiple of at most 64 bits.
    /// </summary>
    private SdoAbortCode? ValidateMappingEntry(uint raw, bool isTpdo)
    {
        var entryIndex = (ushort)((raw >> 16) & 0xFFFF);
        var entrySub = (byte)((raw >> 8) & 0xFF);
        var bitLength = (byte)(raw & 0xFF);
        if (bitLength == 0 || bitLength > 64 || bitLength % 8 != 0)
            return SdoAbortCode.DataTypeLengthMismatch;

        // §7.5.2.36: dummy mapping onto the static data types 0002h..0007h.
        if (entrySub == 0 && PdoMappingEntry.DummyBitLength(entryIndex) is var dummyBits && dummyBits != 0)
            return dummyBits == bitLength ? null : SdoAbortCode.ObjectCannotBeMapped;

        if (!_od.TryGet(entryIndex, entrySub, out var target)) return SdoAbortCode.ObjectDoesNotExist;
        if (!target.PdoMappable) return SdoAbortCode.ObjectCannotBeMapped;
        var needed = isTpdo ? OdAccess.ReadOnly : OdAccess.WriteOnly;
        if ((target.Access & needed) == 0) return SdoAbortCode.ObjectCannotBeMapped;
        int fixedSize = OdEntryLayout.FixedSize(target.DataType);
        if (fixedSize > 0 && bitLength / 8 != fixedSize) return SdoAbortCode.ObjectCannotBeMapped;
        return null;
    }

    // =========================================================================================
    // Rebuild from the OD (actor loop).
    // =========================================================================================

    private void RebuildTpdo(int n)
    {
        var comm = (ushort)(Co.TpdoComm + n - 1);
        var map = (ushort)(Co.TpdoMap + n - 1);
        var rt = _tpdos[n] ??= new TpdoRuntime(n);

        if (_tpdosByCobId.TryGetValue(rt.CobId, out var registered) && ReferenceEquals(registered, rt))
            _tpdosByCobId.Remove(rt.CobId);
        // A transmission waiting out the inhibit time is an event the application already
        // raised; a record write that leaves the PDO valid (an event-timer change, say) must not
        // lose it, so it is re-requested against the rebuilt configuration below.
        bool inhibitedEventPending = rt.InhibitPending;
        rt.EventTimerHandle?.Dispose();
        rt.EventTimerHandle = null;
        rt.InhibitHandle?.Dispose();
        rt.InhibitHandle = null;
        rt.InhibitPending = false;
        rt.SyncCounter = 0;
        rt.SyncAcyclicPending = false;
        rt.RtrBuffer = null;

        uint word = _od.TryReadUnsigned(comm, 0x01, out var w) ? w : CanOpenCobId.InvalidBit;
        rt.Valid = (word & CanOpenCobId.InvalidBit) == 0;
        rt.RtrAllowed = (word & CanOpenCobId.NoRtrBit) == 0;
        rt.CobId = word & CanOpenCobId.CanIdMask;
        rt.TransmissionType = _od.TryReadUnsigned(comm, 0x02, out var t) ? (byte)t : CanOpenTransmissionType.EventDrivenManufacturer;
        rt.InhibitTime = TimeSpan.FromTicks((_od.TryReadUnsigned(comm, 0x03, out var ih) ? ih : 0) * (TimeSpan.TicksPerMillisecond / 10));
        rt.EventTimer = TimeSpan.FromMilliseconds(_od.TryReadUnsigned(comm, 0x05, out var et) ? et : 0);
        rt.Mapping = ReadMappingRecord(map);
        rt.TotalBytes = TotalBytes(rt.Mapping);

        if (rt.Valid)
        {
            _tpdosByCobId[rt.CobId] = rt;
            if (CanOpenTransmissionType.IsEventDriven(rt.TransmissionType) && rt.EventTimer > TimeSpan.Zero)
                ScheduleTpdoEventTimer(rt);
            if (inhibitedEventPending && CanOpenTransmissionType.IsEventDriven(rt.TransmissionType))
                RequestEventDrivenTransmission(rt);
        }
        RebuildCosRelevantEntries();
    }

    private void RebuildRpdo(int n)
    {
        var comm = (ushort)(Co.RpdoComm + n - 1);
        var map = (ushort)(Co.RpdoMap + n - 1);
        var rp = _rpdos[n] ??= new RpdoRuntime(n);

        if (_rpdosByCobId.TryGetValue(rp.CobId, out var registered) && ReferenceEquals(registered, rp))
            _rpdosByCobId.Remove(rp.CobId);
        rp.SyncPending = null;

        uint word = _od.TryReadUnsigned(comm, 0x01, out var w) ? w : CanOpenCobId.InvalidBit;
        rp.Valid = (word & CanOpenCobId.InvalidBit) == 0;
        rp.CobId = word & CanOpenCobId.CanIdMask;
        rp.TransmissionType = _od.TryReadUnsigned(comm, 0x02, out var t) ? (byte)t : CanOpenTransmissionType.EventDrivenManufacturer;
        rp.Mapping = ReadMappingRecord(map);
        rp.TotalBytes = TotalBytes(rp.Mapping);

        if (rp.Valid) _rpdosByCobId[rp.CobId] = rp;
    }

    private PdoMappingEntry[] ReadMappingRecord(ushort map)
    {
        int count = _od.TryReadUnsigned(map, 0x00, out var c) ? (int)c : 0;
        if (count > PdoMapping.MaxEntries) return Array.Empty<PdoMappingEntry>();
        var entries = new List<PdoMappingEntry>(count);
        for (byte s = 1; s <= count; s++)
        {
            if (!_od.TryReadUnsigned(map, s, out var raw) || raw == 0) continue;
            try
            {
                entries.Add(new PdoMappingEntry((ushort)((raw >> 16) & 0xFFFF), (byte)((raw >> 8) & 0xFF), (byte)(raw & 0xFF)));
            }
            catch (ArgumentOutOfRangeException)
            {
                // Validated when written; a malformed slot cannot normally get here.
            }
        }
        return entries.ToArray();
    }

    private static int TotalBytes(PdoMappingEntry[] mapping)
    {
        int total = 0;
        foreach (var e in mapping) total += e.ByteLength;
        return total;
    }

    // =========================================================================================
    // Transmission.
    // =========================================================================================

    private byte[] BuildTpdoPayload(TpdoRuntime rt)
    {
        // Each mapping slot occupies its configured ByteLength window in the frame regardless
        // of whether the OD entry is present or shorter — a dummy or missing entry leaves its
        // window as zero bytes and the next slot lands at its correct offset.
        var payload = new byte[rt.TotalBytes];
        int offset = 0;
        foreach (var entry in rt.Mapping)
        {
            // TryReadRaw snapshots under the OD lock so a concurrent application write cannot
            // tear the copy (see Bugbot 3600644170).
            if (!entry.IsDummy && _od.TryReadRaw(entry.Index, entry.Subindex, out var raw))
            {
                int copy = Math.Min(raw.Length, entry.ByteLength);
                Buffer.BlockCopy(raw, 0, payload, offset, copy);
            }
            offset += entry.ByteLength;
        }
        return payload;
    }

    private void EmitTpdo(TpdoRuntime rt)
    {
        _ = SendControlFrame(rt.CobId, BuildTpdoPayload(rt));
        rt.LastTransmission = _actor.TimeSource.GetTimestamp();
        rt.HasTransmitted = true;
        if (CanOpenTransmissionType.IsEventDriven(rt.TransmissionType) && rt.EventTimer > TimeSpan.Zero)
        {
            // §7.5.2.37: the event timer is "the maximum interval for PDO transmission" — it
            // restarts with every transmission.
            rt.EventTimerHandle?.Dispose();
            ScheduleTpdoEventTimer(rt);
        }
    }

    /// <summary>
    /// An event for an event-driven TPDO (change of state, <c>TriggerTpdoAsync</c>): transmit now
    /// unless the inhibit time since the previous transmission has not elapsed, in which case one
    /// transmission is scheduled for when it has (events in between coalesce into it).
    /// </summary>
    private void RequestEventDrivenTransmission(TpdoRuntime rt)
    {
        if (_state != NmtState.Operational || !rt.Valid) return;
        if (rt.InhibitTime <= TimeSpan.Zero || !rt.HasTransmitted)
        {
            EmitTpdo(rt);
            return;
        }
        var time = _actor.TimeSource;
        long elapsedTicks = time.GetTimestamp() - rt.LastTransmission;
        var elapsed = TimeSpan.FromTicks((long)(elapsedTicks * (TimeSpan.TicksPerSecond / (double)time.Frequency)));
        if (elapsed >= rt.InhibitTime)
        {
            EmitTpdo(rt);
            return;
        }
        if (rt.InhibitPending) return;
        rt.InhibitPending = true;
        rt.InhibitHandle = _actor.Schedule(rt.InhibitTime - elapsed, () =>
        {
            rt.InhibitPending = false;
            rt.InhibitHandle = null;
            if (_disposed != 0 || !ReferenceEquals(_tpdos[rt.PdoIndex], rt)) return;
            if (_state != NmtState.Operational || !rt.Valid) return;
            EmitTpdo(rt);
        });
    }

    private void ScheduleTpdoEventTimer(TpdoRuntime rt)
    {
        if (rt.EventTimer <= TimeSpan.Zero) return;
        rt.EventTimerHandle = _actor.Schedule(rt.EventTimer, () =>
        {
            if (_disposed != 0 || !ReferenceEquals(_tpdos[rt.PdoIndex], rt) || !rt.Valid) return;
            if (!CanOpenTransmissionType.IsEventDriven(rt.TransmissionType) || rt.EventTimer <= TimeSpan.Zero) return;
            // The elapsed timer is an event like any other: it transmits now, or — when the
            // inhibit time is the longer of the two — once that has elapsed. Either
            // transmission re-arms the timer; outside Operational the timer keeps its cycle.
            if (_state == NmtState.Operational) RequestEventDrivenTransmission(rt);
            else ScheduleTpdoEventTimer(rt);
        });
    }

    /// <summary>A consumer requested the PDO with an RTR (CiA 301 §7.2.2.5.2, PDO read).</summary>
    private void HandleTpdoRtr(TpdoRuntime rt)
    {
        if (_state != NmtState.Operational || !rt.Valid || !rt.RtrAllowed) return;
        if (rt.TransmissionType == CanOpenTransmissionType.RtrOnlySynchronous)
        {
            // Table 72: "In case it is synchronous the CANopen device will start sampling with
            // the reception of every SYNC and then will buffer the PDO" — the buffer answers, and
            // before the first SYNC there is nothing sampled to answer with.
            if (rt.RtrBuffer is { } buffered)
            {
                _ = SendControlFrame(rt.CobId, buffered);
                rt.LastTransmission = _actor.TimeSource.GetTimestamp();
                rt.HasTransmitted = true;
            }
            return;
        }
        // §7.5.2.37: the inhibit time bounds every transmission of an event-driven TPDO, and a
        // PDO read is one of them; the RTR-only and synchronous types are outside its scope.
        if (CanOpenTransmissionType.IsEventDriven(rt.TransmissionType)) RequestEventDrivenTransmission(rt);
        else EmitTpdo(rt);
    }

    private void TriggerTpdoOnActor(int pdoIndex)
    {
        if (_state != NmtState.Operational) return;
        if (pdoIndex is < 1 or > Co.PdoCount) return;
        var rt = _tpdos[pdoIndex];
        if (rt is null || !rt.Valid) return;
        if (rt.TransmissionType == CanOpenTransmissionType.SynchronousAcyclic) rt.SyncAcyclicPending = true;
        else if (CanOpenTransmissionType.IsEventDriven(rt.TransmissionType)) RequestEventDrivenTransmission(rt);
        else EmitTpdo(rt);
    }

    // =========================================================================================
    // SYNC (FR-CO-010) — the trigger for every synchronous PDO.
    // =========================================================================================

    private void HandleSync()
    {
        // Table 37: SYNC is not active in Stopped (nor before initialisation is finished).
        if (_state is NmtState.Stopped or NmtState.Initializing) return;
        RaiseSyncReceived(DateTime.UtcNow);
        if (_state != NmtState.Operational) return;

        for (int n = 1; n <= Co.PdoCount; n++)
        {
            var rt = _tpdos[n];
            if (rt is null || !rt.Valid) continue;
            byte type = rt.TransmissionType;
            if (type == CanOpenTransmissionType.SynchronousAcyclic)
            {
                if (!rt.SyncAcyclicPending) continue;
                rt.SyncAcyclicPending = false;
                EmitTpdo(rt);
            }
            else if (type <= CanOpenTransmissionType.SynchronousCyclicMax)
            {
                if (++rt.SyncCounter < type) continue;
                rt.SyncCounter = 0;
                EmitTpdo(rt);
            }
            else if (type == CanOpenTransmissionType.RtrOnlySynchronous)
            {
                rt.RtrBuffer = BuildTpdoPayload(rt);
            }
        }

        // §7.2.2.2: "The data of synchronous RPDOs received after the occurrence of the SYNC
        // object is passed to the application with the occurrence of the following SYNC."
        for (int n = 1; n <= Co.PdoCount; n++)
        {
            var rp = _rpdos[n];
            if (rp is null || !rp.Valid || rp.SyncPending is not { } pending) continue;
            rp.SyncPending = null;
            ActuateRpdo(rp, pending);
        }
    }

    // =========================================================================================
    // Reception.
    // =========================================================================================

    private void HandleRpdo(RpdoRuntime rp, byte[] payload)
    {
        // CiA 301 Table 37: PDOs exist only in Operational.
        if (_state != NmtState.Operational || !rp.Valid) return;

        // §7.5.2.36: fewer data bytes than mapped → not processed, EMCY 8210h if supported;
        // more than mapped → the first bytes up to the mapped length are used.
        if (payload.Length < rp.TotalBytes)
        {
            if (_emcyValid)
            {
                var errorRegister = (byte)_od.ReadUnsigned(Co.ErrorRegister, 0x00);
                _ = SendControlFrame(_emcyCobId, new EmcyMessage(_nodeId, 0x8210, errorRegister).Encode());
            }
            return;
        }

        if (CanOpenTransmissionType.IsSynchronous(rp.TransmissionType))
        {
            rp.SyncPending = payload;
            return;
        }
        ActuateRpdo(rp, payload);
    }

    private void ActuateRpdo(RpdoRuntime rp, byte[] payload)
    {
        int offset = 0;
        foreach (var entry in rp.Mapping)
        {
            if (offset + entry.ByteLength > payload.Length) break;
            if (!entry.IsDummy && _od.TryGet(entry.Index, entry.Subindex, out var odEntry)
                && (odEntry.Access & OdAccess.WriteOnly) != 0)
            {
                var chunk = new byte[entry.ByteLength];
                Buffer.BlockCopy(payload, offset, chunk, 0, entry.ByteLength);
                // Under the OD lock (WriteRaw) so readers on other threads see whole values.
                // The mapping was validated against the entry's width when it was written, so a
                // rejected write here means the entry was re-declared since; that is reported
                // rather than swallowed, because a PDO whose data silently never lands is the
                // kind of defect nothing else would show.
                try { _od.WriteRaw(entry.Index, entry.Subindex, chunk); }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException)
                {
                    RaiseBackgroundException(new InvalidOperationException(
                        $"RPDO{rp.PdoIndex}: the mapped object 0x{entry.Index:X4}:{entry.Subindex:X2} rejected {entry.ByteLength} byte(s): {ex.Message}", ex));
                }
            }
            offset += entry.ByteLength;
        }
        RaiseRpdoReceived(rp.PdoIndex, rp.CobId, payload);
    }

    // =========================================================================================
    // NMT state hooks (called from the transition code on the actor loop).
    // =========================================================================================

    /// <summary>§7.3.2.2.3: "Transitioning to the NMT state Operational creates all PDOs" — every
    /// SYNC counter, latch and sample starts afresh, and every event timer is armed anew: a
    /// timer whose last expiry was deferred by the inhibit time and then dropped on leaving
    /// Operational has no transmission left to re-arm it, so this is where it comes back.</summary>
    private void OnEnterOperational()
    {
        for (int n = 1; n <= Co.PdoCount; n++)
        {
            if (_tpdos[n] is { } rt)
            {
                rt.SyncCounter = 0;
                rt.SyncAcyclicPending = false;
                rt.RtrBuffer = null;
                rt.EventTimerHandle?.Dispose();
                rt.EventTimerHandle = null;
                if (rt.Valid && CanOpenTransmissionType.IsEventDriven(rt.TransmissionType))
                    ScheduleTpdoEventTimer(rt);
            }
            if (_rpdos[n] is { } rp) rp.SyncPending = null;
        }
    }

    private void OnLeaveOperational()
    {
        for (int n = 1; n <= Co.PdoCount; n++)
        {
            if (_tpdos[n] is { } rt)
            {
                rt.SyncAcyclicPending = false;
                rt.RtrBuffer = null;
                rt.InhibitHandle?.Dispose();
                rt.InhibitHandle = null;
                rt.InhibitPending = false;
            }
            if (_rpdos[n] is { } rp) rp.SyncPending = null;
        }
    }

    private void DisposePdoRuntime()
    {
        for (int n = 1; n <= Co.PdoCount; n++)
        {
            if (_tpdos[n] is { } rt)
            {
                rt.EventTimerHandle?.Dispose();
                rt.InhibitHandle?.Dispose();
            }
        }
        _tpdosByCobId.Clear();
        _rpdosByCobId.Clear();
    }

    // =========================================================================================
    // Change of state (FR-CO-006 / CiA 301 §7.2.2.3, "event- and timer-driven").
    // =========================================================================================

    /// <summary>
    /// An application-originated OD write is an internal event for every TPDO that maps the
    /// written entry: event-driven TPDOs transmit (subject to their inhibit time), a
    /// synchronous-acyclic TPDO latches a transmission for the next SYNC. Runs synchronously on
    /// the writer's thread (invoked from <see cref="ObjectDictionary"/>).
    /// </summary>
    /// <remarks>
    /// Only application writes count: bus-originated OD writes (SDO server download commit,
    /// RPDO unpack) run on the node's actor thread and are filtered out here via
    /// <c>ProtocolActor.IsOnCurrentActor</c>, so an RPDO mapped to the same entry as a
    /// TPDO cannot produce a feedback loop with the peer that sent it.
    /// <para>
    /// This is a provenance check, not a TX-echo check, and #23 did not remove it. The writes it
    /// suppresses come from a <em>peer</em> — a real SDO download, a real RPDO — so the bus's
    /// echo flag says nothing about them; what distinguishes them from an application write is
    /// only that they are applied on the actor loop. The subscription opts into echoes, so it
    /// closes no path into here at all: this node's own TPDO coming back on an echo-capable bus
    /// and being unpacked as an RPDO is suppressed by this provenance check and by nothing else.
    /// The check is sound now that #19 has moved <c>IsOnCurrentActor</c> off <c>AsyncLocal</c>
    /// onto a thread-static, so a send task started from actor work no longer reports true and
    /// no longer swallows a legitimate application write.
    /// </para>
    /// Load safety: the actor mailbox is intentionally unbounded, so this path must never
    /// post per write. A volatile snapshot of the mapped entries filters irrelevant writes
    /// with zero actor traffic, and relevant writes are coalesced into a bounded dirty set —
    /// at most one evaluation is queued at any time (an unthrottled writer otherwise grows
    /// the mailbox without bound, which is exactly what killed the
    /// <c>Tpdo_Emission_UnderConcurrentOdWrites_NeverTears</c> stress test).
    /// </remarks>
    private void OnOdEntryWrittenForCoS(ushort index, byte subindex)
    {
        if (!_options.EnableChangeOfStateTpdo) return;
        if (_actor.IsOnCurrentActor) return; // bus-originated write — never re-trigger (see remarks)
        if (Volatile.Read(ref _disposed) != 0) return;

        var key = CosKey(index, subindex);
        if (!_cosRelevantEntries.Contains(key)) return; // no event-driven TPDO maps it — done

        lock (_cosGate)
        {
            (_cosDirty ??= new HashSet<uint>()).Add(key);
            if (_cosPosted) return;
            _cosPosted = true;
        }

        try
        {
            _actor.Post(EvaluateCoSOnActor);
        }
        catch (ObjectDisposedException)
        {
            lock (_cosGate)
            {
                _cosPosted = false;
            }
        }
    }

    // Actor-side evaluation of the coalesced dirty set. Writes landing during the evaluation
    // re-arm the dirty set and re-post, so nothing is lost.
    private void EvaluateCoSOnActor()
    {
        HashSet<uint>? dirty;
        lock (_cosGate)
        {
            dirty = _cosDirty;
            _cosDirty = null;
            _cosPosted = false;
        }
        if (dirty is null || dirty.Count == 0) return;
        if (_disposed != 0 || _state != NmtState.Operational) return;

        for (int n = 1; n <= Co.PdoCount; n++)
        {
            var rt = _tpdos[n];
            if (rt is null || !rt.Valid || !IsChangeOfStateTriggered(rt.TransmissionType)) continue;
            bool hit = false;
            foreach (var e in rt.Mapping)
            {
                if (dirty.Contains(CosKey(e.Index, e.Subindex)))
                {
                    hit = true;
                    break;
                }
            }
            if (!hit) continue;
            if (rt.TransmissionType == CanOpenTransmissionType.SynchronousAcyclic) rt.SyncAcyclicPending = true;
            else RequestEventDrivenTransmission(rt);
        }
    }

    private static bool IsChangeOfStateTriggered(byte transmissionType)
        => transmissionType == CanOpenTransmissionType.SynchronousAcyclic
           || CanOpenTransmissionType.IsEventDriven(transmissionType);

    // Rebuilds the volatile pre-filter snapshot of OD entries mapped in at least one TPDO whose
    // transmission type reacts to a change of state. Called on the actor whenever a TPDO record
    // changes.
    private void RebuildCosRelevantEntries()
    {
        var set = new HashSet<uint>();
        if (_options.EnableChangeOfStateTpdo)
        {
            for (int n = 1; n <= Co.PdoCount; n++)
            {
                var rt = _tpdos[n];
                if (rt is null || !rt.Valid || !IsChangeOfStateTriggered(rt.TransmissionType)) continue;
                foreach (var e in rt.Mapping) set.Add(CosKey(e.Index, e.Subindex));
            }
        }
        _cosRelevantEntries = set;
    }

    private static uint CosKey(ushort index, byte subindex) => ((uint)index << 8) | subindex;

    // =========================================================================================
    // Runtime state objects (actor-confined).
    // =========================================================================================

    private sealed class TpdoRuntime
    {
        public TpdoRuntime(int pdoIndex)
        {
            PdoIndex = pdoIndex;
        }

        public int PdoIndex { get; }
        public bool Valid { get; set; }
        public bool RtrAllowed { get; set; }
        public uint CobId { get; set; }
        public byte TransmissionType { get; set; } = CanOpenTransmissionType.EventDrivenManufacturer;
        public TimeSpan InhibitTime { get; set; }
        public TimeSpan EventTimer { get; set; }
        public PdoMappingEntry[] Mapping { get; set; } = Array.Empty<PdoMappingEntry>();
        public int TotalBytes { get; set; }
        public IDisposable? EventTimerHandle { get; set; }
        public IDisposable? InhibitHandle { get; set; }
        public bool InhibitPending { get; set; }
        public long LastTransmission { get; set; }
        public bool HasTransmitted { get; set; }
        public int SyncCounter { get; set; }
        public bool SyncAcyclicPending { get; set; }
        public byte[]? RtrBuffer { get; set; }
    }

    private sealed class RpdoRuntime
    {
        public RpdoRuntime(int pdoIndex)
        {
            PdoIndex = pdoIndex;
        }

        public int PdoIndex { get; }
        public bool Valid { get; set; }
        public uint CobId { get; set; }
        public byte TransmissionType { get; set; } = CanOpenTransmissionType.EventDrivenManufacturer;
        public PdoMappingEntry[] Mapping { get; set; } = Array.Empty<PdoMappingEntry>();
        public int TotalBytes { get; set; }
        public byte[]? SyncPending { get; set; }
    }
}
