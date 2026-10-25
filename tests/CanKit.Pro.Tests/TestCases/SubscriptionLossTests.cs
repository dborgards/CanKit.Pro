using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases;

/// <summary>Subscription-local capture continuity (FR-RAW-011, FR-RAW-016; #402).</summary>
public class SubscriptionLossTests : IClassFixture<VirtualAdapterFixture>
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(30);

    private static string Session() => $"loss-{Guid.NewGuid():N}";
    private static CanFrame Frame(int id) => CanFrame.Classic(id, new[] { (byte)id });

    private static List<SubscriptionFrame> Drain(ILossAwareSubscription subscription)
    {
        var frames = new List<SubscriptionFrame>();
        while (subscription.TryReadWithSequence(out var frame)) frames.Add(frame);
        return frames;
    }

    [Fact]
    public void Overflow_Counts_Exactly_The_Evicted_Frames_And_Orders_The_Gap_Before_The_Retained_Ones()
    {
        using var bus = ControllableBus.EchoCapable(Session());
        using var service = new CanBusService(bus);
        using var subscription = service.Subscribe(bufferCapacity: 2);
        var loss = (ILossAwareSubscription)subscription;

        for (var id = 1; id <= 5; id++) bus.RaiseObserved(Frame(id), isEcho: false);

        loss.DroppedFrameCount.Should().Be(3);
        var retained = Drain(loss);
        retained.Select(f => f.SequenceNumber).Should().Equal(4UL, 5UL);
        retained.Select(f => f.FrameEvent.Frame.ID).Should().Equal(4, 5);
        subscription.TryRead(out _).Should().BeFalse();
        loss.TryReadWithSequence(out var empty).Should().BeFalse();
        empty.SequenceNumber.Should().Be(0);
    }

    [Fact]
    public void A_Slow_Subscription_Does_Not_Lose_Or_Count_The_Fast_Subscriptions_Frames()
    {
        using var bus = ControllableBus.EchoCapable(Session());
        using var service = new CanBusService(bus);
        using var slow = service.Subscribe(bufferCapacity: 1);
        using var fast = service.Subscribe(bufferCapacity: 1);
        var slowLoss = (ILossAwareSubscription)slow;
        var fastLoss = (ILossAwareSubscription)fast;

        for (var id = 1; id <= 20; id++)
        {
            bus.RaiseObserved(Frame(id), isEcho: false);
            fast.TryRead(out var frame).Should().BeTrue();
            frame.Frame.ID.Should().Be(id);
        }

        slowLoss.DroppedFrameCount.Should().Be(19);
        fastLoss.DroppedFrameCount.Should().Be(0);
        Drain(slowLoss).Single().SequenceNumber.Should().Be(20);
    }

    [Fact]
    public void Filter_And_Echo_Exclusions_Consume_Neither_A_Number_Nor_A_Drop()
    {
        using var bus = ControllableBus.EchoCapable(Session());
        using var service = new CanBusService(bus);
        using var subscription = service.Subscribe(
            CanIdFilter.Range(0x123, 0x123, CanFilterIDType.Standard), bufferCapacity: 1);
        var loss = (ILossAwareSubscription)subscription;

        bus.RaiseObserved(Frame(0x124), isEcho: false);
        bus.RaiseObserved(Frame(0x123), isEcho: true);
        loss.DroppedFrameCount.Should().Be(0);
        loss.TryReadWithSequence(out _).Should().BeFalse();

        bus.RaiseObserved(Frame(0x123), isEcho: false);
        loss.TryReadWithSequence(out var first).Should().BeTrue();
        first.SequenceNumber.Should().Be(1);
        subscription.Reconfigure(e => e.Frame.ID == 0x125);
        bus.RaiseObserved(Frame(0x123), isEcho: false);
        bus.RaiseObserved(Frame(0x125), isEcho: false);
        loss.TryReadWithSequence(out var second).Should().BeTrue();
        second.SequenceNumber.Should().Be(2);
        loss.DroppedFrameCount.Should().Be(0);
    }

    [Fact]
    public async Task Concurrent_Overflow_Retains_The_Last_Enqueue_Numbers_In_Order()
    {
        using var bus = ControllableBus.EchoCapable(Session());
        using var service = new CanBusService(bus);
        using var subscription = service.Subscribe(bufferCapacity: 3);
        var loss = (ILossAwareSubscription)subscription;
        var frame = Frame(0x123);
        var writers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 250; i++) bus.RaiseObserved(frame, isEcho: false);
        }));
        await Task.WhenAll(writers).WaitAsync(Watchdog);

        loss.DroppedFrameCount.Should().Be(997);
        Drain(loss).Select(f => f.SequenceNumber).Should().Equal(998UL, 999UL, 1000UL);
    }

    [Fact]
    public async Task Cancelling_A_Reader_Does_Not_Stop_The_Subscription_Or_Count_A_Drop()
    {
        using var bus = ControllableBus.EchoCapable(Session());
        using var service = new CanBusService(bus);
        using var subscription = service.Subscribe(bufferCapacity: 2);
        var loss = (ILossAwareSubscription)subscription;
        using var cancellation = new CancellationTokenSource();
        await using (var reader = loss.FramesWithSequence.GetAsyncEnumerator(cancellation.Token))
        {
            var pending = reader.MoveNextAsync().AsTask();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Watchdog));
        }
        loss.DroppedFrameCount.Should().Be(0);

        for (var id = 1; id <= 3; id++) bus.RaiseObserved(Frame(id), isEcho: false);
        subscription.TryRead(out var legacy).Should().BeTrue();
        legacy.Frame.ID.Should().Be(2);
        loss.TryReadWithSequence(out var sequenced).Should().BeTrue();
        sequenced.SequenceNumber.Should().Be(3, "both projections consume one shared buffer");
        loss.DroppedFrameCount.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disposal_Drains_The_Retained_Frames_Without_Counting_Another_Loss(bool disposeService)
    {
        using var bus = ControllableBus.EchoCapable(Session());
        using var service = new CanBusService(bus);
        using var subscription = service.Subscribe(bufferCapacity: 2);
        var loss = (ILossAwareSubscription)subscription;
        for (var id = 1; id <= 3; id++) bus.RaiseObserved(Frame(id), isEcho: false);

        if (disposeService) service.Dispose();
        else subscription.Dispose();
        bus.RaiseObserved(Frame(4), isEcho: false);
        var retained = new List<SubscriptionFrame>();
        using var watchdog = new CancellationTokenSource(Watchdog);
        await foreach (var frame in loss.FramesWithSequence.WithCancellation(watchdog.Token)) retained.Add(frame);
        retained.Select(f => f.SequenceNumber).Should().Equal(2UL, 3UL);
        loss.DroppedFrameCount.Should().Be(1);
        (await subscription.WaitToReadAsync()).Should().BeFalse();
    }

    [Fact]
    public void A_Gap_Is_Visible_Before_A_Retained_Partner_And_A_Fresh_Contiguous_Pair_Can_Follow()
    {
        using var bus = ControllableBus.EchoCapable(Session());
        using var service = new CanBusService(bus);
        using var subscription = service.Subscribe(bufferCapacity: 1);
        var loss = (ILossAwareSubscription)subscription;
        bus.RaiseObserved(Frame(1), isEcho: false);
        loss.TryReadWithSequence(out var pending).Should().BeTrue();
        bus.RaiseObserved(Frame(2), isEcho: false);
        bus.RaiseObserved(Frame(3), isEcho: false);
        loss.TryReadWithSequence(out var retained).Should().BeTrue();
        retained.SequenceNumber.Should().NotBe(unchecked(pending.SequenceNumber + 1),
            "a consumer must invalidate pending pairing/timing evidence before using this frame");
        loss.DroppedFrameCount.Should().Be(1);

        bus.RaiseObserved(Frame(4), isEcho: false);
        loss.TryReadWithSequence(out var fresh).Should().BeTrue();
        bus.RaiseObserved(Frame(5), isEcho: false);
        loss.TryReadWithSequence(out var partner).Should().BeTrue();
        partner.SequenceNumber.Should().Be(unchecked(fresh.SequenceNumber + 1));
        loss.DroppedFrameCount.Should().Be(1, "fresh evidence can be contiguous without resetting the lifetime count");
    }

    private static void Deliver(Subscription subscription, byte[] payload, bool echo = false)
    {
        var view = new CanFrameView(CanFrameType.Can20, 0x123, payload, FrameFlags.None);
        byte[]? owned = null;
        subscription.TryDeliver(view, echo, TimeSpan.FromTicks(123), 456, ref owned);
    }

    [Fact]
    public void Sequenced_Frames_Keep_The_Original_Metadata_And_Own_Their_Payload()
    {
        using var bus = ControllableBus.EchoCapable(Session());
        using var service = new CanBusService(bus);
        using var subscription = new Subscription(service, null, null, 2, includeEcho: true);
        var payload = new byte[] { 1, 2 };
        Deliver(subscription, payload, echo: true);
        payload[0] = 99;
        subscription.TryReadWithSequence(out var frame).Should().BeTrue();
        frame.SequenceNumber.Should().Be(1);
        frame.FrameEvent.Frame.Data.ToArray().Should().Equal(1, 2);
        frame.FrameEvent.IsEcho.Should().BeTrue();
        frame.FrameEvent.ReceiveTimestamp.Should().Be(TimeSpan.FromTicks(123));
        frame.FrameEvent.HostArrivalTimestamp.Should().Be(456);
    }

    [Fact]
    public void The_Counter_Saturates_And_Disposal_Refuses_Later_Frames()
    {
        using var bus = ControllableBus.EchoCapable(Session());
        using var service = new CanBusService(bus);
        using var subscription = new Subscription(service, null, null, 1, false,
            initialDroppedFrameCount: long.MaxValue - 1);
        Deliver(subscription, new byte[] { 1 });
        Deliver(subscription, new byte[] { 2 });
        subscription.DroppedFrameCount.Should().Be(long.MaxValue);
        Deliver(subscription, new byte[] { 3 });
        subscription.DroppedFrameCount.Should().Be(long.MaxValue);
        subscription.Dispose();
        Deliver(subscription, new byte[] { 4 });
        subscription.TryReadWithSequence(out var last).Should().BeTrue();
        last.FrameEvent.Frame.Data.ToArray().Should().Equal(3);
        subscription.TryReadWithSequence(out _).Should().BeFalse();
        subscription.DroppedFrameCount.Should().Be(long.MaxValue);
    }

    [Fact]
    public void Sequence_Numbers_Wrap_Without_Inventing_A_Loss()
    {
        using var bus = ControllableBus.EchoCapable(Session());
        using var service = new CanBusService(bus);
        using var subscription = new Subscription(service, null, null, 2, false,
            initialSequenceNumber: ulong.MaxValue - 1);
        Deliver(subscription, new byte[] { 1 });
        Deliver(subscription, new byte[] { 2 });
        Drain(subscription).Select(f => f.SequenceNumber).Should().Equal(ulong.MaxValue, 0UL);
        subscription.DroppedFrameCount.Should().Be(0);
    }

    [Fact]
    public async Task A_Waiting_Stream_Reader_Does_Not_Run_Its_Consumer_Inside_Dispatch()
    {
        using var bus = ControllableBus.EchoCapable(Session());
        using var service = new CanBusService(bus);
        using var subscription = service.Subscribe(bufferCapacity: 1);
        var loss = (ILossAwareSubscription)subscription;
        await using var reader = loss.FramesWithSequence.GetAsyncEnumerator();
        var moving = reader.MoveNextAsync().AsTask();
        moving.IsCompleted.Should().BeFalse();
        var writerThread = Environment.CurrentManagedThreadId;
        var dispatchActive = 1;
        var inline = 0;
        var observed = moving.ContinueWith(completed =>
        {
            if (Environment.CurrentManagedThreadId == writerThread && Volatile.Read(ref dispatchActive) != 0)
                Interlocked.Exchange(ref inline, 1);
            return completed.GetAwaiter().GetResult();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        bus.RaiseObserved(Frame(1), isEcho: false);
        Volatile.Write(ref dispatchActive, 0);
        (await observed.WaitAsync(Watchdog)).Should().BeTrue();
        reader.Current.SequenceNumber.Should().Be(1);
        inline.Should().Be(0, "publishing a frame must not invoke a waiting consumer on the dispatch thread");
    }
}
