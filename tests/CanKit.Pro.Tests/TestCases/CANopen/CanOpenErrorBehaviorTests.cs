using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

// FR-CO-011, CiA 301 §7.5.2.32: optional communication-error behavior, independent of EMCY.
public partial class CanOpenPdoEngineTests
{
    private sealed class ErrorBehaviorRig : IDisposable
    {
        private readonly ICanBus _forwarder;
        internal readonly ControllableBus Bus;
        internal readonly CanBusService Service;
        internal readonly Wire Wire;
        internal readonly ManualTimeSource Clock = new();
        internal readonly CanOpenNode Node;
        internal readonly int BaselineErrors;
        internal readonly int BaselineFaults;

        internal ErrorBehaviorRig(CanOpenNodeOptions? options = null, BusState initial = BusState.ErrActive,
            CanOpenDeviceDescription? description = null)
        {
            var session = NewSession();
            Bus = ControllableBus.Plain(session);
            Bus.BusState = initial;
            _forwarder = Open(session, 1);
            Wire = new Wire(session, 2);
            Bus.OnTransmitting = frame => _forwarder.Transmit(frame);
            Service = new CanBusService(Bus);
            BaselineErrors = Bus.ErrorFrameSubscriberCount;
            BaselineFaults = Bus.FaultSubscriberCount;
            Node = new CanOpenNode(Service, Device, options ?? new CanOpenNodeOptions
            {
                EnableErrorBehavior = true,
                RespondToNodeGuardingRtr = true,
            }, ownsService: false, Clock, description);
            Settle(Node);
        }

        internal void Configure(byte behavior)
        {
            Node.ObjectDictionary.WriteUnsigned(0x1029, 1, behavior);
            Settle(Node);
        }

        internal Task Command(NmtCommand command) => Inject(Node, CanOpenCobId.NmtCommand, (byte)command, Device);

        internal async Task Fail(int source)
        {
            if (source == 0) Node.AddHeartbeatConsumer(1, TimeSpan.FromMilliseconds(100));
            else if (source == 1)
            {
                ConfigureLifeGuarding(Node);
                await Poll();
            }
            else Bus.BusState = BusState.BusOff;
            Advance(Clock, Node, TimeSpan.FromMilliseconds(101));
        }

        internal async Task Recover(int source)
        {
            if (source == 0) await Inject(Node, CanOpenCobId.Heartbeat(1), (byte)NmtState.PreOperational);
            else if (source == 1) await Poll();
            else
            {
                Bus.BusState = BusState.ErrActive;
                Advance(Clock, Node, TimeSpan.FromMilliseconds(51));
            }
        }

        private async Task Poll()
        {
            int count = Wire.Count(CanOpenCobId.Heartbeat(Device));
            await Node.PostToActorAsync(() =>
                Node.HandleIncoming(CanOpenCobId.Heartbeat(Device), Array.Empty<byte>(), isRtr: true));
            await Wire.WaitForCountAsync(CanOpenCobId.Heartbeat(Device), count + 1);
        }

