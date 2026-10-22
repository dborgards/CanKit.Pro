using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
                catch (SdoAbortException abort)
                {
                    measurements[name] = $"aborted {Describe(abort)} (1008h..100Ah are optional in CiA 301)";
                }
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
            // 5FFFh is in the reserved manufacturer area and no profile defines it.
            const ushort Absent = 0x5FFF;
            if (!session.MayTransfer(Absent, 0x00))
            {
                return Outcome.NeedsSetup(
                    $"The peer-SDO gate blocks 0x{Absent:X4}: a bound description lists only what the device has, so "
                    + "this check cannot be run with --eds. Run the sdo group without --eds to measure it.");
            }

            try
            {
                var raw = await session.Client.SdoUploadAsync(session.Target, Absent, 0x00, cancellationToken)
                    .ConfigureAwait(false);
                return Outcome.Fail(
                    $"The device returned {Hex(raw)} for 0x{Absent:X4}:00 instead of aborting. A client cannot tell "
                    + "an implemented object from an unimplemented one on this device.");
            }
            catch (SdoAbortException abort)
            {
                var measurements = new Dictionary<string, string>
                {
                    ["abort code"] = $"0x{abort.AbortCode:X8}",
                    ["meaning"] = Enum.IsDefined(typeof(SdoAbortCode), abort.AbortCode)
                        ? ((SdoAbortCode)abort.AbortCode).ToString()
                        : "not a CiA 301 abort code",
                    ["origin"] = abort.Origin.ToString(),
                };

                if (abort.Origin == SdoAbortOrigin.Local)
                    return Outcome.Inconclusive("The abort came from this client, not from the device: nothing was asked of it.",
                        Attribution.Tool, measurements);

                return abort.AbortCode == (uint)SdoAbortCode.ObjectDoesNotExist
                    ? Outcome.Pass("Aborted 0x06020000 ObjectDoesNotExist, as CiA 301 requires.", measurements)
                    : Outcome.Fail(
                        $"Aborted 0x{abort.AbortCode:X8} where CiA 301 asks for 0x06020000 (object does not exist).",
                        measurements);
            }
            catch (OperationCanceledException)
            {
                return Outcome.Fail(
                    $"The device neither answered nor aborted 0x{Absent:X4}:00 within {session.Options.SdoTimeoutMs} ms. "
                    + "A client has no way to distinguish this from a dead node.");
            }
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
