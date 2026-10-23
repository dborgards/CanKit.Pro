using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Sdo;

namespace CanKit.Pro.CanOpenConformance.Checks;

/// <summary>
/// Error control and network management: the heartbeat the device produces, the node guarding it
/// answers, and whether it follows the NMT state machine. The heartbeat is measured rather than
/// asserted against a tolerance — the period is reported with the interval it was observed over,
/// so the number can be read against the host's own jitter instead of a bound invented here.
/// </summary>
internal static class NmtAndHeartbeatChecks
{
    /// <summary>
    /// How much longer than one heartbeat period an observation window must be before silence in
    /// it is charged to the device. The quantity this covers is the delay between opening the tap
    /// and the producer's next beat — thread scheduling and driver buffering, tens of milliseconds
    /// on a loaded host — not an allowance for the period itself being wrong.
    /// </summary>
    private const int WindowMarginMs = 200;

    /// <summary>
    /// The longest a single check will wait for a device's own period to elapse. Above it the
    /// check reports that it was not measured instead of shortening the wait: a cap that silently
    /// truncates a measurement replaces it with a different one, which is how a conforming slow
    /// producer came to be reported as a defect in the stack.
    /// </summary>
    private const int MaxObservationMs = 60_000;

    /// <summary>
    /// The verdict for "no heartbeat was observed", wherever that is concluded: no frame at all, or
    /// frames that turned out to be boot-up messages or another master's node-guarding responses.
    /// </summary>
    /// <remarks>
    /// One place, because two were one too many. Both questions this asks — is 1017h zero, and
    /// could the window have held a beat — were written on the no-frames-at-all branch and then had
    /// to be rediscovered on the heartbeat-candidate branch, once per review round: first the
    /// 1017h lookup, which a reboot during the window bypassed, and then the window guard, which
    /// the same single boot-up frame bypassed in exactly the same way. Patching the second
    /// instance would have left the shape that produced both. Found in review of #388.
    /// </remarks>
    private static async Task<Outcome> NoHeartbeatAsync(
        Session session, uint cobId, Dictionary<string, string> measurements, int bootUps,
        int guardPolls, int guardResponses, CancellationToken cancellationToken)
    {
        // HBT-01 has already read 1017h, so this does not have to hedge: a verified zero is the
        // device configured not to produce a heartbeat, which CiA 301 permits and which is not an
        // unresolved device problem.
        var configured = await ProducerHeartbeatTimeAsync(session, cancellationToken).ConfigureAwait(false);
        if (configured == 0)
        {
            measurements["1017h producer heartbeat time"] = "0 ms (heartbeat switched off)";
            return Outcome.NotApplicable(
                $"The device is configured not to produce a heartbeat (1017h = 0), so nothing on 0x{cobId:X3} "
                + "is expected"
                + (bootUps > 0 ? $"; the {bootUps} frame(s) seen are boot-up messages." : ".")
                + " That is an optional configuration, not a fault.", measurements);
        }

        // Before charging the silence to the device, the window has to have been able to contain a
        // beat. A conforming 10-second producer observed for the default 4 seconds is silent for
        // the whole window, and calling that "not producing what it is set to" is a device finding
        // drawn from the run's own input. The margin is named rather than tuned: the perturbation
        // is the delay between opening the tap and the producer's next beat — scheduling and
        // driver buffering, tens of milliseconds — so one period plus WindowMarginMs is the
        // smallest window this will draw a device finding from.
        if (configured is { } tooShort && session.Options.HeartbeatWindowMs < tooShort + WindowMarginMs)
        {
            return Outcome.NeedsSetup(
                $"1017h says {tooShort} ms and the observation window was "
                + $"{session.Options.HeartbeatWindowMs} ms, which cannot contain a beat of that period"
                + (bootUps > 0 ? $" (the {bootUps} frame(s) seen are boot-up messages)" : string.Empty)
                + $". Re-run with --heartbeat-ms at least {tooShort + WindowMarginMs}; nothing is concluded "
                + "about the device from a window shorter than its own period.", measurements);
        }

        // Another master's guarding, after the two questions above and before the one below. It is
        // a reason the observation is unusable rather than a fact about the device, so it must not
        // reach the device verdict — but a verified 1017h of zero and a window too short to hold a
        // beat each explain the silence on their own, and naming the neighbour instead would send
        // the operator after the wrong thing.
        //
        // A window holding only an RTR poll or a toggle-one response leaves no heartbeat candidate,
        // so this helper was reached before the guarding branch at the end of HBT-02 could ever run
        // and the absence was charged to the device. Moving the two shared questions in here fixed
        // one bypass and opened this one. Found in review of #388.
        if (guardPolls > 0 || guardResponses > 0)
        {
            return Outcome.Inconclusive(
                $"Another master is node-guarding this device on the same COB-ID ({guardPolls} RTR poll(s), "
                + $"{guardResponses} toggle-one response(s)), and no heartbeat candidate is left in the window"
                + (configured is { } guarded ? $", although 1017h says {guarded} ms" : string.Empty)
                + ". Nothing about the device's own heartbeat can be concluded from that. Stop the other master's "
                + "guarding, or read 100Ch/100Dh — see GRD-01.",
                Attribution.Setup, measurements);
        }

        return Outcome.Inconclusive(
            (bootUps > 0
                ? $"No heartbeat on 0x{cobId:X3}: the {bootUps} frame(s) seen are boot-up messages (00h), which "
                  + "CiA 301 does not report in a heartbeat. The device rebooted during the window and is not "
                  + "producing one"
                : $"No heartbeat on 0x{cobId:X3} in {session.Options.HeartbeatWindowMs} ms")
            + (configured is { } ms ? $", although 1017h says {ms} ms." : "."),
            Attribution.Device, measurements);
    }

