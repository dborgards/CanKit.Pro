using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core;
using CanKit.Pro.Addressing;
using CanKit.Pro.J1939;
using CanKit.Pro.RawCan;

namespace CanKit.Pro.EchoProbe;

/// <summary>
/// Measures what a CAN adapter does with the frames it transmits itself: whether they come back
/// at all, whether they come back flagged as an echo, whether a confirmed send is confirmed, and
/// whether a J1939 node can claim an address against its own echo (issue #249).
///
/// The adapter under test (A) is opened once per work mode (Normal, Echo); a second interface (B)
/// on the same bus, always in Normal mode, acknowledges A's frames on the wire and records what
/// the bus really carried.
/// </summary>
internal static class Program
{
    private const int StdId = 0x123;

    private static async Task<int> Main(string[] args)
    {
        if (HasFlag(args, "--help") || HasFlag(args, "-h") || args.Length == 0)
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }

        var uriA = GetArg(args, "--a");
        var uriB = GetArg(args, "--b");
        if (uriA is null || uriB is null)
        {
            Console.Error.WriteLine("Both --a (adapter under test) and --b (peer on the same bus) are required.");
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var config = new ProbeConfig(
            UriA: uriA,
            UriB: uriB,
            Label: GetArg(args, "--label") ?? Sanitize(uriA),
            UseFd: HasFlag(args, "--fd"),
            Bitrate: ParseInt(GetArg(args, "--bitrate"), 500_000),
            DataBitrate: ParseInt(GetArg(args, "--dbit"), 2_000_000),
            Frames: ParseInt(GetArg(args, "--frames"), 5),
            WaitMs: ParseInt(GetArg(args, "--wait-ms"), 1000),
            SkipClaim: HasFlag(args, "--skip-claim"));
        var modes = (GetArg(args, "--modes") ?? "normal,echo")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(m => m.ToLowerInvariant())
            .ToArray();
        var outDir = GetArg(args, "--out") ?? Directory.GetCurrentDirectory();

        Console.WriteLine($"EchoProbe  A={config.UriA} (under test)  B={config.UriB} (peer)  label={config.Label}");
        Console.WriteLine(config.UseFd
            ? $"  CAN FD: arbitration {config.Bitrate} bit/s, data {config.DataBitrate} bit/s"
            : $"  Classic CAN: {config.Bitrate} bit/s");

        LoadAdapterAssemblies();

        var results = new List<ModeResult>();
        foreach (var workMode in modes.Select(ParseWorkMode))
        {
            Console.WriteLine();
            Console.WriteLine($"=== Work mode: {workMode} ===");
            var result = await RunModeAsync(config, workMode).ConfigureAwait(false);
            results.Add(result);
            PrintResult(result);
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var baseName = Path.Combine(outDir, $"echo-probe-{config.Label}-{stamp}");
        var report = new Report(config, Environment.OSVersion.ToString(), results);
        File.WriteAllText(baseName + ".json", JsonSerializer.Serialize(report, JsonOptions));
        File.WriteAllText(baseName + ".md", RenderMarkdown(report));
        Console.WriteLine();
        Console.WriteLine($"Written: {baseName}.md and .json -- send the .md back (or paste it).");
        return 0;
    }

    private static ChannelWorkMode ParseWorkMode(string mode) => mode switch
    {
        "normal" => ChannelWorkMode.Normal,
        "echo" => ChannelWorkMode.Echo,
        _ => throw new ArgumentException($"Unknown mode '{mode}' (use normal and/or echo)."),
    };

    // CanKit finds an adapter by the assemblies that are loaded when the first bus is opened. The
    // build generates a hint list for the packages the project references, and that is normally
    // enough; loading them here as well costs nothing and does not depend on it. An adapter whose
    // vendor driver is missing still loads (the driver is only needed when a bus is opened).
    private static void LoadAdapterAssemblies()
    {
        foreach (var name in new[] { "CanKit.Adapter.Virtual", "CanKit.Adapter.PCAN", "CanKit.Adapter.Kvaser", "CanKit.Adapter.Vector" })
        {
            try
            {
                Assembly.Load(new AssemblyName(name));
            }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                Console.WriteLine($"  note: adapter assembly {name} could not be loaded ({ex.GetType().Name}).");
            }
        }
    }

    private static async Task<ModeResult> RunModeAsync(ProbeConfig config, ChannelWorkMode workMode)
    {
        var result = new ModeResult { WorkMode = workMode.ToString() };
        using var busB = TryOpen(config.UriB, config, ChannelWorkMode.Normal, out var errorB);
        using var busA = busB is null ? null : TryOpen(config.UriA, config, workMode, out errorB);
        if (busA is null || busB is null)
        {
            result.OpenError = errorB;
            return result;
        }

        result.DeclaresEchoFeature = busA.Options.Features.HasFlag(CanFeature.Echo);
        result.ActualWorkMode = busA.Options.WorkMode.ToString();

        result.Raw = await MeasureRawAsync(busA, busB, config).ConfigureAwait(false);
        result.Confirmed = await MeasureConfirmedAsync(busA, config).ConfigureAwait(false);
        if (!config.UseFd && !config.SkipClaim)
            result.Claim = await MeasureClaimAsync(busA, busB, config).ConfigureAwait(false);
        return result;
    }

