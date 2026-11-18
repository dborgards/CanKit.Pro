using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Application;
using CanKit.Pro.CANopen.Cia401;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Cia401;

/// <summary>
/// The one test per profile that the scope record asks for (<c>docs/reviews/2026-11-17-canopen-device-profiles-scope.md</c>,
/// step 4): a <see cref="Cia401Device"/> at one node and a <see cref="Cia401Peer"/> at another, both
/// built from <see cref="Cia401Objects"/>, on one virtual bus. A contradiction between the two sides
/// on what an object is, where it lives or what it carries fails here, which neither side's own tests
/// can show: each is pinned against the norm written out, not against the other.
/// </summary>
/// <remarks>
/// <para>The tool side is a real <see cref="CanOpenNode"/> (<see cref="Cia401ToolRig"/>), so every
/// access passes its SDO client and its peer gate. The test writes no index: every object is reached
/// through the typed methods of the peer's blocks, <see cref="Cia401Objects"/> members, or the rig's
/// helper that creates a TPDO, for which the peer has no call (it is not a profile object). The only
/// literals are node-ids and COB-IDs, and expected values.</para>
/// <para>The tool is handed the frame of an observed PDO the way a bus monitor would hand it: the
/// COB-ID and the payload the raw master recorded. Nothing is asserted against a wall clock: the
/// barriers and count waits of <see cref="Cia401Rig"/> stand in for time.</para>
/// </remarks>
public class Cia401EndToEndTests
{
    private const byte DeviceId = Cia401Rig.DeviceId;
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

    /// <summary>The tool reads the logical devices of the device over the bus and asks the catalogue
    /// for its peers, the way a tool that finds a node does.</summary>
    private static async Task<Cia401Peer> ConnectAsync(Cia401ToolRig rig)
    {
        var devices = await CanOpenLogicalDevices.ReadAsync(rig.Tool, DeviceId).WithTimeoutAsync(HangGuard);
        var peers = new CanOpenProfileCatalog(new Cia401Profile()).CreatePeers(rig.Tool, DeviceId, devices);
        return peers.Should().ContainSingle().Which.Should().BeOfType<Cia401Peer>().Which;
    }

    /// <summary>Records every <see cref="Cia401Device.OutputsChanged"/> of the device.</summary>
    private sealed class OutputsRecorder
    {
        private readonly object _gate = new();
        private readonly List<Cia401OutputsChangedEventArgs> _seen = new();

        internal OutputsRecorder(Cia401Device device)
        {
            device.OutputsChanged += (_, e) =>
            {
                lock (_gate) _seen.Add(e);
            };
        }

        internal IReadOnlyList<Cia401OutputsChangedEventArgs> Events
        {
            get { lock (_gate) return _seen.ToList(); }
        }
    }

    // ---- discovery -----------------------------------------------------------------------------------

    [Fact]
    public async Task The_Tool_Finds_Exactly_One_Cia401_Peer_With_All_Four_Blocks()
    {
        using var rig = new Cia401ToolRig();

        var peer = await ConnectAsync(rig);

        peer.Profile.Should().BeOfType<Cia401Profile>();
        peer.PeerNodeId.Should().Be(DeviceId);
        peer.LogicalDevice.Index.Should().Be(0);
        peer.LogicalDevice.DeviceType.Should().Be(Cia401ToolRig.FixtureConfiguration.DeviceType,
            "the device type the tool read is the one the device was configured to declare");
        peer.DigitalInputs.Should().NotBeNull();
        peer.DigitalOutputs.Should().NotBeNull();
        peer.AnalogInputs.Should().NotBeNull();
        peer.AnalogOutputs.Should().NotBeNull();
    }

    [Fact]
    public async Task A_Catalogue_Without_The_Profile_Yields_No_Peer_And_No_Error()
    {
        using var rig = new Cia401ToolRig();

        var devices = await CanOpenLogicalDevices.ReadAsync(rig.Tool, DeviceId).WithTimeoutAsync(HangGuard);
        var peers = new CanOpenProfileCatalog().CreatePeers(rig.Tool, DeviceId, devices);

        devices.Devices.Should().ContainSingle("the device is read the same way, whoever holds its profile");
        peers.Should().ContainSingle().Which.Should().BeNull("an unknown profile is not an error, it has no access");
        new CanOpenProfileCatalog(new Cia401Profile()).CreatePeers(rig.Tool, DeviceId, devices)
            .Should().ContainSingle().Which.Should().BeOfType<Cia401Peer>("the control: the same device, with the descriptor, has one");
    }

    // ---- tool to device: outputs ----------------------------------------------------------------------

