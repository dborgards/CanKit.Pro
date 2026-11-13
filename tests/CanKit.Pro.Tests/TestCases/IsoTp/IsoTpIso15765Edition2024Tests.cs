using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core;
using CanKit.Pro.Actor;
using CanKit.Pro.IsoTp;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;
using IsoTpFactory = CanKit.Pro.IsoTp.IsoTp;

namespace CanKit.Pro.Tests.TestCases.IsoTp;

/// <summary>
/// The behaviours #353 settled against ISO 15765-2:2024: N_Ar for the Flow Control a receiver
/// sends, N_Cr from that Flow Control's confirmation, BS/STmin from every FC(CTS), FC(OVFLW) for
/// an escape First Frame beyond <see cref="int.MaxValue"/>, and classic vs CAN FD frames on one
/// link. Receivers and senders run on a <see cref="VirtualClock"/>, so no assertion here depends
/// on how fast the runner is.
/// </summary>
public class IsoTpIso15765Edition2024Tests : IClassFixture<VirtualAdapterFixture>
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);

    private static readonly IsoTpEndpoint EpRecv = IsoTpEndpoint.Normal(txCanId: 0x7E8, rxCanId: 0x7E0);
    private static readonly IsoTpEndpoint EpPeer = IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8);

    private static string NewSession() => $"isotp24-{Guid.NewGuid():N}";

    private static ICanBus OpenClassic(string session, int channel) => CanBus.Open(
        $"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(VirtualAdapterFixture.Bitrate));

    private static ICanBus OpenCanFd(string session, int channel) => CanBus.Open(
        $"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.CanFd).Fd(VirtualAdapterFixture.Bitrate, VirtualAdapterFixture.DataBitrate));

    private static IsoTpChannelOptions Options(TimeSpan nAr, TimeSpan nCr, byte localBs = 0, bool useCanFd = false)
        => new()
        {
            UseCanFd = useCanFd,
            UsePadding = true,
            LocalBlockSize = localBs,
            NAs = TimeSpan.FromMilliseconds(500),
            NAr = nAr,
            NBs = TimeSpan.FromMilliseconds(500),
            NCr = nCr,
        };

    private static readonly byte[] Pdu20 = Enumerable.Range(0, 20).Select(i => (byte)(i + 1)).ToArray();

    private static CanFrame FirstFrame20() => CanFrame.Classic(0x7E0,
        IsoTpFrameCodec.BuildFirstFrame(EpPeer, Pdu20.Length, Pdu20.AsSpan(0, 6), isCanFd: false));

    private static CanFrame Cf20(int index) => CanFrame.Classic(0x7E0,
        IsoTpFrameCodec.BuildConsecutiveFrame(EpPeer, (byte)(index + 1), Pdu20.AsSpan(6 + (7 * index), 7),
            isCanFd: false, padding: true));

    private static readonly TxConfirmation Rejected = new()
    {
        Confirmed = false,
        Timestamp = DateTime.UtcNow,
        FailureReason = TxConfirmFailureReason.Rejected,
    };

    private static readonly TxConfirmation TimedOut = new()
    {
        Confirmed = false,
        Timestamp = DateTime.UtcNow,
        FailureReason = TxConfirmFailureReason.Timeout,
    };

    private sealed class Receiver : IDisposable
    {
        public Receiver(TimeSpan nAr, TimeSpan nCr, byte localBs,
            Func<int, Func<Task<TxConfirmation>>, Task<TxConfirmation>> onFlowControl)
        {
            var session = NewSession();
            RecvBus = OpenClassic(session, 0);
            PeerBus = OpenClassic(session, 1);
            Clock = new VirtualClock();
            Inner = new CanBusService(RecvBus);
            Service = new FlowControlConfirmService(Inner, onFlowControl);
            Actor = Clock.NewActor();
            Channel = new IsoTpChannel(Service, EpRecv, Options(nAr, nCr, localBs), ownsService: false, Actor);
        }

        public ICanBus RecvBus { get; }
        public ICanBus PeerBus { get; }
        public VirtualClock Clock { get; }
        public CanBusService Inner { get; }
        public FlowControlConfirmService Service { get; }
        public ProtocolActor Actor { get; }
        public IsoTpChannel Channel { get; }

        public void Dispose()
        {
            Channel.Dispose();
            Clock.Dispose();
            Inner.Dispose();
            PeerBus.Dispose();
            RecvBus.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------
    // N_Ar (ISO 15765-2:2024 §9.8.1 Table 22, §9.8.2 Table 23): an unconfirmed Flow Control
    // aborts the reception -- at once on a definite failure, at N_Ar when it never comes.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_Rejected_Flow_Control_Aborts_The_Reception_Without_Waiting_For_NCr()
    {
        using var r = new Receiver(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(500), 0,
            (_, _) => Task.FromResult(Rejected));
        var receive = r.Channel.ReceiveAsync();
        r.PeerBus.Transmit(FirstFrame20());

        Func<Task> act = () => receive.WaitAsync(ShortTimeout);
        var ex = (await act.Should().ThrowAsync<IsoTpException>()).Which;
        ex.Should().NotBeOfType<IsoTpTimeoutException>("the driver refused the frame; no timer ran out");
        ex.Message.Should().Contain("Rejected");
        r.Channel.GetReceptionsInProgress().Should().BeEmpty();
    }

    [Fact]
    public async Task A_Flow_Control_Send_That_Throws_Aborts_The_Reception_With_The_Failure_Inside()
    {
        var failure = new InvalidOperationException("driver gone");
        using var r = new Receiver(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(500), 0,
            (_, _) => Task.FromException<TxConfirmation>(failure));

        var receive = r.Channel.ReceiveAsync();
        r.PeerBus.Transmit(FirstFrame20());

        Func<Task> act = () => receive.WaitAsync(ShortTimeout);
        (await act.Should().ThrowAsync<IsoTpException>()).Which.InnerException.Should().BeSameAs(failure);
    }

    // A disposed service ends the subscription, and that loss ends the inbox: the Flow Control's
    // ObjectDisposedException is only reported, so it cannot overtake the loss.
    [Fact]
    public async Task A_Flow_Control_Refused_By_A_Disposed_Service_Is_Reported_Not_Aborted_On()
    {
        var failure = new ObjectDisposedException("service");
        using var r = new Receiver(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(500), 0,
            (_, _) => Task.FromException<TxConfirmation>(failure));
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        r.Channel.BackgroundExceptionOccurred += (_, ex) => reported.TrySetResult(ex);

        var receive = r.Channel.ReceiveAsync();
        r.PeerBus.Transmit(FirstFrame20());
        (await reported.Task.WaitAsync(ShortTimeout)).Should().BeSameAs(failure);
        await r.Channel.SettleAsync().WaitAsync(ShortTimeout);
        receive.IsCompleted.Should().BeFalse();

        r.PeerBus.Transmit(Cf20(0));
        r.PeerBus.Transmit(Cf20(1));
        (await receive.WaitAsync(ShortTimeout)).Should().Equal(Pdu20);
    }

    [Fact]
    public async Task A_Flow_Control_The_Driver_Reports_As_Timed_Out_Aborts_With_NAr()
    {
        using var r = new Receiver(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(500), 0,
            (_, _) => Task.FromResult(TimedOut));

        var receive = r.Channel.ReceiveAsync();
        r.PeerBus.Transmit(FirstFrame20());

        Func<Task> act = () => receive.WaitAsync(ShortTimeout);
        (await act.Should().ThrowAsync<IsoTpTimeoutException>()).Which.Timer.Should().Be(IsoTpTimer.NAr);
    }

    [Fact]
    public async Task A_Flow_Control_That_Is_Never_Confirmed_Times_Out_At_NAr_Not_NCr()
    {
        var nAr = TimeSpan.FromMilliseconds(200);
        var never = new TaskCompletionSource<TxConfirmation>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var r = new Receiver(nAr, TimeSpan.FromMilliseconds(500), 0,
            async (_, send) => { await send(); return await never.Task; });

        var receive = r.Channel.ReceiveAsync();
        r.PeerBus.Transmit(FirstFrame20());
        await r.Clock.WaitUntilTimerArmedAsync(r.Actor, nAr, ShortTimeout);

        await r.Clock.AdvanceAsync(nAr - TimeSpan.FromMilliseconds(1));
        receive.IsCompleted.Should().BeFalse("N_Ar has not run out yet");
        await r.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(2));

        Func<Task> act = () => receive.WaitAsync(ShortTimeout);
        (await act.Should().ThrowAsync<IsoTpTimeoutException>()).Which.Timer.Should().Be(IsoTpTimer.NAr);
        never.TrySetResult(Rejected);
    }

    // The driver's own N_Ar timeout arriving after the channel's deadline fired is the same
    // timeout: it is reported once, by the abort, not a second time in the background. Any
    // other late failure is still reported.
    [Theory]
    [InlineData(TxConfirmFailureReason.Timeout, 1)]
    [InlineData(TxConfirmFailureReason.Rejected, 2)]
    public async Task A_Driver_Timeout_After_NAr_Expired_Is_Not_Reported_Twice(
        TxConfirmFailureReason lateReason, int expectedReports)
    {
        var nAr = TimeSpan.FromMilliseconds(200);
        var late = new TaskCompletionSource<TxConfirmation>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var r = new Receiver(nAr, TimeSpan.FromMilliseconds(500), 0,
            async (_, send) => { await send(); return await late.Task; });
        var reports = new List<Exception>();
        r.Channel.BackgroundExceptionOccurred += (_, ex) => { lock (reports) reports.Add(ex); };

        var receive = r.Channel.ReceiveAsync();
        r.PeerBus.Transmit(FirstFrame20());
        await r.Clock.WaitUntilTimerArmedAsync(r.Actor, nAr, ShortTimeout);
        await r.Clock.AdvanceAsync(nAr + TimeSpan.FromMilliseconds(1));
        Func<Task> act = () => receive.WaitAsync(ShortTimeout);
        (await act.Should().ThrowAsync<IsoTpTimeoutException>()).Which.Timer.Should().Be(IsoTpTimer.NAr);

        late.SetResult(lateReason == TxConfirmFailureReason.Timeout ? TimedOut : Rejected);
        // The late confirmation reaches the actor from a pool thread; give it the time the
        // reported case needs, then count.
        var until = DateTime.UtcNow + TimeSpan.FromMilliseconds(300);
        while (DateTime.UtcNow < until)
        {
            lock (reports) if (reports.Count >= 2) break;
            await Task.Delay(10);
        }
        await r.Channel.SettleAsync().WaitAsync(ShortTimeout);
        lock (reports)
        {
            reports.Should().HaveCount(expectedReports);
            reports[0].Should().BeOfType<IsoTpTimeoutException>();
        }
    }

    [Fact]
    public void With_Overrides_NAr_And_The_Old_Overload_Keeps_It()
    {
        var template = new IsoTpChannelOptions { NAr = TimeSpan.FromMilliseconds(70), NCr = TimeSpan.FromMilliseconds(90) };

        var changed = template.With(nAr: TimeSpan.FromMilliseconds(40));
        changed.NAr.Should().Be(TimeSpan.FromMilliseconds(40));
        changed.NCr.Should().Be(TimeSpan.FromMilliseconds(90));

        // All eleven arguments given: binds to the overload from before NAr existed.
        var old = template.With(null, null, null, null, null, null, null, TimeSpan.FromMilliseconds(30), null, null, null);
        old.NAr.Should().Be(TimeSpan.FromMilliseconds(70));
        old.NCr.Should().Be(TimeSpan.FromMilliseconds(30));
    }

    [Fact]
    public async Task An_Unconfirmed_Block_Flow_Control_Aborts_The_Reception_Mid_Transfer()
    {
        using var r = new Receiver(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(500), localBs: 1,
            (index, send) => index == 1 ? send() : Task.FromResult(Rejected));

        var receive = r.Channel.ReceiveAsync();
        r.PeerBus.Transmit(FirstFrame20());
        // The first Flow Control confirmed: N_Cr runs.
        await r.Clock.WaitUntilTimerArmedAsync(r.Actor, TimeSpan.FromMilliseconds(500), ShortTimeout);
        r.PeerBus.Transmit(Cf20(0));

        Func<Task> act = () => receive.WaitAsync(ShortTimeout);
        (await act.Should().ThrowAsync<IsoTpException>()).Which.Should().NotBeOfType<IsoTpTimeoutException>();
        r.Service.FlowControls.Should().Be(2, "the block's Flow Control was the one that failed");
    }

    // ---------------------------------------------------------------------------------------
    // N_Cr starts at the Flow Control's confirmation (Table 22, Figure 11), not when it is
    // queued: a confirmation delayed by D moves the N_Cr deadline by D.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task NCr_Starts_When_The_Flow_Control_Is_Confirmed()
    {
        var nAr = TimeSpan.FromMilliseconds(1000);
        var nCr = TimeSpan.FromMilliseconds(300);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var r = new Receiver(nAr, nCr, 0,
            async (_, send) => { var c = await send(); await release.Task; return c; });

        var receive = r.Channel.ReceiveAsync();
        r.PeerBus.Transmit(FirstFrame20());
        await r.Clock.WaitUntilTimerArmedAsync(r.Actor, nAr, ShortTimeout);

        // Longer than N_Cr while the confirmation is outstanding: only N_Ar runs.
        await r.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(400));
        receive.IsCompleted.Should().BeFalse("N_Cr does not run before the Flow Control is confirmed");

        release.SetResult(true);
        await r.Clock.WaitUntilTimerArmedAsync(r.Actor, nCr, ShortTimeout);
        await r.Clock.AdvanceAsync(nCr - TimeSpan.FromMilliseconds(1));
        receive.IsCompleted.Should().BeFalse("N_Cr counts from the confirmation");
        await r.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(2));

        Func<Task> act = () => receive.WaitAsync(ShortTimeout);
        (await act.Should().ThrowAsync<IsoTpTimeoutException>()).Which.Timer.Should().Be(IsoTpTimer.NCr);
    }

    // A Consecutive Frame overtaking the confirmation shows the peer has the Flow Control: the
    // reception goes on, and a failure reported for that Flow Control afterwards aborts nothing.
    [Fact]
    public async Task A_Consecutive_Frame_Before_The_Confirmation_Ends_NAr()
    {
        var nAr = TimeSpan.FromMilliseconds(1000);
        var nCr = TimeSpan.FromMilliseconds(300);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var r = new Receiver(nAr, nCr, 0,
            async (_, send) => { await send(); await release.Task; return Rejected; });
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        r.Channel.BackgroundExceptionOccurred += (_, ex) => reported.TrySetResult(ex);

        var receive = r.Channel.ReceiveAsync();
        r.PeerBus.Transmit(FirstFrame20());
        await r.Clock.WaitUntilTimerArmedAsync(r.Actor, nAr, ShortTimeout);

        r.PeerBus.Transmit(Cf20(0));
        // The CF re-armed the deadline as N_Cr.
        await r.Clock.WaitUntilTimerArmedAsync(r.Actor, nCr, ShortTimeout);

        release.SetResult(true);
        (await reported.Task.WaitAsync(ShortTimeout)).Should().BeOfType<IsoTpException>()
            .Which.Message.Should().Contain("Flow Control");
        await r.Channel.SettleAsync().WaitAsync(ShortTimeout);
        receive.IsCompleted.Should().BeFalse("the late failure must not abort a reception the peer is feeding");

        r.PeerBus.Transmit(Cf20(1));
        (await receive.WaitAsync(ShortTimeout)).Should().Equal(Pdu20);
    }

    // ---------------------------------------------------------------------------------------
    // ISO 15765-2:2024 §9.6.3.2: an escape First Frame beyond int.MaxValue is a First Frame the
    // receiver cannot buffer and is answered with FC(OVFLW), once, starting nothing.
    // ---------------------------------------------------------------------------------------
    [Theory]
    [InlineData(0x8000_0000u)]
    [InlineData(0xFFFF_FFFFu)]
    public async Task An_Escape_First_Frame_Beyond_Int32_Is_Answered_With_Overflow(uint announced)
    {
        var session = NewSession();
        using var busRecv = OpenCanFd(session, 0);
        using var busPeer = OpenCanFd(session, 1);
        using var receiver = IsoTpFactory.Open(busRecv, EpRecv,
            Options(TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500), useCanFd: true));

        int overflows = 0, others = 0;
        var seen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        busPeer.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != 0x7E8) return;
            var data = e.CanFrame.Data.ToArray();
            if (data.Length >= 1 && data[0] == 0x32) Interlocked.Increment(ref overflows);
            else Interlocked.Increment(ref others);
            seen.TrySetResult(true);
        };

        var receive = receiver.ReceiveAsync();
        var ff = new byte[64];
        ff[0] = 0x10;
        ff[1] = 0x00;
        ff[2] = (byte)(announced >> 24);
        ff[3] = (byte)(announced >> 16);
        ff[4] = (byte)(announced >> 8);
        ff[5] = (byte)announced;
        busPeer.Transmit(CanFrame.Fd(0x7E0, ff));

        (await seen.Task.WaitAsync(ShortTimeout)).Should().BeTrue();
        await receiver.SettleAsync().WaitAsync(ShortTimeout);
        Volatile.Read(ref overflows).Should().Be(1);
        Volatile.Read(ref others).Should().Be(0);
        receiver.GetReceptionsInProgress().Should().BeEmpty("nothing was started or announced");
        receive.IsCompleted.Should().BeFalse("a reception that never started has nothing to fault");
    }

    // ---------------------------------------------------------------------------------------
    // ISO 15765-2:2024 §9.5.2: on a link carrying both, classic frames neither continue nor end
    // a reception carried by CAN FD frames.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Classic_Frames_Neither_Continue_Nor_End_A_CanFd_Reception()
    {
        var session = NewSession();
        using var busRecv = OpenCanFd(session, 0);
        using var busPeer = OpenCanFd(session, 1);
        using var receiver = IsoTpFactory.Open(busRecv, EpRecv,
            Options(TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(2000), useCanFd: true));
        var fcSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        busPeer.FrameObserved += (_, e) =>
        {
            var data = e.CanFrame.Data.ToArray();
            if (e.CanFrame.ID == 0x7E8 && data.Length > 0 && data[0] == 0x30) fcSeen.TrySetResult(true);
        };

        var pdu = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
        var receive = receiver.ReceiveAsync();
        busPeer.Transmit(CanFrame.Fd(0x7E0,
            IsoTpFrameCodec.BuildFirstFrame(EpPeer, pdu.Length, pdu.AsSpan(0, 62), isCanFd: true)));
        (await fcSeen.Task.WaitAsync(ShortTimeout)).Should().BeTrue();

        // A classic Single Frame and a classic CF with the expected SN: both ignored.
        busPeer.Transmit(CanFrame.Classic(0x7E0,
            IsoTpFrameCodec.BuildSingleFrame(EpPeer, new byte[] { 0xEE }, isCanFd: false, padding: true)));
        busPeer.Transmit(CanFrame.Classic(0x7E0,
            IsoTpFrameCodec.BuildConsecutiveFrame(EpPeer, 1, pdu.AsSpan(62, 7), isCanFd: false, padding: true)));
        await receiver.SettleAsync().WaitAsync(ShortTimeout);
        receive.IsCompleted.Should().BeFalse("a classic frame must not end the CAN FD reception");
        receiver.GetReceptionsInProgress().Should().HaveCount(1);

        busPeer.Transmit(CanFrame.Fd(0x7E0,
            IsoTpFrameCodec.BuildConsecutiveFrame(EpPeer, 1, pdu.AsSpan(62), isCanFd: true, padding: true)));
        (await receive.WaitAsync(ShortTimeout)).Should().Equal(pdu);
        receiver.TryReceiveWithArrival(out _).Should().BeFalse("the classic Single Frame was ignored");
    }

    // ---------------------------------------------------------------------------------------
    // ISO 15765-2:2024 §9.6.5.6 / §9.6.5.5: the sender takes STmin from every FC(CTS); a reserved
    // STmin means 127 ms for the rest of the transfer, whatever a later FC says.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task The_Sender_Takes_StMin_From_Every_Flow_Control_And_Keeps_127_Ms_After_A_Reserved_One()
    {
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);
        using var clock = new VirtualClock();
        using var service = new CanBusService(busA);
        var actor = clock.NewActor();
        using var sender = new IsoTpChannel(service, EpPeer,
            Options(TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500)), ownsService: false, actor);

        int cfs = 0;
        busB.FrameObserved += (_, e) =>
        {
            var data = e.CanFrame.Data.ToArray();
            if (e.CanFrame.ID == 0x7E0 && data.Length > 0 && (data[0] >> 4) == 0x2) Interlocked.Increment(ref cfs);
        };
        void Fc(byte stMinRaw) => busB.Transmit(CanFrame.Classic(0x7E8,
            IsoTpFrameCodec.BuildFlowControl(EpRecv, FlowStatus.ClearToSend, blockSize: 1, stMinRaw, isCanFd: false, padding: true)));
        async Task WaitForCfsAsync(int n)
        {
            var until = DateTime.UtcNow + ShortTimeout;
            while (Volatile.Read(ref cfs) < n)
            {
                if (DateTime.UtcNow > until) throw new TimeoutException($"expected {n} CFs, saw {cfs}");
                await Task.Delay(5);
            }
        }

        var nBs = TimeSpan.FromMilliseconds(500);
        // FF (6) + 4 CFs of 7 bytes, one per block.
        var send = sender.SendAsync(Enumerable.Range(0, 34).Select(i => (byte)i).ToArray());
        await clock.WaitUntilTimerArmedAsync(actor, nBs, ShortTimeout);

        Fc(0x00); // CF1 at once
        await WaitForCfsAsync(1);
        await clock.WaitUntilTimerArmedAsync(actor, nBs, ShortTimeout);

        Fc(0x14); // 20 ms
        await clock.WaitUntilTimerArmedAsync(actor, TimeSpan.FromMilliseconds(20), ShortTimeout);
        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(20));
        await WaitForCfsAsync(2);
        await clock.WaitUntilTimerArmedAsync(actor, nBs, ShortTimeout);

        Fc(0x80); // reserved -> 127 ms
        await clock.WaitUntilTimerArmedAsync(actor, TimeSpan.FromMilliseconds(127), ShortTimeout);
        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(127));
        await WaitForCfsAsync(3);
        await clock.WaitUntilTimerArmedAsync(actor, nBs, ShortTimeout);

        Fc(0x00); // still 127 ms: the reserved value holds for the rest of the transfer
        await clock.WaitUntilTimerArmedAsync(actor, TimeSpan.FromMilliseconds(127), ShortTimeout);
        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(127));
        await send.WaitAsync(ShortTimeout);
        Volatile.Read(ref cfs).Should().Be(4);
    }

    /// <summary>
    /// Forwards everything to an inner service, but hands every Flow Control send (PCI 0x3) to a
    /// test callback with its 1-based index and a delegate that really sends it.
    /// </summary>
    internal sealed class FlowControlConfirmService : ICanBusService
    {
        private readonly ICanBusService _inner;
        private readonly Func<int, Func<Task<TxConfirmation>>, Task<TxConfirmation>> _onFlowControl;
        private int _flowControls;

        public FlowControlConfirmService(ICanBusService inner,
            Func<int, Func<Task<TxConfirmation>>, Task<TxConfirmation>> onFlowControl)
        {
            _inner = inner;
            _onFlowControl = onFlowControl;
        }

        public int FlowControls => Volatile.Read(ref _flowControls);

        public ICanBus Bus => _inner.Bus;
        public int SubscriptionCount => _inner.SubscriptionCount;

        public event EventHandler<Exception>? BackgroundExceptionOccurred
        {
            add => _inner.BackgroundExceptionOccurred += value;
            remove => _inner.BackgroundExceptionOccurred -= value;
        }

        public ISubscription Subscribe(Func<CanFrameEvent, bool>? predicate = null, int? bufferCapacity = null, bool includeEcho = false)
            => _inner.Subscribe(predicate, bufferCapacity, includeEcho);

        public ISubscription Subscribe(CanIdFilter filter, int? bufferCapacity = null, bool includeEcho = false)
            => _inner.Subscribe(filter, bufferCapacity, includeEcho);

        public IReadOnlyList<FilterOverlap> FindOverlappingFilterSubscriptions()
            => _inner.FindOverlappingFilterSubscriptions();

        public Task<TxConfirmation> SendConfirmedAsync(CanFrame frame, TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            var data = frame.Data.Span;
            if (data.Length == 0 || (data[0] >> 4) != 0x3)
                return _inner.SendConfirmedAsync(frame, timeout, cancellationToken);
            int index = Interlocked.Increment(ref _flowControls);
            return _onFlowControl(index, () => _inner.SendConfirmedAsync(frame, timeout, cancellationToken));
        }

        public void Dispose() { /* the test owns the inner service */ }
    }
}
