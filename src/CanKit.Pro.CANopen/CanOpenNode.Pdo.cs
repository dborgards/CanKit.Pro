using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Pro.Actor;
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
    private volatile HashSet<uint> _extendedPdoIds = new();
    private readonly int _pdoCount;
    private readonly TpdoRuntime?[] _tpdos;
    private readonly RpdoRuntime?[] _rpdos;

    // Change-of-state TPDO support (FR-CO-006): volatile pre-filter snapshot of OD entries
    // mapped in at least one event-driven or synchronous-acyclic TPDO (rebuilt from the records
    // by RebuildCosRelevantEntries — on the writing thread as soon as a TPDO record is written,
    // and on the actor after each rebuild), plus the dirty-set coalescing state that bounds CoS
    // posts to at most one queued evaluation (see OnOdEntryWrittenForCoS).
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
        if (pdoIndex < 1 || pdoIndex > _pdoCount)
            throw new ArgumentOutOfRangeException(nameof(pdoIndex), pdoIndex, $"PDO index must be 1..{_pdoCount}.");
        if (mapping is null) throw new ArgumentNullException(nameof(mapping));
        byte type = CanOpenTransmissionType.FromTpdoTransmission(transmission);
        uint word = ValidateCobIdArgument(cobId ?? DefaultPdoCobId(pdoIndex, isTpdo: true));
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
        if (pdoIndex < 1 || pdoIndex > _pdoCount)
            throw new ArgumentOutOfRangeException(nameof(pdoIndex), pdoIndex, $"PDO index must be 1..{_pdoCount}.");
        if (mapping is null) throw new ArgumentNullException(nameof(mapping));
        byte type = CanOpenTransmissionType.FromRpdoTransmission(transmission);
        uint word = ValidateCobIdArgument(cobId ?? DefaultPdoCobId(pdoIndex, isTpdo: false));

        RunOnActorAndWait(() => WritePdoRecords((ushort)(Co.RpdoComm + pdoIndex - 1), (ushort)(Co.RpdoMap + pdoIndex - 1),
            mapping.ToArray(), word, type, inhibit: 0, eventTimerMs: 0, isTpdo: false, pdoIndex));
    }

    /// <inheritdoc />
    public Task TriggerTpdoAsync(int pdoIndex, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _actor.PostAsync(() => TriggerTpdoOnActor(pdoIndex));
    }

    private uint DefaultPdoCobId(int number, bool isTpdo)
    {
        if (number > 4) throw new ArgumentException("PDOs above 4 need an explicit COB-ID.", "cobId");
        return isTpdo ? CanOpenCobId.TpdoDefault(_nodeId, number) : CanOpenCobId.RpdoDefault(_nodeId, number);
    }

    private static uint ValidateCobIdArgument(uint word)
    {
        if ((word & CanOpenCobId.ExtendedFrameBit) == 0 && (word & 0x1FFF_F800) != 0)
            throw new ArgumentException("Bits 28..11 of an 11-bit COB-ID must be zero.", "cobId");
        return word;
    }

    /// <summary>
    /// The CiA 301 §7.5.2.38 re-mapping procedure as OD writes: destroy the PDO (bit 31), disable
    /// the mapping (sub0 = 0), write the entries, enable the mapping (sub0 = N), set the
    /// communication parameters, create the PDO. Each write is validated like an SDO download;
    /// a rejection surfaces as <see cref="ArgumentException"/> and leaves the PDO destroyed —
    /// except the checks ahead of step 1 (records present, optional sub-indices the value needs),
    /// which refuse with the same exception and leave the PDO as it was.
    /// Runs on the actor loop (<see cref="RunOnActorAndWait"/>) and holds the dictionary's write
    /// gate throughout (<see cref="ObjectDictionary.Transaction"/>), so the sequence is one
    /// transaction: the SDO server writes on the same loop, a second configuration queues behind
    /// the whole sequence, and a direct dictionary write on another thread waits for it —
    /// none of them lands between two of its writes.
    /// </summary>
    private void WritePdoRecords(ushort comm, ushort map, PdoMappingEntry[] entries, uint cobIdWord,
        byte transmissionType, ushort inhibit, ushort eventTimerMs, bool isTpdo, int pdoIndex)
    {
        try
        {
            _od.Transaction(() =>
            {
                // Decided before step 1, so a refusal leaves the PDO as it was. A PDO the
                // description does not declare has no records at all (§7.3.3, the loader removes
                // them), and CiA 301 Table 71 makes the inhibit time (03h) and the event timer
                // (05h) optional sub-indices of a TPDO communication record: the loader keeps
                // only the ones the file declares, so they are written only where they exist, and
                // a value that needs an absent one is refused rather than lost.
                RequirePdoRecordEntry(comm, 0x01);
                RequirePdoRecordEntry(comm, 0x02);
                RequirePdoRecordEntry(map, 0x00);
                bool hasInhibit = _od.TryGet(comm, 0x03, out _);
                bool hasEventTimer = _od.TryGet(comm, 0x05, out _);
                if (isTpdo && inhibit != 0 && !hasInhibit)
                    throw new ArgumentException(
                        $"the communication record 0x{comm:X4} has no inhibit time (sub-index 03h), so a non-zero inhibit time cannot be set.", "inhibitTime");
                if (isTpdo && eventTimerMs != 0 && !hasEventTimer)
                    throw new ArgumentException(
                        $"the communication record 0x{comm:X4} has no event timer (sub-index 05h), so an event timer cannot be set.", "eventTimerInterval");

                uint current = _od.ReadUnsigned(comm, 0x01);
                _od.WriteUnsigned(comm, 0x01, ((current & CanOpenCobId.InvalidBit) != 0 ? cobIdWord : current) | CanOpenCobId.InvalidBit);
                _od.WriteUnsigned(map, 0x00, 0);
                for (byte s = 1; s <= PdoMapping.MaxEntries; s++)
                {
                    _od.WriteUnsigned(map, s, s <= entries.Length ? EncodeMappingEntry(entries[s - 1]) : 0u);
                }
                _od.WriteUnsigned(map, 0x00, (uint)entries.Length);
                _od.WriteUnsigned(comm, 0x02, transmissionType);
                if (isTpdo)
                {
                    if (hasInhibit) _od.WriteUnsigned(comm, 0x03, inhibit);
                    if (hasEventTimer) _od.WriteUnsigned(comm, 0x05, eventTimerMs);
                }
                _od.WriteUnsigned(comm, 0x01, cobIdWord);
            });
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException(
                $"{(isTpdo ? "TPDO" : "RPDO")}{pdoIndex} configuration rejected: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// A sub-index <see cref="WritePdoRecords"/> cannot do without. Its absence is the
    /// documented <see cref="ArgumentException"/> (the PDO is not declared on this node), not the
    /// <see cref="KeyNotFoundException"/> the dictionary would raise mid-sequence.
    /// </summary>
    private void RequirePdoRecordEntry(ushort index, byte subindex)
    {
        if (_od.TryGet(index, subindex, out _)) return;
        throw new ArgumentException(
            $"the PDO is not declared on this node: 0x{index:X4}:{subindex:X2} does not exist.", "pdoIndex");
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
                    uint current = _od.ReadUnsigned(index, 0x01);
                    // Unassigned higher PDOs have an invalid identifier placeholder. A master
                    // must be able to repeat its destroy step before assigning their CAN-ID.
                    if (v == current && (v & CanOpenCobId.InvalidBit) != 0) return OdWriteDecision.Accept;
                    if (!IsUsablePdoCobIdWord(v)) return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);
                    // §7.5.2.35 / §7.5.2.37: "It is not allowed to change bit 0 to 29 while the PDO
                    // exists and is valid (bit 31 = 0b)." Bit 30 (RTR / reserved) may change.
                    const uint lower30 = 0x3FFF_FFFF;
                    if ((current & CanOpenCobId.InvalidBit) == 0 && (v & CanOpenCobId.InvalidBit) == 0
                        && (current & lower30) != (v & lower30))
                        return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);
                    // A frame on the SYNC CAN-ID (1005h) is a SYNC and nothing else, so a PDO
                    // cannot be created there (Codex on #133); 1005h refuses the mirror image.
                    if ((v & CanOpenCobId.InvalidBit) == 0 && IsTheSyncCanId(PdoFrameKey(v)))
                        return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);
                    return ValidateMpdoCreation(index, v, isTpdo);
                }
            case 0x02:
                {
                    // Tables 68 and 72: "An attempt to change the value of the transmission type to
                    // any not supported value shall be responded with … 0609 0030h."
                    byte type = value[0];
                    if (_od.TryReadUnsigned((ushort)(index + 0x200), 0, out var mode) && mode >= 0xFE
                        && !CanOpenTransmissionType.IsEventDriven(type))
                        return OdWriteDecision.Reject(SdoAbortCode.GeneralParameterIncompatibility);
                    bool reserved = isTpdo
                        ? CanOpenTransmissionType.IsReservedForTpdo(type)
                        : CanOpenTransmissionType.IsReservedForRpdo(type);
                    return reserved ? OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded) : OdWriteDecision.Accept;
                }
            case 0x05:
                if (isTpdo && (value[0] | (value[1] << 8)) != 0
                    && _od.TryReadUnsigned((ushort)(index + 0x200), 0, out var timerMode) && timerMode >= 0xFE)
                    return OdWriteDecision.Reject(SdoAbortCode.GeneralParameterIncompatibility);
                return OdWriteDecision.Accept;
            case 0x06:
                if (value[0] > 240) return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);
                goto case 0x03;
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
        if (_od.TryReadUnsigned(commIndex, 0x01, out var cobIdWord) && (cobIdWord & CanOpenCobId.InvalidBit) == 0)
            return OdWriteDecision.Reject(SdoAbortCode.UnsupportedAccess);

        if (subindex == 0x00)
        {
            byte count = value[0];
            if (count == 0 || count is 0xFE or 0xFF) return OdWriteDecision.Accept; // disabled or MPDO
            // Tables 69 / 73: 41h..FDh are reserved; FEh/FFh select MPDO instead of a mapping.
            if (count > 0x40) return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);
            // Step 4 of the re-mapping procedure: "If … the device detects that the mapping is
            // not valid or not possible … 0602 0000h or 0604 0042h."
            int totalBits = 0;
            for (byte s = 1; s <= count; s++)
            {
                if (!_od.TryReadUnsigned(index, s, out var raw) || raw == 0)
                    return OdWriteDecision.Reject(SdoAbortCode.ObjectDoesNotExist);
                var abort = ValidateMappingEntry(raw, isTpdo);
                if (abort is { } code) return OdWriteDecision.Reject(code);
                totalBits += (int)(raw & 0xFF);
            }
            return totalBits > 64
                ? OdWriteDecision.Reject(SdoAbortCode.PdoMappingLengthExceeded)
                : OdWriteDecision.Accept;
        }

        // Entries may only change while the mapping is disabled (sub0 = 0); the procedure of
        // §7.5.2.36 / §7.5.2.38 requires that step first.
        if (_od.ReadUnsigned(index, 0x00) != 0) return OdWriteDecision.Reject(SdoAbortCode.UnsupportedAccess);
        uint entry = ObjectDictionary.DecodeU32(value);
        if (entry == 0) return OdWriteDecision.Accept; // clears the slot
        // The entry is judged on its own. The total length is checked once, when step 4 enables
        // the mapping (sub0 = N, above): while it is disabled the other slots may still hold a
        // previous, wider mapping, and a master may write the new entries in any order, so
        // counting them here would refuse a mapping whose final form fits (#358, P-4).
        var reason = ValidateMappingEntry(entry, isTpdo);
        return reason is { } r ? OdWriteDecision.Reject(r) : OdWriteDecision.Accept;
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
        if (bitLength == 0 || bitLength > 64)
            return SdoAbortCode.DataTypeLengthMismatch;

        // §7.5.2.36: dummy mapping onto the static data types 0001h..0007h.
        if (entrySub == 0 && PdoMappingEntry.DummyBitLength(entryIndex) is var dummyBits && dummyBits != 0)
            return dummyBits == bitLength ? null : SdoAbortCode.ObjectCannotBeMapped;

        // A PDOMapping flag cannot make an object the standard forbids into a PDO target.
        if (entryIndex is >= 0x1000 and <= 0x1FFF && !IsCommunicationPdoTarget(entryIndex, entrySub))
            return SdoAbortCode.ObjectCannotBeMapped;

        if (!_od.TryGet(entryIndex, entrySub, out var target)) return SdoAbortCode.ObjectDoesNotExist;
        if (!target.PdoMappable) return SdoAbortCode.ObjectCannotBeMapped;
        var needed = isTpdo ? OdAccess.ReadOnly : OdAccess.WriteOnly;
        if ((target.Access & needed) == 0) return SdoAbortCode.ObjectCannotBeMapped;
        int fixedSize = OdEntryLayout.FixedSize(target.DataType);
        if (fixedSize > 0 && bitLength != fixedSize * 8
            && !(target.DataType == OdDataType.Boolean && bitLength == 1)) return SdoAbortCode.ObjectCannotBeMapped;
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

        // A transmission waiting out the inhibit time is an event the application already
        // raised; a record write that leaves the PDO valid (an event-timer change, say) must not
        // lose it, so it is re-requested against the rebuilt configuration below.
        bool inhibitedEventPending = rt.InhibitPending;
        bool wasValid = rt.Valid;
        uint previousCobId = rt.CobId;
        byte previousType = rt.TransmissionType;
        byte previousStart = rt.SyncStartValue;
        byte previousKind = rt.MappingKind;
        int previousSyncCounter = rt.SyncCounter;
        bool previousSyncStarted = rt.SyncStarted;
        bool previousSyncPending = rt.SyncAcyclicPending;
        byte[]? previousSample = rt.RtrBuffer;
        rt.EventTimerHandle?.Dispose();
        rt.EventTimerHandle = null;
        rt.InhibitHandle?.Dispose();
        rt.InhibitHandle = null;
        rt.InhibitPending = false;
        rt.SyncCounter = 0;
        rt.SyncStarted = false;
        rt.SyncAcyclicPending = false;
        rt.RtrBuffer = null;

        uint word = _od.TryReadUnsigned(comm, 0x01, out var w) ? w : CanOpenCobId.InvalidBit;
        rt.Valid = (word & CanOpenCobId.InvalidBit) == 0;
        rt.RtrAllowed = (word & CanOpenCobId.NoRtrBit) == 0;
        rt.CobId = PdoFrameKey(word);
        rt.TransmissionType = _od.TryReadUnsigned(comm, 0x02, out var t) ? (byte)t : CanOpenTransmissionType.EventDrivenManufacturer;
        rt.InhibitTime = TimeSpan.FromTicks((_od.TryReadUnsigned(comm, 0x03, out var ih) ? ih : 0) * (TimeSpan.TicksPerMillisecond / 10));
        rt.EventTimer = TimeSpan.FromMilliseconds(_od.TryReadUnsigned(comm, 0x05, out var et) ? et : 0);
        rt.SyncStartValue = (byte)(_od.TryReadUnsigned(comm, 0x06, out var sv) ? sv : 0);
        rt.MappingKind = (byte)(_od.TryReadUnsigned(map, 0, out var mode) ? mode : 0);
        bool samePdo = wasValid && rt.Valid && previousCobId == rt.CobId
            && previousType == rt.TransmissionType && previousStart == rt.SyncStartValue && previousKind == rt.MappingKind;
        if (samePdo)
        {
            rt.SyncCounter = previousSyncCounter;
            rt.SyncStarted = previousSyncStarted;
            rt.SyncAcyclicPending = previousSyncPending;
            rt.RtrBuffer = previousSample;
        }
        else
        {
            rt.MpdoPending.Clear();
            rt.MpdoOrder.Clear();
        }
        rt.Mapping = rt.MappingKind == (byte)MpdoAddressMode.Source ? ReadMpdoScanner() : ReadMappingRecord(map);
        rt.TotalBytes = rt.MappingKind >= 0xFE ? 8 : TotalBytes(rt.Mapping);

        if (rt.Valid)
        {
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
        rp.SyncPending = null;
        rp.Deadline?.Dispose();
        rp.Deadline = null;
        rp.EventTimer = TimeSpan.FromMilliseconds(_od.TryReadUnsigned(comm, 0x05, out var et) ? et : 0);
        rp.ShortFrameReported = false;
        rp.MpdoTargetReported = false;

        uint word = _od.TryReadUnsigned(comm, 0x01, out var w) ? w : CanOpenCobId.InvalidBit;
        rp.Valid = (word & CanOpenCobId.InvalidBit) == 0;
        rp.CobId = PdoFrameKey(word);
        rp.TransmissionType = _od.TryReadUnsigned(comm, 0x02, out var t) ? (byte)t : CanOpenTransmissionType.EventDrivenManufacturer;
        rp.MappingKind = (byte)(_od.TryReadUnsigned(map, 0, out var mode) ? mode : 0);
        rp.Mapping = ReadMappingRecord(map);
        rp.TotalBytes = rp.MappingKind >= 0xFE ? 8 : TotalBytes(rp.Mapping);
        if (rp.MappingKind == (byte)MpdoAddressMode.Source) RebuildMpdoDispatcher();
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
        foreach (var e in mapping) total += e.BitLength;
        return (total + 7) / 8;
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
            // tear the copy.
            if (!entry.IsDummy && _od.TryReadRaw(entry.Index, entry.Subindex, out var raw))
            {
                PdoBits.Copy(raw, 0, payload, offset, entry.BitLength);
            }
            offset += entry.BitLength;
        }
        return payload;
    }

    private void EmitTpdo(TpdoRuntime rt)
    {
        if (rt.MappingKind >= 0xFE)
        {
            EmitQueuedMpdo(rt);
            return;
        }
        if ((CanOpenTransmissionType.IsSynchronous(rt.TransmissionType)
             || rt.TransmissionType == CanOpenTransmissionType.RtrOnlySynchronous) && !IsInsideSyncWindow()) return;
        _ = SendPdoPayload(rt, BuildTpdoPayload(rt));
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
        var elapsed = TickMath.ElapsedFromTicks(elapsedTicks, time.Frequency);
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
        if (_state != NmtState.Operational || !rt.Valid || !rt.RtrAllowed || rt.MappingKind >= 0xFE) return;
        if (rt.TransmissionType == CanOpenTransmissionType.RtrOnlySynchronous)
        {
            // Table 72: "In case it is synchronous the CANopen device will start sampling with
            // the reception of every SYNC and then will buffer the PDO" — the buffer answers, and
            // before the first SYNC there is nothing sampled to answer with.
            if (IsInsideSyncWindow() && rt.RtrBuffer is { } buffered)
            {
                _ = SendPdoPayload(rt, buffered);
                rt.LastTransmission = _actor.TimeSource.GetTimestamp();
                rt.HasTransmitted = true;
            }
            return;
        }
        // §7.2.2.3: "the transmission of an event-driven PDO is initiated on receipt of a RTR";
        // §7.5.2.37: the inhibit time bounds every transmission of an event-driven TPDO, and a
        // PDO read is one of them. The RTR-only event-driven type (FDh) "will start sampling with
        // the reception of the RTR and will transmit the PDO immediately" (Table 72), outside the
        // inhibit time's scope.
        if (CanOpenTransmissionType.IsEventDriven(rt.TransmissionType))
        {
            RequestEventDrivenTransmission(rt);
            return;
        }
        if (rt.TransmissionType == CanOpenTransmissionType.RtrOnlyEventDriven) EmitTpdo(rt);
        // A synchronous TPDO (00h–F0h) "is transmitted after the SYNC" (Table 72) and nothing
        // else: its RTR is not answered here, the next SYNC transmits it (Codex on #133).
    }

    private void TriggerTpdoOnActor(int pdoIndex)
    {
        if (_state != NmtState.Operational) return;
        if (pdoIndex < 1 || pdoIndex > _pdoCount) return;
        var rt = _tpdos[pdoIndex];
        if (rt is null || !rt.Valid) return;
        if (rt.MappingKind >= 0xFE)
        {
            if (rt.MappingKind == (byte)MpdoAddressMode.Source) TriggerSourceMpdo(rt);
            return;
        }
        if (rt.TransmissionType == CanOpenTransmissionType.SynchronousAcyclic) rt.SyncAcyclicPending = true;
        else if (CanOpenTransmissionType.IsEventDriven(rt.TransmissionType)) RequestEventDrivenTransmission(rt);
        else EmitTpdo(rt);
    }

    // =========================================================================================
    // SYNC (FR-CO-010) — the trigger for every synchronous PDO.
    // =========================================================================================

    private void HandleSync(byte[] payload)
    {
        // Table 37: SYNC is not active in Stopped (nor before initialisation is finished).
        if (_state is NmtState.Stopped or NmtState.Initializing) return;
        if (!TryAcceptSync(payload, out var counter)) return;
        RaiseSyncReceived(DateTime.UtcNow, counter);
        if (_state != NmtState.Operational) return;

        for (int n = 1; n <= _pdoCount; n++)
        {
            var rt = _tpdos[n];
            if (rt is null || !rt.Valid) continue;
            byte type = rt.TransmissionType;
            if (type is >= CanOpenTransmissionType.SynchronousEverySync and <= CanOpenTransmissionType.SynchronousCyclicMax
                && _syncCounterOverflow > 1 && rt.SyncStartValue != 0 && !rt.SyncStarted)
            {
                if (counter.GetValueOrDefault() != rt.SyncStartValue) continue;
                rt.SyncStarted = true;
            }
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
        for (int n = 1; n <= _pdoCount; n++)
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
        if (rp.MappingKind >= 0xFE)
        {
            HandleMpdo(rp, payload);
            return;
        }

        // §7.5.2.36: fewer data bytes than mapped → not processed, EMCY 8210h if supported;
        // more than mapped → the first bytes up to the mapped length are used.
        if (payload.Length < rp.TotalBytes)
        {
            // Once per run of short frames: a producer that keeps sending one would otherwise
            // put an EMCY on the bus for every frame, and the EMCY traffic is what fills it.
            // A frame that fits ends the run, as does reconfiguring the RPDO.
            if (_emcyValid && !rp.ShortFrameReported)
            {
                rp.ShortFrameReported = true;
                var errorRegister = (byte)_od.ReadUnsigned(Co.ErrorRegister, 0x00);
                _ = EmitEmcy(new EmcyMessage(_nodeId, 0x8210, errorRegister));
            }
            return;
        }

        rp.ShortFrameReported = false;
        if (CanOpenTransmissionType.IsSynchronous(rp.TransmissionType) && !IsInsideSyncWindow()) return;
        RestartRpdoDeadline(rp);

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
            // HandleRpdo checked the complete bit length before actuating or queuing this payload.
            if (!entry.IsDummy && _od.TryGet(entry.Index, entry.Subindex, out var odEntry)
                && (odEntry.Access & OdAccess.WriteOnly) != 0)
            {
                var chunk = PdoBits.Extract(payload, offset, entry.BitLength);
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
            offset += entry.BitLength;
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
        _hasSync = false;
        Interlocked.Increment(ref _syncWindowGeneration);
        for (int n = 1; n <= _pdoCount; n++)
        {
            if (_tpdos[n] is { } rt)
            {
                rt.SyncCounter = 0;
                rt.SyncStarted = false;
                rt.SyncAcyclicPending = false;
                rt.RtrBuffer = null;
                rt.EventTimerHandle?.Dispose();
                rt.EventTimerHandle = null;
                if (rt.Valid && CanOpenTransmissionType.IsEventDriven(rt.TransmissionType))
                    ScheduleTpdoEventTimer(rt);
            }
            if (_rpdos[n] is { } rp)
            {
                rp.SyncPending = null;
                // OnLeaveOperational already cancelled the preceding receive deadline.
            }
        }
    }

    private void OnLeaveOperational()
    {
        _hasSync = false;
        Interlocked.Increment(ref _syncWindowGeneration);
        for (int n = 1; n <= _pdoCount; n++)
        {
            if (_tpdos[n] is { } rt)
            {
                rt.SyncAcyclicPending = false;
                rt.RtrBuffer = null;
                rt.InhibitHandle?.Dispose();
                rt.InhibitHandle = null;
                rt.InhibitPending = false;
                rt.MpdoOrder.Clear();
                rt.MpdoPending.Clear();
            }
            if (_rpdos[n] is { } rp)
            {
                rp.SyncPending = null;
                rp.Deadline?.Dispose();
                rp.Deadline = null;
            }
        }
    }

    private void DisposePdoRuntime()
    {
        foreach (var rp in _rpdos) rp?.Deadline?.Dispose();
        for (int n = 1; n <= _pdoCount; n++)
        {
            if (_tpdos[n] is { } rt)
            {
                rt.EventTimerHandle?.Dispose();
                rt.InhibitHandle?.Dispose();
            }
        }
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
        if (!_options.EnableChangeOfStateTpdo && !_options.EnableChangeOfStateSrdo) return;
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

        // Each kind only on its own option: an entry mapped in both a TPDO and an SRDO is dirty
        // for the SRDO's sake even when change-of-state TPDOs are off.
        if (_options.EnableChangeOfStateTpdo)
        {
            for (int n = 1; n <= _pdoCount; n++)
            {
                var rt = _tpdos[n];
                if (rt is null || !rt.Valid || !IsChangeOfStateTriggered(rt.TransmissionType)) continue;
                if (rt.MappingKind == (byte)MpdoAddressMode.Source)
                {
                    TriggerSourceMpdo(rt, dirty);
                    continue;
                }
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

        if (_options.EnableChangeOfStateSrdo)
        {
            for (int n = 1; n <= _srdoCount; n++)
            {
                if (_srdo.GetState(n).Direction != Safety.SrdoDirection.Transmit) continue;
                bool hit = false;
                foreach (var e in Safety.SrdoRecords.ReadMapping(_od, n))
                {
                    if (dirty.Contains(CosKey(e.Index, e.Subindex)))
                    {
                        hit = true;
                        break;
                    }
                }
                if (hit) _srdo.Trigger(n);
            }
        }
    }

    private static bool IsChangeOfStateTriggered(byte transmissionType)
        => transmissionType == CanOpenTransmissionType.SynchronousAcyclic
           || CanOpenTransmissionType.IsEventDriven(transmissionType);

    // Rebuilds the volatile pre-filter snapshot of OD entries mapped in at least one TPDO whose
    // transmission type reacts to a change of state. Reads the records, not the runtime, because
    // it runs on the writing thread as soon as a TPDO record is written — inside the write gate,
    // so the writer's next write already meets the new filter — and again on the actor after each
    // rebuild (Codex on #133).
    private void RebuildCosRelevantEntries()
    {
        var set = new HashSet<uint>();
        if (_options.EnableChangeOfStateTpdo)
        {
            for (int n = 1; n <= _pdoCount; n++)
            {
                var comm = (ushort)(Co.TpdoComm + n - 1);
                if (!_od.TryReadUnsigned(comm, 0x01, out var word) || (word & CanOpenCobId.InvalidBit) != 0) continue;
                byte type = _od.TryReadUnsigned(comm, 0x02, out var t) ? (byte)t : CanOpenTransmissionType.EventDrivenManufacturer;
                if (!IsChangeOfStateTriggered(type)) continue;
                var map = (ushort)(Co.TpdoMap + n - 1);
                var entries = _od.TryReadUnsigned(map, 0, out var mode) && mode == (byte)MpdoAddressMode.Source
                    ? ReadMpdoScanner() : ReadMappingRecord(map);
                foreach (var e in entries) set.Add(CosKey(e.Index, e.Subindex));
            }
        }
        if (_options.EnableChangeOfStateSrdo) _srdo.CollectChangeOfStateEntries(set, CosKey);
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
        public byte MappingKind { get; set; }
        public bool Valid { get; set; }
        public bool RtrAllowed { get; set; }
        public Dictionary<uint, byte[]> MpdoPending { get; } = new();
        public Queue<uint> MpdoOrder { get; } = new();
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
        public byte SyncStartValue { get; set; }
        public bool SyncStarted { get; set; }
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
        public byte MappingKind { get; set; }
        public bool Valid { get; set; }
        public uint CobId { get; set; }
        public byte TransmissionType { get; set; } = CanOpenTransmissionType.EventDrivenManufacturer;
        public PdoMappingEntry[] Mapping { get; set; } = Array.Empty<PdoMappingEntry>();
        public int TotalBytes { get; set; }
        public byte[]? SyncPending { get; set; }
        public TimeSpan EventTimer { get; set; }
        public IDisposable? Deadline { get; set; }
        public bool ShortFrameReported { get; set; }
        public bool MpdoTargetReported { get; set; }
    }
}
