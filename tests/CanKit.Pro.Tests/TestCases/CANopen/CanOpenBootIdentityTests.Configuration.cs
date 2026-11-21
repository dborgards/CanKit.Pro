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

// FR-CO-067: configuration is a separate gate after identity and before safety/Start.
public partial class CanOpenBootIdentityTests
{
    private const uint ConfigurationDate = 0x12345678, ConfigurationTime = 0x9ABCDEF0;

    private static CanOpenNodeOptions ConfigurationOptions(bool active = false) => new()
    {
        RequireBootConfigurationMatch = true,
        ActiveSlaveBootRetryInterval = active ? TimeSpan.FromMilliseconds(100) : null,
        SdoTimeout = TimeSpan.FromMilliseconds(50)
    };

    private static void ConfigureVersion(MasterRig rig, bool mandatory = true, bool simultaneous = false,
        uint date = ConfigurationDate, uint time = ConfigurationTime)
    {
        Configure(rig, mandatory, simultaneous);
        rig.Node.BindPeerDeviceDescription(Slave, CanOpenDeviceDescription.ParseEds(ConfigurationDescription(false)));
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F26, Slave, date);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F27, Slave, time);
    }

    private static (int, byte)[] ConfigurationRequests(MasterRig rig, byte slave = Slave)
        => rig.Log.Snapshot().Where(f => f.Id == 0x600 + slave && f.Data.Length == 8 && f.Data[0] == 0x40)
            .Select(f => (f.Data[1] | f.Data[2] << 8, f.Data[3])).ToArray();

    private static void SetRemoteVersion(CanOpenNode peer, uint date = ConfigurationDate, uint time = ConfigurationTime)
    {
        SetRemote(peer, 0x1020, 1, date);
        SetRemote(peer, 0x1020, 2, time);
    }

    private static byte[] ConfigurationReply(byte[] request)
        => BootReply(request, request[1] == 0x20 ? request[3] == 1 ? ConfigurationDate : ConfigurationTime : 123);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Configuration_Match_Precedes_Individual_Start(bool active, bool simultaneous)
    {
        using var rig = OpenMaster(ConfigurationOptions(active));
        using var peer = new SilentBootPeer(rig.Peer, Slave, ConfigurationReply);
        ConfigureVersion(rig, simultaneous: simultaneous);
        await ElectActive(rig);
        if (!active) TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Started(rig, Slave), 300, "both configuration values matched");
        ConfigurationRequests(rig).Where(r => r.Item1 == 0x1020).Should().Equal((0x1020, (byte)1), (0x1020, (byte)2));
        rig.Node.State.Should().Be(NmtState.Operational);
        Started(rig, 0).Should().BeFalse();
        var frames = rig.Log.Snapshot();
        frames.FindLastIndex(f => f.Id == 0x580 + Slave).Should().BeLessThan(frames.FindIndex(f => IsNmt(f, NmtCommand.Start, Slave)));
        rig.Node.ObjectDictionary.ReadUnsigned(0x1F26, Slave).Should().Be(ConfigurationDate, "cold election preserves the expectations");
        rig.Node.ObjectDictionary.ReadUnsigned(0x1F27, Slave).Should().Be(ConfigurationTime);
    }

    [Fact]
    public async Task Configuration_Check_Follows_All_Required_Identity_Reads()
    {
        using var rig = OpenMaster(new CanOpenNodeOptions { RequireBootConfigurationMatch = true, RequireBootDeviceTypeUpload = true });
        using var peer = rig.OpenPeerNode(Slave);
        ConfigureVersion(rig);
        SetRemoteVersion(peer);
        SetRemote(peer, 0x1018, 1, 123);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F85, Slave, 123);
        await Elect(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Started(rig, Slave), 300, "identity precedes configuration");
        ConfigurationRequests(rig).Should().Equal((0x1000, (byte)0), (0x1018, (byte)1), (0x1020, (byte)1), (0x1020, (byte)2));
    }

    [Theory]
    [InlineData(0u, 0u, 1)]
    [InlineData(0u, ConfigurationTime, 1)]
    [InlineData(ConfigurationDate, 0u, 2)]
    public async Task A_Zero_Configuration_Expectation_Requires_Configuration_Without_Any_Upload(uint date, uint time, byte subindex)
    {
        using var rig = OpenMaster(ConfigurationOptions());
        ConfigureVersion(rig, date: date, time: time);
        await Elect(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Result(rig).Status == SlaveBootStatus.ConfigurationRequired, 200, "zero requests configuration");
        Result(rig).Index.Should().Be(0x1020);
        Result(rig).Subindex.Should().Be(subindex);
        Result(rig).ExpectedValue.Should().Be(0);
        Result(rig).ActualValue.Should().BeNull();
        Result(rig).Error.Should().BeNull();
        ConfigurationRequests(rig).Should().BeEmpty();
        Started(rig, Slave).Should().BeFalse();
        rig.Node.State.Should().Be(NmtState.PreOperational);
    }

    [Theory]
    [InlineData(1, true, false)]
    [InlineData(2, true, false)]
    [InlineData(1, false, false)]
    [InlineData(2, false, true)]
    public async Task A_Configuration_Mismatch_Blocks_Start_And_Reports_The_Pair(byte subindex, bool mandatory, bool simultaneous)
    {
        using var rig = OpenMaster(ConfigurationOptions());
        using var peer = rig.OpenPeerNode(Slave);
        ConfigureVersion(rig, mandatory, simultaneous);
        SetRemoteVersion(peer);
        SetRemote(peer, 0x1020, subindex, 42);
        int signals = 0;
        rig.Node.FlyingMasterChanged += (_, e) => { if (e.Signal == FlyingMasterSignal.SlaveConfigurationInvalid) Interlocked.Increment(ref signals); };
        await Elect(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Result(rig).Status == SlaveBootStatus.ConfigurationRequired, 200, "a mismatch is configuration work, not permission to start");
        var snapshot = Result(rig);
        snapshot.Index.Should().Be(0x1020);
        snapshot.Subindex.Should().Be(subindex);
        snapshot.ExpectedValue.Should().Be(subindex == 1 ? ConfigurationDate : ConfigurationTime);
        snapshot.ActualValue.Should().Be(42);
        signals.Should().Be(1);
        rig.Node.State.Should().Be(mandatory ? NmtState.PreOperational : NmtState.Operational,
            "without mandatory slaves the announcement-based master already started before the optional announcement");
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await QuiesceAsync(rig.Witness, null);
        Result(rig).Should().BeSameAs(snapshot);
        ConfigurationRequests(rig).Length.Should().Be(subindex);
        Started(rig, Slave).Should().BeFalse();
        Started(rig, 0).Should().BeFalse();
        rig.Log.Snapshot().Any(f => IsNmt(f, NmtCommand.ResetNode, Slave)).Should().Be(mandatory || simultaneous);
        rig.Log.Snapshot().Should().NotContain(f => f.Id == 0x600 + Slave && f.Data.Length == 8 && (f.Data[0] & 0xE0) == 0x20, "this check never downloads configuration");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_Operational_Announcement_Still_Requires_A_Configuration_Match(bool matches)
    {
        using var rig = OpenMaster(ConfigurationOptions());
        using var peer = rig.OpenPeerNode(Slave);
        ConfigureVersion(rig);
        SetRemoteVersion(peer, time: matches ? ConfigurationTime : 42);
        await Elect(rig);
        TransmitHeartbeat(rig.Peer, Slave, 5);
        var expected = matches ? SlaveBootStatus.AlreadyOperational : SlaveBootStatus.ConfigurationRequired;
        await UntilAsync(rig.Clock, rig.Witness, null, () => Result(rig).Status == expected, 200, "the opt-in match gate also applies to a running slave");
        ConfigurationRequests(rig).Length.Should().Be(2);
        Started(rig, Slave).Should().BeFalse();
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task A_Configuration_Read_Failure_Retains_The_Upload_Cause(byte subindex, bool wrongWidth)
    {
        using var rig = OpenMaster(ConfigurationOptions());
        using var peer = rig.OpenPeerNode(Slave);
        ConfigureVersion(rig);
        if (subindex == 2) SetRemote(peer, 0x1020, 1, ConfigurationDate);
        if (wrongWidth) peer.ObjectDictionary.Declare(0x1020, subindex, OdDataType.Domain, OdAccess.ReadOnly, new byte[] { 1 }, pdoMappable: false);
        await Elect(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Result(rig).Status == SlaveBootStatus.ConfigurationReadFailed, 200, "the upload failed");
        Result(rig).Subindex.Should().Be(subindex);
        Result(rig).ExpectedValue.Should().Be(subindex == 1 ? ConfigurationDate : ConfigurationTime);
        if (wrongWidth) Result(rig).Error.Should().BeOfType<InvalidDataException>();
        else Result(rig).Error.Should().BeOfType<SdoAbortException>();
        Started(rig, Slave).Should().BeFalse();
    }

    [Fact]
    public async Task A_Mandatory_1020_Timeout_Does_Not_Use_The_1000_No_Response_Retry()
    {
        using var rig = OpenMaster(ConfigurationOptions(active: true));
        using var peer = new SilentBootPeer(rig.Peer, Slave, r => r[1] == 0 ? BootReply(r, 123) : null);
        ConfigureVersion(rig);
        await ElectActive(rig);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Result(rig).Status == SlaveBootStatus.ConfigurationReadFailed, 150, "1020 times out");
        Result(rig).Error.Should().BeOfType<SdoAbortException>().Which.Origin.Should().Be(SdoAbortOrigin.Local);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromSeconds(1));
        peer.Requests.Should().Be(2);
        Started(rig, Slave).Should().BeFalse();
    }

    [Fact]
    public async Task An_Optional_Configuration_Failure_Retries_After_The_Master_Starts()
    {
        using var rig = OpenMaster(ConfigurationOptions(active: true));
        bool repaired = false;
        using var peer = new SilentBootPeer(rig.Peer, Slave, r => r[1] == 0x20 && !repaired ? BootReply(r, 42) : ConfigurationReply(r));
        ConfigureVersion(rig, mandatory: false, simultaneous: true);
        await ElectActive(rig);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Result(rig).Status == SlaveBootStatus.ConfigurationRequired, 200, "the first optional attempt ended");
        rig.Node.State.Should().Be(NmtState.Operational);
        Started(rig, Slave).Should().BeFalse();
        int requests = peer.Requests;
        repaired = true;
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(99));
        peer.Requests.Should().Be(requests);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(1));
        await UntilAsync(rig.Clock, rig.Witness, null, () => Started(rig, Slave), 200, "the repaired optional slave joins");
        peer.Requests.Should().Be(requests + 3, "retry repeats identity and both configuration values");
        Started(rig, 0).Should().BeFalse();
    }
}
