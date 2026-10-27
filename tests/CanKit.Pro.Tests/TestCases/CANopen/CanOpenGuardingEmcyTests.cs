using System;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

// #423 / FR-CO-011: optional 8130h uses the same aggregate error register and EMCY chain as
// the node's PDO errors. Time advances only after actor barriers; an application EMCY is the
// ordering witness for every no-duplicate assertion.
public partial class CanOpenPdoEngineTests
{
    private static readonly CanOpenNodeOptions GuardingEmcyOptions = new()
    {
        EmitGuardingErrorEmcy = true,
        RespondToNodeGuardingRtr = true,
    };

    private static void ConfigureLifeGuarding(ICanOpenNode node)
    {
        node.ObjectDictionary.WriteUnsigned(0x100C, 0, 50);
        node.ObjectDictionary.WriteUnsigned(0x100D, 0, 2);
        Settle(node);
    }

    private static async Task PollLifeGuardingAsync(Wire wire, ICanOpenNode node)
    {
        var id = CanOpenCobId.Heartbeat(Device);
        int expected = wire.Count(id) + 1;
        wire.SendRtr(id);
        await wire.WaitForCountAsync(id, expected);
        Settle(node);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Guarding_Emcy_Is_Opt_In_And_Timeout_Events_Are_Unchanged(bool lifeGuarding)
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var wire = new Wire(session, 2);
        var clock = new ManualTimeSource();
        using var node = OpenClocked(bus, Device, clock);
        await StartAsync(wire, node);
        await wire.WaitForCountAsync(CanOpenCobId.Heartbeat(Device), 1);
        var timedOut = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        node.HeartbeatTimeout += (_, _) => timedOut.TrySetResult(true);
        node.LifeGuardingEvent += (_, e) =>
        {
            if (e.State == LifeGuardingState.Occurred) timedOut.TrySetResult(true);
        };
        if (lifeGuarding)
        {
            ConfigureLifeGuarding(node);
            await PollLifeGuardingAsync(wire, node);
        }
        else node.AddHeartbeatConsumer(1, TimeSpan.FromMilliseconds(100));
        Advance(clock, node, TimeSpan.FromMilliseconds(101));
        await timedOut.Task.WaitAsync(ShortTimeout);
        await node.SendEmcyAsync(0x1000, 0);
        await ExpectEmcyAsync(wire, 1, 0x1000, 0);
        wire.Count(EmcyId).Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Heartbeat_Emcy_Is_Once_Per_Lapse_And_Preserves_Application_Bits_On_Recovery(bool guardedReply)
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var wire = new Wire(session, 2);
        var clock = new ManualTimeSource();
        using var node = OpenClocked(bus, Device, clock, GuardingEmcyOptions);
        await StartAsync(wire, node);
        node.AddHeartbeatConsumer(1, TimeSpan.FromMilliseconds(100));
        if (guardedReply) node.StartNodeGuardingConsumer(1, TimeSpan.FromSeconds(10), 1);
        Advance(clock, node, TimeSpan.FromMilliseconds(101));
        await ExpectEmcyAsync(wire, 1, 0x8130, Communication);
        Advance(clock, node, TimeSpan.FromMilliseconds(100)); // repeated timeout, same error event
        await node.SendEmcyAsync(0x1000, 0x04);
        await ExpectEmcyAsync(wire, 2, 0x1000, 0x15);
        await Inject(node, CanOpenCobId.Heartbeat(1), (byte)NmtState.PreOperational);
        await ExpectEmcyAsync(wire, 3, ErrorReset, 0x04);
        node.ObjectDictionary.ReadUnsigned(0x1001, 0).Should().Be(0x04);
        wire.Count(EmcyId).Should().Be(3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Heartbeat_Emcy_Only_Resets_When_The_Last_Producer_Recovers(bool removeWatch)
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var wire = new Wire(session, 2);
        var clock = new ManualTimeSource();
        using var node = OpenClocked(bus, Device, clock, GuardingEmcyOptions);
        await StartAsync(wire, node);
        node.AddHeartbeatConsumer(1, TimeSpan.FromMilliseconds(100));
        node.AddHeartbeatConsumer(2, TimeSpan.FromMilliseconds(100));
        Advance(clock, node, TimeSpan.FromMilliseconds(101));
        await ExpectEmcyAsync(wire, 1, 0x8130, Communication);
        await ExpectEmcyAsync(wire, 2, 0x8130, Communication);
        if (removeWatch) node.RemoveHeartbeatConsumer(1);
        else await Inject(node, CanOpenCobId.Heartbeat(1), (byte)NmtState.PreOperational);
        await node.SendEmcyAsync(0x1000, 0);
        await ExpectEmcyAsync(wire, 3, 0x1000, Communication);
        await Inject(node, CanOpenCobId.Heartbeat(2), (byte)NmtState.PreOperational);
        await ExpectEmcyAsync(wire, 4, ErrorReset, 0);
        wire.Count(EmcyId).Should().Be(4);
    }

    [Fact]
    public async Task Heartbeat_Emcy_Reconfiguration_Restarts_The_Watch_But_An_Unchanged_Write_Does_Not()
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var wire = new Wire(session, 2);
        var clock = new ManualTimeSource();
        using var node = OpenClocked(bus, Device, clock, GuardingEmcyOptions);
        await StartAsync(wire, node);
        node.AddHeartbeatConsumer(1, TimeSpan.FromMilliseconds(100));
        Advance(clock, node, TimeSpan.FromMilliseconds(101));
        await ExpectEmcyAsync(wire, 1, 0x8130, Communication);
        node.AddHeartbeatConsumer(1, TimeSpan.FromMilliseconds(100));
        await node.SendEmcyAsync(0x1000, 0);
        await ExpectEmcyAsync(wire, 2, 0x1000, Communication);
        node.AddHeartbeatConsumer(1, TimeSpan.FromMilliseconds(200));
        Settle(node);
        await ExpectEmcyAsync(wire, 3, ErrorReset, 0);
        Advance(clock, node, TimeSpan.FromMilliseconds(201));
        await ExpectEmcyAsync(wire, 4, 0x8130, Communication);
        node.RemoveHeartbeatConsumer(1);
        Settle(node);
        await ExpectEmcyAsync(wire, 5, ErrorReset, 0);
        Advance(clock, node, TimeSpan.FromSeconds(1));
        await node.SendEmcyAsync(0x1000, 0);
        await ExpectEmcyAsync(wire, 6, 0x1000, 0);
        wire.Count(EmcyId).Should().Be(6);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Guarding_Emcy_Reports_Only_Still_Active_Faults_When_Reenabled(bool lifeGuarding, bool recoverWhileDisabled)
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var wire = new Wire(session, 2);
        var clock = new ManualTimeSource();
        using var node = OpenClocked(bus, Device, clock, GuardingEmcyOptions);
        await StartAsync(wire, node);
        await wire.WaitForCountAsync(CanOpenCobId.Heartbeat(Device), 1);
        node.ObjectDictionary.WriteUnsigned(0x1014, 0, EmcyId | CanOpenCobId.InvalidBit);
        if (lifeGuarding)
        {
            ConfigureLifeGuarding(node);
            await PollLifeGuardingAsync(wire, node);
        }
        else node.AddHeartbeatConsumer(1, TimeSpan.FromMilliseconds(100));
        Advance(clock, node, TimeSpan.FromMilliseconds(101));
        if (recoverWhileDisabled)
        {
            if (lifeGuarding) await PollLifeGuardingAsync(wire, node);
            else await Inject(node, CanOpenCobId.Heartbeat(1), (byte)NmtState.PreOperational);
        }
        node.ObjectDictionary.WriteUnsigned(0x1014, 0, EmcyId);
        Settle(node);
        if (!recoverWhileDisabled) await ExpectEmcyAsync(wire, 1, 0x8130, Communication);
        await node.SendEmcyAsync(0x1000, 0);
        await ExpectEmcyAsync(wire, recoverWhileDisabled ? 1 : 2, 0x1000,
            recoverWhileDisabled ? (byte)0 : Communication);
        if (!recoverWhileDisabled)
        {
            node.ObjectDictionary.WriteUnsigned(0x1014, 0, EmcyId | CanOpenCobId.InvalidBit);
            node.ObjectDictionary.WriteUnsigned(0x1014, 0, EmcyId);
            Settle(node);
            if (lifeGuarding) await PollLifeGuardingAsync(wire, node);
            else await Inject(node, CanOpenCobId.Heartbeat(1), (byte)NmtState.PreOperational);
            await ExpectEmcyAsync(wire, 3, ErrorReset, 0); // enabling again did not duplicate the lapse
        }
        wire.Count(EmcyId).Should().Be(recoverWhileDisabled ? 1 : 3);
    }

