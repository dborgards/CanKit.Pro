using System;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Cia401;
using CanKit.Pro.CANopen.Nmt;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Cia401;

/// <summary>
/// The CiA 401 device with analog inputs: what it declares, how a value the application sets reaches
/// <c>6401h</c> and TPDO 2, the interrupt conditions of <c>6421h</c> and the limits and deltas they
/// select, the interrupt source <c>6422h</c>, and the warning of §5.3 (CiA 401-1 v3.1.0 §5.3, §6.1,
/// §6.2.8, §7.2.4 Figure 6, §8.4.2, §8.6.2 to §8.6.9). Indices, defaults, abort codes and EMCY bytes
/// are written out from the norm here, not taken from <see cref="Cia401Objects"/>.
/// </summary>
/// <remarks>
/// The readings the tests pin, each argued on the device's <c>SetAnalogInput</c>: the conditions
/// selected in <c>6421h</c> are OR'd within their group (limits, bits 0 and 1; deltas, bits 2 to 4),
/// and the two groups are ANDed, a group with no bit selected counting as met (Figure 6, §8.6.5,
/// §8.6.6); <c>6421h</c> = 0 never interrupts. Under the defaults (07h, limits and delta 0) every
/// change interrupts. A limit is compared with the input left adjusted in 32 bits, the input's
/// 16-bit value times 2^16 (§8.6.5, §8.6.6); a delta is a plain difference of the 16-bit values, a
/// choice where §8.6.7 to §8.6.9 say nothing.
/// </remarks>
public class Cia401DeviceAnalogInputTests
{
    private const uint AttemptWriteReadOnly = 0x06010002;
    private const uint ValueRangeExceeded = 0x06090030;

    private static Cia401Configuration Analog(int channels) => new() { AnalogInputs = channels };

    /// <summary>A 16-bit input value as a limit object holds it: left adjusted in 32 bits.</summary>
    private static int LeftAdjusted(int counts) => counts << 16;

    private static byte[] I16(params short[] values)
        => values.SelectMany(v => new[] { (byte)v, (byte)(v >> 8) }).ToArray();

    /// <summary>Error code <c>0080h</c> (§5.3, §5.4 Table 1), of the class "error reset or no error"
    /// (CiA 301 v4.2.0 §7.2.7.1, Table 25): on a node without an error the register byte is 00h.</summary>
    private static readonly byte[] WarningEmcy = { 0x80, 0x00, 0x00, 0, 0, 0, 0, 0 };

    /// <summary>A device with <paramref name="channels"/> analog inputs whose TPDO 2 the master has
    /// created over SDO (it is disabled by default, §6.2.8 and the scope record).</summary>
    private static async Task<Cia401Rig> WithTpdo2Async(int channels = 1)
    {
        var rig = new Cia401Rig(Analog(channels), Cia401Rig.WritablePdoRecords);
        try
        {
            await rig.EnableTpdoAsync(2);
            return rig;
        }
        catch
        {
            rig.Dispose();
            throw;
        }
    }

    /// <summary>Sets channel 1 and expects exactly one TPDO 2 more, carrying the value.</summary>
    private static async Task ExpectSentAsync(Cia401Rig rig, short value, string because)
    {
        int before = rig.Count(Cia401Rig.Tpdo2);
        rig.Device.SetAnalogInput(1, value);
        await rig.WaitForCountAsync(Cia401Rig.Tpdo2, before + 1);
        await rig.DrainAsync();
        rig.Count(Cia401Rig.Tpdo2).Should().Be(before + 1, because);
        rig.Payloads(Cia401Rig.Tpdo2)[before].Should().Equal(I16(value), because);
    }

    /// <summary>Sets channel 1 and expects no TPDO 2, and the value in <c>6401h</c> all the same.</summary>
    private static async Task ExpectNothingAsync(Cia401Rig rig, short value, string because)
    {
        int before = rig.Count(Cia401Rig.Tpdo2);
        rig.Device.SetAnalogInput(1, value);
        await rig.DrainAsync();
        rig.Count(Cia401Rig.Tpdo2).Should().Be(before, because);
        rig.Device.ReadAnalogInput(1).Should().Be(value, "the value updates without an interrupt");
    }

    // ---- attach --------------------------------------------------------------------------------

    [Fact]
    public void Attach_Declares_The_Analog_Input_Objects_With_Their_Defaults()
    {
        using var rig = new Cia401Rig(Analog(40));
        var od = rig.Od;

        foreach (ushort index in new ushort[] { 0x6401, 0x6421, 0x6424, 0x6425, 0x6426, 0x6427, 0x6428 })
        {
            od.ReadUnsigned(index, 0x00).Should().Be(40u, $"{index:X4}h has one sub-index per channel");
            od.TryGet(index, 0x00, out var count).Should().BeTrue();
            count.Access.Should().Be(OdAccess.ReadOnly, "the highest sub-index supported is const");
            od.TryGet(index, 41, out _).Should().BeFalse($"{index:X4}h has no channel 41");
        }

        od.ReadUnsigned(0x6422, 0x00).Should().Be(2u, "40 channels are two banks of 32 (§8.6.3)");
        od.TryGet(0x6422, 0x03, out _).Should().BeFalse();

        for (byte sub = 1; sub <= 40; sub++)
        {
            od.TryGet(0x6401, sub, out var input).Should().BeTrue();
            input.DataType.Should().Be(OdDataType.Integer16, "6401h is INTEGER16 (§8.4.2)");
            input.Access.Should().Be(OdAccess.ReadOnly);
            input.PdoMappable.Should().BeTrue("6401h maps into TPDO 2 to 4 by default (§8.4.2)");
            od.ReadSigned(0x6401, sub).Should().Be(0);

            od.TryGet(0x6421, sub, out var selection).Should().BeTrue();
            selection.DataType.Should().Be(OdDataType.Unsigned8);
            od.ReadUnsigned(0x6421, sub).Should().Be(0x07u, "upper limit, lower limit and delta by default (§8.6.2)");

            foreach (ushort limit in new ushort[] { 0x6424, 0x6425 })
            {
                od.TryGet(limit, sub, out var entry).Should().BeTrue();
                entry.DataType.Should().Be(OdDataType.Integer32, $"{limit:X4}h is INTEGER32 (§8.6.5, §8.6.6)");
                entry.Access.Should().Be(OdAccess.ReadWrite);
                od.ReadSigned(limit, sub).Should().Be(0);
            }

            foreach (ushort delta in new ushort[] { 0x6426, 0x6427, 0x6428 })
            {
                od.TryGet(delta, sub, out var entry).Should().BeTrue();
                entry.DataType.Should().Be(OdDataType.Unsigned32, $"{delta:X4}h is UNSIGNED32 (§8.6.7 to §8.6.9)");
                entry.Access.Should().Be(OdAccess.ReadWrite);
                od.ReadUnsigned(delta, sub).Should().Be(0u);
            }
        }

        for (byte bank = 1; bank <= 2; bank++)
        {
            od.TryGet(0x6422, bank, out var source).Should().BeTrue();
            source.DataType.Should().Be(OdDataType.Unsigned32);
            source.Access.Should().Be(OdAccess.ReadOnly, "6422h is ro (§8.6.3)");
            od.ReadUnsigned(0x6422, bank).Should().Be(0u);
        }

        od.TryGet(0x6423, 0x00, out var global).Should().BeTrue();
        global.DataType.Should().Be(OdDataType.Boolean, "6423h is a BOOLEAN variable (§8.6.4)");
        global.GetRawValue().Should().Equal(new byte[] { 0 }, "FALSE by default (§8.6.4, §6.2.8)");
        global.Access.Should().Be(OdAccess.ReadWrite);
    }

