using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Pro.Tests.Infrastructure;

namespace CanKit.Pro.Tests.TestCases.CANopen.Lss;

/// <summary>
/// An <see cref="ICanBus"/> one CiA 801 detection probe listens on, driven by the test
/// (FR-CO-050). Whether a wrong bit rate shows up as error frames, a silence or a degraded bus
/// state is an adapter detail — the same reasoning that gives <see cref="ControllableBus"/> its
/// own echo semantics — so the test states exactly which of a received frame, an error frame, a
/// fault or a bus-state change arrives, instead of hoping an adapter produces it.
///
/// <para>
/// The configurator is a genuine CanKit one: the double opens a real <c>virtual://</c> bus at the
/// candidate rate and work mode, so <see cref="Options"/> report the rate and mode the detection
/// checks without the double inventing a configurator. Everything the detection reacts to —
/// <see cref="FrameObserved"/>, <see cref="ErrorFrameReceived"/>, <see cref="FaultOccurred"/> and
/// <see cref="BusState"/> — is owned here and raised on demand.
/// </para>
/// </summary>
internal sealed class DetectionProbeBus : ICanBus
{
    private readonly ICanBus _configurationSource;
    private readonly IBusRTOptionsConfigurator _options;
    private int _disposed;

    private DetectionProbeBus(ICanBus configurationSource, IBusRTOptionsConfigurator options)
    {
        _configurationSource = configurationSource;
        _options = options;
        BusState = BusState.ErrActive;
    }

    /// <summary>
    /// Opens a probe bus whose <see cref="Options"/> report <paramref name="bitsPerSecond"/> and
    /// <paramref name="workMode"/>, each on its own channel of <paramref name="session"/>.
    /// <paramref name="reportOptions"/>, when given, replaces the loopback configurator with the
    /// one it returns for it — how a test presents the options of another adapter.
    /// </summary>
    public static DetectionProbeBus Open(string session, int channel, int bitsPerSecond,
        ChannelWorkMode workMode = ChannelWorkMode.ListenOnly, bool failErrorSubscription = false,
        Func<IBusRTOptionsConfigurator, IBusRTOptionsConfigurator>? reportOptions = null)
    {
        var source = CanKit.Core.CanBus.Open(
            $"virtual://{session}/{channel}",
            cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(bitsPerSecond).SetWorkMode(workMode));
        var options = reportOptions is null ? source.Options : reportOptions(source.Options);
        return new DetectionProbeBus(source, options) { FailErrorSubscription = failErrorSubscription };
    }

    /// <summary>The last probe bus the factory handed out, for the test to raise events on.</summary>
    public static DetectionProbeBus? Last { get; private set; }

    /// <summary>Whether <see cref="Dispose"/> has run — the detection disposes every probe bus.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>When set before the detection subscribes, <see cref="ErrorFrameReceived"/> rejects
    /// the subscription, as an adapter not configured for error reporting does.</summary>
    public bool FailErrorSubscription { get; set; }

    /// <summary>
    /// A factory for <see cref="LssBitRateDetection"/> that opens one probe bus per candidate on
    /// <paramref name="session"/>, records it as <see cref="Last"/>, and collects them all.
    /// </summary>
    public static Func<int, ICanBus> Factory(string session, List<DetectionProbeBus> opened,
        ChannelWorkMode workMode = ChannelWorkMode.ListenOnly, int? reportRate = null,
        bool failErrorSubscription = false,
        Func<IBusRTOptionsConfigurator, IBusRTOptionsConfigurator>? reportOptions = null)
    {
        var channel = 0;
        return rate =>
        {
            var bus = Open(session, ++channel, reportRate ?? rate, workMode, failErrorSubscription, reportOptions);
            opened.Add(bus);
            Last = bus;
            return bus;
        };
    }

