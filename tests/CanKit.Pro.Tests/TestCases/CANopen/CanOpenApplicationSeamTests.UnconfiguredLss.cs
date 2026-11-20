using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Lss;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

// FR-CO-011, #547: a profile's errors and the node's own communication errors raised while an LSS
// slave waits for its node-id are held in one list and go out behind the boot-up in the order they
// were raised; an error reset of either kind goes the way of the EMCY it ends.
public partial class CanOpenApplicationSeamTests
{
    private static EntryRig UnconfiguredGuardingLssRig(RecordingApplication app, ManualTimeSource clock)
        => new(new CanOpenNodeOptions { EmitGuardingErrorEmcy = true }.WithApplication(app).WithLss(new LssSlaveOptions()),
            clock, nodeId: CanOpenCobId.UnconfiguredNodeId);

    /// <summary>A heartbeat consumer for node 1 that times out at once, on the slave's clock.</summary>
    private static void TimeOutAHeartbeat(EntryRig rig, ManualTimeSource clock)
    {
        rig.Node.AddHeartbeatConsumer(1, TimeSpan.FromMilliseconds(100));
        Advance(clock, rig.Node, TimeSpan.FromMilliseconds(101));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_Profile_Error_And_A_Communication_Error_Held_Together_Go_Out_In_Raise_Order(bool profileFirst)
    {
        var app = Profile();
        var clock = new ManualTimeSource();
        using var rig = UnconfiguredGuardingLssRig(app, clock);
        Settle(rig.Node);
        if (profileFirst) await OnActor(rig.Node, () => app.Host!.RaiseError(0x5000, 0x04));
        TimeOutAHeartbeat(rig, clock);
        if (!profileFirst) await OnActor(rig.Node, () => app.Host!.RaiseError(0x5000, 0x04));
        rig.Node.State.Should().Be(NmtState.Initializing);
        rig.Wire.Sequence.Should().BeEmpty("an unconfigured slave transmits nothing");

        await ConfigureNodeIdAsync(rig);
        await rig.Wire.WaitForCountAsync(SlaveEmcy, 2);
        await EmcyChainSettledAsync(rig.Node);

        var frames = BootUpsAndEmcys(rig.Wire);
        frames.Select(f => f.CobId).Should().Equal(new[] { SlaveHeartbeat, SlaveEmcy, SlaveEmcy });
        // Both carry the register of their release: the communication bits 11h and the profile's 05h.
        var expected = profileFirst
            ? new[] { Emcy(0x5000, 0x15), Emcy(0x8130, 0x15) }
            : new[] { Emcy(0x8130, 0x15), Emcy(0x5000, 0x15) };
        frames[1].Data.Should().Equal(expected[0], "the first raised goes out first");
        frames[2].Data.Should().Equal(expected[1]);
    }

    // Review of #547 (F1), the mirror on the profile's side: a profile error held while the node-id is
    // missing is released behind a boot-up that is kept from the wire; the profile clears it, and a Reset
    // Communication comes before either EMCY's turn. Neither the error nor its 0000h may go out.
    [Fact]
    public async Task A_Reset_Drops_A_Released_Profile_Error_And_Its_Error_Reset_On_An_Lss_Slave()
    {
        var app = Profile();
        int resets = 0;
        app.WhenReset = _ => Interlocked.Increment(ref resets);
        using var rig = UnconfiguredLssRig(app);
        Settle(rig.Node);
        await OnActor(rig.Node, () => app.Host!.RaiseError(0x5000, 0x04));
        var chainWaits = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bootUpMayLeave = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Node.HeldEmcysWaitForBootUpForTests = () => chainWaits.TrySetResult(true);
        rig.Node.BeforeControlFrameSendForTests = id => id == SlaveHeartbeat ? bootUpMayLeave.Task : Task.CompletedTask;
        try
        {
            await ConfigureNodeIdAsync(rig);
            await chainWaits.Task.WithTimeoutAsync(ShortTimeout);
            await OnActor(rig.Node, () => app.Host!.ClearError(0x5000));
            rig.Wire.Transmit(0x000, ResetCommand(NmtCommand.ResetCommunication));
            await WaitUntilAsync(() => Volatile.Read(ref resets) == 1, "the reset");
            Settle(rig.Node);
        }
        finally { bootUpMayLeave.TrySetResult(true); }

        await rig.Wire.WaitForCountAsync(SlaveHeartbeat, 2);
        Settle(rig.Node);
        await EmcyChainSettledAsync(rig.Node);
        BootUpsAndEmcys(rig.Wire).Select(f => f.CobId).Should().Equal(new[] { SlaveHeartbeat, SlaveHeartbeat },
            "the reset ended the error before its EMCY's turn, and its 0000h with it");
    }
}
