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
/// The CiA 408 v2.2.0 device's modes (§9.3.4, §9.3.5, Tables 4 and 5), its setpoint and actual
/// value objects (§8.4 Table 13, §8.5 Table 32, §8.7.1), the process model on the node's clock, and
/// the answer to a received RPDO (§8.4.1, issue #558). Indices, values and frames are written out.
/// </summary>
public class Cia408DeviceProcessTests
{
    private const ushort ControlWord16 = 0x6040;
    private const ushort StatusWord16 = 0x6041;
    private const ushort DeviceMode = 0x6042;
    private const ushort ControlMode = 0x6043;
    private const ushort QSetpoint = 0x6300;
    private const ushort SpoolActual = 0x6301;
    private const ushort PressureSetpoint = 0x6380;
    private const ushort PressureActual = 0x6381;

    private const uint ValueRangeExceeded = 0x0609_0030;
    private const uint DeviceState = 0x0800_0022;
    private const uint ReadOnly = 0x0601_0002;

    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(10);

    /// <summary>A 16-bit valve with spool position and pressure control, closed loop, and the device
    /// modes "setpoint via bus" and "setpoint local".</summary>
    private static Cia408Configuration TwoModes => new()
    {
        Resolution = Cia408Resolution.Valve16,
        ControlModes = new[] { Cia408ControlMode.SpoolPositionClosedLoop, Cia408ControlMode.PressureClosedLoop },
        DeviceModes = new[] { Cia408DeviceMode.SetpointViaBus, Cia408DeviceMode.SetpointLocal },
        ProcessInterval = Interval,
    };

    // ---- objects --------------------------------------------------------------------------------

    [Fact]
    public void The_Setpoint_And_Actual_Value_Records_Of_Every_Supported_Control_Mode_Are_Declared()
    {
        using var rig = new Cia408Rig(TwoModes);

        foreach (var index in new[] { QSetpoint, SpoolActual, PressureSetpoint, PressureActual })
        {
            rig.Od.ReadRaw(index, 0).Should().Equal(new byte[] { 0x03 }, $"{index:X4}h is a value record (§8.7.1)");
            rig.Od.TryGet(index, 1, out var value).Should().BeTrue();
            value.DataType.Should().Be(OdDataType.Integer16);
            rig.Od.ReadRaw(index, 2).Should().Equal(new byte[] { 0x00 }, "ir has no CiA 303-2 unit; dimensionless");
            rig.Od.ReadRaw(index, 3).Should().Equal(new byte[] { 0x00 }, "prefix 0");
            rig.Od.TryGet(index, 2, out var unit).Should().BeTrue();
            unit.Access.Should().Be(OdAccess.ReadOnly, "the unit cannot be configured over the bus (§8.3)");
        }

        rig.Od.TryGet(QSetpoint, 1, out var setpoint);
        setpoint.Access.Should().Be(OdAccess.ReadWrite);
        rig.Od.TryGet(SpoolActual, 1, out var actual);
        actual.Access.Should().Be(OdAccess.ReadOnly);
        rig.Od.TryGet(0x6308, 1, out _).Should().BeFalse("the 32-bit objects belong to another width");
        rig.Od.TryGet(0x6700, 1, out _).Should().BeFalse("flow control is not supported");
    }

    [Fact]
    public void A_Flow_Valve_Declares_Its_Records_With_The_Catalogue_Unit_And_Prefix()
    {
        using var rig = new Cia408Rig(new Cia408Configuration
        {
            Resolution = Cia408Resolution.Valve16,
            ControlModes = new[] { Cia408ControlMode.FlowControl },
        });

        rig.Od.ReadRaw(0x6700, 2).Should().Equal(new byte[] { 0xA9 }, "l/min (§8.3.1, Table 11)");
        rig.Od.ReadRaw(0x6700, 3).Should().Equal(new byte[] { 0xFD }, "milli");
        rig.Od.ReadRaw(0x6701, 2).Should().Equal(new byte[] { 0xA9 });
    }

