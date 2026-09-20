using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Emcy;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Pdo;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

/// <summary>
/// The PDO engine of <c>CanKit.Pro.CANopen</c> (CiA 301 §7.2.2 and §7.5.2.35–§7.5.2.38): the TPDO
/// transmission types (FR-CO-015), inhibit time and event timer (FR-CO-016), synchronous RPDOs
/// (FR-CO-017), the PDO COB-ID rules (FR-CO-018), mapping validation (FR-CO-020) and RPDO length
/// handling (FR-CO-024). Every PDO is derived from its <c>1400h</c>/<c>1600h</c>/<c>1800h</c>/
/// <c>1A00h</c> records, so the tests configure through <c>ConfigureTpdo</c>/<c>ConfigureRpdo</c>,
/// through the object dictionary and over SDO alike, and observe the wire from a raw channel of
/// the virtual bus.
/// </summary>
/// <remarks>
/// <para>
/// No negative is proved with a delay. "No frame was sent" is shown with an <em>ordering
/// witness</em>: a frame the node must emit after the point in question, handled on the same
/// actor loop and leaving through the same send path, so that once the witness has arrived,
/// anything wrongly emitted before it had already been handed to the same FIFO. Where the
/// witness must follow a frame the node <em>received</em>, it is sent on the same channel after
/// that frame (a SYNC after an RTR); where it must follow an actor call, it is a second awaited
/// call. Expected frames are always awaited as positives before an exact count is asserted, so a
/// slow runner delays a test rather than failing it.
/// </para>
/// <para>
/// Inhibit time and event timer run on a <see cref="ManualTimeSource"/> the test moves by hand:
/// the property under test is the interval the node schedules on the clock it measures against,
/// not how long a thread pool took to notice.
/// </para>
/// </remarks>
public class CanOpenPdoEngineTests : IClassFixture<VirtualAdapterFixture>
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);

    private const byte Device = 0x11;
    private const byte Master = 0x01;
    private const byte Observer = 0x02;

    // The pre-defined connection set of node 0x11 (CanOpenCobId).
    private const uint Tpdo1 = 0x180 + Device;
    private const uint Tpdo2 = 0x280 + Device;
    private const uint Rpdo1 = 0x200 + Device;

    private static string NewSession() => $"canopen-pdo-{Guid.NewGuid():N}";

    private static ICanBus Open(string session, int channel) => CanBus.Open(
        $"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(VirtualAdapterFixture.Bitrate));

    // A node whose PDO records a master may write over SDO (CanOpenDynamicMappingTests).
    private static readonly CanOpenNodeOptions MasterConfigurable =
        new() { WritableCommunicationParameters = true };

    // -----------------------------------------------------------------------------------------
    // Helpers.
    // -----------------------------------------------------------------------------------------

    /// <summary>The node under test on a clock the test drives (the internal constructor is the
    /// seam, #113); it owns the service it wraps, exactly as CanOpen.OpenNode builds it.</summary>
    private static ICanOpenNode OpenClocked(ICanBus bus, byte nodeId, ManualTimeSource clock,
        CanOpenNodeOptions? options = null)
        => new CanOpenNode(new CanBusService(bus), nodeId, options ?? new CanOpenNodeOptions(),
            ownsService: true, clock);

    /// <summary>
    /// Two actor round-trips (the reasoning is VirtualClock's): the loop runs
    /// <c>wait → drain mailbox → fire due timers</c>, so the first PostAsync completes during a
    /// drain and may return while that iteration is still firing timers; the second is drained in
    /// the next iteration, which the loop reaches only after those callbacks returned. Reading
    /// <see cref="ICanOpenNode.State"/> is one PostAsync round-trip.
    /// </summary>
    private static void Settle(ICanOpenNode node)
    {
        _ = node.State;
        _ = node.State;
    }

    /// <summary>Settles first — a timer is armed by the callback that decided to arm it, and moving
    /// the clock before that callback has run arms it from the new reading — then advances and
    /// settles again, so that on return every timer the advance made due has fired.</summary>
    private static void Advance(ManualTimeSource clock, ICanOpenNode node, TimeSpan by)
    {
        Settle(node);
        clock.Advance(by);
        Settle(node);
    }

    private static async Task WaitForStateAsync(ICanOpenNode node, NmtState state)
    {
        var deadline = DateTime.UtcNow + ShortTimeout;
        while (node.State != state)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Node 0x{node.NodeId:X2} did not reach {state}; it is in {node.State}.");
            await Task.Delay(5);
        }
    }

    private static async Task StartAsync(Wire wire, ICanOpenNode node)
    {
        wire.SendNmt(NmtCommand.Start, node.NodeId);
        await WaitForStateAsync(node, NmtState.Operational);
    }

    private static byte[] U32Bytes(uint value) => new[]
    {
        (byte)(value & 0xFF), (byte)((value >> 8) & 0xFF), (byte)((value >> 16) & 0xFF), (byte)((value >> 24) & 0xFF),
    };

    private static byte[] MappingEntryBytes(ushort index, byte subindex, byte bitLength)
        => U32Bytes(((uint)index << 16) | ((uint)subindex << 8) | bitLength);

    private static Task<SdoAbortException> DownloadShouldAbortAsync(ICanOpenNode client, ushort index, byte subindex,
        byte[] value)
        => Assert.ThrowsAsync<SdoAbortException>(() =>
            client.SdoDownloadAsync(Device, index, subindex, value).WithTimeoutAsync(ShortTimeout));

    private static Task DownloadAsync(ICanOpenNode client, ushort index, byte subindex, byte[] value)
        => client.SdoDownloadAsync(Device, index, subindex, value).WithTimeoutAsync(ShortTimeout);

    private static Task<byte[]> UploadAsync(ICanOpenNode client, ushort index, byte subindex)
        => client.SdoUploadAsync(Device, index, subindex).WithTimeoutAsync(ShortTimeout);

    /// <summary>
    /// A raw channel on the virtual bus: records every data frame it observes, per COB-ID, and
    /// transmits what a master, a SYNC producer or a PDO consumer would (NMT, SYNC, RTR, PDO).
    /// Remote frames are not recorded, so a test's own RTR on a TPDO's COB-ID is never counted as
    /// the answer to it.
    /// </summary>
    private sealed class Wire : IDisposable
    {
        private readonly ICanBus _bus;
        private readonly object _gate = new();
        private readonly Dictionary<uint, List<byte[]>> _frames = new();

        public Wire(string session, int channel)
        {
            _bus = Open(session, channel);
            _bus.FrameObserved += (_, e) =>
            {
                var frame = e.CanFrame;
                if (frame.IsExtendedFrame || frame.IsRemoteFrame) return;
                var data = frame.Data.ToArray();
                lock (_gate)
                {
                    if (!_frames.TryGetValue((uint)frame.ID, out var list))
                        _frames[(uint)frame.ID] = list = new List<byte[]>();
                    list.Add(data);
                }
            };
        }

        public int Count(uint cobId)
        {
            lock (_gate) return _frames.TryGetValue(cobId, out var list) ? list.Count : 0;
        }

        public byte[][] Payloads(uint cobId)
        {
            lock (_gate) return _frames.TryGetValue(cobId, out var list) ? list.ToArray() : Array.Empty<byte[]>();
        }

        /// <summary>Waits until at least <paramref name="count"/> frames were seen on
        /// <paramref name="cobId"/> — a wait for an effect, never an assertion about time.</summary>
        public async Task WaitForCountAsync(uint cobId, int count)
        {
            var deadline = DateTime.UtcNow + ShortTimeout;
            while (Count(cobId) < count)
            {
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException($"Expected {count} frame(s) on 0x{cobId:X3}, saw {Count(cobId)}.");
                await Task.Delay(5);
            }
        }

        public void Transmit(uint cobId, byte[] data)
            => _bus.Transmit(CanFrame.Classic(unchecked((int)cobId), data, isExtendedFrame: false));

        public void SendSync() => Transmit(CanOpenCobId.Sync, Array.Empty<byte>());

        public void SendRtr(uint cobId)
            => _bus.Transmit(CanFrame.Classic(unchecked((int)cobId), ReadOnlyMemory<byte>.Empty,
                isExtendedFrame: false, isRemoteFrame: true));

        public void SendNmt(NmtCommand command, byte nodeId)
            => Transmit(CanOpenCobId.NmtCommand, new[] { (byte)command, nodeId });

        public void Dispose() => _bus.Dispose();
    }

    // =========================================================================================
    // FR-CO-015 — TPDO transmission types (CiA 301 §7.5.2.37 Table 72, §7.2.2.2 / §7.2.2.3).
    //
    // TPDO2 in type 01h ("cyclic every SYNC") is the per-SYNC ordering witness of the synchronous
    // tests: HandleSync walks the TPDOs in index order, so once TPDO2's k-th frame has arrived,
    // whatever TPDO1 did on SYNC k was handed to the send path before it.
    // =========================================================================================

    // FR-CO-015 (a) — type 00h, synchronous acyclic: §7.2.2.2 "transmitted after occurrence of the
    // SYNC but acyclic (not periodically), only if an event occurred before the SYNC".
    [Fact]
    public async Task Tpdo_SynchronousAcyclic_Transmits_Once_On_The_Sync_After_An_Event()
    {
        var session = NewSession();
        using var busB = Open(session, 1);
        using var wire = new Wire(session, 2);
        using var device = CanOpen.OpenNode(busB, Device);
        var od = device.ObjectDictionary;
        od.AddU16(0x2000, 0x00, 0);
        od.AddU8(0x2001, 0x00, 0);

        device.ConfigureTpdo(1, new PdoMapping().Add(0x2000, 0x00, 16), TpdoTransmission.SynchronousAcyclic);
        device.ConfigureTpdo(2, new PdoMapping().Add(0x2001, 0x00, 8), TpdoTransmission.Synchronous);
        od.ReadUnsigned(0x1800, 0x02).Should().Be((uint)CanOpenTransmissionType.SynchronousAcyclic);
        od.ReadUnsigned(0x1801, 0x02).Should().Be((uint)CanOpenTransmissionType.SynchronousEverySync);

        await StartAsync(wire, device);

        // A SYNC with no preceding event.
        wire.SendSync();
        await wire.WaitForCountAsync(Tpdo2, 1);
        wire.Count(Tpdo1).Should().Be(0, "type 00h transmits only if an event occurred before the SYNC (Table 72)");

        // TriggerTpdoAsync is the internal event: exactly one frame on the next SYNC ...
        await device.TriggerTpdoAsync(1);
        wire.SendSync();
        await wire.WaitForCountAsync(Tpdo1, 1);
        await wire.WaitForCountAsync(Tpdo2, 2);

        // ... and none on the SYNC after it.
        wire.SendSync();
        await wire.WaitForCountAsync(Tpdo2, 3);
        wire.Count(Tpdo1).Should().Be(1, "the event is consumed by the SYNC that transmitted it");

        // An application write to a mapped object is the same internal event (§7.2.2.3, "event-
        // and timer-driven"); the change-of-state evaluation is posted before the SYNC is even on
        // the wire.
        od.WriteUnsigned(0x2000, 0x00, 0x1234);
        wire.SendSync();
        await wire.WaitForCountAsync(Tpdo1, 2);
        await wire.WaitForCountAsync(Tpdo2, 4);
        wire.Payloads(Tpdo1)[1].Should().Equal(0x34, 0x12);

        wire.SendSync();
        await wire.WaitForCountAsync(Tpdo2, 5);
        wire.Count(Tpdo1).Should().Be(2, "a change of state is one event, consumed by one SYNC");
    }

    // FR-CO-015 (b) — types 01h..F0h, cyclic every n-th SYNC: §7.2.2.2 "a transmission type of n
    // means that the message shall be transmitted with every n-th SYNC object". The factor has no
    // TpdoTransmission member, so it is written to 1800h:02 through the object dictionary — first
    // by the §7.5.2.38 procedure (destroy the PDO, change, re-create), then directly while the
    // PDO is valid, which §7.5.2.37 forbids for the inhibit time (sub-index 03h) but not for the
    // transmission type.
    [Fact]
    public async Task Tpdo_Cyclic_Transmits_On_Every_Nth_Sync()
    {
        var session = NewSession();
        using var busB = Open(session, 1);
        using var wire = new Wire(session, 2);
        using var device = CanOpen.OpenNode(busB, Device);
        var od = device.ObjectDictionary;
        od.AddU16(0x2000, 0x00, 0xBEEF);
        od.AddU8(0x2001, 0x00, 0);

        device.ConfigureTpdo(1, new PdoMapping().Add(0x2000, 0x00, 16), TpdoTransmission.Synchronous);
        device.ConfigureTpdo(2, new PdoMapping().Add(0x2001, 0x00, 8), TpdoTransmission.Synchronous);

        // §7.5.2.38 steps 1 and 5 around the change: bit 31 set, the new type, bit 31 cleared.
        uint validWord = od.ReadUnsigned(0x1800, 0x01);
        (validWord & CanOpenCobId.InvalidBit).Should().Be(0u);
        od.WriteUnsigned(0x1800, 0x01, validWord | CanOpenCobId.InvalidBit);
        od.WriteUnsigned(0x1800, 0x02, CanOpenTransmissionType.SynchronousEveryNthSync(3));
        od.WriteUnsigned(0x1800, 0x01, validWord);
        Settle(device); // the rebuilds those writes posted have run
        od.ReadUnsigned(0x1800, 0x02).Should().Be(3u);

        await StartAsync(wire, device);

        for (int k = 1; k <= 9; k++)
        {
            wire.SendSync();
            await wire.WaitForCountAsync(Tpdo1, k / 3);
            await wire.WaitForCountAsync(Tpdo2, k);
            wire.Count(Tpdo1).Should().Be(k / 3, "with type 03h the {0}. SYNC yields a frame only when it is a multiple of three", k);
        }
        foreach (var payload in wire.Payloads(Tpdo1)) payload.Should().Equal(0xEF, 0xBE);

        // The other path: the transmission type may be changed while the PDO is valid. The
        // rebuild restarts the SYNC counter, so the next transmissions are on the 2nd and 4th SYNC.
        od.WriteUnsigned(0x1800, 0x02, CanOpenTransmissionType.SynchronousEveryNthSync(2));
        Settle(device);
        od.ReadUnsigned(0x1800, 0x02).Should().Be(2u);
        for (int k = 1; k <= 4; k++)
        {
            wire.SendSync();
            await wire.WaitForCountAsync(Tpdo1, 3 + k / 2);
            await wire.WaitForCountAsync(Tpdo2, 9 + k);
            wire.Count(Tpdo1).Should().Be(3 + k / 2, "with type 02h the {0}. SYNC after the change yields a frame only when it is even", k);
        }
    }

    // FR-CO-015 (c) — type FCh, RTR-only synchronous: Table 72 "the CANopen device will start
    // sampling with the reception of every SYNC and then will buffer the PDO"; the buffered sample
    // answers the RTR (§7.2.2.5.2, PDO read). Before the first SYNC there is nothing sampled.
    [Fact]
    public async Task Tpdo_RtrOnlySynchronous_Answers_An_Rtr_With_The_Sample_Taken_At_The_Sync()
    {
        var session = NewSession();
        using var busB = Open(session, 1);
        using var wire = new Wire(session, 2);
        using var device = CanOpen.OpenNode(busB, Device);
        var od = device.ObjectDictionary;
        od.AddU16(0x2000, 0x00, 0);
        od.AddU8(0x2001, 0x00, 0);

        device.ConfigureTpdo(1, new PdoMapping().Add(0x2000, 0x00, 16), TpdoTransmission.RtrOnlySynchronous);
        device.ConfigureTpdo(2, new PdoMapping().Add(0x2001, 0x00, 8), TpdoTransmission.Synchronous);
        od.ReadUnsigned(0x1800, 0x02).Should().Be((uint)CanOpenTransmissionType.RtrOnlySynchronous);
        (od.ReadUnsigned(0x1800, 0x01) & CanOpenCobId.NoRtrBit).Should().Be(0u, "ConfigureTpdo's default COB-ID word allows RTR (Table 70 bit 30 = 0b)");

        await StartAsync(wire, device);

        // An RTR before any SYNC: no sample exists, so no answer. The SYNC after it is the
        // witness — same channel, same RX path, same actor loop.
        od.WriteUnsigned(0x2000, 0x00, 0x1111);
        wire.SendRtr(Tpdo1);
        wire.SendSync();
        await wire.WaitForCountAsync(Tpdo2, 1);
        wire.Count(Tpdo1).Should().Be(0, "before the first SYNC nothing has been sampled that could answer an RTR");

        // The OD moves on after the SYNC; the answer is the sample taken at the SYNC.
        od.WriteUnsigned(0x2000, 0x00, 0x2222);
        wire.SendRtr(Tpdo1);
        await wire.WaitForCountAsync(Tpdo1, 1);
        wire.Payloads(Tpdo1)[0].Should().Equal(new byte[] { 0x11, 0x11 }, "the buffered PDO is the one sampled at the SYNC, not the current value");

        // The next SYNC resamples.
        wire.SendSync();
        await wire.WaitForCountAsync(Tpdo2, 2);
        wire.SendRtr(Tpdo1);
        await wire.WaitForCountAsync(Tpdo1, 2);
        wire.Payloads(Tpdo1)[1].Should().Equal(0x22, 0x22);
        wire.Count(Tpdo1).Should().Be(2, "an RTR-only PDO is never transmitted unrequested");
    }

    // FR-CO-015 (d) — type FDh, RTR-only event-driven: Table 72 "the CANopen device will start
    // sampling with the reception of the RTR and will transmit the PDO immediately"; the SYNC
    // plays no part.
    [Fact]
    public async Task Tpdo_RtrOnlyEventDriven_Answers_An_Rtr_With_The_Current_Value_And_Ignores_Sync()
    {
        var session = NewSession();
        using var busB = Open(session, 1);
        using var wire = new Wire(session, 2);
        using var device = CanOpen.OpenNode(busB, Device);
        var od = device.ObjectDictionary;
        od.AddU16(0x2000, 0x00, 0);
        od.AddU8(0x2001, 0x00, 0);

        device.ConfigureTpdo(1, new PdoMapping().Add(0x2000, 0x00, 16), TpdoTransmission.RtrOnlyEventDriven);
        device.ConfigureTpdo(2, new PdoMapping().Add(0x2001, 0x00, 8), TpdoTransmission.Synchronous);
        od.ReadUnsigned(0x1800, 0x02).Should().Be((uint)CanOpenTransmissionType.RtrOnlyEventDriven);

        await StartAsync(wire, device);

        od.WriteUnsigned(0x2000, 0x00, 0x4321);
        wire.SendSync();
        await wire.WaitForCountAsync(Tpdo2, 1);
        wire.Count(Tpdo1).Should().Be(0, "a SYNC alone produces nothing for an RTR-only event-driven PDO");

        wire.SendRtr(Tpdo1);
        await wire.WaitForCountAsync(Tpdo1, 1);
        wire.Payloads(Tpdo1)[0].Should().Equal(new byte[] { 0x21, 0x43 }, "the RTR is answered with the value sampled on its reception");
    }

    // FR-CO-015 (e) — Table 70 bit 30: "no RTR allowed on this PDO". The RTR is ignored; the PDO
    // still transmits on its own trigger.
    [Fact]
    public async Task Tpdo_With_NoRtrBit_Does_Not_Answer_An_Rtr()
    {
        var session = NewSession();
        using var busB = Open(session, 1);
        using var wire = new Wire(session, 2);
        using var device = CanOpen.OpenNode(busB, Device);
        var od = device.ObjectDictionary;
        od.AddU16(0x2000, 0x00, 0xABCD);
        od.AddU8(0x2001, 0x00, 0);

        device.ConfigureTpdo(1, new PdoMapping().Add(0x2000, 0x00, 16), TpdoTransmission.EventDriven,
            cobId: CanOpenCobId.NoRtrBit | Tpdo1);
        device.ConfigureTpdo(2, new PdoMapping().Add(0x2001, 0x00, 8), TpdoTransmission.Synchronous);
        (od.ReadUnsigned(0x1800, 0x01) & CanOpenCobId.NoRtrBit).Should().Be(CanOpenCobId.NoRtrBit);
        (od.ReadUnsigned(0x1800, 0x01) & CanOpenCobId.InvalidBit).Should().Be(0u, "bit 30 does not disable the PDO");

        await StartAsync(wire, device);

        wire.SendRtr(Tpdo1);
        wire.SendSync();
        await wire.WaitForCountAsync(Tpdo2, 1);
        wire.Count(Tpdo1).Should().Be(0, "bit 30 of 1800h:01 forbids RTR on this PDO (Table 70)");

        await device.TriggerTpdoAsync(1);
        await wire.WaitForCountAsync(Tpdo1, 1);
        wire.Payloads(Tpdo1)[0].Should().Equal(0xCD, 0xAB);
    }

    // FR-CO-015 (f) — Table 72 (TPDO: F1h..FBh reserved) and Table 68 (RPDO: F1h..FDh reserved):
    // "An attempt to change the value of the transmission type to any not supported value shall be
    // responded with the SDO abort transfer service (abort code: 0609 0030h)" — over the bus and
    // on the local write path, which reports the same code.
    [Fact]
    public async Task Pdo_Reserved_Transmission_Type_Is_Rejected_With_0609_0030h()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, Master);
        using var device = CanOpen.OpenNode(busB, Device, MasterConfigurable);
        var od = device.ObjectDictionary;

        (await DownloadShouldAbortAsync(master, 0x1800, 0x02, new byte[] { 0xF5 }))
            .AbortCode.Should().Be((uint)SdoAbortCode.ValueRangeExceeded, "F5h is reserved for a TPDO (Table 72)");
        (await DownloadShouldAbortAsync(master, 0x1400, 0x02, new byte[] { 0xF5 }))
            .AbortCode.Should().Be((uint)SdoAbortCode.ValueRangeExceeded, "F5h is reserved for an RPDO (Table 68)");
        (await DownloadShouldAbortAsync(master, 0x1400, 0x02, new byte[] { 0xFC }))
            .AbortCode.Should().Be((uint)SdoAbortCode.ValueRangeExceeded, "RTR-only exists for TPDOs only; FCh is reserved for an RPDO (Table 68)");

        Action tpdo = () => od.WriteUnsigned(0x1800, 0x02, 0xF5);
        tpdo.Should().Throw<ArgumentException>().Which.Message.Should().Contain("06090030");
        Action rpdo = () => od.WriteUnsigned(0x1400, 0x02, 0xFC);
        rpdo.Should().Throw<ArgumentException>().Which.Message.Should().Contain("06090030");

        od.ReadUnsigned(0x1800, 0x02).Should().Be((uint)CanOpenTransmissionType.EventDrivenManufacturer, "a rejected value is not stored");
        od.ReadUnsigned(0x1400, 0x02).Should().Be((uint)CanOpenTransmissionType.EventDrivenManufacturer);

        // The neighbours of the reserved ranges are supported values.
        od.WriteUnsigned(0x1800, 0x02, CanOpenTransmissionType.SynchronousCyclicMax);
        od.WriteUnsigned(0x1800, 0x02, CanOpenTransmissionType.RtrOnlySynchronous);
        od.WriteUnsigned(0x1400, 0x02, CanOpenTransmissionType.SynchronousCyclicMax);
        od.WriteUnsigned(0x1400, 0x02, CanOpenTransmissionType.EventDrivenManufacturer);
    }

    // FR-CO-015 (g) — types FEh/FFh, event-driven: transmits on the internal event — TriggerTpdoAsync
    // and an application write to a mapped object (§7.2.2.3) — and only in NMT state Operational,
    // where alone the PDO exists (§7.3.2.2.5 Table 37).
    [Fact]
    public async Task Tpdo_EventDriven_Transmits_On_Trigger_And_Application_Write_But_Not_In_PreOperational()
    {
        var session = NewSession();
        using var busB = Open(session, 1);
        using var wire = new Wire(session, 2);
        using var device = CanOpen.OpenNode(busB, Device);
        var od = device.ObjectDictionary;
        od.AddU16(0x2000, 0x00, 0);

        device.ConfigureTpdo(1, new PdoMapping().Add(0x2000, 0x00, 16), TpdoTransmission.EventDriven);
        od.ReadUnsigned(0x1800, 0x02).Should().Be((uint)CanOpenTransmissionType.EventDrivenManufacturer);
        device.State.Should().Be(NmtState.PreOperational);

        // Both events in Pre-Operational; the trigger is an awaited round-trip and the write's
        // change-of-state evaluation is posted synchronously, so both are handled before the
        // NMT start frame that follows.
        await device.TriggerTpdoAsync(1);
        od.WriteUnsigned(0x2000, 0x00, 5);

        await StartAsync(wire, device);
        await device.TriggerTpdoAsync(1);
        await wire.WaitForCountAsync(Tpdo1, 1);
        wire.Count(Tpdo1).Should().Be(1, "PDOs exist only in Operational (Table 37): neither Pre-Operational event produced a frame");
        wire.Payloads(Tpdo1)[0].Should().Equal(0x05, 0x00);

        od.WriteUnsigned(0x2000, 0x00, 6);
        await wire.WaitForCountAsync(Tpdo1, 2);
        wire.Payloads(Tpdo1)[1].Should().Equal(new byte[] { 0x06, 0x00 }, "an application write to a mapped object is an internal event");
    }

    // =========================================================================================
    // FR-CO-016 — inhibit time (1800h:03, multiples of 100 µs) and event timer (1800h:05,
    // multiples of 1 ms), CiA 301 §7.5.2.37. Measured on a clock the test moves.
    // =========================================================================================

    // FR-CO-016 — "the minimum interval for PDO transmission if the transmission type is set to
    // FEh and FFh". Events inside the interval are coalesced into one transmission when it elapses,
    // carrying the value current at that moment.
    [Fact]
    public async Task Tpdo_InhibitTime_Coalesces_Events_Into_One_Transmission_When_It_Elapses()
    {
        var clock = new ManualTimeSource();
        var session = NewSession();
        using var busB = Open(session, 1);
        using var wire = new Wire(session, 2);
        // Events come from TriggerTpdoAsync alone, each an awaited actor round-trip, so the number
        // of events is exact; change-of-state would add a second, unawaited event per OD write.
        using var device = OpenClocked(busB, Device, clock, new CanOpenNodeOptions { EnableChangeOfStateTpdo = false });
        var od = device.ObjectDictionary;
        od.AddU16(0x2000, 0x00, 0);
        od.AddU8(0x2001, 0x00, 0);

        device.ConfigureTpdo(1, new PdoMapping().Add(0x2000, 0x00, 16), TpdoTransmission.EventDriven,
            inhibitTime: TimeSpan.FromMilliseconds(50));
        device.ConfigureTpdo(2, new PdoMapping().Add(0x2001, 0x00, 8), TpdoTransmission.EventDriven);
        od.ReadUnsigned(0x1800, 0x03).Should().Be(500u, "1800h:03 is in multiples of 100 µs (§7.5.2.37)");

        await StartAsync(wire, device);

        od.WriteUnsigned(0x2000, 0x00, 1);
        await device.TriggerTpdoAsync(1);
        await wire.WaitForCountAsync(Tpdo1, 1);

        // Two further events while the clock stands still: inside the inhibit time. TPDO2's
        // trigger is the witness — a later awaited call through the same send path.
        od.WriteUnsigned(0x2000, 0x00, 2);
        await device.TriggerTpdoAsync(1);
        od.WriteUnsigned(0x2000, 0x00, 3);
        await device.TriggerTpdoAsync(1);
        await device.TriggerTpdoAsync(2);
        await wire.WaitForCountAsync(Tpdo2, 1);
        wire.Count(Tpdo1).Should().Be(1, "no event inside the inhibit time transmits");

        // One tick short of the interval: still nothing.
        Advance(clock, device, TimeSpan.FromMilliseconds(49));
        await device.TriggerTpdoAsync(2);
        await wire.WaitForCountAsync(Tpdo2, 2);
        wire.Count(Tpdo1).Should().Be(1, "49 ms is inside a 50 ms inhibit time");

        // The interval elapses: exactly one transmission, with the latest value.
        Advance(clock, device, TimeSpan.FromMilliseconds(1));
        await wire.WaitForCountAsync(Tpdo1, 2);
        var payloads = wire.Payloads(Tpdo1);
        payloads.Should().HaveCount(2, "the two inhibited events coalesce into the one transmission at the end of the interval");
        payloads[0].Should().Equal(0x01, 0x00);
        payloads[1].Should().Equal(new byte[] { 0x03, 0x00 }, "the coalesced transmission carries the value current when it goes out");
    }

    // FR-CO-016 — "the maximum interval for PDO transmission if the transmission type is set to
    // FEh and FFh": the timer transmits when the interval passes without an event, and every
    // transmission — an event's included — restarts it.
    [Fact]
    public async Task Tpdo_EventTimer_Is_The_Maximum_Interval_And_Restarts_On_Every_Transmission()
    {
        var clock = new ManualTimeSource();
        var session = NewSession();
        using var busB = Open(session, 1);
        using var wire = new Wire(session, 2);
        using var device = OpenClocked(busB, Device, clock);
        var od = device.ObjectDictionary;
        od.AddU16(0x2000, 0x00, 0);
        od.AddU8(0x2001, 0x00, 0);

        device.ConfigureTpdo(1, new PdoMapping().Add(0x2000, 0x00, 16), TpdoTransmission.EventTimer,
            eventTimerInterval: TimeSpan.FromMilliseconds(100));
        device.ConfigureTpdo(2, new PdoMapping().Add(0x2001, 0x00, 8), TpdoTransmission.EventDriven);
        od.ReadUnsigned(0x1800, 0x05).Should().Be(100u, "1800h:05 is in multiples of 1 ms (§7.5.2.37)");
        od.ReadUnsigned(0x1800, 0x02).Should().Be((uint)CanOpenTransmissionType.EventDrivenManufacturer,
            "the event timer is an attribute of an event-driven PDO, not a transmission type of its own");

        await StartAsync(wire, device);

        // t = 99 ms: not yet. t = 100 ms: the interval passed without an event.
        Advance(clock, device, TimeSpan.FromMilliseconds(99));
        await device.TriggerTpdoAsync(2);
        await wire.WaitForCountAsync(Tpdo2, 1);
        wire.Count(Tpdo1).Should().Be(0, "99 ms of a 100 ms event timer have elapsed");
        Advance(clock, device, TimeSpan.FromMilliseconds(1));
        await wire.WaitForCountAsync(Tpdo1, 1);

        // t = 160 ms: an event (application write) transmits and restarts the interval.
        Advance(clock, device, TimeSpan.FromMilliseconds(60));
        od.WriteUnsigned(0x2000, 0x00, 7);
        await wire.WaitForCountAsync(Tpdo1, 2);

        // t = 220 ms: the timer armed at t = 100 would have been due at t = 200; the restart at
        // t = 160 moved it to t = 260.
        Advance(clock, device, TimeSpan.FromMilliseconds(60));
        await device.TriggerTpdoAsync(2);
        await wire.WaitForCountAsync(Tpdo2, 2);
        wire.Count(Tpdo1).Should().Be(2, "the event at t = 160 ms restarted the interval");

        // t = 260 ms: the restarted interval elapses.
        Advance(clock, device, TimeSpan.FromMilliseconds(40));
        await wire.WaitForCountAsync(Tpdo1, 3);
        var payloads = wire.Payloads(Tpdo1);
        payloads.Should().HaveCount(3);
        payloads[0].Should().Equal(0x00, 0x00);
        payloads[1].Should().Equal(0x07, 0x00);
        payloads[2].Should().Equal(new byte[] { 0x07, 0x00 }, "the timer re-transmits the current value");
    }

    // FR-CO-016 — §7.5.2.37 sub-index 03h: "The value shall not be changed while the PDO exists
    // (bit 31 of sub-index 01h is set to 0b)". The event timer carries no such rule.
    [Fact]
    public async Task Tpdo_InhibitTime_Cannot_Change_While_The_Pdo_Is_Valid()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, Master);
        using var device = CanOpen.OpenNode(busB, Device, MasterConfigurable);
        var od = device.ObjectDictionary;
        od.AddU16(0x2000, 0x00, 0);
        device.ConfigureTpdo(1, new PdoMapping().Add(0x2000, 0x00, 16));

        (await DownloadShouldAbortAsync(master, 0x1800, 0x03, new byte[] { 0x0A, 0x00 }))
            .AbortCode.Should().Be((uint)SdoAbortCode.ValueRangeExceeded);
        Action local = () => od.WriteUnsigned(0x1800, 0x03, 10);
        local.Should().Throw<ArgumentException>().Which.Message.Should().Contain("06090030");
        (await UploadAsync(master, 0x1800, 0x03)).Should().Equal(new byte[] { 0x00, 0x00 }, "the rejected value is not stored");

        // The event timer may change on a valid PDO; the inhibit time once the PDO is destroyed.
        await DownloadAsync(master, 0x1800, 0x05, new byte[] { 0xFA, 0x00 });
        (await UploadAsync(master, 0x1800, 0x05)).Should().Equal(new byte[] { 0xFA, 0x00 }, "250 ms as an UNSIGNED16 in multiples of 1 ms");

        uint valid = od.ReadUnsigned(0x1800, 0x01);
        await DownloadAsync(master, 0x1800, 0x01, U32Bytes(valid | CanOpenCobId.InvalidBit));
        await DownloadAsync(master, 0x1800, 0x03, new byte[] { 0x0A, 0x00 });
        (await UploadAsync(master, 0x1800, 0x03)).Should().Equal(new byte[] { 0x0A, 0x00 }, "1 ms as an UNSIGNED16 in multiples of 100 µs");
        await DownloadAsync(master, 0x1800, 0x01, U32Bytes(valid));
        od.ReadUnsigned(0x1800, 0x03).Should().Be(10u);
    }

    // =========================================================================================
    // FR-CO-017 — RPDO transmission type (CiA 301 §7.5.2.35 Table 68).
    // =========================================================================================

    // FR-CO-017 — type 00h: §7.2.2.2 "the data of synchronous RPDOs received after the occurrence
    // of the SYNC object is passed to the application with the occurrence of the following SYNC".
    [Fact]
    public async Task Rpdo_Synchronous_Holds_Received_Data_Until_The_Next_Sync()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, Master);
        using var device = CanOpen.OpenNode(busB, Device);
        var od = device.ObjectDictionary;
        od.AddU16(0x2100, 0x00, 0);

        device.ConfigureRpdo(1, new PdoMapping().Add(0x2100, 0x00, 16), transmission: RpdoTransmission.Synchronous);
        od.ReadUnsigned(0x1400, 0x02).Should().Be((uint)CanOpenTransmissionType.SynchronousAcyclic);

        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        device.RpdoReceived += (_, e) =>
        {
            if (e.CobId == Rpdo1) received.TrySetResult(e.Payload);
        };

        await master.SendNmtCommandAsync(NmtCommand.Start, Device);
        await WaitForStateAsync(device, NmtState.Operational);

        // The PDO and, on the same channel after it, an SDO upload of the mapped object: the
        // device serves the upload on the same actor loop after it handled the PDO, and answers
        // with the value the object had at that moment.
        busA.Transmit(CanFrame.Classic(unchecked((int)Rpdo1), new byte[] { 0x34, 0x12 }, isExtendedFrame: false));
        (await UploadAsync(master, 0x2100, 0x00)).Should().Equal(new byte[] { 0x00, 0x00 },
            "a synchronous RPDO is actuated with the next SYNC, not on reception (Table 68)");
        received.Task.IsCompleted.Should().BeFalse("RpdoReceived reports the actuation, which has not happened yet");

        await master.SendSyncAsync();
        (await received.Task.WithTimeoutAsync(ShortTimeout)).Should().Equal(0x34, 0x12);
        od.ReadUnsigned(0x2100, 0x00).Should().Be(0x1234u);
    }

    // FR-CO-017 — type FEh (the default): Table 68 "the CANopen device will actualize the data
    // immediately". No SYNC is ever sent.
    [Fact]
    public async Task Rpdo_EventDriven_Actuates_Received_Data_Immediately()
    {
        var session = NewSession();
        using var busB = Open(session, 1);
        using var wire = new Wire(session, 2);
        using var device = CanOpen.OpenNode(busB, Device);
        var od = device.ObjectDictionary;
        od.AddU16(0x2100, 0x00, 0);

        device.ConfigureRpdo(1, new PdoMapping().Add(0x2100, 0x00, 16));
        od.ReadUnsigned(0x1400, 0x02).Should().Be((uint)CanOpenTransmissionType.EventDrivenManufacturer);

        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        device.RpdoReceived += (_, e) =>
        {
            if (e.CobId == Rpdo1) received.TrySetResult(e.Payload);
        };
        // A write the RPDO could not land is reported here rather than swallowed; the assertion
        // below names it, so a failure says why the value is missing instead of only that it is.
        var background = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        device.BackgroundExceptionOccurred += (_, ex) => background.Enqueue(ex);

        await StartAsync(wire, device);
        wire.Transmit(Rpdo1, new byte[] { 0x34, 0x12 });
        (await received.Task.WithTimeoutAsync(ShortTimeout)).Should().Equal(0x34, 0x12);
        od.ReadUnsigned(0x2100, 0x00).Should().Be(0x1234u,
            "the received bytes land in the mapped object (background: {0})", string.Join(" | ", background.Select(x => x.ToString())));
        background.Should().BeEmpty();
    }

    // =========================================================================================
    // FR-CO-018 — PDO COB-ID (CiA 301 §7.5.2.35 Table 66 / §7.5.2.37 Table 70, §7.3.5).
    // =========================================================================================

    // FR-CO-018 — bit 31: "PDO does not exist / is not valid". The record is written, the PDO
    // does not transmit; clearing the bit (bits 0..29 unchanged) creates it.
    [Fact]
    public async Task Tpdo_Configured_With_Bit31_Is_Recorded_But_Does_Not_Transmit()
    {
        var session = NewSession();
        using var busB = Open(session, 1);
        using var wire = new Wire(session, 2);
        using var device = CanOpen.OpenNode(busB, Device);
        var od = device.ObjectDictionary;
        od.AddU16(0x2000, 0x00, 0x5AA5);
        od.AddU8(0x2001, 0x00, 0);

        device.ConfigureTpdo(1, new PdoMapping().Add(0x2000, 0x00, 16), cobId: CanOpenCobId.InvalidBit | Tpdo1);
        device.ConfigureTpdo(2, new PdoMapping().Add(0x2001, 0x00, 8));
        od.ReadUnsigned(0x1800, 0x01).Should().Be(CanOpenCobId.InvalidBit | Tpdo1);
        od.ReadUnsigned(0x1A00, 0x00).Should().Be(1u, "the mapping is recorded even though the PDO is not valid");

        await StartAsync(wire, device);
        await device.TriggerTpdoAsync(1);
        await device.TriggerTpdoAsync(2);
        await wire.WaitForCountAsync(Tpdo2, 1);
        wire.Count(Tpdo1).Should().Be(0, "a PDO with bit 31 set does not exist (Table 70)");

        // §7.5.2.38 step 5: create it by clearing bit 31, everything else unchanged.
        od.WriteUnsigned(0x1800, 0x01, Tpdo1);
        Settle(device);
        await device.TriggerTpdoAsync(1);
        await wire.WaitForCountAsync(Tpdo1, 1);
        wire.Payloads(Tpdo1)[0].Should().Equal(0xA5, 0x5A);
    }

    // FR-CO-018 — a COB-ID word the node cannot use: bit 29 (29-bit CAN-ID; this node speaks base
    // frames only, §7.5.2.37 "responded with ... 0609 0030h") and the restricted CAN-IDs of §7.3.5
    // (07Fh in 001h..07Fh, 581h the default SDO tx of node 1, 701h the NMT error control of node
    // 1). The PDO stays destroyed.
    [Theory]
    [InlineData(CanOpenCobId.ExtendedFrameBit | 0x191u, false)]
    [InlineData(0x07Fu, true)]
    [InlineData(0x581u, true)]
    [InlineData(0x701u, true)]
    public void Pdo_CobId_The_Node_Cannot_Use_Is_Rejected(uint cobId, bool restrictedByNorm)
    {
        var session = NewSession();
        using var busB = Open(session, 1);
        using var device = CanOpen.OpenNode(busB, Device);
        var od = device.ObjectDictionary;
        od.AddU16(0x2000, 0x00, 0);
        od.AddU16(0x2100, 0x00, 0);

        Action tpdo = () => device.ConfigureTpdo(1, new PdoMapping().Add(0x2000, 0x00, 16), cobId: cobId);
        Action rpdo = () => device.ConfigureRpdo(1, new PdoMapping().Add(0x2100, 0x00, 16), cobId: cobId);

        var tpdoRejection = tpdo.Should().Throw<ArgumentException>().Which;
        var rpdoRejection = rpdo.Should().Throw<ArgumentException>().Which;
        if (restrictedByNorm)
        {
            tpdoRejection.Message.Should().Contain("06090030", "the rejection names the abort code the same write produces over SDO");
            rpdoRejection.Message.Should().Contain("06090030");
        }

        (od.ReadUnsigned(0x1800, 0x01) & CanOpenCobId.InvalidBit).Should().Be(CanOpenCobId.InvalidBit, "the PDO is left disabled");
        (od.ReadUnsigned(0x1400, 0x01) & CanOpenCobId.InvalidBit).Should().Be(CanOpenCobId.InvalidBit);
    }

    // FR-CO-018 — Table 70: "It is not allowed to change bit from 0 to 29 while the PDO exists and
    // is valid (bit 31 = 0b)". Destroying the PDO first makes the change legal, and the new CAN-ID
    // is the one the PDO then transmits on.
    [Fact]
    public async Task Tpdo_CanId_Changes_Over_Sdo_Only_While_The_Pdo_Is_Invalid_And_Then_Takes_Effect()
    {
        const uint NewId = 0x1A5;
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, Master);
        using var device = CanOpen.OpenNode(busB, Device, MasterConfigurable);
        device.ObjectDictionary.AddU16(0x2000, 0x00, 0xBEEF);
        master.ObjectDictionary.AddU16(0x2100, 0x00, 0);

        device.ConfigureTpdo(1, new PdoMapping().Add(0x2000, 0x00, 16));
        master.ConfigureRpdo(1, new PdoMapping().Add(0x2100, 0x00, 16), cobId: NewId);

        (await DownloadShouldAbortAsync(master, 0x1800, 0x01, U32Bytes(NewId)))
            .AbortCode.Should().Be((uint)SdoAbortCode.ValueRangeExceeded, "the CAN-ID of a valid PDO cannot change (Table 70)");
        (await UploadAsync(master, 0x1800, 0x01)).Should().Equal(U32Bytes(Tpdo1));

        await DownloadAsync(master, 0x1800, 0x01, U32Bytes(CanOpenCobId.InvalidBit | NewId));
        await DownloadAsync(master, 0x1800, 0x01, U32Bytes(NewId));
        (await UploadAsync(master, 0x1800, 0x01)).Should().Equal(U32Bytes(NewId));

        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        master.RpdoReceived += (_, e) =>
        {
            if (e.CobId == NewId) received.TrySetResult(e.Payload);
        };

        await master.SendNmtCommandAsync(NmtCommand.Start, Device);
        await device.SendNmtCommandAsync(NmtCommand.Start, Master);
        await WaitForStateAsync(device, NmtState.Operational);
        await WaitForStateAsync(master, NmtState.Operational);

        await device.TriggerTpdoAsync(1);
        (await received.Task.WithTimeoutAsync(ShortTimeout)).Should().Equal(0xEF, 0xBE);
        master.ObjectDictionary.ReadUnsigned(0x2100, 0x00).Should().Be(0xBEEFu);
    }

    // FR-CO-018 (#41) — an RPDO COB-ID the node's subscription cannot deliver (outside 080h..77Fh,
    // or with bits above the 11-bit CAN-ID) is rejected at configuration instead of being accepted
    // and never received. Every such 11-bit value is also one §7.3.5 restricts.
    [Theory]
    [InlineData(0x001u)]
    [InlineData(0x780u)]
    [InlineData(0x7FFu)]
    [InlineData(0x1000u)]
    public void Rpdo_CobId_The_Node_Cannot_Receive_Is_Rejected_Rather_Than_Silently_Never_Received(uint cobId)
    {
        var session = NewSession();
        using var busB = Open(session, 1);
        using var device = CanOpen.OpenNode(busB, Device);
        device.ObjectDictionary.AddU16(0x2100, 0x00, 0);

        Action configure = () => device.ConfigureRpdo(1, new PdoMapping().Add(0x2100, 0x00, 16), cobId: cobId);

        configure.Should().Throw<ArgumentException>();
        (device.ObjectDictionary.ReadUnsigned(0x1400, 0x01) & CanOpenCobId.InvalidBit).Should().Be(CanOpenCobId.InvalidBit);
    }

    // =========================================================================================
    // FR-CO-020 — mapping validation (CiA 301 §7.5.2.36 / §7.5.2.38, Tables 69 and 73).
    // =========================================================================================

    // FR-CO-020 — step 3 of the re-mapping procedure: "the mapping (index and sub-index) of a
    // non-existing application object, a wrong length for the mapped application object, or a
    // wrong length for the PDO at all" → 0604 0041h; a bit length that is not a byte multiple is
    // a length that does not match the data type → 0607 0010h. The rejected entry is not stored.
    [Theory]
    [InlineData(0x1A00, 0x1017, 0x00, 16, SdoAbortCode.ObjectCannotBeMapped)]   // a communication object is not PDO-mappable
    [InlineData(0x1A00, 0x2000, 0x00, 16, SdoAbortCode.ObjectCannotBeMapped)]   // UNSIGNED8 mapped as 16 bits
    [InlineData(0x1A00, 0x2000, 0x00, 12, SdoAbortCode.DataTypeLengthMismatch)] // not a byte multiple
    [InlineData(0x1A00, 0x0005, 0x00, 16, SdoAbortCode.ObjectCannotBeMapped)]   // dummy UNSIGNED8 (§7.5.2.36) is 8 bits wide
    [InlineData(0x1600, 0x2001, 0x00, 16, SdoAbortCode.ObjectCannotBeMapped)]   // an RPDO cannot write a read-only object
    public async Task Pdo_Mapping_Entry_That_Cannot_Be_Mapped_Is_Rejected(int mapIndex, int index, int subindex,
        int bitLength, SdoAbortCode expected)
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, Master);
        using var device = CanOpen.OpenNode(busB, Device, MasterConfigurable);
        device.ObjectDictionary.AddU8(0x2000, 0x00, 0);
        device.ObjectDictionary.AddU16(0x2001, 0x00, 0, OdAccess.ReadOnly);

        var map = (ushort)mapIndex;
        await DownloadAsync(master, map, 0x00, new byte[] { 0x00 });

        var entry = MappingEntryBytes((ushort)index, (byte)subindex, (byte)bitLength);
        (await DownloadShouldAbortAsync(master, map, 0x01, entry)).AbortCode.Should().Be((uint)expected);
        (await UploadAsync(master, map, 0x01)).Should().Equal(new byte[] { 0x00, 0x00, 0x00, 0x00 }, "a rejected entry is not stored");
    }

    // FR-CO-020 (#40) — entries take effect by their sub-index, not by the order they were written;
    // sub-index 00h uploads as the UNSIGNED8 Table 73 defines; a dummy entry (§7.5.2.36, static
    // data type 0005h = UNSIGNED8) contributes a zero byte without reading the object dictionary.
    [Fact]
    public async Task Tpdo_Mapping_Entries_Take_Effect_By_Sub_Index_Not_By_Write_Order()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var wire = new Wire(session, 2);
        using var master = CanOpen.OpenNode(busA, Master);
        using var device = CanOpen.OpenNode(busB, Device, MasterConfigurable);
        var od = device.ObjectDictionary;
        od.AddU8(0x2000, 0x00, 0xAA);
        od.AddU16(0x2001, 0x00, 0xBEEF, OdAccess.ReadOnly);
        // A probe at the dummy's own index: a dummy entry must not read it.
        od.AddU8(0x0005, 0x00, 0x77);

        await DownloadAsync(master, 0x1A00, 0x00, new byte[] { 0x00 });
        await DownloadAsync(master, 0x1A00, 0x03, MappingEntryBytes(0x0005, 0x00, 8));
        await DownloadAsync(master, 0x1A00, 0x02, MappingEntryBytes(0x2001, 0x00, 16));
        await DownloadAsync(master, 0x1A00, 0x01, MappingEntryBytes(0x2000, 0x00, 8));
        await DownloadAsync(master, 0x1A00, 0x00, new byte[] { 0x03 });
        await DownloadAsync(master, 0x1800, 0x01, U32Bytes(Tpdo1));

        (await UploadAsync(master, 0x1A00, 0x00)).Should().Equal(0x03);
        (await UploadAsync(master, 0x1A00, 0x01)).Should().Equal(MappingEntryBytes(0x2000, 0x00, 8));
        (await UploadAsync(master, 0x1A00, 0x02)).Should().Equal(MappingEntryBytes(0x2001, 0x00, 16));
        (await UploadAsync(master, 0x1A00, 0x03)).Should().Equal(MappingEntryBytes(0x0005, 0x00, 8));

        await StartAsync(wire, device);
        await device.TriggerTpdoAsync(1);
        await wire.WaitForCountAsync(Tpdo1, 1);
        wire.Payloads(Tpdo1)[0].Should().Equal(new byte[] { 0xAA, 0xEF, 0xBE, 0x00 },
            "sub-index 01h is the first object, 02h the second, and the dummy in 03h is a zero byte");
    }

    // FR-CO-020 — sub-index 00h: step 4 "the mapping is not valid or not possible" (0602 0000h /
    // 0604 0042h) for a count that names an empty slot or more entries than fit; the reserved and
    // MPDO values of Table 73 are not supported (0609 0030h).
    [Fact]
    public async Task Tpdo_Mapping_Count_Is_Validated_Against_The_Entries_And_Table_73()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, Master);
        using var device = CanOpen.OpenNode(busB, Device, MasterConfigurable);
        device.ObjectDictionary.AddU8(0x2000, 0x00, 0);

        await DownloadAsync(master, 0x1A00, 0x00, new byte[] { 0x00 });
        await DownloadAsync(master, 0x1A00, 0x01, MappingEntryBytes(0x2000, 0x00, 8));

        (await DownloadShouldAbortAsync(master, 0x1A00, 0x00, new byte[] { 0x02 }))
            .AbortCode.Should().Be((uint)SdoAbortCode.ObjectDoesNotExist, "slot 02h is empty, so a count of 2 names no object");
        (await DownloadShouldAbortAsync(master, 0x1A00, 0x00, new byte[] { 0x09 }))
            .AbortCode.Should().Be((uint)SdoAbortCode.PdoMappingLengthExceeded, "nine byte-aligned entries cannot fit into 8 bytes");
        (await DownloadShouldAbortAsync(master, 0x1A00, 0x00, new byte[] { 0xFE }))
            .AbortCode.Should().Be((uint)SdoAbortCode.ValueRangeExceeded, "FEh is SAM-MPDO (Table 73), which this node does not support");
        (await UploadAsync(master, 0x1A00, 0x00)).Should().Equal(new byte[] { 0x00 }, "no rejected count is stored");

        await DownloadAsync(master, 0x1A00, 0x00, new byte[] { 0x01 });
        (await UploadAsync(master, 0x1A00, 0x00)).Should().Equal(0x01);
    }

    // FR-CO-020 — a dummy entry in an RPDO (§7.5.2.36: "to fill up the length of the RPDO to fit
    // the length to the according TPDO") skips its bytes and writes nothing.
    [Fact]
    public async Task Rpdo_Dummy_Entry_Skips_Its_Bytes_Without_Touching_The_Object_Dictionary()
    {
        var session = NewSession();
        using var busB = Open(session, 1);
        using var wire = new Wire(session, 2);
        using var device = CanOpen.OpenNode(busB, Device);
        var od = device.ObjectDictionary;
        od.AddU8(0x2100, 0x00, 0);
        // A probe at the dummy's own index: a dummy entry must not write it either.
        od.AddU8(0x0005, 0x00, 0);

        device.ConfigureRpdo(1, new PdoMapping().Add(0x0005, 0x00, 8).Add(0x2100, 0x00, 8));
        od.ReadUnsigned(0x1600, 0x00).Should().Be(2u);

        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        device.RpdoReceived += (_, e) =>
        {
            if (e.CobId == Rpdo1) received.TrySetResult(e.Payload);
        };

        await StartAsync(wire, device);
        wire.Transmit(Rpdo1, new byte[] { 0x55, 0xAA });
        (await received.Task.WithTimeoutAsync(ShortTimeout)).Should().Equal(0x55, 0xAA);
        od.ReadUnsigned(0x2100, 0x00).Should().Be(0xAAu, "the object after the dummy lands at byte offset 1");
        od.ReadUnsigned(0x0005, 0x00).Should().Be(0u, "the dummy's byte is padding, not a write");
    }

    // FR-CO-020 (#42) — the PdoMapping handed to ConfigureTpdo is copied into the mapping record;
    // the caller's object may be mutated afterwards without touching the configured PDO.
    [Fact]
    public async Task Pdo_Mapping_Object_May_Be_Mutated_After_Configuration_Without_Affecting_The_Pdo()
    {
        var session = NewSession();
        using var busB = Open(session, 1);
        using var wire = new Wire(session, 2);
        using var device = CanOpen.OpenNode(busB, Device);
        var od = device.ObjectDictionary;
        od.AddU8(0x2000, 0x00, 0xAA);
        od.AddU16(0x2001, 0x00, 0xBEEF);

        var mapping = new PdoMapping().Add(0x2000, 0x00, 8);
        device.ConfigureTpdo(1, mapping);

        mapping.Add(0x2001, 0x00, 16);
        od.ReadUnsigned(0x1A00, 0x00).Should().Be(1u, "the record holds the mapping as configured");
        od.ReadUnsigned(0x1A00, 0x02).Should().Be(0u);

        await StartAsync(wire, device);
        await device.TriggerTpdoAsync(1);
        await wire.WaitForCountAsync(Tpdo1, 1);
        wire.Payloads(Tpdo1)[0].Should().Equal(0xAA);

        mapping.Clear();
        await device.TriggerTpdoAsync(1);
        await wire.WaitForCountAsync(Tpdo1, 2);
        wire.Payloads(Tpdo1)[1].Should().Equal(0xAA);
    }

    // =========================================================================================
    // FR-CO-024 — RPDO length (CiA 301 §7.5.2.36).
    // =========================================================================================

    // FR-CO-024 — "less data bytes than the number of mapped data bytes": not processed, EMCY with
    // error code 8210h; "more data bytes than the number of mapped data bytes": the first bytes up
    // to the length are used.
    [Fact]
    public async Task Rpdo_Shorter_Than_Its_Mapping_Is_Not_Actuated_And_Raises_Emcy_8210h()
    {
        var session = NewSession();
        using var busB = Open(session, 1);
        using var busC = Open(session, 2);
        using var wire = new Wire(session, 3);
        using var device = CanOpen.OpenNode(busB, Device);
        using var observer = CanOpen.OpenNode(busC, Observer);
        var od = device.ObjectDictionary;
        od.AddU16(0x2100, 0x00, 0);
        od.AddU16(0x2101, 0x00, 0);

        device.ConfigureRpdo(1, new PdoMapping().Add(0x2100, 0x00, 16).Add(0x2101, 0x00, 16));

        var emcy = new TaskCompletionSource<EmcyMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        observer.EmcyReceived += (_, e) =>
        {
            if (e.Message.ProducerNodeId == Device) emcy.TrySetResult(e.Message);
        };
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        device.RpdoReceived += (_, e) =>
        {
            if (e.CobId == Rpdo1) received.TrySetResult(e.Payload);
        };

        await StartAsync(wire, device);

        // Two bytes for a four-byte mapping. The EMCY leaves the same actor callback that declined
        // the PDO, so once it has arrived the decision about the object dictionary is in the past.
        wire.Transmit(Rpdo1, new byte[] { 0x34, 0x12 });
        var message = await emcy.Task.WithTimeoutAsync(ShortTimeout);
        message.ErrorCode.Should().Be((ushort)0x8210, "§7.5.2.36 prescribes error code 8210h for a PDO shorter than its mapping");
        message.ErrorRegister.Should().Be((byte)od.ReadUnsigned(0x1001, 0x00));
        od.ReadUnsigned(0x2100, 0x00).Should().Be(0u, "a PDO shorter than its mapping is not processed");
        od.ReadUnsigned(0x2101, 0x00).Should().Be(0u);
        received.Task.IsCompleted.Should().BeFalse("nothing was actuated, so there is nothing to report");

        // Six bytes for the same mapping: the first four are used.
        wire.Transmit(Rpdo1, new byte[] { 0x34, 0x12, 0x78, 0x56, 0xAA, 0xBB });
        (await received.Task.WithTimeoutAsync(ShortTimeout)).Should().HaveCount(6);
        od.ReadUnsigned(0x2100, 0x00).Should().Be(0x1234u);
        od.ReadUnsigned(0x2101, 0x00).Should().Be(0x5678u, "the first data bytes up to the mapped length are used");
    }

    // =========================================================================================
    // Findings of the #133 review.
    // =========================================================================================

    // FR-CO-020 — steps 1 and 5 of the re-mapping procedure (§7.5.2.38) bracket every mapping
    // change with the PDO destroyed (bit 31 of 1800h:01 set). A mapping written into a live PDO
    // would rebuild it mid-transfer — an emptied mapping transmits empty frames until the master
    // reaches step 4 — so the count and the entries are refused while the PDO exists, and the
    // same writes go through once it is destroyed.
    [Fact]
    public async Task Mapping_Records_Change_Only_While_The_Pdo_Is_Destroyed()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, Master);
        using var device = CanOpen.OpenNode(busB, Device, MasterConfigurable);
        device.ObjectDictionary.AddU8(0x2000, 0x00, 0xAA);
        device.ConfigureTpdo(1, new PdoMapping().Add(0x2000, 0x00, 8));

        (await DownloadShouldAbortAsync(master, 0x1A00, 0x00, new byte[] { 0x00 }))
            .AbortCode.Should().Be((uint)SdoAbortCode.UnsupportedAccess, "TPDO1 exists; step 1 (destroy) has not happened");
        (await DownloadShouldAbortAsync(master, 0x1A00, 0x01, MappingEntryBytes(0x2000, 0x00, 8)))
            .AbortCode.Should().Be((uint)SdoAbortCode.UnsupportedAccess);
        (await UploadAsync(master, 0x1A00, 0x00)).Should().Equal(new byte[] { 0x01 }, "the live mapping is untouched");

        await DownloadAsync(master, 0x1800, 0x01, U32Bytes(CanOpenCobId.InvalidBit | Tpdo1));
        await DownloadAsync(master, 0x1A00, 0x00, new byte[] { 0x00 });
        await DownloadAsync(master, 0x1A00, 0x01, MappingEntryBytes(0x2000, 0x00, 8));
        await DownloadAsync(master, 0x1A00, 0x00, new byte[] { 0x01 });
        await DownloadAsync(master, 0x1800, 0x01, U32Bytes(Tpdo1));
        (await UploadAsync(master, 0x1A00, 0x00)).Should().Equal(0x01);
    }

    // FR-CO-016 — a PDO read (an RTR) of an event-driven TPDO is a transmission of that TPDO,
    // and §7.5.2.37 makes the inhibit time the minimum interval between its transmissions. The
    // SYNC after the RTR is the witness that the RTR was handled (same bus, same subscription,
    // same mailbox); TPDO2's trigger is the witness for the send path, as in the inhibit test.
    [Fact]
    public async Task An_Rtr_On_An_Event_Driven_Tpdo_Respects_The_Inhibit_Time()
    {
        var clock = new ManualTimeSource();
        var session = NewSession();
        using var busB = Open(session, 1);
        using var wire = new Wire(session, 2);
        using var device = OpenClocked(busB, Device, clock, new CanOpenNodeOptions { EnableChangeOfStateTpdo = false });
        var od = device.ObjectDictionary;
        od.AddU16(0x2000, 0x00, 1);
        od.AddU8(0x2001, 0x00, 0);
        device.ConfigureTpdo(1, new PdoMapping().Add(0x2000, 0x00, 16), TpdoTransmission.EventDriven,
            inhibitTime: TimeSpan.FromMilliseconds(50));
        device.ConfigureTpdo(2, new PdoMapping().Add(0x2001, 0x00, 8), TpdoTransmission.EventDriven);
        var syncs = new SemaphoreSlim(0);
        device.SyncReceived += (_, _) => syncs.Release();

        await StartAsync(wire, device);
        await device.TriggerTpdoAsync(1);
        await wire.WaitForCountAsync(Tpdo1, 1);

        wire.SendRtr(Tpdo1);
        wire.SendSync();
        (await syncs.WaitAsync(ShortTimeout)).Should().BeTrue("the SYNC behind the RTR proves the RTR was handled");
        await device.TriggerTpdoAsync(2);
        await wire.WaitForCountAsync(Tpdo2, 1);
        wire.Count(Tpdo1).Should().Be(1, "the RTR arrived inside the 50 ms inhibit time");

        Advance(clock, device, TimeSpan.FromMilliseconds(50));
        await wire.WaitForCountAsync(Tpdo1, 2);
        wire.Payloads(Tpdo1)[1].Should().Equal(0x01, 0x00);
    }

    // FR-CO-016 — an event timer shorter than the inhibit time cannot undercut it: the timer's
    // expiry is an event like any other, and the transmission it asks for waits for the inhibit
    // time to elapse.
    [Fact]
    public async Task An_Event_Timer_Shorter_Than_The_Inhibit_Time_Does_Not_Undercut_It()
    {
        var clock = new ManualTimeSource();
        var session = NewSession();
        using var busB = Open(session, 1);
        using var wire = new Wire(session, 2);
        using var device = OpenClocked(busB, Device, clock, new CanOpenNodeOptions { EnableChangeOfStateTpdo = false });
        var od = device.ObjectDictionary;
        od.AddU16(0x2000, 0x00, 1);
        od.AddU8(0x2001, 0x00, 0);
        device.ConfigureTpdo(1, new PdoMapping().Add(0x2000, 0x00, 16), TpdoTransmission.EventTimer,
            eventTimerInterval: TimeSpan.FromMilliseconds(20), inhibitTime: TimeSpan.FromMilliseconds(50));
        device.ConfigureTpdo(2, new PdoMapping().Add(0x2001, 0x00, 8), TpdoTransmission.EventDriven);

        await StartAsync(wire, device);
        await device.TriggerTpdoAsync(1);
        await wire.WaitForCountAsync(Tpdo1, 1);

        Advance(clock, device, TimeSpan.FromMilliseconds(20));
        await device.TriggerTpdoAsync(2);
        await wire.WaitForCountAsync(Tpdo2, 1);
        wire.Count(Tpdo1).Should().Be(1, "the timer elapsed 20 ms after a transmission, inside the 50 ms inhibit time");

        Advance(clock, device, TimeSpan.FromMilliseconds(30));
        await wire.WaitForCountAsync(Tpdo1, 2);
    }

    // FR-CO-016 — a transmission waiting out the inhibit time is an event the application raised;
    // a record write that leaves the PDO valid (the event timer, 1800h:05, may change while the
    // PDO exists) rebuilds the TPDO but must not lose it.
    [Fact]
    public async Task A_Transmission_Waiting_Out_The_Inhibit_Time_Survives_An_Event_Timer_Write()
    {
        var clock = new ManualTimeSource();
        var session = NewSession();
        using var busB = Open(session, 1);
        using var wire = new Wire(session, 2);
        using var device = OpenClocked(busB, Device, clock, new CanOpenNodeOptions { EnableChangeOfStateTpdo = false });
        var od = device.ObjectDictionary;
        od.AddU16(0x2000, 0x00, 1);
        device.ConfigureTpdo(1, new PdoMapping().Add(0x2000, 0x00, 16), TpdoTransmission.EventDriven,
            inhibitTime: TimeSpan.FromMilliseconds(50));

        await StartAsync(wire, device);
        await device.TriggerTpdoAsync(1);
        await wire.WaitForCountAsync(Tpdo1, 1);
        od.WriteUnsigned(0x2000, 0x00, 2);
        await device.TriggerTpdoAsync(1); // inside the inhibit time: waits

        od.WriteUnsigned(0x1800, 0x05, 500); // allowed while the PDO is valid; rebuilds the TPDO
        Settle(device);
        od.ReadUnsigned(0x1800, 0x05).Should().Be(500u);

        Advance(clock, device, TimeSpan.FromMilliseconds(50));
        await wire.WaitForCountAsync(Tpdo1, 2);
        wire.Payloads(Tpdo1)[1].Should().Equal(new byte[] { 0x02, 0x00 }, "the waiting transmission went out with the current value");
    }
}
