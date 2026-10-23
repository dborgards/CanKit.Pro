using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CanKit.Core.Exceptions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Lss;
using CanKit.Pro.CANopen.Sdo;

namespace CanKit.Pro.CanOpenConformance;

/// <summary>What a check concluded about the device.</summary>
internal enum Verdict
{
    /// <summary>The device did what CiA 301/305 (or the bound device description) says it must.</summary>
    Pass,

    /// <summary>It answered, and the answer contradicts the specification or the description.</summary>
    Fail,

    /// <summary>It was asked and nothing usable came back. Separate from <see cref="Fail"/>
    /// because something other than the device explains it just as well; which, if known, is in
    /// the finding's <see cref="Attribution"/>.</summary>
    Inconclusive,

    /// <summary>Not applicable: the object is optional and absent, or the device declares the
    /// service unsupported. Nothing was concluded and nothing is wrong.</summary>
    Skipped,

    /// <summary>Never attempted, because the run's group selection or safety tier excludes it.
    /// A run with many of these has simply not measured much, which is why the report counts them
    /// separately instead of folding them into the skips.</summary>
    NotRun,
}

/// <summary>
/// Where the fault lies when a check did not pass. This is the column a test report is read for:
/// "it does not work" is worth little next to "it does not work and here is whose problem it is".
/// Nothing infers it from context — each check says it, and <see cref="Checkbook.Classify"/>
/// assigns it for an exception nobody caught.
/// </summary>
internal enum Attribution
{
    /// <summary>Nothing is wrong (a pass), or nothing is claimed (a skip for an optional feature).</summary>
    None,

    /// <summary>The device under test: it answered wrongly, or did not answer something it must.</summary>
    Device,

    /// <summary>This runner or the CanKit.Pro stack beneath it: a local refusal, an unexpected
    /// exception, a request the API could not express. A finding here is a defect report against
    /// this repository, not against the device.</summary>
    Tool,

    /// <summary>The bus, the adapter or its vendor driver: bus-off, error-passive, a rejected
    /// transmit, a missing driver. Wiring, termination and bit rate land here.</summary>
    Transport,

    /// <summary>How the run itself was set up: no <c>--eds</c> where one is needed, a tier that
    /// forbids the check, a group left out, the wrong node-id. The operator can change this and
    /// re-run.</summary>
    Setup,

    /// <summary>Not attributable from what was observed. The report lists these as the work still
    /// to be done rather than pretending to a conclusion.</summary>
    Unknown,
}

/// <summary>How much a check is allowed to change on the device. A conformance run against a
/// commissioned machine must not reconfigure it by accident, so the depth is chosen on the command
/// line and every check declares the depth it needs.</summary>
internal enum Tier
{
    /// <summary>Reads and passive observation only. No SDO download, no NMT command, no LSS.</summary>
    Read = 0,

    /// <summary>Adds SDO downloads (restored to the value read first), NMT state commands, and
    /// SYNC/heartbeat production by this tool. Volatile: nothing is written to the device's
    /// non-volatile memory.</summary>
    Write = 1,

    /// <summary>Adds what outlives a power cycle or changes the device's identity on the bus: LSS
    /// configure node-id and bit timing, LSS store, 1010h store and 1011h restore.</summary>
    Commission = 2,
}

/// <summary>One check's outcome, as it goes into the report.</summary>
internal sealed record Finding(
    string Id,
    string Group,
    string Title,
    Tier Tier,
    string? Requirement,
    Verdict Verdict,
    Attribution Attribution,
    string Detail,
    double? ElapsedMs = null,
    IReadOnlyDictionary<string, string>? Measurements = null);

/// <summary>An error that occurred during the run, with where it occurred and whose it is. Kept
/// apart from the findings because the report has to be able to answer "which errors occurred"
/// without the reader reconstructing it from prose.</summary>
internal sealed record Incident(string Where, string Kind, string Message, Attribution Attribution);

/// <summary>
/// Runs the checks and collects their findings and incidents. Every check goes through
/// <see cref="RunAsync"/>, which is also what keeps one misbehaving device from ending the run: a
/// check that throws is recorded rather than propagated, because only the thing under test may
/// decide the exit code, and an unhandled exception would decide it for every check that had not
/// run yet.
/// </summary>
internal sealed class Checkbook
{
    private readonly List<Finding> _findings = new();
    private readonly List<Incident> _incidents = new();
    private readonly RunOptions _options;

    public Checkbook(RunOptions options) => _options = options;

    public IReadOnlyList<Finding> Findings => _findings;

    public IReadOnlyList<Incident> Incidents => _incidents;

    /// <summary>
    /// Whether the run was interrupted. Once it is, no further check is attempted: each records
    /// itself as not run. An earlier version let the cancellation surface through every remaining
    /// check's own catch, where it was classified as the device failing to answer — so one Ctrl+C
    /// produced a page of device findings about a device nobody had asked anything. Found in
    /// review of #388.
    /// </summary>
    public bool Interrupted { get; private set; }

    /// <summary>Whether any finding is a <see cref="Verdict.Fail"/>; the exit code follows this.</summary>
    public bool AnyFailure => _findings.Any(f => f.Verdict == Verdict.Fail);

