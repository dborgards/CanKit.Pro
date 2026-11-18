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
using CanKit.Pro.CANopen.Cia401;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.Tests.Infrastructure;
using EdsDcfNet.Models;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Cia401;

/// <summary>
/// The tool side of CiA 401: <see cref="Cia401Peer"/> reads and writes a foreign CiA 401 device over
/// the bus through the public node API only, and names what an observed PDO of it carried. Every
/// test runs a real tool node beside the device (<see cref="Cia401ToolRig"/>), so every access goes
/// through the tool's SDO client and its peer SDO gate. Indices, data types and abort codes are
/// written out from the norm here, not taken from <see cref="Cia401Objects"/>, and what the peer
/// wrote is checked on the device's side, not by reading it back through the peer alone.
/// </summary>
public class Cia401PeerTests
{
    private const byte DeviceId = Cia401Rig.DeviceId;
    private const uint ValueRangeExceeded = 0x06090030;
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

    private static Cia401Configuration Config(int di, int @do, int ai, int ao, int logicalDevice = 0) => new()
    {
        DigitalInputs = di,
        DigitalOutputs = @do,
        AnalogInputs = ai,
        AnalogOutputs = ao,
        LogicalDevice = logicalDevice,
    };

    private static byte[] U32(uint value)
        => new[] { (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24) };

    /// <summary>A tool node alone on a bus, for what needs a node but no device: making a peer and
    /// interpreting signals perform no bus access.</summary>
    private sealed class LoneTool : IDisposable
    {
        private readonly CanKit.Abstractions.API.Can.ICanBus _bus;

        internal LoneTool()
        {
            _bus = VirtualAdapterFixture.Open(VirtualAdapterFixture.NewSession("cia401-peer"), 0);
            Node = CanOpen.OpenNode(_bus, nodeId: 0x01);
        }

        internal ICanOpenNode Node { get; }

