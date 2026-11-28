using System;
using System.Collections.Generic;
using AwesomeAssertions;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

public partial class CanOpenApplicationSeamTests
{
    [Fact]
    public void Periodic_Ticks_Keep_Fractional_Phase_After_A_Stall_And_Can_Stop_Themselves()
    {
        using var bus = Open(NewSession(), 1);
        var clock = new ManualTimeSource(frequency: 1000);
        var app = new RecordingApplication("timer");
        using var node = OpenNode(bus, With(app), clock);
        var ticks = new List<TimeSpan>();
        Action stop = null!;
        using var timer = app.Host!.ArmPeriodic(TimeSpan.FromTicks(15000), () =>
        {
            ticks.Add(app.Host.Now);
            if (ticks.Count == 3) stop();
        });
        stop = timer.Dispose;

        Advance(clock, node, TimeSpan.FromMilliseconds(1));
        ticks.Should().BeEmpty();
        Advance(clock, node, TimeSpan.FromMilliseconds(1));
        Advance(clock, node, TimeSpan.FromMilliseconds(8));
        Advance(clock, node, TimeSpan.FromMilliseconds(1));
        Advance(clock, node, TimeSpan.FromMilliseconds(100));

        ticks.Should().Equal(TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(11));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_Periodic_Tick_Held_During_Attach_Can_Be_Cancelled(bool cancel)
    {
        using var bus = Open(NewSession(), 1);
        var clock = new ManualTimeSource();
        var states = new List<NmtState>();
        IDisposable? timer = null;
        var app = new RecordingApplication("timer");
        app.AttachmentResource = () => timer = app.Host!.ArmPeriodic(
            TimeSpan.FromMilliseconds(10), () => states.Add(app.Host.State));

        var holder = new RecordingApplication("holder")
        {
            OnAttach = host =>
            {
                clock.Advance(TimeSpan.FromMilliseconds(35));
                Settle(NodeOf(host));
                states.Should().BeEmpty();
                if (cancel) timer!.Dispose();
            },
        };
        using var node = OpenNode(bus, With(app, holder), clock);
        Advance(clock, node, TimeSpan.FromMilliseconds(4));
        states.Count.Should().Be(cancel ? 0 : 1);
        Advance(clock, node, TimeSpan.FromMilliseconds(1));
        states.Count.Should().Be(cancel ? 0 : 2);
        states.Should().AllSatisfy(state => state.Should().Be(NmtState.PreOperational));
    }

    [Fact]
    public void Periodic_Timers_Validate_Arguments_And_Handle_An_Unreachable_Due_Time()
    {
        using var bus = Open(NewSession(), 1);
        var clock = new ManualTimeSource();
        var app = new RecordingApplication("timer");
        using var node = OpenNode(bus, With(app), clock);
        var host = app.Host!;
        FluentActions.Invoking(() => host.ArmPeriodic(TimeSpan.Zero, () => { }))
            .Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("period");
        FluentActions.Invoking(() => host.ArmPeriodic(TimeSpan.FromTicks(-1), () => { }))
            .Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("period");
        FluentActions.Invoking(() => host.ArmPeriodic(TimeSpan.FromSeconds(1), null!))
            .Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be("onTick");

        Advance(clock, node, TimeSpan.FromSeconds(1));
        var ticks = 0;
        using var timer = host.ArmPeriodic(TimeSpan.MaxValue, () => ticks++);
        Advance(clock, node, TimeSpan.FromSeconds(1));
        ticks.Should().Be(0);
    }
}