    // Every failure of the adapter under test is the tool's subject, not a fault of the tool:
    // it is recorded in the report instead of ending the run.
    private static ICanBus? TryOpen(string uri, ProbeConfig config, ChannelWorkMode workMode, out string? error)
    {
        try
        {
            var bus = CanBus.Open(uri, cfg => Configure(cfg, config, workMode));
            error = null;
            return bus;
        }
        catch (Exception ex)
        {
            error = $"{uri}: {ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    private static void Configure(IBusInitOptionsConfigurator cfg, ProbeConfig config, ChannelWorkMode workMode)
    {
        if (config.UseFd)
            cfg.Fd(config.Bitrate, config.DataBitrate).SetProtocolMode(CanProtocolMode.CanFd);
        else
            cfg.Baud(config.Bitrate).SetProtocolMode(CanProtocolMode.Can20);
        cfg.SetWorkMode(workMode);
    }

    // What the adapter reports for its own transmits, frame by frame, next to what the bus carried.
    private static async Task<RawResult> MeasureRawAsync(ICanBus busA, ICanBus busB, ProbeConfig config)
    {
        var onA = new List<Seen>();
        var onB = new List<Seen>();
        var clock = Stopwatch.StartNew();
        void RecordA(object? sender, CanReceiveDataView e) { lock (onA) onA.Add(Describe(e, clock)); }
        void RecordB(object? sender, CanReceiveDataView e) { lock (onB) onB.Add(Describe(e, clock)); }
        busA.FrameObserved += RecordA;
        busB.FrameObserved += RecordB;
        var sendErrors = new List<string>();
        try
        {
            for (var i = 0; i < config.Frames; i++)
            {
                try
                {
                    busA.Transmit(MakeFrame(config, i));
                }
                catch (Exception ex)
                {
                    sendErrors.Add($"frame {i}: {ex.GetType().Name}: {ex.Message}");
                }

                await Task.Delay(30).ConfigureAwait(false);
            }

            await Task.Delay(config.WaitMs).ConfigureAwait(false);
        }
        finally
        {
            busA.FrameObserved -= RecordA;
            busB.FrameObserved -= RecordB;
        }

        Seen[] a;
        Seen[] b;
        lock (onA) a = onA.Where(s => s.Id == StdId).ToArray();
        lock (onB) b = onB.Where(s => s.Id == StdId).ToArray();
        return new RawResult
        {
            Sent = config.Frames,
            SendErrors = sendErrors,
            SeenOnAUnderTest = a.Length,
            SeenOnAFlaggedAsEcho = a.Count(s => s.IsEcho),
            SeenOnAUnflagged = a.Count(s => !s.IsEcho),
            SeenOnPeer = b.Length,
            PeerFlaggedAsEcho = b.Count(s => s.IsEcho),
            FramesOnA = a,
        };
    }

    private static async Task<ConfirmedResult> MeasureConfirmedAsync(ICanBus busA, ProbeConfig config)
    {
        var attempts = new List<ConfirmedAttempt>();
        using var service = new CanBusService(busA);
        for (var i = 0; i < 3; i++)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                var tx = await service.SendConfirmedAsync(MakeFrame(config, 100 + i)).ConfigureAwait(false);
                attempts.Add(new ConfirmedAttempt(tx.Confirmed, tx.IsApproximated, tx.FailureReason.ToString(),
                    watch.ElapsedMilliseconds, null));
            }
            catch (Exception ex)
            {
                attempts.Add(new ConfirmedAttempt(false, false, "", watch.ElapsedMilliseconds,
                    $"{ex.GetType().Name}: {ex.Message}"));
            }
        }

        return new ConfirmedResult { Attempts = attempts };
    }

