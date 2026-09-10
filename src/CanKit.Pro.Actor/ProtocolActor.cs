using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CanKit.Pro.Actor
{
    /// <summary>
    /// Default <see cref="IProtocolActor"/>: one mailbox (<see cref="ConcurrentQueue{T}"/> of work
    /// items), one loop, one sorted list of pending timers — both owned exclusively by whichever
    /// thread is currently running the loop, so neither needs its own lock (FR-RAW-020/021). The
    /// loop blocks on a <see cref="SemaphoreSlim"/> for either new mailbox work or the next timer
    /// deadline, whichever comes first, and never polls (FR-RAW-022). Any exception from a posted
    /// work item or a fired timer is caught and raised via
    /// <see cref="BackgroundExceptionOccurred"/>; the loop keeps running afterward (FR-RAW-023).
    /// </summary>
    public sealed class ProtocolActor : IProtocolActor
    {
        // Matches the historical, hard-coded join timeout; overridable only through the internal
        // constructor, so tests do not have to spend five seconds proving the timeout is reported.
        private static readonly TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(5);

        // How many cancelled entries _timers must be carrying before a sweep is worth its O(n).
        // Small enough that a rearm-heavy protocol (an ISO-TP N_Cr refreshed per consecutive frame)
        // never accumulates a meaningful corpse tail, large enough that the sweep does not run on
        // every loop iteration of a healthy actor.
        private const int CancelledTimerSweepThreshold = 8;

        // Which actors' callbacks the *current thread* is currently inside, innermost last.
        //
        // Thread-static, not AsyncLocal: an AsyncLocal is captured into the ExecutionContext and
        // therefore flows into every Task.Run and every await continuation started from a callback,
        // so a pool thread spawned by actor work would report "I am on the actor" and take the
        // run-inline branch of a public sync API -- mutating single-writer state from a second
        // thread, and making Dispose() believe it was called reentrantly and skip its join. A
        // thread-static is correct by construction: it is a property of the thread that is actually
        // executing the callback, which is exactly the question IsOnCurrentActor asks.
        //
        // A stack rather than a single slot because callbacks can legitimately nest on one thread:
        // a work item on actor A may synchronously dispose actor B, whose FinalDrain then runs B's
        // queued work on this very thread. While inside B, A must still report true -- the
        // "synchronously waiting on A would deadlock" hazard the property exists to warn about is
        // just as real one frame down. Depth is 1 in every realistic case, so the scan is trivial.
        [ThreadStatic]
        private static List<ProtocolActor>? t_runningActors;

        private readonly ConcurrentQueue<MailboxItem> _mailbox = new();

        // Schedule() insertions go through this queue instead of _mailbox, and are always applied
        // inline on the loop thread (never marshaled through _syncContext): _timers is
        // loop-thread-owned state, not user-facing work, so it must stay on the single logical
        // thread regardless of ActorExecutionMode. Routing it through _mailbox/RunSafely would
        // defer the insert onto the SynchronizationContext's own thread in that mode, letting it
        // race with FireDueTimers/NextWaitTimeoutMilliseconds on the loop thread, and -- since
        // nothing re-signals the loop once that deferred insert finally lands -- could also leave
        // a freshly scheduled timer sitting unnoticed behind whatever (possibly much longer or
        // infinite) wait the loop already committed to.
        private readonly ConcurrentQueue<TimerEntry> _pendingTimerInserts = new();

        // Released once per Post/Schedule call; the loop waits on it (blocking or async depending
        // on execution mode) instead of polling. Over-counting is harmless: an extra pending count
        // just causes one additional, cheap, empty-ish iteration. Under-counting relative to what
        // is actually queued is harmless too, because NextWaitTimeoutMilliseconds refuses to wait
        // at all while either queue is non-empty -- the semaphore is a wake-up hint, not the
        // authority on how much work is outstanding.
        private readonly SemaphoreSlim _signal = new(0, int.MaxValue);

        // Sorted ascending by TimerEntry.DueTimestamp. Touched only by the loop thread (both when
        // draining _pendingTimerInserts and when firing due timers), so it needs no lock of its
        // own -- the same single-writer guarantee FR-RAW-021 asks every protocol instance to have
        // for its own state.
        private readonly List<TimerEntry> _timers = new();

        private readonly CancellationTokenSource _stopCts = new();
        private readonly SynchronizationContext? _syncContext;
        private readonly Thread? _dedicatedThread;
        private readonly Task? _loopTask;

        // Monotonic tick source for every due-time computation. Never DateTime.UtcNow: that is the
        // wall clock, so an NTP step (or a DST jump, or an operator setting the clock) of -1 h made
        // every armed deadline in the protocol stack fire an hour late, and +1 h made all of them
        // fire at once -- N_Bs, N_Cr, P2/P2*, SDO timeouts, the heartbeat consumer, i.e. every
        // timing guarantee the layers above exist to provide. A delay is an elapsed quantity and is
        // now measured as one.
        private readonly ITimeSource _time;

        private readonly TimeSpan _shutdownTimeout;

        private int _disposedFlag;

        // How many entries the timer machinery is carrying that have been cancelled but not yet
        // dropped. Maintained with Interlocked because TimerHandle.Dispose runs on arbitrary
        // caller threads; only ever read by the loop, and only as a heuristic (see
        // CompactCancelledTimers), so a transient over- or undercount costs at most one
        // unnecessary or deferred sweep, never correctness.
        private int _cancelledTimerCount;

        // Guards the disposed-check-then-enqueue sequence in Post/Schedule against a concurrent
        // Dispose: without it, a caller could pass ThrowIfDisposed, lose a race to Dispose setting
        // _disposedFlag, and still enqueue work that FinalDrain has already run past (or will never
        // look at again). Sharing this with Dispose's flag flip makes the two mutually exclusive,
        // so a Post/Schedule call either fully lands before Dispose is observed anywhere, or is
        // rejected with ObjectDisposedException before anything is enqueued -- closing the race
        // completely rather than merely narrowing it.
        private readonly object _disposeGate = new();

        /// <inheritdoc />
        public event EventHandler<Exception>? BackgroundExceptionOccurred;

        /// <summary>
        /// True when <i>the calling thread</i> is currently executing a work item or timer callback
        /// belonging to this actor, false otherwise.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Lets a public sync API safely detect that it is already on the actor loop and run the
        /// requested work inline instead of routing it through <see cref="PostAsync(Action)"/> and
        /// synchronously waiting on the returned task — which would deadlock the loop against
        /// itself. External callers still take the marshal-through-mailbox path exactly as
        /// before.
        /// </para>
        /// <para>
        /// Thread-scoped, deliberately: a <see cref="Task"/> started from inside a callback runs on
        /// a different thread and reports <c>false</c>, because it genuinely is not on the actor
        /// and must not mutate actor state inline. (Backing this with an
        /// <see cref="AsyncLocal{T}"/> instead would flow the flag into every such task through the
        /// <see cref="ExecutionContext"/> and report <c>true</c> there — the bug this property is
        /// most likely to be trusted with preventing.) In
        /// <see cref="ActorExecutionMode.SynchronizationContext"/> mode the flag is set on whichever
        /// thread the context actually runs the callback on, so it stays correct there too.
        /// </para>
        /// </remarks>
        public bool IsOnCurrentActor
        {
            get
            {
                var running = t_runningActors;
                if (running is null) return false;
                for (var i = running.Count - 1; i >= 0; i--)
                {
                    if (ReferenceEquals(running[i], this)) return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Creates an actor and immediately starts its mailbox loop under
        /// <paramref name="mode"/>.
        /// </summary>
        /// <param name="mode">Execution context for the loop (FR-RAW-024). Defaults to a dedicated thread.</param>
        /// <param name="synchronizationContext">
        /// Required when <paramref name="mode"/> is <see cref="ActorExecutionMode.SynchronizationContext"/>;
        /// must be null for every other mode.
        /// </param>
        public ProtocolActor(ActorExecutionMode mode = ActorExecutionMode.DedicatedThread, SynchronizationContext? synchronizationContext = null)
            : this(mode, synchronizationContext, timeSource: null, shutdownTimeout: null)
        {
        }

        // Test seam. Kept internal (and as a separate overload rather than extra optional
        // parameters on the public constructor, which would change that constructor's compiled
        // signature) so the published surface stays exactly as it was: substituting the tick source
        // is what makes timer behaviour testable without sleeping, and shortening the shutdown
        // timeout is what makes the "Dispose gave up" path testable without a five-second wait.
        internal ProtocolActor(
            ActorExecutionMode mode,
            SynchronizationContext? synchronizationContext,
            ITimeSource? timeSource,
            TimeSpan? shutdownTimeout)
        {
            if (mode == ActorExecutionMode.SynchronizationContext)
            {
                _syncContext = synchronizationContext
                    ?? throw new ArgumentNullException(nameof(synchronizationContext), $"{nameof(ActorExecutionMode.SynchronizationContext)} mode requires a non-null context.");
            }
            else if (synchronizationContext is not null)
            {
                throw new ArgumentException($"{nameof(synchronizationContext)} is only used with {nameof(ActorExecutionMode.SynchronizationContext)} mode.", nameof(synchronizationContext));
            }

            _time = timeSource ?? MonotonicTimeSource.Instance;
            _shutdownTimeout = shutdownTimeout ?? DefaultShutdownTimeout;

            if (mode == ActorExecutionMode.DedicatedThread)
            {
                // A genuine System.Threading.Thread, not an async Task with LongRunning: only a
                // real dedicated thread guarantees every iteration -- across every await-equivalent
                // wait point -- keeps running on that exact same thread (FR-RAW-024's verification
                // criterion). An async loop resumed via the thread pool after a wait has no such
                // guarantee, since nothing marshals its continuation back to one specific thread.
                _dedicatedThread = new Thread(RunLoopBlocking) { IsBackground = true, Name = "CanKit.Pro.Actor" };
                _dedicatedThread.Start();
            }
            else
            {
                _loopTask = Task.Run(RunLoopAsync);
            }
        }

        /// <inheritdoc />
        public void Post(Action work)
        {
            if (work is null) throw new ArgumentNullException(nameof(work));
            PostInternal(work, onDispatchFailure: null);
        }

        /// <inheritdoc />
        public Task PostAsync(Action work)
        {
            if (work is null) throw new ArgumentNullException(nameof(work));
            var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            PostInternal(
                () =>
                {
                    try
                    {
                        work();
                        tcs.TrySetResult(null);
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                },
                // If the marshal itself fails (SynchronizationContext mode, Send throws before
                // ever invoking the wrapped work above), the wrapper's own try/catch never runs,
                // so nothing would otherwise complete this task -- it would hang forever even
                // though PostAsync failures are documented to surface via the returned task.
                onDispatchFailure: ex => tcs.TrySetException(ex));
            return tcs.Task;
        }

        /// <inheritdoc />
        public Task<T> PostAsync<T>(Func<T> work)
        {
            if (work is null) throw new ArgumentNullException(nameof(work));
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            PostInternal(
                () =>
                {
                    try
                    {
                        tcs.TrySetResult(work());
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                },
                onDispatchFailure: ex => tcs.TrySetException(ex));
            return tcs.Task;
        }

        private void PostInternal(Action work, Action<Exception>? onDispatchFailure)
        {
            lock (_disposeGate)
            {
                ThrowIfDisposed();
                _mailbox.Enqueue(new MailboxItem(work, onDispatchFailure));
                _signal.Release();
            }
        }

        /// <inheritdoc />
        public IDisposable Schedule(TimeSpan delay, Action callback)
        {
            if (callback is null) throw new ArgumentNullException(nameof(callback));
            if (delay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delay), "Delay must not be negative.");

            var entry = new TimerEntry(DueTimestamp(delay), callback);
            lock (_disposeGate)
            {
                ThrowIfDisposed();
                // Applied by the loop itself, inline and never marshaled (see _pendingTimerInserts),
                // so _timers is only ever touched by the loop thread. _signal.Release() mirrors Post:
                // it wakes the loop promptly even if it's currently blocked waiting on a later,
                // unrelated timer deadline.
                _pendingTimerInserts.Enqueue(entry);
                _signal.Release();
            }
            return new TimerHandle(this, entry);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            // Sharing _disposeGate with Post/Schedule closes the race where a caller passes
            // ThrowIfDisposed, then loses to this flag flip, then still enqueues work that
            // FinalDrain either already ran past or will never look at again.
            lock (_disposeGate)
            {
                if (Interlocked.Exchange(ref _disposedFlag, 1) != 0) return; // idempotent
            }

            // Wakes a blocked wait immediately; further Post/Schedule calls now throw
            // ObjectDisposedException instead of silently queuing work nobody will run.
            _stopCts.Cancel();

            if (IsOnCurrentActor)
            {
                // Reentrant Dispose from within our own loop -- e.g. a Post/Schedule callback that
                // decides to dispose the very actor currently running it. Waiting below would
                // deadlock: Thread.Join/Task.Wait can never complete while the exact thread/task
                // that would complete it is blocked on itself. Once this call returns and the
                // current work item's stack unwinds, the loop's own cancellation check exits it and
                // it tears itself down on its own (see RunLoopBlocking/RunLoopAsync's finally) --
                // no wait is needed, or safe, here.
                return;
            }

            var joined = _dedicatedThread is not null
                ? _dedicatedThread.Join(_shutdownTimeout)
                : WaitForLoopTask();

            if (!joined)
            {
                // Previously this timeout was silent, so a caller could not tell a clean shutdown
                // from an abandoned loop -- the actor looked disposed while a work item was still
                // running on a live background thread, mutating state the caller believed it now
                // owned exclusively. There is exactly one defined channel for "something went wrong
                // in the background" (FR-RAW-023); report it there rather than throwing, because
                // Dispose must stay safe to call from a finally/using block.
                RaiseBackgroundException(new TimeoutException(
                    $"The {nameof(ProtocolActor)} loop did not finish within {_shutdownTimeout}. A work item or timer callback is still running; Dispose returned without joining it, and the loop will tear itself down whenever that callback eventually returns."));
            }

            // _stopCts/_signal are disposed by the loop itself once it actually finishes (see
            // RunLoopBlocking/RunLoopAsync's finally block below), never here -- so a timed-out
            // wait above (the loop still stuck inside a long-running callback) can never dispose
            // them out from under a still-running loop; they get cleaned up whenever that callback
            // eventually returns and the loop winds down on its own.
        }

        private bool WaitForLoopTask()
        {
            if (_loopTask is null) return true;
            try
            {
                return _loopTask.Wait(_shutdownTimeout);
            }
            catch (AggregateException)
            {
                // Expected: the loop observed cancellation and exited via OperationCanceledException.
                // It *did* finish, which is all this return value reports.
                return true;
            }
        }

        private void RunLoopBlocking()
        {
            try
            {
                while (true)
                {
                    if (_stopCts.IsCancellationRequested) break;

                    var timeoutMs = NextWaitTimeoutMilliseconds();
                    try
                    {
                        _signal.Wait(timeoutMs, _stopCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    DrainMailbox();
                    DrainPendingTimerInserts();
                    FireDueTimers();
                }
            }
            finally
            {
                FinalDrain();
                // Disposed here, by the loop itself, only once it has actually stopped running --
                // never by Dispose() directly (see its comment) -- so these objects are never
                // touched again after this point regardless of how long a stuck callback delayed
                // getting here.
                _stopCts.Dispose();
                _signal.Dispose();
            }
        }

        private async Task RunLoopAsync()
        {
            try
            {
                while (true)
                {
                    if (_stopCts.IsCancellationRequested) break;

                    var timeoutMs = NextWaitTimeoutMilliseconds();
                    try
                    {
                        await _signal.WaitAsync(timeoutMs, _stopCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    DrainMailbox();
                    DrainPendingTimerInserts();
                    FireDueTimers();
                }
            }
            finally
            {
                FinalDrain();
                _stopCts.Dispose();
                _signal.Dispose();
            }
        }

        // Dispose semantics: stop accepting new work (Post/Schedule throw ObjectDisposedException
        // from that point on), but run everything already queued at the moment of Dispose to
        // completion -- so a caller awaiting PostAsync exactly when Dispose happens still gets a
        // real result/exception instead of a task that hangs forever. Never waits for or fires
        // not-yet-due timers; those are simply discarded.
        private void FinalDrain()
        {
            // Unlike the live loop, this drains to genuine emptiness: the batching in DrainMailbox
            // exists to keep timers fair while the actor is running, and at shutdown there is
            // nothing left to be fair to -- completeness is the whole promise. The queues cannot
            // grow while we do this: Post/Schedule already throw, so at most a caller that won the
            // _disposeGate race is still landing its final item.
            while (!_mailbox.IsEmpty || !_pendingTimerInserts.IsEmpty)
            {
                DrainMailbox();
                DrainPendingTimerInserts();
            }

            FireDueTimers();
        }

        private int NextWaitTimeoutMilliseconds()
        {
            // Queued work outranks any wait. The semaphore's count is only a hint (DrainMailbox
            // deliberately stops short of emptying the mailbox), so asking the queues directly is
            // what actually guarantees the loop comes straight back for the remainder instead of
            // parking on a timer deadline with work still in hand.
            if (!_mailbox.IsEmpty || !_pendingTimerInserts.IsEmpty) return 0;

            CompactCancelledTimers();

            while (_timers.Count > 0 && _timers[0].IsCancelled)
            {
                var head = _timers[0];
                _timers.RemoveAt(0);
                Retire(head);
            }

            if (_timers.Count == 0) return Timeout.Infinite;

            return ToTimeoutMilliseconds(_timers[0].DueTimestamp - _time.GetTimestamp());
        }

        // Cancelled entries in the middle of _timers are invisible to the cheap head-trim above:
        // they only reach the head once every earlier entry is gone, i.e. no sooner than their own
        // original due time. A protocol that re-arms a long deadline often (an ISO-TP N_Cr or a
        // CANopen heartbeat consumer refreshed per frame) while some other, earlier timer stays
        // pending therefore carried one corpse per re-arm, each of which the O(n) InsertTimerSorted
        // then had to walk past -- turning a steady-state protocol into quadratic work. Sweeping is
        // O(n) too, so it is amortised behind a threshold rather than done on every iteration.
        private void CompactCancelledTimers()
        {
            var cancelled = Volatile.Read(ref _cancelledTimerCount);
            if (cancelled < CancelledTimerSweepThreshold) return;
            if (cancelled * 2 < _timers.Count) return; // a few corpses in a large live list: not worth the walk

            // Side-effecting predicate on purpose: RemoveAll is the only single-pass removal
            // List<T> offers, and each removed entry has to be retired (which is what keeps
            // _cancelledTimerCount honest) while we still hold a reference to it.
            _timers.RemoveAll(entry =>
            {
                if (!entry.IsCancelled) return false;
                Retire(entry);
                return true;
            });
        }

        // Takes an entry out of the timer machinery for good and keeps _cancelledTimerCount in
        // step: the counter tracks cancelled entries *still held* by _timers or
        // _pendingTimerInserts, so every drop has to be paired with the increment that
        // TimerHandle.Dispose made. Retiring also stops a later handle disposal from counting an
        // entry that is no longer anywhere -- the drift that would otherwise make every
        // `using var handle = actor.Schedule(...)` leak one phantom corpse into the threshold.
        private void Retire(TimerEntry entry)
        {
            if (entry.Retire())
                Interlocked.Decrement(ref _cancelledTimerCount);
        }

        private void DrainMailbox()
        {
            // Bounded by a snapshot of what is queued *now*, not "until empty". Every RX reader
            // posts one work item per received frame, so on a saturated bus (~8000 frames/s) an
            // unbounded drain never returns to the timer list and no deadline fires at all --
            // precisely the failure class the layers above this one exist to rule out. Taking a
            // snapshot keeps throughput (still one wake per batch, not per item) while giving the
            // timer check a guaranteed turn in between: anything that arrives during the batch is
            // simply next batch's problem, and NextWaitTimeoutMilliseconds refuses to sleep while
            // it is outstanding.
            var budget = _mailbox.Count;
            while (budget-- > 0 && _mailbox.TryDequeue(out var item))
                RunSafely(item.Work, item.OnDispatchFailure);
        }

        // Always inline on the loop thread, never marshaled through _syncContext -- see the field
        // comment on _pendingTimerInserts for why this must not go through RunSafely/_mailbox.
        private void DrainPendingTimerInserts()
        {
            while (_pendingTimerInserts.TryDequeue(out var entry))
                InsertTimerSorted(entry);
        }

        private void FireDueTimers()
        {
            var now = _time.GetTimestamp();
            while (_timers.Count > 0 && _timers[0].DueTimestamp <= now)
            {
                var entry = _timers[0];
                _timers.RemoveAt(0);

                // Retire() reports whether this entry had been cancelled, and does so atomically
                // with taking it out of circulation -- so a Dispose racing us either wins (we skip
                // the callback) or loses and finds nothing left to cancel, matching Schedule's
                // documented "a callback already in flight may still complete".
                if (!entry.Retire())
                    RunSafely(entry.Callback);
                else
                    Interlocked.Decrement(ref _cancelledTimerCount);
            }
        }

        private void InsertTimerSorted(TimerEntry entry)
        {
            if (entry.IsCancelled)
            {
                // Cancelled before the loop got around to inserting it: it never joins _timers, so
                // it must not be left counted against the sweep threshold either.
                Retire(entry);
                return;
            }

            var index = 0;
            while (index < _timers.Count && _timers[index].DueTimestamp <= entry.DueTimestamp)
                index++;
            _timers.Insert(index, entry);
        }

        private void RunSafely(Action work, Action<Exception>? onDispatchFailure = null)
        {
            if (_syncContext is not null)
            {
                // Send (blocking marshal), not Post (fire-and-forget): by the time this call
                // returns, the work has actually run on the target context, matching every other
                // execution mode's RunSafely guarantee. This is what makes FinalDrain's "queued
                // work completes before Dispose returns" promise hold for SynchronizationContext
                // mode too -- with Post, Dispose could return (and PostAsync callers could hang
                // forever) while work was still only sitting on the dispatcher's queue, never
                // actually executed. Note: as with any blocking marshal onto a UI-style context,
                // callers must not invoke Dispose() synchronously *from* the actor's own target
                // context thread -- that would block the very thread this Send needs serviced,
                // the same well-known pitfall as any synchronous wait on captured-context work.
                try
                {
                    _syncContext.Send(state =>
                    {
                        // Marked here, inside the delegate, because this runs on the context's own
                        // thread, and "am I on the actor?" is a question about the thread actually
                        // executing the callback -- not about the loop thread that is merely
                        // blocked in Send waiting for it.
                        EnterCallbackScope();
                        try { ((Action)state!)(); }
                        catch (Exception ex) { RaiseBackgroundException(ex); }
                        finally { ExitCallbackScope(); }
                    }, work);
                }
                catch (Exception ex)
                {
                    // The user callback's own exceptions are already caught and routed above; this
                    // guards against Send itself throwing (e.g. the target context rejects
                    // marshaling because it is being torn down) -- Send failing this way means
                    // `work` never even ran. A plain fire-and-forget Post has no channel for that
                    // other than BackgroundExceptionOccurred (FR-RAW-023); but PostAsync/
                    // PostAsync<T> wrap their own TaskCompletionSource completion *inside* `work`,
                    // so if it never runs, nothing else would ever complete their returned task --
                    // onDispatchFailure lets them fail it here instead of hanging forever.
                    if (onDispatchFailure is not null)
                        onDispatchFailure(ex);
                    else
                        RaiseBackgroundException(ex);
                }
                return;
            }

            EnterCallbackScope();
            try
            {
                work();
            }
            catch (Exception ex)
            {
                RaiseBackgroundException(ex);
            }
            finally
            {
                ExitCallbackScope();
            }
        }

        private void EnterCallbackScope()
        {
            var running = t_runningActors ??= new List<ProtocolActor>(2);
            running.Add(this);
        }

        private void ExitCallbackScope()
        {
            var running = t_runningActors!;
            running.RemoveAt(running.Count - 1);
        }

        private void RaiseBackgroundException(Exception ex)
        {
            try
            {
                BackgroundExceptionOccurred?.Invoke(this, ex);
            }
            catch
            {
                // A misbehaving subscriber must never be able to crash the actor loop itself.
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposedFlag) != 0)
                throw new ObjectDisposedException(nameof(ProtocolActor));
        }

        // Monotonic timestamp at which a delay scheduled *now* becomes due. Rounds the tick
        // conversion up so a timer can never come due even a fraction of a tick early -- a delay is
        // a floor ("not before"), never a target to be missed on the low side.
        private long DueTimestamp(TimeSpan delay)
        {
            var now = _time.GetTimestamp();
            var ticks = delay.TotalSeconds * _time.Frequency;

            // TimeSpan reaches ~29 000 years; the tick counter does not. Saturating is the right
            // answer for a delay nothing in this process will ever outlive anyway.
            if (ticks >= long.MaxValue - now) return long.MaxValue;

            return now + (long)Math.Ceiling(ticks);
        }

        // Rounds *up*: truncating a 0.4 ms remainder to a 0 ms wait made the loop spin on the
        // semaphore -- burning a core for up to a millisecond before every single timer -- while
        // still not firing any earlier, since the timer is not due until it is due. One extra
        // millisecond of wait costs nothing and turns the busy spin back into a single sleep.
        private int ToTimeoutMilliseconds(long remainingTicks)
        {
            if (remainingTicks <= 0) return 0;

            var ms = remainingTicks * 1000.0 / _time.Frequency;
            return ms >= int.MaxValue ? int.MaxValue : (int)Math.Ceiling(ms);
        }

        // Loop-thread-only view of the pending timer list, for tests that need to prove cancelled
        // entries are actually reclaimed rather than merely never fired. Reading _timers.Count is
        // only safe from the loop, hence the accessor rather than exposing the list.
        internal int PendingTimerCount => _timers.Count;

        // OnDispatchFailure is null for plain Post() items (BackgroundExceptionOccurred is their
        // only failure channel); PostAsync/PostAsync<T> set it to fail their own
        // TaskCompletionSource if the marshal itself fails before Work ever runs (see RunSafely).
        private readonly struct MailboxItem
        {
            public MailboxItem(Action work, Action<Exception>? onDispatchFailure)
            {
                Work = work;
                OnDispatchFailure = onDispatchFailure;
            }

            public Action Work { get; }
            public Action<Exception>? OnDispatchFailure { get; }
        }

        private sealed class TimerEntry
        {
            private const int StateLive = 0;
            private const int StateCancelled = 1;
            private const int StateRetired = 2;

            // Three states rather than a bool, because "cancelled" and "no longer held by the
            // timer machinery" are different facts and _cancelledTimerCount needs both: an entry
            // that already fired must not be counted when its handle is disposed afterwards
            // (`using var handle = actor.Schedule(...)` does exactly that, every time).
            private int _state;

            public TimerEntry(long dueTimestamp, Action callback)
            {
                DueTimestamp = dueTimestamp;
                Callback = callback;
            }

            /// <summary>Due point on the actor's monotonic tick source, not on the wall clock.</summary>
            public long DueTimestamp { get; }

            public Action Callback { get; }

            public bool IsCancelled => Volatile.Read(ref _state) == StateCancelled;

            /// <summary>
            /// Live → Cancelled. True for the first caller of a still-live entry only: a second
            /// Dispose of the same handle, or a Dispose of an entry the loop already retired, is a
            /// no-op that must not be counted.
            /// </summary>
            public bool TryCancel() => Interlocked.CompareExchange(ref _state, StateCancelled, StateLive) == StateLive;

            /// <summary>
            /// Takes the entry out of circulation, whatever it was. Returns true if it had been
            /// cancelled, i.e. if it is still counted in <c>_cancelledTimerCount</c> and if its
            /// callback must not run.
            /// </summary>
            public bool Retire() => Interlocked.Exchange(ref _state, StateRetired) == StateCancelled;
        }

        private sealed class TimerHandle : IDisposable
        {
            private readonly ProtocolActor _owner;
            private readonly TimerEntry _entry;

            public TimerHandle(ProtocolActor owner, TimerEntry entry)
            {
                _owner = owner;
                _entry = entry;
            }

            public void Dispose()
            {
                // Only flags the entry -- removing it here would touch _timers from a caller
                // thread. The loop reclaims it (see CompactCancelledTimers); this counter is how it
                // learns there is anything to reclaim without walking the list to find out.
                if (_entry.TryCancel())
                    Interlocked.Increment(ref _owner._cancelledTimerCount);
            }
        }
    }
}
