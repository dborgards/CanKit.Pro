using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Pro.Actor;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Lss;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;

namespace CanKit.Pro.Tests.TestCases.CANopen.Lss;

/// <summary>
/// The LSS master and real <see cref="CanOpenNode"/> slaves on the virtual bus, with every timer on
/// one manual clock that only the test moves (#362). Response, identification and Fastscan windows,
/// the slave's two switch delays, heartbeats and SDO timeouts are all measured on it, and the bus
/// stamps frames on it too, so a slow host can delay a reply but never make it late.
/// </summary>
/// <remarks>
/// <para>
/// <b>When the clock moves.</b> Only while a test is waiting for something -- a driven master call,
/// <see cref="WaitUntilAsync"/>, <see cref="RunAsync{T}"/>, <see cref="AdvanceAsync"/> -- and then
/// only to the next armed timer, and only once the bench is settled: every frame a node has been
/// handed has gone through its reader to its actor, the actor has run what that posted, no send has
/// passed the node's transmit gate without coming back from the driver, no held frame is being
/// flushed, and no finished adapter switch is still on its way back to the actor
/// (<see cref="CanOpenNode.IsLssSettledForTests"/>). A master call in progress that has no timer
/// armed is about to send, so the clock waits for that too. Advancing and sending are serialized on
/// one gate, and the clock is moved only if no node has been handed a frame since the bench was
/// found settled, so nothing can be sent at one instant and acted on at a later one.
/// </para>
/// <para>
/// <b>What stays on the wall clock.</b> Nothing a protocol decision depends on. The wall clock only
/// bounds how long the bench waits for that settling: a test fails with
/// <see cref="TimeoutException"/> when nothing has moved -- no frame, no step of the clock -- for
/// <see cref="StallBudget"/>, which on a starved host is slow progress and not a hang only if
/// nothing at all happened for half a minute. A request that keeps the clock stepping without ever
/// being satisfied ends at <see cref="MaxVirtualRun"/> of virtual time instead.
/// </para>
/// <para>
/// <b>What a test must not do.</b> Start something time-dependent and carry on while the bench is
/// driving -- <see cref="Master"/> drives each call from the moment it is made. A call that has to
/// stay pending while the test acts in its window goes through <see cref="Undriven"/> and is awaited
/// with <see cref="RunAsync{T}"/>.
/// </para>
/// </remarks>
internal sealed class ClockedLssBench : IDisposable
{
    /// <summary>How long the bench may make no progress at all before a wait is reported as hung.</summary>
    public static readonly TimeSpan StallBudget = TimeSpan.FromSeconds(30);

    /// <summary>How much virtual time one wait may consume before it is reported as never satisfied.</summary>
    public static readonly TimeSpan MaxVirtualRun = TimeSpan.FromMinutes(10);

    private readonly ManualTimeSource _clock = new();
    private readonly ProtocolActor _masterTimers;
    private readonly object _sendGate = new();
    private readonly object _requestGate = new();
    private readonly List<Request> _requests = new();
    private readonly List<IDisposable> _disposables = new();
    private readonly List<TrackedNode> _nodes = new();
    private Task? _pump;
    private int _masterOps;
    private int _nextListener;
    private int _nextChannel = 100;
    private long _lastProgress = DateTime.UtcNow.Ticks;
    private int _disposed;

    public ClockedLssBench(string sessionPrefix, LssMasterOptions options)
    {
        // Zero is reserved for an absent transport stamp.
        _clock.Advance(TimeSpan.FromSeconds(1));
        Session = VirtualAdapterFixture.NewSession(sessionPrefix);
        _masterTimers = new ProtocolActor(ActorExecutionMode.DedicatedThread, null, _clock, null);
        options.Validate();
        var bus = Track(VirtualAdapterFixture.Open(Session, 0));
        var service = new GatedService(new CanBusService(bus, _clock.GetTimestamp), this, tracked: null);
        var master = Track(new LssMaster(service, options, ownsService: true, _masterTimers));
        Master = new ClockedMaster(this, master, drive: true);
        Undriven = new ClockedMaster(this, master, drive: false);
    }

    public string Session { get; }

    /// <summary>The master; each call drives the clock until it completes.</summary>
    public ILssMaster Master { get; }

