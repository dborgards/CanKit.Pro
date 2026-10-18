using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Common.Definitions;

namespace CanKit.Pro.CANopen.Lss;

/// <summary>What one listen at a candidate bit rate showed (CiA 801 §5.2.3, Figure 1).</summary>
public enum LssBitRateProbeOutcome
{
    /// <summary>A frame was received without error. The candidate is the bit rate of the bus.</summary>
    Detected,

    /// <summary>
    /// The controller reported bus errors before any frame was received, or left the
    /// error-active state within the window. The candidate is not the bit rate of the bus.
    /// </summary>
    Rejected,

    /// <summary>
    /// Neither a frame nor an error arrived within the window. Either the bus was silent, or
    /// the adapter does not report error frames; the candidate is neither confirmed nor ruled out.
    /// </summary>
    Inconclusive,
}

/// <summary>One candidate bit rate <see cref="LssBitRateDetection.DetectAsync"/> listened at.</summary>
public sealed class LssBitRateProbe
{
    /// <summary>Constructs a probe record.</summary>
    public LssBitRateProbe(int bitsPerSecond, LssBitRateProbeOutcome outcome, int errorFrames, BusState busState)
    {
        BitsPerSecond = bitsPerSecond;
        Outcome = outcome;
        ErrorFrames = errorFrames;
        BusState = busState;
    }

    /// <summary>The candidate, in bits per second.</summary>
    public int BitsPerSecond { get; }

    /// <summary>What the listen showed.</summary>
    public LssBitRateProbeOutcome Outcome { get; }

    /// <summary>Error frames the controller reported while listening at this candidate.</summary>
    public int ErrorFrames { get; }

    /// <summary>
    /// <see cref="ICanBus.BusState"/> as the probe ended. <see cref="BusState.ErrActive"/>
    /// on a healthy controller; a degraded state is what rejects a candidate that drew no
    /// error frame.
    /// </summary>
    public BusState BusState { get; }
}

/// <summary>The result of <see cref="LssBitRateDetection.DetectAsync"/>.</summary>
public sealed class LssBitRateDetectionResult
{
    /// <summary>Constructs a result.</summary>
    public LssBitRateDetectionResult(int? bitsPerSecond, IReadOnlyList<LssBitRateProbe> probes)
    {
        if (probes is null) throw new ArgumentNullException(nameof(probes));
        if (bitsPerSecond is { } rate && rate <= 0)
            throw new ArgumentOutOfRangeException(nameof(bitsPerSecond), rate, "A detected bit rate is positive.");
        BitsPerSecond = bitsPerSecond;
        Probes = probes;
        TableIndex = bitsPerSecond is { } detected && LssBitTiming.TryGetCia301Index(detected, out var index)
            ? index
            : null;
    }

    /// <summary>
    /// The bit rate of the bus in bits per second, or <see langword="null"/> when no candidate
    /// received a frame within the passes the options allow.
    /// </summary>
    public int? BitsPerSecond { get; }

    /// <summary>Whether a bit rate was found.</summary>
    public bool Detected => BitsPerSecond is not null;

    /// <summary>
    /// The CiA 301 table index (<see cref="LssBitTiming"/>, table selector 0) of
    /// <see cref="BitsPerSecond"/>, when the table lists that rate. <see langword="null"/>
    /// otherwise, and for a rate that is not in the table.
    /// </summary>
    public byte? TableIndex { get; }

    /// <summary>Every candidate listened at, in the order it was tried, over every pass.</summary>
    public IReadOnlyList<LssBitRateProbe> Probes { get; }
}

/// <summary>How <see cref="LssBitRateDetection.DetectAsync"/> walks the bit-rate table.</summary>
public sealed class LssBitRateDetectionOptions
{
    /// <summary>
    /// The CiA 301 bit rates (CiA 801 Table 1), with 500, 250 and 125 kbit/s first: most
    /// networks use one of the three, so trying them first finds the rate in fewer frames
    /// (CiA 801 §6). The rest follow in table order.
    /// </summary>
    public static IReadOnlyList<int> DefaultCandidates { get; } = new[]
    {
        500_000, 250_000, 125_000, 1_000_000, 800_000, 50_000, 20_000, 10_000,
    };

