using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using FluentAssertions;
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

    // FR-RAW-030/032: without echo, SendConfirmed resolves as soon as the driver accepts the
    // frame, explicitly marked as an approximation.
    [Fact]
    public async Task NonEcho_Bus_Confirms_Via_Driver_Acceptance_Approximation()
    {
        using var sender = OpenPlain();
        using var service = new CanBusService(sender);

        var result = await service.SendConfirmed(CanFrame.Classic(0x123, new byte[] { 1, 2, 3 }));

        result.Confirmed.Should().BeTrue();
        result.IsApproximated.Should().BeTrue();
        result.FailureReason.Should().Be(TxConfirmFailureReason.None);
    }

    // FR-RAW-030/031: with echo enabled, SendConfirmed resolves from an actual matched echo frame,
    // never presented as an approximation.
    [Fact]
    public async Task Echo_Bus_Confirms_Via_Real_Echo_Match()
    {
        using var sender = OpenEcho();
        using var service = new CanBusService(sender);

        var result = await service.SendConfirmed(CanFrame.Classic(0x321, new byte[] { 9, 8, 7 }));

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
    // This needs ControllableBus in Deferred echo mode. With the synchronous echo a real adapter
    // delivers, the echo re-enters CanBusService's pending-send lock on the transmitting thread
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
            sends[i] = service.SendConfirmed(frame, TimeSpan.FromSeconds(30));
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
    // register-transmit-match-remove path a real echo-mode adapter drives, and this test exists to
    // hammer it from many threads at once. It says nothing about FIFO ordering -- with a
    // synchronous echo there is never more than one pending entry to order. The test above is
    // where ordering is proven.
    [Fact]
    public async Task Echo_Bus_Matches_Concurrent_Identical_Frames_Individually_Without_Crashing()
    {
        using var sender = OpenEcho();
        using var service = new CanBusService(sender);

        const int n = 16;
        var frame = CanFrame.Classic(0x500, new byte[] { 42 });

        var tasks = Enumerable.Range(0, n).Select(_ => Task.Run(() => service.SendConfirmed(frame))).ToArray();
        var results = await Task.WhenAll(tasks);

        results.Should().HaveCount(n);
        results.Should().OnlyContain(r => r.Confirmed && !r.IsApproximated);
        sender.TransmitCount.Should().Be(n);
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
        var first = service.SendConfirmed(frame, TimeSpan.FromSeconds(30), cancelFirst.Token);
        sender.TransmitCount.Should().Be(1,
            "SendConfirmed registers the pending entry and transmits before it awaits anything");

        sender.EchoAcceptedFrames = true;
        sender.OnTransmitting = _ => cancelFirst.Cancel();

        var second = await service.SendConfirmed(frame, ShortTimeout);

        second.Confirmed.Should().BeTrue(
            "the echo belongs to the only send still waiting for one, not to the cancelled entry");
        second.IsApproximated.Should().BeFalse();
        second.FailureReason.Should().Be(TxConfirmFailureReason.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
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
        var standard = service.SendConfirmed(CanFrame.Classic(0x100, payload), TimeSpan.FromSeconds(30));
        var extended = service.SendConfirmed(
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
        var classic = service.SendConfirmed(CanFrame.Classic(0x100, payload), TimeSpan.FromSeconds(30));
        var fd = service.SendConfirmed(CanFrame.Fd(0x100, payload), TimeSpan.FromSeconds(30));
        sender.TransmitCount.Should().Be(2);

        sender.RaiseObserved(CanFrame.Fd(0x100, payload), isEcho: true);

        var completed = await Task.WhenAny(classic, fd).WaitAsync(ShortTimeout);
        completed.Should().BeSameAs(fd, "a CAN-FD echo confirms the CAN-FD send");
        (await completed).Confirmed.Should().BeTrue();
        classic.IsCompleted.Should().BeFalse("no echo for the Classic frame has arrived yet");

        service.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => classic);
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
        var result = await service.SendConfirmed(CanFrame.Classic(0x123, new byte[] { 1 }), timeout);
        sw.Stop();

        result.Confirmed.Should().BeFalse();
        result.IsApproximated.Should().BeFalse();
        result.FailureReason.Should().Be(TxConfirmFailureReason.Timeout);
        // Bounded, not instant and not "never": close to the configured timeout.
        sw.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(250));
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    // FR-RAW-034 (Should): the timeout is configurable per call, not a single hardcoded value --
    // a longer configured timeout measurably takes longer to fail than a shorter one. Coarse
    // comparison since CI timing is noisy; not a tight tolerance.
    [Fact]
    public async Task Echo_Bus_Timeout_Is_Configurable_Per_Call()
    {
        using var sender = OpenEcho();
        sender.EchoAcceptedFrames = false;
        using var service = new CanBusService(sender);

        var shortSw = Stopwatch.StartNew();
        var shortResult = await service.SendConfirmed(CanFrame.Classic(0x123, new byte[] { 1 }), TimeSpan.FromMilliseconds(100));
        shortSw.Stop();

        var longSw = Stopwatch.StartNew();
        var longResult = await service.SendConfirmed(CanFrame.Classic(0x124, new byte[] { 2 }), TimeSpan.FromMilliseconds(500));
        longSw.Stop();

        shortResult.FailureReason.Should().Be(TxConfirmFailureReason.Timeout);
        longResult.FailureReason.Should().Be(TxConfirmFailureReason.Timeout);
        longSw.Elapsed.Should().BeGreaterThan(shortSw.Elapsed);
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
        var result = await service.SendConfirmed(CanFrame.Fd(0x100, new byte[] { 1, 2, 3, 4 }));

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
        var result = await service.SendConfirmed(CanFrame.Classic(0x100, new byte[] { 1, 2, 3, 4 }), TimeSpan.FromSeconds(5));
        sw.Stop();

        result.FailureReason.Should().Be(TxConfirmFailureReason.Rejected);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1)); // resolved immediately, not via the 5s timeout
    }

    // Disposing the service must not leave an in-flight SendConfirmed call hanging until its own
    // timeout -- standard .NET convention: disposing an in-flight operation's owner cancels it.
    [Fact]
    public async Task Disposing_Service_Cancels_Outstanding_SendConfirmed_Calls()
    {
        using var sender = OpenEcho();
        sender.EchoAcceptedFrames = false;
        var service = new CanBusService(sender);

        var pendingTask = service.SendConfirmed(CanFrame.Classic(0x123, new byte[] { 1 }), TimeSpan.FromSeconds(30));

        service.Dispose();

        Func<Task> act = async () => await pendingTask;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task SendConfirmed_Rejects_Non_Positive_Timeout()
    {
        using var sender = OpenPlain();
        using var service = new CanBusService(sender);

        Func<Task> act = async () => await service.SendConfirmed(CanFrame.Classic(0x123, new byte[] { 1 }), TimeSpan.Zero);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
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

        var pendingTask = service.SendConfirmed(CanFrame.Classic(0x123, new byte[] { 1 }),
            TimeSpan.FromSeconds(30));

        // What a real adapter does when the controller drops off the bus: the state goes BusOff and
        // a Fault-severity exception is reported through the bus's fault channel. The service's
        // OnFaultOccurred must then fail every outstanding confirmation.
        sender.BusState = BusState.BusOff;
        sender.RaiseFault(new InvalidOperationException("simulated bus-off"));

        var sw = Stopwatch.StartNew();
        var result = await pendingTask;
        sw.Stop();

        result.Confirmed.Should().BeFalse();
        result.IsApproximated.Should().BeFalse();
        result.FailureReason.Should().Be(TxConfirmFailureReason.BusOff);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5),
            "the BusOff path must resolve the confirmation immediately, not via the 30 s timeout");
    }
}
