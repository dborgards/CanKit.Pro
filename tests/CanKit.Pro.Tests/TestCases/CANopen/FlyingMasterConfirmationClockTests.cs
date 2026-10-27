using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;
using static CanKit.Pro.Tests.TestCases.CANopen.FlyingMasterRig;

namespace CanKit.Pro.Tests.TestCases.CANopen;

public class FlyingMasterConfirmationClockTests : IClassFixture<VirtualAdapterFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Clock_Steps_Wait_For_A_Real_Echo_Even_When_The_Send_Starts_After_Settling(bool afterSettling)
    {
        using var bus = ControllableBus.EchoCapable(VirtualAdapterFixture.NewSession("flying-confirm"));
        using var peer = ControllableBus.Plain(VirtualAdapterFixture.NewSession("flying-confirm-peer"));
        var clock = new ManualTimeSource();
        using var node = OpenClockedNode(bus, LeftId, clock, out var confirmations);
        peer.OnTransmitting = frame => bus.RaiseObserved(frame, isEcho: false);
        var witness = new ActorWitness(node, peer, WitnessForLeft, confirmations);
        await QuiesceAsync(witness, null);
        bus.EchoMode = EchoDelivery.Deferred;
        bool registeredUnderClockGate = false;
        bus.OnTransmitting = frame =>
        {
            if (frame.ID == 0x123) registeredUnderClockGate = Monitor.IsEntered(clock);
        };
        Task<TxConfirmation>? send = null;
        async Task StartSendAsync()
        {
            send = confirmations.SendConfirmedAsync(CanFrame.Classic(0x123, new byte[] { 1 }));
            await bus.DeferredEchoes.WaitForEnqueuedAsync(1, ShortTimeout);
            var until = DateTime.UtcNow + ShortTimeout;
            while (await confirmations.Timers.NextTimerDelayAsync() != TimeSpan.FromSeconds(1))
            {
                if (DateTime.UtcNow > until) throw new TimeoutException("The echo deadline was not armed.");
                await Task.Yield();
            }
        }

        if (!afterSettling) await StartSendAsync();
        var waiting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        witness.ConfirmationWaitObserved = () => waiting.TrySetResult(true);
        var advance = AdvanceAsync(clock, witness, null, TimeSpan.FromSeconds(2),
            afterSettling ? StartSendAsync : null);
        try
        {
            await Task.WhenAny(waiting.Task, advance).WaitAsync(ShortTimeout);
            clock.GetTimestamp().Should().Be(0, "the requested two-second step must wait for the outstanding echo");
            send!.IsCompleted.Should().BeFalse("driver acceptance alone does not confirm an echo-capable send");
            registeredUnderClockGate.Should().BeTrue("send registration and clock steps share the same gate");
        }
        finally
        {
            bus.DeferredEchoes.ReleaseNext();
            await advance.WaitAsync(ShortTimeout);
        }

        var result = await send!.WaitAsync(ShortTimeout);
        result.Confirmed.Should().BeTrue();
        result.IsApproximated.Should().BeFalse();
        clock.GetTimestamp().Should().Be(TimeSpan.FromSeconds(2).Ticks);
        confirmations.PendingConfirmations.Should().Be(0);
        (await confirmations.Timers.NextTimerDelayAsync()).Should().BeNull();
    }
}
