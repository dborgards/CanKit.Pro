using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Application;
using CanKit.Pro.CANopen.Cia408;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.Tests.Infrastructure;
using EdsDcfNet.Models;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Cia408;

/// <summary>
/// The tool side of CiA 408 v2.2.0: <see cref="Cia408Peer"/> reads and drives a foreign CiA 408
/// device over the bus through the node API only, and names what an observed PDO of it carried.
/// Every test that touches the bus runs a real tool node beside a <see cref="Cia408Device"/>
/// (<see cref="Cia408ToolRig"/>), so every access goes through the tool's SDO client and its peer SDO
/// gate. Indices, control words and abort codes are written out from the norm here, not taken from
/// <see cref="Cia408Objects"/>, and what the peer did is checked on the wire (the raw master sees the
/// tool's requests) and on the device's side.
/// </summary>
public class Cia408PeerTests
{
    private const byte DeviceId = CanOpenProfileRig.DeviceId;
    private const ushort ControlWord16 = 0x6040;
    private const ushort StatusWord16 = 0x6041;
    private const uint ValueRangeExceeded = 0x0609_0030;
    private const uint AttemptWriteReadOnly = 0x0601_0002;

    /// <summary>CiA 408 v2.2.0 §6.3, Table 1: input current too high.</summary>
    private const ushort InputCurrentTooHigh = 0x2110;

    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

    /// <summary>A tool node alone on a bus, for what needs a node but no device: making a peer and
    /// interpreting signals perform no bus access.</summary>
    private sealed class LoneTool : IDisposable
    {
        private readonly CanKit.Abstractions.API.Can.ICanBus _bus;

        internal LoneTool()
        {
            _bus = VirtualAdapterFixture.Open(VirtualAdapterFixture.NewSession("cia408-peer"), 0);
            Node = CanOpen.OpenNode(_bus, nodeId: 0x01);
        }

        internal ICanOpenNode Node { get; }

        internal Cia408Peer Peer(uint deviceType, int logicalDevice = 0)
            => (Cia408Peer)new Cia408Profile().CreatePeer(Node, DeviceId,
                new CanOpenLogicalDevice(logicalDevice, new CanOpenDeviceType(deviceType)));

        public void Dispose()
        {
            Node.Dispose();
            _bus.Dispose();
        }
    }

    private static ForeignPdoSignal Signal(ushort index, byte subindex, int bits, params byte[] value)
        => new(DeviceId, ForeignPdoKind.Tpdo, 1, 0x180 + DeviceId, index, subindex, value,
            ForeignPdoMappingOrigin.LiveMapping, bits);

    /// <summary>
    /// Every SDO request the tool sent to the device so far, in wire order. The barrier: one upload
    /// of <c>1000h:00</c> the gate always lets through, awaited, and seen by the raw master as its
    /// latest request; the tool sends its requests in order and the bus delivers them in order, so
    /// everything sent before it has been seen. The barrier itself is not returned.
    /// </summary>
    private static async Task<byte[][]> RequestsAsync(Cia408ToolRig rig)
    {
        await rig.Tool.SdoUploadAsync(DeviceId, 0x1000, 0x00).WithTimeoutAsync(HangGuard);
        await CanOpenProfileRig.WaitUntilAsync(() => IsBarrier(rig.Rig.Payloads(CanOpenProfileRig.SdoRequest).LastOrDefault()),
            "the barrier request");
        var requests = rig.Rig.Payloads(CanOpenProfileRig.SdoRequest);
        return requests.Take(requests.Length - 1).ToArray();

        static bool IsBarrier(byte[]? request)
            => request is { Length: 8 } && request[0] == 0x40 && request[1] == 0x00 && request[2] == 0x10 && request[3] == 0x00;
    }

    /// <summary>The values the tool downloaded to <paramref name="index"/>:00h, in order, from the
    /// expedited download requests among <paramref name="requests"/> (CiA 301 v4.2.0 §7.2.4.3.3).</summary>
    private static uint[] Downloads(byte[][] requests, ushort index)
        => requests
            .Where(r => (r[0] & 0xE3) == 0x23 && r[1] == (byte)index && r[2] == (byte)(index >> 8) && r[3] == 0x00)
            .Select(r =>
            {
                int length = 4 - ((r[0] >> 2) & 0x03);
                uint value = 0;
                for (int i = 0; i < length; i++) value |= (uint)r[4 + i] << (8 * i);
                return value;
            })
            .ToArray();

    /// <summary>The uploads of <paramref name="index"/>:00h among <paramref name="requests"/>.</summary>
    private static int Uploads(byte[][] requests, ushort index)
        => requests.Count(r => r[0] == 0x40 && r[1] == (byte)index && r[2] == (byte)(index >> 8) && r[3] == 0x00);

    // ---- the descriptor and what the device type says ------------------------------------------------

    [Fact]
    public void CreatePeer_Refuses_A_Logical_Device_Of_Another_Profile_And_Bad_Arguments()
    {
        using var tool = new LoneTool();
        var profile = new Cia408Profile();

        Action other = () => profile.CreatePeer(tool.Node, DeviceId, new CanOpenLogicalDevice(0, new CanOpenDeviceType(0x00030191)));
        Action noNode = () => profile.CreatePeer(null!, DeviceId, new CanOpenLogicalDevice(0, new CanOpenDeviceType(0x22000198)));
        Action noDevice = () => profile.CreatePeer(tool.Node, DeviceId, null!);
        Action badId = () => profile.CreatePeer(tool.Node, 0, new CanOpenLogicalDevice(0, new CanOpenDeviceType(0x22000198)));

        other.Should().ThrowExactly<ArgumentException>().WithParameterName("logicalDevice");
        noNode.Should().ThrowExactly<ArgumentNullException>().WithParameterName("node");
        noDevice.Should().ThrowExactly<ArgumentNullException>().WithParameterName("logicalDevice");
        badId.Should().ThrowExactly<ArgumentOutOfRangeException>();
    }

    [Theory]
    // pQ valve, dtprs 01b: bit 25 is pQ (CiA 408 v2.2.0 §8.6.2, Table 53).
    [InlineData(0x2200_0198u, new[] { 5 }, 1, false)]
    // bit 23, spool position control, names modes 1 and 2; the word does not tell them apart.
    [InlineData(0x2080_0198u, new[] { 1, 2 }, 1, false)]
    // dtprs 01b with bit 17 set: Table 53 NOTE 1 makes the drive bits "do not care" for a 16-bit valve.
    [InlineData(0x2202_0198u, new[] { 5 }, 1, false)]
    // a 32-bit drive in velocity control: dtprs 10b, bit 17 is mode 7.
    [InlineData(0x4002_0198u, new[] { 7 }, 2, true)]
    // dtprs 11b with valve modes only (bit 24: modes 3 and 4): the 16-bit pair.
    [InlineData(0x6100_0198u, new[] { 3, 4 }, 3, false)]
    // dtprs 11b with drive modes only (bits 17 and 19): the 32-bit pair.
    [InlineData(0x600A_0198u, new[] { 7, 9 }, 3, true)]
    // the reserved dtprs 00b: no resolution, the 16-bit pair; bits 26 to 28 are modes 13 to 15.
    [InlineData(0x1C00_0198u, new[] { 13, 14, 15 }, 0, false)]
    public void The_Device_Type_Gives_The_Control_Modes_The_Resolution_And_The_Word_Pair(
        uint deviceType, int[] modes, int dtprs, bool wide)
    {
        using var tool = new LoneTool();

        var peer = tool.Peer(deviceType);

        peer.SupportedControlModes.Select(m => (int)m).Should().Equal(modes);
        peer.Resolution.Should().Be(dtprs == 0 ? null : (Cia408Resolution)dtprs);
        peer.UsesWideWords.Should().Be(wide);
    }

    [Fact]
    public void Valve_And_Drive_Modes_Of_Resolution_11b_Leave_The_Word_Pair_To_The_Running_Mode()
    {
        using var tool = new LoneTool();

        // Bit 19 (mode 9, a drive: 32 bit) beside bit 23 (modes 1 and 2, valves: 16 bit on 11b).
        var peer = tool.Peer(0x6088_0198);

        peer.SupportedControlModes.Select(m => (int)m).Should().Equal(1, 2, 9);
        peer.UsesWideWords.Should().BeNull("the pair follows the mode in 6043h");
    }

