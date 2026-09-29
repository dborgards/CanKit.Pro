using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core;
using CanKit.Pro.Addressing;
using CanKit.Pro.J1939;

// J1939 quickstart: the CAN protocol of trucks, buses and engines
// ================================================================
//
// SAE J1939 sits on top of CAN with 29-bit identifiers. Instead of "frame 0x123" you talk about
// *parameter groups*: "engine speed lives in parameter group 0xF004". Each device on the bus
// has an 8-bit *address* and announces which one it uses ("address claiming"), so messages can
// say who sent them. Inside a parameter group, single values are called SPNs (for example SPN 190
// = engine speed); the group's bytes say where each one sits and how to scale it.
//
// This sample needs no truck and no hardware. It
//   1. opens two virtual CAN buses wired together and puts a J1939 node on each,
//   2. lets both nodes claim an address,
//   3. sends the engine-speed message from A to B and decodes the SPN,
//   4. sends a 24-byte message — longer than a CAN frame — and shows that it arrives whole.
//
//   dotnet run --project samples/CanKit.Pro.Sample.J1939Quickstart
//   dotnet run --project samples/CanKit.Pro.Sample.J1939Quickstart -- --pro   (adds the extras)
//
// Words you will meet:
//   PGN       parameter group number — what a message is about (0xF004 = engine controller 1).
//   SA        source address — the sender's 8-bit address on the bus.
//   NAME      a 64-bit identity every node has (manufacturer, function, serial number, ...).
//             When two nodes want the same address, the lower NAME wins it.
//   SPN       suspect parameter number — one value inside a PGN, e.g. engine speed.
//   J1939-TP  the transport protocol that carries messages longer than 8 bytes.

var pro = args.Contains("--pro");

// ── 1. Two virtual buses and one J1939 node per bus ────────────────────────────────────────
// A node needs a NAME. Real products get theirs from the manufacturer; for the sample only the
// identity number (think: serial number) differs between the nodes.
Step("1. Open two virtual CAN buses and a J1939 node on each");
var session = $"j1939-sample-{Guid.NewGuid():N}";
using var busA = OpenBus(session, channel: 0);
using var busB = OpenBus(session, channel: 1);
using var nodeA = J1939Node.Open(busA, new J1939NodeOptions(MakeName(identity: 0x0000AA)));
using var nodeB = J1939Node.Open(busB, new J1939NodeOptions(MakeName(identity: 0x0000BB)));

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));   // never wait forever

// ── 2. Claim an address ────────────────────────────────────────────────────────────────────
// Each node announces the address it wants and waits a moment (about a quarter of a second)
// for anybody with a better right to object. Only after that may it send messages.
Step("2. Claim addresses: A wants 0x30, B wants 0x40");
await nodeA.ClaimAddressAsync(0x30, cts.Token);
await nodeB.ClaimAddressAsync(0x40, cts.Token);
Console.WriteLine($"   A owns 0x{nodeA.Address:X2}, B owns 0x{nodeB.Address:X2}");

// ── 3. Send one parameter group, decode one value ──────────────────────────────────────────
// EEC1 (electronic engine controller 1, PGN 0xF004) carries engine speed as SPN 190:
// 16 bits at byte offset 3, little-endian, 0.125 rpm per bit.
Step("3. A sends the engine-speed message (PGN 0xF004); B decodes SPN 190");
const uint Eec1Pgn = 0xF004;
var nextAtB = NextMessage(nodeB, m => m.Pgn == Eec1Pgn, cts.Token);
await nodeA.SendAsync(new J1939Message(Eec1Pgn, Eec1(rpm: 2500.0), priority: 3), cts.Token);
var eec1 = await nextAtB;
Console.WriteLine($"   PGN 0x{eec1.Pgn:X4} from source address 0x{eec1.SourceAddress:X2}: " +
                  $"engine speed = {DescribeEngineSpeed(eec1)}");

// ── 4. A value that is "not available" ─────────────────────────────────────────────────────
// J1939 reserves the top of every value range for special meanings. A speed field of 0xFFFF
// does NOT mean 8191.875 rpm; it means "this ECU has no such reading". That is why decoding
// returns a J1939SpnValue instead of a plain number: you have to ask whether it is valid.
Step("4. The same message with engine speed 'not available' (raw value 0xFFFF)");
nextAtB = NextMessage(nodeB, m => m.Pgn == Eec1Pgn, cts.Token);
await nodeA.SendAsync(new J1939Message(Eec1Pgn, Eec1(rpm: null), priority: 3), cts.Token);
Console.WriteLine($"   engine speed = {DescribeEngineSpeed(await nextAtB)}");

