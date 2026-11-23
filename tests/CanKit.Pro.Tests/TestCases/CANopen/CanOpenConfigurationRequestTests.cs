using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.Tests.Infrastructure;
using EdsDcfNet.Models;
using Xunit;
using static CanKit.Pro.Tests.TestCases.CANopen.FlyingMasterRig;

namespace CanKit.Pro.Tests.TestCases.CANopen;

// FR-CO-075: the configuration manager receives the request on its own SDO channel.
public class CanOpenConfigurationRequestTests : IClassFixture<VirtualAdapterFixture>
{
    private const byte Manager = 5;
    private const ushort Index = 0x1F25;

    private static void BindManager(MasterRig rig, byte subindex)
    {
        var description = PeerSdoLaboratory.Variables(Index);
        var entry = description.Objects.Objects[Index];
        entry.ObjectType = 8; // ARRAY
        entry.SubObjects[subindex] = new CanOpenSubObject
        {
            SubIndex = subindex,
            DataType = (ushort)OdDataType.Unsigned32,
            AccessType = AccessType.WriteOnly,
        };
        rig.Node.BindPeerDeviceDescription(Manager, description);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(128)]
    public async Task Manager_Acknowledges_The_Request_For_The_Selected_Target(byte subindex)
    {
        using var rig = OpenMaster();
        BindManager(rig, subindex);
        var pending = subindex == 128
            ? rig.Node.RequestAllConfigurationsAsync(Manager)
            : rig.Node.RequestConfigurationAsync(Manager, subindex);

        await QuiesceAsync(rig.Witness, null);
        var requests = rig.Log.Snapshot().Where(f => f.Id >= 0x601 && f.Id <= 0x67F).ToArray();
        requests.Should().ContainSingle();
        requests[0].Id.Should().Be(0x600 + Manager);
        requests[0].Data.Should().Equal(0x23, 0x25, 0x1F, subindex, 0x63, 0x6F, 0x6E, 0x66);
        pending.IsCompleted.Should().BeFalse();
        rig.Peer.Transmit(CanFrame.Classic(0x580 + Manager, new byte[] { 0x60, 0x25, 0x1F, subindex, 0, 0, 0, 0 }));
        await pending.WithTimeoutAsync(ShortTimeout);
    }

    [Theory]
    [InlineData(0x08000024u)]
    [InlineData(0x08000000u)]
    public async Task Missing_Stored_Configuration_Preserves_The_Managers_Abort(uint code)
    {
        using var rig = OpenMaster();
        BindManager(rig, 12);
        var pending = rig.Node.RequestConfigurationAsync(Manager, 12);
        await QuiesceAsync(rig.Witness, null);
        rig.Peer.Transmit(CanFrame.Classic(0x580 + Manager, SdoFrames.BuildAbort(Index, 12, code)));
        var failure = await Assert.ThrowsAsync<SdoAbortException>(() => pending.WithTimeoutAsync(ShortTimeout));
        failure.AbortCode.Should().Be(code);
        failure.Index.Should().Be(Index);
        failure.Subindex.Should().Be(12);
        failure.Origin.Should().Be(SdoAbortOrigin.Peer);
    }

    [Fact]
    public async Task Invalid_Addresses_Undeclared_Requests_And_Prior_Cancellation_Send_No_Download()
    {
        using var rig = OpenMaster();
        await Assert.ThrowsAsync<ArgumentNullException>(() => CanOpenNodeExtensions.RequestAllConfigurationsAsync(null!, Manager));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => rig.Node.RequestConfigurationAsync(Manager, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => rig.Node.RequestConfigurationAsync(Manager, 128));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => rig.Node.RequestAllConfigurationsAsync(0));
        await Assert.ThrowsAsync<PeerSdoAccessException>(() => rig.Node.RequestConfigurationAsync(Manager, 12));
        BindManager(rig, 128);
        await Assert.ThrowsAsync<PeerSdoAccessException>(() => rig.Node.RequestConfigurationAsync(Manager, 12));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            rig.Node.RequestAllConfigurationsAsync(Manager, new CancellationToken(canceled: true)));
        await QuiesceAsync(rig.Witness, null);
        rig.Log.Snapshot().Should().NotContain(f => f.Id == 0x600 + Manager);
    }
}
