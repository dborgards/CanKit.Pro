using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Cia401;
using CanKit.Pro.CANopen.Nmt;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Cia401;

/// <summary>
/// The CiA 401 device across NMT resets and <c>1010h</c>: Reset Node gives back the device that
/// powered on (the plan's Review Focus 1), Reset Communication leaves the application where it is
/// and gives back the PDO records, the inputs show the application's levels after a reset whatever
/// the dictionary was restored to, and stored output values come up after Reset Node (CiA 401-1
/// v3.1.0 §6.2.5 NOTE, §6.2.7 NOTE, §7.2.5 NOTE 1; CiA 301 v4.2.0 §7.5.2.13). Indices are written out
/// here, not taken from <see cref="Cia401Objects"/>.
/// </summary>
public class Cia401DeviceResetTests
{
    private static Cia401Configuration AllFour => new()
    {
        DigitalInputs = 8,
        DigitalOutputs = 8,
        AnalogInputs = 4,
        AnalogOutputs = 4,
    };

    /// <summary>TPDO 2's COB-ID as the device sets it: the pre-defined CAN-ID, bit 31 set, "PDO does
    /// not exist / is not valid" (CiA 301 v4.2.0 §7.5.2.37).</summary>
    private const uint Tpdo2Disabled = 0x8000_0000u | Cia401Rig.Tpdo2;

    /// <summary>Every entry of the PDO records (<c>1400h</c> to <c>1BFFh</c>) and of the logical
    /// device's application area (<c>6000h</c> to <c>67FFh</c>), as raw bytes.</summary>
    private static Dictionary<uint, byte[]> Snapshot(Cia401Rig rig, ushort first, ushort last)
    {
        var snapshot = new Dictionary<uint, byte[]>();
        foreach (var key in rig.Od.SnapshotKeys())
        {
            var index = (ushort)(key >> 8);
            if (index < first || index > last) continue;
            snapshot[key] = rig.Od.ReadRaw(index, (byte)key);
        }

        return snapshot;
    }

    private static Dictionary<uint, byte[]> PdoRecords(Cia401Rig rig) => Snapshot(rig, 0x1400, 0x1BFF);

    private static Dictionary<uint, byte[]> ApplicationObjects(Cia401Rig rig) => Snapshot(rig, 0x6000, 0x67FF);

    /// <summary>The entries whose value differs between the two snapshots, as readable text.</summary>
    private static IEnumerable<string> Differences(Dictionary<uint, byte[]> expected, Dictionary<uint, byte[]> actual)
    {
        expected.Keys.Should().BeEquivalentTo(actual.Keys, "a reset neither adds nor removes an entry");
        return expected
            .Where(e => !e.Value.SequenceEqual(actual[e.Key]))
            .Select(e => $"{e.Key >> 8:X4}h:{e.Key & 0xFF:X2}h expected {BitConverter.ToString(e.Value)}, "
                + $"was {BitConverter.ToString(actual[e.Key])}");
    }

    /// <summary>Writes, over SDO, a value other than the default into every kind of object the device
    /// has: the digital input masks, polarity and filter, the digital output polarity, error mode,
    /// error value and filter mask, the analog input set-up and global enable, and the analog output
    /// error mode and error value. The outputs themselves are written by PDO.</summary>
    private static async Task WriteEverythingAsync(Cia401Rig rig)
    {
        await rig.WriteU8Async(0x6002, 0x01, 0xFF);
        await rig.WriteU8Async(0x6003, 0x01, 0xFF);
        await rig.WriteU8Async(0x6005, 0x00, 0x00);
        await rig.WriteU8Async(0x6006, 0x01, 0x0F);
        await rig.WriteU8Async(0x6007, 0x01, 0xF0);
        await rig.WriteU8Async(0x6008, 0x01, 0x55);
        await rig.WriteU8Async(0x6202, 0x01, 0x0F);
        await rig.WriteU8Async(0x6206, 0x01, 0x00);
        await rig.WriteU8Async(0x6207, 0x01, 0xFF);
        await rig.WriteU8Async(0x6208, 0x01, 0xF0);
        await rig.WriteU8Async(0x6421, 0x01, 0x04);
        await rig.WriteU8Async(0x6423, 0x00, 0x01);
        await rig.WriteI32Async(0x6424, 0x01, 0x0100_0000);
        await rig.WriteI32Async(0x6425, 0x01, -0x0100_0000);
        await rig.WriteU32Async(0x6426, 0x01, 50);
        await rig.WriteU32Async(0x6427, 0x01, 60);
        await rig.WriteU32Async(0x6428, 0x01, 70);
        await rig.WriteU8Async(0x6443, 0x01, 0x00);
        await rig.WriteI32Async(0x6444, 0x01, 0x04D2_0000);
    }

