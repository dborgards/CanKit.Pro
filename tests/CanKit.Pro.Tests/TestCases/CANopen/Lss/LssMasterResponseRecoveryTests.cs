using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen.Lss;
using CanKit.Pro.RawCan;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Lss;

public class LssMasterResponseRecoveryTests
{
    [Theory]
    [InlineData(0, 0, false, false)]
    [InlineData(0, 10, false, false)]
    [InlineData(0, 10, true, false)]
    [InlineData(0, 10, false, true)]
    [InlineData(1, 10, false, false)]
    [InlineData(2, 10, false, false)]
    public async Task A_Replacement_Scan_Waits_Out_An_Abandoned_Probe_And_Ignores_Its_Reply(
        int failure, int gapMilliseconds, bool unstamped, bool cancelSuccessor)
    {
        var window = TimeSpan.FromMilliseconds(100);
        var gap = TimeSpan.FromMilliseconds(gapMilliseconds);
        using var rig = new ClockedLssRig(new LssMasterOptions { FastscanTimeout = window, FastscanCycleTime = gap });
        using var caller = new CancellationTokenSource();
        var send = rig.Service.Send;
        if (failure != 0)
        {
            rig.Service.Send = _ =>
            {
                rig.Clock.Advance(TimeSpan.FromMilliseconds(50));
                if (failure == 1)
                {
                    caller.Cancel();
                    caller.Token.ThrowIfCancellationRequested();
                }
                return Task.FromResult(new TxConfirmation { Confirmed = false, FailureReason = TxConfirmFailureReason.Timeout });
            };
        }
        else if (unstamped)
            rig.Service.Send = async frame => (await send(frame)) with { HostTransmitTimestamp = 0 };
        var scanning = rig.Master.FastscanAsync(cancellationToken: caller.Token);
        if (failure == 0)
        {
            await rig.ArmedAsync(window);
            await rig.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(50));
            caller.Cancel();
        }
        var scan = async () => await scanning.WaitAsync(LssTestTiming.SchedulingBudget);
        if (failure == 2) await scan.Should().ThrowAsync<CanKit.Pro.CANopen.CanOpenTransportException>();
        else await scan.Should().ThrowAsync<OperationCanceledException>();
        rig.Service.Send = send;
        var remainingWindow = failure == 0 ? TimeSpan.FromMilliseconds(50) : window;

        using var successor = new CancellationTokenSource();
        var next = rig.Master.FastscanAsync(cancellationToken: successor.Token);
        rig.Service.Commands.Count.Should().Be(1);
        await rig.ArmedAsync(remainingWindow);
        var half = TimeSpan.FromTicks(remainingWindow.Ticks / 2);
        await rig.Clock.AdvanceAsync(half);
        // The abandoned probe's matching 4Fh arrives after cancellation. The new probe must
        // still be unsent, so its pre-send drain can discard this reply.
        await rig.InjectAsync(LssFrames.Command(LssCommand.IdentifySlave)).WaitAsync(LssTestTiming.SchedulingBudget);
        await rig.Clock.AdvanceAsync(remainingWindow - half - TimeSpan.FromTicks(1));
        rig.Service.Commands.Count.Should().Be(1);
        await rig.Clock.AdvanceAsync(TimeSpan.FromTicks(1));
        if (gap > TimeSpan.Zero)
        {
            rig.Service.Commands.Count.Should().Be(1);
            await rig.ArmedAsync(gap);
            if (cancelSuccessor)
            {
                successor.Cancel();
                var cancelled = async () => await next.WaitAsync(LssTestTiming.SchedulingBudget);
                await cancelled.Should().ThrowAsync<OperationCanceledException>();
                next = rig.Master.FastscanAsync();
                await rig.ArmedAsync(gap);
            }
            await rig.Clock.AdvanceAsync(gap - TimeSpan.FromTicks(1));
            rig.Service.Commands.Count.Should().Be(1);
            await rig.Clock.AdvanceAsync(TimeSpan.FromTicks(1));
        }
        await rig.ArmedAsync(window);
        rig.Service.Commands.Count.Should().Be(2);
        await rig.Clock.AdvanceAsync(window);
        (await next.WaitAsync(LssTestTiming.SchedulingBudget)).Status.Should().Be(LssFastscanStatus.NoUnconfiguredSlave);
    }

    [Fact]
    public async Task An_Identify_After_A_Cancelled_Probe_Cannot_Collect_The_Abandoned_Reply()
    {
        var window = TimeSpan.FromMilliseconds(100);
        using var rig = new ClockedLssRig(new LssMasterOptions { FastscanTimeout = window, IdentificationTimeout = window });
        using var caller = new CancellationTokenSource();
        var scan = rig.Master.FastscanAsync(cancellationToken: caller.Token);
        await rig.ArmedAsync(window);
        await rig.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(50));
        caller.Cancel();
        var cancel = async () => await scan.WaitAsync(LssTestTiming.SchedulingBudget);
        await cancel.Should().ThrowAsync<OperationCanceledException>();

        var identify = rig.Master.IdentifyRemoteSlavesAsync(new LssIdentityRange(1, 1, 0, 1, 0, 1));
        rig.Service.Commands.Count.Should().Be(1);
        await rig.ArmedAsync(TimeSpan.FromMilliseconds(50));
        await rig.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(25));
        await rig.InjectAsync(LssFrames.Command(LssCommand.IdentifySlave)).WaitAsync(LssTestTiming.SchedulingBudget);
        await rig.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(25));
        await rig.ArmedAsync(window);
        await rig.Clock.AdvanceAsync(window);
        (await identify.WaitAsync(LssTestTiming.SchedulingBudget)).Should().BeFalse();
    }

    [Fact]
    public async Task A_Cancelled_Inquiry_Cannot_Answer_The_Next_Inquiry()
    {
        var window = TimeSpan.FromMilliseconds(100);
        using var rig = new ClockedLssRig(new LssMasterOptions { ResponseTimeout = window });
        using var caller = new CancellationTokenSource();
        var first = rig.Master.InquireNodeIdAsync(caller.Token);
        await rig.ArmedAsync(window);
        await rig.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(50));
        caller.Cancel();
        var cancel = async () => await first.WaitAsync(LssTestTiming.SchedulingBudget);
        await cancel.Should().ThrowAsync<OperationCanceledException>();

        var second = rig.Master.InquireNodeIdAsync();
        rig.Service.Commands.Count.Should().Be(1);
        await rig.ArmedAsync(TimeSpan.FromMilliseconds(50));
        await rig.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(25));
        var reply = LssFrames.Command(LssCommand.InquireNodeId);
        reply[1] = 0x7F;
        await rig.InjectAsync(reply).WaitAsync(LssTestTiming.SchedulingBudget);
        await rig.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(25));
        await rig.ArmedAsync(window);
        var current = LssFrames.Command(LssCommand.InquireNodeId);
        current[1] = 0x42;
        await rig.InjectAsync(current).WaitAsync(LssTestTiming.SchedulingBudget);
        (await second.WaitAsync(LssTestTiming.SchedulingBudget)).Should().Be(0x42);
    }
}