    [Fact]
    public void Attach_Sets_The_Device_Type_And_The_Disabled_Analog_Tpdos()
    {
        using var rig = new Cia401Rig(Analog(12));

        rig.Od.ReadUnsigned(0x1000, 0x00).Should().Be(0x00040191u, "profile 401 with analog inputs, bit 18 (§6.2.1)");
        for (int pdo = 2; pdo <= 4; pdo++)
        {
            var communication = (ushort)(0x1800 + pdo - 1);
            var mapping = (ushort)(0x1A00 + pdo - 1);
            rig.Od.ReadUnsigned(communication, 0x01).Should().Be(0x8000_0000u + (uint)(0x80 + (pdo * 0x100)) + Cia401Rig.DeviceId,
                $"TPDO {pdo} on its pre-defined COB-ID and not valid (§6.2.8 and the scope record)");
            rig.Od.ReadUnsigned(mapping, 0x00).Should().Be(4u, "four 16-bit channels per PDO (§6.2.8)");
            for (byte e = 1; e <= 4; e++)
            {
                uint channel = (uint)(((pdo - 2) * 4) + e);
                rig.Od.ReadUnsigned(mapping, e).Should().Be(0x6401_0010u | (channel << 8));
            }
        }
    }

    [Fact] // entering Operational asks for every entry the device owns; a TPDO that is not valid stays silent
    public async Task Entering_Operational_With_6423h_False_Sends_No_Disabled_Analog_Tpdo()
    {
        using var rig = new Cia401Rig(new Cia401Configuration { DigitalInputs = 8, AnalogInputs = 12 });
        rig.Device.SetAnalogInput(1, 77);
        (await rig.ReadU8Async(0x6423, 0x00)).Should().Be(0x00, "FALSE by default (§8.6.4)");

        await rig.StartAsync();
        await rig.WaitForCountAsync(Cia401Rig.Tpdo1, 1);
        await rig.DrainAsync();

        rig.Count(Cia401Rig.Tpdo1).Should().Be(1, "TPDO 1, the digital inputs, is valid by default (§6.2.6)");
        for (int pdo = 2; pdo <= 4; pdo++)
        {
            rig.Count((uint)(0x80 + (pdo * 0x100)) + Cia401Rig.DeviceId).Should().Be(0,
                $"TPDO {pdo} is disabled by default (§6.2.8 and the scope record)");
        }
    }

    // ---- §5.3, the analog input disable warning ------------------------------------------------------

    [Fact]
    public async Task Entering_Operational_With_6423h_False_Sends_The_Warning_And_Stays_Operational()
    {
        using var rig = new Cia401Rig(Analog(1));

        await rig.StartAsync();
        await rig.WaitForCountAsync(Cia401Rig.Emcy, 1);
        await rig.DrainEmcyAsync();

        // Error code 0080h (§5.4 Table 1) is a warning, not an error (CiA 301 v4.2.0 Table 25,
        // 00xxh): a healthy node keeps a healthy register, in the frame and in 1001h (#556).
        rig.Payloads(Cia401Rig.Emcy).Should().ContainSingle().Which.Should().Equal(WarningEmcy);
        rig.Od.ReadUnsigned(0x1001, 0x00).Should().Be(0x00u, "the warning is no error: 1001h shows none");
        rig.Node.State.Should().Be(NmtState.Operational, "the warning causes no NMT transition (§5.3)");
    }

    [Fact]
    public async Task Writing_6423h_True_After_The_Warning_Sends_Nothing()
    {
        using var rig = new Cia401Rig(Analog(1));
        await rig.StartAsync();
        await rig.WaitForCountAsync(Cia401Rig.Emcy, 1);

        await rig.WriteU8Async(0x6423, 0x00, 1);
        await rig.DrainEmcyAsync();

        rig.Count(Cia401Rig.Emcy).Should().Be(1, "the warning was an event, not an error: there is nothing to reset");
        rig.Od.ReadUnsigned(0x1001, 0x00).Should().Be(0x00u);
        rig.Node.State.Should().Be(NmtState.Operational);
    }

    [Fact]
    public async Task Writing_6423h_False_In_Operational_Raises_No_Warning()
    {
        using var rig = new Cia401Rig(Analog(1));
        await rig.WriteU8Async(0x6423, 0x00, 1);
        await rig.StartAsync();
        await rig.DrainEmcyAsync();
        rig.Count(Cia401Rig.Emcy).Should().Be(0, "6423h is TRUE when the node enters Operational");

        await rig.WriteU8Async(0x6423, 0x00, 0);
        await rig.DrainEmcyAsync();

        rig.Count(Cia401Rig.Emcy).Should().Be(0, "§5.3 ties the warning to the transition into Operational");
    }

