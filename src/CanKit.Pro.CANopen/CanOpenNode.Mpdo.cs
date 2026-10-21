using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Pro.CANopen.Emcy;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Pdo;
using CanKit.Pro.CANopen.Sdo;

namespace CanKit.Pro.CANopen;

// FR-CO-057: CiA 301 §7.2.3, §7.5.2.39/40 (SAM/DAM and scanner/dispatcher lists).
internal sealed partial class CanOpenNode : ICanOpenPdo
{
    // Actor-owned snapshot; OD enumeration belongs to configuration, not reception.
    private ulong[] _mpdoDispatcher = Array.Empty<ulong>();

    private void RebuildMpdoDispatcher() => _mpdoDispatcher = MpdoListWords(0x1FD0, 0x1FFF, 8).ToArray();
    /// <inheritdoc />
    public void ConfigureMultiplexedTpdo(int pdoIndex, MpdoAddressMode mode, uint cobId)
        => ConfigureMpdo(pdoIndex, mode, cobId, isTpdo: true);

    /// <inheritdoc />
    public void ConfigureMultiplexedRpdo(int pdoIndex, MpdoAddressMode mode, uint cobId)
        => ConfigureMpdo(pdoIndex, mode, cobId, isTpdo: false);

    private void ConfigureMpdo(int number, MpdoAddressMode mode, uint cobId, bool isTpdo)
    {
        ThrowIfDisposed();
        if (number < 1 || number > _pdoCount) throw new ArgumentOutOfRangeException(nameof(number));
        if (mode is not (MpdoAddressMode.Source or MpdoAddressMode.Destination))
            throw new ArgumentOutOfRangeException(nameof(mode));
        ValidateCobIdArgument(cobId);
        ushort comm = (ushort)((isTpdo ? Co.TpdoComm : Co.RpdoComm) + number - 1);
        ushort map = (ushort)(comm + 0x200);
        Action writeRecords = () =>
        {
            uint current = _od.ReadUnsigned(comm, 1);
            _od.WriteUnsigned(comm, 1, ((current & CanOpenCobId.InvalidBit) != 0 ? cobId : current) | CanOpenCobId.InvalidBit);
            _od.WriteUnsigned(map, 0, 0);
            _od.WriteUnsigned(map, 0, (byte)mode);
            _od.WriteUnsigned(comm, 2, CanOpenTransmissionType.EventDrivenManufacturer);
            if (_od.TryGet(comm, 3, out _)) _od.WriteUnsigned(comm, 3, 0);
            if (_od.TryGet(comm, 5, out _)) _od.WriteUnsigned(comm, 5, 0);
            if (isTpdo && _od.TryGet(comm, 6, out _)) _od.WriteUnsigned(comm, 6, 0);
            _od.WriteUnsigned(comm, 1, cobId | (isTpdo ? CanOpenCobId.NoRtrBit : 0));
        };
        RunOnActorAndWait(() => _od.Transaction(writeRecords));
    }

