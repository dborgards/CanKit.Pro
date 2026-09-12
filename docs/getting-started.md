# Getting started

CanKit.Pro needs a working CanKit setup, because it is a layer on top of one — it never opens a
bus itself. If you already use CanKit, you have everything.

## Install

```bash
# CanKit: the core plus one adapter for the hardware you talk to
dotnet add package CanKit.Core
dotnet add package CanKit.Adapter.Virtual     # loopback, no hardware — good for a first run
# dotnet add package CanKit.Adapter.PCAN      # or Kvaser, Vector, SocketCAN, ZLG, ControlCAN

# CanKit.Pro: take only the layers you need
dotnet add package CanKit.Pro.RawCan
dotnet add package CanKit.Pro.Actor
dotnet add package CanKit.Pro.Addressing
dotnet add package CanKit.Pro.Reliability
```

Targets `netstandard2.0` and `net10.0`, so .NET Framework 4.6.2+, .NET 10 and later all work.

## Open a bus

Unchanged from CanKit — endpoints are URIs, and the adapter is selected by scheme:

```csharp
using CanKit.Core;

using var bus = CanBus.Open("virtual://demo/0",
    cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(500_000));
```

Everything below takes that `ICanBus`.

## One bus, several protocol instances

The problem CanKit.Pro.RawCan exists for: `ICanBus.ReceiveAsync` is a single stream. Two protocol
stacks reading it compete — each frame goes to whichever asked first, and neither sees the traffic
it needs.

`CanBusService` attaches once to the bus and fans every frame out to independent, filtered,
read-only subscriptions:

```csharp
using CanKit.Pro.RawCan;

using var service = new CanBusService(bus);

// Allocation-free fast path: one ID range per protocol instance.
using var isoTp = service.Subscribe(CanIdFilter.Range(0x700, 0x7FF));

// Predicate when a range or acceptance mask is not enough.
using var errors = service.Subscribe(e => e.Frame.IsErrorFrame);

// Opt in where you want to see your own transmissions come back -- a bus monitor, say.
using var trace = service.Subscribe(includeEcho: true);

await foreach (var e in isoTp.Frames.WithCancellation(token))
{
    // e.Frame            -- CanFrameView: read-only, no ownership, safe to keep past this
    //                       iteration (the demux copies the payload before buffering it).
    // e.IsEcho           -- the bus's own flag; always false unless you asked for echoes.
    // e.ReceiveTimestamp -- what the adapter recorded, zero on adapters that do not timestamp.
}
```

Each subscription has its own bounded, drop-oldest buffer, so a slow consumer drops its own frames
instead of blocking everyone else. Disposing a subscription deregisters it and completes its
stream; disposing the service unwinds all of them and detaches from the bus.

**Echoes are not delivered unless you ask.** On a bus configured for echo
(`WorkMode == ChannelWorkMode.Echo`) the adapter reports your own transmissions back through the
same RX stream. Frames you sent are not frames you received, and a protocol layer that confuses
the two misbehaves only on the hardware that happens to echo — so the default is off, per
subscription, and `includeEcho: true` is a deliberate choice. `SendConfirmed` is unaffected either
way: it matches echoes on the bus event itself, not through a subscription.

Two caveats before you rely on it. The gate filters on the flag the adapter sets, and an adapter
that echoes without setting it delivers the echo anyway (`CanKit.Adapter.Virtual` in
`ChannelWorkMode.Echo` is one). And `IsEcho` is *host-scoped*: it means something on this host sent
the frame, not that **you** sent it — so if you run several protocol instances over one service,
withholding echoes also withholds your siblings' traffic. That is why the protocol layers in this
repository opt in and then apply their own check where they need one — J1939-TP on its source
address, J1939 on its NAME. CANopen opts in without filtering its own non-SYNC traffic, so a
CANopen node on a flagging adapter does see its own PDOs and heartbeats.

If two instances were meant to have disjoint ID spaces, you can check rather than hope:

```csharp
foreach (var overlap in service.FindOverlappingFilterSubscriptions())
    logger.Warning("Overlapping subscriptions {A} and {B}, sharing IDs 0x{Low:X}..0x{High:X}",
        overlap.A, overlap.B, overlap.LowestSharedId, overlap.HighestSharedId);
```

Each result is a `FilterOverlap`: the two subscriptions that share ID space — the relation is
symmetric, so `A` and `B` say nothing beyond registration order — and the range they share. For two
range filters every ID in between is shared as well; for acceptance-code/mask filters the bounds are
a hull around a scattered set. If you only want the pair, it destructures: `var (a, b) = overlap;`.

## Did the frame actually go out?

`ICanBus.Transmit` tells you the driver accepted the frame, which is not the same thing. Where the
bus supports TX echo, `SendConfirmed` waits for the real echo; where it does not, it falls back to
driver acceptance and says so:

