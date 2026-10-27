using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases;

public class ClockedCanBusServiceTests : IClassFixture<VirtualAdapterFixture>
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Missing_Echo_Expires_On_The_Rigs_Clock_At_Its_Own_Deadline()
    {
        using var bus = ControllableBus.DeferredEchoCapable(VirtualAdapterFixture.NewSession("clocked-confirm"));
        var clock = new ManualTimeSource();
        using var service = new ClockedCanBusService(bus, clock);
        var send = service.SendConfirmedAsync(CanFrame.Classic(0x123, new byte[] { 1 }));
        await bus.DeferredEchoes.WaitForEnqueuedAsync(1, HangGuard);
        await ArmedAsync(service, TimeSpan.FromSeconds(1));
        service.PendingConfirmations.Should().Be(1);
        clock.Advance(TimeSpan.FromSeconds(1) - TimeSpan.FromTicks(1));
        await SettleAsync(service);
        send.IsCompleted.Should().BeFalse("driver acceptance cannot replace the missing matching echo");
        (await service.Timers.NextTimerDelayAsync()).Should().Be(TimeSpan.FromTicks(1));
        clock.Advance(TimeSpan.FromTicks(1));
        await SettleAsync(service);
        (await send.WaitAsync(HangGuard)).FailureReason.Should().Be(TxConfirmFailureReason.Timeout);
        service.PendingConfirmations.Should().Be(0);
    }

    [Fact]
    public async Task Echo_And_Caller_Cancellation_Remove_Their_Own_Deadline()
    {
        using var bus = ControllableBus.DeferredEchoCapable(VirtualAdapterFixture.NewSession("clocked-confirm"));
        var clock = new ManualTimeSource();
        using var service = new ClockedCanBusService(bus, clock);
        using var cancel = new CancellationTokenSource();
        var cancelled = service.SendConfirmedAsync(CanFrame.Classic(0x123, new byte[] { 1 }),
            TimeSpan.FromSeconds(1), cancel.Token);
        await bus.DeferredEchoes.WaitForEnqueuedAsync(1, HangGuard);
        await ArmedAsync(service, TimeSpan.FromSeconds(1));
        var confirmed = service.SendConfirmedAsync(CanFrame.Classic(0x124, new byte[] { 2 }), TimeSpan.FromSeconds(2));
        await bus.DeferredEchoes.WaitForEnqueuedAsync(2, HangGuard);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(HangGuard));
        await ArmedAsync(service, TimeSpan.FromSeconds(2));
        service.PendingConfirmations.Should().Be(1);
        bus.DeferredEchoes.ReleaseNext().Should().BeTrue(); // stale echo belongs to cancelled send
        confirmed.IsCompleted.Should().BeFalse();
        bus.DeferredEchoes.ReleaseNext().Should().BeTrue();
        var result = await confirmed.WaitAsync(HangGuard);
        result.Confirmed.Should().BeTrue();
        result.IsApproximated.Should().BeFalse("this send requires its matching echo");
        service.PendingConfirmations.Should().Be(0);
        (await service.Timers.NextTimerDelayAsync()).Should().BeNull("completed sends leave no timer to drive the rig");
        clock.Advance(TimeSpan.FromSeconds(3));
        await SettleAsync(service);
    }

    private static async Task ArmedAsync(ClockedCanBusService service, TimeSpan delay)
    {
        var until = DateTime.UtcNow + HangGuard;
        while (await service.Timers.NextTimerDelayAsync() != delay)
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("The confirmation deadline was not armed.");
            await Task.Yield();
        }
    }

    private static async Task SettleAsync(ClockedCanBusService service)
    {
        await service.Timers.PostAsync(() => { });
        await service.Timers.PostAsync(() => { });
    }
}