    // ---- Review Focus 1 ------------------------------------------------------------------------

    [Fact] // Review Focus 1: a device restored by Reset Node is the device that powered on
    public async Task Reset_Node_Gives_Back_The_Device_That_Powered_On()
    {
        using var rig = new Cia401Rig(AllFour, Cia401Rig.WritablePdoRecords);
        rig.Settle();
        var poweredOn = Snapshot(rig, 0x1400, 0x67FF);
        await WriteEverythingAsync(rig);
        await rig.WriteU8Async(0x1801, 0x02, 0xFE);
        await rig.EnableTpdoAsync(2);
        await rig.StartAndTakeFirstTpdoAsync();
        await rig.SendRpdo1Async(0xA5);
        await rig.SendRpdo2Async(Cia401Rig.Words(100, 200, 300, 400));
        await rig.StopAsync();
        rig.Device.OutputsInErrorState.Should().BeTrue("Stopped is a cause of the error state");
        rig.Device.ReadAnalogOutput(2).Should().Be(0, "6443h:02 = 1 took 6444h:02, 0");
        int tpdo2Before = rig.Count(Cia401Rig.Tpdo2);

        await rig.ResetAsync(NmtCommand.ResetNode);

        Differences(poweredOn, Snapshot(rig, 0x1400, 0x67FF)).Should().BeEmpty(
            "every application object and every PDO record is back at its power-on value");
        (await rig.ReadU8Async(0x6443, 0x01)).Should().Be(0x01, "the catalogue default of 6443h (§8.7.4)");
        (await rig.ReadU8Async(0x6206, 0x01)).Should().Be(0xFF, "the catalogue default of 6206h (§8.3.3)");
        (await rig.ReadU8Async(0x6423, 0x00)).Should().Be(0x00, "the catalogue default of 6423h (§8.6.4)");
        (await rig.ReadU32Async(0x1801, 0x01)).Should().Be(Tpdo2Disabled, "TPDO 2 is disabled again");
        (await rig.ReadU8Async(0x1801, 0x02)).Should().Be(0xFF, "the default transmission type (§6.2.8)");
        rig.Device.OutputsInErrorState.Should().BeFalse("the reset ended Stopped");
        rig.Device.ReadDigitalOutputs(1).Should().Be(0x00, "the held output values are gone: 6200h is 00h (§6.2.5 NOTE)");
        Enumerable.Range(1, 4).Select(c => rig.Device.ReadAnalogOutput(c)).Should().OnlyContain(v => v == 0,
            "the held analog values are gone: 6411h is 0000h (§6.2.7 NOTE)");

        await rig.StartAndTakeFirstTpdoAsync();
        await rig.DrainAsync();
        rig.Count(Cia401Rig.Tpdo2).Should().Be(tpdo2Before, "a disabled TPDO 2 is not sent on entering Operational");
        await rig.SendRpdo1Async(0x0F);
        rig.Device.ReadDigitalOutputs(1).Should().Be(0x0F, "the filter mask is FFh again and no polarity inverts");
    }

