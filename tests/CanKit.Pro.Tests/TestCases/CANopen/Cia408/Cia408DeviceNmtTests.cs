using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Cia408;
using CanKit.Pro.CANopen.Nmt;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Cia408;

/// <summary>
/// The CiA 408 v2.2.0 device state machine coupled to NMT (§7.8, Table 10; §7.6, Table 6): Stopped,
/// Reset Communication and a communication failure force the fault reaction without an EMCY, Reset
/// Node restarts the machine and re-enters the faults the application still reports, and a reset
/// leaves the device's objects as the device powered on (the plan's Review Focus 5). Every expected
/// state and D-list is written out here; indices too.
/// </summary>
public class Cia408DeviceNmtTests
{
    private const ushort ControlWord16 = 0x6040;
    private const ushort StatusWord16 = 0x6041;
    private const ushort ErrorCode = 0x604E;

    /// <summary>Table 1 of CiA 408 v2.2.0 §6.3: input current too high.</summary>
    private const ushort InputCurrentTooHigh = 0x2110;

    private const byte Producer = 0x22;

    // ---- the table ------------------------------------------------------------------------------

    /// <summary>The state each resting state is forced into by Stopped, Reset Communication and a
    /// communication failure: D8 and the highest fault state Table 9 allows (§7.8 Table 10, §7.7
    /// Table 9), written out; a fault state stays and raises nothing.</summary>
    private static readonly Dictionary<string, (string State, string Change)> Forced = new()
    {
        ["Init"] = ("FaultInit", "Init->FaultInit [8,13]"),
        ["Disabled"] = ("FaultDisabled", "Disabled->FaultDisabled [8,12]"),
        ["Hold"] = ("FaultHold", "Hold->FaultHold [8,9]"),
        ["Active"] = ("FaultHold", "Active->FaultHold [8,9]"),
        ["FaultInit"] = ("FaultInit", ""),
        ["FaultDisabled"] = ("FaultDisabled", ""),
        ["FaultHold"] = ("FaultHold", ""),
    };

    /// <summary>Every NMT event from every NMT state it can come from (CiA 301 v4.2.0 §7.3.2.2.1:
    /// C3/C5 and C12 from Pre-operational, C4/C8 and C13 from Operational, C6/C7 and C14 from
    /// Stopped, Reset Node from each) and the communication failure from Pre-operational and
    /// Operational, crossed with every resting state of the device. Stop, Reset Communication and the
    /// failure force <see cref="Forced"/>; Reset Node restarts into INIT, raised also from INIT;
    /// Start and Enter Pre-operational move nothing.</summary>
    public static IEnumerable<object[]> NmtEvents()
    {
        var events = new (string From, string Event)[]
        {
            ("PreOperational", "Start"), ("PreOperational", "Stop"), ("PreOperational", "ResetNode"),
            ("PreOperational", "ResetCommunication"), ("PreOperational", "CommunicationFailure"),
            ("Operational", "EnterPreOperational"), ("Operational", "Stop"), ("Operational", "ResetNode"),
            ("Operational", "ResetCommunication"), ("Operational", "CommunicationFailure"),
            ("Stopped", "Start"), ("Stopped", "EnterPreOperational"), ("Stopped", "ResetNode"),
            ("Stopped", "ResetCommunication"),
        };
        foreach (var (from, nmtEvent) in events)
        {
            foreach (var state in Forced.Keys)
            {
                var (expected, change) = nmtEvent switch
                {
                    "Stop" or "ResetCommunication" or "CommunicationFailure" => Forced[state],
                    "ResetNode" => ("Init", $"{state}->Init [255,0,1]"),
                    _ => (state, ""),
                };
                yield return new object[] { from, state, nmtEvent, expected, change };
            }
        }
    }

    [Theory]
    [MemberData(nameof(NmtEvents))]
    public async Task Each_Nmt_Event_From_Each_Resting_State(string nmtFrom, string from, string nmtEvent, string expected, string changes)
    {
        using var rig = new Cia408Rig(Cia408Rig.Valve);
        if (nmtEvent == "CommunicationFailure") await WatchProducerAsync(rig);
        if (nmtFrom == "Operational") await rig.StartAsync();
        if (nmtFrom == "Stopped") await rig.StopAsync();
        await ReachLocallyAsync(rig, (Cia408State)Enum.Parse(typeof(Cia408State), from));
        rig.Node.State.ToString().Should().Be(nmtFrom);
        int before = rig.Changes.Count;

        await ApplyAsync(rig, nmtEvent);

        rig.Device.State.ToString().Should().Be(expected);
        rig.Changes.Skip(before).Select(Describe).Should().Equal(changes.Length == 0 ? Array.Empty<string>() : new[] { changes });
        (await StatusAsync(rig)).Should().Be(Cia408StateMachine.StatusBits(rig.Device.State),
            "the status word shows the state the event left");
    }

