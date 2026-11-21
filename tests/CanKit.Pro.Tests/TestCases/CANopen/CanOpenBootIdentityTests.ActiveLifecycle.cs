using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;
using static CanKit.Pro.Tests.TestCases.CANopen.FlyingMasterRig;

namespace CanKit.Pro.Tests.TestCases.CANopen;

public partial class CanOpenBootIdentityTests
{
    private static void InvokeBoot(CanOpenNode node, string method, params object[] arguments)
        => typeof(CanOpenNode).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(node, arguments);

    private static MasterRig OpenActiveResetMaster(out CanOpenFlyingMasterTests.ColdResetGate gate)
    {
        var session = NewSession();
        var nodeBus = Open(session, 0);
        var peer = Open(session, 1);
        var clock = new ManualTimeSource();
        gate = new CanOpenFlyingMasterTests.ColdResetGate(new ClockedCanBusService(nodeBus, clock, sendGate: clock)) { ResetTarget = Slave };
        var node = new CanOpenNode(gate, LeftId, ActiveBootOptions(), ownsService: true, timeSource: clock);
        return new MasterRig(clock, node, new ActorWitness(node, peer, WitnessForLeft, gate.Confirmations),
            new FrameLog(nodeBus, peer), peer, nodeBus);
    }

    [Fact]
    public async Task Active_Probe_Waits_For_Reset_Confirmation_Even_After_An_Announcement()
    {
        using var rig = OpenActiveResetMaster(out var gate);
        using var peer = new SilentBootPeer(rig.Peer, Slave, r => BootReply(r, 123));
        using var allowedWait = rig.Witness.AllowPeerVerificationWait(Slave);
        Configure(rig);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F81, Slave, Assigned | BootSlave | MandatorySlave);
        await ElectActive(rig);
        gate.Entered.IsCompleted.Should().BeTrue();
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await QuiesceAsync(rig.Witness, null);
        peer.Requests.Should().Be(0);
        await rig.Node.PostToActorAsync(() =>
            ((bool[])BootField(rig.Node, "_slaveIdentityVerifying"))[Slave].Should().BeFalse("no probe may even be queued before reset confirmation"));
        ((bool[])BootField(rig.Node, "_slaveBootResetPending"))[Slave].Should().BeTrue();
        Started(rig, Slave).Should().BeFalse();
        gate.Release();
        await UntilAsync(rig.Clock, rig.Witness, null, () => Started(rig, Slave), 100, "the confirmed reset releases the probe");
        peer.Requests.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_Rejected_Or_Cancelled_Reset_Is_Retried_Before_Any_Probe(bool cancel)
    {
        using var rig = OpenActiveResetMaster(out var gate);
        using var peer = new SilentBootPeer(rig.Peer, Slave, r => BootReply(r, 123));
        Configure(rig);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F81, Slave, Assigned | BootSlave | MandatorySlave);
        gate.Cancel = cancel;
        gate.Reject = !cancel;
        await ElectActive(rig);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Result(rig).Status == SlaveBootStatus.ResetCommunicationFailed, 100, "the failed reset is observable");
        Result(rig).Error.Should().BeOfType<InvalidOperationException>();
        peer.Requests.Should().Be(0);
        gate.Cancel = false;
        gate.Reject = false;
        gate.Release();
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(99));
        peer.Requests.Should().Be(0);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(1));
        await UntilAsync(rig.Clock, rig.Witness, null, () => Started(rig, Slave), 100, "a confirmed retry precedes the upload");
        peer.Requests.Should().Be(1);
    }

    [Fact]
    public async Task An_Old_Reset_Completion_Cannot_Release_A_Newer_Boot()
    {
        using var rig = OpenActiveResetMaster(out var gate);
        using var peer = new SilentBootPeer(rig.Peer, Slave, r => BootReply(r, 123));
        using var allowedWait = rig.Witness.AllowPeerVerificationWait(Slave);
        Configure(rig);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F81, Slave, Assigned | BootSlave | MandatorySlave);
        await ElectActive(rig);
        int oldGeneration = (int)BootField(rig.Node, "_bootGeneration");
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F84, Slave, 456);
        await rig.Node.PostToActorAsync(() =>
        {
            InvokeBoot(rig.Node, "OnActiveSlaveResetResult", Slave, oldGeneration, true);
            ((bool[])BootField(rig.Node, "_slaveBootResetPending"))[Slave].Should().BeTrue();
            ((bool[])BootField(rig.Node, "_slaveIdentityVerifying"))[Slave].Should().BeFalse();
        });
        gate.Release();
        await UntilAsync(rig.Clock, rig.Witness, null, () => Result(rig).Status == SlaveBootStatus.IdentityMismatch, 100, "only the new expectation decides the boot");
        Started(rig, Slave).Should().BeFalse();
        var before = Result(rig);
        await rig.Node.PostToActorAsync(() => InvokeBoot(rig.Node, "OnActiveSlaveResetResult", Slave,
            (int)BootField(rig.Node, "_bootGeneration"), true));
        Result(rig).Should().BeSameAs(before, "a duplicate completion cannot restart a finished attempt");
    }

    [Fact]
    public async Task Role_Loss_While_Reset_Is_Held_Does_Not_Start_A_Probe_When_It_Completes()
    {
        using var rig = OpenActiveResetMaster(out var gate);
        using var peer = new SilentBootPeer(rig.Peer, Slave, r => BootReply(r, 123));
        using var allowedWait = rig.Witness.AllowPeerVerificationWait(Slave);
        Configure(rig);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F81, Slave, Assigned | BootSlave | MandatorySlave);
        await ElectActive(rig);
        rig.Node.StopFlyingMaster();
        gate.Release();
        await QuiesceAsync(rig.Witness, null);
        peer.Requests.Should().Be(0);
        rig.Node.GetSlaveBootResults().Should().BeEmpty();
    }

    [Fact]
    public void An_Active_Reset_Result_After_Dispose_Is_Discarded()
    {
        using var rig = OpenMaster(ActiveBootOptions());
        rig.Node.Dispose();
        InvokeBoot(rig.Node, "PostActiveSlaveResetResult", Slave, 0, Task.FromResult(true));
    }

    [Theory]
    [InlineData("_disposed", 1)]
    [InlineData("_bootHalted", true)]
    [InlineData("_coldResetPending", true)]
    [InlineData("_flyingMasterRole", FlyingMasterRole.Standby)]
    public async Task Active_Attempts_And_Reset_Completions_Respect_Lifecycle_Barriers(string field, object value)
    {
        using var rig = OpenMaster(ActiveBootOptions());
        Configure(rig);
        await ElectActive(rig);
        await rig.Node.PostToActorAsync(() =>
        {
            InvokeBoot(rig.Node, "CancelBootUp");
            var original = BootField(rig.Node, field);
            ((bool[])BootField(rig.Node, "_slaveBootResetConfirmed"))[Slave] = true;
            SetBootField(rig.Node, field, value);
            try
            {
                InvokeBoot(rig.Node, "StartActiveSlaveAttempt", Slave);
                ((bool[])BootField(rig.Node, "_slaveIdentityVerifying"))[Slave].Should().BeFalse();
                ((bool[])BootField(rig.Node, "_slaveBootResetPending"))[Slave] = true;
                InvokeBoot(rig.Node, "OnActiveSlaveResetResult", Slave, (int)BootField(rig.Node, "_bootGeneration"), true);
                ((bool[])BootField(rig.Node, "_slaveIdentityVerifying"))[Slave].Should().BeFalse();
            }
            finally { SetBootField(rig.Node, field, original); }
        });
    }

    [Theory]
    [InlineData("_slaveStarted")]
    [InlineData("_slaveIdentityVerifying")]
    [InlineData("_slaveVerifying")]
    [InlineData("_slaveConfigurationVerifying")]
    public async Task Active_Attempts_Do_Not_Duplicate_In_Flight_Or_Completed_Work(string field)
    {
        using var rig = OpenMaster(ActiveBootOptions());
        Configure(rig);
        await ElectActive(rig);
        await rig.Node.PostToActorAsync(() =>
        {
            InvokeBoot(rig.Node, "CancelBootUp");
            ((bool[])BootField(rig.Node, field))[Slave] = true;
            InvokeBoot(rig.Node, "StartActiveSlaveAttempt", Slave);
            ((bool[])BootField(rig.Node, "_slaveBootResetPending"))[Slave].Should().BeFalse();
            ((bool[])BootField(rig.Node, field))[Slave] = false;
        });
    }

    [Fact]
    public async Task A_Retry_That_Expires_During_A_Forced_Reset_Hold_Resumes_If_The_Reset_Is_Abandoned()
    {
        using var rig = OpenMaster(ActiveBootOptions());
        bool respond = false;
        using var peer = new SilentBootPeer(rig.Peer, Slave, r => respond ? BootReply(r, 123) : null);
        Configure(rig);
        await ElectActive(rig);
        await UntilAsync(rig.Clock, rig.Witness, null, () => Result(rig).Status == SlaveBootStatus.IdentityReadFailed, 100, "the first try failed");
        await rig.Node.PostToActorAsync(() => SetBootField(rig.Node, "_coldResetPending", true));
        try
        {
            respond = true;
            await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(100));
            peer.Requests.Should().Be(1);
        }
        finally
        {
            await rig.Node.PostToActorAsync(() =>
            {
                SetBootField(rig.Node, "_coldResetPending", false);
                InvokeBoot(rig.Node, "ResumeHeldBoot");
            });
        }
        await UntilAsync(rig.Clock, rig.Witness, null, () => Started(rig, Slave), 100, "an abandoned forced reset resumes silent peers too");
        peer.Requests.Should().Be(2);
    }
}
