using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core;
using CanKit.Pro.Actor;
using CanKit.Pro.IsoTp;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using CanKit.Pro.Uds;
using FluentAssertions;
using Xunit;
using IsoTpFactory = CanKit.Pro.IsoTp.IsoTp;

namespace CanKit.Pro.Tests.TestCases.Uds;

/// <summary>
/// #57 — UDS over functional addressing: one request on the functional identifier, every ECU in
/// the response range may answer, and each answer is read as UDS (positive or negative).
/// </summary>
public class UdsFunctionalClientTests : IClassFixture<VirtualAdapterFixture>
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(300);

    private const uint FunctionalTxId = 0x7DF;
    private const uint Ecu1 = 0x7E8;
    private const uint Ecu2 = 0x7E9;

    private static string NewSession() => $"uds-fa-{Guid.NewGuid():N}";

    private static ICanBus OpenClassic(string session, int channel) => CanBus.Open(
        $"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(VirtualAdapterFixture.Bitrate));

    private static IsoTpFunctionalOptions FastOptions() => new()
    {
        IsExtendedCanId = false,
        UseCanFd = false,
        UsePadding = true,
        NAs = TimeSpan.FromMilliseconds(500),
    };

    /// <summary>
    /// A functional client whose P2, P2* and collection windows run on <see cref="Clock"/>,
    /// and a demux that stamps frames with that same clock. A zero stamp means "unstamped",
    /// so the clock is nudged off zero before anything is sent (#171).
    /// </summary>
    private sealed class WindowClock : IDisposable
    {
        public VirtualClock Clock { get; } = new();
        public ProtocolActor Actor { get; }

        public WindowClock()
        {
            Actor = Clock.NewActor();
            Clock.Advance(TimeSpan.FromMilliseconds(1));
        }

        public long Now => Actor.TimeSource.GetTimestamp();

        public void Dispose() => Clock.Dispose();
    }

    private static UdsFunctionalClient OpenOnClock(ICanBus bus, WindowClock clock,
        TimeSpan? responseWindow = null, TimeSpan? responsePendingWindow = null,
        IsoTpFunctionalOptions? options = null)
        => OpenOnClock(new CanBusService(bus, clock.Actor.TimeSource.GetTimestamp), clock,
            responseWindow, responsePendingWindow, options);

    private static UdsFunctionalClient OpenOnClock(ICanBusService service, WindowClock clock,
        TimeSpan? responseWindow = null, TimeSpan? responsePendingWindow = null,
        IsoTpFunctionalOptions? options = null)
    {
        var iso = new IsoTpFunctionalClient(service, FunctionalTxId, Ecu1, 0x7EF,
            options ?? FastOptions(), ownsService: true, clock.Actor);
        return UdsFunctionalClient.Create(iso, clock.Actor, ownsClient: true,
            responseWindow: responseWindow, responsePendingWindow: responsePendingWindow);
    }

    private static async Task WaitUntilArmed(WindowClock clock, TimeSpan expected, TimeSpan? lateBy = null)
        => await clock.Clock.WaitUntilTimerArmedAsync(clock.Actor, expected, ShortTimeout,
            lateBy ?? TimeSpan.FromMilliseconds(5));

    // Steps the clock until the call returns. One advance of the nominal window can stop a tick
    // short of a ceiling, or land before the collection timer is armed, and the call then sits
    // until the wall-clock budget cancels it.
    private static async Task<T> RunCall<T>(WindowClock clock, Task<T> call)
    {
        await clock.Clock.RunUntilAsync(call, TimeSpan.FromMilliseconds(25), ShortTimeout);
        return await call;
    }

    // While a collection outlasts P2 the listener keeps 20 ms slices. The call's task can
    // complete with that slice still armed, and it is shorter than the next window, so the
    // next window is not the earliest timer until the slice has elapsed and the listener retired.
    private static async Task RetireInFlightSlices(WindowClock clock)
    {
        for (var i = 0; i < 4; i++)
        {
            var delay = await clock.Actor.NextTimerDelayAsync();
            if (delay is null || delay > TimeSpan.FromMilliseconds(20)) return;
            await clock.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(21));
        }
    }

    // Steps the clock through a listener's in-flight slices -- and through nothing longer --
    // until `done` holds for the earliest armed timer. Those slices are re-armed from a thread
    // the test does not see, so "no timer armed" can mean one is about to be; this waits for
    // it rather than taking it for the end, as RetireInFlightSlices does. A window still open
    // is longer than a slice and is never stepped through: whatever waits on it times out here,
    // which is how a window anchored too late shows (#195).
    private static async Task StepThroughSlicesUntil(WindowClock clock, Func<TimeSpan?, bool> done)
    {
        var deadline = DateTime.UtcNow + ShortTimeout;
        while (true)
        {
            var delay = await clock.Actor.NextTimerDelayAsync();
            if (done(delay)) return;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(
                    $"Not done at {clock.Clock.Elapsed}; the earliest timer is {delay?.ToString() ?? "none"} away.");
            if (delay is { } slice && slice <= TimeSpan.FromMilliseconds(20))
                await clock.Clock.AdvanceAsync(slice);
            else
                await Task.Delay(1);
        }
    }

    // A call puts its listener up before the send, and the listener arms its collection for
    // the provisional window from a thread of its own: it reads the remaining time, then arms
    // that much from whatever the clock says by then. Moving the clock in between arms the
    // whole window again from the new reading, and a call waiting on the listener then waits
    // for a window that ends far too late -- measured in a full local run as a timer 990 ms
    // out where the window was long over, behind CI's "Only 1 of 2 expected echoes" (#195). So before the clock moves under a send in flight, this waits for
    // that collection to be armed; nothing else is armed yet, and the clock has not moved since
    // the call noted its window, so it is exactly the window away.
    private static Task WaitForProvisionalWindow(WindowClock clock, TimeSpan window)
        => WaitUntilArmed(clock, window);

    // For StepThroughSlicesUntil: the earliest timer is the one ending at `end`.
    private static Func<TimeSpan?, bool> ArmedUntil(WindowClock clock, TimeSpan end)
        => delay => delay is { } d && d >= end - clock.Clock.Elapsed
                    && d - (end - clock.Clock.Elapsed) <= TimeSpan.FromMilliseconds(5);

    // A collection short enough to be the earliest timer once it is armed: a listener kept by a
    // send in flight re-arms 20 ms slices, and anything longer would hide under them.
    private static readonly TimeSpan ShortCollection = TimeSpan.FromMilliseconds(10);

    // Lets a call's collection run out and returns its result, moving the clock by exactly the
    // collection. It is armed from the confirmation, on a thread the test does not see, so it
    // is waited for rather than stepped towards: stepping until the call returns runs the clock
    // on for as long as the host takes to arm it. Measured while converting these tests, that
    // was several seconds of virtual time -- enough to carry a test past the very window it was
    // checking, and a mutation it should have caught went green (#195).
    private static async Task<T> CollectFor<T>(WindowClock clock, Task<T> call, TimeSpan collection)
    {
        await WaitUntilArmed(clock, collection);
        await clock.Clock.AdvanceAsync(collection);
        return await call.WaitAsync(ShortTimeout);
    }

    private static CanFrame SingleFrameFrom(uint canId, byte[] pdu)
        => CanFrame.Classic(unchecked((int)canId),
            IsoTpFrameCodec.BuildSingleFrame(IsoTpEndpoint.Normal(canId, 0), pdu, isCanFd: false, padding: true));

    [Fact]
    public async Task A_Functional_Request_Collects_Each_Ecus_Answer_Positive_Or_Negative()
    {
        var session = NewSession();
        using var busTester = OpenClassic(session, 0);
        using var busEcus = OpenClassic(session, 1);

        // ECU 1 answers the read; ECU 2 refuses it.
        var ecu1Answer = SingleFrameFrom(Ecu1, new byte[] { 0x62, 0xF1, 0x90, 0x01 });
        var ecu2Answer = SingleFrameFrom(Ecu2, new byte[] { 0x7F, 0x22, 0x31 });
        busEcus.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != unchecked((int)FunctionalTxId)) return;
            busEcus.Transmit(ecu1Answer);
            busEcus.Transmit(ecu2Answer);
        };

        using var functional = UdsFunctionalClient.Create(
            IsoTpFactory.OpenFunctional(busTester, FunctionalTxId, Ecu1, 0x7EF, FastOptions()), ownsClient: true);

        using var cts = new CancellationTokenSource(ShortTimeout);
        var responses = await functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x90 }, Window, cts.Token);

        responses.Should().HaveCount(2);
        var fromEcu1 = responses.Single(r => r.SourceCanId == Ecu1);
        fromEcu1.IsNegative.Should().BeFalse();
        fromEcu1.Response.Should().Equal(0x62, 0xF1, 0x90, 0x01);
        var fromEcu2 = responses.Single(r => r.SourceCanId == Ecu2);
        fromEcu2.IsNegative.Should().BeTrue();
        fromEcu2.NegativeResponseCode.Should().Be(0x31);
    }

    // Codex on #150: only answers to *this* request are attributed to the call. Another
    // tester's answer on a response identifier, or a late answer to an earlier request, is not.
    [Fact]
    public async Task A_Functional_Request_Does_Not_Attribute_Unrelated_Traffic_To_Itself()
    {
        var session = NewSession();
        using var busTester = OpenClassic(session, 0);
        using var busEcus = OpenClassic(session, 1);

        var ours = SingleFrameFrom(Ecu1, new byte[] { 0x62, 0xF1, 0x90, 0x01 });
        var otherService = SingleFrameFrom(Ecu2, new byte[] { 0x50, 0x03, 0x00, 0x32, 0x01, 0xF4 });
        var otherNegative = SingleFrameFrom(Ecu2, new byte[] { 0x7F, 0x10, 0x12 });
        busEcus.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != unchecked((int)FunctionalTxId)) return;
            busEcus.Transmit(otherService);
            busEcus.Transmit(otherNegative);
            busEcus.Transmit(ours);
        };

        using var functional = UdsFunctionalClient.Create(
            IsoTpFactory.OpenFunctional(busTester, FunctionalTxId, Ecu1, 0x7EF, FastOptions()), ownsClient: true);

        using var cts = new CancellationTokenSource(ShortTimeout);
        var responses = await functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x90 }, Window, cts.Token);

        responses.Should().ContainSingle().Which.SourceCanId.Should().Be(Ecu1);
    }

    // Codex on #150: two overlapping calls with the same SID would each collect the other's
    // answers; they run one after the other, the collection window included.
    [Fact]
    public async Task Overlapping_Functional_Requests_Run_One_After_The_Other()
    {
        var session = NewSession();
        using var busTester = OpenClassic(session, 0);
        using var busEcus = OpenClassic(session, 1);

        // The ECU answers the DID it was asked for.
        busEcus.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != unchecked((int)FunctionalTxId)) return;
            var req = e.CanFrame.Data.ToArray();
            busEcus.Transmit(SingleFrameFrom(Ecu1, new byte[] { 0x62, req[2], req[3], req[3] }));
        };

        using var functional = UdsFunctionalClient.Create(
            IsoTpFactory.OpenFunctional(busTester, FunctionalTxId, Ecu1, 0x7EF, FastOptions()), ownsClient: true);

        using var cts = new CancellationTokenSource(ShortTimeout);
        var first = functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x90 }, Window, cts.Token);
        var second = functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x91 }, Window, cts.Token);
        var firstResponses = await first;
        var secondResponses = await second;

        firstResponses.Should().ContainSingle().Which.Response.Should().Equal(0x62, 0xF1, 0x90, 0x90);
        secondResponses.Should().ContainSingle().Which.Response.Should().Equal(0x62, 0xF1, 0x91, 0x91);
    }

    // Codex and Bugbot on #150: a call queued behind another's window does not go out on a
    // client disposed in the meantime.
    [Fact]
    public async Task A_Call_Queued_Behind_Another_Does_Not_Send_After_Dispose()
    {
        var session = NewSession();
        using var busTester = OpenClassic(session, 0);
        using var busEcus = OpenClassic(session, 1);

        int requestsSeen = 0;
        busEcus.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID == unchecked((int)FunctionalTxId)) Interlocked.Increment(ref requestsSeen);
        };

        using var functional = UdsFunctionalClient.Create( // a second Dispose is idempotent
            IsoTpFactory.OpenFunctional(busTester, FunctionalTxId, Ecu1, 0x7EF, FastOptions()), ownsClient: true);

        // The observable a queued call is waiting on: the second SendRawAsync finds
        // _requestLock already held by the first (its collection window still open) and starts
        // waiting on it, rather than a guess at how long the first's window needs (#171).
        var secondQueued = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        functional.RequestLockContended += () => secondQueued.TrySetResult(true);

        using var cts = new CancellationTokenSource(ShortTimeout);
        var first = functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x90 }, TimeSpan.FromMilliseconds(300), cts.Token);
        var second = functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x91 }, TimeSpan.FromMilliseconds(300), cts.Token);
        await secondQueued.Task; // the first is in its window, the second queued behind it

        functional.Dispose();

        Func<Task> act = () => second;
        await act.Should().ThrowAsync<Exception>()
            .Where(ex => ex is ObjectDisposedException || ex is OperationCanceledException,
                "the queued call must not proceed on a disposed client");
        Func<Task> firstAct = () => first;
        await firstAct.Should().ThrowAsync<Exception>(); // its window was cut short by the disposal
        requestsSeen.Should().Be(1, "only the first request reached the wire");
    }

    // Codex on #150: a service with a sub-function echoes it; a late answer for another
    // sub-function is not this request's.
    [Fact]
    public async Task A_Positive_Response_For_Another_SubFunction_Is_Not_Attributed()
    {
        var session = NewSession();
        using var busTester = OpenClassic(session, 0);
        using var busEcus = OpenClassic(session, 1);

        // Asked for Default (0x01), the ECU's late answer says Extended (0x03).
        var stale = SingleFrameFrom(Ecu1, new byte[] { 0x50, 0x03, 0x00, 0x32, 0x01, 0xF4 });
        busEcus.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID == unchecked((int)FunctionalTxId)) busEcus.Transmit(stale);
        };

        using var functional = UdsFunctionalClient.Create(
            IsoTpFactory.OpenFunctional(busTester, FunctionalTxId, Ecu1, 0x7EF, FastOptions()), ownsClient: true);

        using var cts = new CancellationTokenSource(ShortTimeout);
        var responses = await functional.DiagnosticSessionControlAsync(UdsSessionType.Default, Window, cts.Token);

        responses.Should().BeEmpty("an answer for another sub-function is an earlier request's");
    }

    // Codex on #150: a positive response echoes the request's DID; a late answer for another
    // DID arriving in this window is an earlier request's.
    [Fact]
    public async Task A_Positive_Response_For_Another_Did_Is_Not_Attributed()
    {
        var session = NewSession();
        using var busTester = OpenClassic(session, 0);
        using var busEcus = OpenClassic(session, 1);

        var stale = SingleFrameFrom(Ecu1, new byte[] { 0x62, 0xF1, 0x90, 0x01 });
        busEcus.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID == unchecked((int)FunctionalTxId)) busEcus.Transmit(stale);
        };

        using var functional = UdsFunctionalClient.Create(
            IsoTpFactory.OpenFunctional(busTester, FunctionalTxId, Ecu1, 0x7EF, FastOptions()), ownsClient: true);

        using var cts = new CancellationTokenSource(ShortTimeout);
        var responses = await functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x91 }, Window, cts.Token);

        responses.Should().BeEmpty("the answer names DID F190, the request asked for F191");
    }

    // Codex on #150: a suppressed functional send may still be answered negatively; the next
    // call for the same service waits that window out before it collects.
    [Fact]
    public async Task A_Late_Negative_Answer_To_A_Suppressed_Send_Does_Not_Land_In_The_Next_Window()
    {
        var session = NewSession();
        using var clock = new WindowClock();
        using var busTester = OpenClassic(session, 0);
        using var busEcus = OpenClassic(session, 1);

        var negative = SingleFrameFrom(Ecu1, new byte[] { 0x7F, 0x3E, 0x12 });
        var positive = SingleFrameFrom(Ecu1, new byte[] { 0x7E, 0x00 });
        busEcus.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != unchecked((int)FunctionalTxId)) return;
            if (e.CanFrame.Data.Span[2] != 0x80)
                busEcus.Transmit(positive);
        };

        using var functional = OpenOnClock(busTester, clock, responseWindow: TimeSpan.FromMilliseconds(300));

        using var cts = new CancellationTokenSource(ShortTimeout);
        await functional.TesterPresentAsync(cancellationToken: cts.Token);
        await WaitUntilArmed(clock, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(20));

        // 100 ms into the 300 ms window. On the wall clock a loaded runner has stretched this
        // past the window and the negative landed in the next call (macOS, run 36234735257).
        clock.Clock.Advance(TimeSpan.FromMilliseconds(100));
        busEcus.Transmit(negative);
        await clock.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(201));

        var second = functional.TesterPresentAsync(suppressPositiveResponse: false, Window, cts.Token);
        await WaitUntilArmed(clock, Window, TimeSpan.FromMilliseconds(20));
        var responses = await RunCall(clock, second);

        responses.Should().ContainSingle().Which.IsNegative.Should().BeFalse(
            "the late negative answer belongs to the suppressed send and is not collected");
    }

    // Codex on #150: a request's collection window may end before the ECU's P2 does; its late
    // negative answer must not land in the next same-service call's window either.
    [Fact]
    public async Task A_Late_Negative_Answer_To_A_Previous_Request_Does_Not_Land_In_The_Next_Window()
    {
        var session = NewSession();
        using var clock = new WindowClock();
        using var busTester = OpenClassic(session, 0);
        using var busEcus = OpenClassic(session, 1);

        var negative = SingleFrameFrom(Ecu1, new byte[] { 0x7F, 0x22, 0x31 });
        var positive = SingleFrameFrom(Ecu1, new byte[] { 0x62, 0xF1, 0x91, 0x02 });
        busEcus.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != unchecked((int)FunctionalTxId)) return;
            if (e.CanFrame.Data.Span[3] != 0x90)
                busEcus.Transmit(positive);
        };

        using var functional = OpenOnClock(busTester, clock, responseWindow: TimeSpan.FromMilliseconds(300));

        using var cts = new CancellationTokenSource(ShortTimeout);
        // A short collection window: the negative answer comes after it, and still inside P2.
        var first = functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x90 }, TimeSpan.FromMilliseconds(30), cts.Token);
        await WaitUntilArmed(clock, TimeSpan.FromMilliseconds(30));
        await clock.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(31));
        await first;

        clock.Clock.Advance(TimeSpan.FromMilliseconds(120)); // 151 ms from the request: after the collection, inside P2
        busEcus.Transmit(negative);
        await clock.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(160)); // P2 of 300 ms is over

        var second = functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x91 }, Window, cts.Token);
        await WaitUntilArmed(clock, Window, TimeSpan.FromMilliseconds(20));
        var responses = await RunCall(clock, second);

        responses.Should().ContainSingle().Which.IsNegative.Should().BeFalse(
            "F190's late negative answer is the previous request's");
    }

    // Bugbot on #150: a collection that is cancelled still leaves the window in place.
    [Fact]
    public async Task A_Cancelled_Collection_Still_Leaves_Its_Window_For_The_Next_Call()
    {
        var session = NewSession();
        using var clock = new WindowClock();
        using var busTester = OpenClassic(session, 0);
        using var busEcus = OpenClassic(session, 1);

        var negative = SingleFrameFrom(Ecu1, new byte[] { 0x7F, 0x22, 0x31 });
        var positive = SingleFrameFrom(Ecu1, new byte[] { 0x62, 0xF1, 0x91, 0x02 });
        var sentAt = new List<long>();
        busEcus.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != unchecked((int)FunctionalTxId)) return;
            lock (sentAt) sentAt.Add(clock.Now);
            if (e.CanFrame.Data.Span[3] != 0x90)
                busEcus.Transmit(positive);
        };

        using var functional = OpenOnClock(busTester, clock, responseWindow: TimeSpan.FromMilliseconds(300));

        using var early = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        Func<Task> cancelled = () => functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x90 }, Window, early.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();

        // The cancellation is the caller's, at 30 ms of wall time, while the clock has not moved.
        // The negative belongs to the cancelled request: after that cancellation, inside its P2.
        await WaitUntilArmed(clock, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(20));
        clock.Clock.Advance(TimeSpan.FromMilliseconds(150));
        busEcus.Transmit(negative);

        using var cts = new CancellationTokenSource(ShortTimeout);
        var second = functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x91 }, Window, cts.Token);
        // Still inside the 300 ms window: the next request must not have gone out.
        await clock.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(50));
        lock (sentAt) sentAt.Should().HaveCount(1, "the cancelled request's window is still open");

        await clock.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(110)); // past P2
        await WaitUntilArmed(clock, Window, TimeSpan.FromMilliseconds(20));
        var responses = await RunCall(clock, second);
        responses.Should().ContainSingle().Which.IsNegative.Should().BeFalse(
            "the cancelled request's late negative answer is not the next call's");
        long gap;
        lock (sentAt) gap = sentAt[1] - sentAt[0];
        TimeSpan.FromSeconds(gap / (double)clock.Actor.TimeSource.Frequency)
            .Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(250),
                "the cancelled request's window was kept for the next call");
    }

    // Codex on #150: WriteMemoryByAddress echoes the addressAndLengthFormatIdentifier and the
    // address and size it sizes; an answer for another address is not this request's.
    [Fact]
    public async Task A_Memory_Write_Is_Correlated_On_Its_Address_And_Size()
    {
        var session = NewSession();
        using var busTester = OpenClassic(session, 0);
        using var busEcus = OpenClassic(session, 1);

        // ALFID 0x11: a one-byte address (0x34) and a one-byte size (2), then two data bytes.
        var ours = SingleFrameFrom(Ecu1, new byte[] { 0x7D, 0x11, 0x34, 0x02 });
        var other = SingleFrameFrom(Ecu2, new byte[] { 0x7D, 0x11, 0x35, 0x02 });
        busEcus.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != unchecked((int)FunctionalTxId)) return;
            busEcus.Transmit(other);
            busEcus.Transmit(ours);
        };

        using var functional = UdsFunctionalClient.Create(
            IsoTpFactory.OpenFunctional(busTester, FunctionalTxId, Ecu1, 0x7EF, FastOptions()), ownsClient: true);

        using var cts = new CancellationTokenSource(ShortTimeout);
        var responses = await functional.SendRawAsync(new byte[] { 0x3D, 0x11, 0x34, 0x02, 0xAA, 0xBB }, Window, cts.Token);

        responses.Should().ContainSingle().Which.Response.Should().Equal(0x7D, 0x11, 0x34, 0x02);
    }

    // Codex on #150: RequestFileTransfer echoes its modeOfOperation; an answer for another
    // operation is not this request's. (Its positive SID is 0x78 -- not the NRC, which is
    // the third byte of a 0x7F frame.)
    [Fact]
    public async Task A_File_Transfer_Request_Is_Correlated_On_Its_Mode_Of_Operation()
    {
        var session = NewSession();
        using var busTester = OpenClassic(session, 0);
        using var busEcus = OpenClassic(session, 1);

        var ours = SingleFrameFrom(Ecu1, new byte[] { 0x78, 0x01, 0x01, 0x10 });
        var other = SingleFrameFrom(Ecu2, new byte[] { 0x78, 0x02, 0x01, 0x10 });
        busEcus.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != unchecked((int)FunctionalTxId)) return;
            busEcus.Transmit(other);
            busEcus.Transmit(ours);
        };

        using var functional = UdsFunctionalClient.Create(
            IsoTpFactory.OpenFunctional(busTester, FunctionalTxId, Ecu1, 0x7EF, FastOptions()), ownsClient: true);

        using var cts = new CancellationTokenSource(ShortTimeout);
        // AddFile (0x01), a one-byte path "A".
        var responses = await functional.SendRawAsync(new byte[] { 0x38, 0x01, 0x00, 0x01, 0x41 }, Window, cts.Token);

        responses.Should().ContainSingle().Which.Response.Should().Equal(0x78, 0x01, 0x01, 0x10);
    }

    // Codex on #150: ReadDataByPeriodicIdentifier echoes nothing; its answer names the
    // periodic identifier it carries data for, which must be one the request asked for.
    [Fact]
    public async Task A_Periodic_Read_Is_Correlated_On_The_Requested_Identifier()
    {
        var session = NewSession();
        using var busTester = OpenClassic(session, 0);
        using var busEcus = OpenClassic(session, 1);

        var ours = SingleFrameFrom(Ecu1, new byte[] { 0x6A, 0xF1, 0x11, 0x22 });
        var other = SingleFrameFrom(Ecu2, new byte[] { 0x6A, 0xF2, 0x33 });
        busEcus.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != unchecked((int)FunctionalTxId)) return;
            busEcus.Transmit(other);
            busEcus.Transmit(ours);
        };

        using var functional = UdsFunctionalClient.Create(
            IsoTpFactory.OpenFunctional(busTester, FunctionalTxId, Ecu1, 0x7EF, FastOptions()), ownsClient: true);

        using var cts = new CancellationTokenSource(ShortTimeout);
        var responses = await functional.SendRawAsync(new byte[] { 0x2A, 0x01, 0xF1 }, Window, cts.Token);

        responses.Should().ContainSingle().Which.Response.Should().Equal(0x6A, 0xF1, 0x11, 0x22);
    }

    // Codex on #150: the window is the ECU's P2 from the *transmission* -- the instant the
    // driver accepted the frame, as the physical client counts it -- not from before the
    // send. With the driver taking 200 ms to accept the frame, the window ends P2 = 1000 ms
    // after the acceptance, at 1200 ms, not at 1000.
    [Fact]
    public async Task A_Window_Is_Anchored_At_The_Drivers_Acceptance_Not_Before_The_Send()
    {
        using var clock = new WindowClock();
        using var bus = ControllableBus.DeferredEchoCapable(NewSession());
        using var functional = OpenOnClock(bus, clock, responseWindow: TimeSpan.FromMilliseconds(1000));

        var positive = SingleFrameFrom(Ecu1, new byte[] { 0x62, 0xF1, 0x91, 0x02 });
        int handedOver = 0;
        using var hold = new ManualResetEventSlim();
        var inside = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // The first frame: the driver holds it while the clock moves 200 ms, then accepts it.
        bus.OnTransmitting = frame =>
        {
            if (frame.ID != unchecked((int)FunctionalTxId) || Interlocked.Increment(ref handedOver) != 1) return;
            inside.TrySetResult(true);
            hold.Wait();
        };

        var sentFrom = clock.Clock.Elapsed;
        var acceptedAt = sentFrom + TimeSpan.FromMilliseconds(200);
        using var cts = new CancellationTokenSource(ShortTimeout);
        // On the pool: the driver's hold is inside the service's send lock, a synchronous wait.
        var first = Task.Run(() => functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x90 }, ShortCollection, cts.Token));
        await inside.Task.WaitAsync(ShortTimeout);
        await WaitForProvisionalWindow(clock, TimeSpan.FromMilliseconds(1000));
        await clock.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(200));
        hold.Set();
        await bus.DeferredEchoes.WaitForEnqueuedAsync(1, ShortTimeout);
        bus.DeferredEchoes.ReleaseNext();
        await CollectFor(clock, first, ShortCollection);

        // At 1100 ms a window ending at 1200 still holds the next call back, its listener
        // collecting what is left. Anchored before the send, the window would have ended at
        // 1000: the call would be out, and the earliest timer its own collection.
        var second = functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x91 }, Window, cts.Token);
        var windowEnd = acceptedAt + TimeSpan.FromMilliseconds(1000);
        await clock.Clock.AdvanceToAsync(sentFrom + TimeSpan.FromMilliseconds(1100));
        await StepThroughSlicesUntil(clock, ArmedUntil(clock, windowEnd));
        bus.DeferredEchoes.Enqueued.Should().Be(1, "the next call is still waiting out the window");

        // At the window's end, and without the clock moving past a slice, the call goes out.
        await clock.Clock.AdvanceToAsync(windowEnd);
        await StepThroughSlicesUntil(clock, _ => bus.DeferredEchoes.Enqueued >= 2);
        bus.RaiseObserved(positive, isEcho: false);
        bus.DeferredEchoes.ReleaseNext();
        (await RunCall(clock, second)).Should().ContainSingle(
            "the window is P2 from the driver's acceptance, not from before the send");
    }

    // Codex on #150: and not from the confirmation either. With the confirmation held 2 s past
    // the acceptance, the window -- P2 = 1000 ms from the acceptance -- is over by the time
    // the first call returns, and the next call goes out at once rather than 1000 ms later.
    [Fact]
    public async Task A_Window_Is_Anchored_At_The_Drivers_Acceptance_Not_At_The_Confirmation()
    {
        using var clock = new WindowClock();
        using var bus = ControllableBus.DeferredEchoCapable(NewSession());
        var options = new IsoTpFunctionalOptions { IsExtendedCanId = false, UseCanFd = false, UsePadding = true, NAs = ShortTimeout };
        using var functional = OpenOnClock(bus, clock, responseWindow: TimeSpan.FromMilliseconds(1000), options: options);

        var positive = SingleFrameFrom(Ecu1, new byte[] { 0x62, 0xF1, 0x91, 0x02 });

        using var cts = new CancellationTokenSource(ShortTimeout);
        var first = functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x90 }, ShortCollection, cts.Token);
        await bus.DeferredEchoes.WaitForEnqueuedAsync(1, ShortTimeout);
        await WaitForProvisionalWindow(clock, TimeSpan.FromMilliseconds(1000));
        await clock.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(2000)); // the confirmation held, the window long over
        bus.DeferredEchoes.ReleaseNext();
        await CollectFor(clock, first, ShortCollection);

        // The clock is moved from here only through the listener's in-flight slices until the
        // call has gone out. Anchored at the confirmation, the window would still have 1000 ms
        // less the collection to run, which is never stepped through: the call would never be
        // sent (#195).
        var second = functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x91 }, Window, cts.Token);
        await StepThroughSlicesUntil(clock, _ => bus.DeferredEchoes.Enqueued >= 2);
        bus.RaiseObserved(positive, isEcho: false);
        bus.DeferredEchoes.ReleaseNext();
        (await RunCall(clock, second)).Should().ContainSingle(
            "the window is P2 from the driver's acceptance, over by the time the confirmation came");
    }

    // Codex on #150: P2* runs from the 0x78's arrival, not from the end of the collection.
    // With the 0x78 at the start of a 1500 ms window and P2* = 1600 ms, the next call may go
    // out at 1600 ms after the request, not at 3100. The two readings are the collection's
    // length apart, so the collection is long: an upper bound is what a loaded host breaks,
    // and macOS CI on #150 was 300 ms late against a 300 ms difference.
    [Fact]
    public async Task A_Pending_Answer_Extends_The_Window_From_Its_Arrival_Not_From_The_Collections_End()
    {
        var session = NewSession();
        using var busTester = OpenClassic(session, 0);
        using var busEcus = OpenClassic(session, 1);

        var pending = SingleFrameFrom(Ecu1, new byte[] { 0x7F, 0x22, 0x78 });
        var positive = SingleFrameFrom(Ecu1, new byte[] { 0x62, 0xF1, 0x91, 0x02 });
        busEcus.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != unchecked((int)FunctionalTxId)) return;
            if (e.CanFrame.Data.Span[3] == 0x90) busEcus.Transmit(pending); else busEcus.Transmit(positive);
        };

        using var functional = UdsFunctionalClient.Create(
            IsoTpFactory.OpenFunctional(busTester, FunctionalTxId, Ecu1, 0x7EF, FastOptions()),
            ownsClient: true, responseWindow: TimeSpan.FromMilliseconds(100), responsePendingWindow: TimeSpan.FromMilliseconds(1600));

        using var cts = new CancellationTokenSource(ShortTimeout);
        var sw = Stopwatch.StartNew();
        await functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x90 }, TimeSpan.FromMilliseconds(1500), cts.Token);
        var responses = await functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x91 }, TimeSpan.FromMilliseconds(50), cts.Token);
        sw.Stop();

        responses.Should().ContainSingle();
        // From the 0x78 (at ~0 ms) plus 1600 ms the second call goes out and collects for
        // 50 ms: ~1650 ms in all. From the collection's end (1500 ms) plus 1600 ms it could not
        // finish before 3150 ms. The bound sits between the two, 750 ms from either.
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(2400),
            "P2* is counted from the 0x78's arrival");
    }

    // Codex on #150: a collection window longer than P2 may hold a 0x78 from after the
    // request's P2; it answers nothing the window still covers and does not revive it.
    [Fact]
    public async Task A_Pending_Answer_Collected_After_P2_Does_Not_Revive_The_Window()
    {
        var session = NewSession();
        using var clock = new WindowClock();
        using var busTester = OpenClassic(session, 0);
        using var busEcus = OpenClassic(session, 1);

        var pending = SingleFrameFrom(Ecu1, new byte[] { 0x7F, 0x22, 0x78 });
        var positive = SingleFrameFrom(Ecu1, new byte[] { 0x62, 0xF1, 0x91, 0x02 });
        busEcus.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != unchecked((int)FunctionalTxId)) return;
            if (e.CanFrame.Data.Span[3] != 0x90) busEcus.Transmit(positive);
        };

        using var functional = OpenOnClock(busTester, clock,
            responseWindow: TimeSpan.FromMilliseconds(100), responsePendingWindow: TimeSpan.FromMilliseconds(2000));

        using var cts = new CancellationTokenSource(ShortTimeout);
        // P2 = 100 ms; the 0x78 at 250 ms is inside the 400 ms collection but after P2.
        var first = functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x90 }, TimeSpan.FromMilliseconds(400), cts.Token);
        await WaitUntilArmed(clock, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(20));
        await clock.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(250));
        busEcus.Transmit(pending);
        await clock.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(151));
        await first;
        await RetireInFlightSlices(clock);

        // Revived, the window would reach 250 + 2000 ms and this call would not go out after
        // a 100 ms step. It goes out after P2, which is already over.
        var second = functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x91 }, TimeSpan.FromMilliseconds(50), cts.Token);
        await WaitUntilArmed(clock, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(20));
        await clock.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(51));
        var responses = await second;
        responses.Should().ContainSingle();
    }

    // Codex on #150: the listener's subscription is made before the send, but its worker may
    // start after the window has run out; what the subscription buffered meanwhile -- here
    // the immediate 0x78 -- is read before the worker retires, not abandoned with it.
    [Fact]
    public async Task A_Listener_Starting_After_The_Window_Still_Hears_What_It_Buffered()
    {
        var session = NewSession();
        using var clock = new WindowClock();
        using var busTester = OpenClassic(session, 0);
        using var busEcus = OpenClassic(session, 1);

        var pending = SingleFrameFrom(Ecu1, new byte[] { 0x7F, 0x10, 0x78 });
        var negative = SingleFrameFrom(Ecu1, new byte[] { 0x7F, 0x10, 0x12 });
        var positive = SingleFrameFrom(Ecu1, new byte[] { 0x50, 0x03, 0x00, 0x32, 0x01, 0xF4 });
        busEcus.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != unchecked((int)FunctionalTxId)) return;
            if ((e.CanFrame.Data.Span[2] & 0x80) != 0) busEcus.Transmit(pending);
            else busEcus.Transmit(positive);
        };

        using var functional = OpenOnClock(busTester, clock,
            responseWindow: TimeSpan.FromMilliseconds(100), responsePendingWindow: TimeSpan.FromMilliseconds(1500));
        functional.DelayListenerStart(TimeSpan.FromMilliseconds(150)); // past the 100 ms window

        using var cts = new CancellationTokenSource(ShortTimeout);
        await functional.SendRawAsync(new byte[] { 0x10, 0x83 }, Window, cts.Token); // suppressed: the 0x78 is buffered before the worker runs
        await WaitUntilArmed(clock, TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(20));
        await clock.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(150)); // the worker starts, reads the buffered 0x78
        functional.DelayListenerStart(TimeSpan.Zero); // the next call's listener is not the late one
        await WaitUntilArmed(clock, TimeSpan.FromMilliseconds(1300), TimeSpan.FromMilliseconds(300));

        // The second call waits the extended window out. The negative goes in while it is
        // waiting, at 250 ms, and must not be collected as this call's answer.
        var second = functional.DiagnosticSessionControlAsync(UdsSessionType.Extended, Window, cts.Token);
        clock.Clock.Advance(TimeSpan.FromMilliseconds(100));
        busEcus.Transmit(negative);
        // Still P2* from the buffered 0x78, not this call's own collection: a window that was
        // not extended would already be arming that shorter timer.
        await WaitUntilArmed(clock, TimeSpan.FromMilliseconds(1200), TimeSpan.FromMilliseconds(200));
        await clock.Clock.RunUntilAsync(second, TimeSpan.FromMilliseconds(50), ShortTimeout);
        var responses = await second;
        responses.Should().ContainSingle().Which.IsNegative.Should().BeFalse(
            "the late worker read the buffered 0x78 and kept the window to 1500 ms, past the negative at 250");
    }

    // Codex on #150: a cancelled collection loses what it collected, but the listener that
    // owns the window heard the 0x78 too, and the next call waits P2* from it.
    [Fact]
    public async Task A_Cancelled_Collection_Does_Not_Lose_The_Pending_Answer_The_Listener_Heard()
    {
        var session = NewSession();
        using var clock = new WindowClock();
        using var busTester = OpenClassic(session, 0);
        using var busEcus = OpenClassic(session, 1);

        var pending = SingleFrameFrom(Ecu1, new byte[] { 0x7F, 0x22, 0x78 });
        var negative = SingleFrameFrom(Ecu1, new byte[] { 0x7F, 0x22, 0x31 });
        var positive = SingleFrameFrom(Ecu1, new byte[] { 0x62, 0xF1, 0x91, 0x02 });
        busEcus.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != unchecked((int)FunctionalTxId)) return;
            if (e.CanFrame.Data.Span[3] == 0x90) busEcus.Transmit(pending);
            else busEcus.Transmit(positive);
        };

        using var functional = OpenOnClock(busTester, clock,
            responseWindow: TimeSpan.FromMilliseconds(100), responsePendingWindow: TimeSpan.FromMilliseconds(1500));

        using var early = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        Func<Task> cancelled = () => functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x90 }, Window, early.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();

        // The 0x78 went out with the request. Elapse P2 so the listener applies it, then put the
        // negative inside the P2* that application opens.
        await WaitUntilArmed(clock, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(30));
        await clock.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(101));
        await WaitUntilArmed(clock, TimeSpan.FromMilliseconds(1300), TimeSpan.FromMilliseconds(300));
        clock.Clock.Advance(TimeSpan.FromMilliseconds(150));
        busEcus.Transmit(negative);
        await clock.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(1400));

        using var cts = new CancellationTokenSource(ShortTimeout);
        var second = functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x91 }, Window, cts.Token);
        // P2* has elapsed, so this call's own 100 ms window is what is armed. A revived
        // 1500 ms window would still be the earliest timer, and this wait would not match.
        await WaitUntilArmed(clock, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(40));
        var responses = await RunCall(clock, second);
        responses.Should().ContainSingle().Which.IsNegative.Should().BeFalse(
            "the final negative answer at 250 ms belongs to the cancelled request, whose 0x78 the cancellation hid");
    }

    // Bugbot on #150: a wait cancelled by the caller does not cancel the listener, which keeps
    // the window and hears the 0x78 the caller left behind.
    [Fact]
    public async Task A_Cancelled_Wait_Leaves_The_Listener_To_Hear_The_Pending_Answer()
    {
        var session = NewSession();
        using var clock = new WindowClock();
        using var busTester = OpenClassic(session, 0);
        using var busEcus = OpenClassic(session, 1);

        var pending = SingleFrameFrom(Ecu1, new byte[] { 0x7F, 0x3E, 0x78 });
        var negative = SingleFrameFrom(Ecu1, new byte[] { 0x7F, 0x3E, 0x12 });
        var positive = SingleFrameFrom(Ecu1, new byte[] { 0x7E, 0x00 });
        busEcus.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != unchecked((int)FunctionalTxId)) return;
            if (e.CanFrame.Data.Span[2] != 0x80) busEcus.Transmit(positive);
        };

        using var functional = OpenOnClock(busTester, clock,
            responseWindow: TimeSpan.FromMilliseconds(300), responsePendingWindow: TimeSpan.FromMilliseconds(1500));

        using var cts = new CancellationTokenSource(ShortTimeout);
        await functional.TesterPresentAsync(cancellationToken: cts.Token); // suppressed
        await WaitUntilArmed(clock, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(20));
        clock.Clock.Advance(TimeSpan.FromMilliseconds(50));
        busEcus.Transmit(pending); // 0x78 inside P2; the listener applies it when the slice ends

        using var early = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        Func<Task> cancelled = () => functional.TesterPresentAsync(suppressPositiveResponse: false, Window, early.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>(); // cancelled while waiting; the 0x78 it heard is lost

        await clock.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(200)); // 250 ms: the negative, inside the extended window
        busEcus.Transmit(negative);
        await clock.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(1400)); // P2* from the 0x78 is over

        var second = functional.TesterPresentAsync(suppressPositiveResponse: false, Window, cts.Token);
        await WaitUntilArmed(clock, Window, TimeSpan.FromMilliseconds(20));
        var responses = await RunCall(clock, second);
        responses.Should().ContainSingle().Which.IsNegative.Should().BeFalse(
            "the negative at 250 ms belongs to the suppressed send; the window reaches past it");
    }

    // Codex on #150: between a suppressed send and the next call nobody was collecting, so a
    // 0x78 in that gap went unobserved. The listener is up before the send and stays for the
    // window, so the gap is observed and the next call waits P2* from the 0x78.
    [Fact]
    public async Task A_Pending_Answer_In_The_Gap_After_A_Suppressed_Send_Is_Observed()
    {
        var session = NewSession();
        using var clock = new WindowClock();
        using var busTester = OpenClassic(session, 0);
        using var busEcus = OpenClassic(session, 1);

        var pending = SingleFrameFrom(Ecu1, new byte[] { 0x7F, 0x3E, 0x78 });
        var negative = SingleFrameFrom(Ecu1, new byte[] { 0x7F, 0x3E, 0x12 });
        var positive = SingleFrameFrom(Ecu1, new byte[] { 0x7E, 0x00 });
        busEcus.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != unchecked((int)FunctionalTxId)) return;
            if (e.CanFrame.Data.Span[2] != 0x80) busEcus.Transmit(positive);
        };

        using var functional = OpenOnClock(busTester, clock,
            responseWindow: TimeSpan.FromMilliseconds(300), responsePendingWindow: TimeSpan.FromMilliseconds(1500));

        using var cts = new CancellationTokenSource(ShortTimeout);
        await functional.TesterPresentAsync(cancellationToken: cts.Token); // suppressed
        await WaitUntilArmed(clock, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(20));
        clock.Clock.Advance(TimeSpan.FromMilliseconds(100));
        busEcus.Transmit(pending); // 0x78 during the gap, inside P2

        // The next call starts while that window is still open and blocks until the listener
        // has applied the 0x78 and run P2* out. The negative at 500 ms lands in that wait.
        var second = functional.TesterPresentAsync(suppressPositiveResponse: false, Window, cts.Token);
        await clock.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(201)); // the 300 ms slice ends; the 0x78 extends it
        await WaitUntilArmed(clock, TimeSpan.FromMilliseconds(1200), TimeSpan.FromMilliseconds(200));
        clock.Clock.Advance(TimeSpan.FromMilliseconds(200)); // 500 ms: the negative, inside P2*
        busEcus.Transmit(negative);
        // The call is still waiting out P2*, so the earliest timer is that remainder, not the
        // 300 ms collection it arms only once the extension has elapsed.
        await WaitUntilArmed(clock, TimeSpan.FromMilliseconds(900), TimeSpan.FromMilliseconds(400));
        await clock.Clock.RunUntilAsync(second, TimeSpan.FromMilliseconds(50), ShortTimeout);
        var responses = await second;
        responses.Should().ContainSingle().Which.IsNegative.Should().BeFalse(
            "the 0x78 in the gap moved the window past the negative at 500 ms");
    }

    // Codex on #150: a request the functional client refuses before transmitting -- longer
    // than a Single Frame carries -- reaches no ECU, and leaves no window for the next call
    // to wait out; suppressed or not.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_Request_The_Client_Refuses_Leaves_No_Window(bool suppressed)
    {
        var session = NewSession();
        using var busTester = OpenClassic(session, 0);
        using var busEcus = OpenClassic(session, 1);
        var positive = SingleFrameFrom(Ecu1, new byte[] { 0x7E, 0x00 });
        busEcus.FrameObserved += (_, e) => { if (e.CanFrame.ID == unchecked((int)FunctionalTxId)) busEcus.Transmit(positive); };

        using var functional = UdsFunctionalClient.Create(
            IsoTpFactory.OpenFunctional(busTester, FunctionalTxId, Ecu1, 0x7EF, FastOptions()),
            ownsClient: true, responseWindow: TimeSpan.FromSeconds(2));

        using var cts = new CancellationTokenSource(ShortTimeout);
        var oversized = new byte[] { 0x3E, suppressed ? (byte)0x80 : (byte)0x00, 1, 2, 3, 4, 5, 6 }; // eight bytes: one too many
        Func<Task> refused = () => functional.SendRawAsync(oversized, Window, cts.Token);
        await refused.Should().ThrowAsync<InvalidOperationException>();

        // Left with a window, this call would wait 2 s before sending; a loaded host only
        // makes the call slower, so the bound is wide.
        var sw = Stopwatch.StartNew();
        var responses = await functional.TesterPresentAsync(suppressPositiveResponse: false, TimeSpan.FromMilliseconds(100), cts.Token);
        sw.Stop();
        responses.Should().ContainSingle();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "nothing was transmitted, so nothing is answered");
    }

    // Codex on #150: a 0x78 for the service that arrives after the request's subscription is
    // made but before the frame is handed to the driver -- another sender holding the shared
    // service's transmit lock -- answers something else: it is neither returned nor does it
    // move this request's window out.
    // With a 100 ms window the listener is in its short slices when the send returns and the
    // 0x78 is applied by the anchoring; with a 1000 ms window the listener's collection returns
    // after the anchoring and applies it itself (Bugbot on #150). Neither may move the window.
    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    public async Task A_Pending_Answer_From_Before_The_Handoff_Is_Not_This_Requests(int windowMs)
    {
        var p2 = TimeSpan.FromMilliseconds(windowMs);
        using var clock = new WindowClock();
        using var bus = ControllableBus.DeferredEchoCapable(NewSession());
        using var inner = new CanBusService(bus, clock.Actor.TimeSource.GetTimestamp);
        var service = new SendEnteredService(inner);
        using var functional = OpenOnClock(service, clock,
            responseWindow: p2, responsePendingWindow: TimeSpan.FromMilliseconds(3000));

        var pending = SingleFrameFrom(Ecu1, new byte[] { 0x7F, 0x22, 0x78 });
        var positive = SingleFrameFrom(Ecu1, new byte[] { 0x62, 0xF1, 0x90, 0x02 });

        // Another sender on the same service, held inside the driver's Transmit -- and so
        // inside the service's transmit lock -- until released.
        using var holding = new ManualResetEventSlim();
        var inside = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bus.OnTransmitting = f => { if (f.ID == 0x123) { inside.TrySetResult(true); holding.Wait(); } };
        var other = Task.Run(() => inner.SendConfirmed(CanFrame.Classic(0x123, new byte[8]), TimeSpan.FromSeconds(5)));
        await inside.Task.WaitAsync(ShortTimeout);

        // The request reaching the service's SendConfirmed is what the 0x78 must follow: by
        // then its listener and its collection have both subscribed and drained, and what is
        // left is the wait for the lock. A sleep stood in for that once, and a host that
        // delayed the request past it let the drain take the frame -- a pass for the wrong
        // reason (#195). On the pool, since the wait for the lock is synchronous.
        var reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.OnSending = f => { if (f.ID == unchecked((int)FunctionalTxId)) reached.TrySetResult(true); };
        using var cts = new CancellationTokenSource(ShortTimeout);
        var provisionalEnd = clock.Clock.Elapsed + p2; // the listener's window, noted before the send
        var first = Task.Run(() => functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x90 }, ShortCollection, cts.Token));
        await reached.Task.WaitAsync(ShortTimeout);
        await WaitForProvisionalWindow(clock, p2);
        bus.RaiseObserved(pending, isEcho: false); // before the handoff: not this request's
        // Strictly before: the handoff is stamped once the lock is free, 10 ms on.
        await clock.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(10));
        var handedOverAt = clock.Clock.Elapsed; // the clock does not move again until the request is out
        holding.Set();
        await bus.DeferredEchoes.WaitForEnqueuedAsync(1, ShortTimeout);
        bus.DeferredEchoes.ReleaseNext(); // the other sender's confirmation
        await other;
        await bus.DeferredEchoes.WaitForEnqueuedAsync(2, ShortTimeout);
        if (windowMs == 100)
        {
            // The confirmation outlasts the short window: the listener's collection ends while
            // the send is still in flight, so the 0x78 goes to the anchoring, and the listener
            // stays in 20 ms slices -- the second wait is what shows it got that far.
            await WaitUntilArmed(clock, provisionalEnd - clock.Clock.Elapsed);
            await clock.Clock.AdvanceToAsync(provisionalEnd);
            await WaitUntilArmed(clock, TimeSpan.FromMilliseconds(20));
        }
        bus.RaiseObserved(positive, isEcho: false); // after the handoff, inside the collection
        bus.DeferredEchoes.ReleaseNext(); // the request's
        var responses = await CollectFor(clock, first, ShortCollection);
        responses.Should().ContainSingle().Which.IsNegative.Should().BeFalse("the 0x78 from before the handoff is not this request's");

        // Nor did it move the window out: at the end of P2 from the handoff the next call goes
        // out with the clock moved no further than through a slice. Moved out by the 0x78 it
        // would wait for P2*, 3000 ms from the 0x78, which is never stepped through.
        var second = functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x91 }, TimeSpan.FromMilliseconds(50), cts.Token);
        var windowEnd = handedOverAt + p2;
        if (clock.Clock.Elapsed < windowEnd) await clock.Clock.AdvanceToAsync(windowEnd);
        await StepThroughSlicesUntil(clock, _ => bus.DeferredEchoes.Enqueued >= 3);
        bus.DeferredEchoes.ReleaseNext();
        await RunCall(clock, second);
    }

    // Codex on #150: the windows a client is created with are bounded as a collection window
    // is; a listener collecting for longer than a timer measures would fault and leave its
    // window unwaited.
    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(50 * 24 * 3600 * 1000L, 100)]
    [InlineData(100, 50 * 24 * 3600 * 1000L)]
    public void A_Window_Outside_A_Timers_Reach_Is_Rejected_At_Creation(long responseMs, long pendingMs)
    {
        var session = NewSession();
        using var busTester = OpenClassic(session, 0);
        using var client = IsoTpFactory.OpenFunctional(busTester, FunctionalTxId, Ecu1, 0x7EF, FastOptions());

        Action act = () => UdsFunctionalClient.Create(client, ownsClient: false,
            responseWindow: TimeSpan.FromMilliseconds(responseMs), responsePendingWindow: TimeSpan.FromMilliseconds(pendingMs));
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // Codex on #150: a window the collector would reject -- non-positive, or longer than a
    // timer measures -- is checked before anything goes out.
    [Theory]
    [InlineData(-2)]
    [InlineData(0)]
    [InlineData(long.MaxValue)]
    public async Task An_Invalid_Collection_Window_Transmits_Nothing(long windowMilliseconds)
    {
        var session = NewSession();
        using var busTester = OpenClassic(session, 0);
        using var busEcus = OpenClassic(session, 1);
        int seen = 0;
        busEcus.FrameObserved += (_, e) => { if (e.CanFrame.ID == unchecked((int)FunctionalTxId)) Interlocked.Increment(ref seen); };

        using var functional = UdsFunctionalClient.Create(
            IsoTpFactory.OpenFunctional(busTester, FunctionalTxId, Ecu1, 0x7EF, FastOptions()), ownsClient: true);

        var window = windowMilliseconds == long.MaxValue ? TimeSpan.MaxValue : TimeSpan.FromMilliseconds(windowMilliseconds);
        Func<Task> act = () => functional.DiagnosticSessionControlAsync(UdsSessionType.Extended, window);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        // A negative check on another bus: a frame sent by a regression that validated after
        // the send would reach busEcus asynchronously, so this keeps its wall window (#171).
        await Task.Delay(50);
        seen.Should().Be(0, "nothing was transmitted");
    }

    // Codex on #150: InputOutputControlByIdentifier echoes its DID; another tester's control of
    // a different DID is not this call's answer.
    [Fact]
    public async Task An_IO_Control_Answer_For_Another_Did_Is_Not_Attributed()
    {
        var session = NewSession();
        using var busTester = OpenClassic(session, 0);
        using var busEcus = OpenClassic(session, 1);

        var other = SingleFrameFrom(Ecu1, new byte[] { 0x6F, 0xF1, 0x91, 0x03, 0x00 });
        var ours = SingleFrameFrom(Ecu1, new byte[] { 0x6F, 0xF1, 0x90, 0x03, 0x00 });
        busEcus.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != unchecked((int)FunctionalTxId)) return;
            busEcus.Transmit(other);
            busEcus.Transmit(ours);
        };

        using var functional = UdsFunctionalClient.Create(
            IsoTpFactory.OpenFunctional(busTester, FunctionalTxId, Ecu1, 0x7EF, FastOptions()), ownsClient: true);

        using var cts = new CancellationTokenSource(ShortTimeout);
        var responses = await functional.SendRawAsync(new byte[] { 0x2F, 0xF1, 0x90, 0x03, 0x00 }, Window, cts.Token);

        responses.Should().ContainSingle().Which.Response.Should().Equal(0x6F, 0xF1, 0x90, 0x03, 0x00);
    }

    // Codex on #150: a driver that accepts the frame only after the provisional window has run
    // out lets the pre-send listener's window lapse while the send is in flight; anchoring the
    // window at the acceptance must move it out again, or the next call has nothing to wait for.
    // The listener itself never retires here -- the send in flight keeps it -- so this pins the
    // anchor, not a restart of the listener; there is none to make (#198).
    [Fact]
    public async Task A_Window_Is_Anchored_At_An_Acceptance_That_Outlasted_The_Provisional_Window()
    {
        using var clock = new WindowClock();
        using var bus = ControllableBus.DeferredEchoCapable(NewSession());
        var options = new IsoTpFunctionalOptions { IsExtendedCanId = false, UseCanFd = false, UsePadding = true, NAs = ShortTimeout };
        using var functional = OpenOnClock(bus, clock, responseWindow: TimeSpan.FromMilliseconds(1000), options: options);

        var negative = SingleFrameFrom(Ecu1, new byte[] { 0x7F, 0x22, 0x31 });
        var positive = SingleFrameFrom(Ecu1, new byte[] { 0x62, 0xF1, 0x91, 0x02 });
        int handedOver = 0;
        using var hold = new ManualResetEventSlim();
        var inside = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // The first frame: accepted 1100 ms after it was handed over -- longer than the
        // 1000 ms window the pre-send listener was given. The driver holds it while the clock
        // moves.
        bus.OnTransmitting = frame =>
        {
            if (frame.ID != unchecked((int)FunctionalTxId) || Interlocked.Increment(ref handedOver) != 1) return;
            inside.TrySetResult(true);
            hold.Wait();
        };

        var acceptedAt = clock.Clock.Elapsed + TimeSpan.FromMilliseconds(1100);
        var windowEnd = acceptedAt + TimeSpan.FromMilliseconds(1000);
        using var cts = new CancellationTokenSource(ShortTimeout);
        // On the pool: the driver's hold is inside the service's send lock, a synchronous wait.
        var first = Task.Run(() => functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x90 }, ShortCollection, cts.Token));
        await inside.Task.WaitAsync(ShortTimeout);
        await WaitForProvisionalWindow(clock, TimeSpan.FromMilliseconds(1000));
        await clock.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(1100));
        hold.Set();
        await bus.DeferredEchoes.WaitForEnqueuedAsync(1, ShortTimeout);
        bus.DeferredEchoes.ReleaseNext();
        await CollectFor(clock, first, ShortCollection);

        // The next call waits for a listener whose timer runs to the window anchored at the
        // acceptance. Anchored before the send, the window would have ended 1100 ms sooner:
        // over already, and the call out.
        var second = functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x91 }, Window, cts.Token);
        await StepThroughSlicesUntil(clock, ArmedUntil(clock, windowEnd));
        bus.DeferredEchoes.Enqueued.Should().Be(1, "the next call is still waiting out the window");

        // The ECU's negative answer to the first request, late but inside the window as
        // anchored at the acceptance: the waiting call must not collect it.
        bus.RaiseObserved(negative, isEcho: false);
        await clock.Clock.AdvanceToAsync(windowEnd);
        await StepThroughSlicesUntil(clock, _ => bus.DeferredEchoes.Enqueued >= 2);
        bus.RaiseObserved(positive, isEcho: false);
        bus.DeferredEchoes.ReleaseNext();
        var responses = await RunCall(clock, second);

        responses.Should().ContainSingle().Which.IsNegative.Should().BeFalse(
            "the second request waited out the window anchored at the first's late acceptance");
    }

    // Bugbot on #150: a collection that outlasts P2 anchors a window already over; that must
    // not leave a completed listener behind that blocks the next window's real one.
    [Fact]
    public async Task A_Collection_That_Outlasts_The_Window_Does_Not_Leave_A_Zombie_Listener()
    {
        var session = NewSession();
        using var clock = new WindowClock();
        using var busTester = OpenClassic(session, 0);
        using var busEcus = OpenClassic(session, 1);

        var negative = SingleFrameFrom(Ecu1, new byte[] { 0x7F, 0x22, 0x31 });
        var positive = SingleFrameFrom(Ecu1, new byte[] { 0x62, 0xF1, 0x92, 0x03 });
        int seen = 0;
        busEcus.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != unchecked((int)FunctionalTxId)) return;
            if (Interlocked.Increment(ref seen) == 3) busEcus.Transmit(positive);
        };

        using var functional = OpenOnClock(busTester, clock, responseWindow: TimeSpan.FromMilliseconds(400));

        using var cts = new CancellationTokenSource(ShortTimeout);
        // A collection longer than the window: the pre-send listener retires; the anchor after
        // it is already over and must start nothing.
        var first = functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x90 }, TimeSpan.FromMilliseconds(500), cts.Token);
        await WaitUntilArmed(clock, TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(20));
        await clock.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(501));
        await first;
        await RetireInFlightSlices(clock);
        // A short collection; its late negative, at 150 ms, needs a living listener's window.
        var second = functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x91 }, TimeSpan.FromMilliseconds(30), cts.Token);
        await WaitUntilArmed(clock, TimeSpan.FromMilliseconds(30));
        await clock.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(31));
        await second;
        clock.Clock.Advance(TimeSpan.FromMilliseconds(150));
        busEcus.Transmit(negative);
        await clock.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(251)); // the 400 ms window is over
        var third = functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x92 }, Window, cts.Token);
        await WaitUntilArmed(clock, Window, TimeSpan.FromMilliseconds(20));
        var responses = await RunCall(clock, third);

        responses.Should().ContainSingle().Which.IsNegative.Should().BeFalse(
            "the second request's window was honoured by a real listener, not blocked by a zombie");
    }

    // Codex on #150: only one DID is correlated, and a Single Frame holds no more anyway.
    [Fact]
    public async Task A_Functional_Read_For_More_Than_One_Did_Is_Refused()
    {
        var session = NewSession();
        using var busTester = OpenClassic(session, 0);
        using var functional = UdsFunctionalClient.Create(
            IsoTpFactory.OpenFunctional(busTester, FunctionalTxId, Ecu1, 0x7EF, FastOptions()), ownsClient: true);

        Func<Task> act = () => functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x90, 0xF1, 0x91 }, Window);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task An_Unsuppressed_TesterPresent_Needs_A_Window()
    {
        var session = NewSession();
        using var busTester = OpenClassic(session, 0);
        using var functional = UdsFunctionalClient.Create(
            IsoTpFactory.OpenFunctional(busTester, FunctionalTxId, Ecu1, 0x7EF, FastOptions()), ownsClient: true);

        Func<Task> act = () => functional.TesterPresentAsync(suppressPositiveResponse: false);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task TesterPresent_To_Everyone_Is_One_Suppressed_Frame_And_Collects_Nothing()
    {
        var session = NewSession();
        using var busTester = OpenClassic(session, 0);
        using var busEcus = OpenClassic(session, 1);

        var seen = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        busEcus.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID == unchecked((int)FunctionalTxId)) seen.TrySetResult(e.CanFrame.Data.ToArray());
        };

        using var functional = UdsFunctionalClient.Create(
            IsoTpFactory.OpenFunctional(busTester, FunctionalTxId, Ecu1, 0x7EF, FastOptions()), ownsClient: true);

        using var cts = new CancellationTokenSource(ShortTimeout);
        var responses = await functional.TesterPresentAsync(cancellationToken: cts.Token);

        responses.Should().BeEmpty("a suppressed request is not collected for");
        var frame = await seen.Task.WaitAsync(ShortTimeout);
        frame.Take(3).Should().Equal(0x02, 0x3E, 0x80);
    }

    [Fact]
    public async Task DiagnosticSessionControl_To_Everyone_Collects_Each_Ecus_Session_Answer()
    {
        var session = NewSession();
        using var busTester = OpenClassic(session, 0);
        using var busEcus = OpenClassic(session, 1);

        var ecu1Answer = SingleFrameFrom(Ecu1, new byte[] { 0x50, 0x03, 0x00, 0x32, 0x01, 0xF4 });
        busEcus.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID == unchecked((int)FunctionalTxId) && e.CanFrame.Data.Span[1] == 0x10)
                busEcus.Transmit(ecu1Answer);
        };

        using var functional = UdsFunctionalClient.Create(
            IsoTpFactory.OpenFunctional(busTester, FunctionalTxId, Ecu1, 0x7EF, FastOptions()), ownsClient: true);

        using var cts = new CancellationTokenSource(ShortTimeout);
        var responses = await functional.DiagnosticSessionControlAsync(UdsSessionType.Extended, Window, cts.Token);

        responses.Should().ContainSingle().Which.Response[1].Should().Be(0x03);
    }

    // #171: the listener start delay stands in for a late thread pool, and is moved by
    // advancing an injected clock. On the wall clock it would be a sleep, so it is refused.
    [Fact]
    public void A_Listener_Start_Delay_Needs_An_Injected_Clock()
    {
        using var busTester = OpenClassic(NewSession(), 0);
        using var functional = UdsFunctionalClient.Create(
            IsoTpFactory.OpenFunctional(busTester, FunctionalTxId, Ecu1, 0x7EF, FastOptions()), ownsClient: true);

        Action delay = () => functional.DelayListenerStart(TimeSpan.FromMilliseconds(1));
        delay.Should().Throw<InvalidOperationException>();
    }

    // Codex on #150: a suppressed send cancelled after the driver took the frame may still have
    // put it on the bus, so the ECUs' P2 runs from the cancellation, not from before the send.
    // The driver holds the echo while the clock moves 200 ms: the window ends at 500 ms, where
    // the provisional one from before the send ended at 300.
    [Fact]
    public async Task A_Suppressed_Send_Cancelled_After_The_Driver_Took_It_Leaves_P2_From_The_Cancellation()
    {
        using var clock = new WindowClock();
        using var bus = ControllableBus.DeferredEchoCapable(NewSession());
        var options = new IsoTpFunctionalOptions { IsExtendedCanId = false, UseCanFd = false, UsePadding = true, NAs = ShortTimeout };
        using var functional = OpenOnClock(bus, clock, responseWindow: TimeSpan.FromMilliseconds(300), options: options);

        using var cancel = new CancellationTokenSource();
        var suppressed = functional.TesterPresentAsync(cancellationToken: cancel.Token);
        await bus.DeferredEchoes.WaitForEnqueuedAsync(1, ShortTimeout);
        await clock.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(200));
        cancel.Cancel();
        Func<Task> cancelled = () => suppressed;
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        bus.EchoMode = EchoDelivery.Synchronous;

        await NextCallWaitsUntil500(clock, functional, () => bus.TransmitCount);
    }

    // A driver that reports no transmit instant: the window runs from the confirmation. It
    // takes 200 ms to come, so the window ends at 500 ms, not at the provisional 300.
    [Fact]
    public async Task A_Suppressed_Send_Without_A_Transmit_Stamp_Is_Anchored_At_Its_Confirmation()
    {
        using var clock = new WindowClock();
        var service = new StarvedReaderBusService
        {
            OnSendConfirmed = () => clock.Clock.Advance(TimeSpan.FromMilliseconds(200)),
        };
        using var functional = OpenOnClock(service, clock, responseWindow: TimeSpan.FromMilliseconds(300));

        using var cts = new CancellationTokenSource(ShortTimeout);
        await functional.TesterPresentAsync(cancellationToken: cts.Token);
        service.OnSendConfirmed = null;

        await NextCallWaitsUntil500(clock, functional, () => service.Sent.Count);
    }

    // At 400 ms a window ending at 500 still holds the next call back, its listener collecting
    // the 100 ms left. A window that ended at 300 would have let the call out, and the earliest
    // timer would be its own 30 ms collection.
    private static async Task NextCallWaitsUntil500(WindowClock clock, UdsFunctionalClient functional,
        Func<int> transmitted)
    {
        using var cts = new CancellationTokenSource(ShortTimeout);
        var next = functional.TesterPresentAsync(suppressPositiveResponse: false,
            TimeSpan.FromMilliseconds(30), cts.Token);
        await clock.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(400) - clock.Clock.Elapsed
            + TimeSpan.FromMilliseconds(1));
        await WaitUntilArmed(clock, TimeSpan.FromMilliseconds(100));
        transmitted().Should().Be(1, "the next call is still waiting out the window");

        (await RunCall(clock, next)).Should().BeEmpty();
        transmitted().Should().Be(2);
    }

    /// <summary>
    /// Forwards to a real service and reports each <see cref="ICanBusService.SendConfirmed"/> as
    /// it is entered: for a request, the point after it has subscribed and drained, where it
    /// is about to wait for the service's transmit lock.
    /// </summary>
    private sealed class SendEnteredService : ICanBusService
    {
        private readonly ICanBusService _inner;

        public SendEnteredService(ICanBusService inner) => _inner = inner;

        public Action<CanFrame>? OnSending { get; set; }

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

        public Task<TxConfirmation> SendConfirmed(CanFrame frame, TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            OnSending?.Invoke(frame);
            return _inner.SendConfirmed(frame, timeout, cancellationToken);
        }

        public void Dispose() { /* the test owns the inner service */ }
    }
}
