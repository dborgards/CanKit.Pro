using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Cia408;
using CanKit.Pro.CANopen.Nmt;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Cia408;

/// <summary>
/// The CiA 408 v2.2.0 device on a node (<see cref="Cia408Device"/>): what it declares, the control
/// word driving the state machine (§7.6, §7.7, §9.3.2, §9.3.6), the status word it shows (§9.3.3,
/// §9.3.7), faults with their EMCY, <c>604Eh</c> and <c>1029h:02</c> (§6, §8.6.8, §9.3.8), and local
/// control (§9.3.9). Everything goes over the wire from a raw master, SDO and RPDO; the indices are
/// written out here, as the independent check of the catalogue.
/// </summary>
public class Cia408DeviceTests
{
    private const ushort ControlWord16 = 0x6040;
    private const ushort StatusWord16 = 0x6041;
    private const ushort DeviceMode = 0x6042;
    private const ushort ControlMode = 0x6043;
    private const ushort ControlWord32 = 0x6044;
    private const ushort StatusWord32 = 0x6045;
    private const ushort ErrorCode = 0x604E;
    private const ushort Local = 0x604F;

    /// <summary>CiA 301 v4.2.0 Table 22: "Value range of parameter exceeded".</summary>
    private const uint ValueRangeExceeded = 0x0609_0030;

    /// <summary>Table 1 of CiA 408 v2.2.0 §6.3: input current too high.</summary>
    private const ushort InputCurrentTooHigh = 0x2110;

    /// <summary>Table 1: power supply voltage too low.</summary>
    private const ushort SupplyTooLow = 0x3412;

    // ---- attach ---------------------------------------------------------------------------------

    [Fact]
    public void Attach_Declares_The_Eight_Objects_Of_Clause_9_3()
    {
        using var rig = new Cia408Rig(Cia408Rig.Valve);
        rig.Settle();

        Entry(rig, ControlWord16).Should().Be((OdDataType.Unsigned16, OdAccess.ReadWrite, true));
        Entry(rig, StatusWord16).Should().Be((OdDataType.Unsigned16, OdAccess.ReadOnly, true));
        Entry(rig, DeviceMode).Should().Be((OdDataType.Integer8, OdAccess.ReadWrite, true));
        Entry(rig, ControlMode).Should().Be((OdDataType.Integer8, OdAccess.ReadOnly, true), "§9.3.5: one control mode");
        Entry(rig, ControlWord32).Should().Be((OdDataType.Unsigned32, OdAccess.ReadWrite, true));
        Entry(rig, StatusWord32).Should().Be((OdDataType.Unsigned32, OdAccess.ReadOnly, true));
        Entry(rig, ErrorCode).Should().Be((OdDataType.Unsigned16, OdAccess.ReadOnly, true));
        Entry(rig, Local).Should().Be((OdDataType.Integer8, OdAccess.ReadWrite, true));

        rig.Od.ReadUnsigned(ControlWord16, 0).Should().Be(0x0000u, "§9.3.2: default 0000h");
        rig.Od.ReadUnsigned(ControlWord32, 0).Should().Be(0x0000_0000u, "§9.3.6: default 0000 0000h");
        rig.Od.ReadRaw(DeviceMode, 0).Should().Equal(new byte[] { 0x01 }, "§9.3.4: default 01h, setpoint via bus");
        rig.Od.ReadRaw(ControlMode, 0).Should().Equal(new byte[] { 0x02 }, "the first control mode of the configuration");
        rig.Od.ReadUnsigned(ErrorCode, 0).Should().Be(0x0000u, "no fault has occurred");
        rig.Od.ReadRaw(Local, 0).Should().Equal(new byte[] { 0x00 }, "§9.3.9: a valve defaults to the control word over the bus");
        rig.Od.ReadUnsigned(StatusWord16, 0).Should().Be(0x0008u, "INIT (R = 1, Table 4), remote control");
        rig.Od.ReadUnsigned(StatusWord32, 0).Should().Be(0x0000_0008u, "the 32-bit status word shows the same state");
    }

    [Theory]
    [InlineData(new[] { 2, 1 }, (byte)0x01)] // setpoint local, setpoint via bus
    [InlineData(new[] { 2 }, (byte)0x02)]
    public void The_Device_Mode_Defaults_To_1_Where_Supported_And_To_The_First_Mode_Otherwise(int[] modes, byte expected)
    {
        using var rig = new Cia408Rig(new Cia408Configuration
        {
            Resolution = Cia408Resolution.Valve16,
            ControlModes = new[] { Cia408ControlMode.SpoolPositionClosedLoop },
            DeviceModes = modes.Select(m => (Cia408DeviceMode)m).ToArray(),
        });

        rig.Od.ReadRaw(DeviceMode, 0).Should().Equal(new[] { expected });
    }