```csharp
var result = await service.SendConfirmed(CanFrame.Classic(0x123, new byte[] { 1, 2, 3 }));

if (result.Confirmed && !result.IsApproximated)      // a real echo came back
else if (result.Confirmed)                            // driver accepted it; nothing confirmed it
else switch (result.FailureReason) { /* Timeout, BusOff, Rejected */ }
```

It never hangs: timeout, bus-off and outright rejection all resolve within bounded time, and
disposing the service cancels anything still in flight.

Concurrent byte-identical sends are matched FIFO to their own confirmation, so two instances
sending the same frame do not steal each other's echo.

## A threading model instead of locks

Protocol state machines are single-writer by nature. `ProtocolActor` gives you that explicitly:
one mailbox, one loop, work and timer callbacks running strictly one at a time.

```csharp
using CanKit.Pro.Actor;

using var actor = new ProtocolActor();                  // dedicated thread by default
actor.BackgroundExceptionOccurred += (_, ex) => log.Error(ex, "protocol instance failed");

actor.Post(() => state.OnFrame(frame));                 // fire and forget
var count = await actor.PostAsync(() => state.Count);   // request/response
using var timer = actor.Schedule(TimeSpan.FromMilliseconds(150), () => state.OnTimeout());
```

State touched only through `Post`/`PostAsync`/`Schedule` needs no lock of its own. An idle actor
uses no CPU: the loop blocks until either new work or the next timer deadline.

## Timeouts that are actually checked, and bus health

```csharp
using CanKit.Pro.Reliability;

// A deadline is scheduled on the actor's own timer queue, so its expiry is dispatched and run —
// not stored in a field nobody re-reads.
var scheduler = new DeadlineScheduler(actor);
var deadline = scheduler.Arm(TimeSpan.FromMilliseconds(150), () => channel.OnTimeout());

if (deadline.Complete()) { /* we won: onTimeout will not run */ }
deadline.Rearm(TimeSpan.FromMilliseconds(150));         // e.g. on each consecutive frame

// Bus health, pushed rather than polled by you.
using var monitor = new BusStateMonitor(bus, actor);
monitor.StateChanged += (_, e) =>
{
    if (e.Current.IsTransmitBlocked()) AbortActiveTransfer();          // BusOff
    else if (e.Previous.IsDegraded() && !e.Current.IsDegraded()) Resume();
};
```

## CAN IDs without bit-twiddling

```csharp
using CanKit.Pro.Addressing;

CanIdRange.ValidateStandard(0x7FF);                     // ok
CanIdRange.ValidateStandard(0x800);                     // ArgumentOutOfRangeException

var id = J1939Id.ComposePgn(priority: 3, pgn: 0xFEEE, sourceAddress: 0x17);

var fields = J1939Id.Decompose(id);
// fields.Priority, .Pgn, .SourceAddress, .IsPdu1, .DestinationAddress (null for PDU2)
```

## Run something

```bash
git clone https://github.com/dborgards/CanKit.Pro.git
cd CanKit.Pro
dotnet run --project samples/CanKit.Pro.Sample.Demux
```

No hardware needed — it runs on the loopback adapter.

## The protocol layers

Everything above is L2 — the plumbing. The protocol stacks that sit on it live in this repository
too and are published to nuget.org alongside the L2 packages:

```csharp
// ISO 15765-2 over CAN or CAN FD.
using var isoTp = IsoTp.Open(bus, IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8));
await isoTp.SendAsync(payload);

// UDS on top of it: sessions, security access, read/write by identifier, routine control.
using var uds = UdsClient.Create(isoTp);
await uds.DiagnosticSessionControlAsync(UdsSessionType.Extended);
var vin = await uds.ReadDataByIdentifierAsync(0xF190);

// CANopen: SDO, PDO mapping, NMT, heartbeat, EMCY, object dictionary.
using var node = CanOpen.OpenNode(bus, nodeId: 0x01);
var deviceType = await node.SdoUploadAsync(serverNodeId: 0x11, index: 0x1000, subindex: 0x00);

// J1939: address claim, PGN messaging, transport protocol for >8-byte payloads.
using var j1939 = J1939Node.Open(bus, new J1939NodeOptions(myName));
await j1939.ClaimAddressAsync(0x80);
await j1939.SendAsync(new J1939Message(pgn: 0xFEEEu, payload, priority: 6));
```

Each package's `README.md` documents its own guarantees, options and edge cases; the test suites
under `tests/CanKit.Pro.Tests/TestCases/` are the most precise description of what each layer
promises.

> Each snippet above uses one bus per node for readability; the quickstart samples under
> `samples/` show the full, runnable version on the loopback adapter.

## Where next

- [Architecture (arc42)](architecture/arc42-CanKit.Pro.md) — the layer model and the decisions
  behind these APIs (German).
- Each package's `README.md` (also shown on nuget.org for the published ones) documents its
  guarantees and edge cases in more detail than this page does.
