using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core.Exceptions;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases;

/// <summary>
/// Verifies the L2 TX-Confirm abstraction (CanKit.Pro.RawCan, arc42 §6.3 / ADR-7,
/// SRS FR-RAW-030..034).
///
/// The echo-path cases run against <see cref="ControllableBus"/> rather than an adapter: the
/// contract is stated in terms of <c>CanReceiveDataView.IsEcho</c>, and how (or whether) a given
/// adapter flags its self-echo is an adapter detail. Driving the echo from the test keeps these
/// assertions about CanKit.Pro's matching logic, and makes "the echo never arrives" an explicit
/// setting instead of a filter trick. The non-echo cases still use the real loopback adapter,
/// because there the thing under test *is* driver acceptance.
/// </summary>
public class TxConfirmTests : IClassFixture<VirtualAdapterFixture>
{
    private static ICanBus OpenPlain() => VirtualAdapterFixture.Open(VirtualAdapterFixture.NewSession("txconfirm"), 0);

    private static ControllableBus OpenEcho() => ControllableBus.EchoCapable(VirtualAdapterFixture.NewSession("txconfirm"));

    private static ControllableBus OpenDeferredEcho()
        => ControllableBus.DeferredEchoCapable(VirtualAdapterFixture.NewSession("txconfirm"));

