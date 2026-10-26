using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Pro.CANopen.Pdo;
using CanKit.Pro.CANopen.Safety;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Safety;

/// <summary>FR-CO-061: passive capture, deterministic pairing/deadlines and explicit input quality.</summary>
public class ForeignSrdoMonitorTests : IClassFixture<VirtualAdapterFixture>
{
    private static readonly TimeSpan Srvt = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan Sct = TimeSpan.FromMilliseconds(100);
    private static ForeignSrdoConfiguration Config(byte peer = 5, int number = 1, uint id = 0x101,
        TimeSpan? srvt = null, TimeSpan? sct = null, IEnumerable<PdoMappingEntry>? mapping = null)
        => new(peer, number, id, id + 1, mapping ?? new[] { new PdoMappingEntry(0x2000, 1, 16) },
            srvt ?? Srvt, sct ?? Sct, "test DCF revision 7");

    private sealed class Capture : ISubscription
    {
        internal readonly Queue<SubscriptionFrame> Queue = new();
        internal ulong Next = 1;
        internal long Drops;
        internal bool Disposed;
        internal Action? OnRead;
        internal void Add(uint id, byte[] payload, long time, FrameFlags flags = FrameFlags.None,
            CanFrameType kind = CanFrameType.Can20, bool echo = false)
            => Queue.Enqueue(new SubscriptionFrame(new CanFrameEvent(new CanFrameView(kind, (int)id, payload, flags),
                echo, TimeSpan.FromTicks(time * 2), time), Next++));
        public bool TryRead(out CanFrameEvent frameEvent)
        {
            bool result = Read(out var envelope);
            frameEvent = envelope.FrameEvent;
            return result;
        }
        internal bool Read(out SubscriptionFrame frame)
        {
            OnRead?.Invoke();
            if (Queue.Count == 0) { frame = default; return false; }
            frame = Queue.Dequeue(); return true;
        }
        public IAsyncEnumerable<CanFrameEvent> Frames => throw new NotSupportedException();
        public ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) => new(Queue.Count > 0);
        public void Reconfigure(CanIdFilter filter) => throw new NotSupportedException();
        public void Reconfigure(Func<CanFrameEvent, bool>? predicate) => throw new NotSupportedException();
        public void Dispose() => Disposed = true;
    }

    private sealed class OrderedCapture(Capture capture) : ILossAwareSubscription
    {
        public long DroppedFrameCount => capture.Drops;
        public bool TryReadWithSequence(out SubscriptionFrame frame) => capture.Read(out frame);
        public IAsyncEnumerable<SubscriptionFrame> FramesWithSequence => throw new NotSupportedException();
        public bool TryRead(out CanFrameEvent frameEvent) => capture.TryRead(out frameEvent);
        public IAsyncEnumerable<CanFrameEvent> Frames => capture.Frames;
        public ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) => capture.WaitToReadAsync(cancellationToken);
        public void Reconfigure(CanIdFilter filter) => capture.Reconfigure(filter);
        public void Reconfigure(Func<CanFrameEvent, bool>? predicate) => capture.Reconfigure(predicate);
        public void Dispose() => capture.Dispose();
    }

    private sealed class Rig : IDisposable
    {
        internal readonly ManualTimeSource Clock = new();
        internal readonly Capture Input = new();
        internal readonly ForeignSrdoMonitor Monitor;
        internal Rig(bool ordered = true, params ForeignSrdoConfiguration[] configurations)
        {
            Clock.Advance(TimeSpan.FromSeconds(1));
            Monitor = new ForeignSrdoMonitor(ordered ? new OrderedCapture(Input) : Input, "CAN-A",
                configurations.Length == 0 ? new[] { Config() } : configurations, true, Clock);
        }
        internal long At(double milliseconds) => TimeSpan.TicksPerSecond + TimeSpan.FromMilliseconds(milliseconds).Ticks;
        internal void Until(double milliseconds) => Clock.Advance(TimeSpan.FromTicks(At(milliseconds) - Clock.GetTimestamp()));
        internal void Add(uint id, double milliseconds, params byte[] bytes) => Input.Add(id, bytes, At(milliseconds));
        internal void Pair(double first = 1, double second = 2, uint id = 0x101)
        { Add(id, first, 0x12, 0x34); Add(id + 1, second, 0xED, 0xCB); }
        internal ForeignSrdoState State() => Monitor.Poll().States.Single();
        public void Dispose() => Monitor.Dispose();
    }

    [Fact]
    public void Delayed_Poll_Uses_Capture_Time_And_Returns_Immutable_Values_And_Provenance()
    {
        using var rig = new Rig();
        var initial = rig.State();
        initial.Status.Should().Be(ForeignSrdoStatus.NotReceived);
        initial.LastValidTimestamp.Should().BeNull(); initial.Age.Should().BeNull(); initial.Signals.Should().BeEmpty();
        rig.Pair(); rig.Until(50);
        var batch = rig.Monitor.Poll();
        batch.Observations.Select(x => x.Status).Should().Equal(ForeignSrdoStatus.WaitingForInverted, ForeignSrdoStatus.Valid);
        var valid = batch.States.Single();
        valid.IsCurrent.Should().BeTrue(); valid.Age.Should().Be(TimeSpan.FromMilliseconds(48));
        valid.AsOfTimestamp.Should().Be(rig.At(50));
        valid.Configuration.Provenance.Should().Be("test DCF revision 7");
        valid.Signals.Single().Value.Should().Equal(0x12, 0x34);
        valid.Signals.Single().Index.Should().Be(0x2000); valid.Signals.Single().Subindex.Should().Be(1);
        var item = batch.Observations.Last();
        item.BusIdentity.Should().Be("CAN-A"); item.HostTimestamp.Should().Be(rig.At(2));
        item.ReceiveTimestamp.Should().Be(TimeSpan.FromTicks(rig.At(2) * 2)); item.SequenceNumber.Should().Be(2);
        item.TimingAvailable.Should().BeTrue(); item.Configuration.Should().BeSameAs(valid.Configuration);
        item.CaptureQuality.Should().Be(ForeignSrdoCaptureQuality.Contiguous);
        item.EvaluationPeriodId.Should().Be(valid.EvaluationPeriodId);
        ((IList<byte>)valid.Signals.Single().Value).Invoking(x => x[0] = 0).Should().Throw<NotSupportedException>();
        rig.Until(102);
        var expired = rig.State();
        expired.Status.Should().Be(ForeignSrdoStatus.SafeguardCycleExpired);
        expired.IsCurrent.Should().BeFalse(); expired.Signals.Should().BeEmpty(); expired.LastValidTimestamp.Should().Be(rig.At(2));
        valid.IsCurrent.Should().BeTrue("retained results are immutable historical snapshots");
    }

    [Fact]
    public void Interleaved_Srdos_And_Buses_Have_Independent_Pairs_And_Deadlines()
    {
        using var a = new Rig(true, Config(), Config(7, 2, 0x103, sct: TimeSpan.FromMilliseconds(30)));
        using var b = new Rig();
        a.Add(0x101, 1, 1, 2); a.Add(0x103, 2, 3, 4); a.Add(0x102, 3, 254, 253); a.Add(0x104, 4, 252, 251);
        a.Until(35); b.Until(35);
        var batch = a.Monitor.Poll();
        batch.States[0].IsCurrent.Should().BeTrue();
        batch.States[1].Status.Should().Be(ForeignSrdoStatus.SafeguardCycleExpired);
        b.State().Status.Should().Be(ForeignSrdoStatus.NotReceived);
        batch.Observations.Last().HostTimestamp.Should().Be(a.At(34));
    }

    [Fact]
    public void Startup_Inverted_Repeated_Normal_Mismatch_Order_And_Recovery_Are_Explicit()
    {
        using var rig = new Rig();
        rig.Add(0x102, 1, 0); rig.Add(0x102, 2, 0);
        rig.Add(0x101, 3, 1, 2); rig.Add(0x101, 12, 3, 4); rig.Add(0x102, 13, 252, 251);
        rig.Add(0x101, 14, 1, 2); rig.Add(0x102, 15, 0, 0); rig.Add(0x102, 16, 0, 0);
        rig.Pair(17, 18); rig.Until(18);
        var batch = rig.Monitor.Poll();
        batch.Observations.Select(x => x.Status).Should().Equal(ForeignSrdoStatus.StartupInverted, ForeignSrdoStatus.StartupInverted,
            ForeignSrdoStatus.WaitingForInverted, ForeignSrdoStatus.WaitingForInverted, ForeignSrdoStatus.Valid,
            ForeignSrdoStatus.WaitingForInverted, ForeignSrdoStatus.Mismatch, ForeignSrdoStatus.OutOfOrder,
            ForeignSrdoStatus.WaitingForInverted, ForeignSrdoStatus.Valid);
        batch.Observations.Where(x => x.Status != ForeignSrdoStatus.Valid).Should().OnlyContain(x => x.Signals.Count == 0);
        batch.States.Single().IsCurrent.Should().BeTrue();
    }

    [Theory]
    [InlineData(9, ForeignSrdoStatus.Valid)]
    [InlineData(10, ForeignSrdoStatus.OutOfOrder)]
    [InlineData(11, ForeignSrdoStatus.OutOfOrder)]
    public void Srvt_Boundary_Is_Capture_Based_And_Expired_Pending_Data_Is_Not_Used(int delay, ForeignSrdoStatus expected)
    {
        using var rig = new Rig();
        rig.Pair(1, 1 + delay); rig.Until(50);
        var result = rig.Monitor.Poll();
        result.States.Single().Status.Should().Be(expected);
        if (delay >= 10)
        {
            result.Observations.Should().Contain(x => x.Status == ForeignSrdoStatus.ValidationTimeExpired && x.HostTimestamp == rig.At(11));
            result.States.Single().Signals.Should().BeEmpty();
        }
    }

    [Fact]
    public void Sct_And_Srvt_Expire_Once_In_Chronological_Order_With_Sct_First_On_Tie()
    {
        using var rig = new Rig(true, Config(sct: TimeSpan.FromMilliseconds(11)));
        rig.Add(0x101, 1, 1, 2); rig.Until(11);
        rig.Monitor.Poll().Observations.Select(x => x.Status).Should().Equal(ForeignSrdoStatus.WaitingForInverted, ForeignSrdoStatus.SafeguardCycleExpired);
        rig.Monitor.Poll().Observations.Should().BeEmpty();
        rig.Pair(12, 13); rig.Until(13); rig.State().IsCurrent.Should().BeTrue();
        rig.Until(24); rig.State().Status.Should().Be(ForeignSrdoStatus.SafeguardCycleExpired);
    }

    [Fact]
    public void Mismatch_And_Short_Pairs_Do_Not_Reset_Sct_Or_Keep_Old_Values()
    {
        using var rig = new Rig();
        rig.Pair(); rig.Until(2); rig.State().IsCurrent.Should().BeTrue();
        rig.Add(0x101, 90, 1); rig.Add(0x102, 91, 254); rig.Add(0x102, 92, 254);
        rig.Add(0x101, 93, 1, 2); rig.Add(0x102, 94, 254);
        rig.Until(102);
        var batch = rig.Monitor.Poll();
        batch.Observations.Select(x => x.Status).Should().ContainInOrder(ForeignSrdoStatus.ShortPayload, ForeignSrdoStatus.OutOfOrder,
            ForeignSrdoStatus.Mismatch, ForeignSrdoStatus.SafeguardCycleExpired);
        batch.States.Single().Signals.Should().BeEmpty();
    }

    [Theory]
    [InlineData(FrameFlags.Ext, CanFrameType.Can20, 2)]
    [InlineData(FrameFlags.Rtr, CanFrameType.Can20, 2)]
    [InlineData(FrameFlags.Error, CanFrameType.Can20, 2)]
    [InlineData(FrameFlags.None, CanFrameType.CanFd, 2)]
    [InlineData(FrameFlags.None, CanFrameType.Can20, 9)]
    public void Invalid_Srdo_Format_Discards_Pending_And_Current(FrameFlags flags, CanFrameType kind, int length)
    {
        using var rig = new Rig();
        rig.Add(0x101, 1, 1, 2);
        rig.Input.Add(0x102, new byte[length], rig.At(2), flags, kind);
        rig.Until(2);
        var state = rig.State();
        state.Status.Should().Be(ForeignSrdoStatus.InvalidFormat); state.Signals.Should().BeEmpty();
    }

    [Fact]
    public void Extra_Bytes_Within_Classic_Limit_Are_Checked_But_Only_Mapped_Bytes_Are_Decoded()
    {
        using var rig = new Rig();
        rig.Add(0x101, 1, 1, 2, 3); rig.Add(0x102, 2, 254, 253, 252); rig.Until(2);
        rig.State().Signals.Single().Value.Should().Equal(1, 2);
    }

    [Theory]
    [InlineData(FrameFlags.None, CanFrameType.Can20, 0, ForeignSrdoStatus.GfcObserved)]
    [InlineData(FrameFlags.Ext, CanFrameType.Can20, 0, ForeignSrdoStatus.MalformedGfc)]
    [InlineData(FrameFlags.Rtr, CanFrameType.Can20, 0, ForeignSrdoStatus.MalformedGfc)]
    [InlineData(FrameFlags.Error, CanFrameType.Can20, 0, ForeignSrdoStatus.MalformedGfc)]
    [InlineData(FrameFlags.None, CanFrameType.CanFd, 0, ForeignSrdoStatus.MalformedGfc)]
    [InlineData(FrameFlags.None, CanFrameType.Can20, 1, ForeignSrdoStatus.MalformedGfc)]
    public void Every_Gfc_Candidate_Is_Preserved_Without_Node_Attribution_Or_Srdo_Configuration(
        FrameFlags flags, CanFrameType kind, int length, ForeignSrdoStatus expected)
    {
        var clock = new ManualTimeSource(); clock.Advance(TimeSpan.FromSeconds(1));
        var input = new Capture();
        using var monitor = new ForeignSrdoMonitor(new OrderedCapture(input), "CAN-B", Array.Empty<ForeignSrdoConfiguration>(), true, clock);
        input.Add(1, new byte[length], clock.GetTimestamp(), flags, kind, echo: true);
        input.Add(1, new byte[length], 0, flags, kind);
        var batch = monitor.Poll();
        batch.States.Should().BeEmpty(); batch.Observations.Should().HaveCount(2);
        batch.Observations.Should().OnlyContain(x => x.Status == expected && x.Configuration == null && x.BusIdentity == "CAN-B");
        batch.Observations[0].TimingAvailable.Should().BeTrue(); batch.Observations[1].TimingAvailable.Should().BeFalse();
        batch.Observations[1].SequenceNumber.Should().Be(2);
    }

    [Fact]
    public void Subscription_Gap_Prevents_Cross_Gap_Pairs_And_Fresh_Pair_Recovers_Only_Current_Window()
    {
        using var rig = new Rig();
        rig.Add(0x101, 1, 1, 2); rig.Until(1); var before = rig.State();
        rig.Input.Next++; rig.Input.Drops++;
        rig.Add(0x102, 2, 254, 253); rig.Pair(3, 4); rig.Until(4);
        var batch = rig.Monitor.Poll();
        batch.Observations.Should().Contain(x => x.Status == ForeignSrdoStatus.CaptureGap);
        batch.Observations.Should().NotContain(x => x.Status == ForeignSrdoStatus.Valid && x.HostTimestamp == rig.At(2));
        batch.CaptureQuality.Should().Be(ForeignSrdoCaptureQuality.Incomplete); batch.DroppedFrameCount.Should().Be(1);
        var recovered = batch.States.Single();
        recovered.CaptureQuality.Should().Be(ForeignSrdoCaptureQuality.Contiguous); recovered.IsCurrent.Should().BeTrue();
        recovered.Configuration.Should().BeSameAs(before.Configuration);
        recovered.EvaluationPeriodId.Should().NotBe(before.EvaluationPeriodId);
        rig.Until(104);
        rig.State().Status.Should().Be(ForeignSrdoStatus.SafeguardCycleExpired);
    }

    [Fact]
    public void Counter_Only_Or_Concurrent_Loss_Invalidates_Even_A_Reconstructed_Pair()
    {
        using var rig = new Rig();
        rig.Pair(); rig.Until(2);
        int reads = 0;
        rig.Input.OnRead = () => { if (++reads == 2) rig.Input.Drops++; };
        var gap = rig.Monitor.Poll();
        gap.States.Single().Status.Should().Be(ForeignSrdoStatus.CaptureGap);
        gap.States.Single().Signals.Should().BeEmpty();
        rig.Input.OnRead = null;
        rig.Input.Drops++; rig.Pair(3, 4); rig.Until(4);
        rig.State().Status.Should().Be(ForeignSrdoStatus.CaptureGap);
        rig.Pair(5, 6); rig.Until(6); rig.State().IsCurrent.Should().BeTrue();
    }

    [Fact]
    public void First_Sequence_Gap_And_Saturated_Counter_Still_Expose_Loss()
    {
        using var rig = new Rig();
        rig.Input.Next = 4; rig.Input.Drops = long.MaxValue;
        rig.Pair(); rig.Until(2); rig.Monitor.Poll().CaptureQuality.Should().Be(ForeignSrdoCaptureQuality.Incomplete);
        rig.Add(0x101, 3, 1, 2); rig.Input.Next++; rig.Add(0x102, 4, 254, 253); rig.Until(4);
        rig.State().IsCurrent.Should().BeFalse();
    }

    [Fact]
    public void Sequence_Wrap_Is_Contiguous_And_Deadline_Order_Does_Not_Follow_Configuration_Order()
    {
        using var rig = new Rig(true, Config(sct: TimeSpan.FromMilliseconds(100)),
            Config(6, 2, 0x103, sct: TimeSpan.FromMilliseconds(30)), Config(7, 3, 0x105, sct: TimeSpan.FromMilliseconds(150)));
        rig.Input.Next = ulong.MaxValue;
        rig.Pair(); rig.Until(2);
        rig.Monitor.Poll().Observations.Count(x => x.Status == ForeignSrdoStatus.CaptureGap).Should().Be(1);
        rig.Pair(3, 4); rig.Until(4);
        rig.Monitor.Poll().Observations.Should().NotContain(x => x.Status == ForeignSrdoStatus.CaptureGap);
        rig.Monitor.ReplaceConfigurations(new[] { Config(sct: TimeSpan.FromMilliseconds(100)),
            Config(6, 2, 0x103, sct: TimeSpan.FromMilliseconds(30)), Config(7, 3, 0x105, sct: TimeSpan.FromMilliseconds(150)) });
        rig.Until(200);
        var batch = rig.Monitor.Poll();
        batch.Observations.Select(x => x.Configuration!.PeerNodeId).Should().Equal(6, 5, 7);
        batch.Observations.Select(x => x.HostTimestamp).Should().Equal(rig.At(34), rig.At(104), rig.At(154));
        batch.States[0].CaptureQuality.Should().Be(ForeignSrdoCaptureQuality.Contiguous);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(20)]
    public void Missing_Late_And_Future_Timing_Invalidate_Then_Fresh_Captures_Recover(int stamp)
    {
        using var rig = new Rig();
        rig.Pair(); rig.Until(10); rig.State().IsCurrent.Should().BeTrue();
        rig.Input.Add(0x101, new byte[] { 1, 2 }, stamp == 0 ? 0 : rig.At(stamp));
        var state = rig.State(); state.Status.Should().Be(ForeignSrdoStatus.TimingUnavailable); state.Signals.Should().BeEmpty();
        rig.Pair(11, 12); rig.Until(12); rig.State().IsCurrent.Should().BeTrue();
    }

    [Fact]
    public void Replacement_Is_Atomic_And_Discards_Previous_Configuration_And_Buffered_Data()
    {
        using var rig = new Rig();
        rig.Pair(); rig.Until(2); var old = rig.State();
        rig.Pair(3, 4); rig.Until(5);
        var config = Config(mapping: new[] { new PdoMappingEntry(0x3000, 2, 8) });
        rig.Monitor.Invoking(x => x.ReplaceConfigurations(new[] { config, config })).Should().Throw<ArgumentException>();
        rig.Monitor.ReplaceConfigurations(new[] { config });
        var batch = rig.Monitor.Poll();
        batch.Observations.Should().OnlyContain(x => x.Status == ForeignSrdoStatus.BeforeEvaluationPeriod);
        batch.States.Single().LastValidTimestamp.Should().BeNull();
        batch.States.Single().EvaluationPeriodId.Should().NotBe(old.EvaluationPeriodId);
        rig.Pair(6, 7); rig.Until(7);
        rig.State().Signals.Single().Index.Should().Be(0x3000);
    }

    [Fact]
    public void Full_Batches_Do_Not_Advance_Past_Buffered_Frames_Or_Advertise_Current_Values()
    {
        using var rig = new Rig();
        rig.Pair(); rig.Add(0x321, 3, 1); rig.Until(200);
        var first = rig.Monitor.Poll(2);
        first.HasBacklog.Should().BeTrue(); first.States.Single().IsCurrent.Should().BeFalse(); first.States.Single().Signals.Should().BeEmpty();
        first.States.Single().Status.Should().Be(ForeignSrdoStatus.Valid);
        var end = rig.Monitor.Poll(2);
        end.HasBacklog.Should().BeFalse(); end.States.Single().Status.Should().Be(ForeignSrdoStatus.SafeguardCycleExpired);
        rig.Monitor.Invoking(x => x.Poll(0)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Legacy_Capture_Quality_Remains_Unknown_And_Ownership_And_Disposal_Are_Explicit()
    {
        using var rig = new Rig(false);
        rig.Pair(); rig.Until(2);
        var batch = rig.Monitor.Poll();
        batch.CaptureQuality.Should().Be(ForeignSrdoCaptureQuality.Unknown); batch.DroppedFrameCount.Should().BeNull();
        batch.States.Single().CaptureQuality.Should().Be(ForeignSrdoCaptureQuality.Unknown);
        rig.Monitor.Dispose(); rig.Monitor.Dispose(); rig.Input.Disposed.Should().BeTrue();
        rig.Monitor.Invoking(x => x.Poll()).Should().Throw<ObjectDisposedException>();
        rig.Monitor.Invoking(x => x.ReplaceConfigurations(Array.Empty<ForeignSrdoConfiguration>())).Should().Throw<ObjectDisposedException>();
        var borrowed = new Capture();
        new ForeignSrdoMonitor(borrowed, "CAN-C", Array.Empty<ForeignSrdoConfiguration>()).Dispose();
        borrowed.Disposed.Should().BeFalse();
    }

    [Fact]
    public void Open_Uses_Real_Demux_And_Sends_Nothing_Including_On_Gap_Expiry_And_Disposal()
    {
        using var bus = ControllableBus.Plain("passive-401-" + Guid.NewGuid().ToString("N"));
        using var service = new CanBusService(bus);
        using var monitor = ForeignSrdoMonitor.Open(service, "physical-CAN", new[] { Config() }, 2);
        using var gfc = CanFrame.Classic(1, Array.Empty<byte>());
        using var ignored = CanFrame.Classic(0x700, new byte[] { 0 });
        using var ignoredLow = CanFrame.Classic(0, Array.Empty<byte>());
        bus.RaiseObserved(ignoredLow, false);
        bus.RaiseObserved(ignored, false);
        using var normal = CanFrame.Classic(0x101, new byte[] { 1, 2 });
        bus.RaiseObserved(normal, false);
        monitor.Poll();
        bus.RaiseObserved(gfc, true); bus.RaiseObserved(gfc, false); bus.RaiseObserved(gfc, false);
        var batch = monitor.Poll();
        batch.Observations.Count(x => x.Status == ForeignSrdoStatus.GfcObserved).Should().Be(2);
        batch.DroppedFrameCount.Should().Be(1); batch.CaptureQuality.Should().Be(ForeignSrdoCaptureQuality.Incomplete);
        monitor.Dispose(); service.SubscriptionCount.Should().Be(0); bus.TransmitCount.Should().Be(0);
        Action invalid = () => ForeignSrdoMonitor.Open(service, "", new[] { Config() });
        invalid.Should().Throw<ArgumentException>(); service.SubscriptionCount.Should().Be(0);
        Action noService = () => ForeignSrdoMonitor.Open(null!, "CAN", Array.Empty<ForeignSrdoConfiguration>());
        noService.Should().Throw<ArgumentNullException>();
    }
}
