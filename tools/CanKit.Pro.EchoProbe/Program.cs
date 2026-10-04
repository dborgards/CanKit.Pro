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
    private const byte ClaimedAddress = 0x11;

    private static readonly J1939Name ProbeName = new(identityNumber: 0x4711, manufacturerCode: 0x7FF, ecuInstance: 0,
        functionInstance: 0, function: 0x81, reserved: false, vehicleSystem: 0, vehicleSystemInstance: 0,
        industryGroup: 0, arbitraryAddressCapable: false);

    private static async Task<int> Main(string[] args)
    {
        if (HasFlag(args, "--help") || HasFlag(args, "-h") || args.Length == 0)
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }

        // Every option is either absent (its default), or present with a value that can be used.
        // A value that cannot be used ends the run before any hardware is opened: silently
        // substituting a default would run the probe against a bus configuration nobody asked for.
        var optionErrors = new List<string>();
        string? Text(string name)
        {
            var at = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            if (at < 0) return null;
            if (at + 1 >= args.Length || args[at + 1].StartsWith("--", StringComparison.Ordinal))
            {
                optionErrors.Add($"{name} needs a value.");
                return null;
            }

            return args[at + 1];
        }

        int Whole(string name, int fallback)
        {
            var text = Text(name);
            if (text is null) return fallback;
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return value;
            optionErrors.Add($"{name}: '{text}' is not a whole number.");
            return fallback;
        }

        // Nothing may be left over: a typo in an option name would otherwise run with its default.
        var valueOptions = new[] { "--a", "--b", "--label", "--bitrate", "--dbit", "--frames", "--wait-ms", "--modes", "--out" };
        var flagOptions = new[] { "--fd", "--skip-claim", "--help", "-h" };
        // And nothing may be given twice: only one of two values for an option could be the one meant.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var isValueOption = valueOptions.Contains(args[i], StringComparer.OrdinalIgnoreCase);
            if (!isValueOption && !flagOptions.Contains(args[i], StringComparer.OrdinalIgnoreCase))
                optionErrors.Add($"Unknown option or stray argument '{args[i]}'.");
            else if (!seen.Add(args[i]))
                optionErrors.Add($"{args[i]} is given more than once.");
            if (isValueOption) i++; // its value is Text's to check
        }

        var uriA = Text("--a");
        var uriB = Text("--b");
        if (uriA is null || uriB is null)
        {
            foreach (var error in optionErrors) Console.Error.WriteLine(error);
            Console.Error.WriteLine("Both --a (adapter under test) and --b (peer on the same bus) are required.");
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var config = new ProbeConfig(
            UriA: uriA,
            UriB: uriB,
            Label: Text("--label") ?? Sanitize(uriA),
            UseFd: HasFlag(args, "--fd"),
            Bitrate: Whole("--bitrate", 500_000),
            DataBitrate: Whole("--dbit", 2_000_000),
            Frames: Whole("--frames", 5),
            WaitMs: Whole("--wait-ms", 1000),
            SkipClaim: HasFlag(args, "--skip-claim"));
        var modeNames = (Text("--modes") ?? "normal,echo")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(m => m.ToLowerInvariant())
            .ToArray();
        if (modeNames.Length == 0) optionErrors.Add("--modes needs at least one of: normal, echo.");
        optionErrors.AddRange(modeNames.Where(m => m is not ("normal" or "echo")).Select(m => $"--modes: unknown mode '{m}' (use normal and/or echo)."));
        var outDir = Text("--out") ?? Directory.GetCurrentDirectory();

        var invalid = optionErrors.Count > 0 ? string.Join(Environment.NewLine, optionErrors) : null;
        invalid ??= Validate(config);
        if (invalid is not null)
        {
            Console.Error.WriteLine(invalid);
            return 2;
        }

        var modes = modeNames;

        // Before anything is measured: a run that has measured everything must not be lost to a
        // directory that does not exist.
        // The label stays as typed in the report; as part of a file name it may not carry path
        // characters, and it is bounded so that the names stay within the file system's limits.
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var baseName = Path.Combine(outDir, $"echo-probe-{Sanitize(config.Label)}-{stamp}");
        try
        {
            Directory.CreateDirectory(outDir);
            // The two final files are created now, with a note that is replaced when the run is done:
            // an existing directory is not a writable one, and a name that is too long is found
            // here and not after the measurements.
            File.WriteAllText(baseName + ".md", "The run did not finish.");
            File.WriteAllText(baseName + ".json", "{}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"--out {outDir} cannot be used for {Path.GetFileName(baseName)}.md/.json: {ex.Message}");
            return 2;
        }

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

        var report = new Report(config, Environment.OSVersion.ToString(), results);
        File.WriteAllText(baseName + ".json", JsonSerializer.Serialize(report, JsonOptions));
        File.WriteAllText(baseName + ".md", RenderMarkdown(report));
        Console.WriteLine();
        Console.WriteLine($"Written: {baseName}.md and .json -- send the .md back (or paste it).");
        return 0;
    }

    private static string? Validate(ProbeConfig config)
    {
        // The counter in the payload is one byte, and a run without a frame measures nothing.
        if (config.Frames is < 1 or > 100) return "--frames must be between 1 and 100.";
        if (config.Bitrate <= 0) return "--bitrate must be positive.";
        if (config.UseFd && config.DataBitrate <= 0) return "--dbit must be positive.";
        if (config.WaitMs < 0) return "--wait-ms must not be negative.";
        return null;
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
        if (busB is null)
        {
            result.OpenError = errorB;
            return result;
        }

        using var busA = TryOpen(config.UriA, config, workMode, out var errorA);
        if (busA is null)
        {
            result.OpenError = errorA;
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
        void RecordA(object? sender, CanReceiveDataView e) { lock (onA) onA.Add(Describe(e, config, clock)); }
        void RecordB(object? sender, CanReceiveDataView e) { lock (onB) onB.Add(Describe(e, config, clock)); }
        busA.FrameObserved += RecordA;
        busB.FrameObserved += RecordB;
        var sendErrors = new List<string>();
        var sent = 0;
        try
        {
            for (var i = 0; i < config.Frames; i++)
            {
                try
                {
                    // A driver may reject a frame by returning zero instead of throwing; such a frame
                    // never entered the bus, and counting it as sent would pass off its missing
                    // echo as adapter behaviour.
                    var accepted = busA.Transmit(MakeFrame(config, i));
                    if (accepted > 0) sent++;
                    else sendErrors.Add($"frame {i}: the driver rejected it (Transmit returned {accepted})");
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
        lock (onA) a = onA.Where(s => s.IsProbeFrame).ToArray();
        lock (onB) b = onB.Where(s => s.IsProbeFrame).ToArray();
        return new RawResult
        {
            Sent = sent,
            Attempted = config.Frames,
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
        var name = ProbeName;
        var nameBytes = BitConverter.GetBytes(name.Value);
        if (!BitConverter.IsLittleEndian) Array.Reverse(nameBytes);

        // This probe's own claim: the Address Claimed PGN, from the address it claims, carrying its
        // NAME. Another ECU claiming on the same bus is not evidence about A.
        void CountClaims(object? sender, CanReceiveDataView e)
        {
            var frame = e.CanFrame;
            if (!frame.IsExtendedFrame) return;
            var fields = J1939Id.Decompose((uint)frame.ID);
            if (fields.Pgn == 0xEE00u && fields.SourceAddress == ClaimedAddress && frame.Data.Span.SequenceEqual(nameBytes))
                Interlocked.Increment(ref claimFramesOnPeer);
        }

        busB.FrameObserved += CountClaims;
        var watch = Stopwatch.StartNew();
        string state;
        string? error = null;
        byte? address = null;
        try
        {
            using var node = J1939Node.Open(busA, new J1939NodeOptions(ProbeName));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await node.ClaimAddressAsync(ClaimedAddress, timeout.Token).ConfigureAwait(false);
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

    private static Seen Describe(CanReceiveDataView e, ProbeConfig config, Stopwatch clock)
    {
        var frame = e.CanFrame;
        return new Seen(frame.ID, frame.IsExtendedFrame, e.IsEcho,
            frame.Data.Length > 0 ? frame.Data.Span[0] : -1, clock.ElapsedMilliseconds, IsProbeFrame(frame, config));
    }

    // The tool's own frame and nothing else on the bus: the id, the standard frame format, the frame
    // type and length it sent, and the payload pattern with a counter in range. Traffic of other
    // nodes on the same id is not a measurement.
    private static bool IsProbeFrame(CanFrameView frame, ProbeConfig config)
    {
        var length = config.UseFd ? 12 : 8;
        if (frame.ID != StdId || frame.IsExtendedFrame || frame.IsRemoteFrame || frame.Data.Length != length)
            return false;
        if (config.UseFd != (frame.FrameKind == CanFrameType.CanFd))
            return false;
        var data = frame.Data.Span;
        if (data[0] >= config.Frames) return false;
        for (var i = 1; i < length; i++)
        {
            if (data[i] != (byte)(0xA0 + i)) return false;
        }

        return true;
    }

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
                (a.Confirmed ? "confirmed" : $"NOT confirmed ({(a.Error ?? a.FailureReason).Replace("|", "/")})") +
                (a.IsApproximated ? " (approx)" : "") + $" {a.ElapsedMs} ms"));
            var claim = r.Claim is null
                ? "skipped"
                : $"{r.Claim.State} {r.Claim.Address ?? ""} ({r.Claim.ElapsedMs} ms, peer saw {r.Claim.ClaimFramesSeenByPeer})" +
                  (r.Claim.Error is null ? "" : $" error: {r.Claim.Error.Replace("|", "/")}");
            var mode = r.ActualWorkMode is { } actual && actual != r.WorkMode
                ? $"{r.WorkMode} (**adapter reports {actual}**)"
                : r.WorkMode;
            sb.AppendLine($"| {mode} | {r.DeclaresEchoFeature} | {raw.SeenOnAUnderTest}/{raw.Sent} | " +
                $"{raw.SeenOnAFlaggedAsEcho} | {raw.SeenOnAUnflagged} | {raw.SeenOnPeer}/{raw.Sent} | {confirmed} | {claim} |");
        }

        // What went wrong, in full: a missing driver or a rejected transmit must not read as a
        // measurement of the adapter.
        var problems = new List<string>();
        foreach (var r in report.Results)
        {
            if (r.OpenError is not null) problems.Add($"{r.WorkMode}: could not open: {r.OpenError}");
            if (r.ActualWorkMode is { } reported && reported != r.WorkMode)
                problems.Add($"{r.WorkMode}: requested, but the adapter reports work mode {reported}: this row is not a measurement of {r.WorkMode}");
            if (r.Raw is { } raw2)
            {
                if (raw2.Sent < raw2.Attempted)
                    problems.Add($"{r.WorkMode}: only {raw2.Sent} of {raw2.Attempted} frames were accepted by the driver");
                problems.AddRange(raw2.SendErrors.Select(e => $"{r.WorkMode}: send: {e}"));
            }

            if (r.Confirmed is { } c2)
                problems.AddRange(c2.Attempts.Where(a => !a.Confirmed).Select(a =>
                    $"{r.WorkMode}: SendConfirmedAsync not confirmed: reason {a.FailureReason}{(a.Error is null ? "" : ", " + a.Error)}"));
            if (r.Claim?.Error is { } claimError) problems.Add($"{r.WorkMode}: J1939 claim: {claimError}");
        }

        sb.AppendLine();
        sb.AppendLine("Problems reported while measuring (a missing driver or a rejected transmit is not adapter behaviour):");
        if (problems.Count == 0) sb.AppendLine("- none");
        foreach (var problem in problems) sb.AppendLine($"- {problem}");

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

    private static bool HasFlag(string[] args, string name)
        => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    private static string Sanitize(string uri)
    {
        var sb = new StringBuilder();
        foreach (var c in uri) sb.Append(char.IsLetterOrDigit(c) ? c : '-');
        var text = sb.ToString().Trim('-');
        return text.Length > 80 ? text[..80] : text;
    }

    private sealed record ProbeConfig(string UriA, string UriB, string Label, bool UseFd, int Bitrate,
        int DataBitrate, int Frames, int WaitMs, bool SkipClaim);

    private sealed record Report(ProbeConfig Config, string Os, List<ModeResult> Results);

    private sealed record Seen(int Id, bool Extended, bool IsEcho, int Counter, long AtMs, bool IsProbeFrame);

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
        public int Attempted { get; set; }
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