// ── 5. A message longer than one frame ─────────────────────────────────────────────────────
// A CAN frame holds 8 bytes. For a longer payload the node switches to J1939-TP on its own:
// you call SendAsync exactly as before, and the receiver gets the reassembled message.
Step("5. A sends a 24-byte message (PGN 0xFEF0); it is split and reassembled automatically");
var nextBig = NextMessage(nodeB, m => m.Pgn == 0xFEF0, cts.Token);
var big = Enumerable.Range(0, 24).Select(i => (byte)i).ToArray();
await nodeA.SendAsync(new J1939Message(0xFEF0, big, priority: 6), cts.Token);
var reassembled = await nextBig;
Console.WriteLine($"   received {reassembled.Payload.Length} bytes, identical: " +
                  $"{reassembled.Payload.Span.SequenceEqual(big)}");

Console.WriteLine();
Console.WriteLine("Done.");

if (!pro)
{
    Console.WriteLine("Run with '-- --pro' for periodic sending, requests, address conflicts and PGN math.");
    return;
}

// ═══════════════════════════════════════════════════════════════════════════════════════════
// Pro extras (dotnet run ... -- --pro)
// ═══════════════════════════════════════════════════════════════════════════════════════════

// ── A. Periodic sending ────────────────────────────────────────────────────────────────────
// Most J1939 data is broadcast at a fixed rate. StartPeriodicSend keeps a fixed-rate schedule
// (no drift from the time each send takes) until the returned handle is disposed.
Step("A. Broadcast engine speed every 100 ms for ~350 ms");
{
    var count = 0;
    void Count(object? _, J1939Message m) { if (m.Pgn == Eec1Pgn) Interlocked.Increment(ref count); }
    nodeB.MessageReceived += Count;
    using (nodeA.StartPeriodicSend(new J1939Message(Eec1Pgn, Eec1(2500.0), priority: 3),
               TimeSpan.FromMilliseconds(100)))
    {
        await Task.Delay(350, cts.Token);
    }
    nodeB.MessageReceived -= Count;
    Console.WriteLine($"   B received {count} messages (about 3 to 4 expected)");
}

// ── B. Ask for a parameter group ───────────────────────────────────────────────────────────
// Not everything is broadcast. Any node can send a "request" (PGN 0xEA00 whose 3 payload bytes
// are the wanted PGN, little-endian); the node that owns the data answers. The library sends
// the request for you; answering is application logic — here node B watches for it.
Step("B. A requests engine temperature (PGN 0xFEEE) from B, and B answers");
{
    const uint EngineTemperaturePgn = 0xFEEE;
    void Answer(object? _, J1939Message m)
    {
        if (!J1939Pgn.IsRequest(m.Pgn)) return;
        var wanted = (uint)(m.Payload.Span[0] | (m.Payload.Span[1] << 8) | (m.Payload.Span[2] << 16));
        if (wanted != EngineTemperaturePgn) return;
        // SPN 110 (coolant temperature): 1 byte, 1 degC per bit, offset -40 degC.
        var payload = Enumerable.Repeat((byte)0xFF, 8).ToArray();
        payload[0] = (byte)(85 + 40);
        _ = nodeB.SendAsync(new J1939Message(EngineTemperaturePgn, payload));   // fire and forget
    }
    nodeB.MessageReceived += Answer;

    var answer = NextMessage(nodeA, m => m.Pgn == EngineTemperaturePgn, cts.Token);
    await nodeA.RequestPgnAsync(EngineTemperaturePgn, destinationAddress: nodeB.Address!.Value, cts.Token);
    var reply = await answer;
    nodeB.MessageReceived -= Answer;

    var coolant = J1939Spn.Extract(reply.Payload.Span, byteOffset: 0, startBit: 0, bitLength: 8,
        resolution: 1.0, offset: -40.0);
    Console.WriteLine($"   coolant temperature = {coolant.GetValueOrDefault():F0} degC " +
                      $"(from source address 0x{reply.SourceAddress:X2})");
}

