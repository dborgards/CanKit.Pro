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

    // Codex on #150: the window is the ECU's P2 from the *transmission* -- the instant the
    // driver accepted the frame, as the physical client counts it -- not from before the
    // send. With the driver holding the frame 200 ms before accepting it, the next call for
    // the service goes out P2 = 1000 ms after the acceptance, not 800.
    [Fact]
    public async Task A_Window_Is_Anchored_At_The_Drivers_Acceptance_Not_Before_The_Send()
    {
        using var bus = ControllableBus.DeferredEchoCapable(NewSession());
        using var service = new CanBusService(bus);
        using var functional = UdsFunctionalClient.Create(
            IsoTpFactory.OpenFunctional(service, FunctionalTxId, Ecu1, 0x7EF, FastOptions()),
            ownsClient: true, responseWindow: TimeSpan.FromMilliseconds(1000));

        var positive = SingleFrameFrom(Ecu1, new byte[] { 0x62, 0xF1, 0x91, 0x02 });
        var acceptedAt = new List<long>();
        using var hold = new ManualResetEventSlim();
        bus.OnTransmitting = frame =>
        {
            if (frame.ID != unchecked((int)FunctionalTxId)) return;
            lock (acceptedAt)
            {
                if (acceptedAt.Count == 0) hold.Wait(); // the first frame: the driver takes 200 ms to accept it
                acceptedAt.Add(Stopwatch.GetTimestamp());
            }
        };

        using var cts = new CancellationTokenSource(ShortTimeout);
        // On the pool: the driver's hold is inside the service's send lock, a synchronous wait.
        var first = Task.Run(() => functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x90 }, TimeSpan.FromMilliseconds(50), cts.Token));
        await Task.Delay(200);
        hold.Set();
        await bus.DeferredEchoes.WaitForEnqueuedAsync(1, ShortTimeout);
        bus.DeferredEchoes.ReleaseNext();
        await first;

        var second = functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x91 }, Window, cts.Token);
        await bus.DeferredEchoes.WaitForEnqueuedAsync(2, ShortTimeout); // the count never decreases
        bus.DeferredEchoes.ReleaseNext();
        _ = Task.Run(async () => { await Task.Delay(20); bus.RaiseObserved(positive, isEcho: false); });
        (await second).Should().ContainSingle();

        long gap;
        lock (acceptedAt) gap = acceptedAt[1] - acceptedAt[0];
        // A lower bound a loaded host only raises; noted before the send only, the window
        // would have ended 200 ms sooner.
        Between(0, gap).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(950),
            "the window is P2 from the driver's acceptance, not from before the send");
    }

    // Codex on #150: and not from the confirmation either. With the confirmation held 2 s past
    // the acceptance, the window -- P2 = 1000 ms from the acceptance -- is over by the time
    // the first call returns, and the next call goes out at once rather than 950 ms later.
    [Fact]
    public async Task A_Window_Is_Anchored_At_The_Drivers_Acceptance_Not_At_The_Confirmation()
    {
        using var bus = ControllableBus.DeferredEchoCapable(NewSession());
        using var service = new CanBusService(bus);
        var options = new IsoTpFunctionalOptions { IsExtendedCanId = false, UseCanFd = false, UsePadding = true, NAs = TimeSpan.FromSeconds(5) };
        using var functional = UdsFunctionalClient.Create(
            IsoTpFactory.OpenFunctional(service, FunctionalTxId, Ecu1, 0x7EF, options),
            ownsClient: true, responseWindow: TimeSpan.FromMilliseconds(1000));

        var positive = SingleFrameFrom(Ecu1, new byte[] { 0x62, 0xF1, 0x91, 0x02 });
        var sentAt = new List<long>();
        bus.OnTransmitting = frame => { if (frame.ID == unchecked((int)FunctionalTxId)) lock (sentAt) sentAt.Add(Stopwatch.GetTimestamp()); };

        using var cts = new CancellationTokenSource(ShortTimeout);
        var first = functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x90 }, TimeSpan.FromMilliseconds(50), cts.Token);
        await bus.DeferredEchoes.WaitForEnqueuedAsync(1, ShortTimeout);
        await Task.Delay(2000); // the confirmation held, the window long over
        bus.DeferredEchoes.ReleaseNext();
        await first;

        var startedAt = Stopwatch.GetTimestamp();
        var second = functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x91 }, Window, cts.Token);
        await bus.DeferredEchoes.WaitForEnqueuedAsync(2, ShortTimeout);
        bus.DeferredEchoes.ReleaseNext();
        _ = Task.Run(async () => { await Task.Delay(20); bus.RaiseObserved(positive, isEcho: false); });
        (await second).Should().ContainSingle();

        long secondSentAt;
        lock (sentAt) secondSentAt = sentAt[1];
        // Anchored at the confirmation, the second call would wait 950 ms before sending; the
        // bound sits 450 ms from either reading.
        Between(startedAt, secondSentAt).Should().BeLessThan(TimeSpan.FromMilliseconds(500),
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
            ownsClient: true, responseWindow: TimeSpan.FromMilliseconds(100), responsePendingWindow: TimeSpan.FromMilliseconds(1500));
        functional.ListenerStartDelay = TimeSpan.FromMilliseconds(150); // past the 100 ms window

        using var cts = new CancellationTokenSource(ShortTimeout);
        await functional.SendRawAsync(new byte[] { 0x10, 0x83 }, Window, cts.Token); // suppressed: the 0x78 is buffered before the worker runs

        var responses = await functional.DiagnosticSessionControlAsync(UdsSessionType.Extended, Window, cts.Token);
        // P2* = 1500 ms: the negative answer's timer must fire before the second request goes
        // out, with 1250 ms to spare (macOS CI on #150 exceeded 150). The same P2* in the
        // three tests below, for the same reason.
        responses.Should().ContainSingle().Which.IsNegative.Should().BeFalse(
            "the late worker read the buffered 0x78 and kept the window to 1500 ms, past the negative at 250");
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
            ownsClient: true, responseWindow: TimeSpan.FromMilliseconds(100), responsePendingWindow: TimeSpan.FromMilliseconds(1500));

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
            ownsClient: true, responseWindow: TimeSpan.FromMilliseconds(300), responsePendingWindow: TimeSpan.FromMilliseconds(1500));

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
            ownsClient: true, responseWindow: TimeSpan.FromMilliseconds(300), responsePendingWindow: TimeSpan.FromMilliseconds(1500));

        using var cts = new CancellationTokenSource(ShortTimeout);
        await functional.TesterPresentAsync(cancellationToken: cts.Token); // suppressed; 0x78 at 100 ms, negative at 500 ms
        await Task.Delay(200);                                              // nobody collecting: the gap

        var responses = await functional.TesterPresentAsync(suppressPositiveResponse: false, Window, cts.Token);
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
    [InlineData(100, 1000)]
    [InlineData(1000, 2000)]
    public async Task A_Pending_Answer_From_Before_The_Handoff_Is_Not_This_Requests(int windowMs, int boundMs)
    {
        using var bus = ControllableBus.DeferredEchoCapable(NewSession());
        using var service = new CanBusService(bus);
        using var functional = UdsFunctionalClient.Create(
            IsoTpFactory.OpenFunctional(service, FunctionalTxId, Ecu1, 0x7EF, FastOptions()),
            ownsClient: true, responseWindow: TimeSpan.FromMilliseconds(windowMs), responsePendingWindow: TimeSpan.FromMilliseconds(3000));

        var pending = SingleFrameFrom(Ecu1, new byte[] { 0x7F, 0x22, 0x78 });
        var positive = SingleFrameFrom(Ecu1, new byte[] { 0x62, 0xF1, 0x90, 0x02 });

        // Another sender on the same service, held inside the driver's Transmit -- and so
        // inside the service's transmit lock -- until released.
        using var holding = new ManualResetEventSlim();
        var inside = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bus.OnTransmitting = f => { if (f.ID == 0x123) { inside.TrySetResult(true); holding.Wait(); } };
        var other = Task.Run(() => service.SendConfirmed(CanFrame.Classic(0x123, new byte[8]), TimeSpan.FromSeconds(5)));
        await inside.Task.WaitAsync(ShortTimeout);

        using var cts = new CancellationTokenSource(ShortTimeout);
        // The request: subscribed and drained, then waiting for the lock -- on the pool, since
        // the wait is synchronous and would hold this thread. (A host that delays it past the
        // 0x78 lets the drain take the frame: a pass for the wrong reason, never a failure.)
        var first = Task.Run(() => functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x90 }, Window, cts.Token));
        await Task.Delay(50);
        bus.RaiseObserved(pending, isEcho: false); // before the handoff: not this request's
        holding.Set();
        await bus.DeferredEchoes.WaitForEnqueuedAsync(1, ShortTimeout);
        bus.DeferredEchoes.ReleaseNext(); // the other sender's confirmation
        await other;
        await bus.DeferredEchoes.WaitForEnqueuedAsync(2, ShortTimeout);
        bus.DeferredEchoes.ReleaseNext(); // the request's
        _ = Task.Run(async () => { await Task.Delay(20); bus.RaiseObserved(positive, isEcho: false); });
        var responses = await first;
        responses.Should().ContainSingle().Which.IsNegative.Should().BeFalse("the 0x78 from before the handoff is not this request's");

        // Nor did it move the window out: a next call goes out after P2, not after P2* = 3 s.
        var sw = Stopwatch.StartNew();
        var second = functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x91 }, TimeSpan.FromMilliseconds(50), cts.Token);
        await bus.DeferredEchoes.WaitForEnqueuedAsync(3, ShortTimeout);
        bus.DeferredEchoes.ReleaseNext();
        await second;
        sw.Stop();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(boundMs), "the 0x78 from before the handoff did not move the window out");
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
    // out lets the pre-send listener retire; anchoring the window at the acceptance must start
    // one again, or the next call has nothing to wait for.
    [Fact]
    public async Task A_Listener_Is_Restarted_When_The_Acceptance_Outlasted_The_Window()
    {
        using var bus = ControllableBus.DeferredEchoCapable(NewSession());
        using var service = new CanBusService(bus);
        var options = new IsoTpFunctionalOptions { IsExtendedCanId = false, UseCanFd = false, UsePadding = true, NAs = TimeSpan.FromSeconds(3) };
        using var functional = UdsFunctionalClient.Create(
            IsoTpFactory.OpenFunctional(service, FunctionalTxId, Ecu1, 0x7EF, options),
            ownsClient: true, responseWindow: TimeSpan.FromMilliseconds(1000));

        var negative = SingleFrameFrom(Ecu1, new byte[] { 0x7F, 0x22, 0x31 });
        var positive = SingleFrameFrom(Ecu1, new byte[] { 0x62, 0xF1, 0x91, 0x02 });
        int accepted = 0;
        using var hold = new ManualResetEventSlim();
        bus.OnTransmitting = frame =>
        {
            if (frame.ID != unchecked((int)FunctionalTxId)) return;
            if (Interlocked.Increment(ref accepted) == 1) hold.Wait(); // the first frame: accepted 1100 ms after it was handed over
        };

        using var cts = new CancellationTokenSource(ShortTimeout);
        // The first request's acceptance is held for 1100 ms -- longer than the 1000 ms window
        // the pre-send listener was given; the ECU answers negatively 150 ms after the frame
        // is accepted, inside the window as anchored there, with 850 ms to spare for the host
        // to delay that timer.
        var first = Task.Run(() => functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x90 }, TimeSpan.FromMilliseconds(30), cts.Token));
        await Task.Delay(1100);
        hold.Set();
        await bus.DeferredEchoes.WaitForEnqueuedAsync(1, ShortTimeout);
        bus.DeferredEchoes.ReleaseNext();
        _ = Task.Run(async () => { await Task.Delay(150); bus.RaiseObserved(negative, isEcho: false); });
        await first;

        var second = functional.SendRawAsync(new byte[] { 0x22, 0xF1, 0x91 }, Window, cts.Token);
        await bus.DeferredEchoes.WaitForEnqueuedAsync(2, ShortTimeout);
        bus.DeferredEchoes.ReleaseNext();
        _ = Task.Run(async () => { await Task.Delay(20); bus.RaiseObserved(positive, isEcho: false); });
        var responses = await second;

        responses.Should().ContainSingle().Which.IsNegative.Should().BeFalse(
            "the second request waited out the window anchored at the first's late acceptance");
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