    public static async Task RunAsync(Session session, Checkbook book, CancellationToken cancellationToken)
    {
        await book.RunAsync("HBT-01", "heartbeat", "1017h producer heartbeat time", Tier.Read, "FR-CO-008", async () =>
        {
            if (!session.MayTransfer(0x1017, 0x00))
                return Outcome.NeedsSetup(session.GateReason(0x1017, 0x00));

            try
            {
                var raw = await session.Client.SdoUploadAsync(session.Target, 0x1017, 0x00, cancellationToken)
                    .ConfigureAwait(false);
                if (raw.Length != 2)
                    return Outcome.Fail($"1017h:00 is UNSIGNED16, the device returned {IdentityAndSdoChecks.Hex(raw)}.");

                var configured = BitConverter.ToUInt16(raw, 0);
                var measurements = new Dictionary<string, string>
                {
                    ["1017h producer heartbeat time"] = $"{configured} ms{(configured == 0 ? " (heartbeat off)" : string.Empty)}",
                };
                return Outcome.Pass(configured == 0
                    ? "The device is configured not to produce a heartbeat; HBT-02 will find nothing, by configuration."
                    : $"The device is configured to produce a heartbeat every {configured} ms.", measurements);
            }
            catch (SdoAbortException abort) when (abort.AbortCode is (uint)SdoAbortCode.ObjectDoesNotExist)
            {
                return Outcome.NotApplicable("The device has no 1017h: a heartbeat producer is optional in CiA 301.");
            }
        }).ConfigureAwait(false);

        await book.RunAsync("HBT-02", "heartbeat", "Heartbeat observed on 700h+node-id", Tier.Read, "CiA 301 heartbeat", async () =>
        {
            var cobId = CanOpenCobId.Heartbeat(session.Target);
            using var tap = new BusTap(session.Service, (id, extended) => id == cobId && !extended);
            // Bounded at the window the report names, not the window plus the drain that makes it
            // complete: a beat arriving in those 50 ms is real traffic, but it did not arrive
            // inside --heartbeat-ms and the period must not be measured as though it had. Found in
            // review of #388.
            var untilMs = await tap.ListenAsync(session.Options.HeartbeatWindowMs, cancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<TappedFrame> frames = tap.Through(untilMs);

            var measurements = new Dictionary<string, string>
            {
                ["COB-ID"] = $"0x{cobId:X3}",
                ["window"] = $"{session.Options.HeartbeatWindowMs} ms",
                ["frames"] = frames.Count.ToString(CultureInfo.InvariantCulture),
            };

            if (frames.Count == 0)
            {
                return await NoHeartbeatAsync(session, cobId, measurements, bootUps: 0,
                    guardPolls: 0, guardResponses: 0, cancellationToken).ConfigureAwait(false);
            }

            // Another master's node-guarding poll uses this same COB-ID as a remote frame
            // (CanOpenNode.NodeGuarding.cs), and its empty payload is not a malformed heartbeat —
            // it is not the target's traffic at all. Found in review of #388.
            var guardPolls = frames.Count(f => f.IsRemote);
            var guardResponses = frames.Count(f => !f.IsRemote && f.Data.Length == 1 && IsGuardingResponse(f.Data[0]));
            if (guardPolls > 0) measurements["node-guarding RTR polls (another master)"] = guardPolls.ToString(CultureInfo.InvariantCulture);
            if (guardResponses > 0)
            {
                measurements["node-guarding responses (toggle bit set)"] =
                    guardResponses.ToString(CultureInfo.InvariantCulture);
            }

            frames = frames.Where(f => !f.IsRemote && !(f.Data.Length == 1 && IsGuardingResponse(f.Data[0]))).ToArray();
            measurements["frames (heartbeat candidates)"] = frames.Count.ToString(CultureInfo.InvariantCulture);

            // The state byte is the whole payload of a heartbeat. A device sending anything else
            // is not sending a heartbeat, whatever the COB-ID says.
            var states = frames.Select(f => f.Data.Length == 1 ? f.Data[0] : (byte?)null).ToArray();
            var malformed = states.Count(s => s is null);
            if (malformed > 0)
                measurements["malformed (payload not 1 byte)"] = malformed.ToString(CultureInfo.InvariantCulture);

            var observed = states.Where(s => s is not null).Select(s => s!.Value).Distinct().ToArray();
            measurements["states seen"] = observed.Length == 0
                ? "(none)"
                : string.Join(", ", observed.Select(DescribeState));

            // 00h is the boot-up message, not a heartbeat: CiA 301 never reports Initializing in
            // one, and the stack's own NmtState documentation says as much. Counting it meant a
            // device that rebooted with its heartbeat switched off was reported as producing one.
            // Found in review of #388.
            var bootUps = states.Count(s => s == (byte)NmtState.Initializing);
            var heartbeats = frames.Count - malformed - bootUps;
            if (bootUps > 0) measurements["boot-up messages (00h, not heartbeats)"] = bootUps.ToString(CultureInfo.InvariantCulture);

            if (BusTap.Summarise(Heartbeats(frames)) is { } summary)
            {
                measurements["period (host clock)"] = summary.ToString();
                measurements["note"] = "The period is measured on this host's clock, not the device's; "
                    + "a loaded host widens the range without the device changing.";
            }
            else
            {
                measurements["period (host clock)"] = $"not measurable from {frames.Count} frame(s)";
            }

            var unknown = observed.Where(s => !Enum.IsDefined(typeof(NmtState), s)).ToArray();
            if (unknown.Length > 0)
            {
                return Outcome.Fail(
                    $"The heartbeat carries state byte(s) {string.Join(", ", unknown.Select(u => $"0x{u:X2}"))}, which "
                    + "CiA 301 does not define (04h stopped, 05h operational, 7Fh pre-operational, 00h boot-up).",
                    measurements);
            }

            // Any malformed frame, not only all of them. A heartbeat's payload is one byte; a frame
            // on this COB-ID with another length is a violation the report must not hide behind
            // the valid ones beside it. Found in review of #388.
            if (malformed > 0)
            {
                return Outcome.Fail(
                    malformed == frames.Count
                        ? $"All {frames.Count} frame(s) on 0x{cobId:X3} have a payload other than one byte."
                        : $"{malformed} of {frames.Count} frame(s) on 0x{cobId:X3} have a payload other than one byte. "
                          + "A heartbeat is one byte of NMT state.",
                    measurements);
            }

            if (heartbeats <= 0)
            {
                return await NoHeartbeatAsync(session, cobId, measurements, bootUps,
                    guardPolls, guardResponses, cancellationToken).ConfigureAwait(false);
            }

            // Only now. A toggle-zero guarding response carries the bare state and is
            // byte-for-byte a heartbeat, so the two cannot be separated — but a multi-byte frame
            // or an undefined state byte is neither, and those are the device's defects. An
            // earlier version returned here first and turned them into exit 0 whenever another
            // master happened to be polling. Found in review of #388.
            if (guardPolls > 0 || guardResponses > 0)
            {
                return Outcome.Inconclusive(
                    $"Another master is node-guarding this device on the same COB-ID ({guardPolls} RTR poll(s), "
                    + $"{guardResponses} toggle-one response(s)). A guarding response with its toggle bit clear is "
                    + "byte-for-byte a heartbeat, so the two cannot be separated here and no heartbeat verdict is "
                    + "drawn from the remaining frames. Nothing malformed or undefined was seen, which the checks "
                    + "above would have failed. Stop the other master's guarding, or read 100Ch/100Dh — see GRD-01.",
                    Attribution.Setup, measurements);
            }

            return Outcome.Pass($"{heartbeats} heartbeat(s) observed.", measurements);
        }).ConfigureAwait(false);

        await book.RunAsync("HBT-03", "heartbeat", "Heartbeat consumer reports the producer's beats", Tier.Read, "FR-CO-008", async () =>
        {
            // This measures the stack's consumer, not the device: the device only supplies the
            // frames. A failure is therefore attributed to the tool.
            //
            // It deliberately claims no more than its title. An earlier version was called "raises
            // a timeout when the device stops" and passed whenever no timeout was raised, without
            // ever stopping the producer — so a consumer that never raises HeartbeatTimeout would
            // have passed a check named after that event. HBT-04 provokes the loss instead; this
            // one is the half that needs no write. Found in review of #388.
            var period = await ObservedPeriodAsync(session, cancellationToken).ConfigureAwait(false);
            if (period is not { } beat)
                return Outcome.NeedsSetup("The device produces no heartbeat to consume (see HBT-02).");

            var received = 0;
            var timedOut = 0;
            void OnBeat(object? sender, HeartbeatReceivedEventArgs e)
            {
                // The three run states only. The stack reports the 00h boot-up through this same
                // event as NmtState.Initializing (CanOpenEvents.cs), so counting every event
                // credited a reboot as a consumed heartbeat — while Heartbeats(wire.Frames) on the
                // raw side correctly excluded it. A boot-up that rearms the consumer before its
                // timeout then left received > 0 and timedOut == 0, and the check passed with no
                // heartbeat in its own window at all. The same "a boot-up is not a heartbeat" rule
                // HBT-02 already applies to the wire, missing from the decoded path. Found in
                // review of #388.
                if (e.ProducerNodeId == session.Target && IsDefinedRunState((byte)e.State))
                    Interlocked.Increment(ref received);
            }

            void OnTimeout(object? sender, HeartbeatTimeoutEventArgs e)
            {
                if (e.ProducerNodeId == session.Target) Interlocked.Increment(ref timedOut);
            }

            // The wait is the device's period times four, not a constant: the 10-second cap ended
            // the check before the next beat of any producer slower than 2.5 s, and `received` was
            // then still zero — so a conforming device was reported as a defect in the stack. A cap
            // that silently shortens a measurement turns the measurement into a different one.
            // Found in review of #388.
            //
            // Asked before anything is installed, which is the correction that followed: the guard
            // sat *after* AddHeartbeatConsumer, whose timeout is a UInt16 millisecond value
            // (CanOpenNode.ToMilliseconds16 rejects more), so a period above about 21.8 s made the
            // three-period budget exceed 65535 ms and the call threw before the guard could return
            // the unmeasured verdict — a conforming slow producer reported as a tool exception.
            // With the guard first, `needed` bounds the period to MaxObservationMs / 4 and the
            // budget cannot reach that range. Found in review of #388.
            var needed = (int)Math.Max(500, beat.MedianMs * 4 + 300);
            if (needed > MaxObservationMs)
            {
                return Outcome.Inconclusive(
                    $"The device's heartbeat period is {IdentityAndSdoChecks.Ms(beat.MedianMs)}, so four of them "
                    + $"would take {IdentityAndSdoChecks.Ms(needed)} — longer than this check will hold the bus. "
                    + "Nothing is concluded about the consumer, rather than concluding it from a window that "
                    + "could not contain a beat.",
                    Attribution.None,
                    new Dictionary<string, string>
                    {
                        ["observed period (median)"] = IdentityAndSdoChecks.Ms(beat.MedianMs),
                        ["wait this check would need"] = IdentityAndSdoChecks.Ms(needed),
                        ["limit"] = IdentityAndSdoChecks.Ms(MaxObservationMs),
                    });
            }

            session.Client.HeartbeatReceived += OnBeat;
            session.Client.HeartbeatTimeout += OnTimeout;
            try
            {
                // Generous: three periods plus a margin, so a late frame is not a timeout. What is
                // checked is that beats arrive and are not reported as missing, not how tightly
                // the stack tracks the clock.
                var budget = TimeSpan.FromMilliseconds(Math.Max(100, beat.MedianMs * 3 + 200));
                session.Client.AddHeartbeatConsumer(session.Target, budget);

                // A tap over the same interval the consumer is watching. HBT-02 and the period
                // probe are earlier observations: they say the device *was* producing, not that it
                // was producing while this handler was installed. A producer that stops, or a
                // device that is unplugged, between the probe and this window leaves `received` at
                // zero with nothing for the consumer to have missed — and that was reported as a
                // defect in the stack. The claim is about this window, so the evidence has to come
                // from it. Found in review of #388.
                var heartbeatId = CanOpenCobId.Heartbeat(session.Target);
                using var wire = new BusTap(session.Service, (id, extended) => id == heartbeatId && !extended);

                // Through the tap's own window, so the pump is drained and the boundary is the one
                // the verdict names. Swept for after the same defect was reported against HBT-04:
                // a beat still queued at the end would have understated the wire and turned a
                // stack fault into "the device stopped producing". Found in review of #388.
                var wireUntilMs = await wire.ListenAsync(needed, cancellationToken).ConfigureAwait(false);

                var onTheWire = Heartbeats(wire.Through(wireUntilMs)).Count;
                var measurements = new Dictionary<string, string>
                {
                    ["observed period (median)"] = IdentityAndSdoChecks.Ms(beat.MedianMs),
                    ["consumer budget used"] = IdentityAndSdoChecks.Ms(budget.TotalMilliseconds),
                    ["heartbeats on the wire in this window"] = onTheWire.ToString(CultureInfo.InvariantCulture),
                    ["HeartbeatReceived events"] = received.ToString(CultureInfo.InvariantCulture),
                    ["HeartbeatTimeout events"] = timedOut.ToString(CultureInfo.InvariantCulture),
                };

                if (received == 0)
                {
                    // Which of the two it is, decided by the tap rather than assumed. No beat on
                    // the wire means there was nothing to surface, and the device stopping between
                    // the period probe and this window is the device's business, not the stack's.
                    return onTheWire == 0
                        ? Outcome.Inconclusive(
                            "No heartbeat reached the bus during this check's own window, although the period probe "
                            + "had just measured one, so there was nothing for the consumer to report and nothing is "
                            + "concluded about it. The device stopped producing between the two observations.",
                            Attribution.Device, measurements)
                        : Outcome.ToolFault(
                            $"{onTheWire} heartbeat(s) reached the bus during this window and the consumer raised no "
                            + "HeartbeatReceived event. That is a defect in the stack, not in the device.",
                            measurements);
                }

                return timedOut == 0
                    ? Outcome.Pass($"{received} beat(s) consumed, no spurious timeout.", measurements)
                    : Outcome.Inconclusive(
                        $"{received} beat(s) consumed and {timedOut} timeout(s) raised against a budget of "
                        + IdentityAndSdoChecks.Ms(budget.TotalMilliseconds) + " for a period of "
                        + IdentityAndSdoChecks.Ms(beat.MedianMs)
                        + ". Either the device's heartbeat is irregular or this host descheduled the consumer.",
                        Attribution.Unknown, measurements);
            }
            finally
            {
                session.Client.HeartbeatReceived -= OnBeat;
                session.Client.HeartbeatTimeout -= OnTimeout;
                try
                {
                    session.Client.RemoveHeartbeatConsumer(session.Target);
                }
                catch (Exception ex)
                {
                    book.Note("HBT-03 cleanup", ex);
                }
            }
        }).ConfigureAwait(false);

        await book.RunAsync("HBT-04", "heartbeat", "Heartbeat consumer raises a timeout when the producer stops", Tier.Write, "FR-CO-008", async () =>
        {
            // The half that needs a write, and the only way to see the timeout at all: the
            // producer is stopped by writing 1017h = 0, and the original interval is put back in a
            // finally on an uncancellable token — a device left with its heartbeat switched off is
            // a change this run has no business leaving behind.
            if (!session.MayTransfer(0x1017, 0x00))
                return Outcome.NeedsSetup(session.GateReason(0x1017, 0x00));

            var period = await ObservedPeriodAsync(session, cancellationToken).ConfigureAwait(false);
            if (period is not { } beat)
                return Outcome.NeedsSetup("The device produces no heartbeat, so there is none to interrupt (see HBT-02).");

            byte[] original;
            try
            {
                original = await session.Client.SdoUploadAsync(session.Target, 0x1017, 0x00, cancellationToken).ConfigureAwait(false);
            }
            catch (SdoAbortException abort)
            {
                return Outcome.NeedsSetup($"1017h:00 could not be read ({IdentityAndSdoChecks.Describe(abort)}), so it cannot be put back afterwards.");
            }

            if (original.Length != 2)
                return Outcome.NeedsSetup("1017h:00 did not read back as UNSIGNED16; see HBT-01.");

            var timedOut = 0;
            void OnTimeout(object? sender, HeartbeatTimeoutEventArgs e)
            {
                if (e.ProducerNodeId == session.Target) Interlocked.Increment(ref timedOut);
            }

            var budget = TimeSpan.FromMilliseconds(Math.Max(100, beat.MedianMs * 2 + 100));

            // Before the write, because the alternative is stopping the device's heartbeat for a
            // measurement this check was never going to wait out. The 15-second cap did exactly
            // that for a period above roughly 7.5 s: the delay ended before one consumer timeout
            // was due and the absence of that timeout was then reported as a ToolFault. Found in
            // review of #388.
            var waitForTimeout = (int)(budget.TotalMilliseconds * 2 + 300);
            if (waitForTimeout > MaxObservationMs)
            {
                return Outcome.Inconclusive(
                    $"The device's heartbeat period is {IdentityAndSdoChecks.Ms(beat.MedianMs)}, so the consumer "
                    + $"budget is {IdentityAndSdoChecks.Ms(budget.TotalMilliseconds)} and provoking its timeout would "
                    + $"take {IdentityAndSdoChecks.Ms(waitForTimeout)} — longer than this check will leave the "
                    + "device's heartbeat switched off. It is not run, so the producer is not touched.",
                    Attribution.None,
                    new Dictionary<string, string>
                    {
                        ["observed period (median)"] = IdentityAndSdoChecks.Ms(beat.MedianMs),
                        ["wait this check would need"] = IdentityAndSdoChecks.Ms(waitForTimeout),
                        ["limit"] = IdentityAndSdoChecks.Ms(MaxObservationMs),
                    });
            }

            var restoredOk = true;
            var restored = "yes";
            string? lostResponse = null;
            // Set before the write, not after it. The device may accept the download and the call
            // still not return — a cancel in that window used to skip the restore and leave the
            // heartbeat switched off, which is exactly the guarantee this check makes. An abort is
            // the one answer that proves nothing was written. Found in review of #388.
            var mayHaveStopped = true;
            var stillBeating = 0;
            var inFlight = 0;
            var timeoutsBeforeStop = 0;
            session.Client.HeartbeatTimeout += OnTimeout;
            try
            {
                session.Client.AddHeartbeatConsumer(session.Target, budget);
                try
                {
                    await session.Client.SdoDownloadAsync(session.Target, 0x1017, 0x00, new byte[] { 0, 0 }, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (SdoAbortException abort) when (abort.Origin == SdoAbortOrigin.Local)
                {
                    // The client gave up on the response. The device may well have applied the
                    // write, so the flag stays set and the finally restores; the check is recorded
                    // here rather than thrown, because leaving it to Checkbook's handler skipped
                    // the restore verdict below and a failed restore became a mere note. Found in
                    // review of #388.
                    lostResponse = IdentityAndSdoChecks.Describe(abort);
                }
                catch (SdoAbortException abort) when (abort.Origin == SdoAbortOrigin.Peer)
                {
                    // Only the peer refusing proves nothing was written. A local abort — the
                    // client giving up because the response was lost — leaves the flag set, so
                    // the restore is still attempted. Found in review of #388.
                    mayHaveStopped = false;
                    return Outcome.NotApplicable(
                        $"The device refuses a write of 0 to 1017h:00 ({IdentityAndSdoChecks.Describe(abort)}), so its "
                        + "heartbeat cannot be stopped from the bus and the timeout cannot be provoked without pulling "
                        + "the device. Nothing is wrong with that: 1017h may be read-only.");
                }

                // Two budgets, so a beat already in flight when the write landed cannot be
                // mistaken for the producer still running. Not capped: the guard above decided
                // whether this wait is affordable, and it did so before the producer was stopped.
                //
                // Watched on the wire as well, because "no timeout was raised" has two causes and
                // only one of them is the stack's. A device that acknowledges 1017h = 0 and then
                // keeps producing gives the consumer nothing to time out about — correctly — and
                // that was reported as a defect in this repository, which sends the operator to
                // the wrong place and hides the device failing to apply its own configuration.
                // Found in review of #388.
                //
                // The timeout count starts here, not at zero. The consumer was installed before
                // the write, and its budget can expire while the SDO response is still
                // outstanding — so `timedOut` could already be positive for a timeout that had
                // nothing to do with the stop, and the Pass branch would then accept a consumer
                // that never timed out for the stopped interval at all: the exact defect this
                // check exists to find. Found in review of #388.
                timeoutsBeforeStop = Volatile.Read(ref timedOut);

                var stoppedId = CanOpenCobId.Heartbeat(session.Target);
                using (var afterStop = new BusTap(session.Service, (id, extended) => id == stoppedId && !extended))
                {
                    // Through ListenAsync, so the pump is drained and the boundary is explicit.
                    // A plain Task.Delay plus afterStop.Frames read whatever had been delivered by
                    // that instant: a final heartbeat from an intermittently continuing producer
                    // could still be queued, leaving stillBeating == 0 and passing a device that
                    // never obeyed 1017h = 0. Found in review of #388.
                    var stoppedUntilMs = await afterStop.ListenAsync(waitForTimeout, cancellationToken)
                        .ConfigureAwait(false);

                    // Classified by arrival time, not merely counted. The tap opens immediately
                    // after the write returns, so a beat already on the wire when the write landed
                    // shows up within about one period — which is expected, and is the reason the
                    // wait is two budgets in the first place. Counting it as "the device did not
                    // stop" made a leftover frame beside a silent consumer a device defect, when
                    // it is the stack's missed timeout. Only beats after that allowance say the
                    // producer is still running. Found in review of #388, from both reviewers at
                    // once: one for the Pass that bypassed this evidence, one for the in-flight
                    // frame that should not be charged to the device.
                    var inFlightAllowanceMs = beat.MedianMs + WindowMarginMs;
                    var after = Heartbeats(afterStop.Through(stoppedUntilMs));
                    inFlight = after.Count(f => f.AtMs < inFlightAllowanceMs);
                    stillBeating = after.Count - inFlight;
                }
            }
            finally
            {
                session.Client.HeartbeatTimeout -= OnTimeout;
                if (mayHaveStopped)
                {
                    try
                    {
                        await session.Client.SdoDownloadAsync(session.Target, 0x1017, 0x00, original, CancellationToken.None)
                            .ConfigureAwait(false);

                        // Read back, because an acknowledgement is not an application — which is
                        // the argument this check already makes about the *stop* and did not make
                        // about the restore. A device that accepts the write and keeps 1017h at
                        // zero leaves the run having switched its heartbeat off for good, while
                        // "restored: yes" and a Pass say the write tier's promise was kept. This
                        // check deliberately exercises devices that mishandle 1017h, so its own
                        // cleanup is the last place to take their word for it. Found in review
                        // of #388.
                        var back = await session.Client
                            .SdoUploadAsync(session.Target, 0x1017, 0x00, CancellationToken.None).ConfigureAwait(false);
                        if (back.Length != 2 || BitConverter.ToUInt16(back, 0) != BitConverter.ToUInt16(original, 0))
                        {
                            restoredOk = false;
                            restored = $"FAILED — 1017h reads back as {IdentityAndSdoChecks.Hex(back)} after writing "
                                + $"{IdentityAndSdoChecks.Hex(original)}; the device accepted the write and did not "
                                + "apply it, so its heartbeat is not what this run found";
                        }
                    }
                    catch (Exception ex)
                    {
                        book.Note("HBT-04 restore of 1017h", ex);
                        restoredOk = false;
                        restored = $"FAILED ({ex.GetType().Name}) — the device's heartbeat may still be switched off";
                    }
                }

                try
                {
                    session.Client.RemoveHeartbeatConsumer(session.Target);
                }
                catch (Exception ex)
                {
                    book.Note("HBT-04 cleanup", ex);
                }
            }

            var measurements = new Dictionary<string, string>
            {
                ["1017h before"] = $"{BitConverter.ToUInt16(original, 0)} ms",
                ["observed period (median)"] = IdentityAndSdoChecks.Ms(beat.MedianMs),
                ["consumer budget used"] = IdentityAndSdoChecks.Ms(budget.TotalMilliseconds),
                ["HeartbeatTimeout events after the stop"] =
                    (timedOut - timeoutsBeforeStop).ToString(CultureInfo.InvariantCulture),
                ["1017h restored"] = restored,
            };

            // A failed restore outranks the measurement: this check switched the device's heartbeat
            // off on purpose, and a write-tier run that cannot switch it back has broken the one
            // promise the tier makes. Passing on `timedOut` alone let the process exit 0 with the
            // device left mute. Found in review of #388.
            if (!restoredOk)
            {
                return Outcome.Failed(Attribution.Unknown,
                    "1017h could not be written back: " + restored
                    + ". The device is left with its heartbeat disabled and needs attention before it is trusted again.",
                    measurements);
            }

            if (lostResponse is not null)
            {
                measurements["stop write"] = $"no response ({lostResponse}); the device may or may not have applied it";
                return Outcome.Inconclusive(
                    "The write of 1017h = 0 got no answer, so whether the producer was ever stopped is unknown and the "
                    + "timeout below cannot be read as the consumer's behaviour. The original interval was written back "
                    + "regardless, which is the part that matters.", Attribution.Device, measurements);
            }

            measurements["heartbeats within one period of the stop (in flight)"] =
                inFlight.ToString(CultureInfo.InvariantCulture);
            measurements["heartbeats later than that (producer still running)"] =
                stillBeating.ToString(CultureInfo.InvariantCulture);

            // The device's own evidence first, before any verdict about the consumer. A timeout
            // *and* continued traffic happen together on a device that beats intermittently: a gap
            // longer than the budget raises the timeout, and the beats around it prove the
            // producer never stopped. Returning Pass on the timeout alone reported that device as
            // conforming while the run's own measurement said otherwise. Found in review of #388.
            if (stillBeating > 0)
            {
                return Outcome.Fail(
                    $"The device acknowledged the write of 1017h = 0 and then put {stillBeating} heartbeat(s) on the "
                    + $"bus more than one period after it, so it has not applied its own configuration"
                    + (timedOut - timeoutsBeforeStop > 0
                        ? $". The consumer did raise {timedOut - timeoutsBeforeStop} timeout(s), which a gap in that continuing traffic "
                          + "explains and which says nothing in the device's favour"
                        : ". The consumer raised no timeout because there was no missed beat to raise one about — "
                          + "that part is correct")
                    + ".", measurements);
            }

            if (timeoutsBeforeStop > 0)
            {
                measurements["timeouts before the stop (not counted)"] =
                    timeoutsBeforeStop.ToString(CultureInfo.InvariantCulture);
            }

            if (timedOut - timeoutsBeforeStop > 0)
            {
                return Outcome.Pass(
                    $"The producer was stopped and the consumer raised {timedOut - timeoutsBeforeStop} timeout(s) "
                    + "after it.", measurements);
            }

            return Outcome.ToolFault(
                "The producer was stopped by writing 1017h = 0, nothing reached the bus beyond the beat already in "
                + "flight, and the consumer raised no HeartbeatTimeout within two budgets. The missed heartbeat is "
                + "not being detected — a defect in the stack, not in the device.", measurements);
        }).ConfigureAwait(false);

        await book.RunAsync("GRD-01", "guarding", "100Ch/100Dh node guarding parameters", Tier.Read, "FR-CO-009", async () =>
        {
            if (!session.MayTransfer(0x100C, 0x00) || !session.MayTransfer(0x100D, 0x00))
                return Outcome.NeedsSetup(session.GateReason(0x100C, 0x00));

            // One try per object: they shared a catch, so an absent 100Dh reported that the device
            // has no 100Ch. The same shape as STO-01's 1011h and SYN-01's 1006h — swept for here
            // rather than waited for. Found in review of #388.
            byte[] guardTime;
            try
            {
                guardTime = await session.Client.SdoUploadAsync(session.Target, 0x100C, 0x00, cancellationToken).ConfigureAwait(false);
            }
            catch (SdoAbortException abort) when (abort.AbortCode is (uint)SdoAbortCode.ObjectDoesNotExist
                                                      or (uint)SdoAbortCode.SubIndexDoesNotExist)
            {
                return Outcome.NotApplicable("The device has no 100Ch; node guarding is optional and deprecated.");
            }

            try
            {
                var lifeFactor = await session.Client.SdoUploadAsync(session.Target, 0x100D, 0x00, cancellationToken).ConfigureAwait(false);
                var measurements = new Dictionary<string, string>
                {
                    ["100Ch guard time"] = guardTime.Length == 2 ? $"{BitConverter.ToUInt16(guardTime, 0)} ms" : IdentityAndSdoChecks.Hex(guardTime),
                    ["100Dh life time factor"] = lifeFactor.Length == 1 ? lifeFactor[0].ToString(CultureInfo.InvariantCulture) : IdentityAndSdoChecks.Hex(lifeFactor),
                };

                if (guardTime.Length != 2 || lifeFactor.Length != 1)
                    return Outcome.Fail("100Ch is UNSIGNED16 and 100Dh UNSIGNED8; the device returned other lengths.", measurements);

                var time = BitConverter.ToUInt16(guardTime, 0);
                return time == 0 || lifeFactor[0] == 0
                    ? Outcome.Pass("Node guarding is present but switched off (guard time or life time factor is 0); "
                        + "CiA 301 deprecates it in favour of the heartbeat.", measurements)
                    : Outcome.Pass($"Node guarding is active: {time} ms x {lifeFactor[0]}.", measurements);
            }
            catch (SdoAbortException abort) when (abort.AbortCode is (uint)SdoAbortCode.ObjectDoesNotExist
                                                      or (uint)SdoAbortCode.SubIndexDoesNotExist)
            {
                return Outcome.Fail(
                    $"The device has 100Ch ({(guardTime.Length == 2 ? BitConverter.ToUInt16(guardTime, 0) + " ms" : IdentityAndSdoChecks.Hex(guardTime))}) "
                    + "and says it has no 100Dh. CiA 301 defines the two together: a guard time without a life time "
                    + "factor cannot be evaluated.");
            }
        }).ConfigureAwait(false);

        await book.RunAsync("NMT-01", "nmt", "NMT state machine follows the commanded state", Tier.Write, "FR-CO-007", async () =>
        {
            var cobId = CanOpenCobId.Heartbeat(session.Target);
            using var tap = new BusTap(session.Service, (id, extended) => id == cobId && !extended);
            // The operator's window, returning on the first state frame: a fixed 500 ms missed a
            // slower producer depending on its phase and skipped the check although HBT-02 had
            // just observed it. Found in review of #388.
            await tap.WaitForAsync(
                frames => frames.Any(f => !f.IsRemote && f.Data.Length == 1 && IsDefinedRunState(f.Data[0])),
                Math.Max(500, session.Options.HeartbeatWindowMs), cancellationToken).ConfigureAwait(false);

            // Where it was found, so it can be put back. A device left Stopped by a conformance
            // run is a device the next person finds broken.
            var initial = LatestState(tap);

            // The guard is on a *usable* state, not on frames arriving. A device emitting frames
            // with malformed payloads on the heartbeat COB-ID passed the old Frames.Count check,
            // and the run then commanded states with pre-operational as its only fallback — which
            // can leave a device that began Operational in pre-operational, breaking the write
            // tier's promise. Found in review of #388.
            if (initial is null)
            {
                // Data frames, not every frame: a node-guarding RTR from another master on this
                // COB-ID made the count nonzero, so the reason told the operator the device had
                // sent unusable frames when it had sent nothing at all. The verdict is the same
                // either way, but the sentence it carries is what the operator acts on. Found by
                // sweeping for the unfiltered-RTR family in review of #388.
                var fromDevice = tap.Frames.Count(f => !f.IsRemote);
                return Outcome.NeedsSetup(
                    fromDevice == 0
                        ? "Without a heartbeat there is no way to observe the device's NMT state from outside, and this "
                          + "check will not command a state it cannot verify. Enable the heartbeat (1017h) and re-run."
                        : $"{fromDevice} frame(s) arrived on the heartbeat COB-ID and none carries a one-byte NMT "
                          + "state (see HBT-02), so the state before the check cannot be read — and this check will not "
                          + "command a state it would be unable to restore.");
            }
            var measurements = new Dictionary<string, string>
            {
                ["state before"] = initial is { } before ? DescribeState(before) : "(unreadable)",
            };

            var steps = new (NmtCommand Command, NmtState Expected)[]
            {
                (NmtCommand.EnterPreOperational, NmtState.PreOperational),
                (NmtCommand.Start, NmtState.Operational),
                (NmtCommand.Stop, NmtState.Stopped),
                (NmtCommand.EnterPreOperational, NmtState.PreOperational),
            };

            var problems = new List<string>();
            var restoreFailed = false;
            try
            {
                foreach (var (command, expected) in steps)
                {
                    await session.Client.SendNmtCommandAsync(command, session.Target, cancellationToken).ConfigureAwait(false);
                    // The newest *heartbeat*, not the newest frame. On a bus where another master
                    // is guarding this node, an RTR poll or a toggle-one response arriving after a
                    // valid heartbeat displaced it from frames[^1], so the wait timed out and the
                    // step was recorded as a device failure — while LatestState, which filters,
                    // reported the expected state in the very next line. The two now ask the same
                    // question of the same frames. Found in review of #388.
                    var reached = await tap.WaitForAsync(
                        frames => Heartbeats(frames) is { Count: > 0 } beats
                                  && beats[^1].Data[0] == (byte)expected,
                        Math.Max(1000, session.Options.HeartbeatWindowMs), cancellationToken).ConfigureAwait(false);
                    var actual = LatestState(tap);
                    measurements[$"after {command}"] = actual is { } s ? DescribeState(s) : "(no heartbeat)";
                    if (!reached)
                        problems.Add($"{command} did not reach {expected} (heartbeat says {(actual is { } a ? DescribeState(a) : "nothing")})");
                }
            }
            finally
            {
                // Back to where it was found, whatever happened above — including when that was
                // pre-operational. An earlier version skipped the restore in exactly that case, on
                // the reasoning that the last step above ends there anyway; but a cancel or a
                // failure after Start or Stop never reaches the last step, so the commonest
                // power-up state was the one state the check could walk away from. Found in review
                // of #388.
                if (initial is { } restore)
                {
                    // Through the same verifying helper PDO-03 and SYN-02 use. This block used to
                    // send the command and treat a normally completed Task as proof of delivery —
                    // which it is not, since SendNmtCommandAsync discards the transmit result. One
                    // restore path verified and this one did not. Found in review of #388.
                    var restoration = await PdoSyncEmcyChecks.RestoreNmtStateAsync(session, book, "NMT-01", restore)
                        .ConfigureAwait(false);
                    measurements["restored to"] = restoration.State;
                    restoreFailed = !restoration.Succeeded;
                }
            }

            if (restoreFailed)
            {
                return Outcome.Failed(Attribution.Unknown,
                    (problems.Count == 0
                        ? "The device reached every commanded state, but it could not be returned to the state it was "
                        : "The device did not reach every commanded state (" + string.Join("; ", problems) + "), and it could not be returned to the state it was ")
                    + "found in. A write-tier run promises the device is unchanged afterwards; this one has left it "
                    + "somewhere it was not found, which needs attention before the bus is trusted again.", measurements);
            }

            return problems.Count == 0
                ? Outcome.Pass("The device reached every commanded state, and was returned to the state it was found in.", measurements)
                : Outcome.Fail(string.Join("; ", problems) + ".", measurements);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// The heartbeat period as this host saw it, or null when the device produced too few frames
    /// to measure one. Both heartbeat-consumer checks start here, so neither can pick a consumer
    /// budget from a different observation than the other.
    /// </summary>
    private static async Task<IntervalSummary?> ObservedPeriodAsync(Session session, CancellationToken cancellationToken)
    {
        var cobId = CanOpenCobId.Heartbeat(session.Target);
        using var probe = new BusTap(session.Service, (id, extended) => id == cobId && !extended);

        // The operator's window, not a cap of its own. An earlier version waited at most 2000 ms
        // and needed the three frames Summarise asks for, so a producer at 1 s or slower was
        // reported as having no heartbeat — right after HBT-02 had passed it on the full
        // --heartbeat-ms window. Found in review of #388. It stops as soon as three frames are in,
        // so a fast producer costs no more than it did.
        await probe.WaitForAsync(frames => Heartbeats(frames).Count >= 3, session.Options.HeartbeatWindowMs, cancellationToken)
            .ConfigureAwait(false);
        // Heartbeats only. Counting every frame on the COB-ID let three boot-up messages produce
        // a "period", so HBT-03 and HBT-04 ran on it and could blame the stack for not reporting
        // heartbeats that were never sent — right after HBT-02 had said none were. Found in
        // review of #388.
        // No period while guarding traffic shares the COB-ID. A toggle-zero response is
        // byte-for-byte a heartbeat, so a period measured over the remainder would be measured
        // over an unknown mixture — and HBT-03/HBT-04 would then run on it and could blame the
        // stack for beats nobody sent. The evidence was local to HBT-02 until now. Found in
        // review of #388.
        if (HasGuardingTraffic(probe.Frames)) return null;
        return BusTap.Summarise(Heartbeats(probe.Frames));
    }

    /// <summary>
    /// The frames on the heartbeat COB-ID that are heartbeats: one byte of NMT state, and not the
    /// 00h boot-up, which CiA 301 never reports in one. A period measured over anything else is a
    /// measurement of a reboot, not of a producer.
    /// </summary>
    private static List<TappedFrame> Heartbeats(IReadOnlyList<TappedFrame> frames)
        => frames.Where(f => !f.IsRemote && f.Data.Length == 1 && IsDefinedRunState(f.Data[0])).ToList();

    /// <summary>
    /// The newest NMT state a heartbeat reported, or null when none did. Only 04h, 05h and 7Fh
    /// count: 00h is a boot-up, a node-guarding response carries the toggle bit in the high bit
    /// (85h for Operational), and anything else is undefined. An earlier version accepted any
    /// one-byte payload, so NMT-01 ran on 85h and its cleanup mapped that unrecognised value to
    /// EnterPreOperational — leaving a device that began Operational out of service. Found in
    /// review of #388.
    /// </summary>
    private static byte? LatestState(BusTap tap)
    {
        var frames = Heartbeats(tap.Frames);
        for (var i = frames.Count - 1; i >= 0; i--)
            if (IsDefinedRunState(frames[i].Data[0])) return frames[i].Data[0];
        return null;
    }

    /// <summary>
    /// 1017h, the producer heartbeat time in milliseconds; null when it cannot be read. HBT-02
    /// uses it to tell a device that is switched off from one that is failing to produce.
    /// </summary>
    private static async Task<ushort?> ProducerHeartbeatTimeAsync(Session session, CancellationToken cancellationToken)
    {
        if (!session.MayTransfer(0x1017, 0x00)) return null;
        try
        {
            var raw = await session.Client.SdoUploadAsync(session.Target, 0x1017, 0x00, cancellationToken).ConfigureAwait(false);
            return raw.Length == 2 ? BitConverter.ToUInt16(raw, 0) : null;
        }
        catch (SdoAbortException)
        {
            return null;
        }
    }

    /// <summary>The three states a heartbeat may report while the node is running.</summary>
    private static bool IsDefinedRunState(byte state)
        => state is (byte)NmtState.Stopped or (byte)NmtState.Operational or (byte)NmtState.PreOperational;

    /// <summary>
    /// A node-guarding response, which shares the heartbeat's COB-ID and carries the toggle bit in
    /// the high bit on alternating replies — 84h/85h/FFh for the three run states. It is another
    /// master's conversation with the target, legitimate traffic, and not a malformed heartbeat;
    /// calling it one failed a conforming device. Found in review of #388.
    /// </summary>
    /// <summary>
    /// Whether another master's node guarding is visible on the heartbeat COB-ID: an RTR poll or a
    /// toggle-one response proves it. Shared so that every check drawing a conclusion from this
    /// COB-ID uses the same evidence.
    /// </summary>
    private static bool HasGuardingTraffic(IReadOnlyList<TappedFrame> frames)
        => frames.Any(f => f.IsRemote || (f.Data.Length == 1 && IsGuardingResponse(f.Data[0])));

    private static bool IsGuardingResponse(byte payload)
        => (payload & 0x80) != 0 && IsDefinedRunState((byte)(payload & 0x7F));

    internal static string DescribeState(byte state) => state switch
    {
        0x00 => "00h boot-up",
        0x04 => "04h stopped",
        0x05 => "05h operational",
        0x7F => "7Fh pre-operational",
        _ => $"0x{state:X2} (not a CiA 301 state)",
    };
}
