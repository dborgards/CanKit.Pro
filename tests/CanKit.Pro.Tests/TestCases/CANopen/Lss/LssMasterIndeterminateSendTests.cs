using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Lss;
using CanKit.Pro.RawCan;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Lss;

public class LssMasterIndeterminateSendTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(0, 3)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(1, 3)]
    [InlineData(2, 0)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    [InlineData(2, 3)]
    [InlineData(3, 0)]
    [InlineData(3, 1)]
    [InlineData(3, 2)]
    [InlineData(3, 3)]
    public async Task An_Indeterminate_Send_Rejects_Queued_And_Future_Commands_Even_After_A_Late_Transmission(
        int operation, int failure)
    {
        var window = TimeSpan.FromSeconds(100);
        using var rig = new ClockedLssRig(new LssMasterOptions
        {
            ResponseTimeout = window,
            FastscanTimeout = window,
            FastscanCycleTime = TimeSpan.FromSeconds(200),
        });
        using var caller = new CancellationTokenSource();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var send = rig.Service.Send;
        rig.Service.Send = async _ =>
        {
            entered.TrySetResult(true);
            await release.Task;
            if (failure == 1)
            {
                caller.Cancel();
                caller.Token.ThrowIfCancellationRequested();
            }
            if (failure == 2) throw new InvalidOperationException("driver failed after handoff");
            return new TxConfirmation
            {
                Confirmed = false,
                FailureReason = failure == 0 ? TxConfirmFailureReason.Timeout : TxConfirmFailureReason.BusOff,
            };
        };
        Task initial = operation switch
        {
            0 => rig.Master.SwitchStateGlobalAsync(LssState.Waiting, caller.Token),
            1 => rig.Master.ActivateBitTimingAsync(ushort.MaxValue, cancellationToken: caller.Token),
            2 => rig.Master.InquireNodeIdAsync(caller.Token),
            _ => rig.Master.FastscanAsync(cancellationToken: caller.Token),
        };
        await entered.Task.WaitAsync(LssTestTiming.SchedulingBudget);
        var queued = rig.Master.InquireNodeIdAsync();
        queued.IsCompleted.Should().BeFalse("the initial send still holds the command gate");
        release.TrySetResult(true);
        var first = async () => await initial.WaitAsync(LssTestTiming.SchedulingBudget);
        if (failure == 1) await first.Should().ThrowAsync<OperationCanceledException>();
        else if (failure == 2) await first.Should().ThrowAsync<InvalidOperationException>();
        else await first.Should().ThrowAsync<CanOpenTransportException>();
        rig.Service.Send = send;

        var pending = async () => await queued.WaitAsync(LssTestTiming.SchedulingBudget);
        await pending.Should().ThrowAsync<CanOpenTransportException>().WithMessage("*indeterminate transmission*");
        rig.Service.Commands.Count.Should().Be(1);

        // The driver can finally send the abandoned frame beyond every proposed recovery
        // window. Its response must never overlap a replacement request from this master.
        await rig.Clock.AdvanceAsync(TimeSpan.FromDays(1));
        var oldReply = LssFrames.Command(LssCommand.InquireNodeId);
        oldReply[1] = 0x7F;
        await rig.InjectAsync(oldReply).WaitAsync(LssTestTiming.SchedulingBudget);
        var retry = async () => await rig.Master.InquireNodeIdAsync().WaitAsync(LssTestTiming.SchedulingBudget);
        await retry.Should().ThrowAsync<CanOpenTransportException>().WithMessage("*indeterminate transmission*");
        var global = async () => await rig.Master.SwitchStateGlobalAsync(LssState.Waiting)
            .WaitAsync(LssTestTiming.SchedulingBudget);
        await global.Should().ThrowAsync<CanOpenTransportException>().WithMessage("*indeterminate transmission*");
        rig.Service.Commands.Count.Should().Be(1);
    }
}
