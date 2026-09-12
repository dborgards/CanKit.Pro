using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Abstractions.SPI.Common;
using CanKit.Core;
using CanKit.Core.Definitions;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace CanKit.Pro.Tests.TestCases;

/// <summary>
/// Verifies the L2 multi-protocol demultiplexing / subscription layer
/// (CanKit.Pro.RawCan, arc42 §5.3 / ADR-5, SRS FR-RAW-010..013) against the Virtual adapter.
/// </summary>
public class RawCanSubscriptionTests : IClassFixture<VirtualAdapterFixture>
{
    private static string NewSession() => $"rawcan-{Guid.NewGuid():N}";

    // Opens a Virtual bus on a unique session so tests never collide.
    private static ICanBus Open(string session, int channel) => CanBus.Open(
        $"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(VirtualAdapterFixture.Bitrate));

    // Drains up to `count` frames from a subscription, giving up after `timeout`. Delivery on the
    // Virtual hub is synchronous inside Transmit, so a short timeout only guards against a hang if
    // something is broken; the happy path returns as soon as `count` frames are read.
    //
    // Projects away the CanFrameEvent envelope, because most tests here assert on IDs and
    // payloads only; the echo/timestamp tests use DrainEvents below instead.
    private static async Task<List<CanFrameView>> Drain(ISubscription sub, int count, TimeSpan timeout)
    {
        var events = await DrainEvents(sub, count, timeout);
        return events.Select(e => e.Frame).ToList();
    }

    // As Drain, but keeps the whole CanFrameEvent -- IsEcho and ReceiveTimestamp included.
    private static async Task<List<CanFrameEvent>> DrainEvents(ISubscription sub, int count, TimeSpan timeout)
    {
        var result = new List<CanFrameEvent>();
        if (count <= 0) return result;
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await foreach (var frameEvent in sub.Frames.WithCancellation(cts.Token))
            {
                result.Add(frameEvent);
                if (result.Count >= count) break;
            }
        }
        catch (OperationCanceledException)
        {
            // timed out waiting for more frames -> return what we have
        }
        return result;
    }

    // Empties a subscription's buffer through the synchronous TryRead path and returns the IDs in
    // arrival order. Used by the ControllableBus-driven tests, where delivery already happened
    // synchronously inside RaiseObserved, so there is nothing left to wait for and Drain's
    // timeout would only add a way for the test to pass by accident.
    private static List<int> DrainIds(ISubscription sub)
    {
        var ids = new List<int>();
        while (sub.TryRead(out var frameEvent)) ids.Add(frameEvent.Frame.ID);
        return ids;
    }

    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(2);

    // FR-RAW-010: two subscriptions with disjoint ID filters on the same bus each receive only
    // their own matching frames.
    [Fact]
    public async Task Disjoint_Id_Filters_Each_Receive_Only_Their_Own_Frames()
    {
        var session = NewSession();
        using var sender = Open(session, 0);
        using var receiver = Open(session, 1);
        using var service = new CanBusService(receiver);

        using var low = service.Subscribe(CanIdFilter.Range(0x100, 0x1FF, CanFilterIDType.Standard));
        using var high = service.Subscribe(CanIdFilter.Range(0x200, 0x2FF, CanFilterIDType.Standard));

        sender.Transmit(CanFrame.Classic(0x100, new byte[] { 1 }));
        sender.Transmit(CanFrame.Classic(0x101, new byte[] { 2 }));
        sender.Transmit(CanFrame.Classic(0x200, new byte[] { 3 }));
        sender.Transmit(CanFrame.Classic(0x201, new byte[] { 4 }));

        var lowFrames = await Drain(low, 2, ShortTimeout);
        var highFrames = await Drain(high, 2, ShortTimeout);

        lowFrames.Select(f => f.ID).Should().Equal(0x100, 0x101);
        highFrames.Select(f => f.ID).Should().Equal(0x200, 0x201);

        // Neither subscription saw the other's traffic: a further short drain yields nothing.
        (await Drain(low, 1, TimeSpan.FromMilliseconds(200))).Should().BeEmpty();
        (await Drain(high, 1, TimeSpan.FromMilliseconds(200))).Should().BeEmpty();
    }

    // FR-RAW-011: a never-drained (worst-case "blocked") subscription must not delay delivery to a
    // second, actively-draining subscription, nor to the bus's own FrameObserved event.
    [Fact]
    public async Task Slow_Subscription_Does_Not_Block_Others_Or_The_Bus_Event()
    {
        var session = NewSession();
        using var sender = Open(session, 0);
        using var receiver = Open(session, 1);
        using var service = new CanBusService(receiver);

        // A direct FrameObserved counter proves the underlying bus event is not stalled either.
        var busEventCount = 0;
        receiver.FrameObserved += (_, _) => Interlocked.Increment(ref busEventCount);

        // Never drained, tiny buffer: its channel fills and drops oldest, but must never block.
        using var slow = service.Subscribe(bufferCapacity: 1);
        // Actively drained, buffer large enough to hold the whole burst.
        using var fast = service.Subscribe(bufferCapacity: 512);

        const int n = 200;
        for (var i = 0; i < n; i++)
            sender.Transmit(CanFrame.Classic(0x300 + (i & 0x0F), new byte[] { (byte)i }));

        var fastFrames = await Drain(fast, n, ShortTimeout);

        fastFrames.Should().HaveCount(n);
        Volatile.Read(ref busEventCount).Should().Be(n);
    }