    /// <summary>The listen window per candidate when none is chosen: 2 seconds.</summary>
    public static TimeSpan DefaultListenWindow { get; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The bit rates to try, in this order. Defaults to <see cref="DefaultCandidates"/>. Each
    /// must be positive and appear once.
    /// </summary>
    public IReadOnlyList<int> Candidates { get; init; } = DefaultCandidates;

    /// <summary>
    /// How long to listen at one candidate before moving on when neither a frame nor enough
    /// errors arrive. Defaults to <see cref="DefaultListenWindow"/>. The traffic on the bus
    /// decides what is long enough: a heartbeat producer at 1 s needs a window above 1 s.
    /// </summary>
    public TimeSpan ListenWindow { get; init; } = DefaultListenWindow;

    /// <summary>
    /// How many error frames at one candidate rule it out. Defaults to 2, the CiA 801 §5.2.3
    /// improvement over Figure 1: a single corrupted frame at the correct rate (§7) then does
    /// not move the search on. At a wrong rate every frame on the bus is an error, so the
    /// second one follows at once. Must be at least 1.
    /// </summary>
    public int ErrorFramesToReject { get; init; } = 2;

    /// <summary>
    /// How many times to walk <see cref="Candidates"/> before giving up without a result.
    /// Defaults to 1. Must be at least 1. Figure 1 starts over at the end of the table; a
    /// bus that is silent at the moment the correct rate is tried needs the second pass.
    /// </summary>
    public int Passes { get; init; } = 1;

    /// <summary>
    /// When <see langword="true"/> (the default), a bus the factory opens in a work mode other
    /// than <see cref="ChannelWorkMode.ListenOnly"/> fails the detection. CiA 801 §5.2.1
    /// requires the search not to disturb the network: a controller in normal mode at a wrong
    /// rate transmits error frames. Turn this off only on a bench where that is acceptable, or
    /// for an adapter that is silent in normal mode and does not report the listen-only mode.
    /// </summary>
    public bool RequireListenOnly { get; init; } = true;

    private static readonly TimeSpan MaxListenWindow = TimeSpan.FromMilliseconds(int.MaxValue);

    internal void Validate()
    {
        if (Candidates is null || Candidates.Count == 0)
            throw new ArgumentException("At least one candidate bit rate is required.", nameof(Candidates));
        if (Candidates.Any(rate => rate <= 0))
            throw new ArgumentOutOfRangeException(nameof(Candidates), "Every candidate bit rate is positive.");
        if (Candidates.Distinct().Count() != Candidates.Count)
            throw new ArgumentException("Each candidate bit rate appears once.", nameof(Candidates));
        if (ListenWindow <= TimeSpan.Zero || ListenWindow > MaxListenWindow)
        {
            throw new ArgumentOutOfRangeException(nameof(ListenWindow), ListenWindow,
                "The listen window must be positive and at most int.MaxValue milliseconds.");
        }
        if (ErrorFramesToReject < 1)
            throw new ArgumentOutOfRangeException(nameof(ErrorFramesToReject), ErrorFramesToReject, "Must be at least 1.");
        if (Passes < 1)
            throw new ArgumentOutOfRangeException(nameof(Passes), Passes, "Must be at least 1.");
    }
}

/// <summary>
/// Automatic bit-rate detection in listen-only mode (CiA 801 §5.2.3, Figure 1): the procedure
/// behind table 0 index 9 of CiA 305 (<see cref="LssBitTiming.AutomaticIndex"/>), FR-CO-050.
/// </summary>
/// <remarks>
/// <para>
/// For each candidate the caller's factory opens the controller at that rate, accepting every
/// identifier and in <see cref="ChannelWorkMode.ListenOnly"/>, so the search acknowledges
/// nothing and transmits no error frame (§5.2.1). The detection then waits for the first of:
/// a received frame, which the controller only delivers when the rate is right, so the
/// candidate is <see cref="LssBitRateProbeOutcome.Detected"/>; the number of error frames
/// <see cref="LssBitRateDetectionOptions.ErrorFramesToReject"/> names, which makes it
/// <see cref="LssBitRateProbeOutcome.Rejected"/>; or the end of the listen window. A candidate
/// whose window ends with the controller no longer error-active is rejected as well, since some
/// adapters move <see cref="ICanBus.BusState"/> without raising
/// <see cref="ICanBus.ErrorFrameReceived"/>; otherwise it is
/// <see cref="LssBitRateProbeOutcome.Inconclusive"/>. The bus of each probe is disposed before
/// the next is opened.
/// </para>
/// <para>
/// The method needs traffic (§6): at least one frame has to be on the bus while the correct
/// candidate is open. A CANopen manager that wants its devices to find the rate produces it
/// with its heartbeat, with SYNC, or with SDO reads of <c>1000h</c>, <c>1001h</c> or
/// <c>1018h</c>; the node's heartbeat and SYNC producers do that here. The number of frames
/// the search consumes depends on how many candidates are tried before the right one and on
/// the time the adapter needs to close and reopen between two candidates (Figure 2).
/// </para>
/// <para>
/// What CiA 801 describes and this type does not do: measuring the time of a single dominant
/// bit (§5.2.2) needs a timer on the receive line, which no adapter exposes; the external
/// AND/OR gate for controllers without a listen-only mode (§5.2.4) is hardware; and the
/// default fallback bit rate (§5.1) is the approach the report rejects.
/// </para>
/// <para>
/// A fault the bus reports (<see cref="ICanBus.FaultOccurred"/>) ends the detection with that
/// exception, after the bus is disposed: a dead adapter does not look like a silent bus.
/// </para>
/// </remarks>
public static class LssBitRateDetection
{
    /// <summary>
    /// Finds the bit rate of the bus by listening at each candidate in turn.
    /// </summary>
    /// <param name="openListenOnly">
    /// Opens the controller at the given bit rate in bits per second, in
    /// <see cref="ChannelWorkMode.ListenOnly"/>, accepting every identifier, with error
    /// reporting enabled where the adapter offers it. Called once per probe; the bus it returns
    /// is disposed when the probe ends. With CanKit.Core that is
    /// <c>rate => CanBus.Open(endpoint, cfg => cfg.Baud(rate).SetWorkMode(ChannelWorkMode.ListenOnly).EnableErrorInfo())</c>.
    /// </param>
    /// <param name="options">The candidates, the window, and the thresholds; <see langword="null"/>
    /// is a fresh <see cref="LssBitRateDetectionOptions"/>.</param>
    /// <param name="cancellationToken">Ends the detection with
    /// <see cref="OperationCanceledException"/>; the bus of the probe in progress is disposed.</param>
    /// <exception cref="InvalidOperationException">The factory returned <see langword="null"/>,
    /// a bus whose work mode is not listen-only while
    /// <see cref="LssBitRateDetectionOptions.RequireListenOnly"/> holds, or a bus whose
    /// nominal bit rate names a rate other than the candidate.</exception>
    public static Task<LssBitRateDetectionResult> DetectAsync(Func<int, ICanBus> openListenOnly,
        LssBitRateDetectionOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (openListenOnly is null) throw new ArgumentNullException(nameof(openListenOnly));
        var effective = options ?? new LssBitRateDetectionOptions();
        effective.Validate();
        return DetectCoreAsync(openListenOnly, effective, Task.Delay, cancellationToken);
    }

