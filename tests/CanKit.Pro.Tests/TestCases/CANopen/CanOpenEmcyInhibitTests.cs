using System;
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

// FR-CO-011: optional CiA 301 EMCY minimum interval and its configuration/lifetime.
public partial class CanOpenPdoEngineTests
{
    private sealed class EmcyInhibitRig : IDisposable
    {
        private readonly ICanBus _forwarder;
        internal readonly ControllableBus Bus;
        internal readonly CanBusService Service;
        internal readonly Wire Wire;
        internal readonly ManualTimeSource Clock = new();
        internal readonly CanOpenNode Node;

        internal EmcyInhibitRig(CanOpenNodeOptions? options = null, CanOpenDeviceDescription? description = null)
        {
            var session = NewSession();
            Bus = ControllableBus.Plain(session);
            _forwarder = Open(session, 1);
            Wire = new Wire(session, 2);
            Bus.OnTransmitting = frame => _forwarder.Transmit(frame);
            Service = new CanBusService(Bus);
            Node = new CanOpenNode(Service, Device,
                options ?? new CanOpenNodeOptions { EnableEmcyInhibit = true }, ownsService: false, Clock, description);
            Settle(Node);
        }

        internal void Configure(ushort units)
        {
            Node.ObjectDictionary.WriteUnsigned(0x1015, 0, units);
            Settle(Node);
        }

