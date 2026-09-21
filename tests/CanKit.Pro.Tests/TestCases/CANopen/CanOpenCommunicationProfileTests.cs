using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Emcy;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Pdo;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

/// <summary>
/// Virtual-loopback tests for the communication-profile objects of <c>CanKit.Pro.CANopen</c>
/// (SRS FR-CO-013, FR-CO-014, FR-CO-018, FR-CO-019, FR-CO-023; CiA 301 §7.5.2). The node's object
/// dictionary is the single source of truth for its SYNC, EMCY, heartbeat and PDO configuration —
/// in both directions (a write configures the service, a configuration method is a write) and on
/// both write paths (an SDO download from a peer, a local <see cref="ObjectDictionary"/> write) —
/// and an NMT reset restores the power-on values of CiA 301 §7.3.2.2.1.
/// </summary>
/// <remarks>
/// Nothing here is gated on a clock. Every "it happened" is a frame or an event awaited with
/// <c>WithTimeoutAsync</c>. Every "it did not happen" is decided by an ordering witness: a later
/// positive that travels the same ordered path as the negative would have — one bus subscription,
/// one actor mailbox, one event pump, or the actor's due-time-sorted timer list — so that its
/// arrival proves the negative had its turn and did not take it.
/// </remarks>
public class CanOpenCommunicationProfileTests : IClassFixture<VirtualAdapterFixture>
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);

    private const byte Master = 0x01;
    private const byte Slave = 0x11;
    private const byte Tool = 0x02;

    // "save" and "load" as CiA 301 §7.5.2.13 Figure 55 / §7.5.2.14 Figure 57 put them on the wire:
    // UNSIGNED32 0x65766173 / 0x64616F6C, little-endian.
    private static readonly byte[] SaveSignature = { 0x73, 0x61, 0x76, 0x65 };
    private static readonly byte[] LoadSignature = { 0x6C, 0x6F, 0x61, 0x64 };

    private static string NewSession() => VirtualAdapterFixture.NewSession("canopen-profile");

    private static ICanBus Open(string session, int channel) => VirtualAdapterFixture.Open(session, channel);

    private static byte[] U8(byte value) => new[] { value };

    private static byte[] U16(ushort value) => new[] { (byte)(value & 0xFF), (byte)(value >> 8) };

    private static byte[] U32(uint value) => new[]
    {
        (byte)(value & 0xFF), (byte)((value >> 8) & 0xFF), (byte)((value >> 16) & 0xFF), (byte)((value >> 24) & 0xFF),
    };

    private static uint LittleEndian(byte[] raw)
    {
        uint value = 0;
        for (int i = raw.Length - 1; i >= 0; i--) value = (value << 8) | raw[i];
        return value;
    }

    private static Task<byte[]> UploadAsync(ICanOpenNode client, byte server, ushort index, byte subindex)
        => client.SdoUploadAsync(server, index, subindex).WithTimeoutAsync(ShortTimeout);

    private static async Task<uint> UploadUnsignedAsync(ICanOpenNode client, byte server, ushort index, byte subindex)
        => LittleEndian(await UploadAsync(client, server, index, subindex));

    private static Task DownloadAsync(ICanOpenNode client, byte server, ushort index, byte subindex, byte[] data)
        => client.SdoDownloadAsync(server, index, subindex, data).WithTimeoutAsync(ShortTimeout);

    private static async Task ExpectAbortAsync(Func<Task> transfer, SdoAbortCode code)
    {
        var ex = await Assert.ThrowsAsync<SdoAbortException>(transfer);
        ex.AbortCode.Should().Be((uint)code);
    }

    private static TaskCompletionSource<T> NewTcs<T>()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static uint HeartbeatEntry(byte nodeId, ushort milliseconds) => ((uint)nodeId << 16) | milliseconds;

    /// <summary>
    /// Counts the boot-up frames (<c>00h</c> on <c>700h + producer</c>) one node sees from another.
    /// A node sends one when it is opened and one on every NMT reset; a test that waits for the
    /// reset's boot-up first consumes the opening one, so the two cannot be confused. The observer
    /// must be opened before the producer so that the first one is guaranteed to be seen.
    /// </summary>
    private sealed class BootupWatch
    {
        private readonly TaskCompletionSource<bool> _first = NewTcs<bool>();
        private readonly TaskCompletionSource<bool> _second = NewTcs<bool>();
        private int _seen;

        public BootupWatch(ICanOpenNode observer, byte producer)
        {
            observer.HeartbeatReceived += (_, e) =>
            {
                if (e.ProducerNodeId != producer || e.State != NmtState.Initializing) return;
                switch (Interlocked.Increment(ref _seen))
                {
                    case 1: _first.TrySetResult(true); break;
                    case 2: _second.TrySetResult(true); break;
                }
            };
        }

        public Task First => _first.Task.WithTimeoutAsync(ShortTimeout);

        public Task Second => _second.Task.WithTimeoutAsync(ShortTimeout);
    }

    /// <summary>
    /// A positive witness that a node's heartbeat producer is off: per CiA 301 §7.2.8.3.2.2 the two
    /// error control protocols are exclusive ("If the heartbeat producer time is unequal 0 the
    /// heartbeat protocol is used"), so a node answers a node-guarding RTR only while <c>1017h</c> is
    /// 0. The reply to the second of two polls carries the toggle bit (bit 7), which a heartbeat
    /// never does, so that frame is unambiguous evidence.
    /// </summary>
    private static async Task AwaitGuardingReplyAsync(ICanBus observer, byte nodeId)
    {
        uint cobId = CanOpenCobId.Heartbeat(nodeId);
        var toggled = NewTcs<byte>();
        observer.FrameObserved += (_, e) =>
        {
            var frame = e.CanFrame;
            if (frame.IsExtendedFrame || frame.IsRemoteFrame || (uint)frame.ID != cobId || frame.Data.Length < 1) return;
            byte state = frame.Data.Span[0];
            if ((state & 0x80) != 0) toggled.TrySetResult(state);
        };

        observer.Transmit(CanFrame.Classic((int)cobId, ReadOnlyMemory<byte>.Empty, isRemoteFrame: true));
        observer.Transmit(CanFrame.Classic((int)cobId, ReadOnlyMemory<byte>.Empty, isRemoteFrame: true));

        var reply = await toggled.Task.WithTimeoutAsync(ShortTimeout);
        (reply & 0x7F).Should().Be((byte)NmtState.PreOperational,
            "a guarding reply reports the producer's current NMT state in bits 0..6");
    }

    // =========================================================================================
    // FR-CO-013 — the communication-profile objects exist in a fresh node's OD at their CiA 301
    // defaults.
    // =========================================================================================

    // FR-CO-013: every object the node manages is present after OpenNode, at the default its
    // CiA 301 definition prescribes (§7.5.2.1, §7.5.2.2, §7.5.2.5, §7.5.2.6, §7.5.2.11–§7.5.2.14,
    // §7.5.2.17, §7.5.2.19–§7.5.2.21, §7.5.2.33, §7.5.2.35–§7.5.2.38).
    [Fact]
    public void A_Fresh_Node_Carries_The_Communication_Profile_Objects_At_Their_CiA301_Defaults()
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var node = CanOpen.OpenNode(bus, nodeId: Slave);
        var od = node.ObjectDictionary;

        // §7.5.2.1: device profile 0000h = "does not follow a standardized profile".
        od.ReadUnsigned(0x1000, 0x00).Should().Be(0u);
        // §7.5.2.2: no error.
        od.ReadUnsigned(0x1001, 0x00).Should().Be(0u);
        // §7.5.2.21: vendor-ID 0000 0000h = "invalid vendor-ID", i.e. none assigned yet.
        od.ReadUnsigned(0x1018, 0x00).Should().Be(1u);
        od.ReadUnsigned(0x1018, 0x01).Should().Be(0u);
        // §7.5.2.5 / §7.5.2.6: SYNC on 080h, not generated, no cycle period.
        od.ReadUnsigned(0x1005, 0x00).Should().Be(0x0000_0080u);
        od.ReadUnsigned(0x1006, 0x00).Should().Be(0u);
        // §7.5.2.11 / §7.5.2.12: life guarding off.
        od.ReadUnsigned(0x100C, 0x00).Should().Be(0u);
        od.ReadUnsigned(0x100D, 0x00).Should().Be(0u);
        // §7.5.2.13 / §7.5.2.14: sub-index 01h ("all parameters"); the read value 1 announces
        // "saves parameters on command" / "restores default parameters".
        od.ReadUnsigned(0x1010, 0x00).Should().Be(1u);
        od.ReadUnsigned(0x1010, 0x01).Should().Be(1u);
        od.ReadUnsigned(0x1011, 0x00).Should().Be(1u);
        od.ReadUnsigned(0x1011, 0x01).Should().Be(1u);
        // §7.5.2.17: 80h + node-id, valid (bit 31 = 0b).
        od.ReadUnsigned(0x1014, 0x00).Should().Be(0x080u + Slave);
        // §7.5.2.19 / §7.5.2.20: one unused consumer slot, no producer.
        od.ReadUnsigned(0x1016, 0x00).Should().Be(1u);
        od.ReadUnsigned(0x1016, 0x01).Should().Be(0u);
        od.ReadUnsigned(0x1017, 0x00).Should().Be(0u);
        // §7.5.2.33: the default SDO server, 600h / 580h + node-id.
        od.ReadUnsigned(0x1200, 0x00).Should().Be(2u);
        od.ReadUnsigned(0x1200, 0x01).Should().Be(0x600u + Slave);
        od.ReadUnsigned(0x1200, 0x02).Should().Be(0x580u + Slave);

        // §7.5.2.35–§7.5.2.38: four RPDOs and four TPDOs at their pre-defined connection set
        // CAN-IDs, each "PDO does not exist" (bit 31 = 1b), transmission type FEh, no mapping.
        for (int n = 1; n <= 4; n++)
        {
            var rpdoComm = (ushort)(0x1400 + n - 1);
            var rpdoMap = (ushort)(0x1600 + n - 1);
            var tpdoComm = (ushort)(0x1800 + n - 1);
            var tpdoMap = (ushort)(0x1A00 + n - 1);

            od.ReadUnsigned(rpdoComm, 0x00).Should().Be(2u, "RPDO records carry COB-ID and transmission type");
            uint rpdoCobId = od.ReadUnsigned(rpdoComm, 0x01);
            (rpdoCobId & CanOpenCobId.InvalidBit).Should().Be(CanOpenCobId.InvalidBit, "RPDO{0} does not exist yet", n);
            (rpdoCobId & CanOpenCobId.CanIdMask).Should().Be(CanOpenCobId.RpdoDefault(Slave, n));
            od.ReadUnsigned(rpdoComm, 0x02).Should().Be(CanOpenTransmissionType.EventDrivenManufacturer);

            od.ReadUnsigned(tpdoComm, 0x00).Should().Be(5u, "inhibit time and event timer are supported (§7.5.2.37)");
            uint tpdoCobId = od.ReadUnsigned(tpdoComm, 0x01);
            (tpdoCobId & CanOpenCobId.InvalidBit).Should().Be(CanOpenCobId.InvalidBit, "TPDO{0} does not exist yet", n);
            (tpdoCobId & CanOpenCobId.CanIdMask).Should().Be(CanOpenCobId.TpdoDefault(Slave, n));
            od.ReadUnsigned(tpdoComm, 0x02).Should().Be(CanOpenTransmissionType.EventDrivenManufacturer);
            od.ReadUnsigned(tpdoComm, 0x03).Should().Be(0u, "inhibit time off");
            od.ReadUnsigned(tpdoComm, 0x05).Should().Be(0u, "event timer off");

            od.ReadUnsigned(rpdoMap, 0x00).Should().Be(0u, "no RPDO{0} mapping", n);
            od.ReadUnsigned(tpdoMap, 0x00).Should().Be(0u, "no TPDO{0} mapping", n);
        }
    }

    // FR-CO-013: the default SDO server record is constant on the bus (CiA 301 §7.5.2.33 —
    // the node serves 600h/580h + node-id and nothing else), so a master's attempt to write it is
    // refused as a write to a read-only object (Table 22, 0601 0002h).
    [Fact]
    public async Task The_Default_Sdo_Server_Record_1200h_Is_Read_Only_On_The_Bus()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: Master);
        using var slave = CanOpen.OpenNode(busB, nodeId: Slave);

        await ExpectAbortAsync(
            () => DownloadAsync(master, Slave, 0x1200, 0x01, U32(0x600u + Slave)),
            SdoAbortCode.AttemptWriteReadOnly);
        await ExpectAbortAsync(
            () => DownloadAsync(master, Slave, 0x1200, 0x02, U32(0x580u + Slave)),
            SdoAbortCode.AttemptWriteReadOnly);

        (await UploadUnsignedAsync(master, Slave, 0x1200, 0x01)).Should().Be(0x600u + Slave);
        (await UploadUnsignedAsync(master, Slave, 0x1200, 0x02)).Should().Be(0x580u + Slave);
    }

    // FR-CO-013: the PDO communication and mapping records are read-only on the bus by default —
    // the application holds both ends of its PDOs, and the CiA 301 objects overview permits ro for
    // exactly these entries — and writable when the application opens them with
    // WritableCommunicationParameters, in which case the write lands in the record.
    [Theory]
    [InlineData(0x1400, 0x02, 0x01)] // RPDO1 transmission type: synchronous
    [InlineData(0x1600, 0x00, 0x00)] // RPDO1 mapping: disabled
    [InlineData(0x1800, 0x02, 0x01)] // TPDO1 transmission type: every SYNC
    [InlineData(0x1A00, 0x00, 0x00)] // TPDO1 mapping: disabled
    public async Task Pdo_Records_Are_Read_Only_On_The_Bus_Unless_The_Application_Opens_Them(
        int index, int subindex, int value)
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var busC = Open(session, 2);
        using var master = CanOpen.OpenNode(busA, nodeId: Master);
        using var closedDevice = CanOpen.OpenNode(busB, nodeId: Slave);
        using var openDevice = CanOpen.OpenNode(busC, nodeId: 0x12,
            new CanOpenNodeOptions { WritableCommunicationParameters = true });

        var payload = U8((byte)value);

        await ExpectAbortAsync(
            () => DownloadAsync(master, Slave, (ushort)index, (byte)subindex, payload),
            SdoAbortCode.AttemptWriteReadOnly);

        await DownloadAsync(master, 0x12, (ushort)index, (byte)subindex, payload);
        (await UploadUnsignedAsync(master, 0x12, (ushort)index, (byte)subindex)).Should().Be((uint)value);
        openDevice.ObjectDictionary.ReadUnsigned((ushort)index, (byte)subindex).Should().Be((uint)value);
    }

    // FR-CO-013: sub-index 04h of a TPDO communication record is reserved and absent, and CiA 301
    // §7.5.2.37 prescribes 0609 0011h ("sub-index does not exist") for an access to it — which is
    // a different answer from the 0602 0000h an object the node does not have at all gets
    // (Table 22).
    [Fact]
    public async Task The_Reserved_Subindex_04h_Of_A_Tpdo_Record_Is_Absent_And_Not_A_Missing_Object()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: Master);
        using var slave = CanOpen.OpenNode(busB, nodeId: Slave);

        await ExpectAbortAsync(() => UploadAsync(master, Slave, 0x1800, 0x04), SdoAbortCode.SubIndexDoesNotExist);
        await ExpectAbortAsync(() => DownloadAsync(master, Slave, 0x1800, 0x04, U16(0)), SdoAbortCode.SubIndexDoesNotExist);

        await ExpectAbortAsync(() => UploadAsync(master, Slave, 0x1900, 0x00), SdoAbortCode.ObjectDoesNotExist);
        await ExpectAbortAsync(() => DownloadAsync(master, Slave, 0x1900, 0x00, U8(0)), SdoAbortCode.ObjectDoesNotExist);
    }

    // =========================================================================================
    // FR-CO-014 — OD and runtime agree in both directions and on both write paths.
    // =========================================================================================

    // FR-CO-014 (a): a master's SDO download of 1017h starts the heartbeat producer (CiA 301
    // §7.5.2.20), and 0 disables it. Heartbeats arriving prove "on". "Off" is proved positively by
    // the node answering a guarding RTR, which §7.2.8.3.2.2 permits only while 1017h is 0 — see
    // AwaitGuardingReplyAsync — rather than by waiting for heartbeats not to arrive.
    [Fact]
    public async Task An_Sdo_Download_Of_1017h_Starts_The_Heartbeat_Producer_And_Zero_Stops_It()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var observer = Open(session, 2);
        using var master = CanOpen.OpenNode(busA, nodeId: Master);
        using var slave = CanOpen.OpenNode(busB, nodeId: Slave);

        var heartbeat = NewTcs<NmtState>();
        master.HeartbeatReceived += (_, e) =>
        {
            // The boot-up (Initializing) is not a heartbeat; the producer reports Pre-Operational.
            if (e.ProducerNodeId == Slave && e.State == NmtState.PreOperational) heartbeat.TrySetResult(e.State);
        };

        await DownloadAsync(master, Slave, 0x1017, 0x00, U16(100));
        await heartbeat.Task.WithTimeoutAsync(ShortTimeout);
        (await UploadUnsignedAsync(master, Slave, 0x1017, 0x00)).Should().Be(100u);

        await DownloadAsync(master, Slave, 0x1017, 0x00, U16(0));
        (await UploadUnsignedAsync(master, Slave, 0x1017, 0x00)).Should().Be(0u);
        await AwaitGuardingReplyAsync(observer, Slave);
    }

    // FR-CO-014 (b): StartHeartbeatProducer / StopHeartbeatProducer are writes to 1017h, and a
    // master reads the value the node runs with.
    [Fact]
    public async Task StartHeartbeatProducer_Is_Visible_As_1017h_Over_Sdo()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: Master);
        using var slave = CanOpen.OpenNode(busB, nodeId: Slave);

        slave.StartHeartbeatProducer(TimeSpan.FromMilliseconds(200));
        (await UploadUnsignedAsync(master, Slave, 0x1017, 0x00)).Should().Be(200u);

        slave.StopHeartbeatProducer();
        (await UploadUnsignedAsync(master, Slave, 0x1017, 0x00)).Should().Be(0u);
    }

    // FR-CO-014 (c), local path: the application writes 1006h (cycle period, µs) and sets bit 30
    // of 1005h through the dictionary, and the SYNC producer runs — CiA 301 §7.5.2.5, "the first
    // transmission of SYNC object starts within 1 sync cycle after setting bit 30 to 1b".
    [Fact]
    public async Task Local_Writes_To_1006h_And_1005h_Start_The_Sync_Producer()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var consumer = CanOpen.OpenNode(busA, nodeId: Master);
        using var producer = CanOpen.OpenNode(busB, nodeId: Slave);

        int syncs = 0;
        var enough = NewTcs<int>();
        consumer.SyncReceived += (_, _) =>
        {
            if (Interlocked.Increment(ref syncs) >= 2) enough.TrySetResult(syncs);
        };

        producer.ObjectDictionary.WriteUnsigned(0x1006, 0x00, 20_000);
        producer.ObjectDictionary.WriteUnsigned(0x1005, 0x00, CanOpenCobId.Sync | CanOpenCobId.SyncGenerateBit);

        (await enough.Task.WithTimeoutAsync(ShortTimeout)).Should().BeGreaterOrEqualTo(2);
        producer.ObjectDictionary.WriteUnsigned(0x1005, 0x00, CanOpenCobId.Sync);
    }

    // FR-CO-014 (c), method path: StartSyncProducer is the same two writes, and StopSyncProducer
    // clears bit 30 and leaves the period in place.
    [Fact]
    public async Task StartSyncProducer_Is_Visible_In_1005h_And_1006h_Over_Sdo()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: Master);
        using var slave = CanOpen.OpenNode(busB, nodeId: Slave);

        slave.StartSyncProducer(TimeSpan.FromMilliseconds(20));
        (await UploadUnsignedAsync(master, Slave, 0x1006, 0x00)).Should().Be(20_000u, "1006h is in µs (§7.5.2.6)");
        (await UploadUnsignedAsync(master, Slave, 0x1005, 0x00)).Should().Be(CanOpenCobId.Sync | CanOpenCobId.SyncGenerateBit);

        slave.StopSyncProducer();
        (await UploadUnsignedAsync(master, Slave, 0x1005, 0x00)).Should().Be(CanOpenCobId.Sync);
        (await UploadUnsignedAsync(master, Slave, 0x1006, 0x00)).Should().Be(20_000u);
    }

    // FR-CO-014 (d): the objects the node manages take values, not re-declarations — an Add* on
    // one of them throws — while the placeholders 1000h and 1018h are the application's to
    // replace.
    [Fact]
    public void Managed_Communication_Objects_Take_Values_Not_Redeclarations()
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var node = CanOpen.OpenNode(bus, nodeId: Slave);
        var od = node.ObjectDictionary;

        Action redeclareProducerHeartbeat = () => od.AddU32(0x1017, 0x00, 100);
        Action redeclareSyncCobId = () => od.AddU16(0x1005, 0x00, 0x80);
        redeclareProducerHeartbeat.Should().Throw<InvalidOperationException>();
        redeclareSyncCobId.Should().Throw<InvalidOperationException>();
        od.ReadUnsigned(0x1017, 0x00).Should().Be(0u, "the rejected re-declaration left the object as it was");
        od.ReadUnsigned(0x1005, 0x00).Should().Be(0x0000_0080u);

        od.AddU32(0x1000, 0x00, 0x0002_0191, OdAccess.ReadOnly);
        od.AddU32(0x1018, 0x02, 0x0000_1234, OdAccess.ReadOnly);
        od.ReadUnsigned(0x1000, 0x00).Should().Be(0x0002_0191u);
        od.ReadUnsigned(0x1018, 0x02).Should().Be(0x0000_1234u);
    }

    // FR-CO-014 (#133 review) — re-declaring an entry takes the write gate like a write does, so
    // a write validated against the old declaration is stored on it before the new one replaces
    // it and never lands on the new one. The test parks a U32 write in its validator while
    // another thread re-declares the entry as U8: the write completes, then the re-declaration.
    [Fact]
    public async Task A_Redeclaration_Waits_For_The_Write_In_Flight_On_The_Entry()
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var node = CanOpen.OpenNode(bus, nodeId: Slave);
        var od = node.ObjectDictionary;
        od.AddU32(0x2000, 0x00, 0);

        var inner = od.WriteValidator!;
        Task? redeclare = null;
        od.WriteValidator = (index, subindex, value) =>
        {
            if (index == 0x2000 && redeclare is null)
            {
                redeclare = Task.Run(() => od.AddU8(0x2000, 0x00, 0x01));
                Thread.Sleep(200); // a re-declaration that did not wait for the gate would land here
            }
            return inner(index, subindex, value);
        };

        var write = () => od.WriteUnsigned(0x2000, 0x00, 0x1122_3344);
        write.Should().NotThrow("the write is stored on the entry it was validated against");
        await redeclare!.WithTimeoutAsync(ShortTimeout);
        od.TryGet(0x2000, 0x00, out var entry).Should().BeTrue();
        entry.DataType.Should().Be(OdDataType.Unsigned8, "the re-declaration came after the write");
        od.ReadUnsigned(0x2000, 0x00).Should().Be(1u);
    }

    // FR-CO-020 (#133 review) — replacing the 1000h placeholder or extending 1018h goes through
    // Add*, whose mappability default serves application objects; that must not make a
    // communication-profile object a PDO target. The area is refused as a mapping target
    // whatever the entry's flag says — through the API and through a mapping-record write.
    [Fact]
    public void A_Replaced_Placeholder_Cannot_Become_A_Pdo_Target()
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var node = CanOpen.OpenNode(bus, nodeId: Slave);
        var od = node.ObjectDictionary;
        od.AddU32(0x1000, 0x00, 0x0002_0191, OdAccess.ReadOnly);
        od.AddU32(0x1018, 0x02, 0x0000_1234, OdAccess.ReadOnly);
        od.TryGet(0x1000, 0x00, out var deviceType).Should().BeTrue();
        deviceType.PdoMappable.Should().BeTrue("Add*'s default, meant for application objects");

        Action configure = () => node.ConfigureTpdo(1, new PdoMapping().Add(0x1000, 0x00, 32));
        configure.Should().Throw<ArgumentException>().Which.Message.Should().Contain("06040041");
        Action mapIdentity = () => od.WriteUnsigned(0x1A00, 0x01, 0x1018_0220);
        mapIdentity.Should().Throw<ArgumentException>().Which.Message.Should().Contain("06040041");
        od.ReadUnsigned(0x1A00, 0x00).Should().Be(0u, "nothing of the communication profile area was mapped");
    }

    // FR-CO-014 (#133 review) — a typed write resolves the entry's type under the write gate, so
    // a re-declaration is either fully before or fully after it: the value is encoded and
    // range-checked against the declaration it lands on. The test holds the gate in another
    // write's validator, starts the typed write, re-declares the entry inside the held gate,
    // releases — and the write must land on the new declaration.
    [Fact]
    public async Task A_Typed_Write_Resolves_Its_Type_Under_The_Write_Gate()
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var node = CanOpen.OpenNode(bus, nodeId: Slave);
        var od = node.ObjectDictionary;
        od.AddU8(0x2000, 0x00, 0);
        od.AddU8(0x2001, 0x00, 0);

        var inner = od.WriteValidator!;
        using var gateHeld = new ManualResetEventSlim(false);
        using var writeStarted = new ManualResetEventSlim(false);
        od.WriteValidator = (index, subindex, value) =>
        {
            if (index == 0x2001 && !gateHeld.IsSet)
            {
                gateHeld.Set();
                writeStarted.Wait(ShortTimeout);
                Thread.Sleep(200);          // the typed write has started and waits for the gate
                od.AddU32(0x2000, 0x00, 0); // re-declared while the gate is held
            }
            return inner(index, subindex, value);
        };
        var holder = Task.Run(() => od.WriteUnsigned(0x2001, 0x00, 1));
        gateHeld.Wait(ShortTimeout).Should().BeTrue();

        writeStarted.Set();
        var write = () => od.WriteUnsigned(0x2000, 0x00, 0x1122_3344);
        write.Should().NotThrow("the type is resolved once the gate is free — U32, which the value fits");
        await holder.WithTimeoutAsync(ShortTimeout);
        od.ReadUnsigned(0x2000, 0x00).Should().Be(0x1122_3344u);
    }

    // FR-CO-014 (d, #133 review) — the guard covers additions too: a sub-index the node does not
    // declare under a managed object cannot be added by the application, or the generic SDO
    // server would serve it — 1800h:04 is reserved and answers 0609 0011h, 1200h has no sub-index
    // 03h, and 1016h grows only through AddHeartbeatConsumer. The placeholders stay open.
    [Fact]
    public async Task Managed_Communication_Objects_Do_Not_Take_Added_Subindices_Either()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var tool = CanOpen.OpenNode(busA, nodeId: Tool);
        using var node = CanOpen.OpenNode(busB, nodeId: Slave);
        var od = node.ObjectDictionary;

        Action addReservedPdoSubindex = () => od.AddU32(0x1800, 0x04, 0);
        Action addSdoServerSubindex = () => od.AddU8(0x1200, 0x03, 0x11);
        Action growConsumerHeartbeatByHand = () => od.AddU32(0x1016, 0x02, 0);
        addReservedPdoSubindex.Should().Throw<InvalidOperationException>();
        addSdoServerSubindex.Should().Throw<InvalidOperationException>();
        growConsumerHeartbeatByHand.Should().Throw<InvalidOperationException>();
        od.TryGet(0x1800, 0x04, out _).Should().BeFalse();
        od.TryGet(0x1200, 0x03, out _).Should().BeFalse();
        od.TryGet(0x1016, 0x02, out _).Should().BeFalse();
        await ExpectAbortAsync(() => UploadAsync(tool, Slave, 0x1800, 0x04), SdoAbortCode.SubIndexDoesNotExist);

        node.AddHeartbeatConsumer(0x12, TimeSpan.FromSeconds(1));
        node.AddHeartbeatConsumer(0x13, TimeSpan.FromSeconds(1));
        od.ReadUnsigned(0x1016, 0x00).Should().Be(2u, "the node itself grows the array");
        od.AddU32(0x1018, 0x03, 0x0001_0000, OdAccess.ReadOnly);
        od.ReadUnsigned(0x1018, 0x03).Should().Be(0x0001_0000u, "the identity placeholder is the application's to extend");
    }

    // FR-CO-014 (e): 1001h "is a part of an emergency object" (CiA 301 §7.5.2.2) — the error
    // register a master reads over SDO is the one the EMCY carried.
    [Fact]
    public async Task SendEmcyAsync_Writes_The_Error_Register_Into_1001h()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: Master);
        using var slave = CanOpen.OpenNode(busB, nodeId: Slave);

        var received = NewTcs<EmcyMessage>();
        master.EmcyReceived += (_, e) =>
        {
            if (e.Message.ProducerNodeId == Slave) received.TrySetResult(e.Message);
        };

        await slave.SendEmcyAsync(errorCode: 0x8110, errorRegister: 0x81);

        (await received.Task.WithTimeoutAsync(ShortTimeout)).ErrorRegister.Should().Be((byte)0x81);
        (await UploadUnsignedAsync(master, Slave, 0x1001, 0x00)).Should().Be(0x81u);
    }

    // FR-CO-014 (#133 review) — ConfigureTpdo / ConfigureRpdo are several dictionary writes, and
    // they are one transaction: the whole sequence runs on the node's actor loop — one dedicated
    // thread, on which the SDO server stores its downloads too — so neither a second caller nor
    // an SDO remap can interleave with it. The dictionary's write event fires on the writing
    // thread; the test compares the thread of every write of the sequence with the thread an SDO
    // download is stored on, and with the caller's.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConfigurePdo_Writes_Its_Records_On_The_Actor_Loop_As_One_Transaction(bool isTpdo)
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: Master);
        using var slave = CanOpen.OpenNode(busB, nodeId: Slave);
        slave.ObjectDictionary.AddU16(0x2000, 0x00, 0xBEEF);
        ushort comm = isTpdo ? (ushort)0x1800 : (ushort)0x1400;
        ushort map = isTpdo ? (ushort)0x1A00 : (ushort)0x1600;

        var writes = new List<(ushort Index, byte Subindex, int Thread)>();
        slave.ObjectDictionary.EntryWritten += (index, subindex) =>
        {
            lock (writes) writes.Add((index, subindex, Environment.CurrentManagedThreadId));
        };

        await DownloadAsync(master, Slave, 0x1017, 0x00, new byte[] { 0xE8, 0x03 });
        int loopThread;
        lock (writes) loopThread = writes.Single(w => w.Index == 0x1017).Thread;

        int callerThread = await Task.Run(() =>
        {
            if (isTpdo) slave.ConfigureTpdo(1, new PdoMapping().Add(0x2000, 0x00, 16));
            else slave.ConfigureRpdo(1, new PdoMapping().Add(0x2000, 0x00, 16));
            return Environment.CurrentManagedThreadId;
        });
        callerThread.Should().NotBe(loopThread, "the caller is a pool thread, the loop a dedicated one");

        (ushort Index, byte Subindex, int Thread)[] sequence;
        lock (writes) sequence = writes.Where(w => w.Index == comm || w.Index == map).ToArray();
        sequence.Select(w => w.Index).Distinct().Should().BeEquivalentTo(new[] { comm, map }, "both records are written");
        sequence.Should().OnlyContain(w => w.Thread == loopThread,
            "every write of the sequence is stored on the loop, where nothing can interleave with it; none on the caller");
        slave.ObjectDictionary.ReadUnsigned(map, 0x00).Should().Be(1u);
        slave.ObjectDictionary.ReadUnsigned(comm, 0x01).Should().Be(
            isTpdo ? CanOpenCobId.TpdoDefault(Slave, 1) : CanOpenCobId.RpdoDefault(Slave, 1), "the PDO exists once the transaction ran");
    }

    // FR-CO-014 (#133 review) — a direct dictionary write is a third writer, on its own thread, and
    // it must not land between two writes of a configuration sequence either: the sequence holds
    // the dictionary's write gate as one transaction. The validator runs inside the gate, so the
    // order of its calls is the order of the writes; the test parks the sequence in the validator
    // of its first write while four threads hammer an application object, and requires that none
    // of their writes was validated between the sequence's first write and its last.
    [Fact]
    public async Task A_Direct_Write_Cannot_Land_Inside_A_ConfigureTpdo_Transaction()
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var slave = CanOpen.OpenNode(bus, nodeId: Slave);
        var od = slave.ObjectDictionary;
        od.AddU16(0x2000, 0x00, 0xBEEF);
        od.AddU8(0x2001, 0x00, 0x00);

        var validated = new List<ushort>();
        var inner = od.WriteValidator!;
        using var sequenceStarted = new ManualResetEventSlim(false);
        od.WriteValidator = (index, subindex, value) =>
        {
            lock (validated) validated.Add(index);
            if (index == 0x1800 && subindex == 0x01 && !sequenceStarted.IsSet)
            {
                sequenceStarted.Set();
                Thread.Sleep(100); // the hammers are at the gate before the sequence goes on
            }
            return inner(index, subindex, value);
        };
        using var stop = new CancellationTokenSource();
        var hammers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            sequenceStarted.Wait(ShortTimeout);
            while (!stop.IsCancellationRequested) od.WriteUnsigned(0x2001, 0x00, 1);
        })).ToArray();

        slave.ConfigureTpdo(1, new PdoMapping().Add(0x2000, 0x00, 16));
        stop.Cancel();
        await Task.WhenAll(hammers).WithTimeoutAsync(ShortTimeout);

        ushort[] order;
        lock (validated) order = validated.ToArray();
        int first = Array.IndexOf(order, (ushort)0x1800);
        int last = Array.LastIndexOf(order, (ushort)0x1800);
        first.Should().BeGreaterThanOrEqualTo(0);
        order.Skip(first).Take(last - first + 1).Should().OnlyContain(index => index == 0x1800 || index == 0x1A00,
            "between the first write of the sequence (destroy) and its last (create) no other writer got in");
        order.Should().Contain(0x2001, "the hammers did write — before the sequence or after it");
        od.ReadUnsigned(0x1A00, 0x00).Should().Be(1u);
        od.ReadUnsigned(0x1800, 0x01).Should().Be(CanOpenCobId.TpdoDefault(Slave, 1));
    }

    // =========================================================================================
    // FR-CO-018 — SYNC and EMCY COB-IDs: control bits, restricted CAN-IDs, and where the frames go.
    // =========================================================================================

    // FR-CO-018: CiA 301 §7.5.2.5 — a node supporting base frames only answers bit 29 with
    // 0609 0030h; §7.3.5 Table 40 — a restricted CAN-ID "shall not be used as a CAN-ID by any
    // configurable communication object, neither for SYNC, …". The value is left as it was.
    [Theory]
    [InlineData(0x2000_0080u)] // bit 29: 29-bit CAN-ID
    [InlineData(0x0000_0000u)] // 000h: NMT
    [InlineData(0x0000_007Fu)] // 001h–07Fh: reserved
    [InlineData(0x0000_0701u)] // 701h–77Fh: NMT error control
    public async Task A_Sync_CobId_With_Bit29_Or_A_Restricted_CanId_Is_Rejected_Over_Sdo(uint word)
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: Master);
        using var slave = CanOpen.OpenNode(busB, nodeId: Slave);

        await ExpectAbortAsync(() => DownloadAsync(master, Slave, 0x1005, 0x00, U32(word)), SdoAbortCode.ValueRangeExceeded);

        (await UploadUnsignedAsync(master, Slave, 0x1005, 0x00)).Should().Be(0x0000_0080u);
    }

    // FR-CO-018: the same rule on the local path — the dictionary refuses the value with an
    // ArgumentException naming the abort code the SDO write would have produced.
    [Fact]
    public void A_Sync_CobId_With_Bit29_Is_Rejected_Locally_With_The_Same_Abort_Code()
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var node = CanOpen.OpenNode(bus, nodeId: Slave);

        Action write = () => node.ObjectDictionary.WriteUnsigned(0x1005, 0x00, 0x2000_0080u);

        write.Should().Throw<ArgumentException>().Which.Message.Should().Contain("06090030");
        node.ObjectDictionary.ReadUnsigned(0x1005, 0x00).Should().Be(0x0000_0080u);
    }

    // FR-CO-018: CiA 301 §7.5.2.5 — "It is not allowed to change bits 0 to 29, while the object
    // exists (bit 30 = 1b)." Clearing bit 30 in the same write ends the object, so that write may
    // carry a new CAN-ID.
    [Fact]
    public async Task The_Sync_CanId_Cannot_Change_While_The_Node_Generates_Sync()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: Master);
        using var slave = CanOpen.OpenNode(busB, nodeId: Slave);

        slave.StartSyncProducer(TimeSpan.FromMilliseconds(20));

        await ExpectAbortAsync(
            () => DownloadAsync(master, Slave, 0x1005, 0x00, U32(CanOpenCobId.SyncGenerateBit | 0x0F0)),
            SdoAbortCode.ValueRangeExceeded);
        (await UploadUnsignedAsync(master, Slave, 0x1005, 0x00)).Should().Be(CanOpenCobId.SyncGenerateBit | CanOpenCobId.Sync);

        await DownloadAsync(master, Slave, 0x1005, 0x00, U32(0x0F0));
        (await UploadUnsignedAsync(master, Slave, 0x1005, 0x00)).Should().Be(0x0F0u);
    }

    // FR-CO-018: the SYNC producer transmits on the CAN-ID configured in 1005h, not on 080h.
    [Fact]
    public async Task The_Sync_Producer_Transmits_On_The_CanId_Configured_In_1005h()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var observer = Open(session, 1);
        using var producer = CanOpen.OpenNode(busA, nodeId: Slave);

        int syncs = 0;
        var enough = NewTcs<int>();
        observer.FrameObserved += (_, e) =>
        {
            var frame = e.CanFrame;
            if (frame.IsExtendedFrame || frame.IsRemoteFrame || (uint)frame.ID != 0x0F0 || frame.Data.Length != 0) return;
            if (Interlocked.Increment(ref syncs) >= 2) enough.TrySetResult(syncs);
        };

        producer.ObjectDictionary.WriteUnsigned(0x1005, 0x00, 0x0F0);
        producer.StartSyncProducer(TimeSpan.FromMilliseconds(20));

        (await enough.Task.WithTimeoutAsync(ShortTimeout)).Should().BeGreaterOrEqualTo(2);
        producer.ObjectDictionary.ReadUnsigned(0x1005, 0x00).Should().Be(CanOpenCobId.SyncGenerateBit | 0x0F0);
        producer.StopSyncProducer();
    }

    // FR-CO-018: a node whose 1005h a master moved to 0F0h raises SyncReceived for a frame there
    // and no longer for one on 080h. The negative is decided by ordering, not by waiting: each
    // SYNC candidate is followed by an NMT command addressed to the node, all transmitted from
    // one bus; they travel one subscription and one actor mailbox, and their events leave one
    // event pump in order — so the SyncReceived count at each NmtCommandReceived is the count the
    // frame before it produced. A marker after each frame is what tells "080h ignored, 0F0h
    // counted" apart from the reverse, which a single total of 1 cannot.
    [Fact]
    public async Task The_Sync_Consumer_Listens_On_The_CanId_Configured_In_1005h_And_No_Longer_On_080h()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: Master);
        using var consumer = CanOpen.OpenNode(busB, nodeId: Slave);

        await DownloadAsync(master, Slave, 0x1005, 0x00, U32(0x0F0));

        int syncs = 0;
        int markers = 0;
        var syncsAfter080h = NewTcs<int>();
        var syncsAfter0F0h = NewTcs<int>();
        consumer.SyncReceived += (_, _) => Interlocked.Increment(ref syncs);
        consumer.NmtCommandReceived += (_, e) =>
        {
            if (e.Command != NmtCommand.EnterPreOperational || e.TargetNodeId != Slave) return;
            switch (Interlocked.Increment(ref markers))
            {
                case 1: syncsAfter080h.TrySetResult(Volatile.Read(ref syncs)); break;
                case 2: syncsAfter0F0h.TrySetResult(Volatile.Read(ref syncs)); break;
            }
        };

        busA.Transmit(CanFrame.Classic(0x080, Array.Empty<byte>()));
        busA.Transmit(CanFrame.Classic(0x000, new byte[] { (byte)NmtCommand.EnterPreOperational, Slave }));
        busA.Transmit(CanFrame.Classic(0x0F0, Array.Empty<byte>()));
        busA.Transmit(CanFrame.Classic(0x000, new byte[] { (byte)NmtCommand.EnterPreOperational, Slave }));

        (await syncsAfter080h.Task.WithTimeoutAsync(ShortTimeout)).Should().Be(0,
            "080h is no longer the SYNC COB-ID, and the frame there was dispatched before the first marker");
        (await syncsAfter0F0h.Task.WithTimeoutAsync(ShortTimeout)).Should().Be(1,
            "0F0h is the SYNC COB-ID, and the frame there was dispatched before the second marker");
    }

    // FR-CO-018: CiA 301 §7.5.2.17 Table 59 — bit 30 of 1014h is "reserved (always 0b)", and
    // "the bits 0 to 29 shall not be changed, while the object exists and is valid (bit 31 = 0b)".
    [Fact]
    public async Task The_Emcy_CobId_1014h_Rejects_Bit30_And_A_CanId_Change_While_Valid()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: Master);
        using var slave = CanOpen.OpenNode(busB, nodeId: Slave);

        await ExpectAbortAsync(
            () => DownloadAsync(master, Slave, 0x1014, 0x00, U32(0x4000_0000u | CanOpenCobId.Emcy(Slave))),
            SdoAbortCode.ValueRangeExceeded);
        await ExpectAbortAsync(
            () => DownloadAsync(master, Slave, 0x1014, 0x00, U32(0x0F5)),
            SdoAbortCode.ValueRangeExceeded);

        (await UploadUnsignedAsync(master, Slave, 0x1014, 0x00)).Should().Be(CanOpenCobId.Emcy(Slave));
    }

    // FR-CO-018: bit 31 of 1014h set means "EMCY does not exist / is not valid" (Table 59), and
    // the node has no EMCY to send; clearing it brings the service back.
    [Fact]
    public async Task Emcy_Disabled_Through_Bit31_Of_1014h_Makes_SendEmcyAsync_Fault()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: Master);
        using var slave = CanOpen.OpenNode(busB, nodeId: Slave);

        await DownloadAsync(master, Slave, 0x1014, 0x00, U32(CanOpenCobId.InvalidBit | CanOpenCobId.Emcy(Slave)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => slave.SendEmcyAsync(errorCode: 0x1000, errorRegister: 0x01));

        var received = NewTcs<EmcyMessage>();
        master.EmcyReceived += (_, e) =>
        {
            if (e.Message.ProducerNodeId == Slave) received.TrySetResult(e.Message);
        };
        await DownloadAsync(master, Slave, 0x1014, 0x00, U32(CanOpenCobId.Emcy(Slave)));
        await slave.SendEmcyAsync(errorCode: 0x1000, errorRegister: 0x01);
        (await received.Task.WithTimeoutAsync(ShortTimeout)).ErrorCode.Should().Be((ushort)0x1000);
    }

    // FR-CO-018: an EMCY COB-ID a master moved over SDO is where the EMCY goes out. Since bits
    // 0..29 may only change while the object is not valid (§7.5.2.17), the move is two writes:
    // invalidate-and-move, then validate.
    [Fact]
    public async Task An_Emcy_CobId_Moved_Over_Sdo_Is_Where_The_Emcy_Goes_Out()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var observer = Open(session, 2);
        using var master = CanOpen.OpenNode(busA, nodeId: Master);
        using var slave = CanOpen.OpenNode(busB, nodeId: Slave);

        const uint movedTo = 0x0F5;
        var seen = NewTcs<byte[]>();
        observer.FrameObserved += (_, e) =>
        {
            var frame = e.CanFrame;
            if (frame.IsExtendedFrame || frame.IsRemoteFrame || (uint)frame.ID != movedTo) return;
            seen.TrySetResult(frame.Data.ToArray());
        };

        await DownloadAsync(master, Slave, 0x1014, 0x00, U32(CanOpenCobId.InvalidBit | movedTo));
        await DownloadAsync(master, Slave, 0x1014, 0x00, U32(movedTo));
        (await UploadUnsignedAsync(master, Slave, 0x1014, 0x00)).Should().Be(movedTo);

        await slave.SendEmcyAsync(errorCode: 0x8110, errorRegister: 0x01, manufacturerSpecific: new byte[] { 0xAA });

        var payload = await seen.Task.WithTimeoutAsync(ShortTimeout);
        payload.Should().Equal(0x10, 0x81, 0x01, 0xAA, 0x00, 0x00, 0x00, 0x00);
    }

    // =========================================================================================
    // FR-CO-019 — power-on values (CiA 301 §7.3.2.2.1): "the last stored parameters", else the
    // defaults; store and restore through 1010h / 1011h (§7.5.2.13 / §7.5.2.14).
    // =========================================================================================

    // FR-CO-019: a TPDO configured by the application and stored survives an NMT reset — of either
    // kind, since without a device description both restore the same set — and the node comes
    // back Pre-Operational after its boot-up.
    [Theory]
    [InlineData(NmtCommand.ResetCommunication)]
    [InlineData(NmtCommand.ResetNode)]
    public async Task A_Stored_Tpdo_Configuration_Survives_An_Nmt_Reset(NmtCommand reset)
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: Master);
        var bootups = new BootupWatch(master, Slave);
        using var slave = CanOpen.OpenNode(busB, nodeId: Slave);

        slave.ObjectDictionary.AddU16(0x2000, 0x00, 0xBEEF);
        slave.ConfigureTpdo(1, new PdoMapping().Add(0x2000, 0x00, 16));
        slave.StoreParameters();
        await bootups.First;

        await master.SendNmtCommandAsync(reset, Slave);
        await bootups.Second;

        (await UploadUnsignedAsync(master, Slave, 0x1800, 0x01)).Should().Be(CanOpenCobId.TpdoDefault(Slave, 1),
            "the stored TPDO1 is valid (bit 31 = 0b) on its pre-defined CAN-ID");
        (await UploadUnsignedAsync(master, Slave, 0x1A00, 0x00)).Should().Be(1u);
        slave.State.Should().Be(NmtState.PreOperational);
    }

    // FR-CO-019: without a store, the same reset returns the records to the defaults the node was
    // created with: TPDO1 "does not exist" and its mapping is empty.
    [Theory]
    [InlineData(NmtCommand.ResetCommunication)]
    [InlineData(NmtCommand.ResetNode)]
    public async Task Without_A_Store_An_Nmt_Reset_Returns_The_Tpdo_Records_To_Their_Defaults(NmtCommand reset)
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: Master);
        var bootups = new BootupWatch(master, Slave);
        using var slave = CanOpen.OpenNode(busB, nodeId: Slave);

        slave.ObjectDictionary.AddU16(0x2000, 0x00, 0xBEEF);
        slave.ConfigureTpdo(1, new PdoMapping().Add(0x2000, 0x00, 16));
        (await UploadUnsignedAsync(master, Slave, 0x1800, 0x01)).Should().Be(CanOpenCobId.TpdoDefault(Slave, 1));
        (await UploadUnsignedAsync(master, Slave, 0x1A00, 0x00)).Should().Be(1u);
        await bootups.First;

        await master.SendNmtCommandAsync(reset, Slave);
        await bootups.Second;

        uint cobId = await UploadUnsignedAsync(master, Slave, 0x1800, 0x01);
        (cobId & CanOpenCobId.InvalidBit).Should().Be(CanOpenCobId.InvalidBit, "TPDO1 is back to 'does not exist'");
        (cobId & CanOpenCobId.CanIdMask).Should().Be(CanOpenCobId.TpdoDefault(Slave, 1));
        (await UploadUnsignedAsync(master, Slave, 0x1A00, 0x00)).Should().Be(0u);
        slave.State.Should().Be(NmtState.PreOperational);
    }

    // FR-CO-019: a master writing "save" to 1010h:01 (CiA 301 §7.5.2.13 Figure 55) stores the
    // parameters exactly as StoreParameters does; the read value 1 announces "saves parameters on
    // command" (Figure 56).
    [Fact]
    public async Task Save_Written_To_1010h_Over_Sdo_Stores_The_Parameters_Like_StoreParameters()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: Master);
        var bootups = new BootupWatch(master, Slave);
        using var slave = CanOpen.OpenNode(busB, nodeId: Slave);

        slave.ObjectDictionary.AddU16(0x2000, 0x00, 0xBEEF);
        slave.ConfigureTpdo(1, new PdoMapping().Add(0x2000, 0x00, 16));

        (await UploadUnsignedAsync(master, Slave, 0x1010, 0x01)).Should().Be(1u);
        await DownloadAsync(master, Slave, 0x1010, 0x01, SaveSignature);
        (await UploadUnsignedAsync(master, Slave, 0x1010, 0x01)).Should().Be(1u, "the signature is a command, not a value");
        await bootups.First;

        await master.SendNmtCommandAsync(NmtCommand.ResetCommunication, Slave);
        await bootups.Second;

        (await UploadUnsignedAsync(master, Slave, 0x1800, 0x01)).Should().Be(CanOpenCobId.TpdoDefault(Slave, 1));
        (await UploadUnsignedAsync(master, Slave, 0x1A00, 0x00)).Should().Be(1u);
    }

    // FR-CO-019: "load" written to 1011h:01 (CiA 301 §7.5.2.14 Figure 57) after a store makes the
    // next reset restore the defaults — and only the reset: "The default values shall be set
    // valid after the CANopen device is reset" (Figure 58), so the stored configuration is still
    // in force in between.
    [Fact]
    public async Task Load_Written_To_1011h_After_A_Store_Makes_The_Next_Reset_Restore_The_Defaults()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: Master);
        var bootups = new BootupWatch(master, Slave);
        using var slave = CanOpen.OpenNode(busB, nodeId: Slave);

        slave.ObjectDictionary.AddU16(0x2000, 0x00, 0xBEEF);
        slave.ConfigureTpdo(1, new PdoMapping().Add(0x2000, 0x00, 16));
        slave.StoreParameters();

        (await UploadUnsignedAsync(master, Slave, 0x1011, 0x01)).Should().Be(1u);
        await DownloadAsync(master, Slave, 0x1011, 0x01, LoadSignature);
        (await UploadUnsignedAsync(master, Slave, 0x1800, 0x01)).Should().Be(CanOpenCobId.TpdoDefault(Slave, 1),
            "the defaults become valid with the reset, not with the command");
        await bootups.First;

        await master.SendNmtCommandAsync(NmtCommand.ResetCommunication, Slave);
        await bootups.Second;

        uint cobId = await UploadUnsignedAsync(master, Slave, 0x1800, 0x01);
        (cobId & CanOpenCobId.InvalidBit).Should().Be(CanOpenCobId.InvalidBit, "the stored TPDO1 was discarded");
        (await UploadUnsignedAsync(master, Slave, 0x1A00, 0x00)).Should().Be(0u);
    }

    // FR-CO-019: "If a wrong signature is written, the CANopen device shall refuse to store [/
    // restore] and it shall respond with the SDO abort transfer service (abort code: 0800 002xh)"
    // (§7.5.2.13 / §7.5.2.14). The other object's signature is the wrong one here.
    [Theory]
    [InlineData(0x1010, new byte[] { 0x6C, 0x6F, 0x61, 0x64 })] // "load" written to store parameters
    [InlineData(0x1011, new byte[] { 0x73, 0x61, 0x76, 0x65 })] // "save" written to restore defaults
    public async Task A_Wrong_Signature_Written_To_1010h_Or_1011h_Is_Refused_With_0800_0020h(int index, byte[] signature)
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: Master);
        using var slave = CanOpen.OpenNode(busB, nodeId: Slave);

        await ExpectAbortAsync(
            () => DownloadAsync(master, Slave, (ushort)index, 0x01, signature),
            SdoAbortCode.DataCannotBeTransferred);

        (await UploadUnsignedAsync(master, Slave, (ushort)index, 0x01)).Should().Be(1u);
    }

    // FR-CO-019: 1017h is a communication parameter like the PDO records — a heartbeat producer a
    // master configured over SDO survives a reset only after a store. "Survives" is a heartbeat
    // delivered after the reset's boot-up; "is off" is the node answering a guarding RTR, which it
    // does only while 1017h is 0 (see AwaitGuardingReplyAsync).
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_Heartbeat_Producer_Configured_Over_Sdo_Survives_A_Reset_Only_After_A_Store(bool stored)
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var observer = Open(session, 2);
        using var master = CanOpen.OpenNode(busA, nodeId: Master);

        // One handler, so the order of the events it sees is the order the pump delivered them:
        // the first heartbeat proves the producer started, the second boot-up marks the reset,
        // and a heartbeat after that boot-up is one the producer sent from its restored 1017h.
        int bootups = 0;
        var firstBootup = NewTcs<bool>();
        var secondBootup = NewTcs<bool>();
        var heartbeatBeforeReset = NewTcs<bool>();
        var heartbeatAfterReset = NewTcs<bool>();
        master.HeartbeatReceived += (_, e) =>
        {
            if (e.ProducerNodeId != Slave) return;
            if (e.State == NmtState.Initializing)
            {
                switch (++bootups)
                {
                    case 1: firstBootup.TrySetResult(true); break;
                    case 2: secondBootup.TrySetResult(true); break;
                }
                return;
            }
            if (e.State != NmtState.PreOperational) return;
            if (bootups >= 2) heartbeatAfterReset.TrySetResult(true);
            else heartbeatBeforeReset.TrySetResult(true);
        };
        using var slave = CanOpen.OpenNode(busB, nodeId: Slave);

        await DownloadAsync(master, Slave, 0x1017, 0x00, U16(50));
        await heartbeatBeforeReset.Task.WithTimeoutAsync(ShortTimeout);
        if (stored) await DownloadAsync(master, Slave, 0x1010, 0x01, SaveSignature);
        await firstBootup.Task.WithTimeoutAsync(ShortTimeout);

        await master.SendNmtCommandAsync(NmtCommand.ResetCommunication, Slave);
        await secondBootup.Task.WithTimeoutAsync(ShortTimeout);

        if (stored)
        {
            (await UploadUnsignedAsync(master, Slave, 0x1017, 0x00)).Should().Be(50u);
            await heartbeatAfterReset.Task.WithTimeoutAsync(ShortTimeout);
        }
        else
        {
            (await UploadUnsignedAsync(master, Slave, 0x1017, 0x00)).Should().Be(0u);
            await AwaitGuardingReplyAsync(observer, Slave);
        }
    }

    // =========================================================================================
    // FR-CO-023 — the heartbeat objects 1016h / 1017h.
    // =========================================================================================

    // FR-CO-023: AddHeartbeatConsumer fills 1016h — the array grows by one sub-index per producer,
    // each entry laid out as CiA 301 §7.5.2.19 Figure 62 (node-id in bits 23..16, time in ms in
    // bits 15..0) — and a second registration for the same producer reuses its slot.
    [Fact]
    public async Task AddHeartbeatConsumer_Grows_1016h_And_Reuses_A_Producers_Slot()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: Master);
        using var tool = CanOpen.OpenNode(busB, nodeId: Tool);

        master.AddHeartbeatConsumer(0x11, TimeSpan.FromSeconds(1));
        master.AddHeartbeatConsumer(0x12, TimeSpan.FromSeconds(2));
        master.AddHeartbeatConsumer(0x13, TimeSpan.FromSeconds(3));

        var od = master.ObjectDictionary;
        od.ReadUnsigned(0x1016, 0x00).Should().Be(3u);
        od.ReadUnsigned(0x1016, 0x01).Should().Be(HeartbeatEntry(0x11, 1000));
        od.ReadUnsigned(0x1016, 0x02).Should().Be(HeartbeatEntry(0x12, 2000));
        od.ReadUnsigned(0x1016, 0x03).Should().Be(HeartbeatEntry(0x13, 3000));

        master.AddHeartbeatConsumer(0x12, TimeSpan.FromSeconds(4));
        od.ReadUnsigned(0x1016, 0x00).Should().Be(3u, "the producer already had a slot");
        od.ReadUnsigned(0x1016, 0x02).Should().Be(HeartbeatEntry(0x12, 4000));

        // The array is what a configuration tool reads over SDO.
        (await UploadUnsignedAsync(tool, Master, 0x1016, 0x00)).Should().Be(3u);
        (await UploadUnsignedAsync(tool, Master, 0x1016, 0x03)).Should().Be(HeartbeatEntry(0x13, 3000));
    }

    // FR-CO-023 (#133 review) — 1016h:00 is read-only on the bus, but a local write reaches it, and
    // a count above 127 would drive the loops over the array on the actor round the clock (a byte
    // 255 + 1 is 0). The write is refused with the value-range abort, as the SDO layer would.
    [Theory]
    [InlineData(0x80u)]
    [InlineData(0xFFu)]
    public void A_Local_Write_Of_1016h_Sub0_Above_127_Is_Rejected(uint count)
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var node = CanOpen.OpenNode(bus, nodeId: Slave);

        var write = () => node.ObjectDictionary.WriteUnsigned(0x1016, 0x00, count);
        write.Should().Throw<ArgumentException>().Which.Message.Should().Contain("06090030");
        node.ObjectDictionary.ReadUnsigned(0x1016, 0x00).Should().Be(1u, "the count is what the node was created with");
        node.ObjectDictionary.WriteUnsigned(0x1016, 0x00, 0x7F);
        node.ObjectDictionary.ReadUnsigned(0x1016, 0x00).Should().Be(0x7Fu, "127 is the largest count the object can hold");
    }

    // FR-CO-023: RemoveHeartbeatConsumer clears the producer's slot and the consumer stops
    // reporting it. The negative is decided by ordering: the removal's apply is posted to the
    // actor before an NMT command to the node is even transmitted, so NmtCommandReceived is a
    // marker that the removal has been applied; a fresh consumer's timeouts, which fire from the
    // same due-time-sorted timer list, are the clock after it. A consumer that had survived
    // removal (100 ms) would fire at least twice between two timeouts of the witness (300 ms).
    [Fact]
    public async Task RemoveHeartbeatConsumer_Clears_The_1016h_Slot_And_The_Timeout_Stops()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: Master);

        var log = new List<string>();
        var removedConsumerFired = NewTcs<bool>();
        var enough = NewTcs<string[]>();
        bool markerSeen = false;
        int witnessTimeoutsAfterMarker = 0;
        master.HeartbeatTimeout += (_, e) =>
        {
            lock (log)
            {
                log.Add($"timeout:{e.ProducerNodeId:X2}");
                if (e.ProducerNodeId == 0x12) removedConsumerFired.TrySetResult(true);
                if (markerSeen && e.ProducerNodeId == 0x13 && ++witnessTimeoutsAfterMarker == 2)
                    enough.TrySetResult(log.ToArray());
            }
        };
        master.NmtCommandReceived += (_, e) =>
        {
            if (e.Command != NmtCommand.EnterPreOperational || e.TargetNodeId != Master) return;
            lock (log)
            {
                log.Add("marker");
                markerSeen = true;
            }
        };

        master.AddHeartbeatConsumer(0x12, TimeSpan.FromMilliseconds(100));
        await removedConsumerFired.Task.WithTimeoutAsync(ShortTimeout);

        master.RemoveHeartbeatConsumer(0x12);
        master.ObjectDictionary.ReadUnsigned(0x1016, 0x00).Should().Be(1u);
        master.ObjectDictionary.ReadUnsigned(0x1016, 0x01).Should().Be(0u, "the slot is cleared, not removed");

        busB.Transmit(CanFrame.Classic(0x000, new byte[] { (byte)NmtCommand.EnterPreOperational, Master }));

        master.AddHeartbeatConsumer(0x13, TimeSpan.FromMilliseconds(300));
        master.ObjectDictionary.ReadUnsigned(0x1016, 0x00).Should().Be(1u, "the cleared slot is reused");
        master.ObjectDictionary.ReadUnsigned(0x1016, 0x01).Should().Be(HeartbeatEntry(0x13, 300));

        var events = await enough.Task.WithTimeoutAsync(ShortTimeout);
        events.SkipWhile(entry => entry != "marker").Should().NotContain("timeout:12",
            "once the removal is applied the consumer for 0x12 is gone; only the witness reports");
    }

    // FR-CO-023: a consumer heartbeat time a master writes to 1016h over SDO is a consumer like one
    // AddHeartbeatConsumer registers — it reports the producer when it stays silent.
    [Fact]
    public async Task An_Sdo_Download_To_1016h_Creates_A_Consumer_That_Reports_A_Silent_Producer()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: Master);
        using var tool = CanOpen.OpenNode(busB, nodeId: Tool);

        var timeout = NewTcs<HeartbeatTimeoutEventArgs>();
        master.HeartbeatTimeout += (_, e) =>
        {
            if (e.ProducerNodeId == 0x11) timeout.TrySetResult(e);
        };

        await DownloadAsync(tool, Master, 0x1016, 0x01, U32(HeartbeatEntry(0x11, 100)));

        var evt = await timeout.Task.WithTimeoutAsync(ShortTimeout);
        evt.Timeout.Should().Be(TimeSpan.FromMilliseconds(100));
    }

    // FR-CO-023: CiA 301 §7.5.2.19 — "An attempt to configure several heartbeat times unequal 0
    // for the same node-ID the CANopen device shall be responded with the SDO abort transfer
    // service (abort code: 0604 0043h)." Re-writing the producer's own slot is not that.
    [Fact]
    public async Task Two_1016h_Entries_For_The_Same_NodeId_Are_Refused_With_0604_0043h()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: Master);
        using var tool = CanOpen.OpenNode(busB, nodeId: Tool);

        master.AddHeartbeatConsumer(0x11, TimeSpan.FromSeconds(1));
        master.AddHeartbeatConsumer(0x12, TimeSpan.FromSeconds(1));

        await ExpectAbortAsync(
            () => DownloadAsync(tool, Master, 0x1016, 0x02, U32(HeartbeatEntry(0x11, 500))),
            SdoAbortCode.GeneralParameterIncompatibility);
        (await UploadUnsignedAsync(tool, Master, 0x1016, 0x02)).Should().Be(HeartbeatEntry(0x12, 1000));

        await DownloadAsync(tool, Master, 0x1016, 0x01, U32(HeartbeatEntry(0x11, 500)));
        (await UploadUnsignedAsync(tool, Master, 0x1016, 0x01)).Should().Be(HeartbeatEntry(0x11, 500));
    }

    // FR-CO-023: bits 31..24 of a consumer heartbeat time are reserved (00h) per Figure 62; a
    // value with any of them set is out of range (0609 0030h).
    [Fact]
    public async Task A_1016h_Entry_With_Bits_31_To_24_Set_Is_Refused_With_0609_0030h()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: Master);
        using var tool = CanOpen.OpenNode(busB, nodeId: Tool);

        await ExpectAbortAsync(
            () => DownloadAsync(tool, Master, 0x1016, 0x01, U32(0x0100_0000u | HeartbeatEntry(0x11, 100))),
            SdoAbortCode.ValueRangeExceeded);

        (await UploadUnsignedAsync(tool, Master, 0x1016, 0x01)).Should().Be(0u);
    }

    // FR-CO-023: CiA 301 §7.5.2.19 — "If the heartbeat time is 0 or the node-ID is 0 or greater
    // than 127 the corresponding object entry shall be not used." The write is accepted and
    // stored, and no consumer results. That no timeout is raised is decided by ordering on the
    // actor's due-time-sorted timer list: a node-guarding consumer started afterwards arms a
    // life-time deadline (3 × 100 ms) due later than any deadline the entry could have armed
    // (100 ms, or 0), so when its timeout has been delivered any heartbeat timeout would have been
    // delivered before it.
    [Theory]
    [InlineData(0x0000_0064u)] // node-id 0, 100 ms
    [InlineData(0x0011_0000u)] // node-id 11h, 0 ms
    public async Task A_1016h_Entry_With_NodeId_0_Or_Time_0_Is_Accepted_But_Unused(uint entry)
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: Master);
        using var tool = CanOpen.OpenNode(busB, nodeId: Tool);

        var heartbeatTimeouts = new List<byte>();
        var witness = NewTcs<byte[]>();
        master.HeartbeatTimeout += (_, e) =>
        {
            lock (heartbeatTimeouts) heartbeatTimeouts.Add(e.ProducerNodeId);
        };
        master.NodeGuardingTimeout += (_, e) =>
        {
            if (e.ProducerNodeId != 0x7F) return;
            lock (heartbeatTimeouts) witness.TrySetResult(heartbeatTimeouts.ToArray());
        };

        await DownloadAsync(tool, Master, 0x1016, 0x01, U32(entry));
        (await UploadUnsignedAsync(tool, Master, 0x1016, 0x01)).Should().Be(entry, "the entry is stored as written");

        master.StartNodeGuardingConsumer(0x7F, TimeSpan.FromMilliseconds(100), lifeTimeFactor: 3);

        var timeoutsBeforeWitness = await witness.Task.WithTimeoutAsync(ShortTimeout);
        timeoutsBeforeWitness.Should().BeEmpty("an unused entry is no consumer and reports nothing");
    }

    // FR-CO-023 (#133 review) — an NMT reset restores 1016h:00 to the stored count and zeroes the
    // entries the array had grown by since, but the sub-indices themselves stay declared. Growing
    // the array again must reuse them: the entry is written, not re-declared, and the count moves
    // with it.
    [Fact]
    public async Task AddHeartbeatConsumer_Reuses_The_1016h_Slots_A_Reset_Hid()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var tool = CanOpen.OpenNode(busA, nodeId: Tool);
        var bootups = new BootupWatch(tool, Master);
        using var master = CanOpen.OpenNode(busB, nodeId: Master);
        await bootups.First;

        master.AddHeartbeatConsumer(0x11, TimeSpan.FromSeconds(1));
        master.AddHeartbeatConsumer(0x12, TimeSpan.FromSeconds(2));
        master.ObjectDictionary.ReadUnsigned(0x1016, 0x00).Should().Be(2u);

        await tool.SendNmtCommandAsync(NmtCommand.ResetCommunication, Master);
        await bootups.Second;
        master.ObjectDictionary.ReadUnsigned(0x1016, 0x00).Should().Be(1u, "the count the node was created with");
        master.ObjectDictionary.ReadUnsigned(0x1016, 0x02).Should().Be(0u, "the entry is cleared but still declared");

        master.AddHeartbeatConsumer(0x13, TimeSpan.FromSeconds(3));
        master.AddHeartbeatConsumer(0x14, TimeSpan.FromSeconds(4));
        master.ObjectDictionary.ReadUnsigned(0x1016, 0x00).Should().Be(2u, "the second consumer grows the array into the retained slot");
        master.ObjectDictionary.ReadUnsigned(0x1016, 0x01).Should().Be(HeartbeatEntry(0x13, 3000));
        master.ObjectDictionary.ReadUnsigned(0x1016, 0x02).Should().Be(HeartbeatEntry(0x14, 4000));
        (await UploadUnsignedAsync(tool, Master, 0x1016, 0x02)).Should().Be(HeartbeatEntry(0x14, 4000));
    }

    // FR-CO-019 (#133 review) — a store after "load" is the newer instruction: the next reset
    // restores what was stored last, not the defaults the earlier "load" asked for.
    [Fact]
    public async Task A_Store_After_A_Load_Is_What_The_Next_Reset_Restores()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: Master);
        var bootups = new BootupWatch(master, Slave);
        using var slave = CanOpen.OpenNode(busB, nodeId: Slave);

        slave.RestoreDefaultParameters();
        slave.ObjectDictionary.AddU16(0x2000, 0x00, 0xBEEF);
        slave.ConfigureTpdo(1, new PdoMapping().Add(0x2000, 0x00, 16));
        slave.StoreParameters();
        await bootups.First;

        await master.SendNmtCommandAsync(NmtCommand.ResetCommunication, Slave);
        await bootups.Second;

        (await UploadUnsignedAsync(master, Slave, 0x1800, 0x01)).Should().Be(CanOpenCobId.TpdoDefault(Slave, 1),
            "the store came after the load, so it is the store the reset restores");
        (await UploadUnsignedAsync(master, Slave, 0x1A00, 0x00)).Should().Be(1u);
    }

    // FR-CO-019 (#133 review) — "save" written directly to 1010h:01 stores the values as they are
    // at that write, under the dictionary's write gate: a parameter changed right after the save
    // is not what the next reset restores, however busy the actor loop is. The test holds the
    // loop while it saves and then changes 1017h; the reset after the release restores the saved value.
    [Fact]
    public async Task A_Save_Written_Directly_Stores_The_Values_At_That_Write_Not_Later_Ones()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: Master);
        var bootups = new BootupWatch(master, Slave);
        using var slave = (CanOpenNode)CanOpen.OpenNode(busB, nodeId: Slave);
        var od = slave.ObjectDictionary;
        od.WriteUnsigned(0x1017, 0x00, 100);
        await bootups.First;

        using var release = new ManualResetEventSlim(false);
        var held = slave.PostToActorAsync(() => release.Wait(ShortTimeout));
        od.WriteRaw(0x1010, 0x01, new byte[] { 0x73, 0x61, 0x76, 0x65 }); // "save"
        od.WriteUnsigned(0x1017, 0x00, 300);                                // after the save, before the loop ran anything
        release.Set();
        await held.WithTimeoutAsync(ShortTimeout);

        await master.SendNmtCommandAsync(NmtCommand.ResetCommunication, Slave);
        await bootups.Second;
        (await UploadUnsignedAsync(master, Slave, 0x1017, 0x00)).Should().Be(100u, "the save stored 100; the 300 came after it");
    }

    // FR-CO-019 (#133 review) — a "save" cannot tear an NMT reset's restore: the restore writes
    // every restorable object as one transaction under the write gate, and the save's snapshot
    // is taken under the same gate, so it stores all restored values or all live ones, never a
    // mix. The test starts the save from inside the restore — the dictionary's write event, on
    // the actor loop, at the first restored object — and reads what the following reset restores.
    [Fact]
    public async Task A_Save_During_An_Nmt_Reset_Stores_All_Restored_Values_Not_A_Mix()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, nodeId: Master);
        var bootups = new BootupWatch(master, Slave);
        using var slave = CanOpen.OpenNode(busB, nodeId: Slave);
        var od = slave.ObjectDictionary;
        od.WriteUnsigned(0x1006, 0x00, 5000); // restored before 1017h: the restore walks the dictionary in index order
        od.WriteUnsigned(0x1017, 0x00, 300);
        await bootups.First;

        Task? save = null;
        od.EntryWritten += (index, _) =>
        {
            if (index != 0x1006 || save is not null) return;
            // The restore has written 1006h and has 1017h still to write. A save now, on another
            // thread, must wait for the whole restore — or it stores 1006h restored, 1017h live.
            save = Task.Run(() => od.WriteRaw(0x1010, 0x01, new byte[] { 0x73, 0x61, 0x76, 0x65 }));
            Thread.Sleep(300); // long enough for a save that is not held back to complete
        };
        await master.SendNmtCommandAsync(NmtCommand.ResetCommunication, Slave);
        await bootups.Second;
        save.Should().NotBeNull("the restore wrote 1006h");
        await save!.WithTimeoutAsync(ShortTimeout);
        (await UploadUnsignedAsync(master, Slave, 0x1017, 0x00)).Should().Be(0u, "the first reset restored the default");

        var second = new BootupWatch(master, Slave);
        await master.SendNmtCommandAsync(NmtCommand.ResetCommunication, Slave);
        await second.First;
        (await UploadUnsignedAsync(master, Slave, 0x1006, 0x00)).Should().Be(0u);
        (await UploadUnsignedAsync(master, Slave, 0x1017, 0x00)).Should().Be(0u,
            "the save stored the restored values, not the live 300 of an object the restore had not reached");
    }

    // FR-CO-014 (#133 review) — SendSyncAsync transmits on the CAN-ID the dictionary holds when
    // it is called. The apply that updates the runtime's copy of 1005h is posted to the actor, so
    // a caller that has just written 1005h could otherwise race it; reading the dictionary
    // directly closes that by construction — this test pins the behaviour, it cannot time the race.
    [Fact]
    public async Task SendSyncAsync_Transmits_On_The_CanId_Just_Written_To_1005h()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var observer = Open(session, 1);
        using var producer = CanOpen.OpenNode(busA, nodeId: Slave);

        var onNewId = NewTcs<bool>();
        int onOldId = 0;
        observer.FrameObserved += (_, e) =>
        {
            var frame = e.CanFrame;
            if (frame.IsExtendedFrame || frame.IsRemoteFrame || frame.Data.Length != 0) return;
            if ((uint)frame.ID == 0x0F0) onNewId.TrySetResult(true);
            if ((uint)frame.ID == 0x080) Interlocked.Increment(ref onOldId);
        };

        producer.ObjectDictionary.WriteUnsigned(0x1005, 0x00, 0x0F0);
        await producer.SendSyncAsync();

        await onNewId.Task.WithTimeoutAsync(ShortTimeout);
        onOldId.Should().Be(0, "the one SYNC sent went to the CAN-ID the dictionary already held");
    }
}