    /// <summary>Whether the group was selected for this run.</summary>
    public bool Wants(string group) => _options.Groups.Contains(group, StringComparer.OrdinalIgnoreCase);

    /// <summary>Records an error that is not itself a check — a background exception from the
    /// stack, a bus-state change — so the report can list every error the run saw.</summary>
    public void Note(string where, Exception exception)
        => Note(where, exception.GetType().Name, exception.Message, Classify(exception));

    public void Note(string where, string kind, string message, Attribution attribution)
    {
        lock (_incidents) _incidents.Add(new Incident(where, kind, message, attribution));
    }

    /// <summary>
    /// Runs one check, unless the run excludes its group or its tier is deeper than the one the
    /// operator allowed; in either case the finding records that rather than leaving a gap, so
    /// that what was not measured is as visible in the report as what was.
    /// </summary>
    public async Task<Finding> RunAsync(
        string id, string group, string title, Tier tier, string? requirement, Func<Task<Outcome>> check)
    {
        if (!Wants(group))
        {
            return Record(new Finding(id, group, title, tier, requirement, Verdict.NotRun, Attribution.Setup,
                $"Group '{group}' is not in --groups."));
        }

        if (tier > _options.Tier)
        {
            return Record(new Finding(id, group, title, tier, requirement, Verdict.NotRun, Attribution.Setup,
                $"Needs --tier {tier.ToString().ToLowerInvariant()}; this run allows {_options.Tier.ToString().ToLowerInvariant()}."));
        }

        if (Interrupted)
        {
            return Record(new Finding(id, group, title, tier, requirement, Verdict.NotRun, Attribution.Setup,
                "The run was interrupted before this check started."));
        }

        var clock = Stopwatch.StartNew();
        Outcome outcome;
        try
        {
            outcome = await check().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The operator stopped the run. Nothing was measured and nothing about the device
            // follows, so this is not an inconclusive finding about it.
            Interrupted = true;
            clock.Stop();
            return Record(new Finding(id, group, title, tier, requirement, Verdict.NotRun, Attribution.Setup,
                "The run was interrupted while this check was running; whatever it had observed is not reported.",
                clock.Elapsed.TotalMilliseconds));
        }
        catch (Exception ex)
        {
            // A check's own failure is a measurement of the run, not a crash of the tool — but it
            // is also an error that occurred, so it goes in both places.
            var attribution = Classify(ex);
            Note($"check {id}", ex);
            outcome = new Outcome(Verdict.Inconclusive, attribution, Explain(ex, attribution));
        }

        clock.Stop();
        return Record(new Finding(id, group, title, tier, requirement, outcome.Verdict, outcome.Attribution,
            outcome.Detail, clock.Elapsed.TotalMilliseconds, outcome.Measurements));
    }

    /// <summary>
    /// Whose an exception is. The SDO abort needs both of its fields, not just the origin: an
    /// abort the peer sent is the device refusing, and a local one is only this repository's
    /// problem when the client raised it about itself. A local <see
    /// cref="SdoAbortCode.SdoProtocolTimedOut"/> is the client giving up on a server that said
    /// nothing — the device — and a local protocol abort is the client catching the server
    /// breaking the protocol, which is also the device. Attributing either to the runner sends
    /// the operator to the wrong repository; an empty-bus run did exactly that before this
    /// looked at the code.
    /// </summary>
    public static Attribution Classify(Exception exception) => exception switch
    {
        PeerSdoAccessException => Attribution.Setup,
        SdoAbortException { Origin: SdoAbortOrigin.Local } abort => LocalAbort(abort.AbortCode),
        SdoAbortException => Attribution.Device,
        LssTimeoutException => Attribution.Device,
        CanOpenTransportException => Attribution.Transport,
        // OperationCanceledException never reaches here from a check — RunAsync handles the
        // interruption itself — so this arm is for one wrapped in an incident note.
        OperationCanceledException => Attribution.Setup,
        TimeoutException => Attribution.Device,
        CanKitException => Attribution.Transport,
        _ => Attribution.Tool,
    };

    /// <summary>Whose a locally raised SDO abort is, by what the client aborted about.</summary>
    private static Attribution LocalAbort(uint code) => code switch
    {
        // The server did not answer in time. Nothing about the client is implicated.
        (uint)SdoAbortCode.SdoProtocolTimedOut => Attribution.Device,

        // The client aborted because the server broke the protocol — a toggle bit that did not
        // alternate, a sequence number out of order, a bad block CRC. The client is the detector
        // here, not the defect.
        (uint)SdoAbortCode.ToggleBitNotAlternated
            or (uint)SdoAbortCode.CommandSpecifierInvalid
            or (uint)SdoAbortCode.InvalidBlockSize
            or (uint)SdoAbortCode.InvalidSequenceNumber
            or (uint)SdoAbortCode.CrcError => Attribution.Device,

        // Everything else a client raises about itself: it ran out of room, or it refused a
        // transfer of its own accord.
        _ => Attribution.Tool,
    };

