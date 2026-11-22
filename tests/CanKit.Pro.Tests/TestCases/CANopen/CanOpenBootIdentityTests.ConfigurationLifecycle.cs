using System;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using Xunit;
using static CanKit.Pro.Tests.TestCases.CANopen.FlyingMasterRig;

namespace CanKit.Pro.Tests.TestCases.CANopen;

public partial class CanOpenBootIdentityTests
{
    private static SemaphoreSlim ConfigurationChannel(MasterRig rig)
        => (SemaphoreSlim)typeof(CanOpenNode).GetMethod("PeerSdoChannel", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(rig.Node, new object[] { Slave })!;

    private static void DeliverConfiguration(CanOpenNode node, int generation, SlaveBootResult result)
        => InvokeBoot(node, "OnSlaveConfigurationVerified", Slave, generation, result);

    internal static string ConfigurationDescription(bool expectations)
    {
        string identity = ExpectationFile();
        string header = identity.Substring(0, identity.IndexOf("[OptionalObjects]", StringComparison.Ordinal));
        if (!expectations) header = header.Replace("SupportedObjects=0", "SupportedObjects=2\n1=0x1000\n2=0x1018");
        var text = new StringBuilder(header);
        var indices = expectations ? new ushort[] { 0x1F26, 0x1F27 } : new ushort[] { 0x1020 };
        text.Append($"[OptionalObjects]\nSupportedObjects={indices.Length}\n");
        for (int i = 0; i < indices.Length; i++) text.Append($"{i + 1}=0x{indices[i]:X4}\n");
        foreach (ushort index in indices)
        {
            text.Append($"\n[{index:X4}]\nParameterName=Configuration version\nObjectType=0x8\nSubNumber={(expectations ? 2 : 3)}\n"
                + $"[{index:X4}sub0]\nParameterName=Count\nObjectType=0x7\nDataType=0x0005\nAccessType=ro\nDefaultValue={(expectations ? 127 : 2)}\nPDOMapping=0\n");
            foreach (byte sub in expectations ? new[] { Slave } : new byte[] { 1, 2 })
            {
                uint value = expectations ? index == 0x1F26 ? ConfigurationDate : ConfigurationTime : sub == 1 ? ConfigurationDate : ConfigurationTime;
                text.Append($"[{index:X4}sub{sub:X}]\nParameterName=Version\nObjectType=0x7\nDataType=0x0007\nAccessType=rw\nDefaultValue=0x{value:X8}\nParameterValue=0x{value:X8}\nPDOMapping=0\n");
            }
        }
        if (!expectations) text.Append("""

            [1000]
            ParameterName=Device type
            ObjectType=0x7
            DataType=0x0007
            AccessType=ro
            DefaultValue=0
            PDOMapping=0
            [1018]
            ParameterName=Identity
            ObjectType=0x9
            SubNumber=2
            [1018sub0]
            ParameterName=Count
            ObjectType=0x7
            DataType=0x0005
            AccessType=ro
            DefaultValue=1
            PDOMapping=0
            [1018sub1]
            ParameterName=Vendor
            ObjectType=0x7
            DataType=0x0007
            AccessType=ro
            DefaultValue=123
            PDOMapping=0

            """);
        return text.ToString();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Configuration_Expectations_Are_Managed_From_Eds_And_Dcf(bool dcf)
    {
        using var rig = OpenMaster();
        var description = dcf ? CanOpenDeviceDescription.ParseDcf(ConfigurationDescription(true)) : CanOpenDeviceDescription.ParseEds(ConfigurationDescription(true));
        using var peer = rig.OpenPeerNode(Slave, description, ConfigurationOptions());
        foreach (ushort index in new ushort[] { 0x1F26, 0x1F27 })
        {
            peer.ObjectDictionary.ReadUnsigned(index, 0).Should().Be(127);
            peer.ObjectDictionary.ReadUnsigned(index, Slave).Should().Be(index == 0x1F26 ? ConfigurationDate : ConfigurationTime);
            Action count = () => peer.ObjectDictionary.WriteUnsigned(index, 0, 1);
            count.Should().Throw<ArgumentException>();
            Action redeclare = () => peer.ObjectDictionary.AddU32(index, Slave, 42);
            redeclare.Should().Throw<InvalidOperationException>();
        }
    }

    [Fact]
    public async Task Configuration_Expectations_Can_Be_Written_Through_Sdo()
    {
        using var rig = OpenMaster(ConfigurationOptions());
        using var peer = rig.OpenPeerNode(Slave);
        peer.BindPeerDeviceDescription(LeftId, CanOpenDeviceDescription.ParseEds(ConfigurationDescription(true)));
        foreach (ushort index in new ushort[] { 0x1F26, 0x1F27 })
        {
            var write = peer.SdoDownloadAsync(LeftId, index, Slave, new byte[] { 0x78, 0x56, 0x34, 0x12 });
            await UntilAsync(rig.Clock, rig.Witness, null, () => write.IsCompleted, 200, "the expectation is writable through SDO");
            await write;
            rig.Node.ObjectDictionary.ReadUnsigned(index, Slave).Should().Be(ConfigurationDate);
        }
    }

    [Fact]
    public async Task Configuration_Check_Defaults_Off_And_Leaves_Expectation_Objects_Application_Owned()
    {
        using var rig = OpenMaster();
        Configure(rig);
        foreach (ushort index in new ushort[] { 0x1F26, 0x1F27 })
        {
            rig.Node.ObjectDictionary.TryGet(index, Slave, out _).Should().BeFalse();
            rig.Node.ObjectDictionary.AddU32(index, Slave, 42);
            rig.Node.ObjectDictionary.AddU32(index, Slave, 43);
        }
        await Elect(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Started(rig, Slave), 200, "default boot does not check application-owned configuration entries");
        ConfigurationRequests(rig).Should().BeEmpty();
        new CanOpenNodeOptions().RequireBootConfigurationMatch.Should().BeFalse();
        var options = ConfigurationOptions();
        options.With().RequireBootConfigurationMatch.Should().BeTrue();
        options.WithLss(null).RequireBootConfigurationMatch.Should().BeTrue();
        options.WithErrorHistoryCapacity(4).RequireBootConfigurationMatch.Should().BeTrue();
        options.WithSrdoConfigurationCheckInterval(TimeSpan.FromSeconds(1)).RequireBootConfigurationMatch.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Configuration_Uploads_Respect_The_Bound_Description(int description)
    {
        using var rig = OpenMaster(ConfigurationOptions());
        using var peer = rig.OpenPeerNode(Slave);
        SetRemoteVersion(peer);
        ConfigureVersion(rig);
        if (description == 0) rig.Node.UnbindPeerDeviceDescription(Slave);
        else rig.Node.BindPeerDeviceDescription(Slave, description == 2 ? CanOpenDeviceDescription.ParseEds(ConfigurationDescription(false)) : PeerSdoLaboratory.Variables(0x2000));
        await Elect(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        var expected = description == 2 ? SlaveBootStatus.StartRequested : SlaveBootStatus.ConfigurationReadFailed;
        await UntilAsync(rig.Clock, rig.Witness, null, () => Result(rig).Status == expected, 200, "the bound description gates both uploads");
        if (description != 2)
        {
            Result(rig).Error.Should().BeOfType<PeerSdoAccessException>();
            ConfigurationRequests(rig).Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Configuration_Reads_Pin_One_Description_Snapshot()
    {
        using var rig = OpenMaster(ConfigurationOptions());
        using var peer = new SilentBootPeer(rig.Peer, Slave, request =>
        {
            if (request[1] == 0x20 && request[3] == 1)
            {
                rig.Node.BindPeerDeviceDescription(Slave, PeerSdoLaboratory.Variables(0x2000));
            }
            return ConfigurationReply(request);
        });
        ConfigureVersion(rig);
        await Elect(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Started(rig, Slave), 300, "configuration keeps one transaction snapshot");
        Assert.Throws<PeerSdoAccessException>(() => { _ = rig.Node.SdoUploadAsync(Slave, 0x1020, 2); });
        ConfigurationRequests(rig).Take(2).Should().Equal((0x1020, (byte)1), (0x1020, (byte)2));
    }

    [Fact]
    public async Task Configuration_Boot_Deadline_Includes_The_Wait_For_The_Shared_Channel()
    {
        using var rig = OpenMaster(ConfigurationOptions());
        ConfigureVersion(rig);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F89, 0, 100);
        using var held = rig.Witness.HoldPeerSdoChannel(Slave, ConfigurationChannel(rig));
        await Elect(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Result(rig).Status == SlaveBootStatus.VerifyingConfiguration, 50, "configuration waits for the channel");
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Result(rig).Status == SlaveBootStatus.BootTimeout, 150, "the master cannot finish while configuration is unverified");
        held.Dispose();
        await QuiesceAsync(rig.Witness, null);
        ConfigurationRequests(rig).Should().BeEmpty();
        Started(rig, Slave).Should().BeFalse();
    }

    [Fact]
    public async Task Configuration_Deadline_Only_Fails_The_Still_Unverified_Mandatory_Slave()
    {
        const byte waiting = 6;
        using var rig = OpenMaster(ConfigurationOptions());
        using var peer = rig.OpenPeerNode(Slave);
        ConfigureVersion(rig);
        SetRemoteVersion(peer);
        var od = rig.Node.ObjectDictionary;
        od.WriteUnsigned(0x1F81, waiting, Assigned | MandatorySlave | BootSlave | 0x10u);
        od.WriteUnsigned(0x1F26, waiting, ConfigurationDate);
        od.WriteUnsigned(0x1F27, waiting, ConfigurationTime);
        od.WriteUnsigned(0x1F89, 0, 100);
        var channels = (System.Collections.Concurrent.ConcurrentDictionary<byte, SemaphoreSlim>)BootField(rig.Node, "_peerSdoChannels");
        using var held = rig.Witness.HoldPeerSdoChannel(waiting, channels.GetOrAdd(waiting, _ => new SemaphoreSlim(1, 1)));
        await Elect(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        TransmitHeartbeat(rig.Peer, waiting, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Started(rig, Slave), 50, "the first mandatory slave is verified");
        await UntilAsync(rig.Clock, rig.Witness, null, () => rig.Node.GetSlaveBootResults().Single(r => r.NodeId == waiting).Status == SlaveBootStatus.BootTimeout,
            150, "only the waiting configuration exceeds the deadline");
        Result(rig).Status.Should().Be(SlaveBootStatus.StartRequested);
        rig.Log.Snapshot().Should().NotContain(f => IsNmt(f, NmtCommand.ResetNode, Slave));
        rig.Log.Snapshot().Should().Contain(f => IsNmt(f, NmtCommand.ResetNode, waiting));
    }

    [Fact]
    public async Task Active_Boot_Deadline_Includes_Configuration_After_Identity_Succeeded()
    {
        using var rig = OpenMaster(ConfigurationOptions(active: true));
        bool respond = false;
        using var peer = new SilentBootPeer(rig.Peer, Slave, r => r[1] == 0 || respond ? ConfigurationReply(r) : null);
        ConfigureVersion(rig);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F89, 0, 25);
        await ElectActive(rig);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Result(rig).Status == SlaveBootStatus.BootTimeout, 100, "configuration remains mandatory after contact");
        respond = true;
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(500));
        peer.Requests.Should().Be(2);
        Started(rig, Slave).Should().BeFalse();
    }

    [Theory]
    [InlineData(0u, false)]
    [InlineData(Assigned | MandatorySlave, false)]
    [InlineData(Assigned | MandatorySlave | BootSlave, true)]
    public async Task Configuration_Check_Does_Not_Boot_Excluded_Slaves(uint assignment, bool suppress)
    {
        using var rig = OpenMaster(ConfigurationOptions());
        ConfigureVersion(rig);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F81, Slave, assignment | 0x10u);
        if (suppress) rig.Node.ObjectDictionary.WriteUnsigned(Startup, 0, SuppressSlaveStart);
        await Elect(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(200));
        ConfigurationRequests(rig).Should().BeEmpty();
        Started(rig, Slave).Should().BeFalse();
        rig.Node.State.Should().Be(NmtState.Operational);
    }

    [Theory]
    [InlineData(0x1F26)]
    [InlineData(0x1F27)]
    public async Task A_Live_Configuration_Edit_Replaces_The_Waiting_Check_And_Rejects_Its_Stale_Result(ushort index)
    {
        using var rig = OpenMaster(ConfigurationOptions());
        using var peer = rig.OpenPeerNode(Slave);
        SetRemoteVersion(peer);
        ConfigureVersion(rig);
        using var held = rig.Witness.HoldPeerSdoChannel(Slave, ConfigurationChannel(rig));
        await Elect(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Result(rig).Status == SlaveBootStatus.VerifyingConfiguration, 50, "first check waits");
        int old = (int)BootField(rig.Node, "_bootGeneration");
        var snapshot = Result(rig);
        rig.Node.ObjectDictionary.WriteUnsigned(index, Slave, 42);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Result(rig).Status == SlaveBootStatus.VerifyingConfiguration, 50, "new check waits");
        await rig.Node.PostToActorAsync(() => DeliverConfiguration(rig.Node, old, new SlaveBootResult(Slave, SlaveBootStatus.ConfigurationVerified)));
        ((bool[])BootField(rig.Node, "_slaveConfigurationVerifying"))[Slave].Should().BeTrue();
        held.Dispose();
        await UntilAsync(rig.Clock, rig.Witness, null, () => Result(rig).Status == SlaveBootStatus.ConfigurationRequired, 200, "only the new expectation applies");
        Result(rig).ExpectedValue.Should().Be(42);
        Result(rig).Subindex.Should().Be(index == 0x1F26 ? (byte)1 : (byte)2);
        snapshot.Status.Should().Be(SlaveBootStatus.VerifyingConfiguration);
        var before = Result(rig);
        await rig.Node.PostToActorAsync(() => DeliverConfiguration(rig.Node, (int)BootField(rig.Node, "_bootGeneration"), new SlaveBootResult(Slave, SlaveBootStatus.ConfigurationVerified)));
        Result(rig).Should().BeSameAs(before, "a duplicate result cannot reopen the completed check");
        Started(rig, Slave).Should().BeFalse();
    }

