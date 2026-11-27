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
/// The CiA 401 device with digital outputs: what reaches the terminals from RPDO 1 and from SDO
/// through filter mask and polarity, the error state and what leaves it, and what the application
/// is told (CiA 401-1 v3.1.0 §5.2, §6.2.4, §6.2.5, §7.2.1.1, §7.2.3 Figure 5, §8.3.1 to §8.3.5).
/// Indices and defaults are written out from the norm here, not taken from <see cref="Cia401Objects"/>.
/// Events are counted after the device's actor has settled; frames are not counted here.
/// </summary>
public class Cia401DeviceDigitalOutputTests
{
    private static readonly TimeSpan ConsumerTimeout = TimeSpan.FromMilliseconds(100);

    private static Cia401Configuration Outputs(int channels, int inputs = 0)
        => new() { DigitalOutputs = channels, DigitalInputs = inputs };

    /// <summary>Records every <see cref="Cia401Device.OutputsChanged"/> and whether it was raised on
    /// the device node's actor loop.</summary>
    private sealed class Recorder
    {
        private readonly object _gate = new();
        private readonly List<(Cia401OutputsChangedEventArgs Args, bool OnActor)> _seen = new();

        private readonly List<byte[]> _published = new();

        internal Recorder(Cia401Rig rig)
        {
            rig.Device.OutputsChanged += (_, e) =>
            {
                lock (_gate) _seen.Add((e, rig.Node.ActorForTests.IsOnCurrentActor));
            };
            rig.Device.LevelsPublishedForTests += levels =>
            {
                lock (_gate) _published.Add(levels.ToArray());
            };
        }

        /// <summary>Every set of terminal levels the device published, in order: what a reader on
        /// another thread could have seen.</summary>
        internal IReadOnlyList<byte[]> Published
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

    /// <summary>The application reports a device failure, or its end, and the device's actor has
    /// run the report and what it caused.</summary>
    private static void ReportDeviceFailure(Cia401Rig rig, bool active)
    {
        rig.Device.ReportDeviceFailure(active);
        rig.Settle();
    }

    // ---- attach --------------------------------------------------------------------------------

    [Fact]
    public void Attach_Declares_The_Digital_Output_Objects_With_Their_Defaults()
    {
        using var rig = new Cia401Rig(Outputs(12));
        var od = rig.Od;

        foreach (ushort index in new ushort[] { 0x6200, 0x6202, 0x6206, 0x6207, 0x6208 })
        {
            od.ReadUnsigned(index, 0x00).Should().Be(2u, $"{index:X4}h has two groups for twelve outputs (§7.2.1.3)");
            od.TryGet(index, 0x00, out var count).Should().BeTrue();
            count.Access.Should().Be(OdAccess.ReadOnly, "the highest sub-index supported is const (§8.3.1 to §8.3.5)");
            od.TryGet(index, 0x03, out _).Should().BeFalse();
            for (byte sub = 1; sub <= 2; sub++)
            {
                od.TryGet(index, sub, out var entry).Should().BeTrue();
                entry.Access.Should().Be(OdAccess.ReadWrite, $"{index:X4}h is rw (§8.3.1 to §8.3.5)");
            }
        }

        for (byte sub = 1; sub <= 2; sub++)
        {
            od.ReadUnsigned(0x6200, sub).Should().Be(0x00u, "write output defaults to 00h (§8.3.1)");
            od.ReadUnsigned(0x6202, sub).Should().Be(0x00u, "polarity defaults to 00h (§8.3.2)");
            od.ReadUnsigned(0x6206, sub).Should().Be(0xFFu, "error mode defaults to FFh (§8.3.3)");
            od.ReadUnsigned(0x6207, sub).Should().Be(0x00u, "error value defaults to 00h (§8.3.4)");
            od.ReadUnsigned(0x6208, sub).Should().Be(0xFFu, "filter mask defaults to FFh (§8.3.5)");
        }

        od.TryGet(0x6200, 0x01, out var write).Should().BeTrue();
        write.PdoMappable.Should().BeTrue("6200h maps into an RPDO by default (§8.3.1)");
        od.ReadUnsigned(0x1000, 0x00).Should().Be(0x00020191u, "profile 401 with digital outputs, bit 17 (§6.2.1)");
        od.ReadUnsigned(0x1400, 0x01).Should().Be(Cia401Rig.Rpdo1, "RPDO 1 valid on its pre-defined COB-ID (§6.2.5)");
        od.ReadUnsigned(0x1600, 0x00).Should().Be(2u);
        od.ReadUnsigned(0x1600, 0x01).Should().Be(0x62000108u);
        od.ReadUnsigned(0x1600, 0x02).Should().Be(0x62000208u);
        rig.Device.ReadDigitalOutputs(1).Should().Be(0x00);
        rig.Device.ReadDigitalOutputs(2).Should().Be(0x00);
        rig.Device.OutputsInErrorState.Should().BeFalse();
    }