    [Fact] // Reset Communication restores 1000h to 1FFFh only (CiA 301 v4.2.0 §7.3.2.2.1)
    public async Task Reset_Communication_Keeps_The_Application_Objects_And_Gives_Back_The_Pdo_Records()
    {
        using var rig = new Cia401Rig(AllFour, Cia401Rig.WritablePdoRecords);
        rig.Settle();
        var pdoRecordsPoweredOn = PdoRecords(rig);
        await WriteEverythingAsync(rig);
        await rig.WriteU8Async(0x1801, 0x02, 0xFE);
        await rig.EnableTpdoAsync(2);
        await rig.StartAndTakeFirstTpdoAsync();
        await rig.SendRpdo1Async(0xA5);
        await rig.SendRpdo2Async(Cia401Rig.Words(100, 200, 300, 400));
        await rig.EnterPreOperationalAsync();
        var applicationBefore = ApplicationObjects(rig);
        byte digitalBefore = rig.Device.ReadDigitalOutputs(1);

        await rig.ResetAsync(NmtCommand.ResetCommunication);

        Differences(applicationBefore, ApplicationObjects(rig)).Should().BeEmpty("the application objects keep their values");
        Differences(pdoRecordsPoweredOn, PdoRecords(rig)).Should().BeEmpty("the PDO records are back at their defaults");
        (await rig.ReadU32Async(0x1801, 0x01)).Should().Be(Tpdo2Disabled, "TPDO 2 is disabled again");
        rig.Device.ReadDigitalOutputs(1).Should().Be(digitalBefore, "the outputs stay where they are");
        Enumerable.Range(1, 4).Select(c => rig.Device.ReadAnalogOutput(c)).Should().Equal(new short[] { 100, 200, 300, 400 });
    }

    // ---- the inputs after a reset (controller ruling 4b, 4c) -------------------------------------

    [Fact]
    public async Task Reset_Node_Shows_The_Applications_Input_Levels_Not_The_Restored_Ones()
    {
        using var rig = new Cia401Rig(
            new Cia401Configuration { DigitalInputs = 8, AnalogInputs = 4 }, Cia401Rig.WritablePdoRecords);
        await rig.WriteU8Async(0x6002, 0x01, 0xFF);
        rig.Device.SetDigitalInputs(1, 0x0F);
        rig.Device.SetAnalogInput(1, 1234);
        rig.Settle();
        (await rig.ReadU8Async(0x6000, 0x01)).Should().Be(0xF0, "inverted by 6002h before the reset");

        await rig.ResetAsync(NmtCommand.ResetNode);

        (await rig.ReadU8Async(0x6000, 0x01)).Should().Be(0x0F,
            "the physical levels under the restored polarity 00h, not the restored 00h of 6000h");
        (await rig.ReadI16Async(0x6401, 0x01)).Should().Be(1234, "the value the application set, not the restored 0");
        rig.Device.ReadDigitalInputs(1).Should().Be(0x0F);
        rig.Device.ReadAnalogInput(1).Should().Be(1234);

        int tpdo1 = rig.Count(Cia401Rig.Tpdo1);
        int tpdo2 = rig.Count(Cia401Rig.Tpdo2);
        await rig.EnableTpdoAsync(2);
        await rig.StartAsync();
        await rig.WaitForCountAsync(Cia401Rig.Tpdo1, tpdo1 + 1);
        await rig.WaitForCountAsync(Cia401Rig.Tpdo2, tpdo2 + 1);
        rig.Payloads(Cia401Rig.Tpdo1)[tpdo1].Should().Equal(new byte[] { 0x0F }, "entering Operational sends the inputs (§6.1)");
        rig.Payloads(Cia401Rig.Tpdo2)[tpdo2].Should().Equal(Cia401Rig.Words(1234, 0, 0, 0));
    }

