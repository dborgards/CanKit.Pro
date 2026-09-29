using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core;
using CanKit.Pro.IsoTp;
using CanKit.Pro.RawCan;

// ISO-TP quickstart: send messages that are longer than one CAN frame
// ====================================================================
//
// A classic CAN frame carries at most 8 bytes. A diagnostic answer or a firmware block is much
// longer. ISO-TP (ISO 15765-2) is the layer that chops a long message into frames on the
// sender's side and glues it back together on the receiver's side — including the little
// hand-shake ("flow control") that stops a fast sender from flooding a slow receiver.
//
// This sample needs no hardware and no second computer. It
//   1. opens two virtual CAN buses that are wired together, like two devices on one cable,
//   2. puts an ISO-TP channel on each of them,
//   3. sends a short message (one frame) and a long one (many frames),
//   4. checks that the long message came out exactly as it went in.
//
//   dotnet run --project samples/CanKit.Pro.Sample.IsoTpQuickstart
//   dotnet run --project samples/CanKit.Pro.Sample.IsoTpQuickstart -- --pro   (adds the extras)
//
// Words you will meet:
//   CAN ID   the "address" of a frame. ISO-TP uses one ID per direction: here 0x7E0 and 0x7E8.
//   PDU      protocol data unit: the whole message, however many frames it takes.
//   SF FF CF FC   Single / First / Consecutive / Flow-control frame — the four kinds of frame
//            ISO-TP puts on the bus. The channel builds them; you only see the names in docs.
//
// Everything below the "Pro extras" banner is optional; you do not need it to use ISO-TP.

var pro = args.Contains("--pro");

// ── 1. Open two virtual buses ──────────────────────────────────────────────────────────────
// "virtual://<session>/<channel>" is CanKit's loopback adapter: buses that share the same
// session name are connected, and each frame one of them sends is received by the others.
// On real hardware you would pass an endpoint such as "socketcan://can0" instead; nothing else
// in this sample would change. CAN 2.0 at 500 kbit/s is the classic CAN setup.
Step("1. Open two virtual CAN buses");
var session = $"isotp-sample-{Guid.NewGuid():N}";
using var busA = OpenClassic(session, channel: 0);
using var busB = OpenClassic(session, channel: 1);

// ── 2. Open an ISO-TP channel on each bus ──────────────────────────────────────────────────
// An endpoint says which CAN IDs to use: "I transmit on txCanId and listen on rxCanId".
// The two sides mirror each other, just as a tester (0x7E0) and an ECU (0x7E8) would.
Step("2. Open one ISO-TP channel per bus (A sends on 0x7E0, B answers on 0x7E8)");
using var sender = IsoTp.Open(busA, IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8));
using var receiver = IsoTp.Open(busB, IsoTpEndpoint.Normal(txCanId: 0x7E8, rxCanId: 0x7E0));

// Never wait forever in a program that talks to a bus: this token gives up after 10 seconds.
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

// ── 3. A short message: fits in a single frame ─────────────────────────────────────────────
// Up to 7 bytes fit in one frame on classic CAN. Start listening *before* sending; a message
// that arrives while nobody is listening is queued, but starting first keeps the code obvious.
Step("3. Send a short message (3 bytes -> one Single Frame)");
var listening = receiver.ReceiveAsync(cts.Token);
byte[] shortMessage = { 0x22, 0xF1, 0x90 };          // e.g. a UDS "read data by identifier"
await sender.SendAsync(shortMessage, cts.Token);      // completes when the frame is on the bus
Console.WriteLine($"   received: {BitConverter.ToString(await listening)}");

// ── 4. A long message: the channel splits it up for you ────────────────────────────────────
// 200 bytes do not fit in one frame. SendAsync sends a First Frame, waits for the receiver's
// Flow Control frame ("go ahead"), then streams Consecutive Frames. The receiver reassembles
// them and checks their sequence numbers. You just await one call on each side.
Step("4. Send a long message (200 bytes -> First Frame, Flow Control, Consecutive Frames)");
var longMessage = Enumerable.Range(0, 200).Select(i => (byte)i).ToArray();
listening = receiver.ReceiveAsync(cts.Token);
await sender.SendAsync(longMessage, cts.Token);
var received = await listening;
Console.WriteLine($"   received {received.Length} bytes, identical to what was sent: " +
                  $"{received.SequenceEqual(longMessage)}");

Console.WriteLine();
Console.WriteLine("Done. If the receiver had stopped answering, SendAsync would have thrown an");
Console.WriteLine("IsoTpTimeoutException instead of hanging — try '-- --pro' to see that happen.");

if (!pro)
{
    Console.WriteLine("Run with '-- --pro' for tuning, error handling, CAN FD and multiplexing.");
    return;
}

// ═══════════════════════════════════════════════════════════════════════════════════════════
// Pro extras (dotnet run ... -- --pro)
// ═══════════════════════════════════════════════════════════════════════════════════════════

// ── A. Flow control is the receiver's call ─────────────────────────────────────────────────
// The receiver advertises how it wants to be fed in every Flow Control frame: LocalBlockSize
// (send this many frames, then wait for the next Flow Control) and LocalStMin (leave at least
// this long between two frames). Here the receiver asks for 5 ms between frames.
// 200 bytes are 1 First Frame + 28 Consecutive Frames, so the transfer cannot be faster than
// roughly 28 x 5 ms. (The elapsed time varies with the machine; it only has a lower bound.)
Step("A. Receiver-side pacing: LocalStMin = 5 ms, LocalBlockSize = 4");
{
    var pacedSession = $"isotp-sample-{Guid.NewGuid():N}";
    using var pacedA = OpenClassic(pacedSession, 0);
    using var pacedB = OpenClassic(pacedSession, 1);
    using var pacedSender = IsoTp.Open(pacedA, IsoTpEndpoint.Normal(0x7E0, 0x7E8));
    using var pacedReceiver = IsoTp.Open(pacedB, IsoTpEndpoint.Normal(0x7E8, 0x7E0),
        new IsoTpChannelOptions
        {
            LocalStMin = TimeSpan.FromMilliseconds(5),
            LocalBlockSize = 4,
        });

    var pending = pacedReceiver.ReceiveAsync(cts.Token);
    var clock = Stopwatch.StartNew();
    await pacedSender.SendAsync(longMessage, cts.Token);
    await pending;
    Console.WriteLine($"   200 bytes took {clock.ElapsedMilliseconds} ms " +
                      "(the receiver's STmin alone accounts for roughly 140 ms of that)");
}

