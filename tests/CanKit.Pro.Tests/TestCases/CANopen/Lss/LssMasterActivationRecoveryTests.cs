using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Lss;
using CanKit.Pro.RawCan;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Lss;

public class LssMasterActivationRecoveryTests
{
    [Theory]
    [InlineData(50, false)]
    [InlineData(100, false)]
    [InlineData(150, false)]
    [InlineData(50, true)]
    [InlineData(100, true)]
    [InlineData(150, true)]
    public async Task Cancelling_Activation_Or_Its_Successor_Preserves_The_Silent_Period(
        int cancelAt, bool cancelSuccessor)
    {
        using var rig = new ClockedLssRig(new LssMasterOptions
        {
            BitRateSwitch = (_, _) => Task.CompletedTask,
        });
        using var caller = new CancellationTokenSource();
        var activating = rig.Master.ActivateBitTimingAsync(100, 250_000, cancellationToken: caller.Token);
        await rig.ArmedAsync(TimeSpan.FromMilliseconds(100));
        if (cancelAt >= 100)
        {
            await rig.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(100));
            await rig.ArmedAsync(TimeSpan.FromMilliseconds(100));
        }
        if (cancelAt != 100)
            await rig.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(cancelAt < 100 ? cancelAt : cancelAt - 100));
        caller.Cancel();
        var activate = async () => await activating.WaitAsync(LssTestTiming.SchedulingBudget);
        await activate.Should().ThrowAsync<OperationCanceledException>();
        var remaining = TimeSpan.FromMilliseconds(200 - cancelAt);

        if (cancelSuccessor)
        {
            using var successor = new CancellationTokenSource();
            var pending = rig.Master.SwitchStateGlobalAsync(LssState.Waiting, successor.Token);
            rig.Service.Commands.Count.Should().Be(1);
            await rig.ArmedAsync(remaining);
            successor.Cancel();
            var cancel = async () => await pending.WaitAsync(LssTestTiming.SchedulingBudget);
            await cancel.Should().ThrowAsync<OperationCanceledException>();
        }

        await CheckNextCommandAsync(rig, remaining);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(150)]
    public async Task A_Failing_Bit_Rate_Callback_Does_Not_Release_The_Silent_Period(int latency)
    {
        var failure = new InvalidOperationException("retune failed");
        using var rig = new ClockedLssRig(new LssMasterOptions
        {
            BitRateSwitch = (_, _) => throw failure,
        });
        var activating = rig.Master.ActivateBitTimingAsync(100, 250_000);
        await rig.ArmedAsync(TimeSpan.FromMilliseconds(100));
        // Deliver the first deadline late, then fail the callback at that clock reading.
        await rig.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(100 + latency));
        var activate = async () => await activating.WaitAsync(LssTestTiming.SchedulingBudget);
        (await activate.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);

        await CheckNextCommandAsync(rig, TimeSpan.FromMilliseconds(Math.Max(0, 100 - latency)));
    }

    private static async Task CheckNextCommandAsync(ClockedLssRig rig, TimeSpan remaining)
    {
        int sent = rig.Service.Commands.Count;
        var following = rig.Master.SwitchStateGlobalAsync(LssState.Waiting);
        if (remaining > TimeSpan.Zero)
        {
            rig.Service.Commands.Count.Should().Be(sent, "a failed activation must retain the silent period");
            await rig.ArmedAsync(remaining);
            await rig.Clock.AdvanceAsync(remaining - TimeSpan.FromTicks(1));
            rig.Service.Commands.Count.Should().Be(sent);
            await rig.Clock.AdvanceAsync(TimeSpan.FromTicks(1));
        }
        await following.WaitAsync(LssTestTiming.SchedulingBudget);
        rig.Service.Commands.Count.Should().Be(sent + 1);
    }
}
