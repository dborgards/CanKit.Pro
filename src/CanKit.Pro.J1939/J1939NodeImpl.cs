using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Pro.Actor;
using CanKit.Pro.Addressing;
using CanKit.Pro.J1939Tp;
using CanKit.Pro.RawCan;
using CanKit.Pro.Reliability;

namespace CanKit.Pro.J1939;

/// <summary>
/// Actor-driven <see cref="IJ1939Node"/> implementation that composes on the CanKit.Pro L2
/// services (<see cref="ICanBusService"/>, <see cref="IProtocolActor"/>,
/// <see cref="DeadlineScheduler"/>) and delegates multi-frame (&gt; 8 bytes) payloads to a
/// shared <see cref="IJ1939TpChannel"/> per SRS FR-J1939-006.
/// </summary>
/// <remarks>
/// <para>
/// The node subscribes to every extended-ID frame on the bus and classifies each one on the
/// actor loop (single-writer discipline for all node state — claim state, address, pending
/// claim TCS): Address Claim / Cannot Claim (PGN 0xEE00) drives the state machine, Request-PGN
/// (0xEA00) and every other application PGN targeted at us (destination = our SA) or the
/// global address (0xFF) surfaces via <see cref="MessageReceived"/>. TP.CM / TP.DT frames go
/// through the shared J1939-TP channel and their reassembled datagrams show up as
/// <see cref="J1939Message"/> events too.
/// </para>
/// <para>
/// Outbound routing is entirely payload-length based (FR-J1939-006): &lt;= 8 bytes goes on the
/// wire as one 29-bit CAN frame; &gt; 8 bytes is handed to the transport channel which sends
/// TP.BAM for global destinations or TP.CM for a specific destination.
/// </para>
/// </remarks>
internal sealed class J1939NodeImpl : IJ1939Node
{
    private readonly ICanBusService _service;
    private readonly bool _ownsService;

    // True when this node created its own actor and must therefore dispose it. An injected actor
    // belongs to whoever built it, exactly as _ownsService works for the bus service.
    private readonly bool _ownsActor;

    // The clock the actor measures its timers against, read off the actor so the node's own
    // fixed-rate anchor cannot end up on a different one (#92).
    private readonly ITimeSource _time;

    private int _periodicEmissionsCompleted;

    /// <summary>
    /// How many periodic emissions have finished sending. A seam for tests, and specifically for
    /// the one thing a virtual clock cannot observe: OnTick drops a tick whose previous emission
    /// is still in flight, and "in flight" ends on the transport's own loop and the thread pool,
    /// neither of which a test clock governs. Without this a test would have to guess with a
    /// delay, and guessing wrong drops the tick it was about to assert on.
    /// </summary>
    internal int PeriodicEmissionsCompleted => Volatile.Read(ref _periodicEmissionsCompleted);

    private int _periodicEmissionsStarted;

    /// <summary>
    /// How many periodic emissions a tick has started. A seam for tests: counted on the actor,
    /// in the tick itself, so once a test has moved the clock past a tick the count says whether
    /// that tick started an emission or dropped it -- the emission's own effect on the bus is
    /// asynchronous and cannot answer "none" without a guess at how long to wait.
    /// </summary>
    internal int PeriodicEmissionsStarted => Volatile.Read(ref _periodicEmissionsStarted);

    /// <summary>Ticks of <see cref="_time"/> as a <see cref="TimeSpan"/>.</summary>
    private TimeSpan TimeSpanFromTicks(long ticks)
        => TickMath.ElapsedFromTicks(ticks, _time.Frequency);
    private readonly J1939NodeOptions _options;
    private readonly J1939Name _name;
    private readonly ProtocolActor _actor;
    private readonly DeadlineScheduler _deadlines;
    private readonly ISubscription _subscription;
    // The transport channel and its reader task are recreated whenever the node's claimed
    // address changes: J1939TpChannel uses its SourceAddress as the local channel identity and
    // accepts inbound TP frames whose destination address is that identity (or global 0xFF), so
    // after a successful claim we must re-open the channel on the claimed address (Bugbot
    // 3600377721). Mutation of these two fields only happens on the actor loop.
    private IJ1939TpChannel _transport = null!;
    // False from the moment RebindTransportOnLoop disposes _transport until a new channel is in
    // place. A failed reopen leaves the disposed channel in the field, still carrying its address,
    // and the rebind's shortcut must not take it for a live one (#375). Actor loop only.
    private bool _transportLive = true;
    // Null in production; see the constructor.
    private readonly Func<byte, IJ1939TpChannel>? _openTransport;
    private Task _transportReaderTask = null!;
    private readonly Task _readerTask;
    private readonly CancellationTokenSource _readerCts = new();

    private PendingClaim? _pendingClaim;
    private int _disposed;

    /// <inheritdoc />
    public byte? Address
    {
        get
        {
            int v = Volatile.Read(ref _addressStore);
            return v < 0 ? null : (byte)v;
        }
    }

    // Backing store: byte doesn't have a null representation, so we use an int-alike.
    // A negative value means "unclaimed". Reads via Volatile so callers on any thread see the
    // most recently committed value; writes happen only on the actor loop.
    private int _addressStore = -1;

    // Single-frame application PGNs this node has transmitted and not yet seen come back.
    // A received frame equal to one of them is this node's own echo, whatever address the
    // node holds when the echo is finally processed (#121). Bounded, oldest out: an entry
    // only matters while the echo could still arrive, and a bus that never echoes would
    // otherwise grow it without end. Bounded in time as well, as the claim markers are (#355,
    // J-1): a send the driver rejected never echoes and drops its entry at once, and any other
    // outcome lets it match for ClaimEchoGrace after the send returned, measured on the actor's
    // clock and checked when a frame is matched -- on a bus that does not echo an entry otherwise
    // waited for 64 newer sends, and swallowed a peer's identical frame under an address the node
    // had since left. Written from the send path, read and pruned on the actor loop. Address Claim / Cannot Claim do not live here: a burst of ordinary sends
    // would evict one, and the next echo of our own claim would look like a peer with our
    // NAME (#180).
    private readonly List<OwnFrame> _ownFrames = new();
    private readonly object _ownFramesGate = new();
    private const int OwnFramesCapacity = 64;

    // Outstanding Address Claim / Cannot Claim transmits, one object per send. Actor loop
    // only. Not capped and not shared with `_ownFrames`: a Request-for-Address-Claimed
    // burst has to be able to answer without pushing a claim marker out before its echo
    // comes back, or that echo is arbitrated as an equal-NAME peer and the node Cannot
    // Claims itself (#180). Each marker is removed by its own echo. A send the driver
    // rejected never echoes and drops its marker at once; a send that timed out may still
    // echo, so its marker stays until that echo or until ClaimEchoGrace, and a retry's
    // marker is not the one a content match spends on it.
    private readonly List<ClaimEcho> _claimEchoes = new();

    // Every grace armed for a claim echo, including one whose marker the echo already took.
    // Dispose cancels these; the callback itself no-ops when the marker is gone (#180).
    private readonly List<IDeadline> _claimEchoGraces = new();

    // How long a timed-out claim's marker stays after SendConfirmedAsync gives up. The frame was
    // accepted, so the echo can still arrive and has to be recognised as ours; past this
    // the marker is retired by identity. While a newer identical send is outstanding the
    // retirement waits, or the late echo would take that newer marker (#180).
    internal static readonly TimeSpan ClaimEchoGrace = TimeSpan.FromSeconds(1);

    /// <inheritdoc />
    public J1939Name Name => _name;

    /// <inheritdoc />
    public J1939ClaimState ClaimState => (J1939ClaimState)Volatile.Read(ref _claimStateStore);
    private int _claimStateStore;

    /// <inheritdoc />
    public J1939NodeOptions Options => _options;

    /// <inheritdoc />
    public event EventHandler<J1939Message>? MessageReceived;

    /// <inheritdoc />
    public event EventHandler<J1939ClaimEventArgs>? AddressClaimChanged;

    /// <inheritdoc />
    public event EventHandler<Exception>? BackgroundExceptionOccurred;

    /// <summary>
    /// Builds a node on <paramref name="service"/>. A null <paramref name="actor"/> -- the only
    /// value production passes -- makes the node create and own its own loop; an injected one
    /// stays the caller's to dispose.
    /// </summary>
    /// <remarks>
    /// A seam for tests, not a feature: substituting a loop built on a hand-driven monotonic
    /// source is what lets the fixed-rate periodic schedule be asserted without measuring
    /// wall-clock gaps on a shared runner (#92). The node's own anchor arithmetic reads the
    /// clock off the actor rather than taking one of its own, so the two cannot be given
    /// different clocks.
    /// <paramref name="openTransport"/> is the same kind of seam: it opens the node's own TP
    /// channel -- the one seeded here and replaced at every rebind -- in place of
    /// <c>J1939Tp.Open</c>, so a test can hand the transport reader a datagram at the instant a
    /// race needs it, after a rebind or after the actor is gone, instead of hoping the host
    /// lands there (#195). Null, the only value production passes, opens the real channel on
    /// <paramref name="service"/>. Every TP session the node sends runs on that channel: a send
    /// that finds it bound to another address than the one it captured is refused, not given a
    /// channel of its own, and the real channel refuses on its own loop a session that would
    /// start after the address was lost (#347). A substitute opened through the seam is not
    /// told; a test of that refusal opens the real one.
    /// <paramref name="ownsActor"/> hands an injected <paramref name="actor"/> to the node to
    /// dispose, as the one it creates for itself: a seam for a test that needs the node's own
    /// disposal of its actor on a loop with a short shutdown bound.
    /// </remarks>
    internal J1939NodeImpl(ICanBusService service, J1939NodeOptions options, bool ownsService,
        ProtocolActor? actor = null, Func<byte, IJ1939TpChannel>? openTransport = null, bool ownsActor = false)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _ownsService = ownsService;
        _name = options.Name;
        _openTransport = openTransport;

        _ownsActor = actor is null || ownsActor;
        _actor = actor ?? new ProtocolActor();
        _time = _actor.TimeSource;
        _actor.BackgroundExceptionOccurred += OnActorBackgroundException;
        _deadlines = new DeadlineScheduler(_actor);

        // The TP channel is initially bound to the null address 0xFE because the node does not
        // yet have a claimed address. J1939TpChannel filters inbound TP frames by destination
        // address (the PDU1 PS byte), accepting frames directed to its channel identity or the
        // global address (0xFF), so at 0xFE it still receives BAM traffic. Directed TP.CM to a
        // claimed address cannot arrive until we re-open the channel with that identity
        //. RebindTransportOnLoop does exactly that at each ClaimState
        // transition; here we just seed the initial placeholder channel.
        try
        {
            _transport = OpenTransport(J1939Pgn.NullAddress);
        }
        catch
        {
            _actor.BackgroundExceptionOccurred -= OnActorBackgroundException;
            if (_ownsActor) _actor.Dispose();
            throw;
        }
        _transport.BackgroundExceptionOccurred += OnTransportBackgroundException;

        try
        {
            // Subscribe to every extended-ID frame and classify on the actor loop. A single
            // predicate-less subscription is simpler than three overlapping mask filters and
            // still allocation-light: CanFrameEvent is a readonly struct passed by ref via the
            // subscription's async enumerator (FR-RAW-010/011).
            //
            // Echoes are asked for on purpose. `IsEcho` marks what this HOST transmitted, not
            // what this NODE transmitted, and J1939Node.Open documents that several nodes with
            // different NAME identities may share one service. Filtering on the host bit would
            // drop a sibling node's Address Claim, so two local nodes contending for the same
            // address would both believe they had won it without ever arbitrating their NAMEs.
            //
            // Self-identification stays where it can actually be done -- at the node, by the
            // frame this node transmitted, in HandleIncomingAddressClaim. Equal NAME is another
            // CA (SAE J1939-81 §4.4.2, #168).
            _subscription = _service.Subscribe(f => f.Frame.IsExtendedFrame, includeEcho: true);
        }
        catch
        {
            _transport.Dispose();
            _actor.BackgroundExceptionOccurred -= OnActorBackgroundException;
            if (_ownsActor) _actor.Dispose();
            throw;
        }

