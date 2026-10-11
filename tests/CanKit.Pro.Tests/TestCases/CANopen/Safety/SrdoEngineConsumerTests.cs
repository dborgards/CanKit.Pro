using System;
using System.Linq;
using AwesomeAssertions;
using CanKit.Pro.Actor;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Safety;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.Reliability;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;
using static CanKit.Pro.Tests.TestCases.CANopen.Safety.SrdoEngineProducerTests;

namespace CanKit.Pro.Tests.TestCases.CANopen.Safety;

/// <summary>The consumer half (CiA DSP 304 V1.0 §8.1.1 Figures 2 and 3, §8.1.3.1, §9.5) and
/// the GFC (§8.2). SCT and SRVT are deadlines on the engine's clock; the tests move it.
/// FR-CO-040 the consumer, FR-CO-041 the GFC.</summary>
public class SrdoEngineConsumerTests : IDisposable
{
    private const byte NodeId = 0x11;
    private readonly ManualTimeSource _clock = new();
    private readonly ProtocolActor _actor;
    private readonly DeadlineScheduler _deadlines;
    private readonly ObjectDictionary _od = new();
    private readonly RecordingHost _host = new();
    private SrdoEngine _engine = null!;

    public SrdoEngineConsumerTests()
    {
        _actor = new ProtocolActor(ActorExecutionMode.DedicatedThread, null, _clock, null);
        _deadlines = new DeadlineScheduler(_actor);
    }

    public void Dispose()
    {
        _actor.PostAsync(() => _engine.Dispose()).GetAwaiter().GetResult();
        _actor.Dispose();
    }

    private void OnActor(Action work) => _actor.PostAsync(work).GetAwaiter().GetResult();
    private void Settle() { OnActor(() => { }); OnActor(() => { }); }
    private void Advance(TimeSpan by) { Settle(); _clock.Advance(by); Settle(); }

    /// <summary>SRDO 1 consumes 0x2000:00 / 0x2001:00 on 123h/124h, SCT 50 ms, SRVT 20 ms, and is Operational.</summary>
    private void StartConsumer()
    {
        Populate(_od, NodeId, 2);
        Configure(_od, 1, SrdoDirection.Receive, 50, 20, 0x123, 0x124);
        _engine = new SrdoEngine(_actor, _clock, _deadlines, _od, NodeId, 2, _host);
        OnActor(() => { _engine.Rebuild(1); _engine.Rebuild(2); _engine.EnterOperational(); });
        Settle();
    }

    private bool Frame(uint cobId, params byte[] data)
    {
        bool consumed = false;
        OnActor(() => consumed = _engine.TryHandleFrame(cobId, data, isRtr: false));
        return consumed;
    }

    private static readonly byte[] Plain = { 0x78, 0x56, 0x01 };
    private static readonly byte[] Inverse = { 0x87, 0xA9, 0xFE };

    [Fact]
    public void Starts_Invalid_And_Becomes_Valid_With_A_Complete_Pair()
    {
        StartConsumer();
        _engine.GetState(1).IsValid.Should().BeFalse();
        _engine.GetState(1).Reason.Should().Be(SrdoInvalidReason.NotReceived);
        Frame(0x123, Plain).Should().BeTrue();
        _engine.GetState(1).IsValid.Should().BeFalse("one frame is not an SRDO");
        Frame(0x124, Inverse).Should().BeTrue();
        _engine.GetState(1).IsValid.Should().BeTrue();
        _od.ReadUnsigned(0x2000, 0).Should().Be(0x5678u);
        _od.ReadUnsigned(0x2001, 0).Should().Be(0x01u);
        _host.States.Should().ContainInOrder((1, false, SrdoInvalidReason.NotReceived), (1, true, null));
        _host.Received.Should().ContainSingle().Which.Should().Be((1, 0x123u, Plain));
        _host.Log.IndexOf("state:1:valid").Should().BeGreaterThanOrEqualTo(0);
        _host.Log.IndexOf("state:1:valid").Should().BeLessThan(_host.Log.IndexOf("received:1"), "the state change precedes the payload event");
    }

    [Fact]
    public void Leaving_Operational_Cancels_Sct_And_Srvt()
    {
        StartConsumer();
        Frame(0x123, Plain); Frame(0x124, Inverse);   // valid, SCT running
        Frame(0x123, Plain);                          // pending first frame, SRVT running
        OnActor(() => _engine.LeaveOperational());
        _engine.GetState(1).Reason.Should().Be(SrdoInvalidReason.NotOperational);
        int events = _host.Log.Count;
        Advance(TimeSpan.FromMilliseconds(100));
        _engine.GetState(1).Reason.Should().Be(SrdoInvalidReason.NotOperational, "no deadline survives leaving Operational");
        _host.Log.Should().HaveCount(events, "no state event after the NotOperational one");
    }