    [Theory]
    [InlineData("poll")]
    [InlineData("guard-time")]
    [InlineData("factor")]
    [InlineData("heartbeat-mode")]
    public async Task Life_Guarding_Emcy_Ends_On_A_Poll_Or_When_Guarding_Is_Disabled(string recovery)
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var wire = new Wire(session, 2);
        var clock = new ManualTimeSource();
        using var node = OpenClocked(bus, Device, clock, GuardingEmcyOptions);
        await StartAsync(wire, node);
        await wire.WaitForCountAsync(CanOpenCobId.Heartbeat(Device), 1);
        ConfigureLifeGuarding(node);
        await PollLifeGuardingAsync(wire, node);
        Advance(clock, node, TimeSpan.FromMilliseconds(101));
        await ExpectEmcyAsync(wire, 1, 0x8130, Communication);
        Advance(clock, node, TimeSpan.FromMilliseconds(100));
        await node.SendEmcyAsync(0x1000, 0);
        await ExpectEmcyAsync(wire, 2, 0x1000, Communication);
        switch (recovery)
        {
            case "poll": await PollLifeGuardingAsync(wire, node); break;
            case "guard-time": node.ObjectDictionary.WriteUnsigned(0x100C, 0, 0); break;
            case "factor": node.ObjectDictionary.WriteUnsigned(0x100D, 0, 0); break;
            default: node.StartHeartbeatProducer(TimeSpan.FromMilliseconds(20)); break;
        }
        Settle(node);
        await ExpectEmcyAsync(wire, 3, ErrorReset, 0);
        wire.Count(EmcyId).Should().Be(3);
    }

    [Fact]
    public async Task Life_Guarding_Emcy_Is_Reported_After_Enable_And_A_Reset_Does_Not_Emit_An_Error_Reset()
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var wire = new Wire(session, 2);
        var clock = new ManualTimeSource();
        using var node = OpenClocked(bus, Device, clock, GuardingEmcyOptions);
        await StartAsync(wire, node);
        await wire.WaitForCountAsync(CanOpenCobId.Heartbeat(Device), 1);
        node.ObjectDictionary.WriteUnsigned(0x1014, 0, EmcyId | CanOpenCobId.InvalidBit);
        ConfigureLifeGuarding(node);
        await PollLifeGuardingAsync(wire, node);
        Advance(clock, node, TimeSpan.FromMilliseconds(101));
        node.ObjectDictionary.WriteUnsigned(0x1014, 0, EmcyId);
        Settle(node);
        await ExpectEmcyAsync(wire, 1, 0x8130, Communication);
        wire.SendNmt(NmtCommand.ResetCommunication, Device);
        await wire.WaitForCountAsync(CanOpenCobId.Heartbeat(Device), 3); // boot-up, poll reply, reset boot-up
        Settle(node);
        await node.SendEmcyAsync(0x1000, 0);
        await ExpectEmcyAsync(wire, 2, 0x1000, 0);
        wire.Count(EmcyId).Should().Be(2);
    }

    [Fact]
    public async Task Heartbeat_Emcy_Reset_Forgets_Lapses_But_Restores_The_Configured_Watch()
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var wire = new Wire(session, 2);
        var clock = new ManualTimeSource();
        using var node = OpenClocked(bus, Device, clock, GuardingEmcyOptions);
        await StartAsync(wire, node);
        await wire.WaitForCountAsync(CanOpenCobId.Heartbeat(Device), 1);
        node.AddHeartbeatConsumer(1, TimeSpan.FromMilliseconds(100));
        node.StoreParameters();
        Advance(clock, node, TimeSpan.FromMilliseconds(101));
        await ExpectEmcyAsync(wire, 1, 0x8130, Communication);
        wire.SendNmt(NmtCommand.ResetCommunication, Device);
        await wire.WaitForCountAsync(CanOpenCobId.Heartbeat(Device), 2);
        Settle(node);
        await node.SendEmcyAsync(0x1000, 0);
        await ExpectEmcyAsync(wire, 2, 0x1000, 0);
        Advance(clock, node, TimeSpan.FromMilliseconds(101));
        await ExpectEmcyAsync(wire, 3, 0x8130, Communication);
        node.RemoveHeartbeatConsumer(1);
        Settle(node);
        await ExpectEmcyAsync(wire, 4, ErrorReset, 0);
        wire.Count(EmcyId).Should().Be(4);
    }

    [Fact]
    public void Guarding_Emcy_Option_Is_Preserved_By_Existing_Copy_Methods()
    {
        new CanOpenNodeOptions().EmitGuardingErrorEmcy.Should().BeFalse();
        GuardingEmcyOptions.With(sdoTimeout: TimeSpan.FromSeconds(2)).EmitGuardingErrorEmcy.Should().BeTrue();
        GuardingEmcyOptions.WithLss(null).EmitGuardingErrorEmcy.Should().BeTrue();
        GuardingEmcyOptions.WithSrdoConfigurationCheckInterval(null).EmitGuardingErrorEmcy.Should().BeTrue();
    }
}
