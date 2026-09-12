# CanKit.Pro.RawCan

Raw-CAN service layer for [CanKit](https://github.com/pkuyo/CanKit): multi-protocol
demultiplexing / subscriptions (arc42 §5.3, ADR-5; SRS FR-RAW-010..015) and a TX-confirm
abstraction (arc42 §6.3, ADR-7; SRS FR-RAW-030..034).

Status: 1.0.0 – 1.2.3 are **withdrawn from nuget.org** — they were published as stable before
the API had been reviewed. **1.3.0 will be the first release whose API is stable**; until it is
tagged there is no listed version to install, so the `dotnet add package` line below resolves
nothing and the withdrawn releases come back only on an exact version pin. The public surface
can still change until then. See [Versioning](https://github.com/dborgards/CanKit.Pro/blob/main/docs/decisions/0001-versioning-and-api-stability.md).

One `ICanBusService` wraps one `ICanBus` and turns its single `FrameObserved` RX stream into
N independent, filtered, read-only `ISubscription`s — so several protocol instances (ISO-TP,
J1939, CANopen, …) can each see their own view of the same bus **without competing over
`ReceiveAsync`** and without one slow consumer blocking the others.

```csharp
using CanKit.Core;
using CanKit.Pro.RawCan;

using var bus = CanBus.Open("virtual://demo/0", cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(500_000));
using var service = new CanBusService(bus);

// Fast path: one 11-bit ID range per protocol instance (no per-frame delegate).
using var isoTp = service.Subscribe(CanIdFilter.Range(0x700, 0x7FF));

// Generic predicate when a range/mask is not enough.
using var custom = service.Subscribe(e => e.Frame.IsExtendedFrame && e.Frame.Len == 8);

await foreach (var e in isoTp.Frames.WithCancellation(token))
{
    // e.Frame            read-only CanFrameView, no ownership/disposal concerns, and it owns
    //                    its payload -- valid after the adapter has released the RX lease
    // e.IsEcho           the bus's own echo flag (see below)
    // e.ReceiveTimestamp what the adapter recorded; zero on adapters that do not timestamp
}
```

Each subscription owns its own bounded, drop-oldest buffer (FR-RAW-011). Disposing a
subscription deterministically deregisters it and completes its `Frames` stream; disposing the
service unwinds all subscriptions and detaches from the bus (FR-RAW-012). Call
`subscription.Reconfigure(CanIdFilter)` or `Reconfigure(predicate)` to change filter criteria at
runtime without recreating the subscription (FR-RAW-014); only frames observed after the call follow
the new criterion. This layer is built purely on the public `ICanBus.FrameObserved` surface, so it
works identically for every adapter.

### Echoes

A bus opened with `WorkMode == ChannelWorkMode.Echo` reports the host's own transmissions back
through the same RX stream, flagged as echoes. **Subscriptions do not deliver them unless asked**:

```csharp
using var quiet = service.Subscribe();                     // peer traffic only (the default)
using var trace = service.Subscribe(includeEcho: true);    // everything, e.Frame + e.IsEcho
```

Off by default because frames you sent are not frames you received: a J1939 node that treats its
own Address Claim as a competitor's, or a CANopen node that acts on its own PDO, is broken only on
the hardware that happens to echo. Where a subscription did not opt in, an echo is dropped before
the filter runs, so it never reaches a caller-supplied predicate either.

Two limits make this a convenience rather than a guarantee, and both matter:

**The gate only drops what the adapter flags.** An adapter that echoes without setting `IsEcho`
delivers its echo to every subscription no matter what `includeEcho` says — `CanKit.Adapter.Virtual`
in `ChannelWorkMode.Echo` is such an adapter today.

**`IsEcho` is host-scoped, not instance-scoped.** It means *something on this host sent this*, not
*I sent this*. Several protocol instances may share one `ICanBusService` (every factory here
documents that), and a sibling's transmission carries the same flag as your own. So a protocol
layer that shares a service asks for echoes and identifies itself by something it owns — a J1939
source address, a NAME, a CANopen node-id. `includeEcho: false` is for a single consumer that owns
its bus, such as a monitor or a one-node application.

`SendConfirmed` is independent of this: it matches echoes on the bus event itself, so withholding
them from subscribers does not affect TX confirmation.

## TX-Confirm

`SendConfirmed` gives a uniform "was this frame actually sent" answer regardless of whether the
bus has hardware TX echo enabled:

```csharp
// Bus opened with CanFeature.Echo + WorkMode = ChannelWorkMode.Echo -> real echo matching.
// Otherwise -> confirmed as soon as the driver accepts the frame (TxConfirmation.IsApproximated).
var result = await service.SendConfirmed(CanFrame.Classic(0x123, new byte[] { 1, 2, 3 }));

if (result.Confirmed)
{
    // result.IsApproximated tells you whether this was a real echo or driver-acceptance only.
}
else
{
    // result.FailureReason: Timeout, BusOff, or Rejected -- never an indefinite hang.
}
```

Concurrent, byte-identical sends are matched to their own confirmation in FIFO order, never
cross-matched (FR-RAW-031). The per-call timeout is configurable (FR-RAW-034); disposing the
service cancels any outstanding `SendConfirmed` calls rather than leaving them to time out.

## Install

```bash
dotnet add package CanKit.Pro.RawCan

# plus a CanKit adapter for the hardware you actually talk to, e.g.
dotnet add package CanKit.Adapter.Virtual   # loopback, no hardware
# dotnet add package CanKit.Adapter.PCAN    # Kvaser, Vector, SocketCAN, ZLG, ... likewise
```

Dependencies: `CanKit.Abstractions`.

Part of [CanKit.Pro](https://github.com/dborgards/CanKit.Pro) — higher CAN protocol layers
built **on top of** [CanKit](https://github.com/pkuyo/CanKit), which is consumed as a NuGet
package rather than forked.

## License

MIT — see [LICENSE](https://github.com/dborgards/CanKit.Pro/blob/main/LICENSE).
CanKit itself is a separate project licensed under Apache-2.0; see
[THIRD-PARTY-NOTICES.md](https://github.com/dborgards/CanKit.Pro/blob/main/THIRD-PARTY-NOTICES.md).
