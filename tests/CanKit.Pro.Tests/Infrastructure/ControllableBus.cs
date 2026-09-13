using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common;
using CanKit.Abstractions.API.Common.Definitions;

namespace CanKit.Pro.Tests.Infrastructure;

/// <summary>
/// When the TX echo of an accepted transmit reaches <see cref="ICanBus.FrameObserved"/>.
/// </summary>
public enum EchoDelivery
{
    /// <summary>
    /// Raised from inside <see cref="ControllableBus.Transmit(in CanFrame)"/> — on the
    /// transmitting thread, inside whatever lock the caller holds while transmitting. What a real
    /// echo-mode adapter (CanKit.Adapter.Virtual in <c>ChannelWorkMode.Echo</c>) does, and what
    /// makes the reentrancy in <c>CanBusService.SendWithEchoConfirmAsync</c> observable.
    /// </summary>
    Synchronous,

    /// <summary>
    /// Parked in <see cref="ControllableBus.DeferredEchoes"/> instead of being raised. The test
    /// chooses when each echo is delivered, which lets more than one pending send exist at once —
    /// see <see cref="DeferredEchoQueue"/> for why that is not achievable synchronously.
    /// </summary>
    Deferred,
}

/// <summary>
/// An <see cref="ICanBus"/> the test drives directly: it decides whether a transmit is accepted,
/// whether (and when) a TX echo comes back, and what <see cref="BusState"/> the controller reports.
///
/// <para>
/// Why a double rather than the real loopback adapter: the TX-confirm contract
/// (SRS FR-RAW-030..034) is defined in terms of <c>CanReceiveDataView.IsEcho</c>, and whether a
/// given adapter flags its self-echo that way is an adapter detail — CanKit.Adapter.Virtual, for
/// instance, echoes in <c>ChannelWorkMode.Echo</c> without setting the flag. Testing CanKit.Pro's
/// matching logic through an adapter would therefore test the adapter's echo semantics rather than
/// ours, and would silently change meaning whenever an adapter does. Here the test states exactly
/// which echo arrives, so "no echo ever arrives" is a deliberate configuration instead of a filter
/// trick, and rejection is not tied to one adapter's frame validation.
/// </para>
///
/// <para>
/// Configuration is delegated to a genuine CanKit bus, so the double never has to invent a whole
/// configurator and cannot drift from what a real one reports. The two exceptions are the two
/// properties <c>SendConfirmed</c> branches on: <see cref="EchoCapableOptions"/> forces
/// <c>Features</c> to include <c>CanFeature.Echo</c> and <c>WorkMode</c> to <c>Echo</c>, because
/// whether an adapter declares that static capability is the adapter's business — the loopback
/// adapter does not declare it, which would silently route every echo test down the approximated
/// path instead of failing.
/// </para>
/// </summary>
public sealed class ControllableBus : ICanBus
{
    private readonly ICanBus _configurationSource;
    private readonly IBusRTOptionsConfigurator _options;
    private int _disposed;

    private ControllableBus(ICanBus configurationSource, EchoDelivery echoDelivery)
    {
        _configurationSource = configurationSource;
        _options = new EchoCapableOptions(configurationSource.Options);
        EchoMode = echoDelivery;
        DeferredEchoes = new DeferredEchoQueue(frame => RaiseObserved(frame, isEcho: true));
        // What a healthy CAN controller reports; tests move it from here.
        BusState = BusState.ErrActive;
    }

    /// <summary>
    /// Creates a double whose <see cref="Options"/> report <c>ChannelWorkMode.Echo</c> and the
    /// <c>CanFeature.Echo</c> capability — the combination that makes <c>SendConfirmed</c> take
    /// the real-echo-matching path (FR-RAW-031) — and that echoes synchronously from inside
    /// <see cref="Transmit(in CanFrame)"/>, exactly as a real echo-mode adapter does.
    /// </summary>
    public static ControllableBus EchoCapable(string session)
        => new(VirtualAdapterFixture.Open(session, 0, ChannelWorkMode.Echo), EchoDelivery.Synchronous);