    [Theory]
    [InlineData("Stop")]
    [InlineData("ResetCommunication")]
    public async Task Stopped_And_Reset_Communication_Send_No_Emcy_And_Leave_604E(string nmtEvent)
    {
        using var rig = new Cia408Rig(Cia408Rig.Valve);
        await rig.WriteU16Async(ControlWord16, 0, 0x0007);
        rig.Settle();
        rig.Device.SetWarning(InputCurrentTooHigh, true);
        rig.Device.SetWarning(InputCurrentTooHigh, false);
        rig.Settle();
        await rig.DrainEmcyAsync();
        int emcys = rig.Count(CanOpenProfileRig.Emcy);

        await ApplyAsync(rig, nmtEvent);
        await rig.StartAsync();
        await rig.DrainEmcyAsync();

        rig.Device.State.Should().Be(Cia408State.FaultHold);
        rig.Count(CanOpenProfileRig.Emcy).Should().Be(emcys, "the norm forces the transition; it names no error");
        (await rig.ReadU16Async(ErrorCode, 0)).Should().Be(InputCurrentTooHigh, "604Eh is unchanged");
    }

    // ---- causes ---------------------------------------------------------------------------------

    [Fact]
    public async Task The_Reset_Is_Refused_While_A_Communication_Failure_Lasts()
    {
        using var rig = new Cia408Rig(Cia408Rig.Valve);
        await rig.WriteU16Async(ControlWord16, 0, 0x0003);
        await WatchProducerAsync(rig);
        await ApplyAsync(rig, "CommunicationFailure");
        rig.Device.State.Should().Be(Cia408State.FaultHold);

        await rig.WriteU16Async(ControlWord16, 0, 0x0003);
        await rig.WriteU16Async(ControlWord16, 0, 0x000B);
        rig.Settle();
        rig.Device.State.Should().Be(Cia408State.FaultHold, "the failure is a cause while it lasts");

        await BeatAsync(rig);
        await rig.WriteU16Async(ControlWord16, 0, 0x0003);
        await rig.WriteU16Async(ControlWord16, 0, 0x000B);
        rig.Settle();
        rig.Device.State.Should().Be(Cia408State.Hold, "D11 once the failure has ended");
    }

    [Theory]
    [InlineData("Stop")]
    [InlineData("ResetCommunication")]
    public async Task Stopped_And_Reset_Communication_Are_No_Cause_That_Outlasts_The_Event(string nmtEvent)
    {
        using var rig = new Cia408Rig(Cia408Rig.Valve);
        await rig.WriteU16Async(ControlWord16, 0, 0x0003);
        rig.Settle();
        await ApplyAsync(rig, nmtEvent);
        await rig.StartAsync();

        await rig.WriteU16Async(ControlWord16, 0, 0x0003);
        await rig.WriteU16Async(ControlWord16, 0, 0x000B);
        rig.Settle();

        rig.Device.State.Should().Be(Cia408State.Hold, "the reset edge is accepted as soon as no cause is active");
    }

    [Fact] // the seam keeps command writes across Reset Communication and tells them after it
    public async Task A_Control_Word_Raced_Into_Reset_Communication_Is_Judged_In_The_Fault_State()
    {
        using var rig = new Cia408Rig(Cia408Rig.Valve);
        await rig.WriteU16Async(ControlWord16, 0, 0x0007);
        rig.Settle();
        int before = rig.Changes.Count;
        int bootUps = rig.BootUps();

        await rig.WhileActorHeldAsync(() => rig.QueueFramesAsync(
            (CanOpenProfileRig.SdoRequest, CanOpenProfileRig.DownloadU16(ControlWord16, 0, 0x0001)),
            (0x000u, new[] { (byte)NmtCommand.ResetCommunication, CanOpenProfileRig.DeviceId })));
        await CanOpenProfileRig.WaitUntilAsync(() => rig.BootUps() > bootUps, "the boot-up after the reset");
        rig.Settle();

        rig.Changes.Skip(before).Select(Describe).Should().Equal(
            new[] { "Active->FaultHold [8,9]", "FaultHold->FaultDisabled [15]" },
            "the reset forces D8 first, and the word written before it then takes D15 by level (Table 7)");
        rig.Device.State.Should().Be(Cia408State.FaultDisabled);
    }

