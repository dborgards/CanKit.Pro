using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Emcy;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

/// <summary>
/// #170: <see cref="ICanOpenNode.HeartbeatTimeout"/>, <see cref="ICanOpenNode.NodeGuardingTimeout"/>
/// and <see cref="ICanOpenNode.EmcyReceived"/> used to share the drop-oldest event queue with
/// SYNC, heartbeats and PDOs. A subscriber stuck in one handler, and a burst of ordinary events
/// long enough to roll the queue, discarded a timeout that was already waiting. The peer then
/// looked alive. Those three stay in the same ordered queue and are no longer eligible to be
/// the event that is dropped. Ordinary events still are (FR-CO-008 / FR-CO-009 / FR-CO-011).
/// </summary>
public class CanOpenCriticalEventQueueTests : IClassFixture<VirtualAdapterFixture>
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan GuardWindow = TimeSpan.FromSeconds(5);

    private const byte NodeId = 0x10;
    private const byte HeartbeatProducer = 0x22;
    private const byte GuardedNode = 0x33;
    private const byte EmcyProducer = 0x44;
    private const int Capacity = 2;

    [Fact]
    public async Task Timeout_And_Emcy_Are_Not_Dropped_When_Sync_Saturates_The_Queue()
    {
        using var bus = ControllableBus.EchoCapable(VirtualAdapterFixture.NewSession("canopen-evt"));
        var clock = new ManualTimeSource();
        var options = new CanOpenNodeOptions { EventQueueCapacity = Capacity };
        using var node = new CanOpenNode(new CanBusService(bus), NodeId, options, ownsService: true, clock);

        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeats = new List<HeartbeatTimeoutEventArgs>();
        var guardings = new List<NodeGuardingTimeoutEventArgs>();
        var emergencies = new List<EmcyReceivedEventArgs>();
        int syncs = 0;

        node.SyncReceived += (_, _) =>
        {
            if (Interlocked.Increment(ref syncs) == 1)
            {
                entered.TrySetResult(true);
                release.Task.GetAwaiter().GetResult();
            }
        };
        node.HeartbeatTimeout += (_, e) => { lock (heartbeats) heartbeats.Add(e); };
        node.NodeGuardingTimeout += (_, e) => { lock (guardings) guardings.Add(e); };
        node.EmcyReceived += (_, e) => { lock (emergencies) emergencies.Add(e); };

        try
        {
            Settle(node);
            node.State.Should().Be(NmtState.PreOperational);
            node.AddHeartbeatConsumer(HeartbeatProducer, GuardWindow);
            node.StartNodeGuardingConsumer(GuardedNode, GuardWindow, lifeTimeFactor: 1);
            Settle(node);

            // One SYNC is dequeued and stuck in the handler, so the pump cannot drain what follows.
            RaiseSync(bus);
            await entered.Task.WithTimeoutAsync(ShortTimeout);

            long submitted = node.SubmittedEventCount;
            for (int i = 0; i < Capacity; i++) RaiseSync(bus);
            await WaitForSubmittedAsync(node, submitted + Capacity);
            node.SubmittedEventCount.Should().Be(submitted + Capacity, "the queue is full of SYNC and the pump is still inside the first one");

            // Both deadlines were armed at the same frozen time, so one step fires each once.
            // The guarding poll fires too; it only transmits an RTR and does not enqueue.
            submitted = node.SubmittedEventCount;
            Advance(clock, node, GuardWindow);
            node.SubmittedEventCount.Should().Be(submitted + 2);

            var emcy = new EmcyMessage(EmcyProducer, errorCode: 0x8110, errorRegister: 0x11);
            submitted = node.SubmittedEventCount;
            bus.RaiseObserved(
                CanFrame.Classic(unchecked((int)CanOpenCobId.Emcy(EmcyProducer)), emcy.Encode()),
                isEcho: false);
            await WaitForSubmittedAsync(node, submitted + 1);
            node.SubmittedEventCount.Should().Be(submitted + 1);

            // capacity + 3 further SYNCs is enough, under drop-oldest, to push the two queued
            // SYNCs and then the three critical events out of a queue of this capacity. The
            // critical ones have to still be sitting there when the burst has been accepted.
            int evictingBurst = Capacity + 3;
            submitted = node.SubmittedEventCount;
            for (int i = 0; i < evictingBurst; i++) RaiseSync(bus);
            await WaitForSubmittedAsync(node, submitted + evictingBurst);
            node.SubmittedEventCount.Should().Be(submitted + evictingBurst);
            node.QueuedEventCount.Should().Be(Capacity + 3,
                "the two newest SYNCs remain and the timeout, node-guarding timeout and EMCY stay ahead of them");

            release.TrySetResult(true);

            int expectedSyncs = 1 + Capacity;
            var deadline = DateTime.UtcNow + ShortTimeout;
            while (true)
            {
                int queued = node.QueuedEventCount;
                int seen = Volatile.Read(ref syncs);
                if (queued == 0 && seen == expectedSyncs) break;
                if (queued == 0 && seen > expectedSyncs)
                    throw new InvalidOperationException(
                        $"SYNC delivery kept going to {seen}; the ordinary queue is no longer bounded by {Capacity}.");
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException($"Queue still holds {queued} event(s); SYNC handlers ran {seen} time(s).");
                await Task.Delay(1);
            }

            Volatile.Read(ref syncs).Should().Be(expectedSyncs);
            lock (heartbeats)
            {
                heartbeats.Should().ContainSingle();
                heartbeats[0].ProducerNodeId.Should().Be(HeartbeatProducer);
                heartbeats[0].Timeout.Should().Be(GuardWindow);
            }
            lock (guardings)
            {
                guardings.Should().ContainSingle();
                guardings[0].ProducerNodeId.Should().Be(GuardedNode);
                guardings[0].GuardTime.Should().Be(GuardWindow);
                guardings[0].LifeTimeFactor.Should().Be(1);
            }
            lock (emergencies)
            {
                emergencies.Should().ContainSingle();
                emergencies[0].Message.ProducerNodeId.Should().Be(EmcyProducer);
                emergencies[0].Message.ErrorCode.Should().Be(0x8110);
                emergencies[0].Message.ErrorRegister.Should().Be(0x11);
            }
        }
        finally
        {
            release.TrySetResult(true);
        }
    }

    [Fact]
    public void Constructor_Stops_The_Event_Pump_When_Subscribe_Fails()
    {
        using var bus = ControllableBus.EchoCapable(VirtualAdapterFixture.NewSession("canopen-evt-ctor"));
        var service = new CanBusService(bus);
        service.Dispose();

        var act = () => new CanOpenNode(service, NodeId, new CanOpenNodeOptions(), ownsService: false);
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Event_Submitted_After_Dispose_Is_Not_Delivered()
    {
        using var bus = ControllableBus.EchoCapable(VirtualAdapterFixture.NewSession("canopen-evt-disposed"));
        var node = new CanOpenNode(new CanBusService(bus), NodeId, new CanOpenNodeOptions(), ownsService: true);
        using (node)
        {
            Settle(node);
        }

        var ran = 0;
        var submitted = node.SubmittedEventCount;
        node.SubmitEventForTests(() => ran++);

        ran.Should().Be(0);
        node.SubmittedEventCount.Should().Be(submitted);
        node.QueuedEventCount.Should().Be(0);
    }

    private static void RaiseSync(ControllableBus bus)
        => bus.RaiseObserved(
            CanFrame.Classic(unchecked((int)CanOpenCobId.Sync), ReadOnlyMemory<byte>.Empty),
            isEcho: false);

    /// <summary>Two actor round-trips: the loop drains the mailbox it already had, fires due
    /// timers, then takes the next post. One trip can return while those timers are still running.</summary>
    private static void Settle(CanOpenNode node)
    {
        node.PostToActorAsync(() => { }).GetAwaiter().GetResult();
        node.PostToActorAsync(() => { }).GetAwaiter().GetResult();
    }

    private static void Advance(ManualTimeSource clock, CanOpenNode node, TimeSpan by)
    {
        Settle(node);
        clock.Advance(by);
        Settle(node);
    }

    private static async Task WaitForSubmittedAsync(CanOpenNode node, long atLeast)
    {
        var deadline = DateTime.UtcNow + ShortTimeout;
        while (node.SubmittedEventCount < atLeast)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(
                    $"Dispatch queue accepted {node.SubmittedEventCount} event(s); the test wanted {atLeast}.");
            await Task.Delay(1);
        }
    }
}
