using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.Actor;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Safety;
using CanKit.Pro.Reliability;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Safety;

/// <summary>The producer half of <see cref="SrdoEngine"/> (CiA DSP 304 V1.0 §8.1, §8.1.3.1,
/// §9.5), driven without a bus: a real actor on a <see cref="ManualTimeSource"/>, a dictionary
/// holding the records, and a host that records what the engine asks it to send.</summary>
public class SrdoEngineProducerTests : IDisposable
{
    private const byte NodeId = 0x11;
    private readonly ManualTimeSource _clock = new();
    private readonly ProtocolActor _actor;
    private readonly DeadlineScheduler _deadlines;
    private readonly ObjectDictionary _od = new();
    private readonly RecordingHost _host = new();
    private SrdoEngine? _engine;

    public SrdoEngineProducerTests()
    {
        _actor = new ProtocolActor(ActorExecutionMode.DedicatedThread, null, _clock, null);
        _deadlines = new DeadlineScheduler(_actor);
    }

    public void Dispose()
    {
        _actor.PostAsync(() => _engine?.Dispose()).GetAwaiter().GetResult();
        _actor.Dispose();
    }

    internal sealed class RecordingHost : ISrdoEngineHost
    {
        public readonly List<(uint CobId, byte[] Payload)> Sent = new();
        public readonly List<ushort> Emcys = new();
        public readonly List<(int Srdo, uint CobId, byte[] Payload)> Received = new();
        public readonly List<(int Srdo, bool IsValid, SrdoInvalidReason? Reason)> States = new();
        public int Gfcs;
        public readonly List<Exception> Exceptions = new();
        public void Send(uint cobId, byte[] payload) => Sent.Add((cobId, payload));
        public void EmitEmcy(ushort errorCode) => Emcys.Add(errorCode);
        public void SrdoReceived(int srdoNumber, uint cobId, byte[] payload) => Received.Add((srdoNumber, cobId, payload));
        public void SrdoStateChanged(int srdoNumber, bool isValid, SrdoInvalidReason? reason) => States.Add((srdoNumber, isValid, reason));
        public void GlobalFailsafeCommandReceived() => Gfcs++;
        public void ReportBackgroundException(Exception exception) => Exceptions.Add(exception);
    }

    /// <summary>The dictionary a node with SrdoCount = 2 would hold, plus two application objects.</summary>
    internal static void Populate(ObjectDictionary od, byte nodeId, int count)
    {
        od.AddU8(0x1001, 0, 0, OdAccess.ReadOnly, pdoMappable: false);
        od.AddU8(SrdoRecords.GfcParameter, 0, 0, OdAccess.ReadWrite, pdoMappable: false);
        for (int n = 1; n <= count; n++)
        {
            var comm = SrdoRecords.CommIndex(n);
            var map = SrdoRecords.MapIndex(n);
            od.AddU8(comm, 0, 6, OdAccess.ReadOnly, false); od.AddU8(comm, 1, 0, OdAccess.ReadWrite, false);
            od.AddU16(comm, 2, 25, OdAccess.ReadWrite, false); od.AddU8(comm, 3, 20, OdAccess.ReadWrite, false);
            od.AddU8(comm, 4, 254, OdAccess.ReadOnly, false);
            od.AddU32(comm, 5, n == 1 ? CanOpenCobId.SrdoDefaultCobId1(nodeId) : 0u, OdAccess.ReadWrite, false);
            od.AddU32(comm, 6, n == 1 ? CanOpenCobId.SrdoDefaultCobId2(nodeId) : 0u, OdAccess.ReadWrite, false);
            od.AddU8(map, 0, 0, OdAccess.ReadWrite, false);
            for (byte s = 1; s <= 16; s++) od.AddU32(map, s, 0, OdAccess.ReadWrite, false);
        }
        od.AddU8(SrdoRecords.ConfigurationValid, 0, 0, OdAccess.ReadWrite, false);
        od.AddU8(SrdoRecords.Checksum, 0, (byte)count, OdAccess.ReadOnly, false);
        for (byte n = 1; n <= count; n++) od.AddU16(SrdoRecords.Checksum, n, 0, OdAccess.ReadWrite, false);
        od.AddU16(0x2000, 0, 0x1234);
        od.AddU8(0x2001, 0, 0x5A);
    }