    [Fact]
    public async Task Role_Loss_Cancels_Configuration_Waiting_For_The_Channel()
    {
        using var rig = OpenMaster(ConfigurationOptions());
        ConfigureVersion(rig);
        using var held = rig.Witness.HoldPeerSdoChannel(Slave, ConfigurationChannel(rig));
        await Elect(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Result(rig).Status == SlaveBootStatus.VerifyingConfiguration, 50, "configuration waits");
        rig.Node.StopFlyingMaster();
        held.Dispose();
        await QuiesceAsync(rig.Witness, null);
        rig.Node.GetSlaveBootResults().Should().BeEmpty();
        ConfigurationRequests(rig).Should().BeEmpty();
    }

    [Theory]
    [InlineData("_disposed", 1)]
    [InlineData("_flyingMasterRole", FlyingMasterRole.Standby)]
    [InlineData("_bootHalted", true)]
    [InlineData("_coldResetPending", true)]
    public async Task A_Configuration_Result_Respects_Lifecycle_Barriers(string field, object value)
    {
        using var rig = OpenMaster(ConfigurationOptions());
        ConfigureVersion(rig);
        await Elect(rig);
        var before = Result(rig);
        await rig.Node.PostToActorAsync(() =>
        {
            var original = BootField(rig.Node, field);
            ((bool[])BootField(rig.Node, "_slaveConfigurationVerifying"))[Slave] = true;
            SetBootField(rig.Node, field, value);
            try
            {
                DeliverConfiguration(rig.Node, (int)BootField(rig.Node, "_bootGeneration"), new SlaveBootResult(Slave, SlaveBootStatus.ConfigurationVerified));
                ((bool[])BootField(rig.Node, "_slaveConfigurationVerified"))[Slave].Should().BeFalse();
            }
            finally { SetBootField(rig.Node, field, original); }
        });
        Result(rig).Should().BeSameAs(before);
        Started(rig, Slave).Should().BeFalse();
    }