    [Fact]
    public async Task Outputs_Written_Through_The_Peer_Reach_The_Application_Once_Each()
    {
        using var rig = new Cia401ToolRig();
        var recorder = new OutputsRecorder(rig.Rig.Device);
        var peer = await ConnectAsync(rig);
        // A polarity the written value is not mistaken for: the terminal is the written value XOR the
        // polarity, so a write that went to the polarity object instead would leave the same terminal
        // as a write of the value to an unmodified output.
        await peer.DigitalOutputs!.WritePolarityAsync(2, 0x0F).WithTimeoutAsync(HangGuard);
        rig.Rig.Settle();
        var polarityEvents = recorder.Events.Count;

        await peer.DigitalOutputs!.WriteAsync(2, 0x5A).WithTimeoutAsync(HangGuard);
        rig.Rig.Settle();
        var afterDigital = recorder.Events.Skip(polarityEvents).ToList();
        await peer.AnalogOutputs!.WriteAsync(2, -300).WithTimeoutAsync(HangGuard);
        rig.Rig.Settle();

        afterDigital.Should().ContainSingle().Which.Block.Should().Be(Cia401FunctionBlock.DigitalOutput);
        afterDigital[0].Group.Should().Be(2);
        afterDigital[0].Levels.Should().Equal(0x00, 0x55);
        var events = recorder.Events.Skip(polarityEvents).ToList();
        events.Should().HaveCount(2, "one change of the digital outputs and one of the analog ones, no more");
        events[1].Block.Should().Be(Cia401FunctionBlock.AnalogOutput);
        events[1].Channel.Should().Be(2);
        events[1].AnalogLevels.Should().Equal(0, -300);
        rig.Rig.Device.ReadDigitalOutputs(1).Should().Be(0x00);
        rig.Rig.Device.ReadDigitalOutputs(2).Should().Be(0x55, "0x5A with the polarity 0x0F the peer wrote");
        rig.Rig.Device.ReadAnalogOutput(1).Should().Be(0);
        rig.Rig.Device.ReadAnalogOutput(2).Should().Be(-300);
        (await peer.DigitalOutputs!.ReadAsync(2).WithTimeoutAsync(HangGuard)).Should().Be(0x5A, "the value written, not the terminal");
        (await peer.AnalogOutputs!.ReadAsync(2).WithTimeoutAsync(HangGuard)).Should().Be(-300);
    }

    // ---- device to tool: inputs, observed on the bus -----------------------------------------------------

    [Fact]
    public async Task A_Digital_Input_The_Application_Sets_Is_Named_In_The_Observed_Tpdo()
    {
        using var rig = new Cia401ToolRig();
        var peer = await ConnectAsync(rig);
        rig.Rig.Device.SetDigitalInputs(2, 0x81);
        await rig.Rig.StartAndTakeFirstTpdoAsync();

        rig.Rig.Device.SetDigitalInputs(1, 0x3C);
        await rig.Rig.WaitForCountAsync(Cia401Rig.Tpdo1, 2);
        var observed = await peer.ObserveAsync(Cia401Rig.Tpdo1, rig.Rig.Payloads(Cia401Rig.Tpdo1)[1], rig.Description!)
            .WithTimeoutAsync(HangGuard);

        observed.Result.Decoded.Should().BeTrue();
        observed.Values.Should().Equal(
            new Cia401PdoValue(Cia401Objects.ReadDigitalInput8.Index, 0x01, Cia401Objects.ReadDigitalInput8,
                "Read digital input 8-bit, inputs 1–8", 0x3C),
            new Cia401PdoValue(Cia401Objects.ReadDigitalInput8.Index, 0x02, Cia401Objects.ReadDigitalInput8,
                "Read digital input 8-bit, inputs 9–16", 0x81));
    }