        internal async Task WaitForGapAsync()
        {
            var timer = Stopwatch.StartNew();
            var field = typeof(CanOpenNode).GetField("_emcyInhibitWait", BindingFlags.Instance | BindingFlags.NonPublic)!;
            while (!await Node.ActorForTests.PostAsync(() => field.GetValue(Node) is not null))
            {
                if (timer.Elapsed > ShortTimeout) throw new TimeoutException("EMCY never entered the inhibit wait.");
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
    public async Task Emcy_Inhibit_Is_OptIn_And_Zero_Preserves_Immediate_Sends()
    {
        new CanOpenNodeOptions().EnableEmcyInhibit.Should().BeFalse();
        using (var disabled = new EmcyInhibitRig(new CanOpenNodeOptions()))
        {
            disabled.Node.ObjectDictionary.ContainsIndex(0x1015).Should().BeFalse();
            await disabled.Node.SendEmcyAsync(0x1000, 0).WaitAsync(ShortTimeout);
            await disabled.Node.SendEmcyAsync(0x1001, 0).WaitAsync(ShortTimeout);
            await ExpectEmcyAsync(disabled.Wire, 2, 0x1001, 0);
        }
        using var rig = new EmcyInhibitRig();
        rig.Node.ObjectDictionary.ReadUnsigned(0x1015, 0).Should().Be(0);
        await rig.Node.SendEmcyAsync(0x1000, 0).WaitAsync(ShortTimeout);
        await rig.Node.SendEmcyAsync(0x1001, 0).WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 2, 0x1001, 0);
        rig.Clock.GetTimestamp().Should().Be(0);
        new CanOpenNodeOptions { EnableEmcyInhibit = true }.With().EnableEmcyInhibit.Should().BeTrue();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    [InlineData(65535)]
    public async Task Emcy_Inhibit_Enforces_The_Exact_Boundary_And_FIFO_Order(int units)
    {
        using var rig = new EmcyInhibitRig();
        rig.Configure((ushort)units);
        var interval = TimeSpan.FromTicks(units * 1000L);
        await rig.Node.SendEmcyAsync(0x1000, 4).WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 1, 0x1000, 4);
        var second = rig.Node.SendEmcyAsync(0x1001, 8);
        var third = rig.Node.SendEmcyAsync(0x1002, 0);
        await rig.WaitForGapAsync();
        Advance(rig.Clock, rig.Node, interval - TimeSpan.FromTicks(1));
        second.IsCompleted.Should().BeFalse();
        rig.Node.ObjectDictionary.ReadUnsigned(0x1001, 0).Should().Be(4);
        Advance(rig.Clock, rig.Node, TimeSpan.FromTicks(1));
        await second.WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 2, 0x1001, 8);
        await rig.WaitForGapAsync();
        third.IsCompleted.Should().BeFalse();
        Advance(rig.Clock, rig.Node, interval);
        await third.WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 3, 0x1002, 0);
        rig.Wire.Count(EmcyId).Should().Be(3);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(500)]
    [InlineData(2000)]
    public async Task Emcy_Inhibit_Reevaluates_A_Changed_Interval(int replacement)
    {
        using var rig = new EmcyInhibitRig();
        rig.Configure(1000);
        await rig.Node.SendEmcyAsync(0x1000, 0).WaitAsync(ShortTimeout);
        var next = rig.Node.SendEmcyAsync(0x1001, 0);
        await rig.WaitForGapAsync();
        Advance(rig.Clock, rig.Node, TimeSpan.FromMilliseconds(50));
        rig.Configure((ushort)replacement);
        if (replacement > 500)
        {
            await rig.WaitForGapAsync();
            Advance(rig.Clock, rig.Node, TimeSpan.FromMilliseconds(149));
            next.IsCompleted.Should().BeFalse();
            Advance(rig.Clock, rig.Node, TimeSpan.FromMilliseconds(1));
        }
        await next.WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 2, 0x1001, 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Emcy_Inhibit_Cancellation_Does_Not_Consume_A_Send_Slot(bool alreadyCancelled)
    {
        using var rig = new EmcyInhibitRig();
        rig.Configure(1000);
        await rig.Node.SendEmcyAsync(0x1000, 0).WaitAsync(ShortTimeout);
        using var cancellation = new CancellationTokenSource();
        if (alreadyCancelled) cancellation.Cancel();
        var cancelled = rig.Node.SendEmcyAsync(0x1001, 8, cancellationToken: cancellation.Token);
        if (!alreadyCancelled)
        {
            await rig.WaitForGapAsync();
            cancellation.Cancel();
        }
        await FluentActions.Awaiting(() => cancelled.WaitAsync(ShortTimeout)).Should().ThrowAsync<OperationCanceledException>();
        var next = rig.Node.SendEmcyAsync(0x1002, 0);
        await rig.WaitForGapAsync();
        Advance(rig.Clock, rig.Node, TimeSpan.FromMilliseconds(100));
        await next.WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 2, 0x1002, 0);
        rig.Wire.Count(EmcyId).Should().Be(2);
    }

