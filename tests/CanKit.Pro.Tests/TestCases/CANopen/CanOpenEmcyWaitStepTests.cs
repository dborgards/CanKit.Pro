using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

// FR-CO-011, #565, bot round 2 on #574: the waiting step of an EMCY with neither EMCY inhibit nor
// error behaviour (SendWhenNotStoppedAsync) and the disposal of the node.
public partial class CanOpenPdoEngineTests
{
    private static readonly MethodInfo HandleIncomingMethod =
        typeof(CanOpenNode).GetMethod("HandleIncoming", BindingFlags.Instance | BindingFlags.NonPublic)!;

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
}
