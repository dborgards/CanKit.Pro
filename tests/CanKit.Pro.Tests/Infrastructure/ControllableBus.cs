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

    private ControllableBus(ICanBus configurationSource)
    {
        _configurationSource = configurationSource;
        _options = new EchoCapableOptions(configurationSource.Options);
        // What a healthy CAN controller reports; tests move it from here.
        BusState = BusState.ErrActive;
    }

    /// <summary>
    /// Creates a double whose <see cref="Options"/> report <c>ChannelWorkMode.Echo</c> and the
    /// <c>CanFeature.Echo</c> capability — the combination that makes <c>SendConfirmed</c> take
    /// the real-echo-matching path (FR-RAW-031).
    /// </summary>
    public static ControllableBus EchoCapable(string session)
        => new(VirtualAdapterFixture.Open(session, 0, ChannelWorkMode.Echo));



    /// <summary>Whether <see cref="Transmit(in CanFrame)"/> reports the frame as accepted.</summary>
    public bool AcceptTransmit { get; set; } = true;

    /// <summary>Whether an accepted frame is echoed back through <see cref="FrameObserved"/>.</summary>
    public bool EchoAcceptedFrames { get; set; } = true;

    /// <summary>Number of frames handed to <see cref="Transmit(in CanFrame)"/>.</summary>
    public int TransmitCount => Volatile.Read(ref _transmitCount);

    private int _transmitCount;

    /// <summary>Raises <see cref="FrameObserved"/> for <paramref name="frame"/>.</summary>
    public void RaiseObserved(in CanFrame frame, bool isEcho)
        => FrameObserved?.Invoke(this, new CanReceiveDataView(
            new CanReceiveData(frame) { ReceiveTimestamp = TimeSpan.Zero, IsEcho = isEcho }));

    /// <summary>Raises <see cref="FaultOccurred"/>, the signal a bus-off surfaces through.</summary>
    public void RaiseFault(Exception error) => FaultOccurred?.Invoke(this, error);

    // --- ICanBus: configuration, delegated to a real bus -------------------------------------

    public IBusRTOptionsConfigurator Options => _options;

    public BusState BusState { get; set; }

    public BusNativeHandle NativeHandle => BusNativeHandle.Zero;

    // --- ICanBus: TX path, owned by the test -------------------------------------------------

    public int Transmit(in CanFrame frame)
    {
        Interlocked.Increment(ref _transmitCount);
        if (!AcceptTransmit) return 0;

        // A real echo-mode adapter delivers the echo synchronously from inside Transmit; matching
        // that is what makes the reentrancy in CanBusService.SendWithEchoConfirmAsync observable.
        if (EchoAcceptedFrames) RaiseObserved(frame, isEcho: true);
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

    public event EventHandler<ICanErrorInfo>? ErrorFrameReceived;

    public event EventHandler<Exception>? BackgroundExceptionOccurred;
#pragma warning restore CS0067

    public event EventHandler<CanReceiveDataView>? FrameObserved;

    public event EventHandler<Exception>? FaultOccurred;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _configurationSource.Dispose();
    }
}