    [Fact]
    public async Task A_Discovered_Device_Gives_A_Cia408_Peer()
    {
        using var rig = new Cia408ToolRig();

        var devices = await CanOpenLogicalDevices.ReadAsync(rig.Tool, DeviceId).WithTimeoutAsync(HangGuard);
        var peers = new CanOpenProfileCatalog(new Cia408Profile()).CreatePeers(rig.Tool, DeviceId, devices);

        var peer = peers.Should().ContainSingle().Which.Should().BeOfType<Cia408Peer>().Which;
        peer.Profile.Should().BeOfType<Cia408Profile>();
        peer.LogicalDevice.DeviceType.Value.Should().Be(0x2200_0198u);
        peer.SupportedControlModes.Should().Equal(Cia408ControlMode.PQControl);
        peer.Resolution.Should().Be(Cia408Resolution.Valve16);
        (await peer.ReadStatusAsync().WithTimeoutAsync(HangGuard)).Should().Be(new Cia408Status(0x0008, Cia408State.Init));
    }

    // ---- the status word ---------------------------------------------------------------------------------

    [Fact]
    public async Task The_Status_Word_Is_Decoded_Into_State_Local_Warning_And_The_Other_Bits()
    {
        using var rig = new Cia408ToolRig();
        var peer = rig.Peer();
        // R M H D = 1011b (HOLD, Table 4), L, W, and bits 7, 13 and 15 (mode- and manufacturer-specific).
        rig.Rig.Od.WriteRawUnchecked(StatusWord16, 0x00, new byte[] { 0xBB, 0xA0 });

        var status = await peer.ReadStatusAsync().WithTimeoutAsync(HangGuard);

        status.Word.Should().Be(0xA0BBu);
        status.State.Should().Be(Cia408State.Hold);
        status.Local.Should().BeTrue();
        status.Warning.Should().BeTrue();
        status.OtherBits.Should().Be(0xA080u);
    }

    [Theory]
    [InlineData(0x0000, (int)Cia408State.FaultInit)] // 0000b: FAULT_INIT, which shares it with NOT_READY
    [InlineData(0x0001, (int)Cia408State.FaultDisabled)]
    [InlineData(0x0003, (int)Cia408State.FaultHold)]
    [InlineData(0x0008, (int)Cia408State.Init)]
    [InlineData(0x0009, (int)Cia408State.Disabled)]
    [InlineData(0x000F, (int)Cia408State.Active)]
    public async Task Each_State_Of_Table_4_Is_Decoded(ushort word, int state)
    {
        using var rig = new Cia408ToolRig();
        rig.Rig.Od.WriteRawUnchecked(StatusWord16, 0x00, new[] { (byte)word, (byte)(word >> 8) });

        (await rig.Peer().ReadStatusAsync().WithTimeoutAsync(HangGuard)).State.Should().Be((Cia408State)state);
    }

    [Theory]
    [InlineData(0x000A)] // 1010b
    [InlineData(0x000C)] // 1100b
    [InlineData(0x000E)] // 1110b
    public async Task Bits_With_R_Set_That_Encode_No_State_Are_Invalid_Data(ushort word)
    {
        using var rig = new Cia408ToolRig();
        rig.Rig.Od.WriteRawUnchecked(StatusWord16, 0x00, new[] { (byte)word, (byte)(word >> 8) });

        Func<Task> read = () => rig.Peer().ReadStatusAsync().WithTimeoutAsync(HangGuard);

        await read.Should().ThrowExactlyAsync<InvalidDataException>("neither Figure 1 nor Table 4 gives R = 1 with these bits a state");
    }

    [Theory]
    [InlineData(0x0002)] // 0010b
    [InlineData(0x0004)] // 0100b
    [InlineData(0x0007)] // 0111b
    public async Task Bits_With_R_Clear_That_Are_No_Fault_State_Read_As_Fault_Reaction(ushort word)
    {
        using var rig = new Cia408ToolRig();
        rig.Rig.Od.WriteRawUnchecked(StatusWord16, 0x00, new[] { (byte)word, (byte)(word >> 8) });

        (await rig.Peer().ReadStatusAsync().WithTimeoutAsync(HangGuard)).State
            .Should().Be(Cia408State.FaultReaction, "Figure 1 gives FAULT_REACTION 0xxx");
    }

    [Fact]
    public async Task A_Command_Reads_Through_Fault_Reaction_And_Fails_When_It_Lasts()
    {
        using var rig = new Cia408ToolRig();
        rig.Rig.Od.WriteRawUnchecked(StatusWord16, 0x00, new byte[] { 0x07, 0x00 }); // 0111b: FAULT_REACTION
        var peer = rig.Peer(options: rig.Options(reads: 3, pause: TimeSpan.FromMilliseconds(5)));

        Func<Task> enable = () => peer.EnableAsync().WithTimeoutAsync(HangGuard);

        var failure = (await enable.Should().ThrowExactlyAsync<Cia408CommandException>()).Which;
        failure.Reason.Should().Be(Cia408CommandFailure.NotReached);
        failure.State.Should().Be(Cia408State.FaultReaction);
        rig.Pauses.Should().Equal(TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(5));
        Uploads(await RequestsAsync(rig), StatusWord16).Should().Be(3);
        Downloads(await RequestsAsync(rig), ControlWord16).Should().BeEmpty("no resting state was seen, so nothing was written");
    }

    [Fact]
    public async Task A_Command_Goes_On_Once_Fault_Reaction_Has_Passed()
    {
        using var rig = new Cia408ToolRig();
        rig.Rig.Od.WriteRawUnchecked(StatusWord16, 0x00, new byte[] { 0x07, 0x00 });
        // During the first pause the device shows its resting state again, INIT.
        rig.OnPause = () => rig.Rig.Od.WriteRawUnchecked(StatusWord16, 0x00, new byte[] { 0x08, 0x00 });

        await rig.Peer().EnableAsync().WithTimeoutAsync(HangGuard);

        rig.Pauses.Should().ContainSingle();
        rig.Rig.Device.State.Should().Be(Cia408State.Active);
    }

    // ---- the command sequences of Table 7 ----------------------------------------------------------------

    [Theory]
    [InlineData((int)Cia408State.Disabled, new uint[] { 0x0001 })]                 // D2
    [InlineData((int)Cia408State.Hold, new uint[] { 0x0001, 0x0003 })]             // D2, D3
    [InlineData((int)Cia408State.Active, new uint[] { 0x0001, 0x0003, 0x0007 })]   // D2, D3, D4
    public async Task From_Init_Each_Command_Writes_One_Word_Per_Transition_And_Ends_In_Its_State(int state, uint[] words)
    {
        var target = (Cia408State)state;
        using var rig = new Cia408ToolRig();
        var peer = rig.Peer();

        await Command(peer, target).WithTimeoutAsync(HangGuard);

        Downloads(await RequestsAsync(rig), ControlWord16).Should().Equal(words);
        rig.Rig.Device.State.Should().Be(target);
        (await peer.ReadStatusAsync().WithTimeoutAsync(HangGuard)).State.Should().Be(target);
    }

    [Fact]
    public async Task The_Commands_Walk_Up_And_Down_From_Wherever_The_Device_Is()
    {
        using var rig = new Cia408ToolRig();
        var peer = rig.Peer();
        var steps = new (Cia408State Target, uint[] Words)[]
        {
            (Cia408State.Active, new uint[] { 0x0001, 0x0003, 0x0007 }),
            (Cia408State.Hold, new uint[] { 0x0003 }),                   // D5
            (Cia408State.Disabled, new uint[] { 0x0001 }),               // D6
            (Cia408State.Active, new uint[] { 0x0003, 0x0007 }),         // D3, D4
            (Cia408State.Disabled, new uint[] { 0x0003, 0x0001 }),       // D5, D6
            (Cia408State.Hold, new uint[] { 0x0003 }),                   // D3
            (Cia408State.Active, new uint[] { 0x0007 }),                 // D4
            (Cia408State.Active, Array.Empty<uint>()),                   // there already: nothing written
        };

        int seen = 0;
        foreach (var (target, words) in steps)
        {
            await Command(peer, target).WithTimeoutAsync(HangGuard);
            var all = Downloads(await RequestsAsync(rig), ControlWord16);
            all.Skip(seen).Should().Equal(words, $"the way to {target}");
            seen = all.Length;
            rig.Rig.Device.State.Should().Be(target);
        }
    }

