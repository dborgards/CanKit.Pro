using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Pdo;
using CanKit.Pro.CANopen.Safety;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Safety;

/// <summary>Two nodes on the virtual bus: a producer and a consumer configured through
/// <see cref="ICanOpenSafety"/>, and the master configuring the device over SDO. Wire-level
/// expectations are awaited as positives; a negative is shown with an ordering witness
/// (the heartbeat the node emits on an NMT transition, or a later SRDO pair). FR-CO-042 the
/// facade; FR-CO-038 the check at the transition to Operational; FR-CO-039 the pairs on the
/// bus; FR-CO-041 the GFC; FR-CO-035 the reset.</summary>
public class CanOpenSafetyNodeTests : IClassFixture<VirtualAdapterFixture>
{
    private const byte Producer = 0x11;
    private const byte Consumer = 0x12;
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);

    private static ICanBus Open(string session, int channel) => CanBus.Open(
        $"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(VirtualAdapterFixture.Bitrate));

    private static string NewSession() => VirtualAdapterFixture.NewSession("co-safety-node");

    private static ICanOpenNode OpenClocked(ICanBus bus, byte nodeId, ManualTimeSource clock, int srdoCount = 2)
        => new CanOpenNode(new CanBusService(bus), nodeId,
            new CanOpenNodeOptions { SrdoCount = srdoCount, WritableCommunicationParameters = true }, ownsService: true, clock);

    private static void Settle(ICanOpenNode node) { _ = node.State; _ = node.State; }
    private static void Advance(ManualTimeSource clock, ICanOpenNode node, TimeSpan by) { Settle(node); clock.Advance(by); Settle(node); }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + ShortTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(what);
            await Task.Delay(5);
        }
    }

    /// <summary>A raw channel that records data frames per COB-ID and sends NMT.</summary>
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
                var f = e.CanFrame;
                if (f.IsExtendedFrame || f.IsRemoteFrame) return;
                lock (_gate)
                {
                    if (!_frames.TryGetValue((uint)f.ID, out var list)) _frames[(uint)f.ID] = list = new List<byte[]>();
                    list.Add(f.Data.ToArray());
                }
            };
        }
        public int Count(uint cobId) { lock (_gate) return _frames.TryGetValue(cobId, out var l) ? l.Count : 0; }
        /// <summary>Heartbeats (or boot-ups) on <paramref name="cobId"/> that report <paramref name="state"/>.</summary>
        public int CountState(uint cobId, NmtState state) { lock (_gate) return _frames.TryGetValue(cobId, out var l) ? l.Count(p => p.Length == 1 && p[0] == (byte)state) : 0; }
        public Task WaitForStateCountAsync(uint cobId, NmtState state, int count) => WaitUntilAsync(() => CountState(cobId, state) >= count, $"expected {count} {state} frame(s) on 0x{cobId:X3}, saw {CountState(cobId, state)}");
        public byte[][] Payloads(uint cobId) { lock (_gate) return _frames.TryGetValue(cobId, out var l) ? l.ToArray() : Array.Empty<byte[]>(); }
        public Task WaitForCountAsync(uint cobId, int count) => WaitUntilAsync(() => Count(cobId) >= count, $"expected {count} frame(s) on 0x{cobId:X3}, saw {Count(cobId)}");
        public void Transmit(uint cobId, byte[] data) => _bus.Transmit(CanFrame.Classic(unchecked((int)cobId), data, isExtendedFrame: false));
        public void SendNmt(NmtCommand command, byte nodeId) => Transmit(CanOpenCobId.NmtCommand, new[] { (byte)command, nodeId });
        public void Dispose() => _bus.Dispose();
    }

    private static void AddApplicationObjects(ObjectDictionary od)
    {
        od.AddU16(0x2000, 0x00, 0x1234);
        od.AddU8(0x2001, 0x00, 0x5A);
    }

    [Fact]
    public async Task Node_Without_Srdos_Is_Unchanged()
    {
        var session = NewSession();
        using var bus = Open(session, 1);
        using var wire = new Wire(session, 2);
        using var node = CanOpen.OpenNode(bus, Producer);
        var safety = node.Safety();
        safety.SrdoCount.Should().Be(0);
        Assert.Throws<InvalidOperationException>(() => safety.ConfigureSrdoProducer(1, new SrdoMapping(), TimeSpan.FromMilliseconds(25)));
        Assert.Throws<ArgumentOutOfRangeException>(() => safety.GetSrdoState(1));
        // 1300h declared by the application is the application's object: 001h raises nothing.
        node.ObjectDictionary.AddU8(0x1300, 0x00, 1);
        int gfcs = 0;
        safety.GlobalFailsafeCommandReceived += (_, _) => Interlocked.Increment(ref gfcs);
        var preOperational = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        node.NmtCommandReceived += (_, e) => { if (e.Command == NmtCommand.EnterPreOperational) preOperational.TrySetResult(true); };
        wire.SendNmt(NmtCommand.Start, Producer);
        await WaitUntilAsync(() => node.State == NmtState.Operational, "start");
        wire.Transmit(CanOpenCobId.GlobalFailsafeCommand, Array.Empty<byte>());
        // The witness: an NMT command sent after the GFC, delivered on the same event queue.
        wire.SendNmt(NmtCommand.EnterPreOperational, Producer);
        await preOperational.Task.WithTimeoutAsync(ShortTimeout);
        Volatile.Read(ref gfcs).Should().Be(0);
    }

    [Fact]
    public void Safety_Of_A_Foreign_Node_Is_Not_Supported()
    {
        var foreign = new ForeignNode();
        Assert.Throws<NotSupportedException>(() => foreign.Safety());
    }

    [Fact]
    public async Task Configure_Commit_And_Transmit()
    {
        var session = NewSession();
        using var bus = Open(session, 1);
        using var wire = new Wire(session, 2);
        var clock = new ManualTimeSource();
        using var node = OpenClocked(bus, Producer, clock);
        AddApplicationObjects(node.ObjectDictionary);
        var safety = node.Safety();
        safety.ConfigureSrdoProducer(1, new SrdoMapping().Add(0x2000, 0x00, 16).Add(0x2001, 0x00, 8), TimeSpan.FromMilliseconds(25));
        var od = node.ObjectDictionary;
        od.ReadUnsigned(0x1301, 1).Should().Be(1u);
        od.ReadUnsigned(0x1301, 2).Should().Be(25u);
        od.ReadUnsigned(0x1301, 5).Should().Be(0x0FFu + 2 * Producer);
        od.ReadUnsigned(0x1381, 0).Should().Be(4u);
        od.ReadUnsigned(0x1381, 4).Should().Be(0x2001_0008u);
        od.ReadUnsigned(0x13FE, 0).Should().Be(0u, "not committed");
        safety.CommitSafetyConfiguration();
        od.ReadUnsigned(0x13FE, 0).Should().Be(0xA5u);
        SrdoRecords.TryReadCommunication(od, 1, out var p);
        od.ReadUnsigned(0x13FF, 1).Should().Be(SrdoCrc.Compute(p, SrdoMapping.FromEntries(SrdoRecords.ReadMapping(od, 1))));
        wire.SendNmt(NmtCommand.Start, Producer);
        await WaitUntilAsync(() => node.State == NmtState.Operational, "start");
        safety.GetSrdoState(1).IsValid.Should().BeTrue();
        Advance(clock, node, TimeSpan.FromMilliseconds(Producer));
        await wire.WaitForCountAsync(0x0FFu + 2 * Producer, 1);
        await wire.WaitForCountAsync(0x100u + 2 * Producer, 1);
        wire.Payloads(0x0FFu + 2 * Producer)[0].Should().Equal(0x34, 0x12, 0x5A);
        wire.Payloads(0x100u + 2 * Producer)[0].Should().Equal(0xCB, 0xED, 0xA5);
    }

    [Fact]
    public void Commit_Writes_Checksums_Then_Valid()
    {
        var session = NewSession();
        using var bus = Open(session, 1);
        using var node = CanOpen.OpenNode(bus, Producer, new CanOpenNodeOptions { SrdoCount = 2 });
        AddApplicationObjects(node.ObjectDictionary);
        var safety = node.Safety();
        safety.ConfigureSrdoProducer(1, new SrdoMapping().Add(0x2001, 0x00, 8), TimeSpan.FromMilliseconds(25));
        safety.ConfigureSrdoConsumer(2, new SrdoMapping().Add(0x2000, 0x00, 16), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(20), 0x125, 0x126);
        safety.CommitSafetyConfiguration();
        node.ObjectDictionary.ReadUnsigned(0x13FE, 0).Should().Be(0xA5u, "A5h is written after the checksums, which each clear it");
        node.ObjectDictionary.ReadUnsigned(0x13FF, 2).Should().NotBe(0u);
    }

    [Fact]
    public async Task Tampered_Checksum_Makes_The_Configuration_Invalid_At_Start()
    {
        var session = NewSession();
        using var bus = Open(session, 1);
        using var wire = new Wire(session, 2);
        var clock = new ManualTimeSource();
        using var node = OpenClocked(bus, Producer, clock);
        AddApplicationObjects(node.ObjectDictionary);
        var safety = node.Safety();
        var changes = new List<SrdoStateChangedEventArgs>();
        safety.SrdoStateChanged += (_, e) => { lock (changes) changes.Add(e); };
        safety.ConfigureSrdoProducer(1, new SrdoMapping().Add(0x2001, 0x00, 8), TimeSpan.FromMilliseconds(25));
        safety.CommitSafetyConfiguration();
        node.ObjectDictionary.WriteUnsigned(0x13FF, 1, node.ObjectDictionary.ReadUnsigned(0x13FF, 1) ^ 1);
        node.ObjectDictionary.WriteUnsigned(0x13FE, 0, 0xA5);
        node.StartHeartbeatProducer(TimeSpan.FromMilliseconds(100)); // the witness: the state-change heartbeat
        wire.SendNmt(NmtCommand.Start, Producer);
        await WaitUntilAsync(() => node.State == NmtState.Operational, "start");
        await wire.WaitForStateCountAsync(0x700u + Producer, NmtState.Operational, 1);
        Advance(clock, node, TimeSpan.FromMilliseconds(100));
        // The cyclic heartbeat on the same clock is due after the first SRDO cycle (8.5 ms) would
        // have been: it witnesses that the actor has run that cycle. It is not a wire-order
        // guarantee — heartbeats and SRDOs go out through different send chains.
        await wire.WaitForStateCountAsync(0x700u + Producer, NmtState.Operational, 2);
        // The wire-order witness: the SRDO chain's tail, read on the actor after the cycles ran.
        // Once it completes, every pair those cycles handed over has been confirmed on the bus.
        await AwaitSrdoChainAsync((CanOpenNode)node);
        wire.Count(0x0FFu + 2 * Producer).Should().Be(0, "§8.3.1 D: the safety node shall not transmit SRDOs");
        await WaitUntilAsync(() => { lock (changes) return changes.Any(c => c.Reason == SrdoInvalidReason.ConfigurationInvalid); }, "state change");
        safety.GetSrdoState(1).Reason.Should().Be(SrdoInvalidReason.ConfigurationInvalid);
    }

    // CiA 319 v2.0.0 §5.3 step 4 (FR-CO-058): the device compares the checksums at the write of
    // A5h, not only at the transition to Operational, so the configuration entity's readback of
    // step 4 sees 00h. A deleted SRDO has no configuration to protect and is not compared.
    [Fact]
    public void Writing_A5h_Over_A_Wrong_Checksum_Sets_13FEh_Back_To_Zero_At_Once()
    {
        var session = NewSession();
        using var bus = Open(session, 1);
        using var node = CanOpen.OpenNode(bus, Producer, new CanOpenNodeOptions { SrdoCount = 2 });
        AddApplicationObjects(node.ObjectDictionary);
        var safety = node.Safety();
        safety.ConfigureSrdoProducer(1, new SrdoMapping().Add(0x2001, 0x00, 8), TimeSpan.FromMilliseconds(25));
        safety.CommitSafetyConfiguration();
        var od = node.ObjectDictionary;
        od.ReadUnsigned(0x13FE, 0).Should().Be(0xA5u, "the checksums match");

        od.WriteUnsigned(0x13FF, 1, od.ReadUnsigned(0x13FF, 1) ^ 1);
        od.WriteUnsigned(0x13FE, 0, 0xA5);
        od.ReadUnsigned(0x13FE, 0).Should().Be(0u, "§5.3 step 4: the SRD compared the CRCs at the write of A5h");

        od.WriteUnsigned(0x13FF, 2, 0x1234); // SRDO 2 is deleted: its checksum is not compared
        od.WriteUnsigned(0x13FF, 1, od.ReadUnsigned(0x13FF, 1) ^ 1);
        od.WriteUnsigned(0x13FE, 0, 0xA5);
        od.ReadUnsigned(0x13FE, 0).Should().Be(0xA5u, "SRDO 1 matches again and SRDO 2 does not exist");
    }

    // CiA 319 §5.3, after step 5 (FR-CO-036): the first SDO download indication to a safety
    // parameter clears 13FEh "even if the value included in the download indication is identical
    // to the value currently in the object entry"; a download the node answers with a failure
    // "shall not".
    [Fact]
    public async Task A_Download_Of_The_Identical_Value_Clears_13FEh_A_Refused_One_Does_Not()
    {
        var session = NewSession();
        using var bus = Open(session, 1);
        using var wire = new Wire(session, 2);
        using var device = CanOpen.OpenNode(bus, Producer,
            new CanOpenNodeOptions { SrdoCount = 1, WritableCommunicationParameters = true });
        AddApplicationObjects(device.ObjectDictionary);
        device.Safety().ConfigureSrdoProducer(1, new SrdoMapping().Add(0x2001, 0x00, 8), TimeSpan.FromMilliseconds(25));
        device.Safety().CommitSafetyConfiguration();
        var od = device.ObjectDictionary;
        uint request = CanOpenCobId.SdoRx(Producer), response = CanOpenCobId.SdoTx(Producer);

        // Refused: sub-index 04h is the transmission type, 0609 0030h on any write (§8.4.2.2).
        wire.Transmit(request, new byte[] { 0x2F, 0x01, 0x13, 0x04, 254, 0, 0, 0 });
        await wire.WaitForCountAsync(response, 1);
        wire.Payloads(response)[0][0].Should().Be(0x80, "abort");
        od.ReadUnsigned(0x13FE, 0).Should().Be(0xA5u, "a download answered with a failure is no indication");

        // Identical: 25 ms is what the record holds.
        wire.Transmit(request, new byte[] { 0x2B, 0x01, 0x13, 0x02, 25, 0, 0, 0 });
        await wire.WaitForCountAsync(response, 2);
        wire.Payloads(response)[1][0].Should().Be(0x60, "accepted");
        od.ReadUnsigned(0x1301, 2).Should().Be(25u);
        od.ReadUnsigned(0x13FE, 0).Should().Be(0u, "the download indication clears 13FEh, whether or not the value changed");
    }

    // CiA 319 §5.4 (FR-CO-058): after a reset the stored configuration is compared with the
    // checksum stored with it before the boot-up. The store is a snapshot of the dictionary, so
    // a record changed behind the dictionary's back — SetRawValue, the stand-in for a bit flipped
    // in NVRAM — is stored next to the A5h that was never cleared for it, and comes back so. The
    // checksum is recomputed over the record as the dictionary holds it, the inverted mapping
    // slots included (Codex on #329): the second case corrupts one of those, which the plain
    // slots say nothing about.
    [Theory]
    [InlineData(0x1301, 0x02, new byte[] { 40, 0 })]
    [InlineData(0x1381, 0x02, new byte[] { 0x10, 0x00, 0x00, 0x20 })]
    public async Task A_Stored_Configuration_That_Does_Not_Match_Its_Checksum_Comes_Back_Invalid(int index, byte subindex, byte[] corrupted)
    {
        var session = NewSession();
        using var bus = Open(session, 1);
        using var wire = new Wire(session, 2);
        using var node = CanOpen.OpenNode(bus, Producer, new CanOpenNodeOptions { SrdoCount = 1 });
        AddApplicationObjects(node.ObjectDictionary);
        node.Safety().ConfigureSrdoProducer(1, new SrdoMapping().Add(0x2001, 0x00, 8), TimeSpan.FromMilliseconds(30));
        node.Safety().CommitSafetyConfiguration();
        var od = node.ObjectDictionary;
        od.TryGet((ushort)index, subindex, out var entry).Should().BeTrue();
        entry.SetRawValue(corrupted); // not a write: no hook, 13FEh stays A5h
        node.StoreParameters();
        od.ReadUnsigned(0x13FE, 0).Should().Be(0xA5u, "nothing told the node");

        wire.SendNmt(NmtCommand.ResetCommunication, Producer);
        await WaitUntilAsync(() => od.ReadRaw((ushort)index, subindex).AsSpan().SequenceEqual(corrupted) && node.State == NmtState.PreOperational, "restored");
        // The restore and the §5.4 check run in the reset's actor turn; a State read is a round
        // trip queued behind it.
        _ = node.State;
        od.ReadRaw((ushort)index, subindex).Should().Equal(corrupted, "the stored record came back as stored (FR-CO-035)");
        od.ReadUnsigned(0x13FE, 0).Should().Be(0u, "§5.4: the CRC in 13FFh is the one of the record as committed");
    }

    // CiA 319 §5.3 (FR-CO-059): "the SRD should validate the consistency of its safety-related
    // configuration by periodically recalculating the CRC". Opt-in by interval. In Operational a
    // mismatch makes the SRDO ConfigurationInvalid and the producer falls silent (§8.3.1 D).
    // The corrupted byte is the refresh time, or the direction — to 0 (deleted) or 3 (reserved,
    // read as none): the records are compared with the image taken when A5h was written, so a
    // record that now reads as gone is a mismatch, not a deletion (Bugbot on #329). The SRDO then
    // reports itself as no longer existing (reason null, as the record reads), where the time
    // case reports ConfigurationInvalid; either way it is invalid, 13FEh is 00h and nothing more
    // goes out.
    [Theory]
    [InlineData(0x02, 40)]
    [InlineData(0x01, 0)]
    [InlineData(0x01, 3)]
    public async Task The_Periodic_Check_Finds_A_Corrupted_Record_And_Stops_The_Producer(byte subindex, byte value)
    {
        var session = NewSession();
        using var bus = Open(session, 1);
        using var wire = new Wire(session, 2);
        var clock = new ManualTimeSource();
        using var node = new CanOpenNode(new CanBusService(bus), Producer,
            new CanOpenNodeOptions { SrdoCount = 1, SrdoConfigurationCheckInterval = TimeSpan.FromSeconds(1) },
            ownsService: true, clock);
        AddApplicationObjects(node.ObjectDictionary);
        var safety = node.Safety();
        var changes = new List<SrdoStateChangedEventArgs>();
        safety.SrdoStateChanged += (_, e) => { lock (changes) changes.Add(e); };
        safety.ConfigureSrdoProducer(1, new SrdoMapping().Add(0x2001, 0x00, 8), TimeSpan.FromMilliseconds(25));
        safety.CommitSafetyConfiguration();
        wire.SendNmt(NmtCommand.Start, Producer);
        await WaitUntilAsync(() => node.State == NmtState.Operational, "start");
        safety.GetSrdoState(1).IsValid.Should().BeTrue();
        var od = node.ObjectDictionary;

        // A check with nothing wrong changes nothing.
        Advance(clock, node, TimeSpan.FromSeconds(1));
        od.ReadUnsigned(0x13FE, 0).Should().Be(0xA5u);
        safety.GetSrdoState(1).IsValid.Should().BeTrue();

        od.TryGet(0x1301, subindex, out var entry).Should().BeTrue();
        entry.SetRawValue(subindex == 0x02 ? new byte[] { value, 0 } : new[] { value }); // behind the dictionary's back: no hook, nothing cleared
        Advance(clock, node, TimeSpan.FromSeconds(1));
        od.ReadUnsigned(0x13FE, 0).Should().Be(0u, "the next check found the record and its checksum apart");
        await WaitUntilAsync(() => { lock (changes) return changes.Any(c => !c.IsValid); }, "state change");
        safety.GetSrdoState(1).IsValid.Should().BeFalse();
        safety.GetSrdoState(1).Reason.Should().Be(subindex == 0x02 ? SrdoInvalidReason.ConfigurationInvalid : null,
            "a record that reads as deleted or reserved is an SRDO that no longer exists; a wrong time is a wrong checksum");

        // Nothing more goes out: the chain drained, a completed pair's continuation settled, the
        // chain drained again — then the count is final, and a further cycle's worth of time adds
        // nothing to it.
        await AwaitSrdoChainAsync(node);
        Settle(node);
        await AwaitSrdoChainAsync(node);
        int pairs = wire.Count(0x0FFu + 2 * Producer);
        Advance(clock, node, TimeSpan.FromMilliseconds(100));
        await AwaitSrdoChainAsync(node);
        Settle(node);
        await AwaitSrdoChainAsync(node);
        wire.Count(0x0FFu + 2 * Producer).Should().Be(pairs, "§8.3.1 D: the safety node shall not transmit SRDOs");
    }

    // FR-CO-059, the pair that was waiting: a pair pending behind an unconfirmed one does not
    // reach the bus once the periodic check has found the configuration apart from its checksum.
    // The SRDO is no longer valid, and §8.3.1 D has the node transmit nothing.
    [Fact]
    public async Task A_Pending_Pair_Is_Dropped_When_The_Periodic_Check_Invalidates_The_Srdo()
    {
        var session = NewSession();
        using var bus = Open(session, 1);
        using var wire = new Wire(session, 2);
        var clock = new ManualTimeSource();
        var service = new HoldingService(new CanBusService(bus));
        using var node = new CanOpenNode(service, Producer,
            new CanOpenNodeOptions
            {
                SrdoCount = 1,
                EnableChangeOfStateSrdo = false,
                SrdoConfigurationCheckInterval = TimeSpan.FromMilliseconds(60), // armed at open, due at 60 ms
            }, ownsService: true, clock);
        AddApplicationObjects(node.ObjectDictionary);
        node.Safety().ConfigureSrdoProducer(1, new SrdoMapping().Add(0x2001, 0x00, 8), TimeSpan.FromMilliseconds(25));
        node.Safety().CommitSafetyConfiguration();
        uint cob1 = 0x0FFu + 2 * Producer;
        uint cob2 = cob1 + 1;
        wire.SendNmt(NmtCommand.Start, Producer);
        await WaitUntilAsync(() => node.State == NmtState.Operational, "start");

        Advance(clock, node, TimeSpan.FromMilliseconds(Producer));          // first pair at 8.5 ms, held
        await WaitUntilAsync(() => service.Handed(cob1) == 1, "first frame handed to the service");
        Advance(clock, node, TimeSpan.FromMilliseconds(25));                // cycle at 33.5 ms: pending behind the held pair
        service.Handed(cob1).Should().Be(1, "the pair due meanwhile waits behind the held one");

        node.ObjectDictionary.TryGet(0x1301, 0x02, out var refresh).Should().BeTrue();
        refresh.SetRawValue(new byte[] { 40, 0 });                          // behind the dictionary's back
        Advance(clock, node, TimeSpan.FromMilliseconds(30));                // 63.5 ms: the check has run
        await WaitUntilAsync(() => node.Safety().GetSrdoState(1).Reason == SrdoInvalidReason.ConfigurationInvalid, "invalidated");
        node.ObjectDictionary.ReadUnsigned(0x13FE, 0).Should().Be(0u);

        service.Release();                                                  // the held pair completes
        await AwaitSrdoChainAsync(node);
        await WaitUntilAsync(() => !SrdoPairInFlight(node), "nothing in flight");
        service.Handed(cob1).Should().Be(1, "the pair that waited behind the held one does not go out: the period ended with the invalidation");
        service.Handed(cob2).Should().Be(0, "nor does the held pair's inverted half, as on leaving Operational");
    }

    // Codex on #329: a pair of another SRDO that was already queued on the send chain behind the
    // held one is not covered by the pending slot; the period's end is what stops it.
    [Fact]
    public async Task A_Pair_Already_Queued_Behind_A_Held_One_Is_Dropped_When_The_Periodic_Check_Invalidates()
    {
        var session = NewSession();
        using var bus = Open(session, 1);
        using var wire = new Wire(session, 2);
        var clock = new ManualTimeSource();
        var service = new HoldingService(new CanBusService(bus));
        using var node = new CanOpenNode(service, Producer,
            new CanOpenNodeOptions
            {
                SrdoCount = 2,
                EnableChangeOfStateSrdo = false,
                SrdoConfigurationCheckInterval = TimeSpan.FromMilliseconds(60),
            }, ownsService: true, clock);
        AddApplicationObjects(node.ObjectDictionary);
        node.Safety().ConfigureSrdoProducer(1, new SrdoMapping().Add(0x2001, 0x00, 8), TimeSpan.FromMilliseconds(25));
        node.Safety().ConfigureSrdoProducer(2, new SrdoMapping().Add(0x2000, 0x00, 16), TimeSpan.FromMilliseconds(25), 0x125, 0x126);
        node.Safety().CommitSafetyConfiguration();
        uint cob1 = 0x0FFu + 2 * Producer;
        wire.SendNmt(NmtCommand.Start, Producer);
        await WaitUntilAsync(() => node.State == NmtState.Operational, "start");

        // Both first pairs are due at 8.5 ms; SRDO 1's is handed and held, SRDO 2's link is
        // queued on the chain behind it and has handed nothing yet.
        Advance(clock, node, TimeSpan.FromMilliseconds(Producer));
        await WaitUntilAsync(() => service.Handed(cob1) == 1, "first frame handed to the service");
        await WaitUntilAsync(() => SrdoPairInFlight(node, 2), "SRDO 2's pair on the chain");
        service.Handed(0x125).Should().Be(0, "queued behind the held pair");

        node.ObjectDictionary.TryGet(0x1301, 0x02, out var refresh).Should().BeTrue();
        refresh.SetRawValue(new byte[] { 40, 0 });
        Advance(clock, node, TimeSpan.FromMilliseconds(60));                // the check at 60 ms
        await WaitUntilAsync(() => !node.Safety().GetSrdoState(1).IsValid && !node.Safety().GetSrdoState(2).IsValid, "invalidated");

        service.Release();
        await AwaitSrdoChainAsync(node);
        await WaitUntilAsync(() => !SrdoPairInFlight(node) && !SrdoPairInFlight(node, 2), "nothing in flight");
        service.Handed(0x125).Should().Be(0, "the queued pair of SRDO 2 never reaches the service");
        service.Handed(0x126).Should().Be(0);
        service.Handed(cob1 + 1).Should().Be(0, "nor the inverted half of the held pair");
    }

    // A legitimate delete followed by a commit is not a corruption: the image is taken anew at
    // the commit's A5h, so the next periodic check finds nothing.
    [Fact]
    public async Task A_Delete_Followed_By_A_Commit_Survives_The_Periodic_Check()
    {
        var session = NewSession();
        using var bus = Open(session, 1);
        var clock = new ManualTimeSource();
        using var node = new CanOpenNode(new CanBusService(bus), Producer,
            new CanOpenNodeOptions { SrdoCount = 2, SrdoConfigurationCheckInterval = TimeSpan.FromSeconds(1) }, ownsService: true, clock);
        AddApplicationObjects(node.ObjectDictionary);
        var safety = node.Safety();
        safety.ConfigureSrdoProducer(1, new SrdoMapping().Add(0x2001, 0x00, 8), TimeSpan.FromMilliseconds(25));
        safety.ConfigureSrdoProducer(2, new SrdoMapping().Add(0x2000, 0x00, 16), TimeSpan.FromMilliseconds(25), 0x125, 0x126);
        safety.CommitSafetyConfiguration();
        Advance(clock, node, TimeSpan.FromSeconds(1));
        node.ObjectDictionary.ReadUnsigned(0x13FE, 0).Should().Be(0xA5u);

        safety.DeleteSrdo(2);
        node.ObjectDictionary.ReadUnsigned(0x13FE, 0).Should().Be(0u, "the delete is a write");
        Advance(clock, node, TimeSpan.FromSeconds(1));                      // a check with 13FEh at 00h: nothing to compare
        node.ObjectDictionary.ReadUnsigned(0x13FE, 0).Should().Be(0u);
        safety.CommitSafetyConfiguration();
        node.ObjectDictionary.ReadUnsigned(0x13FE, 0).Should().Be(0xA5u);
        Advance(clock, node, TimeSpan.FromSeconds(1));
        Advance(clock, node, TimeSpan.FromSeconds(1));
        node.ObjectDictionary.ReadUnsigned(0x13FE, 0).Should().Be(0xA5u, "deleted by a write, committed, and unchanged since");
    }

    // Codex on #329: a deleted SRDO has no configuration to protect, so its unused record and
    // checksum slot are not in the image — a bit flipped there does not take the live SRDO down.
    [Fact]
    public async Task A_Change_Behind_The_Dictionary_In_A_Deleted_Srdos_Record_Is_Not_A_Mismatch()
    {
        var session = NewSession();
        using var bus = Open(session, 1);
        using var wire = new Wire(session, 2);
        var clock = new ManualTimeSource();
        using var node = new CanOpenNode(new CanBusService(bus), Producer,
            new CanOpenNodeOptions { SrdoCount = 2, SrdoConfigurationCheckInterval = TimeSpan.FromSeconds(1) }, ownsService: true, clock);
        AddApplicationObjects(node.ObjectDictionary);
        var safety = node.Safety();
        safety.ConfigureSrdoProducer(1, new SrdoMapping().Add(0x2001, 0x00, 8), TimeSpan.FromMilliseconds(25));
        safety.CommitSafetyConfiguration();                                 // SRDO 2 stays deleted
        wire.SendNmt(NmtCommand.Start, Producer);
        await WaitUntilAsync(() => node.State == NmtState.Operational, "start");
        var od = node.ObjectDictionary;

        od.TryGet(0x1302, 0x02, out var refresh2).Should().BeTrue();
        refresh2.SetRawValue(new byte[] { 40, 0 });                         // the deleted SRDO's record
        od.TryGet(0x13FF, 0x02, out var checksum2).Should().BeTrue();
        checksum2.SetRawValue(new byte[] { 0x34, 0x12 });                   // and its checksum slot
        Advance(clock, node, TimeSpan.FromSeconds(1));
        od.ReadUnsigned(0x13FE, 0).Should().Be(0xA5u, "nothing of SRDO 1 changed");
        safety.GetSrdoState(1).IsValid.Should().BeTrue();
    }

    // Outside Operational a mismatch clears 13FEh and nothing else: the engine is not re-read —
    // the transition to Operational does that (§9.5) — and the SRDO stays NotOperational.
    [Fact]
    public void A_Mismatch_Found_Outside_Operational_Clears_13FEh_And_Leaves_The_Engine_To_The_Transition()
    {
        var session = NewSession();
        using var bus = Open(session, 1);
        var clock = new ManualTimeSource();
        using var node = new CanOpenNode(new CanBusService(bus), Producer,
            new CanOpenNodeOptions { SrdoCount = 1, SrdoConfigurationCheckInterval = TimeSpan.FromSeconds(1) }, ownsService: true, clock);
        AddApplicationObjects(node.ObjectDictionary);
        var safety = node.Safety();
        safety.ConfigureSrdoProducer(1, new SrdoMapping().Add(0x2001, 0x00, 8), TimeSpan.FromMilliseconds(25));
        safety.CommitSafetyConfiguration();
        node.State.Should().Be(NmtState.PreOperational);

        node.ObjectDictionary.TryGet(0x1301, 0x02, out var refresh).Should().BeTrue();
        refresh.SetRawValue(new byte[] { 40, 0 });
        Advance(clock, node, TimeSpan.FromSeconds(1));
        node.ObjectDictionary.ReadUnsigned(0x13FE, 0).Should().Be(0u);
        safety.GetSrdoState(1).Reason.Should().Be(SrdoInvalidReason.NotOperational, "the engine was not touched");
    }

    [Fact]
    public async Task Configuration_Is_Refused_In_Operational()
    {
        var session = NewSession();
        using var bus = Open(session, 1);
        using var wire = new Wire(session, 2);
        using var node = CanOpen.OpenNode(bus, Producer, new CanOpenNodeOptions { SrdoCount = 1 });
        AddApplicationObjects(node.ObjectDictionary);
        var safety = node.Safety();
        safety.ConfigureSrdoProducer(1, new SrdoMapping().Add(0x2001, 0x00, 8), TimeSpan.FromMilliseconds(25));
        safety.CommitSafetyConfiguration();
        wire.SendNmt(NmtCommand.Start, Producer);
        await WaitUntilAsync(() => node.State == NmtState.Operational, "start");
        Assert.Throws<InvalidOperationException>(() => safety.ConfigureSrdoProducer(1, new SrdoMapping(), TimeSpan.FromMilliseconds(25)));
        Assert.Throws<InvalidOperationException>(() => safety.CommitSafetyConfiguration());
        Assert.Throws<InvalidOperationException>(() => safety.DeleteSrdo(1));
        var ex = Assert.Throws<ArgumentException>(() => node.ObjectDictionary.WriteUnsigned(0x1301, 2, 30));
        ex.Message.Should().Contain("0x08000022");
    }

    [Fact]
    public async Task Producer_And_Consumer_Exchange_An_Srdo()
    {
        var session = NewSession();
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var wire = new Wire(session, 3);
        var clockA = new ManualTimeSource();
        var clockB = new ManualTimeSource();
        using var producer = OpenClocked(busA, Producer, clockA);
        using var consumer = OpenClocked(busB, Consumer, clockB);
        AddApplicationObjects(producer.ObjectDictionary);
        consumer.ObjectDictionary.AddU16(0x3000, 0x00, 0);
        consumer.ObjectDictionary.AddU8(0x3001, 0x00, 0);
        producer.Safety().ConfigureSrdoProducer(1, new SrdoMapping().Add(0x2000, 0x00, 16).Add(0x2001, 0x00, 8), TimeSpan.FromMilliseconds(25));
        producer.Safety().CommitSafetyConfiguration();
        var received = new List<SrdoReceivedEventArgs>();
        consumer.Safety().SrdoReceived += (_, e) => { lock (received) received.Add(e); };
        consumer.Safety().ConfigureSrdoConsumer(1, new SrdoMapping().Add(0x3000, 0x00, 16).Add(0x3001, 0x00, 8),
            TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(20), CanOpenCobId.SrdoDefaultCobId1(Producer), CanOpenCobId.SrdoDefaultCobId2(Producer));
        consumer.Safety().CommitSafetyConfiguration();
        wire.SendNmt(NmtCommand.Start, 0);
        await WaitUntilAsync(() => producer.State == NmtState.Operational && consumer.State == NmtState.Operational, "start");
        Advance(clockA, producer, TimeSpan.FromMilliseconds(Producer));
        await WaitUntilAsync(() => { lock (received) return received.Count >= 1; }, "SRDO received");
        consumer.Safety().GetSrdoState(1).IsValid.Should().BeTrue();
        consumer.ObjectDictionary.ReadUnsigned(0x3000, 0).Should().Be(0x1234u);
        consumer.ObjectDictionary.ReadUnsigned(0x3001, 0).Should().Be(0x5Au);
        received[0].CobId.Should().Be(CanOpenCobId.SrdoDefaultCobId1(Producer));
        // Change of state: the application writes a mapped object, the producer transmits at once
        // (or, if the first pair is still in flight, as soon as it is confirmed).
        producer.ObjectDictionary.WriteUnsigned(0x2001, 0, 0x07);
        await WaitUntilAsync(() => { lock (received) return received.Count >= 2; }, "CoS SRDO");
        consumer.ObjectDictionary.ReadUnsigned(0x3001, 0).Should().Be(0x07u);
        // The consumer's SCT runs on its own clock: 50 ms without a pair invalidates it.
        Advance(clockB, consumer, TimeSpan.FromMilliseconds(50));
        consumer.Safety().GetSrdoState(1).IsValid.Should().BeFalse();
        consumer.Safety().GetSrdoState(1).Reason.Should().Be(SrdoInvalidReason.SafeguardCycleExpired);
    }

    [Fact]
    public async Task Srdo_Change_Of_State_Does_Not_Trigger_A_Tpdo_When_Tpdo_CoS_Is_Off()
    {
        var session = NewSession();
        using var bus = Open(session, 1);
        using var wire = new Wire(session, 2);
        var clock = new ManualTimeSource();
        using var node = new CanOpenNode(new CanBusService(bus), Producer,
            new CanOpenNodeOptions { SrdoCount = 1, EnableChangeOfStateTpdo = false }, ownsService: true, clock);
        AddApplicationObjects(node.ObjectDictionary);
        // One object, mapped in an event-driven TPDO and in a transmit SRDO.
        node.ConfigureTpdo(1, new PdoMapping().Add(0x2001, 0x00, 8), TpdoTransmission.EventDriven);
        node.Safety().ConfigureSrdoProducer(1, new SrdoMapping().Add(0x2001, 0x00, 8), TimeSpan.FromMilliseconds(25));
        node.Safety().CommitSafetyConfiguration();
        wire.SendNmt(NmtCommand.Start, Producer);
        await WaitUntilAsync(() => node.State == NmtState.Operational, "start");
        uint tpdo1 = 0x180u + Producer;
        uint srdo1 = 0x0FFu + 2 * Producer;
        node.ObjectDictionary.WriteUnsigned(0x2001, 0, 0x07);
        await wire.WaitForCountAsync(srdo1, 1); // the SRDO's change of state still runs
        // The witness: a TPDO the application requests afterwards, through the same send path.
        await node.TriggerTpdoAsync(1);
        await wire.WaitForCountAsync(tpdo1, 1);
        wire.Count(tpdo1).Should().Be(1, "EnableChangeOfStateTpdo = false: the write is no TPDO event");
    }

    /// <summary>§8.1: "the redundant transmission is sent after the first transmission"; §9.5:
    /// the consumer takes the pair "in chronological order". Many pairs, so that the two frames
    /// of a pair racing each other would put an inverted frame first somewhere among them.</summary>
    [Fact]
    public async Task Pairs_Reach_The_Consumer_In_Order()
    {
        var session = NewSession();
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var wire = new Wire(session, 3);
        using var producer = OpenClocked(busA, Producer, new ManualTimeSource());
        using var consumer = OpenClocked(busB, Consumer, new ManualTimeSource());
        producer.ObjectDictionary.AddU8(0x2001, 0x00, 0);
        consumer.ObjectDictionary.AddU8(0x3001, 0x00, 0);
        producer.Safety().ConfigureSrdoProducer(1, new SrdoMapping().Add(0x2001, 0x00, 8), TimeSpan.FromMilliseconds(25));
        producer.Safety().CommitSafetyConfiguration();
        consumer.Safety().ConfigureSrdoConsumer(1, new SrdoMapping().Add(0x3001, 0x00, 8),
            TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(20), CanOpenCobId.SrdoDefaultCobId1(Producer), CanOpenCobId.SrdoDefaultCobId2(Producer));
        consumer.Safety().CommitSafetyConfiguration();
        var failures = new List<SrdoInvalidReason>();
        consumer.Safety().SrdoStateChanged += (_, e) =>
        {
            if (e.Reason is SrdoInvalidReason.OutOfOrder or SrdoInvalidReason.Mismatch) lock (failures) failures.Add(e.Reason.Value);
        };
        var last = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        consumer.Safety().SrdoReceived += (_, e) => { if (e.Payload.Length == 1 && e.Payload[0] == 0xEE) last.TrySetResult(true); };
        wire.SendNmt(NmtCommand.Start, 0);
        await WaitUntilAsync(() => producer.State == NmtState.Operational && consumer.State == NmtState.Operational, "start");

        // One pair at a time: pairs due while one is in flight coalesce into one pending pair by
        // design, and back-to-back triggers would put only a handful of pairs on the bus. The test
        // needs all 200 there, so each trigger waits for the one before it to be confirmed.
        var producerNode = (CanOpenNode)producer;
        for (int i = 0; i < 200; i++)
        {
            await producer.Safety().TriggerSrdoAsync(1);
            await AwaitSrdoChainAsync(producerNode);
            await WaitUntilAsync(() => !SrdoPairInFlight(producerNode), "in-flight flag cleared");
        }
        // The last pair, by change of state: delivered after every event the earlier ones raised.
        producer.ObjectDictionary.WriteUnsigned(0x2001, 0x00, 0xEE);
        await last.Task.WithTimeoutAsync(ShortTimeout);
        lock (failures) failures.Should().BeEmpty();
    }

    /// <summary>A bus whose confirmations stall: at most one pair per SRDO is in flight, the pairs
    /// due meanwhile coalesce into one pending pair carrying the latest data, which goes out as
    /// soon as the in-flight pair is confirmed; then the refresh cycle goes on.</summary>
    [Fact]
    public async Task Pairs_Due_While_One_Is_Unconfirmed_Coalesce_Into_The_Latest()
    {
        var session = NewSession();
        using var bus = Open(session, 1);
        using var wire = new Wire(session, 2);
        var clock = new ManualTimeSource();
        var service = new HoldingService(new CanBusService(bus));
        // Change of state off: the write below changes the data, only the cycles transmit.
        using var node = new CanOpenNode(service, Producer,
            new CanOpenNodeOptions { SrdoCount = 1, EnableChangeOfStateSrdo = false }, ownsService: true, clock);
        AddApplicationObjects(node.ObjectDictionary);   // 2001h = 5Ah
        node.Safety().ConfigureSrdoProducer(1, new SrdoMapping().Add(0x2001, 0x00, 8), TimeSpan.FromMilliseconds(25));
        node.Safety().CommitSafetyConfiguration();
        uint cob1 = 0x0FFu + 2 * Producer;
        uint cob2 = cob1 + 1;
        wire.SendNmt(NmtCommand.Start, Producer);
        await WaitUntilAsync(() => node.State == NmtState.Operational, "start");

        Advance(clock, node, TimeSpan.FromMilliseconds(Producer));          // first pair at 8.5 ms, held
        await WaitUntilAsync(() => service.Handed(cob1) == 1, "first frame handed to the service");
        Advance(clock, node, TimeSpan.FromMilliseconds(25));                // cycle at 33.5 ms
        Advance(clock, node, TimeSpan.FromMilliseconds(25));                // cycle at 58.5 ms
        node.ObjectDictionary.WriteUnsigned(0x2001, 0x00, 0x77);
        Advance(clock, node, TimeSpan.FromMilliseconds(25));                // cycle at 83.5 ms, with 77h
        service.Handed(cob1).Should().Be(1, "only the held pair's plain frame has reached the service; none of the three pairs due meanwhile has");
        service.Handed(cob2).Should().Be(0, "the inverted frame follows the plain one's confirmation");

        service.Release();
        await WaitUntilAsync(() => service.Handed(cob2) >= 2, "the pending pair sent after the held one");
        await WaitUntilAsync(() => !SrdoPairInFlight(node), "nothing in flight");
        service.Handed(cob1).Should().Be(2, "exactly one more pair: the three due ones coalesced into one");
        service.Payloads(cob1)[1].Should().Equal(new byte[] { 0x77 }, "the pending pair carries the latest data");
        service.Payloads(cob2)[1].Should().Equal(new byte[] { 0x88 });

        Advance(clock, node, TimeSpan.FromMilliseconds(25));                // cycle at 108.5 ms
        await AwaitSrdoChainAsync(node);
        await WaitUntilAsync(() => !SrdoPairInFlight(node), "nothing in flight");
        service.Handed(cob1).Should().Be(3, "the refresh cycle goes on");
        service.Handed(cob2).Should().Be(3);
    }

    /// <summary>§8.3.2.2: a pair pending behind an unconfirmed one does not reach the bus once the
    /// node has left Operational.</summary>
    [Fact]
    public async Task A_Pending_Pair_Is_Dropped_When_The_Node_Leaves_Operational()
    {
        var session = NewSession();
        using var bus = Open(session, 1);
        using var wire = new Wire(session, 2);
        var clock = new ManualTimeSource();
        var service = new HoldingService(new CanBusService(bus));
        using var node = new CanOpenNode(service, Producer, new CanOpenNodeOptions { SrdoCount = 1 }, ownsService: true, clock);
        AddApplicationObjects(node.ObjectDictionary);
        node.Safety().ConfigureSrdoProducer(1, new SrdoMapping().Add(0x2001, 0x00, 8), TimeSpan.FromMilliseconds(25));
        node.Safety().CommitSafetyConfiguration();
        uint cob1 = 0x0FFu + 2 * Producer;
        uint cob2 = cob1 + 1;
        wire.SendNmt(NmtCommand.Start, Producer);
        await WaitUntilAsync(() => node.State == NmtState.Operational, "start");
        Advance(clock, node, TimeSpan.FromMilliseconds(Producer));          // first pair, held
        await WaitUntilAsync(() => service.Handed(cob1) == 1, "first frame handed to the service");
        Advance(clock, node, TimeSpan.FromMilliseconds(25));                // a second pair pending

        wire.SendNmt(NmtCommand.EnterPreOperational, Producer);
        await WaitUntilAsync(() => node.State == NmtState.PreOperational, "pre-operational");
        service.Release();
        // The held pair's link has ended once nothing is in flight: its completion clears the flag.
        await WaitUntilAsync(() => !SrdoPairInFlight(node), "nothing in flight");
        service.Handed(cob1).Should().Be(1, "the pending pair was dropped with the transition");
        service.Handed(cob2).Should().Be(0,
            "the inverted half of the held pair is not sent after the transition either: a consumer timing out on its SRVT is safer than one refreshing its SCT on a stale pair");
    }

    /// <summary>§8.3.2.2: the pairs that came due in Operational but have not reached the bus yet
    /// — another SRDO's pair queued behind the held one on the send chain — stay off it once the
    /// node has left Operational.</summary>
    [Fact]
    public async Task A_Queued_Pair_Of_Another_Srdo_Does_Not_Reach_The_Bus_After_Leaving_Operational()
    {
        var session = NewSession();
        using var bus = Open(session, 1);
        using var wire = new Wire(session, 2);
        var clock = new ManualTimeSource();
        var service = new HoldingService(new CanBusService(bus));
        using var node = new CanOpenNode(service, Producer, new CanOpenNodeOptions { SrdoCount = 2 }, ownsService: true, clock);
        AddApplicationObjects(node.ObjectDictionary);
        node.Safety().ConfigureSrdoProducer(1, new SrdoMapping().Add(0x2001, 0x00, 8), TimeSpan.FromMilliseconds(25));
        node.Safety().ConfigureSrdoProducer(2, new SrdoMapping().Add(0x2001, 0x00, 8), TimeSpan.FromMilliseconds(25), 0x141, 0x142);
        node.Safety().CommitSafetyConfiguration();
        uint cob1 = 0x0FFu + 2 * Producer;
        wire.SendNmt(NmtCommand.Start, Producer);
        await WaitUntilAsync(() => node.State == NmtState.Operational, "start");
        Advance(clock, node, TimeSpan.FromMilliseconds(Producer));          // both first pairs due: SRDO 1's held, SRDO 2's queued behind it
        await WaitUntilAsync(() => service.Handed(cob1) == 1, "first frame handed to the service");
        SrdoPairInFlight(node, 2).Should().BeTrue("SRDO 2's pair is on the send chain");
        service.Handed(0x141).Should().Be(0, "it waits behind SRDO 1's pair");

        wire.SendNmt(NmtCommand.Stop, Producer);
        await WaitUntilAsync(() => node.State == NmtState.Stopped, "stopped");
        service.Release();
        // Both links have ended once neither SRDO is in flight: each completion clears its flag.
        await WaitUntilAsync(() => !SrdoPairInFlight(node, 1) && !SrdoPairInFlight(node, 2), "nothing in flight");
        service.Handed(0x141).Should().Be(0, "SRDO 2's pair came due in Operational but must not reach the bus after NMT Stop (§8.3.2.2)");
        service.Handed(0x142).Should().Be(0);
        service.Handed(cob1 + 1).Should().Be(0, "nor the inverted half of the held pair");
    }

    /// <summary>The check before each frame cannot see a thread that has passed it and is still
    /// on its way to the service. Each frame of a pair is therefore handed over with the token of
    /// its Operational period, which leaving Operational cancels: a send whose task has not started
    /// is not started, and the inverted frame of a pair in flight is not handed over.</summary>
    [Fact]
    public async Task Leaving_Operational_Cancels_The_Send_Of_A_Pair_In_Flight()
    {
        var session = NewSession();
        using var bus = Open(session, 1);
        using var wire = new Wire(session, 2);
        var clock = new ManualTimeSource();
        var service = new HoldingService(new CanBusService(bus));
        using var node = new CanOpenNode(service, Producer, new CanOpenNodeOptions { SrdoCount = 1 }, ownsService: true, clock);
        AddApplicationObjects(node.ObjectDictionary);
        node.Safety().ConfigureSrdoProducer(1, new SrdoMapping().Add(0x2001, 0x00, 8), TimeSpan.FromMilliseconds(25));
        node.Safety().CommitSafetyConfiguration();
        uint cob1 = 0x0FFu + 2 * Producer;
        wire.SendNmt(NmtCommand.Start, Producer);
        await WaitUntilAsync(() => node.State == NmtState.Operational, "start");
        Advance(clock, node, TimeSpan.FromMilliseconds(Producer));          // the first pair, held at its plain frame
        await WaitUntilAsync(() => service.Handed(cob1) == 1, "first frame handed to the service");
        var token = service.TokenOf(cob1, 0);
        token.CanBeCanceled.Should().BeTrue("the frame carries its period's token");
        token.IsCancellationRequested.Should().BeFalse();

        wire.SendNmt(NmtCommand.Stop, Producer);
        // State is read through the actor, behind the turn that applied the Stop.
        await WaitUntilAsync(() => node.State == NmtState.Stopped, "stopped");
        token.IsCancellationRequested.Should().BeTrue("leaving Operational cancels the period's sends");
        service.Release();
        await WaitUntilAsync(() => !SrdoPairInFlight(node), "the link has ended");
        service.Handed(cob1 + 1).Should().Be(0, "the inverted frame is not handed over");
    }

    /// <summary>Disposing the node cancels the period's sends as leaving Operational does.</summary>
    [Fact]
    public async Task Disposing_The_Node_Cancels_The_Send_Of_A_Pair_In_Flight()
    {
        var session = NewSession();
        using var bus = Open(session, 1);
        var clock = new ManualTimeSource();
        using var service = new HoldingService(new CanBusService(bus));
        var node = new CanOpenNode(service, Producer, new CanOpenNodeOptions { SrdoCount = 1 }, ownsService: false, clock);
        AddApplicationObjects(node.ObjectDictionary);
        node.Safety().ConfigureSrdoProducer(1, new SrdoMapping().Add(0x2001, 0x00, 8), TimeSpan.FromMilliseconds(25));
        node.Safety().CommitSafetyConfiguration();
        uint cob1 = 0x0FFu + 2 * Producer;
        using (var wire = new Wire(session, 2))
        {
            wire.SendNmt(NmtCommand.Start, Producer);
            await WaitUntilAsync(() => node.State == NmtState.Operational, "start");
        }
        Advance(clock, node, TimeSpan.FromMilliseconds(Producer));
        await WaitUntilAsync(() => service.Handed(cob1) == 1, "first frame handed to the service");
        var token = service.TokenOf(cob1, 0);
        token.IsCancellationRequested.Should().BeFalse();
        Task chain = Task.CompletedTask;
        await node.PostToActorAsync(() => chain = node.SrdoSendChainForTests);

        node.Dispose(); // returns after the actor ran its cleanup
        token.IsCancellationRequested.Should().BeTrue("disposing cancels the period's sends");
        service.Release();
        await chain.WithTimeoutAsync(ShortTimeout);
        service.Handed(cob1 + 1).Should().Be(0);
    }

    /// <summary>Each Operational period has a token of its own: a pair of the next period is
    /// handed over with one that is not cancelled, and goes out.</summary>
    [Fact]
    public async Task A_Pair_Of_The_Next_Operational_Period_Is_Not_Cancelled()
    {
        var session = NewSession();
        using var bus = Open(session, 1);
        using var wire = new Wire(session, 2);
        var clock = new ManualTimeSource();
        var service = new HoldingService(new CanBusService(bus));
        using var node = new CanOpenNode(service, Producer, new CanOpenNodeOptions { SrdoCount = 1 }, ownsService: true, clock);
        AddApplicationObjects(node.ObjectDictionary);
        node.Safety().ConfigureSrdoProducer(1, new SrdoMapping().Add(0x2001, 0x00, 8), TimeSpan.FromMilliseconds(25));
        node.Safety().CommitSafetyConfiguration();
        uint cob1 = 0x0FFu + 2 * Producer;
        wire.SendNmt(NmtCommand.Start, Producer);
        await WaitUntilAsync(() => node.State == NmtState.Operational, "start");
        Advance(clock, node, TimeSpan.FromMilliseconds(Producer));
        await WaitUntilAsync(() => service.Handed(cob1) == 1, "first frame handed to the service");
        wire.SendNmt(NmtCommand.Stop, Producer);
        await WaitUntilAsync(() => node.State == NmtState.Stopped, "stopped");
        service.Release();
        await WaitUntilAsync(() => !SrdoPairInFlight(node), "the first period's link has ended");

        wire.SendNmt(NmtCommand.Start, Producer);
        await WaitUntilAsync(() => node.State == NmtState.Operational, "started again");
        Advance(clock, node, TimeSpan.FromMilliseconds(Producer));          // the new period's first pair
        await WaitUntilAsync(() => service.Handed(cob1 + 1) >= 1, "the new pair went out");
        service.TokenOf(cob1, 0).IsCancellationRequested.Should().BeTrue();
        service.TokenOf(cob1, 1).IsCancellationRequested.Should().BeFalse("a fresh period, not the old one");
        service.TokenOf(cob1 + 1, 0).IsCancellationRequested.Should().BeFalse();
    }

    /// <summary>A node with SRDO 1 producing, 1300h = 1, Operational: the GFC can be sent.</summary>
    private static async Task<CanOpenNode> OpenGfcNodeAsync(HoldingService service, Wire wire, bool ownsService = true)
    {
        var node = new CanOpenNode(service, Producer, new CanOpenNodeOptions { SrdoCount = 1 }, ownsService, new ManualTimeSource());
        AddApplicationObjects(node.ObjectDictionary);
        node.Safety().ConfigureSrdoProducer(1, new SrdoMapping().Add(0x2001, 0x00, 8), TimeSpan.FromMilliseconds(25));
        node.Safety().CommitSafetyConfiguration();
        node.ObjectDictionary.WriteUnsigned(0x1300, 0, 1);
        wire.SendNmt(NmtCommand.Start, Producer);
        await WaitUntilAsync(() => node.State == NmtState.Operational, "start");
        return node;
    }

    /// <summary>§8.2: the GFC is sent only in Operational. It is handed over with the token of
    /// the Operational period, like an SRDO frame, so leaving Operational cancels a GFC send that
    /// has not started yet.</summary>
    [Fact]
    public async Task Leaving_Operational_Cancels_A_Gfc_Send_Not_Yet_Started()
    {
        var session = NewSession();
        using var bus = Open(session, 1);
        using var wire = new Wire(session, 2);
        var service = new HoldingService(new CanBusService(bus), holdGfc: true);
        using var node = await OpenGfcNodeAsync(service, wire);
        var call = node.Safety().SendGlobalFailsafeCommandAsync();
        await WaitUntilAsync(() => service.Handed(CanOpenCobId.GlobalFailsafeCommand) == 1, "GFC handed to the service");
        var token = service.TokenOf(CanOpenCobId.GlobalFailsafeCommand, 0);
        token.CanBeCanceled.Should().BeTrue("the GFC carries its period's token");
        token.IsCancellationRequested.Should().BeFalse();

        wire.SendNmt(NmtCommand.Stop, Producer);
        await WaitUntilAsync(() => node.State == NmtState.Stopped, "stopped"); // through the actor, behind the Stop
        token.IsCancellationRequested.Should().BeTrue("leaving Operational cancels the period's sends, the GFC's included");
        service.Release();
        await call.WithTimeoutAsync(ShortTimeout);
    }

    /// <summary>Disposing the node cancels a GFC send as leaving Operational does.</summary>
    [Fact]
    public async Task Disposing_The_Node_Cancels_A_Gfc_Send_Not_Yet_Started()
    {
        var session = NewSession();
        using var bus = Open(session, 1);
        using var service = new HoldingService(new CanBusService(bus), holdGfc: true);
        CancellationToken token;
        using (var wire = new Wire(session, 2))
        {
            var node = await OpenGfcNodeAsync(service, wire, ownsService: false);
            _ = node.Safety().SendGlobalFailsafeCommandAsync(); // completes when the service lets the held frame go
            await WaitUntilAsync(() => service.Handed(CanOpenCobId.GlobalFailsafeCommand) == 1, "GFC handed to the service");
            token = service.TokenOf(CanOpenCobId.GlobalFailsafeCommand, 0);
            token.IsCancellationRequested.Should().BeFalse();
            node.Dispose(); // returns after the actor ran its cleanup
        }
        token.IsCancellationRequested.Should().BeTrue("disposing cancels the period's sends, the GFC's included");
        service.Release();
    }

    /// <summary>A GFC of the next Operational period carries a token that is not cancelled.</summary>
    [Fact]
    public async Task A_Gfc_Of_The_Next_Operational_Period_Is_Not_Cancelled()
    {
        var session = NewSession();
        using var bus = Open(session, 1);
        using var wire = new Wire(session, 2);
        var service = new HoldingService(new CanBusService(bus), holdGfc: true);
        using var node = await OpenGfcNodeAsync(service, wire);
        var first = node.Safety().SendGlobalFailsafeCommandAsync();
        await WaitUntilAsync(() => service.Handed(CanOpenCobId.GlobalFailsafeCommand) == 1, "first GFC handed to the service");
        wire.SendNmt(NmtCommand.Stop, Producer);
        await WaitUntilAsync(() => node.State == NmtState.Stopped, "stopped");
        service.Release();
        await first.WithTimeoutAsync(ShortTimeout);

        wire.SendNmt(NmtCommand.Start, Producer);
        await WaitUntilAsync(() => node.State == NmtState.Operational, "started again");
        await node.Safety().SendGlobalFailsafeCommandAsync();
        await WaitUntilAsync(() => service.Handed(CanOpenCobId.GlobalFailsafeCommand) == 2, "second GFC handed to the service");
        service.TokenOf(CanOpenCobId.GlobalFailsafeCommand, 0).IsCancellationRequested.Should().BeTrue();
        service.TokenOf(CanOpenCobId.GlobalFailsafeCommand, 1).IsCancellationRequested.Should().BeFalse("a fresh period");
    }

    /// <summary>A GFC send the period's token cancels ends quietly: no background exception, and
    /// the call completes rather than ending cancelled — the caller did not ask for it.</summary>
    [Fact]
    public async Task A_Gfc_Send_Cancelled_By_Its_Period_Ends_Quietly()
    {
        var session = NewSession();
        using var bus = Open(session, 1);
        using var wire = new Wire(session, 2);
        var service = new HoldingService(new CanBusService(bus), holdGfc: true, honourTokens: true);
        using var node = await OpenGfcNodeAsync(service, wire);
        var background = new List<Exception>();
        node.BackgroundExceptionOccurred += (_, ex) => { lock (background) background.Add(ex); };
        var send = node.Safety().SendGlobalFailsafeCommandAsync();
        await WaitUntilAsync(() => service.Handed(CanOpenCobId.GlobalFailsafeCommand) == 1, "GFC handed to the service");

        wire.SendNmt(NmtCommand.Stop, Producer);
        await WaitUntilAsync(() => node.State == NmtState.Stopped, "stopped");
        await send.WithTimeoutAsync(ShortTimeout).ContinueWith(_ => { }); // ended, however
        send.Status.Should().Be(TaskStatus.RanToCompletion, "the period's own cancellation is no failure");
        lock (background) background.Should().BeEmpty();
    }

    /// <summary>The call is the send: it completes when the frame was handed over, not when the
    /// actor's turn ended — while the service holds the GFC the call is still running.</summary>
    [Fact]
    public async Task The_Gfc_Call_Completes_When_The_Frame_Was_Handed_Over()
    {
        var session = NewSession();
        using var bus = Open(session, 1);
        using var wire = new Wire(session, 2);
        var service = new HoldingService(new CanBusService(bus), holdGfc: true);
        using var node = await OpenGfcNodeAsync(service, wire);
        var call = node.Safety().SendGlobalFailsafeCommandAsync();
        await WaitUntilAsync(() => service.Handed(CanOpenCobId.GlobalFailsafeCommand) == 1, "GFC handed to the service");
        await node.PostToActorAsync(() => { }); // the actor's turn that sent it is long over
        call.IsCompleted.Should().BeFalse("the service still holds the frame");
        service.TokenOf(CanOpenCobId.GlobalFailsafeCommand, 0).IsCancellationRequested.Should().BeFalse("nobody cancelled");

        service.Release();
        await call.WithTimeoutAsync(ShortTimeout);
        call.Status.Should().Be(TaskStatus.RanToCompletion);
    }

    /// <summary>The caller's token reaches the send, linked with the Operational period's: a
    /// caller that cancels while the service holds the GFC cancels the send, and the call ends
    /// with OperationCanceledException — the caller asked for it — and no background exception.</summary>
    [Fact]
    public async Task Cancelling_The_Callers_Token_Cancels_The_Gfc_Send()
    {
        var session = NewSession();
        using var bus = Open(session, 1);
        using var wire = new Wire(session, 2);
        var service = new HoldingService(new CanBusService(bus), holdGfc: true, honourTokens: true);
        using var node = await OpenGfcNodeAsync(service, wire);
        var background = new List<Exception>();
        node.BackgroundExceptionOccurred += (_, ex) => { lock (background) background.Add(ex); };
        using var cts = new CancellationTokenSource();
        var call = node.Safety().SendGlobalFailsafeCommandAsync(cts.Token);
        await WaitUntilAsync(() => service.Handed(CanOpenCobId.GlobalFailsafeCommand) == 1, "GFC handed to the service");
        var token = service.TokenOf(CanOpenCobId.GlobalFailsafeCommand, 0);
        token.IsCancellationRequested.Should().BeFalse();

        cts.Cancel();
        token.IsCancellationRequested.Should().BeTrue("the send's token is linked with the caller's");
        await call.Invoking(c => c.WithTimeoutAsync(ShortTimeout)).Should().ThrowAsync<OperationCanceledException>();
        node.State.Should().Be(NmtState.Operational, "the period goes on");
        lock (background) background.Should().BeEmpty();
    }

    /// <summary>A caller cancelled before the actor's turn gets OperationCanceledException and no
    /// GFC is handed over.</summary>
    [Fact]
    public async Task A_Gfc_Call_Cancelled_Before_It_Runs_Sends_Nothing()
    {
        var session = NewSession();
        using var bus = Open(session, 1);
        using var wire = new Wire(session, 2);
        var service = new HoldingService(new CanBusService(bus), holdGfc: true);
        using var node = await OpenGfcNodeAsync(service, wire);
        await node.Safety().Invoking(s => s.SendGlobalFailsafeCommandAsync(new CancellationToken(true)))
            .Should().ThrowAsync<OperationCanceledException>();
        await node.PostToActorAsync(() => { });
        service.Handed(CanOpenCobId.GlobalFailsafeCommand).Should().Be(0);
    }

    /// <summary>Disposing the node ends the links on the send chain as leaving Operational does:
    /// neither the held pair's inverted half nor a pair queued behind it reaches the service.</summary>
    [Fact]
    public async Task Disposing_The_Node_Ends_The_Pairs_Still_On_The_Send_Chain()
    {
        var session = NewSession();
        using var bus = Open(session, 1);
        var clock = new ManualTimeSource();
        // The service outlives the node, so what the links would still send after the disposal reaches it.
        using var service = new HoldingService(new CanBusService(bus));
        var node = new CanOpenNode(service, Producer, new CanOpenNodeOptions { SrdoCount = 2 }, ownsService: false, clock);
        AddApplicationObjects(node.ObjectDictionary);
        node.Safety().ConfigureSrdoProducer(1, new SrdoMapping().Add(0x2001, 0x00, 8), TimeSpan.FromMilliseconds(25));
        node.Safety().ConfigureSrdoProducer(2, new SrdoMapping().Add(0x2001, 0x00, 8), TimeSpan.FromMilliseconds(25), 0x141, 0x142);
        node.Safety().CommitSafetyConfiguration();
        uint cob1 = 0x0FFu + 2 * Producer;
        using (var wire = new Wire(session, 2))
        {
            wire.SendNmt(NmtCommand.Start, Producer);
            await WaitUntilAsync(() => node.State == NmtState.Operational, "start");
        }
        Advance(clock, node, TimeSpan.FromMilliseconds(Producer));          // SRDO 1's pair held, SRDO 2's queued behind it
        await WaitUntilAsync(() => service.Handed(cob1) == 1, "first frame handed to the service");
        SrdoPairInFlight(node, 2).Should().BeTrue("SRDO 2's pair is on the send chain");
        Task chain = Task.CompletedTask;
        await node.PostToActorAsync(() => chain = node.SrdoSendChainForTests);

        node.Dispose();
        service.Release();
        await chain.WithTimeoutAsync(ShortTimeout);                         // both links have ended
        service.Handed(cob1 + 1).Should().Be(0, "the held pair's inverted half is not sent by a disposed node");
        service.Handed(0x141).Should().Be(0, "nor the pair queued behind it");
        service.Handed(0x142).Should().Be(0);
    }

    /// <summary>A pair pending when the node leaves Operational is dropped then, not merely held
    /// back while the node is not Operational: after a Stop and a new Start it does not go out
    /// with the data it had before the Stop.</summary>
    [Fact]
    public async Task A_Pair_Pending_Before_A_Stop_Is_Not_Sent_After_The_Next_Start()
    {
        var session = NewSession();
        using var bus = Open(session, 1);
        using var wire = new Wire(session, 2);
        var clock = new ManualTimeSource();
        var service = new HoldingService(new CanBusService(bus));
        // Change of state off: the writes below change the data, only the cycles transmit.
        using var node = new CanOpenNode(service, Producer,
            new CanOpenNodeOptions { SrdoCount = 1, EnableChangeOfStateSrdo = false }, ownsService: true, clock);
        AddApplicationObjects(node.ObjectDictionary);   // 2001h = 5Ah
        node.Safety().ConfigureSrdoProducer(1, new SrdoMapping().Add(0x2001, 0x00, 8), TimeSpan.FromMilliseconds(25));
        node.Safety().CommitSafetyConfiguration();
        uint cob1 = 0x0FFu + 2 * Producer;
        wire.SendNmt(NmtCommand.Start, Producer);
        await WaitUntilAsync(() => node.State == NmtState.Operational, "start");
        Advance(clock, node, TimeSpan.FromMilliseconds(Producer));          // first pair at 8.5 ms, held
        await WaitUntilAsync(() => service.Handed(cob1) == 1, "first frame handed to the service");
        node.ObjectDictionary.WriteUnsigned(0x2001, 0x00, 0x66);
        Advance(clock, node, TimeSpan.FromMilliseconds(25));                // a pair with 66h pending

        wire.SendNmt(NmtCommand.Stop, Producer);
        await WaitUntilAsync(() => node.State == NmtState.Stopped, "stopped");
        node.ObjectDictionary.WriteUnsigned(0x2001, 0x00, 0x77);
        wire.SendNmt(NmtCommand.Start, Producer);
        await WaitUntilAsync(() => node.State == NmtState.Operational, "started again");
        service.Release();                                                  // before the new first cycle is due
        await WaitUntilAsync(() => !SrdoPairInFlight(node), "nothing in flight");
        service.Handed(cob1).Should().Be(1, "the pair pending before the Stop was dropped with it");

        Advance(clock, node, TimeSpan.FromMilliseconds(Producer));          // the new period's first pair
        await WaitUntilAsync(() => service.Handed(cob1 + 1) >= 1, "the new first pair sent");
        await WaitUntilAsync(() => !SrdoPairInFlight(node), "nothing in flight");
        service.Payloads(cob1).Should().BeEquivalentTo(new[] { new byte[] { 0x5A }, new byte[] { 0x77 } },
            options => options.WithStrictOrdering(), "no pair carries the 66h of before the Stop");
    }

    /// <summary>The GFC does not wait behind an SRDO pair whose confirmation is outstanding.</summary>
    [Fact]
    public async Task The_Gfc_Does_Not_Wait_For_An_Unconfirmed_Pair()
    {
        var session = NewSession();
        using var bus = Open(session, 1);
        using var wire = new Wire(session, 2);
        var clock = new ManualTimeSource();
        var service = new HoldingService(new CanBusService(bus));
        using var node = new CanOpenNode(service, Producer, new CanOpenNodeOptions { SrdoCount = 1 }, ownsService: true, clock);
        AddApplicationObjects(node.ObjectDictionary);
        node.Safety().ConfigureSrdoProducer(1, new SrdoMapping().Add(0x2001, 0x00, 8), TimeSpan.FromMilliseconds(25));
        node.Safety().CommitSafetyConfiguration();
        node.ObjectDictionary.WriteUnsigned(0x1300, 0, 1);
        uint cob1 = 0x0FFu + 2 * Producer;
        wire.SendNmt(NmtCommand.Start, Producer);
        await WaitUntilAsync(() => node.State == NmtState.Operational, "start");
        Advance(clock, node, TimeSpan.FromMilliseconds(Producer));
        await WaitUntilAsync(() => service.Handed(cob1) == 1, "first frame handed to the service");

        await node.Safety().SendGlobalFailsafeCommandAsync();
        await WaitUntilAsync(() => service.Handed(CanOpenCobId.GlobalFailsafeCommand) == 1, "GFC handed to the service");
        service.Handed(cob1 + 1).Should().Be(0, "the pair is still unconfirmed");
        service.Release();
    }

    /// <summary>A disposed node has no SRDO running: its state is not reported as the last
    /// snapshot, which would still say valid, but refused as every other member is.</summary>
    [Fact]
    public async Task The_State_Of_A_Disposed_Node_Is_Refused()
    {
        var session = NewSession();
        using var bus = Open(session, 1);
        using var wire = new Wire(session, 2);
        var node = new CanOpenNode(new CanBusService(bus), Producer, new CanOpenNodeOptions { SrdoCount = 1 }, ownsService: true, new ManualTimeSource());
        AddApplicationObjects(node.ObjectDictionary);
        var safety = node.Safety();
        safety.ConfigureSrdoProducer(1, new SrdoMapping().Add(0x2001, 0x00, 8), TimeSpan.FromMilliseconds(25));
        safety.CommitSafetyConfiguration();
        wire.SendNmt(NmtCommand.Start, Producer);
        await WaitUntilAsync(() => node.State == NmtState.Operational, "start");
        safety.GetSrdoState(1).IsValid.Should().BeTrue();

        node.Dispose();
        safety.Invoking(s => s.GetSrdoState(1)).Should().Throw<ObjectDisposedException>();
    }

    /// <summary>An object an existing SRDO maps — producer or consumer — cannot be re-declared:
    /// the CRC covers the mapping entry, not the object's declaration, so a new width or access
    /// would reach the bus or the application unchecked. §8.4.2.2 (1381h–13C0h): "For changing
    /// the SRDO mapping first the SRDO shall be deleted" — once it is, the object can be re-declared,
    /// because creating the SRDO again checks every mapped object anew. An object no SRDO maps
    /// stays re-declarable.</summary>
    [Fact]
    public void An_Object_An_Existing_Srdo_Maps_Cannot_Be_Redeclared()
    {
        var session = NewSession();
        using var bus = Open(session, 1);
        using var node = OpenClocked(bus, Producer, new ManualTimeSource(), srdoCount: 2);
        var od = node.ObjectDictionary;
        AddApplicationObjects(od);
        od.AddU8(0x2002, 0x00, 0x01);
        var safety = node.Safety();
        safety.ConfigureSrdoProducer(1, new SrdoMapping().Add(0x2001, 0x00, 8), TimeSpan.FromMilliseconds(25));
        safety.ConfigureSrdoConsumer(2, new SrdoMapping().Add(0x2000, 0x00, 16), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(20), 0x125, 0x126);
        safety.CommitSafetyConfiguration();

        od.Invoking(o => o.AddU16(0x2001, 0x00, 0x0001)).Should().Throw<InvalidOperationException>()
            .WithMessage("*0x2001:00*SRDO 1*");
        od.Invoking(o => o.AddU8(0x2000, 0x00, 0x01)).Should().Throw<InvalidOperationException>()
            .WithMessage("*0x2000:00*SRDO 2*");
        od.TryGet(0x2001, 0x00, out var kept).Should().BeTrue();
        kept.DataType.Should().Be(OdDataType.Unsigned8, "the refused declaration did not land");
        od.Invoking(o => o.AddU16(0x2002, 0x00, 0x0002)).Should().NotThrow("no SRDO maps 0x2002");

        od.WriteUnsigned(0x1301, 0x01, 0);   // SRDO 1 deleted
        od.Invoking(o => o.AddU16(0x2001, 0x00, 0x0001)).Should().NotThrow("no existing SRDO maps 0x2001 any more");
        od.Invoking(o => o.AddU8(0x2000, 0x00, 0x01)).Should().Throw<InvalidOperationException>("SRDO 2 still exists");
    }

    /// <summary>A mapping that cannot be built does not reach the record: the configured SRDO
    /// survives the attempt instead of being deleted first and then left without a mapping.</summary>
    [Fact]
    public void A_Configured_Srdo_Survives_An_Attempt_With_A_Zero_Width_Entry()
    {
        var session = NewSession();
        using var bus = Open(session, 1);
        var clock = new ManualTimeSource();
        using var node = OpenClocked(bus, Producer, clock, srdoCount: 1);
        AddApplicationObjects(node.ObjectDictionary);
        var safety = node.Safety();
        safety.ConfigureSrdoProducer(1, new SrdoMapping().Add(0x2001, 0x00, 8), TimeSpan.FromMilliseconds(25));
        safety.CommitSafetyConfiguration();
        var od = node.ObjectDictionary;
        od.ReadUnsigned(0x1301, 1).Should().Be(1u);

        var ex = Record.Exception(() => safety.ConfigureSrdoProducer(1, new SrdoMapping().Add(0x2000, 0x00, 16).Add(default(PdoMappingEntry)), TimeSpan.FromMilliseconds(30)));
        ex.Should().BeOfType<ArgumentOutOfRangeException>();
        od.ReadUnsigned(0x1301, 1).Should().Be(1u, "the SRDO still exists");
        od.ReadUnsigned(0x1301, 2).Should().Be(25u);
        od.ReadUnsigned(0x1381, 0).Should().Be(2u);
        od.ReadUnsigned(0x1381, 1).Should().Be(0x2001_0008u);
        od.ReadUnsigned(0x13FE, 0).Should().Be(0xA5u, "nothing was written, so the configuration is still valid");
    }

    /// <summary>A reconfiguration the dictionary would refuse part-way — a mapped object that
    /// does not exist, is not mappable, has another size or none fixed (a string, a domain), or is
    /// not readable for a producer, or
    /// COB-IDs another existing SRDO holds — changes nothing: the committed SRDO, its checksum and
    /// 13FEh stay byte for byte what they were (ObjectDictionary has no rollback, so the writes
    /// are checked before the first one).</summary>
    [Theory]
    [InlineData("missing object")]
    [InlineData("not mappable")]
    [InlineData("wrong size")]
    [InlineData("not readable")]
    [InlineData("cob-ids of srdo 2")]
    [InlineData("visible string")]
    [InlineData("domain")]
    public void A_Refused_Reconfiguration_Leaves_The_Committed_Srdo_As_It_Was(string what)
    {
        var session = NewSession();
        using var bus = Open(session, 1);
        using var node = OpenClocked(bus, Producer, new ManualTimeSource(), srdoCount: 2);
        var od = node.ObjectDictionary;
        AddApplicationObjects(od);
        od.AddU32(0x2002, 0x00, 0, OdAccess.ReadWrite, pdoMappable: false);
        od.AddU8(0x2003, 0x00, 0, OdAccess.WriteOnly);
        od.AddRaw(0x2004, 0x00, OdDataType.VisibleString, new byte[] { 0x41, 0x42 });
        od.AddDomain(0x2005, 0x00, new byte[2]);
        var safety = node.Safety();
        safety.ConfigureSrdoProducer(1, new SrdoMapping().Add(0x2000, 0x00, 16).Add(0x2001, 0x00, 8), TimeSpan.FromMilliseconds(25));
        safety.ConfigureSrdoProducer(2, new SrdoMapping().Add(0x2001, 0x00, 8), TimeSpan.FromMilliseconds(30), 0x141, 0x142);
        safety.CommitSafetyConfiguration();
        var before = SafetyRecordBytes(od);
        before[(0x13FE, 0)].Should().Equal(new byte[] { 0xA5 });

        var (mapping, cobId1) = what switch
        {
            "missing object" => (new SrdoMapping().Add(0x2FFF, 0x00, 8), (uint?)null),
            "not mappable" => (new SrdoMapping().Add(0x2002, 0x00, 32), null),
            "wrong size" => (new SrdoMapping().Add(0x2000, 0x00, 8), null),
            "not readable" => (new SrdoMapping().Add(0x2003, 0x00, 8), null),
            "visible string" => (new SrdoMapping().Add(0x2004, 0x00, 16), null),
            "domain" => (new SrdoMapping().Add(0x2005, 0x00, 16), null),
            _ => (new SrdoMapping().Add(0x2001, 0x00, 8), (uint?)0x141),
        };
        var ex = Record.Exception(() => safety.ConfigureSrdoProducer(1, mapping, TimeSpan.FromMilliseconds(40), cobId1));
        ex.Should().BeOfType<ArgumentException>().Which.Message.Should().StartWith("SRDO1 configuration rejected");
        SafetyRecordBytes(od).Should().BeEquivalentTo(before, options => options.WithStrictOrdering(), "nothing was written");
    }

    /// <summary>Every sub-index of 1301h–1302h, 1381h–1382h, 13FEh and 13FFh, as stored.</summary>
    private static Dictionary<(ushort, byte), byte[]> SafetyRecordBytes(ObjectDictionary od)
    {
        var bytes = new Dictionary<(ushort, byte), byte[]>();
        foreach (ushort index in new ushort[] { 0x1301, 0x1302, 0x1381, 0x1382, 0x13FE, 0x13FF })
        {
            for (int sub = 0; sub <= 16; sub++)
                if (od.TryReadRaw(index, (byte)sub, out var raw)) bytes[(index, (byte)sub)] = raw;
        }
        return bytes;
    }

    /// <summary>§8.3.2.4 note 1: a safety entry is not written in Operational. A direct write runs
    /// its state check and its store under the dictionary's write gate on the writing thread; the
    /// transition into Operational publishes the state and arms the SRDO engine under the same
    /// gate. So a write holding the gate keeps the node out of Operational until it is stored, and
    /// the transition then sees it: 13FEh is 0 and the SRDO is not armed on the old configuration.</summary>
    [Fact]
    public async Task A_Direct_Safety_Write_Holding_The_Gate_Is_Stored_Before_The_Node_Enters_Operational()
    {
        var session = NewSession();
        using var bus = Open(session, 1);
        using var wire = new Wire(session, 2);
        using var node = new CanOpenNode(new CanBusService(bus), Producer, new CanOpenNodeOptions { SrdoCount = 1 }, ownsService: true, new ManualTimeSource());
        AddApplicationObjects(node.ObjectDictionary);
        node.Safety().ConfigureSrdoProducer(1, new SrdoMapping().Add(0x2001, 0x00, 8), TimeSpan.FromMilliseconds(25));
        node.Safety().CommitSafetyConfiguration();
        var od = node.ObjectDictionary;
        node.State.Should().Be(NmtState.PreOperational);
        od.ReadUnsigned(0x13FE, 0).Should().Be(0xA5u);

        using var inside = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() => od.Transaction(() =>
        {
            od.WriteUnsigned(0x1301, 0x02, 40); // sees Pre-Operational: accepted, and 13FEh is cleared with it
            inside.Set();
            release.Wait(ShortTimeout).Should().BeTrue("the test releases the gate");
        }));
        inside.Wait(ShortTimeout).Should().BeTrue();
        try
        {
            wire.SendNmt(NmtCommand.Start, Producer);
            // Positive witness that the actor has reached the transition: it waits at the write gate.
            await WaitUntilAsync(() => od.WriteGateWaiters >= 1, "the transition into Operational waits at the write gate");
            node.StateForTests.Should().Be(NmtState.PreOperational, "Operational is not published while a write holds the gate");
        }
        finally
        {
            release.Set(); // a failed assertion must not leave the gate held for the node's disposal
        }
        await holder.WithTimeoutAsync(ShortTimeout);

        await WaitUntilAsync(() => node.State == NmtState.Operational, "started");
        od.ReadUnsigned(0x1301, 0x02).Should().Be(40u);
        od.ReadUnsigned(0x13FE, 0).Should().Be(0u, "the write before the transition cleared it");
        var state = node.Safety().GetSrdoState(1);
        state.IsValid.Should().BeFalse("the transition saw the write: the configuration is not valid");
        state.Reason.Should().Be(SrdoInvalidReason.ConfigurationInvalid);
        Record.Exception(() => od.WriteUnsigned(0x1301, 0x02, 30)).Should().BeOfType<ArgumentException>()
            .Which.Message.Should().Contain("0x08000022", "a write after the transition is refused");
    }

    private static async Task AwaitSrdoChainAsync(CanOpenNode node)
    {
        Task chain = Task.CompletedTask;
        await node.PostToActorAsync(() => chain = node.SrdoSendChainForTests);
        await chain.WithTimeoutAsync(ShortTimeout);
    }

    private static bool SrdoPairInFlight(CanOpenNode node, int srdoNumber = 1)
    {
        bool inFlight = false;
        node.PostToActorAsync(() => inFlight = node.SrdoPairInFlightForTests(srdoNumber)).GetAwaiter().GetResult();
        return inFlight;
    }

    /// <summary>A bus service that holds the confirmation of every SRDO frame (101h–180h) until
    /// <see cref="Release"/>, as a bus does on which nobody acknowledges; everything else passes
    /// through. Counts what was handed to <see cref="SendConfirmedAsync"/> per COB-ID.</summary>
    private sealed class HoldingService : ICanBusService
    {
        private readonly ICanBusService _inner;
        private readonly object _gate = new();
        private readonly List<(uint CobId, byte[] Data)> _handed = new();
        private readonly List<(CanFrame Frame, TaskCompletionSource<TxConfirmation> Confirmation)> _held = new();
        private bool _holding = true;
        private readonly bool _holdGfc;
        private readonly bool _honourTokens;

        /// <summary>Wraps <paramref name="inner"/>; holds the SRDO frames until <see cref="Release"/>.</summary>
        /// <param name="inner">The service the frames that are not held go to.</param>
        /// <param name="holdGfc">Also hold the GFC (001h), not only the SRDO frames.</param>
        /// <param name="honourTokens">Abandon a held send when its token is cancelled, as a
        /// service does that has not handed the frame to the driver yet.</param>
        public HoldingService(ICanBusService inner, bool holdGfc = false, bool honourTokens = false)
        {
            _inner = inner;
            _holdGfc = holdGfc;
            _honourTokens = honourTokens;
        }

        public ICanBus Bus => _inner.Bus;
        public int SubscriptionCount => _inner.SubscriptionCount;
        public event EventHandler<Exception>? BackgroundExceptionOccurred
        {
            add => _inner.BackgroundExceptionOccurred += value;
            remove => _inner.BackgroundExceptionOccurred -= value;
        }
        public ISubscription Subscribe(Func<CanFrameEvent, bool>? predicate = null, int? bufferCapacity = null, bool includeEcho = false)
            => _inner.Subscribe(predicate, bufferCapacity, includeEcho);
        public ISubscription Subscribe(CanIdFilter filter, int? bufferCapacity = null, bool includeEcho = false)
            => _inner.Subscribe(filter, bufferCapacity, includeEcho);
        public IReadOnlyList<FilterOverlap> FindOverlappingFilterSubscriptions() => _inner.FindOverlappingFilterSubscriptions();

        public Task<TxConfirmation> SendConfirmedAsync(CanFrame frame, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            uint id = (uint)frame.ID;
            lock (_gate)
            {
                _handed.Add((id, frame.Data.ToArray()));
                _tokens.Add((id, cancellationToken));
                if (_holding && (id is >= CanOpenCobId.SrdoFirstCobId and <= CanOpenCobId.SrdoLastCobId
                                 || (_holdGfc && id == CanOpenCobId.GlobalFailsafeCommand)))
                {
                    var confirmation = new TaskCompletionSource<TxConfirmation>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _held.Add((frame, confirmation));
                    if (_honourTokens) cancellationToken.Register(() => confirmation.TrySetCanceled(cancellationToken));
                    return confirmation.Task;
                }
            }
            return _inner.SendConfirmedAsync(frame, timeout, cancellationToken);
        }

        public int Handed(uint cobId) { lock (_gate) return _handed.Count(x => x.CobId == cobId); }

        private readonly List<(uint CobId, CancellationToken Token)> _tokens = new();

        /// <summary>The token the <paramref name="nth"/> frame on <paramref name="cobId"/> was handed over with.</summary>
        public CancellationToken TokenOf(uint cobId, int nth) { lock (_gate) return _tokens.Where(x => x.CobId == cobId).ElementAt(nth).Token; }

        public byte[][] Payloads(uint cobId) { lock (_gate) return _handed.Where(x => x.CobId == cobId).Select(x => x.Data).ToArray(); }

        /// <summary>Stops holding and confirms what was held.</summary>
        public void Release()
        {
            (CanFrame Frame, TaskCompletionSource<TxConfirmation> Confirmation)[] held;
            lock (_gate)
            {
                _holding = false;
                held = _held.ToArray();
                _held.Clear();
            }
            foreach (var h in held) h.Confirmation.TrySetResult(new TxConfirmation { Confirmed = true });
        }

        public void Dispose() => _inner.Dispose();
    }

    [Fact]
    public async Task Enter_PreOperational_Stops_The_Producer()
    {
        var session = NewSession();
        using var bus = Open(session, 1);
        using var wire = new Wire(session, 2);
        var clock = new ManualTimeSource();
        using var node = OpenClocked(bus, Producer, clock);
        AddApplicationObjects(node.ObjectDictionary);
        var safety = node.Safety();
        safety.ConfigureSrdoProducer(1, new SrdoMapping().Add(0x2001, 0x00, 8), TimeSpan.FromMilliseconds(25));
        safety.CommitSafetyConfiguration();
        node.StartHeartbeatProducer(TimeSpan.FromMilliseconds(100)); // the witness
        uint cob1 = 0x0FFu + 2 * Producer;
        uint heartbeat = 0x700u + Producer;
        wire.SendNmt(NmtCommand.Start, Producer);
        await WaitUntilAsync(() => node.State == NmtState.Operational, "start");
        Advance(clock, node, TimeSpan.FromMilliseconds(Producer));
        await wire.WaitForCountAsync(cob1, 1);

        wire.SendNmt(NmtCommand.EnterPreOperational, Producer);
        // The state-change heartbeat goes out after the transition, so the SRDO has been left.
        await wire.WaitForStateCountAsync(heartbeat, NmtState.PreOperational, 1);
        safety.GetSrdoState(1).Reason.Should().Be(SrdoInvalidReason.NotOperational);
        int pairs = wire.Count(cob1);
        Advance(clock, node, TimeSpan.FromMilliseconds(100));
        // The cyclic heartbeat is due after four refresh cycles would have been: a witness that
        // the actor ran them, not a wire-order guarantee (the two go out on different send chains).
        await wire.WaitForStateCountAsync(heartbeat, NmtState.PreOperational, 2);
        await AwaitSrdoChainAsync((CanOpenNode)node); // the wire-order witness, see Tampered_Checksum_…
        wire.Count(cob1).Should().Be(pairs, "§8.3.2.2: safety communication only in Operational");
    }

    [Fact]
    public async Task Reset_Communication_From_Operational_Stops_The_Producer()
    {
        var session = NewSession();
        using var bus = Open(session, 1);
        using var wire = new Wire(session, 2);
        var clock = new ManualTimeSource();
        using var node = OpenClocked(bus, Producer, clock);
        AddApplicationObjects(node.ObjectDictionary);
        var safety = node.Safety();
        safety.ConfigureSrdoProducer(1, new SrdoMapping().Add(0x2001, 0x00, 8), TimeSpan.FromMilliseconds(25));
        safety.CommitSafetyConfiguration();
        node.StartHeartbeatProducer(TimeSpan.FromMilliseconds(100)); // the witness
        node.StoreParameters(); // the reset restores this configuration, not the defaults
        uint cob1 = 0x0FFu + 2 * Producer;
        uint heartbeat = 0x700u + Producer;
        wire.SendNmt(NmtCommand.Start, Producer);
        await WaitUntilAsync(() => node.State == NmtState.Operational, "start");
        Advance(clock, node, TimeSpan.FromMilliseconds(Producer));
        await wire.WaitForCountAsync(cob1, 1);

        wire.SendNmt(NmtCommand.ResetCommunication, Producer);
        // Boot-up of the construction, then of the reset: the SRDO was left before it.
        await wire.WaitForStateCountAsync(heartbeat, NmtState.Initializing, 2);
        safety.GetSrdoState(1).Reason.Should().Be(SrdoInvalidReason.NotOperational);
        int pairs = wire.Count(cob1);
        Advance(clock, node, TimeSpan.FromMilliseconds(100));
        // The first Pre-Operational heartbeat is the one the reset sends after the boot-up; the
        // second is the cyclic tick RestartCycle made due 100 ms after the reset: a witness that
        // the actor ran every refresh cycle inside the advance, not a wire-order guarantee.
        await wire.WaitForStateCountAsync(heartbeat, NmtState.PreOperational, 2);
        await AwaitSrdoChainAsync((CanOpenNode)node); // the wire-order witness, see Tampered_Checksum_…
        wire.Count(cob1).Should().Be(pairs, "the node is Pre-Operational after the reset");
        safety.GetSrdoState(1).Reason.Should().Be(SrdoInvalidReason.NotOperational);
    }

    /// <summary>Modelled on <c>CanOpenCriticalEventQueueTests</c>: a SYNC subscriber holds the
    /// event pump, three GFCs arrive, and the two that are identical to the one already waiting
    /// fold into it — a repeating GFC sender cannot grow the critical queue.</summary>
    [Fact]
    public async Task Repeated_Gfcs_Waiting_For_The_Handler_Are_Folded_Into_One()
    {
        using var bus = ControllableBus.EchoCapable(VirtualAdapterFixture.NewSession("co-safety-gfc-fold"));
        using var node = new CanOpenNode(new CanBusService(bus), Producer, new CanOpenNodeOptions { SrdoCount = 1 }, ownsService: true);
        node.ObjectDictionary.WriteUnsigned(0x1300, 0, 1);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int syncs = 0;
        int gfcs = 0;
        node.SyncReceived += (_, _) =>
        {
            if (Interlocked.Increment(ref syncs) == 1)
            {
                entered.TrySetResult(true);
                release.Task.GetAwaiter().GetResult();
            }
        };
        node.Safety().GlobalFailsafeCommandReceived += (_, _) => Interlocked.Increment(ref gfcs);

        try
        {
            bus.RaiseObserved(CanFrame.Classic(unchecked((int)CanOpenCobId.NmtCommand), new byte[] { (byte)NmtCommand.Start, Producer }), isEcho: false);
            await WaitUntilAsync(() => node.State == NmtState.Operational, "start");
            bus.RaiseObserved(CanFrame.Classic(unchecked((int)CanOpenCobId.Sync), ReadOnlyMemory<byte>.Empty), isEcho: false);
            await entered.Task.WithTimeoutAsync(ShortTimeout);

            for (int i = 0; i < 3; i++)
                bus.RaiseObserved(CanFrame.Classic(unchecked((int)CanOpenCobId.GlobalFailsafeCommand), ReadOnlyMemory<byte>.Empty), isEcho: false);
            await WaitUntilAsync(() => node.CoalescedEventCount == 2 && node.QueuedEventCount == 1, "two GFCs folded into the first");

            release.TrySetResult(true);
            await WaitUntilAsync(() => node.QueuedEventCount == 0 && Volatile.Read(ref gfcs) >= 1, "GFC delivered");
            Volatile.Read(ref gfcs).Should().Be(1);
        }
        finally
        {
            release.TrySetResult(true);
        }
    }

    /// <summary>§8.2 has every safety node act on a GFC, the sender included: on a bus that echoes,
    /// the node's own GFC is reported once, in both echo worlds (#94). A peer heartbeat injected
    /// after it is the ordering witness that no second report of the own GFC follows.</summary>
    [Theory]
    [MemberData(nameof(EchoWorldFixture.Both), MemberType = typeof(EchoWorldFixture))]
    public async Task A_Nodes_Own_Gfc_Is_Reported_Once_On_A_Bus_That_Echoes(EchoWorld world)
    {
        using var echo = EchoWorldFixture.Create(world, NewSession());
        using var node = CanOpen.OpenNode(echo.Bus, Producer, new CanOpenNodeOptions { SrdoCount = 1 });
        int gfcs = 0, witnesses = 0;
        node.Safety().GlobalFailsafeCommandReceived += (_, _) => Interlocked.Increment(ref gfcs);
        node.HeartbeatReceived += (_, e) => { if (e.ProducerNodeId == 0x12) Interlocked.Increment(ref witnesses); };
        node.ObjectDictionary.WriteUnsigned(0x1300, 0, 1);
        echo.InjectPeerFrame(CanFrame.Classic(unchecked((int)CanOpenCobId.NmtCommand),
            new[] { (byte)NmtCommand.Start, Producer }, isExtendedFrame: false));
        await WaitUntilAsync(() => node.State == NmtState.Operational, "start");

        await node.Safety().SendGlobalFailsafeCommandAsync();
        await WaitUntilAsync(() => Volatile.Read(ref gfcs) >= 1, "own GFC reported");
        echo.InjectPeerFrame(CanFrame.Classic((int)CanOpenCobId.Heartbeat(0x12), new[] { (byte)NmtState.Operational }));
        await WaitUntilAsync(() => Volatile.Read(ref witnesses) >= 1, "witness delivered");
        Volatile.Read(ref gfcs).Should().Be(1, "the own GFC is reported, and only once");
    }

    [Fact]
    public async Task Gfc_Round_Trip()
    {
        var session = NewSession();
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var wire = new Wire(session, 3);
        using var sender = CanOpen.OpenNode(busA, Producer, new CanOpenNodeOptions { SrdoCount = 1 });
        using var receiver = CanOpen.OpenNode(busB, Consumer, new CanOpenNodeOptions { SrdoCount = 1 });
        int gfcs = 0;
        receiver.Safety().GlobalFailsafeCommandReceived += (_, _) => Interlocked.Increment(ref gfcs);
        receiver.ObjectDictionary.WriteUnsigned(0x1300, 0, 1);
        wire.SendNmt(NmtCommand.Start, 0);
        await WaitUntilAsync(() => sender.State == NmtState.Operational && receiver.State == NmtState.Operational, "start");
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.Safety().SendGlobalFailsafeCommandAsync());
        sender.ObjectDictionary.WriteUnsigned(0x1300, 0, 1);
        await sender.Safety().SendGlobalFailsafeCommandAsync();
        await wire.WaitForCountAsync(CanOpenCobId.GlobalFailsafeCommand, 1);
        wire.Payloads(CanOpenCobId.GlobalFailsafeCommand)[0].Should().BeEmpty();
        await WaitUntilAsync(() => Volatile.Read(ref gfcs) >= 1, "GFC received");
    }

    [Fact]
    public async Task Reset_Communication_Restores_The_Safety_Objects()
    {
        var session = NewSession();
        using var bus = Open(session, 1);
        using var wire = new Wire(session, 2);
        using var node = CanOpen.OpenNode(bus, Producer, new CanOpenNodeOptions { SrdoCount = 1 });
        AddApplicationObjects(node.ObjectDictionary);
        node.Safety().ConfigureSrdoProducer(1, new SrdoMapping().Add(0x2001, 0x00, 8), TimeSpan.FromMilliseconds(30));
        node.Safety().CommitSafetyConfiguration();
        node.StoreParameters();
        node.ObjectDictionary.WriteUnsigned(0x1301, 1, 0);
        node.ObjectDictionary.WriteUnsigned(0x1301, 2, 40);
        wire.SendNmt(NmtCommand.ResetCommunication, Producer);
        await WaitUntilAsync(() => node.ObjectDictionary.ReadUnsigned(0x1301, 2) == 30, "restored");
        // The reset restores the objects in one actor turn, 13FEh after the records; a State read
        // is a round trip queued behind that turn, so the reads below see all of it.
        _ = node.State;
        node.ObjectDictionary.ReadUnsigned(0x1301, 1).Should().Be(1u);
        node.ObjectDictionary.ReadUnsigned(0x13FE, 0).Should().Be(0xA5u);
    }

    [Fact]
    public void A_Foreign_Node_Does_Not_Provide_Pdo_Monitoring()
    {
        Action access = () => CanKit.Pro.CANopen.Pdo.CanOpenPdoExtensions.Pdo(new ForeignNode());
        access.Should().Throw<NotSupportedException>();
    }

    /// <summary>An ICanOpenNode this library did not create: every member throws.</summary>
    private sealed class ForeignNode : ICanOpenNode
    {
        public byte NodeId => throw new NotImplementedException();
        public CanOpenNodeOptions Options => throw new NotImplementedException();
        public ObjectDictionary ObjectDictionary => throw new NotImplementedException();
        public NmtState State => throw new NotImplementedException();
        public DeviceDescriptionReport? DeviceDescription => throw new NotImplementedException();
        public FlyingMasterRole FlyingMasterRole => throw new NotImplementedException();
        public byte? ActiveFlyingMasterNodeId => throw new NotImplementedException();
        public ushort? ActiveFlyingMasterPriority => throw new NotImplementedException();

        public event EventHandler<HeartbeatReceivedEventArgs>? HeartbeatReceived { add { } remove { } }
        public event EventHandler<HeartbeatTimeoutEventArgs>? HeartbeatTimeout { add { } remove { } }
        public event EventHandler<EmcyReceivedEventArgs>? EmcyReceived { add { } remove { } }
        public event EventHandler<SyncReceivedEventArgs>? SyncReceived { add { } remove { } }
        public event EventHandler<RpdoReceivedEventArgs>? RpdoReceived { add { } remove { } }
        public event EventHandler<NmtCommandReceivedEventArgs>? NmtCommandReceived { add { } remove { } }
        public event EventHandler<NmtResetEventArgs>? ApplicationReset { add { } remove { } }
        public event EventHandler<NodeGuardingReceivedEventArgs>? NodeGuardingReceived { add { } remove { } }
        public event EventHandler<NodeGuardingTimeoutEventArgs>? NodeGuardingTimeout { add { } remove { } }
        public event EventHandler<LifeGuardingEventArgs>? LifeGuardingEvent { add { } remove { } }
        public event EventHandler<Exception>? BackgroundExceptionOccurred { add { } remove { } }
        public event EventHandler<FlyingMasterChangedEventArgs>? FlyingMasterChanged { add { } remove { } }

        public Task SendNmtCommandAsync(NmtCommand command, byte targetNodeId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public void StartFlyingMaster(ushort priorityLevel, TimeSpan activeMasterHeartbeatTimeout) => throw new NotImplementedException();
        public void StopFlyingMaster() => throw new NotImplementedException();
        public void StartHeartbeatProducer(TimeSpan interval) => throw new NotImplementedException();
        public void StopHeartbeatProducer() => throw new NotImplementedException();
        public void AddHeartbeatConsumer(byte producerNodeId, TimeSpan timeout) => throw new NotImplementedException();
        public void RemoveHeartbeatConsumer(byte producerNodeId) => throw new NotImplementedException();
        public void StartSyncProducer(TimeSpan interval) => throw new NotImplementedException();
        public void StopSyncProducer() => throw new NotImplementedException();
        public Task SendSyncAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task SendEmcyAsync(ushort errorCode, byte errorRegister, ReadOnlyMemory<byte> manufacturerSpecific = default,
            CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public void BindPeerDeviceDescription(byte nodeId, CanOpenDeviceDescription description) => throw new NotImplementedException();
        public CanOpenDeviceDescription? GetPeerDeviceDescription(byte nodeId) => throw new NotImplementedException();
        public void UnbindPeerDeviceDescription(byte nodeId) => throw new NotImplementedException();
        public Task<byte[]> SdoUploadAsync(byte serverNodeId, ushort index, byte subindex, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<byte[]> SdoUploadAsync(byte serverNodeId, ushort index, byte subindex, SdoTransferMode mode = SdoTransferMode.Auto,
            CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task SdoDownloadAsync(byte serverNodeId, ushort index, byte subindex, ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task SdoDownloadAsync(byte serverNodeId, ushort index, byte subindex, ReadOnlyMemory<byte> data,
            SdoTransferMode mode = SdoTransferMode.Auto, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public void ConfigureTpdo(int pdoIndex, PdoMapping mapping, TpdoTransmission transmission = TpdoTransmission.EventDriven,
            uint? cobId = null, TimeSpan? eventTimerInterval = null, TimeSpan? inhibitTime = null) => throw new NotImplementedException();
        public void ConfigureRpdo(int pdoIndex, PdoMapping mapping, uint? cobId = null,
            RpdoTransmission transmission = RpdoTransmission.EventDriven) => throw new NotImplementedException();
        public Task TriggerTpdoAsync(int pdoIndex, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ForeignPdoObserveResult> ObserveForeignPdoAsync(byte peerNodeId, uint cobId, ReadOnlyMemory<byte> payload,
            CanOpenDeviceDescription peerDescription, IForeignPdoSink sink, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public void StoreParameters() => throw new NotImplementedException();
        public void RestoreDefaultParameters() => throw new NotImplementedException();
        public void StartNodeGuardingConsumer(byte producerNodeId, TimeSpan guardTime, byte lifeTimeFactor) => throw new NotImplementedException();
        public void StopNodeGuardingConsumer(byte producerNodeId) => throw new NotImplementedException();
        public void Dispose() => throw new NotImplementedException();
    }
}