    // ---- Reset Node -----------------------------------------------------------------------------

    [Fact]
    public async Task Reset_Node_Restarts_And_Reenters_A_Cause_The_Application_Still_Reports()
    {
        using var rig = new Cia408Rig(Cia408Rig.Valve);
        await rig.WriteU16Async(ControlWord16, 0, 0x0007);
        rig.Settle();
        rig.Device.ReportFault(InputCurrentTooHigh, Cia408FaultState.FaultHold, errorRegisterBits: 0x02);
        await rig.WaitForCountAsync(CanOpenProfileRig.Emcy, 1);
        int before = rig.Changes.Count;

        await rig.ResetAsync(NmtCommand.ResetNode);
        await rig.WaitForCountAsync(CanOpenProfileRig.Emcy, 2);

        rig.Changes.Skip(before).Select(Describe).Should().Equal(
            "FaultHold->Init [255,0,1]", "Init->FaultInit [8,13]");
        rig.Payloads(CanOpenProfileRig.Emcy)[1].Take(3).Should().Equal(new byte[] { 0x10, 0x21, 0x03 },
            "the reset ended the error; the cause persists, so its EMCY goes out again after the boot-up");
        rig.Od.ReadRaw(0x1001, 0).Should().Equal(new byte[] { 0x03 });
        (await rig.ReadU16Async(ErrorCode, 0)).Should().Be(InputCurrentTooHigh);

        await rig.WriteU16Async(ControlWord16, 0, 0x0000);
        await rig.WriteU16Async(ControlWord16, 0, 0x0008);
        rig.Settle();
        rig.Device.State.Should().Be(Cia408State.FaultInit, "the cause is still active");

        rig.Device.ClearFaultCause(InputCurrentTooHigh);
        rig.Settle();
        await rig.WriteU16Async(ControlWord16, 0, 0x0000);
        await rig.WriteU16Async(ControlWord16, 0, 0x0008);
        rig.Settle();
        rig.Device.State.Should().Be(Cia408State.Init, "D14");
    }

    [Fact]
    public async Task Reset_Node_With_A_Cause_Asks_For_What_1029h_02_Names_Once_The_Node_Has_Booted()
    {
        using var rig = new Cia408Rig(Cia408Rig.Valve);
        await rig.WriteU8Async(0x1029, 0x02, 0x02);
        await rig.SaveAsync();
        rig.Device.ReportFault(InputCurrentTooHigh, Cia408FaultState.FaultInit);
        await rig.WaitForStateAsync(NmtState.Stopped);

        await rig.ResetAsync(NmtCommand.ResetNode);
        await rig.WaitForStateAsync(NmtState.Stopped);

        rig.Device.State.Should().Be(Cia408State.FaultInit);
    }

    [Fact] // Review Focus 5: a reset leaves the device as it powered on
    public async Task Reset_Node_Gives_Back_The_Device_That_Powered_On_Stored_Values_Included()
    {
        using var rig = new Cia408Rig(Cia408Rig.Valve);
        rig.Settle();
        var poweredOn = ApplicationObjects(rig);
        await rig.WriteU16Async(ControlWord16, 0, 0x0007);
        await rig.WriteU32Async(0x6044, 0, 0x0000_0007);
        rig.Settle();
        rig.Device.ReportFault(InputCurrentTooHigh, Cia408FaultState.FaultHold);
        rig.Device.ClearFaultCause(InputCurrentTooHigh);
        rig.Settle();
        (await rig.ReadU16Async(StatusWord16, 0)).Should().Be(0x0003);
        await rig.SaveAsync();

        await rig.ResetAsync(NmtCommand.ResetNode);

        Differences(poweredOn, ApplicationObjects(rig)).Should().BeEmpty(
            "control and status words and 604Eh come from the device's state, not from what was stored");
        rig.Device.State.Should().Be(Cia408State.Init);
        rig.Device.ControlWord.Should().Be(0u, "the history starts afresh");
    }

