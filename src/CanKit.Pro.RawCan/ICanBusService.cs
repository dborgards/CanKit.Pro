using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;

namespace CanKit.Pro.RawCan
{
    /// <summary>
    /// One demultiplexing service instance per <see cref="ICanBus"/>: it turns the single RX
    /// stream exposed by <see cref="ICanBus.FrameObserved"/> into N independent, filtered
    /// read-only <see cref="ISubscription"/>s, so multiple protocol instances (ISO-TP, J1939,
    /// CANopen, …) can each see their own view of the bus without competing over
    /// <see cref="ICanBus.ReceiveAsync"/> (arc42 §5.3, ADR-5; FR-RAW-010..013).
    /// </summary>
    /// <remarks>
    /// The service is built purely on top of the public <see cref="ICanBus.FrameObserved"/>
    /// surface (a read-only <see cref="CanFrameView"/> per frame, with no disposal/ownership
    /// concerns), so it works identically for every adapter with no per-adapter changes.
    /// Disposing the service unwinds all outstanding subscriptions and detaches its handler from
    /// the underlying bus (FR-RAW-012). Dispose is idempotent.
    /// <para>
    /// <b>Echoes.</b> When the bus is configured for echo (<c>WorkMode == ChannelWorkMode.Echo</c>)
    /// it reports the host's own transmissions back through the same RX stream, flagged as such.
    /// A subscription receives them only if it asked for them, and the flag rides along on every
    /// delivered <see cref="CanFrameEvent"/> either way. The default is off because the frames a
    /// protocol layer sends are not frames it received: a J1939 node that treats its own Address
    /// Claim as a competitor's, or a CANopen node that acts on its own PDO, is broken in a way
    /// that only shows up on hardware that happens to echo. Before this flag existed each of those
    /// layers had rebuilt "is this mine?" out of application data — comparing NAMEs, comparing
    /// source addresses, guarding on actor affinity — to recover a bit the driver had already set.
    /// </para>
    /// </remarks>
    public interface ICanBusService : IDisposable
    {
        /// <summary>
        /// The underlying bus this service demultiplexes.
        /// </summary>
        ICanBus Bus { get; }

        /// <summary>
        /// Number of currently registered (not yet disposed) subscriptions. Primarily for
        /// diagnostics/tests: after disposing every subscription it returns to zero, proving no
        /// registry entries leak (FR-RAW-012).
        /// </summary>
        int SubscriptionCount { get; }

        /// <summary>
        /// Raised when a caller-supplied subscription filter predicate throws during dispatch
        /// (FR-RAW-023-style fault channel). The failing frame is isolated to that subscription
        /// (delivery to the other subscriptions continues), and the exception is surfaced here
        /// instead of being silently swallowed. Invoked synchronously on the bus's dispatch
        /// thread, so handlers must return quickly and must not call back into the service.
        /// </summary>
        event EventHandler<Exception>? BackgroundExceptionOccurred;

        /// <summary>
        /// Registers a subscription that receives every frame for which
        /// <paramref name="predicate"/> returns true; a null predicate accepts all frames
        /// (FR-RAW-010).
        /// </summary>
        /// <param name="predicate">
        /// Per-frame filter, or null to accept all frames. Runs on the bus's dispatch thread
        /// before the payload is copied, so the <see cref="CanFrameEvent.Frame"/> it inspects
        /// aliases the adapter's RX lease and must not be retained beyond the call. It is never
        /// offered an echo unless <paramref name="includeEcho"/> is set.
        /// </param>
        /// <param name="bufferCapacity">
        /// Bounded buffer capacity for this subscription; null uses
        /// <see cref="CanBusService.DefaultBufferCapacity"/>. When the buffer is full the oldest
        /// buffered frame is dropped so dispatch never blocks (FR-RAW-011).
        /// </param>
        /// <param name="includeEcho">
        /// Whether this subscription also receives the local host's own transmit echoes. Defaults
        /// to <c>false</c>: a protocol layer that sees its own transmissions come back as if they
        /// were peer traffic misbehaves in ways that are tedious to diagnose, so opting in is a
        /// decision the caller makes deliberately (see the remarks below).
        /// </param>
        ISubscription Subscribe(Func<CanFrameEvent, bool>? predicate = null, int? bufferCapacity = null, bool includeEcho = false);

        /// <summary>
        /// Registers a subscription using the allocation-free ID-range/mask fast path
        /// (FR-RAW-010/013).
        /// </summary>
        /// <param name="filter">ID-range or acceptance-code/mask filter.</param>
        /// <param name="bufferCapacity">
        /// Bounded buffer capacity for this subscription; null uses
        /// <see cref="CanBusService.DefaultBufferCapacity"/>.
        /// </param>
        /// <param name="includeEcho">
        /// Whether this subscription also receives the local host's own transmit echoes; false by
        /// default. See the predicate overload above.
        /// </param>
        ISubscription Subscribe(CanIdFilter filter, int? bufferCapacity = null, bool includeEcho = false);

        /// <summary>
        /// Diagnostic: finds every pair of currently registered, still-undisposed
        /// <see cref="CanIdFilter"/>-based subscriptions whose ID spaces overlap (FR-RAW-041,
        /// "Should") -- helps catch misconfiguration when multiple protocol instances were meant
        /// to have disjoint ID ranges but don't. Subscriptions registered via the generic
        /// <see cref="Subscribe(Func{CanFrameEvent,bool}, int?, bool)"/> predicate overload are opaque
        /// and are not analyzable, so they are skipped.
        /// </summary>
        IReadOnlyList<(ISubscription First, ISubscription Second)> FindOverlappingFilterSubscriptions();

        /// <summary>
        /// Sends <paramref name="frame"/> and asynchronously confirms it was actually sent, using
        /// a uniform abstraction regardless of whether the underlying bus has hardware TX echo
        /// enabled (arc42 §6.3, ADR-7; FR-RAW-030). When the bus both declares
        /// <see cref="CanFeature.Echo"/> and has <c>WorkMode == ChannelWorkMode.Echo</c> configured,
        /// confirmation comes from an actually-matched echo frame (FR-RAW-031, including correct
        /// FIFO matching of multiple concurrent byte-identical sends — no cross-matching or crash);
        /// otherwise it is a documented approximation based on driver acceptance
        /// (<see cref="TxConfirmation.IsApproximated"/>, FR-RAW-032). Never hangs: timeout,
        /// bus-off, and outright rejection all resolve the returned task within bounded time
        /// (FR-RAW-033) — see <see cref="TxConfirmation"/> for exactly how.
        /// </summary>
        /// <param name="frame">
        /// The frame to send. As with <see cref="ICanBus.Transmit(in CanFrame)"/>, the caller
        /// remains the owner (TX-lease) and is responsible for disposing it after this call
        /// returns/completes — <see cref="ICanBusService"/> never disposes it.
        /// </param>
        /// <param name="timeout">
        /// Maximum time to wait for an echo before failing with
        /// <see cref="TxConfirmFailureReason.Timeout"/> (FR-RAW-034); null uses
        /// <see cref="CanBusService.DefaultConfirmTimeout"/>. Ignored on the approximated path
        /// (driver acceptance is synchronous/immediate). Must be positive.
        /// </param>
        /// <param name="cancellationToken">
        /// Caller-supplied cancellation; cancels the returned task per standard .NET convention,
        /// distinct from the domain-level <see cref="TxConfirmFailureReason.Timeout"/> outcome.
        /// </param>
        Task<TxConfirmation> SendConfirmed(CanFrame frame, TimeSpan? timeout = null, CancellationToken cancellationToken = default);
    }
}
