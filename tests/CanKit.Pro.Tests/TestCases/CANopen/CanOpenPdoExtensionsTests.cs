using System;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Pdo;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

public partial class CanOpenPdoEngineTests
{
    // FR-CO-055: nine booleans cross a byte boundary; a U16 begins at bit nine, and a dummy
    // BOOLEAN consumes one bit. Expected wire bytes are independent of the packer.
    [Fact]
    public async Task Bit_Mapping_Packs_And_Unpacks_Across_Byte_Boundaries()
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var wire = new Wire(session, 2);
        using var device = CanOpen.OpenNode(bus, Device);
        var od = device.ObjectDictionary;
        var map = new PdoMapping();
        for (byte sub = 1; sub <= 9; sub++)
        {
            od.AddRaw(0x2000, sub, OdDataType.Boolean, new[] { (byte)(sub % 2) });
            map.Add(0x2000, sub, 1);
        }
        od.AddU16(0x2001, 0, 0x1234);
        map.Add(0x2001, 0, 16).Add(0x0001, 0, 1);
        map.TotalBits.Should().Be(26);
        map.TotalBytes.Should().Be(4);
        device.ConfigureTpdo(1, map);
        device.ConfigureRpdo(1, map);
        await StartAsync(wire, device);
        await device.TriggerTpdoAsync(1);
        await wire.WaitForCountAsync(Tpdo1, 1);
        wire.Payloads(Tpdo1)[0].Should().Equal(0x55, 0x69, 0x24, 0x00);
        await Inject(device, Rpdo1, 0xAA, 0xFE, 0xFF, 0xFF);
        for (byte sub = 1; sub <= 9; sub++) od.ReadRaw(0x2000, sub)[0].Should().Be(sub % 2 == 0 ? (byte)1 : (byte)0);
        od.ReadUnsigned(0x2001, 0).Should().Be(0xFFFF);
        var full = new PdoMapping();
        for (int i = 0; i < 64; i++) full.Add(0x0001, 0, 1);
        full.TotalBytes.Should().Be(8);
        Action overflow = () => full.Add(0x0001, 0, 1);
        overflow.Should().Throw<InvalidOperationException>();
        full.TotalBits.Should().Be(64);
    }

    // FR-CO-056: receive through the real reader/demux, and distinguish frame formats even
    // where their numeric identifiers coincide.
    [Fact]
    public async Task Additional_Pdos_And_Extended_Ids_Route_Independently()
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var wire = new Wire(session, 2);
        using var device = CanOpen.OpenNode(bus, Device, new CanOpenNodeOptions { PdoCount = 512 });
        var od = device.ObjectDictionary;
        od.AddU8(0x2000, 0, 42);
        od.AddU8(0x2001, 0, 0);
        od.AddU8(0x2002, 0, 0);
        uint ext = CanOpenCobId.ExtendedFrameBit | Rpdo1;
        od.WriteUnsigned(0x19FF, 1, od.ReadUnsigned(0x19FF, 1)); // idempotent destroy before assigning an ID
        device.ConfigureRpdo(1, new PdoMapping().Add(0x2001, 0, 8), Rpdo1);
        device.ConfigureRpdo(512, new PdoMapping().Add(0x2002, 0, 8), ext);
        device.ConfigureTpdo(512, new PdoMapping().Add(0x2000, 0, 8), cobId: CanOpenCobId.ExtendedFrameBit | 0x1234567);
        await StartAsync(wire, device);
        wire.Transmit(ext, new byte[] { 17 });
        wire.Transmit(Rpdo1, new byte[] { 23 });
        var until = DateTime.UtcNow + ShortTimeout;
        while (od.ReadUnsigned(0x2001, 0) != 23 || od.ReadUnsigned(0x2002, 0) != 17)
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("PDOs of different frame formats were not received.");
            await Task.Delay(5);
        }
        await device.TriggerTpdoAsync(512);
        await wire.WaitForCountAsync(CanOpenCobId.ExtendedFrameBit | 0x1234567, 1);
        wire.Payloads(CanOpenCobId.ExtendedFrameBit | 0x1234567)[0].Should().Equal(42);
        od.ReadUnsigned(0x1BFF, 0).Should().Be(1);
    }

    // FR-CO-057: a DAM packet addresses one node or all consumers; unavailable objects cause
    // EMCY 8230h, and the wire carries the target multiplexer and four padded data bytes.
    [Fact]
    public async Task Destination_Mpdo_Addresses_Local_Objects_And_Reports_Unavailable_Ones()
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var wire = new Wire(session, 2);
        using var device = CanOpen.OpenNode(bus, Device);
        var od = device.ObjectDictionary;
        od.AddU16(0x2000, 1, 0);
        device.Pdo().ConfigureMultiplexedRpdo(1, MpdoAddressMode.Destination, Rpdo1);
        device.Pdo().ConfigureMultiplexedTpdo(1, MpdoAddressMode.Destination, Tpdo1);
        await StartAsync(wire, device);
        await Inject(device, Rpdo1, 0x82, 0x00, 0x20, 1, 0x34, 0x12, 0, 0);
        od.ReadUnsigned(0x2000, 1).Should().Be(0);
        await Inject(device, Rpdo1, (byte)(0x80 | Device), 0x00, 0x20, 1, 0x34, 0x12, 0, 0);
        od.ReadUnsigned(0x2000, 1).Should().Be(0x1234);
        await Inject(device, Rpdo1, 0x80, 0x00, 0x20, 1, 0x78, 0x56, 0, 0);
        od.ReadUnsigned(0x2000, 1).Should().Be(0x5678);
        await Inject(device, Rpdo1, 0x80, 0xFF, 0x2F, 0, 0, 0, 0, 0);
        await wire.WaitForCountAsync(CanOpenCobId.Emcy(Device), 1);
        wire.Payloads(CanOpenCobId.Emcy(Device))[0][0].Should().Be(0x30);
        await device.Pdo().SendDestinationMpdoAsync(1, 2, 0x2100, 3, new byte[] { 0xAA, 0xBB });
        await wire.WaitForCountAsync(Tpdo1, 1);
        wire.Payloads(Tpdo1)[0].Should().Equal(0x82, 0x00, 0x21, 3, 0xAA, 0xBB, 0, 0);
    }

    [Fact]
    public async Task Source_Mpdo_Uses_Scanner_And_Dispatcher_And_Has_One_Producer()
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var wire = new Wire(session, 2);
        using var device = CanOpen.OpenNode(bus, Device);
        var od = device.ObjectDictionary;
        od.AddU8(0x2000, 1, 42);
        od.AddU8(0x2000, 2, 43);
        od.AddU16(0x2100, 11, 0);
        od.AddU16(0x2100, 12, 0);
        od.AddU8(0x1FA0, 0, 1);
        od.AddU32(0x1FA0, 1, 0x02200001); // two consecutive subindices
        od.AddU8(0x1FD0, 0, 1);
        od.AddRaw(0x1FD0, 1, OdDataType.Unsigned64, new byte[] { 2, 1, 0, 0x22, 11, 0, 0x21, 2 });
        device.Pdo().ConfigureMultiplexedTpdo(1, MpdoAddressMode.Source, Tpdo1);
        device.Pdo().ConfigureMultiplexedRpdo(1, MpdoAddressMode.Source, Rpdo1);
        Action second = () => device.Pdo().ConfigureMultiplexedTpdo(2, MpdoAddressMode.Source, Tpdo2);
        second.Should().Throw<ArgumentException>();
        Action synchronous = () => od.WriteUnsigned(0x1800, 2, 1);
        synchronous.Should().Throw<ArgumentException>();
        Action timer = () => od.WriteUnsigned(0x1800, 5, 10);
        timer.Should().Throw<ArgumentException>();
        await StartAsync(wire, device);
        await device.TriggerTpdoAsync(1);
        await wire.WaitForCountAsync(Tpdo1, 2);
        wire.Payloads(Tpdo1).Should().Contain(p => p[3] == 1 && p[4] == 42);
        wire.Payloads(Tpdo1).Should().Contain(p => p[3] == 2 && p[4] == 43);
        od.WriteUnsigned(0x2000, 2, 99);
        await wire.WaitForCountAsync(Tpdo1, 3);
        wire.Payloads(Tpdo1)[2].Should().Equal(Device, 0, 0x20, 2, 99, 0, 0, 0);
        await Inject(device, Rpdo1, 2, 0, 0x22, 2, 0x34, 0x12, 0, 0);
        od.ReadUnsigned(0x2100, 12).Should().Be(0x1234);
        od.ReadUnsigned(0x2100, 11).Should().Be(0);
    }
    [Theory]
    [InlineData(OdDataType.Integer24, 3)]
    [InlineData(OdDataType.Integer40, 5)]
    [InlineData(OdDataType.Integer48, 6)]
    [InlineData(OdDataType.Integer56, 7)]
    [InlineData(OdDataType.Unsigned24, 3)]
    [InlineData(OdDataType.Unsigned40, 5)]
    [InlineData(OdDataType.Unsigned48, 6)]
    [InlineData(OdDataType.Unsigned56, 7)]
    public async Task Native_Integer_Widths_Are_Mapped_Without_Padding(OdDataType type, int width)
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var wire = new Wire(session, 2);
        using var device = CanOpen.OpenNode(bus, Device);
        var value = new byte[width];
        for (int i = 0; i < width; i++) value[i] = (byte)(0xF0 + i);
        device.ObjectDictionary.AddRaw(0x2000, 0, type, value);
        var mapping = new PdoMapping().Add(0x2000, 0, (byte)(width * 8));
        device.ConfigureTpdo(1, mapping);
        device.ConfigureRpdo(1, mapping);
        await StartAsync(wire, device);
        await device.TriggerTpdoAsync(1);
        await wire.WaitForCountAsync(Tpdo1, 1);
        wire.Payloads(Tpdo1)[0].Should().Equal(value);
        await Inject(device, Rpdo1, new byte[width]);
        device.ObjectDictionary.ReadRaw(0x2000, 0).Should().OnlyContain(b => b == 0);
    }

    [Fact]
    public async Task Optionally_Mappable_Communication_Status_Objects_Can_Be_Transmitted()
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var wire = new Wire(session, 2);
        using var device = CanOpen.OpenNode(bus, Device);
        var od = device.ObjectDictionary;
        od.WriteUnsigned(0x1001, 0, 0x11);
        od.AddU32(0x1002, 0, 0x12345678, OdAccess.ReadOnly);
        device.ConfigureTpdo(1, new PdoMapping().Add(0x1001, 0, 8).Add(0x1002, 0, 32));
        await StartAsync(wire, device);
        await device.TriggerTpdoAsync(1);
        await wire.WaitForCountAsync(Tpdo1, 1);
        wire.Payloads(Tpdo1)[0].Should().Equal(0x11, 0x78, 0x56, 0x34, 0x12);
        Action receiveReadOnly = () => device.ConfigureRpdo(1, new PdoMapping().Add(0x1002, 0, 32));
        receiveReadOnly.Should().Throw<ArgumentException>();
    }

}
