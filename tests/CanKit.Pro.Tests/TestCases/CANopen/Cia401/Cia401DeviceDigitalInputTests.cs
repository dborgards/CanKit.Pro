using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Application;
using CanKit.Pro.CANopen.Cia401;
using CanKit.Pro.CANopen.Lss;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Cia401;

/// <summary>
/// The CiA 401 device attached to a node, with its digital inputs: what it declares, the device type
/// and default PDO it sets, and how an input the application sets reaches <c>6000h</c> and TPDO 1
/// (CiA 401-1 v3.1.0 §6.1, §7.2.2 Figure 4, §8.2.1 to §8.2.7). Indices, defaults and abort codes are
/// written out from the norm here, not taken from <see cref="Cia401Objects"/>.
/// </summary>
public class Cia401DeviceDigitalInputTests
{
    private const uint AttemptWriteReadOnly = 0x06010002;

    private static Cia401Configuration Inputs(int channels, int logicalDevice = 0)
        => new() { DigitalInputs = channels, LogicalDevice = logicalDevice };

    // ---- attach --------------------------------------------------------------------------------

    [Fact]
    public void Attach_Declares_The_Digital_Input_Objects_With_Their_Defaults()
    {
        using var rig = new Cia401Rig(Inputs(20));
        var od = rig.Od;

        // Twenty channels are three groups of eight (§7.2.1.3): sub-index 0 says 3 everywhere.
        foreach (ushort index in new ushort[] { 0x6000, 0x6002, 0x6003, 0x6006, 0x6007, 0x6008 })
        {
            od.ReadUnsigned(index, 0x00).Should().Be(3u, $"{index:X4}h has three groups");
            od.TryGet(index, 0x00, out var count).Should().BeTrue();
            count.Access.Should().Be(OdAccess.ReadOnly, "the highest sub-index supported is const (§8.2.1 to §8.2.7)");
            od.TryGet(index, 0x04, out _).Should().BeFalse($"{index:X4}h has no fourth group");
        }

        for (byte sub = 1; sub <= 3; sub++)
        {
            od.ReadUnsigned(0x6000, sub).Should().Be(0u);
            od.TryGet(0x6000, sub, out var input).Should().BeTrue();
            input.Access.Should().Be(OdAccess.ReadOnly, "6000h is ro (§8.2.1)");
            input.PdoMappable.Should().BeTrue("6000h maps into a TPDO (§8.2.1)");
            od.ReadUnsigned(0x6002, sub).Should().Be(0x00u, "polarity defaults to 00h (§8.2.2)");
            od.ReadUnsigned(0x6003, sub).Should().Be(0x00u, "the filter defaults to 00h (§8.2.3)");
            od.ReadUnsigned(0x6006, sub).Should().Be(0xFFu, "any change defaults to FFh (§8.2.5)");
            od.ReadUnsigned(0x6007, sub).Should().Be(0x00u, "low-to-high defaults to 00h (§8.2.6)");
            od.ReadUnsigned(0x6008, sub).Should().Be(0x00u, "high-to-low defaults to 00h (§8.2.7)");
        }

        od.TryGet(0x6005, 0x00, out var global).Should().BeTrue();
        global.DataType.Should().Be(OdDataType.Boolean, "6005h is a BOOLEAN variable (§8.2.4)");
        global.GetRawValue().Should().Equal(new byte[] { 1 }, "TRUE by default (§8.2.4)");
        global.PdoMappable.Should().BeFalse("6005h does not map (§8.2.4)");
    }

    [Fact]
    public void Attach_Sets_The_Device_Type_And_The_Default_Tpdo()
    {
        using var rig = new Cia401Rig(Inputs(20));

        rig.Od.ReadUnsigned(0x1000, 0x00).Should().Be(0x00010191u, "profile 401 with digital inputs, bit 16 (§6.2.1)");
        rig.Od.ReadUnsigned(0x1800, 0x01).Should().Be(Cia401Rig.Tpdo1, "TPDO 1 valid on its pre-defined COB-ID");
        rig.Od.ReadUnsigned(0x1800, 0x02).Should().Be(0xFFu, "event-driven, device-profile specific (§6.2.6)");
        rig.Od.ReadUnsigned(0x1A00, 0x00).Should().Be(3u, "one byte per started group (§6.2.6)");
        rig.Od.ReadUnsigned(0x1A00, 0x01).Should().Be(0x60000108u);
        rig.Od.ReadUnsigned(0x1A00, 0x02).Should().Be(0x60000208u);
        rig.Od.ReadUnsigned(0x1A00, 0x03).Should().Be(0x60000308u);
    }

    [Fact]
    public void The_Device_Claims_Its_Logical_Device_And_Asks_For_Its_Pdos_And_Failure_Monitoring()
    {
        var device0 = new Cia401Device(Inputs(8));
        var device1 = new Cia401Device(Inputs(8, logicalDevice: 1));

        device0.ClaimedRanges.Should().Equal(new CanOpenIndexRange(0x6000, 0x67FF));
        device1.ClaimedRanges.Should().Equal(new CanOpenIndexRange(0x6800, 0x6FFF));
        device0.Requirements.PdoCount.Should().Be(1);
        device1.Requirements.PdoCount.Should().Be(65, "TPDO 1 of logical device 1 is PDO 65");
        device0.Requirements.CommunicationFailureMonitoring.Should().BeTrue();
    }

