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
        /// <summary>Every host call in the order it happened, as a short tag, for the tests that
        /// assert ordering between the typed lists (<c>state:1:valid</c>, <c>received:1</c>,
        /// <c>emcy:8210</c>, <c>gfc</c>, <c>send:123</c>).</summary>
        public readonly List<string> Log = new();
        /// <summary>Makes the next <see cref="Send"/> throw (once) instead of recording.</summary>
        public volatile bool ThrowOnNextSend;
        public void Send(uint cobId, byte[] payload)
        {
            if (ThrowOnNextSend)
            {
                ThrowOnNextSend = false;
                throw new InvalidOperationException("bus off");
            }
            Sent.Add((cobId, payload));
            Log.Add($"send:{cobId:X3}");
        }
        public void EmitEmcy(ushort errorCode) { Emcys.Add(errorCode); Log.Add($"emcy:{errorCode:X4}"); }
        public void SrdoReceived(int srdoNumber, uint cobId, byte[] payload) { Received.Add((srdoNumber, cobId, payload)); Log.Add($"received:{srdoNumber}"); }
        public void SrdoStateChanged(int srdoNumber, bool isValid, SrdoInvalidReason? reason)
        {
            States.Add((srdoNumber, isValid, reason));
            Log.Add($"state:{srdoNumber}:{(isValid ? "valid" : reason?.ToString() ?? "gone")}");
        }
        public void GlobalFailsafeCommandReceived() { Gfcs++; Log.Add("gfc"); }
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

    private SrdoEngine Start(int count = 2, IProtocolActor? actor = null)
    {
        Populate(_od, NodeId, count);
        _engine = new SrdoEngine(actor ?? _actor, _clock, _deadlines, _od, NodeId, count, _host);
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
    public void A_Valid_Producer_Rebuilt_To_Direction_None_Is_Reported_As_Gone()
    {
        var engine = Start();
        Configure(_od, 1, SrdoDirection.Transmit, 25, 20, 0x123, 0x124);
        OnActor(() => { engine.Rebuild(1); engine.EnterOperational(); });
        Advance(TimeSpan.FromMilliseconds(NodeId));
        engine.GetState(1).IsValid.Should().BeTrue();
        _od.WriteUnsigned(SrdoRecords.CommIndex(1), 1, 0);
        OnActor(() => engine.Rebuild(1));
        engine.GetState(1).IsValid.Should().BeFalse();
        _host.States.Should().Contain((1, false, null), "a null reason means the SRDO no longer exists, and snapshot and event must agree");
        Advance(TimeSpan.FromMilliseconds(100));
        _host.Sent.Should().HaveCount(2, "a record without a direction does not transmit");
    }

    [Fact]
    public void A_Throwing_Send_Is_Reported_And_The_Cycle_Goes_On()
    {
        var engine = Start();
        Configure(_od, 1, SrdoDirection.Transmit, 25, 20, 0x123, 0x124);
        OnActor(() => { engine.Rebuild(1); engine.EnterOperational(); });
        _host.ThrowOnNextSend = true;
        Advance(TimeSpan.FromMilliseconds(NodeId));
        _host.Exceptions.Should().ContainSingle().Which.Should().BeOfType<InvalidOperationException>();
        _host.Sent.Should().BeEmpty();
        engine.GetState(1).IsValid.Should().BeTrue("a transport failure does not invalidate the SRDO");
        Advance(TimeSpan.FromMilliseconds(25));
        _host.Sent.Should().HaveCount(2, "the refresh cycle was restarted despite the failure");
    }

    [Fact]
    public void Rebuild_Mid_Cycle_Replaces_The_Pending_Cycle()
    {
        var engine = Start();
        Configure(_od, 1, SrdoDirection.Transmit, 25, 20, 0x123, 0x124);
        OnActor(() => { engine.Rebuild(1); engine.EnterOperational(); });
        Advance(TimeSpan.FromMilliseconds(NodeId));   // t = 17 ms, first pair went out at 8.5 ms, next due 33.5 ms
        Advance(TimeSpan.FromMilliseconds(10));       // t = 27 ms
        _host.Sent.Should().HaveCount(2);
        Configure(_od, 1, SrdoDirection.Transmit, 40, 20, 0x123, 0x124);
        OnActor(() => engine.Rebuild(1));             // due again at 27 + 8.5 = 35.5 ms
        Advance(TimeSpan.FromMilliseconds(7));        // t = 34 ms, past the old due time
        _host.Sent.Should().HaveCount(2, "the cycle pending before the rebuild must not fire");
        Advance(TimeSpan.FromMilliseconds(1.5));      // t = 35.5 ms
        _host.Sent.Should().HaveCount(4, "exactly one pair, 0.5 ms × node-id after the rebuild");
        Advance(TimeSpan.FromMilliseconds(39));
        _host.Sent.Should().HaveCount(4);
        Advance(TimeSpan.FromMilliseconds(1));
        _host.Sent.Should().HaveCount(6, "then every 40 ms, the new refresh time");
    }

    [Fact]
    public void Leave_And_Reenter_Mid_Cycle_Starts_Over_With_The_Initial_Delay()
    {
        var engine = Start();
        Configure(_od, 1, SrdoDirection.Transmit, 25, 20, 0x123, 0x124);
        OnActor(() => { engine.Rebuild(1); engine.EnterOperational(); });
        Advance(TimeSpan.FromMilliseconds(NodeId));   // next due 33.5 ms
        Advance(TimeSpan.FromMilliseconds(10));       // t = 27 ms
        OnActor(() => engine.LeaveOperational());
        OnActor(() => engine.EnterOperational());     // due 35.5 ms
        Advance(TimeSpan.FromMilliseconds(7));        // t = 34 ms
        _host.Sent.Should().HaveCount(2, "the cycle of the earlier Operational period must not fire");
        Advance(TimeSpan.FromMilliseconds(1.5));
        _host.Sent.Should().HaveCount(4);
        Advance(TimeSpan.FromMilliseconds(25));
        _host.Sent.Should().HaveCount(6);
    }

    [Fact]
    public void Entering_Operational_Twice_Does_Not_Double_The_Cycle()
    {
        var engine = Start();
        Configure(_od, 1, SrdoDirection.Transmit, 25, 20, 0x123, 0x124);
        OnActor(() => { engine.EnterOperational(); engine.EnterOperational(); });
        Advance(TimeSpan.FromMilliseconds(NodeId));
        _host.Sent.Should().HaveCount(2, "one pair, not two");
        Advance(TimeSpan.FromMilliseconds(25));
        _host.Sent.Should().HaveCount(4);
        Advance(TimeSpan.FromMilliseconds(25));
        _host.Sent.Should().HaveCount(6);
    }

    /// <summary>Forwards to a real actor and remembers every timer the engine asked for, so a test
    /// can see a timer being released, not just not firing.</summary>
    private sealed class TrackingActor : IProtocolActor
    {
        private readonly IProtocolActor _inner;
        public readonly List<Handle> Timers = new();
        public TrackingActor(IProtocolActor inner) => _inner = inner;
        public void Post(Action work) => _inner.Post(work);
        public Task PostAsync(Action work, System.Threading.CancellationToken cancellationToken = default) => _inner.PostAsync(work, cancellationToken);
        public Task<T> PostAsync<T>(Func<T> work, System.Threading.CancellationToken cancellationToken = default) => _inner.PostAsync(work, cancellationToken);
        public IDisposable Schedule(TimeSpan delay, Action callback)
        {
            var handle = new Handle(_inner.Schedule(delay, callback));
            Timers.Add(handle);
            return handle;
        }
        public event EventHandler<Exception> BackgroundExceptionOccurred { add { } remove { } }
        public void Dispose() { }

        public sealed class Handle : IDisposable
        {
            private readonly IDisposable _inner;
            public Handle(IDisposable inner) => _inner = inner;
            public bool Disposed { get; private set; }
            public void Dispose() { Disposed = true; _inner.Dispose(); }
        }
    }

    [Fact]
    public void Leaving_Operational_Releases_The_Pending_Timer()
    {
        var tracking = new TrackingActor(_actor);
        var engine = Start(actor: tracking);
        Configure(_od, 1, SrdoDirection.Transmit, 25, 20, 0x123, 0x124);
        OnActor(() => { engine.Rebuild(1); engine.EnterOperational(); });
        Advance(TimeSpan.FromMilliseconds(NodeId));
        var pending = tracking.Timers[^1];
        pending.Disposed.Should().BeFalse("the cycle is running");
        OnActor(() => engine.LeaveOperational());
        pending.Disposed.Should().BeTrue("a cycle that cannot fire any more must not stay scheduled");
    }

    [Fact]
    public void An_SRDO_Number_Out_Of_Range_Is_Rejected()
    {
        var engine = Start();
        foreach (var n in new[] { 0, 3, -1 })
        {
            engine.Invoking(e => e.Rebuild(n)).Should().Throw<ArgumentOutOfRangeException>();
            engine.Invoking(e => e.Trigger(n)).Should().Throw<ArgumentOutOfRangeException>();
            engine.Invoking(e => e.GetState(n)).Should().Throw<ArgumentOutOfRangeException>();
        }
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
