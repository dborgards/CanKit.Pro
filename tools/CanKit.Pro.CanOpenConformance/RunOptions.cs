using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace CanKit.Pro.CanOpenConformance;

/// <summary>
/// Everything the run was told to do. Parsing is separate from running for the reason EchoProbe
/// found: an option that cannot be used must end the run before a bus is opened, because
/// substituting a default silently would measure a bus configuration nobody asked for.
/// </summary>
internal sealed record RunOptions(
    string Endpoint,
    byte? TargetNode,
    byte ClientNode,
    int Bitrate,
    bool UseFd,
    int DataBitrate,
    string? EdsPath,
    Tier Tier,
    IReadOnlyList<string> Groups,
    int ListenMs,
    int HeartbeatWindowMs,
    int PdoWindowMs,
    int SdoTimeoutMs,
    bool Simulate,
    string Label,
    string OutputDirectory)
{
    /// <summary>Every group, in the order a run executes them: facts first, then what the facts
    /// enable, and the two that change the device last.</summary>
    public static readonly string[] AllGroups =
    {
        "bus", "discovery", "identity", "sdo", "eds", "heartbeat", "guarding",
        "nmt", "sync", "pdo", "emcy", "lss", "store",
    };

    public bool IsVirtual => Endpoint.StartsWith("virtual://", StringComparison.OrdinalIgnoreCase);

    public static string Usage => """
        CanKit.Pro CANopen conformance runner — checks a CANopen device against CiA 301/305 and,
        where one is given, against its own EDS/DCF. Runs on real hardware (PEAK, Kvaser, Vector)
        and, with --simulate, on the hardware-free virtual adapter.

          --endpoint <uri>       Required. kvaser://0, pcan://PCAN_USBBUS1, vector://..., virtual://run/0
          --node <1..127>        The device under test. Omit and only the bus and discovery groups
                                 run — everything else addresses a node.
          --client-node <1..127> This tool's own node-id on the bus. Default 1.
          --bitrate <bit/s>      Default 250000 (classic CAN).
          --fd                   Open the bus as CAN FD (CiA 301 v4.2 devices only).
          --dbit <bit/s>         FD data phase bit rate. Default 2000000.

          --eds <path>           The device's EDS or DCF. Without it the stack transfers only
                                 1000h:00, 1001h:00 and 1018h:00-04 to a peer, so most SDO, PDO
                                 and store checks are skipped. With it, every object the file
                                 declares is read back from the device and compared.

          --tier read|write|commission
                                 How much the run may change. Default read: reads and passive
                                 observation only. write: adds SDO downloads restored afterwards,
                                 NMT commands and SYNC. commission: adds LSS node-id/bit-timing
                                 and 1010h store — these outlive a power cycle.

          --groups <a,b,...>     Default all. One or more of:
                                 bus, discovery, identity, sdo, eds, heartbeat, guarding,
                                 nmt, sync, pdo, emcy, lss, store
          --listen-ms <ms>       Passive discovery window. Default 2000.
          --heartbeat-ms <ms>    Heartbeat observation window. Default 4000.
          --pdo-ms <ms>          PDO observation window. Default 3000.
          --sdo-timeout-ms <ms>  Per-transfer SDO timeout. Default 1000.

          --simulate             virtual:// only: start a simulated CANopen device in-process so
                                 the same checks run with no hardware.
          --label <text>         Names the report files. Default: derived from the endpoint.
          --out <dir>            Where the report goes. Default: the current directory.
          --help

        Exit code 0 when no check failed, 1 when one did, 2 on a usage error.
        """;

    /// <summary>
    /// Parses the command line. Returns null and fills <paramref name="errors"/> when the run
    /// must not start; every problem is collected rather than only the first, so one run of the
    /// tool tells the operator everything that is wrong with the invocation.
    /// </summary>
    public static RunOptions? Parse(string[] args, out IReadOnlyList<string> errors)
    {
        var problems = new List<string>();
        errors = problems;

        var valueOptions = new[]
        {
            "--endpoint", "--node", "--client-node", "--bitrate", "--dbit", "--eds", "--tier",
            "--groups", "--listen-ms", "--heartbeat-ms", "--pdo-ms", "--sdo-timeout-ms",
            "--label", "--out",
        };
        var flagOptions = new[] { "--fd", "--simulate", "--help", "-h" };

        // Nothing may be left over and nothing may be given twice: a typo would otherwise run
        // with a default, and of two values for one option only one can be the one meant.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var isValue = valueOptions.Contains(args[i], StringComparer.OrdinalIgnoreCase);
            if (!isValue && !flagOptions.Contains(args[i], StringComparer.OrdinalIgnoreCase))
                problems.Add($"Unknown option or stray argument '{args[i]}'.");
            else if (!seen.Add(args[i]))
                problems.Add($"{args[i]} is given more than once.");
            if (isValue) i++; // its value belongs to Text
        }

        string? Text(string name)
        {
            var at = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            if (at < 0) return null;
            if (at + 1 >= args.Length || args[at + 1].StartsWith("--", StringComparison.Ordinal))
            {
                problems.Add($"{name} needs a value.");
                return null;
            }

            return args[at + 1];
        }

        int Whole(string name, int fallback, int min, int max)
        {
            var text = Text(name);
            if (text is null) return fallback;
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                problems.Add($"{name}: '{text}' is not a whole number.");
                return fallback;
            }

            if (value < min || value > max)
            {
                problems.Add($"{name}: {value} is outside {min}..{max}.");
                return fallback;
            }

            return value;
        }

        byte? NodeId(string name, byte? fallback)
        {
            var text = Text(name);
            if (text is null) return fallback;
            if (!byte.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                || value is < 1 or > 127)
            {
                problems.Add($"{name}: '{text}' is not a CANopen node-id (1..127).");
                return fallback;
            }

            return value;
        }

        var endpoint = Text("--endpoint");
        if (endpoint is null) problems.Add("--endpoint is required (for example kvaser://0 or virtual://conformance/0).");

        var target = NodeId("--node", null);
        var client = NodeId("--client-node", 1) ?? 1;
        if (target is not null && target == client)
            problems.Add($"--node and --client-node are both {client}; the tool cannot be the device it tests.");

        var tierText = (Text("--tier") ?? "read").ToLowerInvariant();
        var tier = tierText switch
        {
            "read" => Tier.Read,
            "write" => Tier.Write,
            "commission" => Tier.Commission,
            _ => Unknown(),
        };

        Tier Unknown()
        {
            problems.Add($"--tier: '{tierText}' is not read, write or commission.");
            return Tier.Read;
        }

        var groupText = Text("--groups");
        var groups = groupText is null
            ? AllGroups
            : groupText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(g => g.ToLowerInvariant())
                .ToArray();
        if (groups.Length == 0) problems.Add($"--groups needs at least one of: {string.Join(", ", AllGroups)}.");
        foreach (var unknown in groups.Where(g => !AllGroups.Contains(g, StringComparer.OrdinalIgnoreCase)))
            problems.Add($"--groups: unknown group '{unknown}' (known: {string.Join(", ", AllGroups)}).");

        var eds = Text("--eds");
        if (eds is not null && !File.Exists(eds)) problems.Add($"--eds: '{eds}' does not exist.");

        var simulate = args.Contains("--simulate", StringComparer.OrdinalIgnoreCase);
        var useFd = args.Contains("--fd", StringComparer.OrdinalIgnoreCase);

        var options = new RunOptions(
            Endpoint: endpoint ?? string.Empty,
            TargetNode: target,
            ClientNode: client,
            Bitrate: Whole("--bitrate", 250_000, 1, 10_000_000),
            UseFd: useFd,
            DataBitrate: Whole("--dbit", 2_000_000, 1, 20_000_000),
            EdsPath: eds,
            Tier: tier,
            Groups: groups,
            ListenMs: Whole("--listen-ms", 2_000, 100, 120_000),
            HeartbeatWindowMs: Whole("--heartbeat-ms", 4_000, 200, 120_000),
            PdoWindowMs: Whole("--pdo-ms", 3_000, 200, 120_000),
            SdoTimeoutMs: Whole("--sdo-timeout-ms", 1_000, 50, 60_000),
            Simulate: simulate,
            Label: Text("--label") ?? Sanitize(endpoint ?? "run"),
            OutputDirectory: Text("--out") ?? Directory.GetCurrentDirectory());

        if (simulate && !options.IsVirtual)
            problems.Add("--simulate needs a virtual:// endpoint; it starts a device in this process, which no hardware bus would see.");

        return problems.Count > 0 ? null : options;
    }

    /// <summary>The label as typed stays in the report; as part of a file name it may carry no
    /// path characters, and it is bounded so the names stay inside the file system's limits.</summary>
    public static string Sanitize(string text)
    {
        var clean = new string(text.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        while (clean.Contains("--", StringComparison.Ordinal)) clean = clean.Replace("--", "-", StringComparison.Ordinal);
        if (clean.Length == 0) clean = "run";
        return clean.Length > 60 ? clean[..60] : clean;
    }
}
