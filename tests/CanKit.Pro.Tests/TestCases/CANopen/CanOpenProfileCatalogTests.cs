using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Application;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

/// <summary>
/// The profile catalogue maps the logical devices of a peer to typed peer access, keyed by the
/// profile number alone, and does so without touching the bus.
/// </summary>
public class CanOpenProfileCatalogTests : IClassFixture<VirtualAdapterFixture>
{
    private const byte PeerId = 0x21;

    [Fact]
    public void A_Known_Profile_Yields_Its_Peer()
    {
        var catalog = new CanOpenProfileCatalog(new FakeProfile(401));
        var device = new CanOpenLogicalDevice(0, new CanOpenDeviceType(0x000F0191u));

        using var rig = new NodeRig();
        var peer = catalog.CreatePeer(rig.Node, PeerId, device);

        var fake = peer.Should().BeOfType<FakePeer>().Subject;
        fake.Node.Should().BeSameAs(rig.Node);
        fake.PeerNodeId.Should().Be(PeerId);
        fake.LogicalDevice.Should().BeSameAs(device);
        fake.Profile.ProfileNumber.Should().Be(401);
    }

    [Fact]
    public void An_Unknown_Profile_Yields_Null()
    {
        var catalog = new CanOpenProfileCatalog(new FakeProfile(401));
        var device = new CanOpenLogicalDevice(0, new CanOpenDeviceType(0x00000192u)); // profile 402

        using var rig = new NodeRig();

        catalog.CreatePeer(rig.Node, PeerId, device).Should().BeNull();
    }

    [Fact]
    public void A_Device_Without_A_Profile_Yields_Null()
    {
        var catalog = new CanOpenProfileCatalog(new FakeProfile(401));
        var device = new CanOpenLogicalDevice(0, new CanOpenDeviceType(0x00000000u));

        using var rig = new NodeRig();

        catalog.CreatePeer(rig.Node, PeerId, device).Should().BeNull();
    }

    [Fact]
    public async Task A_Read_Result_Maps_Device_By_Device()
    {
        var catalog = new CanOpenProfileCatalog(new FakeProfile(401));
        var upload = new ScriptedUpload(
            (0x1000, 0xFFFF0191u),
            (0x67FF, 0x000F0191u), // profile 401
            (0x6FFF, 0x00010192u), // profile 402
            (0x77FF, 0x00020191u)); // profile 401
        var result = await CanOpenLogicalDevices.ReadCoreAsync(upload.Upload, default);
        result.Devices.Select(d => d.DeviceType.ProfileNumber).Should().Equal((ushort)401, (ushort)402, (ushort)401);

        using var rig = new NodeRig();
        var peers = catalog.CreatePeers(rig.Node, PeerId, result);

        peers.Should().HaveCount(3);
        peers[0].Should().BeOfType<FakePeer>().Which.LogicalDevice.Should().BeSameAs(result.Devices[0]);
        peers[1].Should().BeNull();
        peers[2].Should().BeOfType<FakePeer>().Which.LogicalDevice.Should().BeSameAs(result.Devices[2]);
        peers[0]!.LogicalDevice.Index.Should().Be(0);
        peers[2]!.LogicalDevice.Index.Should().Be(2);
        peers[0]!.PeerNodeId.Should().Be(PeerId);
        peers[2]!.PeerNodeId.Should().Be(PeerId);
    }