    [Fact] // a save stores 6000h and 6401h too; the stored values never show
    public async Task Stored_Input_Values_Do_Not_Show_After_Reset_Node()
    {
        using var rig = new Cia401Rig(new Cia401Configuration { DigitalInputs = 8, AnalogInputs = 1 });
        rig.Device.SetDigitalInputs(1, 0x11);
        rig.Device.SetAnalogInput(1, 111);
        rig.Settle();
        await rig.SaveAsync();
        rig.Device.SetDigitalInputs(1, 0x22);
        rig.Device.SetAnalogInput(1, 222);
        rig.Settle();

        await rig.ResetAsync(NmtCommand.ResetNode);

        (await rig.ReadU8Async(0x6000, 0x01)).Should().Be(0x22, "the levels the application set last, not the stored 11h");
        (await rig.ReadI16Async(0x6401, 0x01)).Should().Be(222, "the value the application set last, not the stored 111");
    }

    [Fact] // the restored polarity applies to the physical levels, whatever it was restored to
    public async Task Reset_Node_Rewrites_6000h_Under_A_Stored_Polarity()
    {
        using var rig = new Cia401Rig(new Cia401Configuration { DigitalInputs = 8 });
        await rig.WriteU8Async(0x6002, 0x01, 0xFF);
        await rig.SaveAsync(); // stores 6002h:01 = FFh, and 6000h:01 = FFh with it
        rig.Device.SetDigitalInputs(1, 0x0F);
        await rig.WriteU8Async(0x6002, 0x01, 0x00);
        rig.Settle();
        (await rig.ReadU8Async(0x6000, 0x01)).Should().Be(0x0F);

        await rig.ResetAsync(NmtCommand.ResetNode);

        (await rig.ReadU8Async(0x6002, 0x01)).Should().Be(0xFF, "the stored polarity");
        (await rig.ReadU8Async(0x6000, 0x01)).Should().Be(0xF0,
            "the physical 0Fh under the stored polarity FFh, not the stored FFh and not 0Fh uninverted");
    }

    [Fact] // §6.2.5 NOTE, §6.2.7 NOTE: "stored values overwrite default values"
    public async Task Stored_Output_Values_Come_Up_After_Reset_Node()
    {
        using var rig = new Cia401Rig(new Cia401Configuration { DigitalOutputs = 8, AnalogOutputs = 1 });
        await rig.WriteU8Async(0x6200, 0x01, 0x5A);
        await rig.WriteI16Async(0x6411, 0x01, 1234);
        await rig.SaveAsync();
        await rig.WriteU8Async(0x6200, 0x01, 0x0F);
        await rig.WriteI16Async(0x6411, 0x01, -5);
        rig.Settle();
        rig.Device.ReadDigitalOutputs(1).Should().Be(0x0F);
        rig.Device.ReadAnalogOutput(1).Should().Be(-5);

        await rig.ResetAsync(NmtCommand.ResetNode);

        rig.Device.ReadDigitalOutputs(1).Should().Be(0x5A, "the outputs come up with the stored 6200h");
        rig.Device.ReadAnalogOutput(1).Should().Be(1234, "and the stored 6411h");
        (await rig.ReadU8Async(0x6200, 0x01)).Should().Be(0x5A);
        (await rig.ReadI16Async(0x6411, 0x01)).Should().Be(1234);
    }

