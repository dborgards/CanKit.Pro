using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Sdo;
using EdsDcfNet.Models;
using Xunit;
using static CanKit.Pro.Tests.TestCases.CANopen.FlyingMasterRig;

namespace CanKit.Pro.Tests.TestCases.CANopen;

// FR-CO-069: application-supplied concise DCF integrates with configuration check/restore/boot.
public partial class CanOpenBootIdentityTests
{
    private static CanOpenNodeOptions DownloadOptions(bool active = false) => new()
    {
        EnableBootConfigurationDownload = true,
        ActiveSlaveBootRetryInterval = active ? TimeSpan.FromMilliseconds(100) : null,
        SdoTimeout = TimeSpan.FromMilliseconds(100)
    };

    private static byte[] BootDcf(params (ushort Index, byte Subindex, byte[] Value)[] writes)
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

    private static CanOpenDeviceDescription DownloadDescription()
    {
        var description = CanOpenDeviceDescription.ParseEds(ConfigurationDescription(false));
        var variables = PeerSdoLaboratory.Variables(0x2000, 0x2001);
        foreach (var pair in variables.Objects.Objects)
        {
            pair.Value.DataType = (ushort)OdDataType.Domain;
            description.Objects.Objects.Add(pair.Key, pair.Value);
        }
        // Reuse the independently parsed record layout, then supply the restore object index.
        var restore = CanOpenDeviceDescription.ParseEds(ConfigurationDescription(false).Replace("1020", "1011"));
        description.Objects.Objects.Add(0x1011, restore.Objects.Objects[0x1011]);
        return description;
    }

