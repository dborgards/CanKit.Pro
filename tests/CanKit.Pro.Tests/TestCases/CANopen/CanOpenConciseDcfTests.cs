using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.Tests.Infrastructure;
using EdsDcfNet.Models;
using Xunit;
using static CanKit.Pro.Tests.TestCases.CANopen.FlyingMasterRig;

namespace CanKit.Pro.Tests.TestCases.CANopen;

// FR-CO-068: explicit CiA 302-3 §3.3 concise-DCF download, not automatic boot or safety commissioning.
public partial class CanOpenConciseDcfTests : IClassFixture<VirtualAdapterFixture>
{
    private const byte Peer = 5;

    private static byte[] Dcf(params (ushort Index, byte Subindex, byte[] Value)[] writes)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((uint)writes.Length);
        foreach (var write in writes)
        {
            writer.Write(write.Index);
            writer.Write(write.Subindex);
            writer.Write((uint)write.Value.Length);
            writer.Write(write.Value);
        }
        return stream.ToArray();
    }

    private static CanOpenDeviceDescription Description(params ushort[] indices)
    {
        var description = PeerSdoLaboratory.Variables(indices);
        foreach (var index in indices) description.Objects.Objects[index].DataType = (ushort)OdDataType.Domain;
        return description;
    }

    private static void Bind(MasterRig rig, params ushort[] indices)
        => rig.Node.BindPeerDeviceDescription(Peer, Description(indices));

    private static async Task Complete(MasterRig rig, Task task)
    {
        await UntilAsync(rig.Clock, rig.Witness, null, () => task.IsCompleted, 500, "the download completes");
        await task;
    }

    private static SemaphoreSlim Channel(MasterRig rig)
        => (SemaphoreSlim)typeof(CanOpenNode).GetMethod("PeerSdoChannel", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(rig.Node, new object[] { Peer })!;

    private static int Requests(MasterRig rig)
        => rig.Log.Snapshot().Count(f => f.Id == 0x600 + Peer);

    [Fact]
    public async Task Literal_Little_Endian_Stream_Preserves_Order_And_Repeated_Pairs()
    {
        using var rig = OpenMaster();
        using var peer = rig.OpenPeerNode(Peer);
        peer.ObjectDictionary.AddDomain(0x2345, 0, Array.Empty<byte>());
        peer.ObjectDictionary.AddDomain(0x2000, 0, Array.Empty<byte>());
        Bind(rig, 0x2345, 0x2000, 0x2002); // An unrelated declared object is not part of the download.
        var observed = new ConcurrentQueue<(ushort, byte[])>();
        peer.ObjectDictionary.EntryWritten += (index, sub) => observed.Enqueue((index, peer.ObjectDictionary.ReadRaw(index, sub)));
        // Literal bytes independently specify the count, indexes and lengths from Figure 1.
        byte[] data = { 3, 0, 0, 0, 0x45, 0x23, 0, 1, 0, 0, 0, 0xA1,
            0, 0x20, 0, 2, 0, 0, 0, 0xB1, 0xB2, 0x45, 0x23, 0, 1, 0, 0, 0, 0xC1 };
        await Complete(rig, rig.Node.DownloadConciseDcfAsync(Peer, data));
        observed.Select(w => w.Item1).Should().Equal(0x2345, 0x2000, 0x2345);
        observed.SelectMany(w => w.Item2).Should().Equal(0xA1, 0xB1, 0xB2, 0xC1);
        rig.Log.Snapshot().Should().NotContain(f => f.Id == 0, "an explicit download sends no NMT command");
        rig.Node.PeerSdoChannelIsFreeForTests(Peer).Should().BeTrue();
    }

    [Theory]
    [InlineData(0, 0x20)]
    [InlineData(3, 0x20)]
    [InlineData(17, 0x20)]
    [InlineData(256, 0xC0)]
    public async Task Auto_Download_Transfers_Empty_Expedited_Segmented_And_Block_Values(int length, byte command)
    {
        using var rig = OpenMaster(new CanOpenNodeOptions { SdoBlockThresholdBytes = 128 });
        using var peer = rig.OpenPeerNode(Peer);
        peer.ObjectDictionary.AddDomain(0x2000, 0, new byte[] { 42 });
        Bind(rig, 0x2000);
        var value = Enumerable.Range(0, length).Select(i => (byte)i).ToArray();
        await Complete(rig, rig.Node.DownloadConciseDcfAsync(Peer, Dcf((0x2000, 0, value))));
        peer.ObjectDictionary.ReadRaw(0x2000, 0).Should().Equal(value);
        (rig.Log.Snapshot().First(f => f.Id == 0x600 + Peer).Data[0] & 0xE0).Should().Be(command);
    }

    [Fact]
    public async Task Empty_List_Is_A_No_Op_Without_A_Description_Or_Channel_Lease()
    {
        using var rig = OpenMaster();
        using var held = rig.Witness.HoldPeerSdoChannel(Peer, Channel(rig));
        await rig.Node.DownloadConciseDcfAsync(Peer, new byte[4]);
        Requests(rig).Should().Be(0);
        rig.Node.PeerSdoChannelIsFreeForTests(Peer).Should().BeFalse();
    }

    public static IEnumerable<object[]> MalformedStreams()
    {
        for (int i = 0; i < 4; i++) yield return new object[] { new byte[i] };
        yield return new object[] { new byte[] { 0xFF, 0xFF, 0xFF, 0xFF } };
        yield return new object[] { new byte[] { 1, 0, 0, 0, 0, 0x20, 0 } };
        yield return new object[] { new byte[] { 1, 0, 0, 0, 0, 0x20, 0, 0xFF, 0xFF, 0xFF, 0xFF } };
        yield return new object[] { new byte[] { 0, 0, 0, 0, 42 } };
        yield return new object[] { Dcf((0x2000, 0, new byte[] { 1 })).Concat(new byte[] { 42 }).ToArray() };
        // Enough bytes for two headers in total, but the first record consumes the second header's space.
        var truncated = Dcf((0x2000, 0, new byte[10]), (0x2001, 0, Array.Empty<byte>()));
        yield return new object[] { truncated.Take(truncated.Length - 6).ToArray() };
        var payload = Dcf((0x2000, 0, new byte[] { 1 }), (0x2001, 0, new byte[2]));
        yield return new object[] { payload.Take(payload.Length - 1).ToArray() };
    }

    [Theory]
    [MemberData(nameof(MalformedStreams))]
    public async Task The_Whole_Stream_Is_Validated_Before_Any_Write(byte[] data)
    {
        using var rig = OpenMaster();
        Bind(rig, 0x2000, 0x2001);
        await Assert.ThrowsAsync<InvalidDataException>(() => Complete(rig, rig.Node.DownloadConciseDcfAsync(Peer, data)));
        await QuiesceAsync(rig.Witness, null);
        Requests(rig).Should().Be(0);
        rig.Node.PeerSdoChannelIsFreeForTests(Peer).Should().BeTrue();
    }

    [Theory]
    [InlineData(0)] // no description
    [InlineData(1)] // missing second index
    [InlineData(2)] // read-only second record
    [InlineData(3)] // constant second record
    [InlineData(4)] // missing second sub-index
    public async Task All_Access_Checks_Precede_The_First_Write(int refusal)
    {
        using var rig = OpenMaster();
        if (refusal != 0)
        {
            var description = Description(refusal == 1 ? new ushort[] { 0x2000 } : new ushort[] { 0x2000, 0x2001 });
            if (refusal is 2 or 3) description.Objects.Objects[0x2001].AccessType = refusal == 2 ? AccessType.ReadOnly : AccessType.Constant;
            rig.Node.BindPeerDeviceDescription(Peer, description);
        }
        var data = Dcf((0x2000, 0, new byte[] { 1 }), (0x2001, refusal == 4 ? (byte)1 : (byte)0, new byte[] { 2 }));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Complete(rig, rig.Node.DownloadConciseDcfAsync(Peer, data)));
        await QuiesceAsync(rig.Witness, null);
        Requests(rig).Should().Be(0);
        rig.Node.PeerSdoChannelIsFreeForTests(Peer).Should().BeTrue();
    }

    [Fact]
    public async Task An_Unbound_Base_Object_Does_Not_Bypass_Configuration_Preflight()
    {
        using var rig = OpenMaster();
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Node.DownloadConciseDcfAsync(Peer, Dcf((0x1000, 0, new byte[4]))));
        Requests(rig).Should().Be(0);
    }

    [Theory]
    [InlineData(AccessType.WriteOnly)]
    [InlineData(AccessType.ReadWrite)]
    [InlineData(AccessType.ReadWriteInput)]
    [InlineData(AccessType.ReadWriteOutput)]
    public async Task Writable_Declared_Access_Is_Accepted(AccessType access)
    {
        using var rig = OpenMaster();
        using var peer = rig.OpenPeerNode(Peer);
        peer.ObjectDictionary.AddDomain(0x2000, 0, Array.Empty<byte>(), OdAccess.WriteOnly);
        var description = Description(0x2000);
        description.Objects.Objects[0x2000].AccessType = access;
        rig.Node.BindPeerDeviceDescription(Peer, description);
        await Complete(rig, rig.Node.DownloadConciseDcfAsync(Peer, Dcf((0x2000, 0, new byte[] { 42 }))));
        peer.ObjectDictionary.TryGet(0x2000, 0, out var entry).Should().BeTrue();
        entry!.GetRawValue().Should().Equal(42);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public async Task The_Size_Limit_Applies_To_Every_Value_Before_Any_Write(int size)
    {
        using var rig = OpenMaster(new CanOpenNodeOptions { MaxSdoTransferBytes = 4 });
        using var peer = rig.OpenPeerNode(Peer);
        peer.ObjectDictionary.AddDomain(0x2000, 0, Array.Empty<byte>());
        peer.ObjectDictionary.AddDomain(0x2001, 0, Array.Empty<byte>());
        Bind(rig, 0x2000, 0x2001);
        var download = rig.Node.DownloadConciseDcfAsync(Peer, Dcf((0x2000, 0, new byte[4]), (0x2001, 0, new byte[size])));
        if (size == 5)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => Complete(rig, download));
            Requests(rig).Should().Be(0);
        }
        else
        {
            await Complete(rig, download);
            peer.ObjectDictionary.ReadRaw(0x2000, 0).Length.Should().Be(4);
            peer.ObjectDictionary.ReadRaw(0x2001, 0).Length.Should().Be(4);
        }
    }

    [Fact]
    public async Task A_Peer_Abort_Stops_At_The_Failing_Record_And_Leaves_The_Written_Prefix()
    {
        using var rig = OpenMaster();
        using var peer = rig.OpenPeerNode(Peer);
        peer.ObjectDictionary.AddDomain(0x2000, 0, Array.Empty<byte>());
        peer.ObjectDictionary.AddDomain(0x2002, 0, Array.Empty<byte>());
        Bind(rig, 0x2000, 0x2001, 0x2002);
        var download = rig.Node.DownloadConciseDcfAsync(Peer, Dcf((0x2000, 0, new byte[] { 1 }), (0x2001, 0, new byte[] { 2 }), (0x2002, 0, new byte[] { 3 })));
        await UntilAsync(rig.Clock, rig.Witness, null, () => download.IsCompleted, 200, "the second record aborts");
        var error = await Assert.ThrowsAsync<SdoAbortException>(() => download);
        error.Index.Should().Be(0x2001);
        error.Origin.Should().Be(SdoAbortOrigin.Peer);
        peer.ObjectDictionary.ReadRaw(0x2000, 0).Should().Equal(1);
        peer.ObjectDictionary.ReadRaw(0x2002, 0).Should().BeEmpty();
        rig.Node.PeerSdoChannelIsFreeForTests(Peer).Should().BeTrue();
        await Complete(rig, rig.Node.SdoDownloadAsync(Peer, 0x2002, 0, new byte[] { 4 }));
    }

    [Fact]
    public async Task A_Local_Timeout_Stops_The_Sequence_And_Releases_The_Channel()
    {
        using var rig = OpenMaster(new CanOpenNodeOptions { SdoTimeout = TimeSpan.FromMilliseconds(50) });
        Bind(rig, 0x2000, 0x2001);
        var download = rig.Node.DownloadConciseDcfAsync(Peer, Dcf((0x2000, 0, new byte[] { 1 }), (0x2001, 0, new byte[] { 2 })));
        await UntilAsync(rig.Clock, rig.Witness, null, () => download.IsCompleted, 200, "the silent peer times out");
        var error = await Assert.ThrowsAsync<SdoAbortException>(() => download);
        error.Index.Should().Be(0x2000);
        error.Origin.Should().Be(SdoAbortOrigin.Local);
        rig.Log.Snapshot().Where(f => f.Id == 0x600 + Peer && f.Data[0] != 0x80).Should().HaveCount(1);
        rig.Node.PeerSdoChannelIsFreeForTests(Peer).Should().BeTrue();
    }
}