    [Fact]
    public void Two_Descriptors_For_One_Profile_Are_Refused()
    {
        var first = new FakeProfile(401, "CiA 401 A");
        var second = new FakeProfile(401, "CiA 401 B");

        FluentActions.Invoking(() => new CanOpenProfileCatalog(first, second))
            .Should().Throw<ArgumentException>()
            .Where(e => e.Message.Contains("401") && e.Message.Contains("CiA 401 A") && e.Message.Contains("CiA 401 B"));
        FluentActions.Invoking(() => new CanOpenProfileCatalog(new List<ICanOpenProfile> { first, second }))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_Descriptor_For_Profile_0_Is_Refused()
    {
        FluentActions.Invoking(() => new CanOpenProfileCatalog(new FakeProfile(0, "Nothing")))
            .Should().Throw<ArgumentException>()
            .Where(e => e.Message.Contains("Nothing"));
    }

    [Fact]
    public void A_Null_List_Or_Element_Is_Refused()
    {
        FluentActions.Invoking(() => new CanOpenProfileCatalog((IEnumerable<ICanOpenProfile>)null!))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new CanOpenProfileCatalog(new FakeProfile(401), null!))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new CanOpenProfileCatalog((ICanOpenProfile[])null!))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_Catalogue_Holds_What_It_Was_Given_And_Finds_It_By_Number()
    {
        var p401 = new FakeProfile(401);
        var p408 = new FakeProfile(408);
        var catalog = new CanOpenProfileCatalog(p401, p408);

        catalog.Profiles.Should().BeEquivalentTo(new ICanOpenProfile[] { p401, p408 });
        catalog.TryGetProfile(408, out var found).Should().BeTrue();
        found.Should().BeSameAs(p408);
        catalog.TryGetProfile(402, out var missing).Should().BeFalse();
        missing.Should().BeNull();
        new CanOpenProfileCatalog().Profiles.Should().BeEmpty();
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)128)]
    public void An_Invalid_Peer_Node_Id_Is_Refused(byte peerNodeId)
    {
        var catalog = new CanOpenProfileCatalog(new FakeProfile(401));
        var device = new CanOpenLogicalDevice(0, new CanOpenDeviceType(0x000F0191u));
        var result = ReadOne(0x000F0191u);

        using var rig = new NodeRig();

        FluentActions.Invoking(() => catalog.CreatePeer(rig.Node, peerNodeId, device))
            .Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => catalog.CreatePeers(rig.Node, peerNodeId, result))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void A_Null_Node_Device_Or_Result_Is_Refused()
    {
        var catalog = new CanOpenProfileCatalog(new FakeProfile(401));
        var device = new CanOpenLogicalDevice(0, new CanOpenDeviceType(0x000F0191u));
        var result = ReadOne(0x000F0191u);

        using var rig = new NodeRig();

        FluentActions.Invoking(() => catalog.CreatePeer(null!, PeerId, device))
            .Should().Throw<ArgumentNullException>().WithParameterName("node");
        FluentActions.Invoking(() => catalog.CreatePeers(null!, PeerId, result))
            .Should().Throw<ArgumentNullException>().WithParameterName("node");
        FluentActions.Invoking(() => catalog.CreatePeer(rig.Node, PeerId, null!))
            .Should().Throw<ArgumentNullException>().WithParameterName("logicalDevice");
        FluentActions.Invoking(() => catalog.CreatePeers(rig.Node, PeerId, null!))
            .Should().Throw<ArgumentNullException>().WithParameterName("result");
    }

    private static CanOpenLogicalDeviceReadResult ReadOne(uint deviceType)
        => CanOpenLogicalDevices.ReadCoreAsync(new ScriptedUpload((0x1000, deviceType)).Upload, default).GetAwaiter().GetResult();

    /// <summary>The cheapest real node: it opens on a virtual bus and the catalogue never uses it.</summary>
    private sealed class NodeRig : IDisposable
    {
        private readonly ICanBus _bus;

        public NodeRig()
        {
            _bus = VirtualAdapterFixture.Open(VirtualAdapterFixture.NewSession("canopen-profile-catalog"), 0);
            Node = CanOpen.OpenNode(_bus, 0x7F);
        }

        public ICanOpenNode Node { get; }

        public void Dispose()
        {
            Node.Dispose();
            _bus.Dispose();
        }
    }

    private sealed class FakeProfile(ushort number, string? name = null) : ICanOpenProfile
    {
        public ushort ProfileNumber { get; } = number;

        public string Name { get; } = name ?? "CiA " + number;

        public ICanOpenProfilePeer CreatePeer(ICanOpenNode node, byte peerNodeId, CanOpenLogicalDevice logicalDevice)
            => new FakePeer(this, node, peerNodeId, logicalDevice);
    }

    private sealed class FakePeer(ICanOpenProfile profile, ICanOpenNode node, byte peerNodeId,
        CanOpenLogicalDevice logicalDevice) : ICanOpenProfilePeer
    {
        public ICanOpenProfile Profile { get; } = profile;

        public ICanOpenNode Node { get; } = node;

        public byte PeerNodeId { get; } = peerNodeId;

        public CanOpenLogicalDevice LogicalDevice { get; } = logicalDevice;
    }

    /// <summary>A scripted peer: an index that is not scripted is the peer's "object does not exist".</summary>
    private sealed class ScriptedUpload
    {
        private readonly Dictionary<ushort, uint> _answers;

        public ScriptedUpload(params (ushort Index, uint Answer)[] answers)
            => _answers = answers.ToDictionary(a => a.Index, a => a.Answer);

        public Task<byte[]> Upload(ushort index, CancellationToken cancellationToken)
        {
            if (!_answers.TryGetValue(index, out var answer))
            {
                throw new SdoAbortException(index, 0, (uint)SdoAbortCode.ObjectDoesNotExist,
                    SdoAbortOrigin.Peer, "no such object");
            }

            return Task.FromResult(BitConverter.GetBytes(answer));
        }
    }
}