    [Fact]
    public async Task Leaving_Operational_Sends_Nothing_And_The_Next_Entry_Sends_The_Warning_Again()
    {
        using var rig = new Cia401Rig(Analog(1));
        await rig.StartAsync();
        await rig.WaitForCountAsync(Cia401Rig.Emcy, 1);

        await rig.EnterPreOperationalAsync();
        await rig.DrainEmcyAsync();
        rig.Count(Cia401Rig.Emcy).Should().Be(1, "the warning left no error for leaving Operational to reset");

        await rig.StartAsync();
        await rig.WaitForCountAsync(Cia401Rig.Emcy, 2);
        await rig.DrainEmcyAsync();

        // §5.3: on each transition into Operational with 6423h FALSE.
        rig.Payloads(Cia401Rig.Emcy).Should().HaveCount(2);
        rig.Payloads(Cia401Rig.Emcy).Should().AllSatisfy(frame => frame.Should().Equal(WarningEmcy));
        rig.Od.ReadUnsigned(0x1001, 0x00).Should().Be(0x00u);
    }

    [Fact]
    public async Task Through_Stopped_Nothing_Is_Held_And_The_Next_Entry_Sends_The_Warning_Again()
    {
        using var rig = new Cia401Rig(Analog(1));
        await rig.StartAsync();
        await rig.WaitForCountAsync(Cia401Rig.Emcy, 1);

        await rig.StopAsync();
        await rig.DrainEmcyAsync();
        rig.Count(Cia401Rig.Emcy).Should().Be(1);

        await rig.StartAsync();
        await rig.WaitForCountAsync(Cia401Rig.Emcy, 2);
        await rig.DrainEmcyAsync();

        rig.Payloads(Cia401Rig.Emcy).Should().HaveCount(2, "no error reset was held in Stopped: only the next warning");
        rig.Payloads(Cia401Rig.Emcy)[1].Should().Equal(WarningEmcy);
        rig.Od.ReadUnsigned(0x1001, 0x00).Should().Be(0x00u);
    }

    [Fact]
    public async Task After_Reset_Communication_The_Next_Entry_Sends_The_Warning_Again()
    {
        using var rig = new Cia401Rig(Analog(1));
        await rig.StartAsync();
        await rig.WaitForCountAsync(Cia401Rig.Emcy, 1);

        await rig.ResetAsync(NmtCommand.ResetCommunication);
        await rig.DrainEmcyAsync();

        rig.Count(Cia401Rig.Emcy).Should().Be(1, "a reset sends no EMCY of the device");
        rig.Node.State.Should().Be(NmtState.PreOperational);
        (await rig.ReadU8Async(0x6423, 0x00)).Should().Be(0, "6423h is still FALSE");

        await rig.StartAsync();
        await rig.WaitForCountAsync(Cia401Rig.Emcy, 2);
        await rig.DrainEmcyAsync();
        rig.Payloads(Cia401Rig.Emcy).Should().HaveCount(2);
        rig.Payloads(Cia401Rig.Emcy)[1].Should().Equal(WarningEmcy, "entering Operational with 6423h FALSE again");
    }

    [Fact]
    public async Task A_Write_Of_2_To_6423h_Is_Refused_With_The_Value_Range_Abort()
    {
        using var rig = new Cia401Rig(Analog(1));

        Cia401Rig.AbortCode(await rig.DownloadU8Async(0x6423, 0x00, 0x02)).Should().Be(ValueRangeExceeded,
            "a BOOLEAN is TRUE or FALSE (CiA 301 v4.2.0 §7.1.4.3)");
        (await rig.ReadU8Async(0x6423, 0x00)).Should().Be(0);
    }

    [Fact]
    public async Task After_Reset_Node_The_Next_Entry_Sends_The_Warning_Again()
    {
        using var rig = new Cia401Rig(Analog(1));
        await rig.StartAsync();
        await rig.WaitForCountAsync(Cia401Rig.Emcy, 1);

        await rig.ResetAsync(NmtCommand.ResetNode);
        await rig.DrainEmcyAsync();

        rig.Count(Cia401Rig.Emcy).Should().Be(1, "a reset sends no EMCY of the device");
        rig.Od.ReadUnsigned(0x1001, 0x00).Should().Be(0x00u);

        await rig.StartAsync();
        await rig.WaitForCountAsync(Cia401Rig.Emcy, 2);
        await rig.DrainEmcyAsync();
        rig.Payloads(Cia401Rig.Emcy)[1].Should().Equal(WarningEmcy);
    }

    // ---- values, 6423h, entering Operational ----------------------------------------------------------

    [Fact]
    public async Task With_6423h_False_The_Value_Updates_And_Nothing_Is_Sent()
    {
        using var rig = await WithTpdo2Async();
        await rig.StartAndTakeFirstTpdoAsync(Cia401Rig.Tpdo2);

        await ExpectNothingAsync(rig, 1200, "6423h is FALSE by default: no analog input interrupts (§8.6.4)");

        (await rig.ReadI16Async(0x6401, 0x01)).Should().Be(1200);
        rig.Payloads(Cia401Rig.Tpdo2).Should().ContainSingle("only entering Operational sent TPDO 2");
    }

    [Fact]
    public async Task Entering_Operational_Sends_Tpdo_2_Once_With_The_Current_Values_Whatever_6423h_Says()
    {
        using var rig = await WithTpdo2Async(4);
        rig.Device.SetAnalogInput(1, 100);
        rig.Device.SetAnalogInput(2, -2);
        rig.Device.SetAnalogInput(3, 300);
        rig.Device.SetAnalogInput(4, 32767);
        rig.Settle();

        await rig.StartAsync();
        await rig.WaitForCountAsync(Cia401Rig.Tpdo2, 1);
        await rig.DrainAsync();

        // §6.1: every TPDO of transmission type 255 goes out on entering Operational; 6423h governs
        // interrupts only (§8.6.4).
        rig.Payloads(Cia401Rig.Tpdo2).Should().ContainSingle("one frame per PDO, not one per channel")
            .Which.Should().Equal(I16(100, -2, 300, 32767));
    }