    [Fact]
    public async Task A_Command_In_A_Fault_State_Fails_Without_Writing()
    {
        using var rig = new Cia408ToolRig();
        var peer = rig.Peer();
        rig.Rig.Device.ReportFault(InputCurrentTooHigh, Cia408FaultState.FaultHold);
        rig.Rig.Settle();
        rig.Rig.Device.State.Should().Be(Cia408State.FaultInit, "Table 9 bounds a fault from INIT to FAULT_INIT");

        Func<Task> enable = () => peer.EnableAsync().WithTimeoutAsync(HangGuard);

        var failure = (await enable.Should().ThrowExactlyAsync<Cia408CommandException>()).WithMessage("*fault*ResetFaultAsync*").Which;
        failure.Reason.Should().Be(Cia408CommandFailure.FaultState);
        failure.State.Should().Be(Cia408State.FaultInit);
        Downloads(await RequestsAsync(rig), ControlWord16).Should().BeEmpty();
    }

    [Fact]
    public async Task A_Device_That_Faults_On_The_Way_Fails_The_Command_With_Its_Fault_State()
    {
        using var rig = new Cia408ToolRig();
        var peer = rig.Peer();
        // As soon as D2 has taken the device to DISABLED, the application reports a fault.
        rig.Rig.Device.StateChanged += (_, e) =>
        {
            if (e.Current == Cia408State.Disabled) rig.Rig.Device.ReportFault(InputCurrentTooHigh, Cia408FaultState.FaultDisabled);
        };

        Func<Task> enable = () => peer.EnableAsync().WithTimeoutAsync(HangGuard);

        var failure = (await enable.Should().ThrowExactlyAsync<Cia408CommandException>()).WithMessage("*fell into FaultDisabled*").Which;
        failure.Reason.Should().Be(Cia408CommandFailure.FaultState);
        failure.State.Should().Be(Cia408State.FaultDisabled);
        Downloads(await RequestsAsync(rig), ControlWord16).Should().Equal(new uint[] { 0x0001 }, "the walk stops where the fault caught it");
    }

    [Fact]
    public async Task A_Device_That_Switches_To_Local_On_The_Way_Fails_The_Command_As_Local_Control()
    {
        using var rig = new Cia408ToolRig();
        var peer = rig.Peer();
        // When D2 has taken the device to DISABLED, the status still shows INIT for one read (a device
        // slower than an SDO round trip), so the peer pauses; during the pause a master switches the
        // device to its local source (604Fh = 1), and the next read shows DISABLED under local control.
        rig.Rig.Device.StateChanged += (_, e) =>
        {
            if (e.Current == Cia408State.Disabled) rig.Rig.Od.WriteRawUnchecked(StatusWord16, 0x00, new byte[] { 0x08, 0x00 });
        };
        int switched = 0;
        rig.OnPause = () =>
        {
            if (System.Threading.Interlocked.Exchange(ref switched, 1) == 0)
                rig.Rig.WriteU8Async(0x604F, 0x00, 0x01).GetAwaiter().GetResult();
        };

        Func<Task> enable = () => peer.EnableAsync().WithTimeoutAsync(HangGuard);

        var failure = (await enable.Should().ThrowExactlyAsync<Cia408CommandException>()).WithMessage("*local control*").Which;
        failure.Reason.Should().Be(Cia408CommandFailure.LocalControl);
        rig.Rig.Device.State.Should().Be(Cia408State.Disabled);
    }

    [Fact]
    public async Task A_Device_In_Local_Control_Is_Refused_Before_A_Control_Word_Is_Written()
    {
        using var rig = new Cia408ToolRig();
        var peer = rig.Peer();
        await peer.WriteLocalAsync(true).WithTimeoutAsync(HangGuard);
        rig.Rig.Settle();

        Func<Task> enable = () => peer.EnableAsync().WithTimeoutAsync(HangGuard);
        Func<Task> reset = () => peer.ResetFaultAsync().WithTimeoutAsync(HangGuard);

        (await enable.Should().ThrowExactlyAsync<Cia408CommandException>()).WithMessage("*local control*604Fh*")
            .Which.Reason.Should().Be(Cia408CommandFailure.LocalControl);
        (await reset.Should().ThrowExactlyAsync<Cia408CommandException>()).WithMessage("*local control*")
            .Which.Reason.Should().Be(Cia408CommandFailure.LocalControl);
        (await peer.ReadStatusAsync().WithTimeoutAsync(HangGuard)).Local.Should().BeTrue("status bit 4 mirrors 604Fh (§9.3.9)");
        Downloads(await RequestsAsync(rig), ControlWord16).Should().BeEmpty();
        rig.Rig.Device.State.Should().Be(Cia408State.Init);
    }

    [Fact] // the tool side of Review Focus 1
    public async Task ResetFaultAsync_Writes_The_Pattern_Then_The_Edge_And_Leaves_The_Fault_State()
    {
        using var rig = new Cia408ToolRig();
        var peer = rig.Peer();
        await peer.EnableAsync().WithTimeoutAsync(HangGuard);
        rig.Rig.Device.ReportFault(InputCurrentTooHigh, Cia408FaultState.FaultHold);
        rig.Rig.Settle();
        rig.Rig.Device.State.Should().Be(Cia408State.FaultHold);
        rig.Rig.Device.ClearFaultCause(InputCurrentTooHigh);
        rig.Rig.Settle();
        int before = Downloads(await RequestsAsync(rig), ControlWord16).Length;

        await peer.ResetFaultAsync().WithTimeoutAsync(HangGuard);

        // The device holds 0007h, the word it faulted under: only 0011b then 1011b is the edge of D11.
        Downloads(await RequestsAsync(rig), ControlWord16).Skip(before).Should().Equal(0x0003u, 0x000Bu);
        rig.Rig.Device.State.Should().Be(Cia408State.Hold, "D11: FAULT_HOLD to HOLD");
        (await peer.ReadErrorCodeAsync().WithTimeoutAsync(HangGuard)).Should().Be(InputCurrentTooHigh, "604Eh keeps the fault that occurred");

        await peer.EnableAsync().WithTimeoutAsync(HangGuard);
        rig.Rig.Device.State.Should().Be(Cia408State.Active);
    }

    [Theory]
    [InlineData((int)Cia408FaultState.FaultDisabled, new uint[] { 0x0001, 0x0009 }, (int)Cia408State.Disabled)] // D10
    [InlineData((int)Cia408FaultState.FaultInit, new uint[] { 0x0000, 0x0008 }, (int)Cia408State.Init)]         // D14
    public async Task ResetFaultAsync_Uses_The_Pattern_Of_The_Fault_State_It_Finds(int fault, uint[] words, int state)
    {
        var reaction = (Cia408FaultState)fault;
        var after = (Cia408State)state;
        using var rig = new Cia408ToolRig();
        var peer = rig.Peer();
        await peer.EnableAsync().WithTimeoutAsync(HangGuard);
        rig.Rig.Device.ReportFault(InputCurrentTooHigh, reaction);
        rig.Rig.Device.ClearFaultCause(InputCurrentTooHigh);
        rig.Rig.Settle();
        int before = Downloads(await RequestsAsync(rig), ControlWord16).Length;

        await peer.ResetFaultAsync().WithTimeoutAsync(HangGuard);

        Downloads(await RequestsAsync(rig), ControlWord16).Skip(before).Should().Equal(words);
        rig.Rig.Device.State.Should().Be(after);
    }

