using System;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

/// <summary>
/// The SDO client transfers an (index, sub-index) only when a peer EDS/DCF bound for that
/// server declares it. With nothing bound, only 1000h:00, 1001h:00 and 1018h:00–04 proceed.
/// A refusal is thrown before any SDO request frame.
/// </summary>
public class CanOpenPeerSdoGateTests : IClassFixture<VirtualAdapterFixture>
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);

    private static string NewSession() => VirtualAdapterFixture.NewSession("canopen-peer-sdo");

    private static ICanBus Open(string session, int channel) => VirtualAdapterFixture.Open(session, channel);

    [Fact]
    public async Task An_Object_The_Peer_Description_Lists_Is_Uploaded_And_Downloaded()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: 0x01);
        using var slave = CanOpen.OpenNode(busB, nodeId: 0x11);
        slave.ObjectDictionary.AddU16(0x2000, 0x00, 0);

        var peer = PeerSdoLaboratory.Variables(0x2000);
        peer.Contains(0x2000, 0x00).Should().BeTrue();
        master.BindPeerDeviceDescription(0x11, peer);

        await master.SdoDownloadAsync(0x11, 0x2000, 0x00, new byte[] { 0x34, 0x12 }).WithTimeoutAsync(ShortTimeout);
        var raw = await master.SdoUploadAsync(0x11, 0x2000, 0x00).WithTimeoutAsync(ShortTimeout);

        raw.Should().Equal(0x34, 0x12);
        slave.ObjectDictionary.ReadUnsigned(0x2000, 0x00).Should().Be(0x1234u);
        master.GetPeerDeviceDescription(0x11).Should().BeSameAs(peer);
    }

    [Fact]
    public void An_Object_The_Peer_Description_Omits_Is_Rejected_Before_A_Frame_Is_Sent()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var sniff = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: 0x01);
        var peer = PeerSdoLaboratory.Variables(0x2000);
        master.BindPeerDeviceDescription(0x11, peer);
        int sdoRequests = 0;
        sniff.FrameObserved += (_, e) =>
        {
            if (!e.CanFrame.IsExtendedFrame && (uint)e.CanFrame.ID == CanOpenCobId.SdoRx(0x11))
                Interlocked.Increment(ref sdoRequests);
        };

        Action uploadMissing = () => master.SdoUploadAsync(0x11, 0x2001, 0x00);
        Action downloadMissing = () => master.SdoDownloadAsync(0x11, 0x2001, 0x00, new byte[] { 0x01 });
        Action uploadOtherSub = () => master.SdoUploadAsync(0x11, 0x2000, 0x01);

        uploadMissing.Should().Throw<PeerSdoAccessException>()
            .Which.Should().Match<PeerSdoAccessException>(ex =>
                ex.PeerDescriptionLoaded && ex.ServerNodeId == 0x11 && ex.Index == 0x2001 && ex.Subindex == 0x00);
        downloadMissing.Should().Throw<PeerSdoAccessException>()
            .Which.PeerDescriptionLoaded.Should().BeTrue();
        uploadOtherSub.Should().Throw<PeerSdoAccessException>()
            .Which.Subindex.Should().Be(0x01);
        sdoRequests.Should().Be(0);
    }

    [Fact]
    public void Without_A_Peer_Description_A_Non_Mandatory_Object_Is_Rejected_Before_A_Frame_Is_Sent()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var sniff = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: 0x01);
        master.GetPeerDeviceDescription(0x11).Should().BeNull();
        int sdoRequests = 0;
        sniff.FrameObserved += (_, e) =>
        {
            if (!e.CanFrame.IsExtendedFrame && (uint)e.CanFrame.ID == CanOpenCobId.SdoRx(0x11))
                Interlocked.Increment(ref sdoRequests);
        };

        Action upload = () => master.SdoUploadAsync(0x11, 0x2000, 0x00);
        Action download = () => master.SdoDownloadAsync(0x11, 0x2000, 0x00, new byte[] { 0x01 }, SdoTransferMode.Block);

        var ex = upload.Should().Throw<PeerSdoAccessException>().Which;
        ex.PeerDescriptionLoaded.Should().BeFalse();
        ex.Index.Should().Be(0x2000);
        download.Should().Throw<PeerSdoAccessException>().Which.PeerDescriptionLoaded.Should().BeFalse();
        sdoRequests.Should().Be(0);
    }

    [Fact]
    public async Task Without_A_Peer_Description_The_Mandatory_Base_Objects_Are_Transferred()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: 0x01);
        using var slave = CanOpen.OpenNode(busB, nodeId: 0x11);

        (await master.SdoUploadAsync(0x11, 0x1000, 0x00).WithTimeoutAsync(ShortTimeout)).Should().Equal(0x00, 0x00, 0x00, 0x00);
        (await master.SdoUploadAsync(0x11, 0x1001, 0x00).WithTimeoutAsync(ShortTimeout)).Should().Equal(0x00);
        slave.ObjectDictionary.AddU32(0x1018, 0x02, 0x0000_1234, OdAccess.ReadOnly);
        slave.ObjectDictionary.AddU32(0x1018, 0x03, 0x0001_0000, OdAccess.ReadOnly);
        slave.ObjectDictionary.AddU32(0x1018, 0x04, 0x0000_002A, OdAccess.ReadOnly);

        (await master.SdoUploadAsync(0x11, 0x1018, 0x00).WithTimeoutAsync(ShortTimeout)).Should().Equal(0x01);
        (await master.SdoUploadAsync(0x11, 0x1018, 0x01).WithTimeoutAsync(ShortTimeout)).Should().Equal(0x00, 0x00, 0x00, 0x00);
        (await master.SdoUploadAsync(0x11, 0x1018, 0x02).WithTimeoutAsync(ShortTimeout)).Should().Equal(0x34, 0x12, 0x00, 0x00);
        (await master.SdoUploadAsync(0x11, 0x1018, 0x03).WithTimeoutAsync(ShortTimeout)).Should().Equal(0x00, 0x00, 0x01, 0x00);
        (await master.SdoUploadAsync(0x11, 0x1018, 0x04).WithTimeoutAsync(ShortTimeout)).Should().Equal(0x2A, 0x00, 0x00, 0x00);

        var write = await Assert.ThrowsAsync<SdoAbortException>(() =>
            master.SdoDownloadAsync(0x11, 0x1000, 0x00, new byte[] { 0x01, 0x00, 0x00, 0x00 }).WithTimeoutAsync(ShortTimeout));
        write.AbortCode.Should().Be((uint)SdoAbortCode.AttemptWriteReadOnly);
        slave.ObjectDictionary.ReadUnsigned(0x1000, 0x00).Should().Be(0u);
    }

    [Theory]
    [InlineData(0x1003, 0x00)]
    [InlineData(0x1018, 0x05)]
    [InlineData(0x1000, 0x01)]
    [InlineData(0x1001, 0x01)]
    public void Without_A_Peer_Description_Optional_And_Non_Mandatory_Subindices_Are_Rejected(int index, int subindex)
    {
        PeerSdoAccessException.IsAllowedWithoutPeerDescription((ushort)index, (byte)subindex).Should().BeFalse();

        var session = NewSession();
        using var busA = Open(session, 0);
        using var sniff = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: 0x01);
        int sdoRequests = 0;
        sniff.FrameObserved += (_, e) =>
        {
            if (!e.CanFrame.IsExtendedFrame && (uint)e.CanFrame.ID == CanOpenCobId.SdoRx(0x11))
                Interlocked.Increment(ref sdoRequests);
        };

        Action upload = () => master.SdoUploadAsync(0x11, (ushort)index, (byte)subindex);
        Action download = () => master.SdoDownloadAsync(0x11, (ushort)index, (byte)subindex, new byte[] { 0x00 });

        upload.Should().Throw<PeerSdoAccessException>().Which.PeerDescriptionLoaded.Should().BeFalse();
        download.Should().Throw<PeerSdoAccessException>().Which.Index.Should().Be((ushort)index);
        sdoRequests.Should().Be(0);
    }

    [Fact]
    public void A_Peer_Description_That_Omits_1000h_Rejects_1000h()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var sniff = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: 0x01);
        master.BindPeerDeviceDescription(0x11, PeerSdoLaboratory.Variables(0x2000));
        int sdoRequests = 0;
        sniff.FrameObserved += (_, e) =>
        {
            if (!e.CanFrame.IsExtendedFrame && (uint)e.CanFrame.ID == CanOpenCobId.SdoRx(0x11))
                Interlocked.Increment(ref sdoRequests);
        };

        Action upload = () => master.SdoUploadAsync(0x11, 0x1000, 0x00);
        Action identity = () => master.SdoUploadAsync(0x11, 0x1018, 0x01);
        Action productCode = () => master.SdoUploadAsync(0x11, 0x1018, 0x02);
        Action serial = () => master.SdoUploadAsync(0x11, 0x1018, 0x04);

        upload.Should().Throw<PeerSdoAccessException>().Which.PeerDescriptionLoaded.Should().BeTrue();
        identity.Should().Throw<PeerSdoAccessException>().Which.PeerDescriptionLoaded.Should().BeTrue();
        productCode.Should().Throw<PeerSdoAccessException>().Which.PeerDescriptionLoaded.Should().BeTrue();
        serial.Should().Throw<PeerSdoAccessException>().Which.PeerDescriptionLoaded.Should().BeTrue();
        sdoRequests.Should().Be(0);

        master.UnbindPeerDeviceDescription(0x11);
        master.GetPeerDeviceDescription(0x11).Should().BeNull();
        Action afterUnbind = () => master.SdoUploadAsync(0x11, 0x2000, 0x00);
        afterUnbind.Should().Throw<PeerSdoAccessException>().Which.PeerDescriptionLoaded.Should().BeFalse();
    }

    [Fact]
    public void A_Dcf_Commissioned_For_Another_Node_Is_Rejected_And_Does_Not_Replace_A_Binding()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var master = CanOpen.OpenNode(busA, nodeId: 0x01);
        var eds = PeerSdoLaboratory.Variables(0x2000);
        eds.NodeId.Should().BeNull();
        master.BindPeerDeviceDescription(0x11, eds);

        var dcf = PeerSdoLaboratory.Configuration(0x05, 0x2100);
        dcf.IsConfigurationFile.Should().BeTrue();
        dcf.NodeId.Should().Be(0x05);

        Action bind = () => master.BindPeerDeviceDescription(0x11, dcf);
        bind.Should().Throw<ArgumentException>();
        master.GetPeerDeviceDescription(0x11).Should().BeSameAs(eds);

        Action upload = () => master.SdoUploadAsync(0x11, 0x2100, 0x00);
        upload.Should().Throw<PeerSdoAccessException>().Which.PeerDescriptionLoaded.Should().BeTrue();
    }

    [Fact]
    public void A_Dcf_Commissioned_For_The_Same_Node_Is_Bound()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var master = CanOpen.OpenNode(busA, nodeId: 0x01);
        var dcf = PeerSdoLaboratory.Configuration(0x11, 0x2000);

        master.BindPeerDeviceDescription(0x11, dcf);

        master.GetPeerDeviceDescription(0x11).Should().BeSameAs(dcf);
    }

    [Fact]
    public void The_Mandatory_Base_Pairs_Are_1000h_1001h_And_The_Full_Identity()
    {
        PeerSdoAccessException.IsAllowedWithoutPeerDescription(0x1000, 0x00).Should().BeTrue();
        PeerSdoAccessException.IsAllowedWithoutPeerDescription(0x1001, 0x00).Should().BeTrue();
        for (byte subindex = 0; subindex <= 4; subindex++)
            PeerSdoAccessException.IsAllowedWithoutPeerDescription(0x1018, subindex).Should().BeTrue();
        PeerSdoAccessException.IsAllowedWithoutPeerDescription(0x1003, 0x00).Should().BeFalse();
        PeerSdoAccessException.IsAllowedWithoutPeerDescription(0x1018, 0x05).Should().BeFalse();
    }
}
