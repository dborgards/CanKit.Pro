using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core;
using CanKit.Pro.Actor;
using CanKit.Pro.Addressing;
using CanKit.Pro.RawCan;
using CanKit.Pro.Reliability;

// RawCan quickstart: several readers, one bus
// ============================================
//
// A CAN bus is a broadcast medium: every frame reaches every node. Inside one program that
// usually means several parts want to read the *same* bus — a diagnostic stack, a telemetry
// logger, a protocol decoder — and each one only cares about certain CAN IDs. If they all call
// ReceiveAsync on the bus itself, they steal frames from each other.
//
// CanKit.Pro.RawCan fixes that with a *bus service*: you put one service on the bus and take out
// as many *subscriptions* as you like. Each subscription has its own filter and its own queue, so
// a slow reader can neither starve nor block the others.
//
// This sample needs no hardware. It
//   1. opens two virtual CAN buses wired together ("writer" plays the other devices,
//      "reader" is your program),
//   2. gives the reader two subscriptions for two disjoint ranges of CAN IDs,
//   3. sends frames from the writer and shows which subscription receives what,
//   4. asks the service "did that frame really go out?" (SendConfirmed).
//
//   dotnet run --project samples/CanKit.Pro.Sample.Demux
//   dotnet run --project samples/CanKit.Pro.Sample.Demux -- --pro   (adds the extras)
//
// Words you will meet:
//   subscription   your own filtered view of the bus (a stream of frames plus an on-off switch).
//   filter         which CAN IDs a subscription wants, e.g. every ID from 0x700 to 0x7FF.
//   echo           the bus reporting back a frame your own node sent (not all hardware can).

var pro = args.Contains("--pro");

// ── 1. Two virtual buses ───────────────────────────────────────────────────────────────────
// "virtual://<session>/<channel>" is CanKit's loopback adapter: buses with the same session name
// are wired together. With real hardware the endpoint would be e.g. "socketcan://can0".
Step("1. Open two virtual CAN buses: 'writer' (the rest of the world) and 'reader' (us)");
const string session = "cankit-pro-sample";
using var writer = OpenBus(session, channel: 0);
using var reader = OpenBus(session, channel: 1);

// ── 2. One service, two subscriptions ──────────────────────────────────────────────────────
// The service attaches to the bus once. From here on, read frames through subscriptions —
// not through reader.ReceiveAsync — so that nothing competes.
Step("2. Put a bus service on the reader and subscribe to two ID ranges");
using var service = new CanBusService(reader);
using var diagnostics = service.Subscribe(CanIdFilter.Range(0x700, 0x7FF, CanFilterIDType.Standard));
using var telemetry = service.Subscribe(CanIdFilter.Range(0x100, 0x1FF, CanFilterIDType.Standard));

// ── 3. Read the subscriptions ──────────────────────────────────────────────────────────────
// Each subscription's Frames is an async stream: 'await foreach' waits for the next matching
// frame. We start one reader task per subscription and tell each how many frames to expect,
// so the sample ends as soon as everything has arrived. The token is only a safety net.
Step("3. Start a reader task per subscription, then send four frames from the writer");
using var safetyNet = new CancellationTokenSource(TimeSpan.FromSeconds(5));
var printers = Task.WhenAll(
    Print("diagnostics", diagnostics, expected: 2, safetyNet.Token),
    Print("telemetry  ", telemetry, expected: 1, safetyNet.Token));

writer.Transmit(CanFrame.Classic(0x701, new byte[] { 0x02, 0x10, 0x01 }));         // diagnostics
writer.Transmit(CanFrame.Classic(0x123, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }));   // telemetry
writer.Transmit(CanFrame.Classic(0x7DF, new byte[] { 0x02, 0x3E, 0x00 }));         // diagnostics
writer.Transmit(CanFrame.Classic(0x555, new byte[] { 0x01 }));                     // nobody wants it
await printers;
Console.WriteLine("   (0x555 matched no subscription, so nobody received it)");

