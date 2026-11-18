using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Cia401;
using CanKit.Pro.CANopen.Nmt;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Cia401;

/// <summary>
/// The CiA 401 device with analog outputs: what reaches the terminals from RPDO 2 and from SDO, the
/// error state they share with the digital outputs and what leaves it, the error mode and error value,
/// and what the application is told (CiA 401-1 v3.1.0 §5.2, §6.2.7, §7.2.5 Figure 7, §8.5.2, §8.7.4,
/// §8.7.5). Indices, defaults and abort codes are written out from the norm here, not taken from
/// <see cref="Cia401Objects"/>.
/// </summary>
/// <remarks>
/// The readings the tests pin, each argued on the device: the error value <c>6444h</c> is an INTEGER32
/// left adjusted (Figure 7's legend; §8.7.5 says nothing of it), so the 16-bit output takes its upper
/// word and ignores the lower; the error state is the one of the digital outputs (Figure 7 has the
/// structure of Figure 5): writes received in it are stored in <c>6411h</c> and reach no terminal,
/// and leaving it moves nothing until the next accepted write.
/// </remarks>
public class Cia401DeviceAnalogOutputTests
{
    private const uint ValueRangeExceeded = 0x06090030;

    private static readonly TimeSpan ConsumerTimeout = TimeSpan.FromMilliseconds(100);

    private static Cia401Configuration AnalogOut(int channels, int digitalOutputs = 0)
        => new() { AnalogOutputs = channels, DigitalOutputs = digitalOutputs };

    /// <summary>A 16-bit value as <c>6444h</c> holds it: left adjusted in 32 bits.</summary>
    private static int LeftAdjusted(short value) => value << 16;

    /// <summary>Records every <see cref="Cia401Device.OutputsChanged"/>, whether it was raised on the
    /// device node's actor loop, and every set of analog levels the device published.</summary>
    private sealed class Recorder
    {
        private readonly object _gate = new();
        private readonly List<(Cia401OutputsChangedEventArgs Args, bool OnActor)> _seen = new();
        private readonly List<short[]> _published = new();

        internal Recorder(Cia401Rig rig)
        {
            rig.Device.OutputsChanged += (_, e) =>
            {
                lock (_gate) _seen.Add((e, rig.Node.ActorForTests.IsOnCurrentActor));
            };
            rig.Device.AnalogLevelsPublishedForTests += levels =>
            {
                lock (_gate) _published.Add(levels.ToArray());
            };
        }

        internal IReadOnlyList<short[]> Published
        {
            get { lock (_gate) return _published.ToList(); }
        }

        internal IReadOnlyList<Cia401OutputsChangedEventArgs> Events
        {
            get { lock (_gate) return _seen.Select(s => s.Args).ToList(); }
        }

        internal bool AllOnActor
        {
            get { lock (_gate) return _seen.All(s => s.OnActor); }
        }
    }

    /// <summary>A heartbeat consumer for node 1 whose producer is silent, and the clock moved past
    /// its time: a communication failure (CiA 401-1 §5.2).</summary>
    private static void HeartbeatTimeout(Cia401Rig rig)
    {
        rig.Node.AddHeartbeatConsumer(0x01, ConsumerTimeout);
        rig.Advance(ConsumerTimeout + TimeSpan.FromMilliseconds(1));
    }

    /// <summary>The producer of <see cref="HeartbeatTimeout"/> is heard again: the failure ends.</summary>
    private static async Task HeartbeatResumes(Cia401Rig rig)
    {
        rig.Transmit(0x701, new byte[] { 0x05 });
        await rig.FenceAsync();
    }

    private static void ReportDeviceFailure(Cia401Rig rig, bool active)
    {
        rig.Device.ReportDeviceFailure(active);
        rig.Settle();
    }

    private static short[] Terminals(Cia401Rig rig, int channels)
        => Enumerable.Range(1, channels).Select(c => rig.Device.ReadAnalogOutput(c)).ToArray();

