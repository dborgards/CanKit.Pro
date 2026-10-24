using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Lss;
using CanKit.Pro.RawCan;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Lss;

/// <summary>
/// <see cref="CanOpenNode.HasTransmitsAwaitingDriverForTests"/> answers from one snapshot of its
/// two counts (#428). FlyingMasterRig waits on it before it moves the clock.
/// </summary>
public class TransmitAwaitingDriverSeamTests
{
    private const uint HeldByDriver = 0x181;
    private const uint OnItsWay = 0x182;

    [Fact]
    public async Task A_send_completing_between_the_two_reads_does_not_hide_one_still_on_its_way()
    {
        var heldEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Completed from the interleaving thread below; its continuations -- the release of the
        // send -- run inline there, which is what lets that thread block on the node's gate.
        var held = new TaskCompletionSource<TxConfirmation>();
        var onItsWayEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseOnItsWay = new ManualResetEventSlim();
        using var service = new LssTestService
        {
            Send = frame =>
            {
                if ((uint)frame.ID == HeldByDriver)
                {
                    heldEntered.TrySetResult(true);
                    return held.Task; // returned from the driver call, confirmation pending
                }
                if ((uint)frame.ID == OnItsWay)
                {
                    onItsWayEntered.TrySetResult(true);
                    releaseOnItsWay.Wait(); // the driver call has not returned
                }
                return Task.FromResult(new TxConfirmation { Confirmed = true });
            },
        };
        using var node = new CanOpenNode(service, 5,
            new CanOpenNodeOptions { Lss = new LssSlaveOptions() }, ownsService: false);
        Task heartbeat = Task.CompletedTask;
        await node.PostToActorAsync(() => heartbeat = node.HeartbeatSendChain);
        await heartbeat.WaitAsync(LssTestTiming.SchedulingBudget);

        var first = node.SendControlFrame(HeldByDriver, new byte[] { 1 });
        Task<bool> second = Task.FromResult(true);
        try
        {
            await heldEntered.Task.WaitAsync(LssTestTiming.SchedulingBudget);
            // Counted as handed to the driver once its reservation no longer reads as awaiting it.
            await UntilAsync(() => !node.HasTransmitsAwaitingDriverForTests);

            second = node.SendControlFrame(OnItsWay, new byte[] { 2 });
            await onItsWayEntered.Task.WaitAsync(LssTestTiming.SchedulingBudget);
            node.HasTransmitsAwaitingDriverForTests.Should().BeTrue("the second send is inside the driver call");

            Thread? completer = null;
            using var completed = new ManualResetEventSlim();
            node.BetweenAwaitingDriverReadsForTests = () =>
            {
                node.BetweenAwaitingDriverReadsForTests = null;
                completer = new Thread(() =>
                {
                    held.SetResult(new TxConfirmation { Confirmed = true });
                    completed.Set();
                })
                { IsBackground = true };
                completer.Start();
                // Until the first send has completed and released its reservation, or until the
                // thread completing it is blocked -- on the node's gate, the only lock on that path.
                var limit = DateTime.UtcNow + LssTestTiming.SchedulingBudget;
                while (!completed.IsSet && (completer.ThreadState & ThreadState.WaitSleepJoin) == 0)
                {
                    if (DateTime.UtcNow > limit) throw new TimeoutException("The first send neither completed nor blocked.");
                    Thread.Yield();
                }
            };

            bool awaiting = node.HasTransmitsAwaitingDriverForTests;

            completer.Should().NotBeNull("the seam runs its hook between the two reads");
            completer!.Join(LssTestTiming.SchedulingBudget).Should().BeTrue();
            (await first.WaitAsync(LssTestTiming.SchedulingBudget)).Should().BeTrue();
            awaiting.Should().BeTrue("the second send was inside the driver call before, during and after the read");
            node.HasTransmitsAwaitingDriverForTests.Should().BeTrue();
        }
        finally
        {
            node.BetweenAwaitingDriverReadsForTests = null;
            held.TrySetResult(new TxConfirmation { Confirmed = true });
            releaseOnItsWay.Set();
        }

        (await second.WaitAsync(LssTestTiming.SchedulingBudget)).Should().BeTrue();
        await UntilAsync(() => !node.HasTransmitsAwaitingDriverForTests);
    }

    // The budget bounds thread-pool hand-offs; it decides nothing the test asserts.
    private static async Task UntilAsync(Func<bool> condition)
    {
        var limit = DateTime.UtcNow + LssTestTiming.SchedulingBudget;
        while (!condition())
        {
            if (DateTime.UtcNow > limit) throw new TimeoutException("The node did not reach the expected state.");
            await Task.Yield();
        }
    }
}
