using System;
using System.Linq;
using System.Reflection;
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
    private static object HeartbeatWait(MasterRig rig)
        => ((Array)BootField(rig.Node, "_slaveBootHeartbeatWaits")).GetValue(Slave)!;

    [Fact]
    public async Task A_Later_Manual_Guarding_Consumer_Cannot_Approve_The_Heartbeat_Wait()
    {
        using var rig = OpenMaster(ErrorControlOptions());
        using var probe = new BootGuardProbe(rig.Peer);
        ConfigureErrorControl(rig);
        await EnterHeartbeatStage(rig);
        rig.Node.StartNodeGuardingConsumer(Slave, TimeSpan.FromMilliseconds(200), 1);
        await probe.FirstPoll.Task.WaitAsync(ShortTimeout);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await QuiesceAsync(rig.Witness, null);
        Result(rig).Status.Should().Be(SlaveBootStatus.WaitingForErrorControl);
        rig.Node.StopNodeGuardingConsumer(Slave);
        await QuiesceAsync(rig.Witness, null);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await QuiesceAsync(rig.Witness, null);
        Started(rig, Slave).Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Buffered_Or_Untimed_Heartbeat_Cannot_Approve_The_New_Boot_Window(int kind)
    {
        using var rig = OpenMaster(ErrorControlOptions());
        ConfigureErrorControl(rig);
        await EnterHeartbeatStage(rig);
        var wait = HeartbeatWait(rig);
        long started = (long)wait.GetType().GetField("Started")!.GetValue(wait)!;
        long due = (long)wait.GetType().GetField("Due")!.GetValue(wait)!;
        long arrival = kind == 0 ? 0 : kind == 1 ? started - 1 : due;
        await rig.Node.PostToActorAsync(() => InvokeBoot(rig.Node, "ObserveBootErrorControlHeartbeat",
            (uint)(0x700 + Slave), new byte[] { 0x7F }, false, false, arrival));
        await QuiesceAsync(rig.Witness, null);
        Result(rig).Status.Should().Be(SlaveBootStatus.WaitingForErrorControl);
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await QuiesceAsync(rig.Witness, null);
        Started(rig, Slave).Should().BeTrue();
    }

    [Fact]
    public async Task A_Relevant_Heartbeat_Edit_Replaces_The_Window_And_Rejects_Its_Old_Timeout()
    {
        using var rig = OpenMaster(ErrorControlOptions());
        ConfigureErrorControl(rig);
        await EnterHeartbeatStage(rig);
        var previous = HeartbeatWait(rig);
        int generation = (int)BootField(rig.Node, "_bootGeneration");
        rig.Node.AddHeartbeatConsumer(6, TimeSpan.FromMilliseconds(300));
        rig.Node.AddHeartbeatConsumer(Slave, TimeSpan.FromMilliseconds(100));
        ((int)BootField(rig.Node, "_bootGeneration")).Should().Be(generation,
            "an unrelated watch or unchanged consumer time does not restart boot");
        rig.Node.AddHeartbeatConsumer(Slave, TimeSpan.FromMilliseconds(200));
        ((int)BootField(rig.Node, "_bootGeneration")).Should().BeGreaterThan(generation);
        TransmitHeartbeat(rig.Peer, Slave, 0);
        await QuiesceAsync(rig.Witness, null);
        var current = HeartbeatWait(rig);
        current.Should().NotBeSameAs(previous);
        await rig.Node.PostToActorAsync(() => InvokeBoot(rig.Node, "OnBootHeartbeatTimeout", Slave, previous));
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(100));
        Result(rig).Status.Should().Be(SlaveBootStatus.WaitingForErrorControl);
        rig.Node.RemoveHeartbeatConsumer(Slave);
        TransmitHeartbeat(rig.Peer, Slave, 0);
        await QuiesceAsync(rig.Witness, null);
        Started(rig, Slave).Should().BeTrue("removing the watch selects the no-monitoring path");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_Abandoned_Forced_Reset_Repeats_Error_Control_With_A_Full_Window(bool receive)
    {
        using var rig = OpenMaster(ErrorControlOptions());
        ConfigureErrorControl(rig);
        await EnterHeartbeatStage(rig);
        await rig.Node.PostToActorAsync(() => SetBootField(rig.Node, "_coldResetPending", true));
        if (receive) TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(100));
        Started(rig, Slave).Should().BeFalse();
        Result(rig).Status.Should().NotBe(SlaveBootStatus.ErrorControlFailed);
        await rig.Node.PostToActorAsync(() =>
        {
            SetBootField(rig.Node, "_coldResetPending", false);
            InvokeBoot(rig.Node, "ResumeHeldBoot");
        });
        Result(rig).Status.Should().Be(SlaveBootStatus.WaitingForErrorControl);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(50));
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await QuiesceAsync(rig.Witness, null);
        Started(rig, Slave).Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task A_Cancelled_Error_Control_Window_Cannot_Start_Or_Report_A_Later_Failure(int action)
    {
        using var rig = OpenMaster(ErrorControlOptions());
        ConfigureErrorControl(rig);
        await EnterHeartbeatStage(rig);
        var previous = HeartbeatWait(rig);
        if (action == 0) rig.Node.StopFlyingMaster();
        else if (action == 1) await rig.Node.PostToActorAsync(() => InvokeBoot(rig.Node, "EnterStandby", (ushort)0, (byte)1));
        else rig.Node.ObjectDictionary.WriteUnsigned(0x1F81, Slave, 0);
        await rig.Node.PostToActorAsync(() => InvokeBoot(rig.Node, "OnBootHeartbeatTimeout", Slave, previous));
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(200));
        rig.Node.GetSlaveBootResults().Should().NotContain(r => r.NodeId == Slave);
        Started(rig, Slave).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Boot_Cancellation_Stops_Own_Guarding_But_Preserves_An_Explicit_Replacement(bool replace)
    {
        using var rig = OpenMaster(ErrorControlOptions());
        using var probe = new BootGuardProbe(rig.Peer);
        int timeouts = 0;
        rig.Node.NodeGuardingTimeout += (_, e) => { if (e.ProducerNodeId == Slave) Interlocked.Increment(ref timeouts); };
        ConfigureErrorControl(rig, heartbeatMs: 0, guardMs: 20, retries: 2);
        await ElectActive(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0);
        await QuiesceAsync(rig.Witness, null);
        await probe.FirstPoll.Task.WaitAsync(ShortTimeout);
        if (replace)
        {
            rig.Node.StartNodeGuardingConsumer(Slave, TimeSpan.FromMilliseconds(40), 2);
            await QuiesceAsync(rig.Witness, null);
        }
        rig.Node.StopFlyingMaster();
        await QuiesceAsync(rig.Witness, null);
        int before = probe.Requests;
        for (int window = 0; window < 6; window++)
            await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(20));
        if (replace) probe.Requests.Should().BeGreaterThan(before);
        else
        {
            probe.Requests.Should().Be(before);
            Volatile.Read(ref timeouts).Should().Be(0, "cancelled monitoring must not report later failures");
        }
    }

    [Fact]
    public async Task A_Guarding_Reply_Restores_The_Full_Retry_Allowance()
    {
        using var rig = OpenMaster(ErrorControlOptions());
        using var probe = new BootGuardProbe(rig.Peer);
        int timeouts = 0;
        rig.Node.NodeGuardingTimeout += (_, e) => { if (e.ProducerNodeId == Slave) Interlocked.Increment(ref timeouts); };
        ConfigureErrorControl(rig, heartbeatMs: 0, guardMs: 20, retries: 2);
        await ElectActive(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0);
        await QuiesceAsync(rig.Witness, null);
        await probe.FirstPoll.Task.WaitAsync(ShortTimeout);
        for (int window = 0; window < 2; window++)
            await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(20));
        TransmitHeartbeat(rig.Peer, Slave, 0x7F); // First genuine toggle-zero guarding response.
        await QuiesceAsync(rig.Witness, null);
        for (int window = 0; window < 2; window++)
            await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(20));
        Volatile.Read(ref timeouts).Should().Be(0);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(20));
        Volatile.Read(ref timeouts).Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Suppressed_Or_Application_Owned_Slave_Start_Skips_Error_Control(bool suppress)
    {
        using var rig = OpenMaster(ErrorControlOptions());
        using var probe = new BootGuardProbe(rig.Peer);
        ConfigureErrorControl(rig, guardMs: 20, retries: 1);
        if (suppress) rig.Node.ObjectDictionary.WriteUnsigned(Startup, 0, SuppressSlaveStart);
        else rig.Node.ObjectDictionary.WriteUnsigned(0x1F81, Slave, Assigned | MandatorySlave | 0x10u);
        await ElectActive(rig);
        TransmitHeartbeat(rig.Peer, Slave, 0);
        await AdvanceAsync(rig.Clock, rig.Witness, null, TimeSpan.FromMilliseconds(200));
        Result(rig).Status.Should().Be(SlaveBootStatus.Announced);
        Started(rig, Slave).Should().BeFalse();
        probe.Requests.Should().Be(0);
    }

    [Fact]
    public async Task Configuration_Download_Precedes_A_Fresh_Error_Control_Window()
    {
        using var rig = OpenMaster(new CanOpenNodeOptions { EnableBootConfigurationDownload = true, EnableBootErrorControl = true });
        using var peer = rig.OpenPeerNode(Slave);
        peer.ObjectDictionary.AddDomain(0x2000, 0, Array.Empty<byte>());
        ConfigureDownload(rig);
        rig.Node.AddHeartbeatConsumer(Slave, TimeSpan.FromMilliseconds(100));
        IDisposable? allowed = null;
        var paused = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Node.AfterConciseDcfWriteForTests = () =>
        {
            allowed ??= rig.Witness.AllowPeerVerificationWait(Slave);
            paused.TrySetResult(true);
            return resume.Task;
        };
        try
        {
            await ElectActive(rig);
            TransmitHeartbeat(rig.Peer, Slave, 0);
            await UntilAsync(rig.Clock, rig.Witness, null, () => paused.Task.IsCompleted, 300, "configuration is still being applied");
            TransmitHeartbeat(rig.Peer, Slave, 0x7F);
            await QuiesceAsync(rig.Witness, null);
            Started(rig, Slave).Should().BeFalse();
        }
        finally { resume.TrySetResult(true); allowed?.Dispose(); }
        await UntilAsync(rig.Clock, rig.Witness, null, () => Result(rig).Status == SlaveBootStatus.WaitingForErrorControl,
            100, "the heartbeat window starts only after configuration completes");
        peer.ObjectDictionary.ReadRaw(0x2000, 0).Should().Equal(42);
        Started(rig, Slave).Should().BeFalse("a heartbeat from during the download is not the required later indication");
        TransmitHeartbeat(rig.Peer, Slave, 0x7F);
        await QuiesceAsync(rig.Witness, null);
        Started(rig, Slave).Should().BeTrue();
    }
}
