using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Pro.CANopen;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;
using static CanKit.Pro.Tests.TestCases.CANopen.FlyingMasterRig;

namespace CanKit.Pro.Tests.TestCases.CANopen;

public partial class CanOpenBootIdentityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelled_Boot_Guarding_Cannot_Send_A_Queued_Poll(bool replace)
    {
        string session = NewSession();
        using var bus = Open(session, 0);
        using var peer = Open(session, 1);
        var clock = new ManualTimeSource();
        using var confirmations = new ClockedCanBusService(bus, clock, sendGate: clock);
        using var gate = new GuardPollGate(confirmations);
        using var node = new CanOpenNode(gate, LeftId, ErrorControlOptions(), ownsService: false, timeSource: clock);
        using var rig = new MasterRig(clock, node, new ActorWitness(node, peer, WitnessForLeft, confirmations),
            new FrameLog(bus, peer), peer, bus);
        using var probe = new BootGuardProbe(peer);
        int errors = 0;
        node.BackgroundExceptionOccurred += (_, _) => Interlocked.Increment(ref errors);
        ConfigureErrorControl(rig, heartbeatMs: 0, guardMs: 20, retries: 1);
        await ElectActive(rig);
        TransmitHeartbeat(peer, Slave, 0);
        await gate.Entered.Task.WaitAsync(ShortTimeout);
        try
        {
            if (replace) node.StartNodeGuardingConsumer(Slave, TimeSpan.FromMilliseconds(40), 1);
            else node.StopFlyingMaster();
            await gate.Cancelled.Task.WaitAsync(ShortTimeout);
        }
        finally { gate.Release.TrySetResult(true); }
        await QuiesceAsync(rig.Witness, null);
        if (replace) await probe.FirstPoll.Task.WaitAsync(ShortTimeout);
        probe.Requests.Should().Be(replace ? 1 : 0);
        Volatile.Read(ref errors).Should().Be(0);
    }

    private sealed class GuardPollGate(ClockedCanBusService inner) : ICanBusService
    {
        private int _held;
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ICanBus Bus => inner.Bus;
        public int SubscriptionCount => inner.SubscriptionCount;
        public event EventHandler<Exception>? BackgroundExceptionOccurred
        {
            add => inner.BackgroundExceptionOccurred += value;
            remove => inner.BackgroundExceptionOccurred -= value;
        }
        public ISubscription Subscribe(Func<CanFrameEvent, bool>? predicate = null, int? bufferCapacity = null, bool includeEcho = false)
            => inner.Subscribe(predicate, bufferCapacity, includeEcho);
        public ISubscription Subscribe(CanIdFilter filter, int? bufferCapacity = null, bool includeEcho = false)
            => inner.Subscribe(filter, bufferCapacity, includeEcho);
        public IReadOnlyList<FilterOverlap> FindOverlappingFilterSubscriptions() => inner.FindOverlappingFilterSubscriptions();
        public async Task<TxConfirmation> SendConfirmedAsync(CanFrame frame, TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            if (frame.IsRemoteFrame && frame.ID == 0x700 + Slave && Interlocked.Exchange(ref _held, 1) == 0)
            {
                using (cancellationToken.Register(() => Cancelled.TrySetResult(true)))
                {
                    Entered.TrySetResult(true);
                    await Release.Task.ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            return await inner.SendConfirmedAsync(frame, timeout, cancellationToken).ConfigureAwait(false);
        }
        public void Dispose() => Release.TrySetResult(true);
    }
}
