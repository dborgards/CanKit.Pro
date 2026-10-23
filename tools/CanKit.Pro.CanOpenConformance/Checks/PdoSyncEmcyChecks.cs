using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Pdo;
using CanKit.Pro.CANopen.Sdo;

namespace CanKit.Pro.CanOpenConformance.Checks;

/// <summary>
/// Process data: what the device publishes and what it accepts, the SYNC it reacts to, and the
/// emergency objects it sends. Reading the communication and mapping parameters first is not
/// ceremony — a TPDO is observed at the COB-ID 1800h says it uses, and a run that assumed the
/// default would report a working device as silent the moment anyone had remapped it.
/// </summary>
internal static class PdoSyncEmcyChecks
{
    /// <summary>How many TPDOs/RPDOs to look at. CiA 301 gives four of each as the default set;
    /// a device may have up to 512, and reading all of them is a different tool's job.</summary>
    private const int PdoCount = 4;

    /// <summary>
    /// The frames the tap saw that the target actually transmitted: remote-transmission requests
    /// are another node asking for a PDO, not the target producing one, and must not be counted
    /// as its traffic in either direction.
    /// </summary>
    private static List<TappedFrame> Transmitted(BusTap tap)
        => tap.Frames.Where(f => !f.IsRemote).ToList();

    /// <summary>How long after an NMT Stop a frame may still be one that was already in flight.
    /// Frames inside it are reported, not judged; frames after it are the device's fault.</summary>
    private const int SettleMs = 200;