    [Fact]
    public async Task Entering_Operational_With_6423h_True_Sends_Tpdo_2_And_No_Warning()
    {
        using var rig = await WithTpdo2Async(4);
        await rig.WriteU8Async(0x6423, 0x00, 1);
        rig.Device.SetAnalogInputs(1, new short[] { 1, 2, 3, 4 });
        rig.Settle();

        await rig.StartAsync();
        await rig.WaitForCountAsync(Cia401Rig.Tpdo2, 1);
        await rig.DrainEmcyAsync();

        rig.Payloads(Cia401Rig.Tpdo2).Should().ContainSingle().Which.Should().Equal(I16(1, 2, 3, 4));
        rig.Count(Cia401Rig.Emcy).Should().Be(0);
    }

    [Fact]
    public async Task Setting_The_Value_It_Has_Sends_Nothing()
    {
        using var rig = await WithTpdo2Async();
        // The upper limit alone (at 0): no delta to turn a zero change down, so only the device's
        // own "no change" check stands between a repeated value and an interrupt.
        await rig.WriteU8Async(0x6421, 0x01, 0x01);
        await rig.WriteU8Async(0x6423, 0x00, 1);
        await rig.StartAndTakeFirstTpdoAsync(Cia401Rig.Tpdo2);

        await ExpectSentAsync(rig, 1200, "1200 is above the upper limit 0");
        await ExpectNothingAsync(rig, 1200, "no change, no interrupt (§8.6.5: every change of the analog input data)");
    }

    // ---- the limits, §8.6.5 and §8.6.6 ---------------------------------------------------------------

    [Fact]
    public async Task The_Upper_Limit_Interrupts_At_And_Above_The_Left_Adjusted_Limit()
    {
        using var rig = await WithTpdo2Async();
        await rig.WriteU8Async(0x6421, 0x01, 0x01);
        await rig.WriteI32Async(0x6424, 0x01, LeftAdjusted(1000));
        await rig.WriteU8Async(0x6423, 0x00, 1);
        await rig.StartAndTakeFirstTpdoAsync(Cia401Rig.Tpdo2);

        await ExpectNothingAsync(rig, 900, "900 is below the upper limit and only bit 0 is selected");
        await ExpectSentAsync(rig, 1000, "equal to the limit triggers (§8.6.5: equal to, or rises above)");
        await ExpectSentAsync(rig, 1200, "above the limit");
        await ExpectNothingAsync(rig, 999, "below the limit again");
    }

    [Fact]
    public async Task Above_The_Upper_Limit_Every_Change_Interrupts()
    {
        using var rig = await WithTpdo2Async();
        await rig.WriteU8Async(0x6421, 0x01, 0x01);
        await rig.WriteI32Async(0x6424, 0x01, LeftAdjusted(1000));
        await rig.WriteU8Async(0x6423, 0x00, 1);
        await rig.StartAndTakeFirstTpdoAsync(Cia401Rig.Tpdo2);

        // §8.6.5: "As long as the trigger condition is met, every change of the analog input data
        // generates a new interrupt."
        await ExpectSentAsync(rig, 1200, "above the limit");
        await ExpectSentAsync(rig, 1300, "still above it");
        await ExpectSentAsync(rig, 1301, "still above it, by one count");
    }

    [Fact]
    public async Task The_Lower_Limit_Interrupts_Below_The_Left_Adjusted_Limit_Only()
    {
        using var rig = await WithTpdo2Async();
        await rig.WriteU8Async(0x6421, 0x01, 0x02);
        await rig.WriteI32Async(0x6425, 0x01, LeftAdjusted(500));
        await rig.WriteU8Async(0x6423, 0x00, 1);
        rig.Device.SetAnalogInput(1, 700);
        await rig.StartAndTakeFirstTpdoAsync(Cia401Rig.Tpdo2);

        await ExpectNothingAsync(rig, 600, "above the lower limit");
        await ExpectNothingAsync(rig, 500, "equal to the lower limit is not below it (§8.6.6: falls below)");
        await ExpectSentAsync(rig, 499, "below the limit");
        await ExpectSentAsync(rig, 400, "every change below it interrupts (§8.6.6)");
    }

    [Fact]
    public async Task A_Negative_Value_Below_A_Negative_Lower_Limit_Interrupts()
    {
        using var rig = await WithTpdo2Async();
        await rig.WriteU8Async(0x6421, 0x01, 0x02);
        await rig.WriteI32Async(0x6425, 0x01, LeftAdjusted(-400));
        await rig.WriteU8Async(0x6423, 0x00, 1);
        await rig.StartAndTakeFirstTpdoAsync(Cia401Rig.Tpdo2);

        await ExpectNothingAsync(rig, -300, "-300 is above -400");
        await ExpectSentAsync(rig, -500, "-500 is below -400; the frame carries it in two's complement");
    }

    [Fact]
    public async Task The_Low_16_Bits_Of_A_Limit_Take_Part_In_The_Comparison()
    {
        using var rig = await WithTpdo2Async();
        await rig.WriteU8Async(0x6421, 0x01, 0x01);
        await rig.WriteI32Async(0x6424, 0x01, LeftAdjusted(1000) | 1);
        await rig.WriteU8Async(0x6423, 0x00, 1);
        await rig.StartAndTakeFirstTpdoAsync(Cia401Rig.Tpdo2);

        // The input left adjusted in 32 bits has zeros in its low 16 bits (§8.4.3), so 1000 is
        // 1 below the limit 03E80001h, and the first value at or above it is 1001.
        await ExpectNothingAsync(rig, 1000, "1000 · 2^16 is below 1000 · 2^16 + 1");
        await ExpectSentAsync(rig, 1001, "1001 · 2^16 is above it");
    }

    [Fact]
    public async Task The_Low_16_Bits_Of_A_Lower_Limit_Take_Part_In_The_Comparison()
    {
        using var rig = await WithTpdo2Async();
        await rig.WriteU8Async(0x6421, 0x01, 0x02);
        await rig.WriteI32Async(0x6425, 0x01, LeftAdjusted(500) | 1);
        await rig.WriteU8Async(0x6423, 0x00, 1);
        rig.Device.SetAnalogInput(1, 700);
        await rig.StartAndTakeFirstTpdoAsync(Cia401Rig.Tpdo2);

        await ExpectNothingAsync(rig, 501, "501 · 2^16 is not below 500 · 2^16 + 1");
        await ExpectSentAsync(rig, 500, "500 · 2^16 is below 500 · 2^16 + 1");
    }