    [Fact]
    public void Attach_Sets_The_Device_Type_The_Default_Pdos_And_The_408_Error_Behaviour()
    {
        using var rig = new Cia408Rig(Cia408Rig.Valve);

        rig.Od.ReadUnsigned(0x1000, 0).Should().Be(Cia408Rig.Valve.DeviceType.Value);
        rig.Od.ReadUnsigned(0x1600, 1).Should().Be(0x6040_0010u, "RPDO 1 maps the 16-bit control word");
        rig.Od.ReadUnsigned(0x1A00, 1).Should().Be(0x6041_0010u, "TPDO 1 maps the 16-bit status word");
        rig.Od.ReadRaw(0x1029, 0).Should().Equal(new byte[] { 0x02 }, "§8.6.8: two error classes");
        rig.Od.ReadRaw(0x1029, 1).Should().Equal(new byte[] { 0x01 }, "communication error, default 01h");
        rig.Od.ReadRaw(0x1029, 2).Should().Equal(new byte[] { 0x01 }, "internal device error, default 01h");
    }

    [Fact]
    public void The_Start_Is_Raised_As_D0_And_D1_Into_Init()
    {
        using var rig = new Cia408Rig(Cia408Rig.Valve);
        rig.Settle();

        rig.Device.State.Should().Be(Cia408State.Init);
        rig.Changes.Should().ContainSingle();
        Describe(rig.Changes[0]).Should().Be("NotReady->Init [0,1]");
    }

    // ---- control word ---------------------------------------------------------------------------

    [Fact]
    public async Task One_Control_Word_Over_Rpdo1_Walks_Init_To_Active_And_Tpdo1_Carries_The_Status()
    {
        using var rig = new Cia408Rig(Cia408Rig.Valve);
        await rig.StartAsync();
        await rig.DrainAsync();
        int before = rig.Count(CanOpenProfileRig.Tpdo1);

        await rig.SendRpdo1Async(0x07, 0x00);
        await rig.WaitForCountAsync(CanOpenProfileRig.Tpdo1, before + 1);
        await rig.DrainAsync();

        rig.Device.State.Should().Be(Cia408State.Active);
        rig.Payloads(CanOpenProfileRig.Tpdo1).Skip(before).Select(p => BitConverter.ToString(p)).Should().Equal(
            new[] { "0F-00", "0F-00" },
            "ACTIVE is RMHD 1111b (Table 4): the change sends one TPDO and the RPDO's answer (§8.4.1) another");
        Describe(rig.Changes.Last()).Should().Be("Init->Active [2,3,4]");
        rig.Od.ReadUnsigned(StatusWord32, 0).Should().Be(0x0000_000Fu);

        int afterRpdo = rig.Count(CanOpenProfileRig.Tpdo1);
        await rig.WriteU16Async(ControlWord16, 0, 0x0003);
        rig.Settle();
        await rig.DrainAsync();
        rig.Payloads(CanOpenProfileRig.Tpdo1).Skip(afterRpdo).Should().ContainSingle()
            .Which.Should().Equal(new byte[] { 0x0B, 0x00 }, "a change over SDO is one TPDO: HOLD (D5)");
    }

    [Fact]
    public async Task Both_Word_Pairs_Drive_One_State_And_Show_It()
    {
        using var rig = new Cia408Rig(Cia408Rig.Valve);

        await rig.WriteU32Async(ControlWord32, 0, 0x0000_0003);
        rig.Settle();
        rig.Device.State.Should().Be(Cia408State.Hold, "a write to 6044h drives the machine");
        (await rig.ReadU16Async(StatusWord16, 0)).Should().Be(0x000B);
        (await rig.ReadU32Async(StatusWord32, 0)).Should().Be(0x0000_000Bu);

        await rig.WriteU16Async(ControlWord16, 0, 0x0001);
        rig.Settle();
        rig.Device.State.Should().Be(Cia408State.Disabled, "a write to 6040h drives the same machine (D6)");
        (await rig.ReadU16Async(StatusWord16, 0)).Should().Be(0x0009);
        (await rig.ReadU32Async(StatusWord32, 0)).Should().Be(0x0000_0009u);
    }

