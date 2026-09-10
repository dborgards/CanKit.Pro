using System;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Pro.Actor;
using CanKit.Pro.Reliability;
using CanKit.Pro.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace CanKit.Pro.Tests.TestCases;

/// <summary>
/// Verifies the L2 bus-state monitor (CanKit.Pro.Reliability, arc42 §5.3 / ADR-11,
/// SRS FR-RAW-051): a self-rearming poll driven through the actor's loop pushes edge-triggered
/// BusState transitions, plus the pure IsTransmitBlocked/IsDegraded helpers.
///
/// The controller state is driven through <see cref="ControllableBus"/>. No CAN adapter exposes a
/// public way to force a real controller into BusOff — reaching one that does would mean either
/// real broken hardware or reflection into an adapter's private statics, and the monitor's
/// contract is about <c>ICanBus.BusState</c>, not about how any one adapter arrives there.
/// </summary>
public class BusStateMonitorTests : IClassFixture<VirtualAdapterFixture>
{
    private static readonly TimeSpan Bounded = TimeSpan.FromSeconds(5);

    private static ControllableBus OpenBus() => ControllableBus.EchoCapable(VirtualAdapterFixture.NewSession("busmonitor"));

    [Fact]
    public void CurrentState_Reflects_The_Bus_State_At_Construction()
    {
        using var bus = OpenBus();
        using var actor = new ProtocolActor();

        // Establish a known, non-default state before the monitor is even constructed.
        bus.BusState = BusState.ErrPassive;

        using var monitor = new BusStateMonitor(bus, actor);

        monitor.CurrentState.Should().Be(BusState.ErrPassive,
            "the monitor baselines CurrentState synchronously from the bus in its constructor");
    }

    [Fact]
    public async Task StateChanged_Is_Not_Raised_While_The_State_Is_Unchanged()
    {
        using var bus = OpenBus();
        using var actor = new ProtocolActor();
        using var monitor = new BusStateMonitor(bus, actor, TimeSpan.FromMilliseconds(20));

        var changes = 0;
        monitor.StateChanged += (_, _) => Interlocked.Increment(ref changes);

        // Let many poll ticks run without ever changing the bus state.
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        Volatile.Read(ref changes).Should().Be(0, "an unchanged state must never raise an edge-triggered event");
    }

    [Fact]
    public async Task StateChanged_Fires_On_A_Transition_Observed_By_The_Poll()
    {
        using var bus = OpenBus();
        using var actor = new ProtocolActor();
        using var monitor = new BusStateMonitor(bus, actor, TimeSpan.FromMilliseconds(20));

        var busOff = new TaskCompletionSource<BusStateChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.StateChanged += (_, e) => { if (e.Current == BusState.BusOff) busOff.TrySetResult(e); };

        bus.BusState = BusState.BusOff;

        (await Task.WhenAny(busOff.Task, Task.Delay(Bounded))).Should().Be(busOff.Task,
            "the self-rearming poll must observe the BusOff transition");
        var args = await busOff.Task;
        args.Previous.Should().Be(BusState.ErrActive);
        args.Current.Should().Be(BusState.BusOff);
        args.Current.IsTransmitBlocked().Should().BeTrue();
        monitor.CurrentState.Should().Be(BusState.BusOff);
    }

    [Fact]
    public async Task StateChanged_Fires_On_Recovery_Back_Down_From_BusOff()
    {
        using var bus = OpenBus();
        using var actor = new ProtocolActor();

        bus.BusState = BusState.BusOff;
        using var monitor = new BusStateMonitor(bus, actor, TimeSpan.FromMilliseconds(20));

        var recovered = new TaskCompletionSource<BusStateChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.StateChanged += (_, e) => { if (e.Current == BusState.ErrActive) recovered.TrySetResult(e); };

        // Recovery (BusOff -> ErrActive) matters too: protocols need to know when to resume.
        bus.BusState = BusState.ErrActive;

        (await Task.WhenAny(recovered.Task, Task.Delay(Bounded))).Should().Be(recovered.Task,
            "recovering transitions must be reported, not only degrading ones");
        var args = await recovered.Task;
        args.Previous.Should().Be(BusState.BusOff);
        args.Current.Should().Be(BusState.ErrActive);
    }

    [Fact]
    public async Task Dispose_Stops_Further_StateChanged_Events_And_Is_Idempotent()
    {
        using var bus = OpenBus();
        using var actor = new ProtocolActor();
        var monitor = new BusStateMonitor(bus, actor, TimeSpan.FromMilliseconds(20));

        var changes = 0;
        monitor.StateChanged += (_, _) => Interlocked.Increment(ref changes);

        monitor.Dispose();
        monitor.Dispose(); // idempotent

        // Change the state only after disposing: with the poll stopped, no event may arrive.
        bus.BusState = BusState.BusOff;
        await Task.Delay(TimeSpan.FromMilliseconds(150)); // several poll intervals

        Volatile.Read(ref changes).Should().Be(0, "a disposed monitor must stop polling and raising events");
    }

    [Theory]
    [InlineData(BusState.None, false)]
    [InlineData(BusState.ErrActive, false)]
    [InlineData(BusState.ErrWarning, false)]
    [InlineData(BusState.ErrPassive, false)]
    [InlineData(BusState.BusOff, true)]
    [InlineData(BusState.Unknown, false)]
    public void IsTransmitBlocked_Is_True_Only_For_BusOff(BusState state, bool expected)
        => state.IsTransmitBlocked().Should().Be(expected);

    [Theory]
    [InlineData(BusState.None, false)]
    [InlineData(BusState.ErrActive, false)]
    [InlineData(BusState.ErrWarning, true)]
    [InlineData(BusState.ErrPassive, true)]
    [InlineData(BusState.BusOff, true)]
    // Unknown means "we could not determine the controller state", and answering a health
    // question with "healthy" on the strength of no information is the one answer that can never
    // be justified -- a caller told "healthy" proceeds as if a bus that may already be off were
    // fine. None stays healthy: it is the "no error condition" reading, not an absence of one.
    [InlineData(BusState.Unknown, true)]
    public void IsDegraded_Is_True_For_Warning_Passive_BusOff_And_Unknown(BusState state, bool expected)
        => state.IsDegraded().Should().Be(expected);

    [Fact]
    public void Constructor_Rejects_A_Nonpositive_Poll_Interval()
    {
        using var bus = OpenBus();
        using var actor = new ProtocolActor();

        Action zero = () => new BusStateMonitor(bus, actor, TimeSpan.Zero);
        Action negative = () => new BusStateMonitor(bus, actor, TimeSpan.FromMilliseconds(-1));

        zero.Should().Throw<ArgumentOutOfRangeException>();
        negative.Should().Throw<ArgumentOutOfRangeException>();
    }
}