    [Fact]
    public async Task A_Limit_Written_As_A_Plain_Count_Is_A_Fraction_Of_One_Step_Of_The_Input()
    {
        using var rig = await WithTpdo2Async();
        await rig.WriteU8Async(0x6421, 0x01, 0x01);
        await rig.WriteI32Async(0x6424, 0x01, 1000);
        await rig.WriteU8Async(0x6423, 0x00, 1);
        await rig.StartAndTakeFirstTpdoAsync(Cia401Rig.Tpdo2);

        await ExpectNothingAsync(rig, -1, "-1 · 2^16 is below 1000");
        await ExpectSentAsync(rig, 1, "1 · 2^16 = 65536 is above 1000: the limit is left adjusted, not in counts");
    }

    [Theory]
    [InlineData((short)1000, 0x03E8_0000)]
    [InlineData((short)-1, unchecked((int)0xFFFF_0000))]
    [InlineData((short)-400, unchecked((int)0xFE70_0000))]
    [InlineData(short.MaxValue, 0x7FFF_0000)]
    [InlineData(short.MinValue, int.MinValue)]
    public void Left_Adjustment_Widens_To_The_Upper_Word_And_Narrows_Back(short value, int widened)
    {
        Cia401LeftAdjusted.Widen(value).Should().Be(widened);
        Cia401LeftAdjusted.Narrow(widened).Should().Be(value);
        Cia401LeftAdjusted.Narrow(widened | 0xFFFF).Should().Be(value, "narrowing drops the low 16 bits");
    }

    // ---- the deltas, §8.6.7 to §8.6.9 ------------------------------------------------------------------

    [Fact]
    public async Task The_Delta_Is_Measured_From_The_Last_Communicated_Value()
    {
        using var rig = await WithTpdo2Async();
        await rig.WriteU8Async(0x6421, 0x01, 0x04);
        await rig.WriteU32Async(0x6426, 0x01, 50);
        await rig.WriteU8Async(0x6423, 0x00, 1);
        rig.Device.SetAnalogInput(1, 1200);
        await rig.StartAndTakeFirstTpdoAsync(Cia401Rig.Tpdo2); // communicates 1200

        await ExpectNothingAsync(rig, 1230, "30 from 1200 is not more than 50");
        await ExpectNothingAsync(rig, 1250, "exactly 50 from 1200 is not more than 50 (Table 148: by more than delta)");
        await ExpectSentAsync(rig, 1260, "60 from 1200, the last value communicated, though only 10 from 1250");
        await ExpectNothingAsync(rig, 1220, "40 below 1260");
        await ExpectSentAsync(rig, 1200, "60 below 1260: the delta works both ways (§8.6.7)");
    }

    [Fact]
    public async Task The_Negative_Delta_Interrupts_On_A_Fall_Only()
    {
        using var rig = await WithTpdo2Async();
        await rig.WriteU8Async(0x6421, 0x01, 0x08);
        await rig.WriteU32Async(0x6427, 0x01, 50);
        await rig.WriteU8Async(0x6423, 0x00, 1);
        rig.Device.SetAnalogInput(1, 1200);
        await rig.StartAndTakeFirstTpdoAsync(Cia401Rig.Tpdo2);

        await ExpectNothingAsync(rig, 1260, "a rise of 60 is no fall (§8.6.8)");
        await ExpectNothingAsync(rig, 1170, "30 below 1200");
        await ExpectNothingAsync(rig, 1150, "exactly 50 below 1200 is not more than 50");
        await ExpectSentAsync(rig, 1140, "60 below 1200");
        await ExpectNothingAsync(rig, 1100, "40 below 1140, the new last communicated value");
    }

    [Fact]
    public async Task The_Positive_Delta_Interrupts_On_A_Rise_Only()
    {
        using var rig = await WithTpdo2Async();
        await rig.WriteU8Async(0x6421, 0x01, 0x10);
        await rig.WriteU32Async(0x6428, 0x01, 50);
        await rig.WriteU8Async(0x6423, 0x00, 1);
        rig.Device.SetAnalogInput(1, 1200);
        await rig.StartAndTakeFirstTpdoAsync(Cia401Rig.Tpdo2);

        await ExpectNothingAsync(rig, 1140, "a fall of 60 is no rise (§8.6.9)");
        await ExpectNothingAsync(rig, 1230, "30 above 1200");
        await ExpectNothingAsync(rig, 1250, "exactly 50 above 1200 is not more than 50");
        await ExpectSentAsync(rig, 1260, "60 above 1200");
        await ExpectNothingAsync(rig, 1300, "40 above 1260, the new last communicated value");
    }

    [Fact]
    public async Task Entering_Operational_Takes_The_Current_Value_As_The_Last_Communicated()
    {
        using var rig = await WithTpdo2Async();
        await rig.WriteU8Async(0x6421, 0x01, 0x00);
        await rig.WriteU8Async(0x6423, 0x00, 1); // the reference is 0 from here
        rig.Device.SetAnalogInput(1, 1200);      // nothing selected: no interrupt, the reference stays 0
        await rig.WriteU8Async(0x6421, 0x01, 0x04);
        await rig.WriteU32Async(0x6426, 0x01, 50);

        await rig.StartAndTakeFirstTpdoAsync(Cia401Rig.Tpdo2); // communicates 1200

        await ExpectNothingAsync(rig, 1230, "30 from 1200, the value at entering Operational; not 1230 from 0");
        await ExpectSentAsync(rig, 1260, "60 from 1200");
    }

    [Fact]
    public async Task Writing_6423h_True_Takes_The_Current_Value_As_The_Last_Communicated()
    {
        using var rig = await WithTpdo2Async();
        await rig.WriteU8Async(0x6421, 0x01, 0x04);
        await rig.WriteU32Async(0x6426, 0x01, 50);
        await rig.StartAndTakeFirstTpdoAsync(Cia401Rig.Tpdo2); // communicates 0

        await ExpectNothingAsync(rig, 1300, "6423h is FALSE");
        await rig.WriteU8Async(0x6423, 0x00, 1);

        await ExpectNothingAsync(rig, 1340, "40 from 1300, the value when interrupts were enabled; not 1340 from 0");
        await ExpectSentAsync(rig, 1360, "60 from 1300");
    }