    [Fact] // Review Focus 1, #557
    public async Task Two_Sdo_Writes_That_Land_Before_The_Device_Is_Told_Acknowledge_The_Fault()
    {
        using var rig = new Cia408Rig(Cia408Rig.Valve);
        await rig.WriteU16Async(ControlWord16, 0, 0x0001);
        rig.Settle();
        rig.Device.ReportFault(SupplyTooLow, Cia408FaultState.FaultDisabled);
        rig.Settle();
        rig.Device.ClearFaultCause(SupplyTooLow);
        rig.Settle();
        rig.Device.State.Should().Be(Cia408State.FaultDisabled);

        // 0009h then 0001h, both committed before the job that tells the device runs: the reset edge
        // is 0001h (the word before the fault) to 0009h, and only the order of the writes shows it.
        await rig.WhileActorHeldAsync(() => rig.QueueFramesAsync(
            (CanOpenProfileRig.SdoRequest, CanOpenProfileRig.DownloadU16(ControlWord16, 0, 0x0009)),
            (CanOpenProfileRig.SdoRequest, CanOpenProfileRig.DownloadU16(ControlWord16, 0, 0x0001))));
        rig.Settle();

        rig.Device.State.Should().Be(Cia408State.Disabled, "D10: FAULT_DISABLED with 0001b then 1001b");
        Describe(rig.Changes.Last()).Should().Be("FaultDisabled->Disabled [10]");
    }

    [Theory]
    [InlineData(ControlWord16, 0x0000_0010u)] // bit 4
    [InlineData(ControlWord16, 0x0000_0020u)] // bit 5
    [InlineData(ControlWord16, 0x0000_1000u)] // bit 12
    [InlineData(ControlWord32, 0x0000_0010u)]
    [InlineData(ControlWord32, 0x0000_0020u)]
    [InlineData(ControlWord32, 0x0000_1000u)]
    [InlineData(ControlWord32, 0x0100_0000u)] // bit 24
    [InlineData(ControlWord32, 0x8000_0000u)] // bit 31
    public async Task A_Control_Word_With_A_Reserved_Bit_Is_Refused(ushort index, uint reservedBit)
    {
        using var rig = new Cia408Rig(Cia408Rig.Valve);
        uint word = 0x0001 | reservedBit;

        var response = index == ControlWord16
            ? await rig.DownloadU16Async(index, 0, (ushort)word)
            : await rig.DownloadU32Async(index, 0, word);
        rig.Settle();

        CanOpenProfileRig.AbortCode(response).Should().Be(ValueRangeExceeded,
            "Tables 73 and 85: reserved, always 0");
        rig.Od.ReadUnsigned(index, 0).Should().Be(0u, "a refused write stores nothing");
        rig.Device.State.Should().Be(Cia408State.Init);
    }

    [Theory]
    [InlineData(ControlWord16, 0x0000_EFC1u)] // bits 6-11 and 13-15 with D
    [InlineData(ControlWord32, 0x00FF_EFC1u)] // and 16-23 in the 32-bit word
    public async Task The_Bits_Without_Behaviour_Are_Stored_And_Handed_On(ushort index, uint word)
    {
        using var rig = new Cia408Rig(Cia408Rig.Valve);

        if (index == ControlWord16) await rig.WriteU16Async(index, 0, (ushort)word);
        else await rig.WriteU32Async(index, 0, word);
        rig.Settle();

        rig.Od.ReadUnsigned(index, 0).Should().Be(word);
        rig.Device.ControlWord.Should().Be(word);
        rig.Device.State.Should().Be(Cia408State.Disabled, "only bits 3..0 move the machine");
    }

    [Fact]
    public void A_Local_Write_To_A_Status_Word_Is_Refused()
    {
        using var rig = new Cia408Rig(Cia408Rig.Valve);
        rig.Settle();

        var act = () => rig.Od.WriteRaw(StatusWord16, 0, new byte[] { 0x0F, 0x00 });

        act.Should().Throw<ArgumentException>();
        rig.Od.ReadUnsigned(StatusWord16, 0).Should().Be(0x0008u);
    }

    // ---- faults ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_Fault_Sends_Its_Emcy_Clears_Ready_In_The_Status_And_Sets_604E()
    {
        using var rig = new Cia408Rig(Cia408Rig.Valve);
        await rig.StartAsync();
        await rig.SendRpdo1Async(0x07, 0x00);
        await rig.DrainAsync();
        int tpdos = rig.Count(CanOpenProfileRig.Tpdo1);

        rig.Device.ReportFault(InputCurrentTooHigh, Cia408FaultState.FaultHold, errorRegisterBits: 0x02);
        await rig.WaitForCountAsync(CanOpenProfileRig.Emcy, 1);
        await rig.WaitForCountAsync(CanOpenProfileRig.Tpdo1, tpdos + 1);

        rig.Payloads(CanOpenProfileRig.Emcy)[0].Take(3).Should().Equal(new byte[] { 0x10, 0x21, 0x03 },
            "the code, and the register with the current bit and the generic bit");
        rig.Payloads(CanOpenProfileRig.Tpdo1).Last().Should().Equal(new byte[] { 0x03, 0x00 }, "FAULT_HOLD is 0011b, R = 0");
        rig.Device.State.Should().Be(Cia408State.FaultHold);
        (await rig.ReadU16Async(ErrorCode, 0)).Should().Be(InputCurrentTooHigh);
        Describe(rig.Changes.Last()).Should().Be("Active->FaultHold [8,9]");
    }

