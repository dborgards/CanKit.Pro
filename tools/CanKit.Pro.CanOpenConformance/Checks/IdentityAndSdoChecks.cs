using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Sdo;

namespace CanKit.Pro.CanOpenConformance.Checks;

/// <summary>
/// Who the device says it is, and whether its SDO server behaves. Identity first because every
/// later finding is only worth keeping if it names the device it was measured on, and because
/// 1018h is readable with no device description bound — the peer-SDO gate allows it.
/// </summary>
internal static class IdentityAndSdoChecks
{
    public static async Task RunAsync(Session session, Checkbook book, CancellationToken cancellationToken)
    {
        await book.RunAsync("IDN-01", "identity", "1000h:00 device type", Tier.Read, "FR-CO-002", async () =>
        {
            var raw = await session.Client.SdoUploadAsync(session.Target, 0x1000, 0x00, cancellationToken)
                .ConfigureAwait(false);
            if (raw.Length != 4)
                return Outcome.Fail($"1000h:00 is UNSIGNED32, the device returned {raw.Length} byte(s): {Hex(raw)}.");

            var deviceType = BitConverter.ToUInt32(raw, 0);
            var profile = deviceType & 0xFFFF;
            return Outcome.Pass($"Device type 0x{deviceType:X8}.", new Dictionary<string, string>
            {
                ["raw"] = Hex(raw),
                ["device profile number"] = profile == 0
                    ? "0 (no standardised device profile)"
                    : $"{profile} (CiA {profile})",
                ["additional information"] = $"0x{(deviceType >> 16):X4}",
            });
        }).ConfigureAwait(false);

        await book.RunAsync("IDN-02", "identity", "1001h:00 error register", Tier.Read, "CiA 301 1001h", async () =>
        {
            var raw = await session.Client.SdoUploadAsync(session.Target, 0x1001, 0x00, cancellationToken)
                .ConfigureAwait(false);
            if (raw.Length != 1)
                return Outcome.Fail($"1001h:00 is UNSIGNED8, the device returned {raw.Length} byte(s): {Hex(raw)}.");

            var register = raw[0];
            var bits = DescribeErrorRegister(register);
            var measurements = new Dictionary<string, string> { ["error register"] = $"0x{register:X2}", ["bits set"] = bits };

            // A set error register is the device reporting a fault about itself, not a conformance
            // failure of the stack — but a run whose later findings were taken from a device in a
            // fault state has to say so, because that state can suppress PDOs entirely.
            return register == 0
                ? Outcome.Pass("No error flagged.", measurements)
                : Outcome.Inconclusive(
                    $"The device flags an error (0x{register:X2}: {bits}). Later findings are measurements of a "
                    + "device in a fault state; clear the fault and re-run before reading them as conformance.",
                    Attribution.Device, measurements);
        }).ConfigureAwait(false);

        await book.RunAsync("IDN-03", "identity", "1018h identity object", Tier.Read, "FR-CO-002", async () =>
        {
            var highest = await session.Client.SdoUploadAsync(session.Target, 0x1018, 0x00, cancellationToken)
                .ConfigureAwait(false);
            if (highest.Length != 1)
                return Outcome.Fail($"1018h:00 is UNSIGNED8, the device returned {Hex(highest)}.");

            var measurements = new Dictionary<string, string> { ["highest sub-index"] = highest[0].ToString(CultureInfo.InvariantCulture) };
            if (highest[0] < 1)
                return Outcome.Fail("1018h:00 says 0: CiA 301 makes 1018h:01 (vendor-id) mandatory.", measurements);

            var names = new[] { "", "vendor-id", "product code", "revision number", "serial number" };
            var problems = new List<string>();

            // The identity object is 1018h:01..04 and nothing else, which is also where this
            // repository's own peer-access policy draws the line. Clamping a larger count to 4
            // normalised the contradiction away and the check then reported "the identity object
            // reads back whole" about a device that had declared sub-indices it has no definition
            // for. Found in review of #388 — the same read-is-not-valid shape as the reserved
            // mapping count and transmission type.
            if (highest[0] > 4)
            {
                problems.Add($"1018h:00 says {highest[0]}; the identity object is defined as 1018h:01..04 and has no "
                    + "sub-index above 4");
            }

            for (byte sub = 1; sub <= Math.Min(highest[0], (byte)4); sub++)
            {
                try
                {
                    var raw = await session.Client.SdoUploadAsync(session.Target, 0x1018, sub, cancellationToken)
                        .ConfigureAwait(false);
                    if (raw.Length != 4)
                    {
                        problems.Add($"1018h:{sub:X2} ({names[sub]}) is UNSIGNED32, got {raw.Length} byte(s)");
                        measurements[names[sub]] = Hex(raw);
                        continue;
                    }

                    measurements[names[sub]] = $"0x{BitConverter.ToUInt32(raw, 0):X8}";
                }
                catch (SdoAbortException abort)
                {
                    problems.Add($"1018h:{sub:X2} ({names[sub]}) aborted {Describe(abort)}, although 1018h:00 says {highest[0]}");
                }
            }

            return problems.Count == 0
                ? Outcome.Pass("The identity object reads back whole.", measurements)
                : Outcome.Fail(string.Join("; ", problems) + ".", measurements);
        }).ConfigureAwait(false);

        await book.RunAsync("IDN-04", "identity", "1008h/1009h/100Ah name and version strings", Tier.Read, "FR-CO-003", async () =>
        {
            // VISIBLE_STRING longer than 4 bytes forces the segmented upload protocol, so this is
            // also the first real exercise of SDO segmentation — on the device's own data, at
            // whatever length it happens to be.
            var wanted = new (ushort Index, string Name)[]
            {
                (0x1008, "manufacturer device name"),
                (0x1009, "manufacturer hardware version"),
                (0x100A, "manufacturer software version"),
            };
            var measurements = new Dictionary<string, string>();
            var blocked = new List<string>();
            var unresolved = new List<string>();
            var read = 0;
            foreach (var (index, name) in wanted)
            {
                if (!session.MayTransfer(index, 0x00))
                {
                    blocked.Add($"0x{index:X4}");
                    measurements[name] = session.TargetDescription is null
                        ? "not read: no device description bound, and the peer-SDO gate allows only 1000h/1001h/1018h"
                        : $"not read: 0x{index:X4}:00 is not declared by the bound description";
                    continue;
                }

                try
                {
                    var raw = await session.Client.SdoUploadAsync(session.Target, index, 0x00, cancellationToken)
                        .ConfigureAwait(false);
                    var protocol = raw.Length > 4 ? ", segmented" : ", expedited";
                    measurements[name] = $"\"{Printable(raw)}\" ({raw.Length} bytes{protocol})";
                    read++;
                }
                catch (SdoAbortException abort) when (abort.AbortCode is (uint)SdoAbortCode.ObjectDoesNotExist
                                                          or (uint)SdoAbortCode.SubIndexDoesNotExist)
                {
                    measurements[name] = $"absent (1008h..100Ah are optional in CiA 301)";
                }
                catch (SdoAbortException abort)
                {
                    // Not absence. An object the run was allowed to ask for that then would not
                    // answer is unresolved, and returning Pass because a *different* string read
                    // put it under "What works". Found in review of #388.
                    measurements[name] = $"aborted {Describe(abort)}";
                    unresolved.Add($"{name} ({Describe(abort)})");
                }
            }

            // Before the zero-read branch, not after it. Behind it, a run where every permitted
            // string timed out had read == 0 and was reported as "the device implements none of
            // them" — the optional-absence verdict this list exists to stop standing in for a
            // non-absence abort, reinstated by the order of two ifs. Found in review of #388.
            if (unresolved.Count > 0)
            {
                return Outcome.Inconclusive(
                    $"{read} of 3 strings read, and {unresolved.Count} the run was allowed to ask for would not answer "
                    + $"({string.Join(", ", unresolved)}). Those are not absences, so the check is not complete.",
                    Attribution.Device, measurements);
            }

            if (read == 0)
            {
                return blocked.Count == wanted.Length
                    ? Outcome.NeedsSetup($"{string.Join(", ", blocked)} blocked: {session.GateReason(0x1008, 0x00)}")
                    : Outcome.NotApplicable("The device implements none of 1008h, 1009h, 100Ah; all three are optional.");
            }

            var segmented = measurements.Values.Count(v => v.Contains(", segmented", StringComparison.Ordinal));
            return Outcome.Pass(
                $"{read} of 3 strings read{(segmented > 0 ? $"; {segmented} needed the segmented SDO protocol" : " (all four bytes or fewer, so expedited)")}. "
                + "The rest are listed with the reason they were not.", measurements);
        }).ConfigureAwait(false);

        await book.RunAsync("SDO-01", "sdo", "Expedited upload round-trip latency", Tier.Read, "FR-CO-002", async () =>
        {
            // 1000h:00 is four bytes, so this is the expedited protocol: one request frame, one
            // response frame. What it measures is the device's SDO server turnaround as the host
            // sees it, which is the number every timeout in a real application is chosen against.
            const int Transfers = 20;
            var samples = new List<double>(Transfers);
            for (var i = 0; i < Transfers && !cancellationToken.IsCancellationRequested; i++)
            {
                var started = System.Diagnostics.Stopwatch.StartNew();
                await session.Client.SdoUploadAsync(session.Target, 0x1000, 0x00, cancellationToken).ConfigureAwait(false);
                samples.Add(started.Elapsed.TotalMilliseconds);
            }

            if (samples.Count == 0) return Outcome.Inconclusive("No transfer completed.");
            samples.Sort();
            var measurements = new Dictionary<string, string>
            {
                ["transfers"] = samples.Count.ToString(CultureInfo.InvariantCulture),
                ["median"] = Ms(samples[samples.Count / 2]),
                ["best"] = Ms(samples[0]),
                ["worst"] = Ms(samples[^1]),
            };
            return Outcome.Pass($"{samples.Count} expedited uploads, median {Ms(samples[samples.Count / 2])}.", measurements);
        }).ConfigureAwait(false);

        await book.RunAsync("SDO-02", "sdo", "Abort on a non-existent object", Tier.Read, "FR-CO-002", async () =>
        {
            // The one check that wants a failure. A device that answers something for an index it
            // does not have is worse than one that aborts, and an SDO server that stays silent
            // makes every client wait out its timeout for a question with a known answer.
            // 5FFFh is in the manufacturer-specific area, so no profile defines it and most devices
            // do not implement it — but a device may, and one that answers is reported as
            // not-applicable rather than failed.
            //
            // Driven on the wire rather than through ICanOpenNode, and that is the check rather
            // than a shortcut. The peer-SDO gate refuses any pair a bound description does not
            // declare and, with no description, everything but 1000h/1001h/1018h — and a
            // description never declares an object the device lacks. So the gate blocks this
            // question through the client API under *every* invocation: an earlier version
            // reported itself setup-skipped always, including under the very re-run its own skip
            // text recommended, which made it a check that could not run. Found in review of #388.
            // The gate is a client-API policy, not a protocol rule; the device's SDO server
            // answers the frame regardless of which client sent it.
            const ushort Absent = 0x5FFF;
            var request = new byte[8];
            request[0] = 0x40; // CiA 301 §7.2.4.3.5: initiate SDO upload
            request[1] = unchecked((byte)Absent);
            request[2] = (byte)(Absent >> 8);
            // Sub-index 0 and the four reserved bytes stay zero.

            var responseId = CanOpenCobId.SdoTx(session.Target);
            using var tap = new BusTap(session.Service, (id, extended) => id == responseId && !extended);

            var accepted = session.Bus.Transmit(CanFrame.Classic((int)CanOpenCobId.SdoRx(session.Target), request));
            if (accepted <= 0)
            {
                return Outcome.TransportFault(
                    $"The driver rejected the SDO upload request frame (Transmit returned {accepted}), so the device "
                    + "was never asked.");
            }

            // Data frames only, in the wait and in everything below it. An RTR another master
            // puts on 580h+node-id is a request for that id, not the device answering: it ended
            // the wait early and then, carrying no data, was reported as "the device answered
            // with 0 byte(s)" — a FAIL charged to a device that may not even have been asked yet.
            // The same correction EMC-01 needed, found by sweeping for it. Found in review of #388.
            await tap.WaitForAsync(frames => frames.Any(f => !f.IsRemote), session.Options.SdoTimeoutMs, cancellationToken)
                .ConfigureAwait(false);

            var responses = tap.Frames.Where(f => !f.IsRemote).ToList();
            var answer = responses.FirstOrDefault(f => f.Data.Length >= 8);
            if (responses.Count == 0)
            {
                return Outcome.Fail(
                    $"The device neither answered nor aborted an upload of 0x{Absent:X4}:00 within "
                    + $"{session.Options.SdoTimeoutMs} ms. A client has no way to distinguish that from a dead node.");
            }

            if (answer.Data is null || answer.Data.Length < 8)
            {
                return Outcome.Fail(
                    $"The device answered on 0x{responseId:X3} with {answer.Data?.Length ?? 0} byte(s); an SDO server "
                    + "response is always 8.");
            }

            var measurements = new Dictionary<string, string>
            {
                ["request"] = $"0x{CanOpenCobId.SdoRx(session.Target):X3} {Hex(request)}",
                ["response"] = $"0x{responseId:X3} {Hex(answer.Data)}",
            };

            // 80h is "abort transfer"; the index and sub-index are echoed in bytes 1..3 and the
            // abort code is the last four, little-endian.
            if (answer.Data[0] != 0x80)
            {
                // Not a failure: 5FFFh is in the manufacturer-specific range, and a device is
                // entitled to implement it. The probe needs an object proven absent, and nothing
                // here proves that — so this says the check could not be performed rather than
                // calling a conforming device non-conforming. An earlier version failed it. Found
                // in review of #388.
                return Outcome.NotApplicable(
                    $"The device answered an upload of 0x{Absent:X4}:00 with command specifier 0x{answer.Data[0]:X2} "
                    + "instead of aborting, which means it implements that object. 5FFFh is manufacturer-specific and a "
                    + "device may legitimately use it, so this probe has no object proven absent on this device and "
                    + "draws no conclusion. Give the run an --eds and read EDS-03 instead: it compares the objects the "
                    + "file declares against the ones the device really has.", measurements);
            }

            var echoed = (ushort)(answer.Data[1] | (answer.Data[2] << 8));
            if (echoed != Absent || answer.Data[3] != 0x00)
            {
                measurements["echoed index"] = $"0x{echoed:X4}:{answer.Data[3]:X2}";
                return Outcome.Fail(
                    $"The abort echoes 0x{echoed:X4}:{answer.Data[3]:X2}, not the 0x{Absent:X4}:00 that was asked for.",
                    measurements);
            }

            var code = BitConverter.ToUInt32(answer.Data, 4);
            measurements["abort code"] = $"0x{code:X8}";
            measurements["meaning"] = Enum.IsDefined(typeof(SdoAbortCode), code)
                ? ((SdoAbortCode)code).ToString()
                : "not a CiA 301 abort code";

            return code == (uint)SdoAbortCode.ObjectDoesNotExist
                ? Outcome.Pass("Aborted 0x06020000 ObjectDoesNotExist, as CiA 301 requires.", measurements)
                : Outcome.Fail(
                    $"Aborted 0x{code:X8} where CiA 301 asks for 0x06020000 (object does not exist).", measurements);
        }).ConfigureAwait(false);

        await book.RunAsync("SDO-03", "sdo", "Write is refused on a read-only object", Tier.Write, "FR-CO-002", async () =>
        {
            // 1000h:00 is read-only in every profile. The write is deliberately the value the
            // device already reports, so that a device which wrongly accepts it is left as it was.
            var current = await session.Client.SdoUploadAsync(session.Target, 0x1000, 0x00, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                await session.Client.SdoDownloadAsync(session.Target, 0x1000, 0x00, current, cancellationToken)
                    .ConfigureAwait(false);
                return Outcome.Fail(
                    "The device accepted a write to 1000h:00, which CiA 301 defines as read-only. (The value written "
                    + "was the one it already held, so nothing changed.)");
            }
            catch (SdoAbortException abort) when (abort.Origin == SdoAbortOrigin.Peer)
            {
                var measurements = new Dictionary<string, string> { ["abort code"] = $"0x{abort.AbortCode:X8}" };
                return abort.AbortCode == (uint)SdoAbortCode.AttemptWriteReadOnly
                    ? Outcome.Pass("Aborted 0x06010002 AttemptWriteReadOnly.", measurements)
                    : Outcome.Inconclusive(
                        $"Refused with 0x{abort.AbortCode:X8} rather than 0x06010002. The write was refused, which is "
                        + "what matters; the code is not the one CiA 301 names.", Attribution.Device, measurements);
            }
        }).ConfigureAwait(false);
    }

