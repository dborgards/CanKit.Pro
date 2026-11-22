using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

// FR-CO-074
public partial class CanOpenPdoEngineTests
{
    private const ushort NmtInhibit = 0x102A;

    [Theory]
    [InlineData(1)]
    [InlineData(65535)]
    public async Task Nmt_Inhibit_Uses_100us_Units_And_Preserves_Command_Order(int units)
    {
        using var rig = new EmcyInhibitRig(new CanOpenNodeOptions { EnableNmtInhibit = true }.With());
        rig.Node.ObjectDictionary.ReadUnsigned(NmtInhibit, 0).Should().Be(0);
        rig.Node.ObjectDictionary.WriteUnsigned(NmtInhibit, 0, (uint)units);
        await rig.Node.SendNmtCommandAsync(NmtCommand.Stop, 1).WaitAsync(ShortTimeout);
        var next = rig.Node.SendNmtCommandAsync(NmtCommand.Start, 2);
        var last = rig.Node.SendNmtCommandAsync(NmtCommand.ResetNode, 3);
        Advance(rig.Clock, rig.Node, TimeSpan.FromTicks(units * 1000L - 1));
        next.IsCompleted.Should().BeFalse();
        Advance(rig.Clock, rig.Node, TimeSpan.FromTicks(1));
        await next.WaitAsync(ShortTimeout);
        last.IsCompleted.Should().BeFalse();
        Advance(rig.Clock, rig.Node, TimeSpan.FromTicks(units * 1000L));
        await last.WaitAsync(ShortTimeout);
        await rig.Wire.WaitForCountAsync(0, 3);
        rig.Wire.Payloads(0).Select(frame => frame[1]).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task Nmt_Inhibit_Measures_From_Send_Completion_When_Handoff_Is_Delayed()
    {
        using var rig = new EmcyInhibitRig(new CanOpenNodeOptions { EnableNmtInhibit = true });
        rig.Node.ObjectDictionary.WriteUnsigned(NmtInhibit, 0, 10);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Node.BeforeControlFrameSendForTests = id => id == 0 && entered.TrySetResult(true)
            ? release.Task : Task.CompletedTask;
        Task first = Task.CompletedTask;
        await rig.Node.PostToActorAsync(() => first = rig.Node.SendNmtCommandAsync(NmtCommand.Stop, 1));
        try
        {
            await entered.Task.WaitAsync(ShortTimeout);
            Advance(rig.Clock, rig.Node, TimeSpan.FromSeconds(1));
        }
        finally { release.TrySetResult(true); }
        await first.WaitAsync(ShortTimeout);
        var second = rig.Node.SendNmtCommandAsync(NmtCommand.Start, 2);
        Settle(rig.Node);
        second.IsCompleted.Should().BeFalse();
        Advance(rig.Clock, rig.Node, TimeSpan.FromMilliseconds(1));
        await second.WaitAsync(ShortTimeout);
    }

    [Fact]
    public async Task Nmt_Inhibit_Reset_Reapplies_Stored_Time_And_Cancellation_Removes_A_Queued_Command()
    {
        using var rig = new EmcyInhibitRig(new CanOpenNodeOptions { EnableNmtInhibit = true });
        rig.Node.StoreParameters();
        rig.Node.ObjectDictionary.WriteUnsigned(NmtInhibit, 0, 65535);
        await rig.Node.SendNmtCommandAsync(NmtCommand.Stop, 1).WaitAsync(ShortTimeout);
        var waiting = rig.Node.SendNmtCommandAsync(NmtCommand.Start, 2);
        using var cancel = new CancellationTokenSource();
        var cancelled = rig.Node.SendNmtCommandAsync(NmtCommand.ResetNode, 3, cancel.Token);
        cancel.Cancel();
        await FluentActions.Awaiting(() => cancelled.WaitAsync(ShortTimeout)).Should().ThrowAsync<OperationCanceledException>();
        await Inject(rig.Node, 0, (byte)NmtCommand.ResetCommunication, Device);
        await waiting.WaitAsync(ShortTimeout);
        await rig.Node.SendNmtCommandAsync(NmtCommand.Start, 4).WaitAsync(ShortTimeout);
        await rig.Wire.WaitForCountAsync(0, 3);
        rig.Wire.Payloads(0).Select(frame => frame[1]).Should().Equal(1, 2, 4);
        rig.Node.ObjectDictionary.ReadUnsigned(NmtInhibit, 0).Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Nmt_Inhibit_Applies_A_Described_Interval_When_Enabled(bool enabled)
    {
        var description = CanOpenDeviceDescription.ParseEds("""
            [FileInfo]
            FileName=nmt.eds
            FileVersion=1
            FileRevision=0
            EDSVersion=4.0
            [DeviceInfo]
            VendorName=Tests
            VendorNumber=0
            ProductName=Nmt
            ProductNumber=0
            RevisionNumber=0
            [MandatoryObjects]
            SupportedObjects=0
            [OptionalObjects]
            SupportedObjects=1
            1=0x102A
            [ManufacturerObjects]
            SupportedObjects=0
            [102A]
            ParameterName=NMT inhibit time
            ObjectType=0x7
            DataType=0x0006
            AccessType=rw
            DefaultValue=10
            PDOMapping=0
            """);
        using var rig = new EmcyInhibitRig(new CanOpenNodeOptions { EnableNmtInhibit = enabled }, description);
        rig.Node.ObjectDictionary.ReadUnsigned(NmtInhibit, 0).Should().Be(10);
        await rig.Node.SendNmtCommandAsync(NmtCommand.Stop, 1).WaitAsync(ShortTimeout);
        var next = rig.Node.SendNmtCommandAsync(NmtCommand.Start, 2);
        if (enabled)
        {
            Settle(rig.Node);
            next.IsCompleted.Should().BeFalse();
            Advance(rig.Clock, rig.Node, TimeSpan.FromMilliseconds(1));
        }
        await next.WaitAsync(ShortTimeout);
        await rig.Wire.WaitForCountAsync(0, 2);
    }

    [Fact]
    public async Task Nmt_Inhibit_Dispose_Cancels_The_Head_And_Its_Successor()
    {
        using var rig = new EmcyInhibitRig(new CanOpenNodeOptions { EnableNmtInhibit = true });
        rig.Node.ObjectDictionary.WriteUnsigned(NmtInhibit, 0, 100);
        await rig.Node.SendNmtCommandAsync(NmtCommand.Start, 1).WaitAsync(ShortTimeout);
        var head = rig.Node.SendNmtCommandAsync(NmtCommand.Start, 2);
        var queued = rig.Node.SendNmtCommandAsync(NmtCommand.Start, 3);
        Settle(rig.Node);
        rig.Node.Dispose();
        foreach (var send in new[] { head, queued })
            await FluentActions.Awaiting(() => send.WaitAsync(ShortTimeout)).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Nmt_Inhibit_Stop_Master_Withdraws_Its_Waiting_Request()
    {
        using var rig = FlyingMasterRig.OpenMaster(new CanOpenNodeOptions { EnableNmtInhibit = true });
        var od = rig.Node.ObjectDictionary;
        od.WriteUnsigned(0x1F81, 2, FlyingMasterRig.Assigned);
        od.WriteUnsigned(FlyingMasterRig.Startup, 0, FlyingMasterRig.SuppressSlaveStart);
        FlyingMasterRig.Tighten(rig.Node);
        rig.Node.StartFlyingMaster(0, FlyingMasterRig.Heartbeat);
        await FlyingMasterRig.UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Node.FlyingMasterRole == FlyingMasterRole.Active, 800, "the master is active");
        rig.Log.Clear();
        await rig.Node.SendNmtCommandAsync(NmtCommand.Start, 1).WaitAsync(ShortTimeout);
        od.WriteUnsigned(NmtInhibit, 0, 65535);
        od.WriteUnsigned(0x1F82, 2, (byte)NmtState.Operational);
        Settle(rig.Node);
        rig.Node.StopFlyingMaster();
        od.WriteUnsigned(NmtInhibit, 0, 0);
        await rig.Node.SendNmtCommandAsync(NmtCommand.Stop, 3).WaitAsync(ShortTimeout);
        await FlyingMasterRig.QuiesceAsync(rig.Witness, null);
        rig.Log.Snapshot().Where(frame => frame.Id == 0).Select(frame => frame.Data[1]).Should().Equal(1, 3);
    }

    [Fact]
    public async Task Nmt_Inhibit_Master_Request_And_Public_Command_Share_The_Interval()
    {
        using var rig = FlyingMasterRig.OpenMaster(new CanOpenNodeOptions { EnableNmtInhibit = true });
        var od = rig.Node.ObjectDictionary;
        od.WriteUnsigned(0x1F81, 2, FlyingMasterRig.Assigned);
        od.WriteUnsigned(FlyingMasterRig.Startup, 0, FlyingMasterRig.SuppressSelfStart | FlyingMasterRig.SuppressSlaveStart);
        FlyingMasterRig.Tighten(rig.Node);
        rig.Node.StartFlyingMaster(0, FlyingMasterRig.Heartbeat);
        await FlyingMasterRig.UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Node.FlyingMasterRole == FlyingMasterRole.Active, 800, "the master is active");
        rig.Log.Clear();
        od.WriteUnsigned(0x1F82, 2, (byte)NmtState.Operational);
        Settle(rig.Node);
        await rig.Node.NmtQueueForTests.WaitAsync(ShortTimeout);
        od.WriteUnsigned(NmtInhibit, 0, 1000);
        var pending = rig.Node.SendNmtCommandAsync(NmtCommand.Stop, 3);
        Advance(rig.Clock, rig.Node, TimeSpan.FromMilliseconds(99));
        pending.IsCompleted.Should().BeFalse();
        od.WriteUnsigned(NmtInhibit, 0, 0);
        await pending.WaitAsync(ShortTimeout);
        await FlyingMasterRig.QuiesceAsync(rig.Witness, null);
        rig.Log.Snapshot().Where(frame => frame.Id == 0).Select(frame => frame.Data[1]).Should().Equal(2, 3);
    }
}
