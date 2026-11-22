using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using Xunit;
using static CanKit.Pro.Tests.TestCases.CANopen.FlyingMasterRig;

namespace CanKit.Pro.Tests.TestCases.CANopen;

// FR-CO-064: lifecycle boundaries are actor turns, not wall-clock races. As in the safety
// boot tests, deliver an already-computed result at the boundary the worker normally posts to.
public partial class CanOpenBootIdentityTests
{
    private static object BootField(CanOpenNode node, string name)
        => typeof(CanOpenNode).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(node)!;

    private static void SetBootField(CanOpenNode node, string name, object value)
        => typeof(CanOpenNode).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(node, value);

    private static void DeliverIdentityResult(CanOpenNode node, int generation, SlaveBootResult result)
        => typeof(CanOpenNode).GetMethod("OnSlaveIdentityVerified", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(node, new object[] { Slave, generation, result });

    [Fact]
    public void Boot_Results_Reject_Null_And_Disposed_Nodes()
    {
        Assert.Throws<ArgumentNullException>(() => CanOpenNodeExtensions.GetSlaveBootResults(null!));
        using var rig = OpenMaster();
        rig.Node.Dispose();
        Assert.Throws<ObjectDisposedException>(() => CanOpenNodeExtensions.GetSlaveBootResults(rig.Node));
    }

#if NET5_0_OR_GREATER
    [Fact]
    public void Boot_Results_Reject_A_Foreign_Node_Without_Calling_It()
    {
        var foreign = DispatchProxy.Create<ICanOpenNode, ForeignBootNode>();
        Assert.Throws<NotSupportedException>(() => foreign.GetSlaveBootResults());
    }

    private class ForeignBootNode : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw new InvalidOperationException("A foreign implementation must not be called.");
    }
#endif

    [Fact]
    public async Task Boot_Results_Can_Be_Read_On_The_Actor_Without_Reposting()
    {
        using var rig = OpenMaster();
        Configure(rig);
        await Elect(rig);
        await rig.Node.PostToActorAsync(() =>
            CanOpenNodeExtensions.GetSlaveBootResults(rig.Node).Single().Status
                .Should().Be(SlaveBootStatus.WaitingForAnnouncement)).WaitAsync(ShortTimeout);
    }

