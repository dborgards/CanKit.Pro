using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Emcy;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
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

    // #201: the critical events are not dropped, but they are not unbounded either. Every
    // timeout kind re-arms on expiry, so one silent peer and a stuck handler used to grow the
    // queue by one event per period for as long as the handler stayed stuck.
    [Fact]
    public async Task Repeated_Timeouts_For_One_Producer_Are_Folded_Into_The_One_Already_Waiting()
    {
        using var bus = ControllableBus.EchoCapable(VirtualAdapterFixture.NewSession("canopen-evt-fold"));
        var clock = new ManualTimeSource();
        using var node = new CanOpenNode(new CanBusService(bus), NodeId, new CanOpenNodeOptions(), ownsService: true, clock);

        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeats = new List<HeartbeatTimeoutEventArgs>();
        var guardings = new List<NodeGuardingTimeoutEventArgs>();
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

        try
        {
            Settle(node);
            node.AddHeartbeatConsumer(HeartbeatProducer, GuardWindow);
            node.StartNodeGuardingConsumer(GuardedNode, GuardWindow, lifeTimeFactor: 1);
            Settle(node);
            RaiseSync(bus);
            await entered.Task.WithTimeoutAsync(ShortTimeout);

            const int Expiries = 4;
            for (int i = 0; i < Expiries; i++) Advance(clock, node, GuardWindow);

            node.QueuedEventCount.Should().Be(2, "one heartbeat timeout and one node-guarding timeout wait, however often they expired");
            node.CoalescedEventCount.Should().Be(2 * (Expiries - 1));

            release.TrySetResult(true);
            // An empty queue only says the last event was taken, not that its handler has run.
            await WaitUntilAsync(() => { lock (heartbeats) lock (guardings) return heartbeats.Count == 1 && guardings.Count == 1; });
            lock (heartbeats) heartbeats.Should().ContainSingle();
            lock (guardings) guardings.Should().ContainSingle();

            // Once delivered, the next expiry is a new event: the handler has not seen it.
            Advance(clock, node, GuardWindow);
            await WaitUntilAsync(() => { lock (heartbeats) return heartbeats.Count == 2; });
        }
        finally
        {
            release.TrySetResult(true);
        }
    }

    [Fact]
    public async Task An_Emcy_Identical_To_One_Already_Waiting_Is_Folded_Into_It()
    {
        using var bus = ControllableBus.EchoCapable(VirtualAdapterFixture.NewSession("canopen-evt-emcy-fold"));
        using var node = new CanOpenNode(new CanBusService(bus), NodeId, new CanOpenNodeOptions(), ownsService: true);

        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
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
        node.EmcyReceived += (_, e) => { lock (emergencies) emergencies.Add(e); };

        try
        {
            Settle(node);
            RaiseSync(bus);
            await entered.Task.WithTimeoutAsync(ShortTimeout);

            RaiseEmcy(bus, EmcyProducer, 0x8110, 0x11, manufacturer: 1);
            for (int i = 0; i < 4; i++) RaiseEmcy(bus, EmcyProducer, 0x8110, 0x11, manufacturer: 1);
            RaiseEmcy(bus, EmcyProducer, 0x8110, 0x11, manufacturer: 2);   // differs in the manufacturer field
            RaiseEmcy(bus, EmcyProducer, 0x8110, 0x12, manufacturer: 1);   // differs in the register
            RaiseEmcy(bus, EmcyProducer, 0x8111, 0x11, manufacturer: 1);   // differs in the code
            RaiseEmcy(bus, EmcyProducer + 1, 0x8110, 0x11, manufacturer: 1); // differs in the producer
            await WaitUntilAsync(() => node.CoalescedEventCount == 4 && node.QueuedEventCount == 5);

            release.TrySetResult(true);
            await WaitUntilAsync(() => node.QueuedEventCount == 0);
            await WaitUntilAsync(() => { lock (emergencies) return emergencies.Count == 5; });
            node.EmcyOverflowCount.Should().Be(0);
        }
        finally
        {
            release.TrySetResult(true);
        }
    }

    [Fact]
    public async Task A_Producer_Cannot_Keep_More_Than_The_Queue_Capacity_Of_Distinct_Emergencies_Waiting()
    {
        using var bus = ControllableBus.EchoCapable(VirtualAdapterFixture.NewSession("canopen-evt-emcy-cap"));
        var options = new CanOpenNodeOptions { EventQueueCapacity = Capacity };
        using var node = new CanOpenNode(new CanBusService(bus), NodeId, options, ownsService: true);

        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredSecond = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var emergencies = new List<EmcyReceivedEventArgs>();
        var background = new List<Exception>();
        int syncs = 0;
        // The first and second SYNC each hold the dispatcher, so the emergencies queued behind
        // them cannot be drained while a burst is still arriving.
        node.SyncReceived += (_, _) =>
        {
            switch (Interlocked.Increment(ref syncs))
            {
                case 1:
                    entered.TrySetResult(true);
                    release.Task.GetAwaiter().GetResult();
                    break;
                case 2:
                    enteredSecond.TrySetResult(true);
                    releaseSecond.Task.GetAwaiter().GetResult();
                    break;
            }
        };
        node.EmcyReceived += (_, e) => { lock (emergencies) emergencies.Add(e); };
        node.BackgroundExceptionOccurred += (_, ex) => { lock (background) background.Add(ex); };

        try
        {
            Settle(node);
            RaiseSync(bus);
            await entered.Task.WithTimeoutAsync(ShortTimeout);

            // Distinct payloads, so folding cannot help: this is the flood that varies its data.
            for (ushort code = 0x8000; code < 0x8000 + 5; code++)
                RaiseEmcy(bus, EmcyProducer, code, 0x01, manufacturer: 0);
            RaiseEmcy(bus, EmcyProducer + 1, 0x8000, 0x01, manufacturer: 0);   // another producer is unaffected
            await WaitUntilAsync(() => node.EmcyOverflowCount == 3 && node.QueuedEventCount == Capacity + 1);
            // The report is raised after the count moves and outside the queue's lock.
            await WaitUntilAsync(() => { lock (background) return background.Count >= 1; });
            lock (background)
            {
                background.Should().ContainSingle("the burst is reported once, not once per discarded frame")
                    .Which.Should().BeOfType<InvalidOperationException>()
                    .Which.Message.Should().Contain("0x44");
            }

            release.TrySetResult(true);
            await WaitUntilAsync(() => node.QueuedEventCount == 0);
            await WaitUntilAsync(() => { lock (emergencies) return emergencies.Count == Capacity + 1; });
            lock (emergencies)
            {
                emergencies.Where(e => e.Message.ProducerNodeId == EmcyProducer).Select(e => e.Message.ErrorCode)
                    .Should().Equal(new ushort[] { 0x8000, 0x8001 }, "the oldest ones are kept, in order");
            }

            // The backlog drained, so the next burst is a new one and is reported again. The
            // dispatcher is held again first: with a fast handler it could otherwise take the
            // first two before the third arrives, and there would be no burst to report.
            RaiseSync(bus);
            await enteredSecond.Task.WithTimeoutAsync(ShortTimeout);
            for (ushort code = 0x9000; code < 0x9000 + 3; code++)
                RaiseEmcy(bus, EmcyProducer, code, 0x01, manufacturer: 0);
            await WaitUntilAsync(() => node.EmcyOverflowCount == 4 && node.QueuedEventCount == Capacity);
            await WaitUntilAsync(() => { lock (background) return background.Count >= 2; });
            lock (background) background.Should().HaveCount(2);

            releaseSecond.TrySetResult(true);
            await WaitUntilAsync(() => { lock (emergencies) return emergencies.Count == Capacity + 1 + Capacity; });
        }
        finally
        {
            release.TrySetResult(true);
            releaseSecond.TrySetResult(true);
        }
    }

    // An EMCY is a state report. "Error, reset, error" folded into "error, reset" would leave a
    // handler believing the producer had recovered.
    [Fact]
    public async Task A_Recurring_Emcy_Is_Not_Folded_Into_An_Earlier_One_Across_A_Different_One()
    {
        using var bus = ControllableBus.EchoCapable(VirtualAdapterFixture.NewSession("canopen-evt-emcy-order"));
        using var node = new CanOpenNode(new CanBusService(bus), NodeId, new CanOpenNodeOptions(), ownsService: true);

        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseEmcy = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredEmcy = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var emergencies = new List<EmcyReceivedEventArgs>();
        int syncs = 0;
        int emcys = 0;
        node.SyncReceived += (_, _) =>
        {
            if (Interlocked.Increment(ref syncs) == 1)
            {
                entered.TrySetResult(true);
                release.Task.GetAwaiter().GetResult();
            }
        };
        node.EmcyReceived += (_, e) =>
        {
            lock (emergencies) emergencies.Add(e);
            if (Interlocked.Increment(ref emcys) == 1)
            {
                enteredEmcy.TrySetResult(true);
                releaseEmcy.Task.GetAwaiter().GetResult();
            }
        };

        try
        {
            Settle(node);
            RaiseSync(bus);
            await entered.Task.WithTimeoutAsync(ShortTimeout);

            RaiseEmcy(bus, EmcyProducer, 0x8110, 0x11, manufacturer: 0);   // error
            RaiseEmcy(bus, EmcyProducer, 0x0000, 0x00, manufacturer: 0);   // error reset
            RaiseEmcy(bus, EmcyProducer, 0x8110, 0x11, manufacturer: 0);   // the same error again
            RaiseEmcy(bus, EmcyProducer, 0x8110, 0x11, manufacturer: 0);   // ... repeated: this one folds
            await WaitUntilAsync(() => node.QueuedEventCount == 3 && node.CoalescedEventCount == 1);

            // The first error is now with the handler. The entry for the recurring one, still
            // waiting, must survive that: a further repeat folds into it.
            release.TrySetResult(true);
            await enteredEmcy.Task.WithTimeoutAsync(ShortTimeout);
            RaiseEmcy(bus, EmcyProducer, 0x8110, 0x11, manufacturer: 0);
            await WaitUntilAsync(() => node.CoalescedEventCount == 2);
            node.QueuedEventCount.Should().Be(2);

            releaseEmcy.TrySetResult(true);
            await WaitUntilAsync(() => { lock (emergencies) return emergencies.Count == 3; });
            lock (emergencies)
                emergencies.Select(e => e.Message.ErrorCode).Should().Equal(new ushort[] { 0x8110, 0x0000, 0x8110 });
        }
        finally
        {
            release.TrySetResult(true);
            releaseEmcy.TrySetResult(true);
        }
    }

    // The same holds for a timeout: "silent, alive again, silent" must not reach the handler as
    // just "silent".
    [Fact]
    public async Task A_Heartbeat_Timeout_Is_Not_Folded_Across_A_Heartbeat_From_The_Same_Producer()
    {
        using var bus = ControllableBus.EchoCapable(VirtualAdapterFixture.NewSession("canopen-evt-hb-order"));
        var clock = new ManualTimeSource();
        using var node = new CanOpenNode(new CanBusService(bus), NodeId, new CanOpenNodeOptions(), ownsService: true, clock);

        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<string>();
        int syncs = 0;
        node.SyncReceived += (_, _) =>
        {
            if (Interlocked.Increment(ref syncs) == 1)
            {
                entered.TrySetResult(true);
                release.Task.GetAwaiter().GetResult();
            }
        };
        node.HeartbeatTimeout += (_, _) => { lock (seen) seen.Add("timeout"); };
        node.HeartbeatReceived += (_, e) => { if (e.ProducerNodeId == HeartbeatProducer) lock (seen) seen.Add("alive"); };

        try
        {
            Settle(node);
            node.AddHeartbeatConsumer(HeartbeatProducer, GuardWindow);
            Settle(node);
            RaiseSync(bus);
            await entered.Task.WithTimeoutAsync(ShortTimeout);

            Advance(clock, node, GuardWindow);                     // silent
            bus.RaiseObserved(
                CanFrame.Classic(unchecked((int)(0x700u + HeartbeatProducer)), new byte[] { 0x05 }),
                isEcho: false);                                    // alive again
            await WaitUntilAsync(() => node.QueuedEventCount == 2);
            Advance(clock, node, GuardWindow);                     // silent again
            node.QueuedEventCount.Should().Be(3);
            node.CoalescedEventCount.Should().Be(0);

            release.TrySetResult(true);
            await WaitUntilAsync(() => { lock (seen) return seen.Count == 3; });
            lock (seen) seen.Should().Equal("timeout", "alive", "timeout");
        }
        finally
        {
            release.TrySetResult(true);
        }
    }

    // A heartbeat that was dropped to make room never reaches the handler, so it cannot separate
    // two timeouts: without this, a flapping peer plus a flood of ordinary events would keep every
    // timeout from folding and the critical backlog would grow again.
    [Fact]
    public async Task A_Heartbeat_That_Was_Dropped_Does_Not_Keep_Two_Timeouts_Apart()
    {
        using var bus = ControllableBus.EchoCapable(VirtualAdapterFixture.NewSession("canopen-evt-hb-dropped"));
        var clock = new ManualTimeSource();
        var options = new CanOpenNodeOptions { EventQueueCapacity = 1 };
        using var node = new CanOpenNode(new CanBusService(bus), NodeId, options, ownsService: true, clock);

        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeouts = 0;
        int syncs = 0;
        node.SyncReceived += (_, _) =>
        {
            if (Interlocked.Increment(ref syncs) == 1)
            {
                entered.TrySetResult(true);
                release.Task.GetAwaiter().GetResult();
            }
        };
        node.HeartbeatTimeout += (_, _) => Interlocked.Increment(ref timeouts);

        try
        {
            Settle(node);
            node.AddHeartbeatConsumer(HeartbeatProducer, GuardWindow);
            Settle(node);
            RaiseSync(bus);
            await entered.Task.WithTimeoutAsync(ShortTimeout);

            Advance(clock, node, GuardWindow);                     // first timeout waits
            long submitted = node.SubmittedEventCount;
            bus.RaiseObserved(
                CanFrame.Classic(unchecked((int)(0x700u + HeartbeatProducer)), new byte[] { 0x05 }),
                isEcho: false);                                    // alive: the only ordinary slot
            await WaitForSubmittedAsync(node, submitted + 1);
            RaiseSync(bus);                                        // takes that slot: the heartbeat is dropped
            await WaitForSubmittedAsync(node, submitted + 2);

            Advance(clock, node, GuardWindow);                     // silent again
            node.CoalescedEventCount.Should().Be(1, "no heartbeat is waiting any more, so nothing separates the two timeouts");
            node.QueuedEventCount.Should().Be(2, "the first timeout and the SYNC that took the heartbeat's place");

            release.TrySetResult(true);
            await WaitUntilAsync(() => Volatile.Read(ref timeouts) == 1 && node.QueuedEventCount == 0);
        }
        finally
        {
            release.TrySetResult(true);
        }
    }

    // The two timeout kinds for one node do not say anything about each other, so neither keeps
    // the other from folding: a node watched by heartbeat and by node guarding would otherwise
    // bring the unbounded backlog back.
    [Fact]
    public async Task Heartbeat_And_Guarding_Timeouts_Of_One_Node_Do_Not_Keep_Each_Other_Apart()
    {
        using var bus = ControllableBus.EchoCapable(VirtualAdapterFixture.NewSession("canopen-evt-two-kinds"));
        var clock = new ManualTimeSource();
        using var node = new CanOpenNode(new CanBusService(bus), NodeId, new CanOpenNodeOptions(), ownsService: true, clock);

        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int heartbeats = 0, guardings = 0, syncs = 0;
        node.SyncReceived += (_, _) =>
        {
            if (Interlocked.Increment(ref syncs) == 1)
            {
                entered.TrySetResult(true);
                release.Task.GetAwaiter().GetResult();
            }
        };
        node.HeartbeatTimeout += (_, _) => Interlocked.Increment(ref heartbeats);
        node.NodeGuardingTimeout += (_, _) => Interlocked.Increment(ref guardings);

        try
        {
            Settle(node);
            node.AddHeartbeatConsumer(GuardedNode, GuardWindow);
            node.StartNodeGuardingConsumer(GuardedNode, GuardWindow, lifeTimeFactor: 1);
            Settle(node);
            RaiseSync(bus);
            await entered.Task.WithTimeoutAsync(ShortTimeout);

            const int Expiries = 4;
            for (int i = 0; i < Expiries; i++) Advance(clock, node, GuardWindow);

            node.QueuedEventCount.Should().Be(2, "one timeout of each kind waits, however often they expired");
            node.CoalescedEventCount.Should().Be(2 * (Expiries - 1));

            release.TrySetResult(true);
            await WaitUntilAsync(() => Volatile.Read(ref heartbeats) == 1 && Volatile.Read(ref guardings) == 1);
        }
        finally
        {
            release.TrySetResult(true);
        }
    }

    // The dropped heartbeat was not the only thing said about the producer: an EMCY reset is
    // still waiting, so that is what an identical EMCY is measured against, not nothing.
    [Fact]
    public async Task Dropping_A_Heartbeat_Falls_Back_To_The_Newest_Event_Still_Waiting_For_The_Producer()
    {
        using var bus = ControllableBus.EchoCapable(VirtualAdapterFixture.NewSession("canopen-evt-hb-dropped-emcy"));
        var options = new CanOpenNodeOptions { EventQueueCapacity = 3 };
        using var node = new CanOpenNode(new CanBusService(bus), NodeId, options, ownsService: true);

        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var codes = new List<ushort>();
        int syncs = 0;
        node.SyncReceived += (_, _) =>
        {
            if (Interlocked.Increment(ref syncs) == 1)
            {
                entered.TrySetResult(true);
                release.Task.GetAwaiter().GetResult();
            }
        };
        node.EmcyReceived += (_, e) => { lock (codes) codes.Add(e.Message.ErrorCode); };

        try
        {
            Settle(node);
            RaiseSync(bus);
            await entered.Task.WithTimeoutAsync(ShortTimeout);

            RaiseEmcy(bus, EmcyProducer, 0x8110, 0x11, manufacturer: 0);   // error
            RaiseEmcy(bus, EmcyProducer, 0x0000, 0x00, manufacturer: 0);   // error reset
            long submitted = node.SubmittedEventCount;
            bus.RaiseObserved(
                CanFrame.Classic(unchecked((int)(0x700u + EmcyProducer)), new byte[] { 0x05 }),
                isEcho: false);                                            // alive: the oldest ordinary event
            for (int i = 0; i < 3; i++) RaiseSync(bus);                    // the third evicts the heartbeat
            await WaitForSubmittedAsync(node, submitted + 4);

            RaiseEmcy(bus, EmcyProducer, 0x8110, 0x11, manufacturer: 0);   // the error again, after the reset
            await WaitUntilAsync(() => node.QueuedEventCount == 2 + 3 + 1);
            node.CoalescedEventCount.Should().Be(0, "the reset is still waiting between the two errors");

            release.TrySetResult(true);
            await WaitUntilAsync(() => { lock (codes) return codes.Count == 3; });
            lock (codes) codes.Should().Equal(new ushort[] { 0x8110, 0x0000, 0x8110 });
        }
        finally
        {
            release.TrySetResult(true);
        }
    }

    // Dropping the heartbeat that separated two timeouts makes them adjacent, so the second is
    // folded into the first then, not only when a timeout happens to be enqueued afterwards. Without
    // that, a flapping producer and a flood of ordinary events keep growing the critical backlog.
    [Fact]
    public async Task Dropping_The_Heartbeat_Between_Two_Timeouts_Folds_Them()
    {
        using var bus = ControllableBus.EchoCapable(VirtualAdapterFixture.NewSession("canopen-evt-hb-between"));
        var clock = new ManualTimeSource();
        var options = new CanOpenNodeOptions { EventQueueCapacity = 2 };
        using var node = new CanOpenNode(new CanBusService(bus), NodeId, options, ownsService: true, clock);

        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int timeouts = 0, syncs = 0;
        node.SyncReceived += (_, _) =>
        {
            if (Interlocked.Increment(ref syncs) == 1)
            {
                entered.TrySetResult(true);
                release.Task.GetAwaiter().GetResult();
            }
        };
        node.HeartbeatTimeout += (_, _) => Interlocked.Increment(ref timeouts);

        try
        {
            Settle(node);
            node.AddHeartbeatConsumer(HeartbeatProducer, GuardWindow);
            Settle(node);
            RaiseSync(bus);
            await entered.Task.WithTimeoutAsync(ShortTimeout);

            Advance(clock, node, GuardWindow);                     // timeout 1
            long submitted = node.SubmittedEventCount;
            bus.RaiseObserved(
                CanFrame.Classic(unchecked((int)(0x700u + HeartbeatProducer)), new byte[] { 0x05 }),
                isEcho: false);                                    // alive: separates the two
            await WaitForSubmittedAsync(node, submitted + 1);
            Advance(clock, node, GuardWindow);                     // timeout 2
            node.CoalescedEventCount.Should().Be(0, "the heartbeat is still waiting between them");
            node.QueuedEventCount.Should().Be(3);

            RaiseSync(bus);                                        // second ordinary event: fits
            RaiseSync(bus);                                        // third: evicts the heartbeat
            await WaitForSubmittedAsync(node, submitted + 3);
            await WaitUntilAsync(() => node.CoalescedEventCount == 1);
            node.QueuedEventCount.Should().Be(3, "timeout 1 and the two SYNCs; timeout 2 was folded into timeout 1");

            release.TrySetResult(true);
            await WaitUntilAsync(() => Volatile.Read(ref timeouts) == 1 && node.QueuedEventCount == 0);

            // The entry the folded timeout left behind must not swallow the next one.
            Advance(clock, node, GuardWindow);
            await WaitUntilAsync(() => Volatile.Read(ref timeouts) == 2);
        }
        finally
        {
            release.TrySetResult(true);
        }
    }

    // Another heartbeat is still waiting between the two timeouts when the first one is dropped,
    // so they stay two events.
    [Fact]
    public async Task Dropping_One_Heartbeat_Keeps_Timeouts_Apart_That_Another_Still_Separates()
    {
        using var bus = ControllableBus.EchoCapable(VirtualAdapterFixture.NewSession("canopen-evt-hb-still-between"));
        var clock = new ManualTimeSource();
        var options = new CanOpenNodeOptions { EventQueueCapacity = 3 };
        using var node = new CanOpenNode(new CanBusService(bus), NodeId, options, ownsService: true, clock);

        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int timeouts = 0, syncs = 0;
        node.SyncReceived += (_, _) =>
        {
            if (Interlocked.Increment(ref syncs) == 1)
            {
                entered.TrySetResult(true);
                release.Task.GetAwaiter().GetResult();
            }
        };
        node.HeartbeatTimeout += (_, _) => Interlocked.Increment(ref timeouts);

        void Alive() => bus.RaiseObserved(
            CanFrame.Classic(unchecked((int)(0x700u + HeartbeatProducer)), new byte[] { 0x05 }),
            isEcho: false);

        try
        {
            Settle(node);
            node.AddHeartbeatConsumer(HeartbeatProducer, GuardWindow);
            Settle(node);
            RaiseSync(bus);
            await entered.Task.WithTimeoutAsync(ShortTimeout);

            Advance(clock, node, GuardWindow);                     // timeout 1
            long submitted = node.SubmittedEventCount;
            Alive();                                               // heartbeat 1
            Alive();                                               // heartbeat 2
            await WaitForSubmittedAsync(node, submitted + 2);
            Advance(clock, node, GuardWindow);                     // timeout 2
            await WaitForSubmittedAsync(node, submitted + 3);
            RaiseSync(bus);                                        // the third ordinary event: fits
            RaiseSync(bus);                                        // the fourth: evicts heartbeat 1
            await WaitForSubmittedAsync(node, submitted + 5);

            node.CoalescedEventCount.Should().Be(0, "heartbeat 2 is still waiting between the timeouts");
            node.QueuedEventCount.Should().Be(2 + 3, "two timeouts, heartbeat 2 and two SYNCs");

            release.TrySetResult(true);
            await WaitUntilAsync(() => Volatile.Read(ref timeouts) == 2 && node.QueuedEventCount == 0);
        }
        finally
        {
            release.TrySetResult(true);
        }
    }

    // A second error separated from the first by a reset stays an event of its own when a
    // heartbeat that was waiting between is dropped, and one that was folded away gives its slot
    // back to the producer.
    [Fact]
    public async Task Reconciling_After_A_Drop_Keeps_Separated_Emergencies_And_Frees_The_Slot_Of_A_Folded_One()
    {
        using var bus = ControllableBus.EchoCapable(VirtualAdapterFixture.NewSession("canopen-evt-emcy-reconcile"));
        var options = new CanOpenNodeOptions { EventQueueCapacity = 4 };
        using var node = new CanOpenNode(new CanBusService(bus), NodeId, options, ownsService: true);

        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var codes = new List<ushort>();
        int syncs = 0;
        node.SyncReceived += (_, _) =>
        {
            if (Interlocked.Increment(ref syncs) == 1)
            {
                entered.TrySetResult(true);
                release.Task.GetAwaiter().GetResult();
            }
        };
        node.EmcyReceived += (_, e) => { lock (codes) codes.Add(e.Message.ErrorCode); };

        void Alive() => bus.RaiseObserved(
            CanFrame.Classic(unchecked((int)(0x700u + EmcyProducer)), new byte[] { 0x05 }),
            isEcho: false);

        try
        {
            Settle(node);
            RaiseSync(bus);
            await entered.Task.WithTimeoutAsync(ShortTimeout);

            RaiseEmcy(bus, EmcyProducer, 0x8110, 0x11, manufacturer: 0);   // error
            RaiseEmcy(bus, EmcyProducer, 0x0000, 0x00, manufacturer: 0);   // reset
            RaiseEmcy(bus, EmcyProducer, 0x8110, 0x11, manufacturer: 0);   // error again: kept, the reset is between
            long submitted = node.SubmittedEventCount;
            Alive();                                                       // heartbeat: the oldest ordinary event
            RaiseEmcy(bus, EmcyProducer, 0x0000, 0x00, manufacturer: 0);   // ... reset again: separated by the error
            await WaitForSubmittedAsync(node, submitted + 2);
            for (int i = 0; i < 4; i++) RaiseSync(bus);                    // the fourth evicts the heartbeat
            await WaitForSubmittedAsync(node, submitted + 6);

            node.CoalescedEventCount.Should().Be(0, "the errors and resets alternate, nothing is adjacent");
            node.QueuedEventCount.Should().Be(4 + 4);

            release.TrySetResult(true);
            await WaitUntilAsync(() => { lock (codes) return codes.Count == 4; });
            lock (codes) codes.Should().Equal(new ushort[] { 0x8110, 0x0000, 0x8110, 0x0000 });
        }
        finally
        {
            release.TrySetResult(true);
        }
    }

    // Two identical emergencies that a heartbeat separated become one when that heartbeat is
    // dropped, and the producer gets the slot of the one that was folded away back.
    [Fact]
    public async Task Dropping_The_Heartbeat_Between_Two_Identical_Emergencies_Folds_Them_And_Frees_A_Slot()
    {
        using var bus = ControllableBus.EchoCapable(VirtualAdapterFixture.NewSession("canopen-evt-emcy-fold-drop"));
        var options = new CanOpenNodeOptions { EventQueueCapacity = 3 };
        using var node = new CanOpenNode(new CanBusService(bus), NodeId, options, ownsService: true);

        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var codes = new List<ushort>();
        int syncs = 0;
        node.SyncReceived += (_, _) =>
        {
            if (Interlocked.Increment(ref syncs) == 1)
            {
                entered.TrySetResult(true);
                release.Task.GetAwaiter().GetResult();
            }
        };
        node.EmcyReceived += (_, e) => { lock (codes) codes.Add(e.Message.ErrorCode); };

        try
        {
            Settle(node);
            RaiseSync(bus);
            await entered.Task.WithTimeoutAsync(ShortTimeout);

            RaiseEmcy(bus, EmcyProducer, 0x8110, 0x11, manufacturer: 0);
            long submitted = node.SubmittedEventCount;
            bus.RaiseObserved(
                CanFrame.Classic(unchecked((int)(0x700u + EmcyProducer)), new byte[] { 0x05 }),
                isEcho: false);                                            // alive: separates the two
            RaiseEmcy(bus, EmcyProducer, 0x8110, 0x11, manufacturer: 0);   // kept for now
            await WaitForSubmittedAsync(node, submitted + 2);
            node.CoalescedEventCount.Should().Be(0);

            RaiseSync(bus);
            RaiseSync(bus);
            RaiseSync(bus);                                                // evicts the heartbeat
            await WaitUntilAsync(() => node.CoalescedEventCount == 1);

            // One slot is free again: with the folded copy still counted, the third distinct
            // emergency below would already be over the capacity of three.
            RaiseEmcy(bus, EmcyProducer, 0x8111, 0x11, manufacturer: 0);
            RaiseEmcy(bus, EmcyProducer, 0x8112, 0x11, manufacturer: 0);
            await WaitUntilAsync(() => node.QueuedEventCount == 3 + 3);
            node.EmcyOverflowCount.Should().Be(0);

            release.TrySetResult(true);
            await WaitUntilAsync(() => { lock (codes) return codes.Count == 3; });
            lock (codes) codes.Should().Equal(new ushort[] { 0x8110, 0x8111, 0x8112 });
        }
        finally
        {
            release.TrySetResult(true);
        }
    }

    // A timeout raised under a reconfigured consumer reports the new settings, and it takes the
    // place of the one still waiting from the old configuration: the handler sees the newest
    // settings once, and reconfiguring again and again cannot grow the backlog.
    [Fact]
    public async Task A_Timeout_Under_A_Reconfigured_Consumer_Replaces_The_Stale_One()
    {
        using var bus = ControllableBus.EchoCapable(VirtualAdapterFixture.NewSession("canopen-evt-reconfig"));
        var clock = new ManualTimeSource();
        using var node = new CanOpenNode(new CanBusService(bus), NodeId, new CanOpenNodeOptions(), ownsService: true, clock);

        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeats = new List<TimeSpan>();
        var guardings = new List<(TimeSpan GuardTime, byte Factor)>();
        int syncs = 0;
        node.SyncReceived += (_, _) =>
        {
            if (Interlocked.Increment(ref syncs) == 1)
            {
                entered.TrySetResult(true);
                release.Task.GetAwaiter().GetResult();
            }
        };
        node.HeartbeatTimeout += (_, e) => { lock (heartbeats) heartbeats.Add(e.Timeout); };
        node.NodeGuardingTimeout += (_, e) => { lock (guardings) guardings.Add((e.GuardTime, e.LifeTimeFactor)); };

        var slow = TimeSpan.FromSeconds(7);
        try
        {
            Settle(node);
            node.AddHeartbeatConsumer(HeartbeatProducer, GuardWindow);
            node.StartNodeGuardingConsumer(GuardedNode, GuardWindow, lifeTimeFactor: 1);
            Settle(node);
            RaiseSync(bus);
            await entered.Task.WithTimeoutAsync(ShortTimeout);

            Advance(clock, node, GuardWindow);                     // both time out under the first settings
            node.QueuedEventCount.Should().Be(2);

            node.AddHeartbeatConsumer(HeartbeatProducer, slow);
            node.StartNodeGuardingConsumer(GuardedNode, GuardWindow, lifeTimeFactor: 2);
            Settle(node);
            Advance(clock, node, TimeSpan.FromSeconds(10));        // and again under the new ones
            node.CoalescedEventCount.Should().Be(2, "each takes the place of the one waiting, it does not queue behind it");

            node.StartNodeGuardingConsumer(GuardedNode, slow, lifeTimeFactor: 2);   // reconfigured once more
            Settle(node);
            Advance(clock, node, TimeSpan.FromSeconds(20));
            node.QueuedEventCount.Should().Be(2);

            release.TrySetResult(true);
            await WaitUntilAsync(() => { lock (heartbeats) lock (guardings) return heartbeats.Count == 1 && guardings.Count == 1; });
            lock (heartbeats) heartbeats.Should().Equal(slow);
            lock (guardings) guardings.Should().Equal((slow, (byte)2));
        }
        finally
        {
            release.TrySetResult(true);
        }
    }

    // The same rule when two timeouts are folded because their separator was dropped: the one that
    // remains reports the newest settings.
    [Fact]
    public async Task Folding_Timeouts_After_A_Drop_Keeps_The_Newest_Settings()
    {
        using var bus = ControllableBus.EchoCapable(VirtualAdapterFixture.NewSession("canopen-evt-reconcile-settings"));
        var clock = new ManualTimeSource();
        var options = new CanOpenNodeOptions { EventQueueCapacity = 2 };
        using var node = new CanOpenNode(new CanBusService(bus), NodeId, options, ownsService: true, clock);

        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeouts = new List<TimeSpan>();
        int syncs = 0;
        node.SyncReceived += (_, _) =>
        {
            if (Interlocked.Increment(ref syncs) == 1)
            {
                entered.TrySetResult(true);
                release.Task.GetAwaiter().GetResult();
            }
        };
        node.HeartbeatTimeout += (_, e) => { lock (timeouts) timeouts.Add(e.Timeout); };

        var slow = TimeSpan.FromSeconds(7);
        try
        {
            Settle(node);
            node.AddHeartbeatConsumer(HeartbeatProducer, GuardWindow);
            Settle(node);
            RaiseSync(bus);
            await entered.Task.WithTimeoutAsync(ShortTimeout);

            Advance(clock, node, GuardWindow);                     // timeout 1, under the first setting
            long submitted = node.SubmittedEventCount;
            bus.RaiseObserved(
                CanFrame.Classic(unchecked((int)(0x700u + HeartbeatProducer)), new byte[] { 0x05 }),
                isEcho: false);                                    // alive: separates the two
            await WaitForSubmittedAsync(node, submitted + 1);
            node.AddHeartbeatConsumer(HeartbeatProducer, slow);
            Settle(node);
            Advance(clock, node, slow);                            // timeout 2, under the new one
            node.CoalescedEventCount.Should().Be(0, "the heartbeat is still waiting between them");

            RaiseSync(bus);
            RaiseSync(bus);                                        // evicts the heartbeat
            await WaitUntilAsync(() => node.CoalescedEventCount == 1);

            release.TrySetResult(true);
            await WaitUntilAsync(() => { lock (timeouts) return timeouts.Count == 1; });
            lock (timeouts) timeouts.Should().Equal(slow);
        }
        finally
        {
            release.TrySetResult(true);
        }
    }

    [Fact]
    public async Task A_Throwing_Queued_Callback_Does_Not_Drop_A_Later_Heartbeat_Timeout()
    {
        using var bus = ControllableBus.EchoCapable(VirtualAdapterFixture.NewSession("canopen-evt-throw"));
        var clock = new ManualTimeSource();
        using var node = new CanOpenNode(new CanBusService(bus), NodeId, new CanOpenNodeOptions(), ownsService: true, clock);

        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeats = new List<HeartbeatTimeoutEventArgs>();
        Exception? background = null;

        node.SyncReceived += (_, _) =>
        {
            entered.TrySetResult(true);
            release.Task.GetAwaiter().GetResult();
        };
        node.HeartbeatTimeout += (_, e) => { lock (heartbeats) heartbeats.Add(e); };
        node.BackgroundExceptionOccurred += (_, ex) => background ??= ex;

        try
        {
            Settle(node);
            node.AddHeartbeatConsumer(HeartbeatProducer, GuardWindow);
            Settle(node);

            // Hold the pump inside the first callback so the throw and the timeout are both
            // queued behind it. The throw is a raw queued delegate, the same shape as a
            // callback that does not catch its own subscriber.
            RaiseSync(bus);
            await entered.Task.WithTimeoutAsync(ShortTimeout);
            node.SubmitEventForTests(() => throw new InvalidOperationException("subscriber failed"));
            Advance(clock, node, GuardWindow);
            release.TrySetResult(true);

            var deadline = DateTime.UtcNow + ShortTimeout;
            while (true)
            {
                int seen;
                lock (heartbeats) seen = heartbeats.Count;
                if (seen > 0 && background is not null) break;
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException(
                        $"Heartbeat timeouts delivered: {seen}; background exception: {background?.GetType().Name ?? "none"}.");
                await Task.Delay(1);
            }

            background.Should().BeOfType<InvalidOperationException>();
            lock (heartbeats)
            {
                heartbeats.Should().ContainSingle();
                heartbeats[0].ProducerNodeId.Should().Be(HeartbeatProducer);
                heartbeats[0].Timeout.Should().Be(GuardWindow);
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

    private static void RaiseEmcy(ControllableBus bus, int producer, ushort code, byte register, byte manufacturer)
    {
        var emcy = new EmcyMessage((byte)producer, code, register, new byte[] { manufacturer, 0, 0, 0, 0 });
        bus.RaiseObserved(
            CanFrame.Classic(unchecked((int)CanOpenCobId.Emcy((byte)producer)), emcy.Encode()),
            isEcho: false);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + ShortTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not reached in time.");
            await Task.Delay(1);
        }
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