// ── B. Errors are exceptions with a name ───────────────────────────────────────────────────
// A First Frame goes out, but nobody answers with Flow Control (the bus has no other node).
// ISO 15765-2 calls the wait limit N_Bs; the channel enforces it and the exception says which
// timer expired. The other timers are NAs (the driver must confirm each frame) and NCr (the
// receiver waits for the next Consecutive Frame). Shortened here so the sample stays quick.
Step("B. Timeouts: send a long message to a bus where nobody listens");
{
    using var lonelyBus = OpenClassic($"isotp-sample-{Guid.NewGuid():N}", 0);
    using var lonelySender = IsoTp.Open(lonelyBus, IsoTpEndpoint.Normal(0x7E0, 0x7E8),
        new IsoTpChannelOptions { NBs = TimeSpan.FromMilliseconds(300) });
    try
    {
        await lonelySender.SendAsync(longMessage, cts.Token);
        Console.WriteLine("   unexpectedly succeeded");
    }
    catch (IsoTpTimeoutException ex)
    {
        Console.WriteLine($"   IsoTpTimeoutException, timer = {ex.Timer}");
    }
}

// ── C. Several conversations over one bus ──────────────────────────────────────────────────
// Each IsoTp.Open(bus, ...) call creates its own bus service. To run several ISO-TP channels
// on the SAME bus (say, two ECUs you talk to), create one CanBusService yourself and pass it
// in: it delivers each channel only the frames that match its CAN IDs.
Step("C. Two ISO-TP channels sharing one bus service");
{
    var sharedSession = $"isotp-sample-{Guid.NewGuid():N}";
    using var testerBus = OpenClassic(sharedSession, 0);
    using var ecuBus = OpenClassic(sharedSession, 1);
    using var testerService = new CanBusService(testerBus);
    using var toEngine = IsoTp.Open(testerService, IsoTpEndpoint.Normal(0x7E0, 0x7E8),
        leaveOpen: true);
    using var toGearbox = IsoTp.Open(testerService, IsoTpEndpoint.Normal(0x7E1, 0x7E9),
        leaveOpen: true);
    using var engine = IsoTp.Open(ecuBus, IsoTpEndpoint.Normal(0x7E8, 0x7E0));
    using var gearbox = IsoTp.Open(ecuBus, IsoTpEndpoint.Normal(0x7E9, 0x7E1));

    var engineGets = engine.ReceiveAsync(cts.Token);
    var gearboxGets = gearbox.ReceiveAsync(cts.Token);
    await Task.WhenAll(
        toEngine.SendAsync(new byte[] { 0xE0, 0x01 }, cts.Token),
        toGearbox.SendAsync(new byte[] { 0x6B, 0x02 }, cts.Token));
    Console.WriteLine($"   engine ECU got  {BitConverter.ToString(await engineGets)}");
    Console.WriteLine($"   gearbox ECU got {BitConverter.ToString(await gearboxGets)}");
}

// ── D. CAN FD: 64 bytes per frame ──────────────────────────────────────────────────────────
// With CAN FD each frame carries up to 64 bytes, so the same 200 bytes need far fewer frames.
// UseCanFd must match how the buses were opened. The counter below listens on the receiving
// bus (FrameObserved) and counts only frames on the sender's ID.
Step("D. CAN FD: the same 200 bytes in fewer frames");
{
    var fdSession = $"isotp-sample-{Guid.NewGuid():N}";
    using var fdA = OpenFd(fdSession, 0);
    using var fdB = OpenFd(fdSession, 1);
    var fdOptions = new IsoTpChannelOptions { UseCanFd = true };
    using var fdSender = IsoTp.Open(fdA, IsoTpEndpoint.Normal(0x7E0, 0x7E8), fdOptions);
    using var fdReceiver = IsoTp.Open(fdB, IsoTpEndpoint.Normal(0x7E8, 0x7E0), fdOptions);

    var frames = 0;
    fdB.FrameObserved += (_, observed) =>
    {
        if (observed.CanFrame.ID == 0x7E0) Interlocked.Increment(ref frames);
    };

    var pending = fdReceiver.ReceiveAsync(cts.Token);
    await fdSender.SendAsync(longMessage, cts.Token);
    var got = await pending;
    Console.WriteLine($"   {got.Length} bytes arrived intact: {got.SequenceEqual(longMessage)}; " +
                      $"data frames on the wire: {frames} (classic CAN needs 29)");
}

Console.WriteLine();
Console.WriteLine("Pro extras done. Next: samples/CanKit.Pro.Sample.UdsQuickstart puts a diagnostic");
Console.WriteLine("client on top of an ISO-TP channel like the ones above.");

static ICanBus OpenClassic(string session, int channel) =>
    CanBus.Open($"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(500_000));

static ICanBus OpenFd(string session, int channel) =>
    CanBus.Open($"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.CanFd).Fd(500_000, 2_000_000));

static void Step(string title) => Console.WriteLine($"\n{title}");
