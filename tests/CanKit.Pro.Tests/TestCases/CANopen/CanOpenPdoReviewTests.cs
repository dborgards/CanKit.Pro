using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Pdo;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

public partial class CanOpenPdoEngineTests
{
    // FR-CO-048: start gates the initial sequence only; periods continue across overflow.
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Sync_Start_Continues_Every_Nth_Sync_After_The_First_Match(byte type)
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var wire = new Wire(session, 2);
        using var node = CanOpen.OpenNode(bus, Device);
        node.ObjectDictionary.AddU8(0x2000, 0, 42);
        node.ObjectDictionary.WriteUnsigned(0x1019, 0, 8);
        node.ConfigureTpdo(1, new PdoMapping().Add(0x2000, 0, 8), TpdoTransmission.Synchronous);
        SetSyncStart(node, 5, type);
        await StartAsync(wire, node);
        int sent = 0;
        for (int sequence = 0; sequence < 15; sequence++)
        {
            await Inject(node, CanOpenCobId.Sync, (byte)((sequence + 4) % 8 + 1));
            if ((sequence + 1) % type == 0) await wire.WaitForCountAsync(Tpdo1, ++sent);
        }
        wire.Count(Tpdo1).Should().Be(15 / type);
    }

    [Fact]
    public async Task Scanner_Change_Is_Visible_Before_The_Actor_Rebuild()
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var wire = new Wire(session, 2);
        using var node = CanOpen.OpenNode(bus, Device);
        var od = node.ObjectDictionary;
        od.AddU8(0x2000, 0, 1);
        od.AddU8(0x2001, 0, 2);
        od.AddU8(0x1FA0, 0, 1);
        od.AddU32(0x1FA0, 1, 0x01200000);
        node.Pdo().ConfigureMultiplexedTpdo(1, MpdoAddressMode.Source, Tpdo1);
        await StartAsync(wire, node);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = ((CanOpenNode)node).PostToActorAsync(() =>
        {
            entered.SetResult(true);
            release.Wait(ShortTimeout);
        });
        try
        {
            await entered.Task.WithTimeoutAsync(ShortTimeout);
            od.WriteUnsigned(0x1FA0, 1, 0x01200100);
            od.WriteUnsigned(0x2001, 0, 99);
        }
        finally { release.Set(); }
        await blocked;
        await wire.WaitForCountAsync(Tpdo1, 1);
        wire.Payloads(Tpdo1)[0].Should().Equal(Device, 1, 0x20, 0, 99, 0, 0, 0);
    }

    [Fact]
    public async Task Mpdo_Emergencies_Are_Latched_And_Rearmed_By_Recovery()
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var wire = new Wire(session, 2);
        using var node = CanOpen.OpenNode(bus, Device);
        node.ObjectDictionary.AddU8(0x2000, 0, 0);
        node.Pdo().ConfigureMultiplexedRpdo(1, MpdoAddressMode.Destination, Rpdo1);
        await StartAsync(wire, node);
        uint emcy = CanOpenCobId.Emcy(Device);
        for (int i = 0; i < 10; i++) await Inject(node, Rpdo1, 0x80);
        await wire.WaitForCountAsync(emcy, 1);
        for (int i = 0; i < 10; i++) await Inject(node, Rpdo1, 0x80, 0xFF, 0x2F, 0, 0, 0, 0, 0);
        await wire.WaitForCountAsync(emcy, 2);
        await Inject(node, Rpdo1, 0x80, 0, 0x20, 0, 42, 0, 0, 0);
        node.ObjectDictionary.ReadUnsigned(0x2000, 0).Should().Be(42);
        await Inject(node, Rpdo1, 0x80);
        await wire.WaitForCountAsync(emcy, 3);
        await Inject(node, Rpdo1, 0x80, 0xFF, 0x2F, 0, 0, 0, 0, 0);
        await wire.WaitForCountAsync(emcy, 4);
        wire.Payloads(emcy).Select(p => p[0]).Should().BeEquivalentTo(new byte[] { 0x10, 0x30, 0x10, 0x30 });
    }

    [Fact]
    public async Task Sync_Length_Emergency_Is_Latched_Until_A_Valid_Sync()
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var wire = new Wire(session, 2);
        using var node = CanOpen.OpenNode(bus, Device);
        await StartAsync(wire, node);
        for (int i = 0; i < 10; i++) await Inject(node, CanOpenCobId.Sync, 1);
        await wire.WaitForCountAsync(CanOpenCobId.Emcy(Device), 1);
        await Inject(node, CanOpenCobId.Sync);
        await Inject(node, CanOpenCobId.Sync, 1);
        await wire.WaitForCountAsync(CanOpenCobId.Emcy(Device), 2);
        wire.Count(CanOpenCobId.Emcy(Device)).Should().Be(2);
    }

    [Fact]
    public async Task Stalled_Sync_Confirmation_Does_Not_Accumulate_Periodic_Ticks()
    {
        using var service = new StarvedReaderBusService();
        var clock = new ManualTimeSource();
        using var node = new CanOpenNode(service, Device, new CanOpenNodeOptions(), ownsService: false, clock);
        node.ObjectDictionary.WriteUnsigned(0x1019, 0, 8);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.ConfirmSendAsync = (frame, _) =>
        {
            if (frame.ID == CanOpenCobId.Sync && frame.Data.Span[0] == 1 && entered.TrySetResult(true))
                release.Wait(ShortTimeout);
            return Task.FromResult(new CanKit.Pro.RawCan.TxConfirmation { Confirmed = true });
        };
        try
        {
            node.StartSyncProducer(TimeSpan.FromMilliseconds(10));
            Advance(clock, node, TimeSpan.FromMilliseconds(10));
            await entered.Task.WithTimeoutAsync(ShortTimeout);
            for (int i = 0; i < 20; i++) Advance(clock, node, TimeSpan.FromMilliseconds(10));
            release.Set();
            await node.SendSyncAsync(); // completion barrier behind any erroneously queued ticks
            lock (service.Sent) service.Sent.Where(p => p.Length == 1 && p[0] != 0).Select(p => p[0]).Should().Equal(1, 2);
        }
        finally { release.Set(); }
    }

    [Theory]
    [InlineData(0x50)]
    [InlineData(0x110)]
    [InlineData(0x610)]
    [InlineData(0x780)]
    public async Task Extended_Pdo_Ids_Do_Not_Inherit_Base_Frame_Restrictions(uint id)
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var wire = new Wire(session, 2);
        using var node = CanOpen.OpenNode(bus, Device);
        node.ObjectDictionary.AddU8(0x2000, 0, 42);
        uint cobId = CanOpenCobId.ExtendedFrameBit | id;
        node.ConfigureTpdo(1, new PdoMapping().Add(0x2000, 0, 8), cobId: cobId);
        await StartAsync(wire, node);
        await node.TriggerTpdoAsync(1);
        await wire.WaitForCountAsync(cobId, 1);
        wire.Payloads(cobId)[0].Should().Equal(42);
    }

    [Fact]
    public async Task Dispatcher_Changes_Are_Applied_Without_Reconfiguring_The_Rpdo()
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var wire = new Wire(session, 2);
        using var node = CanOpen.OpenNode(bus, Device);
        var od = node.ObjectDictionary;
        od.AddU8(0x2100, 1, 0);
        od.AddU8(0x2100, 2, 0);
        od.AddU8(0x1FD0, 0, 1);
        od.AddRaw(0x1FD0, 1, OdDataType.Unsigned64, new byte[] { 2, 1, 0, 0x22, 1, 0, 0x21, 1 });
        node.Pdo().ConfigureMultiplexedRpdo(1, MpdoAddressMode.Source, Rpdo1);
        await StartAsync(wire, node);
        await Inject(node, Rpdo1, 2, 0, 0x22, 1, 42, 0, 0, 0);
        od.WriteRaw(0x1FD0, 1, new byte[] { 2, 1, 0, 0x22, 2, 0, 0x21, 1 });
        await Inject(node, Rpdo1, 2, 0, 0x22, 1, 99, 0, 0, 0);
        od.ReadUnsigned(0x2100, 1).Should().Be(42);
        od.ReadUnsigned(0x2100, 2).Should().Be(99);
    }

    [Fact]
    public async Task Fd_Heartbeat_Still_Reaches_The_Consumer_While_Fd_Pdo_Is_Ignored()
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var wire = new Wire(session, 2);
        using var node = CanOpen.OpenNode(bus, Device);
        node.ObjectDictionary.AddU8(0x2000, 0, 0);
        node.ConfigureRpdo(1, new PdoMapping().Add(0x2000, 0, 8));
        var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        node.HeartbeatReceived += (_, e) => { if (e.ProducerNodeId == 2) received.TrySetResult(true); };
        await StartAsync(wire, node);
        await ((CanOpenNode)node).PostToActorAsync(() =>
        {
            var method = typeof(CanOpenNode).GetMethod("HandleIncomingFrame", BindingFlags.Instance | BindingFlags.NonPublic)!;
            method.Invoke(node, new object[] { Rpdo1, new byte[] { 42 }, false, true });
            method.Invoke(node, new object[] { CanOpenCobId.Heartbeat(2), new byte[] { 5 }, false, true });
        });
        await received.Task.WithTimeoutAsync(ShortTimeout);
        node.ObjectDictionary.ReadUnsigned(0x2000, 0).Should().Be(0);
    }

    [Theory]
    [InlineData(254, false)]
    [InlineData(255, false)]
    [InlineData(254, true)]
    [InlineData(255, true)]
    public async Task Device_Description_Loads_Mpdo_Modes(byte mode, bool dcf)
    {
        var description = CanOpenDeviceDescription.Load(PdoFixture(dcf ? "device.dcf" : "device.eds"));
        if (dcf)
        {
            description.Objects.Objects[0x1600].SubObjects[0].ParameterValue = mode.ToString();
            description.Objects.Objects[0x1A00].SubObjects[0].ParameterValue = mode.ToString();
            description.Objects.Objects[0x1800].SubObjects[5].ParameterValue = "0";
        }
        else
        {
            description.Objects.Objects[0x1600].SubObjects[0].DefaultValue = mode.ToString();
            description.Objects.Objects[0x1A00].SubObjects[0].DefaultValue = mode.ToString();
            description.Objects.Objects[0x1800].SubObjects[5].DefaultValue = "0";
        }
        var session = NewSession();
        using var bus = Open(session, 0);
        using var node = CanOpen.OpenNode(bus, Device, description);
        node.ObjectDictionary.ReadUnsigned(0x1600, 0).Should().Be(mode);
        node.ObjectDictionary.ReadUnsigned(0x1A00, 0).Should().Be(mode);
        (node.ObjectDictionary.ReadUnsigned(0x1800, 1) & CanOpenCobId.InvalidBit).Should().Be(0);
        (node.ObjectDictionary.ReadUnsigned(0x1400, 1) & CanOpenCobId.InvalidBit).Should().Be(0);
        node.DeviceDescription!.Findings.Should().NotContain(f => f.Outcome == DeviceDescriptionOutcome.PdoDisabled);
    }
    [Theory]
    [InlineData(OdDataType.Integer24, 3, "-2")]
    [InlineData(OdDataType.Integer40, 5, "-2")]
    [InlineData(OdDataType.Integer48, 6, "-2")]
    [InlineData(OdDataType.Integer56, 7, "-2")]
    [InlineData(OdDataType.Unsigned24, 3, "0x123456")]
    [InlineData(OdDataType.Unsigned40, 5, "0x123456789A")]
    [InlineData(OdDataType.Unsigned48, 6, "0x123456789ABC")]
    [InlineData(OdDataType.Unsigned56, 7, "0x123456789ABCDE")]
    public async Task Native_Integer_Widths_Load_From_Eds_And_Transmit(OdDataType type, int width, string value)
    {
        var description = CanOpenDeviceDescription.Load(PdoFixture("device.eds"));
        var entry = description.Objects.Objects[0x2000];
        entry.DataType = (ushort)type;
        entry.DefaultValue = value;
        description.Objects.Objects[0x1A00].SubObjects[1].DefaultValue = (0x20000000u | (uint)(width * 8)).ToString();
        description.Objects.Objects[0x1800].SubObjects[5].DefaultValue = "0";
        var session = NewSession();
        using var bus = Open(session, 0);
        using var wire = new Wire(session, 2);
        using var node = CanOpen.OpenNode(bus, Device, description);
        byte[] expected = type < OdDataType.Unsigned24
            ? Enumerable.Range(0, width).Select(i => i == 0 ? (byte)0xFE : (byte)0xFF).ToArray()
            : Enumerable.Range(0, width).Select(i => (byte)(Convert.ToUInt64(value.Substring(2), 16) >> (8 * i))).ToArray();
        node.ObjectDictionary.ReadRaw(0x2000, 0).Should().Equal(expected);
        await StartAsync(wire, node);
        await node.TriggerTpdoAsync(1);
        await wire.WaitForCountAsync(Tpdo1, 1);
        wire.Payloads(Tpdo1)[0].Should().Equal(expected);
    }

    [Fact]
    public async Task Source_Mpdo_Explicit_Send_And_Inhibit_Queue_Keep_Latest_Value_Per_Object()
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var wire = new Wire(session, 2);
        var clock = new ManualTimeSource();
        using var node = OpenClocked(bus, Device, clock);
        var od = node.ObjectDictionary;
        od.AddU8(0x2000, 0, 1);
        od.AddU8(0x2001, 0, 2);
        node.Pdo().ConfigureMultiplexedTpdo(1, MpdoAddressMode.Source, Tpdo1);
        od.WriteUnsigned(0x1800, 1, od.ReadUnsigned(0x1800, 1) | CanOpenCobId.InvalidBit);
        od.WriteUnsigned(0x1800, 3, 1000); // 100 ms
        od.WriteUnsigned(0x1800, 1, Tpdo1 | CanOpenCobId.NoRtrBit);
        await node.Pdo().SendSourceMpdoAsync(1, 0x2000, 0); // inactive in Pre-operational
        await StartAsync(wire, node);
        await node.Pdo().SendSourceMpdoAsync(1, 0x2000, 0);
        await wire.WaitForCountAsync(Tpdo1, 1);
        await node.Pdo().SendSourceMpdoAsync(1, 0x2000, 0);
        await node.Pdo().SendSourceMpdoAsync(1, 0x2001, 0);
        od.WriteUnsigned(0x2000, 0, 99);
        await node.Pdo().SendSourceMpdoAsync(1, 0x2000, 0);
        Advance(clock, node, TimeSpan.FromMilliseconds(100));
        await wire.WaitForCountAsync(Tpdo1, 2);
        wire.Payloads(Tpdo1)[1].Should().Equal(Device, 0, 0x20, 0, 99, 0, 0, 0);
        Advance(clock, node, TimeSpan.FromMilliseconds(100));
        await wire.WaitForCountAsync(Tpdo1, 3);
        wire.Payloads(Tpdo1)[2].Should().Equal(Device, 1, 0x20, 0, 2, 0, 0, 0);
        await Assert.ThrowsAsync<ArgumentException>(() => node.Pdo().SendSourceMpdoAsync(1, 0x2FFF, 0));
        await Assert.ThrowsAsync<InvalidOperationException>(() => node.Pdo().SendSourceMpdoAsync(2, 0x2000, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => node.Pdo().SendSourceMpdoAsync(0, 0x2000, 0));
        Action mode = () => node.Pdo().ConfigureMultiplexedTpdo(1, (MpdoAddressMode)1, Tpdo1);
        mode.Should().Throw<ArgumentOutOfRangeException>();
        Action number = () => node.Pdo().ConfigureMultiplexedRpdo(5, MpdoAddressMode.Source, Rpdo1);
        number.Should().Throw<ArgumentOutOfRangeException>();
        Action address = () => node.Pdo().SendDestinationMpdoAsync(1, 128, 0x2000, 0, new byte[] { 1 });
        address.Should().Throw<ArgumentOutOfRangeException>();
        Action size = () => node.Pdo().SendDestinationMpdoAsync(1, 1, 0x2000, 0, new byte[5]);
        size.Should().Throw<ArgumentOutOfRangeException>();
        Action noNode = () => CanOpenPdoExtensions.Pdo(null!);
        noNode.Should().Throw<ArgumentNullException>();
        Action option = () => CanOpen.OpenNode(bus, 2, new CanOpenNodeOptions { PdoCount = 513 });
        option.Should().Throw<ArgumentOutOfRangeException>();
    }

}
