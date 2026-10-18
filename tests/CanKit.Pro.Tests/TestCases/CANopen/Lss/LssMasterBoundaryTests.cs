using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Lss;
using CanKit.Pro.RawCan;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Lss;

public class LssMasterBoundaryTests
{
    [Theory]
    [InlineData(499, false, false)] // Before handoff.
    [InlineData(500, false, true)] // At handoff.
    [InlineData(1000, false, true)] // At transmit completion.
    [InlineData(0, false, true)] // Missing timestamps do not mean stale frames.
    [InlineData(-1, false, true)]
    [InlineData(0, true, true)] // At the deadline.
    [InlineData(1, true, false)] // Immediately after the deadline.
    public async Task Inquire_Filters_Queued_Replies_By_Their_Arrival_Timestamp(
        long timestamp, bool relativeToDeadline, bool accepted)
    {
        using var service = new LssTestService();
        var timeout = TimeSpan.FromSeconds(1);
        long deadline = 1000 + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        long arrival = relativeToDeadline ? deadline + timestamp : timestamp;
        service.Send = async frame =>
        {
            var reply = new byte[LssFrames.Length];
            reply[0] = frame.Data.Span[0];
            reply[1] = 0x42;
            await service.Subscription.Enqueue(new CanFrameEvent(
                new CanFrameView(CanFrameType.Can20, (int)CanOpenCobId.LssSlaveMessage, reply, FrameFlags.None),
                isEcho: false, receiveTimestamp: TimeSpan.Zero, hostArrivalTimestamp: arrival)).WaitAsync(TimeSpan.FromSeconds(5));
            // Resume the sender only after the master has buffered the reply. The absolute
            // deadline is already past, so neither acceptance nor rejection needs a timer.
            return new TxConfirmation
            {
                Confirmed = true,
                HostHandoffTimestamp = 500,
                HostTransmitTimestamp = 1000,
            };
        };
        using var master = CanOpenLss.OpenMaster(service, new LssMasterOptions { ResponseTimeout = timeout });

        if (accepted)
            (await master.InquireNodeIdAsync()).Should().Be(0x42);
        else
        {
            var inquire = async () => await master.InquireNodeIdAsync();
            await inquire.Should().ThrowAsync<LssTimeoutException>();
        }
    }