    /// <summary>Writes record n as a producer/consumer of 0x2000:00 (16 bit) and 0x2001:00 (8 bit)
    /// and makes the configuration valid (13FFh:n = CRC, 13FEh = A5h).</summary>
    internal static void Configure(ObjectDictionary od, int n, SrdoDirection direction, ushort cycleMs, byte srvtMs, uint cob1, uint cob2)
    {
        var comm = SrdoRecords.CommIndex(n);
        var map = SrdoRecords.MapIndex(n);
        od.WriteUnsigned(comm, 1, 0);
        od.WriteUnsigned(map, 0, 0);
        od.WriteUnsigned(map, 1, 0x2000_0010); od.WriteUnsigned(map, 2, 0x2000_0010);
        od.WriteUnsigned(map, 3, 0x2001_0008); od.WriteUnsigned(map, 4, 0x2001_0008);
        od.WriteUnsigned(map, 0, 4);
        od.WriteUnsigned(comm, 2, cycleMs); od.WriteUnsigned(comm, 3, srvtMs);
        od.WriteUnsigned(comm, 5, cob1); od.WriteUnsigned(comm, 6, cob2);
        od.WriteUnsigned(comm, 1, (byte)direction);
        SrdoRecords.TryReadCommunication(od, n, out var p);
        od.WriteUnsigned(SrdoRecords.Checksum, (byte)n, SrdoCrc.Compute(p, SrdoMapping.FromEntries(SrdoRecords.ReadMapping(od, n))));
        od.WriteUnsigned(SrdoRecords.ConfigurationValid, 0, 0xA5);
    }

    private SrdoEngine Start(int count = 2)
    {
        Populate(_od, NodeId, count);
        _engine = new SrdoEngine(_actor, _clock, _deadlines, _od, NodeId, count, _host);
        return _engine;
    }

    private void OnActor(Action work) => _actor.PostAsync(work).GetAwaiter().GetResult();

    /// <summary>Two round-trips: the first may return while the drain that ran it is still
    /// firing timers; the second is drained only after those callbacks returned.</summary>
    private void Settle() { OnActor(() => { }); OnActor(() => { }); }

    private void Advance(TimeSpan by) { Settle(); _clock.Advance(by); Settle(); }

