using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Lss;
using CanKit.Pro.RawCan;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Lss;

public class LssMasterUnsentTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task An_Unsent_Request_Does_Not_Reserve_A_Response_Or_Silence_Window(int operation, bool cancel)
    {
        var window = TimeSpan.FromSeconds(100);
        using var rig = new ClockedLssRig(new LssMasterOptions
        {
            ResponseTimeout = window,
            FastscanTimeout = window,
            FastscanCycleTime = TimeSpan.FromSeconds(200),
        });
        await rig.Service.Subscription.Started.Task.WaitAsync(LssTestTiming.SchedulingBudget);
        using var caller = new CancellationTokenSource();
        var send = rig.Service.Send;
        rig.Service.Send = _ => Task.FromResult(new TxConfirmation
        {
            Confirmed = cancel,
            FailureReason = cancel ? TxConfirmFailureReason.None : TxConfirmFailureReason.Rejected,
        });
        if (cancel)
            rig.Service.Subscription.BeforeRead = () =>
            {
                rig.Service.Subscription.BeforeRead = null;
                caller.Cancel();
            };
        var request = async () =>
        {
            Task pending = operation switch
            {
                0 => rig.Master.ActivateBitTimingAsync(ushort.MaxValue, cancellationToken: caller.Token),
                1 => rig.Master.InquireNodeIdAsync(caller.Token),
                _ => rig.Master.FastscanAsync(cancellationToken: caller.Token),
            };
            await pending.WaitAsync(LssTestTiming.SchedulingBudget);
        };
        if (cancel) await request.Should().ThrowAsync<OperationCanceledException>();
        else await request.Should().ThrowAsync<CanOpenTransportException>();
        int attempts = cancel ? 0 : 1;
        rig.Service.Commands.Count.Should().Be(attempts);
        rig.Service.Send = send;

        await rig.Master.SwitchStateGlobalAsync(LssState.Waiting).WaitAsync(LssTestTiming.SchedulingBudget);
        rig.Service.Commands.Count.Should().Be(attempts + 1);
        rig.Clock.Elapsed.Should().Be(TimeSpan.FromSeconds(1), "no clock advance is needed for an unsent request");
        if (operation == 2)
        {
            using var nextCaller = new CancellationTokenSource();
            var next = rig.Master.FastscanAsync(cancellationToken: nextCaller.Token);
            rig.Service.Commands.Count.Should().Be(attempts + 2, "a rejected probe must not reserve its cycle gap");
            await rig.ArmedAsync(window);
            nextCaller.Cancel();
            var stop = async () => await next.WaitAsync(LssTestTiming.SchedulingBudget);
            await stop.Should().ThrowAsync<OperationCanceledException>();
        }
    }
}