// ── C. Two nodes want the same address ─────────────────────────────────────────────────────
// Address 0x30 is taken by A. Node C wants it too, but its NAME is numerically higher, so it
// loses the arbitration. Because C's NAME says "arbitrary address capable", it does not give up:
// it falls back to the arbitrary range 0x80..0xF7. A node without that capability would end up
// in the CannotClaim state and must stay silent.
Step("C. Address conflict: C also asks for 0x30");
{
    using var busC = OpenBus(session, channel: 2);
    using var nodeC = J1939Node.Open(busC,
        new J1939NodeOptions(MakeName(identity: 0x0000CC, arbitraryAddressCapable: true)));
    nodeC.AddressClaimChanged += (_, e) =>
    {
        if (e.State != J1939ClaimState.Claiming)            // skip the intermediate attempts
            Console.WriteLine($"   C: state {e.State}, address " +
                              (e.Address is { } a ? $"0x{a:X2}" : "none"));
    };
    await nodeC.ClaimAddressAsync(0x30, cts.Token);
    Console.WriteLine($"   C ended up on 0x{nodeC.Address:X2}; A still owns 0x{nodeA.Address:X2}");
}

// ── D. PGN and address math, without a bus ─────────────────────────────────────────────────
// Every J1939 message is one 29-bit CAN ID: priority (3 bits), PGN (18 bits), source (8 bits).
// CanKit.Pro.Addressing composes and decomposes it, so you never shift bits by hand.
Step("D. Compose and decompose a 29-bit J1939 CAN ID");
{
    var id = J1939Id.ComposePgn(priority: 3, pgn: Eec1Pgn, sourceAddress: 0x30);
    var fields = J1939Id.Decompose(id);
    Console.WriteLine($"   priority 3, PGN 0x{Eec1Pgn:X4}, SA 0x30  ->  CAN ID 0x{id:X8}");
    Console.WriteLine($"   back again: priority {fields.Priority}, PGN 0x{fields.Pgn:X4}, " +
                      $"SA 0x{fields.SourceAddress:X2}, peer-to-peer: {fields.IsPdu1}");
}

Console.WriteLine();
Console.WriteLine("Pro extras done. The SPN catalogue (J1939SpnCatalog) and the node options are");
Console.WriteLine("described in src/CanKit.Pro.J1939/README.md.");

static ICanBus OpenBus(string session, int channel) =>
    CanBus.Open($"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(500_000));

// The NAME is what makes a node unique on the bus. The values are arbitrary sample values.
static J1939Name MakeName(uint identity, bool arbitraryAddressCapable = false) => new(
    identityNumber: identity, manufacturerCode: 0x0AB,
    ecuInstance: 0, functionInstance: 0, function: 0x81, reserved: false,
    vehicleSystem: 0, vehicleSystemInstance: 0, industryGroup: 0,
    arbitraryAddressCapable: arbitraryAddressCapable);

// EEC1 payload: SPN 190 (engine speed) in bytes 3..4, little-endian, 0.125 rpm/bit.
// Bytes without data are 0xFF by J1939 convention; a null rpm writes 0xFFFF = "not available".
static byte[] Eec1(double? rpm)
{
    var payload = Enumerable.Repeat((byte)0xFF, 8).ToArray();
    var raw = rpm is { } value ? (ushort)(value / 0.125) : (ushort)0xFFFF;
    payload[3] = (byte)(raw & 0xFF);
    payload[4] = (byte)(raw >> 8);
    return payload;
}

static string DescribeEngineSpeed(J1939Message message)
{
    var speed = J1939Spn.Extract(message.Payload.Span, byteOffset: 3, startBit: 0,
        bitLength: 16, resolution: 0.125, offset: 0.0);
    return speed.TryGetValue(out var rpm) ? $"{rpm:F1} rpm" : speed.ToString();   // e.g. NotAvailable
}

// Completes with the next received message that matches. Subscribes first, so call it BEFORE the
// send that should trigger the message.
static Task<J1939Message> NextMessage(IJ1939Node node, Func<J1939Message, bool> match,
    CancellationToken cancellationToken)
{
    var source = new TaskCompletionSource<J1939Message>(TaskCreationOptions.RunContinuationsAsynchronously);
    void Handler(object? _, J1939Message message)
    {
        if (!match(message)) return;
        node.MessageReceived -= Handler;
        source.TrySetResult(message);
    }
    node.MessageReceived += Handler;
    cancellationToken.Register(() =>
    {
        node.MessageReceived -= Handler;
        source.TrySetCanceled(cancellationToken);
    });
    return source.Task;
}

static void Step(string title) => Console.WriteLine($"\n{title}");
