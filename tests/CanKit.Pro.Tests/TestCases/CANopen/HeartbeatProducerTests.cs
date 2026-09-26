using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CanKit.Pro.CANopen.Heartbeat;
using CanKit.Pro.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

/// <summary>
/// The heartbeat producer on its own. No consumer is constructed: the module only sends.
/// </summary>
public class HeartbeatProducerTests
{
    [Fact]
    public async Task Sends_On_Each_Interval_And_Stops_When_Cleared()
    {
        using var clock = new VirtualClock();
        var actor = clock.NewActor();
        var sent = new List<TimeSpan>();
        using var producer = new HeartbeatProducer(actor, () => true, () => sent.Add(clock.Elapsed));

        await actor.PostAsync(() => producer.Apply(TimeSpan.FromMilliseconds(100)));
        sent.Should().BeEmpty("the first heartbeat waits out the interval");

        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(100));
        sent.Should().Equal(TimeSpan.FromMilliseconds(100));

        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(100));
        sent.Should().Equal(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200));

        await actor.PostAsync(() => producer.Apply(TimeSpan.Zero));
        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(500));
        sent.Should().HaveCount(2, "clearing the interval stops the producer");
    }

    [Fact]
    public async Task Applying_The_Same_Interval_Keeps_The_Current_Period()
    {
        using var clock = new VirtualClock();
        var actor = clock.NewActor();
        var sent = new List<TimeSpan>();
        using var producer = new HeartbeatProducer(actor, () => true, () => sent.Add(clock.Elapsed));

        await actor.PostAsync(() => producer.Apply(TimeSpan.FromMilliseconds(100)));
        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(60));
        await actor.PostAsync(() => producer.Apply(TimeSpan.FromMilliseconds(100)));
        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(40));

        sent.Should().Equal(new[] { TimeSpan.FromMilliseconds(100) },
            "writing the interval that is already running does not start the period over");
    }

    [Fact]
    public async Task RestartCycle_Waits_A_Full_Interval_From_The_Restart()
    {
        using var clock = new VirtualClock();
        var actor = clock.NewActor();
        var sent = new List<TimeSpan>();
        using var producer = new HeartbeatProducer(actor, () => true, () => sent.Add(clock.Elapsed));

        await actor.PostAsync(() => producer.Apply(TimeSpan.FromMilliseconds(100)));
        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(40));
        await actor.PostAsync(() => producer.RestartCycle());
        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(99));
        sent.Should().BeEmpty("the period starts again at the restart");

        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(1));
        sent.Should().Equal(TimeSpan.FromMilliseconds(140));
    }
}
