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
/// The file is used when that upload aborts, times out, is refused by the peer-SDO gate, finds
/// another SDO already in flight for the server, or does not come back as a usable value.
/// Observations of one peer run one at a time, so two of them do not trip that in-flight limit
/// on each other. A CAN-ID CiA 301 §7.3.5 restricts is not a match.
/// </remarks>
internal sealed partial class CanOpenNode
{
    /// <summary>One observation of a peer at a time. The SDO client allows a single transfer per
    /// server; queueing here lets a second <see cref="ObserveForeignPdoAsync"/> wait instead of
    /// failing with <see cref="InvalidOperationException"/>.</summary>
    private readonly ConcurrentDictionary<byte, SemaphoreSlim> _foreignPdoObserve = new();

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
        if (cobId > CanOpenCobId.CanIdMask)
            throw new ArgumentOutOfRangeException(nameof(cobId), cobId,
                "A foreign PDO is observed on an 11-bit COB-ID.");
        if (payload.Length > 8)
            throw new ArgumentOutOfRangeException(nameof(payload), payload.Length,
                "A classic CAN PDO payload is at most 8 bytes.");

        var gate = _foreignPdoObserve.GetOrAdd(peerNodeId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ObserveForeignPdoCoreAsync(peerNodeId, cobId, payload, peerDescription, sink, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ForeignPdoObserveResult> ObserveForeignPdoCoreAsync(byte peerNodeId, uint cobId,
        ReadOnlyMemory<byte> payload, CanOpenDeviceDescription peerDescription, IForeignPdoSink sink,
        CancellationToken cancellationToken)
    {
        var matches = await MatchPdosAsync(peerDescription, peerNodeId, cobId, cancellationToken)
            .ConfigureAwait(false);
        if (matches.Count == 0)
        {
            return new ForeignPdoObserveResult(cobId, Array.Empty<ForeignPdoObservation>(),
                "no PDO communication record uses this COB-ID");
        }

        var observations = new ForeignPdoObservation[matches.Count];
        for (int i = 0; i < matches.Count; i++)
        {
            observations[i] = await DecodeOneAsync(peerNodeId, cobId, payload, peerDescription, matches[i], sink, cancellationToken)
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

    /// <summary>TPDO 1..4, then RPDO 1..4, whose COB-ID equals <paramref name="cobId"/>. Each
    /// <c>1800h:01</c> / <c>1400h:01</c> is read from the device first. The file is used for a
    /// record only when that upload does not yield a value.</summary>
    private async Task<List<DescribedPdo>> MatchPdosAsync(CanOpenDeviceDescription description, byte peerNodeId,
        uint cobId, CancellationToken cancellationToken)
    {
        var matches = new List<DescribedPdo>(2);
        for (int n = 1; n <= Co.PdoCount; n++)
        {
            var comm = (ushort)(Co.TpdoComm + n - 1);
            if (await CommRecordMatchesAsync(description, peerNodeId, comm, cobId, cancellationToken).ConfigureAwait(false))
                matches.Add(new DescribedPdo(ForeignPdoKind.Tpdo, n, (ushort)(Co.TpdoMap + n - 1)));
        }
        for (int n = 1; n <= Co.PdoCount; n++)
        {
            var comm = (ushort)(Co.RpdoComm + n - 1);
            if (await CommRecordMatchesAsync(description, peerNodeId, comm, cobId, cancellationToken).ConfigureAwait(false))
                matches.Add(new DescribedPdo(ForeignPdoKind.Rpdo, n, (ushort)(Co.RpdoMap + n - 1)));
        }
        return matches;
    }

    /// <summary>True when this communication record's COB-ID is <paramref name="cobId"/>. A live
    /// word that was read decides, including a PDO marked invalid. The file is asked only when
    /// the upload itself fails.</summary>
    private async Task<bool> CommRecordMatchesAsync(CanOpenDeviceDescription description, byte peerNodeId,
        ushort commIndex, uint cobId, CancellationToken cancellationToken)
    {
        var live = await TryReadLiveCobIdAsync(peerNodeId, commIndex, cancellationToken).ConfigureAwait(false);
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

    private async Task<LiveCobId> TryReadLiveCobIdAsync(byte peerNodeId, ushort commIndex, CancellationToken cancellationToken)
    {
        byte[] raw;
        try
        {
            raw = await SdoUploadAsync(peerNodeId, commIndex, 0x01, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsLiveReadUnavailable(ex))
        {
            return LiveCobId.Failed;
        }
        if (raw.Length < 4) return LiveCobId.Failed;
        return LiveCobId.FromWord(ObjectDictionary.DecodeU32(raw));
    }

    /// <summary>The 11-bit CAN-ID of a described PDO communication record, or null when the file
    /// has no usable COB-ID for it (missing, unreadable, invalid, or a 29-bit frame).</summary>
    private static uint? DescribedCobId(CanOpenDeviceDescription description, byte peerNodeId, ushort commIndex)
    {
        if (!TryDescribedObject(description, commIndex, out var record)) return null;
        if (!TryDescribedValue(record, 0x01, out var text)) return null;
        if (ParseDescribedUnsigned(text, peerNodeId) is not { } word) return null;
        return UsableCanId(word);
    }

    /// <summary>Bits 0–10 when the word is a classic CAN-ID with bit 31 clear and the id is one
    /// a PDO may use. Bit 30 (no RTR) is ignored. Bit 31, bit 29, any other bit above the 11-bit
    /// id, or a CAN-ID CiA 301 §7.3.5 restricts (<see cref="CanOpenCobId.IsRestricted"/>) is not
    /// a match.</summary>
    private static uint? UsableCanId(uint word)
    {
        if ((word & CanOpenCobId.InvalidBit) != 0) return null;
        if ((word & CanOpenCobId.ExtendedFrameBit) != 0) return null;
        const uint controlBits = CanOpenCobId.InvalidBit | CanOpenCobId.NoRtrBit | CanOpenCobId.ExtendedFrameBit;
        if ((word & ~controlBits & ~CanOpenCobId.CanIdMask) != 0) return null;
        uint canId = word & CanOpenCobId.CanIdMask;
        if (CanOpenCobId.IsRestricted(canId)) return null;
        return canId;
    }

    private async Task<ForeignPdoObservation> DecodeOneAsync(byte peerNodeId, uint cobId,
        ReadOnlyMemory<byte> payload, CanOpenDeviceDescription description, DescribedPdo pdo,
        IForeignPdoSink sink, CancellationToken cancellationToken)
    {
        var live = await TryReadLiveMappingAsync(peerNodeId, pdo.MapIndex, cancellationToken).ConfigureAwait(false);
        PdoMappingEntry[] mapping;
        ForeignPdoMappingOrigin origin;
        if (live is { } liveEntries)
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

        int total = 0;
        foreach (var entry in mapping) total += entry.ByteLength;
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
                var chunk = new byte[entry.ByteLength];
                payload.Span.Slice(offset, entry.ByteLength).CopyTo(chunk);
                sink.Write(new ForeignPdoSignal(peerNodeId, pdo.Kind, pdo.Number, cobId,
                    entry.Index, entry.Subindex, chunk, origin));
                written++;
            }
            offset += entry.ByteLength;
        }
        return new ForeignPdoObservation(pdo.Kind, pdo.Number, decoded: true, origin, written, reason: null);
    }

    private static ForeignPdoObservation Failed(DescribedPdo pdo, string reason)
        => new(pdo.Kind, pdo.Number, decoded: false, origin: null, signalsWritten: 0, reason);

    /// <summary>
    /// Uploads sub-index 0 and then each mapped entry, whether or not the description lists them.
    /// Returns null when the live record is unavailable: the upload failed, or the count is not a
    /// byte-aligned mapping of at most 8 entries. An empty array is a live count of 0, which is a
    /// mapping and is not a reason to fall back.
    /// </summary>
    private async Task<PdoMappingEntry[]?> TryReadLiveMappingAsync(byte peerNodeId, ushort mapIndex,
        CancellationToken cancellationToken)
    {
        byte[] countBytes;
        try
        {
            countBytes = await SdoUploadAsync(peerNodeId, mapIndex, 0x00, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsLiveReadUnavailable(ex))
        {
            return null;
        }
        if (countBytes.Length < 1) return null;
        int count = countBytes[0];
        if (count > PdoMapping.MaxEntries) return null;

        var entries = new PdoMappingEntry[count];
        int total = 0;
        for (byte s = 1; s <= count; s++)
        {
            byte[] raw;
            try
            {
                raw = await SdoUploadAsync(peerNodeId, mapIndex, s, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsLiveReadUnavailable(ex))
            {
                return null;
            }
            if (raw.Length < 4) return null;
            uint word = (uint)(raw[0] | (raw[1] << 8) | (raw[2] << 16) | (raw[3] << 24));
            if (word == 0) return null;
            PdoMappingEntry entry;
            try
            {
                entry = new PdoMappingEntry((ushort)(word >> 16), (byte)(word >> 8), (byte)word);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
            total += entry.ByteLength;
            if (total > 8) return null;
            entries[s - 1] = entry;
        }
        return entries;
    }

    /// <summary>
    /// An upload that did not yield a value: the server aborted or the transport failed, the
    /// peer-SDO gate refused the pair (<see cref="PeerSdoAccessException"/>), or the client
    /// already has a transfer with that server (<see cref="InvalidOperationException"/>).
    /// Cancellation is not one of these — it propagates.
    /// </summary>
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
        if (countValue > (uint)PdoMapping.MaxEntries)
        {
            reason = "the description's mapping is not a byte-aligned mapping of at most 8 entries";
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
                reason = $"mapping entry {s} is not a byte-aligned length";
                return false;
            }
            total += entry.ByteLength;
            if (total > 8)
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