    [Fact]
    public async Task A_Reset_The_Device_Refuses_Fails_After_The_Bounded_Reads_And_Does_Not_Hang()
    {
        using var rig = new Cia408ToolRig();
        var pause = TimeSpan.FromMilliseconds(7);
        var peer = rig.Peer(options: rig.Options(reads: 4, pause: pause));
        await peer.EnableAsync().WithTimeoutAsync(HangGuard);
        rig.Rig.Device.ReportFault(InputCurrentTooHigh, Cia408FaultState.FaultHold);
        rig.Rig.Settle();
        var earlier = await RequestsAsync(rig);
        long before = rig.PauseClock.GetTimestamp();

        Func<Task> reset = () => peer.ResetFaultAsync().WithTimeoutAsync(HangGuard);

        var failure = (await reset.Should().ThrowExactlyAsync<Cia408CommandException>()).WithMessage("*refused the fault reset*FaultHold*").Which;
        failure.Reason.Should().Be(Cia408CommandFailure.NotReached);
        failure.State.Should().Be(Cia408State.FaultHold);
        var requests = (await RequestsAsync(rig)).Skip(earlier.Length).ToArray();
        Downloads(requests, ControlWord16).Should().Equal(0x0003u, 0x000Bu);
        Uploads(requests, StatusWord16).Should().Be(1 + 4,
            "one read finds the fault state, then the four reads the options allow after the edge, and not one more");
        rig.Pauses.Should().Equal(new[] { pause, pause, pause }, "a pause before each read after the edge but the first");
        TimeSpan.FromTicks((rig.PauseClock.GetTimestamp() - before) * TimeSpan.TicksPerSecond / rig.PauseClock.Frequency)
            .Should().Be(TimeSpan.FromMilliseconds(21), "the pauses ran on the virtual clock");
        rig.Rig.Device.State.Should().Be(Cia408State.FaultHold, "the device refuses the reset while the cause lasts");
    }

    [Fact]
    public async Task ResetFaultAsync_Outside_A_Fault_State_Fails_Without_Writing()
    {
        using var rig = new Cia408ToolRig();

        Func<Task> reset = () => rig.Peer().ResetFaultAsync().WithTimeoutAsync(HangGuard);

        (await reset.Should().ThrowExactlyAsync<Cia408CommandException>()).WithMessage("*not in a fault state*")
            .Which.Reason.Should().Be(Cia408CommandFailure.NoFault);
        Downloads(await RequestsAsync(rig), ControlWord16).Should().BeEmpty();
    }

    [Fact]
    public async Task WriteControlWordAsync_Writes_The_Word_As_Given()
    {
        using var rig = new Cia408ToolRig();

        await rig.Peer().WriteControlWordAsync(0x0F01).WithTimeoutAsync(HangGuard);
        rig.Rig.Settle();

        rig.Rig.Od.ReadUnsigned(ControlWord16, 0x00).Should().Be(0x0F01u);
        rig.Rig.Device.State.Should().Be(Cia408State.Disabled, "bits 3..0 = 0001b take D2; bits 8 to 11 are stored");
    }

    // ---- modes, error code, source ----------------------------------------------------------------------

    [Fact]
    public async Task The_Mode_Objects_Read_And_Write_Through_The_Device()
    {
        using var rig = new Cia408ToolRig();
        var peer = rig.Peer();

        (await peer.ReadDeviceModeAsync().WithTimeoutAsync(HangGuard)).Should().Be(Cia408DeviceMode.SetpointViaBus, "§9.3.4: 01h");
        (await peer.ReadControlModeAsync().WithTimeoutAsync(HangGuard)).Should().Be(Cia408ControlMode.PQControl);
        (await peer.ReadErrorCodeAsync().WithTimeoutAsync(HangGuard)).Should().Be(0);
        (await peer.ReadLocalAsync().WithTimeoutAsync(HangGuard)).Should().BeFalse("§9.3.9: a valve defaults to the bus");

        Func<Task> controlMode = () => peer.WriteControlModeAsync(Cia408ControlMode.PQControl).WithTimeoutAsync(HangGuard);
        Func<Task> deviceMode = () => peer.WriteDeviceModeAsync(Cia408DeviceMode.SetpointLocal).WithTimeoutAsync(HangGuard);
        (await controlMode.Should().ThrowExactlyAsync<SdoAbortException>()).Which.AbortCode
            .Should().Be(AttemptWriteReadOnly, "6043h is read-only with one control mode (§9.3.5)");
        (await deviceMode.Should().ThrowExactlyAsync<SdoAbortException>()).Which.AbortCode
            .Should().Be(ValueRangeExceeded, "the device supports setpoint via bus only");

        await peer.WriteDeviceModeAsync(Cia408DeviceMode.SetpointViaBus).WithTimeoutAsync(HangGuard);
        await peer.WriteLocalAsync(true).WithTimeoutAsync(HangGuard);
        rig.Rig.Od.ReadRaw(0x604F, 0x00).Should().Equal(new byte[] { 0x01 });
        (await peer.ReadLocalAsync().WithTimeoutAsync(HangGuard)).Should().BeTrue();

        rig.Rig.Device.ReportFault(InputCurrentTooHigh, Cia408FaultState.FaultHold);
        rig.Rig.Settle();
        (await peer.ReadErrorCodeAsync().WithTimeoutAsync(HangGuard)).Should().Be(InputCurrentTooHigh);
    }

    [Fact]
    public async Task A_Local_Value_Other_Than_0_Or_1_Is_Invalid_Data()
    {
        using var rig = new Cia408ToolRig();
        rig.Rig.Od.WriteRawUnchecked(0x604F, 0x00, new byte[] { 0xFF }); // -1: manufacturer-specific (Table 93)

        Func<Task> read = () => rig.Peer().ReadLocalAsync().WithTimeoutAsync(HangGuard);

        await read.Should().ThrowExactlyAsync<InvalidDataException>();
    }

    // ---- value records -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_Value_Record_Is_Read_With_The_Unit_And_Prefix_The_Device_Reports()
    {
        using var rig = new Cia408ToolRig();
        rig.Rig.Device.SetActualValue(Cia408Objects.SpoolPositionActualValue, -1234);
        // The catalogue gives "ir" for the unit, which the device reports as 00h with prefix 00h; a
        // device whose manufacturer fixed other ones (§8.3) reports those, and the peer must read them.
        rig.Rig.Od.WriteRawUnchecked(0x6301, 0x02, new byte[] { 0xA2 });
        rig.Rig.Od.WriteRawUnchecked(0x6301, 0x03, new byte[] { 0xFD });
        rig.Rig.Settle();

        var record = await rig.Peer().ReadValueAsync(Cia408Objects.SpoolPositionActualValue).WithTimeoutAsync(HangGuard);

        record.Should().Be(new Cia408ValueRecord(-1234, 0xA2, -3));
    }

    [Fact]
    public async Task A_Setpoint_Is_Written_In_Its_Value_Type()
    {
        using var rig = new Cia408ToolRig();
        var peer = rig.Peer();

        await peer.WriteValueAsync(Cia408Objects.QSetpoint, -5000).WithTimeoutAsync(HangGuard);

        rig.Rig.Od.ReadSigned(0x6300, 0x01).Should().Be(-5000);
        (await peer.ReadValueAsync(Cia408Objects.QSetpoint).WithTimeoutAsync(HangGuard))
            .Should().Be(new Cia408ValueRecord(-5000, 0x00, 0));
    }

    [Fact]
    public async Task ReadAsync_And_WriteAsync_Reach_A_Catalogue_Object_Raw()
    {
        using var rig = new Cia408ToolRig();
        var peer = rig.Peer();

        await peer.WriteAsync(Cia408Objects.VprcSetpoint, 0x01, new byte[] { 0x34, 0x12 }).WithTimeoutAsync(HangGuard);

        rig.Rig.Od.ReadRaw(0x6380, 0x01).Should().Equal(0x34, 0x12);
        (await peer.ReadAsync(Cia408Objects.VprcSetpoint, 0x00).WithTimeoutAsync(HangGuard)).Should().Equal(0x03);
    }

    // ---- what is refused, and by whom ---------------------------------------------------------------------

