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

    private bool FdFrame(uint cobId, params byte[] data)
    {
        bool consumed = false;
        OnActor(() => consumed = _engine.TryHandleFrame(cobId, data, isRtr: false, isFd: true));
        return consumed;
    }

    private static byte[] Padded(byte[] data, int length, byte fill)
    {
        var padded = new byte[length];
        for (int i = 0; i < length; i++) padded[i] = i < data.Length ? data[i] : fill;
        return padded;
    }

    /// <summary>An SRDO frame is a classic CAN frame (§8.1): a payload longer than 8 bytes, or a
    /// CAN FD frame, on COB-ID 1 or 2 is not an SRDO frame. It is rejected as a wrong inverse is —
    /// Mismatch, the pending first frame dropped, nothing actuated, no SrdoReceived, the SCT not
    /// restarted, and no EMCY 8210h (that is for a short pair). §8.1.3.1's "only the first 'n'
    /// bytes are used" applies within 8 bytes. A later correct pair makes the SRDO valid again.</summary>
    [Theory]
    [InlineData("9-byte first")]
    [InlineData("9-byte second")]
    [InlineData("9-byte pair")]
    [InlineData("FD pair")]
    public void A_Frame_That_Is_Not_Classic_Can_Is_No_Srdo_Frame(string fault)
    {
        StartConsumer();
        Frame(0x123, Plain); Frame(0x124, Inverse);   // valid, SCT from here
        Advance(TimeSpan.FromMilliseconds(40));
        int received = _host.Received.Count;
        byte[] plain2 = { 0x22, 0x11, 0x02 }, inverse2 = { 0xDD, 0xEE, 0xFD };
        switch (fault)
        {
            case "9-byte first":
                Frame(0x123, Padded(plain2, 9, 0x00)).Should().BeTrue();
                break;
            case "9-byte second":
                Frame(0x123, plain2);
                Frame(0x124, Padded(inverse2, 9, 0xFF)).Should().BeTrue();
                break;
            case "9-byte pair":
                Frame(0x123, Padded(plain2, 9, 0x00)).Should().BeTrue();
                Frame(0x124, Padded(inverse2, 9, 0xFF)).Should().BeTrue();
                break;
            default:
                FdFrame(0x123, plain2).Should().BeTrue();
                FdFrame(0x124, inverse2).Should().BeTrue();
                break;
        }
        _engine.GetState(1).IsValid.Should().BeFalse();
        _host.States.Should().Contain((1, false, SrdoInvalidReason.Mismatch));
        _od.ReadUnsigned(0x2000, 0).Should().Be(0x5678u, "nothing of the rejected frames was actuated");
        _od.ReadUnsigned(0x2001, 0).Should().Be(0x01u);
        _host.Received.Should().HaveCount(received, "no SrdoReceived");
        _host.Emcys.Should().BeEmpty("8210h is for a pair shorter than the mapping");

        Frame(0x124, inverse2);
        _engine.GetState(1).Reason.Should().Be(SrdoInvalidReason.OutOfOrder, "no first frame is pending");
        Advance(TimeSpan.FromMilliseconds(10));
        _engine.GetState(1).Reason.Should().Be(SrdoInvalidReason.SafeguardCycleExpired, "50 ms since the last pair taken: the SCT was not restarted");

        Frame(0x123, plain2); Frame(0x124, inverse2);
        _engine.GetState(1).IsValid.Should().BeTrue("a correct pair re-validates");
        _od.ReadUnsigned(0x2000, 0).Should().Be(0x1122u);
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

    /// <summary>FR-CO-038, the consumer half (§8.3.1 step D: "in case of mismatch ... the safety
    /// controller shall enter (stay) in safe state"): a consumer whose checksum does not match
    /// its record reports ConfigurationInvalid on entering Operational, and a correct pair on
    /// its COB-IDs is swallowed — consumed as the SRDO's frames, nothing actuated, no
    /// SrdoReceived, no state change, no SCT running — where a valid consumer would take it
    /// (<see cref="Starts_Invalid_And_Becomes_Valid_With_A_Complete_Pair"/>). Mutation safety-M1
    /// of the 2026-10-21 review (the ConfigurationValid gate of TryHandleFrame removed) is what
    /// this test catches (#344).</summary>
    [Fact]
    public void A_Consumer_With_An_Invalid_Configuration_Reports_ConfigurationInvalid_And_Takes_No_Pair()
    {
        Populate(_od, NodeId, 2);
        Configure(_od, 1, SrdoDirection.Receive, 50, 20, 0x123, 0x124);
        _od.WriteUnsigned(SrdoRecords.Checksum, 1, 0xFFFF); // tampered: 13FEh stays A5h, the checksum does not match
        SrdoRecords.IsConfigurationValid(_od, 1).Should().BeFalse();
        _engine = new SrdoEngine(_actor, _clock, _deadlines, _od, NodeId, 2, _host);
        OnActor(() => { _engine.Rebuild(1); _engine.Rebuild(2); _engine.EnterOperational(); });
        Settle();
        _engine.GetState(1).IsValid.Should().BeFalse();
        _engine.GetState(1).Reason.Should().Be(SrdoInvalidReason.ConfigurationInvalid);
        _host.States.Should().Contain((1, false, SrdoInvalidReason.ConfigurationInvalid));
        int states = _host.States.Count;

        Frame(0x123, Plain).Should().BeTrue("the frame is on the SRDO's COB-ID: consumed, not passed on");
        Frame(0x124, Inverse).Should().BeTrue();
        _engine.GetState(1).IsValid.Should().BeFalse("a correct pair does not validate an SRDO whose configuration is invalid");
        _engine.GetState(1).Reason.Should().Be(SrdoInvalidReason.ConfigurationInvalid);
        _od.ReadUnsigned(0x2000, 0).Should().Be(0x1234u, "nothing was actuated");
        _od.ReadUnsigned(0x2001, 0).Should().Be(0x5Au, "nothing was actuated");
        _host.Received.Should().BeEmpty("no SrdoReceived");
        _host.States.Should().HaveCount(states, "no state change");

        Advance(TimeSpan.FromMilliseconds(60));
        _engine.GetState(1).Reason.Should().Be(SrdoInvalidReason.ConfigurationInvalid, "no SCT runs for a consumer that was never armed");
        _host.States.Should().HaveCount(states);
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
        _engine.GetState(1).IsValid.Should().BeFalse("the pending first frame did not survive the rebuild: the inverted frame completes no pair");
        _od.ReadUnsigned(0x2000, 0).Should().Be(0x1234u, "nothing was written");
        _engine.GetState(1).Reason.Should().Be(SrdoInvalidReason.NotReceived, "as re-armed by the rebuild; before its first pair a lone inverted frame is no OutOfOrder");
        _host.Log.Count.Should().Be(events, "no event for the lone inverted frame");
    }

    /// <summary>§9.5 "received in chronological order" is a rule about a pair. A lone inverted
    /// frame after a pair was taken has a plain frame missing before it: OutOfOrder, one
    /// transition, nothing written. (Before the first pair it is no pair of this consumer's:
    /// <see cref="A_Lone_Inverted_Frame_Before_The_First_Pair_Is_No_Pair_And_No_Transition"/>.)</summary>
    [Fact]
    public void Second_Frame_Without_First_Is_OutOfOrder()
    {
        StartConsumer();
        Frame(0x123, Plain); Frame(0x124, Inverse);   // valid: a pair was taken
        Frame(0x124, Inverse).Should().BeTrue();
        _engine.GetState(1).IsValid.Should().BeFalse();
        _engine.GetState(1).Reason.Should().Be(SrdoInvalidReason.OutOfOrder);
        _host.States.Should().Contain((1, false, SrdoInvalidReason.OutOfOrder));
        _od.ReadUnsigned(0x2000, 0).Should().Be(0x5678u, "nothing of the lone frame was written");
    }

    /// <summary>S-7 of the 2026-10-21 review (#360): a consumer that enters Operational between a
    /// producer's plain and inverted frame — a network started with one NMT Start to node 0, the
    /// producer already running — sees the inverted frame first. It belongs to no pair of this
    /// consumer's, and §9.5's chronological-order rule is about a pair: while the SRDO is still
    /// NotReceived as armed the frame is consumed without a transition — no OutOfOrder event,
    /// nothing written, the SCT running on from the arming, not restarted — and the next pair
    /// makes the SRDO valid with the one transition the start-up has anyway.</summary>
    [Fact]
    public void A_Lone_Inverted_Frame_Before_The_First_Pair_Is_No_Pair_And_No_Transition()
    {
        StartConsumer();
        int events = _host.Log.Count;
        Frame(0x124, Inverse).Should().BeTrue("the frame is on the SRDO's COB-ID 2: consumed, not passed on");
        _engine.GetState(1).IsValid.Should().BeFalse();
        _engine.GetState(1).Reason.Should().Be(SrdoInvalidReason.NotReceived, "no pair, no violation");
        _host.Log.Count.Should().Be(events, "no transition, no event");
        _od.ReadUnsigned(0x2000, 0).Should().Be(0x1234u, "nothing was written");

        Advance(TimeSpan.FromMilliseconds(49));
        _engine.GetState(1).Reason.Should().Be(SrdoInvalidReason.NotReceived);
        Advance(TimeSpan.FromMilliseconds(1));
        _engine.GetState(1).Reason.Should().Be(SrdoInvalidReason.SafeguardCycleExpired, "the SCT runs on from the arming: the lone frame did not restart it");
        _host.States.Should().NotContain(s => s.Reason == SrdoInvalidReason.OutOfOrder);

        OnActor(() => { _engine.LeaveOperational(); _engine.EnterOperational(); }); // armed again: NotReceived
        Frame(0x124, Inverse);
        Frame(0x123, Plain); Frame(0x124, Inverse);
        _engine.GetState(1).IsValid.Should().BeTrue("the next pair validates");
        _od.ReadUnsigned(0x2000, 0).Should().Be(0x5678u);
        _host.States.Should().NotContain(s => s.Reason == SrdoInvalidReason.OutOfOrder);
        _host.States.Where(s => s.Srdo == 1).Select(s => s.Reason).Should().Equal(
            SrdoInvalidReason.NotOperational, SrdoInvalidReason.NotReceived, SrdoInvalidReason.SafeguardCycleExpired, // the two lone frames: no entry
            SrdoInvalidReason.NotOperational, SrdoInvalidReason.NotReceived, null);
    }

    /// <summary>Codex on #380: a pair shorter than the mapping is not processed (§8.1.3.1) and
    /// leaves the SRDO NotReceived, but it is a pair — the producer's order has been seen, so the
    /// start-up exemption of <see cref="A_Lone_Inverted_Frame_Before_The_First_Pair_Is_No_Pair_And_No_Transition"/>
    /// is over: a lone inverted frame after it is OutOfOrder, as after any other pair. Arming
    /// again starts a new period, in which a lone inverted frame is again no pair.</summary>
    [Fact]
    public void A_Lone_Inverted_Frame_After_A_Short_Pair_Is_OutOfOrder()
    {
        StartConsumer();
        Frame(0x123, 0x78, 0x56); Frame(0x124, 0x87, 0xA9);   // short: EMCY 8210h, not processed
        _host.Emcys.Should().Equal((ushort)0x8210);
        _engine.GetState(1).Reason.Should().Be(SrdoInvalidReason.NotReceived, "a short pair is not processed");
        Frame(0x124, Inverse).Should().BeTrue();
        _engine.GetState(1).Reason.Should().Be(SrdoInvalidReason.OutOfOrder, "a pair has been seen: this frame's plain frame is missing");
        _host.States.Should().Contain((1, false, SrdoInvalidReason.OutOfOrder));
        _od.ReadUnsigned(0x2000, 0).Should().Be(0x1234u, "nothing was written");

        OnActor(() => { _engine.LeaveOperational(); _engine.EnterOperational(); }); // armed again: NotReceived
        int events = _host.Log.Count;
        Frame(0x124, Inverse);
        _engine.GetState(1).Reason.Should().Be(SrdoInvalidReason.NotReceived, "arming again forgets the pair seen before it");
        _host.Log.Count.Should().Be(events, "no transition, no event");
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

    /// <summary>The GFC is a classic CAN frame with DLC 0 (§8.2): a CAN FD frame on 001h, even
    /// with no data, is not a GFC and raises nothing; the classic one does.</summary>
    [Fact]
    public void A_Can_Fd_Frame_On_001h_Is_No_Gfc()
    {
        StartConsumer();
        _od.WriteUnsigned(SrdoRecords.GfcParameter, 0, 1);
        FdFrame(CanOpenCobId.GlobalFailsafeCommand).Should().BeTrue("001h is a safety id even when ignored");
        _host.Gfcs.Should().Be(0, "a CAN FD frame is no GFC");
        Frame(CanOpenCobId.GlobalFailsafeCommand);
        _host.Gfcs.Should().Be(1, "the classic frame with DLC 0 is");
    }

    [Fact]
    public void Gfc_Is_Received_And_Sent_Only_While_Operational_And_Is_Recorded_In_1300h()
    {
        StartConsumer();
        _od.ReadUnsigned(SrdoRecords.GfcParameter, 0).Should().Be(0u);
        // EN 50325-5:2010 §7.2.1 (OBE-12450): the consumer signals whatever 1300h holds; §6.4.2.1
        // (OBE-12439) it is the indication that sets 1300h to 01, not 01 that admits the indication.
        Frame(CanOpenCobId.GlobalFailsafeCommand).Should().BeTrue();
        _host.Gfcs.Should().Be(1, "1300h = 0 does not gate the reception");
        _od.ReadUnsigned(SrdoRecords.GfcParameter, 0).Should().Be(1u, "OBE-12439");
        _od.WriteUnsigned(SrdoRecords.GfcParameter, 0, 0);
        Frame(CanOpenCobId.GlobalFailsafeCommand, 0x00);
        _host.Gfcs.Should().Be(1, "§8.2.3: L = 0");
        _od.ReadUnsigned(SrdoRecords.GfcParameter, 0).Should().Be(0u, "a frame that is no GFC records nothing");
        bool sent = false;
        OnActor(() => sent = _engine.TrySendGfc());
        sent.Should().BeTrue("1300h = 0 does not gate the request either");
        _host.Sent.Should().ContainSingle().Which.Should().Be((CanOpenCobId.GlobalFailsafeCommand, Array.Empty<byte>()));
        _od.ReadUnsigned(SrdoRecords.GfcParameter, 0).Should().Be(1u, "OBE-12437");
        _od.WriteUnsigned(SrdoRecords.GfcParameter, 0, 0);
        OnActor(() => _engine.LeaveOperational());
        OnActor(() => sent = _engine.TrySendGfc());
        sent.Should().BeFalse();
        Frame(CanOpenCobId.GlobalFailsafeCommand);
        _host.Gfcs.Should().Be(1, "outside Operational nothing is signalled");
        _od.ReadUnsigned(SrdoRecords.GfcParameter, 0).Should().Be(0u, "and nothing is recorded");
    }

    /// <summary>1300h is an optional object (EN 50325-5:2010 §6.4.2.1): a node whose description
    /// leaves it out has nothing to record, and the GFC still goes both ways.</summary>
    [Fact]
    public void A_Gfc_Works_On_A_Node_Without_1300h()
    {
        StartConsumer();
        _od.Remove(SrdoRecords.GfcParameter, 0).Should().BeTrue();
        Frame(CanOpenCobId.GlobalFailsafeCommand);
        _host.Gfcs.Should().Be(1);
        bool sent = false;
        OnActor(() => sent = _engine.TrySendGfc());
        sent.Should().BeTrue();
        _host.Exceptions.Should().BeEmpty("there was nothing to set");
    }

    /// <summary>A dictionary that refuses the record write does not stop the command: the
    /// reception is still signalled and the refusal reported, once per attempt.</summary>
    [Fact]
    public void A_Refused_Write_Of_1300h_Is_Reported_And_Does_Not_Stop_The_Gfc()
    {
        StartConsumer();
        RefuseWritesTo(SrdoRecords.GfcParameter);
        Frame(CanOpenCobId.GlobalFailsafeCommand);
        _host.Gfcs.Should().Be(1);
        bool sent = false;
        OnActor(() => sent = _engine.TrySendGfc());
        sent.Should().BeTrue();
        _host.Exceptions.Should().HaveCount(2).And.OnlyContain(e => e.Message.Contains("1300h"));
        _od.ReadUnsigned(SrdoRecords.GfcParameter, 0).Should().Be(0u);
    }
}
