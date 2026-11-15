using System;
using System.Collections;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Pro.Actor;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Lss;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

public partial class CanOpenPdoEngineTests
{
    private static readonly StubErrorInfo RxOverrun = new() { ControllerStatus = CanControllerStatus.RxOverflow };

    private static void RaiseOverrun(ControllerEmcyRig rig, CanControllerStatus status = CanControllerStatus.RxOverflow)
    {
        rig.Bus.RaiseErrorFrame(new StubErrorInfo { ControllerStatus = status });
        Settle(rig.Node);
    }

    [Fact]
    public async Task Overrun_Emcy_Default_Off_Adds_No_Subscription_And_Acknowledgement_Is_A_NoOp()
    {
        new CanOpenNodeOptions().EmitControllerOverrunEmcy.Should().BeFalse();
        using var rig = new ControllerEmcyRig(enabled: false);
        rig.Bus.ErrorFrameSubscriberCount.Should().Be(rig.BaselineErrors);
        rig.Bus.FaultSubscriberCount.Should().Be(rig.BaselineFaults);
        RaiseOverrun(rig, CanControllerStatus.RxOverflow | CanControllerStatus.TxOverflow);
        rig.Node.AcknowledgeControllerOverrun();
        await rig.Node.SendEmcyAsync(0x1000, 0);
        await ExpectEmcyAsync(rig.Wire, 1, 0x1000, 0);
        rig.Wire.Count(EmcyId).Should().Be(1);
    }

    [Theory]
    [InlineData(CanControllerStatus.RxOverflow)]
    [InlineData(CanControllerStatus.TxOverflow)]
    [InlineData(CanControllerStatus.RxOverflow | CanControllerStatus.TxOverflow)]
    [InlineData(CanControllerStatus.RxOverflow | CanControllerStatus.RxWarning)]
    public async Task Overrun_Emcy_Latches_Both_Directions_Until_Explicit_Acknowledgement(CanControllerStatus status)
    {
        using var rig = new ControllerEmcyRig(enabled: false, overrunEnabled: true);
        rig.Bus.ErrorFrameSubscriberCount.Should().Be(rig.BaselineErrors + 1);
        rig.Bus.FaultSubscriberCount.Should().Be(rig.BaselineFaults, "overruns need no controller-level monitor");
        await rig.Node.SendEmcyAsync(0x1000, 4);
        await ExpectEmcyAsync(rig.Wire, 1, 0x1000, 4);
        RaiseOverrun(rig, status);
        await ExpectEmcyAsync(rig.Wire, 2, 0x8110, 0x15);
        RaiseOverrun(rig, CanControllerStatus.TxOverflow);
        RaiseOverrun(rig);
        rig.Bus.RaiseErrorFrame(new StubErrorInfo { ControllerStatus = CanControllerStatus.Active });
        rig.Observe(BusState.ErrActive);
        rig.Observe(BusState.ErrWarning);
        rig.Observe(BusState.Unknown);
        rig.Observe(BusState.None);
        await rig.Node.SendEmcyAsync(0x1000, 4);
        await ExpectEmcyAsync(rig.Wire, 3, 0x1000, 0x15);
        rig.Node.ObjectDictionary.ReadUnsigned(0x1001, 0).Should().Be(0x15);
        rig.Node.AcknowledgeControllerOverrun();
        await ExpectEmcyAsync(rig.Wire, 4, ErrorReset, 4);
        rig.Node.AcknowledgeControllerOverrun();
        await rig.Node.SendEmcyAsync(0x1000, 4);
        await ExpectEmcyAsync(rig.Wire, 5, 0x1000, 4);
        RaiseOverrun(rig, status);
        await ExpectEmcyAsync(rig.Wire, 6, 0x8110, 0x15);
        rig.Wire.Count(EmcyId).Should().Be(6);
    }