    // A J1939 node claiming an address with the adapter's own echo coming back at it. B acknowledges
    // the frames; what B saw of the claim is the ground truth.
    private static async Task<ClaimResult> MeasureClaimAsync(ICanBus busA, ICanBus busB, ProbeConfig config)
    {
        var claimFramesOnPeer = 0;
        void CountClaims(object? sender, CanReceiveDataView e)
        {
            var frame = e.CanFrame;
            if (frame.IsExtendedFrame && J1939Id.Decompose((uint)frame.ID).Pgn == 0xEE00u)
                Interlocked.Increment(ref claimFramesOnPeer);
        }

        busB.FrameObserved += CountClaims;
        var watch = Stopwatch.StartNew();
        string state;
        string? error = null;
        byte? address = null;
        try
        {
            var name = new J1939Name(identityNumber: 0x4711, manufacturerCode: 0x7FF, ecuInstance: 0,
                functionInstance: 0, function: 0x81, reserved: false, vehicleSystem: 0, vehicleSystemInstance: 0,
                industryGroup: 0, arbitraryAddressCapable: false);
            using var node = J1939Node.Open(busA, new J1939NodeOptions(name));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await node.ClaimAddressAsync(0x11, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
            }

            state = node.ClaimState.ToString();
            address = node.Address;
        }
        catch (Exception ex)
        {
            state = "(node could not be opened)";
            error = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            busB.FrameObserved -= CountClaims;
        }

        return new ClaimResult
        {
            State = state,
            Address = address is { } a ? $"0x{a:X2}" : null,
            Error = error,
            ElapsedMs = watch.ElapsedMilliseconds,
            ClaimFramesSeenByPeer = Volatile.Read(ref claimFramesOnPeer),
        };
    }

    private static CanFrame MakeFrame(ProbeConfig config, int index)
    {
        var length = config.UseFd ? 12 : 8;
        var data = new byte[length];
        data[0] = (byte)index;
        for (var i = 1; i < length; i++) data[i] = (byte)(0xA0 + i);
        return config.UseFd
            ? CanFrame.Fd(StdId, data, BRS: true)
            : CanFrame.Classic(StdId, data);
    }

    private static Seen Describe(CanReceiveDataView e, Stopwatch clock)
        => new(e.CanFrame.ID, e.CanFrame.IsExtendedFrame, e.IsEcho,
            e.CanFrame.Data.Length > 0 ? e.CanFrame.Data.Span[0] : -1, clock.ElapsedMilliseconds);

    private static void PrintResult(ModeResult r)
    {
        if (r.OpenError is not null)
        {
            Console.WriteLine($"  could not open: {r.OpenError}");
            return;
        }

        Console.WriteLine($"  adapter declares CanFeature.Echo: {r.DeclaresEchoFeature}; work mode reported: {r.ActualWorkMode}");
        if (r.Raw is { } raw)
        {
            Console.WriteLine($"  raw: sent {raw.Sent}; A saw its own frames {raw.SeenOnAUnderTest} time(s) " +
                $"(flagged as echo: {raw.SeenOnAFlaggedAsEcho}, unflagged: {raw.SeenOnAUnflagged}); peer B saw them {raw.SeenOnPeer} time(s)");
            foreach (var error in raw.SendErrors) Console.WriteLine($"    send error: {error}");
        }

        if (r.Confirmed is { } confirmed)
        {
            foreach (var attempt in confirmed.Attempts)
                Console.WriteLine($"  SendConfirmedAsync: confirmed={attempt.Confirmed} approximated={attempt.IsApproximated} " +
                    $"reason={attempt.FailureReason} after {attempt.ElapsedMs} ms{(attempt.Error is null ? "" : "  " + attempt.Error)}");
        }

        if (r.Claim is { } claim)
        {
            Console.WriteLine($"  J1939 claim of 0x11: state={claim.State} address={claim.Address ?? "-"} " +
                $"after {claim.ElapsedMs} ms; claim frames the peer saw: {claim.ClaimFramesSeenByPeer}" +
                (claim.Error is null ? "" : $"  ({claim.Error})"));
        }
    }

    private static string RenderMarkdown(Report report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Echo probe: {report.Config.Label}");
        sb.AppendLine();
        sb.AppendLine($"- A (under test): `{report.Config.UriA}`; B (peer): `{report.Config.UriB}`");
        sb.AppendLine(report.Config.UseFd
            ? $"- CAN FD, {report.Config.Bitrate} / {report.Config.DataBitrate} bit/s"
            : $"- Classic CAN, {report.Config.Bitrate} bit/s");
        sb.AppendLine($"- OS: {report.Os}; frames per run: {report.Config.Frames}");
        sb.AppendLine();
        sb.AppendLine("| Work mode | Declares Echo | A sees own TX | flagged | unflagged | B sees | SendConfirmedAsync | J1939 claim |");
        sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var r in report.Results)
        {
            if (r.OpenError is not null)
            {
                sb.AppendLine($"| {r.WorkMode} | - | could not open: {r.OpenError.Replace("|", "/")} | | | | | |");
                continue;
            }

            var raw = r.Raw!;
            var confirmed = string.Join("; ", r.Confirmed!.Attempts.Select(a =>
                $"{(a.Confirmed ? "confirmed" : "NOT confirmed")}{(a.IsApproximated ? " (approx)" : "")} {a.ElapsedMs} ms"));
            var claim = r.Claim is null
                ? "skipped"
                : $"{r.Claim.State} {r.Claim.Address ?? ""} ({r.Claim.ElapsedMs} ms, peer saw {r.Claim.ClaimFramesSeenByPeer})";
            sb.AppendLine($"| {r.WorkMode} | {r.DeclaresEchoFeature} | {raw.SeenOnAUnderTest}/{raw.Sent} | " +
                $"{raw.SeenOnAFlaggedAsEcho} | {raw.SeenOnAUnflagged} | {raw.SeenOnPeer}/{raw.Sent} | {confirmed} | {claim} |");
        }