    // ---- attach --------------------------------------------------------------------------------

    [Fact]
    public void Attach_Declares_The_Analog_Output_Objects_With_Their_Defaults()
    {
        using var rig = new Cia401Rig(AnalogOut(6));
        var od = rig.Od;

        foreach (ushort index in new ushort[] { 0x6411, 0x6443, 0x6444 })
        {
            od.ReadUnsigned(index, 0x00).Should().Be(6u, $"{index:X4}h has one sub-index per channel (§8.5.2, §8.7.4, §8.7.5)");
            od.TryGet(index, 0x00, out var count).Should().BeTrue();
            count.Access.Should().Be(OdAccess.ReadOnly, "the highest sub-index supported is const");
            od.TryGet(index, 0x07, out _).Should().BeFalse();
            for (byte sub = 1; sub <= 6; sub++)
            {
                od.TryGet(index, sub, out var entry).Should().BeTrue();
                entry.Access.Should().Be(OdAccess.ReadWrite, $"{index:X4}h is rw");
            }
        }

        od.TryGet(0x6411, 0x01, out var write).Should().BeTrue();
        write.DataType.Should().Be(OdDataType.Integer16);
        write.PdoMappable.Should().BeTrue("6411h maps into an RPDO by default (§8.5.2)");
        od.TryGet(0x6443, 0x01, out var mode).Should().BeTrue();
        mode.DataType.Should().Be(OdDataType.Unsigned8);
        od.TryGet(0x6444, 0x01, out var value).Should().BeTrue();
        value.DataType.Should().Be(OdDataType.Integer32);
        for (byte sub = 1; sub <= 6; sub++)
        {
            od.ReadSigned(0x6411, sub).Should().Be(0, "write analog output defaults to 0000h (§8.5.2)");
            od.ReadUnsigned(0x6443, sub).Should().Be(0x01u, "error mode defaults to 01h (§8.7.4)");
            od.ReadSigned(0x6444, sub).Should().Be(0, "error value defaults to 0 (§8.7.5)");
        }

        od.ReadUnsigned(0x1000, 0x00).Should().Be(0x00080191u, "profile 401 with analog outputs, bit 19 (§6.2.1)");
        od.ReadUnsigned(0x1401, 0x01).Should().Be(Cia401Rig.Rpdo2, "RPDO 2 valid on its pre-defined COB-ID (§6.2.7)");
        od.ReadUnsigned(0x1601, 0x00).Should().Be(4u);
        od.ReadUnsigned(0x1601, 0x01).Should().Be(0x64110110u);
        od.ReadUnsigned(0x1601, 0x04).Should().Be(0x64110410u);
        od.ReadUnsigned(0x1602, 0x00).Should().Be(2u, "RPDO 3 carries channels 5 and 6 (§6.2.9)");
        od.ReadUnsigned(0x1602, 0x02).Should().Be(0x64110610u);
        Terminals(rig, 6).Should().OnlyContain(v => v == 0);
        rig.Device.OutputsInErrorState.Should().BeFalse();
    }

    // ---- the path to the terminal --------------------------------------------------------------

    [Fact]
    public async Task An_Rpdo_Sets_The_Terminals_And_Raises_OutputsChanged_Once()
    {
        using var rig = new Cia401Rig(AnalogOut(4));
        var recorder = new Recorder(rig);
        await rig.StartAsync();

        await rig.SendRpdo2Async(Cia401Rig.Words(0x1234, -2, 0, 0));

        Terminals(rig, 4).Should().Equal(0x1234, -2, 0, 0);
        (await rig.ReadI16Async(0x6411, 0x02)).Should().Be(-2);
        var raised = recorder.Events.Should().ContainSingle().Which;
        raised.Block.Should().Be(Cia401FunctionBlock.AnalogOutput);
        raised.Group.Should().Be(1, "for an analog output the sub-index of 6411h is the channel");
        raised.Channel.Should().Be(1, "channel 1 is the lowest that changed");
        raised.AnalogLevels.Should().Equal(0x1234, -2, 0, 0);
        raised.Levels.Should().BeEmpty("the device has no digital outputs");
        recorder.AllOnActor.Should().BeTrue("OutputsChanged is raised on the actor loop");
    }