    [Fact]
    public void A_Rebuild_Drops_The_Pending_First_Frame_And_Its_Srvt()
    {
        StartConsumer();
        Frame(0x123, Plain);
        OnActor(() => _engine.Rebuild(1));
        int events = _host.Log.Count;
        Advance(TimeSpan.FromMilliseconds(30));
        _host.States.Should().NotContain(s => s.Reason == SrdoInvalidReason.ValidationTimeExpired, "the old SRVT was cancelled by the rebuild");
        _host.Log.Count.Should().Be(events);
        Frame(0x124, Inverse);
        _engine.GetState(1).IsValid.Should().BeFalse();
        _host.States.Last().Reason.Should().Be(SrdoInvalidReason.OutOfOrder, "the pending first frame did not survive the rebuild");
    }

    [Fact]
    public void Second_Frame_Without_First_Is_OutOfOrder()
    {
        StartConsumer();
        Frame(0x124, Inverse);
        _host.States.Should().Contain((1, false, SrdoInvalidReason.OutOfOrder));
        _od.ReadUnsigned(0x2000, 0).Should().Be(0x1234u, "nothing was written");
    }

    [Fact]
    public void A_Wrong_Inverse_Is_A_Mismatch()
    {
        StartConsumer();
        Frame(0x123, Plain);
        Frame(0x124, 0x87, 0xA9, 0xFF);
        _host.States.Should().Contain((1, false, SrdoInvalidReason.Mismatch));
        _od.ReadUnsigned(0x2000, 0).Should().Be(0x1234u);
        Frame(0x123, Plain);
        Frame(0x124, 0x87, 0xA9);
        _host.States.Where(s => s.Reason == SrdoInvalidReason.Mismatch).Should().HaveCount(1, "the reason did not change, so no second event");
    }

    [Fact]
    public void Srvt_Expiry_Invalidates_And_Drops_The_Pending_Frame()
    {
        StartConsumer();
        Frame(0x123, Plain);
        Advance(TimeSpan.FromMilliseconds(19));
        _host.States.Should().NotContain(s => s.Reason == SrdoInvalidReason.ValidationTimeExpired);
        Advance(TimeSpan.FromMilliseconds(1));
        _host.States.Should().Contain((1, false, SrdoInvalidReason.ValidationTimeExpired));
        Frame(0x124, Inverse);
        _engine.GetState(1).IsValid.Should().BeFalse("the late second frame has no pending first frame");
        _host.States.Last().Reason.Should().Be(SrdoInvalidReason.OutOfOrder);
    }

    [Fact]
    public void Sct_Expiry_Invalidates_A_Valid_Srdo()
    {
        StartConsumer();
        Frame(0x123, Plain); Frame(0x124, Inverse);
        Advance(TimeSpan.FromMilliseconds(49));
        _engine.GetState(1).IsValid.Should().BeTrue();
        Advance(TimeSpan.FromMilliseconds(1));
        _engine.GetState(1).IsValid.Should().BeFalse();
        _engine.GetState(1).Reason.Should().Be(SrdoInvalidReason.SafeguardCycleExpired);
    }

    [Fact]
    public void Valid_Pair_After_Expiry_Rearms_Sct()
    {
        StartConsumer();
        Advance(TimeSpan.FromMilliseconds(50));
        _host.States.Should().Contain((1, false, SrdoInvalidReason.SafeguardCycleExpired));
        Frame(0x123, Plain); Frame(0x124, Inverse);
        _engine.GetState(1).IsValid.Should().BeTrue("the next valid pair re-validates");
        Advance(TimeSpan.FromMilliseconds(50));
        _host.States.Where(s => s.Reason == SrdoInvalidReason.SafeguardCycleExpired).Should().HaveCount(2, "the SCT was re-armed by the pair");
    }

    [Fact]
    public void Each_Pair_Restarts_The_Sct()
    {
        StartConsumer();
        Frame(0x123, Plain); Frame(0x124, Inverse);
        Advance(TimeSpan.FromMilliseconds(40));
        Frame(0x123, Plain); Frame(0x124, Inverse);
        Advance(TimeSpan.FromMilliseconds(40));
        _engine.GetState(1).IsValid.Should().BeTrue("80 ms since the first pair, 40 ms since the last");
    }