    [Fact]
    public async Task Reset_Node_Takes_A_Stored_Source_Of_The_Control_Word()
    {
        using var rig = new Cia408Rig(Cia408Rig.Valve);
        await rig.WriteU8Async(0x604F, 0, 0x01);
        await rig.SaveAsync();
        await rig.WriteU8Async(0x604F, 0, 0x00);

        await rig.ResetAsync(NmtCommand.ResetNode);

        (await rig.ReadU16Async(StatusWord16, 0)).Should().Be(0x0018, "604Fh = 1 is a parameter and came back stored");
        await rig.WriteU16Async(ControlWord16, 0, 0x0001);
        rig.Settle();
        rig.Device.State.Should().Be(Cia408State.Init, "the stored local control is in force");
    }

    [Fact]
    public async Task Reset_Communication_Raises_A_Cause_That_Persists_Again()
    {
        using var rig = new Cia408Rig(Cia408Rig.Valve);
        await rig.WriteU16Async(ControlWord16, 0, 0x0003);
        rig.Settle();
        rig.Device.ReportFault(InputCurrentTooHigh, Cia408FaultState.FaultHold, errorRegisterBits: 0x02);
        await rig.WaitForCountAsync(CanOpenProfileRig.Emcy, 1);

        await rig.ResetAsync(NmtCommand.ResetCommunication);
        await rig.WaitForCountAsync(CanOpenProfileRig.Emcy, 2);

        rig.Payloads(CanOpenProfileRig.Emcy)[1].Take(3).Should().Equal(new byte[] { 0x10, 0x21, 0x03 },
            "the reset ended the error; the cause persists, so its EMCY goes out again after the boot-up");
        rig.Od.ReadRaw(0x1001, 0).Should().Equal(new byte[] { 0x03 });
        await rig.WriteU16Async(ControlWord16, 0, 0x0003);
        await rig.WriteU16Async(ControlWord16, 0, 0x000B);
        rig.Settle();
        rig.Device.State.Should().Be(Cia408State.FaultHold, "the cause is still active");
    }

    [Fact]
    public async Task Reset_Communication_With_A_Cause_Asks_For_What_1029h_02_Names_Once_The_Node_Has_Booted()
    {
        using var rig = new Cia408Rig(Cia408Rig.Valve);
        await rig.WriteU8Async(0x1029, 0x02, 0x02);
        await rig.SaveAsync();
        rig.Device.ReportFault(InputCurrentTooHigh, Cia408FaultState.FaultInit);
        await rig.WaitForStateAsync(NmtState.Stopped);

        await rig.ResetAsync(NmtCommand.ResetCommunication);
        await rig.WaitForStateAsync(NmtState.Stopped);
    }

    [Fact]
    public async Task A_Fault_Reported_In_Initialisation_Asks_For_What_1029h_02_Names_Once_The_Node_Has_Booted()
    {
        var device = new Cia408Device(Cia408Rig.Valve);
        bool report = false;
        device.StateChanged += (_, e) =>
        {
            if (report && e.Transitions.Contains(255)) device.ReportFault(InputCurrentTooHigh, Cia408FaultState.FaultInit);
        };
        using var rig = new Cia408Rig(device);
        await rig.WriteU8Async(0x1029, 0x02, 0x02);
        await rig.SaveAsync();
        report = true;

        await rig.ResetAsync(NmtCommand.ResetNode);
        await rig.WaitForStateAsync(NmtState.Stopped);

        rig.Device.State.Should().Be(Cia408State.FaultInit);
    }

    [Fact]
    public async Task A_Subscriber_That_Throws_On_The_Restart_Keeps_The_Persisting_Cause_From_Nothing()
    {
        var device = new Cia408Device(Cia408Rig.Valve);
        device.StateChanged += (_, e) =>
        {
            if (e.Transitions.Contains(255)) throw new InvalidOperationException("subscriber");
        };
        using var rig = new Cia408Rig(device);
        var reports = new List<Exception>();
        rig.Node.BackgroundExceptionOccurred += (_, e) => { lock (reports) reports.Add(e); };
        await rig.WriteU16Async(ControlWord16, 0, 0x0007);
        rig.Settle();
        rig.Device.ReportFault(InputCurrentTooHigh, Cia408FaultState.FaultHold);
        await rig.WaitForCountAsync(CanOpenProfileRig.Emcy, 1);

        await rig.ResetAsync(NmtCommand.ResetNode);
        await rig.WaitForCountAsync(CanOpenProfileRig.Emcy, 2);
        await rig.FlushBackgroundReportsAsync();

        rig.Device.State.Should().Be(Cia408State.FaultInit, "the cause was entered again despite the throw");
        rig.Payloads(CanOpenProfileRig.Emcy)[1].Take(2).Should().Equal(new byte[] { 0x10, 0x21 });
        (await rig.ReadU16Async(ErrorCode, 0)).Should().Be(InputCurrentTooHigh);
        lock (reports) reports.Should().ContainSingle().Which.ToString().Should().Contain("subscriber");
    }

