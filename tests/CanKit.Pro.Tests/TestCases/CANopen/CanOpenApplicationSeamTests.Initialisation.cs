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

// A profile error raised while the node is in NMT state Initialisation: during a reset, before the
// start-up, and on an LSS slave that is still waiting for its node-id. In Initialisation only the
// boot-up object exists (CiA 301 §7.3.2.2.1); the EMCY is held and follows the boot-up.
public partial class CanOpenApplicationSeamTests
{
    private static readonly byte[] BootUp = { 0x00 };

    /// <summary>The slave's own boot-up and EMCY frames on the wire from <paramref name="from"/> on, in
    /// the order the wire carried them.</summary>
    private static (uint CobId, byte[] Data)[] BootUpsAndEmcys(RawWire wire, int from = 0)
        => wire.Sequence.Skip(from)
            .Where(f => f.CobId == SlaveEmcy || (f.CobId == SlaveHeartbeat && f.Data.SequenceEqual(BootUp)))
            .ToArray();

    private static void ShouldBeBootUpThenEmcy((uint CobId, byte[] Data)[] frames, byte[] emcy)
    {
        frames.Select(f => f.CobId).Should().Equal(new[] { SlaveHeartbeat, SlaveEmcy },
            "in Initialisation only the boot-up exists; the EMCY follows it (CiA 301 §7.3.2.2.1)");
        frames[1].Data.Should().Equal(emcy);
    }

    /// <summary>
    /// Keeps the slave's boot-up from the wire until either an EMCY of the slave is on the wire, or the
    /// node's EMCY chain is waiting for the boot-up. Without the wait the EMCY gets out first, every
    /// time; with it, the boot-up is released as soon as the chain waits for it. The heartbeat and the
    /// EMCY travel on separate chains, so this is what makes their order on the wire observable instead
    /// of a race that the right order usually wins.
    /// </summary>
    private static void HoldBootUpUntilTheEmcyHasHadItsChance(EntryRig rig)
    {
        var chainWaits = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Node.HeldEmcysWaitForBootUpForTests = () => chainWaits.TrySetResult(true);
        rig.Node.BeforeControlFrameSendForTests = id => id == SlaveHeartbeat
            ? WaitUntilAsync(() => chainWaits.Task.IsCompleted || rig.Wire.Count(SlaveEmcy) > 0,
                "the EMCY on the wire, or the EMCY chain waiting for the boot-up")
            : Task.CompletedTask;
    }

    // -----------------------------------------------------------------------------------------
    // During a reset, and before the start-up.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task An_Error_Raised_In_OnReset_Follows_The_Boot_Up()
    {
        var app = Profile();
        app.WhenReset = _ => app.Host!.RaiseError(0x5000, 0x04);
        using var rig = new EntryRig(With(app));
        await rig.Wire.WaitForCountAsync(SlaveHeartbeat, 1);
        Settle(rig.Node);
        HoldBootUpUntilTheEmcyHasHadItsChance(rig);
        int mark = rig.Wire.Sequence.Length;

        await ResetFromWireAsync(rig, NmtCommand.ResetNode);
        await rig.Wire.WaitForCountAsync(SlaveEmcy, 1);
        await EmcyChainSettledAsync(rig.Node);

        ShouldBeBootUpThenEmcy(BootUpsAndEmcys(rig.Wire, mark), Emcy(0x5000, 0x05));
        ErrorRegister(rig.Node).Should().Be(0x05, "the held EMCY wrote the register when it went out");
    }

    [Fact]
    public async Task An_Error_Raised_Before_The_Start_Up_Follows_The_Boot_Up()
    {
        using var release = new ManualResetEventSlim();
        NmtState? stateWhenRaised = null;
        bool released = false;
        var app = Profile();
        // A deadline armed in Attach and due at once runs ahead of the start-up job: the actor does
        // not let later mailbox work overtake a timer that was due before it was posted. It waits
        // for the test, so the boot-up cannot have left before the hold is in place.
        app.OnAttach = host => host.Arm(TimeSpan.Zero, () =>
        {
            released = release.Wait(ShortTimeout);
            stateWhenRaised = host.State;
            host.RaiseError(0x5000, 0x04);
        });
        using var rig = new EntryRig(With(app), new ManualTimeSource());
        HoldBootUpUntilTheEmcyHasHadItsChance(rig);

        release.Set();
        await rig.Wire.WaitForCountAsync(SlaveHeartbeat, 1);
        await rig.Wire.WaitForCountAsync(SlaveEmcy, 1);
        await EmcyChainSettledAsync(rig.Node);

        // Asserted here, not in the callback: a throw on the actor loop is a background exception,
        // not a test failure.
        released.Should().BeTrue("the deadline waited for the test, so the hold was in place before the boot-up");
        stateWhenRaised.Should().Be(NmtState.Initializing, "the error is raised before the node has booted");
        ShouldBeBootUpThenEmcy(BootUpsAndEmcys(rig.Wire), Emcy(0x5000, 0x05));
        ErrorRegister(rig.Node).Should().Be(0x05);
    }