        internal Cia401Peer Peer(uint deviceType, int logicalDevice = 0)
            => (Cia401Peer)new Cia401Profile().CreatePeer(Node, DeviceId,
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

    // ---- the descriptor and the block properties ---------------------------------------------------

    [Theory]
    [InlineData(8, 0, 0, 0)]
    [InlineData(0, 8, 0, 0)]
    [InlineData(0, 0, 2, 0)]
    [InlineData(0, 0, 0, 2)]
    [InlineData(8, 8, 2, 2)]
    public void The_Blocks_Follow_Bits_16_To_19_Of_The_Device_Type(int di, int @do, int ai, int ao)
    {
        using var tool = new LoneTool();
        var type = Config(di, @do, ai, ao).DeviceType.Value;

        var peer = tool.Peer(type);

        (peer.DigitalInputs is not null).Should().Be(di > 0, "bit 16 is the digital inputs (CiA 401-1 §6.2.1)");
        (peer.DigitalOutputs is not null).Should().Be(@do > 0, "bit 17 is the digital outputs");
        (peer.AnalogInputs is not null).Should().Be(ai > 0, "bit 18 is the analog inputs");
        (peer.AnalogOutputs is not null).Should().Be(ao > 0, "bit 19 is the analog outputs");
    }

    [Fact]
    public void The_M_Bit_Changes_No_Block()
    {
        using var tool = new LoneTool();

        var peer = tool.Peer(0x00810191);

        peer.DigitalInputs.Should().NotBeNull();
        peer.DigitalOutputs.Should().BeNull();
        peer.AnalogInputs.Should().BeNull();
        peer.AnalogOutputs.Should().BeNull();
    }

    [Fact]
    public async Task A_Digital_Input_Device_Read_Through_The_Catalogue_Has_Three_Null_Blocks()
    {
        using var rig = new Cia401ToolRig(new[] { Config(8, 0, 0, 0) }, description: null);

        var devices = await CanOpenLogicalDevices.ReadAsync(rig.Tool, DeviceId).WithTimeoutAsync(HangGuard);
        var peers = new CanOpenProfileCatalog(new Cia401Profile()).CreatePeers(rig.Tool, DeviceId, devices);

        var peer = peers.Should().ContainSingle().Which.Should().BeOfType<Cia401Peer>().Which;
        peer.Profile.Should().BeOfType<Cia401Profile>();
        peer.PeerNodeId.Should().Be(DeviceId);
        peer.LogicalDevice.Index.Should().Be(0);
        peer.LogicalDevice.DeviceType.Value.Should().Be(0x00010191u);
        peer.DigitalInputs.Should().NotBeNull();
        peer.DigitalOutputs.Should().BeNull();
        peer.AnalogInputs.Should().BeNull();
        peer.AnalogOutputs.Should().BeNull();
    }

    [Fact]
    public void CreatePeer_Refuses_A_Logical_Device_Of_Another_Profile()
    {
        using var tool = new LoneTool();
        var profile = new Cia401Profile();

        Action other = () => profile.CreatePeer(tool.Node, DeviceId, new CanOpenLogicalDevice(0, new CanOpenDeviceType(0x00020192)));
        Action noNode = () => profile.CreatePeer(null!, DeviceId, new CanOpenLogicalDevice(0, new CanOpenDeviceType(0x00010191)));
        Action noDevice = () => profile.CreatePeer(tool.Node, DeviceId, null!);
        Action badId = () => profile.CreatePeer(tool.Node, 0, new CanOpenLogicalDevice(0, new CanOpenDeviceType(0x00010191)));

        other.Should().Throw<ArgumentException>().WithParameterName("logicalDevice");
        noNode.Should().Throw<ArgumentNullException>().WithParameterName("node");
        noDevice.Should().Throw<ArgumentNullException>().WithParameterName("logicalDevice");
        badId.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ---- round trips, checked on the device ----------------------------------------------------------

    [Fact]
    public async Task Digital_Inputs_Round_Trip()
    {
        using var rig = new Cia401ToolRig();
        var di = rig.Peer().DigitalInputs!;
        rig.Rig.Device.SetDigitalInputs(2, 0xA5);
        rig.Rig.Settle();

        (await di.ReadGroupCountAsync().WithTimeoutAsync(HangGuard)).Should().Be(2);
        await rig.Rig.WaitForCountAsync(Cia401Rig.SdoRequest, 1); // the master sees the tool's requests
        (await di.ReadAsync(2).WithTimeoutAsync(HangGuard)).Should().Be(0xA5);

        await di.WritePolarityAsync(1, 0x0F).WithTimeoutAsync(HangGuard);
        await di.WriteFilterEnableAsync(2, 0x33).WithTimeoutAsync(HangGuard);
        await di.WriteGlobalInterruptEnableAsync(false).WithTimeoutAsync(HangGuard);
        await di.WriteInterruptMaskAnyChangeAsync(2, 0x3C).WithTimeoutAsync(HangGuard);
        await di.WriteInterruptMaskLowToHighAsync(1, 0x81).WithTimeoutAsync(HangGuard);
        await di.WriteInterruptMaskHighToLowAsync(2, 0x42).WithTimeoutAsync(HangGuard);

        var od = rig.Rig.Od;
        od.ReadUnsigned(0x6002, 0x01).Should().Be(0x0Fu);
        od.ReadUnsigned(0x6003, 0x02).Should().Be(0x33u);
        od.ReadRaw(0x6005, 0x00).Should().Equal(0);
        od.ReadUnsigned(0x6006, 0x02).Should().Be(0x3Cu);
        od.ReadUnsigned(0x6007, 0x01).Should().Be(0x81u);
        od.ReadUnsigned(0x6008, 0x02).Should().Be(0x42u);
        (await di.ReadPolarityAsync(1).WithTimeoutAsync(HangGuard)).Should().Be(0x0F);
        (await di.ReadFilterEnableAsync(2).WithTimeoutAsync(HangGuard)).Should().Be(0x33);
        (await di.ReadGlobalInterruptEnableAsync().WithTimeoutAsync(HangGuard)).Should().BeFalse();
        (await di.ReadInterruptMaskAnyChangeAsync(2).WithTimeoutAsync(HangGuard)).Should().Be(0x3C);
        (await di.ReadInterruptMaskLowToHighAsync(1).WithTimeoutAsync(HangGuard)).Should().Be(0x81);
        (await di.ReadInterruptMaskHighToLowAsync(2).WithTimeoutAsync(HangGuard)).Should().Be(0x42);
    }

    [Fact]
    public async Task Digital_Outputs_Round_Trip()
    {
        using var rig = new Cia401ToolRig();
        var @do = rig.Peer().DigitalOutputs!;

        (await @do.ReadGroupCountAsync().WithTimeoutAsync(HangGuard)).Should().Be(2);
        await @do.WriteAsync(1, 0x81).WithTimeoutAsync(HangGuard);
        await @do.WritePolarityAsync(2, 0x0F).WithTimeoutAsync(HangGuard);
        await @do.WriteErrorModeAsync(1, 0x3C).WithTimeoutAsync(HangGuard);
        await @do.WriteErrorValueAsync(2, 0x5A).WithTimeoutAsync(HangGuard);
        await @do.WriteFilterMaskAsync(1, 0xF0).WithTimeoutAsync(HangGuard);
        rig.Rig.Settle();

        var od = rig.Rig.Od;
        od.ReadUnsigned(0x6200, 0x01).Should().Be(0x81u);
        od.ReadUnsigned(0x6202, 0x02).Should().Be(0x0Fu);
        od.ReadUnsigned(0x6206, 0x01).Should().Be(0x3Cu);
        od.ReadUnsigned(0x6207, 0x02).Should().Be(0x5Au);
        od.ReadUnsigned(0x6208, 0x01).Should().Be(0xF0u);
        rig.Rig.Device.ReadDigitalOutputs(1).Should().Be(0x81, "the filter mask was opened when the output was written");
        (await @do.ReadAsync(1).WithTimeoutAsync(HangGuard)).Should().Be(0x81);
        (await @do.ReadPolarityAsync(2).WithTimeoutAsync(HangGuard)).Should().Be(0x0F);
        (await @do.ReadErrorModeAsync(1).WithTimeoutAsync(HangGuard)).Should().Be(0x3C);
        (await @do.ReadErrorValueAsync(2).WithTimeoutAsync(HangGuard)).Should().Be(0x5A);
        (await @do.ReadFilterMaskAsync(1).WithTimeoutAsync(HangGuard)).Should().Be(0xF0);
    }

    [Fact]
    public async Task Analog_Inputs_Round_Trip()
    {
        using var rig = new Cia401ToolRig();
        var ai = rig.Peer().AnalogInputs!;
        rig.Rig.Device.SetAnalogInput(2, -12345);
        rig.Rig.Settle();

        (await ai.ReadChannelCountAsync().WithTimeoutAsync(HangGuard)).Should().Be(2);
        (await ai.ReadAsync(2).WithTimeoutAsync(HangGuard)).Should().Be(-12345);

        await ai.WriteInterruptTriggerAsync(1, Cia401AnalogInterruptTrigger.UpperLimit | Cia401AnalogInterruptTrigger.Delta)
            .WithTimeoutAsync(HangGuard);
        await ai.WriteGlobalInterruptEnableAsync(true).WithTimeoutAsync(HangGuard);
        await ai.WriteInterruptUpperLimitAsync(1, 0x12345678).WithTimeoutAsync(HangGuard);
        await ai.WriteInterruptUpperLimitLeftAdjustedAsync(2, -1000).WithTimeoutAsync(HangGuard);
        await ai.WriteInterruptLowerLimitAsync(1, -0x01020304).WithTimeoutAsync(HangGuard);
        await ai.WriteInterruptLowerLimitLeftAdjustedAsync(2, 300).WithTimeoutAsync(HangGuard);
        await ai.WriteInterruptDeltaAsync(1, 0x01020304).WithTimeoutAsync(HangGuard);
        await ai.WriteInterruptNegativeDeltaAsync(2, 0xF1E2D3C4).WithTimeoutAsync(HangGuard);
        await ai.WriteInterruptPositiveDeltaAsync(1, 0x00ABCDEF).WithTimeoutAsync(HangGuard);

        var od = rig.Rig.Od;
        od.ReadUnsigned(0x6421, 0x01).Should().Be(0x05u, "upper limit is bit 0, delta bit 2 (CiA 401-1 §8.6.2)");
        od.ReadRaw(0x6423, 0x00).Should().Equal(1);
        od.ReadSigned(0x6424, 0x01).Should().Be(0x12345678);
        od.ReadSigned(0x6424, 0x02).Should().Be(-1000 * 65536, "a 16-bit limit is left adjusted in the INTEGER32");
        od.ReadSigned(0x6425, 0x01).Should().Be(-0x01020304);
        od.ReadSigned(0x6425, 0x02).Should().Be(300 * 65536);
        od.ReadUnsigned(0x6426, 0x01).Should().Be(0x01020304u);
        od.ReadUnsigned(0x6427, 0x02).Should().Be(0xF1E2D3C4u);
        od.ReadUnsigned(0x6428, 0x01).Should().Be(0x00ABCDEFu);

        (await ai.ReadInterruptTriggerAsync(1).WithTimeoutAsync(HangGuard))
            .Should().Be(Cia401AnalogInterruptTrigger.UpperLimit | Cia401AnalogInterruptTrigger.Delta);
        (await ai.ReadInterruptTriggerAsync(2).WithTimeoutAsync(HangGuard))
            .Should().Be(Cia401AnalogInterruptTrigger.UpperLimit | Cia401AnalogInterruptTrigger.LowerLimit
                | Cia401AnalogInterruptTrigger.Delta, "07h is the default (CiA 401-1 §8.6.2)");
        (await ai.ReadGlobalInterruptEnableAsync().WithTimeoutAsync(HangGuard)).Should().BeTrue();
        (await ai.ReadInterruptUpperLimitAsync(1).WithTimeoutAsync(HangGuard)).Should().Be(0x12345678);
        (await ai.ReadInterruptUpperLimitAsync(2).WithTimeoutAsync(HangGuard)).Should().Be(-1000 * 65536);
        (await ai.ReadInterruptLowerLimitAsync(1).WithTimeoutAsync(HangGuard)).Should().Be(-0x01020304);
        (await ai.ReadInterruptDeltaAsync(1).WithTimeoutAsync(HangGuard)).Should().Be(0x01020304u);
        (await ai.ReadInterruptNegativeDeltaAsync(2).WithTimeoutAsync(HangGuard)).Should().Be(0xF1E2D3C4u);
        (await ai.ReadInterruptPositiveDeltaAsync(1).WithTimeoutAsync(HangGuard)).Should().Be(0x00ABCDEFu);
        (await ai.ReadInterruptSourceAsync(1).WithTimeoutAsync(HangGuard)).Should().Be(rig.Rig.Od.ReadUnsigned(0x6422, 0x01));
    }

    [Fact]
    public async Task Analog_Outputs_Round_Trip()
    {
        using var rig = new Cia401ToolRig();
        var ao = rig.Peer().AnalogOutputs!;

        (await ao.ReadChannelCountAsync().WithTimeoutAsync(HangGuard)).Should().Be(2);
        (await ao.ReadErrorModeAsync(1).WithTimeoutAsync(HangGuard)).Should().BeTrue("01h is the default (CiA 401-1 §8.7.4)");
        await ao.WriteAsync(1, -300).WithTimeoutAsync(HangGuard);
        await ao.WriteErrorModeAsync(2, false).WithTimeoutAsync(HangGuard);
        await ao.WriteErrorValueAsync(1, 0x7F00_1234).WithTimeoutAsync(HangGuard);
        await ao.WriteErrorValueLeftAdjustedAsync(2, -2).WithTimeoutAsync(HangGuard);
        rig.Rig.Settle();

        var od = rig.Rig.Od;
        od.ReadSigned(0x6411, 0x01).Should().Be(-300);
        od.ReadUnsigned(0x6443, 0x02).Should().Be(0u);
        od.ReadSigned(0x6444, 0x01).Should().Be(0x7F00_1234);
        od.ReadSigned(0x6444, 0x02).Should().Be(-2 * 65536);
        rig.Rig.Device.ReadAnalogOutput(1).Should().Be(-300);
        (await ao.ReadAsync(1).WithTimeoutAsync(HangGuard)).Should().Be(-300);
        (await ao.ReadErrorModeAsync(2).WithTimeoutAsync(HangGuard)).Should().BeFalse();
        (await ao.ReadErrorValueAsync(1).WithTimeoutAsync(HangGuard)).Should().Be(0x7F00_1234);
        (await ao.ReadErrorValueAsync(2).WithTimeoutAsync(HangGuard)).Should().Be(-2 * 65536);
    }

    [Fact]
    public async Task ReadAsync_And_WriteAsync_Reach_A_Catalogue_Object_Raw()
    {
        using var rig = new Cia401ToolRig();
        var peer = rig.Peer();
        rig.Rig.Device.SetAnalogInput(1, 0x1234);
        rig.Rig.Settle();

        (await peer.ReadAsync(Cia401Objects.ReadAnalogInput16, 1).WithTimeoutAsync(HangGuard)).Should().Equal(0x34, 0x12);
        await peer.WriteAsync(Cia401Objects.WriteAnalogOutput16, 2, new byte[] { 0xCD, 0xAB }).WithTimeoutAsync(HangGuard);

        rig.Rig.Od.ReadRaw(0x6411, 0x02).Should().Equal(0xCD, 0xAB);
    }

    // ---- what is refused, and by whom ----------------------------------------------------------------

    [Fact]
    public async Task A_Write_The_Device_Rejects_Surfaces_As_The_Sdo_Abort()
    {
        using var rig = new Cia401ToolRig();
        var ai = rig.Peer().AnalogInputs!;

        Func<Task> reserved = () => ai.WriteInterruptTriggerAsync(1, (Cia401AnalogInterruptTrigger)0x20).WithTimeoutAsync(HangGuard);

        var abort = (await reserved.Should().ThrowAsync<SdoAbortException>()).Which;
        abort.AbortCode.Should().Be(ValueRangeExceeded, "bits 5 to 7 are reserved (CiA 401-1 §8.6.2)");
        abort.Index.Should().Be(0x6421);
        abort.Subindex.Should().Be(0x01);
        abort.Origin.Should().Be(SdoAbortOrigin.Peer);
        rig.Rig.Od.ReadUnsigned(0x6421, 0x01).Should().Be(0x07u);
    }

    [Fact]
    public async Task A_Peer_Without_A_Bound_Description_Is_Refused_By_The_Gate()
    {
        using var rig = new Cia401ToolRig(bindDescription: false);
        var peer = rig.Peer();

        Func<Task> read = () => peer.DigitalInputs!.ReadAsync(1).WithTimeoutAsync(HangGuard);
        Func<Task> write = () => peer.DigitalOutputs!.WriteAsync(1, 0xFF).WithTimeoutAsync(HangGuard);
        Func<Task> raw = () => peer.ReadAsync(Cia401Objects.ReadAnalogInput16, 1).WithTimeoutAsync(HangGuard);

        var refused = (await read.Should().ThrowAsync<PeerSdoAccessException>()).Which;
        refused.PeerDescriptionLoaded.Should().BeFalse();
        refused.Index.Should().Be(0x6000);
        (await write.Should().ThrowAsync<PeerSdoAccessException>()).Which.Index.Should().Be(0x6200);
        (await raw.Should().ThrowAsync<PeerSdoAccessException>()).Which.Index.Should().Be(0x6401);
        rig.Rig.Count(Cia401Rig.SdoRequest).Should().Be(0, "the gate refuses before a frame is sent");
        rig.Rig.Od.ReadUnsigned(0x6200, 0x01).Should().Be(0u);
    }

    [Fact]
    public async Task WriteAsync_Refuses_What_The_Catalogue_Marks_Read_Only_Before_The_Bus()
    {
        using var rig = new Cia401ToolRig();
        var peer = rig.Peer();

        Func<Task> input = () => peer.WriteAsync(Cia401Objects.ReadDigitalInput8, 1, new byte[] { 0xFF });
        Func<Task> source = () => peer.WriteAsync(Cia401Objects.AnalogInputInterruptSource, 1, new byte[4]);
        Func<Task> highest = () => peer.WriteAsync(Cia401Objects.PolarityDigitalInput8, 0, new byte[] { 1 });

        await input.Should().ThrowAsync<InvalidOperationException>();
        await source.Should().ThrowAsync<InvalidOperationException>();
        await highest.Should().ThrowAsync<InvalidOperationException>("sub-index 0 of an array is const (CiA 301 §7.4.5)");
        rig.Rig.Count(Cia401Rig.SdoRequest).Should().Be(0);
    }

    [Fact]
    public async Task Group_And_Channel_Numbers_Are_Checked_Before_The_Bus()
    {
        using var rig = new Cia401ToolRig();
        var peer = rig.Peer();

        Func<Task> zero = () => peer.DigitalInputs!.ReadAsync(0);
        Func<Task> high = () => peer.DigitalOutputs!.WriteAsync(255, 1);
        Func<Task> channel = () => peer.AnalogOutputs!.WriteAsync(0, 1);
        Func<Task> bank = () => peer.AnalogInputs!.ReadInterruptSourceAsync(9);
        Func<Task> nullDefinition = () => peer.ReadAsync(null!, 1);
        Func<Task> nullValue = () => peer.WriteAsync(Cia401Objects.WriteAnalogOutput16, 1, null!);

        await zero.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await high.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await channel.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await bank.Should().ThrowAsync<ArgumentOutOfRangeException>("an interrupt source has at most eight banks (CiA 401-1 §8.6.3)");
        await nullDefinition.Should().ThrowAsync<ArgumentNullException>();
        await nullValue.Should().ThrowAsync<ArgumentNullException>();
        rig.Rig.Count(Cia401Rig.SdoRequest).Should().Be(0);
    }

    [Fact]
    public void Argument_Errors_Are_Thrown_Before_A_Task_Exists()
    {
        using var tool = new LoneTool();
        var peer = tool.Peer(0x000F0191);
        var outOfRange = new (string What, Action Call)[]
        {
            ("a digital input reader", () => _ = peer.DigitalInputs!.ReadPolarityAsync(0)),
            ("a digital input writer", () => _ = peer.DigitalInputs!.WriteInterruptMaskLowToHighAsync(255, 1)),
            ("a digital output reader", () => _ = peer.DigitalOutputs!.ReadErrorValueAsync(0)),
            ("a digital output writer", () => _ = peer.DigitalOutputs!.WriteAsync(255, 1)),
            ("an analog input reader", () => _ = peer.AnalogInputs!.ReadInterruptSourceAsync(9)),
            ("an analog input writer", () => _ = peer.AnalogInputs!.WriteInterruptDeltaAsync(0, 1)),
            ("an analog output reader", () => _ = peer.AnalogOutputs!.ReadErrorModeAsync(0)),
            ("an analog output writer", () => _ = peer.AnalogOutputs!.WriteErrorValueLeftAdjustedAsync(255, 1)),
        };

        foreach (var (what, call) in outOfRange)
            call.Should().ThrowExactly<ArgumentOutOfRangeException>($"{what} checks its number before it returns a task");
        ((Action)(() => _ = peer.ReadAsync(null!, 1))).Should().ThrowExactly<ArgumentNullException>();
        ((Action)(() => _ = peer.WriteAsync(Cia401Objects.ReadDigitalInput8, 1, new byte[] { 1 })))
            .Should().ThrowExactly<InvalidOperationException>();
        ((Action)(() => _ = peer.ObserveAsync(0x191, new byte[] { 1 }, null!))).Should().ThrowExactly<ArgumentNullException>();
    }

    // ---- the codec -----------------------------------------------------------------------------------

    [Fact]
    public void A_Real_Is_Not_Encoded_Or_Decoded_As_An_Integer()
    {
        Action encode = () => Cia401Codec.Encode(OdDataType.Real32, 1);
        Action decode = () => Cia401Codec.Decode(OdDataType.Real32, new byte[4]);

        encode.Should().ThrowExactly<NotSupportedException>();
        decode.Should().ThrowExactly<NotSupportedException>();
    }

    [Fact]
    public void The_Codec_Is_Little_Endian_And_Signed_By_The_Data_Type()
    {
        Cia401Codec.Encode(OdDataType.Integer32, -0x01020304).Should().Equal(0xFC, 0xFC, 0xFD, 0xFE);
        Cia401Codec.Encode(OdDataType.Unsigned16, 0xBEEF).Should().Equal(0xEF, 0xBE);
        Cia401Codec.Decode(OdDataType.Integer16, new byte[] { 0x30, 0xF8 }).Should().Be(-2000);
        Cia401Codec.Decode(OdDataType.Unsigned16, new byte[] { 0x30, 0xF8 }).Should().Be(0xF830);
        Cia401Codec.Decode(OdDataType.Unsigned32, new byte[] { 0xC4, 0xD3, 0xE2, 0xF1 }).Should().Be(0xF1E2D3C4L);
    }

    [Fact]
    public void A_Value_Of_The_Wrong_Length_Is_Refused_Not_Cut()
    {
        Action longer = () => Cia401Codec.Decode(OdDataType.Unsigned8, new byte[] { 1, 0 });
        Action shorter = () => Cia401Codec.Decode(OdDataType.Integer32, new byte[] { 1, 0 });

        longer.Should().Throw<InvalidDataException>();
        shorter.Should().Throw<InvalidDataException>();
    }

    // ---- interpreting signals ------------------------------------------------------------------------

    [Fact]
    public void Interpret_Names_Catalogue_Entries_In_The_Order_Given_And_Signs_By_The_Data_Type()
    {
        using var tool = new LoneTool();
        var peer = tool.Peer(0x000F0191);

        var values = peer.Interpret(new[]
        {
            Signal(0x6401, 0x02, 16, 0x30, 0xF8),
            Signal(0x6000, 0x03, 8, 0x81),
            Signal(0x6401, 0x01, 16, 0x00, 0x80),
            Signal(0x6020, 0x01, 1, 0x01),
            Signal(0x6422, 0x01, 32, 0x01, 0x00, 0x00, 0x80),
        });

        values.Should().Equal(
            new Cia401PdoValue(0x6401, 0x02, Cia401Objects.ReadAnalogInput16, "Read analog input 16-bit, analog input 2", -2000),
            new Cia401PdoValue(0x6000, 0x03, Cia401Objects.ReadDigitalInput8, "Read digital input 8-bit, inputs 17–24", 0x81),
            new Cia401PdoValue(0x6401, 0x01, Cia401Objects.ReadAnalogInput16, "Read analog input 16-bit, analog input 1", -32768),
            new Cia401PdoValue(0x6020, 0x01, Cia401Objects.ReadDigitalInputBit[0], "Read digital input bit 1 to 128, input 1", 1),
            new Cia401PdoValue(0x6422, 0x01, Cia401Objects.AnalogInputInterruptSource, "Analog input interrupt source, bank 1", 0x80000001L));
    }

    [Fact]
    public void Interpret_Keeps_What_It_Does_Not_Know_As_Unsigned_By_The_Mapped_Length()
    {
        using var tool = new LoneTool();
        var peer = tool.Peer(0x000F0191);

        var values = peer.Interpret(new[]
        {
            Signal(0x2000, 0x01, 16, 0xFF, 0xFF),
            Signal(0x6420, 0x01, 8, 0x80),
            Signal(0x6800, 0x01, 8, 0x12),
            Signal(0x1001, 0x00, 4, 0x0A),
        });

        values.Should().Equal(
            new Cia401PdoValue(0x2000, 0x01, null, "2000h:01h", 0xFFFF),
            new Cia401PdoValue(0x6420, 0x01, null, "6420h:01h", 0x80),
            new Cia401PdoValue(0x6800, 0x01, null, "6800h:01h", 0x12),
            new Cia401PdoValue(0x1001, 0x00, null, "1001h:00h", 0x0A));
    }

    [Fact]
    public void Interpret_Shifts_Back_By_The_Logical_Device_Of_The_Peer()
    {
        using var tool = new LoneTool();
        var peer = tool.Peer(0x00010191, logicalDevice: 1);

        var values = peer.Interpret(new[]
        {
            Signal(0x6800, 0x02, 8, 0x5A),
            Signal(0x6000, 0x01, 8, 0x01),
            Signal(0x6C01, 0x01, 16, 0xFF, 0xFF),
        });

        values.Should().Equal(
            new Cia401PdoValue(0x6800, 0x02, Cia401Objects.ReadDigitalInput8, "Read digital input 8-bit, inputs 9–16", 0x5A),
            new Cia401PdoValue(0x6000, 0x01, null, "6000h:01h", 0x01),
            new Cia401PdoValue(0x6C01, 0x01, Cia401Objects.ReadAnalogInput16, "Read analog input 16-bit, analog input 1", -1));
    }

    [Fact]
    public void Interpret_Refuses_A_Signal_Of_Another_Node()
    {
        using var tool = new LoneTool();
        var peer = tool.Peer(0x00010191);
        var other = new ForeignPdoSignal(0x12, ForeignPdoKind.Tpdo, 1, 0x192, 0x6000, 0x01, new byte[] { 1 },
            ForeignPdoMappingOrigin.LiveMapping, 8);

        Action interpret = () => peer.Interpret(new[] { other });
        Action none = () => peer.Interpret(null!);

        interpret.Should().Throw<ArgumentException>();
        none.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Interpret_Returns_A_Mapped_Real_As_Its_Bit_Pattern()
    {
        using var tool = new LoneTool();
        var peer = tool.Peer(0x00050191);

        var values = peer.Interpret(new[] { Signal(0x6403, 0x01, 32, 0x00, 0x00, 0x80, 0x3F) });

        values.Should().Equal(new Cia401PdoValue(0x6403, 0x01, Cia401Objects.ReadAnalogInputFloat,
            "Read analog input float, analog input 1", 0x3F80_0000L));
    }

    // ---- observing the device's PDOs -----------------------------------------------------------------

    [Fact]
    public async Task An_Observed_Default_Tpdo_1_Names_Inputs_1_To_8()
    {
        using var rig = new Cia401ToolRig();
        var peer = rig.Peer();
        rig.Rig.Device.SetDigitalInputs(1, 0x3C);
        rig.Rig.Device.SetDigitalInputs(2, 0x81);
        await rig.Rig.StartAndTakeFirstTpdoAsync();
        var payload = rig.Rig.Payloads(Cia401Rig.Tpdo1)[0];

        var observed = await peer.ObserveAsync(Cia401Rig.Tpdo1, payload, rig.Description!).WithTimeoutAsync(HangGuard);

        observed.Result.Decoded.Should().BeTrue();
        observed.Result.Observations.Should().ContainSingle()
            .Which.Origin.Should().Be(ForeignPdoMappingOrigin.LiveMapping, "the device's mapping was read, not the file's");
        observed.Values.Should().Equal(
            new Cia401PdoValue(0x6000, 0x01, Cia401Objects.ReadDigitalInput8, "Read digital input 8-bit, inputs 1–8", 0x3C),
            new Cia401PdoValue(0x6000, 0x02, Cia401Objects.ReadDigitalInput8, "Read digital input 8-bit, inputs 9–16", 0x81));
    }

    [Fact]
    public async Task A_Remapped_Peer_Is_Read_By_Its_Real_Mapping()
    {
        using var rig = new Cia401ToolRig(Cia401Rig.WritablePdoRecords);
        var peer = rig.Peer();
        var tool = rig.Tool;

        // CiA 301 v4.2.0 §7.5.2.38: disable the PDO, clear the mapping, write the entries, set the
        // count, enable the PDO again.
        var cobId = (await tool.SdoUploadAsync(DeviceId, 0x1800, 0x01).WithTimeoutAsync(HangGuard));
        uint word = (uint)(cobId[0] | (cobId[1] << 8) | (cobId[2] << 16) | (cobId[3] << 24));
        await tool.SdoDownloadAsync(DeviceId, 0x1800, 0x01, U32(word | 0x8000_0000)).WithTimeoutAsync(HangGuard);
        await tool.SdoDownloadAsync(DeviceId, 0x1A00, 0x00, new byte[] { 0 }).WithTimeoutAsync(HangGuard);
        await tool.SdoDownloadAsync(DeviceId, 0x1A00, 0x01, U32(0x6000_0208)).WithTimeoutAsync(HangGuard);
        await tool.SdoDownloadAsync(DeviceId, 0x1A00, 0x02, U32(0x6000_0108)).WithTimeoutAsync(HangGuard);
        await tool.SdoDownloadAsync(DeviceId, 0x1A00, 0x00, new byte[] { 2 }).WithTimeoutAsync(HangGuard);
        await tool.SdoDownloadAsync(DeviceId, 0x1800, 0x01, U32(word)).WithTimeoutAsync(HangGuard);
        rig.Rig.Device.SetDigitalInputs(1, 0x3C);
        rig.Rig.Device.SetDigitalInputs(2, 0x81);
        await rig.Rig.StartAndTakeFirstTpdoAsync();
        var payload = rig.Rig.Payloads(Cia401Rig.Tpdo1)[0];
        payload.Should().Equal(new byte[] { 0x81, 0x3C }, "the device sends by its new mapping");

        var observed = await peer.ObserveAsync(Cia401Rig.Tpdo1, payload, rig.Description!).WithTimeoutAsync(HangGuard);

        observed.Result.Observations.Should().ContainSingle()
            .Which.Origin.Should().Be(ForeignPdoMappingOrigin.LiveMapping);
        observed.Values.Should().Equal(
            new Cia401PdoValue(0x6000, 0x02, Cia401Objects.ReadDigitalInput8, "Read digital input 8-bit, inputs 9–16", 0x81),
            new Cia401PdoValue(0x6000, 0x01, Cia401Objects.ReadDigitalInput8, "Read digital input 8-bit, inputs 1–8", 0x3C));
    }

    [Fact]
    public async Task An_Observation_That_Was_Not_Decoded_Has_No_Values_And_Says_Why()
    {
        using var rig = new Cia401ToolRig();
        var peer = rig.Peer();

        var observed = await peer.ObserveAsync(0x3F0, new byte[] { 1, 2 }, rig.Description!).WithTimeoutAsync(HangGuard);

        observed.Result.Decoded.Should().BeFalse();
        observed.Result.Reason.Should().NotBeNullOrEmpty();
        observed.Values.Should().BeEmpty();
    }

    // ---- logical device 1 ----------------------------------------------------------------------------

    [Fact]
    public async Task Logical_Device_1_Reads_6800h()
    {
        var ld1 = Config(16, 8, 0, 0, logicalDevice: 1);
        using var rig = new Cia401ToolRig(new[] { Config(8, 0, 0, 0), ld1 }, TwoLogicalDevices());
        rig.Rig.Devices[0].SetDigitalInputs(1, 0x11);
        rig.Rig.Devices[1].SetDigitalInputs(2, 0x5A);
        rig.Rig.Settle();

        var devices = await CanOpenLogicalDevices.ReadAsync(rig.Tool, DeviceId).WithTimeoutAsync(HangGuard);
        var peers = new CanOpenProfileCatalog(new Cia401Profile()).CreatePeers(rig.Tool, DeviceId, devices);

        peers.Should().HaveCount(2);
        var first = peers[0].Should().BeOfType<Cia401Peer>().Which;
        var second = peers[1].Should().BeOfType<Cia401Peer>().Which;
        second.LogicalDevice.Index.Should().Be(1);
        second.DigitalOutputs.Should().NotBeNull();
        second.AnalogInputs.Should().BeNull();
        first.DigitalOutputs.Should().BeNull();
        (await second.DigitalInputs!.ReadGroupCountAsync().WithTimeoutAsync(HangGuard)).Should().Be(2);
        (await second.DigitalInputs!.ReadAsync(2).WithTimeoutAsync(HangGuard)).Should().Be(0x5A);
        (await first.DigitalInputs!.ReadAsync(1).WithTimeoutAsync(HangGuard)).Should().Be(0x11);
        await second.DigitalOutputs!.WriteAsync(1, 0x0F).WithTimeoutAsync(HangGuard);
        rig.Rig.Settle();
        rig.Rig.Od.ReadUnsigned(0x6A00, 0x01).Should().Be(0x0Fu, "6200h of logical device 1 is 6A00h (CiA 301 v4.2.0 §7.4.1)");
    }

    /// <summary>The description of the two-device node of <see cref="Logical_Device_1_Reads_6800h"/>:
    /// <c>1000h</c>, the device types <c>67FFh</c> and <c>6FFFh</c>, the inputs of logical device 0
    /// and the inputs and outputs of logical device 1.</summary>
    private static CanOpenDeviceDescription TwoLogicalDevices()
    {
        var text = new StringBuilder();
        text.Append("[FileInfo]\nFileName=two.eds\nFileVersion=1\nFileRevision=0\nEDSVersion=4.0\n")
            .Append("Description=Two CiA 401 devices\nCreationTime=10:00AM\nCreationDate=11-18-2026\nCreatedBy=CanKit.Pro tests\n")
            .Append("[DeviceInfo]\nVendorName=CanKit.Pro\nVendorNumber=0\nProductName=Two\nProductNumber=0\nRevisionNumber=0\n")
            .Append("OrderCode=TWO\nBaudRate_500=1\nSimpleBootUpMaster=0\nSimpleBootUpSlave=1\nGranularity=8\n")
            .Append("DynamicChannelsSupported=0\nGroupMessaging=0\nNrOfRXPDO=0\nNrOfTXPDO=0\nLSS_Supported=0\n")
            .Append("[MandatoryObjects]\nSupportedObjects=1\n1=0x1000\n")
            .Append("[1000]\nParameterName=Device type\nObjectType=0x7\nDataType=0x0007\nAccessType=ro\nDefaultValue=0xFFFF0191\nPDOMapping=0\n")
            .Append("[OptionalObjects]\nSupportedObjects=6\n1=0x6000\n2=0x67FF\n3=0x6800\n4=0x6A00\n5=0x6FFF\n6=0x6805\n");
        void Variable(ushort index, string type, string access)
            => text.Append(string.Format(CultureInfo.InvariantCulture,
                "[{0:X4}]\nParameterName=V{0:X4}\nObjectType=0x7\nDataType={1}\nAccessType={2}\nDefaultValue=0\nPDOMapping=0\n",
                index, type, access));
        void Array(ushort index, int count, string access)
        {
            text.Append(string.Format(CultureInfo.InvariantCulture, "[{0:X4}]\nParameterName=A{0:X4}\nSubNumber={1}\nObjectType=0x8\n", index, count + 1));
            text.Append(string.Format(CultureInfo.InvariantCulture,
                "[{0:X4}sub0]\nParameterName=Highest sub-index supported\nObjectType=0x7\nDataType=0x0005\nAccessType=ro\nDefaultValue={1}\nPDOMapping=0\n",
                index, count));
            for (int s = 1; s <= count; s++)
            {
                text.Append(string.Format(CultureInfo.InvariantCulture,
                    "[{0:X4}sub{1:X}]\nParameterName=E{1}\nObjectType=0x7\nDataType=0x0005\nAccessType={2}\nDefaultValue=0\nPDOMapping=1\n",
                    index, s, access));
            }
        }

        Array(0x6000, 1, "ro");
        Variable(0x67FF, "0x0007", "ro");
        Array(0x6800, 2, "ro");
        Array(0x6A00, 1, "rw");
        Variable(0x6FFF, "0x0007", "ro");
        Variable(0x6805, "0x0001", "rw");
        return CanOpenDeviceDescription.ParseEds(text.ToString());
    }

    // ---- the fixture -----------------------------------------------------------------------------------

    [Fact]
    public void The_Fixture_Describes_What_The_Device_Declares()
    {
        using var rig = new Cia401ToolRig();
        var description = rig.Description!;
        var od = rig.Rig.Od;

        description.Objects.Objects[0x1000].DefaultValue.Should()
            .Be("0x" + Cia401ToolRig.FixtureConfiguration.DeviceType.Value.ToString("X8", CultureInfo.InvariantCulture));

        int described = 0;
        foreach (var obj in description.Objects.Objects.Values.Where(o => o.Index is >= 0x6000 and <= 0x67FF))
        {
            Cia401Objects.TryGet(obj.Index, out var definition).Should().BeTrue($"{obj.Index:X4}h is a CiA 401 object");
            var subs = obj.SubObjects.Count == 0
                ? new[] { (Sub: (byte)0, Type: (ushort?)obj.DataType, Access: obj.AccessType) }
                : obj.SubObjects.Values.Select(s => (Sub: s.SubIndex, Type: (ushort?)s.DataType, Access: s.AccessType)).ToArray();
            foreach (var (sub, type, access) in subs)
            {
                bool highest = definition.IsArray && sub == 0;
                type.Should().Be((ushort)(highest ? OdDataType.Unsigned8 : definition.DataType), $"the type of {obj.Index:X4}h:{sub:X2}h");
                access.Should().Be(highest || definition.Access == OdAccess.ReadOnly ? AccessType.ReadOnly : AccessType.ReadWrite,
                    $"the access of {obj.Index:X4}h:{sub:X2}h");
                od.TryGet(obj.Index, sub, out var entry).Should().BeTrue($"the device declares {obj.Index:X4}h:{sub:X2}h");
                entry.DataType.Should().Be(highest ? OdDataType.Unsigned8 : definition.DataType);
                described++;
            }
        }

        int declared = 0;
        foreach (var definition in Cia401Objects.All)
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
    }
}
