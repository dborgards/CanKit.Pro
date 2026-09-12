using System;
using System.Threading;
using System.Threading.Tasks;

namespace CanKit.Pro.RawCan
{
    /// <summary>
    /// Callback-style convenience layer over <see cref="ICanBusService.Subscribe(Func{CanFrameEvent,bool},int?,bool)"/>
    /// for callers who want "filter + handler" instead of driving the async-enumerable
    /// <see cref="ISubscription.Frames"/> stream themselves.
    /// </summary>
    public static class CanBusServiceExtensions
    {
        /// <summary>
        /// Registers a subscription and invokes <paramref name="onNext"/> for every frame it
        /// accepts, on a dedicated background task. Disposing the returned handle stops delivery
        /// and lets that task end.
        /// </summary>
        /// <remarks>
        /// Built entirely on the existing <see cref="ISubscription"/> pull API, so the same
        /// per-subscription bounded, drop-oldest buffer applies (FR-RAW-011): a slow
        /// <paramref name="onNext"/> only ever falls behind and drops its own oldest frames -- it
        /// can never delay delivery to other subscriptions or to the bus's own
        /// <c>FrameObserved</c> event, because the dispatch hot path never waits on a
        /// subscriber's consumer. An exception thrown by <paramref name="onNext"/> is isolated
        /// per frame -- delivery continues with the next frame -- and reported through
        /// <paramref name="onError"/> if one was given, otherwise through
        /// <see cref="ICanBusService.BackgroundExceptionOccurred"/> when <paramref name="service"/>
        /// is a <see cref="CanBusService"/>.
        /// </remarks>
        /// <param name="service">The service to subscribe on.</param>
        /// <param name="onNext">Invoked for each accepted frame, in arrival order.</param>
        /// <param name="predicate">Per-frame filter, or null to accept all frames.</param>
        /// <param name="bufferCapacity">
        /// Bounded buffer capacity for the underlying subscription; null uses
        /// <see cref="CanBusService.DefaultBufferCapacity"/>.
        /// </param>
        /// <param name="includeEcho">
        /// Whether the callback also sees the local host's own transmit echoes; false by default,
        /// for the reason given on <see cref="ICanBusService"/>.
        /// </param>
        /// <param name="onError">
        /// Where a failure of <paramref name="onNext"/> -- or of the delivery pump itself -- is
        /// reported. When given it is the single destination, in preference to the service's fault
        /// event, because a caller who passes it has said where these belong.
        /// <para>
        /// It is also the *only* destination available for an <see cref="ICanBusService"/>
        /// implementation other than <see cref="CanBusService"/>:
        /// <see cref="ICanBusService.BackgroundExceptionOccurred"/> is an event, and only the type
        /// that declares it can raise it, so this extension cannot report through a foreign
        /// implementation's fault channel however much it would like to. Without
        /// <paramref name="onError"/> such an exception has nowhere to go and is dropped -- the
        /// alternative, letting it out of the pump, would silently end delivery instead.
        /// </para>
        /// </param>
        /// <returns>Disposing this stops the subscription and the background delivery task.</returns>
        public static IDisposable Subscribe(
            this ICanBusService service,
            Action<CanFrameEvent> onNext,
            Func<CanFrameEvent, bool>? predicate = null,
            int? bufferCapacity = null,
            bool includeEcho = false,
            Action<Exception>? onError = null)
        {
            if (service is null) throw new ArgumentNullException(nameof(service));
            if (onNext is null) throw new ArgumentNullException(nameof(onNext));

            return new CallbackSubscription(
                service.Subscribe(predicate, bufferCapacity, includeEcho), service, onNext, onError);
        }

        private sealed class CallbackSubscription : IDisposable
        {
            private readonly ISubscription _subscription;
            private readonly Task _pumpTask;
            private readonly AsyncLocal<bool> _isOnPump = new();
            private int _disposed;

            public CallbackSubscription(
                ISubscription subscription,
                ICanBusService service,
                Action<CanFrameEvent> onNext,
                Action<Exception>? onError)
            {
                _subscription = subscription;
                _pumpTask = Task.Run(async () =>
                {
                    // Marks the entire pump (including onNext) so Dispose can skip the join
                    // below when invoked from the callback itself -- waiting on this task from
                    // inside it would deadlock until the timeout. Same reentrancy guard as
                    // ProtocolActor.Dispose / _isOnLoop.
                    _isOnPump.Value = true;
                    try
                    {
                        await foreach (var frameEvent in _subscription.Frames.ConfigureAwait(false))
                        {
                            // Completing the channel writer (Subscription.Dispose) does not drop
                            // items already buffered. Without these checks a self-dispose from
                            // onNext would skip the pump join and still deliver every remaining
                            // queued frame after Dispose has returned.
                            if (Volatile.Read(ref _disposed) != 0) break;

                            try
                            {
                                onNext(frameEvent);
                            }
                            catch (Exception ex)
                            {
                                Report(service, onError, ex);
                            }

                            if (Volatile.Read(ref _disposed) != 0) break;
                        }
                    }
                    catch (Exception ex)
                    {
                        // The pump itself failed, not just one handler call. Dispose's join is
                        // bounded and may already have given up on this task, so nothing else is
                        // ever going to look at it: report here rather than leave a faulted task
                        // that nobody observes.
                        Report(service, onError, ex);
                    }
                });
            }

            private static void Report(ICanBusService service, Action<Exception>? onError, Exception ex)
            {
                if (onError is not null)
                {
                    // A failing error handler must not take the pump down with it; there is by
                    // definition nowhere left to report that to.
                    try { onError(ex); } catch { /* best-effort */ }
                    return;
                }

                if (service is CanBusService concrete)
                    concrete.RaiseBackgroundException(ex);

                // Otherwise: a foreign ICanBusService and no onError. See the remarks on
                // Subscribe -- its fault event is not ours to raise.
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return; // idempotent
                // Completes the channel, which ends the pump task's `await foreach` gracefully
                // (no exception -- see Subscription.Dispose/ReadAsync).
                _subscription.Dispose();
                if (_isOnPump.Value) return;
                // Best-effort bounded join so a caller who disposes and then immediately tears
                // down surrounding state doesn't race the last in-flight onNext call; matches the
                // same dispose-teardown idiom used for background readers throughout this codebase.
                //
                // Bounded on purpose, and still bounded: an onNext that never returns cannot be
                // cancelled from here, so waiting longer would only move the hang into the caller's
                // Dispose. What the timeout leaves behind is now a task that reports its own
                // failures (see the pump's catch) instead of one nothing will ever observe.
                try { _pumpTask.Wait(TimeSpan.FromSeconds(2)); } catch { /* best-effort */ }
            }
        }
    }
}
