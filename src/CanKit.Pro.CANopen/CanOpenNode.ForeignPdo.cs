using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Pro.CANopen.Pdo;
using CanKit.Pro.CANopen.Sdo;
using EdsDcfNet;
using EdsDcfNet.Models;
using EdsDcfNet.Utilities;

namespace CanKit.Pro.CANopen;

/// <summary>
/// Decodes a PDO of another node (the mapping records <c>1600h</c>–<c>1603h</c> and
/// <c>1A00h</c>–<c>1A03h</c>) into a caller-supplied sink. The local object dictionary is not
/// written.
/// </summary>
/// <remarks>
/// The COB-ID is read live from <c>1400h:01</c> / <c>1800h:01</c>. The mapping is read live from
/// <c>1600h</c>–<c>1603h</c> / <c>1A00h</c>–<c>1A03h</c>. A live value is used ahead of the file.
/// The file is used when that upload aborts, times out, is refused by the peer-SDO gate, or does
/// not come back as a usable value. The live reads of one observation are one snapshot of the
/// peer: the observer takes the peer's SDO channel without waiting and holds it to the end, so a
/// busy channel at the start — a call in flight, a safety transaction — makes the whole snapshot
/// the file's, and a caller arriving meanwhile is refused as in flight (a safety transaction
/// waits). Observations of one peer run one at a time, so two of them do not trip that in-flight
/// limit on each other. A CAN-ID CiA 301 §7.3.5 restricts is not a match.
/// </remarks>
internal sealed partial class CanOpenNode
{
    /// <summary>One observation of a peer at a time. The SDO client allows a single transfer per
    /// server; queueing here lets a second <see cref="ObserveForeignPdoAsync"/> wait instead of
    /// failing with <see cref="InvalidOperationException"/>.</summary>
    private readonly ConcurrentDictionary<byte, SemaphoreSlim> _foreignPdoObserve = new();

    /// <summary>Test seam: awaited between the observer's COB-ID match and its mapping reads —
    /// the gap in which another caller to the peer could otherwise come in.</summary>
    internal Func<Task>? AfterPdoMatchForTests { get; set; }