    private static void ConfigureDownload(MasterRig rig, bool mandatory = true, bool simultaneous = false,
        bool restore = false, byte restoreSub = 1, uint date = 0, uint time = 0)
    {
        Configure(rig, mandatory, simultaneous);
        rig.Node.BindPeerDeviceDescription(Slave, DownloadDescription());
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F26, Slave, date);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F27, Slave, time);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F8A, Slave, restoreSub);
        if (restore) rig.Node.ObjectDictionary.WriteUnsigned(0x1F81, Slave,
            rig.Node.ObjectDictionary.ReadUnsigned(0x1F81, Slave) | 0x80u);
        CanOpenNodeExtensions.SetSlaveBootConfiguration(rig.Node, Slave, BootDcf((0x2000, 0, new byte[] { 42 })));
    }

    private static bool IsDownload((uint Id, byte[] Data) frame, ushort index)
        => frame.Id == 0x600 + Slave && frame.Data.Length == 8 && (frame.Data[0] & 0xE0) == 0x20
           && (frame.Data[1] | frame.Data[2] << 8) == index;

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public async Task Boot_Download_Updates_Before_Individual_Start(bool active, bool simultaneous, bool mismatch, bool operational)
    {
        using var rig = OpenMaster(DownloadOptions(active));
        using var peer = rig.OpenPeerNode(Slave);
        peer.ObjectDictionary.AddDomain(0x2000, 0, new byte[] { 1 });
        SetRemoteVersion(peer, date: 1);
        ConfigureDownload(rig, simultaneous: simultaneous, date: mismatch ? ConfigurationDate : 0, time: ConfigurationTime);
        await ElectActive(rig);
        if (!active) TransmitHeartbeat(rig.Peer, Slave, operational ? (byte)5 : (byte)0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Started(rig, Slave), 500, "configuration precedes Start");
        peer.ObjectDictionary.ReadRaw(0x2000, 0).Should().Equal(42);
        var frames = rig.Log.Snapshot();
        int write = frames.FindIndex(f => IsDownload(f, 0x2000));
        frames.FindIndex(f => IsNmt(f, NmtCommand.EnterPreOperational, Slave)).Should().BeLessThan(write);
        write.Should().BeLessThan(frames.FindIndex(f => IsNmt(f, NmtCommand.Start, Slave)));
        frames.Should().NotContain(f => IsDownload(f, 0x1011), "bit 7 is clear");
        Started(rig, 0).Should().BeFalse();
        rig.Node.State.Should().Be(NmtState.Operational);
        ConfigurationRequests(rig).First().Should().Be((0x1000, (byte)0));
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public async Task Boot_Restore_Is_Acknowledged_Before_Reset_And_Download(bool restore, byte sub)
    {
        using var rig = OpenMaster(DownloadOptions());
        using var peer = rig.OpenPeerNode(Slave);
        peer.ObjectDictionary.AddDomain(0x2000, 0, new byte[] { 1 });
        ConfigureDownload(rig, restore: restore, restoreSub: sub);
        await ElectActive(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Started(rig, Slave), 500, "restore/reset/download completes");
        var frames = rig.Log.Snapshot();
        if (restore && sub != 0)
        {
            int command = frames.FindIndex(f => IsDownload(f, 0x1011));
            frames[command].Data.Skip(4).Should().Equal(0x6C, 0x6F, 0x61, 0x64);
            int ack = frames.FindIndex(command + 1, f => f.Id == 0x580 + Slave && f.Data[0] == 0x60 && f.Data[1] == 0x11);
            int reset = frames.FindIndex(f => IsNmt(f, NmtCommand.ResetNode, Slave));
            int boot = frames.FindIndex(reset + 1, f => f.Id == 0x700 + Slave && f.Data[0] == 0);
            ack.Should().BeGreaterThan(command);
            reset.Should().BeGreaterThan(ack);
            boot.Should().BeGreaterThan(reset);
            frames.FindIndex(f => IsDownload(f, 0x2000)).Should().BeGreaterThan(boot);
        }
        else frames.Should().NotContain(f => IsDownload(f, 0x1011) || IsNmt(f, NmtCommand.ResetNode, Slave));
        peer.ObjectDictionary.ReadRaw(0x2000, 0).Should().Equal(42);
    }

    [Fact]
    public async Task Matching_Configuration_Does_Not_Restore_Or_Download()
    {
        using var rig = OpenMaster(DownloadOptions());
        using var peer = rig.OpenPeerNode(Slave);
        SetRemoteVersion(peer);
        ConfigureDownload(rig, restore: true, date: ConfigurationDate, time: ConfigurationTime);
        await ElectActive(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Started(rig, Slave), 500, "matching versions skip the update");
        rig.Log.Snapshot().Should().NotContain(f => IsDownload(f, 0x1011) || IsDownload(f, 0x2000)
            || IsNmt(f, NmtCommand.EnterPreOperational, Slave) || IsNmt(f, NmtCommand.ResetNode, Slave));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_Or_Removed_Plan_Still_Requires_Configuration(bool remove)
    {
        using var rig = OpenMaster(DownloadOptions());
        using var peer = rig.OpenPeerNode(Slave);
        ConfigureDownload(rig);
        CanOpenNodeExtensions.SetSlaveBootConfiguration(rig.Node, Slave, null);
        if (remove)
        {
            var broken = BootDcf((0x2000, 0, new byte[] { 42 })).Take(8).ToArray();
            Action register = () => CanOpenNodeExtensions.SetSlaveBootConfiguration(rig.Node, Slave, broken);
            register.Should().Throw<InvalidDataException>();
        }
        await ElectActive(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Result(rig).Status == SlaveBootStatus.ConfigurationRequired, 300, "no usable plan");
        Started(rig, Slave).Should().BeFalse();
        rig.Log.Snapshot().Should().NotContain(f => IsDownload(f, 0x2000));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Boot_Download_Preflight_Prevents_Restore_For_An_Invalid_Target(int invalid)
    {
        using var rig = OpenMaster(DownloadOptions());
        using var peer = rig.OpenPeerNode(Slave);
        ConfigureDownload(rig, mandatory: false, restore: true);
        var description = DownloadDescription();
        if (invalid == 0) description.Objects.Objects.Remove(0x2000);
        if (invalid == 1) description.Objects.Objects[0x2000].AccessType = AccessType.ReadOnly;
        if (invalid == 2) description.Objects.Objects.Remove(0x1011);
        if (invalid == 3) description.Objects.Objects[0x1011].SubObjects[1].AccessType = AccessType.ReadOnly;
        rig.Node.BindPeerDeviceDescription(Slave, description);
        await ElectActive(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Result(rig).Status == SlaveBootStatus.ConfigurationDownloadFailed, 300, "all targets preflight before restore");
        Result(rig).Index.Should().Be(invalid < 2 ? (ushort)0x2000 : (ushort)0x1011);
        Result(rig).Error.Should().BeAssignableTo<InvalidOperationException>();
        rig.Log.Snapshot().Should().NotContain(f => IsDownload(f, 0x1011) || IsDownload(f, 0x2000) || IsNmt(f, NmtCommand.EnterPreOperational, Slave));
        Started(rig, Slave).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_Boot_Write_Reports_Original_Abort_And_Stops_The_Prefix(bool restore)
    {
        using var rig = OpenMaster(DownloadOptions());
        using var peer = rig.OpenPeerNode(Slave);
        peer.ObjectDictionary.AddDomain(0x2000, 0, Array.Empty<byte>());
        ConfigureDownload(rig, mandatory: false, restore: restore, restoreSub: 2);
        CanOpenNodeExtensions.SetSlaveBootConfiguration(rig.Node, Slave,
            BootDcf((0x2000, 0, new byte[] { 42 }), (0x2001, 0, new byte[] { 43 }), (0x2000, 0, new byte[] { 44 })));
        await ElectActive(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Result(rig).Status == SlaveBootStatus.ConfigurationDownloadFailed, 400, "remote abort terminates the sequence");
        Result(rig).Index.Should().Be(restore ? (ushort)0x1011 : (ushort)0x2001);
        Result(rig).Error.Should().BeOfType<SdoAbortException>();
        peer.ObjectDictionary.ReadRaw(0x2000, 0).Should().Equal(restore ? Array.Empty<byte>() : new byte[] { 42 });
        Started(rig, Slave).Should().BeFalse();
        rig.Log.Snapshot().Should().NotContain(f => IsNmt(f, NmtCommand.ResetNode, Slave));
    }

    [Fact]
    public async Task Registered_Plan_Is_Copied_And_Live_Replacement_Restarts_Boot()
    {
        using var rig = OpenMaster(DownloadOptions());
        using var peer = rig.OpenPeerNode(Slave);
        peer.ObjectDictionary.AddDomain(0x2000, 0, Array.Empty<byte>());
        ConfigureDownload(rig);
        var data = BootDcf((0x2000, 0, new byte[] { 17 }));
        CanOpenNodeExtensions.SetSlaveBootConfiguration(rig.Node, Slave, data);
        data[data.Length - 1] = 99;
        await ElectActive(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Started(rig, Slave), 300, "original registered bytes");
        peer.ObjectDictionary.ReadRaw(0x2000, 0).Should().Equal(17);
        rig.Log.Clear();
        CanOpenNodeExtensions.SetSlaveBootConfiguration(rig.Node, Slave, BootDcf((0x2000, 0, new byte[] { 18 })));
        TransmitHeartbeat(rig.Peer, Slave, 5);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Started(rig, Slave), 300, "live replacement applies in a fresh generation");
        peer.ObjectDictionary.ReadRaw(0x2000, 0).Should().Equal(18);
    }

    [Fact]
    public void Boot_Configuration_Registration_Validates_Arguments_And_Opt_In()
    {
        using var rig = OpenMaster();
        Action disabled = () => CanOpenNodeExtensions.SetSlaveBootConfiguration(rig.Node, Slave, new byte[4]);
        disabled.Should().Throw<InvalidOperationException>();
        Action missing = () => CanOpenNodeExtensions.SetSlaveBootConfiguration(null!, Slave, new byte[4]);
        missing.Should().Throw<ArgumentNullException>();
        Action zero = () => CanOpenNodeExtensions.SetSlaveBootConfiguration(rig.Node, 0, new byte[4]);
        zero.Should().Throw<ArgumentOutOfRangeException>();
        rig.Node.Dispose();
        disabled.Should().Throw<ObjectDisposedException>();
        new CanOpenNodeOptions().EnableBootConfigurationDownload.Should().BeFalse();
        DownloadOptions().With().EnableBootConfigurationDownload.Should().BeTrue();
    }
}
