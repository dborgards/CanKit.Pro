using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core.Exceptions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Lss;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

public partial class CanOpenPdoEngineTests
{
    private sealed class ControllerEmcyRig : IDisposable
    {
        private readonly ICanBus _forwarder;
        internal readonly ControllableBus Bus;
        internal readonly CanBusService Service;
        internal readonly ControllerBusService NodeService;
        internal readonly Wire Wire;
        internal readonly ManualTimeSource Clock = new();
        internal readonly ICanOpenNode Node;
        internal readonly int BaselineErrors;
        internal readonly int BaselineFaults;

        internal ControllerEmcyRig(BusState initial = BusState.ErrActive, bool enabled = true, bool unconfiguredLss = false)
        {
            var session = NewSession();
            Bus = ControllableBus.Plain(session);
            _forwarder = Open(session, 1);
            Wire = new Wire(session, 2);
            Bus.OnTransmitting = frame => _forwarder.Transmit(frame);
            Bus.BusState = initial;
            Service = new CanBusService(Bus);
            BaselineErrors = Bus.ErrorFrameSubscriberCount;
            BaselineFaults = Bus.FaultSubscriberCount;
            NodeService = new ControllerBusService(Service);
            Node = new CanOpenNode(NodeService, unconfiguredLss ? CanOpenCobId.UnconfiguredNodeId : Device, new CanOpenNodeOptions
            {
                EmitControllerErrorEmcy = enabled,
                EmitGuardingErrorEmcy = true,
                Lss = unconfiguredLss ? new LssSlaveOptions() : null,
            }, ownsService: false, Clock);
            Settle(Node);
        }

        internal void Observe(BusState state)
        {
            Bus.BusState = state;
            Advance(Clock, Node, TimeSpan.FromMilliseconds(51));
        }

        public void Dispose()
        {
            Node.Dispose();
            Service.Dispose();
            Wire.Dispose();
            _forwarder.Dispose();
            Bus.Dispose();
        }
    }

    private sealed class ControllerBusService : ICanBusService
    {
        private readonly ICanBusService _inner;
        internal Exception? NextBusReadFailure;
        internal int BusReads;
        internal ControllerBusService(ICanBusService inner) => _inner = inner;
        public ICanBus Bus
        {
            get
            {
                Interlocked.Increment(ref BusReads);
                if (NextBusReadFailure is not { } failure) return _inner.Bus;
                NextBusReadFailure = null;
                throw failure;
            }
        }
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
        public IReadOnlyList<FilterOverlap> FindOverlappingFilterSubscriptions() => _inner.FindOverlappingFilterSubscriptions();
        public Task<TxConfirmation> SendConfirmedAsync(CanFrame frame, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
            => _inner.SendConfirmedAsync(frame, timeout, cancellationToken);
        public void Dispose() { }
    }

    [Theory]
    [InlineData(BusState.ErrPassive, true)]
    [InlineData(BusState.ErrActive, false)]
    [InlineData(BusState.ErrWarning, false)]
    [InlineData(BusState.BusOff, false)]
    [InlineData(BusState.Unknown, false)]
    [InlineData(BusState.None, false)]
    public async Task Controller_Emcy_Reports_An_Initially_Passive_Controller_At_Startup(BusState state, bool passive)
    {
        using var rig = new ControllerEmcyRig(state);
        // An initially bus-off controller cannot transmit, so observe recovery before the witness.
        if (state == BusState.BusOff) rig.Observe(BusState.ErrActive);
        await rig.Wire.WaitForCountAsync(CanOpenCobId.Heartbeat(Device), 1);
        if (passive) await ExpectEmcyAsync(rig.Wire, 1, 0x8120, Communication);
        await rig.Node.SendEmcyAsync(0x1000, 0);
        await ExpectEmcyAsync(rig.Wire, passive ? 2 : 1, 0x1000, passive ? Communication : (byte)0);
        rig.Wire.Count(EmcyId).Should().Be(passive ? 2 : 1);
    }

    [Fact]
    public async Task Controller_Emcy_Default_Off_Does_Not_Add_Monitor_Subscriptions()
    {
        new CanOpenNodeOptions().EmitControllerErrorEmcy.Should().BeFalse();
        using var rig = new ControllerEmcyRig(enabled: false);
        rig.Bus.ErrorFrameSubscriberCount.Should().Be(rig.BaselineErrors);
        rig.Bus.FaultSubscriberCount.Should().Be(rig.BaselineFaults);
        rig.Observe(BusState.ErrPassive);
        await rig.Node.SendEmcyAsync(0x1000, 0);
        await ExpectEmcyAsync(rig.Wire, 1, 0x1000, 0);
        rig.Wire.Count(EmcyId).Should().Be(1);
    }