    /// <summary>The same master, without driving: the clock stays where it is until the test moves it.</summary>
    public ILssMaster Undriven { get; }

    /// <summary>Virtual time since the bench was created (plus the one second reserved for stamps).</summary>
    public TimeSpan Now => TimeSpan.FromTicks(_clock.GetTimestamp());

    public CanOpenNode Slave(int channel, byte nodeId, LssSlaveOptions? lss = null, int srdoCount = 0,
        TimeSpan? sdoTimeout = null)
        => Node(channel, nodeId, new CanOpenNodeOptions
        {
            Lss = lss ?? new LssSlaveOptions(),
            SdoTimeout = sdoTimeout ?? TimeSpan.FromMilliseconds(250),
            SrdoCount = srdoCount,
        });

    public CanOpenNode Described(int channel, byte nodeId, CanOpenDeviceDescription description, LssSlaveOptions? lss = null)
        => Node(channel, nodeId, new CanOpenNodeOptions
        {
            Lss = lss,
            SdoTimeout = TimeSpan.FromMilliseconds(250),
        }, description: description);

    /// <summary>
    /// A node on this bench's clock. <paramref name="wrap"/> puts a test double between the node and
    /// the bus service; the bench still sees every frame the node is handed and every send it makes.
    /// </summary>
    public CanOpenNode Node(int channel, byte nodeId, CanOpenNodeOptions options,
        Func<ICanBusService, ICanBusService>? wrap = null, CanOpenDeviceDescription? description = null)
    {
        var bus = Track(VirtualAdapterFixture.Open(Session, channel));
        ICanBusService inner = new CanBusService(bus, _clock.GetTimestamp);
        if (wrap is not null) inner = wrap(inner);
        var tracked = new TrackedNode();
        var service = new GatedService(inner, this, tracked);
        var node = new CanOpenNode(service, nodeId, options, ownsService: true, _clock, description);
        tracked.Node = node;
        lock (_nodes) _nodes.Add(tracked);
        return Track(node);
    }

    /// <summary>
    /// Tells the bench that <paramref name="node"/> is held on purpose by the test while
    /// <paramref name="parked"/> is true -- a flush stopped in a test seam, say -- so the clock may
    /// move although the node still has LSS work in hand.
    /// </summary>
    public void Park(CanOpenNode node, Func<bool> parked)
    {
        lock (_nodes)
            _nodes.Single(tracked => ReferenceEquals(tracked.Node, node)).Parked = parked;
    }

    /// <summary>A channel number no other node of this bench uses.</summary>
    public int NextChannel() => Interlocked.Increment(ref _nextChannel);

    /// <summary>A passive observer of everything on the bus, recorded the moment it is transmitted.</summary>
    public Listener Listen()
        => Track(new Listener(Track(VirtualAdapterFixture.Open(Session, 20 + Interlocked.Increment(ref _nextListener)))));

    /// <summary>Drives the clock until <paramref name="condition"/> holds.</summary>
    public Task WaitUntilAsync(Func<bool> condition) => DriveAsync(condition, until: null);

    /// <summary>Drives the clock until <paramref name="operation"/> completes, then returns its result.</summary>
    public async Task<T> RunAsync<T>(Task<T> operation)
    {
        await DriveAsync(() => operation.IsCompleted, until: null).ConfigureAwait(false);
        return await operation.ConfigureAwait(false);
    }

    /// <inheritdoc cref="RunAsync{T}(Task{T})"/>
    public async Task RunAsync(Task operation)
    {
        await DriveAsync(() => operation.IsCompleted, until: null).ConfigureAwait(false);
        await operation.ConfigureAwait(false);
    }

    /// <summary>
    /// Moves the clock by exactly <paramref name="by"/>, stopping at every timer due on the way, and
    /// returns once the bench has settled at that instant. A test's "nothing happens for a while" is
    /// this, not a wall-clock delay: inside it nothing the bench controls can move by itself.
    /// </summary>
    public Task AdvanceAsync(TimeSpan by) => DriveAsync(condition: null, until: Now + by);