    // ---- the path to the terminal --------------------------------------------------------------

    [Fact]
    public async Task An_Rpdo_Sets_The_Terminal_And_Raises_OutputsChanged_Once()
    {
        using var rig = new Cia401Rig(Outputs(8));
        var recorder = new Recorder(rig);
        await rig.StartAsync();

        await rig.SendRpdo1Async(0xA5);

        rig.Device.ReadDigitalOutputs(1).Should().Be(0xA5);
        rig.Device.ReadDigitalOutput(1).Should().BeTrue();
        rig.Device.ReadDigitalOutput(2).Should().BeFalse();
        rig.Device.ReadDigitalOutput(8).Should().BeTrue();
        (await rig.ReadU8Async(0x6200, 0x01)).Should().Be(0xA5);
        var raised = recorder.Events.Should().ContainSingle().Which;
        raised.Block.Should().Be(Cia401FunctionBlock.DigitalOutput);
        raised.Group.Should().Be(1);
        raised.Channel.Should().Be(1, "channel 1 is the lowest that changed");
        raised.Levels.Should().Equal(0xA5);
        recorder.AllOnActor.Should().BeTrue("OutputsChanged is raised on the actor loop");
    }

    [Fact]
    public async Task An_Sdo_Write_Sets_The_Terminal_The_Same_Way()
    {
        using var rig = new Cia401Rig(Outputs(8));
        var recorder = new Recorder(rig);

        await rig.WriteU8Async(0x6200, 0x01, 0xA4);
        rig.Settle();

        rig.Device.ReadDigitalOutputs(1).Should().Be(0xA4);
        var raised = recorder.Events.Should().ContainSingle().Which;
        raised.Channel.Should().Be(3, "channel 3 is the lowest bit of A4h");
        raised.Levels.Should().Equal(0xA4);
    }

    [Fact]
    public async Task A_Write_That_Changes_No_Level_Raises_Nothing()
    {
        using var rig = new Cia401Rig(Outputs(8));
        var recorder = new Recorder(rig);
        await rig.StartAsync();

        await rig.SendRpdo1Async(0xA5);
        await rig.SendRpdo1Async(0xA5);
        await rig.WriteU8Async(0x6208, 0x01, 0x0F);
        rig.Settle();

        recorder.Events.Should().ContainSingle("only the first write changed a terminal");
    }

    [Fact] // review: one OnEntriesWritten, one set of levels
    public async Task Writes_Told_In_One_Callback_Publish_Only_The_Final_Levels()
    {
        using var rig = new Cia401Rig(Outputs(12));
        var recorder = new Recorder(rig);

        // Three writes in one actor job are told in one OnEntriesWritten, sorted: 6200h:01, 6200h:02,
        // 6202h:02. Refreshed after each, group 2 would show its old held value under its new
        // polarity (0Fh) before showing the final 00h.
        await rig.Node.PostToActorAsync(() =>
        {
            rig.Od.WriteRaw(0x6200, 0x01, new byte[] { 0x01 });
            rig.Od.WriteRaw(0x6200, 0x02, new byte[] { 0xFF });
            rig.Od.WriteRaw(0x6202, 0x02, new byte[] { 0xFF });
        });
        rig.Settle();

        recorder.Published.Should().ContainSingle("no intermediate level is published")
            .Which.Should().Equal(0x01, 0x00);
        var raised = recorder.Events.Should().ContainSingle().Which;
        raised.Group.Should().Be(1);
        raised.Channel.Should().Be(1);
        raised.Levels.Should().Equal(0x01, 0x00);
    }