    // Callback-style Subscribe(onNext, predicate): the handler is invoked for matching frames
    // only, in arrival order.
    [Fact]
    public async Task Callback_Subscribe_Invokes_Handler_For_Matching_Frames_Only()
    {
        var session = NewSession();
        using var sender = Open(session, 0);
        using var receiver = Open(session, 1);
        using var service = new CanBusService(receiver);

        var received = new List<int>();
        var lastReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = service.Subscribe(
            frameEvent =>
            {
                lock (received)
                {
                    received.Add(frameEvent.Frame.ID);
                    if (received.Count >= 2) lastReceived.TrySetResult(true);
                }
            },
            predicate: f => f.Frame.ID == 0x123);

        sender.Transmit(CanFrame.Classic(0x123, new byte[] { 1 }));
        sender.Transmit(CanFrame.Classic(0x456, new byte[] { 2 })); // filtered out
        sender.Transmit(CanFrame.Classic(0x123, new byte[] { 3 }));

        await lastReceived.Task.WaitAsync(ShortTimeout);
        lock (received) received.Should().Equal(0x123, 0x123);
    }

    // Callback-style Subscribe must uphold the same FR-RAW-011 guarantee as the raw pull API: a
    // slow/blocking onNext only ever falls behind its own subscription, never the bus event or a
    // second, actively-draining subscription.
    [Fact]
    public async Task Callback_Subscribe_Slow_Handler_Does_Not_Block_Others_Or_The_Bus_Event()
    {
        var session = NewSession();
        using var sender = Open(session, 0);
        using var receiver = Open(session, 1);
        using var service = new CanBusService(receiver);

        var busEventCount = 0;
        receiver.FrameObserved += (_, _) => Interlocked.Increment(ref busEventCount);

        using var block = new SemaphoreSlim(0); // never released: the slow handler blocks forever
        using var slow = service.Subscribe(_ => block.Wait(), bufferCapacity: 1);

        var fastCount = 0;
        var allFastReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        const int n = 200;
        using var fast = service.Subscribe(_ =>
        {
            if (Interlocked.Increment(ref fastCount) >= n) allFastReceived.TrySetResult(true);
        }, bufferCapacity: 512);

        for (var i = 0; i < n; i++)
            sender.Transmit(CanFrame.Classic(0x300 + (i & 0x0F), new byte[] { (byte)i }));

        await allFastReceived.Task.WaitAsync(ShortTimeout);
        Volatile.Read(ref busEventCount).Should().Be(n);
    }

    // Disposing the callback handle stops delivery: no further onNext calls after Dispose returns.
    [Fact]
    public async Task Callback_Subscribe_Dispose_Stops_Delivery()
    {
        var session = NewSession();
        using var sender = Open(session, 0);
        using var receiver = Open(session, 1);
        using var service = new CanBusService(receiver);

        var count = 0;
        var firstReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscription = service.Subscribe(_ =>
        {
            Interlocked.Increment(ref count);
            firstReceived.TrySetResult(true);
        });

        sender.Transmit(CanFrame.Classic(0x100, new byte[] { 1 }));
        await firstReceived.Task.WaitAsync(ShortTimeout);

        subscription.Dispose();
        var countAfterDispose = Volatile.Read(ref count);

        sender.Transmit(CanFrame.Classic(0x100, new byte[] { 2 }));
        sender.Transmit(CanFrame.Classic(0x100, new byte[] { 3 }));
        await Task.Delay(200);

        Volatile.Read(ref count).Should().Be(countAfterDispose);
    }

    // Disposing the handle from inside onNext must not join the pump that is currently
    // invoking that callback: a self-wait can only finish via the two-second timeout.
    [Fact]
    public async Task Callback_Subscribe_Dispose_From_Inside_Handler_Does_Not_Hang()
    {
        var session = NewSession();
        using var sender = Open(session, 0);
        using var receiver = Open(session, 1);
        using var service = new CanBusService(receiver);

        IDisposable? subscription = null;
        var disposed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        subscription = service.Subscribe(_ =>
        {
            subscription!.Dispose();
            disposed.TrySetResult(true);
        });

        sender.Transmit(CanFrame.Classic(0x100, new byte[] { 1 }));
        // The pump join timeout is 2s; completing well under that proves the self-wait was skipped.
        await disposed.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
    }