    /// <summary>Every control mode of Table 82 at both widths: the setpoint and actual value records
    /// the device declares — those the mapping tables name (§8.4 Table 13, §8.5 Table 32, §8.4.10 Table
    /// 31) and those the object definitions make mandatory with the mode at that width (§9) — or the
    /// refusal. Written out per cell; "-" for none.</summary>
    [Theory]
    [InlineData(1, false, "6300", "-")]
    [InlineData(1, true, "6308", "-")]
    [InlineData(2, false, "6300", "6301")]
    [InlineData(2, true, "6308", "6309")]
    [InlineData(3, false, "6380", "-")]
    [InlineData(3, true, "6388", "6389")]              // §9.12.6: 6389h mandatory for 32 bit and mode 3 or 4
    [InlineData(4, false, "6380", "6381")]
    [InlineData(4, true, "6388", "6389")]
    [InlineData(5, false, "6300 6380", "6301 6381 640E")] // §9.16.3: 640Eh mandatory for 16 bit and mode 5
    [InlineData(5, true, "refused", "16 bit only")]     // no 32-bit object for pQ
    [InlineData(6, false, "refused", "drive control mode")] // drives have no 16-bit width
    [InlineData(6, true, "6480", "-")]
    [InlineData(7, false, "refused", "drive control mode")]
    [InlineData(7, true, "6500", "6501")]
    [InlineData(8, false, "refused", "drive control mode")]
    [InlineData(8, true, "6580", "6581")]
    [InlineData(9, false, "refused", "drive control mode")]
    [InlineData(9, true, "6600", "6601")]
    [InlineData(10, false, "refused", "drive control mode")]
    [InlineData(10, true, "6600", "6601")]
    [InlineData(11, false, "refused", "drive control mode")]
    [InlineData(11, true, "-", "-")]                    // no object of §9 is mandatory with mode 11
    [InlineData(12, false, "refused", "drive control mode")]
    [InlineData(12, true, "-", "-")]                    // nor with mode 12
    [InlineData(13, false, "6700", "6701")]
    [InlineData(13, true, "6708", "6709")]
    [InlineData(14, false, "6380 6700", "6381 6701")]
    [InlineData(14, true, "6380 6708", "6381 6701 6709")] // §9.21.5/6 and Table 31's 16-bit 6701h and 6381h
    [InlineData(15, false, "6700 6780", "6701 6781")]   // §9.21.2/3 and §9.31.2/3
    [InlineData(15, true, "6708 6780", "6709 6781")]
    public void Each_Control_Mode_Declares_The_Records_The_Norm_Gives_It(int mode, bool wide, string setpoints, string actuals)
    {
        var configuration = new Cia408Configuration
        {
            Resolution = wide ? Cia408Resolution.Valve32Drive32 : Cia408Resolution.Valve16,
            ControlModes = new[] { (Cia408ControlMode)mode },
        };
        if (setpoints == "refused")
        {
            // The refusal names the mode, the resolution and the reason (in the actuals column).
            var refusal = ((Action)(() => configuration.Validate())).Should().ThrowExactly<ArgumentException>().Which;
            refusal.ParamName.Should().Be("ControlModes");
            refusal.Message.Should().Contain(((Cia408ControlMode)mode).ToString())
                .And.Contain(configuration.Resolution.ToString()).And.Contain(actuals);
            return;
        }

        using var rig = new Cia408Rig(configuration);

        Records(rig, OdAccess.ReadWrite).Should().Be(setpoints, "the setpoints");
        Records(rig, OdAccess.ReadOnly).Should().Be(actuals, "the actual values");
    }

    /// <summary>The value records (sub-index 01h, §8.7.1) the device declares in 6100h to 67FFh with
    /// <paramref name="access"/>, as hexadecimal indices; "-" for none.</summary>
    private static string Records(Cia408Rig rig, OdAccess access)
    {
        var indices = rig.Od.SnapshotKeys()
            .Where(key => (key & 0xFF) == 0x01 && key >> 8 is >= 0x6100 and <= 0x67FF)
            .Where(key => rig.Od.TryGet((ushort)(key >> 8), 0x01, out var entry) && entry.Access == access)
            .Select(key => (key >> 8).ToString("X4", System.Globalization.CultureInfo.InvariantCulture))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        return indices.Count == 0 ? "-" : string.Join(" ", indices);
    }

