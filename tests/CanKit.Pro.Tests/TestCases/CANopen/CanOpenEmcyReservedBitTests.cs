using System;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

// FR-CO-011, #566: bit 6 of the error register is reserved and always 0b (CiA 301 v4.2.0 §7.5.2.2,
// Table 54). Neither SendEmcyAsync nor a write of 1001h through the dictionary puts it on the wire or
// into 1001h: the bit is cleared and everything else proceeds as before. Bit 7, manufacturer-specific,
// stays.
public partial class CanOpenPdoEngineTests
{
    [Theory]
    [InlineData(0x40, 0x00)]
    [InlineData(0x3F, 0x3F)]
    [InlineData(0x7F, 0x3F)]
    [InlineData(0x80, 0x80)]
    [InlineData(0xBF, 0xBF)]
    [InlineData(0xC0, 0x80)]
    public async Task SendEmcyAsync_Transmits_And_Records_The_Reserved_Bit_6_As_Zero(int passed, int expected)
    {
        using var rig = new EmcyInhibitRig(new CanOpenNodeOptions());
        await rig.Node.SendEmcyAsync(0x1000, (byte)passed).WaitAsync(ShortTimeout);
        await ExpectEmcyAsync(rig.Wire, 1, 0x1000, (byte)expected);
        rig.Node.ObjectDictionary.ReadUnsigned(0x1001, 0).Should().Be((uint)expected);
    }

    [Fact]
    public async Task SendEmcyAsync_Held_In_Stopped_Transmits_The_Reserved_Bit_6_As_Zero()
    {
        using var rig = new EmcyInhibitRig(new CanOpenNodeOptions());
        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)NmtCommand.Stop, Device);
        await rig.Node.SendEmcyAsync(0x1000, 0x44).WaitAsync(ShortTimeout);
        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)NmtCommand.Start, Device);
        await ExpectEmcyAsync(rig.Wire, 1, 0x1000, 0x04);
        rig.Node.ObjectDictionary.ReadUnsigned(0x1001, 0).Should().Be(0x04);
    }

    [Theory]
    [InlineData(0x44, 0x04)]
    [InlineData(0xC4, 0x84)]
    [InlineData(0x84, 0x84)]
    public async Task A_Local_Write_Of_1001h_Records_The_Reserved_Bit_6_As_Zero(int written, int expected)
    {
        using var rig = new EmcyInhibitRig(new CanOpenNodeOptions { EmitControllerOverrunEmcy = true });
        rig.Node.ObjectDictionary.WriteUnsigned(0x1001, 0, (uint)written);
        rig.Node.ObjectDictionary.ReadUnsigned(0x1001, 0).Should().Be((uint)expected);
        // The written value is the application's part, which the node's own next EMCY carries.
        rig.Bus.RaiseErrorFrame(new StubErrorInfo { ControllerStatus = CanControllerStatus.RxOverflow });
        await ExpectEmcyAsync(rig.Wire, 1, 0x8110, (byte)(expected | Communication));
        rig.Node.ObjectDictionary.ReadUnsigned(0x1001, 0).Should().Be((uint)(expected | Communication));
    }

    [Fact]
    public async Task An_Sdo_Download_Of_1001h_Is_Refused_As_Read_Only()
    {
        using var rig = new EmcyInhibitRig(new CanOpenNodeOptions());
        await Inject(rig.Node, CanOpenCobId.SdoRx(Device), SdoFrames.BuildDownloadInit(0x1001, 0, new byte[] { 0x44 }));
        await rig.Wire.WaitForCountAsync(CanOpenCobId.SdoTx(Device), 1);
        var response = rig.Wire.Payloads(CanOpenCobId.SdoTx(Device))[0];
        response[0].Should().Be(0x80);
        BitConverter.ToUInt32(response, 4).Should().Be((uint)SdoAbortCode.AttemptWriteReadOnly);
        rig.Node.ObjectDictionary.ReadUnsigned(0x1001, 0).Should().Be(0);
    }

    private static CanOpenDeviceDescription ErrorRegisterDescription(string access, string defaultValue)
        => CanOpenDeviceDescription.ParseEds($@"
[FileInfo]
FileName=register.eds
FileVersion=1
FileRevision=0
EDSVersion=4.0
[DeviceInfo]
VendorName=Tests
VendorNumber=0
ProductName=Register
ProductNumber=0
RevisionNumber=0
[MandatoryObjects]
SupportedObjects=1
1=0x1001
[OptionalObjects]
SupportedObjects=0
[ManufacturerObjects]
SupportedObjects=0
[1001]
ParameterName=Error register
ObjectType=0x7
DataType=0x0005
AccessType={access}
DefaultValue={defaultValue}
PDOMapping=0
");

    [Fact]
    public async Task A_Described_1001h_Records_The_Reserved_Bit_6_As_Zero_From_Its_Default_And_An_Sdo_Download()
    {
        using var rig = new EmcyInhibitRig(new CanOpenNodeOptions(), ErrorRegisterDescription("rw", "0x44"));
        rig.Node.ObjectDictionary.ReadUnsigned(0x1001, 0).Should().Be(0x04, "the described default loses bit 6");
        await Inject(rig.Node, CanOpenCobId.SdoRx(Device), SdoFrames.BuildDownloadInit(0x1001, 0, new byte[] { 0xC8 }));
        await rig.Wire.WaitForCountAsync(CanOpenCobId.SdoTx(Device), 1);
        rig.Wire.Payloads(CanOpenCobId.SdoTx(Device))[0][0].Should().Be(0x60, "the download is accepted");
        rig.Node.ObjectDictionary.ReadUnsigned(0x1001, 0).Should().Be(0x88);
    }
}
