using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Lss;
using CanKit.Pro.CANopen.Nmt;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

// FR-CO-011, #565: no EMCY leaves in Stopped (CiA 301 v4.2.0 §7.3.2.2.4, Table 37), also with
// neither EMCY inhibit nor error behaviour enabled. An EMCY whose turn comes in Stopped is held and
// goes out once the node has left Stopped. Across an NMT reset an application's EMCY does what it did
// before (it goes out after the reset, on the COB-ID active then: LssSlaveTests.An_Emcy_Queued_Before_
// The_Node_Id_Changes_Uses_The_New_Cob_Id), and an EMCY the node raised for its own error state is
// dropped: the reset ended that error (Bugbot on #574). The first EMCY's send is held in the driver
// (ControllableBus.OnTransmitting), so the next ones are queued behind it on the node's EMCY chain
// when the NMT command arrives.
public partial class CanOpenPdoEngineTests
{
    private sealed class SlowEmcyDriver : IDisposable
    {
        private readonly TaskCompletionSource<bool> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _release = new();
        private readonly List<uint> _order = new();
        private readonly TaskCompletionSource<bool> _bootUpEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _releaseBootUp = new();
        private readonly TaskCompletionSource<bool> _heldNodeEmcySent = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _bootUpLeft;

        /// <summary>Whether a held boot-up has left the hold, released or timed out.</summary>
        internal bool BootUpLeft => Volatile.Read(ref _bootUpLeft) != 0;

        /// <summary>Completes when an EMCY of the node-id whose boot-up is held reaches the driver.</summary>
        internal Task EmcyOfHeldNodeSent => _heldNodeEmcySent.Task;

        /// <param name="holdBootUpOf">When set, the boot-up of that node-id is held in the driver too,
        /// until <see cref="ReleaseBootUp"/>.</param>
        internal SlowEmcyDriver(EmcyInhibitRig rig, byte? holdBootUpOf = null)
        {
            var forward = rig.Bus.OnTransmitting!;
            rig.Bus.OnTransmitting = frame =>
            {
                lock (_order) _order.Add((uint)frame.ID);
                if (holdBootUpOf is { } emcyOf && (uint)frame.ID == CanOpenCobId.Emcy(emcyOf)) _heldNodeEmcySent.TrySetResult(true);
                if (holdBootUpOf is { } id && (uint)frame.ID == CanOpenCobId.Heartbeat(id) && frame.Data.Span[0] == 0x00)
                {
                    _bootUpEntered.TrySetResult(true);
                    _releaseBootUp.Wait(ShortTimeout);
                    Volatile.Write(ref _bootUpLeft, 1);
                }
                // Only the first EMCY, 1000h, blocks, as a slow driver would.
                if ((uint)frame.ID == EmcyId && frame.Data.Span[0] == 0x00 && frame.Data.Span[1] == 0x10)
                {
                    _entered.TrySetResult(true);
                    _release.Wait(ShortTimeout);
                }
                forward(frame);
            };
        }

        internal Task Entered => _entered.Task.WaitAsync(ShortTimeout);

        /// <summary>The COB-ID of every frame handed to the driver, in order.</summary>
        internal List<uint> Order
        {
            get { lock (_order) return new List<uint>(_order); }
        }

        internal void Release() => _release.Set();

        internal Task BootUpEntered => _bootUpEntered.Task.WaitAsync(ShortTimeout);

        internal void ReleaseBootUp() => _releaseBootUp.Set();

        public void Dispose()
        {
            _release.Set();
            _releaseBootUp.Set();
            _release.Dispose();
            _releaseBootUp.Dispose();
        }
    }

    /// <summary>Returns once <paramref name="emcy"/> is either waiting for the node to leave Stopped
    /// or finished. Which of the two is what the caller asserts.</summary>
    private static async Task WaitForEmcyHeldOrCompletionAsync(EmcyInhibitRig rig, Task emcy)
    {
        var timer = Stopwatch.StartNew();
        var field = typeof(CanOpenNode).GetField("_emcyInhibitWait", BindingFlags.Instance | BindingFlags.NonPublic)!;
        while (!emcy.IsCompleted && !await rig.Node.ActorForTests.PostAsync(() => field.GetValue(rig.Node) is not null))
        {
            if (timer.Elapsed > ShortTimeout) throw new TimeoutException("The EMCY neither waited nor finished.");
            await Task.Delay(1);
        }
    }