    [Fact]
    public void First_Cycle_Is_Delayed_Half_A_Millisecond_Per_NodeId()
    {
        var engine = Start();
        Configure(_od, 1, SrdoDirection.Transmit, 25, 20, 0x123, 0x124);
        OnActor(() => { engine.Rebuild(1); engine.EnterOperational(); });
        Settle();
        _host.Sent.Should().BeEmpty("§9.5: the first cyclic transmit is delayed 0.5 ms × node-id");
        Advance(TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond * NodeId / 2 - 1));
        _host.Sent.Should().BeEmpty();
        Advance(TimeSpan.FromTicks(1));
        _host.Sent.Should().HaveCount(2);
    }

    [Fact]
    public void A_Transmission_Is_A_Plain_Frame_And_Its_Inverse_On_Following_Ids()
    {
        var engine = Start();
        Configure(_od, 1, SrdoDirection.Transmit, 25, 20, 0x123, 0x124);
        OnActor(() => { engine.Rebuild(1); engine.EnterOperational(); });
        Advance(TimeSpan.FromMilliseconds(NodeId));
        _host.Sent[0].CobId.Should().Be(0x123u);
        _host.Sent[0].Payload.Should().Equal(0x34, 0x12, 0x5A);
        _host.Sent[1].CobId.Should().Be(0x124u);
        _host.Sent[1].Payload.Should().Equal(0xCB, 0xED, 0xA5);
    }

    [Fact]
    public void Cycle_Repeats_Every_Refresh_Time()
    {
        var engine = Start();
        Configure(_od, 1, SrdoDirection.Transmit, 25, 20, 0x123, 0x124);
        OnActor(() => { engine.Rebuild(1); engine.EnterOperational(); });
        Advance(TimeSpan.FromMilliseconds(NodeId));
        _host.Sent.Should().HaveCount(2);
        Advance(TimeSpan.FromMilliseconds(24));
        _host.Sent.Should().HaveCount(2);
        Advance(TimeSpan.FromMilliseconds(1));
        _host.Sent.Should().HaveCount(4);
        Advance(TimeSpan.FromMilliseconds(25));
        _host.Sent.Should().HaveCount(6);
    }

    [Fact]
    public void Trigger_Transmits_Now_And_Restarts_The_Cycle()
    {
        var engine = Start();
        Configure(_od, 1, SrdoDirection.Transmit, 25, 20, 0x123, 0x124);
        OnActor(() => { engine.Rebuild(1); engine.EnterOperational(); });
        Advance(TimeSpan.FromMilliseconds(NodeId));
        Advance(TimeSpan.FromMilliseconds(10));
        _od.WriteUnsigned(0x2001, 0, 0x01);
        OnActor(() => engine.Trigger(1));
        _host.Sent.Should().HaveCount(4);
        _host.Sent[2].Payload.Should().Equal(new byte[] { 0x34, 0x12, 0x01 }, "the payload is sampled at transmission");
        Advance(TimeSpan.FromMilliseconds(24));
        _host.Sent.Should().HaveCount(4, "the refresh cycle restarted with the triggered transmission");
        Advance(TimeSpan.FromMilliseconds(1));
        _host.Sent.Should().HaveCount(6);
    }

    [Fact]
    public void Nothing_Is_Sent_Outside_Operational_Or_With_An_Invalid_Configuration()
    {
        var engine = Start();
        Configure(_od, 1, SrdoDirection.Transmit, 25, 20, 0x123, 0x124);
        OnActor(() => engine.Rebuild(1));
        Advance(TimeSpan.FromMilliseconds(100));
        OnActor(() => engine.Trigger(1));
        _host.Sent.Should().BeEmpty("SRDOs exist only in Operational (§8.3.2.2)");
        engine.GetState(1).IsValid.Should().BeFalse();
        engine.GetState(1).Reason.Should().Be(SrdoInvalidReason.NotOperational);
        _od.WriteUnsigned(SrdoRecords.Checksum, 1, 0xFFFF);
        OnActor(() => { engine.Rebuild(1); engine.EnterOperational(); });
        Advance(TimeSpan.FromMilliseconds(100));
        OnActor(() => engine.Trigger(1));
        _host.Sent.Should().BeEmpty("§8.3.1 D: in case of mismatch the safety node shall not transmit SRDOs");
        engine.GetState(1).Reason.Should().Be(SrdoInvalidReason.ConfigurationInvalid);
        _host.States.Should().Contain((1, false, SrdoInvalidReason.ConfigurationInvalid));
    }

    [Fact]
    public void Leaving_Operational_Stops_The_Cycle_And_Reports()
    {
        var engine = Start();
        Configure(_od, 1, SrdoDirection.Transmit, 25, 20, 0x123, 0x124);
        OnActor(() => { engine.Rebuild(1); engine.EnterOperational(); });
        Advance(TimeSpan.FromMilliseconds(NodeId));
        _host.States.Should().Contain((1, true, null));
        OnActor(() => engine.LeaveOperational());
        Advance(TimeSpan.FromMilliseconds(100));
        _host.Sent.Should().HaveCount(2);
        _host.States.Should().Contain((1, false, SrdoInvalidReason.NotOperational));
        engine.GetState(1).Direction.Should().Be(SrdoDirection.Transmit);
    }

    [Fact]
    public void An_Empty_Mapping_Transmits_Empty_Frames()
    {
        var engine = Start();
        Configure(_od, 1, SrdoDirection.Transmit, 25, 20, 0x123, 0x124);
        _od.WriteUnsigned(SrdoRecords.CommIndex(1), 1, 0);
        _od.WriteUnsigned(SrdoRecords.MapIndex(1), 0, 0);
        _od.WriteUnsigned(SrdoRecords.CommIndex(1), 1, 1);
        SrdoRecords.TryReadCommunication(_od, 1, out var p);
        _od.WriteUnsigned(SrdoRecords.Checksum, 1, SrdoCrc.Compute(p, new SrdoMapping()));
        _od.WriteUnsigned(SrdoRecords.ConfigurationValid, 0, 0xA5);
        OnActor(() => { engine.Rebuild(1); engine.EnterOperational(); });
        Advance(TimeSpan.FromMilliseconds(NodeId));
        _host.Sent.Should().HaveCount(2);
        _host.Sent[0].Payload.Should().BeEmpty("0 ≤ L ≤ 8 (§8.1.3.1)");
    }

    [Fact]
    public void ChangeOfState_Keys_Are_The_Transmit_Mappings()
    {
        var engine = Start();
        Configure(_od, 1, SrdoDirection.Transmit, 25, 20, 0x123, 0x124);
        Configure(_od, 2, SrdoDirection.Receive, 50, 20, 0x125, 0x126);
        var keys = new HashSet<uint>();
        engine.CollectChangeOfStateEntries(keys, (i, s) => ((uint)i << 8) | s);
        keys.Should().BeEquivalentTo(new uint[] { 0x2000_00, 0x2001_00 });
    }
}
