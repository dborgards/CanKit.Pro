using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Sdo;
using EdsDcfNet.Models;
using Xunit;
using static CanKit.Pro.Tests.TestCases.CANopen.FlyingMasterRig;

namespace CanKit.Pro.Tests.TestCases.CANopen;

// FR-CO-076: store the complete DOMAIN on the manager before a separate 1F25h request.
public partial class CanOpenConciseDcfTests
{
    private static void BindStorageManager(MasterRig rig, byte target)
    {
        var description = PeerSdoLaboratory.Variables(0x1F22, 0x1F25);
        foreach (var index in description.Objects.Objects.Keys)
        {
            var entry = description.Objects.Objects[index];
            entry.ObjectType = 8; // ARRAY
            entry.SubObjects[target] = new CanOpenSubObject
            {
                SubIndex = target,
                DataType = (ushort)(index == 0x1F22 ? OdDataType.Domain : OdDataType.Unsigned32),
                AccessType = index == 0x1F22 ? AccessType.ReadWrite : AccessType.WriteOnly,
            };
        }
        rig.Node.BindPeerDeviceDescription(Peer, description);
    }

    [Theory]
    [InlineData(1, 0, 0x23)]
    [InlineData(Peer, 2, 0x21)]
    [InlineData(127, 32, 0xC0)]
    public async Task StoreConciseDcf_Preserves_The_Domain_Until_An_Explicit_Request(byte target, int valueBytes, byte initiate)
    {
        var value = Enumerable.Range(0, valueBytes).Select(i => (byte)i).ToArray();
        var original = valueBytes == 0 ? Dcf() : Dcf((0x2000, 1, value), (0x2000, 1, Array.Empty<byte>()));
        using var rig = OpenMaster(new CanOpenNodeOptions { SdoBlockThresholdBytes = 32, MaxSdoTransferBytes = original.Length });
        using var manager = rig.OpenPeerNode(Peer);
        manager.ObjectDictionary.AddDomain(0x1F22, target, Array.Empty<byte>(), pdoMappable: false);
        manager.ObjectDictionary.AddU32(0x1F25, target, 0, OdAccess.WriteOnly, pdoMappable: false);
        BindStorageManager(rig, target);

        var input = original.ToArray();
        var store = rig.Node.StoreConciseDcfAsync(Peer, target, input);
        input[0] ^= 0xFF;
        await Complete(rig, store);
        var request = rig.Log.Snapshot().First(f => f.Id == 0x600 + Peer).Data;
        if (initiate == 0xC0) (request[0] & 0xE0).Should().Be(initiate);
        else request[0].Should().Be(initiate);
        request.Skip(1).Take(3).Should().Equal(0x22, 0x1F, target);
        manager.ObjectDictionary.ReadUnsigned(0x1F25, target).Should().Be(0u);

        var upload = rig.Node.SdoUploadAsync(Peer, 0x1F22, target);
        await Complete(rig, upload);
        (await upload).Should().Equal(original);
        await Complete(rig, rig.Node.RequestConfigurationAsync(Peer, target));
        manager.ObjectDictionary.ReadUnsigned(0x1F25, target).Should().Be(0x666E6F63u);
    }

    [Theory]
    [MemberData(nameof(MalformedStreams))]
    public async Task StoreConciseDcf_Rejects_Malformed_Streams_Before_Sending(byte[] stream)
    {
        using var rig = OpenMaster();
        BindStorageManager(rig, 12);
        await Assert.ThrowsAsync<InvalidDataException>(() => rig.Node.StoreConciseDcfAsync(Peer, 12, stream));
        await QuiesceAsync(rig.Witness, null);
        Requests(rig).Should().Be(0);
    }

    [Fact]
    public async Task StoreConciseDcf_Validates_The_Total_Size_Addresses_And_Manager_Declaration()
    {
        using var rig = OpenMaster(new CanOpenNodeOptions { MaxSdoTransferBytes = 10 });
        var empty = Dcf();
        await Assert.ThrowsAsync<ArgumentNullException>(() => CanOpenNodeExtensions.StoreConciseDcfAsync(null!, Peer, 12, empty));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => rig.Node.StoreConciseDcfAsync(0, 12, empty));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => rig.Node.StoreConciseDcfAsync(Peer, 0, empty));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => rig.Node.StoreConciseDcfAsync(Peer, 128, empty));
        BindStorageManager(rig, 12);
        await Assert.ThrowsAsync<InvalidDataException>(() => rig.Node.StoreConciseDcfAsync(Peer, 12, Dcf((0x2000, 1, Array.Empty<byte>()))));
        await Assert.ThrowsAsync<PeerSdoAccessException>(() => rig.Node.StoreConciseDcfAsync(Peer, 11, empty));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            rig.Node.StoreConciseDcfAsync(Peer, 12, empty, new CancellationToken(canceled: true)));
        await QuiesceAsync(rig.Witness, null);
        Requests(rig).Should().Be(0);
    }

    [Fact]
    public async Task StoreConciseDcf_Propagates_The_Managers_Abort()
    {
        using var rig = OpenMaster();
        using var manager = rig.OpenPeerNode(Peer);
        manager.ObjectDictionary.AddDomain(0x1F22, 12, Array.Empty<byte>(), OdAccess.ReadOnly);
        BindStorageManager(rig, 12);
        var failure = await Assert.ThrowsAsync<SdoAbortException>(() => Complete(rig,
            rig.Node.StoreConciseDcfAsync(Peer, 12, Dcf())));
        failure.Origin.Should().Be(SdoAbortOrigin.Peer);
        failure.Index.Should().Be(0x1F22);
        failure.Subindex.Should().Be(12);
        failure.AbortCode.Should().Be((uint)SdoAbortCode.AttemptWriteReadOnly);
    }
}