    [Fact]
    public async Task A_32_Bit_PFlow_Valve_Maps_Table_31_And_Its_Model_Sees_The_32_Bit_Flow_Setpoint()
    {
        var model = new FlowProbe();
        using var rig = new Cia408Rig(new Cia408Device(new Cia408Configuration
        {
            Resolution = Cia408Resolution.Valve32Drive32,
            ControlModes = new[] { Cia408ControlMode.PFlowControl },
            ProcessInterval = Interval,
        }, model));

        rig.Od.ReadRaw(0x1A01, 0).Should().Equal(new byte[] { 0x03 });
        rig.Od.ReadUnsigned(0x1A01, 1).Should().Be(0x6045_0020u, "Table 31: 6045h in case of 32-bit");
        rig.Od.ReadUnsigned(0x1A01, 2).Should().Be(0x6701_0110u);
        rig.Od.ReadUnsigned(0x1A01, 3).Should().Be(0x6381_0110u);
        rig.Od.ReadRaw(0x1601, 0).Should().Equal(new byte[] { 0x00 }, "Table 32 has no 32-bit row for pFlow");

        await rig.WriteI32Async(0x6708, 0x01, 123456);
        rig.Advance(Interval);

        model.Flow.Should().Be(123456);
    }

    private sealed class FlowProbe : ICia408Process
    {
        internal long Flow { get; private set; }

        public void Step(Cia408ProcessContext context) => Flow = context.Setpoint(Cia408Objects.VflcSetpoint32);
    }

    // The NMT role (an NMT slave, no virtual device application manager of §8.2) is a documented
    // decision on Cia408Device; the norm defines no device-type bit for it, so no test can tell a
    // device that claims it from one that does not.
    [Fact]
    public async Task A_Device_At_Logical_Device_1_Keeps_Its_Records_At_6B00h_And_Leaves_Logical_Device_0s_Alone()
    {
        // Logical device 1: every index 800h higher (CiA 301 v4.2.0 §7.4.1), 6300h at 6B00h, 6301h at 6B01h.
        static Cia408Configuration Valve(int logicalDevice) => new()
        {
            Resolution = Cia408Resolution.Valve16,
            ControlModes = new[] { Cia408ControlMode.SpoolPositionClosedLoop },
            LogicalDevice = logicalDevice,
            ProcessInterval = Interval,
        };
        var model = new RecordingModel();
        long seen = 0;
        model.OnStep = c =>
        {
            seen = c.Setpoint(Cia408Objects.QSetpoint);
            c.SetActual(Cia408Objects.SpoolPositionActualValue, 55);
        };
        var ld0 = new Cia408Device(Valve(0));
        var ld1 = new Cia408Device(Valve(1), model);
        using var rig = new Cia408Rig(new[] { ld0, ld1 }, description: null);
        var reports = new List<Exception>();
        rig.Node.BackgroundExceptionOccurred += (_, e) => { lock (reports) reports.Add(e); };
        ld0.SetActualValue(Cia408Objects.SpoolPositionActualValue, 11);
        rig.Settle();

        await rig.WriteI16Async(0x6B00, 0x01, 1234);
        rig.Advance(Interval);
        rig.Settle();
        seen.Should().Be(1234, "the model reads 6300h of its own logical device");
        rig.Od.ReadSigned(0x6B01, 0x01).Should().Be(55, "and sets 6301h of it");

        ld1.SetActualValue(Cia408Objects.SpoolPositionActualValue, 77);
        rig.Settle();
        rig.Od.ReadSigned(0x6B01, 0x01).Should().Be(77);
        rig.Od.ReadSigned(0x6301, 0x01).Should().Be(11, "logical device 0's actual value is its own");

        model.OnStep = null;
        await rig.ResetAsync(NmtCommand.ResetNode);
        rig.Settle();
        rig.Od.ReadSigned(0x6B01, 0x01).Should().Be(77, "Reset Node rewrites logical device 1's actual value at 6B01h");
        rig.Od.ReadSigned(0x6301, 0x01).Should().Be(11, "and leaves logical device 0's alone");
        await rig.FlushBackgroundReportsAsync();
        lock (reports) reports.Select(r => r.Message).Should().BeEmpty("no write went to logical device 0's records");
    }