    [Theory]
    [InlineData(BusState.ErrActive)]
    [InlineData(BusState.ErrWarning)]
    [InlineData(BusState.None)]
    public async Task Controller_Emcy_Is_Once_Per_Lapse_And_Recovery_Preserves_Application_Bits(BusState recovery)
    {
        using var rig = new ControllerEmcyRig();
        await rig.Node.SendEmcyAsync(0x1000, 4);
        await ExpectEmcyAsync(rig.Wire, 1, 0x1000, 4);
        rig.Observe(BusState.ErrPassive);
        await ExpectEmcyAsync(rig.Wire, 2, 0x8120, 0x15);
        rig.Observe(BusState.ErrPassive);
        await rig.Node.SendEmcyAsync(0x1000, 4);
        await ExpectEmcyAsync(rig.Wire, 3, 0x1000, 0x15);
        rig.Observe(recovery);
        await ExpectEmcyAsync(rig.Wire, 4, ErrorReset, 4);
        rig.Observe(BusState.ErrPassive);
        await ExpectEmcyAsync(rig.Wire, 5, 0x8120, 0x15);
        rig.Wire.Count(EmcyId).Should().Be(5);
    }

    [Theory]
    [InlineData(BusState.BusOff)]
    [InlineData(BusState.Unknown)]
    public async Task Controller_Emcy_Does_Not_Treat_BusOff_Or_Unknown_As_A_Repair(BusState uncertain)
    {
        using var rig = new ControllerEmcyRig();
        rig.Observe(BusState.ErrPassive);
        await ExpectEmcyAsync(rig.Wire, 1, 0x8120, Communication);
        rig.Observe(uncertain);
        rig.Node.ObjectDictionary.ReadUnsigned(0x1001, 0).Should().Be(Communication);
        rig.Observe(BusState.ErrPassive);
        await rig.Node.SendEmcyAsync(0x1000, 0);
        await ExpectEmcyAsync(rig.Wire, 2, 0x1000, Communication);
        rig.Observe(BusState.ErrActive);
        await ExpectEmcyAsync(rig.Wire, 3, ErrorReset, 0);
        rig.Wire.Count(EmcyId).Should().Be(3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Controller_Emcy_And_Heartbeat_Only_Reset_After_Both_Causes_End(bool controllerFirst)
    {
        using var rig = new ControllerEmcyRig();
        rig.Node.AddHeartbeatConsumer(1, TimeSpan.FromMilliseconds(100));
        rig.Observe(BusState.ErrPassive);
        await ExpectEmcyAsync(rig.Wire, 1, 0x8120, Communication);
        Advance(rig.Clock, rig.Node, TimeSpan.FromMilliseconds(50));
        await ExpectEmcyAsync(rig.Wire, 2, 0x8130, Communication);
        if (controllerFirst) rig.Observe(BusState.ErrActive);
        else await Inject(rig.Node, CanOpenCobId.Heartbeat(1), (byte)NmtState.PreOperational);
        await rig.Node.SendEmcyAsync(0x1000, 0);
        await ExpectEmcyAsync(rig.Wire, 3, 0x1000, Communication);
        if (controllerFirst) await Inject(rig.Node, CanOpenCobId.Heartbeat(1), (byte)NmtState.PreOperational);
        else rig.Observe(BusState.ErrActive);
        await ExpectEmcyAsync(rig.Wire, 4, ErrorReset, 0);
        rig.Wire.Count(EmcyId).Should().Be(4);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Controller_Emcy_Reenable_Reports_Only_A_Still_Active_Cause(bool repaired)
    {
        using var rig = new ControllerEmcyRig();
        rig.Node.ObjectDictionary.WriteUnsigned(0x1014, 0, EmcyId | CanOpenCobId.InvalidBit);
        Settle(rig.Node);
        rig.Observe(BusState.ErrPassive);
        if (repaired) rig.Observe(BusState.ErrActive);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1014, 0, EmcyId);
        Settle(rig.Node);
        if (!repaired) await ExpectEmcyAsync(rig.Wire, 1, 0x8120, Communication);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1014, 0, EmcyId | CanOpenCobId.InvalidBit);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1014, 0, EmcyId);
        Settle(rig.Node);
        await rig.Node.SendEmcyAsync(0x1000, 0);
        await ExpectEmcyAsync(rig.Wire, repaired ? 1 : 2, 0x1000, repaired ? (byte)0 : Communication);
        rig.Wire.Count(EmcyId).Should().Be(repaired ? 1 : 2);
    }

