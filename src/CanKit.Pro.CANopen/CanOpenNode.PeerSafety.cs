using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Pro.CANopen.Safety;

namespace CanKit.Pro.CANopen;

/// <summary>The tool side of CiA DSP 304 V1.0: configuring a peer's safety parameters with
/// readback and acknowledgement (§9.2, Figure 9) and verifying them (§8.3.1 step D). Every
/// transfer is a client SDO through the peer gate (FR-CO-029).</summary>
internal sealed partial class CanOpenNode
{
    private readonly ConcurrentDictionary<byte, SemaphoreSlim> _peerSafetyGate = new();

    public async Task<PeerSafetyResult> ConfigurePeerSafetyAsync(byte peerNodeId, PeerSafetyConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (configuration is null) throw new ArgumentNullException(nameof(configuration));
        CanOpenCobId.ValidateNodeId(peerNodeId);
        ValidateSafetyTimes(configuration); // before the first frame
        // Without a bound description the peer gate refuses the first transfer anyway.
        if (_peerDescriptions.TryGetValue(peerNodeId, out var description))
            EnsureMappingsFitDescription(configuration, description); // before the first frame
        var gate = _peerSafetyGate.GetOrAdd(peerNodeId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int peerCount = await ReadPeerSrdoCountAsync(peerNodeId, cancellationToken).ConfigureAwait(false);
            EnsureConfigurationFitsPeer(configuration, peerCount);
            var writes = SafetyWrites(configuration, peerCount).ToList();
            // "write all safety-relevant parameter incl. checksums"
            foreach (var (index, sub, value) in writes)
                await SdoDownloadAsync(peerNodeId, index, sub, value, cancellationToken).ConfigureAwait(false);
            // "read all safety-relevant parameter incl. checksums back" — "compared"
            var mismatches = await CompareAsync(peerNodeId, writes, cancellationToken).ConfigureAwait(false);
            if (mismatches.Count > 0) return new PeerSafetyResult(false, mismatches);
            // "configuration acknowledged"
            var valid = new[] { SrdoRecords.ConfigurationValidValue };
            await SdoDownloadAsync(peerNodeId, SrdoRecords.ConfigurationValid, 0x00, valid, cancellationToken).ConfigureAwait(false);
            var back = await SdoUploadAsync(peerNodeId, SrdoRecords.ConfigurationValid, 0x00, cancellationToken).ConfigureAwait(false);
            if (!back.AsSpan().SequenceEqual(valid))
                return new PeerSafetyResult(false, new[] { new PeerSafetyMismatch(SrdoRecords.ConfigurationValid, 0x00, valid, back) });
            return new PeerSafetyResult(true, Array.Empty<PeerSafetyMismatch>());
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<PeerSafetyResult> VerifyPeerSafetyConfigurationAsync(byte peerNodeId, PeerSafetyConfiguration expected,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (expected is null) throw new ArgumentNullException(nameof(expected));
        CanOpenCobId.ValidateNodeId(peerNodeId);
        ValidateSafetyTimes(expected); // before the first frame
        var gate = _peerSafetyGate.GetOrAdd(peerNodeId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int peerCount = await ReadPeerSrdoCountAsync(peerNodeId, cancellationToken).ConfigureAwait(false);
            EnsureConfigurationFitsPeer(expected, peerCount);
            // 1300h is not part of step D's list (§8.3.1 D). A deleted SRDO's checksum is not
            // checked by the device (§9.5, last rule: the checksums of the SRDOs that exist), so a
            // stale one is no mismatch; that SRDO's sub1 = 0 still is compared.
            var expectedValues = SafetyWrites(expected, peerCount)
                .Where(w => w.Index != SrdoRecords.GfcParameter)
                .Where(w => w.Index != SrdoRecords.Checksum || expected.Srdos.ContainsKey(w.Subindex))
                .Append((SrdoRecords.ConfigurationValid, (byte)0x00, new[] { SrdoRecords.ConfigurationValidValue }))
                .ToList();
            var mismatches = await CompareAsync(peerNodeId, expectedValues, cancellationToken).ConfigureAwait(false);
            return new PeerSafetyResult(mismatches.Count == 0, mismatches);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<int> ReadPeerSrdoCountAsync(byte peerNodeId, CancellationToken cancellationToken)
    {
        var count = await SdoUploadAsync(peerNodeId, SrdoRecords.Checksum, 0x00, cancellationToken).ConfigureAwait(false);
        if (count.Length < 1) throw new InvalidOperationException("The peer returned no SRDO count from 13FFh:00.");
        // Not clamped: SRDOs the peer claims beyond 64 could never be configured or verified,
        // and a configuration acknowledged over 1..64 would leave them unchecked.
        if (count[0] > SrdoRecords.MaxSrdoCount)
            throw new InvalidOperationException(
                $"The peer reports {count[0]} SRDOs in 13FFh:00; CiA DSP 304 §8.4.2.2 allows at most {SrdoRecords.MaxSrdoCount}.");
        return count[0];
    }

    /// <summary>§9.2 deletes every SRDO of the peer before any mapping is written, so a mapping
    /// the peer refuses leaves it without its previous configuration. What the peer's bound
    /// EDS/DCF shows (CiA 306) is therefore checked before the first frame: each mapped object is
    /// declared, flagged mappable (<c>PDOMapping</c>; absent means not mappable), as wide as its
    /// declared data type when that type has a fixed width, and accessible in the SRDO's direction
    /// (a producer reads it, a consumer writes it — the file's <c>AccessType</c>). What the file
    /// does not show — a rule of the peer's own — stays the peer's to refuse.</summary>
    private static void EnsureMappingsFitDescription(PeerSafetyConfiguration configuration, CanOpenDeviceDescription description)
    {
        foreach (var srdo in configuration.Srdos)
        {
            foreach (var entry in srdo.Value.Mapping.Entries)
            {
                if (MappingEntryRefusal(description, entry, srdo.Value.Parameter.Direction) is { } reason)
                    throw new ArgumentException(
                        $"SRDO {srdo.Key}: the mapping entry {entry.Index:X4}h:{entry.Subindex:X2}h ({entry.BitLength} bits) {reason} in the peer's bound description; nothing was sent (CiA DSP 304 §9.2 deletes every SRDO first).",
                        "configuration");
            }
        }
    }

    private static string? MappingEntryRefusal(CanOpenDeviceDescription description, Pdo.PdoMappingEntry entry, SrdoDirection direction)
    {
        if (!TryDescribedObject(description, entry.Index, out var obj)) return "is not declared";
        DescribedEntry? found = null;
        foreach (var candidate in EntriesOf(obj))
        {
            if (candidate.Subindex == entry.Subindex) { found = candidate; break; }
        }
        if (found is not { } described) return "is not declared";
        if (!described.PdoMappable) return "is not flagged mappable (PDOMapping)";
        if (MapType(described.DataType) is { } type && OdEntryLayout.FixedSize(type) is var size && size > 0 && entry.ByteLength != size)
            return $"is not as wide as its declared {type} ({size * 8} bits)";
        var access = MapAccess(described.Access);
        if (direction == SrdoDirection.Transmit && (access & OdAccess.ReadOnly) == 0) return "is not readable, which a producer needs";
        if (direction == SrdoDirection.Receive && (access & OdAccess.WriteOnly) == 0) return "is not writable, which a consumer needs";
        return null;
    }

    /// <summary>A record above the peer's count does not exist on the peer: writing the others and
    /// reporting success would leave that SRDO missing, so nothing is sent.</summary>
    private static void EnsureConfigurationFitsPeer(PeerSafetyConfiguration configuration, int peerSrdoCount)
    {
        foreach (var number in configuration.Srdos.Keys)
            if (number > peerSrdoCount)
                throw new ArgumentException(
                    $"SRDO {number} is configured, but the peer reports only {peerSrdoCount} SRDO(s) in 13FFh:00.", nameof(configuration));
    }

    /// <summary>The cycle time (sub2) is 1..65535 ms and the validation time (sub3) 1..255 ms; a hand-built configuration is not range-checked anywhere else, and the casts
    /// below would wrap silently.</summary>
    private static void ValidateSafetyTimes(PeerSafetyConfiguration configuration)
    {
        // PeerSafetyConfiguration.Add refuses these already; kept as the check before the first frame.
        foreach (var pair in configuration.Srdos)
            PeerSafetyConfiguration.ValidateTimes(pair.Key, pair.Value.Parameter, "configuration");
    }

    private static ushort CheckMilliseconds(int srdoNumber, string what, TimeSpan time, int min, int max)
        => PeerSafetyConfiguration.CheckMilliseconds(srdoNumber, what, time, min, max, "configuration");

    /// <summary>The writes of §9.2 in order: a first pass that deletes every SRDO; then per SRDO
    /// the deletion, the mapping (disabled, slots, count), the times, the ids, the creation (SRDOs
    /// the configuration does not name stay deleted); then 1300h; then every checksum. The same
    /// list is what the readback compares. The mapping is sub-index 0 and the 2k slots of its k
    /// objects: a slot above the count is no part of the SRDO (§8.4.2.3) — the device neither
    /// uses nor checksums it (§8.4.2.2 field g) — and a peer may not implement it at all.</summary>
    private static IEnumerable<(ushort Index, byte Subindex, byte[] Value)> SafetyWrites(PeerSafetyConfiguration configuration, int peerSrdoCount)
    {
        ValidateSafetyTimes(configuration);
        return SafetyWritesCore(configuration, peerSrdoCount);
    }

    private static IEnumerable<(ushort Index, byte Subindex, byte[] Value)> SafetyWritesCore(PeerSafetyConfiguration configuration, int peerSrdoCount)
    {
        // Delete every SRDO first: the device refuses a COB-ID that another existing SRDO still
        // holds, so a configuration that moves ids between SRDOs needs all of them gone before any is written.
        for (int n = 1; n <= peerSrdoCount; n++)
            yield return (SrdoRecords.CommIndex(n), 0x01, new byte[] { 0 });
        for (int n = 1; n <= peerSrdoCount; n++)
        {
            var comm = SrdoRecords.CommIndex(n);
            var map = SrdoRecords.MapIndex(n);
            if (!configuration.Srdos.TryGetValue(n, out var srdo))
            {
                yield return (comm, 0x01, new byte[] { 0 });
                continue;
            }
            var (p, mapping) = srdo;
            var entries = mapping.ToArray();
            yield return (comm, 0x01, new byte[] { 0 });
            yield return (map, 0x00, new byte[] { 0 });
            for (byte s = 1; s <= 2 * entries.Length; s++)
                yield return (map, s, ObjectDictionary.EncodeU32(EncodeMappingEntry(entries[(s - 1) / 2])));
            yield return (map, 0x00, new[] { (byte)(2 * entries.Length) });
            ushort cycle = CheckMilliseconds(n, "cycle time", p.RefreshOrSafeguardCycleTime, 1, ushort.MaxValue);
            yield return (comm, 0x02, new[] { (byte)cycle, (byte)(cycle >> 8) });
            yield return (comm, 0x03, new[] { (byte)CheckMilliseconds(n, "validation time", p.ValidationTime, 1, byte.MaxValue) });
            yield return (comm, 0x05, ObjectDictionary.EncodeU32(p.CobId1));
            yield return (comm, 0x06, ObjectDictionary.EncodeU32(p.CobId2));
            yield return (comm, 0x01, new[] { (byte)p.Direction });
        }
        yield return (SrdoRecords.GfcParameter, 0x00, new[] { configuration.GlobalFailsafeCommandEnabled ? (byte)1 : (byte)0 });
        for (int n = 1; n <= peerSrdoCount; n++)
        {
            ushort crc = configuration.Srdos.TryGetValue(n, out var srdo) ? SrdoCrc.Compute(srdo.Parameter, srdo.Mapping) : (ushort)0;
            yield return (SrdoRecords.Checksum, (byte)n, new[] { (byte)crc, (byte)(crc >> 8) });
        }
    }

    /// <summary>Uploads each pair once (the last value written to a pair is the expectation) and
    /// lists every difference.</summary>
    private async Task<List<PeerSafetyMismatch>> CompareAsync(byte peerNodeId,
        IReadOnlyList<(ushort Index, byte Subindex, byte[] Value)> expected, CancellationToken cancellationToken)
    {
        var final = new Dictionary<(ushort, byte), byte[]>();
        var order = new List<(ushort, byte)>();
        foreach (var (index, sub, value) in expected)
        {
            if (!final.ContainsKey((index, sub))) order.Add((index, sub));
            final[(index, sub)] = value;
        }
        var mismatches = new List<PeerSafetyMismatch>();
        foreach (var (index, sub) in order)
        {
            var actual = await SdoUploadAsync(peerNodeId, index, sub, cancellationToken).ConfigureAwait(false);
            if (!actual.AsSpan().SequenceEqual(final[(index, sub)]))
                mismatches.Add(new PeerSafetyMismatch(index, sub, final[(index, sub)], actual));
        }
        return mismatches;
    }
}