    /// <summary>The sentence a finding carries when an exception ended the check, written so the
    /// reader does not have to know the exception types to see whose problem it is.</summary>
    private static string Explain(Exception exception, Attribution attribution)
    {
        var head = $"{exception.GetType().Name}: {exception.Message}";
        if (exception is SdoAbortException { Origin: SdoAbortOrigin.Local, AbortCode: (uint)SdoAbortCode.SdoProtocolTimedOut } timedOut)
        {
            // The message reads "this node aborted", which is true and misleading: the client
            // aborted because the server said nothing. Say which it is.
            return $"The device did not answer the SDO transfer for 0x{timedOut.Index:X4}:{timedOut.Subindex:X2} "
                + "within the timeout, so this client aborted it. Nothing came back from the node — check that it is "
                + "powered, on this bus, at this node-id and at this bit rate.";
        }

        return attribution switch
        {
            Attribution.Device => $"{head} — the device did not answer as asked.",
            Attribution.Tool => $"{head} — this is a fault of the runner or the CanKit.Pro stack, not of the device; it belongs in an issue against this repository.",
            Attribution.Transport => $"{head} — the bus, the adapter or its driver, not the device.",
            Attribution.Setup => $"{head} — the run's own configuration; change it and run again.",
            _ => head,
        };
    }

    private Finding Record(Finding finding)
    {
        _findings.Add(finding);
        Print(finding);
        return finding;
    }

    private static void Print(Finding finding)
    {
        var mark = finding.Verdict switch
        {
            Verdict.Pass => "PASS",
            Verdict.Fail => "FAIL",
            Verdict.Inconclusive => "????",
            Verdict.Skipped => "skip",
            _ => " -- ",
        };
        var blame = finding.Verdict is Verdict.Pass or Verdict.NotRun || finding.Attribution == Attribution.None
            ? string.Empty
            : $" [{finding.Attribution.ToString().ToLowerInvariant()}]";
        var timing = finding.ElapsedMs is { } ms ? $" ({ms.ToString("F0", CultureInfo.InvariantCulture)} ms)" : string.Empty;
        Console.WriteLine($"  [{mark}]{blame} {finding.Id}  {finding.Title}{timing}");
        if (finding.Detail.Length > 0)
            Console.WriteLine($"          {finding.Detail}");
        if (finding.Measurements is null) return;
        foreach (var pair in finding.Measurements)
            Console.WriteLine($"          {pair.Key}: {pair.Value}");
    }
}

/// <summary>What a check body returns: the verdict, whose the fault is, why, and what it measured.</summary>
internal sealed record Outcome(
    Verdict Verdict,
    Attribution Attribution,
    string Detail,
    IReadOnlyDictionary<string, string>? Measurements = null)
{
    public static Outcome Pass(string detail, IReadOnlyDictionary<string, string>? measurements = null)
        => new(Verdict.Pass, Attribution.None, detail, measurements);

    /// <summary>The device answered and the answer is wrong. The default attribution of a failure:
    /// a check only concludes <see cref="Verdict.Fail"/> when it heard from the device.</summary>
    public static Outcome Fail(string detail, IReadOnlyDictionary<string, string>? measurements = null)
        => new(Verdict.Fail, Attribution.Device, detail, measurements);

    /// <summary>A failure that is this repository's, not the device's.</summary>
    public static Outcome ToolFault(string detail, IReadOnlyDictionary<string, string>? measurements = null)
        => new(Verdict.Fail, Attribution.Tool, detail, measurements);

    /// <summary>A failure of the bus, the adapter or its driver.</summary>
    public static Outcome TransportFault(string detail, IReadOnlyDictionary<string, string>? measurements = null)
        => new(Verdict.Fail, Attribution.Transport, detail, measurements);

    /// <summary>A failure whose attribution is none of the shorthands above — including
    /// <see cref="Attribution.Unknown"/>, for a contradiction that is real but does not say which
    /// side of it is wrong.</summary>
    public static Outcome Failed(Attribution attribution, string detail,
        IReadOnlyDictionary<string, string>? measurements = null)
        => new(Verdict.Fail, attribution, detail, measurements);

    /// <summary>Asked, nothing usable back, and <paramref name="attribution"/> says who is the
    /// likeliest reason — <see cref="Attribution.Unknown"/> when the observation does not say.</summary>
    public static Outcome Inconclusive(string detail, Attribution attribution = Attribution.Unknown,
        IReadOnlyDictionary<string, string>? measurements = null)
        => new(Verdict.Inconclusive, attribution, detail, measurements);

    /// <summary>The device does not implement an optional feature. Nothing is wrong.</summary>
    public static Outcome NotApplicable(string detail, IReadOnlyDictionary<string, string>? measurements = null)
        => new(Verdict.Skipped, Attribution.None, detail, measurements);

    /// <summary>The check needs something the run was not given — a device description, a deeper
    /// tier, a node-id. The operator can change it.</summary>
    public static Outcome NeedsSetup(string detail, IReadOnlyDictionary<string, string>? measurements = null)
        => new(Verdict.Skipped, Attribution.Setup, detail, measurements);
}