    [Fact]
    public async Task Writes_That_Leave_Every_Level_Where_It_Was_Publish_And_Raise_Nothing()
    {
        using var rig = new Cia401Rig(Outputs(12));
        var recorder = new Recorder(rig);

        await rig.Node.PostToActorAsync(() =>
        {
            rig.Od.WriteRaw(0x6200, 0x01, new byte[] { 0x00 });
            rig.Od.WriteRaw(0x6200, 0x02, new byte[] { 0xFF });
            rig.Od.WriteRaw(0x6202, 0x02, new byte[] { 0xFF });
        });
        rig.Settle();

        rig.Device.ReadDigitalOutputs(2).Should().Be(0x00, "held FFh inverted by 6202h FFh");
        recorder.Published.Should().BeEmpty("the levels after the callback are the levels before it");
        recorder.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task A_Dictionary_Write_From_An_Output_Subscriber_Reaches_The_Terminal()
    {
        using var rig = new Cia401Rig(Outputs(8));
        await rig.StartAsync();
        var levels = new List<byte>();
        rig.Device.OutputsChanged += (_, e) =>
        {
            levels.Add(e.Levels[0]);
            if (e.Levels[0] == 1) rig.Od.WriteRaw(0x6200, 1, new byte[] { 2 });
        };

        await rig.SendRpdo1Async(1);
        rig.Settle();

        levels.Should().Equal(1, 2);
        rig.Device.ReadDigitalOutputs(1).Should().Be(2);
    }

    [Fact] // review: a handler that changes the outputs inline
    public async Task Every_Subscriber_Sees_The_Changes_In_Order_When_A_Handler_Changes_The_Outputs()
    {
        using var rig = new Cia401Rig(Outputs(8));
        await rig.WriteU8Async(0x1029, 0x01, 0x01);
        var first = new List<byte>();
        var second = new List<byte>();
        rig.Device.OutputsChanged += (_, e) =>
        {
            first.Add(e.Levels[0]);
            if (first.Count == 1) rig.Device.ReportDeviceFailure(true); // inline: the device's own callback
        };
        rig.Device.OutputsChanged += (_, e) => second.Add(e.Levels[0]);
        await rig.StartAsync();

        await rig.SendRpdo1Async(0xFF);

        rig.Device.OutputsInErrorState.Should().BeTrue();
        rig.Device.ReadDigitalOutputs(1).Should().Be(0x00);
        first.Should().Equal(new byte[] { 0xFF, 0x00 }, "the write, then the error state");
        second.Should().Equal(new byte[] { 0xFF, 0x00 }, "the same events in the same order, ending on the latest levels");
    }

    [Fact]
    public async Task A_Handler_That_Moves_The_Outputs_Away_And_Back_Raises_Nothing_For_It()
    {
        // In Pre-operational, where 1029h:01 = 00h asks for no state change.
        using var rig = new Cia401Rig(new Cia401Configuration { DigitalOutputs = 8, AnalogOutputs = 1 });
        await rig.WriteI16Async(0x6411, 0x01, 100);
        var events = new List<(Cia401FunctionBlock Block, byte Digital, short Analog)>();
        rig.Device.OutputsChanged += (_, e) =>
        {
            events.Add((e.Block, e.Levels[0], e.AnalogLevels[0]));
            if (events.Count > 1) return;

            // Inside the handler every call applies at once. The error state takes both outputs to
            // their error values, 00h and 0; leaving it moves nothing; with the error values now
            // where the outputs were, entering it again brings both back.
            rig.Device.ReportDeviceFailure(true);
            rig.Device.ReportDeviceFailure(false);
            rig.Od.WriteRaw(0x6207, 0x01, new byte[] { 0x0E });
            rig.Od.WriteRaw(0x6444, 0x01, new byte[] { 0x00, 0x00, 0x64, 0x00 }); // 100 left adjusted, 0064_0000h
            rig.Device.ReportDeviceFailure(true);
        };

        await rig.WriteU8Async(0x6200, 0x01, 0x0E);
        await rig.FenceAsync();

        events.Should().Equal(new[] { (Cia401FunctionBlock.DigitalOutput, (byte)0x0E, (short)100) },
            "the write raised its event; the handler's round ended where it began and raised none");
        rig.Device.OutputsInErrorState.Should().BeTrue();
        rig.Device.ReadDigitalOutputs(1).Should().Be(0x0E);
        rig.Device.ReadAnalogOutput(1).Should().Be(100);
    }

    [Fact]
    public async Task A_Throwing_Subscriber_Keeps_No_Other_Subscriber_From_The_Event()
    {
        using var rig = new Cia401Rig(Outputs(8));
        var seen = new List<byte>();
        rig.Device.OutputsChanged += (_, _) => throw new InvalidOperationException("first");
        rig.Device.OutputsChanged += (_, e) => seen.Add(e.Levels[0]);
        await rig.StartAsync();

        await rig.SendRpdo1Async(0x01);
        await rig.SendRpdo1Async(0x03);

        seen.Should().Equal(new byte[] { 0x01, 0x03 });
    }

    [Fact]
    public async Task Polarity_Inverts_At_The_Terminal_And_Not_In_6200h()
    {
        using var rig = new Cia401Rig(Outputs(8));
        var recorder = new Recorder(rig);

        await rig.WriteU8Async(0x6202, 0x01, 0xFF);
        rig.Settle();
        rig.Device.ReadDigitalOutputs(1).Should().Be(0xFF, "output 0 inverted is level 1 at once (§7.2.1.1)");
        recorder.Events.Should().ContainSingle();

        await rig.StartAsync();
        await rig.SendRpdo1Async(0xA5);

        rig.Device.ReadDigitalOutputs(1).Should().Be(0x5A);
        (await rig.ReadU8Async(0x6200, 0x01)).Should().Be(0xA5, "6200h holds the value written, before polarity");
        recorder.Events.Should().HaveCount(2);
        recorder.Events[1].Levels.Should().Equal(0x5A);
    }

    [Fact]
    public async Task The_Filter_Mask_Lets_Only_Its_Bits_Take_A_Received_Value()
    {
        using var rig = new Cia401Rig(Outputs(8));
        await rig.StartAsync();
        await rig.SendRpdo1Async(0xF0);

        await rig.WriteU8Async(0x6208, 0x01, 0x0F);
        await rig.SendRpdo1Async(0x05);

        rig.Device.ReadDigitalOutputs(1).Should().Be(0xF5, "the high nibble kept its old value (§8.3.5)");
        (await rig.ReadU8Async(0x6200, 0x01)).Should().Be(0x05, "6200h stores what was received");

        await rig.SendRpdo1Async(0x0A);
        rig.Device.ReadDigitalOutputs(1).Should().Be(0xFA);
    }

    // ---- the error state -------------------------------------------------------------------------

    [Fact]
    public async Task A_Heartbeat_Timeout_Puts_The_Outputs_In_Their_Error_State()
    {
        using var rig = new Cia401Rig(Outputs(8));
        var recorder = new Recorder(rig);
        await rig.WriteU8Async(0x6206, 0x01, 0xF0);
        await rig.WriteU8Async(0x6207, 0x01, 0x30);
        await rig.StartAsync();
        await rig.SendRpdo1Async(0xFF);

        HeartbeatTimeout(rig);

        rig.Device.OutputsInErrorState.Should().BeTrue();
        rig.Device.ReadDigitalOutputs(1).Should().Be(0x3F, "the high nibble takes 6207h, the low one is kept (§8.3.3, §8.3.4)");
        rig.Node.State.Should().Be(NmtState.PreOperational, "the node acts on 1029h:01 = 0 itself");
        (await rig.ReadU8Async(0x6200, 0x01)).Should().Be(0xFF, "6200h keeps what was written (§7.2.3 Figure 5)");
        recorder.Events.Should().HaveCount(2);
        recorder.Events[1].Levels.Should().Equal(0x3F);
        recorder.Events[1].Channel.Should().Be(7, "channel 7 is the lowest that changed");
    }

    [Fact]
    public async Task A_Heartbeat_Timeout_That_Leaves_The_Node_Operational_Raises_OutputsChanged_At_Once()
    {
        using var rig = new Cia401Rig(Outputs(8));
        var recorder = new Recorder(rig);
        await rig.WriteU8Async(0x1029, 0x01, 0x01);
        await rig.StartAsync();
        await rig.SendRpdo1Async(0xFF);

        HeartbeatTimeout(rig);

        rig.Node.State.Should().Be(NmtState.Operational, "1029h:01 = 1");
        rig.Device.ReadDigitalOutputs(1).Should().Be(0x00);
        recorder.Events.Should().HaveCount(2, "the callback that told the failure raised the event, with no other callback after it");
    }

    [Fact]
    public async Task Stopped_Puts_The_Outputs_In_Their_Error_State()
    {
        using var rig = new Cia401Rig(Outputs(8));
        var recorder = new Recorder(rig);
        await rig.WriteU8Async(0x6206, 0x01, 0xF0);
        await rig.WriteU8Async(0x6207, 0x01, 0x30);
        await rig.StartAsync();
        await rig.SendRpdo1Async(0xFF);

        await rig.StopAsync();

        rig.Device.OutputsInErrorState.Should().BeTrue("a 'Stop Remote Node' indication is an error (§8.3.3)");
        rig.Device.ReadDigitalOutputs(1).Should().Be(0x3F);
        recorder.Events.Should().HaveCount(2);
    }

    [Fact]
    public async Task When_The_Failure_Ends_Nothing_Moves_Until_The_Next_Write()
    {
        using var rig = new Cia401Rig(Outputs(8));
        var recorder = new Recorder(rig);
        await rig.WriteU8Async(0x6206, 0x01, 0xF0);
        await rig.WriteU8Async(0x6207, 0x01, 0x30);
        await rig.StartAsync();
        await rig.SendRpdo1Async(0xFF);
        HeartbeatTimeout(rig);

        await HeartbeatResumes(rig);

        rig.Device.OutputsInErrorState.Should().BeFalse();
        rig.Device.ReadDigitalOutputs(1).Should().Be(0x3F, "leaving the error state restores nothing by itself");
        recorder.Events.Should().HaveCount(2);

        await rig.StartAsync();
        rig.Device.ReadDigitalOutputs(1).Should().Be(0x3F);
        await rig.SendRpdo1Async(0xFF); // the value 6200h holds already: no change of the entry
        rig.Device.ReadDigitalOutputs(1).Should().Be(0xFF, "the next accepted write sets the outputs");
        recorder.Events.Should().HaveCount(3);
    }

    [Fact] // Review Focus 2
    public async Task An_Output_The_Master_Never_Wrote_Stays_At_Its_Error_Value_When_The_Failure_Ends()
    {
        using var rig = new Cia401Rig(Outputs(8));
        var recorder = new Recorder(rig);
        await rig.WriteU8Async(0x6207, 0x01, 0xFF);
        await rig.StartAsync();

        HeartbeatTimeout(rig);
        rig.Device.ReadDigitalOutputs(1).Should().Be(0xFF);

        await HeartbeatResumes(rig);
        await rig.StartAsync();
        rig.Settle();

        rig.Device.OutputsInErrorState.Should().BeFalse();
        rig.Device.ReadDigitalOutputs(1).Should().Be(0xFF, "6200h was never written: nothing tells the output to move");
        recorder.Events.Should().ContainSingle("only entering the error state moved the outputs");
    }

    [Fact]
    public async Task A_Device_Failure_In_Operational_Takes_The_Node_To_Pre_Operational()
    {
        using var rig = new Cia401Rig(Outputs(8));
        await rig.StartAsync();
        await rig.SendRpdo1Async(0xFF);

        rig.Device.ReportDeviceFailure(true);
        await rig.WaitForStateAsync(NmtState.PreOperational);
        rig.Settle();

        rig.Device.OutputsInErrorState.Should().BeTrue();
        rig.Device.ReadDigitalOutputs(1).Should().Be(0x00, "6206h FFh and 6207h 00h by default");
    }

    [Fact]
    public async Task A_Device_Failure_Reported_Again_Asks_For_No_State_Again()
    {
        using var rig = new Cia401Rig(Outputs(8));
        await rig.StartAsync();
        rig.Device.ReportDeviceFailure(true);
        await rig.WaitForStateAsync(NmtState.PreOperational);
        await rig.StartAsync(); // the master starts the node while the failure lasts

        ReportDeviceFailure(rig, true);
        await rig.DrainAsync();

        rig.Node.State.Should().Be(NmtState.Operational, "the failure is a level: reporting it again is no new failure");
        rig.Device.OutputsInErrorState.Should().BeTrue();
    }

    [Fact] // Review Focus 2 through Stop and Start
    public async Task An_Output_The_Master_Never_Wrote_Stays_At_Its_Error_Value_When_The_Node_Is_Started_Again()
    {
        using var rig = new Cia401Rig(Outputs(8));
        var recorder = new Recorder(rig);
        await rig.WriteU8Async(0x6207, 0x01, 0xFF);
        await rig.StartAsync();
        await rig.StopAsync();
        rig.Device.ReadDigitalOutputs(1).Should().Be(0xFF);

        await rig.StartAsync();
        rig.Settle();

        rig.Device.OutputsInErrorState.Should().BeFalse("Stopped ended");
        rig.Device.ReadDigitalOutputs(1).Should().Be(0xFF, "6200h was never written: nothing tells the output to move");
        recorder.Events.Should().ContainSingle("only entering Stopped moved the outputs");
    }

    [Fact]
    public async Task A_Device_Failure_With_1029h_1_Leaves_The_Node_Operational()
    {
        using var rig = new Cia401Rig(Outputs(8));
        await rig.WriteU8Async(0x1029, 0x01, 0x01);
        await rig.StartAsync();
        await rig.SendRpdo1Async(0xFF);

        ReportDeviceFailure(rig, true);
        await rig.DrainAsync();

        rig.Node.State.Should().Be(NmtState.Operational, "1029h:01 = 1: remain in the current state (§6.2.4)");
        rig.Device.OutputsInErrorState.Should().BeTrue();
        rig.Device.ReadDigitalOutputs(1).Should().Be(0x00);
    }

    [Fact]
    public async Task A_Device_Failure_With_1029h_2_Stops_The_Node()
    {
        using var rig = new Cia401Rig(Outputs(8));
        await rig.WriteU8Async(0x1029, 0x01, 0x02);
        await rig.StartAsync();

        rig.Device.ReportDeviceFailure(true);
        await rig.WaitForStateAsync(NmtState.Stopped);
        rig.Settle();

        rig.Device.OutputsInErrorState.Should().BeTrue();
    }

    [Fact] // review ruling: 02h is unconditional (§6.2.4), unlike the node's own reaction
    public async Task A_Device_Failure_With_1029h_2_Stops_The_Node_From_Pre_Operational()
    {
        using var rig = new Cia401Rig(Outputs(8));
        await rig.WriteU8Async(0x1029, 0x01, 0x02);

        rig.Device.ReportDeviceFailure(true);
        await rig.WaitForStateAsync(NmtState.Stopped);
        rig.Settle();

        rig.Device.OutputsInErrorState.Should().BeTrue();
    }

    [Fact]
    public async Task A_Device_Failure_In_Pre_Operational_Leaves_The_Node_There()
    {
        using var rig = new Cia401Rig(Outputs(8));

        ReportDeviceFailure(rig, true);
        await rig.DrainAsync();

        rig.Node.State.Should().Be(NmtState.PreOperational);
        rig.Device.OutputsInErrorState.Should().BeTrue();
    }

    [Fact] // ruling 4: the error state ends only when no cause remains
    public async Task A_Device_Failure_In_Stopped_Leaves_The_Node_Stopped_And_Outlasts_Stopped()
    {
        using var rig = new Cia401Rig(Outputs(8));
        await rig.StartAsync();
        await rig.StopAsync();

        rig.Device.ReportDeviceFailure(true);
        rig.Settle();
        rig.Node.State.Should().Be(NmtState.Stopped, "1029h:01 = 0 leaves for Pre-operational only from Operational (§6.2.4)");

        await rig.StartAsync();
        rig.Settle();
        rig.Device.OutputsInErrorState.Should().BeTrue("Stopped ended, the device failure did not");
        await rig.SendRpdo1Async(0xFF);
        rig.Device.ReadDigitalOutputs(1).Should().Be(0x00, "a write in the error state reaches no terminal");

        ReportDeviceFailure(rig, false);
        rig.Device.OutputsInErrorState.Should().BeFalse();
        rig.Device.ReadDigitalOutputs(1).Should().Be(0x00);
        await rig.SendRpdo1Async(0xFF);
        rig.Device.ReadDigitalOutputs(1).Should().Be(0xFF);
    }

    [Fact] // §7.2.1.1 Table 37, §7.2.3 Figure 5: the error switch is before the polarity
    public async Task The_Error_Value_Is_Logical_And_Polarity_Inverts_It()
    {
        using var rig = new Cia401Rig(Outputs(8));
        await rig.WriteU8Async(0x1029, 0x01, 0x01);
        await rig.WriteU8Async(0x6202, 0x01, 0xFF);
        await rig.StartAsync();
        await rig.SendRpdo1Async(0x0F);
        rig.Device.ReadDigitalOutputs(1).Should().Be(0xF0);

        ReportDeviceFailure(rig, true);

        rig.Device.ReadDigitalOutputs(1).Should().Be(0xFF, "error value 00h inverted by 6202h FFh");
    }

    [Fact] // §8.3.5: the mask filters received values, not the error value
    public async Task The_Filter_Mask_Does_Not_Hold_Back_The_Error_Value()
    {
        using var rig = new Cia401Rig(Outputs(8));
        await rig.WriteU8Async(0x1029, 0x01, 0x01);
        await rig.WriteU8Async(0x6208, 0x01, 0x0F);
        await rig.WriteU8Async(0x6207, 0x01, 0xFF);
        await rig.StartAsync();

        ReportDeviceFailure(rig, true);

        rig.Device.ReadDigitalOutputs(1).Should().Be(0xFF, "every bit with 6206h = 1 takes 6207h");
    }

    [Fact] // §7.2.3 Figure 5, §8.3.3: in the error state 6200h is cut off and a bit with 6206h = 0 is kept
    public async Task Writes_In_The_Error_State_Are_Stored_In_6200h_And_Reach_No_Terminal()
    {
        using var rig = new Cia401Rig(Outputs(8));
        var recorder = new Recorder(rig);
        await rig.WriteU8Async(0x1029, 0x01, 0x01);
        await rig.WriteU8Async(0x6206, 0x01, 0x0F);
        await rig.StartAsync();
        ReportDeviceFailure(rig, true);

        await rig.SendRpdo1Async(0xFF);

        rig.Device.ReadDigitalOutputs(1).Should().Be(0x00, "neither the bits with 6206h = 0 nor the others follow");
        (await rig.ReadU8Async(0x6200, 0x01)).Should().Be(0xFF, "the write is stored");
        recorder.Events.Should().BeEmpty();

        ReportDeviceFailure(rig, false);
        rig.Device.ReadDigitalOutputs(1).Should().Be(0x00, "the stored write is not applied by leaving the error state");
        await rig.SendRpdo1Async(0xFF);
        rig.Device.ReadDigitalOutputs(1).Should().Be(0xFF);
    }

    [Fact]
    public async Task Error_Mode_Value_And_Polarity_Written_In_The_Error_State_Apply_At_Once()
    {
        using var rig = new Cia401Rig(Outputs(8));
        await rig.WriteU8Async(0x1029, 0x01, 0x01);
        await rig.StartAsync();
        await rig.SendRpdo1Async(0xFF);
        ReportDeviceFailure(rig, true);
        rig.Device.ReadDigitalOutputs(1).Should().Be(0x00);

        await rig.WriteU8Async(0x6207, 0x01, 0x0F);
        rig.Settle();
        rig.Device.ReadDigitalOutputs(1).Should().Be(0x0F, "the error value switch follows 6207h (§7.2.3 Figure 5)");

        await rig.WriteU8Async(0x6206, 0x01, 0xF0);
        await rig.WriteU8Async(0x6207, 0x01, 0xF0);
        rig.Settle();
        rig.Device.ReadDigitalOutputs(1).Should().Be(0xFF, "the low nibble, 6206h = 0, keeps its 1s; the high one takes 6207h");

        await rig.WriteU8Async(0x6202, 0x01, 0x01);
        rig.Settle();
        rig.Device.ReadDigitalOutputs(1).Should().Be(0xFE, "polarity applies in the error state too");
    }

    // ---- reset ---------------------------------------------------------------------------------

    [Fact]
    public async Task Reset_Node_Returns_The_Terminal_To_Its_Default_And_Ends_The_Error_State()
    {
        using var rig = new Cia401Rig(Outputs(8));
        await rig.WriteU8Async(0x6206, 0x01, 0xF0);
        await rig.WriteU8Async(0x6207, 0x01, 0x30);
        await rig.StartAsync();
        await rig.SendRpdo1Async(0xFF);
        await rig.StopAsync();
        rig.Device.ReadDigitalOutputs(1).Should().Be(0x3F);

        await rig.ResetAsync(NmtCommand.ResetNode);

        rig.Device.ReadDigitalOutputs(1).Should().Be(0x00, "6200h is back at 00h (§6.2.5 NOTE)");
        rig.Device.OutputsInErrorState.Should().BeFalse();
        (await rig.ReadU8Async(0x6206, 0x01)).Should().Be(0xFF);
    }

    [Fact] // §6.2.5 NOTE: the outputs return to 6200h after an application reset, which Reset Communication is not
    public async Task Reset_Communication_Ends_The_Error_State_And_Moves_No_Output()
    {
        using var rig = new Cia401Rig(Outputs(8));
        var recorder = new Recorder(rig);
        await rig.WriteU8Async(0x6206, 0x01, 0xF0);
        await rig.WriteU8Async(0x6207, 0x01, 0x30);
        await rig.StartAsync();
        await rig.SendRpdo1Async(0xFF);
        await rig.StopAsync();

        await rig.ResetAsync(NmtCommand.ResetCommunication);

        rig.Device.OutputsInErrorState.Should().BeFalse();
        (await rig.ReadU8Async(0x6200, 0x01)).Should().Be(0xFF, "Reset Communication restores 1000h to 1FFFh only");
        rig.Device.ReadDigitalOutputs(1).Should().Be(0x3F, "the application is not reset: the outputs stay where they are");
        recorder.Events.Should().HaveCount(2);
    }

    [Fact] // Review Focus 2, the failure ended by a reset
    public async Task An_Output_The_Master_Never_Wrote_Stays_At_Its_Error_Value_When_Reset_Communication_Ends_The_Failure()
    {
        using var rig = new Cia401Rig(Outputs(8));
        await rig.WriteU8Async(0x6207, 0x01, 0xFF);
        await rig.StartAsync();
        HeartbeatTimeout(rig);
        rig.Device.ReadDigitalOutputs(1).Should().Be(0xFF);

        await rig.ResetAsync(NmtCommand.ResetCommunication);

        rig.Device.OutputsInErrorState.Should().BeFalse("the reset ended the communication failure");
        rig.Device.ReadDigitalOutputs(1).Should().Be(0xFF, "6200h was never written: nothing tells the output to move");
    }

    [Fact] // review minor 6
    public async Task Reset_Node_During_A_Communication_Failure_Returns_The_Outputs_To_Their_Defaults_Once()
    {
        using var rig = new Cia401Rig(Outputs(8));
        var recorder = new Recorder(rig);
        await rig.WriteU8Async(0x6207, 0x01, 0xFF);
        await rig.StartAsync();
        HeartbeatTimeout(rig);
        rig.Device.OutputsInErrorState.Should().BeTrue();
        rig.Device.ReadDigitalOutputs(1).Should().Be(0xFF);
        int before = recorder.Events.Count;

        await rig.ResetAsync(NmtCommand.ResetNode);

        rig.Device.OutputsInErrorState.Should().BeFalse("the reset ended the communication failure");
        rig.Device.ReadDigitalOutputs(1).Should().Be(0x00, "6200h and 6207h are back at their defaults");
        recorder.Events.Count.Should().Be(before + 1, "the reset moved the outputs once");
        recorder.Events[before].Levels.Should().Equal(0x00);
    }

    [Fact] // ruling 5: a device failure is the application's statement about the hardware
    public async Task A_Device_Failure_Outlasts_Reset_Node_Until_The_Application_Ends_It()
    {
        using var rig = new Cia401Rig(Outputs(8));
        await rig.WriteU8Async(0x1029, 0x01, 0x01);
        ReportDeviceFailure(rig, true);

        await rig.ResetAsync(NmtCommand.ResetNode);

        rig.Device.OutputsInErrorState.Should().BeTrue();
        await rig.StartAsync();
        await rig.SendRpdo1Async(0xFF);
        rig.Device.ReadDigitalOutputs(1).Should().Be(0x00, "the outputs are still in the error state");

        ReportDeviceFailure(rig, false);
        rig.Device.OutputsInErrorState.Should().BeFalse();
        await rig.SendRpdo1Async(0xFF);
        rig.Device.ReadDigitalOutputs(1).Should().Be(0xFF);
    }

    // ---- the event's handler -------------------------------------------------------------------

    [Fact]
    public async Task A_Handler_That_Throws_Does_Not_Break_The_Device()
    {
        using var rig = new Cia401Rig(Outputs(8));
        var failures = new List<Exception>();
        rig.Node.BackgroundExceptionOccurred += (_, ex) => { lock (failures) failures.Add(ex); };
        var calls = 0;
        rig.Device.OutputsChanged += (_, _) =>
        {
            calls++;
            throw new InvalidOperationException("handler");
        };
        await rig.StartAsync();

        await rig.SendRpdo1Async(0x01);
        await rig.SendRpdo1Async(0x03);

        calls.Should().Be(2);
        rig.Device.ReadDigitalOutputs(1).Should().Be(0x03);
        // The node delivers its reports on the thread pool: wait for both, then for anything behind them.
        await Cia401Rig.WaitUntilAsync(() => { lock (failures) return failures.Count >= 2; }, "two background reports");
        await rig.FlushBackgroundReportsAsync();
        lock (failures) failures.Should().HaveCount(2).And.OnlyContain(ex => ex.Message == "handler");
        rig.Node.State.Should().Be(NmtState.Operational);
    }

    [Fact]
    public async Task A_Handler_Can_Call_The_Device_Inline()
    {
        using var rig = new Cia401Rig(Outputs(8, inputs: 8));
        byte? outputsSeen = null;
        byte? inputsSeen = null;
        rig.Device.OutputsChanged += (_, e) =>
        {
            outputsSeen = rig.Device.ReadDigitalOutputs(1);
            rig.Device.SetDigitalInputs(1, e.Levels[0]); // a loop-back from the outputs to the inputs
            inputsSeen = rig.Device.ReadDigitalInputs(1);
        };
        await rig.StartAndTakeFirstTpdoAsync();

        await rig.SendRpdo1Async(0x5C);

        outputsSeen.Should().Be(0x5C, "the device's state is consistent before the event");
        inputsSeen.Should().Be(0x5C, "a set from inside the device's callback applies at once");
        await rig.WaitForCountAsync(Cia401Rig.Tpdo1, 2);
        rig.Payloads(Cia401Rig.Tpdo1)[1].Should().Equal(0x5C);
    }

    // ---- channels, arguments, life cycle -------------------------------------------------------

    [Fact]
    public async Task Twelve_Outputs_Are_Two_Groups_And_Channels_The_Device_Lacks_Show_No_Level()
    {
        using var rig = new Cia401Rig(Outputs(12));
        await rig.StartAsync();

        await rig.SendRpdo1Async(0x00, 0x01);
        rig.Device.ReadDigitalOutput(9).Should().BeTrue("channel 9 is bit 0 of group 2 (§7.2.1.3)");
        rig.Device.ReadDigitalOutputs(2).Should().Be(0x01);

        await rig.WriteU8Async(0x6202, 0x02, 0xFF);
        rig.Settle();
        rig.Device.ReadDigitalOutputs(2).Should().Be(0x0E, "channels 13 to 16 do not exist");
        rig.Device.ReadDigitalOutput(12).Should().BeTrue();
    }

    [Fact]
    public void A_Channel_Or_Group_The_Device_Does_Not_Have_Is_Refused()
    {
        using var rig = new Cia401Rig(Outputs(12));

        FluentActions.Invoking(() => rig.Device.ReadDigitalOutputs(3))
            .Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("group");
        FluentActions.Invoking(() => rig.Device.ReadDigitalOutputs(0))
            .Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("group");
        FluentActions.Invoking(() => rig.Device.ReadDigitalOutput(13))
            .Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("channel");
        FluentActions.Invoking(() => rig.Device.ReadDigitalOutput(0))
            .Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("channel");
    }

    [Fact]
    public void A_Device_Without_Outputs_Has_None_To_Read()
    {
        using var rig = new Cia401Rig(new Cia401Configuration { DigitalInputs = 8 });

        FluentActions.Invoking(() => rig.Device.ReadDigitalOutputs(1))
            .Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("group");
        FluentActions.Invoking(() => rig.Device.ReadDigitalOutput(1))
            .Should().Throw<ArgumentOutOfRangeException>().WithMessage("The device has 0 digital outputs*")
            .Which.ParamName.Should().Be("channel");
        rig.Device.OutputsInErrorState.Should().BeFalse();
    }

    [Fact]
    public void Before_It_Is_Attached_The_Device_Refuses_To_Read_Or_Report()
    {
        var device = new Cia401Device(Outputs(8));

        FluentActions.Invoking(() => device.ReadDigitalOutputs(1)).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => device.ReadDigitalOutput(1)).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => device.ReportDeviceFailure(true)).Should().Throw<InvalidOperationException>();
        device.OutputsInErrorState.Should().BeFalse();
    }
}
