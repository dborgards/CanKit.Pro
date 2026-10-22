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
            using var tap = new BusTap(session.Service, id => id == cobId);
            await tap.ListenAsync(session.Options.HeartbeatWindowMs, cancellationToken).ConfigureAwait(false);
            var frames = tap.Frames;

            var measurements = new Dictionary<string, string>
            {
                ["COB-ID"] = $"0x{cobId:X3}",
                ["window"] = $"{session.Options.HeartbeatWindowMs} ms",
                ["frames"] = frames.Count.ToString(CultureInfo.InvariantCulture),
            };

            if (frames.Count == 0)
            {
                return Outcome.Inconclusive(
                    $"No heartbeat on 0x{cobId:X3} in {session.Options.HeartbeatWindowMs} ms. If HBT-01 read 1017h = 0 "
                    + "this is the configuration, not a fault; otherwise the device is not producing what it is set to.",
                    Attribution.Device, measurements);
            }

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

            if (BusTap.Summarise(frames) is { } summary)
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

            if (malformed == frames.Count)
                return Outcome.Fail($"All {frames.Count} frame(s) on 0x{cobId:X3} have a payload other than one byte.", measurements);

            return Outcome.Pass($"{frames.Count} heartbeat(s) observed.", measurements);
        }).ConfigureAwait(false);

        await book.RunAsync("HBT-03", "heartbeat", "Heartbeat consumer raises a timeout when the device stops", Tier.Read, "FR-CO-008", async () =>
        {
            // This one measures the stack, not the device: it is the consumer side of FR-CO-008,
            // and the device is only the source of the frames it consumes. A failure here is
            // attributed to the tool for that reason.
            var cobId = CanOpenCobId.Heartbeat(session.Target);
            using var probe = new BusTap(session.Service, id => id == cobId);
            await probe.ListenAsync(Math.Min(session.Options.HeartbeatWindowMs, 2000), cancellationToken).ConfigureAwait(false);
            var summary = BusTap.Summarise(probe.Frames);
            if (summary is not { } period)
                return Outcome.NeedsSetup("The device produces no heartbeat to consume (see HBT-02).");

            var received = 0;
            var timedOut = 0;
            void OnBeat(object? sender, HeartbeatReceivedEventArgs e)
            {
                if (e.ProducerNodeId == session.Target) Interlocked.Increment(ref received);
            }

            void OnTimeout(object? sender, HeartbeatTimeoutEventArgs e)
            {
                if (e.ProducerNodeId == session.Target) Interlocked.Increment(ref timedOut);
            }

            session.Client.HeartbeatReceived += OnBeat;
            session.Client.HeartbeatTimeout += OnTimeout;
            try
            {
                // Generous: 3 periods plus a margin, so a late frame is not a timeout. What is
                // being checked is that beats arrive and are not reported as missing, not how
                // tightly the stack tracks the clock.
                var timeout = TimeSpan.FromMilliseconds(Math.Max(100, period.MedianMs * 3 + 200));
                session.Client.AddHeartbeatConsumer(session.Target, timeout);
                await Task.Delay((int)Math.Min(10_000, Math.Max(500, period.MedianMs * 4)), cancellationToken).ConfigureAwait(false);

                var measurements = new Dictionary<string, string>
                {
                    ["observed period (median)"] = IdentityAndSdoChecks.Ms(period.MedianMs),
                    ["consumer timeout used"] = IdentityAndSdoChecks.Ms(timeout.TotalMilliseconds),
                    ["HeartbeatReceived events"] = received.ToString(CultureInfo.InvariantCulture),
                    ["HeartbeatTimeout events"] = timedOut.ToString(CultureInfo.InvariantCulture),
                };

                if (received == 0)
                {
                    return Outcome.ToolFault(
                        "The device's heartbeat is on the bus (HBT-02 saw it) but the consumer raised no "
                        + "HeartbeatReceived event. That is a defect in the stack, not in the device.", measurements);
                }

                return timedOut == 0
                    ? Outcome.Pass($"{received} beat(s) consumed, no spurious timeout.", measurements)
                    : Outcome.Inconclusive(
                        $"{received} beat(s) consumed and {timedOut} timeout(s) raised with a {timeout.TotalMilliseconds:F0} ms "
                        + "budget against a period of " + IdentityAndSdoChecks.Ms(period.MedianMs)
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

        await book.RunAsync("GRD-01", "guarding", "100Ch/100Dh node guarding parameters", Tier.Read, "FR-CO-009", async () =>
        {
            if (!session.MayTransfer(0x100C, 0x00) || !session.MayTransfer(0x100D, 0x00))
                return Outcome.NeedsSetup(session.GateReason(0x100C, 0x00));

            try
            {
                var guardTime = await session.Client.SdoUploadAsync(session.Target, 0x100C, 0x00, cancellationToken).ConfigureAwait(false);
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
            catch (SdoAbortException abort) when (abort.AbortCode is (uint)SdoAbortCode.ObjectDoesNotExist)
            {
                return Outcome.NotApplicable("The device has no 100Ch/100Dh; node guarding is optional and deprecated.");
            }
        }).ConfigureAwait(false);

        await book.RunAsync("NMT-01", "nmt", "NMT state machine follows the commanded state", Tier.Write, "FR-CO-007", async () =>
        {
            var cobId = CanOpenCobId.Heartbeat(session.Target);
            using var tap = new BusTap(session.Service, id => id == cobId);
            await tap.ListenAsync(500, cancellationToken).ConfigureAwait(false);
            if (tap.Frames.Count == 0)
            {
                return Outcome.NeedsSetup(
                    "Without a heartbeat there is no way to observe the device's NMT state from outside, and this "
                    + "check will not command a state it cannot verify. Enable the heartbeat (1017h) and re-run.");
            }

            // Where it was found, so it can be put back. A device left Stopped by a conformance
            // run is a device the next person finds broken.
            var initial = LatestState(tap);
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
            try
            {
                foreach (var (command, expected) in steps)
                {
                    await session.Client.SendNmtCommandAsync(command, session.Target, cancellationToken).ConfigureAwait(false);
                    var reached = await tap.WaitForAsync(
                        frames => frames.Count > 0 && frames[^1].Data.Length == 1 && frames[^1].Data[0] == (byte)expected,
                        Math.Max(1000, session.Options.HeartbeatWindowMs), cancellationToken).ConfigureAwait(false);
                    var actual = LatestState(tap);
                    measurements[$"after {command}"] = actual is { } s ? DescribeState(s) : "(no heartbeat)";
                    if (!reached)
                        problems.Add($"{command} did not reach {expected} (heartbeat says {(actual is { } a ? DescribeState(a) : "nothing")})");
                }
            }
            finally
            {
                // Back to where it was found, whatever happened above.
                if (initial is { } restore && restore != (byte)NmtState.PreOperational)
                {
                    var command = restore == (byte)NmtState.Operational ? NmtCommand.Start
                        : restore == (byte)NmtState.Stopped ? NmtCommand.Stop
                        : NmtCommand.EnterPreOperational;
                    try
                    {
                        await session.Client.SendNmtCommandAsync(command, session.Target, CancellationToken.None).ConfigureAwait(false);
                        measurements["restored to"] = DescribeState(restore);
                    }
                    catch (Exception ex)
                    {
                        book.Note("NMT-01 restore", ex);
                        measurements["restored to"] = $"FAILED ({ex.GetType().Name}) — the device may be left in a state it was not found in";
                    }
                }
            }

            return problems.Count == 0
                ? Outcome.Pass("The device reached every commanded state, and was returned to the state it was found in.", measurements)
                : Outcome.Fail(string.Join("; ", problems) + ".", measurements);
        }).ConfigureAwait(false);
    }

    private static byte? LatestState(BusTap tap)
    {
        var frames = tap.Frames;
        for (var i = frames.Count - 1; i >= 0; i--)
            if (frames[i].Data.Length == 1) return frames[i].Data[0];
        return null;
    }

    internal static string DescribeState(byte state) => state switch
    {
        0x00 => "00h boot-up",
        0x04 => "04h stopped",
        0x05 => "05h operational",
        0x7F => "7Fh pre-operational",
        _ => $"0x{state:X2} (not a CiA 301 state)",
    };
}
