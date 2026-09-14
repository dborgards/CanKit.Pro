using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core;
using CanKit.Pro.IsoTp;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using FluentAssertions;
using Xunit;
// Alias the CanKit.Pro.IsoTp namespace root to avoid clashing with this test namespace's
// trailing "IsoTp" segment (same reason as in IsoTpChannelIntegrationTests).
using IsoTpFactory = CanKit.Pro.IsoTp.IsoTp;

namespace CanKit.Pro.Tests.TestCases.IsoTp;

/// <summary>
/// NFR-003 verification: the sender must pace Consecutive Frames according to the peer's
/// STmin flow-control value (ISO 15765-2). Before these tests existed, no test drove the
/// STmin scheduling path at all (every peer FC used STmin = 0), so a regression to
/// "STmin ignored" (which would flood slow real ECUs) would have been invisible in CI.
/// The pacing assertion used to be a soft wall-clock bound -- "observed spacing roughly reaches
/// the advertised STmin" -- which is a claim about the runner as much as about the code, and #92
/// records it going red on macOS for that reason. It now runs on a clock the test drives, so the
/// property asserted is the one the code implements and there is no tolerance to widen. What a
/// real runner does to effective spacing is a separate question, and #92 step 3 is where it is
/// decided what of that is still worth observing non-gating.
/// </summary>
public class IsoTpStminTimingTests : IClassFixture<VirtualAdapterFixture>
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The virtual resolution a pacing interval is bracketed to.</summary>
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(1);



    private static string NewSession() => $"isotp-stmin-{Guid.NewGuid():N}";

    private static ICanBus OpenClassic(string session, int channel) => CanBus.Open(
        $"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(VirtualAdapterFixture.Bitrate));

    private static IsoTpChannelOptions FastOptions(TimeSpan? localStMin = null)
        => new()
        {
            UseCanFd = false,
            UsePadding = true,
            LocalBlockSize = 0,
            LocalStMin = localStMin ?? TimeSpan.Zero,
            NAs = TimeSpan.FromMilliseconds(500),
            NBs = TimeSpan.FromMilliseconds(500),
            NCr = TimeSpan.FromMilliseconds(500),
            WftMax = 10,
        };

    /// <summary>
    /// #92 step 2, the same property with the clock in the test's hands.
    ///
    /// The wall-clock version above asserts that observed CF spacing roughly reaches the
    /// advertised STmin, which is a claim about the runner as much as about the code — the ISO-TP
    /// README concedes that effective spacing is "STmin + OS scheduling latency … with no hard
    /// real-time guarantee under load", and #92 records this test going red on macOS for exactly
    /// that. Here the sender's actor measures its timers against a clock nobody but this test
    /// moves, so the property becomes the one the code implements: <b>each consecutive frame is
    /// released once STmin of that clock has elapsed, and not before</b>. Each interval is
    /// bracketed from both sides — nothing at STmin minus one tick, the frame at STmin — which is
    /// what makes this about the configured value rather than merely about pacing existing at all.
    ///
    /// There is no real-time quantity left. The probe is safe to assert because the sender is
    /// known to have armed STmin — the test asks its actor rather than inferring it from a frame
    /// on the wire — so a timer one tick away simply cannot have fired.
    /// </summary>
    [Fact]
    public async Task Sender_Advances_One_Consecutive_Frame_Per_Stmin_On_A_Clock_The_Test_Drives()
    {
        var stMin = TimeSpan.FromMilliseconds(5);
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);
        using var snifferBus = OpenClassic(session, 2);

        using var clock = new VirtualClock();
        using var serviceA = new CanBusService(busA);
        using var serviceB = new CanBusService(busB);

        var senderActor = clock.NewActor();
        using var sender = new IsoTpChannel(serviceA, IsoTpEndpoint.Normal(0x7E0, 0x7E8),
            FastOptions(), ownsService: false, senderActor);
        using var receiver = new IsoTpChannel(serviceB, IsoTpEndpoint.Normal(0x7E8, 0x7E0),
            FastOptions(localStMin: stMin), ownsService: false, clock.NewActor());

        var cfCount = 0;
        var fcCount = 0;
        snifferBus.FrameObserved += (_, view) =>
        {
            var data = view.CanFrame.Data.Span;
            if (data.Length == 0) return;
            // The peer's Flow Control, which is what releases the sender into the CF phase.
            if (view.CanFrame.ID == 0x7E8 && (data[0] & 0xF0) == 0x30)
                Interlocked.Increment(ref fcCount);
            if (view.CanFrame.ID == 0x7E0 && (data[0] & 0xF0) == 0x20)
                Interlocked.Increment(ref cfCount);
        };

        // 60 bytes classic: FF carries 6, the remaining 54 go in 8 CFs of 7 bytes.
        const int expectedCfs = 8;
        var pdu = Enumerable.Range(0, 60).Select(i => (byte)(i & 0xFF)).ToArray();
        var recvTask = receiver.ReceiveAsync(new CancellationTokenSource(ShortTimeout).Token);
        var sendTask = sender.SendAsync(pdu);

        // Anchor on the peer's Flow Control: until it lands the sender is waiting for the peer,
        // not for STmin, and asserting "nothing came out" would be true for the wrong reason.
        // Every CF from the first one on is paced -- STmin governs the gap after the FC too.
        await WaitForCountAsync(() => Volatile.Read(ref fcCount), 1, "flow control frames");

        // One tick short of STmin. Stepping to here first is what makes this test about the
        // configured value rather than merely about pacing existing at all: a jump of a whole
        // STmin cannot tell 5 ms from any shorter positive delay, because the overdue timer fires
        // on arrival either way and re-arms from the clock's new value. Codex found that on the
        // first revision of this test, and halving the sender's STmin confirmed it -- the test
        // passed. This probe fails on a halved STmin, because the frame is out before the
        // interval is up.
        var justUnder = stMin - Step;

        for (var expected = 1; expected <= expectedCfs; expected++)
        {
            // Wait until the sender has actually armed STmin before moving the clock. The
            // confirmation that triggers the arming reaches its actor through a thread-pool post
            // ordered against nothing here, so moving first would arm the interval from the new
            // reading and the frame would never come. An earlier revision waited out a fixed
            // grace instead; Codex pointed out that a grace establishes no ordering at all, and
            // it was right -- this asks the actor.
            await clock.WaitUntilTimerArmedAsync(senderActor, stMin, ShortTimeout);

            await clock.AdvanceAsync(justUnder);
            await clock.SettleAsync();
            Volatile.Read(ref cfCount).Should().Be(expected - 1,
                "only {0} of the {1} STmin interval has elapsed, so consecutive frame {2} is not "
                + "due yet", justUnder, stMin, expected);

            await clock.AdvanceAsync(Step);
            await WaitForCountAsync(() => Volatile.Read(ref cfCount), expected, "consecutive frames");
        }

        await sendTask.WaitAsync(ShortTimeout);
        (await recvTask.WaitAsync(ShortTimeout)).Should().Equal(pdu);
        Volatile.Read(ref cfCount).Should().Be(expectedCfs);
    }

    /// <summary>
    /// Waits for the sniffer to have seen <paramref name="target"/> consecutive frames. This is a
    /// wait for an <em>effect</em> — the emission a due timer handed to the thread pool — not an
    /// assertion about how long anything took, so a slow runner delays this test rather than
    /// failing it.
    /// </summary>
    private static async Task WaitForCountAsync(Func<int> count, int target, string what)
    {
        var deadline = DateTime.UtcNow + ShortTimeout;
        while (count() < target)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Expected {target} {what}, saw {count()}.");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task Stmin_Zero_Keeps_MultiFrame_Transfer_Unpaced()
    {
        // Regression guard for the opposite direction: with STmin = 0 the transfer must
        // complete promptly (the existing round-trip tests cover correctness; this pins
        // down that the new pacing path does not inject artificial delays).
        var session = NewSession();
        using var busA = OpenClassic(session, 0);
        using var busB = OpenClassic(session, 1);

        using var sender = IsoTpFactory.Open(busA, IsoTpEndpoint.Normal(0x7E0, 0x7E8), FastOptions());
        using var receiver = IsoTpFactory.Open(busB, IsoTpEndpoint.Normal(0x7E8, 0x7E0), FastOptions());

        var pdu = Enumerable.Range(0, 60).Select(i => (byte)(i & 0xFF)).ToArray();
        var recvTask = receiver.ReceiveAsync(new CancellationTokenSource(ShortTimeout).Token);
        var sw = Stopwatch.StartNew();
        await sender.SendAsync(pdu);
        var got = await recvTask;
        sw.Stop();

        got.Should().Equal(pdu);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2),
            "with STmin = 0 no pacing delay may be injected (8 CFs over an in-memory bus)");
    }
}