    [Fact]
    public void A_Configuration_Result_After_Dispose_Is_Discarded()
    {
        using var rig = OpenMaster(ConfigurationOptions());
        rig.Node.Dispose();
        InvokeBoot(rig.Node, "PostSlaveConfigurationResult", Slave, 0, new SlaveBootResult(Slave, SlaveBootStatus.ConfigurationVerified), CancellationToken.None);
    }

    [Fact]
    public async Task Configuration_Is_Rechecked_After_An_Abandoned_Forced_Reset()
    {
        using var rig = OpenMaster(ConfigurationOptions());
        using var peer = rig.OpenPeerNode(Slave);
        ConfigureVersion(rig);
        SetRemoteVersion(peer);
        using var held = rig.Witness.HoldPeerSdoChannel(Slave, ConfigurationChannel(rig));
        await Elect(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Result(rig).Status == SlaveBootStatus.VerifyingConfiguration, 50, "the worker waits");
        await rig.Node.PostToActorAsync(() => SetBootField(rig.Node, "_coldResetPending", true));
        held.Dispose();
        await QuiesceAsync(rig.Witness, null);
        Started(rig, Slave).Should().BeFalse();
        ConfigurationRequests(rig).Length.Should().Be(2);
        await rig.Node.PostToActorAsync(() =>
        {
            SetBootField(rig.Node, "_coldResetPending", false);
            InvokeBoot(rig.Node, "ResumeHeldBoot");
        });
        await UntilAsync(rig.Clock, rig.Witness, null, () => Started(rig, Slave), 200, "an abandoned reset repeats the check");
        ConfigurationRequests(rig).Length.Should().Be(4);
    }
}