    [Fact]
    public async Task A_Peer_Without_A_Bound_Description_Is_Refused_By_The_Gate()
    {
        using var rig = new Cia408ToolRig(bindDescription: false);
        var peer = rig.Peer();

        Func<Task> status = () => peer.ReadStatusAsync().WithTimeoutAsync(HangGuard);
        Func<Task> enable = () => peer.EnableAsync().WithTimeoutAsync(HangGuard);
        Func<Task> value = () => peer.WriteValueAsync(Cia408Objects.QSetpoint, 1).WithTimeoutAsync(HangGuard);

        var refused = (await status.Should().ThrowExactlyAsync<PeerSdoAccessException>()).Which;
        refused.PeerDescriptionLoaded.Should().BeFalse();
        refused.Index.Should().Be(StatusWord16);
        (await enable.Should().ThrowExactlyAsync<PeerSdoAccessException>()).Which.Index.Should().Be(StatusWord16);
        (await value.Should().ThrowExactlyAsync<PeerSdoAccessException>()).Which.Index.Should().Be(0x6300);
        (await RequestsAsync(rig)).Should().BeEmpty("the refused calls sent nothing");
        rig.Rig.Device.State.Should().Be(Cia408State.Init);
    }

    [Fact]
    public void Argument_Errors_Are_Thrown_Before_A_Task_Exists()
    {
        using var tool = new LoneTool();
        var valve = tool.Peer(0x2200_0198);
        var drive = tool.Peer(0x4002_0198);

        ((Action)(() => _ = valve.WriteControlWordAsync(0x0010))).Should().ThrowExactly<ArgumentOutOfRangeException>("bit 4 is reserved (Table 73)");
        ((Action)(() => _ = valve.WriteControlWordAsync(0x1000))).Should().ThrowExactly<ArgumentOutOfRangeException>("bit 12 is reserved");
        ((Action)(() => _ = valve.WriteControlWordAsync(0x0001_0001))).Should().ThrowExactly<ArgumentOutOfRangeException>("6040h has 16 bits");
        ((Action)(() => _ = drive.WriteControlWordAsync(0x0100_0001))).Should().ThrowExactly<ArgumentOutOfRangeException>("bit 24 of 6044h is reserved (Table 85)");
        ((Action)(() => _ = valve.ReadValueAsync(null!))).Should().ThrowExactly<ArgumentNullException>();
        ((Action)(() => _ = valve.ReadValueAsync(Cia408Objects.DeviceMode))).Should().ThrowExactly<ArgumentException>("a VAR has no unit");
        ((Action)(() => _ = valve.WriteValueAsync(Cia408Objects.QSetpoint, 32768))).Should().ThrowExactly<ArgumentOutOfRangeException>("INTEGER16");
        ((Action)(() => _ = valve.WriteValueAsync(Cia408Objects.QSetpoint, -32769))).Should().ThrowExactly<ArgumentOutOfRangeException>();
        ((Action)(() => _ = valve.WriteValueAsync(Cia408Objects.SpoolPositionActualValue, 1))).Should().ThrowExactly<InvalidOperationException>("an actual value is ro");
        ((Action)(() => _ = valve.WriteAsync(Cia408Objects.DeviceStatusWord16, 0x00, new byte[2]))).Should().ThrowExactly<InvalidOperationException>();
        ((Action)(() => _ = valve.WriteAsync(Cia408Objects.QSetpoint, 0x00, new byte[] { 3 }))).Should().ThrowExactly<InvalidOperationException>("sub-index 00h of a value record is ro");
        ((Action)(() => _ = valve.WriteAsync(null!, 0x00, new byte[1]))).Should().ThrowExactly<ArgumentNullException>();
        ((Action)(() => _ = valve.WriteAsync(Cia408Objects.QSetpoint, 0x01, null!))).Should().ThrowExactly<ArgumentNullException>();
        ((Action)(() => _ = valve.ReadAsync(null!, 0x00))).Should().ThrowExactly<ArgumentNullException>();
        ((Action)(() => _ = valve.ObserveAsync(0x191, new byte[] { 1 }, null!))).Should().ThrowExactly<ArgumentNullException>();
    }

    // ---- interpreting and observing PDOs -------------------------------------------------------------------

    [Fact]
    public void Interpret_Names_Words_Records_And_What_It_Does_Not_Know()
    {
        using var tool = new LoneTool();
        var peer = tool.Peer(0x2200_0198);

        var values = peer.Interpret(new[]
        {
            Signal(0x6041, 0x00, 16, 0x0F, 0x80),
            Signal(0x6381, 0x01, 16, 0x30, 0xF8),
            Signal(0x6381, 0x02, 8, 0xA2),
            Signal(0x6381, 0x03, 8, 0xFD),
            Signal(0x2000, 0x01, 16, 0xFF, 0xFF),
            Signal(0x6280, 0x01, 8, 0x80),
        });

        values.Should().Equal(
            new Cia408PdoValue(0x6041, 0x00, Cia408Objects.DeviceStatusWord16, "Device status word 16-bit", 0x800F),
            new Cia408PdoValue(0x6381, 0x01, Cia408Objects.VprcActualValue, "vprc actual value", -2000),
            new Cia408PdoValue(0x6381, 0x02, Cia408Objects.VprcActualValue, "vprc actual value, unit", 0xA2),
            new Cia408PdoValue(0x6381, 0x03, Cia408Objects.VprcActualValue, "vprc actual value, prefix", -3),
            new Cia408PdoValue(0x2000, 0x01, null, "2000h:01h", 0xFFFF),
            new Cia408PdoValue(0x6280, 0x01, null, "6280h:01h", 0x80)); // reserved (§9.7.2)
    }

    [Fact]
    public void Interpret_Shifts_Back_By_The_Logical_Device_Of_The_Peer()
    {
        using var tool = new LoneTool();
        var peer = tool.Peer(0x2200_0198, logicalDevice: 1);

        var values = peer.Interpret(new[] { Signal(0x6841, 0x00, 16, 0x08, 0x00), Signal(0x6041, 0x00, 16, 0x08, 0x00) });

        values.Should().Equal(
            new Cia408PdoValue(0x6841, 0x00, Cia408Objects.DeviceStatusWord16, "Device status word 16-bit", 0x0008),
            new Cia408PdoValue(0x6041, 0x00, null, "6041h:00h", 0x0008));
    }

    [Fact]
    public void Interpret_Names_An_Unknown_Object_Of_The_Device_And_A_Variable_Sub_Index_Above_00h()
    {
        using var tool = new LoneTool();
        var peer = tool.Peer(0x2200_0198);

        var values = peer.Interpret(new[] { Signal(0x6001, 0x00, 8, 0x05), Signal(0x6041, 0x01, 8, 0x03) });

        var expected = new Cia408PdoValue(0x6041, 0x01, Cia408Objects.DeviceStatusWord16, "Device status word 16-bit, sub-index 01h", 3);
        values.Select(v => v.Description).Should().Equal("6001h:00h", "Device status word 16-bit, sub-index 01h");
        values[0].Index.Should().Be(0x6001);
        values[0].Definition.Should().BeNull("6001h is not in the catalogue");
        values[1].Definition.Should().BeSameAs(Cia408Objects.DeviceStatusWord16);
        values[1].Should().Be(expected);
        values[1].GetHashCode().Should().Be(expected.GetHashCode());
        values[1].ToString().Should().Contain("Device status word 16-bit");
    }

    [Fact]
    public void Interpret_Refuses_A_Signal_Of_Another_Node()
    {
        using var tool = new LoneTool();
        var peer = tool.Peer(0x2200_0198);
        var other = new ForeignPdoSignal(0x12, ForeignPdoKind.Tpdo, 1, 0x192, 0x6041, 0x00, new byte[] { 1, 0 },
            ForeignPdoMappingOrigin.LiveMapping, 16);

        ((Action)(() => peer.Interpret(new[] { other }))).Should().ThrowExactly<ArgumentException>();
        ((Action)(() => peer.Interpret(null!))).Should().ThrowExactly<ArgumentNullException>();
    }