    [Theory]
    [InlineData(CanControllerStatus.None)]
    [InlineData(CanControllerStatus.Active)]
    [InlineData(CanControllerStatus.RxWarning)]
    [InlineData(CanControllerStatus.TxPassive)]
    [InlineData(CanControllerStatus.Unknown)]
    public async Task Overrun_Emcy_Does_Not_Infer_Lost_Objects_From_Other_Error_Flags(CanControllerStatus status)
    {
        using var rig = new ControllerEmcyRig(enabled: false, overrunEnabled: true);
        rig.Bus.RaiseErrorFrame(new StubErrorInfo { Type = FrameErrorType.Controller | FrameErrorType.BusError, ControllerStatus = status });
        Settle(rig.Node);
        rig.Node.AcknowledgeControllerOverrun();
        await rig.Node.SendEmcyAsync(0x1000, 0);
        await ExpectEmcyAsync(rig.Wire, 1, 0x1000, 0);
        rig.Wire.Count(EmcyId).Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Overrun_Emcy_And_Passive_Only_Reset_After_Both_Causes_End(bool acknowledgeFirst)
    {
        using var rig = new ControllerEmcyRig(overrunEnabled: true);
        await rig.Node.SendEmcyAsync(0x1000, 4);
        await ExpectEmcyAsync(rig.Wire, 1, 0x1000, 4);
        RaiseOverrun(rig);
        await ExpectEmcyAsync(rig.Wire, 2, 0x8110, 0x15);
        rig.Observe(BusState.ErrPassive);
        await ExpectEmcyAsync(rig.Wire, 3, 0x8120, 0x15);
        if (acknowledgeFirst) rig.Node.AcknowledgeControllerOverrun();
        else rig.Observe(BusState.ErrActive);
        await rig.Node.SendEmcyAsync(0x1000, 4);
        await ExpectEmcyAsync(rig.Wire, 4, 0x1000, 0x15);
        if (acknowledgeFirst) rig.Observe(BusState.ErrActive);
        else rig.Node.AcknowledgeControllerOverrun();
        await ExpectEmcyAsync(rig.Wire, 5, ErrorReset, 4);
        rig.Wire.Count(EmcyId).Should().Be(5);
    }

    [Fact]
    public async Task Overrun_Emcy_Survives_BusOff_And_Recovery_Until_Acknowledged()
    {
        using var rig = new ControllerEmcyRig(enabled: false, recoveryEnabled: true, overrunEnabled: true);
        RaiseOverrun(rig);
        await ExpectEmcyAsync(rig.Wire, 1, 0x8110, Communication);
        rig.Observe(BusState.BusOff);
        rig.Observe(BusState.ErrActive);
        await ExpectEmcyAsync(rig.Wire, 2, 0x8140, Communication);
        await rig.Node.SendEmcyAsync(0x1000, 0);
        await ExpectEmcyAsync(rig.Wire, 3, 0x1000, Communication);
        rig.Node.AcknowledgeControllerOverrun();
        await ExpectEmcyAsync(rig.Wire, 4, ErrorReset, 0);
        rig.Wire.Count(EmcyId).Should().Be(4);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Overrun_Emcy_Reenable_Reports_Only_An_Unacknowledged_Cause(bool acknowledgedWhileDisabled)
    {
        using var rig = new ControllerEmcyRig(enabled: false, overrunEnabled: true);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1014, 0, EmcyId | CanOpenCobId.InvalidBit);
        Settle(rig.Node);
        RaiseOverrun(rig);
        if (acknowledgedWhileDisabled) rig.Node.AcknowledgeControllerOverrun();
        rig.Node.ObjectDictionary.WriteUnsigned(0x1014, 0, EmcyId);
        Settle(rig.Node);
        int count = acknowledgedWhileDisabled ? 0 : 1;
        if (!acknowledgedWhileDisabled) await ExpectEmcyAsync(rig.Wire, count, 0x8110, Communication);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1014, 0, EmcyId | CanOpenCobId.InvalidBit);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1014, 0, EmcyId);
        Settle(rig.Node);
        await rig.Node.SendEmcyAsync(0x1000, 0);
        await ExpectEmcyAsync(rig.Wire, ++count, 0x1000, acknowledgedWhileDisabled ? (byte)0 : Communication);
        rig.Wire.Count(EmcyId).Should().Be(count);
    }

    [Fact]
    public async Task Overrun_Emcy_Acknowledgement_While_Disabled_Still_Clears_The_Reported_Register()
    {
        using var rig = new ControllerEmcyRig(enabled: false, overrunEnabled: true);
        RaiseOverrun(rig);
        await ExpectEmcyAsync(rig.Wire, 1, 0x8110, Communication);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1014, 0, EmcyId | CanOpenCobId.InvalidBit);
        Settle(rig.Node);
        rig.Node.AcknowledgeControllerOverrun();
        await WaitForErrorRegisterAsync(rig.Node, 0);
        rig.Node.ObjectDictionary.ReadUnsigned(0x1001, 0).Should().Be(0);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1014, 0, EmcyId);
        Settle(rig.Node);
        await rig.Node.SendEmcyAsync(0x1000, 0);
        await ExpectEmcyAsync(rig.Wire, 2, 0x1000, 0);
        rig.Wire.Count(EmcyId).Should().Be(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Overrun_Emcy_Uses_Existing_Stopped_Coalescing(bool acknowledgeWhileStopped)
    {
        using var rig = new ControllerEmcyRig(enabled: false, overrunEnabled: true);
        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)NmtCommand.Stop, Device);
        RaiseOverrun(rig);
        RaiseOverrun(rig, CanControllerStatus.TxOverflow);
        if (acknowledgeWhileStopped) rig.Node.AcknowledgeControllerOverrun();
        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)NmtCommand.Start, Device);
        await ExpectEmcyAsync(rig.Wire, 1, acknowledgeWhileStopped ? ErrorReset : (ushort)0x8110,
            acknowledgeWhileStopped ? (byte)0 : Communication);
        await rig.Node.SendEmcyAsync(0x1000, 0);
        await ExpectEmcyAsync(rig.Wire, 2, 0x1000, acknowledgeWhileStopped ? (byte)0 : Communication);
        rig.Wire.Count(EmcyId).Should().Be(2);
    }

    [Theory]
    [InlineData(NmtCommand.ResetNode)]
    [InlineData(NmtCommand.ResetCommunication)]
    public async Task Overrun_Emcy_Reset_Clears_History_Without_A_Reset_Emcy(NmtCommand command)
    {
        using var rig = new ControllerEmcyRig(enabled: false, overrunEnabled: true);
        RaiseOverrun(rig);
        await ExpectEmcyAsync(rig.Wire, 1, 0x8110, Communication);
        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)command, Device);
        Settle(rig.Node);
        rig.Node.AcknowledgeControllerOverrun();
        await rig.Node.SendEmcyAsync(0x1000, 0);
        await ExpectEmcyAsync(rig.Wire, 2, 0x1000, 0);
        RaiseOverrun(rig);
        await ExpectEmcyAsync(rig.Wire, 3, 0x8110, Communication);
        rig.Wire.Count(EmcyId).Should().Be(3);
    }

    [Theory]
    [InlineData(NmtCommand.ResetNode)]
    [InlineData(NmtCommand.ResetCommunication)]
    public async Task Overrun_Emcy_Reset_Discards_An_Already_Queued_Observation(NmtCommand command)
    {
        using var rig = new ControllerEmcyRig(enabled: false, overrunEnabled: true);
        var node = (CanOpenNode)rig.Node;
        await node.PostToActorAsync(() =>
        {
            rig.Bus.RaiseErrorFrame(RxOverrun); // the queued callback still owns the old history
            typeof(CanOpenNode).GetMethod("HandleIncoming", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(node, new object[] { CanOpenCobId.NmtCommand, new byte[] { (byte)command, Device }, false });
        });
        Settle(node);
        await node.SendEmcyAsync(0x1000, 0);
        await ExpectEmcyAsync(rig.Wire, 1, 0x1000, 0);
        RaiseOverrun(rig);
        await ExpectEmcyAsync(rig.Wire, 2, 0x8110, Communication);
        rig.Wire.Count(EmcyId).Should().Be(2);
    }

    [Fact]
    public async Task Overrun_Emcy_Acknowledgement_On_The_Actor_Allows_A_Later_Notification()
    {
        using var rig = new ControllerEmcyRig(enabled: false, overrunEnabled: true);
        RaiseOverrun(rig);
        await ExpectEmcyAsync(rig.Wire, 1, 0x8110, Communication);
        await ((CanOpenNode)rig.Node).PostToActorAsync(() =>
        {
            rig.Node.AcknowledgeControllerOverrun();
            rig.Bus.RaiseErrorFrame(RxOverrun);
        }).WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 2, ErrorReset, 0);
        await ExpectEmcyAsync(rig.Wire, 3, 0x8110, Communication);
        rig.Wire.Count(EmcyId).Should().Be(3);
    }

    [Fact]
    public async Task Overrun_Emcy_Coalesces_A_Storm_To_One_Queued_Observation()
    {
        using var rig = new ControllerEmcyRig(enabled: false, overrunEnabled: true);
        var node = (CanOpenNode)rig.Node;
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = node.PostToActorAsync(() =>
        {
            entered.TrySetResult(true);
            release.Wait(ShortTimeout);
        });
        try
        {
            await entered.Task.WaitAsync(ShortTimeout);
            for (int i = 0; i < 10_000; i++) rig.Bus.RaiseErrorFrame(RxOverrun);
            var mailbox = (ICollection)typeof(ProtocolActor).GetField("_mailbox", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(node.ActorForTests)!;
            mailbox.Count.Should().Be(1, "a notification storm must not flood the node actor");
        }
        finally
        {
            release.Set();
            await blocked.WaitAsync(ShortTimeout);
        }
        await ExpectEmcyAsync(rig.Wire, 1, 0x8110, Communication);
        rig.Node.AcknowledgeControllerOverrun();
        await ExpectEmcyAsync(rig.Wire, 2, ErrorReset, 0);
        RaiseOverrun(rig);
        await ExpectEmcyAsync(rig.Wire, 3, 0x8110, Communication);
    }

    [Fact]
    public async Task Overrun_Emcy_Does_Not_Replay_Notifications_From_Unconfigured_Lss_Initialisation()
    {
        using var rig = new ControllerEmcyRig(enabled: false, unconfiguredLss: true, overrunEnabled: true);
        RaiseOverrun(rig);
        await Inject(rig.Node, CanOpenCobId.LssMasterMessage, LssCommand.SwitchStateGlobal, 1, 0, 0, 0, 0, 0, 0);
        await Inject(rig.Node, CanOpenCobId.LssMasterMessage, LssCommand.ConfigureNodeId, Device, 0, 0, 0, 0, 0, 0);
        Settle(rig.Node);
        await rig.Node.SendEmcyAsync(0x1000, 0);
        await ExpectEmcyAsync(rig.Wire, 1, 0x1000, 0);
        RaiseOverrun(rig);
        await ExpectEmcyAsync(rig.Wire, 2, 0x8110, Communication);
        rig.Wire.Count(EmcyId).Should().Be(2);
    }

    [Fact]
    public async Task Overrun_Emcy_Removes_Handlers_On_Disposal_And_Failed_Construction()
    {
        using var rig = new ControllerEmcyRig(enabled: false, overrunEnabled: true);
        var captured = rig.Bus.CaptureErrorFrameHandlers()!;
        var failure = new InvalidOperationException("driver teardown");
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Node.BackgroundExceptionOccurred += (_, error) => reported.TrySetResult(error);
        rig.Bus.ErrorFrameUnsubscriptionFailure = failure;
        rig.Node.Dispose();
        (await reported.Task.WaitAsync(ShortTimeout)).Should().BeSameAs(failure);
        rig.Bus.ErrorFrameUnsubscriptionFailure = null;
        rig.Bus.ErrorFrameSubscriberCount.Should().Be(rig.BaselineErrors);
        rig.Bus.FaultSubscriberCount.Should().Be(rig.BaselineFaults);
        Action late = () => captured(rig.Bus, RxOverrun);
        late.Should().NotThrow();
        Action acknowledge = () => rig.Node.AcknowledgeControllerOverrun();
        acknowledge.Should().Throw<ObjectDisposedException>();
        var failing = new ThrowingSubscribeService(rig.Service, failOnCall: 1);
        Action open = () => _ = new CanOpenNode(failing, Device,
            new CanOpenNodeOptions { EmitControllerOverrunEmcy = true }, ownsService: false, rig.Clock);
        open.Should().Throw<InvalidOperationException>();
        rig.Bus.ErrorFrameSubscriberCount.Should().Be(rig.BaselineErrors);
        rig.Service.SubscriptionCount.Should().Be(0);
    }

    [Fact]
    public async Task Overrun_Emcy_Does_Not_Throw_Into_The_Driver_When_Disposal_Wins_The_Post_Race()
    {
        using var rig = new ControllerEmcyRig(enabled: false, overrunEnabled: true);
        var node = (CanOpenNode)rig.Node;
        var observation = typeof(CanOpenNode).GetField("_controllerOverrunObservation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(node)!;
        var pending = observation.GetType().GetField("Pending", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var postGate = typeof(ProtocolActor).GetField("_disposeGate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(node.ActorForTests)!;
        Task callback = Task.CompletedTask;
        try
        {
            lock (postGate)
            {
                callback = Task.Run(() => rig.Bus.RaiseErrorFrame(RxOverrun));
                // Hold the actor's actual posting/disposal lock. Once the callback claims its
                // observation it cannot post until this thread disposes the node and releases
                // the lock. No scheduling delay decides which operation wins this race.
                SpinWait.SpinUntil(() => (int)pending.GetValue(observation)! == 1, ShortTimeout).Should().BeTrue();
                node.Dispose();
            }
        }
        finally
        {
            await FluentActions.Awaiting(() => callback.WaitAsync(ShortTimeout))
                .Should().NotThrowAsync("a normal teardown race must not fault the adapter's receive loop");
        }
        rig.Bus.ErrorFrameSubscriberCount.Should().Be(rig.BaselineErrors);
    }

    [Theory]
    [InlineData("OutOfMemory")]
    [InlineData("StackOverflow")]
    [InlineData("AccessViolation")]
    public async Task Overrun_Emcy_Does_Not_Swallow_Fatal_Detach_Failures(string kind)
    {
        using var rig = new ControllerEmcyRig(enabled: false, overrunEnabled: true);
        Exception failure = kind switch
        {
            "OutOfMemory" => new OutOfMemoryException("simulated fatal detach failure"),
            "StackOverflow" => new StackOverflowException("simulated fatal detach failure"),
            _ => new AccessViolationException("simulated fatal detach failure"),
        };
        rig.Bus.ErrorFrameUnsubscriptionFailure = failure;
        // Invoke only the component's teardown, so a simulated fatal failure does not strand
        // the enclosing node mid-Dispose. The ordinary using still releases its other resources.
        var teardown = (Action)typeof(CanOpenNode).GetMethod("DisposeControllerOverrunMonitor", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate(typeof(Action), rig.Node);
        var outcome = ((CanOpenNode)rig.Node).PostToActorAsync(teardown);
        await FluentActions.Awaiting(() => outcome.WaitAsync(ShortTimeout)).Should().ThrowAsync<Exception>()
            .Where(error => ReferenceEquals(error, failure));
        rig.Bus.ErrorFrameUnsubscriptionFailure = null;
        rig.Bus.ErrorFrameSubscriberCount.Should().Be(rig.BaselineErrors);
    }

    [Fact]
    public void Overrun_Emcy_Rejects_An_Unsupported_Subscription_Instead_Of_Silently_Missing_Errors()
    {
        using var rig = new ControllerEmcyRig(enabled: false);
        rig.Bus.ErrorFrameSubscriptionFailure = new NotSupportedException("error capture disabled");
        Action open = () => _ = new CanOpenNode(rig.NodeService, 2,
            new CanOpenNodeOptions { EmitControllerOverrunEmcy = true }, ownsService: false, rig.Clock);
        open.Should().Throw<NotSupportedException>().WithMessage("error capture disabled");
        rig.Bus.ErrorFrameSubscriberCount.Should().Be(rig.BaselineErrors);
        rig.Service.SubscriptionCount.Should().Be(1, "only the original node's subscription remains");
    }

    [Fact]
    public void Overrun_Emcy_Option_Is_Preserved_By_Copy_Methods_And_Null_Acknowledgement_Is_Rejected()
    {
        var options = new CanOpenNodeOptions { EmitControllerOverrunEmcy = true };
        options.With(sdoTimeout: TimeSpan.FromSeconds(2)).EmitControllerOverrunEmcy.Should().BeTrue();
        options.WithSrdoConfigurationCheckInterval(null).EmitControllerOverrunEmcy.Should().BeTrue();
        options.WithLss(new LssSlaveOptions()).EmitControllerOverrunEmcy.Should().BeTrue();
        options.WithErrorHistoryCapacity(10).EmitControllerOverrunEmcy.Should().BeTrue();
        Action acknowledge = () => CanOpenNodeExtensions.AcknowledgeControllerOverrun(null!);
        acknowledge.Should().Throw<ArgumentNullException>().WithParameterName("node");
    }

#if NET5_0_OR_GREATER
    [Fact]
    public void Overrun_Emcy_Acknowledgement_Rejects_A_Third_Party_Node_Without_Widening_The_Interface()
    {
        var foreign = DispatchProxy.Create<ICanOpenNode, ForeignOverrunNode>();
        Action acknowledge = () => foreign.AcknowledgeControllerOverrun();
        acknowledge.Should().Throw<NotSupportedException>();
    }

    private class ForeignOverrunNode : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new NotSupportedException();
    }
#endif
}
