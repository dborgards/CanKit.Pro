using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Pdo;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

public partial class CanOpenPdoEngineTests
{
    private static object? PdoCall(object node, string method, params object?[] arguments)
        => typeof(CanOpenNode).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(node, arguments);

    private static object PdoField(object node, string field)
        => typeof(CanOpenNode).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(node)!;

    [Fact]
    public void Pdo_Public_Bounds_And_Bit_Mapping_Bounds_Leave_The_Previous_Value_Intact()
    {
        using var service = new StarvedReaderBusService();
        using var node = new CanOpenNode(service, Device, new CanOpenNodeOptions { PdoCount = 5 }, false);
        var mapping = new PdoMapping();
        foreach (int number in new[] { -1, 0, 6 })
        {
            Action tx = () => node.ConfigureTpdo(number, mapping);
            Action rx = () => node.ConfigureRpdo(number, mapping);
            Action mpdo = () => node.ConfigureMultiplexedRpdo(number, MpdoAddressMode.Source, Rpdo1);
            tx.Should().Throw<ArgumentOutOfRangeException>();
            rx.Should().Throw<ArgumentOutOfRangeException>();
            mpdo.Should().Throw<ArgumentOutOfRangeException>();
        }
        Action noTxId = () => node.ConfigureTpdo(5, mapping);
        Action noRxId = () => node.ConfigureRpdo(5, mapping);
        noTxId.Should().Throw<ArgumentException>();
        noRxId.Should().Throw<ArgumentException>();
        foreach (byte width in new byte[] { 0, 65 })
        {
            Action entry = () => new PdoMappingEntry(0x2000, 0, width);
            entry.Should().Throw<ArgumentOutOfRangeException>();
            object boxed = default(PdoMappingEntry);
            typeof(PdoMappingEntry).GetField("<BitLength>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(boxed, width);
            Action add = () => mapping.Add((PdoMappingEntry)boxed);
            add.Should().Throw<ArgumentOutOfRangeException>();
            mapping.Entries.Should().BeEmpty();
        }
        for (int i = 0; i < 64; i++) mapping.Add(1, 0, 1);
        Action overflow = () => mapping.Add(1, 0, 1);
        overflow.Should().Throw<InvalidOperationException>().WithMessage("*64 entries*");
        mapping.TotalBits.Should().Be(64);
        mapping.Entries.Should().HaveCount(64);
        var wideMapping = new PdoMapping().Add(0x2000, 0, 64);
        Action bitsOverflow = () => wideMapping.Add(0x2001, 0, 1);
        bitsOverflow.Should().Throw<InvalidOperationException>().WithMessage("*8-byte*");
        wideMapping.TotalBits.Should().Be(64);
        wideMapping.Entries.Should().HaveCount(1);
        Enumerable.Range(0, 9).Select(i => PdoMappingEntry.DummyBitLength((ushort)i))
            .Should().Equal(0, 1, 8, 16, 32, 8, 16, 32, 0);
        var timestamp = new DateTime(2026, 10, 19);
        new SyncReceivedEventArgs(timestamp).Timestamp.Should().Be(timestamp);
        new SyncReceivedEventArgs(timestamp).Counter.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mpdo_Rejects_Incompatible_Creation_And_Recovers_By_Remapping(bool isTpdo)
    {
        using var service = new StarvedReaderBusService();
        using var node = new CanOpenNode(service, Device, new CanOpenNodeOptions(), false);
        var od = node.ObjectDictionary;
        ushort comm = isTpdo ? (ushort)0x1800 : (ushort)0x1400;
        ushort map = (ushort)(comm + 0x200);
        uint cob = isTpdo ? Tpdo1 : Rpdo1;
        od.WriteUnsigned(comm, 1, cob | CanOpenCobId.InvalidBit);
        od.WriteUnsigned(comm, 2, 1);
        od.WriteUnsigned(map, 0, 254);
        Action create = () => od.WriteUnsigned(comm, 1, cob);
        create.Should().Throw<ArgumentException>().WithMessage("*06040043*");
        od.WriteUnsigned(comm, 2, 254);
        if (isTpdo)
        {
            // The event timer was written before the mapping became MPDO.
            od.WriteUnsigned(map, 0, 0);
            od.WriteUnsigned(comm, 5, 10);
            od.WriteUnsigned(map, 0, 255);
            create.Should().Throw<ArgumentException>().WithMessage("*06040043*");
            od.WriteUnsigned(comm, 5, 0);
        }
        od.WriteUnsigned(comm, 1, cob);
        if (isTpdo) node.ConfigureMultiplexedTpdo(1, MpdoAddressMode.Destination, Tpdo2);
        else node.ConfigureMultiplexedRpdo(1, MpdoAddressMode.Destination, Tpdo2);
        od.ReadUnsigned(comm, 1).Should().Be(Tpdo2 | (isTpdo ? CanOpenCobId.NoRtrBit : 0));
        await Assert.ThrowsAsync<InvalidOperationException>(() => node.SendSourceMpdoAsync(1, 0x2000, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => node.SendSourceMpdoAsync(6, 0x2000, 0));
        Action empty = () => node.SendDestinationMpdoAsync(1, Device, 0x2000, 0, ReadOnlyMemory<byte>.Empty);
        empty.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Mapping_Count_Revalidates_Total_Bits_And_A_Slot_Needs_No_Predecessor()
    {
        using var service = new StarvedReaderBusService();
        using var node = new CanOpenNode(service, Device, new CanOpenNodeOptions(), false);
        var od = node.ObjectDictionary;
        od.AddRaw(0x2000, 0, OdDataType.Unsigned64, new byte[8]);
        od.AddU32(0x2001, 0, 0);
        od.WriteUnsigned(0x1600, 2, 0x20010020);
        od.WriteUnsigned(0x1600, 1, 0x20000040);
        Action count = () => od.WriteUnsigned(0x1600, 0, 2);
        count.Should().Throw<ArgumentException>().WithMessage("*06040042*");
        od.ReadUnsigned(0x1600, 0).Should().Be(0);
        od.Remove(0x1600, 1);
        od.WriteUnsigned(0x1600, 2, 0x20010020);
        od.ReadUnsigned(0x1600, 2).Should().Be(0x20010020);
        foreach (uint raw in new uint[] { 0x20010000, 0x20010041 })
            od.Invoking(d => d.WriteUnsigned(0x1600, 2, raw)).Should().Throw<ArgumentException>().WithMessage("*06070010*");
        await node.PostToActorAsync(() => { });
    }

    [Fact]
    public async Task Mpdo_Dispatcher_Rejects_Mismatches_And_Subindex_Overflow()
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var wire = new Wire(session, 2);
        using var node = CanOpen.OpenNode(bus, Device);
        var od = node.ObjectDictionary;
        od.AddU8(0x2100, 255, 0);
        od.AddU8(0x1FD0, 0, 1);
        od.AddRaw(0x1FD0, 1, OdDataType.Unsigned64, new byte[] { 2, 5, 0, 0x22, 255, 0, 0x21, 2 });
        node.Pdo().ConfigureMultiplexedRpdo(1, MpdoAddressMode.Source, Rpdo1);
        await StartAsync(wire, node);
        foreach (var payload in new[] {
            new byte[] { 0x80, 0, 0x22, 5, 9, 0, 0, 0 }, // DAM on a SAM consumer
            new byte[] { 0, 0, 0x22, 5, 9, 0, 0, 0 }, // invalid SAM source
            new byte[] { 3, 0, 0x22, 5, 9, 0, 0, 0 }, // another source
            new byte[] { 2, 1, 0x22, 5, 9, 0, 0, 0 }, // another index
            new byte[] { 2, 0, 0x22, 4, 9, 0, 0, 0 }, // before the range
            new byte[] { 2, 0, 0x22, 7, 9, 0, 0, 0 }, // after the range
            new byte[] { 2, 0, 0x22, 6, 9, 0, 0, 0 } }) // local subindex would be 256
            await Inject(node, Rpdo1, payload);
        od.ReadUnsigned(0x2100, 255).Should().Be(0);
        await Inject(node, Rpdo1, 2, 0, 0x22, 5, 42, 0, 0, 0);
        od.ReadUnsigned(0x2100, 255).Should().Be(42);
    }

    [Theory]
    [InlineData(0x1003, 0, true, OdAccess.ReadWrite, 1)]
    [InlineData(0x1002, 0, false, OdAccess.ReadWrite, 1)]
    [InlineData(0x2000, 0, false, OdAccess.ReadWrite, 1)]
    [InlineData(0x2000, 0, true, OdAccess.WriteOnly, 1)]
    [InlineData(0x2000, 0, true, OdAccess.ReadWrite, 0)]
    [InlineData(0x2000, 0, true, OdAccess.ReadWrite, 5)]
    public async Task Mpdo_Sources_Require_A_Mappable_Readable_Scalar(ushort index, byte subindex, bool mappable, OdAccess access, int width)
    {
        using var service = new StarvedReaderBusService();
        using var node = new CanOpenNode(service, Device, new CanOpenNodeOptions(), false);
        node.ObjectDictionary.AddRaw(index, subindex, OdDataType.Domain, new byte[width], access, pdoMappable: mappable);
        node.ConfigureMultiplexedTpdo(1, MpdoAddressMode.Destination, Tpdo1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => node.SendSourceMpdoAsync(1, index, subindex));
        node.ConfigureMultiplexedTpdo(1, MpdoAddressMode.Source, Tpdo1);
        await Assert.ThrowsAsync<ArgumentException>(() => node.SendSourceMpdoAsync(1, index, subindex));
    }
    [Fact]
    public void Optional_Communication_Targets_Match_The_CiA_Table()
    {
        var classify = typeof(CanOpenNode).GetMethod("IsCommunicationPdoTarget", BindingFlags.Static | BindingFlags.NonPublic)!;
        var rows = new (ushort Index, byte[] Allowed)[]
        {
            (0x1001, new byte[] { 0 }), (0x1002, new byte[] { 0 }), (0x1013, new byte[] { 0 }),
            (0x1026, new byte[] { 1, 2, 3 }), (0x1200, new byte[] { 1, 2 }), (0x12FF, new byte[] { 1, 2 }),
            (0x1003, Array.Empty<byte>()), (0x1100, Array.Empty<byte>()), (0x1300, Array.Empty<byte>()),
        };
        foreach (var row in rows)
            foreach (byte sub in new byte[] { 0, 1, 2, 3, 4 })
                ((bool)classify.Invoke(null, new object[] { row.Index, sub })!).Should().Be(row.Allowed.Contains(sub));
        var padded = PdoBits.Extract(new byte[] { 0xA5 }, 4, 12);
        padded.Should().Equal(0x0A, 0);
        new PdoMappingEntry(1, 1, 1).IsDummy.Should().BeFalse();
        new PdoMappingEntry(0, 0, 1).IsDummy.Should().BeFalse();
        new PdoMappingEntry(8, 0, 1).IsDummy.Should().BeFalse();
    }

    [Fact]
    public void Options_Cloning_Keeps_Both_Lss_And_Pdo_Count()
    {
        var lss = new CanKit.Pro.CANopen.Lss.LssSlaveOptions();
        var original = new CanOpenNodeOptions { PdoCount = 8, Lss = lss };
        var derived = original.With(pdoCount: 512);
        derived.Lss.Should().BeSameAs(lss);
        derived.WithLss(null).PdoCount.Should().Be(512);
        derived.WithLss(null).Lss.Should().BeNull();
        original.PdoCount.Should().Be(8);
    }

    [Fact]
    public async Task Lss_Suppression_Does_Not_Consume_A_Sync_Counter()
    {
        using var service = new StarvedReaderBusService();
        using var node = new CanOpenNode(service, Device, new CanOpenNodeOptions(), false);
        node.ObjectDictionary.WriteUnsigned(0x1019, 0, 3);
        await node.PostToActorAsync(() => { });
        var silent = typeof(CanOpenNode).GetField("_lssSilent", BindingFlags.Instance | BindingFlags.NonPublic)!;
        silent.SetValue(node, 1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => node.SendSyncAsync(new System.Threading.CancellationToken(true)));
        await node.SendSyncAsync();
        silent.SetValue(node, 0);
        await node.SendSyncAsync();
        lock (service.Sent) service.Sent.Where(p => p.Length == 1 && p[0] != 0).Should().ContainSingle().Which.Should().Equal(1);
    }

    [Fact]
    public async Task A_Sync_Confirmation_After_Dispose_Finishes_Its_Caller()
    {
        using var service = new StarvedReaderBusService();
        using var node = new CanOpenNode(service, Device, new CanOpenNodeOptions(), false);
        node.ObjectDictionary.WriteUnsigned(0x1019, 0, 3);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirm = new TaskCompletionSource<CanKit.Pro.RawCan.TxConfirmation>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.ConfirmSendAsync = (frame, _) =>
        {
            if (frame.ID != CanOpenCobId.Sync) return Task.FromResult(new CanKit.Pro.RawCan.TxConfirmation { Confirmed = true });
            entered.TrySetResult(true);
            return confirm.Task;
        };
        var pending = node.SendSyncAsync();
        await entered.Task.WithTimeoutAsync(ShortTimeout);
        node.Dispose();
        confirm.TrySetResult(new CanKit.Pro.RawCan.TxConfirmation { Confirmed = true });
        await pending.WithTimeoutAsync(ShortTimeout);
    }

    [Fact]
    public async Task Pdo_Configuration_Tolerates_Absent_Optional_Communication_Entries()
    {
        using var service = new StarvedReaderBusService();
        using var node = new CanOpenNode(service, Device, new CanOpenNodeOptions(), false);
        var od = node.ObjectDictionary;
        foreach (ushort comm in new ushort[] { 0x1400, 0x1800 })
        {
            od.Remove(comm, 3);
            od.Remove(comm, 5);
            od.Remove(comm, 6);
        }
        node.ConfigureMultiplexedTpdo(1, MpdoAddressMode.Source, Tpdo1);
        node.ConfigureMultiplexedRpdo(1, MpdoAddressMode.Source, Rpdo1);
        od.WriteUnsigned(0x1800, 1, Tpdo1 | CanOpenCobId.InvalidBit);
        od.Remove(0x1800, 2);
        od.WriteUnsigned(0x1800, 1, Tpdo1);
        od.WriteUnsigned(0x1800, 1, Tpdo1 | CanOpenCobId.InvalidBit);
        od.Remove(0x1A00, 0);
        od.WriteUnsigned(0x1800, 1, Tpdo1);
        od.Remove(0x1800, 1);
        od.AddU8(0x2000, 0, 0);
        await node.PostToActorAsync(() => PdoCall(node, "RefreshExtendedPdoFilter"));
        node.ConfigureRpdo(2, new PdoMapping().Add(0x2000, 0, 8));
        await node.PostToActorAsync(() => { });
        od.ReadUnsigned(0x1401, 1).Should().Be(CanOpenCobId.RpdoDefault(Device, 2));
    }

    [Fact]
    public async Task Missing_And_Disabled_Pdo_Runtimes_Do_Not_Transmit()
    {
        using var service = new StarvedReaderBusService();
        using var node = new CanOpenNode(service, Device, new CanOpenNodeOptions(), false);
        await Inject(node, CanOpenCobId.NmtCommand, (byte)NmtCommand.Start, Device);
        var tx = (Array)PdoField(node, "_tpdos");
        var rx = (Array)PdoField(node, "_rpdos");
        await node.PostToActorAsync(() =>
        {
            tx.SetValue(null, 4);
            rx.SetValue(null, 4);
            PdoCall(node, "TriggerTpdoOnActor", 0);
            PdoCall(node, "TriggerTpdoOnActor", 5);
            PdoCall(node, "TriggerTpdoOnActor", 4);
            PdoCall(node, "TriggerTpdoOnActor", 3);
            PdoCall(node, "HandleTpdoRtr", tx.GetValue(3));
            PdoCall(node, "HandleIncomingFrame", Tpdo1, Array.Empty<byte>(), true, true);
            PdoCall(node, "OnEnterOperational");
            PdoCall(node, "OnLeaveOperational");
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => node.SendSourceMpdoAsync(4, 0x2000, 0));
        lock (service.Sent) service.Sent.Where(p => p.Length > 1).Should().BeEmpty();
    }

    [Fact]
    public async Task Disabled_Emcy_Does_Not_Report_Mpdo_Length_Or_Target_Errors()
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var wire = new Wire(session, 2);
        using var node = CanOpen.OpenNode(bus, Device);
        node.ObjectDictionary.WriteUnsigned(0x1014, 0, CanOpenCobId.InvalidBit | CanOpenCobId.Emcy(Device));
        node.Pdo().ConfigureMultiplexedRpdo(1, MpdoAddressMode.Destination, Rpdo1);
        await StartAsync(wire, node);
        await Inject(node, Rpdo1, 0x80);
        await Inject(node, Rpdo1, 0x80, 0, 0x22, 0, 9, 0, 0, 0);
        await Inject(node, Rpdo1, 1, 0, 0x22, 0, 9, 0, 0, 0);
        await Inject(node, CanOpenCobId.Sync, 1); // bad DLC with EMCY disabled
        wire.Count(CanOpenCobId.Emcy(Device)).Should().Be(0);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task A_Periodic_Sync_Rechecks_Producer_State_At_The_Driver(int retirement)
    {
        using var service = new StarvedReaderBusService();
        var clock = new ManualTimeSource();
        using var node = new CanOpenNode(service, Device, new CanOpenNodeOptions(), false, clock);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int syncs = 0;
        service.ConfirmSendAsync = (frame, _) =>
        {
            if (frame.ID == CanOpenCobId.Sync) System.Threading.Interlocked.Increment(ref syncs);
            return Task.FromResult(new CanKit.Pro.RawCan.TxConfirmation { Confirmed = true });
        };
        node.ObjectDictionary.WriteUnsigned(0x1019, 0, 3);
        node.BeforeControlFrameSendForTests = id =>
        {
            if (id != CanOpenCobId.Sync) return Task.CompletedTask;
            entered.TrySetResult(true);
            return release.Task;
        };
        node.StartSyncProducer(TimeSpan.FromMilliseconds(10));
        Advance(clock, node, TimeSpan.FromMilliseconds(10));
        await entered.Task.WithTimeoutAsync(ShortTimeout);
        var disposed = typeof(CanOpenNode).GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            if (retirement == 0) node.StopSyncProducer();
            if (retirement == 1) await Inject(node, CanOpenCobId.NmtCommand, (byte)NmtCommand.Stop, Device);
            if (retirement == 2) node.StartSyncProducer(TimeSpan.FromMilliseconds(20));
            if (retirement == 3) disposed.SetValue(node, 1);
            if (retirement == 4) typeof(CanOpenNode).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(node, NmtState.Initializing);
            if (retirement < 3) await node.PostToActorAsync(() => { });
            release.TrySetResult(true);
            await ((Task)PdoField(node, "_syncSendChain")).WithTimeoutAsync(ShortTimeout);
            syncs.Should().Be(0);
        }
        finally { disposed.SetValue(node, 0); release.TrySetResult(true); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task A_Synchronous_Pdo_Rechecks_Its_Window_And_Operational_Period(int retirement)
    {
        using var service = new StarvedReaderBusService();
        var clock = new ManualTimeSource();
        using var node = new CanOpenNode(service, Device, new CanOpenNodeOptions(), false, clock);
        node.ObjectDictionary.AddU8(0x2000, 0, 42);
        node.ConfigureTpdo(1, new PdoMapping().Add(0x2000, 0, 8),
            retirement == 4 ? TpdoTransmission.RtrOnlySynchronous : TpdoTransmission.Synchronous);
        node.ObjectDictionary.WriteUnsigned(0x1007, 0, 1000);
        await Inject(node, CanOpenCobId.NmtCommand, (byte)NmtCommand.Start, Device);
        var sent = new System.Collections.Concurrent.ConcurrentQueue<byte>();
        service.ConfirmSendAsync = (frame, _) =>
        {
            if (frame.ID == Tpdo1) sent.Enqueue(frame.Data.Span[0]);
            return Task.FromResult(new CanKit.Pro.RawCan.TxConfirmation { Confirmed = true });
        };
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        node.BeforeControlFrameSendForTests = id =>
        {
            if (id != Tpdo1) return Task.CompletedTask;
            entered.TrySetResult(true);
            return release.Task;
        };
        await Inject(node, CanOpenCobId.Sync);
        if (retirement == 4)
            await node.PostToActorAsync(() => PdoCall(node, "HandleIncomingFrame", Tpdo1, Array.Empty<byte>(), true, false));
        await entered.Task.WithTimeoutAsync(ShortTimeout);
        var disposed = typeof(CanOpenNode).GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            if (retirement == 0) await Inject(node, CanOpenCobId.NmtCommand, (byte)NmtCommand.EnterPreOperational, Device);
            if (retirement == 1)
            {
                node.ObjectDictionary.WriteUnsigned(0x2000, 0, 99);
                await Inject(node, CanOpenCobId.Sync);
            }
            if (retirement == 2 || retirement == 4) Advance(clock, node, TimeSpan.FromMilliseconds(2));
            if (retirement == 3) disposed.SetValue(node, 1);
            release.TrySetResult(true);
            // All accepted reservations end before the final assertion; no wall-clock negative.
            await Task.Run(async () =>
            {
                while (node.HasLssTransmitsInFlight) await Task.Yield();
            }).WithTimeoutAsync(ShortTimeout);
            sent.Should().Equal(retirement == 1 ? new byte[] { 99 } : Array.Empty<byte>());
        }
        finally { disposed.SetValue(node, 0); release.TrySetResult(true); }
    }
    [Fact]
    public async Task Rtr_And_Event_Requests_Keep_Their_Transmission_Semantics_With_A_Window()
    {
        using var service = new StarvedReaderBusService();
        var clock = new ManualTimeSource();
        using var node = new CanOpenNode(service, Device, new CanOpenNodeOptions(), false, clock);
        node.ObjectDictionary.AddU8(0x2000, 0, 42);
        node.ObjectDictionary.WriteUnsigned(0x1007, 0, 1000);
        await Inject(node, CanOpenCobId.NmtCommand, (byte)NmtCommand.Start, Device);
        node.ConfigureTpdo(1, new PdoMapping().Add(0x2000, 0, 8), TpdoTransmission.RtrOnlySynchronous);
        await node.PostToActorAsync(() => PdoCall(node, "HandleIncomingFrame", Tpdo1, Array.Empty<byte>(), true, false));
        await Inject(node, CanOpenCobId.Sync);
        Advance(clock, node, TimeSpan.FromMilliseconds(2));
        await node.PostToActorAsync(() =>
        {
            PdoCall(node, "HandleIncomingFrame", Tpdo1, Array.Empty<byte>(), true, false);
            PdoCall(node, "EmitTpdo", ((Array)PdoField(node, "_tpdos")).GetValue(1));
        });
        node.ConfigureTpdo(1, new PdoMapping().Add(0x2000, 0, 8), TpdoTransmission.EventDriven);
        await node.TriggerTpdoAsync(1);
        await Task.Run(async () => { while (node.HasLssTransmitsInFlight) await Task.Yield(); }).WithTimeoutAsync(ShortTimeout);
        lock (service.Sent) service.Sent.Where(p => p.Length == 1 && p[0] == 42).Should().ContainSingle();
        node.ConfigureMultiplexedTpdo(1, MpdoAddressMode.Destination, Tpdo1);
        node.ObjectDictionary.WriteUnsigned(0x1800, 1, Tpdo1); // even when the RTR flag is allowed, MPDOs do not answer
        await node.TriggerTpdoAsync(1);
        await node.PostToActorAsync(() => PdoCall(node, "HandleIncomingFrame", Tpdo1, Array.Empty<byte>(), true, false));
        lock (service.Sent) service.Sent.Where(p => p.Length == 8).Should().BeEmpty();
    }

    [Fact]
    public async Task Rpdo_Reconfiguration_And_Dispose_Cancel_A_Receive_Deadline()
    {
        using var service = new StarvedReaderBusService();
        var clock = new ManualTimeSource();
        using var node = new CanOpenNode(service, Device, new CanOpenNodeOptions(), false, clock);
        node.ObjectDictionary.AddU8(0x2000, 0, 0);
        node.ConfigureRpdo(1, new PdoMapping().Add(0x2000, 0, 8));
        node.ObjectDictionary.WriteUnsigned(0x1400, 5, 10);
        int timeouts = 0;
        node.RpdoTimeout += (_, _) => System.Threading.Interlocked.Increment(ref timeouts);
        await Inject(node, CanOpenCobId.NmtCommand, (byte)NmtCommand.Start, Device);
        await Inject(node, Rpdo1, 42);
        node.ObjectDictionary.WriteUnsigned(0x1400, 5, 20);
        Advance(clock, node, TimeSpan.FromMilliseconds(30));
        var barrier = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        node.SubmitEventForTests(() => barrier.TrySetResult(true));
        await barrier.Task.WithTimeoutAsync(ShortTimeout);
        timeouts.Should().Be(0);
        await Inject(node, Rpdo1, 99);
        node.Dispose();
        clock.Advance(TimeSpan.FromSeconds(1));
        timeouts.Should().Be(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task A_Retired_Rpdo_Deadline_Does_Not_Report_A_Timeout(int retired)
    {
        using var service = new StarvedReaderBusService();
        using var node = new CanOpenNode(service, Device, new CanOpenNodeOptions(), false, new ManualTimeSource());
        node.ObjectDictionary.AddU8(0x2000, 0, 0);
        node.ConfigureRpdo(1, new PdoMapping().Add(0x2000, 0, 8));
        node.ObjectDictionary.WriteUnsigned(0x1400, 5, 10);
        node.ObjectDictionary.WriteUnsigned(0x1014, 0, CanOpenCobId.Emcy(Device) | CanOpenCobId.InvalidBit);
        await Inject(node, CanOpenCobId.NmtCommand, (byte)NmtCommand.Start, Device);
        await Inject(node, Rpdo1, 42);
        object rp = ((Array)PdoField(node, "_rpdos")).GetValue(1)!;
        using var handle = (IDisposable)rp.GetType().GetProperty("Deadline")!.GetValue(rp)!;
        object entry = handle.GetType().GetField("_entry", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(handle)!;
        var callback = (Action)entry.GetType().GetProperty("Callback")!.GetValue(entry)!;
        handle.Dispose();
        var disposed = typeof(CanOpenNode).GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var state = typeof(CanOpenNode).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!;
        int timeouts = 0;
        node.RpdoTimeout += (_, _) => System.Threading.Interlocked.Increment(ref timeouts);
        await node.PostToActorAsync(() =>
        {
            if (retired == 0) disposed.SetValue(node, 1);
            if (retired == 1) state.SetValue(node, NmtState.PreOperational);
            if (retired == 2) rp.GetType().GetProperty("Valid")!.SetValue(rp, false);
            try { callback(); }
            finally { disposed.SetValue(node, 0); state.SetValue(node, NmtState.Operational); }
        });
        var barrier = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        node.SubmitEventForTests(() => barrier.TrySetResult(true));
        await barrier.Task.WithTimeoutAsync(ShortTimeout);
        timeouts.Should().Be(retired == 3 ? 1 : 0);
    }

    [Fact]
    public async Task Sync_Rejects_An_Emcy_Collision_And_Accepts_The_Id_After_Disabling_Emcy()
    {
        using var service = new StarvedReaderBusService();
        using var node = new CanOpenNode(service, Device, new CanOpenNodeOptions(), false);
        var od = node.ObjectDictionary;
        Action collision = () => od.WriteUnsigned(0x1005, 0, CanOpenCobId.Emcy(Device));
        collision.Should().Throw<ArgumentException>();
        od.WriteUnsigned(0x1014, 0, CanOpenCobId.Emcy(Device) | CanOpenCobId.InvalidBit);
        collision.Should().NotThrow();
        await Inject(node, CanOpenCobId.NmtCommand, (byte)NmtCommand.Stop, Device);
        await Inject(node, CanOpenCobId.NmtCommand, (byte)NmtCommand.Stop, Device);
        await Inject(node, CanOpenCobId.NmtCommand, (byte)NmtCommand.Start, Device);
        node.State.Should().Be(NmtState.Operational);
    }

    [Fact]
    public async Task A_Queued_Periodic_Sync_Is_Discarded_When_The_Producer_Is_Reconfigured()
    {
        using var service = new StarvedReaderBusService();
        using var node = new CanOpenNode(service, Device, new CanOpenNodeOptions(), false);
        Task? pending = null;
        node.StartSyncProducer(TimeSpan.FromSeconds(10));
        await node.PostToActorAsync(() =>
        {
            pending = (Task)PdoCall(node, "SendNextSync", System.Threading.CancellationToken.None, true)!;
            node.StopSyncProducer();
        });
        await pending!.WithTimeoutAsync(ShortTimeout);
        lock (service.Sent) service.Sent.Should().NotContain(p => p.Length == 0);
    }
    [Fact]
    public async Task An_Lss_Node_Id_Change_Retargets_Only_The_Predefined_Pdos()
    {
        using var service = new StarvedReaderBusService();
        using var node = new CanOpenNode(service, Device, new CanOpenNodeOptions
        {
            PdoCount = 8,
            Lss = new CanKit.Pro.CANopen.Lss.LssSlaveOptions(),
        }, false);
        node.ObjectDictionary.AddU8(0x2000, 0, 42);
        node.ConfigureTpdo(5, new PdoMapping().Add(0x2000, 0, 8), cobId: 0x550);
        node.ConfigureRpdo(8, new PdoMapping().Add(0x2000, 0, 8), cobId: 0x560);
        node.StoreParameters();
        await Inject(node, CanOpenCobId.LssMasterMessage, 4, 1, 0, 0, 0, 0, 0, 0);
        await Inject(node, CanOpenCobId.LssMasterMessage, 0x11, 0x22, 0, 0, 0, 0, 0, 0);
        await Inject(node, CanOpenCobId.NmtCommand, (byte)NmtCommand.ResetCommunication, Device);
        node.NodeId.Should().Be(0x22);
        for (int n = 1; n <= 4; n++)
        {
            (node.ObjectDictionary.ReadUnsigned((ushort)(0x1800 + n - 1), 1) & CanOpenCobId.CanIdMask)
                .Should().Be(CanOpenCobId.TpdoDefault(0x22, n));
        }
        node.ObjectDictionary.ReadUnsigned(0x1804, 1).Should().Be(0x550);
        node.ObjectDictionary.ReadUnsigned(0x1407, 1).Should().Be(0x560);
    }

    [Theory]
    [InlineData(0x0200)]
    [InlineData(0x1002)]
    [InlineData(0x2000)]
    public async Task Mpdo_Reads_Declared_Scalars_Outside_And_Inside_The_Profile_Area(ushort index)
    {
        using var service = new StarvedReaderBusService();
        using var node = new CanOpenNode(service, Device, new CanOpenNodeOptions(), false);
        node.ObjectDictionary.AddU8(index, 0, 42);
        node.ConfigureMultiplexedTpdo(1, MpdoAddressMode.Source, Tpdo1);
        await Inject(node, CanOpenCobId.NmtCommand, (byte)NmtCommand.Start, Device);
        await node.SendSourceMpdoAsync(1, index, 0);
        await Task.Run(async () => { while (node.HasLssTransmitsInFlight) await Task.Yield(); }).WithTimeoutAsync(ShortTimeout);
        lock (service.Sent) service.Sent.Single(p => p.Length == 8).Should().Equal(Device, (byte)index, (byte)(index >> 8), 0, 42, 0, 0, 0);
    }

    [Fact]
    public async Task Dispose_Retires_A_Sync_Queued_Behind_Actor_Work()
    {
        using var service = new StarvedReaderBusService();
        using var node = new CanOpenNode(service, Device, new CanOpenNodeOptions(), false);
        using var release = new System.Threading.ManualResetEventSlim();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = node.PostToActorAsync(() => { entered.TrySetResult(true); release.Wait(ShortTimeout); });
        await entered.Task.WithTimeoutAsync(ShortTimeout);
        var queued = node.SendSyncAsync();
        var disposing = Task.Run(node.Dispose);
        try
        {
            await Task.Run(async () => { while ((int)PdoField(node, "_disposed") == 0) await Task.Yield(); }).WithTimeoutAsync(ShortTimeout);
        }
        finally { release.Set(); }
        await Task.WhenAll(blocked, queued, disposing).WithTimeoutAsync(ShortTimeout);
        lock (service.Sent) service.Sent.Should().NotContain(p => p.Length == 0);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Rtr_Is_Ignored_For_An_Inactive_Disabled_Or_Multiplexed_Producer(int unavailable)
    {
        using var service = new StarvedReaderBusService();
        using var node = new CanOpenNode(service, Device, new CanOpenNodeOptions(), false);
        node.ObjectDictionary.AddU8(0x2000, 0, 42);
        node.ConfigureTpdo(1, new PdoMapping().Add(0x2000, 0, 8));
        await Inject(node, CanOpenCobId.NmtCommand, (byte)NmtCommand.Start, Device);
        int frames = 0;
        service.ConfirmSendAsync = (frame, _) =>
        {
            if (frame.ID == Tpdo1) System.Threading.Interlocked.Increment(ref frames);
            return Task.FromResult(new CanKit.Pro.RawCan.TxConfirmation { Confirmed = true });
        };
        if (unavailable == 0) await Inject(node, CanOpenCobId.NmtCommand, (byte)NmtCommand.EnterPreOperational, Device);
        if (unavailable == 1) node.ObjectDictionary.WriteUnsigned(0x1800, 1, Tpdo1 | CanOpenCobId.InvalidBit);
        if (unavailable == 2) node.ObjectDictionary.WriteUnsigned(0x1800, 1, Tpdo1 | CanOpenCobId.NoRtrBit);
        if (unavailable == 3)
        {
            node.ConfigureMultiplexedTpdo(1, MpdoAddressMode.Destination, Tpdo1);
            node.ObjectDictionary.WriteUnsigned(0x1800, 1, Tpdo1);
        }
        await node.PostToActorAsync(() => PdoCall(node, "HandleTpdoRtr", ((Array)PdoField(node, "_tpdos")).GetValue(1)));
        frames.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sync_Start_Does_Not_Gate_Acyclic_Or_Rtr_Sampling(bool rtr)
    {
        using var service = new StarvedReaderBusService();
        using var node = new CanOpenNode(service, Device, new CanOpenNodeOptions(), false);
        node.ObjectDictionary.AddU8(0x2000, 0, 42);
        node.ObjectDictionary.WriteUnsigned(0x1019, 0, 8);
        node.ConfigureTpdo(1, new PdoMapping().Add(0x2000, 0, 8),
            rtr ? TpdoTransmission.RtrOnlySynchronous : TpdoTransmission.SynchronousAcyclic);
        SetSyncStart(node, 5, rtr ? (byte)252 : (byte)0);
        await Inject(node, CanOpenCobId.NmtCommand, (byte)NmtCommand.Start, Device);
        if (!rtr) await node.TriggerTpdoAsync(1);
        await Inject(node, CanOpenCobId.Sync, 1); // deliberately before the configured start value
        if (rtr)
        {
            node.ObjectDictionary.WriteUnsigned(0x2000, 0, 99);
            await node.PostToActorAsync(() => PdoCall(node, "HandleIncomingFrame", Tpdo1, Array.Empty<byte>(), true, false));
        }
        await Task.Run(async () => { while (node.HasLssTransmitsInFlight) await Task.Yield(); }).WithTimeoutAsync(ShortTimeout);
        lock (service.Sent) service.Sent.Where(p => p.Length == 1 && p[0] != 0).Should().ContainSingle().Which.Should().Equal(42);
    }
}
