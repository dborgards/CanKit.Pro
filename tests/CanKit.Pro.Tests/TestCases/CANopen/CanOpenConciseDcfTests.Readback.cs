using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Sdo;
using Xunit;
using static CanKit.Pro.Tests.TestCases.CANopen.FlyingMasterRig;

namespace CanKit.Pro.Tests.TestCases.CANopen;

// FR-CO-077: validated readback from a configuration manager's readable 1F22h slot.
public partial class CanOpenConciseDcfTests
{
    [Theory]
    [InlineData(1, SdoTransferMode.Auto, 0)]
    [InlineData(Peer, SdoTransferMode.Auto, 12)]
    [InlineData(127, SdoTransferMode.Block, 256)]
    public async Task ReadStoredConciseDcf_Returns_The_Selected_Stream(byte target, SdoTransferMode mode, int valueSize)
    {
        var value = Enumerable.Range(0, valueSize).Select(i => (byte)i).ToArray();
        var stored = valueSize == 0 ? Dcf() : Dcf((0x2001, 2, value), (0x2001, 2, Array.Empty<byte>()));
        using var rig = OpenMaster(new CanOpenNodeOptions { MaxSdoTransferBytes = stored.Length });
        using var manager = rig.OpenPeerNode(Peer);
        manager.ObjectDictionary.AddDomain(0x1F22, target, stored, OdAccess.ReadOnly);
        BindStorageManager(rig, target);
        var read = rig.Node.ReadStoredConciseDcfAsync(Peer, target, mode);
        await Complete(rig, read);
        (await read).Should().Equal(stored);
        var first = rig.Log.Snapshot().First(f => f.Id == 0x600 + Peer).Data;
        (first[0] & 0xE0).Should().Be(mode == SdoTransferMode.Block ? 0xA0 : 0x40);
        first.Skip(1).Take(3).Should().Equal(0x22, 0x1F, target);
    }

    [Theory]
    [MemberData(nameof(MalformedStreams))]
    public async Task ReadStoredConciseDcf_Rejects_Malformed_Manager_Data(byte[] malformed)
    {
        using var rig = OpenMaster();
        using var manager = rig.OpenPeerNode(Peer);
        manager.ObjectDictionary.AddDomain(0x1F22, 12, malformed);
        BindStorageManager(rig, 12);
        await Assert.ThrowsAsync<InvalidDataException>(() => Complete(rig, rig.Node.ReadStoredConciseDcfAsync(Peer, 12)));
    }

    [Fact]
    public async Task ReadStoredConciseDcf_Applies_The_Total_Size_Limit_To_Expedited_Responses()
    {
        using var rig = OpenMaster(new CanOpenNodeOptions { MaxSdoTransferBytes = 3 });
        using var manager = rig.OpenPeerNode(Peer);
        manager.ObjectDictionary.AddDomain(0x1F22, 12, Dcf());
        BindStorageManager(rig, 12);
        await Assert.ThrowsAsync<InvalidDataException>(() => Complete(rig, rig.Node.ReadStoredConciseDcfAsync(Peer, 12)));
    }

    [Fact]
    public async Task ReadStoredConciseDcf_Preserves_The_Managers_Write_Only_Abort()
    {
        using var rig = OpenMaster();
        using var manager = rig.OpenPeerNode(Peer);
        manager.ObjectDictionary.AddDomain(0x1F22, 12, Dcf(), OdAccess.WriteOnly);
        BindStorageManager(rig, 12);
        var error = await Assert.ThrowsAsync<SdoAbortException>(() => Complete(rig, rig.Node.ReadStoredConciseDcfAsync(Peer, 12)));
        error.Origin.Should().Be(SdoAbortOrigin.Peer);
        error.Index.Should().Be(0x1F22);
        error.Subindex.Should().Be(12);
        error.AbortCode.Should().Be((uint)SdoAbortCode.AttemptReadWriteOnly);
    }

    [Fact]
    public async Task ReadStoredConciseDcf_Rejects_Invalid_Addresses_And_Forwards_Cancellation()
    {
        using var rig = OpenMaster();
        await Assert.ThrowsAsync<ArgumentNullException>(() => CanOpenNodeExtensions.ReadStoredConciseDcfAsync(null!, Peer, 12));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => rig.Node.ReadStoredConciseDcfAsync(0, 12));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => rig.Node.ReadStoredConciseDcfAsync(Peer, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => rig.Node.ReadStoredConciseDcfAsync(Peer, 128));
        BindStorageManager(rig, 12);
        await Assert.ThrowsAsync<PeerSdoAccessException>(() => rig.Node.ReadStoredConciseDcfAsync(Peer, 11));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            rig.Node.ReadStoredConciseDcfAsync(Peer, 12, cancellationToken: new CancellationToken(canceled: true)));
        await QuiesceAsync(rig.Witness, null);
        Requests(rig).Should().Be(0);

        using var cancel = new CancellationTokenSource();
        var read = rig.Node.ReadStoredConciseDcfAsync(Peer, 12, cancellationToken: cancel.Token);
        await QuiesceAsync(rig.Witness, null);
        Requests(rig).Should().Be(1);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WithTimeoutAsync(ShortTimeout));
    }
}