    [Fact]
    public void Both_Default_Pdos_Are_Set()
    {
        using var rig = new Cia408Rig(TwoModes);

        rig.Od.ReadUnsigned(0x1601, 1).Should().Be(0x6040_0010u);
        rig.Od.ReadUnsigned(0x1601, 2).Should().Be(0x6300_0110u, "RPDO 2 maps the q setpoint (Table 32, mapping 2)");
        rig.Od.ReadUnsigned(0x1A01, 1).Should().Be(0x6041_0010u);
        rig.Od.ReadUnsigned(0x1A01, 2).Should().Be(0x6301_0110u, "TPDO 2 maps the spool position (Table 13, mapping 2)");
    }

    // ---- modes ----------------------------------------------------------------------------------

    /// <summary>The device mode is not changeable in ACTIVE (Tables 4 and 5), nor in HOLD and FAULT_HOLD,
    /// where Table 5 leaves it to the manufacturer and refusing is this device's choice; the control mode
    /// only in INIT, DISABLED, FAULT_INIT and FAULT_DISABLED (Table 5). Every other write is
    /// accepted.</summary>
    [Theory]
    [InlineData("Init", true, true)]
    [InlineData("Disabled", true, true)]
    [InlineData("Hold", false, false)]
    [InlineData("Active", false, false)]
    [InlineData("FaultInit", true, true)]
    [InlineData("FaultDisabled", true, true)]
    [InlineData("FaultHold", false, false)]
    public async Task The_Modes_Are_Changeable_Where_Tables_4_And_5_Allow(string state, bool deviceMode, bool controlMode)
    {
        using var rig = new Cia408Rig(TwoModes);
        await ReachAsync(rig, (Cia408State)Enum.Parse(typeof(Cia408State), state));

        var device = await rig.DownloadU8Async(DeviceMode, 0, 0x02);
        var control = await rig.DownloadU8Async(ControlMode, 0, 0x04);

        if (deviceMode) device[0].Should().Be(0x60);
        else CanOpenProfileRig.AbortCode(device).Should().Be(DeviceState);
        if (controlMode) control[0].Should().Be(0x60);
        else CanOpenProfileRig.AbortCode(control).Should().Be(DeviceState);
        rig.Od.ReadRaw(DeviceMode, 0).Should().Equal(new[] { deviceMode ? (byte)0x02 : (byte)0x01 });
        rig.Od.ReadRaw(ControlMode, 0).Should().Equal(new[] { controlMode ? (byte)0x04 : (byte)0x02 });
    }

    [Theory]
    [InlineData(DeviceMode, (byte)0x00)]
    [InlineData(DeviceMode, (byte)0x03)]
    [InlineData(DeviceMode, (byte)0xFF)]
    [InlineData(ControlMode, (byte)0x00)]
    [InlineData(ControlMode, (byte)0x01)]
    [InlineData(ControlMode, (byte)0x10)]
    public async Task A_Mode_The_Configuration_Does_Not_Support_Is_Refused(ushort index, byte value)
    {
        using var rig = new Cia408Rig(TwoModes);

        var response = await rig.DownloadU8Async(index, 0, value);

        CanOpenProfileRig.AbortCode(response).Should().Be(ValueRangeExceeded);
    }

    [Fact]
    public async Task The_Control_Mode_Of_A_Device_With_One_Is_Read_Only()
    {
        using var rig = new Cia408Rig(Cia408Rig.Valve);

        rig.Od.TryGet(ControlMode, 0, out var entry);
        entry.Access.Should().Be(OdAccess.ReadOnly, "§9.3.5: ro if only one control mode is supported");
        CanOpenProfileRig.AbortCode(await rig.DownloadU8Async(ControlMode, 0, 0x02)).Should().Be(ReadOnly);
    }

    // ---- the PDO answer (#558) --------------------------------------------------------------------