    // Completing the channel writer does not drop items already queued. Disposing from inside
    // onNext skips the pump join, so the pump must stop on the disposed flag rather than drain
    // the remainder -- otherwise further onNext calls run after Dispose has returned.
    [Fact]
    public async Task Callback_Subscribe_Dispose_From_Inside_Handler_Does_Not_Deliver_Buffered_Frames()
    {
        var session = NewSession();
        using var sender = Open(session, 0);
        using var receiver = Open(session, 1);
        using var service = new CanBusService(receiver);

        IDisposable? subscription = null;
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        subscription = service.Subscribe(_ =>
        {
            if (Interlocked.Increment(ref count) == 1)
            {
                entered.TrySetResult(true);
                proceed.Task.GetAwaiter().GetResult();
                subscription!.Dispose();
                disposed.TrySetResult(true);
            }
        });

        sender.Transmit(CanFrame.Classic(0x100, new byte[] { 1 }));
        await entered.Task.WaitAsync(ShortTimeout);

        // Burst into the bounded buffer while onNext is blocked; these would otherwise be
        // delivered after Dispose returns if the pump kept draining the completed channel.
        for (var i = 0; i < 16; i++)
            sender.Transmit(CanFrame.Classic(0x100, new byte[] { (byte)i }));

        proceed.TrySetResult(true);
        await disposed.Task.WaitAsync(ShortTimeout);
        await Task.Delay(200);

        Volatile.Read(ref count).Should().Be(1);
    }

    // A handler exception is isolated per frame -- delivery continues -- and surfaced via the
    // service's existing fault channel, the same as a throwing predicate.
    [Fact]
    public async Task Callback_Subscribe_Handler_Exception_Is_Surfaced_And_Delivery_Continues()
    {
        var session = NewSession();
        using var sender = Open(session, 0);
        using var receiver = Open(session, 1);
        using var service = new CanBusService(receiver);

        var observed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.BackgroundExceptionOccurred += (_, ex) => observed.TrySetResult(ex);

        var secondReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var subscription = service.Subscribe(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                throw new InvalidOperationException("boom");
            secondReceived.TrySetResult(true);
        });

        sender.Transmit(CanFrame.Classic(0x100, new byte[] { 1 }));
        var ex = await observed.Task.WaitAsync(ShortTimeout);
        ex.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("boom");

