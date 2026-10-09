using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Pro.CANopen.Pdo;
using CanKit.Pro.CANopen.Safety;

namespace CanKit.Pro.CANopen;

/// <summary>Splitting a peer's SRDO pair (CiA DSP 304 V1.0 §8.1) with the rules of
/// <see cref="ObserveForeignPdoAsync"/>: live record before file, nothing written here.</summary>
internal sealed partial class CanOpenNode
{
    public async Task<ForeignSrdoObserveResult> ObserveForeignSrdoAsync(byte peerNodeId, uint cobId1, ReadOnlyMemory<byte> frame1,
        ReadOnlyMemory<byte> frame2, CanOpenDeviceDescription peerDescription, IForeignPdoSink sink,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (peerDescription is null) throw new ArgumentNullException(nameof(peerDescription));
        if (sink is null) throw new ArgumentNullException(nameof(sink));
        CanOpenCobId.ValidateNodeId(peerNodeId);
        if (!SrdoFrames.IsCobId1(cobId1))
            throw new ArgumentOutOfRangeException(nameof(cobId1), cobId1, "COB-ID 1 of an SRDO is odd, 257..383 (CiA DSP 304 Figure 7).");
        if (frame1.Length > 8 || frame2.Length > 8)
            throw new ArgumentOutOfRangeException(nameof(frame1), "A classic CAN frame carries at most 8 bytes.");
        var first = frame1.ToArray();
        var second = frame2.ToArray();
        var gate = _foreignPdoObserve.GetOrAdd(peerNodeId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int? number = await MatchSrdoAsync(peerDescription, peerNodeId, cobId1, cancellationToken).ConfigureAwait(false);
            if (number is not { } n)
                return new ForeignSrdoObserveResult(cobId1, null, "no SRDO communication record of the peer uses this COB-ID 1");
            if (!SrdoFrames.IsInversePair(first, second))
                return new ForeignSrdoObserveResult(cobId1, new ForeignPdoObservation(ForeignPdoKind.Srdo, n, decoded: false, origin: null, signalsWritten: 0,
                    "the second frame is not the bitwise inverse of the first with the same length (§8.1)"), null);
            PdoMappingEntry[] mapping;
            ForeignPdoMappingOrigin origin;
            var live = await TryReadLiveSrdoMappingAsync(peerNodeId, n, cancellationToken).ConfigureAwait(false);
            if (live is { } liveEntries) { mapping = liveEntries; origin = ForeignPdoMappingOrigin.LiveMapping; }
            else if (TryDescribedSrdoMapping(peerDescription, n, peerNodeId, out mapping, out var why)) origin = ForeignPdoMappingOrigin.DeviceDescription;
            else return new ForeignSrdoObserveResult(cobId1, new ForeignPdoObservation(ForeignPdoKind.Srdo, n, false, null, 0,
                "the live mapping record could not be read, and the description's mapping could not be used: " + why), null);
            int total = 0;
            foreach (var e in mapping) total += e.ByteLength;
            if (first.Length < total)
                return new ForeignSrdoObserveResult(cobId1, new ForeignPdoObservation(ForeignPdoKind.Srdo, n, false, origin, 0,
                    $"the frames have {first.Length} byte(s) and the mapping needs {total}"), null);
            int offset = 0, written = 0;
            foreach (var entry in mapping)
            {
                var chunk = new byte[entry.ByteLength];
                Buffer.BlockCopy(first, offset, chunk, 0, entry.ByteLength);
                sink.Write(new ForeignPdoSignal(peerNodeId, ForeignPdoKind.Srdo, n, cobId1, entry.Index, entry.Subindex, chunk, origin));
                written++;
                offset += entry.ByteLength;
            }
            return new ForeignSrdoObserveResult(cobId1, new ForeignPdoObservation(ForeignPdoKind.Srdo, n, true, origin, written, null), null);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The record n with live 1300h+n:05 == cobId1; the file's value when that upload fails.</summary>
    private async Task<int?> MatchSrdoAsync(CanOpenDeviceDescription description, byte peerNodeId, uint cobId1, CancellationToken cancellationToken)
    {
        int count = 0;
        for (int n = 1; n <= SrdoRecords.MaxSrdoCount; n++)
            if (description.Objects.Objects.ContainsKey(SrdoRecords.CommIndex(n))) count = n;
        for (int n = 1; n <= count; n++)
        {
            var comm = SrdoRecords.CommIndex(n);
            uint? liveId = null;
            try
            {
                var raw = await SdoUploadAsync(peerNodeId, comm, 0x05, cancellationToken).ConfigureAwait(false);
                if (raw.Length >= 4) liveId = ObjectDictionary.DecodeU32(raw) & CanOpenCobId.CanIdMask;
            }
            catch (Exception ex) when (IsLiveReadUnavailable(ex)) { }
            if (liveId is { } id)
            {
                if (id == cobId1) return n;
                continue;
            }
            if (TryDescribedObject(description, comm, out var record) && TryDescribedValue(record, 0x05, out var text)
                && ParseDescribedUnsigned(text, peerNodeId) is { } word && (word & CanOpenCobId.CanIdMask) == cobId1)
                return n;
        }
        return null;
    }

    /// <summary>Sub0 and the odd entries of 1380h+n; null when unavailable or malformed (an even
    /// count ≤ 16, each odd slot non-zero and byte-aligned, ≤ 8 bytes). A live count of 0 is a mapping.</summary>
    private async Task<PdoMappingEntry[]?> TryReadLiveSrdoMappingAsync(byte peerNodeId, int n, CancellationToken cancellationToken)
    {
        var map = SrdoRecords.MapIndex(n);
        byte[] countBytes;
        try { countBytes = await SdoUploadAsync(peerNodeId, map, 0x00, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (IsLiveReadUnavailable(ex)) { return null; }
        if (countBytes.Length < 1) return null;
        int count = countBytes[0];
        if ((count & 1) != 0 || count > SrdoRecords.MappingSubindices) return null;
        var entries = new List<PdoMappingEntry>(count / 2);
        int total = 0;
        for (byte s = 1; s <= count; s += 2)
        {
            byte[] raw;
            try { raw = await SdoUploadAsync(peerNodeId, map, s, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (IsLiveReadUnavailable(ex)) { return null; }
            if (raw.Length < 4) return null;
            uint word = ObjectDictionary.DecodeU32(raw);
            if (word == 0) return null;
            PdoMappingEntry entry;
            try { entry = new PdoMappingEntry((ushort)(word >> 16), (byte)(word >> 8), (byte)word); }
            catch (ArgumentOutOfRangeException) { return null; }
            total += entry.ByteLength;
            if (total > 8) return null;
            entries.Add(entry);
        }
        return entries.ToArray();
    }

    private static bool TryDescribedSrdoMapping(CanOpenDeviceDescription description, int n, byte peerNodeId,
        out PdoMappingEntry[] entries, out string reason)
    {
        entries = Array.Empty<PdoMappingEntry>();
        if (!TryDescribedObject(description, SrdoRecords.MapIndex(n), out var record)) { reason = "the file declares no mapping record"; return false; }
        if (!TryDescribedValue(record, 0x00, out var countText) || ParseDescribedUnsigned(countText, peerNodeId) is not { } count)
        { reason = "the file has no readable mapping count"; return false; }
        if ((count & 1) != 0 || count > SrdoRecords.MappingSubindices) { reason = "the file's mapping count is not an even number ≤ 16"; return false; }
        var list = new List<PdoMappingEntry>();
        int total = 0;
        for (byte s = 1; s <= count; s += 2)
        {
            if (!TryDescribedValue(record, s, out var text) || ParseDescribedUnsigned(text, peerNodeId) is not { } word || word == 0)
            { reason = $"the file's mapping entry {s} is missing or unreadable"; return false; }
            try { list.Add(new PdoMappingEntry((ushort)(word >> 16), (byte)(word >> 8), (byte)word)); }
            catch (ArgumentOutOfRangeException) { reason = $"the file's mapping entry {s} is not byte-aligned"; return false; }
            total += list[list.Count - 1].ByteLength;
            if (total > 8) { reason = "the file's mapping exceeds 8 bytes"; return false; }
        }
        entries = list.ToArray();
        reason = "";
        return true;
    }
}