        internal async Task WaitForEmcyGate()
        {
            var timer = Stopwatch.StartNew();
            var field = typeof(CanOpenNode).GetField("_emcyInhibitWait", BindingFlags.Instance | BindingFlags.NonPublic)!;
            while (!await Node.ActorForTests.PostAsync(() => field.GetValue(Node) is not null))
            {
                if (timer.Elapsed > ShortTimeout) throw new TimeoutException("EMCY did not enter the Stopped wait.");
                await Task.Delay(1);
            }
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

    [Fact]
    public async Task Error_Behavior_Is_OptIn_And_Copies_Preserve_It()
    {
        new CanOpenNodeOptions().EnableErrorBehavior.Should().BeFalse();
        new CanOpenNodeOptions { EnableErrorBehavior = true }.With().EnableErrorBehavior.Should().BeTrue();
        using var rig = new ErrorBehaviorRig(new CanOpenNodeOptions());
        rig.Node.ObjectDictionary.ContainsIndex(0x1029).Should().BeFalse();
        rig.Bus.ErrorFrameSubscriberCount.Should().Be(rig.BaselineErrors);
        rig.Bus.FaultSubscriberCount.Should().Be(rig.BaselineFaults);
        await rig.Command(NmtCommand.Start);
        await rig.Fail(0);
        await rig.Fail(2);
        rig.Node.State.Should().Be(NmtState.Operational);
    }

    [Theory]
    [InlineData(0, 0, NmtState.PreOperational)]
    [InlineData(0, 1, NmtState.Operational)]
    [InlineData(0, 2, NmtState.Stopped)]
    [InlineData(1, 0, NmtState.PreOperational)]
    [InlineData(1, 1, NmtState.Operational)]
    [InlineData(1, 2, NmtState.Stopped)]
    [InlineData(2, 0, NmtState.PreOperational)]
    [InlineData(2, 1, NmtState.Operational)]
    [InlineData(2, 2, NmtState.Stopped)]
    public async Task Error_Behavior_Applies_All_Three_Policies_Without_Emcy(int source, byte behavior, NmtState expected)
    {
        using var rig = new ErrorBehaviorRig();
        rig.Configure(behavior);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1001, 0, 4);
        await rig.Command(NmtCommand.Start);
        await rig.Fail(source);
        rig.Node.State.Should().Be(expected);
        await rig.Command(NmtCommand.Start); // an active failure must be checked again
        rig.Node.State.Should().Be(expected);
        await rig.Recover(source);
        rig.Node.State.Should().Be(expected, "repair does not start the node");
        await rig.Command(NmtCommand.Start);
        rig.Node.State.Should().Be(NmtState.Operational);
        await rig.Node.SendEmcyAsync(0x1000, 4).WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 1, 0x1000, 4);
        rig.Wire.Count(EmcyId).Should().Be(1);
        rig.Wire.Count(CanOpenCobId.NmtCommand).Should().Be(0, "local reactions do not broadcast NMT commands");
    }

    [Theory]
    [InlineData(0, NmtCommand.EnterPreOperational, NmtState.PreOperational)]
    [InlineData(1, NmtCommand.EnterPreOperational, NmtState.PreOperational)]
    [InlineData(2, NmtCommand.EnterPreOperational, NmtState.PreOperational)]
    [InlineData(0, NmtCommand.Stop, NmtState.Stopped)]
    [InlineData(1, NmtCommand.Stop, NmtState.Stopped)]
    [InlineData(2, NmtCommand.Stop, NmtState.Stopped)]
    public async Task Error_Behavior_Does_Not_React_Outside_Operational(int source, NmtCommand command, NmtState expected)
    {
        using var rig = new ErrorBehaviorRig();
        rig.Configure(2);
        await rig.Command(command);
        await rig.Fail(source);
        rig.Node.State.Should().Be(expected);
    }