    [Fact] // every function block of the first slice is built: nothing is refused
    public void A_Device_With_All_Four_Function_Blocks_Opens()
    {
        using var rig = new Cia401Rig(new Cia401Configuration
        {
            DigitalInputs = 8,
            DigitalOutputs = 8,
            AnalogInputs = 4,
            AnalogOutputs = 4,
        });
        var od = rig.Od;

        od.ReadUnsigned(0x1000, 0x00).Should().Be(0x000F0191u, "profile 401 with bits 16 to 19 (§6.2.1)");
        foreach (ushort index in new ushort[] { 0x6000, 0x6200, 0x6401, 0x6411 })
        {
            od.TryGet(index, 0x01, out _).Should().BeTrue($"{index:X4}h is declared");
        }

        od.ReadUnsigned(0x1400, 0x01).Should().Be(Cia401Rig.Rpdo1, "RPDO 1, digital outputs (§6.2.5)");
        od.ReadUnsigned(0x1401, 0x01).Should().Be(Cia401Rig.Rpdo2, "RPDO 2, analog outputs (§6.2.7)");
        od.ReadUnsigned(0x1800, 0x01).Should().Be(Cia401Rig.Tpdo1, "TPDO 1, digital inputs (§6.2.6)");
        od.ReadUnsigned(0x1801, 0x01).Should().Be(Cia401Rig.Tpdo2, "TPDO 2, analog inputs, valid (§6.2.8, Table 18)");
        rig.Device.ReadDigitalOutputs(1).Should().Be(0x00);
        rig.Device.ReadAnalogOutput(4).Should().Be(0);

        FluentActions.Invoking(() => new Cia401Device(null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new Cia401Device(new Cia401Configuration()))
            .Should().Throw<ArgumentException>("a device with no I/O function is no CiA 401 device (§6.2.1)");
    }

    [Fact]
    public void A_Device_Attaches_To_One_Node_Only()
    {
        using var rig = new Cia401Rig(Inputs(8));
        using var bus = VirtualAdapterFixture.Open(VirtualAdapterFixture.NewSession("cia401-second"), 1);
        using var service = new CanBusService(bus);

        FluentActions.Invoking(() => new CanOpenNode(service, 0x12, Cia401Device.AddTo(new CanOpenNodeOptions(), rig.Device), ownsService: false))
            .Should().Throw<InvalidOperationException>().WithMessage("*attached*");
    }

    // ---- AddTo -----------------------------------------------------------------------------------

    [Fact]
    public void AddTo_Raises_The_Pdo_Count_And_Turns_Error_Behaviour_On()
    {
        var device = new Cia401Device(Inputs(8, logicalDevice: 1));

        var options = Cia401Device.AddTo(new CanOpenNodeOptions(), device);

        options.PdoCount.Should().Be(65);
        options.EnableErrorBehavior.Should().BeTrue("the device monitors communication failures");
        options.Applications.Should().Equal(device);
    }

    [Fact]
    public void AddTo_And_Attach_Refuse_A_Null_Argument()
    {
        var device = new Cia401Device(Inputs(8));

        FluentActions.Invoking(() => Cia401Device.AddTo(null!, device))
            .Should().ThrowExactly<ArgumentNullException>().Which.ParamName.Should().Be("options");
        FluentActions.Invoking(() => Cia401Device.AddTo(new CanOpenNodeOptions(), null!))
            .Should().ThrowExactly<ArgumentNullException>().Which.ParamName.Should().Be("device");
        FluentActions.Invoking(() => device.Attach(null!))
            .Should().ThrowExactly<ArgumentNullException>().Which.ParamName.Should().Be("host");

        // The refused Attach took nothing: the device still attaches to a node.
        using var rig = new Cia401Rig(new[] { device }, description: null, options: null);
        rig.Device.ReadDigitalInputs(1).Should().Be(0x00);
    }

    /// <summary>Options in which every property but the PDO count, error behaviour and the
    /// applications differs from a fresh <see cref="CanOpenNodeOptions"/>, so that a copy that drops
    /// one of them back to its default shows.</summary>
    private static CanOpenNodeOptions EveryOptionChanged(int pdoCount) => new CanOpenNodeOptions
    {
        RequireBootDeviceTypeUpload = true,
        ActiveSlaveBootRetryInterval = TimeSpan.FromSeconds(1),
        RequireBootConfigurationMatch = true,
        EnableBootConfigurationDownload = true,
        EnableBootErrorControl = true,
        EnableNmtErrorHandler = true,
        EnableNmtBootupHandler = true,
        EnableBootKeepAlive = true,
        EnableEmcyInhibit = true,
        EnableNmtInhibit = true,
        EnablePeriodicFlyingMasterForce = true,
        EnableSelfStartingNodes = true,
        EmitGuardingErrorEmcy = true,
        EmitControllerErrorEmcy = true,
        EmitBusOffRecoveryEmcy = true,
        EmitControllerOverrunEmcy = true,
        SdoTimeout = TimeSpan.FromMilliseconds(777),
        SdoServerTimeout = TimeSpan.FromMilliseconds(4321),
        EnableChangeOfStateTpdo = false,
        DefaultTpdoEventTimerInterval = TimeSpan.FromMilliseconds(250),
        EventQueueCapacity = 33,
        MaxSdoTransferBytes = 4096,
        SdoBlockThresholdBytes = 64,
        SdoBlockSize = 50,
        SdoBlockCrcSupported = false,
        SdoBlockMaxRetransmissions = 7,
        RespondToNodeGuardingRtr = false,
        WritableCommunicationParameters = true,
        Profile = CanOpenNodeProfile.Tool,
        SrdoCount = 2,
        EnableChangeOfStateSrdo = false,
        SrdoConfigurationCheckInterval = TimeSpan.FromSeconds(3),
        Lss = new LssSlaveOptions(),
    }.With(pdoCount: pdoCount).WithErrorHistoryCapacity(5);

    /// <summary>Every property of <paramref name="result"/> other than <paramref name="except"/>,
    /// error behaviour and the applications is the one of <paramref name="original"/>, and each of
    /// those differs from a fresh options object: the comparison cannot pass on a default.</summary>
    private static void AssertEveryOtherOptionKept(CanOpenNodeOptions original, CanOpenNodeOptions result, params string[] except)
    {
        var fresh = new CanOpenNodeOptions();
        var compared = 0;
        var kept = typeof(CanOpenNodeOptions).GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(property => property.Name is not (nameof(CanOpenNodeOptions.EnableErrorBehavior) or "Applications")
                && !except.Contains(property.Name));
        foreach (var property in kept)
        {
            property.GetValue(original).Should().NotBe(property.GetValue(fresh), $"{property.Name} is set away from its default");
            property.GetValue(result).Should().Be(property.GetValue(original), $"{property.Name} is kept");
            compared++;
        }

        compared.Should().BeGreaterThan(20, "the loop reaches the options' properties");
    }

    [Fact]
    public void AddTo_Keeps_A_Higher_Pdo_Count_And_Every_Other_Option()
    {
        var other = new Cia401Device(Inputs(8));
        var device = new Cia401Device(Inputs(8, logicalDevice: 1));
        var original = EveryOptionChanged(pdoCount: 100).WithApplication(other);

        var options = Cia401Device.AddTo(original, device);

        options.PdoCount.Should().Be(100, "a count the caller set higher is not lowered");
        options.EnableErrorBehavior.Should().BeTrue();
        options.Applications.Should().Equal(other, device);
        AssertEveryOtherOptionKept(original, options);
    }

    [Fact]
    public void AddTo_That_Raises_The_Pdo_Count_Keeps_Every_Other_Option()
    {
        var device = new Cia401Device(Inputs(8, logicalDevice: 1));
        var original = EveryOptionChanged(pdoCount: 10);

        var options = Cia401Device.AddTo(original, device);

        options.PdoCount.Should().Be(65);
        options.EnableErrorBehavior.Should().BeTrue();
        options.Applications.Should().Equal(device);
        AssertEveryOtherOptionKept(original, options, nameof(CanOpenNodeOptions.PdoCount));
    }

    // ---- inputs, polarity, interrupts ----------------------------------------------------------------

    [Fact]
    public async Task An_Input_Change_In_Operational_Sends_One_Tpdo_With_The_New_Byte()
    {
        using var rig = new Cia401Rig(Inputs(8));
        await rig.StartAndTakeFirstTpdoAsync();

        rig.Device.SetDigitalInput(2, true);
        await rig.WaitForCountAsync(Cia401Rig.Tpdo1, 2);
        await rig.DrainAsync();

        rig.Payloads(Cia401Rig.Tpdo1).Skip(1).Should().ContainSingle().Which.Should().Equal(0x02);
        rig.Device.ReadDigitalInputs(1).Should().Be(0x02);
    }

    [Fact]
    public async Task An_Input_Change_In_Pre_Operational_Sends_Nothing_And_Shows_In_6000h()
    {
        using var rig = new Cia401Rig(Inputs(8));

        rig.Device.SetDigitalInputs(1, 0x81);
        (await rig.ReadU8Async(0x6000, 0x01)).Should().Be(0x81, "the SDO round trip runs after the posted set");
        await rig.DrainAsync();
        rig.Count(Cia401Rig.Tpdo1).Should().Be(0, "PDOs do not exist in Pre-operational (CiA 301 §7.3.2.2.5, Table 37)");

        await rig.StartAsync();
        await rig.WaitForCountAsync(Cia401Rig.Tpdo1, 1);
        await rig.DrainAsync();

        rig.Payloads(Cia401Rig.Tpdo1).Should().ContainSingle("only entering Operational sent it, not the change before")
            .Which.Should().Equal(0x81);
    }

    [Fact] // Review Focus 5
    public async Task Polarity_Applies_Once_To_6000h_The_Tpdo_And_The_Reader()
    {
        using var rig = new Cia401Rig(Inputs(8));
        await rig.WriteU8Async(0x6002, 0x01, 0x0F);

        (await rig.ReadU8Async(0x6000, 0x01)).Should().Be(0x0F, "physical 00h inverted on the low four lines");
        rig.Device.ReadDigitalInputs(1).Should().Be(0x0F);
        await rig.StartAsync();
        await rig.WaitForCountAsync(Cia401Rig.Tpdo1, 1);

        rig.Device.SetDigitalInputs(1, 0x0F);
        await rig.WaitForCountAsync(Cia401Rig.Tpdo1, 2);
        await rig.DrainAsync();

        rig.Payloads(Cia401Rig.Tpdo1).Should().HaveCount(2);
        rig.Payloads(Cia401Rig.Tpdo1)[0].Should().Equal(0x0F);
        rig.Payloads(Cia401Rig.Tpdo1)[1].Should().Equal(new byte[] { 0x00 }, "physical 0Fh inverted on the same lines is 00h");
        (await rig.ReadU8Async(0x6000, 0x01)).Should().Be(0x00);
        rig.Device.ReadDigitalInputs(1).Should().Be(0x00);
    }

    [Fact]
    public async Task Writing_The_Polarity_In_Operational_Is_An_Edge_Like_Any_Other()
    {
        using var rig = new Cia401Rig(Inputs(8));
        await rig.StartAndTakeFirstTpdoAsync();

        await rig.WriteU8Async(0x6002, 0x01, 0x01);
        await rig.WaitForCountAsync(Cia401Rig.Tpdo1, 2);
        await rig.DrainAsync();

        rig.Payloads(Cia401Rig.Tpdo1).Skip(1).Should().ContainSingle().Which.Should().Equal(0x01);
        (await rig.ReadU8Async(0x6000, 0x01)).Should().Be(0x01, "6000h changes as soon as 6002h does (§7.2.2 Figure 4)");
    }

    [Fact]
    public async Task A_Rising_Mask_Sends_On_Its_Own_Rising_Edge_Only()
    {
        using var rig = new Cia401Rig(Inputs(8));
        await rig.WriteU8Async(0x6006, 0x01, 0x00);
        await rig.WriteU8Async(0x6007, 0x01, 0x01);
        await rig.StartAndTakeFirstTpdoAsync();

        rig.Device.SetDigitalInput(1, true);
        await rig.WaitForCountAsync(Cia401Rig.Tpdo1, 2);
        rig.Device.SetDigitalInput(1, false);
        rig.Device.SetDigitalInput(2, true);
        await rig.DrainAsync();

        rig.Payloads(Cia401Rig.Tpdo1).Skip(1).Should().ContainSingle("a falling edge on channel 1 and a rising one on channel 2 are masked")
            .Which.Should().Equal(0x01);
        rig.Device.ReadDigitalInputs(1).Should().Be(0x02, "the value still updates without an interrupt");
    }

    [Fact] // §8.2.6: with the input inverted, the logical rising edge is the physical falling edge
    public async Task With_Polarity_The_Rising_Mask_Sends_On_The_Physical_Falling_Edge()
    {
        using var rig = new Cia401Rig(Inputs(8));
        await rig.WriteU8Async(0x6006, 0x01, 0x00);
        await rig.WriteU8Async(0x6007, 0x01, 0x01);
        await rig.WriteU8Async(0x6002, 0x01, 0x01);
        await rig.StartAndTakeFirstTpdoAsync();

        rig.Device.SetDigitalInput(1, true); // physical rising: logical 1 to 0, masked
        await rig.DrainAsync();
        rig.Payloads(Cia401Rig.Tpdo1).Should().HaveCount(1, "the physical rising edge is a logical falling one");

        rig.Device.SetDigitalInput(1, false); // physical falling: logical 0 to 1
        await rig.WaitForCountAsync(Cia401Rig.Tpdo1, 2);
        await rig.DrainAsync();

        rig.Payloads(Cia401Rig.Tpdo1).Should().HaveCount(2);
        rig.Payloads(Cia401Rig.Tpdo1)[1].Should().Equal(0x01);
    }

    [Fact]
    public async Task With_The_Global_Interrupt_Off_Nothing_Sends_But_The_Value_Updates()
    {
        using var rig = new Cia401Rig(Inputs(8));
        await rig.WriteU8Async(0x6005, 0x00, 0);
        await rig.StartAndTakeFirstTpdoAsync();

        rig.Device.SetDigitalInputs(1, 0xFF);
        await rig.DrainAsync();

        rig.Payloads(Cia401Rig.Tpdo1).Should().HaveCount(1, "only entering Operational sent TPDO 1");
        (await rig.ReadU8Async(0x6000, 0x01)).Should().Be(0xFF);
        rig.Device.ReadDigitalInputs(1).Should().Be(0xFF);
    }

    [Fact]
    public async Task The_Filter_Enable_Is_Stored_And_Changes_Nothing()
    {
        using var rig = new Cia401Rig(Inputs(8));
        await rig.WriteU8Async(0x6003, 0x01, 0xFF);
        await rig.StartAndTakeFirstTpdoAsync();

        rig.Device.SetDigitalInput(1, true);
        await rig.WaitForCountAsync(Cia401Rig.Tpdo1, 2);
        await rig.DrainAsync();

        (await rig.ReadU8Async(0x6003, 0x01)).Should().Be(0xFF);
        rig.Payloads(Cia401Rig.Tpdo1).Skip(1).Should().ContainSingle().Which.Should().Equal(0x01);
    }

    [Fact]
    public async Task Entering_Operational_Sends_Tpdo_1_Once_With_The_Current_Inputs()
    {
        using var rig = new Cia401Rig(Inputs(20));
        // Masks that would send nothing: entering Operational sends regardless (§6.1).
        await rig.WriteU8Async(0x6005, 0x00, 0);
        rig.Device.SetDigitalInputs(1, 0xA5);
        rig.Device.SetDigitalInputs(2, 0x5A);
        rig.Device.SetDigitalInputs(3, 0x0F);
        rig.Settle();

        await rig.StartAsync();
        await rig.WaitForCountAsync(Cia401Rig.Tpdo1, 1);
        await rig.DrainAsync();

        rig.Payloads(Cia401Rig.Tpdo1).Should().ContainSingle("one frame per PDO, not one per sub-index")
            .Which.Should().Equal(0xA5, 0x5A, 0x0F);
    }

    [Fact] // §6.1 names every TPDO of transmission type FFh, not the TPDOs that map an input
    public async Task Entering_Operational_Sends_A_Tpdo_The_Master_Remapped_To_A_Parameter()
    {
        using var rig = new Cia401Rig(Inputs(8), Cia401Rig.WritablePdoRecords);
        // CiA 301 v4.2.0 §7.5.2.38: disable the PDO, clear the mapping, write the entry, set the
        // count, enable the PDO again. TPDO 1 keeps transmission type FFh.
        uint cobId = await rig.ReadU32Async(0x1800, 0x01);
        await rig.WriteU32Async(0x1800, 0x01, cobId | 0x8000_0000u);
        await rig.WriteU8Async(0x1A00, 0x00, 0);
        await rig.WriteU32Async(0x1A00, 0x01, 0x6002_0108);
        await rig.WriteU8Async(0x1A00, 0x00, 1);
        await rig.WriteU32Async(0x1800, 0x01, cobId);
        await rig.WriteU8Async(0x6002, 0x01, 0x5A);
        rig.Device.SetDigitalInputs(1, 0x0F);
        (await rig.ReadU8Async(0x6000, 0x01)).Should().Be(0x55, "6000h shows 0Fh under the polarity 5Ah, which tells it apart from 6002h");
        (await rig.ReadU8Async(0x1800, 0x02)).Should().Be(0xFF);

        await rig.StartAsync();
        await rig.WaitForCountAsync(Cia401Rig.Tpdo1, 1);
        await rig.DrainAsync();

        rig.Payloads(Cia401Rig.Tpdo1).Should().ContainSingle("entering Operational sends every TPDO of type FFh once (CiA 401-1 §6.1)")
            .Which.Should().Equal(new byte[] { 0x5A }, "TPDO 1 now carries the polarity 6002h:01");
    }

    [Theory]
    [InlineData(0xFF, true, 1)]
    [InlineData(0xFE, true, 0)]
    [InlineData(0xFF, false, 0)]
    public async Task Each_Operational_Entry_Uses_The_Live_Type_And_Mapping_Once_Per_Node(
        byte transmissionType, bool enabled, int framesPerEntry)
    {
        using var rig = new Cia401Rig(new[] { Inputs(8), Inputs(8, logicalDevice: 1) }, Cia401Rig.WritablePdoRecords);
        uint cobId = await rig.ReadU32Async(0x1800, 0x01);
        await rig.WriteU32Async(0x1800, 0x01, cobId | 0x8000_0000u);
        await rig.WriteU8Async(0x1A00, 0, 0);
        await rig.WriteU32Async(0x1A00, 1, 0x1001_0008); // error register, outside both logical devices
        await rig.WriteU8Async(0x1A00, 0, 1);
        await rig.WriteU8Async(0x1800, 2, transmissionType);
        if (enabled) await rig.WriteU32Async(0x1800, 1, cobId);

        await rig.StartAsync();
        await rig.DrainAsync();
        rig.Count(Cia401Rig.Tpdo1).Should().Be(framesPerEntry);
        await rig.EnterPreOperationalAsync();
        await rig.StartAsync();
        await rig.DrainAsync();
        var frames = rig.Payloads(Cia401Rig.Tpdo1);
        frames.Should().HaveCount(2 * framesPerEntry);
        foreach (var frame in frames) frame.Should().Equal(new byte[] { 0 });
    }

    [Fact]
    public async Task Twenty_Inputs_Are_Three_Bytes_And_Channel_17_Is_The_Low_Bit_Of_The_Third()
    {
        using var rig = new Cia401Rig(Inputs(20));
        await rig.StartAndTakeFirstTpdoAsync();

        rig.Device.SetDigitalInput(17, true);
        await rig.WaitForCountAsync(Cia401Rig.Tpdo1, 2);
        await rig.DrainAsync();

        rig.Payloads(Cia401Rig.Tpdo1)[1].Should().Equal(0x00, 0x00, 0x01);
    }

    // ---- the application side: arguments, threads, life cycle --------------------------------------------

    [Fact]
    public void A_Channel_Or_Group_The_Device_Does_Not_Have_Is_Refused()
    {
        using var rig = new Cia401Rig(Inputs(20));

        FluentActions.Invoking(() => rig.Device.SetDigitalInput(21, true))
            .Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("channel");
        FluentActions.Invoking(() => rig.Device.SetDigitalInput(0, true))
            .Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("channel");
        FluentActions.Invoking(() => rig.Device.SetDigitalInputs(4, 0))
            .Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("group");
        FluentActions.Invoking(() => rig.Device.SetDigitalInputs(0, 0))
            .Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("group");
        FluentActions.Invoking(() => rig.Device.ReadDigitalInputs(4))
            .Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("group");
    }

    [Fact]
    public void A_Configured_Device_Without_Digital_Inputs_Refuses_Every_Channel_And_Group()
    {
        using var rig = new Cia401Rig(new Cia401Configuration { DigitalOutputs = 8 });

        FluentActions.Invoking(() => rig.Device.SetDigitalInput(1, true)).Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("The device has 0 digital inputs*").Which.ParamName.Should().Be("channel");
        FluentActions.Invoking(() => rig.Device.SetDigitalInputs(1, 0x01)).Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("The device has 0 groups of digital inputs*").Which.ParamName.Should().Be("group");
        FluentActions.Invoking(() => rig.Device.ReadDigitalInputs(1)).Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("The device has 0 groups of digital inputs*").Which.ParamName.Should().Be("group");
    }

    [Fact]
    public void Levels_For_Channels_The_Last_Group_Does_Not_Have_Are_Ignored()
    {
        using var rig = new Cia401Rig(Inputs(20));

        rig.Device.SetDigitalInputs(3, 0xFF);
        rig.Settle();

        rig.Device.ReadDigitalInputs(3).Should().Be(0x0F, "channels 21 to 24 do not exist");
    }

    [Fact]
    public void Sets_From_Several_Threads_On_One_Group_Lose_Nothing()
    {
        using var rig = new Cia401Rig(Inputs(8));
        using var go = new ManualResetEventSlim();

        var threads = Enumerable.Range(1, 8).Select(channel => new Thread(() =>
        {
            go.Wait();
            for (int i = 0; i < 50; i++)
            {
                rig.Device.SetDigitalInput(channel, false);
                rig.Device.SetDigitalInput(channel, true);
            }
        })).ToList();
        threads.ForEach(t => t.Start());
        go.Set();
        threads.ForEach(t => t.Join());
        rig.Settle();

        rig.Device.ReadDigitalInputs(1).Should().Be(0xFF, "each set changes its own channel and keeps the others");
    }

    [Fact]
    public async Task A_Download_To_6000h_Is_Refused_As_Read_Only()
    {
        using var rig = new Cia401Rig(Inputs(8));

        var response = await rig.DownloadU8Async(0x6000, 0x01, 0x55);

        Cia401Rig.AbortCode(response).Should().Be(AttemptWriteReadOnly);
        (await rig.ReadU8Async(0x6000, 0x01)).Should().Be(0x00);
    }

    [Fact]
    public void A_Local_Write_To_A_Read_Only_Entry_Is_Refused_With_The_Read_Only_Abort()
    {
        using var rig = new Cia401Rig(Inputs(8));

        // The dictionary checks the access of an SDO download only; a local write asks the claimant.
        FluentActions.Invoking(() => rig.Od.WriteRaw(0x6000, 0x01, new byte[] { 0x55 }))
            .Should().Throw<ArgumentException>().WithMessage("*0x06010002*");
        FluentActions.Invoking(() => rig.Od.WriteRaw(0x6006, 0x00, new byte[] { 0x02 }))
            .Should().Throw<ArgumentException>().WithMessage("*0x06010002*");
        rig.Od.ReadUnsigned(0x6000, 0x01).Should().Be(0u);
        rig.Od.ReadUnsigned(0x6006, 0x00).Should().Be(1u);

        rig.Od.WriteRaw(0x6006, 0x01, new byte[] { 0x0F });
        rig.Od.ReadUnsigned(0x6006, 0x01).Should().Be(0x0Fu, "a read-write entry is the master's and the user's to write");
    }

    [Fact]
    public void Before_It_Is_Attached_The_Device_Refuses_To_Set_Or_Read()
    {
        var device = new Cia401Device(Inputs(8));

        FluentActions.Invoking(() => device.SetDigitalInput(1, true)).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => device.SetDigitalInputs(1, 0xFF)).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => device.ReadDigitalInputs(1)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_Node_Callback_Before_Attach_Is_Refused()
    {
        var device = new Cia401Device(Inputs(8));

        // The node calls the actor-loop callbacks only once Attach has returned; a caller that does
        // not is told so rather than acting on a device without a host.
        FluentActions.Invoking(() => device.OnNmtStateChanged(NmtState.PreOperational, NmtState.Operational))
            .Should().ThrowExactly<InvalidOperationException>().WithMessage("*called before it was attached*");
    }

    [Fact]
    public void After_The_Node_Is_Disposed_A_Set_Does_Nothing_And_The_Reader_Shows_The_Last_Value()
    {
        Cia401Device device;
        using (var rig = new Cia401Rig(Inputs(8)))
        {
            rig.Device.SetDigitalInputs(1, 0x3C);
            rig.Settle();
            device = rig.Device;
        }

        FluentActions.Invoking(() => device.SetDigitalInputs(1, 0xFF)).Should().NotThrow();
        device.ReadDigitalInputs(1).Should().Be(0x3C);
    }

    // ---- the falling mask, §8.2.7 ---------------------------------------------------------------------

    [Fact]
    public async Task A_Falling_Mask_Sends_On_Its_Own_Falling_Edge_Only()
    {
        using var rig = new Cia401Rig(Inputs(8));
        await rig.WriteU8Async(0x6006, 0x01, 0x00);
        await rig.WriteU8Async(0x6008, 0x01, 0x01);
        await rig.StartAndTakeFirstTpdoAsync();

        rig.Device.SetDigitalInput(1, true); // rising: masked
        await rig.DrainAsync();
        rig.Count(Cia401Rig.Tpdo1).Should().Be(1, "6008h asks for falling edges only");

        rig.Device.SetDigitalInput(1, false); // falling
        await rig.WaitForCountAsync(Cia401Rig.Tpdo1, 2);
        await rig.DrainAsync();

        rig.Payloads(Cia401Rig.Tpdo1).Should().HaveCount(2);
        rig.Payloads(Cia401Rig.Tpdo1)[1].Should().Equal(0x00);
    }

    [Fact] // §8.2.7: with the input inverted, the logical falling edge is the physical rising edge
    public async Task With_Polarity_The_Falling_Mask_Sends_On_The_Physical_Rising_Edge()
    {
        using var rig = new Cia401Rig(Inputs(8));
        await rig.WriteU8Async(0x6006, 0x01, 0x00);
        await rig.WriteU8Async(0x6008, 0x01, 0x01);
        await rig.WriteU8Async(0x6002, 0x01, 0x01);
        await rig.StartAndTakeFirstTpdoAsync();

        rig.Device.SetDigitalInput(1, true); // physical rising: logical 1 to 0
        await rig.WaitForCountAsync(Cia401Rig.Tpdo1, 2);
        await rig.DrainAsync();
        rig.Payloads(Cia401Rig.Tpdo1).Should().HaveCount(2);
        rig.Payloads(Cia401Rig.Tpdo1)[1].Should().Equal(0x00);

        rig.Device.SetDigitalInput(1, false); // physical falling: logical 0 to 1, masked
        await rig.DrainAsync();
        rig.Payloads(Cia401Rig.Tpdo1).Should().HaveCount(2, "the physical falling edge is a logical rising one");
    }

    // ---- logical devices ----------------------------------------------------------------------------

    [Fact]
    public void A_Device_At_Logical_Device_1_Alone_Fails_The_Open()
    {
        using var bus = VirtualAdapterFixture.Open(VirtualAdapterFixture.NewSession("cia401-ld1"), 1);
        using var service = new CanBusService(bus);
        var device = new Cia401Device(Inputs(8, logicalDevice: 1));

        var failure = Assert.Throws<ArgumentException>(
            () => new CanOpenNode(service, 0x12, Cia401Device.AddTo(new CanOpenNodeOptions(), device), ownsService: false));

        failure.ParamName.Should().Be("options");
        failure.Message.Should().Contain("Logical device 0 has no device type",
            "logical devices are numbered from 0 without a gap (CiA 301 v4.2.0 §7.5.2.1)");
    }

    [Fact]
    public async Task Two_Devices_Sit_At_Logical_Devices_0_And_1()
    {
        using var rig = new Cia401Rig(new[] { Inputs(8), Inputs(20, logicalDevice: 1) });

        (await rig.ReadU8Async(0x6000, 0x00)).Should().Be(1, "logical device 0 has one group");
        (await rig.ReadU8Async(0x6800, 0x00)).Should().Be(3, "logical device 1 has its objects 800h higher (CiA 301 v4.2.0 §7.4.1)");
        (await rig.ReadU8Async(0x6805, 0x00)).Should().Be(1, "6005h of logical device 1 is TRUE by default");
        Cia401Rig.AbortCode(await rig.SdoAsync(new byte[] { 0x40, 0x00, 0x60, 0x02, 0, 0, 0, 0 }))
            .Should().Be(0x06090011u, "logical device 1's groups are not declared in 6000h");

        rig.Od.ReadUnsigned(0x1000, 0x00).Should().Be(0xFFFF0191u, "profile 401 with further logical devices (CiA 301 v4.2.0 §7.5.2.1)");
        rig.Od.ReadUnsigned(0x67FF, 0x00).Should().Be(0x00010191u);
        rig.Od.ReadUnsigned(0x6FFF, 0x00).Should().Be(0x00010191u);
        rig.Od.ReadUnsigned(0x1A40, 0x00).Should().Be(3u, "TPDO 65 is TPDO 1 of logical device 1 (CiA 301 v4.2.0 §7.2.2.1)");
        rig.Od.ReadUnsigned(0x1A40, 0x01).Should().Be(0x68000108u);
        rig.Od.ReadUnsigned(0x1A40, 0x03).Should().Be(0x68000308u);

        rig.Devices[1].SetDigitalInput(17, true);
        rig.Settle();

        (await rig.ReadU8Async(0x6800, 0x03)).Should().Be(0x01);
        (await rig.ReadU8Async(0x6000, 0x01)).Should().Be(0x00, "logical device 0 is another device");
        rig.Devices[1].ReadDigitalInputs(3).Should().Be(0x01);
    }

    // ---- NMT and more than 64 inputs ------------------------------------------------------------------

    [Fact]
    public async Task Stopped_To_Operational_Sends_Tpdo_1_With_The_Current_Inputs()
    {
        using var rig = new Cia401Rig(Inputs(8));
        await rig.StartAndTakeFirstTpdoAsync();
        rig.Transmit(0x000, new[] { (byte)NmtCommand.Stop, Cia401Rig.DeviceId });
        await rig.WaitForStateAsync(NmtState.Stopped);
        rig.Device.SetDigitalInputs(1, 0x42);

        await rig.StartAsync();
        await rig.WaitForCountAsync(Cia401Rig.Tpdo1, 2);
        await rig.DrainAsync();

        rig.Payloads(Cia401Rig.Tpdo1).Should().HaveCount(2, "the change in Stopped sent nothing, entering Operational sent once");
        rig.Payloads(Cia401Rig.Tpdo1)[1].Should().Equal(0x42);
    }

    [Fact]
    public async Task A_Device_With_72_Inputs_Opens_And_Its_Ninth_Group_Has_No_Default_Pdo()
    {
        using var rig = new Cia401Rig(Inputs(72));
        var failures = new List<Exception>();
        rig.Node.BackgroundExceptionOccurred += (_, ex) => { lock (failures) failures.Add(ex); };

        await rig.StartAndTakeFirstTpdoAsync();
        rig.Payloads(Cia401Rig.Tpdo1)[0].Should().HaveCount(8, "TPDO 1 carries eight groups, 64 channels (CiA 401-1 §6.2.6)");

        rig.Device.SetDigitalInput(72, true);
        await rig.DrainAsync();

        (await rig.ReadU8Async(0x6000, 0x09)).Should().Be(0x80);
        rig.Count(Cia401Rig.Tpdo1).Should().Be(1, "no default PDO maps group 9 (CiA 401-1 §6.1)");
        await rig.FlushBackgroundReportsAsync(); // the reports are delivered on the thread pool
        lock (failures) failures.Should().BeEmpty("entering Operational and the change raised nothing");
    }

    // ---- BOOLEAN --------------------------------------------------------------------------------------

    [Fact]
    public async Task A_Boolean_Other_Than_0_Or_1_Is_Refused_With_The_Value_Range_Abort()
    {
        using var rig = new Cia401Rig(Inputs(8));

        Cia401Rig.AbortCode(await rig.DownloadU8Async(0x6005, 0x00, 0x02)).Should().Be(0x06090030u,
            "a BOOLEAN is TRUE or FALSE (CiA 301 v4.2.0 §7.1.4.3)");
        (await rig.ReadU8Async(0x6005, 0x00)).Should().Be(1);
        await rig.WriteU8Async(0x6005, 0x00, 0x00);
        (await rig.ReadU8Async(0x6005, 0x00)).Should().Be(0);
        await rig.WriteU8Async(0x6005, 0x00, 0x01);
        (await rig.ReadU8Async(0x6005, 0x00)).Should().Be(1);
    }

    [Fact]
    public void A_Local_Write_Of_A_Boolean_Other_Than_0_Or_1_Is_Refused_With_The_Value_Range_Abort()
    {
        using var rig = new Cia401Rig(Inputs(8));

        // The same ValidateWrite decides a local write and an SDO download: the dictionary asks the
        // claimant for both; a local refusal throws with the abort code an SDO download gets.
        FluentActions.Invoking(() => rig.Od.WriteRaw(0x6005, 0x00, new byte[] { 0x02 }))
            .Should().Throw<ArgumentException>().WithMessage("*0x06090030*");
        rig.Od.ReadRaw(0x6005, 0x00).Should().Equal(new byte[] { 0x01 });

        rig.Od.WriteRaw(0x6005, 0x00, new byte[] { 0x00 });
        rig.Od.ReadRaw(0x6005, 0x00).Should().Equal(new byte[] { 0x00 });
    }
}