        _readerTask = Task.Run(RunReaderAsync);
        _transportReaderTask = StartTransportReader(_transport);
    }

    // =========================================================================================
    // Address Claim (SRS FR-J1939-003 / FR-J1939-004)
    // =========================================================================================

    /// <inheritdoc />
    public Task ClaimAddressAsync(byte preferredAddress, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (preferredAddress == J1939Pgn.GlobalAddress || preferredAddress == J1939Pgn.NullAddress)
            throw new ArgumentOutOfRangeException(nameof(preferredAddress),
                $"Preferred address must be in [0x00, 0xFD]; 0xFE (Null) and 0xFF (Global) are reserved.");

        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration ctr = default;
        if (cancellationToken.CanBeCanceled)
        {
            // On user cancel we must both complete the returned task and tear down the pending
            // claim on the actor loop so `OnClaimAnnounceElapsed` cannot later commit the
            // preferred address after the caller already saw a cancellation (Bugbot
            // 3600440955). The actor post is best-effort — if the node has already been
            // disposed the mailbox is closed and there is no pending state to unwind anyway.
            ctr = cancellationToken.Register(static state =>
            {
                var box = (ClaimCancelState)state!;
                try
                {
                    box.Node._actor.Post(() => box.Node.CancelPendingClaimOnLoop(box.Tcs));
                }
                catch (ObjectDisposedException) { /* actor already gone */ }
                box.Tcs.TrySetCanceled();
            }, new ClaimCancelState(this, tcs));
        }

        _actor.Post(() => BeginClaim(preferredAddress, tcs, ctr));
        return tcs.Task;
    }

    // Runs on the actor loop. Tears down `_pendingClaim` when its owning caller cancels the
    // ClaimAddressAsync task, so the arbitration timer cannot later commit the address after
    // the caller has already observed a cancellation.
    private void CancelPendingClaimOnLoop(TaskCompletionSource<object?> tcs)
    {
        if (_disposed != 0) return;
        var pending = _pendingClaim;
        if (pending is null)
        {
            // OnClaimAnnounceElapsed can race the cancel post and consume
            // `_pendingClaim` on its early-return path (its check for TCS-already-completed
            // fires because the token registration called TrySetCanceled *before* posting
            // the cancel). In that case the deadline callback already cleared the pending
            // slot but returned without touching ClaimState, so without this defensive
            // sweep the node stays wedged in Claiming with no address. Rolling back to
            // NotClaimed here (BeginClaim already invalidated the address and rebound the
            // TP channel to 0xFE) is idempotent and safe if OnClaimAnnounceElapsed already
            // committed the same transition.
            if ((J1939ClaimState)Volatile.Read(ref _claimStateStore) == J1939ClaimState.Claiming)
            {
                WriteAddress(null);
                RebindTransportOnLoop(J1939Pgn.NullAddress);
                SetClaimState(J1939ClaimState.NotClaimed, address: null,
                    contendingSa: null, contendingName: null);
            }
            return;
        }
        // A newer ClaimAddressAsync may have replaced this pending claim already; in that case
        // the fresh claim owns the actor state and we must not disturb it.
        if (!ReferenceEquals(pending.Tcs, tcs)) return;

        _pendingClaim = null;
        pending.Deadline?.Dispose();
        pending.CtRegistration.Dispose();

        // BeginClaim already invalidated the address (WriteAddress(null)) and rebound the TP
        // channel back to the 0xFE placeholder before the arbitration announce, so rolling
        // the state machine back to NotClaimed on cancel is sufficient — there is no prior
        // Claimed state left to restore even for a re-claim on top of a previously-claimed
        // node.
        SetClaimState(J1939ClaimState.NotClaimed, address: null,
            contendingSa: null, contendingName: null);
        pending.Tcs.TrySetCanceled();
    }

    private void BeginClaim(byte preferredAddress, TaskCompletionSource<object?> tcs,
        CancellationTokenRegistration ctr)
    {
        if (_disposed != 0)
        {
            ctr.Dispose();
            tcs.TrySetException(new ObjectDisposedException(nameof(J1939NodeImpl)));
            return;
        }
        if (tcs.Task.IsCompleted)
        {
            ctr.Dispose();
            return;
        }

        // A Cannot Claim still waiting its backoff is overtaken by this claim (Codex on #153).
        // Dropping it settles the loss that scheduled it just as finally as sending it would,
        // so the caller waiting on that loss is answered here. One already handed to the driver
        // is not: that caller is answered when *its* handoff finishes, and faulting it here
        // would let a dispose on the exception suppress the frame. A later loss may already
        // have replaced `_lostClaim`; only a claim whose send is already in flight is left
        // alone. Every such send is remembered, not just the latest: a second loss overwrites
        // `_lostClaim` and the first caller would otherwise have nothing left to complete it.
        _cannotClaimBackoff?.Dispose();
        _cannotClaimBackoff = null;
        if (_lostClaim is { } lost && !_cannotClaimsAwaitingHandoff.Contains(lost))
            CompleteLostClaim();

        // A claim still in arbitration is not silently replaced: the caller who started it
        // is waiting on it, and a second one is a programming error (#58). A re-claim on a
        // node that holds an address, or holds none, is what this method is for. A pending
        // claim whose task has already completed -- cancelled by its caller, whose cancel is
        // still on its way to this loop -- is over, and is swept here so that awaiting the
        // cancellation and claiming again is race-free.
        //
        // The automatic move after an unseat is swept as well, wherever it is -- waiting its
        // backoff or in its announcement window (#355, J-5). Nobody awaits it, so replacing it
        // answers no caller wrongly, and this claim is the application's explicit word on which
        // address the node should hold. It used to fault with advice to wait for
        // AddressClaimChanged, which left the application nothing to do but poll. The move's task
        // is cancelled, not faulted: it did not fail, and ReportUnattendedClaimFailure reports
        // faults only.
        if (_pendingClaim is { } inFlight)
        {
            bool unattended = ReferenceEquals(inFlight.Tcs, _unattendedClaim);
            if (!inFlight.Tcs.Task.IsCompleted && !unattended)
            {
                ctr.Dispose();
                tcs.TrySetException(new InvalidOperationException(
                    $"An address claim for SA 0x{inFlight.PreferredAddress:X2} is in arbitration; await it, or cancel it, before claiming again."));
                return;
            }
            _pendingClaim = null;
            inFlight.Deadline?.Dispose();
            inFlight.CtRegistration.Dispose();
            if (unattended)
            {
                _unattendedClaim = null;
                inFlight.Tcs.TrySetCanceled();
            }
        }

        BeginClaimRound(preferredAddress, tcs, ctr);
    }

    // J1939-81 §4.4.4.3: a node that lost arbitration delays its Cannot Claim, and an
    // arbitrary-address node its next claim, by a pseudo-random 0..153 ms derived from its
    // NAME -- the low byte of the eight bytes' sum, times 0.6 ms, so 255 slots reach the
    // 153 ms endpoint (Codex on #153: modulo 255 mapped a sum of 255 to zero) -- so two nodes
    // colliding on an address do not answer in lockstep for ever (#58). Fixed by the NAME, so
    // a test can choose it; scheduled on the actor, so the state it acts on is the state it
    // read.
    private TimeSpan ClaimBackoff
    {
        get
        {
            int sum = 0;
            foreach (var b in _name.ToBytes()) sum += b;
            return TimeSpan.FromMilliseconds((sum & 0xFF) * 0.6);
        }
    }

    // The delayed Cannot Claim of a lost arbitration, tied to the loss that scheduled it: a
    // new claim cancels it -- its announcement is the newer word on the bus, and a Cannot
    // Claim after it would retract it -- and a second loss reschedules a full backoff rather
    // than inheriting the remainder of the first (Codex on #153, twice). The state check is
    // the second line.
    private IDeadline? _cannotClaimBackoff;

    private void ScheduleCannotClaim()
    {
        // Nothing to dispose. A backoff the new claim overtook was cancelled in BeginClaim,
        // and an unseating starts from Claimed, which has none armed. The field is null here;
        // disposing a handle that is never non-null was a branch nothing reached.
        _cannotClaimBackoff = AfterClaimBackoff(SendCannotClaimIfStillDue);
    }

    // The answer a node without an address owes a Request for Address Claimed waits the same
    // backoff, and for a reason the loss path only shares: a Cannot Claim carries the null
    // address, so two nodes answering one global request at the same instant put identical
    // CAN IDs with different NAMEs on the bus, which arbitration cannot separate (SAE
    // J1939-81 §4.4.4.3; Codex on #153). An answer already waiting is the answer -- sending
    // now would both bypass that delay and make the armed one a second copy.
    private void AnswerRequestWithCannotClaim()
    {
        if (_cannotClaimBackoff is not null) return;
        _cannotClaimBackoff = AfterClaimBackoff(SendCannotClaimIfStillDue);
    }

    private void SendCannotClaimIfStillDue()
    {
        _cannotClaimBackoff = null;
        // Claimed: the claim is the newer word and a Cannot Claim would retract it. Claiming:
        // the round in hand announces, and a claim overtook this one (Codex on #153). The
        // arming path disposes the handle before either transition; this is the second line
        // for a callback already in flight. NotClaimed passes, because that is a node that
        // never claimed answering a scan.
        var state = (J1939ClaimState)Volatile.Read(ref _claimStateStore);
        if (state == J1939ClaimState.Claimed || state == J1939ClaimState.Claiming)
        {
            CompleteLostClaim();
            return;
        }

        // Fault only after SendConfirmedAsync has handed the frame to the driver. Completing
        // the loss beside the fire-and-forget start lets a caller dispose on the exception
        // while that handoff is still pending, and the frame is never sent (Codex on #153).
        // The continuation closes over *this* loss. A second loss may replace `_lostClaim`
        // before SendConfirmedAsync returns; completing whatever is stored then faults the wrong
        // claim and leaves the first one waiting forever (Codex and Bugbot on #153).
        var owed = _lostClaim;
        if (owed is not null)
            _cannotClaimsAwaitingHandoff.Add(owed);
        SendAddressClaimFrame(sourceAddress: J1939Pgn.NullAddress, afterHandoff: () => OnCannotClaimHandoff(owed));
    }

    // The claim of a loss that owes the bus a Cannot Claim: it faults once that frame has been
    // handed to the driver, not when the loss became known. A caller that disposes the node on
    // the exception -- a `using` scope ending on it -- would otherwise tear the actor down
    // inside the backoff, or inside the send, and the frame the loss owes would never be sent
    // at all (Codex on #153).
    private PendingClaim? _lostClaim;

    // Every loss whose Cannot Claim is between the send and the handoff continuation, oldest
    // first. A claim that starts in that interval must not answer these: the frame is already
    // with the driver. One slot is not enough — a second loss replaces `_lostClaim` and, once
    // its own send starts, would drop the only field that still pointed at the first caller.
    private readonly List<PendingClaim> _cannotClaimsAwaitingHandoff = new();

    private void OnCannotClaimHandoff(PendingClaim? owed)
    {
        if (owed is null) return;
        FaultLostClaim(owed);
    }

    private void CompleteLostClaim()
    {
        var lost = _lostClaim;
        if (lost is null) return;
        FaultLostClaim(lost);
    }

    private void FaultLostClaim(PendingClaim lost)
    {
        if (ReferenceEquals(_lostClaim, lost))
            _lostClaim = null;
        _cannotClaimsAwaitingHandoff.RemoveAll(waiting => ReferenceEquals(waiting, lost));
        lost.CtRegistration.Dispose();
        lost.Tcs.TrySetException(new J1939CannotClaimException(lost.RequestedAddress));
    }

    private IDeadline? AfterClaimBackoff(Action onLoop)
    {
        var delay = ClaimBackoff;
        if (delay <= TimeSpan.Zero) { onLoop(); return null; }
        return _deadlines.Arm(delay, () => { if (_disposed == 0) onLoop(); });
    }

    // Registers `retry` as the claim in hand and starts its round after the backoff; a Request
    // for Address Claimed or a contest for the candidate starts it at once (Codex on #153).
    private void BeginClaimRoundAfterBackoff(PendingClaim retry, byte candidate, byte? scanStart)
    {
        _pendingClaim = retry;
        void Start()
        {
            if (!ReferenceEquals(_pendingClaim, retry) || retry.Tcs.Task.IsCompleted) return;
            retry.StartRound = null;
            retry.Deadline?.Dispose();
            retry.Deadline = null;
            BeginClaimRound(candidate, retry.Tcs, retry.CtRegistration, scanStart, retry.RequestedAddress);
        }
        retry.StartRound = Start;
        retry.Deadline = AfterClaimBackoff(Start);
    }

    // Starts (or restarts, for the arbitrary-address fallback) a single arbitration round for
    // `preferredAddress` on the actor loop. Unlike BeginClaim it does not cancel the caller's
    // pending claim — the same TCS (and the scan state) is carried across retries.
    private void BeginClaimRound(byte preferredAddress, TaskCompletionSource<object?> tcs,
        CancellationTokenRegistration ctr, byte? scanStart = null, byte? requestedAddress = null)
    {
        if (_disposed != 0)
        {
            ctr.Dispose();
            tcs.TrySetException(new ObjectDisposedException(nameof(J1939NodeImpl)));
            return;
        }
        if (tcs.Task.IsCompleted)
        {
            ctr.Dispose();
            return;
        }

        // Whether an equal NAME remembered for this address is recent enough to be the race with
        // this claim (J-7, below) is decided on entry: the state handlers and the transport
        // rebind that follow run application code and join threads, and either can outlast the
        // window by itself (Codex on #397).
        bool equalNameIsRecent = _equalNameHeardOn == preferredAddress
            && _time.GetTimestamp() - _equalNameHeardAt <= TickMath.ToTicks(_options.ClaimAnnounceTimeout, _time.Frequency);

        // Invalidate any previously-claimed address *before* announcing the new preferred SA:
        // application traffic must not race and go out on the old SA while the wire already
        // advertises a different preferred address. SendCoreAsync gates on ClaimState==Claimed
        // as well, but clearing the address here also makes the Address getter honest during
        // the arbitration window.
        WriteAddress(null);
        // Publish Claiming *before* the (potentially long) transport rebind so observers never
        // see ClaimState==Claimed with Address==null during a re-claim.
        SetClaimState(J1939ClaimState.Claiming, preferredAddress, contendingSa: null, contendingName: null);

        // An equal NAME heard on some other address was the race with a claim queued behind
        // that frame. This round is a different address, so the observation is over: keeping
        // it would make a later claim of the remembered address lose with no one still on it
        // (#180). A match stays; it is consumed once the announce below has gone out.
        if (_equalNameHeardOn is byte heardOn && heardOn != preferredAddress)
            _equalNameHeardOn = null;
        // Unbind the transport from any prior claimed address so we do not accept directed
        // TP.CM for the old address during the new arbitration window. Placeholder 0xFE still
        // receives broadcast TP.BAM traffic.
        RebindTransportOnLoop(J1939Pgn.NullAddress);

        // Register the pending claim *before* TX so contending peers that arrive during the
        // SendConfirmedAsync await are still handled. Arm the arbitration deadline only after the
        // claim frame is confirmed on the bus — otherwise a failed/slow TX would still let
        // OnClaimAnnounceElapsed commit Claimed.
        _pendingClaim = new PendingClaim(preferredAddress, tcs, deadline: null, ctr)
        {
            ArbitraryScanStart = scanStart,
            RequestedAddress = requestedAddress ?? preferredAddress,
        };
        TransmitAddressClaimConfirmed(sourceAddress: preferredAddress, _pendingClaim);

        // The peer's claim may already have been through this loop, before the claim was
        // pending. Their NAME is not lower than ours, so §4.4.3.3 does not have them
        // retransmit it — this round would otherwise finish uncontested. The announce above
        // is what unseats them; the loss here is ours. A value still stored is this address:
        // one heard on any other address was cleared before the announce. Only a recent one,
        // though: a claim queued behind that frame starts within one announce window, and an
        // observation older than that is a CA that may long have gone -- it used to make a
        // claim an hour later lose at once (#355, J-7).
        if (_equalNameHeardOn is not null)
        {
            _equalNameHeardOn = null;
            if (equalNameIsRecent) LoseEqualNameContest(preferredAddress, _name);
        }
    }

    // J1939-81 §4.5: the arbitrary address field spans 0x80..0xF7.
    private const byte ArbitraryAddressRangeStart = 0x80;
    private const byte ArbitraryAddressRangeEnd = 0xF7;

    // FR-J1939-004: explicit option wins; otherwise the NAME's Arbitrary Address Capable bit
    // decides (J1939-81 §4.5.1).
    private bool ArbitraryClaimingEnabled
        => _options.EnableArbitraryAddressClaiming ?? _name.ArbitraryAddressCapable;

    // Advances the arbitrary-address scan to the next candidate, wrapping once through the
    // whole field. Records the scan start on first use (via <paramref name="scanStart"/>) and
    // returns false when the scan would come back to it — meaning every address in the field
    // was contested and FR-J1939-004's Cannot-Claim applies. The start is the address just lost
    // when that lies in the field, since it has already been tried: recording the next
    // candidate instead had the scan wrap all the way round and claim the lost address a second
    // time, 121 rounds for a field of 120 (#355, J-6). One outside the field starts the scan at
    // the first candidate, and every field address is tried once.
    private static bool TryGetNextArbitraryCandidate(byte preferredAddress, ref byte? scanStart,
        out byte next)
    {
        next = NextArbitraryAddress(preferredAddress);
        if (scanStart is null)
        {
            scanStart = IsArbitraryAddress(preferredAddress) ? preferredAddress : next;
            return true;
        }
        return next != scanStart.Value;
    }

    private static bool IsArbitraryAddress(byte address)
        => address >= ArbitraryAddressRangeStart && address <= ArbitraryAddressRangeEnd;

    private static byte NextArbitraryAddress(byte current)
    {
        if (current < ArbitraryAddressRangeStart) return ArbitraryAddressRangeStart;
        return current >= ArbitraryAddressRangeEnd ? ArbitraryAddressRangeStart : (byte)(current + 1);
    }

    private void OnClaimAnnounceElapsed(byte preferredAddress)
    {
        var pending = _pendingClaim;
        if (pending is null || pending.PreferredAddress != preferredAddress) return;

        // If the caller already cancelled/faulted the returned task (racing between the
        // deadline callback and CancelPendingClaimOnLoop), do not commit the address — the
        // caller has already seen a non-success outcome. The actor
        // serializes both callbacks, so this only catches the rare interleave where the
        // token registration set TrySetCanceled *before* posting the cancel, and the deadline
        // fired before the cancel post ran.
        if (pending.Tcs.Task.IsCompleted)
        {
            _pendingClaim = null;
            pending.Deadline?.Dispose();
            pending.CtRegistration.Dispose();
            // BeginClaim moved us into Claiming and cleared the address /
            // rebound the TP to 0xFE. Because we are abandoning this pending claim without
            // committing, we MUST roll the state machine back to NotClaimed here — otherwise
            // the subsequent CancelPendingClaimOnLoop (or Dispose) sees no pending claim and
            // leaves ClaimState stuck at Claiming with no address.
            if ((J1939ClaimState)Volatile.Read(ref _claimStateStore) == J1939ClaimState.Claiming)
            {
                WriteAddress(null);
                RebindTransportOnLoop(J1939Pgn.NullAddress);
                SetClaimState(J1939ClaimState.NotClaimed, address: null,
                    contendingSa: null, contendingName: null);
            }
            return;
        }

        _pendingClaim = null;
        pending.Deadline?.Dispose();
        pending.CtRegistration.Dispose();

        // Nobody contested us within the arbitration window: commit the address.
        // Rebind the TP channel to the claimed address so directed TP.CM/TP.DT to us gets
        // accepted (the channel filters TP RX by destination address; a 0xFE placeholder drops
        // traffic directed to the claimed address). If open fails after
        // disposing the placeholder channel, do NOT complete Claimed — multi-frame would be
        // broken while Address looks valid.
        if (!RebindTransportOnLoop(preferredAddress))
        {
            // We already announced preferredAddress on the bus; peers may treat it as ours.
            // Broadcast Cannot-Claim (SA 0xFE) so the orphaned announcement is retracted
            //, then leave the node unclaimed.
            WriteAddress(null);
            SetClaimState(J1939ClaimState.CannotClaim, address: null,
                contendingSa: null, contendingName: null);
            SendAddressClaimFrame(sourceAddress: J1939Pgn.NullAddress);
            pending.Tcs.TrySetException(new J1939NodeException(
                $"J1939 claim succeeded on the wire but TP rebind to SA 0x{preferredAddress:X2} failed."));
            return;
        }

        WriteAddress(preferredAddress);
        SetClaimState(J1939ClaimState.Claimed, preferredAddress, contendingSa: null, contendingName: null);
        pending.Tcs.TrySetResult(null);
    }

    // `round` is the claim whose announcement was sent. Matched by identity, not by address: a
    // claim that replaced it for the same address -- a ClaimAddressAsync over the automatic move
    // (J-5), or one after a cancelled claim -- has an announcement of its own, and the replaced
    // round's confirmation or failure is not its (Codex and Bugbot on #414).
    private void OnClaimAnnounceTxConfirmed(byte preferredAddress, PendingClaim round)
    {
        var pending = _pendingClaim;
        if (pending is null || !ReferenceEquals(pending, round)) return;
        if (pending.Tcs.Task.IsCompleted) return;
        if (pending.Deadline is not null) return; // already armed (defensive)
        if ((J1939ClaimState)Volatile.Read(ref _claimStateStore) != J1939ClaimState.Claiming)
            return;

        // Arbitration window starts only after the claim announcement is on the wire.
        pending.Deadline = _deadlines.Arm(_options.ClaimAnnounceTimeout,
            () => OnClaimAnnounceElapsed(preferredAddress));
    }

    private void OnClaimAnnounceTxFailed(byte preferredAddress, PendingClaim round, Exception error, ClaimEcho? echo,
        bool mayStillEcho)
    {
        // Rejected: the driver never accepted the frame, so nothing will echo and the marker
        // has to go now — a retry must not spend its echo on it. Timed out (or bus-off): the
        // frame may already be on the wire. Dropping the marker here makes the late echo look
        // like an equal-NAME peer, or lets it spend the retry's marker (#180).
        ApplyClaimEchoFailure(echo, mayStillEcho);

        var pending = _pendingClaim;
        if (pending is null || !ReferenceEquals(pending, round)) return;

        _pendingClaim = null;
        pending.Deadline?.Dispose();
        pending.CtRegistration.Dispose();

        if ((J1939ClaimState)Volatile.Read(ref _claimStateStore) == J1939ClaimState.Claiming)
        {
            WriteAddress(null);
            RebindTransportOnLoop(J1939Pgn.NullAddress);
            SetClaimState(J1939ClaimState.NotClaimed, address: null,
                contendingSa: null, contendingName: null);
        }

        pending.Tcs.TrySetException(error is J1939NodeException
            ? error
            : new J1939NodeException(
                $"J1939 address claim TX failed for SA 0x{preferredAddress:X2}.", error));
    }

    private void HandleIncomingAddressClaim(uint canId, byte peerSa, byte[] payload, bool isEcho, long receivedAt)
    {
        if (payload.Length < 8) return; // malformed
        var peerName = J1939Name.FromBytes(payload); // the wire order, not the host's (#55)

        // SAE J1939-81 §4.4.2: a CA that receives what it transmits has to tell its own Address
        // Claimed from another CA's, or a duplicate address cannot be detected. Only an echo-mode
        // bus hands the transmit back (VirtualBusHub delivers it to the sender only then). On any
        // other bus the frame is another CA, even when the eight bytes match ours — swallowing it
        // as the echo we are still waiting for is how both stayed Claimed (#168).
        //
        // The match is the claim frame we put on the wire, one marker per send, not the
        // application-PGN ledger (#121, #180). The subscription opts into echoes — the flag
        // marks the host, not the node, so filtering on it would swallow a sibling node's
        // claim (#23). One identical frame still outstanding is the echo; the next one is the
        // peer. Do not go back to dropping every equal NAME on the assumption that the echo
        // gate covers this.
        //
        // A frame the adapter flags as an echo is one too in Normal mode: Vector Classic delivers
        // its own TX event flagged there (#249). Such a marker is only spent by a flagged frame,
        // so a peer's identical claim, which is never flagged, is still arbitrated.
        if (TryTakeClaimEcho(canId, payload, isEcho, receivedAt)) return;

        // A peer at SA=0xFE announces Cannot-Claim. Not directly relevant to *us* unless we
        // are in the middle of claiming — in which case a Cannot-Claim cannot contest us
        // (that peer has already lost).
        if (peerSa == J1939Pgn.NullAddress) return;

        // SAE J1939-81 §4.4.3.3: only the numerically lower NAME keeps the address, and it does
        // so by transmitting Address Claimed again. An equal NAME is not lower for either CA, so
        // HasHigherClaimPriorityThan is false both ways and the "peer loses, re-announce" branch
        // below would answer forever. §4.4.3 does not name a winner for that tie, and it does
        // not say to claim a different address — that sentence is for the CA whose NAME is
        // higher. The address cannot be kept: Cannot Claim (§4.4.3.4), with no further scan.
        if (peerName.Value == _name.Value)
        {
            if (ContestsOurAddress(peerSa))
            {
                // This observation is the contest. A claim we have not started yet must not
                // also lose on the strength of a frame we are answering right now.
                if (_equalNameHeardOn is byte heardOn && heardOn == peerSa)
                    _equalNameHeardOn = null;
                LoseEqualNameContest(peerSa, peerName);
            }
            else
            {
                // Heard before this CA was claiming or holding peerSa — BeginClaim may still
                // be queued behind this frame (#168). Remember the address so that claim loses
                // too; §4.4.3.3 will not make the other CA retransmit an equal NAME for us.
                _equalNameHeardOn = peerSa;
                // When the frame arrived, not when the actor got to it: a backlog on the actor
                // must not make an old observation look recent (Codex on #397).
                _equalNameHeardAt = receivedAt;
            }
            return;
        }

        var pending = _pendingClaim;
        if (pending is not null && peerSa == pending.PreferredAddress)
        {
            // Someone is contending our preferred address during the arbitration window.
            // SAE J1939-81 §4.4.3.2: numerically lower NAME wins.
            if (peerName.HasHigherClaimPriorityThan(_name))
            {
                // We lose this contest, so the address is the peer's now.
                // Arbitrary-address fallback (FR-J1939-004 / SAE J1939-81 §4.5): retry with the
                // next candidate from the arbitrary address field before giving up with
                // Cannot-Claim.
                pending.Deadline?.Dispose();
                pending.Deadline = null;
                var scanStart = pending.ArbitraryScanStart;
                if (ArbitraryClaimingEnabled
                    && TryGetNextArbitraryCandidate(pending.PreferredAddress, ref scanStart,
                        out var nextCandidate))
                {
                    // The caller's TCS and its cancellation registration stay alive across
                    // retries; only the arbitration round is restarted. The pending claim stays
                    // registered through the backoff, so a second ClaimAddressAsync meanwhile
                    // meets the in-flight guard, and the delayed round runs only if it is still
                    // the claim in hand -- not one cancelled or replaced meanwhile (Codex on
                    // #153). The lost address is no longer announced as ours meanwhile.
                    WriteAddress(null);
                    SetClaimState(J1939ClaimState.Claiming, nextCandidate, contendingSa: null, contendingName: null);
                    var retry = new PendingClaim(nextCandidate, pending.Tcs, deadline: null, pending.CtRegistration)
                    {
                        ArbitraryScanStart = scanStart,
                        RequestedAddress = pending.RequestedAddress,
                    };
                    BeginClaimRoundAfterBackoff(retry, nextCandidate, scanStart);
                    return;
                }

                _pendingClaim = null;
                WriteAddress(null);
                // TP channel goes back to placeholder 0xFE — no directed TP traffic reaches
                // us while unclaimed.
                RebindTransportOnLoop(J1939Pgn.NullAddress);
                SetClaimState(J1939ClaimState.CannotClaim, address: null,
                    contendingSa: peerSa, contendingName: peerName);
                // The caller is answered by the backoff, once the Cannot Claim is on the bus.
                _lostClaim = pending;
                ScheduleCannotClaim();
                return;
            }

            // Peer's NAME is numerically greater: they lose. Equal NAME never reaches here
            // (§4.4.3.3, #168). Re-announce our own claim so they hear it,
            // then keep waiting on our deadline. A round still waiting its backoff has nothing
            // to re-announce yet: it starts now, and its announcement is the answer (Codex on
            // #153).
            if (pending.BackingOff) pending.StartRound!();
            else SendAddressClaimFrame(sourceAddress: pending.PreferredAddress);
            return;
        }

        // We are already claimed at SA and a peer claims the same SA.
        if (ClaimState == J1939ClaimState.Claimed && _addressStore >= 0 && peerSa == (byte)_addressStore)
        {
            if (peerName.HasHigherClaimPriorityThan(_name))
            {
                // We are unseated. An arbitrary-address-capable node moves to another address
                // (SAE J1939-81 §4.5, #35): the same scan a contested first claim runs, from the
                // address just lost; only when it finds nothing is Cannot-Claim broadcast.
                byte lost = (byte)_addressStore;
                byte? scanStart = null;
                if (ArbitraryClaimingEnabled && TryGetNextArbitraryCandidate(lost, ref scanStart, out var nextCandidate))
                {
                    // Nobody awaits this round: it was not asked for. Its outcome is announced
                    // through AddressClaimChanged like any claim's, and a failure — the field
                    // exhausted, a TX fault — is reported as a background exception.
                    var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _unattendedClaim = tcs;
                    UnattendedClaimReported = tcs.Task.ContinueWith(t => ReportUnattendedClaimFailure(t.Exception!.GetBaseException()),
                        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                    // The lost address is the peer's from this instant: invalidated now, so no
                    // application traffic goes out under it during the backoff; only the next
                    // round waits. Registered as the claim in hand meanwhile, so a
                    // ClaimAddressAsync during the backoff finds it and replaces it (BeginClaim,
                    // J-5), and the delayed round runs only if it still is (Codex on #153).
                    WriteAddress(null);
                    RebindTransportOnLoop(J1939Pgn.NullAddress);
                    SetClaimState(J1939ClaimState.Claiming, nextCandidate, contendingSa: null, contendingName: null);
                    var unseated = new PendingClaim(nextCandidate, tcs, deadline: null, default)
                    {
                        ArbitraryScanStart = scanStart,
                        RequestedAddress = nextCandidate,
                    };
                    BeginClaimRoundAfterBackoff(unseated, nextCandidate, scanStart);
                    return;
                }
                // Broadcast Cannot-Claim and transition.
                WriteAddress(null);
                RebindTransportOnLoop(J1939Pgn.NullAddress);
                SetClaimState(J1939ClaimState.CannotClaim, address: null,
                    contendingSa: peerSa, contendingName: peerName);
                ScheduleCannotClaim();
            }
            else
            {
                // Peer loses: re-announce our own claim so it moves off our address.
                SendAddressClaimFrame(sourceAddress: (byte)_addressStore);
            }
        }
    }

    // The task of the latest automatic move, the one claim no caller holds. Compared by
    // reference only while that claim is still in hand. Actor-only.
    private TaskCompletionSource<object?>? _unattendedClaim;

    /// <summary>
    /// A seam for tests: the report of the latest automatic round's failure, which runs on the
    /// thread pool once that round faults (and is cancelled, as a continuation, when it does not).
    /// Awaiting it is how a test knows the report has been made, or deliberately not made.
    /// </summary>
    internal Task? UnattendedClaimReported { get; private set; }

    // A disposal ends an automatic round as it ends any claim, with ObjectDisposedException, and
    // that is not a failure of the round: the application disposed the node, and nothing about
    // the bus went wrong (#355, J-3). A ClaimAddressAsync that replaces the round cancels its
    // task (BeginClaim, J-5), and a cancellation never runs this report, which is attached for
    // faults only, so it needs no exclusion here.
    private void ReportUnattendedClaimFailure(Exception error)
    {
        if (error is ObjectDisposedException) return;
        RaiseBackgroundException(error);
    }

    // Set when an Address Claimed carrying our NAME arrives for an SA we are not claiming or
    // holding, so a claim already queued behind that frame also loses — the other CA will not
    // retransmit an equal NAME. Actor-only. It belongs to that queued race: an unrelated round
    // drops it on the way in, and any round that ends drops whatever arrived during it, so a
    // later claim of the remembered address is not lost to a contender who has gone (#180).
    // Nor is one that starts more than an announce window after the frame (J-7): the time the
    // frame arrived, on the actor's clock, is kept beside it.
    private byte? _equalNameHeardOn;
    private long _equalNameHeardAt;

    // WorkMode.Echo is what makes the adapter deliver our own transmit (VirtualBusHub).
    // Without it there is no echo to filter, and an identical frame is the other CA.
    private bool BusEchoesOwnTransmits =>
        _service.Bus.Options.WorkMode == ChannelWorkMode.Echo;

    // An adapter that can flag an echo may do so outside Echo mode (Vector Classic delivers its
    // own TX event, flagged, in Normal mode; measured on no hardware, read from the adapter
    // source, #249). Whether one comes is not known up front, so such a claim is marked too, for
    // a flagged frame only and for a bounded time.
    private bool BusMayFlagEcho =>
        _service.Bus.Options.Features.HasFlag(CanFeature.Echo);

    // The address we are claiming, or the one we hold. A peer on any other SA is not this contest.
    private bool ContestsOurAddress(byte peerSa)
    {
        var pending = _pendingClaim;
        if (pending is not null && peerSa == pending.PreferredAddress)
            return true;
        return ClaimState == J1939ClaimState.Claimed && _addressStore >= 0 && peerSa == (byte)_addressStore;
    }

    // Equal NAME on our address. The pending-loss and unseated-loss paths below, without the
    // arbitrary-address scan: a different address would not make the NAME unique, and §4.4.3.3
    // does not grant that recovery to a tie (#168).
    private void LoseEqualNameContest(byte peerSa, J1939Name peerName)
    {
        var pending = _pendingClaim;
        if (pending is not null && peerSa == pending.PreferredAddress)
        {
            pending.Deadline?.Dispose();
            pending.Deadline = null;
            pending.StartRound = null;
            _pendingClaim = null;
            WriteAddress(null);
            RebindTransportOnLoop(J1939Pgn.NullAddress);
            SetClaimState(J1939ClaimState.CannotClaim, address: null,
                contendingSa: peerSa, contendingName: peerName);
            _lostClaim = pending;
            ScheduleCannotClaim();
            return;
        }

        // The pending claim did not match, and the only other caller is a contest of the
        // address we hold. There is no remaining state in which this loss is someone else's.
        WriteAddress(null);
        RebindTransportOnLoop(J1939Pgn.NullAddress);
        SetClaimState(J1939ClaimState.CannotClaim, address: null,
            contendingSa: peerSa, contendingName: peerName);
        ScheduleCannotClaim();
    }

    private void AnswerRequestForAddressClaimed()
    {
        switch ((J1939ClaimState)Volatile.Read(ref _claimStateStore))
        {
            case J1939ClaimState.Claimed when _addressStore >= 0:
                SendAddressClaimFrame(sourceAddress: (byte)_addressStore);
                break;
            case J1939ClaimState.Claiming when _pendingClaim is { } pending:
                // Arbitrating: the claim for the preferred address is the answer, and a peer
                // that hears it contests it now rather than after the window (§4.4.3). A round
                // still waiting its backoff has announced nothing; the request is the moment to
                // -- it starts the round, which announces (Codex on #153).
                if (pending.BackingOff) pending.StartRound!();
                else SendAddressClaimFrame(sourceAddress: pending.PreferredAddress);
                break;
            default:
                AnswerRequestWithCannotClaim();
                break;
        }
    }

    private void SendAddressClaimFrame(byte sourceAddress, Action? afterHandoff = null)
    {
        // 8-byte little-endian NAME payload. PGN 0xEE00 is PDU1 with PS = 0xFF (global).
        // Re-announcements / Cannot-Claim remain fire-and-forget; the initial claim path uses
        // TransmitAddressClaimConfirmed so ClaimAddressAsync cannot succeed without TX confirm.
        // `afterHandoff` runs back on the actor once SendConfirmedAsync has returned, which is after
        // the driver accepted the frame -- the loss that owes a Cannot Claim faults from there.
        var payload = BuildAddressClaimPayload();
        uint canId = J1939Id.ComposePgn(_options.ClaimPriority, J1939Pgn.AddressClaimed, sourceAddress,
            destinationAddress: J1939Pgn.GlobalAddress);
        TransmitFrame(canId, payload, afterHandoff);
    }

    private byte[] BuildAddressClaimPayload() => _name.ToBytes();

    /// <summary>
    /// Sends the initial Address Claim with <see cref="ICanBusService.SendConfirmedAsync"/> and
    /// posts success/failure back onto the actor so the arbitration deadline is armed only
    /// after a confirmed TX.
    /// </summary>
    private void TransmitAddressClaimConfirmed(byte sourceAddress, PendingClaim round)
    {
        var payload = BuildAddressClaimPayload();
        uint canId = J1939Id.ComposePgn(_options.ClaimPriority, J1939Pgn.AddressClaimed, sourceAddress,
            destinationAddress: J1939Pgn.GlobalAddress);
        // Before it is on the wire: the echo may be back before SendConfirmedAsync returns, and that
        // echo is how HandleIncomingAddressClaim tells our claim from a peer with the same NAME.
        // A bus that does not echo never delivers this frame; recording it would leave an entry
        // that later swallows the peer's identical claim (#168).
        var echo = RecordClaimEcho(canId, payload);
        // Called directly rather than through Task.Run: SendConfirmedAsync's synchronous part is
        // the driver hand-off, and its continuation already runs off this loop; a pool hop
        // in front of it bought nothing and cost one per claim round -- a full arbitrary-
        // address scan is up to 121 of them, the preferred address and the 120 of the field (#58).
        _ = TransmitAddressClaimConfirmedAsync(canId, payload, sourceAddress, round, echo);
    }

    private async Task TransmitAddressClaimConfirmedAsync(uint canId, byte[] payload, byte preferred,
        PendingClaim round, ClaimEcho? echo)
    {
        try
        {
            using var frame = CanFrame.Classic(unchecked((int)canId), payload, isExtendedFrame: true);
            var confirmation = await _service.SendConfirmedAsync(frame).ConfigureAwait(false);
            if (!confirmation.Confirmed)
            {
                var ex = new J1939NodeException(
                    $"J1939 address claim TX failed (id=0x{canId:X8}): {confirmation.FailureReason}.");
                var mayEcho = ClaimEchoMayFollow(confirmation.FailureReason);
                try { _actor.Post(() => OnClaimAnnounceTxFailed(preferred, round, ex, echo, mayEcho)); }
                catch (ObjectDisposedException) { /* the node was disposed: no loop to tell */ }
                return;
            }

            SettleConfirmedClaimEcho(echo);
            try { _actor.Post(() => OnClaimAnnounceTxConfirmed(preferred, round)); }
            catch (ObjectDisposedException) { /* the node was disposed: no loop to tell */ }
        }
        catch (Exception ex)
        {
            // SendConfirmedAsync threw rather than returning a timeout: the scripted and driver
            // failures that do this have not put the frame on the wire.
            try { _actor.Post(() => OnClaimAnnounceTxFailed(preferred, round, ex, echo, mayStillEcho: false)); }
            catch (ObjectDisposedException) { /* the node was disposed: no loop to tell */ }
        }
    }

    private void SetClaimState(J1939ClaimState state, byte? address, byte? contendingSa,
        J1939Name? contendingName)
    {
        // Claiming is the round the observation might still belong to. Every other transition
        // ends a round, and an equal NAME heard during it must not outlive it (#180).
        if (state != J1939ClaimState.Claiming)
            _equalNameHeardOn = null;

        Volatile.Write(ref _claimStateStore, (int)state);
        var args = new J1939ClaimEventArgs(state, address, contendingSa, contendingName);
        try
        {
            AddressClaimChanged?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            RaiseBackgroundException(ex);
        }
    }

    private void WriteAddress(byte? address)
        => Volatile.Write(ref _addressStore, address.HasValue ? address.Value : -1);

    // One Address Claim / Cannot Claim transmit on a bus that echoes, or may flag an echo outside
    // Echo mode (FlaggedOnly). Null when neither: there is nothing to filter, and a recorded
    // marker would later swallow the other CA's identical claim (#168). Actor loop only, same as the list it is added to.
    private ClaimEcho? RecordClaimEcho(uint canId, byte[] payload)
    {
        var echoMode = BusEchoesOwnTransmits;
        if (!echoMode && !BusMayFlagEcho) return null;
        var echo = new ClaimEcho(canId, payload) { FlaggedOnly = !echoMode };
        _claimEchoes.Add(echo);
        return echo;
    }

    // Confirmed. Outside Echo mode there was no echo of our own to match: a flagged one, if
    // this adapter delivers one there, may still follow, and the marker lingers for the grace.
    // In Echo mode the service has matched the echo, and the same frame normally reaches this
    // node's subscription too -- but that is a 1024-deep DropOldest buffer, and an echo dropped
    // there under RX overload left the marker live for ever: the next peer claim with this NAME
    // on that address was swallowed as the echo, and a lingering sibling re-armed its grace
    // every second (#355, J-4). It stays matchable for the grace and expires after it, read off
    // the actor's clock where markers are matched, so a confirmation arms no timer.
    private void SettleConfirmedClaimEcho(ClaimEcho? echo)
    {
        if (echo is null) return;
        if (echo.FlaggedOnly) NoteUnconfirmedClaimEcho(echo, mayStillEcho: true);
        else echo.ExpiresAt = TickMath.DueAt(_time.GetTimestamp(), ClaimEchoGrace, _time.Frequency);
    }

    private bool IsExpired(ClaimEcho echo, long now) => echo.ExpiresAt <= now;

    // Expiry is judged at `receivedAt`, when the reader took the frame off the subscription: an
    // echo that arrived inside the grace and waited on the actor behind a long handler is still
    // this node's own, and taking it for a peer with our NAME would lose the address to
    // ourselves (Codex on #397). Frames are read in order, so a marker expired at this frame's
    // arrival is expired for every later one too.
    private bool TryTakeClaimEcho(uint canId, byte[] payload, bool isEcho, long receivedAt)
    {
        _claimEchoes.RemoveAll(echo => IsExpired(echo, receivedAt));

        // A live send is still waiting for its echo. Prefer that marker over an older one
        // whose confirm already timed out: otherwise the retry's echo is spent on the
        // predecessor, and the predecessor's late echo — or the next real peer — is the
        // frame with no marker left (#180). The timed-out marker is only the match when
        // nothing live is waiting, which is the late echo arriving before the retry.
        int lingering = -1;
        for (int i = 0; i < _claimEchoes.Count; i++)
        {
            var echo = _claimEchoes[i];
            if (!ClaimEchoMatches(echo, canId, payload)) continue;
            if (echo.FlaggedOnly && !isEcho) continue;
            if (!echo.Lingering)
            {
                DropClaimEchoAt(i);
                return true;
            }
            if (lingering < 0) lingering = i;
        }
        if (lingering < 0) return false;
        DropClaimEchoAt(lingering);
        return true;
    }

    private static bool ClaimEchoMatches(ClaimEcho echo, uint canId, byte[] payload)
        => echo.CanId == canId
           && echo.Payload.Length == payload.Length
           && echo.Payload.AsSpan().SequenceEqual(payload);

    private void DropClaimEchoAt(int index)
    {
        // The grace stays armed. This echo already came back, and the callback finds the
        // marker gone. Dispose cancels every grace still outstanding, taken or not.
        _claimEchoes.RemoveAt(index);
    }

    // Rejected: nothing was handed to the driver. Anything else (timeout, bus-off) already
    // was, and the echo can still show up after SendConfirmedAsync has given up.
    private static bool ClaimEchoMayFollow(TxConfirmFailureReason reason)
        => reason != TxConfirmFailureReason.Rejected;

    private void ApplyClaimEchoFailure(ClaimEcho? echo, bool mayStillEcho)
    {
        if (echo is null) return;
        if (mayStillEcho) LingerClaimEcho(echo);
        else _claimEchoes.Remove(echo);
    }

    private void LingerClaimEcho(ClaimEcho echo)
    {
        // The echo can win the race and take the marker before the timeout is posted back.
        // Nothing left to linger in that case: the echo was ours.
        if (!_claimEchoes.Contains(echo)) return;
        echo.Lingering = true;
        ArmClaimEchoGrace(echo);
    }

    private void ArmClaimEchoGrace(ClaimEcho echo)
    {
        ReleaseClaimEchoGrace(echo);
        var grace = _deadlines.Arm(ClaimEchoGrace, () => OnClaimEchoGraceElapsed(echo));
        echo.Grace = grace;
        _claimEchoGraces.Add(grace);
    }

    // A grace handle is dropped from the list once it has fired or been replaced. On a bus that
    // declares the echo capability and never echoes, every confirmed claim send arms one, so a
    // list that only Dispose empties would grow with every Request-for-Address-Claimed (Codex
    // on #287).
    private void ReleaseClaimEchoGrace(ClaimEcho echo)
    {
        if (echo.Grace is not { } grace) return;
        grace.Dispose();
        _claimEchoGraces.Remove(grace);
        echo.Grace = null;
    }

    // Test seam: how many grace handles are still held. Actor loop only.
    internal int ClaimEchoGraceCount => _claimEchoGraces.Count;

    private void OnClaimEchoGraceElapsed(ClaimEcho echo)
    {
        ReleaseClaimEchoGrace(echo);
        if (!_claimEchoes.Contains(echo)) return;
        // A retry of the same frame is in flight. Retiring this marker now lets the late
        // echo spend the retry's, and the retry's own echo is then an equal-NAME peer.
        if (HasLiveClaimEcho(echo))
        {
            ArmClaimEchoGrace(echo);
            return;
        }
        _claimEchoes.Remove(echo);
    }

    private bool HasLiveClaimEcho(ClaimEcho lingering)
    {
        long now = _time.GetTimestamp();
        return _claimEchoes
            .Where(other => !ReferenceEquals(other, lingering) && !other.Lingering && !IsExpired(other, now))
            .Any(other => ClaimEchoMatches(other, lingering.CanId, lingering.Payload));
    }

    private void NoteUnconfirmedClaimEcho(ClaimEcho? echo, bool mayStillEcho)
    {
        if (echo is null) return;
        try { _actor.Post(() => ApplyClaimEchoFailure(echo, mayStillEcho)); }
        catch (ObjectDisposedException) { /* the node was disposed: nothing will match it */ }
    }

    // The frames this node sent and has not seen come back. An echo is recognised by content —
    // the frame itself is the tag — so it is recognised whenever it arrives, and a frame this
    // node did not send is never mistaken for one (#121). A peer sending the identical frame
    // while ours is outstanding is swallowed once, which on a shared bus is indistinguishable
    // from the echo in any case.
    private OwnFrame RecordOwnFrame(uint canId, byte[] payload)
    {
        var entry = new OwnFrame(canId, payload);
        lock (_ownFramesGate)
        {
            if (_ownFrames.Count >= OwnFramesCapacity) _ownFrames.RemoveAt(0);
            _ownFrames.Add(entry);
        }
        return entry;
    }

    // `receivedAt` is when the reader took the frame off the subscription, not when the actor
    // got to it: an echo that arrived inside the grace is the node's own even if a long handler
    // held the actor past it (Codex on #397).
    private bool TryTakeOwnFrame(uint canId, byte[] payload, long receivedAt)
    {
        lock (_ownFramesGate)
        {
            for (int i = 0; i < _ownFrames.Count; i++)
            {
                var entry = _ownFrames[i];
                if (entry.ExpiresAt <= receivedAt)
                {
                    _ownFrames.RemoveAt(i--);
                    continue;
                }
                if (entry.CanId != canId || entry.Payload.Length != payload.Length) continue;
                if (!entry.Payload.AsSpan().SequenceEqual(payload)) continue;
                _ownFrames.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    // Rejected: nothing was handed to the driver, so nothing will echo, and the entry goes now.
    // Anything else may still echo after SendConfirmedAsync returned -- or, on a bus that does
    // not echo, never -- and the entry stays matchable for ClaimEchoGrace. By identity: two sends
    // of the same frame are two entries, and the one a send settles is its own; one its echo
    // already took is simply not found.
    private void SettleOwnFrame(OwnFrame entry, bool mayStillEcho)
    {
        lock (_ownFramesGate)
        {
            if (mayStillEcho)
                entry.ExpiresAt = TickMath.DueAt(_time.GetTimestamp(), ClaimEchoGrace, _time.Frequency);
            else
                _ownFrames.Remove(entry);
        }
    }

    // =========================================================================================
    // Send (SRS FR-J1939-001 / FR-J1939-005 / FR-J1939-006)
    // =========================================================================================

    /// <inheritdoc />
    public Task SendAsync(J1939Message message, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return SendCoreAsync(message, cancellationToken);
    }

    /// <summary>
    /// A seam for tests, null in production: runs on the sender's thread once a send has passed
    /// the claim gate and before it reads <see cref="_transport"/>. A loss processed on the actor
    /// in that window leaves the gate's verdict stale and the channel rebound to 0xFE; the window
    /// is one actor turn wide, so a test holds the sender here while it feeds the actor the
    /// stronger peer's claim, instead of hoping the host lands there (#347).
    /// </summary>
    internal Action? AfterSendGate;

    /// <summary>
    /// A seam for tests, null in production: runs on the actor once a rebind has decided to
    /// replace the channel and before it disposes the current one. After a loss that is the
    /// interval in which the address is already cleared and the channel still carries it, so a
    /// test holds the actor here while a send reaches the channel (#347).
    /// </summary>
    internal Action? BeforeTransportRebind;

    private async Task SendCoreAsync(J1939Message message, CancellationToken cancellationToken)
    {
        if (message.Priority > 7)
            throw new ArgumentOutOfRangeException(nameof(message.Priority), message.Priority,
                "J1939 priority must be in [0, 7].");

        // Gate strictly on ClaimState==Claimed: checking only that the
        // address store is non-negative would let application traffic go out on the previous
        // SA during a re-claim, while the wire already advertises a different preferred
        // address. BeginClaim clears the address as well, so both gates fail closed.
        if ((J1939ClaimState)Volatile.Read(ref _claimStateStore) != J1939ClaimState.Claimed)
            throw new J1939NoAddressException();
        int addr = Volatile.Read(ref _addressStore);
        if (addr < 0)
            throw new J1939NoAddressException();

        byte sa = (byte)addr;
        AfterSendGate?.Invoke();
        try
        {
            // Direct single-frame path (payload <= 8 bytes) — FR-J1939-006.
            if (message.Payload.Length <= 8)
            {
                uint canId = J1939Id.ComposePgn(message.Priority, message.Pgn, sa,
                    destinationAddress: message.DestinationAddress);
                var payload = new byte[message.Payload.Length];
                if (message.Payload.Length > 0) message.Payload.Span.CopyTo(payload);

                using var frame = CanFrame.Classic(unchecked((int)canId), payload, isExtendedFrame: true);
                // Recorded before it is on the wire: the echo may be back before SendConfirmedAsync returns.
                var entry = RecordOwnFrame(canId, payload);
                TxConfirmation confirmation;
                // A throw -- a cancelled wait, a driver fault -- does not say the frame stayed off
                // the wire; only a rejection does.
                var mayStillEcho = true;
                try
                {
                    confirmation = await _service.SendConfirmedAsync(frame, cancellationToken: cancellationToken).ConfigureAwait(false);
                    mayStillEcho = ClaimEchoMayFollow(confirmation.FailureReason);
                }
                finally
                {
                    SettleOwnFrame(entry, mayStillEcho);
                }
                if (!confirmation.Confirmed)
                    throw new J1939NodeException(
                        $"J1939 send failed for PGN 0x{message.Pgn:X}: {confirmation.FailureReason}.");
            }
            else
            {
                // Multi-frame (> 8 bytes) path via the shared J1939-TP channel — FR-J1939-006.
                // After a successful claim RebindTransportOnLoop re-opens _transport on the
                // claimed address, so it is safe to use directly for both TX (peer sees the
                // correct SA on RTS/DT) and RX (CTS/EOM are addressed back to this channel
                // identity).
                // Note (Copilot 3600424623): message.Priority is ignored on this path — TP.CM
                // / TP.DT use the channel's J1939TpOptions.Priority (default 7) because the
                // current IJ1939TpChannel API does not expose a per-send priority.
                // J1939Message.Priority documents the same. Callers who need a specific TP
                // priority must configure J1939NodeOptions.TransportOptions.Priority when
                // opening the node.
                // Matching the address is necessary, not sufficient. A loss clears the address
                // before it retires the channel, so a send arriving in that interval still finds
                // the channel on the lost address. The channel decides on its own loop, where the
                // session starts, by reading the address again (OpenTransport): a loss processed
                // by then refuses it, so no first TP frame is put on the wire after the loss.
                var tpChannel = _transport;
                if (tpChannel.SourceAddress != sa)
                {
                    // The channel does not carry the address the gate admitted. The commit path
                    // cannot leave it so (RebindTransportOnLoop runs before the state turns
                    // Claimed), so the actor gave the address up between the gate and here and
                    // rebound the channel to the 0xFE placeholder: a stronger peer's claim was
                    // processed, and the address is the peer's now (SAE J1939-81), or a re-claim
                    // began and the wire already advertises another one. A single-use channel
                    // on the captured address used to send the whole session under it
                    // regardless, and HasReclaimCrossed below only threw once it had gone out
                    // (#347). Refuse here, as the gate would have one turn earlier.
                    throw new J1939NoAddressException();
                }
                var payloadArr = message.Payload.ToArray();
                if (message.DestinationAddress == J1939Pgn.GlobalAddress)
                    await tpChannel.SendBamAsync(message.Pgn, payloadArr, cancellationToken).ConfigureAwait(false);
                else
                    await tpChannel.SendCmAsync(message.Pgn, message.DestinationAddress, payloadArr, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (ObjectDisposedException) when (HasReclaimCrossed(sa))
        {
            // RebindTransportOnLoop disposed the shared TP channel while
            // our multi-frame send was awaiting SendBamAsync / SendCmAsync. That surfaces as
            // ObjectDisposedException from the TP channel; substitute the canonical
            // reclaim-failure so the caller sees the same failure mode as the pre-send gate
            // rather than an internal-implementation exception.
            throw new J1939NoAddressException();
        }

        // In-flight sends MUST honor a concurrent reclaim. SendCoreAsync
        // captured the claim state / SA before awaiting the wire I/O, but ClaimAddressAsync
        // running on the actor loop between the initial gate and this point may have cleared
        // or moved the address (BeginClaim invalidates the address before announcing the new
        // preferred SA). If that happened the frame(s) we just placed on the wire went out
        // on the previous SA (single-frame path) or spanned the reclaim boundary (multi-
        // frame path); the caller must not observe a successful send that crossed reclaim.
        if (HasReclaimCrossed(sa))
            throw new J1939NoAddressException();
    }

    private bool HasReclaimCrossed(byte capturedSa)
        => (J1939ClaimState)Volatile.Read(ref _claimStateStore) != J1939ClaimState.Claimed
           || Volatile.Read(ref _addressStore) != capturedSa;

    /// <inheritdoc />
    public Task RequestPgnAsync(uint requestedPgn, byte destinationAddress = 0xFF,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (requestedPgn > J1939Pgn.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(requestedPgn), requestedPgn, "PGN must fit in 18 bits.");

        // Request-PGN payload is a 3-byte little-endian PGN.
        var payload = new byte[3];
        payload[0] = (byte)(requestedPgn & 0xFF);
        payload[1] = (byte)((requestedPgn >> 8) & 0xFF);
        payload[2] = (byte)((requestedPgn >> 16) & 0xFF);

        // Request PGN is 0xEA00 (PDU1). The Request frame itself is priority 6 on the wire
        // (SAE J1939-21 §5.3.2 for Request PGN).
        var message = new J1939Message(J1939Pgn.Request, payload, _options.ClaimPriority,
            sourceAddress: 0, destinationAddress: destinationAddress);
        return SendCoreAsync(message, cancellationToken);
    }

    /// <inheritdoc />
    public IDisposable StartPeriodicSend(J1939Message message, TimeSpan period)
    {
        ThrowIfDisposed();
        if (period <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(period), period, "Period must be positive.");
        if (message.Priority > 7)
            throw new ArgumentOutOfRangeException(nameof(message.Priority), message.Priority,
                "J1939 priority must be in [0, 7].");

        // Every periodic PGN — single-frame and multi-frame alike — is driven by the actor /
        // SendAsync loop in PeriodicSchedule. The earlier attempt to route single-frame
        // payloads through the L1 IPeriodicTx handle was reverted (PR #33): the SW-fallback
        // path swallowed Transmit exceptions, and every attempted work-
        // around (reclaim-time handle rebind, address-loss teardown on the actor loop,
        // payload snapshot, handle-detach on dispose, …) revealed yet another edge case.
        // Collapsing to one path removes an entire class of races at the cost of the L1
        // jitter optimization — FR-J1939-007 (SRS Should) still holds via the L2 actor /
        // DeadlineScheduler path, which is what IJ1939Node.StartPeriodicSend documents.
        //
        // Pre-flight claim gate mirrors SendAsync: refuse to arm a
        // schedule without a currently-claimed SA so callers see the same failure mode as
        // a one-shot SendAsync before ClaimAddressAsync completes. Once running, the loop's
        // per-emission SendAsync call re-checks the claim state on every tick, so an
        // address loss after Start stops wire traffic (SendAsync throws
        // J1939NoAddressException, which surfaces via BackgroundExceptionOccurred).
        if ((J1939ClaimState)Volatile.Read(ref _claimStateStore) != J1939ClaimState.Claimed)
            throw new J1939NoAddressException();
        if (Volatile.Read(ref _addressStore) < 0)
            throw new J1939NoAddressException();

        var schedule = new PeriodicSchedule(this, message, period);
        try
        {
            schedule.Start();

            // Registered once started, so every schedule the teardown finds has a tick to
            // cancel. The teardown stops every schedule it finds registered; one registered after
            // the teardown took its list -- a Dispose that began after the check at the top -- is
            // refused here instead: the flag is set before the teardown is queued, so either that
            // teardown sees this schedule or this read sees the flag (Codex on #397). Whatever
            // fails on the way, the schedule is disposed before the failure propagates.
            lock (_schedulesGate) _schedules.Add(schedule);
            AfterPeriodicScheduleStarted?.Invoke();
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(J1939NodeImpl));
            return schedule;
        }
        catch
        {
            schedule.Dispose();
            throw;
        }
    }

    // Every schedule StartPeriodicSend handed out and its caller has not disposed. The node's
    // teardown cancels their ticks: an injected actor outlives the node, and a schedule's tick
    // re-armed on it for ever, each emission failing on the disposed node (#355).
    private readonly HashSet<PeriodicSchedule> _schedules = new();
    private readonly object _schedulesGate = new();

    /// <summary>
    /// A seam for tests, null in production: runs once <see cref="StartPeriodicSend"/> has
    /// registered and started a schedule and before it checks whether the node was disposed
    /// meanwhile -- the instant a concurrent Dispose has to land for that check to matter.
    /// </summary>
    internal Action? AfterPeriodicScheduleStarted;

    // =========================================================================================
    // Wire helpers
    // =========================================================================================

    private void TransmitFrame(uint canId, byte[] payload, Action? afterHandoff = null)
    {
        // Fire-and-forget: address-claim traffic doesn't need a task, but we still want a
        // background exception if the driver rejects it. SendConfirmedAsync is used consistently
        // with the rest of the CanKit.Pro stack. No Task.Run hop in front of it (#58).
        // Recorded first, as in SendCoreAsync: a re-announce's echo is this frame coming back,
        // and it must not be arbitrated as a peer that shares our NAME (#168). Same gate as
        // the initial claim — no echo, no entry — and the same per-send marker, so a burst of
        // these cannot evict one another or the claim they answer (#180).
        var echo = RecordClaimEcho(canId, payload);
        _ = TransmitFrameAsync(canId, payload, echo, afterHandoff);
    }

    private async Task TransmitFrameAsync(uint canId, byte[] payload, ClaimEcho? echo, Action? afterHandoff)
    {
        try
        {
            using var frame = CanFrame.Classic(unchecked((int)canId), payload, isExtendedFrame: true);
            var confirmation = await _service.SendConfirmedAsync(frame).ConfigureAwait(false);
            if (!confirmation.Confirmed)
            {
                NoteUnconfirmedClaimEcho(echo, ClaimEchoMayFollow(confirmation.FailureReason));
                RaiseBackgroundException(new J1939NodeException(
                    $"J1939 frame TX failed (id=0x{canId:X8}): {confirmation.FailureReason}."));
            }
            else
            {
                SettleConfirmedClaimEcho(echo);
            }
        }
        catch (Exception ex)
        {
            NoteUnconfirmedClaimEcho(echo, mayStillEcho: false);
            RaiseBackgroundException(ex);
        }

        if (afterHandoff is null) return;
        try { _actor.Post(afterHandoff); }
        catch (ObjectDisposedException) { /* the node was disposed: no loop to tell */ }
    }

    // =========================================================================================
    // Reader loops
    // =========================================================================================

    /// <summary>
    /// A seam for tests, null in production: runs on the reader once it has taken a frame off the
    /// subscription and stamped it, before the frame is handed to the actor. A test moves the
    /// clock here to model a frame that arrived in time and waited on the actor (Codex on #397).
    /// </summary>
    internal Action<uint>? AfterFrameRead;

    private async Task RunReaderAsync()
    {
        try
        {
            await foreach (var frameEvent in _subscription.Frames.WithCancellation(_readerCts.Token)
                .ConfigureAwait(false))
            {
                // The subscription opts into echoes, so this loop sees host echoes as well as
                // peer traffic; `frameEvent.IsEcho` distinguishes them. Nothing *here* filters on
                // source address, and that is deliberate rather than outstanding: the two frame
                // classes that must not be filtered by it -- Address Claim, which arbitration
                // needs to see even from a peer wrongly using our address, and a frame addressed
                // to this node -- are only distinguishable after the id is decomposed. So the
                // self-traffic drop lives in HandleIncomingFrame, past the Address Claim branch,
                // and Address Claim tells this node's own frame from a peer's by the transmit
                // ledger, not by NAME (#95, #168).
                var frame = frameEvent.Frame;
                if (!frame.IsExtendedFrame) continue;
                var fields = J1939Id.Decompose((uint)frame.ID);
                // Skip TP traffic — the shared J1939-TP channel demuxes those separately.
                if (J1939Pgn.IsTransportCm(fields.Pgn) || J1939Pgn.IsTransportDt(fields.Pgn))
                    continue;
                var pgn = fields.Pgn;
                var sa = fields.SourceAddress;
                var da = fields.PduSpecific;
                var priority = fields.Priority;
                var payload = frame.Data.ToArray();
                var isPdu1 = fields.IsPdu1;
                var canId = (uint)frame.ID;
                var isEcho = frameEvent.IsEcho;
                var receivedAt = _time.GetTimestamp();
                AfterFrameRead?.Invoke(canId);
                _actor.Post(() => HandleIncomingFrame(canId, pgn, priority, sa, da, isPdu1, payload, isEcho, receivedAt));
            }
        }
        catch (OperationCanceledException) { /* expected on Dispose */ }
        catch (Exception ex) { RaiseOnActor(ex); }
    }

    // The channel is told whether the node still holds its address, and asks where a session
    // starts, on its own loop (#347): every loss writes the address before RebindTransportOnLoop
    // retires the channel, so a session that started read the address before the loss, and one
    // that reads it after is refused. The placeholder 0xFE is never held, and no send reaches it.
    private IJ1939TpChannel OpenTransport(byte sourceAddress)
        => _openTransport?.Invoke(sourceAddress)
           ?? new J1939TpChannel(_service, sourceAddress, _options.TransportOptions, ownsService: false,
               sourceAddressHeld: () => !HasReclaimCrossed(sourceAddress));

    private Task StartTransportReader(IJ1939TpChannel transport)
        => Task.Run(() => RunTransportReaderAsync(transport));

    // Bound to a specific transport instance so a rebind (which replaces _transport) does not
    // accidentally cause an already-running reader to switch enumerables mid-flight. The
    // per-instance inbox completes on channel Dispose, so this loop exits cleanly on rebind.
    //
    // A reassembly abort (bad TP.DT sequence, T1, a BAM superseded by the next one from the
    // same source) is thrown out of ReceiveAllAsync, which ends that enumeration. It ends one
    // peer's transfer, not the channel: the inbox stays open and the next datagram is already
    // queued behind the fault, so the reader enumerates again. Stopping here, as it once did,
    // left the node deaf to every multi-frame message until the next rebind (#195). The abort
    // is not re-raised: the channel has already raised it through BackgroundExceptionOccurred,
    // which the node forwards.
    private async Task RunTransportReaderAsync(IJ1939TpChannel transport)
    {
        while (true)
        {
            try
            {
                await ForwardTransportDatagramsAsync(transport).ConfigureAwait(false);
                return;
            }
            catch (J1939TpAbortException) { /* surfaced by the channel; keep reading */ }
            catch (OperationCanceledException) { return; /* expected on Dispose */ }
            catch (ObjectDisposedException) { return; /* the node was disposed with a datagram in hand */ }
            catch (Exception ex) { RaiseOnActor(ex); return; }
        }
    }

    private async Task ForwardTransportDatagramsAsync(IJ1939TpChannel transport)
    {
        await foreach (var datagram in transport.ReceiveAllAsync(_readerCts.Token).ConfigureAwait(false))
        {
            // Drop datagrams from a channel that has already been replaced by rebind
            // (we no longer Wait the old reader on the actor).
            if (!ReferenceEquals(transport, _transport))
                return;

            // Reassembled PDU: emit as a J1939Message just like a single-frame arrival.
            // `IJ1939Node.MessageReceived` documents that handlers run on the node's actor
            // loop; the transport reader is a separate Task, so we must marshal onto the
            // actor before firing the event so single-frame and multi-frame receive paths
            // share the same thread affinity guarantee.
            var message = new J1939Message(datagram.Pgn, datagram.Payload,
                priority: _options.DefaultPriority,
                sourceAddress: datagram.SourceAddress,
                destinationAddress: datagram.DestinationAddress);
            // Throws ObjectDisposedException once the node is disposed: the reader above takes
            // that as its end and drops the datagram in hand, as RunReaderAsync stops posting.
            _actor.Post(() =>
            {
                if (!ReferenceEquals(transport, _transport)) return;
                EmitMessage(message);
            });
        }
    }

    /// <summary>
    /// Re-open <see cref="_transport"/> on <paramref name="sourceAddress"/>. Must run on the
    /// actor loop (single-writer discipline for <c>_transport</c> / <c>_transportReaderTask</c>).
    /// A no-op if the current channel already carries the requested SA and is still live: one
    /// left in place by a failed reopen is disposed, carries its old SA all the same, and is
    /// reopened rather than reported as bound (#375).
    /// </summary>
    /// <remarks>
    /// The previous channel MUST be disposed synchronously on the actor
    /// loop before a new channel is opened. Fire-and-forgetting <c>Dispose</c> in parallel
    /// with opening a fresh channel leaves both TP channels subscribed to the bus at the
    /// same time; broadcast TP.BAM (DA = 0xFF) is accepted by both channels and both fire
    /// reassembled datagrams, so <see cref="MessageReceived"/> sees each multi-frame BAM
    /// once per surviving channel. The channel's Dispose cancels its bus subscription and
    /// joins its own reader (up to 2 s) and its own actor (up to 5 s) on this loop --
    /// milliseconds normally, but it is paid here; we deliberately do not also <c>Wait</c>
    /// the node's transport reader on the actor -- stale posts are filtered by channel
    /// identity instead.
    /// If <c>J1939Tp.Open</c> then fails, the exception is surfaced via
    /// <see cref="BackgroundExceptionOccurred"/> — <c>_transport</c> continues to point at
    /// the now-disposed old channel so subsequent TP sends fail loudly rather than
    /// silently transmit on a stale SA.
    /// </remarks>
    /// <returns>
    /// <see langword="true"/> when the node has a live TP channel bound to
    /// <paramref name="sourceAddress"/> after this call; <see langword="false"/> when open
    /// failed after disposing the previous channel (multi-frame path broken).
    /// </returns>
    private bool RebindTransportOnLoop(byte sourceAddress)
    {
        if (Volatile.Read(ref _disposed) != 0) return false;
        var current = _transport;
        if (_transportLive && current.SourceAddress == sourceAddress) return true;
        BeforeTransportRebind?.Invoke();

        // Tear down the previous transport BEFORE opening the new one so no window exists
        // where two subscribed channels can both surface the same broadcast TP.BAM
        //. Disposing the channel cancels its bus subscription
        // immediately and completes its inbox. Do NOT Wait on the reader here — that would
        // stall the single ProtocolActor for up to ~2 s and block address-claim handling
        //. Late datagrams from the old reader are dropped via the
        // ReferenceEquals(transport, _transport) gate in RunTransportReaderAsync.
        _transportLive = false;
        current.BackgroundExceptionOccurred -= OnTransportBackgroundException;
        try { current.Dispose(); }
        catch (Exception ex) { RaiseBackgroundException(ex); }

        IJ1939TpChannel newTransport;
        try
        {
            newTransport = OpenTransport(sourceAddress);
        }
        catch (Exception ex)
        {
            // The old channel is already disposed; the node's multi-frame path is broken
            // until the next successful rebind, but single-frame traffic keeps working via
            // the direct service. Surface so the application can react. Callers that must
            // not advertise Claimed without a live TP (claim commit) check the return value
            //.
            RaiseBackgroundException(ex);
            return false;
        }

        newTransport.BackgroundExceptionOccurred += OnTransportBackgroundException;
        _transport = newTransport;
        _transportLive = true;
        _transportReaderTask = StartTransportReader(newTransport);
        return true;
    }

    private void HandleIncomingFrame(uint canId, uint pgn, byte priority, byte sa, byte da, bool isPdu1, byte[] payload,
        bool isEcho, long receivedAt)
    {
        try
        {
            // Address Claim (PGN 0xEE00): drive the state machine and stop; not an application PGN.
            if (J1939Pgn.IsAddressClaim(pgn))
            {
                HandleIncomingAddressClaim(canId, sa, payload, isEcho, receivedAt);
                return;
            }

            int myAddr = Volatile.Read(ref _addressStore);

            // Our own traffic, on any bus that echoes (#95). Unconditional on the source address
            // rather than conjoined with IsEcho, matching J1939TpChannel: the flag is an adapter
            // detail -- CanKit.Adapter.Virtual echoes without setting it (#94) -- so a conjunction
            // would be inert on exactly the adapters where nothing else catches this.
            //
            // Safe here and not in the reader because Address Claim returned above. Arbitration is
            // the one thing that must see a peer wrongly using our address, and it runs on PGN
            // 0xEE00, which never reaches this line. What is dropped is an application PGN
            // carrying our own source address, and on a shared bus that is indistinguishable from
            // our own echo in any case.
            //
            // Except when the frame is addressed to this node on purpose. RequestPgnAsync and
            // SendAsync both accept the local address as the destination, and on an echo bus that
            // is a working loopback: the request comes back and the application's responder
            // serves it. Dropping it would break a path that worked before this guard existed
            // (#119, Codex). Same rule the CANopen guard states for 0x600 + id -- an explicitly
            // directed frame is ours to serve whoever sent it -- and it cannot swallow a
            // broadcast, because a broadcast is either PDU2 or carries da == 0xFF.
            //
            // The ledger is consulted first, whichever guard then decides: an echo that the
            // source-address check drops has still come back and its entry has to go, or it
            // would match a peer's identical frame under that address after a re-claim (Bugbot
            // on #140). And the carve-out needs an address to be directed to — while the node
            // holds none, -1 cast to a byte is the global address, and a broadcast echo would
            // read as directed to us (Bugbot on #140).
            bool ownFrame = TryTakeOwnFrame(canId, payload, receivedAt);
            bool directedToUs = myAddr >= 0 && isPdu1 && da == (byte)myAddr;
            if (myAddr >= 0 && sa == (byte)myAddr && !directedToUs) return;

            // The echo of a frame this node sent under an address it no longer holds: BeginClaimRound
            // clears the address before announcing the new one, so the check above cannot fire
            // for it, and it may arrive at any time -- during the arbitration window, or after
            // the claim for the new address completed, when a reader backlog or a late adapter
            // delivered it (#119, #121). It is recognised by content, against the frames this
            // node transmitted, not against an address and a window: a frame this node did not
            // send, whoever holds that address now, is a peer's and is heard. A frame directed
            // to this node is served whoever sent it, as above.
            if (ownFrame && !directedToUs) return;

            // Only surface application PGNs that are either broadcast (PDU2) or directed at us.
            // While the node is Claiming it holds no address, so this also drops a Request for
            // Address Claimed directed at the address it is arbitrating for: only a global request
            // is answered in that window. Whether SAE J1939-81 requires that one to be answered
            // too is open -- the review behind #355 (J-2) cited §4.2.2 from memory, the text has
            // not been checked -- and the README says so. Unchanged until it is.
            if (isPdu1 && da != J1939Pgn.GlobalAddress && (myAddr < 0 || da != (byte)myAddr))
                return;

            // A Request for Address Claimed (SAE J1939-81 §4.2.2, #34) is answered by the node
            // itself: with its claim while it holds or is arbitrating an address, with
            // Cannot-Claim (SA 0xFE) while it holds none — without this a network-management
            // tool scanning the bus never sees the node. The request still reaches the
            // application below.
            if (J1939Pgn.IsRequest(pgn) && payload.Length >= 3
                && (payload[0] | (payload[1] << 8) | (payload[2] << 16)) == J1939Pgn.AddressClaimed)
            {
                AnswerRequestForAddressClaimed();
            }

            var message = new J1939Message(pgn, payload, priority, sa,
                isPdu1 ? da : J1939Pgn.GlobalAddress);
            EmitMessage(message);
        }
        catch (Exception ex)
        {
            RaiseBackgroundException(ex);
        }
    }

    private void EmitMessage(J1939Message message)
    {
        try
        {
            MessageReceived?.Invoke(this, message);
        }
        catch (Exception ex)
        {
            RaiseBackgroundException(ex);
        }
        // No copy is kept: MessageReceived is the receive path. An internal inbox nothing read
        // used to retain up to ReceiveBufferCapacity payloads per node (#355, J12).
    }

    // =========================================================================================
    // Dispose
    // =========================================================================================

    /// <inheritdoc />
    public void Dispose() => TryDispose();

    /// <summary>Disposes; true when this call was the one that did the work, false when another
    /// had taken it. The answer is the atomic exchange's, not an earlier read of the flag.</summary>
    private bool TryDispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return false;
        if (_actor.IsOnCurrentActor && TryQueueDisposal()) return true;
        try
        {
            DisposeCore();
            _disposalDone.TrySetResult(true);
            return true;
        }
        catch (Exception ex)
        {
            // A teardown step that throws is the disposal's failure: a caller awaiting
            // DisposeAsync is told, not told that it succeeded.
            _disposalDone.TrySetException(ex);
            throw;
        }
    }

    // A Dispose called from a callback of the node's actor. That callback may be a handler raised
    // in the middle of a transition -- AddressClaimChanged for Claiming, before the round has
    // registered its claim -- so the disposal does not run inside the turn: what the rest of the
    // turn publishes would outlive the teardown, a claim registered after it never answered
    // (Codex on #397). The whole disposal is queued behind the turn instead, as one unit, so its
    // order is the one it has everywhere else (channel before an owned service, Bugbot on #397);
    // in that clean turn the teardown runs in place. The caller cannot wait for its own actor,
    // so it returns at once; the disposal completion follows the queued disposal, which a
    // DisposeAsync from elsewhere then waits for (Codex on #397), and a failure is reported
    // where background failures go as well, since the caller on the actor never sees it. False
    // when the actor -- an injected one -- is already gone: then the disposal runs in place.
    private bool TryQueueDisposal()
    {
        Task queued;
        try
        {
            queued = _actor.PostAsync(DisposeCore);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        _ = queued.ContinueWith(static (disposal, state) => ((J1939NodeImpl)state!).CompleteQueuedDisposal(disposal),
            this, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        return true;
    }

    private void CompleteQueuedDisposal(Task disposal)
    {
        if (disposal.Exception is { } failed)
        {
            var error = failed.GetBaseException();
            RaiseBackgroundException(error);
            _disposalDone.TrySetException(error);
        }
        else
        {
            _disposalDone.TrySetResult(true);
        }
    }

    private void DisposeCore()
    {
        try { _readerCts.Cancel(); } catch { }

        var transportReader = Volatile.Read(ref _transportReaderTask);
        try
        {
            transportReader = TearDownOnActor();
        }
        finally
        {
            // A channel that throws on Dispose is this disposal's failure, not a reason to leave
            // the rest running: the subscription, the actor and an owned service are released
            // all the same before it propagates (Codex and Bugbot on #397).
            try { _readerTask.Wait(ReaderJoinTimeout); } catch (AggregateException) { /* the reader reports its own faults */ }
            try { transportReader.Wait(ReaderJoinTimeout); } catch (AggregateException) { /* likewise */ }

            _subscription.Dispose();
            // An injected actor is not ours to dispose; the handler is, either way. It comes off
            // only after an owned actor's Dispose: that is where the actor reports a callback that
            // outlived its shutdown bound, as a TimeoutException through its
            // BackgroundExceptionOccurred, and unsubscribed first the node's subscribers never
            // heard of it (#355, from the review of #397).
            if (_ownsActor) _actor.Dispose();
            _actor.BackgroundExceptionOccurred -= OnActorBackgroundException;
            _readerCts.Dispose();
            if (_ownsService) _service.Dispose();
        }
    }

    /// <summary>
    /// How long a Dispose off the actor waits for the actor to run the teardown: the actor's own
    /// shutdown bound. A seam for tests of the path where the actor does not get there in time.
    /// </summary>
    internal TimeSpan TransportTeardownTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long the disposal waits for each of the two readers -- the frame reader and the current
    /// transport reader -- to end. A seam for tests: made infinite, a reader the disposal waits for
    /// from inside its own handler is a hang instead of a 2 s stall, which no test has to time.
    /// </summary>
    internal TimeSpan ReaderJoinTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// A seam for tests, null in production: runs on the disposing thread once the teardown has
    /// been handed to the actor and before that thread waits for it. A test holding the actor in
    /// a rebind (<see cref="BeforeTransportRebind"/>) lets go of it here, which is after the
    /// teardown is queued behind the rebind (#375).
    /// </summary>
    internal Action? AfterTeardownPosted;

    // #375 (J10): the transport is the actor's. RebindTransportOnLoop replaces it there, after
    // checking _disposed once; a Dispose that read _transport on its own thread while a rebind
    // that passed that check was still inside the old channel's Dispose tore down the old channel
    // and left the one the rebind then opened -- its subscription, its reader and its actor --
    // running for ever. Queued on the actor, the teardown runs after any rebind already there,
    // and every later rebind sees _disposed. Returns the reader of the channel it tore down.
    private Task TearDownOnActor()
    {
        // On the actor nothing else can be rebinding: this is the actor, in a turn of the
        // disposal's own (TryQueueDisposal), or one that is past its callbacks already.
        if (_actor.IsOnCurrentActor) return TearDownOnLoop();

        Task<Task> queued;
        try
        {
            queued = _actor.PostAsync(TearDownOnLoop);
        }
        catch (ObjectDisposedException)
        {
            // An injected actor its owner already disposed: no rebind can be queued any more. The
            // claim state went with that actor -- it is actor state, and its loop may still be
            // draining -- but the channel is the node's own and is not left running.
            return DisposeVisibleTransport();
        }

        AfterTeardownPosted?.Invoke();
        // WaitAny does not throw for a faulted task. A teardown step that threw on the actor is
        // this disposal's failure, as it was when it threw on this thread, and GetResult throws
        // it as itself rather than wrapped by the wait.
        if (Task.WaitAny(new Task[] { queued }, TransportTeardownTimeout) == 0)
            return queued.GetAwaiter().GetResult();

        // The actor is held by a callback that has not returned within its own shutdown bound.
        // The channel visible now is disposed from here, as before #375, and the queued teardown
        // still disposes whichever channel is current once the actor gets to it. Nobody waits
        // for it any more, so a channel that throws there is reported where background failures
        // go, not left on a task nobody observes.
        _ = queued.ContinueWith(static (late, state) => ((J1939NodeImpl)state!).RaiseBackgroundException(late.Exception!.GetBaseException()),
            this, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        return DisposeVisibleTransport();
    }

    private Task DisposeVisibleTransport()
    {
        Volatile.Read(ref _transport).Dispose();
        return Volatile.Read(ref _transportReaderTask);
    }

    // Actor loop. The claim half: a loss whose Cannot Claim never got its backoff still answers
    // its caller -- the claim failed, and no dispose makes that less true (Codex on #153). So
    // does every earlier loss whose send is still in flight: `_lostClaim` only holds the latest,
    // and tearing the actor down means those handoffs never post.
    private Task TearDownOnLoop()
    {
        CompleteLostClaim();
        while (_cannotClaimsAwaitingHandoff.Count > 0)
            FaultLostClaim(_cannotClaimsAwaitingHandoff[0]);
        foreach (var grace in _claimEchoGraces)
            grace.Dispose();
        _claimEchoGraces.Clear();
        _claimEchoes.Clear();
        var pending = _pendingClaim;
        if (pending is not null)
        {
            _pendingClaim = null;
            pending.Deadline?.Dispose();
            pending.CtRegistration.Dispose();
            pending.Tcs.TrySetException(new ObjectDisposedException(nameof(J1939NodeImpl)));
        }

        // On the actor, after any tick that was already running: one that re-armed is cancelled
        // here, and none fires afterwards (Codex on #397).
        PeriodicSchedule[] schedules;
        lock (_schedulesGate) schedules = _schedules.ToArray();
        foreach (var schedule in schedules)
            schedule.StopTicking();

        _transport.Dispose();
        return _transportReaderTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        // On the actor (a callback of this node) the disposal is queued behind the current turn
        // and not waited for (TryQueueDisposal): the actor cannot wait for itself. Anywhere else
        // it runs on the thread pool, so the caller's thread is not held for the joins Dispose
        // makes.
        if (_actor.IsOnCurrentActor)
        {
            var won = TryDispose();
            // A call that lost cannot wait for the winner's teardown, which may be waiting for
            // this very actor; it reports the outcome when there is one already, failure included.
            return won || !_disposalDone.Task.IsCompleted ? default : new ValueTask(_disposalDone.Task);
        }

        // One task for every caller: a second call must not return before the first disposal has
        // finished, which running the idempotent Dispose twice would let it do.
        // The completion belongs to whichever Dispose actually does the work (it sets it in its
        // finally), so a caller awaiting it waits for the real teardown, also when a blocking
        // Dispose won. Only the first asynchronous caller starts a Dispose of its own.
        if (Interlocked.Exchange(ref _asyncDisposalStarted, 1) == 0)
        {
            // The failure reaches the callers through the completion; the task itself is observed.
            _ = Task.Run(Dispose).ContinueWith(static failed => _ = failed.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }

        return new ValueTask(_disposalDone.Task);
    }

    private int _asyncDisposalStarted;

    private readonly TaskCompletionSource<bool> _disposalDone = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // =========================================================================================
    // Diagnostics helpers
    // =========================================================================================

    private void RaiseBackgroundException(Exception ex)
    {
        try { BackgroundExceptionOccurred?.Invoke(this, ex); }
        catch { /* misbehaving subscriber must not tear the node down */ }
    }

    private void OnActorBackgroundException(object? sender, Exception ex) => RaiseBackgroundException(ex);
    // The channel raises a fault -- a reassembly abort, a T1 timeout -- on its own actor. Raised
    // from there, a handler that disposed the node made that thread wait for the node's actor
    // to tear the channel down, and tearing it down joins the channel's actor: the thread doing
    // the waiting (#375). From the node's actor a Dispose tears down in place.
    private void OnTransportBackgroundException(object? sender, Exception ex) => RaiseOnActor(ex);

    // Raises a fault that arrived off the node's actor from the actor: a transport fault (above),
    // and the end of either of the node's own readers. Raised on the reader, a handler that
    // disposed the node made the disposal wait for that reader -- its own task -- for the whole
    // join bound, 2 s each (#355, from the review of #397). From the actor the disposal is queued
    // behind the handler's turn, and the reader has returned by the time it is joined. A node
    // whose injected actor is already gone reports the fault where it arrived; a handler that
    // disposes the node from there still waits out that bound on its own reader, as nothing is
    // left to hand it to.
    private void RaiseOnActor(Exception ex)
    {
        try { _actor.Post(() => RaiseBackgroundException(ex)); }
        catch (ObjectDisposedException) { RaiseBackgroundException(ex); }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(J1939NodeImpl));
    }

    // =========================================================================================
    // Nested types
    // =========================================================================================

    // Boxed state passed to CancellationToken.Register so the callback can post the actor
    // teardown for the specific in-flight claim it corresponds to.
    private sealed class ClaimCancelState
    {
        public ClaimCancelState(J1939NodeImpl node, TaskCompletionSource<object?> tcs)
        {
            Node = node;
            Tcs = tcs;
        }

        public J1939NodeImpl Node { get; }
        public TaskCompletionSource<object?> Tcs { get; }
    }

    // Identity, not content. Two claims of the same NAME and address are byte-identical; the
    // one a failed send must drop is the one that send recorded. Lingering is a send whose
    // confirm timed out: the marker stays for ClaimEchoGrace so the late echo is still ours.
    private sealed class ClaimEcho
    {
        public ClaimEcho(uint canId, byte[] payload)
        {
            CanId = canId;
            Payload = payload;
        }

        public uint CanId { get; }
        public byte[] Payload { get; }
        public bool Lingering { get; set; }

        // Recorded outside Echo mode: only a frame the adapter flags as an echo may spend it.
        public bool FlaggedOnly { get; init; }
        public IDeadline? Grace { get; set; }

        // On the actor's clock: an Echo-mode marker whose send was confirmed stops matching here
        // (J-4). Written by the send's continuation, read on the actor.
        public long ExpiresAt
        {
            get => Volatile.Read(ref _expiresAt);
            set => Volatile.Write(ref _expiresAt, value);
        }

        private long _expiresAt = long.MaxValue;
    }

    // One application single-frame send in the ledger. Identity, as with ClaimEcho: the grace
    // of one send retires that send's entry, not an identical later one's.
    private sealed class OwnFrame
    {
        public OwnFrame(uint canId, byte[] payload)
        {
            CanId = canId;
            Payload = payload;
        }

        public uint CanId { get; }
        public byte[] Payload { get; }

        // On the actor's clock; unbounded while the send is still outstanding. Under the gate.
        public long ExpiresAt { get; set; } = long.MaxValue;
    }

    private sealed class PendingClaim
    {
        public PendingClaim(byte preferredAddress, TaskCompletionSource<object?> tcs, IDeadline? deadline,
            CancellationTokenRegistration ctRegistration)
        {
            PreferredAddress = preferredAddress;
            Tcs = tcs;
            Deadline = deadline;
            CtRegistration = ctRegistration;
        }

        public byte PreferredAddress { get; }

        /// <summary>
        /// The address the claim set out for -- the caller's preferred address, or the first
        /// candidate of an automatic move -- which <see cref="PreferredAddress"/> stops being
        /// once an arbitrary-address scan moves on. <see cref="J1939CannotClaimException"/>
        /// reports this one: it documents the address the caller originally requested.
        /// </summary>
        public byte RequestedAddress { get; init; }
        public TaskCompletionSource<object?> Tcs { get; }
        public IDeadline? Deadline { get; set; }
        public CancellationTokenRegistration CtRegistration { get; }

        /// <summary>Where the ongoing arbitrary-address scan started (FR-J1939-004): the lost
        /// address when it lies in the field, the first candidate otherwise; the scan ends when
        /// it comes back to it. Null while no fallback round has run yet. Carried across
        /// retries via <c>BeginClaimRound</c>.</summary>
        public byte? ArbitraryScanStart { get; set; }
        /// <summary>
        /// The round waits the §4.4.4.3 backoff and has announced nothing yet; <see cref="Deadline"/>
        /// is the backoff, and <see cref="StartRound"/> starts the round -- now, when a Request for
        /// Address Claimed or a contest for the candidate makes waiting pointless.
        /// </summary>
        public Action? StartRound { get; set; }
        public bool BackingOff => StartRound is not null;
    }

    /// <summary>
    /// Software periodic-send schedule used for every periodic PGN — single-frame and
    /// multi-frame alike (FR-J1939-006/007). Emissions are anchored to a fixed-rate grid
    /// (t0 + n × period) driven by the L2 <see cref="DeadlineScheduler"/>, so the effective
    /// rate does not drift by the per-emission send time the way a send-then-delay loop does.
    /// Ticks whose previous emission is still in flight are skipped (no overlapping TP
    /// sessions for multi-frame PGNs); ticks that fell behind under load are coalesced by
    /// advancing the anchor instead of bursting them out. Each iteration re-checks the
    /// claim gate through <see cref="J1939NodeImpl.SendAsync"/> (address loss stops emission
    /// automatically) and surfaces failures via
    /// <see cref="J1939NodeImpl.BackgroundExceptionOccurred"/>. The payload is snapshotted
    /// into an owned buffer at construction so in-place caller mutation after Start is not
    /// observable on the wire.
    /// </summary>
    private sealed class PeriodicSchedule : IDisposable
    {
        private readonly J1939NodeImpl _owner;
        private readonly J1939Message _message;
        // In units of the owner's clock, not Stopwatch's. The anchor and the deadline it arms
        // are the same quantity measured twice, and the only way they cannot disagree is to read
        // them off one clock -- which for a test means the one it drives (#92).
        private readonly long _periodTicks;

        // Cancelled by Dispose, so an emission that waits on the bus for its confirmation ends
        // with it instead of being waited out.
        private readonly CancellationTokenSource _cts = new();
        private IDeadline? _tick;
        private long _nextAnchorTicks;
        private Task? _sendTask;
        private int _sendInFlight;
        private int _disposed;

        public PeriodicSchedule(J1939NodeImpl owner, J1939Message message, TimeSpan period)
        {
            _owner = owner;
            _periodTicks = TickMath.ToTicks(period, owner._time.Frequency);

            // Snapshot the caller's payload into an owned array so the wire traffic is
            // frozen at Start-time regardless of whether the caller mutates the buffer that
            // backs message.Payload afterwards. J1939Message.Payload is
            // a ReadOnlyMemory<byte> and its ctor doesn't copy, so re-reading it every
            // emission would alias the caller's buffer — J1939Message's own contract is
            // "payload is copied by the sender". We rebuild the message once here with the
            // owned array so every emission hands SendAsync the same immutable bytes.
            var owned = new byte[message.Payload.Length];
            if (message.Payload.Length > 0) message.Payload.Span.CopyTo(owned);
            _message = new J1939Message(message.Pgn, owned, message.Priority,
                sourceAddress: message.SourceAddress,
                destinationAddress: message.DestinationAddress);
        }

        public void Start()
        {
            // First emission after one full period, then on the fixed-rate grid.
            _nextAnchorTicks = _owner._time.GetTimestamp() + _periodTicks;
            _tick = _owner._deadlines.Arm(_owner.TimeSpanFromTicks(_periodTicks), OnTick);
        }

        private void OnTick()
        {
            if (Volatile.Read(ref _disposed) != 0) return;

            // Never overlap emissions of the same schedule (a multi-frame PGN occupies one
            // TP session per send): a tick whose previous emission is still running is
            // dropped rather than queued.
            if (Interlocked.CompareExchange(ref _sendInFlight, 1, 0) == 0)
            {
                Interlocked.Increment(ref _owner._periodicEmissionsStarted);
                _sendTask = Task.Run(async () =>
                {
                    try
                    {
                        await _owner.SendAsync(_message, _cts.Token).ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException) { /* node disposed mid-send */ }
                    catch (OperationCanceledException) when (_cts.IsCancellationRequested) { /* schedule disposed mid-send */ }
                    catch (Exception ex)
                    {
                        _owner.RaiseBackgroundException(ex);
                    }
                    finally
                    {
                        // Clear the gate first: a reader that sees the count has, by that
                        // ordering, already seen a tick become possible again.
                        Volatile.Write(ref _sendInFlight, 0);
                        Interlocked.Increment(ref _owner._periodicEmissionsCompleted);
                        // A schedule disposed while this emission was running leaves its token
                        // source to the emission: it is the last thing that holds the token.
                        if (Volatile.Read(ref _disposed) != 0) _cts.Dispose();
                    }
                });
            }

            Reschedule();
        }

        private void Reschedule()
        {
            if (Volatile.Read(ref _disposed) != 0) return;

            // Advance the anchor by whole periods until it is back in the future, coalescing
            // any ticks that fell behind (keeps the long-run rate at exactly 1/period).
            var now = _owner._time.GetTimestamp();
            do
            {
                _nextAnchorTicks += _periodTicks;
            } while (_nextAnchorTicks <= now);

            var delay = _owner.TimeSpanFromTicks(_nextAnchorTicks - now);
            // Called from the tick that just fired. That deadline has resolved, and Deadline.Rearm
            // refuses one that has left Pending, so the next tick is always a new deadline: the
            // re-arm attempt this replaced could never succeed (#355, J-8).
            _tick = _owner._deadlines.Arm(delay, OnTick);
        }

        // Actor loop, from the node's teardown: the tick goes, the handle stays the caller's. A
        // schedule is registered only once Start has armed its first tick, so there is one.
        public void StopTicking() => Volatile.Read(ref _tick)!.Dispose();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (_owner._schedulesGate) _owner._schedules.Remove(this);
            try { _tick?.Dispose(); } catch { }
            try { _cts.Cancel(); }
            catch (Exception ex) when (ex is AggregateException or ObjectDisposedException) { /* a callback threw, or the emission that ended first released it */ }

            // From the actor (a callback of the node disposing its schedule) the emission in
            // flight may be waiting for that very actor: waiting for it here would hold the
            // actor for the whole timeout. It ends on its own, the token being cancelled, and
            // releases the token source when it does (the emission cannot fault: it reports its
            // failures itself).
            var send = _sendTask;
            if (send is null) { _cts.Dispose(); return; }
            if (_owner._actor.IsOnCurrentActor) return;
            if (send.Wait(TimeSpan.FromSeconds(2))) _cts.Dispose();
        }


    }

    // Historical note: an earlier revision routed single-frame (<= 8 byte) periodic PGNs
    // through the L1 ICanBus.TransmitPeriodic / IPeriodicTx handle for lower jitter. That
    // dual-path design was reverted (PR #33) because the SW-fallback branch swallowed
    // Transmit exceptions and every attempted work-around opened a new race (reclaim-time
    // rebind, dispose-of-detached-handle, payload aliasing, actor stall, claim gate). A later
    // send-then-delay loop (SendAsync + Task.Delay) was replaced by the current fixed-rate
    // implementation: PeriodicSchedule anchors emissions on the L2 DeadlineScheduler grid
    // (t0 + n × period) so the rate no longer drifts by the per-emission send time
    // (FR-J1939-007), which is what IJ1939Node.StartPeriodicSend documents.
    //
    // The specific L1 blocker — SoftwarePeriodicTx.TrySendOnce silently swallowing every
    // Transmit exception — has now been removed: IPeriodicTx exposes a Faulted event
    // (EventHandler<Exception>) that SoftwarePeriodicTx raises outside its internal gate
    // whenever the inner Transmit call throws, and the loop stays alive so transient
    // failures no longer terminate a schedule invisibly. Wiring J1939 to subscribe to
    // Faulted and forward exceptions through BackgroundExceptionOccurred (mirroring the
    // current actor-loop path) can be done as a follow-up when the native optimization
    // is reintroduced; the L1 error-propagation prerequisite itself is resolved.
}