    [Fact] // review 4c: stored set-up objects come up after Reset Node, and the outputs follow them
    public async Task Stored_Masks_Polarities_And_Error_Settings_Come_Up_After_Reset_Node()
    {
        using var rig = new Cia401Rig(new Cia401Configuration { DigitalInputs = 8, DigitalOutputs = 8, AnalogOutputs = 2 });
        var stored = new (ushort Index, byte Sub, byte Value)[]
        {
            (0x6002, 0x01, 0xFF), (0x6006, 0x01, 0x0F), (0x6202, 0x01, 0x0F),
            (0x6206, 0x01, 0x0F), (0x6207, 0x01, 0xAA), (0x6208, 0x01, 0xF0), (0x6443, 0x02, 0x00),
        };
        foreach (var (index, sub, value) in stored) await rig.WriteU8Async(index, sub, value);
        await rig.WriteI32Async(0x6444, 0x01, 0x0123_0000);
        await rig.WriteI16Async(0x6411, 0x02, 55);
        await rig.WriteU8Async(0x1029, 0x01, 0x01);
        await rig.SaveAsync();
        // Back to the defaults, so that what comes up can only be what was stored.
        foreach (var (index, sub, value) in new (ushort, byte, byte)[]
        {
            (0x6002, 0x01, 0x00), (0x6006, 0x01, 0xFF), (0x6202, 0x01, 0x00),
            (0x6206, 0x01, 0xFF), (0x6207, 0x01, 0x00), (0x6208, 0x01, 0xFF), (0x6443, 0x02, 0x01),
        })
        {
            await rig.WriteU8Async(index, sub, value);
        }

        await rig.WriteI32Async(0x6444, 0x01, 0);
        await rig.WriteI16Async(0x6411, 0x02, 0);
        rig.Settle();
        rig.Device.ReadDigitalOutputs(1).Should().Be(0x00);
        rig.Device.ReportDeviceFailure(true); // outlasts the reset: the analog outputs take their error settings
        rig.Settle();

        await rig.ResetAsync(NmtCommand.ResetNode);

        foreach (var (index, sub, value) in stored)
            (await rig.ReadU8Async(index, sub)).Should().Be(value, $"{index:X4}h:{sub:X2}h comes up as stored");
        (await rig.ReadU32Async(0x6444, 0x01)).Should().Be(0x0123_0000u);
        (await rig.ReadU8Async(0x6000, 0x01)).Should().Be(0xFF, "physical 00h under the stored polarity FFh");
        rig.Device.OutputsInErrorState.Should().BeTrue();
        rig.Device.ReadDigitalOutputs(1).Should().Be(0x05,
            "in the error state the low nibble (stored 6206h 0Fh) takes the stored 6207h AAh, the high one keeps 6200h's 0, "
            + "and the stored 6202h 0Fh inverts the low nibble: 0Ah XOR 0Fh");
        rig.Device.ReadAnalogOutput(1).Should().Be(0x0123, "the stored 6444h:01, 6443h:01 = 1");
        rig.Device.ReadAnalogOutput(2).Should().Be(55, "the stored 6411h:02, kept by the stored 6443h:02 = 0");
    }

    [Fact] // the interrupt state starts afresh from the application's value, not the restored one
    public async Task Reset_Node_Measures_The_Next_Delta_From_The_Applications_Value()
    {
        using var rig = new Cia401Rig(new Cia401Configuration { AnalogInputs = 1 });
        await rig.WriteU8Async(0x6421, 0x01, 0x04); // the delta alone
        await rig.WriteU32Async(0x6426, 0x01, 50);
        await rig.WriteU8Async(0x6423, 0x00, 0x01);
        rig.Device.SetAnalogInput(1, 1000); // more than 50 from 0: an interrupt, the reference is 1000
        rig.Settle();
        await rig.SaveAsync(); // stores 6401h = 1000, 6422h:01 = 1 and the set-up
        rig.Device.SetAnalogInput(1, 1030); // 30 from 1000: no interrupt
        rig.Settle();

        await rig.ResetAsync(NmtCommand.ResetNode);

        (await rig.ReadU32Async(0x6422, 0x01)).Should().Be(0u, "a reset clears the interrupt source");
        rig.Device.SetAnalogInput(1, 1060);
        rig.Settle();
        (await rig.ReadU32Async(0x6422, 0x01)).Should().Be(0u,
            "1060 is 30 from 1030, the application's value at the reset, not 60 from the old or restored 1000");
        rig.Device.SetAnalogInput(1, 1100);
        rig.Settle();
        (await rig.ReadU32Async(0x6422, 0x01)).Should().Be(1u, "70 from 1030 interrupts: the evaluation runs");
    }
}
