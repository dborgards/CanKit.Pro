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

    /// <summary>Each NMT event from each resting state: the state after it and the changes it raised,
    /// written out. Stopped, Reset Communication and a communication failure take D8 into the highest
    /// fault state Table 9 allows from the state (§7.8 Table 10, §7.7 Table 9); Reset Node takes D255,
    /// D0, D1 into INIT (Table 6); Start and Enter Pre-operational move nothing.</summary>
    [Theory]
    [InlineData("Init", "Stop", "FaultInit", "Init->FaultInit [8,13]")]
    [InlineData("Disabled", "Stop", "FaultDisabled", "Disabled->FaultDisabled [8,12]")]
    [InlineData("Hold", "Stop", "FaultHold", "Hold->FaultHold [8,9]")]
    [InlineData("Active", "Stop", "FaultHold", "Active->FaultHold [8,9]")]
    [InlineData("FaultInit", "Stop", "FaultInit", "")]
    [InlineData("FaultDisabled", "Stop", "FaultDisabled", "")]
    [InlineData("FaultHold", "Stop", "FaultHold", "")]
    [InlineData("Init", "ResetCommunication", "FaultInit", "Init->FaultInit [8,13]")]
    [InlineData("Disabled", "ResetCommunication", "FaultDisabled", "Disabled->FaultDisabled [8,12]")]
    [InlineData("Hold", "ResetCommunication", "FaultHold", "Hold->FaultHold [8,9]")]
    [InlineData("Active", "ResetCommunication", "FaultHold", "Active->FaultHold [8,9]")]
    [InlineData("FaultInit", "ResetCommunication", "FaultInit", "")]
    [InlineData("FaultDisabled", "ResetCommunication", "FaultDisabled", "")]
    [InlineData("FaultHold", "ResetCommunication", "FaultHold", "")]
    [InlineData("Init", "CommunicationFailure", "FaultInit", "Init->FaultInit [8,13]")]
    [InlineData("Disabled", "CommunicationFailure", "FaultDisabled", "Disabled->FaultDisabled [8,12]")]
    [InlineData("Hold", "CommunicationFailure", "FaultHold", "Hold->FaultHold [8,9]")]
    [InlineData("Active", "CommunicationFailure", "FaultHold", "Active->FaultHold [8,9]")]
    [InlineData("FaultInit", "CommunicationFailure", "FaultInit", "")]
    [InlineData("FaultDisabled", "CommunicationFailure", "FaultDisabled", "")]
    [InlineData("FaultHold", "CommunicationFailure", "FaultHold", "")]
    [InlineData("Init", "ResetNode", "Init", "Init->Init [255,0,1]")]
    [InlineData("Disabled", "ResetNode", "Init", "Disabled->Init [255,0,1]")]
    [InlineData("Hold", "ResetNode", "Init", "Hold->Init [255,0,1]")]
    [InlineData("Active", "ResetNode", "Init", "Active->Init [255,0,1]")]
    [InlineData("FaultInit", "ResetNode", "Init", "FaultInit->Init [255,0,1]")]
    [InlineData("FaultDisabled", "ResetNode", "Init", "FaultDisabled->Init [255,0,1]")]
    [InlineData("FaultHold", "ResetNode", "Init", "FaultHold->Init [255,0,1]")]
    [InlineData("Init", "Start", "Init", "")]
    [InlineData("Active", "Start", "Active", "")]
    [InlineData("FaultHold", "Start", "FaultHold", "")]
    [InlineData("Active", "EnterPreOperational", "Active", "")]
    [InlineData("FaultDisabled", "EnterPreOperational", "FaultDisabled", "")]
    public async Task Each_Nmt_Event_From_Each_Resting_State(string from, string nmtEvent, string expected, string changes)
    {
        using var rig = new Cia408Rig(Cia408Rig.Valve);
        await ReachAsync(rig, (Cia408State)Enum.Parse(typeof(Cia408State), from));
        if (nmtEvent == "EnterPreOperational") await rig.StartAsync();
        if (nmtEvent == "CommunicationFailure") await WatchProducerAsync(rig);
        rig.Settle();
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

    // ---- helpers --------------------------------------------------------------------------------

    private static async Task ReachAsync(Cia408Rig rig, Cia408State state)
    {
        switch (state)
        {
            case Cia408State.Init:
                break;
            case Cia408State.Disabled:
                await rig.WriteU16Async(ControlWord16, 0, 0x0001);
                break;
            case Cia408State.Hold:
                await rig.WriteU16Async(ControlWord16, 0, 0x0003);
                break;
            case Cia408State.Active:
                await rig.WriteU16Async(ControlWord16, 0, 0x0007);
                break;
            case Cia408State.FaultInit:
                await FaultAsync(rig, Cia408FaultState.FaultInit);
                break;
            case Cia408State.FaultDisabled:
                await rig.WriteU16Async(ControlWord16, 0, 0x0001);
                await FaultAsync(rig, Cia408FaultState.FaultDisabled);
                break;
            case Cia408State.FaultHold:
                await rig.WriteU16Async(ControlWord16, 0, 0x0003);
                await FaultAsync(rig, Cia408FaultState.FaultHold);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(state));
        }

        rig.Settle();
        rig.Device.State.Should().Be(state);
    }

    /// <summary>A fault and the end of its cause: the device rests in the fault state with no cause
    /// left.</summary>
    private static Task FaultAsync(Cia408Rig rig, Cia408FaultState reaction)
    {
        rig.Settle();
        rig.Device.ReportFault(InputCurrentTooHigh, reaction);
        rig.Device.ClearFaultCause(InputCurrentTooHigh);
        rig.Settle();
        return Task.CompletedTask;
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