    public static async Task RunAsync(Session session, Checkbook book, CancellationToken cancellationToken)
    {
        var tpdos = new List<PdoDescriptor>();
        var rpdos = new List<PdoDescriptor>();
        // PDO-01's problems have to reach SYN-02's prerequisite guard. Copying them only on
        // SYN-02's own sweep meant that whenever the pdo group *had* run, an unread mandatory
        // 1800h:02 was dropped and read as "no cyclic synchronous TPDO exists". Found in review
        // of #388.
        var tpdoProblems = new List<string>();

        await book.RunAsync("PDO-01", "pdo", "TPDO communication and mapping parameters (1800h/1A00h)", Tier.Read, "FR-CO-005", async () =>
        {
            var found = await ReadPdosAsync(session, 0x1800, 0x1A00, cancellationToken).ConfigureAwait(false);
            tpdos.AddRange(found.Descriptors);
            tpdoProblems.AddRange(found.Problems);
            return Summarise("TPDO", found, session);
        }).ConfigureAwait(false);

        await book.RunAsync("PDO-02", "pdo", "RPDO communication and mapping parameters (1400h/1600h)", Tier.Read, "FR-CO-005", async () =>
        {
            var found = await ReadPdosAsync(session, 0x1400, 0x1600, cancellationToken).ConfigureAwait(false);
            rpdos.AddRange(found.Descriptors);
            return Summarise("RPDO", found, session);
        }).ConfigureAwait(false);

        await book.RunAsync("PDO-03", "pdo", "TPDOs are transmitted in Operational and not in Stopped", Tier.Write, "FR-CO-022", async () =>
        {
            var valid = tpdos.Where(p => p.Valid).ToList();
            if (valid.Count == 0)
            {
                return tpdos.Count == 0
                    ? Outcome.NeedsSetup("PDO-01 read no TPDO parameters, so there is no COB-ID to watch.")
                    : Outcome.NotApplicable($"All {tpdos.Count} TPDO(s) the device declares are marked invalid (1800h:01 bit 31).");
            }

            var watched = valid.Select(p => (p.CobId, p.Extended)).ToHashSet();
            var initialState = await ObservedNmtStateAsync(session, cancellationToken).ConfigureAwait(false);
            // An RTR frame on a TPDO's COB-ID is another master asking for it, not the target
            // sending it. Counting one as the target's traffic failed a conforming device in the
            // stopped window and made a silent one look busy in the operational one. The tap keeps
            // IsRemote, so the filter is here rather than in the counting below. Found in review
            // of #388.
            using var tap = new BusTap(session.Service, (id, extended) => watched.Contains((id, extended)));

            TappedFrame[] inOperational;
            int inStopped;
            int settling;
            Restoration restoration;
            try
            {
                // Operational is the state PDOs flow in; Stopped is the state they must not.
                // Checking only the first would pass a device that transmits regardless of its NMT
                // state, which is the failure that matters on a machine being switched off.
                await session.Client.SendNmtCommandAsync(NmtCommand.Start, session.Target, cancellationToken).ConfigureAwait(false);
                await tap.ListenAsync(session.Options.PdoWindowMs, cancellationToken).ConfigureAwait(false);
                inOperational = Transmitted(tap).ToArray();

                await session.Client.SendNmtCommandAsync(NmtCommand.Stop, session.Target, cancellationToken).ConfigureAwait(false);
                var atStop = Transmitted(tap).Count;
                await Task.Delay(SettleMs, cancellationToken).ConfigureAwait(false);
                settling = Transmitted(tap).Count - atStop;
                await tap.ListenAsync(Math.Min(2000, session.Options.PdoWindowMs), cancellationToken).ConfigureAwait(false);
                inStopped = Transmitted(tap).Count - atStop - settling;
            }
            finally
            {
                // In a finally and on CancellationToken.None because a Ctrl+C between the Start
                // and the Stop above would otherwise walk away from a machine this check had just
                // stopped.
                restoration = await RestoreNmtStateAsync(session, book, "PDO-03", initialState).ConfigureAwait(false);
            }

            var measurements = new Dictionary<string, string>
            {
                ["TPDOs watched"] = string.Join(", ", valid.Select(p => $"TPDO{p.Number} 0x{p.CobId:X3}")),
                ["frames in Operational"] = $"{inOperational.Length} in {session.Options.PdoWindowMs} ms",
                ["frames in Stopped"] = inStopped.ToString(CultureInfo.InvariantCulture),
                ["frames within the first " + SettleMs + " ms of Stop"] = settling.ToString(CultureInfo.InvariantCulture),
                ["left in state"] = restoration.State,
            };

            foreach (var pdo in valid)
            {
                var mine = inOperational.Where(f => f.CanId == pdo.CobId && f.IsExtended == pdo.Extended).ToArray();
                if (mine.Length == 0)
                {
                    measurements[$"TPDO{pdo.Number} (0x{pdo.CobId:X3})"] =
                        $"silent; transmission type {DescribeTransmission(pdo.TransmissionType)}";
                    continue;
                }

                var summary = BusTap.Summarise(mine);
                var expected = pdo.EventTimerMs > 0 ? $", event timer 1800h:05 = {pdo.EventTimerMs} ms" : string.Empty;
                measurements[$"TPDO{pdo.Number} (0x{pdo.CobId:X3})"] = summary is { } s
                    ? $"{s}; type {DescribeTransmission(pdo.TransmissionType)}{expected}"
                    : $"{mine.Length} frame(s), {pdo.Bytes} B mapped; type {DescribeTransmission(pdo.TransmissionType)}{expected}";

                var wrongLength = mine.Where(f => pdo.Bytes > 0 && f.Data.Length != pdo.Bytes).ToArray();
                if (wrongLength.Length > 0)
                {
                    measurements[$"TPDO{pdo.Number} length"] =
                        $"mapping says {pdo.Bytes} B, {wrongLength.Length} frame(s) carried {string.Join("/", wrongLength.Select(f => f.Data.Length).Distinct())} B";
                }
            }

            if (!restoration.Succeeded)
            {
                return Outcome.Failed(Attribution.Unknown,
                    "The check itself completed, but the device could not be put back into the state it was found in: "
                    + restoration.State + ". A write-tier run promises the device is unchanged afterwards, so this "
                    + "fails whatever the measurements say — the device needs looking at before it is trusted again.",
                    measurements);
            }

            if (inStopped > 0)
            {
                return Outcome.Fail(
                    $"{inStopped} TPDO frame(s) were transmitted more than {SettleMs} ms after the device was Stopped. "
                    + "CiA 301 allows process data only in Operational.", measurements);
            }

            // A frame already on the wire when Stop arrived is not a violation; one the device
            // started afterwards is. The window cannot tell them apart, so it is reported rather
            // than judged — but it is no longer invisible, which it was while the baseline was
            // taken after the delay.
            if (settling > 0)
            {
                return Outcome.Inconclusive(
                    $"No TPDO arrived more than {SettleMs} ms after Stop, but {settling} did within it. A frame already "
                    + "in flight when the command landed explains that, and so does a device that stops a moment late; "
                    + "this window cannot separate the two.", Attribution.Unknown, measurements);
            }

            var mismatched = valid.Any(p => inOperational.Any(
                f => f.CanId == p.CobId && f.IsExtended == p.Extended && p.Bytes > 0 && f.Data.Length != p.Bytes));
            if (mismatched)
            {
                return Outcome.Fail(
                    "A TPDO carried a payload of a different length than its own mapping (1A00h) declares.", measurements);
            }

            if (inOperational.Length == 0)
            {
                // Event-driven TPDOs only transmit when their data changes, and a synchronous one
                // waits for a SYNC this check does not send. Silence is therefore not a fault.
                var synchronous = valid.Where(p => CanOpenTransmissionType.IsSynchronous(p.TransmissionType)).ToArray();
                return Outcome.Inconclusive(
                    $"No TPDO was transmitted in {session.Options.PdoWindowMs} ms of Operational. "
                    + (synchronous.Length > 0
                        ? $"{synchronous.Length} of them are synchronous and wait for a SYNC — see the sync group."
                        : "Event-driven TPDOs transmit when their mapped data changes, so a static device is legitimately silent."),
                    Attribution.Unknown, measurements);
            }

            return Outcome.Pass(
                $"{inOperational.Length} TPDO frame(s) in Operational, none in Stopped.", measurements);
        }).ConfigureAwait(false);

        await book.RunAsync("SYN-01", "sync", "1005h/1006h SYNC parameters", Tier.Read, "FR-CO-010", async () =>
        {
            if (!session.MayTransfer(0x1005, 0x00))
                return Outcome.NeedsSetup(session.GateReason(0x1005, 0x00));

            try
            {
                var raw = await session.Client.SdoUploadAsync(session.Target, 0x1005, 0x00, cancellationToken).ConfigureAwait(false);
                if (raw.Length != 4)
                    return Outcome.Fail($"1005h:00 is UNSIGNED32, the device returned {IdentityAndSdoChecks.Hex(raw)}.");

                var cobId = BitConverter.ToUInt32(raw, 0);
                var measurements = new Dictionary<string, string>
                {
                    ["1005h COB-ID SYNC"] = $"0x{cobId:X8} (id 0x{cobId & CanOpenCobId.CanIdMask:X3})",
                    ["generates SYNC"] = ((cobId & CanOpenCobId.SyncGenerateBit) != 0).ToString(),
                };

                if (session.MayTransfer(0x1006, 0x00))
                {
                    var period = await session.Client.SdoUploadAsync(session.Target, 0x1006, 0x00, cancellationToken).ConfigureAwait(false);
                    measurements["1006h communication cycle period"] = period.Length == 4
                        ? $"{BitConverter.ToUInt32(period, 0)} us"
                        : IdentityAndSdoChecks.Hex(period);
                }

                return Outcome.Pass("SYNC parameters read.", measurements);
            }
            catch (SdoAbortException abort) when (abort.AbortCode is (uint)SdoAbortCode.ObjectDoesNotExist)
            {
                return Outcome.NotApplicable("The device has no 1005h; SYNC is optional in CiA 301.");
            }
        }).ConfigureAwait(false);

        await book.RunAsync("SYN-02", "sync", "Synchronous TPDOs answer the SYNCs this tool produces", Tier.Write, "FR-CO-010", async () =>
        {
            // Transmission type 1..240 means "on every Nth SYNC", so the number of SYNCs to send
            // is the device's, not a constant. An earlier version sent five regardless and failed
            // any device with a type above 5 — a false FAIL from a conformance tool, which is the
            // worst outcome it has. Type 0 is separated out for the same reason: a synchronous
            // *acyclic* TPDO transmits after a SYNC only when the application has data pending, so
            // its silence is not the device's fault and cannot be concluded on.
            // The pdo group may not have run — --groups presents them as independently selectable —
            // so the prerequisite is read here rather than reported as a setup skip under every
            // sync-only invocation. Found in review of #388.
            var known = tpdos;
            var prerequisiteProblems = new List<string>(tpdoProblems);
            if (known.Count == 0)
            {
                var sweep = await ReadPdosAsync(session, 0x1800, 0x1A00, cancellationToken).ConfigureAwait(false);
                known = sweep.Descriptors;
                prerequisiteProblems.AddRange(sweep.Problems);
            }

            // A descriptor whose transmission type could not be read cannot be classified, so the
            // prerequisite is unresolved rather than the feature absent.
            // Any unread transmission type, not only all of them: a descriptor whose 1800h:02 did
            // not come back is filtered out of the classification below, so SYN-02 would otherwise
            // pass having tested the readable TPDOs while an omitted one may be cyclic and silent.
            // Found in review of #388.
            var unreadTypes = known.Where(p => p.Valid && !p.TransmissionTypeRead).ToList();
            if (unreadTypes.Count > 0)
            {
                return Outcome.Inconclusive(
                    $"{unreadTypes.Count} of {known.Count(p => p.Valid)} valid TPDO(s) would not return 1800h:02, so "
                    + "whether they are synchronous is unknown and testing only the rest would report a coverage this "
                    + "run does not have"
                    + (prerequisiteProblems.Count > 0 ? ": " + string.Join("; ", prerequisiteProblems.Take(5)) : string.Empty)
                    + ".",
                    Attribution.Device,
                    new Dictionary<string, string>
                    {
                        ["TPDOs with an unread transmission type"] = string.Join(", ", unreadTypes.Select(p => $"TPDO{p.Number}")),
                        ["prerequisite problems"] = prerequisiteProblems.Count > 0 ? string.Join("; ", prerequisiteProblems) : "(none reported)",
                    });
            }

            // Only PDOs whose transmission type was actually read: a default standing in for a
            // failed mandatory read must not decide which ones are synchronous.
            var cyclic = known
                .Where(p => p.Valid && p.TransmissionTypeRead
                            && p.TransmissionType >= CanOpenTransmissionType.SynchronousEverySync
                            && p.TransmissionType <= CanOpenTransmissionType.SynchronousCyclicMax)
                .ToList();
            var acyclic = known
                .Where(p => p.Valid && p.TransmissionTypeRead
                            && p.TransmissionType == CanOpenTransmissionType.SynchronousAcyclic)
                .ToList();

            if (cyclic.Count == 0)
            {
                if (known.Count == 0) return Outcome.NeedsSetup("No TPDO communication parameters could be read; see PDO-01.");
                return acyclic.Count > 0
                    ? Outcome.Inconclusive(
                        $"{acyclic.Count} TPDO(s) are synchronous acyclic (1800h:02 = 0): they transmit after a SYNC "
                        + "only when the application has data pending, so nothing can be concluded from producing SYNCs. "
                        + "No cyclic synchronous TPDO (type 1..240) exists to measure.",
                        Attribution.None)
                    : Outcome.NotApplicable(
                        $"None of the {known.Count} TPDO(s) read has a cyclic synchronous transmission type "
                        + "(1800h:02 in 1..240), so a SYNC changes nothing observable.");
            }

            // Enough SYNCs for the slowest of them to have had two opportunities, plus a margin,
            // and bounded so that a type-240 TPDO does not turn this into a minutes-long check.
            var overflow = await TargetSyncOverflowAsync(session, cancellationToken).ConfigureAwait(false);
            if (overflow is not { } counterOverflow)
            {
                return Outcome.Inconclusive(
                    "1019h is readable in principle but did not come back usable, so whether this device's SYNC carries "
                    + "a counter is unknown. Sending the wrong form would fail it for silence, so nothing was sent.",
                    Attribution.Device);
            }

            var slowest = cyclic.Max(p => (int)p.TransmissionType);
            var highestStart = cyclic.Max(p => (int)p.SyncStartValue);

            // A TPDO with 1800h:06 set does not begin until the SYNC counter reaches that value,
            // so the counter has to get there before any of this measures the device rather than
            // the run. Two full counter cycles plus the slowest type's own two opportunities.
            // Found in review of #388: a type-1 TPDO with start value 240 received four SYNCs and
            // legitimately emitted nothing, and was failed for it.
            var forStart = highestStart > 0 && counterOverflow >= 2 ? counterOverflow * 2 + slowest * 2 : 0;
            var syncs = Math.Min(600, Math.Max(slowest * 2 + 2, forStart));
            const int SyncPeriodMs = 20;

            // The SYNC goes on the COB-ID the *target* listens to, not this tool's own. The client
            // node's SendSyncAsync uses its own 1005h — the 080h default — so a device configured
            // for another SYNC COB-ID received none of these frames and was then failed for
            // silence. Found in review of #388.
            var syncCobId = await TargetSyncCobIdAsync(session, cancellationToken).ConfigureAwait(false);
            if (syncCobId is not { } sync)
            {
                return Outcome.NeedsSetup(
                    "The target's 1005h could not be read, so there is no way to know which COB-ID its synchronous "
                    + "TPDOs are waiting on. See SYN-01.");
            }

            byte counter = 0;
            var watched = cyclic.Select(p => (p.CobId, p.Extended)).ToHashSet();
            var initialState = await ObservedNmtStateAsync(session, cancellationToken).ConfigureAwait(false);
            using var tap = new BusTap(session.Service, (id, extended) => watched.Contains((id, extended)));
            int quiet;
            Restoration restoration;
            var rejected = 0;
            try
            {
                await session.Client.SendNmtCommandAsync(NmtCommand.Start, session.Target, cancellationToken).ConfigureAwait(false);
                await Task.Delay(300, cancellationToken).ConfigureAwait(false);

                quiet = Transmitted(tap).Count;
                for (var i = 0; i < syncs; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // 1019h in 2..240 means the device's SYNC carries a one-byte counter, and a
                    // conforming consumer rejects a zero-length frame outright — the stack's own
                    // TryAcceptSync enforces that DLC rule. Sending DLC 0 regardless failed a
                    // conforming device for silence. Found in review of #388.
                    var payload = counterOverflow >= 2
                        ? new[] { counter = (byte)(counter % counterOverflow + 1) }
                        : Array.Empty<byte>();
                    var syncFrame = CanFrame.Classic((int)sync.CobId, payload, isExtendedFrame: sync.Extended);
                    if (session.Bus.Transmit(syncFrame) <= 0) rejected++;
                    await Task.Delay(SyncPeriodMs, cancellationToken).ConfigureAwait(false);
                }

                // The last SYNC's answer is still in flight when the loop ends.
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                restoration = await RestoreNmtStateAsync(session, book, "SYN-02", initialState).ConfigureAwait(false);
            }

            var accepted = syncs - rejected;
            if (accepted == 0)
            {
                return Outcome.TransportFault(
                    $"The driver rejected all {syncs} SYNC frames on 0x{sync.CobId:X3}, so the device was never synchronised.");
            }

            var after = Transmitted(tap).Skip(quiet).ToArray();
            var measurements = new Dictionary<string, string>
            {
                ["SYNC counter (1019h)"] = counterOverflow >= 2 ? $"overflow {counterOverflow}, one-byte counter sent" : "none, DLC 0",
                ["highest SYNC start value (1800h:06)"] = highestStart == 0
                    ? "none"
                    : counterOverflow >= 2
                        ? $"{highestStart}; {syncs} SYNCs cover it"
                        : $"{highestStart}, but 1019h reports no counter — a start value without a counter has nothing to start on",
                ["SYNCs sent"] = $"{syncs} on 0x{sync.CobId:X3}{(sync.Extended ? " (29-bit)" : string.Empty)} at {SyncPeriodMs} ms (slowest cyclic type is {slowest})"
                    + (rejected > 0 ? $"; the driver rejected {rejected}" : string.Empty),
                ["frames before the SYNCs"] = quiet.ToString(CultureInfo.InvariantCulture),
                ["left in state"] = restoration.State,
            };
            if (acyclic.Count > 0)
            {
                measurements["acyclic TPDOs not concluded on"] = string.Join(", ",
                    acyclic.Select(p => $"TPDO{p.Number} 0x{p.CobId:X3} type 0"));
            }

            if (!restoration.Succeeded)
            {
                return Outcome.Failed(Attribution.Unknown,
                    "The check itself completed, but the device could not be put back into the state it was found in: "
                    + restoration.State + ". A write-tier run promises the device is unchanged afterwards, so this "
                    + "fails whatever the measurements say — the device needs looking at before it is trusted again.",
                    measurements);
            }

            var silent = new List<string>();
            var unmeasured = new List<string>();
            foreach (var pdo in cyclic)
            {
                var mine = after.Where(f => f.CanId == pdo.CobId && f.IsExtended == pdo.Extended).ToArray();
                var opportunities = accepted / pdo.TransmissionType;
                measurements[$"TPDO{pdo.Number} (0x{pdo.CobId:X3}) type {pdo.TransmissionType}"] =
                    $"{mine.Length} frame(s) for {opportunities} SYNC opportunity/opportunities";
                if (mine.Length > 0) continue;

                // Silence only counts against the device once it has had an opportunity. The SYNC
                // count is capped, and a transmission type above that cap would otherwise be
                // failed for not transmitting on a SYNC that was never sent — the same false FAIL
                // the fixed five-SYNC constant produced, re-entered through the bound.
                var startUnreached = pdo.SyncStartValue > 0 && (counterOverflow < 2 || accepted < counterOverflow);
                if (startUnreached)
                {
                    unmeasured.Add($"TPDO{pdo.Number} (0x{pdo.CobId:X3}, 1800h:06 start value {pdo.SyncStartValue} "
                        + "was not reached by this run's counter)");
                }
                else if (opportunities >= 1)
                {
                    silent.Add($"TPDO{pdo.Number} (0x{pdo.CobId:X3}, type {pdo.TransmissionType})");
                }
                else
                {
                    unmeasured.Add($"TPDO{pdo.Number} (0x{pdo.CobId:X3}, type {pdo.TransmissionType} > {accepted} SYNCs accepted)");
                }
            }

            if (unmeasured.Count > 0) measurements["not measured (fewer SYNCs sent than the type asks for)"] = string.Join(", ", unmeasured);

            // A partial rejection makes the count the device actually saw uncertain, so silence
            // cannot be charged to it. Found in review of #388.
            if (silent.Count > 0 && rejected > 0)
            {
                return Outcome.Inconclusive(
                    $"{string.Join(", ", silent)} transmitted nothing, but the driver rejected {rejected} of {syncs} "
                    + "SYNC frames, so how many the device actually saw is uncertain and its silence cannot be charged "
                    + "to it. Re-run on an interface that accepts every frame.", Attribution.Transport, measurements);
            }

            if (silent.Count > 0)
            {
                return Outcome.Fail(
                    $"{string.Join(", ", silent)} transmitted nothing although {accepted} SYNC object(s) reached the bus, which is "
                    + "more than the transmission type asks for. A cyclic synchronous TPDO must transmit on its Nth SYNC.",
                    measurements);
            }

            var measured = cyclic.Count - unmeasured.Count;
            if (measured == 0)
            {
                return Outcome.Inconclusive(
                    $"Every cyclic synchronous TPDO has a transmission type above the {accepted} SYNCs that reached the bus, "
                    + "so none of them was given an opportunity.", Attribution.Setup, measurements);
            }

            return Outcome.Pass(
                $"{measured} of {cyclic.Count} cyclic synchronous TPDO(s) answered: {after.Length} frame(s) for {accepted} SYNC(s)."
                + (unmeasured.Count > 0 ? $" {unmeasured.Count} was/were not measured." : string.Empty),
                measurements);
        }).ConfigureAwait(false);

        await book.RunAsync("EMC-01", "emcy", "Emergency objects on 80h+node-id", Tier.Read, "FR-CO-011", async () =>
        {
            // Passive only. There is no way to make a conforming device raise an emergency on
            // request, so this records what it sent in the window — and says so, rather than
            // reporting silence as a pass.
            var cobId = session.Options.TargetNode is { } target ? CanOpenCobId.Emcy(target) : 0u;
            using var tap = new BusTap(session.Service,
                (id, extended) => !extended && (session.Options.TargetNode is null
                    ? id > CanOpenCobId.EmcyBase && id < CanOpenCobId.EmcyBase + 128
                    : id == cobId));

            var decoded = new List<string>();
            void OnEmcy(object? sender, EmcyReceivedEventArgs e)
            {
                var m = e.Message;
                lock (decoded)
                {
                    decoded.Add($"node {m.ProducerNodeId}: error 0x{m.ErrorCode:X4}, register 0x{m.ErrorRegister:X2}, "
                        + $"manufacturer {IdentityAndSdoChecks.Hex(m.ManufacturerSpecific)}");
                }
            }

            session.Client.EmcyReceived += OnEmcy;
            try
            {
                await tap.ListenAsync(session.Options.ListenMs, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                session.Client.EmcyReceived -= OnEmcy;
            }

            var frames = tap.Frames;
            var measurements = new Dictionary<string, string>
            {
                ["window"] = $"{session.Options.ListenMs} ms",
                ["frames"] = frames.Count.ToString(CultureInfo.InvariantCulture),
                ["decoded by the stack"] = decoded.Count.ToString(CultureInfo.InvariantCulture),
            };
            foreach (var (text, i) in decoded.Take(10).Select((t, i) => (t, i)))
                measurements[$"emergency {i + 1}"] = text;

            if (frames.Count == 0)
            {
                return Outcome.Inconclusive(
                    $"No emergency object in {session.Options.ListenMs} ms. That is the normal state of a healthy "
                    + "device and not evidence that it can produce one — this check cannot provoke an emergency.",
                    Attribution.None, measurements);
            }

            var malformed = frames.Count(f => f.Data.Length != 8);
            if (malformed > 0) measurements["payload not 8 bytes"] = malformed.ToString(CultureInfo.InvariantCulture);

            // Any malformed frame, not only all of them — the same correction as HBT-02. An
            // emergency object is eight bytes; a frame on this COB-ID with another length is a
            // violation the report must not let a valid neighbour cover for.
            if (malformed > 0)
            {
                return Outcome.Fail(
                    malformed == frames.Count
                        ? $"All {frames.Count} frame(s) on the emergency COB-ID have a payload other than 8 bytes."
                        : $"{malformed} of {frames.Count} frame(s) on the emergency COB-ID have a payload other than "
                          + "8 bytes. An emergency object is always 8.",
                    measurements);
            }

            return decoded.Count == 0
                ? Outcome.ToolFault(
                    $"{frames.Count} well-formed emergency frame(s) were on the bus and the stack decoded none of them.",
                    measurements)
                : Outcome.Pass($"{decoded.Count} emergency object(s) received and decoded.", measurements);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Puts the device back in pre-operational, whatever happened above. Never cancellable: this
    /// is the step that stops a conformance run from leaving a machine stopped, so it must run on
    /// the way out of a cancelled check as much as a successful one. A failure here is recorded
    /// rather than thrown — it would otherwise replace the finding the check had just reached.
    /// </summary>
    private static async Task<Restoration> RestoreNmtStateAsync(Session session, Checkbook book, string checkId, byte? initial)
    {
        // The state the device was found in, not a fixed one. An earlier version always left
        // pre-operational, so a --tier write run against a device found Operational stopped it
        // from operating even when every check passed — the opposite of what the tier promises.
        // Pre-operational remains the fallback for a device whose state could not be read, because
        // it is the state a CANopen device powers up in. Found in review of #388.
        var command = initial switch
        {
            (byte)NmtState.Operational => NmtCommand.Start,
            (byte)NmtState.Stopped => NmtCommand.Stop,
            _ => NmtCommand.EnterPreOperational,
        };
        var name = initial switch
        {
            (byte)NmtState.Operational => "operational",
            (byte)NmtState.Stopped => "stopped",
            (byte)NmtState.PreOperational => "pre-operational",
            _ => "pre-operational (the state before the check could not be read)",
        };

        try
        {
            await session.Client.SendNmtCommandAsync(command, session.Target, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            book.Note($"{checkId} restore of the NMT state", ex);
            return Restoration.Broke(
                $"UNKNOWN — the restore to {name} failed ({ex.GetType().Name}); the device may still be stopped");
        }

        // Sending is not arriving. SendNmtCommandAsync hands out a Task rather than the
        // Task<bool> underneath it, so a frame the adapter rejected completes this await normally
        // and only the background-exception event hears about it — and an earlier version returned
        // Ok on exactly that. The state is verified against the heartbeat instead. Found in review
        // of #388.
        var expected = command switch
        {
            NmtCommand.Start => (byte)NmtState.Operational,
            NmtCommand.Stop => (byte)NmtState.Stopped,
            _ => (byte)NmtState.PreOperational,
        };

        // Wait for the *latest* heartbeat to carry the commanded state, not merely for one to
        // arrive: a beat already on the wire when the command landed still reports the state
        // before it, and taking that as the answer turned a good restore into a failed check.
        // Found in review of #388.
        var reached = await LatestNmtStateAsync(session, expected, CancellationToken.None).ConfigureAwait(false);
        if (reached is null)
        {
            // No heartbeat means no way to observe the NMT state from outside. That is a real
            // limit, not a pass: it is reported as unverified rather than claimed as restored.
            return Restoration.Ok($"{name} (commanded, not verified — the device produces no heartbeat to confirm it)");
        }

        return reached == expected
            ? Restoration.Ok(name)
            : Restoration.Broke(
                $"the device still reports {NmtAndHeartbeatChecks.DescribeState(reached.Value)} after being commanded "
                + $"back to {name}; the restore did not take effect");
    }

    /// <summary>
    /// The device's NMT state as its heartbeat reports it, or null when it produces none. Used to
    /// put the state back after a check that changes it.
    /// </summary>
    private static async Task<byte?> ObservedNmtStateAsync(Session session, CancellationToken cancellationToken)
    {
        var cobId = CanOpenCobId.Heartbeat(session.Target);
        using var probe = new BusTap(session.Service, (id, extended) => id == cobId && !extended);
        // The operator's whole window, not half of it: a producer whose period HBT-02 accepted
        // could be missed here, and the state would then be "unknown" and the device put back to
        // pre-operational though it had been Operational — the restore defeating itself. It
        // returns on the first frame, so a fast producer costs nothing. Found in review of #388.
        await probe.WaitForAsync(frames => frames.Count >= 1, Math.Max(500, session.Options.HeartbeatWindowMs),
            cancellationToken).ConfigureAwait(false);
        var frames = probe.Frames;
        for (var i = frames.Count - 1; i >= 0; i--)
            if (frames[i].Data.Length == 1) return frames[i].Data[0];
        return null;
    }

    /// <summary>
    /// The COB-ID the target's 1005h says its SYNC arrives on, masked to the identifier; the
    /// 080h default when the object is absent or the gate blocks it, and null when it answered
    /// with something unusable. Bit 30 of that word is the device's own produce flag and says
    /// nothing about which identifier it consumes.
    /// </summary>
    private static async Task<(uint CobId, bool Extended)?> TargetSyncCobIdAsync(Session session, CancellationToken cancellationToken)
    {
        if (!session.MayTransfer(0x1005, 0x00)) return (CanOpenCobId.Sync, false);
        try
        {
            var raw = await session.Client.SdoUploadAsync(session.Target, 0x1005, 0x00, cancellationToken).ConfigureAwait(false);
            if (raw.Length != 4) return null;
            var word = BitConverter.ToUInt32(raw, 0);
            // The frame format travels with the identifier. Sending a 29-bit SYNC as an 11-bit
            // frame means a device consuming the extended one hears nothing, and a large
            // identifier does not fit the 11-bit frame at all. Found in review of #388.
            return (word & CanOpenCobId.ExtendedFrameBit) != 0
                ? (word & 0x1FFF_FFFFu, true)
                : (word & CanOpenCobId.CanIdMask, false);
        }
        catch (SdoAbortException abort) when (abort.AbortCode is (uint)SdoAbortCode.ObjectDoesNotExist
                                                  or (uint)SdoAbortCode.SubIndexDoesNotExist)
        {
            // No 1005h at all means the default, which is what such a device uses.
            return (CanOpenCobId.Sync, false);
        }
        catch (SdoAbortException)
        {
            // A declared 1005h that will not be read leaves the prerequisite unresolved. Guessing
            // the default here sent every SYNC to the wrong identifier and then failed the
            // device's cyclic TPDOs for silence. Found in review of #388.
            return null;
        }
    }

    /// <summary>
    /// Waits for the newest heartbeat to report <paramref name="expected"/> and returns the state
    /// it ends on, or null when the device produced no heartbeat at all. Distinct from
    /// <see cref="ObservedNmtStateAsync"/>, which takes the first frame it sees: that is right for
    /// reading the state *before* a check and wrong for confirming a command, because the frame in
    /// flight when the command arrived still carries the old state.
    /// </summary>
    private static async Task<byte?> LatestNmtStateAsync(Session session, byte expected, CancellationToken cancellationToken)
    {
        var cobId = CanOpenCobId.Heartbeat(session.Target);
        using var probe = new BusTap(session.Service, (id, extended) => id == cobId && !extended);
        await probe.WaitForAsync(
            frames => frames.LastOrDefault(f => f.Data.Length == 1) is { Data.Length: 1 } last && last.Data[0] == expected,
            Math.Max(1000, session.Options.HeartbeatWindowMs), cancellationToken).ConfigureAwait(false);

        var frames = probe.Frames;
        for (var i = frames.Count - 1; i >= 0; i--)
            if (frames[i].Data.Length == 1) return frames[i].Data[0];
        return null;
    }

    /// <summary>
    /// 1019h, the SYNC counter overflow. A value of 2..240 means the device's SYNC carries a
    /// one-byte counter that rolls 1..value; 0 or 1 (and an absent object) mean no counter.
    /// </summary>
    private static async Task<byte?> TargetSyncOverflowAsync(Session session, CancellationToken cancellationToken)
    {
        // No 1019h to read means no counter, which is the CiA 301 default.
        if (!session.MayTransfer(0x1019, 0x00)) return 0;
        try
        {
            var raw = await session.Client.SdoUploadAsync(session.Target, 0x1019, 0x00, cancellationToken).ConfigureAwait(false);
            // A wrong length is the device contradicting itself about an UNSIGNED8, not a zero.
            return raw.Length == 1 ? raw[0] : null;
        }
        catch (SdoAbortException abort) when (abort.AbortCode is (uint)SdoAbortCode.ObjectDoesNotExist
                                                  or (uint)SdoAbortCode.SubIndexDoesNotExist)
        {
            return 0;
        }
        catch (SdoAbortException)
        {
            // Anything else leaves it unknown, and guessing "no counter" would send DLC 0 to a
            // device that rejects it and then fail that device for silence. Found in review of
            // #388 — the same mistake as defaulting 1005h, one object along.
            return null;
        }
    }

    /// <summary>One PDO as its own parameters describe it.</summary>
    private readonly record struct PdoDescriptor(
        int Number, uint CobId, bool Extended, bool Valid, byte TransmissionType, bool TransmissionTypeRead,
        byte SyncStartValue, int InhibitMs, int EventTimerMs, int Bytes, int MappedEntries);

    private readonly record struct PdoSweep(List<PdoDescriptor> Descriptors, List<string> Blocked, List<string> Absent, List<string> Problems);

    /// <summary>
    /// Reads the communication and mapping parameters of up to <see cref="PdoCount"/> PDOs. The
    /// COB-ID comes from the device rather than from <see cref="CanOpenCobId.TpdoDefault"/>,
    /// because a remapped device is exactly the case a default would get wrong.
    /// </summary>
    private static async Task<PdoSweep> ReadPdosAsync(Session session, ushort commBase, ushort mapBase, CancellationToken cancellationToken)
    {
        var descriptors = new List<PdoDescriptor>();
        var blocked = new List<string>();
        var absent = new List<string>();
        var problems = new List<string>();

        for (var i = 0; i < PdoCount; i++)
        {
            var comm = (ushort)(commBase + i);
            var map = (ushort)(mapBase + i);
            if (!session.MayTransfer(comm, 0x01))
            {
                blocked.Add($"0x{comm:X4}");
                continue;
            }

            uint cobIdWord;
            try
            {
                var raw = await session.Client.SdoUploadAsync(session.Target, comm, 0x01, cancellationToken).ConfigureAwait(false);
                if (raw.Length != 4)
                {
                    problems.Add($"0x{comm:X4}:01 is UNSIGNED32, got {IdentityAndSdoChecks.Hex(raw)}");
                    continue;
                }

                cobIdWord = BitConverter.ToUInt32(raw, 0);
            }
            catch (SdoAbortException abort) when (abort.AbortCode is (uint)SdoAbortCode.ObjectDoesNotExist or (uint)SdoAbortCode.SubIndexDoesNotExist)
            {
                absent.Add($"0x{comm:X4}");
                continue;
            }

            // 1800h:02 / 1400h:02 is mandatory once the record exists; 03 and 05 are optional.
            var transmission = await ByteOrDefault(session, comm, 0x02, problems, mandatory: true, cancellationToken).ConfigureAwait(false);
            var inhibit = await UShortOrDefault(session, comm, 0x03, problems, cancellationToken).ConfigureAwait(false);
            var eventTimer = await UShortOrDefault(session, comm, 0x05, problems, cancellationToken).ConfigureAwait(false);
            // 1800h:06 is the SYNC start value, optional: the counter value at which this TPDO
            // begins its cycle (CiA 301 §7.5.2.37).
            var syncStart = await ByteOrDefault(session, comm, 0x06, problems, mandatory: false, cancellationToken).ConfigureAwait(false);

            var (bits, entries) = await ReadMappingAsync(session, map, problems, cancellationToken).ConfigureAwait(false);

            // Bit 29 of the COB-ID word selects the 29-bit frame format (CiA 301 §7.5.2.37).
            // Masking with the 11-bit CanIdMask regardless threw most of an extended identifier
            // away, so the check watched an unrelated base id and then failed a conforming PDO for
            // silence. Found in review of #388.
            var extended = (cobIdWord & CanOpenCobId.ExtendedFrameBit) != 0;
            var cobId = extended ? cobIdWord & 0x1FFF_FFFFu : cobIdWord & CanOpenCobId.CanIdMask;

            descriptors.Add(new PdoDescriptor(
                Number: i + 1,
                CobId: cobId,
                Extended: extended,
                Valid: (cobIdWord & CanOpenCobId.InvalidBit) == 0,
                TransmissionType: transmission ?? 0xFF,
                TransmissionTypeRead: transmission is not null,
                SyncStartValue: syncStart ?? 0,
                InhibitMs: inhibit is { } inh ? inh / 10 : 0, // 1800h:03 counts 100 us units
                EventTimerMs: eventTimer ?? 0,
                Bytes: (bits + 7) / 8,
                MappedEntries: entries));
        }

        return new PdoSweep(descriptors, blocked, absent, problems);
    }

    private static async Task<(int Bits, int Entries)> ReadMappingAsync(
        Session session, ushort map, List<string> problems, CancellationToken cancellationToken)
    {
        if (!session.MayTransfer(map, 0x00))
        {
            // Blocked, not absent. The gate path converted this to an empty mapping without a
            // word, which is the same false pass as swallowing an abort — one route further out.
            // Found in review of #388.
            problems.Add($"0x{map:X4} is the mandatory mapping record of a PDO whose communication record exists, and "
                + session.GateReason(map, 0x00));
            return (0, 0);
        }

        try
        {
            var count = await session.Client.SdoUploadAsync(session.Target, map, 0x00, cancellationToken).ConfigureAwait(false);
            if (count.Length != 1)
            {
                problems.Add($"0x{map:X4}:00 is UNSIGNED8, got {IdentityAndSdoChecks.Hex(count)}");
                return (0, 0);
            }


            var bits = 0;
            for (byte sub = 1; sub <= count[0] && sub <= 64; sub++)
            {
                if (!session.MayTransfer(map, sub)) continue;
                byte[] entry;
                try
                {
                    entry = await session.Client.SdoUploadAsync(session.Target, map, sub, cancellationToken).ConfigureAwait(false);
                }
                catch (SdoAbortException abort)
                {
                    // 1A00h:00 said this entry exists, so refusing it is a contradiction the
                    // device is responsible for — not a reason to report a shorter mapping.
                    problems.Add($"0x{map:X4}:{sub:X2} is within the {count[0]} entries 0x{map:X4}:00 declares and aborted "
                        + IdentityAndSdoChecks.Describe(abort));
                    continue;
                }

                if (entry.Length != 4)
                {
                    problems.Add($"0x{map:X4}:{sub:X2} is UNSIGNED32, got {IdentityAndSdoChecks.Hex(entry)}");
                    continue;
                }

                bits += entry[0]; // the low byte of a mapping entry is its bit length
            }

            return (bits, count[0]);
        }
        catch (SdoAbortException abort)
        {
            // Every abort is a problem here, absence included. This helper is reached only after
            // the PDO's communication record has been read, so the PDO exists — and CiA 301 makes
            // the mapping record mandatory for one that does. The round-6 narrowing treated the
            // absence codes as "no such PDO" and thereby moved the false pass rather than removing
            // it: a device with 1800h and no 1A00h was recorded with zero mapped bytes and no
            // problem, so PDO-01/PDO-02 passed and PDO-03 skipped the payload-length check for it.
            // Found in review of #388.
            problems.Add($"0x{map:X4} is the mapping record of a PDO whose communication record exists, and it aborted "
                + IdentityAndSdoChecks.Describe(abort));
            return (0, 0);
        }
    }

    /// <summary>
    /// Reads one byte of a PDO communication record. An abort is recorded in
    /// <paramref name="problems"/> rather than swallowed: 1800h:02 and 1400h:02 are mandatory for
    /// an implemented PDO, and an earlier version turned a failed read of one into the plausible
    /// default 0xFF, so PDO-01 and PDO-02 passed as though they had read it. Null means only "the
    /// gate blocks it" or "the device returned the wrong length", both of which are recorded too.
    /// </summary>
    private static async Task<byte?> ByteOrDefault(
        Session session, ushort index, byte sub, List<string> problems, bool mandatory, CancellationToken cancellationToken)
    {
        if (!session.MayTransfer(index, sub)) return null;
        try
        {
            var raw = await session.Client.SdoUploadAsync(session.Target, index, sub, cancellationToken).ConfigureAwait(false);
            if (raw.Length == 1) return raw[0];
            problems.Add($"0x{index:X4}:{sub:X2} is UNSIGNED8, got {IdentityAndSdoChecks.Hex(raw)}");
            return null;
        }
        catch (SdoAbortException abort)
        {
            if (mandatory)
                problems.Add($"0x{index:X4}:{sub:X2} is mandatory for an implemented PDO and aborted {IdentityAndSdoChecks.Describe(abort)}");
            return null;
        }
    }

    /// <summary>As <see cref="ByteOrDefault"/>, for the two-byte optional entries (inhibit time,
    /// event timer). Those are optional, so an abort is the device saying it has none.</summary>
    private static async Task<int?> UShortOrDefault(
        Session session, ushort index, byte sub, List<string> problems, CancellationToken cancellationToken)
    {
        if (!session.MayTransfer(index, sub)) return null;
        try
        {
            var raw = await session.Client.SdoUploadAsync(session.Target, index, sub, cancellationToken).ConfigureAwait(false);
            if (raw.Length == 2) return BitConverter.ToUInt16(raw, 0);
            problems.Add($"0x{index:X4}:{sub:X2} is UNSIGNED16, got {IdentityAndSdoChecks.Hex(raw)}");
            return null;
        }
        catch (SdoAbortException)
        {
            // 1800h:03 and 1800h:05 are optional; their absence is not a problem.
            return null;
        }
    }

    private static Outcome Summarise(string kind, PdoSweep sweep, Session session)
    {
        var measurements = new Dictionary<string, string>();
        foreach (var pdo in sweep.Descriptors)
        {
            measurements[$"{kind}{pdo.Number}"] =
                $"COB-ID 0x{pdo.CobId:X3}{(pdo.Extended ? " (29-bit)" : string.Empty)}{(pdo.Valid ? string.Empty : " (invalid)")}, "
                + $"type {(pdo.TransmissionTypeRead ? DescribeTransmission(pdo.TransmissionType) : "not read")}, "
                + $"{pdo.MappedEntries} entry/entries = {pdo.Bytes} B"
                + (pdo.EventTimerMs > 0 ? $", event timer {pdo.EventTimerMs} ms" : string.Empty)
                + (pdo.InhibitMs > 0 ? $", inhibit {pdo.InhibitMs} ms" : string.Empty);
        }

        if (sweep.Blocked.Count > 0) measurements["blocked by the peer-SDO gate"] = string.Join(", ", sweep.Blocked);
        if (sweep.Absent.Count > 0) measurements["not implemented"] = string.Join(", ", sweep.Absent);
        foreach (var (problem, i) in sweep.Problems.Take(10).Select((p, i) => (p, i)))
            measurements[$"problem {i + 1}"] = problem;

        if (sweep.Problems.Count > 0)
            return Outcome.Fail($"{sweep.Descriptors.Count} {kind}(s) read; {sweep.Problems.Count} parameter(s) have the wrong length.", measurements);

        if (sweep.Descriptors.Count == 0)
        {
            return sweep.Blocked.Count > 0
                ? Outcome.NeedsSetup($"Every {kind} parameter is blocked: {session.GateReason((ushort)(kind == "TPDO" ? 0x1800 : 0x1400), 0x01)}")
                : Outcome.NotApplicable($"The device implements none of the first {PdoCount} {kind}s.");
        }

        var valid = sweep.Descriptors.Count(p => p.Valid);
        return Outcome.Pass($"{sweep.Descriptors.Count} {kind}(s) read, {valid} valid.", measurements);
    }

    private static string DescribeTransmission(byte type) => type switch
    {
        CanOpenTransmissionType.SynchronousAcyclic => "0 (synchronous, acyclic)",
        <= CanOpenTransmissionType.SynchronousCyclicMax => $"{type} (synchronous, every {type} SYNC)",
        CanOpenTransmissionType.RtrOnlySynchronous => "252 (RTR only, synchronous)",
        CanOpenTransmissionType.RtrOnlyEventDriven => "253 (RTR only, event-driven)",
        CanOpenTransmissionType.EventDrivenManufacturer => "254 (event-driven, manufacturer)",
        CanOpenTransmissionType.EventDrivenProfile => "255 (event-driven, profile)",
        _ => $"{type} (reserved)",
    };
}