    [Theory]
    [InlineData(0, NmtState.PreOperational)]
    [InlineData(2, NmtState.Stopped)]
    public async Task Error_Behavior_Policy_Writes_Reevaluate_An_Active_Failure(byte behavior, NmtState expected)
    {
        using var rig = new ErrorBehaviorRig();
        rig.Configure(1);
        await rig.Command(NmtCommand.Start);
        await rig.Fail(0);
        rig.Configure(behavior);
        rig.Node.State.Should().Be(expected);
        rig.Configure(1);
        rig.Node.State.Should().Be(expected, "a policy write does not resume communication");
        await rig.Command(NmtCommand.Start);
        rig.Node.State.Should().Be(NmtState.Operational);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Error_Behavior_Heartbeat_Reconfiguration_Removes_The_Old_Failure(bool remove)
    {
        using var rig = new ErrorBehaviorRig();
        await rig.Command(NmtCommand.Start);
        await rig.Fail(0);
        if (remove) rig.Node.RemoveHeartbeatConsumer(1);
        else rig.Node.AddHeartbeatConsumer(1, TimeSpan.FromMilliseconds(200));
        await rig.Command(NmtCommand.Start);
        rig.Node.State.Should().Be(NmtState.Operational);
    }

    [Fact]
    public async Task Error_Behavior_Keeps_Independent_Heartbeat_Failures_Until_The_Last_Ends()
    {
        using var rig = new ErrorBehaviorRig();
        rig.Node.AddHeartbeatConsumer(2, TimeSpan.FromMilliseconds(100));
        await rig.Command(NmtCommand.Start);
        await rig.Fail(0);
        rig.Node.RemoveHeartbeatConsumer(1);
        await rig.Command(NmtCommand.Start);
        rig.Node.State.Should().Be(NmtState.PreOperational);
        await Inject(rig.Node, CanOpenCobId.Heartbeat(2), (byte)NmtState.PreOperational);
        await rig.Command(NmtCommand.Start);
        rig.Node.State.Should().Be(NmtState.Operational);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Error_Behavior_Disabling_Life_Guarding_Repairs_Its_Failure(bool heartbeat)
    {
        using var rig = new ErrorBehaviorRig();
        await rig.Command(NmtCommand.Start);
        await rig.Fail(1);
        if (heartbeat) rig.Node.StartHeartbeatProducer(TimeSpan.FromSeconds(1));
        else rig.Node.ObjectDictionary.WriteUnsigned(0x100C, 0, 0);
        await rig.Command(NmtCommand.Start);
        rig.Node.State.Should().Be(NmtState.Operational);
    }

    [Theory]
    [InlineData(BusState.ErrPassive)]
    [InlineData(BusState.ErrWarning)]
    [InlineData(BusState.Unknown)]
    public async Task Error_Behavior_Does_Not_Treat_Other_Controller_Readings_As_New_Failures(BusState state)
    {
        using var rig = new ErrorBehaviorRig();
        await rig.Command(NmtCommand.Start);
        rig.Bus.BusState = state;
        Advance(rig.Clock, rig.Node, TimeSpan.FromMilliseconds(51));
        rig.Node.State.Should().Be(NmtState.Operational);
    }

    [Fact]
    public async Task Error_Behavior_Initial_BusOff_And_Unknown_Recovery_Do_Not_Admit_Operational()
    {
        using var rig = new ErrorBehaviorRig(initial: BusState.BusOff);
        await rig.Command(NmtCommand.Start);
        rig.Node.State.Should().Be(NmtState.PreOperational);
        rig.Bus.BusState = BusState.Unknown;
        Advance(rig.Clock, rig.Node, TimeSpan.FromMilliseconds(51));
        await rig.Command(NmtCommand.Start);
        rig.Node.State.Should().Be(NmtState.PreOperational);
        await rig.Recover(2);
        await rig.Command(NmtCommand.Start);
        rig.Node.State.Should().Be(NmtState.Operational);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Error_Behavior_Is_Independent_Of_A_Disabled_Emcy_Producer(int source)
    {
        using var rig = new ErrorBehaviorRig(new CanOpenNodeOptions
        {
            EnableErrorBehavior = true,
            EmitGuardingErrorEmcy = true,
            RespondToNodeGuardingRtr = true,
        });
        rig.Node.ObjectDictionary.WriteUnsigned(0x1014, 0, EmcyId | CanOpenCobId.InvalidBit);
        await rig.Command(NmtCommand.Start);
        await rig.Fail(source);
        rig.Node.State.Should().Be(NmtState.PreOperational);
    }

    [Theory]
    [InlineData(0, 0x7F)]
    [InlineData(2, 4)]
    public async Task Error_Behavior_Uses_State_Change_Heartbeats(byte behavior, byte state)
    {
        using var rig = new ErrorBehaviorRig();
        rig.Configure(behavior);
        await rig.Wire.WaitForCountAsync(CanOpenCobId.Heartbeat(Device), 1);
        rig.Node.StartHeartbeatProducer(TimeSpan.FromSeconds(1));
        await rig.Command(NmtCommand.Start);
        await rig.Wire.WaitForCountAsync(CanOpenCobId.Heartbeat(Device), 2);
        await rig.Fail(0);
        await rig.Wire.WaitForCountAsync(CanOpenCobId.Heartbeat(Device), 3);
        rig.Wire.Payloads(CanOpenCobId.Heartbeat(Device))[2][0].Should().Be(state);
    }

    [Theory]
    [InlineData(NmtCommand.ResetNode, false)]
    [InlineData(NmtCommand.ResetCommunication, false)]
    [InlineData(NmtCommand.ResetNode, true)]
    [InlineData(NmtCommand.ResetCommunication, true)]
    public async Task Error_Behavior_Reset_Restores_Stored_Or_Default_Policy_And_Clears_Old_Failures(NmtCommand reset, bool factory)
    {
        using var rig = new ErrorBehaviorRig();
        rig.Configure(2);
        rig.Node.AddHeartbeatConsumer(1, TimeSpan.FromMilliseconds(100));
        rig.Node.StoreParameters();
        if (factory) rig.Node.RestoreDefaultParameters();
        rig.Configure(1);
        await rig.Command(NmtCommand.Start);
        await rig.Fail(0);
        await rig.Command(reset);
        rig.Node.ObjectDictionary.ReadUnsigned(0x1029, 1).Should().Be(factory ? 0u : 2u);
        await rig.Command(NmtCommand.Start);
        rig.Node.State.Should().Be(NmtState.Operational, "the old timeout is not replayed by Start");
        rig.Node.AddHeartbeatConsumer(1, TimeSpan.FromMilliseconds(100));
        Advance(rig.Clock, rig.Node, TimeSpan.FromMilliseconds(101));
        rig.Node.State.Should().Be(factory ? NmtState.PreOperational : NmtState.Stopped);
    }

    [Fact]
    public async Task Error_Behavior_Reset_Refreshes_Physical_BusOff_And_Detaches_Monitor_On_Disposal()
    {
        using var rig = new ErrorBehaviorRig();
        await rig.Command(NmtCommand.Start);
        await rig.Fail(2);
        rig.Bus.BusState = BusState.ErrActive;
        await rig.Command(NmtCommand.ResetCommunication);
        Settle(rig.Node);
        await rig.Command(NmtCommand.Start);
        rig.Node.State.Should().Be(NmtState.Operational);
        rig.Node.Dispose();
        rig.Bus.ErrorFrameSubscriberCount.Should().Be(rig.BaselineErrors);
        rig.Bus.FaultSubscriberCount.Should().Be(rig.BaselineFaults);
        rig.Service.SubscriptionCount.Should().Be(0);
    }

    [Fact]
    public void Error_Behavior_Declares_A_Fixed_Non_Mappable_Unsigned8_Array()
    {
        using var rig = new ErrorBehaviorRig();
        var od = rig.Node.ObjectDictionary;
        od.ReadUnsigned(0x1029, 0).Should().Be(1);
        od.ReadUnsigned(0x1029, 1).Should().Be(0);
        od.TryGet(0x1029, 1, out var entry).Should().BeTrue();
        entry.PdoMappable.Should().BeFalse();
        FluentActions.Invoking(() => od.WriteUnsigned(0x1029, 0, 2)).Should().Throw<ArgumentException>().WithMessage("*06010002*");
        FluentActions.Invoking(() => od.AddU8(0x1029, 1, 1)).Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Error_Behavior_Accepts_Unsigned8_Sdo_Writes(byte behavior)
    {
        using var rig = new ErrorBehaviorRig();
        await Inject(rig.Node, CanOpenCobId.SdoRx(Device), SdoFrames.BuildDownloadInit(0x1029, 1, new[] { behavior }));
        await rig.Wire.WaitForCountAsync(CanOpenCobId.SdoTx(Device), 1);
        rig.Wire.Payloads(CanOpenCobId.SdoTx(Device))[0][0].Should().Be(0x60);
        rig.Node.ObjectDictionary.ReadUnsigned(0x1029, 1).Should().Be(behavior);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(255)]
    public async Task Error_Behavior_Rejects_Reserved_And_Undefined_Manufacturer_Values(byte behavior)
    {
        using var rig = new ErrorBehaviorRig();
        FluentActions.Invoking(() => rig.Node.ObjectDictionary.WriteUnsigned(0x1029, 1, behavior))
            .Should().Throw<ArgumentException>().WithMessage("*06090030*");
        await Inject(rig.Node, CanOpenCobId.SdoRx(Device), SdoFrames.BuildDownloadInit(0x1029, 1, new[] { behavior }));
        await rig.Wire.WaitForCountAsync(CanOpenCobId.SdoTx(Device), 1);
        BitConverter.ToUInt32(rig.Wire.Payloads(CanOpenCobId.SdoTx(Device))[0], 4).Should().Be((uint)SdoAbortCode.ValueRangeExceeded);
        rig.Node.ObjectDictionary.ReadUnsigned(0x1029, 1).Should().Be(0);
    }

    [Theory]
    [InlineData(0, 1, SdoAbortCode.AttemptWriteReadOnly)]
    [InlineData(1, 2, SdoAbortCode.LengthTooHigh)]
    [InlineData(2, 1, SdoAbortCode.SubIndexDoesNotExist)]
    public async Task Error_Behavior_Sdo_Rejects_Count_Width_And_Unsupported_Classes(byte subindex, int length, SdoAbortCode abort)
    {
        using var rig = new ErrorBehaviorRig();
        await Inject(rig.Node, CanOpenCobId.SdoRx(Device), SdoFrames.BuildDownloadInit(0x1029, subindex, new byte[length]));
        await rig.Wire.WaitForCountAsync(CanOpenCobId.SdoTx(Device), 1);
        BitConverter.ToUInt32(rig.Wire.Payloads(CanOpenCobId.SdoTx(Device))[0], 4).Should().Be((uint)abort);
    }

    [Fact]
    public async Task Error_Behavior_BusOff_Hints_React_Without_Advancing_The_Poll_Clock()
    {
        using var rig = new ErrorBehaviorRig();
        await rig.Command(NmtCommand.Start);
        rig.Bus.BusState = BusState.BusOff;
        rig.Bus.RaiseErrorFrame();
        Settle(rig.Node);
        rig.Node.State.Should().Be(NmtState.PreOperational);
        rig.Clock.GetTimestamp().Should().Be(0);
    }

    [Fact]
    public async Task Error_Behavior_Stopped_Aborts_An_Active_Sdo_Server_Transfer()
    {
        using var rig = new ErrorBehaviorRig();
        rig.Configure(2);
        rig.Node.ObjectDictionary.AddDomain(0x2000, 0, new byte[12]);
        await rig.Command(NmtCommand.Start);
        await Inject(rig.Node, CanOpenCobId.SdoRx(Device), SdoFrames.BuildDownloadInit(0x2000, 0, new byte[12]));
        await rig.Wire.WaitForCountAsync(CanOpenCobId.SdoTx(Device), 1);
        await rig.Fail(0);
        await rig.Wire.WaitForCountAsync(CanOpenCobId.SdoTx(Device), 2);
        var abort = rig.Wire.Payloads(CanOpenCobId.SdoTx(Device))[1];
        abort[0].Should().Be(0x80);
        BitConverter.ToUInt32(abort, 4).Should().Be((uint)SdoAbortCode.DataCannotBeTransferredDeviceState);
    }

    [Theory]
    [InlineData(0)] // leave Stopped, transmit queued application then latest held application
    [InlineData(1)] // dispose while waiting
    [InlineData(2)] // reset while waiting
    public async Task Error_Behavior_Pauses_Previously_Queued_Emcys_Without_Inhibit_And_Releases_Waits(int ending)
    {
        using var rig = new ErrorBehaviorRig(new CanOpenNodeOptions
        {
            EnableErrorBehavior = true,
            EmitGuardingErrorEmcy = true,
        });
        rig.Configure(2);
        await rig.Command(NmtCommand.Start);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var forward = rig.Bus.OnTransmitting!;
        rig.Bus.OnTransmitting = frame =>
        {
            if ((uint)frame.ID == EmcyId && frame.Data.Span[0] == 0)
            {
                entered.TrySetResult(true);
                release.Wait(ShortTimeout);
            }
            forward(frame);
        };
        var first = rig.Node.SendEmcyAsync(0x1000, 4);
        Task waiting;
        try
        {
            await entered.Task.WaitAsync(ShortTimeout);
            waiting = rig.Node.SendEmcyAsync(0x1001, 8);
            Settle(rig.Node);
            await rig.Fail(0);
        }
        finally { release.Set(); }
        await first.WaitAsync(ShortTimeout);
        await rig.WaitForEmcyGate();
        if (ending == 0) await rig.Node.SendEmcyAsync(0x1002, 4).WaitAsync(ShortTimeout);
        if (ending == 1) rig.Node.Dispose();
        else await rig.Command(ending == 2 ? NmtCommand.ResetCommunication : NmtCommand.EnterPreOperational);
        await waiting.WaitAsync(ShortTimeout);
        if (ending == 0)
        {
            await ExpectEmcyAsync(rig.Wire, 2, 0x1001, 8);
            await ExpectEmcyAsync(rig.Wire, 3, 0x1002, 0x15);
            rig.Wire.Count(EmcyId).Should().Be(3);
        }
        else
        {
            await ExpectEmcyAsync(rig.Wire, 1, 0x1000, 4);
            rig.Wire.Count(EmcyId).Should().Be(1);
        }
    }

    private static CanOpenDeviceDescription ErrorBehaviorDescription(bool dcf, int value = 1, int count = 1, string access = "rw")
    {
        string content = @"
[FileInfo]
FileName=behavior.eds
FileVersion=1
FileRevision=0
EDSVersion=4.0
[DeviceInfo]
VendorName=Tests
VendorNumber=0
ProductName=Behavior
ProductNumber=0
RevisionNumber=0
[MandatoryObjects]
SupportedObjects=0
[OptionalObjects]
SupportedObjects=1
1=0x1029
[ManufacturerObjects]
SupportedObjects=0
[1029]
ParameterName=Error behavior
ObjectType=0x8
SubNumber=2
[1029sub0]
ParameterName=Count
DataType=0x0005
AccessType=const
DefaultValue=" + count + @"
PDOMapping=0
[1029sub1]
ParameterName=Communication error
DataType=0x0005
AccessType=" + access + @"
DefaultValue=" + value + @"
PDOMapping=1
";
        if (dcf) content += "ParameterValue=2\n";
        if (count > 1) content += @"
[1029sub2]
ParameterName=Unsupported class
DataType=0x0005
AccessType=rw
DefaultValue=0
PDOMapping=0
";
        return dcf ? CanOpenDeviceDescription.ParseDcf(content) : CanOpenDeviceDescription.ParseEds(content);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Error_Behavior_Descriptions_Only_Activate_When_Opted_In(bool enabled, bool dcf)
    {
        using var rig = new ErrorBehaviorRig(new CanOpenNodeOptions { EnableErrorBehavior = enabled },
            description: ErrorBehaviorDescription(dcf));
        rig.Node.ObjectDictionary.ReadUnsigned(0x1029, 1).Should().Be(dcf ? 2u : 1u);
        rig.Node.DeviceDescription!.Findings.Any(f => f.Index == 0x1029 && f.Outcome == DeviceDescriptionOutcome.NotImplemented)
            .Should().Be(!enabled);
        await rig.Command(NmtCommand.Start);
        await rig.Fail(0);
        rig.Node.State.Should().Be(enabled && dcf ? NmtState.Stopped : NmtState.Operational);
    }

    [Fact]
    public void Error_Behavior_Description_Keeps_The_Fixed_Count_And_Omits_Unsupported_Classes()
    {
        using var rig = new ErrorBehaviorRig(description: ErrorBehaviorDescription(false, count: 2));
        rig.Node.ObjectDictionary.ReadUnsigned(0x1029, 0).Should().Be(1);
        rig.Node.ObjectDictionary.TryGet(0x1029, 2, out _).Should().BeFalse();
        rig.Node.DeviceDescription!.Findings.Should().Contain(f => f.Index == 0x1029 && f.Subindex == 0 && f.Outcome == DeviceDescriptionOutcome.Corrected);
        rig.Node.DeviceDescription.Findings.Should().Contain(f => f.Index == 0x1029 && f.Subindex == 2 && f.Outcome == DeviceDescriptionOutcome.Omitted);
        rig.Node.ObjectDictionary.TryGet(0x1029, 1, out var entry).Should().BeTrue();
        entry.PdoMappable.Should().BeFalse();
    }

    [Fact]
    public void Error_Behavior_Description_Invalid_Policy_Keeps_Default_And_Reports_It()
    {
        using var rig = new ErrorBehaviorRig(description: ErrorBehaviorDescription(false, value: 3));
        rig.Node.ObjectDictionary.ReadUnsigned(0x1029, 1).Should().Be(0);
        rig.Node.DeviceDescription!.Findings.Should().Contain(f => f.Index == 0x1029 && f.Subindex == 1 && f.Outcome == DeviceDescriptionOutcome.Corrected);
    }

    [Fact]
    public async Task Error_Behavior_Description_May_Make_Communication_Policy_Constant()
    {
        using var rig = new ErrorBehaviorRig(description: ErrorBehaviorDescription(false, access: "const"));
        await Inject(rig.Node, CanOpenCobId.SdoRx(Device), SdoFrames.BuildDownloadInit(0x1029, 1, new byte[] { 0 }));
        await rig.Wire.WaitForCountAsync(CanOpenCobId.SdoTx(Device), 1);
        BitConverter.ToUInt32(rig.Wire.Payloads(CanOpenCobId.SdoTx(Device))[0], 4).Should().Be((uint)SdoAbortCode.AttemptWriteReadOnly);
        rig.Node.ObjectDictionary.ReadUnsigned(0x1029, 1).Should().Be(1);
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(0, 2, true)]
    [InlineData(1, 0, true)]
    [InlineData(1, 2, true)]
    [InlineData(2, 0, true)]
    [InlineData(2, 2, true)]
    [InlineData(0, 2, false)]
    [InlineData(1, 2, false)]
    [InlineData(2, 2, false)]
    public async Task Error_Behavior_Sdo_Policy_Commit_Has_Exactly_One_Terminal_Response(int protocol, byte behavior, bool enabled)
    {
        using var rig = new ErrorBehaviorRig(new CanOpenNodeOptions { EnableErrorBehavior = enabled });
        if (!enabled)
        {
            rig.Node.ObjectDictionary.AddU8(0x1029, 0, 1);
            rig.Node.ObjectDictionary.AddU8(0x1029, 1, 1);
        }
        rig.Configure(1);
        await rig.Command(NmtCommand.Start);
        await rig.Fail(0);
        var responses = new ConcurrentQueue<byte>();
        var forward = rig.Bus.OnTransmitting!;
        rig.Bus.OnTransmitting = frame =>
        {
            if ((uint)frame.ID == CanOpenCobId.SdoTx(Device)) responses.Enqueue(frame.Data.Span[0]);
            forward(frame);
        };
        int preceding = 0;
        byte expected;
        if (protocol == 0)
        {
            await Inject(rig.Node, CanOpenCobId.SdoRx(Device), SdoFrames.BuildDownloadInit(0x1029, 1, new[] { behavior }));
            expected = 0x60;
        }
        else if (protocol == 1)
        {
            await Inject(rig.Node, CanOpenCobId.SdoRx(Device), new byte[] { 0x21, 0x29, 0x10, 1, 1, 0, 0, 0 });
            await Drain();
            preceding = responses.Count;
            await Inject(rig.Node, CanOpenCobId.SdoRx(Device), new byte[] { 0x0D, behavior, 0, 0, 0, 0, 0, 0 });
            expected = 0x20;
        }
        else
        {
            await Inject(rig.Node, CanOpenCobId.SdoRx(Device), SdoBlockFrames.BuildBlockDownloadInit(0x1029, 1, false, true, 1));
            await Drain();
            await Inject(rig.Node, CanOpenCobId.SdoRx(Device), SdoBlockFrames.BuildSegment(1, true, new[] { behavior }));
            await Drain();
            preceding = responses.Count;
            await Inject(rig.Node, CanOpenCobId.SdoRx(Device), SdoBlockFrames.BuildEnd(SdoBlockFrames.CcsBlockDownloadEndBase, 6, 0));
            expected = 0xA1;
        }
        await Drain();
        responses.Skip(preceding).Should().Equal(new[] { expected }, "a committed write must not abort itself as an unfinished session");
        rig.Node.ObjectDictionary.ReadUnsigned(0x1029, 1).Should().Be(behavior);
        rig.Node.State.Should().Be(!enabled ? NmtState.Operational : behavior == 2 ? NmtState.Stopped : NmtState.PreOperational);

        async Task Drain()
        {
            var timer = Stopwatch.StartNew();
            // Every response has reserved its transport in the completed actor dispatch.
            // The counter reaches zero only after all driver handoffs/recordings finished.
            while (rig.Node.HasLssTransmitsInFlight)
            {
                if (timer.Elapsed > ShortTimeout) throw new TimeoutException("SDO responses did not drain.");
                await Task.Delay(1);
            }
        }
    }
}