    [Fact] // Review Focus 2, #558
    public async Task Every_Received_Rpdo_Is_Answered_By_Its_Tpdo_And_An_Sdo_Write_Is_Not()
    {
        using var rig = new Cia408Rig(Cia408Rig.Valve);
        await rig.StartAsync();
        await rig.SendRpdo1Async(0x01, 0x00);
        await rig.DrainAsync();
        int before = rig.Count(CanOpenProfileRig.Tpdo1);

        await rig.SendRpdo1Async(0x01, 0x00);
        await rig.SendRpdo1Async(0x01, 0x00);
        await rig.DrainAsync();
        rig.Count(CanOpenProfileRig.Tpdo1).Should().Be(before + 2, "two RPDOs, two answers, though nothing changed");

        await rig.WriteU16Async(ControlWord16, 0, 0x0001);
        rig.Settle();
        await rig.DrainAsync();
        rig.Count(CanOpenProfileRig.Tpdo1).Should().Be(before + 2, "an SDO write of the same word changes nothing and is no RPDO");
        rig.Payloads(CanOpenProfileRig.Tpdo1).Skip(before).Should().OnlyContain(p => p.SequenceEqual(new byte[] { 0x09, 0x00 }));
    }

    [Fact] // Review Focus 2: the answer carries the status the RPDO's control word made
    public async Task The_Answer_Carries_The_New_Status()
    {
        using var rig = new Cia408Rig(Cia408Rig.Valve);
        await rig.StartAsync();
        await rig.DrainAsync();
        int before = rig.Count(CanOpenProfileRig.Tpdo1);

        await rig.SendRpdo1Async(0x03, 0x00);
        await rig.DrainAsync();

        rig.Payloads(CanOpenProfileRig.Tpdo1).Skip(before).Should().NotBeEmpty()
            .And.OnlyContain(p => p.SequenceEqual(new byte[] { 0x0B, 0x00 }), "HOLD, also in the answer");
    }

    // ---- the process model ------------------------------------------------------------------------

    [Fact]
    public async Task The_Model_Steps_Once_Per_Interval_With_The_Elapsed_Time()
    {
        var model = new RecordingModel();
        using var rig = new Cia408Rig(new Cia408Device(TwoModes, model));
        rig.Settle();
        int before = model.Steps.Count;

        rig.Advance(Interval);
        rig.Advance(Interval);
        rig.Advance(Interval);

        model.Steps.Skip(before).Select(s => s.Elapsed).Should().Equal(Interval, Interval, Interval);
    }

    [Fact]
    public async Task The_Model_Steps_Once_After_A_State_Change_And_Keeps_Its_Period()
    {
        var model = new RecordingModel();
        using var rig = new Cia408Rig(new Cia408Device(TwoModes, model));
        rig.Settle();
        model.Steps.Select(s => s.State).Should().Equal(new[] { Cia408State.Init }, "one step after the start");

        rig.Advance(TimeSpan.FromMilliseconds(4));
        await rig.WriteU16Async(ControlWord16, 0, 0x0003);
        rig.Settle();
        rig.Advance(TimeSpan.FromMilliseconds(6));

        model.Steps.Select(s => $"{s.State} {s.Elapsed.TotalMilliseconds}").Should().Equal(
            "Init 0", "Hold 4", "Hold 6");
    }

    [Fact]
    public void A_Late_Step_Neither_Drifts_The_Period_Nor_Catches_Up_In_A_Burst()
    {
        var model = new RecordingModel();
        using var rig = new Cia408Rig(new Cia408Device(TwoModes, model));
        rig.Settle();
        int before = model.Steps.Count;

        rig.Advance(TimeSpan.FromMilliseconds(35));
        rig.Advance(TimeSpan.FromMilliseconds(5));

        model.Steps.Skip(before).Select(s => s.Elapsed.TotalMilliseconds).Should().Equal(new[] { 35.0, 5.0 },
            "due at 10, taken at 35 with the passed due times skipped; the next is due at 40, not at 45");
    }

    [Fact]
    public async Task An_Actual_Value_The_Model_Sets_Goes_Out_Once_The_Step_Returns()
    {
        long next = 0;
        var model = new RecordingModel { OnStep = c => c.SetActual(Cia408Objects.SpoolPositionActualValue, Interlocked.Increment(ref next) * 100) };
        using var rig = new Cia408Rig(new Cia408Device(TwoModes, model));
        await rig.StartAsync();
        await rig.DrainAsync();
        int before = rig.Count(CanOpenProfileRig.Tpdo2);

        rig.Advance(Interval);
        await rig.DrainAsync();

        rig.Payloads(CanOpenProfileRig.Tpdo2).Skip(before).Select(p => BitConverter.ToString(p)).Should().Equal(
            new[] { $"08-00-{(byte)(next * 100):X2}-{(byte)((next * 100) >> 8):X2}" },
            "one TPDO 2 with the value of the periodic step");
    }

