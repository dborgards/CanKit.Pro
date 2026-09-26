using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CanKit.Pro.CANopen.Heartbeat;
using CanKit.Pro.Reliability;
using CanKit.Pro.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

/// <summary>
/// The heartbeat consumer on its own. No producer is constructed: the module only watches.
/// </summary>
public class HeartbeatConsumerTests
{
    [Fact]
    public async Task Each_Watched_Node_Times_Out_On_Its_Own_Interval()
    {
        using var clock = new VirtualClock();
        var actor = clock.NewActor();
        using var consumer = new HeartbeatConsumer(new DeadlineScheduler(actor));
        var missed = new List<(byte Id, TimeSpan At)>();
        consumer.TimedOut += (id, _) => missed.Add((id, clock.Elapsed));

        await actor.PostAsync(() => consumer.Replace(new Dictionary<byte, TimeSpan>
        {
            [0x11] = TimeSpan.FromMilliseconds(50),
            [0x12] = TimeSpan.FromMilliseconds(80),
        }));

        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(50));
        missed.Should().Equal(new[] { ((byte)0x11, TimeSpan.FromMilliseconds(50)) });

        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(30));
        missed.Should().Equal(new[]
        {
            ((byte)0x11, TimeSpan.FromMilliseconds(50)),
            ((byte)0x12, TimeSpan.FromMilliseconds(80)),
        });
    }

    [Fact]
    public async Task A_Received_Heartbeat_Postpones_The_Timeout()
    {
        using var clock = new VirtualClock();
        var actor = clock.NewActor();
        using var consumer = new HeartbeatConsumer(new DeadlineScheduler(actor));
        var missed = new List<TimeSpan>();
        consumer.TimedOut += (_, _) => missed.Add(clock.Elapsed);

        await actor.PostAsync(() => consumer.Replace(new Dictionary<byte, TimeSpan>
        {
            [0x11] = TimeSpan.FromMilliseconds(50),
        }));
        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(40));
        await actor.PostAsync(() => consumer.NoteReceived(0x11));
        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(40));
        missed.Should().BeEmpty("the timeout is measured from the heartbeat, not from the original arm");

        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(10));
        missed.Should().Equal(TimeSpan.FromMilliseconds(90));
    }

    [Fact]
    public async Task Replacing_The_Set_Drops_A_Watch_Before_It_Can_Time_Out()
    {
        using var clock = new VirtualClock();
        var actor = clock.NewActor();
        using var consumer = new HeartbeatConsumer(new DeadlineScheduler(actor));
        var missed = new List<byte>();
        consumer.TimedOut += (id, _) => missed.Add(id);

        await actor.PostAsync(() => consumer.Replace(new Dictionary<byte, TimeSpan>
        {
            [0x11] = TimeSpan.FromMilliseconds(50),
        }));
        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(20));
        await actor.PostAsync(() =>
        {
            consumer.IsWatching(0x11).Should().BeTrue();
            consumer.Replace(new Dictionary<byte, TimeSpan>());
            consumer.IsWatching(0x11).Should().BeFalse();
        });
        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(100));
        missed.Should().BeEmpty();
    }

    [Fact]
    public async Task Silence_After_A_Timeout_Times_Out_Again()
    {
        using var clock = new VirtualClock();
        var actor = clock.NewActor();
        using var consumer = new HeartbeatConsumer(new DeadlineScheduler(actor));
        var missed = new List<TimeSpan>();
        consumer.TimedOut += (_, _) => missed.Add(clock.Elapsed);

        await actor.PostAsync(() => consumer.Replace(new Dictionary<byte, TimeSpan>
        {
            [0x11] = TimeSpan.FromMilliseconds(50),
        }));
        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(50));
        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(50));
        missed.Should().Equal(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task An_Unchanged_Timeout_Keeps_Its_Deadline_And_A_Changed_One_Does_Not()
    {
        using var clock = new VirtualClock();
        var actor = clock.NewActor();
        using var consumer = new HeartbeatConsumer(new DeadlineScheduler(actor));
        var missed = new List<byte>();
        consumer.TimedOut += (id, _) => missed.Add(id);

        await actor.PostAsync(() => consumer.Replace(new Dictionary<byte, TimeSpan>
        {
            [0x11] = TimeSpan.FromMilliseconds(50),
            [0x12] = TimeSpan.FromMilliseconds(80),
        }));
        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(20));
        await actor.PostAsync(() => consumer.Replace(new Dictionary<byte, TimeSpan>
        {
            [0x11] = TimeSpan.FromMilliseconds(50),
            [0x12] = TimeSpan.FromMilliseconds(40),
        }));

        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(30));
        missed.Should().Equal(new[] { (byte)0x11 }, "the unchanged watch still expires on its original deadline");

        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(10));
        missed.Should().Equal(new[] { (byte)0x11, (byte)0x12 },
            "0x12 was armed again when its timeout changed, measured from the replacement");
    }

    [Fact]
    public void Rejects_A_Null_Scheduler_And_A_Null_Watch_Set()
    {
        Action ctor = () => _ = new HeartbeatConsumer(null!);
        ctor.Should().Throw<ArgumentNullException>().WithParameterName("scheduler");

        var consumer = new HeartbeatConsumer(new ScriptedScheduler());
        Action replace = () => consumer.Replace(null!);
        replace.Should().Throw<ArgumentNullException>().WithParameterName("watches");
    }

    [Fact]
    public void A_Heartbeat_Rearms_From_Scratch_When_The_Deadline_Cannot()
    {
        var scheduler = new ScriptedScheduler { ReturnNull = true };
        using var consumer = new HeartbeatConsumer(scheduler);
        var missed = new List<byte>();
        consumer.TimedOut += (id, _) => missed.Add(id);

        consumer.Replace(One(0x11, 50));
        consumer.NoteReceived(0x11);
        scheduler.Armed.Should().HaveCount(2, "a missing deadline is replaced by a fresh arm");
        scheduler.ReturnNull = false;

        scheduler.Next = new ScriptedDeadline { IsExpired = true };
        consumer.Replace(One(0x12, 50));
        consumer.NoteReceived(0x12);

        scheduler.Next = new ScriptedDeadline { IsCancelled = true };
        consumer.Replace(One(0x13, 50));
        consumer.NoteReceived(0x13);

        scheduler.Next = new ScriptedDeadline { RearmResult = false };
        consumer.Replace(One(0x14, 50));
        consumer.NoteReceived(0x14);

        scheduler.Next = new ScriptedDeadline { RearmResult = true };
        consumer.Replace(One(0x15, 50));
        consumer.NoteReceived(0x15);

        scheduler.Armed.Should().HaveCount(9, "each watch is armed, and each dead deadline is armed again");
        missed.Should().BeEmpty();
    }

    [Fact]
    public void A_Timeout_With_Nobody_Listening_Rearms_And_A_Removed_Watch_Does_Not()
    {
        var scheduler = new ScriptedScheduler { ReturnNull = true };
        using var consumer = new HeartbeatConsumer(scheduler);
        consumer.Replace(One(0x11, 50));
        scheduler.Armed.Should().HaveCount(1);

        scheduler.Armed[0].Callback();
        scheduler.Armed.Should().HaveCount(2, "silence rearms even when TimedOut has no subscriber");

        var orphan = scheduler.Armed[0].Callback;
        consumer.Replace(new Dictionary<byte, TimeSpan>());
        orphan();
        scheduler.Armed.Should().HaveCount(2, "the watch was gone, so the late timeout does not arm another");

        consumer.Replace(One(0x11, 50));
        consumer.Dispose();
        consumer.IsWatching(0x11).Should().BeFalse();
    }

    private static Dictionary<byte, TimeSpan> One(byte id, int ms)
        => new() { [id] = TimeSpan.FromMilliseconds(ms) };

    private sealed class ScriptedScheduler : IDeadlineScheduler
    {
        public List<(TimeSpan Timeout, Action Callback)> Armed { get; } = new();
        public ScriptedDeadline? Next { get; set; }
        public bool ReturnNull { get; set; }

        public IDeadline Arm(TimeSpan timeout, Action onExpired)
        {
            Armed.Add((timeout, onExpired));
            if (ReturnNull) return null!;
            var deadline = Next ?? new ScriptedDeadline();
            Next = null;
            return deadline;
        }
    }

    private sealed class ScriptedDeadline : IDeadline
    {
        public bool IsExpired { get; set; }
        public bool IsCompleted { get; set; }
        public bool IsCancelled { get; set; }
        public bool RearmResult { get; set; } = true;
        public int Disposed { get; private set; }

        public bool Rearm(TimeSpan timeout) => RearmResult;
        public bool Complete() => false;
        public void Dispose() => Disposed++;
    }
}