    /// <summary>
    /// Raises <see cref="FrameObserved"/> once: a received (non-echo) data frame by default, an
    /// echo when <paramref name="isEcho"/> is set, or an error frame carried on the RX stream when
    /// <paramref name="isError"/> is set (the error flag some adapters surface through this stream).
    /// </summary>
    public void RaiseFrame(bool isEcho = false, bool isError = false)
    {
        using var frame = CanFrame.Classic(0x123,
            isError ? ReadOnlyMemory<byte>.Empty : new byte[] { 0x01 }, isErrorFrame: isError);
        FrameObserved?.Invoke(this, new CanReceiveDataView(new CanReceiveData(frame) { IsEcho = isEcho }));
    }

    /// <summary>Raises <see cref="ErrorFrameReceived"/> <paramref name="count"/> times.</summary>
    public void RaiseErrors(int count)
    {
        for (var i = 0; i < count; i++)
            _errorFrameReceived?.Invoke(this, StubErrorInfo.Instance);
    }

    /// <summary>Raises <see cref="FaultOccurred"/>.</summary>
    public void RaiseFault(Exception error) => FaultOccurred?.Invoke(this, error);

    public IBusRTOptionsConfigurator Options => _options;

    public BusState BusState { get; set; }

    public BusNativeHandle NativeHandle => BusNativeHandle.Zero;

    public int Transmit(in CanFrame frame) => throw new NotSupportedException("A detection probe never transmits.");

    public int Transmit(IEnumerable<CanFrame> frames, int timeOut = 0)
        => throw new NotSupportedException("A detection probe never transmits.");

    public int Transmit(ReadOnlySpan<CanFrame> frames, int timeOut = 0)
        => throw new NotSupportedException("A detection probe never transmits.");

    public int Transmit(CanFrame[] frames, int timeOut = 0)
        => throw new NotSupportedException("A detection probe never transmits.");

    public int Transmit(ArraySegment<CanFrame> frames, int timeOut = 0)
        => throw new NotSupportedException("A detection probe never transmits.");

    public Task<int> TransmitAsync(CanFrame frame, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("A detection probe never transmits.");

    public Task<int> TransmitAsync(IEnumerable<CanFrame> frames, int timeOut = 0, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("A detection probe never transmits.");

    public IPeriodicTx TransmitPeriodic(CanFrame frame, PeriodicTxOptions options)
        => throw new NotSupportedException("A detection probe never transmits.");

    public IEnumerable<CanReceiveData> Receive(int count = 1, int timeOut = 0)
        => throw new NotSupportedException("A detection probe delivers frames through FrameObserved only.");

    public Task<IReadOnlyList<CanReceiveData>> ReceiveAsync(int count = 1, int timeOut = 0, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("A detection probe delivers frames through FrameObserved only.");

    public IAsyncEnumerable<CanReceiveData> GetFramesAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException("A detection probe delivers frames through FrameObserved only.");

    public void Reset() { }

    public void ClearBuffer() { }

    public float BusUsage() => 0f;

    public CanErrorCounters ErrorCounters() => default;

#pragma warning disable CS0067 // Never raised: the detection does not subscribe to these.
    public event EventHandler<CanReceiveData>? FrameReceived;

    public event EventHandler<Exception>? BackgroundExceptionOccurred;
#pragma warning restore CS0067

    private EventHandler<ICanErrorInfo>? _errorFrameReceived;

    /// <summary>
    /// Subscribing throws when <see cref="FailErrorSubscription"/> is set, as some adapters do when
    /// they are not configured for error reporting. The detection then falls back to the window and
    /// the bus state (<c>LssBitRateDetection.ProbeAsync</c>'s <c>TrySubscribe</c>).
    /// </summary>
    public event EventHandler<ICanErrorInfo>? ErrorFrameReceived
    {
        add
        {
            if (FailErrorSubscription)
                throw new NotSupportedException("This adapter does not offer the error-frame subscription.");
            _errorFrameReceived += value;
        }
        remove => _errorFrameReceived -= value;
    }

    public event EventHandler<CanReceiveDataView>? FrameObserved;

    public event EventHandler<Exception>? FaultOccurred;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _configurationSource.Dispose();
    }
}