    [Fact]
    public async Task The_Reset_Is_Refused_While_A_Cause_Lasts_And_The_Edge_Is_Consumed()
    {
        using var rig = new Cia408Rig(Cia408Rig.Valve);
        await rig.WriteU16Async(ControlWord16, 0, 0x0001);
        rig.Settle();
        rig.Device.ReportFault(InputCurrentTooHigh, Cia408FaultState.FaultDisabled);
        rig.Device.ReportFault(SupplyTooLow, Cia408FaultState.FaultDisabled);
        rig.Settle();

        await rig.WriteU16Async(ControlWord16, 0, 0x0009);
        rig.Settle();
        rig.Device.State.Should().Be(Cia408State.FaultDisabled, "two causes are active");

        rig.Device.ClearFaultCause(InputCurrentTooHigh);
        rig.Settle();
        await rig.WriteU16Async(ControlWord16, 0, 0x0001);
        await rig.WriteU16Async(ControlWord16, 0, 0x0009);
        rig.Settle();
        rig.Device.State.Should().Be(Cia408State.FaultDisabled, "one cause is still active");

        rig.Device.ClearFaultCause(SupplyTooLow);
        rig.Settle();
        rig.Device.State.Should().Be(Cia408State.FaultDisabled, "the state stays until the master resets");
        await rig.WriteU16Async(ControlWord16, 0, 0x0009);
        rig.Settle();
        rig.Device.State.Should().Be(Cia408State.FaultDisabled,
            "the refused edge was consumed: 1001b after 1001b is no edge");

        await rig.WriteU16Async(ControlWord16, 0, 0x0001);
        await rig.WriteU16Async(ControlWord16, 0, 0x0009);
        rig.Settle();
        rig.Device.State.Should().Be(Cia408State.Disabled, "a fresh 0001b to 1001b with no cause left resets (D10)");
    }

    [Fact]
    public async Task Clearing_Every_Cause_Ends_Its_Errors_But_Leaves_The_State()
    {
        using var rig = new Cia408Rig(Cia408Rig.Valve);
        rig.Device.ReportFault(InputCurrentTooHigh, Cia408FaultState.FaultInit, errorRegisterBits: 0x02);
        rig.Device.ReportFault(SupplyTooLow, Cia408FaultState.FaultInit, errorRegisterBits: 0x04);
        rig.Settle();
        rig.Od.ReadRaw(0x1001, 0).Should().Equal(new byte[] { 0x07 }, "both errors and the generic bit");

        rig.Device.ClearFaultCause(InputCurrentTooHigh);
        rig.Settle();
        rig.Od.ReadRaw(0x1001, 0).Should().Equal(new byte[] { 0x05 });

        rig.Device.ClearFaultCause(SupplyTooLow);
        rig.Settle();
        rig.Od.ReadRaw(0x1001, 0).Should().Equal(new byte[] { 0x00 });
        rig.Device.State.Should().Be(Cia408State.FaultInit, "the state stays until the master resets");
        (await rig.ReadU16Async(ErrorCode, 0)).Should().Be(SupplyTooLow, "604Eh records what occurred last");
    }

    [Fact]
    public void A_Cause_Reported_Again_While_Active_Changes_Nothing()
    {
        using var rig = new Cia408Rig(Cia408Rig.Valve);
        rig.Od.WriteRaw(ControlWord16, 0, new byte[] { 0x03, 0x00 });
        rig.Settle();
        rig.Device.ReportFault(InputCurrentTooHigh, Cia408FaultState.FaultHold);
        rig.Settle();
        int changes = rig.Changes.Count;

        rig.Device.ReportFault(InputCurrentTooHigh, Cia408FaultState.FaultInit);
        rig.Settle();

        rig.Device.State.Should().Be(Cia408State.FaultHold, "the same fault does not react again");
        rig.Changes.Count.Should().Be(changes);
    }

    [Fact]
    public async Task The_Reset_Edge_Can_Be_Made_Across_The_Two_Control_Words()
    {
        using var rig = new Cia408Rig(Cia408Rig.Valve);
        // 6040h last held 1001b (D2 by level), so only a history shared with 6044h makes the edge.
        await rig.WriteU16Async(ControlWord16, 0, 0x0009);
        rig.Settle();
        rig.Device.ReportFault(InputCurrentTooHigh, Cia408FaultState.FaultDisabled);
        rig.Settle();
        rig.Device.ClearFaultCause(InputCurrentTooHigh);
        rig.Settle();

        await rig.WriteU32Async(ControlWord32, 0, 0x0000_0001);
        await rig.WriteU16Async(ControlWord16, 0, 0x0009);
        rig.Settle();

        rig.Device.State.Should().Be(Cia408State.Disabled, "the two control words are one control word with one history");
    }