    /// <inheritdoc />
    public async Task<ForeignPdoObserveResult> ObserveForeignPdoAsync(byte peerNodeId, uint cobId,
        ReadOnlyMemory<byte> payload, CanOpenDeviceDescription peerDescription, IForeignPdoSink sink,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (peerDescription is null) throw new ArgumentNullException(nameof(peerDescription));
        if (sink is null) throw new ArgumentNullException(nameof(sink));
        if (peerNodeId is < CanOpenCobId.MinNodeId or > CanOpenCobId.MaxNodeId)
            throw new ArgumentOutOfRangeException(nameof(peerNodeId), peerNodeId,
                $"CANopen node-id must be in [{CanOpenCobId.MinNodeId}, {CanOpenCobId.MaxNodeId}].");
        if ((cobId & 0xC000_0000) != 0
            || ((cobId & CanOpenCobId.ExtendedFrameBit) == 0 && cobId > CanOpenCobId.CanIdMask))
            throw new ArgumentOutOfRangeException(nameof(cobId), cobId,
                "A PDO key is an 11-bit ID, or a 29-bit ID with bit 29 set.");
        if (payload.Length > 8)
            throw new ArgumentOutOfRangeException(nameof(payload), payload.Length,
                "A classic CAN PDO payload is at most 8 bytes.");

        // The caller's buffer may be a frame slot that is reused as soon as this method yields.
        // Copy before the first wait, as SdoDownloadAsync does.
        var payloadCopy = payload.ToArray();

        var gate = _foreignPdoObserve.GetOrAdd(peerNodeId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        // The live snapshot — COB-IDs, mapping — is one state of the peer: the peer's SDO channel
        // is taken here, without waiting, and held to the end, so a caller that arrives meanwhile
        // is refused as in flight (a safety transaction waits, as for any call in flight) instead
        // of taking the channel between two of these uploads and turning the rest of the snapshot
        // into file values. A channel busy at the start is the "already in flight" refusal every
        // upload below meets, so the whole snapshot is the file's (#295).
        var channel = PeerSdoChannel(peerNodeId);
        bool held = channel.Wait(0);
        Exception? unavailable = null;
        Task<byte[]> UploadLive(ushort index, byte subindex, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return unavailable is { } failure ? Task.FromException<byte[]>(failure) : ReadLiveAsync(index, subindex, ct);
        }
        async Task<byte[]> ReadLiveAsync(ushort index, byte subindex, CancellationToken ct)
        {
            try { return await SdoUploadCoreAsync(peerNodeId, index, subindex, SdoTransferMode.Auto, ct).ConfigureAwait(false); }
            catch (Exception ex) when (IsPeerUnavailableForPdoSnapshot(ex))
            {
                // A timeout or transport failure applies to this snapshot, not just one OD
                // record. Otherwise an offline peer with 512 PDOs incurs 1,024 timeouts.
                unavailable = ex;
                throw;
            }
        }
        LiveUpload upload = held ? UploadLive
            : (_, _, _) => Task.FromException<byte[]>(InFlightRefusal(peerNodeId));
        try
        {
            return await ObserveForeignPdoCoreAsync(peerNodeId, cobId, payloadCopy, peerDescription, sink, upload, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (held) channel.Release();
            gate.Release();
        }
    }

    private async Task<ForeignPdoObserveResult> ObserveForeignPdoCoreAsync(byte peerNodeId, uint cobId,
        ReadOnlyMemory<byte> payload, CanOpenDeviceDescription peerDescription, IForeignPdoSink sink,
        LiveUpload upload, CancellationToken cancellationToken)
    {
        var matches = await MatchPdosAsync(peerDescription, peerNodeId, cobId, upload, cancellationToken)
            .ConfigureAwait(false);
        if (matches.Count == 0)
        {
            return new ForeignPdoObserveResult(cobId, Array.Empty<ForeignPdoObservation>(),
                "no PDO communication record uses this COB-ID");
        }
        if (AfterPdoMatchForTests is { } pause) await pause().ConfigureAwait(false);

        var observations = new ForeignPdoObservation[matches.Count];
        for (int i = 0; i < matches.Count; i++)
        {
            observations[i] = await DecodeOneAsync(peerNodeId, cobId, payload, peerDescription, matches[i], sink, upload, cancellationToken)
                .ConfigureAwait(false);
        }
        return new ForeignPdoObserveResult(cobId, observations, reason: null);
    }

    private readonly struct DescribedPdo
    {
        public DescribedPdo(ForeignPdoKind kind, int number, ushort mapIndex)
        {
            Kind = kind;
            Number = number;
            MapIndex = mapIndex;
        }

        public ForeignPdoKind Kind { get; }
        public int Number { get; }
        public ushort MapIndex { get; }
    }

    /// <summary>Described TPDOs, then RPDOs, in PDO-number order, whose COB-ID equals <paramref name="cobId"/>. Each
    /// <c>1800h:01</c> / <c>1400h:01</c> is read from the device first. The file is used for a
    /// record only when that upload does not yield a value.</summary>
    private static async Task<List<DescribedPdo>> MatchPdosAsync(CanOpenDeviceDescription description, byte peerNodeId,
        uint cobId, LiveUpload upload, CancellationToken cancellationToken)
    {
        var matches = new List<DescribedPdo>(2);
        int count = Math.Max(4, DescribedPdoCount(description));
        for (int n = 1; n <= count; n++)
        {
            var comm = (ushort)(Co.TpdoComm + n - 1);
            if (await CommRecordMatchesAsync(description, peerNodeId, comm, cobId, upload, cancellationToken).ConfigureAwait(false))
                matches.Add(new DescribedPdo(ForeignPdoKind.Tpdo, n, (ushort)(Co.TpdoMap + n - 1)));
        }
        for (int n = 1; n <= count; n++)
        {
            var comm = (ushort)(Co.RpdoComm + n - 1);
            if (await CommRecordMatchesAsync(description, peerNodeId, comm, cobId, upload, cancellationToken).ConfigureAwait(false))
                matches.Add(new DescribedPdo(ForeignPdoKind.Rpdo, n, (ushort)(Co.RpdoMap + n - 1)));
        }
        return matches;
    }

    /// <summary>True when this communication record's COB-ID is <paramref name="cobId"/>. A live
    /// word that was read decides, including a PDO marked invalid. The file is asked only when
    /// the upload itself fails.</summary>
    private static async Task<bool> CommRecordMatchesAsync(CanOpenDeviceDescription description, byte peerNodeId,
        ushort commIndex, uint cobId, LiveUpload upload, CancellationToken cancellationToken)
    {
        var live = await TryReadLiveCobIdAsync(commIndex, upload, cancellationToken).ConfigureAwait(false);
        if (live.Read) return live.CanId == cobId;
        return DescribedCobId(description, peerNodeId, commIndex) == cobId;
    }

    /// <summary>A live <c>1400h:01</c> / <c>1800h:01</c>. <see cref="Read"/> is false only when the
    /// upload failed. <see cref="CanId"/> is the 11-bit id, or null when the word was read and is
    /// not a usable classic COB-ID (invalid, extended, or not a CAN-ID).</summary>
    private readonly struct LiveCobId
    {
        private LiveCobId(bool read, uint? canId)
        {
            Read = read;
            CanId = canId;
        }

        public bool Read { get; }
        public uint? CanId { get; }

        public static LiveCobId Failed => new(false, null);

        public static LiveCobId FromWord(uint word) => new(true, UsableCanId(word));
    }

    private static async Task<LiveCobId> TryReadLiveCobIdAsync(ushort commIndex, LiveUpload upload, CancellationToken cancellationToken)
    {
        byte[] raw;
        try
        {
            raw = await upload(commIndex, 0x01, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsLiveReadUnavailable(ex))
        {
            return LiveCobId.Failed;
        }
        if (raw.Length < 4) return LiveCobId.Failed;
        return LiveCobId.FromWord(ObjectDictionary.DecodeU32(raw));
    }

    /// <summary>The CAN frame routing key of a described PDO communication record, or null when the file
    /// has no usable COB-ID for it (missing, unreadable, or invalid).</summary>
    private static uint? DescribedCobId(CanOpenDeviceDescription description, byte peerNodeId, ushort commIndex)
    {
        if (!TryDescribedObject(description, commIndex, out var record)) return null;
        if (!TryDescribedValue(record, 0x01, out var text)) return null;
        if (ParseDescribedUnsigned(text, peerNodeId) is not { } word) return null;
        return UsableCanId(word);
    }

    /// <summary>The valid PDO routing key, retaining bit 29 to distinguish extended frames.
    /// Control bits 31 (invalid) and 30 (no RTR) are excluded. Base identifiers follow the
    /// CiA 301 §7.3.5 restrictions.</summary>
    private static uint? UsableCanId(uint word)
    {
        if ((word & CanOpenCobId.InvalidBit) != 0) return null;
        if (!IsUsablePdoCobIdWord(word)) return null;
        uint canId = PdoFrameKey(word);
        return canId;
    }

    private static async Task<ForeignPdoObservation> DecodeOneAsync(byte peerNodeId, uint cobId,
        ReadOnlyMemory<byte> payload, CanOpenDeviceDescription description, DescribedPdo pdo,
        IForeignPdoSink sink, LiveUpload upload, CancellationToken cancellationToken)
    {
        var live = await TryReadLiveMappingAsync(pdo.MapIndex, upload, cancellationToken).ConfigureAwait(false);
        PdoMappingEntry[] mapping;
        ForeignPdoMappingOrigin origin;
        if (live.Entries is { } liveEntries)
        {
            mapping = liveEntries;
            origin = ForeignPdoMappingOrigin.LiveMapping;
        }
        else if (!TryDescribedMapping(description, pdo, peerNodeId, out mapping, out var why))
        {
            return Failed(pdo, "the live mapping record could not be read, and the description's mapping could not be used: " + why);
        }
        else
        {
            origin = ForeignPdoMappingOrigin.DeviceDescription;
        }

        byte mode = live.MpdoMode;
        if (live.Entries is null && TryDescribedObject(description, pdo.MapIndex, out var record)
            && TryDescribedValue(record, 0, out var text) && ParseDescribedUnsigned(text, peerNodeId) is { } count)
            mode = (byte)count;
        if (mode >= 0xFE)
        {
            if (payload.Length != 8) return Failed(pdo, "an MPDO must have eight bytes");
            bool destination = (payload.Span[0] & 0x80) != 0;
            byte node = (byte)(payload.Span[0] & 0x7F);
            if (destination != (mode == (byte)MpdoAddressMode.Destination) || (!destination && node == 0))
                return Failed(pdo, "the MPDO addressing mode does not match its mapping record");
            if (pdo.Kind == ForeignPdoKind.Rpdo && destination && node != 0 && node != peerNodeId)
                return Failed(pdo, "the DAM-MPDO addresses another node");
            if (pdo.Kind == ForeignPdoKind.Tpdo && !destination && node != peerNodeId)
                return Failed(pdo, "the SAM-MPDO names another producer");
            var bytes = payload.Span.Slice(4, 4).ToArray();
            sink.Write(new ForeignPdoSignal(peerNodeId, pdo.Kind, pdo.Number, cobId,
                (ushort)(payload.Span[1] | (payload.Span[2] << 8)), payload.Span[3], bytes, origin));
            return new ForeignPdoObservation(pdo.Kind, pdo.Number, true, origin, 1, null);
        }

        int total = 0;
        foreach (var entry in mapping) total += entry.BitLength;
        total = (total + 7) / 8;
        if (payload.Length < total)
        {
            return Failed(pdo, $"the payload has {payload.Length} byte(s) and the mapping needs {total}");
        }

        int offset = 0;
        int written = 0;
        foreach (var entry in mapping)
        {
            if (!entry.IsDummy)
            {
                var chunk = PdoBits.Extract(payload.Span, offset, entry.BitLength);
                sink.Write(new ForeignPdoSignal(peerNodeId, pdo.Kind, pdo.Number, cobId,
                    entry.Index, entry.Subindex, chunk, origin, entry.BitLength));
                written++;
            }
            offset += entry.BitLength;
        }
        return new ForeignPdoObservation(pdo.Kind, pdo.Number, decoded: true, origin, written, reason: null);
    }

    private static ForeignPdoObservation Failed(DescribedPdo pdo, string reason)
        => new(pdo.Kind, pdo.Number, decoded: false, origin: null, signalsWritten: 0, reason);

    /// <summary>
    /// Uploads sub-index 0 and then each mapped entry, whether or not the description lists them.
    /// Returns null when the live record is unavailable: the upload failed, or the count is not a
    /// mapping of at most 64 bits. An empty array is a live count of 0, which is a
    /// mapping and is not a reason to fall back.
    /// </summary>
    private static async Task<LiveMappingRead> TryReadLiveMappingAsync(ushort mapIndex, LiveUpload upload,
        CancellationToken cancellationToken)
        => (await TryReadLiveMappingCoreAsync(mapIndex, 1, PdoMapping.MaxEntries, upload, cancellationToken)
            .ConfigureAwait(false));

    /// <summary>One upload of a live record, in the peer's SDO channel the observer holds for its
    /// whole snapshot (<see cref="ObserveForeignPdoAsync"/>, <see cref="ObserveForeignSrdoAsync"/>),
    /// or the in-flight refusal when the channel was busy at the start.</summary>
    private delegate Task<byte[]> LiveUpload(ushort index, byte subindex, CancellationToken cancellationToken);

    /// <summary>What <see cref="TryReadLiveMappingCoreAsync"/> found. <see cref="Entries"/> is null
    /// when the live record is unavailable or malformed; <see cref="GateRefusedAfterCount"/> is
    /// set when the count was read and a later slot was refused by the peer-SDO gate, so the
    /// live mapping is known to exist and the caller has not seen all of it. <see cref="Refusal"/>
    /// is set when a live SRDO record was read but breaks the pairing rule: the record exists, so
    /// the file is no fallback for it.</summary>
    private readonly struct LiveMappingRead
    {
        public LiveMappingRead(PdoMappingEntry[]? entries, int declaredEntries, bool gateRefusedAfterCount, string? refusal = null, byte mpdoMode = 0)
        {
            Entries = entries;
            DeclaredEntries = declaredEntries;
            GateRefusedAfterCount = gateRefusedAfterCount;
            Refusal = refusal;
            MpdoMode = mpdoMode;
        }

        public PdoMappingEntry[]? Entries { get; }
        public int DeclaredEntries { get; }
        public bool GateRefusedAfterCount { get; }
        public string? Refusal { get; }
        public byte MpdoMode { get; }
    }

    /// <summary>
    /// Uploads sub-index 0 and then the slots 1, 1+stride, … up to the count, whether or not the
    /// description lists them (a PDO record has stride 1; an SRDO record has stride 2 — its even
    /// slots are the inverted copies). The count must be at most <paramref name="maxCount"/> and,
    /// for stride 2, even. Sub-index 00h must be exactly one byte (UNSIGNED8) and each slot exactly
    /// four (UNSIGNED32), non-zero, the total at most 64 bits (byte-aligned for SRDOs); a value of another
    /// length is not cut, the live mapping counts as not read.
    /// For stride 2 every even slot is uploaded too, right after its odd slot. One that cannot be
    /// read counts as an unreadable odd slot does (the live mapping is not read; the caller falls
    /// back to the file); one that was read must repeat its odd slot
    /// (<see cref="Safety.SrdoRecords.IsInvertedSlotOf"/>, the rule the file path and the device's
    /// own validator apply), else it is a <see cref="LiveMappingRead.Refusal"/>.
    /// </summary>
    private static async Task<LiveMappingRead> TryReadLiveMappingCoreAsync(ushort mapIndex, int stride,
        int maxCount, LiveUpload upload, CancellationToken cancellationToken)
    {
        byte[] countBytes;
        try
        {
            countBytes = await upload(mapIndex, 0x00, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsLiveReadUnavailable(ex))
        {
            return default;
        }
        // Exact widths: sub-index 00h is UNSIGNED8, a slot UNSIGNED32. A value of another length
        // is not cut to its low bytes; the live mapping counts as not read.
        if (countBytes.Length != 1) return default;
        int count = countBytes[0];
        if (stride == 1 && count is 0xFE or 0xFF)
            return new LiveMappingRead(Array.Empty<PdoMappingEntry>(), 0, false, mpdoMode: (byte)count);
        if (count > maxCount || (stride == 2 && (count & 1) != 0)) return default;

        int declared = (count + stride - 1) / stride;
        var entries = new List<PdoMappingEntry>(declared);
        int total = 0;
        for (int s = 1; s <= count; s += stride)
        {
            byte[] raw;
            try
            {
                raw = await upload(mapIndex, (byte)s, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsLiveReadUnavailable(ex))
            {
                return new LiveMappingRead(null, declared, ex is PeerSdoAccessException);
            }
            if (raw.Length != 4) return default;
            uint word = (uint)(raw[0] | (raw[1] << 8) | (raw[2] << 16) | (raw[3] << 24));
            if (word == 0) return default;
            PdoMappingEntry entry;
            try
            {
                entry = new PdoMappingEntry((ushort)(word >> 16), (byte)(word >> 8), (byte)word);
            }
            catch (ArgumentOutOfRangeException)
            {
                return default;
            }
            total += entry.BitLength;
            if (total > 64 || (stride == 2 && entry.BitLength % 8 != 0)) return default;
            entries.Add(entry);
            if (stride == 2)
            {
                byte inv = (byte)(s + 1);
                byte[] invRaw;
                try
                {
                    invRaw = await upload(mapIndex, inv, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsLiveReadUnavailable(ex))
                {
                    // As for an odd slot: the live mapping is not read, the file decides.
                    return new LiveMappingRead(null, declared, ex is PeerSdoAccessException);
                }
                if (invRaw.Length != 4) return default;
                uint inverted = (uint)(invRaw[0] | (invRaw[1] << 8) | (invRaw[2] << 16) | (invRaw[3] << 24));
                if (inverted == 0) return default;
                if (!Safety.SrdoRecords.IsInvertedSlotOf(word, inverted))
                    return new LiveMappingRead(null, declared, false, $"entry {inv} does not repeat entry {s} (CiA DSP 304 §8.4.2.2: the inverted object of the one before it)");
            }
        }
        return new LiveMappingRead(entries.ToArray(), declared, false);
    }

    /// <summary>
    /// An upload that did not yield a value: the server aborted or the transport failed, the
    /// peer-SDO gate refused the pair (<see cref="PeerSdoAccessException"/>), or the client
    /// already has a transfer with that server (<see cref="InvalidOperationException"/>).
    /// Cancellation is not one of these — it propagates.
    /// </summary>
    private static bool IsPeerUnavailableForPdoSnapshot(Exception ex)
        => ex is CanOpenTransportException
           || ex is SdoAbortException { Origin: SdoAbortOrigin.Local, AbortCode: (uint)SdoAbortCode.SdoProtocolTimedOut };

    private static bool IsLiveReadUnavailable(Exception ex)
        => ex is SdoAbortException or CanOpenTransportException or InvalidOperationException;

    private static bool TryDescribedMapping(CanOpenDeviceDescription description, DescribedPdo pdo, byte peerNodeId,
        out PdoMappingEntry[] entries, out string reason)
    {
        if (!TryDescribedObject(description, pdo.MapIndex, out var mapObject))
        {
            entries = Array.Empty<PdoMappingEntry>();
            reason = $"mapping record 0x{pdo.MapIndex:X4} is not in the peer description";
            return false;
        }
        return TryReadDescribedMapping(mapObject, peerNodeId, out entries, out reason);
    }

    private static bool TryReadDescribedMapping(CanOpenObject mapObject, byte peerNodeId,
        out PdoMappingEntry[] entries, out string reason)
    {
        entries = Array.Empty<PdoMappingEntry>();
        if (!TryDescribedValue(mapObject, 0x00, out var countText))
        {
            reason = "the description's mapping record has no sub-index 0";
            return false;
        }
        if (ParseDescribedUnsigned(countText, peerNodeId) is not { } countValue)
        {
            reason = "the description's mapping count could not be read";
            return false;
        }
        if (countValue is 0xFE or 0xFF) { reason = ""; return true; }
        if (countValue > (uint)PdoMapping.MaxEntries)
        {
            reason = "the description's mapping is not a mapping of at most 64 bits";
            return false;
        }
        int count = (int)countValue;
        var list = new PdoMappingEntry[count];
        int total = 0;
        for (byte s = 1; s <= count; s++)
        {
            if (!TryDescribedValue(mapObject, s, out var entryText))
            {
                reason = $"the description's mapping record has no sub-index {s}";
                return false;
            }
            if (ParseDescribedUnsigned(entryText, peerNodeId) is not { } word || word == 0)
            {
                reason = $"mapping entry {s} could not be read";
                return false;
            }
            PdoMappingEntry entry;
            try
            {
                entry = new PdoMappingEntry((ushort)(word >> 16), (byte)(word >> 8), (byte)word);
            }
            catch (ArgumentOutOfRangeException)
            {
                reason = $"mapping entry {s} is not a length of 1..64 bits";
                return false;
            }
            total += entry.BitLength;
            if (total > 64)
            {
                reason = "the description's mapping is longer than 8 bytes";
                return false;
            }
            list[s - 1] = entry;
        }
        entries = list;
        reason = "";
        return true;
    }

    private static bool TryDescribedObject(CanOpenDeviceDescription description, ushort index, out CanOpenObject obj)
    {
        if (description.Objects.Objects.TryGetValue(index, out var found))
        {
            obj = found;
            return true;
        }
        obj = null!;
        return false;
    }

    private static bool TryDescribedValue(CanOpenObject obj, byte subindex, out string? value)
    {
        if (obj.SubObjects.Count == 0)
        {
            if (subindex != 0)
            {
                value = null;
                return false;
            }
            value = Prefer(obj.ParameterValue, obj.DefaultValue);
            return true;
        }
        if (obj.SubObjects.TryGetValue(subindex, out var sub))
        {
            value = Prefer(sub.ParameterValue, sub.DefaultValue);
            return true;
        }
        value = null;
        return false;
    }

    /// <summary>A DCF's <c>ParameterValue</c> overrides the <c>DefaultValue</c> it was commissioned from.</summary>
    private static string? Prefer(string? parameter, string? fallback)
        => string.IsNullOrEmpty(parameter) ? fallback : parameter;

    private static uint? ParseDescribedUnsigned(string? text, byte nodeId)
    {
        if (string.IsNullOrEmpty(text)) return null;
        try
        {
            return Convert.ToUInt32(
                CanOpenValueConverter.Parse(text!, CanOpenDataType.Unsigned32, nodeId),
                CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or NotSupportedException
                                   or ArgumentException or InvalidCastException)
        {
            return null;
        }
    }
}
