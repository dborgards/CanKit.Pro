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
        => Transmitted(tap.Frames);

    /// <summary>As above, for a snapshot already bounded to an observation window.</summary>
    private static List<TappedFrame> Transmitted(IEnumerable<TappedFrame> frames)
        => frames.Where(f => !f.IsRemote).ToList();

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
            // Multiplexed TPDOs are excluded here, not only reported by PDO-01. An MPDO's payload
            // is a destination address and an index rather than the mapped process data (CiA 301
            // §7.2.4), so the length rule below does not apply to it and the report says MPDO is
            // not addressed — yet this filter took every valid descriptor, so PDO-03 watched them
            // as ordinary TPDOs and could return Pass from their traffic, contradicting the
            // report's own coverage section. Found in review of #388.
            var multiplexed = tpdos.Where(p => p.Valid && p.MultiplexedMode).ToList();
            var valid = tpdos.Where(p => p.Valid && !p.MultiplexedMode).ToList();
            if (valid.Count == 0 && multiplexed.Count == 0)
            {
                return tpdos.Count == 0
                    ? Outcome.NeedsSetup("PDO-01 read no TPDO parameters, so there is no COB-ID to watch.")
                    : Outcome.NotApplicable($"All {tpdos.Count} TPDO(s) the device declares are marked invalid (1800h:01 bit 31).");
            }

            // Both kinds, because the two rules this check applies do not have the same scope.
            // Only the mapped payload length is particular to an ordinary TPDO, and that is the
            // one an MPDO's payload — a destination address and an index — does not answer to.
            // "Nothing is transmitted in Stopped" applies to every PDO a device sends, and it is
            // the failure that matters on a machine being switched off; leaving MPDOs out of the
            // watch set threw that away along with the length rule, so a device still emitting
            // MPDOs after Stop passed whenever its ordinary TPDOs behaved. Found in review of
            // #388, against the fix one commit earlier that excluded them wholesale.
            var ordinaryIds = valid.Select(p => (p.CobId, p.Extended)).ToHashSet();
            var watched = ordinaryIds.Concat(multiplexed.Select(p => (p.CobId, p.Extended))).ToHashSet();
            var initialState = await ObservedNmtStateAsync(session, cancellationToken).ConfigureAwait(false);
            if (initialState is null)
            {
                return Outcome.NeedsSetup(
                    "The device's NMT state could not be read from its heartbeat, and this check changes that state. "
                    + "Restoring it would mean guessing pre-operational, which would leave a device that began "
                    + "Operational out of service while this reported a successful restore — so the check is not run. "
                    + "Enable the heartbeat (1017h) and re-run; NMT-01 refuses on the same grounds.");
            }

            // An RTR frame on a TPDO's COB-ID is another master asking for it, not the target
            // sending it. Counting one as the target's traffic failed a conforming device in the
            // stopped window and made a silent one look busy in the operational one. The tap keeps
            // IsRemote, so the filter is here rather than in the counting below. Found in review
            // of #388.
            using var tap = new BusTap(session.Service, (id, extended) => watched.Contains((id, extended)));

            TappedFrame[] inOperational;
            int inStopped;
            int settling;
            var multiplexedInOperational = 0;
            var multiplexedInStopped = 0;
            var beforeOperational = 0;
            var stoppedConfirmed = false;
            var operationalConfirmed = false;
            double stopSentAtMs = 0;
            Restoration restoration;
            try
            {
                // Operational is the state PDOs flow in; Stopped is the state they must not.
                // Checking only the first would pass a device that transmits regardless of its NMT
                // state, which is the failure that matters on a machine being switched off.
                await session.Client.SendNmtCommandAsync(NmtCommand.Start, session.Target, cancellationToken).ConfigureAwait(false);

                // Confirm Operational before the window that judges it. Stop has been verified
                // since 932e2d0 and Start was not, so an undelivered Start left a conforming
                // device legitimately silent and the per-descriptor overdue rule charged it for
                // that. Found in review of #388.
                operationalConfirmed = await LatestNmtStateAsync(session, (byte)NmtState.Operational, cancellationToken)
                    .ConfigureAwait(false) == (byte)NmtState.Operational;

                // The boundary, recorded. Everything the tap collected before this instant was
                // emitted in whatever state the device was in when the check found it — and the
                // tap has been open since before the Start went out. Counting those frames as
                // Operational traffic let a device that wrongly emits an event-timed TPDO in
                // pre-operational and then goes silent in Operational satisfy the overdue rule
                // below and pass. Found in review of #388.
                var operationalFromMs = tap.ElapsedMs;

                var operationalUntilMs = await tap.ListenAsync(session.Options.PdoWindowMs, cancellationToken)
                    .ConfigureAwait(false);

                // The Operational verdict is drawn from ordinary TPDOs alone, so that a pass is
                // never derived from traffic of a descriptor the report says it did not check —
                // and only from frames that arrived after Operational was confirmed, so that it
                // is drawn from the state it names.
                // Bounded at both ends: after Operational was confirmed, and not past the window
                // the report names. The drain that makes the window complete is not part of it.
                var inWindow = Transmitted(tap.Through(operationalUntilMs));
                var everything = inWindow.Where(f => f.AtMs >= operationalFromMs).ToList();
                inOperational = everything.Where(f => ordinaryIds.Contains((f.CanId, f.IsExtended))).ToArray();
                multiplexedInOperational = everything.Count - inOperational.Length;
                beforeOperational = inWindow.Count(f => f.AtMs < operationalFromMs);

                // Before the await, not after it: that task completes on the transmit confirmation
                // and not at the instant the frame is handed off, so anything the device emitted
                // in between fell outside every bucket. Found in review of #388.
                stopSentAtMs = tap.ElapsedMs;
                await session.Client.SendNmtCommandAsync(NmtCommand.Stop, session.Target, cancellationToken).ConfigureAwait(false);

                // The baseline is the moment Stop is sent, not the moment it is confirmed. Taking
                // it after the confirming wait left everything in that gap — up to a whole
                // heartbeat window — in no bucket at all, so a device that kept transmitting and
                // only then reported 04h passed the check that exists to catch exactly that.
                // Found in review of #388.
                // Confirm it landed before judging. SendNmtCommandAsync hands out a Task and
                // discards the transmit result, so a frame the adapter rejected completes the
                // await normally — the device then stays Operational and its TPDOs would be read
                // as a conformance failure instead of a transport one.
                stoppedConfirmed = await LatestNmtStateAsync(session, (byte)NmtState.Stopped, cancellationToken)
                    .ConfigureAwait(false) == (byte)NmtState.Stopped;

                await Task.Delay(SettleMs, cancellationToken).ConfigureAwait(false);
                var stoppedUntilMs = await tap.ListenAsync(Math.Min(2000, session.Options.PdoWindowMs), cancellationToken)
                    .ConfigureAwait(false);

                // Classified by arrival time rather than by position in the list: a frame already
                // on the wire when Stop landed is allowed its settling interval, and anything
                // after that is the device still transmitting.
                var afterStop = Transmitted(tap.Through(stoppedUntilMs)).Where(f => f.AtMs >= stopSentAtMs).ToList();
                settling = afterStop.Count(f => f.AtMs < stopSentAtMs + SettleMs);
                inStopped = afterStop.Count - settling;
                multiplexedInStopped = afterStop
                    .Count(f => f.AtMs >= stopSentAtMs + SettleMs && !ordinaryIds.Contains((f.CanId, f.IsExtended)));
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
                ["frames before Operational was confirmed"] = beforeOperational.ToString(CultureInfo.InvariantCulture),
                ["frames in Stopped"] = inStopped.ToString(CultureInfo.InvariantCulture),
                ["frames within the first " + SettleMs + " ms of Stop"] = settling.ToString(CultureInfo.InvariantCulture),
                ["left in state"] = restoration.State,
            };

            if (multiplexed.Count > 0)
            {
                measurements["TPDOs in multiplexed mode"] =
                    string.Join(", ", multiplexed.Select(p => $"TPDO{p.Number} 0x{p.CobId:X3}"))
                    + " — watched for the Stopped rule, outside the mapped-length rule";
                measurements["multiplexed frames in Operational"] =
                    multiplexedInOperational.ToString(CultureInfo.InvariantCulture);
            }

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

            measurements["Start confirmed by heartbeat"] = operationalConfirmed.ToString();
            measurements["Stop confirmed by heartbeat"] = stoppedConfirmed.ToString();

            if (!operationalConfirmed)
            {
                return Outcome.Inconclusive(
                    "The device did not report Operational after the NMT Start command, so nothing observed in the "
                    + "window that followed can be charged to it — a conforming device that never received the Start "
                    + "is legitimately silent. An adapter dropping the command produces exactly this picture.",
                    Attribution.Transport, measurements);
            }

            // The device's own findings from the Operational window reach a verdict here,
            // before anything about Stop can return. They are already measured and they are
            // the device's; an earlier version evaluated them after the Stop branches, so an
            // unconfirmed Stop returned inconclusive first and a real failure sat behind exit
            // 0. Found in review of #388.
            var mismatched = valid.Any(p => inOperational.Any(
                f => f.CanId == p.CobId && f.IsExtended == p.Extended && p.Bytes > 0 && f.Data.Length != p.Bytes));
            if (mismatched)
            {
                return Outcome.Fail(
                    "A TPDO carried a payload of a different length than its own mapping (1A00h) declares.", measurements);
            }

            // Per descriptor, before the aggregate: a TPDO with an event timer short enough to
            // have elapsed inside the window owes a frame regardless of what its neighbours sent.
            // Event-driven types only (254/255). The event timer is the trigger for those; a
            // *synchronous* TPDO with 1800h:05 set still waits for a SYNC, which this check does
            // not send — charging it for silence would be a false FAIL of the kind this whole
            // guard exists to prevent, and only an unread transmission type makes it unknowable.
            var overdue = valid
                .Where(p => p.TransmissionTypeRead && CanOpenTransmissionType.IsEventDriven(p.TransmissionType))
                // The window has to cover the inhibit budget too: 1800h:03 may legitimately defer
                // an event-timer expiry, and declaring the TPDO overdue on the timer alone failed
                // a conforming producer that had just entered its inhibit interval. Found in
                // review of #388.
                .Where(p => p.EventTimerMs > 0 && p.EventTimerMs * 2 + p.InhibitMs <= session.Options.PdoWindowMs)
                .Where(p => !inOperational.Any(f => f.CanId == p.CobId && f.IsExtended == p.Extended))
                .ToList();
            if (overdue.Count > 0)
            {
                return Outcome.Fail(
                    $"{string.Join(", ", overdue.Select(p => $"TPDO{p.Number} (0x{p.CobId:X3}, event timer {p.EventTimerMs} ms"))} "
                    + $"transmitted nothing in {session.Options.PdoWindowMs} ms of Operational, which is at least twice "
                    + "its event timer. Traffic from another TPDO does not excuse it.", measurements);
            }

            if (!stoppedConfirmed)
            {
                return Outcome.Inconclusive(
                    $"The device did not report Stopped after the NMT Stop command, so the {inStopped} frame(s) in the "
                    + "window that followed cannot be charged to it — an adapter that drops the command produces "
                    + "exactly this picture. Nothing in Operational was found wrong, which the findings above report.",
                    Attribution.Transport, measurements);
            }

            if (inStopped > 0)
            {
                return Outcome.Fail(
                    $"{inStopped} TPDO frame(s) were transmitted more than {SettleMs} ms after the device was Stopped"
                    + (multiplexedInStopped > 0 ? $", {multiplexedInStopped} of them from a multiplexed TPDO" : string.Empty)
                    + ". CiA 301 allows process data only in Operational.", measurements);
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


            if (valid.Count == 0)
            {
                // Reached only once the Stopped rule has been applied and found nothing, which is
                // the half of this check an MPDO does answer to. The Operational half needs a
                // descriptor whose mapping predicts a payload length, and there is none.
                return Outcome.NotApplicable(
                    $"Every valid TPDO ({string.Join(", ", multiplexed.Select(p => "TPDO" + p.Number))}) is in "
                    + "multiplexed mode. Nothing was transmitted on their COB-IDs after Stop, which is the part of "
                    + "this check that applies to them; what their payloads should contain is outside what this "
                    + "runner checks.", measurements);
            }

            if (inOperational.Length == 0)
            {
                // Event-driven TPDOs only transmit when their data changes, and a synchronous one
                // waits for a SYNC this check does not send. Silence is therefore not a fault.
                var synchronous = valid.Where(p => CanOpenTransmissionType.IsSynchronous(p.TransmissionType)).ToArray();

                // The attribution follows which case it is, instead of Unknown for both. When
                // *every* watched TPDO is synchronous the silence is not a fault and nothing is
                // claimed, which is what Attribution.None means and what SYN-02 already returns
                // for its acyclic counterpart; Unknown put a correctly behaving device in the
                // report's "unclear" group and told the operator to go and look at it. A silent
                // event-driven TPDO is genuinely unknown: the overdue rule above has already
                // cleared the ones that owed a frame, and whether the application had data to
                // send is not observable from the bus.
                var allSynchronous = synchronous.Length == valid.Count;
                return Outcome.Inconclusive(
                    $"No TPDO was transmitted in {session.Options.PdoWindowMs} ms of Operational. "
                    + (synchronous.Length > 0
                        ? $"{synchronous.Length} of them are synchronous and wait for a SYNC — see the sync group."
                        : "Event-driven TPDOs transmit when their mapped data changes, so a static device is legitimately silent."),
                    allSynchronous ? Attribution.None : Attribution.Unknown, measurements);
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

                var cobIdWord = BitConverter.ToUInt32(raw, 0);

                // Decoded by the same function SYN-02's sender uses, so the two cannot disagree.
                // They did: this check validated the word inline while TargetSyncCobIdAsync masked
                // it again, and a word failed here was still transmitted on there. The reasoning
                // for each condition — including why bit 31 is absent — is on DecodeSyncCobId.
                // Found in review of #388.
                var (syncId, syncProblem) = DecodeSyncCobId(cobIdWord);
                if (syncProblem is { } unusable)
                {
                    return Outcome.Fail(unusable,
                        new Dictionary<string, string> { ["1005h COB-ID SYNC"] = $"0x{cobIdWord:X8}" });
                }

                var cobId = cobIdWord;
                var generates = (cobId & CanOpenCobId.SyncGenerateBit) != 0;
                var measurements = new Dictionary<string, string>
                {
                    ["1005h COB-ID SYNC"] = $"0x{cobId:X8} (id 0x{syncId:X3})",
                    ["generates SYNC"] = generates.ToString(),
                };

                // Its own try: it sat inside 1005h's catch, so an absent 1006h reported that the
                // device has no 1005h at all. A SYNC consumer may expose 1005h without being a
                // producer that needs 1006h. Found in review of #388 — the same shape as STO-01's
                // 1011h and GRD-01's 100Dh.
                if (!session.MayTransfer(0x1006, 0x00))
                {
                    // Not asked, so not concluded. For a producer the pair is mandatory and its
                    // absence from the description is the run's own gap, which is setup rather
                    // than a device finding.
                    measurements["1006h communication cycle period"] = "not read: " + session.GateReason(0x1006, 0x00);
                    if (generates)
                    {
                        return Outcome.NeedsSetup(
                            "1005h has the generate bit set, so 1006h is mandatory for this device, and this run may "
                            + "not ask for it: " + session.GateReason(0x1006, 0x00));
                    }
                }
                else
                {
                    try
                    {
                        var period = await session.Client.SdoUploadAsync(session.Target, 0x1006, 0x00, cancellationToken).ConfigureAwait(false);
                        if (period.Length == 4)
                        {
                            measurements["1006h communication cycle period"] = $"{BitConverter.ToUInt32(period, 0)} us";
                        }
                        else
                        {
                            // An implemented 1006h that is not UNSIGNED32 is the device contradicting
                            // itself, not a detail for the measurements. Found in review of #388.
                            measurements["1006h communication cycle period"] = IdentityAndSdoChecks.Hex(period);
                            return Outcome.Fail(
                                $"1006h:00 is UNSIGNED32 and the device returned {period.Length} byte(s).", measurements);
                        }
                    }
                    catch (SdoAbortException abort) when (abort.AbortCode is (uint)SdoAbortCode.ObjectDoesNotExist
                                                              or (uint)SdoAbortCode.SubIndexDoesNotExist)
                    {
                        // Optional for a consumer, mandatory for a producer (CiA 301 §7.5.2.7):
                        // 1006h is what a SYNC producer transmits on. Treating the absence as
                        // optional regardless recorded a device that claims to generate SYNC
                        // without a cycle period as conforming — the round-20 fix for the
                        // consumer case, applied to the producer case as well. Found in review
                        // of #388.
                        if (generates)
                        {
                            measurements["1006h communication cycle period"] = "absent";
                            return Outcome.Fail(
                                "1005h has the generate bit set, so this device claims to produce SYNC, and 1006h is "
                                + "mandatory for a producer — the communication cycle period is what it would transmit "
                                + "on. The device says it does not exist.", measurements);
                        }

                        measurements["1006h communication cycle period"] = "absent; a SYNC consumer need not be a producer";
                    }
                    catch (SdoAbortException abort)
                    {
                        measurements["1006h communication cycle period"] = $"aborted {IdentityAndSdoChecks.Describe(abort)}";
                        return Outcome.Inconclusive(
                            "1005h was read, but the declared 1006h would not answer, so the SYNC cycle period is "
                            + "unknown.", Attribution.Device, measurements);
                    }
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

            // A pair the peer-SDO gate blocked was never asked for, so it is the run's own input
            // and not a device finding. Returning Inconclusive with device attribution for it
            // blamed the device for a sub-index the bound description omits — the same
            // blocked-versus-refused confusion PDO-01 had one level up. Found while mutating the
            // fixture for that fix: an EDS without 1800sub2 produced "[device] SYN-02".
            var blockedTypes = unreadTypes.Where(p => p.TransmissionTypeBlocked).ToList();
            if (blockedTypes.Count > 0 && blockedTypes.Count == unreadTypes.Count)
            {
                return Outcome.NeedsSetup(
                    $"{blockedTypes.Count} of {known.Count(p => p.Valid)} valid TPDO(s) have a transmission type this "
                    + "run may not ask for, so whether they are synchronous is unknown: "
                    + string.Join("; ", blockedTypes.Select(p => $"TPDO{p.Number}: " + session.GateReason((ushort)(0x1800 + p.Number - 1), 0x02))));
            }

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
            // Before the unread guard: a value that came back and is invalid is the device's
            // defect, and saying "unknown" about it would be the softer and wronger verdict —
            // which is exactly what the previous commit did. The same shape as the reserved 1019h.
            var reservedStarts = cyclic.Where(p => p.SyncStartReserved is not null).ToList();
            if (reservedStarts.Count > 0)
            {
                return Outcome.Fail(
                    string.Join("; ", reservedStarts.Select(p =>
                        $"TPDO{p.Number} (0x{p.CobId:X3}) reports 1800h:06 = {p.SyncStartReserved}"))
                    + ". A SYNC start value is 0..240 and 241..255 is reserved (the stack rejects a write above 240), "
                    + "so no SYNC was sent: a burst sized from a value no device can honour would measure nothing.",
                    new Dictionary<string, string>
                    {
                        ["reserved SYNC start values"] = string.Join(", ", reservedStarts.Select(p =>
                            $"TPDO{p.Number}: {p.SyncStartReserved}")),
                    });
            }

            var unreadStarts = cyclic.Where(p => !p.SyncStartRead).ToList();
            if (unreadStarts.Count > 0)
            {
                return Outcome.Inconclusive(
                    $"{unreadStarts.Count} cyclic synchronous TPDO(s) would not return 1800h:06, so whether they wait "
                    + "for a SYNC start value is unknown. Assuming none would shorten the SYNC burst and could fail a "
                    + "device that is simply still waiting, so nothing was sent.",
                    Attribution.Device,
                    new Dictionary<string, string>
                    {
                        ["TPDOs with an unread SYNC start value"] = string.Join(", ", unreadStarts.Select(p => $"TPDO{p.Number}")),
                    });
            }

            var (overflow, overflowProblem) = await TargetSyncOverflowAsync(session, cancellationToken).ConfigureAwait(false);
            if (overflowProblem is { } reservedOverflow)
            {
                return Outcome.Fail(reservedOverflow + " Nothing was sent, because either form of SYNC would be the "
                    + "wrong one and the device would then be failed for the silence that followed.");
            }

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
            switch (syncCobId.State)
            {
                case SyncCobIdState.Blocked:
                    return Outcome.NeedsSetup(
                        "The target's 1005h may not be read by this run, so there is no way to know which COB-ID its "
                        + $"synchronous TPDOs are waiting on: {syncCobId.Detail} See SYN-01.");

                // The one verdict this round added. Reaching here means the sweep found a cyclic
                // synchronous TPDO, so the device supports SYNC, and CiA 301 §7.5.2.5 then makes
                // 1005h mandatory. Resolving the absence to the 080h default let a device that
                // omits a required object and hard-wires 080h be measured as conforming. Found in
                // review of #388.
                case SyncCobIdState.Absent:
                    return Outcome.Fail(
                        $"{cyclic.Count} cyclic synchronous TPDO(s) are configured, so this device supports SYNC and "
                        + "CiA 301 §7.5.2.5 makes 1005h mandatory — and the device answers that it does not exist. "
                        + "No SYNC was sent: 080h is the identifier a device without SYNC support would use, and "
                        + "sending on it here would measure the default rather than the device.");

                case SyncCobIdState.Malformed:
                    return Outcome.Fail(
                        "The target's 1005h does not name an identifier this run can send on, so no SYNC was "
                        + $"produced: {syncCobId.Detail} See SYN-01.");

                // Inconclusive, not Fail: nothing was measured about the TPDOs, and the device
                // did not answer wrongly — it did not answer. Failing it here made SYN-02 the one
                // place in the run that turned an unresolved read into a device failure, while
                // SYN-01 reached Inconclusive for the same abort and 1019h's unread prerequisite
                // is Inconclusive as well. Found in review of #388.
                case SyncCobIdState.Unresolved:
                    return Outcome.Inconclusive(
                        "The target's 1005h would not come back, so which COB-ID its synchronous TPDOs wait on is "
                        + $"unknown and no SYNC was sent: {syncCobId.Detail} Nothing is concluded about the TPDOs.",
                        Attribution.Device);
            }

            // Extended is constant now: an extended 1005h word is Unusable above, because CiA 301
            // §7.5.2.5 gives SYNC an 11-bit COB-ID and the stack's own validator refuses one.
            var sync = (CobId: syncCobId.CanId, Extended: false);

            byte counter = 0;
            var watched = cyclic.Select(p => (p.CobId, p.Extended)).ToHashSet();
            var initialState = await ObservedNmtStateAsync(session, cancellationToken).ConfigureAwait(false);
            if (initialState is null)
            {
                return Outcome.NeedsSetup(
                    "The device's NMT state could not be read from its heartbeat, and this check changes that state. "
                    + "Restoring it would mean guessing pre-operational, which would leave a device that began "
                    + "Operational out of service while this reported a successful restore — so the check is not run. "
                    + "Enable the heartbeat (1017h) and re-run; NMT-01 refuses on the same grounds.");
            }

            using var tap = new BusTap(session.Service, (id, extended) => watched.Contains((id, extended)));

            // The target's own SYNCs, if it makes any. 1005h bit 30 says it is a producer, and
            // this check then injects a second SYNC stream: a TPDO answering one of the device's
            // own SYNCs would be counted against the tool's accepted count, hiding a response
            // missing from the injected burst, and the two counter streams interleaving makes any
            // 1800h:06 start gate unreadable. Counting the foreign SYNCs would fix the first half
            // and not the second, so this detects the condition and declines to measure through
            // it — the choice the rest of this file makes about anything it cannot separate.
            // Found in review of #388.
            using var syncWatch = new BusTap(session.Service,
                (id, extended) => id == sync.CobId && extended == sync.Extended);
            int quiet;
            double burstFromMs = 0;
            double burstUntilMs = 0;
            Restoration restoration;
            var rejected = 0;
            var operationalConfirmed = false;
            try
            {
                await session.Client.SendNmtCommandAsync(NmtCommand.Start, session.Target, cancellationToken).ConfigureAwait(false);

                // Same reason PDO-03 confirms since 155c602: a Start the adapter rejected
                // completes the await normally, the target never enters Operational, and its
                // synchronous TPDOs are then failed for not answering SYNCs they were never due
                // to answer. Found in review of #388.
                operationalConfirmed = await LatestNmtStateAsync(session, (byte)NmtState.Operational, cancellationToken)
                    .ConfigureAwait(false) == (byte)NmtState.Operational;

                // A timestamp, not a count. Skip(quiet) treated "how many frames had been
                // delivered by now" as the observation boundary — so a TPDO that arrived before
                // the first SYNC but was delivered after this line was credited as a response to
                // it, and a response still queued at the snapshot was dropped. That can pass a
                // target that emitted before the burst and ignored every SYNC, or fail a
                // conforming one under load. Every other window in this tool was moved onto
                // arrival stamps two commits ago; this was the one left behind. Found in review
                // of #388.
                quiet = Transmitted(tap).Count;
                burstFromMs = tap.ElapsedMs;
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

                // The last SYNC's answer is still in flight when the loop ends — and through
                // ListenAsync, so the pump is drained and the window has an explicit end.
                burstUntilMs = await tap.ListenAsync(200, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                restoration = await RestoreNmtStateAsync(session, book, "SYN-02", initialState).ConfigureAwait(false);
            }

            // The observations are assembled before any verdict can return, so each early return
            // carries what was collected. Hoisting the restore guard in beea974 put it ahead of
            // this dictionary and stripped the SYNC count, the COB-ID and the per-TPDO traffic
            // from exactly the verdict that most needs them. Found in review of #388.
            var accepted = syncs - rejected;
            var after = Transmitted(tap.Through(burstUntilMs))
                .Where(f => f.AtMs >= burstFromMs)
                .ToArray();
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

            // Before anything else, as in PDO-03: Start has been sent and the finally has already
            // run the restore, so a restore that did not take decides the verdict rather than
            // being overtaken by a return about the Start.
            if (!restoration.Succeeded)
            {
                return Outcome.Failed(Attribution.Unknown,
                    "The device could not be put back into the state it was found in: " + restoration.State
                    + ". A write-tier run promises the device is unchanged afterwards, so this fails whatever else was "
                    + "measured — the device needs looking at before it is trusted again.", measurements);
            }

            if (!operationalConfirmed)
            {
                return Outcome.Inconclusive(
                    "The device did not report Operational after the NMT Start command, so its synchronous TPDOs were "
                    + "never due and their silence says nothing about it.", Attribution.Transport, measurements);
            }

            // Before any per-TPDO arithmetic: if SYNCs this run did not send were on the bus during
            // the burst, the opportunities cannot be attributed and nothing below means what it
            // says. Neither the virtual adapter nor a hardware one delivers a channel's own
            // transmissions back to it, and echoes are filtered besides — so what this tap holds
            // is someone else's traffic, which on a device whose 1005h sets the generate bit is
            // the device's own.
            var foreignSyncs = Transmitted(syncWatch.Through(burstUntilMs))
                .Count(f => f.AtMs >= burstFromMs);
            if (foreignSyncs > 0)
            {
                measurements["SYNCs this run did not send"] = foreignSyncs.ToString(CultureInfo.InvariantCulture);
                return Outcome.Inconclusive(
                    $"{foreignSyncs} SYNC frame(s) on 0x{sync.CobId:X3} during the burst were not sent by this run, so "
                    + "the target is producing SYNC itself (1005h bit 30). A TPDO answering one of those would be "
                    + "counted against this run's SYNCs and could hide a response missing from the burst, and the two "
                    + "counter streams interleaving makes any 1800h:06 start value unreadable. Stop the target's SYNC "
                    + "producer — 1006h = 0, or clear bit 30 of 1005h — and re-run; nothing is concluded about its "
                    + "synchronous TPDOs here.",
                    Attribution.Setup, measurements);
            }

            if (accepted == 0)
            {
                return Outcome.TransportFault(
                    $"The driver rejected all {syncs} SYNC frames on 0x{sync.CobId:X3}, so the device was never synchronised.",
                    measurements);
            }

            var silent = new List<string>();
            var short_ = new List<string>();
            var unmeasured = new List<string>();
            foreach (var pdo in cyclic)
            {
                var mine = after.Where(f => f.CanId == pdo.CobId && f.IsExtended == pdo.Extended).ToArray();

                // The opportunities this TPDO actually had, which is not the whole burst. With a
                // start value in 1800h:06 the TPDO is correctly silent until the SYNC counter
                // first reaches it, so the SYNCs before that gate are not opportunities it
                // declined — they are SYNCs it was not allowed to answer. Counting them required
                // 481 responses from a device whose 243 were conforming, i.e. the threshold
                // tightened one commit earlier became a false FAIL generator for exactly the
                // devices 1800h:06 exists for. Found in review of #388.
                //
                // The counter runs 1..overflow and starts at 1, so the first SYNC carrying the
                // start value is the (start)th of the burst and the gate skips start - 1 of them.
                // Without a counter a start value has nothing to start on, which the measurements
                // above already say, and the gate is then zero.
                var gated = counterOverflow >= 2 && pdo.SyncStartValue >= 2 ? pdo.SyncStartValue - 1 : 0;
                var usable = Math.Max(0, accepted - gated);
                var opportunities = usable / pdo.TransmissionType;
                measurements[$"TPDO{pdo.Number} (0x{pdo.CobId:X3}) type {pdo.TransmissionType}"] =
                    $"{mine.Length} frame(s) for {opportunities} SYNC opportunity/opportunities"
                    + (gated > 0
                        ? $" ({gated} SYNC(s) before its 1800h:06 start value of {pdo.SyncStartValue} are not "
                          + "opportunities)"
                        : string.Empty);
                // The opportunities the device owes, less one frame of edge allowance — not half
                // of them. Half forgave a device behaving as type 2 while declaring type 1, which
                // is a contradiction of its own 1800h:02 and exactly what this check is for. The
                // allowance is one frame and named as such: the burst already waits 200 ms after
                // the last SYNC and the window is drained and closed at a timestamp, so the only
                // frame still plausibly at risk is the last one. Found in review of #388, against
                // the loose margin introduced to stop a different false FAIL.
                // No opportunity, nothing owed. Math.Max(1, …) would otherwise claim one frame is
                // due from a TPDO whose start gate the burst never reached — which the
                // startUnreached guard above already catches for the cases that occur, so this is
                // the belt to its braces rather than a live defect. Reported as unmeasured, not
                // passed: a TPDO that was never given a chance has told us nothing.
                if (opportunities == 0)
                {
                    unmeasured.Add($"TPDO{pdo.Number} (0x{pdo.CobId:X3}, type {pdo.TransmissionType}: the burst had no "
                        + "SYNC at or after its start value, so it was never due)");
                    continue;
                }

                var expected = Math.Max(1, opportunities - 1);
                if (mine.Length >= expected) continue;
                if (mine.Length > 0)
                {
                    short_.Add($"TPDO{pdo.Number} (0x{pdo.CobId:X3}, type {pdo.TransmissionType}: {mine.Length} frame(s) "
                        + $"for {opportunities} opportunity/opportunities)");
                    continue;
                }


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
            // A shortfall is judged the same way silence is: chargeable to the device only when
            // every SYNC reached the bus. An earlier version returned Fail for rejected == 0 and
            // had no branch at all otherwise, so a short-producing TPDO fell through to Pass the
            // moment one frame had been rejected. Found in review of #388.
            if (short_.Count > 0 && rejected > 0)
            {
                return Outcome.Inconclusive(
                    $"{string.Join(", ", short_)} produced fewer frames than its transmission type asks for, but the "
                    + $"driver rejected {rejected} of {syncs} SYNC frames, so how many the device actually saw is "
                    + "uncertain and the shortfall cannot be charged to it.", Attribution.Transport, measurements);
            }

            if (short_.Count > 0)
            {
                return Outcome.Fail(
                    $"{string.Join(", ", short_)} transmitted far fewer frames than its transmission type asks for. A "
                    + "cyclic synchronous TPDO owes one frame per N SYNCs.", measurements);
            }

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
            // The payloads themselves, reconstructed from the decoded message, so the comparison
            // below can be between *sets* rather than counts. Byte 0..1 are the error code
            // little-endian, byte 2 the error register and bytes 3..7 the manufacturer field
            // (CiA 301 §7.2.7.1), which is exactly what the frame carried.
            var decodedPayloads = new HashSet<string>(StringComparer.Ordinal);
            void OnEmcy(object? sender, EmcyReceivedEventArgs e)
            {
                var m = e.Message;

                // The tap is restricted to --node, so the decode count has to be too. It was not:
                // on a multi-node bus another producer's emergency made decoded.Count nonzero, and
                // a well-formed frame from the *target* that the stack failed to decode was then
                // covered by it — the decoder failure this check exists to surface, masked by a
                // neighbour. Found in review of #388.
                if (session.Options.TargetNode is { } only && m.ProducerNodeId != only) return;

                var payload = new byte[8];
                BitConverter.GetBytes(m.ErrorCode).CopyTo(payload, 0);
                payload[2] = m.ErrorRegister;
                m.ManufacturerSpecific.CopyTo(payload, 3);

                lock (decoded)
                {
                    decoded.Add($"node {m.ProducerNodeId}: error 0x{m.ErrorCode:X4}, register 0x{m.ErrorRegister:X2}, "
                        + $"manufacturer {IdentityAndSdoChecks.Hex(m.ManufacturerSpecific)}");
                    decodedPayloads.Add(Convert.ToHexString(payload));
                }
            }

            session.Client.EmcyReceived += OnEmcy;
            List<TappedFrame> frames;
            int polls;
            try
            {
                var untilMs = await tap.ListenAsync(session.Options.ListenMs, cancellationToken).ConfigureAwait(false);

                // One snapshot, taken at the window's edge, used for the drain *and* for the
                // verdict. The previous version snapshotted twice: the drain waited for the
                // payloads present when the window closed, and the verdict re-read the tap after
                // the grace — so an emergency arriving during the grace was in the verdict's set
                // without ever having been in the set the drain waited for, and its decode could
                // still be queued when the handler came off. A false stack fault, from the fix
                // for a false stack fault. Both reviewers found it. Found in review of #388.
                var window = tap.Through(untilMs);
                frames = Transmitted(window);
                polls = window.Count - frames.Count;

                // The tap and the decoder do not share an instant. A frame arriving near the end
                // of the window is in the tap's list while the node's actor has not yet raised
                // EmcyReceived for it, and detaching here threw that decode away — after which
                // the raw snapshot held a frame the decoder "never" decoded and EMC-01 reported a
                // stack fault that had not happened. So the window closes for both sides at once:
                // the handler stays attached until every payload the tap holds has been decoded,
                // or until a bounded grace runs out.
                //
                // The grace is named rather than tuned. What it covers is one hop through the
                // node's actor queue for a frame already received in this process — microseconds
                // of work, and on a loaded host the scheduling delay in front of it, which is the
                // same quantity SettleMs covers elsewhere in this file. Whether it was enough is
                // recorded, so a report can never present a timeout as a decoder defect without
                // saying so. Found in review of #388.
                var pending = frames.Select(f => Convert.ToHexString(f.Data)).ToHashSet(StringComparer.Ordinal);
                var grace = System.Diagnostics.Stopwatch.StartNew();
                while (grace.Elapsed.TotalMilliseconds < SettleMs)
                {
                    lock (decoded)
                    {
                        if (pending.IsSubsetOf(decodedPayloads)) break;
                    }

                    await Task.Delay(5, cancellationToken).ConfigureAwait(false);
                }

            }
            finally
            {
                session.Client.EmcyReceived -= OnEmcy;
            }

            // Data frames only. An RTR another participant puts on the target's EMCY COB-ID is a
            // request for that id, not an emergency object: an empty one was counted as a
            // malformed emergency and failed the device, and an eight-byte one produced a tool
            // fault because the stack had correctly decoded nothing. The heartbeat and PDO paths
            // filtered IsRemote and this one did not. Found in review of #388.
            var measurements = new Dictionary<string, string>
            {
                ["window"] = $"{session.Options.ListenMs} ms",
                ["frames"] = frames.Count.ToString(CultureInfo.InvariantCulture),
                ["decoded by the stack"] = decoded.Count.ToString(CultureInfo.InvariantCulture),
            };
            if (polls > 0)
            {
                measurements["remote frames ignored"] =
                    $"{polls} RTR frame(s) on this COB-ID, which are requests from another participant";
            }

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

            // Which payloads, not how many. Counting distinct payloads against the number of
            // events still passed a run where the stack raised two events for payload A and none
            // for B: two and two. The question is whether every payload that was on the bus came
            // back decoded, so the sets are compared. Distinct on both sides because the stack
            // may legitimately coalesce identical queued emergencies, and failing that would be a
            // fault the device never caused. Found in review of #388.
            var rawPayloads = frames.Select(f => Convert.ToHexString(f.Data)).ToHashSet(StringComparer.Ordinal);
            HashSet<string> seen;
            lock (decoded) seen = new HashSet<string>(decodedPayloads, StringComparer.Ordinal);
            var missed = rawPayloads.Except(seen, StringComparer.Ordinal).ToList();

            measurements["distinct payloads"] = rawPayloads.Count.ToString(CultureInfo.InvariantCulture);
            measurements["distinct payloads decoded"] = seen.Intersect(rawPayloads, StringComparer.Ordinal).Count()
                .ToString(CultureInfo.InvariantCulture);

            if (missed.Count == 0)
                return Outcome.Pass($"{decoded.Count} emergency object(s) received, every payload decoded.", measurements);

            // Reached only after the grace above has fully elapsed, because the loop leaves early
            // exactly when nothing is missing. So this is not the decoder being slower than the
            // tap, and there is no second verdict for "it might have been": a flag for that was
            // the same condition as this one, which made this branch unreachable — the check's
            // whole purpose, dead. Found while mutating the fix in review of #388.
            //
            // The grace is stated so the reader can judge it rather than take it: what it covers
            // is one hop through the node's in-process actor queue for a frame the tap has
            // already seen, which is microseconds of work plus whatever the host's scheduler adds
            // in front of it. A frame that reached the tap reached the node's reader too, and a
            // load spike long enough to exceed this would have delayed the tap as well.
            measurements["payloads not decoded"] = string.Join(", ", missed.Take(5));
            measurements["decode grace after the window"] = $"{SettleMs} ms";
            return Outcome.ToolFault(
                $"{rawPayloads.Count} distinct emergency payload(s) from node {session.Options.TargetNode} were on "
                + $"the bus and {missed.Count} of them were never raised as an EmcyReceived event, within the window "
                + $"and {SettleMs} ms after it. That is a defect in the stack, not in the device.", measurements);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Puts the device back in pre-operational, whatever happened above. Never cancellable: this
    /// is the step that stops a conformance run from leaving a machine stopped, so it must run on
    /// the way out of a cancelled check as much as a successful one. A failure here is recorded
    /// rather than thrown — it would otherwise replace the finding the check had just reached.
    /// </summary>
    internal static async Task<Restoration> RestoreNmtStateAsync(Session session, Checkbook book, string checkId, byte? initial)
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
            // Not Ok. These checks refuse to start unless the state could be read, so arriving
            // here means the heartbeat stopped while the check ran — and then nothing confirms the
            // device went back, which is exactly the claim the write tier must not make on faith.
            // Found in review of #388.
            return Restoration.Broke(
                $"UNKNOWN — {name} was commanded and no heartbeat came back to confirm it. The device's state after "
                + "this run is unobserved.");
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
        // Wait for a *usable* state, not for any frame: an RTR poll, a boot-up or a toggle-one
        // guarding response arriving first ended the wait and the loop below then found nothing,
        // so the check reported setup-skipped although a real heartbeat followed inside the
        // window. Found in review of #388.
        await probe.WaitForAsync(
            frames => frames.Any(f => !f.IsRemote && f.Data.Length == 1
                && f.Data[0] is (byte)NmtState.Stopped or (byte)NmtState.Operational or (byte)NmtState.PreOperational),
            Math.Max(500, session.Options.HeartbeatWindowMs), cancellationToken).ConfigureAwait(false);
        var frames = probe.Frames;
        for (var i = frames.Count - 1; i >= 0; i--)
        {
            // Only the three run states. A boot-up (00h) or a node-guarding response (toggle bit,
            // 85h for Operational) is not a state this check may restore to, and accepting one
            // meant cleanup mapped it to EnterPreOperational. The same correction NMT-01's helper
            // got in 155c602, which I had applied to one of the two. Found in review of #388.
            if (frames[i].IsRemote || frames[i].Data.Length != 1) continue;
            var value = frames[i].Data[0];
            if (value is (byte)NmtState.Stopped or (byte)NmtState.Operational or (byte)NmtState.PreOperational)
                return value;
        }

        return null;
    }

    /// <summary>
    /// What the target's 1005h yields for a run that wants to send SYNC on it.
    /// </summary>
    /// <remarks>
    /// The word is decoded by <see cref="DecodeSyncCobId"/>, the same function SYN-01 reports
    /// from. Two copies of that masking existed — the check validated the word and this helper
    /// masked it again — so a word SYN-01 had just failed was still handed to SYN-02, which
    /// transmitted on the normalised identifier. Found in review of #388.
    /// </remarks>
    private static async Task<SyncCobId> TargetSyncCobIdAsync(Session session, CancellationToken cancellationToken)
    {
        // Blocked is not absent — the distinction this whole file keeps relearning. A device that
        // aborts with "no such object" uses the default; a pair the gate refused was never asked,
        // and the device may well consume SYNC on an identifier of its own. Returning 080h for
        // that case sent SYNCs to an id nobody was listening on and then charged the silence to
        // the device. The caller already has a setup path for null. Found in review of #388.
        //
        // Reachable only with a description bound that omits 1005h, because without one the TPDO
        // records are blocked too and SYN-02 returns before it gets here — and such a description
        // contradicts itself, since a device with a synchronous TPDO supports SYNC and CiA 301
        // then makes 1005h mandatory. Setup is the right verdict for a contradiction in the run's
        // own input.
        if (!session.MayTransfer(0x1005, 0x00))
            return new SyncCobId(SyncCobIdState.Blocked, 0, session.GateReason(0x1005, 0x00));
        try
        {
            var raw = await session.Client.SdoUploadAsync(session.Target, 0x1005, 0x00, cancellationToken).ConfigureAwait(false);
            if (raw.Length != 4)
            {
                return new SyncCobId(SyncCobIdState.Malformed, 0,
                    $"1005h:00 is UNSIGNED32 and the device returned {IdentityAndSdoChecks.Hex(raw)}.");
            }

            var (canId, problem) = DecodeSyncCobId(BitConverter.ToUInt32(raw, 0));
            return problem is { } bad
                ? new SyncCobId(SyncCobIdState.Malformed, 0, bad)
                : new SyncCobId(SyncCobIdState.Usable, canId, null);
        }
        catch (SdoAbortException abort) when (abort.AbortCode is (uint)SdoAbortCode.ObjectDoesNotExist
                                                  or (uint)SdoAbortCode.SubIndexDoesNotExist)
        {
            // Absent, reported as such rather than resolved to the 080h default here. The caller
            // knows whether that is allowed: it reaches this helper only with a cyclic
            // synchronous TPDO in hand, and a device with one supports SYNC, which makes 1005h
            // mandatory (CiA 301 §7.5.2.5). Handing back the default let a device that omits a
            // required object and hard-wires 080h pass. Found in review of #388 — the same
            // argument I had made for the gate-blocked case one commit earlier and applied to
            // only half of it.
            return new SyncCobId(SyncCobIdState.Absent, CanOpenCobId.Sync, null);
        }
        catch (SdoAbortException abort)
        {
            // A declared 1005h that will not be read leaves the prerequisite unresolved. Guessing
            // the default here sent every SYNC to the wrong identifier and then failed the
            // device's cyclic TPDOs for silence. Found in review of #388.
            return new SyncCobId(SyncCobIdState.Unresolved, 0,
                $"1005h:00 is declared and aborted {IdentityAndSdoChecks.Describe(abort)}.");
        }
    }

    /// <summary>Which of the four things 1005h can be, for a run that wants to send SYNC.</summary>
    private enum SyncCobIdState
    {
        /// <summary>The word decoded to an identifier this run may send on.</summary>
        Usable,

        /// <summary>The device says it has no 1005h. Whether that is allowed is the caller's
        /// question, not this helper's.</summary>
        Absent,

        /// <summary>The peer-SDO gate refused the pair, so the question was never asked.</summary>
        Blocked,

        /// <summary>The device answered, and the answer names no identifier — a wrong length, or
        /// a word CiA 301 does not allow. The device is responsible for that.</summary>
        Malformed,

        /// <summary>The read did not complete: a timeout, or any abort other than "no such
        /// object". Nothing is known about the identifier, and nothing is known about the device
        /// either, so this is not a failure. SYN-01 reaches Inconclusive for the identical abort
        /// through Checkbook's handler, and 1019h's unread prerequisite is Inconclusive too;
        /// collapsing this into Malformed made SYN-02 the only place that failed a device for a
        /// read that never answered. Found in review of #388.</summary>
        Unresolved,
    }

    private readonly record struct SyncCobId(SyncCobIdState State, uint CanId, string? Detail);

    /// <summary>
    /// The identifier a 1005h word names, or the reason it names none. One function, because the
    /// alternative was two: SYN-01 validated the word and <see cref="TargetSyncCobIdAsync"/>
    /// masked it again, so a word the check had failed still reached the sender. CiA 301 §7.5.2.5
    /// Table 55: bit 31 do not care, bit 30 gen., bit 29 frame, bits 28..11 reserved and zero for
    /// an 11-bit identifier, bits 10..0 the CAN-ID. Bit 29 is rejected outright rather than
    /// honoured, because the stack's own IsUsableCobIdWord rejects an extended word for this
    /// object and §7.5.2.5 gives SYNC an 11-bit COB-ID.
    /// </summary>
    private static (uint CanId, string? Problem) DecodeSyncCobId(uint word)
    {
        if ((word & CanOpenCobId.ExtendedFrameBit) != 0)
        {
            return (0, $"1005h:00 is 0x{word:X8} with bit 29 set, which asks for a 29-bit SYNC identifier. "
                + "CiA 301 §7.5.2.5 defines SYNC on an 11-bit COB-ID.");
        }

        if ((word & 0x1FFF_F800) != 0)
        {
            return (0, $"1005h:00 is 0x{word:X8}: bits 11..28 are reserved in an 11-bit COB-ID entry and must be "
                + "zero (CiA 301 §7.5.2.5 Table 55).");
        }

        var canId = word & CanOpenCobId.CanIdMask;
        return CanOpenCobId.IsRestricted(canId)
            ? (0, $"1005h:00 names CAN-ID 0x{canId:X3}, which CiA 301 §7.3.5 restricts to another communication "
                + "object; SYNC may not use it.")
            : (canId, null);
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

        // Sized from the device's own period, not from --heartbeat-ms alone. The state probe that
        // let this check run can catch a beat by chance in a window shorter than one period, and
        // this confirmation then waited that same short window — so a device that *did* restore
        // was reported as having failed to, with a warning that it may have been left stopped.
        // The worst kind of false verdict: it tells the operator to go and look at a device that
        // is fine, about a state the run itself changed. Found in review of #388.
        await probe.WaitForAsync(
            frames => frames.LastOrDefault(f => f.Data.Length == 1) is { Data.Length: 1 } last && last.Data[0] == expected,
            await NmtConfirmationBudgetMsAsync(session, cancellationToken).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

        var frames = probe.Frames;
        for (var i = frames.Count - 1; i >= 0; i--)
        {
            // Only the three run states. A boot-up (00h) or a node-guarding response (toggle bit,
            // 85h for Operational) is not a state this check may restore to, and accepting one
            // meant cleanup mapped it to EnterPreOperational. The same correction NMT-01's helper
            // got in 155c602, which I had applied to one of the two. Found in review of #388.
            if (frames[i].IsRemote || frames[i].Data.Length != 1) continue;
            var value = frames[i].Data[0];
            if (value is (byte)NmtState.Stopped or (byte)NmtState.Operational or (byte)NmtState.PreOperational)
                return value;
        }

        return null;
    }

    /// <summary>
    /// How long a confirmation may wait for the next heartbeat: two of the device's own periods
    /// plus the margin, from whichever of 1017h and a previously measured period is larger, and
    /// never less than the operator's window. A confirmation that waits less than one period
    /// concludes from a phase rather than from the device.
    /// </summary>
    internal static async Task<int> NmtConfirmationBudgetMsAsync(Session session, CancellationToken cancellationToken)
    {
        var declared = await NmtAndHeartbeatChecks.ProducerHeartbeatTimeForConfirmationAsync(session, cancellationToken)
            .ConfigureAwait(false);
        var fromPeriod = declared is { } ms && ms > 0 ? ms * 2 + 200 : 0;
        return Math.Max(Math.Max(1000, session.Options.HeartbeatWindowMs), fromPeriod);
    }

    /// <summary>
    /// 1019h, the SYNC counter overflow. CiA 301 §7.5.2.40 defines 0 as "SYNC without a counter"
    /// and 2..240 as a one-byte counter that rolls 1..value; 1 and 241..255 are reserved. Returns
    /// the value, or a problem for a value that is neither — reporting a reserved one rather than
    /// rounding it into the nearest usable behaviour, which is what hid it.
    /// </summary>
    private static async Task<(byte? Value, string? Problem)> TargetSyncOverflowAsync(Session session, CancellationToken cancellationToken)
    {
        // No 1019h to read means no counter, which is the CiA 301 default.
        if (!session.MayTransfer(0x1019, 0x00)) return ((byte)0, null);
        try
        {
            var raw = await session.Client.SdoUploadAsync(session.Target, 0x1019, 0x00, cancellationToken).ConfigureAwait(false);
            // A wrong length is the device contradicting itself about an UNSIGNED8, not a zero.
            // A wrong length is the device contradicting itself about an UNSIGNED8, which is a
            // conformance violation with a name — not a value that could not be determined. It
            // collapsed into "unknown" and SYN-02 reported an inconclusive prerequisite, so the
            // one concrete thing the device got wrong was the one thing the report did not say.
            // Found in review of #388.
            if (raw.Length != 1)
            {
                return (null, $"1019h:00 is UNSIGNED8 and the device returned {raw.Length} byte(s) "
                    + $"({IdentityAndSdoChecks.Hex(raw)}).");
            }

            // Read is not the same as valid, the third instance of that in this file. Treating 1
            // and 241..255 as "no counter" and "a counter rolling 1..250" let a device with a
            // reserved 1019h be measured as though it conformed. Found by sweeping for the family
            // in review of #388.
            return raw[0] == 1 || raw[0] > CanOpenTransmissionType.SynchronousCyclicMax
                ? (null, $"1019h:00 is 0x{raw[0]:X2}; CiA 301 §7.5.2.40 defines 0 and 2..240 and reserves the rest, "
                    + "so what this device's SYNC carries cannot be derived from it.")
                : (raw[0], null);
        }
        catch (SdoAbortException abort) when (abort.AbortCode is (uint)SdoAbortCode.ObjectDoesNotExist
                                                  or (uint)SdoAbortCode.SubIndexDoesNotExist)
        {
            return ((byte)0, null);
        }
        catch (SdoAbortException)
        {
            // Anything else leaves it unknown, and guessing "no counter" would send DLC 0 to a
            // device that rejects it and then fail that device for silence. Found in review of
            // #388 — the same mistake as defaulting 1005h, one object along.
            return (null, null);
        }
    }

    /// <summary>
    /// What came back from an optional device read. The distinction is the point: <see
    /// cref="Absent"/> means the device said it does not have the object, so the CiA 301 default
    /// applies and a check may proceed; <see cref="Unreadable"/> means the question went
    /// unanswered, so no default is safe and the check must say it does not know. A boolean
    /// collapsed the two and made every fix in this area pick one of the two wrong answers.
    /// </summary>
    private enum ReadOutcome
    {
        Read,
        Absent,
        Unreadable,
    }

    /// <summary>One PDO as its own parameters describe it.</summary>
    private readonly record struct PdoDescriptor(
        int Number, uint CobId, bool Extended, bool Valid, byte TransmissionType, bool TransmissionTypeRead,
        // Why it is unread, not only that it is: the gate blocking 0x..02 is the run's own input
        // and the device refusing it is the device's, and SYN-02 charged both to the device.
        bool TransmissionTypeBlocked,
        byte SyncStartValue, bool SyncStartRead,
        // The value the device returned when it is outside 0..240, or null. Kept rather than
        // collapsed into "unread", because the two want different verdicts: an unread start value
        // is an unresolved prerequisite, and a reserved one is a device defect.
        byte? SyncStartReserved,
        int InhibitMs, int EventTimerMs, int Bytes, bool MappingComplete,
        bool MultiplexedMode, int MappedEntries);

    private readonly record struct PdoSweep(
        List<PdoDescriptor> Descriptors,
        List<string> Blocked,
        List<string> Absent,
        List<string> Problems,
        List<string> NotAddressed);

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
        // What this runner does not check, as distinct from what the gate refused and from what
        // the device got wrong. Neither the operator nor the firmware owner can act on it.
        var notAddressed = new List<string>();

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
            // Asked before the read, because afterwards the two reasons for an unread value are
            // indistinguishable: ByteOrDefault returns Unreadable both for a pair the gate blocks
            // and for one the device refuses.
            var transmissionBlocked = !session.MayTransfer(comm, 0x02);
            var (transmission, transmissionOutcome) = await ByteOrDefault(session, comm, 0x02, problems, blocked, mandatory: true, cancellationToken).ConfigureAwait(false);
            var inhibit = await UShortOrDefault(session, comm, 0x03, problems, cancellationToken).ConfigureAwait(false);
            var eventTimer = await UShortOrDefault(session, comm, 0x05, problems, cancellationToken).ConfigureAwait(false);
            // 1800h:06 is the SYNC start value, optional: the counter value at which this TPDO
            // begins its cycle (CiA 301 §7.5.2.37).
            var (syncStart, syncStartOutcome) = await ByteOrDefault(session, comm, 0x06, problems, blocked, mandatory: false, cancellationToken).ConfigureAwait(false);

            byte? syncStartReserved = null;

            // 0..240, which the stack enforces on write: CanOpenNode.Pdo.cs rejects 1800h:06 above
            // 240 with ValueRangeExceeded, and its README §"SYNC start value" documents the range.
            // A device reporting 241..255 was recorded as a successful read, so PDO-01 passed it
            // and SYN-02 then sized its SYNC burst from a start value no device can honour —
            // passing a device that ignores it, or reading its silence as a transmission failure.
            // The sixth object in the read-is-not-valid family, and this one the stack settles
            // rather than a table reading. Found in review of #388.
            if (syncStart is { } start && start > CanOpenTransmissionType.SynchronousCyclicMax)
            {
                problems.Add($"0x{comm:X4}:06 is {start}; a SYNC start value is 0..240 and 241..255 is reserved.");

                // The outcome goes with the value. Clearing syncStart alone left the outcome at
                // Read, so the descriptor came out with SyncStartValue = 0 and SyncStartRead =
                // true — and SYN-02 then walked past its own unread-start guard and measured the
                // malformed TPDO as though it had no start value. Under --groups sync the sweep's
                // problem is not even in the verdict, so the device could pass. Found in review
                // of #388, against the fix one commit earlier.
                // Neither Read (which let SYN-02 walk past its guard, two commits ago) nor
                // Unreadable (which made SYN-02 report that the object "would not return" and stay
                // inconclusive — a spec violation exiting 0 under --groups sync, one commit ago).
                // It was read, and what came back is invalid: the descriptor says so, and SYN-02
                // fails on it the way it already fails on a reserved 1019h. Found in review
                // of #388.
                syncStart = null;
                syncStartReserved = start;
            }

            // Read is not the same as valid. F1h..FBh is reserved for a TPDO and F1h..FDh for an
            // RPDO (CiA 301 Table 72), and the stack already names both ranges, so a device
            // answering with one was recorded as a successfully read transmission type and
            // PDO-01/PDO-02 passed it. Found in review of #388 — the same shape as the reserved
            // mapping count of the round before.
            if (transmission is { } type)
            {
                var reserved = commBase == 0x1800
                    ? CanOpenTransmissionType.IsReservedForTpdo(type)
                    : CanOpenTransmissionType.IsReservedForRpdo(type);
                if (reserved)
                {
                    problems.Add($"0x{comm:X4}:02 is 0x{type:X2}, which CiA 301 Table 72 reserves for "
                        + $"{(commBase == 0x1800 ? "a TPDO" : "an RPDO")}; it is not a transmission type.");
                }
            }

            var (bits, entries, mappingComplete, multiplexed) = await ReadMappingAsync(session, map, problems, blocked, notAddressed, cancellationToken).ConfigureAwait(false);

            // Bit 29 of the COB-ID word selects the 29-bit frame format (CiA 301 §7.5.2.37).
            // Masking with the 11-bit CanIdMask regardless threw most of an extended identifier
            // away, so the check watched an unrelated base id and then failed a conforming PDO for
            // silence. Found in review of #388.
            var extended = (cobIdWord & CanOpenCobId.ExtendedFrameBit) != 0;
            var cobId = extended ? cobIdWord & 0x1FFF_FFFFu : cobIdWord & CanOpenCobId.CanIdMask;

            // Masking is not validating. A base-frame word with bits 11..28 set, or one naming a
            // CAN-ID CiA 301 §7.3.5 restricts, was masked down to something that looked valid, so
            // the record passed and PDO-03 then watched an id the device never declared. The stack
            // rejects exactly these words in IsUsablePdoCobIdWord, which is private, so the two
            // conditions are spelled out here against the same public helper it uses. Found in
            // review of #388.
            // Both conditions are for a base frame only, as the stack's own helper is: it accepts
            // any extended word, because bits 11..28 are part of a 29-bit identifier and the
            // restricted list is of 11-bit ids.
            //
            // And both are for an *enabled* PDO only. Bit 31 of the word disables the PDO, and a
            // disabled one is not a communication object on the bus: devices routinely park one
            // at 000h, or leave the whole word at FFFFFFFFh, and the previous commit's version of
            // this check failed those conforming devices and recorded no descriptor for the slot.
            // The id is reported as what it is instead. Found in review of #388, against the fix
            // one commit earlier that added the validation without looking at bit 31.
            var enabled = (cobIdWord & CanOpenCobId.InvalidBit) == 0;
            if (enabled && !extended && (cobIdWord & 0x1FFF_F800) != 0)
            {
                problems.Add($"0x{comm:X4}:01 is 0x{cobIdWord:X8}: bit 29 is clear, so this is an 11-bit COB-ID, and "
                    + "bits 11..28 are reserved and must be zero.");
                // No descriptor: the masked id is not what the device declared, and adding it
                // would have PDO-03 watch an identifier nobody uses.
                continue;
            }

            if (enabled && !extended && CanOpenCobId.IsRestricted(cobId))
            {
                problems.Add($"0x{comm:X4}:01 names CAN-ID 0x{cobId:X3}, which CiA 301 §7.3.5 restricts to another "
                    + "communication object; a PDO may not use it.");
                continue;
            }

            descriptors.Add(new PdoDescriptor(
                Number: i + 1,
                CobId: cobId,
                Extended: extended,
                Valid: enabled,
                TransmissionType: transmission ?? 0xFF,
                TransmissionTypeRead: transmissionOutcome == ReadOutcome.Read,
                TransmissionTypeBlocked: transmissionBlocked,
                SyncStartValue: syncStart ?? 0,
                // Absent is resolved: the device has no start value, so 0 is the right answer.
                // Only Unreadable leaves the prerequisite open.
                SyncStartRead: syncStartOutcome != ReadOutcome.Unreadable,
                SyncStartReserved: syncStartReserved,
                InhibitMs: inhibit is { } inh ? inh / 10 : 0, // 1800h:03 counts 100 us units
                EventTimerMs: eventTimer ?? 0,
                Bytes: mappingComplete ? (bits + 7) / 8 : 0,
                MappingComplete: mappingComplete,
                MultiplexedMode: multiplexed,
                MappedEntries: entries));
        }

        // PDOs past the limit, named rather than silently outside the sweep. CiA 301 allows 512
        // of each and this reads the first four, which is a defensible scope for a tool — but a
        // device with a TPDO5 then had its COB-ID undiscovered, so PDO-03 could report that
        // nothing was transmitted in Stopped while that TPDO kept transmitting, and the report's
        // coverage section did not list it. A tool whose own report overstates what it covered is
        // the failure this report is built to avoid. The bound description is the evidence: it
        // declares which PDOs the device has. Found in review of #388 (P1).
        if (session.TargetDescription is { } description)
        {
            var beyond = description.Objects.Objects
                .Select(o => o.Key)
                .Where(i => i > commBase + PdoCount - 1 && i < commBase + 512)
                .OrderBy(i => i)
                .ToList();
            if (beyond.Count > 0)
            {
                notAddressed.Add($"the bound description declares {beyond.Count} further communication record(s) — "
                    + string.Join(", ", beyond.Take(8).Select(i => $"0x{i:X4}"))
                    + (beyond.Count > 8 ? ", …" : string.Empty)
                    + $" — and this runner reads the first {PdoCount} of each direction, so they were neither read "
                    + "nor watched");
            }
        }

        return new PdoSweep(descriptors, blocked, absent, problems, notAddressed);
    }

    private static async Task<(int Bits, int Entries, bool Complete, bool Multiplexed)> ReadMappingAsync(
        Session session, ushort map, List<string> problems, List<string> blocked, List<string> notAddressed,
        CancellationToken cancellationToken)
    {
        var anyEntryBlocked = false;
        if (!session.MayTransfer(map, 0x00))
        {
            // Blocked, not absent, and not the device's fault: it was never asked. This goes to
            // `blocked`, which Summarise reports as a setup limit, and not to `problems`, which it
            // attributes to the device — an earlier version put it there and turned a pair the
            // bound description omits into a device conformance failure. Found in review of #388.
            blocked.Add($"0x{map:X4} (the mandatory mapping record of a PDO whose communication record exists): "
                + session.GateReason(map, 0x00));
            return (0, 0, false, false);
        }

        try
        {
            var count = await session.Client.SdoUploadAsync(session.Target, map, 0x00, cancellationToken).ConfigureAwait(false);
            if (count.Length != 1)
            {
                problems.Add($"0x{map:X4}:00 is UNSIGNED8, got {IdentityAndSdoChecks.Hex(count)}");
                return (0, 0, false, false);
            }

            if (count[0] > 64 && count[0] < (byte)MpdoAddressMode.Source)
            {
                // 41h..FDh is reserved; CanKit.Pro's own README documents it as an invalid mapping
                // count. Reading the first 64 and reporting the device's full count as complete
                // let a malformed device pass. Found in review of #388.
                problems.Add($"0x{map:X4}:00 is 0x{count[0]:X2}; 41h..FDh is a reserved mapping count and CiA 301 "
                    + "allows at most 64 entries.");
                return (0, 0, false, false);
            }

            if (count[0] is (byte)MpdoAddressMode.Source or (byte)MpdoAddressMode.Destination)
            {
                // Not an entry count: these two values put the PDO into multiplexed mode, which
                // this runner states it does not address. Treated as an unmeasured mapping so the
                // observation checks withhold payload-length validation instead of comparing
                // frames against a length derived from sub-indices that do not exist.
                notAddressed.Add($"0x{map:X4}:00 is 0x{count[0]:X2}, which selects "
                    + $"{(count[0] == (byte)MpdoAddressMode.Source ? "source" : "destination")}-addressed MPDO rather "
                    + "than declaring mapping entries. Multiplexed PDOs are outside what this runner checks.");
                return (0, 0, false, true);
            }


            var bits = 0;
            for (byte sub = 1; sub <= count[0] && sub <= 64; sub++)
            {
                if (!session.MayTransfer(map, sub))
                {
                    // Silently dropping its bits left a short byte total beside a full entry
                    // count, and PDO-03 then enforced a payload length nobody had measured.
                    blocked.Add($"0x{map:X4}:{sub:X2} (one of the {count[0]} mapping entries the device declares): "
                        + session.GateReason(map, sub));
                    anyEntryBlocked = true;
                    continue;
                }

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
                    anyEntryBlocked = true;
                    continue;
                }

                if (entry.Length != 4)
                {
                    problems.Add($"0x{map:X4}:{sub:X2} is UNSIGNED32, got {IdentityAndSdoChecks.Hex(entry)}");
                    // Incomplete, not merely noted: the byte total below would otherwise be the
                    // sum of the readable entries and PDO-03 would validate payloads against it.
                    anyEntryBlocked = true;
                    continue;
                }

                // The low byte of a mapping entry is its bit length, and 1..64 is the whole of it
                // (CiA 301 §7.5.2.38). Accumulating it unchecked let two 64-bit entries report a
                // 16-byte payload as a complete mapping, which PDO-03 would then have compared
                // real frames against — on a bus whose frames hold at most 8. Found in review of
                // #388; the width is as much a read-is-not-valid case as the count was.
                if (entry[0] is 0 or > 64)
                {
                    problems.Add($"0x{map:X4}:{sub:X2} declares a field of {entry[0]} bit(s); a mapping entry is 1..64.");
                    anyEntryBlocked = true;
                    continue;
                }

                bits += entry[0];
                if (bits > 64)
                {
                    problems.Add($"0x{map:X4} maps {bits} bits over its first {sub} entry/entries; a PDO holds at most 64.");
                    anyEntryBlocked = true;
                    break;
                }
            }

            return (bits, count[0], !anyEntryBlocked, false);
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
            return (0, 0, false, false);
        }
    }

    /// <summary>
    /// Reads one byte of a PDO communication record. An abort is recorded in
    /// <paramref name="problems"/> rather than swallowed: 1800h:02 and 1400h:02 are mandatory for
    /// an implemented PDO, and an earlier version turned a failed read of one into the plausible
    /// default 0xFF, so PDO-01 and PDO-02 passed as though they had read it. Null means only "the
    /// gate blocks it" or "the device returned the wrong length", both of which are recorded too.
    /// </summary>
    private static async Task<(byte? Value, ReadOutcome Outcome)> ByteOrDefault(
        Session session, ushort index, byte sub, List<string> problems, List<string> blocked, bool mandatory,
        CancellationToken cancellationToken)
    {
        if (!session.MayTransfer(index, sub))
        {
            // For an optional entry, a pair the description does not declare is an absence: the
            // device, per that description, does not have it. For a *mandatory* one it is a
            // contradiction in the description itself, and saying nothing let the check pass while
            // reporting the value as "not read". Found in review of #388.
            if (mandatory)
            {
                // Setup, not firmware: the device was never asked. Recorded on `blocked` so
                // Summarise reports it as the description's own contradiction, and returned as
                // Unreadable so nothing downstream treats the missing value as a default.
                blocked.Add($"0x{index:X4}:{sub:X2} is mandatory for an implemented PDO and "
                    + session.GateReason(index, sub));
                return (null, ReadOutcome.Unreadable);
            }

            return (null, ReadOutcome.Absent);
        }

        try
        {
            var raw = await session.Client.SdoUploadAsync(session.Target, index, sub, cancellationToken).ConfigureAwait(false);
            if (raw.Length == 1) return (raw[0], ReadOutcome.Read);
            problems.Add($"0x{index:X4}:{sub:X2} is UNSIGNED8, got {IdentityAndSdoChecks.Hex(raw)}");
            return (null, ReadOutcome.Unreadable);
        }
        catch (SdoAbortException abort) when (abort.AbortCode is (uint)SdoAbortCode.ObjectDoesNotExist
                                                  or (uint)SdoAbortCode.SubIndexDoesNotExist)
        {
            // Genuinely absent. For a mandatory entry that is still a contradiction; for an
            // optional one it is the device saying it has none, which is the CiA 301 default.
            if (mandatory)
                problems.Add($"0x{index:X4}:{sub:X2} is mandatory for an implemented PDO and the device says it does not exist");
            return (null, ReadOutcome.Absent);
        }
        catch (SdoAbortException abort)
        {
            // Not absence: the entry may well be there and simply would not come back.
            problems.Add($"0x{index:X4}:{sub:X2} is declared{(mandatory ? " and mandatory" : "")} and aborted "
                + IdentityAndSdoChecks.Describe(abort));
            return (null, ReadOutcome.Unreadable);
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
        catch (SdoAbortException abort) when (abort.AbortCode is (uint)SdoAbortCode.ObjectDoesNotExist
                                                  or (uint)SdoAbortCode.SubIndexDoesNotExist)
        {
            // 1800h:03 and 1800h:05 are optional; their absence is not a problem.
            return null;
        }
        catch (SdoAbortException abort)
        {
            // Not absence. Losing 1800h:05 this way erased that a TPDO is event-timed, so the
            // per-descriptor overdue check could not apply to it. Found in review of #388 — the
            // same correction the one-byte path got a round earlier, which I had applied to only
            // half of this pair.
            problems.Add($"0x{index:X4}:{sub:X2} is declared and aborted {IdentityAndSdoChecks.Describe(abort)}");
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
        if (sweep.NotAddressed.Count > 0) measurements["not addressed by this runner"] = string.Join(", ", sweep.NotAddressed);
        if (sweep.Absent.Count > 0) measurements["not implemented"] = string.Join(", ", sweep.Absent);
        foreach (var (problem, i) in sweep.Problems.Take(10).Select((p, i) => (p, i)))
            measurements[$"problem {i + 1}"] = problem;

        if (sweep.Problems.Count > 0)
        {
            return Outcome.Fail(
                $"{sweep.Descriptors.Count} {kind}(s) read; {sweep.Problems.Count} parameter(s) the device would not "
                + "return as declared.", measurements);
        }

        // A pair the gate blocked is the run's own input, not a device defect: the device was
        // never asked. Reported only when it actually left a mandatory parameter unmeasured, and
        // only for descriptors the gate is responsible for — an MPDO is on its own list now,
        // because telling the operator to bind a description they have already bound is worse than
        // saying nothing. Found in review of #388.
        //
        // Both kinds of unmeasured parameter count, not the mapping alone. A description that
        // declares 0x..01 and the whole mapping record but omits the mandatory transmission type
        // at 0x..02 put that pair on `Blocked` while the mapping stayed complete, so this
        // predicate was false, `Problems` was empty, and the verdict was Pass with the type
        // printed as "not read" — the same false pass one level up from the one that moving the
        // mandatory-field case onto `Blocked` was meant to fix. Found in review of #388.
        //
        // The transmission-type clause asks for a *valid* descriptor: a PDO disabled by bit 31 of
        // its COB-ID transmits nothing, so not knowing when it would have transmitted costs the
        // run nothing.
        var incomplete = sweep.Descriptors
            .Where(d => !d.MultiplexedMode && (!d.MappingComplete || (d.Valid && !d.TransmissionTypeRead)))
            .ToList();
        if (sweep.Blocked.Count > 0 && incomplete.Count > 0)
        {
            return Outcome.NeedsSetup(
                $"{sweep.Descriptors.Count} {kind}(s) read, but the peer-SDO gate blocked {sweep.Blocked.Count} pair(s) "
                + $"belonging to the mandatory records of {string.Join(", ", incomplete.Select(d => kind + d.Number))}, so "
                + "what those should carry, or when, is unknown and PDO-03 cannot judge what it sees: "
                + string.Join("; ", sweep.Blocked.Take(5)));
        }

        // A multiplexed PDO is neither the operator's problem nor the device's. The other
        // descriptors are still measured; only this one is set aside.
        // A PDO the sweep never reached makes the verdict incomplete, whatever the ones it did
        // reach say: "no TPDO was transmitted in Stopped" is a claim about every TPDO. Reported
        // as setup because the limit is the run's, not the device's. Found in review of #388.
        var beyondLimit = sweep.NotAddressed
            .Where(n => n.Contains("further communication record", StringComparison.Ordinal))
            .ToList();
        if (beyondLimit.Count > 0)
        {
            return Outcome.NeedsSetup(
                $"{sweep.Descriptors.Count} {kind}(s) were read and found sound, but " + beyondLimit[0]
                + ". Nothing is concluded about the ones that were not: a verdict on \"every PDO\" cannot be drawn "
                + "from the first four.", measurements);
        }

        if (sweep.NotAddressed.Count > 0 && sweep.Descriptors.All(d => d.MultiplexedMode || !d.Valid))
        {
            return Outcome.NotApplicable(
                $"Every valid {kind} is in multiplexed mode, which this runner does not check: "
                + string.Join("; ", sweep.NotAddressed));
        }

        if (sweep.Descriptors.Count == 0)
        {
            return sweep.Blocked.Count > 0
                ? Outcome.NeedsSetup($"Every {kind} parameter is blocked: {session.GateReason((ushort)(kind == "TPDO" ? 0x1800 : 0x1400), 0x01)}")
                : Outcome.NotApplicable($"The device implements none of the first {PdoCount} {kind}s.");
        }

        var valid = sweep.Descriptors.Count(p => p.Valid);
        var multiplexed = sweep.Descriptors.Count(p => p.MultiplexedMode);
        return Outcome.Pass(
            $"{sweep.Descriptors.Count} {kind}(s) read, {valid} valid"
            + (multiplexed > 0 ? $", {multiplexed} set aside as multiplexed." : "."), measurements);
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