    [Fact]
    public async Task An_Error_Cleared_Before_The_Boot_Up_Is_Not_Sent()
    {
        var app = Profile();
        app.WhenReset = _ =>
        {
            var host = app.Host!;
            host.RaiseError(0x5000, 0x04);
            host.RaiseError(0x3200, 0x20);
            host.ClearError(0x5000);
        };
        using var rig = new EntryRig(With(app));
        await rig.Wire.WaitForCountAsync(SlaveHeartbeat, 1);
        Settle(rig.Node);
        int mark = rig.Wire.Sequence.Length;

        await ResetFromWireAsync(rig, NmtCommand.ResetNode);
        await rig.Wire.WaitForCountAsync(SlaveEmcy, 1);
        await EmcyChainSettledAsync(rig.Node);

        ShouldBeBootUpThenEmcy(BootUpsAndEmcys(rig.Wire, mark), Emcy(0x3200, 0x21));
        ErrorRegister(rig.Node).Should().Be(0x21, "the error that ended before the boot-up left nothing behind");
    }

    /// <summary>
    /// A profile raises an error in the first reset's <c>OnReset</c>; its EMCY is released behind that
    /// reset's boot-up, and the boot-up is kept from the wire until the EMCY chain waits for it. With
    /// <paramref name="resetAgain"/> a second Reset Node is handled in that window, as from a master
    /// that resets the node when it sees the boot-up; then the boot-up is let go. Returns the rig and
    /// where on the wire the first reset began.
    /// </summary>
    private static async Task<(EntryRig Rig, int Mark)> ReleaseBehindAHeldBootUpAsync(bool resetAgain)
    {
        var app = Profile();
        int resets = 0;
        app.WhenReset = _ =>
        {
            if (Interlocked.Increment(ref resets) == 1) app.Host!.RaiseError(0x5000, 0x04);
        };
        var rig = new EntryRig(With(app));
        try
        {
            await rig.Wire.WaitForCountAsync(SlaveHeartbeat, 1);
            Settle(rig.Node);
            var chainWaits = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var bootUpMayLeave = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            rig.Node.HeldEmcysWaitForBootUpForTests = () => chainWaits.TrySetResult(true);
            // Off the actor: the boot-up's send waits here, the actor stays free to handle a reset.
            rig.Node.BeforeControlFrameSendForTests = id => id == SlaveHeartbeat ? bootUpMayLeave.Task : Task.CompletedTask;
            int mark = rig.Wire.Sequence.Length;
            int bootUps = rig.Wire.Count(SlaveHeartbeat);

            rig.Wire.Transmit(0x000, ResetCommand(NmtCommand.ResetNode));
            await chainWaits.Task.WithTimeoutAsync(ShortTimeout);
            if (resetAgain)
            {
                rig.Wire.Transmit(0x000, ResetCommand(NmtCommand.ResetNode));
                await WaitUntilAsync(() => Volatile.Read(ref resets) == 2, "the second reset");
                Settle(rig.Node);
            }
            bootUpMayLeave.SetResult(true);

            await rig.Wire.WaitForCountAsync(SlaveHeartbeat, bootUps + (resetAgain ? 2 : 1));
            Settle(rig.Node);
            await EmcyChainSettledAsync(rig.Node);
            return (rig, mark);
        }
        catch
        {
            rig.Dispose();
            throw;
        }
    }

