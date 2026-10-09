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
        if (frame1.Length > 8)
            throw new ArgumentOutOfRangeException(nameof(frame1), frame1.Length, "A classic CAN frame carries at most 8 bytes.");
        if (frame2.Length > 8)
            throw new ArgumentOutOfRangeException(nameof(frame2), frame2.Length, "A classic CAN frame carries at most 8 bytes.");
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
            var read = await TryReadLiveMappingCoreAsync(peerNodeId, SrdoRecords.MapIndex(n), 2, SrdoRecords.MappingSubindices, cancellationToken)
                .ConfigureAwait(false);
            // Unlike a PDO (FR-CO-030), a live mapping the gate only partly lets through is not
            // replaced by the file: the live mapping is known to exist and to differ from what the
            // bound description declares, and splitting safety data by a mapping the device does
            // not use would hand the caller wrong values with a clean "decoded".
            if (read.GateRefusedAfterCount)
                return new ForeignSrdoObserveResult(cobId1, new ForeignPdoObservation(ForeignPdoKind.Srdo, n, false, null, 0,
                    $"the live mapping declares {read.DeclaredEntries} entries but the bound description declares fewer; the file's mapping is not used for safety data"), null);
            if (read.Entries is { } liveEntries) { mapping = liveEntries; origin = ForeignPdoMappingOrigin.LiveMapping; }
            else if (TryDescribedSrdoMapping(peerDescription, n, peerNodeId, out mapping, out var why)) origin = ForeignPdoMappingOrigin.DeviceDescription;
            else return new ForeignSrdoObserveResult(cobId1, new ForeignPdoObservation(ForeignPdoKind.Srdo, n, false, null, 0,
                "the live mapping record could not be read, and the description's mapping could not be used: " + why), null);
            int total = 0;
            foreach (var e in mapping)
            {
                if (e.IsDummy)
                    return new ForeignSrdoObserveResult(cobId1, new ForeignPdoObservation(ForeignPdoKind.Srdo, n, false, origin, 0,
                        "the mapping contains a dummy entry, which an SRDO mapping does not carry"), null);
                total += e.ByteLength;
            }
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

    /// <summary>The existing record n with live 1300h+n:05 == cobId1; the file's value when that
    /// upload fails. A deleted record keeps its COB-IDs and another SRDO may take them over — the
    /// device refuses only the ids of an existing SRDO (§8.4.2.2) — so a record whose direction
    /// (live, or the file's when the upload fails) is 0 does not match.</summary>
    private async Task<int?> MatchSrdoAsync(CanOpenDeviceDescription description, byte peerNodeId, uint cobId1, CancellationToken cancellationToken)
    {
        int count = 0;
        for (int n = 1; n <= SrdoRecords.MaxSrdoCount; n++)
            if (description.Objects.Objects.ContainsKey(SrdoRecords.CommIndex(n))) count = n;
        for (int n = 1; n <= count; n++)
        {
            var comm = SrdoRecords.CommIndex(n);
            uint? liveWord = null;
            try
            {
                var raw = await SdoUploadAsync(peerNodeId, comm, 0x05, cancellationToken).ConfigureAwait(false);
                if (raw.Length >= 4) liveWord = ObjectDictionary.DecodeU32(raw);
            }
            catch (Exception ex) when (IsLiveReadUnavailable(ex)) { }
            // A live word that was read decides, whether it matches or not: the file is only for a record that could not be read.
            if (liveWord is { } live)
            {
                if (SrdoCobId1(live) != cobId1) continue;
            }
            else if (!(TryDescribedObject(description, comm, out var record) && TryDescribedValue(record, 0x05, out var text)
                && ParseDescribedUnsigned(text, peerNodeId) is { } word && SrdoCobId1(word) == cobId1))
                continue;
            if (await IsDeletedSrdoAsync(description, peerNodeId, comm, cancellationToken).ConfigureAwait(false)) continue;
            return n;
        }
        return null;
    }

    /// <summary>Whether sub-index 1 of the record is 0: live, or the file's value when the upload
    /// fails. A direction neither yields is not taken for a deletion.</summary>
    private async Task<bool> IsDeletedSrdoAsync(CanOpenDeviceDescription description, byte peerNodeId, ushort comm, CancellationToken cancellationToken)
    {
        try
        {
            var raw = await SdoUploadAsync(peerNodeId, comm, 0x01, cancellationToken).ConfigureAwait(false);
            if (raw.Length >= 1) return raw[0] == 0;
        }
        catch (Exception ex) when (IsLiveReadUnavailable(ex)) { }
        return TryDescribedObject(description, comm, out var record) && TryDescribedValue(record, 0x01, out var text)
            && ParseDescribedUnsigned(text, peerNodeId) is 0;
    }

    /// <summary>The COB-ID 1 of a 1301h–1340h:05 word: its bits 0–10, and null when any bit above
    /// them is set (bit 31 included) — DSP 304 defines no flags for it. Not
    /// <see cref="UsableCanId"/>, which also rejects 101h–180h, the very range of SRDOs.</summary>
    private static uint? SrdoCobId1(uint word) => (word & ~CanOpenCobId.CanIdMask) == 0 ? word : null;

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
