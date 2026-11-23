using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Sdo;
using Xunit;
using static CanKit.Pro.Tests.TestCases.CANopen.FlyingMasterRig;

namespace CanKit.Pro.Tests.TestCases.CANopen;

/// <summary>FR-CO-065: required device-type uploads in the announcement-based boot path.</summary>
public partial class CanOpenBootIdentityTests
{
    private static CanOpenNodeOptions ReachabilityOptions() => new() { RequireBootDeviceTypeUpload = true };

    [Theory]
    [InlineData(0u, 0u, false)]
    [InlineData(0u, 0x12345678u, false)]
    [InlineData(0u, 0x12345678u, true)]
    [InlineData(0x12345678u, 0x12345678u, false)]
    public async Task Required_Device_Type_Is_Read_Once_Before_Start(uint expected, uint actual, bool simultaneous)
    {
        using var rig = OpenMaster(ReachabilityOptions());
        using var peer = rig.OpenPeerNode(Slave);
        Configure(rig, simultaneous: simultaneous);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F84, Slave, expected);
        SetRemote(peer, 0x1000, 0, actual);
        // Even an unseen optional slave must not be reached by a broadcast Start.
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F81, 6, Assigned | BootSlave | 0x10u);
        await Elect(rig);
        rig.Node.State.Should().Be(NmtState.PreOperational);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Started(rig, Slave), 300, "the device-type upload completed");
        var frames = rig.Log.Snapshot();
        int start = frames.FindIndex(f => IsNmt(f, NmtCommand.Start, Slave));
        frames.Take(start).Should().ContainSingle(f => f.Id == 0x580 + Slave && f.Data.Length == 8
            && f.Data[1] == 0 && f.Data[2] == 0x10 && f.Data[3] == 0);
        frames.Should().NotContain(f => f.Id == 0x600 + Slave && f.Data.Length == 8 && f.Data[2] == 0x10 && f.Data[1] == 0x18);
        Started(rig, 0).Should().BeFalse();
        Started(rig, 6).Should().BeFalse();
        Result(rig).Status.Should().Be(SlaveBootStatus.StartRequested);
        rig.Node.State.Should().Be(NmtState.Operational);
    }

    [Fact]
    public async Task Required_Device_Type_Precedes_Other_Identity_Reads()
    {
        using var rig = OpenMaster(ReachabilityOptions());
        using var peer = rig.OpenPeerNode(Slave);
        Configure(rig);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F85, Slave, 123);
        SetRemote(peer, 0x1018, 1, 123);
        await Elect(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Started(rig, Slave), 300, "reachability then identity completed");
        var requests = rig.Log.Snapshot().Where(f => f.Id == 0x600 + Slave && f.Data.Length == 8 && f.Data[0] == 0x40)
            .Select(f => (f.Data[1] | f.Data[2] << 8, f.Data[3])).ToArray();
        requests.Should().Equal((0x1000, (byte)0), (0x1018, (byte)1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Required_Device_Type_Uses_The_Bound_Description(bool declared)
    {
        using var rig = OpenMaster(ReachabilityOptions());
        using var peer = rig.OpenPeerNode(Slave);
        Configure(rig);
        rig.Node.BindPeerDeviceDescription(Slave, PeerSdoLaboratory.Variables(declared ? (ushort)0x1000 : (ushort)0x2000));
        await Elect(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        var status = declared ? SlaveBootStatus.StartRequested : SlaveBootStatus.IdentityReadFailed;
        await UntilAsync(rig.Clock, rig.Witness, null, () => Result(rig).Status == status, 300, "the device-type upload respects the description");
        if (!declared)
        {
            Result(rig).Error.Should().BeOfType<PeerSdoAccessException>();
            Result(rig).Index.Should().Be(0x1000);
            Result(rig).ExpectedValue.Should().BeNull();
            rig.Log.Snapshot().Should().NotContain(f => f.Id == 0x600 + Slave);
            Started(rig, Slave).Should().BeFalse();
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task Missing_Device_Type_Response_Prevents_Start(bool mandatory, bool simultaneous)
    {
        using var rig = OpenMaster(ReachabilityOptions());
        Configure(rig, mandatory, simultaneous);
        await Elect(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => Result(rig).Status == SlaveBootStatus.IdentityReadFailed, 2000, "the required upload times out");
        var result = Result(rig);
        result.Index.Should().Be(0x1000);
        result.Subindex.Should().Be(0);
        result.ExpectedValue.Should().BeNull();
        result.ActualValue.Should().BeNull();
        result.Error.Should().BeOfType<SdoAbortException>().Which.AbortCode.Should().Be((uint)SdoAbortCode.SdoProtocolTimedOut);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await QuiesceAsync(rig.Witness, null);
        Started(rig, Slave).Should().BeFalse();
        Started(rig, 0).Should().BeFalse();
    }

    [Fact]
    public async Task Required_Device_Type_Must_Be_Unsigned32_Even_Without_A_Comparison()
    {
        using var rig = OpenMaster(ReachabilityOptions());
        using var peer = rig.OpenPeerNode(Slave);
        Configure(rig);
        peer.ObjectDictionary.Declare(0x1000, 0, OdDataType.Domain, OdAccess.ReadOnly, new byte[] { 1 }, pdoMappable: false);
        await Elect(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => Result(rig).Status == SlaveBootStatus.IdentityReadFailed, 300, "the mandatory read must return four bytes");
        Result(rig).Error.Should().BeOfType<InvalidDataException>();
        Result(rig).ExpectedValue.Should().BeNull();
        Started(rig, Slave).Should().BeFalse();
    }

    [Fact]
    public async Task Required_Device_Type_Still_Enforces_A_Nonzero_Expectation()
    {
        using var rig = OpenMaster(ReachabilityOptions());
        using var peer = rig.OpenPeerNode(Slave);
        Configure(rig);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F84, Slave, 123);
        SetRemote(peer, 0x1000, 0, 456);
        await Elect(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => Result(rig).Status == SlaveBootStatus.IdentityMismatch, 300, "a nonzero device-type expectation still compares");
        Result(rig).ExpectedValue.Should().Be(123);
        Result(rig).ActualValue.Should().Be(456);
        Started(rig, Slave).Should().BeFalse();
    }

    [Fact]
    public async Task Required_Device_Type_Checks_An_Already_Operational_Slave()
    {
        using var rig = OpenMaster(ReachabilityOptions());
        using var peer = rig.OpenPeerNode(Slave);
        Configure(rig);
        await Elect(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x05);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => Result(rig).Status == SlaveBootStatus.AlreadyOperational, 300, "reachability of the running slave is verified");
        await QuiesceAsync(rig.Witness, null);
        rig.Log.Snapshot().Should().Contain(f => f.Id == 0x580 + Slave && f.Data.Length == 8 && f.Data[1] == 0 && f.Data[2] == 0x10);
        Started(rig, Slave).Should().BeFalse();
    }

    [Fact]
    public async Task Boot_Deadline_Bounds_Required_Device_Type_While_The_Channel_Is_Occupied()
    {
        using var rig = OpenMaster(ReachabilityOptions());
        Configure(rig);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F89, 0, 100);
        await Elect(rig);
        using var cancel = new CancellationTokenSource();
        var occupying = rig.Node.SdoUploadAsync(Slave, 0x1001, 0, cancellationToken: cancel.Token);
        using var allowedWait = rig.Witness.AllowPeerVerificationWait(Slave);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => Result(rig).Status == SlaveBootStatus.BootTimeout, 300, "the boot deadline also bounds channel acquisition");
        rig.Node.State.Should().Be(NmtState.PreOperational);
        rig.Node.ObjectDictionary.ReadUnsigned(0x1F82, Slave).Should().Be(0x7Fu,
            "the slave announced its state before the identity check timed out");
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => occupying);
        await QuiesceAsync(rig.Witness, null);
        Result(rig).Status.Should().Be(SlaveBootStatus.BootTimeout);
        Started(rig, Slave).Should().BeFalse();
    }

    [Fact]
    public async Task Role_Loss_Cancels_A_Required_Device_Type_Wait()
    {
        using var rig = OpenMaster(ReachabilityOptions());
        Configure(rig);
        await Elect(rig);
        using var cancel = new CancellationTokenSource();
        var occupying = rig.Node.SdoUploadAsync(Slave, 0x1001, 0, cancellationToken: cancel.Token);
        using var allowedWait = rig.Witness.AllowPeerVerificationWait(Slave);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => Result(rig).Status == SlaveBootStatus.VerifyingIdentity, 200, "the device-type check waits on the peer channel");
        rig.Node.StopFlyingMaster();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => occupying);
        await QuiesceAsync(rig.Witness, null);
        rig.Node.GetSlaveBootResults().Should().BeEmpty();
        Started(rig, Slave).Should().BeFalse();
    }

    [Theory]
    [InlineData(Assigned | BootSlave | 0x10u, SuppressSlaveStart)]
    [InlineData(Assigned | 0x10u, 0u)]
    [InlineData(BootSlave | 0x10u, 0u)]
    public async Task Required_Device_Type_Does_Not_Enable_An_Excluded_Slave(uint assignment, uint startup)
    {
        using var rig = OpenMaster(ReachabilityOptions());
        Configure(rig);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F81, Slave, assignment);
        rig.Node.ObjectDictionary.WriteUnsigned(Startup, 0, startup);
        await Elect(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await QuiesceAsync(rig.Witness, null);
        if ((assignment & Assigned) != 0)
            Result(rig).Status.Should().Be(SlaveBootStatus.Announced);
        else
            rig.Node.GetSlaveBootResults().Should().BeEmpty();
        rig.Log.Snapshot().Should().NotContain(f => f.Id == 0x600 + Slave);
        Started(rig, Slave).Should().BeFalse();
    }

    [Fact]
    public void Device_Type_Requirement_Is_Opt_In_And_Survives_Options_Copies()
    {
        new CanOpenNodeOptions().RequireBootDeviceTypeUpload.Should().BeFalse();
        var options = ReachabilityOptions();
        options.With().RequireBootDeviceTypeUpload.Should().BeTrue();
        options.WithLss(null).RequireBootDeviceTypeUpload.Should().BeTrue();
        options.WithSrdoConfigurationCheckInterval(TimeSpan.FromSeconds(1)).RequireBootDeviceTypeUpload.Should().BeTrue();
        options.WithErrorHistoryCapacity(4).RequireBootDeviceTypeUpload.Should().BeTrue();
        options.WithErrorBehavior(true).RequireBootDeviceTypeUpload.Should().BeTrue();
    }
}