        sb.AppendLine();
        sb.AppendLine("Frames A reported for its own id (first run of each mode):");
        foreach (var r in report.Results.Where(r => r.Raw is not null))
        {
            sb.AppendLine();
            sb.AppendLine($"{r.WorkMode}:");
            sb.AppendLine("```");
            foreach (var f in r.Raw!.FramesOnA)
                sb.AppendLine($"  +{f.AtMs,5} ms  id=0x{f.Id:X}  ext={f.Extended}  IsEcho={f.IsEcho}  counter={f.Counter}");
            if (r.Raw.FramesOnA.Length == 0) sb.AppendLine("  (none)");
            sb.AppendLine("```");
        }

        return sb.ToString();
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private const string Usage = """
        EchoProbe: what does an adapter do with the frames it sends itself?

        Wiring: two CAN interfaces on ONE bus, both ends terminated with 120 ohm, same bit rate.
        A is the adapter under test, B is any second interface (it acknowledges A on the wire and
        records what the bus carried). Run once per adapter you want to know about.

        Usage:
          EchoProbe --a <uri> --b <uri> [options]

        Adapter URIs (CanKit): pcan://PCAN_USBBUS1   kvaser://0   vector://...   virtual://probe/0

        Options:
          --bitrate <n>     bit rate, classic or the FD arbitration phase (default 500000)
          --fd              CAN FD (also --dbit <n>, data phase, default 2000000)
          --modes <list>    normal, echo, or both (default normal,echo)
          --frames <n>      frames per run (default 5)
          --wait-ms <n>     wait for stragglers after the last frame (default 1000)
          --label <text>    name for the report files (default: derived from --a)
          --skip-claim      skip the J1939 claim test
          --out <dir>       where the .md and .json go (default: current directory)

        Examples:
          EchoProbe --a pcan://PCAN_USBBUS1 --b kvaser://0 --label pcan-usb-classic
          EchoProbe --a pcan://PCAN_USBBUS2 --b kvaser://0 --fd --label pcan-usb-fd
          EchoProbe --a virtual://probe/0 --b virtual://probe/1     (dry run, no hardware)
        """;

    private static string? GetArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        }

        return null;
    }

    private static bool HasFlag(string[] args, string name)
        => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    private static int ParseInt(string? text, int fallback)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static string Sanitize(string uri)
    {
        var sb = new StringBuilder();
        foreach (var c in uri) sb.Append(char.IsLetterOrDigit(c) ? c : '-');
        return sb.ToString().Trim('-');
    }

    private sealed record ProbeConfig(string UriA, string UriB, string Label, bool UseFd, int Bitrate,
        int DataBitrate, int Frames, int WaitMs, bool SkipClaim);

    private sealed record Report(ProbeConfig Config, string Os, List<ModeResult> Results);

    private sealed record Seen(int Id, bool Extended, bool IsEcho, int Counter, long AtMs);

    private sealed record ConfirmedAttempt(bool Confirmed, bool IsApproximated, string FailureReason, long ElapsedMs, string? Error);

    private sealed class ModeResult
    {
        public string WorkMode { get; set; } = "";
        public string? OpenError { get; set; }
        public bool DeclaresEchoFeature { get; set; }
        public string? ActualWorkMode { get; set; }
        public RawResult? Raw { get; set; }
        public ConfirmedResult? Confirmed { get; set; }
        public ClaimResult? Claim { get; set; }
    }

    private sealed class RawResult
    {
        public int Sent { get; set; }
        public List<string> SendErrors { get; set; } = new();
        public int SeenOnAUnderTest { get; set; }
        public int SeenOnAFlaggedAsEcho { get; set; }
        public int SeenOnAUnflagged { get; set; }
        public int SeenOnPeer { get; set; }
        public int PeerFlaggedAsEcho { get; set; }
        public Seen[] FramesOnA { get; set; } = Array.Empty<Seen>();
    }

    private sealed class ConfirmedResult
    {
        public List<ConfirmedAttempt> Attempts { get; set; } = new();
    }

    private sealed class ClaimResult
    {
        public string State { get; set; } = "";
        public string? Address { get; set; }
        public string? Error { get; set; }
        public long ElapsedMs { get; set; }
        public int ClaimFramesSeenByPeer { get; set; }
    }
}