    /// <summary>Sends 1000h, which the driver holds, queues the EMCYs of <paramref name="codes"/>
    /// behind it, stops the node and lets the driver return.</summary>
    private static async Task<Task[]> QueueBehindASlowSendAndStopAsync(EmcyInhibitRig rig, SlowEmcyDriver driver,
        params ushort[] codes)
    {
        var first = rig.Node.SendEmcyAsync(0x1000, 0);
        await driver.Entered;
        var queued = new Task[codes.Length];
        for (int i = 0; i < codes.Length; i++) queued[i] = rig.Node.SendEmcyAsync(codes[i], 0);
        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)NmtCommand.Stop, Device);
        rig.Node.State.Should().Be(NmtState.Stopped);
        driver.Release();
        await first.WaitAsync(ShortTimeout);
        return queued;
    }

    [Theory]
    [InlineData(NmtCommand.Start, NmtState.Operational)]
    [InlineData(NmtCommand.EnterPreOperational, NmtState.PreOperational)]
    public async Task Emcy_Queued_Before_Stop_Is_Held_Until_The_Node_Leaves_Stopped_With_Default_Options(
        NmtCommand leave, NmtState left)
    {
        using var rig = new EmcyInhibitRig(new CanOpenNodeOptions());
        using var driver = new SlowEmcyDriver(rig);
        var second = (await QueueBehindASlowSendAndStopAsync(rig, driver, 0x1001))[0];

        await WaitForEmcyHeldOrCompletionAsync(rig, second);
        second.IsCompleted.Should().BeFalse("an EMCY whose turn comes in Stopped is held, and its task with it");
        rig.Node.State.Should().Be(NmtState.Stopped);

        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)leave, Device);
        rig.Node.State.Should().Be(left);
        await second.WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 2, 0x1001, 0);
        rig.Wire.Count(EmcyId).Should().Be(2, "the held EMCY goes out once, after the node left Stopped");
    }

    [Fact]
    public async Task Emcys_Held_In_Stopped_Keep_Their_Order_With_Default_Options()
    {
        using var rig = new EmcyInhibitRig(new CanOpenNodeOptions());
        using var driver = new SlowEmcyDriver(rig);
        var queued = await QueueBehindASlowSendAndStopAsync(rig, driver, 0x1001, 0x1002);
        await WaitForEmcyHeldOrCompletionAsync(rig, queued[0]);
        queued[0].IsCompleted.Should().BeFalse();
        queued[1].IsCompleted.Should().BeFalse("the second waits behind the first");

        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)NmtCommand.Start, Device);
        await Task.WhenAll(queued).WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 2, 0x1001, 0);
        await ExpectEmcyAsync(rig.Wire, 3, 0x1002, 0);
        rig.Wire.Count(EmcyId).Should().Be(3);
    }

    [Fact]
    public async Task Emcy_Held_In_Stopped_Is_Cancelled_By_Its_Token_With_Default_Options()
    {
        using var rig = new EmcyInhibitRig(new CanOpenNodeOptions());
        using var driver = new SlowEmcyDriver(rig);
        using var cancellation = new CancellationTokenSource();
        var first = rig.Node.SendEmcyAsync(0x1000, 0);
        await driver.Entered;
        var held = rig.Node.SendEmcyAsync(0x1001, 0x04, cancellationToken: cancellation.Token);
        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)NmtCommand.Stop, Device);
        driver.Release();
        await first.WaitAsync(ShortTimeout);
        await WaitForEmcyHeldOrCompletionAsync(rig, held);
        held.IsCompleted.Should().BeFalse();

        cancellation.Cancel();
        await FluentActions.Awaiting(() => held.WaitAsync(ShortTimeout)).Should().ThrowAsync<OperationCanceledException>();
        // Read before anything else writes 1001h: the cancelled EMCY's 04h must not have reached it.
        rig.Node.ObjectDictionary.ReadUnsigned(0x1001, 0).Should().Be(0, "the cancelled EMCY wrote nothing");
        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)NmtCommand.Start, Device);
        // The witness: had the cancelled EMCY gone out, it would be the second frame.
        await rig.Node.SendEmcyAsync(0x1002, 0).WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 2, 0x1002, 0);
        rig.Wire.Count(EmcyId).Should().Be(2);
    }

    [Fact]
    public async Task Emcy_Held_In_Stopped_Survives_A_Reset_Communication_And_Goes_Out_Behind_The_Boot_Up_On_The_New_Cob_Id()
    {
        const byte newId = 0x12;
        using var rig = new EmcyInhibitRig(new CanOpenNodeOptions { Lss = new LssSlaveOptions() });
        using var driver = new SlowEmcyDriver(rig, holdBootUpOf: newId);
        var second = (await QueueBehindASlowSendAndStopAsync(rig, driver, 0x1001))[0];
        await WaitForEmcyHeldOrCompletionAsync(rig, second);
        second.IsCompleted.Should().BeFalse();

        // A pending node-id, which the Reset Communication makes the active one (CiA 305 §8.3.1).
        await Inject(rig.Node, CanOpenCobId.LssMasterMessage, LssCommand.SwitchStateGlobal, 1, 0, 0, 0, 0, 0, 0);
        await Inject(rig.Node, CanOpenCobId.LssMasterMessage, LssCommand.ConfigureNodeId, newId, 0, 0, 0, 0, 0, 0);
        await Inject(rig.Node, CanOpenCobId.LssMasterMessage, LssCommand.SwitchStateGlobal, 0, 0, 0, 0, 0, 0, 0);

        // The boot-up the reset orders is held in the driver. The released EMCY either starts waiting
        // for it, or (if it did not wait) reaches the driver ahead of it: whichever happens first decides.
        var waitsForBootUp = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Node.HeldEmcysWaitForBootUpForTests = () => waitsForBootUp.TrySetResult(true);
        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)NmtCommand.ResetCommunication, Device);
        rig.Node.NodeId.Should().Be(newId);
        await driver.BootUpEntered;
        var first = await Task.WhenAny(waitsForBootUp.Task, driver.EmcyOfHeldNodeSent).WaitAsync(ShortTimeout);
        first.Should().BeSameAs(waitsForBootUp.Task, "the EMCY released by the reset goes out behind the boot-up");
        driver.ReleaseBootUp();

        await second.WaitAsync(ShortTimeout);
        await rig.Wire.WaitForCountAsync(CanOpenCobId.Emcy(newId), 1);
        var p = rig.Wire.Payloads(CanOpenCobId.Emcy(newId))[0];
        ((ushort)(p[0] | (p[1] << 8))).Should().Be(0x1001);
        rig.Wire.Count(EmcyId).Should().Be(1, "only the first EMCY went out on the old COB-ID");
        var order = driver.Order;
        order.IndexOf(CanOpenCobId.Heartbeat(newId)).Should().BeGreaterThanOrEqualTo(0);
        order.IndexOf(CanOpenCobId.Heartbeat(newId)).Should().BeLessThan(order.IndexOf(CanOpenCobId.Emcy(newId)),
            "the held EMCY goes out behind the boot-up");
    }

    // Codex on #574: an EMCY that waited in Stopped and is released by a reset waits for that reset's
    // boot-up. Cancelling its token then still withdraws it; the boot-up is not cancelled with it.
    [Fact]
    public async Task Emcy_Released_By_A_Reset_Is_Cancelled_By_Its_Token_While_It_Waits_For_The_Boot_Up()
    {
        using var rig = new EmcyInhibitRig(new CanOpenNodeOptions());
        using var driver = new SlowEmcyDriver(rig, holdBootUpOf: Device);
        using var cancellation = new CancellationTokenSource();
        var first = rig.Node.SendEmcyAsync(0x1000, 0);
        await driver.Entered;
        var waiting = rig.Node.SendEmcyAsync(0x1001, 0x04, cancellationToken: cancellation.Token);
        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)NmtCommand.Stop, Device);
        driver.Release();
        await first.WaitAsync(ShortTimeout);
        await WaitForEmcyHeldOrCompletionAsync(rig, waiting);
        waiting.IsCompleted.Should().BeFalse();

        var waitsForBootUp = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Node.HeldEmcysWaitForBootUpForTests = () => waitsForBootUp.TrySetResult(true);
        int bootUps = rig.Wire.Count(CanOpenCobId.Heartbeat(Device));
        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)NmtCommand.ResetCommunication, Device);
        await driver.BootUpEntered;
        await waitsForBootUp.Task.WaitAsync(ShortTimeout);

        cancellation.Cancel();
        await FluentActions.Awaiting(() => waiting.WaitAsync(ShortTimeout)).Should().ThrowAsync<OperationCanceledException>();
        driver.BootUpLeft.Should().BeFalse("the cancellation ended the wait itself, not the boot-up leaving");
        rig.Node.ObjectDictionary.ReadUnsigned(0x1001, 0).Should().Be(0, "the withdrawn EMCY wrote nothing");
        driver.ReleaseBootUp();
        await rig.Wire.WaitForCountAsync(CanOpenCobId.Heartbeat(Device), bootUps + 1);

        // The witness: had the withdrawn EMCY gone out, it would be the second frame.
        await rig.Node.SendEmcyAsync(0x1002, 0).WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 2, 0x1002, 0);
        rig.Wire.Count(EmcyId).Should().Be(2);
    }

    /// <summary>Waits until the head of the EMCY chain waits for the node to leave Stopped.</summary>
    private static Task WaitForEmcyChainWaitingAsync(EmcyInhibitRig rig)
        => WaitForEmcyHeldOrCompletionAsync(rig, new TaskCompletionSource<bool>().Task);

    // Bugbot on #574: an EMCY the node raises for its own error state, which waited in Stopped, is
    // dropped by a reset, as the gated path drops it: the reset ended the error it reports (CiA 301
    // v4.2.0 §7.2.7.1, Figure 37, transitions 5 and 0). It writes neither 1001h nor 1003h.
    [Fact]
    public async Task Node_Heartbeat_Timeout_Emcy_That_Waited_In_Stopped_Is_Dropped_By_A_Reset_With_Default_Options()
    {
        using var rig = new EmcyInhibitRig(new CanOpenNodeOptions { EmitGuardingErrorEmcy = true, ErrorHistoryCapacity = 4 });
        using var driver = new SlowEmcyDriver(rig);
        var first = rig.Node.SendEmcyAsync(0x1000, 0);
        await driver.Entered;
        rig.Node.AddHeartbeatConsumer(1, TimeSpan.FromMilliseconds(100));
        Advance(rig.Clock, rig.Node, TimeSpan.FromMilliseconds(101)); // 8130h, queued behind 1000h
        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)NmtCommand.Stop, Device);
        driver.Release();
        await first.WaitAsync(ShortTimeout);
        await WaitForEmcyChainWaitingAsync(rig);

        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)NmtCommand.ResetCommunication, Device);
        await rig.Node.SendEmcyAsync(0x1002, 0).WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 2, 0x1002, 0);
        rig.Wire.Count(EmcyId).Should().Be(2, "the 8130h of an error the reset ended does not go out");
        rig.Node.ObjectDictionary.ReadUnsigned(0x1003, 0).Should().Be(2u, "1000h and 1002h; nothing for the dropped 8130h");
        rig.Node.ObjectDictionary.ReadUnsigned(0x1001, 0).Should().Be(0);
    }

    [Fact]
    public async Task Node_Rpdo_Length_Emcy_That_Waited_In_Stopped_Is_Dropped_By_A_Reset_With_Default_Options()
    {
        using var rig = new EmcyInhibitRig(new CanOpenNodeOptions());
        rig.Node.ObjectDictionary.AddU16(0x2000, 0, 0);
        rig.Node.ConfigureRpdo(1, new CanKit.Pro.CANopen.Pdo.PdoMapping().Add(0x2000, 0, 16));
        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)NmtCommand.Start, Device);
        using var driver = new SlowEmcyDriver(rig);
        var first = rig.Node.SendEmcyAsync(0x1000, 0);
        await driver.Entered;
        await Inject(rig.Node, Rpdo1, 0x01); // one byte for a 16-bit mapping: 8210h, queued behind 1000h
        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)NmtCommand.Stop, Device);
        driver.Release();
        await first.WaitAsync(ShortTimeout);
        await WaitForEmcyChainWaitingAsync(rig);

        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)NmtCommand.ResetCommunication, Device);
        await rig.Node.SendEmcyAsync(0x1002, 0).WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 2, 0x1002, 0);
        rig.Wire.Count(EmcyId).Should().Be(2, "the 8210h of an error the reset ended does not go out");
    }

    [Fact]
    public async Task Node_Error_Reset_That_Waited_In_Stopped_Is_Dropped_By_A_Reset_With_Default_Options()
    {
        using var rig = new EmcyInhibitRig(new CanOpenNodeOptions { EmitGuardingErrorEmcy = true });
        rig.Node.AddHeartbeatConsumer(1, TimeSpan.FromMilliseconds(100));
        Advance(rig.Clock, rig.Node, TimeSpan.FromMilliseconds(101));
        await ExpectEmcyAsync(rig.Wire, 1, 0x8130, Communication);
        using var driver = new SlowEmcyDriver(rig);
        var slow = rig.Node.SendEmcyAsync(0x1000, 0);
        await driver.Entered;
        await Inject(rig.Node, CanOpenCobId.Heartbeat(1), (byte)NmtState.PreOperational); // 0000h, queued
        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)NmtCommand.Stop, Device);
        driver.Release();
        await slow.WaitAsync(ShortTimeout);
        await WaitForEmcyChainWaitingAsync(rig);

        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)NmtCommand.ResetCommunication, Device);
        await rig.Node.SendEmcyAsync(0x1002, 0).WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 3, 0x1002, 0);
        rig.Wire.Count(EmcyId).Should().Be(3, "8130h, 1000h and 1002h: the reset ended the error without an EMCY, and the 0000h with it");
    }
}
