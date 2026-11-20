using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

// FR-CO-011, #577: an EMCY merely queued behind a slow send when an NMT reset is processed, which
// never waited in Stopped. One the node raised for its own error state (a communication error or
// its 0000h) is dropped, as one that waited in Stopped is (CanOpenEmcyStoppedTests,
// CanOpenEmcyWaitStepTests) and as the gated path drops it: the reset ended the error it reports
// (CiA 301 v4.2.0 §7.2.7.1, Figure 37, transitions 5 and 0). One the application ordered goes out
// after the reset, as it always has (3eb49f3e), and does not wait for the reset's boot-up. The
// first EMCY's send is held in the driver (SlowEmcyDriver), so the next ones are queued behind it.
public partial class CanOpenPdoEngineTests
{
    /// <summary>Records every value written to <c>1001h</c>, on whatever thread writes it.</summary>
    private static ConcurrentQueue<uint> RecordErrorRegisterWrites(EmcyInhibitRig rig)
    {
        var written = new ConcurrentQueue<uint>();
        var od = rig.Node.ObjectDictionary;
        od.EntryWritten += (index, _) =>
        {
            if (index == 0x1001) written.Enqueue(od.ReadUnsigned(0x1001, 0));
        };
        return written;
    }

    /// <summary>Sends 1000h, which the driver holds, and queues the 8130h of a heartbeat timeout
    /// behind it. Returns the 1000h's task.</summary>
    private static async Task<Task> QueueNodeEmcyBehindASlowSendAsync(EmcyInhibitRig rig, SlowEmcyDriver driver)
    {
        var first = rig.Node.SendEmcyAsync(0x1000, 0);
        await driver.Entered;
        rig.Node.AddHeartbeatConsumer(1, TimeSpan.FromMilliseconds(100));
        Advance(rig.Clock, rig.Node, TimeSpan.FromMilliseconds(101)); // 8130h, queued behind 1000h
        return first;
    }