    [Fact]
    public async Task Reset_Node_With_A_Cause_Steps_The_Model_Once_In_The_Final_State()
    {
        var model = new RecordingModel();
        using var rig = new Cia408Rig(new Cia408Device(TwoModes, model));
        rig.Device.ReportFault(0x2110, Cia408FaultState.FaultHold);
        rig.Settle();
        int before = model.Steps.Count;

        await rig.ResetAsync(NmtCommand.ResetNode);

        model.Steps.Skip(before).Select(s => s.State).Should().Equal(new[] { Cia408State.FaultInit },
            "one step for the callback, after the restart and the fault entered again");
    }

    [Fact]
    public async Task Rpdo2_Is_Answered_By_Tpdo2()
    {
        using var rig = new Cia408Rig(TwoModes);
        await rig.WriteU16Async(ControlWord16, 0, 0x0001);
        await rig.StartAsync();
        await rig.DrainAsync();
        int tpdo1 = rig.Count(CanOpenProfileRig.Tpdo1);
        int tpdo2 = rig.Count(CanOpenProfileRig.Tpdo2);

        await rig.SendRpdo2Async(0x01, 0x00, 0x10, 0x00); // DISABLED again, q setpoint 16: no change
        await rig.DrainAsync();

        rig.Count(CanOpenProfileRig.Tpdo2).Should().Be(tpdo2 + 1, "the answer to RPDO 2 is TPDO 2 (§8.4.1)");
        rig.Count(CanOpenProfileRig.Tpdo1).Should().Be(tpdo1, "nothing changed, and RPDO 1 was not received");
    }

    [Fact]
    public void The_Context_Refuses_Another_Thread()
    {
        Exception? caught = null;
        var model = new RecordingModel
        {
            OnStep = c =>
            {
                var worker = new Thread(() =>
                {
                    try
                    {
                        c.SetActual(Cia408Objects.SpoolPositionActualValue, 1);
                    }
                    catch (Exception e)
                    {
                        caught = e;
                    }
                });
                worker.Start();
                worker.Join();
            },
        };
        using var rig = new Cia408Rig(new Cia408Device(TwoModes, model));
        rig.Settle();

        caught.Should().BeOfType<InvalidOperationException>("the context belongs to the thread of its step");
    }

