using System;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Pdo;
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

    private static CanOpenDeviceDescription ErrorRegisterDescription(string access, string defaultValue, int pdoMapping = 0)
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
PDOMapping={pdoMapping}
");

    [Fact]
    public void A_Write_Of_1001h_With_Bit_6_Is_Reported_Once_With_The_Cleared_Value()
    {
        using var rig = new EmcyInhibitRig(new CanOpenNodeOptions());
        var seen = new System.Collections.Generic.List<uint>();
        var od = rig.Node.ObjectDictionary;
        Action<ushort, byte> observe = (index, _) => { if (index == 0x1001) seen.Add(od.ReadUnsigned(0x1001, 0)); };
        od.EntryWritten += observe;
        try
        {
            var written = new byte[] { 0x44 };
            od.WriteRaw(0x1001, 0, written);
            written[0].Should().Be(0x44, "the caller's array is not changed");
        }
        finally { od.EntryWritten -= observe; }
        seen.Should().Equal(0x04u);
    }

    /// <summary>A writable, PDO-mappable 1001h and RPDO1, whose mapping is 1001h:00 (8 bits).</summary>
    private static CanOpenDeviceDescription ErrorRegisterRpdoDescription() => CanOpenDeviceDescription.ParseEds(@"
[FileInfo]
FileName=register-rpdo.eds
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
SupportedObjects=2
1=0x1400
2=0x1600
[ManufacturerObjects]
SupportedObjects=0
[1001]
ParameterName=Error register
ObjectType=0x7
DataType=0x0005
AccessType=rw
DefaultValue=0
PDOMapping=1
[1400]
ParameterName=RPDO1 communication parameter
SubNumber=3
ObjectType=0x9
[1400sub0]
ParameterName=Highest sub-index supported
ObjectType=0x7
DataType=0x0005
AccessType=ro
DefaultValue=2
PDOMapping=0
[1400sub1]
ParameterName=COB-ID
ObjectType=0x7
DataType=0x0007
AccessType=rw
DefaultValue=$NODEID+0x200
PDOMapping=0
[1400sub2]
ParameterName=Transmission type
ObjectType=0x7
DataType=0x0005
AccessType=rw
DefaultValue=255
PDOMapping=0
[1600]
ParameterName=RPDO1 mapping parameter
SubNumber=2
ObjectType=0x9
[1600sub0]
ParameterName=Number of mapped objects
ObjectType=0x7
DataType=0x0005
AccessType=rw
DefaultValue=1
PDOMapping=0
[1600sub1]
ParameterName=Mapping entry 1
ObjectType=0x7
DataType=0x0007
AccessType=rw
DefaultValue=0x10010008
PDOMapping=0
");

    [Fact]
    public async Task An_Rpdo_Store_Into_A_Writable_Described_1001h_Records_The_Reserved_Bit_6_As_Zero()
    {
        using var rig = new EmcyInhibitRig(new CanOpenNodeOptions(), ErrorRegisterRpdoDescription());
        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)NmtCommand.Start, Device);
        await Inject(rig.Node, Rpdo1, 0xC4);
        rig.Node.ObjectDictionary.ReadUnsigned(0x1001, 0).Should().Be(0x84);
    }

    [Fact]
    public async Task An_Mpdo_Store_Into_A_Writable_Described_1001h_Records_The_Reserved_Bit_6_As_Zero()
    {
        using var rig = new EmcyInhibitRig(new CanOpenNodeOptions(), ErrorRegisterRpdoDescription());
        rig.Node.Pdo().ConfigureMultiplexedRpdo(1, MpdoAddressMode.Destination, Rpdo1);
        await Inject(rig.Node, CanOpenCobId.NmtCommand, (byte)NmtCommand.Start, Device);
        await Inject(rig.Node, Rpdo1, 0x80, 0x01, 0x10, 0x00, 0x44, 0, 0, 0);
        rig.Node.ObjectDictionary.ReadUnsigned(0x1001, 0).Should().Be(0x04);
        rig.Wire.Count(EmcyId).Should().Be(0, "the destination was written, so no 8230h");
    }

    [Fact]
    public void A_Described_1001h_Default_Records_The_Reserved_Bit_6_As_Zero()
    {
        using var rig = new EmcyInhibitRig(new CanOpenNodeOptions(), ErrorRegisterDescription("rw", "0x44"));
        rig.Node.ObjectDictionary.ReadUnsigned(0x1001, 0).Should().Be(0x04, "the described default loses bit 6");
        var finding = rig.Node.DeviceDescription!.Findings.Should()
            .ContainSingle(f => f.Index == 0x1001, "the correction is reported as other corrected values are").Which;
        finding.Outcome.Should().Be(DeviceDescriptionOutcome.Corrected);
        finding.Subindex.Should().Be(0);
    }

    [Fact]
    public void A_Described_1001h_Default_Without_Bit_6_Raises_No_Finding()
    {
        using var rig = new EmcyInhibitRig(new CanOpenNodeOptions(), ErrorRegisterDescription("rw", "0x84"));
        rig.Node.ObjectDictionary.ReadUnsigned(0x1001, 0).Should().Be(0x84);
        rig.Node.DeviceDescription!.Findings.Should().NotContain(f => f.Index == 0x1001);
    }

    [Fact]
    public async Task An_Sdo_Download_Of_A_Writable_Described_1001h_Records_The_Reserved_Bit_6_As_Zero()
    {
        using var rig = new EmcyInhibitRig(new CanOpenNodeOptions(), ErrorRegisterDescription("rw", "0"));
        await Inject(rig.Node, CanOpenCobId.SdoRx(Device), SdoFrames.BuildDownloadInit(0x1001, 0, new byte[] { 0xC8 }));
        await rig.Wire.WaitForCountAsync(CanOpenCobId.SdoTx(Device), 1);
        rig.Wire.Payloads(CanOpenCobId.SdoTx(Device))[0][0].Should().Be(0x60, "the download is accepted");
        rig.Node.ObjectDictionary.ReadUnsigned(0x1001, 0).Should().Be(0x88);
    }
}