    [Fact]
    public async Task Reset_Node_Restores_6423h_And_The_Next_Enable_Takes_The_Current_Value()
    {
        using var rig = await WithTpdo2Async();
        await rig.WriteU8Async(0x6423, 0x00, 1);
        await rig.ResetAsync(NmtCommand.ResetNode);
        (await rig.ReadU8Async(0x6423, 0x00)).Should().Be(0, "Reset Node restores the default FALSE");

        await rig.EnableTpdoAsync(2);
        await rig.WriteU8Async(0x6421, 0x01, 0x04);
        await rig.WriteU32Async(0x6426, 0x01, 50);
        await rig.StartAndTakeFirstTpdoAsync(Cia401Rig.Tpdo2); // communicates 0
        await ExpectNothingAsync(rig, 1300, "6423h is FALSE after the reset");
        await rig.WriteU8Async(0x6423, 0x00, 1);

        await ExpectNothingAsync(rig, 1340, "the write is FALSE to TRUE, so the reference is 1300");
    }

    // ---- 6421h, the selection ---------------------------------------------------------------------------

    [Fact]
    public async Task Either_Selected_Limit_Interrupts()
    {
        using var rig = await WithTpdo2Async();
        await rig.WriteU8Async(0x6421, 0x01, 0x03);
        await rig.WriteI32Async(0x6424, 0x01, LeftAdjusted(1000));
        await rig.WriteI32Async(0x6425, 0x01, LeftAdjusted(500));
        await rig.WriteU8Async(0x6423, 0x00, 1);
        rig.Device.SetAnalogInput(1, 700);
        await rig.StartAndTakeFirstTpdoAsync(Cia401Rig.Tpdo2);

        await ExpectNothingAsync(rig, 800, "between the limits");
        await ExpectSentAsync(rig, 1200, "the upper limit holds");
        await ExpectNothingAsync(rig, 800, "between the limits");
        await ExpectSentAsync(rig, 400, "the lower limit holds");
    }

    [Fact] // C2
    public async Task With_A_Limit_And_A_Delta_Selected_A_Change_Above_The_Limit_Needs_The_Delta()
    {
        using var rig = await WithTpdo2Async();
        await rig.WriteU8Async(0x6421, 0x01, 0x05);
        await rig.WriteI32Async(0x6424, 0x01, LeftAdjusted(1000));
        await rig.WriteU32Async(0x6426, 0x01, 50);
        await rig.WriteU8Async(0x6423, 0x00, 1);
        rig.Device.SetAnalogInput(1, 1200);
        await rig.StartAndTakeFirstTpdoAsync(Cia401Rig.Tpdo2); // communicates 1200

        // §8.6.5: every change above the limit interrupts "if there is no additional trigger
        // condition, e.g. an input interrupt delta"; Figure 6 ANDs the limits with the delta.
        await ExpectNothingAsync(rig, 1230, "above the limit, but 30 is not more than the delta");
        await ExpectSentAsync(rig, 1260, "above the limit and 60 from 1200");
    }

    [Fact] // C3
    public async Task With_A_Limit_And_A_Delta_Selected_A_Large_Change_Below_The_Limit_Does_Not_Interrupt()
    {
        using var rig = await WithTpdo2Async();
        await rig.WriteU8Async(0x6421, 0x01, 0x05);
        await rig.WriteI32Async(0x6424, 0x01, LeftAdjusted(1000));
        await rig.WriteU32Async(0x6426, 0x01, 50);
        await rig.WriteU8Async(0x6423, 0x00, 1);
        await rig.StartAndTakeFirstTpdoAsync(Cia401Rig.Tpdo2); // communicates 0

        await ExpectNothingAsync(rig, 100, "100 from 0 is more than the delta, but below the limit");
        await ExpectNothingAsync(rig, 900, "900 from 0 likewise");
        await ExpectSentAsync(rig, 1200, "above the limit and 1200 from 0");
    }

    [Fact] // C5
    public async Task Under_The_Factory_Settings_Every_Change_Interrupts()
    {
        using var rig = await WithTpdo2Async();
        await rig.WriteU8Async(0x6423, 0x00, 1);
        await rig.StartAndTakeFirstTpdoAsync(Cia401Rig.Tpdo2);

        // 6421h = 07h, limits 0: every value is at or above 0 or below it; delta 0: every change
        // is more than 0.
        await ExpectSentAsync(rig, 1, "a change of one count");
        await ExpectSentAsync(rig, -1, "a change into the negative range");
        await ExpectSentAsync(rig, 0, "and back");
        await ExpectSentAsync(rig, 30000, "a large step");
    }

    [Fact]
    public async Task At_The_Default_Selection_A_Delta_Alone_Throttles_The_Interrupts()
    {
        using var rig = await WithTpdo2Async();
        await rig.WriteU32Async(0x6426, 0x01, 50); // 6421h stays 07h, the limits stay 0
        await rig.WriteU8Async(0x6423, 0x00, 1);
        rig.Device.SetAnalogInput(1, 1200);
        await rig.StartAndTakeFirstTpdoAsync(Cia401Rig.Tpdo2); // communicates 1200

        await ExpectNothingAsync(rig, 1230, "the limits hold for every value, but 30 is not more than 50");
        await ExpectSentAsync(rig, 1260, "60 from 1200");
    }

    [Fact]
    public async Task Negative_And_Positive_Delta_Selected_Together_Interrupt_Each_In_Its_Direction()
    {
        using var rig = await WithTpdo2Async();
        await rig.WriteU8Async(0x6421, 0x01, 0x18);
        await rig.WriteU32Async(0x6427, 0x01, 50);
        await rig.WriteU32Async(0x6428, 0x01, 50);
        await rig.WriteU8Async(0x6423, 0x00, 1);
        rig.Device.SetAnalogInput(1, 1200);
        await rig.StartAndTakeFirstTpdoAsync(Cia401Rig.Tpdo2); // communicates 1200

        await ExpectNothingAsync(rig, 1230, "30 above 1200");
        await ExpectSentAsync(rig, 1260, "60 above 1200: the positive delta");
        await ExpectNothingAsync(rig, 1220, "40 below 1260");
        await ExpectSentAsync(rig, 1200, "60 below 1260: the negative delta");
    }