    [Fact]
    public async Task Throws_In_Both_Rounds_Of_A_Restart_Are_Reported_Together()
    {
        var device = new Cia408Device(Cia408Rig.Valve);
        bool armed = false;
        device.StateChanged += (_, e) => { if (armed) throw new InvalidOperationException(Describe(e)); };
        using var rig = new Cia408Rig(device);
        var reports = new List<Exception>();
        rig.Node.BackgroundExceptionOccurred += (_, e) => { lock (reports) reports.Add(e); };
        rig.Device.ReportFault(InputCurrentTooHigh, Cia408FaultState.FaultInit);
        rig.Settle();
        armed = true;

        await rig.ResetAsync(NmtCommand.ResetNode);
        await rig.FlushBackgroundReportsAsync();

        lock (reports)
        {
            reports.Should().ContainSingle().Which.ToString().Should().Contain("FaultInit->Init [255,0,1]")
                .And.Contain("Init->FaultInit [8,13]");
        }
    }

    [Fact]
    public async Task A_Cause_Cleared_By_A_Subscriber_Of_The_Restart_Is_Not_Entered_Again()
    {
        var device = new Cia408Device(Cia408Rig.Valve);
        bool clear = false;
        device.StateChanged += (_, e) =>
        {
            if (clear && e.Transitions.Contains(255)) device.ClearFaultCause(InputCurrentTooHigh);
        };
        using var rig = new Cia408Rig(device);
        rig.Device.ReportFault(InputCurrentTooHigh, Cia408FaultState.FaultHold, errorRegisterBits: 0x02);
        await rig.WaitForCountAsync(CanOpenProfileRig.Emcy, 1);
        await rig.DrainEmcyAsync();
        int emcys = rig.Count(CanOpenProfileRig.Emcy);
        clear = true;

        await rig.ResetAsync(NmtCommand.ResetNode);
        await rig.DrainEmcyAsync();

        rig.Device.State.Should().Be(Cia408State.Init, "the cause was cleared before it could be entered again");
        rig.Od.ReadRaw(0x1001, 0).Should().Equal(new byte[] { 0x00 });
        rig.Count(CanOpenProfileRig.Emcy).Should().Be(emcys, "no EMCY for a cause the application cleared");
    }

    [Fact]
    public async Task Reset_Node_With_A_Cause_Raises_The_Restart_With_The_Status_Of_Init()
    {
        var device = new Cia408Device(Cia408Rig.Valve);
        Cia408Rig? rigRef = null;
        var seen = new List<string>();
        device.StateChanged += (_, e) =>
        {
            if (rigRef is not null) seen.Add($"{Describe(e)} {rigRef.Od.ReadUnsigned(StatusWord16, 0):X4}");
        };
        using var rig = new Cia408Rig(device);
        rig.Settle();
        rig.Device.ReportFault(InputCurrentTooHigh, Cia408FaultState.FaultInit);
        rig.Settle();
        rigRef = rig;

        await rig.ResetAsync(NmtCommand.ResetNode);

        seen.Should().Equal("FaultInit->Init [255,0,1] 0008", "Init->FaultInit [8,13] 0000");
    }

    // ---- helpers --------------------------------------------------------------------------------

    /// <summary>Reaches <paramref name="state"/> from whatever state the device rests in, with local
    /// writes of the control word, which the node takes in every NMT state, Stopped included:
    /// <c>0001h</c>, <c>0000h</c>, <c>0008h</c>, <c>0000h</c> bring every resting state to INIT (Table 7:
    /// D15 then D16 then D14 from FAULT_HOLD, D16 then D14 from FAULT_DISABLED, D14 from FAULT_INIT,
    /// D5 to D7 from the others), then walked from INIT to the state; a fault state is the fault
    /// of its non-fault state, with its cause cleared.</summary>
    private static Task ReachLocallyAsync(Cia408Rig rig, Cia408State state)
    {
        foreach (ushort word in new ushort[] { 0x0001, 0x0000, 0x0008, 0x0000 }) Local(rig, word);
        rig.Settle();
        rig.Device.State.Should().Be(Cia408State.Init);
        switch (state)
        {
            case Cia408State.Init:
                break;
            case Cia408State.Disabled:
                Local(rig, 0x0001);
                break;
            case Cia408State.Hold:
                Local(rig, 0x0003);
                break;
            case Cia408State.Active:
                Local(rig, 0x0007);
                break;
            case Cia408State.FaultInit:
                rig.Settle();
                Fault(rig, Cia408FaultState.FaultInit);
                break;
            case Cia408State.FaultDisabled:
                Local(rig, 0x0001);
                rig.Settle();
                Fault(rig, Cia408FaultState.FaultDisabled);
                break;
            case Cia408State.FaultHold:
                Local(rig, 0x0003);
                rig.Settle();
                Fault(rig, Cia408FaultState.FaultHold);
                break;
        }

        rig.Settle();
        rig.Device.State.Should().Be(state);
        return Task.CompletedTask;
    }