    [Theory]
    [InlineData(NmtCommand.ResetCommunication, false)]
    [InlineData(NmtCommand.ResetNode, false)]
    [InlineData(NmtCommand.ResetCommunication, true)]
    [InlineData(NmtCommand.ResetNode, true)]
    public async Task Emcy_Inhibit_Reset_Drops_Old_Queued_Emcys_And_Starts_A_Fresh_Interval(NmtCommand command, bool factory)
    {
        using var rig = new EmcyInhibitRig();
        rig.Configure(1000);
        rig.Node.StoreParameters();
        if (factory) rig.Node.RestoreDefaultParameters();
        rig.Configure(2000);
        await rig.Node.SendEmcyAsync(0x1000, 0).WaitAsync(ShortTimeout);
        var waiting = rig.Node.SendEmcyAsync(0x1001, 0);
        var queued = rig.Node.SendEmcyAsync(0x1002, 0);
        await rig.WaitForGapAsync();
        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)command, Device);
        await waiting.WaitAsync(ShortTimeout);
        await queued.WaitAsync(ShortTimeout);
        rig.Node.ObjectDictionary.ReadUnsigned(0x1015, 0).Should().Be(factory ? 0u : 1000u);
        await rig.Node.SendEmcyAsync(0x1003, 0).WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 2, 0x1003, 0);
        rig.Wire.Count(EmcyId).Should().Be(2);
    }

    [Fact]
    public async Task Emcy_Inhibit_Disposal_Releases_The_Wait_And_Queued_Callers()
    {
        using var rig = new EmcyInhibitRig();
        rig.Configure(1000);
        await rig.Node.SendEmcyAsync(0x1000, 0).WaitAsync(ShortTimeout);
        var waiting = rig.Node.SendEmcyAsync(0x1001, 0);
        var queued = rig.Node.SendEmcyAsync(0x1002, 0);
        await rig.WaitForGapAsync();
        rig.Node.Dispose();
        await Task.WhenAll(waiting, queued).WaitAsync(ShortTimeout);
        rig.Service.SubscriptionCount.Should().Be(0);
        rig.Wire.Count(EmcyId).Should().Be(1);
    }

    [Fact]
    public async Task Emcy_Inhibit_Stopped_Pauses_Previous_Work_And_Coalesces_New_Reasons()
    {
        using var rig = new EmcyInhibitRig();
        rig.Configure(1000);
        await rig.Node.SendEmcyAsync(0x1000, 0).WaitAsync(ShortTimeout);
        var waiting = rig.Node.SendEmcyAsync(0x1001, 0);
        await rig.WaitForGapAsync();
        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)NmtCommand.Stop, Device);
        await rig.WaitForGapAsync();
        Advance(rig.Clock, rig.Node, TimeSpan.FromMilliseconds(100));
        rig.Configure(1000); // forces re-evaluation after the interval expired, while still Stopped
        await rig.WaitForGapAsync();
        waiting.IsCompleted.Should().BeFalse();
        await rig.Node.SendEmcyAsync(0x1002, 0).WaitAsync(ShortTimeout);
        await rig.Node.SendEmcyAsync(0x1003, 0).WaitAsync(ShortTimeout);
        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)NmtCommand.Start, Device);
        await waiting.WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 2, 0x1001, 0);
        await rig.WaitForGapAsync();
        Advance(rig.Clock, rig.Node, TimeSpan.FromMilliseconds(100));
        await ExpectEmcyAsync(rig.Wire, 3, 0x1003, 0);
        rig.Wire.Count(EmcyId).Should().Be(3);
    }

    [Fact]
    public async Task Emcy_Inhibit_Producer_Disable_Releases_A_Wait_Without_Changing_Application_Bits()
    {
        using var rig = new EmcyInhibitRig();
        rig.Configure(1000);
        await rig.Node.SendEmcyAsync(0x1000, 4).WaitAsync(ShortTimeout);
        var waiting = rig.Node.SendEmcyAsync(0x1001, 8);
        await rig.WaitForGapAsync();
        rig.Node.ObjectDictionary.WriteUnsigned(0x1014, 0, EmcyId | CanOpenCobId.InvalidBit);
        Settle(rig.Node);
        await waiting.WaitAsync(ShortTimeout);
        rig.Node.ObjectDictionary.ReadUnsigned(0x1001, 0).Should().Be(4);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1014, 0, EmcyId);
        var next = rig.Node.SendEmcyAsync(0x1002, 0);
        await rig.WaitForGapAsync();
        Advance(rig.Clock, rig.Node, TimeSpan.FromMilliseconds(100));
        await next.WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 2, 0x1002, 0);
    }

    [Fact]
    public async Task Emcy_Inhibit_Includes_Delayed_Driver_Handoff_In_The_Minimum_Interval()
    {
        using var rig = new EmcyInhibitRig();
        rig.Configure(1000);
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
        var first = rig.Node.SendEmcyAsync(0x1000, 0);
        try
        {
            await entered.Task.WaitAsync(ShortTimeout);
            rig.Clock.Advance(TimeSpan.FromSeconds(1));
        }
        finally { release.Set(); }
        await first.WaitAsync(ShortTimeout);
        var next = rig.Node.SendEmcyAsync(0x1001, 0);
        await rig.WaitForGapAsync();
        Advance(rig.Clock, rig.Node, TimeSpan.FromMilliseconds(99));
        next.IsCompleted.Should().BeFalse();
        Advance(rig.Clock, rig.Node, TimeSpan.FromMilliseconds(1));
        await next.WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 2, 0x1001, 0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(65535)]
    public async Task Emcy_Inhibit_Accepts_Unsigned16_Configuration_Through_Sdo(int units)
    {
        using var rig = new EmcyInhibitRig();
        await Inject(rig.Node, CanOpenCobId.SdoRx(Device),
            SdoFrames.BuildDownloadInit(0x1015, 0, new[] { (byte)units, (byte)(units >> 8) }));
        await rig.Wire.WaitForCountAsync(CanOpenCobId.SdoTx(Device), 1);
        rig.Wire.Payloads(CanOpenCobId.SdoTx(Device))[0][0].Should().Be(0x60);
        rig.Node.ObjectDictionary.ReadUnsigned(0x1015, 0).Should().Be((uint)units);
    }

    [Fact]
    public async Task Emcy_Inhibit_Rejects_A_Wrong_Sdo_Data_Type_Without_Changing_The_Interval()
    {
        using var rig = new EmcyInhibitRig();
        rig.Configure(1000);
        await Inject(rig.Node, CanOpenCobId.SdoRx(Device), SdoFrames.BuildDownloadInit(0x1015, 0, new byte[4]));
        await rig.Wire.WaitForCountAsync(CanOpenCobId.SdoTx(Device), 1);
        var response = rig.Wire.Payloads(CanOpenCobId.SdoTx(Device))[0];
        response[0].Should().Be(0x80);
        BitConverter.ToUInt32(response, 4).Should().Be((uint)SdoAbortCode.LengthTooHigh);
        rig.Node.ObjectDictionary.ReadUnsigned(0x1015, 0).Should().Be(1000);
    }

    [Fact]
    public async Task Emcy_Inhibit_Preserves_Node_Error_Reset_Order_And_Updates_History_Only_When_Sending()
    {
        using var rig = new EmcyInhibitRig(new CanOpenNodeOptions
        {
            EnableEmcyInhibit = true,
            EmitControllerOverrunEmcy = true,
            ErrorHistoryCapacity = 4,
        });
        rig.Configure(1000);
        await rig.Node.SendEmcyAsync(0x1000, 4).WaitAsync(ShortTimeout);
        rig.Bus.RaiseErrorFrame(new StubErrorInfo { ControllerStatus = CanControllerStatus.RxOverflow });
        await rig.WaitForGapAsync();
        rig.Node.AcknowledgeControllerOverrun();
        rig.Node.ObjectDictionary.ReadUnsigned(0x1003, 0).Should().Be(1);
        Advance(rig.Clock, rig.Node, TimeSpan.FromMilliseconds(100));
        await ExpectEmcyAsync(rig.Wire, 2, 0x8110, 0x15);
        await rig.WaitForGapAsync();
        rig.Node.ObjectDictionary.ReadUnsigned(0x1003, 0).Should().Be(2);
        rig.Node.ObjectDictionary.ReadUnsigned(0x1003, 1).Should().Be(0x8110);
        Advance(rig.Clock, rig.Node, TimeSpan.FromMilliseconds(100));
        await ExpectEmcyAsync(rig.Wire, 3, 0, 4);
        rig.Node.ObjectDictionary.ReadUnsigned(0x1003, 0).Should().Be(2);
    }

    [Fact]
    public async Task Emcy_Inhibit_An_Old_Completion_Stamp_Cannot_Rearm_A_Reset_Epoch()
    {
        using var rig = new EmcyInhibitRig();
        rig.Configure(1000);
        rig.Node.StoreParameters();
        await rig.Node.SendEmcyAsync(0x1000, 0).WaitAsync(ShortTimeout);
        var field = typeof(CanOpenNode).GetField("_emcyLastCompletion", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var previous = field.GetValue(rig.Node);
        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)NmtCommand.ResetCommunication, Device);
        // Model an old in-flight send publishing its captured completion after reset.
        await rig.Node.PostToActorAsync(() => field.SetValue(rig.Node, previous));
        await rig.Node.SendEmcyAsync(0x1001, 0).WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 2, 0x1001, 0);
    }

    [Fact]
    public async Task Emcy_Inhibit_A_Failed_Transport_Attempt_Also_Reserves_The_Interval()
    {
        using var rig = new EmcyInhibitRig();
        rig.Configure(1000);
        rig.Bus.AcceptTransmit = false;
        await rig.Node.SendEmcyAsync(0x1000, 0).WaitAsync(ShortTimeout);
        rig.Bus.AcceptTransmit = true;
        var retry = rig.Node.SendEmcyAsync(0x1001, 0);
        await rig.WaitForGapAsync();
        Advance(rig.Clock, rig.Node, TimeSpan.FromMilliseconds(100));
        await retry.WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 1, 0x1001, 0);
    }

    [Fact]
    public async Task Emcy_Inhibit_A_Late_Cancellation_Does_Not_Change_A_Granted_Wait()
    {
        using var rig = new EmcyInhibitRig();
        rig.Configure(1000);
        await rig.Node.SendEmcyAsync(0x1000, 0).WaitAsync(ShortTimeout);
        using var cancellation = new CancellationTokenSource();
        var waiting = rig.Node.SendEmcyAsync(0x1001, 0, cancellationToken: cancellation.Token);
        await rig.WaitForGapAsync();
        await rig.Node.PostToActorAsync(() =>
        {
            Task.Run(cancellation.Cancel).Wait(ShortTimeout).Should().BeTrue(); // cancellation is queued behind this job
            rig.Node.ObjectDictionary.WriteUnsigned(0x1015, 0, 0); // grants/removes the old wait first
        });
        await FluentActions.Awaiting(() => waiting.WaitAsync(ShortTimeout)).Should().ThrowAsync<OperationCanceledException>();
        await rig.Node.SendEmcyAsync(0x1002, 0).WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 2, 0x1002, 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Emcy_Inhibit_Description_Is_Managed_Only_With_The_Capability(bool enabled)
    {
        var description = CanOpenDeviceDescription.ParseEds(@"
[FileInfo]
FileName=inhibit.eds
FileVersion=1
FileRevision=0
EDSVersion=4.0
[DeviceInfo]
VendorName=Tests
VendorNumber=0
ProductName=Inhibit
ProductNumber=0
RevisionNumber=0
[MandatoryObjects]
SupportedObjects=0
[OptionalObjects]
SupportedObjects=1
1=0x1015
[ManufacturerObjects]
SupportedObjects=0
[1015]
ParameterName=EMCY inhibit
ObjectType=0x7
DataType=0x0006
AccessType=rw
DefaultValue=1000
PDOMapping=0
");
        using var rig = new EmcyInhibitRig(new CanOpenNodeOptions { EnableEmcyInhibit = enabled }, description);
        rig.Node.ObjectDictionary.ReadUnsigned(0x1015, 0).Should().Be(1000);
        rig.Node.DeviceDescription!.Findings.Any(f => f.Index == 0x1015 && f.Outcome == DeviceDescriptionOutcome.NotImplemented)
            .Should().Be(!enabled);
        await rig.Node.SendEmcyAsync(0x1000, 0).WaitAsync(ShortTimeout);
        var second = rig.Node.SendEmcyAsync(0x1001, 0);
        if (enabled)
        {
            await rig.WaitForGapAsync();
            Advance(rig.Clock, rig.Node, TimeSpan.FromMilliseconds(100));
        }
        await second.WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 2, 0x1001, 0);
    }
}