    [Fact]
    public async Task An_Inverted_Window_Is_Two_Limits_In_An_Or_Not_The_Xor_Of_Figure_6()
    {
        using var rig = await WithTpdo2Async();
        await rig.WriteU8Async(0x6421, 0x01, 0x03);
        await rig.WriteI32Async(0x6424, 0x01, LeftAdjusted(500));
        await rig.WriteI32Async(0x6425, 0x01, LeftAdjusted(1000));
        await rig.WriteU8Async(0x6423, 0x00, 1);
        await rig.StartAndTakeFirstTpdoAsync(Cia401Rig.Tpdo2);

        // Upper 500 at or below lower 1000: 700 is at or above the one and below the other, both
        // hold; an XOR of them would hold for neither.
        await ExpectSentAsync(rig, 700, "both limits hold");
        await ExpectSentAsync(rig, 300, "below the lower limit only");
        await ExpectSentAsync(rig, 1200, "above the upper limit only");
    }

    [Fact]
    public async Task A_Channel_With_No_Condition_Selected_Never_Interrupts()
    {
        using var rig = await WithTpdo2Async();
        await rig.WriteU8Async(0x6421, 0x01, 0x00);
        await rig.WriteU8Async(0x6423, 0x00, 1);
        await rig.StartAndTakeFirstTpdoAsync(Cia401Rig.Tpdo2);

        await ExpectNothingAsync(rig, 1200, "6421h:01 selects nothing");
        await ExpectNothingAsync(rig, -1200, "6421h:01 selects nothing");
        (await rig.ReadU32Async(0x6422, 0x01)).Should().Be(0u, "no interrupt, no source bit");
    }

    [Fact]
    public async Task A_Selection_With_A_Reserved_Bit_Is_Refused_With_The_Value_Range_Abort()
    {
        using var rig = new Cia401Rig(Analog(1));

        foreach (byte value in new byte[] { 0x20, 0x40, 0x80, 0xFF, 0x27 })
        {
            Cia401Rig.AbortCode(await rig.DownloadU8Async(0x6421, 0x01, value)).Should().Be(ValueRangeExceeded,
                $"bits 5 to 7 of 6421h are reserved, 0b only (§8.6.2 Table 148); {value:X2}h sets one");
        }

        (await rig.ReadU8Async(0x6421, 0x01)).Should().Be(0x07, "the refused writes stored nothing");
        await rig.WriteU8Async(0x6421, 0x01, 0x1F);
        (await rig.ReadU8Async(0x6421, 0x01)).Should().Be(0x1F, "bits 0 to 4 are the five conditions");

        FluentActions.Invoking(() => rig.Od.WriteRaw(0x6421, 0x01, new byte[] { 0x20 }))
            .Should().Throw<ArgumentException>().WithMessage("*0x06090030*");
    }

    // ---- 6422h, the interrupt source ------------------------------------------------------------------

    [Fact]
    public async Task An_Interrupt_Sets_The_Channel_Bit_In_Its_Bank_Of_6422h()
    {
        using var rig = new Cia401Rig(Analog(40));
        await rig.WriteU8Async(0x6423, 0x00, 1);
        await rig.StartAsync();

        rig.Device.SetAnalogInput(1, 5);
        (await rig.ReadU32Async(0x6422, 0x01)).Should().Be(0x0000_0001u, "channel 1 is bit 0 of bank 1");
        rig.Device.SetAnalogInput(2, 5);
        rig.Device.SetAnalogInput(32, 5);
        (await rig.ReadU32Async(0x6422, 0x01)).Should().Be(0x8000_0003u, "channel 32 is bit 31 of bank 1");
        rig.Device.SetAnalogInput(33, 5);
        (await rig.ReadU32Async(0x6422, 0x02)).Should().Be(0x0000_0001u, "channel 33 is bit 0 of bank 2");
        (await rig.ReadU32Async(0x6422, 0x01)).Should().Be(0x8000_0003u, "the bits accumulate");
    }

    [Fact]
    public async Task The_Source_Bits_Are_Cleared_By_A_Write_To_6423h_By_Entering_Operational_And_By_A_Reset()
    {
        using var rig = new Cia401Rig(Analog(40));
        await rig.WriteU8Async(0x6423, 0x00, 1);
        await rig.StartAsync();

        rig.Device.SetAnalogInput(1, 5);
        rig.Device.SetAnalogInput(33, 5);
        (await rig.ReadU32Async(0x6422, 0x02)).Should().Be(1u);
        await rig.WriteU8Async(0x6423, 0x00, 1);
        rig.Settle();
        (await rig.ReadU32Async(0x6422, 0x01)).Should().Be(0u, "a write to 6423h clears every bank");
        (await rig.ReadU32Async(0x6422, 0x02)).Should().Be(0u);

        rig.Device.SetAnalogInput(1, 6);
        (await rig.ReadU32Async(0x6422, 0x01)).Should().Be(1u);
        await rig.WriteU8Async(0x6423, 0x00, 0);
        rig.Settle();
        (await rig.ReadU32Async(0x6422, 0x01)).Should().Be(0u, "a write of FALSE clears it too");

        await rig.WriteU8Async(0x6423, 0x00, 1);
        rig.Device.SetAnalogInput(1, 7);
        (await rig.ReadU32Async(0x6422, 0x01)).Should().Be(1u);
        await rig.EnterPreOperationalAsync();
        await rig.StartAsync();
        rig.Settle();
        (await rig.ReadU32Async(0x6422, 0x01)).Should().Be(0u, "entering Operational clears it");

        rig.Device.SetAnalogInput(1, 8);
        (await rig.ReadU32Async(0x6422, 0x01)).Should().Be(1u);
        await rig.ResetAsync(NmtCommand.ResetCommunication);
        (await rig.ReadU32Async(0x6422, 0x01)).Should().Be(0u, "Reset Communication, which restores nothing from 6000h on, clears it");
    }

    [Fact]
    public async Task With_6423h_False_No_Source_Bit_Is_Set()
    {
        using var rig = new Cia401Rig(Analog(1));
        await rig.StartAsync();

        rig.Device.SetAnalogInput(1, 5);

        (await rig.ReadU32Async(0x6422, 0x01)).Should().Be(0u);
    }

