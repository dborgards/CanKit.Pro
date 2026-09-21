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

    private static TimeSpan Between(long earlier, long later)
        => TimeSpan.FromSeconds((later - earlier) / (double)Stopwatch.Frequency);

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

        using var functional = UdsFunctionalClient.Create(
            IsoTpFactory.OpenFunctional(busTester, FunctionalTxId, Ecu1, 0x7EF, FastOptions()),
            ownsClient: true, responseWindow: TimeSpan.FromMilliseconds(300));

        var sentAt = new List<long>();
        busEcus.FrameObserved += (_, e) => { if (e.CanFrame.ID == unchecked((int)FunctionalTxId)) lock (sentAt) sentAt.Add(Stopwatch.GetTimestamp()); };

        using var early = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        Func<Task> cancelled = () => functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x90 }, Window, early.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();

        using var cts = new CancellationTokenSource(ShortTimeout);
        var responses = await functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x91 }, Window, cts.Token);
        responses.Should().ContainSingle().Which.IsNegative.Should().BeFalse(
            "the cancelled request's late negative answer is not the next call's");
        // And the reason it is not: the second request waited the window out. A lower bound,
        // which a loaded host only raises.
        long gap;
        lock (sentAt) gap = sentAt[1] - sentAt[0];
        Between(0, gap).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(250),
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

        var positive = SingleFrameFrom(Ecu1, new byte[] { 0x62, 0xF1, 0x91, 0x02 });
        var sentAt = new List<long>();
        bus.OnTransmitting = frame => { if (frame.ID == unchecked((int)FunctionalTxId)) lock (sentAt) sentAt.Add(Stopwatch.GetTimestamp()); };

        using var cts = new CancellationTokenSource(ShortTimeout);
        // First request: its echo -- the transmit confirmation -- is held for 200 ms. The window
        // is P2 = 300 ms from the transmission as the confirmation places it, so the next call
        // for the service may not go out before 300 ms after that confirmation; anchored before
        // the send instead, the window would end 200 ms sooner. Asserted as a lower bound on
        // when the second request went out -- a loaded host only makes it later.
        var first = functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x90 }, TimeSpan.FromMilliseconds(50), cts.Token);
        await bus.DeferredEchoes.WaitForEnqueuedAsync(1, ShortTimeout);
        await Task.Delay(200);
        var confirmedAt = Stopwatch.GetTimestamp();
        bus.DeferredEchoes.ReleaseNext();
        await first;

        // The second collects for the whole P2: its answer is timed, and a host that delays the
        // timer past a short window would leave the collection empty (macOS CI on #150).
        var second = functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x91 }, Window, cts.Token);
        await bus.DeferredEchoes.WaitForEnqueuedAsync(2, ShortTimeout); // the count never decreases
        bus.DeferredEchoes.ReleaseNext();
        _ = Task.Run(async () => { await Task.Delay(20); bus.RaiseObserved(positive, isEcho: false); });
        (await second).Should().ContainSingle();

        long secondSentAt;
        lock (sentAt) secondSentAt = sentAt[1];
        Between(confirmedAt, secondSentAt).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(250),
            "the window is anchored at the transmission, 300 ms of P2 from the confirmation");
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

    // Codex on #150: a collection window longer than P2 may hold a 0x78 from after the
    // request's P2; it answers nothing the window still covers and does not revive it.
    [Fact]
    public async Task A_Pending_Answer_Collected_After_P2_Does_Not_Revive_The_Window()
    {
        var session = NewSession();
        using var busTester = OpenClassic(session, 0);
        using var busEcus = OpenClassic(session, 1);

        var pending = SingleFrameFrom(Ecu1, new byte[] { 0x7F, 0x22, 0x78 });
        var positive = SingleFrameFrom(Ecu1, new byte[] { 0x62, 0xF1, 0x91, 0x02 });
        busEcus.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != unchecked((int)FunctionalTxId)) return;
            if (e.CanFrame.Data.Span[3] == 0x90)
                _ = Task.Run(async () => { await Task.Delay(250); busEcus.Transmit(pending); });
            else busEcus.Transmit(positive);
        };

        using var functional = UdsFunctionalClient.Create(
            IsoTpFactory.OpenFunctional(busTester, FunctionalTxId, Ecu1, 0x7EF, FastOptions()),
            ownsClient: true, responseWindow: TimeSpan.FromMilliseconds(100), responsePendingWindow: TimeSpan.FromMilliseconds(2000));

        using var cts = new CancellationTokenSource(ShortTimeout);
        // P2 = 100 ms; the 0x78 at 250 ms is inside the 400 ms collection but after P2.
        await functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x90 }, TimeSpan.FromMilliseconds(400), cts.Token);

        // Revived, the window would reach 2250 ms and this call would wait most of two
        // seconds; a loaded host only makes the call slower, so the bound is wide.
        var sw = Stopwatch.StartNew();
        var responses = await functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x91 }, TimeSpan.FromMilliseconds(50), cts.Token);
        sw.Stop();
        responses.Should().ContainSingle();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "the 0x78 from after P2 did not revive the window");
    }

    // Codex on #150: the listener's subscription is made before the send, but its worker may
    // start after the window has run out; what the subscription buffered meanwhile -- here
    // the immediate 0x78 -- is read before the worker retires, not abandoned with it.
    [Fact]
    public async Task A_Listener_Starting_After_The_Window_Still_Hears_What_It_Buffered()
    {
        var session = NewSession();
        using var busTester = OpenClassic(session, 0);
        using var busEcus = OpenClassic(session, 1);

        var pending = SingleFrameFrom(Ecu1, new byte[] { 0x7F, 0x10, 0x78 });
        var negative = SingleFrameFrom(Ecu1, new byte[] { 0x7F, 0x10, 0x12 });
        var positive = SingleFrameFrom(Ecu1, new byte[] { 0x50, 0x03, 0x00, 0x32, 0x01, 0xF4 });
        busEcus.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != unchecked((int)FunctionalTxId)) return;
            if ((e.CanFrame.Data.Span[2] & 0x80) != 0)
            {
                busEcus.Transmit(pending);
                _ = Task.Run(async () => { await Task.Delay(250); busEcus.Transmit(negative); });
            }
            else busEcus.Transmit(positive);
        };

        using var functional = UdsFunctionalClient.Create(
            IsoTpFactory.OpenFunctional(busTester, FunctionalTxId, Ecu1, 0x7EF, FastOptions()),
            ownsClient: true, responseWindow: TimeSpan.FromMilliseconds(100), responsePendingWindow: TimeSpan.FromMilliseconds(400));
        functional.ListenerStartDelay = TimeSpan.FromMilliseconds(150); // past the 100 ms window

        using var cts = new CancellationTokenSource(ShortTimeout);
        await functional.SendRawAsync(new byte[] { 0x10, 0x83 }, Window, cts.Token); // suppressed: the 0x78 is buffered before the worker runs

        var responses = await functional.DiagnosticSessionControlAsync(UdsSessionType.Extended, Window, cts.Token);
        responses.Should().ContainSingle().Which.IsNegative.Should().BeFalse(
            "the late worker read the buffered 0x78 and kept the window to 400 ms, past the negative at 250");
    }

    // Codex on #150: a cancelled collection loses what it collected, but the listener that
    // owns the window heard the 0x78 too, and the next call waits P2* from it.
    [Fact]
    public async Task A_Cancelled_Collection_Does_Not_Lose_The_Pending_Answer_The_Listener_Heard()
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

    // Bugbot on #150: a wait cancelled by the caller does not cancel the listener, which keeps
    // the window and hears the 0x78 the caller left behind.
    [Fact]
    public async Task A_Cancelled_Wait_Leaves_The_Listener_To_Hear_The_Pending_Answer()
    {
        var session = NewSession();
        using var busTester = OpenClassic(session, 0);
        using var busEcus = OpenClassic(session, 1);

        var pending = SingleFrameFrom(Ecu1, new byte[] { 0x7F, 0x3E, 0x78 });
        var negative = SingleFrameFrom(Ecu1, new byte[] { 0x7F, 0x3E, 0x12 });
        var positive = SingleFrameFrom(Ecu1, new byte[] { 0x7E, 0x00 });
        busEcus.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != unchecked((int)FunctionalTxId)) return;
            if (e.CanFrame.Data.Span[2] == 0x80)
                _ = Task.Run(async () =>
                {
                    await Task.Delay(50); busEcus.Transmit(pending);
                    await Task.Delay(200); busEcus.Transmit(negative);
                });
            else busEcus.Transmit(positive);
        };

        using var functional = UdsFunctionalClient.Create(
            IsoTpFactory.OpenFunctional(busTester, FunctionalTxId, Ecu1, 0x7EF, FastOptions()),
            ownsClient: true, responseWindow: TimeSpan.FromMilliseconds(300), responsePendingWindow: TimeSpan.FromMilliseconds(600));

        using var cts = new CancellationTokenSource(ShortTimeout);
        await functional.TesterPresentAsync(cancellationToken: cts.Token); // suppressed; 0x78 at 50 ms, negative at 250 ms, window to 650 ms

        using var early = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        Func<Task> cancelled = () => functional.TesterPresentAsync(suppressPositiveResponse: false, Window, early.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>(); // cancelled while waiting; the 0x78 it heard is lost

        var responses = await functional.TesterPresentAsync(suppressPositiveResponse: false, Window, cts.Token);
        responses.Should().ContainSingle().Which.IsNegative.Should().BeFalse(
            "the negative at 250 ms belongs to the suppressed send; the window reaches to 650 ms");
    }

    // Codex on #150: between a suppressed send and the next call nobody was collecting, so a
    // 0x78 in that gap went unobserved. The listener is up before the send and stays for the
    // window, so the gap is observed and the next call waits P2* from the 0x78.
    [Fact]
    public async Task A_Pending_Answer_In_The_Gap_After_A_Suppressed_Send_Is_Observed()
    {
        var session = NewSession();
        using var busTester = OpenClassic(session, 0);
        using var busEcus = OpenClassic(session, 1);

        var pending = SingleFrameFrom(Ecu1, new byte[] { 0x7F, 0x3E, 0x78 });
        var negative = SingleFrameFrom(Ecu1, new byte[] { 0x7F, 0x3E, 0x12 });
        var positive = SingleFrameFrom(Ecu1, new byte[] { 0x7E, 0x00 });
        busEcus.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != unchecked((int)FunctionalTxId)) return;
            if (e.CanFrame.Data.Span[2] == 0x80)
                _ = Task.Run(async () =>
                {
                    await Task.Delay(100); busEcus.Transmit(pending);
                    await Task.Delay(400); busEcus.Transmit(negative);
                });
            else busEcus.Transmit(positive);
        };

        using var functional = UdsFunctionalClient.Create(
            IsoTpFactory.OpenFunctional(busTester, FunctionalTxId, Ecu1, 0x7EF, FastOptions()),
            ownsClient: true, responseWindow: TimeSpan.FromMilliseconds(300), responsePendingWindow: TimeSpan.FromMilliseconds(600));

        using var cts = new CancellationTokenSource(ShortTimeout);
        await functional.TesterPresentAsync(cancellationToken: cts.Token); // suppressed; 0x78 at 100 ms, negative at 500 ms
        await Task.Delay(200);                                              // nobody collecting: the gap

        var responses = await functional.TesterPresentAsync(suppressPositiveResponse: false, Window, cts.Token);
        responses.Should().ContainSingle().Which.IsNegative.Should().BeFalse(
            "the 0x78 in the gap moved the window past the negative at 500 ms");
    }

    // Codex on #150: a window the collector would reject is checked before anything goes out.
    [Fact]
    public async Task An_Invalid_Collection_Window_Transmits_Nothing()
    {
        var session = NewSession();
        using var busTester = OpenClassic(session, 0);
        using var busEcus = OpenClassic(session, 1);
        int seen = 0;
        busEcus.FrameObserved += (_, e) => { if (e.CanFrame.ID == unchecked((int)FunctionalTxId)) Interlocked.Increment(ref seen); };

        using var functional = UdsFunctionalClient.Create(
            IsoTpFactory.OpenFunctional(busTester, FunctionalTxId, Ecu1, 0x7EF, FastOptions()), ownsClient: true);

        Func<Task> act = () => functional.DiagnosticSessionControlAsync(UdsSessionType.Extended, TimeSpan.FromMilliseconds(-2));
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
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

    // Codex on #150: a transmit confirmation that outlasts the window lets the pre-send
    // listener retire; anchoring the window afterwards must start one again, or the next call
    // has nothing to wait for.
    [Fact]
    public async Task A_Listener_Is_Restarted_When_The_Confirmation_Outlasted_The_Window()
    {
        using var bus = ControllableBus.DeferredEchoCapable(NewSession());
        using var service = new CanBusService(bus);
        // N_As long enough to hold the confirmation past the window without timing the send out.
        var options = new IsoTpFunctionalOptions { IsExtendedCanId = false, UseCanFd = false, UsePadding = true, NAs = TimeSpan.FromSeconds(2) };
        using var functional = UdsFunctionalClient.Create(
            IsoTpFactory.OpenFunctional(service, FunctionalTxId, Ecu1, 0x7EF, options),
            ownsClient: true, responseWindow: TimeSpan.FromMilliseconds(1000));

        var negative = SingleFrameFrom(Ecu1, new byte[] { 0x7F, 0x22, 0x31 });
        var positive = SingleFrameFrom(Ecu1, new byte[] { 0x62, 0xF1, 0x91, 0x02 });

        using var cts = new CancellationTokenSource(ShortTimeout);
        // The first request's confirmation is held for 1100 ms -- longer than the 1000 ms
        // window the pre-send listener was given; the ECU answers negatively 150 ms after the
        // frame is confirmed, inside the window as anchored at the transmission, with 850 ms
        // to spare for the host to delay that timer.
        var first = functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x90 }, TimeSpan.FromMilliseconds(30), cts.Token);
        await bus.DeferredEchoes.WaitForEnqueuedAsync(1, ShortTimeout);
        await Task.Delay(1100);
        bus.DeferredEchoes.ReleaseNext();
        _ = Task.Run(async () => { await Task.Delay(150); bus.RaiseObserved(negative, isEcho: false); });
        await first;

        var second = functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x91 }, Window, cts.Token);
        await bus.DeferredEchoes.WaitForEnqueuedAsync(2, ShortTimeout);
        bus.DeferredEchoes.ReleaseNext();
        _ = Task.Run(async () => { await Task.Delay(20); bus.RaiseObserved(positive, isEcho: false); });
        var responses = await second;

        responses.Should().ContainSingle().Which.IsNegative.Should().BeFalse(
            "the second request waited out the window anchored at the first's late confirmation");
    }

    // Codex on #150: with the confirmation held past the window, the pre-send listener has
    // retired by the time the collection runs; a collection the caller then cancels leaves by
    // exception, and the window must still be anchored for the request that went out.
    [Fact]
    public async Task A_Cancelled_Collection_After_A_Late_Confirmation_Still_Anchors_Its_Window()
    {
        using var bus = ControllableBus.DeferredEchoCapable(NewSession());
        using var service = new CanBusService(bus);
        var options = new IsoTpFunctionalOptions { IsExtendedCanId = false, UseCanFd = false, UsePadding = true, NAs = TimeSpan.FromSeconds(2) };
        using var functional = UdsFunctionalClient.Create(
            IsoTpFactory.OpenFunctional(service, FunctionalTxId, Ecu1, 0x7EF, options),
            ownsClient: true, responseWindow: TimeSpan.FromMilliseconds(1000));

        var negative = SingleFrameFrom(Ecu1, new byte[] { 0x7F, 0x22, 0x31 });
        var positive = SingleFrameFrom(Ecu1, new byte[] { 0x62, 0xF1, 0x91, 0x02 });

        // The confirmation is held for 1100 ms, past the 1000 ms window of the pre-send
        // listener; the collection is then cancelled 50 ms in, and the ECU answers negatively
        // after that, 150 ms after the confirmation -- inside its P2 from the transmission.
        // The quantity the host perturbs is the negative answer's timer, which must fire
        // before the second request goes out at 1000 ms after the cancellation: a margin of
        // 900 ms (Windows CI on #150 exceeded 300).
        using var early = new CancellationTokenSource();
        var first = functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x90 }, Window, early.Token);
        await bus.DeferredEchoes.WaitForEnqueuedAsync(1, ShortTimeout);
        await Task.Delay(1100);
        bus.DeferredEchoes.ReleaseNext();
        await Task.Delay(50);
        early.Cancel();
        Func<Task> cancelled = () => first;
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        _ = Task.Run(async () => { await Task.Delay(100); bus.RaiseObserved(negative, isEcho: false); });

        using var cts = new CancellationTokenSource(ShortTimeout);
        var second = functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x91 }, Window, cts.Token);
        await bus.DeferredEchoes.WaitForEnqueuedAsync(2, ShortTimeout);
        bus.DeferredEchoes.ReleaseNext();
        _ = Task.Run(async () => { await Task.Delay(20); bus.RaiseObserved(positive, isEcho: false); });
        var responses = await second;

        responses.Should().ContainSingle().Which.IsNegative.Should().BeFalse(
            "the cancelled request's late negative answer fell in the window anchored when its collection was cancelled");
    }

    // Codex on #150: a suppressed send whose confirmation outlasts the window must not be
    // left without a listener between the transmission and the send's return -- a 0x78
    // answered at the transmission would be lost, and with it the P2* that covers the
    // request's final answer.
    [Fact]
    public async Task A_Listener_Is_Kept_Through_A_Send_Whose_Confirmation_Outlasts_The_Window()
    {
        using var bus = ControllableBus.DeferredEchoCapable(NewSession());
        using var service = new CanBusService(bus);
        var options = new IsoTpFunctionalOptions { IsExtendedCanId = false, UseCanFd = false, UsePadding = true, NAs = TimeSpan.FromSeconds(2) };
        using var functional = UdsFunctionalClient.Create(
            IsoTpFactory.OpenFunctional(service, FunctionalTxId, Ecu1, 0x7EF, options),
            ownsClient: true, responseWindow: TimeSpan.FromMilliseconds(400), responsePendingWindow: TimeSpan.FromMilliseconds(2000));

        var pending = SingleFrameFrom(Ecu1, new byte[] { 0x7F, 0x10, 0x78 });
        var negative = SingleFrameFrom(Ecu1, new byte[] { 0x7F, 0x10, 0x12 });
        var positive = SingleFrameFrom(Ecu1, new byte[] { 0x50, 0x03, 0x00, 0x32, 0x01, 0xF4 });

        using var cts = new CancellationTokenSource(ShortTimeout);
        // The confirmation is held for 600 ms, past the 400 ms provisional window; the frame is
        // on the bus meanwhile, and the ECU's 0x78 arrives at 500 ms, 100 ms before the
        // confirmation is released -- heard by the listener while the send is still in
        // flight. The negative answer follows 600 ms after the confirmation: past the P2
        // window anchored there (1000 ms), inside P2* = 2000 ms from the 0x78 (2500 ms), with
        // 1300 ms to spare for the host to delay its timer.
        var suppressed = functional.SendRawAsync(new byte[] { 0x10, 0x83 }, Window, cts.Token);
        await bus.DeferredEchoes.WaitForEnqueuedAsync(1, ShortTimeout);
        await Task.Delay(500);
        bus.RaiseObserved(pending, isEcho: false);
        await Task.Delay(100);
        bus.DeferredEchoes.ReleaseNext();
        await suppressed;
        _ = Task.Run(async () => { await Task.Delay(600); bus.RaiseObserved(negative, isEcho: false); });

        var second = functional.DiagnosticSessionControlAsync(UdsSessionType.Extended, Window, cts.Token);
        await bus.DeferredEchoes.WaitForEnqueuedAsync(2, ShortTimeout);
        bus.DeferredEchoes.ReleaseNext();
        _ = Task.Run(async () => { await Task.Delay(20); bus.RaiseObserved(positive, isEcho: false); });
        var responses = await second;

        responses.Should().ContainSingle().Which.IsNegative.Should().BeFalse(
            "the listener kept through the send heard the 0x78 and held the window past the negative answer");
    }

    // Bugbot on #150: a collection that outlasts P2 anchors a window already over; that must
    // not leave a completed listener behind that blocks the next window's real one.
    [Fact]
    public async Task A_Collection_That_Outlasts_The_Window_Does_Not_Leave_A_Zombie_Listener()
    {
        var session = NewSession();
        using var busTester = OpenClassic(session, 0);
        using var busEcus = OpenClassic(session, 1);

        var negative = SingleFrameFrom(Ecu1, new byte[] { 0x7F, 0x22, 0x31 });
        var positive = SingleFrameFrom(Ecu1, new byte[] { 0x62, 0xF1, 0x92, 0x03 });
        int seen = 0;
        busEcus.FrameObserved += (_, e) =>
        {
            if (e.CanFrame.ID != unchecked((int)FunctionalTxId)) return;
            int n = Interlocked.Increment(ref seen);
            if (n == 2) _ = Task.Run(async () => { await Task.Delay(150); busEcus.Transmit(negative); });
            if (n == 3) busEcus.Transmit(positive);
        };

        using var functional = UdsFunctionalClient.Create(
            IsoTpFactory.OpenFunctional(busTester, FunctionalTxId, Ecu1, 0x7EF, FastOptions()),
            ownsClient: true, responseWindow: TimeSpan.FromMilliseconds(400));

        using var cts = new CancellationTokenSource(ShortTimeout);
        // A collection longer than the window: the pre-send listener retires; the anchor after
        // it is already over and must start nothing.
        await functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x90 }, TimeSpan.FromMilliseconds(500), cts.Token);
        // A short collection; its late negative, at 150 ms, needs a living listener's window.
        await functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x91 }, TimeSpan.FromMilliseconds(30), cts.Token);
        var responses = await functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x92 }, Window, cts.Token);

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
}
