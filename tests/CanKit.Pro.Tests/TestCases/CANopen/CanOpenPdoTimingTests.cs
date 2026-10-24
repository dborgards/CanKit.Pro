using System;
using System.Collections.Generic;
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
    private static string PdoFixture(string name)
        => string.Join(System.IO.Path.DirectorySeparatorChar.ToString(),
            AppContext.BaseDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar),
            "TestCases", "CANopen", "Fixtures", name);

    private static void SetSyncStart(ICanOpenNode node, byte start, byte type = 1)
    {
        var od = node.ObjectDictionary;
        uint word = od.ReadUnsigned(0x1800, 1);
        od.WriteUnsigned(0x1800, 1, word | CanOpenCobId.InvalidBit);
        od.WriteUnsigned(0x1800, 2, type);
        od.WriteUnsigned(0x1800, 6, start);
        od.WriteUnsigned(0x1800, 1, word);
        Settle(node);
    }

    // Injection is still on the actor, through the frame dispatcher, but provides a precise
    // receive barrier before advancing the virtual clock. Transmission is observed on the bus.
    private static Task Inject(ICanOpenNode node, uint id, params byte[] payload)
        => ((CanOpenNode)node).PostToActorAsync(() => typeof(CanOpenNode)
            .GetMethod("HandleIncoming", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(node, new object[] { id, payload, false }));

    // FR-CO-052: the matching SYNC is the FIRST counted SYNC, not the first transmission for n>1.
    [Fact]
    public async Task Sync_Start_Waits_Through_Overflow_And_Restarts_On_Operational()
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var wire = new Wire(session, 2);
        using var device = CanOpen.OpenNode(bus, Device);
        var od = device.ObjectDictionary;
        od.AddU16(0x2000, 0, 42);
        od.AddU8(0x2001, 0, 1);
        od.WriteUnsigned(0x1019, 0, 8);
        device.ConfigureTpdo(1, new PdoMapping().Add(0x2000, 0, 16), TpdoTransmission.Synchronous);
        device.ConfigureTpdo(2, new PdoMapping().Add(0x2001, 0, 8), TpdoTransmission.Synchronous);
        SetSyncStart(device, 5, 3);
        await StartAsync(wire, device);
        int seen = 0;
        foreach (byte counter in new byte[] { 6, 7, 8, 1, 2, 3, 4, 5, 6 })
        {
            wire.Transmit(CanOpenCobId.Sync, new[] { counter });
            await wire.WaitForCountAsync(Tpdo2, ++seen);
            if (counter == 5)
            {
                // An unrelated parameter write must not re-arm the start gate or reset its phase.
                od.WriteUnsigned(0x1800, 5, 10);
                Settle(device);
            }
        }
        wire.Count(Tpdo1).Should().Be(0);
        wire.Transmit(CanOpenCobId.Sync, new byte[] { 7 });
        await wire.WaitForCountAsync(Tpdo2, ++seen);
        await wire.WaitForCountAsync(Tpdo1, 1);
        wire.SendNmt(NmtCommand.EnterPreOperational, Device);
        await WaitForStateAsync(device, NmtState.PreOperational);
        await StartAsync(wire, device);
        wire.Transmit(CanOpenCobId.Sync, new byte[] { 6 });
        await wire.WaitForCountAsync(Tpdo2, ++seen);
        wire.Count(Tpdo1).Should().Be(1);
        foreach (byte counter in new byte[] { 5, 6, 7 })
        {
            wire.Transmit(CanOpenCobId.Sync, new[] { counter });
            await wire.WaitForCountAsync(Tpdo2, ++seen);
        }
        await wire.WaitForCountAsync(Tpdo1, 2);
    }

    [Fact]
    public async Task Sync_Start_Is_Ignored_Without_A_Counter_And_Zero_Does_Not_Wait()
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var wire = new Wire(session, 2);
        using var device = CanOpen.OpenNode(bus, Device);
        var od = device.ObjectDictionary;
        od.AddU8(0x2000, 0, 42);
        device.ConfigureTpdo(1, new PdoMapping().Add(0x2000, 0, 8), TpdoTransmission.Synchronous);
        SetSyncStart(device, 5);
        await StartAsync(wire, device);
        wire.SendSync();
        await wire.WaitForCountAsync(Tpdo1, 1);
        od.WriteUnsigned(0x1019, 0, 8);
        SetSyncStart(device, 0);
        wire.Transmit(CanOpenCobId.Sync, new byte[] { 7 });
        await wire.WaitForCountAsync(Tpdo1, 2);
    }

    // FR-CO-052, #358: §7.5.2.37 gives the start value to the PDO — "The SYNC message of which
    // the counter value equals the SYNC start value shall be regarded as the first received SYNC
    // message" — and exempts no synchronous type. Start 5 on counters 1, 2, 3, …: SYNCs 1–4 do not
    // count, so an acyclic TPDO triggered before them goes out on SYNC 5, type n on the n-th SYNC
    // from 5 on, and an FCh TPDO has nothing sampled until SYNC 5, so its RTRs before then go
    // unanswered. TPDO2 (type 01h, start 0) is the per-SYNC ordering witness; an RTR sent after
    // SYNC k is witnessed by TPDO2's frame for SYNC k + 1. Frame-driven, on the virtual clock.
    [Theory]
    [InlineData(CanOpenTransmissionType.SynchronousAcyclic)]
    [InlineData(CanOpenTransmissionType.SynchronousEverySync)]
    [InlineData(CanOpenTransmissionType.SynchronousCyclicMax)]
    [InlineData(CanOpenTransmissionType.RtrOnlySynchronous)]
    public async Task Sync_Start_Gates_Every_Synchronous_Transmission_Type(byte type)
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var wire = new Wire(session, 2);
        var clock = new ManualTimeSource();
        using var device = OpenClocked(bus, Device, clock);
        var od = device.ObjectDictionary;
        od.AddU8(0x2000, 0, 42);
        od.AddU8(0x2001, 0, 1);
        od.WriteUnsigned(0x1019, 0, 8);
        device.ConfigureTpdo(1, new PdoMapping().Add(0x2000, 0, 8), TpdoTransmission.Synchronous);
        device.ConfigureTpdo(2, new PdoMapping().Add(0x2001, 0, 8), TpdoTransmission.Synchronous);
        SetSyncStart(device, 5, type);
        await StartAsync(wire, device);
        if (type == CanOpenTransmissionType.SynchronousAcyclic) await device.TriggerTpdoAsync(1);

        bool rtr = type == CanOpenTransmissionType.RtrOnlySynchronous;
        int first = 4 + (type is CanOpenTransmissionType.SynchronousAcyclic or CanOpenTransmissionType.RtrOnlySynchronous ? 1 : type);
        for (int k = 1; k <= first; k++)
        {
            wire.Transmit(CanOpenCobId.Sync, new[] { (byte)((k - 1) % 8 + 1) });
            await wire.WaitForCountAsync(Tpdo2, k);
            if (k < first || rtr)
                wire.Count(Tpdo1).Should().Be(0, "SYNC {0} (counter {1}) is before the first counted SYNC, or answers no RTR", k, (k - 1) % 8 + 1);
            if (rtr) wire.SendRtr(Tpdo1);
        }
        await wire.WaitForCountAsync(Tpdo1, 1);
        wire.Payloads(Tpdo1)[0].Should().Equal(42);
    }

    [Fact]
    public async Task Sync_Producer_Counts_Wraps_And_Resets_After_Stopped()
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var wire = new Wire(session, 2);
        var clock = new ManualTimeSource();
        using var device = OpenClocked(bus, Device, clock);
        device.ObjectDictionary.WriteUnsigned(0x1019, 0, 3);
        for (int i = 1; i <= 5; i++)
        {
            await device.SendSyncAsync();
            await wire.WaitForCountAsync(CanOpenCobId.Sync, i);
        }
        wire.Payloads(CanOpenCobId.Sync).Select(p => p.Single()).Should().Equal(1, 2, 3, 1, 2);
        wire.SendNmt(NmtCommand.Stop, Device);
        await WaitForStateAsync(device, NmtState.Stopped);
        wire.SendNmt(NmtCommand.EnterPreOperational, Device);
        await WaitForStateAsync(device, NmtState.PreOperational);
        device.StartSyncProducer(TimeSpan.FromMilliseconds(100));
        Advance(clock, device, TimeSpan.FromMilliseconds(100));
        await wire.WaitForCountAsync(CanOpenCobId.Sync, 6);
        wire.Payloads(CanOpenCobId.Sync)[5].Should().Equal(new byte[] { 1 });
    }

    [Fact]
    public async Task Sync_Parameters_Enforce_Values_State_And_Rpdo_Reserved_Subindex()
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var masterBus = Open(session, 1);
        using var device = CanOpen.OpenNode(bus, Device, MasterConfigurable);
        using var master = CanOpen.OpenNode(masterBus, Master);
        foreach (byte value in new byte[] { 1, 241, 255 })
            (await DownloadShouldAbortAsync(master, 0x1019, 0, new[] { value })).AbortCode
                .Should().Be((uint)CanKit.Pro.CANopen.Sdo.SdoAbortCode.ValueRangeExceeded);
        await DownloadAsync(master, 0x1019, 0, new byte[] { 10 });
        await DownloadAsync(master, 0x1006, 0, U32Bytes(1000));
        (await DownloadShouldAbortAsync(master, 0x1019, 0, new byte[] { 20 })).AbortCode
            .Should().Be((uint)CanKit.Pro.CANopen.Sdo.SdoAbortCode.DataCannotBeTransferredDeviceState);
        await DownloadAsync(master, 0x1800, 6, new byte[] { 240 });
        (await DownloadShouldAbortAsync(master, 0x1800, 6, new byte[] { 241 })).AbortCode
            .Should().Be((uint)CanKit.Pro.CANopen.Sdo.SdoAbortCode.ValueRangeExceeded);
        await DownloadAsync(master, 0x1800, 1, U32Bytes(Tpdo1));
        (await DownloadShouldAbortAsync(master, 0x1800, 6, new byte[] { 5 })).AbortCode
            .Should().Be((uint)CanKit.Pro.CANopen.Sdo.SdoAbortCode.ValueRangeExceeded);
        (await DownloadShouldAbortAsync(master, 0x1400, 6, new byte[] { 5 })).AbortCode
            .Should().Be((uint)CanKit.Pro.CANopen.Sdo.SdoAbortCode.SubIndexDoesNotExist);
    }

    // FR-CO-053: window boundaries are measured against the same virtual clock as the actor.
    [Fact]
    public async Task Sync_Window_Discards_Late_Rpdos_And_Reopens_On_The_Next_Sync()
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var wire = new Wire(session, 2);
        var clock = new ManualTimeSource();
        using var device = OpenClocked(bus, Device, clock);
        var od = device.ObjectDictionary;
        od.AddU8(0x2000, 0, 0);
        od.AddU8(0x2001, 0, 1);
        od.WriteUnsigned(0x1007, 0, 1000);
        device.ConfigureRpdo(1, new PdoMapping().Add(0x2000, 0, 8), transmission: RpdoTransmission.Synchronous);
        device.ConfigureTpdo(1, new PdoMapping().Add(0x2001, 0, 8), TpdoTransmission.Synchronous);
        await StartAsync(wire, device);
        await Inject(device, Rpdo1, 1); // no window before first SYNC
        await Inject(device, CanOpenCobId.Sync);
        od.ReadUnsigned(0x2000, 0).Should().Be(0);
        await wire.WaitForCountAsync(Tpdo1, 1);
        await Inject(device, Rpdo1, 2);
        Advance(clock, device, TimeSpan.FromMilliseconds(1));
        await Inject(device, Rpdo1, 3); // closed at the boundary
        await device.TriggerTpdoAsync(1); // no late synchronous transmission
        await Inject(device, CanOpenCobId.Sync);
        od.ReadUnsigned(0x2000, 0).Should().Be(2);
        await wire.WaitForCountAsync(Tpdo1, 2);
        wire.Count(Tpdo1).Should().Be(2);
        await Inject(device, Rpdo1, 4);
        await Inject(device, CanOpenCobId.Sync);
        od.ReadUnsigned(0x2000, 0).Should().Be(4);
    }

    [Fact]
    public async Task Unexpected_Sync_Length_Does_Not_Actuate_And_Raises_8240()
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var wire = new Wire(session, 2);
        using var device = CanOpen.OpenNode(bus, Device);
        device.ObjectDictionary.AddU8(0x2000, 0, 0);
        device.ObjectDictionary.WriteUnsigned(0x1019, 0, 8);
        device.ConfigureRpdo(1, new PdoMapping().Add(0x2000, 0, 8), transmission: RpdoTransmission.Synchronous);
        await StartAsync(wire, device);
        await Inject(device, Rpdo1, 42);
        await Inject(device, CanOpenCobId.Sync);
        await wire.WaitForCountAsync(CanOpenCobId.Emcy(Device), 1);
        wire.Payloads(CanOpenCobId.Emcy(Device))[0].Take(2).Should().Equal(0x40, 0x82);
        device.ObjectDictionary.ReadUnsigned(0x2000, 0).Should().Be(0);
        await Inject(device, CanOpenCobId.Sync, 0);
        await Inject(device, CanOpenCobId.Sync, 241);
        device.ObjectDictionary.ReadUnsigned(0x2000, 0).Should().Be(0);
        var received = new TaskCompletionSource<byte?>(TaskCreationOptions.RunContinuationsAsynchronously);
        device.SyncReceived += (_, e) => received.TrySetResult(e.Counter);
        await Inject(device, CanOpenCobId.Sync, 9); // consumer ignores the local overflow's value
        (await received.Task.WithTimeoutAsync(ShortTimeout)).Should().Be(9);
        device.ObjectDictionary.ReadUnsigned(0x2000, 0).Should().Be(42);
    }

    // FR-CO-054: no timer before first frame; a short frame does not refresh it; timeout is
    // one-shot until reception, and the NMT transition cancels it.
    [Fact]
    public async Task Rpdo_Deadline_Starts_On_Reception_Refreshes_And_Stops_With_Nmt()
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var wire = new Wire(session, 2);
        var clock = new ManualTimeSource();
        using var device = OpenClocked(bus, Device, clock);
        var od = device.ObjectDictionary;
        od.AddU16(0x2000, 0, 0);
        device.ConfigureRpdo(1, new PdoMapping().Add(0x2000, 0, 16));
        od.WriteUnsigned(0x1400, 5, 100);
        var notices = new List<RpdoTimeoutEventArgs>();
        using var arrived = new SemaphoreSlim(0);
        device.Pdo().RpdoTimeout += (_, e) => { lock (notices) notices.Add(e); arrived.Release(); };
        await StartAsync(wire, device);
        Advance(clock, device, TimeSpan.FromSeconds(1));
        await Inject(device, Rpdo1, 1, 0);
        Advance(clock, device, TimeSpan.FromMilliseconds(90));
        await Inject(device, Rpdo1, 2, 0);
        Advance(clock, device, TimeSpan.FromMilliseconds(90));
        await Inject(device, Rpdo1, 3); // too short, no refresh
        Advance(clock, device, TimeSpan.FromMilliseconds(10));
        (await arrived.WaitAsync(ShortTimeout)).Should().BeTrue();
        lock (notices)
        {
            notices.Should().ContainSingle();
            notices[0].PdoIndex.Should().Be(1);
            notices[0].Timeout.Should().Be(TimeSpan.FromMilliseconds(100));
            notices[0].CobId.Should().Be(Rpdo1);
        }
        await wire.WaitForCountAsync(CanOpenCobId.Emcy(Device), 2); // short frame + deadline
        wire.Payloads(CanOpenCobId.Emcy(Device)).Should().Contain(p => p[0] == 0x50 && p[1] == 0x82);
        Advance(clock, device, TimeSpan.FromSeconds(1));
        await Inject(device, Rpdo1, 4, 0);
        wire.SendNmt(NmtCommand.Stop, Device);
        await WaitForStateAsync(device, NmtState.Stopped);
        Advance(clock, device, TimeSpan.FromSeconds(1));
        await StartAsync(wire, device);
        Advance(clock, device, TimeSpan.FromSeconds(1));
        await Inject(device, Rpdo1, 5, 0);
        Advance(clock, device, TimeSpan.FromMilliseconds(100));
        (await arrived.WaitAsync(ShortTimeout)).Should().BeTrue();
        lock (notices) notices.Should().HaveCount(2);
    }
    [Fact]
    public async Task Description_Loads_Sync_Timing_And_Reset_Restores_It()
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var wire = new Wire(session, 2);
        var file = CanOpenDeviceDescription.Load(PdoFixture("pdo-timing.dcf"));
        using var device = new CanOpenNode(new CanKit.Pro.RawCan.CanBusService(bus), Device,
            new CanOpenNodeOptions(), ownsService: true, new ManualTimeSource(), description: file);
        device.DeviceDescription!.IsExact.Should().BeTrue(string.Join("\n", device.DeviceDescription.Findings));
        var od = device.ObjectDictionary;
        od.TryGet(0x1001, 0, out var errorRegister).Should().BeTrue();
        errorRegister.PdoMappable.Should().BeTrue();
        od.ReadUnsigned(0x1019, 0).Should().Be(8);
        od.ReadUnsigned(0x1006, 0).Should().Be(100000);
        od.ReadUnsigned(0x1007, 0).Should().Be(1000);
        od.ReadUnsigned(0x1800, 6).Should().Be(5);
        od.ReadUnsigned(0x1400, 5).Should().Be(100);
        await StartAsync(wire, device);
        await Inject(device, CanOpenCobId.Sync, 5);
        await Inject(device, CanOpenCobId.Sync, 6);
        await wire.WaitForCountAsync(Tpdo1, 1);
        od.WriteUnsigned(0x1006, 0, 0);
        od.WriteUnsigned(0x1019, 0, 10);
        od.WriteUnsigned(0x1007, 0, 2000);
        await Inject(device, CanOpenCobId.NmtCommand, (byte)NmtCommand.ResetCommunication, Device);
        od.ReadUnsigned(0x1019, 0).Should().Be(8);
        od.ReadUnsigned(0x1007, 0).Should().Be(1000);
        od.ReadUnsigned(0x1800, 6).Should().Be(5);
    }

    [Fact]
    public async Task Rpdo_Timeouts_Do_Not_Fold_Across_A_Queued_Reception()
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var wire = new Wire(session, 2);
        var clock = new ManualTimeSource();
        using var device = OpenClocked(bus, Device, clock);
        device.ObjectDictionary.AddU8(0x2000, 0, 0);
        device.ConfigureRpdo(1, new PdoMapping().Add(0x2000, 0, 8));
        device.ObjectDictionary.WriteUnsigned(0x1400, 5, 100);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<char>();
        int timeouts = 0;
        device.RpdoReceived += (_, _) =>
        {
            order.Add('R');
            if (entered.TrySetResult(true)) release.Wait(ShortTimeout);
        };
        device.Pdo().RpdoTimeout += (_, _) =>
        {
            order.Add('T');
            if (++timeouts == 2) completed.TrySetResult(true);
        };
        try
        {
            await StartAsync(wire, device);
            await Inject(device, Rpdo1, 1);
            await entered.Task.WithTimeoutAsync(ShortTimeout);
            Advance(clock, device, TimeSpan.FromMilliseconds(100));
            await Inject(device, Rpdo1, 2);
            Advance(clock, device, TimeSpan.FromMilliseconds(100));
            release.Set();
            await completed.Task.WithTimeoutAsync(ShortTimeout);
            order.Should().Equal('R', 'T', 'R', 'T');
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task Concurrent_Sync_Requests_Keep_Counter_Order_And_Cancellation_Does_Not_Consume_One()
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var wire = new Wire(session, 2);
        using var device = CanOpen.OpenNode(bus, Device);
        device.ObjectDictionary.WriteUnsigned(0x1019, 0, 3);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => device.SendSyncAsync(canceled.Token));
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => device.SendSyncAsync()));
        await wire.WaitForCountAsync(CanOpenCobId.Sync, 12);
        wire.Payloads(CanOpenCobId.Sync).Select(p => p.Single()).Should().Equal(1, 2, 3, 1, 2, 3, 1, 2, 3, 1, 2, 3);
    }

}
