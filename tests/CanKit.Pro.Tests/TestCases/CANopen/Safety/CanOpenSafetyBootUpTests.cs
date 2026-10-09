using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Safety;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;
using static CanKit.Pro.Tests.TestCases.CANopen.FlyingMasterRig;

namespace CanKit.Pro.Tests.TestCases.CANopen.Safety;

/// <summary>CiA DSP 304 V1.0 §8.3.1 step D in the boot-up manager (CiA 302-2): an assigned slave
/// whose bound DCF declares SRDOs is verified over SDO before the master sends NMT Start. The
/// slave is a real device node on the rig's peer bus, so it answers the SDO uploads.</summary>
public class CanOpenSafetyBootUpTests : IClassFixture<VirtualAdapterFixture>
{
    private const byte Slave = 0x05; // safety.dcf is commissioned for node 5

    private static CanOpenDeviceDescription SlaveDcf(bool validChecksum)
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestCases", "CANopen", "Fixtures", "safety.dcf"));
        var parameter = new SrdoCommunicationParameter(SrdoDirection.Transmit, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(20), 0x109, 0x10A);
        var mapping = new SrdoMapping().Add(0x2000, 0x00, 16).Add(0x2001, 0x00, 8);
        ushort crc = SrdoCrc.Compute(parameter, mapping);
        return CanOpenDeviceDescription.ParseDcf(text.Replace("ParameterValue=0x0000", $"ParameterValue=0x{(validChecksum ? crc : (ushort)(crc ^ 1)):X4}"));
    }

    /// <summary>The slave: a device opened from the DCF (its 13FFh is what the file says), on
    /// the rig's peer bus, in Pre-Operational.</summary>
    private static ICanOpenNode OpenSlave(ICanBus peerBus, bool validChecksum)
        => CanOpen.OpenNode(peerBus, SlaveDcf(validChecksum), new CanOpenNodeOptions { WritableCommunicationParameters = true });

    private static List<(FlyingMasterSignal Signal, byte? Other)> Record(CanOpenNode master)
    {
        var signals = new List<(FlyingMasterSignal, byte?)>();
        master.FlyingMasterChanged += (_, e) => { lock (signals) signals.Add((e.Signal, e.OtherNodeId)); };
        return signals;
    }

    [Fact]
    public async Task A_Verified_Safety_Slave_Is_Started()
    {
        using var rig = OpenMaster();
        using var slave = OpenSlave(rig.Peer, validChecksum: true);
        rig.Node.BindPeerDeviceDescription(Slave, SlaveDcf(validChecksum: true));
        var od = rig.Node.ObjectDictionary;
        od.WriteUnsigned(Startup, 0x00, SuppressSelfStart);
        od.WriteUnsigned(0x1F81, Slave, Assigned | BootSlave);
        Tighten(rig.Node);
        var signals = Record(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null, () => rig.Node.FlyingMasterRole == FlyingMasterRole.Active, 800, "the master is active");
        await UntilAsync(rig.Clock, rig.Witness, null, () => slave.State == NmtState.Operational, 2000, "the slave was verified and started");
        rig.Log.Snapshot().Should().Contain(f => IsNmt(f, NmtCommand.Start, Slave));
        signals.Should().NotContain(s => s.Signal == FlyingMasterSignal.SlaveSafetyConfigurationInvalid);
        rig.Log.Snapshot().Should().Contain(f => f.Id == 0x600u + Slave, "step D read the slave's safety objects over SDO");
    }

    [Fact]
    public async Task An_Unverified_Optional_Slave_Is_Skipped_And_Signalled()
    {
        using var rig = OpenMaster();
        using var slave = OpenSlave(rig.Peer, validChecksum: false);
        rig.Node.BindPeerDeviceDescription(Slave, SlaveDcf(validChecksum: true));
        var od = rig.Node.ObjectDictionary;
        od.WriteUnsigned(0x1F81, Slave, Assigned | BootSlave);
        Tighten(rig.Node);
        var signals = Record(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null, () => { lock (signals) return signals.Any(s => s.Signal == FlyingMasterSignal.SlaveSafetyConfigurationInvalid); }, 2000, "the verification failed");
        signals.Should().Contain((FlyingMasterSignal.SlaveSafetyConfigurationInvalid, (byte?)Slave));
        await QuiesceAsync(rig.Witness, null);
        rig.Log.Snapshot().Should().NotContain(f => IsNmt(f, NmtCommand.Start, Slave), "§8.3.1 D: no start without a verified configuration");
        rig.Node.State.Should().Be(NmtState.Operational, "an optional slave does not hold the master");
        slave.State.Should().Be(NmtState.PreOperational);
    }

    [Fact]
    public async Task An_Unverified_Mandatory_Slave_Halts_The_Boot()
    {
        using var rig = OpenMaster();
        using var slave = OpenSlave(rig.Peer, validChecksum: false);
        rig.Node.BindPeerDeviceDescription(Slave, SlaveDcf(validChecksum: true));
        var od = rig.Node.ObjectDictionary;
        od.WriteUnsigned(0x1F81, Slave, Assigned | BootSlave | MandatorySlave);
        Tighten(rig.Node);
        var signals = Record(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null, () => { lock (signals) return signals.Any(s => s.Signal == FlyingMasterSignal.SlaveSafetyConfigurationInvalid); }, 2000, "the verification failed");
        await QuiesceAsync(rig.Witness, null);
        rig.Node.State.Should().NotBe(NmtState.Operational, "a mandatory slave that fails step D halts the boot like a boot timeout");
        rig.Log.Snapshot().Should().NotContain(f => IsNmt(f, NmtCommand.Start, Slave));
        rig.Log.Snapshot().Should().Contain(f => IsNmt(f, NmtCommand.ResetNode, Slave), "1F80h bits 4 and 6 clear: Reset Node to the failing slave");
    }

    [Fact]
    public async Task A_Simultaneous_Start_Waits_For_The_Verification()
    {
        using var rig = OpenMaster();
        using var slave = OpenSlave(rig.Peer, validChecksum: true);
        rig.Node.BindPeerDeviceDescription(Slave, SlaveDcf(validChecksum: true));
        var od = rig.Node.ObjectDictionary;
        od.WriteUnsigned(Startup, 0x00, 0x02); // bit 1: one NMT Start to all
        od.WriteUnsigned(0x1F81, Slave, Assigned | BootSlave | MandatorySlave);
        Tighten(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null, () => slave.State == NmtState.Operational, 2000, "the broadcast start went out after the verification");
        var log = rig.Log.Snapshot();
        int sdo = log.FindIndex(f => f.Id == 0x600u + Slave);
        int start = log.FindIndex(f => IsNmt(f, NmtCommand.Start, 0));
        sdo.Should().BeGreaterThanOrEqualTo(0);
        start.Should().BeGreaterThan(sdo, "the broadcast waits for every running verification");
    }

    [Fact]
    public async Task A_Slave_Without_Srdo_Records_Is_Booted_As_Before()
    {
        using var rig = OpenMaster();
        var plain = CanOpenDeviceDescription.Load(Path.Combine(AppContext.BaseDirectory, "TestCases", "CANopen", "Fixtures", "device.dcf"));
        using var slave = CanOpen.OpenNode(rig.Peer, plain);
        rig.Node.BindPeerDeviceDescription(Slave, plain);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F81, Slave, Assigned | BootSlave);
        Tighten(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null, () => slave.State == NmtState.Operational, 2000, "started");
        rig.Log.Snapshot().Should().NotContain(f => f.Id == 0x600u + Slave, "no SDO: the file declares no SRDO");
    }
}
