using System;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;
using static CanKit.Pro.Tests.TestCases.CANopen.FlyingMasterRig;

namespace CanKit.Pro.Tests.TestCases.CANopen;

public partial class CanOpenFlyingMasterTests
{
    private static CanOpenNodeOptions PeriodicForceOptions() => new CanOpenNodeOptions
    {
        EnablePeriodicFlyingMasterForce = true
    }.With();

    [Fact]
    public void Periodic_Force_Rejects_Explicit_Echo_Mode_At_Open()
    {
        using var bus = VirtualAdapterFixture.Open(NewSession(), 0, ChannelWorkMode.Echo);
        Action open = () => CanOpen.OpenNode(bus, LeftId, PeriodicForceOptions()).Dispose();
        open.Should().Throw<ArgumentException>().WithParameterName("options");
        using var compatible = CanOpen.OpenNode(bus, LeftId);
    }

    [Fact]
    public async Task Periodic_Force_Uses_The_Live_Cycle_Without_A_Claim_Or_Reset()
    {
        using var rig = OpenMaster(PeriodicForceOptions());
        Tighten(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Node.FlyingMasterRole == FlyingMasterRole.Active, 800, "the node won the election");
        rig.Log.Clear();
        var od = rig.Node.ObjectDictionary;
        od.WriteUnsigned(Timing, 6, 100);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(99));
        rig.Log.Snapshot().Should().NotContain(f => f.Id == CanOpenCobId.FlyingMasterForce);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(1));
        rig.Log.Snapshot().Should().ContainSingle(f => f.Id == CanOpenCobId.FlyingMasterForce);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(100));
        od.WriteUnsigned(Timing, 6, 0);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(200));
        var frames = rig.Log.Snapshot();
        frames.Count(f => f.Id == CanOpenCobId.FlyingMasterForce && f.Data.Length == 0).Should().Be(2);
        frames.Should().NotContain(f => f.Id == CanOpenCobId.FlyingMasterTrigger || f.Id == CanOpenCobId.FlyingMasterClaim);
        Resets(rig.Log).Should().Be(0);
        rig.Node.FlyingMasterRole.Should().Be(FlyingMasterRole.Active);
    }

    [Fact]
    public async Task A_Held_Periodic_Force_Expires_Without_Queuing_Another()
    {
        using var rig = OpenMaster(PeriodicForceOptions());
        Tighten(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Node.FlyingMasterRole == FlyingMasterRole.Active, 800, "the node won the election");
        rig.Log.Clear();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Node.BeforeControlFrameSendForTests = id => id == CanOpenCobId.FlyingMasterForce && entered.TrySetResult(true)
            ? release.Task : Task.CompletedTask;
        rig.Node.ObjectDictionary.WriteUnsigned(Timing, 6, 100);
        await rig.Witness.SettleAsync();
        try
        {
            rig.Clock.Advance(TimeSpan.FromMilliseconds(100));
            await entered.Task.WaitAsync(ShortTimeout);
            rig.Clock.Advance(TimeSpan.FromMilliseconds(100));
            await rig.Witness.SettleAsync();
        }
        finally
        {
            release.TrySetResult(true);
        }
        await QuiesceAsync(rig.Witness, null);
        rig.Log.Snapshot().Should().NotContain(f => f.Id == CanOpenCobId.FlyingMasterForce);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(100));
        rig.Log.Snapshot().Should().ContainSingle(f => f.Id == CanOpenCobId.FlyingMasterForce);
    }

    [Fact]
    public async Task Rejoined_Active_Masters_Only_The_Force_Recipient_Orders_Reset()
    {
        var clock = new ManualTimeSource();
        var a = NewSession();
        var b = NewSession();
        using var aBus = Open(a, 0);
        using var bBus = Open(b, 0);
        using var aTap = Open(a, 1);
        using var bTap = Open(b, 1);
        using var left = OpenClockedNode(aBus, LeftId, clock, out var aService, options: PeriodicForceOptions());
        using var right = OpenClockedNode(bBus, RightId, clock, out var bService, options: PeriodicForceOptions());
        var aw = new ActorWitness(left, aTap, WitnessForLeft, aService);
        var bw = new ActorWitness(right, bTap, WitnessForRight, bService);
        foreach (var node in new[] { left, right })
        {
            Tighten(node);
            node.ObjectDictionary.WriteUnsigned(Startup, 0, SuppressSelfStart | SuppressSlaveStart);
        }
        left.ObjectDictionary.WriteUnsigned(Timing, 6, 300);
        right.ObjectDictionary.WriteUnsigned(Timing, 6, 2000);
        left.StartFlyingMaster(1, Heartbeat);
        right.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(clock, aw, bw, () => left.FlyingMasterRole == FlyingMasterRole.Active
            && right.FlyingMasterRole == FlyingMasterRole.Active, 800, "both isolated masters win");

        // Reconnect the two segments for the control traffic in question. A tap on each
        // segment sees only local sends, so the assertion identifies the reset's origin.
        using var sentLeft = new FrameLog(aTap);
        using var sentRight = new FrameLog(bTap);
        aTap.FrameObserved += (_, e) => ForwardElectionFrame(e, bTap);
        bTap.FrameObserved += (_, e) => ForwardElectionFrame(e, aTap);

        await AdvanceAsync(clock, aw, bw, TimeSpan.FromMilliseconds(300));
        sentLeft.Snapshot().Should().ContainSingle(f => f.Id == CanOpenCobId.FlyingMasterForce && f.Data.Length == 0);
        Resets(sentRight).Should().Be(1);
        Resets(sentLeft).Should().Be(0);
        left.FlyingMasterRole.Should().Be(FlyingMasterRole.Delaying);
        right.FlyingMasterRole.Should().Be(FlyingMasterRole.Delaying);
        await UntilAsync(clock, aw, bw, () => left.FlyingMasterRole == FlyingMasterRole.Standby
            && right.FlyingMasterRole == FlyingMasterRole.Active, 800, "the higher-priority master wins the warm election");

        // The winner's next probe must leave its standby and heartbeat watch intact.
        right.ObjectDictionary.WriteUnsigned(Timing, 6, 100);
        await AdvanceAsync(clock, aw, bw, TimeSpan.FromMilliseconds(100));
        sentRight.Snapshot().Should().ContainSingle(f => f.Id == CanOpenCobId.FlyingMasterForce);
        left.FlyingMasterRole.Should().Be(FlyingMasterRole.Standby);
        left.ActiveFlyingMasterNodeId.Should().Be(RightId);
        Resets(sentLeft).Should().Be(0);
        Resets(sentRight).Should().Be(1);
    }

    private static void ForwardElectionFrame(CanReceiveDataView e, ICanBus destination)
    {
        var frame = e.CanFrame;
        if (frame.ID is (int)CanOpenCobId.NmtCommand or (int)CanOpenCobId.FlyingMasterForce
            or (int)CanOpenCobId.FlyingMasterTrigger or (int)CanOpenCobId.FlyingMasterClaim
            or (int)CanOpenCobId.FlyingMasterDetect)
            destination.Transmit(CanFrame.Classic(frame.ID, frame.Data.ToArray()));
    }
}