        sender.Transmit(CanFrame.Classic(0x100, new byte[] { 2 }));
        await secondReceived.Task.WaitAsync(ShortTimeout);
    }

    // FR-RAW-012: creating and disposing N subscriptions leaves no entries in the service registry.
    [Fact]
    public void Disposing_Subscriptions_Leaves_No_Registry_Entries()
    {
        var session = NewSession();
        using var receiver = Open(session, 0);
        using var service = new CanBusService(receiver);

        service.SubscriptionCount.Should().Be(0);

        var subs = new List<ISubscription>();
        for (var i = 0; i < 20; i++)
            subs.Add(service.Subscribe(CanIdFilter.Range((uint)i, (uint)i, CanFilterIDType.Standard)));

        service.SubscriptionCount.Should().Be(20);

        foreach (var sub in subs)
            sub.Dispose();

        service.SubscriptionCount.Should().Be(0);
    }

    // FR-RAW-014: reconfiguring a subscription's filter at runtime applies to subsequent frames only.
    [Fact]
    public async Task Reconfigure_Filter_At_Runtime_Subsequent_Frames_Follow_New_Criterion()
    {
        var session = NewSession();
        using var sender = Open(session, 0);
        using var receiver = Open(session, 1);
        using var service = new CanBusService(receiver);

        using var sub = service.Subscribe(CanIdFilter.Range(0x100, 0x1FF, CanFilterIDType.Standard));

        sender.Transmit(CanFrame.Classic(0x100, new byte[] { 1 }));
        sender.Transmit(CanFrame.Classic(0x200, new byte[] { 2 }));

        var before = await Drain(sub, 1, ShortTimeout);
        before.Select(f => f.ID).Should().Equal(0x100);

        sub.Reconfigure(CanIdFilter.Range(0x200, 0x2FF, CanFilterIDType.Standard));

        sender.Transmit(CanFrame.Classic(0x101, new byte[] { 3 }));
        sender.Transmit(CanFrame.Classic(0x201, new byte[] { 4 }));

        var after = await Drain(sub, 1, ShortTimeout);
        after.Select(f => f.ID).Should().Equal(0x201);
    }

    // FR-RAW-014, predicate overload: the same runtime-reconfiguration guarantee as for the
    // ID-filter overload, plus the two things only this overload can express -- replacing an
    // ID filter with a predicate, and passing null to go back to accepting everything.
    // Driven through ControllableBus so each RaiseObserved is delivered synchronously on this
    // thread: "which frames arrived after the reconfigure" is then a fact, not a drain race.
    [Fact]
    public void Reconfigure_Predicate_At_Runtime_Subsequent_Frames_Follow_New_Criterion()
    {
        using var bus = ControllableBus.EchoCapable(NewSession());
        using var service = new CanBusService(bus);

        using var sub = service.Subscribe(CanIdFilter.Range(0x100, 0x1FF, CanFilterIDType.Standard));

        bus.RaiseObserved(CanFrame.Classic(0x100, new byte[] { 1 }), isEcho: false);
        bus.RaiseObserved(CanFrame.Classic(0x200, new byte[] { 2 }), isEcho: false);
        DrainIds(sub).Should().Equal(0x100);

        // Predicate replaces the ID filter outright: 0x200 now matches and 0x100 no longer does,
        // which an "and-ed on top of the old filter" implementation could not produce.
        sub.Reconfigure(f => f.Frame.ID >= 0x200);

        bus.RaiseObserved(CanFrame.Classic(0x100, new byte[] { 3 }), isEcho: false);
        bus.RaiseObserved(CanFrame.Classic(0x201, new byte[] { 4 }), isEcho: false);
        DrainIds(sub).Should().Equal(0x201);

        // null is documented as "accept all", so both of the above must now arrive.
        sub.Reconfigure((Func<CanFrameEvent, bool>?)null);

        bus.RaiseObserved(CanFrame.Classic(0x100, new byte[] { 5 }), isEcho: false);
        bus.RaiseObserved(CanFrame.Classic(0x201, new byte[] { 6 }), isEcho: false);
        DrainIds(sub).Should().Equal(0x100, 0x201);
    }

    // FR-RAW-012/014: a disposed subscription is not a silently inert one. Reconfigure after
    // Dispose must throw ObjectDisposedException -- reconfiguring a subscription that will never
    // deliver another frame is a caller bug, and swallowing it would hide it.
    [Fact]
    public void Reconfigure_After_Dispose_Throws_On_Both_Overloads()
    {
        using var bus = ControllableBus.EchoCapable(NewSession());
        using var service = new CanBusService(bus);

        var sub = service.Subscribe();
        sub.Dispose();

        var byFilter = () => sub.Reconfigure(CanIdFilter.Range(0x100, 0x1FF, CanFilterIDType.Standard));
        var byPredicate = () => sub.Reconfigure(f => f.Frame.ID == 0x100);

        byFilter.Should().Throw<ObjectDisposedException>();
        byPredicate.Should().Throw<ObjectDisposedException>();
    }

    // FR-RAW-011: the per-subscription buffer is bounded *and* drop-oldest. Bounded alone is not
    // the requirement -- a drop-newest buffer is also bounded, and also never blocks dispatch, so
    // only asserting "the consumer is not blocked" leaves the discard policy untested. What must
    // hold is that an undrained subscription keeps the most recent `capacity` frames: monitoring
    // consumers want current traffic, not a snapshot frozen at the moment they fell behind.
    [Fact]
    public void Full_Subscription_Buffer_Drops_The_Oldest_Frames_Not_The_Newest()
    {
        using var bus = ControllableBus.EchoCapable(NewSession());
        using var service = new CanBusService(bus);

        const int capacity = 4;
        const int sent = 7;
        using var sub = service.Subscribe(bufferCapacity: capacity);

        // Never drained while sending: every frame past the fourth has to displace one.
        for (var i = 0; i < sent; i++)
            bus.RaiseObserved(CanFrame.Classic(0x100 + i, new byte[] { (byte)i }), isEcho: false);

        var buffered = DrainIds(sub);

        buffered.Should().HaveCount(capacity, "the buffer is bounded at its configured capacity");
        buffered.Should().Equal(new[] { 0x103, 0x104, 0x105, 0x106 },
            "a full drop-oldest buffer discards the three oldest frames and keeps the newest four, "
            + "still in arrival order");
    }

    // TryRead is the non-blocking counterpart to Frames: it removes what is already buffered and
    // reports emptiness rather than waiting. Both halves matter -- a TryRead that awaited arrival
    // would deadlock the request/reply clients that use it to drain stale chatter before issuing
    // a request, and one that returned a stale frame twice would double-deliver it.
    [Fact]
    public void TryRead_Removes_One_Buffered_Frame_And_Reports_An_Empty_Buffer()
    {
        using var bus = ControllableBus.EchoCapable(NewSession());
        using var service = new CanBusService(bus);
        using var sub = service.Subscribe();

        sub.TryRead(out var nothing).Should().BeFalse("nothing has been delivered yet");
        nothing.Should().Be(default(CanFrameEvent));

        bus.RaiseObserved(CanFrame.Classic(0x100, new byte[] { 1 }), isEcho: false);
        bus.RaiseObserved(CanFrame.Classic(0x101, new byte[] { 2 }), isEcho: false);

        sub.TryRead(out var first).Should().BeTrue();
        first.Frame.ID.Should().Be(0x100);
        first.Frame.Data.ToArray().Should().Equal(new byte[] { 1 });

        sub.TryRead(out var second).Should().BeTrue("TryRead consumes, so the next call sees the next frame");
        second.Frame.ID.Should().Be(0x101);

        sub.TryRead(out _).Should().BeFalse("both buffered frames have been consumed");
    }

    // FR-RAW-013: the ID-range/mask fast path matches and excludes correctly (unit-level, no bus).
    [Fact]
    public void IdFilter_FastPath_Matches_And_Excludes()
    {
        static CanFrameView View(int id, bool extended) => new(
            CanFrameType.Can20, id, ReadOnlyMemory<byte>.Empty,
            extended ? FrameFlags.Ext : FrameFlags.None);

        var range = CanIdFilter.Range(0x100, 0x1FF, CanFilterIDType.Standard);
        range.Matches(View(0x100, extended: false)).Should().BeTrue();
        range.Matches(View(0x1FF, extended: false)).Should().BeTrue();
        range.Matches(View(0x0FF, extended: false)).Should().BeFalse();
        range.Matches(View(0x200, extended: false)).Should().BeFalse();
        // Same numeric ID in the other ID space never matches.
        range.Matches(View(0x150, extended: true)).Should().BeFalse();

        var extRange = CanIdFilter.Range(0x100, 0x1FF, CanFilterIDType.Extend);
        extRange.Matches(View(0x150, extended: true)).Should().BeTrue();
        extRange.Matches(View(0x150, extended: false)).Should().BeFalse();

        var mask = CanIdFilter.Mask(0x100, 0x700, CanFilterIDType.Standard);
        mask.Matches(View(0x100, extended: false)).Should().BeTrue();
        mask.Matches(View(0x1FF, extended: false)).Should().BeTrue(); // 0x1FF & 0x700 == 0x100
        mask.Matches(View(0x200, extended: false)).Should().BeFalse(); // 0x200 & 0x700 == 0x200
    }

    // Range(from,to) rejects an inverted range up front.
    [Fact]
    public void IdFilter_Range_Rejects_Inverted_Bounds()
    {
        Action act = () => CanIdFilter.Range(0x200, 0x100);
        act.Should().Throw<ArgumentException>();
    }

    // FR-RAW-012: disposing the same subscription twice is a safe no-op.
    [Fact]
    public void Double_Dispose_Of_Subscription_Is_Safe()
    {
        var session = NewSession();
        using var receiver = Open(session, 0);
        using var service = new CanBusService(receiver);

        var sub = service.Subscribe();
        sub.Dispose();
        sub.Dispose(); // must not throw

        service.SubscriptionCount.Should().Be(0);
    }

    // Disposing the service unwinds subscriptions, detaches from FrameObserved, and delivers no
    // further frames to any of its subscriptions.
    [Fact]
    public async Task Disposing_Service_Stops_Delivery_And_Detaches()
    {
        var session = NewSession();
        using var sender = Open(session, 0);
        using var receiver = Open(session, 1);
        var service = new CanBusService(receiver);
        var sub = service.Subscribe();

        sender.Transmit(CanFrame.Classic(0x123, new byte[] { 1 }));
        (await Drain(sub, 1, ShortTimeout)).Select(f => f.ID).Should().Equal(0x123);

        service.Dispose();
        service.SubscriptionCount.Should().Be(0);

        // After disposal the subscription's stream is completed and no new frames arrive; a drain
        // returns immediately with nothing even though the bus keeps carrying traffic.
        sender.Transmit(CanFrame.Classic(0x124, new byte[] { 2 }));
        (await Drain(sub, 1, TimeSpan.FromMilliseconds(500))).Should().BeEmpty();

        // Double-dispose of the service itself is also safe.
        service.Dispose();
    }

    // Allocator whose owner zeroes its buffer on Dispose, simulating a pooled buffer being
    // returned to the pool and reused by someone else. Used to make "did the subscription queue
    // an aliased view instead of an independent copy" observable deterministically, rather than
    // relying on ArrayPool's actual (unspecified) reuse timing.
    private sealed class PoisoningBufferAllocator : IBufferAllocator
    {
        public IMemoryOwner<byte> Rent(int length, bool zeroFill = false) => new PoisoningOwner(length);

        public bool FrameNeedDispose => true;

        private sealed class PoisoningOwner(int length) : IMemoryOwner<byte>
        {
            private readonly byte[] _buffer = new byte[length];

            public Memory<byte> Memory => _buffer;

            public void Dispose() => Array.Clear(_buffer, 0, _buffer.Length);
        }
    }

    // Regression test for a Bugbot finding on the original PR: ICanBus.FrameObserved fires (and
    // TryDeliver enqueues into the subscription's channel) *before* the adapter is finished with
    // the frame it just handed out, and the adapter may dispose that RX lease -- returning its
    // buffer to a pool that hands it straight to someone else -- long before this subscription has
    // read its buffered copy. Queuing the raw CanFrameView (which aliases that memory) instead of a
    // copy would let the later dispose corrupt an unread buffered frame.
    //
    // Driven through ControllableBus rather than an adapter: whether an adapter's RX frames are
    // pooled, and when it disposes them, is an adapter implementation detail that the test would
    // otherwise silently depend on. Here the lease is disposed at an exact, deliberate point.
    [Fact]
    public async Task Buffered_Frame_Survives_Its_Source_RX_Lease_Being_Disposed_Afterward()
    {
        var allocator = new PoisoningBufferAllocator();

        using var bus = ControllableBus.EchoCapable(NewSession());
        using var service = new CanBusService(bus);
        using var sub = service.Subscribe();

        // An RX lease exactly as an adapter hands one out: a frame over pooled memory that the
        // adapter owns and disposes on its own schedule.
        var payload = new byte[] { 1, 2, 3 };
        var owner = allocator.Rent(payload.Length);
        payload.AsSpan().CopyTo(owner.Memory.Span);
        var lease = CanFrame.Classic(0x100, owner);

        bus.RaiseObserved(lease, isEcho: false);

        // Pool return: PoisoningBufferAllocator zeroes the buffer right here, synchronously, well
        // before this test drains the subscription.
        lease.Dispose();

        var frames = await Drain(sub, 1, ShortTimeout);

        frames.Should().ContainSingle();
        frames[0].Data.ToArray().Should().Equal(new byte[] { 1, 2, 3 });
    }

    // Regression test for a second Bugbot finding: a caller-supplied predicate that throws must
    // not suppress delivery to other subscriptions, nor abort the dispatch loop early.
    [Fact]
    public async Task Throwing_Predicate_In_One_Subscription_Does_Not_Suppress_Delivery_To_Others()
    {
        var session = NewSession();
        using var sender = Open(session, 0);
        using var receiver = Open(session, 1);
        using var service = new CanBusService(receiver);

        // Registered first, so it is dispatched before `healthy` in the snapshot order.
        using var broken = service.Subscribe(_ => throw new InvalidOperationException("boom"));
        using var healthy = service.Subscribe();

        sender.Transmit(CanFrame.Classic(0x100, new byte[] { 9 }));

        var healthyFrames = await Drain(healthy, 1, ShortTimeout);
        healthyFrames.Select(f => f.ID).Should().Equal(0x100);
    }

    // The same predicate fault must also be surfaced through the service's fault channel
    // (BackgroundExceptionOccurred) instead of being silently swallowed.
    [Fact]
    public async Task Throwing_Predicate_Is_Surfaced_Via_BackgroundExceptionOccurred()
    {
        var session = NewSession();
        using var sender = Open(session, 0);
        using var receiver = Open(session, 1);
        using var service = new CanBusService(receiver);

        var observed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.BackgroundExceptionOccurred += (_, ex) => observed.TrySetResult(ex);

        using var broken = service.Subscribe(_ => throw new InvalidOperationException("boom"));
        using var healthy = service.Subscribe();

        sender.Transmit(CanFrame.Classic(0x100, new byte[] { 9 }));

        var ex = await observed.Task.WaitAsync(ShortTimeout);
        ex.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("boom");

        // Delivery isolation still holds alongside the fault channel.
        var healthyFrames = await Drain(healthy, 1, ShortTimeout);
        healthyFrames.Select(f => f.ID).Should().Equal(0x100);
    }

    // =============================================================================================
    // #23 -- the subscription element carries the bus's echo flag and receive timestamp, and
    // echoes are withheld unless the subscriber asked for them.
    //
    // ControllableBus rather than the Virtual adapter throughout: these assertions are about what
    // the demux does with the CanReceiveDataView it is handed, so the test has to be the one that
    // decides what IsEcho and ReceiveTimestamp are on it.
    // =============================================================================================

    // FR-RAW-010: the default subscription is echo-free. Before #23 the demux dropped the bus's
    // echo flag, so every subscriber saw its own transmissions arrive as if a peer had sent them --
    // which is what drove J1939, CANopen and the J1939 transport to each rebuild "is this mine?"
    // out of application data.
    [Fact]
    public void Subscription_Does_Not_Deliver_Echoes_By_Default()
    {
        using var bus = ControllableBus.EchoCapable(NewSession());
        using var service = new CanBusService(bus);
        using var sub = service.Subscribe();

        bus.RaiseObserved(CanFrame.Classic(0x100, new byte[] { 1 }), isEcho: false);
        bus.RaiseObserved(CanFrame.Classic(0x101, new byte[] { 2 }), isEcho: true);
        bus.RaiseObserved(CanFrame.Classic(0x102, new byte[] { 3 }), isEcho: false);

        DrainIds(sub).Should().Equal(0x100, 0x102);
    }

    // ... and opting in gets them, flagged, interleaved in arrival order with the received frames
    // rather than on a separate channel.
    [Fact]
    public void Subscription_With_IncludeEcho_Receives_Echoes_Marked_As_Such()
    {
        using var bus = ControllableBus.EchoCapable(NewSession());
        using var service = new CanBusService(bus);
        using var sub = service.Subscribe(includeEcho: true);

        bus.RaiseObserved(CanFrame.Classic(0x100, new byte[] { 1 }), isEcho: false);
        bus.RaiseObserved(CanFrame.Classic(0x101, new byte[] { 2 }), isEcho: true);
        bus.RaiseObserved(CanFrame.Classic(0x102, new byte[] { 3 }), isEcho: false);

        var events = new List<CanFrameEvent>();
        while (sub.TryRead(out var e)) events.Add(e);

        events.Select(e => e.Frame.ID).Should().Equal(0x100, 0x101, 0x102);
        events.Select(e => e.IsEcho).Should().Equal(false, true, false);
    }

    // The echo choice is per subscription, not per service: one service, two subscriptions, two
    // different views of the same frame.
    [Fact]
    public void IncludeEcho_Is_Decided_Per_Subscription()
    {
        using var bus = ControllableBus.EchoCapable(NewSession());
        using var service = new CanBusService(bus);
        using var quiet = service.Subscribe();
        using var loud = service.Subscribe(includeEcho: true);

        bus.RaiseObserved(CanFrame.Classic(0x200, new byte[] { 1 }), isEcho: true);
        bus.RaiseObserved(CanFrame.Classic(0x201, new byte[] { 2 }), isEcho: false);

        DrainIds(quiet).Should().Equal(0x201);
        DrainIds(loud).Should().Equal(0x200, 0x201);
    }

    // The echo gate runs before the filter, so an echo never reaches a caller-supplied predicate
    // either. A predicate is arbitrary user code -- letting an echo run it would put the side
    // effect back exactly where withholding the frame was supposed to prevent it.
    [Fact]
    public void Echo_Is_Never_Offered_To_A_Predicate_That_Did_Not_Opt_In()
    {
        using var bus = ControllableBus.EchoCapable(NewSession());
        using var service = new CanBusService(bus);

        var seenByPredicate = new List<int>();
        using var sub = service.Subscribe(e =>
        {
            lock (seenByPredicate) seenByPredicate.Add(e.Frame.ID);
            return true;
        });

        bus.RaiseObserved(CanFrame.Classic(0x300, new byte[] { 1 }), isEcho: true);
        bus.RaiseObserved(CanFrame.Classic(0x301, new byte[] { 2 }), isEcho: false);

        lock (seenByPredicate) seenByPredicate.Should().Equal(0x301);
        DrainIds(sub).Should().Equal(0x301);
    }

    // An opted-in predicate does see the flag, and can filter on it.
    [Fact]
    public void An_Opted_In_Predicate_Can_Filter_On_IsEcho()
    {
        using var bus = ControllableBus.EchoCapable(NewSession());
        using var service = new CanBusService(bus);
        using var sub = service.Subscribe(e => e.IsEcho, includeEcho: true);

        bus.RaiseObserved(CanFrame.Classic(0x400, new byte[] { 1 }), isEcho: false);
        bus.RaiseObserved(CanFrame.Classic(0x401, new byte[] { 2 }), isEcho: true);

        DrainIds(sub).Should().Equal(0x401);
    }

    // FR-RAW-010: the bus's own receive timestamp reaches the subscriber unmodified. The demux
    // used to discard it, leaving no way to order frames by when the adapter saw them.
    [Fact]
    public void Receive_Timestamp_Reaches_The_Subscriber_Unmodified()
    {
        using var bus = ControllableBus.EchoCapable(NewSession());
        using var service = new CanBusService(bus);
        using var sub = service.Subscribe();

        var first = TimeSpan.FromMilliseconds(12.5);
        var second = TimeSpan.FromMilliseconds(37.25);
        bus.RaiseObserved(CanFrame.Classic(0x500, new byte[] { 1 }), isEcho: false, receiveTimestamp: first);
        bus.RaiseObserved(CanFrame.Classic(0x501, new byte[] { 2 }), isEcho: false, receiveTimestamp: second);

        var events = new List<CanFrameEvent>();
        while (sub.TryRead(out var e)) events.Add(e);

        events.Select(e => e.ReceiveTimestamp).Should().Equal(first, second);
    }

    // An adapter that does not timestamp reports zero, which must arrive as zero rather than as
    // something the demux invented (a capture time of its own would look like data and be wrong).
    [Fact]
    public void An_Untimestamped_Adapter_Yields_A_Zero_Timestamp()
    {
        using var bus = ControllableBus.EchoCapable(NewSession());
        using var service = new CanBusService(bus);
        using var sub = service.Subscribe();

        bus.RaiseObserved(CanFrame.Classic(0x502, new byte[] { 1 }), isEcho: false);

        sub.TryRead(out var only).Should().BeTrue();
        only.ReceiveTimestamp.Should().Be(TimeSpan.Zero);
    }

    // Reconfiguring the filter must not silently re-open the echo gate: includeEcho is fixed at
    // Subscribe time and is not part of what Reconfigure replaces.
    [Fact]
    public void Reconfigure_Does_Not_Change_The_Echo_Choice()
    {
        using var bus = ControllableBus.EchoCapable(NewSession());
        using var service = new CanBusService(bus);
        using var quiet = service.Subscribe();
        using var loud = service.Subscribe(includeEcho: true);

        quiet.Reconfigure(CanIdFilter.Range(0x600, 0x6FF, CanFilterIDType.Standard));
        loud.Reconfigure(CanIdFilter.Range(0x600, 0x6FF, CanFilterIDType.Standard));

        bus.RaiseObserved(CanFrame.Classic(0x600, new byte[] { 1 }), isEcho: true);
        bus.RaiseObserved(CanFrame.Classic(0x601, new byte[] { 2 }), isEcho: false);

        DrainIds(quiet).Should().Equal(0x601);
        DrainIds(loud).Should().Equal(0x600, 0x601);
    }

    // CanFrameEvent equality must compare payload *bytes*, not which array holds them.
    //
    // CanFrameView is a record struct over a ReadOnlyMemory<byte>, and its generated equality
    // tests the memory segment rather than the contents. Delegating to it looked harmless and was
    // not: TryDeliver allocates a fresh array per delivered frame, so the same frame fanned out to
    // two subscriptions produced two events that compared unequal — the opposite of what `a == b`
    // means for a value type.
    [Fact]
    public void Two_Subscriptions_Receiving_The_Same_Frame_Produce_Equal_Events()
    {
        using var bus = ControllableBus.EchoCapable(NewSession());
        using var service = new CanBusService(bus);
        using var first = service.Subscribe();
        using var second = service.Subscribe();

        bus.RaiseObserved(
            CanFrame.Classic(0x123, new byte[] { 1, 2, 3, 4 }),
            isEcho: false,
            receiveTimestamp: TimeSpan.FromMilliseconds(7));

        first.TryRead(out var a).Should().BeTrue();
        second.TryRead(out var b).Should().BeTrue();

        // Distinct buffers by construction -- that is exactly the case that used to break.
        MemoryMarshal.TryGetArray(a.Frame.Data, out var segA).Should().BeTrue();
        MemoryMarshal.TryGetArray(b.Frame.Data, out var segB).Should().BeTrue();
        ReferenceEquals(segA.Array, segB.Array).Should().BeFalse(
            "each subscription buffers its own copy");

        a.Should().Be(b);
        (a == b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode(), "equal values must hash equally");
    }

    // ... and events that differ in any compared component are not equal.
    [Fact]
    public void Events_Differing_In_Payload_Echo_Or_Timestamp_Are_Not_Equal()
    {
        var frame = new CanFrameView(
            CanFrameType.Can20, 0x123, new byte[] { 1, 2, 3 }, FrameFlags.None);
        var baseline = new CanFrameEvent(frame, isEcho: false, TimeSpan.FromMilliseconds(5));

        var otherPayload = new CanFrameEvent(
            new CanFrameView(CanFrameType.Can20, 0x123, new byte[] { 1, 2, 4 }, FrameFlags.None),
            isEcho: false, TimeSpan.FromMilliseconds(5));
        var otherId = new CanFrameEvent(
            new CanFrameView(CanFrameType.Can20, 0x124, new byte[] { 1, 2, 3 }, FrameFlags.None),
            isEcho: false, TimeSpan.FromMilliseconds(5));

        baseline.Should().NotBe(otherPayload, "payload bytes are compared");
        baseline.Should().NotBe(otherId);
        baseline.Should().NotBe(new CanFrameEvent(frame, isEcho: true, TimeSpan.FromMilliseconds(5)));
        baseline.Should().NotBe(new CanFrameEvent(frame, isEcho: false, TimeSpan.FromMilliseconds(6)));
    }

    // SendConfirmed's echo matching (FR-RAW-031) reads the bus event directly, not a subscription,
    // so withholding echoes from subscribers must not disturb it. Worth pinning: the two paths sit
    // in the same OnFrameObserved and it would be easy to gate both on one flag.
    [Fact]
    public async Task Withholding_Echoes_From_Subscribers_Does_Not_Break_SendConfirmed()
    {
        using var bus = ControllableBus.EchoCapable(NewSession());
        using var service = new CanBusService(bus);
        using var sub = service.Subscribe(); // default: no echoes

        var confirmation = await service.SendConfirmed(
            CanFrame.Classic(0x700, new byte[] { 1, 2, 3 }),
            TimeSpan.FromSeconds(2));

        confirmation.Confirmed.Should().BeTrue();
        confirmation.IsApproximated.Should().BeFalse("the bus is echo-capable, so this is a real echo match");

        // ... and the subscriber still did not see the echo that confirmed it.
        DrainIds(sub).Should().BeEmpty();
    }
}
