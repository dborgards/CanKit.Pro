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

    /// <summary>How a safety object is transferred. <see cref="Mandatory"/>: written and read
    /// back, every abort propagates. <see cref="OptionalOnPeer"/>: a tx SRDO's SRVT the bound file
    /// does not declare (CiA 319 §6.2) — written all the same; a peer that answers "sub-index does
    /// not exist" does not implement it and is not asked to read it back, one that answers
    /// "read-only" holds it and is; the readback of a verification tolerates "sub-index does not
    /// exist" likewise. <see cref="ReadbackOnly"/>: a tx SRDO's SRVT the file declares
    /// non-writable — not written, read back and compared like every other value.</summary>
    private enum SafetyTransfer { Mandatory, OptionalOnPeer, ReadbackOnly }

    /// <summary>One (index, sub-index, value) of the §9.2 list, and how it is transferred.</summary>
    private readonly record struct SafetyWrite(ushort Index, byte Subindex, byte[] Value, SafetyTransfer Kind = SafetyTransfer.Mandatory);

    public async Task<PeerSafetyResult> ConfigurePeerSafetyAsync(byte peerNodeId, PeerSafetyConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (configuration is null) throw new ArgumentNullException(nameof(configuration));
        var requested = configuration;
        CanOpenCobId.ValidateNodeId(peerNodeId);
        ValidateSafetyTimes(configuration, nameof(configuration)); // before the first frame
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
            // Before the first frame: every SRDO named has a record the file implies; the transfers
            // every configuration makes pass the gate now, as they would mid-transaction; the file
            // carries one identity; the mappings and the SRVTs fit what it declares.
            EnsureConfigurationFitsDescription(configuration, binding, nameof(configuration));
            EnsureFixedTransfersFitDescription(peerNodeId, binding, FixedConfigurationTransfers(configuration));
            var identity = ExpectedIdentity(peerNodeId, binding);
            EnsureMappingsFitDescription(configuration, binding);
            EnsureValidationTimesFitDescription(configuration, binding, nameof(configuration));
            EnsureWritesFitDescription(peerNodeId, binding, FixedConfigurationWrites(configuration, binding), "Nothing was sent.");
            // CiA 319 §5.2: "Prior to the configuration of an SRD, the configuration entity shall
            // perform a verification of the identity of the SRD" — the first frames of the transaction.
            var mismatches = await VerifyPeerIdentityAsync(peerNodeId, identity, binding, cancellationToken).ConfigureAwait(false);
            if (mismatches.Count > 0) return new PeerSafetyResult(false, mismatches);
            int peerCount = await ReadPeerSrdoCountAsync(peerNodeId, binding, cancellationToken).ConfigureAwait(false);
            EnsureConfigurationFitsPeer(configuration, peerCount, nameof(configuration));
            EnsureFixedTransfersFitDescription(peerNodeId, binding, CountedConfigurationTransfers(peerCount));
            var writes = SafetyWrites(configuration, peerCount, binding, nameof(configuration)).ToList();
            EnsureWritesFitDescription(peerNodeId, binding, WrittenPairs(writes), "Nothing was written.");
            // §9.2 "write all safety-relevant parameter incl. checksums"; CiA 319 §5.3 step 1: the
            // SRDO configuration data, then the checksums to 13FFh.
            var declined = new HashSet<(ushort, byte)>(); // optional writes the peer does not implement
            // A ReadbackOnly write — the file declares it read-only — is read back, not written.
            foreach (var write in writes.Where(w => w.Kind != SafetyTransfer.ReadbackOnly))
            {
                try
                {
                    await SdoDownloadCoreAsync(peerNodeId, write.Index, write.Subindex, write.Value, SdoTransferMode.Auto, cancellationToken, transaction: binding).ConfigureAwait(false);
                }
                catch (SdoAbortException ex) when (write.Kind == SafetyTransfer.OptionalOnPeer && (IsNotImplemented(ex) || IsReadOnly(ex)))
                {
                    // The SRVT of a tx SRDO the peer does not implement (CiA 319 §6.2): it
                    // checksums the default, which is what was being written. A peer that holds
                    // it read-only is asked to read it back like any other value.
                    if (IsNotImplemented(ex)) declined.Add((write.Index, write.Subindex));
                }
            }
            // §9.2 "read all safety-relevant parameter incl. checksums back" — "compared"; step 2.
            mismatches = await CompareAsync(peerNodeId, writes, declined.Contains, binding, cancellationToken).ConfigureAwait(false);
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
            if (configuration.Storage == PeerSafetyStorage.None) return new PeerSafetyResult(true, Array.Empty<PeerSafetyMismatch>());
            // Step 5: "the configuration entity can request a SDO download service to object 1010h
            // ... with the signature 65 76 61 73h" — after the acknowledgement, so that what is
            // stored includes 13FEh = A5h.
            try
            {
                await SdoDownloadCoreAsync(peerNodeId, Co.StoreParameters, (byte)configuration.Storage, (byte[])SaveSignature.Clone(),
                    SdoTransferMode.Auto, cancellationToken, transaction: binding).ConfigureAwait(false);
            }
            catch (SdoAbortException abort)
            {
                // The acknowledgement stands on the peer; the store did not. The result says both.
                return new PeerSafetyResult(true, Array.Empty<PeerSafetyMismatch>(), stored: false, storageAbort: abort);
            }
            return new PeerSafetyResult(true, Array.Empty<PeerSafetyMismatch>(), stored: true);
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
        ValidateSafetyTimes(expected, nameof(expected)); // before the first frame
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
        const string paramName = "expected"; // the public method's parameter, whichever pre-check refuses
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
            ValidateSafetyTimes(expected, paramName); // before the first frame, as the two below
            var binding = new PeerDescriptionBinding(description, Array.Empty<(ushort, byte)>());
            EnsureConfigurationFitsDescription(expected, binding, paramName);
            EnsureFixedTransfersFitDescription(peerNodeId, binding, FixedVerificationTransfers(expected));
            EnsureValidationTimesFitDescription(expected, binding, paramName);
            int peerCount = await ReadPeerSrdoCountAsync(peerNodeId, binding, cancellationToken).ConfigureAwait(false);
            EnsureConfigurationFitsPeer(expected, peerCount, paramName);
            EnsureFixedTransfersFitDescription(peerNodeId, binding, CountedVerificationTransfers(peerCount));
            // 1300h is not part of step D's list (§8.3.1 D). A deleted SRDO's checksum is not
            // checked by the device (§9.5, last rule: the checksums of the SRDOs that exist), so a
            // stale one is no mismatch; that SRDO's sub1 = 0 still is compared.
            var expectedValues = SafetyWrites(expected, peerCount, binding, paramName)
                .Where(w => w.Index != SrdoRecords.GfcParameter)
                .Where(w => w.Index != SrdoRecords.Checksum || expected.Srdos.ContainsKey(w.Subindex))
                .Append(new SafetyWrite(SrdoRecords.ConfigurationValid, 0x00, new[] { SrdoRecords.ConfigurationValidValue }))
                .ToList();
            // No write told which optional SRVTs the peer implements: the readback of one the file
            // does not declare tolerates "sub-index does not exist" (CiA 319 §6.2).
            var optional = new HashSet<(ushort, byte)>(expectedValues.Where(w => w.Kind == SafetyTransfer.OptionalOnPeer).Select(w => (w.Index, w.Subindex)));
            var mismatches = await CompareAsync(peerNodeId, expectedValues, optional.Contains, binding, cancellationToken).ConfigureAwait(false);
            return new PeerSafetyResult(mismatches.Count == 0, mismatches);
        }
        finally
        {
            gate.Release();
        }
    }

    // -----------------------------------------------------------------------------------------
    // Before the first frame.
    // -----------------------------------------------------------------------------------------

    /// <summary>The refusal of a configuration with no description bound for the peer, before
    /// any frame: its first transfer would be 1018h:01, which the gate lets through without a
    /// description — but the transaction has nothing to compare it with, and every safety object
    /// after it would be refused.</summary>
    private static PeerSdoAccessException NoDescriptionForSafety(byte peerNodeId)
        => new(peerNodeId, Co.Identity, 0x01, peerDescriptionLoaded: false,
            $"A safety configuration of node 0x{peerNodeId:X2} needs a peer EDS or DCF bound with BindPeerDeviceDescription: " +
            "its first transfer, 1018h:01, is compared with the file's identity (CiA 319 §5.2), and every safety object after it " +
            "passes the peer-SDO gate only with one. Nothing was sent.");

    /// <summary>The transfers a configuration makes whatever the peer answers — the identity, the
    /// count, 1300h, the checksums of the SRDOs it names, 13FEh and the store — in the order they
    /// are made. Refused by the gate before the first frame instead of mid-transaction, where a
    /// refusal would leave the peer part-way through its configuration.</summary>
    private static IEnumerable<(ushort Index, byte Subindex)> FixedConfigurationTransfers(PeerSafetyConfiguration configuration)
    {
        yield return (Co.Identity, 0x01);
        yield return (Co.Identity, 0x02);
        yield return (Co.Identity, 0x03);
        yield return (SrdoRecords.Checksum, 0x00);
        yield return (SrdoRecords.GfcParameter, 0x00);
        foreach (var n in configuration.Srdos.Keys.OrderBy(n => n)) yield return (SrdoRecords.Checksum, (byte)n);
        yield return (SrdoRecords.ConfigurationValid, 0x00);
        if (configuration.Storage != PeerSafetyStorage.None) yield return (Co.StoreParameters, (byte)configuration.Storage);
    }

    /// <summary>The transfers a verification makes whatever the peer answers: the count, the
    /// checksums of the SRDOs it expects, 13FEh.</summary>
    private static IEnumerable<(ushort Index, byte Subindex)> FixedVerificationTransfers(PeerSafetyConfiguration expected)
    {
        yield return (SrdoRecords.Checksum, 0x00);
        foreach (var n in expected.Srdos.Keys.OrderBy(n => n)) yield return (SrdoRecords.Checksum, (byte)n);
        yield return (SrdoRecords.ConfigurationValid, 0x00);
    }

    /// <summary>The transfers a configuration makes for every SRDO up to the peer's count, named
    /// or not, whatever the peer answers — the deletion (sub-index 01h of each communication
    /// record, the delete-first pass) and each checksum 13FFh:n (0 for an SRDO not named) — known
    /// once 13FFh:00 is read and run through the gate then, before the first write: a refusal
    /// there would otherwise come after the peer's SRDOs were deleted and its 13FEh cleared.</summary>
    private static IEnumerable<(ushort Index, byte Subindex)> CountedConfigurationTransfers(int peerSrdoCount)
    {
        for (int n = 1; n <= peerSrdoCount; n++) yield return (SrdoRecords.CommIndex(n), 0x01);
        for (int n = 1; n <= peerSrdoCount; n++) yield return (SrdoRecords.Checksum, (byte)n);
    }

    /// <summary>The transfers a verification makes for every SRDO up to the peer's count: sub-index
    /// 01h of each communication record (an SRDO not expected is compared as deleted).</summary>
    private static IEnumerable<(ushort Index, byte Subindex)> CountedVerificationTransfers(int peerSrdoCount)
    {
        for (int n = 1; n <= peerSrdoCount; n++) yield return (SrdoRecords.CommIndex(n), 0x01);
    }

    /// <summary>The gate every transfer passes, run over <paramref name="transfers"/> before the
    /// first frame: the same decision, the same exception, earlier.</summary>
    private static void EnsureFixedTransfersFitDescription(byte peerNodeId, PeerDescriptionBinding binding, IEnumerable<(ushort Index, byte Subindex)> transfers)
    {
        foreach (var (index, subindex) in transfers)
            EnsurePeerSdoAccess(peerNodeId, index, subindex, binding);
    }

    /// <summary>The downloads a configuration makes whatever the peer answers, known before the
    /// first frame: the §9.2 list up to the highest SRDO it names — the peer's count is at least
    /// that, or the call ends at the count — then 13FEh and the 1010h sub-index asked for.</summary>
    private static IEnumerable<(ushort Index, byte Subindex)> FixedConfigurationWrites(PeerSafetyConfiguration configuration, PeerDescriptionBinding binding)
    {
        int highestNamed = configuration.Srdos.Keys.DefaultIfEmpty(0).Max();
        foreach (var pair in WrittenPairs(SafetyWrites(configuration, highestNamed, binding, nameof(configuration)))) yield return pair;
        yield return (SrdoRecords.ConfigurationValid, 0x00);
        if (configuration.Storage != PeerSafetyStorage.None) yield return (Co.StoreParameters, (byte)configuration.Storage);
    }

    /// <summary>The pairs of <paramref name="writes"/> that are downloaded: a ReadbackOnly SRVT is not.</summary>
    private static IEnumerable<(ushort Index, byte Subindex)> WrittenPairs(IEnumerable<SafetyWrite> writes)
        => writes.Where(w => w.Kind != SafetyTransfer.ReadbackOnly).Select(w => (w.Index, w.Subindex));

    /// <summary>A pair the bound file declares <c>ro</c> or <c>const</c> is one a device that
    /// honours its file refuses with 0601 0002h — 13FFh:n among them, whose access a description
    /// may restrict (#289). CiA 319 §5.3 step 1 downloads every value of the list, so such a
    /// refusal would come mid-transaction, after the delete-first pass of §9.2 had deleted the
    /// peer's SRDOs and cleared its 13FEh. Refused before that instead, with the gate: the writes
    /// the configuration fixes before the first frame, those of the SRDOs it does not name once the
    /// count is read (#343). A pair the file does not declare is the peer's to judge.</summary>
    private static void EnsureWritesFitDescription(byte peerNodeId, PeerDescriptionBinding binding,
        IEnumerable<(ushort Index, byte Subindex)> writes, string nothingDone)
    {
        foreach (var (index, subindex) in writes)
        {
            if (!binding.DeclaresReadOnly(index, subindex)) continue;
            throw new InvalidOperationException(
                $"The description bound for node 0x{peerNodeId:X2} declares {index:X4}h:{subindex:X2}h read-only (AccessType ro or const), " +
                "and the configuration downloads it (CiA 319 §5.3 step 1): the peer would refuse that write with 0601 0002h part-way through, " +
                $"after CiA DSP 304 §9.2 had deleted its SRDOs and cleared its 13FEh. {nothingDone}");
        }
    }

    /// <summary>What CiA 319 §5.2 compares the peer with: the file's own 1018h:01–03 — "the
    /// entries in the EDS corresponding to the SRD" — as declared (ParameterValue, DefaultValue
    /// where none). Checked before the first frame: each of the three has a value this
    /// implementation reads (a plain number; a <c>$NODEID</c> formula is no identity and refused); the
    /// <c>[DeviceInfo]</c> numbers, where set, agree with them — a file that contradicts itself
    /// describes no one device, and an absent <c>[DeviceInfo]</c> key, which reads as 0, is not
    /// a contradiction; and the three are not all 0 — a file that says vendor-ID, product code
    /// and revision 0 carries no identity, and a comparison with it would prove nothing.</summary>
    private static (uint VendorId, uint ProductCode, uint RevisionNumber) ExpectedIdentity(byte peerNodeId, PeerDescriptionBinding binding)
    {
        var declared = binding.DeclaredIdentity;
        uint vendorId = IdentityValue(peerNodeId, 0x01, declared.VendorId);
        uint productCode = IdentityValue(peerNodeId, 0x02, declared.ProductCode);
        uint revisionNumber = IdentityValue(peerNodeId, 0x03, declared.RevisionNumber);
        var info = binding.DeviceInfoIdentity;
        EnsureDeviceInfoAgrees(peerNodeId, "VendorNumber", info.VendorId, 0x01, vendorId);
        EnsureDeviceInfoAgrees(peerNodeId, "ProductNumber", info.ProductCode, 0x02, productCode);
        EnsureDeviceInfoAgrees(peerNodeId, "RevisionNumber", info.RevisionNumber, 0x03, revisionNumber);
        if (vendorId == 0 && productCode == 0 && revisionNumber == 0)
            throw new InvalidOperationException(
                $"The description bound for node 0x{peerNodeId:X2} carries no identity: its 1018h:01, :02 and :03 are all 0, " +
                "so the peer's identity cannot be verified against it (CiA 319 §5.2). Nothing was sent.");
        return (vendorId, productCode, revisionNumber);
    }

    private static uint IdentityValue(byte peerNodeId, byte subindex, uint? declared)
        => declared ?? throw new InvalidOperationException(
            $"The description bound for node 0x{peerNodeId:X2} declares 1018h:{subindex:X2} without a value this implementation can read " +
            "(ParameterValue, or DefaultValue where none: a plain number, not a $NODEID formula), so the peer's identity cannot be verified against it (CiA 319 §5.2). Nothing was sent.");

    private static void EnsureDeviceInfoAgrees(byte peerNodeId, string key, uint deviceInfo, byte subindex, uint declared)
    {
        if (deviceInfo != 0 && deviceInfo != declared)
            throw new InvalidOperationException(
                $"The description bound for node 0x{peerNodeId:X2} contradicts itself: [DeviceInfo] {key} is 0x{deviceInfo:X8} while 1018h:{subindex:X2} is 0x{declared:X8}, " +
                "so it describes no one device to verify the peer against (CiA 319 §5.2). Nothing was sent.");
    }

    // -----------------------------------------------------------------------------------------
    // The frames.
    // -----------------------------------------------------------------------------------------

    /// <summary>CiA 319 §5.2: 1018h:01 (vendor-ID), :02 (product code) and :03 (revision number)
    /// are uploaded and compared with <paramref name="expected"/>, the file's identity. All three
    /// are read; each that differs is a mismatch with the four bytes expected and the bytes the
    /// peer returned, whatever their number. §5.2 requires all three of an SRD, where CiA 301
    /// §7.5.2.21 makes :02 and :03 optional: a peer that aborts one of them with "sub-index does
    /// not exist" is not an SRD this procedure can configure, and the abort propagates as every
    /// abort does — unless an earlier entry already differed, when that answer is part of the
    /// same finding, another device, and is reported as a mismatch with no bytes read. The
    /// further verifications the note of §5.2 names — firmware, hardware, serial number,
    /// location — are the application's.</summary>
    private async Task<List<PeerSafetyMismatch>> VerifyPeerIdentityAsync(byte peerNodeId,
        (uint VendorId, uint ProductCode, uint RevisionNumber) expected, PeerDescriptionBinding binding, CancellationToken cancellationToken)
    {
        var mismatches = new List<PeerSafetyMismatch>();
        foreach (var (subindex, value) in new (byte Subindex, uint Value)[] { (0x01, expected.VendorId), (0x02, expected.ProductCode), (0x03, expected.RevisionNumber) })
        {
            var bytes = ObjectDictionary.EncodeU32(value);
            byte[] actual;
            try
            {
                actual = await SdoUploadCoreAsync(peerNodeId, Co.Identity, subindex, SdoTransferMode.Auto, cancellationToken, transaction: binding).ConfigureAwait(false);
            }
            catch (SdoAbortException ex) when (mismatches.Count > 0 && IsNotImplemented(ex))
            {
                mismatches.Add(new PeerSafetyMismatch(Co.Identity, subindex, bytes, Array.Empty<byte>()));
                continue;
            }
            if (!actual.AsSpan().SequenceEqual(bytes))
                mismatches.Add(new PeerSafetyMismatch(Co.Identity, subindex, bytes, actual));
        }
        return mismatches;
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
        // The §9.2 list touches every SRDO up to the count, named or not — a configuration deletes
        // each (130(n)h:01 = 0) and zeroes its checksum (13FFh:n), a verification reads each sub1 —
        // and the gate implies records only up to the file's highest (FR-CO-029). A peer that
        // reports more than the file implies is refused here, before the first write: at the first
        // record above the file the gate would refuse mid-transaction, after the delete-first pass
        // had already deleted the peer's SRDOs and cleared its 13FEh.
        if (count[0] > binding.DescribedSrdoCount)
            throw new InvalidOperationException(
                $"The peer reports {count[0]} SRDOs in 13FFh:00, but its bound description declares SRDO records only up to {binding.DescribedSrdoCount}, " +
                $"and the peer-SDO gate lets through no record above them (FR-CO-029): SRDOs {binding.DescribedSrdoCount + 1}..{count[0]} could be neither deleted nor verified, " +
                "so the bound file does not describe this peer. Nothing was written, and nothing further was sent.");
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
    /// sub-index 3, or with it non-writable (<see cref="ValidationTimeKind"/>), is one the peer
    /// is taken not to hold a writable SRVT for, and such a peer checksums 20 ms. A configuration
    /// or expectation that says another value there is refused before the first frame: the value
    /// cannot be put on the peer, and a checksum over it would not be the peer's — so the
    /// parameter that reaches the write and the checksum says 20 ms, and nothing substitutes it.</summary>
    private static void EnsureValidationTimesFitDescription(PeerSafetyConfiguration configuration, PeerDescriptionBinding binding, string paramName)
    {
        foreach (var srdo in configuration.Srdos)
        {
            var parameter = srdo.Value.Parameter;
            if (ValidationTimeKind(binding, srdo.Key, parameter.Direction) == SafetyTransfer.Mandatory) continue;
            ushort srvt = PeerSafetyConfiguration.CheckMilliseconds(srdo.Key, "validation time", parameter.ValidationTime, 1, byte.MaxValue, paramName);
            if (srvt == SrdoRecords.DefaultValidationTimeMs) continue;
            throw new ArgumentException(
                $"SRDO {srdo.Key}: the peer's bound description declares record {SrdoRecords.CommIndex(srdo.Key):X4}h without a writable sub-index 03h, so the peer is taken not to implement the SRVT of this tx SRDO and to checksum it at the default of {SrdoRecords.DefaultValidationTimeMs} ms (CiA 319 §6.2); an SRVT of {srvt} ms would not be its checksum. Nothing was sent.",
                paramName);
        }
    }

    /// <summary>How sub-index 3 of SRDO <paramref name="srdoNumber"/> is transferred: for a
    /// consumer, whose SRVT is not optional, like every other value; for a producer as the bound
    /// file declares it (<see cref="PeerDescriptionBinding.ValidationTimeDeclaration"/>) — not at
    /// all: written as optional, the peer's answer deciding; read-only: read back, not written;
    /// writable, or the record not declared and so implied whole: like every other value.</summary>
    private static SafetyTransfer ValidationTimeKind(PeerDescriptionBinding binding, int srdoNumber, SrdoDirection direction)
    {
        if (direction != SrdoDirection.Transmit) return SafetyTransfer.Mandatory;
        return binding.ValidationTimeDeclaration(srdoNumber) switch
        {
            SrvtDeclaration.Undeclared => SafetyTransfer.OptionalOnPeer,
            SrvtDeclaration.ReadOnly => SafetyTransfer.ReadbackOnly,
            _ => SafetyTransfer.Mandatory,
        };
    }

    /// <summary>"Sub-index does not exist" (0609 0011h): the one abort that says a peer does not
    /// implement an optional sub-index, rather than refusing the value.</summary>
    private static bool IsNotImplemented(SdoAbortException abort) => abort.AbortCode == (uint)SdoAbortCode.SubIndexDoesNotExist;

    /// <summary>"Attempt to write a read-only object" (0601 0002h): the peer holds the sub-index and
    /// will not take a value — for an optional SRVT, the default it holds is what the checksum covers.</summary>
    private static bool IsReadOnly(SdoAbortException abort) => abort.AbortCode == (uint)SdoAbortCode.AttemptWriteReadOnly;

    /// <summary>The gate lets through the records the bound file declares or implies — up to its
    /// highest SRDO record (FR-CO-029) — and no record above them, so an SRDO named above that
    /// count could never be written or read: refused before the first frame, not at its first
    /// record after the delete-first pass.</summary>
    private static void EnsureConfigurationFitsDescription(PeerSafetyConfiguration configuration, PeerDescriptionBinding binding, string paramName)
    {
        if (!binding.IsBound) return; // the gate refuses the first transfer
        foreach (var number in configuration.Srdos.Keys)
            if (number > binding.DescribedSrdoCount)
                throw new ArgumentException(
                    $"SRDO {number} is configured, but the peer's bound description declares only {binding.DescribedSrdoCount} SRDO record(s), and the peer-SDO gate lets through no record above them (FR-CO-029); nothing was sent.", paramName);
    }

    /// <summary>A record above the peer's count does not exist on the peer: writing the others and
    /// reporting success would leave that SRDO missing, so nothing is sent.</summary>
    private static void EnsureConfigurationFitsPeer(PeerSafetyConfiguration configuration, int peerSrdoCount, string paramName)
    {
        foreach (var number in configuration.Srdos.Keys)
            if (number > peerSrdoCount)
                throw new ArgumentException(
                    $"SRDO {number} is configured, but the peer reports only {peerSrdoCount} SRDO(s) in 13FFh:00.", paramName);
    }

    /// <summary>The cycle time (sub2) is 1..65535 ms and the validation time (sub3) 1..255 ms; a hand-built configuration is not range-checked anywhere else, and the casts
    /// below would wrap silently.</summary>
    private static void ValidateSafetyTimes(PeerSafetyConfiguration configuration, string paramName)
    {
        // PeerSafetyConfiguration.Add refuses these already; kept as the check before the first frame.
        foreach (var pair in configuration.Srdos)
            PeerSafetyConfiguration.ValidateTimes(pair.Key, pair.Value.Parameter, paramName);
    }

    /// <summary>The writes of §9.2 in order: a first pass that deletes every SRDO; then per SRDO
    /// the deletion, the mapping (disabled, slots, count), the times, the ids, the creation (SRDOs
    /// the configuration does not name stay deleted); then 1300h; then every checksum. The same
    /// list is what the readback compares. The mapping is sub-index 0 and the 2k slots of its k
    /// objects: a slot above the count is no part of the SRDO (§8.4.2.2) — the device neither
    /// uses nor checksums it (§8.4.2.2 field g) — and a peer may not implement it at all. The SRVT
    /// of a tx SRDO is transferred as the file declares it (<see cref="ValidationTimeKind"/>),
    /// and its checksum is over the 20 ms the configuration says there when the peer may not
    /// take another (CiA 319 §6.2, checked before the first frame).</summary>
    private static IEnumerable<SafetyWrite> SafetyWrites(PeerSafetyConfiguration configuration, int peerSrdoCount, PeerDescriptionBinding binding, string paramName)
    {
        ValidateSafetyTimes(configuration, paramName);
        return SafetyWritesCore(configuration, peerSrdoCount, binding, paramName);
    }

    private static IEnumerable<SafetyWrite> SafetyWritesCore(PeerSafetyConfiguration configuration, int peerSrdoCount, PeerDescriptionBinding binding, string paramName)
    {
        // Delete every SRDO first: the device refuses a COB-ID that another existing SRDO still
        // holds, so a configuration that moves ids between SRDOs needs all of them gone before any is written.
        for (int n = 1; n <= peerSrdoCount; n++)
            yield return new SafetyWrite(SrdoRecords.CommIndex(n), 0x01, new byte[] { 0 });
        for (int n = 1; n <= peerSrdoCount; n++)
        {
            var comm = SrdoRecords.CommIndex(n);
            var map = SrdoRecords.MapIndex(n);
            if (!configuration.Srdos.TryGetValue(n, out var srdo))
            {
                yield return new SafetyWrite(comm, 0x01, new byte[] { 0 });
                continue;
            }
            var (p, mapping) = srdo;
            var entries = mapping.ToArray();
            yield return new SafetyWrite(comm, 0x01, new byte[] { 0 });
            yield return new SafetyWrite(map, 0x00, new byte[] { 0 });
            for (byte s = 1; s <= 2 * entries.Length; s++)
                yield return new SafetyWrite(map, s, ObjectDictionary.EncodeU32(EncodeMappingEntry(entries[(s - 1) / 2])));
            yield return new SafetyWrite(map, 0x00, new[] { (byte)(2 * entries.Length) });
            ushort cycle = PeerSafetyConfiguration.CheckMilliseconds(n, "cycle time", p.RefreshOrSafeguardCycleTime, 1, ushort.MaxValue, paramName);
            yield return new SafetyWrite(comm, 0x02, new[] { (byte)cycle, (byte)(cycle >> 8) });
            // Sub-index 3 is part of every record — a delete writes sub-index 1 only, so a value
            // from an earlier configuration would otherwise survive on a peer that does hold it.
            byte srvt = (byte)PeerSafetyConfiguration.CheckMilliseconds(n, "validation time", p.ValidationTime, 1, byte.MaxValue, paramName);
            yield return new SafetyWrite(comm, 0x03, new[] { srvt }, ValidationTimeKind(binding, n, p.Direction));
            yield return new SafetyWrite(comm, 0x05, ObjectDictionary.EncodeU32(p.CobId1));
            yield return new SafetyWrite(comm, 0x06, ObjectDictionary.EncodeU32(p.CobId2));
            yield return new SafetyWrite(comm, 0x01, new[] { (byte)p.Direction });
        }
        yield return new SafetyWrite(SrdoRecords.GfcParameter, 0x00, new[] { configuration.GlobalFailsafeCommandEnabled ? (byte)1 : (byte)0 });
        for (int n = 1; n <= peerSrdoCount; n++)
        {
            // The checksum covers the SRVT as the record holds it — for a tx SRDO whose SRVT the
            // peer may not take, the 20 ms the peer checksums too (CiA 319 §6.2;
            // EnsureValidationTimesFitDescription).
            ushort crc = configuration.Srdos.TryGetValue(n, out var srdo) ? SrdoCrc.Compute(srdo.Parameter, srdo.Mapping) : (ushort)0;
            yield return new SafetyWrite(SrdoRecords.Checksum, (byte)n, new[] { (byte)crc, (byte)(crc >> 8) });
        }
    }

    /// <summary>Uploads each pair once (the last value written to a pair is the expectation) and
    /// lists every difference. A pair <paramref name="notImplementedOnPeer"/> admits — an optional
    /// SRVT the peer declined to write, or one a verification has no write to go by — may be
    /// answered with 0609 0011h: no difference (CiA 319 §6.2). Every other abort propagates: a
    /// peer that took a value answers for it.</summary>
    private async Task<List<PeerSafetyMismatch>> CompareAsync(byte peerNodeId, IReadOnlyList<SafetyWrite> expected,
        Func<(ushort, byte), bool> notImplementedOnPeer, PeerDescriptionBinding binding, CancellationToken cancellationToken)
    {
        var final = new Dictionary<(ushort, byte), byte[]>();
        var order = new List<(ushort, byte)>();
        foreach (var write in expected)
        {
            var pair = (write.Index, write.Subindex);
            if (!final.ContainsKey(pair)) order.Add(pair);
            final[pair] = write.Value;
        }
        var mismatches = new List<PeerSafetyMismatch>();
        foreach (var (index, sub) in order)
        {
            byte[] actual;
            try
            {
                actual = await SdoUploadCoreAsync(peerNodeId, index, sub, SdoTransferMode.Auto, cancellationToken, transaction: binding).ConfigureAwait(false);
            }
            catch (SdoAbortException ex) when (notImplementedOnPeer((index, sub)) && IsNotImplemented(ex))
            {
                continue;
            }
            if (!actual.AsSpan().SequenceEqual(final[(index, sub)]))
                mismatches.Add(new PeerSafetyMismatch(index, sub, final[(index, sub)], actual));
        }
        return mismatches;
    }
}