    [Fact]
    public async Task An_Sdo_Write_Sets_The_Terminal_The_Same_Way()
    {
        using var rig = new Cia401Rig(AnalogOut(4));
        var recorder = new Recorder(rig);

        await rig.WriteI16Async(0x6411, 0x03, 500);
        rig.Settle();

        rig.Device.ReadAnalogOutput(3).Should().Be(500);
        var raised = recorder.Events.Should().ContainSingle().Which;
        raised.Channel.Should().Be(3);
        raised.Group.Should().Be(3);
        raised.AnalogLevels.Should().Equal(0, 0, 500, 0);
    }

    [Fact]
    public async Task Channel_5_Is_Carried_By_Rpdo_3()
    {
        using var rig = new Cia401Rig(AnalogOut(5));
        await rig.StartAsync();

        rig.Transmit(0x400 + Cia401Rig.DeviceId, Cia401Rig.Words(77));
        await rig.FenceAsync();

        rig.Device.ReadAnalogOutput(5).Should().Be(77, "RPDO 3 carries analog outputs 5 to 8 (§6.2.9)");
    }

    [Fact]
    public async Task A_Write_That_Changes_No_Level_Raises_Nothing()
    {
        using var rig = new Cia401Rig(AnalogOut(4));
        var recorder = new Recorder(rig);
        await rig.StartAsync();

        await rig.SendRpdo2Async(Cia401Rig.Words(10, 20, 30, 40));
        await rig.SendRpdo2Async(Cia401Rig.Words(10, 20, 30, 40));
        await rig.WriteU8Async(0x6443, 0x01, 0x00);
        await rig.WriteI32Async(0x6444, 0x01, LeftAdjusted(99));
        rig.Settle();

        recorder.Events.Should().ContainSingle("only the first write changed a terminal");
    }

    [Fact] // one OnEntriesWritten, one set of levels
    public async Task Writes_Told_In_One_Callback_Publish_Only_The_Final_Levels()
    {
        using var rig = new Cia401Rig(AnalogOut(2));
        var recorder = new Recorder(rig);

        await rig.Node.PostToActorAsync(() =>
        {
            rig.Od.WriteRaw(0x6411, 0x01, new byte[] { 0x01, 0x00 });
            rig.Od.WriteRaw(0x6411, 0x02, new byte[] { 0x02, 0x00 });
        });
        rig.Settle();

        recorder.Published.Should().ContainSingle("no intermediate level is published")
            .Which.Should().Equal(1, 2);
        var raised = recorder.Events.Should().ContainSingle().Which;
        raised.Channel.Should().Be(1);
        raised.AnalogLevels.Should().Equal(1, 2);
    }

    // ---- the error state -------------------------------------------------------------------------

    [Fact]
    public async Task A_Heartbeat_Timeout_Puts_The_Analog_Outputs_In_Their_Error_State()
    {
        using var rig = new Cia401Rig(AnalogOut(2));
        var recorder = new Recorder(rig);
        await rig.WriteU8Async(0x6443, 0x02, 0x00);
        await rig.WriteI32Async(0x6444, 0x01, 0x1234_0000);
        await rig.StartAsync();
        await rig.SendRpdo2Async(Cia401Rig.Words(100, 200));

        HeartbeatTimeout(rig);

        rig.Device.OutputsInErrorState.Should().BeTrue();
        rig.Device.ReadAnalogOutput(1).Should().Be(0x1234, "6443h:01 = 1: the upper word of 6444h:01 (§8.7.4, §8.7.5, Figure 7)");
        rig.Device.ReadAnalogOutput(2).Should().Be(200, "6443h:02 = 0: the value is kept (§8.7.4)");
        rig.Node.State.Should().Be(NmtState.PreOperational, "the node acts on 1029h:01 = 0 itself");
        (await rig.ReadI16Async(0x6411, 0x01)).Should().Be(100, "6411h keeps what was written (Figure 7)");
        recorder.Events.Should().HaveCount(2);
        recorder.Events[1].Block.Should().Be(Cia401FunctionBlock.AnalogOutput);
        recorder.Events[1].Channel.Should().Be(1);
        recorder.Events[1].AnalogLevels.Should().Equal(0x1234, 200);
    }