    private static void Local(Cia408Rig rig, ushort word) => rig.Od.WriteRaw(ControlWord16, 0, new[] { (byte)word, (byte)(word >> 8) });

    private static void Fault(Cia408Rig rig, Cia408FaultState reaction)
    {
        rig.Device.ReportFault(InputCurrentTooHigh, reaction);
        rig.Device.ClearFaultCause(InputCurrentTooHigh);
        rig.Settle();
    }

    private static async Task ApplyAsync(Cia408Rig rig, string nmtEvent)
    {
        switch (nmtEvent)
        {
            case "Stop":
                await rig.StopAsync();
                break;
            case "ResetCommunication":
                await rig.ResetAsync(NmtCommand.ResetCommunication);
                break;
            case "ResetNode":
                await rig.ResetAsync(NmtCommand.ResetNode);
                break;
            case "CommunicationFailure":
                rig.Advance(TimeSpan.FromMilliseconds(150));
                break;
            case "Start":
                await rig.StartAsync();
                rig.Settle();
                break;
            case "EnterPreOperational":
                await rig.EnterPreOperationalAsync();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(nmtEvent));
        }
    }

    /// <summary>Makes the device watch the heartbeat of <see cref="Producer"/>, 100 ms, and beats once,
    /// so that 150 ms of silence is a communication failure.</summary>
    private static async Task WatchProducerAsync(Cia408Rig rig)
    {
        rig.Node.AddHeartbeatConsumer(Producer, TimeSpan.FromMilliseconds(100));
        await BeatAsync(rig);
    }

    private static async Task BeatAsync(Cia408Rig rig)
    {
        using var seen = new SemaphoreSlim(0);
        void OnBeat(object? sender, HeartbeatReceivedEventArgs e)
        {
            if (e.ProducerNodeId == Producer) seen.Release();
        }

        rig.Node.HeartbeatReceived += OnBeat;
        try
        {
            rig.Transmit(0x700u + Producer, new[] { (byte)NmtState.Operational });
            (await seen.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue("the node saw the heartbeat");
        }
        finally
        {
            rig.Node.HeartbeatReceived -= OnBeat;
        }

        rig.Settle();
    }

    private static async Task<byte> StatusAsync(Cia408Rig rig)
        => rig.Node.State == NmtState.Stopped
            ? (byte)rig.Od.ReadUnsigned(StatusWord16, 0)
            : (byte)await rig.ReadU16Async(StatusWord16, 0);

    /// <summary>Every entry of the logical device's application area, <c>6000h</c> to <c>67FFh</c>,
    /// as raw bytes.</summary>
    private static Dictionary<uint, byte[]> ApplicationObjects(Cia408Rig rig)
    {
        var snapshot = new Dictionary<uint, byte[]>();
        foreach (var key in rig.Od.SnapshotKeys())
        {
            var index = (ushort)(key >> 8);
            if (index < 0x6000 || index > 0x67FF) continue;
            snapshot[key] = rig.Od.ReadRaw(index, (byte)key);
        }

        return snapshot;
    }

    private static IEnumerable<string> Differences(Dictionary<uint, byte[]> expected, Dictionary<uint, byte[]> actual)
    {
        expected.Keys.Should().BeEquivalentTo(actual.Keys, "a reset neither adds nor removes an entry");
        return expected
            .Where(e => !e.Value.SequenceEqual(actual[e.Key]))
            .Select(e => $"{e.Key >> 8:X4}h:{e.Key & 0xFF:X2}h expected {BitConverter.ToString(e.Value)}, "
                + $"was {BitConverter.ToString(actual[e.Key])}");
    }

    private static string Describe(Cia408StateChangedEventArgs e)
        => $"{e.Previous}->{e.Current} [{string.Join(",", e.Transitions)}]";
}
