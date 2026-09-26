using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CanKit.Pro.Actor;
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

    [Fact]
    public void Rejects_A_Null_Collaborator_And_A_Negative_Interval()
    {
        var actor = new RecordingActor();
        Action noActor = () => _ = new HeartbeatProducer(null!, () => true, () => { });
        noActor.Should().Throw<ArgumentNullException>().WithParameterName("actor");
        Action noGate = () => _ = new HeartbeatProducer(actor, null!, () => { });
        noGate.Should().Throw<ArgumentNullException>().WithParameterName("canEmit");
        Action noEmit = () => _ = new HeartbeatProducer(actor, () => true, null!);
        noEmit.Should().Throw<ArgumentNullException>().WithParameterName("emit");

        using var producer = new HeartbeatProducer(actor, () => true, () => { });
        Action negative = () => producer.Apply(TimeSpan.FromTicks(-1));
        negative.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("interval");
    }

    [Fact]
    public void Restarting_Before_A_Tick_Is_Armed_Still_Stops_The_Old_Callback()
    {
        var actor = new RecordingActor { FailSchedule = true };
        using var producer = new HeartbeatProducer(actor, () => true, () => { });

        Action apply = () => producer.Apply(TimeSpan.FromMilliseconds(10));
        apply.Should().Throw<InvalidOperationException>();
        producer.Interval.Should().Be(TimeSpan.FromMilliseconds(10));

        // The failed arm left no handle. Restart still has to dispose that empty handle and
        // then fail the same way, rather than emit on a schedule that never landed.
        Action restart = () => producer.RestartCycle();
        restart.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task A_Superseded_Tick_Does_Not_Emit_And_A_Blocked_Node_Does_Not_Either()
    {
        var actor = new RecordingActor();
        var sent = 0;
        var allow = true;
        using var producer = new HeartbeatProducer(actor, () => allow, () => sent++);

        producer.Apply(TimeSpan.FromMilliseconds(100));
        var superseded = actor.Callbacks[0];
        producer.Apply(TimeSpan.FromMilliseconds(80));
        superseded();
        sent.Should().Be(0, "the tick belonged to the interval that was replaced");

        allow = false;
        actor.Callbacks[1]();
        sent.Should().Be(0, "the node asked the producer not to emit");
        actor.Callbacks.Should().HaveCount(2, "a blocked tick does not arm the next one");

        allow = true;
        actor.Callbacks[1]();
        sent.Should().Be(1);
    }

    /// <summary>Records every timer the producer arms, so a test can run a callback the producer
    /// has already replaced.</summary>
    private sealed class RecordingActor : IProtocolActor
    {
        public List<Action> Callbacks { get; } = new();
        public bool FailSchedule { get; set; }
        public event EventHandler<Exception>? BackgroundExceptionOccurred { add { } remove { } }

        public void Post(Action work) => work();
        public Task PostAsync(Action work) { work(); return Task.CompletedTask; }
        public Task<T> PostAsync<T>(Func<T> work) => Task.FromResult(work());

        public IDisposable Schedule(TimeSpan delay, Action callback)
        {
            if (FailSchedule) throw new InvalidOperationException("The actor refused the timer.");
            Callbacks.Add(callback);
            return new CallbackHandle();
        }

        public void Dispose() { }

        private sealed class CallbackHandle : IDisposable
        {
            public void Dispose() { }
        }
    }
}