    [Fact] // controller ruling 1: the upper word decides, the lower is dropped
    public async Task The_Error_Value_Is_The_Upper_Word_Of_6444h_And_Its_Low_Word_Is_Ignored()
    {
        using var rig = new Cia401Rig(AnalogOut(3));
        await rig.WriteI32Async(0x6444, 0x01, 0x1234_5678);
        await rig.WriteI32Async(0x6444, 0x02, unchecked((int)0xFFFF_FFFFu));
        await rig.WriteI32Async(0x6444, 0x03, 0x0000_7FFF);
        await rig.StartAsync();
        await rig.SendRpdo2Async(Cia401Rig.Words(5, 5, 5));

        await rig.StopAsync();

        rig.Device.ReadAnalogOutput(1).Should().Be(0x1234, "the low word 5678h is a fraction a 16-bit output cannot show");
        rig.Device.ReadAnalogOutput(2).Should().Be(-1, "FFFFh, the upper word, is -1");
        rig.Device.ReadAnalogOutput(3).Should().Be(0, "a value below one step of the 16-bit output is 0");
    }

    [Fact]
    public async Task Stopped_Puts_The_Analog_Outputs_In_Their_Error_State()
    {
        using var rig = new Cia401Rig(AnalogOut(1));
        var recorder = new Recorder(rig);
        await rig.WriteI32Async(0x6444, 0x01, LeftAdjusted(-300));
        await rig.StartAsync();
        await rig.SendRpdo2Async(Cia401Rig.Words(1000));

        await rig.StopAsync();

        rig.Device.OutputsInErrorState.Should().BeTrue("a 'Stop remote node' indication (§8.7.4)");
        rig.Device.ReadAnalogOutput(1).Should().Be(-300);
        recorder.Events.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_Device_Failure_Puts_The_Analog_Outputs_In_Their_Error_State()
    {
        using var rig = new Cia401Rig(AnalogOut(1));
        await rig.WriteU8Async(0x1029, 0x01, 0x01);
        await rig.WriteI32Async(0x6444, 0x01, LeftAdjusted(42));
        await rig.StartAsync();
        await rig.SendRpdo2Async(Cia401Rig.Words(1000));

        ReportDeviceFailure(rig, true);

        rig.Device.OutputsInErrorState.Should().BeTrue("an internal device failure (§8.7.4)");
        rig.Device.ReadAnalogOutput(1).Should().Be(42);
        rig.Node.State.Should().Be(NmtState.Operational, "1029h:01 = 1");

        ReportDeviceFailure(rig, false);
        rig.Device.ReadAnalogOutput(1).Should().Be(42, "the end of the failure moves nothing");
        await rig.SendRpdo2Async(Cia401Rig.Words(1000));
        rig.Device.ReadAnalogOutput(1).Should().Be(1000, "the next accepted write does");
    }

    [Fact]
    public async Task Writes_In_The_Error_State_Are_Stored_In_6411h_And_Reach_No_Terminal()
    {
        using var rig = new Cia401Rig(AnalogOut(2));
        await rig.WriteU8Async(0x1029, 0x01, 0x01);
        await rig.WriteU8Async(0x6443, 0x02, 0x00);
        await rig.WriteI32Async(0x6444, 0x01, LeftAdjusted(7));
        await rig.StartAsync();
        await rig.SendRpdo2Async(Cia401Rig.Words(100, 200));
        HeartbeatTimeout(rig);

        await rig.SendRpdo2Async(Cia401Rig.Words(111, 222));

        Terminals(rig, 2).Should().Equal(new short[] { 7, 200 },
            "the error switch takes 6411h off the path, also for a channel that keeps its value (Figure 7)");
        (await rig.ReadI16Async(0x6411, 0x01)).Should().Be(111);
        (await rig.ReadI16Async(0x6411, 0x02)).Should().Be(222);
    }

    [Fact]
    public async Task When_The_Failure_Ends_Nothing_Moves_Until_The_Next_Write()
    {
        using var rig = new Cia401Rig(AnalogOut(1));
        var recorder = new Recorder(rig);
        await rig.WriteI32Async(0x6444, 0x01, LeftAdjusted(7));
        await rig.StartAsync();
        await rig.SendRpdo2Async(Cia401Rig.Words(100));
        HeartbeatTimeout(rig);

        await HeartbeatResumes(rig);

        rig.Device.OutputsInErrorState.Should().BeFalse();
        rig.Device.ReadAnalogOutput(1).Should().Be(7, "leaving the error state restores nothing by itself");
        recorder.Events.Should().HaveCount(2);

        await rig.StartAsync();
        await rig.SendRpdo2Async(Cia401Rig.Words(100)); // the value 6411h holds already
        rig.Device.ReadAnalogOutput(1).Should().Be(100, "the next accepted write sets the output");
        recorder.Events.Should().HaveCount(3);
    }

    [Fact] // Review Focus 2, for the analog outputs
    public async Task An_Output_The_Master_Never_Wrote_Stays_At_Its_Error_Value_When_The_Failure_Ends()
    {
        using var rig = new Cia401Rig(AnalogOut(1));
        var recorder = new Recorder(rig);
        await rig.WriteI32Async(0x6444, 0x01, LeftAdjusted(0x0100));
        await rig.StartAsync();

        HeartbeatTimeout(rig);
        rig.Device.ReadAnalogOutput(1).Should().Be(0x0100);

        await HeartbeatResumes(rig);
        await rig.StartAsync();
        rig.Settle();

        rig.Device.OutputsInErrorState.Should().BeFalse();
        rig.Device.ReadAnalogOutput(1).Should().Be(0x0100, "6411h was never written: nothing tells the output to move");
        recorder.Events.Should().ContainSingle("only entering the error state moved the output");
    }

    [Fact]
    public async Task Error_Mode_And_Value_Written_In_The_Error_State_Apply_At_Once()
    {
        using var rig = new Cia401Rig(AnalogOut(2));
        await rig.WriteU8Async(0x1029, 0x01, 0x01);
        await rig.WriteU8Async(0x6443, 0x02, 0x00);
        await rig.StartAsync();
        await rig.SendRpdo2Async(Cia401Rig.Words(100, 200));
        HeartbeatTimeout(rig);
        Terminals(rig, 2).Should().Equal(0, 200);

        await rig.WriteI32Async(0x6444, 0x01, LeftAdjusted(55));
        rig.Settle();
        rig.Device.ReadAnalogOutput(1).Should().Be(55, "the error switch is live while the failure lasts (Figure 7)");

        await rig.WriteI32Async(0x6444, 0x02, LeftAdjusted(66));
        await rig.WriteU8Async(0x6443, 0x02, 0x01);
        rig.Settle();
        rig.Device.ReadAnalogOutput(2).Should().Be(66, "6443h:02 = 1 now takes the error value");

        await rig.WriteU8Async(0x6443, 0x01, 0x00);
        await rig.WriteI32Async(0x6444, 0x01, LeftAdjusted(77));
        rig.Settle();
        rig.Device.ReadAnalogOutput(1).Should().Be(55, "6443h:01 = 0 keeps the value the output has (§8.7.4)");
    }

    [Fact] // §8.7.4: "others = reserved"
    public async Task An_Error_Mode_Other_Than_0_Or_1_Is_Refused_With_The_Value_Range_Abort()
    {
        using var rig = new Cia401Rig(AnalogOut(1));

        foreach (byte value in new byte[] { 0x02, 0x80, 0xFF })
        {
            Cia401Rig.AbortCode(await rig.DownloadU8Async(0x6443, 0x01, value)).Should().Be(ValueRangeExceeded,
                $"6443h knows 0h and 1h, others are reserved (§8.7.4); {value:X2}h is one");
        }

        (await rig.ReadU8Async(0x6443, 0x01)).Should().Be(0x01, "the refused writes stored nothing");
        await rig.WriteU8Async(0x6443, 0x01, 0x00);
        (await rig.ReadU8Async(0x6443, 0x01)).Should().Be(0x00);
        await rig.WriteU8Async(0x6443, 0x01, 0x01);

        FluentActions.Invoking(() => rig.Od.WriteRaw(0x6443, 0x01, new byte[] { 0x02 }))
            .Should().Throw<ArgumentException>().WithMessage("*0x06090030*");
        rig.Od.ReadUnsigned(0x6443, 0x01).Should().Be(0x01u);
    }

    // ---- both blocks, one event mechanism ------------------------------------------------------

    [Fact] // controller ruling 3: one event per block, digital first
    public async Task Entering_The_Error_State_Raises_One_Event_Per_Block_Digital_First()
    {
        using var rig = new Cia401Rig(AnalogOut(1, digitalOutputs: 8));
        var recorder = new Recorder(rig);
        await rig.WriteU8Async(0x6207, 0x01, 0x0F);
        await rig.WriteI32Async(0x6444, 0x01, LeftAdjusted(-1));
        await rig.StartAsync();

        await rig.StopAsync();

        recorder.Events.Should().HaveCount(2, "one callback moved both blocks");
        recorder.Events[0].Block.Should().Be(Cia401FunctionBlock.DigitalOutput);
        recorder.Events[0].Channel.Should().Be(1);
        recorder.Events[1].Block.Should().Be(Cia401FunctionBlock.AnalogOutput);
        recorder.Events[1].Channel.Should().Be(1);
        foreach (var e in recorder.Events)
        {
            e.Levels.Should().Equal(new byte[] { 0x0F }, "each event carries both blocks' levels after the callback");
            e.AnalogLevels.Should().Equal(new short[] { -1 });
        }

        recorder.AllOnActor.Should().BeTrue();
    }

    [Fact]
    public async Task Every_Subscriber_Sees_The_Changes_In_Order_When_A_Handler_Changes_The_Analog_Outputs()
    {
        using var rig = new Cia401Rig(AnalogOut(1));
        await rig.WriteU8Async(0x1029, 0x01, 0x01);
        await rig.WriteI32Async(0x6444, 0x01, LeftAdjusted(9));
        var first = new List<short>();
        var second = new List<short>();
        rig.Device.OutputsChanged += (_, e) =>
        {
            first.Add(e.AnalogLevels[0]);
            if (first.Count == 1) rig.Device.ReportDeviceFailure(true); // inline: the device's own callback
        };
        rig.Device.OutputsChanged += (_, e) => second.Add(e.AnalogLevels[0]);
        await rig.StartAsync();

        await rig.SendRpdo2Async(Cia401Rig.Words(500));

        rig.Device.ReadAnalogOutput(1).Should().Be(9);
        first.Should().Equal(new short[] { 500, 9 }, "the write, then the error state");
        second.Should().Equal(new short[] { 500, 9 }, "the same events in the same order, ending on the latest levels");
    }

    // ---- reset ---------------------------------------------------------------------------------

    [Fact]
    public async Task Reset_Node_Returns_The_Analog_Outputs_To_Their_Defaults_And_Ends_The_Error_State()
    {
        using var rig = new Cia401Rig(AnalogOut(2));
        await rig.WriteI32Async(0x6444, 0x01, LeftAdjusted(7));
        await rig.WriteU8Async(0x6443, 0x02, 0x00);
        await rig.StartAsync();
        await rig.SendRpdo2Async(Cia401Rig.Words(100, 200));
        await rig.StopAsync();
        Terminals(rig, 2).Should().Equal(7, 200);

        await rig.ResetAsync(NmtCommand.ResetNode);

        Terminals(rig, 2).Should().Equal(new short[] { 0, 0 }, "6411h is back at 0000h (§6.2.7 NOTE)");
        rig.Device.OutputsInErrorState.Should().BeFalse();
        (await rig.ReadU8Async(0x6443, 0x02)).Should().Be(0x01);
    }

    [Fact] // the application is not reset by Reset Communication
    public async Task Reset_Communication_Ends_The_Error_State_And_Moves_No_Analog_Output()
    {
        using var rig = new Cia401Rig(AnalogOut(2));
        var recorder = new Recorder(rig);
        await rig.WriteI32Async(0x6444, 0x01, LeftAdjusted(7));
        await rig.StartAsync();
        await rig.SendRpdo2Async(Cia401Rig.Words(100, 200));
        await rig.StopAsync();

        await rig.ResetAsync(NmtCommand.ResetCommunication);

        rig.Device.OutputsInErrorState.Should().BeFalse();
        (await rig.ReadI16Async(0x6411, 0x01)).Should().Be(100, "Reset Communication restores 1000h to 1FFFh only");
        Terminals(rig, 2).Should().Equal(new short[] { 7, 0 }, "the application is not reset: the outputs stay where they are");
        recorder.Events.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_Device_Failure_Outlasts_Reset_Node_For_The_Analog_Outputs()
    {
        using var rig = new Cia401Rig(AnalogOut(1));
        await rig.WriteU8Async(0x1029, 0x01, 0x01);
        await rig.WriteI32Async(0x6444, 0x01, LeftAdjusted(7));
        ReportDeviceFailure(rig, true);
        rig.Device.ReadAnalogOutput(1).Should().Be(7);

        await rig.ResetAsync(NmtCommand.ResetNode);

        rig.Device.OutputsInErrorState.Should().BeTrue();
        rig.Device.ReadAnalogOutput(1).Should().Be(0, "6444h is back at 0 and the failure persists");
        await rig.StartAsync();
        await rig.SendRpdo2Async(Cia401Rig.Words(300));
        rig.Device.ReadAnalogOutput(1).Should().Be(0, "the outputs are still in the error state");

        ReportDeviceFailure(rig, false);
        await rig.SendRpdo2Async(Cia401Rig.Words(301));
        rig.Device.ReadAnalogOutput(1).Should().Be(301);
    }

    // ---- channels, arguments, life cycle -------------------------------------------------------

    [Fact]
    public void A_Channel_The_Device_Does_Not_Have_Is_Refused()
    {
        using var rig = new Cia401Rig(AnalogOut(2));

        FluentActions.Invoking(() => rig.Device.ReadAnalogOutput(3))
            .Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("channel");
        FluentActions.Invoking(() => rig.Device.ReadAnalogOutput(0))
            .Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("channel");
    }

    [Fact]
    public void A_Device_Without_Analog_Outputs_Has_None_To_Read()
    {
        using var rig = new Cia401Rig(new Cia401Configuration { AnalogInputs = 1 });

        FluentActions.Invoking(() => rig.Device.ReadAnalogOutput(1))
            .Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("channel");
        rig.Device.OutputsInErrorState.Should().BeFalse();
    }

    [Fact]
    public void Before_It_Is_Attached_The_Device_Refuses_To_Read()
    {
        var device = new Cia401Device(AnalogOut(1));

        FluentActions.Invoking(() => device.ReadAnalogOutput(1)).Should().Throw<InvalidOperationException>();
        device.OutputsInErrorState.Should().BeFalse();
    }
}