    /// <summary>
    /// Same echo-capable configuration as <see cref="EchoCapable"/>, but every accepted transmit's
    /// echo is parked in <see cref="DeferredEchoes"/> until the test releases it.
    /// </summary>
    /// <remarks>
    /// Use this whenever the behaviour under test needs two or more sends to be pending at the
    /// same time. A synchronous echo makes that impossible — it re-enters
    /// <c>CanBusService</c>'s pending-send lock on the transmitting thread before that thread ever
    /// leaves <c>Transmit</c>, so the pending list only ever holds the entry belonging to the
    /// thread currently inside it, no matter how many callers race. See
    /// <see cref="DeferredEchoQueue"/>.
    /// </remarks>
    public static ControllableBus DeferredEchoCapable(string session)
        => new(VirtualAdapterFixture.Open(session, 0, ChannelWorkMode.Echo), EchoDelivery.Deferred);

    /// <summary>Whether <see cref="Transmit(in CanFrame)"/> reports the frame as accepted.</summary>
    public bool AcceptTransmit { get; set; } = true;

    /// <summary>Whether an accepted frame is echoed back through <see cref="FrameObserved"/>.</summary>
    public bool EchoAcceptedFrames { get; set; } = true;

    /// <summary>
    /// Whether an accepted frame's echo is raised inside <see cref="Transmit(in CanFrame)"/> or
    /// parked in <see cref="DeferredEchoes"/>. Settable mid-test so a scenario can, for example,
    /// let the first send confirm normally and only defer the ones it needs to overlap.
    /// </summary>
    public EchoDelivery EchoMode { get; set; }

    /// <summary>
    /// Echoes parked by <see cref="EchoDelivery.Deferred"/> mode, and the handle that releases
    /// them. Always present; stays empty while <see cref="EchoMode"/> is
    /// <see cref="EchoDelivery.Synchronous"/>.
    /// </summary>
    public DeferredEchoQueue DeferredEchoes { get; }

    /// <summary>
    /// Invoked for every accepted transmit, on the transmitting thread, after the frame is
    /// counted and before its echo is raised or parked.
    /// </summary>
    /// <remarks>
    /// The point of the hook is *where* it runs, not that it runs: <c>CanBusService</c> transmits
    /// while holding its pending-send lock, so anything done here happens with that lock held and
    /// with the transmitting send already registered. That is what lets a test act on the pending
    /// list at an instant no other thread can change it — a background continuation that wants the
    /// same lock simply waits until this returns — instead of racing one.
    /// </remarks>
    public Action<CanFrame>? OnTransmitting { get; set; }

    /// <summary>Number of frames handed to <see cref="Transmit(in CanFrame)"/>.</summary>
    public int TransmitCount => Volatile.Read(ref _transmitCount);

    private int _transmitCount;

    /// <summary>
    /// Raises <see cref="FrameObserved"/> for <paramref name="frame"/>. The default
    /// <paramref name="receiveTimestamp"/> of zero is what an adapter that does not timestamp
    /// reports; pass a real value where a test asserts the timestamp reaches the subscriber.
    /// </summary>
    public void RaiseObserved(in CanFrame frame, bool isEcho, TimeSpan receiveTimestamp = default)
        => FrameObserved?.Invoke(this, new CanReceiveDataView(
            new CanReceiveData(frame) { ReceiveTimestamp = receiveTimestamp, IsEcho = isEcho }));

    /// <summary>Raises <see cref="FaultOccurred"/>, the signal a bus-off surfaces through.</summary>
    public void RaiseFault(Exception error) => FaultOccurred?.Invoke(this, error);

    /// <summary>
    /// Raises <see cref="ErrorFrameReceived"/> once, i.e. one error frame off the wire. A degraded
    /// bus produces these in the thousands per second, which is what makes them worth driving from
    /// a test at volume rather than one at a time.
    /// </summary>
    public void RaiseErrorFrame(ICanErrorInfo? info = null)
        => ErrorFrameReceived?.Invoke(this, info ?? StubErrorInfo.Instance);

