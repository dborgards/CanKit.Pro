using System;
using System.Collections.Generic;
using System.Linq;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Pro.Actor;
using CanKit.Pro.RawCan;

namespace CanKit.Pro.CANopen.Safety;

/// <summary>Passive continuous SRDO/GFC analysis (FR-CO-061), independent of any CANopen node or object dictionary.</summary>
/// <remarks>
/// This monitor has only a receive subscription. It sends nothing, performs no SDO acquisition,
/// and never actuates process data. Polls are serialized with replacement and disposal.
/// <para>HostArrivalTimestamp must be captured before buffering, in the same process's Stopwatch
/// clock. Adapter timestamps are retained only as metadata. Poll drains captured frames before
/// advancing to the current clock, so UI scheduling does not change pair timing. Missing, late,
/// future or backward timestamps explicitly invalidate timing. Upstream driver latency and losses
/// are not measurable here. Results are diagnostics, never remote-failure confirmation or safety approval.</para>
/// <para>The input must have one exclusive reader for its entire lifetime. Ordered loss metadata
/// is used when available; legacy subscriptions remain of unknown capture quality. Even a
/// contiguous subscription cannot prove complete capture at the adapter or on the bus.</para>
/// </remarks>
public sealed class ForeignSrdoMonitor : IDisposable
{
    private static readonly IReadOnlyList<ForeignSrdoSignal> NoSignals = Array.Empty<ForeignSrdoSignal>();
    private readonly object _gate = new();
    private readonly ISubscription _subscription;
    private readonly ILossAwareSubscription? _lossAware;
    private readonly bool _ownsSubscription;
    private readonly ITimeSource _clock;
    private readonly string _bus;
    private Runtime[] _runtimes;
    private Dictionary<uint, Runtime> _routes;
    private ForeignSrdoCaptureQuality _traceQuality;
    private Guid _period = Guid.NewGuid();
    private ulong _expectedSequence = 1;
    private long _dropCount;
    private long _watermark;
    private long _configurationStart;
    private bool _disposed;

    /// <summary>Uses an existing, exclusively read subscription. Ownership is explicit; the
    /// caller must include all configured identifiers and 001h, including malformed candidates.</summary>
    public ForeignSrdoMonitor(ISubscription subscription, string busIdentity,
        IEnumerable<ForeignSrdoConfiguration> configurations, bool ownsSubscription = false)
        : this(subscription, busIdentity, configurations, ownsSubscription, MonotonicTimeSource.Instance) { }

    internal ForeignSrdoMonitor(ISubscription subscription, string busIdentity,
        IEnumerable<ForeignSrdoConfiguration> configurations, bool ownsSubscription, ITimeSource clock)
    {
        _subscription = subscription ?? throw new ArgumentNullException(nameof(subscription));
        if (string.IsNullOrWhiteSpace(busIdentity)) throw new ArgumentException("A capture bus identity is required.", nameof(busIdentity));
        _bus = busIdentity; _ownsSubscription = ownsSubscription; _clock = clock;
        _lossAware = subscription as ILossAwareSubscription;
        _traceQuality = _lossAware is null ? ForeignSrdoCaptureQuality.Unknown : ForeignSrdoCaptureQuality.Contiguous;
        _configurationStart = _watermark = clock.GetTimestamp();
        (_runtimes, _routes) = Prepare(configurations, _watermark);
    }

    /// <summary>Opens and owns a private subscription, including host echoes (the monitor has no
    /// transmissions of its own). Disposal closes only that subscription, never the shared service.</summary>
    public static ForeignSrdoMonitor Open(ICanBusService service, string busIdentity,
        IEnumerable<ForeignSrdoConfiguration> configurations, int? bufferCapacity = null)
    {
        if (service is null) throw new ArgumentNullException(nameof(service));
        var subscription = service.Subscribe(e => (uint)e.Frame.ID is 1 or >= 0x101 and <= 0x180,
            bufferCapacity, includeEcho: true);
        try { return new ForeignSrdoMonitor(subscription, busIdentity, configurations, ownsSubscription: true); }
        catch { subscription.Dispose(); throw; }
    }