    [Fact]
    public async Task A_Changed_604E_Is_Transmitted_Where_A_Tpdo_Maps_It()
    {
        using var rig = new Cia408Rig(Cia408Rig.Valve);
        rig.Node.ConfigureTpdo(2, new CanKit.Pro.CANopen.Pdo.PdoMapping().Add(ErrorCode, 0x00, 16));
        await rig.StartAsync();
        rig.Device.SetWarning(InputCurrentTooHigh, true);
        await rig.WaitForCountAsync(CanOpenProfileRig.Tpdo2, 1);

        rig.Device.SetWarning(SupplyTooLow, true);
        await rig.WaitForCountAsync(CanOpenProfileRig.Tpdo2, 2);
        rig.Device.ReportFault(0x5000, Cia408FaultState.FaultInit);
        await rig.WaitForCountAsync(CanOpenProfileRig.Tpdo2, 3);
        await rig.DrainAsync();

        rig.Payloads(CanOpenProfileRig.Tpdo2).Should().HaveCount(3).And.SatisfyRespectively(
            p => p.Should().Equal(new byte[] { 0x10, 0x21 }),
            p => p.Should().Equal(new byte[] { 0x12, 0x34 }, "the status word did not change, 604Eh did"),
            p => p.Should().Equal(new byte[] { 0x00, 0x50 }));
    }

    [Fact]
    public async Task The_Fault_States_Still_Fall_By_Level_While_A_Cause_Lasts()
    {
        using var rig = new Cia408Rig(Cia408Rig.Valve);
        await rig.WriteU16Async(ControlWord16, 0, 0x0003);
        rig.Settle();
        rig.Device.ReportFault(InputCurrentTooHigh, Cia408FaultState.FaultHold);
        rig.Settle();

        await rig.WriteU16Async(ControlWord16, 0, 0x0001);
        rig.Settle();
        rig.Device.State.Should().Be(Cia408State.FaultDisabled, "D15");
        await rig.WriteU16Async(ControlWord16, 0, 0x0000);
        rig.Settle();
        rig.Device.State.Should().Be(Cia408State.FaultInit, "D16");
    }

    [Fact]
    public async Task A_Fault_That_Moves_No_State_Raises_No_Change_But_Its_Emcy()
    {
        using var rig = new Cia408Rig(Cia408Rig.Valve);
        rig.Device.ReportFault(InputCurrentTooHigh, Cia408FaultState.FaultInit);
        rig.Settle();
        rig.Device.State.Should().Be(Cia408State.FaultInit);
        int changes = rig.Changes.Count;
        await rig.StartAsync();
        await rig.DrainEmcyAsync();
        int emcys = rig.Count(CanOpenProfileRig.Emcy);

        rig.Device.ReportFault(SupplyTooLow, Cia408FaultState.FaultHold);
        await rig.WaitForCountAsync(CanOpenProfileRig.Emcy, emcys + 1);
        rig.Settle();

        rig.Device.State.Should().Be(Cia408State.FaultInit, "Table 9: from FAULT_INIT only FAULT_INIT");
        rig.Changes.Count.Should().Be(changes, "D8 and D13 back into FAULT_INIT change nothing");
        rig.Payloads(CanOpenProfileRig.Emcy)[emcys].Take(2).Should().Equal(new byte[] { 0x12, 0x34 });
        (await rig.ReadU16Async(ErrorCode, 0)).Should().Be(SupplyTooLow);
    }

    [Theory] // Review Focus 3, #559
    [InlineData(null, NmtState.Operational)]
    [InlineData((byte)0x00, NmtState.PreOperational)]
    [InlineData((byte)0x01, NmtState.Operational)]
    [InlineData((byte)0x02, NmtState.Stopped)]
    public async Task A_Fault_From_Operational_Asks_For_What_1029h_02_Names(byte? internalDeviceError, NmtState expected)
    {
        using var rig = new Cia408Rig(Cia408Rig.Valve);
        if (internalDeviceError is { } value) await rig.WriteU8Async(0x1029, 0x02, value);
        await rig.StartAsync();
        await rig.DrainAsync();
        int tpdos = rig.Count(CanOpenProfileRig.Tpdo1);

        rig.Device.ReportFault(InputCurrentTooHigh, Cia408FaultState.FaultInit);
        rig.Settle();
        await rig.DrainAsync();

        rig.Device.State.Should().Be(Cia408State.FaultInit);
        rig.Node.State.Should().Be(expected);
        rig.Count(CanOpenProfileRig.Tpdo1).Should().Be(tpdos + 1,
            "the status goes out in Operational, before the node leaves it");
    }

