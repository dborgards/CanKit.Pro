using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core;
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

        using var cts = new CancellationTokenSource(ShortTimeout);
        var first = functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x90 }, TimeSpan.FromMilliseconds(300), cts.Token);
        var second = functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x91 }, TimeSpan.FromMilliseconds(300), cts.Token);
        await Task.Delay(50); // the first is in its window, the second queued behind it

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
        using var busTester = OpenClassic(session, 0);
        using var busEcus = OpenClassic(session, 1);

        var negative = SingleFrameFrom(Ecu1, new byte[] { 0x7F, 0x3E, 0x12 });
        var positive = SingleFrameFrom(Ecu1, new byte[] { 0x7E, 0x00 });
        busEcus.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != unchecked((int)FunctionalTxId)) return;
            if (e.CanFrame.Data.Span[2] == 0x80)
                _ = Task.Run(async () => { await Task.Delay(100); busEcus.Transmit(negative); });
            else
                busEcus.Transmit(positive);
        };

        using var functional = UdsFunctionalClient.Create(
            IsoTpFactory.OpenFunctional(busTester, FunctionalTxId, Ecu1, 0x7EF, FastOptions()),
            ownsClient: true, responseWindow: TimeSpan.FromMilliseconds(300));

        using var cts = new CancellationTokenSource(ShortTimeout);
        await functional.TesterPresentAsync(cancellationToken: cts.Token);
        var responses = await functional.TesterPresentAsync(suppressPositiveResponse: false, Window, cts.Token);

        responses.Should().ContainSingle().Which.IsNegative.Should().BeFalse(
            "the late negative answer belongs to the suppressed send and is not collected");
    }

    // Codex on #150: a request's collection window may end before the ECU's P2 does; its late
    // negative answer must not land in the next same-service call's window either.
    [Fact]
    public async Task A_Late_Negative_Answer_To_A_Previous_Request_Does_Not_Land_In_The_Next_Window()
    {
        var session = NewSession();
        using var busTester = OpenClassic(session, 0);
        using var busEcus = OpenClassic(session, 1);

        var negative = SingleFrameFrom(Ecu1, new byte[] { 0x7F, 0x22, 0x31 });
        var positive = SingleFrameFrom(Ecu1, new byte[] { 0x62, 0xF1, 0x91, 0x02 });
        busEcus.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != unchecked((int)FunctionalTxId)) return;
            if (e.CanFrame.Data.Span[3] == 0x90)
                _ = Task.Run(async () => { await Task.Delay(150); busEcus.Transmit(negative); });
            else
                busEcus.Transmit(positive);
        };

        using var functional = UdsFunctionalClient.Create(
            IsoTpFactory.OpenFunctional(busTester, FunctionalTxId, Ecu1, 0x7EF, FastOptions()),
            ownsClient: true, responseWindow: TimeSpan.FromMilliseconds(300));

        using var cts = new CancellationTokenSource(ShortTimeout);
        // A short collection window: the negative answer comes after it.
        await functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x90 }, TimeSpan.FromMilliseconds(30), cts.Token);
        var responses = await functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x91 }, Window, cts.Token);

        responses.Should().ContainSingle().Which.IsNegative.Should().BeFalse(
            "F190's late negative answer is the previous request's");
    }

    // Bugbot on #150: a collection that is cancelled still leaves the window in place.
    [Fact]
    public async Task A_Cancelled_Collection_Still_Leaves_Its_Window_For_The_Next_Call()
    {
        var session = NewSession();
        using var busTester = OpenClassic(session, 0);
        using var busEcus = OpenClassic(session, 1);

        var negative = SingleFrameFrom(Ecu1, new byte[] { 0x7F, 0x22, 0x31 });
        var positive = SingleFrameFrom(Ecu1, new byte[] { 0x62, 0xF1, 0x91, 0x02 });
        busEcus.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != unchecked((int)FunctionalTxId)) return;
            if (e.CanFrame.Data.Span[3] == 0x90)
                _ = Task.Run(async () => { await Task.Delay(150); busEcus.Transmit(negative); });
            else
                busEcus.Transmit(positive);
        };

        // A cancelled collection takes the conservative reading (P2* from the cancellation);
        // kept short here so the test measures the window, not the default P2*.
        using var functional = UdsFunctionalClient.Create(
            IsoTpFactory.OpenFunctional(busTester, FunctionalTxId, Ecu1, 0x7EF, FastOptions()),
            ownsClient: true, responseWindow: TimeSpan.FromMilliseconds(300), responsePendingWindow: TimeSpan.FromMilliseconds(300));

        using var early = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        Func<Task> cancelled = () => functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x90 }, Window, early.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();

        using var cts = new CancellationTokenSource(ShortTimeout);
        var responses = await functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x91 }, Window, cts.Token);
        responses.Should().ContainSingle().Which.IsNegative.Should().BeFalse(
            "the cancelled request's late negative answer is not the next call's");
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

    // Codex on #150: the window is the ECU's P2 from the *transmission*. With the transmit
    // confirmation delayed, a window anchored before the send ends too early; after the
    // collection it is moved out to the send's instant, which the collection's length gives.
    [Fact]
    public async Task A_Window_Is_Anchored_At_The_Transmission_However_Late_It_Was_Confirmed()
    {
        using var bus = ControllableBus.DeferredEchoCapable(NewSession());
        using var service = new CanBusService(bus);
        using var functional = UdsFunctionalClient.Create(
            IsoTpFactory.OpenFunctional(service, FunctionalTxId, Ecu1, 0x7EF, FastOptions()),
            ownsClient: true, responseWindow: TimeSpan.FromMilliseconds(300));

        var negative = SingleFrameFrom(Ecu1, new byte[] { 0x7F, 0x22, 0x31 });
        var positive = SingleFrameFrom(Ecu1, new byte[] { 0x62, 0xF1, 0x91, 0x02 });

        using var cts = new CancellationTokenSource(ShortTimeout);
        // First request: its echo -- the transmit confirmation -- is held for 200 ms; the ECU
        // answers negatively 450 ms after the frame went out, inside its P2 of 300 ms from the
        // confirmation's point of view as the client sees it... and before that from the wire's.
        var first = functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x90 }, TimeSpan.FromMilliseconds(50), cts.Token);
        await bus.DeferredEchoes.WaitForEnqueuedAsync(1, ShortTimeout);
        _ = Task.Run(async () => { await Task.Delay(450); bus.RaiseObserved(negative, isEcho: false); });
        await Task.Delay(200);
        bus.DeferredEchoes.ReleaseNext();
        await first;

        // The second request, at once: its own echo is released promptly, and the ECU answers it.
        var second = functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x91 }, Window, cts.Token);
        await bus.DeferredEchoes.WaitForEnqueuedAsync(2, ShortTimeout); // the count never decreases
        bus.DeferredEchoes.ReleaseNext();
        _ = Task.Run(async () => { await Task.Delay(20); bus.RaiseObserved(positive, isEcho: false); });
        var responses = await second;

        responses.Should().ContainSingle().Which.IsNegative.Should().BeFalse(
            "the negative answer came 450 ms after the first request's transmission, inside its window");
    }

    // Codex on #150: P2* runs from the 0x78's arrival, not from the end of the collection.
    // With the 0x78 at the start of a 300 ms window and P2* = 400 ms, the next call may go
    // out at 400 ms after the request, not at 700.
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
            ownsClient: true, responseWindow: TimeSpan.FromMilliseconds(100), responsePendingWindow: TimeSpan.FromMilliseconds(400));

        using var cts = new CancellationTokenSource(ShortTimeout);
        var sw = Stopwatch.StartNew();
        await functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x90 }, TimeSpan.FromMilliseconds(300), cts.Token);
        var responses = await functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x91 }, TimeSpan.FromMilliseconds(50), cts.Token);
        sw.Stop();

        responses.Should().ContainSingle();
        // From the 0x78 (at ~0 ms) plus 400 ms the second call goes out and collects for 50 ms:
        // ~450 ms in all. From the collection's end (300 ms) plus 400 ms it could not finish
        // before 750 ms. The bound sits between the two.
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(620),
            "P2* is counted from the 0x78's arrival");
    }

    // Codex on #150: a cancelled collection cannot show what arrived; the window takes the
    // conservative reading and the next call waits P2* from the cancellation.
    [Fact]
    public async Task A_Cancelled_Collection_Leaves_A_Conservative_Window()
    {
        var session = NewSession();
        using var busTester = OpenClassic(session, 0);
        using var busEcus = OpenClassic(session, 1);

        var pending = SingleFrameFrom(Ecu1, new byte[] { 0x7F, 0x22, 0x78 });
        var negative = SingleFrameFrom(Ecu1, new byte[] { 0x7F, 0x22, 0x31 });
        var positive = SingleFrameFrom(Ecu1, new byte[] { 0x62, 0xF1, 0x91, 0x02 });
        busEcus.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != unchecked((int)FunctionalTxId)) return;
            if (e.CanFrame.Data.Span[3] == 0x90)
            {
                busEcus.Transmit(pending);
                _ = Task.Run(async () => { await Task.Delay(250); busEcus.Transmit(negative); });
            }
            else busEcus.Transmit(positive);
        };

        using var functional = UdsFunctionalClient.Create(
            IsoTpFactory.OpenFunctional(busTester, FunctionalTxId, Ecu1, 0x7EF, FastOptions()),
            ownsClient: true, responseWindow: TimeSpan.FromMilliseconds(100), responsePendingWindow: TimeSpan.FromMilliseconds(400));

        using var early = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        Func<Task> cancelled = () => functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x90 }, Window, early.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();

        using var cts = new CancellationTokenSource(ShortTimeout);
        var responses = await functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x91 }, Window, cts.Token);
        responses.Should().ContainSingle().Which.IsNegative.Should().BeFalse(
            "the final negative answer at 250 ms belongs to the cancelled request, whose 0x78 the cancellation hid");
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
}
