using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Pro.CANopen.Safety;
using CanKit.Pro.CANopen.Sdo;

namespace CanKit.Pro.CANopen;

/// <summary>The tool side of CiA DSP 304 V1.0 — configuring a peer's safety parameters with
/// readback and acknowledgement (§9.2, Figure 9) and verifying them (§8.3.1 step D) — run as
/// the configuration entity of CiA 319 v2.0.0 clause 5: the peer's identity is verified first
/// (§5.2), the writes, the readback and the acknowledgement are steps 1–3 of §5.3, step 5 stores
/// the configuration when asked, and the SRVT of a tx SRDO the peer does not implement is
/// checksummed at its default (§6.2). Every transfer is a client SDO through the peer gate (FR-CO-029).</summary>
internal sealed partial class CanOpenNode
{
    /// <summary>Test seam: awaited between the readback and the 13FEh = A5h write of a
    /// configuration — the gap in which another write would stale the acknowledged checksum.</summary>
    internal Func<Task>? AfterSafetyReadbackForTests { get; set; }

    public async Task<PeerSafetyResult> ConfigurePeerSafetyAsync(byte peerNodeId, PeerSafetyConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (configuration is null) throw new ArgumentNullException(nameof(configuration));
        var requested = configuration;
        CanOpenCobId.ValidateNodeId(peerNodeId);
        ValidateSafetyTimes(configuration); // before the first frame
        // The peer's SDO channel, for the whole transaction: waited for (a call in flight ends first),
        // then held, so another SDO call of this node to the peer is refused as in flight until the
        // transaction ends. Every transfer below is a ...CoreAsync one.
        var gate = PeerSdoChannel(peerNodeId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The transaction starts when it owns the channel: one description for its whole length,
            // the one bound now, which the pre-checks use and every transfer's gate check below. A
            // rebind or an edit meanwhile takes effect for the next call. A refusal here releases
            // the channel. The configuration likewise: a copy taken now is what is checked, written
            // and compared, so a change to the caller's instance meanwhile is not part of this
            // transaction.
            configuration = requested.Snapshot();
            var binding = CapturePeerDescription(peerNodeId, configuration);
            // Without a bound description there is no identity to compare the first frame with,
            // and the gate would refuse every safety transfer after it: refused before any frame.
            if (!binding.IsBound) throw NoDescriptionForSafety(peerNodeId);
            EnsureIdentityFitsDescription(peerNodeId, binding); // before the first frame, as the three below
            EnsureMappingsFitDescription(configuration, binding);
            EnsureValidationTimesFitDescription(configuration, binding, nameof(configuration));
            EnsureStorageFitsDescription(peerNodeId, configuration, binding);
            // CiA 319 §5.2: "Prior to the configuration of an SRD, the configuration entity shall
            // perform a verification of the identity of the SRD" — the first frames of the transaction.
            var identity = await VerifyPeerIdentityAsync(peerNodeId, binding, cancellationToken).ConfigureAwait(false);
            if (identity.Count > 0) return new PeerSafetyResult(false, identity);
            int peerCount = await ReadPeerSrdoCountAsync(peerNodeId, binding, cancellationToken).ConfigureAwait(false);
            EnsureConfigurationFitsPeer(configuration, peerCount);
            var writes = SafetyWrites(configuration, peerCount, binding).ToList();
            // §9.2 "write all safety-relevant parameter incl. checksums"; CiA 319 §5.3 step 1: the
            // SRDO configuration data, then the checksums to 13FFh.
            foreach (var (index, sub, value, optional) in writes)
            {
                try
                {
                    await SdoDownloadCoreAsync(peerNodeId, index, sub, value, SdoTransferMode.Auto, cancellationToken, transaction: binding).ConfigureAwait(false);
                }
                catch (SdoAbortException ex) when (optional && IsNotImplemented(ex))
                {
                    // The SRVT of a tx SRDO the peer does not implement (CiA 319 §6.2): it
                    // checksums the default, which is what was being written.
                }
            }
            // §9.2 "read all safety-relevant parameter incl. checksums back" — "compared"; step 2.
            var mismatches = await CompareAsync(peerNodeId, writes, binding, cancellationToken).ConfigureAwait(false);
            if (mismatches.Count > 0) return new PeerSafetyResult(false, mismatches);
            if (AfterSafetyReadbackForTests is { } pause) await pause().ConfigureAwait(false);
            // §9.2 "configuration acknowledged"; step 3. Step 4 is the SRD's own check of the
            // checksums: an SRD that found one wrong has set 13FEh back to 0, which the readback
            // shows.
            var valid = new[] { SrdoRecords.ConfigurationValidValue };
            await SdoDownloadCoreAsync(peerNodeId, SrdoRecords.ConfigurationValid, 0x00, valid, SdoTransferMode.Auto, cancellationToken, transaction: binding).ConfigureAwait(false);
            var back = await SdoUploadCoreAsync(peerNodeId, SrdoRecords.ConfigurationValid, 0x00, SdoTransferMode.Auto, cancellationToken, transaction: binding).ConfigureAwait(false);
            if (!back.AsSpan().SequenceEqual(valid))
                return new PeerSafetyResult(false, new[] { new PeerSafetyMismatch(SrdoRecords.ConfigurationValid, 0x00, valid, back) });
            // Step 5: "the configuration entity can request a SDO download service to object 1010h
            // ... with the signature 65 76 61 73h" — after the acknowledgement, so that what is
            // stored includes 13FEh = A5h.
            bool stored = false;
            if (configuration.Storage != PeerSafetyStorage.None)
            {
                await SdoDownloadCoreAsync(peerNodeId, Co.StoreParameters, (byte)configuration.Storage, (byte[])SaveSignature.Clone(),
                    SdoTransferMode.Auto, cancellationToken, transaction: binding).ConfigureAwait(false);
                stored = true;
            }
            return new PeerSafetyResult(true, Array.Empty<PeerSafetyMismatch>(), stored);
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
        var requested = expected;
        CanOpenCobId.ValidateNodeId(peerNodeId);
        ValidateSafetyTimes(expected); // before the first frame
        // The expectation as it was when the transaction started: a copy taken once it owns the channel.
        return (await VerifyPeerSafetyCoreAsync(peerNodeId, _ => requested.Snapshot(), cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>The transaction of <see cref="VerifyPeerSafetyConfigurationAsync"/>, with the
    /// expectation taken from <paramref name="expectationOf"/> once the transaction owns the peer's
    /// SDO channel — called with one reading of the description bound at that moment, the same
    /// reading the gate of every transfer is built from, so an expectation derived from the
    /// description (step D in the boot-up) and the gate cannot see different contents. Null from
    /// <paramref name="expectationOf"/> means there is nothing to verify, and the result is null:
    /// nothing was sent.</summary>
    private async Task<PeerSafetyResult?> VerifyPeerSafetyCoreAsync(byte peerNodeId,
        Func<DescribedObjects?, PeerSafetyConfiguration?> expectationOf, CancellationToken cancellationToken)
    {
        // The peer's SDO channel, for the whole transaction: waited for (a call in flight ends first),
        // then held, so another SDO call of this node to the peer is refused as in flight until the
        // transaction ends. Every transfer below is a ...CoreAsync one.
        var gate = PeerSdoChannel(peerNodeId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var description = CaptureBoundPeerDescription(peerNodeId); // one reading, from the moment it owns the channel
            var expected = expectationOf(description);
            if (expected is null) return null;
            ValidateSafetyTimes(expected); // before the first frame
            var binding = new PeerDescriptionBinding(description, Array.Empty<(ushort, byte)>());
            EnsureValidationTimesFitDescription(expected, binding, "expected"); // before the first frame
            int peerCount = await ReadPeerSrdoCountAsync(peerNodeId, binding, cancellationToken).ConfigureAwait(false);
            EnsureConfigurationFitsPeer(expected, peerCount);
            // 1300h is not part of step D's list (§8.3.1 D). A deleted SRDO's checksum is not
            // checked by the device (§9.5, last rule: the checksums of the SRDOs that exist), so a
            // stale one is no mismatch; that SRDO's sub1 = 0 still is compared.
            var expectedValues = SafetyWrites(expected, peerCount, binding)
                .Where(w => w.Index != SrdoRecords.GfcParameter)
                .Where(w => w.Index != SrdoRecords.Checksum || expected.Srdos.ContainsKey(w.Subindex))
                .Append((SrdoRecords.ConfigurationValid, (byte)0x00, new[] { SrdoRecords.ConfigurationValidValue }, false))
                .ToList();
            var mismatches = await CompareAsync(peerNodeId, expectedValues, binding, cancellationToken).ConfigureAwait(false);
            return new PeerSafetyResult(mismatches.Count == 0, mismatches);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>CiA 319 §5.2: 1018h:01 (vendor-ID), :02 (product code) and :03 (revision number)
    /// are uploaded and compared with the bound description's <c>[DeviceInfo]</c>
    /// (<c>VendorNumber</c>, <c>ProductNumber</c>, <c>RevisionNumber</c>, CiA 306). All three are
    /// read; each that differs is a mismatch with the four bytes expected and the bytes the peer
    /// returned, whatever their number. §5.2 requires all three of an SRD, where CiA 301
    /// §7.5.2.21 makes :02 and :03 optional: a peer that aborts one of them is not an SRD this
    /// procedure can configure, and the abort propagates as every abort does. The further
    /// verifications the note of §5.2 names — firmware, hardware, serial number, location — are
    /// the application's.</summary>
    private async Task<List<PeerSafetyMismatch>> VerifyPeerIdentityAsync(byte peerNodeId, PeerDescriptionBinding binding, CancellationToken cancellationToken)
    {
        var (vendorId, productCode, revisionNumber) = binding.Identity;
        var mismatches = new List<PeerSafetyMismatch>();
        foreach (var (subindex, value) in new (byte Subindex, uint Value)[] { (0x01, vendorId), (0x02, productCode), (0x03, revisionNumber) })
        {
            var expected = ObjectDictionary.EncodeU32(value);
            var actual = await SdoUploadCoreAsync(peerNodeId, Co.Identity, subindex, SdoTransferMode.Auto, cancellationToken, transaction: binding).ConfigureAwait(false);
            if (!actual.AsSpan().SequenceEqual(expected))
                mismatches.Add(new PeerSafetyMismatch(Co.Identity, subindex, expected, actual));
        }
        return mismatches;
    }

    /// <summary>The refusal of a configuration with no description bound for the peer, before
    /// any frame: its first transfer would be 1018h:01, which the gate lets through without a
    /// description — but the transaction has nothing to compare it with, and every safety object
    /// after it would be refused.</summary>
    private static PeerSdoAccessException NoDescriptionForSafety(byte peerNodeId)
        => new(peerNodeId, Co.Identity, 0x01, peerDescriptionLoaded: false,
            $"A safety configuration of node 0x{peerNodeId:X2} needs a peer EDS or DCF bound with BindPeerDeviceDescription: " +
            "its first transfer, 1018h:01, is compared with the file's identity (CiA 319 §5.2), and every safety object after it " +
            "passes the peer-SDO gate only with one. Nothing was sent.");

    /// <summary>What CiA 319 §5.2 needs of the bound file, checked before the first frame: it
    /// declares 1018h:01–03 — the gate would otherwise refuse the second or third entry a frame
    /// or two in — and it carries an identity. EdsDcfNet reads an absent <c>VendorNumber</c>,
    /// <c>ProductNumber</c> or <c>RevisionNumber</c> as 0 without a diagnostic, so a file whose
    /// three are all 0 describes no device, and a comparison against it would prove nothing or
    /// blame the peer for the file.</summary>
    private static void EnsureIdentityFitsDescription(byte peerNodeId, PeerDescriptionBinding binding)
    {
        for (byte subindex = 0x01; subindex <= 0x03; subindex++)
        {
            if (!binding.Declares(Co.Identity, subindex))
                throw PeerSdoAccessException.NotInDescription(peerNodeId, Co.Identity, subindex);
        }
        var (vendorId, productCode, revisionNumber) = binding.Identity;
        if (vendorId == 0 && productCode == 0 && revisionNumber == 0)
            throw new InvalidOperationException(
                $"The description bound for node 0x{peerNodeId:X2} carries no identity: its [DeviceInfo] VendorNumber, ProductNumber and RevisionNumber are all 0 " +
                "(an absent key reads as 0), so the peer's 1018h:01-03 cannot be verified against it (CiA 319 §5.2). Nothing was sent.");
    }

    private async Task<int> ReadPeerSrdoCountAsync(byte peerNodeId, PeerDescriptionBinding binding, CancellationToken cancellationToken)
    {
        var count = await SdoUploadCoreAsync(peerNodeId, SrdoRecords.Checksum, 0x00, SdoTransferMode.Auto, cancellationToken, transaction: binding).ConfigureAwait(false);
        // UNSIGNED8 (§8.4.2.2): exactly one byte. A longer response is not cut to its low byte.
        if (count.Length != 1)
            throw new InvalidOperationException(
                $"The peer returned {count.Length} byte(s) for the SRDO count 13FFh:00, which is UNSIGNED8 — one byte (CiA DSP 304 §8.4.2.2); nothing further was sent.");
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
    /// declared, flagged mappable (<c>PDOMapping</c>; absent means not mappable), of a declared
    /// data type with a fixed width — a string, a domain, a missing <c>DataType</c> or a type code
    /// this implementation does not know is refused — and as wide as that type, and accessible in
    /// the SRDO's direction
    /// (a producer reads it, a consumer writes it — the file's <c>AccessType</c>). What the file
    /// does not show — a rule of the peer's own — stays the peer's to refuse.</summary>
    private static void EnsureMappingsFitDescription(PeerSafetyConfiguration configuration, PeerDescriptionBinding description)
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

    private static string? MappingEntryRefusal(PeerDescriptionBinding description, Pdo.PdoMappingEntry entry, SrdoDirection direction)
    {
        if (!description.TryGetMappedEntry(entry.Index, entry.Subindex, out var described)) return "is not declared";
        if (!described.PdoMappable) return "is not flagged mappable (PDOMapping)";
        // An SRDO maps objects of fixed width only (this implementation's rule, as in the
        // dictionary's validator): a string, a domain or a type the file does not name is refused.
        if (MapType(described.DataType) is not { } type || !OdEntryLayout.HasFixedSize(type))
            return $"has no fixed width (declared {(MapType(described.DataType) is { } t ? t.ToString() : "with no data type this implementation knows")}), and an SRDO maps fixed-width objects only";
        int size = OdEntryLayout.FixedSize(type);
        if (entry.ByteLength != size)
            return $"is not as wide as its declared {type} ({size * 8} bits)";
        var access = MapAccess(described.Access);
        if (direction == SrdoDirection.Transmit && (access & OdAccess.ReadOnly) == 0) return "is not readable, which a producer needs";
        if (direction == SrdoDirection.Receive && (access & OdAccess.WriteOnly) == 0) return "is not writable, which a consumer needs";
        return null;
    }

    /// <summary>CiA 319 §6.2 on the tool: "SRVT for SRDOs with Tx transmission direction – For
    /// the calculation the default value of 20d shall be used" when the parameter is not
    /// implemented. A tx SRDO whose communication record the bound description declares without
    /// sub-index 3 (<see cref="ValidationTimeIsOptional"/>) is one the peer is taken not to hold
    /// an SRVT for, and such a peer checksums 20 ms. A configuration or expectation that says
    /// another value there is refused before the first frame: whether the peer holds the
    /// sub-index is known only once the write is answered, and a checksum over another value
    /// would not be the peer's if it does not — so the parameter that reaches the write and the
    /// checksum says 20 ms, and nothing substitutes it.</summary>
    private static void EnsureValidationTimesFitDescription(PeerSafetyConfiguration configuration, PeerDescriptionBinding binding, string paramName)
    {
        foreach (var srdo in configuration.Srdos)
        {
            var parameter = srdo.Value.Parameter;
            if (!ValidationTimeIsOptional(binding, srdo.Key, parameter.Direction)) continue;
            ushort srvt = PeerSafetyConfiguration.CheckMilliseconds(srdo.Key, "validation time", parameter.ValidationTime, 1, byte.MaxValue, paramName);
            if (srvt == SrdoRecords.DefaultValidationTimeMs) continue;
            throw new ArgumentException(
                $"SRDO {srdo.Key}: the peer's bound description declares record {SrdoRecords.CommIndex(srdo.Key):X4}h without sub-index 03h, so the peer is taken not to implement the SRVT of this tx SRDO and to checksum it at the default of {SrdoRecords.DefaultValidationTimeMs} ms (CiA 319 §6.2); an SRVT of {srvt} ms would not be its checksum. Nothing was sent.",
                paramName);
        }
    }

    /// <summary>Whether sub-index 3 of SRDO <paramref name="srdoNumber"/> may be one the peer does
    /// not hold: for a producer whose record the bound description declares without it (CiA 319
    /// §6.2, <see cref="PeerDescriptionBinding.DeclaresValidationTime"/>). Never for a consumer,
    /// whose SRVT is not optional, and never without a description. The file is not trusted
    /// alone: the sub-index is written and read back anyway, and the peer's answer decides —
    /// 0609 0011h means it does not implement it (<see cref="IsNotImplemented"/>), anything
    /// else that it does, with the 20 ms it was given.</summary>
    private static bool ValidationTimeIsOptional(PeerDescriptionBinding binding, int srdoNumber, SrdoDirection direction)
        => direction == SrdoDirection.Transmit && binding.IsBound && !binding.DeclaresValidationTime(srdoNumber);

    /// <summary>"Sub-index does not exist" (0609 0011h): the one abort that says a peer does not
    /// implement an optional sub-index, rather than refusing the value.</summary>
    private static bool IsNotImplemented(SdoAbortException abort) => abort.AbortCode == (uint)SdoAbortCode.SubIndexDoesNotExist;

    /// <summary>Step 5 downloads 1010h after the acknowledgement; a sub-index the peer's bound
    /// description does not declare would be refused by the gate then — the configuration
    /// acknowledged, the store not done. Refused before the first frame instead, as the gate would.</summary>
    private static void EnsureStorageFitsDescription(byte peerNodeId, PeerSafetyConfiguration configuration, PeerDescriptionBinding description)
    {
        if (configuration.Storage == PeerSafetyStorage.None) return;
        if (!description.Declares(Co.StoreParameters, (byte)configuration.Storage))
            throw PeerSdoAccessException.NotInDescription(peerNodeId, Co.StoreParameters, (byte)configuration.Storage);
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
    /// objects: a slot above the count is no part of the SRDO (§8.4.2.2) — the device neither
    /// uses nor checksums it (§8.4.2.2 field g) — and a peer may not implement it at all. The SRVT
    /// of a tx SRDO whose record the file declares without it is written all the same, as
    /// optional: the peer's abort 0609 0011h is taken as "not implemented" and tolerated, by the
    /// write and the readback alike, and its checksum is over the 20 ms the configuration says
    /// there (CiA 319 §6.2, checked before the first frame).</summary>
    private static IEnumerable<(ushort Index, byte Subindex, byte[] Value, bool Optional)> SafetyWrites(PeerSafetyConfiguration configuration, int peerSrdoCount, PeerDescriptionBinding binding)
    {
        ValidateSafetyTimes(configuration);
        return SafetyWritesCore(configuration, peerSrdoCount, binding);
    }

    private static IEnumerable<(ushort Index, byte Subindex, byte[] Value, bool Optional)> SafetyWritesCore(PeerSafetyConfiguration configuration, int peerSrdoCount, PeerDescriptionBinding binding)
    {
        // Delete every SRDO first: the device refuses a COB-ID that another existing SRDO still
        // holds, so a configuration that moves ids between SRDOs needs all of them gone before any is written.
        for (int n = 1; n <= peerSrdoCount; n++)
            yield return (SrdoRecords.CommIndex(n), 0x01, new byte[] { 0 }, false);
        for (int n = 1; n <= peerSrdoCount; n++)
        {
            var comm = SrdoRecords.CommIndex(n);
            var map = SrdoRecords.MapIndex(n);
            if (!configuration.Srdos.TryGetValue(n, out var srdo))
            {
                yield return (comm, 0x01, new byte[] { 0 }, false);
                continue;
            }
            var (p, mapping) = srdo;
            var entries = mapping.ToArray();
            yield return (comm, 0x01, new byte[] { 0 }, false);
            yield return (map, 0x00, new byte[] { 0 }, false);
            for (byte s = 1; s <= 2 * entries.Length; s++)
                yield return (map, s, ObjectDictionary.EncodeU32(EncodeMappingEntry(entries[(s - 1) / 2])), false);
            yield return (map, 0x00, new[] { (byte)(2 * entries.Length) }, false);
            ushort cycle = CheckMilliseconds(n, "cycle time", p.RefreshOrSafeguardCycleTime, 1, ushort.MaxValue);
            yield return (comm, 0x02, new[] { (byte)cycle, (byte)(cycle >> 8) }, false);
            // Sub-index 3 is written for every SRDO — a delete writes sub-index 1 only, so a value
            // from an earlier configuration would otherwise survive on a peer that does hold it.
            // Optional for a tx SRDO whose record the file declares without it: 20 ms then, by
            // EnsureValidationTimesFitDescription, and a peer that does not hold the sub-index
            // aborts, which is tolerated (CiA 319 §6.2).
            yield return (comm, 0x03, new[] { (byte)CheckMilliseconds(n, "validation time", p.ValidationTime, 1, byte.MaxValue) }, ValidationTimeIsOptional(binding, n, p.Direction));
            yield return (comm, 0x05, ObjectDictionary.EncodeU32(p.CobId1), false);
            yield return (comm, 0x06, ObjectDictionary.EncodeU32(p.CobId2), false);
            yield return (comm, 0x01, new[] { (byte)p.Direction }, false);
        }
        yield return (SrdoRecords.GfcParameter, 0x00, new[] { configuration.GlobalFailsafeCommandEnabled ? (byte)1 : (byte)0 }, false);
        for (int n = 1; n <= peerSrdoCount; n++)
        {
            // The checksum covers the SRVT as written — for a tx SRDO without one on the peer the
            // 20 ms the peer checksums too (CiA 319 §6.2; EnsureValidationTimesFitDescription).
            ushort crc = configuration.Srdos.TryGetValue(n, out var srdo) ? SrdoCrc.Compute(srdo.Parameter, srdo.Mapping) : (ushort)0;
            yield return (SrdoRecords.Checksum, (byte)n, new[] { (byte)crc, (byte)(crc >> 8) }, false);
        }
    }

    /// <summary>Uploads each pair once (the last value written to a pair is the expectation) and
    /// lists every difference. An optional pair the peer answers with 0609 0011h is one it does
    /// not implement: no difference (CiA 319 §6.2).</summary>
    private async Task<List<PeerSafetyMismatch>> CompareAsync(byte peerNodeId,
        IReadOnlyList<(ushort Index, byte Subindex, byte[] Value, bool Optional)> expected, PeerDescriptionBinding binding, CancellationToken cancellationToken)
    {
        var final = new Dictionary<(ushort, byte), (byte[] Value, bool Optional)>();
        var order = new List<(ushort, byte)>();
        foreach (var (index, sub, value, optional) in expected)
        {
            if (!final.ContainsKey((index, sub))) order.Add((index, sub));
            final[(index, sub)] = (value, optional);
        }
        var mismatches = new List<PeerSafetyMismatch>();
        foreach (var (index, sub) in order)
        {
            var (value, optional) = final[(index, sub)];
            byte[] actual;
            try
            {
                actual = await SdoUploadCoreAsync(peerNodeId, index, sub, SdoTransferMode.Auto, cancellationToken, transaction: binding).ConfigureAwait(false);
            }
            catch (SdoAbortException ex) when (optional && IsNotImplemented(ex))
            {
                continue;
            }
            if (!actual.AsSpan().SequenceEqual(value))
                mismatches.Add(new PeerSafetyMismatch(index, sub, value, actual));
        }
        return mismatches;
    }
}