    // --- ICanBus: configuration, delegated to a real bus -------------------------------------

    public IBusRTOptionsConfigurator Options => _options;

    public BusState BusState { get; set; }

    public BusNativeHandle NativeHandle => BusNativeHandle.Zero;

    // --- ICanBus: TX path, owned by the test -------------------------------------------------

    public int Transmit(in CanFrame frame)
    {
        Interlocked.Increment(ref _transmitCount);
        if (!AcceptTransmit) return 0;

        OnTransmitting?.Invoke(frame);

        if (EchoAcceptedFrames)
        {
            // Synchronous is the default because that is what a real echo-mode adapter does, and
            // matching it is what makes the reentrancy in CanBusService.SendWithEchoConfirmAsync
            // observable. Deferred parks the echo instead, so the caller leaves Transmit — and
            // releases the service's pending-send lock — with its entry still pending; see
            // DeferredEchoQueue for why some FR-RAW-031 behaviour is only reachable that way.
            if (EchoMode == EchoDelivery.Deferred) DeferredEchoes.Park(frame);
            else RaiseObserved(frame, isEcho: true);
        }

        return 1;
    }

    public int Transmit(IEnumerable<CanFrame> frames, int timeOut = 0)
    {
        var sent = 0;
        foreach (var frame in frames) sent += Transmit(frame);
        return sent;
    }

    public int Transmit(ReadOnlySpan<CanFrame> frames, int timeOut = 0)
    {
        var sent = 0;
        foreach (var frame in frames) sent += Transmit(frame);
        return sent;
    }

    public int Transmit(CanFrame[] frames, int timeOut = 0) => Transmit((IEnumerable<CanFrame>)frames, timeOut);

    public int Transmit(ArraySegment<CanFrame> frames, int timeOut = 0) => Transmit((IEnumerable<CanFrame>)frames, timeOut);

    public Task<int> TransmitAsync(CanFrame frame, CancellationToken cancellationToken = default)
        => Task.FromResult(Transmit(frame));

    public Task<int> TransmitAsync(IEnumerable<CanFrame> frames, int timeOut = 0, CancellationToken cancellationToken = default)
        => Task.FromResult(Transmit(frames, timeOut));

    public IPeriodicTx TransmitPeriodic(CanFrame frame, PeriodicTxOptions options)
        => throw new NotSupportedException("Periodic TX is not part of what this double models.");

    // --- ICanBus: RX pull API, unused by CanKit.Pro (it consumes FrameObserved) ---------------

    public IEnumerable<CanReceiveData> Receive(int count = 1, int timeOut = 0)
        => throw new NotSupportedException("This double delivers frames through FrameObserved only.");

    public Task<IReadOnlyList<CanReceiveData>> ReceiveAsync(int count = 1, int timeOut = 0, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This double delivers frames through FrameObserved only.");

    public IAsyncEnumerable<CanReceiveData> GetFramesAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This double delivers frames through FrameObserved only.");

    // --- ICanBus: remaining surface ----------------------------------------------------------

    public void Reset() { }

    public void ClearBuffer() { }

    public float BusUsage() => 0f;

    public CanErrorCounters ErrorCounters() => default;

#pragma warning disable CS0067 // Never raised: nothing in CanKit.Pro subscribes to these.
    public event EventHandler<CanReceiveData>? FrameReceived;

    public event EventHandler<Exception>? BackgroundExceptionOccurred;
#pragma warning restore CS0067

    // Raised by RaiseErrorFrame: BusStateMonitor subscribes to this as a low-latency hint that the
    // controller's BusState may have moved, so it is not in the never-raised group above.
    public event EventHandler<ICanErrorInfo>? ErrorFrameReceived;

    public event EventHandler<CanReceiveDataView>? FrameObserved;

    public event EventHandler<Exception>? FaultOccurred;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _configurationSource.Dispose();
    }
}