    /// <summary>Atomically replaces prepared input and starts a fresh evaluation period. Queued
    /// SRDOs captured before replacement are reported and discarded; GFC trace items are retained.
    /// Invalid/ambiguous configurations throw before changing the running session.</summary>
    public void ReplaceConfigurations(IEnumerable<ForeignSrdoConfiguration> configurations)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            long now = _clock.GetTimestamp();
            var prepared = Prepare(configurations, now);
            (_runtimes, _routes) = prepared;
            _period = Guid.NewGuid();
            _configurationStart = _watermark = now;
        }
    }

    /// <summary>Drains at most maxFrames captured inputs, returning all observations directly.
    /// A full batch conservatively reports backlog and does not advance deadlines to wall time;
    /// poll again until HasBacklog is false. No callbacks run on the capture thread.</summary>
    public ForeignSrdoPollResult Poll(int maxFrames = 4096)
    {
        if (maxFrames < 1) throw new ArgumentOutOfRangeException(nameof(maxFrames));
        lock (_gate)
        {
            ThrowIfDisposed();
            var items = new List<ForeignSrdoObservation>();
            long before = _lossAware?.DroppedFrameCount ?? 0;
            bool counterChanged = before != _dropCount;
            if (counterChanged) Gap(items, _watermark);
            bool sequenceGap = false;
            int count = 0;
            while (count < maxFrames && TryRead(out var input, out var sequence))
            {
                count++;
                if (sequence.HasValue)
                {
                    if (sequence.Value != _expectedSequence)
                    {
                        sequenceGap = true;
                        Gap(items, _watermark);
                    }
                    _expectedSequence = unchecked(sequence.Value + 1);
                }
                Process(input, sequence, items);
            }
            bool backlog = count == maxFrames;
            long after = _lossAware?.DroppedFrameCount ?? 0;
            // A counter alone cannot locate an eviction relative to frames read concurrently.
            // Unless an ordered gap accounted for a stable count, discard any reconstructed pair.
            if (after != before || (counterChanged && !sequenceGap)) Gap(items, _watermark);
            _dropCount = after;
            if (!backlog) Advance(_clock.GetTimestamp(), items);
            return new ForeignSrdoPollResult(items.ToArray(), _runtimes.Select(rt => Snapshot(rt, !backlog)).ToArray(),
                backlog, _traceQuality, _lossAware is null ? null : after);
        }
    }

    /// <summary>Stops analysis and, if owned, disposes its subscription. Idempotent.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_ownsSubscription) _subscription.Dispose();
        }
    }

    private (Runtime[], Dictionary<uint, Runtime>) Prepare(IEnumerable<ForeignSrdoConfiguration> configurations, long now)
    {
        if (configurations is null) throw new ArgumentNullException(nameof(configurations));
        var runtimes = new List<Runtime>();
        var routes = new Dictionary<uint, Runtime>();
        var keys = new HashSet<(byte, int)>();
        foreach (var config in configurations)
        {
            if (config is null) throw new ArgumentException("A configuration cannot be null.", nameof(configurations));
            if (!keys.Add((config.PeerNodeId, config.Number)) || routes.ContainsKey(config.CobId1) || routes.ContainsKey(config.CobId2))
                throw new ArgumentException("Ambiguous SRDO association or overlapping COB-IDs on one bus.", nameof(configurations));
            var runtime = new Runtime(config, InitialQuality(), TickMath.DueAt(now, config.SafeguardCycleTime, _clock.Frequency));
            runtimes.Add(runtime); routes.Add(config.CobId1, runtime); routes.Add(config.CobId2, runtime);
        }
        return (runtimes.ToArray(), routes);
    }

    private bool TryRead(out CanFrameEvent input, out ulong? sequence)
    {
        if (_lossAware is not null)
        {
            bool found = _lossAware.TryReadWithSequence(out var envelope);
            input = envelope.FrameEvent; sequence = envelope.SequenceNumber;
            return found;
        }
        sequence = null;
        return _subscription.TryRead(out input);
    }

    private void Process(CanFrameEvent input, ulong? sequence, List<ForeignSrdoObservation> items)
    {
        var frame = input.Frame;
        uint id = (uint)frame.ID;
        bool gfc = id == 1;
        if (!gfc && !_routes.ContainsKey(id)) return;
        long timestamp = input.HostArrivalTimestamp;
        bool timing = timestamp > 0 && timestamp >= _watermark && timestamp <= _clock.GetTimestamp();
        bool classic = frame.FrameKind == CanFrameType.Can20 && !frame.IsExtendedFrame && !frame.IsRemoteFrame && !frame.IsErrorFrame && frame.Data.Length <= 8;
        if (gfc)
        {
            if (timing) Advance(timestamp, items);
            // GFC is independent of all SRDO state, local NMT and configuration count.
            items.Add(Item(classic && frame.Data.Length == 0 ? ForeignSrdoStatus.GfcObserved : ForeignSrdoStatus.MalformedGfc,
                timestamp, input.ReceiveTimestamp, sequence, null, _traceQuality, timing, NoSignals));
            return;
        }
        var rt = _routes[id];
        if (timestamp > 0 && timestamp < _configurationStart)
        {
            items.Add(Item(ForeignSrdoStatus.BeforeEvaluationPeriod, timestamp, input.ReceiveTimestamp,
                sequence, rt.Config, rt.Quality, false, NoSignals));
            return;
        }
        if (!timing)
        {
            _period = Guid.NewGuid();
            foreach (var affected in _runtimes)
            {
                Invalidate(affected, ForeignSrdoStatus.TimingUnavailable);
                affected.Sct = null; affected.Timing = false; affected.PairSeen = false;
            }
            items.Add(Item(ForeignSrdoStatus.TimingUnavailable, timestamp, input.ReceiveTimestamp,
                sequence, rt.Config, rt.Quality, false, NoSignals));
            return;
        }
        Advance(timestamp, items);
        if (!rt.Timing)
        {
            rt.Sct = TickMath.DueAt(timestamp, rt.Config.SafeguardCycleTime, _clock.Frequency);
            rt.Timing = true;
        }
        if (!classic) Invalidate(rt, ForeignSrdoStatus.InvalidFormat);
        else if (id == rt.Config.CobId1)
        {
            rt.Pending = frame.Data.ToArray();
            rt.Srvt = TickMath.DueAt(timestamp, rt.Config.ValidationTime, _clock.Frequency);
            rt.Signals = NoSignals;
            rt.Status = ForeignSrdoStatus.WaitingForInverted;
        }
        else if (rt.Pending is not { } normal)
        {
            if (!rt.PairSeen && rt.Status is ForeignSrdoStatus.NotReceived or ForeignSrdoStatus.StartupInverted
                or ForeignSrdoStatus.CaptureGap or ForeignSrdoStatus.TimingUnavailable)
                rt.Status = ForeignSrdoStatus.StartupInverted;
            else Invalidate(rt, ForeignSrdoStatus.OutOfOrder);
        }
        else
        {
            rt.PairSeen = true; rt.Pending = null; rt.Srvt = null;
            if (!SrdoFrames.IsInversePair(normal, frame.Data.ToArray())) Invalidate(rt, ForeignSrdoStatus.Mismatch);
            else if (normal.Length < rt.Config.TotalBytes) Invalidate(rt, ForeignSrdoStatus.ShortPayload);
            else
            {
                var signals = new List<ForeignSrdoSignal>();
                int offset = 0;
                foreach (var mapping in rt.Config.Mapping)
                {
                    var value = new byte[mapping.ByteLength];
                    Buffer.BlockCopy(normal, offset, value, 0, value.Length);
                    offset += value.Length;
                    signals.Add(new ForeignSrdoSignal(mapping.Index, mapping.Subindex, value));
                }
                rt.Signals = Array.AsReadOnly(signals.ToArray()); rt.Status = ForeignSrdoStatus.Valid;
                rt.LastValid = timestamp; rt.Quality = InitialQuality();
                rt.Sct = TickMath.DueAt(timestamp, rt.Config.SafeguardCycleTime, _clock.Frequency);
            }
        }
        items.Add(Item(rt.Status, timestamp, input.ReceiveTimestamp, sequence, rt.Config, rt.Quality, true, rt.Signals));
    }

    private void Advance(long target, List<ForeignSrdoObservation> items)
    {
        // Process the earliest deadline across all SRDOs; within one SRDO, SCT wins an exact
        // tie as the existing consumer arms SCT first. Expiry precedes a frame at the boundary.
        while (true)
        {
            Runtime? next = null;
            long due = long.MaxValue;
            bool sct = false;
            foreach (var rt in _runtimes)
            {
                if (rt.Sct is { } cycle && cycle <= target && (next is null || cycle < due))
                { next = rt; due = cycle; sct = true; }
                if (rt.Srvt is { } validation && validation <= target && (next is null || validation < due))
                { next = rt; due = validation; sct = false; }
            }
            if (next is null) break;
            if (sct) next.Sct = null;
            Invalidate(next, sct ? ForeignSrdoStatus.SafeguardCycleExpired : ForeignSrdoStatus.ValidationTimeExpired);
            items.Add(Item(next.Status, due, null, null, next.Config, next.Quality, next.Timing, NoSignals));
        }
        _watermark = Math.Max(_watermark, target);
    }

    private void Gap(List<ForeignSrdoObservation> items, long timestamp)
    {
        _traceQuality = ForeignSrdoCaptureQuality.Incomplete;
        _period = Guid.NewGuid();
        foreach (var rt in _runtimes)
        {
            Invalidate(rt, ForeignSrdoStatus.CaptureGap);
            rt.Quality = ForeignSrdoCaptureQuality.Incomplete;
            rt.PairSeen = false; rt.Sct = null; rt.Timing = false;
        }
        items.Add(Item(ForeignSrdoStatus.CaptureGap, timestamp, null, null, null, _traceQuality, false, NoSignals));
    }

    private static void Invalidate(Runtime rt, ForeignSrdoStatus reason)
    { rt.Pending = null; rt.Srvt = null; rt.Status = reason; rt.Signals = NoSignals; }

    private ForeignSrdoCaptureQuality InitialQuality() => _lossAware is null
        ? ForeignSrdoCaptureQuality.Unknown : ForeignSrdoCaptureQuality.Contiguous;

    private ForeignSrdoObservation Item(ForeignSrdoStatus status, long timestamp, TimeSpan? receive,
        ulong? sequence, ForeignSrdoConfiguration? config, ForeignSrdoCaptureQuality quality, bool timing,
        IReadOnlyList<ForeignSrdoSignal> signals)
        => new(_bus, status, timestamp, receive, sequence, config, _period, quality, timing, signals);

    private ForeignSrdoState Snapshot(Runtime rt, bool caughtUp)
        => new(rt.Config, _period, rt.Status, rt.Quality, _watermark, rt.LastValid,
            rt.LastValid is { } valid ? TickMath.ElapsedFromTicks(_watermark - valid, _clock.Frequency) : null,
            caughtUp && rt.Status == ForeignSrdoStatus.Valid ? rt.Signals : NoSignals, caughtUp);

    private void ThrowIfDisposed()
    { if (_disposed) throw new ObjectDisposedException(nameof(ForeignSrdoMonitor)); }

    private sealed class Runtime
    {
        internal Runtime(ForeignSrdoConfiguration config, ForeignSrdoCaptureQuality quality, long sct)
        { Config = config; Quality = quality; Sct = sct; }
        internal readonly ForeignSrdoConfiguration Config;
        internal ForeignSrdoStatus Status = ForeignSrdoStatus.NotReceived;
        internal ForeignSrdoCaptureQuality Quality;
        internal IReadOnlyList<ForeignSrdoSignal> Signals = NoSignals;
        internal byte[]? Pending;
        internal long? Sct;
        internal long? Srvt;
        internal long? LastValid;
        internal bool PairSeen;
        internal bool Timing = true;
    }
}
