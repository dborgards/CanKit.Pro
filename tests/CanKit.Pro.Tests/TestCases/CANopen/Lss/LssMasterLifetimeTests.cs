using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.Actor;
using CanKit.Pro.CANopen.Lss;
using CanKit.Pro.RawCan;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Lss;

public class LssMasterLifetimeTests
{
    [Fact]
    public async Task Production_Timeouts_And_Delays_Do_Not_Create_An_Actor_Thread()
    {
        int loops = ProtocolActor.RunningLoopCount;
        using var service = new LssTestService();
        service.Send = _ => Task.FromResult(new TxConfirmation { Confirmed = true });
        await using var master = CanOpenLss.OpenMaster(service, new LssMasterOptions
        {
            FastscanTimeout = TimeSpan.FromMilliseconds(10),
        });

        ProtocolActor.RunningLoopCount.Should().Be(loops);
        await master.ActivateBitTimingAsync(1).WaitAsync(LssTestTiming.SchedulingBudget);
        var scan = await master.FastscanAsync().WaitAsync(LssTestTiming.SchedulingBudget);
        scan.Status.Should().Be(LssFastscanStatus.NoUnconfiguredSlave);
        ProtocolActor.RunningLoopCount.Should().Be(loops);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Production_Disposal_Cancels_A_Pending_Window_With_The_Master_ObjectName(bool asyncDispose)
    {
        using var service = new LssTestService();
        service.Send = _ => Task.FromResult(new TxConfirmation { Confirmed = true });
        using var master = CanOpenLss.OpenMaster(service, new LssMasterOptions
        {
            ResponseTimeout = TimeSpan.FromMinutes(1),
        });
        var pending = master.InquireNodeIdAsync();
        pending.IsCompleted.Should().BeFalse();

        if (asyncDispose) await master.DisposeAsync();
        else master.Dispose();

        var inquire = async () => await pending.WaitAsync(LssTestTiming.SchedulingBudget);
        var error = await inquire.Should().ThrowAsync<ObjectDisposedException>();
        error.Which.ObjectName.Should().Be(nameof(LssMaster));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_Resource_Disposal_Uses_The_Master_Identity_Unless_The_Caller_Cancelled(bool dispose, bool cancel)
    {
        using var service = new LssTestService();
        using var caller = new CancellationTokenSource();
        ILssMaster? master = null;
        service.Send = frame =>
        {
            if (cancel) caller.Cancel();
            if (dispose) master!.Dispose();
            throw new ObjectDisposedException("adapter");
        };
        using var opened = CanOpenLss.OpenMaster(service);
        master = opened;
        var inquire = async () => await opened.InquireNodeIdAsync(caller.Token).WaitAsync(LssTestTiming.SchedulingBudget);

        if (cancel)
        {
            var error = await inquire.Should().ThrowAsync<OperationCanceledException>();
            error.Which.CancellationToken.Should().Be(caller.Token);
        }
        else
        {
            var error = await inquire.Should().ThrowAsync<ObjectDisposedException>();
            error.Which.ObjectName.Should().Be(dispose ? nameof(LssMaster) : "adapter");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disposal_During_The_First_Foreground_Drain_Reports_The_Master_ObjectName(bool parkReader)
    {
        using var rig = new ClockedLssRig(parkReader: parkReader);
        await rig.Service.Subscription.Started.Task.WaitAsync(LssTestTiming.SchedulingBudget);
        bool sent = false;
        rig.Service.Send = frame =>
        {
            sent = true;
            return Task.FromResult(new TxConfirmation { Confirmed = true });
        };
        rig.Service.Subscription.BeforeRead = () =>
        {
            if (!sent) return;
            rig.Service.Subscription.BeforeRead = null;
            rig.Master.Dispose();
            rig.Service.Subscription.ReadFailure = new InvalidOperationException("pull failed during disposal");
        };
        try
        {
            var inquire = async () => await rig.Master.InquireNodeIdAsync().WaitAsync(LssTestTiming.SchedulingBudget);
            var error = await inquire.Should().ThrowAsync<ObjectDisposedException>();
            error.Which.ObjectName.Should().Be(nameof(LssMaster));
        }
        finally
        {
            if (parkReader)
            {
                rig.Service.Subscription.Release.TrySetResult(true);
                await rig.Service.Subscription.Finished.Task.WaitAsync(LssTestTiming.SchedulingBudget);
            }
        }
    }
}