    [Fact]
    public void A_Second_First_Frame_Replaces_The_Pending_One()
    {
        StartConsumer();
        Frame(0x123, 0x00, 0x00, 0x00);
        Advance(TimeSpan.FromMilliseconds(15));
        Frame(0x123, Plain);
        Advance(TimeSpan.FromMilliseconds(15));
        _host.States.Should().NotContain(s => s.Reason == SrdoInvalidReason.ValidationTimeExpired, "the SRVT restarted with the second first frame");
        Frame(0x124, Inverse);
        _engine.GetState(1).IsValid.Should().BeTrue();
        _od.ReadUnsigned(0x2000, 0).Should().Be(0x5678u, "the later first frame is the one paired");
    }

    [Fact]
    public void A_Short_Pair_Raises_Emcy_8210h_Once_And_Is_Not_Processed()
    {
        StartConsumer();
        Frame(0x123, 0x78, 0x56); Frame(0x124, 0x87, 0xA9);
        _host.Emcys.Should().Equal((ushort)0x8210);
        _od.ReadUnsigned(0x2000, 0).Should().Be(0x1234u);
        _engine.GetState(1).IsValid.Should().BeFalse("SCT decides; the pair was not processed");
        Frame(0x123, 0x78, 0x56); Frame(0x124, 0x87, 0xA9);
        _host.Emcys.Should().HaveCount(1, "once per run of short frames");
        Frame(0x123, Plain); Frame(0x124, Inverse);
        _engine.GetState(1).IsValid.Should().BeTrue();
        Frame(0x123, 0x78, 0x56); Frame(0x124, 0x87, 0xA9);
        _host.Emcys.Should().HaveCount(2, "a frame that fits ends the run");
    }

