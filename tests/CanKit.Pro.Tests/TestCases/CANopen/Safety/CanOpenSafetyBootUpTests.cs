using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Safety;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;
using static CanKit.Pro.Tests.TestCases.CANopen.FlyingMasterRig;

namespace CanKit.Pro.Tests.TestCases.CANopen.Safety;

/// <summary>CiA DSP 304 V1.0 §8.3.1 step D in the boot-up manager (CiA 302-2): an assigned slave
/// whose bound DCF declares SRDOs is verified over SDO before the master sends NMT Start. The
/// slave is a real device node on the rig's peer bus, so it answers the SDO uploads
/// (FR-CO-045).</summary>
public class CanOpenSafetyBootUpTests : IClassFixture<VirtualAdapterFixture>
{
    private const byte Slave = 0x05; // safety.dcf is commissioned for node 5
    private const byte Other = 0x06;
    private const uint KeepAlive = 0x10; // 1F81h bit 4: the master does not reset the slave

    private static CanOpenDeviceDescription SlaveDcf(bool validChecksum)
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestCases", "CANopen", "Fixtures", "safety.dcf"));
        var parameter = new SrdoCommunicationParameter(SrdoDirection.Transmit, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(20), 0x109, 0x10A);
        var mapping = new SrdoMapping().Add(0x2000, 0x00, 16).Add(0x2001, 0x00, 8);
        ushort crc = SrdoCrc.Compute(parameter, mapping);
        return CanOpenDeviceDescription.ParseDcf(text.Replace("ParameterValue=0x0000", $"ParameterValue=0x{(validChecksum ? crc : (ushort)(crc ^ 1)):X4}"));
    }

    private static CanOpenDeviceDescription PlainDcf()
        => CanOpenDeviceDescription.Load(Path.Combine(AppContext.BaseDirectory, "TestCases", "CANopen", "Fixtures", "device.dcf"));

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

    private static bool Has(List<(FlyingMasterSignal Signal, byte? Other)> signals, FlyingMasterSignal signal)
    {
        lock (signals) return signals.Any(s => s.Signal == signal);
    }

    private static bool Logged(MasterRig rig, NmtCommand command, byte target)
        => rig.Log.Snapshot().Any(f => IsNmt(f, command, target));

    /// <summary>Client requests to the slave, not counting the abort a cancelled transfer sends.</summary>
    private static int SdoRequests(MasterRig rig) => rig.Log.Snapshot().Count(f => f.Id == 0x600u + Slave && f.Data[0] != 0x80);

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
        await UntilAsync(rig.Clock, rig.Witness, null, () => Has(signals, FlyingMasterSignal.SlaveSafetyConfigurationInvalid), 2000, "the verification failed");
        signals.Should().Contain((FlyingMasterSignal.SlaveSafetyConfigurationInvalid, (byte?)Slave));

        // Ordering witness: an Enter Pre-Operational requested after the signal goes through the
        // same FIFO NMT queue, so a Start the failed verification had queued is ahead of it.
        od.WriteUnsigned(0x1F82, Slave, (byte)NmtState.PreOperational);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Logged(rig, NmtCommand.EnterPreOperational, Slave), 200, "the witness request was sent");
        rig.Log.Snapshot().Should().NotContain(f => IsNmt(f, NmtCommand.Start, Slave), "§8.3.1 D: no start without a verified configuration");
        rig.Node.State.Should().Be(NmtState.Operational, "an optional slave does not hold the master");
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
        await UntilAsync(rig.Clock, rig.Witness, null, () => Has(signals, FlyingMasterSignal.SlaveSafetyConfigurationInvalid), 2000, "the verification failed");
        await UntilAsync(rig.Clock, rig.Witness, null, () => Logged(rig, NmtCommand.ResetNode, Slave), 200,
            "1F80h bits 4 and 6 clear: Reset Node to the failing slave");
        var log = rig.Log.Snapshot(); // the reaction is on the wire, so a Start queued before it is too
        rig.Node.State.Should().NotBe(NmtState.Operational, "a mandatory slave that fails step D halts the boot like a boot timeout");
        log.Should().NotContain(f => IsNmt(f, NmtCommand.Start, Slave));
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
        await UntilAsync(rig.Clock, rig.Witness, null, () => slave.State == NmtState.Operational, 2000, "the start went out after the verification");
        var log = rig.Log.Snapshot();
        int lastReply = log.FindLastIndex(f => f.Id == 0x580u + Slave);
        int start = log.FindIndex(f => IsNmt(f, NmtCommand.Start, Slave));
        lastReply.Should().BeGreaterThanOrEqualTo(0);
        start.Should().BeGreaterThan(lastReply, "the start waits until the verification has its last reply");
        log.Should().NotContain(f => IsNmt(f, NmtCommand.Start, 0), "with a safety slave assigned the master starts each slave on its own");
    }

    /// <summary>1F81h bit 4: a keep-alive slave is not reset, so it may sit in Pre-Operational
    /// without having announced to this master when the start moment comes. A broadcast Start
    /// would reach it before step D (§8.3.1: "before NMT Start"); the master therefore sends
    /// none while a safety slave is assigned, and starts that slave on its own once it has
    /// announced and verified.</summary>
    [Fact]
    public async Task A_Simultaneous_Start_Does_Not_Reach_A_Safety_Slave_Before_Its_Verification()
    {
        using var rig = OpenMaster();
        using var slave = OpenSlave(rig.Peer, validChecksum: true);
        rig.Node.BindPeerDeviceDescription(Slave, SlaveDcf(validChecksum: true));
        var od = rig.Node.ObjectDictionary;
        od.WriteUnsigned(Startup, 0x00, 0x02); // bit 1: simultaneous start; no mandatory slave
        od.WriteUnsigned(0x1F81, Slave, Assigned | BootSlave | KeepAlive);
        Tighten(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null, () => rig.Node.State == NmtState.Operational, 2000, "the start moment passed: the master started itself");

        // Ordering witness: a request queued after the start moment goes through the same FIFO
        // NMT queue, so a Start the moment had queued is ahead of it on the wire.
        od.WriteUnsigned(0x1F82, Slave, (byte)NmtState.PreOperational);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Logged(rig, NmtCommand.EnterPreOperational, Slave), 200, "the witness request was sent");
        rig.Log.Snapshot().Should().NotContain(f => IsNmt(f, NmtCommand.Start, 0) || IsNmt(f, NmtCommand.Start, Slave),
            "nothing may start the unverified safety slave");
        slave.State.Should().Be(NmtState.PreOperational);

        TransmitHeartbeat(rig.Peer, Slave, (byte)NmtState.PreOperational); // the slave announces
        await UntilAsync(rig.Clock, rig.Witness, null, () => slave.State == NmtState.Operational, 2000, "verified, then started");
        var log = rig.Log.Snapshot();
        int lastReply = log.FindLastIndex(f => f.Id == 0x580u + Slave);
        lastReply.Should().BeGreaterThanOrEqualTo(0, "step D read the slave");
        log.FindIndex(f => IsNmt(f, NmtCommand.Start, Slave)).Should().BeGreaterThan(lastReply);
        log.Should().NotContain(f => IsNmt(f, NmtCommand.Start, 0));
    }

    /// <summary>A bound description can be edited in place, and the peer-SDO gate reads it live:
    /// step D must read it live too. The first boot verifies and starts the slave; the bound
    /// instance is then edited (SRDO 1's refresh time 30 → 40 ms) and a second boot verifies
    /// against the new content, which the slave no longer matches.</summary>
    [Fact]
    public async Task Step_D_Uses_The_Bound_Description_As_It_Is_Now()
    {
        using var rig = OpenMaster();
        using var slave = OpenSlave(rig.Peer, validChecksum: true);
        var dcf = SlaveDcf(validChecksum: true);
        rig.Node.BindPeerDeviceDescription(Slave, dcf);
        var od = rig.Node.ObjectDictionary;
        od.WriteUnsigned(Startup, 0x00, SuppressSelfStart);
        od.WriteUnsigned(0x1F81, Slave, Assigned | BootSlave);
        Tighten(rig.Node);
        var signals = Record(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null, () => slave.State == NmtState.Operational, 2000, "the first boot verified and started the slave");

        dcf.Objects.Objects[0x1301].SubObjects[0x02].ParameterValue = "40";
        PeerSafetyConfiguration.FromDeviceDescription(dcf, Slave).Srdos[1].Parameter.RefreshOrSafeguardCycleTime
            .Should().Be(TimeSpan.FromMilliseconds(40), "the edit is in the bound instance");
        od.WriteUnsigned(0x1F81, Slave, Assigned | BootSlave); // a live edit boots the network again
        await UntilAsync(rig.Clock, rig.Witness, null, () => Has(signals, FlyingMasterSignal.SlaveSafetyConfigurationInvalid), 2000,
            "the second step D compares with 40 ms, and the slave holds 30 ms");
        signals.Should().Contain((FlyingMasterSignal.SlaveSafetyConfigurationInvalid, (byte?)Slave));
    }

    /// <summary>The simultaneous start keeps its moment — every mandatory slave seen, no
    /// verification running — but with a safety slave assigned it is one Start per slave: the
    /// non-safety slave that has been waiting is started at that moment, the safety slave only
    /// after its verification, and no Start goes to node 0.</summary>
    [Fact]
    public async Task A_Simultaneous_Start_With_A_Safety_Slave_Starts_Each_Slave_On_Its_Own()
    {
        using var rig = OpenMaster();
        using var slave = OpenSlave(rig.Peer, validChecksum: true);
        using var other = CanOpen.OpenNode(rig.Peer, Other);
        rig.Node.BindPeerDeviceDescription(Slave, SlaveDcf(validChecksum: true));
        var od = rig.Node.ObjectDictionary;
        od.WriteUnsigned(Startup, 0x00, 0x02); // bit 1: simultaneous start
        od.WriteUnsigned(0x1F81, Slave, Assigned | BootSlave | MandatorySlave);
        od.WriteUnsigned(0x1F81, Other, Assigned | BootSlave | MandatorySlave);
        Tighten(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null, () => slave.State == NmtState.Operational && other.State == NmtState.Operational, 2000,
            "both slaves started");
        var log = rig.Log.Snapshot();
        int lastReply = log.FindLastIndex(f => f.Id == 0x580u + Slave);
        lastReply.Should().BeGreaterThanOrEqualTo(0, "step D read the safety slave");
        log.FindIndex(f => IsNmt(f, NmtCommand.Start, Other)).Should().BeGreaterThan(lastReply,
            "the non-safety slave waits for the start moment, which waits for the verification");
        log.FindIndex(f => IsNmt(f, NmtCommand.Start, Slave)).Should().BeGreaterThan(lastReply);
        log.Should().NotContain(f => IsNmt(f, NmtCommand.Start, 0), "no Start to node 0 while a safety slave is assigned");
    }

    /// <summary>A slave whose DCF leaves SRDO 2 out (SRDOs 1 and 3, checksums valid): the device
    /// holds 1302h deleted, and step D with the master bound to the same file reaches it and
    /// verifies the slave, which is then started. The device is opened without
    /// WritableCommunicationParameters: step D only reads.</summary>
    [Fact]
    public async Task A_Slave_Whose_File_Leaves_An_Srdo_Number_Out_Is_Verified_And_Started()
    {
        var text = CanOpenSafetyDeviceDescriptionTests.GappedSafetyDcfText();
        var expected = PeerSafetyConfiguration.FromDeviceDescription(CanOpenDeviceDescription.ParseDcf(text), Slave);
        expected.Srdos.Keys.Should().BeEquivalentTo(new[] { 1, 3 });
        text = text.Replace("ParameterValue=0x0000", $"ParameterValue=0x{SrdoCrc.Compute(expected.Srdos[1].Parameter, expected.Srdos[1].Mapping):X4}");
        int sub3 = text.IndexOf("[13FFsub3]", StringComparison.Ordinal);
        int value = text.IndexOf("DefaultValue=0", sub3, StringComparison.Ordinal);
        text = text[..value] + $"DefaultValue=0x{SrdoCrc.Compute(expected.Srdos[3].Parameter, expected.Srdos[3].Mapping):X4}" + text[(value + "DefaultValue=0".Length)..];
        var dcf = CanOpenDeviceDescription.ParseDcf(text);

        using var rig = OpenMaster();
        using var slave = CanOpen.OpenNode(rig.Peer, dcf);
        SrdoRecords.IsConfigurationValid(slave.ObjectDictionary, 3).Should().BeTrue("the file's checksums match its records");
        slave.ObjectDictionary.ReadUnsigned(0x1302, 1).Should().Be(0u);
        rig.Node.BindPeerDeviceDescription(Slave, dcf);
        var od = rig.Node.ObjectDictionary;
        od.WriteUnsigned(Startup, 0x00, SuppressSelfStart);
        od.WriteUnsigned(0x1F81, Slave, Assigned | BootSlave);
        Tighten(rig.Node);
        var signals = Record(rig.Node);
        var failures = new List<Exception>();
        rig.Node.BackgroundExceptionOccurred += (_, e) => { lock (failures) failures.Add(e); };
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null, () => slave.State == NmtState.Operational || Has(signals, FlyingMasterSignal.SlaveSafetyConfigurationInvalid),
            2000, "step D decided");
        lock (failures) failures.Should().BeEmpty();
        signals.Should().NotContain(s => s.Signal == FlyingMasterSignal.SlaveSafetyConfigurationInvalid);
        slave.State.Should().Be(NmtState.Operational);
        rig.Log.Snapshot().Should().Contain(f => f.Id == 0x600u + Slave && f.Data[1] == 0x02 && f.Data[2] == 0x13, "step D read the undeclared 1302h");
    }

    [Fact]
    public async Task A_Slave_Without_Srdo_Records_Is_Booted_As_Before()
    {
        using var rig = OpenMaster();
        var plain = PlainDcf();
        using var slave = CanOpen.OpenNode(rig.Peer, plain);
        rig.Node.BindPeerDeviceDescription(Slave, plain);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F81, Slave, Assigned | BootSlave);
        Tighten(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null, () => slave.State == NmtState.Operational, 2000, "started");
        rig.Log.Snapshot().Should().NotContain(f => f.Id == 0x600u + Slave, "no SDO: the file declares no SRDO");
    }

    [Fact]
    public async Task A_Safety_Halt_Is_Not_Followed_By_The_Boot_Timeout_Reaction()
    {
        using var rig = OpenMaster();
        using var slave = OpenSlave(rig.Peer, validChecksum: false);
        rig.Node.BindPeerDeviceDescription(Slave, SlaveDcf(validChecksum: true));
        var od = rig.Node.ObjectDictionary;
        od.WriteUnsigned(0x1F81, Slave, Assigned | BootSlave | MandatorySlave);
        od.WriteUnsigned(0x1F81, Other, Assigned | BootSlave | MandatorySlave); // never announces
        od.WriteUnsigned(0x1F89, 0x00, 10000);
        Tighten(rig.Node);
        var signals = Record(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null, () => rig.Node.FlyingMasterRole == FlyingMasterRole.Active, 800, "the master is active");
        // 1F89h is armed when the master becomes active; this budget ends half-way through it.
        await UntilAsync(rig.Clock, rig.Witness, null, () => Has(signals, FlyingMasterSignal.SlaveSafetyConfigurationInvalid), 5000,
            "step D failed before 1F89h elapsed");

        for (int i = 0; i < 11; i++)
            await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(1000)); // well past 1F89h

        var log = rig.Log.Snapshot();
        log.Count(f => IsNmt(f, NmtCommand.ResetNode, Slave)).Should().Be(1, "one boot-error reaction per boot");
        log.Should().NotContain(f => IsNmt(f, NmtCommand.ResetNode, Other), "the halted boot does not time out as well");
        Has(signals, FlyingMasterSignal.SlaveBootTimeout).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_Verification_Ending_While_A_Forced_Reset_Is_Held_Acts_Only_After_It(bool resetConfirmed)
    {
        var (rig, gate) = OpenGatedMaster();
        using var _ = rig;
        using var slave = OpenSlave(rig.Peer, validChecksum: true);
        rig.Node.BindPeerDeviceDescription(Slave, SlaveDcf(validChecksum: true));
        var od = rig.Node.ObjectDictionary;
        od.WriteUnsigned(Startup, 0x00, SuppressSelfStart);
        od.WriteUnsigned(0x1F81, Slave, Assigned | BootSlave);
        Tighten(rig.Node);
        var signals = Record(rig.Node);
        gate.PassResets = 1; // the cold boot's broadcast passes, the forced one is held
        gate.HoldSdo = true;
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null, () => gate.SdoHeld >= 1, 2000, "step D began and its first request is held");

        rig.Peer.Transmit(CanFrame.Classic(unchecked((int)CanOpenCobId.FlyingMasterForce), Array.Empty<byte>(), isExtendedFrame: false));
        await gate.ResetEntered.WaitAsync(TimeSpan.FromSeconds(5));

        gate.HoldSdo = false;
        gate.ReleaseSdo(0, transmit: true);
        await SettleUntilAsync(rig, () => SlaveFlag(rig.Node, "_slaveVerified"), "the verification ended inside the hold");
        await QuiesceAsync(rig.Witness, null);
        int requests = SdoRequests(rig);
        rig.Log.Snapshot().Should().NotContain(f => IsNmt(f, NmtCommand.Start, Slave) || IsNmt(f, NmtCommand.ResetNode, Slave));
        Has(signals, FlyingMasterSignal.SlaveSafetyConfigurationInvalid).Should().BeFalse();

        if (resetConfirmed)
        {
            // The reset cancels this boot: the verified result is gone with it, and nothing was
            // queued behind the reset during the hold.
            gate.ReleaseReset(confirm: true);
            await SettleUntilAsync(rig, () => rig.Node.FlyingMasterRole == FlyingMasterRole.Delaying, "the confirmed reset restarts the election");
            await QuiesceAsync(rig.Witness, null);
            rig.Log.Snapshot().Should().NotContain(f => IsNmt(f, NmtCommand.Start, Slave), "nothing was started in the hold");
        }
        else
        {
            // The reset never left: ResumeHeldBoot starts the slave from the result kept in the hold.
            gate.ReleaseReset(confirm: false);
            await SettleUntilAsync(rig, () => Logged(rig, NmtCommand.Start, Slave), "the resumed boot starts the verified slave");
            SdoRequests(rig).Should().Be(requests, "the result from the hold is kept, not verified again");
        }
    }

    [Fact]
    public async Task A_Verification_Failing_While_A_Forced_Reset_Is_Held_Is_Repeated_After_It()
    {
        var (rig, gate) = OpenGatedMaster();
        using var _ = rig;
        using var slave = OpenSlave(rig.Peer, validChecksum: false);
        rig.Node.BindPeerDeviceDescription(Slave, SlaveDcf(validChecksum: true));
        var od = rig.Node.ObjectDictionary;
        od.WriteUnsigned(Startup, 0x00, SuppressSelfStart);
        od.WriteUnsigned(0x1F81, Slave, Assigned | BootSlave);
        Tighten(rig.Node);
        var signals = Record(rig.Node);
        gate.PassResets = 1; // the cold boot's broadcast passes, the forced one is held
        gate.HoldSdo = true;
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null, () => gate.SdoHeld >= 1, 2000, "step D began and its first request is held");

        rig.Peer.Transmit(CanFrame.Classic(unchecked((int)CanOpenCobId.FlyingMasterForce), Array.Empty<byte>(), isExtendedFrame: false));
        await gate.ResetEntered.WaitAsync(TimeSpan.FromSeconds(5));

        gate.HoldSdo = false;
        gate.ReleaseSdo(0, transmit: true);
        await SettleUntilAsync(rig, () => !SlaveFlag(rig.Node, "_slaveVerifying"), "the failed verification ended inside the hold");
        await QuiesceAsync(rig.Witness, null);
        rig.Log.Snapshot().Should().NotContain(f => IsNmt(f, NmtCommand.Start, Slave) || IsNmt(f, NmtCommand.ResetNode, Slave),
            "nothing is started or reset in the hold");
        Has(signals, FlyingMasterSignal.SlaveSafetyConfigurationInvalid).Should().BeFalse("nothing is signalled in the hold");

        // The reset never left: ResumeHeldBoot finds the slave unverified and verifies it again.
        int resumed = rig.Log.Snapshot().Count;
        gate.ReleaseReset(confirm: false);
        await SettleUntilAsync(rig, () => Has(signals, FlyingMasterSignal.SlaveSafetyConfigurationInvalid) || Logged(rig, NmtCommand.Start, Slave),
            "the resumed boot decided about the slave");
        var log = rig.Log.Snapshot();
        log.Should().NotContain(f => IsNmt(f, NmtCommand.Start, Slave), "a failed result from the hold does not start the slave");
        log.Skip(resumed).Should().Contain(f => f.Id == 0x600u + Slave && f.Data[0] != 0x80, "a second step-D request after the resume");
        Has(signals, FlyingMasterSignal.SlaveSafetyConfigurationInvalid).Should().BeTrue("the repeated verification failed");
    }

    [Fact]
    public async Task A_Result_From_A_Cancelled_Boot_Is_Not_Taken_For_The_Newer_Verification()
    {
        var (rig, gate) = OpenGatedMaster();
        using var _ = rig;
        using var slave = OpenSlave(rig.Peer, validChecksum: true);
        rig.Node.BindPeerDeviceDescription(Slave, SlaveDcf(validChecksum: true));
        var od = rig.Node.ObjectDictionary;
        od.WriteUnsigned(Startup, 0x00, SuppressSelfStart);
        od.WriteUnsigned(0x1F81, Slave, Assigned | BootSlave);
        Tighten(rig.Node);
        gate.HoldSdo = true;
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null, () => gate.SdoHeld >= 1, 2000, "the first verification is pending");
        int stale = await OnActorAsync(rig.Node, () => (int)Field(rig.Node, "_bootGeneration"));

        od.WriteUnsigned(0x1F81, Slave, Assigned | BootSlave); // a live edit boots the network again
        await SettleUntilAsync(rig, () => gate.SdoHeld >= 2, "the new boot's verification is pending");
        (await OnActorAsync(rig.Node, () => (int)Field(rig.Node, "_bootGeneration"))).Should().BeGreaterThan(stale);

        // A cancelled verification throws and posts nothing; a stale post exists only when its
        // last reply was handled before CancelBootUp ran, an actor-turn window the rig cannot place
        // a boot into. The post is therefore delivered here as the actor would have run it.
        var onSlaveVerified = typeof(CanOpenNode).GetMethod("OnSlaveVerified", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await rig.Node.PostToActorAsync(() => onSlaveVerified.Invoke(rig.Node, new object?[] { Slave, stale, true, null }));
        await QuiesceAsync(rig.Witness, null);
        rig.Log.Snapshot().Should().NotContain(f => IsNmt(f, NmtCommand.Start, Slave), "the old boot's result does not start the slave");
        SlaveFlag(rig.Node, "_slaveVerifying").Should().BeTrue("the newer verification is still the one that decides");

        gate.ReleaseSdo(0, transmit: false); // the cancelled boot's request never reaches the slave
        gate.HoldSdo = false;
        gate.ReleaseSdo(1, transmit: true);
        await UntilAsync(rig.Clock, rig.Witness, null, () => slave.State == NmtState.Operational, 2000, "the newer verification decided");
    }

    [Fact]
    public async Task Losing_The_Role_Cancels_A_Pending_Verification()
    {
        var (rig, gate) = OpenGatedMaster();
        using var _ = rig;
        using var slave = OpenSlave(rig.Peer, validChecksum: true);
        rig.Node.BindPeerDeviceDescription(Slave, SlaveDcf(validChecksum: true));
        var od = rig.Node.ObjectDictionary;
        od.WriteUnsigned(0x1F81, Slave, Assigned | BootSlave);
        Tighten(rig.Node);
        var signals = Record(rig.Node);
        gate.HoldSdo = true;
        rig.Node.StartFlyingMaster(1, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null, () => gate.SdoHeld >= 1, 2000, "the verification is pending");

        rig.Peer.Transmit(CanFrame.Classic(unchecked((int)CanOpenCobId.FlyingMasterClaim), new byte[] { 0, 0x30 }, isExtendedFrame: false));
        await SettleUntilAsync(rig, () => rig.Node.FlyingMasterRole == FlyingMasterRole.Standby, "a better claim deposes the master");

        gate.HoldSdo = false;
        gate.ReleaseSdo(0, transmit: true);
        for (int i = 0; i < 5; i++) await QuiesceAsync(rig.Witness, null);

        SdoRequests(rig).Should().Be(1, "the cancelled verification sends nothing after its pending request");
        rig.Log.Snapshot().Should().NotContain(f => IsNmt(f, NmtCommand.Start, Slave));
        Has(signals, FlyingMasterSignal.SlaveSafetyConfigurationInvalid).Should().BeFalse("a cancelled verification has no outcome");
        (await OnActorAsync(rig.Node, () => ((bool[])Field(rig.Node, "_slaveVerifying")).Any(v => v))).Should().BeFalse();
    }

    [Fact]
    public async Task A_Verification_That_Throws_Is_Reported_And_Signalled()
    {
        using var rig = OpenMaster();
        using var slave = CanOpen.OpenNode(rig.Peer, PlainDcf()); // node 5 without 13FFh: the upload aborts
        rig.Node.BindPeerDeviceDescription(Slave, SlaveDcf(validChecksum: true));
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F81, Slave, Assigned | BootSlave);
        Tighten(rig.Node);
        var signals = Record(rig.Node);
        var failures = new List<Exception>();
        rig.Node.BackgroundExceptionOccurred += (_, e) => { lock (failures) failures.Add(e); };
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Has(signals, FlyingMasterSignal.SlaveSafetyConfigurationInvalid), 2000, "the verification failed");
        signals.Should().Contain((FlyingMasterSignal.SlaveSafetyConfigurationInvalid, (byte?)Slave));
        lock (failures) failures.Should().Contain(e => e is SdoAbortException, "the abort is reported, not swallowed");
        slave.State.Should().NotBe(NmtState.Operational);
    }

    [Fact]
    public async Task A_Simultaneous_Start_Halts_On_A_Failed_Optional_Slave()
    {
        using var rig = OpenMaster();
        using var slave = OpenSlave(rig.Peer, validChecksum: false);
        rig.Node.BindPeerDeviceDescription(Slave, SlaveDcf(validChecksum: true));
        var od = rig.Node.ObjectDictionary;
        od.WriteUnsigned(Startup, 0x00, 0x02); // bit 1: one NMT Start to all
        od.WriteUnsigned(0x1F81, Slave, Assigned | BootSlave);
        od.WriteUnsigned(0x1F81, Other, Assigned | BootSlave | MandatorySlave); // holds the broadcast until it announces
        Tighten(rig.Node);
        var signals = Record(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null, () => SdoRequests(rig) > 0, 2000, "step D began");
        TransmitHeartbeat(rig.Peer, Other, 0x00); // the last mandatory slave announces: only step D holds the broadcast now
        await UntilAsync(rig.Clock, rig.Witness, null, () => Has(signals, FlyingMasterSignal.SlaveSafetyConfigurationInvalid), 2000, "the verification failed");
        await UntilAsync(rig.Clock, rig.Witness, null, () => Logged(rig, NmtCommand.ResetNode, Slave), 200, "the boot-error reaction");

        var log = rig.Log.Snapshot();
        log.Should().NotContain(f => IsNmt(f, NmtCommand.Start, 0), "a broadcast cannot leave the failed slave out");
        log.Should().NotContain(f => IsNmt(f, NmtCommand.Start, Slave));
        rig.Node.State.Should().NotBe(NmtState.Operational);
    }

    [Theory]
    [InlineData(0x10u, NmtCommand.ResetNode)]
    [InlineData(0x40u, NmtCommand.Stop)]
    public async Task A_Failed_Mandatory_Slave_Applies_The_1F80h_Error_Reaction(uint startup, NmtCommand toAll)
    {
        using var rig = OpenMaster();
        using var slave = OpenSlave(rig.Peer, validChecksum: false);
        rig.Node.BindPeerDeviceDescription(Slave, SlaveDcf(validChecksum: true));
        var od = rig.Node.ObjectDictionary;
        od.WriteUnsigned(Startup, 0x00, startup);
        od.WriteUnsigned(0x1F81, Slave, Assigned | BootSlave | MandatorySlave);
        od.WriteUnsigned(0x1F81, Other, Assigned);
        Tighten(rig.Node);
        var signals = Record(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Has(signals, FlyingMasterSignal.SlaveSafetyConfigurationInvalid), 2000, "the verification failed");
        await UntilAsync(rig.Clock, rig.Witness, null, () => Logged(rig, toAll, Slave) && Logged(rig, toAll, Other), 200,
            "the reaction goes to every assigned slave");

        var log = rig.Log.Snapshot();
        log.Count(f => IsNmt(f, toAll, Slave)).Should().Be(1, "the command to all reaches the failed slave once");
        log.Count(f => IsNmt(f, NmtCommand.ResetNode, Slave)).Should().Be(toAll == NmtCommand.ResetNode ? 1 : 0,
            "no extra per-slave Reset Node under bit 4 or 6");
        log.Should().NotContain(f => IsNmt(f, NmtCommand.Start, Slave));
        rig.Node.State.Should().NotBe(NmtState.Operational);
    }

    private static (MasterRig Rig, SafetyBootGate Gate) OpenGatedMaster()
    {
        var session = NewSession();
        var nodeBus = Open(session, 0);
        var peer = Open(session, 1);
        var clock = new ManualTimeSource();
        var gate = new SafetyBootGate(new CanBusService(nodeBus));
        var node = new CanOpenNode(gate, LeftId, new CanOpenNodeOptions(), ownsService: true, timeSource: clock);
        return (new MasterRig(clock, node, new ActorWitness(node, peer, WitnessForLeft), new FrameLog(nodeBus, peer), peer, nodeBus), gate);
    }

    /// <summary>Real-time settling without moving the clock: no timer becomes due, so only the
    /// work already queued (SDO replies, a released send) runs.</summary>
    private static async Task SettleUntilAsync(MasterRig rig, Func<bool> done, string why)
    {
        for (int i = 0; i < 100 && !done(); i++) await QuiesceAsync(rig.Witness, null);
        done().Should().BeTrue(why);
    }

    private static object Field(CanOpenNode node, string name)
        => typeof(CanOpenNode).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(node)!;

    private static bool SlaveFlag(CanOpenNode node, string name) => ((bool[])Field(node, name))[Slave];

    private static async Task<T> OnActorAsync<T>(CanOpenNode node, Func<T> read)
    {
        T value = default!;
        await node.PostToActorAsync(() => value = read());
        return value;
    }

    /// <summary>Holds the master's SDO requests (not its aborts) to the slave while <see cref="HoldSdo"/> is set,
    /// each until released, and holds the broadcast Reset Communication after
    /// <see cref="PassResets"/> have passed, until <see cref="ReleaseReset"/>.</summary>
    private sealed class SafetyBootGate : ICanBusService
    {
        private readonly CanBusService _inner;
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource<bool>> _heldSdo = new();
        private readonly TaskCompletionSource<bool> _resetEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _resetRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _resetsSeen;
        private volatile bool _holdSdo;

        public SafetyBootGate(CanBusService inner) => _inner = inner;

        public bool HoldSdo { get => _holdSdo; set => _holdSdo = value; }
        public int PassResets { get; set; } = int.MaxValue;
        public Task ResetEntered => _resetEntered.Task;

        public int SdoHeld
        {
            get { lock (_gate) return _heldSdo.Count; }
        }

        /// <summary>Lets held request <paramref name="index"/> go: onto the bus, or reported as
        /// confirmed without reaching it.</summary>
        public void ReleaseSdo(int index, bool transmit)
        {
            TaskCompletionSource<bool> held;
            lock (_gate) held = _heldSdo[index];
            held.TrySetResult(transmit);
        }

        public void ReleaseReset(bool confirm) => _resetRelease.TrySetResult(confirm);

        public ICanBus Bus => _inner.Bus;
        public int SubscriptionCount => _inner.SubscriptionCount;
        public event EventHandler<Exception>? BackgroundExceptionOccurred
        {
            add => _inner.BackgroundExceptionOccurred += value;
            remove => _inner.BackgroundExceptionOccurred -= value;
        }

        public ISubscription Subscribe(Func<CanFrameEvent, bool>? predicate = null, int? bufferCapacity = null, bool includeEcho = false)
            => _inner.Subscribe(predicate, bufferCapacity, includeEcho);

        public ISubscription Subscribe(CanIdFilter filter, int? bufferCapacity = null, bool includeEcho = false)
            => _inner.Subscribe(filter, bufferCapacity, includeEcho);

        public IReadOnlyList<FilterOverlap> FindOverlappingFilterSubscriptions()
            => _inner.FindOverlappingFilterSubscriptions();

        public async Task<TxConfirmation> SendConfirmedAsync(CanFrame frame, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            // An abort (a cancelled transfer) passes: only the requests of a running verification wait.
            if (_holdSdo && frame.ID == (int)CanOpenCobId.SdoRx(Slave) && !frame.IsExtendedFrame && frame.Data.Span[0] != 0x80)
            {
                var held = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_gate) _heldSdo.Add(held);
                if (!await held.Task.ConfigureAwait(false))
                    return new TxConfirmation { Confirmed = true };
            }
            else if (IsBroadcastReset(frame) && Interlocked.Increment(ref _resetsSeen) > PassResets)
            {
                _resetEntered.TrySetResult(true);
                if (!await _resetRelease.Task.ConfigureAwait(false))
                    return new TxConfirmation { Confirmed = false, FailureReason = TxConfirmFailureReason.Rejected };
            }
            return await _inner.SendConfirmedAsync(frame, timeout, cancellationToken).ConfigureAwait(false);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                foreach (var held in _heldSdo) held.TrySetResult(false);
            }
            _resetRelease.TrySetResult(false);
            _inner.Dispose();
        }

        private static bool IsBroadcastReset(CanFrame frame)
        {
            var data = frame.Data.ToArray();
            return frame.ID == 0 && !frame.IsExtendedFrame && data.Length >= 2
                && data[0] == (byte)NmtCommand.ResetCommunication && data[1] == 0;
        }
    }
}