    [Fact]
    public async Task Controller_Emcy_Reset_Rechecks_The_Physical_Level_After_Bootup()
    {
        using var rig = new ControllerEmcyRig();
        rig.Observe(BusState.ErrPassive);
        await ExpectEmcyAsync(rig.Wire, 1, 0x8120, Communication);
        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)NmtCommand.ResetCommunication, Device);
        await rig.Wire.WaitForCountAsync(CanOpenCobId.Heartbeat(Device), 2);
        await ExpectEmcyAsync(rig.Wire, 2, 0x8120, Communication);
        rig.Observe(BusState.ErrActive);
        await ExpectEmcyAsync(rig.Wire, 3, ErrorReset, 0);
        rig.Wire.Count(EmcyId).Should().Be(3);
    }

    [Theory]
    [InlineData(BusState.ErrActive, NmtCommand.ResetCommunication)]
    [InlineData(BusState.ErrWarning, NmtCommand.ResetCommunication)]
    [InlineData(BusState.None, NmtCommand.ResetCommunication)]
    [InlineData(BusState.BusOff, NmtCommand.ResetCommunication)]
    [InlineData(BusState.Unknown, NmtCommand.ResetCommunication)]
    [InlineData(BusState.ErrActive, NmtCommand.ResetNode)]
    [InlineData(BusState.ErrWarning, NmtCommand.ResetNode)]
    [InlineData(BusState.None, NmtCommand.ResetNode)]
    [InlineData(BusState.BusOff, NmtCommand.ResetNode)]
    [InlineData(BusState.Unknown, NmtCommand.ResetNode)]
    public async Task Controller_Emcy_Reset_Takes_A_Fresh_Baseline_Without_A_Poll_Or_Hint(BusState current, NmtCommand command)
    {
        using var rig = new ControllerEmcyRig();
        rig.Observe(BusState.ErrPassive);
        await ExpectEmcyAsync(rig.Wire, 1, 0x8120, Communication);
        var beforeReset = rig.Clock.GetTimestamp();
        rig.Bus.BusState = current;
        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)command, Device);
        await rig.Wire.WaitForCountAsync(CanOpenCobId.Heartbeat(Device), 2);
        rig.Clock.GetTimestamp().Should().Be(beforeReset);
        await rig.Node.SendEmcyAsync(0x1000, 0);
        await ExpectEmcyAsync(rig.Wire, 2, 0x1000, 0);
        // The replacement monitor must also recognize the next passive edge, even if the
        // pre-reset monitor's cached level was already passive.
        rig.Observe(BusState.ErrPassive);
        await ExpectEmcyAsync(rig.Wire, 3, 0x8120, Communication);
        rig.Wire.Count(EmcyId).Should().Be(3);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Controller_Emcy_Reset_Retries_A_Failed_Baseline_And_Cleans_Up_The_Retry(bool disposeBeforeRetry, bool disposedRead)
    {
        using var rig = new ControllerEmcyRig();
        rig.Observe(BusState.ErrPassive);
        await ExpectEmcyAsync(rig.Wire, 1, 0x8120, Communication);
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Node.BackgroundExceptionOccurred += (_, error) => reported.TrySetResult(error);
        rig.NodeService.NextBusReadFailure = disposedRead
            ? new ObjectDisposedException(null, "transient controller status read")
            : new CanKitException(CanKitErrorCode.NativeCallFailed, "transient controller status read");
        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)NmtCommand.ResetCommunication, Device);
        (await reported.Task.WaitAsync(ShortTimeout)).Message.Should().Be("transient controller status read");
        if (disposeBeforeRetry)
        {
            rig.Node.Dispose();
            rig.Bus.ErrorFrameSubscriberCount.Should().Be(rig.BaselineErrors);
            rig.Bus.FaultSubscriberCount.Should().Be(rig.BaselineFaults);
            return;
        }
        await rig.Node.SendEmcyAsync(0x1000, 0);
        await ExpectEmcyAsync(rig.Wire, 2, 0x1000, 0);
        Advance(rig.Clock, rig.Node, TimeSpan.FromMilliseconds(51));
        await ExpectEmcyAsync(rig.Wire, 3, 0x8120, Communication);
        rig.Bus.ErrorFrameSubscriberCount.Should().Be(rig.BaselineErrors + 1);
        rig.Bus.FaultSubscriberCount.Should().Be(rig.BaselineFaults + 1);
        rig.Wire.Count(EmcyId).Should().Be(3);
    }

    [Fact]
    public async Task Controller_Emcy_Does_Not_Retry_An_Unexpected_Baseline_Error()
    {
        using var rig = new ControllerEmcyRig();
        rig.Observe(BusState.ErrPassive);
        await ExpectEmcyAsync(rig.Wire, 1, 0x8120, Communication);
        rig.NodeService.NextBusReadFailure = new InvalidOperationException("unexpected baseline failure");
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Node.BackgroundExceptionOccurred += (_, error) => reported.TrySetResult(error);
        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)NmtCommand.ResetCommunication, Device);
        // The frame-dispatch boundary reports the unexpected exception, without a status retry.
        (await reported.Task.WaitAsync(ShortTimeout)).Should().BeOfType<InvalidOperationException>();
        Advance(rig.Clock, rig.Node, TimeSpan.FromMilliseconds(51));
        await rig.Node.SendEmcyAsync(0x1000, 0);
        await ExpectEmcyAsync(rig.Wire, 2, 0x1000, 0);
        rig.Wire.Count(EmcyId).Should().Be(2);
    }

    [Theory]
    [InlineData("CompleteColdReset")]
    [InlineData("CompleteForcedReset")]
    public async Task Controller_Emcy_Unexpected_Read_Does_Not_Interrupt_Self_Reset_Echo_Bookkeeping(string complete)
    {
        using var rig = new ControllerEmcyRig();
        var node = (CanOpenNode)rig.Node;
        int resets = 0;
        node.ApplicationReset += (_, _) => Interlocked.Increment(ref resets);
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        node.BackgroundExceptionOccurred += (_, error) => reported.TrySetResult(error);
        rig.NodeService.NextBusReadFailure = new InvalidOperationException("unexpected reset baseline");
        await node.PostToActorAsync(() =>
        {
            typeof(CanOpenNode).GetField("_coldResetPending", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(node, true);
            typeof(CanOpenNode).GetMethod(complete, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(node, null);
            ((bool)typeof(CanOpenNode).GetField("_ignoreBroadcastResetEcho", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(node)!).Should().BeTrue();
        });
        (await reported.Task.WaitAsync(ShortTimeout)).Should().BeOfType<InvalidOperationException>();
        await Inject(node, CanOpenCobId.NmtCommand, (byte)NmtCommand.ResetCommunication, 0);
        await node.SendEmcyAsync(0x1000, 0);
        await ExpectEmcyAsync(rig.Wire, 1, 0x1000, 0);
        Volatile.Read(ref resets).Should().Be(1, "the broadcast echo must not apply the self-reset again");
    }

    [Fact]
    public async Task Controller_Emcy_Unexpected_Read_Does_Not_Lose_The_Lss_Configure_Reply()
    {
        using var rig = new ControllerEmcyRig(unconfiguredLss: true);
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Node.BackgroundExceptionOccurred += (_, error) => reported.TrySetResult(error);
        await Inject(rig.Node, CanOpenCobId.LssMasterMessage, LssCommand.SwitchStateGlobal, 1, 0, 0, 0, 0, 0, 0);
        rig.NodeService.NextBusReadFailure = new InvalidOperationException("unexpected LSS baseline");
        await Inject(rig.Node, CanOpenCobId.LssMasterMessage, LssCommand.ConfigureNodeId, Device, 0, 0, 0, 0, 0, 0);
        await rig.Wire.WaitForCountAsync(CanOpenCobId.LssSlaveMessage, 1);
        rig.Wire.Payloads(CanOpenCobId.LssSlaveMessage)[0].Should().Equal(LssCommand.ConfigureNodeId, 0, 0, 0, 0, 0, 0, 0);
        (await reported.Task.WaitAsync(ShortTimeout)).Should().BeOfType<InvalidOperationException>();
        rig.Node.NodeId.Should().Be(Device);
        rig.Node.State.Should().Be(NmtState.PreOperational);
    }

    [Fact]
    public async Task Controller_Emcy_Queued_Baseline_Does_Not_Read_After_Actor_Disposal()
    {
        using var rig = new ControllerEmcyRig();
        var node = (CanOpenNode)rig.Node;
        var failure = new InvalidOperationException("must remain unread");
        rig.NodeService.NextBusReadFailure = failure;
        Task? tail = null;
        await node.PostToActorAsync(() =>
        {
            typeof(CanOpenNode).GetMethod("HandleIncoming", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(node, new object[] { CanOpenCobId.NmtCommand,
                    new byte[] { (byte)NmtCommand.ResetCommunication, Device }, false });
            tail = node.PostToActorAsync(() => rig.NodeService.NextBusReadFailure.Should().BeSameAs(failure));
            node.Dispose();
        });
        await tail!.WaitAsync(ShortTimeout);
        rig.Bus.ErrorFrameSubscriberCount.Should().Be(rig.BaselineErrors);
        rig.Bus.FaultSubscriberCount.Should().Be(rig.BaselineFaults);
    }

    [Fact]
    public async Task Controller_Emcy_Lss_Reset_Invalidates_An_Already_Queued_Baseline()
    {
        using var rig = new ControllerEmcyRig(initial: BusState.ErrPassive, unconfiguredLss: true);
        var node = (CanOpenNode)rig.Node;
        await Inject(node, CanOpenCobId.LssMasterMessage, LssCommand.SwitchStateGlobal, 1, 0, 0, 0, 0, 0, 0);
        await node.PostToActorAsync(() =>
        {
            var incoming = typeof(CanOpenNode).GetMethod("HandleIncoming", BindingFlags.Instance | BindingFlags.NonPublic)!;
            incoming.Invoke(node, new object[] { CanOpenCobId.LssMasterMessage,
                new byte[] { LssCommand.ConfigureNodeId, Device, 0, 0, 0, 0, 0, 0 }, false });
            incoming.Invoke(node, new object[] { CanOpenCobId.NmtCommand,
                new byte[] { (byte)NmtCommand.ResetNode, Device }, false });
        });
        Settle(node);
        node.NodeId.Should().Be(CanOpenCobId.UnconfiguredNodeId);
        node.State.Should().Be(NmtState.Initializing);
        await Inject(node, CanOpenCobId.LssMasterMessage, LssCommand.SwitchStateGlobal, 1, 0, 0, 0, 0, 0, 0);
        await Inject(node, CanOpenCobId.LssMasterMessage, LssCommand.ConfigureNodeId, Device, 0, 0, 0, 0, 0, 0);
        await ExpectEmcyAsync(rig.Wire, 1, 0x8120, Communication);
        await node.SendEmcyAsync(0x1000, 0);
        await ExpectEmcyAsync(rig.Wire, 2, 0x1000, Communication);
        rig.Wire.Count(EmcyId).Should().Be(2);
    }

    [Fact]
    public async Task Controller_Emcy_Only_The_Latest_Queued_Reset_May_Read_A_Baseline()
    {
        using var rig = new ControllerEmcyRig();
        var node = (CanOpenNode)rig.Node;
        int reads = Volatile.Read(ref rig.NodeService.BusReads);
        await node.PostToActorAsync(() =>
        {
            var incoming = typeof(CanOpenNode).GetMethod("HandleIncoming", BindingFlags.Instance | BindingFlags.NonPublic)!;
            incoming.Invoke(node, new object[] { CanOpenCobId.NmtCommand,
                new byte[] { (byte)NmtCommand.ResetCommunication, Device }, false });
            incoming.Invoke(node, new object[] { CanOpenCobId.NmtCommand,
                new byte[] { (byte)NmtCommand.ResetCommunication, Device }, false });
        });
        Settle(node);
        Volatile.Read(ref rig.NodeService.BusReads).Should().Be(reads + 1);
        await node.SendEmcyAsync(0x1000, 0);
        await ExpectEmcyAsync(rig.Wire, 1, 0x1000, 0);
    }

    [Fact]
    public async Task Controller_Emcy_Lss_Reset_Invalidates_A_Pending_Baseline_Retry()
    {
        using var rig = new ControllerEmcyRig(unconfiguredLss: true);
        var node = (CanOpenNode)rig.Node;
        await Inject(node, CanOpenCobId.LssMasterMessage, LssCommand.SwitchStateGlobal, 1, 0, 0, 0, 0, 0, 0);
        await Inject(node, CanOpenCobId.LssMasterMessage, LssCommand.ConfigureNodeId, Device, 0, 0, 0, 0, 0, 0);
        Settle(node);
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        node.BackgroundExceptionOccurred += (_, error) => reported.TrySetResult(error);
        rig.Bus.BusState = BusState.ErrPassive;
        rig.NodeService.NextBusReadFailure = new CanKitException(CanKitErrorCode.NativeCallFailed, "reset retry");
        await Inject(node, CanOpenCobId.NmtCommand, (byte)NmtCommand.ResetCommunication, Device);
        (await reported.Task.WaitAsync(ShortTimeout)).Message.Should().Be("reset retry");
        await Inject(node, CanOpenCobId.NmtCommand, (byte)NmtCommand.ResetNode, Device);
        Settle(node);
        int reads = Volatile.Read(ref rig.NodeService.BusReads);
        Advance(rig.Clock, node, TimeSpan.FromMilliseconds(51));
        Volatile.Read(ref rig.NodeService.BusReads).Should().Be(reads);
        node.NodeId.Should().Be(CanOpenCobId.UnconfiguredNodeId);
        await Inject(node, CanOpenCobId.LssMasterMessage, LssCommand.SwitchStateGlobal, 1, 0, 0, 0, 0, 0, 0);
        await Inject(node, CanOpenCobId.LssMasterMessage, LssCommand.ConfigureNodeId, Device, 0, 0, 0, 0, 0, 0);
        await ExpectEmcyAsync(rig.Wire, 1, 0x8120, Communication);
        await node.SendEmcyAsync(0x1000, 0);
        await ExpectEmcyAsync(rig.Wire, 2, 0x1000, Communication);
        rig.Wire.Count(EmcyId).Should().Be(2);
    }

    [Fact]
    public void Controller_Emcy_Monitor_Is_Removed_On_Dispose_And_Failed_Construction()
    {
        using var rig = new ControllerEmcyRig();
        rig.Bus.ErrorFrameSubscriberCount.Should().Be(rig.BaselineErrors + 1);
        rig.Bus.FaultSubscriberCount.Should().Be(rig.BaselineFaults + 1);
        rig.Node.Dispose();
        rig.Bus.ErrorFrameSubscriberCount.Should().Be(rig.BaselineErrors);
        rig.Bus.FaultSubscriberCount.Should().Be(rig.BaselineFaults);
        rig.Service.SubscriptionCount.Should().Be(0);
        var failing = new ThrowingSubscribeService(rig.Service, failOnCall: 1);
        Action open = () => _ = new CanOpenNode(failing, Device,
            new CanOpenNodeOptions { EmitControllerErrorEmcy = true }, ownsService: false, rig.Clock);
        open.Should().Throw<InvalidOperationException>();
        rig.Bus.ErrorFrameSubscriberCount.Should().Be(rig.BaselineErrors);
        rig.Bus.FaultSubscriberCount.Should().Be(rig.BaselineFaults);
        rig.Service.SubscriptionCount.Should().Be(0);
    }

    [Fact]
    public async Task Controller_Emcy_Uses_Fault_Hints_Without_Advancing_The_Poll_Clock()
    {
        using var rig = new ControllerEmcyRig();
        rig.Bus.BusState = BusState.ErrPassive;
        rig.Bus.RaiseFault(new InvalidOperationException("controller hint"));
        Settle(rig.Node);
        await ExpectEmcyAsync(rig.Wire, 1, 0x8120, Communication);
        rig.Clock.GetTimestamp().Should().Be(0);
    }

    [Fact]
    public void Controller_Emcy_Option_Is_Preserved_By_Existing_Copy_Methods()
    {
        var options = new CanOpenNodeOptions { EmitControllerErrorEmcy = true };
        options.With(sdoTimeout: TimeSpan.FromSeconds(2)).EmitControllerErrorEmcy.Should().BeTrue();
        options.WithSrdoConfigurationCheckInterval(null).EmitControllerErrorEmcy.Should().BeTrue();
        options.WithLss(new CanKit.Pro.CANopen.Lss.LssSlaveOptions()).EmitControllerErrorEmcy.Should().BeTrue();
    }
}