    [Fact]
    public async Task The_Observed_Tpdo_1_And_Tpdo_2_Are_Named()
    {
        using var rig = new Cia408ToolRig();
        var peer = rig.Peer();
        await rig.Rig.StartAsync();
        rig.Rig.Device.SetActualValue(Cia408Objects.SpoolPositionActualValue, 100);
        rig.Rig.Device.SetActualValue(Cia408Objects.VprcActualValue, -200);
        await peer.EnableAsync().WithTimeoutAsync(HangGuard);
        await rig.Rig.DrainAsync();
        var tpdo1 = rig.Rig.Payloads(CanOpenProfileRig.Tpdo1).Last();
        var tpdo2 = rig.Rig.Payloads(CanOpenProfileRig.Tpdo2).Last();

        var first = await peer.ObserveAsync(CanOpenProfileRig.Tpdo1, tpdo1, rig.Description!).WithTimeoutAsync(HangGuard);
        var second = await peer.ObserveAsync(CanOpenProfileRig.Tpdo2, tpdo2, rig.Description!).WithTimeoutAsync(HangGuard);

        first.Result.Decoded.Should().BeTrue();
        first.Values.Should().Equal(
            new Cia408PdoValue(0x6041, 0x00, Cia408Objects.DeviceStatusWord16, "Device status word 16-bit", 0x000F));
        second.Result.Observations.Should().ContainSingle()
            .Which.Origin.Should().Be(ForeignPdoMappingOrigin.LiveMapping, "the device's mapping was read, not the file's");
        second.Values.Should().Equal(
            new Cia408PdoValue(0x6041, 0x00, Cia408Objects.DeviceStatusWord16, "Device status word 16-bit", 0x000F),
            new Cia408PdoValue(0x6301, 0x01, Cia408Objects.SpoolPositionActualValue, "Spool position actual value", 100),
            new Cia408PdoValue(0x6381, 0x01, Cia408Objects.VprcActualValue, "vprc actual value", -200));
    }

    // ---- logical device 1 ----------------------------------------------------------------------------------

    [Fact]
    public async Task Logical_Device_1_Is_Driven_At_6840h_And_Read_At_6841h()
    {
        var ld1 = new Cia408Configuration
        {
            Resolution = Cia408Resolution.Valve16,
            ControlModes = new[] { Cia408ControlMode.SpoolPositionClosedLoop },
            LogicalDevice = 1,
        };
        var devices = new[] { new Cia408Device(Cia408ToolRig.FixtureConfiguration), new Cia408Device(ld1) };
        using var rig = new Cia408ToolRig(devices, TwoLogicalDevices());

        var found = await CanOpenLogicalDevices.ReadAsync(rig.Tool, DeviceId).WithTimeoutAsync(HangGuard);
        var peers = new CanOpenProfileCatalog(new Cia408Profile()).CreatePeers(rig.Tool, DeviceId, found);

        peers.Should().HaveCount(2);
        var second = peers[1].Should().BeOfType<Cia408Peer>().Which;
        second.LogicalDevice.Index.Should().Be(1);
        second.SupportedControlModes.Should().Equal(Cia408ControlMode.SpoolPositionOpenLoop, Cia408ControlMode.SpoolPositionClosedLoop);
        await second.EnableAsync().WithTimeoutAsync(HangGuard);

        rig.Rig.Devices[1].State.Should().Be(Cia408State.Active);
        rig.Rig.Devices[0].State.Should().Be(Cia408State.Init, "logical device 0 is not touched");
        var requests = await RequestsAsync(rig);
        Downloads(requests, 0x6840).Should().Equal(0x0001u, 0x0003u, 0x0007u);
        Downloads(requests, ControlWord16).Should().BeEmpty();
        rig.Rig.Od.ReadUnsigned(0x6841, 0x00).Should().Be(0x000Fu, "6041h of logical device 1 is 6841h (CiA 301 v4.2.0 §7.4.1)");
    }

    [Fact]
    public async Task A_32_Bit_Valve_Is_Driven_At_6044h_And_Read_At_6045h()
    {
        // dtprs 10b (§8.6.2, Table 53): the valve uses the 32-bit pair; 604Fh defaults to 0 for a valve.
        var wide = new Cia408Configuration
        {
            Resolution = Cia408Resolution.Valve32Drive32,
            ControlModes = new[] { Cia408ControlMode.SpoolPositionClosedLoop },
        };
        using var rig = new Cia408ToolRig(new[] { new Cia408Device(wide) }, WideWords());
        var peer = rig.Peer(configuration: wide);
        peer.UsesWideWords.Should().BeTrue();

        await peer.EnableAsync().WithTimeoutAsync(HangGuard);

        rig.Rig.Device.State.Should().Be(Cia408State.Active);
        var requests = await RequestsAsync(rig);
        Downloads(requests, 0x6044).Should().Equal(0x0001u, 0x0003u, 0x0007u);
        Downloads(requests, ControlWord16).Should().BeEmpty();
        Uploads(requests, 0x6045).Should().BeGreaterThan(0);
        Uploads(requests, StatusWord16).Should().Be(0, "the status is read from 6045h");
        (await peer.ReadStatusAsync().WithTimeoutAsync(HangGuard)).Should().Be(new Cia408Status(0x0000_000F, Cia408State.Active));
    }

    [Fact]
    public async Task A_Device_Of_Resolution_11b_Is_Driven_On_The_Pair_Of_The_Mode_It_Runs()
    {
        // dtprs 11b with a position drive (mode 9, 32 bit) as the default and a spool valve mode
        // (mode 1, 16 bit on 11b): Table 53 NOTE 2 by Cia408Configuration.IsWide.
        var mixed = new Cia408Configuration
        {
            Resolution = Cia408Resolution.Valve16Drive32,
            ControlModes = new[] { Cia408ControlMode.PositionControlAxis, Cia408ControlMode.SpoolPositionOpenLoop },
        };
        using var rig = new Cia408ToolRig(new[] { new Cia408Device(mixed) }, BothPairs(mixed.DeviceType.Value));
        var peer = rig.Peer(configuration: mixed);
        peer.UsesWideWords.Should().BeNull();
        await peer.WriteLocalAsync(false).WithTimeoutAsync(HangGuard); // a drive powers on in local control (§9.3.9)

        await peer.DisableAsync().WithTimeoutAsync(HangGuard);
        var running9 = await RequestsAsync(rig);
        Downloads(running9, 0x6044).Should().Equal(new uint[] { 0x0001 }, "6043h holds 9, a drive mode: the 32-bit pair");
        Downloads(running9, ControlWord16).Should().BeEmpty();

        await peer.WriteControlModeAsync(Cia408ControlMode.SpoolPositionOpenLoop).WithTimeoutAsync(HangGuard);
        await peer.EnableAsync().WithTimeoutAsync(HangGuard);

        var running1 = (await RequestsAsync(rig)).Skip(running9.Length).ToArray();
        Downloads(running1, ControlWord16).Should().Equal(new uint[] { 0x0003, 0x0007 }, "6043h holds 1, a 16-bit valve mode on 11b");
        Downloads(running1, 0x6044).Should().BeEmpty();
        Uploads(running1, 0x6041).Should().BeGreaterThan(0);
        rig.Rig.Device.State.Should().Be(Cia408State.Active);
    }

    [Fact]
    public async Task Without_6043h_A_Device_Of_Resolution_11b_Is_Driven_On_The_Pair_Of_Its_Lowest_Mode()
    {
        // 6043h is Optional (§9.3.5): a device without it runs its default control mode, which a device
        // built from a description takes as the lowest mode its type announces, here mode 1, a 16-bit
        // valve mode on 11b. The device of this test answers 6043h with 0602 0000h.
        var mixed = new Cia408Configuration
        {
            Resolution = Cia408Resolution.Valve16Drive32,
            ControlModes = new[] { Cia408ControlMode.SpoolPositionOpenLoop, Cia408ControlMode.PositionControlAxis },
        };
        using var rig = new Cia408ToolRig(new[] { new Cia408Device(mixed) }, BothPairs(mixed.DeviceType.Value));
        rig.Rig.Od.Remove(0x6043, 0x00);
        var peer = rig.Peer(configuration: mixed);
        peer.UsesWideWords.Should().BeNull();

        await peer.EnableAsync().WithTimeoutAsync(HangGuard);

        rig.Rig.Device.State.Should().Be(Cia408State.Active);
        var requests = await RequestsAsync(rig);
        Downloads(requests, ControlWord16).Should().Equal(0x0001u, 0x0003u, 0x0007u);
        Downloads(requests, 0x6044).Should().BeEmpty();
    }

