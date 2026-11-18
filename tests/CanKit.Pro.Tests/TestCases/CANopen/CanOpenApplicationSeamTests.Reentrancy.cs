using System;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Application;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

// What an application's callback changes while the node is still acting on what it told it: a
// callback that ends the condition it was told about, and one that disposes the node, in front of
// the work the node had lined up behind the callback.
public partial class CanOpenApplicationSeamTests
{
    private static readonly byte[] HeartbeatTimeoutCode = { 0x30, 0x81 };

    /// <summary>Error behaviour and the guarding EMCY: a heartbeat timeout or a life-guarding lapse
    /// is a communication failure, and it raises EMCY <c>8130h</c> as well.</summary>
    private static CanOpenNodeOptions WithGuardingEmcy(params ICanOpenApplication[] applications)
        => applications.Aggregate(new CanOpenNodeOptions { EnableErrorBehavior = true, EmitGuardingErrorEmcy = true },
            (options, application) => options.WithApplication(application));

    private static bool CarriesCode(byte[] emcy, byte[] code) => emcy[0] == code[0] && emcy[1] == code[1];

    private static Task<bool> FailureActiveAsync(CanOpenNode node, RecordingApplication app)
        => node.ActorForTests.PostAsync(() => app.Host!.CommunicationFailureActive);

    // -----------------------------------------------------------------------------------------
    // A callback that ends the communication failure it was told about.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_Watch_Dropped_From_The_Failure_Callback_Leaves_No_Heartbeat_Error()
    {
        var app = Watcher();
        app.WhenFailureChanged = active =>
        {
            if (active) app.Host!.ObjectDictionary.WriteUnsigned(0x1016, 1, 0);
        };
        using var watch = new ProducerWatchRig(WithGuardingEmcy(app));
        await watch.BeatAsync();

        watch.Silence();
        await EmcyChainSettledAsync(watch.Rig.Node);

        Failures(app).Should().Equal("failure True", "failure False");
        watch.Rig.Wire.Payloads(SlaveEmcy).Should().NotContain(emcy => CarriesCode(emcy, HeartbeatTimeoutCode),
            "the producer is no longer watched, so nothing would ever end its error");
        ErrorRegister(watch.Rig.Node).Should().Be(0);
        (await FailureActiveAsync(watch.Rig.Node, app)).Should().BeFalse();
    }

    [Fact]
    public async Task A_Watch_Dropped_From_The_State_Change_The_Failure_Caused_Leaves_No_Heartbeat_Error()
    {
        // The error behaviour takes the node to Pre-Operational, and the profile drops the watch when
        // it hears of that change: after the publish, before the node would raise the error.
        var app = new RecordingApplication("leaver");
        app.WhenNmtChanged = (previous, _) =>
        {
            if (previous == NmtState.Operational) app.Host!.ObjectDictionary.WriteUnsigned(0x1016, 1, 0);
        };
        using var watch = new ProducerWatchRig(WithGuardingEmcy(app));
        watch.Rig.Od.WriteUnsigned(0x1029, 1, 0);
        await watch.Rig.StartAsync();
        Settle(watch.Rig.Node);
        await watch.BeatAsync();

        watch.Silence();
        await EmcyChainSettledAsync(watch.Rig.Node);

        watch.Rig.Node.State.Should().Be(NmtState.PreOperational, "the failure was there when the node acted on it");
        watch.Rig.Wire.Payloads(SlaveEmcy).Should().NotContain(emcy => CarriesCode(emcy, HeartbeatTimeoutCode));
        ErrorRegister(watch.Rig.Node).Should().Be(0);
    }

    [Fact]
    public async Task Life_Guarding_Switched_Off_From_The_Failure_Callback_Leaves_No_Error()
    {
        var app = Watcher();
        app.WhenFailureChanged = active =>
        {
            if (active) app.Host!.ObjectDictionary.WriteUnsigned(0x100D, 0, 0);
        };
        var clock = new ManualTimeSource();
        using var rig = new EntryRig(WithGuardingEmcy(app), clock);
        rig.Od.WriteUnsigned(0x100C, 0, 50);
        rig.Od.WriteUnsigned(0x100D, 0, 2);
        Settle(rig.Node);
        await PollLifeGuarding(rig.Node);

        Advance(clock, rig.Node, TimeSpan.FromMilliseconds(101));
        await EmcyChainSettledAsync(rig.Node);

        Failures(app).Should().Equal("failure True", "failure False");
        rig.Wire.Payloads(SlaveEmcy).Should().NotContain(emcy => CarriesCode(emcy, HeartbeatTimeoutCode),
            "life guarding is off, so no poll would ever end its error");
        ErrorRegister(rig.Node).Should().Be(0);
        (await FailureActiveAsync(rig.Node, app)).Should().BeFalse();
    }
}
