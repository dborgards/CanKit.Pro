using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Pro.Actor;
using CanKit.Pro.RawCan;
using CanKit.Pro.Reliability;

namespace CanKit.Pro.IsoTp;

/// <summary>
/// Actor-driven <see cref="IIsoTpChannel"/> that composes on top of the CanKit.Pro L2 services:
/// <see cref="ICanBusService"/> for RX demux and TX-confirm, <see cref="IProtocolActor"/> for
/// single-writer state, <see cref="DeadlineScheduler"/> for N_As/N_Bs/N_Cr timers.
/// </summary>
/// <remarks>
/// <para>Threading model (SRS FR-TP-016, FR-RAW-020..023):</para>
/// <list type="bullet">
/// <item><description>Every protocol-state field (TX operation, RX buffer, sequence numbers,
/// block counters, deadlines) lives inside the actor and is only ever read/written on the
/// actor's loop thread — no locks needed inside the state machines.</description></item>
/// <item><description>RX frames from the demux subscription are marshaled onto the actor via
/// <see cref="IProtocolActor.Post(Action)"/>; TX confirmations from
/// <see cref="ICanBusService.SendConfirmedAsync"/> run on the thread-pool and post their outcome back
/// onto the actor.</description></item>
/// <item><description>The subscription reader is a single <see cref="Task"/> that ends when the
/// subscription completes (i.e. the channel or the owning service is disposed) — no busy loop
/// (FR-RAW-022).</description></item>
/// <item><description>Any exception raised by an event handler or a scheduled callback is
/// surfaced via <see cref="BackgroundExceptionOccurred"/> (FR-RAW-023).</description></item>
/// </list>
/// <para>The channel does not construct <see cref="CanFrame"/> instances that own memory:
/// frames are built with plain <see cref="ReadOnlyMemory{Byte}"/> payloads (backed by
/// stack- or heap-allocated arrays), so there is no <see cref="IDisposable"/> ownership to
/// forward to <see cref="ICanBusService.SendConfirmedAsync"/> — matching the plain-payload variant
/// of the frame factory.</para>
/// </remarks>
internal sealed class IsoTpChannel : IIsoTpChannel
{
    private readonly ICanBusService _service;
    private readonly bool _ownsService;

    // True when this channel created its own actor and must therefore dispose it. An injected
    // actor belongs to whoever built it -- disposing it here would tear down a loop the caller
    // may still be using, which is the same reason _ownsService exists for the bus service.
    private readonly bool _ownsActor;
    private readonly IsoTpEndpoint _endpoint;
    private readonly IsoTpChannelOptions _options;
    // Cached at construction so a negative / unencodable LocalStMin fails Open instead of
    // throwing on the actor loop mid-FF (which left ReceiveAsync hung).
    private readonly byte _localStMinRaw;

    private readonly IProtocolActor _actor;
    private readonly DeadlineScheduler _deadlines;
    private readonly ISubscription _subscription;
    private readonly Task _readerTask;
    private readonly CancellationTokenSource _readerCts = new();

    // Bounded receive inbox for consumers. Drop-oldest so a stalled reader never stalls the RX
    // state machine (mirrors the L2 Subscription policy). Items are either a fully reassembled
    // PDU or a reassembly-abort fault (N_Cr / SN mismatch / SF·FF supersede) so a blocked
    // ReceiveAsync completes instead of hanging (FR-TP-010) —
    // the FailTx analogue on the RX side.
    //
    // The inbox is not completed when the bus service is lost (only by Dispose): that failure is
    // kept beside it. A receiver takes what is buffered first and meets the failure only on an
    // empty inbox, which is the order the callers rely on, and a discard keeps writing back what
    // it retains into an inbox that is still open.
    private readonly Channel<RxInboxItem> _pduInbox;
    private volatile Exception? _inboxLost;
    // Set while a discard has taken the items it retains out of the inbox and not yet put them
    // back: a receiver meeting an empty inbox then must not read that as "nothing more to come".
    private int _discarding;

    /// <summary>For tests: runs between a discard taking the retained items out of the inbox and
    /// putting them back, on the actor.</summary>
    internal Action? DiscardGapObserver { get; set; }
    private readonly TaskCompletionSource<bool> _inboxLostSignal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // The channel was disposed: kept beside the inbox like the loss, not as the completion of its
    // writer, so that a discard can keep writing back what it retains whatever thread disposes the
    // channel and whenever (#262). A receiver takes what is buffered and meets the end on an empty
    // inbox.
    private volatile bool _inboxClosed;
    private readonly TaskCompletionSource<bool> _inboxClosedSignal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Serializes SendAsync callers: one outbound PDU on the wire at a time, per ISO 15765-2's
    // "one N-USData at a time" model. Also avoids competition for _tx state across calls.
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    // TX state (only accessed on the actor loop). All null/zero while idle.
    private TxState? _tx;

    // Count of SendFrameOnBus → SendConfirmedAsync operations still outstanding (actor-loop only).
    // Cancelled SendAsync must hold _sendGate until this drains so a subsequent send cannot
    // interleave frames with an aborted PDU that already submitted work to the bus
    //.
    private int _busTxInFlight;

    // Set when a SendAsync caller is waiting for _busTxInFlight to reach zero (actor-loop only).
    private TaskCompletionSource<object?>? _busTxIdleWaiter;

    // RX state (only accessed on the actor loop). Null while no multi-frame reassembly in flight.
    private RxState? _rx;

    // The arrival stamp of the First Frame of the reception in progress, or 0 when there is none.
    // Written on the actor loop with _rx; read from any thread by TryGetReceptionInProgress.
    // Copy-on-write: the reader appends, the actor withdraws, callers read a snapshot.
    private IsoTpReceptionInProgress[] _receptionsInProgress = Array.Empty<IsoTpReceptionInProgress>();
    // Frames are taken from the subscription and posted to the actor under this lock, by the
    // reader task and by any caller that needs what is buffered *now* (see PumpSubscription).
    private readonly object _pumpGate = new();
    // The instant of the latest DiscardPendingPdus. Everything that arrived before it is what
    // the discard was asked to drop -- wherever it was at the time: in the demux buffer, in the
    // actor mailbox, or half reassembled -- so the actor drops such a frame unanswered, by its
    // arrival stamp, and the clear keeps a reception that began after it (Codex and Bugbot on
    // #143). An event built without a host stamp -- only a foreign ICanBusService produces
    // those -- is stamped when taken: as "now" by the reader task and a deadline check, as the
    // instant before the discard by the discard's own pump, since it was buffered when the
    // discard began.
    private long _discardStamp;

    private int _disposed;

    // What the current SendAsync is awaiting, published for Dispose's fallback when the actor is
    // already gone (#205). Completing a TaskCompletionSource is thread-safe; the actor-owned state
    // behind it is not touched. A stale reference only ever completes something nobody awaits.
    private TaskCompletionSource<IsoTpTransmitStamps>? _sendCompletion;
    private TaskCompletionSource<object?>? _idleWait;

    /// <summary>
    /// Maximum PDU length this channel will transmit or reassemble — the same codec limit
    /// outbound <see cref="SendAsync"/> enforces via <see cref="IsoTpFrameCodec"/>
    /// (classic ≤ 4095; CAN-FD ≤ <see cref="IsoTpFrameCodec.MaxFdFirstFrameLength"/> capped to
    /// <see cref="int.MaxValue"/>).
    /// </summary>
    private int MaxPduLength =>
        _options.UseCanFd
            ? (int)Math.Min(IsoTpFrameCodec.MaxFdFirstFrameLength, int.MaxValue)
            : IsoTpFrameCodec.MaxClassicFirstFrameLength;

    /// <inheritdoc />
    public IsoTpEndpoint Endpoint => _endpoint;

    /// <inheritdoc />
    public IsoTpChannelOptions Options => _options;

    /// <inheritdoc />
    public event EventHandler<IsoTpDatagramReceivedEventArgs>? DatagramReceived;

    /// <inheritdoc />
    public event EventHandler<Exception>? BackgroundExceptionOccurred;