    [Fact]
    public async Task Dispose_Leaves_A_Parked_Readers_Cancellation_Source_Alive()
    {
        using var service = new LssTestService();
        service.Subscription.ParkReader = true;
        using var master = CanOpenLss.OpenMaster(service, leaveOpen: false);
        try
        {
            await service.Subscription.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            master.Dispose();

            service.Disposals.Should().Be(1);
            service.Subscription.Disposed.Should().BeTrue();
            service.Subscription.Finished.Task.IsCompleted.Should().BeFalse();
            service.Subscription.Release.TrySetResult(true);
            // Accessing WaitHandle throws if Dispose tore down the source while this reader
            // was still using its token. The reader is held by a signal, not a scheduling delay.
            (await service.Subscription.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5)))
                .Should().BeTrue();
        }
        finally
        {
            service.Subscription.Release.TrySetResult(true);
            await service.Subscription.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task A_Zero_Delay_Return_To_Waiting_Requires_A_Rate_And_A_Switch(bool rate, bool callback)
    {
        using var rig = new ClockedLssRig(new LssMasterOptions
        {
            BitRateSwitch = callback ? (_, _) => Task.CompletedTask : null,
        });
        var activate = async () => await rig.Master.ActivateBitTimingAsync(0,
            bitsPerSecond: rate ? 250_000 : null, returnToWaiting: true);

        await activate.Should().ThrowAsync<ArgumentException>();
        rig.Service.Commands.Should().BeEmpty("validation must happen before the slave changes rate");
    }

    [Fact]
    public async Task A_Submillisecond_Response_Window_Ends_Only_When_The_Clock_Reaches_It()
    {
        using var rig = new ClockedLssRig(new LssMasterOptions { ResponseTimeout = TimeSpan.FromTicks(1) });
        var inquire = rig.Master.InquireNodeIdAsync();
        await rig.ArmedAsync(TimeSpan.FromTicks(1));
        inquire.IsCompleted.Should().BeFalse();

        await rig.Clock.AdvanceAsync(TimeSpan.FromTicks(1));

        var result = async () => await inquire.WaitAsync(TimeSpan.FromSeconds(5));
        await result.Should().ThrowAsync<LssTimeoutException>();
    }

    [Fact]
    public async Task A_Deadline_Near_The_Counter_Limit_Saturates_Instead_Of_Wrapping()
    {
        using var service = new LssTestService();
        service.Send = async frame =>
        {
            var payload = new byte[LssFrames.Length];
            payload[0] = frame.Data.Span[0];
            payload[1] = 0x42;
            await service.Subscription.Enqueue(new CanFrameEvent(
                new CanFrameView(CanFrameType.Can20, (int)CanOpenCobId.LssSlaveMessage, payload, FrameFlags.None),
                false, TimeSpan.Zero, long.MaxValue)).WaitAsync(TimeSpan.FromSeconds(5));
            return new TxConfirmation
            {
                Confirmed = true,
                HostHandoffTimestamp = long.MaxValue - 1,
                HostTransmitTimestamp = long.MaxValue - 1,
            };
        };
        using var master = CanOpenLss.OpenMaster(service);

        (await master.InquireNodeIdAsync()).Should().Be(0x42);
    }

    [Fact]
    public async Task Caller_Cancellation_Wins_Over_Disposal_For_A_Queued_Command()
    {
        using var rig = new ClockedLssRig();
        var first = rig.Master.InquireVendorIdAsync();
        await rig.ArmedAsync(TimeSpan.FromSeconds(1));
        using var caller = new CancellationTokenSource();
        var queued = rig.Master.InquireProductCodeAsync(caller.Token);
        // This callback runs with the caller token already cancelled. It forces disposal into
        // the same cancellation notification that releases the queued semaphore waiter.
        using var dispose = caller.Token.Register(rig.Master.Dispose);
        caller.Cancel();

        var queuedResult = async () => await queued.WaitAsync(TimeSpan.FromSeconds(5));
        await queuedResult.Should().ThrowAsync<OperationCanceledException>();
        var firstResult = async () => await first.WaitAsync(TimeSpan.FromSeconds(5));
        await firstResult.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task Async_Disposal_Does_Not_Take_An_Injected_Timer_Actor()
    {
        using var rig = new ClockedLssRig();
        await rig.Master.DisposeAsync();

        (await rig.Timers.PostAsync(() => true)).Should().BeTrue();
    }

    [Fact]
    public async Task Fastscan_Does_Not_Send_The_Next_Probe_Before_The_Cycle_Gap_Ends()
    {
        var window = TimeSpan.FromMilliseconds(100);
        var gap = TimeSpan.FromMilliseconds(1);
        using var rig = new ClockedLssRig(new LssMasterOptions
        {
            FastscanTimeout = window,
            FastscanCycleTime = gap,
        });
        rig.Add(new LssAddress(1, 0, 0, 0));
        var scan = rig.Master.FastscanAsync(new LssFastscanRequest
        {
            VendorId = new LssFastscanField { Mode = LssFastscanFieldMode.Match, Value = 1 },
            ProductCode = new LssFastscanField { Mode = LssFastscanFieldMode.Skip },
            RevisionNumber = new LssFastscanField { Mode = LssFastscanFieldMode.Skip },
            SerialNumber = new LssFastscanField { Mode = LssFastscanFieldMode.Match, Value = 0 },
        });
        await rig.ArmedAsync(window);
        rig.Service.Commands.Count.Should().Be(1);
        await rig.Clock.AdvanceAsync(window);
        await rig.ArmedAsync(gap);
        rig.Service.Commands.Count.Should().Be(1);
        await rig.Clock.AdvanceAsync(gap - TimeSpan.FromTicks(1));
        rig.Service.Commands.Count.Should().Be(1);
        await rig.Clock.AdvanceAsync(TimeSpan.FromTicks(1));

        (await rig.RunAsync(scan)).Succeeded.Should().BeTrue();
    }

}