    /// <inheritdoc />
    public Task SendSourceMpdoAsync(int pdoIndex, ushort index, byte subindex,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _actor.PostAsync(() =>
        {
            var rt = RequireMpdo(pdoIndex, MpdoAddressMode.Source);
            var raw = ReadMpdoSource(index, subindex);
            QueueMpdo(rt, BuildMpdo(false, _nodeId, index, subindex, raw));
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task SendDestinationMpdoAsync(int pdoIndex, byte destinationNodeId, ushort index, byte subindex,
        ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (destinationNodeId > 127) throw new ArgumentOutOfRangeException(nameof(destinationNodeId));
        if (value.Length is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(value));
        var copy = value.ToArray();
        return _actor.PostAsync(() => QueueMpdo(RequireMpdo(pdoIndex, MpdoAddressMode.Destination),
            BuildMpdo(true, destinationNodeId, index, subindex, copy)), cancellationToken);
    }

    private TpdoRuntime RequireMpdo(int number, MpdoAddressMode mode)
    {
        if (number < 1 || number > _pdoCount) throw new ArgumentOutOfRangeException(nameof(number));
        var rt = _tpdos[number];
        if (rt is null || !rt.Valid || rt.MappingKind != (byte)mode)
            throw new InvalidOperationException("The PDO is not a valid MPDO of the requested addressing mode.");
        return rt;
    }

    private byte[] ReadMpdoSource(ushort index, byte subindex)
    {
        if (!IsMpdoTarget(index, subindex, isTpdo: true, out var target))
            throw new ArgumentException($"Object 0x{index:X4}:{subindex:X2} is not a readable, PDO-mappable object of 1..4 bytes.");
        _od.TryReadRaw(index, subindex, out var raw);
        return raw;
    }

    private bool IsMpdoTarget(ushort index, byte subindex, bool isTpdo, out OdEntry target)
    {
        if (!_od.TryGet(index, subindex, out target!)) return false;
        return (index is not (>= 0x1000 and <= 0x1FFF) || IsCommunicationPdoTarget(index, subindex)) && target.PdoMappable
            && (target.Access & (isTpdo ? OdAccess.ReadOnly : OdAccess.WriteOnly)) != 0
            && target.Size is >= 1 and <= 4;
    }

    private static byte[] BuildMpdo(bool destination, byte node, ushort index, byte subindex, byte[] value)
    {
        var frame = new byte[8];
        frame[0] = (byte)((destination ? 0x80 : 0) | node);
        frame[1] = (byte)index;
        frame[2] = (byte)(index >> 8);
        frame[3] = subindex;
        Array.Copy(value, 0, frame, 4, value.Length);
        return frame;
    }

    private void QueueMpdo(TpdoRuntime rt, byte[] payload)
    {
        if (_state != NmtState.Operational) return;
        uint key = (uint)(payload[0] | (payload[1] << 8) | (payload[2] << 16) | (payload[3] << 24));
        if (!rt.MpdoPending.ContainsKey(key)) rt.MpdoOrder.Enqueue(key);
        rt.MpdoPending[key] = payload;
        RequestEventDrivenTransmission(rt);
    }

    private void EmitQueuedMpdo(TpdoRuntime rt)
    {
        while (rt.MpdoOrder.Count != 0)
        {
            uint key = rt.MpdoOrder.Dequeue();
            var payload = rt.MpdoPending[key];
            rt.MpdoPending.Remove(key);
            _ = SendControlFrame(rt.CobId, payload);
            rt.LastTransmission = _actor.TimeSource.GetTimestamp();
            rt.HasTransmitted = true;
            if (rt.InhibitTime == TimeSpan.Zero) continue;
            if (rt.MpdoOrder.Count != 0) RequestEventDrivenTransmission(rt);
            return;
        }
    }

    private void TriggerSourceMpdo(TpdoRuntime rt, HashSet<uint>? dirty = null)
    {
        foreach (var entry in rt.Mapping.Where(entry => dirty is null || dirty.Contains(CosKey(entry.Index, entry.Subindex))))
        {
            QueueMpdo(rt, BuildMpdo(false, _nodeId, entry.Index, entry.Subindex,
                ReadMpdoSource(entry.Index, entry.Subindex)));
        }
    }

    private void HandleMpdo(RpdoRuntime rp, byte[] payload)
    {
        if (payload.Length != 8)
        {
            if (_emcyValid && !rp.ShortFrameReported)
            {
                rp.ShortFrameReported = true;
                _ = EmitEmcy(new EmcyMessage(_nodeId, 0x8210, (byte)_od.ReadUnsigned(Co.ErrorRegister, 0)));
            }
            return;
        }
        rp.ShortFrameReported = false;
        bool destination = (payload[0] & 0x80) != 0;
        byte node = (byte)(payload[0] & 0x7F);
        if (destination != (rp.MappingKind == (byte)MpdoAddressMode.Destination)) return;
        ushort index = (ushort)(payload[1] | (payload[2] << 8));
        byte subindex = payload[3];
        bool written = false;
        if (destination)
        {
            if (node != 0 && node != _nodeId) return;
            written = WriteMpdoTarget(index, subindex, payload);
            if (!written && _emcyValid && !rp.MpdoTargetReported)
            {
                rp.MpdoTargetReported = true;
                _ = EmitEmcy(new EmcyMessage(_nodeId, 0x8230, (byte)_od.ReadUnsigned(Co.ErrorRegister, 0)));
            }
        }
        else
        {
            if (node == 0) return;
            foreach (ulong entry in _mpdoDispatcher)
            {
                int count = Math.Max(1, (int)(entry >> 56));
                int delta = subindex - (byte)(entry >> 8);
                if ((byte)entry != node || (ushort)(entry >> 16) != index || delta < 0 || delta >= count) continue;
                int localSub = (byte)(entry >> 32) + delta;
                if (localSub > 255) continue;
                written |= WriteMpdoTarget((ushort)(entry >> 40), (byte)localSub, payload);
            }
        }
        if (!written) return;
        rp.MpdoTargetReported = false;
        RestartRpdoDeadline(rp);
        RaiseRpdoReceived(rp.PdoIndex, rp.CobId, payload);
    }

    private bool WriteMpdoTarget(ushort index, byte subindex, byte[] payload)
    {
        if (!IsMpdoTarget(index, subindex, isTpdo: false, out var target)) return false;
        var value = new byte[target.Size];
        Array.Copy(payload, 4, value, 0, value.Length);
        // The entry is resolved again for the write: re-declared since with another width, gone,
        // or refused by a write validator, it is a target that is not available rather than an
        // exception out of the frame handler (#358, P-5). A DAM caller reports that as the latched
        // 8230h; a SAM caller skips the dispatcher entry, as it does a target that fails the check.
        return _od.TryWriteRaw(index, subindex, value, out _);
    }

    private IEnumerable<ulong> MpdoListWords(ushort first, ushort last, int width)
    {
        foreach (var key in _od.SnapshotKeys().OrderBy(k => k))
        {
            ushort index = (ushort)(key >> 8);
            byte sub = (byte)key;
            if (index < first || index > last || sub == 0 || sub == 255) continue;
            if (!_od.TryReadUnsigned(index, 0, out var count) || sub > count) continue;
            if (!_od.TryReadRaw(index, sub, out var raw) || raw.Length != width) continue;
            ulong word = 0;
            for (int b = width - 1; b >= 0; b--) word = (word << 8) | raw[b];
            if (word != 0) yield return word;
        }
    }

    private PdoMappingEntry[] ReadMpdoScanner()
    {
        var entries = new List<PdoMappingEntry>();
        var seen = new HashSet<uint>();
        foreach (ulong word in MpdoListWords(0x1FA0, 0x1FCF, 4))
        {
            ushort index = (ushort)(word >> 8);
            int count = Math.Max(1, (int)(word >> 24));
            for (int sub = (byte)word; sub <= 255 && sub < (byte)word + count; sub++)
            {
                if (!IsMpdoTarget(index, (byte)sub, isTpdo: true, out var target)) continue;
                if (seen.Add(CosKey(index, (byte)sub))) entries.Add(new PdoMappingEntry(index, (byte)sub, (byte)(target.Size * 8)));
            }
        }
        return entries.ToArray();
    }

    private OdWriteDecision ValidateMpdoCreation(ushort comm, uint word, bool isTpdo)
    {
        if ((word & CanOpenCobId.InvalidBit) != 0) return OdWriteDecision.Accept;
        if (!_od.TryReadUnsigned((ushort)(comm + 0x200), 0, out var mode) || mode < 0xFE) return OdWriteDecision.Accept;
        if (_od.TryReadUnsigned(comm, 2, out var type) && !CanOpenTransmissionType.IsEventDriven((byte)type))
            return OdWriteDecision.Reject(SdoAbortCode.GeneralParameterIncompatibility);
        if (isTpdo && _od.TryReadUnsigned(comm, 5, out var timer) && timer != 0)
            return OdWriteDecision.Reject(SdoAbortCode.GeneralParameterIncompatibility);
        if (!isTpdo || mode != (byte)MpdoAddressMode.Source) return OdWriteDecision.Accept;
        for (int n = 1; n <= _pdoCount; n++)
        {
            ushort other = (ushort)(Co.TpdoComm + n - 1);
            if (other == comm || !_od.TryReadUnsigned(other, 1, out var cob) || (cob & CanOpenCobId.InvalidBit) != 0) continue;
            if (_od.TryReadUnsigned((ushort)(other + 0x200), 0, out var mapping) && mapping == mode)
                return OdWriteDecision.Reject(SdoAbortCode.GeneralParameterIncompatibility);
        }
        return OdWriteDecision.Accept;
    }
}