    /// <summary>
    /// Builds a channel on <paramref name="service"/>. A null <paramref name="actor"/> -- the only
    /// value production passes -- makes the channel create and own its own loop; an injected one
    /// stays the caller's to dispose.
    /// </summary>
    /// <remarks>
    /// The actor parameter is a seam for tests, not a feature: substituting a loop built on a
    /// hand-driven monotonic source is what makes STmin pacing and the N_Cr timeout assertable
    /// without measuring wall-clock time on a shared runner (#92). It works because nothing in
    /// this class reads a clock of its own for an interval -- every one goes through
    /// <see cref="IProtocolActor.Schedule"/> or the <see cref="DeadlineScheduler"/> built on it,
    /// so substituting the actor puts all of them on the one clock the test controls. The
    /// <c>Stopwatch</c> readings that remain here are arrival and transmit *instants* on the
    /// wire (#112), which are facts about the host and deliberately not on the actor's clock.
    /// </remarks>
    internal IsoTpChannel(ICanBusService service, IsoTpEndpoint endpoint,
        IsoTpChannelOptions options, bool ownsService, IProtocolActor? actor = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _endpoint = endpoint;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _ownsService = ownsService;
        _ownsActor = actor is null;
        // Encode once: EncodeStMin throws on negative values; surfacing at Open keeps the RX
        // path free of codec throws that ProtocolActor would only raise as BackgroundException.
        _localStMinRaw = IsoTpFrameCodec.EncodeStMin(_options.LocalStMin);
        // The same for the protocol timers: a negative one makes the deadline scheduler throw
        // after a send is already on the wire or a reception already started, which leaves it
        // with no deadline at all. Zero would fire on the next loop iteration, i.e. never wait.
        RequirePositive(nameof(_options.NAs), _options.NAs);
        RequirePositive(nameof(_options.NBs), _options.NBs);
        RequirePositive(nameof(_options.NCr), _options.NCr);

        var inboxOptions = new BoundedChannelOptions(Math.Max(1, _options.ReceiveBufferCapacity))
        {
            SingleReader = false,
            SingleWriter = true, // written only from the actor loop
            FullMode = BoundedChannelFullMode.DropOldest,
        };
        _pduInbox = Channel.CreateBounded<RxInboxItem>(inboxOptions);

        _actor = actor ?? new ProtocolActor();
        _actor.BackgroundExceptionOccurred += OnActorBackgroundException;
        _deadlines = new DeadlineScheduler(_actor);

        try
        {
            var idFilter = CanIdFilter.Range(
                _endpoint.RxCanId, _endpoint.RxCanId,
                _endpoint.IsExtendedCanId ? CanFilterIDType.Extend : CanFilterIDType.Standard);
            // Echoes are asked for on purpose. `IsEcho` marks what this HOST transmitted, not
            // what this CHANNEL transmitted, and IsoTp.Open documents that channels with
            // disjoint endpoints may share one service (FR-TP-018). Two reciprocal channels in
            // one process -- a tester and a simulated ECU, the shape most of these tests use --
            // are peers to each other, so filtering on the host bit would make the receiver miss
            // the SF/FF entirely and the sender time out.
            //
            // The endpoint is the instance-level identity, and this filter already applies it: a
            // channel transmits on TxCanId and accepts only RxCanId, so its own frames cannot
            // match its own filter -- unless the two are the same identifier and nothing in the
            // payload tells the directions apart either: Extended addressing writes the target
            // byte outbound and accepts only the source byte inbound, which the reader's
            // address-extension filter already applies (Codex on #147). Only when the
            // identifier *and* the extension byte coincide is a host echo of this channel's own
            // frame indistinguishable from the peer's, and there it is withheld (#56); a
            // reciprocal in-process pair of that shape is unreachable by construction.
            bool selfAddressed = _endpoint.TxCanId == _endpoint.RxCanId
                && (!_endpoint.UsesAddressExtension
                    || _endpoint.AddressExtension == _endpoint.RxAddressExtension);
            _subscription = _service.Subscribe(idFilter, includeEcho: !selfAddressed);
        }
        catch
        {
            _actor.BackgroundExceptionOccurred -= OnActorBackgroundException;
            if (_ownsActor) _actor.Dispose();
            throw;
        }

        _readerTask = Task.Run(RunReaderAsync);
    }

    /// <inheritdoc />
    public async Task SendAsync(ReadOnlyMemory<byte> pdu, CancellationToken cancellationToken = default)
        => await SendWithTransmitStampAsync(pdu, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IsoTpTransmitStamps> SendWithTransmitStampAsync(ReadOnlyMemory<byte> pdu,
        CancellationToken cancellationToken = default)
    {
        if (pdu.Length == 0)
            throw new ArgumentException("ISO-TP PDU must be non-empty.", nameof(pdu));
        if (pdu.Length > MaxPduLength)
            throw new ArgumentOutOfRangeException(nameof(pdu), pdu.Length,
                $"ISO-TP PDU length must be at most {MaxPduLength} bytes for this channel's frame kind.");
        ThrowIfDisposed();

        // Serialize per-channel: peer state (SN, block, deadlines) is only valid for one PDU at
        // a time. A canceled wait leaves the previous PDU untouched -- exactly the standard
        // .NET-cancellation-of-a-queued-op semantics.
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var tcs = new TaskCompletionSource<IsoTpTransmitStamps>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pduBytes = pdu.ToArray();
            Volatile.Write(ref _sendCompletion, tcs);
            CancellationTokenRegistration ctr = cancellationToken.CanBeCanceled
                ? cancellationToken.Register(static state =>
                {
                    var (self, t, ct) = ((IsoTpChannel, TaskCompletionSource<IsoTpTransmitStamps>, CancellationToken))state!;
                    self.CancelInFlightSend(t, ct);
                }, (this, tcs, cancellationToken))
                : default;

            try
            {
                _actor.Post(() => BeginSendOnLoop(pduBytes, tcs, cancellationToken));
            }
            catch (Exception ex)
            {
                ctr.Dispose();
                tcs.TrySetException(ex);
            }

            IsoTpTransmitStamps transmitStamp;
            try
            {
                transmitStamp = await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                ctr.Dispose();
                // Hold the gate until any SendConfirmedAsync already submitted for this PDU finishes.
                // CancelInFlightSend completes the TCS immediately but must not let the next
                // SendAsync race frames onto the bus. Skip on dispose:
                // no subsequent SendAsync can run, and bus-TX confirmations may never post
                // back onto a torn-down actor.
                //
                // Not unbounded while the channel lives (#202): every frame's SendConfirmedAsync is
                // bounded by N_As on both the echo and the approximated path, so a confirm that
                // never comes back still posts a timed-out outcome and drains _busTxInFlight.
                // That bound is what keeps this wait -- made with _sendGate held -- from wedging
                // the channel; it does not take a token of its own.
                if (Volatile.Read(ref _disposed) == 0)
                    await WaitForBusTxIdleAsync().ConfigureAwait(false);
            }

            return transmitStamp;
        }
        finally
        {
            // Dispose may tear down the gate after FailTx unblocks us but before Release runs
            //. Swallow ODE so shutdown stays clean.
            try { _sendGate.Release(); }
            catch (ObjectDisposedException) { /* channel disposed */ }
        }
    }

    /// <inheritdoc />
    public async Task<byte[]> ReceiveAsync(CancellationToken cancellationToken = default)
        => (await ReceiveWithArrivalAsync(cancellationToken).ConfigureAwait(false)).Pdu;

    /// <inheritdoc />
    public async Task<IsoTpReceivedPdu> ReceiveWithArrivalAsync(
        CancellationToken cancellationToken = default)
    {
        var (taken, item) = await TakeNextAsync(cancellationToken).ConfigureAwait(false);
        if (!taken)
            throw new InvalidOperationException("Channel is disposed; no more PDUs will arrive.");
        return new IsoTpReceivedPdu(UnwrapInboxItem(item), item.ArrivalTimestamp, item.FirstFrameArrivalTimestamp);
    }

    /// <inheritdoc />
    public IReadOnlyList<IsoTpReceptionInProgress> GetReceptionsInProgress()
    {
        // The reader task is one more scheduling hop the answer must not wait for: what the
        // demux has buffered is published here, on the caller's thread, before the snapshot
        // (Codex on #143, third round).
        PumpSubscription();
        return Array.AsReadOnly(Volatile.Read(ref _receptionsInProgress));
    }

    private void PublishReception(IsoTpReceptionInProgress reception)
    {
        while (true)
        {
            var current = Volatile.Read(ref _receptionsInProgress);
            if (Array.IndexOf(current, reception) >= 0) return;
            var next = new IsoTpReceptionInProgress[current.Length + 1];
            Array.Copy(current, next, current.Length);
            next[current.Length] = reception;
            if (Interlocked.CompareExchange(ref _receptionsInProgress, next, current) == current)
                return;
        }
    }

    private void WithdrawReception(IsoTpReceptionInProgress reception)
    {
        while (true)
        {
            var current = Volatile.Read(ref _receptionsInProgress);
            int at = Array.IndexOf(current, reception);
            if (at < 0) return;
            var next = new IsoTpReceptionInProgress[current.Length - 1];
            Array.Copy(current, 0, next, 0, at);
            Array.Copy(current, at + 1, next, at, current.Length - at - 1);
            if (Interlocked.CompareExchange(ref _receptionsInProgress, next, current) == current)
                return;
        }
    }

    /// <inheritdoc />
    public bool TryReceiveWithArrival(out IsoTpReceivedPdu pdu)
    {
        if (_pduInbox.Reader.TryRead(out var item))
        {
            pdu = new IsoTpReceivedPdu(UnwrapInboxItem(item), item.ArrivalTimestamp, item.FirstFrameArrivalTimestamp);
            return true;
        }

        pdu = default;
        return false;
    }

