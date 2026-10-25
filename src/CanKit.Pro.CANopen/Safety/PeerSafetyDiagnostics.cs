using System;
using System.Collections.Generic;
using System.Linq;
using CanKit.Pro.CANopen.Pdo;

namespace CanKit.Pro.CANopen.Safety;

/// <summary>Pure interpretation of captured bytes. Never manufactures a live image from fallback values.</summary>
internal static class PeerSafetyDiagnostics
{
    internal static PeerSafetySnapshot Build(byte peer, DateTimeOffset started, TimeSpan duration,
        IReadOnlyList<PeerSafetyEntry> entries, IReadOnlyList<PeerSafetyEntry> rechecks,
        List<PeerSafetyFinding> findings, int count, bool countComplete, bool hasExpected,
        IReadOnlyDictionary<(ushort, byte), byte[]> expected,
        Func<PdoMappingEntry, SrdoDirection, string?>? mappingRefusal = null,
        Func<int, SrdoCommunicationParameter, string?>? parameterRefusal = null)
    {
        var values = entries.ToDictionary(e => (e.Index, e.Subindex));
        var again = rechecks.ToDictionary(e => (e.Index, e.Subindex));
        void Find(ushort index, byte sub, PeerSafetyFindingKind kind, string message)
            => findings.Add(new PeerSafetyFinding(index, sub, kind, message));
        bool fieldsComplete = true;
        uint? Read(ushort index, byte sub, int width, bool allowSrvtRule = false)
        {
            if (values.TryGetValue((index, sub), out var e) &&
                (e.Origin == SafetyValueOrigin.Live || allowSrvtRule && e.Origin == SafetyValueOrigin.CiA319Default))
            {
                if (e.RawValue.Count == width) return e.UnsignedValue;
                Find(index, sub, PeerSafetyFindingKind.InvalidValue, $"Expected {width} bytes, received {e.RawValue.Count}.");
            }
            else Find(index, sub, PeerSafetyFindingKind.Unavailable, "No live value; a file/default value is not device evidence.");
            fieldsComplete = false;
            return null;
        }
        bool Evidence(PeerSafetyEntry e) => e.Origin is SafetyValueOrigin.Live or SafetyValueOrigin.CiA319Default;
        bool Required(PeerSafetyEntry e) => e.Index >= SrdoRecords.CommunicationBase;
        var consistency = PeerSafetySnapshotConsistency.Unconfirmed;
        foreach (var e in entries.Where(e => again.TryGetValue((e.Index, e.Subindex), out var later) &&
                     Evidence(e) && Evidence(later) &&
                     (e.Origin != later.Origin || !e.RawValue.SequenceEqual(later.RawValue))))
        {
            consistency = PeerSafetySnapshotConsistency.ChangeObserved;
            Find(e.Index, e.Subindex, PeerSafetyFindingKind.ConfigurationChanged, "The repeated live/SRVT-rule evidence differs; start a new acquisition before using this configuration.");
        }
        // A missing optional GFC or identity does not make an SRDO mapping incomplete. It still
        // has an entry-level finding and cannot satisfy an expectation requiring that value.
        bool complete = countComplete && entries.Where(e => Required(e) && e.Index != SrdoRecords.GfcParameter)
            .All(e => Evidence(e) && again.TryGetValue((e.Index, e.Subindex), out var later) && Evidence(later));
        uint? flag = Read(SrdoRecords.ConfigurationValid, 0, 1);
        bool? valid = flag switch { SrdoRecords.ConfigurationValidValue => true, 0 => false, _ => null };
        if (flag.HasValue && valid is null)
            Find(SrdoRecords.ConfigurationValid, 0, PeerSafetyFindingKind.InvalidValue, "13FEh is neither 00h nor A5h.");
        var result = new List<PeerSafetySrdoSnapshot>();
        var usedIds = new Dictionary<uint, int>();
        var collisions = new HashSet<int>();
        for (int n = 1; n <= count; n++)
        {
            ushort comm = SrdoRecords.CommIndex(n), map = SrdoRecords.MapIndex(n);
            var highest = Read(comm, 0, 1);
            var direction = Read(comm, 1, 1);
            var cycle = Read(comm, 2, 2);
            var srvt = Read(comm, 3, 1, allowSrvtRule: true);
            var transmission = Read(comm, 4, 1);
            var cob1 = Read(comm, 5, 4);
            var cob2 = Read(comm, 6, 4);
            var slotsCount = Read(map, 0, 1);
            var stored = Read(SrdoRecords.Checksum, (byte)n, 2);
            var slots = new List<(byte Subindex, uint Value)>();
            bool rawMapping = slotsCount is <= 128;
            if (rawMapping)
                for (byte sub = 1; sub <= slotsCount; sub++)
                {
                    if (Read(map, sub, 4) is { } word) slots.Add((sub, word));
                    else rawMapping = false;
                }
            bool rawCommunication = direction.HasValue && cycle.HasValue && srvt.HasValue && cob1.HasValue && cob2.HasValue;
            ushort? computed = rawCommunication && rawMapping
                ? SrdoCrc.Crc16Xmodem(SrdoCrc.CanonicalBytes((byte)direction!.Value, (ushort)cycle!.Value,
                    (byte)srvt!.Value, cob1!.Value, cob2!.Value, (byte)slotsCount!.Value, slots))
                : null;
            var comparison = stored.HasValue && computed.HasValue
                ? stored.Value == computed.Value ? SafetyComparison.Match : SafetyComparison.Mismatch
                : SafetyComparison.Unavailable;
            // Deleted SRDO checksums are not verified by the existing device/verification path.
            if (direction == 0) comparison = SafetyComparison.NotRequested;
            if (comparison == SafetyComparison.Mismatch)
                Find(SrdoRecords.Checksum, (byte)n, PeerSafetyFindingKind.SignatureMismatch, "Stored signature differs from the independently recomputed signature (implementation rules; #289 remains open).");
            // Like the existing verification path, a deleted record is identified by direction
            // alone; its stale timing/COB-ID values are not active communication parameters.
            bool communicationValid = highest == 6 && direction is 0 or 1 or 2 &&
                (direction == 0 || transmission == SrdoRecords.TransmissionType && cycle is > 0 && srvt is > 0 &&
                    cob1.HasValue && cob2.HasValue && SrdoFrames.IsCobIdPair(cob1.Value, cob2.Value));
            if (!communicationValid)
                Find(comm, 0, PeerSafetyFindingKind.InvalidValue, "Communication record is incomplete or outside this implementation's direction, timing, transmission-type or COB-ID rules.");
            var mapping = new SrdoMapping();
            bool mappingValid = rawMapping && slotsCount is > 0 and <= SrdoRecords.MappingSubindices && (slotsCount.Value & 1) == 0;
            if (mappingValid)
            {
                try
                {
                    for (int i = 0; i < slots.Count; i += 2)
                    {
                        uint word = slots[i].Value;
                        if (!SrdoRecords.IsInvertedSlotOf(word, slots[i + 1].Value))
                        { mappingValid = false; break; }
                        mapping.Add(new PdoMappingEntry((ushort)(word >> 16), (byte)(word >> 8), (byte)word));
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                { mappingValid = false; }
            }
            if (mappingValid && direction is 1 or 2 && mappingRefusal is not null)
                for (int i = 0; i < mapping.Entries.Count; i++)
                {
                    var entry = mapping.Entries[i];
                    if (mappingRefusal(entry, (SrdoDirection)direction.Value) is { } refusal)
                    {
                        mappingValid = false;
                        Find(map, (byte)(2 * i + 1), PeerSafetyFindingKind.InvalidMapping,
                            $"Mapped object {entry.Index:X4}h:{entry.Subindex:X2} {refusal} in the bound description.");
                    }
                }
            if (!mappingValid && direction != 0)
                Find(map, 0, PeerSafetyFindingKind.InvalidMapping, "Mapping is incomplete, has invalid plain/inverted slots or exceeds this implementation's byte-aligned 8-byte mapping limits.");
            SrdoCommunicationParameter? parameter = communicationValid && rawCommunication
                ? new SrdoCommunicationParameter((SrdoDirection)direction!.Value, TimeSpan.FromMilliseconds(cycle!.Value),
                    TimeSpan.FromMilliseconds(srvt!.Value), cob1!.Value, cob2!.Value)
                : null;
            if (parameter.HasValue && parameterRefusal?.Invoke(n, parameter.Value) is { } parameterReason)
            {
                communicationValid = false;
                Find(comm, 3, PeerSafetyFindingKind.InvalidValue, parameterReason);
            }
            if (direction is 1 or 2 && cob1.HasValue && cob2.HasValue)
                foreach (uint id in new[] { cob1.Value, cob2.Value })
                {
                    if (usedIds.TryGetValue(id, out int other))
                    {
                        collisions.Add(n); collisions.Add(other);
                        Find(comm, 5, PeerSafetyFindingKind.InvalidValue, $"COB-ID {id:X} is also used by SRDO {other}.");
                    }
                    else usedIds.Add(id, n);
                }
            result.Add(new PeerSafetySrdoSnapshot(n, parameter,
                mappingValid && direction != 0 ? mapping.ToArray() : Array.Empty<PdoMappingEntry>(),
                stored.HasValue ? (ushort)stored.Value : null, computed, comparison,
                direction is 1 or 2 && communicationValid && mappingValid && comparison == SafetyComparison.Match &&
                valid == true && complete && consistency == PeerSafetySnapshotConsistency.Unconfirmed));
        }
        complete &= fieldsComplete;
        // A later collision or missing field invalidates the earlier record too.
        result = result.Select(s => !complete || collisions.Contains(s.Number)
            ? new PeerSafetySrdoSnapshot(s.Number, s.Parameter, s.Mapping.ToArray(), s.StoredSignature,
                s.ComputedSignature, s.SignatureComparison, false) : s).ToList();
        var expectedComparison = hasExpected ? SafetyComparison.Match : SafetyComparison.NotRequested;
        foreach (var pair in expected)
        {
            if (!values.TryGetValue(pair.Key, out var e) || !Evidence(e))
            {
                if (expectedComparison != SafetyComparison.Mismatch) expectedComparison = SafetyComparison.Unavailable;
                Find(pair.Key.Item1, pair.Key.Item2, PeerSafetyFindingKind.ExpectedUnavailable, "The expected entry has no live/explicit SRVT-rule evidence.");
            }
            else if (!e.RawValue.SequenceEqual(pair.Value))
            {
                expectedComparison = SafetyComparison.Mismatch;
                Find(e.Index, e.Subindex, PeerSafetyFindingKind.ExpectedMismatch, "The captured value differs from the supplied expectation.");
            }
        }
        if (hasExpected && expectedComparison != SafetyComparison.Mismatch && findings.Any(f => f.Kind == PeerSafetyFindingKind.ExpectedUnavailable))
            expectedComparison = SafetyComparison.Unavailable;
        if (hasExpected && expectedComparison == SafetyComparison.Match &&
            (!complete || consistency == PeerSafetySnapshotConsistency.ChangeObserved))
        {
            expectedComparison = SafetyComparison.Unavailable;
            Find(0, 0, PeerSafetyFindingKind.ExpectedUnavailable,
                "A complete acquisition without observed changes is required to establish a matching expected configuration.");
        }
        findings.Add(new PeerSafetyFinding(0, 0, PeerSafetyFindingKind.NormativeUncertainty,
            "CRC byte order and version-specific interoperability remain subject to #289. This diagnostic is not a safety approval."));
        return new PeerSafetySnapshot(peer, started, duration, entries, rechecks, result, findings,
            valid, complete, expectedComparison, consistency);
    }
}