    [Fact]
    public async Task The_Default_Pause_Runs_On_The_Clock()
    {
        // The options' own delay, with the shortest pause there is: the refused reset reads twice and
        // pauses once between, on Task.Delay. Nothing is asserted about time.
        using var rig = new Cia408ToolRig(new[] { new Cia408Device(Cia408ToolRig.FixtureConfiguration) }, Cia408ToolRig.FixtureDescription);
        var peer = rig.Peer(options: new Cia408PeerOptions { StatusReads = 2, StatusPause = TimeSpan.FromTicks(1) });
        rig.Rig.Device.ReportFault(0x2110, Cia408FaultState.FaultInit);
        rig.Rig.Settle();

        Func<Task> reset = () => peer.ResetFaultAsync().WithTimeoutAsync(HangGuard);

        (await reset.Should().ThrowExactlyAsync<Cia408CommandException>()).Which.Reason.Should().Be(Cia408CommandFailure.NotReached);
    }

    [Fact]
    public void The_Peer_Options_Are_Checked_By_The_Descriptor()
    {
        FluentActions.Invoking(() => new Cia408Profile(new Cia408PeerOptions { StatusReads = 0 }))
            .Should().Throw<ArgumentOutOfRangeException>().WithParameterName("StatusReads");
        FluentActions.Invoking(() => new Cia408Profile(new Cia408PeerOptions { StatusPause = TimeSpan.FromTicks(-1) }))
            .Should().Throw<ArgumentOutOfRangeException>().WithParameterName("StatusPause");
        FluentActions.Invoking(() => new Cia408Profile(new Cia408PeerOptions { Delay = null! }))
            .Should().Throw<ArgumentNullException>().WithParameterName("Delay");
        new Cia408Profile().PeerOptions.Should().BeSameAs(Cia408PeerOptions.Default);
    }

    /// <summary>A description with <c>1000h</c> = <paramref name="deviceType"/>, both word pairs,
    /// <c>6043h</c> and <c>604Fh</c>.</summary>
    private static CanOpenDeviceDescription BothPairs(uint deviceType)
    {
        var text = new StringBuilder();
        text.Append("[FileInfo]\nFileName=both.eds\nFileVersion=1\nFileRevision=0\nEDSVersion=4.0\n")
            .Append("Description=A CiA 408 device with both pairs\nCreationTime=10:00AM\nCreationDate=11-19-2026\nCreatedBy=CanKit.Pro tests\n")
            .Append("[DeviceInfo]\nVendorName=CanKit.Pro\nVendorNumber=0\nProductName=Both\nProductNumber=0\nRevisionNumber=0\n")
            .Append("OrderCode=BOTH\nBaudRate_500=1\nSimpleBootUpMaster=0\nSimpleBootUpSlave=1\nGranularity=8\n")
            .Append("DynamicChannelsSupported=0\nGroupMessaging=0\nNrOfRXPDO=0\nNrOfTXPDO=0\nLSS_Supported=0\n")
            .Append("[MandatoryObjects]\nSupportedObjects=1\n1=0x1000\n")
            .Append(string.Format(CultureInfo.InvariantCulture,
                "[1000]\nParameterName=Device type\nObjectType=0x7\nDataType=0x0007\nAccessType=ro\nDefaultValue=0x{0:X8}\nPDOMapping=0\n", deviceType))
            .Append("[OptionalObjects]\nSupportedObjects=6\n1=0x6040\n2=0x6041\n3=0x6043\n4=0x6044\n5=0x6045\n6=0x604F\n");
        void Variable(ushort index, string type, string access)
            => text.Append(string.Format(CultureInfo.InvariantCulture,
                "[{0:X4}]\nParameterName=V{0:X4}\nObjectType=0x7\nDataType={1}\nAccessType={2}\nDefaultValue=0\nPDOMapping=1\n",
                index, type, access));

        Variable(0x6040, "0x0006", "rw");
        Variable(0x6041, "0x0006", "ro");
        Variable(0x6043, "0x0002", "rw");
        Variable(0x6044, "0x0007", "rw");
        Variable(0x6045, "0x0007", "ro");
        Variable(0x604F, "0x0002", "rw");
        return CanOpenDeviceDescription.ParseEds(text.ToString());
    }

    /// <summary>The description of the 32-bit valve of <see cref="A_32_Bit_Valve_Is_Driven_At_6044h_And_Read_At_6045h"/>:
    /// <c>1000h</c> and the 32-bit control and status words only, so a read of the 16-bit pair would be
    /// refused by the gate.</summary>
    private static CanOpenDeviceDescription WideWords()
    {
        var text = new StringBuilder();
        text.Append("[FileInfo]\nFileName=wide.eds\nFileVersion=1\nFileRevision=0\nEDSVersion=4.0\n")
            .Append("Description=A 32-bit CiA 408 valve\nCreationTime=10:00AM\nCreationDate=11-19-2026\nCreatedBy=CanKit.Pro tests\n")
            .Append("[DeviceInfo]\nVendorName=CanKit.Pro\nVendorNumber=0\nProductName=Wide\nProductNumber=0\nRevisionNumber=0\n")
            .Append("OrderCode=WIDE\nBaudRate_500=1\nSimpleBootUpMaster=0\nSimpleBootUpSlave=1\nGranularity=8\n")
            .Append("DynamicChannelsSupported=0\nGroupMessaging=0\nNrOfRXPDO=0\nNrOfTXPDO=0\nLSS_Supported=0\n")
            .Append("[MandatoryObjects]\nSupportedObjects=1\n1=0x1000\n")
            .Append("[1000]\nParameterName=Device type\nObjectType=0x7\nDataType=0x0007\nAccessType=ro\nDefaultValue=0x40800198\nPDOMapping=0\n")
            .Append("[OptionalObjects]\nSupportedObjects=2\n1=0x6044\n2=0x6045\n")
            .Append("[6044]\nParameterName=Device control word 32-bit\nObjectType=0x7\nDataType=0x0007\nAccessType=rw\nDefaultValue=0\nPDOMapping=1\n")
            .Append("[6045]\nParameterName=Device status word 32-bit\nObjectType=0x7\nDataType=0x0007\nAccessType=ro\nDefaultValue=0\nPDOMapping=1\n");
        return CanOpenDeviceDescription.ParseEds(text.ToString());
    }

    /// <summary>The description of the two-device node of
    /// <see cref="Logical_Device_1_Is_Driven_At_6840h_And_Read_At_6841h"/>: <c>1000h</c>, the device
    /// types <c>67FFh</c> and <c>6FFFh</c>, and the words of logical device 1.</summary>
    private static CanOpenDeviceDescription TwoLogicalDevices()
    {
        var text = new StringBuilder();
        text.Append("[FileInfo]\nFileName=two.eds\nFileVersion=1\nFileRevision=0\nEDSVersion=4.0\n")
            .Append("Description=Two CiA 408 devices\nCreationTime=10:00AM\nCreationDate=11-19-2026\nCreatedBy=CanKit.Pro tests\n")
            .Append("[DeviceInfo]\nVendorName=CanKit.Pro\nVendorNumber=0\nProductName=Two\nProductNumber=0\nRevisionNumber=0\n")
            .Append("OrderCode=TWO\nBaudRate_500=1\nSimpleBootUpMaster=0\nSimpleBootUpSlave=1\nGranularity=8\n")
            .Append("DynamicChannelsSupported=0\nGroupMessaging=0\nNrOfRXPDO=0\nNrOfTXPDO=0\nLSS_Supported=0\n")
            .Append("[MandatoryObjects]\nSupportedObjects=1\n1=0x1000\n")
            .Append("[1000]\nParameterName=Device type\nObjectType=0x7\nDataType=0x0007\nAccessType=ro\nDefaultValue=0xFFFF0198\nPDOMapping=0\n")
            .Append("[OptionalObjects]\nSupportedObjects=4\n1=0x67FF\n2=0x6840\n3=0x6841\n4=0x6FFF\n");
        void Variable(ushort index, string type, string access)
            => text.Append(string.Format(CultureInfo.InvariantCulture,
                "[{0:X4}]\nParameterName=V{0:X4}\nObjectType=0x7\nDataType={1}\nAccessType={2}\nDefaultValue=0\nPDOMapping=0\n",
                index, type, access));

        Variable(0x67FF, "0x0007", "ro");
        Variable(0x6840, "0x0006", "rw");
        Variable(0x6841, "0x0006", "ro");
        Variable(0x6FFF, "0x0007", "ro");
        return CanOpenDeviceDescription.ParseEds(text.ToString());
    }