    [Fact]
    public async Task A_Second_Slave_Uses_The_Same_Boot_And_Starts_Only_After_Its_Own_Check()
    {
        const byte other = 6;
        using var rig = OpenMaster();
        using var first = rig.OpenPeerNode(Slave);
        using var second = rig.OpenPeerNode(other);
        Configure(rig);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F81, other, Assigned | BootSlave | 0x10u);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F85, Slave, 123);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F85, other, 456);
        SetRemote(first, 0x1018, 1, 123);
        SetRemote(second, 0x1018, 1, 456);
        await Elect(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Started(rig, Slave), 200, "the first slave verified");
        Started(rig, other).Should().BeFalse();
        TransmitHeartbeat(rig.Peer, other, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Started(rig, other), 200, "the second slave verified");
        rig.Node.GetSlaveBootResults().Select(r => r.NodeId).Should().Equal(Slave, other);
        rig.Node.GetSlaveBootResults().Should().OnlyContain(r => r.Status == SlaveBootStatus.StartRequested);
    }

    [Fact]
    public async Task A_Stale_Result_Cannot_Complete_A_Newer_Verification()
    {
        using var rig = OpenMaster();
        Configure(rig);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F84, Slave, 1);
        await Elect(rig);
        using var cancel = new CancellationTokenSource();
        var occupying = rig.Node.SdoUploadAsync(Slave, 0x1001, 0, cancellationToken: cancel.Token);
        using var allowedWait = rig.Witness.AllowPeerVerificationWait(Slave);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => Result(rig).Status == SlaveBootStatus.VerifyingIdentity, 200, "the first boot waits");
        int stale = 0;
        await rig.Node.PostToActorAsync(() => stale = (int)BootField(rig.Node, "_bootGeneration"));
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F84, Slave, 2);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => Result(rig).Status == SlaveBootStatus.VerifyingIdentity, 200, "the new boot waits");
        await rig.Node.PostToActorAsync(() => DeliverIdentityResult(rig.Node, stale,
            new SlaveBootResult(Slave, SlaveBootStatus.IdentityVerified)));
        Result(rig).Status.Should().Be(SlaveBootStatus.VerifyingIdentity);
        Started(rig, Slave).Should().BeFalse();
        using var peer = rig.OpenPeerNode(Slave);
        SetRemote(peer, 0x1000, 0, 1);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => occupying);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => Result(rig).Status == SlaveBootStatus.IdentityMismatch, 200, "only the new expectation decides");
        Result(rig).ExpectedValue.Should().Be(2);
        Started(rig, Slave).Should().BeFalse();
    }

    [Fact]
    public async Task Broadcast_Start_Preserves_Already_Operational_And_Latches_Start_Requested()
    {
        const byte running = 6, disabled = 7, unseen = 8;
        using var rig = OpenMaster();
        Configure(rig, simultaneous: true);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F81, running, Assigned | BootSlave | 0x10u);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F81, disabled, Assigned | 0x10u);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F81, unseen, Assigned | BootSlave | 0x10u);
        await Elect(rig);
        TransmitHeartbeat(rig.Peer, running, 0x05);
        TransmitHeartbeat(rig.Peer, disabled, 0x7F);
        await QuiesceAsync(rig.Witness, null);
        var runningBefore = rig.Node.GetSlaveBootResults().Single(r => r.NodeId == running);
        runningBefore.Status.Should().Be(SlaveBootStatus.AlreadyOperational);
        Started(rig, 0).Should().BeFalse("the mandatory slave has not announced");
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Started(rig, 0), 200, "the network starts together");
        var results = rig.Node.GetSlaveBootResults();
        results.Single(r => r.NodeId == running).Should().BeSameAs(runningBefore);
        results.Single(r => r.NodeId == disabled).Status.Should().Be(SlaveBootStatus.Announced);
        results.Single(r => r.NodeId == unseen).Status.Should().Be(SlaveBootStatus.WaitingForAnnouncement);
        var started = Result(rig);
        started.Status.Should().Be(SlaveBootStatus.StartRequested);

        TransmitHeartbeat(rig.Peer, Slave, 0x05);
        await QuiesceAsync(rig.Witness, null);
        Result(rig).Should().BeSameAs(started, "an Operational heartbeat does not rewrite how boot started the slave");
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await QuiesceAsync(rig.Witness, null);
        Result(rig).Should().BeSameAs(started);
        Started(rig, Slave).Should().BeFalse("the broadcast already queued its Start");
        rig.Log.Snapshot().Count(f => IsNmt(f, NmtCommand.Start, 0)).Should().Be(1);
    }

    [Fact]
    public async Task A_Duplicate_Result_Does_Not_Overwrite_An_Already_Completed_Boot()
    {
        using var rig = OpenMaster();
        using var peer = rig.OpenPeerNode(Slave);
        Configure(rig);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F85, Slave, 123);
        SetRemote(peer, 0x1018, 1, 123);
        await Elect(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Started(rig, Slave), 200, "boot completed");
        var before = Result(rig);
        await rig.Node.PostToActorAsync(() => DeliverIdentityResult(rig.Node,
            (int)BootField(rig.Node, "_bootGeneration"), new SlaveBootResult(Slave, SlaveBootStatus.IdentityMismatch)));
        Result(rig).Should().BeSameAs(before);
        rig.Node.State.Should().Be(NmtState.Operational);
    }

    [Theory]
    [InlineData("_disposed", 1)]
    [InlineData("_flyingMasterRole", FlyingMasterRole.Standby)]
    [InlineData("_bootHalted", true)]
    [InlineData("_coldResetPending", true)]
    public async Task A_Result_Cannot_Start_A_Slave_Across_A_Lifecycle_Barrier(string field, object barrier)
    {
        using var rig = OpenMaster();
        Configure(rig);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F85, Slave, 123);
        await Elect(rig);
        var before = Result(rig);
        await rig.Node.PostToActorAsync(() =>
        {
            // Pin the exact turn where the barrier is visible but queued completion still
            // carries the current generation. Restore it before normal disposal of the rig.
            var old = BootField(rig.Node, field);
            ((bool[])BootField(rig.Node, "_slaveIdentityVerifying"))[Slave] = true;
            SetBootField(rig.Node, field, barrier);
            try
            {
                DeliverIdentityResult(rig.Node, (int)BootField(rig.Node, "_bootGeneration"),
                    new SlaveBootResult(Slave, SlaveBootStatus.IdentityVerified));
                ((bool[])BootField(rig.Node, "_slaveIdentityVerified"))[Slave].Should().BeFalse();
            }
            finally { SetBootField(rig.Node, field, old); }
        });
        Result(rig).Should().BeSameAs(before);
        Started(rig, Slave).Should().BeFalse();
        rig.Node.State.Should().Be(NmtState.PreOperational);
    }

    [Fact]
    public void A_Result_Posted_After_The_Actor_Is_Disposed_Is_Discarded()
    {
        using var rig = OpenMaster();
        rig.Node.Dispose();
        // The worker can pass its cancellation check just before Dispose closes the mailbox.
        Action post = () => typeof(CanOpenNode).GetMethod("PostSlaveIdentityResult", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(rig.Node, new object[] { Slave, 0, new SlaveBootResult(Slave, SlaveBootStatus.IdentityVerified), CancellationToken.None });
        post.Should().NotThrow();
        Started(rig, Slave).Should().BeFalse();
    }

    [Fact]
    public async Task An_Announcement_After_Boot_Cancellation_Does_Not_Recreate_Results()
    {
        using var rig = OpenMaster();
        Configure(rig);
        await Elect(rig);
        await rig.Node.PostToActorAsync(() =>
        {
            typeof(CanOpenNode).GetMethod("CancelBootUp", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(rig.Node, null);
            SetBootField(rig.Node, "_coldResetPending", true);
        });
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await QuiesceAsync(rig.Witness, null);
        rig.Node.GetSlaveBootResults().Should().BeEmpty();
        Started(rig, Slave).Should().BeFalse();
        await rig.Node.PostToActorAsync(() => SetBootField(rig.Node, "_coldResetPending", false));
    }
}
