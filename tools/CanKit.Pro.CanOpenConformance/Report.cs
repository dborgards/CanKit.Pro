using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CanKit.Pro.CanOpenConformance;

/// <summary>Everything a run produced, ready to be written out or pasted into an issue.</summary>
internal sealed record Report(
    string Label,
    DateTimeOffset StartedAt,
    double DurationSeconds,
    string ToolVersion,
    string Host,
    RunOptions Options,
    string? OpenError,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<Incident> Incidents,
    IReadOnlyList<string> BackgroundExceptions);

/// <summary>
/// Writes the run up as a report that can be read on its own. Three things it is built to answer
/// without the reader going back to the console: what works, what does not and whose fault that is,
/// and — the one a conformance report usually leaves out — what was never checked, and why.
/// </summary>
internal static class ReportRenderer
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string ToJson(Report report) => JsonSerializer.Serialize(report, Json);

    public static string ToolVersion =>
        typeof(ReportRenderer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(ReportRenderer).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    public static string ToMarkdown(Report report)
    {
        var text = new StringBuilder();
        Heading(text, report);
        VerdictSection(text, report);
        Subject(text, report);
        Failures(text, report);
        Unclear(text, report);
        Working(text, report);
        Coverage(text, report);
        Errors(text, report);
        AllFindings(text, report);
        HowToRead(text, report);
        return text.ToString();
    }

    private static void Heading(StringBuilder text, Report report)
    {
        text.AppendLine($"# CANopen conformance report — {report.Label}");
        text.AppendLine();
        if (report.Options.Simulate)
        {
            text.AppendLine("> **This is a simulated run.** The device under test is this repository's own CANopen");
            text.AppendLine("> stack on a virtual bus, not hardware. It shows that the checks ask coherent questions;");
            text.AppendLine("> it is not evidence about any real device. Do not file it as a hardware result.");
            text.AppendLine();
        }

        text.AppendLine("| | |");
        text.AppendLine("| --- | --- |");
        Row(text, "Run started", report.StartedAt.ToString("u", CultureInfo.InvariantCulture));
        Row(text, "Duration", $"{report.DurationSeconds.ToString("F1", CultureInfo.InvariantCulture)} s");
        Row(text, "Runner version", report.ToolVersion);
        Row(text, "Host", report.Host);
        Row(text, "Endpoint", $"`{report.Options.Endpoint}`");
        Row(text, "Bit rate", report.Options.UseFd
            ? $"{report.Options.Bitrate} bit/s arbitration, {report.Options.DataBitrate} bit/s data (CAN FD)"
            : $"{report.Options.Bitrate} bit/s (classic CAN 2.0)");
        Row(text, "Device under test", report.Options.TargetNode is { } node ? $"node {node}" : "none (bus-wide groups only)");
        Row(text, "Runner's own node-id", report.Options.ClientNode.ToString(CultureInfo.InvariantCulture));
        Row(text, "Device description", report.Options.EdsPath is { } eds ? $"`{Path.GetFileName(eds)}`" : "**none** — see *What was not checked*");
        Row(text, "Safety tier", report.Options.Tier.ToString().ToLowerInvariant());
        Row(text, "Groups", string.Join(", ", report.Options.Groups));
        text.AppendLine();
    }

    private static void VerdictSection(StringBuilder text, Report report)
    {
        text.AppendLine("## Verdict");
        text.AppendLine();
        if (report.OpenError is { } error)
        {
            // A driver loader error can be twenty lines of search paths. The first line names the
            // problem; the rest is in the .json, where a reader who wants it will look, and it is
            // not worth making the verdict unreadable for.
            var lines = error.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            text.AppendLine($"**The bus could not be opened, so nothing was measured.** `{lines[0].Trim()}`");
            text.AppendLine();
            if (lines.Length > 1)
            {
                text.AppendLine($"The driver loader added {lines.Length - 1} further line(s) of detail; they are in the");
                text.AppendLine("`.json` next to this file, under `openError`.");
                text.AppendLine();
            }

            text.AppendLine("This is the adapter, its vendor driver or the endpoint string — not the device. On Windows the");
            text.AppendLine("driver has to be installed separately: `PCANBasic.dll` for PEAK, `canlib32.dll` for Kvaser, the");
            text.AppendLine("XL driver for Vector.");
            text.AppendLine();
            return;
        }

        var counts = report.Findings.GroupBy(f => f.Verdict).ToDictionary(g => g.Key, g => g.Count());
        var failed = counts.GetValueOrDefault(Verdict.Fail);
        var passed = counts.GetValueOrDefault(Verdict.Pass);
        var unclear = counts.GetValueOrDefault(Verdict.Inconclusive);

        text.AppendLine(failed == 0
            ? $"**No check failed.** {passed} passed, {unclear} inconclusive, "
              + $"{counts.GetValueOrDefault(Verdict.Skipped)} not applicable, {counts.GetValueOrDefault(Verdict.NotRun)} not run."
            : $"**{failed} check(s) failed.** {passed} passed, {unclear} inconclusive, "
              + $"{counts.GetValueOrDefault(Verdict.Skipped)} not applicable, {counts.GetValueOrDefault(Verdict.NotRun)} not run.");
        text.AppendLine();

        text.AppendLine("| Verdict | Count | Meaning |");
        text.AppendLine("| --- | --- | --- |");
        text.AppendLine($"| Pass | {passed} | The device did what the specification or its own description requires. |");
        text.AppendLine($"| Fail | {failed} | It answered, and the answer contradicts that. |");
        text.AppendLine($"| Inconclusive | {unclear} | It was asked and nothing usable came back. See the attribution. |");
        text.AppendLine($"| Not applicable | {counts.GetValueOrDefault(Verdict.Skipped)} | An optional feature the device does not have, or input the run was not given. |");
        text.AppendLine($"| Not run | {counts.GetValueOrDefault(Verdict.NotRun)} | Excluded by `--groups` or `--tier`. Nothing was measured. |");
        text.AppendLine();

        var blamed = report.Findings
            .Where(f => f.Verdict is Verdict.Fail or Verdict.Inconclusive && f.Attribution != Attribution.None)
            .GroupBy(f => f.Attribution)
            .OrderByDescending(g => g.Count())
            .ToList();
        if (blamed.Count == 0) return;

        text.AppendLine("Where the unresolved findings point:");
        text.AppendLine();
        text.AppendLine("| Attribution | Findings | What it means |");
        text.AppendLine("| --- | --- | --- |");
        foreach (var group in blamed)
        {
            text.AppendLine($"| {group.Key} | {group.Count()} | {Explain(group.Key)} |");
        }

        text.AppendLine();
    }

    private static string Explain(Attribution attribution) => attribution switch
    {
        Attribution.Device => "The device under test. Take it to whoever ships its firmware.",
        Attribution.Tool => "This runner or the CanKit.Pro stack. **Open an issue against this repository** — these are not device defects.",
        Attribution.Transport => "The bus, the adapter or its vendor driver: wiring, termination, bit rate, a missing driver.",
        Attribution.Setup => "How this run was invoked. Change the arguments and run again.",
        Attribution.Unknown => "Not attributable from what was observed. Needs a second run, or a second interface on the bus.",
        _ => "—",
    };

    /// <summary>Names the device the report is about, from what the run actually read off it. A
    /// report that does not identify its subject cannot be filed against anything.</summary>
    private static void Subject(StringBuilder text, Report report)
    {
        if (report.OpenError is not null) return;
        var identity = Measurements(report, "IDN-03");
        var deviceType = Measurements(report, "IDN-01");
        var strings = Measurements(report, "IDN-04");
        var file = Measurements(report, "EDS-01");
        if (identity.Count == 0 && deviceType.Count == 0 && strings.Count == 0 && file.Count == 0) return;

        text.AppendLine("## What was tested");
        text.AppendLine();
        text.AppendLine("| | |");
        text.AppendLine("| --- | --- |");
        foreach (var key in new[] { "vendor-id", "product code", "revision number", "serial number" })
            if (identity.TryGetValue(key, out var value)) Row(text, key, $"`{value}`");
        foreach (var key in new[] { "device profile number", "additional information" })
            if (deviceType.TryGetValue(key, out var value)) Row(text, $"1000h {key}", value);
        foreach (var key in new[] { "manufacturer device name", "manufacturer hardware version", "manufacturer software version" })
            if (strings.TryGetValue(key, out var value)) Row(text, key, value);
        foreach (var key in new[] { "product name", "vendor name", "objects declared", "LSS supported", "TPDOs declared", "RPDOs declared" })
            if (file.TryGetValue(key, out var value)) Row(text, $"EDS: {key}", value);
        text.AppendLine();
    }

    private static void Failures(StringBuilder text, Report report)
    {
        var failures = report.Findings.Where(f => f.Verdict == Verdict.Fail).ToList();
        text.AppendLine("## What does not work");
        text.AppendLine();
        if (failures.Count == 0)
        {
            text.AppendLine("Nothing that was checked failed. What was *not* checked is below — that list is the one");
            text.AppendLine("to read before concluding the device conforms.");
            text.AppendLine();
            return;
        }

        foreach (var group in failures.GroupBy(f => f.Attribution))
        {
            text.AppendLine($"### Attributed to: {group.Key} — {Explain(group.Key)}");
            text.AppendLine();
            foreach (var finding in group) Detail(text, finding);
        }
    }

    private static void Unclear(StringBuilder text, Report report)
    {
        var unclear = report.Findings.Where(f => f.Verdict == Verdict.Inconclusive).ToList();
        if (unclear.Count == 0) return;

        text.AppendLine("## What could not be concluded");
        text.AppendLine();
        text.AppendLine("These were asked and nothing usable came back. Each says who the likeliest reason is; an");
        text.AppendLine("*Unknown* attribution means the observation does not decide it, and that is the honest answer");
        text.AppendLine("rather than a verdict.");
        text.AppendLine();
        foreach (var group in unclear.GroupBy(f => f.Attribution))
        {
            text.AppendLine($"### Attributed to: {group.Key} — {Explain(group.Key)}");
            text.AppendLine();
            foreach (var finding in group) Detail(text, finding);
        }
    }

    private static void Working(StringBuilder text, Report report)
    {
        var passed = report.Findings.Where(f => f.Verdict == Verdict.Pass).ToList();
        text.AppendLine("## What works");
        text.AppendLine();
        if (passed.Count == 0)
        {
            text.AppendLine("No check passed.");
            text.AppendLine();
            return;
        }

        text.AppendLine("| Check | What was established | Measured in |");
        text.AppendLine("| --- | --- | --- |");
        foreach (var finding in passed)
        {
            text.AppendLine($"| `{finding.Id}` {Escape(finding.Title)} | {Escape(finding.Detail)} | "
                + $"{(finding.ElapsedMs is { } ms ? ms.ToString("F0", CultureInfo.InvariantCulture) + " ms" : "—")} |");
        }

        text.AppendLine();
    }

    /// <summary>
    /// The section a conformance report is usually missing: every feature the tool knows how to
    /// check, and whether this run checked it. A device is not shown to conform by a page of
    /// passes if half the protocol was never addressed, and the reasons here are the ones the
    /// operator can act on.
    /// </summary>
    private static void Coverage(StringBuilder text, Report report)
    {
        text.AppendLine("## What was checked, and what was not");
        text.AppendLine();
        text.AppendLine("| Feature | Checks | Measured | Not measured — why |");
        text.AppendLine("| --- | --- | --- | --- |");

        foreach (var group in RunOptions.AllGroups)
        {
            var findings = report.Findings.Where(f => string.Equals(f.Group, group, StringComparison.OrdinalIgnoreCase)).ToList();
            if (findings.Count == 0)
            {
                text.AppendLine($"| {Feature(group)} | — | none | the runner ran no check in this group |");
                continue;
            }

            var measured = findings.Count(f => f.Verdict is Verdict.Pass or Verdict.Fail);
            var reasons = findings
                .Where(f => f.Verdict is Verdict.Skipped or Verdict.NotRun or Verdict.Inconclusive)
                .Select(f => $"`{f.Id}`: {Shorten(f.Detail)}")
                .ToList();

            text.AppendLine($"| {Feature(group)} | {findings.Count} | "
                + $"{measured} ({findings.Count(f => f.Verdict == Verdict.Pass)} pass, {findings.Count(f => f.Verdict == Verdict.Fail)} fail) | "
                + (reasons.Count == 0 ? "—" : Escape(string.Join("<br>", reasons))) + " |");
        }

        text.AppendLine();

        var notAddressed = new[]
        {
            ("SDO block transfer (CiA 301 §7.2.4.3)", "Not addressed by this runner: block mode is negotiated per transfer, and provoking it needs an object large enough that the device would have to be configured for it."),
            ("Multiplexed PDO / MPDO (CiA 301 §7.2.3)", "Not addressed: a device has to be configured for source or destination mode first, which is beyond what a read-tier run may do."),
            ("CANopen Safety / SRDO (CiA 304)", "Not addressed: an SRDO consumer needs the peer's safety configuration, and a conformance run must not write it."),
            ("NMT flying master (CiA 302-2)", "Not addressed: it is a property of a set of masters, not of one device."),
            ("LSS bit-timing change and activation", "Not addressed: a wrong bit rate silently removes the device from the bus, and recovering it needs physical access."),
            ("Boot-up behaviour after power cycle", "Not addressed: the runner cannot power-cycle the device."),
        };

        text.AppendLine("Beyond the groups above, these are deliberately **not** part of a run, so that no reader");
        text.AppendLine("mistakes their absence for a pass:");
        text.AppendLine();
        text.AppendLine("| Not addressed | Why |");
        text.AppendLine("| --- | --- |");
        foreach (var (what, why) in notAddressed) text.AppendLine($"| {what} | {why} |");
        text.AppendLine();
    }

    private static void Errors(StringBuilder text, Report report)
    {
        text.AppendLine("## Errors that occurred");
        text.AppendLine();
        if (report.Incidents.Count == 0 && report.BackgroundExceptions.Count == 0)
        {
            text.AppendLine("No exception was raised and no background failure was reported during the run.");
            text.AppendLine();
            return;
        }

        if (report.Incidents.Count > 0)
        {
            text.AppendLine("| Where | Kind | Message | Attributed to |");
            text.AppendLine("| --- | --- | --- | --- |");
            foreach (var incident in report.Incidents)
            {
                text.AppendLine($"| {Escape(incident.Where)} | `{incident.Kind}` | {Escape(incident.Message)} | {incident.Attribution} |");
            }

            text.AppendLine();
        }

        if (report.BackgroundExceptions.Count > 0)
        {
            text.AppendLine("Background failures reported by the stack's own actor or bus service. These happened");
            text.AppendLine("outside any one check, and an entry here is a finding against this repository:");
            text.AppendLine();
            foreach (var background in report.BackgroundExceptions) text.AppendLine($"- `{Escape(background)}`");
            text.AppendLine();
        }
    }

    private static void AllFindings(StringBuilder text, Report report)
    {
        text.AppendLine("## Every check, in the order it ran");
        text.AppendLine();
        text.AppendLine("| Check | Group | Tier | Requirement | Verdict | Attribution | Detail |");
        text.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
        foreach (var finding in report.Findings)
        {
            text.AppendLine($"| `{finding.Id}` {Escape(finding.Title)} | {finding.Group} | "
                + $"{finding.Tier.ToString().ToLowerInvariant()} | {finding.Requirement ?? "—"} | "
                + $"{Mark(finding.Verdict)} | {(finding.Attribution == Attribution.None ? "—" : finding.Attribution.ToString())} | "
                + $"{Escape(Shorten(finding.Detail))} |");
        }

        text.AppendLine();
    }

    private static void HowToRead(StringBuilder text, Report report)
    {
        text.AppendLine("## How to read this");
        text.AppendLine();
        text.AppendLine("- **A pass is about one question.** Each check names the object or service it asked about;");
        text.AppendLine("  nothing here generalises from a pass to \"the device conforms\".");
        text.AppendLine("- **Every period is measured on this host's clock**, not the device's. A loaded host widens the");
        text.AppendLine("  range without the device changing, so a period is reported with its spread and never gated on.");
        text.AppendLine("- **An SDO abort from the device and one raised locally are different findings.** The local one");
        text.AppendLine("  never reached the bus; it is attributed to the runner, not to the device.");
        if (report.Options.EdsPath is null)
        {
            text.AppendLine("- **No device description was given.** The stack transfers only `1000h:00`, `1001h:00` and");
            text.AppendLine("  `1018h:00`–`04` to a peer with no EDS or DCF bound, so every check that needed another object");
            text.AppendLine("  is marked *not applicable* above rather than attempted. Re-run with `--eds <file>` to measure");
            text.AppendLine("  the rest; that is the single biggest change in coverage available.");
        }

        if (report.Options.Tier != Tier.Commission)
        {
            text.AppendLine($"- **The tier was `{report.Options.Tier.ToString().ToLowerInvariant()}`.** Checks needing a deeper tier were not run. They are listed");
            text.AppendLine("  as *not run* with the tier they need; raising it changes the device, so it is a deliberate choice.");
        }

        text.AppendLine();
    }

    private static void Detail(StringBuilder text, Finding finding)
    {
        text.AppendLine($"**`{finding.Id}` {finding.Title}**"
            + (finding.Requirement is { } requirement ? $" — {requirement}" : string.Empty));
        text.AppendLine();
        text.AppendLine(finding.Detail);
        text.AppendLine();
        if (finding.Measurements is { Count: > 0 })
        {
            foreach (var pair in finding.Measurements) text.AppendLine($"- {Escape(pair.Key)}: `{Escape(pair.Value)}`");
            text.AppendLine();
        }
    }

    private static IReadOnlyDictionary<string, string> Measurements(Report report, string id)
        => report.Findings.FirstOrDefault(f => f.Id == id)?.Measurements ?? new Dictionary<string, string>();

    private static string Feature(string group) => group switch
    {
        "bus" => "Adapter and bus state",
        "discovery" => "Node discovery (presence, boot-up)",
        "identity" => "Identity (1000h, 1001h, 1018h, name strings)",
        "sdo" => "SDO client/server (expedited, segmented, aborts)",
        "eds" => "EDS/DCF cross-check against the device",
        "heartbeat" => "Heartbeat producer and consumer (1017h)",
        "guarding" => "Node guarding (100Ch/100Dh)",
        "nmt" => "NMT state machine",
        "sync" => "SYNC (1005h/1006h)",
        "pdo" => "PDO mapping and transmission (1400h/1600h/1800h/1A00h)",
        "emcy" => "Emergency objects (EMCY)",
        "lss" => "Layer Setting Services (CiA 305)",
        "store" => "Store and restore parameters (1010h/1011h)",
        _ => group,
    };

    private static string Mark(Verdict verdict) => verdict switch
    {
        Verdict.Pass => "**pass**",
        Verdict.Fail => "**FAIL**",
        Verdict.Inconclusive => "inconclusive",
        Verdict.Skipped => "n/a",
        _ => "not run",
    };

    private static void Row(StringBuilder text, string name, string value) => text.AppendLine($"| {name} | {value} |");

    private static string Shorten(string detail)
    {
        var single = detail.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return single.Length <= 220 ? single : single[..217] + "...";
    }

    /// <summary>A pipe inside a cell ends the cell, and these strings carry device output.</summary>
    private static string Escape(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);
}