    [Fact]
    public async Task Downloads_To_6401h_And_6422h_Are_Refused_As_Read_Only()
    {
        using var rig = new Cia401Rig(Analog(1));

        Cia401Rig.AbortCode(await rig.SdoAsync(new byte[] { 0x2B, 0x01, 0x64, 0x01, 0x10, 0x00, 0, 0 }))
            .Should().Be(AttemptWriteReadOnly);
        Cia401Rig.AbortCode(await rig.DownloadU32Async(0x6422, 0x01, 1)).Should().Be(AttemptWriteReadOnly);
        FluentActions.Invoking(() => rig.Od.WriteRaw(0x6401, 0x01, new byte[] { 0x10, 0x00 }))
            .Should().Throw<ArgumentException>().WithMessage("*0x06010002*");
    }

    // ---- one batch, one request ---------------------------------------------------------------------

    [Fact]
    public async Task Several_Channels_Set_At_Once_Send_One_Tpdo()
    {
        using var rig = await WithTpdo2Async(4);
        await rig.WriteU8Async(0x6423, 0x00, 1);
        await rig.StartAndTakeFirstTpdoAsync(Cia401Rig.Tpdo2);

        rig.Device.SetAnalogInputs(1, new short[] { 10, 20, 30, 40 });
        await rig.WaitForCountAsync(Cia401Rig.Tpdo2, 2);
        await rig.DrainAsync();

        rig.Payloads(Cia401Rig.Tpdo2).Skip(1).Should().ContainSingle("four interrupts of one set are one request")
            .Which.Should().Equal(I16(10, 20, 30, 40));
        (await rig.ReadU32Async(0x6422, 0x01)).Should().Be(0x0Fu);
        rig.Device.ReadAnalogInput(3).Should().Be(30);
    }

    [Fact]
    public async Task SetAnalogInputs_Takes_A_Copy_Of_The_Values()
    {
        using var rig = new Cia401Rig(Analog(2));
        var values = new short[] { 7, 8 };

        rig.Device.SetAnalogInputs(1, values);
        values[0] = 99;
        rig.Settle();

        rig.Device.ReadAnalogInput(1).Should().Be(7);
        rig.Device.ReadAnalogInput(2).Should().Be(8);
    }

    // ---- the application side ---------------------------------------------------------------------------

    [Fact]
    public void A_Channel_The_Device_Does_Not_Have_Is_Refused()
    {
        using var rig = new Cia401Rig(Analog(4));

        FluentActions.Invoking(() => rig.Device.SetAnalogInput(0, 1))
            .Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("channel");
        FluentActions.Invoking(() => rig.Device.SetAnalogInput(5, 1))
            .Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("channel");
        FluentActions.Invoking(() => rig.Device.ReadAnalogInput(5))
            .Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("channel");
        FluentActions.Invoking(() => rig.Device.SetAnalogInputs(0, new short[] { 1 }))
            .Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("firstChannel");
        FluentActions.Invoking(() => rig.Device.SetAnalogInputs(3, new short[] { 1, 2, 3 }))
            .Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("values");
    }

    [Fact]
    public void An_Empty_Set_Of_Values_Is_Accepted_And_Sets_Nothing()
    {
        using var rig = new Cia401Rig(Analog(4));
        rig.Device.SetAnalogInput(4, 44);
        rig.Settle();

        FluentActions.Invoking(() => rig.Device.SetAnalogInputs(4, ReadOnlySpan<short>.Empty)).Should().NotThrow(
            "an empty span from the last channel does not run past it");
        rig.Settle();

        rig.Device.ReadAnalogInput(4).Should().Be(44);
    }

    [Fact]
    public void A_Device_Without_Analog_Inputs_Has_No_Channel()
    {
        using var rig = new Cia401Rig(new Cia401Configuration { DigitalInputs = 8 });

        FluentActions.Invoking(() => rig.Device.SetAnalogInput(1, 1))
            .Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("channel");
        FluentActions.Invoking(() => rig.Device.ReadAnalogInput(1))
            .Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("channel");
    }

    [Fact]
    public void Before_It_Is_Attached_The_Device_Refuses_To_Set_Or_Read_Analog_Inputs()
    {
        var device = new Cia401Device(Analog(1));

        FluentActions.Invoking(() => device.SetAnalogInput(1, 1)).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => device.SetAnalogInputs(1, new short[] { 1 })).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => device.ReadAnalogInput(1)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void After_The_Node_Is_Disposed_A_Set_Does_Nothing_And_The_Reader_Shows_The_Last_Value()
    {
        Cia401Device device;
        using (var rig = new Cia401Rig(Analog(1)))
        {
            rig.Device.SetAnalogInput(1, -1234);
            rig.Settle();
            device = rig.Device;
        }

        FluentActions.Invoking(() => device.SetAnalogInput(1, 1)).Should().NotThrow();
        device.ReadAnalogInput(1).Should().Be(-1234);
    }

    [Fact]
    public async Task A_Device_With_Digital_And_Analog_Inputs_Sends_Both_Tpdos_On_Entering_Operational()
    {
        using var rig = new Cia401Rig(new Cia401Configuration { DigitalInputs = 8, AnalogInputs = 1 }, Cia401Rig.WritablePdoRecords);
        await rig.EnableTpdoAsync(2);
        rig.Device.SetDigitalInputs(1, 0x5A);
        rig.Device.SetAnalogInput(1, 77);
        rig.Settle();

        await rig.StartAsync();
        await rig.WaitForCountAsync(Cia401Rig.Tpdo1, 1);
        await rig.WaitForCountAsync(Cia401Rig.Tpdo2, 1);
        await rig.DrainAsync();

        rig.Payloads(Cia401Rig.Tpdo1).Should().ContainSingle().Which.Should().Equal(0x5A);
        rig.Payloads(Cia401Rig.Tpdo2).Should().ContainSingle().Which.Should().Equal(I16(77));
        rig.Od.ReadUnsigned(0x1000, 0x00).Should().Be(0x00050191u, "digital and analog inputs, bits 16 and 18 (§6.2.1)");
    }
}