    /// <summary>
    /// The search. <paramref name="delay"/> is the listen window; a test passes one it ends
    /// itself. The options are validated by the caller.
    /// </summary>
    internal static async Task<LssBitRateDetectionResult> DetectCoreAsync(Func<int, ICanBus> openListenOnly,
        LssBitRateDetectionOptions options, Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken cancellationToken)
    {
        var probes = new List<LssBitRateProbe>();
        for (var pass = 0; pass < options.Passes; pass++)
        {
            foreach (var candidate in options.Candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var probe = await ProbeAsync(openListenOnly, candidate, options, delay, cancellationToken)
                    .ConfigureAwait(false);
                probes.Add(probe);
                if (probe.Outcome == LssBitRateProbeOutcome.Detected)
                    return new LssBitRateDetectionResult(candidate, probes);
            }
        }

        return new LssBitRateDetectionResult(null, probes);
    }

    private static async Task<LssBitRateProbe> ProbeAsync(Func<int, ICanBus> openListenOnly, int candidate,
        LssBitRateDetectionOptions options, Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken cancellationToken)
    {
        var bus = openListenOnly(candidate)
            ?? throw new InvalidOperationException($"The factory returned no bus for {candidate} bit/s.");
        using (bus)
        {
            CheckOpenedAsRequested(bus, candidate, options);

            var decided = new TaskCompletionSource<LssBitRateProbeOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            var errorFrames = 0;
            var threshold = options.ErrorFramesToReject;

            // The handlers run on the adapter's receive thread. They only complete the source;
            // the probe is finished on the awaiting side.
            EventHandler<CanReceiveDataView> onFrame = (_, e) =>
            {
                // Only a genuine received data frame confirms the rate. An echo is this host's own
                // transmission (the search sends nothing, so one from a double must not count), and
                // an error frame carried on the RX stream is a wrong-rate symptom, not a reception:
                // adapters surface error frames that way too (docs/getting-started.md subscribes to
                // e.Frame.IsErrorFrame), and at a wrong rate one could otherwise win the race and be
                // reported as the bus's rate. Rejection stays on ErrorFrameReceived alone, so an
                // error reported through both channels is not counted twice against the threshold.
                if (!e.IsEcho && !e.CanFrame.IsErrorFrame)
                    decided.TrySetResult(LssBitRateProbeOutcome.Detected);
            };
            EventHandler<ICanErrorInfo> onError = (_, _) =>
            {
                if (Interlocked.Increment(ref errorFrames) >= threshold)
                    decided.TrySetResult(LssBitRateProbeOutcome.Rejected);
            };
            EventHandler<Exception> onFault = (_, ex) => decided.TrySetException(ex);

            bus.FrameObserved += onFrame;
            // Some adapters refuse the error-frame subscription unless configured for it. The
            // window and the bus state still decide the probe then, as the remarks say.
            var errorSubscribed = TrySubscribe(() => bus.ErrorFrameReceived += onError);
            var faultSubscribed = TrySubscribe(() => bus.FaultOccurred += onFault);
            try
            {
                using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var elapsed = delay(options.ListenWindow, window.Token);
                await Task.WhenAny(decided.Task, elapsed).ConfigureAwait(false);

                // The probe's outcome is always the single value `decided` resolves to, never one
                // computed beside it: that is what closes the window-edge race. When the window
                // wins, the window's own outcome is offered to the same source, so a frame or a
                // fault that completed it first still wins (TrySetResult then no-ops), and a frame
                // arriving after is simply too late rather than silently dropped.
                if (!decided.Task.IsCompleted)
                {
                    // A window that ended because the caller cancelled surfaces that, not a result.
                    cancellationToken.ThrowIfCancellationRequested();
                    var state = bus.BusState;
                    var degraded = state is BusState.ErrWarning or BusState.ErrPassive or BusState.BusOff;
                    decided.TrySetResult(degraded ? LssBitRateProbeOutcome.Rejected : LssBitRateProbeOutcome.Inconclusive);
                }
                else
                {
                    // A result arrived before the window closed; release its timer.
                    window.Cancel();
                }

                var outcome = await decided.Task.ConfigureAwait(false);
                return new LssBitRateProbe(candidate, outcome, Volatile.Read(ref errorFrames), bus.BusState);
            }
            finally
            {
                bus.FrameObserved -= onFrame;
                if (errorSubscribed) TrySubscribe(() => bus.ErrorFrameReceived -= onError);
                if (faultSubscribed) TrySubscribe(() => bus.FaultOccurred -= onFault);
            }
        }
    }

