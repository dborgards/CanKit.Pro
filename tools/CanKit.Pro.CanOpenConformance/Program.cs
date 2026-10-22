using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Pro.CanOpenConformance.Checks;

namespace CanKit.Pro.CanOpenConformance;

/// <summary>
/// A CANopen conformance runner: it asks a device the questions CiA 301 and CiA 305 answer, and —
/// where an EDS or DCF is given — the questions that file answers, then writes up what worked,
/// what did not, and whose fault that is.
/// </summary>
/// <remarks>
/// It lives in <c>tools/</c> and not in <c>tests/</c> or <c>samples/</c> deliberately. The test
/// suite runs in CI on three operating systems with no CAN hardware, so a hardware suite there
/// would be a permanently skipped shell; SRS §7 puts hardware-in-the-loop tests outside the
/// standard CI gate (assumption A-5). The samples promise, in writing, that none of them needs
/// hardware. A diagnostic tool that CI compiles and never runs is the shape this work has —
/// <c>tools/CanKit.Pro.EchoProbe</c> is the precedent.
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine(RunOptions.Usage);
            return args.Length == 0 ? 2 : 0;
        }

        var options = RunOptions.Parse(args, out var errors);
        if (options is null)
        {
            foreach (var error in errors) Console.Error.WriteLine(error);
            Console.Error.WriteLine();
            Console.Error.WriteLine(RunOptions.Usage);
            return 2;
        }

        // A simulated run with no description would skip every check the peer-SDO gate blocks,
        // which is most of them — so it binds the description the simulated device is built from.
        // An explicit --eds still wins: pointing a simulated run at another file is a legitimate
        // way to see what the eds group reports for a description that does not fit.
        if (options.Simulate && options.EdsPath is null && File.Exists(SimulatedDevice.DescriptionPath))
            options = options with { EdsPath = SimulatedDevice.DescriptionPath };

        // Before anything is measured: a run that has measured everything must not be lost to a
        // directory that cannot be written. The two files are created now with a placeholder, so
        // a name that is too long or a directory that is not writable is found here and not after
        // the device has been put through forty transfers.
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var baseName = Path.Combine(options.OutputDirectory, $"canopen-conformance-{RunOptions.Sanitize(options.Label)}-{stamp}");
        try
        {
            Directory.CreateDirectory(options.OutputDirectory);
            File.WriteAllText(baseName + ".md", "The run did not finish.");
            File.WriteAllText(baseName + ".json", "{}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"--out {options.OutputDirectory} cannot be used for {Path.GetFileName(baseName)}.md/.json: {ex.Message}");
            return 2;
        }

        Announce(options);

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            // Ctrl+C has to leave the device as it was found: cancelling the token lets each
            // check's finally block run (NMT state restored, LSS switched back to Waiting) and
            // then writes the partial report, which is more useful than a killed process.
            e.Cancel = true;
            Console.WriteLine();
            Console.WriteLine("Interrupted — finishing the current check, restoring the device and writing the report.");
            cancellation.Cancel();
        };

        var book = new Checkbook(options);
        var startedAt = DateTimeOffset.Now;
        var clock = Stopwatch.StartNew();
        string? openError = null;
        var background = new List<string>();

        SimulatedDevice? simulated = null;
        try
        {
            if (options.Simulate)
            {
                // The simulated device needs a node-id, and the run needs to be pointed at it.
                var deviceNode = options.TargetNode ?? (byte)0x11;
                simulated = SimulatedDevice.Start(options.Endpoint, deviceNode, options.Bitrate);
                Console.WriteLine($"  simulated CANopen device started as node {deviceNode} on the same virtual session.");
                if (options.TargetNode is null)
                {
                    Console.Error.WriteLine("--simulate started a device as node 17; pass --node 17 to test against it.");
                    return 2;
                }
            }

            using var session = Session.Open(options, out openError);
            await RunGroupsAsync(session, book, options, cancellation.Token).ConfigureAwait(false);
            lock (session.BackgroundExceptions) background.AddRange(session.BackgroundExceptions);
        }
        catch (OpenFailedException ex)
        {
            openError = ex.Message;
            Console.Error.WriteLine();
            Console.Error.WriteLine($"The bus could not be opened: {ex.Message}");
            Console.Error.WriteLine("That is the adapter, its vendor driver or the endpoint string — not the device.");
        }
        catch (OperationCanceledException)
        {
            book.Note("run", "OperationCanceledException", "The run was interrupted.", Attribution.Setup);
        }
        finally
        {
            simulated?.Dispose();
        }

        clock.Stop();
        var report = new Report(
            Label: options.Label,
            StartedAt: startedAt,
            DurationSeconds: clock.Elapsed.TotalSeconds,
            ToolVersion: ReportRenderer.ToolVersion,
            Host: $"{Environment.OSVersion} / {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} / {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}",
            Options: options,
            OpenError: openError,
            Findings: book.Findings,
            Incidents: book.Incidents,
            BackgroundExceptions: background);

        File.WriteAllText(baseName + ".json", ReportRenderer.ToJson(report));
        File.WriteAllText(baseName + ".md", ReportRenderer.ToMarkdown(report));

        Console.WriteLine();
        Summarise(report);
        Console.WriteLine();
        Console.WriteLine($"Report written: {baseName}.md and .json");

        // An unopened bus is not a device verdict, and must not be reported as a passing run.
        if (openError is not null) return 1;
        return book.AnyFailure ? 1 : 0;
    }

    /// <summary>
    /// The groups, in the order they have to run. Identity before everything that quotes the
    /// device's own values back, PDO parameters before PDO observation, and the two groups that
    /// change the device last — so that an interrupted run has done the least to it.
    /// </summary>
    private static async Task RunGroupsAsync(Session session, Checkbook book, RunOptions options, CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine("=== Adapter and bus ===");
        await BusAndDiscoveryChecks.RunAsync(session, book, cancellationToken).ConfigureAwait(false);

        if (options.TargetNode is null)
        {
            Console.WriteLine();
            Console.WriteLine("No --node given: the device-specific groups are not run. Re-run with --node <1..127>.");
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("=== Identity and SDO ===");
            await IdentityAndSdoChecks.RunAsync(session, book, cancellationToken).ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine("=== Device description ===");
            await EdsChecks.RunAsync(session, book, cancellationToken).ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine("=== Error control and NMT ===");
            await NmtAndHeartbeatChecks.RunAsync(session, book, cancellationToken).ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine("=== Process data, SYNC and EMCY ===");
            await PdoSyncEmcyChecks.RunAsync(session, book, cancellationToken).ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine("=== LSS and parameter storage ===");
            await LssAndStoreChecks.RunAsync(session, book, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void Announce(RunOptions options)
    {
        Console.WriteLine($"CANopen conformance  endpoint={options.Endpoint}  "
            + $"node={(options.TargetNode is { } n ? n.ToString(CultureInfo.InvariantCulture) : "(none)")}  "
            + $"tier={options.Tier.ToString().ToLowerInvariant()}");
        Console.WriteLine(options.UseFd
            ? $"  CAN FD: arbitration {options.Bitrate} bit/s, data {options.DataBitrate} bit/s"
            : $"  Classic CAN: {options.Bitrate} bit/s");
        Console.WriteLine(options.EdsPath is { } eds
            ? $"  Device description: {Path.GetFileName(eds)}"
            : "  No device description. Without one the stack transfers only 1000h:00, 1001h:00 and 1018h:00-04 to a"
              + Environment.NewLine + "  peer, so most checks below will be skipped. Pass --eds <file> to measure them.");
        if (options.Tier == Tier.Read)
            Console.WriteLine("  Tier read: nothing on the device will be changed.");
        else if (options.Tier == Tier.Write)
            Console.WriteLine("  Tier write: NMT state and volatile parameters will change, and be restored.");
        else
            Console.WriteLine("  Tier COMMISSION: LSS and 1010h may change the device permanently.");
    }

    private static void Summarise(Report report)
    {
        var counts = report.Findings.GroupBy(f => f.Verdict).ToDictionary(g => g.Key, g => g.Count());
        Console.WriteLine($"{counts.GetValueOrDefault(Verdict.Pass)} passed, "
            + $"{counts.GetValueOrDefault(Verdict.Fail)} FAILED, "
            + $"{counts.GetValueOrDefault(Verdict.Inconclusive)} inconclusive, "
            + $"{counts.GetValueOrDefault(Verdict.Skipped)} not applicable, "
            + $"{counts.GetValueOrDefault(Verdict.NotRun)} not run.");

        foreach (var group in report.Findings
            .Where(f => f.Verdict is Verdict.Fail or Verdict.Inconclusive && f.Attribution != Attribution.None)
            .GroupBy(f => f.Attribution)
            .OrderByDescending(g => g.Count()))
        {
            Console.WriteLine($"  {group.Count()} unresolved finding(s) attributed to: {group.Key.ToString().ToLowerInvariant()}");
        }

        if (report.BackgroundExceptions.Count > 0)
            Console.WriteLine($"  {report.BackgroundExceptions.Count} background exception(s) from the stack — see the report.");
    }
}
