using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using EdsDcfNet.Models;
using Xunit;
using static CanKit.Pro.Tests.TestCases.CANopen.FlyingMasterRig;

namespace CanKit.Pro.Tests.TestCases.CANopen;

public partial class CanOpenConciseDcfTests
{
    [Fact]
    public async Task Input_Is_Copied_Before_Waiting_And_Description_Is_Captured_After_Waiting()
    {
        using var rig = OpenMaster();
        using var peer = rig.OpenPeerNode(Peer);
        peer.ObjectDictionary.AddDomain(0x2000, 0, Array.Empty<byte>());
        Bind(rig, 0x2001); // Replaced while the transaction waits for ownership.
        using var held = rig.Witness.HoldPeerSdoChannel(Peer, Channel(rig));
        var data = Dcf((0x2000, 0, new byte[] { 42 }));
        var download = rig.Node.DownloadConciseDcfAsync(Peer, data);
        download.IsCompleted.Should().BeFalse();
        Requests(rig).Should().Be(0);
        data[data.Length - 1] = 99;
        Bind(rig, 0x2000);
        held.Dispose();
        await Complete(rig, download);
        peer.ObjectDictionary.ReadRaw(0x2000, 0).Should().Equal(42);
    }

    [Fact]
    public async Task Description_And_Data_Remain_Pinned_Between_Acknowledged_Writes()
    {
        using var rig = OpenMaster();
        using var peer = rig.OpenPeerNode(Peer);
        peer.ObjectDictionary.AddDomain(0x2000, 0, Array.Empty<byte>());
        peer.ObjectDictionary.AddDomain(0x2001, 0, Array.Empty<byte>());
        var description = Description(0x2000, 0x2001);
        rig.Node.BindPeerDeviceDescription(Peer, description);
        var data = Dcf((0x2000, 0, new byte[] { 1 }), (0x2001, 0, new byte[] { 42 }));
        rig.Node.AfterConciseDcfWriteForTests = () =>
        {
            description.Objects.Objects[0x2001].AccessType = AccessType.ReadOnly;
            rig.Node.UnbindPeerDeviceDescription(Peer);
            data[data.Length - 1] = 99;
            return Task.CompletedTask;
        };
        await Complete(rig, rig.Node.DownloadConciseDcfAsync(Peer, data));
        peer.ObjectDictionary.ReadRaw(0x2001, 0).Should().Equal(42);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Node.DownloadConciseDcfAsync(Peer, data));
    }

    [Fact]
    public async Task The_Channel_Remains_Owned_Between_Records_While_Other_Peers_Can_Proceed()
    {
        using var rig = OpenMaster();
        using var peer = rig.OpenPeerNode(Peer);
        using var other = rig.OpenPeerNode(6);
        peer.ObjectDictionary.AddDomain(0x2000, 0, Array.Empty<byte>());
        peer.ObjectDictionary.AddDomain(0x2001, 0, Array.Empty<byte>());
        other.ObjectDictionary.AddDomain(0x2000, 0, Array.Empty<byte>());
        Bind(rig, 0x2000, 0x2001);
        rig.Node.BindPeerDeviceDescription(6, Description(0x2000));
        var paused = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int completed = 0;
        rig.Node.AfterConciseDcfWriteForTests = () =>
        {
            if (Interlocked.Increment(ref completed) != 1) return Task.CompletedTask;
            paused.SetResult(true);
            return resume.Task;
        };
        var download = rig.Node.DownloadConciseDcfAsync(Peer, Dcf((0x2000, 0, new byte[] { 1 }), (0x2001, 0, new byte[] { 2 })));
        try
        {
            await UntilAsync(rig.Clock, rig.Witness, null, () => paused.Task.IsCompleted, 200, "the first SDO was acknowledged");
            rig.Node.PeerSdoChannelIsFreeForTests(Peer).Should().BeFalse();
            await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Node.SdoUploadAsync(Peer, 0x2000, 0));
            await Complete(rig, rig.Node.DownloadConciseDcfAsync(6, Dcf((0x2000, 0, new byte[] { 3 }))));
            peer.ObjectDictionary.ReadRaw(0x2001, 0).Should().BeEmpty();
        }
        finally { resume.TrySetResult(true); }
        await Complete(rig, download);
        peer.ObjectDictionary.ReadRaw(0x2001, 0).Should().Equal(2);
        rig.Node.PeerSdoChannelIsFreeForTests(Peer).Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_Or_Disposal_While_Waiting_Does_Not_Release_Another_Owners_Lease(bool dispose)
    {
        using var rig = OpenMaster();
        Bind(rig, 0x2000);
        using var cancellation = new CancellationTokenSource();
        using var held = rig.Witness.HoldPeerSdoChannel(Peer, Channel(rig));
        var download = rig.Node.DownloadConciseDcfAsync(Peer, Dcf((0x2000, 0, new byte[] { 1 })), cancellation.Token);
        download.IsCompleted.Should().BeFalse();
        if (dispose) rig.Node.Dispose();
        else cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await download.WaitAsync(ShortTimeout));
        Channel(rig).CurrentCount.Should().Be(0);
        Requests(rig).Should().Be(0);
        held.Dispose();
        Channel(rig).CurrentCount.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_Or_Disposal_Between_Records_Stops_The_Remaining_Writes(bool dispose)
    {
        using var rig = OpenMaster();
        using var peer = rig.OpenPeerNode(Peer);
        peer.ObjectDictionary.AddDomain(0x2000, 0, Array.Empty<byte>());
        peer.ObjectDictionary.AddDomain(0x2001, 0, Array.Empty<byte>());
        Bind(rig, 0x2000, 0x2001);
        using var cancellation = new CancellationTokenSource();
        var paused = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Node.AfterConciseDcfWriteForTests = () =>
        {
            paused.TrySetResult(true);
            return resume.Task;
        };
        var download = rig.Node.DownloadConciseDcfAsync(Peer, Dcf((0x2000, 0, new byte[] { 1 }), (0x2001, 0, new byte[] { 2 })), cancellation.Token);
        try
        {
            await UntilAsync(rig.Clock, rig.Witness, null, () => paused.Task.IsCompleted, 200, "the first write was acknowledged");
            if (dispose) rig.Node.Dispose();
            else cancellation.Cancel();
        }
        finally { resume.TrySetResult(true); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await download.WaitAsync(ShortTimeout));
        peer.ObjectDictionary.ReadRaw(0x2000, 0).Should().Equal(1);
        peer.ObjectDictionary.ReadRaw(0x2001, 0).Should().BeEmpty();
        rig.Node.PeerSdoChannelIsFreeForTests(Peer).Should().BeTrue();
    }

    [Fact]
    public async Task Cancellation_During_An_In_Flight_Sdo_Releases_The_Lease()
    {
        using var rig = OpenMaster();
        Bind(rig, 0x2000, 0x2001);
        using var cancellation = new CancellationTokenSource();
        var download = rig.Node.DownloadConciseDcfAsync(Peer, Dcf((0x2000, 0, new byte[] { 1 }), (0x2001, 0, new byte[] { 2 })), cancellation.Token);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Requests(rig) > 0, 100, "the first SDO waits for a reply");
        cancellation.Cancel();
        await CompleteCancellation(rig, download);
        rig.Node.PeerSdoChannelIsFreeForTests(Peer).Should().BeTrue();
    }

    private static async Task CompleteCancellation(MasterRig rig, Task download)
    {
        await UntilAsync(rig.Clock, rig.Witness, null, () => download.IsCompleted, 100, "the SDO is cancelled");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(128)]
    [InlineData(255)]
    public async Task Invalid_Node_Ids_Are_Rejected(byte nodeId)
    {
        using var rig = OpenMaster();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => rig.Node.DownloadConciseDcfAsync(nodeId, new byte[4]));
        Requests(rig).Should().Be(0);
    }

    [Fact]
    public async Task A_PreCancelled_Call_Sends_Nothing_Even_For_An_Empty_List()
    {
        using var rig = OpenMaster();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Node.DownloadConciseDcfAsync(Peer, new byte[4], cancellation.Token));
        Requests(rig).Should().Be(0);
    }

    [Fact]
    public async Task Null_And_Disposed_Nodes_Are_Rejected()
    {
        Assert.Throws<ArgumentNullException>(() => { _ = CanOpenNodeExtensions.DownloadConciseDcfAsync(null!, Peer, new byte[4]); });
        using var rig = OpenMaster();
        rig.Node.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => rig.Node.DownloadConciseDcfAsync(Peer, new byte[4]));
    }

#if NET5_0_OR_GREATER
    [Fact]
    public void A_Foreign_Node_Is_Refused_Without_Calling_It()
    {
        var foreign = DispatchProxy.Create<ICanOpenNode, ForeignDcfNode>();
        Assert.Throws<NotSupportedException>(() => { _ = foreign.DownloadConciseDcfAsync(Peer, new byte[4]); });
    }

    private class ForeignDcfNode : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
            => throw new InvalidOperationException("The foreign node must not be called.");
    }
#endif
}
