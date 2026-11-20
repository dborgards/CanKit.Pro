using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

// FR-CO-011, #565, bot round 2 on #574: the waiting step of an EMCY with neither EMCY inhibit nor
// error behaviour (SendWhenNotStoppedAsync), across {application EMCY, node EMCY} x {Start, Enter
// Pre-operational, Reset Communication, Reset Node, disposal, token cancelled} x {while it waits in
// Stopped, after its wait woke but before its next pass ran, while it waits for a boot-up}. The cells
// without a test here are argued in the PR report. "In one turn" means the NMT commands are handled in
// a single actor job, so that the step's next pass, which its wake posts, runs only after all of them.
public partial class CanOpenPdoEngineTests
{
    private static readonly MethodInfo HandleIncomingMethod =
        typeof(CanOpenNode).GetMethod("HandleIncoming", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static Task InjectInOneTurn(CanOpenNode node, params byte[] nmtCommands)
        => node.PostToActorAsync(() =>
        {
            foreach (byte command in nmtCommands)
                HandleIncomingMethod.Invoke(node, new object[] { CanOpenCobId.NmtCommand, new[] { command, Device }, false });
        });

    /// <summary>A node EMCY (8130h of a heartbeat timeout) queued behind the held 1000h, then Stop:
    /// returns once the 8130h waits in Stopped.</summary>
    private static async Task NodeEmcyWaitsInStoppedAsync(EmcyInhibitRig rig, SlowEmcyDriver driver)
    {
        var first = rig.Node.SendEmcyAsync(0x1000, 0);
        await driver.Entered;
        rig.Node.AddHeartbeatConsumer(1, TimeSpan.FromMilliseconds(100));
        Advance(rig.Clock, rig.Node, TimeSpan.FromMilliseconds(101));
        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)NmtCommand.Stop, Device);
        driver.Release();
        await first.WaitAsync(ShortTimeout);
        await WaitForEmcyChainWaitingAsync(rig);
    }

    private static CanOpenNodeOptions NodeEmcyOptions => new() { EmitGuardingErrorEmcy = true, ErrorHistoryCapacity = 4 };

    // --- Phase A: while the EMCY waits in Stopped ---------------------------------------------------

    [Theory]
    [InlineData(NmtCommand.Start)]
    [InlineData(NmtCommand.EnterPreOperational)]
    public async Task Node_Emcy_That_Waited_In_Stopped_Goes_Out_When_The_Node_Leaves_Stopped(NmtCommand leave)
    {
        using var rig = new EmcyInhibitRig(NodeEmcyOptions);
        using var driver = new SlowEmcyDriver(rig);
        await NodeEmcyWaitsInStoppedAsync(rig, driver);
        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)leave, Device);
        await ExpectEmcyAsync(rig.Wire, 2, 0x8130, Communication);
        await rig.Node.SendEmcyAsync(0x1002, 0).WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 3, 0x1002, Communication);
        rig.Wire.Count(EmcyId).Should().Be(3);
    }

    [Fact]
    public async Task Node_Emcy_That_Waited_In_Stopped_Is_Dropped_By_A_Reset_Node()
    {
        using var rig = new EmcyInhibitRig(NodeEmcyOptions);
        using var driver = new SlowEmcyDriver(rig);
        await NodeEmcyWaitsInStoppedAsync(rig, driver);
        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)NmtCommand.ResetNode, Device);
        await rig.Node.SendEmcyAsync(0x1002, 0).WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 2, 0x1002, 0);
        rig.Wire.Count(EmcyId).Should().Be(2);
        rig.Node.ObjectDictionary.ReadUnsigned(0x1003, 0).Should().Be(2u, "nothing for the dropped 8130h");
    }

    [Theory]
    [InlineData(NmtCommand.ResetCommunication)]
    [InlineData(NmtCommand.ResetNode)]
    public async Task Application_Emcy_That_Waited_In_Stopped_Goes_Out_Once_Behind_The_Boot_Up_Of_A_Reset(NmtCommand reset)
    {
        using var rig = new EmcyInhibitRig(new CanOpenNodeOptions());
        using var driver = new SlowEmcyDriver(rig);
        var second = (await QueueBehindASlowSendAndStopAsync(rig, driver, 0x1001))[0];
        await WaitForEmcyHeldOrCompletionAsync(rig, second);
        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)reset, Device);
        await second.WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 2, 0x1001, 0);
        await rig.Node.SendEmcyAsync(0x1002, 0).WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 3, 0x1002, 0);
        rig.Wire.Count(EmcyId).Should().Be(3, "the waiting EMCY went out exactly once");
        SecondEmcyShouldFollowBootUps(driver, resets: 1);
    }

    /// <summary>The second EMCY handed to the driver — the one that waited — follows the boot-up of
    /// each of <paramref name="resets"/>: at least that many boot-ups come before it, and none after
    /// it. The node's start-up boot-up can reach the driver late, anywhere before the resets' ones, so
    /// it is not counted exactly.</summary>
    private static void SecondEmcyShouldFollowBootUps(SlowEmcyDriver driver, int resets)
    {
        var order = driver.Order;
        int second = order.IndexOf(EmcyId, order.IndexOf(EmcyId) + 1);
        uint bootUp = CanOpenCobId.Heartbeat(Device);
        order.Take(second).Count(id => id == bootUp).Should().BeGreaterThanOrEqualTo(resets, "every reset's boot-up goes out first");
        order.Skip(second + 1).Should().NotContain(bootUp, "no boot-up is left behind the EMCY that waited");
    }

    // Bugbot on #574: the disposal ends an application EMCY's wait in Stopped; its task completes,
    // as a queued EMCY's does on disposal on the gated path, and nothing is sent.
    [Fact]
    public async Task Application_Emcy_Waiting_In_Stopped_Completes_When_The_Node_Is_Disposed()
    {
        using var rig = new EmcyInhibitRig(new CanOpenNodeOptions());
        using var driver = new SlowEmcyDriver(rig);
        var second = (await QueueBehindASlowSendAndStopAsync(rig, driver, 0x1001))[0];
        await WaitForEmcyHeldOrCompletionAsync(rig, second);
        second.IsCompleted.Should().BeFalse();

        rig.Node.Dispose();
        await second.WaitAsync(ShortTimeout);
        second.Status.Should().Be(TaskStatus.RanToCompletion);
        rig.Wire.Payloads(EmcyId).Select(p => p[0] | (p[1] << 8)).Should().NotContain(0x1001);
    }

    // --- Phase B: after the wait woke, before the next pass ran -------------------------------------

    // Codex on #574: a Start wakes a node EMCY's wait, and a reset is handled before its next pass. The
    // reset ended the error: the EMCY is dropped and enters nothing in 1003h.
    [Theory]
    [InlineData(NmtCommand.Start, NmtCommand.ResetCommunication)]
    [InlineData(NmtCommand.Start, NmtCommand.ResetNode)]
    [InlineData(NmtCommand.EnterPreOperational, NmtCommand.ResetCommunication)]
    [InlineData(NmtCommand.EnterPreOperational, NmtCommand.ResetNode)]
    public async Task Node_Emcy_Woken_By_A_State_Change_Is_Dropped_By_A_Reset_Before_Its_Next_Pass(NmtCommand leave, NmtCommand reset)
    {
        using var rig = new EmcyInhibitRig(NodeEmcyOptions);
        using var driver = new SlowEmcyDriver(rig);
        await NodeEmcyWaitsInStoppedAsync(rig, driver);

        await InjectInOneTurn(rig.Node, (byte)leave, (byte)reset);
        await rig.Node.SendEmcyAsync(0x1002, 0).WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 2, 0x1002, 0);
        rig.Wire.Count(EmcyId).Should().Be(2, "the 8130h of an error the reset ended does not go out");
        rig.Node.ObjectDictionary.ReadUnsigned(0x1003, 0).Should().Be(2u, "1000h and 1002h only");
    }

    [Theory]
    [InlineData(NmtCommand.ResetCommunication)]
    [InlineData(NmtCommand.ResetNode)]
    public async Task Application_Emcy_Woken_By_Start_Goes_Out_Once_When_A_Reset_Comes_Before_Its_Next_Pass(NmtCommand reset)
    {
        using var rig = new EmcyInhibitRig(new CanOpenNodeOptions());
        using var driver = new SlowEmcyDriver(rig);
        var second = (await QueueBehindASlowSendAndStopAsync(rig, driver, 0x1001))[0];
        await WaitForEmcyHeldOrCompletionAsync(rig, second);

        await InjectInOneTurn(rig.Node, (byte)NmtCommand.Start, (byte)reset);
        await second.WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 2, 0x1001, 0);
        await rig.Node.SendEmcyAsync(0x1002, 0).WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 3, 0x1002, 0);
        rig.Wire.Count(EmcyId).Should().Be(3, "the application's EMCY goes out exactly once");
        SecondEmcyShouldFollowBootUps(driver, resets: 1);
    }

    [Fact]
    public async Task Application_Emcy_Woken_By_Start_Is_Withdrawn_By_A_Token_Cancelled_Before_Its_Next_Pass()
    {
        using var rig = new EmcyInhibitRig(new CanOpenNodeOptions());
        using var driver = new SlowEmcyDriver(rig);
        using var cancellation = new CancellationTokenSource();
        var first = rig.Node.SendEmcyAsync(0x1000, 0);
        await driver.Entered;
        var waiting = rig.Node.SendEmcyAsync(0x1001, 0x04, cancellationToken: cancellation.Token);
        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)NmtCommand.Stop, Device);
        driver.Release();
        await first.WaitAsync(ShortTimeout);
        await WaitForEmcyHeldOrCompletionAsync(rig, waiting);

        await rig.Node.PostToActorAsync(() =>
        {
            HandleIncomingMethod.Invoke(rig.Node, new object[] { CanOpenCobId.NmtCommand, new[] { (byte)NmtCommand.Start, Device }, false });
            cancellation.Cancel();
        });
        await FluentActions.Awaiting(() => waiting.WaitAsync(ShortTimeout)).Should().ThrowAsync<OperationCanceledException>();
        rig.Node.ObjectDictionary.ReadUnsigned(0x1001, 0).Should().Be(0, "the withdrawn EMCY wrote nothing");
        await rig.Node.SendEmcyAsync(0x1002, 0).WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 2, 0x1002, 0);
        rig.Wire.Count(EmcyId).Should().Be(2);
    }

    [Fact]
    public async Task Application_Emcy_Woken_By_Start_Completes_Without_A_Frame_When_The_Node_Is_Disposed_Before_Its_Next_Pass()
    {
        using var rig = new EmcyInhibitRig(new CanOpenNodeOptions());
        using var driver = new SlowEmcyDriver(rig);
        var second = (await QueueBehindASlowSendAndStopAsync(rig, driver, 0x1001))[0];
        await WaitForEmcyHeldOrCompletionAsync(rig, second);

        await rig.Node.PostToActorAsync(() =>
        {
            HandleIncomingMethod.Invoke(rig.Node, new object[] { CanOpenCobId.NmtCommand, new[] { (byte)NmtCommand.Start, Device }, false });
            rig.Node.Dispose();
        });
        await second.WaitAsync(ShortTimeout);
        second.Status.Should().Be(TaskStatus.RanToCompletion);
        rig.Wire.Payloads(EmcyId).Select(p => p[0] | (p[1] << 8)).Should().NotContain(0x1001);
    }

    // --- Phase C: while the EMCY waits for a reset's boot-up ----------------------------------------

    /// <summary>The application's 1001h waits in Stopped; a Reset Communication, whose boot-up the
    /// driver holds, releases it into its wait for that boot-up. Returns the 1001h's task.</summary>
    private static async Task<Task> ApplicationEmcyWaitsForAHeldBootUpAsync(EmcyInhibitRig rig, SlowEmcyDriver driver)
    {
        var second = (await QueueBehindASlowSendAndStopAsync(rig, driver, 0x1001))[0];
        await WaitForEmcyHeldOrCompletionAsync(rig, second);
        var waitsForBootUp = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Node.HeldEmcysWaitForBootUpForTests = () => waitsForBootUp.TrySetResult(true);
        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)NmtCommand.ResetCommunication, Device);
        await driver.BootUpEntered;
        await waitsForBootUp.Task.WaitAsync(ShortTimeout);
        return second;
    }

    // Bugbot on #574: the disposal ends the wait for a boot-up too; the task completes while the
    // boot-up is still held, and nothing is sent.
    [Fact]
    public async Task Application_Emcy_Waiting_For_A_Boot_Up_Completes_When_The_Node_Is_Disposed()
    {
        using var rig = new EmcyInhibitRig(new CanOpenNodeOptions());
        using var driver = new SlowEmcyDriver(rig, holdBootUpOf: Device);
        var second = await ApplicationEmcyWaitsForAHeldBootUpAsync(rig, driver);

        rig.Node.Dispose();
        await second.WaitAsync(ShortTimeout);
        driver.BootUpLeft.Should().BeFalse("the disposal ended the wait, not the boot-up leaving");
        second.Status.Should().Be(TaskStatus.RanToCompletion);
        driver.ReleaseBootUp();
        rig.Wire.Payloads(EmcyId).Select(p => p[0] | (p[1] << 8)).Should().NotContain(0x1001);
    }

    [Theory]
    [InlineData(NmtCommand.ResetCommunication)]
    [InlineData(NmtCommand.ResetNode)]
    public async Task Application_Emcy_Waiting_For_A_Boot_Up_Goes_Out_Once_Behind_The_Next_Resets_Boot_Up(NmtCommand reset)
    {
        using var rig = new EmcyInhibitRig(new CanOpenNodeOptions());
        using var driver = new SlowEmcyDriver(rig, holdBootUpOf: Device);
        var second = await ApplicationEmcyWaitsForAHeldBootUpAsync(rig, driver);

        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)reset, Device);
        driver.ReleaseBootUp();
        await second.WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 2, 0x1001, 0);
        await rig.Node.SendEmcyAsync(0x1002, 0).WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 3, 0x1002, 0);
        rig.Wire.Count(EmcyId).Should().Be(3, "exactly once");
        SecondEmcyShouldFollowBootUps(driver, resets: 2);
    }

    // A node EMCY that waited in Stopped, woken by Start while the boot-up of an earlier reset is still
    // held, waits for that boot-up; a reset in that wait ended its error: dropped.
    [Theory]
    [InlineData(NmtCommand.ResetCommunication)]
    [InlineData(NmtCommand.ResetNode)]
    public async Task Node_Emcy_Waiting_For_A_Boot_Up_Is_Dropped_By_A_Reset(NmtCommand reset)
    {
        using var rig = new EmcyInhibitRig(NodeEmcyOptions);
        using var driver = new SlowEmcyDriver(rig, holdBootUpOf: Device);
        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)NmtCommand.ResetCommunication, Device);
        await driver.BootUpEntered; // the first reset's boot-up stays held
        await NodeEmcyWaitsInStoppedAsync(rig, driver);
        var waitsForBootUp = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Node.HeldEmcysWaitForBootUpForTests = () => waitsForBootUp.TrySetResult(true);
        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)NmtCommand.Start, Device);
        await waitsForBootUp.Task.WaitAsync(ShortTimeout);

        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)reset, Device);
        driver.ReleaseBootUp();
        await rig.Node.SendEmcyAsync(0x1002, 0).WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 2, 0x1002, 0);
        rig.Wire.Count(EmcyId).Should().Be(2, "the 8130h of an error the reset ended does not go out");
    }
}