    [Fact]
    public async Task An_Analog_Interrupt_The_Peer_Configures_Fires_The_Tpdo_It_Is_Observed_In()
    {
        using var rig = new Cia401ToolRig(Cia401Rig.WritablePdoRecords);
        var peer = await ConnectAsync(rig);
        await rig.Rig.EnableTpdoAsync(2); // a PDO is not a profile object: the peer has no call for it
        var ai = peer.AnalogInputs!;
        await ai.WriteInterruptTriggerAsync(1, Cia401AnalogInterruptTrigger.Delta).WithTimeoutAsync(HangGuard);
        await ai.WriteInterruptDeltaAsync(1, 50).WithTimeoutAsync(HangGuard);
        await ai.WriteGlobalInterruptEnableAsync(true).WithTimeoutAsync(HangGuard);
        rig.Rig.Device.SetAnalogInput(1, 1200);
        await rig.Rig.StartAndTakeFirstTpdoAsync(Cia401Rig.Tpdo2); // communicates 1200

        rig.Rig.Device.SetAnalogInput(1, 1230);
        await rig.Rig.DrainAsync();
        rig.Rig.Count(Cia401Rig.Tpdo2).Should().Be(1, "30 from the value communicated is not more than the delta the peer wrote");
        rig.Rig.Device.SetAnalogInput(1, 1300);
        await rig.Rig.WaitForCountAsync(Cia401Rig.Tpdo2, 2);
        await rig.Rig.DrainAsync();
        rig.Rig.Count(Cia401Rig.Tpdo2).Should().Be(2, "100 from the value communicated interrupts, once");
        var observed = await peer.ObserveAsync(Cia401Rig.Tpdo2, rig.Rig.Payloads(Cia401Rig.Tpdo2)[1], rig.Description!)
            .WithTimeoutAsync(HangGuard);

        observed.Result.Decoded.Should().BeTrue();
        observed.Values.Should().Equal(
            new Cia401PdoValue(Cia401Objects.ReadAnalogInput16.Index, 0x01, Cia401Objects.ReadAnalogInput16,
                "Read analog input 16-bit, analog input 1", 1300),
            new Cia401PdoValue(Cia401Objects.ReadAnalogInput16.Index, 0x02, Cia401Objects.ReadAnalogInput16,
                "Read analog input 16-bit, analog input 2", 0));
    }

    // ---- both sides agree on what is there -------------------------------------------------------------

    [Fact]
    public async Task What_The_Application_Sets_And_What_The_Device_Declares_Reads_Back_Through_The_Peer()
    {
        using var rig = new Cia401ToolRig();
        var peer = await ConnectAsync(rig);
        var configuration = Cia401ToolRig.FixtureConfiguration;
        rig.Rig.Device.SetDigitalInputs(1, 0xA5);
        rig.Rig.Device.SetDigitalInputs(2, 0x0F);
        rig.Rig.Device.SetAnalogInput(1, -12345);
        rig.Rig.Device.SetAnalogInput(2, 321);
        rig.Rig.Settle();

        // What the application set.
        (await peer.DigitalInputs!.ReadAsync(1).WithTimeoutAsync(HangGuard)).Should().Be(0xA5);
        (await peer.DigitalInputs!.ReadAsync(2).WithTimeoutAsync(HangGuard)).Should().Be(0x0F);
        (await peer.AnalogInputs!.ReadAsync(1).WithTimeoutAsync(HangGuard)).Should().Be(-12345);
        (await peer.AnalogInputs!.ReadAsync(2).WithTimeoutAsync(HangGuard)).Should().Be(321);

        // What the device declared and defaulted itself, as the configuration says.
        (await peer.DigitalInputs!.ReadGroupCountAsync().WithTimeoutAsync(HangGuard)).Should().Be(configuration.DigitalInputs / 8);
        (await peer.DigitalOutputs!.ReadGroupCountAsync().WithTimeoutAsync(HangGuard)).Should().Be(configuration.DigitalOutputs / 8);
        (await peer.AnalogInputs!.ReadChannelCountAsync().WithTimeoutAsync(HangGuard)).Should().Be(configuration.AnalogInputs);
        (await peer.AnalogOutputs!.ReadChannelCountAsync().WithTimeoutAsync(HangGuard)).Should().Be(configuration.AnalogOutputs);
        (await peer.DigitalInputs!.ReadGlobalInterruptEnableAsync().WithTimeoutAsync(HangGuard)).Should().BeTrue();
        (await peer.AnalogInputs!.ReadGlobalInterruptEnableAsync().WithTimeoutAsync(HangGuard)).Should().BeFalse(
            "6423h is FALSE by default, as the device builds it");
        (await peer.AnalogOutputs!.ReadErrorModeAsync(1).WithTimeoutAsync(HangGuard)).Should().BeTrue();

        // What the tool wrote, read back by the tool and found on the device.
        await peer.DigitalOutputs!.WriteAsync(1, 0x81).WithTimeoutAsync(HangGuard);
        await peer.AnalogOutputs!.WriteAsync(1, 1234).WithTimeoutAsync(HangGuard);
        (await peer.DigitalOutputs!.ReadAsync(1).WithTimeoutAsync(HangGuard)).Should().Be(0x81);
        (await peer.AnalogOutputs!.ReadAsync(1).WithTimeoutAsync(HangGuard)).Should().Be(1234);
        rig.Rig.Device.ReadDigitalOutputs(1).Should().Be(0x81);
        rig.Rig.Device.ReadAnalogOutput(1).Should().Be(1234);
        rig.Rig.Device.ReadDigitalInputs(1).Should().Be(0xA5);
        rig.Rig.Device.ReadAnalogInput(2).Should().Be(321);
    }
}
