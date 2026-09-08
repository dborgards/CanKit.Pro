using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
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

    // FR-RAW-031: concurrent, byte-identical sends must each be matched to their own confirmation
    // -- no cross-matching, no crash. This is the exact class of bug the review flagged for the
    // ISO-TP prototype's deadline queue crashing on identical in-flight frames. Launched via
    // Task.Run so they can genuinely interleave across real threads; the echo is delivered
    // synchronously inside Transmit (as a real echo-mode adapter does), so true overlap of pending
    // registrations isn't guaranteed on every single run, but this still exercises the exact
    // thread-safety-sensitive paths (concurrent register/match/remove under the service's pending
    // registry lock) end to end.
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
}
