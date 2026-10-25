using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Pro.CANopen.Safety;
using CanKit.Pro.CANopen.Sdo;

namespace CanKit.Pro.CANopen;

internal sealed partial class CanOpenNode : ICanOpenSafetyInspection
{
    public async Task<PeerSafetySnapshot> ReadPeerSafetyConfigurationAsync(byte peerNodeId,
        PeerSafetyConfiguration? expected = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        CanOpenCobId.ValidateNodeId(peerNodeId);
        var channel = PeerSdoChannel(peerNodeId);
        await channel.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var description = CaptureBoundPeerDescription(peerNodeId) ?? throw NoDescriptionForSafety(peerNodeId);
            var expectation = expected?.Snapshot();
            if (expectation is not null) ValidateSafetyTimes(expectation, nameof(expected));
            var binding = new PeerDescriptionBinding(description, Array.Empty<(ushort, byte)>());
            if (expectation is not null) EnsureValidationTimesFitDescription(expectation, binding, nameof(expected));
            var started = DateTimeOffset.UtcNow;
            var timer = Stopwatch.StartNew();
            var entries = new List<PeerSafetyEntry>();
            var rechecks = new List<PeerSafetyEntry>();
            var findings = new List<PeerSafetyFinding>();
            var requests = new List<(ushort Index, byte Subindex, int Width)>();
            bool recheckWidthsValid = true;

            PeerSafetyEntry Fallback(ushort index, byte sub, int width, SdoAbortException? abort)
            {
                // Only an explicitly declared file value, never a loader-created default image.
                if (description.TryGet(index, out var described))
                {
                    var entry = described.Find(e => e.Subindex == sub);
                    if (ParseDescribedUnsigned(entry.Value, peerNodeId) is { } word &&
                        (width == 4 || word < (1u << (8 * width))))
                        return new PeerSafetyEntry(index, sub, ObjectDictionary.EncodeU32(word).Take(width).ToArray(),
                            SafetyValueOrigin.DeviceDescription, abort?.AbortCode, abort?.Origin);
                }
                return new PeerSafetyEntry(index, sub, Array.Empty<byte>(), SafetyValueOrigin.Unavailable, abort?.AbortCode, abort?.Origin);
            }

            async Task<PeerSafetyEntry> Read(ushort index, byte sub, int width, bool recheck = false)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var raw = await SdoUploadCoreAsync(peerNodeId, index, sub, SdoTransferMode.Auto,
                        cancellationToken, transaction: binding).ConfigureAwait(false);
                    if (recheck && raw.Length != width)
                    {
                        findings.Add(new PeerSafetyFinding(index, sub, PeerSafetyFindingKind.InvalidValue,
                            $"Repeated upload expected {width} bytes, received {raw.Length}."));
                        if (index > SrdoRecords.GfcParameter) recheckWidthsValid = false;
                    }
                    return new PeerSafetyEntry(index, sub, raw, SafetyValueOrigin.Live);
                }
                catch (SdoAbortException abort)
                {
                    findings.Add(new PeerSafetyFinding(index, sub, PeerSafetyFindingKind.SdoAbort, abort.Message));
                    if (SrdoRecords.IsCommunicationRecord(index) && sub == 3 &&
                        abort.Origin == SdoAbortOrigin.Peer && abort.AbortCode == 0x06090011 &&
                        binding.ValidationTimeDeclaration(index - SrdoRecords.CommunicationBase) == SrvtDeclaration.Undeclared &&
                        entries.Any(e => e.Index == index && e.Subindex == 1 && e.Origin == SafetyValueOrigin.Live && e.UnsignedValue == 1))
                        return new PeerSafetyEntry(index, sub, new[] { SrdoRecords.DefaultValidationTimeMs },
                            SafetyValueOrigin.CiA319Default, abort.AbortCode, abort.Origin);
                    return Fallback(index, sub, width, abort);
                }
                catch (PeerSdoAccessException ex)
                {
                    findings.Add(new PeerSafetyFinding(index, sub, PeerSafetyFindingKind.AccessRefused, ex.Message));
                    return Fallback(index, sub, width, null);
                }
                catch (Exception ex) when (ex is CanOpenTransportException || ex is InvalidOperationException and not ObjectDisposedException)
                {
                    findings.Add(new PeerSafetyFinding(index, sub, PeerSafetyFindingKind.Unavailable, ex.Message));
                    return Fallback(index, sub, width, null);
                }
            }

            async Task<PeerSafetyEntry> Capture(ushort index, byte sub, int width)
            {
                requests.Add((index, sub, width));
                var entry = await Read(index, sub, width).ConfigureAwait(false);
                entries.Add(entry);
                return entry;
            }

            for (byte sub = 1; sub <= 4; sub++) await Capture(Co.Identity, sub, 4).ConfigureAwait(false);
            await Capture(SrdoRecords.GfcParameter, 0, 1).ConfigureAwait(false);
            await Capture(SrdoRecords.ConfigurationValid, 0, 1).ConfigureAwait(false);
            var count = await Capture(SrdoRecords.Checksum, 0, 1).ConfigureAwait(false);
            int bound = binding.DescribedSrdoCount;
            int peerCount = count.RawValue.Count == 1 ? count.RawValue[0] : -1;
            bool countUsable = count.Origin == SafetyValueOrigin.Live && peerCount >= 0 && peerCount <= SrdoRecords.MaxSrdoCount;
            if (countUsable && peerCount <= bound) bound = peerCount;
            else findings.Add(new PeerSafetyFinding(SrdoRecords.Checksum, 0, PeerSafetyFindingKind.InvalidValue,
                "The live SRDO count is unavailable, malformed, above 64 or above the bound description. Inspection stays bounded by the description."));
            bool mappingCountsComplete = true;
            for (int n = 1; n <= bound; n++)
            {
                ushort comm = SrdoRecords.CommIndex(n), map = SrdoRecords.MapIndex(n);
                for (byte sub = 0; sub <= 6; sub++)
                    await Capture(comm, sub, sub is 2 ? 2 : sub is 5 or 6 ? 4 : 1).ConfigureAwait(false);
                var mappingCount = await Capture(map, 0, 1).ConfigureAwait(false);
                // Retain every advertised slot, including malformed pairs. 128 is the bounded
                // record range; interpretation below retains this implementation's 8-byte limit.
                int slots = mappingCount.RawValue.Count == 1 ? mappingCount.RawValue[0] : -1;
                if (slots >= 0 && slots <= 128)
                    for (byte sub = 1; sub <= slots; sub++)
                        await Capture(map, sub, 4).ConfigureAwait(false);
                else mappingCountsComplete = false;
                await Capture(SrdoRecords.Checksum, (byte)n, 2).ConfigureAwait(false);
            }
            foreach (var request in requests)
                rechecks.Add(await Read(request.Index, request.Subindex, request.Width, recheck: true).ConfigureAwait(false));

            var expectedValues = new Dictionary<(ushort, byte), byte[]>();
            if (expectation is not null)
            {
                foreach (var value in SafetyWrites(expectation, bound, binding, nameof(expected))
                             .Where(w => w.Index != SrdoRecords.Checksum || expectation.Srdos.ContainsKey(w.Subindex)))
                    expectedValues[(value.Index, value.Subindex)] = value.Value;
                expectedValues[(SrdoRecords.ConfigurationValid, 0)] = new[] { SrdoRecords.ConfigurationValidValue };
                foreach (var n in expectation.Srdos.Keys.Where(n => n > bound))
                    findings.Add(new PeerSafetyFinding(SrdoRecords.CommIndex(n), 1, PeerSafetyFindingKind.ExpectedUnavailable,
                        "The expected SRDO lies outside the acquired peer/description range."));
            }
            var mappedObjects = entries.Where(e => SrdoRecords.IsMappingRecord(e.Index) &&
                    (e.Subindex & 1) != 0 && e.RawValue.Count == 4)
                .Select(e => ((ushort)(e.UnsignedValue!.Value >> 16), (byte)(e.UnsignedValue.Value >> 8)));
            var mappingBinding = new PeerDescriptionBinding(description, mappedObjects);
            return PeerSafetyDiagnostics.Build(peerNodeId, started, timer.Elapsed, entries, rechecks, findings,
                bound, recheckWidthsValid && mappingCountsComplete && countUsable && peerCount <= binding.DescribedSrdoCount,
                expectation is not null, expectedValues, (entry, direction) => MappingEntryRefusal(mappingBinding, entry, direction),
                (n, parameter) => ValidationTimeKind(binding, n, parameter.Direction) != SafetyTransfer.Mandatory &&
                    parameter.ValidationTime != TimeSpan.FromMilliseconds(SrdoRecords.DefaultValidationTimeMs)
                    ? "The bound file declares no writable producer SRVT; the existing CiA 319 rule requires 20 ms. Raw values are retained without substitution."
                    : null);
        }
        finally { channel.Release(); }
    }
}