// ── 4. Was it really sent? ─────────────────────────────────────────────────────────────────
// Transmit only says the driver accepted the frame. SendConfirmed answers the question protocols
// actually ask: "did it go out?". Where the hardware reports TX echo, the answer is exact; where
// it cannot, you get the driver's acceptance instead and IsApproximated says so.
Step("4. Send a frame from the reader and ask for confirmation");
var confirmation = await service.SendConfirmed(CanFrame.Classic(0x201, new byte[] { 1, 2, 3 }));
Console.WriteLine($"   confirmed={confirmation.Confirmed}, approximated={confirmation.IsApproximated}, " +
                  $"failure reason={confirmation.FailureReason}");
Console.WriteLine("   (approximated=True: this loopback bus does not report echoes, so the driver's acceptance");
Console.WriteLine("    stands in for a real one. On hardware that reports TX echo the answer is exact.)");

// ── 5. J1939 IDs without bit shifting ──────────────────────────────────────────────────────
// CanKit.Pro.Addressing builds and takes apart the 29-bit identifiers of J1939.
Step("5. Compose a J1939 CAN ID from its fields");
var engineTemperature = J1939Id.ComposePgn(priority: 3, pgn: 0xFEEE, sourceAddress: 0x17);
Console.WriteLine($"   priority 3, PGN 0xFEEE, source address 0x17  ->  CAN ID 0x{engineTemperature:X8}");

Console.WriteLine();
Console.WriteLine("Done.");

if (!pro)
{
    Console.WriteLine("Run with '-- --pro' for predicates, runtime re-filtering, overlap detection, queue limits and deadlines.");
    return;
}

// ═══════════════════════════════════════════════════════════════════════════════════════════
// Pro extras (dotnet run ... -- --pro)
// ═══════════════════════════════════════════════════════════════════════════════════════════

// ── A. Any condition, not just ID ranges ───────────────────────────────────────────────────
// Besides CanIdFilter (allocation-free fast path) a subscription can take any predicate over
// the frame event. Here: only 29-bit frames with at least four data bytes. A filter can be
// swapped while the subscription is live with Reconfigure — nothing is lost or re-subscribed.
Step("A. Predicate subscription, then Reconfigure at runtime");
{
    using var longExtended = service.Subscribe(
        e => e.Frame.IsExtendedFrame && e.Frame.Data.Length >= 4);
    writer.Transmit(CanFrame.Classic(0x18FEEE17, new byte[] { 1, 2 }, isExtendedFrame: true));      // too short
    writer.Transmit(CanFrame.Classic(0x18FEEE17, new byte[] { 1, 2, 3, 4 }, isExtendedFrame: true)); // match
    writer.Transmit(CanFrame.Classic(0x123, new byte[] { 1, 2, 3, 4 }));                             // standard
    await Task.Delay(100);
    Console.WriteLine($"   predicate saw: {Drain(longExtended)}");

    longExtended.Reconfigure(CanIdFilter.Range(0x600, 0x6FF, CanFilterIDType.Standard));
    writer.Transmit(CanFrame.Classic(0x123, new byte[] { 9 }));                                     // no longer relevant
    writer.Transmit(CanFrame.Classic(0x650, new byte[] { 9 }));
    await Task.Delay(100);
    Console.WriteLine($"   after Reconfigure to 0x600..0x6FF: {Drain(longExtended)}");
}

// ── B. Find filters that collide ───────────────────────────────────────────────────────────
// Two protocol instances that both accept the same ID both get the frame — usually a
// configuration mistake. The service can list every pair of subscriptions whose filters overlap
// (only ID filters take part; predicates are opaque).
Step("B. Detect overlapping subscriptions");
{
    using var stray = service.Subscribe(CanIdFilter.Range(0x7F0, 0x7FF, CanFilterIDType.Standard));
    foreach (var overlap in service.FindOverlappingFilterSubscriptions())
    {
        Console.WriteLine($"   overlap between two subscriptions on IDs 0x{overlap.LowestSharedId:X3}" +
                          $"..0x{overlap.HighestSharedId:X3}");
    }
}

