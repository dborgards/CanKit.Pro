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
}
