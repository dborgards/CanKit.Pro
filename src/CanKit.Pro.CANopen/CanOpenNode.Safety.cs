using System;
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
internal sealed partial class CanOpenNode
{
    private readonly int _srdoCount;

    /// <summary>The highest SRDO record a description declares; 0 without one. Completed when the
    /// loader learns the safety objects (device-description task).</summary>
    private static int DescribedSrdoCount(CanOpenDeviceDescription? description) => 0;

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

    /// <summary>One CAN-ID carries one communication object (Codex on #133). PDO, SYNC and EMCY
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
}
