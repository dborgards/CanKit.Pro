using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Cia401;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Pdo;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Cia401;

/// <summary>
/// What the rig's barrier (<see cref="CanOpenTransmitBarrier"/>) rests on, and why it replaced the
/// witness frame: a completed send has been seen by the master's channel, and a send that has not
/// completed keeps the barrier shut where a witness frame already said "done".
/// </summary>
public class Cia401RigTests
{
    private static Cia401Configuration Inputs(int channels) => new() { DigitalInputs = channels };

    [Fact]
    public async Task A_Completed_Send_Has_Already_Been_Seen_By_The_Master()
    {
        using var rig = new Cia401Rig(Inputs(8));

        // No wait between the send's completion and the count: the virtual adapter hands the frame
        // to the other channels' FrameObserved inside the driver call (CanKit v0.5.6
        // VirtualBusHub.Broadcast), and the send completes only after that call has returned.
        for (int sent = 1; sent <= 50; sent++)
        {
            await rig.Node.SendNmtCommandAsync(NmtCommand.EnterPreOperational, 0x7F);
            rig.Count(0x000).Should().Be(sent);
        }
    }

    [Fact]
    public async Task A_Held_Send_Keeps_The_Barrier_Shut_Where_A_Witness_Frame_Said_Done()
    {
        using var rig = new Cia401Rig(Inputs(8));
        rig.Od.AddU8(0x2101, 0x00, 0);
        rig.Node.ConfigureTpdo(2, new PdoMapping().Add(0x2101, 0x00, 8));
        await rig.StartAndTakeFirstTpdoAsync();
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Node.BeforeControlFrameSendForTests = cobId => cobId == Cia401Rig.Tpdo1 ? release.Task : Task.CompletedTask;

        try
        {
            await HeldSendScenarioAsync(rig, release);
        }
        finally
        {
            // A failing assertion must not leave the held send behind for the node's disposal.
            release.TrySetResult(true);
        }
    }

    private static async Task HeldSendScenarioAsync(Cia401Rig rig, TaskCompletionSource<bool> release)
    {
        const uint witnessCobId = 0x280 + Cia401Rig.DeviceId;
        rig.Device.SetDigitalInput(1, true); // decided on the actor; its frame held before the driver

        // The old witness: a TPDO triggered after it, and its frame on the wire.
        await rig.Node.TriggerTpdoAsync(2);
        await rig.WaitForCountAsync(witnessCobId, 1);
        rig.Count(Cia401Rig.Tpdo1).Should().Be(1,
            "the witness frame overtook the held TPDO 1: \"nothing was sent\" would pass here, falsely");

        var barrier = rig.DrainAsync();
        await rig.Node.PostToActorAsync(() => { });
        await rig.Node.PostToActorAsync(() => { });
        rig.Node.HasLssTransmitsInFlight.Should().BeTrue("the held TPDO 1 keeps its reservation");
        barrier.IsCompleted.Should().BeFalse("the barrier cannot open while a decided frame has not reached the bus");

        release.SetResult(true);
        await barrier;

        rig.Count(Cia401Rig.Tpdo1).Should().Be(2, "once the barrier opens, the frame it waited for has been seen");
        rig.Payloads(Cia401Rig.Tpdo1)[1].Should().Equal(0x01);
    }
}