    [Fact]
    public void The_Context_Is_Valid_During_Its_Step_Only()
    {
        Cia408ProcessContext? kept = null;
        var model = new RecordingModel { OnStep = c => kept = c };
        using var rig = new Cia408Rig(new Cia408Device(TwoModes, model));
        rig.Settle();

        kept.Should().NotBeNull();
        ((Action)(() => _ = kept!.State)).Should().Throw<InvalidOperationException>();
        ((Action)(() => kept!.SetStatusBit(12, true))).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task A_Setpoint_From_Rpdo2_Reaches_The_Model_And_Its_Actual_Value_Goes_Out_In_Tpdo2()
    {
        var model = new RecordingModel
        {
            OnStep = c => c.SetActual(Cia408Objects.SpoolPositionActualValue, c.Setpoint(Cia408Objects.QSetpoint) / 2),
        };
        using var rig = new Cia408Rig(new Cia408Device(TwoModes, model));
        await rig.StartAsync();
        await rig.DrainAsync();

        await rig.SendRpdo2Async(0x07, 0x00, 0xD2, 0x04); // ACTIVE, q setpoint 1234
        rig.Advance(Interval);
        await rig.DrainAsync();

        model.Steps.Last().Setpoint.Should().Be(1234);
        model.Steps.Last().ControlMode.Should().Be(Cia408ControlMode.SpoolPositionClosedLoop);
        rig.Payloads(CanOpenProfileRig.Tpdo2).Last().Should().Equal(new byte[] { 0x0F, 0x00, 0x69, 0x02 },
            "ACTIVE and the actual value 617 the model set");
        (await rig.ReadI16Async(SpoolActual, 1)).Should().Be(617);
    }

    [Fact]
    public async Task The_Model_Sets_Status_Bits_7_To_15_And_None_Of_The_Device()
    {
        var model = new RecordingModel { OnStep = c => c.SetStatusBit(12, true) };
        using var rig = new Cia408Rig(new Cia408Device(TwoModes, model));
        rig.Settle();

        (await rig.ReadU16Async(StatusWord16, 0)).Should().Be(0x1008, "INIT and RT, reached target (Table 76)");

        var refused = new List<int>();
        model.OnStep = c =>
        {
            foreach (var bit in new[] { 0, 3, 4, 5, 6, 24, 31 })
            {
                try
                {
                    c.SetStatusBit(bit, true);
                }
                catch (ArgumentOutOfRangeException)
                {
                    refused.Add(bit);
                }
            }
        };
        rig.Advance(Interval);
        refused.Should().Equal(0, 3, 4, 5, 6, 24, 31);
    }

    [Fact]
    public async Task The_Model_Clears_A_Status_Bit_And_Reads_The_Device_Mode()
    {
        var seen = new List<Cia408DeviceMode>();
        var model = new RecordingModel
        {
            OnStep = c =>
            {
                seen.Add(c.DeviceMode);
                c.SetStatusBit(12, seen.Count == 1);
            },
        };
        using var rig = new Cia408Rig(new Cia408Device(TwoModes, model));
        rig.Settle();
        (await rig.ReadU16Async(StatusWord16, 0)).Should().Be(0x1008, "set in the first step");

        rig.Advance(Interval);

        (await rig.ReadU16Async(StatusWord16, 0)).Should().Be(0x0008, "cleared in the second");
        seen.Should().Equal(Cia408DeviceMode.SetpointViaBus, Cia408DeviceMode.SetpointViaBus);
    }

    [Fact]
    public async Task A_Throwing_Model_Is_Reported_And_Keeps_Stepping()
    {
        int calls = 0;
        var model = new RecordingModel
        {
            OnStep = _ =>
            {
                if (Interlocked.Increment(ref calls) == 2) throw new InvalidOperationException("model");
            },
        };
        using var rig = new Cia408Rig(new Cia408Device(TwoModes, model));
        var reports = new List<Exception>();
        rig.Node.BackgroundExceptionOccurred += (_, e) => { lock (reports) reports.Add(e); };
        rig.Settle();

        rig.Advance(Interval);
        rig.Advance(Interval);
        rig.Advance(Interval);
        await rig.FlushBackgroundReportsAsync();

        calls.Should().Be(4, "the start, then three intervals: the throw stopped no later step");
        lock (reports) reports.Should().ContainSingle().Which.ToString().Should().Contain("model");
        rig.Device.State.Should().Be(Cia408State.Init, "a throwing model is no fault of the device");
    }

    [Fact]
    public async Task The_Model_Keeps_Its_Period_Across_Resets()
    {
        var model = new RecordingModel();
        using var rig = new Cia408Rig(new Cia408Device(TwoModes, model));
        rig.Settle();

        await rig.ResetAsync(NmtCommand.ResetCommunication);
        await rig.ResetAsync(NmtCommand.ResetNode);
        int before = model.Steps.Count;
        rig.Advance(Interval);
        rig.Advance(Interval);

        model.Steps.Count.Should().Be(before + 2, "the deadline is the device's; no reset cancels it");
    }

    [Fact]
    public async Task Without_A_Model_The_Application_Sets_The_Actual_Value()
    {
        using var rig = new Cia408Rig(TwoModes);
        await rig.StartAsync();
        await rig.DrainAsync();
        int before = rig.Count(CanOpenProfileRig.Tpdo2);

        rig.Device.SetActualValue(Cia408Objects.SpoolPositionActualValue, -300);
        await rig.WaitForCountAsync(CanOpenProfileRig.Tpdo2, before + 1);

        rig.Payloads(CanOpenProfileRig.Tpdo2).Last().Should().Equal(new byte[] { 0x08, 0x00, 0xD4, 0xFE });
        ((Action)(() => rig.Device.SetActualValue(Cia408Objects.QSetpoint, 1))).Should().Throw<ArgumentException>(
            "a setpoint is no actual value");
        ((Action)(() => rig.Device.SetActualValue(Cia408Objects.VflcActualValue, 1))).Should().Throw<ArgumentException>(
            "flow control is not supported");
        ((Action)(() => rig.Device.SetActualValue(Cia408Objects.SpoolPositionActualValue, 40000)))
            .Should().Throw<ArgumentOutOfRangeException>("an INTEGER16")
            .Which.Message.Should().Contain("-32768 to 32767", "the message names the range the value missed");
    }

    [Fact] // Review Focus 5 for the actual values: they come from the device, not from what was stored
    public async Task Reset_Node_Rewrites_The_Actual_Values_From_The_Device_And_Keeps_Restored_Setpoints()
    {
        using var rig = new Cia408Rig(TwoModes);
        rig.Device.SetActualValue(Cia408Objects.SpoolPositionActualValue, 77);
        await rig.WriteI16Async(QSetpoint, 1, 100);
        rig.Settle();
        await rig.SaveAsync();
        rig.Device.SetActualValue(Cia408Objects.SpoolPositionActualValue, 55);
        await rig.WriteI16Async(QSetpoint, 1, 200);
        rig.Settle();

        await rig.ResetAsync(NmtCommand.ResetNode);

        (await rig.ReadI16Async(SpoolActual, 1)).Should().Be(55, "the value the device holds, not the stored 77");
        (await rig.ReadI16Async(QSetpoint, 1)).Should().Be(100, "a setpoint is a parameter and takes the stored value");
    }

    [Fact]
    public void SetActualValue_Before_The_Attach_Says_So()
    {
        var device = Cia408Device.FromDescription();

        ((Action)(() => device.SetActualValue(Cia408Objects.SpoolPositionActualValue, 1)))
            .Should().Throw<InvalidOperationException>("no node has attached the device yet");
    }

    [Fact]
    public void ProcessInterval_Must_Be_Positive()
    {
        var zero = () => new Cia408Device(new Cia408Configuration
        {
            Resolution = Cia408Resolution.Valve16,
            ControlModes = new[] { Cia408ControlMode.SpoolPositionClosedLoop },
            ProcessInterval = TimeSpan.Zero,
        });

        zero.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("ProcessInterval");
        new Cia408Configuration().ProcessInterval.Should().Be(TimeSpan.FromMilliseconds(10));
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
                Fault(rig, Cia408FaultState.FaultInit);
                break;
            case Cia408State.FaultDisabled:
                await rig.WriteU16Async(ControlWord16, 0, 0x0001);
                Fault(rig, Cia408FaultState.FaultDisabled);
                break;
            case Cia408State.FaultHold:
                await rig.WriteU16Async(ControlWord16, 0, 0x0003);
                Fault(rig, Cia408FaultState.FaultHold);
                break;
        }

        rig.Settle();
        rig.Device.State.Should().Be(state);
    }

    private static void Fault(Cia408Rig rig, Cia408FaultState reaction)
    {
        rig.Settle();
        rig.Device.ReportFault(0x2110, reaction);
        rig.Device.ClearFaultCause(0x2110);
        rig.Settle();
    }

    private sealed class RecordingModel : ICia408Process
    {
        private readonly object _gate = new();
        private readonly List<StepRecord> _steps = new();

        internal Action<Cia408ProcessContext>? OnStep { get; set; }

        internal IReadOnlyList<StepRecord> Steps
        {
            get
            {
                lock (_gate) return _steps.ToArray();
            }
        }

        public void Step(Cia408ProcessContext context)
        {
            lock (_gate)
            {
                _steps.Add(new StepRecord(context.State, context.Elapsed, context.ControlMode,
                    context.Setpoint(Cia408Objects.QSetpoint)));
            }

            OnStep?.Invoke(context);
        }
    }

    private readonly record struct StepRecord(Cia408State State, TimeSpan Elapsed, Cia408ControlMode ControlMode, long Setpoint);
}