    [Fact]
    public async Task A_Released_Emcy_Goes_Out_Behind_A_Held_Boot_Up()
    {
        var (rig, mark) = await ReleaseBehindAHeldBootUpAsync(resetAgain: false);
        using (rig)
        {
            ShouldBeBootUpThenEmcy(BootUpsAndEmcys(rig.Wire, mark), Emcy(0x5000, 0x05));
            ErrorRegister(rig.Node).Should().Be(0x05);
        }
    }

    [Fact]
    public async Task A_Reset_Before_A_Released_Emcy_Goes_Out_Drops_It()
    {
        var (rig, mark) = await ReleaseBehindAHeldBootUpAsync(resetAgain: true);
        using (rig)
        {
            BootUpsAndEmcys(rig.Wire, mark).Select(f => f.CobId).Should().Equal(new[] { SlaveHeartbeat, SlaveHeartbeat },
                "the second reset ended the error before its EMCY's turn came: two boot-ups and nothing else");
            ErrorRegister(rig.Node).Should().Be(0, "a reset ends a profile error without an EMCY, and nothing writes it back");
        }
    }

    // -----------------------------------------------------------------------------------------
    // An LSS slave without a node-id.
    // -----------------------------------------------------------------------------------------

    private static EntryRig UnconfiguredLssRig(RecordingApplication app)
        => new(With(app).WithLss(new LssSlaveOptions()), nodeId: CanOpenCobId.UnconfiguredNodeId);

    /// <summary>The LSS master gives the slave node-id <see cref="Slave"/>; returns once the slave has
    /// confirmed it, which it does after it has ordered its boot-up.</summary>
    private static async Task ConfigureNodeIdAsync(EntryRig rig)
    {
        int replies = rig.Wire.Count(CanOpenCobId.LssSlaveMessage);
        rig.Wire.Transmit(CanOpenCobId.LssMasterMessage, new byte[] { LssCommand.SwitchStateGlobal, 1, 0, 0, 0, 0, 0, 0 });
        rig.Wire.Transmit(CanOpenCobId.LssMasterMessage, new byte[] { LssCommand.ConfigureNodeId, Slave, 0, 0, 0, 0, 0, 0 });
        await rig.Wire.WaitForCountAsync(CanOpenCobId.LssSlaveMessage, replies + 1);
        Settle(rig.Node);
    }

    [Fact]
    public async Task An_Error_Raised_While_The_Node_Id_Is_Missing_Follows_The_Boot_Up()
    {
        var app = Profile();
        using var rig = UnconfiguredLssRig(app);
        Settle(rig.Node);

        await OnActor(rig.Node, () => app.Host!.RaiseError(0x5000, 0x04));
        await EmcyChainSettledAsync(rig.Node);

        rig.Node.State.Should().Be(NmtState.Initializing, "the slave waits for its node-id");
        rig.Wire.Sequence.Should().BeEmpty("an unconfigured slave transmits nothing");

        await ConfigureNodeIdAsync(rig);
        await rig.Wire.WaitForCountAsync(SlaveEmcy, 1);
        await EmcyChainSettledAsync(rig.Node);

        ShouldBeBootUpThenEmcy(BootUpsAndEmcys(rig.Wire), Emcy(0x5000, 0x05));
        ErrorRegister(rig.Node).Should().Be(0x05, "the error the register must show reached it with the EMCY");
    }

    [Fact]
    public async Task A_Reset_While_An_Error_Is_Held_Drops_It()
    {
        var app = Profile();
        using var rig = UnconfiguredLssRig(app);
        Settle(rig.Node);
        await OnActor(rig.Node, () => app.Host!.RaiseError(0x5000, 0x04));

        // A broadcast reset reaches an unconfigured slave; Reset Communication keeps the pending
        // node-id, which is unconfigured, so the slave stays in Initialisation.
        rig.Wire.Transmit(0x000, new byte[] { (byte)NmtCommand.ResetCommunication, 0 });
        await WaitUntilAsync(() => app.Log.Contains("reset ResetCommunication"), "the reset");
        Settle(rig.Node);
        rig.Node.State.Should().Be(NmtState.Initializing);

        await ConfigureNodeIdAsync(rig);
        await rig.Wire.WaitForCountAsync(SlaveHeartbeat, 1);
        await EmcyChainSettledAsync(rig.Node);

        rig.Wire.Count(SlaveEmcy).Should().Be(0, "the reset ended the error before its EMCY could go out");
        ErrorRegister(rig.Node).Should().Be(0);
    }
}