    /// <summary>Lets everything already handed to the nodes run, without moving the clock.</summary>
    public Task SettleAsync() => AdvanceAsync(TimeSpan.Zero);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        for (int i = _disposables.Count - 1; i >= 0; i--)
            _disposables[i].Dispose();
        _masterTimers.Dispose();
        lock (_requestGate)
        {
            foreach (var request in _requests)
                request.Done.TrySetException(new ObjectDisposedException(nameof(ClockedLssBench)));
            _requests.Clear();
        }
    }

    private T Track<T>(T item) where T : IDisposable
    {
        _disposables.Add(item);
        return item;
    }

    // ---------------------------------------------------------------------------------------------
    // The pump: one loop moves the clock for every waiter, so two waits never race each other.
    // ---------------------------------------------------------------------------------------------

    private Task DriveAsync(Func<bool>? condition, TimeSpan? until)
    {
        var request = new Request(condition, until, Now);
        lock (_requestGate)
        {
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(ClockedLssBench));
            _requests.Add(request);
            MarkProgress();
            _pump ??= Task.Run(PumpAsync);
        }
        return request.Done.Task;
    }

    private sealed class Request
    {
        public Request(Func<bool>? condition, TimeSpan? until, TimeSpan started)
        {
            Condition = condition;
            Until = until;
            Started = started;
        }

        public Func<bool>? Condition { get; }
        public TimeSpan? Until { get; }
        public TimeSpan Started { get; }
        public bool SettledAtUntil { get; set; }

        // Conditions are evaluated on the thread pool, never on the pump: a condition may block
        // (LssState() posts to the node's actor and waits), and a blocked pump could not report
        // the stall (Codex on #387). Only the pump reads and writes these.
        public Task<bool>? Probe { get; set; }
        public bool Seen { get; set; }
        public Exception? Failed { get; set; }

        public TaskCompletionSource<bool> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Takes the result of a finished probe, and starts the next one when none is running.</summary>
    private static void Observe(Request request)
    {
        if (request.Until is not null || request.Seen || request.Failed is not null) return;
        if (request.Probe is not { } probe)
        {
            request.Probe = Task.Run(request.Condition!);
            return;
        }
        if (!probe.IsCompleted) return;
        request.Probe = null;
        if (probe.Status == TaskStatus.RanToCompletion) request.Seen = probe.Result;
        else request.Failed = probe.Exception?.GetBaseException() ?? new TaskCanceledException(probe);
    }

    /// <summary>
    /// Evaluates every waiting condition afresh -- started after everything the caller has seen,
    /// so a false answer is about the bench as it is now -- and waits for the answers.
    /// </summary>
    private async Task EvaluateAsync()
    {
        Request[] waiting;
        lock (_requestGate) waiting = _requests.Where(request => request.Until is null).ToArray();
        foreach (var request in waiting)
        {
            // A probe started earlier answers about an earlier bench; let it finish, then ask again.
            while (request.Probe is { IsCompleted: false }) await IdleAsync().ConfigureAwait(false);
            Observe(request);
            Observe(request);
            while (request.Probe is { IsCompleted: false }) await IdleAsync().ConfigureAwait(false);
            Observe(request);
        }
    }

    /// <summary>Completes every satisfied request. False, with the pump retired, when none is left.</summary>
    private bool ReleaseSatisfied()
    {
        lock (_requestGate)
        {
            for (int i = _requests.Count - 1; i >= 0; i--)
            {
                var request = _requests[i];
                if (request.Failed is { } failure)
                {
                    _requests.RemoveAt(i);
                    request.Done.TrySetException(failure);
                }
                else if (request.Until is { } until ? Now >= until && request.SettledAtUntil : request.Seen)
                {
                    _requests.RemoveAt(i);
                    request.Done.TrySetResult(true);
                    MarkProgress();
                }
                else if (Now - request.Started > MaxVirtualRun)
                {
                    _requests.RemoveAt(i);
                    request.Done.TrySetException(new TimeoutException(
                        $"The bench advanced {MaxVirtualRun} of virtual time and the wait was still not satisfied."));
                }
            }
            if (_requests.Count != 0) return true;
            _pump = null;
            return false;
        }
    }

    /// <summary>Whether a waiter's condition has been seen to hold (or to throw). Never blocks.</summary>
    private bool AnyConditionSatisfied()
    {
        Request[] waiting;
        lock (_requestGate) waiting = _requests.Where(request => request.Until is null).ToArray();
        foreach (var request in waiting) Observe(request);
        return waiting.Any(request => request.Seen || request.Failed is not null);
    }

    private async Task PumpAsync()
    {
        try
        {
            while (true)
            {
                await EvaluateAsync().ConfigureAwait(false);
                if (!ReleaseSatisfied()) return;
                var settled = await SettleCoreAsync().ConfigureAwait(false);
                if (settled is not { } version) continue;

                lock (_requestGate)
                {
                    foreach (var request in _requests.Where(request => request.Until is { } at && Now >= at))
                        request.SettledAtUntil = true;
                }
                // Once more after settling: a condition the settled bench meets must not see the
                // clock move first.
                await EvaluateAsync().ConfigureAwait(false);
                if (!ReleaseSatisfied()) return;

                var due = await NextDueAsync().ConfigureAwait(false);
                if (due is not { } next) continue;
                TimeSpan? cap;
                lock (_requestGate)
                {
                    cap = _requests.Where(request => request.Until is not null)
                        .Select(request => (TimeSpan?)(request.Until!.Value - Now))
                        .Min();
                }
                if (cap is { } limit && (next.Delay is null || limit < next.Delay)) next = (limit, next.MasterArmed);
                if (next.Delay is not { } step || (Volatile.Read(ref _masterOps) > 0 && !next.MasterArmed))
                {
                    // Nothing to wait for on the clock, or a master call about to send: let it.
                    await IdleAsync().ConfigureAwait(false);
                    continue;
                }

                lock (_sendGate)
                {
                    // A frame handed to a node since the bench was found settled may still be on
                    // its way to the actor; moving the clock now would let it act late.
                    if (Version() != version) continue;
                    _clock.Advance(step);
                }
                if (step > TimeSpan.Zero) MarkProgress();
                await PokeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The stall detector, a disposed bench or a broken actor: every waiter gets the reason.
            lock (_requestGate)
            {
                foreach (var request in _requests)
                    request.Done.TrySetException(ex);
                _requests.Clear();
                _pump = null;
            }
        }
    }

    private long Version()
    {
        long version = 0;
        lock (_nodes)
            foreach (var node in _nodes)
                version += Interlocked.Read(ref node.Matched);
        return version;
    }

    private TrackedNode[] Nodes()
    {
        lock (_nodes) return _nodes.ToArray();
    }

    /// <summary>
    /// Waits until every node has acted on everything it was handed and has nothing in flight, and
    /// returns the bench's frame count at that point. Null when a condition was met meanwhile.
    /// </summary>
    private async Task<long?> SettleCoreAsync()
    {
        while (true)
        {
            long before = Version();
            foreach (var node in Nodes().Where(node => !node.Gone))
            {
                if (!await WaitForAsync(() => node.Gone || Interlocked.Read(ref node.Matched) == Interlocked.Read(ref node.Handled))
                        .ConfigureAwait(false))
                    return null;
                // Twice: the second post is drained only after the iteration that ran the first
                // has fired its timers (VirtualClock remarks).
                for (int i = 0; i < 2 && !node.Gone; i++)
                    if (!await BarrierAsync(node).ConfigureAwait(false)) return null;
                if (!await WaitForAsync(() => node.Gone || node.Node!.IsLssSettledForTests || node.Parked()).ConfigureAwait(false))
                    return null;
            }
            long after = Version();
            if (after == before) return after;
            MarkProgress();
        }
    }

    private async Task<bool> BarrierAsync(TrackedNode node)
    {
        Task barrier;
        try
        {
            barrier = node.Node!.ActorForTests.PostAsync(() => { });
        }
        catch (ObjectDisposedException)
        {
            node.Gone = true;
            return true;
        }
        if (!await WaitForAsync(() => barrier.IsCompleted).ConfigureAwait(false)) return false;
        if (barrier.IsFaulted || barrier.IsCanceled) node.Gone = true;
        return true;
    }

    /// <summary>The earliest armed timer of the master and every node, and whether the master has one.</summary>
    private async Task<(TimeSpan? Delay, bool MasterArmed)?> NextDueAsync()
    {
        var master = await AskAsync(_masterTimers).ConfigureAwait(false);
        if (master.Aborted) return null;
        TimeSpan? earliest = master.Delay;
        foreach (var node in Nodes().Where(node => !node.Gone))
        {
            var next = await AskAsync(node.Node!.ActorForTests).ConfigureAwait(false);
            if (next.Aborted) return null;
            if (next.Delay is { } delay && (earliest is null || delay < earliest)) earliest = delay;
        }
        return (earliest, master.Delay is not null);
    }

    private async Task<(bool Aborted, TimeSpan? Delay)> AskAsync(ProtocolActor actor)
    {
        Task<TimeSpan?> asked;
        try
        {
            asked = actor.NextTimerDelayAsync();
        }
        catch (ObjectDisposedException)
        {
            return (false, null);
        }
        if (!await WaitForAsync(() => asked.IsCompleted).ConfigureAwait(false)) return (true, null);
        if (asked.Status != TaskStatus.RanToCompletion) return (false, null);
        var delay = asked.Result;
        return (false, delay is { } d && d < TimeSpan.Zero ? TimeSpan.Zero : delay);
    }

    /// <summary>Wakes every actor after a move of the clock, so each fires what became due.</summary>
    private async Task PokeAsync()
    {
        for (int round = 0; round < 2; round++)
        {
            if (!await PokeOneAsync(_masterTimers).ConfigureAwait(false)) return;
            foreach (var node in Nodes())
                if (!node.Gone && !await PokeOneAsync(node.Node!.ActorForTests).ConfigureAwait(false)) return;
        }
    }

    private async Task<bool> PokeOneAsync(ProtocolActor actor)
    {
        Task poked;
        try
        {
            poked = actor.PostAsync(() => { });
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
        return await WaitForAsync(() => poked.IsCompleted).ConfigureAwait(false);
    }

    /// <summary>
    /// Polls <paramref name="condition"/>. False when a waiter's own condition was met first -- an
    /// actor can be held on purpose by the test, and the wait has to let go of it then.
    /// </summary>
    private async Task<bool> WaitForAsync(Func<bool> condition)
    {
        while (!condition())
        {
            if (AnyConditionSatisfied()) return false;
            await IdleAsync().ConfigureAwait(false);
        }
        return true;
    }

    private async Task IdleAsync()
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(ClockedLssBench));
        if (DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastProgress) > StallBudget.Ticks)
            throw new TimeoutException(
                $"The LSS bench made no progress for {StallBudget}: no frame reached a node and the clock did not move.");
        await Task.Delay(1).ConfigureAwait(false);
    }

    private void MarkProgress() => Interlocked.Exchange(ref _lastProgress, DateTime.UtcNow.Ticks);

    // ---------------------------------------------------------------------------------------------
    // Instrumentation of the transport
    // ---------------------------------------------------------------------------------------------

    private sealed class TrackedNode
    {
        public long Matched;
        public long Handled;
        public CanOpenNode? Node;
        public Func<bool> Parked = static () => false;
        private volatile bool _gone;

        public bool Gone
        {
            get => _gone || Node is null;
            set => _gone = value;
        }
    }

    /// <summary>
    /// Every send of the master and of the nodes goes through the bench's send gate, which the clock
    /// is moved under. A node's service also counts what the node is handed and what its reader has
    /// passed on to the actor.
    /// </summary>
    private sealed class GatedService : ICanBusService
    {
        private readonly ICanBusService _inner;
        private readonly ClockedLssBench _bench;
        private readonly TrackedNode? _tracked;

        public GatedService(ICanBusService inner, ClockedLssBench bench, TrackedNode? tracked)
        {
            _inner = inner;
            _bench = bench;
            _tracked = tracked;
        }

        public ICanBus Bus => _inner.Bus;
        public int SubscriptionCount => _inner.SubscriptionCount;

        public event EventHandler<Exception>? BackgroundExceptionOccurred
        {
            add => _inner.BackgroundExceptionOccurred += value;
            remove => _inner.BackgroundExceptionOccurred -= value;
        }

        public ISubscription Subscribe(Func<CanFrameEvent, bool>? predicate = null, int? bufferCapacity = null,
            bool includeEcho = false)
        {
            if (_tracked is not { } tracked) return _inner.Subscribe(predicate, bufferCapacity, includeEcho);
            var inner = _inner.Subscribe(frame =>
            {
                if (predicate is not null && !predicate(frame)) return false;
                Interlocked.Increment(ref tracked.Matched);
                return true;
            }, bufferCapacity, includeEcho);
            return new CountingSubscription(inner, tracked);
        }

        public ISubscription Subscribe(CanIdFilter filter, int? bufferCapacity = null, bool includeEcho = false)
        {
            if (_tracked is not null)
                throw new NotSupportedException("The bench counts frames through a predicate subscription.");
            return _inner.Subscribe(filter, bufferCapacity, includeEcho);
        }

        public IReadOnlyList<FilterOverlap> FindOverlappingFilterSubscriptions() => _inner.FindOverlappingFilterSubscriptions();

        public Task<TxConfirmation> SendConfirmedAsync(CanFrame frame, TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            lock (_bench._sendGate) return _inner.SendConfirmedAsync(frame, timeout, cancellationToken);
        }

        public void Dispose()
        {
            if (_tracked is not null) _tracked.Gone = true;
            _inner.Dispose();
        }
    }

    private sealed class CountingSubscription : ISubscription
    {
        private readonly ISubscription _inner;
        private readonly TrackedNode _tracked;

        public CountingSubscription(ISubscription inner, TrackedNode tracked)
        {
            _inner = inner;
            _tracked = tracked;
        }

        public IAsyncEnumerable<CanFrameEvent> Frames => ReadAsync();

        public bool TryRead(out CanFrameEvent frameEvent)
        {
            if (!_inner.TryRead(out frameEvent)) return false;
            Interlocked.Increment(ref _tracked.Handled);
            return true;
        }

        public ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
            => _inner.WaitToReadAsync(cancellationToken);

        public void Reconfigure(CanIdFilter filter) => throw new NotSupportedException();

        public void Reconfigure(Func<CanFrameEvent, bool>? predicate) => throw new NotSupportedException();

        public void Dispose()
        {
            _tracked.Gone = true;
            _inner.Dispose();
        }

        private async IAsyncEnumerable<CanFrameEvent> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var frame in _inner.Frames.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                yield return frame;
                // The consumer asks for the next frame only after it has posted this one to its actor.
                Interlocked.Increment(ref _tracked.Handled);
            }
        }
    }

    /// <summary>Counts master calls in progress; with <c>drive</c>, each call drives the clock until it completes.</summary>
    private sealed class ClockedMaster : ILssMaster
    {
        private readonly ClockedLssBench _bench;
        private readonly ILssMaster _inner;
        private readonly bool _drive;

        public ClockedMaster(ClockedLssBench bench, ILssMaster inner, bool drive)
        {
            _bench = bench;
            _inner = inner;
            _drive = drive;
        }

        public LssMasterOptions Options => _inner.Options;

        private Task<T> Call<T>(Func<Task<T>> start)
        {
            Interlocked.Increment(ref _bench._masterOps);
            Task<T> operation;
            try
            {
                operation = start();
            }
            catch
            {
                Interlocked.Decrement(ref _bench._masterOps);
                throw;
            }
            _ = operation.ContinueWith(static (_, state) => Interlocked.Decrement(ref ((ClockedLssBench)state!)._masterOps),
                _bench, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return _drive ? _bench.RunAsync(operation) : operation;
        }

        // start() runs inside Call<T>, after the call is counted, so an argument error is still
        // thrown by the call itself rather than stored in the task.
        private Task Call(Func<Task> start) => Call(() => AsBool(start()));

        private static async Task<bool> AsBool(Task operation)
        {
            await operation.ConfigureAwait(false);
            return true;
        }

        public Task SwitchStateGlobalAsync(LssState state, CancellationToken cancellationToken = default)
            => Call(() => _inner.SwitchStateGlobalAsync(state, cancellationToken));

        public Task<bool> SwitchStateSelectiveAsync(LssAddress address, CancellationToken cancellationToken = default)
            => Call(() => _inner.SwitchStateSelectiveAsync(address, cancellationToken));

        public Task<LssServiceResult> ConfigureNodeIdAsync(byte nodeId, CancellationToken cancellationToken = default)
            => Call(() => _inner.ConfigureNodeIdAsync(nodeId, cancellationToken));

        public Task<LssServiceResult> ConfigureBitTimingAsync(byte tableSelector, byte tableIndex,
            CancellationToken cancellationToken = default)
            => Call(() => _inner.ConfigureBitTimingAsync(tableSelector, tableIndex, cancellationToken));

        public Task ActivateBitTimingAsync(ushort switchDelayMilliseconds, int? bitsPerSecond = null,
            bool returnToWaiting = false, CancellationToken cancellationToken = default)
            => Call(() => _inner.ActivateBitTimingAsync(switchDelayMilliseconds, bitsPerSecond, returnToWaiting, cancellationToken));

        public Task<LssServiceResult> StoreConfigurationAsync(CancellationToken cancellationToken = default)
            => Call(() => _inner.StoreConfigurationAsync(cancellationToken));

        public Task<uint> InquireVendorIdAsync(CancellationToken cancellationToken = default)
            => Call(() => _inner.InquireVendorIdAsync(cancellationToken));

        public Task<uint> InquireProductCodeAsync(CancellationToken cancellationToken = default)
            => Call(() => _inner.InquireProductCodeAsync(cancellationToken));

        public Task<uint> InquireRevisionNumberAsync(CancellationToken cancellationToken = default)
            => Call(() => _inner.InquireRevisionNumberAsync(cancellationToken));

        public Task<uint> InquireSerialNumberAsync(CancellationToken cancellationToken = default)
            => Call(() => _inner.InquireSerialNumberAsync(cancellationToken));

        public Task<LssAddress> InquireAddressAsync(CancellationToken cancellationToken = default)
            => Call(() => _inner.InquireAddressAsync(cancellationToken));

        public Task<byte> InquireNodeIdAsync(CancellationToken cancellationToken = default)
            => Call(() => _inner.InquireNodeIdAsync(cancellationToken));

        public Task<bool> IdentifyRemoteSlavesAsync(LssIdentityRange range, CancellationToken cancellationToken = default)
            => Call(() => _inner.IdentifyRemoteSlavesAsync(range, cancellationToken));

        public Task<bool> IdentifyNonConfiguredSlavesAsync(CancellationToken cancellationToken = default)
            => Call(() => _inner.IdentifyNonConfiguredSlavesAsync(cancellationToken));

        public Task<LssFastscanResult> FastscanAsync(LssFastscanRequest? request = null,
            CancellationToken cancellationToken = default)
            => Call(() => _inner.FastscanAsync(request, cancellationToken));

        public Task<byte[]?> TransceiveManufacturerAsync(byte commandSpecifier, ReadOnlyMemory<byte> parameters,
            CancellationToken cancellationToken = default)
            => Call(() => _inner.TransceiveManufacturerAsync(commandSpecifier, parameters, cancellationToken));

        public void Dispose() => _inner.Dispose();

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    /// <summary>Records every frame on the bus as it is transmitted -- synchronously, so a frame the
    /// bench has seen sent is already here.</summary>
    internal sealed class Listener : IDisposable
    {
        private readonly ICanBus _bus;
        private readonly List<uint> _ids = new();
        private readonly List<byte> _lssReplies = new();

        public Listener(ICanBus bus)
        {
            _bus = bus;
            _bus.FrameObserved += OnFrame;
        }

        public IReadOnlyList<uint> Ids
        {
            get { lock (_ids) return _ids.ToArray(); }
        }

        /// <summary>The command specifier of every slave reply (7E4h) heard, in order.</summary>
        public IReadOnlyList<byte> LssReplies
        {
            get { lock (_ids) return _lssReplies.ToArray(); }
        }

        public void Dispose() => _bus.FrameObserved -= OnFrame;

        private void OnFrame(object? sender, CanReceiveDataView e)
        {
            var frame = e.CanFrame;
            lock (_ids)
            {
                _ids.Add((uint)frame.ID);
                if ((uint)frame.ID == CanOpenCobId.LssSlaveMessage && frame.Data.Length > 0)
                    _lssReplies.Add(frame.Data.Span[0]);
            }
        }
    }
}