    // ---- the codec's range ----------------------------------------------------------------------------------

    [Fact]
    public void The_Value_Range_Of_A_Type_Is_Its_Width_And_Sign()
    {
        CanOpenValueCodec.IsInRange(OdDataType.Integer16, short.MaxValue).Should().BeTrue();
        CanOpenValueCodec.IsInRange(OdDataType.Integer16, short.MinValue).Should().BeTrue();
        CanOpenValueCodec.IsInRange(OdDataType.Integer16, short.MaxValue + 1).Should().BeFalse();
        CanOpenValueCodec.IsInRange(OdDataType.Integer16, short.MinValue - 1).Should().BeFalse();
        CanOpenValueCodec.IsInRange(OdDataType.Unsigned8, 255).Should().BeTrue();
        CanOpenValueCodec.IsInRange(OdDataType.Unsigned8, 256).Should().BeFalse();
        CanOpenValueCodec.IsInRange(OdDataType.Unsigned8, -1).Should().BeFalse();
        CanOpenValueCodec.IsInRange(OdDataType.Unsigned32, uint.MaxValue).Should().BeTrue();
        CanOpenValueCodec.IsInRange(OdDataType.Integer64, long.MinValue).Should().BeTrue();
        CanOpenValueCodec.IsInRange(OdDataType.Unsigned64, long.MaxValue).Should().BeTrue();
        CanOpenValueCodec.IsInRange(OdDataType.Unsigned64, -1).Should().BeFalse();
    }

    // ---- the fixture -----------------------------------------------------------------------------------------

    [Fact]
    public void The_Fixture_Describes_What_The_Device_Declares()
    {
        using var rig = new Cia408ToolRig();
        var description = rig.Description!;
        var od = rig.Rig.Od;

        description.Objects.Objects[0x1000].DefaultValue.Should()
            .Be("0x" + Cia408ToolRig.FixtureConfiguration.DeviceType.Value.ToString("X8", CultureInfo.InvariantCulture));

        int described = 0;
        foreach (var obj in description.Objects.Objects.Values.Where(o => o.Index is >= 0x6000 and <= 0x67FF))
        {
            Cia408Objects.TryGet(obj.Index, out var definition).Should().BeTrue($"{obj.Index:X4}h is a CiA 408 object");
            var subs = obj.SubObjects.Count == 0
                ? new[] { (Sub: (byte)0, Type: obj.DataType, Access: obj.AccessType, Default: obj.DefaultValue) }
                : obj.SubObjects.Values.Select(s => (Sub: s.SubIndex, Type: (ushort?)s.DataType, Access: s.AccessType, Default: s.DefaultValue))
                    .ToArray();
            foreach (var (sub, type, access, defaultValue) in subs)
            {
                // §8.7.1: 00h the count, 01h the value, 02h the unit (UNSIGNED8), 03h the prefix (INTEGER8).
                var expected = !definition.IsValueRecord || sub == 1 ? definition.ValueType
                    : sub == 3 ? OdDataType.Integer8 : OdDataType.Unsigned8;
                type.Should().Be((ushort)expected, $"the type of {obj.Index:X4}h:{sub:X2}h");
                od.TryGet(obj.Index, sub, out var entry).Should().BeTrue($"the device declares {obj.Index:X4}h:{sub:X2}h");
                entry.DataType.Should().Be(expected);
                entry.Access.Should().Be(OdAccessOf(access), $"the device's access of {obj.Index:X4}h:{sub:X2}h is the fixture's");
                CanOpenValueCodec.Decode(expected, od.ReadRaw(obj.Index, sub)).Should().Be(ParseSigned(defaultValue!),
                    $"the device's value of {obj.Index:X4}h:{sub:X2}h after opening is the fixture's default");
                described++;
            }
        }

        int declared = 0;
        foreach (var definition in Cia408Objects.All)
        {
            for (int sub = 0; sub <= 0xFF; sub++)
            {
                if (!od.TryGet(definition.Index, (byte)sub, out _)) continue;
                description.Contains(definition.Index, (byte)sub).Should().BeTrue($"the fixture describes {definition.Index:X4}h:{sub:X2}h");
                declared++;
            }
        }

        declared.Should().Be(described, "the fixture and the device have the same entries in 6000h to 67FFh");
        described.Should().BeGreaterThan(0);

        // 1000h, 1029h and the default PDO records hold, once the node has opened, what the fixture
        // says: the COB-IDs with their validity bit, the transmission types, the mapping entries.
        od.ReadUnsigned(0x1000, 0x00).Should().Be(ParseDescribed(description.Objects.Objects[0x1000].DefaultValue!));
        int records = 0;
        foreach (ushort index in new ushort[] { 0x1029, 0x1400, 0x1401, 0x1600, 0x1601, 0x1800, 0x1801, 0x1A00, 0x1A01 })
        {
            foreach (var sub in description.Objects.Objects[index].SubObjects.Values)
            {
                od.ReadUnsigned(index, sub.SubIndex).Should().Be(ParseDescribed(sub.DefaultValue!),
                    $"{index:X4}h:{sub.SubIndex:X2}h of the device is what the fixture describes");
                records++;
            }
        }

        records.Should().Be(3 + (2 * 5) + (2 * 6) + (4 * 9),
            "1029h of three entries, two RPDO communication records of five, two TPDO ones of six, four mapping records of nine");
    }

    /// <summary>The dictionary access an EDS access type stands for, each its own rather than every
    /// writable one taken for <c>rw</c>: <c>ro</c>, <c>wo</c>, <c>rw</c>; the fixture uses no other.</summary>
    private static OdAccess OdAccessOf(AccessType access) => access switch
    {
        AccessType.ReadOnly => OdAccess.ReadOnly,
        AccessType.WriteOnly => OdAccess.WriteOnly,
        AccessType.ReadWrite => OdAccess.ReadWrite,
        _ => throw new ArgumentOutOfRangeException(nameof(access), access, "The fixture uses ro, wo and rw only."),
    };

    /// <summary>A decimal (possibly negative) or <c>0x</c> hexadecimal default of the fixture.</summary>
    private static long ParseSigned(string text)
        => text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.Parse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : long.Parse(text, CultureInfo.InvariantCulture);

    /// <summary>A numeric value of the fixture: decimal or <c>0x</c> hexadecimal, optionally after
    /// <c>$NODEID+</c>, resolved for the device's node-id (CiA 306).</summary>
    private static uint ParseDescribed(string text)
    {
        uint offset = 0;
        if (text.StartsWith("$NODEID", StringComparison.OrdinalIgnoreCase))
        {
            offset = DeviceId;
            text = text.Substring("$NODEID".Length).TrimStart('+');
        }

        uint value = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.Parse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : uint.Parse(text, CultureInfo.InvariantCulture);
        return value + offset;
    }

    private static Task Command(Cia408Peer peer, Cia408State target) => target switch
    {
        Cia408State.Active => peer.EnableAsync(),
        Cia408State.Hold => peer.HoldAsync(),
        Cia408State.Disabled => peer.DisableAsync(),
        _ => throw new ArgumentOutOfRangeException(nameof(target)),
    };
}