    // Only ever a bound against a hang: every wait below is on an event the test itself caused.
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);

    // FR-RAW-030/032: without echo, SendConfirmedAsync resolves as soon as the driver accepts the
    // frame, explicitly marked as an approximation.
    [Fact]
    public async Task NonEcho_Bus_Confirms_Via_Driver_Acceptance_Approximation()
    {
        using var sender = OpenPlain();
        using var service = new CanBusService(sender);

        var result = await service.SendConfirmedAsync(CanFrame.Classic(0x123, new byte[] { 1, 2, 3 }));

        result.Confirmed.Should().BeTrue();
        result.IsApproximated.Should().BeTrue();
        result.FailureReason.Should().Be(TxConfirmFailureReason.None);
    }

    // FR-RAW-030/031: with echo enabled, SendConfirmedAsync resolves from an actual matched echo frame,
    // never presented as an approximation.
    [Fact]
    public async Task Echo_Bus_Confirms_Via_Real_Echo_Match()
    {
        using var sender = OpenEcho();
        using var service = new CanBusService(sender);

        var result = await service.SendConfirmedAsync(CanFrame.Classic(0x321, new byte[] { 9, 8, 7 }));

        result.Confirmed.Should().BeTrue();
        result.IsApproximated.Should().BeFalse();
        result.FailureReason.Should().Be(TxConfirmFailureReason.None);
    }

    // FR-RAW-031, the actual FIFO assertion: with several byte-identical sends pending at once,
    // the n-th echo must confirm the n-th *transmitted* send. Nothing else can tell the callers
    // apart -- the frames are identical on the wire and the TxConfirmations they receive are
    // identical too -- so the only observable of "matched FIFO" is which caller's task completes
    // when, and that is exactly what this asserts.
    //
    // This needs ControllableBus in Deferred echo mode. With the synchronous echo the Virtual
    // adapter delivers, the echo re-enters CanBusService's pending-send lock on the transmitting thread
    // before that thread leaves Transmit, so the pending list holds exactly one entry -- that
    // thread's own -- for the entire match. Every FIFO ordering rule is then vacuously satisfied
    // and the test cannot fail however the matching code is written (see DeferredEchoQueue).
    [Fact]
    public async Task Echo_Bus_Matches_Identical_Pending_Sends_In_Fifo_Order()
    {
        using var sender = OpenDeferredEcho();
        using var service = new CanBusService(sender);

        const int n = 4;
        var frame = CanFrame.Classic(0x500, new byte[] { 42 });

        // Long per-call timeout: the sends must still be pending when the last one registers, and
        // the only thing that may ever complete one is an echo this test releases.
        var sends = new Task<TxConfirmation>[n];
        for (var i = 0; i < n; i++)
        {
            sends[i] = service.SendConfirmedAsync(frame, TimeSpan.FromSeconds(30));
            // A parked echo means Transmit ran, which CanBusService does after registering the
            // pending entry -- so this waits on registration order, not on wall-clock luck.
            await sender.DeferredEchoes.WaitForEnqueuedAsync(i + 1, ShortTimeout);
        }

        sender.DeferredEchoes.Count.Should().Be(n, "no echo has been released yet");
        sends.Should().OnlyContain(t => !t.IsCompleted,
            "a send may only resolve once its own echo comes back");

        for (var i = 0; i < n; i++)
        {
            sender.DeferredEchoes.ReleaseNext().Should().BeTrue();

            // WhenAny over everything still outstanding, rather than awaiting sends[i] directly:
            // a LIFO (or arbitrary) match resolves the *wrong* caller, and this reports that as
            // "the wrong send was confirmed" immediately instead of as a timeout minutes later.
            var completed = await Task.WhenAny(sends.Skip(i)).WaitAsync(ShortTimeout);
            completed.Should().BeSameAs(sends[i],
                "the {0}. echo must confirm the {0}. transmitted send, not a later one", i + 1);

            var confirmation = await completed;
            confirmation.Confirmed.Should().BeTrue();
            confirmation.IsApproximated.Should().BeFalse();
            confirmation.FailureReason.Should().Be(TxConfirmFailureReason.None);

            for (var later = i + 1; later < n; later++)
                sends[later].IsCompleted.Should().BeFalse(
                    "one echo confirms exactly one send, so send {0} must still be pending", later);
        }
    }

    // FR-RAW-031 under real concurrency: byte-identical sends racing across threads must each get
    // their own confirmation -- no cross-matching, no crash. This is the exact class of bug the
    // review flagged for the ISO-TP prototype's deadline queue crashing on identical in-flight
    // frames. Deliberately kept on the *synchronous* echo bus: that is the reentrant
    // register-transmit-match-remove path the Virtual adapter's timing drives, and this test exists
    // to hammer it from many threads at once. It says nothing about FIFO ordering -- with a
    // synchronous echo there is never more than one pending entry to order. The test above is
    // where ordering is proven, and the test below is the same stress with the echo arriving the
    // way a hardware adapter's does.
    [Fact]
    public async Task Echo_Bus_Matches_Concurrent_Identical_Frames_Individually_Without_Crashing()
    {
        using var sender = OpenEcho();
        using var service = new CanBusService(sender);

        const int n = 16;
        var frame = CanFrame.Classic(0x500, new byte[] { 42 });

        var tasks = Enumerable.Range(0, n).Select(_ => Task.Run(() => service.SendConfirmedAsync(frame))).ToArray();
        var results = await Task.WhenAll(tasks);

        results.Should().HaveCount(n);
        results.Should().OnlyContain(r => r.Confirmed && !r.IsApproximated);
        sender.TransmitCount.Should().Be(n);
    }

    // #352 (R-8): the same 16-thread stress, with the echo delivered the way a hardware adapter
    // delivers it -- from another thread, after Transmit has returned. Only here does the pending
    // FIFO for one key actually hold several entries while sends keep racing into it: a releaser
    // thread hands back one echo at a time, and only once at least two are parked, so every
    // release but the last matches against a list of two or more. The synchronous test above
    // cannot reach that state at all.
    //
    // No clock: the releaser waits on transmits having happened, and the bound below only catches
    // a send that is never confirmed.
    [Fact]
    public async Task Deferred_Echo_Bus_Matches_Concurrent_Identical_Frames_Individually()
    {
        using var sender = OpenDeferredEcho();
        using var service = new CanBusService(sender);

        const int n = 16;
        var frame = CanFrame.Classic(0x500, new byte[] { 42 });
        var hangBound = TimeSpan.FromSeconds(30);

        var releaser = Task.Run(async () =>
        {
            for (var released = 0; released < n; released++)
            {
                await sender.DeferredEchoes.WaitForEnqueuedAsync(Math.Min(released + 2, n), hangBound);
                sender.DeferredEchoes.ReleaseNext().Should().BeTrue();
            }
        });
        var sends = Enumerable.Range(0, n)
            .Select(_ => Task.Run(() => service.SendConfirmedAsync(frame, TimeSpan.FromSeconds(60))))
            .ToArray();

        var results = await Task.WhenAll(sends).WaitAsync(hangBound);
        await releaser.WaitAsync(hangBound);

        results.Should().OnlyContain(r => r.Confirmed && !r.IsApproximated);
        sender.TransmitCount.Should().Be(n);
        sender.DeferredEchoes.Count.Should().Be(0, "every echo was handed back and consumed");
    }

    // FR-RAW-031/033: a pending send that has already been resolved -- here by cancellation, in
    // the field usually by its own timeout -- must leave the echo FIFO at the moment it is
    // resolved. While it stayed there it was the oldest entry for its key, so the *next*
    // byte-identical send lost its echo to it and timed out too: one timeout cascading into the
    // next.
    //
    // The setup is what makes this deterministic instead of a race against a pool thread. The
    // first send is resolved from inside the second send's Transmit, i.e. on the transmitting
    // thread while CanBusService still holds its pending-send lock. The first send's own async
    // cleanup wants that same lock, so it cannot run until this Transmit returns -- by which time
    // the second send's echo has already been matched, inside the same call. If the resolution
    // path does not unlink the entry itself, the expired entry is therefore *guaranteed*, not
    // merely likely, to be the FIFO head when that echo arrives.
    [Fact]
    public async Task Echo_Bus_Does_Not_Let_A_Resolved_Send_Consume_A_Later_Identical_Echo()
    {
        using var sender = OpenEcho();
        using var service = new CanBusService(sender);

        var frame = CanFrame.Classic(0x600, new byte[] { 7, 7 });
        using var cancelFirst = new CancellationTokenSource();

        // The first send stays pending: its echo never comes back.
        sender.EchoAcceptedFrames = false;
        var first = service.SendConfirmedAsync(frame, TimeSpan.FromSeconds(30), cancelFirst.Token);
        sender.TransmitCount.Should().Be(1,
            "SendConfirmedAsync registers the pending entry and transmits before it awaits anything");

        sender.EchoAcceptedFrames = true;
        sender.OnTransmitting = _ => cancelFirst.Cancel();

        var second = await service.SendConfirmedAsync(frame, ShortTimeout);

        second.Confirmed.Should().BeTrue(
            "the echo belongs to the only send still waiting for one, not to the cancelled entry");
        second.IsApproximated.Should().BeFalse();
        second.FailureReason.Should().Be(TxConfirmFailureReason.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
    }

    // Regression for the fix to #24 itself. An earlier revision unlinked the expired entry under
    // _pendingGate *before* completing its Tcs, so resolving a send had to wait for that lock --
    // which SendWithEchoConfirmAsync holds across _bus.Transmit. The visible cost was on the
    // caller's own thread: CancellationTokenSource.Cancel() for one send blocked until an
    // unrelated send's driver call returned. Completing the Tcs without the lock removes that;
    // TryMatchEcho's skip of already-completed entries is what keeps #24 fixed meanwhile.
    //
    // Scope, stated so this test is not read as promising more than it checks: the caller's
    // SendConfirmedAsync task still completes only after the `finally` unlinks, and that unlink does
    // take the lock. That coupling predates this change and follows from holding _pendingGate
    // across Transmit at all -- the deliberate decision recorded next to that lock (#53). What is
    // asserted here is the part this change is responsible for.
    //
    // Causal, not timed: the claim is "Cancel() returned while the other send was still inside
    // Transmit", not "within N ms" (unmeasurable on a shared runner, see #92). The 2 s allowance
    // is slack for a slow runner -- with the coupling in place Cancel() cannot return until the
    // release below, which happens afterwards.
    [Fact]
    public async Task Cancelling_One_Send_Does_Not_Block_On_An_Unrelated_Send_Inside_Transmit()
    {
        using var sender = OpenEcho();
        using var service = new CanBusService(sender);

        using var cancelFirst = new CancellationTokenSource();
        sender.EchoAcceptedFrames = false;
        var first = service.SendConfirmedAsync(
            CanFrame.Classic(0x610, new byte[] { 1 }), TimeSpan.FromSeconds(30), cancelFirst.Token);

        // A second, unrelated send parks inside Transmit -- and so inside _pendingGate.
        using var stuckInTransmit = new ManualResetEventSlim(false);
        using var reachedTransmit = new ManualResetEventSlim(false);
        sender.OnTransmitting = f =>
        {
            if (f.ID != 0x611) return;
            reachedTransmit.Set();
            stuckInTransmit.Wait(TimeSpan.FromSeconds(10));
        };
        var blocked = Task.Run(() => service.SendConfirmedAsync(
            CanFrame.Classic(0x611, new byte[] { 2 }), ShortTimeout));

        reachedTransmit.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue(
            "the second send must actually be parked in Transmit for this test to mean anything");

        var cancelling = Task.Run(() => cancelFirst.Cancel());
        var finished = await Task.WhenAny(cancelling, Task.Delay(TimeSpan.FromSeconds(2)));
        var cancelReturnedWhileBlocked = ReferenceEquals(finished, cancelling);

        stuckInTransmit.Set();
        await cancelling;
        await blocked;

        cancelReturnedWhileBlocked.Should().BeTrue(
            "cancelling one send must not wait on an unrelated send's driver call to return");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
    }

    // #294: the caller's token is consulted before the driver call. A token already cancelled
    // when the call is made abandons the send on the approximated path: nothing is handed to the
    // driver, and the call ends with OperationCanceledException rather than a confirmation.
    [Fact]
    public async Task A_Token_Cancelled_Before_The_Call_Abandons_The_Approximated_Send()
    {
        using var sender = ControllableBus.Plain(VirtualAdapterFixture.NewSession("txconfirm"));
        using var service = new CanBusService(sender);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var send = service.SendConfirmedAsync(CanFrame.Classic(0x620, new byte[] { 1 }), ShortTimeout, cancelled.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        sender.TransmitCount.Should().Be(0, "a send cancelled before the driver call must not reach the driver");
        sender.FirstAsyncTransmit.IsCompleted.Should().BeFalse("nor its asynchronous transmit");
    }

    // #294 on the echo path: the token is consulted inside the pending gate, immediately before
    // Transmit, so a send that waited for the gate behind another send's driver call — and was
    // cancelled meanwhile — is abandoned there instead of being transmitted after the wait.
    [Fact]
    public async Task A_Token_Cancelled_While_The_Echo_Path_Waits_On_The_Gate_Abandons_The_Send()
    {
        using var sender = OpenEcho();
        using var service = new CanBusService(sender);

        // One send parks inside Transmit, and so inside the pending gate.
        using var stuckInTransmit = new ManualResetEventSlim(false);
        using var reachedTransmit = new ManualResetEventSlim(false);
        sender.OnTransmitting = f =>
        {
            if (f.ID != 0x630) return;
            reachedTransmit.Set();
            stuckInTransmit.Wait(TimeSpan.FromSeconds(10));
        };
        var holder = Task.Run(() => service.SendConfirmedAsync(CanFrame.Classic(0x630, new byte[] { 1 }), ShortTimeout));
        reachedTransmit.Wait(ShortTimeout).Should().BeTrue("the first send must be parked in Transmit for this test to mean anything");

        // The second send queues for the gate; its token is cancelled while it waits.
        using var cancelSecond = new CancellationTokenSource();
        var second = Task.Run(() => service.SendConfirmedAsync(CanFrame.Classic(0x631, new byte[] { 2 }), ShortTimeout, cancelSecond.Token));
        await Task.Delay(50);
        cancelSecond.Cancel();
        stuckInTransmit.Set();

        (await holder).Confirmed.Should().BeTrue();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        sender.TransmitCount.Should().Be(1, "the cancelled send must not reach the driver after its wait for the gate");
    }

    // FR-RAW-031: the echo is matched on what identifies the frame, not on its ID alone. A
    // standard 0x100 and an extended 0x100 carrying the same payload are two different frames on
    // the wire; keyed on the ID alone they share one FIFO, so the extended frame's echo confirms
    // whichever of the two was sent first and the other waits for an echo that has already been
    // consumed.
    [Fact]
    public async Task Echo_Bus_Does_Not_Confirm_A_Standard_Send_From_An_Extended_Echo_With_The_Same_Id()
    {
        using var sender = OpenEcho();
        using var service = new CanBusService(sender);
        sender.EchoAcceptedFrames = false; // every echo in this test is delivered by hand

        var payload = new byte[] { 0xAB };
        var standard = service.SendConfirmedAsync(CanFrame.Classic(0x100, payload), TimeSpan.FromSeconds(30));
        var extended = service.SendConfirmedAsync(
            CanFrame.Classic(0x100, payload, isExtendedFrame: true), TimeSpan.FromSeconds(30));
        sender.TransmitCount.Should().Be(2);

        sender.RaiseObserved(CanFrame.Classic(0x100, payload, isExtendedFrame: true), isEcho: true);

        var completed = await Task.WhenAny(standard, extended).WaitAsync(ShortTimeout);
        completed.Should().BeSameAs(extended, "an extended-ID echo confirms the extended-ID send");
        (await completed).Confirmed.Should().BeTrue();
        standard.IsCompleted.Should().BeFalse("no echo for the standard-ID frame has arrived yet");

        service.Dispose(); // resolves the send left outstanding on purpose
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => standard);
    }

    // FR-RAW-031, the same point for the frame kind: a Classic and a CAN-FD frame with the same ID
    // and the same payload bytes are not interchangeable, and one's echo must not confirm the
    // other.
    [Fact]
    public async Task Echo_Bus_Does_Not_Confirm_A_Classic_Send_From_A_Can_Fd_Echo_With_The_Same_Id()
    {
        using var sender = OpenEcho();
        using var service = new CanBusService(sender);
        sender.EchoAcceptedFrames = false;

        var payload = new byte[] { 0xAB };
        var classic = service.SendConfirmedAsync(CanFrame.Classic(0x100, payload), TimeSpan.FromSeconds(30));
        var fd = service.SendConfirmedAsync(CanFrame.Fd(0x100, payload), TimeSpan.FromSeconds(30));
        sender.TransmitCount.Should().Be(2);

        sender.RaiseObserved(CanFrame.Fd(0x100, payload), isEcho: true);

        var completed = await Task.WhenAny(classic, fd).WaitAsync(ShortTimeout);
        completed.Should().BeSameAs(fd, "a CAN-FD echo confirms the CAN-FD send");
        (await completed).Confirmed.Should().BeTrue();
        classic.IsCompleted.Should().BeFalse("no echo for the Classic frame has arrived yet");

        service.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => classic);
    }

    // #352 (Q7): what SendConfirmedAsync documents for a CAN-FD length no frame carries -- it is
    // sent, not refused, and an echo of the frame padded to the next valid length (as an adapter
    // rebuilding the echo from its DLC delivers it) does not confirm it, so the call times out.
    // The timeout is tripped by hand once the padded echo has been offered: had it matched, the
    // confirmation would already be set and the trip would change nothing.
    [Fact]
    public async Task A_Non_Canonical_Fd_Length_Is_Sent_And_Its_Padded_Echo_Does_Not_Confirm_It()
    {
        using var sender = OpenEcho();
        sender.EchoAcceptedFrames = false;
        using var service = new CanBusService(sender);
        CancellationTokenSource? armed = null;
        service.ArmConfirmTimeout = (source, _) => armed = source;

        var payload = Enumerable.Range(1, 10).Select(i => (byte)i).ToArray();
        var send = service.SendConfirmedAsync(CanFrame.Fd(0x675, payload), ShortTimeout);
        sender.TransmitCount.Should().Be(1, "a non-canonical length is not refused");

        sender.RaiseObserved(CanFrame.Fd(0x675, payload.Concat(new byte[2]).ToArray()), isEcho: true);
        armed.Should().NotBeNull("the echo wait is armed before the call returns");
        armed!.Cancel();

        (await send.WaitAsync(ShortTimeout)).FailureReason.Should().Be(TxConfirmFailureReason.Timeout);
    }

    // FR-RAW-033: a send whose echo will never arrive fails observably (Confirmed = false,
    // FailureReason = Timeout) within the configured timeout, not an indefinite hang.
    [Fact]
    public async Task Echo_Bus_Times_Out_Observably_When_No_Echo_Arrives()
    {
        using var sender = OpenEcho();
        sender.EchoAcceptedFrames = false; // accepted by the driver, but the echo never comes back
        using var service = new CanBusService(sender);

        var timeout = TimeSpan.FromMilliseconds(300);
        var sw = Stopwatch.StartNew();
        var result = await service.SendConfirmedAsync(CanFrame.Classic(0x123, new byte[] { 1 }), timeout);
        sw.Stop();

        result.Confirmed.Should().BeFalse();
        result.IsApproximated.Should().BeFalse();
        result.FailureReason.Should().Be(TxConfirmFailureReason.Timeout);
        // Bounded, not instant and not "never": close to the configured timeout.
        sw.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(250));
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    // FR-RAW-033 / #202: "never hangs" holds on the approximated path too. A custom bus whose
    // TransmitAsync never completes resolves as a timed-out confirmation instead of hanging. No
    // lower bound on the elapsed time is asserted: the only failure of interest is the hang, and
    // that is bounded by ShortTimeout rather than measured against a clock.
    [Fact]
    public async Task NonEcho_Bus_Times_Out_Observably_When_TransmitAsync_Never_Completes()
    {
        using var bus = ControllableBus.Plain(VirtualAdapterFixture.NewSession("txconfirm"));
        bus.StallTransmitAsync = true;
        using var service = new CanBusService(bus);

        var result = await service.SendConfirmedAsync(CanFrame.Classic(0x123, new byte[] { 1 }), TimeSpan.FromMilliseconds(50))
            .WaitAsync(ShortTimeout);

        result.Confirmed.Should().BeFalse();
        result.IsApproximated.Should().BeFalse();
        result.FailureReason.Should().Be(TxConfirmFailureReason.Timeout);
        bus.ReleaseStalledTransmits();
    }

    // Codex and Bugbot on #216: when the timeout wins, the driver's operation may still be running
    // while the caller -- entitled to, once SendConfirmedAsync completes -- disposes its TX lease. The
    // driver must have been handed a frame that does not depend on that lease.
    [Fact]
    public async Task A_Timed_Out_Approximated_Send_Leaves_The_Driver_A_Frame_Independent_Of_The_Callers_Lease()
    {
        using var bus = ControllableBus.Plain(VirtualAdapterFixture.NewSession("txconfirm"));
        bus.StallTransmitAsync = true;
        using var service = new CanBusService(bus);

        var payload = new byte[] { 1, 2, 3, 4 };
        var owner = new ScrubbedOwner((byte[])payload.Clone());
        using var lease = CanFrame.Classic(0x123, owner);

        var result = await service.SendConfirmedAsync(lease, TimeSpan.FromMilliseconds(50)).WaitAsync(ShortTimeout);
        result.FailureReason.Should().Be(TxConfirmFailureReason.Timeout);
        lease.Dispose(); // what the caller may now do

        owner.Disposed.Should().BeTrue("the caller's lease is released");
        var handed = await bus.FirstAsyncTransmit.WaitAsync(ShortTimeout);
        handed.Data.ToArray().Should().Equal(payload, "the driver's copy survives the caller's release");
        bus.ReleaseStalledTransmits();
    }

    // The transmit a timed-out send abandons may still fail later; nobody awaits it, so the
    // service must observe it itself, or the fault surfaces as an unobserved task exception.
    [Fact]
    public async Task A_Late_Fault_Of_The_Abandoned_Transmit_Is_Observed()
    {
        using var bus = ControllableBus.Plain(VirtualAdapterFixture.NewSession("txconfirm"));
        bus.StallTransmitAsync = true;
        using var service = new CanBusService(bus);
        var observed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.AbandonedTransmitFaultObserved = e => observed.TrySetResult(e);
        var marker = new InvalidOperationException("late driver failure");

        (await service.SendConfirmedAsync(CanFrame.Classic(0x123, new byte[] { 1 }), TimeSpan.FromMilliseconds(50))
            .WaitAsync(ShortTimeout)).FailureReason.Should().Be(TxConfirmFailureReason.Timeout);
        bus.FaultStalledTransmits(marker);

        (await observed.Task.WaitAsync(ShortTimeout)).Should().BeSameAs(marker);
    }

    // The private copy handed to the driver is the same frame: kind, identifier, flags, payload.
    [Fact]
    public async Task The_Drivers_Copy_Is_The_Same_Frame_For_Every_Kind()
    {
        var frames = new[]
        {
            CanFrame.Classic(0x123, new byte[] { 1, 2, 3 }),
            CanFrame.Classic(0x1ABCDE, new byte[] { 4 }, isExtendedFrame: true),
            CanFrame.Classic(0x321, ReadOnlyMemory<byte>.Empty, isRemoteFrame: true),
            CanFrame.Fd(0x456, new byte[12], BRS: true, ESI: false, isExtendedFrame: false),
            CanFrame.Fd(0x1FEDCB, new byte[] { 9, 8, 7, 6 }, BRS: false, ESI: true, isExtendedFrame: true),
        };

        foreach (var original in frames)
        {
            using var bus = ControllableBus.Plain(VirtualAdapterFixture.NewSession("txconfirm"));
            using var service = new CanBusService(bus);

            (await service.SendConfirmedAsync(original, ShortTimeout)).Confirmed.Should().BeTrue();

            var handed = await bus.FirstAsyncTransmit.WaitAsync(ShortTimeout);
            handed.FrameKind.Should().Be(original.FrameKind);
            handed.ID.Should().Be(original.ID);
            handed.Flags.Should().Be(original.Flags);
            handed.Data.ToArray().Should().Equal(original.Data.ToArray());
        }
    }

    // #352 (R-5): a remote frame with a data length -- built through the memory-owner factory,
    // which allows it -- is a frame the echo path transmits as it is. The approximated path used to
    // rebuild it through the ReadOnlyMemory factory, which refuses that shape, and threw before
    // the driver call. Both paths now send it, and the driver gets the same frame.
    [Fact]
    public async Task A_Remote_Frame_With_A_Data_Length_Is_Sent_On_Both_Paths()
    {
        CanFrame RemoteWithLength() => CanFrame.Classic(0x322, new ScrubbedOwner(new byte[8]), isRemoteFrame: true, ownMemory: false);

        using (var plain = ControllableBus.Plain(VirtualAdapterFixture.NewSession("txconfirm")))
        using (var service = new CanBusService(plain))
        {
            var original = RemoteWithLength();
            var result = await service.SendConfirmedAsync(original, ShortTimeout);
            result.Confirmed.Should().BeTrue();
            result.IsApproximated.Should().BeTrue();

            var handed = await plain.FirstAsyncTransmit.WaitAsync(ShortTimeout);
            handed.FrameKind.Should().Be(original.FrameKind);
            handed.ID.Should().Be(original.ID);
            handed.Flags.Should().Be(original.Flags);
            handed.IsRemoteFrame.Should().BeTrue();
            handed.Len.Should().Be(8, "the data length of a remote frame is part of the frame");
        }

        using (var echo = OpenEcho())
        using (var service = new CanBusService(echo))
        {
            var result = await service.SendConfirmedAsync(RemoteWithLength(), ShortTimeout);
            result.Confirmed.Should().BeTrue();
            result.IsApproximated.Should().BeFalse();
        }
    }

    // R-5 follow-up: the remote frame's private copy travels in a memory owner, and CanKit's
    // CanFrame.Dispose disposes the owner whatever ownMemory says (v0.5.6 CanFrame.cs:370). A driver
    // that disposes the frame it was handed therefore reaches that owner: it must release nothing
    // and throw nothing, and never reach the caller's buffer, which the copy is detached from.
    [Fact]
    public async Task A_Driver_Disposing_Its_Copy_Of_A_Remote_Frame_Releases_Nothing_Of_The_Callers()
    {
        var callers = new ScrubbedOwner(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        var original = CanFrame.Classic(0x323, callers, isRemoteFrame: true, ownMemory: false);
        using var plain = ControllableBus.Plain(VirtualAdapterFixture.NewSession("txconfirm"));
        using var service = new CanBusService(plain);

        (await service.SendConfirmedAsync(original, ShortTimeout)).Confirmed.Should().BeTrue();
        var handed = await plain.FirstAsyncTransmit.WaitAsync(ShortTimeout);

        handed.Invoking(f => f.Dispose()).Should().NotThrow();
        callers.Disposed.Should().BeFalse("the driver's copy does not own the caller's buffer");
        original.Data.ToArray().Should().Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
    }

    private sealed class ScrubbedOwner : System.Buffers.IMemoryOwner<byte>
    {
        private readonly byte[] _buffer;
        public ScrubbedOwner(byte[] buffer) => _buffer = buffer;
        public bool Disposed { get; private set; }
        public Memory<byte> Memory => _buffer;
        public void Dispose()
        {
            Disposed = true;
            for (var i = 0; i < _buffer.Length; i++) _buffer[i] = 0xFF; // a returned pool buffer gets reused
        }
    }

    // Codex on #216: a timed-out send asks the driver to stop the transmit it abandoned, so a
    // cooperative bus does not put a stale frame on the wire behind the caller's next send.
    [Fact]
    public async Task A_Timed_Out_Approximated_Send_Cancels_The_Drivers_Transmit()
    {
        using var bus = ControllableBus.Plain(VirtualAdapterFixture.NewSession("txconfirm"));
        bus.StallTransmitAsync = true;
        using var service = new CanBusService(bus);

        (await service.SendConfirmedAsync(CanFrame.Classic(0x123, new byte[] { 1 }), TimeSpan.FromMilliseconds(50))
            .WaitAsync(ShortTimeout)).FailureReason.Should().Be(TxConfirmFailureReason.Timeout);

        (await bus.FirstAsyncTransmitToken.WaitAsync(ShortTimeout)).IsCancellationRequested.Should().BeTrue();
    }

    // Bugbot on #216: cancelling a stalled send is a cancellation, immediately -- not a timeout
    // reported after the full timeout has run out. The timeout here is far beyond ShortTimeout,
    // so a send that waited for it would trip the hang bound instead of completing.
    [Fact]
    public async Task Cancelling_A_Stalled_Approximated_Send_Cancels_It_Instead_Of_Waiting_For_The_Timeout()
    {
        using var bus = ControllableBus.Plain(VirtualAdapterFixture.NewSession("txconfirm"));
        bus.StallTransmitAsync = true;
        using var service = new CanBusService(bus);
        using var cts = new CancellationTokenSource();

        var send = service.SendConfirmedAsync(CanFrame.Classic(0x123, new byte[] { 1 }), TimeSpan.FromMinutes(5), cts.Token);
        await bus.FirstAsyncTransmit.WaitAsync(ShortTimeout);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send.WaitAsync(ShortTimeout));
        send.IsCanceled.Should().BeTrue();
        bus.ReleaseStalledTransmits();
    }

    // #202: the bound must not turn an asynchronous but timely acceptance into a timeout.
    [Fact]
    public async Task NonEcho_Bus_Confirms_When_An_Asynchronous_TransmitAsync_Completes_In_Time()
    {
        using var bus = ControllableBus.Plain(VirtualAdapterFixture.NewSession("txconfirm"));
        bus.StallTransmitAsync = true;
        using var service = new CanBusService(bus);

        var send = service.SendConfirmedAsync(CanFrame.Classic(0x123, new byte[] { 1 }), ShortTimeout);
        bus.ReleaseStalledTransmits();
        var result = await send.WaitAsync(ShortTimeout);

        result.Confirmed.Should().BeTrue();
        result.IsApproximated.Should().BeTrue();
    }

    // FR-RAW-034 (Should): the timeout is configurable per call, not a single hardcoded value.
    // #362: this compared two Stopwatch readings, and a stall of 400 ms during the shorter call
    // inverted them. The timeouts now run on a clock the test moves: each call is seen to arm
    // exactly its own timeout, and to time out when -- and only when -- that much has passed,
    // while the other call is still waiting.
    [Fact]
    public async Task Echo_Bus_Timeout_Is_Configurable_Per_Call()
    {
        using var sender = OpenEcho();
        sender.EchoAcceptedFrames = false;
        using var service = new CanBusService(sender);
        using var clock = new VirtualClock();
        var actor = clock.NewActor();
        service.ArmConfirmTimeout = (source, timeout) => actor.Schedule(timeout, () =>
        {
            try { source.Cancel(); }
            catch (ObjectDisposedException) { /* the call ended first and released it */ }
        });

        var shortTimeout = TimeSpan.FromMilliseconds(100);
        var longTimeout = TimeSpan.FromMilliseconds(500);

        // The longer one first, so the earliest armed timer is always the one just sent.
        var longSend = service.SendConfirmedAsync(CanFrame.Classic(0x124, new byte[] { 2 }), longTimeout);
        await clock.WaitUntilTimerArmedAsync(actor, longTimeout, ShortTimeout);
        var shortSend = service.SendConfirmedAsync(CanFrame.Classic(0x123, new byte[] { 1 }), shortTimeout);
        await clock.WaitUntilTimerArmedAsync(actor, shortTimeout, ShortTimeout);

        await clock.AdvanceAsync(shortTimeout);
        (await shortSend.WaitAsync(ShortTimeout)).FailureReason.Should().Be(TxConfirmFailureReason.Timeout);
        longSend.IsCompleted.Should().BeFalse("its own 500 ms timeout has not run out");

        await clock.AdvanceAsync(longTimeout - shortTimeout - TimeSpan.FromMilliseconds(1));
        longSend.IsCompleted.Should().BeFalse("its own 500 ms timeout is still 1 ms away");

        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(1));
        (await longSend.WaitAsync(ShortTimeout)).FailureReason.Should().Be(TxConfirmFailureReason.Timeout);
    }

    // FR-RAW-033: outright rejection (driver never accepted the frame) resolves immediately as
    // Rejected -- it must not be indistinguishable from a timeout, and must not wait for one.
    [Fact]
    public async Task NonEcho_Bus_Reports_Rejected_When_Driver_Does_Not_Accept_The_Frame()
    {
        using var sender = OpenPlain(); // Classic (Can20) protocol mode
        using var service = new CanBusService(sender);

        // An FD frame on a Classic-mode bus is rejected by the adapter's transceiver (returns 0),
        // regardless of echo.
        var result = await service.SendConfirmedAsync(CanFrame.Fd(0x100, new byte[] { 1, 2, 3, 4 }));

        result.Confirmed.Should().BeFalse();
        result.IsApproximated.Should().BeFalse();
        result.FailureReason.Should().Be(TxConfirmFailureReason.Rejected);
    }

    [Fact]
    public async Task Echo_Bus_Reports_Rejected_Immediately_Without_Waiting_For_Timeout()
    {
        using var sender = OpenEcho();
        sender.AcceptTransmit = false; // the driver refuses the frame outright
        using var service = new CanBusService(sender);

        var sw = Stopwatch.StartNew();
        var result = await service.SendConfirmedAsync(CanFrame.Classic(0x100, new byte[] { 1, 2, 3, 4 }), TimeSpan.FromSeconds(5));
        sw.Stop();

        result.FailureReason.Should().Be(TxConfirmFailureReason.Rejected);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1)); // resolved immediately, not via the 5s timeout
    }

    // Disposing the service must not leave an in-flight SendConfirmedAsync call hanging until its own
    // timeout -- standard .NET convention: disposing an in-flight operation's owner cancels it.
    [Fact]
    public async Task Disposing_Service_Cancels_Outstanding_SendConfirmed_Calls()
    {
        using var sender = OpenEcho();
        sender.EchoAcceptedFrames = false;
        var service = new CanBusService(sender);

        var pendingTask = service.SendConfirmedAsync(CanFrame.Classic(0x123, new byte[] { 1 }), TimeSpan.FromSeconds(30));

        service.Dispose();

        Func<Task> act = async () => await pendingTask;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // #352 (R-2): the same on the approximated path, for a bus whose TransmitAsync is genuinely
    // asynchronous. The call returns its task only once the send is parked on the pending
    // transmit -- everything before that wait runs on this thread -- so Dispose lands on a tracked
    // send. No timeout is armed -- the seam swallows it -- so only Dispose can end the call: the
    // outcome does not depend on the runtime's timer, and the hang bound only turns a missing
    // cancellation into a failure instead of a hung run.
    [Fact]
    public async Task Disposing_Service_Cancels_An_Approximated_Send_Whose_Transmit_Is_Pending()
    {
        using var bus = ControllableBus.Plain(VirtualAdapterFixture.NewSession("txconfirm"));
        bus.StallTransmitAsync = true;
        using var service = new CanBusService(bus); // Dispose is idempotent: the explicit call below is the one under test
        service.ArmConfirmTimeout = (_, _) => { }; // arms nothing: only Dispose ends it

        var send = service.SendConfirmedAsync(CanFrame.Classic(0x672, new byte[] { 1 }), TimeSpan.FromMinutes(5));
        service.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send.WaitAsync(ShortTimeout));
        send.IsCanceled.Should().BeTrue("a disposal is a cancellation, as on the echo path");
        (await bus.FirstAsyncTransmitToken.WaitAsync(ShortTimeout)).IsCancellationRequested
            .Should().BeTrue("the driver is asked to stop the transmit the send abandoned");
        bus.ReleaseStalledTransmits();
    }

    // #352 (R-2): Dispose landing after the driver was handed the frame but before the send is
    // tracked. Arming the timeout is that instant, and the seam disposes the service there. The
    // send finds the sweep done and ends at once instead of waiting out its timeout.
    [Fact]
    public async Task Dispose_Before_A_Pending_Approximated_Send_Is_Tracked_Still_Cancels_It()
    {
        using var bus = ControllableBus.Plain(VirtualAdapterFixture.NewSession("txconfirm"));
        bus.StallTransmitAsync = true;
        using var service = new CanBusService(bus);
        service.ArmConfirmTimeout = (_, _) => service.Dispose(); // arms nothing: only Dispose ends it

        var send = service.SendConfirmedAsync(CanFrame.Classic(0x673, new byte[] { 1 }), TimeSpan.FromMinutes(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send.WaitAsync(ShortTimeout));
        send.IsCanceled.Should().BeTrue();
        bus.ReleaseStalledTransmits();
    }

    // #352 (R-2): a send whose timeout had already run out when Dispose reached it reports the
    // timeout, not a cancellation -- Dispose ends only a wait that is still running. The seam trips
    // the timeout and then disposes, both before the send is tracked.
    [Fact]
    public async Task An_Approximated_Send_That_Timed_Out_Before_Dispose_Reports_The_Timeout()
    {
        using var bus = ControllableBus.Plain(VirtualAdapterFixture.NewSession("txconfirm"));
        bus.StallTransmitAsync = true;
        using var service = new CanBusService(bus);
        service.ArmConfirmTimeout = (source, _) =>
        {
            source.Cancel();
            service.Dispose();
        };

        var result = await service.SendConfirmedAsync(CanFrame.Classic(0x674, new byte[] { 1 }), TimeSpan.FromMinutes(5))
            .WaitAsync(ShortTimeout);

        result.FailureReason.Should().Be(TxConfirmFailureReason.Timeout);
        bus.ReleaseStalledTransmits();
    }

    // #352 (mutation R-M2): Dispose landing between SendConfirmedAsync's eager disposed check and
    // the pending-send gate. Dispose sets _pendingDisposed under that gate before it sweeps, and the
    // send checks it there before registering, so it is refused instead of registering an entry
    // nobody will ever sweep, transmitting, and waiting out its own timeout. The send's read of
    // WorkMode is the instant between the two checks, and the double disposes the service there.
    [Fact]
    public async Task Dispose_Between_The_Disposed_Check_And_The_Gate_Refuses_The_Send()
    {
        using var sender = OpenEcho();
        var service = new CanBusService(sender);
        sender.OnWorkModeRead = () =>
        {
            sender.OnWorkModeRead = null;
            service.Dispose();
        };

        var send = service.SendConfirmedAsync(CanFrame.Classic(0x670, new byte[] { 1 }), TimeSpan.FromSeconds(30));

        await Assert.ThrowsAsync<ObjectDisposedException>(() => send.WaitAsync(ShortTimeout));
        sender.TransmitCount.Should().Be(0, "a send refused at the gate never reaches the driver");
    }

    // #352: after Dispose a send is refused on both paths. On the approximated path the eager check
    // is the only thing that refuses it; on the echo path the gate check above would as well.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SendConfirmed_After_Dispose_Throws_ObjectDisposedException(bool echoPath)
    {
        using var sender = echoPath ? OpenEcho() : ControllableBus.Plain(VirtualAdapterFixture.NewSession("txconfirm"));
        var service = new CanBusService(sender);
        service.Dispose();

        var send = service.SendConfirmedAsync(CanFrame.Classic(0x671, new byte[] { 1 }), ShortTimeout);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => send.WaitAsync(ShortTimeout));
        sender.TransmitCount.Should().Be(0);
    }

    [Fact]
    public async Task SendConfirmed_Rejects_Non_Positive_Timeout()
    {
        using var sender = OpenPlain();
        using var service = new CanBusService(sender);

        Func<Task> act = async () => await service.SendConfirmedAsync(CanFrame.Classic(0x123, new byte[] { 1 }), TimeSpan.Zero);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    // #352 (R-4): a timeout CancelAfter cannot arm used to be found out only after the frame was on
    // the wire -- the echo path arms it after Transmit, and the approximated path whenever the
    // transmit is still pending -- so the caller was told "bad argument" about a frame that had
    // gone out. Such a timeout is now clamped to the bound before the driver is called, on both
    // paths and on one bound for every target framework: the frame goes out once, and the wait is
    // armed at int.MaxValue ms. The seam records what is armed and trips it at once, so nothing
    // here waits on a clock.
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task A_Timeout_Above_The_Bound_Is_Clamped_To_It_On_Both_Paths(bool echoPath, bool maxValue)
    {
        var timeout = maxValue
            ? TimeSpan.MaxValue
            : TimeSpan.FromMilliseconds(int.MaxValue) + TimeSpan.FromMilliseconds(1);
        using var sender = echoPath
            ? OpenEcho()
            : ControllableBus.Plain(VirtualAdapterFixture.NewSession("txconfirm"));
        sender.EchoAcceptedFrames = false; // on the echo path, only the armed timeout ends the wait
        sender.StallTransmitAsync = !echoPath; // on the approximated path, only a pending transmit arms it
        using var service = new CanBusService(sender);
        var armed = new List<TimeSpan>();
        service.ArmConfirmTimeout = (source, armedTimeout) =>
        {
            lock (armed) armed.Add(armedTimeout);
            source.Cancel();
        };

        var result = await service.SendConfirmedAsync(CanFrame.Classic(0x660, new byte[] { 1 }), timeout)
            .WaitAsync(ShortTimeout);
        sender.ReleaseStalledTransmits();

        result.FailureReason.Should().Be(TxConfirmFailureReason.Timeout, "the armed timeout was tripped");
        armed.Should().Equal(new[] { TimeSpan.FromMilliseconds(int.MaxValue) }, "the timeout is clamped to the bound");
        var handedToTheDriver = echoPath ? sender.TransmitCount == 1 : sender.FirstAsyncTransmit.IsCompleted;
        handedToTheDriver.Should().BeTrue("the frame is sent, not refused");
    }

    // The real timer accepts the bound, and with the clamp it is never handed more: on the echo path
    // CancelAfter runs after the frame is out, which is where an over-large value used to throw.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task A_Timeout_At_Or_Above_The_Bound_Is_Armed_By_The_Real_Timer_On_The_Echo_Path(int row)
    {
        var timeout = row switch
        {
            0 => TimeSpan.FromMilliseconds(int.MaxValue),
            1 => TimeSpan.FromMilliseconds(int.MaxValue) + TimeSpan.FromMilliseconds(1),
            _ => TimeSpan.MaxValue,
        };
        using var sender = OpenDeferredEcho();
        using var service = new CanBusService(sender);

        var send = service.SendConfirmedAsync(CanFrame.Classic(0x661, new byte[] { 1 }), timeout);
        await sender.DeferredEchoes.WaitForEnqueuedAsync(1, ShortTimeout);
        sender.DeferredEchoes.ReleaseNext().Should().BeTrue();

        (await send.WaitAsync(ShortTimeout)).Confirmed.Should().BeTrue();
    }

    // FR-RAW-033: a bus-off transition while a confirmation is outstanding must resolve it
    // immediately with FailureReason = BusOff -- not leave the caller hanging until the configured
    // timeout.
    [Fact]
    public async Task Outstanding_SendConfirmed_Resolves_As_BusOff_Immediately_On_Fault()
    {
        using var sender = OpenEcho();
        sender.EchoAcceptedFrames = false;
        using var service = new CanBusService(sender);

        var pendingTask = service.SendConfirmedAsync(CanFrame.Classic(0x123, new byte[] { 1 }),
            TimeSpan.FromSeconds(30));

        // The one combination that produces BusOff: the state reads BusOff and a fault is reported
        // through the bus's fault channel. The service's OnFaultOccurred must then fail every
        // outstanding confirmation. No CanKit 0.5.6 adapter raises a fault for a bus-off (#348);
        // the double does, which is why this path is testable at all.
        sender.BusState = BusState.BusOff;
        sender.RaiseFault(new InvalidOperationException("simulated bus-off"));

        // "Immediately, not via the 30 s timeout" is in the result, not in a Stopwatch (#362): the
        // timeout would resolve it as Timeout, and 30 s is past the hang bound, so only the sweep
        // can produce BusOff here. The Stopwatch bound (< 5 s) duplicated the hang bound and was
        // the one assertion a stalled host could turn red.
        var result = await pendingTask.WaitAsync(ShortTimeout);

        result.Confirmed.Should().BeFalse();
        result.IsApproximated.Should().BeFalse();
        result.FailureReason.Should().Be(TxConfirmFailureReason.BusOff);
    }

    // The other half of the guard the test above exercises only one side of: a fault reported
    // while the controller is *not* off the bus is not a bus-off, and must not fail the outstanding
    // confirmation as one. The test above sets BusState = BusOff *and* raises the fault, so the
    // BusState check in OnFaultOccurred could be deleted without it going red (mutation R-M1 in
    // docs/reviews/2026-10-21-deep-review/rawcan-addressing.md, #348). Here the state stays
    // ErrActive, the fault is raised, and the send's own echo -- parked until after the fault --
    // must still confirm it: with the guard gone the sweep would have resolved it as BusOff first.
    [Fact]
    public async Task A_Fault_While_The_Bus_Is_Not_Off_Leaves_The_Send_Pending()
    {
        using var sender = OpenDeferredEcho();
        using var service = new CanBusService(sender);

        var send = service.SendConfirmedAsync(CanFrame.Classic(0x123, new byte[] { 1 }), TimeSpan.FromSeconds(30));
        await sender.DeferredEchoes.WaitForEnqueuedAsync(1, ShortTimeout);

        sender.BusState.Should().Be(BusState.ErrActive, "the double reports a healthy controller until a test moves it");
        sender.RaiseFault(new InvalidOperationException("poll loop died, controller still on the bus"));

        sender.DeferredEchoes.ReleaseNext().Should().BeTrue();
        var result = await send.WaitAsync(ShortTimeout);

        result.Confirmed.Should().BeTrue("a fault that is not a bus-off leaves the send to its own echo");
        result.IsApproximated.Should().BeFalse();
        result.FailureReason.Should().Be(TxConfirmFailureReason.None);
    }

    // #348 (R-9): the only fault a CanKit 0.5.6 adapter raises is its receive loop dying, after
    // which nothing will ever arrive -- and until this change the service swallowed it unless the
    // bus was also off. It now goes through the service's one fault channel, as the adapter's own
    // exception, so a consumer that only reads subscriptions can learn, on the service it
    // subscribed on, that the bus is dead.
    // FaultOccurred is raised synchronously by the double, so the relay has run when RaiseFault
    // returns; nothing here waits on a clock.
    [Fact]
    public void A_Bus_Fault_Is_Relayed_Through_BackgroundExceptionOccurred()
    {
        using var sender = OpenEcho();
        using var service = new CanBusService(sender);

        Exception? relayed = null;
        service.BackgroundExceptionOccurred += (_, ex) => relayed = ex;

        var fault = new InvalidOperationException("poll loop died");
        sender.RaiseFault(fault);

        relayed.Should().BeSameAs(fault, "the adapter's exception is relayed as itself, not wrapped");
    }

    // #381: a multicast Invoke stops at the first listener that throws, and a single try around it
    // kept dispatch alive but hid the relayed fault -- the one signal that the bus is dead -- from
    // every listener registered after a throwing one. The relay is synchronous, as above.
    [Fact]
    public void A_Throwing_Fault_Listener_Does_Not_Hide_The_Fault_From_Later_Listeners()
    {
        using var sender = OpenEcho();
        using var service = new CanBusService(sender);

        Exception? second = null;
        service.BackgroundExceptionOccurred += (_, _) => throw new InvalidOperationException("the logging listener failed");
        service.BackgroundExceptionOccurred += (_, ex) => second = ex;

        var fault = new InvalidOperationException("poll loop died");
        sender.RaiseFault(fault);

        second.Should().BeSameAs(fault, "every listener hears of the fault, whatever an earlier one does");
    }

    // Codex on #369: the relay runs before the bus-off sweep, and a listener may answer "the bus
    // is dead" by disposing the bus -- after which the PCAN, Kvaser, SocketCAN and ZLG BusState
    // getters throw CanBusDisposedException (ThrowIfDisposed). The state the sweep decides
    // on is therefore read *before* any listener runs. Read afterwards, the getter throws out of
    // OnFaultOccurred, the sweep never runs, and the outstanding send times out instead of
    // resolving as BusOff. The double throws the same way on request; the fault is raised
    // synchronously, so nothing here waits on a clock.
    [Fact]
    public async Task A_Listener_That_Disposes_The_Bus_On_The_Relayed_Fault_Does_Not_Defeat_The_BusOff_Sweep()
    {
        using var sender = OpenDeferredEcho();
        sender.BusStateFailureOnceDisposed = () => new CanBusDisposedException();
        using var service = new CanBusService(sender);

        var send = service.SendConfirmedAsync(CanFrame.Classic(0x125, new byte[] { 1 }), TimeSpan.FromSeconds(30));
        await sender.DeferredEchoes.WaitForEnqueuedAsync(1, ShortTimeout);

        service.BackgroundExceptionOccurred += (_, _) => sender.Dispose();
        sender.BusState = BusState.BusOff;
        sender.RaiseFault(new InvalidOperationException("poll loop died, controller off the bus"));

        var result = await send.WaitAsync(ShortTimeout);

        result.Confirmed.Should().BeFalse();
        result.FailureReason.Should().Be(TxConfirmFailureReason.BusOff,
            "the sweep decides on the state read before the listener disposed the bus");
    }

    // The other side of the read above: OnFaultOccurred runs on the poll thread that just died, and
    // the BusState getter there may itself throw -- CanBusDisposedException from the adapters'
    // ThrowIfDisposed once the bus is disposed, ObjectDisposedException from a bus that follows
    // .NET's own convention. A read that fails that way counts as "not off": the sweep is skipped
    // even though the state was set to BusOff before the bus went away, and the relay still
    // happens. An unguarded read throws out of OnFaultOccurred before any listener runs. The send's
    // own echo, parked until after the fault, then confirms it -- had the sweep run, it would have
    // resolved the send as BusOff first. The fault is raised synchronously by the double, so the
    // relay has run when RaiseFault returns; nothing here waits on a clock.
    [Theory]
    [InlineData(typeof(CanBusDisposedException))]
    [InlineData(typeof(ObjectDisposedException))]
    public async Task A_BusState_Read_That_Fails_Skips_The_Sweep_But_Not_The_Relay(Type failure)
    {
        using var sender = OpenDeferredEcho();
        sender.BusStateFailureOnceDisposed = () => failure == typeof(ObjectDisposedException)
            ? new ObjectDisposedException(nameof(ControllableBus))
            : new CanBusDisposedException();
        using var service = new CanBusService(sender);

        var send = service.SendConfirmedAsync(CanFrame.Classic(0x126, new byte[] { 1 }), TimeSpan.FromSeconds(30));
        await sender.DeferredEchoes.WaitForEnqueuedAsync(1, ShortTimeout);

        Exception? relayed = null;
        service.BackgroundExceptionOccurred += (_, ex) => relayed = ex;

        sender.BusState = BusState.BusOff;
        sender.Dispose();
        var fault = new InvalidOperationException("poll loop died");
        sender.Invoking(s => s.RaiseFault(fault)).Should().NotThrow();

        relayed.Should().BeSameAs(fault, "a BusState read that fails must not swallow the relay");

        sender.DeferredEchoes.ReleaseNext().Should().BeTrue();
        var result = await send.WaitAsync(ShortTimeout);
        result.FailureReason.Should().Be(TxConfirmFailureReason.None,
            "a state that could not be read is not a bus-off, so the send is not resolved as one");
        result.Confirmed.Should().BeTrue("the send is left to its own echo");
    }

    // A getter that throws something other than a failed state read (a third-party bus's own
    // bug, say) is not swallowed -- it still escapes into the adapter's raiseFault -- but the
    // fault is relayed before it does, so "relayed either way" (README § TX-Confirm) holds for it
    // too (Codex on #369). No sweep runs: the exception leaves OnFaultOccurred first, and the
    // send is left to its own echo. Synchronous throughout, nothing waits on a clock.
    [Fact]
    public async Task A_BusState_Read_That_Throws_Anything_Else_Still_Relays_The_Fault()
    {
        using var sender = OpenDeferredEcho();
        var unexpected = new InvalidOperationException("third-party getter bug");
        sender.BusStateFailureOnceDisposed = () => unexpected;
        using var service = new CanBusService(sender);

        var send = service.SendConfirmedAsync(CanFrame.Classic(0x127, new byte[] { 1 }), TimeSpan.FromSeconds(30));
        await sender.DeferredEchoes.WaitForEnqueuedAsync(1, ShortTimeout);

        Exception? relayed = null;
        service.BackgroundExceptionOccurred += (_, ex) => relayed = ex;

        sender.BusState = BusState.BusOff;
        sender.Dispose();
        var fault = new InvalidOperationException("poll loop died");
        sender.Invoking(s => s.RaiseFault(fault)).Should().Throw<InvalidOperationException>()
            .Which.Should().BeSameAs(unexpected, "an exception that is not a failed state read is not swallowed");

        relayed.Should().BeSameAs(fault, "the fault is relayed before the getter's exception escapes");

        sender.DeferredEchoes.ReleaseNext().Should().BeTrue();
        var result = await send.WaitAsync(ShortTimeout);
        result.FailureReason.Should().Be(TxConfirmFailureReason.None, "no sweep ran");
        result.Confirmed.Should().BeTrue("the send is left to its own echo");
    }

    // #349, a characterisation of the matching as it stands, not a contract: the maintainer's
    // decision there is whether a late echo of a send that already timed out is absorbed for a
    // bounded time (the J1939 node's ClaimEchoGrace model, #180) or left as a documented limit of
    // content-based matching. Either way this is the scenario, made reproducible: the first send's
    // frame was accepted and its echo is on its way, but arrives only after the send timed out and
    // a byte-identical second send went out behind it. Today that late echo confirms the second
    // send -- the two echoes cannot be told apart on the wire, and the first send's entry is gone.
    // A decision for the tombstone turns this test into the red one it is implemented against.
    [Fact]
    public async Task A_Late_Echo_Of_A_Timed_Out_Send_Is_Matched_To_The_Next_Identical_Send()
    {
        using var sender = OpenDeferredEcho();
        using var service = new CanBusService(sender);

        var frame = CanFrame.Classic(0x700, new byte[] { 3, 1, 4 });

        // Not lost, late: the echo stays parked past the send's own timeout. The timeout is a
        // delay the test waits out, not a quantity it measures (#92).
        var first = await service.SendConfirmedAsync(frame, TimeSpan.FromMilliseconds(50)).WaitAsync(ShortTimeout);
        first.FailureReason.Should().Be(TxConfirmFailureReason.Timeout);
        sender.DeferredEchoes.Count.Should().Be(1, "the first send's echo is still on its way");

        var second = service.SendConfirmedAsync(frame, TimeSpan.FromSeconds(30));
        await sender.DeferredEchoes.WaitForEnqueuedAsync(2, ShortTimeout);

        // Oldest first: this is the first send's echo, arriving late.
        sender.DeferredEchoes.ReleaseNext().Should().BeTrue();

        // Inverts with a tombstone: the second send would then still be pending here, waiting for
        // the echo that is still parked.
        var confirmation = await second.WaitAsync(ShortTimeout);
        confirmation.Confirmed.Should().BeTrue("as it stands, the late echo is matched to the next identical send (#349)");
        confirmation.IsApproximated.Should().BeFalse();
        sender.DeferredEchoes.Count.Should().Be(1, "the second send's own echo has not been released");
    }

    // Echo matching runs for every echo frame the adapter reports while any send is outstanding,
    // and it used to copy that frame's payload into the lookup key just to ask whether anything
    // was waiting for it. The key never leaves the lookup, so it can borrow the payload instead.
    //
    // Measured rather than asserted structurally, because "does not allocate" is precisely the
    // claim: the frames below deliberately do not match the outstanding send, so every one of them
    // takes the full lookup path. GetAllocatedBytesForCurrentThread is exact for this thread, and
    // RaiseObserved delivers synchronously on it, so the only noise is the fixed per-call cost of
    // raising the event -- far below the 64-byte payload a copy would add each time.
    [Fact]
    public async Task Echo_Lookup_Does_Not_Copy_The_Payload_Of_Every_Echo_Frame()
    {
        using var sender = OpenEcho();
        using var service = new CanBusService(sender);
        sender.EchoAcceptedFrames = false;

        // One outstanding send, so the lookup is actually reached (with none, OnFrameObserved
        // short-circuits before building a key at all).
        var outstanding = service.SendConfirmedAsync(CanFrame.Classic(0x111, new byte[] { 1 }),
            TimeSpan.FromSeconds(30));

        // A 64-byte payload on an ID nothing is waiting for: reached, hashed, compared, no match.
        var unmatched = CanFrame.Fd(0x222, new byte[64]);

        const int warmup = 50;
        const int measured = 500;
        for (var i = 0; i < warmup; i++) sender.RaiseObserved(unmatched, isEcho: true);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < measured; i++) sender.RaiseObserved(unmatched, isEcho: true);
        var perFrame = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)measured;

        perFrame.Should().BeLessThan(64,
            "the lookup key must borrow the echo payload rather than copy it");

        outstanding.IsCompleted.Should().BeFalse("none of those echoes matched the pending send");

        service.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => outstanding);
    }

    // The arrival stamp the demux hands to every subscriber must say when RawCan saw the frame,
    // not when RawCan finished with it. The two differ by an unbounded amount on exactly one
    // path: OnFrameObserved calls TryMatchEcho first, and TryMatchEcho takes the pending-send
    // lock that SendWithEchoConfirmAsync deliberately holds across Transmit (#102 and the comment
    // on that lock). A punctual frame arriving while a send is inside Transmit therefore waits
    // there, and a stamp taken after the wait makes it look as late as the wait was long -- which
    // is precisely the wrong reject the stamp exists to prevent (Codex on #112).
    //
    // Asserted as an ordering, not as a duration: the stamp must predate the instant this test
    // released the lock. Nothing the host does can reorder those two -- the blocked thread cannot
    // run before it is released -- so there is no tolerance to widen, and no clock quantity the
    // runner can perturb. A load spike only makes the gap larger.
    [Fact]
    public async Task Frame_Arrival_Is_Stamped_Before_The_Pending_Send_Lock()
    {
        using var sender = OpenEcho();
        // The test raises the echo itself, on a thread it controls, so the frame reaches the
        // demux while the transmitting thread is still holding the lock.
        sender.EchoAcceptedFrames = false;
        using var service = new CanBusService(sender);
        using var subscription = service.Subscribe(
            CanIdFilter.Range(0x500, 0x500, CanFilterIDType.Standard), includeEcho: true);

        using var transmitting = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        sender.OnTransmitting = _ =>
        {
            transmitting.Set();
            release.Wait(ShortTimeout);
        };

        var frame = CanFrame.Classic(0x500, new byte[] { 42 });
        // Off the test thread: SendConfirmedAsync runs synchronously as far as Transmit, so calling it
        // here would park *this* thread in OnTransmitting and there would be nobody left to
        // release it.
        var send = Task.Run(() => service.SendConfirmedAsync(frame, TimeSpan.FromSeconds(30)));
        transmitting.Wait(ShortTimeout).Should().BeTrue("the send must reach Transmit");

        // A dedicated thread rather than the pool: the point of the test is that this thread ends
        // up parked on the pending-send lock, and a thread is where that is observable.
        var observer = new Thread(() => sender.RaiseObserved(frame, isEcho: true))
        {
            IsBackground = true,
            Name = "echo-observer",
        };
        observer.Start();

        // Wait for the signal -- the observer actually blocked -- instead of sleeping and hoping.
        // ThreadState is a heuristic, so a timeout backs it up; either way the assertion below is
        // an ordering against an instant that has not happened yet.
        var waitingForLock = SpinWait.SpinUntil(
            () => observer.ThreadState.HasFlag(System.Threading.ThreadState.WaitSleepJoin), ShortTimeout);
        waitingForLock.Should().BeTrue(
            "the echo must reach the demux while the pending-send lock is held");

        subscription.TryRead(out _).Should().BeFalse(
            "the frame cannot have been delivered yet -- its dispatch is behind the lock");

        var releasedAt = Stopwatch.GetTimestamp();
        release.Set();

        (await send.WaitAsync(ShortTimeout)).Confirmed.Should().BeTrue();
        observer.Join(ShortTimeout).Should().BeTrue();

        subscription.TryRead(out var delivered).Should().BeTrue("the echo was subscribed to");
        delivered.HostArrivalTimestamp.Should().BeLessThan(releasedAt,
            "the stamp must be taken when the frame arrived, which was before this test let the "
            + "transmitting thread out of Transmit");
    }

    // #102 says a blocking driver makes "every subscription stall with it -- including
    // subscriptions that have nothing to do with the frame being sent". Half of that is already
    // pinned, one test up: Frame_Arrival_Is_Stamped_Before_The_Pending_Send_Lock parks an echo on
    // a dedicated thread and asserts it cannot be delivered while the driver holds the lock.
    //
    // This is the complement, and it is what makes the ticket's claim precise. OnFrameObserved
    // takes that lock only via TryMatchEcho, i.e. only for a frame flagged as an echo while a send
    // is pending; dispatch itself reads a volatile snapshot and takes no lock. So a *plain* frame
    // goes through untouched, and the blanket stall is a consequence of one RX thread queueing
    // behind the blocked echo rather than of subscriptions being serialised against sends.
    //
    // Note what this is not: it passes whether or not Transmit holds the lock, so it documents the
    // lock-free path rather than guarding #102. The test above is the regression pin.
    [Fact]
    public async Task A_Blocking_Transmit_Does_Not_Hold_Up_A_Plain_Frame()
    {
        using var sender = OpenEcho();
        // The test raises every frame itself, so nothing arrives except when it says so.
        sender.EchoAcceptedFrames = false;
        using var service = new CanBusService(sender);

        const int watchedId = 0x321;
        using var subscription = service.Subscribe(
            CanIdFilter.Range(watchedId, watchedId, CanFilterIDType.Standard), includeEcho: true);

        using var transmitting = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        // Deliberately much larger than ShortTimeout, and a hang bound rather than a tolerance:
        // the assertion below must be the thing that expires first if dispatch ever starts taking
        // the lock, so that the test fails on it instead of outliving the block and passing.
        var driverBlockBound = TimeSpan.FromSeconds(30);
        sender.OnTransmitting = _ =>
        {
            transmitting.Set();
            release.Wait(driverBlockBound);
        };

        var sent = CanFrame.Classic(0x500, new byte[] { 42 });
        // Off the test thread, for the same reason as the test above: SendConfirmedAsync runs
        // synchronously as far as Transmit, so calling it here would park *this* thread in
        // OnTransmitting with nobody left to release it.
        var send = Task.Run(() => service.SendConfirmedAsync(sent, TimeSpan.FromSeconds(30)));
        try
        {
            transmitting.Wait(ShortTimeout).Should().BeTrue("the send must reach Transmit");

            // On another thread, and awaited *before* the driver is released: raising it here
            // would only block the test thread, and dispatch would then complete once the driver
            // gave up on its own bound -- so `TryRead` would succeed and the test would pass for
            // the very stall it claims to rule out (Codex and Bugbot, on #124).
            //
            // Completing this await while the transmitting thread is still parked in the driver is
            // the assertion. Not an echo, so OnFrameObserved never calls TryMatchEcho.
            var dispatch = Task.Run(() => sender.RaiseObserved(
                CanFrame.Classic(watchedId, new byte[] { 7 }), isEcho: false));
            await dispatch.WaitAsync(ShortTimeout);

            subscription.TryRead(out var delivered).Should().BeTrue(
                "a plain frame's dispatch never takes the pending-send lock, so it goes through "
                + "while a driver is blocked inside Transmit (#102)");
            delivered.Frame.ID.Should().Be(watchedId);
        }
        finally
        {
            // Unconditional: a failed assertion above would otherwise leave the send inside
            // Transmit holding the lock, and Dispose wants that same lock -- so the run would wedge
            // instead of reporting what actually broke (Codex and Bugbot, on #124).
            release.Set();
        }

        // The send is still waiting on an echo that never came; give it one so the test leaves
        // nothing outstanding.
        sender.RaiseObserved(sent, isEcho: true);
        (await send.WaitAsync(ShortTimeout)).Confirmed.Should().BeTrue();
    }

    // #352 (R-3): which results carry the two host stamps is path-dependent, and nothing in RawCan
    // pinned it -- only UDS and LSS tests consumed the stamps. The service is given a counter as
    // its host clock, so "before" and "after" are orderings of readings rather than durations,
    // and the timeouts are tripped through the ArmConfirmTimeout seam rather than waited out.
    private static CanBusService WithCountingClock(ICanBus bus)
    {
        long ticks = 0;
        return new CanBusService(bus, () => Interlocked.Increment(ref ticks));
    }

    private static void ShouldBracketTheDriverCall(TxConfirmation result)
    {
        result.HostHandoffTimestamp.Should().BePositive("the reading before the driver call is carried");
        result.HostTransmitTimestamp.Should().BeGreaterThan(result.HostHandoffTimestamp,
            "the reading after the driver call is taken after the one before it");
    }

    private static void ShouldCarryNoStamps(TxConfirmation result)
    {
        result.HostHandoffTimestamp.Should().Be(0);
        result.HostTransmitTimestamp.Should().Be(0);
    }

    // On the echo path the driver call has returned before the echo wait begins, so every result
    // for an accepted frame -- confirmed, timed out, bus-off -- carries both readings.
    [Fact]
    public async Task The_Echo_Path_Stamps_Every_Result_For_A_Frame_The_Driver_Accepted()
    {
        using var sender = OpenEcho();
        using var service = WithCountingClock(sender);

        var confirmed = await service.SendConfirmedAsync(CanFrame.Classic(0x640, new byte[] { 1 }), ShortTimeout);
        confirmed.Confirmed.Should().BeTrue();
        ShouldBracketTheDriverCall(confirmed);

        sender.EchoAcceptedFrames = false;
        service.ArmConfirmTimeout = static (source, _) => source.Cancel();
        var timedOut = await service.SendConfirmedAsync(CanFrame.Classic(0x641, new byte[] { 2 }), ShortTimeout)
            .WaitAsync(ShortTimeout);
        timedOut.FailureReason.Should().Be(TxConfirmFailureReason.Timeout);
        ShouldBracketTheDriverCall(timedOut);

        service.ArmConfirmTimeout = static (_, _) => { };
        var outstanding = service.SendConfirmedAsync(CanFrame.Classic(0x642, new byte[] { 3 }), ShortTimeout);
        sender.BusState = BusState.BusOff;
        sender.RaiseFault(new InvalidOperationException("simulated bus-off"));
        var busOff = await outstanding.WaitAsync(ShortTimeout);
        busOff.FailureReason.Should().Be(TxConfirmFailureReason.BusOff);
        ShouldBracketTheDriverCall(busOff);
    }

    [Fact]
    public async Task The_Echo_Path_Stamps_No_Rejection()
    {
        using var sender = OpenEcho();
        sender.AcceptTransmit = false;
        using var service = WithCountingClock(sender);

        var rejected = await service.SendConfirmedAsync(CanFrame.Classic(0x643, new byte[] { 1 }), ShortTimeout);

        rejected.FailureReason.Should().Be(TxConfirmFailureReason.Rejected);
        ShouldCarryNoStamps(rejected);
    }

    // On the approximated path only a confirmation is stamped: a timeout there means the driver's
    // asynchronous transmit never completed, so there is no reading after acceptance, and a
    // rejection carries none either.
    [Fact]
    public async Task The_Approximated_Path_Stamps_A_Confirmation_Only()
    {
        using var bus = ControllableBus.Plain(VirtualAdapterFixture.NewSession("txconfirm"));
        using var service = WithCountingClock(bus);

        var confirmed = await service.SendConfirmedAsync(CanFrame.Classic(0x650, new byte[] { 1 }), ShortTimeout);
        confirmed.Confirmed.Should().BeTrue();
        confirmed.IsApproximated.Should().BeTrue();
        ShouldBracketTheDriverCall(confirmed);

        bus.StallTransmitAsync = true;
        service.ArmConfirmTimeout = static (source, _) => source.Cancel();
        var timedOut = await service.SendConfirmedAsync(CanFrame.Classic(0x651, new byte[] { 2 }), ShortTimeout)
            .WaitAsync(ShortTimeout);
        bus.ReleaseStalledTransmits();
        timedOut.FailureReason.Should().Be(TxConfirmFailureReason.Timeout);
        ShouldCarryNoStamps(timedOut);

        bus.AcceptTransmit = false;
        var rejected = await service.SendConfirmedAsync(CanFrame.Classic(0x652, new byte[] { 3 }), ShortTimeout);
        rejected.FailureReason.Should().Be(TxConfirmFailureReason.Rejected);
        ShouldCarryNoStamps(rejected);
    }
}
