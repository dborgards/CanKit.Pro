using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.Actor;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Application;
using CanKit.Pro.CANopen.Cia401;
using CanKit.Pro.CANopen.Nmt;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Cia401;

/// <summary>
/// The plant model of a CiA 401 device (<see cref="ICia401Process"/>): when the device calls it, on
/// which thread, what it may do from there, and what it may not cause. Nothing here waits on a wall
/// clock for a result: the actor is settled, or a frame is awaited with a hang guard.
/// </summary>
public class Cia401ProcessTests
{
    private static Cia401Configuration Digital8 => new() { DigitalInputs = 8, DigitalOutputs = 8 };

    /// <summary>A model that runs <see cref="Body"/> and records each call: the actor whose callback
    /// it ran in (none off the actor), the node's NMT state then, and how deeply calls were nested.</summary>
    private sealed class Model : ICia401Process
    {
        private static readonly FieldInfo HostField =
            typeof(Cia401Device).GetField("_host", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Cia401Device has no _host field any more.");

        // The actors whose callbacks the current thread is inside, innermost last: thread-static, so
        // what it holds is a property of the thread the model runs on.
        private static readonly FieldInfo RunningActors =
            typeof(ProtocolActor).GetField("t_runningActors", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ProtocolActor has no t_runningActors field any more.");

        private readonly object _gate = new();
        private readonly List<(ProtocolActor? Actor, NmtState State)> _calls = new();
        private int _depth;

        internal Action<Cia401Device, int>? Body { get; set; }

        /// <summary>The deepest nesting of calls seen; 1 when no call ran inside another.</summary>
        internal int MaxDepth { get; private set; }

        internal int Calls
        {
            get { lock (_gate) return _calls.Count; }
        }

        internal IReadOnlyList<(ProtocolActor? Actor, NmtState State)> Seen
        {
            get { lock (_gate) return _calls.ToList(); }
        }

        public void OnOutputsChanged(Cia401Device device)
        {
            var actor = (RunningActors.GetValue(null) as List<ProtocolActor>)?.LastOrDefault();
            // The device's host; State may be read on the actor loop, which is checked separately.
            var host = (ICanOpenApplicationHost?)HostField.GetValue(device);
            int call;
            lock (_gate)
            {
                _calls.Add((actor, actor is not null && host is not null ? host.State : NmtState.Initializing));
                call = _calls.Count;
            }

            _depth++;
            MaxDepth = Math.Max(MaxDepth, _depth);
            try
            {
                Body?.Invoke(device, call);
            }
            finally
            {
                _depth--;
            }
        }
    }

    private static Cia401Rig Open(Cia401Configuration configuration, Model model, CanOpenNodeOptions? options = null)
        => new(configuration, model, options);

    // ---- the brief's loop ----------------------------------------------------------------------

    [Fact] // a model that mirrors digital output group 1 onto digital input group 1
    public async Task A_Model_That_Mirrors_The_Outputs_Closes_The_Loop_On_The_Actor()
    {
        var model = new Model { Body = (device, _) => device.SetDigitalInputs(1, device.ReadDigitalOutputs(1)) };
        using var rig = Open(Digital8, model);
        await rig.StartAndTakeFirstTpdoAsync();

        await rig.SendRpdo1Async(0x5C);

        await rig.WaitForCountAsync(Cia401Rig.Tpdo1, 2);
        await rig.DrainAsync();
        rig.Count(Cia401Rig.Tpdo1).Should().Be(2, "one RPDO, one TPDO");
        rig.Payloads(Cia401Rig.Tpdo1)[1].Should().Equal(new byte[] { 0x5C }, "the input carries the output's byte");
        rig.Device.ReadDigitalInputs(1).Should().Be(0x5C);
        model.Calls.Should().Be(2, "once after attach, once for the RPDO");
        model.Seen.Should().OnlyContain(c => ReferenceEquals(c.Actor, rig.Node.ActorForTests),
            "the model runs in a callback of the node's actor loop, on no other thread");
    }

    // ---- when it is called ---------------------------------------------------------------------

    [Fact]
    public void The_Model_Is_Called_Once_After_Attach_In_Pre_Operational()
    {
        var model = new Model { Body = (device, call) => device.SetAnalogInput(1, 321) };
        using var rig = Open(new Cia401Configuration { AnalogInputs = 1, DigitalOutputs = 8 }, model);
        rig.Settle();

        model.Calls.Should().Be(1);
        model.Seen[0].Actor.Should().BeSameAs(rig.Node.ActorForTests, "the call is a callback of the device on the actor loop");
        model.Seen[0].State.Should().Be(NmtState.PreOperational, "the call is queued behind the start-up, after the boot-up");
        rig.Device.ReadAnalogInput(1).Should().Be(321, "the model's first answer is the inputs' initial value");
    }

    [Fact]
    public async Task The_Model_Is_Called_Once_After_Every_Reset_With_The_Outputs_Reloaded()
    {
        var levelsSeen = new List<byte>();
        var model = new Model { Body = (device, _) => levelsSeen.Add(device.ReadDigitalOutputs(1)) };
        using var rig = Open(Digital8, model);
        await rig.WriteU8Async(0x6200, 0x01, 0x5A);
        rig.Settle();
        model.Calls.Should().Be(2, "after attach, and for the write");

        await rig.ResetAsync(NmtCommand.ResetCommunication);
        model.Calls.Should().Be(3, "once after Reset Communication, which moved no output");
        model.Seen[2].State.Should().Be(NmtState.Initializing, "inside the reset, before the boot-up");

        await rig.ResetAsync(NmtCommand.ResetNode);
        model.Calls.Should().Be(4, "once after Reset Node, which moved the outputs: not twice");
        levelsSeen.Should().Equal(new byte[] { 0x00, 0x5A, 0x5A, 0x00 },
            "the call after Reset Node sees the outputs reloaded from 6200h");
    }

    [Fact]
    public async Task The_Model_Runs_After_The_Subscribers_Of_OutputsChanged()
    {
        var order = new List<string>();
        var model = new Model { Body = (_, call) => order.Add($"model {call}") };
        using var rig = Open(Digital8, model);
        rig.Device.OutputsChanged += (_, e) => order.Add($"event {e.Levels[0]:X2}");
        rig.Settle();

        await rig.WriteU8Async(0x6200, 0x01, 0x01);
        rig.Settle();

        order.Should().Equal("model 1", "event 01", "model 2");
    }

    // ---- what it may do, and what it may not cause ----------------------------------------------

    [Fact] // controller ruling 5: inputs the model sets do not call it again
    public async Task Inputs_The_Model_Sets_Do_Not_Call_It_Again()
    {
        // The model caps itself, so that a device that does call it again for its own inputs fails
        // this test with a count instead of recursing until the stack overflows.
        const int Cap = 20;
        var model = new Model();
        model.Body = (device, call) =>
        {
            if (call > Cap) return;
            device.SetDigitalInputs(1, (byte)call);
            device.SetAnalogInputs(1, new short[] { (short)call, (short)(call + 1) });
        };
        using var rig = Open(new Cia401Configuration { DigitalInputs = 8, DigitalOutputs = 8, AnalogInputs = 2 }, model);
        await rig.StartAndTakeFirstTpdoAsync();

        await rig.SendRpdo1Async(0x01);

        model.Calls.Should().Be(2, "once after attach, once for the RPDO, and never for the inputs it set");
        model.MaxDepth.Should().Be(1);
        rig.Device.ReadDigitalInputs(1).Should().Be(0x02);
        rig.Device.ReadAnalogInput(2).Should().Be(3);
    }

    [Fact]
    public async Task A_Model_That_Changes_The_Outputs_Sees_The_Change_Inline_And_Is_Not_Called_Again_For_It()
    {
        byte? afterFailure = null;
        var model = new Model();
        model.Body = (device, call) =>
        {
            if (call != 2) return;
            device.ReportDeviceFailure(true); // inline: the model runs in the device's callback
            afterFailure = device.ReadDigitalOutputs(1);
        };
        using var rig = Open(Digital8, model);
        var events = new List<byte>();
        rig.Device.OutputsChanged += (_, e) => events.Add(e.Levels[0]);
        await rig.WriteU8Async(0x1029, 0x01, 0x01);
        await rig.WriteU8Async(0x6207, 0x01, 0x0F);
        await rig.StartAsync();

        await rig.SendRpdo1Async(0xF0);

        afterFailure.Should().Be(0x0F, "the failure applies before ReportDeviceFailure returns");
        events.Should().Equal(new byte[] { 0xF0, 0x0F }, "the subscribers are told both changes, in order");
        model.Calls.Should().Be(2, "the model is not called again for the change it made itself");
        model.MaxDepth.Should().Be(1, "never nested");
        rig.Device.OutputsInErrorState.Should().BeTrue();
    }

    [Fact]
    public async Task A_Model_That_Throws_Does_Not_Break_The_Device()
    {
        var failures = new List<Exception>();
        var model = new Model { Body = (_, _) => throw new InvalidOperationException("model") };
        using var rig = Open(Digital8, model);
        // The call after attach throws too; its report is delivered before the count starts.
        await rig.FlushBackgroundReportsAsync();
        rig.Node.BackgroundExceptionOccurred += (_, ex) => { lock (failures) failures.Add(ex); };
        var events = new List<byte>();
        rig.Device.OutputsChanged += (_, e) => events.Add(e.Levels[0]);
        await rig.StartAsync();

        await rig.SendRpdo1Async(0x01);
        await rig.SendRpdo1Async(0x03);

        rig.Device.ReadDigitalOutputs(1).Should().Be(0x03);
        events.Should().Equal(new byte[] { 0x01, 0x03 });
        model.Calls.Should().Be(3, "after attach, and once per RPDO");
        await Cia401Rig.WaitUntilAsync(() => { lock (failures) return failures.Count >= 2; }, "two background reports");
        await rig.FlushBackgroundReportsAsync();
        lock (failures)
        {
            failures.Should().HaveCount(2, "the two calls made after the subscription to the reports")
                .And.OnlyContain(ex => ex.Message == "model");
        }

        rig.Node.State.Should().Be(NmtState.Operational);
    }

    [Fact]
    public async Task A_Throwing_Subscriber_Does_Not_Keep_The_Model_From_Its_Call()
    {
        var model = new Model();
        using var rig = Open(Digital8, model);
        rig.Device.OutputsChanged += (_, _) => throw new InvalidOperationException("subscriber");
        await rig.StartAsync();

        await rig.SendRpdo1Async(0x01);

        model.Calls.Should().Be(2, "after attach, and for the RPDO, though the subscriber threw");
    }

    [Fact] // review 4a: inputs set in the call after a reset
    public async Task Inputs_The_Model_Sets_After_A_Reset_Are_Sent_On_The_Next_Start()
    {
        var model = new Model();
        model.Body = (device, _) =>
        {
            if (model.Seen[^1].State != NmtState.Initializing) return;
            device.SetDigitalInputs(1, 0x3C);
            device.SetAnalogInputs(1, new short[] { 777 });
        };
        using var rig = Open(new Cia401Configuration { DigitalInputs = 8, AnalogInputs = 1 }, model);
        rig.Settle();
        int tpdo1 = rig.Count(Cia401Rig.Tpdo1);

        await rig.ResetAsync(NmtCommand.ResetNode);

        model.Seen[^1].State.Should().Be(NmtState.Initializing, "the call after the reset is made in Initialisation");
        await rig.DrainAsync();
        rig.Count(Cia401Rig.Tpdo1).Should().Be(tpdo1, "in Initialisation and Pre-operational nothing is sent");
        (await rig.ReadU8Async(0x6000, 0x01)).Should().Be(0x3C, "the values are in the dictionary");
        (await rig.ReadI16Async(0x6401, 0x01)).Should().Be(777);

        await rig.StartAsync();
        await rig.WaitForCountAsync(Cia401Rig.Tpdo1, tpdo1 + 1);
        rig.Payloads(Cia401Rig.Tpdo1)[tpdo1].Should().Equal(new byte[] { 0x3C }, "the TPDO on entering Operational carries them");
    }

    [Fact] // review 4b
    public void Inputs_The_Application_Sets_Do_Not_Call_The_Model()
    {
        var model = new Model();
        using var rig = Open(new Cia401Configuration { DigitalInputs = 8, DigitalOutputs = 8, AnalogInputs = 1 }, model);
        rig.Settle();
        model.Calls.Should().Be(1, "the call after attach");

        rig.Device.SetDigitalInputs(1, 0x0F);
        rig.Device.SetDigitalInput(8, true);
        rig.Device.SetAnalogInput(1, 123);
        rig.Device.SetAnalogInputs(1, new short[] { 456 });
        rig.Settle();

        rig.Device.ReadDigitalInputs(1).Should().Be(0x8F);
        rig.Device.ReadAnalogInput(1).Should().Be(456);
        model.Calls.Should().Be(1, "the model is told of outputs, and no output moved");
    }

    [Fact] // a model that sets several analog inputs uses SetAnalogInputs: one TPDO
    public async Task A_Model_That_Mirrors_The_Analog_Outputs_Sends_One_Tpdo_For_Them()
    {
        var model = new Model
        {
            Body = (device, _) => device.SetAnalogInputs(1,
                Enumerable.Range(1, 4).Select(c => device.ReadAnalogOutput(c)).ToArray()),
        };
        using var rig = Open(new Cia401Configuration { AnalogInputs = 4, AnalogOutputs = 4 }, model,
            Cia401Rig.WritablePdoRecords);
        await rig.WriteU8Async(0x6423, 0x00, 0x01);
        await rig.EnableTpdoAsync(2);
        await rig.StartAndTakeFirstTpdoAsync(Cia401Rig.Tpdo2);
        int before = rig.Count(Cia401Rig.Tpdo2);

        await rig.SendRpdo2Async(Cia401Rig.Words(1, 2, 3, 4));

        await rig.WaitForCountAsync(Cia401Rig.Tpdo2, before + 1);
        await rig.DrainAsync();
        rig.Count(Cia401Rig.Tpdo2).Should().Be(before + 1, "four inputs set in one step are one transmission");
        rig.Payloads(Cia401Rig.Tpdo2)[before].Should().Equal(Cia401Rig.Words(1, 2, 3, 4));
    }
}