    private void RefuseWritesTo(ushort index)
        => _od.WriteValidator = (i, _, _) => i == index ? OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded) : OdWriteDecision.Accept;

    /// <summary>Data the consumer cannot use counts as not received, as §8.1.3.1 leaves a short
    /// pair unprocessed. A pair whose second mapped object refuses its bytes makes the SRDO
    /// invalid with ActuationRefused instead of valid: no SrdoReceived, the refusal reported once, the object written before it stays
    /// written. The next pair the objects take makes it valid again.</summary>
    [Fact]
    public void A_Refused_Mapped_Write_Invalidates_With_ActuationRefused()
    {
        StartConsumer();
        Frame(0x123, Plain); Frame(0x124, Inverse);
        _engine.GetState(1).IsValid.Should().BeTrue();
        int statesBefore = _host.States.Count;
        RefuseWritesTo(0x2001);
        Frame(0x123, 0x22, 0x11, 0x02); Frame(0x124, 0xDD, 0xEE, 0xFD);
        _engine.GetState(1).IsValid.Should().BeFalse("a pair the mapped objects refused is not received");
        _engine.GetState(1).Reason.Should().Be(SrdoInvalidReason.ActuationRefused);
        _host.States.Should().HaveCount(statesBefore + 1, "one valid-to-invalid transition for the refused pair");
        _host.States.Last().Should().Be((1, false, SrdoInvalidReason.ActuationRefused));
        _host.Received.Should().ContainSingle("only the first pair was received").Which.Payload.Should().Equal(Plain);
        _host.Exceptions.Should().ContainSingle().Which.Message.Should().Contain("0x2001:00");
        _od.ReadUnsigned(0x2000, 0).Should().Be(0x1122u, "the entry before the refusal stays written");
        _od.ReadUnsigned(0x2001, 0).Should().Be(0x01u, "the refused entry keeps the value of the last pair");

        _od.WriteValidator = null;
        Frame(0x123, 0x22, 0x11, 0x03); Frame(0x124, 0xDD, 0xEE, 0xFC);
        _engine.GetState(1).IsValid.Should().BeTrue("a pair the objects take re-validates");
        _host.States.Should().HaveCount(statesBefore + 2, "one invalid-to-valid transition for the accepted pair");
        _host.States.Last().Should().Be((1, true, (SrdoInvalidReason?)null));
        _host.Received.Should().HaveCount(2);
        _od.ReadUnsigned(0x2001, 0).Should().Be(0x03u);
    }

    /// <summary>The first refusal ends the pair: the objects after it are not written.</summary>
    [Fact]
    public void A_Refused_Mapped_Write_Stops_The_Pair_At_The_Refusal()
    {
        StartConsumer();
        RefuseWritesTo(0x2000);
        Frame(0x123, Plain); Frame(0x124, Inverse);
        _engine.GetState(1).Reason.Should().Be(SrdoInvalidReason.ActuationRefused);
        _od.ReadUnsigned(0x2000, 0).Should().Be(0x1234u);
        _od.ReadUnsigned(0x2001, 0).Should().Be(0x5Au, "the entry after the refusal is not written");
        _host.Exceptions.Should().ContainSingle();
        _host.Received.Should().BeEmpty();
    }

    /// <summary>A refused pair is not received (as a short one, §8.1.3.1), so it does not restart the SCT — the SCT
    /// runs on from the last pair that was taken and, when it expires, replaces ActuationRefused
    /// with SafeguardCycleExpired.</summary>
    [Fact]
    public void A_Refused_Pair_Does_Not_Restart_The_Sct()
    {
        StartConsumer();
        Frame(0x123, Plain); Frame(0x124, Inverse);
        Advance(TimeSpan.FromMilliseconds(40));
        RefuseWritesTo(0x2001);
        Frame(0x123, Plain); Frame(0x124, Inverse);
        _engine.GetState(1).Reason.Should().Be(SrdoInvalidReason.ActuationRefused);
        Advance(TimeSpan.FromMilliseconds(10));
        _engine.GetState(1).Reason.Should().Be(SrdoInvalidReason.SafeguardCycleExpired, "50 ms since the last pair taken");
    }

    [Fact]
    public void A_Long_Pair_Uses_The_First_Mapped_Bytes()
    {
        StartConsumer();
        Frame(0x123, 0x78, 0x56, 0x01, 0xAA); Frame(0x124, 0x87, 0xA9, 0xFE, 0x55);
        _engine.GetState(1).IsValid.Should().BeTrue();
        _od.ReadUnsigned(0x2001, 0).Should().Be(0x01u);
    }

    [Fact]
    public void Frames_On_Own_Transmit_CobIds_Are_Ignored()
    {
        Populate(_od, NodeId, 2);
        Configure(_od, 1, SrdoDirection.Transmit, 25, 20, 0x123, 0x124);
        Configure(_od, 2, SrdoDirection.Receive, 50, 20, 0x125, 0x126);
        _engine = new SrdoEngine(_actor, _clock, _deadlines, _od, NodeId, 2, _host);
        OnActor(() => { _engine.Rebuild(1); _engine.Rebuild(2); _engine.EnterOperational(); });
        Frame(0x124, Inverse).Should().BeTrue("an echo of our own second frame is ours to swallow");
        _host.States.Should().NotContain(s => s.Reason == SrdoInvalidReason.OutOfOrder);
        Frame(0x127, Plain).Should().BeFalse("not an SRDO of this node");
    }

    [Fact]
    public void Rtr_And_Not_Operational_Are_Swallowed_Without_Effect()
    {
        StartConsumer();
        bool consumed = false;
        OnActor(() => consumed = _engine.TryHandleFrame(0x123, Array.Empty<byte>(), isRtr: true));
        consumed.Should().BeTrue("§8.1: RTR is not possible — nothing answers it");
        OnActor(() => _engine.LeaveOperational());
        Frame(0x123, Plain).Should().BeTrue();
        Frame(0x124, Inverse).Should().BeTrue();
        _engine.GetState(1).Reason.Should().Be(SrdoInvalidReason.NotOperational);
        _od.ReadUnsigned(0x2000, 0).Should().Be(0x1234u);
    }

    [Fact]
    public void Gfc_Is_Received_And_Sent_Only_While_1300h_Is_1_And_Operational()
    {
        StartConsumer();
        Frame(CanOpenCobId.GlobalFailsafeCommand).Should().BeTrue("001h is a safety id even when ignored");
        _host.Gfcs.Should().Be(0, "1300h = 0: GFC is not valid");
        bool sent = true;
        OnActor(() => sent = _engine.TrySendGfc());
        sent.Should().BeFalse();
        _od.WriteUnsigned(SrdoRecords.GfcParameter, 0, 1);
        Frame(CanOpenCobId.GlobalFailsafeCommand);
        _host.Gfcs.Should().Be(1);
        Frame(CanOpenCobId.GlobalFailsafeCommand, 0x00);
        _host.Gfcs.Should().Be(1, "§8.2.3: L = 0");
        OnActor(() => sent = _engine.TrySendGfc());
        sent.Should().BeTrue();
        _host.Sent.Should().ContainSingle().Which.Should().Be((CanOpenCobId.GlobalFailsafeCommand, Array.Empty<byte>()));
        OnActor(() => _engine.LeaveOperational());
        OnActor(() => sent = _engine.TrySendGfc());
        sent.Should().BeFalse();
    }
}
