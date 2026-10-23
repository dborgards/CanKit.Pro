using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Pro.RawCan;

namespace CanKit.Pro.CanOpenConformance;

/// <summary>One frame as the tap saw it, with the moment it arrived measured on one clock.</summary>
/// <summary>
/// The format travels with the identifier. A 29-bit PDO and unrelated 11-bit traffic can carry
/// the same number, and matching on the number alone let one stand in for the other — a silent
/// extended TPDO looked like it answered, and standard traffic was judged against the TPDO's
/// mapping. Found in review of #388.
/// </summary>
internal readonly record struct TappedFrame(uint CanId, bool IsExtended, byte[] Data, bool IsRemote, double AtMs);

/// <summary>
/// Records what the bus carried for a window, filtered to the COB-IDs a check cares about.
/// Measuring a period needs arrival times from one clock rather than the driver's timestamps,
/// which differ per adapter in resolution and epoch; what this measures is therefore the interval
/// as the host saw it, and every finding that reports a period says so.
/// </summary>
internal sealed class BusTap : IDisposable
{
    private readonly List<TappedFrame> _frames = new();
    private readonly long _startedAt = Stopwatch.GetTimestamp();
    private readonly IDisposable _subscription;

    public BusTap(ICanBusService service, Func<uint, bool, bool> wanted)
    {
        _subscription = service.Subscribe(
            onNext: e =>
            {
                var frame = e.Frame;
                // CanFrameView.ID is signed; a CAN id never is, and the COB-ID arithmetic in
                // CanOpenCobId is unsigned throughout.
                var canId = unchecked((uint)frame.ID);
                if (!wanted(canId, frame.IsExtendedFrame)) return;
                // Stamped when the frame reached the demultiplexer, not when this callback ran.
                // Subscribe delivers on its own asynchronous pump, so the callback's own clock
                // records when that pump happened to be scheduled — and on a busy host a frame
                // received *before* a check's boundary could be given an AtMs after it and be
                // charged to the device as traffic in the state it had just left. Every boundary
                // in this tool rests on AtMs, so this is the foundation under PDO-03's Stop
                // baseline, its Operational boundary and HBT-04's in-flight allowance.
                //
                // The > 0 guard and the fallback are the handling this repository already uses for
                // the same field (IsoTpChannel.cs): an event built outside the demux carries no
                // stamp, and treating a zero as an arrival would place it at the tap's own start.
                // Found in review of #388.
                var arrival = e.HostArrivalTimestamp > 0 ? e.HostArrivalTimestamp : Stopwatch.GetTimestamp();
                lock (_frames)
                {
                    _frames.Add(new TappedFrame(canId, frame.IsExtendedFrame, frame.Data.ToArray(), frame.IsRemoteFrame,
                        ToMs(arrival - _startedAt)));
                }
            },
            // Echoes of this tool's own transmits are not bus traffic from the device, and on an
            // adapter that flags them they would otherwise be counted as the device's answers.
            predicate: e => !e.IsEcho,
            bufferCapacity: 4096);
    }

    /// <summary>The tap's own clock, in the same units as <see cref="TappedFrame.AtMs"/>. A check
    /// that has to say "after this moment" reads it when the moment happens.</summary>
    public double ElapsedMs => ToMs(Stopwatch.GetTimestamp() - _startedAt);

    /// <summary>Stopwatch ticks as milliseconds, so the arrival stamps and <see cref="ElapsedMs"/>
    /// share one baseline — a boundary compared against a different clock is not a boundary.</summary>
    private static double ToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    /// <summary>Every frame seen so far, oldest first.</summary>
    public IReadOnlyList<TappedFrame> Frames
    {
        get { lock (_frames) return _frames.ToArray(); }
    }

    public IReadOnlyList<TappedFrame> WithId(uint canId, bool extended)
        => Frames.Where(f => f.CanId == canId && f.IsExtended == extended).ToArray();

    /// <summary>
    /// Waits out a window, then lets the delivery pump catch up before the caller looks.
    /// </summary>
    /// <remarks>
    /// The second part is not politeness. Frames are stamped at arrival now, so one that reached
    /// the demultiplexer inside the window carries an AtMs inside it — but if the pump has not
    /// delivered it yet, a snapshot taken the instant the delay returns leaves it out of the list
    /// altogether, and the check concludes over fewer frames than the window actually held. The
    /// drain waits for the list to stop growing, bounded, so a steady talker cannot hold a check
    /// open: it is one pump hop for frames already received in this process. Found in review
    /// of #388.
    /// </remarks>
    public async Task ListenAsync(int milliseconds, CancellationToken cancellationToken = default)
    {
        await Task.Delay(milliseconds, cancellationToken).ConfigureAwait(false);

        var settled = Stopwatch.StartNew();
        var seen = -1;
        while (settled.Elapsed.TotalMilliseconds < DrainMs)
        {
            int now;
            lock (_frames) now = _frames.Count;
            if (now == seen) return;
            seen = now;
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>How long <see cref="ListenAsync"/> lets the delivery pump catch up. What it covers
    /// is one hop for frames already received in this process, plus whatever the host's scheduler
    /// puts in front of it — not an allowance for the device being late.</summary>
    private const int DrainMs = 50;

    /// <summary>
    /// Waits until <paramref name="predicate"/> holds for the frames collected, or the deadline
    /// passes. Returns whether it held — a check reports the timeout itself, because "the device
    /// said nothing" is a result and not an error of the tool.
    /// </summary>
    public async Task<bool> WaitForAsync(Func<IReadOnlyList<TappedFrame>, bool> predicate, int timeoutMs, CancellationToken cancellationToken = default)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed.TotalMilliseconds < timeoutMs)
        {
            if (predicate(Frames)) return true;
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }

        return predicate(Frames);
    }

    public void Dispose() => _subscription.Dispose();

    /// <summary>
    /// The intervals between consecutive arrivals, and the robust statistics a report can stand
    /// behind on a loaded host: the median and the inter-quartile range. Deliberately not the mean
    /// and standard deviation — one descheduled thread moves both, and CLAUDE.md records what
    /// that costs.
    /// </summary>
    public static IntervalSummary? Summarise(IReadOnlyList<TappedFrame> frames)
    {
        if (frames.Count < 3) return null;
        var gaps = new double[frames.Count - 1];
        for (var i = 1; i < frames.Count; i++) gaps[i - 1] = frames[i].AtMs - frames[i - 1].AtMs;
        Array.Sort(gaps);
        return new IntervalSummary(frames.Count, Quantile(gaps, 0.5), Quantile(gaps, 0.25), Quantile(gaps, 0.75), gaps[0], gaps[^1]);
    }

    private static double Quantile(double[] sorted, double q)
    {
        if (sorted.Length == 1) return sorted[0];
        var position = q * (sorted.Length - 1);
        var low = (int)Math.Floor(position);
        var high = (int)Math.Ceiling(position);
        return low == high ? sorted[low] : sorted[low] + (position - low) * (sorted[high] - sorted[low]);
    }
}

/// <summary>Robust summary of the gaps between consecutive frames of one COB-ID, in milliseconds.</summary>
internal readonly record struct IntervalSummary(int Count, double MedianMs, double Q1Ms, double Q3Ms, double MinMs, double MaxMs)
{
    public double IqrMs => Q3Ms - Q1Ms;

    public override string ToString() =>
        string.Format(CultureInfo.InvariantCulture,
            "{0} frames, median {1:F1} ms, IQR {2:F1} ms, range {3:F1}..{4:F1} ms",
            Count, MedianMs, IqrMs, MinMs, MaxMs);
}