    [Fact]
    public void ReportFault_Checks_Its_Arguments_On_The_Calling_Thread()
    {
        var unattached = new Cia408Device(Cia408Rig.Valve);
        ((Action)(() => unattached.ReportFault(InputCurrentTooHigh, Cia408FaultState.FaultInit)))
            .Should().Throw<InvalidOperationException>();

        using var rig = new Cia408Rig(Cia408Rig.Valve);
        var device = rig.Device;
        foreach (var code in new ushort[] { 0x0000, 0x0001, 0x0050, 0x00FF })
        {
            ((Action)(() => device.ReportFault(code, Cia408FaultState.FaultInit)))
                .Should().Throw<ArgumentOutOfRangeException>("CiA 301 Table 25: 00xxh is \"error reset or no error\"")
                .Which.ParamName.Should().Be("errorCode");
        }
        ((Action)(() => device.ReportFault(InputCurrentTooHigh, (Cia408FaultState)7)))
            .Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("reaction");
        ((Action)(() => device.ReportFault(InputCurrentTooHigh, Cia408FaultState.FaultInit, 0x20)))
            .Should().Throw<ArgumentOutOfRangeException>("§8.6.3: the profile-specific bit is reserved")
            .Which.ParamName.Should().Be("errorRegisterBits");
        ((Action)(() => device.ReportFault(InputCurrentTooHigh, Cia408FaultState.FaultInit, 0x40)))
            .Should().Throw<ArgumentOutOfRangeException>("CiA 301 Table 54: bit 6 is reserved")
            .Which.ParamName.Should().Be("errorRegisterBits");
        rig.Settle();
        rig.Device.State.Should().Be(Cia408State.Init);
    }

    // ---- warning --------------------------------------------------------------------------------

    [Fact]
    public async Task A_Warning_Sets_Status_Bit_5_And_604E_Without_A_State_Change()
    {
        using var rig = new Cia408Rig(Cia408Rig.Valve);
        rig.Settle();
        int changes = rig.Changes.Count;

        rig.Device.SetWarning(InputCurrentTooHigh, true);
        rig.Settle();
        (await rig.ReadU16Async(StatusWord16, 0)).Should().Be(0x0028, "INIT with W");
        (await rig.ReadU32Async(StatusWord32, 0)).Should().Be(0x0000_0028u);
        (await rig.ReadU16Async(ErrorCode, 0)).Should().Be(InputCurrentTooHigh);

        rig.Device.SetWarning(SupplyTooLow, true);
        rig.Device.SetWarning(InputCurrentTooHigh, false);
        rig.Settle();
        (await rig.ReadU16Async(StatusWord16, 0)).Should().Be(0x0028, "one warning is still active");

        rig.Device.SetWarning(SupplyTooLow, false);
        rig.Settle();
        (await rig.ReadU16Async(StatusWord16, 0)).Should().Be(0x0008);
        (await rig.ReadU16Async(ErrorCode, 0)).Should().Be(SupplyTooLow, "604Eh records what occurred last");
        rig.Changes.Count.Should().Be(changes);
        await rig.DrainEmcyAsync();
        rig.Count(CanOpenProfileRig.Emcy).Should().Be(0, "a warning is no error of CiA 301 and sends no EMCY");

        ((Action)(() => rig.Device.SetWarning(0x0000, true))).Should().Throw<ArgumentOutOfRangeException>();
    }

    // ---- local control --------------------------------------------------------------------------

    [Fact]
    public async Task A_Drive_Powers_On_In_Local_Control_And_Ignores_The_Bus_Control_Word()
    {
        using var rig = new Cia408Rig(Cia408Rig.Drive);
        rig.Settle();

        rig.Od.ReadRaw(Local, 0).Should().Equal(new byte[] { 0x01 }, "§9.3.9: a drive defaults to local control");
        (await rig.ReadU32Async(StatusWord32, 0)).Should().Be(0x0000_0018u, "INIT with L");

        await rig.WriteU32Async(ControlWord32, 0, 0x0000_0007);
        rig.Settle();
        rig.Device.State.Should().Be(Cia408State.Init, "the bus is not the source of the control word");

        rig.Device.SetLocalControlWord(0x0000_0007);
        rig.Settle();
        rig.Device.State.Should().Be(Cia408State.Active);
    }

    [Fact]
    public async Task Device_Local_Switches_The_Source_Of_The_Control_Word_And_Status_Bit_4_Follows()
    {
        using var rig = new Cia408Rig(Cia408Rig.Valve);

        await rig.WriteU8Async(Local, 0, 0x01);
        rig.Settle();
        (await rig.ReadU16Async(StatusWord16, 0)).Should().Be(0x0018);
        await rig.WriteU16Async(ControlWord16, 0, 0x0001);
        rig.Settle();
        rig.Device.State.Should().Be(Cia408State.Init, "ignored in local control");
        rig.Device.SetLocalControlWord(0x0003);
        rig.Settle();
        rig.Device.State.Should().Be(Cia408State.Hold);

        await rig.WriteU8Async(Local, 0, 0x00);
        rig.Settle();
        (await rig.ReadU16Async(StatusWord16, 0)).Should().Be(0x000B);
        rig.Device.SetLocalControlWord(0x0007);
        rig.Settle();
        rig.Device.State.Should().Be(Cia408State.Hold, "the local word is ignored under remote control");
        await rig.WriteU16Async(ControlWord16, 0, 0x0007);
        rig.Settle();
        rig.Device.State.Should().Be(Cia408State.Active);
    }