    /// <inheritdoc />
    public Task SettleAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0) return Task.CompletedTask;
        // What the demux has buffered goes to the actor now rather than after the reader's
        // next scheduling; then a no-op posted behind it completes once the actor has taken
        // everything queued so far. Called from the actor itself, the post would wait for the
        // loop it is on, and the pumped frames cannot be handled inline either: frames the
        // reader task posted earlier sit ahead of them in the mailbox, and reassembly does not
        // survive the reordering (Bugbot on #150). They are handled after the current work
        // item, which the contract says.
        PumpSubscription();
        if (_actor is ProtocolActor { IsOnCurrentActor: true }) return Task.CompletedTask;
        try
        {
            return _actor.PostAsync(() => { }, cancellationToken);
        }
        catch (ObjectDisposedException)
        {
            return Task.CompletedTask; // channel tearing down: nothing left on its way
        }
    }

    public int DiscardPendingPdus() => DiscardPendingPdus(arrivedBefore: 0);

    /// <inheritdoc />
    public int DiscardPendingPdus(long arrivedBefore)
    {
        // Everything that arrived up to now is pending -- or up to the caller's stamp, which
        // may be earlier (Codex on #150); the discard stamp never moves back, so a frame
        // already admitted stays admitted. The stamp goes first, so a frame the
        // pump posts -- or the reader task posts concurrently -- is dropped by the actor if it
        // arrived before it, and answered with no Flow Control that would invite the rest of
        // a transfer the caller has given up on (Bugbot on #143). Whatever the demux has
        // buffered is posted now rather than after the caller's next request, so its outcome
        // -- dropped -- is settled by the time the clear below runs. Stamp and drain happen
        // under the pump lock: an event without a host stamp is stamped when taken, and the
        // reader task taking one between the two would stamp a frame buffered before the
        // discard as after it (Codex on #143).
        long stamp;
        lock (_pumpGate)
        {
            stamp = arrivedBefore > 0 ? arrivedBefore : Stopwatch.GetTimestamp();
            if (stamp > Volatile.Read(ref _discardStamp)) Volatile.Write(ref _discardStamp, stamp);
            PumpSubscription(unstampedArrival: stamp - 1);
        }

        // Clear the actor-side state on the actor: the reassembly, the records and the inbox
        // items that belong to receptions from before the stamp. Silent -- not AbortRx -- so
        // no BackgroundExceptionOccurred is raised and no fault enqueued that would at once be
        // drained. Post+wait so anything already queued on the mailbox is settled first. A
        // reception that began after the stamp is kept: it is not what the caller asked to
        // drop, and its PDU may be the one the caller waits for next.
        // Called from the actor itself -- a BackgroundExceptionOccurred handler runs there --
        // the post-and-wait below would wait for the very loop it is on (#56). The clear runs
        // inline instead: it is on the single writer already.
        if (_actor is ProtocolActor { IsOnCurrentActor: true })
            return ClearReceptionsBefore(stamp);

        Task<int> cleared;
        try
        {
            cleared = _actor.PostAsync(() => ClearReceptionsBefore(stamp));
        }
        catch (ObjectDisposedException)
        {
            return 0; // channel tearing down: nothing left to clear
        }

        return cleared.GetAwaiter().GetResult();
    }

    // On the actor. Clears the reassembly, the records and the inbox items that belong to
    // receptions from before the stamp; returns how many inbox items went.
    private int ClearReceptionsBefore(long stamp)
    {
        int discarded = 0;
        var rx = _rx;
        if (rx is not null && rx.Announce.FirstFrameArrivalTimestamp < stamp)
        {
            rx.CancelDeadline();
            _rx = null;
        }
        foreach (var reception in Volatile.Read(ref _receptionsInProgress)
                     .Where(r => r.FirstFrameArrivalTimestamp < stamp))
            WithdrawReception(reception);
        // Drain both PDU and AbortRx-fault items from before the stamp. Leaving a fault behind
        // would poison the next ReceiveAsync after a higher-layer timeout/cancel -- the
        // opposite of the reset intent. Items from after it go back in their order; this runs
        // on the inbox's single writer.
        var kept = new List<RxInboxItem>();
        Volatile.Write(ref _discarding, 1);
        try
        {
            while (_pduInbox.Reader.TryRead(out var item))
            {
                // An error item carries the first-frame stamp of the reception it aborted, so an
                // abort of a reception that began after the stamp is kept as that reception's
                // outcome (Codex on #143).
                if (item.FirstFrameArrivalTimestamp >= stamp)
                    kept.Add(item);
                else
                    discarded++;
            }

            DiscardGapObserver?.Invoke();
            foreach (var item in kept)
                _pduInbox.Writer.TryWrite(item);
        }
        finally
        {
            Volatile.Write(ref _discarding, 0);
        }
        return discarded;
    }

    /// <inheritdoc />
    public IAsyncEnumerable<byte[]> ReceiveAllAsync(CancellationToken cancellationToken = default)
        => ReadAllAsync(cancellationToken);

    private async IAsyncEnumerable<byte[]> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (true)
        {
            var (taken, item) = await TakeNextAsync(cancellationToken).ConfigureAwait(false);
            if (!taken) yield break;
            yield return UnwrapInboxItem(item);
        }
    }

    /// <summary>
    /// The next item of the inbox, waiting for one. False once the inbox is completed (the channel
    /// was disposed). If the bus service was lost, what is buffered is delivered first and the loss
    /// is thrown only on an empty inbox, to every receiver.
    /// </summary>
    private async Task<(bool Taken, RxInboxItem Item)> TakeNextAsync(CancellationToken cancellationToken)
    {
        var lossConfirmed = false;
        while (true)
        {
            // Before any read: a canceled token must not consume a buffered PDU, as the wait on the
            // token did before this loop existed (and as TryReceiveWithArrival documents).
            cancellationToken.ThrowIfCancellationRequested();
            if (_pduInbox.Reader.TryRead(out var item)) return (true, item);
            var lost = _inboxLost;
            if (lost is not null || _inboxClosed)
            {
                // A discard has the retained items out of the inbox for a moment: wait it out.
                if (Volatile.Read(ref _discarding) != 0)
                {
                    lossConfirmed = false;
                    await Task.Yield();
                    continue;
                }

                // The empty inbox was read before the flag: a discard that finished in between has
                // put its items back. Read once more, after the flag, before it counts.
                if (!lossConfirmed)
                {
                    lossConfirmed = true;
                    continue;
                }

                // The loss is the more specific end; a channel that was only disposed ends quietly.
                if (lost is not null) throw lost;
                return (false, default);
            }

            using var release = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var wait = _pduInbox.Reader.WaitToReadAsync(release.Token).AsTask();
            var first = await Task.WhenAny(wait, _inboxLostSignal.Task, _inboxClosedSignal.Task).ConfigureAwait(false);
            if (first != wait)
            {
                // The loss or the disposal came first: let go of the wait and look again, items
                // before the end.
                release.Cancel();
                try { await wait.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* the wait we just released */ }
                continue;
            }

            await wait.ConfigureAwait(false);
        }
    }

    private static byte[] UnwrapInboxItem(RxInboxItem item)
    {
        if (item.Error is not null)
            throw item.Error;
        return item.Pdu!;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Order matters: stop pulling frames off the subscription so nothing new is queued to the
        // actor while it winds down, then complete the PDU inbox so pending ReceiveAsync callers
        // see graceful termination, then tear down the actor (which cancels any pending deadlines
        // by observing ObjectDisposedException on their next actor.Schedule call), then finally
        // release the demux subscription and, if owned, the service.
        try { _readerCts.Cancel(); } catch { /* nothing else to do */ }

        // Consumers awaiting ReceiveAsync/ReadAllAsync are let go before the reader task is torn
        // down -- otherwise a consumer could observe a canceled reader without any end signal. The
        // end is a flag and a signal beside the inbox, not the completion of its writer: a
        // DiscardPendingPdus has the items it retains out of the inbox for a moment while it runs
        // on the actor, and completing the writer from this thread in that moment made the
        // write-back fail and the retained PDUs vanish (#262). It wakes the receivers at once, from
        // whichever thread this is, and needs no actor to run.
        _inboxClosed = true;
        _inboxClosedSignal.TrySetResult(true);

        // Fail any in-flight SendAsync so its caller doesn't hang forever waiting for a TCS the
        // now-disposed actor will never complete. Also release any bus-TX idle waiter that would
        // otherwise block SendAsync's finally path after CancelInFlightSend.
        void FailInFlightSend()
        {
            var tx = _tx;
            _tx = null;
            tx?.Fail(new ObjectDisposedException(nameof(IsoTpChannel)));
            var idle = _busTxIdleWaiter;
            _busTxIdleWaiter = null;
            idle?.TrySetResult(null);

            // A reassembly under way dies with the channel: its deadline is cancelled and its
            // record withdrawn, so GetReceptionsInProgress does not keep reporting a transfer
            // nobody will complete (Codex on #216). Posted behind any frame already queued, so
            // that frame is handled first (and dropped by the disposed guard).
            var rx = _rx;
            if (rx is not null)
            {
                rx.CancelDeadline();
                _rx = null;
                WithdrawReception(rx.Announce);
            }
        }

        try
        {
            _actor.Post(FailInFlightSend);
        }
        catch (ObjectDisposedException)
        {
            // An injected actor its owner already disposed (#205): Dispose must still be safe to
            // call and must still run the rest of its cleanup below. Actor state (_tx,
            // _busTxIdleWaiter) is not touched from here -- the owner's Dispose may still be
            // joining a loop that is running (Bugbot on #216). What the awaiting SendAsync waits
            // on is thread-safe to complete, and is all that has to be released.
            Volatile.Read(ref _sendCompletion)?.TrySetException(new ObjectDisposedException(nameof(IsoTpChannel)));
            Volatile.Read(ref _idleWait)?.TrySetResult(null);
        }

        try { _readerTask.Wait(TimeSpan.FromSeconds(2)); } catch { /* observed via task; not fatal */ }

        _subscription.Dispose();

        // Whatever is still published is withdrawn last: the reader has stopped, the subscription
        // is closed, and under the pump lock no caller-side pump is mid-way through announcing a
        // frame (later ones refuse, above). Needed when the actor is gone and can no longer run
        // the cleanup it was asked to (Codex on #216); the published records are a lock-free
        // snapshot any thread may update, unlike _rx, which stays untouched here.
        lock (_pumpGate)
        {
            foreach (var reception in Volatile.Read(ref _receptionsInProgress))
                WithdrawReception(reception);
        }
        // Actor.Dispose drains the FailTx/idle-waiter post above (FinalDrain), so the in-flight
        // SendAsync can leave its await and enter WaitForBusTxIdleAsync / Release. An injected
        // actor is not ours to dispose -- the caller may still be running other channels on it --
        // but the handler is, so it comes off either way rather than outliving this channel on a
        // loop that keeps going.
        _actor.BackgroundExceptionOccurred -= OnActorBackgroundException;
        if (_ownsActor) _actor.Dispose();
        _readerCts.Dispose();

        // Do not dispose _sendGate while an in-flight SendAsync still holds it — Release would
        // then throw ObjectDisposedException. Wait briefly for that caller
        // to finish its finally path; timed-out Wait leaves the gate held and Dispose still
        // proceeds (Release then no-ops via the catch in SendAsync).
        try { _sendGate.Wait(TimeSpan.FromSeconds(2)); }
        catch (ObjectDisposedException) { /* already disposed */ }
        try { _sendGate.Dispose(); }
        catch (ObjectDisposedException) { /* already disposed */ }

        if (_ownsService)
            _service.Dispose();
    }

    // -----------------------------------------------------------------------------------------
    // Subscription reader — one long-lived Task per channel that pushes RX frames onto the actor.
    // -----------------------------------------------------------------------------------------
    private async Task RunReaderAsync()
    {
        Exception lost;
        try
        {
            while (await _subscription.WaitToReadAsync(_readerCts.Token).ConfigureAwait(false))
                PumpSubscription();

            // The subscription ended on its own: the service it came from was disposed under a
            // channel opened with leaveOpen.
            lost = new ObjectDisposedException(nameof(ICanBusService),
                "The bus service was disposed while the ISO-TP channel was open.");
        }
        catch (OperationCanceledException)
        {
            return; // expected on Dispose
        }
        catch (Exception ex) when (ex is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException
            and not AppDomainUnloadedException
            and not BadImageFormatException
            and not CannotUnloadAppDomainException
            and not ThreadAbortException)
        {
            lost = ex;
        }
        catch (Exception ex) when (ex is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or AppDomainUnloadedException
            or BadImageFormatException
            or CannotUnloadAppDomainException
            or ThreadAbortException)
        {
            // The filter above is what keeps the generic catch closed, so these used to leave
            // this method entirely. This method is the reader task. Nothing else observes that
            // task except Dispose, and Dispose swallows the wait, so the inbox loss was never
            // published and a ReceiveAsync already waiting never woke. Publish first, then
            // rethrow: the post is queued before the task faults, and the task still faults.
            // A stack overflow the runtime detects itself is not delivered here (the process
            // ends), and ThreadAbortException is not thrown on this runtime. An
            // OutOfMemoryException from the pump is delivered, and has to take this path.
            PublishSubscriptionLoss(ex);
            throw;
        }

        PublishSubscriptionLoss(lost);
    }

    // Nothing will ever arrive again. A receiver waiting on the inbox would wait for ever, so
    // the reason is recorded and the receivers are woken: what is buffered is delivered first,
    // and then every receiver -- not just one -- gets the failure. Sends already fail on their
    // own, the bus refuses them. On the actor, like everything else that touches the inbox.
    private void PublishSubscriptionLoss(Exception lost)
    {
        try
        {
            // Under the pump lock: a caller that is pumping the subscription right now -- it took
            // the last frame and has not posted it yet -- finishes first, so the frame is on the
            // actor ahead of the loss and a receiver cannot meet the loss before it.
            lock (_pumpGate)
            {
                _actor.Post(() => EndInboxAfterSubscriptionLoss(lost));
            }
        }
        catch (ObjectDisposedException)
        {
            // The actor is gone (an injected one its owner disposed): there is nobody to write the
            // inbox for. The loss is still reported; the channel's own disposal completes the inbox.
            RaiseBackgroundException(lost);
        }
    }

    private void EndInboxAfterSubscriptionLoss(Exception lost)
    {
        // A reassembly under way dies with the subscription. No fault item for it: in the bounded
        // inbox it would push the oldest finished PDU out, and only one receiver would see it.
        var rx = _rx;
        if (rx is not null)
        {
            rx.CancelDeadline();
            _rx = null;
            WithdrawReception(rx.Announce);
        }

        _inboxLost = lost;
        _inboxLostSignal.TrySetResult(true);
        RaiseBackgroundException(lost);
    }

    private static void RequirePositive(string name, TimeSpan value)
    {
        if (value <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(name, value, name + " must be greater than zero.");
    }

    /// <summary>
    /// Takes every frame the subscription has buffered and posts it to the actor, in order.
    /// Run by the reader task when frames arrive, and by a caller that must see the buffered
    /// frames now -- a deadline check, a discard -- rather than after the reader's next
    /// scheduling. The lock keeps the two from interleaving, which would reorder frames.
    /// </summary>
    private void PumpSubscription(long unstampedArrival = 0)
    {
        lock (_pumpGate)
        {
            // A disposed channel takes nothing more off the subscription: a First Frame ingested
            // now would be announced and then posted to an actor that no longer runs it, and
            // nothing would ever withdraw the announcement (Codex on #216).
            if (Volatile.Read(ref _disposed) != 0)
                return;

            while (_subscription.TryRead(out var frameEvent))
            {
                try
                {
                    IngestFrame(frameEvent, unstampedArrival);
                }
                catch (ObjectDisposedException)
                {
                    return; // actor gone: the channel is tearing down
                }
            }
        }
    }

    private void IngestFrame(in CanFrameEvent frameEvent, long unstampedArrival)
    {
        // The demux stamps every frame before it is buffered for any subscription, so
        // neither this reader's channel wait, nor the actor mailbox, nor reassembly
        // contributes to it -- all three are scheduling, and a deadline measured after
        // scheduling is not a deadline. The adapter's own ReceiveTimestamp cannot serve:
        // zero on adapters that do not timestamp, and documented as not comparable
        // across buses.
        //
        // Falling back to "now" keeps an event built outside the demux (a hand-rolled
        // ISubscription in a test, say) usable rather than making it look infinitely
        // old, which is what a zero stamp would mean to a deadline.
        var frameArrival = frameEvent.HostArrivalTimestamp > 0
            ? frameEvent.HostArrivalTimestamp
            : unstampedArrival > 0 ? unstampedArrival : Stopwatch.GetTimestamp();
        var frame = frameEvent.Frame;
        // Not the hazard the previous comment described: the subscription already hands
        // out a payload it owns, so nothing the adapter does can corrupt it. What it hands
        // out is one array shared by every subscription that matched the frame, and it is
        // a ReadOnlyMemory<byte> while the state machine below wants a byte[] it keeps
        // across await points -- so this stays a copy, now as this channel's private
        // buffer rather than as protection against the RX lease.
        var payload = frame.Data.ToArray();
        var addrExt = _endpoint.UsesAddressExtension;
        // Endpoint uses an address-extension byte and the first byte does not match:
        // skip this frame silently (matches ISO 15765-2 §5.2.4.4 semantics for a foreign
        // address-extension on the same CAN-ID).
        //
        // Fix: filter on the *RX* address extension. For Extended
        // addressing that is the local node's source address (which the peer writes into
        // the AE byte when it addresses us), NOT our outbound target-address byte.
        // For Mixed addressing the two are the same, so this is unchanged there.
        if (addrExt && (payload.Length == 0 || payload[0] != _endpoint.RxAddressExtension))
            return;

        // Pass the on-wire frame kind into TryParsePci so CAN-FD escape SF/FF headers are
        // accepted only for real FD frames (develop codec API: isCanFd required).
        bool isCanFd = frame.FrameKind == CanFrameType.CanFd;
        // A First Frame is published here, before the actor sees it: a caller whose deadline
        // fires while the actor is behind must find the frame at its arrival, not at its
        // processing (Codex on #143). Appended, not written over: a PDU whose frames are all
        // still in the mailbox is a reception in progress too, and the next First Frame must
        // not hide it (Codex, again). The actor withdraws each record on its outcome.
        IsoTpReceptionInProgress? announce = null;
        if (IsoTpFrameCodec.TryParsePci(payload, _endpoint, isCanFd, out var pci)
            && pci.Type == PciType.FirstFrame
            && !FitsASingleFrame(pci, payload.Length)
            && frameArrival >= Volatile.Read(ref _discardStamp))
        {
            announce = new IsoTpReceptionInProgress(frameArrival, pci.Length,
                new ReadOnlyMemory<byte>(payload, pci.DataOffset,
                    Math.Max(0, payload.Length - pci.DataOffset)));
            PublishReception(announce);
        }
        _actor.Post(() => HandleReceivedFrame(payload, isCanFd, frameArrival, announce));
    }

    private void RaiseBackgroundException(Exception ex)
    {
        try
        {
            BackgroundExceptionOccurred?.Invoke(this, ex);
        }
        catch
        {
            // A misbehaving subscriber must not tear down the channel.
        }
    }

    private void OnActorBackgroundException(object? sender, Exception ex) => RaiseBackgroundException(ex);

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(IsoTpChannel));
    }

    // -----------------------------------------------------------------------------------------
    // TX side (all methods run on the actor loop unless noted)
    // -----------------------------------------------------------------------------------------

    private void BeginSendOnLoop(byte[] pdu, TaskCompletionSource<IsoTpTransmitStamps> tcs, CancellationToken ct)
    {
        // Fix: the send may have been canceled between when the caller
        // posted us and when the actor got around to running us -- CancelInFlightSend may have
        // even already completed `tcs` synchronously (and/or posted actor-side cleanup that
        // saw _tx==null). In either case the outbound frame must never hit the wire;
        // TrySetCanceled is a no-op if the TCS is already completed.
        if (IsSendAlreadyCanceled(tcs, ct))
            return;

        // Disposed between the caller's check and this callback: Dispose has already failed
        // whatever was in flight, and found nothing, so a send that began now would put a frame
        // on the bus after disposal and never be completed. HandleReceivedFrame has the same guard.
        if (Volatile.Read(ref _disposed) != 0)
        {
            tcs.TrySetException(new ObjectDisposedException(nameof(IsoTpChannel)));
            return;
        }

        if (_tx is not null)
        {
            // Sender gate already serializes SendAsync, so _tx must be null when we get here.
            // If it isn't, something has gone catastrophically wrong; fail this send rather
            // than corrupt the state machine.
            tcs.TrySetException(new InvalidOperationException(
                "Internal error: another ISO-TP send is already in flight (send-gate leaked)."));
            return;
        }

        _tx = new TxState(pdu, tcs);

        // Re-check after publishing _tx: CancelInFlightSend can complete `tcs` on another
        // thread the moment the token flips, and will post actor cleanup after us. If the
        // caller already canceled, drop _tx and emit nothing.
        if (IsSendAlreadyCanceled(tcs, ct))
        {
            if (ReferenceEquals(_tx?.Tcs, tcs))
                _tx = null;
            return;
        }

        // Fix: any synchronous throw from the codec (bad endpoint / bad
        // length / etc.) must clear _tx and fail the TCS. Otherwise the actor's own
        // BackgroundException handler swallows the throw, the awaiting SendAsync never
        // completes, and _tx stays pinned to the dead operation -- so the next SendAsync
        // sees the "gate leaked" branch above forever.
        try
        {
            int sfMax = IsoTpFrameCodec.SingleFrameMaxDataLength(_options.UseCanFd, _endpoint.UsesAddressExtension);
            if (pdu.Length <= sfMax)
            {
                SendSingleFrame();
            }
            else
            {
                SendFirstFrame();
            }
        }
        catch (Exception ex)
        {
            FailTx(ex);
        }
    }

    private static bool IsSendAlreadyCanceled(TaskCompletionSource<IsoTpTransmitStamps> tcs, CancellationToken ct)
    {
        if (!tcs.Task.IsCompleted && !ct.IsCancellationRequested)
            return false;
        tcs.TrySetCanceled(ct);
        return true;
    }

    private void SendSingleFrame()
    {
        var tx = _tx!;
        var payload = IsoTpFrameCodec.BuildSingleFrame(_endpoint, tx.Pdu.AsSpan(),
            _options.UseCanFd, _options.UsePadding, _options.PaddingByte);
        SendFrameOnBus(payload, expectTx: TxExpect.SingleFrameConfirm);
    }

    private void SendFirstFrame()
    {
        var tx = _tx!;
        bool useLong = tx.Pdu.Length > IsoTpFrameCodec.MaxClassicFirstFrameLength;
        int ffData = IsoTpFrameCodec.FirstFrameMaxDataLength(_options.UseCanFd,
            _endpoint.UsesAddressExtension, useLong);
        if (ffData <= 0)
        {
            FailTx(new InvalidOperationException(
                "ISO-TP first-frame data capacity is non-positive for the configured endpoint/frame kind."));
            return;
        }

        var firstChunk = tx.Pdu.AsSpan(0, Math.Min(ffData, tx.Pdu.Length));
        var payload = IsoTpFrameCodec.BuildFirstFrame(_endpoint, tx.Pdu.Length, firstChunk,
            _options.UseCanFd);

        tx.Offset = firstChunk.Length;
        tx.NextSn = IsoTpFrameCodec.FirstConsecutiveSequenceNumber;
        // Stay in SingleOrFirstInFlight until FF is TX-confirmed. N_Bs is armed only after that
        // confirmation, matching block-FC waits that arm N_Bs after the last
        // CF of a block is confirmed. Early peer FC is deferred until confirm (see OnSendConfirmed).
        tx.State = TxStage.SingleOrFirstInFlight;
        tx.WaitFramesReceived = 0;
        SendFrameOnBus(payload, expectTx: TxExpect.FirstFrameConfirm);
    }

    private void SendNextConsecutiveFrame(TxState tx)
    {
        if (!ReferenceEquals(_tx, tx)) return; // canceled/failed/replaced while STmin scheduled
        tx.StMinTimer = null;

        int cfCap = IsoTpFrameCodec.ConsecutiveFrameMaxDataLength(_options.UseCanFd,
            _endpoint.UsesAddressExtension);
        int remaining = tx.Pdu.Length - tx.Offset;
        int chunkLen = Math.Min(cfCap, remaining);
        var chunk = tx.Pdu.AsSpan(tx.Offset, chunkLen);
        var payload = IsoTpFrameCodec.BuildConsecutiveFrame(_endpoint, tx.NextSn, chunk,
            _options.UseCanFd, _options.UsePadding, _options.PaddingByte);
        tx.LastCfChunkLen = chunkLen;
        tx.State = TxStage.SendingCf;
        SendFrameOnBus(payload, expectTx: TxExpect.ConsecutiveFrameConfirm);
    }

    private enum TxExpect
    {
        SingleFrameConfirm,
        FirstFrameConfirm,
        ConsecutiveFrameConfirm,
    }

    // Non-blocking fire: SendConfirmedAsync runs on the thread pool and posts its outcome back on the
    // actor. The actor loop is never blocked on a Task.await (SendConfirmedAsync can wait up to N_As
    // for an echo). Both the success handler and the fault handler are named methods on TxState
    // so a canceled/timed-out send that we already failed doesn't touch _tx twice.
    private void SendFrameOnBus(byte[] payload, TxExpect expectTx)
    {
        var frame = _options.UseCanFd
            ? CanFrame.Fd(unchecked((int)_endpoint.TxCanId), payload,
                isExtendedFrame: _endpoint.IsExtendedCanId)
            : CanFrame.Classic(unchecked((int)_endpoint.TxCanId), payload,
                isExtendedFrame: _endpoint.IsExtendedCanId);

        // Capture the current TX for closure identity: if _tx has been replaced by a subsequent
        // send by the time confirmation lands, we must not touch that unrelated operation.
        var expected = _tx;
        var timeout = _options.NAs;

        // Increment before leaving the actor so WaitForBusTxIdleAsync never observes a gap
        // between "frame submitted" and "in-flight count visible".
        _busTxInFlight++;

        _ = Task.Run(async () =>
        {
            TxConfirmation? confirmation = null;
            Exception? failure = null;
            try
            {
                // The fallback handoff instant, for a service that reports none: taken here,
                // before the call, it is no later than the frame's wire instant, but the
                // service's own lock wait can sit between the two; the service's reading,
                // taken inside that lock, replaces it in OnSendConfirmed (Codex on #147).
                if (expected is not null)
                    expected.LastFrameHandoffTimestamp = Stopwatch.GetTimestamp();
                var c = await _service.SendConfirmedAsync(frame, timeout).ConfigureAwait(false);
                confirmation = c;
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            try
            {
                _actor.Post(() =>
                {
                    try
                    {
                        OnSendConfirmed(expected, expectTx, confirmation, failure);
                    }
                    finally
                    {
                        NoteBusTxCompleted();
                    }
                });
            }
            catch (ObjectDisposedException)
            {
                // Actor is gone; the channel is tearing down. Dispose already releases any idle
                // waiter, so there is nothing left to synchronize with.
            }
        });
    }

    /// <summary>
    /// Actor-loop: one <see cref="SendFrameOnBus"/> confirmation has finished. Unblocks a
    /// <see cref="WaitForBusTxIdleAsync"/> waiter when the last in-flight bus TX drains.
    /// </summary>
    private void NoteBusTxCompleted()
    {
        if (_busTxInFlight > 0)
            _busTxInFlight--;
        if (_busTxInFlight == 0 && _busTxIdleWaiter is not null)
        {
            var waiter = _busTxIdleWaiter;
            _busTxIdleWaiter = null;
            waiter.TrySetResult(null);
        }
    }

    /// <summary>
    /// Completes when every <see cref="SendFrameOnBus"/> started for the current send has had
    /// its confirmation posted back onto the actor (or the channel/actor is already disposed).
    /// </summary>
    private Task WaitForBusTxIdleAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return Task.CompletedTask;

        var waiter = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _idleWait, waiter);
        try
        {
            _actor.Post(() =>
            {
                // Channel dispose may have raced past the Volatile check above; do not arm a
                // waiter that Dispose's FailTx post already missed.
                if (Volatile.Read(ref _disposed) != 0 || _busTxInFlight == 0)
                {
                    waiter.TrySetResult(null);
                }
                else
                {
                    // Send gate serializes SendAsync, so at most one idle waiter exists.
                    _busTxIdleWaiter = waiter;
                }
            });
        }
        catch (ObjectDisposedException)
        {
            waiter.TrySetResult(null);
        }
        return waiter.Task;
    }

    private void OnSendConfirmed(TxState? expected, TxExpect kind, TxConfirmation? confirmation, Exception? failure)
    {
        // Late confirmation for a canceled/replaced/aborted TX: ignore.
        if (expected is null || !ReferenceEquals(expected, _tx))
            return;

        if (failure is not null)
        {
            FailTx(failure);
            return;
        }

        var conf = confirmation!.Value;

        // The bus reports when it handed this frame to the driver. Recording it here, on the
        // actor, rather than around the send: this task's own scheduling and the pending-send
        // lock both sit between "we decided to send" and the frame going out, and P2 starts at
        // the second of those (Codex on #112). The last frame to be confirmed leaves the value
        // CompleteTx hands back, which is the instant the request finished transmitting.
        if (conf.HostTransmitTimestamp > 0)
            expected.LastFrameTransmitTimestamp = conf.HostTransmitTimestamp;
        // Frames go out one at a time, so this is the last frame's by the time the send
        // completes. A caller's cutoff for "could still be a response to this PDU" must not be
        // later than that frame's wire instant; the acceptance stamp above can be (#146), and
        // so can a reading taken outside the service's lock (Codex on #147).
        if (conf.HostHandoffTimestamp > 0)
            expected.LastFrameHandoffTimestamp = conf.HostHandoffTimestamp;

        if (!conf.Confirmed)
        {
            switch (conf.FailureReason)
            {
                case TxConfirmFailureReason.Timeout:
                    FailTx(new IsoTpTimeoutException(IsoTpTimer.NAs,
                        "N_As timer expired waiting for CAN driver TX confirmation."));
                    break;
                case TxConfirmFailureReason.BusOff:
                    FailTx(new IsoTpException("CAN bus went BusOff during ISO-TP transmission."));
                    break;
                case TxConfirmFailureReason.Rejected:
                    FailTx(new IsoTpSendRejectedException(
                        "CAN driver rejected the ISO-TP frame (Transmit returned 0)."));
                    break;
                default:
                    FailTx(new IsoTpException("ISO-TP TX confirmation failed with unknown reason."));
                    break;
            }
            return;
        }

        switch (kind)
        {
            case TxExpect.SingleFrameConfirm:
                CompleteTx();
                break;

            case TxExpect.FirstFrameConfirm:
                {
                    var tx = expected;
                    // Fix: N_Bs starts after FF TX-confirm, not when FF is
                    // handed to the driver. Apply any FC that arrived during the confirm wait
                    // only now so CF cannot race ahead of FF on the wire.
                    tx.State = TxStage.WaitFcInitial;
                    ApplyDeferredFlowControlsOrArmNBs(tx);
                    break;
                }

            case TxExpect.ConsecutiveFrameConfirm:
                {
                    var tx = expected;
                    tx.Offset += tx.LastCfChunkLen;
                    tx.NextSn = IsoTpFrameCodec.NextConsecutiveSequenceNumber(tx.NextSn);

                    if (tx.Offset >= tx.Pdu.Length)
                    {
                        CompleteTx();
                        break;
                    }

                    // Block accounting: BS==0 => "everything in one block", never wait for FC.
                    if (tx.BlockSize != 0)
                    {
                        tx.CfsInCurrentBlock++;
                        if (tx.CfsInCurrentBlock >= tx.BlockSize)
                        {
                            // Fix: peer may answer FC after the last CF of
                            // the block is on the wire but before our TX-confirm completes.
                            // Those FCs were deferred while State==SendingCf; apply them now
                            // instead of arming N_Bs and timing out on an already-received FC.
                            tx.State = TxStage.WaitFcBlock;
                            tx.CfsInCurrentBlock = 0;
                            ApplyDeferredFlowControlsOrArmNBs(tx);
                            break;
                        }
                    }

                    // Mid-block CF confirm: any FC deferred during SendingCf was unexpected for
                    // a conforming peer; drop it so it cannot be applied at a later WaitFcBlock.
                    tx.DeferredFcs?.Clear();
                    ScheduleNextCf(tx);
                    break;
                }
        }
    }

    private void ScheduleNextCf(TxState tx)
    {
        if (tx.StMin > TimeSpan.Zero)
        {
            // The timer belongs to this transfer: it is disposed on every completion path, and
            // should it still fire, it sends only if this transfer is still the one in flight —
            // never a Consecutive Frame of the transfer that replaced it, carrying the new PDU's
            // bytes under the old sequence number (#25).
            tx.StMinTimer?.Dispose();
            tx.StMinTimer = _actor.Schedule(tx.StMin, () => SendNextConsecutiveFrame(tx));
        }
        else
        {
            SendNextConsecutiveFrame(tx);
        }
    }

    private void ArmNBs()
    {
        var tx = _tx!;
        tx.NBsDeadline?.Dispose();
        tx.NBsDeadline = _deadlines.Arm(_options.NBs, OnNBsExpired);
    }

    private void OnNBsExpired()
    {
        var tx = _tx;
        if (tx is null) return;
        if (tx.State is not (TxStage.WaitFcInitial or TxStage.WaitFcBlock)) return;
        FailTx(new IsoTpTimeoutException(IsoTpTimer.NBs,
            "N_Bs timer expired waiting for peer Flow-Control frame."));
    }

    private void CompleteTx()
    {
        var tx = _tx;
        if (tx is null) return;
        tx.NBsDeadline?.Complete();
        tx.StMinTimer?.Dispose();
        _tx = null;
        tx.Tcs.TrySetResult(new IsoTpTransmitStamps(tx.LastFrameHandoffTimestamp, tx.LastFrameTransmitTimestamp));
    }

    private void FailTx(Exception ex)
    {
        var tx = _tx;
        if (tx is null) return;
        tx.NBsDeadline?.Complete();
        _tx = null;
        tx.Fail(ex);
    }

    private void CancelInFlightSend(TaskCompletionSource<IsoTpTransmitStamps> tcs, CancellationToken ct)
    {
        // Complete the caller's await immediately (any thread). BeginSendOnLoop may still be
        // sitting in the actor mailbox ahead of our cleanup work item; completing `tcs` here
        // lets that begin observe tcs.Task.IsCompleted and refuse to emit.
        tcs.TrySetCanceled(ct);

        // Hop to the actor to tear down any TX state that begin already published. Safe if
        // begin has not run yet (_tx is null / different TCS) or if dispose raced Post.
        try
        {
            _actor.Post(() =>
            {
                var tx = _tx;
                if (tx is not null && ReferenceEquals(tx.Tcs, tcs))
                {
                    tx.NBsDeadline?.Complete();
                    tx.StMinTimer?.Dispose();
                    _tx = null;
                }
            });
        }
        catch (ObjectDisposedException)
        {
            // Channel tearing down; TCS already canceled above.
        }
    }

    // -----------------------------------------------------------------------------------------
    // RX side (all methods run on the actor loop)
    // -----------------------------------------------------------------------------------------

    private void HandleReceivedFrame(byte[] payload, bool isCanFd, long frameArrival,
        IsoTpReceptionInProgress? announce)
    {
        // Frames still in the mailbox when the channel is disposed are drained by the owned
        // actor's final drain; they must not complete a PDU nobody can receive any more (#206).
        if (Volatile.Read(ref _disposed) != 0)
        {
            // A First Frame the reader already announced must not stay reported as in progress.
            if (announce is not null) WithdrawReception(announce);
            return;
        }

        if (!IsoTpFrameCodec.TryParsePci(payload, _endpoint, isCanFd, out var pci))
            return; // truncated / reserved: drop silently (bounds-safe per FR-TP-007)

        // Arrived before the latest DiscardPendingPdus: part of what it was asked to drop,
        // wherever the frame was at the time. Dropped unanswered -- a Flow Control for a
        // First Frame here would invite the rest of a transfer nobody waits for. Flow Control
        // itself is exempt: it belongs to an outbound transfer the discard does not concern.
        if (pci.Type != PciType.FlowControl && frameArrival < Volatile.Read(ref _discardStamp))
        {
            if (announce is not null) WithdrawReception(announce);
            return;
        }

        switch (pci.Type)
        {
            case PciType.SingleFrame:
                HandleRxSingleFrame(payload, pci, frameArrival);
                break;
            case PciType.FirstFrame:
                // The record is null only for a frame the pump saw as stale, which the check
                // above dropped; built here regardless, so the list never holds a null.
                announce ??= new IsoTpReceptionInProgress(frameArrival, pci.Length,
                    new ReadOnlyMemory<byte>(payload, pci.DataOffset,
                        Math.Max(0, payload.Length - pci.DataOffset)));
                if (!TryBeginRx(payload, pci, frameArrival, announce))
                {
                    // Refused, or complete in the one frame (and then already in the inbox):
                    // the reader's record is withdrawn.
                    WithdrawReception(announce);
                }
                break;
            case PciType.ConsecutiveFrame:
                HandleRxConsecutiveFrame(payload, pci, frameArrival);
                break;
            case PciType.FlowControl:
                HandleRxFlowControl(pci);
                break;
        }
    }

    private void HandleRxSingleFrame(byte[] payload, Pci pci, long frameArrival)
    {
        // A racing SF starts a fresh PDU: abort any in-flight reassembly (matches ISO 15765-2
        // §6.5.2's "an unexpected N_PCI type shall abort reception"). Must go through AbortRx —
        // a silent _rx clear leaves ReceiveAsync parked on an empty inbox.
        if (_rx is not null)
        {
            AbortRx(new IsoTpException(
                "ISO-TP Single Frame aborted in-flight multi-frame reception (unexpected N_PCI)."));
        }

        if (pci.DataOffset + pci.Length > payload.Length) return; // codec already validates but guard again
        var pdu = new byte[pci.Length];
        Array.Copy(payload, pci.DataOffset, pdu, 0, pci.Length);
        EmitPdu(pdu, frameArrival);
    }

    // A First Frame announcing no more than a Single Frame of the same CAN_DL could carry is
    // ignored (ISO 15765-2 §9.6.3.1, #56): the capacity is 7 less the address extension in the
    // short PCI form (CAN_DL 8), CAN_DL - 2 less the extension in the escape form beyond it.
    private bool FitsASingleFrame(in Pci pci, int canDl)
    {
        int addrExtBytes = _endpoint.UsesAddressExtension ? 1 : 0;
        int capacity = canDl <= IsoTpFrameCodec.ClassicCanMaxData
            ? IsoTpFrameCodec.ClassicCanMaxData - 1 - addrExtBytes
            : canDl - 2 - addrExtBytes;
        return pci.Length <= capacity;
    }

    // True when a reassembly is now in progress for this First Frame; false when the frame was
    // ignored or refused.
    private bool TryBeginRx(byte[] payload, Pci pci, long frameArrival,
        IsoTpReceptionInProgress announce)
    {
        // Two frames are not First Frames and are ignored -- with no effect on a reassembly in
        // flight, which only a First Frame proper aborts (Bugbot on #147). A First Frame must
        // be a full frame: classic CAN_DL 8, CAN FD at least 8 -- its CAN_DL is the RX_DL every
        // Consecutive Frame of the transfer is held to (ISO 15765-2 §9.8, #27); a shorter one
        // is ignored. And one announcing what a Single Frame of the same CAN_DL could have
        // carried is ignored too (§9.6.3.1, #56): no Flow Control, no reassembly. The Single
        // Frame capacity at that CAN_DL is 7 less the address extension in the short PCI form
        // (CAN_DL 8) and CAN_DL - 2 less the extension in the escape form beyond it.
        if (payload.Length < IsoTpFrameCodec.ClassicCanMaxData) return false;
        if (FitsASingleFrame(pci, payload.Length)) return false;

        // A new FF aborts any half-built reassembly (ISO 15765-2 §6.5.5). AbortRx so a blocked
        // ReceiveAsync observes the drop — including when the new FF is then refused with
        // FC(OVFLW) and no replacement session is started.
        if (_rx is not null)
        {
            AbortRx(new IsoTpException(
                "ISO-TP First Frame aborted in-flight multi-frame reception."));
        }

        // Cap reassembly allocation to the codec limit for this frame kind
        // and to MaxReceivePduLength (#26): a CAN-FD escape FF can announce up to int.MaxValue;
        // refuse with FC(OVFLW) and do not allocate.
        if (pci.Length > MaxPduLength || pci.Length > _options.MaxReceivePduLength)
        {
            SendOverflowFlowControl();
            return false;
        }

        int firstChunk = payload.Length - pci.DataOffset;
        if (firstChunk < 0) firstChunk = 0;
        if (firstChunk > pci.Length) firstChunk = pci.Length;

        byte[] buffer;
        try
        {
            buffer = new byte[pci.Length];
        }
        catch (OutOfMemoryException)
        {
            // CAN-FD escape FF can announce up to int.MaxValue; if the process cannot honor the
            // codec max, refuse with OVFLW rather than faulting the actor loop.
            SendOverflowFlowControl();
            return false;
        }
        Array.Copy(payload, pci.DataOffset, buffer, 0, firstChunk);

        // Reply with FC(CTS, BS, STmin) advertising our own block size / separation time. The
        // announced length exceeds what one frame carries (checked above), so at least one
        // Consecutive Frame follows.
        var fc = IsoTpFrameCodec.BuildFlowControl(_endpoint, FlowStatus.ClearToSend,
            _options.LocalBlockSize, _localStMinRaw,
            _options.UseCanFd, _options.UsePadding, _options.PaddingByte);
        SendUnsequencedFrame(fc);

        _rx = new RxState(buffer, received: firstChunk,
            expectedSn: IsoTpFrameCodec.FirstConsecutiveSequenceNumber,
            blockCounter: _options.LocalBlockSize,
            rxDl: payload.Length,
            announce);
        // The reader published the record; re-published here only if a DiscardPendingPdus
        // posted between the two removed it, so the list states what is in progress.
        PublishReception(announce);
        ArmNCr();
        return true;
    }

    private void HandleRxConsecutiveFrame(byte[] payload, Pci pci, long frameArrival)
    {
        var rx = _rx;
        if (rx is null) return; // stray CF, no reassembly in progress: drop per ISO 15765-2 §6.5.2.

        if (pci.SequenceNumber != rx.ExpectedSn)
        {
            // Sequence-number mismatch (FR-TP-002 negative case). Abort reception: surface via
            // BackgroundExceptionOccurred and fault any blocked ReceiveAsync (FailTx analogue)
            // so the waiter does not hang on _pduInbox. The peer sender
            // will hit its own N_Cr / missing-FC path independently.
            AbortRx(new IsoTpException(
                $"ISO-TP CF sequence-number mismatch: expected {rx.ExpectedSn}, got {pci.SequenceNumber}."));
            return;
        }

        int remaining = rx.Buffer.Length - rx.Received;
        int available = payload.Length - pci.DataOffset;
        // CAN_DL validation (ISO 15765-2 §9.8, #27): a Consecutive Frame that is not the last
        // carries RX_DL bytes, the CAN_DL the First Frame set; the last one carries at least the
        // remaining bytes and no more than RX_DL. Anything else — a shortened CF whose 4 bytes
        // would shift everything after it, a PCI-only CF — is ignored: not
        // copied, no SN or BS advance, N_Cr untouched, as if it had not arrived.
        bool isLast = remaining <= rx.RxDl - pci.DataOffset;
        bool conforming = isLast
            ? available >= remaining && payload.Length <= rx.RxDl
            : payload.Length == rx.RxDl;
        if (!conforming) return;
        int copy = Math.Min(remaining, available);

        Array.Copy(payload, pci.DataOffset, rx.Buffer, rx.Received, copy);
        rx.Received += copy;
        rx.ExpectedSn = IsoTpFrameCodec.NextConsecutiveSequenceNumber(rx.ExpectedSn);

        if (rx.Received >= rx.Buffer.Length)
        {
            rx.CancelDeadline();
            var pdu = rx.Buffer;
            _rx = null;
            // The last consecutive frame's arrival: a PDU is complete when its final frame
            // lands, not when its first one did -- and the first frame's arrival goes with it,
            // for a caller whose deadline ended there (#28).
            EmitPdu(pdu, frameArrival, rx.Announce.FirstFrameArrivalTimestamp);
            // Withdrawn only once the PDU is in the inbox: a caller whose deadline expires in
            // between must find either the reception in progress or the PDU, never neither.
            WithdrawReception(rx.Announce);
            return;
        }

        // BS accounting: LocalBlockSize == 0 means "no further FCs required in this session"
        // (send everything, matching the peer's own semantics when BS=0). Otherwise, decrement
        // and send another FC once we've received a full block.
        if (_options.LocalBlockSize != 0)
        {
            rx.BlockCounter--;
            if (rx.BlockCounter <= 0)
            {
                var fc = IsoTpFrameCodec.BuildFlowControl(_endpoint, FlowStatus.ClearToSend,
                    _options.LocalBlockSize, _localStMinRaw,
                    _options.UseCanFd, _options.UsePadding, _options.PaddingByte);
                SendUnsequencedFrame(fc);
                rx.BlockCounter = _options.LocalBlockSize;
            }
        }

        // Any CF (including the one we just wrote a fresh FC after) refreshes N_Cr for the next CF.
        rx.RearmDeadline(_deadlines, _options.NCr, OnNCrExpired);
    }

    private void HandleRxFlowControl(Pci pci) => HandleRxFlowControl(pci, deferIfBusy: true);

    private void HandleRxFlowControl(Pci pci, bool deferIfBusy)
    {
        var tx = _tx;
        if (tx is null)
            return; // FC with no matching pending TX: drop (matches ISO 15765-2 §6.5.5.2).

        // FF handed to the driver but not yet TX-confirmed: defer FC until FirstFrameConfirm so
        // we neither arm N_Bs early nor emit CF before FF confirm.
        // Queue (not overwrite) so multiple Wait FCs in this window still count toward WftMax
        //.
        if (deferIfBusy && tx.State == TxStage.SingleOrFirstInFlight && tx.Offset > 0)
        {
            (tx.DeferredFcs ??= new List<Pci>()).Add(pci);
            return;
        }

        // Last CF of a block may still be awaiting TX-confirm (State==SendingCf) when the peer
        // already answers with FC. Defer until ConsecutiveFrameConfirm enters WaitFcBlock
        //; also queue so Wait counting is preserved.
        if (deferIfBusy && tx.State == TxStage.SendingCf)
        {
            (tx.DeferredFcs ??= new List<Pci>()).Add(pci);
            return;
        }

        if (tx.State is not (TxStage.WaitFcInitial or TxStage.WaitFcBlock))
            return;

        // Peer FC responded within N_Bs -> stop that timer.
        tx.NBsDeadline?.Complete();
        tx.NBsDeadline = null;

        switch (pci.FlowStatus)
        {
            case FlowStatus.ClearToSend:
                // BS and STmin are the first FC.CTS's for the whole transfer; ISO 15765-2 has
                // the sender ignore the values a later FC.CTS carries (#56).
                if (!tx.FlowParametersTaken)
                {
                    tx.BlockSize = pci.BlockSize;
                    tx.StMin = pci.StMin;
                    tx.FlowParametersTaken = true;
                }
                tx.CfsInCurrentBlock = 0;
                tx.WaitFramesReceived = 0;
                tx.State = TxStage.SendingCf;
                ScheduleNextCf(tx);
                break;

            case FlowStatus.Wait:
                tx.WaitFramesReceived++;
                if (tx.WaitFramesReceived > _options.WftMax)
                {
                    FailTx(new IsoTpWaitFrameLimitExceededException(
                        tx.WaitFramesReceived, _options.WftMax));
                    return;
                }
                // Wait state: re-arm N_Bs and stay in WaitFc* until CTS/Overflow arrives.
                ArmNBs();
                break;

            case FlowStatus.Overflow:
                FailTx(new IsoTpOverflowException(
                    "Peer indicated Flow-Control Overflow (FS=OVFLW); PDU too large for the receiver."));
                break;
        }
    }

    /// <summary>
    /// Applies Flow-Control frames that arrived while FF/CF TX-confirm was still outstanding,
    /// in arrival order so Wait counting toward <see cref="IsoTpChannelOptions.WftMax"/> is
    /// preserved. Arms N_Bs when nothing was deferred.
    /// </summary>
    private void ApplyDeferredFlowControlsOrArmNBs(TxState tx)
    {
        var deferred = tx.DeferredFcs;
        tx.DeferredFcs = null;
        if (deferred is null || deferred.Count == 0)
        {
            ArmNBs();
            return;
        }

        foreach (var pci in deferred)
        {
            if (!ReferenceEquals(_tx, tx))
                return; // FailTx / CompleteTx already cleared this operation
            // deferIfBusy: false — we are already in WaitFc*; do not re-queue into DeferredFcs
            // if a prior CTS in this batch moved State to SendingCf.
            HandleRxFlowControl(pci, deferIfBusy: false);
        }

        // If every deferred FC was ignored (unexpected status / state) and we are still waiting
        // for peer FC without a live N_Bs deadline, arm it now.
        if (ReferenceEquals(_tx, tx)
            && tx.State is (TxStage.WaitFcInitial or TxStage.WaitFcBlock)
            && tx.NBsDeadline is null)
        {
            ArmNBs();
        }
    }

    private void ArmNCr()
    {
        var rx = _rx;
        if (rx is null) return;
        rx.RearmDeadline(_deadlines, _options.NCr, OnNCrExpired);
    }

    private void OnNCrExpired()
    {
        if (_rx is null) return;
        // FR-TP-010: N_Cr expiry must complete the receive with an observable error — not only
        // BackgroundExceptionOccurred (which leaves ReceiveAsync parked on an empty inbox).
        AbortRx(new IsoTpTimeoutException(IsoTpTimer.NCr,
            "N_Cr timer expired waiting for next Consecutive Frame."));
    }

    /// <summary>
    /// Aborts in-flight multi-frame reassembly. Mirrors <see cref="FailTx"/> on the receive side:
    /// clears RX state, raises <see cref="BackgroundExceptionOccurred"/>, and enqueues the fault
    /// into the PDU inbox so a blocked <see cref="ReceiveAsync"/>/<see cref="ReceiveAllAsync"/>
    /// completes with the same exception instead of waiting indefinitely (also covers SF/FF supersede and FF→OVFLW that would otherwise clear
    /// <c>_rx</c> silently). Successful PDUs already in the inbox are unaffected (FIFO); exactly
    /// one waiter consumes the fault item, preserving multi-receive inbox semantics for
    /// subsequent PDUs.
    /// </summary>
    private void AbortRx(Exception ex)
    {
        var rx = _rx;
        if (rx is not null)
        {
            rx.CancelDeadline();
            _rx = null;
        }

        // A producer callback that outlived the disposal queues nothing: the inbox's writer is not
        // completed any more, so this is what keeps the end of the channel the end.
        if (!_inboxClosed)
            _pduInbox.Writer.TryWrite(RxInboxItem.FromError(ex,
                rx?.Announce.FirstFrameArrivalTimestamp ?? Stopwatch.GetTimestamp()));
        // After the error item, for the same reason the completion path withdraws it after the
        // PDU: a waiter that saw the reception in progress must find its outcome in the inbox.
        if (rx is not null)
            WithdrawReception(rx.Announce);
        // Raised last: a handler that discards inline (#56) then finds the fault in the inbox
        // and drains it, instead of the fault landing after the drain and poisoning the next
        // receive.
        RaiseBackgroundException(ex);
    }

    private void SendOverflowFlowControl()
    {
        var ovflw = IsoTpFrameCodec.BuildFlowControl(_endpoint, FlowStatus.Overflow,
            blockSize: 0, stMinRaw: 0,
            _options.UseCanFd, _options.UsePadding, _options.PaddingByte);
        SendUnsequencedFrame(ovflw);
    }

    // Fire-and-forget send of a frame that is NOT part of the current TX PDU (typically a FC we
    // emit while receiving). Confirmation failures don't fail an outbound PDU; they surface as
    // background exceptions instead so a broken FC doesn't kill an unrelated in-flight send.
    private void SendUnsequencedFrame(byte[] payload)
    {
        var frame = _options.UseCanFd
            ? CanFrame.Fd(unchecked((int)_endpoint.TxCanId), payload,
                isExtendedFrame: _endpoint.IsExtendedCanId)
            : CanFrame.Classic(unchecked((int)_endpoint.TxCanId), payload,
                isExtendedFrame: _endpoint.IsExtendedCanId);
        var timeout = _options.NAs;

        _ = Task.Run(async () =>
        {
            try
            {
                var conf = await _service.SendConfirmedAsync(frame, timeout).ConfigureAwait(false);
                if (!conf.Confirmed)
                {
                    RaiseBackgroundException(new IsoTpException(
                        $"ISO-TP unsequenced-frame send failed: {conf.FailureReason}."));
                }
            }
            catch (Exception ex)
            {
                RaiseBackgroundException(ex);
            }
        });
    }

    private void EmitPdu(byte[] pdu, long frameArrival) => EmitPdu(pdu, frameArrival, frameArrival);

    private void EmitPdu(byte[] pdu, long frameArrival, long firstFrameArrival)
    {
        // Enqueue first so ReceiveAsync/ReceiveAllAsync can observe the PDU even if a
        // DatagramReceived handler blocks. Raise the event off the actor loop so a sync wait
        // on ReceiveAsync / SendAsync / DiscardPendingPdus cannot deadlock the mailbox
        //.
        // As in AbortRx: no PDU is queued once the channel is disposed.
        if (!_inboxClosed)
            _pduInbox.Writer.TryWrite(RxInboxItem.FromPdu(pdu, frameArrival, firstFrameArrival));

        var handler = DatagramReceived;
        if (handler is null || Volatile.Read(ref _disposed) != 0)
            return;

        var endpoint = _endpoint;
        _ = Task.Run(() =>
        {
            // No disposed check here: a start racing Dispose cannot be excluded without holding a
            // lock across the handler, and the check above already stops emission once
            // disposal has begun (#206).
            try
            {
                handler.Invoke(this, new IsoTpDatagramReceivedEventArgs(endpoint, pdu));
            }
            catch (Exception ex)
            {
                RaiseBackgroundException(ex);
            }
        });
    }

    // -----------------------------------------------------------------------------------------
    // Nested types
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// One inbox delivery: either a reassembled PDU or a reassembly-abort fault.
    /// </summary>
    private readonly struct RxInboxItem
    {
        private RxInboxItem(byte[]? pdu, Exception? error, long arrivalTimestamp, long firstFrameArrivalTimestamp)
        {
            Pdu = pdu;
            Error = error;
            ArrivalTimestamp = arrivalTimestamp;
            FirstFrameArrivalTimestamp = firstFrameArrivalTimestamp;
        }

        /// <summary>The arrival stamp of the PDU's first frame (see <see cref="IsoTpReceivedPdu.FirstFrameArrivalTimestamp"/>).</summary>
        public long FirstFrameArrivalTimestamp { get; }

        public byte[]? Pdu { get; }
        public Exception? Error { get; }

        /// <summary>
        /// When this item was created, from <see cref="Stopwatch.GetTimestamp"/>. Stamped at
        /// enqueue rather than at delivery: the two differ by however long the reader was
        /// descheduled, and a deadline that measures the second one is not a deadline.
        /// </summary>
        public long ArrivalTimestamp { get; }

        public static RxInboxItem FromPdu(byte[] pdu, long arrivalTimestamp, long firstFrameArrivalTimestamp)
            => new(pdu, null, arrivalTimestamp, firstFrameArrivalTimestamp);

        /// <summary>An aborted reassembly's fault, stamped with the First Frame arrival of the
        /// reception it ends, so a discard tells its reception's age.</summary>
        public static RxInboxItem FromError(Exception error, long firstFrameArrival)
            => new(null, error, Stopwatch.GetTimestamp(), firstFrameArrival);
    }

    private enum TxStage
    {
        SingleOrFirstInFlight = 0,
        WaitFcInitial = 1,
        SendingCf = 2,
        WaitFcBlock = 3,
    }

    private sealed class TxState
    {
        public TxState(byte[] pdu, TaskCompletionSource<IsoTpTransmitStamps> tcs)
        {
            Pdu = pdu;
            Tcs = tcs;
            State = TxStage.SingleOrFirstInFlight;
        }

        public byte[] Pdu { get; }
        public TaskCompletionSource<IsoTpTransmitStamps> Tcs { get; }

        /// <summary>
        /// <see cref="Stopwatch.GetTimestamp"/> reading reported by the bus for the most recent
        /// frame of this PDU it handed to the driver. Written and read on the actor loop only.
        /// When the send completes, the last value written is the last frame's -- which is the
        /// instant P2 starts.
        /// </summary>
        public long LastFrameTransmitTimestamp { get; set; }
        /// <summary>Just before the latest frame was handed to the driver (#146).</summary>
        public long LastFrameHandoffTimestamp { get; set; }

        public TxStage State { get; set; }
        public int Offset { get; set; }
        public byte NextSn { get; set; }
        public int LastCfChunkLen { get; set; }

        // Peer-provided from the last CTS-FC.
        public byte BlockSize { get; set; }
        public TimeSpan StMin { get; set; }
        /// <summary>Whether the first FC.CTS of this transfer has set BS and STmin.</summary>
        public bool FlowParametersTaken { get; set; }

        // Wait frame tracking (FR-TP-011).
        public int WaitFramesReceived { get; set; }
        public int CfsInCurrentBlock { get; set; }

        public IDeadline? NBsDeadline { get; set; }

        /// <summary>The STmin timer armed for this transfer's next Consecutive Frame, disposed
        /// with the transfer so it cannot fire into the next one (#25).</summary>
        public IDisposable? StMinTimer { get; set; }

        /// <summary>
        /// Flow-Control frames that arrived while FF/last-CF-of-block TX confirmation was still
        /// outstanding. Kept as a list so multiple Wait FCs in
        /// that window still count toward WftMax.
        /// </summary>
        public List<Pci>? DeferredFcs { get; set; }

        public void Fail(Exception ex)
        {
            NBsDeadline?.Dispose();
            NBsDeadline = null;
            StMinTimer?.Dispose();
            StMinTimer = null;
            Tcs.TrySetException(ex);
        }
    }

    private sealed class RxState
    {
        public RxState(byte[] buffer, int received, byte expectedSn, int blockCounter, int rxDl,
            IsoTpReceptionInProgress announce)
        {
            Buffer = buffer;
            Received = received;
            ExpectedSn = expectedSn;
            BlockCounter = blockCounter;
            RxDl = rxDl;
            Announce = announce;
        }

        /// <summary>The record published for this reception's First Frame; its arrival stamp is
        /// delivered with the PDU (#28).</summary>
        public IsoTpReceptionInProgress Announce { get; }

        public byte[] Buffer { get; }

        /// <summary>The First Frame's CAN_DL: the data length every Consecutive Frame of this
        /// transfer but the last must have (ISO 15765-2 §9.8).</summary>
        public int RxDl { get; }
        public int Received { get; set; }
        public byte ExpectedSn { get; set; }
        public int BlockCounter { get; set; }
        public IDeadline? Deadline { get; private set; }

        public void RearmDeadline(DeadlineScheduler scheduler, TimeSpan timeout, Action onExpired)
        {
            var existing = Deadline;
            if (existing is not null && !existing.IsExpired && !existing.IsCancelled && existing.Rearm(timeout)) return;
            existing?.Dispose();
            Deadline = scheduler.Arm(timeout, onExpired);
        }

        public void CancelDeadline()
        {
            Deadline?.Dispose();
            Deadline = null;
        }
    }
}