    // The reproduction of #577.
    [Theory]
    [InlineData(NmtCommand.ResetCommunication)]
    [InlineData(NmtCommand.ResetNode)]
    public async Task Node_Emcy_Queued_Behind_A_Slow_Send_Is_Dropped_By_A_Reset_With_Default_Options(NmtCommand reset)
    {
        using var rig = new EmcyInhibitRig(NodeEmcyOptions);
        using var driver = new SlowEmcyDriver(rig);
        var registers = RecordErrorRegisterWrites(rig);
        var first = await QueueNodeEmcyBehindASlowSendAsync(rig, driver);

        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)reset, Device);
        rig.Node.State.Should().Be(NmtState.PreOperational);
        driver.Release();
        await first.WaitAsync(ShortTimeout);

        // The witness is queued behind the 8130h: had that gone out, it would be the second frame.
        await rig.Node.SendEmcyAsync(0x1002, 0).WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 2, 0x1002, 0);
        rig.Wire.Count(EmcyId).Should().Be(2, "the 8130h of an error the reset ended does not go out");
        rig.Node.ObjectDictionary.ReadUnsigned(0x1003, 0).Should().Be(2u, "1000h and 1002h; nothing for the dropped 8130h");
        registers.Should().NotContain(0x11u, "the dropped 8130h never wrote its register to 1001h");
        rig.Node.ObjectDictionary.ReadUnsigned(0x1001, 0).Should().Be(0);

        // The node is as if the error had never been announced: its cause ending sends no 0000h.
        await Inject(rig.Node, CanOpenCobId.Heartbeat(1), (byte)NmtState.PreOperational);
        await rig.Node.SendEmcyAsync(0x1003, 0).WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 3, 0x1003, 0);
        rig.Wire.Count(EmcyId).Should().Be(3, "no 0000h follows for an error the bus never saw");
    }

    [Theory]
    [InlineData(NmtCommand.ResetCommunication)]
    [InlineData(NmtCommand.ResetNode)]
    public async Task Node_Error_Reset_Queued_Behind_A_Slow_Send_Is_Dropped_By_A_Reset_With_Default_Options(NmtCommand reset)
    {
        using var rig = new EmcyInhibitRig(new CanOpenNodeOptions { EmitGuardingErrorEmcy = true });
        rig.Node.AddHeartbeatConsumer(1, TimeSpan.FromMilliseconds(100));
        Advance(rig.Clock, rig.Node, TimeSpan.FromMilliseconds(101));
        await ExpectEmcyAsync(rig.Wire, 1, 0x8130, Communication);
        using var driver = new SlowEmcyDriver(rig);
        var slow = rig.Node.SendEmcyAsync(0x1000, 0);
        await driver.Entered;
        await Inject(rig.Node, CanOpenCobId.Heartbeat(1), (byte)NmtState.PreOperational); // 0000h, queued

        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)reset, Device);
        driver.Release();
        await slow.WaitAsync(ShortTimeout);
        await rig.Node.SendEmcyAsync(0x1002, 0).WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 3, 0x1002, 0);
        rig.Wire.Count(EmcyId).Should().Be(3, "8130h, 1000h and 1002h: the reset ended the error without an EMCY, and the 0000h with it");
    }

    // The application's kind in the same place keeps the contract of 3eb49f3e: it goes out after the
    // reset, and it does not wait for the reset's boot-up, since it never waited in Stopped. The boot-up
    // is held in the driver; whichever comes first — the EMCY's task completing, or the EMCY starting to
    // wait for the boot-up — decides.
    [Theory]
    [InlineData(NmtCommand.ResetCommunication)]
    [InlineData(NmtCommand.ResetNode)]
    public async Task Application_Emcy_Queued_Behind_A_Slow_Send_Survives_A_Reset_Without_Waiting_For_Its_Boot_Up(NmtCommand reset)
    {
        using var rig = new EmcyInhibitRig(new CanOpenNodeOptions());
        using var driver = new SlowEmcyDriver(rig, holdBootUpOf: Device);
        var waitsForBootUp = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Node.HeldEmcysWaitForBootUpForTests = () => waitsForBootUp.TrySetResult(true);
        var first = rig.Node.SendEmcyAsync(0x1000, 0);
        await driver.Entered;
        var queued = rig.Node.SendEmcyAsync(0x1001, 0x04);

        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)reset, Device);
        await driver.BootUpEntered;
        driver.Release();
        await first.WaitAsync(ShortTimeout);
        var decided = await Task.WhenAny(queued, waitsForBootUp.Task).WaitAsync(ShortTimeout);
        decided.Should().BeSameAs(queued, "an EMCY that never waited in Stopped does not wait for the reset's boot-up");
        driver.BootUpLeft.Should().BeFalse("the boot-up is still held");
        await ExpectEmcyAsync(rig.Wire, 2, 0x1001, 0x04);
        rig.Node.ObjectDictionary.ReadUnsigned(0x1001, 0).Should().Be(0x04u, "the application's EMCY went out and wrote its register");
        driver.ReleaseBootUp();
    }

    // A state change other than a reset or Stop ends no error: a node EMCY queued across it goes out.
    // In Pre-operational, Enter Pre-operational changes nothing, so the node is started first for it.
    [Theory]
    [InlineData(NmtCommand.Start, NmtState.Operational)]
    [InlineData(NmtCommand.EnterPreOperational, NmtState.PreOperational)]
    public async Task Node_Emcy_Queued_Behind_A_Slow_Send_Goes_Out_After_A_State_Change_With_Default_Options(
        NmtCommand change, NmtState reached)
    {
        using var rig = new EmcyInhibitRig(NodeEmcyOptions);
        if (change == NmtCommand.EnterPreOperational)
            await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)NmtCommand.Start, Device);
        using var driver = new SlowEmcyDriver(rig);
        var first = await QueueNodeEmcyBehindASlowSendAsync(rig, driver);

        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)change, Device);
        rig.Node.State.Should().Be(reached);
        driver.Release();
        await first.WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 2, 0x8130, Communication);
        await rig.Node.SendEmcyAsync(0x1002, 0).WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 3, 0x1002, Communication);
        rig.Wire.Count(EmcyId).Should().Be(3);
        rig.Node.ObjectDictionary.ReadUnsigned(0x1003, 0).Should().Be(3u);
    }

    // The application's kind across the same state changes: it goes out, as it always has.
    [Theory]
    [InlineData(NmtCommand.Start, NmtState.Operational)]
    [InlineData(NmtCommand.EnterPreOperational, NmtState.PreOperational)]
    public async Task Application_Emcy_Queued_Behind_A_Slow_Send_Goes_Out_After_A_State_Change_With_Default_Options(
        NmtCommand change, NmtState reached)
    {
        using var rig = new EmcyInhibitRig(new CanOpenNodeOptions());
        if (change == NmtCommand.EnterPreOperational)
            await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)NmtCommand.Start, Device);
        using var driver = new SlowEmcyDriver(rig);
        var first = rig.Node.SendEmcyAsync(0x1000, 0);
        await driver.Entered;
        var queued = rig.Node.SendEmcyAsync(0x1001, 0x04);

        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)change, Device);
        rig.Node.State.Should().Be(reached);
        driver.Release();
        await first.WaitAsync(ShortTimeout);
        await queued.WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 2, 0x1001, 0x04);
        rig.Wire.Count(EmcyId).Should().Be(2);
    }

    // Disposing the node while an application EMCY is queued completes its task without a frame, as on
    // the gated path (Emcy_Inhibit_Disposal_Releases_The_Wait_And_Queued_Callers) and as for one that
    // waits (CanOpenEmcyWaitStepTests). The driver records a frame before the send completes, so once
    // the task has completed, a frame it sent would already be in the driver's list.
    [Fact]
    public async Task Application_Emcy_Queued_Behind_A_Slow_Send_Completes_Without_A_Frame_When_The_Node_Is_Disposed()
    {
        using var rig = new EmcyInhibitRig(new CanOpenNodeOptions());
        using var driver = new SlowEmcyDriver(rig);
        var first = rig.Node.SendEmcyAsync(0x1000, 0);
        await driver.Entered;
        var queued = rig.Node.SendEmcyAsync(0x1001, 0x04);
        await rig.Node.PostToActorAsync(() => { }); // the 1001h is on the chain behind the held 1000h

        rig.Node.Dispose();
        driver.Release();
        await queued.WaitAsync(ShortTimeout);
        queued.Status.Should().Be(TaskStatus.RanToCompletion);
        driver.Order.Count(id => id == EmcyId).Should().Be(1, "only the held 1000h reached the driver");
        _ = first.Exception;
    }

    // The gated path — EMCY inhibit (here with 1015h = 0) or error behaviour — has always dropped a
    // queued node EMCY on a reset (its epoch); pinned here for the same reproduction.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Node_Emcy_Queued_Behind_A_Slow_Send_Is_Dropped_By_A_Reset_On_The_Gated_Path(bool inhibit, bool errorBehavior)
    {
        using var rig = new EmcyInhibitRig(new CanOpenNodeOptions
        {
            EmitGuardingErrorEmcy = true,
            ErrorHistoryCapacity = 4,
            EnableEmcyInhibit = inhibit,
            EnableErrorBehavior = errorBehavior,
        });
        using var driver = new SlowEmcyDriver(rig);
        var first = await QueueNodeEmcyBehindASlowSendAsync(rig, driver);

        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)NmtCommand.ResetCommunication, Device);
        driver.Release();
        await first.WaitAsync(ShortTimeout);
        await rig.Node.SendEmcyAsync(0x1002, 0).WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 2, 0x1002, 0);
        rig.Wire.Count(EmcyId).Should().Be(2, "the 8130h of an error the reset ended does not go out");
        rig.Node.ObjectDictionary.ReadUnsigned(0x1003, 0).Should().Be(2u);
    }
}