    [Theory]
    [InlineData((byte)0x02)]
    [InlineData((byte)0x7F)]
    [InlineData((byte)0xFF)] // -1, manufacturer-specific in Table 93, not built
    public async Task Device_Local_Takes_0_And_1_Only(byte value)
    {
        using var rig = new Cia408Rig(Cia408Rig.Valve);

        var response = await rig.DownloadU8Async(Local, 0, value);

        CanOpenProfileRig.AbortCode(response).Should().Be(ValueRangeExceeded);
        rig.Od.ReadRaw(Local, 0).Should().Equal(new byte[] { 0x00 });
    }

    [Fact]
    public void SetLocalControlWord_Refuses_A_Reserved_Bit_On_The_Calling_Thread()
    {
        using var rig = new Cia408Rig(Cia408Rig.Drive);

        foreach (var bit in new[] { 0x10u, 0x20u, 0x1000u, 0x0100_0000u })
        {
            ((Action)(() => rig.Device.SetLocalControlWord(0x0001 | bit)))
                .Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("controlWord");
        }

        rig.Settle();
        rig.Device.State.Should().Be(Cia408State.Init);
    }

    // ---- the event ------------------------------------------------------------------------------

    [Fact]
    public async Task A_Change_Made_From_A_Handler_Is_Raised_After_The_Running_Event_To_Every_Subscriber()
    {
        var device = new Cia408Device(Cia408Rig.Valve);
        var first = new List<string>();
        var second = new List<string>();
        device.StateChanged += (_, e) =>
        {
            first.Add(Describe(e));
            if (e.Current == Cia408State.Active) device.ReportFault(InputCurrentTooHigh, Cia408FaultState.FaultHold);
        };
        device.StateChanged += (_, e) => second.Add(Describe(e));
        using var rig = new Cia408Rig(device);

        await rig.WriteU16Async(ControlWord16, 0, 0x0007);
        rig.Settle();

        var expected = new[] { "NotReady->Init [0,1]", "Init->Active [2,3,4]", "Active->FaultHold [8,9]" };
        first.Should().Equal(expected);
        second.Should().Equal(expected, "each subscriber sees the changes in the order they happened");
    }

    [Fact]
    public async Task A_Handler_Reads_The_Status_Of_The_State_It_Is_Told_Of()
    {
        var device = new Cia408Device(Cia408Rig.Valve);
        Cia408Rig? rigRef = null;
        var seen = new List<string>();
        device.StateChanged += (_, e) =>
        {
            if (rigRef is null) return;
            seen.Add($"{e.Current}:{rigRef.Od.ReadUnsigned(StatusWord16, 0):X4}:{rigRef.Od.ReadUnsigned(StatusWord32, 0):X8}");
        };
        using var rig = new Cia408Rig(device);
        rig.Settle();
        rigRef = rig;

        await rig.WriteU16Async(ControlWord16, 0, 0x0007);
        rig.Settle();
        rig.Device.ReportFault(InputCurrentTooHigh, Cia408FaultState.FaultHold);
        rig.Settle();

        seen.Should().Equal("Active:000F:0000000F", "FaultHold:0003:00000003");
    }

    [Fact]
    public async Task A_Fault_Reported_From_A_Handler_Asks_For_Its_Nmt_State_After_Every_Subscriber_Saw_It()
    {
        var device = new Cia408Device(Cia408Rig.Valve);
        Cia408Rig? rigRef = null;
        var seen = new List<string>();
        device.StateChanged += (_, e) =>
        {
            if (e.Current == Cia408State.Active) device.ReportFault(InputCurrentTooHigh, Cia408FaultState.FaultHold);
        };
        device.StateChanged += (_, e) =>
        {
            if (rigRef is not null) seen.Add($"{e.Current} in {rigRef.Node.State}");
        };
        using var rig = new Cia408Rig(device);
        rig.Settle();
        rigRef = rig;
        await rig.WriteU8Async(0x1029, 0x02, 0x00);
        await rig.StartAsync();

        await rig.SendRpdo1Async(0x07, 0x00);
        await rig.WaitForStateAsync(NmtState.PreOperational);
        rig.Settle();

        seen.Should().Equal("Active in Operational", "FaultHold in Operational");
    }