// ── C. A slow reader cannot block anybody ──────────────────────────────────────────────────
// Every subscription has its own bounded queue (bufferCapacity). When a reader falls behind and
// the queue is full, the OLDEST frame is dropped to make room — the bus loop is never held up,
// and the other subscriptions are unaffected. Here a subscription with room for 3 frames gets 10
// while nobody reads it.
Step("C. Bounded queues: a subscription that is not read keeps only the newest frames");
{
    using var lazy = service.Subscribe(CanIdFilter.Range(0x400, 0x4FF, CanFilterIDType.Standard),
        bufferCapacity: 3);
    for (var i = 1; i <= 10; i++)
        writer.Transmit(CanFrame.Classic(0x400 + i, new byte[] { (byte)i }));
    await Task.Delay(100);
    Console.WriteLine($"   10 frames sent, capacity 3, still queued: {Drain(lazy)}");
}

// ── D. Timeouts that are checked, on one thread ────────────────────────────────────────────
// Protocol code needs timers ("no answer within 150 ms -> give up") and must not touch its state
// from several threads. A ProtocolActor is one mailbox and one loop; DeadlineScheduler arms
// timeouts on it, so expiry callbacks run in the same single-writer context as everything else.
Step("D. A ProtocolActor with two deadlines: one expires, one is completed in time");
{
    using var actor = new ProtocolActor();
    var deadlines = new DeadlineScheduler(actor);
    var expired = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

    using var slow = deadlines.Arm(TimeSpan.FromMilliseconds(150),
        () => expired.TrySetResult(Environment.CurrentManagedThreadId));
    using var answered = deadlines.Arm(TimeSpan.FromMilliseconds(150),
        () => Console.WriteLine("   this must not print"));
    answered.Complete();                                     // the answer came in time

    var actorThread = await actor.PostAsync(() => Environment.CurrentManagedThreadId);
    var expiredOn = await expired.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Console.WriteLine($"   'slow' expired={slow.IsExpired}, 'answered' completed={answered.IsCompleted}");
    Console.WriteLine($"   expiry ran on the actor's thread: {expiredOn == actorThread}");

    // The same actor drives bus-health monitoring: transitions to ErrWarning / ErrPassive / BusOff
    // and the recovery arrive as StateChanged events. The virtual bus never degrades, so this
    // only shows the wiring; on hardware, this is where you would abort an active transfer.
    using var monitor = new BusStateMonitor(reader, actor);
    monitor.StateChanged += (_, e) => Console.WriteLine($"   bus state {e.Previous} -> {e.Current}");
    Console.WriteLine($"   the monitor is watching; current state: {monitor.CurrentState}");
}

Console.WriteLine();
Console.WriteLine("Pro extras done. The package READMEs (src/CanKit.Pro.RawCan, .Actor, .Reliability)");
Console.WriteLine("describe the guarantees behind each of these.");

static ICanBus OpenBus(string session, int channel) =>
    CanBus.Open($"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(500_000));

static void Step(string title) => Console.WriteLine($"\n{title}");

// What is queued in a subscription right now, as a short readable list.
static string Drain(ISubscription subscription)
{
    var seen = new System.Collections.Generic.List<string>();
    while (subscription.TryRead(out var e))
        seen.Add($"0x{e.Frame.ID:X3}{(e.IsEcho ? " (echo)" : "")}");
    return seen.Count == 0 ? "nothing" : string.Join(", ", seen);
}

// Reads one subscription until 'expected' frames have arrived (or the safety net fires).
static async Task Print(string label, ISubscription subscription, int expected, CancellationToken token)
{
    var received = 0;
    try
    {
        await foreach (var frameEvent in subscription.Frames.WithCancellation(token))
        {
            var frame = frameEvent.Frame;
            // Echoes are left out unless a subscription asks for them, so IsEcho is false here.
            Console.WriteLine($"   [{label}] ID 0x{frame.ID:X3}, {frame.Data.Length} data bytes, " +
                              $"echo={frameEvent.IsEcho}");
            if (++received == expected) break;
        }
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine($"   [{label}] gave up after {received} of {expected} frames");
    }
}
