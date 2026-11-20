using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;
using static CanKit.Pro.Tests.TestCases.CANopen.FlyingMasterRig;

namespace CanKit.Pro.Tests.TestCases.CANopen;

/// <summary>FR-CO-064: identity expectations gate the boot; failures and cancellation are
/// observable on a virtual bus with a controlled protocol clock.</summary>
public class CanOpenBootIdentityTests : IClassFixture<VirtualAdapterFixture>
{
    private const byte Slave = 5;

    private static string ExpectationFile()
    {
        var text = new StringBuilder("""
            [FileInfo]
            FileName=expectations.eds
            FileVersion=1
            FileRevision=0
            EDSVersion=4.0
            Description=Boot identity test
            CreationTime=10:00AM
            CreationDate=11-18-2026
            CreatedBy=CanKit.Pro tests
            [DeviceInfo]
            VendorName=CanKit.Pro
            VendorNumber=0
            ProductName=Identity
            ProductNumber=0
            RevisionNumber=0
            BaudRate_500=1
            SimpleBootUpSlave=1
            Granularity=8
            NrOfRXPDO=0
            NrOfTXPDO=0
            [MandatoryObjects]
            SupportedObjects=0
            [OptionalObjects]
            SupportedObjects=5
            1=0x1F84
            2=0x1F85
            3=0x1F86
            4=0x1F87
            5=0x1F88

            """);
        for (ushort index = 0x1F84; index <= 0x1F88; index++)
            text.Append($"\n[{index:X4}]\nParameterName=Expected identity\nObjectType=0x8\nSubNumber=2\n"
                + $"[{index:X4}sub0]\nParameterName=Count\nObjectType=0x7\nDataType=0x0005\nAccessType=ro\nDefaultValue=127\nPDOMapping=0\n"
                + $"[{index:X4}sub5]\nParameterName=Expected slave 5\nObjectType=0x7\nDataType=0x0007\nAccessType=rw\nDefaultValue=123\nPDOMapping=0\n");
        return text.ToString();
    }