    [Fact]
    public async Task A_Bus_Word_Written_Before_The_Switch_To_Local_Control_Is_Applied()
    {
        using var rig = new Cia408Rig(Cia408Rig.Valve);
        rig.Settle();

        await rig.WhileActorHeldAsync(() => rig.QueueFramesAsync(
            (CanOpenProfileRig.SdoRequest, CanOpenProfileRig.DownloadU16(ControlWord16, 0, 0x0001)),
            (CanOpenProfileRig.SdoRequest, CanOpenProfileRig.DownloadU8(Local, 0, 0x01))));
        rig.Settle();

        rig.Device.State.Should().Be(Cia408State.Disabled, "the word was written under bus control");
        (await rig.ReadU16Async(StatusWord16, 0)).Should().Be(0x0019, "DISABLED, now with L");
    }

    [Fact]
    public async Task A_Bus_Word_Written_After_The_Switch_To_Local_Control_Is_Ignored()
    {
        using var rig = new Cia408Rig(Cia408Rig.Valve);
        rig.Settle();

        await rig.WhileActorHeldAsync(() => rig.QueueFramesAsync(
            (CanOpenProfileRig.SdoRequest, CanOpenProfileRig.DownloadU8(Local, 0, 0x01)),
            (CanOpenProfileRig.SdoRequest, CanOpenProfileRig.DownloadU16(ControlWord16, 0, 0x0001))));
        rig.Settle();

        rig.Device.State.Should().Be(Cia408State.Init, "the word was written under local control");
        (await rig.ReadU16Async(StatusWord16, 0)).Should().Be(0x0018, "INIT, with L");
    }

    [Fact]
    public async Task A_Bus_Word_Written_Before_The_Switch_To_Bus_Control_Is_Ignored()
    {
        using var rig = new Cia408Rig(Cia408Rig.Drive);
        rig.Settle();

        await rig.WhileActorHeldAsync(() => rig.QueueFramesAsync(
            (CanOpenProfileRig.SdoRequest, CanOpenProfileRig.DownloadU32(ControlWord32, 0, 0x0000_0001)),
            (CanOpenProfileRig.SdoRequest, CanOpenProfileRig.DownloadU8(Local, 0, 0x00))));
        rig.Settle();

        rig.Device.State.Should().Be(Cia408State.Init, "the word was written under local control");
        (await rig.ReadU32Async(StatusWord32, 0)).Should().Be(0x0000_0008u, "INIT, now without L");
    }

    [Fact]
    public async Task An_Ignored_Bus_Word_Is_Not_Taken_As_The_Previous_Word()
    {
        using var rig = new Cia408Rig(Cia408Rig.Drive);
        rig.Device.SetLocalControlWord(0x0009);
        rig.Settle();
        rig.Device.State.Should().Be(Cia408State.Disabled, "D2 by level");
        rig.Device.ReportFault(InputCurrentTooHigh, Cia408FaultState.FaultDisabled);
        rig.Device.ClearFaultCause(InputCurrentTooHigh);
        rig.Settle();

        await rig.WriteU32Async(ControlWord32, 0, 0x0000_0001);
        rig.Settle();
        rig.Device.SetLocalControlWord(0x0009);
        rig.Settle();

        rig.Device.State.Should().Be(Cia408State.FaultDisabled,
            "1001b after the local 1001b is no edge: the bus 0001b in between was ignored");
    }

    [Fact]
    public async Task A_Status_Word_That_Differs_From_The_State_Is_Rewritten_On_The_Next_Word()
    {
        using var rig = new Cia408Rig(Cia408Rig.Valve);
        rig.Settle();
        rig.Od.WriteRawUnchecked(StatusWord16, 0, new byte[] { 0xFF, 0x00 });
        rig.Od.WriteRawUnchecked(StatusWord32, 0, new byte[] { 0xFF, 0x00, 0x00, 0x00 });

        await rig.WriteU16Async(ControlWord16, 0, 0x0000);
        rig.Settle();

        rig.Device.State.Should().Be(Cia408State.Init, "0000b in INIT moves nothing");
        rig.Od.ReadUnsigned(StatusWord16, 0).Should().Be(0x0008u, "compared with the dictionary, not with what was written last");
        rig.Od.ReadUnsigned(StatusWord32, 0).Should().Be(0x0000_0008u);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static (OdDataType, OdAccess, bool) Entry(Cia408Rig rig, ushort index)
    {
        rig.Od.TryGet(index, 0, out var entry).Should().BeTrue($"{index:X4}h is declared");
        return (entry.DataType, entry.Access, entry.PdoMappable);
    }

    private static string Describe(Cia408StateChangedEventArgs e)
        => $"{e.Previous}->{e.Current} [{string.Join(",", e.Transitions)}]";
}