    /// <summary>The bits of 1001h, by name, so a set register reads as something actionable.</summary>
    private static string DescribeErrorRegister(byte register)
    {
        if (register == 0) return "none";
        var names = new[] { "generic", "current", "voltage", "temperature", "communication", "device profile", "reserved", "manufacturer" };
        return string.Join(", ", Enumerable.Range(0, 8).Where(b => (register & (1 << b)) != 0).Select(b => names[b]));
    }

    internal static string Describe(SdoAbortException abort)
        => Enum.IsDefined(typeof(SdoAbortCode), abort.AbortCode)
            ? $"0x{abort.AbortCode:X8} ({(SdoAbortCode)abort.AbortCode})"
            : $"0x{abort.AbortCode:X8}";

    internal static string Hex(byte[] data) => data.Length == 0 ? "(empty)" : string.Concat(data.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));

    internal static string Ms(double value) => value.ToString("F2", CultureInfo.InvariantCulture) + " ms";

    /// <summary>A VISIBLE_STRING as the device sent it, with anything unprintable shown rather
    /// than written to the operator's terminal.</summary>
    internal static string Printable(byte[] data)
    {
        var text = new StringBuilder(data.Length);
        foreach (var b in data)
        {
            if (b == 0) break;
            text.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
        }

        return text.ToString();
    }
}