    private static void Configure(MasterRig rig, bool mandatory = true, bool simultaneous = false)
    {
        Tighten(rig.Node);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F81, Slave,
            Assigned | BootSlave | 0x10u | (mandatory ? MandatorySlave : 0));
        rig.Node.ObjectDictionary.WriteUnsigned(Startup, 0, simultaneous ? 2u : 0u);
    }

    private static void SetRemote(CanOpenNode peer, ushort index, byte subindex, uint value)
        => peer.ObjectDictionary.AddU32(index, subindex, value, OdAccess.ReadOnly, pdoMappable: false);

    private static bool Started(MasterRig rig, byte target)
        => rig.Log.Snapshot().Any(f => IsNmt(f, NmtCommand.Start, target));

    private static SlaveBootResult Result(MasterRig rig)
        => ((ICanOpenNode)rig.Node).GetSlaveBootResults().Single(r => r.NodeId == Slave);

    private static async Task Elect(MasterRig rig)
    {
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Node.FlyingMasterRole == FlyingMasterRole.Active, 800, "the master is elected");
        rig.Log.Clear();
    }

    [Fact]
    public async Task All_Five_Expectations_Are_Read_Before_The_Slave_Is_Started()
    {
        using var rig = OpenMaster();
        using var peer = rig.OpenPeerNode(Slave);
        Configure(rig);
        for (int i = 0; i < 5; i++)
        {
            uint value = 0x81234560u + (uint)i;
            rig.Node.ObjectDictionary.WriteUnsigned((ushort)(0x1F84 + i), Slave, value);
            SetRemote(peer, i == 0 ? (ushort)0x1000 : (ushort)0x1018, (byte)i, value);
        }
        await Elect(rig);
        rig.Node.State.Should().Be(NmtState.PreOperational);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Started(rig, Slave), 200, "all identity reads matched");
        Result(rig).Status.Should().Be(SlaveBootStatus.StartRequested);
        rig.Node.State.Should().Be(NmtState.Operational);
        var frames = rig.Log.Snapshot();
        int start = frames.FindIndex(f => IsNmt(f, NmtCommand.Start, Slave));
        for (int i = 0; i < 5; i++)
        {
            ushort index = i == 0 ? (ushort)0x1000 : (ushort)0x1018;
            frames.Take(start).Should().Contain(f => f.Id == 0x580 + Slave && f.Data.Length == 8
                && f.Data[1] == (byte)index && f.Data[2] == (byte)(index >> 8) && f.Data[3] == (byte)i);
            rig.Node.ObjectDictionary.ReadUnsigned((ushort)(0x1F84 + i), Slave).Should().Be(0x81234560u + (uint)i,
                "the cold election reset keeps the configured expectations");
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task A_Mismatch_Reports_The_Remote_Pair_And_Prevents_Start(int field)
    {
        using var rig = OpenMaster();
        using var peer = rig.OpenPeerNode(Slave);
        Configure(rig);
        rig.Node.ObjectDictionary.WriteUnsigned((ushort)(0x1F84 + field), Slave, 456);
        ushort index = field == 0 ? (ushort)0x1000 : (ushort)0x1018;
        SetRemote(peer, index, (byte)field, 123);
        await Elect(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => Result(rig).Status == SlaveBootStatus.IdentityMismatch, 200, "the identity differs");
        await QuiesceAsync(rig.Witness, null);
        var result = Result(rig);
        result.Index.Should().Be(index);
        result.Subindex.Should().Be((byte)field);
        result.ExpectedValue.Should().Be(456);
        result.ActualValue.Should().Be(123);
        result.Error.Should().BeNull();
        Started(rig, Slave).Should().BeFalse();
        Started(rig, 0).Should().BeFalse();
        rig.Node.State.Should().Be(NmtState.PreOperational);
    }

    [Theory]
    [InlineData(0x00010002u, 0x00010002u, true)]
    [InlineData(0x00010002u, 0x00010003u, true)]
    [InlineData(0x00010002u, 0x00010001u, false)]
    [InlineData(0x00010002u, 0x00020002u, false)]
    [InlineData(0x00010002u, 0x0000FFFFu, false)]
    [InlineData(0x00010000u, 0x00010000u, true)]
    [InlineData(0x0001FFFFu, 0x0001FFFFu, true)]
    [InlineData(0x0001FFFFu, 0x00020000u, false)]
    [InlineData(0xFFFF8000u, 0xFFFFFFFFu, true)]
    [InlineData(0x00000001u, 0x00000000u, false)]
    [InlineData(0x00000000u, 0xFFFFFFFFu, true)]
    public async Task Revision_Compatibility_Uses_Equal_Major_And_Minimum_Minor(uint expected, uint actual, bool compatible)
    {
        using var rig = OpenMaster();
        using var peer = rig.OpenPeerNode(Slave);
        Configure(rig);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F87, Slave, expected);
        SetRemote(peer, 0x1018, 3, actual);
        await Elect(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        var status = compatible ? SlaveBootStatus.StartRequested : SlaveBootStatus.IdentityMismatch;
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => Result(rig).Status == status && (!compatible || Started(rig, Slave)),
            200, "the revision follows CiA 302-2 section 6.9");
        await QuiesceAsync(rig.Witness, null);
        Started(rig, Slave).Should().Be(compatible);
        Started(rig, 0).Should().BeFalse();
        rig.Node.State.Should().Be(compatible ? NmtState.Operational : NmtState.PreOperational);
        if (!compatible)
        {
            Result(rig).Index.Should().Be(0x1018);
            Result(rig).Subindex.Should().Be(3);
            Result(rig).ExpectedValue.Should().Be(expected);
            Result(rig).ActualValue.Should().Be(actual);
            Result(rig).Error.Should().BeNull();
        }
        if (expected == 0)
            rig.Log.Snapshot().Should().NotContain(f => f.Id == 0x600 + Slave);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_Failed_Optional_Slave_Never_Receives_A_Broadcast_Start(bool simultaneous)
    {
        using var rig = OpenMaster();
        using var peer = rig.OpenPeerNode(Slave);
        Configure(rig, mandatory: false, simultaneous: simultaneous);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F85, Slave, 123);
        SetRemote(peer, 0x1018, 1, 456);
        await Elect(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => Result(rig).Status == SlaveBootStatus.IdentityMismatch, 200, "the optional slave differs");
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await QuiesceAsync(rig.Witness, null);
        Started(rig, Slave).Should().BeFalse();
        Started(rig, 0).Should().BeFalse();
    }

    [Fact]
    public async Task Zero_Expectations_Do_Not_Start_An_Identity_Transfer()
    {
        using var rig = OpenMaster();
        Configure(rig);
        await Elect(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Started(rig, Slave), 200, "the existing boot path is kept");
        rig.Log.Snapshot().Should().NotContain(f => f.Id == 0x600 + Slave);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_Already_Operational_Slave_Is_Checked_Without_Sending_Start(bool matches)
    {
        using var rig = OpenMaster();
        using var peer = rig.OpenPeerNode(Slave);
        Configure(rig);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F85, Slave, 123);
        SetRemote(peer, 0x1018, 1, matches ? 123u : 456u);
        await Elect(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x05);
        var status = matches ? SlaveBootStatus.AlreadyOperational : SlaveBootStatus.IdentityMismatch;
        await UntilAsync(rig.Clock, rig.Witness, null, () => Result(rig).Status == status,
            200, "an Operational announcement does not bypass identity verification");
        await QuiesceAsync(rig.Witness, null);
        rig.Log.Snapshot().Should().Contain(f => f.Id == 0x600 + Slave && f.Data.Length == 8
            && f.Data[1] == 0x18 && f.Data[2] == 0x10 && f.Data[3] == 1);
        Started(rig, Slave).Should().BeFalse();
        Started(rig, 0).Should().BeFalse();
        rig.Node.State.Should().Be(matches ? NmtState.Operational : NmtState.PreOperational);
    }

    [Fact]
    public async Task An_Offline_Sdo_Server_Reports_The_Upload_Timeout()
    {
        using var rig = OpenMaster();
        Configure(rig);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F84, Slave, 1);
        await Elect(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => Result(rig).Status == SlaveBootStatus.IdentityReadFailed, 2000, "the SDO upload times out");
        Result(rig).Index.Should().Be(0x1000);
        Result(rig).Error.Should().BeOfType<SdoAbortException>().Which.AbortCode.Should().Be((uint)SdoAbortCode.SdoProtocolTimedOut);
        Started(rig, Slave).Should().BeFalse();
    }

    [Fact]
    public async Task A_Short_Identity_Is_A_Read_Failure()
    {
        using var rig = OpenMaster();
        using var peer = rig.OpenPeerNode(Slave);
        Configure(rig);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F84, Slave, 1);
        peer.ObjectDictionary.Declare(0x1000, 0, OdDataType.Domain, OdAccess.ReadOnly, new byte[] { 1 }, pdoMappable: false);
        await Elect(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => Result(rig).Status == SlaveBootStatus.IdentityReadFailed, 200, "UNSIGNED32 requires four bytes");
        Result(rig).Error.Should().BeOfType<InvalidDataException>();
        Started(rig, Slave).Should().BeFalse();
    }

    [Fact]
    public async Task A_Missing_Mandatory_Slave_Has_A_Boot_Timeout_Result()
    {
        using var rig = OpenMaster();
        Configure(rig);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F89, 0, 100);
        await Elect(rig);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => Result(rig).Status == SlaveBootStatus.BootTimeout, 300, "the slave never announced");
        Started(rig, Slave).Should().BeFalse();
    }

    [Fact]
    public async Task Stopping_While_Verification_Waits_For_The_Channel_Cancels_That_Boot()
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
            () => Result(rig).Status == SlaveBootStatus.VerifyingIdentity, 200, "verification waits on the existing transfer");
        var snapshot = rig.Node.GetSlaveBootResults();
        rig.Node.StopFlyingMaster();
        cancel.Cancel();
        try { await occupying; } catch (OperationCanceledException) { }
        await QuiesceAsync(rig.Witness, null);
        rig.Node.GetSlaveBootResults().Should().BeEmpty();
        snapshot.Single().Status.Should().Be(SlaveBootStatus.VerifyingIdentity);
        Started(rig, Slave).Should().BeFalse();
    }

    [Fact]
    public void Expectations_Are_Managed_Unsigned32_Arrays()
    {
        using var rig = OpenMaster();
        for (ushort index = 0x1F84; index <= 0x1F88; index++)
        {
            var od = rig.Node.ObjectDictionary;
            od.ReadUnsigned(index, 0).Should().Be(127);
            od.ReadUnsigned(index, Slave).Should().Be(0);
            Action count = () => od.WriteUnsigned(index, 0, 1);
            count.Should().Throw<ArgumentException>();
            Action redeclare = () => od.AddU32(index, Slave, 0);
            redeclare.Should().Throw<InvalidOperationException>();
        }
    }

    [Fact]
    public async Task A_Live_Edit_Replaces_A_Queued_Verification_With_The_New_Expectation()
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
            () => Result(rig).Status == SlaveBootStatus.VerifyingIdentity, 200, "the first boot waits for the channel");
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F84, Slave, 2);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => Result(rig).Status == SlaveBootStatus.VerifyingIdentity, 200, "the new boot also waits");
        using var peer = rig.OpenPeerNode(Slave);
        SetRemote(peer, 0x1000, 0, 1);
        cancel.Cancel();
        try { await occupying; } catch (OperationCanceledException) { }
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => Result(rig).Status == SlaveBootStatus.IdentityMismatch, 300, "only the new expectation applies");
        Result(rig).ExpectedValue.Should().Be(2);
        Result(rig).ActualValue.Should().Be(1);
        Started(rig, Slave).Should().BeFalse();
    }

    [Fact]
    public async Task An_Optional_Identity_Entry_Missing_On_The_Peer_Reports_The_Peer_Abort()
    {
        using var rig = OpenMaster();
        using var peer = rig.OpenPeerNode(Slave);
        Configure(rig);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F88, Slave, 123);
        await Elect(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => Result(rig).Status == SlaveBootStatus.IdentityReadFailed, 200, "the peer does not implement serial number");
        Result(rig).Index.Should().Be(0x1018);
        Result(rig).Subindex.Should().Be(4);
        Result(rig).Error.Should().BeOfType<SdoAbortException>().Which.Origin.Should().Be(SdoAbortOrigin.Peer);
        Started(rig, Slave).Should().BeFalse();
    }

    [Fact]
    public async Task Expectation_Values_Are_Writable_Over_Sdo_And_Read_Back_Unchanged()
    {
        using var rig = OpenMaster();
        using var peer = rig.OpenPeerNode(Slave);
        peer.BindPeerDeviceDescription(LeftId, CanOpenDeviceDescription.ParseEds(ExpectationFile()));
        for (ushort index = 0x1F84; index <= 0x1F88; index++)
        {
            var write = peer.SdoDownloadAsync(LeftId, index, Slave, new byte[] { 0x78, 0x56, 0x34, 0x12 });
            await UntilAsync(rig.Clock, rig.Witness, null, () => write.IsCompleted, 200, "the master accepts the expectation over SDO");
            await write;
            rig.Node.ObjectDictionary.ReadUnsigned(index, Slave).Should().Be(0x12345678);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Described_Expectations_Are_Loaded_As_Managed_Values(bool dcf)
    {
        using var rig = OpenMaster();
        var description = dcf ? CanOpenDeviceDescription.ParseDcf(ExpectationFile())
            : CanOpenDeviceDescription.ParseEds(ExpectationFile());
        using var peer = rig.OpenPeerNode(Slave, description);
        for (ushort index = 0x1F84; index <= 0x1F88; index++)
        {
            peer.ObjectDictionary.ReadUnsigned(index, Slave).Should().Be(123);
            peer.ObjectDictionary.ReadUnsigned(index, 0).Should().Be(127);
        }
    }

    [Fact]
    public async Task Simultaneous_Start_Addresses_A_Verified_Slave_Individually()
    {
        using var rig = OpenMaster();
        using var peer = rig.OpenPeerNode(Slave);
        Configure(rig, simultaneous: true);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F85, Slave, 123);
        SetRemote(peer, 0x1018, 1, 123);
        await Elect(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Started(rig, Slave), 300, "the identity matched");
        Started(rig, 0).Should().BeFalse();
        rig.Node.State.Should().Be(NmtState.Operational);
    }

    [Fact]
    public async Task The_Boot_Deadline_Also_Bounds_An_Incomplete_Identity_Read()
    {
        using var rig = OpenMaster();
        Configure(rig);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F84, Slave, 1);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F89, 0, 100);
        await Elect(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => Result(rig).Status == SlaveBootStatus.BootTimeout, 300, "the boot deadline precedes the SDO timeout");
        await QuiesceAsync(rig.Witness, null);
        Result(rig).Status.Should().Be(SlaveBootStatus.BootTimeout);
        Started(rig, Slave).Should().BeFalse();
    }

    [Fact]
    public async Task Suppressed_Slave_Boot_Does_Not_Wait_For_Identity()
    {
        using var rig = OpenMaster();
        Configure(rig);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F84, Slave, 1);
        rig.Node.ObjectDictionary.WriteUnsigned(Startup, 0, SuppressSlaveStart);
        await Elect(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => rig.Node.State == NmtState.Operational, 200, "identity only gates enabled slave boots");
        rig.Log.Snapshot().Should().NotContain(f => f.Id == 0x600 + Slave);
        Started(rig, Slave).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_Bound_Description_Must_Declare_The_Identity_Object(bool declared)
    {
        using var rig = OpenMaster();
        using var peer = rig.OpenPeerNode(Slave);
        Configure(rig);
        SetRemote(peer, 0x1000, 0, 123);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F84, Slave, 123);
        rig.Node.BindPeerDeviceDescription(Slave, PeerSdoLaboratory.Variables(declared ? (ushort)0x1000 : (ushort)0x2000));
        await Elect(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        var expectedStatus = declared ? SlaveBootStatus.StartRequested : SlaveBootStatus.IdentityReadFailed;
        await UntilAsync(rig.Clock, rig.Witness, null,
            () => Result(rig).Status == expectedStatus, 300, "the transaction respects its bound description");
        if (!declared)
        {
            Result(rig).Error.Should().BeOfType<PeerSdoAccessException>();
            Started(rig, Slave).Should().BeFalse();
        }
    }
}