    private static void CheckOpenedAsRequested(ICanBus bus, int candidate, LssBitRateDetectionOptions options)
    {
        var busOptions = bus.Options;
        if (options.RequireListenOnly && busOptions.WorkMode != ChannelWorkMode.ListenOnly)
        {
            throw new InvalidOperationException(
                $"The bus for {candidate} bit/s was opened in {busOptions.WorkMode} mode. CiA 801 §5.2.1 requires " +
                "listen-only: a controller in another mode disturbs the network at a wrong rate. Open the bus with " +
                "ChannelWorkMode.ListenOnly, or set RequireListenOnly to false where that is acceptable.");
        }

        // A timing given as a target rate names the rate the factory asked for; one given as
        // segments does not, and is not checked.
        var timing = busOptions.BitTiming;
        var nominal = timing.Classic?.Nominal.Bitrate ?? timing.Fd?.Nominal.Bitrate;
        if (nominal is { } opened && opened != (uint)candidate)
        {
            throw new InvalidOperationException(
                $"The bus for {candidate} bit/s reports a nominal bit rate of {opened} bit/s. The factory has to " +
                "open each probe at the candidate it is given, or the result names the wrong rate.");
        }
    }

    private static bool TrySubscribe(Action subscribe)
    {
        try
        {
            subscribe();
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }
}
